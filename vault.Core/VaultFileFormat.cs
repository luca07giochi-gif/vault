using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using vault.Core.Crypto;

namespace vault.Core
{
    public static class VaultFileFormat
    {
        public static readonly byte[] MAGIC = { 0x56, 0x41, 0x55, 0x4C }; // "VAUL"
        public const string VaultIdPrefix = "vlt_";

        public const byte LEGACY_VERSION = 1;
        public const byte STREAMING_VERSION = 2;
        public const byte ULTRA_STREAMING_VERSION = 3;
        public const byte LEGACY_VERSION_WITH_ID = 4;
        public const byte STREAMING_VERSION_WITH_ID = 5;
        public const byte ULTRA_STREAMING_VERSION_WITH_ID = 6;
        public const byte VERSION = ULTRA_STREAMING_VERSION_WITH_ID;

        public const int SALT_SIZE = 16;
        public const int NONCE_SIZE = 12;
        public const int VAULT_ID_SIZE = 16;
        public const int HEADER_SIZE_LEGACY = 4 + 1 + SALT_SIZE + NONCE_SIZE;
        public const int HEADER_SIZE_WITH_ID = HEADER_SIZE_LEGACY + VAULT_ID_SIZE;
        public const int HEADER_SIZE = HEADER_SIZE_WITH_ID;

        private const int ChunkTagSize = 16;
        private const int ChunkEndMarker = -1;

        // ---------- HEADER ----------
        public sealed class Header
        {
            public byte[] Magic { get; }
            public byte Version { get; }
            public byte[] Salt { get; }
            public byte[] Nonce { get; }
            public byte[]? VaultIdBytes { get; }
            public int Size { get; }
            public bool HasVaultId => VaultIdBytes != null && VaultIdBytes.Length == VAULT_ID_SIZE;
            public string? VaultId => HasVaultId ? FormatVaultId(VaultIdBytes!) : null;

            public Header(byte[] magic, byte version, byte[] salt, byte[] nonce, byte[]? vaultIdBytes = null)
            {
                Magic = magic ?? throw new ArgumentNullException(nameof(magic));
                Version = version;
                Salt = salt ?? throw new ArgumentNullException(nameof(salt));
                Nonce = nonce ?? throw new ArgumentNullException(nameof(nonce));
                VaultIdBytes = NormalizeVaultIdBytes(vaultIdBytes);
                Size = GetHeaderSize(version);
            }
        }

        public static Header ReadHeader(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ReadHeader(fs);
        }

        public static Header ReadHeader(Stream stream)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            if (!stream.CanRead)
                throw new InvalidOperationException(VaultText.T("core.format.sourceNotReadable"));

            byte[] prefix = new byte[5];
            stream.ReadExactly(prefix);
            ValidateMagicAndVersion(prefix);

            byte version = prefix[4];
            int headerSize = GetHeaderSize(version);
            byte[] headerBytes = new byte[headerSize];
            Buffer.BlockCopy(prefix, 0, headerBytes, 0, prefix.Length);
            if (headerSize > prefix.Length)
                stream.ReadExactly(headerBytes.AsSpan(prefix.Length));

            return ReadHeaderFromBytes(headerBytes);
        }

        internal static Header ReadHeaderFromBytes(byte[] headerBytes)
        {
            if (headerBytes.Length < HEADER_SIZE_LEGACY)
                throw new InvalidDataException(VaultText.T("core.format.headerIncomplete"));

            ValidateMagicAndVersion(headerBytes);
            byte version = headerBytes[4];

            byte[] salt = new byte[SALT_SIZE];
            Array.Copy(headerBytes, 5, salt, 0, SALT_SIZE);

            byte[] nonce = new byte[NONCE_SIZE];
            Array.Copy(headerBytes, 5 + SALT_SIZE, nonce, 0, NONCE_SIZE);

            byte[]? vaultIdBytes = null;
            if (HasVaultId(version))
            {
                if (headerBytes.Length < HEADER_SIZE_WITH_ID)
                    throw new InvalidDataException(VaultText.T("core.format.headerIncomplete"));

                vaultIdBytes = new byte[VAULT_ID_SIZE];
                Array.Copy(headerBytes, HEADER_SIZE_LEGACY, vaultIdBytes, 0, VAULT_ID_SIZE);
            }

            return new Header(MAGIC, version, salt, nonce, vaultIdBytes);
        }

        // ---------- GENERATORS ----------
        public static byte[] GenerateSalt() =>
            RandomNumberGenerator.GetBytes(SALT_SIZE);

        public static byte[] GenerateNonce() =>
            RandomNumberGenerator.GetBytes(NONCE_SIZE);

        public static byte[] GenerateVaultIdBytes() =>
            RandomNumberGenerator.GetBytes(VAULT_ID_SIZE);

        public static string GenerateVaultId() =>
            FormatVaultId(GenerateVaultIdBytes());

        public static string FormatVaultId(byte[] vaultIdBytes)
        {
            byte[] normalized = NormalizeVaultIdBytes(vaultIdBytes)
                ?? throw new ArgumentException("VaultId non valido.", nameof(vaultIdBytes));

            return $"{VaultIdPrefix}{Convert.ToHexString(normalized).ToLowerInvariant()}";
        }

        public static byte[] ParseVaultId(string? vaultId)
        {
            if (string.IsNullOrWhiteSpace(vaultId))
                throw new ArgumentException("VaultId non valido.", nameof(vaultId));

            string trimmed = vaultId.Trim();
            if (trimmed.StartsWith(VaultIdPrefix, StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed[VaultIdPrefix.Length..];

            if (trimmed.Length != VAULT_ID_SIZE * 2)
                throw new ArgumentException("VaultId non valido.", nameof(vaultId));

            return Convert.FromHexString(trimmed);
        }

        public static bool HasVaultId(byte version) =>
            version == LEGACY_VERSION_WITH_ID ||
            version == STREAMING_VERSION_WITH_ID ||
            version == ULTRA_STREAMING_VERSION_WITH_ID;

        public static int GetHeaderSize(byte version) =>
            HasVaultId(version) ? HEADER_SIZE_WITH_ID : HEADER_SIZE_LEGACY;

        public static VaultStorageKind GetStorageKind(byte version) => version switch
        {
            LEGACY_VERSION or LEGACY_VERSION_WITH_ID => VaultStorageKind.Legacy,
            ULTRA_STREAMING_VERSION or ULTRA_STREAMING_VERSION_WITH_ID => VaultStorageKind.Ultra,
            STREAMING_VERSION or STREAMING_VERSION_WITH_ID => VaultStorageKind.Extended,
            _ => throw new InvalidDataException(VaultText.T("core.format.versionUnsupported"))
        };

        public static byte MapToVersionWithVaultId(byte version) => GetStorageKind(version) switch
        {
            VaultStorageKind.Legacy => LEGACY_VERSION_WITH_ID,
            VaultStorageKind.Extended => STREAMING_VERSION_WITH_ID,
            VaultStorageKind.Ultra => ULTRA_STREAMING_VERSION_WITH_ID,
            _ => throw new InvalidDataException(VaultText.T("core.format.versionUnsupported"))
        };

        // ---------- LEGACY PAYLOAD (v1) ----------
        public static byte[] ReadEncryptedPayload(string filePath)
        {
            Header header = ReadHeader(filePath);
            if (GetStorageKind(header.Version) != VaultStorageKind.Legacy)
            {
                throw new InvalidOperationException(
                    VaultText.T("core.format.streamingNotLegacy"));
            }

            long fileSize = new FileInfo(filePath).Length;
            if (fileSize > int.MaxValue)
            {
                throw new InvalidOperationException(
                    VaultText.T("core.format.vaultTooLargeLegacy"));
            }

            byte[] all = File.ReadAllBytes(filePath);

            if (all.Length <= header.Size)
                throw new InvalidDataException(VaultText.T("core.format.payloadMissing"));

            byte[] payload = new byte[all.Length - header.Size];
            Array.Copy(all, header.Size, payload, 0, payload.Length);

            return payload;
        }

        public static void WriteVault(
            string filePath,
            byte[] encryptedPayload,
            byte[] salt,
            byte[] nonce,
            byte version = LEGACY_VERSION,
            byte[]? vaultIdBytes = null)
        {
            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            WriteHeader(fs, version, salt, nonce, vaultIdBytes);
            fs.Write(encryptedPayload);
        }

        // ---------- STREAMING PAYLOAD (v2) ----------
        public static Stream CreateStreamingEncryptingWriteStream(
            Stream targetStream,
            byte[] key,
            Header header,
            int chunkSize = 4 * 1024 * 1024)
        {
            if (targetStream == null)
                throw new ArgumentNullException(nameof(targetStream));
            if (key == null)
                throw new ArgumentNullException(nameof(key));
            if (header == null)
                throw new ArgumentNullException(nameof(header));
            if (GetStorageKind(header.Version) is not (VaultStorageKind.Extended or VaultStorageKind.Ultra))
                throw new InvalidOperationException(VaultText.T("core.format.headerNotStreaming"));
            if (!targetStream.CanWrite)
                throw new InvalidOperationException(VaultText.T("core.format.targetNotWritable"));
            if (chunkSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(chunkSize), VaultText.T("core.format.chunkSizeInvalid"));

            WriteHeader(targetStream, header.Version, header.Salt, header.Nonce, header.VaultIdBytes);
            byte[] headerAad = SerializeHeaderForAad(header);
            return new ChunkedEncryptingWriteStream(targetStream, key, headerAad, header.Nonce, chunkSize);
        }

        public static Stream CreateStreamingDecryptingReadStream(
            Stream sourceStream,
            byte[] key,
            Header header)
        {
            if (sourceStream == null)
                throw new ArgumentNullException(nameof(sourceStream));
            if (key == null)
                throw new ArgumentNullException(nameof(key));
            if (header == null)
                throw new ArgumentNullException(nameof(header));
            if (GetStorageKind(header.Version) is not (VaultStorageKind.Extended or VaultStorageKind.Ultra))
                throw new InvalidOperationException(VaultText.T("core.format.headerNotStreaming"));
            if (!sourceStream.CanRead)
                throw new InvalidOperationException(VaultText.T("core.format.sourceNotReadable"));

            byte[] headerAad = SerializeHeaderForAad(header);
            return new ChunkedDecryptingReadStream(sourceStream, key, headerAad, header.Nonce);
        }

        // ---------- AAD ----------
        public static byte[] SerializeHeaderForAad(Header header)
        {
            if (header == null)
                throw new ArgumentNullException(nameof(header));

            using var ms = new MemoryStream();
            ms.Write(header.Magic);
            ms.WriteByte(header.Version);
            ms.Write(header.Salt);
            ms.Write(header.Nonce);
            if (header.HasVaultId)
                ms.Write(header.VaultIdBytes!);

            return ms.ToArray();
        }

        private static void WriteHeader(Stream stream, byte version, byte[] salt, byte[] nonce, byte[]? vaultIdBytes = null)
        {
            stream.Write(MAGIC);
            stream.WriteByte(version);
            stream.Write(salt, 0, SALT_SIZE);
            stream.Write(nonce, 0, NONCE_SIZE);
            if (HasVaultId(version))
            {
                byte[] normalized = NormalizeVaultIdBytes(vaultIdBytes)
                    ?? throw new InvalidOperationException("VaultId mancante per il formato del vault.");
                stream.Write(normalized, 0, normalized.Length);
            }
        }

        private static void ValidateMagicAndVersion(ReadOnlySpan<byte> headerBytes)
        {
            for (int i = 0; i < 4; i++)
            {
                if (headerBytes[i] != MAGIC[i])
                    throw new InvalidDataException(VaultText.T("core.format.magicInvalid"));
            }

            byte version = headerBytes[4];
            if (version != LEGACY_VERSION &&
                version != STREAMING_VERSION &&
                version != ULTRA_STREAMING_VERSION &&
                version != LEGACY_VERSION_WITH_ID &&
                version != STREAMING_VERSION_WITH_ID &&
                version != ULTRA_STREAMING_VERSION_WITH_ID)
            {
                throw new InvalidDataException(VaultText.T("core.format.versionUnsupported"));
            }
        }

        private static byte[]? NormalizeVaultIdBytes(byte[]? vaultIdBytes)
        {
            if (vaultIdBytes == null)
                return null;
            if (vaultIdBytes.Length != VAULT_ID_SIZE)
                throw new ArgumentException("VaultId non valido.", nameof(vaultIdBytes));

            return vaultIdBytes.ToArray();
        }

        public enum VaultStorageKind
        {
            Legacy,
            Extended,
            Ultra
        }

        private static byte[] DeriveChunkNonce(byte[] baseNonce, int chunkIndex)
        {
            byte[] nonce = new byte[NONCE_SIZE];
            Buffer.BlockCopy(baseNonce, 0, nonce, 0, NONCE_SIZE);
            BinaryPrimitives.WriteInt32BigEndian(nonce.AsSpan(NONCE_SIZE - sizeof(int)), chunkIndex);
            return nonce;
        }

        private static byte[] BuildChunkAad(byte[] headerAad, int chunkIndex, int plainLength)
        {
            byte[] aad = new byte[headerAad.Length + sizeof(int) + sizeof(int)];
            Buffer.BlockCopy(headerAad, 0, aad, 0, headerAad.Length);
            BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(headerAad.Length, sizeof(int)), chunkIndex);
            BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(headerAad.Length + sizeof(int), sizeof(int)), plainLength);
            return aad;
        }

        private static void WriteInt32(Stream stream, int value)
        {
            Span<byte> buffer = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
            stream.Write(buffer);
        }

        private static int ReadInt32(Stream stream)
        {
            Span<byte> buffer = stackalloc byte[sizeof(int)];
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = stream.Read(buffer[offset..]);
                if (read == 0)
                    throw new InvalidDataException(VaultText.T("core.format.chunkLengthIncomplete"));

                offset += read;
            }

            return BinaryPrimitives.ReadInt32LittleEndian(buffer);
        }

        private static byte[] ReadExactBytes(Stream stream, int length, string errorMessage)
        {
            byte[] result = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = stream.Read(result, offset, length - offset);
                if (read == 0)
                    throw new InvalidDataException(errorMessage);

                offset += read;
            }

            return result;
        }

        private sealed class ChunkedEncryptingWriteStream : Stream
        {
            private readonly Stream _target;
            private readonly byte[] _key;
            private readonly byte[] _headerAad;
            private readonly byte[] _baseNonce;
            private readonly byte[] _buffer;

            private int _bufferCount;
            private int _chunkIndex;
            private bool _finalized;
            private bool _disposed;

            public ChunkedEncryptingWriteStream(
                Stream target,
                byte[] key,
                byte[] headerAad,
                byte[] baseNonce,
                int chunkSize)
            {
                _target = target;
                _key = key;
                _headerAad = headerAad;
                _baseNonce = baseNonce;
                _buffer = new byte[chunkSize];
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => !_disposed;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
                _target.Flush();
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();

            public override void SetLength(long value) =>
                throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(ChunkedEncryptingWriteStream));
                if (buffer == null)
                    throw new ArgumentNullException(nameof(buffer));
                if (offset < 0 || count < 0 || offset + count > buffer.Length)
                    throw new ArgumentOutOfRangeException(nameof(count));

                while (count > 0)
                {
                    int toCopy = Math.Min(count, _buffer.Length - _bufferCount);
                    Buffer.BlockCopy(buffer, offset, _buffer, _bufferCount, toCopy);
                    _bufferCount += toCopy;
                    offset += toCopy;
                    count -= toCopy;

                    if (_bufferCount == _buffer.Length)
                        EncryptAndWriteCurrentBuffer(_bufferCount);
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (_disposed)
                    return;

                if (disposing)
                {
                    FinalizePayload();
                    CryptographicOperations.ZeroMemory(_buffer);
                }

                _disposed = true;
                base.Dispose(disposing);
            }

            private void FinalizePayload()
            {
                if (_finalized)
                    return;

                if (_bufferCount > 0)
                    EncryptAndWriteCurrentBuffer(_bufferCount);

                WriteInt32(_target, ChunkEndMarker);
                _target.Flush();
                _finalized = true;
            }

            private void EncryptAndWriteCurrentBuffer(int plainLength)
            {
                byte[] plaintext = new byte[plainLength];
                Buffer.BlockCopy(_buffer, 0, plaintext, 0, plainLength);

                byte[] nonce = DeriveChunkNonce(_baseNonce, _chunkIndex);
                byte[] chunkAad = BuildChunkAad(_headerAad, _chunkIndex, plainLength);

                byte[] cipherAndTag = AesGcmProvider.Encrypt(_key, nonce, plaintext, chunkAad);
                CryptographicOperations.ZeroMemory(plaintext);

                WriteInt32(_target, plainLength);
                _target.Write(cipherAndTag, 0, cipherAndTag.Length);
                CryptographicOperations.ZeroMemory(cipherAndTag);

                _chunkIndex = checked(_chunkIndex + 1);
                _bufferCount = 0;
            }
        }

        private sealed class ChunkedDecryptingReadStream : Stream
        {
            private readonly Stream _source;
            private readonly byte[] _key;
            private readonly byte[] _headerAad;
            private readonly byte[] _baseNonce;

            private byte[]? _currentChunk;
            private int _currentChunkOffset;
            private int _chunkIndex;
            private bool _reachedEnd;
            private bool _disposed;

            public ChunkedDecryptingReadStream(
                Stream source,
                byte[] key,
                byte[] headerAad,
                byte[] baseNonce)
            {
                _source = source;
                _key = key;
                _headerAad = headerAad;
                _baseNonce = baseNonce;
            }

            public override bool CanRead => !_disposed;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
                // No-op.
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(ChunkedDecryptingReadStream));
                if (buffer == null)
                    throw new ArgumentNullException(nameof(buffer));
                if (offset < 0 || count < 0 || offset + count > buffer.Length)
                    throw new ArgumentOutOfRangeException(nameof(count));
                if (count == 0)
                    return 0;

                int total = 0;
                while (count > 0)
                {
                    if (_currentChunk == null || _currentChunkOffset >= _currentChunk.Length)
                    {
                        if (!LoadNextChunk())
                            break;
                    }

                    int available = _currentChunk!.Length - _currentChunkOffset;
                    int toCopy = Math.Min(available, count);
                    Buffer.BlockCopy(_currentChunk, _currentChunkOffset, buffer, offset, toCopy);
                    _currentChunkOffset += toCopy;
                    offset += toCopy;
                    count -= toCopy;
                    total += toCopy;
                }

                return total;
            }

            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();

            public override void SetLength(long value) =>
                throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (_disposed)
                    return;

                if (disposing && _currentChunk != null)
                {
                    CryptographicOperations.ZeroMemory(_currentChunk);
                    _currentChunk = null;
                }

                _disposed = true;
                base.Dispose(disposing);
            }

            private bool LoadNextChunk()
            {
                if (_reachedEnd)
                    return false;

                int plainLength = ReadInt32(_source);
                if (plainLength == ChunkEndMarker)
                {
                    _reachedEnd = true;
                    EnsureNoTrailingDataAfterEndMarker();
                    return false;
                }

                if (plainLength < 0)
                    throw new InvalidDataException(VaultText.T("core.format.chunkLengthInvalid"));

                int cipherLength = checked(plainLength + ChunkTagSize);
                byte[] cipherAndTag = ReadExactBytes(
                    _source,
                    cipherLength,
                    VaultText.T("core.format.encryptedChunkIncomplete"));

                byte[] nonce = DeriveChunkNonce(_baseNonce, _chunkIndex);
                byte[] chunkAad = BuildChunkAad(_headerAad, _chunkIndex, plainLength);

                byte[] plaintext = AesGcmProvider.Decrypt(_key, nonce, cipherAndTag, chunkAad);
                CryptographicOperations.ZeroMemory(cipherAndTag);

                if (plaintext.Length != plainLength)
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                    throw new InvalidDataException(VaultText.T("core.format.decryptedChunkLengthInvalid"));
                }

                if (_currentChunk != null)
                    CryptographicOperations.ZeroMemory(_currentChunk);

                _currentChunk = plaintext;
                _currentChunkOffset = 0;
                _chunkIndex = checked(_chunkIndex + 1);
                return true;
            }

            private void EnsureNoTrailingDataAfterEndMarker()
            {
                if (_source.CanSeek)
                {
                    if (_source.Position != _source.Length)
                        throw new InvalidDataException(VaultText.T("core.format.trailingData"));

                    return;
                }

                int next = _source.ReadByte();
                if (next != -1)
                    throw new InvalidDataException(VaultText.T("core.format.trailingData"));
            }
        }
    }
}
