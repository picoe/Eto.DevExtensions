using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Eto.DevExtension.LanguageServer;

namespace Eto.DevExtension.PreviewHost
{
	/// <summary>
	/// Resolves the project's assemblies and a matching Eto + Eto platform for this process.
	/// Call <see cref="Configure"/> once, before anything touches an Eto type.
	/// </summary>
	/// <remarks>
	/// A process only ever serves one project, since assemblies can't be swapped out once loaded.
	/// </remarks>
	static class ProjectAssemblies
	{
		// Eto and the libraries it shares with the project, which must all come from the same place
		static readonly string[] SerializationNames = { "Eto.Serialization.Xaml", "Eto.Serialization.Json" };
		static readonly string[] SharedNames = { "Portable.Xaml", "Newtonsoft.Json" };

		static readonly Dictionary<string, string> namedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		static readonly ConcurrentDictionary<string, Assembly> loaded = new ConcurrentDictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
		static readonly List<string> probeDirectories = new List<string>();
		static readonly List<string> watchedFiles = new List<string>();
		static readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
		static System.Threading.Timer changeTimer;
		static readonly object loadLock = new object();
		static AssemblyDependencyResolver dependencyResolver;
		static Action<string> log;
		static PreviewPlatform platform;

#if MACOS
		// Contents/Resources/eto in the app bundle, beside Contents/MonoBundle
		static readonly string FallbackDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "eto"));
#else
		static readonly string FallbackDirectory = Path.Combine(AppContext.BaseDirectory, "eto");
#endif

		/// <summary>Project assembly files, its own first.</summary>
		public static IReadOnlyList<string> Paths { get; private set; } = Array.Empty<string>();

		/// <summary>Identifies the assembly set, so a render for a different project can be refused.</summary>
		public static string Key { get; private set; }

		public static string MainAssembly => Paths.Count > 0 ? Paths[0] : null;

		/// <summary>File Eto.dll was loaded from, since a copy loaded from memory has no location.</summary>
		public static string EtoFile => namedFiles.TryGetValue("Eto", out var file) ? file : null;

		public static string GetKey(IEnumerable<string> paths) => string.Join("|", paths ?? Enumerable.Empty<string>());

		public static void Configure(PreviewPlatform previewPlatform, IList<string> paths, string documentPath, Action<string> logger)
		{
			platform = previewPlatform;
			log = logger;
			Paths = (paths ?? new List<string>()).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			Key = GetKey(paths);

			foreach (var directory in Paths.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase))
				probeDirectories.Add(directory);

			if (MainAssembly != null)
			{
				try
				{
					dependencyResolver = new AssemblyDependencyResolver(MainAssembly);
				}
				catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
				{
					// no deps.json, as with .NET Framework projects
				}
			}

			ChooseEto(documentPath);

			AssemblyLoadContext.Default.Resolving += Resolve;
			AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolveUnmanaged;
		}

		/// <summary>Loads the project's own assemblies, so the designer can find their types.</summary>
		public static void LoadProject()
		{
			foreach (var path in Paths)
			{
				try
				{
					Load(path);
				}
				catch (Exception ex) when (ex is IOException || ex is BadImageFormatException || ex is UnauthorizedAccessException)
				{
					log?.Invoke($"Could not load {path}: {ex.Message}");
				}
			}
		}

		/// <summary>Calls <paramref name="changed"/> once any project file loaded so far is rebuilt.</summary>
		public static void WatchForChanges(Action changed)
		{
			List<string> files;
			lock (loadLock)
				files = watchedFiles.ToList();

			var timer = changeTimer = new System.Threading.Timer(_ => changed());
			foreach (var group in files.GroupBy(Path.GetDirectoryName, StringComparer.OrdinalIgnoreCase))
			{
				var names = new HashSet<string>(group.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
				var watcher = new FileSystemWatcher(group.Key) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
				FileSystemEventHandler handler = (sender, e) =>
				{
					// a build writes several files, so wait for it to settle
					if (names.Contains(e.Name))
						timer.Change(1000, System.Threading.Timeout.Infinite);
				};
				watcher.Changed += handler;
				watcher.Created += handler;
				watcher.EnableRaisingEvents = true;
				watchers.Add(watcher);
			}
		}

		static void ChooseEto(string documentPath)
		{
			// the project's own copy, so its custom controls bind to the Eto they were built against
			var etoDirectory = probeDirectories.FirstOrDefault(r => File.Exists(Path.Combine(r, "Eto.dll")))
				?? EtoAssemblyLocator.Find(documentPath, log);
			var eto = etoDirectory != null ? Path.Combine(etoDirectory, "Eto.dll") : null;
			var version = eto != null && File.Exists(eto) ? GetPackageVersion(eto) : null;
			var platformFile = version != null ? FindPlatform(version, etoDirectory) : null;

			if (platformFile == null)
			{
				if (eto != null)
					log?.Invoke($"No {platform.AssemblyName} {version} found for {eto}, so using Eto bundled with the extension.");
				foreach (var name in new[] { "Eto", platform.AssemblyName }.Concat(platform.CompanionNames).Concat(SerializationNames).Concat(SharedNames).Concat(platform.SharedNames))
					SetFallback(name);
				return;
			}

			// the located copy may be built for a newer runtime than this one
			namedFiles["Eto"] = FindPackage("Eto", version, RuntimeFrameworks(), "eto.forms") ?? eto;
			namedFiles[platform.AssemblyName] = platformFile;
			foreach (var name in platform.CompanionNames)
			{
				var file = FindBeside(name, Path.GetDirectoryName(platformFile));
				if (file != null)
					namedFiles[name] = file;
				else
					SetFallback(name);
			}
			foreach (var name in SerializationNames)
			{
				var file = FindBeside(name, etoDirectory) ?? FindPackage(name, version, RuntimeFrameworks());
				if (file != null)
					namedFiles[name] = file;
				else
					SetFallback(name);
			}
			foreach (var name in SharedNames.Concat(platform.SharedNames))
			{
				var file = probeDirectories.Select(r => FindBeside(name, r)).FirstOrDefault(r => r != null);
				if (file != null)
					namedFiles[name] = file;
				else
					SetFallback(name);
			}
			log?.Invoke($"Using Eto {version} from {namedFiles["Eto"]} with {platformFile}");
		}

		static void SetFallback(string name)
		{
			var file = Path.Combine(FallbackDirectory, name + ".dll");
			if (File.Exists(file))
				namedFiles[name] = file;
		}

		static string FindBeside(string name, string directory)
		{
			if (directory == null)
				return null;
			var file = Path.Combine(directory, name + ".dll");
			return File.Exists(file) ? file : null;
		}

		/// <summary>Eto platform of the same version as Eto, preferring a build for this runtime from the package over one next to the project.</summary>
		static string FindPlatform(string version, string etoDirectory)
		{
			var name = platform.AssemblyName;
			var fromPackage = FindPackage(name, version, platform.GetFrameworks, platform.PackageId);
			if (fromPackage != null)
				return fromPackage;

			var beside = FindBeside(name, etoDirectory) ?? probeDirectories.Select(r => FindBeside(name, r)).FirstOrDefault(r => r != null);
			return beside != null && GetPackageVersion(beside) == version ? beside : null;
		}

		// newest first, but never newer than the runtime we're on
		public static IEnumerable<string> RuntimeFrameworks()
		{
			for (var major = Environment.Version.Major; major >= 5; major--)
				yield return $"net{major}.0";
			yield return "netstandard2.0";
		}

		static string FindPackage(string name, string version, IEnumerable<string> frameworks, string packageId = null) =>
			FindPackage(name, version, _ => frameworks, packageId);

		/// <param name="getFrameworks">Given the lib folders in the package, returns the ones to use, best first.</param>
		static string FindPackage(string name, string version, Func<IEnumerable<string>, IEnumerable<string>> getFrameworks, string packageId = null)
		{
			packageId = (packageId ?? name).ToLowerInvariant();
			foreach (var folder in GetPackageFolders())
			{
				var lib = Path.Combine(folder, packageId, version.ToLowerInvariant(), "lib");
				if (!Directory.Exists(lib))
					continue;
				var available = Directory.EnumerateDirectories(lib).Select(Path.GetFileName).ToList();
				foreach (var framework in getFrameworks(available))
				{
					var file = Path.Combine(lib, framework, name + ".dll");
					if (File.Exists(file))
						return file;
				}
			}
			return null;
		}

		static IEnumerable<string> GetPackageFolders()
		{
			var configured = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
			if (!string.IsNullOrEmpty(configured))
				yield return configured;
			yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
		}

		static string GetPackageVersion(string file)
		{
			var version = FileVersionInfo.GetVersionInfo(file).ProductVersion;
			if (string.IsNullOrEmpty(version))
				return null;
			var plus = version.IndexOf('+');
			return plus >= 0 ? version.Substring(0, plus) : version;
		}

		static Assembly Resolve(AssemblyLoadContext context, AssemblyName name)
		{
			if (loaded.TryGetValue(name.Name, out var assembly))
				return assembly;

			var file = FindFile(name);
			if (file == null)
				return null;
			try
			{
				return Load(file);
			}
			catch (Exception ex) when (ex is IOException || ex is BadImageFormatException || ex is UnauthorizedAccessException)
			{
				log?.Invoke($"Could not load {file}: {ex.Message}");
				return null;
			}
		}

		static string FindFile(AssemblyName name)
		{
			if (namedFiles.TryGetValue(name.Name, out var named))
				return named;

			var own = Paths.FirstOrDefault(r => string.Equals(Path.GetFileNameWithoutExtension(r), name.Name, StringComparison.OrdinalIgnoreCase));
			if (own != null)
				return own;

			var resolved = dependencyResolver?.ResolveAssemblyToPath(name);
			if (resolved != null && File.Exists(resolved))
				return resolved;

			foreach (var directory in probeDirectories)
			{
				foreach (var extension in new[] { ".dll", ".exe" })
				{
					var candidate = Path.Combine(directory, name.Name + extension);
					if (File.Exists(candidate))
						return candidate;
				}
			}

			var fallback = Path.Combine(FallbackDirectory, name.Name + ".dll");
			return File.Exists(fallback) ? fallback : null;
		}

		static Assembly Load(string file)
		{
			lock (loadLock)
			{
				var name = AssemblyName.GetAssemblyName(file).Name;
				if (loaded.TryGetValue(name, out var existing))
					return existing;

				Assembly assembly;
				if (IsImmutable(file))
					assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(file);
				else
				{
					// read into memory so the project can still be rebuilt while the preview is open
					using (var stream = new MemoryStream(File.ReadAllBytes(file)))
						assembly = AssemblyLoadContext.Default.LoadFromStream(stream);
					watchedFiles.Add(file);
				}
				loaded[name] = assembly;
				return assembly;
			}
		}

		// package and bundled copies never change, so they can be loaded in place
		static bool IsImmutable(string file) =>
			file.StartsWith(FallbackDirectory, StringComparison.OrdinalIgnoreCase)
			|| GetPackageFolders().Any(r => file.StartsWith(r, StringComparison.OrdinalIgnoreCase));

		static IntPtr ResolveUnmanaged(Assembly assembly, string name)
		{
			var path = dependencyResolver?.ResolveUnmanagedDllToPath(name);
			return path != null ? NativeLibrary.Load(path) : IntPtr.Zero;
		}
	}
}
