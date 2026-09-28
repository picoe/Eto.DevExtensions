using Eto.Designer;
using Eto.Drawing;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Eto.DevExtension.VisualStudio.Windows.Editor
{
	/// <summary>
	/// The .NET preview host that draws designer files, shared by the open designers of one project.
	/// </summary>
	/// <remarks>
	/// Previews are drawn in a separate process so the project's own code never loads into Visual Studio.
	/// </remarks>
	sealed class PreviewHostClient
	{
		// long enough for a first render that compiles code, short enough to recover from a hung control
		static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(30);

		static readonly Dictionary<string, PreviewHostClient> hosts = new Dictionary<string, PreviewHostClient>(StringComparer.OrdinalIgnoreCase);
		// platforms whose host couldn't start, usually for want of a runtime, so don't keep trying
		static readonly HashSet<string> unavailable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		readonly string key;
		readonly SemaphoreSlim requestLock = new SemaphoreSlim(1, 1);
		int references;
		Process process;
		JsonRpc rpc;
		PreviewPlatform platform;

		/// <summary>Raised on the UI thread after the project is rebuilt, so previews can be redrawn.</summary>
		public event EventHandler ProjectChanged;

		PreviewHostClient(string key) => this.key = key;

		bool IsRunning => rpc != null && process != null && !process.HasExited;

		/// <param name="key">Identifies the project, so its designers share one host.</param>
		public static PreviewHostClient Acquire(string key)
		{
			key = key ?? string.Empty;
			lock (hosts)
			{
				if (!hosts.TryGetValue(key, out var host))
					hosts[key] = host = new PreviewHostClient(key);
				host.references++;
				return host;
			}
		}

		public void Release()
		{
			lock (hosts)
			{
				if (--references > 0)
					return;
				hosts.Remove(key);
			}
			Stop();
		}

		/// <param name="assemblies">Project assembly files, the project's own first, or null to let the host find them.</param>
		public async Task<PreviewRenderResult> RenderAsync(PreviewRenderRequest request, IList<string> assemblies)
		{
			await requestLock.WaitAsync();
			try
			{
				// a second try covers a host that exited after a rebuild, or that serves an older build
				var wanted = await PreviewPlatforms.ResolveAsync();
				for (var attempt = 0; attempt < 2; attempt++)
				{
					var server = await GetServerAsync(wanted);
					if (server == null)
						return Error(wanted.Requirement, null, wanted);

					try
					{
						using (var timeout = new CancellationTokenSource(RenderTimeout))
						{
							var result = await server.InvokeWithParameterObjectAsync<JToken>(
								"preview/render",
								new
								{
									fileName = request.FileName,
									text = request.Text,
									assemblies,
									width = request.Size?.Width,
									height = request.Size?.Height,
									scale = request.Scale
								},
								timeout.Token);

							if ((bool?)result?["restartRequired"] == true)
							{
								Stop();
								continue;
							}
							return Read(result, wanted);
						}
					}
					catch (OperationCanceledException)
					{
						Stop();
						return Error("The preview took too long to draw, so it was stopped.", null, wanted);
					}
					catch (Exception ex) when (ex is ConnectionLostException || ex is ObjectDisposedException || ex is IOException)
					{
						Stop();
						if (attempt > 0)
							return Error("The preview host stopped unexpectedly.", ex.ToString(), wanted);
					}
				}
				return Error("The preview host could not load the project.", null, wanted);
			}
			finally
			{
				requestLock.Release();
			}
		}

		static PreviewRenderResult Read(JToken result, PreviewPlatform platform)
		{
			var error = result?["error"];
			if (error != null && error.Type == JTokenType.Object)
				return Error((string)error["message"], (string)error["details"], platform);

			var image = (string)result?["image"];
			return new PreviewRenderResult
			{
				Image = string.IsNullOrEmpty(image) ? null : Convert.FromBase64String(image),
				Size = new Size((int?)result?["width"] ?? 0, (int?)result?["height"] ?? 0),
				Platform = platform.Label
			};
		}

		static PreviewRenderResult Error(string message, string details, PreviewPlatform platform) =>
			new PreviewRenderResult { Error = new DesignError { Message = message, Details = details ?? message }, Platform = platform.Label };

		async Task<JsonRpc> GetServerAsync(PreviewPlatform wanted)
		{
			if (IsRunning && platform?.Id == wanted.Id)
				return rpc;
			if (unavailable.Contains(wanted.Id))
				return null;

			// clean up after a host that exited, such as after a rebuild, or one drawing with another platform
			Stop();

			if (!File.Exists(wanted.HostPath))
			{
				unavailable.Add(wanted.Id);
				Debug.WriteLine($"Eto preview host not found at {wanted.HostPath}");
				return null;
			}

			try
			{
				var start = new ProcessStartInfo("dotnet", $"\"{wanted.HostPath}\" --platform {wanted.Id}")
				{
					UseShellExecute = false,
					CreateNoWindow = true,
					RedirectStandardInput = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true
				};
				if (wanted.LibraryPath != null)
					start.EnvironmentVariables["PATH"] = wanted.LibraryPath + Path.PathSeparator + start.EnvironmentVariables["PATH"];
				process = Process.Start(start);
				platform = wanted;
				process.ErrorDataReceived += (sender, e) => Debug.WriteLine(e.Data);
				process.BeginErrorReadLine();

				rpc = new JsonRpc(new HeaderDelimitedMessageHandler(process.StandardInput.BaseStream, process.StandardOutput.BaseStream));
				rpc.AddLocalRpcMethod("preview/restart", new Action(OnRestart));
				rpc.AddLocalRpcMethod("window/logMessage", new Action<int, string>((type, message) => Debug.WriteLine($"Eto preview: {message}")));
				rpc.StartListening();
				await rpc.InvokeWithParameterObjectAsync<JToken>("initialize", new { processId = Process.GetCurrentProcess().Id, theme = GetTheme() });
				await rpc.NotifyWithParameterObjectAsync("initialized", new { });
				return rpc;
			}
			catch (Exception ex)
			{
				unavailable.Add(wanted.Id);
				Debug.WriteLine($"Could not start the Eto preview host: {ex}");
				Stop();
				return null;
			}
		}

		void OnRestart()
		{
			// the host exits right after this, and the next render starts a fresh one
			Eto.Forms.Application.Instance.AsyncInvoke(() => ProjectChanged?.Invoke(this, EventArgs.Empty));
		}

		static object GetTheme()
		{
			var theme = Global.Theme;
			return new
			{
				designerPanel = theme.DesignerPanel.ToHex(),
				designerBackground = theme.DesignerBackground.ToHex(),
				projectBackground = theme.ProjectBackground.ToHex(),
				projectForeground = theme.ProjectForeground.ToHex()
			};
		}

		void Stop()
		{
			var oldRpc = rpc;
			var oldProcess = process;
			rpc = null;
			process = null;
			platform = null;
			try
			{
				oldRpc?.Dispose();
				if (oldProcess != null && !oldProcess.HasExited)
					oldProcess.Kill();
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"Could not stop the Eto preview host: {ex.Message}");
			}
			oldProcess?.Dispose();
		}
	}
}
