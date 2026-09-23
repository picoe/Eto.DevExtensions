using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Eto.Designer.Completion
{
	/// <summary>A completion ready to insert, with its text already adjusted for the format.</summary>
	public class DocumentCompletionItem
	{
		public CompletionItem Item { get; set; }

		public string Label { get; set; }

		public string InsertText { get; set; }

		/// <summary>Declaration to add to the root element so the item resolves, or null when none is needed.</summary>
		public DocumentTextEdit NamespaceEdit { get; set; }
	}

	/// <summary>What the cursor sits on, and the span of text a completion should replace.</summary>
	public class DocumentCompletionContext
	{
		public ParseInfo Info { get; set; }

		public CompletionFormat Format { get; set; }

		public int Start { get; set; }

		public int End { get; set; }

		/// <summary>True when the cursor is already inside a quoted value.</summary>
		public bool Quoted { get; set; }

		public IList<Assembly> ProjectAssemblies { get; set; }

		public string Text { get; set; }
	}

	/// <summary>Text to insert at an offset of the document the completion was computed for.</summary>
	public class DocumentTextEdit
	{
		public int Offset { get; set; }

		public string Text { get; set; }
	}

	/// <summary>
	/// Editor-neutral completion for a whole .xeto/.jeto document at a cursor offset.
	/// </summary>
	/// <remarks>
	/// <see cref="GetContext"/> only parses text, so it is cheap enough for the UI thread;
	/// <see cref="GetItems"/> reflects over Eto.
	/// </remarks>
	public static class DocumentCompletion
	{
		public static CompletionFormat GetFormat(string path) =>
			path != null && path.EndsWith(".jeto", StringComparison.OrdinalIgnoreCase)
				? CompletionFormat.Json
				: CompletionFormat.Xaml;

		/// <returns>The context, or null when there is nothing to complete at the offset.</returns>
		public static DocumentCompletionContext GetContext(string text, int offset, CompletionFormat format, string rootTypeName = null, IList<Assembly> projectAssemblies = null)
		{
			offset = Math.Min(Math.Max(offset, 0), text.Length);

			ParseInfo info;
			bool quoted;
			int start;
			if (format == CompletionFormat.Json)
			{
				info = JsonParser.Read(text.Substring(0, offset), rootTypeName);
				quoted = info.InString;
				start = quoted ? ScanBack(text, offset, IsValueChar) : ScanBack(text, offset, IsJsonTokenChar);
			}
			else
			{
				// the xaml parser keys off what precedes the word being typed, so cut it first
				quoted = IsInsideAttributeValue(text, offset);
				start = quoted ? ScanBack(text, offset, IsValueChar) : GetTokenStart(text, offset);
				info = XmlParser.Read(text.Substring(0, start));
			}
			if (info.Mode == CompletionMode.None)
				return null;

			var end = quoted
				? ScanForward(text, offset, IsValueChar)
				: ScanForward(text, offset, format == CompletionFormat.Json ? (Func<char, bool>)IsJsonTokenChar : IsTokenChar);

			return new DocumentCompletionContext { Info = info, Format = format, Start = start, End = end, Quoted = quoted, Text = text, ProjectAssemblies = projectAssemblies };
		}

		public static List<DocumentCompletionItem> GetItems(DocumentCompletionContext context)
		{
			var info = context.Info;
			var format = context.Format;
			// json keys and values are always quoted, so add them when the cursor isn't already in a string
			var addQuotes = format == CompletionFormat.Json && !context.Quoted;
			var addColon = addQuotes && info.Mode == CompletionMode.Property;

			var results = new List<DocumentCompletionItem>();
			foreach (var item in Completion.GetCompletionItems(info.Namespaces, info.Mode, info.Path, info.Context, format, context.ProjectAssemblies).OrderBy(r => r.Name))
			{
				var label = item.Name;
				var insert = label;
				if (format == CompletionFormat.Json)
				{
					if (item.Type == CompletionType.Literal && (label == "True" || label == "False"))
					{
						// json booleans are lowercase and unquoted
						label = label.ToLowerInvariant();
						insert = label;
					}
					else
					{
						if (addQuotes)
							insert = "\"" + insert + "\"";
						if (addColon)
							insert += ": ";
					}
				}
				var namespaceEdit = item.Namespace != null ? GetNamespaceDeclaration(context.Text, item.Namespace) : null;
				results.Add(new DocumentCompletionItem { Item = item, Label = label, InsertText = insert, NamespaceEdit = namespaceEdit });
			}
			return results;
		}

		public static List<DocumentCompletionItem> GetCompletions(string text, int offset, CompletionFormat format, string rootTypeName = null, IList<Assembly> projectAssemblies = null)
		{
			var context = GetContext(text, offset, format, rootTypeName, projectAssemblies);
			return context == null ? new List<DocumentCompletionItem>() : GetItems(context);
		}

		/// <summary>The completion item for the word under the cursor, used for hover text.</summary>
		public static CompletionItem FindItemAt(string text, int offset, CompletionFormat format, string rootTypeName, out int start, out int end, IList<Assembly> projectAssemblies = null)
		{
			offset = Math.Min(Math.Max(offset, 0), text.Length);
			start = GetTokenStart(text, offset);
			end = ScanForward(text, offset, IsTokenChar);
			if (end <= start)
				return null;

			var word = text.Substring(start, end - start);
			var info = format == CompletionFormat.Json
				? JsonParser.Read(text.Substring(0, start), rootTypeName)
				: XmlParser.Read(text.Substring(0, start));
			if (info.Mode == CompletionMode.None)
				return null;

			return Completion.GetCompletionItems(info.Namespaces, info.Mode, info.Path, info.Context, format, projectAssemblies)
				.FirstOrDefault(r => r.Name == word);
		}

		static readonly Regex rootTagReg = new Regex(@"<(?![?!])[A-Za-z_][\w.:-]*", RegexOptions.Compiled);
		static readonly Regex xmlnsAttributeReg = new Regex(@"\s+xmlns(:[\w.-]+)?\s*=\s*(""[^""]*""|'[^']*')", RegexOptions.Compiled);

		/// <summary>
		/// Where to declare a namespace on the root element: after its last xmlns, or its name when it has none.
		/// </summary>
		/// <returns>The edit, or null when there is no complete root start tag to add it to.</returns>
		public static DocumentTextEdit GetNamespaceDeclaration(string text, CompletionNamespace ns)
		{
			var root = rootTagReg.Match(text);
			if (!root.Success)
				return null;

			var tagEnd = FindTagEnd(text, root.Index + root.Length);
			if (tagEnd < 0)
				return null;

			var offset = root.Index + root.Length;
			var tag = text.Substring(offset, tagEnd - offset);
			var last = xmlnsAttributeReg.Matches(tag).Cast<Match>().LastOrDefault();
			if (last != null)
				offset += last.Index + last.Length;

			return new DocumentTextEdit
			{
				Offset = offset,
				Text = " xmlns:" + ns.Prefix + "=\"" + ns.Namespace + "\""
			};
		}

		/// <summary>Index of the '>' closing a start tag, skipping quoted attribute values.</summary>
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
				else if (ch == '>')
					return i;
				else if (ch == '<')
					return -1;
			}
			return -1;
		}

		static int ScanBack(string text, int offset, Func<char, bool> include)
		{
			var i = Math.Min(offset, text.Length);
			while (i > 0 && include(text[i - 1]))
				i--;
			return i;
		}

		static int ScanForward(string text, int offset, Func<char, bool> include)
		{
			var i = Math.Max(Math.Min(offset, text.Length), 0);
			while (i < text.Length && include(text[i]))
				i++;
			return i;
		}

		static bool IsTokenChar(char ch) =>
			char.IsLetterOrDigit(ch) || ch == '_' || ch == ':' || ch == '.' || ch == '$' || ch == '-';

		// a bare json token can't contain ':', which separates a key from its value
		static bool IsJsonTokenChar(char ch) => ch != ':' && IsTokenChar(ch);

		static bool IsValueChar(char ch) =>
			ch != '"' && ch != '\'' && ch != '<' && ch != '>' && ch != '\n';

		/// <summary>
		/// Start of the word being typed. A dotted name such as StackLayout.Items keeps the
		/// part before the dot, which is what tells the parser it is a property element.
		/// </summary>
		static int GetTokenStart(string text, int offset)
		{
			var start = ScanBack(text, offset, IsTokenChar);
			if (offset == 0)
				return start;
			var dot = text.LastIndexOf('.', offset - 1);
			return dot >= start && dot < offset ? dot + 1 : start;
		}

		/// <summary>True when the cursor sits within a quoted attribute value of an unclosed tag.</summary>
		static bool IsInsideAttributeValue(string text, int offset)
		{
			var tagStart = text.LastIndexOf('<', Math.Max(offset - 1, 0));
			if (offset == 0 || tagStart < 0)
				return false;

			var quote = '\0';
			for (var i = tagStart; i < offset; i++)
			{
				var ch = text[i];
				if (quote != '\0')
				{
					if (ch == quote)
						quote = '\0';
				}
				else if (ch == '"' || ch == '\'')
					quote = ch;
				else if (ch == '>')
					return false;
			}
			return quote != '\0';
		}
	}
}
