using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Eto.DevExtension.LanguageServer.Lsp;
using Eto.Designer.Completion;

namespace Eto.DevExtension.LanguageServer
{
	/// <summary>Wires the LSP methods this server supports to the completion engine.</summary>
	public class XetoLanguageServer
	{
		readonly LspConnection connection;
		readonly DocumentStore documents = new DocumentStore();

		bool warnedAboutVersion;

		public XetoLanguageServer(LspConnection connection)
		{
			this.connection = connection;

			connection.OnRequest("initialize", Initialize);
			connection.OnRequest("shutdown", _ => null);
			connection.OnNotification("initialized", _ => { });
			connection.OnNotification("exit", _ => connection.Stop());
			connection.OnNotification("textDocument/didOpen", DidOpen);
			connection.OnNotification("textDocument/didChange", DidChange);
			connection.OnNotification("textDocument/didClose", DidClose);
			connection.OnRequest("textDocument/completion", Complete);
			connection.OnRequest("textDocument/hover", HoverAt);
		}

		object Initialize(JsonNode parameters)
		{
			var configured = parameters?["initializationOptions"]?["etoAssemblyPath"]?.GetValue<string>();
			if (!string.IsNullOrWhiteSpace(configured))
			{
				if (EtoAssemblyResolver.UseProjectPath(configured))
					connection.Log($"Using configured Eto assembly path: {configured}");
				else
					connection.Log($"Configured Eto assembly path not found: {configured}");
			}

			return new
			{
				capabilities = new
				{
					textDocumentSync = 1,
					hoverProvider = true,
					completionProvider = new
					{
						resolveProvider = false,
						triggerCharacters = new[] { "<", ".", " ", "\"", "'", ":", "{", ",", "=" }
					}
				},
				serverInfo = new { name = "Eto Designer Language Server", version = ThisVersion }
			};
		}

		static string ThisVersion =>
			typeof(XetoLanguageServer).Assembly.GetName().Version?.ToString() ?? "0.0.0";

		void DidOpen(JsonNode parameters)
		{
			var document = parameters?["textDocument"];
			var uri = document?["uri"]?.GetValue<string>();
			if (uri == null)
				return;
			documents.Set(uri, document["text"]?.GetValue<string>());
			UseEtoFor(uri);
		}

		void DidChange(JsonNode parameters)
		{
			var uri = parameters?["textDocument"]?["uri"]?.GetValue<string>();
			var changes = parameters?["contentChanges"] as JsonArray;
			if (uri == null || changes == null || changes.Count == 0)
				return;
			// full sync, so the last change carries the whole document
			documents.Set(uri, changes[changes.Count - 1]?["text"]?.GetValue<string>());
		}

		void DidClose(JsonNode parameters)
		{
			var uri = parameters?["textDocument"]?["uri"]?.GetValue<string>();
			if (uri != null)
			{
				documents.Remove(uri);
				RootTypeLocator.Forget(ToLocalPath(uri));
			}
		}

		object Complete(JsonNode parameters)
		{
			if (!TryGetContext(parameters, out var text, out var offset, out var format, out var rootType))
				return new CompletionList();
			return new CompletionList { Items = CompletionService.GetCompletions(text, offset, format, rootType) };
		}

		object HoverAt(JsonNode parameters)
		{
			if (!TryGetContext(parameters, out var text, out var offset, out var format, out var rootType))
				return null;
			return CompletionService.GetHover(text, offset, format, rootType);
		}

		bool TryGetContext(JsonNode parameters, out string text, out int offset, out Eto.Designer.Completion.CompletionFormat format, out string rootTypeName)
		{
			text = null;
			offset = 0;
			format = Eto.Designer.Completion.CompletionFormat.Xaml;
			rootTypeName = null;

			var uri = parameters?["textDocument"]?["uri"]?.GetValue<string>();
			if (uri == null)
				return false;
			text = documents.Get(uri);
			if (text == null)
				return false;

			var position = parameters["position"];
			if (position == null)
				return false;
			offset = DocumentStore.GetOffset(text, position["line"]?.GetValue<int>() ?? 0, position["character"]?.GetValue<int>() ?? 0);
			format = DocumentCompletion.GetFormat(uri);
			if (format == Eto.Designer.Completion.CompletionFormat.Json)
				rootTypeName = RootTypeLocator.Find(ToLocalPath(uri));
			return true;
		}

		/// <summary>
		/// Points Eto resolution at the project owning this document. Only the first document
		/// wins, since the assembly stays loaded for the life of the process.
		/// </summary>
		void UseEtoFor(string uri)
		{
			var path = ToLocalPath(uri);
			if (path == null)
				return;

			var found = EtoAssemblyLocator.Find(path, connection.Log);
			if (found == null)
			{
				if (EtoAssemblyResolver.ResolvedFrom == null)
					connection.Log($"No Eto reference found for {path}, using the version bundled with the extension.");
				return;
			}

			if (EtoAssemblyResolver.UseProjectPath(found) || warnedAboutVersion)
				return;

			warnedAboutVersion = true;
			connection.Notify("window/showMessageRequest", new
			{
				type = 2,
				message = $"{Path.GetFileName(path)} references a different Eto.Forms than the one already loaded ({EtoAssemblyResolver.ResolvedFrom}). Restart the Eto language server to pick it up."
			});
		}

		static string ToLocalPath(string uri)
		{
			try
			{
				var parsed = new Uri(uri);
				return parsed.IsFile ? parsed.LocalPath : null;
			}
			catch (UriFormatException)
			{
				return null;
			}
		}
	}
}
