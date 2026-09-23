import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import {
	LanguageClient,
	LanguageClientOptions,
	ServerOptions,
	TransportKind
} from 'vscode-languageclient/node';
import { isPreviewable, PreviewPanel } from './preview';
import { PreviewHost } from './previewHost';

const SERVER_DLL = 'Eto.DevExtension.LanguageServer.dll';
const PREVIEW_HOST_DLL = 'Eto.DevExtension.PreviewHost.dll';

let client: LanguageClient | undefined;
let output: vscode.OutputChannel;
let previewHost: PreviewHost | undefined;

export async function activate(context: vscode.ExtensionContext): Promise<void> {
	output = vscode.window.createOutputChannel('Eto.Forms Designer');
	context.subscriptions.push(output);

	context.subscriptions.push(
		vscode.commands.registerCommand('eto.restartLanguageServer', () => restart(context)),
		vscode.commands.registerCommand('eto.openPreview', (uri?: vscode.Uri) => openPreview(context, uri)),
		{ dispose: () => previewHost?.dispose() }
	);

	// the server pins one Eto version per session, so these need a fresh process
	context.subscriptions.push(
		vscode.workspace.onDidChangeConfiguration(e => {
			if (e.affectsConfiguration('eto.dotnetPath')
				|| e.affectsConfiguration('eto.languageServer.path')
				|| e.affectsConfiguration('eto.etoAssemblyPath')) {
				restart(context);
			}
		})
	);

	await start(context);
}

export async function deactivate(): Promise<void> {
	await stop();
}

async function restart(context: vscode.ExtensionContext): Promise<void> {
	await stop();
	await start(context);
}

async function stop(): Promise<void> {
	const running = client;
	client = undefined;
	if (running) {
		await running.stop();
	}
}

async function start(context: vscode.ExtensionContext): Promise<void> {
	const config = vscode.workspace.getConfiguration('eto');

	const server = resolveServerPath(context, config.get<string>('languageServer.path'));
	if (!server) {
		vscode.window.showErrorMessage(
			`Could not find ${SERVER_DLL}. Set "eto.languageServer.path" to point at it.`
		);
		return;
	}

	const dotnet = config.get<string>('dotnetPath')?.trim() || 'dotnet';
	const serverOptions: ServerOptions = {
		run: { command: dotnet, args: [server], transport: TransportKind.stdio },
		debug: { command: dotnet, args: [server], transport: TransportKind.stdio }
	};

	const etoAssemblyPath = config.get<string>('etoAssemblyPath')?.trim();
	const clientOptions: LanguageClientOptions = {
		documentSelector: [
			{ scheme: 'file', language: 'xeto' },
			{ scheme: 'file', language: 'jeto' }
		],
		outputChannel: output,
		initializationOptions: etoAssemblyPath ? { etoAssemblyPath } : {}
	};

	const starting = new LanguageClient('eto', 'Eto.Forms Designer', serverOptions, clientOptions);
	client = starting;
	try {
		await starting.start();
		context.subscriptions.push(starting);
	} catch (error) {
		client = undefined;
		output.appendLine(`${error}`);
		vscode.window.showErrorMessage(
			`Could not start the Eto language server using "${dotnet}". Install the .NET 8 runtime or set "eto.dotnetPath".`
		);
	}
}

async function openPreview(context: vscode.ExtensionContext, uri?: vscode.Uri): Promise<void> {
	const document = uri
		? await vscode.workspace.openTextDocument(uri)
		: vscode.window.activeTextEditor?.document;
	if (!document || !isPreviewable(document)) {
		vscode.window.showInformationMessage('Open a .xeto, .jeto or .eto.cs file to preview it.');
		return;
	}

	if (!previewHost) {
		const config = vscode.workspace.getConfiguration('eto');
		const hostPath = resolvePath(context, config.get<string>('previewHost.path'), 'preview', PREVIEW_HOST_DLL,
			path.join('Eto.DevExtension.PreviewHost', 'Debug', 'net8.0-windows'));
		if (!hostPath) {
			vscode.window.showErrorMessage(`Could not find ${PREVIEW_HOST_DLL}. Set "eto.previewHost.path" to point at it.`);
			return;
		}
		previewHost = new PreviewHost(config.get<string>('dotnetPath')?.trim() || 'dotnet', hostPath, output);
	}
	PreviewPanel.show(previewHost, document);
}

function resolveServerPath(context: vscode.ExtensionContext, configured?: string): string | undefined {
	return resolvePath(context, configured, 'server', SERVER_DLL, path.join('Eto.DevExtension.LanguageServer', 'Debug', 'net8.0'));
}

/**
 * @param folder Where the dll is bundled in the extension.
 * @param buildFolder Where a debug build puts it under artifacts, for running out of the source tree.
 */
function resolvePath(context: vscode.ExtensionContext, configured: string | undefined, folder: string, dll: string, buildFolder: string): string | undefined {
	const candidates = [
		configured?.trim(),
		path.join(context.extensionPath, folder, dll),
		path.join(context.extensionPath, '..', '..', 'artifacts', buildFolder, dll)
	];

	for (const candidate of candidates) {
		if (!candidate) {
			continue;
		}
		const resolved = path.isAbsolute(candidate) ? candidate : path.join(context.extensionPath, candidate);
		if (fs.existsSync(resolved)) {
			return resolved;
		}
	}
	return undefined;
}
