using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Eto.DevExtension.LanguageServer
{
	/// <summary>
	/// Finds the built assemblies of the project a designer file belongs to and the projects it references.
	/// </summary>
	public static class ProjectAssemblyLocator
	{
		static readonly string[] ProjectExtensions = { "*.csproj", "*.fsproj", "*.vbproj" };

		// scanning the output folders on every keystroke is too slow, but a rebuild into a new folder should still be noticed
		static readonly TimeSpan CacheTime = TimeSpan.FromSeconds(10);
		static readonly ConcurrentDictionary<string, (DateTime Time, List<string> Paths)> cache = new ConcurrentDictionary<string, (DateTime, List<string>)>(StringComparer.OrdinalIgnoreCase);

		/// <summary>Assembly files for the document's project, its own first, or empty when it hasn't been built.</summary>
		public static List<string> Find(string documentPath, Action<string> log = null)
		{
			var project = FindProject(documentPath);
			if (project == null)
				return new List<string>();

			if (cache.TryGetValue(project, out var cached) && DateTime.UtcNow - cached.Time < CacheTime)
				return cached.Paths;

			var paths = new List<string>();
			var main = FindOutput(project, log);
			if (main != null)
			{
				paths.Add(main);
				var outputDir = Path.GetDirectoryName(main);
				var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { project };
				AddReferences(project, outputDir, paths, visited, log);
			}
			else
				log?.Invoke($"No build output found for {project}, so its types won't be offered until it is built.");

			cache[project] = (DateTime.UtcNow, paths);
			return paths;
		}

		static string FindProject(string documentPath)
		{
			if (string.IsNullOrEmpty(documentPath))
				return null;
			var dir = new FileInfo(documentPath).Directory;
			while (dir != null)
			{
				var project = ProjectExtensions.SelectMany(p => dir.EnumerateFiles(p)).FirstOrDefault();
				if (project != null)
					return project.FullName;
				dir = dir.Parent;
			}
			return null;
		}

		static void AddReferences(string project, string outputDir, List<string> paths, HashSet<string> visited, Action<string> log)
		{
			foreach (var reference in ProjectProperties.Get(project, log).ProjectReferences)
			{
				if (!visited.Add(reference) || !File.Exists(reference))
					continue;

				// prefer the copy built alongside the main project so the versions match
				var name = ProjectProperties.Get(reference, log).AssemblyName;
				var local = new[] { ".dll", ".exe" }.Select(r => Path.Combine(outputDir, name + r)).FirstOrDefault(File.Exists);
				var path = local ?? FindOutput(reference, log);
				if (path != null && !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
					paths.Add(path);

				AddReferences(reference, outputDir, paths, visited, log);
			}
		}

		/// <summary>Newest build of the project's assembly in its output folders.</summary>
		static string FindOutput(string project, Action<string> log)
		{
			var properties = ProjectProperties.Get(project, log);
			// an .exe next to a .dll is the native launcher of a .NET app, so only fall back to it
			return new[] { ".dll", ".exe" }
				.Select(extension => properties.FindNewest(properties.AssemblyName + extension, log))
				.FirstOrDefault(r => r != null)?.FullName;
		}
	}
}
