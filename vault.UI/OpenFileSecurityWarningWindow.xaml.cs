using System.Windows;
using vault.UI.Localization;

namespace vault.UI
{
    public partial class OpenFileSecurityWarningWindow : Window
    {
        public OpenFileSecurityWarningWindow()
        {
            InitializeComponent();
            ApplyLocalization();
        }

        public bool DoNotShowAgain => DoNotShowAgainCheckBox.IsChecked == true;

        private void Continue_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void ApplyLocalization()
        {
            Title = UiText.Get("openWarn.windowTitle");
            BodyTextBlock.Text = UiText.Get("openWarn.body");
            DoNotShowAgainCheckBox.Content = UiText.Get("openWarn.doNotShow");
            CancelButton.Content = UiText.Get("common.cancel");
            ContinueButton.Content = UiText.Get("common.continue");
        }
    }
}
