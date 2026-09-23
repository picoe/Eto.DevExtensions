using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Eto.DevExtension.LanguageServer.Lsp
{
	/// <summary>
	/// Minimal LSP transport over a pair of streams. Register handlers, then call <see cref="Run"/>.
	/// </summary>
	public class LspConnection
	{
		public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
		};

		readonly Stream input;
		readonly Stream output;
		readonly object writeLock = new object();
		readonly Dictionary<string, Func<JsonNode, object>> requests = new Dictionary<string, Func<JsonNode, object>>();
		readonly Dictionary<string, Action<JsonNode>> notifications = new Dictionary<string, Action<JsonNode>>();

		bool running = true;

		public LspConnection(Stream input, Stream output)
		{
			this.input = input;
			this.output = output;
		}

		public void OnRequest(string method, Func<JsonNode, object> handler) => requests[method] = handler;

		public void OnNotification(string method, Action<JsonNode> handler) => notifications[method] = handler;

		public void Stop() => running = false;

		public void Run()
		{
			while (running)
			{
				var message = ReadMessage();
				if (message == null)
					break;

				JsonNode node;
				try
				{
					node = JsonNode.Parse(message);
				}
				catch (JsonException)
				{
					continue;
				}

				var method = node?["method"]?.GetValue<string>();
				if (method == null)
					continue;
				var id = node["id"];
				var parameters = node["params"];

				if (id != null)
				{
					if (requests.TryGetValue(method, out var handler))
					{
						try
						{
							Respond(id, handler(parameters));
						}
						catch (Exception ex)
						{
							RespondError(id, ex);
						}
					}
					else
					{
						// -32601 MethodNotFound
						WriteMessage(new JsonObject
						{
							["jsonrpc"] = "2.0",
							["id"] = id.DeepClone(),
							["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Unhandled method " + method }
						});
					}
				}
				else if (notifications.TryGetValue(method, out var handler))
				{
					try
					{
						handler(parameters);
					}
					catch (Exception ex)
					{
						Log("Error handling " + method + ": " + ex);
					}
				}
			}
		}

		public void Notify(string method, object parameters)
		{
			WriteMessage(new JsonObject
			{
				["jsonrpc"] = "2.0",
				["method"] = method,
				["params"] = ToNode(parameters)
			});
		}

		/// <summary>Sends a message to the client's output channel.</summary>
		public void Log(string message) => Notify("window/logMessage", new { type = 3, message });

		void Respond(JsonNode id, object result)
		{
			WriteMessage(new JsonObject
			{
				["jsonrpc"] = "2.0",
				["id"] = id.DeepClone(),
				["result"] = ToNode(result)
			});
		}

		void RespondError(JsonNode id, Exception ex)
		{
			WriteMessage(new JsonObject
			{
				["jsonrpc"] = "2.0",
				["id"] = id.DeepClone(),
				// -32603 InternalError
				["error"] = new JsonObject { ["code"] = -32603, ["message"] = ex.Message, ["data"] = ex.ToString() }
			});
		}

		static JsonNode ToNode(object value) =>
			value == null ? null : JsonSerializer.SerializeToNode(value, value.GetType(), JsonOptions);

		string ReadMessage()
		{
			var length = -1;
			var header = new StringBuilder();
			while (true)
			{
				var ch = input.ReadByte();
				if (ch < 0)
					return null;
				if (ch != '\n')
				{
					if (ch != '\r')
						header.Append((char)ch);
					continue;
				}

				var line = header.ToString();
				header.Clear();
				if (line.Length == 0)
					break;
				if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
					int.TryParse(line.Substring("Content-Length:".Length).Trim(), out length);
			}

			if (length < 0)
				return null;

			var buffer = new byte[length];
			var read = 0;
			while (read < length)
			{
				var count = input.Read(buffer, read, length - read);
				if (count <= 0)
					return null;
				read += count;
			}
			return Encoding.UTF8.GetString(buffer);
		}

		void WriteMessage(JsonNode message)
		{
			var body = Encoding.UTF8.GetBytes(message.ToJsonString(JsonOptions));
			var header = Encoding.ASCII.GetBytes("Content-Length: " + body.Length + "\r\n\r\n");
			lock (writeLock)
			{
				output.Write(header, 0, header.Length);
				output.Write(body, 0, body.Length);
				output.Flush();
			}
		}
	}
}
