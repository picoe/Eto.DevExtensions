using Eto.Drawing;
using Eto.Forms;
using System;
using System.Threading.Tasks;

namespace Eto.Designer
{
	public class PreviewRenderRequest
	{
		public string FileName { get; set; }
		public string Text { get; set; }
		/// <summary>Size the user dragged the preview to, or null for its own size.</summary>
		public Size? Size { get; set; }
		public float Scale { get; set; } = 1;
	}

	public class PreviewRenderResult
	{
		/// <summary>PNG data, or null when there's nothing to show.</summary>
		public byte[] Image { get; set; }
		/// <summary>Size of the image in logical pixels.</summary>
		public Size Size { get; set; }
		public DesignError Error { get; set; }
	}

	/// <summary>
	/// Shows a preview drawn somewhere else, such as a separate process, so the project's code never runs here.
	/// </summary>
	public class RemoteDesignPanel : Scrollable, IDesignHost
	{
		readonly Func<PreviewRenderRequest, Task<PreviewRenderResult>> render;
		readonly DesignSurface designSurface;
		readonly Drawable imageView;
		BuilderInfo builder;
		Bitmap image;
		string fileName;
		string code;
		bool rendering;
		bool renderPending;

		public Action ControlCreating { get; set; }
		public Action ControlCreated { get; set; }
		public Action<DesignError> Error { get; set; }

		/// <param name="render">Draws the preview, called with one request at a time.</param>
		public RemoteDesignPanel(Func<PreviewRenderRequest, Task<PreviewRenderResult>> render)
		{
			this.render = render;
			imageView = new Drawable { Size = new Size(1, 1) };
			// stretched while dragging to a new size, until the redraw at that size comes back
			imageView.Paint += (sender, e) =>
			{
				if (image != null)
					e.Graphics.DrawImage(image, new RectangleF(imageView.Size));
			};
			designSurface = new DesignSurface();
			designSurface.RequestedSizeChanged += (sender, e) => Render();
			Border = BorderType.None;
			BackgroundColor = Global.Theme.DesignerBackground;
			Content = designSurface;
		}

		public Control GetContainer() => this;

		public bool SetBuilder(string fileName)
		{
			this.fileName = fileName;
			builder = BuilderInfo.Find(fileName);
			return builder != null;
		}

		public string GetCodeFile(string fileName) => builder?.GetCodeFile(fileName);

		public void Update(string code)
		{
			this.code = code;
			Render();
		}

		async void Render()
		{
			if (code == null)
				return;
			if (rendering)
			{
				renderPending = true;
				return;
			}

			rendering = true;
			try
			{
				do
				{
					renderPending = false;
					var request = new PreviewRenderRequest
					{
						FileName = fileName,
						Text = code,
						Size = designSurface.RequestedSize,
						Scale = (ParentWindow?.Screen ?? Screen.PrimaryScreen)?.LogicalPixelSize ?? 1
					};
					PreviewRenderResult result;
					try
					{
						result = await render(request);
					}
					catch (Exception ex)
					{
						result = new PreviewRenderResult { Error = new DesignError { Message = ex.GetBaseException().Message, Details = ex.ToString() } };
					}
					if (IsDisposed)
						return;
					Show(result);
				}
				while (renderPending);
			}
			finally
			{
				rendering = false;
			}
		}

		void Show(PreviewRenderResult result)
		{
			if (result == null)
				return;
			if (result.Error != null)
			{
				// keep the last good preview up
				Error?.Invoke(result.Error);
				return;
			}

			ControlCreating?.Invoke();
			var old = image;
			image = result.Image != null ? new Bitmap(result.Image) : null;
			old?.Dispose();
			// while the user has picked a size the surface owns it, and an older result mustn't undo a drag
			if (designSurface.RequestedSize == null)
				imageView.Size = new Size(Math.Max(1, result.Size.Width), Math.Max(1, result.Size.Height));
			if (designSurface.Content == null)
				designSurface.Content = imageView;
			imageView.Invalidate();
			ControlCreated?.Invoke();
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				image?.Dispose();
				image = null;
			}
			base.Dispose(disposing);
		}
	}
}
