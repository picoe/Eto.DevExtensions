using System;

namespace Eto.Designer
{
    public class DesignError : MarshalByRefObject
	{
		public string Message { get; set; }
		public string Details { get; set; }
		/// <summary>Where the error is in the text, or null when not known.</summary>
		public DesignErrorRange Range { get; set; }
	}

	/// <summary>Zero based lines and columns.</summary>
	[Serializable]
	public class DesignErrorRange
	{
		public int StartLine { get; set; }
		public int StartColumn { get; set; }
		public int EndLine { get; set; }
		public int EndColumn { get; set; }
	}
}
