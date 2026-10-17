using System.Text.RegularExpressions;

namespace TomeOfFyendal.Bot
{
    /// <summary>
    /// A post's first picture: Discord's own signed link and when it expires, read from the link's "ex"
    /// parameter (hex unix seconds). Only the link is kept, never the picture; the website shows it to
    /// signed-in members until it expires.
    /// </summary>
    public static class PostPhoto
    {
        private static readonly Regex ExRx = new(@"[?&]ex=(?<hex>[0-9a-fA-F]{6,12})(?:&|$)", RegexOptions.Compiled);
        private static readonly Regex ImageExtRx = new(@"\.(?:png|jpe?g|webp|gif)(?:\?|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <param name="attachments">Each attachment's URL and content type (null when Discord gave none).</param>
        /// <param name="utcNow">Fallback expiry base for a link without "ex": 20 hours from now, under Discord's usual day.</param>
        public static (string? Url, DateTime? ExpiresAt) From(IEnumerable<(string Url, string? ContentType)> attachments, DateTime utcNow)
        {
            foreach (var (url, type) in attachments)
            {
                var isImage = type?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? ImageExtRx.IsMatch(url);
                var fromDiscord = url.StartsWith("https://cdn.discordapp.com/", StringComparison.Ordinal)
                               || url.StartsWith("https://media.discordapp.net/", StringComparison.Ordinal);
                if (!isImage || !fromDiscord || url.Length > 1000) continue;

                var ex = ExRx.Match(url);
                var expires = ex.Success && long.TryParse(ex.Groups["hex"].Value, System.Globalization.NumberStyles.HexNumber, null, out var secs)
                    ? DateTimeOffset.FromUnixTimeSeconds(secs).UtcDateTime
                    : utcNow.AddHours(20);
                return (url, expires);
            }
            return (null, null);
        }
    }
}
