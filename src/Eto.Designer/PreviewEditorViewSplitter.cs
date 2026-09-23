using System;
using Eto.Forms;
using Eto.Drawing;
using System.Collections.Generic;

namespace Eto.Designer
{
    /// <summary>Shows the preview and code editor split, with a bar to swap, rotate or collapse them.</summary>
    public class PreviewEditorViewSplitter : Panel
    {
        static double lastPosition = 0.4;

        readonly Splitter splitter;
        readonly Panel firstSlot = new Panel();
        readonly Panel secondSlot = new Panel();
        readonly Panel innerBarSlot = new Panel();
        readonly Panel outerBarSlot = new Panel();
        readonly Panel mainSlot = new Panel();
        readonly PreviewSplitBar bar;
        Orientation orientation = Orientation.Vertical;
        bool swapped;
        PreviewPane? singlePane;
        // share of the space given to the design pane, whichever side it's on
        double designPosition = lastPosition;
        bool rebuilding;

        public Control Editor { get; }

        public PreviewEditorView Preview { get; }

        /// <summary>Raised when the user changes the orientation, swaps or collapses the panes.</summary>
        public event EventHandler<EventArgs> LayoutChanged;

        /// <summary>Vertical stacks the panes above each other, Horizontal puts them side by side.</summary>
        public Orientation Orientation
        {
            get => orientation;
            set => SetLayout(value, swapped, singlePane);
        }

        /// <summary>Puts the code before the design.</summary>
        public bool Swapped
        {
            get => swapped;
            set => SetLayout(orientation, value, singlePane);
        }

        /// <summary>The only pane shown, or null to show both.</summary>
        public PreviewPane? SinglePane
        {
            get => singlePane;
            set => SetLayout(orientation, swapped, value);
        }

        /// <summary>Share of the space given to the design pane when split.</summary>
        public double RelativePosition
        {
            get => designPosition;
            set
            {
                designPosition = value;
                ApplyPosition();
            }
        }

        public PreviewEditorViewSplitter(Control editor, IDesignHost designHost, Func<string> getCode)
        {
            Preview = new PreviewEditorView(designHost, getCode);
            Editor = editor;

            splitter = new Splitter
            {
                FixedPanel = SplitterFixedPanel.None,
                Panel1 = firstSlot,
                Panel2 = new TableLayout(innerBarSlot, new TableRow(secondSlot) { ScaleHeight = true })
            };
            splitter.PositionChanged += Splitter_PositionChanged;

            bar = new PreviewSplitBar(this);
            Content = new TableLayout(new TableRow(mainSlot) { ScaleHeight = true }, outerBarSlot);
            Rebuild();
        }

        /// <summary>Applies a whole layout at once, such as one restored from settings.</summary>
        public void SetLayout(Orientation orientation, bool swapped, PreviewPane? singlePane)
        {
            if (this.orientation == orientation && this.swapped == swapped && this.singlePane == singlePane)
                return;
            this.orientation = orientation;
            this.swapped = swapped;
            this.singlePane = singlePane;
            Rebuild();
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }

        internal void TabClicked(PreviewPane pane)
        {
            SinglePane = singlePane == pane ? (PreviewPane?)null : pane;
        }

        // keeps the first pane, so swapping picks which one collapses
        internal void ToggleCollapsed() => SinglePane = singlePane != null ? (PreviewPane?)null : swapped ? PreviewPane.Code : PreviewPane.Design;

        void Rebuild()
        {
            rebuilding = true;
            // detach everything first so each control has one parent when re-added
            mainSlot.Content = null;
            firstSlot.Content = null;
            secondSlot.Content = null;
            innerBarSlot.Content = null;
            outerBarSlot.Content = null;

            if (singlePane != null)
            {
                mainSlot.Content = singlePane == PreviewPane.Design ? Preview : Editor;
                outerBarSlot.Content = bar;
            }
            else
            {
                splitter.Orientation = orientation;
                firstSlot.Content = swapped ? Editor : Preview;
                secondSlot.Content = swapped ? Preview : Editor;
                // stacked panes keep the bar on the split line, like VS's XAML designer
                if (orientation == Orientation.Vertical)
                    innerBarSlot.Content = bar;
                else
                    outerBarSlot.Content = bar;
                mainSlot.Content = splitter;
                ApplyPosition();
            }
            bar.UpdateState();
            rebuilding = false;
        }

        void ApplyPosition()
        {
            var old = rebuilding;
            rebuilding = true;
            splitter.RelativePosition = swapped ? 1 - designPosition : designPosition;
            rebuilding = old;
        }

        void Splitter_PositionChanged(object sender, EventArgs e)
        {
            if (rebuilding || singlePane != null || !splitter.Loaded)
                return;
            designPosition = swapped ? 1 - splitter.RelativePosition : splitter.RelativePosition;
            lastPosition = designPosition;
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Preview?.Invalidate();
        }
    }
}
