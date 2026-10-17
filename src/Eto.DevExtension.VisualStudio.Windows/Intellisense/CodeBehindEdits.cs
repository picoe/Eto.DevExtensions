using Eto.Designer.Completion;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Eto.DevExtension.VisualStudio.Intellisense
{
	/// <summary>Adds text from a completion, such as a new event handler, to the code behind.</summary>
	static class CodeBehindEdits
	{
		public static void Apply(DocumentFileEdit edit)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			if (edit == null || string.IsNullOrEmpty(edit.Text))
				return;

			try
			{
				// an open document gets the edit in its buffer, so it can be undone and unsaved changes are kept
				var docData = new RunningDocumentTable(ServiceProvider.GlobalProvider).FindDocument(edit.FilePath) as IVsTextBuffer;
				var buffer = docData == null ? null : Services.GetComponentService<IVsEditorAdaptersFactoryService>()?.GetDocumentBuffer(docData);
				if (buffer != null)
				{
					var snapshot = buffer.CurrentSnapshot;
					var line = snapshot.GetLineFromLineNumber(Math.Min(edit.Line, snapshot.LineCount - 1));
					buffer.Insert(line.Start.Position + Math.Min(edit.Character, line.Length), edit.Text);
					return;
				}

				// otherwise write the file, rather than leave an unsaved buffer nobody can see
				var bytes = File.ReadAllBytes(edit.FilePath);
				Encoding encoding;
				string text;
				using (var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false), true))
				{
					text = reader.ReadToEnd();
					encoding = reader.CurrentEncoding;
				}
				var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
				if (encoding is UTF8Encoding)
					encoding = new UTF8Encoding(hasBom);

				var offset = EtoLanguageServer.GetOffset(text, edit.Line, edit.Character);
				File.WriteAllText(edit.FilePath, text.Insert(offset, edit.Text), encoding);
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"Could not add to {edit.FilePath}: {ex}");
			}
		}
	}
}
