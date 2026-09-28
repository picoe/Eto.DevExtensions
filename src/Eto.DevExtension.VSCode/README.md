# Eto.Forms Designer

A live preview, IntelliSense and syntax highlighting for building cross-platform desktop apps with
[Eto.Forms](https://github.com/picoe/Eto) in VS Code.

## Features

- **Live preview** of your views beside the editor, whether they're written in XAML (`.xeto`), JSON (`.jeto`)
  or code (`.eto.cs`). It redraws as you type and after each build, and shows your own custom controls once the
  project is built. Drag the corner handle to try other sizes, and pick which platform and theme draw it from the drop downs.
- **IntelliSense** in `.xeto` and `.jeto` files for controls, properties, events and values, including your own
  controls. Picking one of your controls in a `.xeto` file adds the `xmlns` it needs.
- **Hover documentation** from the Eto.Forms API docs.
- **Syntax highlighting** for `.xeto` and `.jeto` files.

Everything matches the Eto.Forms version your project uses, so you see the API you're building against.

To open the preview, click the preview button in the editor's title bar, or run **Eto: Open Preview to the Side**.

## Requirements

- The [.NET 8 runtime](https://dotnet.microsoft.com/download) or newer.
- On Windows, the preview also needs the .NET Desktop Runtime 8 or newer.
- On Linux, the preview needs GTK 3.

The preview runs your project's code in a separate process, so it can't affect VS Code. It can draw with WPF or
Windows Forms on Windows, macOS or Mac64 on macOS, and GTK anywhere GTK 3 is installed (on macOS, from Homebrew
or MacPorts). **Auto** picks the one your solution uses.

## Settings

| Setting | Description |
| --- | --- |
| `eto.dotnetPath` | Path to the `dotnet` executable. Uses `dotnet` from `PATH` when empty. |
| `eto.languageServer.path` | Path to `Eto.DevExtension.LanguageServer.dll`. Uses the bundled copy when empty. |
| `eto.previewHost.path` | Path to `Eto.DevExtension.PreviewHost.dll`. Uses the bundled copy when empty. Not used for Eto.macOS previews. |
| `eto.etoAssemblyPath` | Folder containing the `Eto.dll` to complete against. Detected from the project when empty. |
| `eto.trace.server` | Logs the traffic between VS Code and the language server. |

Only one Eto.Forms version is loaded at a time. To pick up a different project's version, or after changing
these settings, run **Eto: Restart Language Server**.

## About Eto.Forms

Eto.Forms is a .NET UI framework that lets you write your user interface once and run it on Windows, macOS and
Linux. Each platform uses its own native toolkit (WPF or Windows Forms on Windows, Cocoa on macOS and GTK on
Linux), so your app looks and feels at home everywhere.

When you need more, you can use each platform's own features directly, or build your own controls with a separate
implementation for each platform. Version 2.12 adds app-wide light and dark themes that can follow the system setting.

## Links

- [Eto.Forms on GitHub](https://github.com/picoe/Eto)
- [Eto.Forms wiki](https://github.com/picoe/Eto/wiki)
- [Report an issue with this extension](https://github.com/picoe/Eto.DevExtensions/issues)
