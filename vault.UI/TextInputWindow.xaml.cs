using System.Windows;
using vault.UI.Localization;

namespace vault.UI
{
    public partial class TextInputWindow : Window
    {
        public TextInputWindow(string title, string prompt, string confirmButtonText, string initialValue = "")
        {
            InitializeComponent();

            Title = title;
            PromptTextBlock.Text = prompt;
            ConfirmButton.Content = confirmButtonText;
            CancelButton.Content = UiText.Get("common.cancel");
            ValueTextBox.Text = initialValue ?? string.Empty;

            Loaded += (_, _) =>
            {
                ValueTextBox.Focus();
                ValueTextBox.SelectAll();
            };
        }

        public string InputValue => ValueTextBox.Text.Trim();

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(InputValue))
            {
                MessageBox.Show(UiText.Get("textInput.msg.enterValue"));
                return;
            }

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
