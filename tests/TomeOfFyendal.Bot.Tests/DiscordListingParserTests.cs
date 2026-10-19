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

        // ---- Shapes seen in the real marketplace channels (re-typed, prices changed) ----

        [Fact]
        public void Thousands_separators_are_not_decimals()
        {
            var p = DiscordListingParser.Parse("WTS OBO\nGem Mint 10 CF Grasp of the Arknight $1,850\nMarvel Malice $2,300.50");

            Assert.Equal(1850m, p.Items[0].Price);
            Assert.Equal("Grasp of the Arknight", p.Items[0].CardName);
            Assert.Equal("C", p.Items[0].Foiling);
            Assert.Equal("Graded", p.Items[0].Variant);
            Assert.Equal(2300.50m, p.Items[1].Price);
            Assert.Equal("Marvel", p.Items[1].Variant);
        }

        [Fact]
        public void Capital_EA_is_extended_art_and_lowercase_ea_is_each()
        {
            var p = DiscordListingParser.Parse("WTS:\nEA CF Flowstate Embodiment - 75usd per\n3x Snatch (red) $2 ea");

            Assert.Equal(2, p.Items.Count);
            Assert.Equal("Flowstate Embodiment", p.Items[0].CardName);
            Assert.Equal("EA", p.Items[0].Variant);
            Assert.Equal(75m, p.Items[0].Price);
            Assert.Equal("Snatch", p.Items[1].CardName);
            Assert.Null(p.Items[1].Variant);
            Assert.Equal(3, p.Items[1].Qty);
        }

        [Fact]
        public void Shipping_region_before_the_card_does_not_eat_the_line()
        {
            var item = Assert.Single(DiscordListingParser.Parse("WTS CONUS marvel soul harvest - $90 w/tracked shipping").Items);

            Assert.Equal("soul harvest", item.CardName);
            Assert.Equal("Marvel", item.Variant);
            Assert.Equal(90m, item.Price);
        }

        [Fact]
        public void Header_lines_with_terms_only_add_no_cards()
        {
            var p = DiscordListingParser.Parse("WTS CONUS 85% TCGP\nBeckon Steel x2");

            Assert.Equal(ListingType.WTS, p.Type);
            var item = Assert.Single(p.Items);
            Assert.Equal("Beckon Steel", item.CardName);
            Assert.Equal(2, item.Qty);
            Assert.Equal(85, p.MarketPercent);
        }

        [Theory]
        [InlineData("WTS 90% of TCGplayer\nBeckon Steel", 90)]
        [InlineData("WTS 80% market, shipped\nBeckon Steel", 80)]
        [InlineData("WTS\nBeckon Steel $5 (50% off)", null)]
        public void Percent_of_TCGplayer_is_read_from_the_post(string post, int? expected)
        {
            Assert.Equal(expected, DiscordListingParser.Parse(post).MarketPercent);
        }

        [Theory]
        [InlineData("WTB Censor x3 $5ea", 3, 5, "USD")]
        [InlineData("WTS Censor x2 €6 each", 2, 6, "EUR")]
        [InlineData("WTS Censor 3x $5", 3, 5, "USD")]
        [InlineData("WTS Censor x3 5$", 3, 5, "USD")]
        public void Quantity_before_a_symbol_price_is_not_read_as_the_price(string post, int qty, decimal price, string currency)
        {
            var item = Assert.Single(DiscordListingParser.Parse(post).Items);
            Assert.Equal("Censor", item.CardName);
            Assert.Equal((qty, price, currency), (item.Qty, item.Price, item.Currency));
        }

        [Fact]
        public void Percent_of_TCG_Low_on_the_card_line()
        {
            var p = DiscordListingParser.Parse("WTS Tome of Fyendal 90% TCGLOW");

            Assert.Equal(ListingType.WTS, p.Type);
            Assert.Equal("Tome of Fyendal", Assert.Single(p.Items).CardName);
            Assert.Equal(90, p.MarketPercent);
            Assert.Equal("low", p.PercentBasis);
        }

        [Theory]
        [InlineData("WTS 90% TCG low\nBeckon Steel", "low")]
        [InlineData("WTS 85% TCGL\nBeckon Steel", "low")]
        [InlineData("WTS 80% of low\nBeckon Steel", "low")]
        [InlineData("WTS CONUS 85% TCGP\nBeckon Steel", null)]
        [InlineData("WTS 90% TCG\nBeckon Steel", null)]
        public void Percent_basis_is_low_only_when_the_post_says_low(string post, string? basis)
        {
            Assert.Equal(basis, DiscordListingParser.Parse(post).PercentBasis);
        }

        [Fact]
        public void Section_header_sets_finish_and_edition_until_the_next_one()
        {
            var p = DiscordListingParser.Parse("WTS\nRF 1st ed:\nReduce to Runechant (Yel) - 5usd per\nOthers:\nArcane Lens $10");

            Assert.Equal(2, p.Items.Count);
            Assert.Equal("Reduce to Runechant", p.Items[0].CardName);
            Assert.Equal(("R", "1st", "2"), (p.Items[0].Foiling, p.Items[0].Edition, p.Items[0].Pitch));
            Assert.Equal(5m, p.Items[0].Price);
            Assert.Null(p.Items[1].Foiling);
            Assert.Null(p.Items[1].Edition);
        }

        [Fact]
        public void Struck_through_and_sold_lines_are_skipped()
        {
            var p = DiscordListingParser.Parse("WTS\n~~CF Shove Off $40~~ SOLD\nNullrune Boots $200\nSpellbane Aegis - sold");

            Assert.Equal("Nullrune Boots", Assert.Single(p.Items).CardName);
        }

        [Fact]
        public void A_bare_number_before_OBO_is_the_price()
        {
            var item = Assert.Single(DiscordListingParser.Parse("WTS\nCrown of Dominion 1500 OBO").Items);

            Assert.Equal("Crown of Dominion", item.CardName);
            Assert.Equal(1500m, item.Price);
        }

        [Fact]
        public void A_finish_line_with_a_note_is_a_section_header()
        {
            var p = DiscordListingParser.Parse("WTS\nGold Foils ( open to trades for other gold foils )\nCrown of Dominion 1500 OBO");

            var item = Assert.Single(p.Items);
            Assert.Equal("Crown of Dominion", item.CardName);
            Assert.Equal("G", item.Foiling);
        }

        [Fact]
        public void Pitch_before_a_colon_and_euro_decimals_are_read()
        {
            var p = DiscordListingParser.Parse("WTS:\n• Staunch Response red Marvel: 70€\n• Waning Moon CF: 6,5€");

            Assert.Equal(("Staunch Response", "1", "Marvel"), (p.Items[0].CardName, p.Items[0].Pitch, p.Items[0].Variant));
            Assert.Equal((6.5m, "EUR"), (p.Items[1].Price, p.Items[1].Currency));
        }

        [Fact]
        public void Grade_in_brackets_is_a_variant_not_part_of_the_name()
        {
            var item = Assert.Single(DiscordListingParser.Parse("WTS\nGF Aether Ironweave (psa 9)").Items);

            Assert.Equal("Aether Ironweave", item.CardName);
            Assert.Equal("G", item.Foiling);
            Assert.Equal("Graded", item.Variant);
        }

        [Theory]
        [InlineData("bump", true)]
        [InlineData("Bump!", true)]
        [InlineData("bumping :)", true)]
        [InlineData("BUMPED", true)]
        [InlineData("bumper sleeves for sale?", false)]
        [InlineData("Is this still available? I'd love to bump into you at the next event and trade", false)] // a conversation
        [InlineData("", false)]
        [InlineData(null, false)]
        public void A_short_bump_reply_is_a_bump(string? reply, bool bump) =>
            Assert.Equal(bump, DiscordListingParser.IsBump(reply));

        [Fact]
        public void A_price_line_with_each_prices_the_cards_above_it_in_the_same_paragraph()
        {
            var p = DiscordListingParser.Parse("WTB\nGF Dynastic Diadem\nGF Obsidian Fire Vein\n250€ each\n\nCF War - 30€");
            Assert.All(p.Items.Take(2), i => Assert.Equal((250m, "EUR"), (i.Price!.Value, i.Currency)));
            Assert.Equal(30m, p.Items[2].Price);

            var apart = DiscordListingParser.Parse("WTS\nCF Soulshackle FAB120\n\n2 signed artist proofs\n$15 each");
            Assert.Null(apart.Items[0].Price);
        }

        [Fact]
        public void A_price_line_without_each_is_not_shared()
        {
            var p = DiscordListingParser.Parse("WTS\nSnatch (red)\n$5 shipping");
            Assert.Null(Assert.Single(p.Items).Price);
        }

        [Theory]
        [InlineData("WTB\n3x deadly spinneret 8", "deadly spinneret", 3, 8)]
        [InlineData("WTB\n1x flick knives ea 8", "flick knives", 1, 8)]
        [InlineData("WTB\n1x rf grasp of the arknight 70", "grasp of the arknight", 1, 70)]
        public void A_bare_number_ending_the_line_is_the_price(string post, string name, int qty, int price)
        {
            var item = Assert.Single(DiscordListingParser.Parse(post).Items);
            Assert.Equal((name, qty, (decimal?)price), (item.CardName, item.Qty, item.Price));
        }

        [Fact]
        public void A_set_number_is_not_a_bare_price()
        {
            var item = Assert.Single(DiscordListingParser.Parse("WTS\n3x EA deadwood dirge red LGS 363").Items);
            Assert.Equal("LGS363", item.Number);
            Assert.Null(item.Price);
        }

        [Theory]
        [InlineData("WTS\nRF Cull playset $160", 3, "53.33")]
        [InlineData("WTS\nRF EA Widespread Ruin playset $150", 3, "50")] // EA is extended art, not "each"
        [InlineData("WTS\nRF Runic Reaving playset $65 each", 3, "65")]
        public void A_playset_is_three_copies_priced_per_copy(string post, int qty, string price)
        {
            var item = Assert.Single(DiscordListingParser.Parse(post).Items);
            Assert.Equal((qty, (decimal?)decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture)), (item.Qty, item.Price));
        }

        [Fact]
        public void A_comma_name_keeps_its_full_form_for_the_resolver()
        {
            var item = Assert.Single(DiscordListingParser.Parse("WTS\nArakni, Redback (Marvel) - HNT - $16").Items);
            Assert.Equal("Arakni", item.CardName);
            Assert.Equal("Arakni, Redback", item.FullName);
            Assert.Equal("Marvel", item.Variant);
        }

        [Theory]
        [InlineData("WTS\nCorrupted Crown CF - GEM Pack 6 - $30", "Corrupted Crown", null)]
        [InlineData("WTB\nMV Fealty - 100€", "Fealty", "Marvel")]
        public void Pack_labels_and_MV_are_not_part_of_the_name(string post, string name, string? variant)
        {
            var item = Assert.Single(DiscordListingParser.Parse(post).Items);
            Assert.Equal((name, variant), (item.CardName, item.Variant));
        }
    }
}
