
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using vault.Core;
using vault.Core.Domain;
using vault.UI.Localization;

namespace vault.UI
{
    public partial class MainWindow : Window
    {
        private const string TempOpenPrefix = "vault-open-";
        private const string InternalDragDataFormat = "vault.internal-item-ids";
        private const int TempDeleteRetryCount = 720; // 720 * 10s ~= 2 ore
        private static readonly TimeSpan TempDeleteRetryDelay = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan TempOpenFallbackDelay = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan ZipOpenFallbackDelay = TimeSpan.FromHours(12);
        private static readonly TimeSpan AutoVaultLockTimeout = TimeSpan.FromHours(1);
        private const string PreferencesFileName = "ui-preferences.json";

        private readonly VaultManager _vaultManager = new VaultManager();
        private bool _exportWarningAcknowledged;
        private bool _openWarningAcknowledged;
        private string _currentFolderPath = string.Empty;
        private Point _dragStartPoint;
        private bool _isInternalDragRunning;
        private bool _isMarqueeSelecting;
        private bool _mouseDownStartedOnItem;
        private Point _marqueeStartPoint;
        private readonly DispatcherTimer _autoLockTimer = new DispatcherTimer();
        private DateTime _lastUserActivityUtc = DateTime.UtcNow;
        private readonly string? _startupVaultPath;
        private bool _quickOpenMode;
        private bool _isApplyingLocalization;

        public MainWindow(string? startupVaultPath = null)
        {
            InitializeComponent();
            _startupVaultPath = NormalizeStartupVaultPath(startupVaultPath);
            _quickOpenMode = !string.IsNullOrWhiteSpace(_startupVaultPath);

            LoadUiPreferences();
            VaultText.SetLanguage(UiText.CurrentLanguage);
            ApplyLocalization();
            ConfigureStartupMode();
            Topmost = false;

            _autoLockTimer.Interval = TimeSpan.FromMinutes(1);
            _autoLockTimer.Tick += AutoLockTimer_Tick;
            _autoLockTimer.Start();

            PreviewMouseDown += (_, _) => RegisterUserActivity();
            PreviewKeyDown += (_, _) => RegisterUserActivity();
            Activated += (_, _) =>
            {
                RegisterUserActivity();
                Topmost = false;
            };
            Deactivated += (_, _) => Topmost = false;

            Closed += (_, _) => CleanupOrphanTempOpenFiles(TimeSpan.Zero);
            CleanupOrphanTempOpenFiles(TimeSpan.Zero);
            AggiornaUI();
        }

        private static string T(string key) => UiText.Get(key);

        private static string Tf(string key, params object[] args) => UiText.Format(key, args);

        private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isApplyingLocalization)
                return;

            if (LanguageComboBox.SelectedItem is not ComboBoxItem selected ||
                selected.Tag is not string languageCode)
            {
                return;
            }

            UiText.SetLanguage(languageCode);
            VaultText.SetLanguage(UiText.CurrentLanguage);
            SaveUiPreferences();
            ApplyLocalization();
            AggiornaUI();
        }

        private void ApplyLocalization()
        {
            _isApplyingLocalization = true;
            try
            {
                Title = T("main.windowTitle");
                MainTitleTextBlock.Text = T("main.title");
                LanguageLabelTextBlock.Text = T("main.languageLabel");
                PopulateLanguageComboBox();

                CreateVaultGroupBox.Header = T("main.group.create");
                CreatePasswordLabelTextBlock.Text = T("main.label.masterPassword");
                CreateConfirmPasswordLabelTextBlock.Text = T("main.label.confirmPassword");
                CreateFormatLabelTextBlock.Text = T("main.label.vaultFormat");
                CreateFormatSelectItem.Content = T("format.select");
                CreateFormatLegacyItem.Content = T("format.legacy");
                CreateFormatExtendedItem.Content = T("format.extended");
                CreateFormatUltraItem.Content = T("format.ultra");
                CreateFormatNoteTextBlock.Text = T("format.note");
                CreateVaultButton.Content = T("main.button.createVault");

                OpenVaultGroupBox.Header = T("main.group.open");
                OpenVaultFileLabelTextBlock.Text = T("main.label.vaultFile");
                BrowseVaultButton.Content = T("main.button.browse");
                OpenVaultPasswordLabelTextBlock.Text = T("main.label.masterPassword");
                OpenVaultButton.Content = T("main.button.openVault");

                QuickOpenTitleTextBlock.Text = T("main.quickOpenTitle");
                QuickOpenFileLabelTextBlock.Text = T("main.label.file");
                QuickOpenPasswordLabelTextBlock.Text = T("main.label.masterPassword");
                OpenStartupVaultButton.Content = T("main.button.openVault");
                ShowFullHomeButton.Content = T("main.button.showHome");

                FolderUpButton.ToolTip = T("main.tooltip.folderUp");
                FolderContentGroupBox.Header = T("main.group.folderContent");

                ContextOpenMenuItem.Header = T("main.ctx.open");
                ContextExportMenuItem.Header = T("main.ctx.export");
                ContextRenameMenuItem.Header = T("main.ctx.rename");
                ContextMoveMenuItem.Header = T("main.ctx.move");
                ContextDeleteMenuItem.Header = T("main.ctx.delete");
                ContextAddFileMenuItem.Header = T("main.ctx.addFile");
                ContextNewFolderMenuItem.Header = T("main.ctx.newFolder");
                ContextRefreshMenuItem.Header = T("main.ctx.refresh");

                NameColumn.Header = T("main.col.name");
                TypeColumn.Header = T("main.col.type");
                SizeColumn.Header = T("main.col.size");
                AddedColumn.Header = T("main.col.added");

                DragHintTextBlock.Text = T("main.dragHint");

                AddFileButton.Content = T("main.button.addFile");
                NewFolderButton.Content = T("main.button.newFolder");
                MoveItemsButton.Content = T("main.button.move");
                RenameItemButton.Content = T("main.button.rename");
                RemoveFileButton.Content = T("main.button.remove");
                ExportFileButton.Content = T("main.button.export");
                OpenFileButton.Content = T("main.button.open");
                VaultSettingsButton.Content = T("main.button.settings");
                CloseVaultButton.Content = T("main.button.closeVault");
            }
            finally
            {
                _isApplyingLocalization = false;
            }
        }

        private void PopulateLanguageComboBox()
        {
            LanguageComboBox.Items.Clear();

            AddLanguageItem("it");
            AddLanguageItem("en");
            AddLanguageItem("es");
            AddLanguageItem("fr");

            foreach (ComboBoxItem item in LanguageComboBox.Items)
            {
                if (item.Tag is string code &&
                    string.Equals(code, UiText.CurrentLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    LanguageComboBox.SelectedItem = item;
                    return;
                }
            }

            if (LanguageComboBox.Items.Count > 0)
                LanguageComboBox.SelectedIndex = 0;
        }

        private void AddLanguageItem(string code)
        {
            LanguageComboBox.Items.Add(new ComboBoxItem
            {
                Tag = code,
                Content = T($"lang.{code}")
            });
        }

        private Task RunLongOperationAsync(Func<Task> operation, string message, int showDelayMs = 600)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            return RunLongOperationAsync(_ => operation(), message, showDelayMs);
        }

        private async Task RunLongOperationAsync(
            Func<IProgress<double>, Task> operation,
            string message,
            int showDelayMs = 600)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            LoadingProgressWindow? progressWindow = null;
            double? lastPercent = null;
            using var showDelayCts = new CancellationTokenSource();

            var progress = new Progress<double>(value =>
            {
                if (double.IsNaN(value) || double.IsInfinity(value))
                    return;

                double clamped = Math.Max(0, Math.Min(100, value));
                lastPercent = clamped;
                progressWindow?.SetProgress(clamped);
            });

            Task showTask = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(showDelayMs, showDelayCts.Token);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        progressWindow = new LoadingProgressWindow(message) { Owner = this };
                        if (lastPercent.HasValue)
                        {
                            progressWindow.SetProgress(lastPercent.Value);
                        }
                        else
                        {
                            progressWindow.SetIndeterminate();
                        }

                        progressWindow.Show();
                    });
                }
                catch (TaskCanceledException)
                {
                    // Expected when operation ends quickly.
                }
            });

            try
            {
                await operation(progress);
            }
            finally
            {
                showDelayCts.Cancel();
                await showTask;

                if (progressWindow != null)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (progressWindow.IsVisible)
                            progressWindow.Close();
                    });
                }
            }
        }

        private Task<T> RunLongOperationAsync<T>(Func<Task<T>> operation, string message, int showDelayMs = 600)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            return RunLongOperationAsync(_ => operation(), message, showDelayMs);
        }

        private async Task<T> RunLongOperationAsync<T>(
            Func<IProgress<double>, Task<T>> operation,
            string message,
            int showDelayMs = 600)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            T result = default!;
            await RunLongOperationAsync(async progress =>
            {
                result = await operation(progress);
            }, message, showDelayMs);

            return result;
        }

        private static string? NormalizeStartupVaultPath(string? startupVaultPath)
        {
            if (!IsVaultExtension(startupVaultPath))
                return null;

            try
            {
                return Path.GetFullPath(startupVaultPath!);
            }
            catch
            {
                return startupVaultPath;
            }
        }

        private void ConfigureStartupMode()
        {
            if (string.IsNullOrWhiteSpace(_startupVaultPath))
                return;

            OpenVaultPathTextBox.Text = _startupVaultPath;
            StartupVaultPathText.Text = _startupVaultPath;

            if (!File.Exists(_startupVaultPath))
            {
                MessageBox.Show(
                    Tf("main.msg.startupVaultMissing", _startupVaultPath ?? string.Empty),
                    T("main.title.fileNotFound"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            Loaded += (_, _) =>
            {
                StartupVaultPasswordBox.Focus();
                StartupVaultPasswordBox.SelectAll();
            };
        }

        private void BrowseVault_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = T("main.dialog.vaultFilter"),
                DefaultExt = ".vault",
                CheckFileExists = true,
                CheckPathExists = true,
                Multiselect = false,
                FilterIndex = 1,
                Title = T("main.dialog.vaultSelectTitle")
            };

            while (dialog.ShowDialog() == true)
            {
                if (IsVaultExtension(dialog.FileName))
                {
                    OpenVaultPathTextBox.Text = dialog.FileName;
                    return;
                }

                MessageBox.Show(
                    T("main.msg.selectVaultExtension"),
                    T("main.title.invalidFormat"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private async void CreateVault_Click(object sender, RoutedEventArgs e)
        {
            string password = CreatePasswordBox.Password;
            string confirmPassword = CreateConfirmPasswordBox.Password;

            if (string.IsNullOrWhiteSpace(password))
            {
                MessageBox.Show(T("main.msg.enterCreatePassword"));
                return;
            }

            if (password != confirmPassword)
            {
                MessageBox.Show(T("main.msg.passwordMismatch"));
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = T("main.dialog.vaultFilter"),
                FileName = T("main.dialog.defaultVaultName")
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                if (!TryGetSelectedCreateVaultFormat(out VaultStorageFormat selectedFormat))
                {
                    MessageBox.Show(T("main.msg.selectFormat"));
                    return;
                }

                await RunLongOperationAsync(
                    progress => Task.Run(() => _vaultManager.CreateVault(dialog.FileName, password, selectedFormat, progress)),
                    T("main.progress.creatingVault"));

                _currentFolderPath = string.Empty;
                RegisterUserActivity();
                AggiornaUI();
                string formatLabel = StorageFormatToLabel(selectedFormat);
                MessageBox.Show(Tf("main.msg.vaultCreated", formatLabel));
            }
            catch (Exception ex)
            {
                MessageBox.Show(Tf("main.msg.errorCreating", ex.Message), T("common.error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                password = string.Empty;
                confirmPassword = string.Empty;
                CreatePasswordBox.Password = string.Empty;
                CreateConfirmPasswordBox.Password = string.Empty;
            }
        }

        private bool TryGetSelectedCreateVaultFormat(out VaultStorageFormat format)
        {
            format = VaultStorageFormat.Extended;

            if (CreateVaultFormatComboBox.SelectedItem is not ComboBoxItem selected ||
                selected.Tag is not string tag)
            {
                return false;
            }

            if (tag.Equals("legacy", StringComparison.OrdinalIgnoreCase))
            {
                format = VaultStorageFormat.Legacy;
                return true;
            }

            if (tag.Equals("extended", StringComparison.OrdinalIgnoreCase))
            {
                format = VaultStorageFormat.Extended;
                return true;
            }

            if (tag.Equals("ultra", StringComparison.OrdinalIgnoreCase))
            {
                format = VaultStorageFormat.Ultra;
                return true;
            }

            return false;
        }

        private async void OpenVault_Click(object sender, RoutedEventArgs e)
        {
            string path = OpenVaultPathTextBox.Text;
            string password = OpenPasswordBox.Password;

            try
            {
                await TryOpenVaultAsync(path, password);
            }
            finally
            {
                password = string.Empty;
                OpenPasswordBox.Password = string.Empty;
            }
        }

        private void OpenPasswordBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;

            e.Handled = true;
            OpenVault_Click(OpenVaultButton, new RoutedEventArgs());
        }

        private async void OpenStartupVault_Click(object sender, RoutedEventArgs e)
        {
            string path = _startupVaultPath ?? StartupVaultPathText.Text;
            string password = StartupVaultPasswordBox.Password;

            try
            {
                if (await TryOpenVaultAsync(path, password))
                {
                    _quickOpenMode = false;
                }
            }
            finally
            {
                password = string.Empty;
                StartupVaultPasswordBox.Password = string.Empty;
            }
        }

        private void StartupVaultPasswordBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;

            e.Handled = true;
            OpenStartupVault_Click(OpenStartupVaultButton, new RoutedEventArgs());
        }

        private void ShowFullHome_Click(object sender, RoutedEventArgs e)
        {
            _quickOpenMode = false;
            StartupVaultPasswordBox.Password = string.Empty;
            AggiornaUI();
            OpenPasswordBox.Focus();
        }

        private async Task<bool> TryOpenVaultAsync(string path, string password)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                MessageBox.Show(T("main.msg.selectVaultToOpen"));
                return false;
            }

            if (!File.Exists(path))
            {
                MessageBox.Show(T("main.msg.selectedFileMissing"));
                return false;
            }

            if (!IsVaultExtension(path))
            {
                MessageBox.Show(T("main.msg.selectedFileInvalid"));
                return false;
            }

            if (string.IsNullOrWhiteSpace(password))
            {
                MessageBox.Show(T("main.msg.enterOpenPassword"));
                return false;
            }

            try
            {
                await RunLongOperationAsync(
                    progress => Task.Run(() => _vaultManager.OpenVault(path, password, progress)),
                    T("main.progress.openingVault"));

                _currentFolderPath = string.Empty;
                RegisterUserActivity();
                AggiornaUI();
                MessageBox.Show(T("main.msg.vaultOpened"));
                return true;
            }
            catch (CryptographicException ex)
            {
                MessageBox.Show(
                    Tf("main.msg.remainingAttempts", ex.Message, _vaultManager.RemainingOpenAttempts),
                    T("common.error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }
            catch (Exception ex)
            {
                MessageBox.Show(Tf("main.msg.unexpectedError", ex.Message), T("common.error"), MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void FolderUp_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_currentFolderPath))
                return;

            int idx = _currentFolderPath.LastIndexOf('/');
            _currentFolderPath = idx < 0 ? string.Empty : _currentFolderPath[..idx];
            RefreshCurrentFolderItems();
        }

        private void ItemsListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            VaultFileItem? selected = GetItemAtPoint(e.GetPosition(ItemsListView));
            if (selected == null)
                return;

            if (selected.IsFolder)
            {
                _currentFolderPath = NormalizeFolderPath(selected.FullPath);
                RefreshCurrentFolderItems();
                return;
            }

            OpenItems(new[] { selected }, showFinalSummary: false, allowMultiConfirm: false);
        }

        private async void AddFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = T("main.dialog.allFilesFilter"),
                Multiselect = true,
                CheckFileExists = true
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                int added = await RunLongOperationAsync(
                    progress => Task.Run(() => _vaultManager.AddExternalPaths(dialog.FileNames, _currentFolderPath, progress)),
                    T("main.progress.addingFiles"));
                RefreshCurrentFolderItems();
                MessageBox.Show(Tf("main.msg.itemsAdded", added));
            }
            catch (Exception ex)
            {
                MessageBox.Show(Tf("main.msg.errorAddingFiles", ex.Message), T("common.error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void NewFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new TextInputWindow(
                T("move.prompt.newFolderTitle"),
                T("main.prompt.folderName"),
                T("move.prompt.create"))
            {
                Owner = this
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                _vaultManager.CreateFolder(dialog.InputValue, _currentFolderPath);
                RefreshCurrentFolderItems();
            }
            catch (Exception ex)
            {
                MessageBox.Show(Tf("main.msg.errorCreatingFolder", ex.Message), T("common.error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MoveItems_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedItems();
            if (selected.Count == 0)
            {
                MessageBox.Show(T("main.msg.selectAtLeastOneMove"));
                return;
            }

            var moveWindow = new MoveItemsWindow(_vaultManager, _currentFolderPath)
            {
                Owner = this
            };

            if (moveWindow.ShowDialog() != true)
                return;

            try
            {
                _vaultManager.MoveItems(selected.Select(i => i.Id), moveWindow.SelectedDestinationPath);
                EnsureCurrentFolderExists();
                RefreshCurrentFolderItems();
            }
            catch (Exception ex)
            {
                MessageBox.Show(Tf("main.msg.errorMoving", ex.Message), T("common.error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RenameItem_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedItems();
            if (selected.Count == 0)
            {
                MessageBox.Show(T("main.msg.selectOneRename"));
                return;
            }

            if (selected.Count > 1)
            {
                MessageBox.Show(T("main.msg.selectSingleRename"));
                return;
            }

            RenameSingleItem(selected[0]);
        }

        private void RemoveFile_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedItems();
            if (selected.Count == 0)
            {
                MessageBox.Show(T("main.msg.selectAtLeastOneList"));
                return;
            }

            var result = MessageBox.Show(
                Tf("main.msg.confirmDelete", selected.Count),
                T("main.title.confirmDelete"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            try
            {
                _vaultManager.DeleteItems(selected.Select(s => s.Id));
                EnsureCurrentFolderExists();
                RefreshCurrentFolderItems();
                MessageBox.Show(Tf("main.msg.itemsRemoved", selected.Count));
            }
            catch (Exception ex)
            {
                MessageBox.Show(Tf("main.msg.errorRemoving", ex.Message), T("common.error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ExportFile_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedItems();
            if (selected.Count == 0)
            {
                MessageBox.Show(T("main.msg.selectAtLeastOneFile"));
                return;
            }

            var files = selected.Where(s => !s.IsFolder).ToList();
            int skippedFolders = selected.Count - files.Count;
            if (files.Count == 0)
            {
                MessageBox.Show(T("main.msg.onlyFoldersUseMove"));
                return;
            }

            if (!_exportWarningAcknowledged)
            {
                var warningResult = MessageBox.Show(
                    T("main.msg.exportSecurityWarning"),
                    T("main.title.exportSecurityWarning"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (warningResult != MessageBoxResult.Yes)
                    return;

                _exportWarningAcknowledged = true;
            }

            try
            {
                if (files.Count == 1)
                {
                    var onlyFile = files[0];
                    var dialog = new SaveFileDialog
                    {
                        Filter = T("main.dialog.allFilesFilter"),
                        FileName = string.IsNullOrWhiteSpace(onlyFile.FileName) ? T("main.default.exportFileName") : onlyFile.FileName,
                        OverwritePrompt = true
                    };

                    if (dialog.ShowDialog() != true)
                        return;

                    await RunLongOperationAsync(
                        progress => Task.Run(() => _vaultManager.ExportFile(onlyFile.Id, dialog.FileName, progress)),
                        T("main.progress.exporting"));
                    MessageBox.Show(T("main.msg.fileExported"));
                    return;
                }

                int exported = 0;
                foreach (var file in files)
                {
                    var dialog = new SaveFileDialog
                    {
                        Filter = T("main.dialog.allFilesFilter"),
                        FileName = string.IsNullOrWhiteSpace(file.FileName) ? T("main.default.exportFileName") : file.FileName,
                        OverwritePrompt = true
                    };

                    if (dialog.ShowDialog() != true)
                        continue;

                    await RunLongOperationAsync(
                        progress => Task.Run(() => _vaultManager.ExportFile(file.Id, dialog.FileName, progress)),
                        T("main.progress.exporting"));
                    exported++;
                }

                string suffix = skippedFolders > 0 ? $"\n{Tf("main.msg.foldersIgnored", skippedFolders)}" : string.Empty;
                MessageBox.Show(Tf("main.msg.filesExported", exported, files.Count, suffix));
            }
            catch (Exception ex)
            {
                MessageBox.Show(Tf("main.msg.errorExporting", ex.Message), T("common.error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedItems();
            if (selected.Count == 0)
            {
                MessageBox.Show(T("main.msg.selectAtLeastOneFile"));
                return;
            }

            OpenItems(selected, showFinalSummary: true, allowMultiConfirm: true);
        }

        private void OpenItems(
            IReadOnlyCollection<VaultFileItem> selected,
            bool showFinalSummary,
            bool allowMultiConfirm)
        {
            var files = selected.Where(s => !s.IsFolder).ToList();
            int skippedFolders = selected.Count - files.Count;
            if (files.Count == 0)
            {
                MessageBox.Show(T("main.msg.onlyFoldersDoubleClick"));
                return;
            }

            if (!_openWarningAcknowledged)
            {
                var warningWindow = new OpenFileSecurityWarningWindow { Owner = this };
                bool? dialogResult = warningWindow.ShowDialog();
                if (dialogResult != true)
                    return;

                if (warningWindow.DoNotShowAgain)
                {
                    _openWarningAcknowledged = true;
                    SaveUiPreferences();
                }
            }

            if (allowMultiConfirm && files.Count > 1)
            {
                var confirm = MessageBox.Show(
                    Tf("main.msg.confirmMultiOpen", files.Count),
                    T("main.title.confirmMultiOpen"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (confirm != MessageBoxResult.Yes)
                    return;
            }

            int opened = 0;
            foreach (var file in files)
            {
                try
                {
                    OpenFileFromVault(file);
                    opened++;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        Tf("main.msg.cannotOpenFile", file.FileName, ex.Message),
                        T("main.title.fileOpenError"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }

            if (showFinalSummary && (files.Count > 1 || skippedFolders > 0))
            {
                string suffix = skippedFolders > 0 ? $"\n{Tf("main.msg.foldersIgnored", skippedFolders)}" : string.Empty;
                MessageBox.Show(
                    Tf("main.msg.filesOpened", opened, files.Count, suffix, T("main.msg.tempCleanup")));
            }
        }

        private async void VaultSettings_Click(object sender, RoutedEventArgs e)
        {
            VaultStorageFormat currentFormat =
                _vaultManager.CurrentVaultStorageFormat ?? VaultStorageFormat.Extended;

            var settingsWindow = new VaultSettingsWindow(currentFormat) { Owner = this };
            if (settingsWindow.ShowDialog() != true)
            {
                settingsWindow.ClearSensitiveInputs();
                return;
            }

            string newPassword = settingsWindow.NewPassword;
            try
            {
                var completedActions = new List<string>();

                if (settingsWindow.ShouldChangePassword)
                {
                    await RunLongOperationAsync(
                        progress => Task.Run(() =>
                        {
                            _vaultManager.ChangePassword(newPassword, progress);
                            return 0;
                        }),
                        T("main.progress.updatingPassword"));
                    completedActions.Add(T("main.msg.passwordUpdated"));
                }

                if (settingsWindow.ShouldChangeStorageFormat)
                {
                    VaultStorageFormat newFormat = settingsWindow.SelectedStorageFormat;
                    await RunLongOperationAsync(
                        progress => Task.Run(() =>
                        {
                            _vaultManager.ChangeStorageFormat(newFormat, progress);
                            return 0;
                        }),
                        T("main.progress.convertingFormat"));
                    completedActions.Add(
                        Tf("main.msg.formatConverted", StorageFormatToLabel(newFormat)));
                }

                AggiornaUI();

                if (completedActions.Count == 0)
                {
                    MessageBox.Show(T("main.msg.noChangesApplied"));
                }
                else
                {
                    MessageBox.Show(string.Join("\n", completedActions));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    Tf("main.msg.errorSettings", ex.Message),
                    T("common.error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                newPassword = string.Empty;
                settingsWindow.ClearSensitiveInputs();
            }
        }

        private void CloseVault_Click(object sender, RoutedEventArgs e)
        {
            _vaultManager.CloseVault();
            _currentFolderPath = string.Empty;
            AggiornaUI();
        }

        private void RefreshFolder_Click(object sender, RoutedEventArgs e)
        {
            RefreshCurrentFolderItems();
        }

        private void AutoLockTimer_Tick(object? sender, EventArgs e)
        {
            if (!_vaultManager.IsVaultOpen)
                return;

            if (DateTime.UtcNow - _lastUserActivityUtc < AutoVaultLockTimeout)
                return;

            _vaultManager.CloseVault();
            _currentFolderPath = string.Empty;
            AggiornaUI();

            MessageBox.Show(
                T("main.msg.autoLock"),
                T("main.title.autoLock"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            RegisterUserActivity();
        }

        private void RegisterUserActivity()
        {
            _lastUserActivityUtc = DateTime.UtcNow;
        }

        private void ItemsListView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(ItemsListView);
            VaultFileItem? clickedItem = GetItemAtPoint(_dragStartPoint);
            _mouseDownStartedOnItem = clickedItem != null;

            if (_mouseDownStartedOnItem)
            {
                HideSelectionMarquee();
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.None)
            {
                ItemsListView.SelectedItems.Clear();
            }

            _isMarqueeSelecting = true;
            _marqueeStartPoint = _dragStartPoint;
            ShowSelectionMarquee(new Rect(_marqueeStartPoint, _marqueeStartPoint));
            ItemsListView.CaptureMouse();
            e.Handled = true;
        }

        private void ItemsListView_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _mouseDownStartedOnItem = false;
            if (_isMarqueeSelecting)
            {
                FinishMarqueeSelection();
                e.Handled = true;
            }
        }

        private void ItemsListView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            VaultFileItem? clickedItem = GetItemAtPoint(e.GetPosition(ItemsListView));
            if (clickedItem == null)
            {
                ItemsListView.SelectedItems.Clear();
                return;
            }

            bool alreadySelected = GetSelectedItems().Any(item => item.Id == clickedItem.Id);
            if (alreadySelected)
                return;

            ItemsListView.SelectedItems.Clear();
            ItemsListView.SelectedItem = clickedItem;
        }

        private void ItemsListView_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isMarqueeSelecting)
            {
                if (e.LeftButton != MouseButtonState.Pressed)
                {
                    FinishMarqueeSelection();
                    return;
                }

                UpdateMarqueeSelection(e.GetPosition(ItemsListView));
                e.Handled = true;
                return;
            }

            if (_isInternalDragRunning)
                return;

            if (!_mouseDownStartedOnItem)
                return;

            if (e.LeftButton != MouseButtonState.Pressed)
                return;

            Point currentPosition = e.GetPosition(ItemsListView);
            Vector delta = currentPosition - _dragStartPoint;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            VaultFileItem? startItem = GetItemAtPoint(_dragStartPoint);
            if (startItem == null)
                return;

            var selected = GetSelectedItems();
            if (selected.Count == 0 || !selected.Any(s => s.Id == startItem.Id))
                return;

            string[] ids = selected.Select(s => s.Id.ToString("D")).ToArray();
            var payload = new DataObject();
            payload.SetData(InternalDragDataFormat, ids);

            try
            {
                _isInternalDragRunning = true;
                DragDrop.DoDragDrop(ItemsListView, payload, DragDropEffects.Move);
            }
            finally
            {
                _isInternalDragRunning = false;
                _mouseDownStartedOnItem = false;
            }
        }

        private void ItemsListView_DragOver(object sender, DragEventArgs e)
        {
            SetDropEffects(e);
        }

        private void ItemsListView_Drop(object sender, DragEventArgs e)
        {
            if (!_vaultManager.IsVaultOpen)
                return;

            string destination = GetDropDestinationFolder(e);
            ExecuteDropToDestination(e, destination);
        }

        private void BreadcrumbHost_DragOver(object sender, DragEventArgs e)
        {
            SetDropEffects(e);
        }

        private void BreadcrumbHost_Drop(object sender, DragEventArgs e)
        {
            ExecuteDropToDestination(e, _currentFolderPath);
        }

        private void BreadcrumbSegment_DragOver(object sender, DragEventArgs e)
        {
            SetDropEffects(e);
            e.Handled = true;
        }

        private void BreadcrumbSegment_Drop(object sender, DragEventArgs e)
        {
            string destination = GetBreadcrumbPathFromSender(sender) ?? _currentFolderPath;
            ExecuteDropToDestination(e, destination);
        }

        private void BreadcrumbSegment_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            string? destination = GetBreadcrumbPathFromSender(sender);
            if (destination == null)
                return;

            _currentFolderPath = destination;
            RefreshCurrentFolderItems();
            e.Handled = true;
        }

        private static string? GetBreadcrumbPathFromSender(object sender)
        {
            if (sender is not FrameworkElement element)
                return null;

            if (element.Tag is not string tag)
                return null;

            return NormalizeFolderPath(tag);
        }

        private void SetDropEffects(DragEventArgs e)
        {
            if (!_vaultManager.IsVaultOpen)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                return;
            }

            if (e.Data.GetDataPresent(InternalDragDataFormat))
            {
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
                return;
            }

            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        private async void ExecuteDropToDestination(DragEventArgs e, string destination)
        {
            if (!_vaultManager.IsVaultOpen)
                return;

            try
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped || dropped.Length == 0)
                        return;

                    int added = await RunLongOperationAsync(
                        progress => Task.Run(() => _vaultManager.AddExternalPaths(dropped, destination, progress)),
                        T("main.progress.addingFiles"));
                    RefreshCurrentFolderItems();
                    MessageBox.Show(Tf("main.msg.itemsAdded", added));
                    e.Handled = true;
                    return;
                }

                if (e.Data.GetDataPresent(InternalDragDataFormat))
                {
                    if (e.Data.GetData(InternalDragDataFormat) is not string[] idStrings || idStrings.Length == 0)
                        return;

                    var ids = new List<Guid>();
                    foreach (string idString in idStrings)
                    {
                        if (Guid.TryParse(idString, out Guid parsed))
                            ids.Add(parsed);
                    }

                    if (ids.Count == 0)
                        return;

                    await RunLongOperationAsync(
                        () => Task.Run(() =>
                        {
                            _vaultManager.MoveItems(ids, destination);
                            return 0;
                        }),
                        T("main.progress.moving"));
                    EnsureCurrentFolderExists();
                    RefreshCurrentFolderItems();
                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(Tf("main.msg.operationNotCompleted", ex.Message), T("common.error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string GetDropDestinationFolder(DragEventArgs e)
        {
            Point position = e.GetPosition(ItemsListView);
            VaultFileItem? hovered = GetItemAtPoint(position);
            if (hovered != null && hovered.IsFolder)
                return hovered.FullPath;

            return _currentFolderPath;
        }

        private VaultFileItem? GetItemAtPoint(Point point)
        {
            DependencyObject? element = ItemsListView.InputHitTest(point) as DependencyObject;
            while (element != null && element is not ListViewItem)
                element = VisualTreeHelper.GetParent(element);

            if (element is not ListViewItem listViewItem)
                return null;

            return listViewItem.Content as VaultFileItem;
        }

        private void UpdateMarqueeSelection(Point currentPoint)
        {
            Rect selectionRect = GetNormalizedSelectionRect(_marqueeStartPoint, currentPoint);
            ShowSelectionMarquee(selectionRect);

            ItemsListView.SelectedItems.Clear();
            foreach (var item in ItemsListView.Items.OfType<VaultFileItem>())
            {
                if (ItemsListView.ItemContainerGenerator.ContainerFromItem(item) is not ListViewItem container)
                    continue;

                Point topLeft = container.TranslatePoint(new Point(0, 0), ItemsListView);
                Rect itemRect = new Rect(topLeft, new Size(container.ActualWidth, container.ActualHeight));
                if (selectionRect.IntersectsWith(itemRect))
                {
                    ItemsListView.SelectedItems.Add(item);
                }
            }
        }

        private void FinishMarqueeSelection()
        {
            _isMarqueeSelecting = false;
            if (ItemsListView.IsMouseCaptured)
            {
                ItemsListView.ReleaseMouseCapture();
            }

            HideSelectionMarquee();
        }

        private static Rect GetNormalizedSelectionRect(Point a, Point b)
        {
            double left = Math.Min(a.X, b.X);
            double top = Math.Min(a.Y, b.Y);
            double width = Math.Abs(a.X - b.X);
            double height = Math.Abs(a.Y - b.Y);
            return new Rect(left, top, width, height);
        }

        private void ShowSelectionMarquee(Rect rect)
        {
            Canvas.SetLeft(SelectionMarquee, rect.Left);
            Canvas.SetTop(SelectionMarquee, rect.Top);
            SelectionMarquee.Width = rect.Width;
            SelectionMarquee.Height = rect.Height;
            SelectionMarquee.Visibility = Visibility.Visible;
        }

        private void HideSelectionMarquee()
        {
            SelectionMarquee.Visibility = Visibility.Collapsed;
            SelectionMarquee.Width = 0;
            SelectionMarquee.Height = 0;
        }

        private void AggiornaUI()
        {
            bool open = _vaultManager.IsVaultOpen;

            SetupPanel.Visibility = !open && !_quickOpenMode ? Visibility.Visible : Visibility.Collapsed;
            QuickOpenPanel.Visibility = !open && _quickOpenMode ? Visibility.Visible : Visibility.Collapsed;
            VaultPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

            if (!open)
            {
                ItemsListView.ItemsSource = null;
                OpenedVaultPathText.Text = string.Empty;
                RefreshBreadcrumbBar();
                FolderUpButton.IsEnabled = false;
                HideSelectionMarquee();

                if (_quickOpenMode && !string.IsNullOrWhiteSpace(_startupVaultPath))
                {
                    StartupVaultPathText.Text = _startupVaultPath;
                }

                return;
            }

            string openFormatLabel = StorageFormatToLabel(_vaultManager.CurrentVaultStorageFormat ?? VaultStorageFormat.Extended);
            OpenedVaultPathText.Text = Tf("main.msg.openedVaultPath", openFormatLabel, _vaultManager.CurrentVaultPath ?? string.Empty);
            EnsureCurrentFolderExists();
            RefreshCurrentFolderItems();
        }

        private void RefreshCurrentFolderItems()
        {
            if (!_vaultManager.IsVaultOpen)
                return;

            EnsureCurrentFolderExists();

            ItemsListView.ItemsSource = _vaultManager.GetItemsInFolder(_currentFolderPath);
            ItemsListView.Items.Refresh();

            RefreshBreadcrumbBar();

            FolderUpButton.IsEnabled = !string.IsNullOrWhiteSpace(_currentFolderPath);
        }

        private void RefreshBreadcrumbBar()
        {
            if (BreadcrumbPanel == null)
                return;

            BreadcrumbPanel.Children.Clear();

            bool atRoot = string.IsNullOrWhiteSpace(_currentFolderPath);
            AddBreadcrumbSegment("/", string.Empty, atRoot);

            if (atRoot)
                return;

            string[] segments = _currentFolderPath
                .Split('/', StringSplitOptions.RemoveEmptyEntries);

            string cumulativePath = string.Empty;
            for (int i = 0; i < segments.Length; i++)
            {
                BreadcrumbPanel.Children.Add(new TextBlock
                {
                    Text = ">",
                    Margin = new Thickness(6, 2, 6, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromRgb(182, 192, 212))
                });

                cumulativePath = string.IsNullOrWhiteSpace(cumulativePath)
                    ? segments[i]
                    : $"{cumulativePath}/{segments[i]}";

                AddBreadcrumbSegment(
                    segments[i],
                    cumulativePath,
                    isCurrent: i == segments.Length - 1);
            }
        }

        private void AddBreadcrumbSegment(string label, string path, bool isCurrent)
        {
            var text = new TextBlock
            {
                Text = label,
                FontSize = 13,
                FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = new SolidColorBrush(Colors.White),
                VerticalAlignment = VerticalAlignment.Center
            };

            var border = new Border
            {
                Child = text,
                Tag = path,
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 2, 0),
                CornerRadius = new CornerRadius(5),
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(1),
                Background = isCurrent
                    ? new SolidColorBrush(Color.FromRgb(59, 79, 114))
                    : new SolidColorBrush(Color.FromRgb(38, 44, 58)),
                BorderBrush = isCurrent
                    ? new SolidColorBrush(Color.FromRgb(110, 140, 192))
                    : new SolidColorBrush(Color.FromRgb(58, 66, 82)),
                AllowDrop = true
            };

            border.MouseLeftButtonUp += BreadcrumbSegment_MouseLeftButtonUp;
            border.DragOver += BreadcrumbSegment_DragOver;
            border.Drop += BreadcrumbSegment_Drop;

            BreadcrumbPanel.Children.Add(border);
        }

        private void EnsureCurrentFolderExists()
        {
            if (!_vaultManager.IsVaultOpen)
            {
                _currentFolderPath = string.Empty;
                return;
            }

            _currentFolderPath = NormalizeFolderPath(_currentFolderPath);
            bool exists = _vaultManager.GetAllFolderPaths().Any(path =>
                string.Equals(path, _currentFolderPath, StringComparison.OrdinalIgnoreCase));

            if (!exists)
                _currentFolderPath = string.Empty;
        }

        private void RenameSingleItem(VaultFileItem item)
        {
            var dialog = new RenameItemWindow(item.FileName, item.IsFolder)
            {
                Owner = this
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                _vaultManager.RenameItem(item.Id, dialog.ResultName);
                EnsureCurrentFolderExists();
                RefreshCurrentFolderItems();
            }
            catch (Exception ex)
            {
                MessageBox.Show(Tf("main.msg.renameError", ex.Message), T("common.error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string NormalizeFolderPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            string normalized = path.Replace('\\', '/').Trim().Trim('/');
            return normalized.Length == 0 ? string.Empty : normalized;
        }

        private string StorageFormatToLabel(VaultStorageFormat format) =>
            format switch
            {
                VaultStorageFormat.Legacy => T("format.short.legacy"),
                VaultStorageFormat.Ultra => T("format.short.ultra"),
                _ => T("format.short.extended")
            };

        private static bool IsVaultExtension(string? path) =>
            !string.IsNullOrWhiteSpace(path) &&
            string.Equals(Path.GetExtension(path), ".vault", StringComparison.OrdinalIgnoreCase);

        private List<VaultFileItem> GetSelectedItems() =>
            ItemsListView.SelectedItems.Cast<VaultFileItem>().ToList();

        private void OpenFileFromVault(VaultFileItem file)
        {
            if (file.ContentLength == 0)
                throw new InvalidOperationException(T("main.msg.emptyVaultFile"));

            string safeFileName = SanitizeFileName(file.FileName);
            string tempDir = Path.Combine(Path.GetTempPath(), $"{TempOpenPrefix}{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            string tempPath = Path.Combine(tempDir, safeFileName);
            using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                foreach (var chunk in file.GetContentChunks())
                {
                    if (chunk.Length == 0)
                        continue;

                    output.Write(chunk, 0, chunk.Length);
                }

                output.Flush(true);
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = tempPath,
                    UseShellExecute = true,
                    Verb = "open",
                    WorkingDirectory = tempDir
                };

                Process? startedProcess = Process.Start(startInfo);
                if (startedProcess == null)
                    throw new InvalidOperationException(T("main.msg.fileLaunchFailed"));

                string extension = Path.GetExtension(safeFileName);
                _ = CleanupOpenedFileAsync(startedProcess, tempPath, tempDir, extension);
            }
            catch
            {
                TrySecureDeleteFile(tempPath);
                TryDeleteDirectory(tempDir);
                throw;
            }
        }

        private static string SanitizeFileName(string? fileName)
        {
            string candidate = string.IsNullOrWhiteSpace(fileName) ? "vault-file.bin" : fileName;
            foreach (char ch in Path.GetInvalidFileNameChars())
                candidate = candidate.Replace(ch, '_');

            return string.IsNullOrWhiteSpace(candidate) ? "vault-file.bin" : candidate;
        }

        private static async Task CleanupOpenedFileAsync(
            Process process,
            string filePath,
            string tempDir,
            string extension)
        {
            bool processEnded = false;
            bool fallbackAlreadyElapsed = false;
            TimeSpan fallbackDelay = extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                ? ZipOpenFallbackDelay
                : TempOpenFallbackDelay;

            try
            {
                Task waitTask = process.WaitForExitAsync();

                // If shell process exits immediately (common with .zip / explorer), do not trust it.
                Task firstObservation = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(2)));
                if (firstObservation != waitTask)
                {
                    Task completed = await Task.WhenAny(waitTask, Task.Delay(fallbackDelay));
                    if (completed == waitTask)
                    {
                        processEnded = true;
                    }
                    else
                    {
                        fallbackAlreadyElapsed = true;
                    }
                }
            }
            catch
            {
                // Some shell-opened processes are not awaitable from here.
            }
            finally
            {
                process.Dispose();
            }

            if (!processEnded && !fallbackAlreadyElapsed)
            {
                // Safety > strict cleanup timing: keep temp file alive a bit longer
                // to avoid "file not found / moved" while external app opens it.
                await Task.Delay(fallbackDelay);
            }

            for (int i = 0; i < TempDeleteRetryCount; i++)
            {
                if (TrySecureDeleteFile(filePath))
                {
                    TryDeleteDirectory(tempDir);
                    return;
                }

                await Task.Delay(TempDeleteRetryDelay);
            }
        }

        private static bool TrySecureDeleteFile(string filePath)
        {
            if (!File.Exists(filePath))
                return true;

            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None);
                long len = fs.Length;
                byte[] wipe = new byte[8192];
                long written = 0;

                while (written < len)
                {
                    int toWrite = (int)Math.Min(wipe.Length, len - written);
                    fs.Write(wipe, 0, toWrite);
                    written += toWrite;
                }

                fs.Flush(true);
            }
            catch
            {
                return false;
            }

            try
            {
                File.Delete(filePath);
                return !File.Exists(filePath);
            }
            catch
            {
                return false;
            }
        }

        private static void TryDeleteDirectory(string dirPath)
        {
            if (!Directory.Exists(dirPath))
                return;

            try
            {
                Directory.Delete(dirPath, true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }

        private static void CleanupOrphanTempOpenFiles(TimeSpan minAge)
        {
            try
            {
                string baseTemp = Path.GetTempPath();
                string[] dirs = Directory.GetDirectories(baseTemp, $"{TempOpenPrefix}*");
                foreach (string dir in dirs)
                {
                    try
                    {
                        DateTime lastWrite = Directory.GetLastWriteTimeUtc(dir);
                        TimeSpan age = DateTime.UtcNow - lastWrite;
                        if (age < minAge)
                            continue;

                        foreach (string file in Directory.GetFiles(dir))
                        {
                            TrySecureDeleteFile(file);
                        }

                        TryDeleteDirectory(dir);
                    }
                    catch
                    {
                        // Best effort cleanup.
                    }
                }
            }
            catch
            {
                // Best effort cleanup.
            }
        }

        private static string GetPreferencesPath() =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "vault.UI",
                PreferencesFileName);

        private sealed class UiPreferences
        {
            public bool SuppressOpenWarning { get; set; }
            public string Language { get; set; } = "it";
        }

        private void LoadUiPreferences()
        {
            _openWarningAcknowledged = false;
            UiText.SetLanguage("it");
            try
            {
                string path = GetPreferencesPath();
                if (!File.Exists(path))
                    return;

                string content = File.ReadAllText(path);
                UiPreferences? preferences = JsonSerializer.Deserialize<UiPreferences>(
                    content,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (preferences == null)
                {
                    _openWarningAcknowledged = content.Contains("\"suppressOpenWarning\":true", StringComparison.OrdinalIgnoreCase);
                    return;
                }

                _openWarningAcknowledged = preferences.SuppressOpenWarning;
                UiText.SetLanguage(preferences.Language);
            }
            catch
            {
                _openWarningAcknowledged = false;
                UiText.SetLanguage("it");
            }
        }

        private void SaveUiPreferences()
        {
            try
            {
                string path = GetPreferencesPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                var preferences = new UiPreferences
                {
                    SuppressOpenWarning = _openWarningAcknowledged,
                    Language = UiText.CurrentLanguage
                };

                string json = JsonSerializer.Serialize(preferences);
                File.WriteAllText(path, json);
            }
            catch
            {
                // Best effort persistence.
            }
        }
    }
}
