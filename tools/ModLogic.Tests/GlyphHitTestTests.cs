using System.Collections.Generic;
using System.Linq;
using LanguageStudyStardewValleyMod;

namespace ModLogic.Tests
{
    public class GlyphHitTestTests
    {
        /// <summary>
        /// Cells laid out the way a renderer would record them: 10px per character, 20px lines,
        /// a new line at every "\n" (which itself draws nothing), and every <paramref name="wrapAfter"/>
        /// characters -- a break the text itself doesn't show, like SpriteText's internal wrap.
        /// </summary>
        private static List<GlyphCell> Layout(string text, int wrapAfter = int.MaxValue)
        {
            var cells = new List<GlyphCell>();
            int column = 0, line = 0;

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    column = 0;
                    line++;
                    continue;
                }

                if (column == wrapAfter)
                {
                    column = 0;
                    line++;
                }

                cells.Add(new GlyphCell(i, column * 10, line * 20, (column + 1) * 10, (line + 1) * 20));
                column++;
            }

            return cells;
        }

        [Fact]
        public void Finds_the_cell_under_a_point()
        {
            var cells = Layout("abc");

            Assert.Equal(1, GlyphHitTest.HitCell(cells, 15, 5));
            Assert.Null(GlyphHitTest.HitCell(cells, 35, 5));
            Assert.Null(GlyphHitTest.HitCell(cells, 5, 25));
        }

        [Fact]
        public void Unwrapped_index_skips_the_line_breaks_before_it()
        {
            Assert.Equal(0, GlyphHitTest.ToUnwrappedIndex("ab\ncd", 0));
            Assert.Equal(2, GlyphHitTest.ToUnwrappedIndex("ab\ncd", 3));
            Assert.Equal(2, GlyphHitTest.ToUnwrappedIndex("ab\r\ncd", 4));
        }

        [Fact]
        public void Unwrapped_index_agrees_with_split_lines()
        {
            // the segment data is laid over string.Concat(SplitLines(text)); the mapping must land on
            // the same character there for every drawn character
            string text = "植える\r\nオークの\n木";
            string unwrapped = string.Concat(TextHitTest.SplitLines(text));

            foreach (var cell in Layout(text).Where(cell => text[cell.Index] != '\r'))
                Assert.Equal(text[cell.Index], unwrapped[GlyphHitTest.ToUnwrappedIndex(text, cell.Index)]);
        }

        [Fact]
        public void Finds_the_segment_covering_a_character()
        {
            var segments = new[] { "オーク", "の", "木" };

            Assert.Equal((0, 0), GlyphHitTest.SegmentAt(segments, 2));
            Assert.Equal((1, 3), GlyphHitTest.SegmentAt(segments, 3));
            Assert.Equal((2, 4), GlyphHitTest.SegmentAt(segments, 4));
            Assert.Null(GlyphHitTest.SegmentAt(segments, 5));
        }

        [Fact]
        public void A_word_is_boxed_where_it_was_drawn_after_a_renderer_side_wrap()
        {
            // the case the old approach got wrong: the text has no newline, but the renderer broke
            // the line after 4 characters -- "木" is at the start of line 2, not off the right edge
            string text = "オークの木が";
            var cells = Layout(text, wrapAfter: 4);
            var hovered = cells[GlyphHitTest.HitCell(cells, 5, 25)!.Value];

            Assert.Equal(4, hovered.Index);
            Assert.Equal(new Box(0, 20, 10, 40), GlyphHitTest.SpanBounds(cells, text, 4, 1, hovered));
        }

        [Fact]
        public void A_word_split_across_lines_is_boxed_only_on_the_hovered_line()
        {
            string text = "の長時間";
            var cells = Layout(text, wrapAfter: 2);   // "の長" / "時間"
            var onSecondLine = cells[3];

            Assert.Equal(new Box(0, 20, 20, 40), GlyphHitTest.SpanBounds(cells, text, 1, 3, onSecondLine));
            Assert.Equal(new Box(10, 0, 20, 20), GlyphHitTest.SpanBounds(cells, text, 1, 3, cells[1]));
        }

        [Fact]
        public void Undrawn_characters_are_not_hit()
        {
            // mid-typewriter: only the first three characters have been drawn
            string text = "オークの木";
            var cells = Layout(text).Take(3).ToList();

            Assert.Null(GlyphHitTest.HitCell(cells, 45, 5));
            Assert.Equal(new Box(0, 0, 30, 20), GlyphHitTest.SpanBounds(cells, text, 0, 3, cells[0]));
        }

        [Fact]
        public void Line_bounds_cover_only_the_hovered_line()
        {
            var cells = Layout("ab\ncde");

            Assert.Equal(new Box(0, 20, 30, 40), GlyphHitTest.LineBounds(cells, cells[3]));
        }
    }
}
