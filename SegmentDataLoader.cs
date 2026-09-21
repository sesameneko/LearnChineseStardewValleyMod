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
    /// entries, and coverage is partial (item descriptions only today -- see Plan.md's data task), so
    /// anything unparseable is skipped rather than failing the load and costing every string its
    /// boundaries.
    /// </summary>
    public static class SegmentDataLoader
    {
        /// <summary>Loads every bundled segment file for a source locale.</summary>
        public static SegmentIndex Load(IModHelper helper, string sourceLanguage)
        {
            var index = new SegmentIndex();
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
            int rejected = 0;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                // the "_comment" header is a string, not an entry
                if (property.Value.ValueKind != JsonValueKind.Object)
                    continue;

                if (!property.Value.TryGetProperty("japanese", out var japaneseElement)
                    || !property.Value.TryGetProperty("segments", out var segmentsElement)
                    || segmentsElement.ValueKind != JsonValueKind.Array)
                {
                    rejected++;
                    continue;
                }

                var segments = segmentsElement
                    .EnumerateArray()
                    .Select(segment => segment.TryGetProperty("text", out var text) ? text.GetString() : null)
                    .Where(text => text != null)
                    .Select(text => text!)
                    .ToArray();

                if (!index.TryAdd(japaneseElement.GetString(), segments))
                    rejected++;
            }

            return rejected;
        }
    }
}
