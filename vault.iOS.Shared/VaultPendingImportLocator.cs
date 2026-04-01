using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace vault.iOS.Shared
{
    public static class VaultPendingImportLocator
    {
        private const string QueueRootFolderName = "Importazioni Cassaforte";

        public static string GetVaultId(string? vaultPath)
        {
            string normalized = NormalizePath(vaultPath);
            if (string.IsNullOrWhiteSpace(normalized))
                throw new ArgumentException("Percorso vault non valido.", nameof(vaultPath));

            byte[] bytes = Encoding.UTF8.GetBytes(normalized);
            byte[] hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        public static string GetQueueRootPath(string? vaultPath)
        {
            string normalized = NormalizePath(vaultPath);
            if (string.IsNullOrWhiteSpace(normalized))
                throw new ArgumentException("Percorso vault non valido.", nameof(vaultPath));

            string? directory = Path.GetDirectoryName(normalized);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("Cartella del vault non disponibile.");

            string baseName = Path.GetFileNameWithoutExtension(normalized);
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = Path.GetFileName(normalized);

            string safeBaseName = SanitizeFileName(baseName);
            string shortHash = GetVaultId(normalized)[..12];
            return Path.Combine(directory, QueueRootFolderName, $"{safeBaseName}-{shortHash}");
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
            string trimmed = string.IsNullOrWhiteSpace(fileName) ? "Vault" : fileName.Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars())
                trimmed = trimmed.Replace(invalid, '_');

            return string.IsNullOrWhiteSpace(trimmed) ? "Vault" : trimmed;
        }
    }
}
