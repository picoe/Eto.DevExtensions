# Eto.Forms Designer for VS Code

Autocompletion, hover documentation and a live preview for [Eto.Forms](https://github.com/picoe/Eto) designer files.

## Features

- A live preview of `.xeto`, `.jeto` and `.eto.cs` files beside the editor. Run
  **Eto: Open Preview to the Side**, or use the preview button in the editor title bar. Your own
  controls show up once the project is built, and the preview redraws after each build. Drag the
  corner handle to try the form at other sizes; click the size label to go back to its own size

- Completion of control names, properties, events and property values in `.xeto` (xaml) and `.jeto` (json) files
- Completion of your own controls from the project and the projects it references, once built. In
  `.xeto` files picking one adds the `xmlns` it needs; in `.jeto` files they complete as
  `"Namespace.Type, Assembly"`
- Hover documentation pulled from the Eto.Forms xml docs
- Syntax highlighting for both file types

Completions come from the Eto.Forms version your project references, so they match the API you are
building against. The version is detected from the nearest project file when you open a designer
file, using `obj/project.assets.json` and falling back to the build output. If no reference is found,
the copy bundled with the extension is used.

## Requirements

The [.NET 8 runtime](https://dotnet.microsoft.com/download) or newer, used to run the language server.
On Windows the preview also needs the .NET Desktop Runtime 8 or newer, and on Linux it needs GTK 3.

The preview runs your project's code in a separate process, drawing with the Eto.Forms version the
project uses and the platform for your OS: Eto.Wpf on Windows, Eto.Gtk on Linux, and on macOS
Eto.macOS when a project in the solution references `Eto.Platform.macOS`, otherwise Eto.Mac64.

## Settings

| Setting | Description |
| --- | --- |
| `eto.dotnetPath` | Path to the `dotnet` executable. Uses `dotnet` from `PATH` when empty. |
| `eto.languageServer.path` | Path to `Eto.DevExtension.LanguageServer.dll`. Uses the bundled copy when empty. |
| `eto.previewHost.path` | Path to `Eto.DevExtension.PreviewHost.dll`. Uses the bundled copy when empty. Not used for Eto.macOS previews. |
| `eto.preview.macPlatform` | `auto`, `macOS` or `Mac64`: the platform used for previews on macOS. |
| `eto.etoAssemblyPath` | Folder containing the `Eto.dll` to complete against. Detected from the project when empty. |
| `eto.trace.server` | Logs the traffic between VS Code and the language server. |

Only one Eto.Forms version is loaded per session. After changing any of these settings, or to pick up
a different project, run **Eto: Restart Language Server**.

## Building

```sh
npm install
npm run build:server   # publishes the .NET language server into ./server and the preview hosts into ./preview
npm run compile
```

The Eto.macOS preview host is an app bundle, so it's only built when packaging on a Mac with the .NET
macOS workload. Without it, Mac previews use Eto.Mac64. If the workload rejects your Xcode version, pass
`npm run build:server -- -p:ValidateXcodeVersion=false`.

Press <kbd>F5</kbd> to launch an extension development host. `npm run package` produces a `.vsix` in
`artifacts/vscode`.
