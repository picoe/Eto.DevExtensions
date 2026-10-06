using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.IO;
using System.Text;
using System.Collections;

namespace Eto.Designer.Completion
{
	public class CompletionNamespace
	{
		public string Prefix { get; set; }

		public string Namespace { get; set; }
	}

	public enum CompletionMode
	{
		None,
		Class,
		Property,
		Value
	}

	public class CompletionPathNode
	{
		public List<CompletionNamespace> Namespaces { get; set; }
		List<string> attributes;
		public List<string> Attributes { get { return attributes ?? (attributes = new List<string>()); } }
		public string LocalName { get; set; }
		public string Prefix { get; set; }
		public CompletionMode Mode { get; set; }

		public string Name
		{
			get {
				return string.IsNullOrEmpty(Prefix) ? LocalName : Prefix + ":" + LocalName;
			}
		}

		public CompletionPathNode(string prefix, string localName, CompletionMode mode)
		{
			if (!string.IsNullOrEmpty(prefix) && prefix.EndsWith(":"))
				prefix = prefix.TrimEnd(':');
			Prefix = prefix;
			LocalName = localName;
			Mode = mode;
		}
	}

	public abstract class Completion
	{
		public string Prefix { get; set; }

		public virtual Func<Type, bool> GetFilter(IEnumerable<string> path)
		{
			return null;
		}

		public abstract IEnumerable<CompletionItem> GetClasses(IEnumerable<string> path, Func<Type, bool> filter);

		public abstract IEnumerable<CompletionItem> GetProperties(string objectName, IEnumerable<string> path);

		public virtual IEnumerable<CompletionItem> GetPropertyValues(string objectName, string propertyName, IEnumerable<string> path)
		{
			yield break;
		}

		/// <summary>
		/// Type name implied by the end of the path, for objects that don't name their own type.
		/// </summary>
		public virtual string GetImpliedTypeName(IEnumerable<string> path)
		{
			return null;
		}

		/// <summary>True when <paramref name="node"/> is a property element of a dictionary, eg. Panel.Properties.</summary>
		public virtual bool IsDictionaryContent(string node) => false;

		/// <summary>
		/// Determine whether the specified objectName has content, or null if not known by this completion handler.
		/// </summary>
		public virtual bool? HasContent(string objectName, IEnumerable<string> path)
		{
			return null;
		}

		public string PrefixWithColon
		{
			get { return string.IsNullOrEmpty(Prefix) ? string.Empty : Prefix + ":"; }
		}

		public virtual bool HandlesPrefix(string prefix)
		{
			return prefix == Prefix || string.IsNullOrEmpty(prefix) == string.IsNullOrEmpty(Prefix);
		}

		public const string EtoFormsNamespace = "http://schema.picoe.ca/eto.forms";
		public const string XamlNamespace2006 = "http://schemas.microsoft.com/winfx/2006/xaml";
		public const string DesignNamespace = "http://schema.picoe.ca/eto.forms/design";
		public const string MarkupCompatibilityNamespace = "http://schemas.openxmlformats.org/markup-compatibility/2006";

		public static IEnumerable<CompletionItem> GetCompletionItems(IEnumerable<CompletionNamespace> namespaces, CompletionMode mode, IEnumerable<string> path, CompletionPathNode context, CompletionFormat format = CompletionFormat.Xaml, IList<Assembly> projectAssemblies = null)
		{
			if (mode == CompletionMode.None)
				return Enumerable.Empty<CompletionItem>();
			var completions = GetCompletions(namespaces, format, projectAssemblies).ToList();
			IEnumerable<CompletionItem> items;
			if (mode == CompletionMode.Property && context != null)
			{
				var contextName = context.Name;
				if (string.IsNullOrEmpty(contextName))
				{
					// json objects may leave out $type, in which case the property they sit in names the type
					contextName = GetImpliedTypeName(completions, path);
				}
				else if (contextName.EndsWith("."))
				{
					// if it contains a dot it is a property element. only show completions for the current namespace.
					contextName = contextName.TrimEnd('.');
					completions = completions.Where(r => r.Prefix == context.Prefix).ToList();
				}
				var isPropertyElement = context.Name.EndsWith(".");
				items = completions
					.SelectMany(r => r.GetProperties(contextName, path))
					.Where(r => !context.Attributes.Contains(r.Name))
					.Where(r => format == CompletionFormat.Json || isPropertyElement || !r.Behavior.HasFlag(CompletionBehavior.PropertyElementOnly));

				// entries of a dictionary such as Widget.Properties need a key
				var parent = path.Reverse().Skip(1).FirstOrDefault();
				if (format == CompletionFormat.Xaml && !isPropertyElement && parent != null && completions.Any(r => r.IsDictionaryContent(parent)))
				{
					var x = completions.OfType<XamlCompletion>().FirstOrDefault()?.PrefixWithColon ?? "x:";
					if (!context.Attributes.Contains(x + "Key"))
						items = items.Concat(new[] { new CompletionItem { Name = x + "Key", Type = CompletionType.Attribute, Description = "Key to add this object to the dictionary with, eg. to use it with {StaticResource}." } });
				}
			}
			else if (mode == CompletionMode.Value && context != null && context.Mode == CompletionMode.Property)
			{
				var lastPath = path.LastOrDefault();
				if (lastPath != null && lastPath.Contains("."))
					lastPath = GetImpliedTypeName(completions, path) ?? lastPath;
				items = completions.SelectMany(r => r.GetPropertyValues(lastPath, context.LocalName, path));
			}
			else
			{
				var filter = completions.Select(r => r.GetFilter(path)).Where(r => r != null).FirstOrDefault();
				items = completions.SelectMany(r => r.GetClasses(path, filter));
			}
			if (format == CompletionFormat.Json)
			{
				// property elements are a xaml-only construct
				items = items.Where(r => !r.Behavior.HasFlag(CompletionBehavior.ChildProperty));
			}
			return items;
		}

		static string GetImpliedTypeName(IEnumerable<Completion> completions, IEnumerable<string> path) =>
			completions.Select(r => r.GetImpliedTypeName(path)).FirstOrDefault(r => !string.IsNullOrEmpty(r));

		/// <summary>
		/// Declared namespaces plus the ones Eto's xaml reader adds implicitly, so files that omit
		/// xmlns - the usual case when loading into an existing instance - still complete.
		/// </summary>
		static IEnumerable<CompletionNamespace> GetEffectiveNamespaces(IEnumerable<CompletionNamespace> namespaces)
		{
			var effective = namespaces?.ToList() ?? new List<CompletionNamespace>();
			if (!effective.Any(r => string.IsNullOrEmpty(r.Prefix)))
				effective.Add(new CompletionNamespace { Prefix = string.Empty, Namespace = EtoFormsNamespace });
			if (!effective.Any(r => r.Prefix == "x"))
				effective.Add(new CompletionNamespace { Prefix = "x", Namespace = XamlNamespace2006 });
			return effective;
		}

		/// <param name="projectAssemblies">Built assemblies of the project being edited, its own first.</param>
		public static IEnumerable<Completion> GetCompletions(IEnumerable<CompletionNamespace> namespaces, CompletionFormat format = CompletionFormat.Xaml, IList<Assembly> projectAssemblies = null)
		{
			projectAssemblies = projectAssemblies ?? Array.Empty<Assembly>();
			if (format == CompletionFormat.Json)
			{
				// json has no namespace declarations, Eto.Forms types go by name and anything else by full name
				yield return new JsonCompletion();
				yield return new TypeCompletion { Assembly = typeof(Eto.Widget).Assembly, Namespace = "Eto.Forms" };
				yield return new TypeCompletion { Assembly = typeof(Eto.Widget).Assembly, Namespace = "Eto" };
				// newer Eto finds types without an assembly name in the assembly being loaded, ie. the project's own
				var localAssembly = typeof(Eto.Serialization.Json.NamespaceManager).GetProperty("LocalAssembly") != null ? projectAssemblies.FirstOrDefault() : null;
				foreach (var assembly in projectAssemblies)
					yield return new TypeCompletion { Assembly = assembly, UseFullName = true, OmitAssemblyName = assembly == localAssembly };
				yield break;
			}

			yield return new GeneralCompletion { ProjectAssemblies = projectAssemblies };

			var effective = GetEffectiveNamespaces(namespaces).ToList();
			foreach (var ns in effective)
			{
				if (ProjectTypes.TryParseClrNamespace(ns.Namespace, out var clrNamespace, out var assemblyName))
				{
					var assembly = ProjectTypes.FindAssembly(assemblyName, projectAssemblies);
					if (assembly != null)
						yield return new TypeCompletion { Prefix = ns.Prefix, Assembly = assembly, Namespace = clrNamespace };
				}
				if (ns.Namespace == EtoFormsNamespace)
				{
					yield return new TypeCompletion
					{
						Prefix = ns.Prefix,
						Assembly = typeof(Eto.Widget).Assembly,
						Namespace = "Eto.Forms"
					};
					yield return new TypeCompletion
					{
						Prefix = ns.Prefix,
						Assembly = typeof(Eto.Widget).Assembly,
						Namespace = "Eto"
					};
					yield return new TypeCompletion
					{
						Prefix = ns.Prefix,
						Assembly = typeof(Eto.Serialization.Xaml.XamlReader).Assembly,
						Namespace = "Eto.Serialization.Xaml.Extensions"
					};
				}
				if (ns.Namespace == XamlNamespace2006)
				{
					yield return new XamlCompletion { Prefix = ns.Prefix };
				}
				if (ns.Namespace == DesignNamespace)
				{
					yield return new DesignCompletion { Prefix = ns.Prefix };
				}
			}

			if (projectAssemblies.Count > 0)
				yield return new ProjectTypeCompletion(effective, projectAssemblies);
		}
	}
}