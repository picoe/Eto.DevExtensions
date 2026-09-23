using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace Eto.DevExtension.LanguageServer
{
	/// <summary>
	/// Loads a project's built assemblies so completion can reflect over them, reloading after each build.
	/// </summary>
	/// <remarks>
	/// Files are read into memory so builds can still overwrite them, and each set lives in its own
	/// context so the previous build can be unloaded.
	/// </remarks>
	public static class ProjectAssemblyLoader
	{
		class LoadedSet
		{
			public string Stamp;
			public ProjectLoadContext Context;
			public IList<Assembly> Assemblies;
		}

		static readonly Dictionary<string, LoadedSet> sets = new Dictionary<string, LoadedSet>(StringComparer.OrdinalIgnoreCase);
		static readonly object loadLock = new object();

		/// <param name="paths">Assembly files, the project's own first.</param>
		public static IList<Assembly> Load(IList<string> paths, Action<string> log = null)
		{
			var files = paths?.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			if (files == null || files.Count == 0)
				return Array.Empty<Assembly>();

			var key = string.Join("|", files);
			var stamp = GetStamp(files);
			lock (loadLock)
			{
				if (sets.TryGetValue(key, out var set) && set.Stamp == stamp)
					return set.Assemblies;

				set?.Context.Unload();
				sets.Remove(key);

				// project assemblies must bind to the Eto completion reflects over, so make sure it's loaded first
				_ = typeof(Eto.Widget).Assembly;
				_ = typeof(Eto.Serialization.Xaml.XamlReader).Assembly;

				var context = new ProjectLoadContext(files);
				var assemblies = new List<Assembly>();
				foreach (var file in files)
				{
					try
					{
						assemblies.Add(context.LoadFile(file));
					}
					catch (Exception ex) when (ex is IOException || ex is BadImageFormatException || ex is UnauthorizedAccessException)
					{
						// likely mid-build, so don't cache and try again next time
						log?.Invoke($"Could not load {file}: {ex.Message}");
						context.Unload();
						return Array.Empty<Assembly>();
					}
				}

				log?.Invoke($"Loaded project assemblies: {string.Join(", ", files.Select(Path.GetFileName))}");
				sets[key] = new LoadedSet { Stamp = stamp, Context = context, Assemblies = assemblies };
				return assemblies;
			}
		}

		static string GetStamp(IEnumerable<string> files) =>
			string.Join("|", files.Select(r =>
			{
				var info = new FileInfo(r);
				return info.LastWriteTimeUtc.Ticks + ":" + info.Length;
			}));

		class ProjectLoadContext : AssemblyLoadContext
		{
			readonly Dictionary<string, string> files;
			readonly List<string> directories;

			public ProjectLoadContext(IEnumerable<string> paths) : base("Eto project assemblies", isCollectible: true)
			{
				files = paths.GroupBy(r => Path.GetFileNameWithoutExtension(r), StringComparer.OrdinalIgnoreCase)
					.ToDictionary(r => r.Key, r => r.First(), StringComparer.OrdinalIgnoreCase);
				directories = paths.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			}

			public Assembly LoadFile(string path)
			{
				var name = Path.GetFileNameWithoutExtension(path);
				var loaded = Assemblies.FirstOrDefault(r => string.Equals(r.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
				if (loaded != null)
					return loaded;
				using (var stream = new MemoryStream(File.ReadAllBytes(path)))
					return LoadFromStream(stream);
			}

			protected override Assembly Load(AssemblyName name)
			{
				// share whatever the server already has regardless of version, so Eto types stay the same types
				var shared = Default.Assemblies.FirstOrDefault(r => string.Equals(r.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase));
				if (shared != null)
					return shared;

				if (files.TryGetValue(name.Name, out var file))
					return LoadFile(file);

				foreach (var directory in directories)
				{
					var candidate = Path.Combine(directory, name.Name + ".dll");
					if (File.Exists(candidate))
						return LoadFile(candidate);
				}

				// framework assemblies come from the default context
				return null;
			}
		}
	}
}
