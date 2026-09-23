using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Eto.DevExtension.LanguageServer
{
	/// <summary>
	/// Finds the built assemblies of the project a designer file belongs to and the projects it references.
	/// </summary>
	/// <remarks>
	/// Looks for the newest build under each project's bin folder, so a project that builds elsewhere
	/// needs its paths supplied by the client instead.
	/// </remarks>
	public static class ProjectAssemblyLocator
	{
		static readonly string[] ProjectExtensions = { "*.csproj", "*.fsproj", "*.vbproj" };
		static readonly Regex assemblyNameReg = new Regex(@"<AssemblyName>\s*(?<name>[^<$]+?)\s*</AssemblyName>", RegexOptions.Compiled);
		static readonly Regex projectReferenceReg = new Regex(@"<ProjectReference\s+Include\s*=\s*""(?<path>[^""]+)""", RegexOptions.Compiled);

		// scanning bin on every keystroke is too slow, but a rebuild into a new folder should still be noticed
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
			var main = FindOutput(project);
			if (main != null)
			{
				paths.Add(main);
				var outputDir = Path.GetDirectoryName(main);
				var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { project };
				AddReferences(project, outputDir, paths, visited);
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

		static void AddReferences(string project, string outputDir, List<string> paths, HashSet<string> visited)
		{
			foreach (var reference in ReadProjectReferences(project))
			{
				if (!visited.Add(reference) || !File.Exists(reference))
					continue;

				// prefer the copy built alongside the main project so the versions match
				var name = GetAssemblyName(reference);
				var local = new[] { ".dll", ".exe" }.Select(r => Path.Combine(outputDir, name + r)).FirstOrDefault(File.Exists);
				var path = local ?? FindOutput(reference);
				if (path != null && !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
					paths.Add(path);

				AddReferences(reference, outputDir, paths, visited);
			}
		}

		static IEnumerable<string> ReadProjectReferences(string project)
		{
			var dir = Path.GetDirectoryName(project);
			return projectReferenceReg.Matches(ReadText(project))
				.Cast<Match>()
				.Select(r => Path.GetFullPath(Path.Combine(dir, r.Groups["path"].Value.Replace('\\', Path.DirectorySeparatorChar))))
				.ToList();
		}

		static string GetAssemblyName(string project)
		{
			var match = assemblyNameReg.Match(ReadText(project));
			return match.Success ? match.Groups["name"].Value : Path.GetFileNameWithoutExtension(project);
		}

		/// <summary>Newest build of the project's assembly under its bin folder.</summary>
		static string FindOutput(string project)
		{
			var bin = Path.Combine(Path.GetDirectoryName(project), "bin");
			if (!Directory.Exists(bin))
				return null;

			var name = GetAssemblyName(project);
			try
			{
				// an .exe next to a .dll is the native launcher of a .NET app, so only fall back to it
				return new[] { ".dll", ".exe" }
					.Select(extension => new DirectoryInfo(bin)
						.EnumerateFiles(name + extension, SearchOption.AllDirectories)
						// skip ref/ reference assemblies, which have no usable types
						.Where(r => !string.Equals(r.Directory?.Name, "ref", StringComparison.OrdinalIgnoreCase))
						.OrderByDescending(r => r.LastWriteTimeUtc)
						.FirstOrDefault())
					.FirstOrDefault(r => r != null)?.FullName;
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
			{
				return null;
			}
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
