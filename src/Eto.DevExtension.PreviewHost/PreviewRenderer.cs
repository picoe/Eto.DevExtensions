using System;
using System.IO;
using System.Threading.Tasks;
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

		public static RenderResult Png(byte[] png, int width, int height) =>
			new RenderResult { Image = Convert.ToBase64String(png), Width = width, Height = height };

		/// <summary>For a control with nothing to draw.</summary>
		public static RenderResult Empty(int width, int height) =>
			new RenderResult { Width = Math.Max(width, 0), Height = Math.Max(height, 0) };
	}

	class RenderError
	{
		public string Message { get; set; }
		public string Details { get; set; }
	}

	/// <summary>Builds a designer file into a control and draws it to a PNG. Use from the UI thread only.</summary>
	class PreviewRenderer
	{
		readonly PreviewPlatform platform;
		string builderFile;
		IInterfaceBuilder builder;
		IBuildToken token;

		public PreviewRenderer(PreviewPlatform platform) => this.platform = platform;

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
					control => Capture(control, request).ContinueWith(r => completion.TrySetResult(r.IsFaulted ? Error(r.Exception) : r.Result)),
					ex => completion.TrySetResult(Error(ex)));
			}
			catch (Exception ex)
			{
				completion.TrySetResult(Error(ex));
			}
			return completion.Task;
		}

		Task<RenderResult> Capture(Control control, RenderRequest request)
		{
			try
			{
				return platform.CaptureAsync(control, request);
			}
			catch (Exception ex)
			{
				return Task.FromException<RenderResult>(ex);
			}
		}

		static RenderResult Error(Exception ex)
		{
			ex = (ex as AggregateException)?.Flatten().InnerException ?? ex;
			var root = ex.GetBaseException();
			return new RenderResult { Error = new RenderError { Message = root.Message, Details = ex.ToString() } };
		}
	}

	static class OffscreenForm
	{
		/// <summary>A form out of sight holding the control, shown for real so load events fire and templates apply like in the app.</summary>
		public static Form Create(Control control, RenderRequest request, out Control content)
		{
			content = DesignPanel.GetContent(control);
			if (request.Width != null || request.Height != null)
				content.Size = new Eto.Drawing.Size(request.Width ?? -1, request.Height ?? -1);

			return new Form
			{
				Content = content,
				WindowStyle = WindowStyle.None,
				ShowInTaskbar = false,
				ShowActivated = false,
				Resizable = false,
				Location = new Eto.Drawing.Point(-32000, -32000)
			};
		}
	}
}
