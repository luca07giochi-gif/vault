using System;
using System.Collections.Generic;
using System.Linq;
using vault.Core;

namespace vault.Core.Domain
{
    public class VaultFileItem
    {
        public Guid Id { get; set; }
        public string FileName { get; set; } = "";
        public string ParentPath { get; set; } = "";
        public bool IsFolder { get; set; }
        public long AddedTicks { get; set; }
        public byte[] Content { get; set; } = Array.Empty<byte>();
        public List<byte[]> ContentChunks { get; set; } = new List<byte[]>();
        public long ContentLengthOverride { get; set; } = -1;

        public string FullPath =>
            string.IsNullOrWhiteSpace(ParentPath) ? FileName : $"{ParentPath}/{FileName}";

        public string ItemTypeLabel => IsFolder ? VaultText.T("core.item.folder") : VaultText.T("core.item.file");
        public string ParentPathLabel => string.IsNullOrWhiteSpace(ParentPath) ? "/" : $"/{ParentPath}";
        public string IconEmoji => GetIconEmoji(FileName, IsFolder);

        public long ContentLength =>
            IsFolder
                ? 0
                : (ContentLengthOverride >= 0
                    ? ContentLengthOverride
                    : (HasChunkedContent
                        ? ContentChunks.Sum(chunk => (long)chunk.Length)
                        : (Content?.LongLength ?? 0)));

        public bool HasChunkedContent =>
            ContentChunks != null &&
            ContentChunks.Count > 0;

        public string SizeLabel => IsFolder ? "-" : FormatBytes(ContentLength);
        public string AddedAtLabel =>
            new DateTime(AddedTicks == 0 ? DateTime.UtcNow.Ticks : AddedTicks, DateTimeKind.Utc)
                .ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm");

        public IEnumerable<byte[]> GetContentChunks()
        {
            if (IsFolder)
                yield break;

            if (HasChunkedContent)
            {
                foreach (var chunk in ContentChunks)
                {
                    if (chunk is { Length: > 0 })
                        yield return chunk;
                }

                yield break;
            }

            if (Content is { Length: > 0 })
                yield return Content;
        }

        private static string FormatBytes(long size)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = size;
            int unitIndex = 0;

            while (value >= 1024 && unitIndex < units.Length - 1)
            {
                value /= 1024;
                unitIndex++;
            }

            return $"{value:0.##} {units[unitIndex]}";
        }

        private static string GetIconEmoji(string? fileName, bool isFolder)
        {
            if (isFolder)
                return "📁";

            string extension = (System.IO.Path.GetExtension(fileName) ?? string.Empty).ToLowerInvariant();

            if (extension is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".tif" or ".tiff" or ".svg" or ".heic")
                return "🖼️";

            if (extension is ".pdf" or ".doc" or ".docx" or ".txt" or ".rtf" or ".odt")
                return "📄";

            if (extension is ".xls" or ".xlsx" or ".csv" or ".ods")
                return "📊";

            if (extension is ".ppt" or ".pptx" or ".odp")
                return "📽️";

            if (extension is ".zip" or ".rar" or ".7z" or ".tar" or ".gz")
                return "🗜️";

            if (extension is ".mp3" or ".wav" or ".flac" or ".m4a")
                return "🎵";

            if (extension is ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm")
                return "🎬";

            if (extension is ".vault")
                return "🔐";

            if (extension is ".exe" or ".msi" or ".bat" or ".cmd" or ".ps1")
                return "⚙️";

            return "📦";
        }
    }
}
