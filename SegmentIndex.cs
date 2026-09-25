using System;
using System.Collections.Generic;

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

        public int Count => this.byNormalized.Count;

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
            if (spaceless.Length > 0)
                this.bySpaceless.TryAdd(spaceless, segments);

            return true;
        }

        /// <summary>
        /// Looks up segments for a string as it was drawn -- which, like translation lookup, may
        /// carry newlines the game inserted when wrapping.
        /// </summary>
        public bool TryGetSegments(string? displayedText, out IReadOnlyList<TextSegment> segments)
        {
            segments = Array.Empty<TextSegment>();

            if (string.IsNullOrWhiteSpace(displayedText))
                return false;

            string normalized = TranslationMap.Normalize(displayedText);
            if (this.byNormalized.TryGetValue(normalized, out var direct))
            {
                segments = direct;
                return true;
            }

            string spaceless = normalized.Replace(" ", "");
            if (spaceless.Length > 0 && this.bySpaceless.TryGetValue(spaceless, out var unwrapped))
            {
                segments = unwrapped;
                return true;
            }

            return false;
        }

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
