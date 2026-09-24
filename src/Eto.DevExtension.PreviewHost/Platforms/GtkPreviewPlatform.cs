#if !WINDOWS && !MACOS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Eto.Forms;

namespace Eto.DevExtension.PreviewHost
{
	class GtkPreviewPlatform : PreviewPlatform
	{
		public override string Name => "Gtk";
		public override string AssemblyName => "Eto.Gtk";
		public override string PackageId => "eto.platform.gtk";

		public override IEnumerable<string> SharedNames => new[] { "GtkSharp", "GdkSharp", "GLibSharp", "GioSharp", "AtkSharp", "PangoSharp", "CairoSharp" };

		public override IEnumerable<string> GetFrameworks(IEnumerable<string> available) =>
			ProjectAssemblies.RuntimeFrameworks();

		public override object CreatePlatform() => new Eto.GtkSharp.Platform();

		// otherwise the first text box shows with its text selected, as the offscreen window focuses it
		public override void Initialized() => Gtk.Settings.Default.SetProperty("gtk-entry-select-on-focus", new GLib.Value(false));

		public override Task<RenderResult> CaptureAsync(object control, RenderRequest request)
		{
			var etoControl = (Control)control;
			var content = Eto.Designer.DesignPanel.GetContent(etoControl);
			if (request.Width != null || request.Height != null)
				content.Size = new Eto.Drawing.Size(request.Width ?? -1, request.Height ?? -1);

			// an offscreen window, since Wayland won't let a real one be moved out of sight
			var window = new Gtk.OffscreenWindow();
			Gtk.Widget widget = null;
			try
			{
				widget = Gtk3Helpers.ToNative(content, true);
				window.Add(widget);
				// GTK would give wrapping labels their narrowest width, unlike an Eto form
				if (request.Width == null)
					content.Width = (int)Math.Ceiling(content.GetPreferredSize().Width);
				window.ShowAll();
				window.Focus = null;
				for (var i = 0; i < 10 && Gtk.Application.EventsPending(); i++)
					Gtk.Application.RunIteration(false);

				var width = widget.AllocatedWidth;
				var height = widget.AllocatedHeight;
				if (width <= 0 || height <= 0)
					return Task.FromResult(RenderResult.Empty(width, height));

				var scale = request.Scale > 0 ? request.Scale : 1;
				using (var surface = new Cairo.ImageSurface(Cairo.Format.Argb32, (int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale)))
				{
					using (var context = new Cairo.Context(surface))
					{
						context.Scale(scale, scale);
						widget.Draw(context);
					}
					surface.Flush();
					// CairoSharp can only write PNGs to a file
					var file = Path.GetTempFileName();
					try
					{
						surface.WriteToPng(file);
						return Task.FromResult(RenderResult.Png(File.ReadAllBytes(file), width, height));
					}
					finally
					{
						File.Delete(file);
					}
				}
			}
			finally
			{
				if (widget?.Parent == window)
					window.Remove(widget);
				window.Destroy();
				etoControl.Dispose();
			}
		}
	}
}
#endif
