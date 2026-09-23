using Eto.DevExtension.VisualStudio.Windows.Editor;
using Eto.DevExtension.VisualStudio.Windows.Util;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Eto.DevExtension.VisualStudio.Intellisense
{
	/// <summary>
	/// Build output of the project a designer file belongs to and the projects it references, as
	/// Visual Studio knows it, so the language server doesn't have to guess at custom output paths.
	/// </summary>
	static class ProjectAssemblyPaths
	{
		// reading the project system needs the UI thread, so don't do it on every keystroke
		static readonly TimeSpan CacheTime = TimeSpan.FromSeconds(10);
		static readonly ConcurrentDictionary<string, (DateTime Time, List<string> Paths)> cache = new ConcurrentDictionary<string, (DateTime, List<string>)>(StringComparer.OrdinalIgnoreCase);

		/// <returns>The assembly files, the project's own first, or null when the project isn't known or built.</returns>
		public static async Task<List<string>> GetAsync(string filePath)
		{
			if (string.IsNullOrEmpty(filePath))
				return null;
			if (cache.TryGetValue(filePath, out var cached) && DateTime.UtcNow - cached.Time < CacheTime)
				return cached.Paths;

			List<string> paths = null;
			try
			{
				await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
				var project = TextViewExtensions.GetContainingProject(filePath);
				var output = project != null ? EditorFactory.GetAssemblyPath(project) : null;
				if (output != null && File.Exists(output))
				{
					paths = new List<string> { output };
					AddReferences(project, paths, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { project.UniqueName });
				}
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"Could not read project outputs for {filePath}: {ex}");
			}

			cache[filePath] = (DateTime.UtcNow, paths);
			return paths;
		}

		static void AddReferences(EnvDTE.Project project, List<string> paths, HashSet<string> visited)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			if (!(project.Object is VSLangProj.VSProject vsproject))
				return;

			foreach (VSLangProj.Reference reference in vsproject.References)
			{
				var source = reference.SourceProject;
				if (source == null || !visited.Add(source.UniqueName))
					continue;

				var path = EditorFactory.GetAssemblyPath(source);
				if (path != null && File.Exists(path))
					paths.Add(path);
				AddReferences(source, paths, visited);
			}
		}
	}
}
