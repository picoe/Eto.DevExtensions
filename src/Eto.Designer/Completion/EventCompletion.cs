using System;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Eto.Designer.Completion
{
	/// <summary>
	/// Works out which event a value is being typed for, so a handler can be offered from the code behind.
	/// </summary>
	public static class EventCompletion
	{
		public class Request
		{
			public EventInfo Event { get; set; }

			/// <summary>ID of the object the event is on, or null when it has none.</summary>
			public string ObjectId { get; set; }

			/// <summary>Name of a new handler, eg. OkButton_Click, or Button_Click when the object has no ID.</summary>
			public string SuggestedName
			{
				get
				{
					var owner = string.IsNullOrEmpty(ObjectId) ? Event.ReflectedType?.Name : ObjectId;
					owner = Regex.Replace(owner ?? string.Empty, @"\W", string.Empty);
					if (owner.Length > 0)
						owner = char.ToUpperInvariant(owner[0]) + owner.Substring(1) + "_";
					return owner + Event.Name;
				}
			}
		}

		static readonly Regex xamlIdReg = new Regex(@"(?<![\w.:-])(?:ID|[\w.-]+:Name)\s*=\s*(?:""(?<id>[^""]*)""|'(?<id>[^']*)')", RegexOptions.Compiled);
		static readonly Regex jsonIdReg = new Regex(@"[""']?(?:ID|\$name)[""']?\s*:\s*(?:""(?<id>[^""]*)""|'(?<id>[^']*)')", RegexOptions.Compiled);

		/// <returns>The request, or null when the value isn't for an event.</returns>
		public static Request Read(DocumentCompletionContext context)
		{
			if (context == null || context.Binding != null || context.Markup != null || context.Info.Mode != CompletionMode.Value)
				return null;

			var info = context.Info;
			var evt = Completion.GetEvent(info.Namespaces, info.Path, info.Context, context.Format, context.ProjectAssemblies);
			if (evt == null)
				return null;

			var id = context.Format == CompletionFormat.Json ? GetJsonId(context.Text, context.Start) : GetXamlId(context.Text, context.Start);
			return new Request { Event = evt, ObjectId = string.IsNullOrWhiteSpace(id) ? null : id.Trim() };
		}

		static string GetXamlId(string text, int offset)
		{
			var tagStart = text.LastIndexOf('<', Math.Max(offset - 1, 0));
			if (tagStart < 0)
				return null;
			var tagEnd = text.IndexOf('>', offset);
			var tag = text.Substring(tagStart, (tagEnd < 0 ? text.Length : tagEnd) - tagStart);
			var match = xamlIdReg.Match(tag);
			return match.Success ? match.Groups["id"].Value : null;
		}

		/// <summary>ID of the json object around <paramref name="offset"/>, ignoring any objects nested in it.</summary>
		static string GetJsonId(string text, int offset)
		{
			var start = -1;
			var depth = 0;
			for (var i = offset - 1; i >= 0; i--)
			{
				var ch = text[i];
				if (ch == '}' || ch == ']')
					depth++;
				else if (ch == '{' || ch == '[')
				{
					if (depth == 0)
					{
						if (ch == '[')
							return null;
						start = i;
						break;
					}
					depth--;
				}
			}
			if (start < 0)
				return null;

			// keep only the object's own text, so a child's ID isn't mistaken for it
			var own = new StringBuilder();
			depth = 0;
			var quote = '\0';
			for (var i = start + 1; i < text.Length; i++)
			{
				var ch = text[i];
				if (quote != '\0')
				{
					if (ch == '\\')
					{
						if (depth == 0 && i + 1 < text.Length)
							own.Append(ch).Append(text[i + 1]);
						i++;
						continue;
					}
					if (ch == quote)
						quote = '\0';
				}
				else if (ch == '"' || ch == '\'')
					quote = ch;
				else if (ch == '{' || ch == '[')
				{
					depth++;
					continue;
				}
				else if (ch == '}' || ch == ']')
				{
					if (depth == 0)
						break;
					depth--;
					continue;
				}
				if (depth == 0)
					own.Append(ch);
			}

			var match = jsonIdReg.Match(own.ToString());
			return match.Success ? match.Groups["id"].Value : null;
		}
	}
}
