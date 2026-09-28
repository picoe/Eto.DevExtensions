using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.Settings;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Eto.DevExtension.VisualStudio.Windows.Editor
{
	/// <summary>A platform the preview can draw with.</summary>
	sealed class PreviewPlatform
	{
		/// <summary>Name passed to the host with --platform.</summary>
		public string Id { get; set; }
		public string Label { get; set; }
		/// <summary>Matches a project that uses this platform, from a package or project reference.</summary>
		public Regex Reference { get; set; }
		public string HostPath { get; set; }
		/// <summary>Folder the host needs first on PATH, or null.</summary>
		public string LibraryPath { get; set; }
		/// <summary>Shown when the host can't be started.</summary>
		public string Requirement { get; set; }
	}

	/// <summary>
	/// The platforms the preview can draw with, and the one picked for the open solution.
	/// </summary>
	static class PreviewPlatforms
	{
		public const string Auto = "auto";
		/// <summary>The platform's usual theme, which for WPF is the system one.</summary>
		public const string DefaultTheme = "";
		const string HostDll = "Eto.DevExtension.PreviewHost.dll";
		const string CollectionPath = @"Eto.DevExtension\PreviewPlatform";
		const string ThemeCollectionPath = @"Eto.DevExtension\PreviewTheme";
		const string Requirement = "The preview needs the .NET 8 Desktop Runtime (or newer) to be installed.";
		// scanning projects on every keystroke is too slow, but an edited project should still be noticed
		static readonly TimeSpan CacheTime = TimeSpan.FromSeconds(10);

		static (DateTime Time, string Solution, string Id) autoCache;
		// used when there's no solution to remember it with
		static string unsavedChoice = Auto;
		static string unsavedTheme = DefaultTheme;

		/// <summary>Raised on the UI thread when <see cref="Choice"/> changes.</summary>
		public static event EventHandler ChoiceChanged;

		/// <summary>Raised on the UI thread when <see cref="ThemeChoice"/> changes.</summary>
		public static event EventHandler ThemeChoiceChanged;

		/// <summary>Platforms available on this machine, best first.</summary>
		public static List<PreviewPlatform> GetAvailable()
		{
			var previewDir = Path.Combine(Path.GetDirectoryName(typeof(PreviewPlatforms).Assembly.Location), "preview");
			var windowsHost = Path.Combine(previewDir, "win", HostDll);
			var platforms = new List<PreviewPlatform>
			{
				new PreviewPlatform { Id = "Wpf", Label = "WPF", Reference = new Regex(@"Eto\.Platform\.Wpf\b|Eto\.Wpf\.csproj", RegexOptions.IgnoreCase), HostPath = windowsHost, Requirement = Requirement },
				new PreviewPlatform { Id = "WinForms", Label = "WinForms", Reference = new Regex(@"Eto\.Platform\.Windows\b|Eto\.WinForms\.csproj", RegexOptions.IgnoreCase), HostPath = windowsHost, Requirement = Requirement }
			};

			var gtk = FindGtk();
			var netHost = Path.Combine(previewDir, "net", HostDll);
			if (gtk != null && File.Exists(netHost))
			{
				platforms.Add(new PreviewPlatform
				{
					Id = "Gtk",
					Label = "Gtk",
					Reference = new Regex(@"Eto\.Platform\.Gtk\b|Eto\.Gtk\.csproj", RegexOptions.IgnoreCase),
					HostPath = netHost,
					LibraryPath = gtk,
					Requirement = "The preview needs the .NET 8 runtime (or newer) and GTK 3."
				});
			}
			return platforms;
		}

		/// <summary>A platform id or <see cref="Auto"/>, remembered for each solution. Use on the UI thread.</summary>
		public static string Choice
		{
			get => GetChoice(CollectionPath, Auto, unsavedChoice);
			set
			{
				if (SetChoice(CollectionPath, value ?? Auto, Auto, ref unsavedChoice))
					ChoiceChanged?.Invoke(null, EventArgs.Empty);
			}
		}

		/// <summary>A theme name from the host, or <see cref="DefaultTheme"/>, remembered for each solution. Use on the UI thread.</summary>
		public static string ThemeChoice
		{
			get => GetChoice(ThemeCollectionPath, DefaultTheme, unsavedTheme);
			set
			{
				if (SetChoice(ThemeCollectionPath, value ?? DefaultTheme, DefaultTheme, ref unsavedTheme))
					ThemeChoiceChanged?.Invoke(null, EventArgs.Empty);
			}
		}

		static string GetChoice(string collection, string fallback, string unsaved)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			var solution = GetSolutionPath();
			if (string.IsNullOrEmpty(solution))
				return unsaved;
			var store = GetStore();
			return store?.GetString(collection, solution, fallback) ?? fallback;
		}

		/// <returns>true when the choice changed.</returns>
		static bool SetChoice(string collection, string value, string fallback, ref string unsaved)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			if (value == GetChoice(collection, fallback, unsaved))
				return false;

			var solution = GetSolutionPath();
			var store = string.IsNullOrEmpty(solution) ? null : GetStore();
			if (store == null)
				unsaved = value;
			else if (value == fallback)
			{
				if (store.CollectionExists(collection))
					store.DeleteProperty(collection, solution);
			}
			else
			{
				store.CreateCollection(collection);
				store.SetString(collection, solution, value);
			}
			return true;
		}

		/// <summary>The platform to draw with: the one picked, or for Auto the first the solution references, otherwise WPF.</summary>
		public static async Task<PreviewPlatform> ResolveAsync()
		{
			await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
			var platforms = GetAvailable();
			var choice = Choice;
			return platforms.FirstOrDefault(r => r.Id == choice) ?? Detect(platforms);
		}

		static PreviewPlatform Detect(List<PreviewPlatform> platforms)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			var solution = GetSolutionPath() ?? string.Empty;
			var cached = autoCache;
			if (cached.Solution == solution && DateTime.UtcNow - cached.Time < CacheTime)
			{
				var platform = platforms.FirstOrDefault(r => r.Id == cached.Id);
				if (platform != null)
					return platform;
			}

			var texts = GetProjectFiles().Select(ReadText).ToList();
			var referenced = platforms.FirstOrDefault(platform => texts.Any(text => platform.Reference.IsMatch(text)));
			var result = referenced ?? platforms[0];
			if (cached.Id != result.Id || cached.Solution != solution)
			{
				Debug.WriteLine(referenced != null
					? $"Eto preview: drawing with {result.Label}, as the solution uses it."
					: $"Eto preview: drawing with {result.Label}, as the solution doesn't reference an Eto platform available here.");
			}
			autoCache = (DateTime.UtcNow, solution, result.Id);
			return result;
		}

		static IEnumerable<string> GetProjectFiles()
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			var dte = (DTE2)Package.GetGlobalService(typeof(SDTE));
			var files = new List<string>();
			if (dte?.Solution?.Projects != null)
			{
				foreach (Project project in dte.Solution.Projects)
					AddProjectFiles(project, files);
			}
			return files;
		}

		static void AddProjectFiles(Project project, List<string> files)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			if (project == null)
				return;
			if (project.Kind == ProjectKinds.vsProjectKindSolutionFolder)
			{
				foreach (ProjectItem item in project.ProjectItems)
					AddProjectFiles(item.SubProject, files);
				return;
			}
			try
			{
				if (!string.IsNullOrEmpty(project.FullName))
					files.Add(project.FullName);
			}
			catch (Exception ex)
			{
				// unloaded projects throw
				Debug.WriteLine($"Eto preview: could not read project {project.Name}: {ex.Message}");
			}
		}

		static string ReadText(string fileName)
		{
			try
			{
				return File.ReadAllText(fileName);
			}
			catch
			{
				return string.Empty;
			}
		}

		/// <summary>Folder holding GTK 3, or null when it isn't installed.</summary>
		static string FindGtk()
		{
			// where GtkSharp's build installs it, or anywhere on PATH such as MSYS2
			var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			var installed = string.IsNullOrEmpty(localAppData) ? null : Path.Combine(localAppData, "Gtk", "3.24.24");
			var path = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator);
			return new[] { installed }.Concat(path).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r) && HasGtk(r));
		}

		static bool HasGtk(string dir)
		{
			try
			{
				return File.Exists(Path.Combine(dir, "libgtk-3-0.dll"));
			}
			catch (ArgumentException)
			{
				// PATH can hold invalid entries
				return false;
			}
		}

		static string GetSolutionPath()
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			var dte = (DTE2)Package.GetGlobalService(typeof(SDTE));
			return dte?.Solution?.FullName;
		}

		static WritableSettingsStore GetStore()
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			try
			{
				return new ShellSettingsManager(ServiceProvider.GlobalProvider).GetWritableSettingsStore(SettingsScope.UserSettings);
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"Eto preview: could not open settings: {ex.Message}");
				return null;
			}
		}
	}
}
