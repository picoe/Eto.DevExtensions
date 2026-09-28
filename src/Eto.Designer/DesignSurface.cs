using Eto.Drawing;
using Eto.Forms;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Eto.Designer
{
	/// <summary>
	/// Shows the content centered, with its size above it and a handle to resize it by.
	/// </summary>
	public class DesignSurface : Drawable
	{
		public static Size GripPadding = new Size(4, 4);
		static Size GripSize = new Size(10, 10);
		static Font LabelFont = SystemFonts.Default(8);
		bool _enabled;
		bool _hover;
		bool _dragging;
		PointF _startDrag;
		SizeF _startSize;
		Size? _originalContentSize;

		public bool EnableResizing { get; set; } = true;

		public event EventHandler InvalidateContent;

		/// <summary>Raised when the user drags the content to a size, or resets it to auto size.</summary>
		public event EventHandler RequestedSizeChanged;

		/// <summary>Size the user dragged the content to, or null for its own size.</summary>
		public Size? RequestedSize => _sizeBounds != null ? Size.Round(_sizeBounds.Value) : (Size?)null;

		public DesignSurface()
		{
			// doesn't work correctly on Gtk2 due to lack of control transparency
			_enabled = !Platform.Instance.IsGtk;
			if (_enabled)
				Padding = new Padding(32, 40, 32, 32);
		}

		bool CanResize => _enabled && EnableResizing && _content != null;

		Control _content;

		public new Control Content
		{
			get { return _content; }
			set
			{
				if (_content != null)
					_content.SizeChanged -= content_Relayout;
				_content = value;

				if (_content != null)
				{
					// centered side to side only, so dragging the corner grows it both ways and down
					base.Content = new TableLayout
					{
						Rows =
						{
							new TableRow(new TableCell { ScaleWidth = true }, _content, new TableCell { ScaleWidth = true }),
							new TableRow { ScaleHeight = true }
						}
					};
					_content.SizeChanged += content_SizeChanged;
					_content.SizeChanged += content_Relayout;
				}
				else
				{
					base.Content = null;
				}
				Invalidate();
			}
		}

		// the border, label and handle follow the content, which only gets its size and place after layout
		void content_Relayout(object sender, EventArgs e) => Invalidate();

		protected override void OnSizeChanged(EventArgs e)
		{
			base.OnSizeChanged(e);
			Invalidate();
		}

		private void content_SizeChanged(object sender, EventArgs e)
		{
			if (_content.Size == Size.Empty)
				return;
			_originalContentSize = _content?.Size;
			if (_sizeBounds != null)
				_content.Size = Size.Round(_sizeBounds.Value);
			_content.SizeChanged -= content_SizeChanged;
		}

		SizeF? _sizeBounds;

		RectangleF ContentBounds
		{
			get
			{
				if (_content == null)
					return RectangleF.Empty;
				var contentRect = RectangleFromScreen(_content.RectangleToScreen(new RectangleF(_content.Size)));
				return new RectangleF(contentRect.Location, _sizeBounds ?? contentRect.Size);
			}
		}

		RectangleF GripBounds
		{
			get
			{
				var bounds = ContentBounds;
				return new RectangleF(bounds.Right - 1, bounds.Bottom - 1, GripSize.Width, GripSize.Height);
			}
		}

		string LabelText => $"{_content?.Size.Width}x{_content?.Size.Height}";

		RectangleF LabelBounds
		{
			get
			{
				var bounds = ContentBounds;
				var size = LabelFont.MeasureString(LabelText) + new SizeF(12, 2);
				return new RectangleF(bounds.Center.X - size.Width / 2, bounds.Top - 30, size.Width, size.Height);
			}
		}

		void SetSize(SizeF size)
		{
			_sizeBounds = size;
			_content.Size = Size.Round(size);
			Invalidate();
			RequestedSizeChanged?.Invoke(this, EventArgs.Empty);
		}

		void ResetSize()
		{
			_sizeBounds = null;
			_content.Size = _originalContentSize ?? new Size(-1, -1);
			Invalidate();
			InvalidateContent?.Invoke(this, EventArgs.Empty);
			RequestedSizeChanged?.Invoke(this, EventArgs.Empty);
		}

		bool IsOverGrip(PointF location) => RectangleF.Inflate(GripBounds, new SizeF(2, 2)).Contains(location);

		bool IsOverLabel(PointF location) => _sizeBounds != null && LabelBounds.Contains(location);

		protected override void OnPaint(PaintEventArgs e)
		{
			base.OnPaint(e);
			if (!_enabled || _content == null)
				return;
			var g = e.Graphics;
			var theme = Global.Theme;
			var bounds = ContentBounds;
			g.DrawRectangle(theme.DesignerBorder, bounds.X - 1, bounds.Y - 1, bounds.Width + 1, bounds.Height + 1);
			if (!CanResize)
				return;
			var gripColor = theme.SizeGrip;
			if (_hover || _dragging)
			{
				var outline = RectangleF.Inflate(ContentBounds, GripPadding);
				using (var pen = new Pen(gripColor, 1) { DashStyle = DashStyles.Dash })
					g.DrawRectangle(pen, outline);
			}

			var label = LabelBounds;
			g.FillPath(theme.SizeLabelBackground, GraphicsPath.GetRoundRect(label, 2));
			g.DrawText(LabelFont, theme.SizeLabelForeground, label.Location + new SizeF(6, 1), LabelText);

			g.FillEllipse(gripColor, GripBounds);
		}

		protected override void OnMouseDown(MouseEventArgs e)
		{
			base.OnMouseDown(e);
			if (!CanResize || e.Buttons != MouseButtons.Primary)
				return;
			if (IsOverGrip(e.Location))
			{
				_dragging = true;
				_startDrag = e.Location;
				_startSize = ContentBounds.Size;
				e.Handled = true;
			}
			else if (IsOverLabel(e.Location))
			{
				ResetSize();
				e.Handled = true;
			}
		}

		protected override void OnMouseUp(MouseEventArgs e)
		{
			base.OnMouseUp(e);
			if (_dragging)
			{
				_dragging = false;
				Invalidate();
				e.Handled = true;
			}
		}

		protected override void OnMouseMove(MouseEventArgs e)
		{
			base.OnMouseMove(e);
			if (!CanResize)
				return;
			if (_dragging)
			{
				// centered, so it grows both ways and the edge under the mouse moves twice as far
				var diff = e.Location - _startDrag;
				SetSize(new SizeF(Math.Max(1, _startSize.Width + diff.X * 2), Math.Max(1, _startSize.Height + diff.Y)));
				return;
			}

			var hover = RectangleF.Inflate(ContentBounds, GripSize).Contains(e.Location);
			if (hover != _hover)
			{
				_hover = hover;
				Invalidate();
			}
			var overGrip = IsOverGrip(e.Location);
			var overLabel = IsOverLabel(e.Location);
			Cursor = overGrip ? Cursors.SizeBottomRight : overLabel ? Cursors.Pointer : Cursors.Default;
			// setting it on every move would keep restarting the tooltip
			var toolTip = overGrip ? "Drag to resize" : overLabel ? "Click to reset to auto size" : null;
			if (toolTip != ToolTip)
				ToolTip = toolTip;
		}

		protected override void OnMouseLeave(MouseEventArgs e)
		{
			base.OnMouseLeave(e);
			if (_hover && !_dragging)
			{
				_hover = false;
				Invalidate();
			}
		}
	}
}
