using System;
using System.Collections.Generic;
using CoreGraphics;
using Foundation;
using UIKit;

namespace vault.iOS
{
    public sealed class InstagramAnalysisViewController : UIViewController
    {
        private UISegmentedControl? _segmentControl;
        private UITableView? _tableView;
        private UILabel? _emptyLabel;
        private UIActivityIndicatorView? _loadingIndicator;
        private UIButton? _importButton;
        private InstagramTableSource? _tableSource;
        private readonly InstagramAnalysisService _analysisService = new();
        private InstagramDocumentPickerDelegate? _documentPickerDelegate;
        private UIDocumentPickerViewController? _documentPicker;

        private List<InstagramAnalysisService.InstagramUser> _followers = new();
        private List<InstagramAnalysisService.InstagramUser> _following = new();
        private List<InstagramAnalysisService.InstagramUser> _notFollowingBack = new();

        public override void ViewDidLoad()
        {
            base.ViewDidLoad();

            Title = "Analisi Instagram";
            View!.BackgroundColor = UIColor.White;
            NavigationItem.LeftBarButtonItem = new UIBarButtonItem(
                UIBarButtonSystemItem.Done,
                (_, _) => NavigationController?.PopViewController(true));

            _importButton = UIButton.FromType(UIButtonType.System);
            _importButton.SetTitle("Importa dati", UIControlState.Normal);
            _importButton.TitleLabel!.Font = UIFont.SystemFontOfSize(16, UIFontWeight.Semibold);
            _importButton.SetTitleColor(UIColor.FromRGB(10, 132, 255), UIControlState.Normal);
            _importButton.TouchUpInside += (_, _) => PickDataFile();

            _segmentControl = new UISegmentedControl(new[] { "Followers", "Seguiti", "Non ricambiano" })
            {
                SelectedSegment = 0,
                Enabled = false
            };
            _segmentControl.ValueChanged += (_, _) => OnSegmentChanged();

            var headerView = new UIView
            {
                BackgroundColor = UIColor.FromRGB(240, 240, 240),
                TranslatesAutoresizingMaskIntoConstraints = false
            };
            headerView.AddSubview(_importButton);
            headerView.AddSubview(_segmentControl);
            View.AddSubview(headerView);

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

            _tableSource = new InstagramTableSource(_followers);
            _tableView = new UITableView(CGRect.Empty, UITableViewStyle.Plain)
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                Delegate = new InstagramTableDelegate(),
                DataSource = _tableSource,
                Hidden = true,
                RowHeight = 60
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
            _documentPicker = new UIDocumentPickerViewController(
                new[] { "com.pkware.zip-archive" }, UIDocumentPickerMode.Import)
            {
                AllowsMultipleSelection = false
            };

            _documentPickerDelegate = new InstagramDocumentPickerDelegate(ProcessPickedDocuments);
            _documentPicker.Delegate = _documentPickerDelegate;
            PresentViewController(_documentPicker, true, null);
#pragma warning restore CA1422
        }

        private void ProcessPickedDocuments(NSUrl[] urls)
        {
            if (urls.Length > 0)
                _ = ProcessInstagramDataAsync(urls[0]);
        }

        private async System.Threading.Tasks.Task ProcessInstagramDataAsync(NSUrl fileUrl)
        {
            SetLoading(true);
            try
            {
                var result = await _analysisService.AnalyzeFromZipAsync(fileUrl);
                BeginInvokeOnMainThread(() =>
                {
                    _followers = result.Followers;
                    _following = result.Following;
                    _notFollowingBack = result.NotFollowingBack;

                    if (_followers.Count == 0 && _following.Count == 0)
                    {
                        ShowNotification("Nessun dato trovato", "Non sono stati trovati dati Instagram validi nel file selezionato.");
                        return;
                    }

                    if (_segmentControl != null)
                    {
                        _segmentControl.Enabled = true;
                        _segmentControl.SelectedSegment = 0;
                    }

                    if (_tableView != null)
                        _tableView.Hidden = false;
                    if (_emptyLabel != null)
                        _emptyLabel.Hidden = true;

                    OnSegmentChanged();
                });
            }
            catch (Exception ex)
            {
                BeginInvokeOnMainThread(() =>
                    ShowNotification("Errore", $"Non è stato possibile analizzare il file: {ex.Message}"));
            }
            finally
            {
                SetLoading(false);
            }
        }

        private void SetLoading(bool isLoading)
        {
            BeginInvokeOnMainThread(() =>
            {
                if (_loadingIndicator != null)
                {
                    if (isLoading)
                        _loadingIndicator.StartAnimating();
                    else
                        _loadingIndicator.StopAnimating();
                    _loadingIndicator.Hidden = !isLoading;
                }

                if (_importButton != null)
                    _importButton.Enabled = !isLoading;
            });
        }

        private void OnSegmentChanged()
        {
            if (_segmentControl == null || _tableView == null || _tableSource == null)
                return;

            List<InstagramAnalysisService.InstagramUser> users = _segmentControl.SelectedSegment switch
            {
                0 => _followers,
                1 => _following,
                2 => _notFollowingBack,
                _ => new List<InstagramAnalysisService.InstagramUser>()
            };

            _tableSource.UpdateUsers(users);
            _tableView.ReloadData();
        }

        private void ShowNotification(string title, string message)
        {
            var alert = UIAlertController.Create(title, message, UIAlertControllerStyle.Alert);
            alert.AddAction(UIAlertAction.Create("OK", UIAlertActionStyle.Default, null));
            PresentViewController(alert, true, null);
        }
    }

    internal sealed class InstagramDocumentPickerDelegate : UIDocumentPickerDelegate
    {
        private readonly Action<NSUrl[]> _onPicked;

        public InstagramDocumentPickerDelegate(Action<NSUrl[]> onPicked)
        {
            _onPicked = onPicked;
        }

        public override void DidPickDocument(UIDocumentPickerViewController controller, NSUrl url)
        {
            NotifyPicked(controller, new[] { url });
        }

        public override void DidPickDocument(UIDocumentPickerViewController controller, NSUrl[] urls)
        {
            NotifyPicked(controller, urls ?? Array.Empty<NSUrl>());
        }

        public override void WasCancelled(UIDocumentPickerViewController controller)
        {
            controller.DismissViewController(true, null);
        }

        private void NotifyPicked(UIDocumentPickerViewController controller, NSUrl[] urls)
        {
            controller.DismissViewController(true, () =>
                UIApplication.SharedApplication.BeginInvokeOnMainThread(() => _onPicked(urls)));
        }
    }

    public sealed class InstagramUserCell : UITableViewCell
    {
        public const string CellId = "InstagramUserCell";

        private UILabel? _usernameLabel;
        private UIButton? _linkButton;
        private string? _currentUrl;

        public InstagramUserCell(IntPtr handle) : base(handle)
        {
            Initialize();
        }

        public InstagramUserCell(UITableViewCellStyle style, string reuseIdentifier) : base(style, reuseIdentifier)
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
            UIImage? linkImage = UIImage.GetSystemImage("link");
            if (linkImage != null)
                _linkButton.SetImage(linkImage, UIControlState.Normal);
            else
                _linkButton.SetTitle("\U0001F517", UIControlState.Normal);
            _linkButton.AccessibilityLabel = "Apri profilo Instagram";
            _linkButton.TouchUpInside += OpenProfile;
            ContentView.AddSubview(_linkButton);
        }

        public void Configure(InstagramAnalysisService.InstagramUser user)
        {
            if (_usernameLabel != null)
                _usernameLabel.Text = user.Username;
            _currentUrl = user.InstagramUrl;
        }

        private void OpenProfile(object? sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_currentUrl))
                return;

            NSUrl? url = NSUrl.FromString(_currentUrl);
            if (url == null)
                return;

#pragma warning disable CA1422
            UIApplication.SharedApplication.OpenUrl(url);
#pragma warning restore CA1422
        }

        public override void LayoutSubviews()
        {
            base.LayoutSubviews();

            nfloat padding = 16;
            nfloat linkButtonSize = 44;
            if (_linkButton != null)
            {
                _linkButton.Frame = new CGRect(
                    ContentView.Bounds.Width - linkButtonSize - padding,
                    (ContentView.Bounds.Height - linkButtonSize) / 2,
                    linkButtonSize,
                    linkButtonSize);
            }

            if (_usernameLabel != null)
            {
                _usernameLabel.Frame = new CGRect(
                    padding,
                    0,
                    ContentView.Bounds.Width - linkButtonSize - (padding * 2),
                    ContentView.Bounds.Height);
            }
        }
    }

    public sealed class InstagramTableSource : UITableViewDataSource
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
                ?? new InstagramUserCell(UITableViewCellStyle.Default, InstagramUserCell.CellId);

            if (indexPath.Row >= 0 && indexPath.Row < _users.Count)
                cell.Configure(_users[indexPath.Row]);
            else
                cell.Configure(new InstagramAnalysisService.InstagramUser());

            return cell;
        }

        public override nint RowsInSection(UITableView tableView, nint section) => _users.Count;

        public override nint NumberOfSections(UITableView tableView) => 1;
    }

    public sealed class InstagramTableDelegate : UITableViewDelegate
    {
    }
}
