namespace vault.iOS.Shared
{
    public sealed class RecentVaultRecord
    {
        public string VaultId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string LastKnownPath { get; set; } = string.Empty;
        public string? BookmarkDataBase64 { get; set; }
        public string? ImportFolderPath { get; set; }
        public string? ImportFolderBookmarkDataBase64 { get; set; }
        public string StorageFormat { get; set; } = string.Empty;
        public long LastOpenedAtUtc { get; set; }
        public bool IsPinned { get; set; }
    }
}
