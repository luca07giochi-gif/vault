using System.Collections.Generic;

namespace vault.Core.Domain
{
    public class VaultContent
    {
        public required VaultMetadata Metadata { get; set; }   // Metadata obbligatorio
        public List<VaultFileItem> Files { get; set; } = new List<VaultFileItem>();
    }
}
