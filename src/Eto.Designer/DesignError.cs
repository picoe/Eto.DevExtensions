using System;

namespace Eto.Designer
{
    public class DesignError : MarshalByRefObject
	{
		public string Message { get; set; }
		public string Details { get; set; }
	}
}