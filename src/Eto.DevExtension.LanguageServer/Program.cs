using System;
using System.IO;
using Eto.DevExtension.LanguageServer.Lsp;

namespace Eto.DevExtension.LanguageServer
{
	static class Program
	{
		static int Main(string[] args)
		{
			var connection = new LspConnection(Console.OpenStandardInput(), Console.OpenStandardOutput());

			// must happen before any Eto type is touched, which is why the completion
			// engine is only reached through CompletionService
			EtoAssemblyResolver.Install(connection.Log);

			// stdout is the protocol channel
			Console.SetOut(TextWriter.Null);

			var server = new XetoLanguageServer(connection);
			try
			{
				connection.Run();
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine(ex);
				return 1;
			}
			return 0;
		}
	}
}
