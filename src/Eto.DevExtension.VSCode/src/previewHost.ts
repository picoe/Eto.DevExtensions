import { ChildProcess, spawn } from 'child_process';
import * as vscode from 'vscode';
import {
	createMessageConnection,
	MessageConnection,
	StreamMessageReader,
	StreamMessageWriter
} from 'vscode-jsonrpc/node';
import { HostLaunch } from './hostLaunch';

// long enough for a first render that compiles code, short enough to recover from a hung control
const RENDER_TIMEOUT_MS = 30000;

export interface RenderRequest {
	fileName: string;
	text: string;
	width?: number;
	height?: number;
	scale: number;
}

export interface RenderResult {
	image?: string;
	width?: number;
	height?: number;
	error?: { message: string; details?: string };
	/** Label of the platform it was drawn with. */
	platform?: string;
}

/**
 * The .NET process that draws designer files to images, so project code runs outside VS Code.
 * Renders one request at a time, and restarts the process whenever it exits or stops responding.
 */
export class PreviewHost implements vscode.Disposable {
	private process: ChildProcess | undefined;
	private connection: MessageConnection | undefined;
	private starting: Promise<MessageConnection | undefined> | undefined;
	private queue: Promise<unknown> = Promise.resolve();
	private launchKey: string | undefined;
	// hosts that couldn't start, usually for want of a runtime, so they aren't tried again
	private readonly unavailable = new Set<string>();
	private readonly restarted = new vscode.EventEmitter<void>();

	/** Fires after the project is rebuilt, so previews can be redrawn. */
	readonly onDidRequestRedraw = this.restarted.event;

	constructor(
		private readonly resolveLaunch: (fileName: string) => Promise<HostLaunch | undefined>,
		private readonly output: vscode.OutputChannel
	) { }

	render(request: RenderRequest): Promise<RenderResult> {
		const next = this.queue.then(() => this.renderNow(request));
		this.queue = next.catch(() => undefined);
		return next;
	}

	dispose(): void {
		this.stop();
		this.restarted.dispose();
	}

	private async renderNow(request: RenderRequest): Promise<RenderResult> {
		// a second try covers a host that exited after a rebuild, or that serves another project
		for (let attempt = 0; attempt < 2; attempt++) {
			const launch = await this.resolveLaunch(request.fileName);
			if (!launch) {
				return error('Could not find the Eto preview host. Set "eto.previewHost.path" to point at it.');
			}
			const connection = await this.getConnection(launch);
			if (!connection) {
				return error(`${launch.requirement} See the Eto.Forms Designer output for details.`);
			}

			let timer: NodeJS.Timeout | undefined;
			try {
				const timeout = new Promise<'timeout'>(resolve => timer = setTimeout(() => resolve('timeout'), RENDER_TIMEOUT_MS));
				const result = await Promise.race([
					connection.sendRequest<RenderResult & { restartRequired?: boolean }>('preview/render', { ...request, assemblies: null }),
					timeout
				]);
				if (result === 'timeout') {
					this.stop();
					return error('The preview took too long to draw, so it was stopped.');
				}
				if (result?.restartRequired) {
					this.stop();
					continue;
				}
				return { ...result, platform: launch.platform };
			} catch (e) {
				this.stop();
				if (attempt > 0) {
					return error('The preview host stopped unexpectedly.', `${e}`);
				}
			} finally {
				clearTimeout(timer);
			}
		}
		return error('The preview host could not load the project.');
	}

	private getConnection(launch: HostLaunch): Promise<MessageConnection | undefined> {
		const key = JSON.stringify([launch.command, ...launch.args]);
		if (this.launchKey === key && this.connection && this.process && isRunning(this.process)) {
			return Promise.resolve(this.connection);
		}
		if (this.unavailable.has(key)) {
			return Promise.resolve(undefined);
		}
		this.stop();
		this.launchKey = key;
		this.starting ??= this.start(launch, key).finally(() => this.starting = undefined);
		return this.starting;
	}

	private async start(launch: HostLaunch, key: string): Promise<MessageConnection | undefined> {
		try {
			const child = spawn(launch.command, launch.args, { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true, env: launch.env });
			const failed = new Promise<never>((_, reject) => child.once('error', reject));
			child.stderr?.on('data', data => this.output.append(`${data}`));

			const connection = createMessageConnection(new StreamMessageReader(child.stdout!), new StreamMessageWriter(child.stdin!));
			connection.onNotification('preview/restart', () => {
				this.output.appendLine('Project rebuilt, restarting the preview host.');
				this.restarted.fire();
			});
			connection.onNotification('window/logMessage', (params: { message: string }) => this.output.appendLine(params.message));
			connection.listen();

			this.process = child;
			this.connection = connection;
			await Promise.race([connection.sendRequest('initialize', { processId: process.pid }), failed]);
			connection.sendNotification('initialized', {});
			return connection;
		} catch (e) {
			this.unavailable.add(key);
			this.output.appendLine(`Could not start the Eto preview host using "${launch.command}": ${e}`);
			this.stop();
			return undefined;
		}
	}

	private stop(): void {
		const connection = this.connection;
		const child = this.process;
		this.connection = undefined;
		this.process = undefined;
		this.launchKey = undefined;
		connection?.dispose();
		if (child && isRunning(child)) {
			child.kill();
		}
	}
}

function isRunning(child: ChildProcess): boolean {
	return child.exitCode === null && child.signalCode === null;
}

function error(message: string, details?: string): RenderResult {
	return { error: { message, details: details ?? message } };
}
