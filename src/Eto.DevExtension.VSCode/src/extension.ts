import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import {
	LanguageClient,
	LanguageClientOptions,
	ServerOptions,
	TransportKind
} from 'vscode-languageclient/node';

const SERVER_DLL = 'Eto.DevExtension.LanguageServer.dll';

let client: LanguageClient | undefined;
let output: vscode.OutputChannel;

export async function activate(context: vscode.ExtensionContext): Promise<void> {
	output = vscode.window.createOutputChannel('Eto.Forms Designer');
	context.subscriptions.push(output);

	context.subscriptions.push(
		vscode.commands.registerCommand('eto.restartLanguageServer', () => restart(context))
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

function resolveServerPath(context: vscode.ExtensionContext, configured?: string): string | undefined {
	const candidates = [
		configured?.trim(),
		path.join(context.extensionPath, 'server', SERVER_DLL),
		// running out of the source tree during development
		path.join(context.extensionPath, '..', '..', 'artifacts', 'Eto.DevExtension.LanguageServer', 'Debug', 'net8.0', SERVER_DLL)
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
