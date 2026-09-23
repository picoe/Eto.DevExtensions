using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.RegularExpressions;

namespace Eto.DevExtension.LanguageServer
{
	/// <summary>
	/// Finds the Eto type a designer file loads into, by reading the base class of its code behind.
	/// A .jeto root object usually has no $type, so this is the only thing that names it.
	/// </summary>
	public static class RootTypeLocator
	{
		static readonly ConcurrentDictionary<string, string> cache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		static readonly string[] Extensions = { ".cs", ".vb", ".fs" };

		public static void Forget(string documentPath)
		{
			if (documentPath != null)
				cache.TryRemove(documentPath, out _);
		}

		/// <summary>Base type name of the code behind class, or null when there isn't one.</summary>
		public static string Find(string documentPath)
		{
			if (string.IsNullOrEmpty(documentPath))
				return null;
			return cache.GetOrAdd(documentPath, Resolve);
		}

		static string Resolve(string documentPath)
		{
			// MyPanel.jeto pairs with either MyPanel.jeto.cs or MyPanel.cs
			var directory = Path.GetDirectoryName(documentPath);
			var fileName = Path.GetFileName(documentPath);
			var className = Path.GetFileNameWithoutExtension(fileName);

			foreach (var extension in Extensions)
			{
				foreach (var candidate in new[] { documentPath + extension, Path.Combine(directory ?? string.Empty, className + extension) })
				{
					var baseType = ReadBaseType(candidate, className);
					if (baseType != null)
						return baseType;
				}
			}
			return null;
		}

		static string ReadBaseType(string path, string className)
		{
			if (!File.Exists(path))
				return null;

			string source;
			try
			{
				source = File.ReadAllText(path);
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
			{
				return null;
			}

			var name = Regex.Escape(className);
			var csharp = Regex.Match(source, @"\bclass\s+" + name + @"\s*(<[^>]*>)?\s*:\s*(?<base>[A-Za-z_][\w.]*)");
			if (csharp.Success)
				return LastSegment(csharp.Groups["base"].Value);

			var basic = Regex.Match(source, @"\bClass\s+" + name + @"\b\s*\r?\n\s*Inherits\s+(?<base>[A-Za-z_][\w.]*)", RegexOptions.IgnoreCase);
			if (basic.Success)
				return LastSegment(basic.Groups["base"].Value);

			var fsharp = Regex.Match(source, @"\btype\s+" + name + @"\s*\([^)]*\)\s*(as\s+\w+\s*)?=\s*inherit\s+(?<base>[A-Za-z_][\w.]*)");
			return fsharp.Success ? LastSegment(fsharp.Groups["base"].Value) : null;
		}

		static string LastSegment(string typeName)
		{
			var dot = typeName.LastIndexOf('.');
			return dot < 0 ? typeName : typeName.Substring(dot + 1);
		}
	}
}
