using System;
using System.Collections;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Permissions;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Shell;
using EnvDTE;

using Eto.Forms;
using System.Linq;
using System.Windows.Forms.Integration;
using System.Text;
using Eto.Designer;
using Eto.Designer.Builders;
using System.Windows.Threading;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.Utilities;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Editor.Internal;
using ITextBuffer = Microsoft.VisualStudio.Text.ITextBuffer;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using System.Windows.Media;
using IOleServiceProvider = Microsoft.VisualStudio.OLE.Interop.IServiceProvider;
using System.Collections.Generic;
using Eto.DevExtension.VisualStudio.Intellisense;

namespace Eto.DevExtension.VisualStudio.Windows.Editor
{
	[ComVisible(true)]
	public sealed class EtoPreviewPane : Microsoft.VisualStudio.Shell.WindowPane,
		IVsWindowPane,
		IVsTextBufferDataEvents,
		IVsTextLinesEvents,
		IOleCommandTarget,
		IVsCodeWindow, // support setting breakpoints
		IVsCodeWindowEx,
		IVsFindTarget, // find in the embedded editor
		IVsFindTarget2,
		IVsFindTarget3, // enables the quick find bar instead of only the find dialog
		IVsFindTarget4
	{
		IVsTextLines textBuffer;
		ITextBuffer documentBuffer;
		CodeEditorHost editor;
		EtoAddinPackage package;
		PreviewEditorView preview;
		PreviewEditorViewSplitter previewSplitter;
		PreviewHostClient previewHost;
		DropDown platformDropDown;
		// label of the platform the last preview was drawn with, shown beside Auto
		string drawnPlatform;
		bool loadingPlatforms;
		DropDown themeDropDown;
		// themes the host offers, and the one it last drew with, shown beside Default
		string[] drawnThemes;
		string drawnTheme;
		bool loadingThemes;
		Panel editorControl;
		uint dataEventsCookie;
		uint linesEventsCookie;
		uint docCookie;
		bool disposed;

		IWpfTextView WpfTextView => editor.textViewHost?.TextView;

		bool CodeEditorHasFocus => WpfTextView?.VisualElement?.IsKeyboardFocused == true;

		void RegisterIndependentView(bool subscribe)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			var textManager = (IVsTextManager)GetService(typeof(SVsTextManager));
			if (textManager != null)
			{
				if (subscribe)
					textManager.RegisterIndependentView(this, textBuffer);
				else
					textManager.UnregisterIndependentView(this, textBuffer);
			}

			var dataEvents = GetConnectionPoint<IVsTextBufferDataEvents>();
			if (dataEvents != null)
			{
				if (subscribe)
					dataEvents.Advise(this, out dataEventsCookie);
				else if (dataEventsCookie != 0)
				{
					dataEvents.Unadvise(dataEventsCookie);
					dataEventsCookie = 0;
				}
			}

			var linesEvents = GetConnectionPoint<IVsTextLinesEvents>();
			if (linesEvents != null)
			{
				if (subscribe)
					linesEvents.Advise(this, out linesEventsCookie);
				else if (linesEventsCookie != 0)
				{
					linesEvents.Unadvise(linesEventsCookie);
					linesEventsCookie = 0;
				}
			}

		}

		#region "Window.Pane Overrides"
		/// <summary>
		/// Constructor that calls the Microsoft.VisualStudio.Shell.WindowPane constructor then
		/// our initialization functions.
		/// </summary>
		/// <param name="package">Our Package instance.</param>
		/// <param name="projectKey">Identifies the project, so its designers share one preview host.</param>
		public EtoPreviewPane(EtoAddinPackage package, string fileName, IVsTextLines textBuffer, string projectKey, CodeEditorHost codeEditor)
			: base(package)
		{
			this.editor = codeEditor;
			this.package = package;
			this.textBuffer = textBuffer;
			FileName = fileName;

			editorControl = new Panel();
			editorControl.Content = editor.wpfElement.ToEto();

			documentBuffer = Services.GetComponentService<IVsEditorAdaptersFactoryService>()?.GetDocumentBuffer(textBuffer);
			var host = previewHost = PreviewHostClient.Acquire(projectKey);
			host.ProjectChanged += PreviewHost_ProjectChanged;
			var designHost = new RemoteDesignPanel(async request =>
			{
				var snapshot = documentBuffer?.CurrentSnapshot;
				var result = await host.RenderAsync(request, await ProjectAssemblyPaths.GetAsync(fileName));
				if (snapshot != null && !disposed)
					PreviewErrorTagger.Get(documentBuffer).Show(result?.Error, snapshot);
				if (result?.Platform != null && result.Platform != drawnPlatform)
				{
					drawnPlatform = result.Platform;
					LoadPlatforms();
				}
				if (result?.Themes != null && (result.Theme != drawnTheme || drawnThemes == null || !result.Themes.SequenceEqual(drawnThemes)))
				{
					drawnThemes = result.Themes;
					drawnTheme = result.Theme;
					LoadThemes();
				}
				return result;
			});

			previewSplitter = new PreviewEditorViewSplitter(editorControl, designHost, () => textBuffer?.GetText());
			previewSplitter.GotFocus += (sender, e) =>
			{
				WpfTextView?.VisualElement?.Focus();
			};
			preview = previewSplitter.Preview;
			PreviewLayoutSettings.Load(previewSplitter);
			previewSplitter.LayoutChanged += (sender, e) => PreviewLayoutSettings.Save(previewSplitter);

			platformDropDown = new DropDown { ToolTip = "Platform to draw the preview with" };
			platformDropDown.SelectedKeyChanged += PlatformDropDown_SelectedKeyChanged;
			themeDropDown = new DropDown { ToolTip = "Theme to draw the preview with" };
			themeDropDown.SelectedKeyChanged += ThemeDropDown_SelectedKeyChanged;
			preview.ToolBar = new StackLayout
			{
				Orientation = Orientation.Horizontal,
				Spacing = 4,
				Items = { platformDropDown, themeDropDown }
			};
			LoadPlatforms();
			LoadThemes();
			PreviewPlatforms.ChoiceChanged += PreviewPlatforms_ChoiceChanged;
			PreviewPlatforms.ThemeChoiceChanged += PreviewPlatforms_ThemeChoiceChanged;
			VSColorTheme.ThemeChanged += VSColorTheme_ThemeChanged;

			var content = previewSplitter.ToNative(true);
			Wizards.EtoInitializer.ApplyTheme(content);

			if (!preview.SetBuilder(fileName))
				throw new InvalidOperationException(string.Format("Could not find builder for file {0}", fileName));
			Content = content;

		}

		protected override bool PreProcessMessage(ref System.Windows.Forms.Message m)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			// copy the Message into a MSG[] array, so we can pass
			// it along to the active core editor's IVsWindowPane.TranslateAccelerator
			var pMsg = new MSG[1];
			pMsg[0].hwnd = m.HWnd;
			pMsg[0].message = (uint)m.Msg;
			pMsg[0].wParam = m.WParam;
			pMsg[0].lParam = m.LParam;

			var filterKeys2 = Services.GetService<SVsFilterKeys, IVsFilterKeys2>();
			if (filterKeys2 != null)
			{
				// support global keyboard shortcuts

				int hr = filterKeys2.TranslateAcceleratorEx(pMsg,
					(uint)__VSTRANSACCELEXFLAGS.VSTAEXF_UseGlobalKBScope,
					0,
					null,
					out _,
					out _,
					out _,
					out _);
				if (hr == 0)
					return true;
			}

			if (editor.viewAdapter != null)
			{
				var vsWindowPane = (IVsWindowPane)editor.viewAdapter;

				return vsWindowPane.TranslateAccelerator(pMsg) == 0;
			}
			return base.PreProcessMessage(ref m);
		}

		const int NotSupported = (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
        int IOleCommandTarget.Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			var hr = NotSupported;
			if (pguidCmdGroup == VSConstants.GUID_VSStandardCommandSet97 && nCmdID == (int)VSConstants.VSStd97CmdID.ViewCode)
			{
				ViewCode();
				return VSConstants.S_OK;
			}
			if (CodeEditorHasFocus && editor.viewAdapter != null)
			{
				var cmdTarget = (IOleCommandTarget)editor.viewAdapter;
				hr = cmdTarget.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
			}
			return hr;
		}

		int IOleCommandTarget.QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[]prgCmds, IntPtr pCmdText)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			var hr = NotSupported;
			if (pguidCmdGroup == VSConstants.GUID_VSStandardCommandSet97)
			{
				for (int i = 0; i < prgCmds.Length; i++)
				{
					if (prgCmds[i].cmdID == (int)VSConstants.VSStd97CmdID.ViewCode)
					{
						prgCmds[i].cmdf = (uint)(OLECMDF.OLECMDF_ENABLED | OLECMDF.OLECMDF_SUPPORTED);
						return VSConstants.S_OK;
                    }
				}
			}

			if (CodeEditorHasFocus && editor.viewAdapter != null)
			{
				var cmdTarget = (IOleCommandTarget)editor.viewAdapter;
				hr = cmdTarget.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);
			}
			return hr;
		}

		protected override void Initialize()
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			base.Initialize();

			RegisterIndependentView(true);

			SetupCommands();

			InheritKeyBindings();

			// the document may not be registered yet while the frame is being created
			if (!LockDocument())
				ThreadHelper.JoinableTaskFactory.StartOnIdle(() => LockDocument());

			preview.Update();
		}

		/// <summary>
		/// Holds the document open until our text view is closed, as VS releases it before closing the pane,
		/// and a view still open when it closes throws from the editor's margins.
		/// </summary>
		bool LockDocument()
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			if (disposed || docCookie != 0)
				return true;
			var rdt = (IVsRunningDocumentTable)GetService(typeof(SVsRunningDocumentTable));
			if (rdt == null)
				return false;
			var hr = rdt.FindAndLockDocument((uint)_VSRDTFLAGS.RDT_ReadLock, FileName, out _, out _, out var docData, out var cookie);
			if (docData != IntPtr.Zero)
				Marshal.Release(docData);
			if (hr != VSConstants.S_OK || cookie == 0)
				return false;
			docCookie = cookie;
			return true;
		}

		void UnlockDocument()
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			if (docCookie == 0)
				return;
			var rdt = (IVsRunningDocumentTable)GetService(typeof(SVsRunningDocumentTable));
			rdt?.UnlockDocument((uint)(_VSRDTFLAGS.RDT_ReadLock | _VSRDTFLAGS.RDT_Unlock_NoSave), docCookie);
			docCookie = 0;
		}

		void InheritKeyBindings()
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			// allow text editor keyboard shortcuts to be used in our embedded editor
			var frame = (IVsWindowFrame)GetService(typeof(SVsWindowFrame));
			if (frame != null)
			{
				Guid commandUiGuid = VSConstants.GUID_TextEditorFactory;
				frame.SetGuidProperty((int)__VSFPROPID.VSFPROPID_InheritKeyBindings, ref commandUiGuid);
			}
		}

		IConnectionPoint GetConnectionPoint<T>()
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			var container = textBuffer as IConnectionPointContainer;
			var guid = typeof(T).GUID;
			IConnectionPoint cp;
			container.FindConnectionPoint(ref guid, out cp);
			return cp;
		}

#endregion


		/// <summary>
		/// returns the name of the file currently loaded
		/// </summary>
		public string FileName { get; private set; }

		protected override void Dispose(bool disposing)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			try
			{
				if (disposing)
				{

					RegisterIndependentView(false);

					PreviewPlatforms.ChoiceChanged -= PreviewPlatforms_ChoiceChanged;
					PreviewPlatforms.ThemeChoiceChanged -= PreviewPlatforms_ThemeChoiceChanged;
					VSColorTheme.ThemeChanged -= VSColorTheme_ThemeChanged;

					disposed = true;

					// the buffer outlives the pane when the file is also open in a plain editor
					if (documentBuffer != null)
						PreviewErrorTagger.Get(documentBuffer).Show(null, documentBuffer.CurrentSnapshot);

					// close the view before letting the document close
					editor.Close();
					UnlockDocument();

					if (previewHost != null)
					{
						previewHost.ProjectChanged -= PreviewHost_ProjectChanged;
						previewHost.Release();
						previewHost = null;
					}

					//previewSplitter?.Dispose();
					//previewSplitter = null;

					editorControl?.Dispose();
					editorControl = null;

					GC.SuppressFinalize(this);
				}
			}
			finally
			{
				base.Dispose(disposing);
			}
		}

#region Command Handling Functions

		void SetupCommands()
		{
			var mcs = GetService(typeof(IMenuCommandService)) as IMenuCommandService;
			if (mcs != null)
			{
				//mcs.AddCommand(new MenuCommand((sender, e) => ViewCode(), new CommandID(VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.ViewCode)));
				mcs.AddCommand(VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.ViewCode, ViewCode);
			}
		}

		void ViewCode()
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			// Open the referenced document using the standard text editor.
			var codeFile = preview.GetCodeFile(FileName);

			IVsWindowFrame frame;
			IVsUIHierarchy hierarchy;
			uint itemid;
			if (!VsShellUtilities.IsDocumentOpen(this, codeFile, VSConstants.LOGVIEWID.Primary_guid, out hierarchy, out itemid, out frame)
				&& !VsShellUtilities.IsDocumentOpen(this, codeFile, VSConstants.LOGVIEWID.TextView_guid, out hierarchy, out itemid, out frame))
			{
				VsShellUtilities.OpenDocumentWithSpecificEditor(this, codeFile, VSConstants.VsEditorFactoryGuid.TextEditor_guid, VSConstants.LOGVIEWID.Primary_guid, out hierarchy, out itemid, out frame);
			}
			ErrorHandler.ThrowOnFailure(frame.Show());
		}

#endregion


		void PreviewHost_ProjectChanged(object sender, EventArgs e) => preview?.Update();

		void VSColorTheme_ThemeChanged(ThemeChangedEventArgs e)
		{
			// may be raised off the UI thread
			Eto.Forms.Application.Instance.AsyncInvoke(() =>
			{
				if (!disposed)
					previewSplitter?.UpdateTheme();
			});
		}

		void LoadPlatforms()
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			if (platformDropDown == null)
				return;
			var choice = PreviewPlatforms.Choice;
			var auto = choice == PreviewPlatforms.Auto && drawnPlatform != null ? $"Auto ({drawnPlatform})" : "Auto";
			var items = new List<IListItem> { new ListItem { Key = PreviewPlatforms.Auto, Text = auto } };
			items.AddRange(PreviewPlatforms.GetAvailable().Select(r => new ListItem { Key = r.Id, Text = r.Label }));

			loadingPlatforms = true;
			platformDropDown.DataStore = items;
			platformDropDown.SelectedKey = items.Any(r => r.Key == choice) ? choice : PreviewPlatforms.Auto;
			loadingPlatforms = false;
		}

		void PlatformDropDown_SelectedKeyChanged(object sender, EventArgs e)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			if (!loadingPlatforms && platformDropDown.SelectedKey != null)
				PreviewPlatforms.Choice = platformDropDown.SelectedKey;
		}

		void PreviewPlatforms_ChoiceChanged(object sender, EventArgs e)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			drawnPlatform = null;
			LoadPlatforms();
			// another platform has its own themes
			drawnThemes = null;
			drawnTheme = null;
			LoadThemes();
			preview?.Update();
		}

		void LoadThemes()
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			if (themeDropDown == null)
				return;
			var choice = PreviewPlatforms.ThemeChoice;
			var names = (drawnThemes ?? Array.Empty<string>()).ToList();
			// until the host lists its themes, still show what was picked
			if (drawnThemes == null && choice != PreviewPlatforms.DefaultTheme)
				names.Add(choice);
			var selected = names.Contains(choice) ? choice : PreviewPlatforms.DefaultTheme;
			var label = selected == PreviewPlatforms.DefaultTheme && drawnTheme != null ? $"Default ({drawnTheme})" : "Default";
			var items = new List<IListItem> { new ListItem { Key = PreviewPlatforms.DefaultTheme, Text = label } };
			items.AddRange(names.Select(r => new ListItem { Key = r, Text = r }));

			loadingThemes = true;
			themeDropDown.DataStore = items;
			themeDropDown.SelectedKey = selected;
			loadingThemes = false;
		}

		void ThemeDropDown_SelectedKeyChanged(object sender, EventArgs e)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			if (!loadingThemes && themeDropDown.SelectedKey != null)
				PreviewPlatforms.ThemeChoice = themeDropDown.SelectedKey;
		}

		void PreviewPlatforms_ThemeChoiceChanged(object sender, EventArgs e)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			LoadThemes();
			preview?.Update();
		}

		void IVsTextBufferDataEvents.OnFileChanged(uint grfChange, uint dwFileAttrs)
		{
			preview.Update();
		}

		int IVsTextBufferDataEvents.OnLoadCompleted(int fReload)
		{
			preview.Update();
			return VSConstants.S_OK;
		}

		void IVsTextLinesEvents.OnChangeLineAttributes(int iFirstLine, int iLastLine)
		{
		}

		void IVsTextLinesEvents.OnChangeLineText(TextLineChange[] pTextLineChange, int fLast)
		{
			preview.Update();
		}

		public override int SaveUIState(out Stream stateStream)
		{
			var ret = base.SaveUIState(out stateStream);
			stateStream = stateStream ?? new MemoryStream();
			using (var bw = new BinaryWriter(stateStream, Encoding.UTF8, true))
				bw.Write(previewSplitter.RelativePosition);
			return 0;
		}

		public override int LoadUIState(Stream stateStream)
		{
			var ret = base.LoadUIState(stateStream);
			if (stateStream != null)
			{
				using (var br = new BinaryReader(stateStream, Encoding.UTF8, true))
					previewSplitter.RelativePosition = br.ReadDouble();
			}
			return 0;
		}

		public int SetBuffer(IVsTextLines pBuffer) => editor.codeWindow.SetBuffer(pBuffer);
		public int GetBuffer(out IVsTextLines ppBuffer) => editor.codeWindow.GetBuffer(out ppBuffer);
		public int GetPrimaryView(out IVsTextView ppView) => editor.codeWindow.GetPrimaryView(out ppView);
		public int GetSecondaryView(out IVsTextView ppView) => editor.codeWindow.GetSecondaryView(out ppView);
		public int SetViewClassID(ref Guid clsidView) => editor.codeWindow.SetViewClassID(ref clsidView);
		public int GetViewClassID(out Guid pclsidView) => editor.codeWindow.GetViewClassID(out pclsidView);
		public int SetBaseEditorCaption(string[] pszBaseEditorCaption) => editor.codeWindow.SetBaseEditorCaption(pszBaseEditorCaption);
		public int GetEditorCaption(READONLYSTATUS dwReadOnly, out string pbstrEditorCaption) => editor.codeWindow.GetEditorCaption(dwReadOnly, out pbstrEditorCaption);
		public int Close() => editor.Close();
		public int GetLastActiveView(out IVsTextView ppView) => editor.codeWindow.GetLastActiveView(out ppView);

		int IVsCodeWindowEx.Initialize(uint grfCodeWindowBehaviorFlags, VSUSERCONTEXTATTRIBUTEUSAGE usageAuxUserContext, string szNameAuxUserContext, string szValueAuxUserContext, uint InitViewFlags, INITVIEW[] pInitView) => ((IVsCodeWindowEx)editor.codeWindow).Initialize(grfCodeWindowBehaviorFlags, usageAuxUserContext, szNameAuxUserContext, szValueAuxUserContext, InitViewFlags, pInitView);
		int IVsCodeWindowEx.IsReadOnly() => ((IVsCodeWindowEx)editor.codeWindow).IsReadOnly();

		IVsFindTarget FindTarget => editor.viewAdapter as IVsFindTarget;

		int IVsFindTarget.GetCapabilities(bool[] pfImage, uint[] pgrfOptions) => FindTarget?.GetCapabilities(pfImage, pgrfOptions) ?? VSConstants.E_FAIL;
		int IVsFindTarget.GetProperty(uint propid, out object pvar)
		{
			pvar = null;
			return FindTarget?.GetProperty(propid, out pvar) ?? VSConstants.E_FAIL;
		}
		int IVsFindTarget.GetSearchImage(uint grfOptions, IVsTextSpanSet[] ppSpans, out IVsTextImage ppTextImage)
		{
			ppTextImage = null;
			return FindTarget?.GetSearchImage(grfOptions, ppSpans, out ppTextImage) ?? VSConstants.E_FAIL;
		}
		int IVsFindTarget.Find(string pszSearch, uint grfOptions, int fResetStartPoint, IVsFindHelper pHelper, out uint pResult)
		{
			pResult = 0;
			return FindTarget?.Find(pszSearch, grfOptions, fResetStartPoint, pHelper, out pResult) ?? VSConstants.E_FAIL;
		}
		int IVsFindTarget.Replace(string pszSearch, string pszReplace, uint grfOptions, int fResetStartPoint, IVsFindHelper pHelper, out int pfReplaced)
		{
			pfReplaced = 0;
			return FindTarget?.Replace(pszSearch, pszReplace, grfOptions, fResetStartPoint, pHelper, out pfReplaced) ?? VSConstants.E_FAIL;
		}
		int IVsFindTarget.GetMatchRect(RECT[] prc) => FindTarget?.GetMatchRect(prc) ?? VSConstants.E_FAIL;
		int IVsFindTarget.NavigateTo(TextSpan[] pts) => FindTarget?.NavigateTo(pts) ?? VSConstants.E_FAIL;
		int IVsFindTarget.GetCurrentSpan(TextSpan[] pts) => FindTarget?.GetCurrentSpan(pts) ?? VSConstants.E_FAIL;
		int IVsFindTarget.SetFindState(object pUnk) => FindTarget?.SetFindState(pUnk) ?? VSConstants.E_FAIL;
		int IVsFindTarget.GetFindState(out object ppunk)
		{
			ppunk = null;
			return FindTarget?.GetFindState(out ppunk) ?? VSConstants.E_FAIL;
		}
		int IVsFindTarget.NotifyFindTarget(uint notification) => FindTarget?.NotifyFindTarget(notification) ?? VSConstants.E_FAIL;
		int IVsFindTarget.MarkSpan(TextSpan[] pts) => FindTarget?.MarkSpan(pts) ?? VSConstants.E_FAIL;

		int IVsFindTarget2.NavigateTo2(IVsTextSpanSet pSpans, TextSelMode iSelMode) => (editor.viewAdapter as IVsFindTarget2)?.NavigateTo2(pSpans, iSelMode) ?? VSConstants.E_FAIL;

		int IVsFindTarget3.IsNewUISupported => (editor.viewAdapter as IVsFindTarget3)?.IsNewUISupported ?? 0;
		int IVsFindTarget3.NotifyShowingNewUI() => (editor.viewAdapter as IVsFindTarget3)?.NotifyShowingNewUI() ?? VSConstants.E_FAIL;

		int IVsFindTarget4.IsAutonomous => (editor.viewAdapter as IVsFindTarget4)?.IsAutonomous ?? 0;
		int IVsFindTarget4.IsIncrementalSearchSupported => (editor.viewAdapter as IVsFindTarget4)?.IsIncrementalSearchSupported ?? 0;
	}
}
