using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CoreGraphics;
using Foundation;
using Photos;
using PhotosUI;
using UIKit;
using vault.Core.Domain;

namespace vault.iOS
{
    public sealed class MainViewController : UIViewController
    {
        private const string CellId = "VaultItemCell";
        private const double LongPressSeconds = 0.45d;

        private enum BrowserViewMode
        {
            List,
            Preview
        }

        private readonly List<VaultFileItem> _visibleItems = new();
        private readonly HashSet<string> _temporaryFiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<Guid> _selectedItemIds = new();

        private VaultPortableReader? _session;
        private NSUrl? _vaultUrl;
        private string _currentFolder = string.Empty;
        private bool _isSelectionMode;
        private BrowserViewMode _viewMode = BrowserViewMode.List;

        private UITableView? _tableView;
        private UICollectionView? _collectionView;
        private UILabel? _emptyLabel;
        private UIView? _busyOverlay;
        private UIActivityIndicatorView? _busyIndicator;
        private UILabel? _busyLabel;

        private UIBarButtonItem? _openVaultButton;
        private UIBarButtonItem? _addFileButton;
        private UIBarButtonItem? _viewModeButton;
        private UIBarButtonItem? _upButton;
        private UIBarButtonItem? _selectionDoneButton;
        private UIBarButtonItem? _batchMoveButton;
        private UIBarButtonItem? _batchDeleteButton;
        private UIButton? _pathTitleButton;
        private UILongPressGestureRecognizer? _tableLongPressRecognizer;
        private UILongPressGestureRecognizer? _collectionLongPressRecognizer;

        private UIDocumentInteractionController? _documentInteractionController;
        private DocumentInteractionDelegate? _documentInteractionDelegate;
        private PickerDelegate? _pickerDelegate;
        private GalleryMultiPickerDelegate? _galleryMultiPickerDelegate;

        public override void ViewDidLoad()
        {
            base.ViewDidLoad();

            View!.BackgroundColor = UIColor.White;

            _tableView = new UITableView(View.Bounds, UITableViewStyle.InsetGrouped)
            {
                AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight,
                Source = new VaultTableSource(this)
            };
            _tableLongPressRecognizer = new UILongPressGestureRecognizer(HandleTableLongPress)
            {
                MinimumPressDuration = LongPressSeconds
            };
            _tableView.AddGestureRecognizer(_tableLongPressRecognizer);
            View.AddSubview(_tableView);

            var previewLayout = new UICollectionViewFlowLayout
            {
                MinimumInteritemSpacing = 12f,
                MinimumLineSpacing = 12f,
                SectionInset = new UIEdgeInsets(12f, 12f, 12f, 12f),
                ItemSize = new CGSize(160, 170)
            };

            _collectionView = new UICollectionView(View.Bounds, previewLayout)
            {
                AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight,
                BackgroundColor = UIColor.Clear,
                Hidden = true,
                Source = new PreviewCollectionSource(this),
                AllowsSelection = true,
                AllowsMultipleSelection = true
            };
            _collectionView.RegisterClassForCell(typeof(PreviewCell), PreviewCell.CellReuseId);
            _collectionLongPressRecognizer = new UILongPressGestureRecognizer(HandleCollectionLongPress)
            {
                MinimumPressDuration = LongPressSeconds
            };
            _collectionView.AddGestureRecognizer(_collectionLongPressRecognizer);
            View.AddSubview(_collectionView);

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

            UpdatePreviewLayout();
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
                "Aggiungi",
                UIBarButtonItemStyle.Plain,
                (_, _) => _ = PickAddSourceAsync());

            _viewModeButton = new UIBarButtonItem(
                "Anteprime",
                UIBarButtonItemStyle.Plain,
                (_, _) => ToggleViewMode());

            _upButton = new UIBarButtonItem(
                "Su",
                UIBarButtonItemStyle.Plain,
                (_, _) => NavigateUp());

            _selectionDoneButton = new UIBarButtonItem(
                "Fine",
                UIBarButtonItemStyle.Plain,
                (_, _) => ExitSelectionMode(clearSelection: true));

            _batchMoveButton = new UIBarButtonItem(
                "Sposta",
                UIBarButtonItemStyle.Plain,
                (_, _) => PromptMoveSelectedItems());

            _batchDeleteButton = new UIBarButtonItem(
                "Elimina",
                UIBarButtonItemStyle.Plain,
                (_, _) => PromptDeleteSelectedItems());

            _pathTitleButton = new UIButton(UIButtonType.System);
            _pathTitleButton.SetTitle("Cassaforte iOS", UIControlState.Normal);
            _pathTitleButton.TitleLabel!.Font = UIFont.SystemFontOfSize(17, UIFontWeight.Semibold);
            _pathTitleButton.TouchUpInside += (_, _) => OpenFolderTreePage();
            NavigationItem.TitleView = _pathTitleButton;

            NavigationItem.LeftBarButtonItem = _openVaultButton;
            RefreshNavigationItems();
        }

        private void UpdateUiState()
        {
            bool hasVault = _session != null;
            bool canGoUp = hasVault && !string.IsNullOrWhiteSpace(_currentFolder);
            string titlePath = hasVault
                ? (string.IsNullOrWhiteSpace(_currentFolder) ? "/" : $"/{_currentFolder}")
                : "Cassaforte iOS";

            Title = titlePath;

            if (_pathTitleButton != null)
            {
                _pathTitleButton.SetTitle(titlePath, UIControlState.Normal);
                _pathTitleButton.Enabled = hasVault && !_isSelectionMode;
                _pathTitleButton.SizeToFit();
            }

            if (!hasVault)
            {
                if (_emptyLabel != null)
                {
                    _emptyLabel.Hidden = false;
                    _emptyLabel.Text = "Tocca \"Apri vault\" per selezionare un file .vault.";
                }
            }
            else if (_emptyLabel != null)
            {
                _emptyLabel.Hidden = _visibleItems.Count > 0;
                _emptyLabel.Text = "Cartella vuota.";
            }

            if (_addFileButton != null)
                _addFileButton.Enabled = hasVault && !_isSelectionMode;

            if (_upButton != null)
                _upButton.Enabled = canGoUp && !_isSelectionMode;

            if (_viewModeButton != null)
            {
                _viewModeButton.Enabled = hasVault && !_isSelectionMode;
                _viewModeButton.Title = _viewMode == BrowserViewMode.List ? "Anteprime" : "Elenco";
            }

            ApplyViewModeVisibility();
            RefreshNavigationItems();
        }

        private void RefreshNavigationItems()
        {
            if (_isSelectionMode)
            {
                if (_openVaultButton != null)
                    _openVaultButton.Enabled = false;

                if (_batchMoveButton != null)
                    _batchMoveButton.Enabled = _selectedItemIds.Count > 0;

                if (_batchDeleteButton != null)
                    _batchDeleteButton.Enabled = _selectedItemIds.Count > 0;

                NavigationItem.RightBarButtonItems = new[]
                {
                    _selectionDoneButton,
                    _batchMoveButton,
                    _batchDeleteButton
                }.Where(b => b != null).Cast<UIBarButtonItem>().ToArray();
                return;
            }

            if (_openVaultButton != null)
                _openVaultButton.Enabled = true;

            NavigationItem.RightBarButtonItems = new[]
            {
                _addFileButton,
                _viewModeButton,
                _upButton
            }.Where(b => b != null).Cast<UIBarButtonItem>().ToArray();
        }

        private void ApplyViewModeVisibility()
        {
            bool showPreview = _viewMode == BrowserViewMode.Preview;

            if (_tableView != null)
                _tableView.Hidden = showPreview;

            if (_collectionView != null)
                _collectionView.Hidden = !showPreview;
        }

        private void UpdatePreviewLayout()
        {
            if (_collectionView?.CollectionViewLayout is not UICollectionViewFlowLayout flow)
                return;

            UIView? view = View;
            if (view == null)
                return;

            nfloat width = view.Bounds.Width;
            int columns = width >= 720 ? 4 : width >= 520 ? 3 : 2;
            nfloat inset = 12f;
            nfloat totalSpacing = inset * (columns + 1);
            nfloat cellWidth = (width - totalSpacing) / columns;
            if (cellWidth < 120f)
                cellWidth = 120f;

            flow.ItemSize = new CGSize(cellWidth, cellWidth + 52f);
            flow.InvalidateLayout();
        }

        private void ToggleViewMode()
        {
            _viewMode = _viewMode == BrowserViewMode.List
                ? BrowserViewMode.Preview
                : BrowserViewMode.List;

            ApplyViewModeVisibility();
            UpdateUiState();
            ReloadVisibleData();
        }

        private void OpenFolderTreePage()
        {
            if (_session == null || NavigationController == null)
                return;

            var tree = new FolderTreeViewController(_session, _currentFolder, OnFolderTreeClosed);
            NavigationController.PushViewController(tree, true);
        }

        private void OnFolderTreeClosed(string selectedFolderPath, bool hasChanges)
        {
            _currentFolder = NormalizeFolderPath(selectedFolderPath);
            EnsureCurrentFolderStillExists();

            if (!hasChanges)
            {
                ReloadFolderItems();
                return;
            }

            _ = RunBusyAsync("Aggiornamento cartelle...", async () =>
            {
                await PersistVaultAsync();
                ReloadFolderItems();
            });
        }

        private void HandleTableLongPress(UILongPressGestureRecognizer gesture)
        {
            if (gesture.State != UIGestureRecognizerState.Began || _tableView == null)
                return;

            CGPoint point = gesture.LocationInView(_tableView);
            NSIndexPath? indexPath = _tableView.IndexPathForRowAtPoint(point);
            if (indexPath == null || indexPath.Row < 0 || indexPath.Row >= _visibleItems.Count)
                return;

            StartSelectionModeWithItem(_visibleItems[indexPath.Row].Id);
        }

        private void HandleCollectionLongPress(UILongPressGestureRecognizer gesture)
        {
            if (gesture.State != UIGestureRecognizerState.Began || _collectionView == null)
                return;

            CGPoint point = gesture.LocationInView(_collectionView);
            NSIndexPath? indexPath = _collectionView.IndexPathForItemAtPoint(point);
            if (indexPath == null || indexPath.Row < 0 || indexPath.Row >= _visibleItems.Count)
                return;

            StartSelectionModeWithItem(_visibleItems[indexPath.Row].Id);
        }

        private void StartSelectionModeWithItem(Guid itemId)
        {
            _isSelectionMode = true;
            _selectedItemIds.Add(itemId);
            UpdateUiState();
            ReloadVisibleData();
        }

        private void ToggleSelectedItem(Guid itemId)
        {
            if (!_selectedItemIds.Add(itemId))
                _selectedItemIds.Remove(itemId);

            UpdateUiState();
            ReloadVisibleData();
        }

        private void ExitSelectionMode(bool clearSelection)
        {
            _isSelectionMode = false;
            if (clearSelection)
                _selectedItemIds.Clear();

            UpdateUiState();
            ReloadVisibleData();
        }

        private void PromptMoveSelectedItems()
        {
            if (_session == null || _selectedItemIds.Count == 0)
                return;

            Guid[] selectedIds = _selectedItemIds.ToArray();
            IReadOnlyList<string> folders = _session.GetAllFolderPaths();
            UIAlertController sheet = UIAlertController.Create(
                "Sposta selezione",
                $"{selectedIds.Length} elementi selezionati",
                UIAlertControllerStyle.ActionSheet);

            foreach (string folder in folders)
            {
                string label = string.IsNullOrWhiteSpace(folder) ? "/" : $"/{folder}";
                sheet.AddAction(UIAlertAction.Create(label, UIAlertActionStyle.Default, __ =>
                {
                    _ = RunBusyAsync("Spostamento elementi...", async () =>
                    {
                        if (_session == null)
                            return;

                        _session.MoveItems(selectedIds, folder);
                        await PersistVaultAsync();
                        EnsureCurrentFolderStillExists();
                        ExitSelectionMode(clearSelection: true);
                        ReloadFolderItems();
                    });
                }));
            }

            sheet.AddAction(UIAlertAction.Create("Annulla", UIAlertActionStyle.Cancel, null));
            ConfigurePopover(sheet);
            PresentViewController(sheet, true, null);
        }

        private void PromptDeleteSelectedItems()
        {
            if (_session == null || _selectedItemIds.Count == 0)
                return;

            Guid[] selectedIds = _selectedItemIds.ToArray();
            UIAlertController alert = UIAlertController.Create(
                "Elimina selezione",
                $"Vuoi eliminare {selectedIds.Length} elementi selezionati?",
                UIAlertControllerStyle.Alert);

            alert.AddAction(UIAlertAction.Create("Annulla", UIAlertActionStyle.Cancel, null));
            alert.AddAction(UIAlertAction.Create("Elimina", UIAlertActionStyle.Destructive, __ =>
            {
                _ = RunBusyAsync("Eliminazione elementi...", async () =>
                {
                    if (_session == null)
                        return;

                    _session.DeleteItems(selectedIds);
                    await PersistVaultAsync();
                    EnsureCurrentFolderStillExists();
                    ExitSelectionMode(clearSelection: true);
                    ReloadFolderItems();
                });
            }));

            PresentViewController(alert, true, null);
        }

        private void EnsureCurrentFolderStillExists()
        {
            if (_session == null)
            {
                _currentFolder = string.Empty;
                return;
            }

            _currentFolder = NormalizeFolderPath(_currentFolder);
            if (string.IsNullOrWhiteSpace(_currentFolder))
            {
                _currentFolder = string.Empty;
                return;
            }

            HashSet<string> folders = _session.GetAllFolderPaths()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (folders.Contains(_currentFolder))
                return;

            string probe = _currentFolder;
            while (!string.IsNullOrWhiteSpace(probe))
            {
                probe = GetParentPath(probe);
                if (folders.Contains(probe))
                {
                    _currentFolder = probe;
                    return;
                }
            }

            _currentFolder = string.Empty;
        }

        private static string NormalizeFolderPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            return path.Trim().Trim('/');
        }

        private void ReloadVisibleData()
        {
            _tableView?.ReloadData();
            _collectionView?.ReloadData();
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

        private async Task PickAddSourceAsync()
        {
            if (_session == null)
            {
                ShowError("Apri prima un vault.");
                return;
            }

            UIAlertController sheet = UIAlertController.Create(
                "Aggiungi contenuto",
                "Seleziona da dove importare",
                UIAlertControllerStyle.ActionSheet);

            sheet.AddAction(UIAlertAction.Create("File", UIAlertActionStyle.Default, __ =>
            {
                _ = PickFilesToAddAsync();
            }));

            sheet.AddAction(UIAlertAction.Create("Galleria foto/video", UIAlertActionStyle.Default, __ =>
            {
                _ = PickGalleryMediaToAddAsync();
            }));

            sheet.AddAction(UIAlertAction.Create("Annulla", UIAlertActionStyle.Cancel, null));
            ConfigurePopover(sheet);
            PresentViewController(sheet, true, null);

            await Task.CompletedTask;
        }

        private async Task PickGalleryMediaToAddAsync()
        {
            if (_session == null)
            {
                ShowError("Apri prima un vault.");
                return;
            }

            var configuration = new PHPickerConfiguration(PHPhotoLibrary.SharedPhotoLibrary)
            {
                SelectionLimit = 0
            };

            var picker = new PHPickerViewController(configuration);
            _galleryMultiPickerDelegate = new GalleryMultiPickerDelegate(results =>
            {
                _ = HandlePickedGalleryResultsAsync(results);
            });
            picker.Delegate = _galleryMultiPickerDelegate;

            PresentViewController(picker, true, null);
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
                _isSelectionMode = false;
                _selectedItemIds.Clear();

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

        private async Task HandlePickedGalleryResultsAsync(PHPickerResult[] results)
        {
            if (_session == null || results == null || results.Length == 0)
                return;

            await RunBusyAsync("Aggiunta media...", async () =>
            {
                VaultPortableReader session = _session!;

                foreach (PHPickerResult result in results)
                {
                    string tempPath = await ExtractPickerResultToTempPathAsync(result);
                    try
                    {
                        await Task.Run(() => session.AddFileFromPath(tempPath, _currentFolder));
                    }
                    finally
                    {
                        TryDeletePath(tempPath);
                    }
                }

                await PersistVaultAsync();
                ReloadFolderItems();
            });
        }

        private void ReloadFolderItems()
        {
            EnsureCurrentFolderStillExists();
            _visibleItems.Clear();

            if (_session != null)
            {
                _visibleItems.AddRange(_session.GetItemsInFolder(_currentFolder));
            }

            _selectedItemIds.RemoveWhere(id => _visibleItems.All(item => item.Id != id));
            if (_isSelectionMode && _selectedItemIds.Count == 0)
                _isSelectionMode = false;

            ReloadVisibleData();
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
                        EnsureCurrentFolderStillExists();
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
                    EnsureCurrentFolderStillExists();
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

        private static async Task<string> ExtractPickerResultToTempPathAsync(PHPickerResult result)
        {
            if (result == null)
                throw new ArgumentNullException(nameof(result));

            NSItemProvider provider = result.ItemProvider
                ?? throw new InvalidOperationException("Elemento galleria non valido.");

            string typeIdentifier = provider.RegisteredTypeIdentifiers?
                .FirstOrDefault(id =>
                    id.Contains("image", StringComparison.OrdinalIgnoreCase) ||
                    id.Contains("movie", StringComparison.OrdinalIgnoreCase) ||
                    id.Contains("video", StringComparison.OrdinalIgnoreCase))
                ?? provider.RegisteredTypeIdentifiers?.FirstOrDefault()
                ?? string.Empty;

            if (string.IsNullOrWhiteSpace(typeIdentifier))
                throw new InvalidOperationException("Tipo media non supportato.");

            return await LoadFileRepresentationToTempPathAsync(provider, typeIdentifier);
        }

        private static Task<string> LoadFileRepresentationToTempPathAsync(NSItemProvider provider, string typeIdentifier)
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            provider.LoadFileRepresentation(typeIdentifier, (url, error) =>
            {
                if (error != null)
                {
                    tcs.TrySetException(new NSErrorException(error));
                    return;
                }

                if (url == null)
                {
                    tcs.TrySetException(new InvalidOperationException("Impossibile caricare il media selezionato."));
                    return;
                }

                try
                {
                    string? sourcePath = url.Path;
                    if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                    {
                        tcs.TrySetException(new InvalidOperationException("Il media selezionato non e disponibile."));
                        return;
                    }

                    string extension = Path.GetExtension(sourcePath);
                    if (string.IsNullOrWhiteSpace(extension))
                        extension = GuessExtensionForMediaType(typeIdentifier);

                    string tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{extension}");
                    File.Copy(sourcePath, tempPath, overwrite: true);
                    tcs.TrySetResult(tempPath);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            return tcs.Task;
        }

        private static string GuessExtensionForMediaType(string mediaType)
        {
            if (mediaType.Contains("png", StringComparison.OrdinalIgnoreCase))
                return ".png";

            if (mediaType.Contains("movie", StringComparison.OrdinalIgnoreCase) ||
                mediaType.Contains("video", StringComparison.OrdinalIgnoreCase))
                return ".mov";

            return ".jpg";
        }

        private static void TryDeletePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

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
            if (_isSelectionMode)
            {
                ToggleSelectedItem(item.Id);
                return;
            }

            _ = OpenItemActionsAsync(item);
        }

        private void HandleCollectionItemTapped(int index)
        {
            if (index < 0 || index >= _visibleItems.Count)
                return;

            VaultFileItem item = _visibleItems[index];
            if (_isSelectionMode)
            {
                ToggleSelectedItem(item.Id);
                return;
            }

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
                cell.Accessory = _owner._isSelectionMode
                    ? (_owner._selectedItemIds.Contains(item.Id) ? UITableViewCellAccessory.Checkmark : UITableViewCellAccessory.None)
                    : (item.IsFolder ? UITableViewCellAccessory.DisclosureIndicator : UITableViewCellAccessory.None);
                return cell;
            }

            public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
            {
                tableView.DeselectRow(indexPath, true);
                _owner.HandleRowTapped(indexPath.Row);
            }
        }

        private sealed class PreviewCollectionSource : UICollectionViewSource
        {
            private readonly MainViewController _owner;

            public PreviewCollectionSource(MainViewController owner)
            {
                _owner = owner;
            }

            public override nint GetItemsCount(UICollectionView collectionView, nint section) =>
                _owner._visibleItems.Count;

            public override UICollectionViewCell GetCell(UICollectionView collectionView, NSIndexPath indexPath)
            {
                var cell = (PreviewCell)collectionView.DequeueReusableCell(PreviewCell.CellReuseId, indexPath);
                VaultFileItem item = _owner._visibleItems[indexPath.Row];
                bool isSelected = _owner._selectedItemIds.Contains(item.Id);
                cell.Configure(item, isSelected, _owner._isSelectionMode);
                return cell;
            }

            public override void ItemSelected(UICollectionView collectionView, NSIndexPath indexPath)
            {
                collectionView.DeselectItem(indexPath, false);
                _owner.HandleCollectionItemTapped(indexPath.Row);
            }
        }

        private sealed class PreviewCell : UICollectionViewCell
        {
            public static readonly NSString CellReuseId = new("VaultPreviewCell");

            private readonly UIView _card = new();
            private readonly UIImageView _iconView = new();
            private readonly UILabel _titleLabel = new();
            private readonly UILabel _subtitleLabel = new();
            private readonly UILabel _selectionBadge = new();
            private bool _initialized;

            public PreviewCell(IntPtr handle)
                : base(handle)
            {
                Initialize();
            }

            public PreviewCell(CGRect frame)
                : base(frame)
            {
                Initialize();
            }

            private void Initialize()
            {
                if (_initialized)
                    return;
                _initialized = true;

                BackgroundColor = UIColor.Clear;
                ContentView.BackgroundColor = UIColor.Clear;

                _card.BackgroundColor = UIColor.FromRGB(245, 245, 247);
                _card.Layer.CornerRadius = 12f;
                _card.Layer.BorderWidth = 1f;
                _card.Layer.BorderColor = UIColor.FromRGB(220, 220, 225).CGColor;
                _card.ClipsToBounds = true;

                _iconView.ContentMode = UIViewContentMode.ScaleAspectFit;
                _iconView.TintColor = UIColor.FromRGB(10, 132, 255);

                _titleLabel.Font = UIFont.SystemFontOfSize(14, UIFontWeight.Semibold);
                _titleLabel.TextColor = UIColor.Black;
                _titleLabel.Lines = 2;

                _subtitleLabel.Font = UIFont.SystemFontOfSize(12);
                _subtitleLabel.TextColor = UIColor.DarkGray;
                _subtitleLabel.Lines = 1;

                _selectionBadge.Hidden = true;
                _selectionBadge.Font = UIFont.SystemFontOfSize(13, UIFontWeight.Bold);
                _selectionBadge.TextAlignment = UITextAlignment.Center;
                _selectionBadge.TextColor = UIColor.White;
                _selectionBadge.BackgroundColor = UIColor.FromRGB(10, 132, 255);
                _selectionBadge.Layer.CornerRadius = 10f;
                _selectionBadge.ClipsToBounds = true;

                _card.AddSubview(_iconView);
                _card.AddSubview(_titleLabel);
                _card.AddSubview(_subtitleLabel);
                _card.AddSubview(_selectionBadge);
                ContentView.AddSubview(_card);
            }

            public override void LayoutSubviews()
            {
                base.LayoutSubviews();

                nfloat x = 2f;
                nfloat y = 2f;
                nfloat width = ContentView.Bounds.Width - 4f;
                nfloat height = ContentView.Bounds.Height - 4f;
                _card.Frame = new CGRect(x, y, width, height);

                nfloat iconHeight = width - 32f;
                if (iconHeight < 56f)
                    iconHeight = 56f;

                _iconView.Frame = new CGRect(12f, 12f, width - 24f, iconHeight);
                _titleLabel.Frame = new CGRect(10f, _iconView.Frame.Bottom + 6f, width - 20f, 34f);
                _subtitleLabel.Frame = new CGRect(10f, _titleLabel.Frame.Bottom, width - 20f, 16f);
                _selectionBadge.Frame = new CGRect(width - 28f, 8f, 20f, 20f);
            }

            public void Configure(VaultFileItem item, bool isSelected, bool isSelectionMode)
            {
                _iconView.Image = UIImage.GetSystemImage(GetSymbolName(item));
                _iconView.TintColor = item.IsFolder ? UIColor.FromRGB(10, 132, 255) : UIColor.Gray;

                _titleLabel.Text = item.FileName;
                _subtitleLabel.Text = item.IsFolder ? "Cartella" : item.SizeLabel;

                _selectionBadge.Hidden = !isSelectionMode;
                _selectionBadge.Text = isSelected ? "OK" : string.Empty;
                _selectionBadge.BackgroundColor = isSelected ? UIColor.FromRGB(10, 132, 255) : UIColor.Gray;
                _card.Layer.BorderColor = isSelected
                    ? UIColor.FromRGB(10, 132, 255).CGColor
                    : UIColor.FromRGB(220, 220, 225).CGColor;
                _card.Layer.BorderWidth = isSelected ? 2f : 1f;
            }

            private static string GetSymbolName(VaultFileItem item)
            {
                if (item.IsFolder)
                    return "folder.fill";

                string ext = (Path.GetExtension(item.FileName) ?? string.Empty).ToLowerInvariant();
                if (ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".heic" or ".tif" or ".tiff")
                    return "photo.fill";
                if (ext is ".mp4" or ".mov" or ".avi" or ".mkv" or ".webm")
                    return "video.fill";
                if (ext is ".mp3" or ".wav" or ".flac" or ".m4a")
                    return "music.note";
                if (ext is ".zip" or ".rar" or ".7z" or ".tar" or ".gz")
                    return "archivebox.fill";
                if (ext is ".pdf")
                    return "doc.richtext.fill";

                return "doc.fill";
            }
        }

        private sealed class FolderTreeViewController : UIViewController
        {
            private const string FolderCellId = "FolderTreeCell";

            private readonly VaultPortableReader _session;
            private readonly Action<string, bool> _onClose;
            private readonly List<string> _folderPaths = new();
            private string _selectedPath;
            private bool _hasChanges;
            private UITableView? _tableView;

            public FolderTreeViewController(
                VaultPortableReader session,
                string currentPath,
                Action<string, bool> onClose)
            {
                _session = session ?? throw new ArgumentNullException(nameof(session));
                _selectedPath = NormalizeFolderPath(currentPath);
                _onClose = onClose ?? throw new ArgumentNullException(nameof(onClose));
            }

            public override void ViewDidLoad()
            {
                base.ViewDidLoad();

                View!.BackgroundColor = UIColor.White;
                Title = "Cartelle vault";
                NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
                NavigationItem.RightBarButtonItem = new UIBarButtonItem(
                    "Nuova",
                    UIBarButtonItemStyle.Plain,
                    (_, _) => PromptCreateFolder(_selectedPath));

                _tableView = new UITableView(View.Bounds, UITableViewStyle.InsetGrouped)
                {
                    AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight,
                    Source = new FolderTreeSource(this)
                };
                View.AddSubview(_tableView);

                ReloadFolders();
            }

            public override void ViewDidDisappear(bool animated)
            {
                base.ViewDidDisappear(animated);

                if (IsMovingFromParentViewController)
                    _onClose(_selectedPath, _hasChanges);
            }

            private void ReloadFolders()
            {
                _folderPaths.Clear();
                _folderPaths.AddRange(_session.GetAllFolderPaths());
                EnsureSelectedPathExists();
                _tableView?.ReloadData();
            }

            private void EnsureSelectedPathExists()
            {
                if (_folderPaths.Any(p => string.Equals(p, _selectedPath, StringComparison.OrdinalIgnoreCase)))
                    return;

                string probe = _selectedPath;
                while (!string.IsNullOrWhiteSpace(probe))
                {
                    probe = GetParentPath(probe);
                    if (_folderPaths.Any(p => string.Equals(p, probe, StringComparison.OrdinalIgnoreCase)))
                    {
                        _selectedPath = probe;
                        return;
                    }
                }

                _selectedPath = string.Empty;
            }

            private void HandleFolderTapped(int index)
            {
                if (index < 0 || index >= _folderPaths.Count)
                    return;

                string folderPath = _folderPaths[index];
                string label = string.IsNullOrWhiteSpace(folderPath) ? "/" : $"/{folderPath}";
                UIAlertController sheet = UIAlertController.Create(label, null, UIAlertControllerStyle.ActionSheet);

                sheet.AddAction(UIAlertAction.Create("Apri qui", UIAlertActionStyle.Default, _ =>
                {
                    _selectedPath = folderPath;
                    NavigationController?.PopViewController(true);
                }));

                sheet.AddAction(UIAlertAction.Create("Nuova cartella qui", UIAlertActionStyle.Default, _ =>
                {
                    PromptCreateFolder(folderPath);
                }));

                if (!string.IsNullOrWhiteSpace(folderPath))
                {
                    sheet.AddAction(UIAlertAction.Create("Sposta cartella", UIAlertActionStyle.Default, _ =>
                    {
                        PromptMoveFolder(folderPath);
                    }));

                    sheet.AddAction(UIAlertAction.Create("Elimina cartella", UIAlertActionStyle.Destructive, _ =>
                    {
                        PromptDeleteFolder(folderPath);
                    }));
                }

                sheet.AddAction(UIAlertAction.Create("Annulla", UIAlertActionStyle.Cancel, null));
                ConfigurePopover(sheet);
                PresentViewController(sheet, true, null);
            }

            private void PromptCreateFolder(string parentPath)
            {
                UIAlertController alert = UIAlertController.Create("Nuova cartella", null, UIAlertControllerStyle.Alert);
                alert.AddTextField(field =>
                {
                    field.Placeholder = "Nome cartella";
                    field.ClearButtonMode = UITextFieldViewMode.WhileEditing;
                });

                alert.AddAction(UIAlertAction.Create("Annulla", UIAlertActionStyle.Cancel, null));
                alert.AddAction(UIAlertAction.Create("Crea", UIAlertActionStyle.Default, _ =>
                {
                    string name = alert.TextFields?.FirstOrDefault()?.Text ?? string.Empty;
                    try
                    {
                        VaultFileItem folder = _session.CreateFolder(name, parentPath);
                        _selectedPath = folder.ParentPath;
                        _hasChanges = true;
                        ReloadFolders();
                    }
                    catch (Exception ex)
                    {
                        ShowError(ex.Message);
                    }
                }));

                PresentViewController(alert, true, null);
            }

            private void PromptMoveFolder(string folderPath)
            {
                VaultFileItem? folder = FindFolder(folderPath);
                if (folder == null)
                    return;

                IReadOnlyList<string> destinations = _session.GetAllFolderPaths()
                    .Where(path =>
                        !string.Equals(path, folderPath, StringComparison.OrdinalIgnoreCase) &&
                        !path.StartsWith(folderPath + "/", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                UIAlertController sheet = UIAlertController.Create("Sposta cartella in...", null, UIAlertControllerStyle.ActionSheet);
                foreach (string destination in destinations)
                {
                    string label = string.IsNullOrWhiteSpace(destination) ? "/" : $"/{destination}";
                    sheet.AddAction(UIAlertAction.Create(label, UIAlertActionStyle.Default, _ =>
                    {
                        try
                        {
                            string oldPath = folderPath;
                            _session.MoveItems(new[] { folder.Id }, destination);
                            _hasChanges = true;

                            VaultFileItem? moved = _session.Files.FirstOrDefault(f => f.Id == folder.Id);
                            if (moved != null &&
                                (_selectedPath.Equals(oldPath, StringComparison.OrdinalIgnoreCase) ||
                                 _selectedPath.StartsWith(oldPath + "/", StringComparison.OrdinalIgnoreCase)))
                            {
                                string suffix = _selectedPath.Length > oldPath.Length
                                    ? _selectedPath[oldPath.Length..]
                                    : string.Empty;
                                _selectedPath = NormalizeFolderPath(moved.FullPath + suffix);
                            }

                            ReloadFolders();
                        }
                        catch (Exception ex)
                        {
                            ShowError(ex.Message);
                        }
                    }));
                }

                sheet.AddAction(UIAlertAction.Create("Annulla", UIAlertActionStyle.Cancel, null));
                ConfigurePopover(sheet);
                PresentViewController(sheet, true, null);
            }

            private void PromptDeleteFolder(string folderPath)
            {
                VaultFileItem? folder = FindFolder(folderPath);
                if (folder == null)
                    return;

                UIAlertController alert = UIAlertController.Create(
                    "Elimina cartella",
                    $"Vuoi eliminare \"{folder.FileName}\" e il suo contenuto?",
                    UIAlertControllerStyle.Alert);

                alert.AddAction(UIAlertAction.Create("Annulla", UIAlertActionStyle.Cancel, null));
                alert.AddAction(UIAlertAction.Create("Elimina", UIAlertActionStyle.Destructive, _ =>
                {
                    try
                    {
                        _session.DeleteItems(new[] { folder.Id });
                        _hasChanges = true;

                        if (_selectedPath.Equals(folderPath, StringComparison.OrdinalIgnoreCase) ||
                            _selectedPath.StartsWith(folderPath + "/", StringComparison.OrdinalIgnoreCase))
                        {
                            _selectedPath = GetParentPath(folderPath);
                        }

                        ReloadFolders();
                    }
                    catch (Exception ex)
                    {
                        ShowError(ex.Message);
                    }
                }));

                PresentViewController(alert, true, null);
            }

            private VaultFileItem? FindFolder(string folderPath)
            {
                return _session.Files.FirstOrDefault(f =>
                    f.IsFolder &&
                    string.Equals(f.FullPath, folderPath, StringComparison.OrdinalIgnoreCase));
            }

            private void ConfigurePopover(UIAlertController sheet)
            {
                UIPopoverPresentationController? popover = sheet.PopoverPresentationController;
                if (popover == null || View == null)
                    return;

                popover.SourceView = View;
                popover.SourceRect = new CGRect(View.Bounds.GetMidX(), View.Bounds.GetMidY(), 1, 1);
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

            private static string NormalizeFolderPath(string? path)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return string.Empty;

                return path.Trim().Trim('/');
            }

            private static string GetParentPath(string folderPath)
            {
                if (string.IsNullOrWhiteSpace(folderPath))
                    return string.Empty;

                int idx = folderPath.LastIndexOf('/');
                return idx < 0 ? string.Empty : folderPath[..idx];
            }

            private sealed class FolderTreeSource : UITableViewSource
            {
                private readonly FolderTreeViewController _owner;

                public FolderTreeSource(FolderTreeViewController owner)
                {
                    _owner = owner;
                }

                public override nint RowsInSection(UITableView tableView, nint section) =>
                    _owner._folderPaths.Count;

                public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
                {
                    UITableViewCell cell = tableView.DequeueReusableCell(FolderCellId)
                        ?? new UITableViewCell(UITableViewCellStyle.Subtitle, FolderCellId);

                    string path = _owner._folderPaths[indexPath.Row];
                    string displayName = string.IsNullOrWhiteSpace(path)
                        ? "/"
                        : path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? path;

                    int depth = string.IsNullOrWhiteSpace(path)
                        ? 0
                        : path.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
                    cell.IndentationLevel = depth;
                    cell.IndentationWidth = 14f;

                    UIListContentConfiguration content = cell.DefaultContentConfiguration;
                    content.Text = $"[DIR] {displayName}";
                    content.SecondaryText = string.IsNullOrWhiteSpace(path) ? "/" : $"/{path}";
                    cell.ContentConfiguration = content;
                    cell.Accessory = string.Equals(path, _owner._selectedPath, StringComparison.OrdinalIgnoreCase)
                        ? UITableViewCellAccessory.Checkmark
                        : UITableViewCellAccessory.DisclosureIndicator;
                    return cell;
                }

                public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
                {
                    tableView.DeselectRow(indexPath, true);
                    _owner.HandleFolderTapped(indexPath.Row);
                }
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

        private sealed class GalleryMultiPickerDelegate : PHPickerViewControllerDelegate
        {
            private readonly Action<PHPickerResult[]> _onPicked;

            public GalleryMultiPickerDelegate(Action<PHPickerResult[]> onPicked)
            {
                _onPicked = onPicked;
            }

            public override void DidFinishPicking(PHPickerViewController picker, PHPickerResult[] results)
            {
                PHPickerResult[] safeResults = results ?? Array.Empty<PHPickerResult>();
                picker.DismissViewController(true, () =>
                {
                    UIApplication.SharedApplication.BeginInvokeOnMainThread(() => _onPicked(safeResults));
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
