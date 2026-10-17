using Eto.Designer;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using Microsoft.VisualStudio.Utilities;
using System;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Threading;

namespace Eto.DevExtension.VisualStudio.Intellisense
{
	/// <summary>
	/// Go To Definition on an event's value in a .xeto/.jeto file, which opens its handler in the code behind.
	/// </summary>
	/// <remarks>
	/// Anything else goes to the XML or JSON editor as before.
	/// </remarks>
	[Export(typeof(ICommandHandler))]
	[Name("EtoGoToDefinition")]
	[ContentType("xeto")]
	[ContentType("Json")]
	[TextViewRole(PredefinedTextViewRoles.Interactive)]
	class GoToDefinitionHandler : ICommandHandler<GoToDefinitionCommandArgs>
	{
		[Import]
		ITextDocumentFactoryService DocumentFactory { get; set; }

		public string DisplayName => "Eto Go To Definition";

		public CommandState GetCommandState(GoToDefinitionCommandArgs args) =>
			GetDocument(args.SubjectBuffer) != null ? CommandState.Available : CommandState.Unspecified;

		public bool ExecuteCommand(GoToDefinitionCommandArgs args, CommandExecutionContext executionContext)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			var document = GetDocument(args.SubjectBuffer);
			return document != null && TryGoTo(args.TextView, args.SubjectBuffer, document.FilePath);
		}

		/// <summary>Opens the handler of the event value at the caret, for the Eto preview pane which gets the command first.</summary>
		/// <returns>True when the caret is on an event's value, whether or not its handler was found.</returns>
		public static bool TryGoTo(ITextView textView, ITextBuffer buffer, string filePath)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			var caret = textView.Caret.Position.Point.GetPoint(buffer, PositionAffinity.Successor);
			if (caret == null)
				return false;

			var text = caret.Value.Snapshot.GetText();
			var offset = caret.Value.Position;
			(bool IsEvent, string FilePath, int Line, int Character) definition;
			try
			{
				definition = ThreadHelper.JoinableTaskFactory.Run(async () =>
				{
					var assemblies = await ProjectAssemblyPaths.GetAsync(filePath);
					using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
						return await EtoLanguageServer.GetDefinitionAsync(filePath, text, offset, assemblies, timeout.Token);
				});
			}
			catch (OperationCanceledException)
			{
				return false;
			}

			if (!definition.IsEvent)
				return false;

			if (definition.FilePath == null)
			{
				SetStatus("No handler with that name in the code behind.");
				return true;
			}

			try
			{
				VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, definition.FilePath, VSConstants.LOGVIEWID.TextView_guid, out _, out _, out var frame, out var view);
				ErrorHandler.ThrowOnFailure(frame.Show());
				view = view ?? VsShellUtilities.GetTextView(frame);
				if (view != null)
				{
					view.SetCaretPos(definition.Line, definition.Character);
					view.CenterLines(definition.Line, 1);
				}
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"Could not open {definition.FilePath}: {ex}");
				SetStatus("Could not open " + definition.FilePath);
			}
			return true;
		}

		/// <summary>The .xeto/.jeto document of the buffer, or null for other files such as plain json.</summary>
		ITextDocument GetDocument(ITextBuffer buffer)
		{
			if (!DocumentFactory.TryGetTextDocument(buffer, out var document))
				return null;
			var extension = BuilderInfo.Find(document.FilePath)?.Extension;
			return string.Equals(extension, ".xeto", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".jeto", StringComparison.OrdinalIgnoreCase) ? document : null;
		}

		static void SetStatus(string text)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			(ServiceProvider.GlobalProvider.GetService(typeof(SVsStatusbar)) as IVsStatusbar)?.SetText(text);
		}
	}
}
