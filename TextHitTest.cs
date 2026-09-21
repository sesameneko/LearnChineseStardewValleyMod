using System;
using System.Text;
using System.Collections.Generic;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>
    /// Word-level hit-testing inside a string that the game has already drawn.
    ///
    /// Deliberately free of StardewValley/MonoGame types so it can be unit-tested without launching
    /// the game: the two things that *do* need the game -- measuring text and knowing where it was
    /// drawn -- are passed in, as a measuring delegate and plain coordinates. See
    /// Patches/TextCapturePatches.cs for the game-side half.
    /// </summary>
    public static class TextHitTest
    {
        /// <summary>The character classes segment boundaries are drawn between.</summary>
        private enum CharClass { Whitespace, Hiragana, Katakana, Ideograph, Latin, Punctuation, Other }

        /// <summary>
        /// Wraps text the way SpriteText does when it's given a width, by inserting newlines.
        ///
        /// SpriteFont text arrives already wrapped -- the game runs it through Game1.parseText,
        /// which inserts real newlines -- but SpriteText wraps *internally* while drawing, so the
        /// string handed to it is a single unbroken line and the mod saw it as one. That put every
        /// word after the first line break in the wrong place: the hit-test measured its x as though
        /// the whole string ran off the right of the screen.
        ///
        /// Only newlines are inserted -- never a character removed, including the space a line
        /// break lands on. SegmentIndex.TryGetSegmentsForLine requires the rendered lines to
        /// re-concatenate into exactly the segmented source, so dropping a space would silently
        /// cost every gloss on the string.
        /// </summary>
        /// <param name="breakAnywhere">
        /// Whether a line may break between any two characters, rather than only at spaces.
        /// Japanese breaks mid-word even when a space is available earlier in the line -- vanilla
        /// renders "ガタガタの ベッ" / "ドに" -- so this is set by locale, not by the text.
        /// </param>
        public static string WrapToWidth(string? text, int width, bool breakAnywhere, Func<string, int> measure)
        {
            if (string.IsNullOrEmpty(text) || width <= 0)
                return text ?? string.Empty;

            var wrapped = new StringBuilder(text.Length + 8);
            int lineStart = 0;      // index in `wrapped` where the current line begins
            int lastSpace = -1;     // index in `wrapped` of the last space on the current line

            foreach (char c in text)
            {
                if (c == '\n')
                {
                    wrapped.Append(c);
                    lineStart = wrapped.Length;
                    lastSpace = -1;
                    continue;
                }

                wrapped.Append(c);

                if (c == ' ')
                    lastSpace = wrapped.Length - 1;

                string line = wrapped.ToString(lineStart, wrapped.Length - lineStart);
                if (measure(line) <= width || line.Length <= 1)
                    continue;

                // break before the character that overflowed, or back at the last space when the
                // locale only breaks on spaces; a line with no space breaks anywhere regardless
                int breakAt = breakAnywhere || lastSpace < 0
                    ? wrapped.Length - 1
                    : lastSpace + 1;

                if (breakAt <= lineStart)
                    breakAt = wrapped.Length - 1;

                wrapped.Insert(breakAt, '\n');
                lineStart = breakAt + 1;
                lastSpace = -1;
            }

            return wrapped.ToString();
        }

        /// <summary>Splits a drawn string into its rendered lines. The game wraps by inserting newlines.</summary>
        public static string[] SplitLines(string text)
        {
            return (text ?? "").Replace("\r\n", "\n").Split('\n');
        }

        /// <summary>
        /// Splits one line into selectable runs.
        ///
        /// Japanese has no word-boundary spaces, so runs are grouped by character class instead:
        /// consecutive kanji form one run, consecutive kana another, and so on. That gets
        /// 長時間 or オーク as single units, while splitting okurigana off its stem (植える ->
        /// 植 + える), which is imperfect -- good enough to prove hit-testing, and not what ships.
        /// The real boundaries come from the hand-segmented data in tools/extracted-strings.
        ///
        /// Whitespace runs are included rather than dropped so that the returned runs still tile the
        /// line exactly; prefix measurement depends on that. Callers should ignore blank runs when
        /// deciding what is selectable.
        /// </summary>
        public static IReadOnlyList<string> SplitSegments(string line)
        {
            var segments = new List<string>();
            if (string.IsNullOrEmpty(line))
                return segments;

            int start = 0;
            CharClass current = ClassOf(line[0]);

            for (int i = 1; i < line.Length; i++)
            {
                CharClass next = ClassOf(line[i]);
                if (next == current)
                    continue;

                segments.Add(line.Substring(start, i - start));
                start = i;
                current = next;
            }

            segments.Add(line.Substring(start));
            return segments;
        }

        /// <summary>Which rendered line (if any) a y coordinate falls on.</summary>
        public static int? HitLine(int lineCount, float topY, float lineHeight, float pointY)
        {
            if (lineCount <= 0 || lineHeight <= 0)
                return null;

            if (pointY < topY || pointY >= topY + (lineHeight * lineCount))
                return null;

            int index = (int)((pointY - topY) / lineHeight);
            return index >= 0 && index < lineCount ? index : null;
        }

        /// <summary>
        /// Which segment (if any) an x coordinate falls on.
        /// <paramref name="measurePrefix"/> is given the text from the line's start up to the end of
        /// a segment, and returns its rendered width -- so the caller supplies the real font metrics
        /// (SpriteFont.MeasureString, or SpriteText.getWidthOfString) and this stays game-free.
        /// </summary>
        public static int? HitSegment(IReadOnlyList<string> segments, Func<string, float> measurePrefix, float leftX, float pointX)
        {
            if (segments is null || segments.Count == 0 || measurePrefix is null)
                return null;

            if (pointX < leftX)
                return null;

            string prefix = "";
            float previousWidth = 0f;

            for (int i = 0; i < segments.Count; i++)
            {
                prefix += segments[i];
                float width = measurePrefix(prefix);

                if (pointX < leftX + width)
                    return pointX >= leftX + previousWidth ? i : null;

                previousWidth = width;
            }

            return null;
        }

        /// <summary>The x offset and width of one segment within its line, in rendered pixels.</summary>
        public static (float Left, float Width) SegmentExtent(IReadOnlyList<string> segments, Func<string, float> measurePrefix, int index)
        {
            string beforeSegment = Concat(segments, 0, index);
            float left = index == 0 ? 0f : measurePrefix(beforeSegment);
            float right = measurePrefix(beforeSegment + segments[index]);

            return (left, right - left);
        }

        private static string Concat(IReadOnlyList<string> segments, int start, int count)
        {
            var builder = new System.Text.StringBuilder();
            for (int i = start; i < start + count; i++)
                builder.Append(segments[i]);
            return builder.ToString();
        }

        private static CharClass ClassOf(char c)
        {
            if (char.IsWhiteSpace(c))
                return CharClass.Whitespace;

            // hiragana, plus the iteration marks that behave as part of a kana run
            if (c >= '぀' && c <= 'ゟ')
                return CharClass.Hiragana;

            // katakana, including the prolonged sound mark that ends many loanwords
            if ((c >= '゠' && c <= 'ヿ') || (c >= 'ｦ' && c <= 'ﾝ'))
                return CharClass.Katakana;

            if ((c >= '一' && c <= '鿿') || (c >= '㐀' && c <= '䶿'))
                return CharClass.Ideograph;

            if (char.IsLetterOrDigit(c))
                return CharClass.Latin;

            if (char.IsPunctuation(c) || char.IsSymbol(c) || (c >= '　' && c <= '〿'))
                return CharClass.Punctuation;

            return CharClass.Other;
        }
    }
}
