namespace vault.iOS.Shared
{
    public sealed class VaultPendingImportManifest
    {
        public int SchemaVersion { get; set; } = 1;
        public string VaultId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string LastKnownPath { get; set; } = string.Empty;
        public long UpdatedAtUtc { get; set; }
    }
}
