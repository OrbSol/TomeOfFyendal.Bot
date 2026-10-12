using TomeOfFyendal.Bot;
using Xunit;

namespace TomeOfFyendal.Bot.Tests
{
    public class MarketPriceTests
    {
        // Mask of Momentum: one TCGplayer product, two finishes worth very different amounts.
        private static readonly (string, decimal?)[] Mask =
        {
            ("Unlimited Edition Rainbow Foil", 50.57m),
            ("1st Edition Cold Foil", 999.00m),
        };

        [Theory]
        [InlineData("Unlimited Edition Normal", "S")]
        [InlineData("Normal", "S")]
        [InlineData("1st Edition Rainbow Foil", "R")]
        [InlineData("Cold Foil", "C")]
        [InlineData("Gold Cold Foil", "G")]
        [InlineData(null, "S")]
        public void FoilOf_maps_tcgplayer_subtypes(string? subType, string expected) =>
            Assert.Equal(expected, MarketPrice.FoilOf(subType));

        [Fact]
        public void Rainbow_listing_is_compared_with_the_rainbow_market_not_the_cold_foil()
        {
            Assert.Equal(50.57m, MarketPrice.ForFoil(Mask, "R"));
            Assert.Equal(999.00m, MarketPrice.ForFoil(Mask, "C"));
        }

        [Fact]
        public void Unknown_finish_uses_the_lowest_market_so_honest_asks_are_not_flagged()
        {
            Assert.Equal(50.57m, MarketPrice.ForFoil(Mask, null));
        }

        [Fact]
        public void Finish_with_no_price_returns_null()
        {
            Assert.Null(MarketPrice.ForFoil(Mask, "G"));
            Assert.Null(MarketPrice.ForFoil(new[] { ("Normal", (decimal?)null) }, "S"));
        }

        [Fact]
        public void Several_editions_of_one_finish_use_the_lowest()
        {
            var tunic = new (string, decimal?)[] { ("1st Edition Normal", 104.42m), ("Unlimited Edition Normal", 99.56m) };
            Assert.Equal(99.56m, MarketPrice.ForFoil(tunic, "S"));
        }
    }
}
