using System;
using System.Reflection;

namespace Eto.DevExtension.PreviewHost
{
	/// <summary>Finds where in a designer file a XAML or JSON error is, to underline it in the editor.</summary>
	static class ErrorLocation
	{
		/// <returns>The word at the error's position, its whole line when there's no word there, or null when the error has no position.</returns>
		public static RenderRange Find(Exception ex, string text)
		{
			// the outermost one is from this file, as inner ones can be from files its controls load
			for (var e = ex; e != null; e = e.InnerException)
			{
				// by name, as Portable.Xaml, Newtonsoft.Json and System.Xml each have their own, in the project's version
				var line = GetInt(e, "LineNumber");
				if (line > 0)
					return GetRange(text, line - 1, GetInt(e, "LinePosition") - 1);
			}
			return null;
		}

		static int GetInt(Exception ex, string name) =>
			ex.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(ex) is int value ? value : 0;

		static RenderRange GetRange(string text, int line, int column)
		{
			var lines = text.Split('\n');
			if (line >= lines.Length)
				return null;
			var lineText = lines[line].TrimEnd('\r');

			var start = column;
			// Newtonsoft points just past what it read
			if (!IsWord(lineText, start) && IsWord(lineText, start - 1))
				start--;
			var end = start;
			if (IsWord(lineText, start))
			{
				while (IsWord(lineText, start - 1))
					start--;
				while (IsWord(lineText, end))
					end++;
			}
			else
			{
				start = lineText.Length - lineText.TrimStart().Length;
				end = lineText.TrimEnd().Length;
				// nothing to underline on a blank line, so mark where it is
				if (end <= start)
					end = start + 1;
			}
			return new RenderRange
			{
				Start = new RenderPosition { Line = line, Character = start },
				End = new RenderPosition { Line = line, Character = end }
			};
		}

		static bool IsWord(string text, int index) =>
			index >= 0 && index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_' || text[index] == '.' || text[index] == ':');
	}
}
