using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
				// the serializers live in their own packages, beside Eto's rather than in its folder
				if (!File.Exists(file) && probe == ResolvedFrom)
					file = FindPackageBeside(name.Name);
				if (file == null || !File.Exists(file))
					continue;
				log?.Invoke($"Loading {name.Name} from {file}");
				return context.LoadFromAssemblyPath(file);
			}
			return null;
		}

		/// <summary>The package of the same version as the project's Eto.dll, eg. eto.serialization.xaml/2.13.0/lib/netstandard2.0.</summary>
		static string FindPackageBeside(string name)
		{
			if (!name.StartsWith("Eto.", StringComparison.Ordinal))
				return null;
			var eto = Path.Combine(ResolvedFrom, "Eto.dll");
			var version = File.Exists(eto) ? FileVersionInfo.GetVersionInfo(eto).ProductVersion : null;
			if (string.IsNullOrEmpty(version))
				return null;
			var plus = version.IndexOf('+');
			if (plus >= 0)
				version = version.Substring(0, plus);

			var packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
			if (string.IsNullOrEmpty(packages))
				packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
			var lib = Path.Combine(packages, name.ToLowerInvariant(), version.ToLowerInvariant(), "lib");
			if (!Directory.Exists(lib))
				return null;
			var file = Path.Combine(lib, "netstandard2.0", name + ".dll");
			return File.Exists(file) ? file : Directory.EnumerateFiles(lib, name + ".dll", SearchOption.AllDirectories).FirstOrDefault();
		}
	}
}
