using System;
using System.Collections.Generic;
using CoreGraphics;
using Foundation;
using UIKit;

namespace vault.iOS
{
    public sealed class InstagramAnalysisViewController : UIViewController, IUIDocumentPickerDelegate
    {
        private UITableView? _tableView;
        private UILabel? _emptyLabel;
        private UIActivityIndicatorView? _loadingIndicator;
        private UITabBar? _bottomTabBar;

        private List<InstagramAnalysisService.InstagramUser> _followers = new();
        private List<InstagramAnalysisService.InstagramUser> _following = new();
        private List<InstagramAnalysisService.InstagramUser> _notFollowingBack = new();
        private List<InstagramAnalysisService.InstagramUser> _currentList = new();

        private UITabBarItem? _homeTabItem;
        private UITabBarItem? _importTabItem;
        private UITabBarItem? _followersTabItem;
        private UITabBarItem? _followingTabItem;
        private UITabBarItem? _notFollowingBackTabItem;

        private InstagramAnalysisService _analysisService = new();
        private UIDocumentPickerViewController? _documentPicker;

        public override void ViewDidLoad()
        {
            base.ViewDidLoad();

            Title = "Analisi Instagram";
            View!.BackgroundColor = UIColor.White;
            EdgesForExtendedLayout = UIRectEdge.None;
            if (NavigationController != null)
            {
                NavigationController.NavigationBar.Hidden = true;
            }
            NavigationItem.HidesBackButton = true;

            // Setup bottom tab bar
            SetupBottomTabBar();

            // Setup table view with optimizations for large datasets
            _tableView = new UITableView(CGRect.Empty, UITableViewStyle.Plain)
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                Delegate = new InstagramTableDelegate(),
                DataSource = new InstagramTableSource(_currentList),
                Hidden = true,
                EstimatedRowHeight = 54,
                RowHeight = 54,
                SeparatorStyle = UITableViewCellSeparatorStyle.SingleLine,
                SeparatorInset = UIEdgeInsets.Zero,
                PrefetchingEnabled = true
            };
            _tableView.RegisterClassForCellReuse(typeof(InstagramUserCell), InstagramUserCell.CellId);
            View.AddSubview(_tableView);

            NSLayoutConstraint.ActivateConstraints(new NSLayoutConstraint[]
            {
                _tableView.TopAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.TopAnchor, 8),
                _tableView.LeadingAnchor.ConstraintEqualTo(View.LeadingAnchor),
                _tableView.TrailingAnchor.ConstraintEqualTo(View.TrailingAnchor),
                _tableView.BottomAnchor.ConstraintEqualTo(_bottomTabBar!.TopAnchor)
            });

            // Setup empty label
            _emptyLabel = new UILabel
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                TextAlignment = UITextAlignment.Center,
                TextColor = UIColor.DarkGray,
                Text = "Importa i dati di Instagram per iniziare l'analisi",
                Lines = 2,
                Font = UIFont.SystemFontOfSize(16)
            };
            View.AddSubview(_emptyLabel);

            NSLayoutConstraint.ActivateConstraints(new NSLayoutConstraint[]
            {
                _emptyLabel.TopAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.TopAnchor, 8),
                _emptyLabel.LeadingAnchor.ConstraintEqualTo(View.LeadingAnchor),
                _emptyLabel.TrailingAnchor.ConstraintEqualTo(View.TrailingAnchor),
                _emptyLabel.BottomAnchor.ConstraintEqualTo(_bottomTabBar!.TopAnchor)
            });

            // Setup loading indicator
            _loadingIndicator = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Large)
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                HidesWhenStopped = true
            };
            View.AddSubview(_loadingIndicator);

            NSLayoutConstraint.ActivateConstraints(new NSLayoutConstraint[]
            {
                _loadingIndicator.CenterXAnchor.ConstraintEqualTo(View.CenterXAnchor),
                _loadingIndicator.CenterYAnchor.ConstraintEqualTo(View.CenterYAnchor)
            });
        }

        private void SetupBottomTabBar()
        {
            _homeTabItem = new UITabBarItem("Home", UIImage.GetSystemImage("house"), 0);
            _importTabItem = new UITabBarItem("Importa", UIImage.GetSystemImage("square.and.arrow.down"), 1);
            _followersTabItem = new UITabBarItem("Followers", UIImage.GetSystemImage("person.2"), 2);
            _followingTabItem = new UITabBarItem("Seguiti", UIImage.GetSystemImage("heart"), 3);
            _notFollowingBackTabItem = new UITabBarItem("Non ricambia", UIImage.GetSystemImage("person.badge.xmark"), 4);

            _bottomTabBar = new UITabBar
            {
                Translucent = false,
                BarTintColor = UIColor.FromRGB(249, 249, 252),
                TintColor = UIColor.FromRGB(10, 132, 255),
                Items = new[]
                {
                    _homeTabItem,
                    _importTabItem,
                    _followersTabItem,
                    _followingTabItem,
                    _notFollowingBackTabItem
                }
            };

            _bottomTabBar.ItemSelected += OnTabBarItemSelected;
            _bottomTabBar.TranslatesAutoresizingMaskIntoConstraints = false;
            View.AddSubview(_bottomTabBar);

            NSLayoutConstraint.ActivateConstraints(new NSLayoutConstraint[]
            {
                _bottomTabBar.LeadingAnchor.ConstraintEqualTo(View.LeadingAnchor),
                _bottomTabBar.TrailingAnchor.ConstraintEqualTo(View.TrailingAnchor),
                _bottomTabBar.BottomAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.BottomAnchor),
                _bottomTabBar.HeightAnchor.ConstraintEqualTo(50)
            });
        }

        private void OnTabBarItemSelected(object? sender, UITabBarItemEventArgs args)
        {
            if (_bottomTabBar != null)
                _bottomTabBar.SelectedItem = null;

            if (args.Item == null)
                return;

            if (ReferenceEquals(args.Item, _homeTabItem))
            {
                GoBackToHome();
                return;
            }

            if (ReferenceEquals(args.Item, _importTabItem))
            {
                PickDataFile();
                return;
            }

            if (_followers.Count == 0 && _following.Count == 0)
            {
                ShowNotification("Nessun dato", "Importa prima i dati di Instagram.");
                return;
            }

            if (ReferenceEquals(args.Item, _followersTabItem))
            {
                UpdateList(_followers);
            }
            else if (ReferenceEquals(args.Item, _followingTabItem))
            {
                UpdateList(_following);
            }
            else if (ReferenceEquals(args.Item, _notFollowingBackTabItem))
            {
                UpdateList(_notFollowingBack);
            }
        }

        private void UpdateList(List<InstagramAnalysisService.InstagramUser> newList)
        {
            _currentList.Clear();
            _currentList.AddRange(newList);

            if (_tableView?.DataSource is InstagramTableSource dataSource)
            {
                dataSource.UpdateUsers(_currentList);
            }

            _tableView?.ReloadData();
            if (_tableView != null && _currentList.Count > 0)
            {
                _tableView.ScrollToRow(NSIndexPath.FromRowSection(0, 0), UITableViewScrollPosition.Top, false);
            }
        }

        private void GoBackToHome()
        {
            if (NavigationController != null && NavigationController.ViewControllers.Length > 1)
            {
                NavigationController.PopViewController(true);
            }
            else
            {
                DismissViewController(true, null);
            }
        }

        private void PickDataFile()
        {
#pragma warning disable CA1422
            var types = new[] { "com.pkware.zip-archive", "public.json", "com.compuserve.gif" };
            _documentPicker = new UIDocumentPickerViewController(types, UIDocumentPickerMode.Import)
            {
                Delegate = this,
                AllowsMultipleSelection = false
            };

            if (_documentPicker != null)
            {
                PresentViewController(_documentPicker, true, null);
            }
#pragma warning restore CA1422
        }

        [Export("documentPicker:didPickDocumentsAtURLs:")]
        public void DocumentPickerDidPickDocuments(UIDocumentPickerViewController controller, NSUrl[] urls)
        {
            if (urls.Length == 0)
                return;

            var fileUrl = urls[0];
            _ = ProcessInstagramDataAsync(fileUrl);
        }

        [Export("documentPickerWasCancelled:")]
        public void DocumentPickerWasCancelled(UIDocumentPickerViewController controller)
        {
            // User cancelled
        }

        private async System.Threading.Tasks.Task ProcessInstagramDataAsync(NSUrl fileUrl)
        {
            BeginInvokeOnMainThread(() =>
            {
                _loadingIndicator?.StartAnimating();
                _loadingIndicator!.Hidden = false;
            });

            try
            {
                var result = await _analysisService.AnalyzeFromZipAsync(fileUrl);

                BeginInvokeOnMainThread(() =>
                {
                    _loadingIndicator?.StopAnimating();
                    _loadingIndicator!.Hidden = true;

                    _followers = result.Followers;
                    _following = result.Following;
                    _notFollowingBack = result.NotFollowingBack;

                    if (_followers.Count > 0 || _following.Count > 0)
                    {
                        _emptyLabel!.Hidden = true;
                        _tableView!.Hidden = false;
                        // Show followers by default
                        UpdateList(_followers);
                    }
                    else
                    {
                        ShowNotification("Nessun dato trovato", "Non sono stati trovati dati Instagram validi nel file selezionato.");
                    }
                });
            }
            catch (Exception ex)
            {
                BeginInvokeOnMainThread(() =>
                {
                    _loadingIndicator?.StopAnimating();
                    _loadingIndicator!.Hidden = true;
                    ShowNotification("Errore", $"Si è verificato un errore: {ex.Message}");
                });
            }
        }

        private void ShowNotification(string title, string message)
        {
            var alert = UIAlertController.Create(title, message, UIAlertControllerStyle.Alert);
            alert.AddAction(UIAlertAction.Create("OK", UIAlertActionStyle.Default, null));
            PresentViewController(alert, true, null);
        }
    }

    // Table view cell for Instagram users
    public sealed class InstagramUserCell : UITableViewCell
    {
        public const string CellId = "InstagramUserCell";
        private UILabel? _usernameLabel;
        private UIButton? _linkButton;

        public InstagramUserCell(IntPtr handle) : base(handle)
        {
            Initialize();
        }

        private void Initialize()
        {
            SelectionStyle = UITableViewCellSelectionStyle.None;
            BackgroundColor = UIColor.White;

            _usernameLabel = new UILabel
            {
                Font = UIFont.SystemFontOfSize(16, UIFontWeight.Medium),
                TextColor = UIColor.Black,
                Lines = 1,
                LineBreakMode = UILineBreakMode.TailTruncation,
                AdjustsFontSizeToFitWidth = false
            };
            ContentView.AddSubview(_usernameLabel);

            _linkButton = UIButton.FromType(UIButtonType.System);
            _linkButton.SetTitle("🔗", UIControlState.Normal);
            _linkButton.TitleLabel!.Font = UIFont.SystemFontOfSize(18);
            ContentView.AddSubview(_linkButton);
        }

        public void Configure(InstagramAnalysisService.InstagramUser user)
        {
            _usernameLabel!.Text = user.Username;

            _linkButton?.RemoveTarget(null, UIControlEvent.AllEvents);
            _linkButton?.AddTarget((_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(user.InstagramUrl))
                {
#pragma warning disable CA1422
                    UIApplication.SharedApplication.OpenUrl(new NSUrl(user.InstagramUrl), new UIApplicationOpenUrlOptions(), null);
#pragma warning restore CA1422
                }
            }, UIControlEvent.TouchUpInside);
        }

        public override void LayoutSubviews()
        {
            base.LayoutSubviews();

            nfloat padding = 16;
            nfloat linkButtonSize = 40;

            _linkButton!.Frame = new CGRect(
                ContentView.Bounds.Width - linkButtonSize - padding,
                (ContentView.Bounds.Height - linkButtonSize) / 2,
                linkButtonSize,
                linkButtonSize);

            _usernameLabel!.Frame = new CGRect(
                padding,
                0,
                ContentView.Bounds.Width - linkButtonSize - (padding * 2),
                ContentView.Bounds.Height);
        }
    }

    // Table view data source
    public class InstagramTableSource : UITableViewDataSource
    {
        private List<InstagramAnalysisService.InstagramUser> _users;

        public InstagramTableSource(List<InstagramAnalysisService.InstagramUser> users)
        {
            _users = users;
        }

        public void UpdateUsers(List<InstagramAnalysisService.InstagramUser> users)
        {
            _users = users;
        }

        public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
        {
            var cell = tableView.DequeueReusableCell(InstagramUserCell.CellId, indexPath) as InstagramUserCell
                ?? new InstagramUserCell(IntPtr.Zero);

            if (indexPath.Row < _users.Count)
            {
                cell.Configure(_users[indexPath.Row]);
            }

            return cell;
        }

        public override nint RowsInSection(UITableView tableView, nint section)
        {
            return _users.Count;
        }

        public override nint NumberOfSections(UITableView tableView)
        {
            return 1;
        }
    }

    // Table view delegate
    public class InstagramTableDelegate : UITableViewDelegate
    {
        // Remove GetHeightForRow to use automatic row height estimation
    }
}
