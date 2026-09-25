using System.Text.Json;
using LanguageStudyStardewValleyMod;

namespace ModLogic.Tests
{
    /// <summary>
    /// The Collections-page tooltips for achievements and secret notes, rebuilt the way
    /// <c>CollectionsPage.createDescription</c> builds them, then translated through a map fed by
    /// <see cref="DataTextShapes"/> from the real extracted ja/en data.
    /// </summary>
    public class DataTextShapesTests
    {
        private static readonly string ExtractedDir =
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "extracted-strings");

        private static readonly string NL = Environment.NewLine;

        private static Dictionary<string, string> Load(string folder, string table)
        {
            string path = Path.GetFullPath(Path.Combine(ExtractedDir, folder, table + ".json"));
            Assert.True(File.Exists(path), $"Expected extracted data at '{path}'.");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.GetProperty("entries").EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetString()!);
        }

        private static TranslationMap Map()
        {
            var map = new TranslationMap();

            map.AddTable(DataTextShapes.AchievementTexts(Load("data-ja", "Achievements")),
                         DataTextShapes.AchievementTexts(Load("data-en", "Achievements")));

            var (source, target) = DataTextShapes.SecretNoteParagraphs(Load("data-ja", "SecretNotes"), Load("data-en", "SecretNotes"), out _);
            map.AddTable(source, target);

            foreach (var (sourceTemplate, targetTemplate) in DataTextShapes.NoteHeaderTemplates(Load("ja", "Locations"), Load("en", "Locations")))
                map.AddPair(sourceTemplate, targetTemplate);

            return map;
        }

        /// <summary>What the game draws for a note: header, blank line, cleaned text, cut at 15 lines.</summary>
        private static string NoteTooltip(string header, string note, string playerName = "Ben")
        {
            // paragraphs rejoined with the blank lines the game keeps between them (unwrapped: the
            // map is wrap-insensitive, and fewer lines only makes the 15-line cut less likely)
            string body = string.Join(NL + NL, DataTextShapes.NoteParagraphs(note)).Replace("\n", NL).Replace("{0}", playerName);

            string[] lines = body.Split(NL);
            if (lines.Length > 15)
                body = string.Join(NL, lines.Take(15)).Trim() + NL + "(...)";

            return header + NL + NL + body;
        }

        [Fact]
        public void Translates_an_achievement_tooltip()
        {
            var achievements = Load("data-ja", "Achievements");
            string[] fields = achievements["0"].Split('^');

            Assert.True(Map().TryLookup(fields[0] + NL + NL + fields[1], out string translation));
            Assert.Equal("Greenhorn (15k)" + NL + NL + "Earn 15,000g", translation);
        }

        [Fact]
        public void Translates_a_short_secret_note_with_its_header()
        {
            string note = Load("data-ja", "SecretNotes")["25"];

            Assert.True(Map().TryLookup(NoteTooltip(Load("ja", "Locations")["Secret_Note_Name"] + " #25", note), out string translation));
            Assert.StartsWith("Secret Note #25", translation);
            Assert.Contains("necklace", translation);
        }

        [Fact]
        public void Fills_the_players_name_into_a_note()
        {
            string note = Load("data-ja", "SecretNotes")["22"];

            Assert.True(Map().TryLookup(NoteTooltip(Load("ja", "Locations")["Secret_Note_Name"] + " #22", note, "Ben"), out string translation));
            Assert.Contains("Ben", translation);
            Assert.DoesNotContain("{0}", translation);
        }

        [Fact]
        public void Translates_a_note_the_game_cut_short()
        {
            // the enchantment list runs far past 15 lines, so the tooltip ends in "(...)"
            string note = Load("data-ja", "SecretNotes")["1008"];
            string tooltip = NoteTooltip(Load("ja", "Locations")["Journal_Name"] + " #8", note);
            Assert.EndsWith("(...)", tooltip);

            Assert.True(Map().TryLookup(tooltip, out string translation));
            Assert.EndsWith("(...)", translation);
        }

        [Fact]
        public void Note_text_is_cleaned_the_way_the_game_draws_it()
        {
            string[] paragraphs = DataTextShapes.NoteParagraphs("^^アイウ^エオ^^カキ%revealtaste:Leah:196%revealtaste:Leah:426 @");

            Assert.Equal(new[] { "アイウ\nエオ", "カキ {0}" }, paragraphs);
        }

        [Fact]
        public void Most_notes_pair_up_paragraph_for_paragraph()
        {
            var japanese = Load("data-ja", "SecretNotes");
            DataTextShapes.SecretNoteParagraphs(japanese, Load("data-en", "SecretNotes"), out int skipped);

            // a mismatched note is skipped, not mis-paired; most should still line up
            Assert.True(skipped <= japanese.Count / 4, $"{skipped} of {japanese.Count} notes had mismatched paragraph counts");
        }
    }
}
