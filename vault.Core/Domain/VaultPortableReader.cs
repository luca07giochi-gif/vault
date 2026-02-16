using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using vault.Core;
using vault.Core.Crypto;

namespace vault.Core.Domain
{
    // In-memory vault session for web/mobile scenarios:
    // open from byte[] and read/modify without filesystem dependencies.
    public sealed class VaultPortableReader : IDisposable
    {
        private const long MaxStandardImportableFileBytes = int.MaxValue - 4096L;
        private const int UltraFileChunkSizeBytes = 8 * 1024 * 1024;

        private readonly VaultContent _content;
        private readonly VaultStorageFormat _storageFormat;
        private readonly byte[] _salt;
        private byte[]? _sessionKey;
        private bool _disposed;

        private VaultPortableReader(
            VaultContent content,
            VaultStorageFormat storageFormat,
            byte[] sessionKey,
            byte[] salt)
        {
            _content = content ?? throw new ArgumentNullException(nameof(content));
            _storageFormat = storageFormat;
            _sessionKey = sessionKey ?? throw new ArgumentNullException(nameof(sessionKey));
            _salt = salt ?? throw new ArgumentNullException(nameof(salt));
        }

        public VaultStorageFormat StorageFormat => _storageFormat;

        public bool IsDirty { get; private set; }

        public IReadOnlyList<VaultFileItem> Files =>
            _content.Files.AsReadOnly();

        public IReadOnlyList<VaultFileItem> GetItemsInFolder(string? folderPath)
        {
            ThrowIfDisposed();

            string normalized = NormalizePath(folderPath);
            EnsureFolderExists(normalized);

            return _content.Files
                .Where(f => string.Equals(f.ParentPath, normalized, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.IsFolder)
                .ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public IReadOnlyList<string> GetAllFolderPaths()
        {
            ThrowIfDisposed();

            var paths = _content.Files
                .Where(f => f.IsFolder)
                .Select(f => f.FullPath)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            paths.Insert(0, string.Empty); // root
            return paths;
        }

        public VaultFileItem CreateFolder(string folderName, string? parentFolderPath)
        {
            ThrowIfDisposed();

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
                Content = Array.Empty<byte>(),
                ContentChunks = new List<byte[]>()
            };

            _content.Files.Add(created);
            IsDirty = true;
            return created;
        }

        public VaultFileItem AddFile(string fileName, byte[] content, string? targetFolderPath)
        {
            ThrowIfDisposed();

            if (content == null)
                throw new ArgumentNullException(nameof(content));

            string normalizedTarget = NormalizePath(targetFolderPath);
            EnsureFolderExists(normalizedTarget);

            bool allowLargeSingleFile = _storageFormat == VaultStorageFormat.Ultra;
            ValidateItemSize(content.LongLength, fileName, allowLargeSingleFile);

            string cleanName = SanitizeName(fileName, isFolder: false);
            string uniqueName = EnsureUniqueName(normalizedTarget, cleanName, isFolder: false);

            byte[] copied = content.Length == 0 ? Array.Empty<byte>() : content.ToArray();
            var payload = BuildPayloadFromBuffer(copied, allowLargeSingleFile);

            var created = new VaultFileItem
            {
                Id = Guid.NewGuid(),
                FileName = uniqueName,
                ParentPath = normalizedTarget,
                IsFolder = false,
                AddedTicks = DateTime.UtcNow.Ticks,
                Content = payload.Content,
                ContentChunks = payload.ContentChunks
            };

            _content.Files.Add(created);
            IsDirty = true;
            return created;
        }

        public void RenameItem(Guid itemId, string newName)
        {
            ThrowIfDisposed();

            if (string.IsNullOrWhiteSpace(newName))
                throw new ArgumentException(VaultText.T("core.error.invalidName"), nameof(newName));

            VaultFileItem? item = _content.Files.FirstOrDefault(f => f.Id == itemId);
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
                IsDirty = true;
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

            IsDirty = true;
        }

        public void MoveItems(IEnumerable<Guid> itemIds, string? destinationFolderPath)
        {
            ThrowIfDisposed();
            if (itemIds == null)
                throw new ArgumentNullException(nameof(itemIds));

            string destination = NormalizePath(destinationFolderPath);
            EnsureFolderExists(destination);

            var requested = itemIds.Distinct().ToList();
            if (requested.Count == 0)
                return;

            var selected = _content.Files.Where(f => requested.Contains(f.Id)).ToList();
            if (selected.Count == 0)
                return;

            var selectedFolderPaths = selected
                .Where(f => f.IsFolder)
                .Select(f => f.FullPath)
                .ToList();

            foreach (string folderPath in selectedFolderPaths)
            {
                if (string.Equals(destination, folderPath, StringComparison.OrdinalIgnoreCase) ||
                    destination.StartsWith(folderPath + "/", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(VaultText.T("core.error.invalidMoveDestinationSelf"));
                }
            }

            var oldPaths = _content.Files.ToDictionary(f => f.Id, f => f.FullPath);

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

                string desiredName = root.FileName;
                string uniqueName = EnsureUniqueName(destination, desiredName, root.IsFolder);
                root.ParentPath = destination;
                root.FileName = uniqueName;

                if (!root.IsFolder)
                    continue;

                string newRootPath = root.FullPath;
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

            IsDirty = true;
        }

        public void DeleteItems(IEnumerable<Guid> itemIds)
        {
            ThrowIfDisposed();
            if (itemIds == null)
                throw new ArgumentNullException(nameof(itemIds));

            var requested = itemIds.Distinct().ToList();
            if (requested.Count == 0)
                return;

            var selected = _content.Files.Where(f => requested.Contains(f.Id)).ToList();
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

            IsDirty = true;
        }

        public byte[] ReadFileContent(Guid fileId)
        {
            ThrowIfDisposed();

            VaultFileItem? file = _content.Files.FirstOrDefault(f => f.Id == fileId);
            if (file == null)
                throw new FileNotFoundException(VaultText.T("core.error.fileNotFound"));
            if (file.IsFolder)
                throw new InvalidOperationException(VaultText.T("core.error.selectedIsFolder"));

            long contentLength = file.ContentLength;
            if (contentLength == 0)
                return Array.Empty<byte>();

            if (contentLength > int.MaxValue)
                throw new InvalidOperationException(VaultText.F("core.error.fileTooLargeForFormat", file.FileName));

            using var ms = new MemoryStream((int)contentLength);
            foreach (byte[] chunk in file.GetContentChunks())
            {
                if (chunk.Length == 0)
                    continue;

                ms.Write(chunk, 0, chunk.Length);
            }

            return ms.ToArray();
        }

        public byte[] ExportVaultBytes(IProgress<double>? progress = null)
        {
            ThrowIfDisposed();
            if (_sessionKey == null || _sessionKey.Length == 0)
                throw new InvalidOperationException(VaultText.T("core.error.noVaultOpen"));

            if (_storageFormat == VaultStorageFormat.Legacy)
                return SaveLegacy(progress);

            bool ultra = _storageFormat == VaultStorageFormat.Ultra;
            return SaveStreaming(ultra, progress);
        }

        public static VaultPortableReader Open(
            byte[] vaultBytes,
            string password,
            bool allowUltra = false,
            IProgress<double>? progress = null)
        {
            if (vaultBytes == null)
                throw new ArgumentNullException(nameof(vaultBytes));

            using var ms = new MemoryStream(vaultBytes, writable: false);
            return Open(ms, password, allowUltra, progress);
        }

        public static VaultPortableReader Open(
            Stream vaultStream,
            string password,
            bool allowUltra = false,
            IProgress<double>? progress = null)
        {
            if (vaultStream == null)
                throw new ArgumentNullException(nameof(vaultStream));
            if (!vaultStream.CanRead)
                throw new InvalidOperationException(VaultText.T("core.format.sourceNotReadable"));

            if (string.IsNullOrWhiteSpace(password))
                throw new CryptographicException(VaultText.T("core.error.passwordWrong"));

            if (vaultStream.CanSeek)
            {
                vaultStream.Position = 0;
                if (vaultStream.Length < VaultFileFormat.HEADER_SIZE)
                    throw new InvalidDataException(VaultText.T("core.format.fileTooShort"));
            }

            ReportProgress(progress, 2);

            byte[] headerBytes = ReadExactly(vaultStream, VaultFileFormat.HEADER_SIZE, VaultText.T("core.format.headerIncomplete"));
            var header = VaultFileFormat.ReadHeaderFromBytes(headerBytes);
            ReportProgress(progress, 8);

            byte[] pwdBytes = Encoding.UTF8.GetBytes(password);
            byte[] sessionKey = Array.Empty<byte>();
            try
            {
                sessionKey = KeyDerivation.DeriveKey(pwdBytes, header.Salt);
                ReportProgress(progress, 20);

                VaultStorageFormat format = header.Version switch
                {
                    VaultFileFormat.LEGACY_VERSION => VaultStorageFormat.Legacy,
                    VaultFileFormat.ULTRA_STREAMING_VERSION => VaultStorageFormat.Ultra,
                    _ => VaultStorageFormat.Extended
                };

                if (format == VaultStorageFormat.Ultra && !allowUltra)
                    throw new NotSupportedException(VaultText.T("core.error.ultraNotSupportedInWeb"));

                VaultContent content = format switch
                {
                    VaultStorageFormat.Legacy => ReadLegacy(vaultStream, header, sessionKey, progress),
                    VaultStorageFormat.Ultra or VaultStorageFormat.Extended => ReadStreaming(vaultStream, header, sessionKey, progress),
                    _ => throw new InvalidDataException(VaultText.T("core.error.unsupportedVaultVersion"))
                };

                ReportProgress(progress, 100);
                return new VaultPortableReader(content, format, sessionKey, header.Salt.ToArray());
            }
            catch
            {
                if (sessionKey.Length > 0)
                    Array.Clear(sessionKey, 0, sessionKey.Length);
                throw;
            }
            finally
            {
                Array.Clear(pwdBytes, 0, pwdBytes.Length);
            }
        }

        private byte[] SaveLegacy(IProgress<double>? progress)
        {
            _content.Metadata.Version = 3;

            byte[] nonce = VaultFileFormat.GenerateNonce();
            var header = new VaultFileFormat.Header(
                VaultFileFormat.MAGIC,
                VaultFileFormat.LEGACY_VERSION,
                _salt,
                nonce);

            ReportProgress(progress, 5);
            byte[] plaintext = VaultSerializer.Serialize(
                _content,
                CreateScaledProgress(progress, 8, 55),
                ultraContent: false);

            ReportProgress(progress, 70);
            byte[] aad = VaultFileFormat.SerializeHeaderForAad(header);
            byte[] encrypted = AesGcmProvider.Encrypt(_sessionKey!, nonce, plaintext, aad);
            Array.Clear(plaintext, 0, plaintext.Length);

            using var output = new MemoryStream();
            WriteHeader(output, header);
            output.Write(encrypted, 0, encrypted.Length);
            Array.Clear(encrypted, 0, encrypted.Length);

            ReportProgress(progress, 100);
            IsDirty = false;
            return output.ToArray();
        }

        private byte[] SaveStreaming(bool ultraContent, IProgress<double>? progress)
        {
            _content.Metadata.Version = ultraContent ? 4 : 3;

            byte version = ultraContent
                ? VaultFileFormat.ULTRA_STREAMING_VERSION
                : VaultFileFormat.STREAMING_VERSION;

            byte[] nonce = VaultFileFormat.GenerateNonce();
            var header = new VaultFileFormat.Header(
                VaultFileFormat.MAGIC,
                version,
                _salt,
                nonce);

            using var output = new MemoryStream();
            using Stream encrypting = VaultFileFormat.CreateStreamingEncryptingWriteStream(output, _sessionKey!, header);
            VaultSerializer.SerializeToStream(
                _content,
                encrypting,
                CreateScaledProgress(progress, 5, 98),
                ultraContent: ultraContent);

            ReportProgress(progress, 100);
            IsDirty = false;
            return output.ToArray();
        }

        private static void WriteHeader(Stream output, VaultFileFormat.Header header)
        {
            output.Write(VaultFileFormat.MAGIC, 0, VaultFileFormat.MAGIC.Length);
            output.WriteByte(header.Version);
            output.Write(header.Salt, 0, header.Salt.Length);
            output.Write(header.Nonce, 0, header.Nonce.Length);
        }

        private static VaultContent ReadLegacy(
            Stream vaultStream,
            VaultFileFormat.Header header,
            byte[] sessionKey,
            IProgress<double>? progress)
        {
            byte[] encryptedPayload = ReadToEnd(vaultStream);
            if (encryptedPayload.Length <= 0)
                throw new InvalidDataException(VaultText.T("core.format.payloadMissing"));

            ReportProgress(progress, 38);

            byte[] aad = VaultFileFormat.SerializeHeaderForAad(header);
            byte[] decrypted = AesGcmProvider.Decrypt(sessionKey, header.Nonce, encryptedPayload, aad);
            Array.Clear(encryptedPayload, 0, encryptedPayload.Length);
            ReportProgress(progress, 62);

            try
            {
                if (decrypted.Length < 4 || BitConverter.ToInt32(decrypted, 0) != 0x5641554C)
                    throw new CryptographicException(VaultText.T("core.error.passwordWrong"));

                var deserializeProgress = CreateScaledProgress(progress, 62, 98);
                return VaultSerializer.Deserialize(decrypted, deserializeProgress);
            }
            finally
            {
                Array.Clear(decrypted, 0, decrypted.Length);
            }
        }

        private static VaultContent ReadStreaming(
            Stream vaultStream,
            VaultFileFormat.Header header,
            byte[] sessionKey,
            IProgress<double>? progress)
        {
            using Stream decryptedPayload = VaultFileFormat.CreateStreamingDecryptingReadStream(vaultStream, sessionKey, header);
            var deserializeProgress = CreateScaledProgress(progress, 20, 98);
            return VaultSerializer.Deserialize(decryptedPayload, deserializeProgress);
        }

        private static byte[] ReadExactly(Stream input, int length, string errorMessage)
        {
            if (length < 0)
                throw new ArgumentOutOfRangeException(nameof(length));

            var buffer = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = input.Read(buffer, offset, length - offset);
                if (read <= 0)
                    throw new InvalidDataException(errorMessage);

                offset += read;
            }

            return buffer;
        }

        private static byte[] ReadToEnd(Stream input)
        {
            using var ms = new MemoryStream();
            input.CopyTo(ms);
            return ms.ToArray();
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

        private static FilePayload BuildPayloadFromBuffer(byte[] content, bool allowLargeSingleFile)
        {
            if (!allowLargeSingleFile || content.LongLength <= MaxStandardImportableFileBytes)
                return new FilePayload(content, new List<byte[]>());

            var chunks = new List<byte[]>();
            int offset = 0;
            while (offset < content.Length)
            {
                int size = Math.Min(UltraFileChunkSizeBytes, content.Length - offset);
                var chunk = new byte[size];
                Buffer.BlockCopy(content, offset, chunk, 0, size);
                chunks.Add(chunk);
                offset += size;
            }

            Array.Clear(content, 0, content.Length);
            return new FilePayload(Array.Empty<byte>(), chunks);
        }

        private static void ValidateItemSize(long sizeBytes, string itemName, bool allowLargeSingleFile)
        {
            if (allowLargeSingleFile || sizeBytes <= MaxStandardImportableFileBytes)
                return;

            throw new InvalidOperationException(
                VaultText.F("core.error.fileTooLargeForFormat", itemName));
        }

        private bool FolderExists(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return true;

            return _content.Files.Any(f =>
                f.IsFolder &&
                string.Equals(f.FullPath, path, StringComparison.OrdinalIgnoreCase));
        }

        private void EnsureFolderExists(string path)
        {
            if (!FolderExists(path))
                throw new InvalidOperationException(VaultText.T("core.error.destinationFolderMissing"));
        }

        private string EnsureUniqueName(string parentPath, string desiredName, bool isFolder)
        {
            string clean = SanitizeName(desiredName, isFolder);

            bool Exists(string name) => _content.Files.Any(f =>
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

        private static string NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            string normalized = path.Replace('\\', '/').Trim().Trim('/');
            if (normalized.Length == 0)
                return string.Empty;

            string[] segments = normalized
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

        private static IProgress<double>? CreateScaledProgress(
            IProgress<double>? outer,
            double fromInclusive,
            double toInclusive)
        {
            if (outer == null)
                return null;

            double start = Math.Max(0, Math.Min(100, fromInclusive));
            double end = Math.Max(0, Math.Min(100, toInclusive));
            double span = Math.Max(0, end - start);

            return new Progress<double>(value =>
            {
                if (double.IsNaN(value) || double.IsInfinity(value))
                    return;

                double inner = Math.Max(0, Math.Min(100, value));
                double mapped = start + (inner / 100.0) * span;
                outer.Report(mapped);
            });
        }

        private static void ReportProgress(IProgress<double>? progress, double value)
        {
            if (progress == null)
                return;

            if (double.IsNaN(value) || double.IsInfinity(value))
                return;

            double clamped = Math.Max(0, Math.Min(100, value));
            progress.Report(clamped);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(VaultPortableReader));
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            if (_sessionKey != null && _sessionKey.Length > 0)
                Array.Clear(_sessionKey, 0, _sessionKey.Length);

            _sessionKey = null;
            _disposed = true;
        }
    }
}
