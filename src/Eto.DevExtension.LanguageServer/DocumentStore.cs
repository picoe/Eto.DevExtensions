using System;
using System.Collections.Concurrent;

namespace Eto.DevExtension.LanguageServer
{
	/// <summary>Text of the documents the client currently has open, keyed by uri.</summary>
	public class DocumentStore
	{
		readonly ConcurrentDictionary<string, string> documents = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

		public void Set(string uri, string text) => documents[uri] = text ?? string.Empty;

		public void Remove(string uri) => documents.TryRemove(uri, out _);

		public string Get(string uri) => documents.TryGetValue(uri, out var text) ? text : null;

		/// <summary>Character offset of an LSP line/character position, clamped to the document.</summary>
		public static int GetOffset(string text, int line, int character)
		{
			var offset = 0;
			for (var i = 0; i < line; i++)
			{
				var next = text.IndexOf('\n', offset);
				if (next < 0)
					return text.Length;
				offset = next + 1;
			}
			var lineEnd = text.IndexOf('\n', offset);
			if (lineEnd < 0)
				lineEnd = text.Length;
			else if (lineEnd > offset && text[lineEnd - 1] == '\r')
				lineEnd--;
			return Math.Min(offset + character, lineEnd);
		}

		/// <summary>LSP line/character position of a character offset.</summary>
		public static (int Line, int Character) GetPosition(string text, int offset)
		{
			offset = Math.Min(Math.Max(offset, 0), text.Length);
			var line = 0;
			var lineStart = 0;
			for (var i = 0; i < offset; i++)
			{
				if (text[i] != '\n')
					continue;
				line++;
				lineStart = i + 1;
			}
			return (line, offset - lineStart);
		}
	}
}
