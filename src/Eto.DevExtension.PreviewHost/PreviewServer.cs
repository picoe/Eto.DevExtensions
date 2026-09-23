using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Eto.DevExtension.LanguageServer;
using Eto.DevExtension.LanguageServer.Lsp;

namespace Eto.DevExtension.PreviewHost
{
	/// <summary>
	/// Handles the preview protocol. Eto only starts on the first render, once it's known which project
	/// (and so which Eto) this process is for.
	/// </summary>
	class PreviewServer
	{
		readonly LspConnection connection;
		readonly ManualResetEventSlim startUI = new ManualResetEventSlim();
		readonly TaskCompletionSource<bool> uiReady = new TaskCompletionSource<bool>();
		Dictionary<string, string> theme = new Dictionary<string, string>();
		bool configured;
		object renderer;

		public PreviewServer(LspConnection connection)
		{
			this.connection = connection;
			connection.OnRequest("initialize", Initialize);
			connection.OnRequest("shutdown", _ => null);
			connection.OnNotification("initialized", _ => { });
			connection.OnNotification("exit", _ => Environment.Exit(0));
			connection.OnRequest("preview/render", Render);
		}

		object Initialize(JsonNode parameters)
		{
			if (parameters?["theme"] is JsonObject colors)
				theme = colors.Where(r => r.Value != null).ToDictionary(r => r.Key, r => r.Value.GetValue<string>(), StringComparer.OrdinalIgnoreCase);
			return new { capabilities = new { }, serverInfo = new { name = "Eto Preview Host" } };
		}

		object Render(JsonNode parameters)
		{
			var request = new RenderRequest
			{
				FileName = parameters?["fileName"]?.GetValue<string>(),
				Text = parameters?["text"]?.GetValue<string>() ?? string.Empty,
				Width = parameters?["width"]?.GetValue<int?>(),
				Height = parameters?["height"]?.GetValue<int?>(),
				Scale = parameters?["scale"]?.GetValue<double?>() ?? 1
			};
			var assemblies = (parameters?["assemblies"] as JsonArray)?.Select(r => r?.GetValue<string>()).Where(r => !string.IsNullOrEmpty(r)).ToList()
				?? ProjectAssemblyLocator.Find(request.FileName, connection.Log);

			if (!configured)
			{
				ProjectAssemblies.Configure(assemblies, request.FileName, connection.Log);
				configured = true;
				startUI.Set();
				uiReady.Task.Wait();
			}
			else if (ProjectAssemblies.GetKey(assemblies) != ProjectAssemblies.Key)
				return new RenderResult { RestartRequired = true };

			return RenderOnUI(request);
		}

		/// <summary>Runs the UI on the calling thread, which must be the process's main STA thread.</summary>
		public void RunUI()
		{
			startUI.Wait();
			StartEto();
		}

		// kept apart so no Eto type is touched before the project's Eto is chosen
		[MethodImpl(MethodImplOptions.NoInlining)]
		void StartEto()
		{
			var platform = new Eto.Wpf.Platform();
			platform.Add<Eto.Designer.IPlatformTheme>(() => new HostTheme(theme));
			Eto.Designer.Builders.BaseCompiledInterfaceBuilder.EtoAssemblyPath = ProjectAssemblies.EtoFile;

			var app = new Eto.Forms.Application(platform);
			app.Initialized += (sender, e) =>
			{
				// the offscreen forms come and go, and must not end the process
				System.Windows.Application.Current.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
				ProjectAssemblies.LoadProject();
				ProjectAssemblies.WatchForChanges(Restart);
				renderer = new PreviewRenderer();
				uiReady.TrySetResult(true);
			};
			app.UnhandledException += (sender, e) => connection.Log($"Unhandled exception: {e.ExceptionObject}");
			app.Run();
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		RenderResult RenderOnUI(RenderRequest request)
		{
			var result = Eto.Forms.Application.Instance.Invoke(() => ((PreviewRenderer)renderer).RenderAsync(request));
			return result.GetAwaiter().GetResult();
		}

		void Restart()
		{
			connection.Log("Project assemblies changed, restarting the preview host.");
			connection.Notify("preview/restart", new { });
			Environment.Exit(0);
		}
	}
}
