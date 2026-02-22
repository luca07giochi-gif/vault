using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using vault.Core.Domain;

namespace vault.UI
{
    public sealed class VaultThumbnailConverter : IValueConverter
    {
        private const long MaxPreviewBytes = 25L * 1024L * 1024L;
        private const int ThumbnailPixelWidth = 180;
        private static readonly HashSet<string> PreviewImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff",
            ".heic", ".heif", ".heics", ".heifs"
        };

        private static readonly ConcurrentDictionary<Guid, ImageSource> Cache = new();
        private static readonly ConcurrentDictionary<Guid, byte> NotPreviewableCache = new();
        private static int _previewEnabled = 1;
        private static int _cacheOnlyMode;

        public static bool IsPreviewEnabled =>
            Volatile.Read(ref _previewEnabled) != 0;

        public static void SetPreviewEnabled(bool enabled)
        {
            Volatile.Write(ref _previewEnabled, enabled ? 1 : 0);
            if (!enabled)
            {
                Cache.Clear();
                NotPreviewableCache.Clear();
            }
        }

        public static void SetCacheOnlyMode(bool enabled) =>
            Volatile.Write(ref _cacheOnlyMode, enabled ? 1 : 0);

        public static int CountPreviewable(IEnumerable<VaultFileItem> items)
        {
            if (items == null)
                return 0;

            int count = 0;
            foreach (VaultFileItem item in items)
            {
                if (IsPreviewCandidate(item))
                    count++;
            }

            return count;
        }

        public static void PreloadThumbnails(
            IEnumerable<VaultFileItem> items,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (items == null)
            {
                progress?.Report(100);
                return;
            }

            List<VaultFileItem> candidates = items
                .Where(IsPreviewCandidate)
                .ToList();

            if (candidates.Count == 0)
            {
                progress?.Report(100);
                return;
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = TryGetOrCreateThumbnail(candidates[i]);
                progress?.Report((i + 1) * 100.0 / candidates.Count);
            }
        }

        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not VaultFileItem item || item.IsFolder)
                return null;

            if (Volatile.Read(ref _previewEnabled) == 0)
                return null;

            if (Cache.TryGetValue(item.Id, out ImageSource? cached))
                return cached;

            if (Volatile.Read(ref _cacheOnlyMode) != 0)
                return null;

            return TryGetOrCreateThumbnail(item);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;

        private static ImageSource? TryGetOrCreateThumbnail(VaultFileItem item)
        {
            if (!IsPreviewCandidate(item))
            {
                NotPreviewableCache[item.Id] = 0;
                return null;
            }

            if (Cache.TryGetValue(item.Id, out ImageSource? cached))
                return cached;

            if (NotPreviewableCache.ContainsKey(item.Id))
                return null;

            string extension = Path.GetExtension(item.FileName) ?? string.Empty;
            byte[] bytes = ReadItemBytes(item);
            if (bytes.Length == 0)
            {
                NotPreviewableCache[item.Id] = 0;
                return null;
            }

            if (!LooksLikeImage(bytes, extension))
            {
                NotPreviewableCache[item.Id] = 0;
                return null;
            }

            ImageSource? created = BuildThumbnail(bytes);
            if (Cache.Count > 1500)
            {
                Cache.Clear();
                NotPreviewableCache.Clear();
            }

            if (created != null)
            {
                Cache[item.Id] = created;
                return created;
            }

            NotPreviewableCache[item.Id] = 0;
            return null;
        }

        private static bool IsPreviewCandidate(VaultFileItem item)
        {
            if (item.IsFolder || item.ContentLength <= 0 || item.ContentLength > MaxPreviewBytes)
                return false;

            string extension = Path.GetExtension(item.FileName) ?? string.Empty;
            return PreviewImageExtensions.Contains(extension);
        }

        private static byte[] ReadItemBytes(VaultFileItem item)
        {
            try
            {
                using var ms = new MemoryStream();
                foreach (byte[] chunk in item.GetContentChunks())
                {
                    if (chunk.Length == 0)
                        continue;

                    ms.Write(chunk, 0, chunk.Length);
                    if (ms.Length > MaxPreviewBytes)
                        return Array.Empty<byte>();
                }

                return ms.ToArray();
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }

        private static ImageSource? BuildThumbnail(byte[] bytes)
        {
            if (bytes.Length == 0)
                return null;

            try
            {
                using var ms = new MemoryStream(bytes, writable: false);
                ms.Position = 0;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.DecodePixelWidth = ThumbnailPixelWidth;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                try
                {
                    using var fallback = new MemoryStream(bytes, writable: false);
                    BitmapDecoder decoder = BitmapDecoder.Create(
                        fallback,
                        BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);

                    BitmapSource? firstFrame = decoder.Frames.FirstOrDefault();
                    if (firstFrame == null)
                        return null;

                    if (firstFrame.PixelWidth > ThumbnailPixelWidth)
                    {
                        double scale = ThumbnailPixelWidth / (double)firstFrame.PixelWidth;
                        var transformed = new TransformedBitmap(firstFrame, new ScaleTransform(scale, scale));
                        transformed.Freeze();
                        return transformed;
                    }

                    firstFrame.Freeze();
                    return firstFrame;
                }
                catch
                {
                    return null;
                }
            }
        }

        private static bool LooksLikeImage(byte[] bytes, string extension)
        {
            if (PreviewImageExtensions.Contains(extension))
                return true;

            if (bytes.Length >= 3 &&
                bytes[0] == 0xFF &&
                bytes[1] == 0xD8 &&
                bytes[2] == 0xFF)
            {
                return true; // JPEG
            }

            if (bytes.Length >= 8 &&
                bytes[0] == 0x89 &&
                bytes[1] == 0x50 &&
                bytes[2] == 0x4E &&
                bytes[3] == 0x47 &&
                bytes[4] == 0x0D &&
                bytes[5] == 0x0A &&
                bytes[6] == 0x1A &&
                bytes[7] == 0x0A)
            {
                return true; // PNG
            }

            if (bytes.Length >= 6 &&
                bytes[0] == 0x47 &&
                bytes[1] == 0x49 &&
                bytes[2] == 0x46 &&
                bytes[3] == 0x38 &&
                (bytes[4] == 0x37 || bytes[4] == 0x39) &&
                bytes[5] == 0x61)
            {
                return true; // GIF
            }

            if (bytes.Length >= 2 && bytes[0] == 0x42 && bytes[1] == 0x4D)
                return true; // BMP

            if (bytes.Length >= 4 &&
                ((bytes[0] == 0x49 && bytes[1] == 0x49 && bytes[2] == 0x2A && bytes[3] == 0x00) ||
                 (bytes[0] == 0x4D && bytes[1] == 0x4D && bytes[2] == 0x00 && bytes[3] == 0x2A)))
            {
                return true; // TIFF
            }

            if (bytes.Length >= 12 &&
                bytes[0] == 0x52 &&
                bytes[1] == 0x49 &&
                bytes[2] == 0x46 &&
                bytes[3] == 0x46 &&
                bytes[8] == 0x57 &&
                bytes[9] == 0x45 &&
                bytes[10] == 0x42 &&
                bytes[11] == 0x50)
            {
                return true; // WEBP
            }

            if (bytes.Length >= 12 &&
                bytes[4] == 0x66 &&
                bytes[5] == 0x74 &&
                bytes[6] == 0x79 &&
                bytes[7] == 0x70)
            {
                string brand = System.Text.Encoding.ASCII.GetString(bytes, 8, 4);
                if (brand is "heic" or "heix" or "hevc" or "hevx" or "heif" or "mif1" or "msf1")
                    return true; // HEIF/HEIC
            }

            return false;
        }
    }
}
