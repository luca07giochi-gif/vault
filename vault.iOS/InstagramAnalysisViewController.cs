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
        private List<InstagramAnalysisService.InstagramUser> _currentList = new();
        
        private InstagramAnalysisService _analysisService = new();
        private UIDocumentPickerViewController? _documentPicker;

        public override void ViewDidLoad()
        {
            base.ViewDidLoad();

            Title = "Analisi Instagram";
            View.BackgroundColor = UIColor.White;

            // Setup navigation
            NavigationItem.LeftBarButtonItem = new UIBarButtonItem(
                UIBarButtonSystemItem.Done,
                (_, _) => DismissViewController(true, null));

            // Setup import button
            _importButton = UIButton.FromType(UIButtonType.System);
            _importButton.SetTitle("Importa dati", UIControlState.Normal);
            _importButton.TitleLabel!.Font = UIFont.SystemFontOfSize(16, UIFontWeight.Semibold);
            _importButton.SetTitleColor(UIColor.FromRGB(10, 132, 255), UIControlState.Normal);
            _importButton.TouchUpInside += (_, _) => PickDataFile();

            var importContainer = new UIView { BackgroundColor = UIColor.FromRGB(240, 240, 240) };
            importContainer.AddSubview(_importButton);

            // Setup segment control for switching between lists
            _segmentControl = new UISegmentedControl(new[] { "Followers", "Seguiti", "Non mi seguono" })
            {
                SelectedSegment = 0,
                Enabled = false
            };
            _segmentControl.ValueChanged += (_, _) => OnSegmentChanged();

            var headerView = new UIView(new CGRect(0, 0, View.Bounds.Width, 90))
            {
                BackgroundColor = UIColor.FromRGB(240, 240, 240)
            };
            _importButton.Frame = new CGRect(10, 8, View.Bounds.Width - 20, 36);
            _importButton.AutoresizingMask = UIViewAutoresizing.FlexibleWidth;
            headerView.AddSubview(_importButton);

            _segmentControl.Frame = new CGRect(10, 50, View.Bounds.Width - 20, 34);
            _segmentControl.AutoresizingMask = UIViewAutoresizing.FlexibleWidth;
            headerView.AddSubview(_segmentControl);
            headerView.AutoresizingMask = UIViewAutoresizing.FlexibleWidth;

            View.AddSubview(headerView);

            // Setup table view
            _tableView = new UITableView(new CGRect(0, 90, View.Bounds.Width, View.Bounds.Height - 90), UITableViewStyle.Plain)
            {
                AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight,
                Delegate = new InstagramTableDelegate(),
                DataSource = new InstagramTableSource(_currentList),
                Hidden = true
            };
            _tableView.RegisterClassForCellReuse(typeof(InstagramUserCell), InstagramUserCell.CellId);
            _tableView.RowHeight = 60;
            View.AddSubview(_tableView);

            // Setup empty label
            _emptyLabel = new UILabel(new CGRect(0, 90, View.Bounds.Width, View.Bounds.Height - 90))
            {
                AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight,
                TextAlignment = UITextAlignment.Center,
                TextColor = UIColor.DarkGray,
                Text = "Importa i dati di Instagram per iniziare l'analisi",
                Lines = 2,
                Font = UIFont.SystemFontOfSize(16)
            };
            View.AddSubview(_emptyLabel);

            // Setup loading indicator
            _loadingIndicator = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Large)
            {
                Center = new CGPoint(View.Bounds.GetMidX(), View.Bounds.GetMidY()),
                AutoresizingMask = UIViewAutoresizing.FlexibleTopMargin | UIViewAutoresizing.FlexibleBottomMargin |
                                   UIViewAutoresizing.FlexibleLeftMargin | UIViewAutoresizing.FlexibleRightMargin,
                Hidden = true
            };
            View.AddSubview(_loadingIndicator);
        }

        private void PickDataFile()
        {
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
                _loadingIndicator!.StartAnimating();
                _loadingIndicator.Hidden = false;
            });

            try
            {
                // For now, show a message that data import is being prepared
                BeginInvokeOnMainThread(() =>
                {
                    _loadingIndicator!.StopAnimating();
                    _loadingIndicator.Hidden = true;
                    ShowNotification("Importazione non ancora implementata", "L'app è pronta per l'importazione dei dati di Instagram dal tuo download dati.");
                });
            }
            catch (Exception ex)
            {
                BeginInvokeOnMainThread(() =>
                {
                    _loadingIndicator!.StopAnimating();
                    _loadingIndicator.Hidden = true;
                    ShowNotification("Errore", $"Si è verificato un errore: {ex.Message}");
                });
            }
        }

        private void OnSegmentChanged()
        {
            if (_segmentControl == null || _tableView == null)
                return;

            _currentList.Clear();

            switch (_segmentControl.SelectedSegment)
            {
                case 0: // Followers
                    _currentList.AddRange(_followers);
                    break;
                case 1: // Following
                    _currentList.AddRange(_following);
                    break;
                case 2: // Not following back
                    _currentList.AddRange(_notFollowingBack);
                    break;
            }

            _tableView.ReloadData();
        }

        private void ShowNotification(string title, string message)
        {
            var alert = UIAlertController.Create(title, message, UIAlertControllerStyle.Alert);
            alert.AddAction(UIAlertAction.Create("OK", UIAlertActionStyle.Default, null));
            PresentViewController(alert, true, null);
        }

        public override void ViewWillLayoutSubviews()
        {
            base.ViewWillLayoutSubviews();

            if (_segmentControl != null && _importButton != null)
            {
                _importButton.Frame = new CGRect(10, 8, View.Bounds.Width - 20, 36);
                _segmentControl.Frame = new CGRect(10, 50, View.Bounds.Width - 20, 34);
            }
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
                    UIApplication.SharedApplication.OpenUrl(new NSUrl(user.InstagramUrl));
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
        private readonly List<InstagramAnalysisService.InstagramUser> _users;

        public InstagramTableSource(List<InstagramAnalysisService.InstagramUser> users)
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
        public override nfloat GetHeightForRow(UITableView tableView, NSIndexPath indexPath)
        {
            return 60;
        }
    }
}
