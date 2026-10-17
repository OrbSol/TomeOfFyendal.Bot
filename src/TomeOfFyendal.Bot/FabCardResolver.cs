using System.Text.RegularExpressions;
using TomeOfFyendal.Bot.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace TomeOfFyendal.Bot
{
    /// <summary>
    /// Maps a parsed listing line to a FabPrintings row and its TCGplayer product.
    /// Set+number (CRU178) wins. Otherwise the name is matched against FabCards,
    /// accent and punctuation insensitive, with the pitch colour breaking ties
    /// between the red/yellow/blue versions of a name, and a prefix fallback for
    /// posts that shorten long names. Cards the-fab-cube has not added yet (new sets) fall back
    /// to TCGplayer product names. Both name indexes are small and cached an hour.
    /// </summary>
    public class FabCardResolver
    {
        private const string NameIndexKey = "fabmarket:name-index";
        private const string ProductIndexKey = "fabmarket:product-index";
        private static readonly Regex NonAlnum = new(@"[^a-z0-9]+", RegexOptions.Compiled);
        // TCGplayer names: "Staunch Response (Red) (Marvel) - TNP019", "Deadly Spinneret (Extended Art)".
        private static readonly Regex ParenRx = new(@"\(([^)]*)\)", RegexOptions.Compiled);
        private static readonly Regex NumberSuffixRx = new(@"\s+-\s+[A-Z0-9]+$", RegexOptions.Compiled);

        private readonly BotDbContext _db;
        private readonly IMemoryCache _cache;

        public FabCardResolver(BotDbContext db, IMemoryCache cache)
        {
            _db = db;
            _cache = cache;
        }

        /// <param name="PrintingUniqueId">Null when matched through a TCGplayer product name only.</param>
        public sealed record Resolved(string? PrintingUniqueId, int? ProductId, string CardName, string? SetId, string? Foiling);
        private sealed record IndexEntry(string UniqueId, string? Pitch);
        private sealed record ProductEntry(int ProductId, string Name, string? Number, string? Pitch, string Tags);

        public async Task<Resolved?> ResolveAsync(ParsedItem item, CancellationToken ct)
        {
            if (item.Number is not null)
            {
                var byNumber = await _db.FabPrintings.AsNoTracking()
                    .Where(p => p.CardId == item.Number && p.Language == "en" && !p.IsBackFace)
                    .Select(p => new { p.PrintingUniqueId, p.TcgPlayerProductId, p.SetId, p.Foiling, p.Edition, Name = p.Card!.Name })
                    .ToListAsync(ct);
                var pick = await PreferAsync(byNumber.Select(p => (p.PrintingUniqueId, p.TcgPlayerProductId, p.SetId, p.Foiling, p.Edition, p.Name)), item.Foiling, item.Edition, ct);
                if (pick is not null) return pick;
            }

            var key = Normalize(item.CardName);
            if (key.Length < 3) return null;
            var index = await GetNameIndexAsync(ct);
            var pitch = item.Pitch;
            // "Arakni, Redback": the parser cuts at a comma (chatter like "Tunic, NM"); an exact full name wins.
            if (item.FullName is not null && Normalize(item.FullName) is var full
                && (index.ContainsKey(full) || (await GetProductIndexAsync(ct)).ContainsKey(full)))
                key = full;

            if (!index.TryGetValue(key, out List<IndexEntry>? entries))
                (entries, pitch) = WithoutLeadingWords(index, item.CardName, pitch);
            if (entries is null)
            {
                // Prefix fallback: "Fyendal's Spring" -> "fyendalsspringtunic"; longest match wins.
                // Both sides need 6+ characters, or a short fragment would match some long name.
                var best = key.Length < 6 ? null
                    : index.Keys.Where(k => k.Length >= 6 && (k.StartsWith(key) || key.StartsWith(k))).OrderByDescending(k => k.Length).FirstOrDefault();
                if (best is null) return await ResolveByProductAsync(item, key, ct);
                entries = index[best];
            }

            var cardUniqueId = entries.Count == 1 ? entries[0].UniqueId
                : (entries.FirstOrDefault(e => pitch != null && e.Pitch == pitch) ?? entries[0]).UniqueId;

            var printings = await _db.FabPrintings.AsNoTracking()
                .Where(p => p.CardUniqueId == cardUniqueId && p.Language == "en" && !p.IsBackFace)
                .Select(p => new { p.PrintingUniqueId, p.TcgPlayerProductId, p.SetId, p.Foiling, p.Edition, Name = p.Card!.Name })
                .ToListAsync(ct);

            var scoped = item.SetCode is null ? printings : printings.Where(p => string.Equals(p.SetId, item.SetCode, StringComparison.OrdinalIgnoreCase)).ToList();
            if (scoped.Count == 0) scoped = printings;
            return await PreferAsync(scoped.Select(p => (p.PrintingUniqueId, p.TcgPlayerProductId, p.SetId, p.Foiling, p.Edition, p.Name)), item.Foiling, item.Edition, ct);
        }

        /// <summary>
        /// "Atlanta Sink Below", "Tournament pack yellow Warrior's Valor": drops up to three leading words until the
        /// rest is a card name (six or more letters). A dropped red/yellow/blue becomes the pitch. Exact matches only,
        /// so "Red in the Ledger" still matches itself first.
        /// </summary>
        private static (List<T>? Entries, string? Pitch) WithoutLeadingWords<T>(Dictionary<string, List<T>> index, string name, string? pitch)
        {
            var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var drop = 1; drop <= Math.Min(3, words.Length - 1); drop++)
            {
                var dropped = words[drop - 1].ToLowerInvariant();
                pitch = dropped switch { "red" => "1", "yellow" or "yel" => "2", "blue" or "blu" => "3", _ => pitch };
                var key = Normalize(string.Join(' ', words[drop..]));
                if (key.Length >= 6 && index.TryGetValue(key, out var entries)) return (entries, pitch);
            }
            return (null, pitch);
        }

        /// <summary>
        /// New sets reach TCGplayer (and our daily product list) before the-fab-cube dataset. Matches the product
        /// name without its "(Red)", "(Marvel)", "- TNP019" parts; prefers the named pitch, then the named version
        /// (Marvel, EA, AA, FA), otherwise a regular product; ties go to the cheapest, like printings.
        /// </summary>
        private async Task<Resolved?> ResolveByProductAsync(ParsedItem item, string key, CancellationToken ct)
        {
            var index = await GetProductIndexAsync(ct);
            var pitch = item.Pitch;
            if (!index.TryGetValue(key, out List<ProductEntry>? list))
                (list, pitch) = WithoutLeadingWords(index, item.CardName, pitch);
            if (list is null) return null;

            var wants = (item.Variant ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            bool Named(ProductEntry e) =>
                (wants.Contains("Marvel") && e.Tags.Contains("marvel")) || (wants.Contains("EA") && e.Tags.Contains("extended art"))
                || (wants.Contains("AA") && e.Tags.Contains("alternate art")) || (wants.Contains("FA") && e.Tags.Contains("full art"));
            static bool Special(ProductEntry e) =>
                e.Tags.Contains("marvel") || e.Tags.Contains("extended art") || e.Tags.Contains("alternate art")
                || e.Tags.Contains("full art") || e.Tags.Contains("korean");

            var byPitch = pitch is null ? list : list.Where(e => e.Pitch == pitch).ToList();
            if (byPitch.Count == 0) byPitch = list;
            // Region exclusives (Korean) only when nothing else carries the named version.
            var named = byPitch.Where(Named).ToList();
            var pool = named.Where(e => !e.Tags.Contains("korean")).ToList();
            if (pool.Count == 0) pool = named;
            if (pool.Count == 0) pool = byPitch.Where(e => !Special(e)).ToList();
            if (pool.Count == 0) pool = byPitch;
            return await PreferAsync(pool.Select(e => ((string?)null, (string?)e.ProductId.ToString(), e.Number is { Length: >= 3 } n ? n[..3] : null, (string?)null, (string?)null, e.Name)),
                item.Foiling, item.Edition, ct);
        }

        private async Task<Resolved?> PreferAsync(IEnumerable<(string? Id, string? Product, string? Set, string? Foil, string? Edition, string Name)> rows, string? wantFoil, string? wantEdition, CancellationToken ct)
        {
            var list = rows.ToList();
            if (list.Count == 0) return null;

            // Same rule as MarketPrice: when the post leaves the printing open, assume the cheapest matching one,
            // so an honest ask is never flagged against a pricier reprint or extended art.
            var productIds = list.Select(r => int.TryParse(r.Product, out var p) ? p : 0).Where(p => p != 0).Distinct().ToList();
            var markets = new Dictionary<int, List<(string SubType, decimal? Market)>>();
            if (productIds.Count > 1)
            {
                var latest = await _db.FabPriceSnapshots.AsNoTracking().MaxAsync(s => (DateOnly?)s.SnapshotDate, ct);
                var snaps = await _db.FabPriceSnapshots.AsNoTracking()
                    .Where(s => s.SnapshotDate == latest && productIds.Contains(s.ProductId))
                    .Select(s => new { s.ProductId, s.SubType, s.Market })
                    .ToListAsync(ct);
                markets = snaps.GroupBy(s => s.ProductId).ToDictionary(g => g.Key, g => g.Select(s => (s.SubType, s.Market)).ToList());
            }
            decimal MarketOf((string? Id, string? Product, string? Set, string? Foil, string? Edition, string Name) r) =>
                int.TryParse(r.Product, out var p) && markets.TryGetValue(p, out var m) && MarketPrice.ForFoil(m, wantFoil ?? r.Foil ?? "S") is decimal v ? v : decimal.MaxValue;

            var ordered = list
                .OrderByDescending(r => wantFoil != null && string.Equals(r.Foil ?? "S", wantFoil, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(r => wantEdition != null && r.Edition != null && r.Edition.StartsWith(wantEdition, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(r => !string.IsNullOrEmpty(r.Product))
                .ThenBy(MarketOf)
                .ThenBy(r => (r.Foil ?? "S") == "S" ? 0 : 1)
                .First();
            int? productId = int.TryParse(ordered.Product, out var pid) ? pid : null;
            return new Resolved(ordered.Id, productId, ordered.Name, ordered.Set, ordered.Foil);
        }

        private Task<Dictionary<string, List<IndexEntry>>> GetNameIndexAsync(CancellationToken ct) =>
            _cache.GetOrCreateAsync(NameIndexKey, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
                var names = await _db.FabCards.AsNoTracking().Select(c => new { c.UniqueId, c.Name, c.Pitch }).ToListAsync(ct);
                var dict = new Dictionary<string, List<IndexEntry>>(names.Count);
                foreach (var n in names)
                {
                    var key = Normalize(n.Name);
                    if (key.Length < 3) continue;
                    if (!dict.TryGetValue(key, out var list)) dict[key] = list = new List<IndexEntry>();
                    list.Add(new IndexEntry(n.UniqueId, n.Pitch));
                }
                return dict;
            })!;

        private Task<Dictionary<string, List<ProductEntry>>> GetProductIndexAsync(CancellationToken ct) =>
            _cache.GetOrCreateAsync(ProductIndexKey, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
                var rows = await _db.FabTcgProducts.AsNoTracking().Where(p => p.IsSingle).Select(p => new { p.ProductId, p.Name, p.Number }).ToListAsync(ct);
                var dict = new Dictionary<string, List<ProductEntry>>();
                foreach (var r in rows)
                {
                    var tags = string.Join(" | ", ParenRx.Matches(r.Name).Select(m => m.Groups[1].Value.ToLowerInvariant()));
                    var baseName = NumberSuffixRx.Replace(ParenRx.Replace(r.Name, " "), "").Trim();
                    var key = Normalize(baseName);
                    if (key.Length < 3) continue;
                    var pitch = Regex.IsMatch(tags, @"\bred\b") ? "1" : Regex.IsMatch(tags, @"\byellow\b") ? "2" : Regex.IsMatch(tags, @"\bblue\b") ? "3" : null;
                    if (!dict.TryGetValue(key, out var list)) dict[key] = list = new List<ProductEntry>();
                    list.Add(new ProductEntry(r.ProductId, baseName, r.Number, pitch, tags));
                }
                return dict;
            })!;

        public static string Normalize(string s)
        {
            var folded = s.Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder(folded.Length);
            foreach (var ch in folded)
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
                    sb.Append(char.ToLowerInvariant(ch));
            return NonAlnum.Replace(sb.ToString(), "");
        }
    }
}
