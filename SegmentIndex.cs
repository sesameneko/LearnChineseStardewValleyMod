using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>
    /// One hovering unit: the text as drawn, plus its reading and in-context gloss when the
    /// hand-segmented data supplies them. The heuristic fallback produces text with no gloss.
    /// </summary>
    /// <param name="Text">The characters this segment covers on screen.</param>
    /// <param name="Reading">Romanised reading, e.g. "ueru".</param>
    /// <param name="Gloss">What the word means *in this sentence*, e.g. "to plant".</param>
    /// <param name="Kana">
    /// Kana reading, e.g. "うえる". The lossless form: kana to romaji is deterministic, romaji to
    /// kana is not (ō is おう in gakkō but おお in tōri), so this is what the data holds and what a
    /// romaji/kana preference would be rendered from. It also renders, which romaji doesn't: the
    /// game's font has no macron glyph and silently substitutes '*'.
    /// </param>
    public readonly record struct TextSegment(string Text, string? Reading, string? Gloss, string? Kana = null)
    {
        public static TextSegment Plain(string text) => new(text, null, null);

        /// <summary>The same segment covering only part of its text, for one side of a line break.</summary>
        public TextSegment Clip(int start, int length) => this with { Text = this.Text.Substring(start, length) };
    }

    /// <summary>
    /// Exact word boundaries for strings we have hand-segmented data for, keyed by the source text.
    ///
    /// Japanese word boundaries can't be derived by rule, so <see cref="TextHitTest.SplitSegments"/>'s
    /// character-class heuristic is only ever a fallback: it blobs an unbroken kanji or hiragana run
    /// into one unit. Where data exists (currently item names and descriptions -- see the data task
    /// in Plan.md) this supplies the real boundaries instead.
    ///
    /// Game-free so it can be unit-tested; the mod loads the JSON and feeds it in.
    /// </summary>
    public sealed class SegmentIndex
    {
        private readonly Dictionary<string, IReadOnlyList<TextSegment>> byNormalized = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<TextSegment>> bySpaceless = new(StringComparer.Ordinal);

        /// <summary>
        /// Source strings whose data <see cref="TryAdd"/> turned away, keyed spaceless, with what the
        /// segments joined to instead -- so a miss on one of them can be blamed on the data rather
        /// than reported as "no data".
        /// </summary>
        private readonly Dictionary<string, string> rejected = new(StringComparer.Ordinal);

        /// <summary>
        /// Source strings holding <c>{N}</c> tokens, which the game <c>string.Format</c>s before drawing
        /// ("手持ちのお金：{0}G" is drawn as "手持ちのお金： 29,560G"), so no exact lookup can match them.
        /// About 5% of the data. Matched by regex, most-literal first, as TranslationMap does.
        /// </summary>
        private readonly List<SegmentTemplate> templates = new();

        private bool templatesNeedSorting;

        /// <summary>Every spaceless key in ordinal order, for the prefix lookup; rebuilt lazily after an add.</summary>
        private string[]? sortedSpaceless;

        /// <summary>
        /// Memoises the non-exact lookups (misses included) by drawn text: a hovered string is
        /// re-queried every frame, and a miss otherwise re-runs every template regex.
        /// </summary>
        private readonly Dictionary<string, IReadOnlyList<TextSegment>?> looseCache = new(StringComparer.Ordinal);

        private const int LooseCacheLimit = 512;

        /// <summary>
        /// The shortest drawn text the prefix lookup will try. Short strings are too often the start
        /// of something unrelated -- a drawn "500" is a prefix of the indexed "50000Gをかせぐ".
        /// </summary>
        private const int MinimumPrefixLength = 6;

        public int Count => this.byNormalized.Count;

        /// <summary>The number of token templates that can be matched.</summary>
        public int TemplateCount => this.templates.Count;

        /// <summary>Every segment in the index, for diagnostics that need to inspect the data as loaded.</summary>
        public IEnumerable<TextSegment> AllSegments()
        {
            foreach (var segments in this.byNormalized.Values)
            {
                foreach (var segment in segments)
                    yield return segment;
            }
        }

        /// <summary>
        /// Adds one source string's segmentation. Rejects data that violates the concatenation
        /// invariant (segments must reproduce the source exactly), since every position calculation
        /// downstream assumes it -- bad data would silently mis-place highlights.
        /// </summary>
        public bool TryAdd(string? japanese, IReadOnlyList<TextSegment>? segments)
        {
            if (string.IsNullOrWhiteSpace(japanese) || segments is null || segments.Count == 0)
                return false;

            string joined = Concat(segments);
            if (!string.Equals(joined, japanese, StringComparison.Ordinal))
            {
                this.rejected.TryAdd(Spaceless(japanese), joined);
                return false;
            }

            string normalized = TranslationMap.Normalize(japanese);
            if (normalized.Length == 0)
                return false;

            this.byNormalized.TryAdd(normalized, segments);

            string spaceless = normalized.Replace(" ", "");
            if (spaceless.Length > 0 && this.bySpaceless.TryAdd(spaceless, segments))
                this.sortedSpaceless = null;

            if (japanese.IndexOf('{') >= 0)
                this.AddTemplate(spaceless, segments);

            this.looseCache.Clear();
            return true;
        }

        /// <summary>
        /// Registers a segmented string as a token template, if it is one. Only a template whose
        /// tokens each sit inside one segment qualifies: a token split across two segments can't be
        /// filled in without guessing where its value divides.
        /// </summary>
        private void AddTemplate(string spaceless, IReadOnlyList<TextSegment> segments)
        {
            int wholeTokens = TranslationMap.TokenPattern.Matches(spaceless).Count;
            if (wholeTokens == 0)
                return;

            int segmentTokens = 0;
            foreach (TextSegment segment in segments)
                segmentTokens += TranslationMap.TokenPattern.Matches(segment.Text).Count;
            if (segmentTokens != wholeTokens)
                return;

            var matcher = TranslationMap.BuildMatcher(spaceless);
            if (matcher is null)
                return;

            this.templates.Add(new SegmentTemplate(matcher, TranslationMap.LongestLiteralOf(spaceless), TranslationMap.LiteralLengthOf(spaceless), segments));
            this.templatesNeedSorting = true;
        }

        /// <summary>
        /// Looks up segments for a string as it was drawn -- which, like translation lookup, may
        /// carry newlines the game inserted when wrapping.
        ///
        /// Tries, in order: the exact string; the string ignoring whitespace; a token template the
        /// game filled in; and a longer indexed string the drawn text is the start of (mail, whose
        /// stored text carries a trailing <c>%item ... %%[#]title</c> the game strips before drawing).
        /// Whatever is found is then re-laid over the drawn text, so the returned segments always
        /// reproduce the drawn lines exactly -- whitespace the game added or dropped included --
        /// which is what <see cref="TryGetSegmentsForLine"/> requires.
        /// </summary>
        public bool TryGetSegments(string? displayedText, out IReadOnlyList<TextSegment> segments)
        {
            segments = Array.Empty<TextSegment>();

            if (string.IsNullOrWhiteSpace(displayedText))
                return false;

            string drawn = Unwrapped(displayedText);

            // the common case, and the only one that allocates nothing per frame
            string normalized = TranslationMap.Normalize(displayedText);
            if (this.byNormalized.TryGetValue(normalized, out var direct) && Joins(direct, drawn))
            {
                segments = direct;
                return true;
            }

            if (!this.looseCache.TryGetValue(drawn, out var loose))
            {
                string spaceless = RemoveAllWhitespace(drawn);

                IReadOnlyList<TextSegment>? found = direct;
                if (found is null && spaceless.Length > 0)
                {
                    if (!this.bySpaceless.TryGetValue(spaceless, out found))
                        found = this.MatchTemplate(spaceless) ?? this.MatchPrefix(spaceless);
                }

                loose = found is null ? null : Retile(found, drawn);

                if (this.looseCache.Count >= LooseCacheLimit)
                    this.looseCache.Clear();
                this.looseCache[drawn] = loose;
            }

            if (loose is null)
                return false;

            segments = loose;
            return true;
        }

        /// <summary>Fills in the first template the drawn text matches, most-literal first.</summary>
        private IReadOnlyList<TextSegment>? MatchTemplate(string spaceless)
        {
            if (this.templatesNeedSorting)
            {
                // a specific template ("手持ちのお金：{0}G") must be tried before a loose one ("{0} 牧場")
                this.templates.Sort((a, b) => b.LiteralLength.CompareTo(a.LiteralLength));
                this.templatesNeedSorting = false;
            }

            foreach (SegmentTemplate template in this.templates)
            {
                if (!spaceless.Contains(template.Anchor, StringComparison.Ordinal))
                    continue;

                Match match;
                try
                {
                    match = template.Matcher.Match(spaceless);
                }
                catch (RegexMatchTimeoutException)
                {
                    continue;
                }

                if (match.Success)
                    return this.Fill(template.Segments, match);
            }

            return null;
        }

        /// <summary>
        /// Substitutes a match's captured values into a template's segments. A segment that is
        /// nothing but a token whose value is itself segmented data -- the season in
        /// "{2}年目、{0}日、{1}", an item name inside a sentence -- takes that data's boundaries and glosses.
        /// </summary>
        private IReadOnlyList<TextSegment> Fill(IReadOnlyList<TextSegment> segments, Match match)
        {
            var result = new List<TextSegment>(segments.Count);

            foreach (TextSegment segment in segments)
            {
                Match lone = TranslationMap.TokenPattern.Match(segment.Text);
                if (lone.Success && lone.Length == segment.Text.Length)
                {
                    Group value = match.Groups[$"t{lone.Groups[1].Value}"];
                    if (value.Success && this.bySpaceless.TryGetValue(value.Value, out var inner))
                    {
                        result.AddRange(inner);
                        continue;
                    }
                }

                result.Add(segment with
                {
                    Text = FillTokens(segment.Text, match),
                    Gloss = segment.Gloss is null ? null : FillTokens(segment.Gloss, match),
                });
            }

            return result;
        }

        private static string FillTokens(string text, Match match)
        {
            return TranslationMap.TokenPattern.Replace(text, token =>
            {
                Group value = match.Groups[$"t{token.Groups[1].Value}"];
                return value.Success ? value.Value : token.Value;
            });
        }

        /// <summary>
        /// The indexed string the drawn text is the start of, if any. Covers text the game draws
        /// only part of: mail with its trailing item/title commands stripped, one page of a longer
        /// dialogue.
        /// </summary>
        private IReadOnlyList<TextSegment>? MatchPrefix(string spaceless)
        {
            if (spaceless.Length < MinimumPrefixLength)
                return null;

            if (this.sortedSpaceless is null)
            {
                this.sortedSpaceless = new string[this.bySpaceless.Count];
                this.bySpaceless.Keys.CopyTo(this.sortedSpaceless, 0);
                Array.Sort(this.sortedSpaceless, StringComparer.Ordinal);
            }

            // every string starting with the drawn text sorts at or just after it
            int at = Array.BinarySearch(this.sortedSpaceless, spaceless, StringComparer.Ordinal);
            if (at < 0)
                at = ~at;

            if (at < this.sortedSpaceless.Length && this.sortedSpaceless[at].StartsWith(spaceless, StringComparison.Ordinal))
                return this.bySpaceless[this.sortedSpaceless[at]];

            return null;
        }

        /// <summary>
        /// Re-lays segments over the drawn text, matching them character for character but ignoring
        /// whitespace on both sides: each segment takes the drawn characters it covers plus any
        /// whitespace that follows them. Segments past the end of the drawn text are dropped (and
        /// the last one clipped), for prefix matches. Returns null if the visible characters differ,
        /// or the data runs out before the drawn text does.
        /// </summary>
        private static IReadOnlyList<TextSegment>? Retile(IReadOnlyList<TextSegment> source, string drawn)
        {
            var visible = new List<int>(drawn.Length);
            for (int i = 0; i < drawn.Length; i++)
            {
                if (!char.IsWhiteSpace(drawn[i]))
                    visible.Add(i);
            }

            var result = new List<TextSegment>(source.Count);
            int consumed = 0;
            int start = 0;

            foreach (TextSegment segment in source)
            {
                if (consumed >= visible.Count)
                    break;

                foreach (char c in segment.Text)
                {
                    if (char.IsWhiteSpace(c))
                        continue;
                    if (consumed >= visible.Count)
                        break;
                    if (drawn[visible[consumed]] != c)
                        return null;
                    consumed++;
                }

                int end = consumed >= visible.Count ? drawn.Length : visible[consumed];
                if (end > start)
                    result.Add(segment with { Text = drawn.Substring(start, end - start) });
                start = end;
            }

            if (consumed < visible.Count || result.Count == 0)
                return null;

            return result;
        }

        /// <summary>Whether the segments' text joins to exactly <paramref name="text"/>, without allocating.</summary>
        private static bool Joins(IReadOnlyList<TextSegment> segments, string text)
        {
            int position = 0;
            foreach (TextSegment segment in segments)
            {
                if (position + segment.Text.Length > text.Length
                    || string.CompareOrdinal(segment.Text, 0, text, position, segment.Text.Length) != 0)
                    return false;
                position += segment.Text.Length;
            }

            return position == text.Length;
        }

        /// <summary>The drawn text with the game's wrap newlines removed: what <see cref="TryGetSegmentsForLine"/> compares against.</summary>
        private static string Unwrapped(string displayedText) => string.Concat(TextHitTest.SplitLines(displayedText));

        private static string RemoveAllWhitespace(string text)
        {
            var builder = new System.Text.StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (!char.IsWhiteSpace(c))
                    builder.Append(c);
            }

            return builder.ToString();
        }

        /// <param name="Anchor">The longest literal run, which any matching text must contain; far cheaper to rule out than the regex.</param>
        private readonly record struct SegmentTemplate(Regex Matcher, string Anchor, int LiteralLength, IReadOnlyList<TextSegment> Segments);

        /// <summary>
        /// Says why <see cref="TryGetSegments"/> found nothing for a drawn string, for the hover log:
        /// either its data was rejected at load, or there's none, in which case the indexed string
        /// sharing the longest prefix with it is named -- the usual cause of a miss is text the game
        /// assembled at draw time (a filled-in name, a picked gender variant, one page of a longer
        /// dialogue), and the point where the nearest string diverges shows where that happened.
        ///
        /// Scans every entry, so call it once per logged miss, never per frame.
        /// </summary>
        public string ExplainMiss(string? displayedText)
        {
            if (string.IsNullOrWhiteSpace(displayedText))
                return "the drawn text is blank";

            string drawn = Spaceless(displayedText);

            if (this.rejected.TryGetValue(drawn, out string? joined))
                return $"segment data exists but was rejected at load: its segments join to {Quote(joined)}, not the source text";

            string? nearest = null;
            int shared = 0;
            foreach (string candidate in this.bySpaceless.Keys)
            {
                int common = CommonPrefixLength(drawn, candidate);
                if (common > shared)
                {
                    shared = common;
                    nearest = candidate;
                }
            }

            if (nearest is null)
                return "no segment data for this text, and no indexed string starts with the same character";

            if (shared == drawn.Length)
                return $"no segment data for this text; it is a prefix of the indexed {Quote(nearest)} (drawn text may be one page or a truncation of it)";

            return $"no segment data for this text; nearest indexed string shares the first {shared} of {drawn.Length} character(s), "
                   + $"then has {Quote(Excerpt(nearest, shared))} where the drawn text has {Quote(Excerpt(drawn, shared))}: {Quote(nearest)}";
        }

        /// <summary>
        /// Says why <see cref="TryGetSegmentsForLine"/> refused to map data it had found -- almost
        /// always because the rendered lines don't join back into the segmented source exactly,
        /// e.g. a space the game dropped or added at a wrap.
        /// </summary>
        public static string ExplainLineMismatch(IReadOnlyList<TextSegment> segments, string[] lines, int lineIndex)
        {
            if (lines is null || lineIndex < 0 || lineIndex >= lines.Length)
                return $"line {lineIndex} is out of range for {lines?.Length ?? 0} drawn line(s)";

            string data = Concat(segments);
            string drawn = string.Concat(lines);

            if (data == drawn)
                return "segment data matches the drawn text, but no segment falls on this line";

            int at = CommonPrefixLength(data, drawn);
            return $"segment data found, but the drawn lines don't join back into it: they differ at character {at}, "
                   + $"where the data has {Quote(Excerpt(data, at))} and the drawn lines have {Quote(Excerpt(drawn, at))}";
        }

        /// <summary>
        /// Maps whole-string segments onto one wrapped line.
        ///
        /// Segmentation describes the unwrapped source, but hit-testing happens per rendered line, so
        /// the segments have to be walked against the concatenated lines and cut wherever a line
        /// break falls mid-segment. Returns false if the two don't line up character-for-character,
        /// which is the caller's signal to fall back to the heuristic rather than draw a box in the
        /// wrong place.
        /// </summary>
        public static bool TryGetSegmentsForLine(IReadOnlyList<TextSegment> segments, string[] lines, int lineIndex, out IReadOnlyList<TextSegment> lineSegments)
        {
            lineSegments = Array.Empty<TextSegment>();

            if (segments is null || lines is null || lineIndex < 0 || lineIndex >= lines.Length)
                return false;

            // the rendered lines, minus the break characters, must be exactly the segmented source
            if (Concat(segments) != string.Concat(lines))
                return false;

            int lineStart = 0;
            for (int i = 0; i < lineIndex; i++)
                lineStart += lines[i].Length;

            int lineEnd = lineStart + lines[lineIndex].Length;

            var result = new List<TextSegment>();
            int position = 0;

            foreach (TextSegment segment in segments)
            {
                int segmentStart = position;
                int segmentEnd = position + segment.Text.Length;
                position = segmentEnd;

                // clip the segment to this line; a segment straddling the break contributes to both
                int from = Math.Max(segmentStart, lineStart);
                int to = Math.Min(segmentEnd, lineEnd);
                if (to <= from)
                    continue;

                result.Add(segment.Clip(from - segmentStart, to - from));
            }

            if (result.Count == 0)
                return false;

            lineSegments = result;
            return true;
        }

        private static string Spaceless(string text) => TranslationMap.Normalize(text).Replace(" ", "");

        private static int CommonPrefixLength(string a, string b)
        {
            int length = Math.Min(a.Length, b.Length);
            int i = 0;
            while (i < length && a[i] == b[i])
                i++;
            return i;
        }

        /// <summary>A few characters from <paramref name="start"/>, or a marker for the end of the string.</summary>
        private static string Excerpt(string text, int start, int length = 8)
        {
            if (start >= text.Length)
                return "<end>";
            return text.Length - start <= length ? text.Substring(start) : text.Substring(start, length) + "…";
        }

        /// <summary>Quotes text for the log with its line breaks visible, since those are often what differs.</summary>
        public static string Quote(string text) => "「" + text.Replace("\r", "\\r").Replace("\n", "\\n") + "」";

        /// <summary>The text of every segment, joined -- what the segments must reproduce exactly.</summary>
        public static string Concat(IReadOnlyList<TextSegment> segments)
        {
            var builder = new System.Text.StringBuilder();
            foreach (var segment in segments)
                builder.Append(segment.Text);
            return builder.ToString();
        }
    }
}
