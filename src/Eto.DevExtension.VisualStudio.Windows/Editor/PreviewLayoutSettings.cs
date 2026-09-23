using Eto.Designer;
using Eto.Forms;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Settings;
using System;
using System.Diagnostics;

namespace Eto.DevExtension.VisualStudio.Windows.Editor
{
	/// <summary>
	/// Remembers how the last changed preview laid out its panes, for newly opened files.
	/// </summary>
	static class PreviewLayoutSettings
	{
		const string CollectionPath = @"Eto.DevExtension\PreviewLayout";

		/// <summary>Applies the saved layout to a newly created splitter. Use on the UI thread.</summary>
		public static void Load(PreviewEditorViewSplitter splitter)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			var store = GetStore();
			if (store == null || !store.CollectionExists(CollectionPath))
				return;
			var orientation = Enum.TryParse<Orientation>(store.GetString(CollectionPath, "Orientation", null), out var o) ? o : splitter.Orientation;
			var swapped = store.GetBoolean(CollectionPath, "Swapped", splitter.Swapped);
			var singlePane = Enum.TryParse<PreviewPane>(store.GetString(CollectionPath, "SinglePane", null), out var p) ? p : (PreviewPane?)null;
			splitter.SetLayout(orientation, swapped, singlePane);
		}

		/// <summary>Saves the splitter's layout. Use on the UI thread.</summary>
		public static void Save(PreviewEditorViewSplitter splitter)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			var store = GetStore();
			if (store == null)
				return;
			store.CreateCollection(CollectionPath);
			store.SetString(CollectionPath, "Orientation", splitter.Orientation.ToString());
			store.SetBoolean(CollectionPath, "Swapped", splitter.Swapped);
			store.SetString(CollectionPath, "SinglePane", splitter.SinglePane?.ToString() ?? string.Empty);
		}

		static WritableSettingsStore GetStore()
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			try
			{
				return new ShellSettingsManager(ServiceProvider.GlobalProvider).GetWritableSettingsStore(SettingsScope.UserSettings);
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"Eto preview: could not open settings: {ex.Message}");
				return null;
			}
		}
	}
}
