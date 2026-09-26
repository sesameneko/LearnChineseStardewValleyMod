using System;
using System.Collections.Generic;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>
    /// One character the game actually drew, and the cell it occupies on screen: from the pen
    /// position where the renderer drew it to where the pen stood once it had advanced past it,
    /// by one line of height. Cells of neighbouring characters on a line tile it without gaps, so
    /// the cursor is always over some character rather than falling between glyphs.
    /// </summary>
    /// <param name="Index">The character's position in the drawn text, newlines included.</param>
    public readonly record struct GlyphCell(int Index, float Left, float Top, float Right, float Bottom)
    {
        public bool Contains(float x, float y) => x >= this.Left && x < this.Right && y >= this.Top && y < this.Bottom;

        /// <summary>Whether two cells sit on the same rendered line.</summary>
        public bool SameLineAs(GlyphCell other) => Math.Abs(this.Top - other.Top) < 0.5f;
    }

    /// <summary>An axis-aligned box in UI pixels; a stand-in for MonoGame's Rectangle, which this file can't reference.</summary>
    public readonly record struct Box(float Left, float Top, float Right, float Bottom);

    /// <summary>
    /// Word-level hit-testing against glyph positions recorded from inside the game's own text
    /// renderers (see Patches/GlyphCapturePatches.cs).
    ///
    /// This replaces re-deriving the layout -- re-wrapping the text, measuring prefixes, assuming a
    /// line height -- with reading back where each character actually landed, so line breaks,
    /// kinsoku, skipped glyphs and the dialogue typewriter are all whatever the renderer did.
    ///
    /// Game-free so it can be unit-tested; the game-side half only supplies the cells.
    /// </summary>
    public static class GlyphHitTest
    {
        /// <summary>The position in <paramref name="cells"/> of the cell under a point, if any.</summary>
        public static int? HitCell(IReadOnlyList<GlyphCell> cells, float x, float y)
        {
            for (int i = 0; i < cells.Count; i++)
            {
                if (cells[i].Contains(x, y))
                    return i;
            }

            return null;
        }

        /// <summary>
        /// Maps an index into the drawn text onto the same character in its unwrapped form -- the
        /// text with line breaks removed exactly as <c>TextHitTest.SplitLines</c> removes them
        /// ("\r\n" and "\n"), which is what segment data is laid over.
        /// </summary>
        public static int ToUnwrappedIndex(string text, int index)
        {
            int removed = 0;
            for (int i = 0; i < index && i < text.Length; i++)
            {
                if (text[i] == '\n' || (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n'))
                    removed++;
            }

            return index - removed;
        }

        /// <summary>Which segment covers a character of the unwrapped text, with that segment's start.</summary>
        public static (int Segment, int Start)? SegmentAt(IReadOnlyList<string> segmentTexts, int unwrappedIndex)
        {
            if (unwrappedIndex < 0)
                return null;

            int position = 0;
            for (int i = 0; i < segmentTexts.Count; i++)
            {
                int end = position + segmentTexts[i].Length;
                if (unwrappedIndex < end)
                    return (i, position);
                position = end;
            }

            return null;
        }

        /// <summary>
        /// The box round the drawn characters of an unwrapped span that share a line with
        /// <paramref name="onLineOf"/> -- a word the renderer broke across two lines is outlined
        /// only on the line being hovered. Null when none of it was drawn on that line.
        /// </summary>
        public static Box? SpanBounds(IReadOnlyList<GlyphCell> cells, string text, int unwrappedStart, int unwrappedLength, GlyphCell onLineOf)
        {
            int unwrappedEnd = unwrappedStart + unwrappedLength;
            Box? bounds = null;

            foreach (GlyphCell cell in cells)
            {
                if (!cell.SameLineAs(onLineOf))
                    continue;

                int at = ToUnwrappedIndex(text, cell.Index);
                if (at < unwrappedStart || at >= unwrappedEnd)
                    continue;

                bounds = Union(bounds, cell);
            }

            return bounds;
        }

        /// <summary>The box round every drawn character on the same line as <paramref name="onLineOf"/>.</summary>
        public static Box LineBounds(IReadOnlyList<GlyphCell> cells, GlyphCell onLineOf)
        {
            Box? bounds = null;
            foreach (GlyphCell cell in cells)
            {
                if (cell.SameLineAs(onLineOf))
                    bounds = Union(bounds, cell);
            }

            return bounds ?? new Box(onLineOf.Left, onLineOf.Top, onLineOf.Right, onLineOf.Bottom);
        }

        private static Box Union(Box? box, GlyphCell cell)
        {
            return box is { } b
                ? new Box(Math.Min(b.Left, cell.Left), Math.Min(b.Top, cell.Top), Math.Max(b.Right, cell.Right), Math.Max(b.Bottom, cell.Bottom))
                : new Box(cell.Left, cell.Top, cell.Right, cell.Bottom);
        }
    }
}
