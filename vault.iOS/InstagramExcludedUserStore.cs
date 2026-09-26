using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace vault.iOS
{
    internal static class InstagramExcludedUserStore
    {
        private const string AppFolderName = "LucApp";
        private const string FileName = "instagram-usernames-to-exclude.txt";

        private static string FilePath
        {
            get
            {
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                return Path.Combine(documentsPath, AppFolderName, FileName);
            }
        }

        public static List<string> Load()
        {
            string path = FilePath;
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            if (!File.Exists(path))
                File.WriteAllText(path, string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            return File.ReadAllLines(path, Encoding.UTF8)
                .Select(Normalize)
                .Where(username => IsValidUsername(username))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static void Add(string value)
        {
            string username = Normalize(value);
            if (!IsValidUsername(username))
                throw new ArgumentException("Inserisci un nome utente Instagram valido.", nameof(value));

            List<string> usernames = Load();
            if (usernames.Any(existing => string.Equals(existing, username, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Questo nome utente è già presente nell’elenco.");

            usernames.Add(username);
            Save(usernames);
        }

        public static void Remove(string username)
        {
            List<string> usernames = Load()
                .Where(existing => !string.Equals(existing, username, StringComparison.OrdinalIgnoreCase))
                .ToList();
            Save(usernames);
        }

        private static void Save(IEnumerable<string> usernames)
        {
            string path = FilePath;
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllLines(
                path,
                usernames,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        private static string Normalize(string? username) => (username ?? string.Empty).Trim().TrimStart('@').Trim();

        private static bool IsValidUsername(string username) =>
            !string.IsNullOrWhiteSpace(username) &&
            username.Length <= 30 &&
            username.All(IsValidUsernameCharacter);

        private static bool IsValidUsernameCharacter(char character) =>
            (character >= 'a' && character <= 'z') ||
            (character >= 'A' && character <= 'Z') ||
            (character >= '0' && character <= '9') ||
            character == '.' ||
            character == '_';
    }
}
