using System;
using System.IO;
using System.Linq;
using System.Windows;
using vault.UI.Localization;

namespace vault.UI
{
    public partial class RenameItemWindow : Window
    {
        private readonly bool _isFolder;
        private readonly string _originalExtension;
        private readonly string _originalFileName;

        public RenameItemWindow(string fileName, bool isFolder)
        {
            InitializeComponent();
            _isFolder = isFolder;
            _originalFileName = fileName ?? string.Empty;

            ApplyLocalization();

            if (isFolder)
            {
                NameTextBox.Text = _originalFileName;
                _originalExtension = string.Empty;
                ExtensionPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                string extWithDot = Path.GetExtension(_originalFileName);
                _originalExtension = extWithDot.TrimStart('.');

                string baseName = _originalFileName;
                if (!string.IsNullOrWhiteSpace(extWithDot))
                {
                    baseName = Path.GetFileNameWithoutExtension(_originalFileName);
                }

                NameTextBox.Text = baseName;
                ExtensionTextBox.Text = _originalExtension;
            }

            Loaded += (_, _) =>
            {
                NameTextBox.Focus();
                NameTextBox.SelectAll();
            };
        }

        public string ResultName { get; private set; } = string.Empty;

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            string namePart = (NameTextBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(namePart))
            {
                MessageBox.Show(UiText.Get("rename.msg.nameEmpty"));
                return;
            }

            if (namePart.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                MessageBox.Show(UiText.Get("rename.msg.invalidChars"));
                return;
            }

            if (_isFolder)
            {
                ResultName = namePart;
                DialogResult = true;
                return;
            }

            string extensionPart = (ExtensionTextBox.Text ?? string.Empty).Trim().TrimStart('.');
            if (extensionPart.Length > 16)
            {
                MessageBox.Show(UiText.Get("rename.msg.extensionTooLong"));
                return;
            }

            if (extensionPart.Length > 0 && !extensionPart.All(char.IsLetterOrDigit))
            {
                MessageBox.Show(UiText.Get("rename.msg.extensionInvalid"));
                return;
            }

            bool extensionChanged = !string.Equals(extensionPart, _originalExtension, StringComparison.OrdinalIgnoreCase);
            if (extensionChanged)
            {
                var warning = MessageBox.Show(
                    UiText.Get("rename.msg.extensionChanged"),
                    UiText.Get("rename.title.extensionWarning"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (warning != MessageBoxResult.Yes)
                    return;
            }

            string combined = extensionPart.Length == 0
                ? namePart
                : $"{namePart}.{extensionPart}";

            if (combined.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                MessageBox.Show(UiText.Get("rename.msg.fullNameInvalid"));
                return;
            }

            ResultName = combined;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void ApplyLocalization()
        {
            Title = UiText.Get("rename.windowTitle");
            TitleTextBlock.Text = _isFolder
                ? UiText.Get("rename.title.folder")
                : UiText.Get("rename.title.file");
            NameLabelTextBlock.Text = UiText.Get("rename.label.fileName");
            ExtensionLabelTextBlock.Text = UiText.Get("rename.label.extension");
            ExtensionHintTextBlock.Text = UiText.Get("rename.hint.extension");
            CancelButton.Content = UiText.Get("common.cancel");
            ConfirmRenameButton.Content = UiText.Get("rename.button.confirm");
        }
    }
}
