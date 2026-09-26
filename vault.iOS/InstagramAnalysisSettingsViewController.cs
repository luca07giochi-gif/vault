using System;
using System.Collections.Generic;
using CoreGraphics;
using Foundation;
using UIKit;

namespace vault.iOS
{
    public sealed class InstagramAnalysisSettingsViewController : UIViewController
    {
        private readonly List<string> _usernames = new();
        private UITextField? _usernameField;
        private UITableView? _tableView;
        private UILabel? _emptyLabel;

        public override void ViewDidLoad()
        {
            base.ViewDidLoad();

            Title = "Esclusioni Instagram";
            View!.BackgroundColor = UIColor.White;
            NavigationItem.LeftBarButtonItem = new UIBarButtonItem(
                UIBarButtonSystemItem.Done,
                (_, _) => NavigationController?.PopViewController(true));

            _usernameField = new UITextField
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                BorderStyle = UITextBorderStyle.RoundedRect,
                Placeholder = "Nome utente Instagram",
                AutocapitalizationType = UITextAutocapitalizationType.None,
                AutocorrectionType = UITextAutocorrectionType.No,
                KeyboardType = UIKeyboardType.ASCIICapable,
                ReturnKeyType = UIReturnKeyType.Done,
                ClearButtonMode = UITextFieldViewMode.WhileEditing
            };
            _usernameField.ShouldReturn += _ =>
            {
                AddUsername();
                return true;
            };

            UIButton addButton = UIButton.FromType(UIButtonType.System);
            addButton.TranslatesAutoresizingMaskIntoConstraints = false;
            addButton.SetTitle("Aggiungi", UIControlState.Normal);
            addButton.TitleLabel!.Font = UIFont.SystemFontOfSize(16, UIFontWeight.Semibold);
            addButton.TouchUpInside += (_, _) => AddUsername();

            UIStackView entryRow = new()
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                Axis = UILayoutConstraintAxis.Horizontal,
                Alignment = UIStackViewAlignment.Center,
                Spacing = 10
            };
            entryRow.AddArrangedSubview(_usernameField);
            entryRow.AddArrangedSubview(addButton);

            UILabel note = new()
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                Text = "Gli username inseriti vengono esclusi da Seguiti e Non ricambiano. Scorri a sinistra su una riga per rimuoverla.",
                Font = UIFont.SystemFontOfSize(14),
                TextColor = UIColor.DarkGray,
                Lines = 0
            };

            _tableView = new UITableView(CGRect.Empty, UITableViewStyle.Plain)
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                DataSource = new ExcludedUsernamesDataSource(_usernames, RemoveUsername),
                RowHeight = 52,
                TableFooterView = new UIView(CGRect.Empty)
            };

            _emptyLabel = new UILabel
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                Text = "Non hai ancora aggiunto username da escludere.",
                TextAlignment = UITextAlignment.Center,
                TextColor = UIColor.DarkGray,
                Lines = 0,
                Font = UIFont.SystemFontOfSize(15)
            };

            View.AddSubviews(entryRow, note, _tableView, _emptyLabel);
            NSLayoutConstraint.ActivateConstraints(new NSLayoutConstraint[]
            {
                entryRow.TopAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.TopAnchor, 16),
                entryRow.LeadingAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.LeadingAnchor, 16),
                entryRow.TrailingAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.TrailingAnchor, -16),
                _usernameField.HeightAnchor.ConstraintEqualTo(42),
                addButton.WidthAnchor.ConstraintGreaterThanOrEqualTo(76),
                note.TopAnchor.ConstraintEqualTo(entryRow.BottomAnchor, 10),
                note.LeadingAnchor.ConstraintEqualTo(entryRow.LeadingAnchor),
                note.TrailingAnchor.ConstraintEqualTo(entryRow.TrailingAnchor),
                _tableView.TopAnchor.ConstraintEqualTo(note.BottomAnchor, 12),
                _tableView.LeadingAnchor.ConstraintEqualTo(View.LeadingAnchor),
                _tableView.TrailingAnchor.ConstraintEqualTo(View.TrailingAnchor),
                _tableView.BottomAnchor.ConstraintEqualTo(View.BottomAnchor),
                _emptyLabel.TopAnchor.ConstraintEqualTo(note.BottomAnchor, 30),
                _emptyLabel.LeadingAnchor.ConstraintEqualTo(entryRow.LeadingAnchor),
                _emptyLabel.TrailingAnchor.ConstraintEqualTo(entryRow.TrailingAnchor)
            });

            ReloadUsernames();
        }

        private void AddUsername()
        {
            if (_usernameField == null)
                return;

            try
            {
                InstagramExcludedUserStore.Add(_usernameField.Text ?? string.Empty);
                _usernameField.Text = string.Empty;
                _usernameField.ResignFirstResponder();
                ReloadUsernames();
            }
            catch (Exception ex)
            {
                ShowMessage("Impossibile aggiungere lo username", ex.Message);
            }
        }

        private void RemoveUsername(string username)
        {
            try
            {
                InstagramExcludedUserStore.Remove(username);
                ReloadUsernames();
            }
            catch (Exception ex)
            {
                ShowMessage("Impossibile rimuovere lo username", ex.Message);
            }
        }

        private void ReloadUsernames()
        {
            try
            {
                _usernames.Clear();
                _usernames.AddRange(InstagramExcludedUserStore.Load());
                _tableView?.ReloadData();
                if (_emptyLabel != null)
                    _emptyLabel.Hidden = _usernames.Count > 0;
            }
            catch (Exception ex)
            {
                ShowMessage("Impossibile leggere le impostazioni", ex.Message);
            }
        }

        private void ShowMessage(string title, string message)
        {
            UIAlertController alert = UIAlertController.Create(title, message, UIAlertControllerStyle.Alert);
            alert.AddAction(UIAlertAction.Create("OK", UIAlertActionStyle.Default, null));
            PresentViewController(alert, true, null);
        }
    }

    internal sealed class ExcludedUsernamesDataSource : UITableViewDataSource
    {
        private readonly IReadOnlyList<string> _usernames;
        private readonly Action<string> _removeUsername;

        public ExcludedUsernamesDataSource(IReadOnlyList<string> usernames, Action<string> removeUsername)
        {
            _usernames = usernames;
            _removeUsername = removeUsername;
        }

        public override nint NumberOfSections(UITableView tableView) => 1;

        public override nint RowsInSection(UITableView tableView, nint section) => _usernames.Count;

        public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
        {
            const string cellId = "InstagramExcludedUsername";
            UITableViewCell cell = tableView.DequeueReusableCell(cellId)
                ?? new UITableViewCell(UITableViewCellStyle.Default, cellId);
            UIListContentConfiguration content = cell.DefaultContentConfiguration;
            content.Text = "@" + _usernames[indexPath.Row];
            cell.ContentConfiguration = content;
            cell.SelectionStyle = UITableViewCellSelectionStyle.None;
            return cell;
        }

        public override bool CanEditRow(UITableView tableView, NSIndexPath indexPath) => true;

        public override void CommitEditingStyle(
            UITableView tableView,
            UITableViewCellEditingStyle editingStyle,
            NSIndexPath indexPath)
        {
            if (editingStyle != UITableViewCellEditingStyle.Delete ||
                indexPath.Row < 0 || indexPath.Row >= _usernames.Count)
            {
                return;
            }

            _removeUsername(_usernames[indexPath.Row]);
        }
    }
}
