using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Eto.DevExtension.LanguageServer.Lsp
{
	// Only the subset of the protocol this server speaks.

	public class Position
	{
		public int Line { get; set; }
		public int Character { get; set; }
	}

	public class Range
	{
		public Position Start { get; set; }
		public Position End { get; set; }
	}

	public class TextEdit
	{
		public Range Range { get; set; }
		public string NewText { get; set; }
	}

	public class MarkupContent
	{
		public string Kind { get; set; } = "markdown";
		public string Value { get; set; }
	}

	public class CompletionItem
	{
		public string Label { get; set; }
		public int Kind { get; set; }
		public string Detail { get; set; }
		public MarkupContent Documentation { get; set; }
		public string SortText { get; set; }
		public string FilterText { get; set; }
		public TextEdit TextEdit { get; set; }
		public List<TextEdit> AdditionalTextEdits { get; set; }
		public int? InsertTextFormat { get; set; }
		public List<string> CommitCharacters { get; set; }
		public Command Command { get; set; }
		public CompletionData Data { get; set; }
	}

	/// <summary>Extra detail for this server's own clients, which other clients ignore.</summary>
	public class CompletionData
	{
		public string ValueText { get; set; }
		public int ValueReplaces { get; set; }
	}

	public class Command
	{
		public string Title { get; set; }
		[JsonPropertyName("command")]
		public string Name { get; set; }
		public List<object> Arguments { get; set; }
	}

	public class Location
	{
		public string Uri { get; set; }
		public Range Range { get; set; }
	}

	public class WorkspaceEdit
	{
		/// <summary>Edits keyed by document uri.</summary>
		public Dictionary<string, List<TextEdit>> Changes { get; set; }
	}

	public class CompletionList
	{
		public bool IsIncomplete { get; set; }
		public List<CompletionItem> Items { get; set; } = new List<CompletionItem>();
	}

	public class Hover
	{
		public MarkupContent Contents { get; set; }
		public Range Range { get; set; }
	}

	/// <summary>LSP InsertTextFormat values.</summary>
	public static class InsertTextFormat
	{
		public const int PlainText = 1;
		public const int Snippet = 2;
	}

	/// <summary>LSP CompletionItemKind values.</summary>
	public static class CompletionItemKind
	{
		public const int Method = 2;
		public const int Class = 7;
		public const int Property = 10;
		public const int Event = 23;
		public const int Field = 5;
		public const int Value = 12;
		public const int EnumMember = 20;
	}
}
