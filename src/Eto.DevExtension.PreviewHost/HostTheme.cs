using System.Collections.Generic;
using System.Linq;
using Eto.Designer;
using Eto.Drawing;

namespace Eto.DevExtension.PreviewHost
{
	/// <summary>Theme colors sent by the editor, so previews match it.</summary>
	class HostTheme : IPlatformTheme
	{
		readonly IReadOnlyDictionary<string, string> colors;

		public HostTheme(IReadOnlyDictionary<string, string> colors) => this.colors = colors;

		Color Get(string name, Color fallback) =>
			colors.TryGetValue(name, out var value) && Color.TryParse(value, out var color) ? color : fallback;

		public Color ProjectBackground => Get(nameof(ProjectBackground), SystemColors.Control);
		public Color ProjectForeground => Get(nameof(ProjectForeground), SystemColors.ControlText);
		public Color ProjectDialogBackground => Get(nameof(ProjectDialogBackground), SystemColors.Control);
		public Color ErrorForeground => Get(nameof(ErrorForeground), Colors.Red);
		public Color SummaryBackground => Get(nameof(SummaryBackground), SystemColors.Control);
		public Color SummaryForeground => Get(nameof(SummaryForeground), SystemColors.ControlText);
		public Color DesignerBackground => Get(nameof(DesignerBackground), SystemColors.Control);
		// the platform's window colour, so panels look like forms in dark mode, but Gtk can report it as transparent
		public Color DesignerPanel => Get(nameof(DesignerPanel), SystemColors.WindowBackground is { A: > 0 } window ? window : Color.FromRgb(0xF0F0F0));
		public Color SplitBarBackground => Get(nameof(SplitBarBackground), SystemColors.Control);
		public Color SplitBarForeground => Get(nameof(SplitBarForeground), SystemColors.ControlText);
		public Color SplitBarHoverBackground => Get(nameof(SplitBarHoverBackground), SystemColors.Highlight);
		public Color SplitBarSelectedBackground => Get(nameof(SplitBarSelectedBackground), SystemColors.Highlight);
		public Color SplitBarSelectedForeground => Get(nameof(SplitBarSelectedForeground), SystemColors.HighlightText);
		public IEnumerable<PlatformColor> AllColors => Enumerable.Empty<PlatformColor>();
	}
}
