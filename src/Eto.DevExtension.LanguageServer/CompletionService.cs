using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using Eto.Designer.Completion;
using Eto.DevExtension.LanguageServer.Lsp;

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

		public static List<Lsp.CompletionItem> GetCompletions(string text, int offset, CompletionFormat format, string rootTypeName = null, IList<Assembly> projectAssemblies = null)
		{
			var results = new List<Lsp.CompletionItem>();
			var context = DocumentCompletion.GetContext(text, offset, format, rootTypeName, projectAssemblies);
			if (context == null)
				return results;

			var range = ToRange(text, context.Start, context.End);
			foreach (var item in DocumentCompletion.GetItems(context))
			{
				results.Add(new Lsp.CompletionItem
				{
					Label = item.Label,
					Kind = GetKind(item.Item.Type),
					Detail = string.IsNullOrEmpty(item.Item.Suffix) ? null : item.Item.Suffix,
					Documentation = ToMarkup(item.Item.Description),
					SortText = item.Label,
					FilterText = item.Label,
					TextEdit = new TextEdit { Range = range, NewText = item.InsertText },
					AdditionalTextEdits = item.NamespaceEdit == null ? null : new List<TextEdit>
					{
						new TextEdit { Range = ToRange(text, item.NamespaceEdit.Offset, item.NamespaceEdit.Offset), NewText = item.NamespaceEdit.Text }
					}
				});
			}
			return results;
		}

		public static Hover GetHover(string text, int offset, CompletionFormat format, string rootTypeName = null, IList<Assembly> projectAssemblies = null)
		{
			var match = DocumentCompletion.FindItemAt(text, offset, format, rootTypeName, out var start, out var end, projectAssemblies);
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
