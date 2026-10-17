using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using TomeOfFyendal.Bot;
using TomeOfFyendal.Bot.Data;
using Xunit;

namespace TomeOfFyendal.Bot.Tests
{
    public class FabCardResolverTests
    {
        [Fact]
        public async Task Post_without_a_set_resolves_to_the_cheapest_printing_of_that_finish()
        {
            var db = new BotDbContext(new DbContextOptionsBuilder<BotDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            // The pricier Extended Art printing comes first, so picking "the first match" would choose it.
            db.FabCards.Add(new FabCard
            {
                UniqueId = "cnc", Name = "Command and Conquer",
                Printings =
                {
                    new FabPrinting { PrintingUniqueId = "hnt", SetId = "HNT", CardId = "HNT260", Foiling = "R", TcgPlayerProductId = "614533" },
                    new FabPrinting { PrintingUniqueId = "arc", SetId = "ARC", CardId = "ARC159", Foiling = "R", TcgPlayerProductId = "225899" },
                },
            });
            var day = new DateOnly(2026, 10, 12);
            db.FabPriceSnapshots.AddRange(
                new FabPriceSnapshot { ProductId = 614533, SubType = "Rainbow Foil", SnapshotDate = day, Market = 212.05m },
                new FabPriceSnapshot { ProductId = 225899, SubType = "1st Edition Rainbow Foil", SnapshotDate = day, Market = 249.99m },
                new FabPriceSnapshot { ProductId = 225899, SubType = "Unlimited Edition Rainbow Foil", SnapshotDate = day, Market = 54.12m });
            await db.SaveChangesAsync();

            var item = DiscordListingParser.Parse("WTS\nCommand and Conquer RF LP $45").Items.Single();
            var resolved = await new FabCardResolver(db, new MemoryCache(new MemoryCacheOptions())).ResolveAsync(item, default);

            Assert.Equal(225899, resolved?.ProductId);
        }

        [Fact]
        public async Task A_card_the_dataset_lacks_matches_a_TCGplayer_product_and_its_named_version()
        {
            var db = new BotDbContext(new DbContextOptionsBuilder<BotDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            db.FabTcgProducts.AddRange(
                new FabTcgProduct { ProductId = 1, Name = "Deadly Spinneret", Number = "IAR243", IsSingle = true },
                new FabTcgProduct { ProductId = 2, Name = "Deadly Spinneret (Extended Art)", Number = "IAR243", IsSingle = true },
                new FabTcgProduct { ProductId = 3, Name = "Staunch Response (Yellow) (Marvel) - TNP020", Number = "TNP020", IsSingle = true },
                new FabTcgProduct { ProductId = 4, Name = "Staunch Response (Red) (Marvel) - TNP019", Number = "TNP019", IsSingle = true },
                new FabTcgProduct { ProductId = 5, Name = "Staunch Response (Red)", Number = "WTR051", IsSingle = true });
            await db.SaveChangesAsync();
            var resolver = new FabCardResolver(db, new MemoryCache(new MemoryCacheOptions()));

            var ea = await resolver.ResolveAsync(DiscordListingParser.Parse("WTS\nEA RF Deadly Spinneret $40").Items.Single(), default);
            var plain = await resolver.ResolveAsync(DiscordListingParser.Parse("WTS\nDeadly Spinneret $20").Items.Single(), default);
            var marvel = await resolver.ResolveAsync(DiscordListingParser.Parse("WTS\nStaunch Response red Marvel: 70€").Items.Single(), default);

            Assert.Equal(2, ea?.ProductId);
            Assert.Equal(1, plain?.ProductId);
            Assert.Equal(4, marvel?.ProductId);
            Assert.Null(marvel?.PrintingUniqueId);
        }

        [Fact]
        public async Task Leading_words_are_dropped_and_a_dropped_colour_picks_the_pitch()
        {
            var db = new BotDbContext(new DbContextOptionsBuilder<BotDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            db.FabCards.AddRange(
                new FabCard { UniqueId = "valor-red", Name = "Warrior's Valor", Pitch = "1", Printings = { new FabPrinting { PrintingUniqueId = "p-red", SetId = "WTR" } } },
                new FabCard { UniqueId = "valor-yellow", Name = "Warrior's Valor", Pitch = "2", Printings = { new FabPrinting { PrintingUniqueId = "p-yellow", SetId = "WTR" } } });
            await db.SaveChangesAsync();

            var item = DiscordListingParser.Parse("WTS\nTournament pack yellow Warrior's Valor $60").Items.Single();
            var resolved = await new FabCardResolver(db, new MemoryCache(new MemoryCacheOptions())).ResolveAsync(item, default);

            Assert.Equal("p-yellow", resolved?.PrintingUniqueId);
        }

        [Fact]
        public async Task A_named_version_picks_that_printing_and_a_plain_post_the_regular_one()
        {
            var db = new BotDbContext(new DbContextOptionsBuilder<BotDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            db.FabCards.Add(new FabCard
            {
                UniqueId = "lexi", Name = "Lexi",
                Printings =
                {
                    new FabPrinting { PrintingUniqueId = "reg", SetId = "ELE", CardId = "ELE001", Foiling = "S", TcgPlayerProductId = "10" },
                    new FabPrinting { PrintingUniqueId = "mv", SetId = "PEN", CardId = "PEN001", Foiling = "C", TcgPlayerProductId = "11" },
                },
            });
            db.FabTcgProducts.AddRange(
                new FabTcgProduct { ProductId = 10, Name = "Lexi", Number = "ELE001", IsSingle = true },
                new FabTcgProduct { ProductId = 11, Name = "Lexi (Marvel)", Number = "PEN001", IsSingle = true });
            await db.SaveChangesAsync();
            var resolver = new FabCardResolver(db, new MemoryCache(new MemoryCacheOptions()));

            var marvel = DiscordListingParser.Parse("WTS\nLexi Marvel $25").Items.Single();
            var plain = DiscordListingParser.Parse("WTS\nLexi $5").Items.Single();

            Assert.Equal("mv", (await resolver.ResolveAsync(marvel, default))?.PrintingUniqueId);
            Assert.Equal("reg", (await resolver.ResolveAsync(plain, default))?.PrintingUniqueId);
        }

        [Fact]
        public async Task A_named_version_missing_from_the_dataset_comes_from_TCGplayer()
        {
            var db = new BotDbContext(new DbContextOptionsBuilder<BotDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            db.FabCards.Add(new FabCard { UniqueId = "a", Name = "Aurora", Printings = { new FabPrinting { PrintingUniqueId = "reg", SetId = "ROS", CardId = "ROS008", Foiling = "S", TcgPlayerProductId = "20" } } });
            db.FabTcgProducts.AddRange(
                new FabTcgProduct { ProductId = 20, Name = "Aurora", Number = "ROS008", IsSingle = true },
                new FabTcgProduct { ProductId = 21, Name = "Aurora (Marvel)", Number = "ROS008", IsSingle = true });
            await db.SaveChangesAsync();

            var item = DiscordListingParser.Parse("WTS\nAurora Marvel $90ea").Items.Single();
            var resolved = await new FabCardResolver(db, new MemoryCache(new MemoryCacheOptions())).ResolveAsync(item, default);

            Assert.Equal(21, resolved?.ProductId);
        }

        [Fact]
        public async Task A_full_comma_name_wins_over_its_first_word()
        {
            var db = new BotDbContext(new DbContextOptionsBuilder<BotDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            db.FabCards.AddRange(
                new FabCard { UniqueId = "a", Name = "Arakni", Printings = { new FabPrinting { PrintingUniqueId = "a1", SetId = "DYN", CardId = "DYN001", Foiling = "S" } } },
                new FabCard { UniqueId = "r", Name = "Arakni, Redback", Printings = { new FabPrinting { PrintingUniqueId = "r1", SetId = "HNT", CardId = "HNT001", Foiling = "S" } } });
            await db.SaveChangesAsync();
            var resolver = new FabCardResolver(db, new MemoryCache(new MemoryCacheOptions()));

            var redback = DiscordListingParser.Parse("WTS\nArakni, Redback - HNT - $16").Items.Single();
            var chatter = DiscordListingParser.Parse("WTS\nArakni, mint, $16").Items.Single();

            Assert.Equal("Arakni, Redback", (await resolver.ResolveAsync(redback, default))?.CardName);
            Assert.Equal("Arakni", (await resolver.ResolveAsync(chatter, default))?.CardName);
        }
    }
}
