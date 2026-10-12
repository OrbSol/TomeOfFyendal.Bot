using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TomeOfFyendal.Bot
{
    public enum ListingType { Unknown, WTS, WTB, WTT }

    public sealed record ParsedItem(
        string CardName, string? SetCode, string? Number, string? Foiling, string? Condition, string? Pitch, string? Edition,
        decimal? Price, string Currency, int Qty, byte Confidence, ListingType? LineType);

    public sealed record ParsedListing(ListingType Type, IReadOnlyList<ParsedItem> Items, bool HasOffPlatformPayment)
    {
        public bool IsEmpty => Type == ListingType.Unknown || Items.Count == 0;
    }

    /// <summary>
    /// Pure, dependency-free parser for marketplace posts. Posts are semi-structured:
    /// a WTS/WTB header, then one card per line with optional set+number, foiling,
    /// condition, pitch colour, quantity and a price. Card names are resolved against
    /// the catalog later by <see cref="FabCardResolver"/>; this class only extracts fields.
    /// </summary>
    public static class DiscordListingParser
    {
        private static readonly RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

        private static readonly Regex Wts = new(@"(?<![a-z])(wts|fs|for sale|selling|h:|have:|haves?\b)(?![a-z])", Opt);
        private static readonly Regex Wtb = new(@"(?<![a-z])(wtb|lf|looking for|buying|iso|w:|want:|wants?\b)(?![a-z])", Opt);
        private static readonly Regex Wtt = new(@"(?<![a-z])(wtt|ft|for trade|trading|trade)(?![a-z])", Opt);

        private static readonly Regex LineHeadRx = new(@"^\s*(?<h>wts|wtb|wtt|fs|lf|ft|iso|h|w|have|want|haves|wants|selling|buying|for sale|looking for|trade|for trade)\s*[:\-–]?\s+", Opt);
        private static readonly Regex PriceRx = new(@"(?:\$\s?(?<usd>\d{1,5}(?:[.,]\d{1,2})?))|(?:(?<usd2>\d{1,5}(?:[.,]\d{1,2})?)\s?(?:usd|dollars|bucks|\$))|(?:€\s?(?<eur>\d{1,5}(?:[.,]\d{1,2})?))|(?:(?<eur2>\d{1,5}(?:[.,]\d{1,2})?)\s?(?:€|eur|euros?))|(?:£\s?(?<gbp>\d{1,5}(?:[.,]\d{1,2})?))|(?:(?<gbp2>\d{1,5}(?:[.,]\d{1,2})?)\s?(?:£|gbp))", Opt);
        private static readonly Regex QtyRx = new(@"(?:(?<![a-z\d])(?<a>\d{1,2})\s?x(?![a-z]))|(?:(?<![a-z])x\s?(?<b>\d{1,2})(?!\d))", Opt);
        private static readonly Regex FoilRx = new(@"(?<![a-z])(cold[\s-]?foil|cf|rainbow[\s-]?foil|rf|gold[\s-]?foil|gf|non[\s-]?foil|nf|regular|standard)(?![a-z])", Opt);
        private static readonly Regex CondRx = new(@"(?<![a-z])(nm|near[\s-]?mint|lp|lightly[\s-]?played|mp|moderately[\s-]?played|hp|heavily[\s-]?played|dmg|damaged)(?![a-z])", Opt);
        // Pitch colour only counts when bracketed "(red)" or trailing the name "Snatch red"; a colour
        // inside a name ("Red in the Ledger") is left alone.
        private static readonly Regex PitchParenRx = new(@"[\(\[]\s*(?<p>red|yellow|blue)\s*[\)\]]", Opt);
        private static readonly Regex PitchTailRx = new(@"(?<=\s[-–]?\s?)(?<p>red|yellow|blue)\s*$", Opt);
        // Bare set code without a number ("Crown of Providence WTR"): three capitals, case-sensitive.
        private static readonly Regex BareSetRx = new(@"(?<![A-Za-z\d])(?<set>[A-Z]{3})(?![A-Za-z\d])", RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex EditionRx = new(@"(?<![a-z])(1st\s?(?:ed(?:ition)?)?|first\s?ed(?:ition)?|unlimited|unl|alpha)(?![a-z])", Opt);
        private static readonly Regex SetNumRx = new(@"(?<![a-z])(?<set>[A-Z]{3})\s?-?\s?(?<num>\d{3})(?<suffix>[a-z]?)(?![a-z\d])", Opt);
        private static readonly Regex UrlRx = new(@"https?://\S+", Opt);
        private static readonly Regex PaymentRx = new(@"(?<![a-z])(venmo|cash\s?app|cashapp|zelle|paypal\.me|f&f|friends\s*(?:and|&)\s*family)(?![a-z])", Opt);
        private static readonly Regex MentionRx = new(@"<@!?\d+>|<#\d+>|<a?:\w+:\d+>|@everyone|@here", Opt);
        private static readonly Regex MarkdownRx = new(@"[*_`~>#|]+", Opt);
        private static readonly Regex LeadBulletRx = new(@"^\s*(?:[-•·*]|\d+[.)])\s*", Opt);
        private static readonly Regex SpaceRx = new(@"\s+", Opt);
        private static readonly Regex TrailingJunkRx = new(@"^[\s\-–—:,.;|/\\\[\]]+|[\s\-–—:,.;|/\\\[\]]+$", Opt);
        // A header word on its own, optionally followed by a bracketed note: "WTS", "H:", "WTS (all NM, shipped CONUS)".
        private static readonly Regex HeaderOnlyRx = new(@"^(?<h>wts|wtb|wtt|fs|lf|ft|h|w|have|want|selling|buying|for sale|looking for|trade)[\s:!.-]*(?:\(.*\))?[\s:!.,-]*$", Opt);
        // Everything from the first noise phrase to the end of the line is not part of a card name.
        private static readonly Regex NoiseRx = new(@"(?<![a-z])(shipped|shipping|ship|each|ea|obo|o\.b\.o\.?|or best offer|firm|pics?|photos?|paying|plus shipping|\+\s?shipping|us only|usa only|conus|g&s|f&f|paypal|venmo|cashapp|cash app|zelle|only|dm me|pm me|taking offers|offers?|too|also|as well|or trade|for trade)(?![a-z]).*$", Opt);
        private static readonly Regex AfterCommaRx = new(@"\s*,.*$", Opt);

        public static ParsedListing Parse(string? content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return new ParsedListing(ListingType.Unknown, Array.Empty<ParsedItem>(), false);

            var text = MentionRx.Replace(content, " ");
            var hasPayment = PaymentRx.IsMatch(text);
            text = UrlRx.Replace(text, " ");

            var type = DetectType(text);
            var items = new List<ParsedItem>();
            ListingType? section = null;

            foreach (var rawLine in SplitLines(text))
            {
                var line = Clean(rawLine);
                if (line.Length < 3) continue;
                var ho = HeaderOnlyRx.Match(line);
                if (ho.Success) { section = HeaderType(ho.Groups["h"].Value); continue; }

                var lineType = section;
                var hm = LineHeadRx.Match(line);
                if (hm.Success) { lineType = HeaderType(hm.Groups["h"].Value); line = line[hm.Length..]; if (section is null) section = lineType; }

                var item = ParseLine(line, lineType);
                if (item is not null) items.Add(item);
                if (items.Count >= 40) break;
            }

            if (type == ListingType.Unknown && items.Any(i => i.LineType.HasValue))
                type = items.First(i => i.LineType.HasValue).LineType!.Value;

            return new ParsedListing(type, items, hasPayment);
        }

        public static string? NormalizeFoil(string? raw)
        {
            if (raw is null) return null;
            var f = raw.ToLowerInvariant().Replace(" ", "").Replace("-", "");
            return f switch
            {
                "cf" or "coldfoil" => "C",
                "rf" or "rainbowfoil" => "R",
                "gf" or "goldfoil" => "G",
                "nf" or "nonfoil" or "regular" or "standard" => "S",
                _ => null,
            };
        }

        public static string? NormalizeCondition(string? raw)
        {
            if (raw is null) return null;
            var c = raw.ToLowerInvariant().Replace(" ", "").Replace("-", "");
            return c switch
            {
                "nm" or "nearmint" => "NM",
                "lp" or "lightlyplayed" => "LP",
                "mp" or "moderatelyplayed" => "MP",
                "hp" or "heavilyplayed" => "HP",
                "dmg" or "damaged" => "DMG",
                _ => null,
            };
        }

        private static ListingType? HeaderType(string h)
        {
            var k = h.Trim().TrimEnd(':').ToLowerInvariant();
            return k switch
            {
                "wts" or "fs" or "for sale" or "selling" or "h" or "have" or "haves" => ListingType.WTS,
                "wtb" or "lf" or "looking for" or "buying" or "iso" or "w" or "want" or "wants" => ListingType.WTB,
                "wtt" or "ft" or "for trade" or "trade" or "trading" => ListingType.WTT,
                _ => null,
            };
        }

        private static ListingType DetectType(string text)
        {
            var head = text.Length > 400 ? text[..400] : text;
            var s = Wts.Match(head); var b = Wtb.Match(head); var t = Wtt.Match(head);
            var best = ListingType.Unknown; var pos = int.MaxValue;
            if (s.Success && s.Index < pos) { best = ListingType.WTS; pos = s.Index; }
            if (b.Success && b.Index < pos) { best = ListingType.WTB; pos = b.Index; }
            if (t.Success && t.Index < pos) { best = ListingType.WTT; }
            if (best == ListingType.Unknown && PriceRx.IsMatch(text)) best = ListingType.WTS;
            return best;
        }

        private static IEnumerable<string> SplitLines(string text)
        {
            foreach (var line in text.Split('\n'))
                foreach (var part in line.Split(new[] { " | ", " // ", "; ", ". " }, StringSplitOptions.RemoveEmptyEntries))
                    yield return part;
        }

        private static string Clean(string line)
        {
            var s = MarkdownRx.Replace(line, " ");
            s = LeadBulletRx.Replace(s, "");
            s = SpaceRx.Replace(s, " ").Trim();
            return s;
        }

        private static ParsedItem? ParseLine(string line, ListingType? lineType)
        {
            var work = line;
            byte confidence = 0;

            decimal? price = null; var currency = "USD";
            var pm = PriceRx.Match(work);
            if (pm.Success)
            {
                string raw;
                if (pm.Groups["usd"].Success) raw = pm.Groups["usd"].Value;
                else if (pm.Groups["usd2"].Success) raw = pm.Groups["usd2"].Value;
                else if (pm.Groups["eur"].Success) { raw = pm.Groups["eur"].Value; currency = "EUR"; }
                else if (pm.Groups["eur2"].Success) { raw = pm.Groups["eur2"].Value; currency = "EUR"; }
                else if (pm.Groups["gbp"].Success) { raw = pm.Groups["gbp"].Value; currency = "GBP"; }
                else { raw = pm.Groups["gbp2"].Value; currency = "GBP"; }
                if (decimal.TryParse(raw.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var p) && p > 0 && p < 100000)
                {
                    price = decimal.Round(p, 2);
                    confidence += 25;
                }
                work = work.Remove(pm.Index, pm.Length).Insert(pm.Index, " ");
            }

            var qty = 1;
            var qm = QtyRx.Match(work);
            if (qm.Success)
            {
                var raw = qm.Groups["a"].Success ? qm.Groups["a"].Value : qm.Groups["b"].Value;
                if (int.TryParse(raw, out var q) && q is > 0 and < 100) qty = q;
                work = work.Remove(qm.Index, qm.Length).Insert(qm.Index, " ");
            }

            string? foil = null;
            var fm = FoilRx.Match(work);
            if (fm.Success) { foil = NormalizeFoil(fm.Groups[1].Value); if (foil != null) confidence += 10; work = work.Remove(fm.Index, fm.Length).Insert(fm.Index, " "); }

            string? cond = null;
            var cm = CondRx.Match(work);
            if (cm.Success) { cond = NormalizeCondition(cm.Groups[1].Value); if (cond != null) confidence += 10; work = work.Remove(cm.Index, cm.Length).Insert(cm.Index, " "); }

            string? edition = null;
            var em = EditionRx.Match(work);
            if (em.Success)
            {
                var e = em.Groups[1].Value.ToLowerInvariant();
                edition = e.StartsWith("1st") || e.StartsWith("first") ? "1st" : e.StartsWith("unl") ? "Unlimited" : "Alpha";
                work = work.Remove(em.Index, em.Length).Insert(em.Index, " ");
            }

            string? set = null, number = null;
            var sm = SetNumRx.Match(work);
            if (sm.Success)
            {
                set = sm.Groups["set"].Value.ToUpperInvariant();
                number = set + sm.Groups["num"].Value + sm.Groups["suffix"].Value.ToLowerInvariant();
                confidence += 15;
                work = work.Remove(sm.Index, sm.Length).Insert(sm.Index, " ");
            }

            // Drop trailing chatter: everything after a noise phrase, and anything after a comma once a price was read.
            work = NoiseRx.Replace(work, "");
            if (price.HasValue) work = AfterCommaRx.Replace(work, "");
            work = SpaceRx.Replace(work, " ").Trim();

            string? pitch = null;
            var ptm = PitchParenRx.Match(work);
            if (!ptm.Success) ptm = PitchTailRx.Match(work);
            if (ptm.Success)
            {
                pitch = ptm.Groups["p"].Value.ToLowerInvariant() switch { "red" => "1", "yellow" => "2", _ => "3" };
                work = work.Remove(ptm.Index, ptm.Length).Insert(ptm.Index, " ");
            }

            if (set is null)
            {
                var bs = BareSetRx.Match(work);
                if (bs.Success) { set = bs.Groups["set"].Value; work = work.Remove(bs.Index, bs.Length).Insert(bs.Index, " "); }
            }

            var name = TrailingJunkRx.Replace(SpaceRx.Replace(work, " "), "").Trim();
            name = BalanceParens(name);
            if (name.Length > 200) name = name[..200];

            if (name.Length < 3 && number is null) return null;
            if (name.Length >= 3) confidence += 40;
            if (price is null && number is null && name.Split(' ').Length > 8) return null; // prose, not a card line

            return new ParsedItem(
                CardName: name.Length >= 3 ? name : number!,
                SetCode: set, Number: number, Foiling: foil, Condition: cond, Pitch: pitch, Edition: edition,
                Price: price, Currency: currency, Qty: qty, Confidence: Math.Min(confidence, (byte)100),
                LineType: lineType);
        }

        /// <summary>"Cintari Saber (Reverse" -> "Cintari Saber (Reverse)"; a lone "(" or ")" is dropped.</summary>
        private static string BalanceParens(string name)
        {
            var open = name.Count(c => c == '('); var close = name.Count(c => c == ')');
            if (open == close) return name.Trim();
            if (open > close && name.LastIndexOf('(') > name.LastIndexOf(')'))
            {
                var tail = name[(name.LastIndexOf('(') + 1)..].Trim();
                return tail.Length == 0 ? name[..name.LastIndexOf('(')].Trim() : name.Trim() + ")";
            }
            return name.Replace("(", "").Replace(")", "").Trim();
        }

        /// <summary>Generated description for the listing row, never the member's own words.</summary>
        public static string Summarize(ListingType type, IReadOnlyList<ParsedItem> items)
        {
            var sb = new StringBuilder();
            sb.Append(type).Append(" · ").Append(items.Count).Append(items.Count == 1 ? " card" : " cards");
            var prices = items.Where(i => i.Price.HasValue && i.Currency == "USD").Select(i => i.Price!.Value).ToList();
            if (prices.Count > 0)
            {
                var lo = prices.Min(); var hi = prices.Max();
                sb.Append(" · ").Append(lo == hi ? $"${lo:0.##}" : $"${lo:0.##} to ${hi:0.##}");
            }
            return sb.ToString();
        }
    }
}
