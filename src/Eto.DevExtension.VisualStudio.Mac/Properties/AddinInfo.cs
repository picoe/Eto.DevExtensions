using System;
using Mono.Addins;
using Mono.Addins.Description;

[assembly:Addin(
	"Mac", 
	Namespace = "Eto.DevExtension.VisualStudio",
	Version = "2.7.0.0"
)]

[assembly:AddinName("Eto.Forms VS Extension")]
[assembly:AddinCategory("Eto.Forms")]
[assembly:AddinDescription(@"Extension to easily start developing with the Eto.Forms cross platform UI framework.

Provides:

- File and Project templates for C#, VB.NET, and F#.
- Autocomplete for the Xaml view editor.
- Xaml, Json, and Code based live form preview.")]
[assembly:AddinAuthor("Curtis Wensley")]