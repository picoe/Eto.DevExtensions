using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Eto.Designer.Completion
{
	/// <summary>
	/// Completes markup extensions in xaml attribute values, eg. <c>{x:Static local:Type.Member}</c>.
	/// </summary>
	/// <remarks>
	/// Extensions and their options are found by reflection over the namespaces the document declares,
	/// so custom extensions complete the same as Eto's own.
	/// </remarks>
	public static class MarkupCompletion
	{
		const RegexOptions opts = RegexOptions.Compiled | RegexOptions.Singleline;
		static readonly Regex xmlnsReg = new Regex(@"\sxmlns(?::(?<prefix>[\w.-]+))?\s*=\s*(?:""(?<ns>[^""]*)""|'(?<ns>[^']*)')", opts);
		static readonly Regex keyReg = new Regex(@"\sx:Key\s*=\s*(?:""(?<key>[^""]*)""|'(?<key>[^']*)')", opts);

		internal enum Kind
		{
			/// <summary>The extension's name, right after the brace.</summary>
			Extension,

			/// <summary>An option name, or the first positional value.</summary>
			Argument,

			/// <summary>The value of a named option.</summary>
			Value
		}

		public class Request
		{
			internal Kind Kind;
			internal string Extension;
			internal string Argument;
			internal bool Positional;
			internal string Partial;
			internal HashSet<string> UsedArguments;
			internal Dictionary<string, string> Namespaces;
			internal List<string> ResourceKeys;
			internal int Offset;

			public int Start { get; set; }

			public int End { get; set; }
		}

		/// <returns>The request, or null when the cursor isn't inside markup.</returns>
		public static Request Read(string text, int offset)
		{
			var valueStart = offset;
			while (valueStart > 0 && text[valueStart - 1] != '"' && text[valueStart - 1] != '\'' && text[valueStart - 1] != '\n')
				valueStart--;
			var typed = text.Substring(valueStart, offset - valueStart);
			if (typed.StartsWith("{}", StringComparison.Ordinal))
				return null; // escaped, so it's literal text

			// the innermost brace still open is the markup being typed
			var open = new Stack<int>();
			for (var i = 0; i < typed.Length; i++)
			{
				if (typed[i] == '{')
					open.Push(i);
				else if (typed[i] == '}' && open.Count > 0)
					open.Pop();
			}
			if (open.Count == 0)
				return null;
			var markupStart = open.Peek() + 1;
			var markup = typed.Substring(markupStart);

			var end = offset;
			while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_' || text[end] == ':' || text[end] == '.'))
				end++;

			var request = new Request
			{
				Namespaces = GetNamespaces(text),
				UsedArguments = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
				Offset = offset,
				End = end
			};

			var nameLength = 0;
			while (nameLength < markup.Length && IsNameChar(markup[nameLength]))
				nameLength++;
			request.Extension = markup.Substring(0, nameLength);
			if (nameLength == markup.Length)
			{
				request.Kind = Kind.Extension;
				request.Partial = request.Extension;
				request.Start = offset - nameLength;
				return request;
			}

			var arguments = SplitArguments(markup.Substring(nameLength));
			var current = arguments[arguments.Count - 1];
			foreach (var argument in arguments.Take(arguments.Count - 1))
			{
				var equals = argument.IndexOf('=');
				if (equals > 0)
					request.UsedArguments.Add(argument.Substring(0, equals).Trim());
			}

			var currentEquals = current.IndexOf('=');
			if (currentEquals > 0)
			{
				request.Kind = Kind.Value;
				request.Argument = current.Substring(0, currentEquals).Trim();
				request.Partial = current.Substring(currentEquals + 1).TrimStart();
			}
			else
			{
				request.Kind = Kind.Argument;
				request.Positional = arguments.Count == 1;
				request.Partial = current.TrimStart();
			}
			if (request.Partial.Any(r => !IsNameChar(r)))
				return null;

			// a member is completed after the dot, eg. local:Type.|
			var dot = request.Partial.LastIndexOf('.');
			request.Start = offset - request.Partial.Length + (dot >= 0 ? dot + 1 : 0);

			if (request.Extension.Contains("Resource"))
				request.ResourceKeys = keyReg.Matches(text).Cast<Match>().Select(r => r.Groups["key"].Value).Distinct().ToList();
			return request;
		}

		static bool IsNameChar(char ch) => char.IsLetterOrDigit(ch) || ch == '_' || ch == ':' || ch == '.' || ch == '-';

		/// <summary>Comma separated arguments, keeping commas inside nested markup.</summary>
		static List<string> SplitArguments(string text)
		{
			var result = new List<string>();
			var depth = 0;
			var start = 0;
			for (var i = 0; i < text.Length; i++)
			{
				if (text[i] == '{')
					depth++;
				else if (text[i] == '}')
					depth--;
				else if (text[i] == ',' && depth == 0)
				{
					result.Add(text.Substring(start, i - start));
					start = i + 1;
				}
			}
			result.Add(text.Substring(start));
			return result;
		}

		static Dictionary<string, string> GetNamespaces(string text)
		{
			var namespaces = new Dictionary<string, string>();
			foreach (Match m in xmlnsReg.Matches(text))
				namespaces[m.Groups["prefix"].Success ? m.Groups["prefix"].Value : string.Empty] = m.Groups["ns"].Value;
			// Eto's xaml reader adds these when the file leaves them out
			if (!namespaces.ContainsKey(string.Empty))
				namespaces[string.Empty] = Completion.EtoFormsNamespace;
			if (!namespaces.ContainsKey("x"))
				namespaces["x"] = Completion.XamlNamespace2006;
			return namespaces;
		}

		public static IEnumerable<CompletionItem> GetItems(Request request, IList<Assembly> projectAssemblies)
		{
			var types = new XamlTypes(request.Namespaces, projectAssemblies);
			if (request.Kind == Kind.Extension)
				return GetExtensions(types);

			var extension = types.FindExtension(request.Extension);
			if (extension == null)
				return Enumerable.Empty<CompletionItem>();

			if (request.Kind == Kind.Value)
			{
				var property = GetProperty(extension, request.Argument);
				return property != null ? GetValues(request, types, extension, property.Name, property.PropertyType, property) : Enumerable.Empty<CompletionItem>();
			}

			// once a member is being typed, eg. local:Type.|, option names no longer fit
			var items = request.Partial.Contains('.') ? Enumerable.Empty<CompletionItem>() : GetArguments(extension, request.UsedArguments);
			if (request.Positional)
			{
				var positional = GetPositional(extension, out var positionalType);
				if (positional != null || positionalType != null)
					items = items.Concat(GetValues(request, types, extension, positional?.Name, positional?.PropertyType ?? positionalType, positional));
			}
			return items;
		}

		static IEnumerable<CompletionItem> GetExtensions(XamlTypes types)
		{
			foreach (var entry in types.All())
			{
				if (!IsMarkupExtension(entry.Type))
					continue;
				var name = entry.Type.Name;
				if (name.EndsWith("Extension", StringComparison.Ordinal) && name.Length > "Extension".Length)
					name = name.Substring(0, name.Length - "Extension".Length);
				yield return new CompletionItem
				{
					Name = (entry.Prefix.Length > 0 ? entry.Prefix + ":" : string.Empty) + name,
					Type = CompletionType.Class,
					Description = XmlComments.GetSummary(entry.Type)
				};
			}
		}

		static IEnumerable<CompletionItem> GetArguments(Type extension, HashSet<string> used)
		{
			foreach (var property in GetSettableProperties(extension))
			{
				if (used.Contains(property.Name))
					continue;
				yield return new CompletionItem
				{
					Name = property.Name + "=",
					Type = CompletionType.Attribute,
					Suffix = GetTypeName(property.PropertyType),
					Description = XmlComments.GetSummary(property)
				};
			}
		}

		/// <summary>Property the first positional argument sets, via its constructor.</summary>
		static PropertyInfo GetPositional(Type extension, out Type parameterType)
		{
			parameterType = null;
			try
			{
				var parameter = extension.GetConstructors().Select(r => r.GetParameters()).FirstOrDefault(r => r.Length == 1)?[0];
				if (parameter == null)
					return null;
				parameterType = parameter.ParameterType;
				var properties = GetSettableProperties(extension).ToList();
				// by name, as the extension may use a different copy of the xaml library
				return properties.FirstOrDefault(r => r.GetCustomAttributesData().Any(a => a.AttributeType.Name == "ConstructorArgumentAttribute"
						&& a.ConstructorArguments.FirstOrDefault().Value as string == parameter.Name))
					?? properties.FirstOrDefault(r => string.Equals(r.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));
			}
			catch (Exception)
			{
				return null;
			}
		}

		static IEnumerable<CompletionItem> GetValues(Request request, XamlTypes types, Type extension, string name, Type valueType, PropertyInfo property)
		{
			if (valueType == null)
				yield break;
			valueType = Nullable.GetUnderlyingType(valueType) ?? valueType;

			if (request.ResourceKeys != null && (valueType == typeof(object) || valueType == typeof(string)))
			{
				foreach (var key in request.ResourceKeys)
					yield return new CompletionItem { Name = key, Type = CompletionType.Literal };
				yield break;
			}

			if (valueType == typeof(bool))
			{
				yield return new CompletionItem { Name = "True", Type = CompletionType.Literal };
				yield return new CompletionItem { Name = "False", Type = CompletionType.Literal };
				yield break;
			}

			if (valueType.IsEnum)
			{
				foreach (var value in Enum.GetNames(valueType))
					yield return new CompletionItem { Name = value, Type = CompletionType.Literal, Description = property != null ? XmlComments.GetEnum(property, value) : null };
				yield break;
			}

			// a static member, eg. x:Static's Member
			var isMember = string.Equals(name, "Member", StringComparison.OrdinalIgnoreCase) || IsExtension(extension, "StaticExtension");
			var isType = valueType == typeof(Type) || string.Equals(name, "TypeName", StringComparison.OrdinalIgnoreCase) || IsExtension(extension, "TypeExtension");
			if (!isMember && !isType)
				yield break;

			var dot = request.Partial.LastIndexOf('.');
			if (isMember && dot > 0)
			{
				var owner = types.FindType(request.Partial.Substring(0, dot));
				if (owner == null)
					yield break;
				foreach (var member in GetStaticMembers(owner))
					yield return member;
				yield break;
			}

			foreach (var entry in types.All())
			{
				if (!(isMember ? HasStaticMembers(entry.Type) : IsInstanceType(entry.Type)))
					continue;
				yield return new CompletionItem
				{
					Name = (entry.Prefix.Length > 0 ? entry.Prefix + ":" : string.Empty) + entry.Type.Name,
					Type = CompletionType.Class,
					Description = XmlComments.GetSummary(entry.Type)
				};
			}
		}

		static IEnumerable<CompletionItem> GetStaticMembers(Type type)
		{
			const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
			IEnumerable<MemberInfo> members;
			try
			{
				members = type.GetProperties(flags).Where(r => r.GetIndexParameters().Length == 0).Cast<MemberInfo>()
					.Concat(type.GetFields(flags).Where(r => !r.IsSpecialName))
					.ToList();
			}
			catch (Exception)
			{
				yield break;
			}
			foreach (var member in members.GroupBy(r => r.Name).Select(r => r.First()))
			{
				var property = member as PropertyInfo;
				yield return new CompletionItem
				{
					Name = member.Name,
					Type = property != null ? CompletionType.Property : CompletionType.Field,
					Suffix = GetTypeName(property?.PropertyType ?? ((FieldInfo)member).FieldType),
					Description = property != null ? XmlComments.GetSummary(property) : null
				};
			}
		}

		static bool IsExtension(Type type, string name) => type.Name == name;

		static bool IsInstanceType(Type type) =>
			!type.IsGenericTypeDefinition
			&& !(type.IsAbstract && type.IsSealed)
			&& !typeof(Attribute).IsAssignableFrom(type)
			&& !typeof(Delegate).IsAssignableFrom(type)
			&& !IsMarkupExtension(type);

		static bool HasStaticMembers(Type type)
		{
			if (type.IsGenericTypeDefinition)
				return false;
			try
			{
				const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
				return type.GetProperties(flags).Any(r => r.GetIndexParameters().Length == 0) || type.GetFields(flags).Any(r => !r.IsSpecialName);
			}
			catch (Exception)
			{
				return false;
			}
		}

		static IEnumerable<PropertyInfo> GetSettableProperties(Type type)
		{
			try
			{
				return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
					.Where(r => r.SetMethod?.IsPublic == true && r.GetIndexParameters().Length == 0 && r.GetCustomAttribute<ObsoleteAttribute>() == null)
					.ToList();
			}
			catch (Exception)
			{
				return Enumerable.Empty<PropertyInfo>();
			}
		}

		static PropertyInfo GetProperty(Type type, string name) =>
			GetSettableProperties(type).FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

		/// <summary>True for a markup extension, by name so any copy of the xaml library counts.</summary>
		static bool IsMarkupExtension(Type type)
		{
			if (type.IsAbstract || type.IsGenericTypeDefinition || !type.IsPublic && !type.IsNestedPublic)
				return false;
			for (var current = type.BaseType; current != null; current = current.BaseType)
			{
				if (current.Name == "MarkupExtension")
					return true;
			}
			return false;
		}

		static string GetTypeName(Type type)
		{
			var underlying = Nullable.GetUnderlyingType(type);
			if (underlying != null)
				return underlying.Name + "?";
			if (type.IsGenericType)
				return type.Name.Substring(0, type.Name.IndexOf('`')) + "<" + string.Join(", ", type.GetGenericArguments().Select(GetTypeName)) + ">";
			return type.Name;
		}

		/// <summary>The types each declared xml namespace brings in.</summary>
		class XamlTypes
		{
			public struct Entry
			{
				public string Prefix;
				public Type Type;
			}

			static readonly ConditionalWeakTable<Assembly, List<KeyValuePair<string, KeyValuePair<string, string>>>> definitions =
				new ConditionalWeakTable<Assembly, List<KeyValuePair<string, KeyValuePair<string, string>>>>();

			readonly Dictionary<string, string> namespaces;
			readonly IList<Assembly> projectAssemblies;
			readonly Dictionary<string, List<Type>> cache = new Dictionary<string, List<Type>>();

			public XamlTypes(Dictionary<string, string> namespaces, IList<Assembly> projectAssemblies)
			{
				this.namespaces = namespaces;
				this.projectAssemblies = projectAssemblies ?? Array.Empty<Assembly>();
			}

			public IEnumerable<Entry> All() =>
				namespaces.SelectMany(ns => GetTypes(ns.Key).Select(t => new Entry { Prefix = ns.Key, Type = t }));

			public Type FindExtension(string name) =>
				FindType(name + "Extension") ?? FindType(name);

			/// <summary>Type from a xaml name such as local:MyType.</summary>
			public Type FindType(string name)
			{
				var colon = name.IndexOf(':');
				var prefix = colon >= 0 ? name.Substring(0, colon) : string.Empty;
				var local = name.Substring(colon + 1);
				return GetTypes(prefix).FirstOrDefault(r => r.Name == local);
			}

			List<Type> GetTypes(string prefix)
			{
				if (cache.TryGetValue(prefix, out var types))
					return types;
				types = namespaces.TryGetValue(prefix, out var ns) ? LoadTypes(ns).ToList() : new List<Type>();
				return cache[prefix] = types;
			}

			IEnumerable<Type> LoadTypes(string ns)
			{
				if (ProjectTypes.TryParseClrNamespace(ns, out var clrNamespace, out var assemblyName))
				{
					var assembly = ProjectTypes.FindAssembly(assemblyName, projectAssemblies);
					return assembly != null ? ProjectTypes.GetTypes(assembly).Where(r => r.Namespace == clrNamespace) : Enumerable.Empty<Type>();
				}
				if (ns == Completion.XamlNamespace2006)
					return Portable.Xaml.XamlLanguage.AllTypes.Select(r => r.UnderlyingType).Where(r => r != null);

				// any other namespace is mapped to clr namespaces by [XmlnsDefinition]
				return GetDefiningAssemblies()
					.SelectMany(GetDefinitions)
					.Where(r => r.Key == ns)
					.SelectMany(r =>
					{
						var assembly = ProjectTypes.FindAssembly(r.Value.Value, projectAssemblies);
						return assembly != null ? ProjectTypes.GetTypes(assembly).Where(t => t.Namespace == r.Value.Key) : Enumerable.Empty<Type>();
					})
					.Distinct();
			}

			IEnumerable<Assembly> GetDefiningAssemblies() =>
				new[] { typeof(Eto.Serialization.Xaml.XamlReader).Assembly, typeof(Eto.Widget).Assembly }.Concat(projectAssemblies).Distinct();

			/// <summary>xml namespace to clr namespace and assembly name, from the assembly's [XmlnsDefinition] attributes.</summary>
			static List<KeyValuePair<string, KeyValuePair<string, string>>> GetDefinitions(Assembly assembly) =>
				definitions.GetValue(assembly, a =>
				{
					var result = new List<KeyValuePair<string, KeyValuePair<string, string>>>();
					try
					{
						foreach (var attribute in a.GetCustomAttributesData().Where(r => r.AttributeType.Name == "XmlnsDefinitionAttribute"))
						{
							var xmlNamespace = attribute.ConstructorArguments[0].Value as string;
							var clrNamespace = attribute.ConstructorArguments[1].Value as string;
							var assemblyName = attribute.NamedArguments.FirstOrDefault(r => r.MemberName == "AssemblyName").TypedValue.Value as string
								?? ProjectTypes.GetAssemblyName(a);
							result.Add(new KeyValuePair<string, KeyValuePair<string, string>>(xmlNamespace, new KeyValuePair<string, string>(clrNamespace, assemblyName)));
						}
					}
					catch (Exception)
					{
						// assemblies whose attributes can't load just don't add any namespaces
					}
					return result;
				});
		}
	}
}
