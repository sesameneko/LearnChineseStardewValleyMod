using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using LanguageStudyStardewValleyMod.Patches;
using StardewModdingAPI;
using StardewValley;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>
    /// The M3 proof of concept: hit-tests the mouse against the text
    /// <see cref="TextCapturePatches"/> recorded this frame and outlines the word underneath it.
    ///
    /// No translation is wired up on purpose -- this exists to prove that the word under the cursor
    /// can be identified at all. The placement math it relies on lives in <see cref="TextHitTest"/>,
    /// which is game-free and unit-tested.
    /// </summary>
    public static class WordHoverOverlay
    {
        /// <summary>The word found under the cursor on the last draw, for logging.</summary>
        public static string? LastHitWord { get; private set; }

        public static void Draw(SpriteBatch spriteBatch)
        {
            if (!TextCapturePatches.Enabled)
                return;

            try
            {
                using (TextCapturePatches.SuppressRecording())
                {
                    var hit = FindWordUnderCursor();
                    LastHitWord = hit?.Word;

                    if (hit is null)
                        return;

                    // the whole line, faintly, then the word itself -- makes a wrong line and a
                    // wrong word within the right line distinguishable at a glance
                    DrawOutline(spriteBatch, hit.Value.Line, Color.Cyan * 0.35f, thickness: 2);
                    DrawOutline(spriteBatch, hit.Value.Word_Bounds, Color.Lime, thickness: 2);

                    DrawLabel(spriteBatch, hit.Value.Word, hit.Value.Word_Bounds);
                }
            }
            catch (Exception ex)
            {
                ModEntry.Log($"Error in word-hover overlay: {ex}", LogLevel.Error);
            }
        }

        private readonly record struct Hit(string Word, Rectangle Word_Bounds, Rectangle Line);

        private static Hit? FindWordUnderCursor()
        {
            float mouseX = Game1.getMouseX(ui_scale: true);
            float mouseY = Game1.getMouseY(ui_scale: true);

            var recorded = TextCapturePatches.DrawnThisFrame;

            // in reverse: the last thing drawn is the thing on top, so it wins the hit
            for (int i = recorded.Count - 1; i >= 0; i--)
            {
                var drawn = recorded[i];

                string[] lines = TextHitTest.SplitLines(drawn.Text);
                float lineHeight = drawn.LineHeight;

                int? lineIndex = TextHitTest.HitLine(lines.Length, drawn.Y, lineHeight, mouseY);
                if (lineIndex is null)
                    continue;

                string line = lines[lineIndex.Value];
                var segments = TextHitTest.SplitSegments(line);

                int? segmentIndex = TextHitTest.HitSegment(segments, drawn.MeasurePrefix, drawn.X, mouseX);
                if (segmentIndex is null)
                    continue;

                string word = segments[segmentIndex.Value];
                if (string.IsNullOrWhiteSpace(word))
                    continue; // the gap between two words, not a word

                var (left, width) = TextHitTest.SegmentExtent(segments, drawn.MeasurePrefix, segmentIndex.Value);

                float lineTop = drawn.Y + (lineIndex.Value * lineHeight);
                var wordBounds = new Rectangle((int)(drawn.X + left), (int)lineTop, (int)Math.Ceiling(width), (int)Math.Ceiling(lineHeight));
                var lineBounds = new Rectangle((int)drawn.X, (int)lineTop, (int)Math.Ceiling(drawn.MeasurePrefix(line)), (int)Math.Ceiling(lineHeight));

                return new Hit(word, wordBounds, lineBounds);
            }

            return null;
        }

        private static void DrawOutline(SpriteBatch b, Rectangle rect, Color color, int thickness)
        {
            var pixel = Game1.staminaRect;

            b.Draw(pixel, new Rectangle(rect.X, rect.Y, rect.Width, thickness), color);
            b.Draw(pixel, new Rectangle(rect.X, rect.Bottom - thickness, rect.Width, thickness), color);
            b.Draw(pixel, new Rectangle(rect.X, rect.Y, thickness, rect.Height), color);
            b.Draw(pixel, new Rectangle(rect.Right - thickness, rect.Y, thickness, rect.Height), color);
        }

        /// <summary>Shows the matched word just above its outline, so a mis-split is obvious on screen.</summary>
        private static void DrawLabel(SpriteBatch b, string word, Rectangle wordBounds)
        {
            var font = Game1.smallFont;
            Vector2 size = font.MeasureString(word);

            int x = wordBounds.X;
            int y = wordBounds.Y - (int)size.Y - 4;
            if (y < 0)
                y = wordBounds.Bottom + 4;

            b.Draw(Game1.staminaRect, new Rectangle(x - 2, y - 2, (int)size.X + 4, (int)size.Y + 4), Color.Black * 0.75f);
            b.DrawString(font, word, new Vector2(x, y), Color.Lime);
        }
    }
}
