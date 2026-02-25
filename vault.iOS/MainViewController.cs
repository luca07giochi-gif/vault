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
            UIView? view = View;
            if (view == null)
                return;

            if (_busyOverlay != null)
            {
                _busyOverlay.Frame = view.Bounds;
            }

            if (_busyIndicator != null && _busyLabel != null)
            {
                const float indicatorSize = 56f;
                const float labelWidth = 280f;
                const float labelHeight = 48f;
                float centerX = (float)view.Bounds.GetMidX();
                float centerY = (float)view.Bounds.GetMidY();

                _busyIndicator.Frame = new CGRect(centerX - indicatorSize / 2f, centerY - 52f, indicatorSize, indicatorSize);
                _busyLabel.Frame = new CGRect(centerX - labelWidth / 2f, centerY + 8f, labelWidth, labelHeight);
            }
        }

        private void BuildBusyOverlay()
        {
            UIView? view = View;
            if (view == null)
                return;

            _busyOverlay = new UIView(view.Bounds)
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
            view.AddSubview(_busyOverlay);
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
                    {
                        ShowError("Nessun file selezionato.");
                        return;
                    }

                    BeginInvokeOnMainThread(() => PromptPasswordAndOpenVault(picked));
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
#pragma warning disable CA1422
            var picker = new UIDocumentPickerViewController(new[] { "public.data" }, UIDocumentPickerMode.Open)
            {
                AllowsMultipleSelection = allowsMultipleSelection
            };
#pragma warning restore CA1422

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
            prompt.AddAction(UIAlertAction.Create("Apri", UIAlertActionStyle.Default, __ =>
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
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                VaultPortableReader reader = await Task.Run(() => OpenVaultReader(vaultUrl, password));

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
                VaultPortableReader session = _session!;
                foreach (NSUrl url in urls)
                {
                    if (url == null)
                        continue;

                    await Task.Run(() => AddFileFromUrl(session, url, _currentFolder));
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

            sheet.AddAction(UIAlertAction.Create("Apri", UIAlertActionStyle.Default, __ =>
            {
                _ = OpenFileAsync(item);
            }));
            sheet.AddAction(UIAlertAction.Create("Esporta", UIAlertActionStyle.Default, __ =>
            {
                _ = ExportFileAsync(item);
            }));
            sheet.AddAction(UIAlertAction.Create("Rinomina", UIAlertActionStyle.Default, __ => PromptRename(item)));
            sheet.AddAction(UIAlertAction.Create("Sposta", UIAlertActionStyle.Default, __ => PromptMove(item)));
            sheet.AddAction(UIAlertAction.Create("Elimina", UIAlertActionStyle.Destructive, __ => PromptDelete(item)));
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
                string tempPath = await Task.Run(() => WriteTemporaryFileFromVault(_session, item));
                PresentDocumentPreview(tempPath);
            });
        }

        private async Task ExportFileAsync(VaultFileItem item)
        {
            if (_session == null)
                return;

            await RunBusyAsync("Preparazione export...", async () =>
            {
                string tempPath = await Task.Run(() => WriteTemporaryFileFromVault(_session, item));
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
            alert.AddAction(UIAlertAction.Create("Conferma", UIAlertActionStyle.Default, __ =>
            {
                string newName = alert.TextFields?.FirstOrDefault()?.Text ?? string.Empty;
                _ = RunBusyAsync("Rinomina...", async () =>
                {
                    if (_session == null)
                        return;

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

                sheet.AddAction(UIAlertAction.Create(label, UIAlertActionStyle.Default, __ =>
                {
                    _ = RunBusyAsync("Spostamento...", async () =>
                    {
                        if (_session == null)
                            return;

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
            alert.AddAction(UIAlertAction.Create("Elimina", UIAlertActionStyle.Destructive, __ =>
            {
                _ = RunBusyAsync("Eliminazione...", async () =>
                {
                    if (_session == null)
                        return;

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

            VaultPortableReader session = _session;
            NSUrl vaultUrl = _vaultUrl;
            await Task.Run(() => PersistVaultToUrl(vaultUrl, session));
        }

        private async Task RunBusyAsync(string message, Func<Task> action)
        {
            SetBusyState(true, message);
            try
            {
                await action();
            }
            catch (OutOfMemoryException)
            {
                ShowError("Memoria iOS insufficiente durante l'operazione richiesta. Chiudi altre app e riprova.");
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
            UIView? view = View;
            if (view == null)
                return;

            _busyLabel.Text = string.IsNullOrWhiteSpace(message) ? "Operazione in corso..." : message;
            _busyOverlay.Hidden = !busy;
            view.UserInteractionEnabled = !busy;

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
            UIView? view = View;
            if (view == null)
                return;

            NSUrl fileUrl = NSUrl.FromFilename(localPath);
            _documentInteractionDelegate ??= new DocumentInteractionDelegate(this);
            _documentInteractionController = UIDocumentInteractionController.FromUrl(fileUrl);
            _documentInteractionController.Delegate = _documentInteractionDelegate;

            bool previewShown = _documentInteractionController.PresentPreview(true);
            if (!previewShown)
            {
                _documentInteractionController.PresentOptionsMenu(
                    new CGRect(view.Bounds.GetMidX(), view.Bounds.GetMidY(), 1, 1),
                    view,
                    true);
            }
        }

        private void PresentShareSheet(string localPath)
        {
            UIView? view = View;
            if (view == null)
                return;

            NSUrl fileUrl = NSUrl.FromFilename(localPath);
            var activity = new UIActivityViewController(new NSObject[] { fileUrl }, null);
            activity.CompletionWithItemsHandler = (_, _, _, _) => DeleteTemporaryFile(localPath);

            UIPopoverPresentationController? popover = activity.PopoverPresentationController;
            if (popover != null)
            {
                popover.SourceView = view;
                popover.SourceRect = new CGRect(view.Bounds.GetMidX(), view.Bounds.GetMidY(), 1, 1);
            }

            PresentViewController(activity, true, null);
        }

        private string WriteTemporaryFileFromVault(VaultPortableReader session, VaultFileItem item)
        {
            string tempPath = CreateTemporaryPath(item.FileName);
            using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                session.CopyFileContentToStream(item.Id, output);
            }

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

        private static void AddFileFromUrl(VaultPortableReader session, NSUrl fileUrl, string targetFolder)
        {
            using var scope = new SecurityScopeAccess(fileUrl);

            string? path = fileUrl.Path;
            if (string.IsNullOrWhiteSpace(path))
                throw new IOException("Percorso file non valido.");

            session.AddFileFromPath(path, targetFolder);
        }

        private static string CreateTemporaryPath(string originalFileName)
        {
            string extension = Path.GetExtension(originalFileName ?? string.Empty);
            string tempName = $"{Guid.NewGuid():N}{extension}";
            return Path.Combine(Path.GetTempPath(), tempName);
        }

        private static VaultPortableReader OpenVaultReader(NSUrl fileUrl, string password)
        {
            using var scope = new SecurityScopeAccess(fileUrl);

            string? path = fileUrl.Path;
            if (string.IsNullOrWhiteSpace(path))
                throw new IOException("Percorso file non valido.");

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return VaultPortableReader.Open(stream, password, allowUltra: true);
        }

        private static void PersistVaultToUrl(NSUrl fileUrl, VaultPortableReader session)
        {
            using var scope = new SecurityScopeAccess(fileUrl);

            string? path = fileUrl.Path;
            if (string.IsNullOrWhiteSpace(path))
                throw new IOException("Percorso file non valido.");

            using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                session.SaveToStream(output);
            }
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
            UIView? view = View;
            if (view == null)
                return;

            popover.SourceView = view;
            popover.SourceRect = new CGRect(view.Bounds.GetMidX(), view.Bounds.GetMidY(), 1, 1);
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
                UIListContentConfiguration content = cell.DefaultContentConfiguration;
                content.Text = $"{item.IconEmoji}  {item.FileName}";
                content.SecondaryText = item.IsFolder
                    ? $"Cartella - {item.AddedAtLabel}"
                    : $"{item.SizeLabel} - {item.AddedAtLabel}";
                cell.ContentConfiguration = content;
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
                NotifyPicked(controller, new[] { url });
            }

            public override void DidPickDocument(UIDocumentPickerViewController controller, NSUrl[] urls)
            {
                NotifyPicked(controller, urls);
            }

            public override void WasCancelled(UIDocumentPickerViewController controller)
            {
                controller.DismissViewController(true, null);
            }

            private void NotifyPicked(UIDocumentPickerViewController controller, NSUrl[]? urls)
            {
                NSUrl[] safeUrls = urls ?? Array.Empty<NSUrl>();
                controller.DismissViewController(true, () =>
                {
                    UIApplication.SharedApplication.BeginInvokeOnMainThread(() => _onPicked(safeUrls));
                });
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
