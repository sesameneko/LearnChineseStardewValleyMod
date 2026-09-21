using System;
using System.Linq;
using LanguageStudyStardewValleyMod;
using Xunit;

namespace ModLogic.Tests
{
    /// <summary>
    /// Tests for the SpriteText wrap replica. The measurer is a fake -- every character is 10 wide --
    /// so the cases read as character counts.
    /// </summary>
    public class WrapToWidthTests
    {
        private static readonly Func<string, int> TenPerChar = s => s.Length * 10;

        [Fact]
        public void LeavesShortTextAlone()
        {
            Assert.Equal("abc", TextHitTest.WrapToWidth("abc", 100, breakAnywhere: false, TenPerChar));
        }

        [Fact]
        public void NoWidthMeansNoWrapping()
        {
            Assert.Equal("abcdefghij", TextHitTest.WrapToWidth("abcdefghij", -1, breakAnywhere: true, TenPerChar));
        }

        [Fact]
        public void BreaksAnywhereMidWord()
        {
            // 4 chars per line: the break lands between characters, as Japanese does
            Assert.Equal("あいうえ\nおかきく\nけこ", TextHitTest.WrapToWidth("あいうえおかきくけこ", 40, breakAnywhere: true, TenPerChar));
        }

        /// <summary>
        /// Note the space stays at the end of the wrapped line rather than being swallowed: the
        /// concatenation invariant forbids dropping it, and a trailing space renders as nothing.
        /// </summary>
        [Fact]
        public void BreaksAtTheLastSpaceWhenAsked()
        {
            Assert.Equal("one two \nthree", TextHitTest.WrapToWidth("one two three", 80, breakAnywhere: false, TenPerChar));
        }

        [Fact]
        public void BreaksAnywhereWhenAWordIsLongerThanTheLine()
        {
            Assert.Equal("abcd\nefgh\nij", TextHitTest.WrapToWidth("abcdefghij", 40, breakAnywhere: false, TenPerChar));
        }

        [Fact]
        public void KeepsExplicitNewlines()
        {
            Assert.Equal("ab\ncd", TextHitTest.WrapToWidth("ab\ncd", 100, breakAnywhere: true, TenPerChar));
        }

        /// <summary>
        /// The invariant SegmentIndex.TryGetSegmentsForLine depends on: the rendered lines must
        /// re-concatenate into exactly the source, so wrapping may only ever *add* newlines.
        /// Dropping the space at a break would cost every gloss on the string.
        /// </summary>
        [Theory]
        [InlineData("one two three four five", 80, false)]
        [InlineData("あいうえおかきくけこさしす", 40, true)]
        [InlineData("a bb ccc dddd eeeee ffffff", 50, false)]
        [InlineData("mixed text with  double  spaces", 70, false)]
        public void OnlyEverInsertsNewlines(string text, int width, bool breakAnywhere)
        {
            string wrapped = TextHitTest.WrapToWidth(text, width, breakAnywhere, TenPerChar);

            Assert.Equal(text, wrapped.Replace("\n", ""));
            Assert.Equal(text, string.Concat(TextHitTest.SplitLines(wrapped)));
        }

        [Theory]
        [InlineData("あいうえおかきくけこさしすせそ", 40, true)]
        [InlineData("one two three four five six", 80, false)]
        public void NoLineExceedsTheWidthUnlessItCannotBreak(string text, int width, bool breakAnywhere)
        {
            string[] lines = TextHitTest.SplitLines(TextHitTest.WrapToWidth(text, width, breakAnywhere, TenPerChar));

            foreach (string line in lines)
                Assert.True(TenPerChar(line) <= width || line.Length <= 1, $"line over width: '{line}'");
        }

        [Fact]
        public void DoesNotEmitEmptyLines()
        {
            string[] lines = TextHitTest.SplitLines(TextHitTest.WrapToWidth("one two three four", 40, breakAnywhere: false, TenPerChar));
            Assert.DoesNotContain("", lines.Select(line => line.Trim() == "" ? "" : line));
        }
    }
}
