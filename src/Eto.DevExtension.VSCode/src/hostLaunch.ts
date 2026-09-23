import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';

const HOST_DLL = 'Eto.DevExtension.PreviewHost.dll';
const MAC_APP = 'Eto Preview Host.app';
const MAC_EXECUTABLE = path.join(MAC_APP, 'Contents', 'MacOS', 'Eto.DevExtension.PreviewHost');

// scanning projects on every keystroke is too slow, but an edited project should still be noticed
const CACHE_MS = 10000;

export const AUTO = 'auto';

/** A platform the preview can draw with. */
export interface PlatformOption {
	id: string;
	label: string;
}

/** How to start a preview host process. */
export interface HostLaunch {
	command: string;
	args: string[];
	env?: NodeJS.ProcessEnv;
	/** Label of the platform it draws with. */
	platform: string;
	/** Shown when the process can't be started. */
	requirement: string;
}

interface Platform extends PlatformOption {
	/** Matches a project that uses this platform, from a package or project reference. */
	reference: RegExp;
	launch(): HostLaunch | undefined;
}

/**
 * Picks the preview host for a document. The platforms offered depend on the OS and what's installed,
 * and Auto picks the first one the solution references, otherwise the OS's usual one.
 */
export class HostLauncher {
	private readonly autoCache = new Map<string, { time: number; platform: Platform }>();
	private lastMessage: string | undefined;

	constructor(private readonly extensionPath: string, private readonly output: vscode.OutputChannel) { }

	/** Platforms available on this machine, best first. */
	getPlatforms(): PlatformOption[] {
		return this.getAvailable().map(r => ({ id: r.id, label: r.label }));
	}

	/** @param choice A platform id, or {@link AUTO}. */
	async resolve(fileName: string, choice: string): Promise<HostLaunch | undefined> {
		const platforms = this.getAvailable();
		const platform = platforms.find(r => r.id === choice) ?? await this.detect(fileName, platforms);
		return platform?.launch();
	}

	private getAvailable(): Platform[] {
		const config = vscode.workspace.getConfiguration('eto');
		const dotnet = config.get<string>('dotnetPath')?.trim() || 'dotnet';
		const configured = config.get<string>('previewHost.path')?.trim();
		const netHost = () => this.find(configured, path.join('preview', 'net', HOST_DLL), path.join('Eto.DevExtension.PreviewHost', 'Debug', 'net8.0', HOST_DLL));
		const platforms: Platform[] = [];

		if (process.platform === 'win32') {
			const windowsHost = () => this.find(configured, path.join('preview', 'win', HOST_DLL), path.join('Eto.DevExtension.PreviewHost', 'Debug', 'net8.0-windows', HOST_DLL));
			const requirement = 'The preview needs the .NET 8 Desktop Runtime (or newer).';
			platforms.push(
				{ id: 'Wpf', label: 'WPF', reference: /Eto\.Platform\.Wpf\b|Eto\.Wpf\.csproj/i, launch: () => withDotnet(dotnet, windowsHost(), 'Wpf', 'WPF', requirement) },
				{ id: 'WinForms', label: 'WinForms', reference: /Eto\.Platform\.Windows\b|Eto\.WinForms\.csproj/i, launch: () => withDotnet(dotnet, windowsHost(), 'WinForms', 'WinForms', requirement) }
			);
		}
		if (process.platform === 'darwin') {
			const app = this.find(undefined, path.join('preview', 'macos', MAC_EXECUTABLE), path.join('Eto.DevExtension.PreviewHost.macOS', 'Debug', 'net10.0-macos', MAC_EXECUTABLE));
			if (app) {
				platforms.push({
					id: 'macOS', label: 'macOS', reference: /Eto\.Platform\.macOS\b|Eto\.macOS\.csproj/i,
					launch: () => ({ command: app, args: [], platform: 'macOS', requirement: 'The Eto.macOS preview host could not be started.' })
				});
			}
			platforms.push({
				id: 'Mac64', label: 'Mac64', reference: /Eto\.Platform\.Mac64\b|Eto\.Mac64\.csproj/i,
				launch: () => withDotnet(dotnet, netHost(), 'Mac64', 'Mac64', 'The preview needs the .NET 8 runtime (or newer).')
			});
		}

		const gtk = findGtk();
		if (gtk) {
			platforms.push({
				id: 'Gtk', label: 'Gtk', reference: /Eto\.Platform\.Gtk\b|Eto\.Gtk\.csproj/i,
				launch: () => {
					const launch = withDotnet(dotnet, netHost(), 'Gtk', 'Gtk', 'The preview needs the .NET 8 runtime (or newer) and GTK 3.');
					return launch && { ...launch, env: gtk.env };
				}
			});
		}
		return platforms;
	}

	private async detect(fileName: string, platforms: Platform[]): Promise<Platform | undefined> {
		const key = path.dirname(fileName);
		const cached = this.autoCache.get(key);
		if (cached && Date.now() - cached.time < CACHE_MS && platforms.some(r => r.id === cached.platform.id)) {
			return cached.platform;
		}
		if (platforms.length === 0) {
			return undefined;
		}

		const texts = (findSolutionProjects(fileName) ?? await findWorkspaceProjects()).map(readText);
		const referenced = platforms.find(platform => texts.some(text => platform.reference.test(text)));
		const platform = referenced ?? platforms[0];
		this.autoCache.set(key, { time: Date.now(), platform });
		this.log(referenced
			? `Previewing with ${platform.label}, as the solution uses it.`
			: `Previewing with ${platform.label}, as the solution doesn't reference an Eto platform available here.`);
		return platform;
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

	private log(message: string): void {
		if (message !== this.lastMessage) {
			this.lastMessage = message;
			this.output.appendLine(message);
		}
	}
}

function withDotnet(dotnet: string, dll: string | undefined, id: string, label: string, requirement: string): HostLaunch | undefined {
	return dll ? { command: dotnet, args: [dll, '--platform', id], platform: label, requirement } : undefined;
}

/** GTK 3, and the environment the host needs to find it, or undefined when it isn't installed. */
function findGtk(): { env?: NodeJS.ProcessEnv } | undefined {
	if (process.platform === 'darwin') {
		// Homebrew (Apple silicon, then Intel) or MacPorts, none of which are on the default library path
		const dir = ['/opt/homebrew/lib', '/usr/local/lib', '/opt/local/lib'].find(r => fs.existsSync(path.join(r, 'libgtk-3.0.dylib')));
		return dir ? { env: { ...process.env, DYLD_FALLBACK_LIBRARY_PATH: joinPath(dir, process.env.DYLD_FALLBACK_LIBRARY_PATH) } } : undefined;
	}
	if (process.platform === 'win32') {
		// where GtkSharp's build installs it, or anywhere on PATH such as MSYS2
		const installed = process.env.LOCALAPPDATA ? path.join(process.env.LOCALAPPDATA, 'Gtk', '3.24.24') : undefined;
		const dir = [installed, ...(process.env.PATH ?? '').split(path.delimiter)].find(r => r && fs.existsSync(path.join(r, 'libgtk-3-0.dll')));
		return dir ? { env: { ...process.env, PATH: joinPath(dir, process.env.PATH) } } : undefined;
	}
	// the desktop's own toolkit, so assume it's there
	return {};
}

function joinPath(first: string, rest: string | undefined): string {
	return rest ? `${first}${path.delimiter}${rest}` : first;
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
