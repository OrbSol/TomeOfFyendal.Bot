namespace TomeOfFyendal.Bot
{
    /// <summary>
    /// Picks the TCGplayer market price that a listing should be compared against. One TCGplayer
    /// product can carry several finishes (e.g. Rainbow Foil at $50 and Cold Foil at $999), so a
    /// listing is compared only with the finish it names. When the finish is unknown, or several
    /// editions share it, the LOWEST matching market is used, so an honest ask is never reported as
    /// "far under market" because a pricier version exists.
    /// </summary>
    public static class MarketPrice
    {
        /// <summary>Finish code of a TCGplayer sub-type: S standard, R rainbow, C cold, G gold.</summary>
        public static string FoilOf(string? subType)
        {
            if (string.IsNullOrEmpty(subType)) return "S";
            if (subType.Contains("Gold", StringComparison.OrdinalIgnoreCase)) return "G";
            if (subType.Contains("Cold", StringComparison.OrdinalIgnoreCase)) return "C";
            if (subType.Contains("Rainbow", StringComparison.OrdinalIgnoreCase)) return "R";
            return "S";
        }

        /// <summary>Market for <paramref name="foiling"/> (null = any finish), or null when none is priced.</summary>
        public static decimal? ForFoil(IEnumerable<(string SubType, decimal? Market)> rows, string? foiling)
        {
            var priced = rows.Where(r => r.Market is > 0);
            if (!string.IsNullOrEmpty(foiling))
                priced = priced.Where(r => FoilOf(r.SubType) == foiling);
            var list = priced.Select(r => r.Market!.Value).ToList();
            return list.Count == 0 ? null : list.Min();
        }
    }
}
