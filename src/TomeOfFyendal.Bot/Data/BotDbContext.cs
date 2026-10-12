using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace TomeOfFyendal.Bot.Data
{
    /// <summary>
    /// The only tables the bot touches in the shared Tome of Fyendal database. It writes the three
    /// Discord tables and reads the card catalog and daily prices. The schema is owned by the website's
    /// migrations; this context never creates or changes tables, and its SQL login cannot (see deploy/db-access.sql).
    /// </summary>
    public class BotDbContext : DbContext
    {
        public BotDbContext(DbContextOptions<BotDbContext> options) : base(options) { }

        // Written by the bot
        public DbSet<FabDiscordListing> FabDiscordListings => Set<FabDiscordListing>();
        public DbSet<FabDiscordListingItem> FabDiscordListingItems => Set<FabDiscordListingItem>();
        public DbSet<FabDiscordSeller> FabDiscordSellers => Set<FabDiscordSeller>();

        // Read only: match post lines to cards, compare asks with the market
        public DbSet<FabCard> FabCards => Set<FabCard>();
        public DbSet<FabPrinting> FabPrintings => Set<FabPrinting>();
        public DbSet<FabPriceSnapshot> FabPriceSnapshots => Set<FabPriceSnapshot>();

        protected override void OnModelCreating(ModelBuilder b)
        {
            b.Entity<FabDiscordListing>(e =>
            {
                e.HasMany(x => x.Items).WithOne(i => i.Listing).HasForeignKey(i => i.ListingId);
            });
            b.Entity<FabDiscordListingItem>(e => e.Property(x => x.Price).HasPrecision(10, 2));
            b.Entity<FabDiscordSeller>(e => e.Property(x => x.AuthorId).ValueGeneratedNever());
            b.Entity<FabCard>(e => e.HasMany(x => x.Printings).WithOne(p => p.Card).HasForeignKey(p => p.CardUniqueId));
            b.Entity<FabPriceSnapshot>(e =>
            {
                e.HasKey(x => new { x.ProductId, x.SubType, x.SnapshotDate });
                e.Property(x => x.Market).HasPrecision(9, 2);
            });
        }
    }

    /// <summary>
    /// One Discord marketplace post. Only parsed facts are kept: type, items, prices, author id and
    /// display name, timestamps. The message text itself is never stored.
    /// </summary>
    public class FabDiscordListing
    {
        [Key] public long Id { get; set; }
        public long GuildId { get; set; }
        public long ChannelId { get; set; }
        public long MessageId { get; set; }
        [StringLength(100)] public string? ChannelName { get; set; }
        public DateTime PostedAt { get; set; }
        public DateTime? EditedAt { get; set; }
        /// <summary>WTS, WTB or WTT.</summary>
        [Required, StringLength(4)] public string Type { get; set; } = "WTS";
        public long AuthorId { get; set; }
        [StringLength(100)] public string? AuthorName { get; set; }
        /// <summary>Active, Removed (message deleted or no longer a listing) or Expired (older than the window).</summary>
        [Required, StringLength(10)] public string Status { get; set; } = "Active";
        public DateTime? RemovedAt { get; set; }
        /// <summary>Generated one-line description, never the member's own text.</summary>
        [StringLength(300)] public string? Summary { get; set; }
        public int ItemCount { get; set; }
        public bool HasImage { get; set; }
        public bool Flagged { get; set; }
        [StringLength(200)] public string? FlagReason { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public ICollection<FabDiscordListingItem> Items { get; set; } = new List<FabDiscordListingItem>();
    }

    /// <summary>One card line inside a listing, resolved to a printing and TCGplayer product where possible.</summary>
    public class FabDiscordListingItem
    {
        [Key] public long Id { get; set; }
        public long ListingId { get; set; }
        [StringLength(100)] public string? PrintingUniqueId { get; set; }
        public int? ProductId { get; set; }
        [Required, StringLength(200)] public string CardName { get; set; } = string.Empty;
        [StringLength(10)] public string? SetCode { get; set; }
        /// <summary>S, R, C or G (the-fab-cube codes). Null when the post did not say.</summary>
        [StringLength(2)] public string? Foiling { get; set; }
        /// <summary>NM, LP, MP, HP or DMG.</summary>
        [StringLength(4)] public string? Condition { get; set; }
        public decimal? Price { get; set; }
        [Required, StringLength(3)] public string Currency { get; set; } = "USD";
        public int Qty { get; set; } = 1;
        /// <summary>0-100 parser confidence.</summary>
        public byte Confidence { get; set; }

        public FabDiscordListing? Listing { get; set; }
    }

    /// <summary>One row per Discord author: opt-out, and whether they were reported. No score is kept.</summary>
    public class FabDiscordSeller
    {
        [Key] public long AuthorId { get; set; }
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
        /// <summary>Member asked to be excluded; their posts are skipped.</summary>
        public bool OptedOut { get; set; }
        /// <summary>Reported before: named alone in the server's watchlist channel.</summary>
        public bool Watchlisted { get; set; }
    }

    /// <summary>A card from the open the-fab-cube dataset (read only; imported by the website).</summary>
    public class FabCard
    {
        [Key] public string UniqueId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        /// <summary>"1" red, "2" yellow, "3" blue, null when the card has no pitch.</summary>
        public string? Pitch { get; set; }
        public ICollection<FabPrinting> Printings { get; set; } = new List<FabPrinting>();
    }

    /// <summary>One printing of a card (read only).</summary>
    public class FabPrinting
    {
        [Key] public string PrintingUniqueId { get; set; } = string.Empty;
        public string CardUniqueId { get; set; } = string.Empty;
        public string? SetId { get; set; }
        /// <summary>Collector number as printed, e.g. CRU178.</summary>
        public string? CardId { get; set; }
        public string? Edition { get; set; }
        public string? Foiling { get; set; }
        public string Language { get; set; } = "en";
        public string? TcgPlayerProductId { get; set; }
        public bool IsBackFace { get; set; }
        public FabCard? Card { get; set; }
    }

    /// <summary>Daily TCGplayer market per product and finish (read only; collected by the website).</summary>
    public class FabPriceSnapshot
    {
        public int ProductId { get; set; }
        public string SubType { get; set; } = "Normal";
        public DateOnly SnapshotDate { get; set; }
        public decimal? Market { get; set; }
    }
}
