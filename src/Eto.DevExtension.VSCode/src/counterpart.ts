import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';

const SWITCH_COMMAND = 'eto.switchDesignerCode';
const CONTEXT_KEY = 'eto.hasCounterpart';
const CODE_EXTENSIONS = ['.cs', '.vb', '.fs'];

/** Files that may pair with this one, best first: the code behind of a designer file, or the designer of a code behind. */
function candidates(fileName: string): string[] {
	const dir = path.dirname(fileName);
	const base = path.basename(fileName);
	let m: RegExpMatchArray | null;
	// MyForm.xeto ↔ MyForm.xeto.cs, or MyForm.cs like the language server accepts
	if ((m = base.match(/^(.+)\.(xeto|jeto)$/i))) {
		return [...CODE_EXTENSIONS.map(r => fileName + r), ...CODE_EXTENSIONS.map(r => path.join(dir, m![1] + r))];
	}
	if ((m = base.match(/^(.+\.(xeto|jeto))\.(cs|vb|fs)$/i))) {
		return [path.join(dir, m[1])];
	}
	// MyForm.eto.cs ↔ MyForm.cs
	if ((m = base.match(/^(.+)\.eto(\.(cs|vb|fs))$/i))) {
		return [path.join(dir, m[1] + m[2])];
	}
	if ((m = base.match(/^(.+)(\.(cs|vb|fs))$/i))) {
		return [path.join(dir, `${m[1]}.eto${m[2]}`), path.join(dir, m[1] + '.xeto'), path.join(dir, m[1] + '.jeto')];
	}
	return [];
}

function findCounterpart(document: vscode.TextDocument | undefined): string | undefined {
	if (document?.uri.scheme !== 'file') {
		return undefined;
	}
	return candidates(document.fileName).find(r => fs.existsSync(r));
}

/** Registers the command that jumps between a designer file and its code behind, shown only where there's one to jump to. */
export function registerSwitchDesignerCode(): vscode.Disposable {
	const updateContext = () =>
		vscode.commands.executeCommand('setContext', CONTEXT_KEY, findCounterpart(vscode.window.activeTextEditor?.document) !== undefined);
	updateContext();

	return vscode.Disposable.from(
		vscode.commands.registerCommand(SWITCH_COMMAND, async (uri?: vscode.Uri) => {
			const document = uri ? await vscode.workspace.openTextDocument(uri) : vscode.window.activeTextEditor?.document;
			const counterpart = findCounterpart(document);
			if (!counterpart) {
				vscode.window.showInformationMessage('No matching Eto designer or code behind file was found.');
				return;
			}
			await vscode.window.showTextDocument(vscode.Uri.file(counterpart));
		}),
		vscode.window.onDidChangeActiveTextEditor(updateContext),
		vscode.workspace.onDidCreateFiles(updateContext),
		vscode.workspace.onDidDeleteFiles(updateContext),
		vscode.workspace.onDidRenameFiles(updateContext)
	);
}
