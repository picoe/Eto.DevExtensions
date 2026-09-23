using Eto.Designer.Completion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using CompletionItem = Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data.CompletionItem;
using Microsoft.VisualStudio.Text.Editor;
using System.Collections.Generic;
using System.Threading;

namespace Eto.DevExtension.VisualStudio.Intellisense
{
	/// <summary>Inserts Eto completions in .jeto files, leaving the json editor's own items alone.</summary>
	class JetoCompletionManager : IAsyncCompletionCommitManager
	{
		readonly ITextView textView;

		public JetoCompletionManager(ITextView textView)
		{
			this.textView = textView;
		}

		// closing a string commits, and the typed quote then steps over the closing one
		public IEnumerable<char> PotentialCommitCharacters { get; } = new[] { '"', '\'' };

		public bool ShouldCommitCompletion(IAsyncCompletionSession session, SnapshotPoint location, char typedChar, CancellationToken token) => true;

		public CommitResult TryCommit(IAsyncCompletionSession session, ITextBuffer buffer, CompletionItem item, char typedChar, CancellationToken token)
		{
			if (!item.Properties.TryGetProperty(JetoCompletionSource.ItemKey, out DocumentCompletionItem etoitem)
				|| !session.Properties.TryGetProperty(JetoCompletionSource.SpanKey, out ITrackingSpan trackingSpan))
				return CommitResult.Unhandled;

			var span = trackingSpan.GetSpan(buffer.CurrentSnapshot);
			var newSnapshot = buffer.Replace(span, etoitem.InsertText);
			if (newSnapshot.TextBuffer == textView.TextBuffer)
				textView.Caret.MoveTo(new SnapshotPoint(newSnapshot, span.Start.Position + etoitem.InsertText.Length));

			// Enter and Tab only commit, but a typed quote still goes through
			return CommitResult.Handled;
		}
	}
}
