using System;
using System.IO;
using System.Threading.Tasks;
using Eto.Designer;
using Eto.Designer.Builders;
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
		/// <summary>Theme name, or null for the default.</summary>
		public string Theme;
	}

	class RenderResult
	{
		public string Image { get; set; }
		public int Width { get; set; }
		public int Height { get; set; }
		public RenderError Error { get; set; }
		public bool? RestartRequired { get; set; }
		/// <summary>Theme names that can be picked.</summary>
		public string[] Themes { get; set; }
		/// <summary>Name of the theme it was drawn with.</summary>
		public string Theme { get; set; }

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
		/// <summary>Where in the text the error is, or null when not known.</summary>
		public RenderRange Range { get; set; }
	}

	/// <summary>Zero based, like LSP.</summary>
	class RenderRange
	{
		public RenderPosition Start { get; set; }
		public RenderPosition End { get; set; }
	}

	class RenderPosition
	{
		public int Line { get; set; }
		public int Character { get; set; }
	}

	/// <summary>Builds a designer file into a control and draws it to a PNG. Use from the UI thread only.</summary>
	class PreviewRenderer
	{
		readonly PreviewPlatform platform;
		readonly PreviewThemes themes;
		string builderFile;
		IInterfaceBuilder builder;
		IBuildToken token;

		public PreviewRenderer(PreviewPlatform platform, PreviewThemes themes)
		{
			this.platform = platform;
			this.themes = themes;
		}

		public async Task<RenderResult> RenderAsync(RenderRequest request)
		{
			themes.Apply(request.Theme);
			var result = await BuildAsync(request);
			result.Themes = themes.Names;
			result.Theme = themes.Current;
			return result;
		}

		Task<RenderResult> BuildAsync(RenderRequest request)
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
					return Task.FromResult(Error(new NotSupportedException($"No designer for {Path.GetFileName(request.FileName)}"), null));

				// code files are compiled, so line numbers in their errors are for other files
				var text = builder is XamlInterfaceBuilder || builder is JsonInterfaceBuilder ? request.Text : null;
				token?.Cancel();
				token = builder.Create(
					request.Text,
					ProjectAssemblies.MainAssembly,
					ProjectAssemblies.Paths,
					// once built, an error with a line number is from another file the control loaded
					control => Capture(control, request).ContinueWith(r => completion.TrySetResult(r.IsFaulted ? Error(r.Exception, null) : r.Result)),
					ex => completion.TrySetResult(Error(ex, text)));
			}
			catch (Exception ex)
			{
				completion.TrySetResult(Error(ex, null));
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

		/// <param name="text">The text that was built, to find where the error is in, or null to not look.</param>
		static RenderResult Error(Exception ex, string text)
		{
			ex = (ex as AggregateException)?.Flatten().InnerException ?? ex;
			var root = ex.GetBaseException();
			var range = text != null ? ErrorLocation.Find(ex, text) : null;
			return new RenderResult { Error = new RenderError { Message = root.Message, Details = ex.ToString(), Range = range } };
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
