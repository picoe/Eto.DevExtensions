using System;
using System.Linq;
using Eto.Forms;
using Eto.Designer;
using System.Reflection;
using System.Diagnostics;

namespace Eto.DevExtension.VisualStudio.Mac
{
	public static class EtoInitializer
	{
        static readonly object Cell_Key = new object();
        static bool initialized;
		public static void Initialize()
		{
			if (initialized)
				return;

			initialized = true;

#if VS2019
            Style.Add<Eto.Mac.Forms.Controls.TextBoxHandler>(null, h =>
            {
				// VS 2019 for Mac is dumb and GC's the cell even though it's still in use.
                h.Widget.Properties[Cell_Key] = h.Control.Cell;
            });
#endif
#if VS2022
			AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
			{
				var name = new AssemblyName(e.Name);
				if (name.Name == "Microsoft.macOS")
					return typeof(AppKit.NSView).Assembly;
				return null;
			};
#endif

			try
			{
				var platform = Platform.Instance;
				if (platform == null)
				{
					platform = new Eto.Mac.Platform();
					Platform.Initialize(platform);
				}

				platform.LoadAssembly(typeof(EtoInitializer).Assembly);

				if (Application.Instance == null)
					new Eto.Forms.Application().Attach();

			}
			catch (Exception ex)
			{
				Debug .WriteLine($"{ex}");
			}
		}
	}
}

