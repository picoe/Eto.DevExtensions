using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Eto.DevExtension.LanguageServer
{
	/// <summary>
	/// Finds the Eto.dll belonging to the project a designer file lives in, so completions
	/// match the Eto.Forms version the project actually builds against.
	/// </summary>
	public static class EtoAssemblyLocator
	{
		static readonly string[] ProjectExtensions = { "*.csproj", "*.fsproj", "*.vbproj" };

		/// <summary>Returns the folder containing Eto.dll, or null when no project reference is found.</summary>
		public static string Find(string documentPath, Action<string> log = null)
		{
			if (string.IsNullOrEmpty(documentPath))
				return null;

			var dir = Directory.Exists(documentPath) ? new DirectoryInfo(documentPath) : new FileInfo(documentPath).Directory;
			while (dir != null)
			{
				if (ProjectExtensions.Any(p => dir.EnumerateFiles(p).Any()))
				{
					var path = FromAssets(dir.FullName, log) ?? FromBuildOutput(dir.FullName, log);
					if (path != null)
						return path;
				}
				dir = dir.Parent;
			}
			return null;
		}

		/// <summary>Resolves the restored Eto.Forms package out of project.assets.json.</summary>
		static string FromAssets(string projectDir, Action<string> log)
		{
			var assetsPath = Path.Combine(projectDir, "obj", "project.assets.json");
			if (!File.Exists(assetsPath))
				return null;

			try
			{
				var root = JsonNode.Parse(File.ReadAllText(assetsPath)) as JsonObject;
				var libraries = root?["libraries"] as JsonObject;
				var folders = (root?["packageFolders"] as JsonObject)?.Select(r => r.Key).ToList();
				if (libraries == null || folders == null)
					return null;

				foreach (var library in libraries)
				{
					if (!library.Key.StartsWith("Eto.Forms/", StringComparison.OrdinalIgnoreCase))
						continue;

					var relative = (library.Value as JsonObject)?["path"]?.GetValue<string>();
					var files = (library.Value as JsonObject)?["files"] as JsonArray;
					if (relative == null || files == null)
						continue;

					var dll = files.Select(r => r?.GetValue<string>())
						.FirstOrDefault(r => r != null && r.StartsWith("lib/", StringComparison.Ordinal) && r.EndsWith("/Eto.dll", StringComparison.Ordinal));
					if (dll == null)
						continue;

					foreach (var folder in folders)
					{
						var full = Path.GetFullPath(Path.Combine(folder, relative, dll.Replace('/', Path.DirectorySeparatorChar)));
						if (!File.Exists(full))
							continue;
						log?.Invoke($"Using Eto from {library.Key} ({full})");
						return Path.GetDirectoryName(full);
					}
				}
			}
			catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
			{
				log?.Invoke($"Could not read {assetsPath}: {ex.Message}");
			}
			return null;
		}

		/// <summary>Falls back to the newest Eto.dll under the project's build output.</summary>
		static string FromBuildOutput(string projectDir, Action<string> log)
		{
			var bin = Path.Combine(projectDir, "bin");
			if (!Directory.Exists(bin))
				return null;

			try
			{
				var newest = new DirectoryInfo(bin)
					.EnumerateFiles("Eto.dll", SearchOption.AllDirectories)
					.OrderByDescending(r => r.LastWriteTimeUtc)
					.FirstOrDefault();
				if (newest == null)
					return null;
				log?.Invoke($"Using Eto from build output ({newest.FullName})");
				return newest.DirectoryName;
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
			{
				log?.Invoke($"Could not scan {bin}: {ex.Message}");
				return null;
			}
		}
	}
}
