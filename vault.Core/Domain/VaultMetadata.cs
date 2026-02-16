using System;

namespace vault.Core.Domain
{
    // Metadata logico del Vault (NON crittografico)
    public class VaultMetadata
    {
        public int Version { get; set; }        // Versione del Vault
        public long CreatedTicks { get; set; }  // Timestamp di creazione
    }
}
