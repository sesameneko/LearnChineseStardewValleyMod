using System;
using System.Collections.Generic;

namespace LanguageStudyStardewValleyMod
{
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
        private readonly Dictionary<string, IReadOnlyList<string>> byNormalized = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<string>> bySpaceless = new(StringComparer.Ordinal);

        public int Count => this.byNormalized.Count;

        /// <summary>
        /// Adds one source string's segmentation. Rejects data that violates the concatenation
        /// invariant (segments must reproduce the source exactly), since every position calculation
        /// downstream assumes it -- bad data would silently mis-place highlights.
        /// </summary>
        public bool TryAdd(string? japanese, IReadOnlyList<string>? segments)
        {
            if (string.IsNullOrWhiteSpace(japanese) || segments is null || segments.Count == 0)
                return false;

            if (!string.Equals(string.Concat(segments), japanese, StringComparison.Ordinal))
                return false;

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
        public bool TryGetSegments(string? displayedText, out IReadOnlyList<string> segments)
        {
            segments = Array.Empty<string>();

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
        /// Maps whole-string segments onto one wrapped line.
        ///
        /// Segmentation describes the unwrapped source, but hit-testing happens per rendered line, so
        /// the segments have to be walked against the concatenated lines and cut wherever a line
        /// break falls mid-segment. Returns false if the two don't line up character-for-character,
        /// which is the caller's signal to fall back to the heuristic rather than draw a box in the
        /// wrong place.
        /// </summary>
        public static bool TryGetSegmentsForLine(IReadOnlyList<string> segments, string[] lines, int lineIndex, out IReadOnlyList<string> lineSegments)
        {
            lineSegments = Array.Empty<string>();

            if (segments is null || lines is null || lineIndex < 0 || lineIndex >= lines.Length)
                return false;

            // the rendered lines, minus the break characters, must be exactly the segmented source
            if (string.Concat(segments) != string.Concat(lines))
                return false;

            int lineStart = 0;
            for (int i = 0; i < lineIndex; i++)
                lineStart += lines[i].Length;

            int lineEnd = lineStart + lines[lineIndex].Length;

            var result = new List<string>();
            int position = 0;

            foreach (string segment in segments)
            {
                int segmentStart = position;
                int segmentEnd = position + segment.Length;
                position = segmentEnd;

                // clip the segment to this line; a segment straddling the break contributes to both
                int from = Math.Max(segmentStart, lineStart);
                int to = Math.Min(segmentEnd, lineEnd);
                if (to <= from)
                    continue;

                result.Add(segment.Substring(from - segmentStart, to - from));
            }

            if (result.Count == 0)
                return false;

            lineSegments = result;
            return true;
        }
    }
}
