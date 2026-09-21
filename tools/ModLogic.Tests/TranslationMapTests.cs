using System;
using System.Collections.Generic;
using LanguageStudyStardewValleyMod;

namespace ModLogic.Tests
{
    public class TranslationMapTests
    {
        private static TranslationMap MapOf(params (string key, string source, string target)[] rows)
        {
            var source = new Dictionary<string, string>();
            var target = new Dictionary<string, string>();
            foreach (var row in rows)
            {
                source[row.key] = row.source;
                target[row.key] = row.target;
            }

            var map = new TranslationMap();
            map.AddTable(source, target);
            return map;
        }

        [Fact]
        public void Joins_two_locale_variants_on_their_shared_keys()
        {
            var map = MapOf(("Acorn_Name", "どんぐり", "Acorn"));

            Assert.True(map.TryLookup("どんぐり", out string translation));
            Assert.Equal("Acorn", translation);
        }

        [Fact]
        public void Skips_keys_that_exist_in_only_one_locale()
        {
            // the real game data has exactly this shape -- e.g. Jelly_Flavored_(O)282_Name is in en but not ja
            var source = new Dictionary<string, string> { ["Shared"] = "共有" };
            var target = new Dictionary<string, string> { ["Shared"] = "Shared", ["EnglishOnly"] = "English only" };

            var map = new TranslationMap();
            map.AddTable(source, target);

            Assert.Equal(1, map.Count);
            Assert.True(map.TryLookup("共有", out _));
        }

        [Fact]
        public void Skips_entries_whose_locale_variant_was_never_translated()
        {
            var map = MapOf(("Untranslated", "Joja Cola", "Joja Cola"));

            Assert.Equal(0, map.Count);
            Assert.False(map.TryLookup("Joja Cola", out _));
        }

        [Fact]
        public void Lookup_ignores_the_newlines_the_game_inserts_when_word_wrapping()
        {
            // Japanese wraps mid-sentence with no space in the original, so the displayed text can
            // only be recovered by dropping the inserted whitespace entirely, not collapsing it.
            var map = MapOf(("Acorn_Description", "植えるとオークの木が育つ。", "An oak tree grows if you plant it."));

            Assert.True(map.TryLookup("植えるとオーク\nの木が育つ。", out string translation));
            Assert.Equal("An oak tree grows if you plant it.", translation);
        }

        [Fact]
        public void Lookup_collapses_wrapping_in_space_separated_text_too()
        {
            var map = MapOf(("Greeting", "Bonjour tout le monde", "Hello everyone"));

            Assert.True(map.TryLookup("Bonjour tout\n   le monde  ", out string translation));
            Assert.Equal("Hello everyone", translation);
        }

        [Fact]
        public void Gender_variants_are_indexed_individually_as_well_as_whole()
        {
            var map = MapOf(("Title", "お兄さん^お姉さん", "Mister^Miss"));

            Assert.True(map.TryLookup("お姉さん", out string feminine));
            Assert.Equal("Miss", feminine);
            Assert.True(map.TryLookup("お兄さん", out string masculine));
            Assert.Equal("Mister", masculine);
        }

        [Fact]
        public void Mismatched_variant_counts_translate_nothing_rather_than_something_wrong()
        {
            var map = MapOf(("Title", "あ^い^う", "One^Two"));

            Assert.False(map.TryLookup("い", out _));
            // the whole unsplit string is still a legitimate pair
            Assert.True(map.TryLookup("あ^い^う", out _));
        }

        [Fact]
        public void First_table_wins_when_two_tables_disagree_about_the_same_source_string()
        {
            var map = new TranslationMap();
            map.AddTable(new Dictionary<string, string> { ["A"] = "石" }, new Dictionary<string, string> { ["A"] = "Stone" });
            map.AddTable(new Dictionary<string, string> { ["B"] = "石" }, new Dictionary<string, string> { ["B"] = "Rock" });

            Assert.True(map.TryLookup("石", out string translation));
            Assert.Equal("Stone", translation);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   \n ")]
        public void Blank_lookups_are_never_translations(string? text)
        {
            var map = MapOf(("Acorn_Name", "どんぐり", "Acorn"));

            Assert.False(map.TryLookup(text, out _));
        }

        [Fact]
        public void Translates_a_tooltip_the_game_concatenated_out_of_two_tables()
        {
            // the real Farmer Pants case: the description and the "Dyeable." notice are separate
            // entries (Strings/Pants and Strings/UI) that only ever meet on screen
            var map = new TranslationMap();
            map.AddTable(
                new Dictionary<string, string> { ["FarmerPants_Description"] = "この分野では長時間快適で丈夫。" },
                new Dictionary<string, string> { ["FarmerPants_Description"] = "Comfortable and durable for long hours in the field." });
            map.AddTable(
                new Dictionary<string, string> { ["Clothes_Dyeable"] = "可染性。" },
                new Dictionary<string, string> { ["Clothes_Dyeable"] = "Dyeable." });

            // exactly as it arrives from the game: word-wrapped mid-sentence, blank line between sections
            Assert.True(map.TryLookup("この分野では長時間快適\nで丈夫。\n\n可染性。", out string translation));
            Assert.Equal(
                "Comfortable and durable for long hours in the field." + Environment.NewLine + Environment.NewLine + "Dyeable.",
                translation);
        }

        [Fact]
        public void A_concatenated_tooltip_translates_all_its_paragraphs_or_none()
        {
            // a half-translated box would present untranslated source text as if it were the translation
            var map = MapOf(("Known", "既知の段落。", "A known paragraph."));

            Assert.False(map.TryLookup("既知の段落。\n\n未知の段落。", out _));
        }

        [Fact]
        public void Paragraph_fallback_ignores_an_extra_blank_line()
        {
            var map = new TranslationMap();
            map.AddTable(
                new Dictionary<string, string> { ["A"] = "一つ目。", ["B"] = "二つ目。" },
                new Dictionary<string, string> { ["A"] = "First.", ["B"] = "Second." });

            Assert.True(map.TryLookup("一つ目。\n\n\n二つ目。", out string translation));
            Assert.Equal("First." + Environment.NewLine + Environment.NewLine + "Second.", translation);
        }

        [Fact]
        public void A_single_paragraph_never_goes_through_the_paragraph_fallback()
        {
            var map = MapOf(("Known", "既知の段落。", "A known paragraph."));

            Assert.False(map.TryLookup("未知の段落。", out _));
        }

        [Fact]
        public void Normalize_collapses_every_whitespace_run_and_trims()
        {
            Assert.Equal("a b c", TranslationMap.Normalize("  a \n\t b   c  "));
            Assert.Equal("", TranslationMap.Normalize(null));
        }
        [Fact]
        public void Matches_a_token_template_the_game_formatted_at_draw_time()
        {
            // the journal button: the table holds the template, the screen shows the keybind filled in
            var map = MapOf(("QuestButton_Hover", "日記 （{0}）", "Journal ({0})"));

            Assert.True(map.TryLookup("日記 （F）", out string translation));
            Assert.Equal("Journal (F)", translation);
        }

        [Fact]
        public void Substitutes_captured_values_by_token_index_not_by_position()
        {
            // the two locales order their tokens differently, so position-based filling would swap them
            var map = MapOf(("Swapped", "{0}を{1}に渡した", "Gave {1} the {0}"));

            Assert.True(map.TryLookup("パンをルイスに渡した", out string translation));
            Assert.Equal("Gave ルイス the パン", translation);
        }

        [Fact]
        public void Translates_a_captured_value_that_is_itself_a_known_string()
        {
            var map = MapOf(
                ("Found", "{0}を見つけた。", "You found the {0}."),
                ("Acorn_Name", "どんぐり", "Acorn"));

            Assert.True(map.TryLookup("どんぐりを見つけた。", out string translation));
            Assert.Equal("You found the Acorn.", translation);
        }

        [Fact]
        public void Matches_a_token_template_across_the_newlines_word_wrapping_inserted()
        {
            var map = MapOf(("Wrapped", "こうげきりょくが{0}上がった。", "Attack increased by {0}."));

            Assert.True(map.TryLookup("こうげきりょ\nくが5上がった。", out string translation));
            Assert.Equal("Attack increased by 5.", translation);
        }

        [Fact]
        public void Prefers_the_most_specific_template_over_a_looser_one()
        {
            var map = MapOf(
                ("Loose", "{0}（{1}）", "{0} [{1}]"),
                ("Specific", "日記 （{0}）", "Journal ({0})"));

            Assert.True(map.TryLookup("日記 （F）", out string translation));
            Assert.Equal("Journal (F)", translation);
        }

        [Fact]
        public void Skips_a_template_whose_two_locales_disagree_on_their_tokens()
        {
            // nothing to fill {1} from, so filling it would emit a literal "{1}" into the tooltip
            var map = MapOf(("Mismatched", "{0}を渡した", "Gave {1} the {0}"));

            Assert.Equal(0, map.TemplateCount);
            Assert.False(map.TryLookup("パンを渡した", out _));
        }

        [Fact]
        public void Skips_a_template_that_is_all_token_and_would_match_anything()
        {
            var map = MapOf(("AllToken", "{0}", "{0}"), ("Adjacent", "{0}{1}", "{1}{0}"));

            Assert.Equal(0, map.TemplateCount);
            Assert.False(map.TryLookup("なんでもいい", out _));
        }

        [Fact]
        public void Reports_no_translation_when_no_template_matches()
        {
            var map = MapOf(("QuestButton_Hover", "日記 （{0}）", "Journal ({0})"));

            Assert.False(map.TryLookup("まったく別の文", out string translation));
            Assert.Equal("", translation);
        }

        [Fact]
        public void Repeated_lookups_of_the_same_template_text_agree()
        {
            // the cache is what makes a per-frame tooltip query cheap; it must not change the answer
            var map = MapOf(("QuestButton_Hover", "日記 （{0}）", "Journal ({0})"));

            Assert.True(map.TryLookup("日記 （F）", out string first));
            Assert.True(map.TryLookup("日記 （F）", out string second));
            Assert.False(map.TryLookup("別（X）", out _));
            Assert.False(map.TryLookup("別（X）", out _));
            Assert.Equal(first, second);
        }

        [Fact]
        public void Paragraph_lookup_can_use_a_template_for_one_of_its_paragraphs()
        {
            var map = MapOf(
                ("Desc", "あたたかいズボン。", "Warm pants."),
                ("Buff", "こうげきりょくが{0}上がった。", "Attack increased by {0}."));

            Assert.True(map.TryLookup("あたたかいズボン。\n\nこうげきりょくが5上がった。", out string translation));
            Assert.Contains("Warm pants.", translation);
            Assert.Contains("Attack increased by 5.", translation);
        }

    }
}
