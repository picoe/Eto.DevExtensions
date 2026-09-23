using System.Collections.Generic;

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

	/// <summary>LSP CompletionItemKind values.</summary>
	public static class CompletionItemKind
	{
		public const int Class = 7;
		public const int Property = 10;
		public const int Event = 23;
		public const int Field = 5;
		public const int Value = 12;
		public const int EnumMember = 20;
	}
}
