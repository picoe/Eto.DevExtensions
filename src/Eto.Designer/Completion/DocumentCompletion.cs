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

		/// <summary>
		/// Empty value to add after <see cref="InsertText"/>, eg. ="", for editors that can place the caret,
		/// which goes inside its closing quote when it has one; null when there is none.
		/// </summary>
		public string ValueText { get; set; }

		/// <summary>Characters after the completed word that <see cref="ValueText"/> replaces, eg. a json key's closing quote.</summary>
		public int ValueReplaces { get; set; }

		/// <summary>Where the caret goes within <see cref="ValueText"/>.</summary>
		public int ValueCaret => ValueText == null ? 0 : ValueText.EndsWith("\"") ? ValueText.Length - 1 : ValueText.Length;

		/// <summary>Declaration to add to the root element so the item resolves, or null when none is needed.</summary>
		public DocumentTextEdit NamespaceEdit { get; set; }

		/// <summary>Text to add to the code behind when the item is chosen, eg. a new event handler.</summary>
		public DocumentFileEdit CodeBehindEdit { get; set; }
	}

	/// <summary>Text to insert into another file, at a zero-based line and character.</summary>
	public class DocumentFileEdit
	{
		public string FilePath { get; set; }

		public int Line { get; set; }

		public int Character { get; set; }

		public string Text { get; set; }
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

		/// <summary>Set when the cursor is inside a {Binding ...} value.</summary>
		public BindingCompletion.Request Binding { get; set; }

		/// <summary>Set when the cursor is inside other xaml markup, eg. {x:Static ...}.</summary>
		public MarkupCompletion.Request Markup { get; set; }

		/// <summary>Set to the element to close when the cursor follows "&lt;/".</summary>
		public string ClosingTag { get; set; }
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

			var jsonInfo = format == CompletionFormat.Json ? JsonParser.Read(text.Substring(0, offset), rootTypeName) : null;
			var quoted = jsonInfo?.InString ?? IsInsideAttributeValue(text, offset);

			var binding = quoted ? BindingCompletion.Read(text, offset, format) : null;
			if (binding != null)
			{
				var bindingInfo = new ParseInfo { Nodes = new List<CompletionPathNode>(), Mode = CompletionMode.Value };
				return new DocumentCompletionContext { Info = bindingInfo, Format = format, Start = binding.Start, End = binding.End, Quoted = true, Text = text, ProjectAssemblies = projectAssemblies, Binding = binding };
			}

			var markup = quoted && jsonInfo == null ? MarkupCompletion.Read(text, offset) : null;
			if (markup != null)
			{
				var markupInfo = new ParseInfo { Nodes = new List<CompletionPathNode>(), Mode = CompletionMode.Value };
				return new DocumentCompletionContext { Info = markupInfo, Format = format, Start = markup.Start, End = markup.End, Quoted = true, Text = text, ProjectAssemblies = projectAssemblies, Markup = markup };
			}

			if (jsonInfo == null && !quoted)
			{
				var nameStart = ScanBack(text, offset, IsTokenChar);
				if (nameStart >= 2 && text[nameStart - 1] == '/' && text[nameStart - 2] == '<')
				{
					var element = XmlParser.GetUnclosedElement(text, nameStart - 2);
					if (element == null)
						return null;
					var closingInfo = new ParseInfo { Nodes = new List<CompletionPathNode>(), Mode = CompletionMode.Value };
					return new DocumentCompletionContext { Info = closingInfo, Format = format, Start = nameStart, End = ScanForward(text, offset, IsTokenChar), Text = text, ProjectAssemblies = projectAssemblies, ClosingTag = element };
				}
			}

			ParseInfo info;
			int start;
			if (jsonInfo != null)
			{
				info = jsonInfo;
				start = quoted ? ScanBack(text, offset, IsValueChar) : ScanBack(text, offset, IsJsonTokenChar);
			}
			else
			{
				// the xaml parser keys off what precedes the word being typed, so cut it first
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
			if (context.ClosingTag != null)
			{
				var closing = new CompletionItem { Name = context.ClosingTag, Type = CompletionType.Class, Description = $"Closes the <{context.ClosingTag}> element." };
				var bracket = context.End < context.Text.Length && context.Text[context.End] == '>' ? string.Empty : ">";
				return new List<DocumentCompletionItem> { new DocumentCompletionItem { Item = closing, Label = context.ClosingTag, InsertText = context.ClosingTag + bracket } };
			}

			if (context.Binding != null || context.Markup != null)
			{
				var items = context.Binding != null
					? BindingCompletion.GetItems(context.Binding, context.ProjectAssemblies)
					: MarkupCompletion.GetItems(context.Markup, context.ProjectAssemblies);
				return items
					.OrderBy(r => r.Name)
					.Select(r => new DocumentCompletionItem { Item = r, Label = r.Name, InsertText = r.Name })
					.ToList();
			}

			var info = context.Info;
			var format = context.Format;
			// json keys and values are always quoted, so add them when the cursor isn't already in a string
			var addQuotes = format == CompletionFormat.Json && !context.Quoted;
			var next = NextNonSpace(context.Text, context.End);
			// don't add a second separator when renaming a property that already has a value
			var addColon = addQuotes && info.Mode == CompletionMode.Property && next != ':';
			// "Te|" finishes the key, unless it already has a value
			var closeKey = format == CompletionFormat.Json && context.Quoted && info.Mode == CompletionMode.Property
				&& context.End < context.Text.Length && context.Text[context.End] == '"' && NextNonSpace(context.Text, context.End + 1) != ':';
			var addEquals = format == CompletionFormat.Xaml && !context.Quoted && info.Mode == CompletionMode.Property && !info.IsChildProperty && next != '=';

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
				string value = null;
				if (closeKey)
					value = item.Behavior.HasFlag(CompletionBehavior.StringValue) ? "\": \"\"" : "\": ";
				else if (addColon && item.Behavior.HasFlag(CompletionBehavior.StringValue))
					value = "\"\"";
				else if (addEquals && IsAttribute(item))
					value = "=\"\"";
				var namespaceEdit = item.Namespace != null ? GetNamespaceDeclaration(context.Text, item.Namespace) : null;
				results.Add(new DocumentCompletionItem { Item = item, Label = label, InsertText = insert, ValueText = value, ValueReplaces = closeKey ? 1 : 0, NamespaceEdit = namespaceEdit });
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

		static bool IsAttribute(CompletionItem item) =>
			(item.Type == CompletionType.Property || item.Type == CompletionType.Event || item.Type == CompletionType.Attribute)
			&& (item.Behavior & (CompletionBehavior.ChildProperty | CompletionBehavior.PropertyElementOnly)) == 0;

		static char NextNonSpace(string text, int offset)
		{
			var i = ScanForward(text, offset, char.IsWhiteSpace);
			return i < text.Length ? text[i] : '\0';
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
