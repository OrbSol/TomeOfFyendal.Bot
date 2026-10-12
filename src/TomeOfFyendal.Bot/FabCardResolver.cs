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
    /// posts that shorten long names. The name index is small and cached an hour.
    /// </summary>
    public class FabCardResolver
    {
        private const string NameIndexKey = "fabmarket:name-index";
        private static readonly Regex NonAlnum = new(@"[^a-z0-9]+", RegexOptions.Compiled);

        private readonly BotDbContext _db;
        private readonly IMemoryCache _cache;

        public FabCardResolver(BotDbContext db, IMemoryCache cache)
        {
            _db = db;
            _cache = cache;
        }

        public sealed record Resolved(string PrintingUniqueId, int? ProductId, string CardName, string? SetId, string? Foiling);
        private sealed record IndexEntry(string UniqueId, string? Pitch);

        public async Task<Resolved?> ResolveAsync(ParsedItem item, CancellationToken ct)
        {
            if (item.Number is not null)
            {
                var byNumber = await _db.FabPrintings.AsNoTracking()
                    .Where(p => p.CardId == item.Number && p.Language == "en" && !p.IsBackFace)
                    .Select(p => new { p.PrintingUniqueId, p.TcgPlayerProductId, p.SetId, p.Foiling, p.Edition, Name = p.Card!.Name })
                    .ToListAsync(ct);
                var pick = Prefer(byNumber.Select(p => (p.PrintingUniqueId, p.TcgPlayerProductId, p.SetId, p.Foiling, p.Edition, p.Name)), item.Foiling, item.Edition);
                if (pick is not null) return pick;
            }

            var key = Normalize(item.CardName);
            if (key.Length < 3) return null;
            var index = await GetNameIndexAsync(ct);

            if (!index.TryGetValue(key, out var entries))
            {
                // Prefix fallback: "Fyendal's Spring" -> "fyendalsspringtunic"; longest match wins.
                // Both sides need 6+ characters, or a short fragment would match some long name.
                var best = key.Length < 6 ? null
                    : index.Keys.Where(k => k.Length >= 6 && (k.StartsWith(key) || key.StartsWith(k))).OrderByDescending(k => k.Length).FirstOrDefault();
                if (best is null) return null;
                entries = index[best];
            }

            var cardUniqueId = entries.Count == 1 ? entries[0].UniqueId
                : (entries.FirstOrDefault(e => item.Pitch != null && e.Pitch == item.Pitch) ?? entries[0]).UniqueId;

            var printings = await _db.FabPrintings.AsNoTracking()
                .Where(p => p.CardUniqueId == cardUniqueId && p.Language == "en" && !p.IsBackFace)
                .Select(p => new { p.PrintingUniqueId, p.TcgPlayerProductId, p.SetId, p.Foiling, p.Edition, Name = p.Card!.Name })
                .ToListAsync(ct);

            var scoped = item.SetCode is null ? printings : printings.Where(p => string.Equals(p.SetId, item.SetCode, StringComparison.OrdinalIgnoreCase)).ToList();
            if (scoped.Count == 0) scoped = printings;
            return Prefer(scoped.Select(p => (p.PrintingUniqueId, p.TcgPlayerProductId, p.SetId, p.Foiling, p.Edition, p.Name)), item.Foiling, item.Edition);
        }

        private static Resolved? Prefer(IEnumerable<(string Id, string? Product, string? Set, string? Foil, string? Edition, string Name)> rows, string? wantFoil, string? wantEdition)
        {
            var list = rows.ToList();
            if (list.Count == 0) return null;
            var ordered = list
                .OrderByDescending(r => wantFoil != null && string.Equals(r.Foil ?? "S", wantFoil, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(r => wantEdition != null && r.Edition != null && r.Edition.StartsWith(wantEdition, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(r => !string.IsNullOrEmpty(r.Product))
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
