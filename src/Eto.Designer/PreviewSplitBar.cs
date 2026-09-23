using System;
using Eto.Forms;
using Eto.Drawing;

namespace Eto.Designer
{
    /// <summary>A pane shown by <see cref="PreviewEditorViewSplitter"/>.</summary>
    public enum PreviewPane
    {
        Design,
        Code
    }

    /// <summary>
    /// Bar between the preview and code, with tabs for each pane and buttons to rearrange them.
    /// </summary>
    class PreviewSplitBar : Panel
    {
        readonly PreviewEditorViewSplitter splitter;
        readonly SplitBarButton designTab;
        readonly SplitBarButton codeTab;
        readonly SplitBarButton swapButton;
        readonly SplitBarButton orientationButton;
        readonly SplitBarButton collapseButton;
        readonly StackLayout tabs = new StackLayout { Orientation = Orientation.Horizontal, VerticalContentAlignment = VerticalAlignment.Stretch };

        public PreviewSplitBar(PreviewEditorViewSplitter splitter)
        {
            this.splitter = splitter;
            BackgroundColor = Global.Theme.SplitBarBackground;
            Padding = new Padding(2, 1);

            designTab = new SplitBarButton { Text = "Design" };
            designTab.Click += (sender, e) => splitter.TabClicked(PreviewPane.Design);
            codeTab = new SplitBarButton { Text = "Code" };
            codeTab.Click += (sender, e) => splitter.TabClicked(PreviewPane.Code);

            swapButton = new SplitBarButton { ToolTip = "Swap panes", PaintIcon = PaintSwap };
            swapButton.Click += (sender, e) => splitter.Swapped = !splitter.Swapped;

            orientationButton = new SplitBarButton { PaintIcon = PaintOrientation };
            orientationButton.Click += (sender, e) => splitter.Orientation = splitter.Orientation == Orientation.Vertical ? Orientation.Horizontal : Orientation.Vertical;

            collapseButton = new SplitBarButton { PaintIcon = PaintCollapse };
            collapseButton.Click += (sender, e) => splitter.ToggleCollapsed();

            Content = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Items = { tabs, new StackLayoutItem(null, expand: true), orientationButton, collapseButton }
            };
            UpdateState();
        }

        /// <summary>Reflects the splitter's current layout.</summary>
        public void UpdateState()
        {
            var first = splitter.Swapped ? codeTab : designTab;
            var second = splitter.Swapped ? designTab : codeTab;
            tabs.Items.Clear();
            tabs.Items.Add(first);
            tabs.Items.Add(swapButton);
            tabs.Items.Add(second);

            var single = splitter.SinglePane;
            designTab.Selected = single == PreviewPane.Design;
            codeTab.Selected = single == PreviewPane.Code;
            designTab.ToolTip = single == PreviewPane.Design ? "Show both panes" : "Show only the design";
            codeTab.ToolTip = single == PreviewPane.Code ? "Show both panes" : "Show only the code";

            swapButton.Visible = single == null;
            orientationButton.Visible = single == null;
            orientationButton.ToolTip = splitter.Orientation == Orientation.Vertical ? "Show panes side by side" : "Show panes above each other";
            collapseButton.ToolTip = single == null ? "Collapse pane" : "Expand pane";
            foreach (var button in new[] { designTab, codeTab, swapButton, orientationButton, collapseButton })
                button.Invalidate();
        }

        void PaintSwap(Graphics g, RectangleF rect, Color color)
        {
            var c = rect.Center;
            if (splitter.Orientation == Orientation.Vertical)
            {
                DrawArrow(g, color, new PointF(c.X - 2, c.Y + 4), new PointF(c.X - 2, c.Y - 4));
                DrawArrow(g, color, new PointF(c.X + 2, c.Y - 4), new PointF(c.X + 2, c.Y + 4));
            }
            else
            {
                DrawArrow(g, color, new PointF(c.X + 4, c.Y - 2), new PointF(c.X - 4, c.Y - 2));
                DrawArrow(g, color, new PointF(c.X - 4, c.Y + 2), new PointF(c.X + 4, c.Y + 2));
            }
        }

        void PaintOrientation(Graphics g, RectangleF rect, Color color)
        {
            // shows the layout the button switches to
            var box = new RectangleF(rect.Center.X - 5, rect.Center.Y - 5, 10, 10);
            g.DrawRectangle(color, box);
            if (splitter.Orientation == Orientation.Vertical)
                g.DrawLine(color, box.MiddleTop, box.MiddleBottom);
            else
                g.DrawLine(color, box.MiddleLeft, box.MiddleRight);
        }

        void PaintCollapse(Graphics g, RectangleF rect, Color color)
        {
            // chevrons point the way the second pane collapses, or back when expanding
            var c = rect.Center;
            var sign = splitter.SinglePane == null ? 1 : -1;
            for (int i = -1; i <= 1; i += 2)
            {
                var offset = i * 2 * sign;
                if (splitter.Orientation == Orientation.Vertical || splitter.SinglePane != null)
                {
                    var y = c.Y + offset;
                    g.DrawLines(color, new PointF(c.X - 4, y - 2 * sign), new PointF(c.X, y + 2 * sign), new PointF(c.X + 4, y - 2 * sign));
                }
                else
                {
                    var x = c.X + offset;
                    g.DrawLines(color, new PointF(x - 2 * sign, c.Y - 4), new PointF(x + 2 * sign, c.Y), new PointF(x - 2 * sign, c.Y + 4));
                }
            }
        }

        static void DrawArrow(Graphics g, Color color, PointF from, PointF to)
        {
            g.DrawLine(color, from, to);
            var dir = to - from;
            var length = (float)Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
            var unit = new PointF(dir.X / length, dir.Y / length);
            var normal = new PointF(-unit.Y, unit.X);
            g.DrawLine(color, to, to - unit * 3 + normal * 3);
            g.DrawLine(color, to, to - unit * 3 - normal * 3);
        }
    }

    /// <summary>Flat tab or icon button drawn in the split bar's theme colors.</summary>
    class SplitBarButton : Drawable
    {
        static readonly Font font = SystemFonts.Default();
        bool hover;
        string text;

        public event EventHandler<EventArgs> Click;

        public bool Selected { get; set; }

        public Action<Graphics, RectangleF, Color> PaintIcon { get; set; }

        public string Text
        {
            get => text;
            set
            {
                text = value;
                var size = font.MeasureString(value);
                Size = new Size((int)Math.Ceiling(size.Width) + 16, -1);
            }
        }

        public SplitBarButton()
        {
            Size = new Size(22, 20);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var theme = Global.Theme;
            var g = e.Graphics;
            var rect = new RectangleF(PointF.Empty, Size);
            if (Selected)
                g.FillRectangle(theme.SplitBarSelectedBackground, rect);
            else if (hover)
                g.FillRectangle(theme.SplitBarHoverBackground, rect);

            var color = Selected ? theme.SplitBarSelectedForeground : theme.SplitBarForeground;
            if (text != null)
            {
                var size = g.MeasureString(font, text);
                g.DrawText(font, color, rect.Center - size / 2, text);
            }
            PaintIcon?.Invoke(g, rect, color);
        }

        protected override void OnMouseEnter(MouseEventArgs e)
        {
            base.OnMouseEnter(e);
            hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            hover = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Buttons == MouseButtons.Primary)
            {
                e.Handled = true;
                Click?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
