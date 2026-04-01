namespace vault.iOS.Shared
{
    public static class AppGroupConfig
    {
        public const string Identifier = "group.com.luca07giochi.vaultios.shared";
        
        /// <summary>
        /// Restituisce il percorso root per i dati condivisi tra app e share extension.
        /// Usa la Documents directory dell'app, accessibile da entrambi i processi.
        /// </summary>
        public static string GetSharedVaultQueuePath()
        {
            // Su iOS, Documents è il percorso che rimane stabile tra sessioni
            string docsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(docsPath, "VaultSharedQueue");
        }
    }
}
