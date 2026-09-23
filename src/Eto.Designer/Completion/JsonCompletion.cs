using System;
using System.Collections.Generic;
using System.Linq;

namespace Eto.Designer.Completion
{
	/// <summary>
	/// Completions for the json-specific parts of a .jeto document.
	/// </summary>
	class JsonCompletion : Completion
	{
		public override IEnumerable<CompletionItem> GetClasses(IEnumerable<string> path, Func<Type, bool> filter)
		{
			yield break;
		}

		public override IEnumerable<CompletionItem> GetProperties(string objectName, IEnumerable<string> path)
		{
			yield return new CompletionItem
			{
				Name = JsonParser.TypeProperty,
				Type = CompletionType.Attribute,
				Description = "The Eto.Forms type to create for this object."
			};
			yield return new CompletionItem
			{
				Name = JsonParser.NameProperty,
				Type = CompletionType.Attribute,
				Description = "Sets the ID of the object, which will automatically bind to a field or property of the same name in your backing class."
			};
		}

		public override bool HandlesPrefix(string prefix) => true;
	}
}
