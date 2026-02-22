using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CoreGraphics;
using Foundation;
using UIKit;
using vault.Core.Domain;

namespace vault.iOS
{
    public sealed class MainViewController : UIViewController
    {
        private const string CellId = "VaultItemCell";

        private readonly List<VaultFileItem> _visibleItems = new();
        private readonly HashSet<string> _temporaryFiles = new(StringComparer.OrdinalIgnoreCase);

        private VaultPortableReader? _session;
        private NSUrl? _vaultUrl;
        private string _currentFolder = string.Empty;

        private UITableView? _tableView;
        private UILabel? _emptyLabel;
        private UIView? _busyOverlay;
        private UIActivityIndicatorView? _busyIndicator;
        private UILabel? _busyLabel;

        private UIBarButtonItem? _openVaultButton;
        private UIBarButtonItem? _addFileButton;
        private UIBarButtonItem? _upButton;

        private UIDocumentInteractionController? _documentInteractionController;
        private DocumentInteractionDelegate? _documentInteractionDelegate;
        private PickerDelegate? _pickerDelegate;

        public override void ViewDidLoad()
        {
            base.ViewDidLoad();

            View!.BackgroundColor = UIColor.White;

            _tableView = new UITableView(View.Bounds, UITableViewStyle.InsetGrouped)
            {
                AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight,
                Source = new VaultTableSource(this)
            };
            View.AddSubview(_tableView);

            _emptyLabel = new UILabel(View.Bounds)
            {
                AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight,
                TextAlignment = UITextAlignment.Center,
                Lines = 0,
                Font = UIFont.SystemFontOfSize(16),
                TextColor = UIColor.DarkGray,
                Text = "Tocca \"Apri vault\" per selezionare un file .vault.",
                Hidden = false
            };
            View.AddSubview(_emptyLabel);

            BuildBusyOverlay();
            ConfigureNavigationItems();
            UpdateUiState();
        }

        public override void ViewDidLayoutSubviews()
        {
            base.ViewDidLayoutSubviews();

            if (_busyOverlay != null)
            {
                _busyOverlay.Frame = View.Bounds;
            }

            if (_busyIndicator != null && _busyLabel != null)
            {
                const float indicatorSize = 56f;
                const float labelWidth = 280f;
                const float labelHeight = 48f;
                float centerX = (float)View.Bounds.GetMidX();
                float centerY = (float)View.Bounds.GetMidY();

                _busyIndicator.Frame = new CGRect(centerX - indicatorSize / 2f, centerY - 52f, indicatorSize, indicatorSize);
                _busyLabel.Frame = new CGRect(centerX - labelWidth / 2f, centerY + 8f, labelWidth, labelHeight);
            }
        }

        private void BuildBusyOverlay()
        {
            _busyOverlay = new UIView(View.Bounds)
            {
                AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight,
                BackgroundColor = UIColor.Black.ColorWithAlpha(0.35f),
                Hidden = true
            };

            _busyIndicator = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Large)
            {
                Color = UIColor.White
            };

            _busyLabel = new UILabel
            {
                Font = UIFont.SystemFontOfSize(15, UIFontWeight.Medium),
                TextColor = UIColor.White,
                TextAlignment = UITextAlignment.Center,
                Lines = 2,
                Text = "Operazione in corso..."
            };

            _busyOverlay.AddSubview(_busyIndicator);
            _busyOverlay.AddSubview(_busyLabel);
            View.AddSubview(_busyOverlay);
        }

        private void ConfigureNavigationItems()
        {
            _openVaultButton = new UIBarButtonItem(
                "Apri vault",
                UIBarButtonItemStyle.Plain,
                (_, _) => _ = PickVaultToOpenAsync());

            _addFileButton = new UIBarButtonItem(
                "Aggiungi file",
                UIBarButtonItemStyle.Plain,
                (_, _) => _ = PickFilesToAddAsync());

            _upButton = new UIBarButtonItem(
                "Su",
                UIBarButtonItemStyle.Plain,
                (_, _) => NavigateUp());

            NavigationItem.LeftBarButtonItem = _openVaultButton;
            NavigationItem.RightBarButtonItems = new[] { _addFileButton, _upButton };
        }

        private void UpdateUiState()
        {
            bool hasVault = _session != null;
            bool canGoUp = hasVault && !string.IsNullOrWhiteSpace(_currentFolder);

            if (_addFileButton != null)
                _addFileButton.Enabled = hasVault;

            if (_upButton != null)
                _upButton.Enabled = canGoUp;

            if (!hasVault)
            {
                Title = "Cassaforte iOS";
                if (_emptyLabel != null)
                {
                    _emptyLabel.Hidden = false;
                    _emptyLabel.Text = "Tocca \"Apri vault\" per selezionare un file .vault.";
                }
                return;
            }

            string titlePath = string.IsNullOrWhiteSpace(_currentFolder) ? "/" : $"/{_currentFolder}";
            Title = titlePath;

            if (_emptyLabel != null)
            {
                _emptyLabel.Hidden = _visibleItems.Count > 0;
                _emptyLabel.Text = "Cartella vuota.";
            }
        }

        private async Task PickVaultToOpenAsync()
        {
            PresentDocumentPicker(
                allowsMultipleSelection: false,
                onPicked: urls =>
                {
                    NSUrl? picked = urls?.FirstOrDefault();
                    if (picked == null)
                        return;

                    if (!string.Equals(Path.GetExtension(picked.LastPathComponent ?? string.Empty), ".vault", StringComparison.OrdinalIgnoreCase))
                    {
                        ShowError("Seleziona un file con estensione .vault.");
                        return;
                    }

                    PromptPasswordAndOpenVault(picked);
                });

            await Task.CompletedTask;
        }

        private async Task PickFilesToAddAsync()
        {
            if (_session == null)
            {
                ShowError("Apri prima un vault.");
                return;
            }

            PresentDocumentPicker(
                allowsMultipleSelection: true,
                onPicked: urls => _ = AddPickedFilesAsync(urls));

            await Task.CompletedTask;
        }

        private void PresentDocumentPicker(bool allowsMultipleSelection, Action<NSUrl[]> onPicked)
        {
            var picker = new UIDocumentPickerViewController(new[] { "public.data" }, UIDocumentPickerMode.Open)
            {
                AllowsMultipleSelection = allowsMultipleSelection
            };

            _pickerDelegate = new PickerDelegate(onPicked);
            picker.Delegate = _pickerDelegate;

            PresentViewController(picker, true, null);
        }

        private void PromptPasswordAndOpenVault(NSUrl vaultUrl)
        {
            UIAlertController prompt = UIAlertController.Create(
                "Apri vault",
                vaultUrl.LastPathComponent ?? "File vault",
                UIAlertControllerStyle.Alert);

            prompt.AddTextField(field =>
            {
                field.Placeholder = "Password";
                field.SecureTextEntry = true;
                field.ReturnKeyType = UIReturnKeyType.Done;
            });

            prompt.AddAction(UIAlertAction.Create("Annulla", UIAlertActionStyle.Cancel, null));
            prompt.AddAction(UIAlertAction.Create("Apri", UIAlertActionStyle.Default, _ =>
            {
                string password = prompt.TextFields?.FirstOrDefault()?.Text ?? string.Empty;
                _ = OpenVaultAsync(vaultUrl, password);
            }));

            PresentViewController(prompt, true, null);
        }

        private async Task OpenVaultAsync(NSUrl vaultUrl, string password)
        {
            if (string.IsNullOrWhiteSpace(password))
            {
                ShowError("Inserisci la password.");
                return;
            }

            await RunBusyAsync("Apertura vault...", async () =>
            {
                byte[] vaultBytes = await Task.Run(() => ReadAllBytes(vaultUrl));
                VaultPortableReader reader = await Task.Run(() => VaultPortableReader.Open(vaultBytes, password, allowUltra: true));

                _session?.Dispose();
                _session = reader;
                _vaultUrl = vaultUrl;
                _currentFolder = string.Empty;

                ReloadFolderItems();
            });
        }

        private async Task AddPickedFilesAsync(NSUrl[] urls)
        {
            if (_session == null || urls == null || urls.Length == 0)
                return;

            await RunBusyAsync("Aggiunta file...", async () =>
            {
                foreach (NSUrl url in urls)
                {
                    if (url == null)
                        continue;

                    string fileName = url.LastPathComponent ?? Path.GetFileName(url.Path ?? string.Empty);
                    if (string.IsNullOrWhiteSpace(fileName))
                        fileName = "file.bin";

                    byte[] fileBytes = await Task.Run(() => ReadAllBytes(url));
                    try
                    {
                        _session.AddFile(fileName, fileBytes, _currentFolder);
                    }
                    finally
                    {
                        if (fileBytes.Length > 0)
                            Array.Clear(fileBytes, 0, fileBytes.Length);
                    }
                }

                await PersistVaultAsync();
                ReloadFolderItems();
            });
        }

        private void ReloadFolderItems()
        {
            _visibleItems.Clear();

            if (_session != null)
            {
                _visibleItems.AddRange(_session.GetItemsInFolder(_currentFolder));
            }

            _tableView?.ReloadData();
            UpdateUiState();
        }

        private void NavigateUp()
        {
            if (_session == null || string.IsNullOrWhiteSpace(_currentFolder))
                return;

            _currentFolder = GetParentPath(_currentFolder);
            ReloadFolderItems();
        }

        private async Task OpenItemActionsAsync(VaultFileItem item)
        {
            if (item.IsFolder)
            {
                _currentFolder = item.FullPath;
                ReloadFolderItems();
                return;
            }

            UIAlertController sheet = UIAlertController.Create(
                item.FileName,
                $"{item.SizeLabel} - {item.AddedAtLabel}",
                UIAlertControllerStyle.ActionSheet);

            sheet.AddAction(UIAlertAction.Create("Apri", UIAlertActionStyle.Default, _ => _ = OpenFileAsync(item)));
            sheet.AddAction(UIAlertAction.Create("Esporta", UIAlertActionStyle.Default, _ => _ = ExportFileAsync(item)));
            sheet.AddAction(UIAlertAction.Create("Rinomina", UIAlertActionStyle.Default, _ => PromptRename(item)));
            sheet.AddAction(UIAlertAction.Create("Sposta", UIAlertActionStyle.Default, _ => PromptMove(item)));
            sheet.AddAction(UIAlertAction.Create("Elimina", UIAlertActionStyle.Destructive, _ => PromptDelete(item)));
            sheet.AddAction(UIAlertAction.Create("Annulla", UIAlertActionStyle.Cancel, null));

            ConfigurePopover(sheet);
            PresentViewController(sheet, true, null);

            await Task.CompletedTask;
        }

        private async Task OpenFileAsync(VaultFileItem item)
        {
            if (_session == null)
                return;

            await RunBusyAsync("Preparazione file...", async () =>
            {
                byte[] fileBytes = await Task.Run(() => _session.ReadFileContent(item.Id));
                string tempPath = await Task.Run(() => WriteTemporaryFile(item.FileName, fileBytes));
                if (fileBytes.Length > 0)
                    Array.Clear(fileBytes, 0, fileBytes.Length);

                PresentDocumentPreview(tempPath);
            });
        }

        private async Task ExportFileAsync(VaultFileItem item)
        {
            if (_session == null)
                return;

            await RunBusyAsync("Preparazione export...", async () =>
            {
                byte[] fileBytes = await Task.Run(() => _session.ReadFileContent(item.Id));
                string tempPath = await Task.Run(() => WriteTemporaryFile(item.FileName, fileBytes));
                if (fileBytes.Length > 0)
                    Array.Clear(fileBytes, 0, fileBytes.Length);

                PresentShareSheet(tempPath);
            });
        }

        private void PromptRename(VaultFileItem item)
        {
            if (_session == null)
                return;

            UIAlertController alert = UIAlertController.Create("Rinomina file", null, UIAlertControllerStyle.Alert);
            alert.AddTextField(field =>
            {
                field.Text = item.FileName;
                field.ClearButtonMode = UITextFieldViewMode.WhileEditing;
            });

            alert.AddAction(UIAlertAction.Create("Annulla", UIAlertActionStyle.Cancel, null));
            alert.AddAction(UIAlertAction.Create("Conferma", UIAlertActionStyle.Default, _ =>
            {
                string newName = alert.TextFields?.FirstOrDefault()?.Text ?? string.Empty;
                _ = RunBusyAsync("Rinomina...", async () =>
                {
                    _session.RenameItem(item.Id, newName);
                    await PersistVaultAsync();
                    ReloadFolderItems();
                });
            }));

            PresentViewController(alert, true, null);
        }

        private void PromptMove(VaultFileItem item)
        {
            if (_session == null)
                return;

            IReadOnlyList<string> folders = _session.GetAllFolderPaths();
            UIAlertController sheet = UIAlertController.Create("Sposta in...", null, UIAlertControllerStyle.ActionSheet);

            foreach (string folder in folders)
            {
                if (item.IsFolder &&
                    (string.Equals(folder, item.FullPath, StringComparison.OrdinalIgnoreCase) ||
                     folder.StartsWith(item.FullPath + "/", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                string label = string.IsNullOrWhiteSpace(folder) ? "/" : $"/{folder}";
                if (string.Equals(folder, item.ParentPath, StringComparison.OrdinalIgnoreCase))
                    label += " (attuale)";

                sheet.AddAction(UIAlertAction.Create(label, UIAlertActionStyle.Default, _ =>
                {
                    _ = RunBusyAsync("Spostamento...", async () =>
                    {
                        _session.MoveItems(new[] { item.Id }, folder);
                        await PersistVaultAsync();
                        ReloadFolderItems();
                    });
                }));
            }

            sheet.AddAction(UIAlertAction.Create("Annulla", UIAlertActionStyle.Cancel, null));
            ConfigurePopover(sheet);
            PresentViewController(sheet, true, null);
        }

        private void PromptDelete(VaultFileItem item)
        {
            if (_session == null)
                return;

            UIAlertController alert = UIAlertController.Create(
                "Elimina elemento",
                $"Vuoi eliminare \"{item.FileName}\"?",
                UIAlertControllerStyle.Alert);

            alert.AddAction(UIAlertAction.Create("Annulla", UIAlertActionStyle.Cancel, null));
            alert.AddAction(UIAlertAction.Create("Elimina", UIAlertActionStyle.Destructive, _ =>
            {
                _ = RunBusyAsync("Eliminazione...", async () =>
                {
                    _session.DeleteItems(new[] { item.Id });
                    await PersistVaultAsync();
                    ReloadFolderItems();
                });
            }));

            PresentViewController(alert, true, null);
        }

        private async Task PersistVaultAsync()
        {
            if (_session == null || _vaultUrl == null || !_session.IsDirty)
                return;

            byte[] updatedVault = await Task.Run(() => _session.ExportVaultBytes());
            await Task.Run(() => WriteAllBytes(_vaultUrl, updatedVault));
        }

        private async Task RunBusyAsync(string message, Func<Task> action)
        {
            SetBusyState(true, message);
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
            finally
            {
                SetBusyState(false, string.Empty);
            }
        }

        private void SetBusyState(bool busy, string message)
        {
            if (_busyOverlay == null || _busyIndicator == null || _busyLabel == null)
                return;

            _busyLabel.Text = string.IsNullOrWhiteSpace(message) ? "Operazione in corso..." : message;
            _busyOverlay.Hidden = !busy;
            View.UserInteractionEnabled = !busy;

            if (busy)
                _busyIndicator.StartAnimating();
            else
                _busyIndicator.StopAnimating();
        }

        private void ShowError(string message)
        {
            UIAlertController alert = UIAlertController.Create(
                "Operazione non riuscita",
                string.IsNullOrWhiteSpace(message) ? "Errore sconosciuto." : message,
                UIAlertControllerStyle.Alert);

            alert.AddAction(UIAlertAction.Create("OK", UIAlertActionStyle.Default, null));
            PresentViewController(alert, true, null);
        }

        private void PresentDocumentPreview(string localPath)
        {
            NSUrl fileUrl = NSUrl.FromFilename(localPath);
            _documentInteractionDelegate ??= new DocumentInteractionDelegate(this);
            _documentInteractionController = UIDocumentInteractionController.FromUrl(fileUrl);
            _documentInteractionController.Delegate = _documentInteractionDelegate;

            bool previewShown = _documentInteractionController.PresentPreview(true);
            if (!previewShown)
            {
                _documentInteractionController.PresentOptionsMenu(
                    new CGRect(View.Bounds.GetMidX(), View.Bounds.GetMidY(), 1, 1),
                    View,
                    true);
            }
        }

        private void PresentShareSheet(string localPath)
        {
            NSUrl fileUrl = NSUrl.FromFilename(localPath);
            var activity = new UIActivityViewController(new NSObject[] { fileUrl }, null);
            activity.CompletionWithItemsHandler = (_, _, _, _) => DeleteTemporaryFile(localPath);

            UIPopoverPresentationController? popover = activity.PopoverPresentationController;
            if (popover != null)
            {
                popover.SourceView = View;
                popover.SourceRect = new CGRect(View.Bounds.GetMidX(), View.Bounds.GetMidY(), 1, 1);
            }

            PresentViewController(activity, true, null);
        }

        private string WriteTemporaryFile(string originalFileName, byte[] content)
        {
            string extension = Path.GetExtension(originalFileName ?? string.Empty);
            string tempName = $"{Guid.NewGuid():N}{extension}";
            string tempPath = Path.Combine(Path.GetTempPath(), tempName);

            File.WriteAllBytes(tempPath, content);
            _temporaryFiles.Add(tempPath);
            return tempPath;
        }

        private void DeleteTemporaryFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            _temporaryFiles.Remove(path);
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best effort cleanup.
            }
        }

        private static byte[] ReadAllBytes(NSUrl fileUrl)
        {
            using var scope = new SecurityScopeAccess(fileUrl);

            string? path = fileUrl.Path;
            if (string.IsNullOrWhiteSpace(path))
                throw new IOException("Percorso file non valido.");

            return File.ReadAllBytes(path);
        }

        private static void WriteAllBytes(NSUrl fileUrl, byte[] content)
        {
            using var scope = new SecurityScopeAccess(fileUrl);

            string? path = fileUrl.Path;
            if (string.IsNullOrWhiteSpace(path))
                throw new IOException("Percorso file non valido.");

            File.WriteAllBytes(path, content);
        }

        private static string GetParentPath(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
                return string.Empty;

            int idx = folderPath.LastIndexOf('/');
            return idx < 0 ? string.Empty : folderPath[..idx];
        }

        private void ConfigurePopover(UIAlertController sheet)
        {
            UIPopoverPresentationController? popover = sheet.PopoverPresentationController;
            if (popover == null)
                return;

            popover.SourceView = View;
            popover.SourceRect = new CGRect(View.Bounds.GetMidX(), View.Bounds.GetMidY(), 1, 1);
        }

        private void HandleRowTapped(int index)
        {
            if (index < 0 || index >= _visibleItems.Count)
                return;

            VaultFileItem item = _visibleItems[index];
            _ = OpenItemActionsAsync(item);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (string path in _temporaryFiles.ToList())
                    DeleteTemporaryFile(path);

                _session?.Dispose();
                _session = null;
            }

            base.Dispose(disposing);
        }

        private sealed class VaultTableSource : UITableViewSource
        {
            private readonly MainViewController _owner;

            public VaultTableSource(MainViewController owner)
            {
                _owner = owner;
            }

            public override nint RowsInSection(UITableView tableView, nint section) =>
                _owner._visibleItems.Count;

            public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
            {
                UITableViewCell cell = tableView.DequeueReusableCell(CellId)
                    ?? new UITableViewCell(UITableViewCellStyle.Subtitle, CellId);

                VaultFileItem item = _owner._visibleItems[indexPath.Row];
                cell.TextLabel.Text = $"{item.IconEmoji}  {item.FileName}";
                cell.DetailTextLabel.Text = item.IsFolder
                    ? $"Cartella - {item.AddedAtLabel}"
                    : $"{item.SizeLabel} - {item.AddedAtLabel}";
                cell.Accessory = item.IsFolder ? UITableViewCellAccessory.DisclosureIndicator : UITableViewCellAccessory.None;
                return cell;
            }

            public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
            {
                tableView.DeselectRow(indexPath, true);
                _owner.HandleRowTapped(indexPath.Row);
            }
        }

        private sealed class PickerDelegate : UIDocumentPickerDelegate
        {
            private readonly Action<NSUrl[]> _onPicked;

            public PickerDelegate(Action<NSUrl[]> onPicked)
            {
                _onPicked = onPicked;
            }

            public override void DidPickDocument(UIDocumentPickerViewController controller, NSUrl url)
            {
                _onPicked(new[] { url });
            }

            public override void DidPickDocumentAtUrls(UIDocumentPickerViewController controller, NSUrl[] urls)
            {
                _onPicked(urls);
            }
        }

        private sealed class DocumentInteractionDelegate : UIDocumentInteractionControllerDelegate
        {
            private readonly UIViewController _owner;

            public DocumentInteractionDelegate(UIViewController owner)
            {
                _owner = owner;
            }

            public override UIViewController ViewControllerForPreview(UIDocumentInteractionController controller)
            {
                return _owner;
            }
        }

        private sealed class SecurityScopeAccess : IDisposable
        {
            private readonly NSUrl _url;
            private readonly bool _isAccessing;

            public SecurityScopeAccess(NSUrl url)
            {
                _url = url;
                _isAccessing = _url.StartAccessingSecurityScopedResource();
            }

            public void Dispose()
            {
                if (_isAccessing)
                {
                    _url.StopAccessingSecurityScopedResource();
                }
            }
        }
    }
}
