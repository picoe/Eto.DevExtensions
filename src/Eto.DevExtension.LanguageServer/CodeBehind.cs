using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Eto.DevExtension.LanguageServer
{
	/// <summary>
	/// The C# class a .xeto/.jeto file loads into, for listing and adding its event handlers.
	/// </summary>
	public class CodeBehind
	{
		static readonly Regex xamlClassReg = new Regex(@"\b[\w.-]+:Class\s*=\s*(?:""(?<name>[^""]*)""|'(?<name>[^']*)')", RegexOptions.Compiled);

		static readonly Dictionary<Type, string> keywords = new Dictionary<Type, string>
		{
			{ typeof(object), "object" }, { typeof(string), "string" }, { typeof(bool), "bool" },
			{ typeof(byte), "byte" }, { typeof(sbyte), "sbyte" }, { typeof(char), "char" },
			{ typeof(short), "short" }, { typeof(ushort), "ushort" }, { typeof(int), "int" },
			{ typeof(uint), "uint" }, { typeof(long), "long" }, { typeof(ulong), "ulong" },
			{ typeof(float), "float" }, { typeof(double), "double" }, { typeof(decimal), "decimal" }
		};

		readonly string text;
		readonly ClassDeclarationSyntax declaration;

		public string FilePath { get; }

		CodeBehind(string filePath, string text, ClassDeclarationSyntax declaration)
		{
			FilePath = filePath;
			this.text = text;
			this.declaration = declaration;
		}

		/// <summary>Reads the code behind of a designer file, eg. MyForm.xeto.cs or MyForm.cs for MyForm.xeto.</summary>
		/// <param name="documentText">Contents of the designer file, which may name its class with x:Class.</param>
		/// <returns>The code behind, or null when there isn't a C# one with a matching class.</returns>
		public static CodeBehind Load(string documentPath, string documentText)
		{
			if (string.IsNullOrEmpty(documentPath))
				return null;

			var className = Path.GetFileNameWithoutExtension(documentPath);
			var xamlClass = documentText == null ? null : xamlClassReg.Match(documentText);
			if (xamlClass?.Success == true)
				className = xamlClass.Groups["name"].Value.Split('.').Last().Trim();

			var directory = Path.GetDirectoryName(documentPath) ?? string.Empty;
			var candidates = new[]
			{
				documentPath + ".cs",
				Path.Combine(directory, Path.GetFileNameWithoutExtension(documentPath) + ".cs"),
				Path.Combine(directory, className + ".cs")
			};
			foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
			{
				var codeBehind = Read(candidate, className);
				if (codeBehind != null)
					return codeBehind;
			}
			return null;
		}

		static CodeBehind Read(string path, string className)
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

			var root = CSharpSyntaxTree.ParseText(source).GetRoot();
			var declaration = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(r => r.Identifier.ValueText == className);
			return declaration == null || declaration.CloseBraceToken.IsMissing ? null : new CodeBehind(path, source, declaration);
		}

		/// <summary>Names of the methods in the class that could handle <paramref name="evt"/>.</summary>
		public IEnumerable<string> GetHandlers(EventInfo evt)
		{
			var count = evt.EventHandlerType?.GetMethod("Invoke")?.GetParameters().Length ?? -1;
			return declaration.Members.OfType<MethodDeclarationSyntax>()
				.Where(r => r.ParameterList.Parameters.Count == count && r.ReturnType is PredefinedTypeSyntax returnType && returnType.Keyword.IsKind(SyntaxKind.VoidKeyword))
				.Select(r => r.Identifier.ValueText)
				.Distinct();
		}

		/// <summary>Offset of the name of the method <paramref name="name"/>, or null when the class has none.</summary>
		public int? FindMethod(string name) =>
			declaration.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(r => r.Identifier.ValueText == name)?.Identifier.SpanStart;

		/// <summary>Text to insert into the class for a new handler of <paramref name="evt"/>.</summary>
		/// <returns>The edit, or null when the event's signature can't be written.</returns>
		public (int Offset, string Text)? AddHandler(string name, EventInfo evt)
		{
			var parameters = evt.EventHandlerType?.GetMethod("Invoke")?.GetParameters();
			if (parameters == null)
				return null;

			var namespaces = GetImportedNamespaces();
			var signature = string.Join(", ", parameters.Select((p, i) => GetTypeName(p.ParameterType, namespaces) + " " + GetParameterName(p, i)));

			var newLine = text.Contains("\r\n") ? "\r\n" : "\n";
			var closeBrace = declaration.CloseBraceToken.SpanStart;
			var lineStart = text.LastIndexOf('\n', Math.Max(closeBrace - 1, 0)) + 1;
			var closeIndent = text.Substring(lineStart, closeBrace - lineStart);
			var braceOnOwnLine = closeIndent.All(r => r == ' ' || r == '\t');
			if (!braceOnOwnLine)
				closeIndent = GetLineIndent(declaration.SpanStart);
			var indent = declaration.Members.Count > 0 ? GetLineIndent(declaration.Members[0].SpanStart) : closeIndent + (text.Contains("\n\t") ? "\t" : "    ");

			var method = new StringBuilder();
			if (!braceOnOwnLine)
				method.Append(newLine);
			else if (declaration.Members.Count > 0)
				method.Append(newLine);
			method.Append(indent).Append("protected void ").Append(name).Append('(').Append(signature).Append(')').Append(newLine);
			method.Append(indent).Append('{').Append(newLine);
			method.Append(indent).Append('}').Append(newLine);
			if (!braceOnOwnLine)
				method.Append(closeIndent);

			return (braceOnOwnLine ? lineStart : closeBrace, method.ToString());
		}

		/// <summary>Converts an offset in the file to an LSP line and character.</summary>
		public (int Line, int Character) GetPosition(int offset) => DocumentStore.GetPosition(text, offset);

		string GetLineIndent(int offset)
		{
			var lineStart = text.LastIndexOf('\n', Math.Max(offset - 1, 0)) + 1;
			var end = lineStart;
			while (end < text.Length && (text[end] == ' ' || text[end] == '\t'))
				end++;
			return text.Substring(lineStart, end - lineStart);
		}

		HashSet<string> GetImportedNamespaces()
		{
			var namespaces = new HashSet<string>(StringComparer.Ordinal);
			foreach (var node in declaration.Ancestors())
			{
				SyntaxList<UsingDirectiveSyntax> usings;
				if (node is CompilationUnitSyntax unit)
					usings = unit.Usings;
				else if (node is BaseNamespaceDeclarationSyntax ns)
				{
					usings = ns.Usings;
					// code inside a namespace sees the types of it and its parents
					var name = ns.Name.ToString();
					while (!string.IsNullOrEmpty(name))
					{
						namespaces.Add(name);
						var dot = name.LastIndexOf('.');
						name = dot < 0 ? null : name.Substring(0, dot);
					}
				}
				else
					continue;

				foreach (var directive in usings.Where(r => r.Alias == null && r.StaticKeyword.IsKind(SyntaxKind.None)))
					namespaces.Add(directive.Name.ToString());
			}
			return namespaces;
		}

		static string GetParameterName(ParameterInfo parameter, int index) =>
			string.IsNullOrEmpty(parameter.Name) ? "arg" + index : SyntaxFacts.GetKeywordKind(parameter.Name) != SyntaxKind.None ? "@" + parameter.Name : parameter.Name;

		static string GetTypeName(Type type, HashSet<string> namespaces)
		{
			if (keywords.TryGetValue(type, out var keyword))
				return keyword;
			if (type.IsByRef)
				return GetTypeName(type.GetElementType(), namespaces);
			if (type.IsArray)
				return GetTypeName(type.GetElementType(), namespaces) + "[" + new string(',', type.GetArrayRank() - 1) + "]";

			var nullable = Nullable.GetUnderlyingType(type);
			if (nullable != null)
				return GetTypeName(nullable, namespaces) + "?";

			var name = type.Name;
			var tick = name.IndexOf('`');
			if (tick >= 0)
				name = name.Substring(0, tick);

			var genericArguments = type.IsGenericType ? type.GetGenericArguments() : Type.EmptyTypes;
			if (type.IsNested)
			{
				// nested generic types carry their parent's arguments first
				var parent = type.DeclaringType;
				var parentArguments = parent.IsGenericTypeDefinition && genericArguments.Length > 0
					? parent.MakeGenericType(genericArguments.Take(parent.GetGenericArguments().Length).ToArray())
					: parent;
				genericArguments = genericArguments.Skip(parent.GetGenericArguments().Length).ToArray();
				name = GetTypeName(parentArguments, namespaces) + "." + name;
			}
			else if (!string.IsNullOrEmpty(type.Namespace) && !namespaces.Contains(type.Namespace))
				name = type.Namespace + "." + name;

			if (genericArguments.Length > 0)
				name += "<" + string.Join(", ", genericArguments.Select(r => GetTypeName(r, namespaces))) + ">";
			return name;
		}
	}
}
