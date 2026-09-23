using System.Collections.Generic;
using System.Linq;

namespace Eto.Designer.Completion
{
	/// <summary>
	/// What the cursor is sitting on, as reported by <see cref="XmlParser"/> or <see cref="JsonParser"/>.
	/// </summary>
	public class ParseInfo
	{
		List<string> path;

		public IEnumerable<CompletionPathNode> Nodes { get; set; }

		public CompletionMode Mode { get; set; }

		public bool IsChildProperty { get; set; }

		/// <summary>True when the cursor is inside a quoted json string.</summary>
		public bool InString { get; set; }

		/// <summary>
		/// Enclosing class names from the root to the cursor, eg. Panel, Panel.Content, Button.
		/// </summary>
		public List<string> Path
		{
			get => path ?? (path = Nodes?.Where(r => r.Mode == CompletionMode.Class).Select(r => r.Name).ToList() ?? new List<string>());
			set => path = value;
		}

		public IEnumerable<CompletionNamespace> Namespaces =>
			Nodes?.SelectMany(r => r.Namespaces ?? Enumerable.Empty<CompletionNamespace>()) ?? Enumerable.Empty<CompletionNamespace>();

		public CompletionPathNode Context => Nodes?.LastOrDefault();
	}
}
