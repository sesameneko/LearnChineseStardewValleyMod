using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StardewModdingAPI;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>
    /// Loads the hand-segmented word boundaries bundled with the mod into a <see cref="SegmentIndex"/>.
    ///
    /// The files are read loosely on purpose: each one carries a "_comment" header alongside its real
    /// entries, and the loader shouldn't depend on every file being well-formed, so anything
    /// unparseable is skipped rather than failing the load and costing every string its
    /// boundaries.
    /// </summary>
    public static class SegmentDataLoader
    {
        /// <summary>Loads every bundled segment file for a source locale.</summary>
        public static SegmentIndex Load(IModHelper helper, string sourceLanguage)
        {
            var index = new SegmentIndex();

            // the HUD clock's frames are built in code rather than read from a table, so their
            // segments are too (ja only so far; the zh clock is a TODOs.md item)
            if (sourceLanguage == "ja")
                ClockSegments.AddTo(index);

            string directory = Path.Combine(helper.DirectoryPath, "assets", "segments", sourceLanguage);

            if (!Directory.Exists(directory))
            {
                ModEntry.Log($"No segment data for '{sourceLanguage}' at '{directory}' -- word hover will fall back to character-class splitting.", LogLevel.Debug);
                return index;
            }

            int rejected = 0;
            foreach (string path in Directory.EnumerateFiles(directory, "*.json").OrderBy(path => path))
            {
                try
                {
                    rejected += LoadFile(path, index);
                }
                catch (Exception ex)
                {
                    ModEntry.Log($"Couldn't read segment data '{Path.GetFileName(path)}': {ex.GetType().Name}: {ex.Message}", LogLevel.Warn);
                }
            }

            ModEntry.Log($"Segment data loaded: exact word boundaries for {index.Count} '{sourceLanguage}' string(s)"
                         + (rejected > 0 ? $"; {rejected} entr(ies) skipped as malformed" : "") + ".");

            return index;
        }

        /// <summary>Adds one file's entries; returns how many were rejected.</summary>
        private static int LoadFile(string path, SegmentIndex index)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            string table = Path.GetFileNameWithoutExtension(path);
            int rejected = 0;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                // the "_comment" header is a string, not an entry
                if (property.Value.ValueKind != JsonValueKind.Object)
                    continue;

                if (!property.Value.TryGetProperty("chinese", out var sourceElement)
                    || !property.Value.TryGetProperty("segments", out var segmentsElement)
                    || segmentsElement.ValueKind != JsonValueKind.Array)
                {
                    rejected++;
                    continue;
                }

                string? sourceText = sourceElement.GetString();
                string? english = property.Value.TryGetProperty("english", out var englishElement) && englishElement.ValueKind == JsonValueKind.String
                    ? englishElement.GetString()
                    : null;

                // each segment remembers where it was authored, so a word saved as a flashcard can
                // point back at its sentence
                int offset = 0;
                var segments = new List<TextSegment>();
                foreach (var element in segmentsElement.EnumerateArray())
                {
                    var segment = ReadSegment(element);
                    if (segment.Text.Length == 0)
                        continue;

                    segments.Add(segment with { Source = new SegmentSource(table, property.Name, offset, segment.Text.Length) });
                    offset += segment.Text.Length;
                }

                if (!index.TryAdd(sourceText, segments))
                    rejected++;
                else
                    index.Entries.Add(table, property.Name, sourceText!, english);
            }

            return rejected;
        }

        private static TextSegment ReadSegment(JsonElement element)
        {
            string text = element.TryGetProperty("text", out var textElement) ? textElement.GetString() ?? "" : "";
            string? pinyin = element.TryGetProperty("pinyin", out var pinyinElement) ? pinyinElement.GetString() : null;
            string? gloss = element.TryGetProperty("gloss", out var glossElement) ? glossElement.GetString() : null;

            // the gloss is shown in the game's font and gets the ASCII-only treatment, since the
            // font doesn't exist yet at load; the Chinese text itself is left exactly as authored,
            // since it has to keep matching what the game drew
            // the pinyin is stored as authored (space-separated syllables with tone marks): the
            // ASCII-only pass would strip its tones, so it's font-safed where it's drawn instead
            // TODO(zh migration): WordHoverOverlay and the flashcards still read Kana, so pinyin
            // isn't displayed yet -- see TODOs.md, "Chinese migration"
            return new TextSegment(text, pinyin, FontSafeText.Apply(gloss));
        }
    }
}
