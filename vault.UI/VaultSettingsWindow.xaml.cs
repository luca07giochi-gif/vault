using System;
using System.Windows;
using System.Windows.Controls;
using vault.Core.Domain;
using vault.UI.Localization;

namespace vault.UI
{
    public partial class VaultSettingsWindow : Window
    {
        public VaultStorageFormat CurrentStorageFormat { get; }

        public VaultSettingsWindow(VaultStorageFormat currentStorageFormat)
        {
            InitializeComponent();
            CurrentStorageFormat = currentStorageFormat;

            ApplyLocalization();
            CurrentFormatTextBlock.Text = UiText.Format("settings.label.currentFormat", FormatToLabel(CurrentStorageFormat));
            StorageFormatComboBox.SelectedIndex = CurrentStorageFormat switch
            {
                VaultStorageFormat.Legacy => 0,
                VaultStorageFormat.Extended => 1,
                VaultStorageFormat.Ultra => 2,
                _ => 1
            };
        }

        public string NewPassword => NewPasswordBox.Password;

        public bool ShouldChangePassword =>
            !string.IsNullOrWhiteSpace(NewPasswordBox.Password) ||
            !string.IsNullOrWhiteSpace(ConfirmPasswordBox.Password);

        public VaultStorageFormat SelectedStorageFormat =>
            GetSelectedStorageFormat();

        public bool ShouldChangeStorageFormat =>
            SelectedStorageFormat != CurrentStorageFormat;

        public void ClearSensitiveInputs()
        {
            NewPasswordBox.Password = string.Empty;
            ConfirmPasswordBox.Password = string.Empty;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            bool wantsPasswordChange = ShouldChangePassword;
            bool wantsFormatChange = ShouldChangeStorageFormat;

            if (!wantsPasswordChange && !wantsFormatChange)
            {
                MessageBox.Show(UiText.Get("settings.msg.noChange"));
                return;
            }

            if (wantsPasswordChange)
            {
                if (string.IsNullOrWhiteSpace(NewPasswordBox.Password))
                {
                    MessageBox.Show(UiText.Get("settings.msg.enterNewPassword"));
                    return;
                }

                if (NewPasswordBox.Password != ConfirmPasswordBox.Password)
                {
                    MessageBox.Show(UiText.Get("settings.msg.passwordMismatch"));
                    return;
                }
            }

            // Best effort: remove confirmation copy before returning.
            ConfirmPasswordBox.Password = string.Empty;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            ClearSensitiveInputs();
            DialogResult = false;
        }

        private VaultStorageFormat GetSelectedStorageFormat()
        {
            if (StorageFormatComboBox.SelectedItem is not ComboBoxItem selected ||
                selected.Tag is not string tag)
            {
                return VaultStorageFormat.Extended;
            }

            if (tag.Equals("legacy", StringComparison.OrdinalIgnoreCase))
                return VaultStorageFormat.Legacy;

            if (tag.Equals("ultra", StringComparison.OrdinalIgnoreCase))
                return VaultStorageFormat.Ultra;

            return VaultStorageFormat.Extended;
        }

        private static string FormatToLabel(VaultStorageFormat format) =>
            format switch
            {
                VaultStorageFormat.Legacy => UiText.Get("format.short.legacy"),
                VaultStorageFormat.Ultra => UiText.Get("format.short.ultra"),
                _ => UiText.Get("format.short.extended")
            };

        private void ApplyLocalization()
        {
            Title = UiText.Get("settings.windowTitle");
            HeaderTextBlock.Text = UiText.Get("settings.header");
            PasswordHeaderTextBlock.Text = UiText.Get("settings.passwordHeader");
            NewPasswordLabelTextBlock.Text = UiText.Get("settings.label.newPassword");
            ConfirmPasswordLabelTextBlock.Text = UiText.Get("settings.label.confirmPassword");
            FormatHeaderTextBlock.Text = UiText.Get("settings.formatHeader");
            NewFormatLabelTextBlock.Text = UiText.Get("settings.label.newFormat");
            StorageFormatLegacyItem.Content = UiText.Get("format.legacy");
            StorageFormatExtendedItem.Content = UiText.Get("format.extended");
            StorageFormatUltraItem.Content = UiText.Get("format.ultra");
            FormatNoteTextBlock.Text = UiText.Get("format.note");
            CancelButton.Content = UiText.Get("common.cancel");
            ApplyButton.Content = UiText.Get("common.apply");
        }
    }
}
