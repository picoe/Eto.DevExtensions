using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Eto.Designer.Completion
{
	/// <summary>
	/// Type lookups over the assemblies of the project being edited, tolerant of types that fail to load.
	/// </summary>
	static class ProjectTypes
	{
		const string ClrNamespacePrefix = "clr-namespace:";

		static readonly ConditionalWeakTable<Assembly, List<Type>> types = new ConditionalWeakTable<Assembly, List<Type>>();
		static readonly ConditionalWeakTable<Assembly, List<string>> namespaces = new ConditionalWeakTable<Assembly, List<string>>();

		/// <summary>Public types of the assembly, skipping any whose dependencies can't be found.</summary>
		public static List<Type> GetTypes(Assembly assembly) => types.GetValue(assembly, LoadTypes);

		static List<Type> LoadTypes(Assembly assembly)
		{
			try
			{
				return assembly.GetExportedTypes().ToList();
			}
			catch (Exception)
			{
				try
				{
					return assembly.GetTypes().Where(r => r.IsVisible).ToList();
				}
				catch (ReflectionTypeLoadException ex)
				{
					return ex.Types.Where(r => r != null && r.IsVisible).ToList();
				}
				catch (Exception)
				{
					return new List<Type>();
				}
			}
		}

		/// <summary>Namespaces holding at least one type a designer file could create.</summary>
		public static List<string> GetNamespaces(Assembly assembly) =>
			namespaces.GetValue(assembly, a => GetTypes(a)
				.Where(r => IsCreatable(r) && !string.IsNullOrEmpty(r.Namespace))
				.Select(r => r.Namespace)
				.Distinct()
				.OrderBy(r => r, StringComparer.Ordinal)
				.ToList());

		public static bool IsCreatable(Type type) => !type.IsGenericType && !type.IsAbstract && !type.IsNested;

		public static string GetAssemblyName(Assembly assembly) => assembly.GetName().Name;

		public static string GetClrNamespace(string ns, Assembly assembly) =>
			ClrNamespacePrefix + ns + ";assembly=" + GetAssemblyName(assembly);

		/// <summary>Splits an xmlns value such as "clr-namespace:My.Controls;assembly=MyApp".</summary>
		public static bool TryParseClrNamespace(string value, out string ns, out string assemblyName)
		{
			ns = assemblyName = null;
			if (value == null || !value.StartsWith(ClrNamespacePrefix, StringComparison.Ordinal))
				return false;

			foreach (var part in value.Substring(ClrNamespacePrefix.Length).Split(';'))
			{
				var trimmed = part.Trim();
				if (trimmed.StartsWith("assembly=", StringComparison.Ordinal))
					assemblyName = trimmed.Substring("assembly=".Length).Trim();
				else if (ns == null)
					ns = trimmed;
			}
			return !string.IsNullOrEmpty(ns);
		}

		/// <summary>
		/// Assembly an xmlns refers to. Without an assembly name it is the project's own, as Eto's
		/// xaml reader uses the assembly of the object being loaded.
		/// </summary>
		public static Assembly FindAssembly(string assemblyName, IList<Assembly> projectAssemblies)
		{
			if (string.IsNullOrEmpty(assemblyName))
				return projectAssemblies?.FirstOrDefault();

			var project = projectAssemblies?.FirstOrDefault(r => GetAssemblyName(r) == assemblyName);
			if (project != null)
				return project;

			// prefer the copies completion already reflects over, eg. clr-namespace:Eto.Drawing;assembly=Eto
			foreach (var known in new[] { typeof(Eto.Widget).Assembly, typeof(Eto.Serialization.Xaml.XamlReader).Assembly })
			{
				if (GetAssemblyName(known) == assemblyName)
					return known;
			}
			return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(r => !r.IsDynamic && GetAssemblyName(r) == assemblyName);
		}
	}
}
