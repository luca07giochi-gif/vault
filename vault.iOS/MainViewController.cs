using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreGraphics;
using Foundation;
using ImageIO;
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
        private const float BottomMenuHeight = 62f;
        private const string RuntimeTempDirectoryName = "vault-ios-runtime";
        private const string ThumbnailCacheDirectoryName = "thumbnails";
        private const string PreviewPerformancePreferenceKey = "vault.ios.preview.performance";
        private const string PreviewPerformanceFastValue = "fast";
        private const string PreviewPerformanceCompactValue = "compact";
        private const int ThumbnailCacheLimit = 36;
        private const int ThumbnailDiskCacheFileLimit = 260;
        private const int ThumbnailPrefetchPadding = 8;
        private const int ThumbnailMinPixelSize = 240;
        private const int ThumbnailDefaultPixelSize = 480;
        private const int ThumbnailMaxPixelSize = 640;
        private const int ThumbnailDecodeConcurrency = 4;

        private enum BrowserViewMode
        {
            List,
            Preview
        }

        private enum PreviewPerformanceMode
        {
            Fast,
            Compact
        }

        private readonly List<VaultFileItem> _visibleItems = new();
        private readonly HashSet<string> _temporaryFiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<Guid> _selectedItemIds = new();
        private readonly Dictionary<Guid, UIImage> _thumbnailCache = new();
        private readonly HashSet<Guid> _thumbnailLoading = new();
        private readonly SemaphoreSlim _thumbnailSemaphore = new(ThumbnailDecodeConcurrency, ThumbnailDecodeConcurrency);
        private readonly object _thumbnailDiskCacheLock = new();

        private VaultPortableReader? _session;
        private NSUrl? _vaultUrl;
        private string _sessionPassword = string.Empty;
        private string _currentFolder = string.Empty;
        private bool _isSelectionMode;
        private BrowserViewMode _viewMode = BrowserViewMode.List;
        private PreviewPerformanceMode _previewPerformanceMode = PreviewPerformanceMode.Fast;
        private int _thumbnailRequestVersion;
        private int _thumbnailTargetPixelSize = ThumbnailDefaultPixelSize;
        private int _thumbnailMemoryCacheLimit = ThumbnailCacheLimit;
        private int _thumbnailDiskCacheFileLimit = ThumbnailDiskCacheFileLimit;
        private int _thumbnailPrefetchPadding = ThumbnailPrefetchPadding;
        private int _thumbnailMaxPixelSize = ThumbnailMaxPixelSize;
        private bool _thumbnailDiskCacheEnabled = true;

        private UITableView? _tableView;
        private UICollectionView? _collectionView;
        private UILabel? _emptyLabel;
        private UIButton? _openVaultCenteredButton;
        private UITabBar? _bottomTabBar;
        private UITabBarItem? _vaultTabItem;
        private UITabBarItem? _addTabItem;
        private UITabBarItem? _viewTabItem;
        private UITabBarItem? _renameTabItem;
        private UITabBarItem? _settingsTabItem;
        private UIView? _busyOverlay;
        private UIActivityIndicatorView? _busyIndicator;
        private UILabel? _busyLabel;
        private UIProgressView? _busyProgressView;
        private UILabel? _busyProgressPercentLabel;

        private UIView? _pathTitleContainer;
        private UIButton? _pathTitleButton;
        private UIButton? _pathNavigateUpButton;
        private UILongPressGestureRecognizer? _tableLongPressRecognizer;
        private UILongPressGestureRecognizer? _collectionLongPressRecognizer;

        private UIDocumentInteractionController? _documentInteractionController;
        private DocumentInteractionDelegate? _documentInteractionDelegate;
        private PickerDelegate? _pickerDelegate;
        private GalleryMultiPickerDelegate? _galleryMultiPickerDelegate;
        private string? _activePreviewTemporaryPath;

        public static void CleanupStaleRuntimeTemporaryFiles()
        {
            string runtimeRoot = GetRuntimeTempDirectoryPath();
            if (!Directory.Exists(runtimeRoot))
                return;

            try
            {
                Directory.Delete(runtimeRoot, recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }

        private static string GetRuntimeTempDirectoryPath()
        {
            return Path.Combine(Path.GetTempPath(), RuntimeTempDirectoryName);
        }

        private static string GetThumbnailCacheDirectoryPath()
        {
            return Path.Combine(GetRuntimeTempDirectoryPath(), ThumbnailCacheDirectoryName);
        }

        private void LoadPreviewPerformancePreference()
        {
            string? raw = NSUserDefaults.StandardUserDefaults.StringForKey(PreviewPerformancePreferenceKey);
            PreviewPerformanceMode mode = string.Equals(raw, PreviewPerformanceCompactValue, StringComparison.OrdinalIgnoreCase)
                ? PreviewPerformanceMode.Compact
                : PreviewPerformanceMode.Fast;

            ApplyPreviewPerformanceMode(mode, persist: false);
        }

        private void ApplyPreviewPerformanceMode(PreviewPerformanceMode mode, bool persist)
        {
            _previewPerformanceMode = mode;

            if (mode == PreviewPerformanceMode.Fast)
            {
                _thumbnailDiskCacheEnabled = true;
                _thumbnailMemoryCacheLimit = ThumbnailCacheLimit;
                _thumbnailDiskCacheFileLimit = ThumbnailDiskCacheFileLimit;
                _thumbnailPrefetchPadding = ThumbnailPrefetchPadding;
                _thumbnailMaxPixelSize = ThumbnailMaxPixelSize;
            }
            else
            {
                _thumbnailDiskCacheEnabled = false;
                _thumbnailMemoryCacheLimit = 18;
                _thumbnailDiskCacheFileLimit = 0;
                _thumbnailPrefetchPadding = 3;
                _thumbnailMaxPixelSize = 480;
                ClearThumbnailDiskCache();
            }

            if (persist)
            {
                NSUserDefaults defaults = NSUserDefaults.StandardUserDefaults;
                defaults.SetString(
                    mode == PreviewPerformanceMode.Fast ? PreviewPerformanceFastValue : PreviewPerformanceCompactValue,
                    PreviewPerformancePreferenceKey);
                defaults.Synchronize();
            }
        }

        public void OnAppDidEnterBackground()
        {
            ClearThumbnailCache();
            ClearActivePreviewTemporaryFile();
        }

        public void OnAppWillTerminate()
        {
            CleanupTemporaryRuntimeFiles();
            CloseCurrentVaultSession(reloadUi: false);
        }

        public override void ViewDidLoad()
        {
            base.ViewDidLoad();

            LoadPreviewPerformancePreference();

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

            _openVaultCenteredButton = new UIButton(UIButtonType.System);
            _openVaultCenteredButton.SetTitle("Apri vault", UIControlState.Normal);
            _openVaultCenteredButton.SetTitleColor(UIColor.White, UIControlState.Normal);
            _openVaultCenteredButton.BackgroundColor = UIColor.FromRGB(10, 132, 255);
            _openVaultCenteredButton.TitleLabel!.Font = UIFont.SystemFontOfSize(18, UIFontWeight.Semibold);
            _openVaultCenteredButton.Layer.CornerRadius = 13f;
            _openVaultCenteredButton.TouchUpInside += (_, _) => _ = PickVaultToOpenAsync();
            View.AddSubview(_openVaultCenteredButton);

            SetupBottomMenu();
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
                const float labelHeight = 42f;
                const float progressWidth = 240f;
                const float progressHeight = 4f;
                const float percentHeight = 18f;
                float centerX = (float)view.Bounds.GetMidX();
                float centerY = (float)view.Bounds.GetMidY();

                _busyIndicator.Frame = new CGRect(centerX - indicatorSize / 2f, centerY - 52f, indicatorSize, indicatorSize);
                _busyLabel.Frame = new CGRect(centerX - labelWidth / 2f, centerY + 8f, labelWidth, labelHeight);

                if (_busyProgressView != null)
                    _busyProgressView.Frame = new CGRect(centerX - progressWidth / 2f, centerY + 58f, progressWidth, progressHeight);

                if (_busyProgressPercentLabel != null)
                    _busyProgressPercentLabel.Frame = new CGRect(centerX - 48f, centerY + 66f, 96f, percentHeight);
            }

            if (_bottomTabBar != null)
            {
                nfloat totalHeight = BottomMenuHeight + view.SafeAreaInsets.Bottom;
                _bottomTabBar.Frame = new CGRect(0, view.Bounds.Height - totalHeight, view.Bounds.Width, totalHeight);
            }

            nfloat bottomInset = (_bottomTabBar != null && !_bottomTabBar.Hidden)
                ? _bottomTabBar.Frame.Height
                : 0f;

            if (_tableView != null)
            {
                _tableView.ContentInset = new UIEdgeInsets(0, 0, bottomInset, 0);
                _tableView.ScrollIndicatorInsets = new UIEdgeInsets(0, 0, bottomInset, 0);
            }

            if (_collectionView != null)
            {
                _collectionView.ContentInset = new UIEdgeInsets(0, 0, bottomInset, 0);
                _collectionView.ScrollIndicatorInsets = new UIEdgeInsets(0, 0, bottomInset, 0);
            }

            if (_openVaultCenteredButton != null)
            {
                nfloat buttonWidth = view.Bounds.Width - (nfloat)40f;
                if (buttonWidth > 250f)
                    buttonWidth = 250f;
                nfloat buttonHeight = 54f;
                _openVaultCenteredButton.Frame = new CGRect(
                    (view.Bounds.Width - buttonWidth) / 2f,
                    (view.Bounds.Height - buttonHeight) / 2f,
                    buttonWidth,
                    buttonHeight);
            }

            UpdatePreviewLayout();
        }

        public override void DidReceiveMemoryWarning()
        {
            base.DidReceiveMemoryWarning();
            ClearThumbnailCache();
            _collectionView?.ReloadData();
        }

        private void SetupBottomMenu()
        {
            _vaultTabItem = new UITabBarItem("Sposta", UIImage.GetSystemImage("arrowshape.turn.up.right"), 0);
            _addTabItem = new UITabBarItem("Aggiungi", UIImage.GetSystemImage("plus.circle"), 1);
            _viewTabItem = new UITabBarItem("Anteprime", UIImage.GetSystemImage("square.grid.2x2"), 2);
            _renameTabItem = new UITabBarItem("Rimuovi", UIImage.GetSystemImage("trash"), 3);
            _settingsTabItem = new UITabBarItem("Impostazioni", UIImage.GetSystemImage("gearshape"), 4);

            _bottomTabBar = new UITabBar
            {
                Translucent = false,
                BarTintColor = UIColor.FromRGB(249, 249, 252),
                TintColor = UIColor.FromRGB(10, 132, 255),
                Items = new[]
                {
                    _vaultTabItem,
                    _addTabItem,
                    _viewTabItem,
                    _renameTabItem,
                    _settingsTabItem
                }
            };

            _bottomTabBar.ItemSelected += OnBottomTabBarItemSelected;
            View?.AddSubview(_bottomTabBar);
        }

        private void OnBottomTabBarItemSelected(object? sender, UITabBarItemEventArgs args)
        {
            HandleBottomMenuSelection(args.Item);
        }

        private void HandleBottomMenuSelection(UITabBarItem? item)
        {
            if (_bottomTabBar != null)
                _bottomTabBar.SelectedItem = null;
            if (item == null)
                return;

            if (ReferenceEquals(item, _vaultTabItem))
            {
                if (_session != null)
                    HandleMoveRequestFromBottomMenu();
                return;
            }

            if (_session == null)
                return;

            if (ReferenceEquals(item, _addTabItem))
            {
                _ = PickAddSourceAsync();
                return;
            }

            if (ReferenceEquals(item, _viewTabItem))
            {
                ToggleViewMode();
                return;
            }

            if (ReferenceEquals(item, _renameTabItem))
            {
                HandleTopRemoveRequest();
                return;
            }

            if (ReferenceEquals(item, _settingsTabItem))
            {
                OpenSettingsMenu();
            }
        }

        private void HandleMoveRequestFromBottomMenu()
        {
            if (_session == null)
                return;
            if (_selectedItemIds.Count == 0)
            {
                ShowError("Seleziona uno o piu elementi da spostare.");
                return;
            }

            OpenMoveDestinationPage(_selectedItemIds.ToArray());
        }

        private void ToggleSelectionModeFromBottomMenu()
        {
            if (_session == null)
                return;

            if (_isSelectionMode)
            {
                ExitSelectionMode(clearSelection: true);
                return;
            }

            _isSelectionMode = true;
            UpdateUiState();
            ReloadVisibleData();
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

            _busyProgressView = new UIProgressView(UIProgressViewStyle.Default)
            {
                Progress = 0f,
                Hidden = true,
                TrackTintColor = UIColor.White.ColorWithAlpha(0.24f),
                ProgressTintColor = UIColor.FromRGB(90, 200, 255)
            };

            _busyProgressPercentLabel = new UILabel
            {
                Font = UIFont.SystemFontOfSize(12, UIFontWeight.Semibold),
                TextColor = UIColor.White,
                TextAlignment = UITextAlignment.Center,
                Hidden = true,
                Text = "0%"
            };

            _busyOverlay.AddSubview(_busyIndicator);
            _busyOverlay.AddSubview(_busyLabel);
            _busyOverlay.AddSubview(_busyProgressView);
            _busyOverlay.AddSubview(_busyProgressPercentLabel);
            view.AddSubview(_busyOverlay);
        }

        private void ConfigureNavigationItems()
        {
            _pathTitleContainer = new UIView(new CGRect(0, 0, 240, 32));

            _pathNavigateUpButton = new UIButton(UIButtonType.System);
            _pathNavigateUpButton.SetImage(UIImage.GetSystemImage("chevron.up"), UIControlState.Normal);
            _pathNavigateUpButton.TintColor = UIColor.FromRGB(10, 132, 255);
            _pathNavigateUpButton.TouchUpInside += (_, _) => NavigateUp();

            _pathTitleButton = new UIButton(UIButtonType.System);
            _pathTitleButton.SetTitle("Cassaforte iOS", UIControlState.Normal);
            _pathTitleButton.TitleLabel!.Font = UIFont.SystemFontOfSize(17, UIFontWeight.Semibold);
            _pathTitleButton.TitleLabel.LineBreakMode = UILineBreakMode.HeadTruncation;
            _pathTitleButton.HorizontalAlignment = UIControlContentHorizontalAlignment.Left;
            _pathTitleButton.TouchUpInside += (_, _) => OpenFolderTreePage();

            _pathTitleContainer.AddSubview(_pathNavigateUpButton);
            _pathTitleContainer.AddSubview(_pathTitleButton);
            NavigationItem.TitleView = _pathTitleContainer;
            NavigationItem.LeftBarButtonItem = null;
            NavigationItem.RightBarButtonItems = Array.Empty<UIBarButtonItem>();
        }

        private void UpdateUiState()
        {
            bool hasVault = _session != null;
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

            if (_pathNavigateUpButton != null)
            {
                bool canNavigateUp = hasVault && !_isSelectionMode && !string.IsNullOrWhiteSpace(_currentFolder);
                _pathNavigateUpButton.Enabled = canNavigateUp;
                _pathNavigateUpButton.Alpha = canNavigateUp ? 1f : 0.38f;
            }

            if (_pathTitleContainer != null && _pathTitleButton != null && _pathNavigateUpButton != null)
            {
                nfloat arrowSize = 22f;
                nfloat spacing = 4f;
                nfloat navBarWidth = NavigationController?.NavigationBar.Bounds.Width
                    ?? View?.Bounds.Width
                    ?? 320f;
                nfloat reservedSideWidth = 192f;
                nfloat maxContainerWidth = navBarWidth - reservedSideWidth;
                if (maxContainerWidth < 90f)
                    maxContainerWidth = 90f;

                nfloat maxTitleWidth = maxContainerWidth - arrowSize - spacing;
                if (maxTitleWidth < 44f)
                    maxTitleWidth = 44f;

                nfloat titleWidth = _pathTitleButton.Bounds.Width + 8f;
                if (titleWidth < 44f)
                    titleWidth = 44f;
                if (titleWidth > maxTitleWidth)
                    titleWidth = maxTitleWidth;

                _pathNavigateUpButton.Frame = new CGRect(0, 5f, arrowSize, arrowSize);
                _pathTitleButton.Frame = new CGRect(arrowSize + spacing, 0, titleWidth, 32f);
                _pathTitleContainer.Frame = new CGRect(0, 0, arrowSize + spacing + titleWidth, 32f);
            }

            if (_vaultTabItem != null)
            {
                _vaultTabItem.Title = "Sposta";
                _vaultTabItem.Image = UIImage.GetSystemImage("arrowshape.turn.up.right");
            }

            if (_viewTabItem != null)
            {
                _viewTabItem.Title = "Anteprime";
                _viewTabItem.Image = UIImage.GetSystemImage("square.grid.2x2");
            }

            if (_renameTabItem != null)
            {
                _renameTabItem.Title = "Rimuovi";
                _renameTabItem.Image = UIImage.GetSystemImage("trash");
            }

            if (_bottomTabBar != null)
                _bottomTabBar.Hidden = !hasVault;

            if (_openVaultCenteredButton != null)
                _openVaultCenteredButton.Hidden = hasVault;

            if (!hasVault)
            {
                _isSelectionMode = false;
                _selectedItemIds.Clear();
                if (_tableView != null)
                    _tableView.Hidden = true;
                if (_collectionView != null)
                    _collectionView.Hidden = true;
                if (_emptyLabel != null)
                    _emptyLabel.Hidden = true;
                RefreshNavigationItems();
                View?.SetNeedsLayout();
                return;
            }

            if (_emptyLabel != null)
            {
                _emptyLabel.Hidden = _visibleItems.Count > 0;
                _emptyLabel.Text = "Cartella vuota.";
            }

            ApplyViewModeVisibility();
            RefreshNavigationItems();
            View?.SetNeedsLayout();
        }

        private void RefreshNavigationItems()
        {
            if (_session == null)
            {
                NavigationItem.LeftBarButtonItem = null;
                NavigationItem.RightBarButtonItems = Array.Empty<UIBarButtonItem>();
                return;
            }

            NavigationItem.LeftBarButtonItem = new UIBarButtonItem(
                "Rinomina",
                UIBarButtonItemStyle.Plain,
                (_, _) => HandleRenameRequest());

            UIBarButtonItem selectionButton = new UIBarButtonItem(
                _isSelectionMode ? "Esci" : "Selezione",
                UIBarButtonItemStyle.Plain,
                (_, _) => ToggleSelectionModeFromBottomMenu());

            NavigationItem.RightBarButtonItems = new[] { selectionButton };
        }

        private void ApplyViewModeVisibility()
        {
            if (_session == null)
            {
                if (_tableView != null)
                    _tableView.Hidden = true;
                if (_collectionView != null)
                    _collectionView.Hidden = true;
                return;
            }

            bool showPreview = _viewMode == BrowserViewMode.Preview;

            if (_tableView != null)
                _tableView.Hidden = showPreview;

            if (_collectionView != null)
                _collectionView.Hidden = !showPreview;

            if (showPreview)
                PrefetchNearbyThumbnails();
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

            nfloat scale = UIScreen.MainScreen.Scale;
            int targetPixels = (int)Math.Ceiling(cellWidth * scale);
            if (targetPixels < ThumbnailMinPixelSize)
                targetPixels = ThumbnailMinPixelSize;
            if (targetPixels > _thumbnailMaxPixelSize)
                targetPixels = _thumbnailMaxPixelSize;
            _thumbnailTargetPixelSize = targetPixels;

            flow.ItemSize = new CGSize(cellWidth, cellWidth + 52f);
            flow.InvalidateLayout();

            if (_viewMode == BrowserViewMode.Preview)
                PrefetchNearbyThumbnails();
        }

        private void ToggleViewMode()
        {
            if (_session == null || _isSelectionMode)
                return;

            Interlocked.Increment(ref _thumbnailRequestVersion);
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

        private void PromptCloseVault()
        {
            if (_session == null)
                return;

            UIAlertController alert = UIAlertController.Create(
                "Chiudi vault",
                "Vuoi chiudere il vault corrente?",
                UIAlertControllerStyle.Alert);

            alert.AddAction(UIAlertAction.Create("Annulla", UIAlertActionStyle.Cancel, null));
            alert.AddAction(UIAlertAction.Create("Chiudi", UIAlertActionStyle.Destructive, __ =>
            {
                CloseCurrentVaultSession(reloadUi: true);
            }));

            PresentViewController(alert, true, null);
        }

        private void CloseCurrentVaultSession(bool reloadUi)
        {
            _session?.Dispose();
            _session = null;
            _vaultUrl = null;
            _sessionPassword = string.Empty;
            _currentFolder = string.Empty;
            _isSelectionMode = false;
            _selectedItemIds.Clear();
            _visibleItems.Clear();
            ClearThumbnailCache();
            ClearThumbnailDiskCache();

            if (reloadUi)
                ReloadFolderItems();
        }

        private void ClearThumbnailCache()
        {
            Interlocked.Increment(ref _thumbnailRequestVersion);
            _thumbnailLoading.Clear();
            foreach (UIImage image in _thumbnailCache.Values)
                image.Dispose();
            _thumbnailCache.Clear();
        }

        private void CleanupTemporaryRuntimeFiles()
        {
            ClearActivePreviewTemporaryFile();
            foreach (string path in _temporaryFiles.ToList())
                DeleteTemporaryFile(path);
        }

        private void ClearActivePreviewTemporaryFile()
        {
            if (string.IsNullOrWhiteSpace(_activePreviewTemporaryPath))
                return;

            string path = _activePreviewTemporaryPath;
            _activePreviewTemporaryPath = null;
            DeleteTemporaryFile(path);
        }

        private void OnDocumentInteractionClosed()
        {
            _documentInteractionController = null;
            ClearActivePreviewTemporaryFile();
        }

        private void HandleRenameRequest()
        {
            if (_session == null)
                return;

            if (_selectedItemIds.Count == 1)
            {
                Guid selectedId = _selectedItemIds.First();
                VaultFileItem? selected = _visibleItems.FirstOrDefault(item => item.Id == selectedId)
                    ?? _session.Files.FirstOrDefault(item => item.Id == selectedId);
                if (selected != null)
                {
                    PromptRename(selected);
                    return;
                }
            }

            if (!string.IsNullOrWhiteSpace(_currentFolder))
            {
                PromptRenameCurrentFolder();
                return;
            }

            ShowError("Per rinominare seleziona un elemento o entra in una cartella.");
        }

        private void HandleTopRemoveRequest()
        {
            if (_session == null)
                return;

            if (_isSelectionMode)
            {
                if (_selectedItemIds.Count == 0)
                {
                    ShowError("Seleziona almeno un elemento da eliminare.");
                    return;
                }

                PromptDeleteSelectedItems();
                return;
            }

            if (!string.IsNullOrWhiteSpace(_currentFolder))
            {
                PromptDeleteCurrentFolder();
                return;
            }

            ShowError("Per rimuovere elementi attiva Selezione multipla.");
        }

        private void PromptRenameCurrentFolder()
        {
            if (_session == null || string.IsNullOrWhiteSpace(_currentFolder))
                return;

            VaultFileItem? folder = _session.Files.FirstOrDefault(item =>
                item.IsFolder &&
                string.Equals(item.FullPath, _currentFolder, StringComparison.OrdinalIgnoreCase));
            if (folder == null)
            {
                ShowError("Cartella corrente non trovata.");
                return;
            }

            UIAlertController alert = UIAlertController.Create("Rinomina cartella", null, UIAlertControllerStyle.Alert);
            alert.AddTextField(field =>
            {
                field.Text = folder.FileName;
                field.ClearButtonMode = UITextFieldViewMode.WhileEditing;
            });

            alert.AddAction(UIAlertAction.Create("Annulla", UIAlertActionStyle.Cancel, null));
            alert.AddAction(UIAlertAction.Create("Conferma", UIAlertActionStyle.Default, __ =>
            {
                string newName = alert.TextFields?.FirstOrDefault()?.Text ?? string.Empty;
                _ = RunBusyAsync("Rinomina cartella...", async () =>
                {
                    if (_session == null)
                        return;

                    Guid folderId = folder.Id;
                    _session.RenameItem(folderId, newName);
                    await PersistVaultAsync();

                    VaultFileItem? renamed = _session.Files.FirstOrDefault(item => item.Id == folderId);
                    if (renamed != null)
                        _currentFolder = renamed.FullPath;

                    ReloadFolderItems();
                });
            }));

            PresentViewController(alert, true, null);
        }

        private void PromptDeleteCurrentFolder()
        {
            if (_session == null || string.IsNullOrWhiteSpace(_currentFolder))
                return;

            VaultFileItem? folder = _session.Files.FirstOrDefault(item =>
                item.IsFolder &&
                string.Equals(item.FullPath, _currentFolder, StringComparison.OrdinalIgnoreCase));
            if (folder == null)
            {
                ShowError("Cartella corrente non trovata.");
                return;
            }

            string parentFolder = folder.ParentPath;
            UIAlertController alert = UIAlertController.Create(
                "Rimuovi cartella",
                $"Vuoi eliminare \"{folder.FileName}\" e il contenuto?",
                UIAlertControllerStyle.Alert);

            alert.AddAction(UIAlertAction.Create("Annulla", UIAlertActionStyle.Cancel, null));
            alert.AddAction(UIAlertAction.Create("Rimuovi", UIAlertActionStyle.Destructive, __ =>
            {
                _ = RunBusyAsync("Eliminazione cartella...", async () =>
                {
                    if (_session == null)
                        return;

                    _session.DeleteItems(new[] { folder.Id });
                    await PersistVaultAsync();
                    _currentFolder = NormalizeFolderPath(parentFolder);
                    EnsureCurrentFolderStillExists();
                    ReloadFolderItems();
                });
            }));

            PresentViewController(alert, true, null);
        }

        private void OpenSettingsMenu()
        {
            UIAlertController sheet = UIAlertController.Create("Impostazioni", null, UIAlertControllerStyle.ActionSheet);

            if (_session != null && !string.IsNullOrWhiteSpace(_currentFolder))
            {
                sheet.AddAction(UIAlertAction.Create("Vai su", UIAlertActionStyle.Default, __ => NavigateUp()));
            }

            if (_session != null)
            {
                sheet.AddAction(UIAlertAction.Create("Struttura cartelle", UIAlertActionStyle.Default, __ => OpenFolderTreePage()));
                sheet.AddAction(UIAlertAction.Create(
                    $"Anteprime: {GetPreviewPerformanceLabel(_previewPerformanceMode)}",
                    UIAlertActionStyle.Default,
                    __ => OpenPreviewPerformanceMenu()));
                sheet.AddAction(UIAlertAction.Create(
                    $"Formato vault: {GetStorageFormatLabel(_session.StorageFormat)}",
                    UIAlertActionStyle.Default,
                    __ => OpenStorageFormatMenu()));
                sheet.AddAction(UIAlertAction.Create("Cambia password", UIAlertActionStyle.Default, __ => PromptChangePassword()));

                string selectLabel = _isSelectionMode ? "Fine selezione" : "Selezione multipla";
                sheet.AddAction(UIAlertAction.Create(selectLabel, UIAlertActionStyle.Default, __ => ToggleSelectionModeFromBottomMenu()));

                if (_isSelectionMode && _selectedItemIds.Count > 0)
                {
                    sheet.AddAction(UIAlertAction.Create("Sposta selezione", UIAlertActionStyle.Default, __ => PromptMoveSelectedItems()));
                    sheet.AddAction(UIAlertAction.Create("Elimina selezione", UIAlertActionStyle.Destructive, __ => PromptDeleteSelectedItems()));
                }

                sheet.AddAction(UIAlertAction.Create("Chiudi vault", UIAlertActionStyle.Destructive, __ => PromptCloseVault()));
            }

            sheet.AddAction(UIAlertAction.Create("Annulla", UIAlertActionStyle.Cancel, null));
            ConfigurePopover(sheet);
            PresentViewController(sheet, true, null);
        }

        private static string GetPreviewPerformanceLabel(PreviewPerformanceMode mode)
        {
            return mode == PreviewPerformanceMode.Fast ? "Veloce" : "Compatta";
        }

        private void OpenPreviewPerformanceMenu()
        {
            UIAlertController sheet = UIAlertController.Create(
                "Prestazioni anteprime",
                "Veloce usa piu cache e spazio temporaneo. Compatta riduce cache e spazio.",
                UIAlertControllerStyle.ActionSheet);

            PreviewPerformanceMode[] modes =
            {
                PreviewPerformanceMode.Fast,
                PreviewPerformanceMode.Compact
            };

            foreach (PreviewPerformanceMode mode in modes)
            {
                string label = GetPreviewPerformanceLabel(mode);
                if (mode == _previewPerformanceMode)
                    label += " (attuale)";

                sheet.AddAction(UIAlertAction.Create(label, UIAlertActionStyle.Default, __ =>
                {
                    SetPreviewPerformanceMode(mode);
                }));
            }

            sheet.AddAction(UIAlertAction.Create("Annulla", UIAlertActionStyle.Cancel, null));
            ConfigurePopover(sheet);
            PresentViewController(sheet, true, null);
        }

        private void SetPreviewPerformanceMode(PreviewPerformanceMode mode)
        {
            if (_previewPerformanceMode == mode)
                return;

            ApplyPreviewPerformanceMode(mode, persist: true);
            ClearThumbnailCache();
            if (!_thumbnailDiskCacheEnabled)
                ClearThumbnailDiskCache();

            if (_viewMode == BrowserViewMode.Preview)
            {
                ReloadVisibleData();
                BeginInvokeOnMainThread(PrefetchNearbyThumbnails);
            }
        }

        private static string GetStorageFormatLabel(VaultStorageFormat format)
        {
            return format switch
            {
                VaultStorageFormat.Legacy => "Legacy",
                VaultStorageFormat.Ultra => "Ultra",
                _ => "Esteso"
            };
        }

        private void OpenStorageFormatMenu()
        {
            if (_session == null)
                return;

            VaultStorageFormat current = _session.StorageFormat;
            UIAlertController sheet = UIAlertController.Create(
                "Formato vault",
                $"Formato attuale: {GetStorageFormatLabel(current)}",
                UIAlertControllerStyle.ActionSheet);

            VaultStorageFormat[] formats =
            {
                VaultStorageFormat.Legacy,
                VaultStorageFormat.Extended,
                VaultStorageFormat.Ultra
            };

            foreach (VaultStorageFormat format in formats)
            {
                string label = GetStorageFormatLabel(format);
                if (format == current)
                    label += " (attuale)";

                sheet.AddAction(UIAlertAction.Create(label, UIAlertActionStyle.Default, __ =>
                {
                    if (format == current)
                        return;

                    _ = ChangeStorageFormatAsync(format);
                }));
            }

            sheet.AddAction(UIAlertAction.Create("Annulla", UIAlertActionStyle.Cancel, null));
            ConfigurePopover(sheet);
            PresentViewController(sheet, true, null);
        }

        private async Task ChangeStorageFormatAsync(VaultStorageFormat newFormat)
        {
            if (_session == null)
                return;
            if (_session.StorageFormat == newFormat)
                return;
            if (_vaultUrl == null)
            {
                ShowError("File vault non disponibile.");
                return;
            }
            if (string.IsNullOrWhiteSpace(_sessionPassword))
            {
                ShowError("Password sessione non disponibile. Riapri il vault.");
                return;
            }

            await RunBusyWithProgressAsync($"Cambio formato in {GetStorageFormatLabel(newFormat)}...", async progress =>
            {
                if (_session == null)
                    return;

                string rollbackPassword = _sessionPassword;
                string rollbackFolder = _currentFolder;

                _session.ChangeStorageFormat(newFormat);
                try
                {
                    await PersistVaultAsync(progress);
                }
                catch
                {
                    await RestoreSessionFromDiskAsync(rollbackPassword, rollbackFolder);
                    throw;
                }

                ReloadFolderItems();
            });
        }

        private void PromptChangePassword()
        {
            if (_session == null)
                return;
            if (string.IsNullOrWhiteSpace(_sessionPassword))
            {
                ShowError("Password sessione non disponibile. Riapri il vault.");
                return;
            }

            UIAlertController alert = UIAlertController.Create(
                "Cambia password",
                "Inserisci la nuova password",
                UIAlertControllerStyle.Alert);

            alert.AddTextField(field =>
            {
                field.Placeholder = "Nuova password";
                field.SecureTextEntry = true;
            });
            alert.AddTextField(field =>
            {
                field.Placeholder = "Conferma password";
                field.SecureTextEntry = true;
            });

            alert.AddAction(UIAlertAction.Create("Annulla", UIAlertActionStyle.Cancel, null));
            alert.AddAction(UIAlertAction.Create("Conferma", UIAlertActionStyle.Default, __ =>
            {
                string newPassword = alert.TextFields?.ElementAtOrDefault(0)?.Text ?? string.Empty;
                string confirmPassword = alert.TextFields?.ElementAtOrDefault(1)?.Text ?? string.Empty;

                if (string.IsNullOrWhiteSpace(newPassword))
                {
                    ShowError("Inserisci una nuova password valida.");
                    return;
                }

                if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
                {
                    ShowError("Le password non coincidono.");
                    return;
                }

                _ = ChangePasswordAsync(newPassword);
            }));

            PresentViewController(alert, true, null);
        }

        private async Task ChangePasswordAsync(string newPassword)
        {
            if (_session == null)
                return;
            if (_vaultUrl == null)
            {
                ShowError("File vault non disponibile.");
                return;
            }
            if (string.IsNullOrWhiteSpace(_sessionPassword))
            {
                ShowError("Password sessione non disponibile. Riapri il vault.");
                return;
            }

            await RunBusyWithProgressAsync("Aggiornamento password...", async progress =>
            {
                if (_session == null)
                    return;

                string rollbackPassword = _sessionPassword;
                string rollbackFolder = _currentFolder;

                _session.ChangePassword(newPassword);
                try
                {
                    await PersistVaultAsync(progress);
                }
                catch
                {
                    await RestoreSessionFromDiskAsync(rollbackPassword, rollbackFolder);
                    throw;
                }

                _sessionPassword = newPassword;
            });
        }

        private async Task RestoreSessionFromDiskAsync(string password, string folderPath)
        {
            if (_vaultUrl == null)
                throw new InvalidOperationException("File vault non disponibile.");

            VaultPortableReader restored = await Task.Run(() => OpenVaultReader(_vaultUrl, password));

            _session?.Dispose();
            _session = restored;
            _sessionPassword = password;
            _currentFolder = NormalizeFolderPath(folderPath);
            _isSelectionMode = false;
            _selectedItemIds.Clear();
            ClearThumbnailCache();
            ReloadFolderItems();
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

            OpenMoveDestinationPage(_selectedItemIds.ToArray());
        }

        private void OpenMoveDestinationPage(Guid[] selectedIds)
        {
            if (_session == null || NavigationController == null || selectedIds.Length == 0)
                return;

            var destinationPage = new MoveDestinationViewController(
                _session,
                _currentFolder,
                selectedIds,
                (destinationPath, idsToMove) => _ = MoveSelectedItemsToDestinationAsync(idsToMove, destinationPath));

            NavigationController.PushViewController(destinationPage, true);
        }

        private async Task MoveSelectedItemsToDestinationAsync(Guid[] selectedIds, string destinationPath)
        {
            if (_session == null || selectedIds.Length == 0)
                return;

            string normalizedDestination = NormalizeFolderPath(destinationPath);
            await RunBusyAsync("Spostamento elementi...", async () =>
            {
                if (_session == null)
                    return;

                var createdFolderIds = new List<Guid>();
                try
                {
                    EnsureDestinationFolderExistsForMove(normalizedDestination, createdFolderIds);
                    _session.MoveItems(selectedIds, normalizedDestination);
                    await PersistVaultAsync();
                    EnsureCurrentFolderStillExists();
                    ExitSelectionMode(clearSelection: true);
                    ReloadFolderItems();
                }
                catch
                {
                    if (_session != null && createdFolderIds.Count > 0)
                    {
                        try
                        {
                            _session.DeleteItems(createdFolderIds);
                        }
                        catch
                        {
                            // Best effort rollback.
                        }
                    }

                    throw;
                }
            });
        }

        private void EnsureDestinationFolderExistsForMove(string destinationPath, ICollection<Guid>? createdFolderIds = null)
        {
            if (_session == null)
                return;

            string normalized = NormalizeFolderPath(destinationPath);
            if (string.IsNullOrWhiteSpace(normalized))
                return;

            HashSet<string> existing = _session.GetAllFolderPaths()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (existing.Contains(normalized))
                return;

            string parent = string.Empty;
            foreach (string segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                string current = string.IsNullOrWhiteSpace(parent)
                    ? segment
                    : $"{parent}/{segment}";

                if (existing.Contains(current))
                {
                    parent = current;
                    continue;
                }

                VaultFileItem created = _session.CreateFolder(segment, parent);
                string createdPath = NormalizeFolderPath(created.FullPath);
                existing.Add(createdPath);
                parent = createdPath;
                createdFolderIds?.Add(created.Id);
            }
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

            if (_viewMode == BrowserViewMode.Preview)
                BeginInvokeOnMainThread(PrefetchNearbyThumbnails);
        }

        private void ReloadThumbnailCell(Guid itemId)
        {
            if (_collectionView == null || _viewMode != BrowserViewMode.Preview)
                return;

            int index = _visibleItems.FindIndex(item => item.Id == itemId);
            if (index < 0)
                return;

            NSIndexPath indexPath = NSIndexPath.FromItemSection(index, 0);
            if (_collectionView.CellForItem(indexPath) is not PreviewCell cell)
                return;

            VaultFileItem item = _visibleItems[index];
            bool isSelected = _selectedItemIds.Contains(item.Id);
            UIImage? thumbnail = _thumbnailCache.TryGetValue(item.Id, out UIImage? cached) ? cached : null;
            cell.Configure(item, thumbnail, isSelected, _isSelectionMode);
        }

        private void PrefetchNearbyThumbnails()
        {
            if (_session == null || _collectionView == null || _viewMode != BrowserViewMode.Preview)
                return;
            if (_visibleItems.Count == 0)
                return;

            NSIndexPath[] visiblePaths = _collectionView.IndexPathsForVisibleItems ?? Array.Empty<NSIndexPath>();
            if (visiblePaths.Length == 0)
                return;

            int minVisible = visiblePaths.Min(path => (int)path.Item);
            int maxVisible = visiblePaths.Max(path => (int)path.Item);
            int start = Math.Max(0, minVisible - _thumbnailPrefetchPadding);
            int end = Math.Min(_visibleItems.Count - 1, maxVisible + _thumbnailPrefetchPadding);

            for (int i = start; i <= end; i++)
            {
                VaultFileItem item = _visibleItems[i];
                if (item.IsFolder || !IsImagePreviewCandidate(item.FileName))
                    continue;
                if (_thumbnailCache.ContainsKey(item.Id))
                    continue;
                if (_thumbnailLoading.Contains(item.Id))
                    continue;

                QueueThumbnailGeneration(item);
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

            await RunBusyWithProgressAsync("Apertura vault...", async progress =>
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                VaultPortableReader reader = await Task.Run(() => OpenVaultReader(vaultUrl, password, progress));

                _session?.Dispose();
                _session = reader;
                _vaultUrl = vaultUrl;
                _sessionPassword = password;
                _currentFolder = string.Empty;
                _isSelectionMode = false;
                _selectedItemIds.Clear();
                ClearThumbnailCache();
                ClearThumbnailDiskCache();

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
            Interlocked.Increment(ref _thumbnailRequestVersion);
            EnsureCurrentFolderStillExists();
            _visibleItems.Clear();

            if (_session != null)
            {
                _visibleItems.AddRange(_session.GetItemsInFolder(_currentFolder));
            }

            HashSet<Guid> visibleIds = _visibleItems.Select(item => item.Id).ToHashSet();
            foreach (Guid id in _thumbnailCache.Keys.ToList())
            {
                if (visibleIds.Contains(id))
                    continue;

                _thumbnailCache[id].Dispose();
                _thumbnailCache.Remove(id);
            }
            _thumbnailLoading.RemoveWhere(id => !visibleIds.Contains(id));

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

        private async Task PersistVaultAsync(IProgress<double>? progress = null)
        {
            if (_session == null || _vaultUrl == null || !_session.IsDirty)
                return;

            VaultPortableReader session = _session;
            NSUrl vaultUrl = _vaultUrl;
            await Task.Run(() => PersistVaultToUrl(vaultUrl, session, progress));
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

        private async Task RunBusyWithProgressAsync(string message, Func<IProgress<double>, Task> action)
        {
            SetBusyState(true, message, showProgress: true);
            IProgress<double> progress = new Progress<double>(UpdateBusyProgress);
            UpdateBusyProgress(0d);

            try
            {
                await action(progress);
                UpdateBusyProgress(100d);
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

        private void SetBusyState(bool busy, string message, bool showProgress = false)
        {
            if (_busyOverlay == null || _busyIndicator == null || _busyLabel == null)
                return;
            UIView? view = View;
            if (view == null)
                return;

            _busyLabel.Text = string.IsNullOrWhiteSpace(message) ? "Operazione in corso..." : message;
            _busyOverlay.Hidden = !busy;
            view.UserInteractionEnabled = !busy;

            if (_busyProgressView != null)
            {
                _busyProgressView.Hidden = !busy || !showProgress;
                _busyProgressView.Progress = showProgress ? 0f : _busyProgressView.Progress;
            }

            if (_busyProgressPercentLabel != null)
            {
                _busyProgressPercentLabel.Hidden = !busy || !showProgress;
                if (showProgress)
                    _busyProgressPercentLabel.Text = "0%";
            }

            if (busy)
                _busyIndicator.StartAnimating();
            else
                _busyIndicator.StopAnimating();
        }

        private void UpdateBusyProgress(double percent)
        {
            if (_busyProgressView == null || _busyProgressPercentLabel == null)
                return;

            void ApplyProgress()
            {
                double clamped = Math.Max(0d, Math.Min(100d, percent));
                _busyProgressView.Progress = (float)(clamped / 100d);
                _busyProgressPercentLabel.Text = $"{Math.Round(clamped):0}%";
            }

            if (NSThread.Current.IsMainThread)
            {
                ApplyProgress();
                return;
            }

            BeginInvokeOnMainThread(ApplyProgress);
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

            ClearActivePreviewTemporaryFile();
            _activePreviewTemporaryPath = localPath;

            NSUrl fileUrl = NSUrl.FromFilename(localPath);
            _documentInteractionDelegate ??= new DocumentInteractionDelegate(this, OnDocumentInteractionClosed);
            _documentInteractionController = UIDocumentInteractionController.FromUrl(fileUrl);
            _documentInteractionController.Delegate = _documentInteractionDelegate;

            bool previewShown = _documentInteractionController.PresentPreview(true);
            if (previewShown)
                return;

            bool menuShown = _documentInteractionController.PresentOptionsMenu(
                new CGRect(view.Bounds.GetMidX(), view.Bounds.GetMidY(), 1, 1),
                view,
                true);

            if (!menuShown)
                OnDocumentInteractionClosed();
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

            if (string.Equals(path, _activePreviewTemporaryPath, StringComparison.OrdinalIgnoreCase))
                _activePreviewTemporaryPath = null;

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

                    string runtimeRoot = GetRuntimeTempDirectoryPath();
                    Directory.CreateDirectory(runtimeRoot);
                    string tempPath = Path.Combine(runtimeRoot, $"{Guid.NewGuid():N}{extension}");
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
            string runtimeRoot = GetRuntimeTempDirectoryPath();
            Directory.CreateDirectory(runtimeRoot);
            return Path.Combine(runtimeRoot, tempName);
        }

        private static VaultPortableReader OpenVaultReader(NSUrl fileUrl, string password, IProgress<double>? progress = null)
        {
            using var scope = new SecurityScopeAccess(fileUrl);

            string? path = fileUrl.Path;
            if (string.IsNullOrWhiteSpace(path))
                throw new IOException("Percorso file non valido.");

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return VaultPortableReader.Open(stream, password, allowUltra: true, progress: progress);
        }

        private static void PersistVaultToUrl(NSUrl fileUrl, VaultPortableReader session, IProgress<double>? progress = null)
        {
            using var scope = new SecurityScopeAccess(fileUrl);

            string? path = fileUrl.Path;
            if (string.IsNullOrWhiteSpace(path))
                throw new IOException("Percorso file non valido.");

            string fileName = Path.GetFileName(path);
            string tmpPath = CreateVaultWriteTempPath(fileName, ".tmp");
            string backupPath = CreateVaultWriteTempPath(fileName, ".bak");

            try
            {
                using (var output = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    session.SaveToStream(output, progress);
                    output.Flush(flushToDisk: true);
                }

                if (!File.Exists(path))
                {
                    File.Move(tmpPath, path);
                    TryDeletePath(backupPath);
                    return;
                }

                if (!TryReplaceFile(tmpPath, path, backupPath))
                {
                    if (!TryMoveWithOverwrite(tmpPath, path))
                        OverwriteFileWithRollback(tmpPath, path, backupPath);
                }

                TryDeletePath(tmpPath);
                TryDeletePath(backupPath);
            }
            catch
            {
                TryDeletePath(tmpPath);
                TryDeletePath(backupPath);
                throw;
            }
        }

        private static string CreateVaultWriteTempPath(string baseFileName, string suffix)
        {
            string runtimeRoot = GetRuntimeTempDirectoryPath();
            Directory.CreateDirectory(runtimeRoot);
            string safeBaseName = string.IsNullOrWhiteSpace(baseFileName) ? "vault" : baseFileName;
            return Path.Combine(runtimeRoot, $"{safeBaseName}.{Guid.NewGuid():N}{suffix}");
        }

        private static bool TryReplaceFile(string sourcePath, string destinationPath, string backupPath)
        {
            try
            {
                File.Replace(sourcePath, destinationPath, backupPath, ignoreMetadataErrors: true);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (PlatformNotSupportedException)
            {
                return false;
            }
        }

        private static bool TryMoveWithOverwrite(string sourcePath, string destinationPath)
        {
            try
            {
                File.Move(sourcePath, destinationPath, overwrite: true);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (PlatformNotSupportedException)
            {
                return false;
            }
        }

        private static void OverwriteFileWithRollback(string sourcePath, string destinationPath, string backupPath)
        {
            bool hasBackup = false;

            try
            {
                if (File.Exists(destinationPath))
                {
                    File.Copy(destinationPath, backupPath, overwrite: true);
                    hasBackup = true;
                }

                using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var output = new FileStream(destinationPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
                output.SetLength(0);
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            catch
            {
                if (hasBackup && File.Exists(backupPath))
                {
                    try
                    {
                        using var backupInput = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        using var restoreOutput = new FileStream(destinationPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
                        restoreOutput.SetLength(0);
                        backupInput.CopyTo(restoreOutput);
                        restoreOutput.Flush(flushToDisk: true);
                    }
                    catch
                    {
                        // Best effort rollback.
                    }
                }

                throw;
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

        private UIImage? GetOrQueueThumbnail(VaultFileItem item)
        {
            if (item.IsFolder || !IsImagePreviewCandidate(item.FileName))
                return null;

            if (_thumbnailCache.TryGetValue(item.Id, out UIImage? existing))
                return existing;

            UIImage? fromDisk = TryLoadThumbnailFromDisk(item.Id);
            if (fromDisk != null)
            {
                StoreThumbnailInMemoryCache(item.Id, fromDisk);
                return fromDisk;
            }

            QueueThumbnailGeneration(item);
            return null;
        }

        private void StoreThumbnailInMemoryCache(Guid itemId, UIImage image)
        {
            if (_thumbnailCache.TryGetValue(itemId, out UIImage? previous))
            {
                _thumbnailCache[itemId] = image;
                if (!ReferenceEquals(previous, image) && !IsPreviewItemVisible(itemId))
                    previous.Dispose();
                return;
            }

            EvictThumbnailFromMemoryCacheIfNeeded(itemId);
            _thumbnailCache[itemId] = image;
        }

        private void EvictThumbnailFromMemoryCacheIfNeeded(Guid preserveItemId)
        {
            if (_thumbnailMemoryCacheLimit <= 0 || _thumbnailCache.Count < _thumbnailMemoryCacheLimit)
                return;

            HashSet<Guid> protectedIds = GetVisiblePreviewItemIds();
            protectedIds.Add(preserveItemId);

            Guid? removeId = null;
            foreach (Guid candidate in _thumbnailCache.Keys)
            {
                if (protectedIds.Contains(candidate))
                    continue;

                removeId = candidate;
                break;
            }

            if (removeId == null)
                return;

            UIImage image = _thumbnailCache[removeId.Value];
            _thumbnailCache.Remove(removeId.Value);
            image.Dispose();
        }

        private bool IsPreviewItemVisible(Guid itemId)
        {
            return GetVisiblePreviewItemIds().Contains(itemId);
        }

        private HashSet<Guid> GetVisiblePreviewItemIds()
        {
            var visibleIds = new HashSet<Guid>();
            if (_collectionView == null)
                return visibleIds;

            NSIndexPath[] visiblePaths = _collectionView.IndexPathsForVisibleItems ?? Array.Empty<NSIndexPath>();
            foreach (NSIndexPath path in visiblePaths)
            {
                int index = (int)path.Item;
                if (index < 0 || index >= _visibleItems.Count)
                    continue;

                visibleIds.Add(_visibleItems[index].Id);
            }

            return visibleIds;
        }

        private void QueueThumbnailGeneration(VaultFileItem item)
        {
            if (_session == null || _viewMode != BrowserViewMode.Preview || item.IsFolder || !IsImagePreviewCandidate(item.FileName))
                return;
            if (_thumbnailCache.ContainsKey(item.Id))
                return;
            if (!_thumbnailLoading.Add(item.Id))
                return;

            int requestVersion = Volatile.Read(ref _thumbnailRequestVersion);

            _ = Task.Run(async () =>
                {
                    await _thumbnailSemaphore.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (requestVersion != Volatile.Read(ref _thumbnailRequestVersion))
                            return null;

                        return BuildThumbnailImageForItem(item);
                    }
                    finally
                    {
                        _thumbnailSemaphore.Release();
                    }
                })
                .ContinueWith(task =>
                {
                    BeginInvokeOnMainThread(() =>
                    {
                        _thumbnailLoading.Remove(item.Id);
                        if (task.Status != TaskStatus.RanToCompletion)
                            return;

                        UIImage? thumb = task.Result;
                        if (thumb == null)
                            return;

                        if (_session == null ||
                            requestVersion != Volatile.Read(ref _thumbnailRequestVersion) ||
                            _visibleItems.All(visible => visible.Id != item.Id))
                        {
                            thumb.Dispose();
                            return;
                        }

                        StoreThumbnailInMemoryCache(item.Id, thumb);
                        ReloadThumbnailCell(item.Id);
                    });
                });
        }

        private UIImage? BuildThumbnailImageForItem(VaultFileItem item)
        {
            VaultPortableReader? session = _session;
            if (session == null)
                return null;

            UIImage? cachedThumbnail = TryLoadThumbnailFromDisk(item.Id);
            if (cachedThumbnail != null)
                return cachedThumbnail;

            bool deleteTempAfterUse = false;
            string sourcePath = string.Empty;
            try
            {
                if (!session.TryGetLocalContentPath(item.Id, out sourcePath))
                {
                    sourcePath = CreateTemporaryPath(item.FileName);
                    deleteTempAfterUse = true;
                    using (var output = new FileStream(sourcePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        session.CopyFileContentToStream(item.Id, output);
                    }
                }

                UIImage? generated = CreateDownsampledThumbnail(sourcePath, _thumbnailTargetPixelSize);
                if (generated != null)
                    SaveThumbnailToDisk(item.Id, generated);

                return generated;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (deleteTempAfterUse)
                    TryDeletePath(sourcePath);
            }
        }

        private UIImage? TryLoadThumbnailFromDisk(Guid itemId)
        {
            if (!_thumbnailDiskCacheEnabled)
                return null;

            string cachePath = GetThumbnailCachePath(itemId);
            if (!File.Exists(cachePath))
                return null;

            try
            {
                UIImage? image = UIImage.FromFile(cachePath);
                if (image == null)
                    return null;

                lock (_thumbnailDiskCacheLock)
                {
                    if (File.Exists(cachePath))
                        File.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow);
                }

                UIImage rendered = image.ImageWithRenderingMode(UIImageRenderingMode.AlwaysOriginal);
                if (!ReferenceEquals(rendered, image))
                    image.Dispose();

                return rendered;
            }
            catch
            {
                TryDeletePath(cachePath);
                return null;
            }
        }

        private void SaveThumbnailToDisk(Guid itemId, UIImage image)
        {
            if (!_thumbnailDiskCacheEnabled)
                return;

            string cacheDirectory = GetThumbnailCacheDirectoryPath();
            string cachePath = GetThumbnailCachePath(itemId);

            lock (_thumbnailDiskCacheLock)
            {
                try
                {
                    Directory.CreateDirectory(cacheDirectory);
                    using NSData? jpegData = image.AsJPEG((nfloat)0.78f);
                    if (jpegData == null)
                        return;

                    File.WriteAllBytes(cachePath, jpegData.ToArray());
                    File.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow);
                    PruneThumbnailDiskCacheIfNeeded();
                }
                catch
                {
                    // Best effort cache write.
                }
            }
        }

        private void PruneThumbnailDiskCacheIfNeeded()
        {
            if (_thumbnailDiskCacheFileLimit <= 0)
                return;

            string cacheDirectory = GetThumbnailCacheDirectoryPath();
            if (!Directory.Exists(cacheDirectory))
                return;

            FileInfo[] files;
            try
            {
                files = new DirectoryInfo(cacheDirectory).GetFiles("*.jpg");
            }
            catch
            {
                return;
            }

            if (files.Length <= _thumbnailDiskCacheFileLimit)
                return;

            int toRemove = files.Length - _thumbnailDiskCacheFileLimit;
            foreach (FileInfo file in files.OrderBy(f => f.LastWriteTimeUtc).Take(toRemove))
                TryDeletePath(file.FullName);
        }

        private static string GetThumbnailCachePath(Guid itemId)
        {
            return Path.Combine(GetThumbnailCacheDirectoryPath(), $"{itemId:N}.jpg");
        }

        private void ClearThumbnailDiskCache()
        {
            string cacheDirectory = GetThumbnailCacheDirectoryPath();
            if (!Directory.Exists(cacheDirectory))
                return;

            lock (_thumbnailDiskCacheLock)
            {
                try
                {
                    Directory.Delete(cacheDirectory, recursive: true);
                }
                catch
                {
                    // Best effort cleanup.
                }
            }
        }

        private static UIImage? CreateDownsampledThumbnail(string sourcePath, int maxPixelSize)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                return null;

            NSUrl fileUrl = NSUrl.FromFilename(sourcePath);
            using CGImageSource? imageSource = CGImageSource.FromUrl(fileUrl, new CGImageOptions
            {
                ShouldCache = false
            });
            if (imageSource == null || imageSource.ImageCount == 0)
                return null;

            using CGImage? cgImage = imageSource.CreateThumbnail(0, new CGImageThumbnailOptions
            {
                CreateThumbnailFromImageIfAbsent = true,
                CreateThumbnailWithTransform = true,
                MaxPixelSize = maxPixelSize,
                ShouldCache = true,
                ShouldCacheImmediately = true
            });
            if (cgImage == null)
                return null;

            UIImage? baseImage = UIImage.FromImage(cgImage);
            if (baseImage == null)
                return null;

            UIImage rendered = baseImage.ImageWithRenderingMode(UIImageRenderingMode.AlwaysOriginal);
            if (!ReferenceEquals(rendered, baseImage))
                baseImage.Dispose();

            return rendered;
        }

        private static bool IsImagePreviewCandidate(string? fileName)
        {
            string ext = (Path.GetExtension(fileName ?? string.Empty) ?? string.Empty).ToLowerInvariant();
            return ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".heic" or ".tif" or ".tiff";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CleanupTemporaryRuntimeFiles();
                CloseCurrentVaultSession(reloadUi: false);
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
                UIImage? thumbnail = _owner.GetOrQueueThumbnail(item);
                cell.Configure(item, thumbnail, isSelected, _owner._isSelectionMode);
                return cell;
            }

            public override void ItemSelected(UICollectionView collectionView, NSIndexPath indexPath)
            {
                collectionView.DeselectItem(indexPath, false);
                _owner.HandleCollectionItemTapped(indexPath.Row);
            }

            public override void Scrolled(UIScrollView scrollView)
            {
                _owner.PrefetchNearbyThumbnails();
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

            public void Configure(VaultFileItem item, UIImage? thumbnail, bool isSelected, bool isSelectionMode)
            {
                if (thumbnail != null)
                {
                    _iconView.Image = thumbnail;
                    _iconView.ContentMode = UIViewContentMode.ScaleAspectFill;
                    _iconView.ClipsToBounds = true;
                    _iconView.TintColor = UIColor.Clear;
                }
                else
                {
                    _iconView.Image = UIImage.GetSystemImage(GetSymbolName(item));
                    _iconView.ContentMode = UIViewContentMode.ScaleAspectFit;
                    _iconView.ClipsToBounds = false;
                    _iconView.TintColor = item.IsFolder ? UIColor.FromRGB(10, 132, 255) : UIColor.Gray;
                }

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

        private sealed class MoveDestinationViewController : UIViewController
        {
            private const string MoveCellId = "MoveDestinationCell";

            private readonly VaultPortableReader _session;
            private readonly Guid[] _itemIds;
            private readonly Action<string, Guid[]> _onMoveConfirmed;
            private readonly List<string> _folderPaths = new();
            private string _selectedDestination;
            private UITableView? _tableView;

            public MoveDestinationViewController(
                VaultPortableReader session,
                string currentFolder,
                Guid[] itemIds,
                Action<string, Guid[]> onMoveConfirmed)
            {
                _session = session ?? throw new ArgumentNullException(nameof(session));
                _itemIds = itemIds ?? Array.Empty<Guid>();
                _selectedDestination = NormalizeFolderPath(currentFolder);
                _onMoveConfirmed = onMoveConfirmed ?? throw new ArgumentNullException(nameof(onMoveConfirmed));
            }

            public override void ViewDidLoad()
            {
                base.ViewDidLoad();

                View!.BackgroundColor = UIColor.White;
                Title = "Sposta elementi";
                NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
                NavigationItem.RightBarButtonItems = new[]
                {
                    new UIBarButtonItem("Sposta qui", UIBarButtonItemStyle.Done, (_, _) => ConfirmMove()),
                    new UIBarButtonItem("Nuova", UIBarButtonItemStyle.Plain, (_, _) => PromptCreateFolder(_selectedDestination))
                };

                _tableView = new UITableView(View.Bounds, UITableViewStyle.InsetGrouped)
                {
                    AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight,
                    Source = new MoveDestinationSource(this)
                };
                View.AddSubview(_tableView);

                ReloadFolders();
            }

            private void ReloadFolders()
            {
                _folderPaths.Clear();
                _folderPaths.AddRange(_session.GetAllFolderPaths().OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
                EnsureSelectedDestinationExists();
                _tableView?.ReloadData();
            }

            private void EnsureSelectedDestinationExists()
            {
                if (_folderPaths.Any(path => string.Equals(path, _selectedDestination, StringComparison.OrdinalIgnoreCase)))
                    return;

                string probe = _selectedDestination;
                while (!string.IsNullOrWhiteSpace(probe))
                {
                    probe = GetParentPath(probe);
                    if (_folderPaths.Any(path => string.Equals(path, probe, StringComparison.OrdinalIgnoreCase)))
                    {
                        _selectedDestination = probe;
                        return;
                    }
                }

                _selectedDestination = string.Empty;
            }

            private void HandleFolderTapped(int index)
            {
                if (index < 0 || index >= _folderPaths.Count)
                    return;

                _selectedDestination = _folderPaths[index];
                _tableView?.ReloadData();
            }

            private void ConfirmMove()
            {
                if (_itemIds.Length == 0)
                {
                    ShowError("Nessun elemento selezionato.");
                    return;
                }

                string destination = _selectedDestination;
                Guid[] ids = _itemIds.ToArray();
                _onMoveConfirmed(destination, ids);
                NavigationController?.PopViewController(true);
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
                    string rawName = alert.TextFields?.FirstOrDefault()?.Text ?? string.Empty;
                    string name = NormalizeFolderName(rawName);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        ShowError("Inserisci un nome cartella valido.");
                        return;
                    }

                    string normalizedParent = NormalizeFolderPath(parentPath);
                    string fullPath = string.IsNullOrWhiteSpace(normalizedParent)
                        ? name
                        : $"{normalizedParent}/{name}";
                    if (_folderPaths.Any(path => string.Equals(path, fullPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        ShowError("Esiste gia una cartella con questo nome nella posizione scelta.");
                        return;
                    }

                    _folderPaths.Add(fullPath);
                    _folderPaths.Sort(StringComparer.OrdinalIgnoreCase);
                    _selectedDestination = fullPath;
                    _tableView?.ReloadData();
                }));

                PresentViewController(alert, true, null);
            }

            private static string NormalizeFolderName(string name)
            {
                string trimmed = name?.Trim() ?? string.Empty;
                if (trimmed.Contains('/') || trimmed.Contains('\\'))
                    return string.Empty;
                return trimmed;
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

            private void ShowError(string message)
            {
                UIAlertController alert = UIAlertController.Create(
                    "Operazione non riuscita",
                    string.IsNullOrWhiteSpace(message) ? "Errore sconosciuto." : message,
                    UIAlertControllerStyle.Alert);
                alert.AddAction(UIAlertAction.Create("OK", UIAlertActionStyle.Default, null));
                PresentViewController(alert, true, null);
            }

            private sealed class MoveDestinationSource : UITableViewSource
            {
                private readonly MoveDestinationViewController _owner;

                public MoveDestinationSource(MoveDestinationViewController owner)
                {
                    _owner = owner;
                }

                public override nint RowsInSection(UITableView tableView, nint section) =>
                    _owner._folderPaths.Count;

                public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
                {
                    UITableViewCell cell = tableView.DequeueReusableCell(MoveCellId)
                        ?? new UITableViewCell(UITableViewCellStyle.Subtitle, MoveCellId);

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
                    cell.Accessory = string.Equals(path, _owner._selectedDestination, StringComparison.OrdinalIgnoreCase)
                        ? UITableViewCellAccessory.Checkmark
                        : UITableViewCellAccessory.None;
                    return cell;
                }

                public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
                {
                    tableView.DeselectRow(indexPath, true);
                    _owner.HandleFolderTapped(indexPath.Row);
                }
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
                    BeginInvokeOnMainThread(() => PromptCreateFolder(folderPath));
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
                    string rawName = alert.TextFields?.FirstOrDefault()?.Text ?? string.Empty;
                    string name = NormalizeFolderName(rawName);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        ShowError("Inserisci un nome cartella valido.");
                        return;
                    }

                    string normalizedParent = NormalizeFolderPath(parentPath);
                    try
                    {
                        VaultFileItem folder = _session.CreateFolder(name, normalizedParent);
                        _selectedPath = NormalizeFolderPath(folder.FullPath);
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

            private static string NormalizeFolderName(string name)
            {
                string trimmed = name?.Trim() ?? string.Empty;
                if (trimmed.Contains('/') || trimmed.Contains('\\'))
                    return string.Empty;
                return trimmed;
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
            private readonly Action _onClosed;

            public DocumentInteractionDelegate(UIViewController owner, Action onClosed)
            {
                _owner = owner;
                _onClosed = onClosed;
            }

            public override UIViewController ViewControllerForPreview(UIDocumentInteractionController controller)
            {
                return _owner;
            }

            public override void DidEndPreview(UIDocumentInteractionController controller)
            {
                _onClosed();
            }

            public override void DidDismissOptionsMenu(UIDocumentInteractionController controller)
            {
                _onClosed();
            }

            public override void DidDismissOpenInMenu(UIDocumentInteractionController controller)
            {
                _onClosed();
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
