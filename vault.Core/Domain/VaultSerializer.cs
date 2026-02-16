using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using vault.Core;

namespace vault.Core.Domain
{
    public static class VaultSerializer
    {
        private const int PLAINTEXT_MAGIC = 0x5641554C; // "VAUL"
        private const int FORMAT_VERSION_STANDARD = 3;
        private const int FORMAT_VERSION_ULTRA = 4;

        public static byte[] Serialize(
            VaultContent vault,
            IProgress<double>? progress = null,
            bool ultraContent = false)
        {
            using var ms = new MemoryStream();
            SerializeToStream(vault, ms, progress, ultraContent);
            return ms.ToArray();
        }

        public static void SerializeToStream(
            VaultContent vault,
            Stream output,
            IProgress<double>? progress = null,
            bool ultraContent = false)
        {
            if (vault == null) throw new ArgumentNullException(nameof(vault));
            if (output == null) throw new ArgumentNullException(nameof(output));

            int formatVersion = ultraContent ? FORMAT_VERSION_ULTRA : FORMAT_VERSION_STANDARD;
            using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);

            writer.Write(PLAINTEXT_MAGIC);
            writer.Write(formatVersion);
            writer.Write(vault.Metadata.CreatedTicks);
            writer.Write(vault.Files.Count);

            long totalContentBytes = 0;
            foreach (var file in vault.Files)
            {
                if (file.IsFolder)
                    continue;

                totalContentBytes = checked(totalContentBytes + file.ContentLength);
            }

            long writtenContentBytes = 0;
            int itemCount = vault.Files.Count;

            for (int i = 0; i < itemCount; i++)
            {
                var file = vault.Files[i];
                writer.Write(file.Id.ToByteArray());
                writer.Write(file.FileName ?? string.Empty);
                writer.Write(file.ParentPath ?? string.Empty);
                writer.Write(file.IsFolder);
                writer.Write(file.AddedTicks);

                if (formatVersion >= FORMAT_VERSION_ULTRA)
                {
                    WriteUltraPayload(
                        writer,
                        output,
                        file,
                        bytesWritten =>
                        {
                            writtenContentBytes += bytesWritten;
                            if (totalContentBytes > 0)
                            {
                                ReportProgress(progress, writtenContentBytes * 100.0 / totalContentBytes);
                            }
                        });
                }
                else
                {
                    WriteStandardPayload(
                        writer,
                        output,
                        file,
                        bytesWritten =>
                        {
                            writtenContentBytes += bytesWritten;
                            if (totalContentBytes > 0)
                            {
                                ReportProgress(progress, writtenContentBytes * 100.0 / totalContentBytes);
                            }
                        });
                }

                if (totalContentBytes == 0 && itemCount > 0)
                {
                    ReportProgress(progress, (i + 1) * 100.0 / itemCount);
                }
            }

            writer.Flush();
            ReportProgress(progress, 100);
        }

        public static long EstimateSerializedSize(VaultContent vault, bool ultraContent = false)
        {
            if (vault == null)
                throw new ArgumentNullException(nameof(vault));

            long total =
                sizeof(int) +   // magic
                sizeof(int) +   // format version
                sizeof(long) +  // created ticks
                sizeof(int);    // file count

            foreach (var file in vault.Files)
            {
                total = checked(total + 16); // Guid
                total = checked(total + GetEncodedStringSize(file.FileName ?? string.Empty));
                total = checked(total + GetEncodedStringSize(file.ParentPath ?? string.Empty));
                total = checked(total + sizeof(bool));
                total = checked(total + sizeof(long));

                long contentLength = file.IsFolder ? 0 : file.ContentLength;
                if (ultraContent)
                {
                    total = checked(total + sizeof(long)); // total content length
                    total = checked(total + sizeof(int));  // chunk count
                    total = checked(total + GetChunkCountForWrite(file) * sizeof(int));
                    total = checked(total + contentLength);
                }
                else
                {
                    total = checked(total + sizeof(int)); // content length prefix
                    total = checked(total + contentLength);
                }
            }

            return total;
        }

        public static VaultContent Deserialize(byte[] data, IProgress<double>? progress = null)
        {
            if (data == null || data.Length < sizeof(int))
                throw new CryptographicException(VaultText.T("core.serializer.vaultInvalid"));

            using var ms = new MemoryStream(data, writable: false);
            return Deserialize(ms, progress);
        }

        public static VaultContent Deserialize(Stream input, IProgress<double>? progress = null)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));

            if (input.CanSeek && input.Length - input.Position < sizeof(int))
                throw new CryptographicException(VaultText.T("core.serializer.vaultInvalid"));

            using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);

            int magic = reader.ReadInt32();
            if (magic != PLAINTEXT_MAGIC)
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

            var files = new List<VaultFileItem>(count);

            if (version >= FORMAT_VERSION_ULTRA)
            {
                for (int i = 0; i < count; i++)
                {
                    double itemStartPercent = count == 0 ? 100 : i * 100.0 / count;
                    double itemEndPercent = count == 0 ? 100 : (i + 1) * 100.0 / count;
                    ReportProgress(progress, itemStartPercent);

                    var item = new VaultFileItem
                    {
                        Id = new Guid(ReadRequiredBytes(reader, 16, VaultText.T("core.serializer.fileIdIncomplete"))),
                        FileName = reader.ReadString(),
                        ParentPath = NormalizePath(reader.ReadString()),
                        IsFolder = reader.ReadBoolean(),
                        AddedTicks = reader.ReadInt64()
                    };

                    long contentLen = reader.ReadInt64();
                    if (contentLen < 0)
                        throw new CryptographicException(VaultText.T("core.serializer.fileLengthInvalid"));

                    int chunkCount = reader.ReadInt32();
                    if (chunkCount < 0)
                        throw new CryptographicException(VaultText.T("core.serializer.chunkCountInvalid"));

                    var chunks = new List<byte[]>(Math.Max(0, chunkCount));
                    long readContentBytes = 0;
                    for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
                    {
                        int chunkLen = reader.ReadInt32();
                        if (chunkLen < 0)
                            throw new CryptographicException(VaultText.T("core.serializer.chunkSizeInvalid"));

                        byte[] chunk = ReadRequiredBytes(
                            reader,
                            chunkLen,
                            VaultText.T("core.serializer.chunkIncomplete"),
                            chunkRead =>
                            {
                                readContentBytes += chunkRead;
                                if (contentLen > 0)
                                {
                                    double itemProgress = itemStartPercent +
                                        (readContentBytes * 1.0 / contentLen) * (itemEndPercent - itemStartPercent);
                                    ReportProgress(progress, itemProgress);
                                }
                            });

                        if (chunk.Length > 0)
                            chunks.Add(chunk);
                    }

                    if (readContentBytes != contentLen)
                        throw new CryptographicException(VaultText.T("core.serializer.contentLengthMismatch"));

                    if (item.IsFolder)
                    {
                        if (contentLen != 0 || chunks.Count > 0)
                            throw new CryptographicException(VaultText.T("core.serializer.folderContentInvalid"));

                        item.Content = Array.Empty<byte>();
                        item.ContentChunks = new List<byte[]>();
                    }
                    else
                    {
                        item.Content = Array.Empty<byte>();
                        item.ContentChunks = chunks;
                    }

                    files.Add(item);
                    ReportProgress(progress, itemEndPercent);
                }

                EnsureNoTrailingData(input);
                ReportProgress(progress, 100);
                return new VaultContent { Metadata = metadata, Files = files };
            }

            if (version == FORMAT_VERSION_STANDARD)
            {
                for (int i = 0; i < count; i++)
                {
                    double itemStartPercent = count == 0 ? 100 : i * 100.0 / count;
                    double itemEndPercent = count == 0 ? 100 : (i + 1) * 100.0 / count;
                    ReportProgress(progress, itemStartPercent);

                    var item = new VaultFileItem
                    {
                        Id = new Guid(ReadRequiredBytes(reader, 16, VaultText.T("core.serializer.fileIdIncomplete"))),
                        FileName = reader.ReadString(),
                        ParentPath = NormalizePath(reader.ReadString()),
                        IsFolder = reader.ReadBoolean(),
                        AddedTicks = reader.ReadInt64(),
                        ContentChunks = new List<byte[]>()
                    };

                    int contentLen = reader.ReadInt32();
                    if (contentLen < 0)
                        throw new CryptographicException(VaultText.T("core.serializer.fileLengthInvalid"));

                    int readContentBytes = 0;
                    byte[] content = ReadRequiredBytes(
                        reader,
                        contentLen,
                        VaultText.T("core.serializer.fileContentIncomplete"),
                        chunkRead =>
                        {
                            readContentBytes += chunkRead;
                            if (contentLen > 0)
                            {
                                double itemProgress = itemStartPercent +
                                    (readContentBytes * 1.0 / contentLen) * (itemEndPercent - itemStartPercent);
                                ReportProgress(progress, itemProgress);
                            }
                        });

                    item.Content = item.IsFolder ? Array.Empty<byte>() : content;
                    files.Add(item);
                    ReportProgress(progress, itemEndPercent);
                }

                EnsureNoTrailingData(input);
                ReportProgress(progress, 100);
                return new VaultContent { Metadata = metadata, Files = files };
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
                        Id = new Guid(ReadRequiredBytes(reader, 16, VaultText.T("core.serializer.fileIdIncomplete"))),
                        FileName = reader.ReadString(),
                        ParentPath = string.Empty,
                        IsFolder = false,
                        AddedTicks = reader.ReadInt64(),
                        ContentChunks = new List<byte[]>()
                    };

                    int contentLen = reader.ReadInt32();
                    if (contentLen < 0)
                        throw new CryptographicException(VaultText.T("core.serializer.fileLengthInvalid"));

                    int readContentBytes = 0;
                    file.Content = ReadRequiredBytes(
                        reader,
                        contentLen,
                        VaultText.T("core.serializer.fileContentIncomplete"),
                        chunkRead =>
                        {
                            readContentBytes += chunkRead;
                            if (contentLen > 0)
                            {
                                double itemProgress = itemStartPercent +
                                    (readContentBytes * 1.0 / contentLen) * (itemEndPercent - itemStartPercent);
                                ReportProgress(progress, itemProgress);
                            }
                        });

                    files.Add(file);
                    ReportProgress(progress, itemEndPercent);
                }

                EnsureNoTrailingData(input);
                metadata.Version = FORMAT_VERSION_STANDARD;
                ReportProgress(progress, 100);
                return new VaultContent { Metadata = metadata, Files = files };
            }

            // Import legacy schema (v1): entries username/password/url + eventuale blob.
            for (int i = 0; i < count; i++)
            {
                double itemStartPercent = count == 0 ? 100 : i * 100.0 / count;
                double itemEndPercent = count == 0 ? 100 : (i + 1) * 100.0 / count;
                ReportProgress(progress, itemStartPercent);

                Guid id = new Guid(ReadRequiredBytes(reader, 16, VaultText.T("core.serializer.legacyIdIncomplete")));
                string title = reader.ReadString();
                _ = reader.ReadString(); // username
                _ = reader.ReadString(); // password
                _ = reader.ReadString(); // url
                string fileName = reader.ReadString();
                int contentLen = reader.ReadInt32();
                if (contentLen < 0)
                    throw new CryptographicException(VaultText.T("core.serializer.fileLengthInvalid"));

                int readContentBytes = 0;
                byte[] content = ReadRequiredBytes(
                    reader,
                    contentLen,
                    VaultText.T("core.serializer.legacyContentIncomplete"),
                    chunkRead =>
                    {
                        readContentBytes += chunkRead;
                        if (contentLen > 0)
                        {
                            double itemProgress = itemStartPercent +
                                (readContentBytes * 1.0 / contentLen) * (itemEndPercent - itemStartPercent);
                            ReportProgress(progress, itemProgress);
                        }
                    });

                // Legacy entries without file content are ignored during migration.
                if (content.Length == 0)
                {
                    ReportProgress(progress, itemEndPercent);
                    continue;
                }

                files.Add(new VaultFileItem
                {
                    Id = id,
                    FileName = string.IsNullOrWhiteSpace(fileName)
                        ? (string.IsNullOrWhiteSpace(title) ? $"legacy_file_{i + 1}" : title)
                        : fileName,
                    ParentPath = string.Empty,
                    IsFolder = false,
                    AddedTicks = metadata.CreatedTicks,
                    Content = content,
                    ContentChunks = new List<byte[]>()
                });

                ReportProgress(progress, itemEndPercent);
            }

            EnsureNoTrailingData(input);
            metadata.Version = FORMAT_VERSION_STANDARD;
            ReportProgress(progress, 100);
            return new VaultContent { Metadata = metadata, Files = files };
        }

        private static void WriteStandardPayload(
            BinaryWriter writer,
            Stream output,
            VaultFileItem file,
            Action<int>? onChunkWritten = null)
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

            int len = (int)contentLength;
            writer.Write(len);

            if (len == 0)
                return;

            int written = 0;
            foreach (var chunk in file.GetContentChunks())
            {
                if (chunk.Length == 0)
                    continue;

                WriteBufferInChunks(output, chunk, bytes =>
                {
                    written += bytes;
                    onChunkWritten?.Invoke(bytes);
                });
            }

            if (written != len)
                throw new InvalidOperationException(VaultText.T("core.serializer.contentInconsistent"));
        }

        private static void WriteUltraPayload(
            BinaryWriter writer,
            Stream output,
            VaultFileItem file,
            Action<int>? onChunkWritten = null)
        {
            if (file.IsFolder)
            {
                writer.Write(0L);
                writer.Write(0);
                return;
            }

            long contentLength = file.ContentLength;
            writer.Write(contentLength);

            var chunks = new List<byte[]>();
            foreach (var chunk in file.GetContentChunks())
            {
                if (chunk.Length > 0)
                    chunks.Add(chunk);
            }

            writer.Write(chunks.Count);
            long written = 0;
            foreach (var chunk in chunks)
            {
                writer.Write(chunk.Length);
                WriteBufferInChunks(output, chunk, bytes =>
                {
                    written += bytes;
                    onChunkWritten?.Invoke(bytes);
                });
            }

            if (written != contentLength)
                throw new InvalidOperationException(VaultText.T("core.serializer.contentInconsistent"));
        }

        private static int GetChunkCountForWrite(VaultFileItem file)
        {
            if (file.IsFolder)
                return 0;

            int count = 0;
            foreach (var chunk in file.GetContentChunks())
            {
                if (chunk.Length > 0)
                    count++;
            }

            return count;
        }

        private static long GetEncodedStringSize(string value)
        {
            int utf8ByteCount = Encoding.UTF8.GetByteCount(value);
            return checked(Get7BitEncodedIntSize(utf8ByteCount) + utf8ByteCount);
        }

        private static int Get7BitEncodedIntSize(int value)
        {
            uint v = (uint)value;
            int bytes = 1;
            while (v >= 0x80)
            {
                v >>= 7;
                bytes++;
            }

            return bytes;
        }

        private static byte[] ReadRequiredBytes(
            BinaryReader reader,
            int length,
            string errorMessage,
            Action<int>? onChunkRead = null)
        {
            if (length == 0)
                return Array.Empty<byte>();

            byte[] bytes = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = reader.Read(bytes, offset, length - offset);
                if (read <= 0)
                    throw new CryptographicException(errorMessage);

                offset += read;
                onChunkRead?.Invoke(read);
            }

            return bytes;
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

        private static string NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            string normalized = path.Replace('\\', '/').Trim().Trim('/');
            if (normalized.Length == 0)
                return string.Empty;

            string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return string.Join('/', segments);
        }

        private static void WriteBufferInChunks(Stream output, byte[] buffer, Action<int>? onChunkWritten = null)
        {
            const int chunkSize = 128 * 1024;
            int offset = 0;
            while (offset < buffer.Length)
            {
                int toWrite = Math.Min(chunkSize, buffer.Length - offset);
                output.Write(buffer, offset, toWrite);
                offset += toWrite;
                onChunkWritten?.Invoke(toWrite);
            }
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
    }
}
