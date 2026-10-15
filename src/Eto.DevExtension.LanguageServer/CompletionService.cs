using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Eto.Designer.Completion;
using Eto.DevExtension.LanguageServer.Lsp;
using Microsoft.CodeAnalysis.CSharp;

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

		public const string CreateEventHandlerCommand = "eto.createEventHandler";

		/// <param name="snippets">The client takes snippets, so property names get an empty value with the caret inside.</param>
		/// <param name="triggerSuggestCommand">Client command that reopens completion, so a value list shows straight after its property.</param>
		/// <param name="triggerCharacter">The character that opened completion, if any.</param>
		/// <param name="documentPath">Path of the document, used to find its code behind.</param>
		public static List<Lsp.CompletionItem> GetCompletions(string text, int offset, CompletionFormat format, string rootTypeName = null, IList<Assembly> projectAssemblies = null, bool snippets = false, string triggerSuggestCommand = null, string triggerCharacter = null, string documentPath = null)
		{
			var results = new List<Lsp.CompletionItem>();
			var context = DocumentCompletion.GetContext(text, offset, format, rootTypeName, projectAssemblies);
			if (context == null)
				return results;
			// '/' is only a trigger for "</", so it doesn't pop up in json comments or paths
			if (triggerCharacter == "/" && context.ClosingTag == null)
				return results;
			// a comma between json properties is too early; the space or quote after it opens completion instead
			if (triggerCharacter == "," && format == CompletionFormat.Json && !context.Quoted)
				return results;

			var range = ToRange(text, context.Start, context.End);
			var eventRequest = EventCompletion.Read(context);
			if (eventRequest != null)
			{
				AddEventHandlers(results, context, eventRequest, offset, range, documentPath);
				return results;
			}

			foreach (var item in DocumentCompletion.GetItems(context))
			{
				var snippet = snippets && item.ValueText != null;
				results.Add(new Lsp.CompletionItem
				{
					Label = item.Label,
					Kind = GetKind(item.Item.Type),
					Detail = string.IsNullOrEmpty(item.Item.Suffix) ? null : item.Item.Suffix,
					Documentation = ToMarkup(item.Item.Description),
					SortText = item.Label,
					FilterText = item.Label,
					TextEdit = snippet
						? new TextEdit { Range = ToRange(text, context.Start, context.End + item.ValueReplaces), NewText = ToSnippet(item) }
						: new TextEdit { Range = range, NewText = item.InsertText },
					InsertTextFormat = snippet ? Lsp.InsertTextFormat.Snippet : (int?)null,
					Command = snippet && triggerSuggestCommand != null && item.Item.Behavior.HasFlag(CompletionBehavior.HasValues)
						? new Command { Title = "Suggest", Name = triggerSuggestCommand }
						: null,
					// lets a client without snippets, such as Visual Studio, add the value itself
					Data = item.ValueText == null ? null : new CompletionData { ValueText = item.ValueText, ValueReplaces = item.ValueReplaces },
					AdditionalTextEdits = item.NamespaceEdit == null ? null : new List<TextEdit>
					{
						new TextEdit { Range = ToRange(text, item.NamespaceEdit.Offset, item.NamespaceEdit.Offset), NewText = item.NamespaceEdit.Text }
					}
				});
			}
			return results;
		}

		/// <summary>Methods in the code behind that can handle the event, and one to create a new handler.</summary>
		static void AddEventHandlers(List<Lsp.CompletionItem> results, DocumentCompletionContext context, EventCompletion.Request request, int offset, Lsp.Range range, string documentPath)
		{
			var codeBehind = CodeBehind.Load(documentPath, context.Text);
			if (codeBehind == null)
				return;

			// values are quoted, so add them when the cursor isn't already in a string, eg. after typing Click=
			var addQuotes = !context.Quoted;
			string Quote(string name) => addQuotes ? "\"" + name + "\"" : name;

			var handlers = codeBehind.GetHandlers(request.Event).ToList();
			foreach (var handler in handlers)
			{
				results.Add(new Lsp.CompletionItem
				{
					Label = handler,
					Kind = CompletionItemKind.Method,
					SortText = "1" + handler,
					FilterText = handler,
					TextEdit = new TextEdit { Range = range, NewText = Quote(handler) }
				});
			}

			// a name typed before asking for completions is used for the new handler
			var typed = context.Quoted ? context.Text.Substring(context.Start, Math.Max(offset - context.Start, 0)).Trim() : null;
			var name = !string.IsNullOrEmpty(typed) && SyntaxFacts.IsValidIdentifier(typed) ? typed : request.SuggestedName;
			if (handlers.Contains(name))
				return;

			var insert = codeBehind.AddHandler(name, request.Event);
			if (insert == null)
				return;

			var position = codeBehind.GetPosition(insert.Value.Offset);
			var at = new Position { Line = position.Line, Character = position.Character };
			var fileName = Path.GetFileName(codeBehind.FilePath);
			var edit = new WorkspaceEdit
			{
				Changes = new Dictionary<string, List<TextEdit>>
				{
					[new Uri(codeBehind.FilePath).AbsoluteUri] = new List<TextEdit> { new TextEdit { Range = new Lsp.Range { Start = at, End = at }, NewText = insert.Value.Text } }
				}
			};
			results.Add(new Lsp.CompletionItem
			{
				Label = name,
				Kind = CompletionItemKind.Event,
				Detail = "Create in " + fileName,
				Documentation = new MarkupContent { Value = $"Adds a `{name}` method to {fileName} to handle {request.Event.Name}." },
				SortText = "0" + name,
				FilterText = name,
				TextEdit = new TextEdit { Range = range, NewText = Quote(name) },
				Command = new Command { Title = "Create event handler", Name = CreateEventHandlerCommand, Arguments = new List<object> { edit } }
			});
		}

		/// <summary>Where the handler named by an event's value is in the code behind.</summary>
		/// <returns>The location, an empty list when the value names no method there, or null when the value isn't for an event.</returns>
		public static List<Location> GetDefinition(string text, int offset, CompletionFormat format, string rootTypeName, IList<Assembly> projectAssemblies, string documentPath)
		{
			var context = DocumentCompletion.GetContext(text, offset, format, rootTypeName, projectAssemblies);
			if (EventCompletion.Read(context) == null)
				return null;

			var results = new List<Location>();
			var name = text.Substring(context.Start, context.End - context.Start).Trim();
			var codeBehind = string.IsNullOrEmpty(name) ? null : CodeBehind.Load(documentPath, text);
			var method = codeBehind?.FindMethod(name);
			if (method != null)
			{
				var position = codeBehind.GetPosition(method.Value);
				var start = new Position { Line = position.Line, Character = position.Character };
				var end = new Position { Line = position.Line, Character = position.Character + name.Length };
				results.Add(new Location { Uri = new Uri(codeBehind.FilePath).AbsoluteUri, Range = new Lsp.Range { Start = start, End = end } });
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

		static string ToSnippet(DocumentCompletionItem item) =>
			EscapeSnippet(item.InsertText + item.ValueText.Substring(0, item.ValueCaret)) + "$0" + EscapeSnippet(item.ValueText.Substring(item.ValueCaret));

		static string EscapeSnippet(string text) =>
			text.Replace("\\", "\\\\").Replace("$", "\\$").Replace("}", "\\}");

		static int GetKind(CompletionType type)
		{
			switch (type)
			{
				case CompletionType.Class: return CompletionItemKind.Class;
				case CompletionType.Property: return CompletionItemKind.Property;
				case CompletionType.Event: return CompletionItemKind.Event;
				case CompletionType.Field: return CompletionItemKind.Field;
				case CompletionType.Attribute: return CompletionItemKind.Property;
				case CompletionType.Method: return CompletionItemKind.Method;
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
