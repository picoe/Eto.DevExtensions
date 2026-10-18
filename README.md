# Eto.Forms Developer Extensions

Templates, a live preview and IntelliSense for building cross-platform desktop apps with
[Eto.Forms](https://github.com/picoe/Eto), for Visual Studio, VS Code (and compatible editors) and Rider.

## Getting the extensions

| Editor | Where to get it |
| --- | --- |
| Visual Studio 2022 and 2026 (Windows) | [Visual Studio Marketplace](https://marketplace.visualstudio.com/items?itemName=CurtisWensley.EtoDevExtensionVisualStudio2022), or **Extensions > Manage Extensions** and search for *Eto.Forms* |
| VS Code | [Visual Studio Marketplace](https://marketplace.visualstudio.com/items?itemName=CurtisWensley.eto-forms-designer), or search for *Eto.Forms Designer* in the Extensions view |
| VSCodium, Cursor, Windsurf and other VS Code forks | [Open VSX](https://open-vsx.org/extension/CurtisWensley/eto-forms-designer) |
| Rider 2025.1 or newer | Not on the JetBrains Marketplace yet. Download the `.zip` from the [latest release](https://github.com/picoe/Eto.DevExtensions/releases/latest) and use **Settings > Plugins > ⚙ > Install Plugin from Disk** |

Every [release](https://github.com/picoe/Eto.DevExtensions/releases) also has the `.vsix` files, for installing by hand
or trying a pre-release before it reaches the marketplaces. In VS Code use **Extensions: Install from VSIX...**; in
Visual Studio, double-click the `.vsix`.

## What you get

- **Live preview** of `.xeto` (XAML), `.jeto` (JSON) and `.eto.cs` (code) views beside the editor. It redraws as you
  type and after each build. Pick which platform (WPF, WinForms, macOS, GTK) and theme draws it, and drag the corner
  handle to try other sizes.
- **IntelliSense** in `.xeto` and `.jeto` files for controls, properties, events, values and `{Binding}` paths,
  matched to the Eto.Forms version your project uses.
- **Event handlers**: completing an event lists the matching methods in the code behind, or adds a new one. Go to
  definition jumps from the handler name to the method.
- **Hover documentation** and **syntax highlighting**.
- **Project and item templates** (Visual Studio only) for C#, F# and Visual Basic.

Editor-specific details and settings are in each extension's readme:
[VS Code](src/Eto.DevExtension.VSCode/README.md), [Rider](src/Eto.DevExtension.Rider/README.md),
[Visual Studio](src/Eto.DevExtension.VisualStudio.Windows/overview.md).

## Requirements

- The [.NET 8 runtime](https://dotnet.microsoft.com/download) or newer (VS Code and Rider).
- On Windows, the preview also needs the .NET Desktop Runtime 8 or newer. On Linux, it needs GTK 3.

## Tips

### Build to see your own controls

The preview and IntelliSense read your project's compiled output, so your own controls, view models and new
properties show up after you build. Until the first build only the built-in Eto.Forms controls are known.

### File naming

The preview and code behind are found by file name:

| View | Code behind |
| --- | --- |
| `MyForm.xeto` | `MyForm.xeto.cs` |
| `MyForm.jeto` | `MyForm.jeto.cs` |
| `MyForm.eto.cs` | `MyForm.cs` |

An `.eto.cs` file is compiled on its own for the preview, so it should hold a single class derived from a control
(e.g. `Panel`, `Dialog` or `Form`) with a parameterless constructor that builds the UI, as the templates do.

### Sample data in the preview (design data context)

Views that use `{Binding}` usually look empty in the preview because nothing sets their `DataContext`. Give the
view a **design data context** to fill it with sample data. It's only used by the preview and IntelliSense, never when
your app runs, and it also makes `{Binding ...}` complete the properties of that type.

This needs Eto.Forms 2.13 or newer in your project.

In a `.xeto` file, add the `d` namespace and a `d:DataContext`:

```xml
<Panel xmlns="http://schema.picoe.ca/eto.forms"
       xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
       xmlns:d="http://schema.picoe.ca/eto.forms/design"
       xmlns:local="clr-namespace:MyApp;assembly=MyApp"
       x:Class="MyApp.MyPanel"
       d:DataContext="{d:DesignInstance local:MyViewModel}">
  <TextBox Text="{Binding Name}" />
</Panel>
```

In a `.jeto` file, give either the type name, or an object with a `$type` and the sample values to use:

```json
{
  "$type": "Panel",
  "d:DataContext": "MyApp.MyViewModel, MyApp",
  "Content": { "$type": "TextBox", "Text": "{Binding Name}" }
}
```

```json
"d:DataContext": { "$type": "MyApp.MyViewModel, MyApp", "Name": "Sample name" }
```

Things to know:

- The type needs a public parameterless constructor to show sample data. Without one, `{Binding}` still completes
  its properties but the preview stays empty.
- Only the file being previewed uses its `d:DataContext`. A user control inside it gets its data from the parent,
  as it would at runtime.
- In `.eto.cs` views, or anywhere else in your code, check `Control.IsDesignMode` to tell when you're running in the
  preview, e.g. to set `DataContext = new MyViewModel { Name = "Sample name" }` only there.

### Only one Eto.Forms version at a time

In VS Code and Rider the language server loads one Eto.Forms version. After switching to a project that uses a
different version, run **Eto: Restart Language Server** (VS Code) or **Tools > Restart Eto Language Server** (Rider).

## Building and contributing

See [BUILDING.md](src/Eto.DevExtension.VSCode/BUILDING.md) for the VS Code extension and the
[Rider readme](src/Eto.DevExtension.Rider/README.md#building) for Rider. The Visual Studio extension builds from
[Eto.DevExtension.VisualStudio.Windows.slnx](src/Eto.DevExtension.VisualStudio.Windows.slnx) on Windows.

Please report problems on the [issue tracker](https://github.com/picoe/Eto.DevExtensions/issues).

### Releasing

1. Close a milestone named with the version, e.g. `2.13.0`. This builds all the extensions and makes a GitHub
   pre-release with them.
2. Try the pre-release, then edit it and untick **Set as a pre-release**. This publishes to the Visual Studio
   Marketplace and Open VSX.

If the build fails, fix it on `main`, then reopen and close the milestone to try again.

## License

[MIT](LICENSE)
