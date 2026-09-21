using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using LanguageStudyStardewValleyMod;

namespace ModLogic.Tests
{
    public class SegmentIndexTests
    {
        private static readonly string[] AcornSegments = { "植える", "と", "オーク", "の", "木", "が", "育つ。" };
        private const string Acorn = "植えるとオークの木が育つ。";

        private static SegmentIndex IndexWithAcorn()
        {
            var index = new SegmentIndex();
            Assert.True(index.TryAdd(Acorn, AcornSegments));
            return index;
        }

        [Fact]
        public void Supplies_exact_boundaries_where_data_exists()
        {
            Assert.True(IndexWithAcorn().TryGetSegments(Acorn, out var segments));
            Assert.Equal(AcornSegments, segments);
        }

        [Fact]
        public void Finds_data_for_text_the_game_word_wrapped()
        {
            Assert.True(IndexWithAcorn().TryGetSegments("植えるとオーク\nの木が育つ。", out var segments));
            Assert.Equal(AcornSegments, segments);
        }

        [Fact]
        public void Reports_nothing_for_a_string_with_no_data()
        {
            Assert.False(IndexWithAcorn().TryGetSegments("知らない文章。", out _));
        }

        [Fact]
        public void Rejects_data_that_breaks_the_concatenation_invariant()
        {
            // every on-screen position is computed from prefix lengths, so data that doesn't
            // reproduce the source would silently mis-place highlights
            var index = new SegmentIndex();

            Assert.False(index.TryAdd(Acorn, new[] { "植える", "と", "オーク" }));
            Assert.Equal(0, index.Count);
        }

        [Fact]
        public void Maps_segments_onto_a_single_unwrapped_line()
        {
            var lines = new[] { Acorn };

            Assert.True(SegmentIndex.TryGetSegmentsForLine(AcornSegments, lines, 0, out var lineSegments));
            Assert.Equal(AcornSegments, lineSegments);
        }

        [Fact]
        public void Splits_a_segment_that_straddles_a_line_break()
        {
            // "オーク" is cut by the wrap: "オー" ends line 0 and "ク" starts line 1
            var lines = new[] { "植えるとオー", "クの木が育つ。" };

            Assert.True(SegmentIndex.TryGetSegmentsForLine(AcornSegments, lines, 0, out var first));
            Assert.Equal(new[] { "植える", "と", "オー" }, first);
            Assert.Equal("植えるとオー", string.Concat(first));

            Assert.True(SegmentIndex.TryGetSegmentsForLine(AcornSegments, lines, 1, out var second));
            Assert.Equal(new[] { "ク", "の", "木", "が", "育つ。" }, second);
            Assert.Equal("クの木が育つ。", string.Concat(second));
        }

        [Fact]
        public void Line_segments_always_reproduce_their_line()
        {
            var lines = new[] { "植えると", "オークの木", "が育つ。" };

            for (int i = 0; i < lines.Length; i++)
            {
                Assert.True(SegmentIndex.TryGetSegmentsForLine(AcornSegments, lines, i, out var lineSegments));
                Assert.Equal(lines[i], string.Concat(lineSegments));
            }
        }

        [Fact]
        public void Refuses_to_map_when_the_lines_do_not_match_the_data()
        {
            // stale data against different on-screen text: better to fall back than to box the wrong word
            var lines = new[] { "まったく違う文章。" };

            Assert.False(SegmentIndex.TryGetSegmentsForLine(AcornSegments, lines, 0, out _));
        }

        [Fact]
        public void Loads_the_real_hand_segmented_objects_data()
        {
            string path = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..",
                "extracted-strings", "literal-translations", "Objects_Description.json"));
            Assert.True(File.Exists(path), $"Expected hand-segmented data at '{path}'.");

            var index = new SegmentIndex();
            int added = 0, rejected = 0;

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                    continue; // the "_comment" header

                string japanese = property.Value.GetProperty("japanese").GetString()!;
                var segments = property.Value.GetProperty("segments")
                    .EnumerateArray()
                    .Select(segment => segment.GetProperty("text").GetString()!)
                    .ToArray();

                if (index.TryAdd(japanese, segments))
                    added++;
                else
                    rejected++;
            }

            // the invariant is documented as checked for all 744 descriptions; hold the data to it
            Assert.Equal(0, rejected);
            Assert.InRange(added, 700, 745);

            Assert.True(index.TryGetSegments(Acorn, out var acorn));
            Assert.Equal(Acorn, string.Concat(acorn));
            Assert.True(acorn.Count > 1);
        }
    }
}
