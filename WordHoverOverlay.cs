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
    /// Word hover: hit-tests the mouse against the text
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

        /// <summary>The segment under the cursor on the last draw that had text to test, for click-to-save.</summary>
        private static TextSegment? lastHitSegment;

        /// <summary>The tick <see cref="lastHitSegment"/> was found on.</summary>
        private static int lastHitTick = -1;

        /// <summary>
        /// The word hovered in the last frame or two, when its boundaries came from segment data --
        /// the only words a flashcard can be made from, since the fallback split has no gloss or kana.
        ///
        /// Read from input handling, which runs before the frame is drawn, so this is always the
        /// previous frame's hit: what the player saw when they clicked. Only frames that recorded
        /// text update it, because the overlay is drawn from more than one pass per frame and the
        /// later ones find nothing left to test.
        /// </summary>
        public static bool TryGetHoveredWord(out TextSegment segment)
        {
            segment = default;
            if (lastHitSegment is not { } hit || Game1.ticks - lastHitTick > 2)
                return false;

            segment = hit;
            return true;
        }

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

                    if (hit is not null || TextCapturePatches.DrawnThisFrame.Count > 0)
                    {
                        lastHitSegment = hit is { Exact: true } exactHit ? exactHit.Segment : null;
                        lastHitTick = Game1.ticks;
                    }

                    if (hit is null)
                        return;

                    if (DebugVisualizer)
                    {
                        // the whole line, faintly, then the word itself -- makes a wrong line and a
                        // wrong word within the right line distinguishable at a glance, with lime
                        // vs amber calling out where the boundaries came from
                        Color wordColor = hit.Value.Exact ? Color.Lime : Color.Orange;
                        DrawOutline(spriteBatch, hit.Value.Line, Color.Cyan * 0.35f, thickness: 2);
                        DrawOutline(spriteBatch, hit.Value.Word_Bounds, wordColor, thickness: 2);
                    }
                    else
                        DrawUnderline(spriteBatch, hit.Value.Word_Bounds);

                    string label = Describe(hit.Value.Segment);
                    LogIfNewlyHovered(hit.Value, label);
                    DrawLabel(spriteBatch, label, hit.Value.Word_Bounds);
                }
            }
            catch (Exception ex)
            {
                ModEntry.Log($"Error in word-hover overlay: {ex}", LogLevel.Error);
            }
        }

        /// <summary>The word and label last written to the log, so a word held under the cursor logs once rather than every frame.</summary>
        private static (string Word, string Label)? LastLogged;

        /// <summary>
        /// Logs the hovered word and the label shown for it, once each time the cursor lands on a
        /// different word. Draw runs several times a frame and a word stays hovered for many
        /// frames, so this compares against the last thing logged rather than logging per call.
        ///
        /// When the word came from the character-class fallback rather than segment data, the
        /// drawn string and the stage its lookup failed at are logged too -- that's the case this
        /// log exists to debug.
        /// </summary>
        private static void LogIfNewlyHovered(Hit hit, string label)
        {
            if (ModEntry.Instance?.LogHoveredWords != true)
                return;

            string word = hit.Segment.Text;
            if (LastLogged == (word, label))
                return;

            LastLogged = (word, label);

            string message = $"Hovered '{word}' -> {label.Replace("\n", " ")}";
            if (!hit.Exact)
                message += $"\n    fallback split -- {ExplainFallback(hit)}\n    drawn text: {SegmentIndex.Quote(hit.DrawnText)}";

            ModEntry.Log(message);
        }

        /// <summary>Which stage of <see cref="ResolveSegments"/> gave up on a hit, and why.</summary>
        private static string ExplainFallback(Hit hit)
        {
            var index = ModEntry.Instance?.Segments;
            if (index is null)
                return "segment data isn't loaded";

            if (!index.TryGetSegments(hit.DrawnText, out var wholeString))
                return index.ExplainMiss(hit.DrawnText);

            return SegmentIndex.ExplainLineMismatch(wholeString, hit.Lines, hit.LineIndex);
        }

        private readonly record struct Hit(TextSegment Segment, Rectangle Word_Bounds, Rectangle Line, bool Exact, string DrawnText, string[] Lines, int LineIndex);

        private static Hit? FindWordUnderCursor()
        {
            float mouseX = Game1.getMouseX(ui_scale: true);
            float mouseY = Game1.getMouseY(ui_scale: true);

            var recorded = TextCapturePatches.DrawnThisFrame;

            // while we're drawing a tooltip of our own -- pinned or lingering -- only its text is
            // hoverable, otherwise the cursor moving across that box matches whatever happens to be
            // underneath it
            bool ownTooltipUp = FrozenTooltip.IsFrozen || TooltipLinger.IsShowing;

            // in reverse: the last thing drawn is the thing on top, so it wins the hit
            for (int i = recorded.Count - 1; i >= 0; i--)
            {
                var drawn = recorded[i];

                if (ownTooltipUp && !drawn.FromModTooltip)
                    continue;

                if (drawn.Glyphs is { } glyphs)
                {
                    // the renderer was instrumented: its glyphs are the whole truth, including
                    // "nothing drawn yet" mid-typewriter, so never fall through to re-deriving it
                    if (FindWordInGlyphs(drawn, glyphs, mouseX, mouseY) is { } glyphHit)
                        return glyphHit;
                    continue;
                }

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
                if (!TextHitTest.IsHoverable(segment.Text, exact))
                    continue; // the gap between two words, or a bare number or symbol

                var (left, width) = TextHitTest.SegmentExtent(texts, drawn.MeasurePrefix, segmentIndex.Value);

                float lineTop = drawn.Y + (lineIndex.Value * lineHeight);
                var wordBounds = new Rectangle((int)(drawn.X + left), (int)lineTop, (int)Math.Ceiling(width), (int)Math.Ceiling(lineHeight));
                var lineBounds = new Rectangle((int)drawn.X, (int)lineTop, (int)Math.Ceiling(drawn.MeasurePrefix(line)), (int)Math.Ceiling(lineHeight));

                return new Hit(segment, wordBounds, lineBounds, exact, drawn.Text, lines, lineIndex.Value);
            }

            return null;
        }

        /// <summary>
        /// Hit-tests against the positions the renderer recorded for each character (see
        /// <see cref="GlyphHitTest"/>): find the glyph under the cursor, the segment that character
        /// belongs to, and outline that segment's glyphs on the hovered line. Nothing is measured or
        /// re-wrapped, so this can't disagree with what's on screen.
        /// </summary>
        private static Hit? FindWordInGlyphs(DrawnText drawn, IReadOnlyList<GlyphCell> glyphs, float mouseX, float mouseY)
        {
            if (GlyphHitTest.HitCell(glyphs, mouseX, mouseY) is not { } cellAt)
                return null;

            GlyphCell cell = glyphs[cellAt];
            string[] lines = TextHitTest.SplitLines(drawn.Text);
            string unwrapped = string.Concat(lines);

            var index = ModEntry.Instance?.Segments;
            IReadOnlyList<TextSegment>? found = null;
            bool exact = index != null
                         && index.TryGetSegments(drawn.Text, out found)
                         && SegmentIndex.Concat(found) == unwrapped;
            IReadOnlyList<TextSegment> segments = exact
                ? found!
                : TextHitTest.SplitSegments(unwrapped).Select(TextSegment.Plain).ToList();

            var texts = segments.Select(segment => segment.Text).ToList();
            if (GlyphHitTest.SegmentAt(texts, GlyphHitTest.ToUnwrappedIndex(drawn.Text, cell.Index)) is not var (segmentIndex, start))
                return null;

            var segment = segments[segmentIndex];
            if (!TextHitTest.IsHoverable(segment.Text, exact))
                return null; // the gap between two words, or a bare number or symbol

            if (GlyphHitTest.SpanBounds(glyphs, drawn.Text, start, segment.Text.Length, cell) is not { } word)
                return null;

            Box line = GlyphHitTest.LineBounds(glyphs, cell);
            int lineIndex = LineOfCharacter(lines, cell.Index);

            return new Hit(segment, ToRectangle(word), ToRectangle(line), exact, drawn.Text, lines, lineIndex);
        }

        /// <summary>Which newline-separated line of the drawn text a character index falls in, for the fallback log.</summary>
        private static int LineOfCharacter(string[] lines, int index)
        {
            int position = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                position += lines[i].Length + 1;
                if (index < position)
                    return i;
            }

            return Math.Max(0, lines.Length - 1);
        }

        private static Rectangle ToRectangle(Box box)
        {
            int left = (int)Math.Floor(box.Left);
            int top = (int)Math.Floor(box.Top);
            return new Rectangle(left, top, (int)Math.Ceiling(box.Right) - left, (int)Math.Ceiling(box.Bottom) - top);
        }

        /// <summary>
        /// Prefers hand-segmented boundaries, falling back to the character-class heuristic.
        ///
        /// The data describes the unwrapped source string, so it also has to be mapped onto the
        /// rendered line; if either step doesn't line up exactly, the fallback is used rather than
        /// a box drawn in the wrong place.
        /// </summary>
        /// <summary>
        /// The label text: the kana, then the romaji, then the gloss, one per line -- English last,
        /// matching the title-screen bubbles.
        ///
        /// The romaji is generated here from the kana (<see cref="KanaRomaji"/>) rather than read
        /// from the data's "reading" field -- kana is the source of truth -- and font-safed against
        /// the font it's drawn in: macrons are kept where ExtendedFont has added them, and doubled
        /// (ō to oo) if it couldn't. Where the kana has no kana in it (Joja, 2.0) the
        /// romaji would just repeat it, so that line is dropped.
        ///
        /// The source word itself is deliberately left out -- it's already on screen directly under
        /// the outline, so repeating it just widened the label over the text being read. With no
        /// gloss (the amber fallback, where there's no segment data) the word is all there is.
        /// </summary>
        private static string Describe(TextSegment segment)
        {
            if (string.IsNullOrWhiteSpace(segment.Gloss))
                return segment.Text;

            string gloss = IsSaved(segment) ? SavedMark() + segment.Gloss : segment.Gloss;

            if (string.IsNullOrWhiteSpace(segment.Kana))
                return gloss;

            string romaji = FontSafeText.Apply(KanaRomaji.Convert(segment.Kana), ExtendedFont.DrawableCharacters(Game1.smallFont));

            return romaji == segment.Kana
                ? $"{segment.Kana}\n{gloss}"
                : $"{segment.Kana}\n{romaji}\n{gloss}";
        }

        /// <summary>Whether the word is already a flashcard in the current source language.</summary>
        private static bool IsSaved(TextSegment segment)
        {
            string? language = ModEntry.Instance?.StudyLanguage;
            return language is not null && FlashcardStore.Deck.Contains(language, segment.Text, segment.Kana);
        }

        /// <summary>
        /// Prefixed to a saved word's gloss. A star where the font has one (the Japanese fonts do),
        /// otherwise a bracketed word -- never a bare '*', which is also what the font draws for a
        /// glyph it's missing.
        /// </summary>
        private static string SavedMark()
        {
            return ExtendedFont.DrawableCharacters(Game1.smallFont).Contains('★') ? "★ " : "[saved] ";
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
        /// <summary>
        /// Whether to draw the authoring visualisation -- a faint cyan box round the whole line and
        /// a coloured box round the matched word, lime when the boundaries came from hand-segmented
        /// data and amber when they came from the character-class fallback.
        ///
        /// Off for the shipping look, which underlines the word instead. Turn it on when working on
        /// segment data: the boxes are what make a wrong line, a wrong word within the right line,
        /// and missing segment data distinguishable at a glance, and the underline deliberately
        /// shows none of that.
        ///
        /// static readonly rather than const so the unused branch still compiles -- a const bool
        /// lets the compiler prove one side dead and warn about it (CS0162).
        /// </summary>
        private static readonly bool DebugVisualizer = false;

        private const float LayerDepth = 1f;

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

        /// <summary>The underline's warm gold, picked to sit with the game's wood-and-parchment UI.</summary>
        private static readonly Color UnderlineColor = new Color(255, 190, 70);

        /// <summary>
        /// Underlines the hovered word, for the shipping look.
        ///
        /// Drawn in three pieces rather than one bar: the middle at full height with shorter, dimmer
        /// caps either side, which is how you fake a rounded end at pixel-art scale. A soft shadow
        /// underneath lifts it off the text it sits against, the same trick the game's own text
        /// uses via drawTextWithShadow.
        /// </summary>
        private static void DrawUnderline(SpriteBatch b, Rectangle word)
        {
            const int thickness = 4;
            const int inset = 3;

            int y = word.Bottom - 2;

            DrawRect(b, new Rectangle(word.X + inset, y + thickness, word.Width - (inset * 2), 2), Color.Black * 0.3f);
            DrawRect(b, new Rectangle(word.X + inset, y, word.Width - (inset * 2), thickness), UnderlineColor);
            DrawRect(b, new Rectangle(word.X + 1, y + 1, inset - 1, thickness - 2), UnderlineColor * 0.7f);
            DrawRect(b, new Rectangle(word.Right - inset, y + 1, inset - 1, thickness - 2), UnderlineColor * 0.7f);
        }

        /// <summary>
        /// Shows the gloss in the game's own small speech bubble, pointing at the word.
        ///
        /// Vanilla art rather than a hand-drawn panel: this is what signs use, so it ships as-is.
        ///
        /// Despite living in SpriteText, the bubble draws with Game1.smallFont through
        /// Utility.drawTextWithShadow -- the same SpriteFont as the tooltips -- so "\n" is the line
        /// break it honours and SpriteText's own "^" comes out as a literal caret. Its geometry,
        /// from the 1.6.15 IL, is: box x = pos.X - size.X / 2 - 4, y = pos.Y - size.Y,
        /// w = size.X + 16, h = size.Y + 12, where size is smallFont.MeasureString of the text.
        /// The placement below mirrors that so the bubble lands exactly clear of the word.
        ///
        /// Note the bubble paints its own text, so the exact-vs-fallback colour no longer shows
        /// here -- the word outline still carries that signal.
        /// </summary>
        private static void DrawLabel(SpriteBatch b, string gloss, Rectangle wordBounds)
        {
            Vector2 size = Game1.smallFont.MeasureString(gloss);

            // the anchor is the bubble's bottom centre, and its box reaches 12px below that
            bool above = wordBounds.Y - LabelGap - size.Y - 12 >= 0;
            float bottomCenterY = above
                ? wordBounds.Y - LabelGap - 12
                : wordBounds.Bottom + LabelGap + size.Y;

            // keep it on screen: it's centred on the word, which can sit near either edge
            float half = (size.X / 2) + 12;
            float centerX = Math.Clamp(wordBounds.Center.X, half, Math.Max(half, Game1.uiViewport.Width - half));

            SpriteText.drawSmallTextBubble(
                b,
                gloss,
                new Vector2(centerX, bottomCenterY),
                maxWidth: -1, // already hard-wrapped by Describe; parseText would re-wrap it
                layerDepth: LayerDepth,
                drawPointerOnTop: !above
            );
        }
    }
}
