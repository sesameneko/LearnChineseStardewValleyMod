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
    /// <param name="Reading">
    /// Pinyin as stored: one syllable per hanzi, space-separated, with tone marks ("mù chǎng").
    /// <see cref="Pinyin"/> turns it into what's drawn.
    /// </param>
    /// <param name="Gloss">What the word means *in this sentence*, e.g. "to plant".</param>
    /// <param name="Source">
    /// The authored entry this segment was written in, for pointing a flashcard back at the sentence
    /// a word was saved from. Null where the segment isn't a slice of one entry: the clock, the
    /// character-class fallback, and a token the game filled in.
    /// </param>
    public readonly record struct TextSegment(string Text, string? Reading, string? Gloss, SegmentSource? Source = null)
    {
        public static TextSegment Plain(string text) => new(text, null, null);

        /// <summary>
        /// The same segment covering only part of its text, for one side of a line break. The source
        /// still names the whole authored segment: half a wrapped word is still that word.
        /// </summary>
        public TextSegment Clip(int start, int length) => this with { Text = this.Text.Substring(start, length) };
    }

    /// <summary>
    /// Where an authored segment sits: entry <paramref name="Key"/> of the segment file
    /// <paramref name="Table"/>, characters <paramref name="Offset"/> to
    /// <paramref name="Offset"/> + <paramref name="Length"/> of its <c>chinese</c> string.
    /// </summary>
    public sealed record SegmentSource(string Table, string Key, int Offset, int Length);

    /// <summary>
    /// Exact word boundaries for strings we have hand-segmented data for, keyed by the source text.
    ///
    /// Japanese word boundaries can't be derived by rule, so <see cref="TextHitTest.SplitSegments"/>'s
    /// character-class heuristic is only ever a fallback: it blobs an unbroken kanji or hiragana run
    /// into one unit. Where data exists (every string the game ships) this supplies the real boundaries
    /// instead.
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

        /// <summary>
        /// The shortest whole entry the composite matcher will place as a piece. Guards against a
        /// one-character entry landing by coincidence and bringing the wrong gloss: 日 is stored as
        /// "Sunday", but in 8日 it means "day".
        /// </summary>
        private const int MinimumPieceLength = 2;

        /// <summary>
        /// The shortest run the composite matcher will take from the *middle* of a longer entry (a
        /// dialogue page). Longer than <see cref="MinimumPieceLength"/> because a short run of
        /// characters appears somewhere in the 17k entries by chance far more often than a short
        /// entry equals the text outright.
        /// </summary>
        private const int MinimumSubstringLength = 6;

        /// <summary>How much of the drawn text a composite match must cover with known pieces; below this it's likely coincidence.</summary>
        private const double MinimumCompositeCoverage = 0.7;

        public int Count => this.byNormalized.Count;

        /// <summary>Every authored entry by table and key, for resolving a flashcard's context pointer.</summary>
        public SourceEntries Entries { get; } = new();

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

            // the composite matcher places templates mid-text, so it needs the same pattern
            // unanchored at the end and pinned to a start position; a trailing token would then
            // capture a single character, so such templates are left out of composites
            Regex? pieceMatcher = null;
            string whole = matcher.ToString();
            if (!spaceless.EndsWith("}", StringComparison.Ordinal) && whole.StartsWith("^", StringComparison.Ordinal) && whole.EndsWith("$", StringComparison.Ordinal))
                pieceMatcher = new Regex(@"\G" + whole.Substring(1, whole.Length - 2), matcher.Options, matcher.MatchTimeout);

            this.templates.Add(new SegmentTemplate(matcher, pieceMatcher, TranslationMap.LongestLiteralOf(spaceless), TranslationMap.LiteralLengthOf(spaceless), segments));
            this.templatesNeedSorting = true;
        }

        /// <summary>
        /// Looks up segments for a string as it was drawn -- which, like translation lookup, may
        /// carry newlines the game inserted when wrapping.
        ///
        /// Tries, in order: the exact string; the string ignoring whitespace; a token template the
        /// game filled in; and a longer indexed string the drawn text is the start of (mail, whose
        /// stored text carries a trailing <c>%item ... %%[#]title</c> the game strips before drawing);
        /// and last, a composite of several known pieces (see <see cref="MatchComposite"/>).
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
                        found = this.MatchTemplate(spaceless) ?? this.MatchPrefix(spaceless) ?? this.MatchComposite(spaceless);
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

                bool hasToken = TranslationMap.TokenPattern.IsMatch(segment.Text);
                result.Add(segment with
                {
                    Text = FillTokens(segment.Text, match),
                    Gloss = segment.Gloss is null ? null : FillTokens(segment.Gloss, match),
                    // a filled-in value is text no entry holds
                    Source = hasToken ? null : segment.Source,
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
        /// Covers text the game assembled from several entries -- a quest's description built from
        /// four table strings plus an item and an NPC name, a clothing description followed by
        /// "可染性。", the second page of a dialogue -- with known pieces laid end to end.
        ///
        /// A piece is a whole entry (at least <see cref="MinimumPieceLength"/> characters), a token
        /// template filled in mid-text, or a run of at least <see cref="MinimumSubstringLength"/>
        /// characters from inside a longer entry. Among every way of tiling the text, the one that
        /// covers the most characters wins, then the one using fewest pieces, since a long piece is
        /// likelier to be the real source than several short ones. Characters no piece covers keep
        /// the character-class split. The result is rejected unless pieces cover
        /// <see cref="MinimumCompositeCoverage"/> of the text.
        ///
        /// Expensive -- a substring scan over every entry per run start -- but only reached after
        /// every other lookup failed, and memoised by the caller.
        /// </summary>
        private IReadOnlyList<TextSegment>? MatchComposite(string drawn)
        {
            int length = drawn.Length;
            if (length < MinimumPieceLength)
                return null;

            // only templates whose longest literal appears somewhere in the text can be placed
            var usableTemplates = new List<SegmentTemplate>();
            foreach (SegmentTemplate template in this.templates)
            {
                if (template.PieceMatcher is not null && drawn.Contains(template.Anchor, StringComparison.Ordinal))
                    usableTemplates.Add(template);
            }

            var candidates = new List<Piece>[length];
            SubstringRun previousRun = default;

            for (int start = 0; start < length; start++)
            {
                var here = candidates[start] = new List<Piece>();

                // whole entries starting here, every length
                for (int end = start + MinimumPieceLength; end <= length; end++)
                {
                    if (this.bySpaceless.TryGetValue(drawn.Substring(start, end - start), out var entry))
                        here.Add(new Piece(end - start, end - start, entry));
                }

                // templates filled in from here
                foreach (SegmentTemplate template in usableTemplates)
                {
                    Match match;
                    try
                    {
                        match = template.PieceMatcher!.Match(drawn, start);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        continue;
                    }

                    if (match.Success && match.Index == start && match.Length >= MinimumPieceLength)
                        here.Add(new Piece(match.Length, this.CoveredBy(template.PieceMatcher!, match), this.Fill(template.Segments, match)));
                }

                // the longest run from inside some entry; a run continuing from the previous
                // position is just that run one character shorter, so it isn't searched again
                SubstringRun run = previousRun.Length > MinimumSubstringLength
                    ? previousRun with { Offset = previousRun.Offset + 1, Length = previousRun.Length - 1 }
                    : this.LongestRunFrom(drawn, start);
                if (run.Length >= MinimumSubstringLength)
                    here.Add(new Piece(run.Length, run.Length, null, run.Key, run.Offset));
                previousRun = run;
            }

            // best[i]: the best tiling of drawn[i..], by (covered characters desc, pieces asc)
            var covered = new int[length + 1];
            var pieces = new int[length + 1];
            var choice = new Piece?[length + 1];

            for (int start = length - 1; start >= 0; start--)
            {
                // leaving this character uncovered
                covered[start] = covered[start + 1];
                pieces[start] = pieces[start + 1];
                choice[start] = null;

                foreach (Piece piece in candidates[start])
                {
                    int end = start + piece.Length;
                    int total = covered[end] + piece.Covered;
                    int count = pieces[end] + 1;
                    if (total > covered[start] || (total == covered[start] && count < pieces[start]))
                    {
                        covered[start] = total;
                        pieces[start] = count;
                        choice[start] = piece;
                    }
                }
            }

            if (covered[0] < length * MinimumCompositeCoverage)
                return null;

            var result = new List<TextSegment>();
            var gap = new System.Text.StringBuilder();
            for (int position = 0; position < length;)
            {
                if (choice[position] is not Piece piece)
                {
                    gap.Append(drawn[position]);
                    position++;
                    continue;
                }

                FlushGap(gap, result);
                result.AddRange(piece.Segments ?? SliceVisible(this.bySpaceless[piece.SourceKey!], piece.SourceOffset, piece.Length));
                position += piece.Length;
            }

            FlushGap(gap, result);
            return result;
        }

        /// <summary>
        /// How many characters a placed template really accounts for: its literal text, plus any
        /// captured value that is itself a known entry. An unknown value (a number, a player's name)
        /// counts for nothing -- otherwise a template opening with a token ("{0} 牧場") could swallow
        /// a whole sentence up to its literal and outscore the entries that sentence is really made of.
        /// </summary>
        private int CoveredBy(Regex matcher, Match match)
        {
            int covered = match.Length;
            foreach (string name in matcher.GetGroupNames())
            {
                if (name.Length < 2 || name[0] != 't')
                    continue;

                Group value = match.Groups[name];
                if (value.Success && !this.bySpaceless.ContainsKey(value.Value))
                    covered -= value.Length;
            }

            return covered;
        }

        /// <summary>Uncovered characters keep the character-class split, as they would with no data at all.</summary>
        private static void FlushGap(System.Text.StringBuilder gap, List<TextSegment> result)
        {
            if (gap.Length == 0)
                return;

            foreach (string part in TextHitTest.SplitSegments(gap.ToString()))
                result.Add(TextSegment.Plain(part));
            gap.Clear();
        }

        /// <summary>
        /// The longest run of <paramref name="drawn"/> from <paramref name="start"/> found inside any
        /// entry, by scanning every entry for its first <see cref="MinimumSubstringLength"/> characters.
        /// </summary>
        private SubstringRun LongestRunFrom(string drawn, int start)
        {
            if (drawn.Length - start < MinimumSubstringLength)
                return default;

            string probe = drawn.Substring(start, MinimumSubstringLength);
            SubstringRun best = default;

            foreach (string key in this.bySpaceless.Keys)
            {
                int offset = key.IndexOf(probe, StringComparison.Ordinal);
                if (offset < 0)
                    continue;

                int run = MinimumSubstringLength;
                while (start + run < drawn.Length && offset + run < key.Length && drawn[start + run] == key[offset + run])
                    run++;

                if (run > best.Length)
                    best = new SubstringRun(key, offset, run);
            }

            return best;
        }

        /// <summary>
        /// The segments covering visible (non-whitespace) characters <paramref name="from"/> to
        /// <paramref name="from"/> + <paramref name="count"/> of an entry, clipping the segments at
        /// either end. Whitespace inside the range is kept; <see cref="Retile"/> re-lays it anyway.
        /// </summary>
        private static IReadOnlyList<TextSegment> SliceVisible(IReadOnlyList<TextSegment> segments, int from, int count)
        {
            int to = from + count;
            var result = new List<TextSegment>();
            int visible = 0;

            foreach (TextSegment segment in segments)
            {
                int first = -1, last = -1;
                for (int i = 0; i < segment.Text.Length; i++)
                {
                    if (char.IsWhiteSpace(segment.Text[i]))
                        continue;
                    if (visible >= from && visible < to)
                    {
                        if (first < 0)
                            first = i;
                        last = i;
                    }
                    visible++;
                }

                if (first >= 0)
                    result.Add(segment.Clip(first, last - first + 1));
                if (visible >= to)
                    break;
            }

            return result;
        }

        /// <summary>A candidate piece of a composite: a known length of the drawn text and where its segments come from.</summary>
        /// <param name="Covered">How many of its characters count towards the tiling's score; see <see cref="CoveredBy"/>.</param>
        /// <param name="Segments">The segments, for a whole entry or a filled-in template.</param>
        /// <param name="SourceKey">For a run from inside a longer entry: that entry, sliced only if the piece is chosen.</param>
        private readonly record struct Piece(int Length, int Covered, IReadOnlyList<TextSegment>? Segments, string? SourceKey = null, int SourceOffset = 0);

        private readonly record struct SubstringRun(string Key, int Offset, int Length);

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
        /// <param name="PieceMatcher">The same pattern for use mid-text by the composite matcher (<c>\G</c>-anchored, open-ended); null when unusable there.</param>
        private readonly record struct SegmentTemplate(Regex Matcher, Regex? PieceMatcher, string Anchor, int LiteralLength, IReadOnlyList<TextSegment> Segments);

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
