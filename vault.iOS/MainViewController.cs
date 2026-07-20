using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AVFoundation;
using AVKit;
using CoreGraphics;
using Foundation;
using ImageIO;
using Photos;
using PhotosUI;
using SharpCompress.Archives;
using UIKit;
using vault.Core;
using vault.Core.Domain;
using vault.iOS.Shared;

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
        private const string ItemSortPreferenceKey = "vault.ios.item.sort";
        private const string ItemSortNameAscendingValue = "name_asc";
        private const string ItemSortNameDescendingValue = "name_desc";
        private const string ItemSortLatestAddedValue = "latest_added";
        private const string AutoOpenVaultPreferenceKey = "vault.ios.auto.open.vault";
        private const int ThumbnailCacheLimit = 36;
        private const int ThumbnailDiskCacheFileLimit = 260;
        private const int ThumbnailPrefetchPadding = 8;
        private const int ThumbnailMinPixelSize = 240;
        private const int ThumbnailDefaultPixelSize = 480;
        private const int ThumbnailMaxPixelSize = 640;
        private const int ThumbnailDecodeConcurrency = 4;
        private const long LegacyAutoUpgradeThresholdBytes = 180L * 1024 * 1024;
        private const long LegacyUltraUpgradeThresholdBytes = 700L * 1024 * 1024;
        private const long PersistSafetyMarginBytes = 96L * 1024 * 1024;
        private const int VaultPersistCopyBufferSize = 1024 * 1024;
        private const int DraftSummaryEntryLimit = 12;
        private const int DraftPromptVisibleSummaryLimit = 6;

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

        private enum ItemSortMode
        {
            NameAscending,
            NameDescending,
            LatestAdded
        }

        private enum PendingChangesDecision
        {
            Cancel,
            Discard,
            Save
        }

        private readonly List<VaultFileItem> _visibleItems = new();
        private readonly HashSet<string> _temporaryFiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<Guid> _selectedItemIds = new();
        private readonly Dictionary<Guid, UIImage> _thumbnailCache = new();
        private readonly HashSet<Guid> _thumbnailLoading = new();
        private readonly SemaphoreSlim _thumbnailSemaphore = new(ThumbnailDecodeConcurrency, ThumbnailDecodeConcurrency);
        private readonly object _thumbnailDiskCacheLock = new();
        private readonly List<string> _pendingChangeSummary = new();

        private VaultPortableReader? _session;
        private NSUrl? _vaultUrl;
        private string _sessionPassword = string.Empty;
        private string _currentFolder = string.Empty;
        private bool _isSelectionMode;
        private BrowserViewMode _viewMode = BrowserViewMode.List;
        private PreviewPerformanceMode _previewPerformanceMode = PreviewPerformanceMode.Fast;
        private ItemSortMode _itemSortMode = ItemSortMode.NameAscending;
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
        private UIButton? _createVaultCenteredButton;
        private UIButton? _extraButton;
        private UILabel? _homeVersionLabel;
        private UITabBar? _bottomTabBar;
        private UITabBarItem? _vaultTabItem;
        private UITabBarItem? _addTabItem;
        private UITabBarItem? _viewTabItem;
        private UITabBarItem? _renameTabItem;
        private UITabBarItem? _settingsTabItem;
        private UITabBarItem? _extraTabItem;
        private UIView? _busyOverlay;
        private UIActivityIndicatorView? _busyIndicator;
        private UILabel? _busyLabel;
        private UIProgressView? _busyProgressView;
        private UILabel? _busyProgressPercentLabel;
        private CancellationTokenSource? _busyPseudoProgressCts;

        private UIView? _pathTitleContainer;
        private UIButton? _pathTitleButton;
        private UIButton? _pathNavigateUpButton;
        private UIButton? _settingsGearButton;
        private UILongPressGestureRecognizer? _tableLongPressRecognizer;
        private UILongPressGestureRecognizer? _collectionLongPressRecognizer;

        private UIDocumentInteractionController? _documentInteractionController;
        private DocumentInteractionDelegate? _documentInteractionDelegate;
        private InAppVideoPlayerViewController? _videoPlayerController;
        private PickerDelegate? _pickerDelegate;
        private GalleryMultiPickerDelegate? _galleryMultiPickerDelegate;
        private string? _activePreviewTemporaryPath;
        private SharedVaultQueueStore? _sharedQueueStore;
        private string? _sharedQueueRootPath;
        private string? _currentVaultRecentId;
        private bool _pendingImportPromptVisible;
        private CancellationTokenSource? _pendingImportPromptCts;
        private bool _manualSaveModeEnabled;
        private NSUrl? _pendingIncomingVaultUrl;
        private string? _autoOpenVaultPath;

        private bool HasPendingVaultSaveChanges =>
            _session != null && (_session.IsDirty || _session.NeedsVaultIdUpgrade);

        private bool ShouldShowUnsavedChangesIndicator =>
            _session != null && _manualSaveModeEnabled && HasPendingVaultSaveChanges;

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

        private static string GetAppDocumentsDirectoryPath()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
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

        private void LoadItemSortPreference()
        {
            string? raw = NSUserDefaults.StandardUserDefaults.StringForKey(ItemSortPreferenceKey);
            ItemSortMode mode = raw switch
            {
                ItemSortNameDescendingValue => ItemSortMode.NameDescending,
                ItemSortLatestAddedValue => ItemSortMode.LatestAdded,
                _ => ItemSortMode.NameAscending
            };

            _itemSortMode = mode;
        }

        private void LoadAutoOpenVaultPreference()
        {
            _autoOpenVaultPath = NSUserDefaults.StandardUserDefaults.StringForKey(AutoOpenVaultPreferenceKey);
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
            TryPersistUnsavedDraftSnapshot();
        }

        public void OnAppWillTerminate()
        {
            TryPersistUnsavedDraftSnapshot();
            CleanupTemporaryRuntimeFiles();
            CloseCurrentVaultSession(reloadUi: false);
        }

        public override void ViewDidLoad()
        {
            base.ViewDidLoad();

            LoadPreviewPerformancePreference();
            LoadItemSortPreference();
            LoadAutoOpenVaultPreference();

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

            _createVaultCenteredButton = new UIButton(UIButtonType.System);
            _createVaultCenteredButton.SetTitle("Crea vault", UIControlState.Normal);
            _createVaultCenteredButton.SetTitleColor(UIColor.White, UIControlState.Normal);
            _createVaultCenteredButton.BackgroundColor = UIColor.FromRGB(52, 199, 89);
            _createVaultCenteredButton.TitleLabel!.Font = UIFont.SystemFontOfSize(18, UIFontWeight.Semibold);
            _createVaultCenteredButton.Layer.CornerRadius = 13f;
            _createVaultCenteredButton.TouchUpInside += (_, _) => PromptCreateVaultSettingsMenu();
            View.AddSubview(_createVaultCenteredButton);

            _extraButton = new UIButton(UIButtonType.System);
            _extraButton.SetTitle("Extra", UIControlState.Normal);
            _extraButton.SetTitleColor(UIColor.White, UIControlState.Normal);
            _extraButton.BackgroundColor = UIColor.FromRGB(255, 159, 10);
            _extraButton.TitleLabel!.Font = UIFont.SystemFontOfSize(18, UIFontWeight.Semibold);
            _extraButton.Layer.CornerRadius = 13f;
            _extraButton.TouchUpInside += (_, _) => ShowExtraMenu();
            View.AddSubview(_extraButton);

            _homeVersionLabel = new UILabel
            {
                TextAlignment = UITextAlignment.Center,
                Lines = 1,
                Font = UIFont.SystemFontOfSize(12f),
                TextColor = UIColor.FromRGB(120, 120, 120),
                Text = GetHomeVersionText(),
                Hidden = false
            };
            View.AddSubview(_homeVersionLabel);

            SetupBottomMenu();
            BuildBusyOverlay();
            ConfigureNavigationItems();
            ShareVaultRegistryBridge.RepublishAppManagedVaults();
            UpdateUiState();
        }

        public override void ViewDidAppear(bool animated)
        {
            base.ViewDidAppear(animated);

            if (_pendingIncomingVaultUrl != null)
            {
                NSUrl pendingUrl = _pendingIncomingVaultUrl;
                _pendingIncomingVaultUrl = null;
                PromptPasswordAndOpenVault(pendingUrl);
                return;
            }

            // Auto-open vault if configured and no session is active
            if (_session == null && !string.IsNullOrWhiteSpace(_autoOpenVaultPath) && File.Exists(_autoOpenVaultPath))
            {
                NSUrl autoOpenUrl = NSUrl.FromFilename(_autoOpenVaultPath);
                if (autoOpenUrl != null)
                {
                    PromptPasswordAndOpenVault(autoOpenUrl);
                    return;
                }
            }

            if (_session != null)
                SchedulePendingImportsPrompt(TimeSpan.FromMilliseconds(200));
        }

        public bool HandleIncomingVaultUrl(NSUrl vaultUrl)
        {
            if (vaultUrl == null)
                return false;

            BeginInvokeOnMainThread(() =>
            {
                if (IsViewLoaded)
                    PromptPasswordAndOpenVault(vaultUrl);
                else
                    _pendingIncomingVaultUrl = vaultUrl;
            });

            return true;
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

            if (_openVaultCenteredButton != null || _createVaultCenteredButton != null || _extraButton != null)
            {
                nfloat buttonWidth = view.Bounds.Width - (nfloat)40f;
                if (buttonWidth > 250f)
                    buttonWidth = 250f;
                nfloat buttonHeight = 54f;
                nfloat spacing = 12f;
                nfloat totalHeight = (buttonHeight * 3f) + (spacing * 2f);
                nfloat startY = (view.Bounds.Height - totalHeight) / 2f;

                if (_openVaultCenteredButton != null)
                {
                    _openVaultCenteredButton.Frame = new CGRect(
                        (view.Bounds.Width - buttonWidth) / 2f,
                        startY,
                        buttonWidth,
                        buttonHeight);
                }

                if (_createVaultCenteredButton != null)
                {
                    _createVaultCenteredButton.Frame = new CGRect(
                        (view.Bounds.Width - buttonWidth) / 2f,
                        startY + buttonHeight + spacing,
                        buttonWidth,
                        buttonHeight);
                }

                if (_extraButton != null)
                {
                    _extraButton.Frame = new CGRect(
                        (view.Bounds.Width - buttonWidth) / 2f,
                        startY + (buttonHeight * 2f) + (spacing * 2f),
                        buttonWidth,
                        buttonHeight);
                }
            }

            if (_homeVersionLabel != null)
            {
                nfloat labelWidth = view.Bounds.Width - 40f;
                if (labelWidth < 120f)
                    labelWidth = view.Bounds.Width;
                nfloat labelHeight = 18f;
                nfloat bottomPadding = 8f;
                nfloat y = view.Bounds.Height - view.SafeAreaInsets.Bottom - labelHeight - bottomPadding;
                _homeVersionLabel.Frame = new CGRect((view.Bounds.Width - labelWidth) / 2f, y, labelWidth, labelHeight);
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
            _extraTabItem = new UITabBarItem("Extra", UIImage.GetSystemImage("star.fill"), 4);
            _settingsTabItem = new UITabBarItem("Impostazioni", UIImage.GetSystemImage("gearshape"), 5);

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
                    _extraTabItem,
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

            if (ReferenceEquals(item, _extraTabItem))
            {
                OpenInstagramAnalysis();
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

            _settingsGearButton = UIButton.FromType(UIButtonType.System);
            _settingsGearButton.SetImage(UIImage.GetSystemImage("gearshape.fill"), UIControlState.Normal);
            _settingsGearButton.TintColor = UIColor.FromRGB(10, 132, 255);
            _settingsGearButton.TouchUpInside += (_, _) => OpenManageRecentVaultsMenu();
            _settingsGearButton.Hidden = false;

            NavigationItem.RightBarButtonItem = new UIBarButtonItem(_settingsGearButton);
        }

        private void UpdateUiState()
        {
            bool hasVault = _session != null;
            string titlePath = hasVault
                ? (string.IsNullOrWhiteSpace(_currentFolder) ? "/" : $"/{_currentFolder}")
                : "Cassaforte iOS";

            Title = titlePath;
            NavigationItem.Prompt = ShouldShowUnsavedChangesIndicator ? "Modifiche non salvate" : null;

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
            if (_createVaultCenteredButton != null)
                _createVaultCenteredButton.Hidden = hasVault;
            if (_extraButton != null)
                _extraButton.Hidden = hasVault;
            if (_homeVersionLabel != null)
                _homeVersionLabel.Hidden = hasVault;

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

                if (_settingsGearButton != null)
                {
                    UIBarButtonItem gearButton = new UIBarButtonItem(_settingsGearButton);
                    NavigationItem.RightBarButtonItems = new[] { gearButton };
                }
                else
                {
                    NavigationItem.RightBarButtonItems = null;
                }

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

            _ = PromptCloseVaultAsync();
        }

        private static string GetHomeVersionText()
        {
            // Use current date/time as fallback since bundle modification date may not be reliable
            return $"Ultima modifica: {DateTime.Now:dd/MM/yyyy HH:mm}";
        }

        private async Task PromptCloseVaultAsync()
        {
            if (_session == null)
                return;

            if (HasPendingVaultSaveChanges)
            {
                bool canClose = await ConfirmCanLeaveCurrentVaultAsync(
                    "Chiudi vault",
                    "Ci sono modifiche non salvate. Vuoi salvarle prima di chiudere il vault?",
                    "Salva e chiudi",
                    "Chiudi senza salvare");
                if (canClose)
                    CloseCurrentVaultSession(reloadUi: true);
                return;
            }

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
            CancelPendingImportPrompt();
            _session?.Dispose();
            _session = null;
            _vaultUrl = null;
            _sharedQueueStore = null;
            _sharedQueueRootPath = null;
            _sessionPassword = string.Empty;
            _currentFolder = string.Empty;
            _currentVaultRecentId = null;
            _pendingImportPromptVisible = false;
            _manualSaveModeEnabled = false;
            ClearPendingChangeSummary();
            _isSelectionMode = false;
            _selectedItemIds.Clear();
            _visibleItems.Clear();
            ClearThumbnailCache();
            ClearThumbnailDiskCache();

            if (reloadUi)
                ReloadFolderItems();
        }

        private void CancelPendingImportPrompt()
        {
            try
            {
                _pendingImportPromptCts?.Cancel();
            }
            catch
            {
                // Best effort cancellation.
            }
            finally
            {
                _pendingImportPromptCts?.Dispose();
                _pendingImportPromptCts = null;
            }
        }

        private void SchedulePendingImportsPrompt(TimeSpan? initialDelay = null)
        {
            if (_session == null)
                return;

            CancelPendingImportPrompt();

            var cts = new CancellationTokenSource();
            _pendingImportPromptCts = cts;
            TimeSpan delay = initialDelay ?? TimeSpan.FromMilliseconds(350);

            _ = Task.Run(async () =>
            {
                try
                {
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay, cts.Token);

                    for (int attempt = 0; attempt < 8; attempt++)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(150), cts.Token);

                        bool canPresent = false;
                        await RunOnMainThreadAsync(() =>
                        {
                            canPresent =
                                _session != null &&
                                ViewIfLoaded?.Window != null &&
                                PresentedViewController == null &&
                                !IsBeingDismissed;
                        });

                        if (!canPresent)
                            continue;

                        await RunOnMainThreadAsync(() =>
                        {
                            if (!cts.IsCancellationRequested)
                                PromptPendingImportsForCurrentVaultIfNeeded();
                        });
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    // Prompt rescheduled or no longer relevant.
                }
                finally
                {
                    await RunOnMainThreadAsync(() =>
                    {
                        if (ReferenceEquals(_pendingImportPromptCts, cts))
                        {
                            _pendingImportPromptCts.Dispose();
                            _pendingImportPromptCts = null;
                        }
                        else
                        {
                            cts.Dispose();
                        }
                    });
                }
            });
        }

        private Task RunOnMainThreadAsync(Action action)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            BeginInvokeOnMainThread(() =>
            {
                try
                {
                    action();
                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            return tcs.Task;
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
                    RecordPendingChange($"Rinominata la cartella in \"{newName}\".");
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
                    RecordPendingChange($"Eliminata la cartella \"{folder.FileName}\".");
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
                    $"Ordina file: {GetItemSortModeLabel(_itemSortMode)}",
                    UIAlertActionStyle.Default,
                    __ => OpenSortMenu()));
                sheet.AddAction(UIAlertAction.Create(
                    "Scegli ordinamento file di default",
                    UIAlertActionStyle.Default,
                    __ => OpenDefaultSortMenu()));
                sheet.AddAction(UIAlertAction.Create(
                    $"Anteprime: {GetPreviewPerformanceLabel(_previewPerformanceMode)}",
                    UIAlertActionStyle.Default,
                    __ => OpenPreviewPerformanceMenu()));
                sheet.AddAction(UIAlertAction.Create(
                    $"Formato vault: {GetStorageFormatLabel(_session.StorageFormat)}",
                    UIAlertActionStyle.Default,
                    __ => OpenStorageFormatMenu()));
                string protectionLabel = _session.RequiresPassword
                    ? "Protezione: attiva"
                    : "Protezione: veloce";
                sheet.AddAction(UIAlertAction.Create(
                    protectionLabel,
                    UIAlertActionStyle.Default,
                    __ => PromptProtectionSettings()));

                if (_manualSaveModeEnabled)
                {
                    string saveLabel = HasPendingVaultSaveChanges
                        ? "Salva modifiche"
                        : "Salva modifiche (nessuna in attesa)";
                    sheet.AddAction(UIAlertAction.Create(saveLabel, UIAlertActionStyle.Default, __ => _ = SaveVaultChangesNowAsync()));
                    sheet.AddAction(UIAlertAction.Create("Torna al salvataggio automatico", UIAlertActionStyle.Default, __ => _ = RestoreAutomaticSaveModeAsync()));
                }
                else
                {
                    sheet.AddAction(UIAlertAction.Create(
                        "Raggruppa modifiche e salva manualmente",
                        UIAlertActionStyle.Default,
                        __ => EnableManualSaveMode()));
                }

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

        private void OpenSortMenu()
        {
            UIAlertController sheet = UIAlertController.Create("Ordina file", null, UIAlertControllerStyle.ActionSheet);
            sheet.AddAction(UIAlertAction.Create("Nome A-Z", UIAlertActionStyle.Default, __ => ApplyItemSortMode(ItemSortMode.NameAscending, persist: false)));
            sheet.AddAction(UIAlertAction.Create("Nome Z-A", UIAlertActionStyle.Default, __ => ApplyItemSortMode(ItemSortMode.NameDescending, persist: false)));
            sheet.AddAction(UIAlertAction.Create("Ultima aggiunta", UIAlertActionStyle.Default, __ => ApplyItemSortMode(ItemSortMode.LatestAdded, persist: false)));
            sheet.AddAction(UIAlertAction.Create("Annulla", UIAlertActionStyle.Cancel, null));
            ConfigurePopover(sheet);
            PresentViewController(sheet, true, null);
        }

        private void OpenDefaultSortMenu()
        {
            UIAlertController sheet = UIAlertController.Create("Scegli ordinamento file di default", null, UIAlertControllerStyle.ActionSheet);
            sheet.AddAction(UIAlertAction.Create("Nome A-Z", UIAlertActionStyle.Default, __ => ApplyItemSortMode(ItemSortMode.NameAscending, persist: true)));
            sheet.AddAction(UIAlertAction.Create("Nome Z-A", UIAlertActionStyle.Default, __ => ApplyItemSortMode(ItemSortMode.NameDescending, persist: true)));
            sheet.AddAction(UIAlertAction.Create("Ultima aggiunta", UIAlertActionStyle.Default, __ => ApplyItemSortMode(ItemSortMode.LatestAdded, persist: true)));
            sheet.AddAction(UIAlertAction.Create("Annulla", UIAlertActionStyle.Cancel, null));
            ConfigurePopover(sheet);
            PresentViewController(sheet, true, null);
        }

        private void OpenInstagramAnalysis()
        {
            var instagramController = new InstagramAnalysisViewController();
            var navigationController = new UINavigationController(instagramController);
            navigationController.ModalPresentationStyle = UIModalPresentationStyle.FormSheet;
            PresentViewController(navigationController, true, null);
        }

        private void ApplyItemSortMode(ItemSortMode mode, bool persist = true)
        {
            if (_itemSortMode == mode)
                return;

            _itemSortMode = mode;

            if (persist)
            {
                // Save preference
                NSUserDefaults defaults = NSUserDefaults.StandardUserDefaults;
                string value = mode switch
                {
                    ItemSortMode.NameDescending => ItemSortNameDescendingValue,
                    ItemSortMode.LatestAdded => ItemSortLatestAddedValue,
                    _ => ItemSortNameAscendingValue
                };
                defaults.SetString(value, ItemSortPreferenceKey);
                defaults.Synchronize();
            }

            ReloadFolderItems();
        }

        private static string GetItemSortModeLabel(ItemSortMode mode) =>
            mode switch
            {
                ItemSortMode.NameDescending => "Nome Z-A",
                ItemSortMode.LatestAdded => "Ultima aggiunta",
                _ => "Nome A-Z"
            };

        private void EnableManualSaveMode()
        {
            if (_session == null || _manualSaveModeEnabled)
                return;

            _manualSaveModeEnabled = true;
            ShowSimpleAlert(
                "Salvataggio manuale attivo",
                "Da adesso le modifiche resteranno aperte finche non scegli \"Salva modifiche\" dal menu impostazioni.");
        }

        private async Task RestoreAutomaticSaveModeAsync()
        {
            if (_session == null || !_manualSaveModeEnabled)
                return;

            if (HasPendingVaultSaveChanges)
            {
                bool saved = await SaveVaultChangesInternalAsync("Salvataggio modifiche...");
                if (!saved)
                    return;
            }

            _manualSaveModeEnabled = false;
            ShowSimpleAlert(
                "Salvataggio automatico attivo",
                "Da ora le modifiche torneranno a essere salvate subito.");
        }

        private async Task SaveVaultChangesNowAsync()
        {
            if (_session == null)
                return;

            if (!HasPendingVaultSaveChanges)
            {
                ShowSimpleAlert("Nessuna modifica da salvare", "Il vault e gia aggiornato.");
                return;
            }

            await SaveVaultChangesInternalAsync("Salvataggio modifiche...");
        }

        private async Task<bool> SaveVaultChangesInternalAsync(string busyMessage)
        {
            if (!HasPendingVaultSaveChanges)
                return true;

            await RunBusyWithProgressAsync(busyMessage, async progress =>
            {
                await PersistVaultAsync(progress, force: true);
            });

            if (HasPendingVaultSaveChanges)
            {
                UpdateUiState();
                return false;
            }

            DeleteCurrentDraftIfPresent();
            ClearPendingChangeSummary();
            UpdateUiState();
            return true;
        }

        private async Task<bool> ConfirmCanLeaveCurrentVaultAsync(
            string title,
            string message,
            string saveActionTitle,
            string discardActionTitle,
            Func<Task<bool>>? discardActionAsync = null)
        {
            if (!HasPendingVaultSaveChanges)
                return true;

            var completion = new TaskCompletionSource<PendingChangesDecision>();
            UIAlertController alert = UIAlertController.Create(title, message, UIAlertControllerStyle.Alert);

            alert.AddAction(UIAlertAction.Create("Annulla", UIAlertActionStyle.Cancel, __ =>
            {
                completion.TrySetResult(PendingChangesDecision.Cancel);
            }));
            alert.AddAction(UIAlertAction.Create(discardActionTitle, UIAlertActionStyle.Destructive, __ =>
            {
                completion.TrySetResult(PendingChangesDecision.Discard);
            }));
            alert.AddAction(UIAlertAction.Create(saveActionTitle, UIAlertActionStyle.Default, __ =>
            {
                completion.TrySetResult(PendingChangesDecision.Save);
            }));

            PresentViewController(alert, true, null);

            PendingChangesDecision decision = await completion.Task;
            return decision switch
            {
                PendingChangesDecision.Save => await SaveVaultChangesInternalAsync("Salvataggio modifiche..."),
                PendingChangesDecision.Discard => discardActionAsync == null ? true : await discardActionAsync(),
                _ => false
            };
        }

        private void RecordPendingChange(string summary)
        {
            if (!_manualSaveModeEnabled || string.IsNullOrWhiteSpace(summary))
                return;

            string normalized = summary.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
                return;

            _pendingChangeSummary.Add(normalized);
            if (_pendingChangeSummary.Count > DraftSummaryEntryLimit)
                _pendingChangeSummary.RemoveRange(0, _pendingChangeSummary.Count - DraftSummaryEntryLimit);

            UpdateUiState();
        }

        private void ClearPendingChangeSummary()
        {
            _pendingChangeSummary.Clear();
        }

        private IReadOnlyList<string> GetDraftChangeSummarySnapshot()
        {
            if (_pendingChangeSummary.Count > 0)
                return _pendingChangeSummary.ToArray();

            if (HasPendingVaultSaveChanges)
                return new[] { "Modifiche al contenuto del vault non ancora salvate." };

            return Array.Empty<string>();
        }

        private VaultSessionDraftManifest CreateCurrentDraftManifest()
        {
            if (_session == null || _vaultUrl == null)
                throw new InvalidOperationException("Vault non aperto.");

            string displayName = _vaultUrl.LastPathComponent
                ?? Path.GetFileName(_vaultUrl.Path ?? string.Empty)
                ?? "Vault";
            IReadOnlyList<string> summary = GetDraftChangeSummarySnapshot();

            return new VaultSessionDraftManifest
            {
                VaultId = _session.VaultId,
                DisplayName = displayName,
                LastKnownPath = _vaultUrl.Path ?? string.Empty,
                SavedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ChangeCount = summary.Count,
                ChangeSummary = summary.ToList()
            };
        }

        private void TryPersistUnsavedDraftSnapshot()
        {
            if (!_manualSaveModeEnabled || !HasPendingVaultSaveChanges || _session == null || _vaultUrl == null)
                return;

            try
            {
                SharedVaultQueueStore? store = TryGetCurrentVaultQueueStore(showErrorIfUnavailable: false);
                if (store == null)
                    return;

                Directory.CreateDirectory(store.DraftRootPath);
                PersistVaultToUrl(NSUrl.FromFilename(store.DraftVaultFilePath), _session);
                store.SaveDraftManifest(CreateCurrentDraftManifest());
            }
            catch
            {
                // Best effort backup. The prompt on the next open is only shown when the draft was written correctly.
            }
        }

        private void DeleteCurrentDraftIfPresent()
        {
            try
            {
                SharedVaultQueueStore? store = TryGetCurrentVaultQueueStore(showErrorIfUnavailable: false);
                store?.DeleteDraft();
            }
            catch
            {
                // Best effort cleanup.
            }
        }

        private async Task PromptUnsavedDraftForCurrentVaultIfNeededAsync()
        {
            if (_session == null || _vaultUrl == null)
                return;

            SharedVaultQueueStore? store = TryGetCurrentVaultQueueStore(showErrorIfUnavailable: false);
            if (store == null)
                return;

            VaultSessionDraftManifest? draft = store.LoadDraftManifest();
            if (draft == null || !File.Exists(store.DraftVaultFilePath))
            {
                store.DeleteDraft();
                return;
            }

            if (!string.Equals(draft.VaultId, _session.VaultId, StringComparison.OrdinalIgnoreCase))
                return;

            string message = BuildDraftPromptMessage(draft);
            var completion = new TaskCompletionSource<PendingChangesDecision>();
            UIAlertController alert = UIAlertController.Create(
                "Modifiche recuperate",
                message,
                UIAlertControllerStyle.Alert);

            alert.AddAction(UIAlertAction.Create("Piu tardi", UIAlertActionStyle.Cancel, __ =>
            {
                completion.TrySetResult(PendingChangesDecision.Cancel);
            }));
            alert.AddAction(UIAlertAction.Create("Scarta", UIAlertActionStyle.Destructive, __ =>
            {
                completion.TrySetResult(PendingChangesDecision.Discard);
            }));
            alert.AddAction(UIAlertAction.Create("Salva", UIAlertActionStyle.Default, __ =>
            {
                completion.TrySetResult(PendingChangesDecision.Save);
            }));

            PresentViewController(alert, true, null);

            PendingChangesDecision decision = await completion.Task;
            if (decision == PendingChangesDecision.Save)
            {
                await ApplyDraftToCurrentVaultAsync(store);
                return;
            }

            if (decision == PendingChangesDecision.Discard)
                store.DeleteDraft();
        }

        private static string BuildDraftPromptMessage(VaultSessionDraftManifest draft)
        {
            var lines = new List<string>();
            if (draft.SavedAtUtc > 0)
            {
                DateTimeOffset savedAt = DateTimeOffset.FromUnixTimeSeconds(draft.SavedAtUtc).ToLocalTime();
                lines.Add($"Ho trovato una bozza salvata il {savedAt:dd/MM/yyyy HH:mm}.");
            }

            IReadOnlyList<string> summary = draft.ChangeSummary is { Count: > 0 }
                ? draft.ChangeSummary
                : Array.Empty<string>();
            if (summary.Count == 0)
                return string.Join("\n", lines.Append("Vuoi salvare o scartare le modifiche recuperate?"));

            lines.Add("Riepilogo modifiche:");
            foreach (string item in summary.Take(DraftPromptVisibleSummaryLimit))
                lines.Add($"- {item}");

            if (summary.Count > DraftPromptVisibleSummaryLimit)
                lines.Add($"+ altre {summary.Count - DraftPromptVisibleSummaryLimit} modifiche");

            lines.Add("Vuoi salvarle nel vault oppure scartarle?");
            return string.Join("\n", lines);
        }

        private async Task ApplyDraftToCurrentVaultAsync(SharedVaultQueueStore store)
        {
            if (_vaultUrl == null)
                return;

            string rollbackPassword = _sessionPassword;
            string rollbackFolder = _currentFolder;
            bool applied = false;
            await RunBusyWithProgressAsync("Salvataggio modifiche recuperate...", async progress =>
            {
                await Task.Run(() => ApplyDraftFileToVault(_vaultUrl, store.DraftVaultFilePath, progress));
                applied = true;
            });

            if (!applied)
                return;

            bool restored = false;
            await RunBusyAsync("Riapertura vault...", async () =>
            {
                await RestoreSessionFromDiskAsync(rollbackPassword, rollbackFolder);
                restored = true;
            });

            if (!restored)
                return;

            store.DeleteDraft();
            ClearPendingChangeSummary();
            _manualSaveModeEnabled = false;
            ReloadFolderItems();
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
                ShowError("Non riesco a trovare il vault selezionato.");
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
                    await PersistVaultAsync(progress, force: true);
                }
                catch
                {
                    await RestoreSessionFromDiskAsync(rollbackPassword, rollbackFolder);
                    throw;
                }

                ReloadFolderItems();
            });
        }

        private void PromptProtectionSettings()
        {
            if (_session == null)
                return;

            if (_session.RequiresPassword)
            {
                UIAlertController alert = UIAlertController.Create(
                    "Password del vault",
                    "Inserisci la password attuale. Se lasci vuoti i campi della nuova password, il vault passera alla modalita veloce.",
                    UIAlertControllerStyle.Alert);

                alert.AddTextField(field =>
                {
                    field.Placeholder = "Password attuale";
                    field.SecureTextEntry = true;
                });
                alert.AddTextField(field =>
                {
                    field.Placeholder = "Nuova password";
                    field.SecureTextEntry = true;
                });
                alert.AddTextField(field =>
                {
                    field.Placeholder = "Conferma nuova password";
                    field.SecureTextEntry = true;
                });

                alert.AddAction(UIAlertAction.Create("Annulla", UIAlertActionStyle.Cancel, null));
                alert.AddAction(UIAlertAction.Create("Applica", UIAlertActionStyle.Default, __ =>
                {
                    string currentPassword = alert.TextFields?.ElementAtOrDefault(0)?.Text ?? string.Empty;
                    string newPassword = alert.TextFields?.ElementAtOrDefault(1)?.Text ?? string.Empty;
                    string confirmPassword = alert.TextFields?.ElementAtOrDefault(2)?.Text ?? string.Empty;

                    if (!IsCurrentSessionPasswordValid(currentPassword))
                    {
                        ShowError("Password attuale non corretta.");
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(newPassword) && string.IsNullOrWhiteSpace(confirmPassword))
                    {
                        _ = DisablePasswordProtectionAsync(currentPassword);
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(newPassword))
                    {
                        ShowError("Inserisci una nuova password oppure lascia vuoti entrambi i campi.");
                        return;
                    }

                    if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
                    {
                        ShowError("Le password non coincidono.");
                        return;
                    }

                    _ = ChangePasswordAsync(currentPassword, newPassword);
                }));

                PresentViewController(alert, true, null);
                return;
            }

            UIAlertController fastAlert = UIAlertController.Create(
                "Attiva protezione",
                "Il vault e in modalita veloce. Inserisci una password per proteggerlo.",
                UIAlertControllerStyle.Alert);

            fastAlert.AddTextField(field =>
            {
                field.Placeholder = "Nuova password";
                field.SecureTextEntry = true;
            });
            fastAlert.AddTextField(field =>
            {
                field.Placeholder = "Conferma nuova password";
                field.SecureTextEntry = true;
            });

            fastAlert.AddAction(UIAlertAction.Create("Annulla", UIAlertActionStyle.Cancel, null));
            fastAlert.AddAction(UIAlertAction.Create("Proteggi", UIAlertActionStyle.Default, __ =>
            {
                string newPassword = fastAlert.TextFields?.ElementAtOrDefault(0)?.Text ?? string.Empty;
                string confirmPassword = fastAlert.TextFields?.ElementAtOrDefault(1)?.Text ?? string.Empty;

                if (string.IsNullOrWhiteSpace(newPassword))
                {
                    ShowError("Inserisci una password valida.");
                    return;
                }

                if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
                {
                    ShowError("Le password non coincidono.");
                    return;
                }

                _ = ChangePasswordAsync(string.Empty, newPassword);
            }));

            PresentViewController(fastAlert, true, null);
        }

        private bool IsCurrentSessionPasswordValid(string password)
        {
            if (_session == null || !_session.RequiresPassword)
                return true;

            return string.Equals(password ?? string.Empty, _sessionPassword, StringComparison.Ordinal);
        }

        private async Task ChangePasswordAsync(string currentPassword, string newPassword)
        {
            if (_session == null)
                return;
            if (_vaultUrl == null)
            {
                ShowError("Non riesco a trovare il vault selezionato.");
                return;
            }
            if (_session.RequiresPassword && !IsCurrentSessionPasswordValid(currentPassword))
            {
                ShowError("Password attuale non corretta.");
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
                    await PersistVaultAsync(progress, force: true);
                }
                catch
                {
                    await RestoreSessionFromDiskAsync(rollbackPassword, rollbackFolder);
                    throw;
                }

                _sessionPassword = newPassword;
            });
        }

        private async Task DisablePasswordProtectionAsync(string currentPassword)
        {
            if (_session == null)
                return;
            if (_vaultUrl == null)
            {
                ShowError("Non riesco a trovare il vault selezionato.");
                return;
            }
            if (!_session.RequiresPassword)
            {
                ShowSimpleAlert("Nessuna modifica", "Questo vault e gia in modalita veloce.");
                return;
            }
            if (!IsCurrentSessionPasswordValid(currentPassword))
            {
                ShowError("Password attuale non corretta.");
                return;
            }

            await RunBusyWithProgressAsync("Attivazione modalita veloce...", async progress =>
            {
                if (_session == null)
                    return;

                string rollbackPassword = _sessionPassword;
                string rollbackFolder = _currentFolder;

                _session.DisablePasswordProtection();
                try
                {
                    await PersistVaultAsync(progress, force: true);
                }
                catch
                {
                    await RestoreSessionFromDiskAsync(rollbackPassword, rollbackFolder);
                    throw;
                }

                _sessionPassword = string.Empty;
            });
        }

        private async Task RestoreSessionFromDiskAsync(string password, string folderPath)
        {
            if (_vaultUrl == null)
                throw new InvalidOperationException("Non riesco ad accedere al vault selezionato.");

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

        private SharedVaultQueueStore? TryGetCurrentVaultQueueStore(bool showErrorIfUnavailable)
        {
            if (_session == null || _vaultUrl == null)
            {
                if (showErrorIfUnavailable)
                    ShowError("Apri prima un vault.");
                return null;
            }

            try
            {
                string rootPath = EnsureCurrentVaultPendingImportFolder();
                if (string.IsNullOrWhiteSpace(rootPath))
                    throw new InvalidOperationException("Impossibile accedere al percorso di condivisione.");

                if (_sharedQueueStore != null &&
                    string.Equals(_sharedQueueRootPath, rootPath, StringComparison.OrdinalIgnoreCase))
                    return _sharedQueueStore;

                return CacheSharedQueueStore(rootPath);
            }
            catch (Exception ex)
            {
                if (showErrorIfUnavailable)
                    ShowError(ex.Message);
                return null;
            }
        }

        private string EnsureCurrentVaultPendingImportFolder()
        {
            if (_session == null || _vaultUrl == null)
                throw new InvalidOperationException("Vault non aperto.");

            string documentsRootPath = GetAppDocumentsDirectoryPath();
            string importsRootPath = VaultPendingImportLocator.GetAppImportsRootPath(documentsRootPath);
            Directory.CreateDirectory(importsRootPath);

            RecentVaultRecord? existing = FindExistingCurrentVaultRecord();
            string vaultId = _session.VaultId;
            string displayName = _vaultUrl.LastPathComponent
                ?? Path.GetFileName(_vaultUrl.Path ?? string.Empty)
                ?? "Vault";

            string targetRootPath = ResolvePendingImportRootPath(existing, importsRootPath, displayName, vaultId);
            Directory.CreateDirectory(targetRootPath);

            SharedVaultQueueStore store = new(targetRootPath);
            store.SaveVaultManifest(new VaultPendingImportManifest
            {
                VaultId = vaultId,
                DisplayName = displayName,
                LastKnownPath = _vaultUrl.Path ?? string.Empty
            });

            _sharedQueueStore = store;
            _sharedQueueRootPath = targetRootPath;
            return targetRootPath;
        }

        private RecentVaultRecord? FindExistingCurrentVaultRecord()
        {
            if (_session == null || _vaultUrl == null)
                return null;

            string currentVaultId = _session.VaultId;
            string currentVaultPath = VaultPendingImportLocator.NormalizePath(_vaultUrl.Path);

            return ShareVaultRegistryBridge.LoadPublishedVaultsMergedWithLocalVaults()
                .FirstOrDefault(vault =>
                    string.Equals(vault.VaultId, currentVaultId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(VaultPendingImportLocator.NormalizePath(vault.LastKnownPath), currentVaultPath, StringComparison.OrdinalIgnoreCase));
        }

        private SharedVaultQueueStore CacheSharedQueueStore(string rootPath)
        {
            _sharedQueueRootPath = rootPath;
            _sharedQueueStore = new SharedVaultQueueStore(rootPath);
            return _sharedQueueStore;
        }

        private string GetCurrentVaultQueueId()
        {
            if (!string.IsNullOrWhiteSpace(_currentVaultRecentId))
                return _currentVaultRecentId;

            return _session?.VaultId ?? string.Empty;
        }

        private SharedVaultQueueStore? TryResolveQueueStoreForCurrentVault(
            string currentVaultId,
            IEnumerable<string>? preferredJobIds,
            bool showErrorIfUnavailable)
        {
            if (string.IsNullOrWhiteSpace(currentVaultId))
                return null;

            SharedVaultQueueStore? currentStore = TryGetCurrentVaultQueueStore(showErrorIfUnavailable: false);
            if (StoreMatchesCurrentVault(currentStore, currentVaultId, preferredJobIds))
                return currentStore;

            foreach (string rootPath in EnumerateCandidateQueueRootPathsForCurrentVault())
            {
                if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
                    continue;

                SharedVaultQueueStore store;
                try
                {
                    store = new SharedVaultQueueStore(rootPath);
                }
                catch
                {
                    continue;
                }

                if (!StoreMatchesCurrentVault(store, currentVaultId, preferredJobIds))
                    continue;

                return CacheSharedQueueStore(rootPath);
            }

            return showErrorIfUnavailable ? TryGetCurrentVaultQueueStore(showErrorIfUnavailable: true) : null;
        }

        private IEnumerable<string> EnumerateCandidateQueueRootPathsForCurrentVault()
        {
            HashSet<string> emitted = new(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(_sharedQueueRootPath))
            {
                string normalizedCurrent = VaultPendingImportLocator.NormalizePath(_sharedQueueRootPath);
                if (!string.IsNullOrWhiteSpace(normalizedCurrent) && emitted.Add(normalizedCurrent))
                    yield return normalizedCurrent;
            }

            RecentVaultRecord? currentRecord = FindExistingCurrentVaultRecord();
            if (!string.IsNullOrWhiteSpace(currentRecord?.ImportFolderPath))
            {
                string normalizedRecordPath = VaultPendingImportLocator.NormalizePath(currentRecord.ImportFolderPath);
                if (!string.IsNullOrWhiteSpace(normalizedRecordPath) && emitted.Add(normalizedRecordPath))
                    yield return normalizedRecordPath;
            }

            if (_session != null && _vaultUrl != null)
            {
                string currentVaultId = _session.VaultId;
                string currentVaultPath = VaultPendingImportLocator.NormalizePath(_vaultUrl.Path);

                foreach (RecentVaultRecord record in ShareVaultRegistryBridge.LoadPublishedVaultsMergedWithLocalVaults())
                {
                    bool matchesVault = string.Equals(record.VaultId, currentVaultId, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(VaultPendingImportLocator.NormalizePath(record.LastKnownPath), currentVaultPath, StringComparison.OrdinalIgnoreCase);
                    if (!matchesVault || string.IsNullOrWhiteSpace(record.ImportFolderPath))
                        continue;

                    string normalizedRecordPath = VaultPendingImportLocator.NormalizePath(record.ImportFolderPath);
                    if (!string.IsNullOrWhiteSpace(normalizedRecordPath) && emitted.Add(normalizedRecordPath))
                        yield return normalizedRecordPath;
                }
            }
        }

        private static bool StoreMatchesCurrentVault(
            SharedVaultQueueStore? store,
            string currentVaultId,
            IEnumerable<string>? preferredJobIds)
        {
            if (store == null || string.IsNullOrWhiteSpace(currentVaultId))
                return false;

            if (preferredJobIds != null)
            {
                return store.LoadPendingJobs(preferredJobIds)
                    .Any(job => string.Equals(job.VaultId, currentVaultId, StringComparison.OrdinalIgnoreCase));
            }

            PendingImportAggregate? aggregate = store.GetPendingAggregateForVault(currentVaultId);
            return aggregate != null && aggregate.FileCount > 0;
        }

        private static string ResolvePendingImportRootPath(
            RecentVaultRecord? existingRecord,
            string importsRootPath,
            string displayName,
            string vaultId)
        {
            string expectedPath = VaultPendingImportLocator.GetVaultFolderPath(importsRootPath, displayName, vaultId);
            if (existingRecord == null || string.IsNullOrWhiteSpace(existingRecord.ImportFolderPath))
                return expectedPath;

            string existingPath = VaultPendingImportLocator.NormalizePath(existingRecord.ImportFolderPath);
            if (string.IsNullOrWhiteSpace(existingPath))
                return expectedPath;

            VaultPendingImportManifest? manifest = SharedVaultQueueStore.TryReadVaultManifest(existingPath);
            if (manifest == null || string.Equals(manifest.VaultId, vaultId, StringComparison.OrdinalIgnoreCase))
                return existingPath;

            return expectedPath;
        }

        private void RegisterCurrentVaultAsRecent()
        {
            if (_session == null || _vaultUrl == null)
                return;

            string? vaultPath = _vaultUrl.Path;
            RecentVaultRecord? existing = FindExistingCurrentVaultRecord();
            string displayName = _vaultUrl.LastPathComponent
                ?? Path.GetFileName(vaultPath ?? string.Empty)
                ?? "Vault";
            string importFolderPath = EnsureCurrentVaultPendingImportFolder();
            string? importFolderBookmark = TryCreateBookmarkDataBase64(NSUrl.FromFilename(importFolderPath));

            RecentVaultRecord record = new()
            {
                VaultId = _session.VaultId,
                DisplayName = displayName,
                LastKnownPath = vaultPath ?? string.Empty,
                BookmarkDataBase64 = TryCreateBookmarkDataBase64(_vaultUrl) ?? existing?.BookmarkDataBase64,
                ImportFolderPath = importFolderPath,
                ImportFolderBookmarkDataBase64 = importFolderBookmark ?? existing?.ImportFolderBookmarkDataBase64,
                StorageFormat = _session.StorageFormat.ToString(),
                LastOpenedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                IsPinned = existing?.IsPinned ?? false
            };

            ShareVaultRegistryBridge.UpsertAppManagedVault(record);
            _currentVaultRecentId = _session.VaultId;
        }

        private void OpenManageRecentVaultsMenu()
        {
            ShareVaultRegistryBridge.RepublishAppManagedVaults();
            IReadOnlyList<RecentVaultRecord> recentVaults = ShareVaultRegistryBridge.LoadAppManagedVaults();

            UIAlertController alert = UIAlertController.Create(
                "Vault visibili nel menu Condividi",
                recentVaults.Count > 0 ? $"{recentVaults.Count} vault disponibili" : "Nessun vault disponibile",
                UIAlertControllerStyle.Alert);

            // Add option to disable auto-open vault if one is set
            if (!string.IsNullOrWhiteSpace(_autoOpenVaultPath))
            {
                string autoOpenVaultName = Path.GetFileName(_autoOpenVaultPath);
                alert.AddAction(UIAlertAction.Create(
                    $"Disabilita apertura automatica: {autoOpenVaultName}",
                    UIAlertActionStyle.Default,
                    _ => ClearAutoOpenVault()));
            }

            if (recentVaults.Count > 0)
            {
                foreach (RecentVaultRecord vault in recentVaults)
                {
                    string displayName = vault.DisplayName ?? "Vault";
                    string vaultPath = vault.LastKnownPath ?? "(percorso sconosciuto)";
                    string actionTitle = $"{displayName}\n{vaultPath}";

                    alert.AddAction(UIAlertAction.Create(
                        actionTitle,
                        UIAlertActionStyle.Default,
                        _ => PromptVaultActions(vault)));
                }
            }

            alert.AddAction(UIAlertAction.Create("Chiudi", UIAlertActionStyle.Cancel, null));

            PresentViewController(alert, true, null);
        }

        private void ClearAutoOpenVault()
        {
            _autoOpenVaultPath = null;
            NSUserDefaults defaults = NSUserDefaults.StandardUserDefaults;
            defaults.RemoveObject(AutoOpenVaultPreferenceKey);
            defaults.Synchronize();
            ShowSimpleAlert("Apertura automatica disabilitata", "Il vault non verrà più aperto automaticamente all'avvio dell'app.");
        }

        private void PromptVaultActions(RecentVaultRecord vault)
        {
            UIAlertController actionAlert = UIAlertController.Create(
                vault.DisplayName ?? "Vault",
                null,
                UIAlertControllerStyle.ActionSheet);

            actionAlert.AddAction(UIAlertAction.Create(
                "Non mostrare nel menu Condividi",
                UIAlertActionStyle.Destructive,
                _ => RemoveVaultFromRecents(vault)));

            actionAlert.AddAction(UIAlertAction.Create(
                "Annulla",
                UIAlertActionStyle.Cancel,
                null));

            PresentViewController(actionAlert, true, null);
        }

        private void RemoveVaultFromRecents(RecentVaultRecord vault)
        {
            try
            {
                ShareVaultRegistryBridge.RemoveAppManagedVault(vault.VaultId);
                ShowSimpleAlert("Aggiornato", $"{vault.DisplayName} non verra piu mostrato nel menu Condividi.");
            }
            catch (Exception ex)
            {
                ShowSimpleAlert("Errore", $"Impossibile rimuovere il vault: {ex.Message}");
            }
        }

        private static string? TryCreateBookmarkDataBase64(NSUrl fileUrl)
        {
            try
            {
                NSError? bookmarkError;
                // iOS non supporta le opzioni security-scoped del binding .NET usate su macOS.
                NSData bookmarkData = fileUrl.CreateBookmarkData(
                    0,
                    null,
                    null,
                    out bookmarkError);

                if (bookmarkError != null || bookmarkData == null || bookmarkData.Length <= 0)
                    return null;

                return Convert.ToBase64String(bookmarkData.ToArray());
            }
            catch
            {
                return null;
            }
        }

        private void ShowSimpleAlert(string title, string message)
        {
            UIAlertController alert = UIAlertController.Create(title, message, UIAlertControllerStyle.Alert);
            alert.AddAction(UIAlertAction.Create("OK", UIAlertActionStyle.Default, null));
            PresentViewController(alert, true, null);
        }

        private void PromptPendingImportsForCurrentVaultIfNeeded()
        {
            if (_session == null || _vaultUrl == null || _pendingImportPromptVisible)
                return;

            string currentVaultRecentId = GetCurrentVaultQueueId();
            if (string.IsNullOrWhiteSpace(currentVaultRecentId))
                return;

            SharedVaultQueueStore? store = TryResolveQueueStoreForCurrentVault(
                currentVaultRecentId,
                preferredJobIds: null,
                showErrorIfUnavailable: false);
            if (store == null)
                return;

            PendingImportAggregate? aggregate = store.GetPendingAggregateForVault(currentVaultRecentId);
            if (aggregate == null || aggregate.FileCount <= 0)
                return;

            _pendingImportPromptVisible = true;
            string message = aggregate.JobCount <= 1
                ? $"C'e {aggregate.FileCount} file in attesa per questo vault."
                : $"Ci sono {aggregate.FileCount} file in attesa per questo vault in {aggregate.JobCount} invii.";

            UIAlertController alert = UIAlertController.Create(
                "File in attesa",
                message,
                UIAlertControllerStyle.Alert);

            alert.AddAction(UIAlertAction.Create("Piu tardi", UIAlertActionStyle.Cancel, __ =>
            {
                _pendingImportPromptVisible = false;
            }));
            alert.AddAction(UIAlertAction.Create("Scarta", UIAlertActionStyle.Destructive, __ =>
            {
                _pendingImportPromptVisible = false;
                _ = DiscardPendingImportsAsync(aggregate.JobIds);
            }));
            alert.AddAction(UIAlertAction.Create("Scegli cartella", UIAlertActionStyle.Default, __ =>
            {
                _pendingImportPromptVisible = false;
                PresentPendingImportDestinationPage(aggregate);
            }));

            PresentViewController(alert, true, null);
        }

        private void PresentPendingImportDestinationPage(PendingImportAggregate aggregate)
        {
            if (_session == null || NavigationController == null || aggregate == null || aggregate.JobIds.Length == 0)
                return;

            var page = new PendingImportDestinationViewController(
                _session,
                _currentFolder,
                aggregate.FileCount,
                destinationPath => _ = ImportPendingJobsToDestinationAsync(aggregate.JobIds, destinationPath));

            NavigationController.PushViewController(page, true);
        }

        private async Task DiscardPendingImportsAsync(string[] jobIds)
        {
            if (jobIds == null || jobIds.Length == 0)
                return;

            string currentVaultRecentId = GetCurrentVaultQueueId();
            SharedVaultQueueStore? store = TryResolveQueueStoreForCurrentVault(
                currentVaultRecentId,
                preferredJobIds: jobIds,
                showErrorIfUnavailable: true);
            if (store == null)
                return;

            await RunBusyAsync("Eliminazione file in attesa...", async () =>
            {
                await Task.Run(() =>
                {
                    try
                    {
                        foreach (string jobId in jobIds)
                            store.UpdatePendingJobStatus(jobId, PendingImportStatus.Discarded);
                    }
                    catch
                    {
                        // Best effort status update.
                    }

                    store.DeleteJobs(jobIds);
                });
            });
        }

        private async Task ImportPendingJobsToDestinationAsync(string[] jobIds, string destinationPath)
        {
            if (_session == null || jobIds == null || jobIds.Length == 0)
                return;

            string currentVaultRecentId = GetCurrentVaultQueueId();
            if (string.IsNullOrWhiteSpace(currentVaultRecentId))
                return;

            SharedVaultQueueStore? store = TryResolveQueueStoreForCurrentVault(
                currentVaultRecentId,
                preferredJobIds: jobIds,
                showErrorIfUnavailable: true);
            if (store == null)
                return;
            IReadOnlyList<PendingImportJob> jobs = store.LoadPendingJobs(jobIds)
                .Where(job =>
                    job.Status == PendingImportStatus.Pending &&
                    string.Equals(job.VaultId, currentVaultRecentId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(job => job.CreatedAtUtc)
                .ToArray();

            if (jobs.Count == 0)
            {
                ShowError("Nessun file in attesa da importare.");
                return;
            }

            string rollbackPassword = _sessionPassword;
            string rollbackFolder = _currentFolder;
            string normalizedDestination = NormalizeFolderPath(destinationPath);

            await RunBusyWithProgressAsync("Importazione file in attesa...", async progress =>
            {
                if (_session == null)
                    return;

                int totalItems = jobs.Sum(job => job.Items?.Count ?? 0);
                if (totalItems <= 0)
                    throw new InvalidOperationException("Nessun file disponibile nel job selezionato.");

                try
                {
                    foreach (PendingImportJob job in jobs)
                        store.UpdatePendingJobStatus(job.JobId, PendingImportStatus.Importing);

                    ReportProgress(progress, 6d);
                    EnsureDestinationFolderExistsForMove(normalizedDestination);

                    int processedItems = 0;
                    foreach (PendingImportJob job in jobs)
                    {
                        foreach (PendingImportItem item in job.Items)
                        {
                            string stagedPath = store.ResolveStagedFilePath(job, item);
                            if (!File.Exists(stagedPath))
                                throw new FileNotFoundException("File in attesa non trovato.", stagedPath);

                            await Task.Run(() => _session.AddFileFromPath(stagedPath, normalizedDestination));
                            processedItems++;
                            double itemProgress = 10d + (processedItems / (double)totalItems) * 58d;
                            ReportProgress(progress, itemProgress);
                        }
                    }

                    await PersistVaultAsync(CreateScaledProgress(progress, 70d, 100d), force: true);
                    EnsureCurrentFolderStillExists();
                    ReloadFolderItems();
                }
                catch (Exception ex)
                {
                    foreach (PendingImportJob job in jobs)
                        store.UpdatePendingJobStatus(job.JobId, PendingImportStatus.Pending, ex.Message);

                    await RestoreSessionFromDiskAsync(rollbackPassword, rollbackFolder);
                    throw;
                }
            });

            try
            {
                await Task.Run(() =>
                {
                    foreach (PendingImportJob job in jobs)
                        store.UpdatePendingJobStatus(job.JobId, PendingImportStatus.Completed);

                    store.DeleteJobs(jobs.Select(job => job.JobId));
                });
            }
            catch
            {
                // Best effort cleanup. Jobs already completed and won't be prompted again.
            }
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
                RecordPendingChange("Aggiornata la struttura cartelle.");
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

            HandleItemLongPress(indexPath.Row);
        }

        private void HandleCollectionLongPress(UILongPressGestureRecognizer gesture)
        {
            if (gesture.State != UIGestureRecognizerState.Began || _collectionView == null)
                return;

            CGPoint point = gesture.LocationInView(_collectionView);
            NSIndexPath? indexPath = _collectionView.IndexPathForItemAtPoint(point);
            if (indexPath == null || indexPath.Row < 0 || indexPath.Row >= _visibleItems.Count)
                return;

            HandleItemLongPress(indexPath.Row);
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
            await RunBusyWithProgressAsync("Spostamento elementi...", async progress =>
            {
                if (_session == null)
                    return;

                var createdFolderIds = new List<Guid>();
                try
                {
                    ReportProgress(progress, 6d);
                    EnsureDestinationFolderExistsForMove(normalizedDestination, createdFolderIds);
                    _session.MoveItems(selectedIds, normalizedDestination);
                    RecordPendingChange(selectedIds.Length == 1
                        ? "Spostato 1 elemento."
                        : $"Spostati {selectedIds.Length} elementi.");
                    ReportProgress(progress, 18d);
                    await PersistVaultAsync(CreateScaledProgress(progress, 20d, 100d));
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
                    RecordPendingChange(selectedIds.Length == 1
                        ? "Eliminato 1 elemento."
                        : $"Eliminati {selectedIds.Length} elementi.");
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

        private static string NormalizeFolderName(string name)
        {
            string trimmed = name?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmed))
                return string.Empty;
            if (trimmed.Contains('/') || trimmed.Contains('\\'))
                return string.Empty;
            if (string.Equals(trimmed, ".", StringComparison.Ordinal) ||
                string.Equals(trimmed, "..", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            return trimmed;
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
            if (!await ConfirmCanLeaveCurrentVaultAsync(
                    "Apri un altro vault",
                    "Ci sono modifiche non salvate nel vault aperto. Vuoi salvarle prima di aprirne un altro?",
                    "Salva e continua",
                    "Continua senza salvare",
                    DiscardPendingChangesForCurrentVaultAsync))
            {
                return;
            }

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

        private async Task<bool> DiscardPendingChangesForCurrentVaultAsync()
        {
            if (!HasPendingVaultSaveChanges)
                return true;
            if (_vaultUrl == null)
            {
                ShowError("Non riesco a trovare il vault selezionato.");
                return false;
            }

            string rollbackPassword = _sessionPassword;
            string rollbackFolder = _currentFolder;
            bool restored = false;
            await RunBusyAsync("Ripristino vault...", async () =>
            {
                await RestoreSessionFromDiskAsync(rollbackPassword, rollbackFolder);
                restored = true;
            });

            if (!restored)
            {
                UpdateUiState();
                return false;
            }

            DeleteCurrentDraftIfPresent();
            ClearPendingChangeSummary();
            UpdateUiState();
            return !HasPendingVaultSaveChanges;
        }

        private void ShowExtraMenu()
        {
            UIAlertController sheet = UIAlertController.Create(
                "Extra",
                "Funzionalita aggiuntive",
                UIAlertControllerStyle.ActionSheet);

            sheet.AddAction(UIAlertAction.Create(
                "Analisi Instagram",
                UIAlertActionStyle.Default,
                __ => ShowInstagramAnalysis()));

            sheet.AddAction(UIAlertAction.Create("Annulla", UIAlertActionStyle.Cancel, null));
            ConfigurePopover(sheet);
            PresentViewController(sheet, true, null);
        }

        private void ShowInstagramAnalysis()
        {
            var instagramVC = new InstagramAnalysisViewController();
            var navController = new UINavigationController(instagramVC);
            PresentViewController(navController, true, null);
        }

        private void PromptCreateVaultSettingsMenu()
        {
            UIAlertController sheet = UIAlertController.Create(
                "Nuovo vault",
                "Scegli formato e modalita di protezione",
                UIAlertControllerStyle.ActionSheet);

            VaultStorageFormat[] formats =
            {
                VaultStorageFormat.Extended,
                VaultStorageFormat.Ultra,
                VaultStorageFormat.Legacy
            };

            foreach (VaultStorageFormat format in formats)
            {
                sheet.AddAction(UIAlertAction.Create(
                    $"Formato {GetStorageFormatLabel(format)}",
                    UIAlertActionStyle.Default,
                    __ => PromptCreateVaultProtectionModeMenu(format)));
            }

            sheet.AddAction(UIAlertAction.Create("Annulla", UIAlertActionStyle.Cancel, null));
            ConfigurePopover(sheet);
            PresentViewController(sheet, true, null);
        }

        private void PromptCreateVaultProtectionModeMenu(VaultStorageFormat format)
        {
            UIAlertController sheet = UIAlertController.Create(
                "Modalita di protezione",
                "Seleziona se il nuovo vault deve avere una password.",
                UIAlertControllerStyle.ActionSheet);

            sheet.AddAction(UIAlertAction.Create("Proteggi con password", UIAlertActionStyle.Default, __ =>
            {
                PromptCreateVaultDetails(format, passwordProtected: true);
            }));

            sheet.AddAction(UIAlertAction.Create("Modalita veloce", UIAlertActionStyle.Default, __ =>
            {
                PromptCreateVaultDetails(format, passwordProtected: false);
            }));

            sheet.AddAction(UIAlertAction.Create("Annulla", UIAlertActionStyle.Cancel, null));
            ConfigurePopover(sheet);
            PresentViewController(sheet, true, null);
        }

        private void PromptCreateVaultDetails(VaultStorageFormat format, bool passwordProtected)
        {
            string protectionLabel = passwordProtected
                ? "Protezione: attiva"
                : "Protezione: veloce";

            UIAlertController alert = UIAlertController.Create(
                "Crea vault",
                $"Formato: {GetStorageFormatLabel(format)}\n{protectionLabel}",
                UIAlertControllerStyle.Alert);

            alert.AddTextField(field =>
            {
                field.Placeholder = "Nome vault";
                field.Text = "vault_ios";
                field.ClearButtonMode = UITextFieldViewMode.WhileEditing;
                field.AutocorrectionType = UITextAutocorrectionType.No;
                field.SpellCheckingType = UITextSpellCheckingType.No;
                field.AutocapitalizationType = UITextAutocapitalizationType.None;
                field.TextContentType = UITextContentType.OneTimeCode;
            });

            if (passwordProtected)
            {
                alert.AddTextField(field =>
                {
                    field.Placeholder = "Password";
                    field.SecureTextEntry = true;
                    field.AutocorrectionType = UITextAutocorrectionType.No;
                    field.SpellCheckingType = UITextSpellCheckingType.No;
                    field.TextContentType = UITextContentType.OneTimeCode;
                });
                alert.AddTextField(field =>
                {
                    field.Placeholder = "Conferma password";
                    field.SecureTextEntry = true;
                    field.AutocorrectionType = UITextAutocorrectionType.No;
                    field.SpellCheckingType = UITextSpellCheckingType.No;
                    field.TextContentType = UITextContentType.OneTimeCode;
                });
            }

            alert.AddAction(UIAlertAction.Create("Annulla", UIAlertActionStyle.Cancel, null));
            alert.AddAction(UIAlertAction.Create("Crea", UIAlertActionStyle.Default, __ =>
            {
                string requestedName = alert.TextFields?.ElementAtOrDefault(0)?.Text ?? string.Empty;
                string password = passwordProtected
                    ? (alert.TextFields?.ElementAtOrDefault(1)?.Text ?? string.Empty)
                    : string.Empty;
                string confirm = passwordProtected
                    ? (alert.TextFields?.ElementAtOrDefault(2)?.Text ?? string.Empty)
                    : string.Empty;

                if (passwordProtected && string.IsNullOrWhiteSpace(password))
                {
                    ShowError("Inserisci una password valida.");
                    return;
                }

                if (passwordProtected && !string.Equals(password, confirm, StringComparison.Ordinal))
                {
                    ShowError("Le password non coincidono.");
                    return;
                }

                _ = CreateVaultFromIosAsync(
                    requestedName,
                    passwordProtected ? password : string.Empty,
                    format,
                    passwordProtected);
            }));

            PresentViewController(alert, true, null);
        }

        private async Task CreateVaultFromIosAsync(
            string requestedName,
            string password,
            VaultStorageFormat format,
            bool passwordProtected)
        {
            string tempVaultPath = BuildCreateVaultTempPath(requestedName);
            await RunBusyWithProgressAsync("Creazione vault...", async progress =>
            {
                await Task.Run(() =>
                {
                    var manager = new VaultManager();
                    manager.CreateVault(tempVaultPath, password, format, passwordProtected, progress);
                });
            });

            if (!File.Exists(tempVaultPath))
            {
                ShowError("File vault creato non trovato.");
                return;
            }

            BeginInvokeOnMainThread(() => PromptVaultSaveDestination(tempVaultPath, requestedName, password));
        }

        private void PromptVaultSaveDestination(string tempVaultPath, string requestedName, string password)
        {
            UIAlertController sheet = UIAlertController.Create(
                "Dove salvare il vault?",
                "Scegli la posizione di salvataggio",
                UIAlertControllerStyle.ActionSheet);

            sheet.AddAction(UIAlertAction.Create("Scegli in File/iCloud", UIAlertActionStyle.Default, _ =>
            {
                PresentVaultExportPicker(tempVaultPath, password);
            }));

            sheet.AddAction(UIAlertAction.Create("Dentro l'app (Documents/Vaults)", UIAlertActionStyle.Default, _ =>
            {
                SaveVaultInsideAppAndOpen(tempVaultPath, requestedName, password);
            }));

            sheet.AddAction(UIAlertAction.Create("Annulla", UIAlertActionStyle.Cancel, _ =>
            {
                TryDeletePath(tempVaultPath);
            }));

            ConfigurePopover(sheet);
            PresentViewController(sheet, true, null);
        }

        private void SaveVaultInsideAppAndOpen(string tempVaultPath, string requestedName, string password)
        {
            string finalPath;
            try
            {
                finalPath = BuildNewVaultPath(requestedName);
                File.Move(tempVaultPath, finalPath);
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
                TryDeletePath(tempVaultPath);
                return;
            }

            _ = OpenVaultAsync(NSUrl.FromFilename(finalPath), password);
        }

        private void PresentVaultExportPicker(string tempVaultPath, string password)
        {
            NSUrl sourceUrl = NSUrl.FromFilename(tempVaultPath);
#pragma warning disable CA1422
            var picker = new UIDocumentPickerViewController(new[] { sourceUrl }, UIDocumentPickerMode.ExportToService);
#pragma warning restore CA1422

            _pickerDelegate = new PickerDelegate(
                onPicked: urls =>
                {
                    NSUrl? exportedUrl = urls?.FirstOrDefault();
                    TryDeletePath(tempVaultPath);
                    if (exportedUrl == null)
                    {
                        ShowError("Nessuna destinazione selezionata.");
                        return;
                    }

                    _ = OpenVaultAsync(exportedUrl, password);
                },
                onCancelled: () =>
                {
                    TryDeletePath(tempVaultPath);
                });

            picker.Delegate = _pickerDelegate;
            PresentViewController(picker, true, null);
        }

        private static string BuildCreateVaultTempPath(string requestedName)
        {
            string runtimeRoot = GetRuntimeTempDirectoryPath();
            Directory.CreateDirectory(runtimeRoot);

            string fileName = NormalizeVaultFileName(requestedName);
            string candidatePath = Path.Combine(runtimeRoot, fileName);
            if (!File.Exists(candidatePath))
                return candidatePath;

            string baseName = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            return Path.Combine(runtimeRoot, $"{baseName}_{Guid.NewGuid():N}{extension}");
        }

        private static string BuildNewVaultPath(string requestedName)
        {
            string documentsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string vaultsDirectory = Path.Combine(documentsDirectory, "Vaults");
            Directory.CreateDirectory(vaultsDirectory);

            string fileName = NormalizeVaultFileName(requestedName);
            string candidatePath = Path.Combine(vaultsDirectory, fileName);
            if (!File.Exists(candidatePath))
                return candidatePath;

            string baseName = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            int index = 2;

            while (true)
            {
                string indexedName = $"{baseName}_{index}{extension}";
                candidatePath = Path.Combine(vaultsDirectory, indexedName);
                if (!File.Exists(candidatePath))
                    return candidatePath;

                index++;
            }
        }

        private static string NormalizeVaultFileName(string requestedName)
        {
            string name = (requestedName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
                name = "vault_ios";

            foreach (char invalid in Path.GetInvalidFileNameChars())
                name = name.Replace(invalid, '_');

            name = name.Replace('/', '_').Replace('\\', '_');
            if (name.EndsWith(".vault", StringComparison.OrdinalIgnoreCase))
                return name;

            return $"{name}.vault";
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

            sheet.AddAction(UIAlertAction.Create("Cartella (qui)", UIAlertActionStyle.Default, __ =>
            {
                PromptCreateFolderInCurrentPath();
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

        private void PromptCreateFolderInCurrentPath()
        {
            if (_session == null)
            {
                ShowError("Apri prima un vault.");
                return;
            }

            UIAlertController alert = UIAlertController.Create("Nuova cartella", null, UIAlertControllerStyle.Alert);
            alert.AddTextField(field =>
            {
                field.Placeholder = "Nome cartella";
                field.ClearButtonMode = UITextFieldViewMode.WhileEditing;
                field.AutocorrectionType = UITextAutocorrectionType.No;
                field.SpellCheckingType = UITextSpellCheckingType.No;
                field.AutocapitalizationType = UITextAutocapitalizationType.None;
                field.TextContentType = UITextContentType.OneTimeCode;
            });

            alert.AddAction(UIAlertAction.Create("Annulla", UIAlertActionStyle.Cancel, null));
            alert.AddAction(UIAlertAction.Create("Crea", UIAlertActionStyle.Default, __ =>
            {
                string rawName = alert.TextFields?.FirstOrDefault()?.Text ?? string.Empty;
                string name = NormalizeFolderName(rawName);
                if (string.IsNullOrWhiteSpace(name))
                {
                    ShowError("Inserisci un nome cartella valido.");
                    return;
                }

                _ = RunBusyWithProgressAsync("Creazione cartella...", async progress =>
                {
                    if (_session == null)
                        return;

                    Guid createdId = Guid.Empty;
                    try
                    {
                        VaultFileItem created = _session.CreateFolder(name, _currentFolder);
                        createdId = created.Id;
                        RecordPendingChange($"Creata la cartella \"{name}\".");
                        ReportProgress(progress, 15d);
                        await PersistVaultAsync(CreateScaledProgress(progress, 18d, 100d));
                        ReloadFolderItems();
                    }
                    catch
                    {
                        if (_session != null && createdId != Guid.Empty)
                        {
                            try
                            {
                                _session.DeleteItems(new[] { createdId });
                            }
                            catch
                            {
                                // Best effort rollback.
                            }
                        }

                        throw;
                    }
                });
            }));

            PresentViewController(alert, true, null);
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
            VaultFileFormat.Header header;
            try
            {
                header = ReadVaultHeader(vaultUrl);
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
                return;
            }

            string vaultPath = vaultUrl.Path ?? string.Empty;

            if (!header.RequiresPassword)
            {
                PromptAutoOpenVault(vaultUrl, vaultPath, string.Empty, header);
                return;
            }

            UIAlertController prompt = UIAlertController.Create(
                "Apri vault",
                vaultUrl.LastPathComponent ?? "File vault",
                UIAlertControllerStyle.Alert);

            prompt.AddTextField(field =>
            {
                field.Placeholder = "Password";
                field.SecureTextEntry = true;
                field.ReturnKeyType = UIReturnKeyType.Done;
                field.AutocorrectionType = UITextAutocorrectionType.No;
                field.SpellCheckingType = UITextSpellCheckingType.No;
                field.TextContentType = UITextContentType.OneTimeCode;
            });

            prompt.AddAction(UIAlertAction.Create("Annulla", UIAlertActionStyle.Cancel, null));
            prompt.AddAction(UIAlertAction.Create("Apri", UIAlertActionStyle.Default, __ =>
            {
                string password = prompt.TextFields?.FirstOrDefault()?.Text ?? string.Empty;
                PromptAutoOpenVault(vaultUrl, vaultPath, password, header);
            }));

            PresentViewController(prompt, true, null);
        }

        private void PromptAutoOpenVault(NSUrl vaultUrl, string vaultPath, string password, VaultFileFormat.Header? knownHeader = null)
        {
            // Check if this is already the auto-open vault
            if (string.Equals(vaultPath, _autoOpenVaultPath, StringComparison.OrdinalIgnoreCase))
            {
                _ = OpenVaultAsync(vaultUrl, password, knownHeader);
                return;
            }

            UIAlertController alert = UIAlertController.Create(
                "Apertura automatica",
                $"Vuoi aprire sempre questo vault ({Path.GetFileName(vaultPath)}) all'avvio dell'app?",
                UIAlertControllerStyle.Alert);

            alert.AddAction(UIAlertAction.Create("No", UIAlertActionStyle.Default, __ =>
            {
                _ = OpenVaultAsync(vaultUrl, password, knownHeader);
            }));

            alert.AddAction(UIAlertAction.Create("Sì", UIAlertActionStyle.Default, __ =>
            {
                // Save the auto-open vault preference
                _autoOpenVaultPath = vaultPath;
                NSUserDefaults defaults = NSUserDefaults.StandardUserDefaults;
                defaults.SetString(vaultPath, AutoOpenVaultPreferenceKey);
                defaults.Synchronize();

                _ = OpenVaultAsync(vaultUrl, password, knownHeader);
            }));

            PresentViewController(alert, true, null);
        }

        private async Task OpenVaultAsync(NSUrl vaultUrl, string password, VaultFileFormat.Header? knownHeader = null)
        {
            VaultFileFormat.Header header;
            try
            {
                header = knownHeader ?? await Task.Run(() => ReadVaultHeader(vaultUrl));
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
                return;
            }

            if (header.RequiresPassword && string.IsNullOrWhiteSpace(password))
            {
                ShowError("Inserisci la password.");
                return;
            }

            bool opened = false;
            await RunBusyWithProgressAsync("Apertura vault...", async progress =>
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                VaultPortableReader reader = await Task.Run(() => OpenVaultReader(vaultUrl, password, progress));

                _session?.Dispose();
                _session = reader;
                _vaultUrl = vaultUrl;
                _sharedQueueStore = null;
                _sharedQueueRootPath = null;
                _sessionPassword = header.RequiresPassword ? password : string.Empty;
                _currentFolder = string.Empty;
                _manualSaveModeEnabled = false;
                ClearPendingChangeSummary();
                _isSelectionMode = false;
                _selectedItemIds.Clear();
                ClearThumbnailCache();
                ClearThumbnailDiskCache();

                ReloadFolderItems();
                opened = true;
            });

            if (!opened || _session == null || _vaultUrl == null)
                return;

            if (_session.NeedsVaultIdUpgrade)
            {
                ShowSimpleAlert(
                    "Aggiornamento vault",
                    "Questo vault non aveva ancora un identificatore interno. Lo aggiorno ora per renderlo compatibile con la condivisione.");
                await RunBusyWithProgressAsync("Aggiornamento vault...", async progress =>
                {
                    await PersistVaultAsync(progress, force: true);
                });

                if (_session.NeedsVaultIdUpgrade)
                    return;
            }

            RegisterCurrentVaultAsRecent();
            await PromptUnsavedDraftForCurrentVaultIfNeededAsync();
            SchedulePendingImportsPrompt();
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

                RecordPendingChange(urls.Length == 1
                    ? "Aggiunto 1 file."
                    : $"Aggiunti {urls.Length} file.");
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
                    string importFileName = ResolvePickerResultFileName(result, tempPath);
                    try
                    {
                        await Task.Run(() =>
                        {
                            using var stream = new FileStream(
                                tempPath,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.Read,
                                VaultPersistCopyBufferSize,
                                FileOptions.SequentialScan);
                            session.AddFileFromStream(importFileName, stream, stream.Length, _currentFolder);
                        });
                    }
                    finally
                    {
                        TryDeletePath(tempPath);
                    }
                }

                RecordPendingChange(results.Length == 1
                    ? "Aggiunto 1 elemento dalla libreria foto."
                    : $"Aggiunti {results.Length} elementi dalla libreria foto.");
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
                SortVisibleItems();
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

        private void SortVisibleItems()
        {
            if (_visibleItems.Count <= 1)
                return;

            IEnumerable<VaultFileItem> ordered = _itemSortMode switch
            {
                ItemSortMode.NameDescending => _visibleItems
                    .OrderBy(item => item.IsFolder ? 0 : 1)
                    .ThenByDescending(item => item.FileName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.AddedTicks),
                ItemSortMode.LatestAdded => _visibleItems
                    .OrderBy(item => item.IsFolder ? 0 : 1)
                    .ThenByDescending(item => item.AddedTicks)
                    .ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase),
                _ => _visibleItems
                    .OrderBy(item => item.IsFolder ? 0 : 1)
                    .ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.AddedTicks)
            };

            List<VaultFileItem> sorted = ordered.ToList();
            _visibleItems.Clear();
            _visibleItems.AddRange(sorted);
        }

        private void NavigateUp()
        {
            if (_session == null || string.IsNullOrWhiteSpace(_currentFolder))
                return;

            _currentFolder = GetParentPath(_currentFolder);
            ReloadFolderItems();
        }

        private async Task OpenItemActionsAsync(VaultFileItem item, bool includeSelectAction = false)
        {
            if (item.IsFolder)
            {
                StartSelectionModeWithItem(item.Id);
                return;
            }

            UIAlertController sheet = UIAlertController.Create(
                item.FileName,
                $"{item.SizeLabel} - {item.AddedAtLabel}",
                UIAlertControllerStyle.ActionSheet);

            if (includeSelectAction)
            {
                sheet.AddAction(UIAlertAction.Create("Seleziona", UIAlertActionStyle.Default, __ =>
                {
                    StartSelectionModeWithItem(item.Id);
                }));
            }
            sheet.AddAction(UIAlertAction.Create("Apri", UIAlertActionStyle.Default, __ =>
            {
                _ = OpenFileAsync(item);
            }));
            if (IsImagePreviewCandidate(item.FileName))
            {
                sheet.AddAction(UIAlertAction.Create("Ruota a sinistra", UIAlertActionStyle.Default, __ =>
                {
                    _ = OpenImageEditorAsync(item, initialQuarterTurns: -1);
                }));
                sheet.AddAction(UIAlertAction.Create("Ruota a destra", UIAlertActionStyle.Default, __ =>
                {
                    _ = OpenImageEditorAsync(item, initialQuarterTurns: 1);
                }));
            }
            if (IsArchiveExtractionCandidate(item.FileName))
            {
                sheet.AddAction(UIAlertAction.Create("Estrai in cartella", UIAlertActionStyle.Default, __ =>
                {
                    _ = ExtractArchiveAsync(item);
                }));
            }
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

        private async Task RotateImagePermanentAsync(VaultFileItem item, bool clockwise)
        {
            if (_session == null || item.IsFolder || !IsImagePreviewCandidate(item.FileName))
                return;

            VaultPortableReader session = _session;
            Guid originalId = item.Id;
            string originalParent = item.ParentPath;
            string originalName = item.FileName;
            string rotatedName = GetRotatedOutputName(originalName);

            string originalBackupPath = CreateTemporaryPath(originalName);
            string rotatedPath = CreateTemporaryPath(rotatedName);
            Guid rotatedId = Guid.Empty;

            try
            {
                await RunBusyWithProgressAsync(
                    clockwise ? "Rotazione a destra..." : "Rotazione a sinistra...",
                    async progress =>
                    {
                        if (_session == null)
                            return;

                        ReportProgress(progress, 4d);
                        await Task.Run(() =>
                        {
                            using var output = new FileStream(
                                originalBackupPath,
                                FileMode.Create,
                                FileAccess.Write,
                                FileShare.None,
                                VaultPersistCopyBufferSize,
                                FileOptions.SequentialScan);
                            session.CopyFileContentToStream(originalId, output);
                        });

                        ReportProgress(progress, 24d);
                        await Task.Run(() =>
                        {
                            RotateImageFileOnDisk(originalBackupPath, rotatedPath, rotatedName, clockwise);
                        });

                        try
                        {
                            ReportProgress(progress, 46d);
                            VaultFileItem added = await Task.Run(() => session.AddFileFromPath(rotatedPath, originalParent));
                            rotatedId = added.Id;

                            session.DeleteItems(new[] { originalId });
                            session.RenameItem(rotatedId, rotatedName);
                            RecordPendingChange($"Ruotata l'immagine \"{item.FileName}\".");
                            await PersistVaultAsync(CreateScaledProgress(progress, 52d, 100d));
                            EnsureCurrentFolderStillExists();
                            ReloadFolderItems();
                        }
                        catch
                        {
                            TryRollbackRotateOperation(session, rotatedId, originalId, originalBackupPath, originalParent, originalName);
                            throw;
                        }
                    });
            }
            finally
            {
                TryDeletePath(originalBackupPath);
                TryDeletePath(rotatedPath);
            }
        }

        private async Task OpenFileAsync(VaultFileItem item)
        {
            if (_session == null)
                return;

            if (item.IsFolder)
            {
                _currentFolder = item.FullPath;
                ReloadFolderItems();
                return;
            }

            if (IsVideoPreviewCandidate(item.FileName))
            {
                await OpenVideoInAppAsync(item);
                return;
            }

            if (TryPresentImageGallery(item))
            {
                await Task.CompletedTask;
                return;
            }

            await RunBusyAsync("Preparazione file...", async () =>
            {
                string tempPath = await Task.Run(() => WriteTemporaryFileFromVault(_session, item));
                PresentDocumentPreview(tempPath);
            });
        }

        private async Task OpenVideoInAppAsync(VaultFileItem item)
        {
            if (_session == null)
                return;

            await RunBusyAsync("Preparazione video...", async () =>
            {
                (string localPath, bool deleteOnClose) = await Task.Run(() => ResolvePlaybackPath(_session, item));
                PresentInAppVideoPlayer(localPath, item.FileName, deleteOnClose);
            });
        }

        private async Task OpenImageEditorAsync(VaultFileItem item, int initialQuarterTurns = 0)
        {
            if (_session == null || item.IsFolder || !IsImagePreviewCandidate(item.FileName))
                return;

            await RunBusyAsync("Preparazione modifica...", async () =>
            {
                string tempPath = await Task.Run(() => WriteTemporaryFileFromVault(_session, item));
                try
                {
                    UIImage? image = await Task.Run(() => LoadFullResolutionImage(tempPath));
                    if (image == null)
                        throw new InvalidOperationException("Immagine non disponibile.");

                    BeginInvokeOnMainThread(() =>
                    {
                        var editor = new ImageEditViewController(
                            this,
                            item,
                            image,
                            onSaved: null,
                            initialQuarterTurns: initialQuarterTurns);

                        NavigationController?.PushViewController(editor, true);
                    });
                }
                finally
                {
                    TryDeletePath(tempPath);
                }
            });
        }

        private async Task ExtractArchiveAsync(VaultFileItem item)
        {
            if (_session == null || item.IsFolder || !IsArchiveExtractionCandidate(item.FileName))
                return;

            VaultPortableReader session = _session;
            string sourcePath = string.Empty;
            bool deleteSourcePath = false;
            Guid rootFolderId = Guid.Empty;

            await RunBusyWithProgressAsync("Estrazione archivio...", async progress =>
            {
                try
                {
                    ReportProgress(progress, 4d);
                    (sourcePath, deleteSourcePath) = await Task.Run(() => ResolveReadableContentPath(session, item));

                    ReportProgress(progress, 10d);
                    VaultFileItem extractionRoot = await Task.Run(() =>
                        session.CreateFolder(GetArchiveExtractionFolderName(item.FileName), item.ParentPath));
                    rootFolderId = extractionRoot.Id;

                    await Task.Run(() => ExtractArchiveIntoFolder(session, sourcePath, extractionRoot.FullPath, progress));
                    RecordPendingChange($"Estratto l'archivio \"{item.FileName}\".");

                    ReportProgress(progress, 88d);
                    await PersistVaultAsync(CreateScaledProgress(progress, 88d, 100d));
                    EnsureCurrentFolderStillExists();
                    ReloadFolderItems();
                }
                catch
                {
                    if (rootFolderId != Guid.Empty)
                    {
                        try
                        {
                            session.DeleteItems(new[] { rootFolderId });
                        }
                        catch
                        {
                            // Best effort rollback.
                        }
                    }

                    throw;
                }
                finally
                {
                    if (deleteSourcePath && !string.IsNullOrWhiteSpace(sourcePath))
                        DeleteTemporaryFile(sourcePath);
                }
            });
        }

        private bool TryPresentImageGallery(VaultFileItem item)
        {
            if (_session == null || item.IsFolder || !IsImagePreviewCandidate(item.FileName))
                return false;

            List<VaultFileItem> images = _visibleItems
                .Where(visible => !visible.IsFolder && IsImagePreviewCandidate(visible.FileName))
                .ToList();
            if (images.Count == 0)
                return false;

            int startIndex = images.FindIndex(image => image.Id == item.Id);
            if (startIndex < 0)
                return false;

            var viewer = new ImageGalleryViewController(this, _session, images, startIndex);
            var nav = new UINavigationController(viewer)
            {
                ModalPresentationStyle = UIModalPresentationStyle.FullScreen
            };
            PresentViewController(nav, true, null);
            return true;
        }

        private async Task<VaultFileItem> SaveEditedImageAsync(
            VaultFileItem sourceItem,
            UIImage editedImage,
            bool overwrite,
            IProgress<double>? progress = null)
        {
            if (_session == null)
                throw new InvalidOperationException("Vault non aperto.");
            if (sourceItem.IsFolder)
                throw new InvalidOperationException("Elemento non valido.");

            VaultPortableReader session = _session;
            string sourceName = sourceItem.FileName;
            string outputName = overwrite
                ? GetEditableOutputName(sourceName, appendModifiedSuffix: false)
                : GetEditableOutputName(sourceName, appendModifiedSuffix: true);

            string editedTempPath = CreateTemporaryPath(outputName);
            string originalBackupPath = overwrite ? CreateTemporaryPath(sourceName) : string.Empty;
            Guid addedId = Guid.Empty;

            try
            {
                if (overwrite)
                {
                    ReportProgress(progress, 6d);
                    await Task.Run(() =>
                    {
                        using var output = new FileStream(
                            originalBackupPath,
                            FileMode.Create,
                            FileAccess.Write,
                            FileShare.None,
                            VaultPersistCopyBufferSize,
                            FileOptions.SequentialScan);
                        session.CopyFileContentToStream(sourceItem.Id, output);
                    });
                }

                ReportProgress(progress, overwrite ? 22d : 10d);
                await Task.Run(() => WriteImageToPath(editedImage, editedTempPath, outputName));

                ReportProgress(progress, overwrite ? 44d : 32d);
                VaultFileItem added = await Task.Run(() => session.AddFileFromPath(editedTempPath, sourceItem.ParentPath));
                addedId = added.Id;
                session.RenameItem(addedId, outputName);
                RecordPendingChange(overwrite
                    ? $"Modificata l'immagine \"{sourceItem.FileName}\"."
                    : $"Salvata una copia modificata di \"{sourceItem.FileName}\".");

                if (overwrite)
                {
                    ReportProgress(progress, 60d);
                    session.DeleteItems(new[] { sourceItem.Id });
                    await PersistVaultAsync(CreateScaledProgress(progress, 66d, 100d));
                }
                else
                {
                    await PersistVaultAsync(CreateScaledProgress(progress, 50d, 100d));
                }

                ClearThumbnailCache();
                ClearThumbnailDiskCache();
                EnsureCurrentFolderStillExists();
                ReloadFolderItems();

                return session.Files.FirstOrDefault(file => file.Id == addedId) ?? added;
            }
            catch
            {
                if (overwrite)
                {
                    TryRollbackEditedImageOperation(
                        session,
                        addedId,
                        sourceItem.Id,
                        originalBackupPath,
                        sourceItem.ParentPath,
                        sourceItem.FileName);
                }
                else if (addedId != Guid.Empty)
                {
                    try
                    {
                        session.DeleteItems(new[] { addedId });
                    }
                    catch
                    {
                        // Best effort rollback.
                    }
                }

                throw;
            }
            finally
            {
                TryDeletePath(editedTempPath);
                TryDeletePath(originalBackupPath);
            }
        }

        private void PresentInAppVideoPlayer(string localPath, string fileName, bool deleteOnClose)
        {
            if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
                throw new FileNotFoundException("Video temporaneo non trovato.", localPath);

            _videoPlayerController = new InAppVideoPlayerViewController(
                localPath,
                fileName,
                onClosed: () =>
                {
                    _videoPlayerController = null;
                    if (deleteOnClose)
                        DeleteTemporaryFile(localPath);
                });

            var nav = new UINavigationController(_videoPlayerController)
            {
                ModalPresentationStyle = UIModalPresentationStyle.FullScreen
            };

            PresentViewController(nav, true, null);
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
                    RecordPendingChange($"Rinominato \"{item.FileName}\" in \"{newName}\".");
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
                    _ = RunBusyWithProgressAsync("Spostamento...", async progress =>
                    {
                        if (_session == null)
                            return;

                        _session.MoveItems(new[] { item.Id }, folder);
                        RecordPendingChange($"Spostato \"{item.FileName}\".");
                        ReportProgress(progress, 14d);
                        await PersistVaultAsync(CreateScaledProgress(progress, 18d, 100d));
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
                    RecordPendingChange($"Eliminato \"{item.FileName}\".");
                    await PersistVaultAsync();
                    EnsureCurrentFolderStillExists();
                    ReloadFolderItems();
                });
            }));

            PresentViewController(alert, true, null);
        }

        private async Task PersistVaultAsync(IProgress<double>? progress = null, bool force = false)
        {
            if (_session == null || _vaultUrl == null || (!_session.IsDirty && !_session.NeedsVaultIdUpgrade))
                return;
            if (!force && _manualSaveModeEnabled)
                return;

            VaultPortableReader session = _session;
            NSUrl vaultUrl = _vaultUrl;
            ClearThumbnailCache();
            PrepareSessionForPersist(session);
            EnsureEnoughFreeSpaceForPersist(session, vaultUrl);
            await Task.Run(() => PersistVaultToUrl(vaultUrl, session, progress));

            if (force && _manualSaveModeEnabled && !HasPendingVaultSaveChanges)
            {
                DeleteCurrentDraftIfPresent();
                ClearPendingChangeSummary();
            }
        }

        private static void PrepareSessionForPersist(VaultPortableReader session)
        {
            if (session.StorageFormat != VaultStorageFormat.Legacy)
                return;

            long payloadBytes = EstimateSessionPayloadBytes(session);
            if (payloadBytes < LegacyAutoUpgradeThresholdBytes)
                return;

            VaultStorageFormat targetFormat = payloadBytes >= LegacyUltraUpgradeThresholdBytes
                ? VaultStorageFormat.Ultra
                : VaultStorageFormat.Extended;

            session.ChangeStorageFormat(targetFormat);
        }

        private static void EnsureEnoughFreeSpaceForPersist(VaultPortableReader session, NSUrl vaultUrl)
        {
            long availableBytes = TryGetConservativeAvailableBytes(vaultUrl.Path);
            if (availableBytes <= 0)
                return;

            long estimatedOutputBytes = EstimatePersistOutputBytes(session, vaultUrl);
            long requiredBytes = checked(estimatedOutputBytes + PersistSafetyMarginBytes);
            if (availableBytes >= requiredBytes)
                return;

            long missingBytes = requiredBytes - availableBytes;
            throw new IOException(
                $"Spazio insufficiente per completare il salvataggio del vault. " +
                $"Disponibile {FormatByteSize(availableBytes)}, richiesto circa {FormatByteSize(requiredBytes)} " +
                $"(mancano {FormatByteSize(missingBytes)}).");
        }

        private static long EstimatePersistOutputBytes(VaultPortableReader session, NSUrl vaultUrl)
        {
            long sessionBytes = EstimateSessionPayloadBytes(session);
            long overheadBytes = sessionBytes > 0
                ? Math.Max(8L * 1024 * 1024, sessionBytes / 25L)
                : 8L * 1024 * 1024;
            long estimatedBytes = checked(sessionBytes + overheadBytes);

            long fileSizeBytes = TryGetVaultFileSize(vaultUrl);
            if (fileSizeBytes > estimatedBytes)
                estimatedBytes = fileSizeBytes;

            return estimatedBytes;
        }

        private static long EstimateSessionPayloadBytes(VaultPortableReader session)
        {
            long total = 0;
            foreach (VaultFileItem item in session.Files)
            {
                if (item.IsFolder)
                    continue;

                long size = item.ContentLength;
                if (size <= 0)
                    continue;

                total = checked(total + size);
            }

            return total;
        }

        private static long TryGetVaultFileSize(NSUrl vaultUrl)
        {
            try
            {
                string? path = vaultUrl.Path;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return 0;

                return new FileInfo(path).Length;
            }
            catch
            {
                return 0;
            }
        }

        private static long TryGetConservativeAvailableBytes(string? destinationPath)
        {
            long destinationFree = TryGetAvailableBytes(destinationPath);
            long runtimeFree = TryGetAvailableBytes(GetRuntimeTempDirectoryPath());

            if (destinationFree > 0 && runtimeFree > 0)
                return Math.Min(destinationFree, runtimeFree);

            if (destinationFree > 0)
                return destinationFree;

            if (runtimeFree > 0)
                return runtimeFree;

            return -1;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StatVfsData
        {
            public ulong f_bsize;
            public ulong f_frsize;
            public ulong f_blocks;
            public ulong f_bfree;
            public ulong f_bavail;
            public ulong f_files;
            public ulong f_ffree;
            public ulong f_favail;
            public ulong f_fsid;
            public ulong f_flag;
            public ulong f_namemax;
        }

        [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "statvfs", SetLastError = true)]
        private static extern int StatVfs(string path, out StatVfsData stat);

        private static long TryGetAvailableBytes(string? targetPath)
        {
            string resolvedPath = ResolveSpaceProbePath(targetPath);
            long statValue = TryGetAvailableBytesWithStatVfs(resolvedPath);
            if (statValue > 0)
                return statValue;

            try
            {
                string root = Path.GetPathRoot(resolvedPath) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(root))
                    return -1;

                return new DriveInfo(root).AvailableFreeSpace;
            }
            catch
            {
                return -1;
            }
        }

        private static long TryGetAvailableBytesWithStatVfs(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                    return -1;

                if (StatVfs(path, out StatVfsData stat) != 0)
                    return -1;

                double bytes = (double)stat.f_bavail * stat.f_frsize;
                if (bytes <= 0d)
                    return -1;
                if (bytes >= long.MaxValue)
                    return long.MaxValue;

                return (long)bytes;
            }
            catch
            {
                return -1;
            }
        }

        private static string ResolveSpaceProbePath(string? targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
                return Path.GetTempPath();

            string? current = targetPath;
            if (File.Exists(current))
                current = Path.GetDirectoryName(current) ?? current;

            if (Directory.Exists(current))
                return current;

            while (!string.IsNullOrWhiteSpace(current))
            {
                current = Path.GetDirectoryName(current);
                if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
                    return current;
            }

            return Path.GetTempPath();
        }

        private static string FormatByteSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = Math.Max(0d, bytes);
            int unitIndex = 0;
            while (value >= 1024d && unitIndex < units.Length - 1)
            {
                value /= 1024d;
                unitIndex++;
            }

            return $"{value:0.#} {units[unitIndex]}";
        }

        private static void ReportProgress(IProgress<double>? progress, double value)
        {
            progress?.Report(Math.Max(0d, Math.Min(100d, value)));
        }

        private static IProgress<double> CreateScaledProgress(IProgress<double>? progress, double startPercent, double endPercent)
        {
            double safeStart = Math.Max(0d, Math.Min(100d, startPercent));
            double safeEnd = Math.Max(safeStart, Math.Min(100d, endPercent));
            double span = safeEnd - safeStart;

            return new Progress<double>(value =>
            {
                double inner = Math.Max(0d, Math.Min(100d, value));
                double mapped = safeStart + (inner / 100d) * span;
                ReportProgress(progress, mapped);
            });
        }

        private async Task RunBusyAsync(string message, Func<Task> action)
        {
            SetBusyState(true, message, showProgress: true);
            StartBusyPseudoProgress();
            try
            {
                await action();
                StopBusyPseudoProgress();
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
                StopBusyPseudoProgress();
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

        private void StartBusyPseudoProgress()
        {
            StopBusyPseudoProgress();

            var cts = new CancellationTokenSource();
            _busyPseudoProgressCts = cts;
            UpdateBusyProgress(0d);

            _ = Task.Run(async () =>
            {
                double current = 0d;
                try
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(140, cts.Token);
                        current = GetNextBusyPseudoProgress(current);
                        UpdateBusyProgress(current);
                    }
                }
                catch (OperationCanceledException)
                {
                }
            });
        }

        private void StopBusyPseudoProgress()
        {
            CancellationTokenSource? cts = Interlocked.Exchange(ref _busyPseudoProgressCts, null);
            if (cts == null)
                return;

            try
            {
                cts.Cancel();
            }
            catch
            {
            }

            cts.Dispose();
        }

        private static double GetNextBusyPseudoProgress(double currentPercent)
        {
            double clamped = Math.Max(0d, Math.Min(96d, currentPercent));
            if (clamped < 24d)
                return clamped + 8d;
            if (clamped < 52d)
                return clamped + 5d;
            if (clamped < 74d)
                return clamped + 3d;
            if (clamped < 88d)
                return clamped + 1.4d;
            if (clamped < 95d)
                return clamped + 0.6d;

            return 96d;
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

        private (string path, bool deleteOnClose) ResolvePlaybackPath(VaultPortableReader session, VaultFileItem item)
        {
            if (session.TryGetLocalContentPath(item.Id, out string localPath) &&
                !string.IsNullOrWhiteSpace(localPath) &&
                File.Exists(localPath))
            {
                return (localPath, false);
            }

            return (WriteTemporaryFileFromVault(session, item), true);
        }

        private (string path, bool deleteOnClose) ResolveReadableContentPath(VaultPortableReader session, VaultFileItem item)
        {
            if (session.TryGetLocalContentPath(item.Id, out string localPath) &&
                !string.IsNullOrWhiteSpace(localPath) &&
                File.Exists(localPath))
            {
                return (localPath, false);
            }

            return (WriteTemporaryFileFromVault(session, item), true);
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

        private static string ResolvePickerResultFileName(PHPickerResult result, string tempPath)
        {
            string extension = Path.GetExtension(tempPath ?? string.Empty);
            string? candidate = result?.ItemProvider?.SuggestedName;

            if (string.IsNullOrWhiteSpace(candidate))
                candidate = TryGetPhotoLibraryOriginalFileName(result?.AssetIdentifier);

            if (string.IsNullOrWhiteSpace(candidate))
                candidate = Path.GetFileName(tempPath);

            candidate = (candidate ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(Path.GetExtension(candidate)) && !string.IsNullOrWhiteSpace(extension))
                candidate += extension;

            return string.IsNullOrWhiteSpace(candidate) ? $"media{extension}" : candidate;
        }

        private static string? TryGetPhotoLibraryOriginalFileName(string? assetIdentifier)
        {
            if (string.IsNullOrWhiteSpace(assetIdentifier))
                return null;

            try
            {
                PHFetchResult assets = PHAsset.FetchAssetsUsingLocalIdentifiers(new[] { assetIdentifier }, null);
                if (assets.Count <= 0)
                    return null;

                if (assets[0] is not PHAsset asset)
                    return null;

                return asset.ValueForKey(new NSString("filename"))?.ToString();
            }
            catch
            {
                return null;
            }
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

        private static VaultFileFormat.Header ReadVaultHeader(NSUrl fileUrl)
        {
            using var scope = new SecurityScopeAccess(fileUrl);

            string? path = fileUrl.Path;
            if (string.IsNullOrWhiteSpace(path))
                throw new IOException("Percorso file non valido.");

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return VaultFileFormat.ReadHeader(stream);
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
            string? destinationDirectory = Path.GetDirectoryName(path);
            string tmpPath = CreateVaultWriteTempPathNearDestination(path, fileName, ".tmp", out bool tmpNearDestination);
            string backupPath = CreateVaultWriteTempPath(
                fileName,
                ".bak",
                tmpNearDestination ? destinationDirectory : null);
            string? swapBackupPath = null;
            bool success = false;

            try
            {
                using (var output = new FileStream(
                    tmpPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    VaultPersistCopyBufferSize,
                    FileOptions.SequentialScan))
                {
                    session.SaveToStream(output, progress);
                    output.Flush(flushToDisk: true);
                }

                if (!File.Exists(path))
                {
                    if (!TryMoveWithOverwrite(tmpPath, path))
                        File.Move(tmpPath, path);

                    success = true;
                    return;
                }

                // First choice: replace with overwrite move (no full backup duplication).
                if (TryMoveWithOverwrite(tmpPath, path))
                {
                    success = true;
                    return;
                }

                // Second choice: two-phase rename in destination directory.
                if (tmpNearDestination && TrySwapWithRenamedBackup(tmpPath, path, out swapBackupPath))
                {
                    success = true;
                    return;
                }

                // Third choice: platform replace.
                if (TryReplaceFile(tmpPath, path, backupPath))
                {
                    success = true;
                    return;
                }

                // Last resort with explicit rollback copy.
                OverwriteFileWithRollback(tmpPath, path, backupPath);
                success = true;
            }
            finally
            {
                TryDeletePath(tmpPath);
                TryDeletePath(backupPath);
                if (success)
                    TryDeletePath(swapBackupPath ?? string.Empty);
            }
        }

        private static void ApplyDraftFileToVault(NSUrl fileUrl, string draftFilePath, IProgress<double>? progress = null)
        {
            using var scope = new SecurityScopeAccess(fileUrl);

            string? destinationPath = fileUrl.Path;
            if (string.IsNullOrWhiteSpace(destinationPath))
                throw new IOException("Percorso file non valido.");
            if (string.IsNullOrWhiteSpace(draftFilePath) || !File.Exists(draftFilePath))
                throw new FileNotFoundException("Bozza del vault non trovata.", draftFilePath);

            string fileName = Path.GetFileName(destinationPath);
            string? destinationDirectory = Path.GetDirectoryName(destinationPath);
            string tmpPath = CreateVaultWriteTempPathNearDestination(destinationPath, fileName, ".tmp", out bool tmpNearDestination);
            string backupPath = CreateVaultWriteTempPath(
                fileName,
                ".bak",
                tmpNearDestination ? destinationDirectory : null);
            string? swapBackupPath = null;
            bool success = false;

            try
            {
                CopyFileWithProgress(draftFilePath, tmpPath, progress, 0d, 72d);

                if (!File.Exists(destinationPath))
                {
                    if (!TryMoveWithOverwrite(tmpPath, destinationPath))
                        File.Move(tmpPath, destinationPath);

                    ReportProgress(progress, 100d);
                    success = true;
                    return;
                }

                ReportProgress(progress, 78d);
                if (TryMoveWithOverwrite(tmpPath, destinationPath))
                {
                    ReportProgress(progress, 100d);
                    success = true;
                    return;
                }

                ReportProgress(progress, 84d);
                if (tmpNearDestination && TrySwapWithRenamedBackup(tmpPath, destinationPath, out swapBackupPath))
                {
                    ReportProgress(progress, 100d);
                    success = true;
                    return;
                }

                ReportProgress(progress, 90d);
                if (TryReplaceFile(tmpPath, destinationPath, backupPath))
                {
                    ReportProgress(progress, 100d);
                    success = true;
                    return;
                }

                OverwriteFileWithRollback(tmpPath, destinationPath, backupPath);
                ReportProgress(progress, 100d);
                success = true;
            }
            finally
            {
                TryDeletePath(tmpPath);
                TryDeletePath(backupPath);
                if (success)
                    TryDeletePath(swapBackupPath ?? string.Empty);
            }
        }

        private static void CopyFileWithProgress(string sourcePath, string destinationPath, IProgress<double>? progress, double startPercent, double endPercent)
        {
            using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, VaultPersistCopyBufferSize, FileOptions.SequentialScan);
            using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, VaultPersistCopyBufferSize, FileOptions.SequentialScan);

            long totalBytes = Math.Max(1L, input.Length);
            long copiedBytes = 0L;
            byte[] buffer = new byte[VaultPersistCopyBufferSize];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                copiedBytes += read;
                double ratio = copiedBytes / (double)totalBytes;
                double mapped = startPercent + ((endPercent - startPercent) * ratio);
                ReportProgress(progress, mapped);
            }

            output.Flush(flushToDisk: true);
        }

        private static string CreateVaultWriteTempPathNearDestination(
            string destinationPath,
            string baseFileName,
            string suffix,
            out bool nearDestination)
        {
            nearDestination = false;
            string? destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (string.IsNullOrWhiteSpace(destinationDirectory))
                return CreateVaultWriteTempPath(baseFileName, suffix);

            string candidate = CreateVaultWriteTempPath(baseFileName, suffix, destinationDirectory);
            try
            {
                using (var probe = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                }

                File.Delete(candidate);
                nearDestination = true;
                return candidate;
            }
            catch
            {
                TryDeletePath(candidate);
                return CreateVaultWriteTempPath(baseFileName, suffix);
            }
        }

        private static string CreateVaultWriteTempPath(string baseFileName, string suffix, string? preferredDirectory = null)
        {
            string safeBaseName = string.IsNullOrWhiteSpace(baseFileName) ? "vault" : baseFileName;
            string fileName = $".{safeBaseName}.{Guid.NewGuid():N}{suffix}";

            if (!string.IsNullOrWhiteSpace(preferredDirectory))
            {
                try
                {
                    Directory.CreateDirectory(preferredDirectory);
                    return Path.Combine(preferredDirectory, fileName);
                }
                catch
                {
                    // Fallback to runtime directory.
                }
            }

            string runtimeRoot = GetRuntimeTempDirectoryPath();
            Directory.CreateDirectory(runtimeRoot);
            return Path.Combine(runtimeRoot, fileName);
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

        private static bool TrySwapWithRenamedBackup(string sourcePath, string destinationPath, out string? backupPath)
        {
            backupPath = null;
            string? destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (string.IsNullOrWhiteSpace(destinationDirectory))
                return false;

            string destinationName = Path.GetFileName(destinationPath);
            backupPath = Path.Combine(destinationDirectory, $".{destinationName}.{Guid.NewGuid():N}.rollback");
            bool destinationMoved = false;

            try
            {
                File.Move(destinationPath, backupPath);
                destinationMoved = true;
                File.Move(sourcePath, destinationPath);
                TryDeletePath(backupPath);
                backupPath = null;
                return true;
            }
            catch
            {
                if (destinationMoved)
                {
                    try
                    {
                        if (!File.Exists(destinationPath) && File.Exists(backupPath))
                        {
                            File.Move(backupPath, destinationPath);
                            backupPath = null;
                        }
                    }
                    catch
                    {
                        // Best effort rollback.
                    }
                }

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

                using var input = new FileStream(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    VaultPersistCopyBufferSize,
                    FileOptions.SequentialScan);
                using var output = new FileStream(
                    destinationPath,
                    FileMode.OpenOrCreate,
                    FileAccess.Write,
                    FileShare.None,
                    VaultPersistCopyBufferSize,
                    FileOptions.SequentialScan);
                output.SetLength(0);
                input.CopyTo(output, VaultPersistCopyBufferSize);
                output.Flush(flushToDisk: true);
            }
            catch
            {
                if (hasBackup && File.Exists(backupPath))
                {
                    try
                    {
                        using var backupInput = new FileStream(
                            backupPath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            VaultPersistCopyBufferSize,
                            FileOptions.SequentialScan);
                        using var restoreOutput = new FileStream(
                            destinationPath,
                            FileMode.OpenOrCreate,
                            FileAccess.Write,
                            FileShare.None,
                            VaultPersistCopyBufferSize,
                            FileOptions.SequentialScan);
                        restoreOutput.SetLength(0);
                        backupInput.CopyTo(restoreOutput, VaultPersistCopyBufferSize);
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

            _ = OpenFileAsync(item);
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

            _ = OpenFileAsync(item);
        }

        private void HandleItemLongPress(int index)
        {
            if (index < 0 || index >= _visibleItems.Count)
                return;

            VaultFileItem item = _visibleItems[index];
            if (_isSelectionMode)
            {
                ToggleSelectedItem(item.Id);
                return;
            }

            if (item.IsFolder)
            {
                StartSelectionModeWithItem(item.Id);
                return;
            }

            _ = OpenItemActionsAsync(item, includeSelectAction: true);
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

            // Get image orientation from EXIF data
            var imageOptions = new CGImageOptions();
            using var properties = imageSource.CopyPropertiesAtIndex(0, imageOptions);
            nint orientation = properties?.Dictionary.TryGetValue(ImageIO.CGImageProperties.Orientation, out NSObject? orientationValue) == true
                ? (orientationValue as NSNumber)?.Int32Value ?? 1
                : 1;

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

            UIImage? baseImage = UIImage.FromImage(cgImage, 1, (UIImageOrientation)orientation);
            if (baseImage == null)
                return null;

            UIImage rendered = baseImage.ImageWithRenderingMode(UIImageRenderingMode.AlwaysOriginal);
            if (!ReferenceEquals(rendered, baseImage))
                baseImage.Dispose();

            return rendered;
        }

        private static UIImage? LoadFullResolutionImage(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                return null;

            using NSData? data = NSData.FromFile(sourcePath);
            UIImage? baseImage = data != null ? UIImage.LoadFromData(data) : UIImage.FromFile(sourcePath);
            if (baseImage == null)
                return null;

            // Fix orientation for full resolution images
            if (baseImage.Orientation != UIImageOrientation.Up)
            {
                UIImage? fixedImage = FixOrientation(baseImage);
                if (fixedImage != null)
                {
                    baseImage.Dispose();
                    return fixedImage.ImageWithRenderingMode(UIImageRenderingMode.AlwaysOriginal);
                }
            }

            UIImage rendered = baseImage.ImageWithRenderingMode(UIImageRenderingMode.AlwaysOriginal);
            if (!ReferenceEquals(rendered, baseImage))
                baseImage.Dispose();

            return rendered;
        }

        private static UIImage? FixOrientation(UIImage image)
        {
            if (image.Orientation == UIImageOrientation.Up)
                return image;

            UIGraphics.BeginImageContextWithOptions(new CGSize(image.Size.Width, image.Size.Height), false, image.Scale);
            var context = UIGraphics.GetCurrentContext();

            if (image.Orientation == UIImageOrientation.Down)
            {
                context.TranslateCTM(image.Size.Width, image.Size.Height);
                context.RotateCTM((nfloat)Math.PI);
            }
            else if (image.Orientation == UIImageOrientation.Left)
            {
                context.TranslateCTM(0, image.Size.Height);
                context.RotateCTM((nfloat)(3 * Math.PI / 2));
            }
            else if (image.Orientation == UIImageOrientation.Right)
            {
                context.TranslateCTM(image.Size.Width, 0);
                context.RotateCTM((nfloat)(Math.PI / 2));
            }
            else if (image.Orientation == UIImageOrientation.UpMirrored)
            {
                context.TranslateCTM(image.Size.Width, 0);
                context.ScaleCTM(-1, 1);
            }
            else if (image.Orientation == UIImageOrientation.DownMirrored)
            {
                context.TranslateCTM(0, image.Size.Height);
                context.ScaleCTM(-1, 1);
            }
            else if (image.Orientation == UIImageOrientation.LeftMirrored)
            {
                context.TranslateCTM(image.Size.Height, 0);
                context.ScaleCTM(-1, 1);
                context.RotateCTM((nfloat)(3 * Math.PI / 2));
            }
            else if (image.Orientation == UIImageOrientation.RightMirrored)
            {
                context.TranslateCTM(0, image.Size.Width);
                context.ScaleCTM(-1, 1);
                context.RotateCTM((nfloat)(Math.PI / 2));
            }

            image.Draw(new CGPoint(0, 0));
            UIImage? fixedImage = UIGraphics.GetImageFromCurrentImageContext();
            UIGraphics.EndImageContext();

            return fixedImage;
        }

        private static bool IsImagePreviewCandidate(string? fileName)
        {
            string ext = (Path.GetExtension(fileName ?? string.Empty) ?? string.Empty).ToLowerInvariant();
            return ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".heic" or ".tif" or ".tiff";
        }

        private static bool IsVideoPreviewCandidate(string? fileName)
        {
            string ext = (Path.GetExtension(fileName ?? string.Empty) ?? string.Empty).ToLowerInvariant();
            return ext is ".mov" or ".mp4" or ".m4v";
        }

        private static bool IsArchiveExtractionCandidate(string? fileName)
        {
            string normalized = (fileName ?? string.Empty).ToLowerInvariant();
            return normalized.EndsWith(".zip", StringComparison.Ordinal) ||
                   normalized.EndsWith(".rar", StringComparison.Ordinal) ||
                   normalized.EndsWith(".7z", StringComparison.Ordinal) ||
                   normalized.EndsWith(".tar", StringComparison.Ordinal) ||
                   normalized.EndsWith(".tar.gz", StringComparison.Ordinal) ||
                   normalized.EndsWith(".tgz", StringComparison.Ordinal) ||
                   normalized.EndsWith(".tar.bz2", StringComparison.Ordinal) ||
                   normalized.EndsWith(".tbz2", StringComparison.Ordinal) ||
                   normalized.EndsWith(".tar.xz", StringComparison.Ordinal) ||
                   normalized.EndsWith(".txz", StringComparison.Ordinal) ||
                   normalized.EndsWith(".gz", StringComparison.Ordinal) ||
                   normalized.EndsWith(".bz2", StringComparison.Ordinal) ||
                   normalized.EndsWith(".xz", StringComparison.Ordinal);
        }

        private static string GetArchiveExtractionFolderName(string? fileName)
        {
            string safeName = string.IsNullOrWhiteSpace(fileName) ? "archivio" : fileName.Trim();
            string normalized = safeName.ToLowerInvariant();
            string[] doubleExtensions =
            {
                ".tar.gz", ".tar.bz2", ".tar.xz", ".tgz", ".tbz2", ".txz"
            };

            foreach (string ext in doubleExtensions)
            {
                if (!normalized.EndsWith(ext, StringComparison.Ordinal))
                    continue;

                return safeName[..^ext.Length];
            }

            string single = Path.GetFileNameWithoutExtension(safeName);
            return string.IsNullOrWhiteSpace(single) ? "archivio" : single;
        }

        private static string GetEditableOutputName(string? originalName, bool appendModifiedSuffix)
        {
            string safeOriginal = string.IsNullOrWhiteSpace(originalName) ? "immagine.jpg" : originalName;
            string baseName = Path.GetFileNameWithoutExtension(safeOriginal);
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "immagine";

            string ext = (Path.GetExtension(safeOriginal) ?? string.Empty).ToLowerInvariant();
            if (ext is not ".jpg" and not ".jpeg" and not ".png")
                ext = ".jpg";

            return appendModifiedSuffix
                ? $"{baseName}-modificato{ext}"
                : $"{baseName}{ext}";
        }

        private static string GetRotatedOutputName(string? originalName)
        {
            string safeOriginal = string.IsNullOrWhiteSpace(originalName) ? "immagine.jpg" : originalName;
            string ext = (Path.GetExtension(safeOriginal) ?? string.Empty).ToLowerInvariant();
            if (ext is ".jpg" or ".jpeg" or ".png")
                return safeOriginal;

            string baseName = Path.GetFileNameWithoutExtension(safeOriginal);
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "immagine";

            return $"{baseName}.jpg";
        }

        private static void WriteImageToPath(UIImage image, string destinationPath, string outputName)
        {
            if (image == null)
                throw new ArgumentNullException(nameof(image));
            if (string.IsNullOrWhiteSpace(destinationPath))
                throw new ArgumentException("Path non valido.", nameof(destinationPath));

            UIImage normalized = NormalizeImageOrientation(image);
            bool disposeNormalized = !ReferenceEquals(normalized, image);

            try
            {
                using NSData? data = EncodeImageForName(normalized, outputName);
                if (data == null)
                    throw new InvalidOperationException("Impossibile codificare l'immagine.");

                File.WriteAllBytes(destinationPath, data.ToArray());
            }
            finally
            {
                if (disposeNormalized)
                    normalized.Dispose();
            }
        }

        private static void RotateImageFileOnDisk(string sourcePath, string destinationPath, string outputName, bool clockwise)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                throw new FileNotFoundException("Immagine sorgente non trovata.", sourcePath);

            using UIImage? source = UIImage.FromFile(sourcePath);
            if (source == null)
                throw new InvalidOperationException("Formato immagine non supportato per la rotazione.");

            using UIImage rotated = RotateImageBy90(source, clockwise);
            using NSData? data = EncodeImageForName(rotated, outputName);
            if (data == null)
                throw new InvalidOperationException("Impossibile salvare l'immagine ruotata.");

            File.WriteAllBytes(destinationPath, data.ToArray());
        }

        private static UIImage RotateImageBy90(UIImage source, bool clockwise)
        {
            UIImage normalized = NormalizeImageOrientation(source);
            bool disposeNormalized = !ReferenceEquals(normalized, source);

            nfloat srcWidth = normalized.Size.Width;
            nfloat srcHeight = normalized.Size.Height;
            CGSize dstSize = new CGSize(srcHeight, srcWidth);

            using var renderer = new UIGraphicsImageRenderer(dstSize);
            try
            {
                UIImage rendered = renderer.CreateImage(context =>
                {
                    CGContext ctx = context.CGContext;
                    if (clockwise)
                    {
                        ctx.TranslateCTM(dstSize.Width, 0f);
                        ctx.RotateCTM((nfloat)(Math.PI / 2d));
                    }
                    else
                    {
                        ctx.TranslateCTM(0f, dstSize.Height);
                        ctx.RotateCTM((nfloat)(-Math.PI / 2d));
                    }

                    normalized.Draw(new CGRect(0f, 0f, srcWidth, srcHeight));
                });

                UIImage result = rendered.ImageWithRenderingMode(UIImageRenderingMode.AlwaysOriginal);
                if (!ReferenceEquals(result, rendered))
                    rendered.Dispose();

                return result;
            }
            finally
            {
                if (disposeNormalized)
                    normalized.Dispose();
            }
        }

        private static int NormalizeQuarterTurns(int quarterTurns)
        {
            int normalized = quarterTurns % 4;
            return normalized < 0 ? normalized + 4 : normalized;
        }

        private static UIImage RotateImageByQuarterTurns(UIImage source, int quarterTurns)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            int normalizedTurns = NormalizeQuarterTurns(quarterTurns);
            if (normalizedTurns == 0)
            {
                UIImage same = source.ImageWithRenderingMode(UIImageRenderingMode.AlwaysOriginal);
                return ReferenceEquals(same, source) ? source : same;
            }

            UIImage current = source;
            bool ownsCurrent = false;

            try
            {
                for (int i = 0; i < normalizedTurns; i++)
                {
                    UIImage rotated = RotateImageBy90(current, clockwise: true);
                    if (ownsCurrent)
                        current.Dispose();

                    current = rotated;
                    ownsCurrent = true;
                }

                return current;
            }
            catch
            {
                if (ownsCurrent)
                    current.Dispose();
                throw;
            }
        }

        private static UIImage CreateEditorPreviewImage(UIImage source, int maxPixelSize)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            UIImage normalized = NormalizeImageOrientation(source);
            bool disposeNormalized = !ReferenceEquals(normalized, source);

            try
            {
                nfloat srcWidth = normalized.Size.Width;
                nfloat srcHeight = normalized.Size.Height;
                if (srcWidth <= 0f || srcHeight <= 0f)
                    throw new InvalidOperationException("Dimensioni immagine non valide.");

                nfloat longestSide = srcWidth >= srcHeight ? srcWidth : srcHeight;
                nfloat scale = longestSide > maxPixelSize
                    ? (nfloat)(maxPixelSize / (double)longestSide)
                    : 1f;
                CGSize dstSize = new CGSize(
                    (nfloat)Math.Max(1d, (double)(srcWidth * scale)),
                    (nfloat)Math.Max(1d, (double)(srcHeight * scale)));

                using var renderer = new UIGraphicsImageRenderer(dstSize);
                UIImage rendered = renderer.CreateImage(_ =>
                {
                    normalized.Draw(new CGRect(0f, 0f, dstSize.Width, dstSize.Height));
                });

                UIImage result = rendered.ImageWithRenderingMode(UIImageRenderingMode.AlwaysOriginal);
                if (!ReferenceEquals(result, rendered))
                    rendered.Dispose();

                return result;
            }
            finally
            {
                if (disposeNormalized)
                    normalized.Dispose();
            }
        }

        private static UIImage CropImage(UIImage source, CGRect cropRect)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            UIImage normalized = NormalizeImageOrientation(source);
            bool disposeNormalized = !ReferenceEquals(normalized, source);

            try
            {
                CGRect bounded = new CGRect(
                    (nfloat)Math.Max(0d, (double)cropRect.X),
                    (nfloat)Math.Max(0d, (double)cropRect.Y),
                    (nfloat)Math.Max(1d, (double)cropRect.Width),
                    (nfloat)Math.Max(1d, (double)cropRect.Height));

                nfloat maxWidth = normalized.Size.Width - bounded.X;
                nfloat maxHeight = normalized.Size.Height - bounded.Y;
                bounded.Width = (nfloat)Math.Max(1d, Math.Min((double)bounded.Width, (double)maxWidth));
                bounded.Height = (nfloat)Math.Max(1d, Math.Min((double)bounded.Height, (double)maxHeight));

                using var renderer = new UIGraphicsImageRenderer(bounded.Size);
                UIImage rendered = renderer.CreateImage(_ =>
                {
                    normalized.Draw(new CGRect(-bounded.X, -bounded.Y, normalized.Size.Width, normalized.Size.Height));
                });

                UIImage result = rendered.ImageWithRenderingMode(UIImageRenderingMode.AlwaysOriginal);
                if (!ReferenceEquals(result, rendered))
                    rendered.Dispose();

                return result;
            }
            finally
            {
                if (disposeNormalized)
                    normalized.Dispose();
            }
        }

        private static UIImage NormalizeImageOrientation(UIImage image)
        {
            if (image.Orientation == UIImageOrientation.Up)
                return image;

            using var renderer = new UIGraphicsImageRenderer(image.Size);
            UIImage normalized = renderer.CreateImage(_ =>
            {
                image.Draw(new CGRect(0f, 0f, image.Size.Width, image.Size.Height));
            });

            return normalized;
        }

        private static NSData? EncodeImageForName(UIImage image, string fileName)
        {
            string ext = (Path.GetExtension(fileName ?? string.Empty) ?? string.Empty).ToLowerInvariant();
            if (ext == ".png")
                return image.AsPNG();

            return image.AsJPEG((nfloat)0.92f);
        }

        private static void ExtractArchiveIntoFolder(
            VaultPortableReader session,
            string archivePath,
            string rootFolderPath,
            IProgress<double>? progress)
        {
            if (session == null)
                throw new ArgumentNullException(nameof(session));
            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
                throw new FileNotFoundException("Archivio non trovato.", archivePath);
            if (string.IsNullOrWhiteSpace(rootFolderPath))
                throw new ArgumentException("Cartella destinazione non valida.", nameof(rootFolderPath));

            using IArchive archive = ArchiveFactory.Open(archivePath);
            var entries = archive.Entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
                .ToList();

            if (entries.Count == 0)
                return;

            var folderMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [string.Empty] = rootFolderPath
            };

            int processed = 0;
            foreach (var entry in entries)
            {
                string normalizedPath = NormalizeArchiveEntryPath((string?)entry.Key);
                if (string.IsNullOrWhiteSpace(normalizedPath) || ShouldSkipArchiveEntry(normalizedPath))
                {
                    processed++;
                    ReportArchiveExtractionProgress(progress, processed, entries.Count);
                    continue;
                }

                if (entry.IsDirectory)
                {
                    EnsureArchiveFolderPath(session, rootFolderPath, normalizedPath, folderMap);
                    processed++;
                    ReportArchiveExtractionProgress(progress, processed, entries.Count);
                    continue;
                }

                string parentRelative = GetParentPath(normalizedPath);
                string targetFolderPath = EnsureArchiveFolderPath(session, rootFolderPath, parentRelative, folderMap);
                string fileName = GetPathNodeName(normalizedPath);
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    processed++;
                    ReportArchiveExtractionProgress(progress, processed, entries.Count);
                    continue;
                }

                using Stream entryStream = entry.OpenEntryStream();
                long entrySize = entry.Size;
                if (entrySize >= 0)
                {
                    session.AddFileFromStream(fileName, entryStream, entrySize, targetFolderPath);
                }
                else
                {
                    string tempPath = CreateTemporaryPath(fileName);
                    try
                    {
                        using (var output = new FileStream(
                            tempPath,
                            FileMode.Create,
                            FileAccess.Write,
                            FileShare.None,
                            VaultPersistCopyBufferSize,
                            FileOptions.SequentialScan))
                        {
                            entryStream.CopyTo(output, VaultPersistCopyBufferSize);
                        }

                        VaultFileItem added = session.AddFileFromPath(tempPath, targetFolderPath);
                        session.RenameItem(added.Id, fileName);
                    }
                    finally
                    {
                        TryDeletePath(tempPath);
                    }
                }

                processed++;
                ReportArchiveExtractionProgress(progress, processed, entries.Count);
            }
        }

        private static void ReportArchiveExtractionProgress(IProgress<double>? progress, int processedEntries, int totalEntries)
        {
            if (totalEntries <= 0)
            {
                ReportProgress(progress, 84d);
                return;
            }

            double percent = 12d + (72d * processedEntries / totalEntries);
            ReportProgress(progress, percent);
        }

        private static string EnsureArchiveFolderPath(
            VaultPortableReader session,
            string rootFolderPath,
            string? relativeFolderPath,
            IDictionary<string, string> folderMap)
        {
            string normalizedRelative = NormalizeArchiveEntryPath(relativeFolderPath);
            if (string.IsNullOrWhiteSpace(normalizedRelative))
                return rootFolderPath;

            if (folderMap.TryGetValue(normalizedRelative, out string? mapped))
                return mapped;

            string currentRelative = string.Empty;
            string currentActual = rootFolderPath;
            foreach (string segment in normalizedRelative.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                currentRelative = string.IsNullOrWhiteSpace(currentRelative)
                    ? segment
                    : $"{currentRelative}/{segment}";

                if (folderMap.TryGetValue(currentRelative, out string? existing))
                {
                    currentActual = existing;
                    continue;
                }

                VaultFileItem created = session.CreateFolder(segment, currentActual);
                currentActual = created.FullPath;
                folderMap[currentRelative] = currentActual;
            }

            return currentActual;
        }

        private static string NormalizeArchiveEntryPath(string? rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                return string.Empty;

            string normalized = rawPath.Replace('\\', '/').Trim().Trim('/');
            while (normalized.StartsWith("./", StringComparison.Ordinal))
                normalized = normalized[2..];

            if (string.IsNullOrWhiteSpace(normalized))
                return string.Empty;

            string[] segments = normalized
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Where(segment => !string.Equals(segment, ".", StringComparison.Ordinal))
                .ToArray();

            if (segments.Any(segment => string.Equals(segment, "..", StringComparison.Ordinal)))
                throw new InvalidOperationException("Archivio non valido: percorso interno non sicuro.");

            return string.Join("/", segments);
        }

        private static bool ShouldSkipArchiveEntry(string normalizedPath)
        {
            if (string.IsNullOrWhiteSpace(normalizedPath))
                return true;

            if (normalizedPath.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string nodeName = GetPathNodeName(normalizedPath);
            return nodeName.StartsWith("._", StringComparison.Ordinal);
        }

        private static string GetPathNodeName(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            int idx = path.LastIndexOf('/');
            return idx < 0 ? path : path[(idx + 1)..];
        }

        private static void TryRollbackRotateOperation(
            VaultPortableReader session,
            Guid rotatedId,
            Guid originalId,
            string originalBackupPath,
            string originalParent,
            string originalName)
        {
            try
            {
                if (rotatedId != Guid.Empty)
                    session.DeleteItems(new[] { rotatedId });
            }
            catch
            {
                // Best effort rollback.
            }

            bool originalStillPresent = session.Files.Any(file => file.Id == originalId);
            if (originalStillPresent || !File.Exists(originalBackupPath))
                return;

            try
            {
                VaultFileItem restored = session.AddFileFromPath(originalBackupPath, originalParent);
                session.RenameItem(restored.Id, originalName);
            }
            catch
            {
                // Best effort rollback.
            }
        }

        private static void TryRollbackEditedImageOperation(
            VaultPortableReader session,
            Guid addedId,
            Guid originalId,
            string originalBackupPath,
            string originalParent,
            string originalName)
        {
            TryRollbackRotateOperation(session, addedId, originalId, originalBackupPath, originalParent, originalName);
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
                _subtitleLabel.Lines = 2;

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
                _subtitleLabel.Frame = new CGRect(10f, _titleLabel.Frame.Bottom, width - 20f, 30f);
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
                _subtitleLabel.Text = item.IsFolder
                    ? $"Cartella\nAggiunta: {item.AddedAtLabel}"
                    : $"{item.SizeLabel}\nAggiunta: {item.AddedAtLabel}";

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
                if (string.IsNullOrWhiteSpace(trimmed))
                    return string.Empty;
                if (trimmed.Contains('/') || trimmed.Contains('\\'))
                    return string.Empty;
                if (string.Equals(trimmed, ".", StringComparison.Ordinal) ||
                    string.Equals(trimmed, "..", StringComparison.Ordinal))
                {
                    return string.Empty;
                }
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

        private sealed class PendingImportDestinationViewController : UIViewController
        {
            private const string ImportCellId = "PendingImportDestinationCell";

            private readonly VaultPortableReader _session;
            private readonly Action<string> _onImportConfirmed;
            private readonly List<string> _folderPaths = new();
            private readonly int _fileCount;
            private string _selectedDestination;
            private UITableView? _tableView;

            public PendingImportDestinationViewController(
                VaultPortableReader session,
                string currentFolder,
                int fileCount,
                Action<string> onImportConfirmed)
            {
                _session = session ?? throw new ArgumentNullException(nameof(session));
                _fileCount = Math.Max(0, fileCount);
                _selectedDestination = NormalizeFolderPath(currentFolder);
                _onImportConfirmed = onImportConfirmed ?? throw new ArgumentNullException(nameof(onImportConfirmed));
            }

            public override void ViewDidLoad()
            {
                base.ViewDidLoad();

                View!.BackgroundColor = UIColor.White;
                Title = _fileCount == 1 ? "Importa file in attesa" : $"Importa {_fileCount} file";
                NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
                NavigationItem.RightBarButtonItems = new[]
                {
                    new UIBarButtonItem("Importa qui", UIBarButtonItemStyle.Done, (_, _) => ConfirmImport()),
                    new UIBarButtonItem("Nuova", UIBarButtonItemStyle.Plain, (_, _) => PromptCreateFolder(_selectedDestination))
                };

                _tableView = new UITableView(View.Bounds, UITableViewStyle.InsetGrouped)
                {
                    AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight,
                    Source = new PendingImportDestinationSource(this)
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

            private void ConfirmImport()
            {
                string destination = _selectedDestination;
                _onImportConfirmed(destination);
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

            private sealed class PendingImportDestinationSource : UITableViewSource
            {
                private readonly PendingImportDestinationViewController _owner;

                public PendingImportDestinationSource(PendingImportDestinationViewController owner)
                {
                    _owner = owner;
                }

                public override nint RowsInSection(UITableView tableView, nint section) =>
                    _owner._folderPaths.Count;

                public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
                {
                    UITableViewCell cell = tableView.DequeueReusableCell(ImportCellId)
                        ?? new UITableViewCell(UITableViewCellStyle.Subtitle, ImportCellId);

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

        private sealed class ImageGalleryViewController : UIViewController
        {
            private readonly MainViewController _owner;
            private readonly VaultPortableReader _session;
            private readonly List<VaultFileItem> _images;
            private readonly Dictionary<Guid, string> _ownedTempPaths = new();
            private readonly object _pathLock = new();

            private int _currentIndex;
            private int _loadVersion;
            private bool _cleanedUp;

            private UIImage? _currentImage;
            private UIScrollView? _scrollView;
            private UIImageView? _imageView;
            private UILabel? _counterLabel;
            private UILabel? _hintLabel;
            private UILabel? _errorLabel;
            private UIActivityIndicatorView? _spinner;
            private UISwipeGestureRecognizer? _swipeLeftGesture;
            private UISwipeGestureRecognizer? _swipeRightGesture;
            private ImageZoomScrollDelegate? _scrollDelegate;

            public ImageGalleryViewController(
                MainViewController owner,
                VaultPortableReader session,
                IReadOnlyList<VaultFileItem> images,
                int startIndex)
            {
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
                _session = session ?? throw new ArgumentNullException(nameof(session));
                _images = images?.ToList() ?? throw new ArgumentNullException(nameof(images));
                _currentIndex = Math.Max(0, Math.Min(startIndex, Math.Max(0, _images.Count - 1)));
            }

            public override void ViewDidLoad()
            {
                base.ViewDidLoad();

                View!.BackgroundColor = UIColor.Black;
                NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
                NavigationItem.LeftBarButtonItem = new UIBarButtonItem(
                    "Chiudi",
                    UIBarButtonItemStyle.Done,
                    (_, _) => DismissViewController(true, null));
                NavigationItem.RightBarButtonItem = CreateEditButton();

                _scrollView = new UIScrollView
                {
                    BackgroundColor = UIColor.Black,
                    BouncesZoom = true,
                    MinimumZoomScale = 1f,
                    MaximumZoomScale = 4f,
                    ShowsHorizontalScrollIndicator = false,
                    ShowsVerticalScrollIndicator = false
                };
                _scrollDelegate = new ImageZoomScrollDelegate(this);
                _scrollView.Delegate = _scrollDelegate;

                _imageView = new UIImageView
                {
                    ContentMode = UIViewContentMode.ScaleToFill,
                    BackgroundColor = UIColor.Black,
                    UserInteractionEnabled = true
                };
                _scrollView.AddSubview(_imageView);

                _spinner = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Large)
                {
                    Color = UIColor.White,
                    HidesWhenStopped = true
                };

                _counterLabel = new UILabel
                {
                    TextColor = UIColor.White,
                    Font = UIFont.SystemFontOfSize(13, UIFontWeight.Semibold),
                    TextAlignment = UITextAlignment.Center
                };

                _hintLabel = new UILabel
                {
                    TextColor = UIColor.FromWhiteAlpha(1f, 0.72f),
                    Font = UIFont.SystemFontOfSize(12f),
                    TextAlignment = UITextAlignment.Center,
                    Text = "Scorri o usa pinch / doppio tocco"
                };

                _errorLabel = new UILabel
                {
                    TextColor = UIColor.FromRGB(255, 105, 97),
                    Font = UIFont.SystemFontOfSize(14, UIFontWeight.Medium),
                    TextAlignment = UITextAlignment.Center,
                    Lines = 2,
                    Hidden = true
                };

                View.AddSubview(_scrollView);
                View.AddSubview(_spinner);
                View.AddSubview(_counterLabel);
                View.AddSubview(_hintLabel);
                View.AddSubview(_errorLabel);

                var doubleTap = new UITapGestureRecognizer(HandleDoubleTap)
                {
                    NumberOfTapsRequired = 2
                };
                _scrollView.AddGestureRecognizer(doubleTap);

                _swipeLeftGesture = new UISwipeGestureRecognizer(() => MoveRelative(+1))
                {
                    Direction = UISwipeGestureRecognizerDirection.Left
                };
                _swipeRightGesture = new UISwipeGestureRecognizer(() => MoveRelative(-1))
                {
                    Direction = UISwipeGestureRecognizerDirection.Right
                };
                View.AddGestureRecognizer(_swipeLeftGesture);
                View.AddGestureRecognizer(_swipeRightGesture);

                UpdateHeader();
                _ = LoadCurrentImageAsync();
            }

            public override void ViewDidLayoutSubviews()
            {
                base.ViewDidLayoutSubviews();
                if (View == null)
                    return;

                nfloat width = View.Bounds.Width;
                nfloat height = View.Bounds.Height;
                CGSize previousScrollSize = _scrollView?.Frame.Size ?? CGSize.Empty;
                if (_scrollView != null)
                    _scrollView.Frame = new CGRect(0f, 0f, width, height);
                _spinner!.Center = new CGPoint(width / 2f, height / 2f);

                _counterLabel!.Frame = new CGRect(20f, height - 72f, width - 40f, 20f);
                _hintLabel!.Frame = new CGRect(20f, height - 52f, width - 40f, 18f);
                _errorLabel!.Frame = new CGRect(20f, height - 102f, width - 40f, 42f);

                if (_currentImage != null && _scrollView != null)
                {
                    bool boundsChanged = previousScrollSize.Width != _scrollView.Frame.Width ||
                        previousScrollSize.Height != _scrollView.Frame.Height;
                    UpdateZoomLayout(resetZoom: boundsChanged);
                }
            }

            public override void ViewDidDisappear(bool animated)
            {
                base.ViewDidDisappear(animated);
                if (IsBeingDismissed || NavigationController?.IsBeingDismissed == true)
                    Cleanup();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    Cleanup();

                base.Dispose(disposing);
            }

            private void MoveRelative(int delta)
            {
                if (_images.Count <= 1)
                    return;

                int next = _currentIndex + delta;
                if (next < 0 || next >= _images.Count)
                    return;

                _currentIndex = next;
                UpdateHeader();
                _ = LoadCurrentImageAsync();
            }

            private void UpdateHeader()
            {
                if (_images.Count == 0)
                {
                    Title = "Immagini";
                    if (_counterLabel != null)
                        _counterLabel.Text = string.Empty;
                    return;
                }

                VaultFileItem current = _images[_currentIndex];
                Title = current.FileName;
                if (_counterLabel != null)
                    _counterLabel.Text = $"{_currentIndex + 1} / {_images.Count}";
            }

            private async Task LoadCurrentImageAsync()
            {
                if (_images.Count == 0 || _imageView == null || _spinner == null)
                    return;

                int loadVersion = Interlocked.Increment(ref _loadVersion);
                if (_errorLabel != null)
                    _errorLabel.Hidden = true;
                _spinner.StartAnimating();

                VaultFileItem current = _images[_currentIndex];

                try
                {
                    string sourcePath = await Task.Run(() => ResolveImagePath(current)).ConfigureAwait(false);
                    UIImage? image = await Task.Run(() => MainViewController.LoadFullResolutionImage(sourcePath))
                        .ConfigureAwait(false);
                    if (image == null)
                        throw new InvalidOperationException("Immagine non disponibile.");

                    BeginInvokeOnMainThread(() =>
                    {
                        if (loadVersion != Volatile.Read(ref _loadVersion))
                        {
                            image.Dispose();
                            return;
                        }

                        _currentImage?.Dispose();
                        _currentImage = image;
                        _imageView.Image = image;
                        UpdateZoomLayout(resetZoom: true);
                        _spinner.StopAnimating();
                        if (_errorLabel != null)
                            _errorLabel.Hidden = true;
                    });
                }
                catch
                {
                    BeginInvokeOnMainThread(() =>
                    {
                        if (loadVersion != Volatile.Read(ref _loadVersion))
                            return;

                        _spinner.StopAnimating();
                        _imageView!.Image = null;
                        _currentImage?.Dispose();
                        _currentImage = null;
                        UpdateZoomLayout(resetZoom: true);
                        if (_errorLabel != null)
                            _errorLabel.Hidden = false;
                        if (_errorLabel != null)
                            _errorLabel.Text = "Impossibile caricare questa immagine.";
                    });
                }
            }

            private string ResolveImagePath(VaultFileItem item)
            {
                if (_session.TryGetLocalContentPath(item.Id, out string localPath) && File.Exists(localPath))
                    return localPath;

                lock (_pathLock)
                {
                    if (_ownedTempPaths.TryGetValue(item.Id, out string? cached) &&
                        !string.IsNullOrWhiteSpace(cached) &&
                        File.Exists(cached))
                    {
                        return cached;
                    }
                }

                string tempPath = MainViewController.CreateTemporaryPath(item.FileName);
                using (var output = new FileStream(
                    tempPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    VaultPersistCopyBufferSize,
                    FileOptions.SequentialScan))
                {
                    _session.CopyFileContentToStream(item.Id, output);
                }

                lock (_pathLock)
                {
                    _ownedTempPaths[item.Id] = tempPath;
                }

                return tempPath;
            }

            private void Cleanup()
            {
                if (_cleanedUp)
                    return;
                _cleanedUp = true;

                Interlocked.Increment(ref _loadVersion);
                _currentImage?.Dispose();
                _currentImage = null;

                List<string> tempPaths;
                lock (_pathLock)
                {
                    tempPaths = _ownedTempPaths.Values.ToList();
                    _ownedTempPaths.Clear();
                }

                foreach (string path in tempPaths)
                    MainViewController.TryDeletePath(path);
            }

            private UIBarButtonItem CreateEditButton()
            {
                UIImage? symbol = UIImage.GetSystemImage("square.and.pencil") ?? UIImage.GetSystemImage("pencil");
                var item = symbol != null
                    ? new UIBarButtonItem(symbol, UIBarButtonItemStyle.Plain, (_, _) => OpenEditorForCurrentImage())
                    : new UIBarButtonItem("Modifica", UIBarButtonItemStyle.Plain, (_, _) => OpenEditorForCurrentImage());
                item.TintColor = UIColor.White;
                return item;
            }

            private void OpenEditorForCurrentImage()
            {
                if (_images.Count == 0 || _currentImage == null)
                    return;

                VaultFileItem current = _images[_currentIndex];
                var editor = new ImageEditViewController(
                    _owner,
                    current,
                    _currentImage,
                    HandleEditedImageSaved);

                NavigationController?.PushViewController(editor, true);
            }

            private void HandleEditedImageSaved(VaultFileItem savedItem, bool overwrite)
            {
                if (_images.Count == 0 || _imageView == null)
                    return;

                VaultFileItem previous = _images[_currentIndex];
                RemoveOwnedPath(previous.Id);

                if (overwrite)
                {
                    _images[_currentIndex] = savedItem;
                }
                else
                {
                    int insertIndex = Math.Min(_currentIndex + 1, _images.Count);
                    _images.Insert(insertIndex, savedItem);
                    _currentIndex = insertIndex;
                }

                _imageView.Image = null;
                _currentImage?.Dispose();
                _currentImage = null;
                UpdateZoomLayout(resetZoom: true);
                UpdateHeader();
                _ = LoadCurrentImageAsync();
            }

            private void RemoveOwnedPath(Guid itemId)
            {
                string? ownedPath = null;
                lock (_pathLock)
                {
                    if (_ownedTempPaths.TryGetValue(itemId, out string? cached))
                    {
                        ownedPath = cached;
                        _ownedTempPaths.Remove(itemId);
                    }
                }

                if (!string.IsNullOrWhiteSpace(ownedPath))
                    MainViewController.TryDeletePath(ownedPath);
            }

            private void HandleDoubleTap(UITapGestureRecognizer gesture)
            {
                if (_scrollView == null || _imageView == null || _currentImage == null)
                    return;

                nfloat minZoom = _scrollView.MinimumZoomScale;
                if (_scrollView.ZoomScale > minZoom + 0.01f)
                {
                    _scrollView.SetZoomScale(minZoom, true);
                    return;
                }

                nfloat targetZoom = (nfloat)Math.Min((double)_scrollView.MaximumZoomScale, (double)(minZoom * 2.5f));
                CGPoint tapPoint = gesture.LocationInView(_imageView);
                CGSize boundsSize = _scrollView.Bounds.Size;
                nfloat zoomWidth = boundsSize.Width / targetZoom;
                nfloat zoomHeight = boundsSize.Height / targetZoom;
                CGRect zoomRect = new CGRect(
                    tapPoint.X - zoomWidth / 2f,
                    tapPoint.Y - zoomHeight / 2f,
                    zoomWidth,
                    zoomHeight);

                _scrollView.ZoomToRect(zoomRect, true);
            }

            private void UpdateZoomLayout(bool resetZoom)
            {
                if (_scrollView == null || _imageView == null)
                    return;

                if (_currentImage == null)
                {
                    _scrollView.MinimumZoomScale = 1f;
                    _scrollView.MaximumZoomScale = 4f;
                    _scrollView.ZoomScale = 1f;
                    _imageView.Frame = _scrollView.Bounds;
                    _scrollView.ContentSize = _imageView.Frame.Size;
                    _scrollView.ContentInset = UIEdgeInsets.Zero;
                    UpdateZoomInteractionState();
                    return;
                }

                CGSize fittedSize = CalculateAspectFitSize(_currentImage.Size, _scrollView.Bounds.Size);
                _imageView.Frame = new CGRect(0f, 0f, fittedSize.Width, fittedSize.Height);
                _scrollView.ContentSize = fittedSize;
                _scrollView.MinimumZoomScale = 1f;
                _scrollView.MaximumZoomScale = 4f;

                if (resetZoom || _scrollView.ZoomScale < _scrollView.MinimumZoomScale)
                    _scrollView.ZoomScale = _scrollView.MinimumZoomScale;

                UpdateScrollInsets();
                UpdateZoomInteractionState();
            }

            private void UpdateScrollInsets()
            {
                if (_scrollView == null || _imageView == null)
                    return;

                CGSize boundsSize = _scrollView.Bounds.Size;
                CGRect imageFrame = _imageView.Frame;
                nfloat horizontalInset = (nfloat)Math.Max(0d, (double)((boundsSize.Width - imageFrame.Width) / 2f));
                nfloat verticalInset = (nfloat)Math.Max(0d, (double)((boundsSize.Height - imageFrame.Height) / 2f));
                _scrollView.ContentInset = new UIEdgeInsets(verticalInset, horizontalInset, verticalInset, horizontalInset);
            }

            private void UpdateZoomInteractionState()
            {
                bool isZoomed = _scrollView != null && _scrollView.ZoomScale > _scrollView.MinimumZoomScale + 0.01f;

                if (_scrollView != null)
                {
                    _scrollView.ScrollEnabled = isZoomed;
                    _scrollView.Bounces = isZoomed;
                    _scrollView.AlwaysBounceVertical = isZoomed;
                    _scrollView.AlwaysBounceHorizontal = isZoomed;
                    _scrollView.PanGestureRecognizer.Enabled = isZoomed;

                    if (!isZoomed)
                    {
                        CGPoint centeredOffset = new CGPoint(
                            -_scrollView.ContentInset.Left,
                            -_scrollView.ContentInset.Top);
                        _scrollView.SetContentOffset(centeredOffset, false);
                    }
                }

                if (_swipeLeftGesture != null)
                    _swipeLeftGesture.Enabled = !isZoomed;
                if (_swipeRightGesture != null)
                    _swipeRightGesture.Enabled = !isZoomed;
                if (_hintLabel != null)
                {
                    _hintLabel.Text = isZoomed
                        ? "Doppio tocco per tornare alla vista completa"
                        : "Scorri o usa pinch / doppio tocco";
                }
            }

            private static CGSize CalculateAspectFitSize(CGSize imageSize, CGSize boundsSize)
            {
                if (imageSize.Width <= 0f || imageSize.Height <= 0f || boundsSize.Width <= 0f || boundsSize.Height <= 0f)
                    return boundsSize;

                nfloat scale = (nfloat)Math.Min(
                    (double)(boundsSize.Width / imageSize.Width),
                    (double)(boundsSize.Height / imageSize.Height));
                double scaleValue = scale;
                if (scale <= 0f || double.IsNaN(scaleValue) || double.IsInfinity(scaleValue))
                    return boundsSize;

                return new CGSize(
                    (nfloat)Math.Max(1d, (double)(imageSize.Width * scale)),
                    (nfloat)Math.Max(1d, (double)(imageSize.Height * scale)));
            }

            private sealed class ImageZoomScrollDelegate : UIScrollViewDelegate
            {
                private readonly ImageGalleryViewController _owner;

                public ImageZoomScrollDelegate(ImageGalleryViewController owner)
                {
                    _owner = owner;
                }

                public override UIView ViewForZoomingInScrollView(UIScrollView scrollView)
                {
                    return _owner._imageView!;
                }

                public override void DidZoom(UIScrollView scrollView)
                {
                    _owner.UpdateScrollInsets();
                    _owner.UpdateZoomInteractionState();
                }
            }
        }

        private sealed class ImageEditViewController : UIViewController
        {
            private readonly MainViewController _owner;
            private readonly VaultFileItem _sourceItem;
            private readonly Action<VaultFileItem, bool> _onSaved;

            private UIImage _workingImage;
            private bool _ownsWorkingImage;
            private bool _isDirty;
            private UIImage? _previewImage;
            private int _pendingQuarterTurns;

            private UIImageView? _imageView;
            private UIVisualEffectView? _toolbarBackground;
            private UIStackView? _toolbarStack;
            private UIView? _busyOverlay;
            private UIActivityIndicatorView? _busyIndicator;
            private UILabel? _busyLabel;
            private UIProgressView? _busyProgressView;
            private UILabel? _busyPercentLabel;
            private CancellationTokenSource? _busyPseudoProgressCts;

            public ImageEditViewController(
                MainViewController owner,
                VaultFileItem sourceItem,
                UIImage workingImage,
                Action<VaultFileItem, bool>? onSaved,
                int initialQuarterTurns = 0)
            {
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
                _sourceItem = sourceItem ?? throw new ArgumentNullException(nameof(sourceItem));
                _workingImage = workingImage ?? throw new ArgumentNullException(nameof(workingImage));
                _onSaved = onSaved ?? ((_, _) => { });
                _pendingQuarterTurns = MainViewController.NormalizeQuarterTurns(initialQuarterTurns);
                _isDirty = _pendingQuarterTurns != 0;
            }

            public override void ViewDidLoad()
            {
                base.ViewDidLoad();

                View!.BackgroundColor = UIColor.Black;
                Title = "Modifica";
                NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
                NavigationItem.LeftBarButtonItem = new UIBarButtonItem(
                    "Annulla",
                    UIBarButtonItemStyle.Plain,
                    (_, _) => CloseEditor());
                NavigationItem.RightBarButtonItem = new UIBarButtonItem(
                    "Salva",
                    UIBarButtonItemStyle.Done,
                    (_, _) => PromptSaveOptions());

                _imageView = new UIImageView
                {
                    ContentMode = UIViewContentMode.ScaleAspectFit,
                    BackgroundColor = UIColor.Black,
                    Image = null
                };

                _toolbarBackground = new UIVisualEffectView(UIBlurEffect.FromStyle(UIBlurEffectStyle.SystemChromeMaterialDark))
                {
                    ClipsToBounds = true
                };
                _toolbarBackground.Layer.CornerRadius = 18f;

                _toolbarStack = new UIStackView
                {
                    Axis = UILayoutConstraintAxis.Horizontal,
                    Alignment = UIStackViewAlignment.Center,
                    Distribution = UIStackViewDistribution.FillEqually,
                    Spacing = 8f
                };

                _toolbarBackground.ContentView.AddSubview(_toolbarStack);
                _toolbarStack.AddArrangedSubview(CreateEditorButton("rotate.left", "SX", RotateLeft));
                _toolbarStack.AddArrangedSubview(CreateEditorButton("crop", "Crop", OpenCropEditor));
                _toolbarStack.AddArrangedSubview(CreateEditorButton("rotate.right", "DX", RotateRight));

                _busyOverlay = new UIView
                {
                    BackgroundColor = UIColor.FromWhiteAlpha(0f, 0.45f),
                    Hidden = true
                };

                _busyIndicator = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Large)
                {
                    Color = UIColor.White,
                    HidesWhenStopped = true
                };

                _busyLabel = new UILabel
                {
                    TextColor = UIColor.White,
                    Font = UIFont.SystemFontOfSize(15f, UIFontWeight.Semibold),
                    TextAlignment = UITextAlignment.Center,
                    Lines = 2
                };

                _busyProgressView = new UIProgressView(UIProgressViewStyle.Default)
                {
                    TrackTintColor = UIColor.FromWhiteAlpha(1f, 0.18f),
                    ProgressTintColor = UIColor.White
                };

                _busyPercentLabel = new UILabel
                {
                    TextColor = UIColor.FromWhiteAlpha(1f, 0.86f),
                    Font = UIFont.MonospacedDigitSystemFontOfSize(13f, UIFontWeight.Medium),
                    TextAlignment = UITextAlignment.Center
                };

                _busyOverlay.AddSubview(_busyIndicator);
                _busyOverlay.AddSubview(_busyLabel);
                _busyOverlay.AddSubview(_busyProgressView);
                _busyOverlay.AddSubview(_busyPercentLabel);

                View.AddSubview(_imageView);
                View.AddSubview(_toolbarBackground);
                View.AddSubview(_busyOverlay);
                RebuildPreviewImageFromWorkingImage();
            }

            public override void ViewDidLayoutSubviews()
            {
                base.ViewDidLayoutSubviews();
                if (View == null)
                    return;

                CGRect bounds = View.Bounds;
                UIEdgeInsets insets = View.SafeAreaInsets;

                nfloat toolbarHeight = 74f;
                nfloat toolbarY = bounds.Height - insets.Bottom - toolbarHeight - 14f;

                _imageView!.Frame = new CGRect(
                    0f,
                    insets.Top,
                    bounds.Width,
                    (nfloat)Math.Max(0d, (double)(toolbarY - insets.Top - 12f)));

                _toolbarBackground!.Frame = new CGRect(16f, toolbarY, bounds.Width - 32f, toolbarHeight);
                _toolbarStack!.Frame = _toolbarBackground.ContentView.Bounds.Inset(10f, 10f);

                _busyOverlay!.Frame = bounds;
                _busyIndicator!.Center = new CGPoint(bounds.GetMidX(), bounds.GetMidY() - 34f);
                _busyLabel!.Frame = new CGRect(28f, _busyIndicator.Frame.GetMaxY() + 12f, bounds.Width - 56f, 42f);
                _busyProgressView!.Frame = new CGRect(34f, _busyLabel.Frame.GetMaxY() + 10f, bounds.Width - 68f, 8f);
                _busyPercentLabel!.Frame = new CGRect(34f, _busyProgressView.Frame.GetMaxY() + 8f, bounds.Width - 68f, 18f);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    DisposePreviewImage();
                    if (_ownsWorkingImage)
                        _workingImage.Dispose();
                }

                base.Dispose(disposing);
            }

            private UIButton CreateEditorButton(string systemImageName, string fallbackTitle, Action action)
            {
                var button = new UIButton(UIButtonType.System);
                button.TintColor = UIColor.White;
                if (button.TitleLabel != null)
                    button.TitleLabel.Font = UIFont.SystemFontOfSize(24f, UIFontWeight.Semibold);

                UIImage? symbol = UIImage.GetSystemImage(systemImageName);
                if (symbol != null)
                {
                    button.SetImage(symbol, UIControlState.Normal);
                    if (button.ImageView != null)
                        button.ImageView.ContentMode = UIViewContentMode.ScaleAspectFit;
                }
                else
                {
                    button.SetTitle(fallbackTitle, UIControlState.Normal);
                }

                button.TouchUpInside += (_, _) => action();
                return button;
            }

            private void CloseEditor()
            {
                if (!_isDirty)
                {
                    NavigationController?.PopViewController(true);
                    return;
                }

                UIAlertController alert = UIAlertController.Create(
                    "Scarta modifiche?",
                    "Le modifiche non salvate andranno perse.",
                    UIAlertControllerStyle.Alert);
                alert.AddAction(UIAlertAction.Create("Continua a modificare", UIAlertActionStyle.Cancel, null));
                alert.AddAction(UIAlertAction.Create("Scarta", UIAlertActionStyle.Destructive, _ =>
                {
                    NavigationController?.PopViewController(true);
                }));
                PresentViewController(alert, true, null);
            }

            private void RotateLeft() => _ = RotateWorkingImageAsync(clockwise: false);

            private void RotateRight() => _ = RotateWorkingImageAsync(clockwise: true);

            private async Task RotateWorkingImageAsync(bool clockwise)
            {
                ShowBusy(clockwise ? "Rotazione a destra..." : "Rotazione a sinistra...", showProgress: true);
                try
                {
                    UIImage previewSource = _previewImage ?? _workingImage;
                    UIImage rotatedPreview = await Task.Run(() => MainViewController.RotateImageBy90(previewSource, clockwise));
                    ReplacePreviewImage(rotatedPreview);
                    _pendingQuarterTurns = MainViewController.NormalizeQuarterTurns(_pendingQuarterTurns + (clockwise ? 1 : -1));
                    _isDirty = true;
                }
                catch (Exception ex)
                {
                    ShowError(ex.Message);
                }
                finally
                {
                    HideBusy();
                }
            }

            private async Task MaterializePendingRotationAsync()
            {
                if (_pendingQuarterTurns == 0)
                    return;

                UIImage rotated = await Task.Run(() => MainViewController.RotateImageByQuarterTurns(_workingImage, _pendingQuarterTurns));
                ReplaceWorkingImage(rotated, markDirty: false);
            }

            private void RebuildPreviewImageFromWorkingImage()
            {
                int maxPixelSize = GetEditorPreviewMaxPixelSize();
                UIImage preview = MainViewController.CreateEditorPreviewImage(_workingImage, maxPixelSize);
                if (_pendingQuarterTurns != 0)
                {
                    UIImage rotatedPreview = MainViewController.RotateImageByQuarterTurns(preview, _pendingQuarterTurns);
                    if (!ReferenceEquals(rotatedPreview, preview))
                        preview.Dispose();
                    preview = rotatedPreview;
                }

                ReplacePreviewImage(preview);
            }

            private void ReplacePreviewImage(UIImage image)
            {
                DisposePreviewImage();
                _previewImage = image;
                if (_imageView != null)
                    _imageView.Image = image;
            }

            private void DisposePreviewImage()
            {
                _previewImage?.Dispose();
                _previewImage = null;
            }

            private int GetEditorPreviewMaxPixelSize()
            {
                CGSize screenSize = UIScreen.MainScreen.Bounds.Size;
                double longestSide = Math.Max((double)screenSize.Width, (double)screenSize.Height) * UIScreen.MainScreen.Scale * 1.5d;
                return (int)Math.Max(1400d, Math.Min(2200d, longestSide));
            }

            private void OpenCropEditor() => _ = OpenCropEditorAsync();

            private void ReplaceWorkingImage(UIImage image, bool markDirty)
            {
                if (_ownsWorkingImage)
                    _workingImage.Dispose();

                _workingImage = image;
                _ownsWorkingImage = true;
                _pendingQuarterTurns = 0;
                RebuildPreviewImageFromWorkingImage();
                if (markDirty)
                    _isDirty = true;
            }

            private void PromptSaveOptions()
            {
                UIAlertController sheet = UIAlertController.Create(
                    "Salva immagine",
                    null,
                    UIAlertControllerStyle.ActionSheet);

                sheet.AddAction(UIAlertAction.Create("Sovrascrivi originale", UIAlertActionStyle.Default, __ =>
                {
                    _ = SaveChangesAsync(overwrite: true);
                }));
                sheet.AddAction(UIAlertAction.Create("Salva copia", UIAlertActionStyle.Default, __ =>
                {
                    _ = SaveChangesAsync(overwrite: false);
                }));
                sheet.AddAction(UIAlertAction.Create("Annulla", UIAlertActionStyle.Cancel, null));

                UIPopoverPresentationController? popover = sheet.PopoverPresentationController;
                if (popover != null && View != null)
                {
                    popover.SourceView = View;
                    popover.SourceRect = new CGRect(View.Bounds.GetMidX(), View.Bounds.GetMidY(), 1, 1);
                }

                PresentViewController(sheet, true, null);
            }

            private async Task SaveChangesAsync(bool overwrite)
            {
                ShowBusy(overwrite ? "Salvataggio immagine..." : "Creazione copia modificata...", showProgress: true);
                try
                {
                    await MaterializePendingRotationAsync();
                    var progress = new Progress<double>(value => UpdateBusyProgress(value));
                    VaultFileItem saved = await _owner.SaveEditedImageAsync(_sourceItem, _workingImage, overwrite, progress);
                    _onSaved(saved, overwrite);
                    NavigationController?.PopViewController(true);
                }
                catch (Exception ex)
                {
                    ShowError(ex.Message);
                }
                finally
                {
                    HideBusy();
                }
            }

            private async Task OpenCropEditorAsync()
            {
                ShowBusy("Preparazione ritaglio...", showProgress: true);
                try
                {
                    await MaterializePendingRotationAsync();
                }
                catch (Exception ex)
                {
                    ShowError(ex.Message);
                    return;
                }
                finally
                {
                    HideBusy();
                }

                var cropController = new ImageCropViewController(_workingImage, cropped =>
                {
                    ReplaceWorkingImage(cropped, markDirty: true);
                });

                NavigationController?.PushViewController(cropController, true);
            }

            private void ShowBusy(string title, bool showProgress = false)
            {
                if (_busyOverlay == null)
                    return;

                if (showProgress)
                    StartBusyPseudoProgress();
                else
                    StopBusyPseudoProgress();

                _busyLabel!.Text = title;
                _busyProgressView!.Progress = 0f;
                _busyPercentLabel!.Text = "0%";
                _busyProgressView.Hidden = !showProgress;
                _busyPercentLabel.Hidden = !showProgress;
                _busyOverlay.Hidden = false;
                _busyIndicator!.StartAnimating();
            }

            private void UpdateBusyProgress(double value)
            {
                if (_busyProgressView == null || _busyPercentLabel == null)
                    return;

                float clamped = (float)Math.Max(0d, Math.Min(1d, value));
                BeginInvokeOnMainThread(() =>
                {
                    _busyProgressView.Progress = clamped;
                    _busyPercentLabel.Text = $"{Math.Round(clamped * 100d):0}%";
                });
            }

            private void StartBusyPseudoProgress()
            {
                StopBusyPseudoProgress();

                var cts = new CancellationTokenSource();
                _busyPseudoProgressCts = cts;
                UpdateBusyProgress(0d);

                _ = Task.Run(async () =>
                {
                    double current = 0d;
                    try
                    {
                        while (!cts.Token.IsCancellationRequested)
                        {
                            await Task.Delay(140, cts.Token);
                            current = GetNextPseudoProgress(current);
                            UpdateBusyProgress(current);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                });
            }

            private void StopBusyPseudoProgress()
            {
                CancellationTokenSource? cts = Interlocked.Exchange(ref _busyPseudoProgressCts, null);
                if (cts == null)
                    return;

                try
                {
                    cts.Cancel();
                }
                catch
                {
                }

                cts.Dispose();
            }

            private static double GetNextPseudoProgress(double currentPercent)
            {
                double clamped = Math.Max(0d, Math.Min(0.96d, currentPercent));
                if (clamped < 0.24d)
                    return clamped + 0.08d;
                if (clamped < 0.52d)
                    return clamped + 0.05d;
                if (clamped < 0.74d)
                    return clamped + 0.03d;
                if (clamped < 0.88d)
                    return clamped + 0.014d;
                if (clamped < 0.95d)
                    return clamped + 0.006d;

                return 0.96d;
            }

            private void HideBusy()
            {
                if (_busyOverlay == null)
                    return;

                StopBusyPseudoProgress();
                _busyIndicator!.StopAnimating();
                _busyOverlay.Hidden = true;
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
        }

        private sealed class ImageCropViewController : UIViewController
        {
            private static readonly nfloat MinCropSize = 88f;
            private static readonly nfloat HandleSize = 28f;

            private readonly UIImage _image;
            private readonly Action<UIImage> _onApplied;

            private UIView? _previewContainer;
            private UIImageView? _imageView;
            private UILabel? _hintLabel;
            private UIView? _topShade;
            private UIView? _leftShade;
            private UIView? _rightShade;
            private UIView? _bottomShade;
            private UIView? _cropBorder;
            private UIView? _topLeftHandle;
            private UIView? _topRightHandle;
            private UIView? _bottomLeftHandle;
            private UIView? _bottomRightHandle;

            private CGRect _displayedImageFrame;
            private CGRect _cropRect;
            private CGRect _gestureStartRect;
            private bool _cropInitialized;

            public ImageCropViewController(UIImage image, Action<UIImage> onApplied)
            {
                _image = image ?? throw new ArgumentNullException(nameof(image));
                _onApplied = onApplied ?? throw new ArgumentNullException(nameof(onApplied));
            }

            public override void ViewDidLoad()
            {
                base.ViewDidLoad();

                View!.BackgroundColor = UIColor.Black;
                Title = "Ritaglia";
                NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
                NavigationItem.LeftBarButtonItem = new UIBarButtonItem(
                    "Annulla",
                    UIBarButtonItemStyle.Plain,
                    (_, _) => NavigationController?.PopViewController(true));
                NavigationItem.RightBarButtonItem = new UIBarButtonItem(
                    "Applica",
                    UIBarButtonItemStyle.Done,
                    (_, _) => ApplyCrop());

                _previewContainer = new UIView
                {
                    BackgroundColor = UIColor.Black,
                    ClipsToBounds = true
                };

                _imageView = new UIImageView(_image)
                {
                    ContentMode = UIViewContentMode.ScaleAspectFit,
                    BackgroundColor = UIColor.Black
                };

                _hintLabel = new UILabel
                {
                    Text = "Trascina l'area per ritagliare",
                    TextColor = UIColor.FromWhiteAlpha(1f, 0.78f),
                    Font = UIFont.SystemFontOfSize(13f, UIFontWeight.Medium),
                    TextAlignment = UITextAlignment.Center
                };

                _topShade = CreateShadeView();
                _leftShade = CreateShadeView();
                _rightShade = CreateShadeView();
                _bottomShade = CreateShadeView();

                _cropBorder = new UIView
                {
                    BackgroundColor = UIColor.Clear,
                    UserInteractionEnabled = true
                };
                _cropBorder.Layer.BorderColor = UIColor.White.CGColor;
                _cropBorder.Layer.BorderWidth = 2f;
                _cropBorder.AddGestureRecognizer(new UIPanGestureRecognizer(HandleMoveCropRect));

                _topLeftHandle = CreateHandleView(0);
                _topRightHandle = CreateHandleView(1);
                _bottomLeftHandle = CreateHandleView(2);
                _bottomRightHandle = CreateHandleView(3);

                _previewContainer.AddSubview(_imageView);
                _previewContainer.AddSubview(_topShade);
                _previewContainer.AddSubview(_leftShade);
                _previewContainer.AddSubview(_rightShade);
                _previewContainer.AddSubview(_bottomShade);
                _previewContainer.AddSubview(_cropBorder);
                _previewContainer.AddSubview(_topLeftHandle);
                _previewContainer.AddSubview(_topRightHandle);
                _previewContainer.AddSubview(_bottomLeftHandle);
                _previewContainer.AddSubview(_bottomRightHandle);

                View.AddSubview(_previewContainer);
                View.AddSubview(_hintLabel);
            }

            public override void ViewDidLayoutSubviews()
            {
                base.ViewDidLayoutSubviews();
                if (View == null)
                    return;

                CGRect bounds = View.Bounds;
                UIEdgeInsets insets = View.SafeAreaInsets;
                nfloat hintHeight = 24f;
                nfloat bottomSpacing = 18f;
                CGRect previewFrame = new CGRect(
                    12f,
                    insets.Top + 12f,
                    bounds.Width - 24f,
                    bounds.Height - insets.Top - insets.Bottom - hintHeight - bottomSpacing - 24f);

                _previewContainer!.Frame = previewFrame;
                _imageView!.Frame = _previewContainer.Bounds;
                _hintLabel!.Frame = new CGRect(16f, previewFrame.GetMaxY() + 8f, bounds.Width - 32f, hintHeight);

                _displayedImageFrame = GetAspectFitFrame(_image.Size, _previewContainer.Bounds);
                if (!_cropInitialized)
                {
                    nfloat insetX = (nfloat)Math.Min(24d, (double)_displayedImageFrame.Width * 0.12d);
                    nfloat insetY = (nfloat)Math.Min(24d, (double)_displayedImageFrame.Height * 0.12d);
                    _cropRect = _displayedImageFrame.Inset(insetX, insetY);
                    _cropInitialized = true;
                }
                else
                {
                    _cropRect = ClampMovedRect(_cropRect, _displayedImageFrame);
                }

                UpdateCropOverlay();
            }

            private static UIView CreateShadeView()
            {
                return new UIView
                {
                    BackgroundColor = UIColor.FromWhiteAlpha(0f, 0.55f),
                    UserInteractionEnabled = false
                };
            }

            private UIView CreateHandleView(int tag)
            {
                var handle = new UIView
                {
                    BackgroundColor = UIColor.White,
                    Tag = tag
                };
                handle.Layer.CornerRadius = HandleSize / 2f;
                handle.Layer.BorderColor = UIColor.Black.CGColor;
                handle.Layer.BorderWidth = 1.2f;
                handle.AddGestureRecognizer(new UIPanGestureRecognizer(HandleResizeCropRect));
                return handle;
            }

            private void HandleMoveCropRect(UIPanGestureRecognizer gesture)
            {
                if (_previewContainer == null)
                    return;

                if (gesture.State == UIGestureRecognizerState.Began)
                    _gestureStartRect = _cropRect;

                CGPoint translation = gesture.TranslationInView(_previewContainer);
                if (gesture.State is UIGestureRecognizerState.Changed or UIGestureRecognizerState.Ended)
                {
                    _cropRect = ClampMovedRect(new CGRect(
                        _gestureStartRect.X + translation.X,
                        _gestureStartRect.Y + translation.Y,
                        _gestureStartRect.Width,
                        _gestureStartRect.Height), _displayedImageFrame);
                    UpdateCropOverlay();
                }
            }

            private void HandleResizeCropRect(UIPanGestureRecognizer gesture)
            {
                if (_previewContainer == null || gesture.View == null)
                    return;

                if (gesture.State == UIGestureRecognizerState.Began)
                    _gestureStartRect = _cropRect;

                CGPoint translation = gesture.TranslationInView(_previewContainer);
                CGRect nextRect = gesture.View.Tag switch
                {
                    0 => ResizeTopLeft(_gestureStartRect, translation),
                    1 => ResizeTopRight(_gestureStartRect, translation),
                    2 => ResizeBottomLeft(_gestureStartRect, translation),
                    _ => ResizeBottomRight(_gestureStartRect, translation)
                };

                _cropRect = nextRect;
                UpdateCropOverlay();
            }

            private CGRect ResizeTopLeft(CGRect start, CGPoint translation)
            {
                nfloat right = start.GetMaxX();
                nfloat bottom = start.GetMaxY();
                nfloat left = (nfloat)Math.Max((double)_displayedImageFrame.X, Math.Min((double)(start.X + translation.X), (double)(right - MinCropSize)));
                nfloat top = (nfloat)Math.Max((double)_displayedImageFrame.Y, Math.Min((double)(start.Y + translation.Y), (double)(bottom - MinCropSize)));
                return new CGRect(left, top, right - left, bottom - top);
            }

            private CGRect ResizeTopRight(CGRect start, CGPoint translation)
            {
                nfloat left = start.X;
                nfloat bottom = start.GetMaxY();
                nfloat right = (nfloat)Math.Min((double)_displayedImageFrame.GetMaxX(), Math.Max((double)(start.GetMaxX() + translation.X), (double)(left + MinCropSize)));
                nfloat top = (nfloat)Math.Max((double)_displayedImageFrame.Y, Math.Min((double)(start.Y + translation.Y), (double)(bottom - MinCropSize)));
                return new CGRect(left, top, right - left, bottom - top);
            }

            private CGRect ResizeBottomLeft(CGRect start, CGPoint translation)
            {
                nfloat right = start.GetMaxX();
                nfloat top = start.Y;
                nfloat left = (nfloat)Math.Max((double)_displayedImageFrame.X, Math.Min((double)(start.X + translation.X), (double)(right - MinCropSize)));
                nfloat bottom = (nfloat)Math.Min((double)_displayedImageFrame.GetMaxY(), Math.Max((double)(start.GetMaxY() + translation.Y), (double)(top + MinCropSize)));
                return new CGRect(left, top, right - left, bottom - top);
            }

            private CGRect ResizeBottomRight(CGRect start, CGPoint translation)
            {
                nfloat left = start.X;
                nfloat top = start.Y;
                nfloat right = (nfloat)Math.Min((double)_displayedImageFrame.GetMaxX(), Math.Max((double)(start.GetMaxX() + translation.X), (double)(left + MinCropSize)));
                nfloat bottom = (nfloat)Math.Min((double)_displayedImageFrame.GetMaxY(), Math.Max((double)(start.GetMaxY() + translation.Y), (double)(top + MinCropSize)));
                return new CGRect(left, top, right - left, bottom - top);
            }

            private CGRect ClampMovedRect(CGRect rect, CGRect bounds)
            {
                nfloat width = (nfloat)Math.Min((double)rect.Width, (double)bounds.Width);
                nfloat height = (nfloat)Math.Min((double)rect.Height, (double)bounds.Height);
                nfloat maxX = bounds.GetMaxX() - width;
                nfloat maxY = bounds.GetMaxY() - height;
                nfloat x = (nfloat)Math.Max((double)bounds.X, Math.Min((double)rect.X, (double)maxX));
                nfloat y = (nfloat)Math.Max((double)bounds.Y, Math.Min((double)rect.Y, (double)maxY));
                return new CGRect(x, y, width, height);
            }

            private void UpdateCropOverlay()
            {
                if (_previewContainer == null ||
                    _topShade == null ||
                    _leftShade == null ||
                    _rightShade == null ||
                    _bottomShade == null ||
                    _cropBorder == null)
                {
                    return;
                }

                CGRect imageFrame = _displayedImageFrame;
                CGRect crop = _cropRect;

                _topShade.Frame = new CGRect(imageFrame.X, imageFrame.Y, imageFrame.Width, (nfloat)Math.Max(0d, (double)(crop.Y - imageFrame.Y)));
                _bottomShade.Frame = new CGRect(imageFrame.X, crop.GetMaxY(), imageFrame.Width, (nfloat)Math.Max(0d, (double)(imageFrame.GetMaxY() - crop.GetMaxY())));
                _leftShade.Frame = new CGRect(imageFrame.X, crop.Y, (nfloat)Math.Max(0d, (double)(crop.X - imageFrame.X)), crop.Height);
                _rightShade.Frame = new CGRect(crop.GetMaxX(), crop.Y, (nfloat)Math.Max(0d, (double)(imageFrame.GetMaxX() - crop.GetMaxX())), crop.Height);
                _cropBorder.Frame = crop;

                PositionHandle(_topLeftHandle, crop.X, crop.Y);
                PositionHandle(_topRightHandle, crop.GetMaxX(), crop.Y);
                PositionHandle(_bottomLeftHandle, crop.X, crop.GetMaxY());
                PositionHandle(_bottomRightHandle, crop.GetMaxX(), crop.GetMaxY());
            }

            private void PositionHandle(UIView? handle, nfloat centerX, nfloat centerY)
            {
                if (handle == null)
                    return;

                handle.Frame = new CGRect(
                    centerX - (HandleSize / 2f),
                    centerY - (HandleSize / 2f),
                    HandleSize,
                    HandleSize);
            }

            private void ApplyCrop()
            {
                if (_displayedImageFrame.Width <= 0f || _displayedImageFrame.Height <= 0f)
                    return;

                nfloat scaleX = _image.Size.Width / _displayedImageFrame.Width;
                nfloat scaleY = _image.Size.Height / _displayedImageFrame.Height;

                CGRect cropInImage = new CGRect(
                    (_cropRect.X - _displayedImageFrame.X) * scaleX,
                    (_cropRect.Y - _displayedImageFrame.Y) * scaleY,
                    _cropRect.Width * scaleX,
                    _cropRect.Height * scaleY);

                UIImage cropped = MainViewController.CropImage(_image, cropInImage);
                NavigationController?.PopViewController(true);
                _onApplied(cropped);
            }

            private static CGRect GetAspectFitFrame(CGSize contentSize, CGRect bounds)
            {
                if (contentSize.Width <= 0f || contentSize.Height <= 0f || bounds.Width <= 0f || bounds.Height <= 0f)
                    return CGRect.Empty;

                nfloat scale = (nfloat)Math.Min((double)(bounds.Width / contentSize.Width), (double)(bounds.Height / contentSize.Height));
                nfloat width = contentSize.Width * scale;
                nfloat height = contentSize.Height * scale;
                nfloat x = bounds.X + ((bounds.Width - width) / 2f);
                nfloat y = bounds.Y + ((bounds.Height - height) / 2f);
                return new CGRect(x, y, width, height);
            }
        }

        private sealed class InAppVideoPlayerViewController : UIViewController
        {
            private readonly string _localPath;
            private readonly string _displayName;
            private readonly Action _onClosed;

            private AVPlayer? _player;
            private AVPlayerViewController? _playerController;
            private bool _closed;

            public InAppVideoPlayerViewController(string localPath, string displayName, Action onClosed)
            {
                _localPath = localPath ?? throw new ArgumentNullException(nameof(localPath));
                _displayName = string.IsNullOrWhiteSpace(displayName) ? "Video" : displayName;
                _onClosed = onClosed ?? throw new ArgumentNullException(nameof(onClosed));
            }

            public override void ViewDidLoad()
            {
                base.ViewDidLoad();

                View!.BackgroundColor = UIColor.Black;
                Title = _displayName;
                NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
                NavigationItem.LeftBarButtonItem = new UIBarButtonItem(
                    "Chiudi",
                    UIBarButtonItemStyle.Done,
                    (_, _) => DismissViewController(true, null));

                NSUrl sourceUrl = NSUrl.FromFilename(_localPath);
                _player = AVPlayer.FromUrl(sourceUrl);
                _playerController = new AVPlayerViewController
                {
                    Player = _player,
                    ShowsPlaybackControls = true
                };

                AVPlayerViewController playerController = _playerController;
                UIView? playerView = playerController.View;
                if (playerView == null)
                    throw new InvalidOperationException("Player video non disponibile.");

                AddChildViewController(playerController);
                View.AddSubview(playerView);
                _playerController.DidMoveToParentViewController(this);
            }

            public override void ViewDidAppear(bool animated)
            {
                base.ViewDidAppear(animated);
                _player?.Play();
            }

            public override void ViewWillDisappear(bool animated)
            {
                base.ViewWillDisappear(animated);
                _player?.Pause();
            }

            public override void ViewDidLayoutSubviews()
            {
                base.ViewDidLayoutSubviews();
                if (_playerController?.View == null || View == null)
                    return;

                _playerController.View.Frame = View.Bounds;
            }

            public override void ViewDidDisappear(bool animated)
            {
                base.ViewDidDisappear(animated);
                if (IsBeingDismissed || NavigationController?.IsBeingDismissed == true)
                    CloseAndCleanup();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    CloseAndCleanup();

                base.Dispose(disposing);
            }

            private void CloseAndCleanup()
            {
                if (_closed)
                    return;
                _closed = true;

                try
                {
                    _player?.Pause();
                }
                catch
                {
                    // Best effort.
                }

                if (_playerController != null)
                {
                    try
                    {
                        _playerController.WillMoveToParentViewController(null);
                        _playerController.View?.RemoveFromSuperview();
                        _playerController.RemoveFromParentViewController();
                    }
                    catch
                    {
                        // Best effort.
                    }
                }

                _playerController?.Dispose();
                _player?.Dispose();
                _playerController = null;
                _player = null;
                _onClosed();
            }
        }

        private sealed class PickerDelegate : UIDocumentPickerDelegate
        {
            private readonly Action<NSUrl[]> _onPicked;
            private readonly Action? _onCancelled;

            public PickerDelegate(Action<NSUrl[]> onPicked, Action? onCancelled = null)
            {
                _onPicked = onPicked ?? throw new ArgumentNullException(nameof(onPicked));
                _onCancelled = onCancelled;
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
                controller.DismissViewController(true, () =>
                {
                    if (_onCancelled == null)
                        return;

                    UIApplication.SharedApplication.BeginInvokeOnMainThread(_onCancelled);
                });
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

