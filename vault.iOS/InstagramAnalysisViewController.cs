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
        private InstagramListDataSource? _tableSource;

        public override void ViewDidLoad()
        {
            base.ViewDidLoad();

            Title = "Analisi Instagram";
            View!.BackgroundColor = UIColor.SystemBackground;
            EdgesForExtendedLayout = UIRectEdge.None;
            if (NavigationController != null)
            {
                NavigationController.NavigationBar.Hidden = true;
            }
            NavigationItem.HidesBackButton = true;

            SetupBottomTabBar();

            _tableView = new UITableView(CGRect.Empty, UITableViewStyle.Plain)
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                Hidden = true,
                EstimatedRowHeight = 56,
                RowHeight = 56,
                SeparatorStyle = UITableViewCellSeparatorStyle.SingleLine,
                SeparatorInset = UIEdgeInsets.Zero,
                SeparatorColor = UIColor.Separator,
                BackgroundColor = UIColor.SystemBackground
            };

            _tableSource = new InstagramListDataSource(_currentList);
            _tableView.DataSource = _tableSource;
            _tableView.Delegate = new InstagramListDelegate(this);
            View.AddSubview(_tableView);

            NSLayoutConstraint.ActivateConstraints(new NSLayoutConstraint[]
            {
                _tableView.TopAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.TopAnchor, 8),
                _tableView.LeadingAnchor.ConstraintEqualTo(View.LeadingAnchor),
                _tableView.TrailingAnchor.ConstraintEqualTo(View.TrailingAnchor),
                _tableView.BottomAnchor.ConstraintEqualTo(_bottomTabBar!.TopAnchor)
            });

            _emptyLabel = new UILabel
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                TextAlignment = UITextAlignment.Center,
                TextColor = UIColor.SecondaryLabel,
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

        public override void ViewWillAppear(bool animated)
        {
            base.ViewWillAppear(animated);
            NavigationController?.SetNavigationBarHidden(true, animated);
        }

        public override void ViewWillDisappear(bool animated)
        {
            if (NavigationController != null)
            {
                NavigationController.SetNavigationBarHidden(false, animated);
            }

            base.ViewWillDisappear(animated);
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
                BarTintColor = UIColor.SystemBackground,
                TintColor = UIColor.SystemBlue,
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

            _tableSource?.UpdateUsers(_currentList);
            _tableView?.ReloadData();

            if (_tableView != null && _currentList.Count > 0)
            {
                _tableView.SetContentOffset(CGPoint.Empty, false);
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
                        UpdateList(_followers);
                    }
                    else
                    {
                        _emptyLabel!.Hidden = false;
                        _tableView!.Hidden = true;
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

        private void OpenInstagramProfile(string? username)
        {
            if (string.IsNullOrWhiteSpace(username))
                return;

            string url = $"https://www.instagram.com/{username}/";
#pragma warning disable CA1422
            UIApplication.SharedApplication.OpenUrl(new NSUrl(url), new UIApplicationOpenUrlOptions(), null);
#pragma warning restore CA1422
        }

        private sealed class InstagramListDelegate : UITableViewDelegate
        {
            private readonly InstagramAnalysisViewController _owner;

            public InstagramListDelegate(InstagramAnalysisViewController owner)
            {
                _owner = owner;
            }

            public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
            {
                if (indexPath.Row >= 0 && indexPath.Row < _owner._currentList.Count)
                {
                    _owner.OpenInstagramProfile(_owner._currentList[indexPath.Row].Username);
                }

                tableView.DeselectRow(indexPath, true);
            }
        }
    }

    public class InstagramListDataSource : UITableViewDataSource
    {
        private readonly List<InstagramAnalysisService.InstagramUser> _users = new();

        public InstagramListDataSource(List<InstagramAnalysisService.InstagramUser> users)
        {
            _users.AddRange(users);
        }

        public void UpdateUsers(List<InstagramAnalysisService.InstagramUser> users)
        {
            _users.Clear();
            _users.AddRange(users);
        }

        public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
        {
            const string cellId = "InstagramSimpleCell";
            var cell = tableView.DequeueReusableCell(cellId) ?? new UITableViewCell(UITableViewCellStyle.Default, cellId);
            string username = indexPath.Row < _users.Count ? _users[indexPath.Row].Username : string.Empty;
            cell.TextLabel!.Text = username;
            cell.TextLabel.Font = UIFont.SystemFontOfSize(16, UIFontWeight.Medium);
            cell.TextLabel.TextColor = UIColor.Label;
            cell.SelectionStyle = UITableViewCellSelectionStyle.Default;
            cell.BackgroundColor = UIColor.SystemBackground;

            var linkButton = new UIButton(UIButtonType.System)
            {
                Frame = new CGRect(0, 0, 32, 32)
            };
            linkButton.SetImage(UIImage.GetSystemImage("link"), UIControlState.Normal);
            linkButton.TintColor = UIColor.SystemBlue;
            linkButton.TouchUpInside += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(username))
                {
#pragma warning disable CA1422
                    UIApplication.SharedApplication.OpenUrl(new NSUrl($"https://www.instagram.com/{username}/?hl=it"), new UIApplicationOpenUrlOptions(), null);
#pragma warning restore CA1422
                }
            };

            cell.Accessory = UITableViewCellAccessory.None;
            cell.AccessoryView = linkButton;
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
}
