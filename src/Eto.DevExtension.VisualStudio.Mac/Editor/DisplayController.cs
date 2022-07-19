using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using MonoDevelop.Ide;
using MonoDevelop.Ide.Gui.Documents;


namespace Eto.DevExtension.VisualStudio.Mac.Editor
{
	[ExportDocumentControllerFactory(Id = "EtoFormsDesigner", FileExtension = "*", InsertBefore = "DefaultDisplayBinding")]
	public class DesignerDisplayBinding : FileDocumentControllerFactory
	{
		bool enabled = true;

		static DesignerDisplayBinding()
		{
			EtoInitializer.Initialize();
		}

		protected override Task<IEnumerable<DocumentControllerDescription>> GetSupportedControllersAsync(FileDescriptor file)
		{
			if (!enabled)
				return Task.FromResult(Enumerable.Empty<DocumentControllerDescription>());

			// get the list of nodes from here to troubleshoot the InsertBefore above
			// var nodes = Mono.Addins.AddinManager.GetExtensionNodes("/MonoDevelop/Ide/DocumentControllerFactories");
			var info = Eto.Designer.BuilderInfo.Find(file.FilePath);
			if (info == null)
				return Task.FromResult(Enumerable.Empty<DocumentControllerDescription>());

			return Task.FromResult<IEnumerable<DocumentControllerDescription>>(new [] { new DocumentControllerDescription
			{
				Name = "Eto.Forms Visual Designer",
				Role = DocumentControllerRole.VisualDesign,
				CanUseAsDefault = true
			} });
		}

		public override async Task<DocumentController> CreateController(FileDescriptor file, DocumentControllerDescription controllerDescription)
		{
			enabled = false;
			var existing = await IdeServices.DocumentControllerService.CreateTextEditorController(file);
			enabled = true;
			return new EtoDesignerViewContent(existing);
		}

		public override string Id => "Eto.DevExtension.VisualStudio.Mac.Editor.DesignerDisplayBinding";
	}

}
