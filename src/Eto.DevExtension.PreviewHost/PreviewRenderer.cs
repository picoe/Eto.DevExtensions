using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Eto.Designer;
using Eto.Forms;

namespace Eto.DevExtension.PreviewHost
{
	class RenderRequest
	{
		public string FileName;
		public string Text;
		public int? Width;
		public int? Height;
		public double Scale = 1;
	}

	class RenderResult
	{
		public string Image { get; set; }
		public int Width { get; set; }
		public int Height { get; set; }
		public RenderError Error { get; set; }
		public bool? RestartRequired { get; set; }
	}

	class RenderError
	{
		public string Message { get; set; }
		public string Details { get; set; }
	}

	/// <summary>Builds a designer file into a control and draws it to a PNG. Use from the UI thread only.</summary>
	class PreviewRenderer
	{
		string builderFile;
		IInterfaceBuilder builder;
		IBuildToken token;

		public Task<RenderResult> RenderAsync(RenderRequest request)
		{
			var completion = new TaskCompletionSource<RenderResult>();
			try
			{
				if (!string.Equals(builderFile, request.FileName, StringComparison.OrdinalIgnoreCase))
				{
					builder = BuilderInfo.Find(request.FileName)?.CreateBuilder();
					builderFile = request.FileName;
				}
				if (builder == null)
					return Task.FromResult(Error(new NotSupportedException($"No designer for {Path.GetFileName(request.FileName)}")));

				token?.Cancel();
				token = builder.Create(
					request.Text,
					ProjectAssemblies.MainAssembly,
					ProjectAssemblies.Paths,
					control => CaptureAsync(control, request).ContinueWith(r => completion.TrySetResult(r.IsFaulted ? Error(r.Exception) : r.Result)),
					ex => completion.TrySetResult(Error(ex)));
			}
			catch (Exception ex)
			{
				completion.TrySetResult(Error(ex));
			}
			return completion.Task;
		}

		static async Task<RenderResult> CaptureAsync(Control control, RenderRequest request)
		{
			var content = DesignPanel.GetContent(control);
			if (request.Width != null || request.Height != null)
				content.Size = new Eto.Drawing.Size(request.Width ?? -1, request.Height ?? -1);

			// shown for real, but out of sight, so load events fire and templates apply like in the app
			var form = new Form
			{
				Content = content,
				WindowStyle = Eto.Forms.WindowStyle.None,
				ShowInTaskbar = false,
				ShowActivated = false,
				Resizable = false,
				Location = new Eto.Drawing.Point(-32000, -32000)
			};
			try
			{
				form.Show();
				await Dispatcher.Yield(DispatcherPriority.ContextIdle);

				var element = content.ToNative();
				element.UpdateLayout();
				var width = (int)Math.Ceiling(element.ActualWidth);
				var height = (int)Math.Ceiling(element.ActualHeight);
				if (width <= 0 || height <= 0)
					return new RenderResult { Width = Math.Max(width, 0), Height = Math.Max(height, 0) };

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
					return new RenderResult { Image = Convert.ToBase64String(stream.ToArray()), Width = width, Height = height };
				}
			}
			finally
			{
				form.Close();
				control.Dispose();
			}
		}

		static RenderResult Error(Exception ex)
		{
			ex = (ex as AggregateException)?.Flatten().InnerException ?? ex;
			var root = ex.GetBaseException();
			return new RenderResult { Error = new RenderError { Message = root.Message, Details = ex.ToString() } };
		}
	}
}
