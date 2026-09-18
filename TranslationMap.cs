using System;
using System.Collections.Generic;
using System.Text;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>
    /// A source-language-text -> target-language-text lookup, built by joining two locale variants
    /// of the same string table on their shared keys.
    ///
    /// Deliberately has no dependency on StardewValley/MonoGame types so it can be unit-tested
    /// without launching the game (see tools/ModLogic.Tests). <see cref="TranslationIndex"/> is the
    /// game-side half that actually loads the assets and feeds them in here.
    /// </summary>
    public sealed class TranslationMap
    {
        /// <summary>The gender/variant delimiter the game uses inside a single string value.</summary>
        private const char VariantDelimiter = '^';

        /// <summary>Keyed by <see cref="Normalize"/>d source text.</summary>
        private readonly Dictionary<string, string> byNormalized = new(StringComparer.Ordinal);

        /// <summary>
        /// Keyed by source text with *all* whitespace removed. Needed because the game word-wraps
        /// hover text by inserting newlines, and in a script with no word-boundary spaces (Japanese,
        /// Chinese) those newlines land mid-sentence -- so the displayed text can't be recovered to
        /// the original by collapsing whitespace to a single space, only by dropping it entirely.
        /// </summary>
        private readonly Dictionary<string, string> bySpaceless = new(StringComparer.Ordinal);

        /// <summary>The number of distinct source strings that can be translated.</summary>
        public int Count => this.byNormalized.Count;

        /// <summary>
        /// Joins one string table's source and target locale variants on their shared keys and adds
        /// every resulting text pair. Keys present in only one of the two are skipped (the game's own
        /// data has a few of these -- see tools/extracted-strings/README.md).
        /// </summary>
        public void AddTable(IReadOnlyDictionary<string, string> source, IReadOnlyDictionary<string, string> target)
        {
            if (source is null || target is null)
                return;

            foreach (var pair in source)
            {
                if (!target.TryGetValue(pair.Key, out string? targetValue))
                    continue;

                this.AddPair(pair.Value, targetValue);
            }
        }

        /// <summary>
        /// Adds a single source -> target text pair, plus (when both sides agree on how many there
        /// are) each of its '^'-delimited gender variants as its own pair, since only one variant of
        /// the pair is ever the text actually displayed.
        /// </summary>
        public void AddPair(string? sourceText, string? targetText)
        {
            if (sourceText is null || targetText is null)
                return;

            this.AddSingle(sourceText, targetText);

            if (sourceText.IndexOf(VariantDelimiter) < 0)
                return;

            string[] sourceVariants = sourceText.Split(VariantDelimiter);
            string[] targetVariants = targetText.Split(VariantDelimiter);
            if (sourceVariants.Length != targetVariants.Length)
                return; // mismatched variant counts: can't tell which maps to which, so translate nothing rather than something wrong

            for (int i = 0; i < sourceVariants.Length; i++)
                this.AddSingle(sourceVariants[i], targetVariants[i]);
        }

        private void AddSingle(string sourceText, string targetText)
        {
            string normalizedSource = Normalize(sourceText);
            string normalizedTarget = Normalize(targetText);

            if (normalizedSource.Length == 0 || normalizedTarget.Length == 0)
                return;

            // an untranslated entry (the locale variant just repeats the source) is noise, not a translation
            if (string.Equals(normalizedSource, normalizedTarget, StringComparison.Ordinal))
                return;

            // first table wins on collision, so index construction order is what decides ambiguities
            if (!this.byNormalized.ContainsKey(normalizedSource))
                this.byNormalized[normalizedSource] = normalizedTarget;

            string spaceless = RemoveWhitespace(normalizedSource);
            if (spaceless.Length > 0 && !this.bySpaceless.ContainsKey(spaceless))
                this.bySpaceless[spaceless] = normalizedTarget;
        }

        /// <summary>Looks up the translation of a piece of text as it was actually displayed on screen.</summary>
        public bool TryLookup(string? displayedText, out string translation)
        {
            translation = "";

            if (string.IsNullOrWhiteSpace(displayedText))
                return false;

            string normalized = Normalize(displayedText);
            if (this.byNormalized.TryGetValue(normalized, out string? direct))
            {
                translation = direct;
                return true;
            }

            string spaceless = RemoveWhitespace(normalized);
            if (spaceless.Length > 0 && this.bySpaceless.TryGetValue(spaceless, out string? unwrapped))
            {
                translation = unwrapped;
                return true;
            }

            return false;
        }

        /// <summary>Collapses every run of whitespace (the game's word-wrap newlines included) to one space, and trims.</summary>
        public static string Normalize(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return "";

            var result = new StringBuilder(text.Length);
            bool pendingSpace = false;

            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    pendingSpace = result.Length > 0;
                    continue;
                }

                if (pendingSpace)
                {
                    result.Append(' ');
                    pendingSpace = false;
                }

                result.Append(c);
            }

            return result.ToString();
        }

        private static string RemoveWhitespace(string text)
        {
            if (text.IndexOf(' ') < 0)
                return text;

            return text.Replace(" ", "");
        }
    }
}
