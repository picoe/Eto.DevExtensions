using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Eto.Designer.Completion;
using Eto.DevExtension.LanguageServer.Lsp;
using Engine = Eto.Designer.Completion.Completion;

namespace Eto.DevExtension.LanguageServer
{
	/// <summary>
	/// Turns a cursor position in a .xeto/.jeto document into LSP completions and hovers.
	/// </summary>
	/// <remarks>
	/// Everything here reflects over Eto, so it must not be touched until
	/// <see cref="EtoAssemblyResolver"/> is installed.
	/// </remarks>
	public static class CompletionService
	{
		static readonly Regex tagReg = new Regex(@"<[^>]+>", RegexOptions.Compiled);

		static CompletionService()
		{
			XmlComments.EncodeHtml = false;
		}

		public static List<Lsp.CompletionItem> GetCompletions(string text, int offset, CompletionFormat format, string rootTypeName = null)
		{
			var results = new List<Lsp.CompletionItem>();
			offset = Math.Min(Math.Max(offset, 0), text.Length);

			ParseInfo info;
			bool quoted;
			int start;
			if (format == CompletionFormat.Json)
			{
				info = JsonParser.Read(text.Substring(0, offset), rootTypeName);
				quoted = info.InString;
				start = quoted ? ScanBack(text, offset, IsValueChar) : offset;
			}
			else
			{
				// the xaml parser keys off what precedes the word being typed, so cut it first
				quoted = IsInsideAttributeValue(text, offset);
				start = quoted ? ScanBack(text, offset, IsValueChar) : GetTokenStart(text, offset);
				info = XmlParser.Read(text.Substring(0, start));
			}
			if (info.Mode == CompletionMode.None)
				return results;

			var end = quoted ? ScanForward(text, offset, IsValueChar) : ScanForward(text, offset, IsTokenChar);

			var range = ToRange(text, start, end);
			// json keys and values are always quoted, so add them when the cursor isn't already in a string
			var addQuotes = format == CompletionFormat.Json && !quoted;
			var addColon = addQuotes && info.Mode == CompletionMode.Property;

			foreach (var item in Engine.GetCompletionItems(info.Namespaces, info.Mode, info.Path, info.Context, format).OrderBy(r => r.Name))
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

				results.Add(new Lsp.CompletionItem
				{
					Label = label,
					Kind = GetKind(item.Type),
					Detail = string.IsNullOrEmpty(item.Suffix) ? null : item.Suffix,
					Documentation = ToMarkup(item.Description),
					SortText = label,
					FilterText = label,
					TextEdit = new TextEdit { Range = range, NewText = insert }
				});
			}
			return results;
		}

		public static Hover GetHover(string text, int offset, CompletionFormat format, string rootTypeName = null)
		{
			offset = Math.Min(Math.Max(offset, 0), text.Length);
			var start = GetTokenStart(text, offset);
			var end = ScanForward(text, offset, IsTokenChar);
			if (end <= start)
				return null;

			var word = text.Substring(start, end - start);
			var info = format == CompletionFormat.Json
				? JsonParser.Read(text.Substring(0, start), rootTypeName)
				: XmlParser.Read(text.Substring(0, start));
			if (info.Mode == CompletionMode.None)
				return null;

			var match = Engine.GetCompletionItems(info.Namespaces, info.Mode, info.Path, info.Context, format)
				.FirstOrDefault(r => r.Name == word);
			if (match == null || string.IsNullOrWhiteSpace(match.Description))
				return null;

			return new Hover
			{
				Contents = ToMarkup(match.Description),
				Range = ToRange(text, start, end)
			};
		}

		static int GetKind(CompletionType type)
		{
			switch (type)
			{
				case CompletionType.Class: return CompletionItemKind.Class;
				case CompletionType.Property: return CompletionItemKind.Property;
				case CompletionType.Event: return CompletionItemKind.Event;
				case CompletionType.Field: return CompletionItemKind.Field;
				case CompletionType.Attribute: return CompletionItemKind.Property;
				default: return CompletionItemKind.Value;
			}
		}

		static Lsp.Range ToRange(string text, int start, int end)
		{
			var from = DocumentStore.GetPosition(text, start);
			var to = DocumentStore.GetPosition(text, end);
			return new Lsp.Range
			{
				Start = new Position { Line = from.Line, Character = from.Character },
				End = new Position { Line = to.Line, Character = to.Character }
			};
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

		static MarkupContent ToMarkup(string description)
		{
			if (string.IsNullOrWhiteSpace(description))
				return null;

			var signature = (string)null;
			var body = description;
			var marker = description.IndexOf("Summary", StringComparison.Ordinal);
			if (marker >= 0)
			{
				signature = description.Substring(0, marker).Trim();
				body = description.Substring(marker + "Summary".Length).Trim();
			}

			body = tagReg.Replace(body, string.Empty).Trim();
			var value = string.IsNullOrEmpty(signature) ? body : "`" + signature + "`\n\n" + body;
			return string.IsNullOrWhiteSpace(value) ? null : new MarkupContent { Value = value };
		}
	}
}
