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
			yield return new CompletionItem
			{
				Name = BindingCompletion.DesignDataContextProperty,
				Type = CompletionType.Attribute,
				Description = DesignCompletion.DataContextDescription + " Use a type name such as \"MyApp.MyViewModel, MyApp\", or an object with a $type and sample values."
			};
		}

		public override Func<Type, bool> GetFilter(IEnumerable<string> path)
		{
			// a data context object is created from its $type, so it needs a parameterless constructor
			var last = path.LastOrDefault();
			if (last != null && (last.EndsWith("." + BindingCompletion.DesignDataContextProperty) || last.EndsWith("." + BindingCompletion.DataContextProperty)))
				return t => IsDataContextType(t) && t.GetConstructor(Type.EmptyTypes) != null;
			return null;
		}

		/// <summary>Classes that could be a data context, leaving out controls and other framework plumbing.</summary>
		public static bool IsDataContextType(Type type) =>
			type.IsClass
			&& !type.IsAbstract
			&& !type.IsGenericTypeDefinition
			&& !typeof(Eto.Widget).IsAssignableFrom(type)
			&& !typeof(Attribute).IsAssignableFrom(type)
			&& !typeof(EventArgs).IsAssignableFrom(type)
			&& !typeof(Delegate).IsAssignableFrom(type)
			&& !typeof(Exception).IsAssignableFrom(type);

		public override bool HandlesPrefix(string prefix) => true;
	}
}
