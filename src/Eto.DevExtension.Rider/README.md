# Eto.Forms Designer for Rider

Autocompletion, hover documentation and a live preview for [Eto.Forms](https://github.com/picoe/Eto) designer files,
with the same features as the VS Code extension. Needs Rider 2025.1 or newer.

## Features

- A live preview of `.xeto`, `.jeto` and `.eto.cs` files in the **Eto Preview** tool window, which appears once you open
  one of those files. Open it from its button on the right-hand side, **View | Tool Windows**, or **Open Eto Preview** in
  the editor's right-click menu. It follows the file you're editing and redraws as you type and after each build. Drag the corner handle to try other sizes; click the size label to go back to the form's own size
- Pick the platform to draw with from the drop down in the preview, the same as in VS Code. The **Log** tab shows the preview host's output
- Completion of controls, properties, events and values, including your own controls once the project is built
- Hover documentation from the Eto.Forms xml docs
- Syntax highlighting for both file types, using the VS Code extension's grammars

Requires the [.NET 8 runtime](https://dotnet.microsoft.com/download) or newer. On Windows the preview also needs the
.NET Desktop Runtime, and on Linux GTK 3.

## Settings

**Settings | Tools | Eto.Forms Designer** has the same options as VS Code's `eto.*` settings: the `dotnet` executable,
the language server and preview host paths, and the folder of the `Eto.dll` to complete against. Run
**Tools | Restart Eto Language Server** to pick up a different project's Eto version.

## Building

Needs a JDK 17 or newer to run Gradle (`JAVA_HOME` or on `PATH`), and the .NET SDK. Gradle downloads the JDK it compiles with.

```sh
./gradlew runIde       # starts Rider with the plugin, in a separate sandbox
./gradlew buildPlugin  # zip in build/distributions, installable from Settings | Plugins | Install Plugin from Disk
```

Both build the language server and preview hosts first. The Eto.macOS preview host is only built on a Mac with the
.NET macOS workload; without it Mac previews use Eto.Mac64.

The build uses the newest Rider installed in the usual places for Windows, macOS and Linux, including JetBrains Toolbox,
snap and flatpak installs. Set `riderPath` in `gradle.properties` (or the `RIDER_PATH` environment variable) to use a
specific one. With no Rider installed, Rider `riderVersion` is downloaded instead, which is large.

From VS Code, run **Eto.DevExtension.Rider** from Run and Debug. **Eto.DevExtension.Rider (debug)** attaches a debugger
to Rider, which needs the Debugger for Java extension.
