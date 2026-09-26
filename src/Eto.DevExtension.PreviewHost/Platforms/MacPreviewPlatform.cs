#if !WINDOWS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Eto.Forms;
#if MACOS
using AppKit;
using Foundation;
using CoreGraphics;
#else
using MonoMac.AppKit;
using MonoMac.Foundation;
using MonoMac.CoreGraphics;
#endif

namespace Eto.DevExtension.PreviewHost
{
	/// <summary>Eto.macOS in the app bundle host, or Eto.Mac64 when run with dotnet.</summary>
	class MacPreviewPlatform : PreviewPlatform
	{
#if MACOS
		public override string Name => "macOS";
		public override string AssemblyName => "Eto.macOS";
		public override string PackageId => "eto.platform.macos";

		// folders are versioned, e.g. net10.0-macos26.0
		public override IEnumerable<string> GetFrameworks(IEnumerable<string> available) =>
			Majors().SelectMany(major => available
				.Where(r => r.StartsWith($"net{major}.0-macos", StringComparison.OrdinalIgnoreCase))
				.OrderByDescending(r => r, StringComparer.OrdinalIgnoreCase));
#else
		public override string Name => "Mac64";
		public override string AssemblyName => "Eto.Mac64";
		public override string PackageId => "eto.platform.mac64";
		public override IEnumerable<string> CompanionNames => new[] { "MonoMac" };

		public override IEnumerable<string> GetFrameworks(IEnumerable<string> available) =>
			ProjectAssemblies.RuntimeFrameworks();
#endif

		public override object CreatePlatform() => new Eto.Mac.Platform();

		// no dock icon or menu bar
		public override void Initialized() => NSApplication.SharedApplication.ActivationPolicy = NSApplicationActivationPolicy.Accessory;

		public override Task<RenderResult> CaptureAsync(object control, RenderRequest request)
		{
			var form = OffscreenForm.Create((Control)control, request, out var content);
			try
			{
				form.Show();
				Application.Instance.RunIteration();

				#if MACOS
				var view = MacOSHelpers.ToNative(content);
#else
				var view = MonoMac64Helpers.ToNative(content);
#endif
				var width = (int)Math.Ceiling(view.Frame.Width);
				var height = (int)Math.Ceiling(view.Frame.Height);
				if (width <= 0 || height <= 0)
					return Task.FromResult(RenderResult.Empty(width, height));

				// sized by hand, since the offscreen window has no screen to take a backing scale from
				var scale = request.Scale > 0 ? request.Scale : 1;
				var bounds = view.Bounds;
				var rep = view.BitmapImageRepForCachingDisplayInRect(bounds);
				if (Math.Abs(rep.PixelsWide - width * scale) > 1)
					rep = new NSBitmapImageRep(IntPtr.Zero, (int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 8, 4, true, false, NSColorSpace.DeviceRGB, 0, 32) { Size = bounds.Size };
				view.CacheDisplay(bounds, rep);

				var data = rep.RepresentationUsingTypeProperties(NSBitmapImageFileType.Png, new NSDictionary());
				using (var input = data.AsStream())
				using (var stream = new MemoryStream())
				{
					input.CopyTo(stream);
					return Task.FromResult(RenderResult.Png(stream.ToArray(), width, height));
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
