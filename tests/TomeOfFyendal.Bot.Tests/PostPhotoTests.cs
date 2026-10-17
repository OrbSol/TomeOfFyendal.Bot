using TomeOfFyendal.Bot;
using Xunit;

namespace TomeOfFyendal.Bot.Tests
{
    public class PostPhotoTests
    {
        private static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Takes_the_first_image_and_reads_its_expiry_from_the_link()
        {
            var (url, expires) = PostPhoto.From(new (string, string?)[]
            {
                ("https://cdn.discordapp.com/attachments/1/2/list.txt?ex=6712a000&is=1&hm=a", "text/plain"),
                ("https://cdn.discordapp.com/attachments/1/2/proof.jpg?ex=6712a000&is=1&hm=b", "image/jpeg"),
                ("https://cdn.discordapp.com/attachments/1/2/second.png?ex=6712a000&is=1&hm=c", "image/png"),
            }, Now);

            Assert.EndsWith("proof.jpg?ex=6712a000&is=1&hm=b", url);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(0x6712a000).UtcDateTime, expires);
        }

        [Fact]
        public void No_content_type_falls_back_to_the_file_extension_and_no_ex_to_twenty_hours()
        {
            var (url, expires) = PostPhoto.From(new (string, string?)[] { ("https://media.discordapp.net/attachments/1/2/proof.webp", null) }, Now);
            Assert.NotNull(url);
            Assert.Equal(Now.AddHours(20), expires);
        }

        [Fact]
        public void Ignores_links_that_are_not_Discord_images()
        {
            Assert.Equal((null, null), PostPhoto.From(new (string, string?)[]
            {
                ("https://example.com/proof.jpg", "image/jpeg"),
                ("https://cdn.discordapp.com/attachments/1/2/clip.mp4", "video/mp4"),
            }, Now));
        }
    }
}
