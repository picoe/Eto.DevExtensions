using Eto.Designer;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;

namespace Eto.DevExtension.VisualStudio.Windows.Editor
{
	[Export(typeof(ITaggerProvider))]
	[ContentType("xeto")]
	[ContentType("JSON")]
	[TagType(typeof(IErrorTag))]
	sealed class PreviewErrorTaggerProvider : ITaggerProvider
	{
		public ITagger<T> CreateTagger<T>(ITextBuffer buffer) where T : ITag => PreviewErrorTagger.Get(buffer) as ITagger<T>;
	}

	/// <summary>Underlines the error the preview last found in a designer file.</summary>
	sealed class PreviewErrorTagger : ITagger<IErrorTag>
	{
		readonly ITextBuffer buffer;
		ITrackingSpan span;
		string message;

		PreviewErrorTagger(ITextBuffer buffer) => this.buffer = buffer;

		public static PreviewErrorTagger Get(ITextBuffer buffer) =>
			buffer.Properties.GetOrCreateSingletonProperty(() => new PreviewErrorTagger(buffer));

		public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

		/// <param name="error">The error to show, or null to clear it.</param>
		/// <param name="snapshot">The text the preview was drawn from, which the error's range is for.</param>
		public void Show(DesignError error, ITextSnapshot snapshot)
		{
			var hadError = span != null;
			span = GetSpan(error?.Range, snapshot);
			message = error?.Message;
			if (hadError || span != null)
			{
				var current = buffer.CurrentSnapshot;
				TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(current, 0, current.Length)));
			}
		}

		static ITrackingSpan GetSpan(DesignErrorRange range, ITextSnapshot snapshot)
		{
			if (range == null || range.StartLine >= snapshot.LineCount || range.EndLine >= snapshot.LineCount)
				return null;
			var start = GetPoint(snapshot, range.StartLine, range.StartColumn);
			var end = Math.Max(start, GetPoint(snapshot, range.EndLine, range.EndColumn));
			return snapshot.CreateTrackingSpan(Span.FromBounds(start, end), SpanTrackingMode.EdgeExclusive);
		}

		static int GetPoint(ITextSnapshot snapshot, int line, int column)
		{
			var textLine = snapshot.GetLineFromLineNumber(line);
			return textLine.Start + Math.Min(column, textLine.Length);
		}

		public IEnumerable<ITagSpan<IErrorTag>> GetTags(NormalizedSnapshotSpanCollection spans)
		{
			var tracked = span;
			if (tracked == null || spans.Count == 0)
				yield break;
			var current = tracked.GetSpan(spans[0].Snapshot);
			if (spans.IntersectsWith(new NormalizedSnapshotSpanCollection(current)))
				yield return new TagSpan<IErrorTag>(current, new ErrorTag(PredefinedErrorTypeNames.SyntaxError, message));
		}
	}
}
