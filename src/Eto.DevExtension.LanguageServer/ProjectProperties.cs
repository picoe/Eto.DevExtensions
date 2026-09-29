using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Eto.DevExtension.LanguageServer
{
	/// <summary>
	/// Where a project builds to, as MSBuild evaluates it, so custom BaseOutputPath, OutputPath or
	/// BaseIntermediateOutputPath settings are honoured.
	/// </summary>
	/// <remarks>Falls back to the default bin/obj layout when the .NET SDK can't evaluate the project.</remarks>
	public class ProjectProperties
	{
		static readonly string[] Properties = { "AssemblyName", "TargetPath", "OutputPath", "BaseOutputPath", "ProjectAssetsFile" };
		static readonly Regex assemblyNameReg = new Regex(@"<AssemblyName>\s*(?<name>[^<$]+?)\s*</AssemblyName>", RegexOptions.Compiled);
		static readonly Regex projectReferenceReg = new Regex(@"<ProjectReference\s+Include\s*=\s*""(?<path>[^""]+)""", RegexOptions.Compiled);

		// evaluating takes a moment, so only redo it when the project file changes
		static readonly ConcurrentDictionary<string, (DateTime Modified, ProjectProperties Properties)> cache = new ConcurrentDictionary<string, (DateTime, ProjectProperties)>(StringComparer.OrdinalIgnoreCase);

		public string AssemblyName { get; private set; }
		public string ProjectAssetsFile { get; private set; }
		public IList<string> ProjectReferences { get; private set; }

		/// <summary>Folders the project's builds land in, most specific first.</summary>
		public IList<string> OutputFolders { get; private set; }

		/// <summary>Gets the properties of the given project file.</summary>
		public static ProjectProperties Get(string project, Action<string> log = null)
		{
			var modified = File.GetLastWriteTimeUtc(project);
			if (cache.TryGetValue(project, out var cached) && cached.Modified == modified)
				return cached.Properties;

			var properties = Evaluate(project, log) ?? Default(project);
			cache[project] = (modified, properties);
			return properties;
		}

		static ProjectProperties Default(string project)
		{
			var dir = Path.GetDirectoryName(project);
			var text = ReadText(project);
			var match = assemblyNameReg.Match(text);
			return new ProjectProperties
			{
				AssemblyName = match.Success ? match.Groups["name"].Value : Path.GetFileNameWithoutExtension(project),
				ProjectAssetsFile = Path.Combine(dir, "obj", "project.assets.json"),
				OutputFolders = new[] { Path.Combine(dir, "bin") },
				ProjectReferences = projectReferenceReg.Matches(text)
					.Cast<Match>()
					.Select(r => Path.GetFullPath(Path.Combine(dir, r.Groups["path"].Value.Replace('\\', Path.DirectorySeparatorChar))))
					.ToList()
			};
		}

		static ProjectProperties Evaluate(string project, Action<string> log)
		{
			var dir = Path.GetDirectoryName(project);
			var info = new ProcessStartInfo(FindDotnet())
			{
				// run from the project so its global.json picks the SDK
				WorkingDirectory = dir,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};
			info.ArgumentList.Add("msbuild");
			info.ArgumentList.Add(project);
			info.ArgumentList.Add("-nologo");
			foreach (var property in Properties)
				info.ArgumentList.Add("-getProperty:" + property);
			info.ArgumentList.Add("-getItem:ProjectReference");

			try
			{
				using var process = Process.Start(info);
				var error = process.StandardError.ReadToEndAsync();
				var output = process.StandardOutput.ReadToEnd();
				if (!process.WaitForExit(30000))
				{
					process.Kill(true);
					log?.Invoke($"Timed out evaluating {project}, so assuming it builds to bin.");
					return null;
				}
				if (process.ExitCode != 0)
				{
					log?.Invoke($"Could not evaluate {project}, so assuming it builds to bin: {(output + error.Result).Trim()}");
					return null;
				}

				var json = JsonNode.Parse(output.Substring(Math.Max(0, output.IndexOf('{'))));
				string Property(string name)
				{
					var value = json?["Properties"]?[name]?.GetValue<string>();
					return string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value, dir);
				}

				var targetPath = Property("TargetPath");
				var folders = new[] { targetPath != null ? Path.GetDirectoryName(targetPath) : null, Property("OutputPath"), Property("BaseOutputPath") }
					.Where(r => r != null)
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.ToList();
				var name = json?["Properties"]?["AssemblyName"]?.GetValue<string>();

				return new ProjectProperties
				{
					AssemblyName = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(project) : name,
					ProjectAssetsFile = Property("ProjectAssetsFile") ?? Path.Combine(dir, "obj", "project.assets.json"),
					OutputFolders = folders.Count > 0 ? folders : new List<string> { Path.Combine(dir, "bin") },
					ProjectReferences = ((json?["Items"]?["ProjectReference"] as JsonArray) ?? new JsonArray())
						.Select(r => r?["FullPath"]?.GetValue<string>())
						.Where(r => !string.IsNullOrEmpty(r))
						.ToList()
				};
			}
			catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is IOException || ex is JsonException || ex is InvalidOperationException)
			{
				log?.Invoke($"Could not evaluate {project}, so assuming it builds to bin: {ex.Message}");
				return null;
			}
		}

		/// <summary>Newest build of <paramref name="fileName"/> in the project's output folders.</summary>
		public FileInfo FindNewest(string fileName, Action<string> log = null)
		{
			FileInfo newest = null;
			foreach (var folder in OutputFolders.Where(Directory.Exists))
			{
				try
				{
					var found = new DirectoryInfo(folder)
						.EnumerateFiles(fileName, SearchOption.AllDirectories)
						// skip ref/ reference assemblies, which have no usable types
						.Where(r => !string.Equals(r.Directory?.Name, "ref", StringComparison.OrdinalIgnoreCase))
						.OrderByDescending(r => r.LastWriteTimeUtc)
						.FirstOrDefault();
					if (found != null && (newest == null || found.LastWriteTimeUtc > newest.LastWriteTimeUtc))
						newest = found;
				}
				catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
				{
					log?.Invoke($"Could not scan {folder}: {ex.Message}");
				}
			}
			return newest;
		}

		static string FindDotnet()
		{
			var exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";
			// usually we're already running under it, but the macOS host is a native app
			if (string.Equals(Path.GetFileName(Environment.ProcessPath), exe, StringComparison.OrdinalIgnoreCase))
				return Environment.ProcessPath;

			var roots = new[]
			{
				Environment.GetEnvironmentVariable("DOTNET_ROOT"),
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
				"/usr/local/share/dotnet",
				"/usr/share/dotnet",
				"/usr/lib/dotnet",
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet")
			};
			// GUI apps on macOS don't get the shell's PATH, so check the usual install locations too
			return roots.Where(r => !string.IsNullOrEmpty(r)).Select(r => Path.Combine(r, exe)).FirstOrDefault(File.Exists) ?? exe;
		}

		static string ReadText(string path)
		{
			try
			{
				return File.ReadAllText(path);
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
			{
				return string.Empty;
			}
		}
	}
}
