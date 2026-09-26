#if WINDOWS
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Eto.Forms;

namespace Eto.DevExtension.PreviewHost
{
	class WinFormsPreviewPlatform : PreviewPlatform
	{
		public override string Name => "WinForms";
		public override string AssemblyName => "Eto.WinForms";
		public override string PackageId => "eto.platform.windows";

		public override IEnumerable<string> GetFrameworks(IEnumerable<string> available)
		{
			foreach (var major in Majors())
			{
				yield return $"net{major}.0-windows7.0";
				yield return $"net{major}.0-windows";
			}
			yield return "netcoreapp3.1";
		}

		public override object CreatePlatform() => new Eto.WinForms.Platform();

		public override Task<RenderResult> CaptureAsync(object control, RenderRequest request)
		{
			var form = OffscreenForm.Create((Control)control, request, out var content);
			try
			{
				form.Show();
				System.Windows.Forms.Application.DoEvents();

				var native = WinFormsHelpers.ToNative(content);
				var width = native.Width;
				var height = native.Height;
				if (width <= 0 || height <= 0)
					return Task.FromResult(RenderResult.Empty(width, height));

				// WinForms can only draw at the screen's own scale, so report the size in logical pixels
				var dpiScale = native.DeviceDpi / 96.0;
				using (var bitmap = new System.Drawing.Bitmap(width, height))
				using (var stream = new MemoryStream())
				{
					native.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, width, height));
					bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
					return Task.FromResult(RenderResult.Png(stream.ToArray(), (int)Math.Round(width / dpiScale), (int)Math.Round(height / dpiScale)));
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
