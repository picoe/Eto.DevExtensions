using System;
using System.Collections.Generic;

namespace Eto.Designer.Completion
{
	/// <summary>
	/// Reads a .jeto document up to the cursor and reports what should be completed there.
	/// </summary>
	/// <remarks>
	/// Objects are keyed by a <c>$type</c> property, so a nested object maps onto the same
	/// "Class.Property" path the xaml parser produces for a property element. Json.NET's relaxed
	/// syntax is accepted: comments, single quotes and unquoted property names.
	/// </remarks>
	public static class JsonParser
	{
		public const string TypeProperty = "$type";
		public const string NameProperty = "$name";

		enum Container
		{
			Object,
			Array
		}

		class Frame
		{
			public Container Container;
			public int PathLength;
			public string TypeName;
			public string PropertyName;
			public bool ExpectValue;
			public bool PushedType;
			public readonly List<string> Keys = new List<string>();
		}

		public static ParseInfo Read(string text) => Read(text, null);

		/// <param name="rootTypeName">
		/// Type the document loads into, used when the root object leaves out its $type.
		/// </param>
		public static ParseInfo Read(string text, string rootTypeName)
		{
			var path = new List<string>();
			var stack = new List<Frame>();
			Frame frame = null;

			// set when the final token runs up to the cursor, ie. the one being typed
			bool? partialIsValue = null;
			var partialIsQuoted = false;
			var inComment = false;

			var i = 0;
			while (i < text.Length)
			{
				var ch = text[i];

				if (char.IsWhiteSpace(ch))
				{
					i++;
					continue;
				}

				if (ch == '/' && i + 1 < text.Length && (text[i + 1] == '/' || text[i + 1] == '*'))
				{
					i = SkipComment(text, i, out inComment);
					continue;
				}

				if (ch == '"' || ch == '\'')
				{
					var end = ReadQuoted(text, i, out var value);
					var complete = end < text.Length;
					if (complete)
						Accept(frame, path, value, true);
					else
					{
						partialIsValue = IsValuePosition(frame);
						partialIsQuoted = true;
					}
					i = end + 1;
					continue;
				}

				if (IsBareChar(ch))
				{
					var end = i;
					while (end < text.Length && IsBareChar(text[end]))
						end++;
					if (end < text.Length)
						Accept(frame, path, text.Substring(i, end - i), false);
					else
						partialIsValue = IsValuePosition(frame);
					i = end;
					continue;
				}

				switch (ch)
				{
					case ':':
						if (frame != null && frame.Container == Container.Object)
							frame.ExpectValue = true;
						break;
					case ',':
						if (frame != null)
						{
							frame.ExpectValue = false;
							frame.PropertyName = null;
						}
						break;
					case '{':
					case '[':
						var child = new Frame
						{
							Container = ch == '{' ? Container.Object : Container.Array,
							PathLength = path.Count
						};
						if (frame == null && child.Container == Container.Object && !string.IsNullOrEmpty(rootTypeName))
						{
							child.TypeName = rootTypeName;
							child.PushedType = true;
							path.Add(rootTypeName);
						}
						// descending into a property of a known type adds the same path entry a
						// <Type.Property> element would in xaml, so content types resolve the same way
						if (frame != null
							&& frame.Container == Container.Object
							&& !string.IsNullOrEmpty(frame.TypeName)
							&& !string.IsNullOrEmpty(frame.PropertyName))
						{
							path.Add(frame.TypeName + "." + frame.PropertyName);
						}
						stack.Add(child);
						frame = child;
						break;
					case '}':
					case ']':
						if (frame != null)
						{
							path.RemoveRange(frame.PathLength, path.Count - frame.PathLength);
							stack.RemoveAt(stack.Count - 1);
							frame = stack.Count > 0 ? stack[stack.Count - 1] : null;
							if (frame != null)
							{
								frame.ExpectValue = false;
								frame.PropertyName = null;
							}
						}
						break;
				}
				i++;
			}

			var nodes = new List<CompletionPathNode>();
			var info = new ParseInfo { Nodes = nodes, Path = path, Mode = CompletionMode.None, InString = partialIsQuoted };
			if (inComment || frame == null || frame.Container == Container.Array)
				return info;

			if (partialIsValue == false || (partialIsValue == null && !frame.ExpectValue))
			{
				var context = new CompletionPathNode(string.Empty, frame.TypeName ?? string.Empty, CompletionMode.Class);
				context.Attributes.AddRange(frame.Keys);
				nodes.Add(context);
				info.Mode = CompletionMode.Property;
			}
			else if (frame.PropertyName == TypeProperty)
			{
				info.Mode = CompletionMode.Class;
			}
			else if (!string.IsNullOrEmpty(frame.PropertyName))
			{
				nodes.Add(new CompletionPathNode(string.Empty, frame.PropertyName, CompletionMode.Property));
				info.Mode = CompletionMode.Value;
			}

			return info;
		}

		static bool IsValuePosition(Frame frame) =>
			frame == null || frame.Container == Container.Array || frame.ExpectValue;

		static void Accept(Frame frame, List<string> path, string value, bool quoted)
		{
			if (frame == null)
				return;

			if (!IsValuePosition(frame))
			{
				frame.PropertyName = value;
				frame.Keys.Add(value);
			}
			else if (frame.Container == Container.Object)
			{
				if (frame.PropertyName == TypeProperty && quoted)
				{
					// may be assembly qualified, eg. "My.Namespace.MyPanel, MyAssembly"
					var comma = value.IndexOf(',');
					frame.TypeName = (comma >= 0 ? value.Substring(0, comma) : value).Trim();
					if (frame.PushedType && path.Count == frame.PathLength + 1)
						path[frame.PathLength] = frame.TypeName;
					else
					{
						path.Add(frame.TypeName);
						frame.PushedType = true;
					}
				}
				frame.ExpectValue = false;
			}
		}

		/// <summary>Index of the closing quote, or the text length when it is unterminated.</summary>
		static int ReadQuoted(string text, int start, out string value)
		{
			var quote = text[start];
			var i = start + 1;
			while (i < text.Length)
			{
				if (text[i] == '\\')
				{
					i += 2;
					continue;
				}
				if (text[i] == quote)
					break;
				i++;
			}
			var end = Math.Min(i, text.Length);
			value = text.Substring(start + 1, end - start - 1);
			return end;
		}

		/// <summary>Index just past the comment, flagging when the cursor is still inside it.</summary>
		static int SkipComment(string text, int start, out bool unterminated)
		{
			if (text[start + 1] == '/')
			{
				var newline = text.IndexOf('\n', start);
				unterminated = false;
				return newline < 0 ? text.Length : newline + 1;
			}

			var close = text.IndexOf("*/", start + 2, StringComparison.Ordinal);
			unterminated = close < 0;
			return close < 0 ? text.Length : close + 2;
		}

		static bool IsBareChar(char ch) =>
			char.IsLetterOrDigit(ch) || ch == '_' || ch == '$' || ch == '.' || ch == '-' || ch == '+';
	}
}
