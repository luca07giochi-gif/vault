using System.Text.RegularExpressions;

namespace vault.iOS.Shared
{
    public static class VaultPendingImportLocator
    {
        public const string AppImportsRootFolderName = "Importazioni Cassaforte";
        private const int ShortIdLength = 8;

        public static string GetAppImportsRootPath(string? documentsRootPath)
        {
            string normalizedDocumentsPath = NormalizePath(documentsRootPath);
            if (string.IsNullOrWhiteSpace(normalizedDocumentsPath))
                throw new ArgumentException("Cartella documenti non valida.", nameof(documentsRootPath));

            return Path.Combine(normalizedDocumentsPath, AppImportsRootFolderName);
        }

        public static string GetVaultFolderPath(string appImportsRootPath, string? displayName, string vaultId)
        {
            string normalizedRoot = NormalizePath(appImportsRootPath);
            if (string.IsNullOrWhiteSpace(normalizedRoot))
                throw new ArgumentException("Cartella importazioni non valida.", nameof(appImportsRootPath));

            return Path.Combine(normalizedRoot, GetVaultFolderName(displayName, vaultId));
        }

        public static string GetVaultFolderName(string? displayName, string vaultId)
        {
            string safeName = SanitizeFileName(displayName);
            return $"{safeName} [{GetVaultShortId(vaultId)}]";
        }

        public static string GetVaultShortId(string vaultId)
        {
            if (string.IsNullOrWhiteSpace(vaultId))
                throw new ArgumentException("VaultId non valido.", nameof(vaultId));

            string normalized = Regex.Replace(vaultId.Trim(), "^vlt_", string.Empty, RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, "[^a-zA-Z0-9]", string.Empty);
            if (string.IsNullOrWhiteSpace(normalized))
                throw new ArgumentException("VaultId non valido.", nameof(vaultId));

            return normalized.Length <= ShortIdLength
                ? normalized.ToLowerInvariant()
                : normalized[..ShortIdLength].ToLowerInvariant();
        }

        public static string NormalizePath(string? path)
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

        public static string SanitizeFileName(string? fileName)
        {
            string trimmed = string.IsNullOrWhiteSpace(fileName) ? "Vault" : fileName.Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars())
                trimmed = trimmed.Replace(invalid, '_');

            return string.IsNullOrWhiteSpace(trimmed) ? "Vault" : trimmed;
        }
    }
}
