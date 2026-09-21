using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

        /// <summary>Whether the last hit's boundaries came from hand-segmented data rather than the fallback heuristic.</summary>
        public static bool LastHitWasExact { get; private set; }

        /// <summary>
        /// Whether the overlay has already been drawn this frame. It gets one chance per frame, at
        /// the latest point available: right after a tooltip is drawn when there is one, otherwise
        /// the HUD/menu pass.
        /// </summary>
        public static bool DrawnThisFrame { get; set; }

        public static void Draw(SpriteBatch spriteBatch)
        {
            if (!TextCapturePatches.Enabled || DrawnThisFrame)
                return;

            DrawnThisFrame = true;

            try
            {
                using (TextCapturePatches.SuppressRecording())
                {
                    var hit = FindWordUnderCursor();
                    LastHitWord = hit?.Segment.Text;
                    LastHitWasExact = hit?.Exact ?? false;

                    if (hit is null)
                        return;

                    // green when the boundaries came from hand-segmented data, amber when they came
                    // from the character-class fallback -- so a blobbed kanji/hiragana run is
                    // recognisable on sight as missing data rather than a hit-testing bug
                    Color wordColor = hit.Value.Exact ? Color.Lime : Color.Orange;

                    // the whole line, faintly, then the word itself -- makes a wrong line and a
                    // wrong word within the right line distinguishable at a glance
                    DrawOutline(spriteBatch, hit.Value.Line, Color.Cyan * 0.35f, thickness: 2);
                    DrawOutline(spriteBatch, hit.Value.Word_Bounds, wordColor, thickness: 2);

                    DrawLabel(spriteBatch, Describe(hit.Value.Segment), hit.Value.Word_Bounds, wordColor);
                }
            }
            catch (Exception ex)
            {
                ModEntry.Log($"Error in word-hover overlay: {ex}", LogLevel.Error);
            }
        }

        private readonly record struct Hit(TextSegment Segment, Rectangle Word_Bounds, Rectangle Line, bool Exact);

        private static Hit? FindWordUnderCursor()
        {
            float mouseX = Game1.getMouseX(ui_scale: true);
            float mouseY = Game1.getMouseY(ui_scale: true);

            var recorded = TextCapturePatches.DrawnThisFrame;

            // while a tooltip is pinned, only its own text is hoverable -- otherwise the cursor
            // moving across the pinned box matches whatever else happens to be underneath it
            bool frozen = FrozenTooltip.IsFrozen;

            // in reverse: the last thing drawn is the thing on top, so it wins the hit
            for (int i = recorded.Count - 1; i >= 0; i--)
            {
                var drawn = recorded[i];

                if (frozen && !drawn.FromFrozenTooltip)
                    continue;

                string[] lines = TextHitTest.SplitLines(drawn.Text);
                float lineHeight = drawn.LineHeight;

                int? lineIndex = TextHitTest.HitLine(lines.Length, drawn.Y, lineHeight, mouseY);
                if (lineIndex is null)
                    continue;

                string line = lines[lineIndex.Value];
                var (segments, exact) = ResolveSegments(drawn.Text, lines, lineIndex.Value, line);
                var texts = segments.Select(segment => segment.Text).ToList();

                int? segmentIndex = TextHitTest.HitSegment(texts, drawn.MeasurePrefix, drawn.X, mouseX);
                if (segmentIndex is null)
                    continue;

                var segment = segments[segmentIndex.Value];
                if (string.IsNullOrWhiteSpace(segment.Text))
                    continue; // the gap between two words, not a word

                var (left, width) = TextHitTest.SegmentExtent(texts, drawn.MeasurePrefix, segmentIndex.Value);

                float lineTop = drawn.Y + (lineIndex.Value * lineHeight);
                var wordBounds = new Rectangle((int)(drawn.X + left), (int)lineTop, (int)Math.Ceiling(width), (int)Math.Ceiling(lineHeight));
                var lineBounds = new Rectangle((int)drawn.X, (int)lineTop, (int)Math.Ceiling(drawn.MeasurePrefix(line)), (int)Math.Ceiling(lineHeight));

                return new Hit(segment, wordBounds, lineBounds, exact);
            }

            return null;
        }

        /// <summary>
        /// Prefers hand-segmented boundaries, falling back to the character-class heuristic.
        ///
        /// The data describes the unwrapped source string, so it also has to be mapped onto the
        /// rendered line; if either step doesn't line up exactly, the fallback is used rather than
        /// a box drawn in the wrong place.
        /// </summary>
        /// <summary>The label text: the word plus its in-context gloss and reading where we have them.</summary>
        private static string Describe(TextSegment segment)
        {
            if (string.IsNullOrWhiteSpace(segment.Gloss))
                return segment.Text;

            return string.IsNullOrWhiteSpace(segment.Reading)
                ? $"{segment.Text}  —  {segment.Gloss}"
                : $"{segment.Text} ({segment.Reading})  —  {segment.Gloss}";
        }

        private static (IReadOnlyList<TextSegment> Segments, bool Exact) ResolveSegments(string drawnText, string[] lines, int lineIndex, string line)
        {
            var index = ModEntry.Instance?.Segments;

            if (index != null
                && index.TryGetSegments(drawnText, out var wholeString)
                && SegmentIndex.TryGetSegmentsForLine(wholeString, lines, lineIndex, out var lineSegments))
            {
                return (lineSegments, true);
            }

            return (TextHitTest.SplitSegments(line).Select(TextSegment.Plain).ToList(), false);
        }

        /// <summary>MonoGame's private sort-mode field, read to decide which layer depth is "on top".</summary>
        private static readonly FieldInfo? SortModeField =
            typeof(SpriteBatch).GetField("_sortMode", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>
        /// The layer depth that puts a sprite in front, for the batch we're actually drawing into.
        ///
        /// Which end of the range is "front" depends on the sort mode, and the game uses several:
        /// with FrontToBack the highest depth wins, with BackToFront the lowest does, and with
        /// Deferred depth is ignored entirely and call order decides. Guessing a constant put the
        /// overlay behind the very tooltip it annotates -- once as 0f, once as 1f -- so it's read
        /// from the batch instead. Tooltips themselves draw at 0.9-0.95.
        /// </summary>
        private static float TopLayerDepth(SpriteBatch b)
        {
            try
            {
                if (SortModeField?.GetValue(b) is SpriteSortMode mode)
                    return mode == SpriteSortMode.BackToFront ? 0f : 1f;
            }
            catch
            {
                // reflection blocked or the field renamed: fall through to the deferred-safe default
            }

            return 1f;
        }

        private static void DrawOutline(SpriteBatch b, Rectangle rect, Color color, int thickness)
        {
            DrawRect(b, new Rectangle(rect.X, rect.Y, rect.Width, thickness), color);
            DrawRect(b, new Rectangle(rect.X, rect.Bottom - thickness, rect.Width, thickness), color);
            DrawRect(b, new Rectangle(rect.X, rect.Y, thickness, rect.Height), color);
            DrawRect(b, new Rectangle(rect.Right - thickness, rect.Y, thickness, rect.Height), color);
        }

        private static void DrawRect(SpriteBatch b, Rectangle rect, Color color)
        {
            b.Draw(Game1.staminaRect, rect, null, color, 0f, Vector2.Zero, SpriteEffects.None, TopLayerDepth(b));
        }

        /// <summary>Shows the matched word just above its outline, so a mis-split is obvious on screen.</summary>
        private static void DrawLabel(SpriteBatch b, string word, Rectangle wordBounds, Color color)
        {
            var font = Game1.smallFont;
            Vector2 size = font.MeasureString(word);

            int x = wordBounds.X;
            int y = wordBounds.Y - (int)size.Y - 4;
            if (y < 0)
                y = wordBounds.Bottom + 4;

            DrawRect(b, new Rectangle(x - 4, y - 2, (int)size.X + 8, (int)size.Y + 4), Color.Black * 0.85f);
            b.DrawString(font, word, new Vector2(x, y), color, 0f, Vector2.Zero, 1f, SpriteEffects.None, TopLayerDepth(b));
        }
    }
}
