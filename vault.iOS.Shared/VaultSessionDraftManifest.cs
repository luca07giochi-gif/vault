using System.Collections.Generic;

namespace vault.iOS.Shared
{
    public sealed class VaultSessionDraftManifest
    {
        public int SchemaVersion { get; set; } = 1;
        public string VaultId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string LastKnownPath { get; set; } = string.Empty;
        public long SavedAtUtc { get; set; }
        public int ChangeCount { get; set; }
        public List<string> ChangeSummary { get; set; } = new();
    }
}
