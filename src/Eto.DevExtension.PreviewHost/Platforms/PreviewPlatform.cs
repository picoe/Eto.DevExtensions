using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Eto.DevExtension.PreviewHost
{
	/// <summary>
	/// The Eto platform a preview is drawn with. Pick one with <see cref="Get"/>.
	/// </summary>
	/// <remarks>
	/// The name members are read before Eto is loaded, so only the methods taking or returning objects may touch Eto types.
	/// </remarks>
	abstract class PreviewPlatform
	{
		/// <summary>Name passed with --platform.</summary>
		public abstract string Name { get; }

		/// <summary>Eto platform assembly, e.g. Eto.Wpf.</summary>
		public abstract string AssemblyName { get; }

		/// <summary>NuGet package id holding <see cref="AssemblyName"/>.</summary>
		public abstract string PackageId { get; }

		/// <summary>Assemblies that ship beside <see cref="AssemblyName"/> in its package and must match it.</summary>
		public virtual IEnumerable<string> CompanionNames => Enumerable.Empty<string>();

		/// <summary>Other libraries the platform needs, taken from the project when it has them.</summary>
		public virtual IEnumerable<string> SharedNames => Enumerable.Empty<string>();

		/// <summary>Package lib folders to look in, best first.</summary>
		public abstract IEnumerable<string> GetFrameworks(IEnumerable<string> available);

		/// <summary>Creates the Eto.Platform.</summary>
		public abstract object CreatePlatform();

		/// <summary>Called once the application is running.</summary>
		public virtual void Initialized() { }

		/// <summary>Draws the control to a PNG. <paramref name="control"/> is an Eto.Forms.Control, and is disposed after.</summary>
		public abstract Task<RenderResult> CaptureAsync(object control, RenderRequest request);

		// newest first, but never newer than the runtime we're on
		protected static IEnumerable<int> Majors()
		{
			for (var major = Environment.Version.Major; major >= 5; major--)
				yield return major;
		}

		public static PreviewPlatform Get(string name)
		{
			if (string.IsNullOrEmpty(name))
				name = Default;
			switch (name.ToLowerInvariant())
			{
#if WINDOWS
				case "wpf":
					return new WpfPreviewPlatform();
#elif MACOS
				case "macos":
					return new MacPreviewPlatform();
#else
				case "gtk":
					return new GtkPreviewPlatform();
				case "mac64":
					return new MacPreviewPlatform();
#endif
				default:
					throw new NotSupportedException($"The {name} platform isn't supported by this preview host");
			}
		}

#if WINDOWS
		static string Default => "Wpf";
#elif MACOS
		static string Default => "macOS";
#else
		static string Default => OperatingSystem.IsMacOS() ? "Mac64" : "Gtk";
#endif
	}
}
