import * as path from 'path';
import * as vscode from 'vscode';
import { PreviewHost, RenderResult } from './previewHost';

const REFRESH_DELAY_MS = 500;

/** Designer files the preview can draw. */
export function isPreviewable(document: vscode.TextDocument): boolean {
	return document.uri.scheme === 'file' && /\.(xeto|jeto|eto\.cs|eto\.vb)$/i.test(document.fileName);
}

/**
 * A single preview beside the editor that follows the active designer file and redraws as it is edited.
 */
export class PreviewPanel implements vscode.Disposable {
	private static current: PreviewPanel | undefined;

	private readonly panel: vscode.WebviewPanel;
	private readonly disposables: vscode.Disposable[] = [];
	private document: vscode.TextDocument | undefined;
	private size: { width: number; height: number } | undefined;
	private scale = 1;
	private ready = false;
	private rendering = false;
	private renderPending = false;
	private timer: NodeJS.Timeout | undefined;

	static show(host: PreviewHost, document: vscode.TextDocument): void {
		if (PreviewPanel.current) {
			PreviewPanel.current.panel.reveal(vscode.ViewColumn.Beside, true);
		} else {
			PreviewPanel.current = new PreviewPanel(host);
		}
		PreviewPanel.current.setDocument(document);
	}

	private constructor(private readonly host: PreviewHost) {
		this.panel = vscode.window.createWebviewPanel('eto.preview', 'Eto Preview', { viewColumn: vscode.ViewColumn.Beside, preserveFocus: true }, {
			enableScripts: true,
			localResourceRoots: []
		});
		this.panel.webview.html = getHtml();

		this.disposables.push(
			this.panel.onDidDispose(() => this.dispose()),
			this.panel.webview.onDidReceiveMessage(message => this.onMessage(message)),
			vscode.workspace.onDidChangeTextDocument(e => {
				if (e.document === this.document) {
					this.schedule();
				}
			}),
			vscode.window.onDidChangeActiveTextEditor(editor => {
				if (editor && isPreviewable(editor.document) && editor.document !== this.document) {
					this.setDocument(editor.document);
				}
			}),
			host.onDidRequestRedraw(() => this.render())
		);
	}

	dispose(): void {
		PreviewPanel.current = undefined;
		clearTimeout(this.timer);
		this.panel.dispose();
		for (const disposable of this.disposables.splice(0)) {
			disposable.dispose();
		}
	}

	private setDocument(document: vscode.TextDocument): void {
		this.document = document;
		// a size picked for one file rarely suits the next
		this.size = undefined;
		this.panel.title = `Preview ${path.basename(document.fileName)}`;
		this.render();
	}

	private onMessage(message: { type: string; scale?: number; width?: number; height?: number }): void {
		switch (message.type) {
			case 'ready':
				this.ready = true;
				this.scale = message.scale || 1;
				this.render();
				break;
			case 'scale':
				this.scale = message.scale || 1;
				this.render();
				break;
			case 'resize':
				this.size = { width: Math.round(message.width!), height: Math.round(message.height!) };
				this.render();
				break;
			case 'reset':
				this.size = undefined;
				this.render();
				break;
		}
	}

	private schedule(): void {
		clearTimeout(this.timer);
		this.timer = setTimeout(() => this.render(), REFRESH_DELAY_MS);
	}

	private async render(): Promise<void> {
		if (!this.ready || !this.document) {
			return;
		}
		if (this.rendering) {
			this.renderPending = true;
			return;
		}

		this.rendering = true;
		try {
			do {
				this.renderPending = false;
				const document: vscode.TextDocument = this.document;
				const result: RenderResult = await this.host.render({
					fileName: document.fileName,
					text: document.getText(),
					width: this.size?.width,
					height: this.size?.height,
					scale: this.scale
				});
				if (PreviewPanel.current !== this) {
					return;
				}
				// results for a file the user already moved away from would only flicker
				if (document === this.document) {
					this.panel.webview.postMessage(result.error
						? { type: 'error', message: result.error.message, details: result.error.details }
						: { type: 'image', image: result.image, width: result.width, height: result.height, sized: !!this.size });
				}
			} while (this.renderPending);
		} finally {
			this.rendering = false;
		}
	}
}

function getHtml(): string {
	const nonce = [...Array(32)].map(() => Math.floor(Math.random() * 36).toString(36)).join('');
	return /* html */ `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'nonce-${nonce}'; script-src 'nonce-${nonce}';">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<style nonce="${nonce}">
	html, body { height: 100%; margin: 0; }
	body { background: var(--vscode-editor-background); color: var(--vscode-foreground); font-family: var(--vscode-font-family); font-size: var(--vscode-font-size); display: flex; flex-direction: column; }
	#surface { flex: 1; overflow: auto; padding: 40px 32px 32px; }
	#frame { position: relative; display: none; margin: 0 auto; outline: 1px dashed transparent; outline-offset: 4px; }
	#frame:hover, #frame.dragging { outline-color: var(--vscode-focusBorder); }
	#frame img { display: block; width: 100%; height: 100%; }
	#size { position: absolute; left: 50%; top: -30px; transform: translateX(-50%); padding: 1px 6px; font-size: 11px; white-space: nowrap; background: var(--vscode-badge-background); color: var(--vscode-badge-foreground); border: none; border-radius: 2px; cursor: default; }
	#size.sized { cursor: pointer; }
	#grip { position: absolute; right: -9px; bottom: -9px; width: 10px; height: 10px; border-radius: 50%; background: var(--vscode-focusBorder); cursor: nwse-resize; }
	#status { padding: 16px 32px; color: var(--vscode-descriptionForeground); }
	#error { display: none; padding: 6px 10px; background: var(--vscode-inputValidation-errorBackground); border-top: 1px solid var(--vscode-inputValidation-errorBorder); color: var(--vscode-foreground); white-space: pre-wrap; }
</style>
</head>
<body>
<div id="surface">
	<div id="status">Drawing preview…</div>
	<div id="frame"><img id="image" alt=""><button id="size" title=""></button><div id="grip" title="Drag to resize"></div></div>
</div>
<div id="error"></div>
<script nonce="${nonce}">
	const vscode = acquireVsCodeApi();
	const frame = document.getElementById('frame');
	const image = document.getElementById('image');
	const size = document.getElementById('size');
	const grip = document.getElementById('grip');
	const status = document.getElementById('status');
	const error = document.getElementById('error');
	let drag, sent = 0, sized = false;

	function setFrameSize(width, height) {
		frame.style.width = width + 'px';
		frame.style.height = height + 'px';
		size.textContent = width + 'x' + height;
	}

	window.addEventListener('message', e => {
		const message = e.data;
		if (message.type === 'image') {
			error.style.display = 'none';
			status.style.display = 'none';
			frame.style.display = 'block';
			sized = message.sized;
			size.classList.toggle('sized', sized);
			size.title = sized ? 'Click to reset to auto size' : '';
			image.src = message.image ? 'data:image/png;base64,' + message.image : '';
			// while dragging the frame follows the mouse, and an older result mustn't undo that
			if (!drag)
				setFrameSize(message.width, message.height);
		} else if (message.type === 'error') {
			// keep the last good preview up
			error.textContent = message.message;
			error.title = message.details || '';
			error.style.display = 'block';
			status.style.display = 'none';
		}
	});

	grip.addEventListener('pointerdown', e => {
		drag = { x: e.clientX, y: e.clientY, width: frame.offsetWidth, height: frame.offsetHeight };
		frame.classList.add('dragging');
		grip.setPointerCapture(e.pointerId);
		e.preventDefault();
	});
	grip.addEventListener('pointermove', e => {
		if (!drag)
			return;
		// centered, so it grows both ways and the edge under the mouse moves twice as far
		const width = Math.max(1, Math.round(drag.width + (e.clientX - drag.x) * 2));
		const height = Math.max(1, Math.round(drag.height + e.clientY - drag.y));
		setFrameSize(width, height);
		const now = Date.now();
		if (now - sent > 100) {
			sent = now;
			vscode.postMessage({ type: 'resize', width, height });
		}
	});
	grip.addEventListener('pointerup', () => {
		if (!drag)
			return;
		drag = undefined;
		frame.classList.remove('dragging');
		vscode.postMessage({ type: 'resize', width: frame.offsetWidth, height: frame.offsetHeight });
	});
	size.addEventListener('click', () => {
		if (sized)
			vscode.postMessage({ type: 'reset' });
	});

	function watchScale() {
		matchMedia('(resolution: ' + devicePixelRatio + 'dppx)').addEventListener('change', () => {
			vscode.postMessage({ type: 'scale', scale: devicePixelRatio });
			watchScale();
		}, { once: true });
	}
	watchScale();
	vscode.postMessage({ type: 'ready', scale: devicePixelRatio });
</script>
</body>
</html>`;
}
