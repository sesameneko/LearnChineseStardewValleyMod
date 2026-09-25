using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using LanguageStudyStardewValleyMod;

namespace ModLogic.Tests
{
    public class SegmentIndexTests
    {
        private const string Acorn = "植えるとオークの木が育つ。";

        private static readonly TextSegment[] AcornSegments =
        {
            new("植える", "ueru", "to plant"),
            TextSegment.Plain("と"),
            TextSegment.Plain("オーク"),
            TextSegment.Plain("の"),
            TextSegment.Plain("木"),
            TextSegment.Plain("が"),
            TextSegment.Plain("育つ。"),
        };

        private static string[] TextOf(IEnumerable<TextSegment> segments) => segments.Select(s => s.Text).ToArray();

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
            Assert.Equal(TextOf(AcornSegments), TextOf(segments));
        }

        [Fact]
        public void Finds_data_for_text_the_game_word_wrapped()
        {
            Assert.True(IndexWithAcorn().TryGetSegments("植えるとオーク\nの木が育つ。", out var segments));
            Assert.Equal(TextOf(AcornSegments), TextOf(segments));
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

            Assert.False(index.TryAdd(Acorn, new[] { TextSegment.Plain("植える"), TextSegment.Plain("と"), TextSegment.Plain("オーク") }));
            Assert.Equal(0, index.Count);
        }

        [Fact]
        public void Maps_segments_onto_a_single_unwrapped_line()
        {
            var lines = new[] { Acorn };

            Assert.True(SegmentIndex.TryGetSegmentsForLine(AcornSegments, lines, 0, out var lineSegments));
            Assert.Equal(TextOf(AcornSegments), TextOf(lineSegments));
        }

        [Fact]
        public void Splits_a_segment_that_straddles_a_line_break()
        {
            // "オーク" is cut by the wrap: "オー" ends line 0 and "ク" starts line 1
            var lines = new[] { "植えるとオー", "クの木が育つ。" };

            Assert.True(SegmentIndex.TryGetSegmentsForLine(AcornSegments, lines, 0, out var first));
            Assert.Equal(new[] { "植える", "と", "オー" }, TextOf(first));
            Assert.Equal("植えるとオー", SegmentIndex.Concat(first));

            Assert.True(SegmentIndex.TryGetSegmentsForLine(AcornSegments, lines, 1, out var second));
            Assert.Equal(new[] { "ク", "の", "木", "が", "育つ。" }, TextOf(second));
            Assert.Equal("クの木が育つ。", SegmentIndex.Concat(second));

            // a clipped segment keeps the gloss of the word it came from
            Assert.Equal("to plant", first[0].Gloss);
        }

        [Fact]
        public void Line_segments_always_reproduce_their_line()
        {
            var lines = new[] { "植えると", "オークの木", "が育つ。" };

            for (int i = 0; i < lines.Length; i++)
            {
                Assert.True(SegmentIndex.TryGetSegmentsForLine(AcornSegments, lines, i, out var lineSegments));
                Assert.Equal(lines[i], SegmentIndex.Concat(lineSegments));
            }
        }

        [Fact]
        public void Explains_a_miss_by_naming_where_the_nearest_string_diverges()
        {
            // the typical runtime-assembled miss: same sentence, different noun filled in
            string why = IndexWithAcorn().ExplainMiss("植えるとリンゴの木が育つ。");

            Assert.Contains("shares the first 4 of 13", why);
            Assert.Contains("「オークの木が育つ…」", why); // what the data has at the divergence
            Assert.Contains("「リンゴの木が育つ…」", why); // what was drawn
        }

        [Fact]
        public void Explains_a_miss_on_a_truncation_of_an_indexed_string()
        {
            // one page of a longer dialogue, or a note the game cut short
            string why = IndexWithAcorn().ExplainMiss("植えると\nオーク");

            Assert.Contains("is a prefix of the indexed", why);
        }

        [Fact]
        public void Explains_a_miss_with_nothing_similar_indexed()
        {
            Assert.Contains("no indexed string starts with the same character", IndexWithAcorn().ExplainMiss("知らない文章。"));
        }

        [Fact]
        public void Explains_a_miss_on_data_that_was_rejected_at_load()
        {
            var index = new SegmentIndex();
            index.TryAdd(Acorn, new[] { TextSegment.Plain("植える"), TextSegment.Plain("と") });

            string why = index.ExplainMiss(Acorn);

            Assert.Contains("rejected at load", why);
            Assert.Contains("「植えると」", why);
        }

        [Fact]
        public void Explains_a_line_mismatch_with_the_first_differing_character()
        {
            // a space the game kept at the wrap that the source doesn't have
            var lines = new[] { "植えると ", "オークの木が育つ。" };

            string why = SegmentIndex.ExplainLineMismatch(AcornSegments, lines, 1);

            Assert.Contains("differ at character 4", why);
            Assert.Contains("「 オークの木が育…」", why);
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
                    .Select(segment => new TextSegment(
                        segment.GetProperty("text").GetString()!,
                        segment.TryGetProperty("reading", out var reading) ? reading.GetString() : null,
                        segment.TryGetProperty("gloss", out var gloss) ? gloss.GetString() : null))
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
            Assert.Equal(Acorn, SegmentIndex.Concat(acorn));
            Assert.True(acorn.Count > 1);

            // the real data carries in-context glosses, which is the whole point of using it
            Assert.All(acorn, segment => Assert.False(string.IsNullOrWhiteSpace(segment.Gloss)));
        }
    }
}
