using Foundation;

namespace vault.iOS.Shared
{
    public static class AppGroupConfig
    {
        public const string Identifier = "group.com.luca07giochi.vaultios.shared";
        
        /// <summary>
        /// Restituisce il percorso root per i dati condivisi tra app e share extension.
        /// Su configurazioni con App Groups disponibile, usa il container condiviso.
        /// Altrimenti usa una cartella nella temp directory accessibile da entrambi.
        /// </summary>
        public static string GetSharedVaultQueuePath()
        {
            string tempDir = NSTemporaryDirectory();
            return Path.Combine(tempDir, "VaultSharedQueue");
        }
    }
}
