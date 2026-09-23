using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Eto.DevExtension.VisualStudio.Intellisense
{
	// .jeto files open in the json editor, so these register for json and switch off for any other file

	[ContentType("JSON"), Name("JetoCompletion")]
	[Export(typeof(IAsyncCompletionSourceProvider))]
	public class JetoCompletionSourceProvider : IAsyncCompletionSourceProvider
	{
		[Import]
		public ITextDocumentFactoryService TextDocumentFactoryService { get; set; }

		public IAsyncCompletionSource GetOrCreate(ITextView textView) =>
			textView.Properties.GetOrCreateSingletonProperty<IAsyncCompletionSource>(typeof(JetoCompletionSourceProvider), () =>
			{
				var path = JetoFiles.GetPath(TextDocumentFactoryService, textView);
				return path != null ? new JetoCompletionSource(path) : (IAsyncCompletionSource)NoCompletion.Instance;
			});
	}

	[ContentType("JSON"), Name("JetoCompletion")]
	[Export(typeof(IAsyncCompletionCommitManagerProvider))]
	public class JetoCompletionManagerProvider : IAsyncCompletionCommitManagerProvider
	{
		[Import]
		public ITextDocumentFactoryService TextDocumentFactoryService { get; set; }

		public IAsyncCompletionCommitManager GetOrCreate(ITextView textView) =>
			textView.Properties.GetOrCreateSingletonProperty<IAsyncCompletionCommitManager>(typeof(JetoCompletionManagerProvider), () =>
				JetoFiles.GetPath(TextDocumentFactoryService, textView) != null
					? new JetoCompletionManager(textView)
					: (IAsyncCompletionCommitManager)NoCompletion.Instance);
	}

	static class JetoFiles
	{
		/// <summary>Path of the file in the view, or null when it isn't a .jeto file.</summary>
		public static string GetPath(ITextDocumentFactoryService documents, ITextView textView)
		{
			if (!documents.TryGetTextDocument(textView.TextDataModel.DocumentBuffer, out var document))
				return null;
			var path = document.FilePath;
			return path != null && path.EndsWith(".jeto", StringComparison.OrdinalIgnoreCase) ? path : null;
		}
	}

	/// <summary>Stands in for plain .json files so the json editor is left untouched.</summary>
	class NoCompletion : IAsyncCompletionSource, IAsyncCompletionCommitManager
	{
		public static readonly NoCompletion Instance = new NoCompletion();

		public IEnumerable<char> PotentialCommitCharacters => Enumerable.Empty<char>();

		public CompletionStartData InitializeCompletion(CompletionTrigger trigger, SnapshotPoint triggerLocation, CancellationToken token) =>
			CompletionStartData.DoesNotParticipateInCompletion;

		public Task<CompletionContext> GetCompletionContextAsync(IAsyncCompletionSession session, CompletionTrigger trigger, SnapshotPoint triggerLocation, SnapshotSpan applicableToSpan, CancellationToken token) =>
			Task.FromResult(CompletionContext.Empty);

		public Task<object> GetDescriptionAsync(IAsyncCompletionSession session, CompletionItem item, CancellationToken token) =>
			Task.FromResult<object>(null);

		public bool ShouldCommitCompletion(IAsyncCompletionSession session, SnapshotPoint location, char typedChar, CancellationToken token) => false;

		public CommitResult TryCommit(IAsyncCompletionSession session, ITextBuffer buffer, CompletionItem item, char typedChar, CancellationToken token) =>
			CommitResult.Unhandled;
	}
}
