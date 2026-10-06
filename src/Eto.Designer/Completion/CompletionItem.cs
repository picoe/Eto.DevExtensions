using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.IO;
using System.Text;

namespace Eto.Designer.Completion
{
	[Flags]
	public enum CompletionBehavior
	{
		None = 0,
		ChildProperty = 1 << 0,

		/// <summary>A read-only collection, which xaml can only fill using a property element.</summary>
		PropertyElementOnly = 1 << 1
	}

	public class CompletionItem
	{
		public CompletionType Type { get; set; }

		public string Name { get; set; }

		public string Description { get; set; }

		public string Suffix { get; set; }

		public CompletionBehavior Behavior { get; set; }

		/// <summary>Namespace the document must declare for this item to resolve, when it hasn't yet.</summary>
		public CompletionNamespace Namespace { get; set; }
	}
	
}
