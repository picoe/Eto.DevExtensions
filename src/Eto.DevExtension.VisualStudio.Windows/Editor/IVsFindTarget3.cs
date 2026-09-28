using System.Runtime.InteropServices;

// Not in the public SDK; VS asks a document for these to decide between the quick find bar and the find dialog.
// Namespace, names and TypeIdentifier must match VS's own declarations so casts to and from the editor's view succeed.
namespace Microsoft.VisualStudio.Editor.Internal
{
	[ComImport, TypeIdentifier, Guid("A2F0D62B-D0DD-4C59-AAB8-79CD20785451"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	public interface IVsFindTarget3
	{
		int IsNewUISupported { [PreserveSig] get; }

		[PreserveSig]
		int NotifyShowingNewUI();
	}

	[ComImport, TypeIdentifier, Guid("47AB8522-6BB1-4C85-BA88-E4B4513F8BE1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	public interface IVsFindTarget4
	{
		int IsAutonomous { [PreserveSig] get; }

		int IsIncrementalSearchSupported { [PreserveSig] get; }
	}
}
