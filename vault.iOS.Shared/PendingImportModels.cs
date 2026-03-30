using System.Collections.Generic;

namespace vault.iOS.Shared
{
    public enum PendingImportStatus
    {
        Pending,
        Importing,
        Completed,
        Discarded,
        Failed
    }

    public sealed class PendingImportItem
    {
        public string ItemId { get; set; } = string.Empty;
        public string OriginalFileName { get; set; } = string.Empty;
        public string StagedRelativePath { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public string? SourceHint { get; set; }
    }

    public sealed class PendingImportJob
    {
        public int SchemaVersion { get; set; } = 1;
        public string JobId { get; set; } = string.Empty;
        public string VaultId { get; set; } = string.Empty;
        public string VaultDisplayName { get; set; } = string.Empty;
        public long CreatedAtUtc { get; set; }
        public long UpdatedAtUtc { get; set; }
        public PendingImportStatus Status { get; set; } = PendingImportStatus.Pending;
        public int FileCount { get; set; }
        public long TotalBytes { get; set; }
        public string? ErrorMessage { get; set; }
        public List<PendingImportItem> Items { get; set; } = new();
    }

    public sealed class PendingImportAggregate
    {
        public string VaultId { get; set; } = string.Empty;
        public string VaultDisplayName { get; set; } = string.Empty;
        public int JobCount { get; set; }
        public int FileCount { get; set; }
        public long TotalBytes { get; set; }
        public string[] JobIds { get; set; } = [];
    }
}
