using System;
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
