using System;
using System.Collections.Generic;
using System.Linq;

namespace Eto.Designer.Completion
{
	/// <summary>
	/// Completions for designer-only attributes, eg. d:DataContext.
	/// </summary>
	class DesignCompletion : Completion
	{
		public const string DataContextDescription = "Sample data context shown only in the designer, which also lets {Binding} complete its properties.";

		public override IEnumerable<CompletionItem> GetClasses(IEnumerable<string> path, Func<Type, bool> filter)
		{
			yield break;
		}

		public override IEnumerable<CompletionItem> GetProperties(string objectName, IEnumerable<string> path)
		{
			if (objectName.Contains('.'))
				yield break;
			yield return new CompletionItem
			{
				Name = PrefixWithColon + BindingCompletion.DataContextProperty,
				Type = CompletionType.Attribute,
				Description = DataContextDescription + " Use {" + PrefixWithColon + "DesignInstance local:MyViewModel}."
			};
		}

		public override bool HandlesPrefix(string prefix) => true;
	}
}
