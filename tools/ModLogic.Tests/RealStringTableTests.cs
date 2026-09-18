using System.Text.Json;
using LanguageStudyStardewValleyMod;

namespace ModLogic.Tests
{
    /// <summary>
    /// Builds a <see cref="TranslationMap"/> out of the real ja/en string tables extracted from the
    /// installed game (see tools/extracted-strings), so the join behaves the same here as it will at
    /// runtime -- where the same two dictionaries arrive from the content pipeline instead of JSON.
    /// </summary>
    public class RealStringTableTests
    {
        private static readonly string ExtractedDir =
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "extracted-strings");

        private static Dictionary<string, string> LoadTable(string locale, string table)
        {
            string path = Path.GetFullPath(Path.Combine(ExtractedDir, locale, table + ".json"));
            Assert.True(File.Exists(path), $"Expected extracted strings at '{path}' (regenerate with tools/XnbStringTool).");

            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);

            var entries = new Dictionary<string, string>();
            foreach (var property in document.RootElement.GetProperty("entries").EnumerateObject())
                entries[property.Name] = property.Value.GetString()!;

            return entries;
        }

        private static TranslationMap ObjectsMap()
        {
            var map = new TranslationMap();
            map.AddTable(LoadTable("ja", "Objects"), LoadTable("en", "Objects"));
            return map;
        }

        [Fact]
        public void Translates_a_real_item_name()
        {
            Assert.True(ObjectsMap().TryLookup("ドングリ", out string translation));
            Assert.Equal("Acorn", translation);
        }

        [Fact]
        public void Translates_a_real_item_description_even_when_word_wrapped_mid_sentence()
        {
            var map = ObjectsMap();

            string japanese = LoadTable("ja", "Objects")["Acorn_Description"];
            string wrapped = japanese.Insert(japanese.Length / 2, "\n");

            Assert.True(map.TryLookup(wrapped, out string translation));
            Assert.Equal(LoadTable("en", "Objects")["Acorn_Description"], translation);
        }

        [Fact]
        public void Indexes_most_of_the_real_objects_table()
        {
            var map = ObjectsMap();

            // ~1500 entries in the table; the shortfall is the handful of ja-only/en-only keys plus
            // entries whose ja text is identical to the en text (deliberately not indexed).
            Assert.InRange(map.Count, 1200, 1600);
        }
    }
}
