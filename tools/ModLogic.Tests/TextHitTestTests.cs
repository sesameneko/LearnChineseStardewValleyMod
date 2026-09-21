using System;
using System.Collections.Generic;
using System.Linq;
using LanguageStudyStardewValleyMod;

namespace ModLogic.Tests
{
    public class TextHitTestTests
    {
        /// <summary>A stand-in for real font metrics: every character is 10px wide.</summary>
        private static Func<string, float> FixedWidth(float perChar = 10f) => text => text.Length * perChar;

        [Fact]
        public void Segments_always_tile_the_line_exactly()
        {
            // the same invariant the hand-segmented data in tools/extracted-strings guarantees:
            // without it, measuring a prefix tells you nothing about where a segment starts
            foreach (string line in new[]
                     {
                         "植えるとオークの木が育つ。",
                         "Comfortable and durable for long hours.",
                         "攻撃 +5",
                         "日記 （F）",
                         "",
                     })
            {
                Assert.Equal(line, string.Concat(TextHitTest.SplitSegments(line)));
            }
        }

        [Fact]
        public void Splits_japanese_by_character_class()
        {
            var segments = TextHitTest.SplitSegments("オークの木");

            Assert.Equal(new[] { "オーク", "の", "木" }, segments);
        }

        [Fact]
        public void Keeps_a_run_of_kanji_together()
        {
            // a known limit of the class-based heuristic: 長時間 and 快適 are two words but one
            // unbroken kanji run, so they merge. Fine for the PoC; the shipped boundaries come from
            // the hand-segmented data instead.
            Assert.Contains("長時間快適", TextHitTest.SplitSegments("この分野では長時間快適で丈夫。"));
        }

        [Fact]
        public void Keeps_whitespace_runs_so_prefixes_stay_measurable()
        {
            var segments = TextHitTest.SplitSegments("Hello big world");

            Assert.Equal(new[] { "Hello", " ", "big", " ", "world" }, segments);
        }

        [Fact]
        public void Splits_a_drawn_string_into_the_lines_it_rendered_as()
        {
            Assert.Equal(new[] { "one", "two" }, TextHitTest.SplitLines("one\ntwo"));
            Assert.Equal(new[] { "one", "two" }, TextHitTest.SplitLines("one\r\ntwo"));
            Assert.Equal(new[] { "solo" }, TextHitTest.SplitLines("solo"));
        }

        [Theory]
        [InlineData(100f, 0)]   // first line spans [100, 132)
        [InlineData(131f, 0)]   // still the first line
        [InlineData(132f, 1)]   // second spans [132, 164)
        [InlineData(165f, 2)]   // third spans [164, 196)
        public void Finds_the_line_a_point_falls_on(float pointY, int expected)
        {
            Assert.Equal(expected, TextHitTest.HitLine(lineCount: 3, topY: 100f, lineHeight: 32f, pointY: pointY));
        }

        [Theory]
        [InlineData(99f)]    // above the block
        [InlineData(196f)]   // below the block
        public void Reports_no_line_outside_the_text_block(float pointY)
        {
            Assert.Null(TextHitTest.HitLine(lineCount: 3, topY: 100f, lineHeight: 32f, pointY: pointY));
        }

        [Fact]
        public void Finds_the_segment_a_point_falls_on()
        {
            var segments = TextHitTest.SplitSegments("オークの木");  // 3 + 1 + 1 chars, 10px each

            var measure = FixedWidth();
            Assert.Equal(0, TextHitTest.HitSegment(segments, measure, leftX: 50f, pointX: 55f));    // inside オーク
            Assert.Equal(1, TextHitTest.HitSegment(segments, measure, leftX: 50f, pointX: 85f));    // inside の
            Assert.Equal(2, TextHitTest.HitSegment(segments, measure, leftX: 50f, pointX: 95f));    // inside 木
        }

        [Theory]
        [InlineData(49f)]    // left of the text
        [InlineData(101f)]   // right of the text
        public void Reports_no_segment_outside_the_line(float pointX)
        {
            var segments = TextHitTest.SplitSegments("オークの木");

            Assert.Null(TextHitTest.HitSegment(segments, FixedWidth(), leftX: 50f, pointX: pointX));
        }

        [Fact]
        public void Segment_extents_are_contiguous_and_match_the_hit_test()
        {
            var segments = TextHitTest.SplitSegments("オークの木");
            var measure = FixedWidth();

            float expectedLeft = 0f;
            for (int i = 0; i < segments.Count; i++)
            {
                var (left, width) = TextHitTest.SegmentExtent(segments, measure, i);

                Assert.Equal(expectedLeft, left, 3);
                Assert.Equal(segments[i].Length * 10f, width, 3);

                // a point in the middle of this extent must hit this very segment
                Assert.Equal(i, TextHitTest.HitSegment(segments, measure, leftX: 0f, pointX: left + (width / 2f)));

                expectedLeft += width;
            }
        }

        [Fact]
        public void Handles_an_empty_line_without_throwing()
        {
            Assert.Empty(TextHitTest.SplitSegments(""));
            Assert.Null(TextHitTest.HitSegment(new List<string>(), FixedWidth(), 0f, 0f));
        }
    }
}
