using System.Collections.Generic;
using LanguageStudyStardewValleyMod;

namespace ModLogic.Tests
{
    public class PinyinTests
    {
        [Theory]
        [InlineData("mù chǎng", "mùchǎng")]
        [InlineData("xī ān", "xī'ān")]
        [InlineData("tiān é", "tiān'é")]
        [InlineData("yì diǎn r", "yìdiǎnr")]
        [InlineData("dōng xi", "dōngxi")]
        [InlineData("xīng qī yī", "xīngqīyī")]
        [InlineData("Joja chāo shì", "Joja chāoshì")]
        [InlineData("bǎi fēn zhī shí", "bǎifēnzhīshí")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void Joins_syllables_per_word(string? stored, string shown)
        {
            Assert.Equal(shown, Pinyin.Display(stored));
        }

        [Theory]
        [InlineData("mù chǎng", "mu4chang3")]
        [InlineData("dōng xi", "dong1xi")]
        [InlineData("lǜ", "lü4")]
        [InlineData("xī ān", "xi1an1")]
        [InlineData("ń", "n2")]
        public void Writes_tone_numbers(string stored, string shown)
        {
            Assert.Equal(shown, Pinyin.ToneNumbers(stored));
        }

        [Fact]
        public void Falls_back_to_numbers_only_where_the_font_lacks_a_mark()
        {
            // the zh SmallFont has the 2nd and 4th tones but not the 3rd
            var font = new HashSet<char>("áàéèíìóòúùü");
            Assert.Equal("mùlì", Pinyin.ForFont("mù lì", font));
            Assert.Equal("mu4chang3", Pinyin.ForFont("mù chǎng", font));
            Assert.Equal("de", Pinyin.ForFont("de", null));
        }
    }
}
