using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

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

        public async Task<AnalysisResult> AnalyzeFollowersAsync(string followersJson, string followingJson)
        {
            return await Task.Run(() =>
            {
                var result = new AnalysisResult();

                try
                {
                    // Parse JSON data (assuming Instagram data download format)
                    result.Followers = ParseInstagramJson(followersJson).OrderBy(u => u.Username).ToList();
                    result.Following = ParseInstagramJson(followingJson).OrderBy(u => u.Username).ToList();

                    // Find users who we follow but don't follow us back
                    var followersSet = new HashSet<string>(result.Followers.Select(u => u.Username.ToLower()));
                    result.NotFollowingBack = result.Following
                        .Where(u => !followersSet.Contains(u.Username.ToLower()))
                        .OrderBy(u => u.Username)
                        .ToList();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error analyzing followers: {ex.Message}");
                }

                return result;
            });
        }

        private List<InstagramUser> ParseInstagramJson(string jsonData)
        {
            var users = new List<InstagramUser>();

            if (string.IsNullOrWhiteSpace(jsonData))
                return users;

            try
            {
                // Try to parse as JSON array of objects with "string_list_data" key
                // This is the format Instagram uses in their data download
                int startIdx = 0;
                while (true)
                {
                    int valueIdx = jsonData.IndexOf("\"value\":", startIdx);
                    if (valueIdx < 0)
                        break;

                    int colonIdx = jsonData.IndexOf(":", valueIdx);
                    int quoteIdx = jsonData.IndexOf("\"", colonIdx + 1);
                    int endQuoteIdx = jsonData.IndexOf("\"", quoteIdx + 1);

                    if (endQuoteIdx < 0)
                        break;

                    string username = jsonData.Substring(quoteIdx + 1, endQuoteIdx - quoteIdx - 1).Trim();

                    if (!string.IsNullOrWhiteSpace(username) && IsValidInstagramUsername(username))
                    {
                        users.Add(new InstagramUser { Username = username });
                    }

                    startIdx = endQuoteIdx + 1;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error parsing JSON: {ex.Message}");
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
