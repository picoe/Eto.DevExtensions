using System;
using Eto.Forms;
using Eto.Drawing;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Diagnostics;

namespace Eto.Designer
{
	public class PreviewEditorView : Panel
	{
		IDesignHost designPanel;
		Panel errorPanel;
		UITimer timer;
		UITimer errorTimer;
		Func<string> getCode;
		Control errorContent;
		Panel designPanelHolder;

		public double RefreshTime { get; set; } = 0.5;

		public double ErrorDisplayTime { get; set; } = 6.0;

		public string MainAssemblyPath { get; set; }

		public static bool EnableAppDomains { get; set; } = Platform.Instance.Supports<IEtoAdapterHandler>();

		public PreviewEditorView(string mainAssembly, IEnumerable<string> references, Func<string> getCode)
		{
			//Size = new Size (200, 200);
			MainAssemblyPath = mainAssembly;
			this.getCode = getCode;

#if NETFRAMEWORK
			if (EnableAppDomains)
				designPanel = new AppDomainDesignHost();
			else
				designPanel = new InProcessDesignPanel();
#else
			designPanel = new InProcessDesignPanel();
#endif

			designPanel.MainAssembly = mainAssembly;
			designPanel.References = references;

			designPanel.ControlCreating = () => FinishProcessing(null);
			designPanel.Error = FinishProcessing;

			designPanelHolder = new Panel();
			designPanelHolder.Content = designPanel.GetContainer();

			designPanel.ContainerChanged = () => designPanelHolder.Content = designPanel.GetContainer();

			errorPanel = new Panel { Padding = new Padding(5), Visible = false, BackgroundColor = new Color(Colors.Red, .4f) };

			Content = new StackLayout
			{
				HorizontalContentAlignment = HorizontalAlignment.Stretch,
				Items =
			 	{
			 		new StackLayoutItem(designPanelHolder, expand: true),
			 		errorPanel
			 	}
			};

			timer = new UITimer { Interval = RefreshTime };
			timer.Elapsed += Timer_Elapsed;

			errorTimer = new UITimer { Interval = ErrorDisplayTime };
			errorTimer.Elapsed += ErrorTimer_Elapsed;
		}

		void ErrorTimer_Elapsed(object sender, EventArgs e)
		{
			errorTimer.Stop();
			if (errorContent != null)
			{
				errorPanel.Content = errorContent;
				errorPanel.Visible = true;
				errorContent = null;
			}
		}

		//Stopwatch sw = new Stopwatch();

		void Timer_Elapsed(object sender, EventArgs e)
		{
			timer.Stop();
			//sw.Start();
			var code = getCode();
			if (!string.IsNullOrEmpty(code))
			{
				designPanel?.Update(code);
			}
		}

		protected override void OnGotFocus(EventArgs e)
		{
			base.OnGotFocus(e);
			designPanel?.Invalidate();
		}

		void FinishProcessing(DesignError error)
		{
			if (error != null)
			{
				var errorLabel = new Label { Text = error.Message, ToolTip = error.Details.ToString() };
				if (errorPanel.Visible)
				{
					errorPanel.Content = errorLabel;
				}
				else
				{
					errorContent = errorLabel;
					errorTimer.Start();
				}
			}
			else
			{
				errorPanel.Visible = false;
				errorContent = null;
			}

			//sw.Stop();
			//System.Diagnostics.Debug.WriteLine($"Elapsed: {sw.Elapsed}");

			//if (processingCount > 1)
			//{
			//	// process was requested while we were processing the last one, so redo
			//	processingCount = 1;
			//	timer.Start();
			//}
			//else
			//	processingCount = 0;
		}

		void Stop()
		{
			timer.Stop();
		}

		/// <summary>
		/// Call to update the preview with the current code
		/// </summary>
		public void Update()
		{
			timer.Start();
		}

		/// <summary>
		/// Call to set the builder based on the file name
		/// </summary>
		/// <param name="fileName"></param>
		public bool SetBuilder(string fileName)
		{
			Stop();
			return designPanel.SetBuilder(fileName);
		}

		public string GetCodeFile(string fileName)
		{
			return designPanel.GetCodeFile(fileName);
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				designPanelHolder.Content = null;
				designPanel.Dispose();
				designPanel = null;
			}
			base.Dispose(disposing);
		}
	}
}

