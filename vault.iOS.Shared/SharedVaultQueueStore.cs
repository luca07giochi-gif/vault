using System.Text.Json;
using System.Text.Json.Serialization;

namespace vault.iOS.Shared
{
    public sealed class SharedVaultQueueStore
    {
        private const int SchemaVersion = 1;

        private readonly string _rootPath;
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        public SharedVaultQueueStore(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
                throw new ArgumentException("Percorso storage condiviso non valido.", nameof(rootPath));

            _rootPath = rootPath;
            Directory.CreateDirectory(_rootPath);
            Directory.CreateDirectory(PendingImportsRootPath);
        }

        public string RootPath => _rootPath;

        public string PendingImportsRootPath => Path.Combine(_rootPath, "pending-imports");

        public string VaultManifestPath => Path.Combine(_rootPath, "vault.json");

        public string DraftRootPath => Path.Combine(_rootPath, "modifiche-non-salvate");

        public string DraftManifestPath => Path.Combine(DraftRootPath, "draft.json");

        public string DraftVaultFilePath => Path.Combine(DraftRootPath, "bozza.vault");

        public IReadOnlyList<RecentVaultRecord> LoadRecentVaults()
        {
            RecentVaultRegistryDocument document = ReadJson(RecentVaultsPath, CreateEmptyRecentVaultRegistry);
            return document.Vaults
                .OrderByDescending(vault => vault.IsPinned)
                .ThenByDescending(vault => vault.LastOpenedAtUtc)
                .ToArray();
        }

        public VaultPendingImportManifest? LoadVaultManifest()
        {
            return ReadJson<VaultPendingImportManifest?>(VaultManifestPath, () => null);
        }

        public VaultSessionDraftManifest? LoadDraftManifest()
        {
            return ReadJson<VaultSessionDraftManifest?>(DraftManifestPath, () => null);
        }

        public void SaveVaultManifest(VaultPendingImportManifest manifest)
        {
            if (manifest == null)
                throw new ArgumentNullException(nameof(manifest));

            NormalizeManifest(manifest);
            WriteJsonAtomic(VaultManifestPath, manifest);
        }

        public void SaveDraftManifest(VaultSessionDraftManifest manifest)
        {
            if (manifest == null)
                throw new ArgumentNullException(nameof(manifest));

            NormalizeDraftManifest(manifest);
            Directory.CreateDirectory(DraftRootPath);
            WriteJsonAtomic(DraftManifestPath, manifest);
        }

        public void DeleteDraft()
        {
            if (!Directory.Exists(DraftRootPath))
                return;

            Directory.Delete(DraftRootPath, recursive: true);
        }

        public static VaultPendingImportManifest? TryReadVaultManifest(string? rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
                return null;

            string manifestPath = Path.Combine(rootPath, "vault.json");
            if (!File.Exists(manifestPath))
                return null;

            try
            {
                using FileStream input = new(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                return JsonSerializer.Deserialize<VaultPendingImportManifest>(input, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch
            {
                return null;
            }
        }

        public RecentVaultRecord? FindRecentVaultById(string? vaultId)
        {
            if (string.IsNullOrWhiteSpace(vaultId))
                return null;

            return LoadRecentVaults().FirstOrDefault(vault =>
                string.Equals(vault.VaultId, vaultId, StringComparison.OrdinalIgnoreCase));
        }

        public RecentVaultRecord? FindRecentVaultByPath(string? lastKnownPath)
        {
            string normalizedPath = NormalizePath(lastKnownPath);
            if (string.IsNullOrWhiteSpace(normalizedPath))
                return null;

            return LoadRecentVaults().FirstOrDefault(vault =>
                string.Equals(NormalizePath(vault.LastKnownPath), normalizedPath, StringComparison.OrdinalIgnoreCase));
        }

        public RecentVaultRecord UpsertRecentVault(RecentVaultRecord record, int limit = 12)
        {
            if (record == null)
                throw new ArgumentNullException(nameof(record));

            record.VaultId = string.IsNullOrWhiteSpace(record.VaultId)
                ? Guid.NewGuid().ToString("N")
                : record.VaultId.Trim();
            record.DisplayName = string.IsNullOrWhiteSpace(record.DisplayName)
                ? "Vault"
                : record.DisplayName.Trim();
            record.LastKnownPath = NormalizePath(record.LastKnownPath);
            record.LastOpenedAtUtc = record.LastOpenedAtUtc <= 0
                ? DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                : record.LastOpenedAtUtc;

            RecentVaultRegistryDocument document = ReadJson(RecentVaultsPath, CreateEmptyRecentVaultRegistry);
            RecentVaultRecord? existing = document.Vaults.FirstOrDefault(vault =>
                string.Equals(vault.VaultId, record.VaultId, StringComparison.OrdinalIgnoreCase));

            if (existing == null && !string.IsNullOrWhiteSpace(record.LastKnownPath))
            {
                existing = document.Vaults.FirstOrDefault(vault =>
                    string.Equals(NormalizePath(vault.LastKnownPath), record.LastKnownPath, StringComparison.OrdinalIgnoreCase));
            }

            if (existing == null)
            {
                document.Vaults.Add(record);
            }
            else
            {
                existing.VaultId = record.VaultId;
                existing.DisplayName = record.DisplayName;
                existing.LastKnownPath = record.LastKnownPath;
                existing.BookmarkDataBase64 = string.IsNullOrWhiteSpace(record.BookmarkDataBase64)
                    ? existing.BookmarkDataBase64
                    : record.BookmarkDataBase64;
                existing.ImportFolderPath = string.IsNullOrWhiteSpace(record.ImportFolderPath)
                    ? existing.ImportFolderPath
                    : record.ImportFolderPath;
                existing.ImportFolderBookmarkDataBase64 = string.IsNullOrWhiteSpace(record.ImportFolderBookmarkDataBase64)
                    ? existing.ImportFolderBookmarkDataBase64
                    : record.ImportFolderBookmarkDataBase64;
                existing.StorageFormat = string.IsNullOrWhiteSpace(record.StorageFormat)
                    ? existing.StorageFormat
                    : record.StorageFormat;
                existing.LastOpenedAtUtc = record.LastOpenedAtUtc;
                existing.IsPinned = record.IsPinned;
                record = existing;
            }

            document.Vaults = document.Vaults
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

            WriteJsonAtomic(RecentVaultsPath, document);
            return record;
        }

        public PendingImportJob CreatePendingJob(string vaultId, string vaultDisplayName)
        {
            if (string.IsNullOrWhiteSpace(vaultId))
                throw new ArgumentException("VaultId non valido.", nameof(vaultId));

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return new PendingImportJob
            {
                JobId = Guid.NewGuid().ToString("N"),
                VaultId = vaultId.Trim(),
                VaultDisplayName = string.IsNullOrWhiteSpace(vaultDisplayName) ? "Vault" : vaultDisplayName.Trim(),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Status = PendingImportStatus.Pending
            };
        }

        public void SavePendingJob(PendingImportJob job)
        {
            if (job == null)
                throw new ArgumentNullException(nameof(job));

            NormalizeJob(job);

            string jobDirectory = GetJobDirectory(job.JobId);
            Directory.CreateDirectory(jobDirectory);
            Directory.CreateDirectory(GetJobFilesDirectory(job.JobId));

            WriteJsonAtomic(GetJobManifestPath(job.JobId), job);

            PendingImportJobIndexDocument index = ReadJson(JobIndexPath, CreateEmptyJobIndex);
            PendingImportJobIndexItem? summary = index.Jobs.FirstOrDefault(existing =>
                string.Equals(existing.JobId, job.JobId, StringComparison.OrdinalIgnoreCase));

            if (summary == null)
            {
                summary = new PendingImportJobIndexItem();
                index.Jobs.Add(summary);
            }

            summary.JobId = job.JobId;
            summary.VaultId = job.VaultId;
            summary.VaultDisplayName = job.VaultDisplayName;
            summary.Status = job.Status;
            summary.CreatedAtUtc = job.CreatedAtUtc;
            summary.UpdatedAtUtc = job.UpdatedAtUtc;
            summary.FileCount = job.FileCount;
            summary.TotalBytes = job.TotalBytes;

            index.Jobs = index.Jobs
                .OrderByDescending(existing => existing.CreatedAtUtc)
                .ToList();

            WriteJsonAtomic(JobIndexPath, index);
        }

        public PendingImportJob? LoadPendingJob(string? jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
                return null;

            string manifestPath = GetJobManifestPath(jobId);
            return ReadJson<PendingImportJob?>(manifestPath, () => null);
        }

        public IReadOnlyList<PendingImportJob> LoadPendingJobsForVault(string vaultId)
        {
            if (string.IsNullOrWhiteSpace(vaultId))
                return Array.Empty<PendingImportJob>();

            PendingImportJobIndexDocument index = ReadJson(JobIndexPath, CreateEmptyJobIndex);
            return index.Jobs
                .Where(job => string.Equals(job.VaultId, vaultId, StringComparison.OrdinalIgnoreCase) &&
                              job.Status == PendingImportStatus.Pending)
                .OrderBy(job => job.CreatedAtUtc)
                .Select(job => LoadPendingJob(job.JobId))
                .Where(job => job != null)
                .Cast<PendingImportJob>()
                .ToArray();
        }

        public IReadOnlyList<PendingImportJob> LoadPendingJobs(IEnumerable<string> jobIds)
        {
            if (jobIds == null)
                return Array.Empty<PendingImportJob>();

            return jobIds
                .Where(jobId => !string.IsNullOrWhiteSpace(jobId))
                .Select(LoadPendingJob)
                .Where(job => job != null)
                .Cast<PendingImportJob>()
                .ToArray();
        }

        public PendingImportAggregate? GetPendingAggregateForVault(string? vaultId)
        {
            if (string.IsNullOrWhiteSpace(vaultId))
                return null;

            IReadOnlyList<PendingImportJob> jobs = LoadPendingJobsForVault(vaultId);
            if (jobs.Count == 0)
                return null;

            return new PendingImportAggregate
            {
                VaultId = vaultId,
                VaultDisplayName = jobs[0].VaultDisplayName,
                JobCount = jobs.Count,
                FileCount = jobs.Sum(job => job.FileCount),
                TotalBytes = jobs.Sum(job => job.TotalBytes),
                JobIds = jobs.Select(job => job.JobId).ToArray()
            };
        }

        public void UpdatePendingJobStatus(string jobId, PendingImportStatus status, string? errorMessage = null)
        {
            PendingImportJob? job = LoadPendingJob(jobId);
            if (job == null)
                return;

            job.Status = status;
            job.ErrorMessage = string.IsNullOrWhiteSpace(errorMessage) ? null : errorMessage.Trim();
            job.UpdatedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            SavePendingJob(job);
        }

        public void DeleteJobs(IEnumerable<string> jobIds)
        {
            if (jobIds == null)
                return;

            string[] ids = jobIds
                .Where(jobId => !string.IsNullOrWhiteSpace(jobId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (ids.Length == 0)
                return;

            PendingImportJobIndexDocument index = ReadJson(JobIndexPath, CreateEmptyJobIndex);
            index.Jobs.RemoveAll(job => ids.Contains(job.JobId, StringComparer.OrdinalIgnoreCase));
            WriteJsonAtomic(JobIndexPath, index);

            foreach (string jobId in ids)
            {
                string directory = GetJobDirectory(jobId);
                if (!Directory.Exists(directory))
                    continue;

                Directory.Delete(directory, recursive: true);
            }
        }

        public string GetJobDirectory(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
                throw new ArgumentException("JobId non valido.", nameof(jobId));

            return Path.Combine(PendingImportsRootPath, jobId.Trim());
        }

        public string GetJobFilesDirectory(string jobId)
        {
            return Path.Combine(GetJobDirectory(jobId), "files");
        }

        public string ResolveStagedFilePath(PendingImportJob job, PendingImportItem item)
        {
            if (job == null)
                throw new ArgumentNullException(nameof(job));
            if (item == null)
                throw new ArgumentNullException(nameof(item));
            if (string.IsNullOrWhiteSpace(item.StagedRelativePath))
                throw new InvalidOperationException("Percorso file staged non valido.");

            string jobDirectory = GetJobDirectory(job.JobId);
            string relativePath = item.StagedRelativePath
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);

            string resolved = Path.GetFullPath(Path.Combine(jobDirectory, relativePath));
            string fullJobDirectory = Path.GetFullPath(jobDirectory);
            if (!resolved.StartsWith(fullJobDirectory, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Percorso staged non valido.");

            return resolved;
        }

        public string BuildUniqueStagedFilePath(string jobId, string originalFileName)
        {
            string safeName = SanitizeFileName(originalFileName);
            string baseName = Path.GetFileNameWithoutExtension(safeName);
            string extension = Path.GetExtension(safeName);
            string directory = GetJobFilesDirectory(jobId);
            Directory.CreateDirectory(directory);

            string candidate = Path.Combine(directory, safeName);
            int counter = 1;
            while (File.Exists(candidate))
            {
                candidate = Path.Combine(directory, $"{baseName}-{counter}{extension}");
                counter++;
            }

            return candidate;
        }

        public string GetRelativePathForJob(string jobId, string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(absolutePath))
                throw new ArgumentException("Percorso assoluto non valido.", nameof(absolutePath));

            string jobDirectory = Path.GetFullPath(GetJobDirectory(jobId));
            string fullPath = Path.GetFullPath(absolutePath);
            if (!fullPath.StartsWith(jobDirectory, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Il file staged non appartiene al job richiesto.");

            string relativePath = Path.GetRelativePath(jobDirectory, fullPath);
            return relativePath.Replace(Path.DirectorySeparatorChar, '/');
        }

        private string RecentVaultsPath => Path.Combine(_rootPath, "recent-vaults.json");

        private string JobIndexPath => Path.Combine(PendingImportsRootPath, "jobs.json");

        private string GetJobManifestPath(string jobId)
        {
            return Path.Combine(GetJobDirectory(jobId), "manifest.json");
        }

        private void NormalizeJob(PendingImportJob job)
        {
            job.SchemaVersion = SchemaVersion;
            job.JobId = string.IsNullOrWhiteSpace(job.JobId) ? Guid.NewGuid().ToString("N") : job.JobId.Trim();
            job.VaultId = string.IsNullOrWhiteSpace(job.VaultId)
                ? throw new InvalidOperationException("VaultId mancante.")
                : job.VaultId.Trim();
            job.VaultDisplayName = string.IsNullOrWhiteSpace(job.VaultDisplayName) ? "Vault" : job.VaultDisplayName.Trim();
            job.Items ??= new List<PendingImportItem>();

            foreach (PendingImportItem item in job.Items)
            {
                item.ItemId = string.IsNullOrWhiteSpace(item.ItemId) ? Guid.NewGuid().ToString("N") : item.ItemId.Trim();
                item.OriginalFileName = string.IsNullOrWhiteSpace(item.OriginalFileName)
                    ? "file"
                    : item.OriginalFileName.Trim();
                item.StagedRelativePath = item.StagedRelativePath?.Trim() ?? string.Empty;
                item.ContentType = item.ContentType?.Trim() ?? string.Empty;
            }

            job.FileCount = job.Items.Count;
            job.TotalBytes = job.Items.Sum(item => Math.Max(0L, item.FileSize));
            if (job.CreatedAtUtc <= 0)
                job.CreatedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            job.UpdatedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        private static void NormalizeManifest(VaultPendingImportManifest manifest)
        {
            manifest.SchemaVersion = SchemaVersion;
            manifest.VaultId = string.IsNullOrWhiteSpace(manifest.VaultId)
                ? throw new InvalidOperationException("VaultId mancante nel manifest.")
                : manifest.VaultId.Trim();
            manifest.DisplayName = string.IsNullOrWhiteSpace(manifest.DisplayName)
                ? "Vault"
                : manifest.DisplayName.Trim();
            manifest.LastKnownPath = NormalizePath(manifest.LastKnownPath);
            manifest.UpdatedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        private static void NormalizeDraftManifest(VaultSessionDraftManifest manifest)
        {
            manifest.SchemaVersion = SchemaVersion;
            manifest.VaultId = string.IsNullOrWhiteSpace(manifest.VaultId)
                ? throw new InvalidOperationException("VaultId mancante nella bozza.")
                : manifest.VaultId.Trim();
            manifest.DisplayName = string.IsNullOrWhiteSpace(manifest.DisplayName)
                ? "Vault"
                : manifest.DisplayName.Trim();
            manifest.LastKnownPath = NormalizePath(manifest.LastKnownPath);
            manifest.ChangeSummary ??= new List<string>();
            manifest.ChangeSummary = manifest.ChangeSummary
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            manifest.ChangeCount = manifest.ChangeSummary.Count;
            manifest.SavedAtUtc = manifest.SavedAtUtc <= 0
                ? DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                : manifest.SavedAtUtc;
        }

        private T ReadJson<T>(string path, Func<T> defaultFactory)
        {
            if (!File.Exists(path))
                return defaultFactory();

            try
            {
                using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                T? data = JsonSerializer.Deserialize<T>(input, _jsonOptions);
                return data ?? defaultFactory();
            }
            catch
            {
                return defaultFactory();
            }
        }

        private void WriteJsonAtomic<T>(string path, T value)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string tempPath = Path.Combine(directory ?? _rootPath, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            using (FileStream output = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, value, _jsonOptions);
                output.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
        }

        private static RecentVaultRegistryDocument CreateEmptyRecentVaultRegistry()
        {
            return new RecentVaultRegistryDocument
            {
                SchemaVersion = SchemaVersion
            };
        }

        private static PendingImportJobIndexDocument CreateEmptyJobIndex()
        {
            return new PendingImportJobIndexDocument
            {
                SchemaVersion = SchemaVersion
            };
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

        private static string SanitizeFileName(string? fileName)
        {
            string trimmed = string.IsNullOrWhiteSpace(fileName) ? "file" : fileName.Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars())
                trimmed = trimmed.Replace(invalid, '_');

            return string.IsNullOrWhiteSpace(trimmed) ? "file" : trimmed;
        }

        private sealed class RecentVaultRegistryDocument
        {
            public int SchemaVersion { get; set; }
            public List<RecentVaultRecord> Vaults { get; set; } = new();
        }

        private sealed class PendingImportJobIndexDocument
        {
            public int SchemaVersion { get; set; }
            public List<PendingImportJobIndexItem> Jobs { get; set; } = new();
        }

        private sealed class PendingImportJobIndexItem
        {
            public string JobId { get; set; } = string.Empty;
            public string VaultId { get; set; } = string.Empty;
            public string VaultDisplayName { get; set; } = string.Empty;
            public PendingImportStatus Status { get; set; }
            public long CreatedAtUtc { get; set; }
            public long UpdatedAtUtc { get; set; }
            public int FileCount { get; set; }
            public long TotalBytes { get; set; }
        }
    }
}
