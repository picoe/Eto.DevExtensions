// Packages the extension to artifacts/vscode with the version from Directory.Build.props.
// The version lives there so every extension shares it; package.json only has a placeholder.
const { execFileSync } = require('child_process');
const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '..');
const repo = path.resolve(root, '..', '..');
const out = path.join(repo, 'artifacts', 'vscode');

const props = fs.readFileSync(path.join(repo, 'Directory.Build.props'), 'utf8');
const version = /<DevVersion>(.+?)<\/DevVersion>/.exec(props)[1];

fs.mkdirSync(out, { recursive: true });
// named like the Visual Studio and Rider packages, rather than vsce's default of the extension id
const file = path.join(out, `Eto.DevExtension.VSCode-${version}.vsix`);
execFileSync('npx', ['vsce', 'package', version, '--no-update-package-json', '--out', file], {
	cwd: root,
	stdio: 'inherit',
	shell: process.platform === 'win32'
});
