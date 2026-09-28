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
                AppContext.BaseDirectory, "..", "..", "..",
                "fixtures", "segments-ja", "Objects_Description.json"));
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

        private static readonly TextSegment[] MoneySegments =
        {
            new("手持ち", "temochi", "on hand"),
            TextSegment.Plain("の"),
            new("お金", "okane", "money"),
            TextSegment.Plain("："),
            new("{0}G", null, "{0}g"),
        };

        [Fact]
        public void Fills_in_a_token_template_and_follows_the_drawn_whitespace()
        {
            var index = new SegmentIndex();
            Assert.True(index.TryAdd("手持ちのお金：{0}G", MoneySegments));

            // the game draws a space the stored template doesn't have
            Assert.True(index.TryGetSegments("手持ちのお金： 29,560G", out var segments));
            Assert.Equal(new[] { "手持ち", "の", "お金", "： ", "29,560G" }, TextOf(segments));
            Assert.Equal("29,560g", segments[^1].Gloss);
        }

        [Fact]
        public void Fills_in_a_template_the_game_word_wrapped()
        {
            var index = new SegmentIndex();
            Assert.True(index.TryAdd("手持ちのお金：{0}G", MoneySegments));

            string drawn = "手持ちのお\n金：29,560G";
            Assert.True(index.TryGetSegments(drawn, out var segments));

            string[] lines = TextHitTest.SplitLines(drawn);
            Assert.True(SegmentIndex.TryGetSegmentsForLine(segments, lines, 1, out var second));
            Assert.Equal(new[] { "金", "：", "29,560G" }, TextOf(second));
        }

        [Fact]
        public void A_token_whose_value_is_segmented_data_takes_that_data()
        {
            var index = new SegmentIndex();
            Assert.True(index.TryAdd("秋", new[] { new TextSegment("秋", "aki", "fall") }));
            Assert.True(index.TryAdd("{2}年目、{0}日、{1}", new[]
            {
                new TextSegment("{2}年目、", "nenme", "year {2}"),
                new TextSegment("{0}日、", "nichi", "day {0}"),
                new TextSegment("{1}", "", "(season)"),
            }));

            Assert.True(index.TryGetSegments("2年目、8日、秋", out var segments));
            Assert.Equal(new[] { "2年目、", "8日、", "秋" }, TextOf(segments));
            Assert.Equal(new[] { "year 2", "day 8", "fall" }, segments.Select(s => s.Gloss).ToArray());
        }

        [Fact]
        public void Matches_the_drawn_start_of_a_longer_string()
        {
            // mail: the stored text ends in commands the game strips before drawing
            var index = new SegmentIndex();
            Assert.True(index.TryAdd("やあ、どうも。^-ウィリーより%item quest 13 true %%[#]招待状", new[]
            {
                new TextSegment("やあ、", "yaa", "hey"),
                new TextSegment("どうも。^", "doumo", "hello"),
                new TextSegment("-ウィリーより", "wirii yori", "from Willy"),
                TextSegment.Plain("%item quest 13 true %%[#]招待状"),
            }));

            Assert.True(index.TryGetSegments("やあ、どうも。^-ウィリーより ", out var segments));
            Assert.Equal(new[] { "やあ、", "どうも。^", "-ウィリーより " }, TextOf(segments));
        }

        [Fact]
        public void Does_not_prefix_match_short_text()
        {
            var index = new SegmentIndex();
            Assert.True(index.TryAdd("50000Gをかせぐ", new[] { TextSegment.Plain("50000G"), TextSegment.Plain("をかせぐ") }));

            Assert.False(index.TryGetSegments("500", out _));
        }

        [Fact]
        public void Does_not_match_text_that_only_resembles_a_template()
        {
            var index = new SegmentIndex();
            Assert.True(index.TryAdd("手持ちのお金：{0}G", MoneySegments));

            Assert.False(index.TryGetSegments("稼いだ利益：579,858G", out _));
        }

        [Fact]
        public void Covers_text_joined_from_several_entries()
        {
            var index = new SegmentIndex();
            Assert.True(index.TryAdd("この分野では丈夫。", new[] { new TextSegment("この分野では", null, "in this field"), new TextSegment("丈夫。", null, "sturdy") }));
            Assert.True(index.TryAdd("可染性。", new[] { new TextSegment("可染性。", null, "dyeable") }));

            // the segments tile the drawn text with its line breaks removed, as TryGetSegmentsForLine expects
            Assert.True(index.TryGetSegments("この分野では丈夫。\n\n可染性。", out var segments));
            Assert.Equal(new[] { "この分野では", "丈夫。", "可染性。" }, TextOf(segments));
        }

        [Fact]
        public void Covers_a_dialogue_page_from_the_middle_of_its_entry()
        {
            var index = new SegmentIndex();
            Assert.True(index.TryAdd("どうだったかね？#$e#君の おじいさんは 愛してたんじゃ。", new[]
            {
                new TextSegment("どうだったかね？#$e#", null, "how was it?"),
                new TextSegment("君", null, "you"),
                new TextSegment("の ", null, "'s"),
                new TextSegment("おじいさん", null, "grandfather"),
                new TextSegment("は ", null, "(topic)"),
                new TextSegment("愛してたんじゃ。", null, "loved"),
            }));

            Assert.True(index.TryGetSegments("君の おじいさんは 愛して\nたんじゃ。", out var segments));
            Assert.Equal(new[] { "君", "の ", "おじいさん", "は ", "愛してたんじゃ。" }, TextOf(segments));
            Assert.Equal("grandfather", segments[2].Gloss);
        }

        [Fact]
        public void Leaves_uncovered_characters_to_the_heuristic()
        {
            var index = new SegmentIndex();
            Assert.True(index.TryAdd("ルイス", new[] { new TextSegment("ルイス", null, "Lewis") }));
            Assert.True(index.TryAdd("どうしても欲しいのは", new[] { new TextSegment("どうしても", null, "no matter what"), new TextSegment("欲しいのは", null, "what I want is") }));

            Assert.True(index.TryGetSegments("どうしても欲しいのは - ルイス", out var segments));
            Assert.Equal(new[] { "どうしても", "欲しいのは ", "- ", "ルイス" }, TextOf(segments));
            Assert.Null(segments[2].Gloss);
            Assert.Equal("Lewis", segments[3].Gloss);
        }

        [Fact]
        public void Refuses_a_composite_that_covers_too_little()
        {
            var index = new SegmentIndex();
            Assert.True(index.TryAdd("ルイス", new[] { new TextSegment("ルイス", null, "Lewis") }));

            Assert.False(index.TryGetSegments("ルイスさんは今日もとても元気そうだ", out _));
        }

        [Fact]
        public void Ignores_one_character_entries_in_a_composite()
        {
            // 日 is stored as "Sunday"; it must not be what labels the 日 in a date
            var index = new SegmentIndex();
            Assert.True(index.TryAdd("日", new[] { new TextSegment("日", null, "Sunday") }));
            Assert.True(index.TryAdd("ようこそ", new[] { new TextSegment("ようこそ", null, "welcome") }));

            Assert.True(index.TryGetSegments("ようこそようこそ8日", out var segments));
            Assert.DoesNotContain(segments, s => s.Gloss == "Sunday");
        }

        [Theory]
        [InlineData("どうしても欲しいのは 完熟のパースニップで\nす。地元の 牧場主なら 届けてくれると 思いま\nす。\n                - ルイス\n\n\n- 105g を 受け取り時に 支払\n- ルイスが 喜ぶ")]
        [InlineData("この分野では長時間快適\nで丈夫。\n\n可染性。")]
        [InlineData("君の おじいさんは ガタガタの ベッ\nドに よく 文句を 言ってたよ。でも\n、 彼は あの家を 心の底から 愛して\nたんじゃ。")]
        [InlineData("2年目、8日、秋")]
        [InlineData("手持ちのお金： 29,560G")]
        [InlineData("稼いだ利益：579,858G")]
        [InlineData("Goatland 牧場")]
        [InlineData("やあ、どうも。^釣りの旅からちょうど帰ってきたところだ。気が向いたら海岸\nへ来てくれよ。^お前さんにわたしたいもんがあるんだ。^-ウィリーより ")]
        public void Finds_real_data_for_text_the_game_assembled_at_draw_time(string drawn)
        {
            // each of these fell back to the heuristic split in a live session
            var index = new SegmentIndex();
            foreach (string table in new[] { "StringsFromCSFiles", "UI", "Data_mail", "Pants", "NPCNames", "Objects_Name", "Dialogue-Lewis" })
                LoadRealTable(index, table);

            Assert.True(index.TryGetSegments(drawn, out var segments), index.ExplainMiss(drawn));
            Assert.True(segments.Count > 1);

            string[] lines = TextHitTest.SplitLines(drawn);
            for (int i = 0; i < lines.Length; i++)
            {
                // a blank line between paragraphs holds no segment, and can't be hovered anyway
                if (string.IsNullOrWhiteSpace(lines[i]))
                    continue;

                Assert.True(SegmentIndex.TryGetSegmentsForLine(segments, lines, i, out _), SegmentIndex.ExplainLineMismatch(segments, lines, i));
            }
        }

        [Fact]
        public void A_template_opening_with_a_token_does_not_swallow_the_text_before_its_literal()
        {
            // "{0} 牧場" placed at the start once captured everything up to 牧場主 and won,
            // labelling half the quest "(farm name)"
            var index = new SegmentIndex();
            foreach (string table in new[] { "StringsFromCSFiles", "UI", "NPCNames", "Objects_Name" })
                LoadRealTable(index, table);

            Assert.True(index.TryGetSegments("どうしても欲しいのは 完熟のパースニップで\nす。地元の 牧場主なら 届けてくれると 思いま\nす。\n                - ルイス\n\n\n- 105g を 受け取り時に 支払\n- ルイスが 喜ぶ", out var segments));

            Assert.Equal("どうしても", segments[0].Text);
            Assert.Contains(segments, s => s.Text == "パースニップ");
            Assert.Contains(segments, s => s.Text == "牧場主");
            // trimmed: a segment carries the whitespace drawn after it, here the signature's indent
            Assert.All(segments, s => Assert.True(s.Text.Trim().Length <= 12, $"segment {SegmentIndex.Quote(s.Text)} swallowed too much"));
        }

        private static void LoadRealTable(SegmentIndex index, string table)
        {
            string path = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..",
                "fixtures", "segments-ja", table + ".json"));

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object || !property.Value.TryGetProperty("segments", out var segments))
                    continue;

                index.TryAdd(property.Value.GetProperty("japanese").GetString(), segments
                    .EnumerateArray()
                    .Select(segment => new TextSegment(
                        segment.GetProperty("text").GetString()!,
                        segment.TryGetProperty("reading", out var reading) ? reading.GetString() : null,
                        segment.TryGetProperty("gloss", out var gloss) ? gloss.GetString() : null))
                    .ToArray());
            }
        }
    }
}
