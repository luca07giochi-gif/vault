using CoreGraphics;
using Foundation;
using UIKit;
using vault.Core.Domain;
using vault.iOS.Shared;

namespace vault.iOS.ShareExtension
{
    [Register("ShareViewController")]
    public sealed class ShareViewController : UIViewController
    {
        private const string CellId = "RecentVaultCell";
        private const long LegacyAutoUpgradeThresholdBytes = 180L * 1024 * 1024;
        private const long LegacyUltraUpgradeThresholdBytes = 700L * 1024 * 1024;
        private const int MaxDestinationActions = 40;
        private static readonly UIColor BackgroundColor = UIColor.White;
        private static readonly UIColor PrimaryTextColor = UIColor.Black;
        private static readonly UIColor SecondaryTextColor = UIColor.FromRGB(110, 110, 115);

        private readonly List<RecentVaultRecord> _recentVaults = new();
        private UILabel? _summaryLabel;
        private UILabel? _emptyLabel;
        private UITableView? _tableView;
        private UIButton? _confirmButton;
        private UIButton? _cancelButton;
        private UIActivityIndicatorView? _busyIndicator;
        private UILabel? _busyLabel;
        private string? _selectedVaultId;
        private bool _isBusy;

        public override void ViewDidLoad()
        {
            base.ViewDidLoad();

            BuildUi();
            LoadRecentVaults();
        }

        public override void ViewWillAppear(bool animated)
        {
            base.ViewWillAppear(animated);
            LoadRecentVaults();
        }

        private void BuildUi()
        {
            View!.BackgroundColor = BackgroundColor;

            UILabel titleLabel = new()
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                Text = "Invia a Vault",
                Font = UIFont.BoldSystemFontOfSize(22f),
                TextColor = PrimaryTextColor
            };

            _summaryLabel = new UILabel
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                Lines = 0,
                Font = UIFont.SystemFontOfSize(15f),
                TextColor = SecondaryTextColor
            };

            _emptyLabel = new UILabel
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                Lines = 0,
                Font = UIFont.SystemFontOfSize(15f),
                TextAlignment = UITextAlignment.Center,
                TextColor = SecondaryTextColor,
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
            _confirmButton.SetTitle("Importa", UIControlState.Normal);
            _confirmButton.TitleLabel!.Font = UIFont.BoldSystemFontOfSize(16f);
            _confirmButton.TouchUpInside += async (_, _) => await ImportIntoSelectedVaultAsync();

            _busyIndicator = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Medium)
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                Hidden = true
            };

            _busyLabel = new UILabel
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                Font = UIFont.SystemFontOfSize(14f),
                TextColor = SecondaryTextColor,
                Hidden = true,
                Lines = 2,
                TextAlignment = UITextAlignment.Center
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
                _busyLabel.LeadingAnchor.ConstraintEqualTo(titleLabel.LeadingAnchor),
                _busyLabel.TrailingAnchor.ConstraintEqualTo(titleLabel.TrailingAnchor),
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
                    1 => "1 file pronto per essere importato nel vault selezionato.",
                    _ => $"{providerCount} file pronti per essere importati nel vault selezionato."
                };
            }

            _recentVaults.Clear();
            _recentVaults.AddRange(ShareVaultRegistryBridge.LoadPublishedVaults());
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

        private async Task ImportIntoSelectedVaultAsync()
        {
            if (_isBusy)
                return;

            IReadOnlyList<NSItemProvider> providers = GetIncomingProviders();
            if (providers.Count == 0)
            {
                ShowError("Nessun file da importare.");
                return;
            }

            _recentVaults.Clear();
            _recentVaults.AddRange(ShareVaultRegistryBridge.LoadPublishedVaults());
            if (_recentVaults.Count == 0)
            {
                ShowError("Non ci sono vault disponibili. Apri l'app principale, apri un vault e controlla l'elenco dal pulsante impostazioni.");
                UpdateUiState();
                return;
            }

            if (string.IsNullOrWhiteSpace(_selectedVaultId) && _recentVaults.Count > 0)
                _selectedVaultId = _recentVaults.First().VaultId;

            _tableView?.ReloadData();

            RecentVaultRecord? selectedVault = _recentVaults.FirstOrDefault(vault =>
                string.Equals(vault.VaultId, _selectedVaultId, StringComparison.OrdinalIgnoreCase));
            if (selectedVault == null)
            {
                ShowError("Scegli un vault.");
                return;
            }

            string? password = await PromptPasswordAsync(selectedVault.DisplayName);
            if (password == null)
                return;

            SetBusy(true, "Sto aprendo il vault...");

            VaultPortableReader? session = null;
            NSUrl? vaultUrl = null;
            try
            {
                vaultUrl = ResolveVaultUrl(selectedVault);
                session = await Task.Run(() => OpenVaultReader(vaultUrl, password));
            }
            catch (Exception ex)
            {
                SetBusy(false, string.Empty);
                ShowError(ex.Message);
                session?.Dispose();
                return;
            }

            SetBusy(false, string.Empty);

            try
            {
                string? destination = await PromptDestinationAsync(session);
                if (destination == null)
                    return;

                await ImportProvidersIntoVaultAsync(selectedVault, vaultUrl, session, providers, destination);
            }
            finally
            {
                session.Dispose();
            }
        }

        private async Task ImportProvidersIntoVaultAsync(
            RecentVaultRecord selectedVault,
            NSUrl vaultUrl,
            VaultPortableReader session,
            IReadOnlyList<NSItemProvider> providers,
            string destinationPath)
        {
            List<string> temporaryPaths = new();
            SetBusy(true, "Sto importando i file...");

            try
            {
                int completed = 0;
                foreach (NSItemProvider provider in providers)
                {
                    string tempPath = await LoadProviderToTempPathAsync(provider);
                    temporaryPaths.Add(tempPath);

                    string currentPath = tempPath;
                    await Task.Run(() => session.AddFileFromPath(currentPath, destinationPath));

                    completed++;
                    SetBusy(true, providers.Count == 1
                        ? "Sto importando i file..."
                        : $"Sto importando i file... ({completed}/{providers.Count})");
                }

                PrepareSessionForPersist(session, selectedVault);
                await Task.Run(() => PersistVaultToUrl(vaultUrl, session));
                await CompleteAndCloseAsync();
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
            finally
            {
                SetBusy(false, string.Empty);
                foreach (string path in temporaryPaths)
                    TryDeletePath(path);
            }
        }

        private static void PrepareSessionForPersist(VaultPortableReader session, RecentVaultRecord record)
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
            record.StorageFormat = targetFormat.ToString();
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

        private async Task<string?> PromptPasswordAsync(string? vaultName)
        {
            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            UIAlertController alert = UIAlertController.Create(
                "Password del vault",
                string.IsNullOrWhiteSpace(vaultName) ? "Inserisci la password del vault." : $"Inserisci la password di {vaultName}.",
                UIAlertControllerStyle.Alert);

            alert.AddTextField(field =>
            {
                field.Placeholder = "Password";
                field.SecureTextEntry = true;
                field.TextContentType = UITextContentType.Password;
                field.AutocorrectionType = UITextAutocorrectionType.No;
                field.AutocapitalizationType = UITextAutocapitalizationType.None;
            });

            alert.AddAction(UIAlertAction.Create("Annulla", UIAlertActionStyle.Cancel, _ => tcs.TrySetResult(null)));
            alert.AddAction(UIAlertAction.Create("Apri", UIAlertActionStyle.Default, _ =>
            {
                string password = alert.TextFields?.FirstOrDefault()?.Text?.Trim() ?? string.Empty;
                tcs.TrySetResult(password);
            }));

            PresentViewController(alert, true, null);
            return await tcs.Task;
        }

        private async Task<string?> PromptDestinationAsync(VaultPortableReader session)
        {
            IReadOnlyList<string> folderPaths = session.GetAllFolderPaths()
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            UIAlertController sheet = UIAlertController.Create(
                "Dove importare?",
                folderPaths.Count > MaxDestinationActions ? "Mostrate le prime cartelle piu comuni." : null,
                UIAlertControllerStyle.ActionSheet);

            foreach (string folderPath in folderPaths.Take(MaxDestinationActions))
            {
                string label = string.IsNullOrWhiteSpace(folderPath) ? "/" : $"/{folderPath}";
                sheet.AddAction(UIAlertAction.Create(label, UIAlertActionStyle.Default, _ => tcs.TrySetResult(folderPath)));
            }

            sheet.AddAction(UIAlertAction.Create("Annulla", UIAlertActionStyle.Cancel, _ => tcs.TrySetResult(null)));
            PresentViewController(sheet, true, null);
            return await tcs.Task;
        }

        private static Task<string> LoadProviderToTempPathAsync(NSItemProvider provider)
        {
            string typeIdentifier = SelectPreferredTypeIdentifier(provider);
            if (string.IsNullOrWhiteSpace(typeIdentifier))
                throw new InvalidOperationException("Tipo file non supportato.");

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

                    string preferredName = BuildPreferredFileName(provider, sourcePath, typeIdentifier);
                    string destinationPath = CreateTemporaryImportPath(preferredName);
                    File.Copy(sourcePath, destinationPath, overwrite: true);
                    tcs.TrySetResult(destinationPath);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            return tcs.Task;
        }

        private static string CreateTemporaryImportPath(string originalFileName)
        {
            string extension = Path.GetExtension(originalFileName ?? string.Empty);
            string runtimeRoot = Path.Combine(Path.GetTempPath(), "vault-ios-share-runtime");
            Directory.CreateDirectory(runtimeRoot);
            return Path.Combine(runtimeRoot, $"{Guid.NewGuid():N}{extension}");
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
                return ".zip";
            if (typeIdentifier.Contains("movie", StringComparison.OrdinalIgnoreCase) ||
                typeIdentifier.Contains("video", StringComparison.OrdinalIgnoreCase))
                return ".mov";

            return ".dat";
        }

        private static NSUrl ResolveVaultUrl(RecentVaultRecord vault)
        {
            if (!string.IsNullOrWhiteSpace(vault.BookmarkDataBase64))
            {
                try
                {
                    NSData data = NSData.FromArray(Convert.FromBase64String(vault.BookmarkDataBase64));
                    bool isStale;
                    NSError? error;
                    // iOS risolve il bookmark senza usare le opzioni security-scoped del binding macOS.
                    NSUrl? resolved = NSUrl.FromBookmarkData(
                        data,
                        0,
                        null,
                        out isStale,
                        out error);

                    if (resolved != null && error == null)
                        return resolved;
                }
                catch
                {
                    // Fallback below.
                }
            }

            string? path = vault.LastKnownPath;
            if (!string.IsNullOrWhiteSpace(path))
                return NSUrl.FromFilename(path);

            throw new InvalidOperationException("Questo vault non e disponibile al momento. Aprilo di nuovo nell'app principale e riprova.");
        }

        private static VaultPortableReader OpenVaultReader(NSUrl fileUrl, string password)
        {
            using var scope = new SecurityScopeAccess(fileUrl);

            string? path = fileUrl.Path;
            if (string.IsNullOrWhiteSpace(path))
                throw new IOException("Percorso vault non valido.");

            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return VaultPortableReader.Open(stream, password, allowUltra: true);
        }

        private static void PersistVaultToUrl(NSUrl fileUrl, VaultPortableReader session)
        {
            using var scope = new SecurityScopeAccess(fileUrl);

            string? path = fileUrl.Path;
            if (string.IsNullOrWhiteSpace(path))
                throw new IOException("Percorso vault non valido.");

            string tempPath = CreateVaultWriteTempPathNearDestination(path);
            try
            {
                using (FileStream output = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    session.SaveToStream(output);
                    output.Flush(flushToDisk: true);
                }

                try
                {
                    File.Move(tempPath, path, overwrite: true);
                }
                catch
                {
                    File.Copy(tempPath, path, overwrite: true);
                    File.Delete(tempPath);
                }
            }
            finally
            {
                TryDeletePath(tempPath);
            }
        }

        private static string CreateVaultWriteTempPathNearDestination(string destinationPath)
        {
            string fileName = Path.GetFileName(destinationPath);
            string? destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                try
                {
                    Directory.CreateDirectory(destinationDirectory);
                    return Path.Combine(destinationDirectory, $".{fileName}.{Guid.NewGuid():N}.tmp");
                }
                catch
                {
                    // Fallback below.
                }
            }

            string runtimeRoot = Path.Combine(Path.GetTempPath(), "vault-ios-share-runtime");
            Directory.CreateDirectory(runtimeRoot);
            return Path.Combine(runtimeRoot, $"{Guid.NewGuid():N}.tmp");
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
                {
                    _emptyLabel.Text = "Apri l'app principale, apri almeno un vault e controlla quali vault sono visibili dal pulsante impostazioni in alto a destra.";
                }
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
                    _url.StopAccessingSecurityScopedResource();
            }
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
                    ? "Vault disponibile per la condivisione"
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
