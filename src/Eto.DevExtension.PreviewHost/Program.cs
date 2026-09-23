using System;
using System.IO;
using System.Threading;
using Eto.DevExtension.LanguageServer.Lsp;

namespace Eto.DevExtension.PreviewHost
{
	static class Program
	{
		[STAThread]
		static int Main(string[] args)
		{
			var connection = new LspConnection(Console.OpenStandardInput(), Console.OpenStandardOutput());

			// stdout is the protocol channel
			Console.SetOut(TextWriter.Null);

			var server = new PreviewServer(connection);
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
