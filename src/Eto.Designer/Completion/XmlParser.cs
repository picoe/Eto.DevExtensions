using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;

namespace Eto.Designer.Completion
{
	public static class XmlParser
	{
		const RegexOptions opts = RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase;
		static readonly Regex valueReg = new Regex(@"(?<=\w+\s*=\s*)(('[^']*)|(""[^""]*))?$", opts);
		static readonly Regex propertyReg = new Regex(@"([<](\w+:)?\w+\s+)([^<>]+\s+)?[^<>/]*$", opts);
		//static readonly Regex propertyReg = new Regex(@"([<]\w+\s+)([^<]*)?(?<!(/|([/][>])|[>])[^<]*)$", opts);
		static readonly Regex classReg = new Regex(@"([<]\w*)$", opts);
		static readonly Regex classPropertyReg = new Regex(@"([<](\w+:)?\w*[.])$", opts);
		static readonly Regex usedPrefixReg = new Regex(@"(?:[<]|\s)(?<p>[A-Za-z_][\w.-]*):", opts);
		static readonly Regex declaredPrefixReg = new Regex(@"xmlns:(?<p>[\w.-]+)\s*=", opts);
		static readonly Regex defaultNamespaceReg = new Regex(@"(?<![\w:])xmlns\s*=", opts);
		static readonly Regex rootElementReg = new Regex(@"[<](?<name>[A-Za-z_][\w.-]*(:[\w.-]+)?)", opts);

		/// <summary>
		/// Declares the namespaces Eto's xaml reader adds implicitly, plus any prefix the document
		/// uses without declaring, so the reader doesn't abort on the first undeclared prefix and
		/// lose everything after it.
		/// </summary>
		static string DeclareNamespaces(string text)
		{
			var root = rootElementReg.Match(text);
			if (!root.Success)
				return text;

			var declared = new HashSet<string>(declaredPrefixReg.Matches(text).Cast<Match>().Select(r => r.Groups["p"].Value));
			var sb = new StringBuilder();
			if (!defaultNamespaceReg.IsMatch(text))
				sb.Append(" xmlns=\"").Append(Completion.EtoFormsNamespace).Append('"');
			if (declared.Add("x"))
				sb.Append(" xmlns:x=\"").Append(Completion.XamlNamespace2006).Append('"');

			foreach (Match used in usedPrefixReg.Matches(text))
			{
				var prefix = used.Groups["p"].Value;
				if (prefix == "xmlns" || prefix == "xml" || !declared.Add(prefix))
					continue;
				// enough for the reader to accept the prefix; nothing completes against it
				sb.Append(" xmlns:").Append(prefix).Append("=\"clr-namespace:\"");
			}

			if (sb.Length == 0)
				return text;

			var insertAt = root.Index + root.Length;
			return text.Substring(0, insertAt) + sb + text.Substring(insertAt);
		}

		/// <summary>Name of the innermost element still open at <paramref name="offset"/>, or null when all are closed.</summary>
		public static string GetUnclosedElement(string text, int offset)
		{
			var open = new List<string>();
			var i = 0;
			while ((i = text.IndexOf('<', i)) >= 0 && i < offset)
			{
				if (Skip(text, ref i, "<!--", "-->") || Skip(text, ref i, "<![CDATA[", "]]>") || Skip(text, ref i, "<?", "?>") || Skip(text, ref i, "<!", ">"))
					continue;

				var closing = i + 1 < text.Length && text[i + 1] == '/';
				var nameStart = closing ? i + 2 : i + 1;
				var nameEnd = nameStart;
				while (nameEnd < text.Length && (char.IsLetterOrDigit(text[nameEnd]) || text[nameEnd] == '_' || text[nameEnd] == ':' || text[nameEnd] == '.' || text[nameEnd] == '-'))
					nameEnd++;
				var name = text.Substring(nameStart, nameEnd - nameStart);

				// find the tag's end, stepping over quoted values that may contain '>'
				var quote = '\0';
				var end = nameEnd;
				for (; end < offset; end++)
				{
					var ch = text[end];
					if (quote != '\0')
					{
						if (ch == quote)
							quote = '\0';
					}
					else if (ch == '"' || ch == '\'')
						quote = ch;
					else if (ch == '>' || ch == '<')
						break;
				}
				if (end >= offset || text[end] == '<')
				{
					// an unfinished start tag still needs closing
					if (!closing && name.Length > 0)
						open.Add(name);
					i = end;
					continue;
				}

				if (closing)
				{
					// closing an outer element also closes anything left open inside it
					var index = open.LastIndexOf(name);
					if (index >= 0)
						open.RemoveRange(index, open.Count - index);
				}
				else if (name.Length > 0 && text[end - 1] != '/')
					open.Add(name);
				i = end + 1;
			}
			return open.Count > 0 ? open[open.Count - 1] : null;
		}

		static bool Skip(string text, ref int index, string start, string end)
		{
			if (string.CompareOrdinal(text, index, start, 0, start.Length) != 0)
				return false;
			var found = text.IndexOf(end, index + start.Length, StringComparison.Ordinal);
			index = found < 0 ? text.Length : found + end.Length;
			return true;
		}

		public static ParseInfo Read(string text)
		{
			var nodes = new List<CompletionPathNode>();
			var info = new ParseInfo { Nodes = nodes, Mode = CompletionMode.None };

			// check the last part of the xml to see what type of completion we are in
			// and complete it so we can parse the property or class name.
			Match m;
			if ((m = valueReg.Match(text)).Success)
			{
				info.Mode = CompletionMode.Value;
				// without an actual value, xmlns doesn't parse, so put a dummy value here
				text = text.Substring(0, m.Index) + "'value'>";
			}
			else if (classReg.Match(text).Success)
			{
				info.Mode = CompletionMode.Class;
				text += ">";
			}
			else if (propertyReg.Match(text).Success)
			{
				int i = text.Length - 1;
				while (i > 0 && !char.IsWhiteSpace(text[i]) && text[i] != '<')
					i--;
				if (i < text.Length - 1)
					text = text.Substring(0, i + 1);

				info.Mode = CompletionMode.Property;
				text += ">";
			}
			else if (classPropertyReg.Match(text).Success)
			{
				info.Mode = CompletionMode.Property;
				info.IsChildProperty = true;
				text += ">";
			}


			CompletionPathNode current = null;
			CompletionPathNode attribute = null;
			try
			{
				using (var reader = XmlReader.Create(new StringReader(DeclareNamespaces(text))))
				{
					while (reader.Read())
					{
						attribute = null;
						switch (reader.NodeType)
						{
							case XmlNodeType.Element:
								if (reader.IsEmptyElement)
									continue;
								nodes.Add(current = new CompletionPathNode(reader.Prefix, reader.LocalName, CompletionMode.Class));
								if (reader.HasAttributes && reader.MoveToFirstAttribute())
								{
									do
									{
										if (reader.Name == "xmlns" || reader.Prefix == "xmlns")
										{
											// record namespaces
											if (current.Namespaces == null)
												current.Namespaces = new List<CompletionNamespace>();
											var prefix = reader.Prefix == "xmlns" ? reader.LocalName : string.Empty;
											current.Namespaces.Add(new CompletionNamespace { Prefix = prefix, Namespace = reader.Value });
											var name = reader.LocalName;
											if (!string.IsNullOrEmpty(reader.Prefix))
												name = reader.Prefix + ":" + name;
											attribute = new CompletionPathNode(string.Empty, name, CompletionMode.Property);
										}
										else
										{
											current.Attributes.Add(reader.Name);
											attribute = new CompletionPathNode(reader.Prefix, reader.LocalName, CompletionMode.Property);
										}
									} while (reader.MoveToNextAttribute());
								}
								break;
							case XmlNodeType.EndElement:
								if (nodes.Count > 0)
									nodes.RemoveAt(nodes.Count - 1);
								current = nodes.Count > 0 ? nodes[nodes.Count - 1] : null;
								break;
							default:
								break;
						}
					}
				}
			}
			catch (XmlException)
			{
				// ignore errors, since xml will usually be incomplete
			}

			if (current?.LocalName.Contains(".") == true && info.Mode == CompletionMode.None && current.Mode == CompletionMode.Class)
			{
				current.LocalName = current.LocalName.Substring(current.LocalName.IndexOf('.') + 1);
				current.Mode = CompletionMode.Property;
				info.Mode = CompletionMode.Value;
			}

			if (info.Mode == CompletionMode.Value && attribute != null)
				nodes.Add(attribute);

			return info;
		}
	}
}
