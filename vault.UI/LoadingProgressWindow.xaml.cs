using System.Windows;
using vault.UI.Localization;

namespace vault.UI
{
    public partial class LoadingProgressWindow : Window
    {
        public LoadingProgressWindow(string message)
        {
            InitializeComponent();
            Title = UiText.Get("loading.windowTitle");
            MessageTextBlock.Text = message;
            PercentTextBlock.Text = UiText.Get("loading.inProgress");
        }

        public void SetIndeterminate()
        {
            OperationProgressBar.IsIndeterminate = true;
            PercentTextBlock.Text = UiText.Get("loading.inProgress");
        }

        public void SetProgress(double percent)
        {
            double clamped = percent;
            if (double.IsNaN(clamped) || double.IsInfinity(clamped))
                clamped = 0;

            if (clamped < 0) clamped = 0;
            if (clamped > 100) clamped = 100;

            OperationProgressBar.IsIndeterminate = false;
            OperationProgressBar.Value = clamped;
            PercentTextBlock.Text = $"{clamped:0}%";
        }
    }
}
