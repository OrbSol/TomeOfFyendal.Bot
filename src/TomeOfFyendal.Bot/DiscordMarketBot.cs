using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TomeOfFyendal.Bot.Data;

namespace TomeOfFyendal.Bot
{
    /// <summary>
    /// Read-only Discord gateway listener for the marketplace channels. Needs the Message Content
    /// privileged intent. Never posts, reacts, DMs or edits. Stores parsed listings, marks deletions,
    /// records watchlist reports, and reads channel history once at startup.
    /// </summary>
    public class DiscordMarketBot : BackgroundService
    {
        private readonly IServiceProvider _services;
        private readonly BotSettings _settings;
        private readonly ILogger<DiscordMarketBot> _logger;
        private DiscordSocketClient? _client;
        private int _readyOnce;

        public DiscordMarketBot(IServiceProvider services, IOptions<BotSettings> settings, ILogger<DiscordMarketBot> logger)
        {
            _services = services;
            _settings = settings.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _client = new DiscordSocketClient(new DiscordSocketConfig
            {
                // No GuildMembers or GuildPresences: the bot never lists members or watches who is online.
                GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent,
                MessageCacheSize = 0,
                AlwaysDownloadUsers = false,
                LogLevel = LogSeverity.Info,
            });
            _client.Log += OnLog;
            _client.Ready += OnReady;
            // Handlers hand off to the thread pool so the gateway task is never blocked.
            _client.MessageReceived += msg => { FireAndForget(() => HandleMessageAsync(msg, isEdit: false, stoppingToken), "message"); return Task.CompletedTask; };
            _client.MessageUpdated += (before, after, channel) =>
            {
                // Discord also sends MESSAGE_UPDATE when it unfurls a link embed. Those carry no edit
                // timestamp and often no content; treating them as edits would wipe the listing.
                if (after.EditedTimestamp is null) return Task.CompletedTask;
                FireAndForget(() => HandleMessageAsync(after, isEdit: true, stoppingToken), "edit");
                return Task.CompletedTask;
            };
            _client.MessageDeleted += (message, channel) => { FireAndForget(() => MarkRemovedAsync(message.Id, stoppingToken), "delete"); return Task.CompletedTask; };

            try
            {
                await _client.LoginAsync(TokenType.Bot, _settings.BotToken);
                await _client.StartAsync();

                while (!stoppingToken.IsCancellationRequested)
                {
                    try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken); } catch (OperationCanceledException) { break; }
                    await SafeAsync(() => ExpireOldAsync(stoppingToken), "expire");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Discord bot failed to start");
                throw; // let the service exit so systemd shows the failure
            }
            finally
            {
                try { await _client.StopAsync(); } catch { /* shutting down */ }
                await _client.DisposeAsync();
            }
        }

        private Task OnLog(LogMessage m)
        {
            var level = m.Severity switch
            {
                LogSeverity.Critical or LogSeverity.Error => LogLevel.Error,
                LogSeverity.Warning => LogLevel.Warning,
                LogSeverity.Info => LogLevel.Information,
                _ => LogLevel.Debug,
            };
            _logger.Log(level, m.Exception, "[Discord] {Source}: {Message}", m.Source, m.Message);
            return Task.CompletedTask;
        }

        private Task OnReady()
        {
            if (Interlocked.Exchange(ref _readyOnce, 1) == 1) return Task.CompletedTask;
            _logger.LogInformation("Discord bot ready as {User}; backfilling {Days} days", _client!.CurrentUser?.Username, _settings.BackfillDays);
            _ = Task.Run(() => SafeAsync(() => BackfillAsync(CancellationToken.None), "backfill"));
            return Task.CompletedTask;
        }

        private void FireAndForget(Func<Task> work, string what) => _ = Task.Run(() => SafeAsync(work, what));

        private async Task SafeAsync(Func<Task> work, string what)
        {
            try { await work(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogError(ex, "Discord bot: {What} failed", what); }
        }

        // ----------------------------------------------------------------- ingest

        // One message at a time: a post and its edit (or a live post during backfill) can otherwise both
        // see "no listing yet" and both insert, tripping the unique MessageId index. Volume is low.
        private readonly SemaphoreSlim _ingest = new(1, 1);

        private async Task HandleMessageAsync(SocketMessage msg, bool isEdit, CancellationToken ct)
        {
            if (msg.Author.IsBot || msg.Author.IsWebhook) return;
            if (msg.Channel is not SocketGuildChannel gc || gc.Guild.Id != _settings.GuildId) return;

            await _ingest.WaitAsync(ct);
            try
            {
                // Watchlist posts apply once: edits never re-apply.
                if (_settings.WatchlistChannelId == msg.Channel.Id) { if (!isEdit) await AddWatchlistAsync(msg, ct); return; }
                if (Array.IndexOf(_settings.ListingChannelIds, msg.Channel.Id) < 0) return;
                await UpsertListingAsync(msg, ct);
            }
            finally { _ingest.Release(); }
        }

        private async Task UpsertListingAsync(IMessage msg, CancellationToken ct)
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var resolver = scope.ServiceProvider.GetRequiredService<FabCardResolver>();
            var now = DateTime.UtcNow;

            var authorId = unchecked((long)msg.Author.Id);
            var seller = await db.FabDiscordSellers.FindAsync(new object[] { authorId }, ct);
            if (seller?.OptedOut == true) return;

            var parsed = DiscordListingParser.Parse(msg.Content);
            var messageId = unchecked((long)msg.Id);
            var listing = await db.FabDiscordListings.Include(l => l.Items).FirstOrDefaultAsync(l => l.MessageId == messageId, ct);

            // A "W:" line inside a sell post (or "H:" inside a buy post) is a different kind of listing;
            // items carry no type of their own, so those lines are left out rather than mislabelled.
            // Trade posts keep both sides.
            var candidates = parsed.Type == ListingType.WTT
                ? parsed.Items
                : parsed.Items.Where(i => i.LineType is null || i.LineType == parsed.Type || i.LineType == ListingType.WTT).ToList();
            // Only lines that match a real card are kept: shipping notes, "Others:", playmats and typos stay in Discord.
            var kept = new List<(ParsedItem Item, FabCardResolver.Resolved Card)>();
            foreach (var item in candidates)
                if (await resolver.ResolveAsync(item, ct) is { } card) kept.Add((item, card));

            if (parsed.IsEmpty || kept.Count == 0)
            {
                if (listing is not null) { listing.Status = "Removed"; listing.RemovedAt = now; listing.UpdatedAt = now; await db.SaveChangesAsync(ct); }
                return;
            }

            var guildUser = msg.Author as IGuildUser;
            var displayName = Trunc(guildUser?.DisplayName ?? guildUser?.Nickname ?? msg.Author.GlobalName ?? msg.Author.Username, 100);

            if (seller is null)
            {
                seller = new FabDiscordSeller { AuthorId = authorId, FirstSeen = now };
                db.FabDiscordSellers.Add(seller);
            }
            seller.LastSeen = now;

            if (listing is null)
            {
                listing = new FabDiscordListing
                {
                    GuildId = unchecked((long)_settings.GuildId),
                    ChannelId = unchecked((long)msg.Channel.Id),
                    MessageId = messageId,
                    PostedAt = msg.Timestamp.UtcDateTime,
                    AuthorId = authorId,
                    CreatedAt = now,
                };
                db.FabDiscordListings.Add(listing);
            }
            else
            {
                db.FabDiscordListingItems.RemoveRange(listing.Items);
                listing.Items.Clear();
            }

            listing.ChannelName = Trunc(msg.Channel.Name, 100);
            listing.EditedAt = msg.EditedTimestamp?.UtcDateTime;
            listing.Type = parsed.Type.ToString();
            listing.AuthorName = displayName;
            listing.Status = "Active";
            listing.RemovedAt = null;
            listing.HasImage = msg.Attachments.Count > 0;
            (listing.PhotoUrl, listing.PhotoExpiresAt) = PostPhoto.From(msg.Attachments.Select(a => (a.Url, (string?)a.ContentType)), now);
            listing.MarketPercent = (byte?)parsed.MarketPercent;
            listing.PercentBasis = parsed.PercentBasis;
            listing.UpdatedAt = now;

            // Reported first: FlagReason is truncated to 200 characters.
            var flags = new List<string>();
            if (seller.Watchlisted) flags.Add(ReportedFlag);
            if (parsed.HasOffPlatformPayment) flags.Add("payment outside Discord/PayPal G&S");
            // A percent of TCG Low says nothing about market, so only "% of market" can raise this flag.
            if (parsed.Type == ListingType.WTS && parsed.PercentBasis is null && parsed.MarketPercent is int pct && pct <= 100 * (1 - _settings.UnderMarketFlagFraction))
                flags.Add($"asks {pct}% of TCGplayer market");

            foreach (var (item, resolved) in kept)
            {
                var row = new FabDiscordListingItem
                {
                    PrintingUniqueId = resolved.PrintingUniqueId,
                    ProductId = resolved.ProductId,
                    CardName = Trunc(resolved.CardName, 200)!,
                    SetCode = Trunc(resolved.SetId ?? item.SetCode, 10),
                    Foiling = item.Foiling ?? resolved.Foiling,
                    Condition = item.Condition,
                    Variant = Trunc(item.Variant, 20),
                    Price = item.Price,
                    Currency = item.Currency,
                    Qty = item.Qty,
                    Confidence = (byte)Math.Min(100, item.Confidence + 15),
                };
                listing.Items.Add(row);

                // A Marvel, extended art, graded copy... is priced on its own; the catalog cannot tell its market yet.
                if (parsed.Type == ListingType.WTS && item.Variant is null && item.Currency == "USD" && item.Price is decimal ask && resolved.ProductId is int pid)
                {
                    var market = await LatestMarketAsync(db, pid, row.Foiling, ct);
                    if (market is decimal m && m > 0 && ask < m * (1 - _settings.UnderMarketFlagFraction))
                        flags.Add($"{row.CardName} asks {(1 - ask / m):P0} under market");
                }
            }
            listing.ItemCount = listing.Items.Count;
            listing.Summary = DiscordListingParser.Summarize(parsed.Type, kept.Select(k => k.Item).ToList());
            listing.Flagged = flags.Count > 0;
            listing.FlagReason = flags.Count > 0 ? Trunc(string.Join("; ", flags.Distinct()), 200) : null;

            await db.SaveChangesAsync(ct);
        }

        /// <summary>Latest market for the listed finish only (see <see cref="MarketPrice"/>).</summary>
        private static async Task<decimal?> LatestMarketAsync(BotDbContext db, int productId, string? foiling, CancellationToken ct)
        {
            var latest = await db.FabPriceSnapshots.AsNoTracking()
                .Where(s => s.ProductId == productId)
                .MaxAsync(s => (DateOnly?)s.SnapshotDate, ct);
            if (latest is null) return null;
            var rows = await db.FabPriceSnapshots.AsNoTracking()
                .Where(s => s.ProductId == productId && s.SnapshotDate == latest)
                .Select(s => new { s.SubType, s.Market })
                .ToListAsync(ct);
            return MarketPrice.ForFoil(rows.Select(r => (r.SubType, r.Market)), foiling);
        }

        private async Task MarkRemovedAsync(ulong messageId, CancellationToken ct)
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var id = unchecked((long)messageId);
            var now = DateTime.UtcNow;
            await db.FabDiscordListings.Where(l => l.MessageId == id && l.Status == "Active")
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.Status, "Removed").SetProperty(l => l.RemovedAt, now).SetProperty(l => l.UpdatedAt, now), ct);
        }

        private async Task ExpireOldAsync(CancellationToken ct)
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var cutoff = DateTime.UtcNow.AddDays(-_settings.ListingExpireDays);
            var now = DateTime.UtcNow;
            var n = await db.FabDiscordListings.Where(l => l.Status == "Active" && l.PostedAt < cutoff)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.Status, "Expired").SetProperty(l => l.UpdatedAt, now), ct);
            if (n > 0) _logger.LogInformation("Expired {Count} listings older than {Days} days", n, _settings.ListingExpireDays);

            // Retention promised in the privacy policy: off the board for PurgeAfterDays -> deleted (items cascade).
            // A seller row survives only while it has listings, an opt-out to honour, or a report.
            var purgeBefore = now.AddDays(-_settings.PurgeAfterDays);
            var purged = await db.FabDiscordListings.Where(l => l.Status != "Active" && l.UpdatedAt < purgeBefore).ExecuteDeleteAsync(ct);
            var sellers = await db.FabDiscordSellers
                .Where(s => !s.OptedOut && !s.Watchlisted && !db.FabDiscordListings.Any(l => l.AuthorId == s.AuthorId))
                .ExecuteDeleteAsync(ct);
            if (purged + sellers > 0) _logger.LogInformation("Purged {Listings} old listings and {Sellers} seller rows", purged, sellers);
        }

        private const string ReportedFlag = "reported before in #paypal-watchlist";

        /// <summary>
        /// A watchlist post often names the reporter or a victim as well as the offender, and the bot
        /// cannot tell them apart. So only a post that mentions exactly one user (besides its author)
        /// marks that user; anything ambiguous is logged for a moderator and changes nothing.
        /// </summary>
        private async Task AddWatchlistAsync(IMessage msg, CancellationToken ct)
        {
            var mentioned = msg.MentionedUserIds.Where(id => id != msg.Author.Id).Distinct().ToList();
            if (mentioned.Count == 0) return;
            if (mentioned.Count > 1)
            {
                _logger.LogInformation("Watchlist post {MessageId} mentions {Count} users; not applied automatically", msg.Id, mentioned.Count);
                return;
            }
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var now = DateTime.UtcNow;
            var id = unchecked((long)mentioned[0]);
            var seller = await db.FabDiscordSellers.FindAsync(new object[] { id }, ct);
            if (seller is null) { seller = new FabDiscordSeller { AuthorId = id, FirstSeen = now, LastSeen = now }; db.FabDiscordSellers.Add(seller); }
            seller.Watchlisted = true;
            // Keep the post's other flag reasons; reported goes first.
            foreach (var l in await db.FabDiscordListings.Where(l => l.AuthorId == id && l.Status == "Active").ToListAsync(ct))
            {
                if (l.FlagReason?.Contains(ReportedFlag) == true) continue;
                l.Flagged = true;
                l.FlagReason = Trunc(l.FlagReason is null ? ReportedFlag : $"{ReportedFlag}; {l.FlagReason}", 200);
                l.UpdatedAt = now;
            }
            await db.SaveChangesAsync(ct);
        }

        // --------------------------------------------------------------- backfill

        private async Task BackfillAsync(CancellationToken ct)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-_settings.BackfillDays);

            // Reports first, so listings read during the pass are flagged correctly.
            if (_settings.WatchlistChannelId is ulong wl) await WalkChannelAsync(wl, DateTimeOffset.UtcNow.AddDays(-365), AddWatchlistAsync, ct);

            var seen = 0;
            foreach (var channelId in _settings.ListingChannelIds)
            {
                var walkStart = DateTime.UtcNow;
                if (await WalkChannelAsync(channelId, cutoff, UpsertListingAsync, ct) is not { } walk) continue;
                seen += walk.Seen.Count;
                await RemoveMissingAsync(channelId, walk.Seen, walk.Covered.UtcDateTime, walkStart, ct);
            }

            await ExpireOldAsync(ct);
            _logger.LogInformation("Discord backfill done: {Count} listing messages processed", seen);
        }

        /// <summary>
        /// Reads a channel newest-first back to <paramref name="cutoff"/>. Returns the ids read and how far back
        /// the walk got (the cutoff, unless the page cap stopped it first), or null when the bot cannot see it.
        /// </summary>
        private async Task<(HashSet<long> Seen, DateTimeOffset Covered)?> WalkChannelAsync(ulong channelId, DateTimeOffset cutoff, Func<IMessage, CancellationToken, Task> handler, CancellationToken ct)
        {
            if (_client?.GetChannel(channelId) is not IMessageChannel channel)
            {
                _logger.LogWarning("Discord: channel {Id} not visible to the bot", channelId);
                return null;
            }

            var seen = new HashSet<long>();
            var covered = DateTimeOffset.UtcNow;
            IMessage? oldest = null;
            for (var page = 0; page < 200; page++)
            {
                var batch = oldest is null
                    ? await channel.GetMessagesAsync(100).FlattenAsync()
                    : await channel.GetMessagesAsync(oldest, Direction.Before, 100).FlattenAsync();
                var list = batch.OrderByDescending(m => m.Timestamp).ToList();
                if (list.Count == 0) { covered = cutoff; break; }

                var stop = false;
                foreach (var m in list)
                {
                    if (m.Timestamp < cutoff) { stop = true; break; }
                    seen.Add(unchecked((long)m.Id));
                    if (m.Author.IsBot || m.Author.IsWebhook) continue;
                    await _ingest.WaitAsync(ct);
                    try { await handler(m, ct); }
                    finally { _ingest.Release(); }
                }
                if (stop) { covered = cutoff; break; }
                oldest = list[^1];
                covered = oldest.Timestamp;
                await Task.Delay(TimeSpan.FromMilliseconds(600), ct);
            }
            return (seen, covered);
        }

        /// <summary>Posts deleted while the bot was offline: active listings in the walked window that Discord no longer has.</summary>
        private async Task RemoveMissingAsync(ulong channelId, HashSet<long> seen, DateTime from, DateTime to, CancellationToken ct)
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var channel = unchecked((long)channelId);
            var ids = seen.ToList();
            var now = DateTime.UtcNow;
            var n = await db.FabDiscordListings
                .Where(l => l.ChannelId == channel && l.Status == "Active" && l.PostedAt >= from && l.PostedAt < to && !ids.Contains(l.MessageId))
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.Status, "Removed").SetProperty(l => l.RemovedAt, now).SetProperty(l => l.UpdatedAt, now), ct);
            if (n > 0) _logger.LogInformation("{Count} listings in channel {Id} were deleted while offline", n, channelId);
        }

        private static string? Trunc(string? s, int max) => s is null ? null : (s.Length <= max ? s : s[..max]);
    }
}
