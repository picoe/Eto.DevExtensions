#if WINDOWS
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Eto.Forms;

namespace Eto.DevExtension.PreviewHost
{
	class WpfPreviewPlatform : PreviewPlatform
	{
		public override string Name => "Wpf";
		public override string AssemblyName => "Eto.Wpf";
		public override string PackageId => "eto.platform.wpf";

		public override IEnumerable<string> GetFrameworks(IEnumerable<string> available)
		{
			foreach (var major in Majors())
			{
				yield return $"net{major}.0-windows7.0";
				yield return $"net{major}.0-windows";
			}
			yield return "netcoreapp3.1";
		}

		public override object CreatePlatform() => new Eto.Wpf.Platform();

		// the offscreen forms come and go, and must not end the process
		public override void Initialized() => System.Windows.Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

		public override async Task<RenderResult> CaptureAsync(object control, RenderRequest request)
		{
			var form = OffscreenForm.Create((Control)control, request, out var content);
			try
			{
				form.Show();
				await Dispatcher.Yield(DispatcherPriority.ContextIdle);

				var element = WpfHelpers.ToNative(content);
				element.UpdateLayout();
				var width = (int)Math.Ceiling(element.ActualWidth);
				var height = (int)Math.Ceiling(element.ActualHeight);
				if (width <= 0 || height <= 0)
					return RenderResult.Empty(width, height);

				var scale = request.Scale > 0 ? request.Scale : 1;
				var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
				// through a brush, since rendering the element directly includes its offset in the window
				var visual = new DrawingVisual();
				using (var context = visual.RenderOpen())
					context.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, width, height));
				bitmap.Render(visual);

				var encoder = new PngBitmapEncoder();
				encoder.Frames.Add(BitmapFrame.Create(bitmap));
				using (var stream = new MemoryStream())
				{
					encoder.Save(stream);
					return RenderResult.Png(stream.ToArray(), width, height);
				}
			}
			finally
			{
				form.Close();
				((Control)control).Dispose();
			}
		}
	}
}
#endif
