using Eto.Designer.Completion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using CompletionItem = Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data.CompletionItem;
using Microsoft.VisualStudio.Text.Adornments;
using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Eto.DevExtension.VisualStudio.Intellisense
{
	/// <summary>Adds Eto completions to the json editor when it has a .jeto file open.</summary>
	class JetoCompletionSource : IAsyncCompletionSource
	{
		internal const string ItemKey = "eto";
		// span to replace on commit, which may differ from the session's when the json editor also participates
		internal static readonly object SpanKey = new object();

		readonly string filePath;

		static JetoCompletionSource()
		{
			XmlComments.EncodeHtml = false;
		}

		public JetoCompletionSource(string filePath)
		{
			this.filePath = filePath;
			// pick up base class changes in the code behind each time the file is opened
			RootTypeLocator.Forget(filePath);
		}

		public CompletionStartData InitializeCompletion(CompletionTrigger trigger, SnapshotPoint triggerLocation, CancellationToken token)
		{
			if (trigger.Reason != CompletionTriggerReason.Invoke
				&& trigger.Reason != CompletionTriggerReason.InvokeAndCommitIfUnique
				&& !(trigger.Reason == CompletionTriggerReason.Insertion && IsTriggerChar(trigger.Character)))
				return CompletionStartData.DoesNotParticipateInCompletion;

			var context = GetContext(triggerLocation);
			if (context == null)
				return CompletionStartData.DoesNotParticipateInCompletion;

			var span = new SnapshotSpan(triggerLocation.Snapshot, context.Start, context.End - context.Start);
			return new CompletionStartData(CompletionParticipation.ProvidesItems, span);
		}

		public Task<CompletionContext> GetCompletionContextAsync(IAsyncCompletionSession session, CompletionTrigger trigger, SnapshotPoint triggerLocation, SnapshotSpan applicableToSpan, CancellationToken token)
		{
			return Task.Run(() =>
			{
				try
				{
					var context = GetContext(triggerLocation);
					if (context == null)
						return CompletionContext.Empty;

					var snapshot = triggerLocation.Snapshot;
					session.Properties[SpanKey] = snapshot.CreateTrackingSpan(context.Start, context.End - context.Start, SpanTrackingMode.EdgeInclusive);

					var items = DocumentCompletion.GetItems(context).Select(r =>
					{
						var item = new CompletionItem(r.Label,
							source: this,
							icon: XamlCompletionSource.GetGlyph(r.Item.Type),
							filters: ImmutableArray<CompletionFilter>.Empty,
							suffix: r.Item.Suffix ?? string.Empty,
							insertText: r.InsertText,
							sortText: r.Label,
							filterText: r.Label,
							attributeIcons: ImmutableArray<ImageElement>.Empty);
						item.Properties[ItemKey] = r;
						return item;
					});
					return new CompletionContext(items.ToImmutableArray(), null, InitialSelectionHint.RegularSelection);
				}
				catch (Exception ex)
				{
					Debug.WriteLine($"Error doing jeto autocomplete: {ex}");
					throw;
				}
			}, token);
		}

		public Task<object> GetDescriptionAsync(IAsyncCompletionSession session, CompletionItem item, CancellationToken token)
		{
			item.Properties.TryGetProperty(ItemKey, out DocumentCompletionItem etoitem);
			return Task.FromResult<object>(etoitem?.Item.Description);
		}

		DocumentCompletionContext GetContext(SnapshotPoint point)
		{
			var text = point.Snapshot.GetText();
			return DocumentCompletion.GetContext(text, point.Position, CompletionFormat.Json, RootTypeLocator.Find(filePath));
		}

		// matches the language server's trigger characters so both editors pop up in the same places
		static bool IsTriggerChar(char ch) =>
			char.IsLetterOrDigit(ch) || ch == '_' || ch == '$' || ch == '"' || ch == '\'' || ch == '{' || ch == ',' || ch == ':' || ch == ' ';
	}
}
