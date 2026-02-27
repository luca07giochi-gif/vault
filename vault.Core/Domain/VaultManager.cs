using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using vault.Core;
using vault.Core.Crypto;

namespace vault.Core.Domain
{
    public class VaultManager
    {
        private const int MaxFailedOpenAttempts = 5;
        private const long MaxStandardImportableFileBytes = int.MaxValue - 4096L;
        private const long MaxLegacyVaultPlaintextBytes = int.MaxValue - 16L;
        private const int UltraFileChunkSizeBytes = 8 * 1024 * 1024;
        private const int LegacyReadBufferSize = 256 * 1024;
        private static readonly TimeSpan FailedOpenWindow = TimeSpan.FromMinutes(5);

        private VaultContent? _content;
        private byte[]? _sessionKey;
        private byte[]? _salt;
        private string? _currentFilePath;
        private VaultStorageFormat _storageFormat = VaultStorageFormat.Extended;

        private int _failedOpenAttempts;
        private DateTime _firstFailedOpenAttemptUtc = DateTime.MinValue;

        public bool IsVaultOpen => _content != null;
        public string? CurrentVaultPath => _currentFilePath;
        public VaultStorageFormat? CurrentVaultStorageFormat =>
            IsVaultOpen ? _storageFormat : null;

        public int RemainingOpenAttempts =>
            Math.Max(0, MaxFailedOpenAttempts - _failedOpenAttempts);

        public IReadOnlyList<VaultFileItem> Files =>
            _content?.Files.AsReadOnly()
            ?? (IReadOnlyList<VaultFileItem>)Array.Empty<VaultFileItem>();

        // ---------- OPEN ----------
        public void OpenVault(string path, string password, IProgress<double>? progress = null)
        {
            EnsureOpenAttemptAllowed();
            ReportProgress(progress, 2);

            if (IsVaultOpen)
                throw new InvalidOperationException(VaultText.T("core.error.vaultAlreadyOpen"));

            byte[] pwdBytes = Encoding.UTF8.GetBytes(password);
            try
            {
                var header = VaultFileFormat.ReadHeader(path);
                ReportProgress(progress, 8);

                _salt = header.Salt;
                _sessionKey = KeyDerivation.DeriveKey(pwdBytes, _salt);
                ReportProgress(progress, 18);

                _storageFormat = header.Version switch
                {
                    VaultFileFormat.LEGACY_VERSION => VaultStorageFormat.Legacy,
                    VaultFileFormat.ULTRA_STREAMING_VERSION => VaultStorageFormat.Ultra,
                    _ => VaultStorageFormat.Extended
                };

                if (header.Version == VaultFileFormat.LEGACY_VERSION)
                {
                    byte[] encryptedPayload = ReadLegacyEncryptedPayloadWithProgress(path, CreateScaledProgress(progress, 18, 48));
                    ReportProgress(progress, 48);

                    byte[] aad = VaultFileFormat.SerializeHeaderForAad(header);
                    byte[] decrypted = AesGcmProvider.Decrypt(
                        _sessionKey,
                        header.Nonce,
                        encryptedPayload,
                        aad,
                        CreateScaledProgress(progress, 48, 74));
                    Array.Clear(encryptedPayload, 0, encryptedPayload.Length);
                    ReportProgress(progress, 74);

                    if (decrypted.Length < 4 || BitConverter.ToInt32(decrypted, 0) != 0x5641554C)
                        throw new CryptographicException(VaultText.T("core.error.passwordWrong"));

                    var deserializeProgress = CreateScaledProgress(progress, 74, 98);
                    _content = VaultSerializer.Deserialize(decrypted, deserializeProgress);
                    Array.Clear(decrypted, 0, decrypted.Length);
                }
                else if (header.Version == VaultFileFormat.STREAMING_VERSION ||
                         header.Version == VaultFileFormat.ULTRA_STREAMING_VERSION)
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    fs.Position = VaultFileFormat.HEADER_SIZE;

                    using var decryptedPayload =
                        VaultFileFormat.CreateStreamingDecryptingReadStream(fs, _sessionKey, header);

                    var deserializeProgress = CreateScaledProgress(progress, 20, 98);
                    _content = VaultSerializer.Deserialize(decryptedPayload, deserializeProgress);
                }
                else
                {
                    throw new InvalidDataException(VaultText.T("core.error.unsupportedVaultVersion"));
                }

                _currentFilePath = path;
                ResetFailedOpenAttempts();
                ReportProgress(progress, 100);
            }
            catch (Exception ex) when (
                ex is CryptographicException ||
                ex is InvalidDataException ||
                ex is EndOfStreamException ||
                ex is InvalidOperationException ||
                ex is OverflowException)
            {
                RegisterFailedOpenAttempt();
                ResetOpenState();
                throw new CryptographicException(VaultText.T("core.error.passwordOrCorrupted"));
            }
            catch
            {
                ResetOpenState();
                throw;
            }
            finally
            {
                Array.Clear(pwdBytes, 0, pwdBytes.Length);
            }
        }

        // ---------- CREATE ----------
        public void CreateVault(string path, string password) =>
            CreateVault(path, password, VaultStorageFormat.Extended, null);

        public void CreateVault(string path, string password, VaultStorageFormat storageFormat) =>
            CreateVault(path, password, storageFormat, null);

        public void CreateVault(
            string path,
            string password,
            VaultStorageFormat storageFormat,
            IProgress<double>? progress = null)
        {
            if (IsVaultOpen)
                throw new InvalidOperationException(VaultText.T("core.error.vaultAlreadyOpen"));

            ReportProgress(progress, 5);

            byte[] pwdBytes = Encoding.UTF8.GetBytes(password);
            try
            {
                _salt = VaultFileFormat.GenerateSalt();
                _sessionKey = KeyDerivation.DeriveKey(pwdBytes, _salt);
                ReportProgress(progress, 22);

                _content = new VaultContent
                {
                    Metadata = new VaultMetadata
                    {
                        Version = storageFormat == VaultStorageFormat.Ultra ? 4 : 3,
                        CreatedTicks = DateTime.UtcNow.Ticks
                    },
                    Files = new List<VaultFileItem>()
                };

                _storageFormat = storageFormat;
                _currentFilePath = path;
                Save(CreateScaledProgress(progress, 22, 100));
            }
            finally
            {
                Array.Clear(pwdBytes, 0, pwdBytes.Length);
            }
        }

        // ---------- NAVIGATION ----------
        public IReadOnlyList<VaultFileItem> GetItemsInFolder(string? folderPath)
        {
            EnsureVaultOpen();
            string normalized = NormalizePath(folderPath);
            EnsureFolderExists(normalized);

            return _content!.Files
                .Where(f => string.Equals(f.ParentPath, normalized, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.IsFolder)
                .ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public IReadOnlyList<string> GetAllFolderPaths()
        {
            EnsureVaultOpen();
            var paths = _content!.Files
                .Where(f => f.IsFolder)
                .Select(f => f.FullPath)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            paths.Insert(0, string.Empty); // root
            return paths;
        }

        // ---------- FILES / FOLDERS ----------
        public void AddFile(string sourcePath) => AddFile(sourcePath, string.Empty);

        public void AddFile(string sourcePath, string? targetFolderPath)
        {
            EnsureVaultOpen();
            string normalizedTarget = NormalizePath(targetFolderPath);
            EnsureFolderExists(normalizedTarget);

            AddPathInternal(sourcePath, normalizedTarget);
            Save();
        }

        public int AddExternalPaths(
            IEnumerable<string> sourcePaths,
            string? targetFolderPath,
            IProgress<double>? progress = null)
        {
            EnsureVaultOpen();
            if (sourcePaths == null)
                throw new ArgumentNullException(nameof(sourcePaths));

            string normalizedTarget = NormalizePath(targetFolderPath);
            EnsureFolderExists(normalizedTarget);

            var paths = sourcePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToList();

            if (paths.Count == 0)
            {
                ReportProgress(progress, 100);
                return 0;
            }

            long totalEstimatedBytes = EstimateImportBytes(paths);
            long importedBytes = 0;
            int processedPaths = 0;
            var importProgress = CreateScaledProgress(progress, 0, 90);

            int added = 0;
            foreach (string path in paths)
            {
                if (totalEstimatedBytes > 0)
                {
                    added += AddPathInternal(path, normalizedTarget, delta =>
                    {
                        if (delta <= 0)
                            return;

                        importedBytes = SafeAdd(importedBytes, delta);
                        double percent = importedBytes * 100.0 / totalEstimatedBytes;
                        ReportProgress(importProgress, percent);
                    });
                }
                else
                {
                    added += AddPathInternal(path, normalizedTarget);
                    processedPaths++;
                    double percent = processedPaths * 100.0 / paths.Count;
                    ReportProgress(importProgress, percent);
                }
            }

            ReportProgress(importProgress, 100);

            if (added > 0)
            {
                Save(CreateScaledProgress(progress, 90, 100));
            }
            else
            {
                ReportProgress(progress, 100);
            }

            return added;
        }

        public VaultFileItem CreateFolder(string folderName, string? parentFolderPath)
        {
            EnsureVaultOpen();
            string normalizedParent = NormalizePath(parentFolderPath);
            EnsureFolderExists(normalizedParent);

            string cleaned = SanitizeName(folderName, isFolder: true);
            string uniqueName = EnsureUniqueName(normalizedParent, cleaned, isFolder: true);

            var created = new VaultFileItem
            {
                Id = Guid.NewGuid(),
                FileName = uniqueName,
                ParentPath = normalizedParent,
                IsFolder = true,
                AddedTicks = DateTime.UtcNow.Ticks,
                Content = Array.Empty<byte>()
            };

            _content!.Files.Add(created);
            Save();
            return created;
        }

        public void MoveItems(IEnumerable<Guid> itemIds, string? destinationFolderPath)
        {
            EnsureVaultOpen();
            if (itemIds == null)
                throw new ArgumentNullException(nameof(itemIds));

            string destination = NormalizePath(destinationFolderPath);
            EnsureFolderExists(destination);

            var requested = itemIds.Distinct().ToList();
            if (requested.Count == 0)
                return;

            var selected = _content!.Files.Where(f => requested.Contains(f.Id)).ToList();
            if (selected.Count == 0)
                return;

            var selectedFolderPaths = selected
                .Where(f => f.IsFolder)
                .Select(f => f.FullPath)
                .ToList();

            // Prevent moving a folder into itself or one of its descendants.
            foreach (string folderPath in selectedFolderPaths)
            {
                if (string.Equals(destination, folderPath, StringComparison.OrdinalIgnoreCase) ||
                    destination.StartsWith(folderPath + "/", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(VaultText.T("core.error.invalidMoveDestinationSelf"));
                }
            }

            var selectedIds = selected.Select(f => f.Id).ToHashSet();
            var oldPaths = _content.Files.ToDictionary(f => f.Id, f => f.FullPath);

            // Move only roots; descendants of selected folders follow automatically.
            var roots = selected.Where(item =>
            {
                string itemPath = oldPaths[item.Id];
                return !selected.Any(parent =>
                    parent.IsFolder &&
                    parent.Id != item.Id &&
                    itemPath.StartsWith(oldPaths[parent.Id] + "/", StringComparison.OrdinalIgnoreCase));
            }).ToList();

            foreach (var root in roots)
            {
                string oldRootPath = oldPaths[root.Id];
                if (string.Equals(root.ParentPath, destination, StringComparison.OrdinalIgnoreCase))
                    continue;

                string targetName = EnsureUniqueName(destination, root.FileName, root.IsFolder);
                root.ParentPath = destination;
                root.FileName = targetName;

                string newRootPath = root.FullPath;
                if (!root.IsFolder)
                    continue;

                foreach (var descendant in _content.Files)
                {
                    if (descendant.Id == root.Id)
                        continue;

                    string oldPath = oldPaths[descendant.Id];
                    if (!oldPath.StartsWith(oldRootPath + "/", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string relative = oldPath[(oldRootPath.Length + 1)..];
                    string updatedPath = CombinePath(newRootPath, relative);
                    descendant.ParentPath = GetParentPath(updatedPath);
                    descendant.FileName = GetNodeName(updatedPath);
                }
            }

            Save();
        }

        public void RenameItem(Guid itemId, string newName)
        {
            EnsureVaultOpen();
            if (string.IsNullOrWhiteSpace(newName))
                throw new ArgumentException(VaultText.T("core.error.invalidName"), nameof(newName));

            VaultFileItem? item = _content!.Files.FirstOrDefault(f => f.Id == itemId);
            if (item == null)
                throw new FileNotFoundException(VaultText.T("core.error.itemNotFound"));

            string cleaned = SanitizeName(newName, item.IsFolder);
            if (string.Equals(item.FileName, cleaned, StringComparison.OrdinalIgnoreCase))
                return;

            string uniqueName = EnsureUniqueName(item.ParentPath, cleaned, item.IsFolder);
            var oldPaths = _content.Files.ToDictionary(f => f.Id, f => f.FullPath);
            string oldRootPath = oldPaths[item.Id];

            item.FileName = uniqueName;
            if (!item.IsFolder)
            {
                Save();
                return;
            }

            string newRootPath = item.FullPath;
            foreach (var descendant in _content.Files)
            {
                if (descendant.Id == item.Id)
                    continue;

                string oldPath = oldPaths[descendant.Id];
                if (!oldPath.StartsWith(oldRootPath + "/", StringComparison.OrdinalIgnoreCase))
                    continue;

                string relative = oldPath[(oldRootPath.Length + 1)..];
                string updatedPath = CombinePath(newRootPath, relative);
                descendant.ParentPath = GetParentPath(updatedPath);
                descendant.FileName = GetNodeName(updatedPath);
            }

            Save();
        }

        public void DeleteFile(Guid fileId) => DeleteItems(new[] { fileId });

        public void DeleteItems(IEnumerable<Guid> itemIds)
        {
            EnsureVaultOpen();
            if (itemIds == null)
                throw new ArgumentNullException(nameof(itemIds));

            var requested = itemIds.Distinct().ToList();
            if (requested.Count == 0)
                return;

            var selected = _content!.Files.Where(f => requested.Contains(f.Id)).ToList();
            if (selected.Count == 0)
                return;

            var removeIds = new HashSet<Guid>(selected.Select(s => s.Id));
            var selectedFolders = selected.Where(s => s.IsFolder).Select(s => s.FullPath).ToList();

            foreach (var item in _content.Files)
            {
                if (removeIds.Contains(item.Id))
                    continue;

                if (selectedFolders.Any(folder => item.FullPath.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)))
                    removeIds.Add(item.Id);
            }

            var toRemove = _content.Files.Where(f => removeIds.Contains(f.Id)).ToList();
            foreach (var item in toRemove)
            {
                if (item.Content.Length > 0)
                    Array.Clear(item.Content, 0, item.Content.Length);

                if (item.ContentChunks.Count > 0)
                {
                    foreach (var chunk in item.ContentChunks)
                    {
                        if (chunk.Length > 0)
                            Array.Clear(chunk, 0, chunk.Length);
                    }

                    item.ContentChunks.Clear();
                }

                _content.Files.Remove(item);
            }

            Save();
        }

        public void ExportFile(Guid fileId, string destinationPath, IProgress<double>? progress = null)
        {
            EnsureVaultOpen();
            if (string.IsNullOrWhiteSpace(destinationPath))
                throw new ArgumentException(VaultText.T("core.error.invalidDestinationPath"), nameof(destinationPath));

            var file = _content!.Files.FirstOrDefault(e => e.Id == fileId);
            if (file == null)
                throw new FileNotFoundException(VaultText.T("core.error.fileNotFound"));
            if (file.IsFolder)
                throw new InvalidOperationException(VaultText.T("core.error.selectedIsFolder"));

            ReportProgress(progress, 2);

            long totalBytes = file.ContentLength;
            if (totalBytes == 0)
            {
                File.WriteAllBytes(destinationPath, Array.Empty<byte>());
                ReportProgress(progress, 100);
                return;
            }

            long written = 0;

            using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            foreach (var chunk in file.GetContentChunks())
            {
                if (chunk.Length == 0)
                    continue;

                output.Write(chunk, 0, chunk.Length);
                written += chunk.Length;
                ReportProgress(progress, written * 100.0 / totalBytes);
            }

            output.Flush(true);
            ReportProgress(progress, 100);
        }

        public void ChangePassword(string newPassword, IProgress<double>? progress = null)
        {
            EnsureVaultOpen();
            if (string.IsNullOrWhiteSpace(newPassword))
                throw new ArgumentException(VaultText.T("core.error.invalidNewPassword"), nameof(newPassword));

            ReportProgress(progress, 3);

            byte[] pwdBytes = Encoding.UTF8.GetBytes(newPassword);
            byte[] newSalt = VaultFileFormat.GenerateSalt();
            byte[] newSessionKey = Array.Empty<byte>();
            byte[]? previousSessionKey = _sessionKey;
            byte[]? previousSalt = _salt;

            try
            {
                newSessionKey = KeyDerivation.DeriveKey(pwdBytes, newSalt);
                ReportProgress(progress, 20);

                _sessionKey = newSessionKey;
                _salt = newSalt;

                Save(CreateScaledProgress(progress, 20, 100));

                if (previousSessionKey != null)
                    Array.Clear(previousSessionKey, 0, previousSessionKey.Length);
            }
            catch
            {
                if (newSessionKey.Length > 0)
                    Array.Clear(newSessionKey, 0, newSessionKey.Length);

                _sessionKey = previousSessionKey;
                _salt = previousSalt;
                throw;
            }
            finally
            {
                Array.Clear(pwdBytes, 0, pwdBytes.Length);
            }
        }

        public void ChangeStorageFormat(VaultStorageFormat newFormat, IProgress<double>? progress = null)
        {
            EnsureVaultOpen();

            if (newFormat != VaultStorageFormat.Legacy &&
                newFormat != VaultStorageFormat.Extended &&
                newFormat != VaultStorageFormat.Ultra)
            {
                throw new ArgumentOutOfRangeException(nameof(newFormat), VaultText.T("core.error.unsupportedStorageFormat"));
            }

            if (_storageFormat == newFormat)
            {
                ReportProgress(progress, 100);
                return;
            }

            VaultStorageFormat previousFormat = _storageFormat;
            try
            {
                _storageFormat = newFormat;
                ReportProgress(progress, 5);
                Save(CreateScaledProgress(progress, 5, 100));
            }
            catch
            {
                _storageFormat = previousFormat;
                throw;
            }
        }

        // ---------- SAVE ----------
        private void Save(IProgress<double>? progress = null)
        {
            if (!IsVaultOpen || _sessionKey == null || _currentFilePath == null || _salt == null)
                return;

            if (_storageFormat == VaultStorageFormat.Legacy)
            {
                SaveLegacyFormat(_sessionKey, _salt, _currentFilePath, progress);
                return;
            }

            if (_storageFormat == VaultStorageFormat.Ultra)
            {
                SaveUltraFormat(_sessionKey, _salt, _currentFilePath, progress);
                return;
            }

            SaveExtendedFormat(_sessionKey, _salt, _currentFilePath, progress);
        }

        private void SaveLegacyFormat(
            byte[] sessionKey,
            byte[] salt,
            string vaultPath,
            IProgress<double>? progress = null)
        {
            _content!.Metadata.Version = 3;
            long estimatedSize = VaultSerializer.EstimateSerializedSize(_content!);
            if (estimatedSize > MaxLegacyVaultPlaintextBytes)
            {
                throw new InvalidOperationException(
                    VaultText.T("core.error.legacySizeLimit"));
            }

            byte[] nonce = VaultFileFormat.GenerateNonce();
            var header = new VaultFileFormat.Header(
                VaultFileFormat.MAGIC,
                VaultFileFormat.LEGACY_VERSION,
                salt,
                nonce);

            ReportProgress(progress, 4);
            byte[] plaintext = VaultSerializer.Serialize(
                _content!,
                CreateScaledProgress(progress, 4, 58),
                ultraContent: false);
            ReportProgress(progress, 58);
            byte[] aad = VaultFileFormat.SerializeHeaderForAad(header);
            byte[] encrypted = AesGcmProvider.Encrypt(sessionKey, nonce, plaintext, aad);
            ReportProgress(progress, 78);

            string tempPath = vaultPath + ".tmp";
            try
            {
                VaultFileFormat.WriteVault(
                    tempPath,
                    encrypted,
                    salt,
                    nonce,
                    VaultFileFormat.LEGACY_VERSION);

                ReportProgress(progress, 92);
                ReplaceVaultFile(tempPath, vaultPath);
                ReportProgress(progress, 100);
            }
            finally
            {
                if (plaintext.Length > 0)
                    Array.Clear(plaintext, 0, plaintext.Length);

                if (encrypted.Length > 0)
                    Array.Clear(encrypted, 0, encrypted.Length);

                if (File.Exists(tempPath))
                    TrySecureDeleteFile(tempPath);
            }
        }

        private void SaveExtendedFormat(
            byte[] sessionKey,
            byte[] salt,
            string vaultPath,
            IProgress<double>? progress = null)
        {
            _content!.Metadata.Version = 3;
            byte[] nonce = VaultFileFormat.GenerateNonce();
            var header = new VaultFileFormat.Header(
                VaultFileFormat.MAGIC,
                VaultFileFormat.STREAMING_VERSION,
                salt,
                nonce);

            string tempPath = vaultPath + ".tmp";
            try
            {
                using (var tempFile = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var encryptedPayload =
                    VaultFileFormat.CreateStreamingEncryptingWriteStream(tempFile, sessionKey, header))
                {
                    ReportProgress(progress, 4);
                    VaultSerializer.SerializeToStream(
                        _content!,
                        encryptedPayload,
                        CreateScaledProgress(progress, 4, 90),
                        ultraContent: false);
                }

                ReportProgress(progress, 94);
                ReplaceVaultFile(tempPath, vaultPath);
                ReportProgress(progress, 100);
            }
            finally
            {
                if (File.Exists(tempPath))
                    TrySecureDeleteFile(tempPath);
            }
        }

        private void SaveUltraFormat(
            byte[] sessionKey,
            byte[] salt,
            string vaultPath,
            IProgress<double>? progress = null)
        {
            _content!.Metadata.Version = 4;
            byte[] nonce = VaultFileFormat.GenerateNonce();
            var header = new VaultFileFormat.Header(
                VaultFileFormat.MAGIC,
                VaultFileFormat.ULTRA_STREAMING_VERSION,
                salt,
                nonce);

            string tempPath = vaultPath + ".tmp";
            try
            {
                using (var tempFile = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var encryptedPayload =
                    VaultFileFormat.CreateStreamingEncryptingWriteStream(tempFile, sessionKey, header))
                {
                    ReportProgress(progress, 4);
                    VaultSerializer.SerializeToStream(
                        _content!,
                        encryptedPayload,
                        CreateScaledProgress(progress, 4, 90),
                        ultraContent: true);
                }

                ReportProgress(progress, 94);
                ReplaceVaultFile(tempPath, vaultPath);
                ReportProgress(progress, 100);
            }
            finally
            {
                if (File.Exists(tempPath))
                    TrySecureDeleteFile(tempPath);
            }
        }

        private static void ReplaceVaultFile(string tempPath, string vaultPath)
        {
            string backupPath = BuildBackupPath(vaultPath);
            string legacyBackupPath = vaultPath + ".bak";

            if (File.Exists(vaultPath))
            {
                File.Replace(tempPath, vaultPath, backupPath, true);
                TrySetHiddenAttributes(backupPath);

                if (File.Exists(legacyBackupPath))
                    TrySetHiddenAttributes(legacyBackupPath);

                return;
            }

            File.Move(tempPath, vaultPath, true);
        }

        // ---------- CLOSE ----------
        public void CloseVault()
        {
            if (_content != null)
            {
                foreach (var file in _content.Files)
                {
                    if (file.Content.Length > 0)
                        Array.Clear(file.Content, 0, file.Content.Length);

                    if (file.ContentChunks.Count > 0)
                    {
                        foreach (var chunk in file.ContentChunks)
                        {
                            if (chunk.Length > 0)
                                Array.Clear(chunk, 0, chunk.Length);
                        }

                        file.ContentChunks.Clear();
                    }
                }

                _content = null;
            }

            if (_sessionKey != null)
                Array.Clear(_sessionKey, 0, _sessionKey.Length);

            _sessionKey = null;
            _salt = null;
            _currentFilePath = null;
            _storageFormat = VaultStorageFormat.Extended;
        }

        // ---------- HELPERS ----------
        private int AddPathInternal(string sourcePath, string targetFolderPath, Action<long>? onBytesImported = null)
        {
            if (File.Exists(sourcePath))
            {
                if (Path.GetExtension(sourcePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                    return ImportZipAsFolder(sourcePath, targetFolderPath, onBytesImported);

                string fileName = SanitizeName(Path.GetFileName(sourcePath), isFolder: false);
                string uniqueName = EnsureUniqueName(targetFolderPath, fileName, isFolder: false);
                var payload = ReadFilePayload(
                    sourcePath,
                    allowLargeSingleFile: _storageFormat == VaultStorageFormat.Ultra,
                    onBytesRead: onBytesImported);

                _content!.Files.Add(new VaultFileItem
                {
                    Id = Guid.NewGuid(),
                    FileName = uniqueName,
                    ParentPath = targetFolderPath,
                    IsFolder = false,
                    AddedTicks = DateTime.UtcNow.Ticks,
                    Content = payload.Content,
                    ContentChunks = payload.ContentChunks
                });

                return 1;
            }

            if (Directory.Exists(sourcePath))
                return ImportDirectoryAsFolder(sourcePath, targetFolderPath, onBytesImported);

            throw new FileNotFoundException(VaultText.T("core.error.pathNotFound"), sourcePath);
        }

        private int ImportDirectoryAsFolder(
            string sourceDirectory,
            string targetFolderPath,
            Action<long>? onBytesImported = null)
        {
            string rootName = SanitizeName(Path.GetFileName(sourceDirectory), isFolder: true);
            string uniqueRootName = EnsureUniqueName(targetFolderPath, rootName, isFolder: true);
            var root = AddFolderItem(targetFolderPath, uniqueRootName);

            int added = 1;
            string rootPath = root.FullPath;

            foreach (string directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                string relative = NormalizePath(Path.GetRelativePath(sourceDirectory, directory));
                string fullFolderPath = CombinePath(rootPath, relative);
                EnsureFolderPathExists(fullFolderPath);
                added++;
            }

            foreach (string file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                string relative = NormalizePath(Path.GetRelativePath(sourceDirectory, file));
                string parentPath = GetParentPath(CombinePath(rootPath, relative));
                string fileName = GetNodeName(relative);
                string cleanName = SanitizeName(fileName, isFolder: false);
                string uniqueName = EnsureUniqueName(parentPath, cleanName, isFolder: false);
                var payload = ReadFilePayload(
                    file,
                    allowLargeSingleFile: _storageFormat == VaultStorageFormat.Ultra,
                    onBytesRead: onBytesImported);

                _content!.Files.Add(new VaultFileItem
                {
                    Id = Guid.NewGuid(),
                    FileName = uniqueName,
                    ParentPath = parentPath,
                    IsFolder = false,
                    AddedTicks = DateTime.UtcNow.Ticks,
                    Content = payload.Content,
                    ContentChunks = payload.ContentChunks
                });

                added++;
            }

            return added;
        }

        private int ImportZipAsFolder(
            string zipPath,
            string targetFolderPath,
            Action<long>? onBytesImported = null)
        {
            string rootName = SanitizeName(Path.GetFileNameWithoutExtension(zipPath), isFolder: true);
            if (string.IsNullOrWhiteSpace(rootName))
                rootName = "zip";

            string uniqueRootName = EnsureUniqueName(targetFolderPath, rootName, isFolder: true);
            var root = AddFolderItem(targetFolderPath, uniqueRootName);
            string rootPath = root.FullPath;
            int added = 1;

            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                string full = entry.FullName.Replace('\\', '/');
                if (string.IsNullOrWhiteSpace(full))
                    continue;

                bool isDirectory = full.EndsWith("/", StringComparison.Ordinal);
                string trimmed = full.TrimEnd('/');
                if (trimmed.Length == 0)
                    continue;

                string[] segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0)
                    continue;

                string parent = rootPath;
                for (int i = 0; i < segments.Length - 1; i++)
                {
                    string cleanSegment = SanitizeName(segments[i], isFolder: true);
                    parent = EnsureFolderInParent(parent, cleanSegment);
                }

                string leaf = SanitizeName(segments[^1], isFolder: isDirectory);
                if (isDirectory)
                {
                    EnsureFolderInParent(parent, leaf);
                    added++;
                    continue;
                }

                string fileName = EnsureUniqueName(parent, leaf, isFolder: false);
                var payload = ReadZipEntryPayload(
                    entry,
                    allowLargeSingleFile: _storageFormat == VaultStorageFormat.Ultra,
                    onBytesRead: onBytesImported);

                _content!.Files.Add(new VaultFileItem
                {
                    Id = Guid.NewGuid(),
                    FileName = fileName,
                    ParentPath = parent,
                    IsFolder = false,
                    AddedTicks = DateTime.UtcNow.Ticks,
                    Content = payload.Content,
                    ContentChunks = payload.ContentChunks
                });

                added++;
            }

            return added;
        }

        private VaultFileItem AddFolderItem(string parentPath, string name)
        {
            var folder = new VaultFileItem
            {
                Id = Guid.NewGuid(),
                FileName = name,
                ParentPath = parentPath,
                IsFolder = true,
                AddedTicks = DateTime.UtcNow.Ticks,
                Content = Array.Empty<byte>(),
                ContentChunks = new List<byte[]>()
            };

            _content!.Files.Add(folder);
            return folder;
        }

        private string EnsureFolderInParent(string parentPath, string folderName)
        {
            var existing = _content!.Files.FirstOrDefault(f =>
                f.IsFolder &&
                string.Equals(f.ParentPath, parentPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(f.FileName, folderName, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
                return existing.FullPath;

            string unique = EnsureUniqueName(parentPath, folderName, isFolder: true);
            return AddFolderItem(parentPath, unique).FullPath;
        }

        private string EnsureFolderPathExists(string fullPath)
        {
            string normalized = NormalizePath(fullPath);
            if (string.IsNullOrWhiteSpace(normalized))
                return string.Empty;

            if (FolderExists(normalized))
                return normalized;

            string parent = GetParentPath(normalized);
            string name = GetNodeName(normalized);
            if (!string.IsNullOrWhiteSpace(parent))
                EnsureFolderPathExists(parent);

            return EnsureFolderInParent(parent, name);
        }

        private bool FolderExists(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return true;

            return _content!.Files.Any(f =>
                f.IsFolder &&
                string.Equals(f.FullPath, path, StringComparison.OrdinalIgnoreCase));
        }

        private void EnsureFolderExists(string path)
        {
            if (!FolderExists(path))
                throw new InvalidOperationException(VaultText.T("core.error.destinationFolderMissing"));
        }

        private sealed class FilePayload
        {
            public FilePayload(byte[] content, List<byte[]> contentChunks)
            {
                Content = content ?? Array.Empty<byte>();
                ContentChunks = contentChunks ?? new List<byte[]>();
            }

            public byte[] Content { get; }
            public List<byte[]> ContentChunks { get; }
        }

        private static FilePayload ReadFilePayload(
            string sourcePath,
            bool allowLargeSingleFile,
            Action<long>? onBytesRead = null)
        {
            var info = new FileInfo(sourcePath);
            ValidateItemSize(info.Length, Path.GetFileName(sourcePath), allowLargeSingleFile);

            using var fs = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!allowLargeSingleFile || info.Length <= MaxStandardImportableFileBytes)
                return ReadStreamAsSingleArray(fs, Path.GetFileName(sourcePath), onBytesRead);

            return ReadStreamAsChunkList(fs, Path.GetFileName(sourcePath), onBytesRead);
        }

        private static FilePayload ReadZipEntryPayload(
            ZipArchiveEntry entry,
            bool allowLargeSingleFile,
            Action<long>? onBytesRead = null)
        {
            ValidateItemSize(entry.Length, entry.FullName, allowLargeSingleFile);

            using var input = entry.Open();
            if (!allowLargeSingleFile || entry.Length <= MaxStandardImportableFileBytes)
                return ReadStreamAsSingleArray(input, entry.FullName, onBytesRead);

            return ReadStreamAsChunkList(input, entry.FullName, onBytesRead);
        }

        private static FilePayload ReadStreamAsSingleArray(
            Stream input,
            string itemName,
            Action<long>? onBytesRead = null)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));

            const int bufferSize = 256 * 1024;
            byte[] buffer = new byte[bufferSize];
            long totalRead = 0;
            using var ms = new MemoryStream();

            while (true)
            {
                int read = input.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    break;

                totalRead += read;
                ValidateItemSize(totalRead, itemName, allowLargeSingleFile: false);
                ms.Write(buffer, 0, read);
                onBytesRead?.Invoke(read);
            }

            byte[] content = ms.ToArray();
            return new FilePayload(content, new List<byte[]>());
        }

        private static FilePayload ReadStreamAsChunkList(
            Stream input,
            string itemName,
            Action<long>? onBytesRead = null)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));

            var chunks = new List<byte[]>();
            byte[] buffer = new byte[UltraFileChunkSizeBytes];
            long totalRead = 0;

            while (true)
            {
                int read = input.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    break;

                totalRead += read;
                ValidateItemSize(totalRead, itemName, allowLargeSingleFile: true);

                var chunk = new byte[read];
                Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                chunks.Add(chunk);
                onBytesRead?.Invoke(read);
            }

            return new FilePayload(Array.Empty<byte>(), chunks);
        }

        private static void ValidateItemSize(long sizeBytes, string itemName, bool allowLargeSingleFile)
        {
            if (allowLargeSingleFile || sizeBytes <= MaxStandardImportableFileBytes)
                return;

            throw new InvalidOperationException(
                VaultText.F("core.error.fileTooLargeForFormat", itemName));
        }

        private static long EstimateImportBytes(IEnumerable<string> sourcePaths)
        {
            long totalBytes = 0;
            foreach (string sourcePath in sourcePaths)
            {
                if (File.Exists(sourcePath))
                {
                    if (Path.GetExtension(sourcePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        totalBytes = SafeAdd(totalBytes, EstimateZipEntryBytes(sourcePath));
                    }
                    else
                    {
                        totalBytes = SafeAdd(totalBytes, new FileInfo(sourcePath).Length);
                    }

                    continue;
                }

                if (!Directory.Exists(sourcePath))
                    continue;

                foreach (string file in Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories))
                {
                    totalBytes = SafeAdd(totalBytes, new FileInfo(file).Length);
                }
            }

            return totalBytes;
        }

        private static long EstimateZipEntryBytes(string zipPath)
        {
            long totalBytes = 0;
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                bool isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
                if (isDirectory)
                    continue;

                totalBytes = SafeAdd(totalBytes, entry.Length);
            }

            return totalBytes;
        }

        private static long SafeAdd(long left, long right)
        {
            if (right <= 0)
                return left;

            if (left > long.MaxValue - right)
                return long.MaxValue;

            return left + right;
        }

        private static byte[] ReadLegacyEncryptedPayloadWithProgress(string path, IProgress<double>? progress)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length <= VaultFileFormat.HEADER_SIZE)
                throw new InvalidDataException(VaultText.T("core.format.payloadMissing"));

            long payloadLengthLong = fs.Length - VaultFileFormat.HEADER_SIZE;
            if (payloadLengthLong > int.MaxValue)
                throw new InvalidOperationException(VaultText.T("core.format.vaultTooLargeLegacy"));

            int payloadLength = (int)payloadLengthLong;
            var payload = new byte[payloadLength];
            fs.Position = VaultFileFormat.HEADER_SIZE;

            int offset = 0;
            var buffer = new byte[LegacyReadBufferSize];
            while (offset < payload.Length)
            {
                int toRead = Math.Min(buffer.Length, payload.Length - offset);
                int read = fs.Read(buffer, 0, toRead);
                if (read <= 0)
                    throw new InvalidDataException(VaultText.T("core.format.payloadMissing"));

                Buffer.BlockCopy(buffer, 0, payload, offset, read);
                offset += read;
                ReportProgress(progress, payload.Length == 0 ? 100 : offset * 100.0 / payload.Length);
            }

            ReportProgress(progress, 100);
            return payload;
        }

        private static void ReportProgress(IProgress<double>? progress, double value)
        {
            if (progress == null)
                return;

            if (double.IsNaN(value) || double.IsInfinity(value))
                return;

            progress.Report(Math.Clamp(value, 0, 100));
        }

        private static IProgress<double>? CreateScaledProgress(
            IProgress<double>? progress,
            double startPercent,
            double endPercent)
        {
            if (progress == null)
                return null;

            double start = Math.Clamp(startPercent, 0, 100);
            double end = Math.Clamp(endPercent, 0, 100);
            double range = end - start;

            return new Progress<double>(value =>
            {
                double normalized = Math.Clamp(value, 0, 100) / 100.0;
                ReportProgress(progress, start + normalized * range);
            });
        }

        private static string NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            string normalized = path.Replace('\\', '/').Trim().Trim('/');
            if (normalized.Length == 0)
                return string.Empty;

            var segments = normalized
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Where(s => s != ".")
                .ToArray();

            if (segments.Any(s => s == ".."))
                throw new ArgumentException(VaultText.T("core.error.invalidPath"));

            return string.Join('/', segments);
        }

        private static string CombinePath(string left, string right)
        {
            string l = NormalizePath(left);
            string r = NormalizePath(right);
            if (string.IsNullOrWhiteSpace(l)) return r;
            if (string.IsNullOrWhiteSpace(r)) return l;
            return $"{l}/{r}";
        }

        private static string GetParentPath(string fullPath)
        {
            string normalized = NormalizePath(fullPath);
            if (string.IsNullOrWhiteSpace(normalized))
                return string.Empty;

            int idx = normalized.LastIndexOf('/');
            return idx < 0 ? string.Empty : normalized[..idx];
        }

        private static string GetNodeName(string fullPath)
        {
            string normalized = NormalizePath(fullPath);
            if (string.IsNullOrWhiteSpace(normalized))
                return string.Empty;

            int idx = normalized.LastIndexOf('/');
            return idx < 0 ? normalized : normalized[(idx + 1)..];
        }

        private string EnsureUniqueName(string parentPath, string desiredName, bool isFolder)
        {
            string clean = SanitizeName(desiredName, isFolder);

            bool Exists(string name) => _content!.Files.Any(f =>
                string.Equals(f.ParentPath, parentPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(f.FileName, name, StringComparison.OrdinalIgnoreCase));

            if (!Exists(clean))
                return clean;

            string baseName;
            string extension;
            if (isFolder)
            {
                baseName = clean;
                extension = string.Empty;
            }
            else
            {
                baseName = Path.GetFileNameWithoutExtension(clean);
                extension = Path.GetExtension(clean);
                if (string.IsNullOrWhiteSpace(baseName))
                    baseName = VaultText.T("core.default.fileName");
            }

            int index = 1;
            while (true)
            {
                string candidate = $"{baseName} ({index}){extension}";
                if (!Exists(candidate))
                    return candidate;
                index++;
            }
        }

        private static string SanitizeName(string name, bool isFolder)
        {
            string candidate = string.IsNullOrWhiteSpace(name)
                ? (isFolder ? VaultText.T("core.default.newFolder") : VaultText.T("core.default.fileBin"))
                : name.Trim();

            foreach (char ch in Path.GetInvalidFileNameChars())
                candidate = candidate.Replace(ch, '_');

            candidate = candidate.Replace('/', '_').Replace('\\', '_');
            return string.IsNullOrWhiteSpace(candidate)
                ? (isFolder ? VaultText.T("core.default.newFolder") : VaultText.T("core.default.fileBin"))
                : candidate;
        }

        private static string BuildBackupPath(string vaultPath)
        {
            string parentDirectory = Path.GetDirectoryName(vaultPath) ?? Directory.GetCurrentDirectory();
            string backupDirectory = Path.Combine(parentDirectory, ".vault-backups");
            Directory.CreateDirectory(backupDirectory);
            TrySetHiddenAttributes(backupDirectory);

            string backupFileName = $"{Path.GetFileName(vaultPath)}.bak";
            return Path.Combine(backupDirectory, backupFileName);
        }

        private static void TrySetHiddenAttributes(string path)
        {
            try
            {
                FileAttributes current = File.GetAttributes(path);
                FileAttributes desired = current | FileAttributes.Hidden | FileAttributes.System;
                if (current != desired)
                    File.SetAttributes(path, desired);
            }
            catch
            {
                // Best effort hidden flag.
            }
        }

        private void EnsureVaultOpen()
        {
            if (!IsVaultOpen)
                throw new InvalidOperationException(VaultText.T("core.error.noVaultOpen"));
        }

        private void EnsureOpenAttemptAllowed()
        {
            if (_failedOpenAttempts < MaxFailedOpenAttempts)
                return;

            if (DateTime.UtcNow - _firstFailedOpenAttemptUtc > FailedOpenWindow)
            {
                ResetFailedOpenAttempts();
                return;
            }

            throw new CryptographicException(
                VaultText.T("core.error.tooManyAttempts"));
        }

        private void RegisterFailedOpenAttempt()
        {
            DateTime now = DateTime.UtcNow;

            if (_firstFailedOpenAttemptUtc == DateTime.MinValue ||
                now - _firstFailedOpenAttemptUtc > FailedOpenWindow)
            {
                _firstFailedOpenAttemptUtc = now;
                _failedOpenAttempts = 1;
                return;
            }

            _failedOpenAttempts++;
        }

        private void ResetFailedOpenAttempts()
        {
            _failedOpenAttempts = 0;
            _firstFailedOpenAttemptUtc = DateTime.MinValue;
        }

        private void ResetOpenState()
        {
            _content = null;

            if (_sessionKey != null)
                Array.Clear(_sessionKey, 0, _sessionKey.Length);

            _sessionKey = null;
            _salt = null;
            _currentFilePath = null;
            _storageFormat = VaultStorageFormat.Extended;
        }

        private static void TrySecureDeleteFile(string path)
        {
            try
            {
                var info = new FileInfo(path);
                long length = info.Length;

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                byte[] wipe = new byte[8192];
                long written = 0;

                while (written < length)
                {
                    int toWrite = (int)Math.Min(wipe.Length, length - written);
                    fs.Write(wipe, 0, toWrite);
                    written += toWrite;
                }

                fs.Flush(true);
            }
            catch
            {
                // Best effort overwrite.
            }
            finally
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // Best effort delete.
                }
            }
        }
    }
}
