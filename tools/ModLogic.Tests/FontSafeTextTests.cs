using LanguageStudyStardewValleyMod;
using Xunit;

namespace ModLogic.Tests
{
    public class FontSafeTextTests
    {
        [Theory]
        [InlineData("ganjōsa", "ganjoosa")]
        [InlineData("jūnansa", "juunansa")]
        [InlineData("dōgu", "doogu")]
        [InlineData("ā ī ū ē ō", "aa ii uu ee oo")]
        public void DoublesMacronVowels(string input, string expected)
        {
            Assert.Equal(expected, FontSafeText.Apply(input));
        }

        [Theory]
        [InlineData("café", "cafe")]
        [InlineData("piñata", "pinata")]
        [InlineData("word — gloss", "word -- gloss")]
        [InlineData("don’t", "don't")]
        [InlineData("wait…", "wait...")]
        public void ReplacesWhatTheFontLacks(string input, string expected)
        {
            Assert.Equal(expected, FontSafeText.Apply(input));
        }

        [Theory]
        [InlineData("plain ascii gloss")]
        [InlineData("tool")]
        [InlineData("")]
        public void LeavesAsciiAlone(string input)
        {
            Assert.Equal(input, FontSafeText.Apply(input));
        }

        [Fact]
        public void HandlesNull()
        {
            Assert.Equal("", FontSafeText.Apply(null));
        }

        /// <summary>
        /// The point of the exercise: nothing reaching the font may be non-ASCII, or it renders as
        /// the font's DefaultCharacter ('*') instead.
        /// </summary>
        [Theory]
        [InlineData("ganjōsa — sturdiness")]
        [InlineData("ā ī ū ē ō Ā Ī Ū Ē Ō")]
        [InlineData("café “quoted” … don’t")]
        public void OutputIsAllAscii(string input)
        {
            foreach (char c in FontSafeText.Apply(input))
                Assert.True(c <= 127, $"non-ASCII survived: {(int)c:X4}");
        }
    }
}
