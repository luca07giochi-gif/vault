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
    public sealed class VaultPortableReader : IDisposable
    {
        private const long MaxStandardImportableFileBytes = int.MaxValue - 4096L;
        private const int UltraFileChunkSizeBytes = 8 * 1024 * 1024;
        private const int PlaintextMagic = 0x5641554C; // "VAUL"
        private const int PlaintextFormatVersionStandard = 3;
        private const int PlaintextFormatVersionUltra = 4;
        private const int CopyBufferSize = 256 * 1024;

        private readonly VaultContent _content;
        private readonly VaultStorageFormat _storageFormat;
        private readonly byte[] _salt;
        private readonly string _sessionDirectory;
        private readonly Dictionary<Guid, FileContentHandle> _fileContent = new();
        private byte[]? _sessionKey;
        private bool _disposed;

        private VaultPortableReader(
            VaultContent content,
            VaultStorageFormat storageFormat,
            byte[] sessionKey,
            byte[] salt,
            string sessionDirectory,
            IDictionary<Guid, FileContentHandle>? initialContent)
        {
            _content = content ?? throw new ArgumentNullException(nameof(content));
            _storageFormat = storageFormat;
            _sessionKey = sessionKey ?? throw new ArgumentNullException(nameof(sessionKey));
            _salt = salt ?? throw new ArgumentNullException(nameof(salt));
            _sessionDirectory = sessionDirectory ?? throw new ArgumentNullException(nameof(sessionDirectory));

            Directory.CreateDirectory(_sessionDirectory);
            if (initialContent != null)
            {
                foreach (KeyValuePair<Guid, FileContentHandle> entry in initialContent)
                    _fileContent[entry.Key] = entry.Value;
            }
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

            paths.Insert(0, string.Empty);
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
                ContentChunks = new List<byte[]>(),
                ContentLengthOverride = 0
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

            using var source = new MemoryStream(content, writable: false);
            return AddFileFromStreamInternal(fileName, source, content.LongLength, targetFolderPath);
        }

        public VaultFileItem AddFileFromPath(string sourcePath, string? targetFolderPath)
        {
            ThrowIfDisposed();

            if (string.IsNullOrWhiteSpace(sourcePath))
                throw new ArgumentException(VaultText.T("core.error.invalidPath"), nameof(sourcePath));
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException(VaultText.T("core.error.fileNotFound"), sourcePath);

            var info = new FileInfo(sourcePath);
            using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.SequentialScan);

            string fileName = Path.GetFileName(sourcePath);
            return AddFileFromStreamInternal(fileName, source, info.Length, targetFolderPath);
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
            foreach (VaultFileItem descendant in _content.Files)
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

            List<Guid> requested = itemIds.Distinct().ToList();
            if (requested.Count == 0)
                return;

            List<VaultFileItem> selected = _content.Files.Where(f => requested.Contains(f.Id)).ToList();
            if (selected.Count == 0)
                return;

            List<string> selectedFolderPaths = selected
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

            List<VaultFileItem> roots = selected.Where(item =>
            {
                string itemPath = oldPaths[item.Id];
                return !selected.Any(parent =>
                    parent.IsFolder &&
                    parent.Id != item.Id &&
                    itemPath.StartsWith(oldPaths[parent.Id] + "/", StringComparison.OrdinalIgnoreCase));
            }).ToList();

            foreach (VaultFileItem root in roots)
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
                foreach (VaultFileItem descendant in _content.Files)
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

            List<Guid> requested = itemIds.Distinct().ToList();
            if (requested.Count == 0)
                return;

            List<VaultFileItem> selected = _content.Files.Where(f => requested.Contains(f.Id)).ToList();
            if (selected.Count == 0)
                return;

            var removeIds = new HashSet<Guid>(selected.Select(s => s.Id));
            List<string> selectedFolders = selected.Where(s => s.IsFolder).Select(s => s.FullPath).ToList();

            foreach (VaultFileItem item in _content.Files)
            {
                if (removeIds.Contains(item.Id))
                    continue;

                if (selectedFolders.Any(folder => item.FullPath.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)))
                    removeIds.Add(item.Id);
            }

            List<VaultFileItem> toRemove = _content.Files.Where(f => removeIds.Contains(f.Id)).ToList();
            foreach (VaultFileItem item in toRemove)
            {
                RemoveHandle(item.Id);
                ClearItemPayload(item);
                _content.Files.Remove(item);
            }

            IsDirty = true;
        }

        public byte[] ReadFileContent(Guid fileId)
        {
            ThrowIfDisposed();

            VaultFileItem file = GetFileItem(fileId);
            if (file.IsFolder)
                throw new InvalidOperationException(VaultText.T("core.error.selectedIsFolder"));

            long contentLength = file.ContentLength;
            if (contentLength == 0)
                return Array.Empty<byte>();

            if (contentLength > int.MaxValue)
                throw new InvalidOperationException(VaultText.F("core.error.fileTooLargeForFormat", file.FileName));

            using var ms = new MemoryStream((int)contentLength);
            CopyFileContentToStream(fileId, ms);
            return ms.ToArray();
        }

        public void CopyFileContentToStream(Guid fileId, Stream destination)
        {
            ThrowIfDisposed();

            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            if (!destination.CanWrite)
                throw new InvalidOperationException(VaultText.T("core.format.targetNotWritable"));

            VaultFileItem file = GetFileItem(fileId);
            if (file.IsFolder)
                throw new InvalidOperationException(VaultText.T("core.error.selectedIsFolder"));

            long contentLength = file.ContentLength;
            if (contentLength == 0)
                return;

            using Stream source = OpenContentReadStream(file);
            CopyExactly(
                source,
                destination,
                contentLength,
                VaultText.T("core.serializer.fileContentIncomplete"));
        }

        public byte[] ExportVaultBytes(IProgress<double>? progress = null)
        {
            ThrowIfDisposed();

            using var ms = new MemoryStream();
            SaveToStream(ms, progress);
            return ms.ToArray();
        }

        public void SaveToStream(Stream output, IProgress<double>? progress = null)
        {
            ThrowIfDisposed();

            if (output == null)
                throw new ArgumentNullException(nameof(output));
            if (!output.CanWrite)
                throw new InvalidOperationException(VaultText.T("core.format.targetNotWritable"));
            if (_sessionKey == null || _sessionKey.Length == 0)
                throw new InvalidOperationException(VaultText.T("core.error.noVaultOpen"));

            if (_storageFormat == VaultStorageFormat.Legacy)
            {
                byte[] bytes = SaveLegacy(progress);
                output.Write(bytes, 0, bytes.Length);
                Array.Clear(bytes, 0, bytes.Length);
                IsDirty = false;
                return;
            }

            bool ultra = _storageFormat == VaultStorageFormat.Ultra;
            SaveStreamingToStream(output, ultra, progress);
            IsDirty = false;
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

            string sessionDirectory = CreateSessionDirectory();

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

                OpenSessionData opened = format switch
                {
                    VaultStorageFormat.Legacy => ReadLegacy(vaultStream, header, sessionKey, sessionDirectory, progress),
                    VaultStorageFormat.Ultra or VaultStorageFormat.Extended => ReadStreaming(vaultStream, header, sessionKey, sessionDirectory, progress),
                    _ => throw new InvalidDataException(VaultText.T("core.error.unsupportedVaultVersion"))
                };

                ReportProgress(progress, 100);
                return new VaultPortableReader(
                    opened.Content,
                    format,
                    sessionKey,
                    header.Salt.ToArray(),
                    sessionDirectory,
                    opened.ContentHandles);
            }
            catch
            {
                if (sessionKey.Length > 0)
                    Array.Clear(sessionKey, 0, sessionKey.Length);

                CleanupSessionDirectory(sessionDirectory);
                throw;
            }
            finally
            {
                Array.Clear(pwdBytes, 0, pwdBytes.Length);
            }
        }

        private VaultFileItem AddFileFromStreamInternal(
            string fileName,
            Stream source,
            long contentLength,
            string? targetFolderPath)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (!source.CanRead)
                throw new InvalidOperationException(VaultText.T("core.format.sourceNotReadable"));
            if (contentLength < 0)
                throw new ArgumentOutOfRangeException(nameof(contentLength));

            string normalizedTarget = NormalizePath(targetFolderPath);
            EnsureFolderExists(normalizedTarget);

            bool allowLargeSingleFile = _storageFormat == VaultStorageFormat.Ultra;
            ValidateItemSize(contentLength, fileName, allowLargeSingleFile);

            string cleanName = SanitizeName(fileName, isFolder: false);
            string uniqueName = EnsureUniqueName(normalizedTarget, cleanName, isFolder: false);

            Guid id = Guid.NewGuid();
            FileContentHandle handle = CreateHandleFromStream(id, uniqueName, source, contentLength);

            var created = new VaultFileItem
            {
                Id = id,
                FileName = uniqueName,
                ParentPath = normalizedTarget,
                IsFolder = false,
                AddedTicks = DateTime.UtcNow.Ticks,
                Content = Array.Empty<byte>(),
                ContentChunks = new List<byte[]>(),
                ContentLengthOverride = contentLength
            };

            _fileContent[id] = handle;
            _content.Files.Add(created);
            IsDirty = true;
            return created;
        }

        private byte[] SaveLegacy(IProgress<double>? progress)
        {
            _content.Metadata.Version = PlaintextFormatVersionStandard;

            byte[] nonce = VaultFileFormat.GenerateNonce();
            var header = new VaultFileFormat.Header(
                VaultFileFormat.MAGIC,
                VaultFileFormat.LEGACY_VERSION,
                _salt,
                nonce);

            ReportProgress(progress, 5);

            byte[] plaintext;
            using (var plaintextStream = new MemoryStream())
            {
                WritePlaintext(plaintextStream, ultraContent: false, CreateScaledProgress(progress, 8, 55));
                plaintext = plaintextStream.ToArray();
            }

            ReportProgress(progress, 70);
            byte[] aad = VaultFileFormat.SerializeHeaderForAad(header);
            byte[] encrypted = AesGcmProvider.Encrypt(_sessionKey!, nonce, plaintext, aad);
            Array.Clear(plaintext, 0, plaintext.Length);

            using var output = new MemoryStream();
            WriteHeader(output, header);
            output.Write(encrypted, 0, encrypted.Length);
            Array.Clear(encrypted, 0, encrypted.Length);

            ReportProgress(progress, 100);
            return output.ToArray();
        }

        private void SaveStreamingToStream(Stream output, bool ultraContent, IProgress<double>? progress)
        {
            _content.Metadata.Version = ultraContent
                ? PlaintextFormatVersionUltra
                : PlaintextFormatVersionStandard;

            byte version = ultraContent
                ? VaultFileFormat.ULTRA_STREAMING_VERSION
                : VaultFileFormat.STREAMING_VERSION;

            byte[] nonce = VaultFileFormat.GenerateNonce();
            var header = new VaultFileFormat.Header(
                VaultFileFormat.MAGIC,
                version,
                _salt,
                nonce);

            using Stream encrypting = VaultFileFormat.CreateStreamingEncryptingWriteStream(output, _sessionKey!, header);
            WritePlaintext(encrypting, ultraContent, CreateScaledProgress(progress, 5, 98));
            ReportProgress(progress, 100);
        }

        private void WritePlaintext(Stream output, bool ultraContent, IProgress<double>? progress)
        {
            int formatVersion = ultraContent
                ? PlaintextFormatVersionUltra
                : PlaintextFormatVersionStandard;

            using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
            writer.Write(PlaintextMagic);
            writer.Write(formatVersion);
            writer.Write(_content.Metadata.CreatedTicks);
            writer.Write(_content.Files.Count);

            long totalContentBytes = 0;
            foreach (VaultFileItem file in _content.Files)
            {
                if (file.IsFolder)
                    continue;

                totalContentBytes = checked(totalContentBytes + file.ContentLength);
            }

            long writtenContentBytes = 0;
            int itemCount = _content.Files.Count;

            for (int i = 0; i < itemCount; i++)
            {
                VaultFileItem file = _content.Files[i];
                writer.Write(file.Id.ToByteArray());
                writer.Write(file.FileName ?? string.Empty);
                writer.Write(file.ParentPath ?? string.Empty);
                writer.Write(file.IsFolder);
                writer.Write(file.AddedTicks);

                if (ultraContent)
                {
                    WriteUltraPayload(
                        writer,
                        output,
                        file,
                        written =>
                        {
                            writtenContentBytes += written;
                            if (totalContentBytes > 0)
                                ReportProgress(progress, writtenContentBytes * 100.0 / totalContentBytes);
                        });
                }
                else
                {
                    WriteStandardPayload(
                        writer,
                        output,
                        file,
                        written =>
                        {
                            writtenContentBytes += written;
                            if (totalContentBytes > 0)
                                ReportProgress(progress, writtenContentBytes * 100.0 / totalContentBytes);
                        });
                }

                if (totalContentBytes == 0 && itemCount > 0)
                    ReportProgress(progress, (i + 1) * 100.0 / itemCount);
            }

            writer.Flush();
            ReportProgress(progress, 100);
        }

        private void WriteStandardPayload(
            BinaryWriter writer,
            Stream output,
            VaultFileItem file,
            Action<int>? onChunkWritten)
        {
            if (file.IsFolder)
            {
                writer.Write(0);
                return;
            }

            long contentLength = file.ContentLength;
            if (contentLength > int.MaxValue)
            {
                throw new InvalidOperationException(
                    VaultText.F("core.error.fileTooLargeForFormat", file.FileName));
            }

            writer.Write((int)contentLength);
            if (contentLength == 0)
                return;

            using Stream source = OpenContentReadStream(file);
            CopyExactly(
                source,
                output,
                contentLength,
                VaultText.T("core.serializer.contentInconsistent"),
                onChunkWritten);
        }

        private void WriteUltraPayload(
            BinaryWriter writer,
            Stream output,
            VaultFileItem file,
            Action<int>? onChunkWritten)
        {
            if (file.IsFolder)
            {
                writer.Write(0L);
                writer.Write(0);
                return;
            }

            long contentLength = file.ContentLength;
            writer.Write(contentLength);

            if (contentLength == 0)
            {
                writer.Write(0);
                return;
            }

            long chunkCountLong = checked((contentLength + UltraFileChunkSizeBytes - 1L) / UltraFileChunkSizeBytes);
            if (chunkCountLong > int.MaxValue)
                throw new InvalidOperationException(VaultText.T("core.serializer.chunkCountInvalid"));

            int chunkCount = (int)chunkCountLong;
            writer.Write(chunkCount);

            using Stream source = OpenContentReadStream(file);
            long remaining = contentLength;
            for (int i = 0; i < chunkCount; i++)
            {
                int chunkLength = (int)Math.Min(UltraFileChunkSizeBytes, remaining);
                writer.Write(chunkLength);
                CopyExactly(
                    source,
                    output,
                    chunkLength,
                    VaultText.T("core.serializer.contentInconsistent"),
                    onChunkWritten);

                remaining -= chunkLength;
            }

            if (remaining != 0)
                throw new InvalidOperationException(VaultText.T("core.serializer.contentInconsistent"));
        }

        private static void WriteHeader(Stream output, VaultFileFormat.Header header)
        {
            output.Write(VaultFileFormat.MAGIC, 0, VaultFileFormat.MAGIC.Length);
            output.WriteByte(header.Version);
            output.Write(header.Salt, 0, header.Salt.Length);
            output.Write(header.Nonce, 0, header.Nonce.Length);
        }

        private static OpenSessionData ReadLegacy(
            Stream vaultStream,
            VaultFileFormat.Header header,
            byte[] sessionKey,
            string sessionDirectory,
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
                if (decrypted.Length < 4 || BitConverter.ToInt32(decrypted, 0) != PlaintextMagic)
                    throw new CryptographicException(VaultText.T("core.error.passwordWrong"));

                var deserializeProgress = CreateScaledProgress(progress, 62, 94);
                VaultContent content = VaultSerializer.Deserialize(decrypted, deserializeProgress);
                Dictionary<Guid, FileContentHandle> handles = BuildHandlesFromInMemoryContent(content, sessionDirectory);
                ReportProgress(progress, 100);
                return new OpenSessionData(content, handles);
            }
            finally
            {
                Array.Clear(decrypted, 0, decrypted.Length);
            }
        }

        private static OpenSessionData ReadStreaming(
            Stream vaultStream,
            VaultFileFormat.Header header,
            byte[] sessionKey,
            string sessionDirectory,
            IProgress<double>? progress)
        {
            using Stream decryptedPayload = VaultFileFormat.CreateStreamingDecryptingReadStream(vaultStream, sessionKey, header);
            return DeserializeStreamingPlaintext(decryptedPayload, sessionDirectory, CreateScaledProgress(progress, 20, 98));
        }

        private static OpenSessionData DeserializeStreamingPlaintext(
            Stream input,
            string sessionDirectory,
            IProgress<double>? progress)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));

            using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);

            int magic = reader.ReadInt32();
            if (magic != PlaintextMagic)
                throw new CryptographicException(VaultText.T("core.serializer.corruptWrongPassword"));

            int version = reader.ReadInt32();
            var metadata = new VaultMetadata
            {
                Version = version,
                CreatedTicks = reader.ReadInt64()
            };

            int count = reader.ReadInt32();
            if (count < 0)
                throw new CryptographicException(VaultText.T("core.serializer.invalidItemCount"));

            var files = new List<VaultFileItem>(Math.Max(0, count));
            var handles = new Dictionary<Guid, FileContentHandle>();

            if (version >= PlaintextFormatVersionUltra)
            {
                for (int i = 0; i < count; i++)
                {
                    double itemStartPercent = count == 0 ? 100 : i * 100.0 / count;
                    double itemEndPercent = count == 0 ? 100 : (i + 1) * 100.0 / count;
                    ReportProgress(progress, itemStartPercent);

                    VaultFileItem item = ReadCommonItemMetadata(reader);

                    long contentLen = reader.ReadInt64();
                    if (contentLen < 0)
                        throw new CryptographicException(VaultText.T("core.serializer.fileLengthInvalid"));

                    int chunkCount = reader.ReadInt32();
                    if (chunkCount < 0)
                        throw new CryptographicException(VaultText.T("core.serializer.chunkCountInvalid"));

                    if (item.IsFolder)
                    {
                        if (contentLen != 0 || chunkCount != 0)
                            throw new CryptographicException(VaultText.T("core.serializer.folderContentInvalid"));
                        item.ContentLengthOverride = 0;
                    }
                    else
                    {
                        FileContentHandle handle = ReadUltraPayloadToHandle(
                            reader,
                            item.Id,
                            item.FileName,
                            contentLen,
                            chunkCount,
                            sessionDirectory);

                        handles[item.Id] = handle;
                        item.ContentLengthOverride = contentLen;
                    }

                    files.Add(item);
                    ReportProgress(progress, itemEndPercent);
                }

                EnsureNoTrailingData(input);
                ReportProgress(progress, 100);
                return new OpenSessionData(
                    new VaultContent { Metadata = metadata, Files = files },
                    handles);
            }

            if (version == PlaintextFormatVersionStandard)
            {
                for (int i = 0; i < count; i++)
                {
                    double itemStartPercent = count == 0 ? 100 : i * 100.0 / count;
                    double itemEndPercent = count == 0 ? 100 : (i + 1) * 100.0 / count;
                    ReportProgress(progress, itemStartPercent);

                    VaultFileItem item = ReadCommonItemMetadata(reader);

                    int contentLen = reader.ReadInt32();
                    if (contentLen < 0)
                        throw new CryptographicException(VaultText.T("core.serializer.fileLengthInvalid"));

                    if (item.IsFolder)
                    {
                        if (contentLen != 0)
                            throw new CryptographicException(VaultText.T("core.serializer.folderContentInvalid"));
                        item.ContentLengthOverride = 0;
                    }
                    else
                    {
                        FileContentHandle handle = ReadStandardPayloadToHandle(
                            reader,
                            item.Id,
                            item.FileName,
                            contentLen,
                            sessionDirectory);

                        handles[item.Id] = handle;
                        item.ContentLengthOverride = contentLen;
                    }

                    files.Add(item);
                    ReportProgress(progress, itemEndPercent);
                }

                EnsureNoTrailingData(input);
                ReportProgress(progress, 100);
                return new OpenSessionData(
                    new VaultContent { Metadata = metadata, Files = files },
                    handles);
            }

            if (version == 2)
            {
                for (int i = 0; i < count; i++)
                {
                    double itemStartPercent = count == 0 ? 100 : i * 100.0 / count;
                    double itemEndPercent = count == 0 ? 100 : (i + 1) * 100.0 / count;
                    ReportProgress(progress, itemStartPercent);

                    var file = new VaultFileItem
                    {
                        Id = new Guid(ReadExactly(reader.BaseStream, 16, VaultText.T("core.serializer.fileIdIncomplete"))),
                        FileName = reader.ReadString(),
                        ParentPath = string.Empty,
                        IsFolder = false,
                        AddedTicks = reader.ReadInt64(),
                        Content = Array.Empty<byte>(),
                        ContentChunks = new List<byte[]>()
                    };

                    int contentLen = reader.ReadInt32();
                    if (contentLen < 0)
                        throw new CryptographicException(VaultText.T("core.serializer.fileLengthInvalid"));

                    FileContentHandle handle = ReadStandardPayloadToHandle(
                        reader,
                        file.Id,
                        file.FileName,
                        contentLen,
                        sessionDirectory);

                    handles[file.Id] = handle;
                    file.ContentLengthOverride = contentLen;

                    files.Add(file);
                    ReportProgress(progress, itemEndPercent);
                }

                EnsureNoTrailingData(input);
                metadata.Version = PlaintextFormatVersionStandard;
                ReportProgress(progress, 100);
                return new OpenSessionData(
                    new VaultContent { Metadata = metadata, Files = files },
                    handles);
            }

            for (int i = 0; i < count; i++)
            {
                double itemStartPercent = count == 0 ? 100 : i * 100.0 / count;
                double itemEndPercent = count == 0 ? 100 : (i + 1) * 100.0 / count;
                ReportProgress(progress, itemStartPercent);

                Guid id = new Guid(ReadExactly(reader.BaseStream, 16, VaultText.T("core.serializer.legacyIdIncomplete")));
                string title = reader.ReadString();
                _ = reader.ReadString();
                _ = reader.ReadString();
                _ = reader.ReadString();
                string fileName = reader.ReadString();
                int contentLen = reader.ReadInt32();
                if (contentLen < 0)
                    throw new CryptographicException(VaultText.T("core.serializer.fileLengthInvalid"));

                if (contentLen == 0)
                {
                    ReportProgress(progress, itemEndPercent);
                    continue;
                }

                string finalName = string.IsNullOrWhiteSpace(fileName)
                    ? (string.IsNullOrWhiteSpace(title) ? $"legacy_file_{i + 1}" : title)
                    : fileName;

                var item = new VaultFileItem
                {
                    Id = id,
                    FileName = finalName,
                    ParentPath = string.Empty,
                    IsFolder = false,
                    AddedTicks = metadata.CreatedTicks,
                    Content = Array.Empty<byte>(),
                    ContentChunks = new List<byte[]>(),
                    ContentLengthOverride = contentLen
                };

                FileContentHandle handle = ReadStandardPayloadToHandle(
                    reader,
                    id,
                    finalName,
                    contentLen,
                    sessionDirectory);

                handles[id] = handle;
                files.Add(item);
                ReportProgress(progress, itemEndPercent);
            }

            EnsureNoTrailingData(input);
            metadata.Version = PlaintextFormatVersionStandard;
            ReportProgress(progress, 100);
            return new OpenSessionData(
                new VaultContent { Metadata = metadata, Files = files },
                handles);
        }

        private static VaultFileItem ReadCommonItemMetadata(BinaryReader reader)
        {
            return new VaultFileItem
            {
                Id = new Guid(ReadExactly(reader.BaseStream, 16, VaultText.T("core.serializer.fileIdIncomplete"))),
                FileName = reader.ReadString(),
                ParentPath = NormalizePath(reader.ReadString()),
                IsFolder = reader.ReadBoolean(),
                AddedTicks = reader.ReadInt64(),
                Content = Array.Empty<byte>(),
                ContentChunks = new List<byte[]>()
            };
        }

        private static FileContentHandle ReadStandardPayloadToHandle(
            BinaryReader reader,
            Guid itemId,
            string fileName,
            int contentLength,
            string sessionDirectory)
        {
            if (contentLength <= 0)
                return FileContentHandle.Empty();

            string tempPath = BuildSessionFilePath(sessionDirectory, itemId, fileName);
            using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.SequentialScan))
            {
                CopyExactly(
                    reader.BaseStream,
                    output,
                    contentLength,
                    VaultText.T("core.serializer.fileContentIncomplete"));
            }

            return FileContentHandle.FromTemporaryFile(tempPath, contentLength);
        }

        private static FileContentHandle ReadUltraPayloadToHandle(
            BinaryReader reader,
            Guid itemId,
            string fileName,
            long contentLength,
            int chunkCount,
            string sessionDirectory)
        {
            if (contentLength == 0)
            {
                long skipped = 0;
                for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
                {
                    int chunkLen = reader.ReadInt32();
                    if (chunkLen < 0)
                        throw new CryptographicException(VaultText.T("core.serializer.chunkSizeInvalid"));
                    if (chunkLen > 0)
                    {
                        CopyExactly(
                            reader.BaseStream,
                            Stream.Null,
                            chunkLen,
                            VaultText.T("core.serializer.chunkIncomplete"));
                        skipped += chunkLen;
                    }
                }

                if (skipped != 0)
                    throw new CryptographicException(VaultText.T("core.serializer.contentLengthMismatch"));

                return FileContentHandle.Empty();
            }

            string tempPath = BuildSessionFilePath(sessionDirectory, itemId, fileName);
            long copied = 0;
            using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.SequentialScan))
            {
                for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
                {
                    int chunkLen = reader.ReadInt32();
                    if (chunkLen < 0)
                        throw new CryptographicException(VaultText.T("core.serializer.chunkSizeInvalid"));

                    if (chunkLen == 0)
                        continue;

                    CopyExactly(
                        reader.BaseStream,
                        output,
                        chunkLen,
                        VaultText.T("core.serializer.chunkIncomplete"));
                    copied += chunkLen;
                }
            }

            if (copied != contentLength)
                throw new CryptographicException(VaultText.T("core.serializer.contentLengthMismatch"));

            return FileContentHandle.FromTemporaryFile(tempPath, contentLength);
        }

        private static Dictionary<Guid, FileContentHandle> BuildHandlesFromInMemoryContent(
            VaultContent content,
            string sessionDirectory)
        {
            var handles = new Dictionary<Guid, FileContentHandle>();
            foreach (VaultFileItem item in content.Files)
            {
                if (item.IsFolder)
                {
                    item.ContentLengthOverride = 0;
                    continue;
                }

                long contentLength = item.ContentLength;
                if (contentLength == 0)
                {
                    item.ContentLengthOverride = 0;
                    handles[item.Id] = FileContentHandle.Empty();
                    continue;
                }

                string tempPath = BuildSessionFilePath(sessionDirectory, item.Id, item.FileName);
                using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.SequentialScan))
                {
                    foreach (byte[] chunk in item.GetContentChunks())
                    {
                        if (chunk.Length == 0)
                            continue;

                        output.Write(chunk, 0, chunk.Length);
                    }
                }

                handles[item.Id] = FileContentHandle.FromTemporaryFile(tempPath, contentLength);
                ClearItemPayload(item);
                item.ContentLengthOverride = contentLength;
            }

            return handles;
        }

        private FileContentHandle CreateHandleFromStream(
            Guid itemId,
            string fileName,
            Stream source,
            long contentLength)
        {
            if (contentLength == 0)
                return FileContentHandle.Empty();

            string tempPath = BuildSessionFilePath(_sessionDirectory, itemId, fileName);
            using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.SequentialScan))
            {
                CopyExactly(
                    source,
                    output,
                    contentLength,
                    VaultText.T("core.serializer.fileContentIncomplete"));
            }

            return FileContentHandle.FromTemporaryFile(tempPath, contentLength);
        }

        private Stream OpenContentReadStream(VaultFileItem file)
        {
            if (_fileContent.TryGetValue(file.Id, out FileContentHandle? handle))
                return handle.OpenRead();

            long length = file.ContentLength;
            if (length == 0)
                return Stream.Null;

            if (!file.HasChunkedContent)
                return new MemoryStream(file.Content ?? Array.Empty<byte>(), writable: false);

            if (length > int.MaxValue)
                throw new InvalidOperationException(VaultText.F("core.error.fileTooLargeForFormat", file.FileName));

            var ms = new MemoryStream((int)length);
            foreach (byte[] chunk in file.GetContentChunks())
            {
                if (chunk.Length == 0)
                    continue;
                ms.Write(chunk, 0, chunk.Length);
            }

            ms.Position = 0;
            return ms;
        }

        private static string BuildSessionFilePath(string sessionDirectory, Guid id, string? fileName)
        {
            string extension = Path.GetExtension(fileName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(extension) || extension.Length > 16)
                extension = ".bin";

            return Path.Combine(sessionDirectory, $"{id:N}{extension}");
        }

        private static string CreateSessionDirectory()
        {
            string root = Path.Combine(Path.GetTempPath(), "vault-portable");
            Directory.CreateDirectory(root);

            string session = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(session);
            return session;
        }

        private static void CleanupSessionDirectory(string sessionDirectory)
        {
            if (string.IsNullOrWhiteSpace(sessionDirectory))
                return;

            try
            {
                if (Directory.Exists(sessionDirectory))
                    Directory.Delete(sessionDirectory, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }

        private static void CopyExactly(
            Stream source,
            Stream destination,
            long bytesToCopy,
            string errorMessage,
            Action<int>? onChunkCopied = null)
        {
            if (bytesToCopy < 0)
                throw new ArgumentOutOfRangeException(nameof(bytesToCopy));

            byte[] buffer = new byte[CopyBufferSize];
            long remaining = bytesToCopy;

            while (remaining > 0)
            {
                int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read <= 0)
                    throw new CryptographicException(errorMessage);

                destination.Write(buffer, 0, read);
                remaining -= read;
                onChunkCopied?.Invoke(read);
            }
        }

        private static byte[] ReadExactly(Stream input, int length, string errorMessage)
        {
            if (length < 0)
                throw new ArgumentOutOfRangeException(nameof(length));

            byte[] buffer = new byte[length];
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

        private static void EnsureNoTrailingData(Stream stream)
        {
            if (stream.CanSeek)
            {
                if (stream.Position != stream.Length)
                    throw new CryptographicException(VaultText.T("core.serializer.trailingData"));

                return;
            }

            int next = stream.ReadByte();
            if (next != -1)
                throw new CryptographicException(VaultText.T("core.serializer.trailingData"));
        }

        private static void ValidateItemSize(long sizeBytes, string itemName, bool allowLargeSingleFile)
        {
            if (allowLargeSingleFile || sizeBytes <= MaxStandardImportableFileBytes)
                return;

            throw new InvalidOperationException(
                VaultText.F("core.error.fileTooLargeForFormat", itemName));
        }

        private static void ClearItemPayload(VaultFileItem item)
        {
            if (item.Content.Length > 0)
                Array.Clear(item.Content, 0, item.Content.Length);

            if (item.ContentChunks.Count > 0)
            {
                foreach (byte[] chunk in item.ContentChunks)
                {
                    if (chunk.Length > 0)
                        Array.Clear(chunk, 0, chunk.Length);
                }

                item.ContentChunks.Clear();
            }

            item.Content = Array.Empty<byte>();
        }

        private VaultFileItem GetFileItem(Guid fileId)
        {
            VaultFileItem? file = _content.Files.FirstOrDefault(f => f.Id == fileId);
            if (file == null)
                throw new FileNotFoundException(VaultText.T("core.error.fileNotFound"));
            return file;
        }

        private void RemoveHandle(Guid fileId)
        {
            if (_fileContent.TryGetValue(fileId, out FileContentHandle? handle))
            {
                handle.Dispose();
                _fileContent.Remove(fileId);
            }
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

            foreach (FileContentHandle handle in _fileContent.Values)
                handle.Dispose();
            _fileContent.Clear();

            CleanupSessionDirectory(_sessionDirectory);
            _disposed = true;
        }

        private sealed class OpenSessionData
        {
            public OpenSessionData(VaultContent content, Dictionary<Guid, FileContentHandle> contentHandles)
            {
                Content = content ?? throw new ArgumentNullException(nameof(content));
                ContentHandles = contentHandles ?? throw new ArgumentNullException(nameof(contentHandles));
            }

            public VaultContent Content { get; }
            public Dictionary<Guid, FileContentHandle> ContentHandles { get; }
        }

        private sealed class FileContentHandle : IDisposable
        {
            private readonly string? _temporaryPath;
            private readonly bool _deleteOnDispose;

            private FileContentHandle(long length, string? temporaryPath, bool deleteOnDispose)
            {
                Length = length;
                _temporaryPath = temporaryPath;
                _deleteOnDispose = deleteOnDispose;
            }

            public long Length { get; }

            public static FileContentHandle Empty()
            {
                return new FileContentHandle(0, null, deleteOnDispose: false);
            }

            public static FileContentHandle FromTemporaryFile(string temporaryPath, long length)
            {
                if (string.IsNullOrWhiteSpace(temporaryPath))
                    throw new ArgumentException("Path non valido.", nameof(temporaryPath));
                if (length < 0)
                    throw new ArgumentOutOfRangeException(nameof(length));

                return new FileContentHandle(length, temporaryPath, deleteOnDispose: true);
            }

            public Stream OpenRead()
            {
                if (Length == 0)
                    return Stream.Null;

                if (string.IsNullOrWhiteSpace(_temporaryPath))
                    throw new FileNotFoundException(VaultText.T("core.error.fileNotFound"));
                if (!File.Exists(_temporaryPath))
                    throw new FileNotFoundException(VaultText.T("core.error.fileNotFound"), _temporaryPath);

                return new FileStream(
                    _temporaryPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    CopyBufferSize,
                    FileOptions.SequentialScan);
            }

            public void Dispose()
            {
                if (!_deleteOnDispose || string.IsNullOrWhiteSpace(_temporaryPath))
                    return;

                try
                {
                    if (File.Exists(_temporaryPath))
                        File.Delete(_temporaryPath);
                }
                catch
                {
                    // Best effort.
                }
            }
        }
    }
}
