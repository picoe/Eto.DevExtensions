using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using System;

namespace Eto.DevExtension.VisualStudio.Intellisense
{
	/// <summary>Go To Definition from the editor's context menu, which goes straight to the text view's command chain.</summary>
	class GoToDefinitionFilter : IOleCommandTarget
	{
		readonly ITextView textView;
		readonly ITextDocument document;
		IOleCommandTarget next;

		/// <summary>Adds the filter once the view first gets focus, so it sits above the editor's own filters.</summary>
		public static void Attach(IVsTextView textViewAdapter, ITextView textView, ITextDocument document)
		{
			void GotFocus(object sender, EventArgs e)
			{
				ThreadHelper.ThrowIfNotOnUIThread();
				textView.GotAggregateFocus -= GotFocus;
				var filter = new GoToDefinitionFilter(textView, document);
				ErrorHandler.ThrowOnFailure(textViewAdapter.AddCommandFilter(filter, out filter.next));
			}
			textView.GotAggregateFocus += GotFocus;
		}

		GoToDefinitionFilter(ITextView textView, ITextDocument document)
		{
			this.textView = textView;
			this.document = document;
		}

		static bool IsGoToDefinition(Guid group, uint id) =>
			group == VSConstants.GUID_VSStandardCommandSet97 && id == (uint)VSConstants.VSStd97CmdID.GotoDefn;

		public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			if (cCmds == 1 && IsGoToDefinition(pguidCmdGroup, prgCmds[0].cmdID))
			{
				prgCmds[0].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_ENABLED);
				return VSConstants.S_OK;
			}
			return next.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);
		}

		public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			if (IsGoToDefinition(pguidCmdGroup, nCmdID) && GoToDefinitionHandler.TryGoTo(textView, document.TextBuffer, document.FilePath))
				return VSConstants.S_OK;
			return next.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
		}
	}
}
