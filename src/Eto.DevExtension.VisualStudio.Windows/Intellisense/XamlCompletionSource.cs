using Eto.Designer.Completion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Text;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using mvli = Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Threading;
using System.Collections.Immutable;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Core.Imaging;

namespace Eto.DevExtension.VisualStudio.Intellisense
{
	class XamlCompletionSource : IAsyncCompletionSource
	{
		internal const string ItemKey = "eto";

		readonly string filePath;

		static XamlCompletionSource()
		{
			XmlComments.EncodeHtml = false;
		}

		public XamlCompletionSource(ITextView textView)
		{
			if (textView.TextDataModel.DocumentBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document))
				filePath = document.FilePath;
		}

		internal static ImageElement GetGlyph(CompletionType type)
		{
            switch (type)
			{
				case CompletionType.Class:
					return new ImageElement(new ImageId(KnownImageIds.ImageCatalogGuid, KnownImageIds.Class));
				case CompletionType.Property:
					return new ImageElement(new ImageId(KnownImageIds.ImageCatalogGuid, KnownImageIds.Property));
				case CompletionType.Event:
					return new ImageElement(new ImageId(KnownImageIds.ImageCatalogGuid, KnownImageIds.Event));
				case CompletionType.Field:
					return new ImageElement(new ImageId(KnownImageIds.ImageCatalogGuid, KnownImageIds.Field));
				case CompletionType.Attribute:
					return new ImageElement(new ImageId(KnownImageIds.ImageCatalogGuid, KnownImageIds.Attribute));
				default:
				case CompletionType.Literal:
					return new ImageElement(new ImageId(KnownImageIds.ImageCatalogGuid, KnownImageIds.Literal));
			}
		}

		/// <summary>
		/// Completions from the language server, which also knows the project's own types, or from
		/// Eto alone when the server can't be started.
		/// </summary>
		internal static async Task<List<DocumentCompletionItem>> GetItemsAsync(string filePath, DocumentCompletionContext context, int offset, CancellationToken token)
		{
			var assemblies = await ProjectAssemblyPaths.GetAsync(filePath);
			await TaskScheduler.Default;
			return await EtoLanguageServer.GetCompletionsAsync(filePath, context.Text, offset, assemblies, token)
				?? DocumentCompletion.GetItems(context);
		}

		public void Dispose()
		{
		}

		public async Task<CompletionContext> GetCompletionContextAsync(IAsyncCompletionSession session, CompletionTrigger trigger, SnapshotPoint triggerLocation, SnapshotSpan applicableToSpan, CancellationToken token)
		{
			var text = triggerLocation.Snapshot.GetText();
			var offset = triggerLocation.Position;
			await TaskScheduler.Default;
			try
			{
				var context = DocumentCompletion.GetContext(text, offset, CompletionFormat.Xaml);
				if (context == null)
					return CompletionContext.Empty;

				// only offer classes straight after '<' or a property element's '.'
				var prevCh = context.Start > 0 ? text[context.Start - 1] : '\0';
				if (context.Info.Mode == CompletionMode.Class && prevCh != '<' && prevCh != '.')
					return CompletionContext.Empty;

				var items = await GetItemsAsync(filePath, context, offset, token);

				// translate to VS completions
				var completionList = new List<mvli.AsyncCompletion.Data.CompletionItem>();
				foreach (var cls in items.OrderBy(r => r.Label))
				{
					var displayText = cls.Label;
					var item = new mvli.AsyncCompletion.Data.CompletionItem(displayText,
						source: this,
						filters: ImmutableArray<CompletionFilter>.Empty,
						icon: GetGlyph(cls.Item.Type),
						suffix: cls.Item.Suffix ?? string.Empty,
						attributeIcons: ImmutableArray<ImageElement>.Empty,
						insertText: cls.InsertText, sortText: displayText, filterText: displayText
						);
					item.Properties[ItemKey] = cls;
					completionList.Add(item);
				}

				return new CompletionContext(completionList.ToImmutableArray(), null, InitialSelectionHint.RegularSelection);
			}
			catch (Exception ex) when (!(ex is OperationCanceledException))
			{
				Debug.WriteLine($"Error doing autocomplete: {ex}");
				throw;
			}
		}

		public Task<object> GetDescriptionAsync(IAsyncCompletionSession session, mvli.AsyncCompletion.Data.CompletionItem item, CancellationToken token)
		{
			item.Properties.TryGetProperty(ItemKey, out DocumentCompletionItem etoitem);
			return Task.FromResult<object>(etoitem?.Item.Description);
		}

		public CompletionStartData InitializeCompletion(CompletionTrigger trigger, SnapshotPoint triggerLocation, CancellationToken token)
		{
			if ((
					trigger.Reason == CompletionTriggerReason.Insertion
					|| trigger.Reason == CompletionTriggerReason.Invoke
				)
				&& !char.IsControl(trigger.Character))
			{
				var span = XamlCompletionManager.FindTokenSpan(new SnapshotSpan(triggerLocation, triggerLocation));

				return new CompletionStartData(CompletionParticipation.ProvidesItems, span);
			}

			return CompletionStartData.DoesNotParticipateInCompletion;
		}
	}
}
