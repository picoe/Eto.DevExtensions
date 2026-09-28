using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Eto.DevExtension.PreviewHost
{
	/// <summary>Switches the Eto theme previews are drawn with. Use on the UI thread.</summary>
	/// <remarks>Themes need Eto 2.12 or newer, older projects only ever get the default.</remarks>
	class PreviewThemes
	{
		readonly PreviewPlatform platform;
		readonly HostTheme hostTheme;
		readonly Action<string> log;
		bool supported = true;
		bool loaded;
		// Eto.Forms.Theme objects, kept untyped so older Eto can still load this class
		object initial;
		object current;
		List<object> all;

		public PreviewThemes(PreviewPlatform platform, HostTheme hostTheme, Action<string> log)
		{
			this.platform = platform;
			this.hostTheme = hostTheme;
			this.log = log;
		}

		/// <summary>Names of the themes the platform offers, or empty when it has none.</summary>
		public string[] Names { get; private set; } = Array.Empty<string>();

		/// <summary>Name of the theme now in use, or null when unknown.</summary>
		public string Current { get; private set; }

		/// <param name="name">A name from <see cref="Names"/>, or null for the platform's default.</param>
		public void Apply(string name)
		{
			if (!supported)
				return;
			try
			{
				ApplyTheme(name);
			}
			catch (Exception ex) when (ex is TypeLoadException || ex is MissingMemberException)
			{
				supported = false;
				Names = Array.Empty<string>();
				Current = null;
			}
			catch (Exception ex)
			{
				log($"Could not apply the {name ?? "default"} theme: {ex.Message}");
			}
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		void ApplyTheme(string name)
		{
			var app = Eto.Forms.Application.Instance;
			if (!loaded)
			{
				loaded = true;
				initial = current = app.Theme;
				all = LoadThemes().Cast<object>().ToList();
				Names = all.Cast<Eto.Forms.Theme>().Select(r => r.Name).Distinct().ToArray();
			}

			var theme = all.Cast<Eto.Forms.Theme>().FirstOrDefault(r => r.Name == name)
				?? (Eto.Forms.Theme)platform.GetDefaultTheme()
				?? (Eto.Forms.Theme)initial;
			if (theme != null && !ReferenceEquals(theme, current))
			{
				app.Theme = theme;
				current = theme;
			}
			// the editor's panel colour only suits the platform's untouched look
			hostTheme.Themed = !ReferenceEquals(current, initial);
			Current = ((Eto.Forms.Theme)current)?.Name;
		}

		IEnumerable<Eto.Forms.Theme> LoadThemes()
		{
			try
			{
				return Eto.Forms.Themes.AllThemes.Where(r => r != null).ToList();
			}
			catch (Exception ex) when (!(ex is TypeLoadException || ex is MissingMemberException))
			{
				// e.g. Eto.WinForms only has themes when built for .NET 9 or newer
				log($"The platform has no themes: {ex.Message}");
				return Enumerable.Empty<Eto.Forms.Theme>();
			}
		}
	}
}
