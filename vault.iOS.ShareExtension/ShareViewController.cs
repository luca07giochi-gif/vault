using CoreGraphics;
using Foundation;
using UIKit;
using vault.iOS.Shared;

namespace vault.iOS.ShareExtension
{
    [Register("ShareViewController")]
    public sealed class ShareViewController : UIViewController
    {
        private const string CellId = "RecentVaultCell";

        private readonly List<RecentVaultRecord> _recentVaults = new();
        private UILabel? _summaryLabel;
        private UILabel? _emptyLabel;
        private UITableView? _tableView;
        private UIButton? _confirmButton;
        private UIButton? _cancelButton;
        private UIActivityIndicatorView? _busyIndicator;
        private UILabel? _busyLabel;
        private string? _selectedVaultId;
        private SharedVaultQueueStore? _store;
        private bool _isBusy;

        public override void ViewDidLoad()
        {
            base.ViewDidLoad();

            BuildUi();
            LoadRecentVaults();
        }

        private void BuildUi()
        {
            View!.BackgroundColor = UIColor.SystemBackgroundColor;

            UILabel titleLabel = new()
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                Text = "Invia a Vault",
                Font = UIFont.BoldSystemFontOfSize(22f),
                TextColor = UIColor.LabelColor
            };

            _summaryLabel = new UILabel
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                Lines = 0,
                Font = UIFont.SystemFontOfSize(15f),
                TextColor = UIColor.SecondaryLabelColor
            };

            _emptyLabel = new UILabel
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                Lines = 0,
                Font = UIFont.SystemFontOfSize(15f),
                TextAlignment = UITextAlignment.Center,
                TextColor = UIColor.SecondaryLabelColor,
                Hidden = true
            };

            _tableView = new UITableView(CGRect.Empty, UITableViewStyle.InsetGrouped)
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                Source = new RecentVaultSource(this)
            };

            _cancelButton = UIButton.FromType(UIButtonType.System);
            _cancelButton.TranslatesAutoresizingMaskIntoConstraints = false;
            _cancelButton.SetTitle("Annulla", UIControlState.Normal);
            _cancelButton.TouchUpInside += (_, _) => CancelAndClose();

            _confirmButton = UIButton.FromType(UIButtonType.System);
            _confirmButton.TranslatesAutoresizingMaskIntoConstraints = false;
            _confirmButton.SetTitle("Metti in coda", UIControlState.Normal);
            _confirmButton.TitleLabel!.Font = UIFont.BoldSystemFontOfSize(16f);
            _confirmButton.TouchUpInside += async (_, _) => await QueueIncomingFilesAsync();

            _busyIndicator = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Medium)
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                Hidden = true
            };

            _busyLabel = new UILabel
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                Font = UIFont.SystemFontOfSize(14f),
                TextColor = UIColor.SecondaryLabelColor,
                Hidden = true
            };

            UIStackView buttonStack = new()
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                Axis = UILayoutConstraintAxis.Horizontal,
                Distribution = UIStackViewDistribution.FillEqually,
                Spacing = 12f
            };
            buttonStack.AddArrangedSubview(_cancelButton);
            buttonStack.AddArrangedSubview(_confirmButton);

            View.AddSubview(titleLabel);
            View.AddSubview(_summaryLabel);
            View.AddSubview(_emptyLabel);
            View.AddSubview(_tableView);
            View.AddSubview(buttonStack);
            View.AddSubview(_busyIndicator);
            View.AddSubview(_busyLabel);

            NSLayoutConstraint.ActivateConstraints(new[]
            {
                titleLabel.TopAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.TopAnchor, 20f),
                titleLabel.LeadingAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.LeadingAnchor, 20f),
                titleLabel.TrailingAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.TrailingAnchor, -20f),

                _summaryLabel.TopAnchor.ConstraintEqualTo(titleLabel.BottomAnchor, 10f),
                _summaryLabel.LeadingAnchor.ConstraintEqualTo(titleLabel.LeadingAnchor),
                _summaryLabel.TrailingAnchor.ConstraintEqualTo(titleLabel.TrailingAnchor),

                _emptyLabel.TopAnchor.ConstraintEqualTo(_summaryLabel.BottomAnchor, 18f),
                _emptyLabel.LeadingAnchor.ConstraintEqualTo(titleLabel.LeadingAnchor),
                _emptyLabel.TrailingAnchor.ConstraintEqualTo(titleLabel.TrailingAnchor),

                _tableView.TopAnchor.ConstraintEqualTo(_summaryLabel.BottomAnchor, 16f),
                _tableView.LeadingAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.LeadingAnchor),
                _tableView.TrailingAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.TrailingAnchor),

                buttonStack.TopAnchor.ConstraintEqualTo(_tableView.BottomAnchor, 12f),
                buttonStack.LeadingAnchor.ConstraintEqualTo(titleLabel.LeadingAnchor),
                buttonStack.TrailingAnchor.ConstraintEqualTo(titleLabel.TrailingAnchor),
                buttonStack.BottomAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.BottomAnchor, -18f),
                buttonStack.HeightAnchor.ConstraintEqualTo(46f),

                _busyIndicator.TopAnchor.ConstraintEqualTo(buttonStack.BottomAnchor, 12f),
                _busyIndicator.CenterXAnchor.ConstraintEqualTo(View.CenterXAnchor),

                _busyLabel.TopAnchor.ConstraintEqualTo(_busyIndicator.BottomAnchor, 6f),
                _busyLabel.CenterXAnchor.ConstraintEqualTo(View.CenterXAnchor),
                _busyLabel.BottomAnchor.ConstraintLessThanOrEqualTo(View.SafeAreaLayoutGuide.BottomAnchor, -6f)
            });
        }

        private void LoadRecentVaults()
        {
            int providerCount = GetIncomingProviders().Count;
            if (_summaryLabel != null)
            {
                _summaryLabel.Text = providerCount switch
                {
                    <= 0 => "Nessun file rilevato nella condivisione.",
                    1 => "1 file pronto per essere aggiunto in coda.",
                    _ => $"{providerCount} file pronti per essere aggiunti in coda."
                };
            }

            SharedVaultQueueStore? store = GetStore(allowAlertOnFailure: true);
            if (store == null)
            {
                UpdateUiState();
                return;
            }

            _recentVaults.Clear();
            _recentVaults.AddRange(store.LoadRecentVaults());
            _selectedVaultId = _recentVaults.FirstOrDefault()?.VaultId;
            _tableView?.ReloadData();
            UpdateUiState();
        }

        private IReadOnlyList<NSItemProvider> GetIncomingProviders()
        {
            List<NSItemProvider> providers = new();
            NSExtensionContext? context = ExtensionContext;
            NSExtensionItem[]? inputItems = context?.InputItems?
                .OfType<NSExtensionItem>()
                .ToArray();

            if (inputItems == null)
                return providers;

            foreach (NSExtensionItem item in inputItems)
            {
                if (item.Attachments == null)
                    continue;

                providers.AddRange(item.Attachments.Where(provider => provider != null));
            }

            return providers;
        }

        private SharedVaultQueueStore? GetStore(bool allowAlertOnFailure)
        {
            if (_store != null)
                return _store;

            try
            {
                NSUrl? containerUrl = NSFileManager.DefaultManager.GetContainerUrl(AppGroupConfig.Identifier);
                string? rootPath = containerUrl?.Path;
                if (string.IsNullOrWhiteSpace(rootPath))
                    throw new InvalidOperationException("Contenitore condiviso non disponibile.");

                _store = new SharedVaultQueueStore(rootPath);
                return _store;
            }
            catch (Exception ex)
            {
                if (allowAlertOnFailure)
                    ShowError(ex.Message);
                return null;
            }
        }

        private async Task QueueIncomingFilesAsync()
        {
            if (_isBusy)
                return;

            SharedVaultQueueStore? store = GetStore(allowAlertOnFailure: true);
            if (store == null)
                return;

            IReadOnlyList<NSItemProvider> providers = GetIncomingProviders();
            if (providers.Count == 0)
            {
                ShowError("Nessun file da mettere in coda.");
                return;
            }

            RecentVaultRecord? selectedVault = _recentVaults.FirstOrDefault(vault =>
                string.Equals(vault.VaultId, _selectedVaultId, StringComparison.OrdinalIgnoreCase));
            if (selectedVault == null)
            {
                ShowError("Seleziona un vault recente.");
                return;
            }

            PendingImportJob job = store.CreatePendingJob(selectedVault.VaultId, selectedVault.DisplayName);
            SetBusy(true, "Salvataggio in coda...");

            try
            {
                foreach (NSItemProvider provider in providers)
                {
                    PendingImportItem item = await StageProviderAsync(store, job.JobId, provider);
                    job.Items.Add(item);
                }

                if (job.Items.Count == 0)
                    throw new InvalidOperationException("Nessun file valido da mettere in coda.");

                store.SavePendingJob(job);
                await CompleteAndCloseAsync();
            }
            catch (Exception ex)
            {
                try
                {
                    store.DeleteJobs(new[] { job.JobId });
                }
                catch
                {
                    // Best effort cleanup.
                }

                ShowError(ex.Message);
            }
            finally
            {
                SetBusy(false, string.Empty);
            }
        }

        private static Task<PendingImportItem> StageProviderAsync(SharedVaultQueueStore store, string jobId, NSItemProvider provider)
        {
            string typeIdentifier = SelectPreferredTypeIdentifier(provider);
            if (string.IsNullOrWhiteSpace(typeIdentifier))
                throw new InvalidOperationException("Tipo file non supportato.");

            var tcs = new TaskCompletionSource<PendingImportItem>(TaskCreationOptions.RunContinuationsAsynchronously);
            provider.LoadFileRepresentation(typeIdentifier, (url, error) =>
            {
                if (error != null)
                {
                    tcs.TrySetException(new NSErrorException(error));
                    return;
                }

                if (url == null)
                {
                    tcs.TrySetException(new InvalidOperationException("Impossibile leggere il file condiviso."));
                    return;
                }

                try
                {
                    string? sourcePath = url.Path;
                    if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                    {
                        tcs.TrySetException(new FileNotFoundException("Il file condiviso non e disponibile.", sourcePath));
                        return;
                    }

                    string stagedFileName = BuildPreferredFileName(provider, sourcePath, typeIdentifier);
                    string destinationPath = store.BuildUniqueStagedFilePath(jobId, stagedFileName);
                    File.Copy(sourcePath, destinationPath, overwrite: true);

                    FileInfo info = new(destinationPath);
                    tcs.TrySetResult(new PendingImportItem
                    {
                        ItemId = Guid.NewGuid().ToString("N"),
                        OriginalFileName = Path.GetFileName(destinationPath),
                        StagedRelativePath = store.GetRelativePathForJob(jobId, destinationPath),
                        ContentType = typeIdentifier,
                        FileSize = info.Exists ? info.Length : 0,
                        SourceHint = provider.SuggestedName
                    });
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            return tcs.Task;
        }

        private static string SelectPreferredTypeIdentifier(NSItemProvider provider)
        {
            string[] identifiers = provider.RegisteredTypeIdentifiers ?? Array.Empty<string>();
            return identifiers.FirstOrDefault(id =>
                       id.Contains("image", StringComparison.OrdinalIgnoreCase) ||
                       id.Contains("movie", StringComparison.OrdinalIgnoreCase) ||
                       id.Contains("video", StringComparison.OrdinalIgnoreCase) ||
                       id.Contains("pdf", StringComparison.OrdinalIgnoreCase) ||
                       id.Contains("zip", StringComparison.OrdinalIgnoreCase) ||
                       id.Contains("archive", StringComparison.OrdinalIgnoreCase))
                   ?? identifiers.FirstOrDefault()
                   ?? string.Empty;
        }

        private static string BuildPreferredFileName(NSItemProvider provider, string sourcePath, string typeIdentifier)
        {
            string fileName = provider.SuggestedName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = Path.GetFileName(sourcePath);

            string extension = Path.GetExtension(fileName);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = Path.GetExtension(sourcePath);
                if (string.IsNullOrWhiteSpace(extension))
                    extension = GuessExtension(typeIdentifier);

                fileName = $"{fileName}{extension}";
            }

            return fileName;
        }

        private static string GuessExtension(string typeIdentifier)
        {
            if (typeIdentifier.Contains("png", StringComparison.OrdinalIgnoreCase))
                return ".png";
            if (typeIdentifier.Contains("gif", StringComparison.OrdinalIgnoreCase))
                return ".gif";
            if (typeIdentifier.Contains("heic", StringComparison.OrdinalIgnoreCase))
                return ".heic";
            if (typeIdentifier.Contains("pdf", StringComparison.OrdinalIgnoreCase))
                return ".pdf";
            if (typeIdentifier.Contains("zip", StringComparison.OrdinalIgnoreCase) ||
                typeIdentifier.Contains("archive", StringComparison.OrdinalIgnoreCase))
            {
                return ".zip";
            }
            if (typeIdentifier.Contains("movie", StringComparison.OrdinalIgnoreCase) ||
                typeIdentifier.Contains("video", StringComparison.OrdinalIgnoreCase))
            {
                return ".mov";
            }

            return ".dat";
        }

        private async Task CompleteAndCloseAsync()
        {
            if (ExtensionContext == null)
            {
                DismissViewController(true, null);
                return;
            }

            await ExtensionContext.CompleteRequestAsync(Array.Empty<NSExtensionItem>());
        }

        private void CancelAndClose()
        {
            if (_isBusy)
                return;

            if (ExtensionContext == null)
            {
                DismissViewController(true, null);
                return;
            }

            using NSMutableDictionary userInfo = new();
            using NSError error = new(new NSString("vault.iOS.ShareExtension"), -1, userInfo);
            ExtensionContext.CancelRequest(error);
        }

        private void UpdateUiState()
        {
            bool hasRecentVaults = _recentVaults.Count > 0;
            bool hasIncoming = GetIncomingProviders().Count > 0;

            if (_emptyLabel != null)
            {
                _emptyLabel.Hidden = hasRecentVaults;
                if (!hasRecentVaults)
                    _emptyLabel.Text = "Apri almeno un vault nell'app principale per farlo comparire qui.";
            }

            if (_tableView != null)
                _tableView.Hidden = !hasRecentVaults;

            if (_confirmButton != null)
                _confirmButton.Enabled = hasRecentVaults && hasIncoming && !_isBusy;

            if (_cancelButton != null)
                _cancelButton.Enabled = !_isBusy;
        }

        private void SetSelectedVault(string vaultId)
        {
            _selectedVaultId = vaultId;
            _tableView?.ReloadData();
            UpdateUiState();
        }

        private void SetBusy(bool busy, string message)
        {
            _isBusy = busy;

            if (_busyIndicator != null)
            {
                _busyIndicator.Hidden = !busy;
                if (busy)
                    _busyIndicator.StartAnimating();
                else
                    _busyIndicator.StopAnimating();
            }

            if (_busyLabel != null)
            {
                _busyLabel.Hidden = !busy;
                _busyLabel.Text = message;
            }

            if (_tableView != null)
                _tableView.UserInteractionEnabled = !busy;

            UpdateUiState();
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

        private sealed class RecentVaultSource : UITableViewSource
        {
            private readonly ShareViewController _owner;

            public RecentVaultSource(ShareViewController owner)
            {
                _owner = owner;
            }

            public override nint RowsInSection(UITableView tableView, nint section) =>
                _owner._recentVaults.Count;

            public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
            {
                UITableViewCell cell = tableView.DequeueReusableCell(CellId)
                    ?? new UITableViewCell(UITableViewCellStyle.Subtitle, CellId);

                RecentVaultRecord vault = _owner._recentVaults[indexPath.Row];
                UIListContentConfiguration content = cell.DefaultContentConfiguration;
                content.Text = string.IsNullOrWhiteSpace(vault.DisplayName) ? "Vault" : vault.DisplayName;
                content.SecondaryText = string.IsNullOrWhiteSpace(vault.LastKnownPath)
                    ? "Vault recente"
                    : vault.LastKnownPath;
                cell.ContentConfiguration = content;
                cell.Accessory = string.Equals(vault.VaultId, _owner._selectedVaultId, StringComparison.OrdinalIgnoreCase)
                    ? UITableViewCellAccessory.Checkmark
                    : UITableViewCellAccessory.None;
                return cell;
            }

            public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
            {
                tableView.DeselectRow(indexPath, true);
                RecentVaultRecord vault = _owner._recentVaults[indexPath.Row];
                _owner.SetSelectedVault(vault.VaultId);
            }
        }
    }
}
