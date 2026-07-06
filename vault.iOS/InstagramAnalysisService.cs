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
                    string zipPath = zipUrl.Path;
                    if (!File.Exists(zipPath))
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
                // Extract usernames from Instagram hrefs
                var pattern = new Regex(@"https?://(?:www\.)?instagram\.com/([A-Za-z0-9._]+)", RegexOptions.IgnoreCase);
                var matches = pattern.Matches(htmlContent);

                var usernameSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match match in matches)
                {
                    string username = match.Groups[1].Value;
                    if (!string.IsNullOrWhiteSpace(username) && IsValidInstagramUsername(username))
                    {
                        usernameSet.Add(username);
                    }
                }

                foreach (var username in usernameSet.OrderBy(u => u))
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

        private bool IsValidInstagramUsername(string username)
        {
            if (string.IsNullOrWhiteSpace(username))
                return false;

            // Instagram usernames can contain letters, numbers, periods, and underscores
            return username.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '_');
        }
    }
}
