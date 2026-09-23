using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.IO;
using System.Text;
using System.Collections;
using Eto.Drawing;
using Portable.Xaml;
using Eto.Forms;
using sc = System.ComponentModel;

namespace Eto.Designer.Completion
{
	class TypeCompletion : Completion
	{
		public Assembly Assembly { get; set; }

		/// <summary>Namespace to offer types from, or null for every namespace in the assembly.</summary>
		public string Namespace { get; set; }

		/// <summary>Names types as "Namespace.Type, Assembly", the way a .jeto $type refers to types outside Eto.</summary>
		public bool UseFullName { get; set; }

		List<Type> exportedTypes;

		List<Type> GetExportedTypes()
		{
			if (exportedTypes == null)
				exportedTypes = ProjectTypes.GetTypes(Assembly).Where(r => ProjectTypes.IsCreatable(r) && (Namespace == null || r.Namespace == Namespace)).ToList();
			return exportedTypes;
		}

		string GetName(Type type) =>
			UseFullName ? type.FullName + ", " + ProjectTypes.GetAssemblyName(type.Assembly) : PrefixWithColon + type.Name;

		bool IsOwnType(Type type) =>
			type.Assembly == Assembly && (Namespace == null || type.Namespace == Namespace);

		/// <summary>Type named by an object in the document, or null when it isn't one of ours.</summary>
		Type FindType(string objectName)
		{
			if (string.IsNullOrEmpty(objectName))
				return null;
			string fullName;
			if (UseFullName)
			{
				// json paths drop the assembly name, but a $type value being typed may still have it
				var comma = objectName.IndexOf(',');
				fullName = (comma >= 0 ? objectName.Substring(0, comma) : objectName).Trim();
			}
			else
			{
				var prefix = PrefixWithColon;
				if (!objectName.StartsWith(prefix))
					return null;
				fullName = Namespace + "." + objectName.Substring(prefix.Length);
			}
			try
			{
				return Assembly.GetType(fullName, false);
			}
			catch (Exception)
			{
				// project types can fail to load when one of their dependencies is missing
				return null;
			}
		}

		static bool Matches(Func<Type, bool> filter, Type type)
		{
			try
			{
				return filter(type);
			}
			catch (Exception)
			{
				return false;
			}
		}

		public static sc.TypeConverter GetConverter(Type type)
		{
			var attribute = type.GetCustomAttribute<sc.TypeConverterAttribute>(false);

			if (attribute != null)
			{
				var converterType = Type.GetType(attribute.ConverterTypeName, false);
				if (converterType != null)
					return Activator.CreateInstance(converterType) as sc.TypeConverter;
			}

			return sc.TypeDescriptor.GetConverter(type);
		}

		public override Func<Type, bool> GetFilter(IEnumerable<string> path)
		{
			string propertyName;
			var nodeType = GetNodeType(path.LastOrDefault(), out propertyName);
			var contentType = GetContentType(nodeType, propertyName);
			if (contentType != null)
			{
				var converter = GetConverter(contentType);
				return t =>
				{
					if (contentType.IsAssignableFrom(t))
						return true;

					if (converter?.CanConvertFrom(t) == true)
						return true;
					if (GetConverter(t)?.CanConvertTo(contentType) == true && contentType != typeof(string))
						return true;

					return false;
				};
			}
			return null;
		}

		public override IEnumerable<CompletionItem> GetClasses(IEnumerable<string> path, Func<Type, bool> filter)
		{
			var prefixWithColon = PrefixWithColon;

			string contentProperty;
			var lastPath = path.LastOrDefault();
			var nodeType = GetNodeType(lastPath, out contentProperty);
			var contentType = GetContentType(nodeType, contentProperty);

			var types = GetExportedTypes();
			if (filter != null)
			{
				foreach (var result in types.Where(r => Matches(filter, r)))
				{
					// special case, don't allow windows as a child control
					if (contentType != null
					    && typeof(Window).IsAssignableFrom(result))
						continue;


					yield return new CompletionItem
					{
						Name = GetName(result),
						Description = XmlComments.GetSummary(result),
						Type = CompletionType.Class
					};
				}
			}

			if (contentType != null
				&& !contentType.IsAbstract
				&& !types.Contains(contentType)
				&& contentType.Assembly == Assembly)
				yield return new CompletionItem
				{
					Name = GetName(contentType),
					Description = XmlComments.GetSummary(contentType),
					Type = CompletionType.Class
				};
			if (nodeType != null && !UseFullName && !lastPath.Contains("."))
			{
				yield return new CompletionItem
				{
					Name = prefixWithColon + nodeType.Name + ".",
					Description = "Add a property tag",
					Type = CompletionType.Property,
					Behavior = CompletionBehavior.ChildProperty
				};
			}

		}

		public override string GetImpliedTypeName(IEnumerable<string> path)
		{
			string propertyName;
			var nodeType = GetNodeType(path?.LastOrDefault(), out propertyName);
			var contentType = GetContentType(nodeType, propertyName);
			return contentType != null && IsOwnType(contentType) ? GetName(contentType) : null;
		}

		Type GetNodeType(string last, out string propertyName)
		{
			propertyName = null;
			if (string.IsNullOrEmpty(last))
				return null;

			var types = GetExportedTypes();
			if (UseFullName)
			{
				// a full name is dotted already, so "My.Panel.Content" is either a type or a type's property
				var type = FindType(last);
				if (type == null)
				{
					var lastDot = last.LastIndexOf('.');
					if (lastDot <= 0)
						return null;
					propertyName = last.Substring(lastDot + 1);
					type = FindType(last.Substring(0, lastDot));
				}
				return type != null && types.Contains(type) ? type : null;
			}

			var prefix = PrefixWithColon;
			if (!string.IsNullOrEmpty(prefix))
			{
				if (!last.StartsWith(prefix))
					return null;
				last = last.Substring(prefix.Length);
			}
			else if (last.Contains(':'))
				return null;

			if (string.IsNullOrEmpty(last))
				return null;
			var dotIndex = last.IndexOf('.');
			if (dotIndex > 0)
			{
				propertyName = last.Substring(dotIndex + 1);
				last = last.Substring(0, dotIndex);
			}
			last = Namespace + "." + last;
			return types.FirstOrDefault(r => r.FullName == last);
		}

		Type GetContentType(Type type, string propertyName)
		{
			if (type != null)
			{
				if (string.IsNullOrEmpty(propertyName))
				{
					var contentProperty = type.GetCustomAttribute<ContentPropertyAttribute>();
					if (contentProperty != null)
					{
						propertyName = contentProperty.Name;
					}
				}
				if (!string.IsNullOrEmpty(propertyName))
				{
					var prop = type.GetProperty(propertyName);
					if (prop != null)
					{
						var propType = prop.PropertyType;
						if (typeof(IList).IsAssignableFrom(propType))
						{
							var list = propType.GetInterfaces().FirstOrDefault(r => r.IsGenericType && r.GetGenericTypeDefinition() == typeof(IList<>));
							if (list != null)
							{
								return list.GenericTypeArguments[0];
							}
						}
						return prop.PropertyType;
					}
				}
			}
			return null;
		}

		public override bool? HasContent(string objectName, IEnumerable<string> path)
		{
			var type = FindType(objectName);
			if (type != null)
			{
				return type.GetTypeInfo().GetCustomAttribute<Eto.ContentPropertyAttribute>() != null;
			}
			return base.HasContent(objectName, path);
		}

		public override IEnumerable<CompletionItem> GetProperties(string objectName, IEnumerable<string> path)
		{
			var type = FindType(objectName);
			if (type != null)
			{
				foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
				{
					if (prop.SetMethod == null || !prop.SetMethod.IsPublic)
						continue;

					if (prop.GetCustomAttribute<ObsoleteAttribute>() != null)
						continue;

					var underlyingType = Nullable.GetUnderlyingType(prop.PropertyType);
					var suffix = underlyingType != null ? underlyingType.Name + "?" : prop.PropertyType.Name;

					// todo: get friendly names for generic types

					yield return new CompletionItem
					{
						Name = prop.Name,
						Suffix = suffix,
						Description = XmlComments.GetSummary(prop),
						Type = CompletionType.Property
					};
				}
				foreach (var evt in type.GetEvents(BindingFlags.Public | BindingFlags.Instance))
				{
					yield return new CompletionItem
					{
						Name = evt.Name,
						Description = XmlComments.GetSummary(evt),
						Type = CompletionType.Event
					};
				}
			}
		}

		public override IEnumerable<CompletionItem> GetPropertyValues(string objectName, string propertyName, IEnumerable<string> path)
		{
			var type = FindType(objectName);
			if (type != null)
			{
				var prop = type.GetRuntimeProperty(propertyName);
				if (prop != null)
				{
					var propertyType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
					if (propertyType == typeof(bool))
					{
						yield return new CompletionItem { Type = CompletionType.Literal, Name = "True" };
						yield return new CompletionItem { Type = CompletionType.Literal, Name = "False" };
					}
					else if (propertyType == typeof(Color))
					{
						foreach (var col in typeof(Colors).GetProperties(BindingFlags.Static | BindingFlags.Public).Where(r => r.PropertyType == typeof(Color)))
						{
							yield return new CompletionItem { Type = CompletionType.Literal, Name = col.Name };
						}
						yield return new CompletionItem { Type = CompletionType.Literal, Name = "#FFFFFF" };
						yield return new CompletionItem { Type = CompletionType.Literal, Name = "#FFFFFFFF" };
					}
					else if (propertyType.IsEnum)
					{
						foreach (var name in Enum.GetNames(propertyType))
						{
							yield return new CompletionItem
							{
								Type = CompletionType.Literal,
								Name = name,
								Description = XmlComments.GetEnum(prop, name)
							};
						}
					}
				}
			}
		}
	}
}
