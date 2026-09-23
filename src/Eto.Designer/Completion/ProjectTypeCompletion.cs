using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Eto.Designer.Completion
{
	/// <summary>
	/// Offers project types from namespaces the xaml document hasn't declared, each carrying the xmlns to add.
	/// </summary>
	class ProjectTypeCompletion : Completion
	{
		readonly List<KeyValuePair<TypeCompletion, CompletionNamespace>> sources = new List<KeyValuePair<TypeCompletion, CompletionNamespace>>();

		public ProjectTypeCompletion(IList<CompletionNamespace> declared, IList<Assembly> projectAssemblies)
		{
			var usedPrefixes = new HashSet<string>(declared.Select(r => r.Prefix ?? string.Empty));
			foreach (var assembly in projectAssemblies)
			{
				foreach (var ns in ProjectTypes.GetNamespaces(assembly))
				{
					if (declared.Any(r => IsDeclaration(r, ns, assembly, projectAssemblies)))
						continue;

					var prefix = CreatePrefix(ns, usedPrefixes);
					sources.Add(new KeyValuePair<TypeCompletion, CompletionNamespace>(
						new TypeCompletion { Prefix = prefix, Assembly = assembly, Namespace = ns },
						new CompletionNamespace { Prefix = prefix, Namespace = ProjectTypes.GetClrNamespace(ns, assembly) }));
				}
			}
		}

		static bool IsDeclaration(CompletionNamespace declared, string ns, Assembly assembly, IList<Assembly> projectAssemblies) =>
			ProjectTypes.TryParseClrNamespace(declared.Namespace, out var declaredNs, out var assemblyName)
			&& declaredNs == ns
			&& ProjectTypes.FindAssembly(assemblyName, projectAssemblies) == assembly;

		/// <summary>Prefix from the last part of the namespace, eg. "controls" for MyApp.Controls.</summary>
		static string CreatePrefix(string ns, HashSet<string> used)
		{
			var name = ns.Substring(ns.LastIndexOf('.') + 1);
			var sb = new StringBuilder();
			foreach (var ch in name)
			{
				if (char.IsLetterOrDigit(ch) || ch == '_')
					sb.Append(char.ToLowerInvariant(ch));
			}
			if (sb.Length == 0 || !char.IsLetter(sb[0]))
				sb.Insert(0, "local");

			var baseName = sb.ToString();
			var prefix = baseName;
			// "x" is taken implicitly by Eto's reader even when not declared
			for (var i = 2; prefix == "x" || !used.Add(prefix); i++)
				prefix = baseName + i;
			return prefix;
		}

		public override IEnumerable<CompletionItem> GetClasses(IEnumerable<string> path, Func<Type, bool> filter)
		{
			// with no content type to match there is nothing sensible to offer
			if (filter == null)
				yield break;

			foreach (var source in sources)
			{
				foreach (var item in source.Key.GetClasses(path, filter))
				{
					if (item.Type != CompletionType.Class)
						continue;
					item.Namespace = source.Value;
					item.Suffix = source.Key.Namespace;
					yield return item;
				}
			}
		}

		public override IEnumerable<CompletionItem> GetProperties(string objectName, IEnumerable<string> path)
		{
			yield break;
		}

		public override bool HandlesPrefix(string prefix) => true;
	}
}
