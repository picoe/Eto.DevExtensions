import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';

const HOST_DLL = 'Eto.DevExtension.PreviewHost.dll';
const MAC_APP = 'Eto Preview Host.app';
const MAC_EXECUTABLE = path.join(MAC_APP, 'Contents', 'MacOS', 'Eto.DevExtension.PreviewHost');

// scanning projects on every keystroke is too slow, but an edited project should still be noticed
const CACHE_MS = 10000;

type MacPlatform = 'macOS' | 'Mac64';

/** How to start a preview host process. */
export interface HostLaunch {
	command: string;
	args: string[];
	/** Shown when the process can't be started. */
	requirement: string;
}

/**
 * Picks the preview host for a document: Wpf on Windows, Gtk on Linux, and on macOS either the
 * Eto.macOS app bundle or Mac64, depending on what the solution uses.
 */
export class HostLauncher {
	private readonly macCache = new Map<string, { time: number; platform: MacPlatform }>();
	private lastMessage: string | undefined;

	constructor(private readonly extensionPath: string, private readonly output: vscode.OutputChannel) { }

	async resolve(fileName: string): Promise<HostLaunch | undefined> {
		const config = vscode.workspace.getConfiguration('eto');
		const dotnet = config.get<string>('dotnetPath')?.trim() || 'dotnet';
		const configured = config.get<string>('previewHost.path')?.trim();

		if (process.platform === 'win32') {
			const dll = this.find(configured, path.join('preview', 'win', HOST_DLL), path.join('Eto.DevExtension.PreviewHost', 'Debug', 'net8.0-windows', HOST_DLL));
			return withDotnet(dotnet, dll, [], 'The preview needs the .NET 8 Desktop Runtime (or newer).');
		}

		const dll = this.find(configured, path.join('preview', 'net', HOST_DLL), path.join('Eto.DevExtension.PreviewHost', 'Debug', 'net8.0', HOST_DLL));
		if (process.platform !== 'darwin') {
			return withDotnet(dotnet, dll, ['--platform', 'Gtk'], 'The preview needs the .NET 8 runtime (or newer) and GTK 3.');
		}

		if (await this.getMacPlatform(fileName) === 'macOS') {
			const app = this.find(undefined, path.join('preview', 'macos', MAC_EXECUTABLE), path.join('Eto.DevExtension.PreviewHost.macOS', 'Debug', 'net10.0-macos', MAC_EXECUTABLE));
			if (app) {
				return { command: app, args: [], requirement: 'The Eto.macOS preview host could not be started.' };
			}
			this.log('This copy of the extension has no Eto.macOS preview host, so previewing with Mac64 instead.');
		}
		return withDotnet(dotnet, dll, ['--platform', 'Mac64'], 'The preview needs the .NET 8 runtime (or newer).');
	}

	/**
	 * @param bundled Where the host is in the packaged extension.
	 * @param build Where a debug build puts it under artifacts, for running out of the source tree.
	 */
	private find(configured: string | undefined, bundled: string, build: string): string | undefined {
		const candidates = [
			configured,
			path.join(this.extensionPath, bundled),
			path.join(this.extensionPath, '..', '..', 'artifacts', build)
		];
		for (const candidate of candidates) {
			if (!candidate) {
				continue;
			}
			const resolved = path.isAbsolute(candidate) ? candidate : path.join(this.extensionPath, candidate);
			if (fs.existsSync(resolved)) {
				return resolved;
			}
		}
		return undefined;
	}

	private async getMacPlatform(fileName: string): Promise<MacPlatform> {
		const configured = vscode.workspace.getConfiguration('eto').get<string>('preview.macPlatform');
		if (configured === 'macOS' || configured === 'Mac64') {
			return configured;
		}

		const key = path.dirname(fileName);
		const cached = this.macCache.get(key);
		if (cached && Date.now() - cached.time < CACHE_MS) {
			return cached.platform;
		}

		const projects = findSolutionProjects(fileName) ?? await findWorkspaceProjects();
		let platform: MacPlatform = 'Mac64';
		let reason = 'no project in the solution references Eto.Platform.macOS';
		for (const project of projects) {
			const text = readText(project);
			if (/Eto\.Platform\.macOS\b|Eto\.macOS\.csproj/i.test(text)) {
				platform = 'macOS';
				reason = `${path.basename(project)} references it`;
				break;
			}
		}
		this.macCache.set(key, { time: Date.now(), platform });
		this.log(`Previewing with ${platform === 'macOS' ? 'Eto.macOS' : 'Eto.Mac64'}, as ${reason}. Set "eto.preview.macPlatform" to choose.`);
		return platform;
	}

	private log(message: string): void {
		if (message !== this.lastMessage) {
			this.lastMessage = message;
			this.output.appendLine(message);
		}
	}
}

function withDotnet(dotnet: string, dll: string | undefined, args: string[], requirement: string): HostLaunch | undefined {
	return dll ? { command: dotnet, args: [dll, ...args], requirement } : undefined;
}

/** Projects in the nearest solution above the file, or undefined when there's none. */
function findSolutionProjects(fileName: string): string[] | undefined {
	let dir = path.dirname(fileName);
	for (; ;) {
		const solution = readDir(dir).find(r => /\.slnx?$/i.test(r));
		if (solution) {
			const file = path.join(dir, solution);
			const pattern = solution.toLowerCase().endsWith('.slnx')
				? /<Project\s+Path="([^"]+\.(?:cs|vb|fs)proj)"/gi
				: /^Project\("[^"]*"\)\s*=\s*"[^"]*",\s*"([^"]+\.(?:cs|vb|fs)proj)"/gim;
			return [...readText(file).matchAll(pattern)]
				.map(r => path.resolve(dir, r[1].replace(/\\/g, path.sep)));
		}
		const parent = path.dirname(dir);
		if (parent === dir) {
			return undefined;
		}
		dir = parent;
	}
}

async function findWorkspaceProjects(): Promise<string[]> {
	const files = await vscode.workspace.findFiles('**/*.{csproj,vbproj,fsproj}', '**/{bin,obj,node_modules}/**', 500);
	return files.map(r => r.fsPath);
}

function readDir(dir: string): string[] {
	try {
		return fs.readdirSync(dir);
	} catch {
		return [];
	}
}

function readText(file: string): string {
	try {
		return fs.readFileSync(file, 'utf8');
	} catch {
		return '';
	}
}
