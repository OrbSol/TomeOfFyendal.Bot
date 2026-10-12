using TomeOfFyendal.Bot;
using Xunit;

namespace TomeOfFyendal.Bot.Tests
{
    /// <summary>Real-world post shapes from marketplace channels, and what must come out of each.</summary>
    public class DiscordListingParserTests
    {
        [Fact]
        public void Classic_sell_post_reads_each_card_and_drops_footer_chatter()
        {
            var p = DiscordListingParser.Parse(
                "WTS\nFyendal's Spring Tunic CRU178 NM $92 shipped\nCommand and Conquer RF LP - $45\n2x Snatch (red) $3 each\nPayPal G&S only, US shipping");

            Assert.Equal(ListingType.WTS, p.Type);
            Assert.Equal(3, p.Items.Count);

            var tunic = p.Items[0];
            Assert.Equal("Fyendal's Spring Tunic", tunic.CardName);
            Assert.Equal("CRU178", tunic.Number);
            Assert.Equal("NM", tunic.Condition);
            Assert.Equal(92m, tunic.Price);

            Assert.Equal("Command and Conquer", p.Items[1].CardName);
            Assert.Equal("R", p.Items[1].Foiling);
            Assert.Equal("LP", p.Items[1].Condition);

            var snatch = p.Items[2];
            Assert.Equal("Snatch", snatch.CardName);
            Assert.Equal(2, snatch.Qty);
            Assert.Equal("1", snatch.Pitch);
        }

        [Fact]
        public void Buy_post_splits_sentences_and_strips_paying_and_too()
        {
            var p = DiscordListingParser.Parse("WTB: Arknight Shard CF, paying $650. LF Metacarpus Node cold foil too.");

            Assert.Equal(ListingType.WTB, p.Type);
            Assert.Equal(new[] { "Arknight Shard", "Metacarpus Node" }, p.Items.Select(i => i.CardName));
            Assert.All(p.Items, i => Assert.Equal("C", i.Foiling));
            Assert.Equal(650m, p.Items[0].Price);
        }

        [Fact]
        public void Have_want_lines_carry_their_own_type()
        {
            var p = DiscordListingParser.Parse("H: Skullhorn CF NM $190 | Breeze Rider Boots cf $95\nW: Courage of Bladehold");

            Assert.Equal(ListingType.WTS, p.Items[0].LineType);
            Assert.Equal(ListingType.WTS, p.Items[1].LineType);
            Assert.Equal(ListingType.WTB, p.Items[2].LineType);
            Assert.Equal("Courage of Bladehold", p.Items[2].CardName);
        }

        [Fact]
        public void Prose_post_yields_no_items()
        {
            var p = DiscordListingParser.Parse("hey everyone, anyone going to the armory this weekend? looking to trade some stuff and maybe sell a few cards after");
            Assert.True(p.IsEmpty);
        }

        [Fact]
        public void Markdown_and_payment_words_are_handled()
        {
            var p = DiscordListingParser.Parse("**WTS** __Cintari Saber__ (Reverse) 1st ed cold foil **$95** obo, pics on request, venmo ok");

            Assert.True(p.HasOffPlatformPayment);
            var item = Assert.Single(p.Items);
            Assert.Equal("Cintari Saber (Reverse)", item.CardName);
            Assert.Equal("1st", item.Edition);
            Assert.Equal(95m, item.Price);
        }

        [Fact]
        public void Euro_prices_keep_their_currency()
        {
            var p = DiscordListingParser.Parse("FS Enlightened Strike RF 25€ , Art of War 18 usd");
            var item = p.Items[0];
            Assert.Equal("Enlightened Strike", item.CardName);
            Assert.Equal(25m, item.Price);
            Assert.Equal("EUR", item.Currency);
        }

        [Fact]
        public void Header_with_a_note_is_not_read_as_a_card()
        {
            var p = DiscordListingParser.Parse("WTS (all NM, shipped CONUS)\n- Heart of Fyendal CF $380\n- 3x Enlightened Strike (blue) RF $12 ea\n- Crown of Providence WTR 1st $25 obo");

            Assert.Equal(3, p.Items.Count);
            Assert.Equal("Heart of Fyendal", p.Items[0].CardName);
            Assert.Equal(3, p.Items[1].Qty);
            Assert.Equal("3", p.Items[1].Pitch);
            Assert.Equal("Crown of Providence", p.Items[2].CardName);
            Assert.Equal("WTR", p.Items[2].SetCode);
        }

        [Fact]
        public void Mentions_and_links_are_removed()
        {
            var p = DiscordListingParser.Parse("<@123456> wtb Eye of Ophidia CF 1st edition, paying $1200 https://example.com/x");
            var item = Assert.Single(p.Items);
            Assert.Equal("Eye of Ophidia", item.CardName);
            Assert.Equal(1200m, item.Price);
        }

        [Fact]
        public void Colour_inside_a_card_name_is_not_taken_as_pitch()
        {
            var p = DiscordListingParser.Parse("WTS Red in the Ledger (blue) NM $4 each");
            var item = Assert.Single(p.Items);
            Assert.Equal("Red in the Ledger", item.CardName);
            Assert.Equal("3", item.Pitch);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Empty_input_is_empty(string? text)
        {
            Assert.True(DiscordListingParser.Parse(text).IsEmpty);
        }

        [Fact]
        public void Very_long_post_is_capped()
        {
            var lines = string.Join("\n", Enumerable.Range(1, 200).Select(i => $"Card Number {i} $5"));
            var p = DiscordListingParser.Parse("WTS\n" + lines);
            Assert.True(p.Items.Count <= 40);
        }

        [Fact]
        public void Summary_never_quotes_the_post()
        {
            var p = DiscordListingParser.Parse("WTS\nHeart of Fyendal CF $380\nSnatch (red) $3");
            Assert.Equal("WTS · 2 cards · $3 to $380", DiscordListingParser.Summarize(p.Type, p.Items));
        }
    }
}
