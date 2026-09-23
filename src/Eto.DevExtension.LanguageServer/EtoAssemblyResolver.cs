using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace Eto.DevExtension.LanguageServer
{
	/// <summary>
	/// Resolves Eto assemblies from the open project, falling back to the copy shipped with
	/// the extension. Install it before anything touches an Eto type.
	/// </summary>
	public static class EtoAssemblyResolver
	{
		static readonly List<string> probePaths = new List<string>();
		static Action<string> log;

		/// <summary>Folder Eto was ultimately loaded from, for reporting to the user.</summary>
		public static string ResolvedFrom { get; private set; }

		public static void Install(Action<string> logger)
		{
			log = logger;
			var fallback = Path.Combine(AppContext.BaseDirectory, "eto");
			if (Directory.Exists(fallback))
				probePaths.Add(fallback);
			AssemblyLoadContext.Default.Resolving += Resolve;
		}

		/// <summary>
		/// Points resolution at a project's assembly folder. Only the first call takes effect,
		/// since an assembly cannot be swapped out once loaded.
		/// </summary>
		/// <returns>True if this folder is the one in use.</returns>
		public static bool UseProjectPath(string path)
		{
			if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
				return false;
			if (ResolvedFrom != null)
				return string.Equals(ResolvedFrom, path, StringComparison.OrdinalIgnoreCase);

			probePaths.Insert(0, path);
			ResolvedFrom = path;
			return true;
		}

		static Assembly Resolve(AssemblyLoadContext context, AssemblyName name)
		{
			foreach (var probe in probePaths)
			{
				var file = Path.Combine(probe, name.Name + ".dll");
				if (!File.Exists(file))
					continue;
				log?.Invoke($"Loading {name.Name} from {file}");
				return context.LoadFromAssemblyPath(file);
			}
			return null;
		}
	}
}
