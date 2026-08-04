using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Foundation;

namespace vault.iOS
{
    public class InstagramAnalysisService
    {
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
            return await Task.Run(() =>
            {
                var result = new AnalysisResult();

                try
                {
                    string? zipPath = zipUrl.Path;
                    if (string.IsNullOrEmpty(zipPath) || !File.Exists(zipPath))
                        return result;

                    using (var archive = ZipFile.OpenRead(zipPath))
                    {
                        string followersHtml = ExtractHtmlFromZip(archive, "connections/followers_and_following/followers_1.html");
                        string followingHtml = ExtractHtmlFromZip(archive, "connections/followers_and_following/following.html");

                        if (!string.IsNullOrEmpty(followersHtml))
                            result.Followers = ParseInstagramHtml(followersHtml).OrderBy(u => u.Username).ToList();

                        if (!string.IsNullOrEmpty(followingHtml))
                            result.Following = ParseInstagramHtml(followingHtml).OrderBy(u => u.Username).ToList();

                        // Find users who we follow but don't follow us back
                        var followersSet = new HashSet<string>(result.Followers.Select(u => u.Username.ToLower()));
                        result.NotFollowingBack = result.Following
                            .Where(u => !followersSet.Contains(u.Username.ToLower()))
                            .OrderBy(u => u.Username)
                            .ToList();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error analyzing Instagram data: {ex.Message}");
                }

                return result;
            });
        }

        private string ExtractHtmlFromZip(ZipArchive archive, string entryPath)
        {
            var entry = archive.GetEntry(entryPath);
            if (entry == null)
                return string.Empty;

            using (var reader = new StreamReader(entry.Open()))
            {
                return reader.ReadToEnd();
            }
        }

        private List<InstagramUser> ParseInstagramHtml(string htmlContent)
        {
            var users = new List<InstagramUser>();

            if (string.IsNullOrWhiteSpace(htmlContent))
                return users;

            try
            {
                var usernameSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var username in ExtractUsernamesFromAnchors(htmlContent))
                {
                    if (IsValidInstagramUsername(username))
                    {
                        usernameSet.Add(username);
                    }
                }

                if (usernameSet.Count == 0)
                {
                    foreach (var username in ExtractUsernamesFromText(htmlContent))
                    {
                        if (IsValidInstagramUsername(username))
                        {
                            usernameSet.Add(username);
                        }
                    }
                }

                foreach (var username in usernameSet.OrderBy(u => u, StringComparer.OrdinalIgnoreCase))
                {
                    users.Add(new InstagramUser { Username = username });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error parsing HTML: {ex.Message}");
            }

            return users;
        }

        private IEnumerable<string> ExtractUsernamesFromAnchors(string htmlContent)
        {
            var hrefRegex = new Regex(@"<a[^>]+href=""(?<href>[^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            var matches = hrefRegex.Matches(htmlContent);

            foreach (Match match in matches)
            {
                var href = match.Groups["href"].Value?.Trim();
                if (string.IsNullOrWhiteSpace(href))
                    continue;

                var normalizedHref = href.Split('?', 2)[0].Split('#', 2)[0];
                if (!normalizedHref.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !normalizedHref.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!normalizedHref.Contains("instagram.com", StringComparison.OrdinalIgnoreCase))
                    continue;

                string path;
                try
                {
                    path = new Uri(normalizedHref).AbsolutePath;
                }
                catch
                {
                    path = normalizedHref;
                }

                path = path.Trim('/');
                if (path.StartsWith("_u/", StringComparison.OrdinalIgnoreCase))
                {
                    path = path.Substring(3);
                }

                path = path.Split('/', 2)[0].Trim();
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                yield return path;
            }
        }

        private IEnumerable<string> ExtractUsernamesFromText(string htmlContent)
        {
            var cleaned = Regex.Replace(htmlContent, "<[^>]+>", " ", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            cleaned = Regex.Replace(cleaned, "&nbsp;", " ", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\s+", "\n", RegexOptions.Multiline);

            var headerLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "followers",
                "following",
                "follower",
                "seguaci",
                "seguiti",
                "seguito",
                "segui"
            };

            foreach (var line in cleaned.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var value = line.Trim();
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                if (headerLabels.Contains(value))
                    continue;

                if (value.Equals("profiles you choose to see content from", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (IsValidInstagramUsername(value))
                {
                    yield return value;
                }
            }
        }

        private bool IsValidInstagramUsername(string username)
        {
            if (string.IsNullOrWhiteSpace(username))
                return false;

            if (username.Equals("_u", StringComparison.OrdinalIgnoreCase))
                return false;

            if (username.Contains("/", StringComparison.Ordinal))
                return false;

            if (username.Contains("?", StringComparison.Ordinal) || username.Contains("#", StringComparison.Ordinal))
                return false;

            return username.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '_');
        }
    }
}
