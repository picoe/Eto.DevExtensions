using System;
using System.Collections.Generic;
using System.Linq;

namespace Eto.Designer.Completion
{
	/// <summary>A completion ready to insert, with its text already adjusted for the format.</summary>
	public class DocumentCompletionItem
	{
		public CompletionItem Item { get; set; }

		public string Label { get; set; }

		public string InsertText { get; set; }
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
		public static DocumentCompletionContext GetContext(string text, int offset, CompletionFormat format, string rootTypeName = null)
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

			return new DocumentCompletionContext { Info = info, Format = format, Start = start, End = end, Quoted = quoted };
		}

		public static List<DocumentCompletionItem> GetItems(DocumentCompletionContext context)
		{
			var info = context.Info;
			var format = context.Format;
			// json keys and values are always quoted, so add them when the cursor isn't already in a string
			var addQuotes = format == CompletionFormat.Json && !context.Quoted;
			var addColon = addQuotes && info.Mode == CompletionMode.Property;

			var results = new List<DocumentCompletionItem>();
			foreach (var item in Completion.GetCompletionItems(info.Namespaces, info.Mode, info.Path, info.Context, format).OrderBy(r => r.Name))
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
				results.Add(new DocumentCompletionItem { Item = item, Label = label, InsertText = insert });
			}
			return results;
		}

		public static List<DocumentCompletionItem> GetCompletions(string text, int offset, CompletionFormat format, string rootTypeName = null)
		{
			var context = GetContext(text, offset, format, rootTypeName);
			return context == null ? new List<DocumentCompletionItem>() : GetItems(context);
		}

		/// <summary>The completion item for the word under the cursor, used for hover text.</summary>
		public static CompletionItem FindItemAt(string text, int offset, CompletionFormat format, string rootTypeName, out int start, out int end)
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

			return Completion.GetCompletionItems(info.Namespaces, info.Mode, info.Path, info.Context, format)
				.FirstOrDefault(r => r.Name == word);
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
