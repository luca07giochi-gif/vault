using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Foundation;

namespace vault.iOS
{
    public class InstagramAnalysisService
    {
        private const string ConnectionsPath = "connections/followers_and_following/";

        private static readonly Regex FollowersFileRegex = new(
            @"^connections/followers_and_following/followers(?:_\d+)?\.html$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex HrefRegex = new(
            "<a\\b[^>]*\\bhref\\s*=\\s*[\\\"'](?<href>[^\\\"']+)[\\\"'][^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex HtmlTagRegex = new(
            "<[^>]+>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex WhitespaceRegex = new(
            @"\s+", RegexOptions.CultureInvariant);

        private static readonly HashSet<string> HeaderLabels = new(StringComparer.OrdinalIgnoreCase)
        {
            "followers", "following", "follower", "seguaci", "seguiti", "seguito", "segui"
        };

        public class InstagramUser
        {
            public string Username { get; set; } = string.Empty;

            public string InstagramUrl => $"https://www.instagram.com/{Username}/";
        }

        public class AnalysisResult
        {
            public List<InstagramUser> Followers { get; set; } = new();
            public List<InstagramUser> Following { get; set; } = new();
            public List<InstagramUser> NotFollowingBack { get; set; } = new();
        }

        public async Task<AnalysisResult> AnalyzeFromZipAsync(NSUrl zipUrl)
        {
            if (zipUrl == null)
                throw new ArgumentNullException(nameof(zipUrl));

            string? zipPath = zipUrl.Path;
            if (string.IsNullOrWhiteSpace(zipPath))
                throw new InvalidDataException("Il file selezionato non ha un percorso valido.");

            bool hasSecurityScope = zipUrl.StartAccessingSecurityScopedResource();
            try
            {
                return await Task.Run(() => AnalyzeFromZipPath(zipPath));
            }
            finally
            {
                if (hasSecurityScope)
                    zipUrl.StopAccessingSecurityScopedResource();
            }
        }

        private static AnalysisResult AnalyzeFromZipPath(string zipPath)
        {
            if (!File.Exists(zipPath))
                throw new FileNotFoundException("Il file selezionato non è disponibile.", zipPath);

            using var archive = ZipFile.OpenRead(zipPath);
            var followersFiles = archive.Entries
                .Where(entry => FollowersFileRegex.IsMatch(entry.FullName.Replace('\\', '/')))
                .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var followingFile = archive.GetEntry(ConnectionsPath + "following.html");
            if (followersFiles.Count == 0 || followingFile == null)
            {
                throw new InvalidDataException(
                    "Lo ZIP non contiene le liste followers e following dell'esportazione Instagram.");
            }

            var followers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in followersFiles)
                followers.UnionWith(ParseInstagramHtml(ReadEntry(entry)));

            var following = ParseInstagramHtml(ReadEntry(followingFile));
            var result = new AnalysisResult
            {
                Followers = ToUsers(followers),
                Following = ToUsers(following),
                NotFollowingBack = ToUsers(following.Where(username => !followers.Contains(username)))
            };

            return result;
        }

        private static string ReadEntry(ZipArchiveEntry entry)
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        private static List<string> ParseInstagramHtml(string htmlContent)
        {
            var usernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match match in HrefRegex.Matches(htmlContent))
            {
                string href = System.Net.WebUtility.HtmlDecode(match.Groups["href"].Value).Trim();
                if (TryGetUsernameFromInstagramUrl(href, out string username))
                    usernames.Add(username);
            }

            // Keep compatibility with export variants whose profile links aren't absolute URLs.
            if (usernames.Count == 0)
                usernames.UnionWith(ParseUsernamesFromText(htmlContent));

            return usernames.OrderBy(username => username, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool TryGetUsernameFromInstagramUrl(string href, out string username)
        {
            username = string.Empty;
            if (!Uri.TryCreate(href, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                (!uri.Host.Equals("instagram.com", StringComparison.OrdinalIgnoreCase) &&
                 !uri.Host.Equals("www.instagram.com", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var pathParts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            int usernamePart = pathParts.Length > 0 && pathParts[0].Equals("_u", StringComparison.OrdinalIgnoreCase)
                ? 1
                : 0;

            if (pathParts.Length <= usernamePart)
                return false;

            string candidate = Uri.UnescapeDataString(pathParts[usernamePart]);
            if (!IsValidInstagramUsername(candidate))
                return false;

            username = candidate;
            return true;
        }

        private static IEnumerable<string> ParseUsernamesFromText(string htmlContent)
        {
            string text = System.Net.WebUtility.HtmlDecode(HtmlTagRegex.Replace(htmlContent, " "));
            text = WhitespaceRegex.Replace(text, "\n");

            foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = line.Trim();
                if (HeaderLabels.Contains(candidate) ||
                    candidate.Equals("profiles you choose to see content from", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsValidInstagramUsername(candidate))
                    yield return candidate;
            }
        }

        private static List<InstagramUser> ToUsers(IEnumerable<string> usernames)
        {
            return usernames
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(username => username, StringComparer.OrdinalIgnoreCase)
                .Select(username => new InstagramUser { Username = username })
                .ToList();
        }

        private static bool IsValidInstagramUsername(string username)
        {
            if (string.IsNullOrWhiteSpace(username) || username.Equals("_u", StringComparison.OrdinalIgnoreCase))
                return false;

            return username.All(character => char.IsLetterOrDigit(character) || character == '.' || character == '_');
        }
    }
}
