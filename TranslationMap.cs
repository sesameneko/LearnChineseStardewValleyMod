using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

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

        /// <summary>
        /// Source strings holding <c>{N}</c> format tokens, which the game fills in with
        /// <c>string.Format</c> at draw time -- so the text on screen never equals the stored
        /// template and no exact lookup can ever match it. Roughly 9% of the shared ja/en entries
        /// are these. Matched by regex on lookup miss; see <see cref="TryLookupByTemplate"/>.
        /// </summary>
        private readonly List<Template> templates = new();

        /// <summary>Guards against registering the same source template twice.</summary>
        private readonly HashSet<string> templateSources = new(StringComparer.Ordinal);

        /// <summary>
        /// Memoises template results (misses included) keyed by normalized displayed text, because a
        /// hovered tooltip re-queries every frame and a miss otherwise re-scans every template.
        /// </summary>
        private readonly Dictionary<string, string?> templateCache = new(StringComparer.Ordinal);

        /// <summary>Cap on <see cref="templateCache"/>, which is cleared wholesale when reached.</summary>
        private const int TemplateCacheLimit = 512;

        /// <summary>Set when a template is added, so the match order is re-sorted before the next lookup.</summary>
        private bool templatesNeedSorting;

        /// <summary>The number of distinct source strings that can be translated.</summary>
        public int Count => this.byNormalized.Count;

        /// <summary>The number of token-substituted templates that can be matched.</summary>
        public int TemplateCount => this.templates.Count;

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

            // an untranslated entry (the locale variant just repeats the source) is noise, not a
            // translation -- but it is still a known paragraph, which the paragraph pass needs (a
            // secret note signed "-Qi" in both locales would otherwise fail the whole note)
            if (string.Equals(normalizedSource, normalizedTarget, StringComparison.Ordinal))
            {
                this.sameInBothLocales.Add(normalizedSource);
                return;
            }

            // first table wins on collision, so index construction order is what decides ambiguities
            if (!this.byNormalized.ContainsKey(normalizedSource))
                this.byNormalized[normalizedSource] = normalizedTarget;

            string spaceless = RemoveWhitespace(normalizedSource);
            if (spaceless.Length > 0 && !this.bySpaceless.ContainsKey(spaceless))
                this.bySpaceless[spaceless] = normalizedTarget;

            this.AddTemplate(normalizedSource, normalizedTarget);
        }

        /// <summary>
        /// Registers a source/target pair as a token template, if it is one. A pair qualifies only
        /// when both sides use the same set of <c>{N}</c> tokens -- a target with a token the source
        /// can't supply has nothing to fill it from, and a source token the target drops would make
        /// the match ambiguous for no gain.
        /// </summary>
        private void AddTemplate(string normalizedSource, string normalizedTarget)
        {
            if (normalizedSource.IndexOf('{') < 0)
                return;

            MatchCollection sourceTokens = TokenPattern.Matches(normalizedSource);
            if (sourceTokens.Count == 0)
                return;

            var sourceIndexes = new HashSet<int>();
            foreach (Match token in sourceTokens)
                sourceIndexes.Add(int.Parse(token.Groups[1].Value));

            var targetIndexes = new HashSet<int>();
            foreach (Match token in TokenPattern.Matches(normalizedTarget))
                targetIndexes.Add(int.Parse(token.Groups[1].Value));

            if (!sourceIndexes.SetEquals(targetIndexes))
                return;

            if (!this.templateSources.Add(normalizedSource))
                return;

            Regex? normalizedMatcher = BuildMatcher(normalizedSource);
            if (normalizedMatcher is null)
                return;

            // the spaceless matcher is the word-wrap fallback, exactly as bySpaceless is for exact
            // lookups: captured values lose their internal spaces, which is acceptable because what
            // the game substitutes is nearly always a number, a keybind or a single name
            Regex? spacelessMatcher = BuildMatcher(RemoveWhitespace(normalizedSource));

            string anchor = LongestLiteralOf(normalizedSource);
            this.templates.Add(new Template(normalizedMatcher, spacelessMatcher, normalizedTarget, LiteralLengthOf(normalizedSource), anchor, RemoveWhitespace(anchor)));
            this.templatesNeedSorting = true;
            this.templateCache.Clear();
        }

        /// <summary>
        /// The template's longest stretch of literal (non-token) text. Any text that matches the
        /// template must contain it verbatim, so a substring check rules most templates out without
        /// touching their regex -- which matters because a lookup miss tries every one of them.
        /// </summary>
        private static string LongestLiteralOf(string template)
        {
            string longest = "";
            int position = 0;

            foreach (Match token in TokenPattern.Matches(template))
            {
                string literal = template.Substring(position, token.Index - position);
                if (literal.Length > longest.Length)
                    longest = literal;
                position = token.Index + token.Length;
            }

            string tail = template.Substring(position);
            return tail.Length > longest.Length ? tail : longest;
        }

        /// <summary>How much of a template is fixed text rather than tokens or whitespace.</summary>
        private static int LiteralLengthOf(string template)
        {
            int length = 0;
            int position = 0;

            foreach (Match token in TokenPattern.Matches(template))
            {
                length += CountLiteral(template.Substring(position, token.Index - position));
                position = token.Index + token.Length;
            }

            return length + CountLiteral(template.Substring(position));
        }

        private static int CountLiteral(string text)
        {
            int count = 0;
            foreach (char c in text)
            {
                if (!char.IsWhiteSpace(c))
                    count++;
            }

            return count;
        }

        /// <summary>
        /// Turns a source template into an anchored regex: literal parts escaped, each token a
        /// named capture group (<c>t0</c>, <c>t1</c>, ...), a repeated token a back-reference to its
        /// own first capture. Returns null for a template too loose to match safely.
        /// </summary>
        private static Regex? BuildMatcher(string template)
        {
            var pattern = new StringBuilder("^");
            var captured = new HashSet<int>();
            int literalLength = 0;
            int position = 0;
            bool previousWasToken = false;

            foreach (Match token in TokenPattern.Matches(template))
            {
                string literal = template.Substring(position, token.Index - position);

                // two tokens with nothing between them can be split anywhere: the capture would be
                // arbitrary, so refuse the template rather than translate with made-up values
                if (previousWasToken && literal.Length == 0)
                    return null;

                pattern.Append(Regex.Escape(literal));
                literalLength += CountLiteral(literal);

                int index = int.Parse(token.Groups[1].Value);
                pattern.Append(captured.Add(index) ? $"(?<t{index}>.+?)" : $@"\k<t{index}>");

                position = token.Index + token.Length;
                previousWasToken = true;
            }

            string tail = template.Substring(position);
            pattern.Append(Regex.Escape(tail));
            literalLength += CountLiteral(tail);
            pattern.Append('$');

            // a template that is (almost) all token matches any text at all, which would translate
            // unrelated strings into this entry's target
            if (literalLength < 2)
                return null;

            return new Regex(pattern.ToString(), RegexOptions.Compiled | RegexOptions.CultureInvariant, MatchTimeout);
        }

        /// <summary>Looks up the translation of a piece of text as it was actually displayed on screen.</summary>
        public bool TryLookup(string? displayedText, out string translation)
        {
            translation = "";

            if (string.IsNullOrWhiteSpace(displayedText))
                return false;

            // exact first, then paragraph by paragraph, and only then the loose matchers on the
            // whole text: a template's {N} capture will happily swallow a blank line and every
            // paragraph after it (the secret-note header "ひみつのメモ #{0}" matched an entire note
            // that way), so a multi-paragraph tooltip must get its per-paragraph chance first
            if (this.TryLookupExact(displayedText!, out translation))
                return true;

            if (this.TryLookupByParagraph(displayedText!, out translation))
                return true;

            return this.TryLookupLoose(displayedText!, out translation);
        }

        /// <summary>
        /// Every way one whole piece of text can be matched: exact, wrap-insensitive, then as a
        /// token template. Shared by <see cref="TryLookup"/> and the per-paragraph pass so both get
        /// the same coverage.
        /// </summary>
        private bool TryLookupOne(string text, out string translation)
        {
            return this.TryLookupExact(text, out translation) || this.TryLookupLoose(text, out translation);
        }

        /// <summary>Token-template and cut-short ("(...)") matching of one whole piece of text.</summary>
        private bool TryLookupLoose(string text, out string translation)
        {
            translation = "";

            string normalized = Normalize(text);
            if (normalized.Length == 0)
                return false;

            if (this.TryLookupByTemplate(normalized, RemoveWhitespace(normalized), out translation))
                return true;

            return this.TryLookupTruncated(normalized, out translation);
        }

        /// <summary>
        /// Matches text the game cut short and marked with "(...)": the Collections page shows a
        /// secret note's first 15 wrapped lines, then a newline and a literal "(...)". What's left
        /// is a prefix of a known source string, so this finds the one source that starts with it and
        /// returns that full translation, re-marked. It is ambiguous if two sources share the prefix
        /// and translate differently; then it matches nothing rather than guess.
        /// </summary>
        private bool TryLookupTruncated(string normalized, out string translation)
        {
            translation = "";

            if (!normalized.EndsWith(TruncationMarker, StringComparison.Ordinal))
                return false;

            string prefix = RemoveWhitespace(normalized.Substring(0, normalized.Length - TruncationMarker.Length));
            if (prefix.Length < MinTruncatedPrefix)
                return false;

            // a hovered tooltip re-queries every frame; the scan below is over every source string
            if (!this.truncatedCache.TryGetValue(prefix, out string? found))
            {
                foreach (var pair in this.bySpaceless)
                {
                    // a prefix equal to a whole source is fine: the cut can land exactly at a paragraph end
                    if (!pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                        continue;

                    if (found != null && found != pair.Value)
                    {
                        found = null;
                        break;
                    }

                    found = pair.Value;
                }

                if (this.truncatedCache.Count >= TemplateCacheLimit)
                    this.truncatedCache.Clear();
                this.truncatedCache[prefix] = found;
            }

            if (found is null)
                return false;

            translation = found + " " + TruncationMarker;
            return true;
        }

        /// <summary>What the game appends to a tooltip it cut short (a literal, in every locale).</summary>
        private const string TruncationMarker = "(...)";

        /// <summary>A prefix shorter than this is too unspecific to pin one source string down.</summary>
        private const int MinTruncatedPrefix = 8;

        /// <summary>
        /// Source strings whose target is identical (untranslated). Never a whole-text result, since
        /// a tooltip repeating the source adds nothing -- only a paragraph carried through inside a
        /// larger translated tooltip.
        /// </summary>
        private readonly HashSet<string> sameInBothLocales = new(StringComparer.Ordinal);

        /// <summary>Memoises <see cref="TryLookupTruncated"/>, misses included, by spaceless prefix.</summary>
        private readonly Dictionary<string, string?> truncatedCache = new(StringComparer.Ordinal);

        /// <summary>
        /// Matches text the game produced by <c>string.Format</c>-ing a template, e.g. the journal
        /// button's "日記 （F）" against the stored "日記 （{0}）" -> "Journal ({0})".
        ///
        /// The captured values are substituted into the target template *by token index*, never by
        /// position, because the two locales order their tokens differently ("攻撃 +{0}" vs
        /// "+{0} Attack"). A captured value that is itself a known source string is translated too,
        /// so an item name interpolated into a sentence doesn't stay in the source language.
        /// </summary>
        private bool TryLookupByTemplate(string normalized, string spaceless, out string translation)
        {
            translation = "";

            if (this.templates.Count == 0)
                return false;

            if (this.templatesNeedSorting)
            {
                // most-literal first, so a specific template ("攻撃 +{0}") is tried before a loose
                // one ("{0} ({1})") that would otherwise swallow it on insertion order alone
                this.templates.Sort((a, b) => b.LiteralLength.CompareTo(a.LiteralLength));
                this.templatesNeedSorting = false;
            }

            if (this.templateCache.TryGetValue(normalized, out string? cached))
            {
                translation = cached ?? "";
                return cached is not null;
            }

            string? result = null;
            foreach (Template template in this.templates)
            {
                bool couldMatchNormalized = normalized.Contains(template.Anchor, StringComparison.Ordinal);
                bool couldMatchSpaceless = spaceless.Length > 0 && template.SpacelessAnchor.Length > 0
                    && spaceless.Contains(template.SpacelessAnchor, StringComparison.Ordinal);
                if (!couldMatchNormalized && !couldMatchSpaceless)
                    continue;

                Match match = couldMatchNormalized ? template.Matcher.Match(normalized) : Match.Empty;
                if (!match.Success && couldMatchSpaceless && template.SpacelessMatcher is not null)
                    match = template.SpacelessMatcher.Match(spaceless);

                if (!match.Success)
                    continue;

                result = this.Fill(template.Target, match);
                break;
            }

            // misses are cached too: a tooltip we can't translate re-queries every frame as well
            if (this.templateCache.Count >= TemplateCacheLimit)
                this.templateCache.Clear();
            this.templateCache[normalized] = result;

            translation = result ?? "";
            return result is not null;
        }

        /// <summary>Substitutes a match's captures into the target template by token index.</summary>
        private string Fill(string target, Match match)
        {
            return TokenPattern.Replace(target, token =>
            {
                Group capture = match.Groups[$"t{token.Groups[1].Value}"];
                if (!capture.Success)
                    return token.Value;

                string value = capture.Value.Trim();
                return this.TryLookupExact(value, out string translated) ? translated : value;
            });
        }

        /// <summary>The exact-match half of a lookup only -- no templates, so this can't recurse.</summary>
        private bool TryLookupExact(string text, out string translation)
        {
            translation = "";

            string normalized = Normalize(text);
            if (normalized.Length == 0)
                return false;

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

        /// <summary>
        /// Handles tooltips the game concatenates out of several table entries at draw time, which no
        /// whole-string lookup can match. Clothing is the case that surfaced this: the body is the
        /// item's description, a blank line, then "Dyeable." -- two separate entries (Pants and UI)
        /// that are each in the index, joined only on screen.
        ///
        /// Every paragraph has to translate for this to report success: a partial result would put
        /// untranslated source text inside a box whose whole purpose is to be the translation.
        /// </summary>
        private bool TryLookupByParagraph(string displayedText, out string translation)
        {
            translation = "";

            string[] paragraphs = ParagraphBreak.Split(displayedText);
            if (paragraphs.Length < 2)
                return false;

            var translated = new List<string>(paragraphs.Length);
            foreach (string paragraph in paragraphs)
            {
                if (Normalize(paragraph).Length == 0)
                    continue; // a stray extra blank line, not a paragraph of its own

                // deliberately not recursive into another paragraph split: a paragraph that is
                // itself unmatched stops the whole lookup
                if (!this.TryLookupOne(paragraph, out string paragraphTranslation))
                {
                    // text both locales write identically ("-Qi") carries over as-is
                    if (!this.sameInBothLocales.Contains(Normalize(paragraph)))
                        return false;
                    paragraphTranslation = Normalize(paragraph);
                }

                translated.Add(paragraphTranslation);
            }

            if (translated.Count < 2)
                return false;

            translation = string.Join(Environment.NewLine + Environment.NewLine, translated);
            return true;
        }

        /// <summary>A blank line, i.e. what the game puts between two concatenated tooltip sections.</summary>
        private static readonly Regex ParagraphBreak = new(@"\n[ \t]*\r?\n", RegexOptions.Compiled);

        /// <summary>A <c>string.Format</c> placeholder. Only bare <c>{N}</c> counts; any other brace is literal text.</summary>
        private static readonly Regex TokenPattern = new(@"\{(\d+)\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>Backstop against a pathological template regex stalling a draw.</summary>
        private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

        /// <summary>One token-substituted source template and the target template to fill from it.</summary>
        /// <param name="Matcher">Matches the normalized displayed text.</param>
        /// <param name="SpacelessMatcher">The word-wrap fallback, matching whitespace-stripped text; null when the template can't be matched that way.</param>
        /// <param name="Target">The target-locale template, still holding its <c>{N}</c> tokens.</param>
        /// <param name="LiteralLength">How much non-token, non-space text the source template pins down; more specific templates are matched first.</param>
        /// <param name="Anchor">The template's longest run of literal text; text not containing it cannot possibly match, which is far cheaper to rule out than running the regex.</param>
        /// <param name="SpacelessAnchor"><paramref name="Anchor"/> with its spaces removed, for the wrap fallback.</param>
        private readonly record struct Template(Regex Matcher, Regex? SpacelessMatcher, string Target, int LiteralLength, string Anchor, string SpacelessAnchor);

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
