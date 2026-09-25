using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>
    /// Turns the two int-keyed Data assets (Achievements, SecretNotes) into the text their
    /// Collections-page tooltips actually draw, so <see cref="TranslationMap"/> can join them like
    /// any string table. Their raw values never appear on screen as-is.
    ///
    /// Every shape here comes from <c>CollectionsPage.createDescription</c> in the installed 1.6.15
    /// assembly (read with ikdasm), not guessed. Game-free so it can be unit-tested.
    /// </summary>
    public static class DataTextShapes
    {
        /// <summary>
        /// Achievements: a record <c>name^description^visible^prerequisite^icon</c>, drawn as
        /// <c>name + NewLine + NewLine + description</c>. The two fields become separate entries
        /// (<c>&lt;id&gt;#0</c>, <c>&lt;id&gt;#1</c>), which the map's per-paragraph pass then matches one
        /// paragraph at a time.
        /// </summary>
        public static Dictionary<string, string> AchievementTexts(IReadOnlyDictionary<string, string> records)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in records)
            {
                string[] fields = pair.Value.Split('^');
                if (fields.Length >= 2)
                {
                    result[pair.Key + "#0"] = fields[0];
                    result[pair.Key + "#1"] = fields[1];
                }
            }

            return result;
        }

        /// <summary>
        /// Secret notes and journal scraps, as (source, target) tables joined on
        /// <c>&lt;id&gt;#&lt;paragraph&gt;</c>.
        ///
        /// The game draws a note as <c>ParseGiftReveals(text).TrimStart(' ', '^')</c> with <c>^</c> made a
        /// newline and <c>@</c> the player's name; a <c>^^</c> is therefore a blank line, which the map's
        /// paragraph pass splits on. Hence one entry per paragraph, not per note. <c>@</c> becomes
        /// <c>{0}</c> so the pair registers as a token template and matches whatever name is drawn.
        /// A value starting with <c>!</c> is an image note with no text.
        ///
        /// Paragraphs are paired by position, so a note whose two locales don't have the same number
        /// of paragraphs is left out rather than risk pairing the wrong ones; <paramref name="skipped"/>
        /// counts them.
        /// </summary>
        public static (Dictionary<string, string> Source, Dictionary<string, string> Target) SecretNoteParagraphs(
            IReadOnlyDictionary<string, string> source, IReadOnlyDictionary<string, string> target, out int skipped)
        {
            var sourceOut = new Dictionary<string, string>(StringComparer.Ordinal);
            var targetOut = new Dictionary<string, string>(StringComparer.Ordinal);
            skipped = 0;

            foreach (var pair in source)
            {
                if (!target.TryGetValue(pair.Key, out string? targetNote))
                    continue;

                if (pair.Value.StartsWith("!", StringComparison.Ordinal) || targetNote.StartsWith("!", StringComparison.Ordinal))
                    continue;

                string[] sourceParagraphs = NoteParagraphs(pair.Value);
                string[] targetParagraphs = NoteParagraphs(targetNote);
                if (sourceParagraphs.Length != targetParagraphs.Length)
                {
                    skipped++;
                    continue;
                }

                for (int i = 0; i < sourceParagraphs.Length; i++)
                {
                    sourceOut[$"{pair.Key}#{i}"] = sourceParagraphs[i];
                    targetOut[$"{pair.Key}#{i}"] = targetParagraphs[i];
                }
            }

            return (sourceOut, targetOut);
        }

        /// <summary>
        /// The tooltip header, "Secret Note #3" / "Journal Scrap #2": the localized name, " #" and
        /// a number. Returned as a {0} template pair per name, for <see cref="TranslationMap.AddPair"/>.
        /// </summary>
        public static IEnumerable<(string Source, string Target)> NoteHeaderTemplates(
            IReadOnlyDictionary<string, string> sourceLocations, IReadOnlyDictionary<string, string> targetLocations)
        {
            foreach (string key in new[] { "Secret_Note_Name", "Journal_Name" })
            {
                if (sourceLocations.TryGetValue(key, out string? sourceName) && targetLocations.TryGetValue(key, out string? targetName))
                    yield return (sourceName + " #{0}", targetName + " #{0}");
            }
        }

        /// <summary>One note's text as drawn, split at its blank lines.</summary>
        public static string[] NoteParagraphs(string note)
        {
            string text = GiftReveal.Replace(note, "").TrimStart(' ', '^').Replace("^", "\n").Replace("@", "{0}");

            var paragraphs = new List<string>();
            foreach (string paragraph in BlankLine.Split(text))
            {
                if (paragraph.Trim().Length > 0)
                    paragraphs.Add(paragraph.Trim());
            }

            return paragraphs.ToArray();
        }

        /// <summary>
        /// <c>%revealtaste:Leah:196</c>, which <c>Utility.ParseGiftReveals</c> strips (recording the
        /// taste as revealed). The tokens run together with no whitespace between them.
        /// </summary>
        private static readonly Regex GiftReveal = new(@"%revealtaste\S*", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>A blank line: two newlines with only spaces between them (notes indent with spaces).</summary>
        private static readonly Regex BlankLine = new(@"\n[ \t]*\n", RegexOptions.Compiled);
    }
}
