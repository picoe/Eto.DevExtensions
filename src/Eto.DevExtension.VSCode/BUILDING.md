# Building the VS Code extension

To build the Eto.macOS preview host, install the pinned macOS workload with Xcode 26.6, from the repo root:
`sudo dotnet workload install macos --from-rollback-file workloads.json`

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
