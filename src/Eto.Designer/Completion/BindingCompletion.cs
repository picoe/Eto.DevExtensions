using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Eto.Designer.Completion
{
	/// <summary>
	/// Completes <c>{Binding ...}</c> values from the type given by the nearest <c>d:DataContext</c> or <c>DataContext</c>.
	/// </summary>
	/// <remarks>
	/// <see cref="Read"/> only parses text; <see cref="GetItems"/> reflects over the project.
	/// </remarks>
	public static class BindingCompletion
	{
		public const string DataContextProperty = "DataContext";
		public const string DesignDataContextProperty = "d:DataContext";

		const RegexOptions opts = RegexOptions.Compiled | RegexOptions.Singleline;
		static readonly Regex pathReg = new Regex(@"\{\s*Binding(?:\s+(?:Path\s*=\s*)?|[^{}]*,\s*Path\s*=\s*)(?<path>[\w.]*)$", opts);
		static readonly Regex valueReg = new Regex(@"\{\s*Binding\b[^{}]*?[\s,](?<name>(?!Path\b)\w+)\s*=\s*(?<value>\w*)$", opts);
		static readonly Regex extensionReg = new Regex(@"^\s*\{\s*(?<value>\w*)$", opts);
		static readonly Regex usedOptionReg = new Regex(@"[\s,](?<name>\w+)\s*=", opts);
		static readonly Regex optionReg = new Regex(@"\{\s*Binding\b[^{}]*,\s*(?<value>\w*)$", opts);
		static readonly Regex bindingPathReg = new Regex(@"^\s*\{\s*Binding(?:\s+(?:Path\s*=\s*)?(?<path>[\w.]+))?", opts);
		static readonly Regex markupReg = new Regex(@"^\s*\{\s*(?<ext>[\w.:-]+)(?<args>[^{}]*)\}?", opts);

		internal enum Kind
		{
			Path,
			Value,
			Option,
			Extension
		}

		/// <summary>One element or object from the root to the cursor, as far as its data context goes.</summary>
		internal class Scope
		{
			/// <summary>Type name from d:DataContext, as written.</summary>
			public string DesignType;

			/// <summary>Path from DataContext="{Binding Path}".</summary>
			public string BindingPath;

			/// <summary>Any other markup given to DataContext, eg. {x:Static local:Samples.Main}.</summary>
			public string DataContextMarkup;

			/// <summary>Object given in a &lt;Panel.DataContext&gt; property element.</summary>
			public XamlElement DataContextElement;

			public Dictionary<string, string> Namespaces;
		}

		public class Request
		{
			internal Kind Kind;
			internal CompletionFormat Format;
			internal List<Scope> Scopes;
			internal string[] ParentPath;
			internal string Option;
			internal HashSet<string> UsedOptions;

			public int Start { get; set; }

			public int End { get; set; }
		}

		/// <returns>The request, or null when the cursor isn't in a binding.</returns>
		public static Request Read(string text, int offset, CompletionFormat format)
		{
			var valueStart = offset;
			while (valueStart > 0 && text[valueStart - 1] != '"' && text[valueStart - 1] != '\'' && text[valueStart - 1] != '\n')
				valueStart--;
			var typed = text.Substring(valueStart, offset - valueStart);

			Match m;
			Kind kind;
			if ((m = valueReg.Match(typed)).Success)
				kind = Kind.Value;
			else if ((m = pathReg.Match(typed)).Success)
				kind = Kind.Path;
			else if ((m = optionReg.Match(typed)).Success)
				kind = Kind.Option;
			else if (format == CompletionFormat.Json && (m = extensionReg.Match(typed)).Success)
				kind = Kind.Extension;
			else
				return null;

			// xaml options come from the binding extension itself, see MarkupCompletion
			if (format == CompletionFormat.Xaml && kind != Kind.Path)
				return null;

			var value = m.Groups[kind == Kind.Path ? "path" : "value"];
			var start = valueStart + value.Index;
			var parentPath = new string[0];
			if (kind == Kind.Path)
			{
				// only the last segment is replaced, the rest picks the type to complete from
				var dot = value.Value.LastIndexOf('.');
				if (dot >= 0)
				{
					parentPath = value.Value.Substring(0, dot).Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
					start += dot + 1;
				}
			}

			var end = offset;
			while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
				end++;

			string editing;
			var scopes = format == CompletionFormat.Json ? ReadJsonScopes(text, offset, out editing) : ReadXamlScopes(text, offset, out editing);
			// a DataContext binding comes from the parent, not the object's own data context
			if (editing == DataContextProperty && scopes.Count > 0)
				scopes.RemoveAt(scopes.Count - 1);

			var usedOptions = new HashSet<string>(usedOptionReg.Matches(typed).Cast<Match>().Select(r => r.Groups["name"].Value), StringComparer.OrdinalIgnoreCase);
			var option = kind == Kind.Value ? m.Groups["name"].Value : null;
			return new Request { Kind = kind, Format = format, Scopes = scopes, ParentPath = parentPath, Start = start, End = end, Option = option, UsedOptions = usedOptions };
		}

		public static IEnumerable<CompletionItem> GetItems(Request request, IList<Assembly> projectAssemblies)
		{
			if (request.Format == CompletionFormat.Json)
			{
				// what jeto bindings support comes from the project's own Eto.Serialization.Json
				var binding = GetJsonBindingType();
				if (binding == null)
					return Enumerable.Empty<CompletionItem>();
				switch (request.Kind)
				{
					case Kind.Extension:
						return new[] { new CompletionItem { Name = "Binding", Type = CompletionType.Class, Description = "Binds the property to the data context, eg. {Binding Name, Mode=OneWay}." } };
					case Kind.Option:
						return GetOptions(binding)
							.Where(r => !request.UsedOptions.Contains(r.Name))
							.Select(r => new CompletionItem { Name = r.Name + "=", Type = CompletionType.Attribute, Suffix = GetTypeName(r.PropertyType) });
					case Kind.Value:
						return GetOptionValues(GetOptions(binding).FirstOrDefault(r => string.Equals(r.Name, request.Option, StringComparison.OrdinalIgnoreCase)));
				}
			}

			var type = GetDataContextType(request, projectAssemblies);
			foreach (var name in request.ParentPath)
				type = GetPropertyType(type, name);
			if (type == null)
				return Enumerable.Empty<CompletionItem>();

			return GetProperties(type).Select(prop => new CompletionItem
			{
				Name = prop.Name,
				Suffix = GetTypeName(prop.PropertyType),
				Description = XmlComments.GetSummary(prop),
				Type = CompletionType.Property
			});
		}

		static Type GetJsonBindingType()
		{
			try
			{
				return typeof(Eto.Serialization.Json.JsonReader).Assembly.GetType("Eto.Serialization.Json.Converters.BindingInfo", false);
			}
			catch (Exception)
			{
				return null;
			}
		}

		static IEnumerable<PropertyInfo> GetOptions(Type binding) =>
			binding.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(r => r.SetMethod?.IsPublic == true);

		static IEnumerable<CompletionItem> GetOptionValues(PropertyInfo option)
		{
			var type = option == null ? null : Nullable.GetUnderlyingType(option.PropertyType) ?? option.PropertyType;
			if (type == typeof(bool))
				return new[] { "True", "False" }.Select(r => new CompletionItem { Name = r, Type = CompletionType.Literal });
			if (type?.IsEnum == true)
				return Enum.GetNames(type).Select(r => new CompletionItem { Name = r, Type = CompletionType.Literal });
			return Enumerable.Empty<CompletionItem>();
		}

		static Type GetDataContextType(Request request, IList<Assembly> projectAssemblies)
		{
			Type type = null;
			foreach (var scope in request.Scopes)
			{
				if (request.Format == CompletionFormat.Json && scope.DesignType != null)
					type = FindJsonType(scope.DesignType, projectAssemblies);
				else if (scope.DesignType != null)
					type = ResolveMarkup(scope.DesignType, scope.Namespaces, projectAssemblies);
				else if (scope.DataContextElement != null)
					type = ResolveElement(scope.DataContextElement, scope.Namespaces, projectAssemblies);
				else if (scope.DataContextMarkup != null)
					type = ResolveMarkup(scope.DataContextMarkup, scope.Namespaces, projectAssemblies);
				else if (scope.BindingPath != null)
				{
					foreach (var name in scope.BindingPath.Split('.'))
						type = GetPropertyType(type, name);
				}
			}
			return type;
		}

		static IEnumerable<PropertyInfo> GetProperties(Type type)
		{
			try
			{
				return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
					.Where(r => r.GetMethod?.IsPublic == true && r.GetIndexParameters().Length == 0)
					.GroupBy(r => r.Name)
					.Select(r => r.First())
					.ToList();
			}
			catch (Exception)
			{
				// project types can fail to load when one of their dependencies is missing
				return Enumerable.Empty<PropertyInfo>();
			}
		}

		static Type GetPropertyType(Type type, string name)
		{
			if (type == null || string.IsNullOrEmpty(name))
				return type;
			return GetProperties(type).FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))?.PropertyType;
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

		/// <summary>Type made by markup such as {d:DesignInstance local:Type} or {x:Static local:Type.Member}.</summary>
		static Type ResolveMarkup(string value, Dictionary<string, string> namespaces, IList<Assembly> projectAssemblies)
		{
			var m = markupReg.Match(value ?? string.Empty);
			if (!m.Success)
				return null;
			var extension = m.Groups["ext"].Value;
			var arguments = new Dictionary<string, string>();
			string positional = null;
			foreach (var part in m.Groups["args"].Value.Split(','))
			{
				var equals = part.IndexOf('=');
				if (equals < 0)
					positional = positional ?? (part.Trim().Length > 0 ? part.Trim() : null);
				else
					arguments[part.Substring(0, equals).Trim()] = part.Substring(equals + 1).Trim();
			}
			return Resolve(extension, positional, arguments, namespaces, projectAssemblies);
		}

		/// <summary>Type of an object element, eg. &lt;local:MyViewModel/&gt; or &lt;x:Static Member="local:Type.Member"/&gt;.</summary>
		static Type ResolveElement(XamlElement element, Dictionary<string, string> namespaces, IList<Assembly> projectAssemblies)
		{
			if (IsExtension(element.Name, "DesignInstance") || IsExtension(element.Name, "Static"))
				return Resolve(element.Name, null, element.Attributes.ToDictionary(r => r.Key, r => r.Value), namespaces, projectAssemblies);
			return ResolveTypeName(element.Name, namespaces, projectAssemblies);
		}

		static Type Resolve(string extension, string positional, Dictionary<string, string> arguments, Dictionary<string, string> namespaces, IList<Assembly> projectAssemblies)
		{
			if (IsExtension(extension, "DesignInstance"))
				return ResolveTypeName(positional ?? Get(arguments, "Type"), namespaces, projectAssemblies);
			if (IsExtension(extension, "Static"))
				return ResolveStatic(positional ?? Get(arguments, "Member"), namespaces, projectAssemblies);
			if (IsExtension(extension, "Binding"))
				return null;

			// other markup extensions say what they make, when it's more than object
			var extensionType = ResolveTypeName(extension + "Extension", namespaces, projectAssemblies) ?? ResolveTypeName(extension, namespaces, projectAssemblies);
			return extensionType != null ? GetReturnType(extensionType) : null;
		}

		static string Get(Dictionary<string, string> arguments, string name) =>
			arguments.TryGetValue(name, out var value) ? value : null;

		static bool IsExtension(string name, string extension)
		{
			var local = name.Substring(name.IndexOf(':') + 1);
			return local == extension || local == extension + "Extension";
		}

		static Type GetReturnType(Type extensionType)
		{
			try
			{
				// by name, as the extension may use a different copy of the xaml library
				var attribute = extensionType.GetCustomAttributesData().FirstOrDefault(r => r.AttributeType.Name == "MarkupExtensionReturnTypeAttribute");
				var type = attribute?.ConstructorArguments.FirstOrDefault().Value as Type;
				return type == typeof(object) ? null : type;
			}
			catch (Exception)
			{
				return null;
			}
		}

		static Type ResolveStatic(string member, Dictionary<string, string> namespaces, IList<Assembly> projectAssemblies)
		{
			var dot = member?.LastIndexOf('.') ?? -1;
			if (dot <= 0)
				return null;
			var type = ResolveTypeName(member.Substring(0, dot), namespaces, projectAssemblies);
			var name = member.Substring(dot + 1);
			try
			{
				const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
				return type?.GetProperty(name, flags)?.PropertyType ?? type?.GetField(name, flags)?.FieldType;
			}
			catch (Exception)
			{
				return null;
			}
		}

		/// <summary>Type from a xaml name such as local:MyViewModel.</summary>
		static Type ResolveTypeName(string name, Dictionary<string, string> namespaces, IList<Assembly> projectAssemblies)
		{
			if (string.IsNullOrEmpty(name))
				return null;
			var colon = name.IndexOf(':');
			var prefix = colon >= 0 ? name.Substring(0, colon) : string.Empty;
			name = name.Substring(colon + 1);

			if (!namespaces.TryGetValue(prefix, out var ns) && prefix.Length > 0)
				return null;
			if ((ns == null || ns == Completion.EtoFormsNamespace) && prefix.Length == 0)
				return GetType(typeof(Eto.Widget).Assembly, "Eto.Forms." + name) ?? GetType(typeof(Eto.Widget).Assembly, "Eto." + name);
			if (!ProjectTypes.TryParseClrNamespace(ns, out var clrNamespace, out var assemblyName))
				return null;
			return GetType(ProjectTypes.FindAssembly(assemblyName, projectAssemblies), clrNamespace + "." + name);
		}

		static Type FindJsonType(string value, IList<Assembly> projectAssemblies)
		{
			var comma = value.IndexOf(',');
			var name = (comma >= 0 ? value.Substring(0, comma) : value).Trim();
			var assemblyName = comma >= 0 ? value.Substring(comma + 1).Trim() : null;
			if (!name.Contains("."))
				return GetType(typeof(Eto.Widget).Assembly, "Eto.Forms." + name);
			if (!string.IsNullOrEmpty(assemblyName))
				return GetType(ProjectTypes.FindAssembly(assemblyName, projectAssemblies), name);
			return (projectAssemblies ?? Array.Empty<Assembly>()).Select(r => GetType(r, name)).FirstOrDefault(r => r != null);
		}

		static Type GetType(Assembly assembly, string fullName)
		{
			try
			{
				return assembly?.GetType(fullName, false);
			}
			catch (Exception)
			{
				return null;
			}
		}

		static string GetBindingPath(string value)
		{
			var m = bindingPathReg.Match(value ?? string.Empty);
			return m.Success ? m.Groups["path"].Value : null;
		}

		#region Xaml

		static readonly Regex tagReg = new Regex(@"<!--.*?(-->|$)|<[?!][^>]*>?|<(?<close>/)?(?<name>[\w:.-]+)(?<attrs>(?:[^<>""'/]|""[^""]*""|'[^']*')*)(?<self>/)?>", opts);
		static readonly Regex attributeReg = new Regex(@"(?<name>[\w:.-]+)\s*=\s*(?:""(?<value>[^""]*)""?|'(?<value>[^']*)'?)", opts);
		static readonly Regex editingAttributeReg = new Regex(@"(?<name>[\w:.-]+)\s*=\s*[""'][^""']*$", opts);

		internal class XamlElement
		{
			public string Name;
			public List<KeyValuePair<string, string>> Attributes;
			public XamlElement Parent;
			public readonly List<XamlElement> Children = new List<XamlElement>();
		}

		static List<Scope> ReadXamlScopes(string text, int offset, out string editing)
		{
			var tagStart = text.LastIndexOf('<', Math.Max(offset - 1, 0));

			// the element being edited may declare its data context after the cursor
			var tagEnd = FindTagEnd(text, tagStart + 1);
			var tag = text.Substring(tagStart + 1, tagEnd - tagStart - 1);
			var nameLength = 0;
			while (nameLength < tag.Length && !char.IsWhiteSpace(tag[nameLength]) && tag[nameLength] != '/')
				nameLength++;
			var closed = tagEnd < text.Length && text[tagEnd] == '>';
			var selfClosing = !closed || tag.TrimEnd().EndsWith("/", StringComparison.Ordinal);

			// the whole document is read, as a <Panel.DataContext> can come after the cursor too
			var document = new XamlElement { Name = string.Empty, Attributes = new List<KeyValuePair<string, string>>() };
			var parent = AddElements(text.Substring(0, tagStart), document);
			var current = new XamlElement { Name = tag.Substring(0, nameLength), Attributes = ReadAttributes(tag.Substring(nameLength)), Parent = parent };
			parent.Children.Add(current);
			AddElements(text.Substring(closed ? tagEnd + 1 : tagEnd), selfClosing ? parent : current);

			var editingMatch = editingAttributeReg.Match(text.Substring(tagStart, offset - tagStart));
			editing = editingMatch.Success ? editingMatch.Groups["name"].Value : null;

			var chain = new List<XamlElement>();
			for (var element = current; element != null && element != document; element = element.Parent)
				chain.Insert(0, element);

			var namespaces = new Dictionary<string, string>();
			var scopes = new List<Scope>();
			foreach (var element in chain)
			{
				AddNamespaces(element, namespaces);
				// property elements such as <Panel.Content> don't have a data context of their own
				if (element.Name.Contains("."))
					continue;

				var scope = new Scope { Namespaces = new Dictionary<string, string>(namespaces) };
				foreach (var attribute in element.Attributes)
				{
					var colon = attribute.Key.IndexOf(':');
					if (colon > 0 && attribute.Key.Substring(colon + 1) == DataContextProperty
						&& namespaces.TryGetValue(attribute.Key.Substring(0, colon), out var ns) && ns == Completion.DesignNamespace)
						scope.DesignType = attribute.Value;
					else if (attribute.Key == DataContextProperty)
					{
						scope.BindingPath = GetBindingPath(attribute.Value);
						if (scope.BindingPath == null)
							scope.DataContextMarkup = attribute.Value;
					}
				}

				var property = element.Children.FirstOrDefault(r => r.Name.EndsWith("." + DataContextProperty, StringComparison.Ordinal));
				var value = property?.Children.FirstOrDefault();
				if (value != null)
				{
					AddNamespaces(property, scope.Namespaces);
					AddNamespaces(value, scope.Namespaces);
					scope.DataContextElement = value;
				}
				scopes.Add(scope);
			}
			return scopes;
		}

		static void AddNamespaces(XamlElement element, Dictionary<string, string> namespaces)
		{
			foreach (var attribute in element.Attributes)
			{
				if (attribute.Key == "xmlns")
					namespaces[string.Empty] = attribute.Value;
				else if (attribute.Key.StartsWith("xmlns:", StringComparison.Ordinal))
					namespaces[attribute.Key.Substring(6)] = attribute.Value;
			}
		}

		/// <summary>Adds the elements in <paramref name="text"/> under <paramref name="parent"/>, returning the one still open at the end.</summary>
		static XamlElement AddElements(string text, XamlElement parent)
		{
			foreach (Match m in tagReg.Matches(text))
			{
				if (!m.Groups["name"].Success)
					continue;
				var name = m.Groups["name"].Value;
				if (m.Groups["close"].Success)
				{
					var open = parent;
					while (open != null && open.Name != name)
						open = open.Parent;
					// a stray close tag is ignored rather than closing everything
					if (open?.Parent != null)
						parent = open.Parent;
					continue;
				}
				var element = new XamlElement { Name = name, Attributes = ReadAttributes(m.Groups["attrs"].Value), Parent = parent };
				parent.Children.Add(element);
				if (!m.Groups["self"].Success)
					parent = element;
			}
			return parent;
		}

		static List<KeyValuePair<string, string>> ReadAttributes(string text) =>
			attributeReg.Matches(text).Cast<Match>()
				.Select(r => new KeyValuePair<string, string>(r.Groups["name"].Value, r.Groups["value"].Value))
				.ToList();

		/// <summary>Index of the '>' closing a start tag, or where the tag stops when it isn't closed yet.</summary>
		static int FindTagEnd(string text, int start)
		{
			var quote = '\0';
			for (var i = start; i < text.Length; i++)
			{
				var ch = text[i];
				if (quote != '\0')
				{
					if (ch == quote)
						quote = '\0';
				}
				else if (ch == '"' || ch == '\'')
					quote = ch;
				else if (ch == '>' || ch == '<')
					return i;
			}
			return text.Length;
		}

		#endregion

		#region Json

		static List<Scope> ReadJsonScopes(string text, int offset, out string editing)
		{
			// objects enclosing the cursor, each read in full since d:DataContext may come after it
			var starts = new List<int>();
			var containers = new List<char>();
			string lastKey = null;
			var expectValue = false;
			editing = null;
			for (var i = 0; i < offset; i++)
			{
				var ch = text[i];
				if (ch == '/' && i + 1 < text.Length && (text[i + 1] == '/' || text[i + 1] == '*'))
				{
					i = SkipComment(text, i) - 1;
					continue;
				}
				if (ch == '"' || ch == '\'')
				{
					var end = SkipString(text, i, out var closed);
					if (end > offset || !closed)
					{
						// the string holding the cursor
						editing = expectValue ? lastKey : null;
						break;
					}
					if (!expectValue && containers.LastOrDefault() == '{')
						lastKey = text.Substring(i + 1, end - i - 2);
					i = end - 1;
					continue;
				}
				switch (ch)
				{
					case '{':
					case '[':
						starts.Add(i);
						containers.Add(ch);
						expectValue = false;
						break;
					case '}':
					case ']':
						if (starts.Count > 0)
						{
							starts.RemoveAt(starts.Count - 1);
							containers.RemoveAt(containers.Count - 1);
						}
						expectValue = false;
						break;
					case ':':
						expectValue = true;
						break;
					case ',':
						expectValue = false;
						break;
				}
			}

			var scopes = new List<Scope>();
			for (var i = 0; i < starts.Count; i++)
			{
				if (containers[i] != '{')
					continue;
				var members = ReadJsonMembers(text, starts[i]);
				var scope = new Scope();
				if (members.TryGetValue(DesignDataContextProperty, out var design))
					scope.DesignType = design;
				if (members.TryGetValue(DataContextProperty, out var dataContext))
				{
					scope.BindingPath = GetBindingPath(dataContext);
					// otherwise it's an object given by its $type
					if (scope.BindingPath == null && scope.DesignType == null)
						scope.DesignType = dataContext;
				}
				scopes.Add(scope);
			}
			return scopes;
		}

		/// <summary>
		/// Top level members of the object starting at <paramref name="start"/>, with string values
		/// as-is and object values as their $type.
		/// </summary>
		static Dictionary<string, string> ReadJsonMembers(string text, int start)
		{
			var members = new Dictionary<string, string>();
			var depth = 0;
			string key = null;
			var expectValue = false;
			for (var i = start; i < text.Length; i++)
			{
				var ch = text[i];
				if (ch == '/' && i + 1 < text.Length && (text[i + 1] == '/' || text[i + 1] == '*'))
				{
					i = SkipComment(text, i) - 1;
					continue;
				}
				if (ch == '"' || ch == '\'')
				{
					var end = SkipString(text, i, out var closed);
					var value = text.Substring(i + 1, end - i - (closed ? 2 : 1));
					if (depth == 1)
					{
						if (!expectValue)
							key = value;
						else if (key != null)
							members[key] = value;
					}
					else if (depth == 2 && expectValue && key != null && IsTypeKey(text, i))
						members[key] = value;
					i = end - 1;
					continue;
				}
				switch (ch)
				{
					case '{':
					case '[':
						depth++;
						break;
					case '}':
					case ']':
						depth--;
						if (depth == 0)
							return members;
						if (depth == 1)
							expectValue = false;
						break;
					case ':':
						if (depth == 1)
							expectValue = true;
						break;
					case ',':
						if (depth == 1)
						{
							expectValue = false;
							key = null;
						}
						break;
				}
			}
			return members;
		}

		/// <summary>True when the string at <paramref name="index"/> is the value of a "$type" key.</summary>
		static bool IsTypeKey(string text, int index)
		{
			var i = index - 1;
			while (i >= 0 && char.IsWhiteSpace(text[i]))
				i--;
			if (i < 0 || text[i] != ':')
				return false;
			i--;
			while (i >= 0 && char.IsWhiteSpace(text[i]))
				i--;
			var keyEnd = i;
			if (keyEnd < 0 || (text[keyEnd] != '"' && text[keyEnd] != '\''))
				return false;
			var keyStart = text.LastIndexOf(text[keyEnd], keyEnd - 1);
			return keyStart >= 0 && text.Substring(keyStart + 1, keyEnd - keyStart - 1) == JsonParser.TypeProperty;
		}

		/// <summary>Index just past the closing quote, or the text length when unterminated.</summary>
		static int SkipString(string text, int start, out bool closed)
		{
			var quote = text[start];
			for (var i = start + 1; i < text.Length; i++)
			{
				if (text[i] == '\\')
					i++;
				else if (text[i] == quote)
				{
					closed = true;
					return i + 1;
				}
			}
			closed = false;
			return text.Length;
		}

		static int SkipComment(string text, int start)
		{
			if (text[start + 1] == '/')
			{
				var newline = text.IndexOf('\n', start);
				return newline < 0 ? text.Length : newline + 1;
			}
			var close = text.IndexOf("*/", start + 2, StringComparison.Ordinal);
			return close < 0 ? text.Length : close + 2;
		}

		#endregion
	}
}
