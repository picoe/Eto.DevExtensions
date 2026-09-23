# Eto.Forms Designer for VS Code

Autocompletion and hover documentation for [Eto.Forms](https://github.com/picoe/Eto) designer files.

## Features

- Completion of control names, properties, events and property values in `.xeto` (xaml) and `.jeto` (json) files
- Hover documentation pulled from the Eto.Forms xml docs
- Syntax highlighting for both file types

Completions come from the Eto.Forms version your project references, so they match the API you are
building against. The version is detected from the nearest project file when you open a designer
file, using `obj/project.assets.json` and falling back to the build output. If no reference is found,
the copy bundled with the extension is used.

## Requirements

The [.NET 8 runtime](https://dotnet.microsoft.com/download) or newer, used to run the language server.

## Settings

| Setting | Description |
| --- | --- |
| `eto.dotnetPath` | Path to the `dotnet` executable. Uses `dotnet` from `PATH` when empty. |
| `eto.languageServer.path` | Path to `Eto.DevExtension.LanguageServer.dll`. Uses the bundled copy when empty. |
| `eto.etoAssemblyPath` | Folder containing the `Eto.dll` to complete against. Detected from the project when empty. |
| `eto.trace.server` | Logs the traffic between VS Code and the language server. |

Only one Eto.Forms version is loaded per session. After changing any of these settings, or to pick up
a different project, run **Eto: Restart Language Server**.

## Building

```sh
npm install
npm run build:server   # publishes the .NET language server into ./server
npm run compile
```

Press <kbd>F5</kbd> to launch an extension development host. `npm run package` produces a `.vsix` in
`artifacts/vscode`.
