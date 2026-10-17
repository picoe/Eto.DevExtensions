using Eto.Designer.Completion;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Eto.DevExtension.VisualStudio.Intellisense
{
	/// <summary>
	/// The .NET language server shared with VS Code, started on first use.
	/// </summary>
	/// <remarks>
	/// Completions come from here so the project's own assemblies are never loaded into Visual Studio.
	/// </remarks>
	static class EtoLanguageServer
	{
		const string ServerDll = "Eto.DevExtension.LanguageServer.dll";

		static readonly SemaphoreSlim startLock = new SemaphoreSlim(1, 1);
		// the server handles one message at a time, and the text sent must match the request that follows it
		static readonly SemaphoreSlim requestLock = new SemaphoreSlim(1, 1);
		static readonly ConcurrentDictionary<string, int> versions = new ConcurrentDictionary<string, int>();
		static readonly ConcurrentDictionary<string, string> sentAssemblies = new ConcurrentDictionary<string, string>();

		static Process process;
		static JsonRpc rpc;
		static bool unavailable;

		static bool IsRunning => rpc != null && process != null && !process.HasExited;

		/// <summary>Makes the server re-read the document's code behind, as when a file is reopened.</summary>
		public static void Reset(string filePath)
		{
			var uri = ToUri(filePath);
			if (uri == null || !versions.TryRemove(uri, out _) || !IsRunning)
				return;
			sentAssemblies.TryRemove(uri, out _);
			_ = rpc.NotifyWithParameterObjectAsync("textDocument/didClose", new { textDocument = new { uri } });
		}

		/// <param name="assemblies">Project assembly files, the document's own first, or null to let the server find them.</param>
		/// <returns>The completions, or null when the server can't be used.</returns>
		public static async Task<List<DocumentCompletionItem>> GetCompletionsAsync(string filePath, string text, int offset, IList<string> assemblies, CancellationToken token)
		{
			var uri = ToUri(filePath);
			if (uri == null)
				return null;

			var server = await GetServerAsync();
			if (server == null)
				return null;

			await requestLock.WaitAsync(token);
			try
			{
				await SyncAsync(server, uri, text, assemblies);

				var position = GetPosition(text, offset);
				var result = await server.InvokeWithParameterObjectAsync<JToken>(
					"textDocument/completion",
					new { textDocument = new { uri }, position = new { line = position.Line, character = position.Character } },
					token);
				return ReadItems(result, text);
			}
			catch (Exception ex) when (!(ex is OperationCanceledException))
			{
				Debug.WriteLine($"Eto language server request failed: {ex}");
				return null;
			}
			finally
			{
				requestLock.Release();
			}
		}

		/// <summary>Where the event handler named at <paramref name="offset"/> is in the code behind.</summary>
		/// <returns>
		/// The file and zero-based position, with a null path when there's no handler. IsEvent is true
		/// when the offset is in an event's value, even when its handler wasn't found.
		/// </returns>
		public static async Task<(bool IsEvent, string FilePath, int Line, int Character)> GetDefinitionAsync(string filePath, string text, int offset, IList<string> assemblies, CancellationToken token)
		{
			var uri = ToUri(filePath);
			var server = uri == null ? null : await GetServerAsync();
			if (server == null)
				return default;

			await requestLock.WaitAsync(token);
			try
			{
				await SyncAsync(server, uri, text, assemblies);

				var position = GetPosition(text, offset);
				var result = await server.InvokeWithParameterObjectAsync<JToken>(
					"textDocument/definition",
					new { textDocument = new { uri }, position = new { line = position.Line, character = position.Character } },
					token);
				var location = (result as JArray)?.FirstOrDefault() ?? (result as JObject);
				var start = location?["range"]?["start"];
				if (start == null || !Uri.TryCreate((string)location["uri"], UriKind.Absolute, out var target) || !target.IsFile)
					return (result is JArray, null, 0, 0);
				return (true, target.LocalPath, (int?)start["line"] ?? 0, (int?)start["character"] ?? 0);
			}
			catch (Exception ex) when (!(ex is OperationCanceledException))
			{
				Debug.WriteLine($"Eto language server request failed: {ex}");
				return default;
			}
			finally
			{
				requestLock.Release();
			}
		}

		/// <summary>Sends the document's text, and its project assemblies when they've changed.</summary>
		static async Task SyncAsync(JsonRpc server, string uri, string text, IList<string> assemblies)
		{
			var version = versions.AddOrUpdate(uri, 1, (key, value) => value + 1);
			if (version == 1)
				await server.NotifyWithParameterObjectAsync("textDocument/didOpen", new { textDocument = new { uri, languageId = "eto", version, text } });
			else
				await server.NotifyWithParameterObjectAsync("textDocument/didChange", new { textDocument = new { uri, version }, contentChanges = new[] { new { text } } });

			var assemblyKey = assemblies == null ? string.Empty : string.Join("|", assemblies);
			if (sentAssemblies.TryGetValue(uri, out var sent) ? sent != assemblyKey : assemblies != null)
			{
				await server.NotifyWithParameterObjectAsync("eto/setProjectAssemblies", new { textDocument = new { uri }, assemblies });
				sentAssemblies[uri] = assemblyKey;
			}
		}

		static async Task<JsonRpc> GetServerAsync()
		{
			if (IsRunning)
				return rpc;
			if (unavailable)
				return null;

			await startLock.WaitAsync();
			try
			{
				if (IsRunning)
					return rpc;

				var serverPath = Path.Combine(Path.GetDirectoryName(typeof(EtoLanguageServer).Assembly.Location), "server", ServerDll);
				if (!File.Exists(serverPath))
				{
					unavailable = true;
					Debug.WriteLine($"Eto language server not found at {serverPath}");
					return null;
				}

				var start = new ProcessStartInfo("dotnet", "\"" + serverPath + "\"")
				{
					UseShellExecute = false,
					CreateNoWindow = true,
					RedirectStandardInput = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true
				};
				process = Process.Start(start);
				process.ErrorDataReceived += (sender, e) => Debug.WriteLine(e.Data);
				process.BeginErrorReadLine();

				rpc = new JsonRpc(new HeaderDelimitedMessageHandler(process.StandardInput.BaseStream, process.StandardOutput.BaseStream));
				rpc.StartListening();
				await rpc.InvokeWithParameterObjectAsync<JToken>("initialize", new { processId = Process.GetCurrentProcess().Id, capabilities = new { } });
				await rpc.NotifyWithParameterObjectAsync("initialized", new { });

				// a new process knows nothing of what was sent to the last one
				versions.Clear();
				sentAssemblies.Clear();
				return rpc;
			}
			catch (Exception ex)
			{
				// usually no .NET runtime to run it with, so don't keep trying
				unavailable = true;
				Debug.WriteLine($"Could not start the Eto language server: {ex}");
				return null;
			}
			finally
			{
				startLock.Release();
			}
		}

		static List<DocumentCompletionItem> ReadItems(JToken result, string text)
		{
			var items = new List<DocumentCompletionItem>();
			var list = result?["items"] as JArray ?? result as JArray;
			if (list == null)
				return items;

			foreach (var entry in list)
			{
				var label = (string)entry["label"];
				if (label == null)
					continue;

				DocumentTextEdit namespaceEdit = null;
				var extra = (entry["additionalTextEdits"] as JArray)?.FirstOrDefault();
				if (extra != null)
				{
					var start = extra["range"]?["start"];
					namespaceEdit = new DocumentTextEdit
					{
						Offset = GetOffset(text, (int?)start?["line"] ?? 0, (int?)start?["character"] ?? 0),
						Text = (string)extra["newText"]
					};
				}

				items.Add(new DocumentCompletionItem
				{
					Label = label,
					InsertText = (string)entry["textEdit"]?["newText"] ?? label,
					ValueText = (string)entry["data"]?["valueText"],
					ValueReplaces = (int?)entry["data"]?["valueReplaces"] ?? 0,
					NamespaceEdit = namespaceEdit,
					CodeBehindEdit = ReadCodeBehindEdit(entry["command"]),
					Item = new Designer.Completion.CompletionItem
					{
						Name = label,
						Type = GetType((int?)entry["kind"] ?? 0),
						Suffix = (string)entry["detail"],
						// markdown from the server, which the completion tooltip shows as plain text
						Description = ((string)entry["documentation"]?["value"])?.Replace("`", string.Empty),
						Behavior = label.EndsWith(".") ? CompletionBehavior.ChildProperty : CompletionBehavior.None
					}
				});
			}
			return items;
		}

		/// <summary>The code behind insert of a "create event handler" command, which VS applies itself on commit.</summary>
		static DocumentFileEdit ReadCodeBehindEdit(JToken command)
		{
			if ((string)command?["command"] != "eto.createEventHandler")
				return null;
			var change = (command["arguments"]?.FirstOrDefault()?["changes"] as JObject)?.Properties().FirstOrDefault();
			var edit = (change?.Value as JArray)?.FirstOrDefault();
			if (edit == null || !Uri.TryCreate(change.Name, UriKind.Absolute, out var uri) || !uri.IsFile)
				return null;
			var start = edit["range"]?["start"];
			return new DocumentFileEdit
			{
				FilePath = uri.LocalPath,
				Line = (int?)start?["line"] ?? 0,
				Character = (int?)start?["character"] ?? 0,
				Text = (string)edit["newText"]
			};
		}

		// LSP CompletionItemKind values the server sends
		static CompletionType GetType(int kind)
		{
			switch (kind)
			{
				case 7: return CompletionType.Class;
				case 10: return CompletionType.Property;
				case 23: return CompletionType.Event;
				case 5: return CompletionType.Field;
				case 2: return CompletionType.Method;
				default: return CompletionType.Literal;
			}
		}

		static string ToUri(string filePath)
		{
			if (string.IsNullOrEmpty(filePath) || !Path.IsPathRooted(filePath))
				return null;
			return new Uri(filePath).AbsoluteUri;
		}

		static (int Line, int Character) GetPosition(string text, int offset)
		{
			offset = Math.Min(Math.Max(offset, 0), text.Length);
			var lineStart = offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;
			var line = 0;
			for (var i = 0; i < lineStart; i++)
			{
				if (text[i] == '\n')
					line++;
			}
			return (line, offset - lineStart);
		}

		internal static int GetOffset(string text, int line, int character)
		{
			var offset = 0;
			for (var i = 0; i < line; i++)
			{
				var next = text.IndexOf('\n', offset);
				if (next < 0)
					return text.Length;
				offset = next + 1;
			}
			return Math.Min(offset + character, text.Length);
		}
	}
}
