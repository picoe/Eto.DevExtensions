// Publishes the language server to server/ and the preview hosts to preview/ for packaging.
// Extra arguments go to the Eto.macOS host build, e.g. `npm run build:server -- -p:ValidateXcodeVersion=false`.
const { execFileSync } = require('child_process');
const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '..');
const src = path.resolve(root, '..');
const artifacts = path.resolve(src, '..', 'artifacts');
const host = path.join(src, 'Eto.DevExtension.PreviewHost', 'Eto.DevExtension.PreviewHost.csproj');
const preview = path.join(root, 'preview');

function dotnet(...args) {
	execFileSync('dotnet', args, { stdio: 'inherit', cwd: root });
}

fs.rmSync(preview, { recursive: true, force: true });

dotnet('publish', path.join(src, 'Eto.DevExtension.LanguageServer', 'Eto.DevExtension.LanguageServer.csproj'), '-c', 'Release', '-o', path.join(root, 'server'));
dotnet('publish', host, '-c', 'Release', '-f', 'net8.0-windows', '-o', path.join(preview, 'win'));
dotnet('publish', host, '-c', 'Release', '-f', 'net8.0', '-o', path.join(preview, 'net'));

if (process.platform === 'darwin') {
	try {
		dotnet('build', path.join(src, 'Eto.DevExtension.PreviewHost.macOS', 'Eto.DevExtension.PreviewHost.macOS.csproj'), '-c', 'Release', ...process.argv.slice(2));
		const app = 'Eto Preview Host.app';
		fs.mkdirSync(path.join(preview, 'macos'), { recursive: true });
		// ditto keeps the bundle's signature intact
		execFileSync('ditto', [path.join(artifacts, 'Eto.DevExtension.PreviewHost.macOS', 'Release', 'net10.0-macos', app), path.join(preview, 'macos', app)], { stdio: 'inherit' });
	} catch {
		// usually a missing macOS workload or mismatched Xcode, which shouldn't stop the rest from packaging
		console.warn('Could not build the Eto.macOS preview host, so Mac previews will use Eto.Mac64.');
	}
} else {
	console.log('Skipping the Eto.macOS preview host, which only builds on macOS. Mac previews will use Eto.Mac64.');
}
