using System.Text;
using System.Text.Json;

using Foundation;
using UIKit;

namespace vault.iOS.Shared
{
    public static class ShareVaultRegistryBridge
    {
        private const int SchemaVersion = 1;
        private const string AppDefaultsKey = "vault.share.registry.v1";
        private const string PasteboardName = "com.luca07giochi.vaultios.share.registry";
        private const string PasteboardType = "com.luca07giochi.vaultios.share.registry+json";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public static IReadOnlyList<RecentVaultRecord> LoadAppManagedVaults()
        {
            string? json = NSUserDefaults.StandardUserDefaults.StringForKey(AppDefaultsKey);
            return DeserializeVaults(json);
        }

        public static IReadOnlyList<RecentVaultRecord> LoadPublishedVaults()
        {
            try
            {
                UIPasteboard? pasteboard = TryGetNamedPasteboard(create: false);
                if (pasteboard == null)
                    return Array.Empty<RecentVaultRecord>();

                if (!string.IsNullOrWhiteSpace(pasteboard.String))
                    return DeserializeVaults(pasteboard.String);

                NSObject? value = pasteboard.GetValue(PasteboardType);
                if (value is NSString text)
                    return DeserializeVaults(text.ToString());

                if (value is NSData data)
                    return DeserializeVaults(NSString.FromData(data, NSStringEncoding.UTF8)?.ToString());
            }
            catch
            {
                // Ignore and fall back to empty.
            }

            return Array.Empty<RecentVaultRecord>();
        }

        public static RecentVaultRecord UpsertAppManagedVault(RecentVaultRecord record, int limit = 12)
        {
            if (record == null)
                throw new ArgumentNullException(nameof(record));

            List<RecentVaultRecord> vaults = LoadAppManagedVaults().ToList();
            NormalizeRecord(record);

            RecentVaultRecord? existing = vaults.FirstOrDefault(vault =>
                string.Equals(vault.VaultId, record.VaultId, StringComparison.OrdinalIgnoreCase));

            if (existing == null && !string.IsNullOrWhiteSpace(record.LastKnownPath))
            {
                existing = vaults.FirstOrDefault(vault =>
                    string.Equals(NormalizePath(vault.LastKnownPath), record.LastKnownPath, StringComparison.OrdinalIgnoreCase));
            }

            if (existing == null)
            {
                vaults.Add(record);
            }
            else
            {
                existing.DisplayName = record.DisplayName;
                existing.LastKnownPath = record.LastKnownPath;
                existing.BookmarkDataBase64 = string.IsNullOrWhiteSpace(record.BookmarkDataBase64)
                    ? existing.BookmarkDataBase64
                    : record.BookmarkDataBase64;
                existing.StorageFormat = string.IsNullOrWhiteSpace(record.StorageFormat)
                    ? existing.StorageFormat
                    : record.StorageFormat;
                existing.LastOpenedAtUtc = record.LastOpenedAtUtc;
                existing.IsPinned = record.IsPinned;
                record = existing;
            }

            vaults = vaults
                .GroupBy(vault => string.IsNullOrWhiteSpace(vault.LastKnownPath) ? vault.VaultId : NormalizePath(vault.LastKnownPath),
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(vault => vault.IsPinned)
                    .ThenByDescending(vault => vault.LastOpenedAtUtc)
                    .First())
                .OrderByDescending(vault => vault.IsPinned)
                .ThenByDescending(vault => vault.LastOpenedAtUtc)
                .Take(Math.Max(1, limit))
                .ToList();

            SaveAppManagedVaults(vaults);
            return record;
        }

        public static void RemoveAppManagedVault(string vaultId)
        {
            if (string.IsNullOrWhiteSpace(vaultId))
                return;

            List<RecentVaultRecord> filtered = LoadAppManagedVaults()
                .Where(vault => !string.Equals(vault.VaultId, vaultId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            SaveAppManagedVaults(filtered);
        }

        public static void RepublishAppManagedVaults()
        {
            SavePublishedVaults(LoadAppManagedVaults());
        }

        public static void SaveAppManagedVaults(IEnumerable<RecentVaultRecord> vaults)
        {
            List<RecentVaultRecord> normalized = vaults?
                .Where(vault => vault != null)
                .Select(CloneAndNormalizeRecord)
                .OrderByDescending(vault => vault.IsPinned)
                .ThenByDescending(vault => vault.LastOpenedAtUtc)
                .ToList()
                ?? new List<RecentVaultRecord>();

            string json = SerializeVaults(normalized);
            NSUserDefaults defaults = NSUserDefaults.StandardUserDefaults;
            defaults.SetString(json, AppDefaultsKey);
            defaults.Synchronize();

            SavePublishedVaults(normalized);
        }

        private static void SavePublishedVaults(IEnumerable<RecentVaultRecord> vaults)
        {
            try
            {
                string json = SerializeVaults(vaults);
                UIPasteboard? pasteboard = TryGetNamedPasteboard(create: true);
                if (pasteboard == null)
                    return;

                pasteboard.String = json;

                NSData data = NSData.FromArray(Encoding.UTF8.GetBytes(json));
                pasteboard.SetData(data, PasteboardType);
            }
            catch
            {
                // Best effort publish.
            }
        }

        private static UIPasteboard? TryGetNamedPasteboard(bool create)
        {
            try
            {
                UIPasteboard? pasteboard = UIPasteboard.FromName(PasteboardName, create);
                if (pasteboard == null)
                    return null;

                TryMarkPersistent(pasteboard);
                return pasteboard;
            }
            catch
            {
                return null;
            }
        }

        private static void TryMarkPersistent(UIPasteboard pasteboard)
        {
            try
            {
                pasteboard.SetValueForKey(NSNumber.FromBoolean(true), new NSString("persistent"));
            }
            catch
            {
                // Some iOS versions may ignore this; publication still remains best effort.
            }
        }

        private static string SerializeVaults(IEnumerable<RecentVaultRecord> vaults)
        {
            RegistryDocument document = new()
            {
                SchemaVersion = SchemaVersion,
                Vaults = vaults?.Select(CloneAndNormalizeRecord).ToList() ?? new List<RecentVaultRecord>()
            };

            return JsonSerializer.Serialize(document, JsonOptions);
        }

        private static IReadOnlyList<RecentVaultRecord> DeserializeVaults(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return Array.Empty<RecentVaultRecord>();

            try
            {
                RegistryDocument? document = JsonSerializer.Deserialize<RegistryDocument>(json, JsonOptions);
                if (document?.Vaults == null)
                    return Array.Empty<RecentVaultRecord>();

                return document.Vaults
                    .Where(vault => vault != null)
                    .Select(CloneAndNormalizeRecord)
                    .OrderByDescending(vault => vault.IsPinned)
                    .ThenByDescending(vault => vault.LastOpenedAtUtc)
                    .ToArray();
            }
            catch
            {
                return Array.Empty<RecentVaultRecord>();
            }
        }

        private static RecentVaultRecord CloneAndNormalizeRecord(RecentVaultRecord record)
        {
            RecentVaultRecord clone = new()
            {
                VaultId = record.VaultId,
                DisplayName = record.DisplayName,
                LastKnownPath = record.LastKnownPath,
                BookmarkDataBase64 = record.BookmarkDataBase64,
                StorageFormat = record.StorageFormat,
                LastOpenedAtUtc = record.LastOpenedAtUtc,
                IsPinned = record.IsPinned
            };

            NormalizeRecord(clone);
            return clone;
        }

        private static void NormalizeRecord(RecentVaultRecord record)
        {
            record.VaultId = string.IsNullOrWhiteSpace(record.VaultId)
                ? Guid.NewGuid().ToString("N")
                : record.VaultId.Trim();
            record.DisplayName = string.IsNullOrWhiteSpace(record.DisplayName)
                ? "Vault"
                : record.DisplayName.Trim();
            record.LastKnownPath = NormalizePath(record.LastKnownPath);
            record.BookmarkDataBase64 = string.IsNullOrWhiteSpace(record.BookmarkDataBase64)
                ? null
                : record.BookmarkDataBase64.Trim();
            record.StorageFormat = string.IsNullOrWhiteSpace(record.StorageFormat)
                ? string.Empty
                : record.StorageFormat.Trim();
            record.LastOpenedAtUtc = record.LastOpenedAtUtc <= 0
                ? DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                : record.LastOpenedAtUtc;
        }

        private static string NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            try
            {
                return Path.GetFullPath(path)
                    .Trim()
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path.Trim();
            }
        }

        private sealed class RegistryDocument
        {
            public int SchemaVersion { get; set; }
            public List<RecentVaultRecord> Vaults { get; set; } = new();
        }
    }
}
