using System;
using System.Collections.Generic;
using CoreGraphics;
using Foundation;
using UIKit;

namespace vault.iOS
{
    public sealed class InstagramAnalysisViewController : UIViewController, IUIDocumentPickerDelegate
    {
        private UISegmentedControl? _segmentControl;
        private UITableView? _tableView;
        private UILabel? _emptyLabel;
        private UIActivityIndicatorView? _loadingIndicator;
        private UIButton? _importButton;

        private List<InstagramAnalysisService.InstagramUser> _followers = new();
        private List<InstagramAnalysisService.InstagramUser> _following = new();
        private List<InstagramAnalysisService.InstagramUser> _notFollowingBack = new();
        
        private InstagramAnalysisService _analysisService = new();
        private UIDocumentPickerViewController? _documentPicker;

        public override void ViewDidLoad()
        {
            base.ViewDidLoad();

            Title = "Analisi Instagram";
            View!.BackgroundColor = UIColor.White;

            // Setup navigation - remove Done button since we're in a navigation controller
            NavigationItem.LeftBarButtonItem = new UIBarButtonItem(
                UIBarButtonSystemItem.Done,
                (_, _) => NavigationController?.PopViewController(true));

            // Setup import button
            _importButton = UIButton.FromType(UIButtonType.System);
            _importButton.SetTitle("Importa dati", UIControlState.Normal);
            _importButton.TitleLabel!.Font = UIFont.SystemFontOfSize(16, UIFontWeight.Semibold);
            _importButton.SetTitleColor(UIColor.FromRGB(10, 132, 255), UIControlState.Normal);
            _importButton.TouchUpInside += (_, _) => PickDataFile();

            // Setup segment control for switching between lists
            _segmentControl = new UISegmentedControl(new[] { "Followers", "Seguiti", "Non mi seguono" })
            {
                SelectedSegment = 0,
                Enabled = false
            };
            _segmentControl.ValueChanged += (_, _) => OnSegmentChanged();

            // Create header view with proper spacing from navigation bar
            var headerView = new UIView
            {
                BackgroundColor = UIColor.FromRGB(240, 240, 240),
                TranslatesAutoresizingMaskIntoConstraints = false
            };

            headerView.AddSubview(_importButton);
            headerView.AddSubview(_segmentControl);

            View.AddSubview(headerView);

            // Setup constraints for header
            _importButton.TranslatesAutoresizingMaskIntoConstraints = false;
            _segmentControl.TranslatesAutoresizingMaskIntoConstraints = false;

            NSLayoutConstraint.ActivateConstraints(new NSLayoutConstraint[]
            {
                headerView.TopAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.TopAnchor),
                headerView.LeadingAnchor.ConstraintEqualTo(View.LeadingAnchor),
                headerView.TrailingAnchor.ConstraintEqualTo(View.TrailingAnchor),
                headerView.HeightAnchor.ConstraintEqualTo(90),

                _importButton.TopAnchor.ConstraintEqualTo(headerView.TopAnchor, 8),
                _importButton.LeadingAnchor.ConstraintEqualTo(headerView.LeadingAnchor, 10),
                _importButton.TrailingAnchor.ConstraintEqualTo(headerView.TrailingAnchor, -10),
                _importButton.HeightAnchor.ConstraintEqualTo(36),

                _segmentControl.TopAnchor.ConstraintEqualTo(_importButton.BottomAnchor, 6),
                _segmentControl.LeadingAnchor.ConstraintEqualTo(headerView.LeadingAnchor, 10),
                _segmentControl.TrailingAnchor.ConstraintEqualTo(headerView.TrailingAnchor, -10),
                _segmentControl.HeightAnchor.ConstraintEqualTo(34)
            });

            // Setup table view with optimizations for large datasets
            _tableView = new UITableView(CGRect.Empty, UITableViewStyle.Plain)
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                Delegate = new InstagramTableDelegate(),
                DataSource = new InstagramTableSource(_followers),
                Hidden = true,
                EstimatedRowHeight = 60,
                RowHeight = UITableView.AutomaticDimension
            };
            _tableView.RegisterClassForCellReuse(typeof(InstagramUserCell), InstagramUserCell.CellId);
            _tableView.SeparatorStyle = UITableViewCellSeparatorStyle.SingleLine;
            _tableView.SeparatorInset = UIEdgeInsets.Zero;
            View.AddSubview(_tableView);

            NSLayoutConstraint.ActivateConstraints(new NSLayoutConstraint[]
            {
                _tableView.TopAnchor.ConstraintEqualTo(headerView.BottomAnchor),
                _tableView.LeadingAnchor.ConstraintEqualTo(View.LeadingAnchor),
                _tableView.TrailingAnchor.ConstraintEqualTo(View.TrailingAnchor),
                _tableView.BottomAnchor.ConstraintEqualTo(View.BottomAnchor)
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
                _emptyLabel.TopAnchor.ConstraintEqualTo(headerView.BottomAnchor),
                _emptyLabel.LeadingAnchor.ConstraintEqualTo(View.LeadingAnchor),
                _emptyLabel.TrailingAnchor.ConstraintEqualTo(View.TrailingAnchor),
                _emptyLabel.BottomAnchor.ConstraintEqualTo(View.BottomAnchor)
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
                        _segmentControl!.Enabled = true;
                        _emptyLabel!.Hidden = true;
                        _tableView!.Hidden = false;
                        OnSegmentChanged();
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

        private void OnSegmentChanged()
        {
            if (_segmentControl == null || _tableView == null)
                return;

            // Assign the appropriate list directly instead of modifying the existing one
            List<InstagramAnalysisService.InstagramUser> newList;
            switch (_segmentControl.SelectedSegment)
            {
                case 0: // Followers
                    newList = _followers;
                    break;
                case 1: // Following
                    newList = _following;
                    break;
                case 2: // Not following back
                    newList = _notFollowingBack;
                    break;
                default:
                    return;
            }

            // Update the data source with the new list
            if (_tableView.DataSource is InstagramTableSource dataSource)
            {
                dataSource.UpdateUsers(newList);
            }

            _tableView.ReloadData();
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
                TextColor = UIColor.Black
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
            nfloat linkButtonSize = 44;

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
