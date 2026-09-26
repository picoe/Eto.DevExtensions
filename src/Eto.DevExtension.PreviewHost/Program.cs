using System;
using System.IO;
using System.Threading;
using Eto.DevExtension.LanguageServer.Lsp;

namespace Eto.DevExtension.PreviewHost
{
	static class Program
	{
		/// <summary>Pass --platform Wpf|Gtk|Mac64|macOS to pick the Eto platform, otherwise the OS's default is used.</summary>
		[STAThread]
		static int Main(string[] args)
		{
			var index = Array.FindIndex(args, r => r == "--platform");
			var platform = PreviewPlatform.Get(index >= 0 && index + 1 < args.Length ? args[index + 1] : null);

			var connection = new LspConnection(Console.OpenStandardInput(), Console.OpenStandardOutput());

			// stdout is the protocol channel
			Console.SetOut(TextWriter.Null);

			var server = new PreviewServer(connection, platform);
			var reader = new Thread(() =>
			{
				try
				{
					connection.Run();
				}
				catch (Exception ex)
				{
					Console.Error.WriteLine(ex);
				}
				// the editor went away
				Environment.Exit(0);
			})
			{ IsBackground = true, Name = "Preview protocol" };
			reader.Start();

			server.RunUI();
			return 0;
		}
	}
}
