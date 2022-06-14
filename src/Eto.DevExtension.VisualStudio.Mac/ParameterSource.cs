using System;
using Eto.DevExtension.Shared;
using MonoDevelop.Ide.Templates;

namespace Eto.DevExtension.VisualStudio.Mac
{
	public class ParameterSource : IParameterSource
	{
		readonly TemplateWizard wizard;

		public event Action<string, string> ParameterChanged;

		public ParameterSource(TemplateWizard wizard)
		{
			this.wizard = wizard;
		}

		public bool IsSupportedParameter(string parameter)
		{
#if VS2022
			if (string.Equals(parameter, "Net6", StringComparison.OrdinalIgnoreCase))
				return true;
#endif
			return wizard.IsSupportedParameter(parameter);
		}

		public string GetParameter(string parameter)
		{
			return wizard.Parameters[parameter];
		}

		public void SetParameter(string parameter, string value)
		{
			wizard.Parameters[parameter] = value;
			if (ParameterChanged != null)
				ParameterChanged(parameter, value);
		}

		public Version TargetFrameworkVersion
		{
			get { return new Version(4, 5); }
		}

		public bool SeparateMac
		{
			get { return Eto.Platform.Detect.IsMac; }
		}
	}
}

