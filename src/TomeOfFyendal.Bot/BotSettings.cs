namespace TomeOfFyendal.Bot
{
    /// <summary>"Bot" configuration section. The token comes from Vault or user-secrets, never from appsettings.</summary>
    public class BotSettings
    {
        public string? BotToken { get; set; }
        public ulong GuildId { get; set; }
        /// <summary>Channels whose posts are parsed as listings (selling/buying/trading marketplace).</summary>
        public ulong[] ListingChannelIds { get; set; } = Array.Empty<ulong>();
        /// <summary>#paypal-watchlist: a post naming exactly one member marks them "reported before".</summary>
        public ulong? WatchlistChannelId { get; set; }
        /// <summary>How far back to read the listing channels on startup.</summary>
        public int BackfillDays { get; set; } = 30;
        /// <summary>Listings not posted or bumped (the seller replying "bump") in this many days are marked Expired.</summary>
        public int ListingExpireDays { get; set; } = 10;
        /// <summary>Removed and expired listings are deleted for good this many days after they leave the board (privacy policy).</summary>
        public int PurgeAfterDays { get; set; } = 7;
        /// <summary>Flag asks this far under the TCGplayer market (fraction).</summary>
        public decimal UnderMarketFlagFraction { get; set; } = 0.25m;
    }
}
