using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using LanguageStudyStardewValleyMod.Patches;
using StardewModdingAPI;
using StardewValley;
using StardewValley.BellsAndWhistles;

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
        /// Draws the overlay for whatever word is under the cursor right now.
        ///
        /// Called after every tooltip draw and at the end of every render pass, deliberately
        /// without a once-per-frame guard. There used to be one, and it was the bug: the game
        /// raises RenderedHud *twice* per Display.Rendering, so the tooltip was drawn again after
        /// the overlay and the guard then suppressed the redraw that would have gone on top --
        /// leaving the overlay buried. A frame-scoped latch can only be correct if you know which
        /// pass is the last one, and that isn't knowable here. Redrawing is idempotent, so the
        /// robust rule is simply "always draw after a tooltip"; the cost is a few extra hit-tests
        /// per frame in a debug-only feature.
        /// </summary>
        public static void Draw(SpriteBatch spriteBatch)
        {
            if (!TextCapturePatches.Enabled)
                return;

            DrawTrace.Note("wordOverlay");

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

                    DrawLabel(spriteBatch, Describe(hit.Value.Segment), hit.Value.Word_Bounds);
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
        /// <summary>
        /// The label text: the gloss on the first line, the reading in brackets on the second.
        ///
        /// The source word itself is deliberately left out -- it's already on screen directly under
        /// the outline, so repeating it just widened the label over the text being read. With no
        /// gloss (the amber fallback, where there's no segment data) the word is all there is.
        /// </summary>
        private static string Describe(TextSegment segment)
        {
            if (string.IsNullOrWhiteSpace(segment.Gloss))
                return segment.Text;

            return string.IsNullOrWhiteSpace(segment.Reading)
                ? segment.Gloss
                : $"{segment.Gloss}\n({segment.Reading})";
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

        /// <summary>
        /// The layer depth the overlay draws at. Tooltips themselves draw at 0.9-0.95.
        ///
        /// Depth is not what puts this overlay on top: the batch it draws into is Deferred, where
        /// depth is ignored entirely and call order decides. 0f, 1f, and reading the batch's real
        /// sort mode out of MonoGame's private field to pick between them were all tried, and all
        /// three left the overlay behind the tooltip -- the cause was that the frozen tooltip was
        /// drawn a second time *after* the overlay (see ModEntry.DrawOverlays). So this is a plain
        /// constant, kept at the front end of the range for any batch that does sort.
        /// </summary>
        private const float LayerDepth = 1f;

        /// <summary>
        /// How opaque the gloss label's backing panel is. Low enough to read the game art through
        /// it -- at the old 0.85 only 15% of the background showed through, which looked flatly
        /// opaque against the tooltip it sits over.
        /// </summary>
        private const float LabelBackgroundOpacity = 0.6f;

        private static void DrawOutline(SpriteBatch b, Rectangle rect, Color color, int thickness)
        {
            DrawRect(b, new Rectangle(rect.X, rect.Y, rect.Width, thickness), color);
            DrawRect(b, new Rectangle(rect.X, rect.Bottom - thickness, rect.Width, thickness), color);
            DrawRect(b, new Rectangle(rect.X, rect.Y, thickness, rect.Height), color);
            DrawRect(b, new Rectangle(rect.Right - thickness, rect.Y, thickness, rect.Height), color);
        }

        private static void DrawRect(SpriteBatch b, Rectangle rect, Color color)
        {
            b.Draw(Game1.staminaRect, rect, null, color, 0f, Vector2.Zero, SpriteEffects.None, LayerDepth);
        }

        /// <summary>Space between the word's outline and the bubble's pointer.</summary>
        private const int LabelGap = 12;

        /// <summary>
        /// Shows the gloss in the game's own small speech bubble, pointing at the word.
        ///
        /// Vanilla art rather than a hand-drawn panel: this is what signs use, so it ships as-is.
        /// The bubble anchors by the bottom-centre of its body with the pointer hanging below, so
        /// sitting above the word is the natural case and going below just flips the pointer.
        ///
        /// Note the bubble paints its own text, so the exact-vs-fallback colour no longer shows
        /// here -- the word outline still carries that signal.
        /// </summary>
        private static void DrawLabel(SpriteBatch b, string gloss, Rectangle wordBounds)
        {
            // SpriteText's line break is '^', not '\n' -- the 1.6.15 IL checks for it fifteen times
            // against once for '\n'. Describe stays renderer-neutral and it's translated here.
            string text = gloss.Replace("\n", "^");

            int width = SpriteText.getWidthOfString(text);
            int height = SpriteText.getHeightOfString(text);

            // keep the bubble on screen: it's centred on the word, which can sit near an edge
            int half = (width / 2) + 8;
            int centerX = Math.Clamp(wordBounds.Center.X, half, Math.Max(half, Game1.uiViewport.Width - half));

            bool above = wordBounds.Y - LabelGap - height >= 0;
            float bottomCenterY = above
                ? wordBounds.Y - LabelGap
                : wordBounds.Bottom + LabelGap + height;

            SpriteText.drawSmallTextBubble(
                b,
                text,
                new Vector2(centerX, bottomCenterY),
                maxWidth: -1,
                layerDepth: LayerDepth,
                drawPointerOnTop: !above
            );
        }
    }
}
