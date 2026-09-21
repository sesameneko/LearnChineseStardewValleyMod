using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.BellsAndWhistles;

namespace LanguageStudyStardewValleyMod.Patches
{
    /// <summary>One string the game drew this frame, and where it put it.</summary>
    /// <param name="Text">The text as drawn, newlines included.</param>
    /// <param name="X">Left edge, in UI-space pixels.</param>
    /// <param name="Y">Top edge, in UI-space pixels.</param>
    /// <param name="Scale">Draw scale; 1 for the overloads that don't take one.</param>
    /// <param name="Font">The SpriteFont it was drawn with, or null when it was drawn by SpriteText.</param>
    public readonly record struct DrawnText(string Text, float X, float Y, float Scale, SpriteFont? Font, bool FromModTooltip)
    {
        /// <summary>Whether this came from SpriteText's bitmap font rather than a SpriteFont.</summary>
        public bool IsBitmapFont => this.Font is null;

        /// <summary>The width of a prefix of this text, using whichever font actually drew it.</summary>
        public float MeasurePrefix(string prefix)
        {
            return this.IsBitmapFont
                ? SpriteText.getWidthOfString(prefix)
                : this.Font!.MeasureString(prefix).X * this.Scale;
        }

        /// <summary>The height of one rendered line, using whichever font actually drew it.</summary>
        public float LineHeight => this.IsBitmapFont
            ? SpriteText.getHeightOfString("A")
            : this.Font!.LineSpacing * this.Scale;
    }

    /// <summary>
    /// Records every piece of UI text the game draws in a frame, so the mouse can be hit-tested
    /// against it afterwards. This is the M3 proof of concept: SDV keeps no record of where text
    /// landed (labels are immediate-mode -- see Plan.md), so the only way to know is to watch it
    /// being drawn.
    ///
    /// Two renderers have to be covered, because the game has two:
    ///  - SpriteFont text goes through SpriteBatch.DrawString. The game calls exactly four of
    ///    MonoGame's overloads, and patching those four records each draw exactly once (the other
    ///    overloads either delegate to ones the game never calls, or inline the glyph loop).
    ///  - SpriteText is a bitmap font that draws one quad per glyph via SpriteBatch.Draw, so it
    ///    never touches DrawString at all. It gets its own patch on its single funnel method.
    ///    This is not a minor case: DialogueBox, QuestLog and ShopMenu use it exclusively.
    /// </summary>
    public static class TextCapturePatches
    {
        private static readonly List<DrawnText> drawnThisFrame = new();

        /// <summary>Set while the mod is drawing, so our own overlay text isn't recorded as game text.</summary>
        private static bool suppressed;

        /// <summary>
        /// Whether to record at all. This runs on a very hot path -- every text draw in the game --
        /// so it stays a single flag that the prefixes check first.
        ///
        /// Hardcoded on while M3 word hover is the feature being built. It used to default to off
        /// with an ls_word_hover console command to switch it on, and that cost real debugging time:
        /// the flag isn't persisted, so every game launch came up with word hover silently off, and
        /// a perfectly good build looks exactly like a broken one -- no highlighting, no glosses,
        /// no error. Flip this line to turn the capture off.
        /// </summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>
        /// Diagnostics for why text does or doesn't get recorded. Counted before any gating, so a
        /// patch that never fires is distinguishable from one whose output is being filtered out --
        /// the SpriteFont patches live in MonoGame's assembly rather than the game's, and that is
        /// exactly the kind of difference worth being able to see rather than infer.
        /// </summary>
        public static long SpriteFontCalls { get; private set; }
        public static long SpriteTextCalls { get; private set; }
        public static long RejectedNotUiMode { get; private set; }
        public static long RejectedBlank { get; private set; }
        public static long RecordedTotal { get; private set; }

        /// <summary>How many times a frame-start signal arrived, vs how many times the list was consumed.</summary>
        public static long FrameStarts { get; private set; }
        public static long FrameConsumes { get; private set; }

        public static void ResetCounters()
        {
            SpriteFontCalls = 0;
            SpriteTextCalls = 0;
            RejectedNotUiMode = 0;
            RejectedBlank = 0;
            RecordedTotal = 0;
            FrameStarts = 0;
            FrameConsumes = 0;
        }

        /// <summary>What the game drew this frame, in draw order (so the last entry is on top).</summary>
        public static IReadOnlyList<DrawnText> DrawnThisFrame => drawnThisFrame;

        /// <summary>
        /// Set by the ls_dump_text command; consumed inside the draw. The dump has to happen at the
        /// same point in the frame as the hit-test, because a console command runs during the update
        /// tick and can read a list that the current frame hasn't finished filling.
        /// </summary>
        public static bool DumpPending { get; set; }

        /// <summary>
        /// Counts frame-start signals but deliberately does NOT clear.
        ///
        /// SMAPI's Display.Rendering can fire more than once per frame -- it is driven off the
        /// game's ModHooks render-step hook, and a menu that ends and re-begins the sprite batch
        /// (QuestLog does, to scissor-clip its scrollable description) produces several steps. When
        /// this cleared the list, every draw before the final step was discarded before hit-testing
        /// ran, so a page's text could vanish entirely while the counters kept climbing. The list is
        /// cleared in <see cref="ConsumeFrame"/> instead, once its contents have actually been used.
        /// </summary>
        public static void BeginFrame()
        {
            FrameStarts++;
        }

        /// <summary>Clears the recorded text, after it has been hit-tested against.</summary>
        public static void ConsumeFrame()
        {
            FrameConsumes++;
            drawnThisFrame.Clear();
        }

        /// <summary>Stops recording for the duration of the mod's own drawing.</summary>
        public static IDisposable SuppressRecording()
        {
            return new Suppression();
        }

        private static void Record(string? text, float x, float y, float scale, SpriteFont? font)
        {
            if (font is null)
                SpriteTextCalls++;
            else
                SpriteFontCalls++;

            if (!Enabled || suppressed)
                return;

            // world-space text (damage numbers, speech bubbles) is drawn in a different coordinate
            // space than the mouse is hit-tested in, so recording it would produce phantom hits
            if (!Game1.uiMode)
            {
                RejectedNotUiMode++;
                return;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                RejectedBlank++;
                return;
            }

            // safety valve: if nothing consumes the list (no menu and no HUD drawn), don't grow forever
            if (drawnThisFrame.Count > 4000)
                drawnThisFrame.Clear();

            RecordedTotal++;
            drawnThisFrame.Add(new DrawnText(text!, x, y, scale, font, TooltipReissue.IsReissuing));
        }

        #region SpriteBatch.DrawString -- the four overloads the game actually calls
        public static void Prefix_DrawString(SpriteFont spriteFont, string text, Vector2 position)
        {
            Record(text, position.X, position.Y, 1f, spriteFont);
        }

        /// <summary>
        /// The Vector2-scale overload: the one holding the actual glyph loop, which the float-scale
        /// overload widens into. Patching the implementation rather than the wrapper is what makes
        /// this fire at all -- see ApplyTextCapturePatches in ModEntry.
        /// </summary>
        public static void Prefix_DrawStringScaledVector(SpriteFont spriteFont, string text, Vector2 position, Vector2 scale)
        {
            Record(text, position.X, position.Y, scale.X, spriteFont);
        }

        public static void Prefix_DrawStringBuilder(SpriteFont spriteFont, StringBuilder text, Vector2 position)
        {
            Record(text?.ToString(), position.X, position.Y, 1f, spriteFont);
        }

        public static void Prefix_DrawStringBuilderScaledVector(SpriteFont spriteFont, StringBuilder text, Vector2 position, Vector2 scale)
        {
            Record(text?.ToString(), position.X, position.Y, scale.X, spriteFont);
        }
        #endregion

        #region SpriteText -- the bitmap font (dialogue, quest log, shops)
        /// <summary>
        /// SpriteText wraps internally rather than receiving pre-wrapped text, so the width it was
        /// given has to be captured and the wrap replayed -- otherwise every word after the first
        /// break is hit-tested as though the line ran off the screen (see TextHitTest.WrapToWidth).
        /// </summary>
        public static void Prefix_SpriteTextDrawString(string s, int x, int y, int width)
        {
            Record(WrapLikeSpriteText(s, width), x, y, 1f, font: null);
        }

        /// <summary>
        /// Wrapped strings, keyed by the text and the width it was wrapped to.
        ///
        /// The same dialogue is re-drawn every frame, and wrapping measures a growing prefix per
        /// character, so without this the hot path would re-measure the whole string ~60 times a
        /// second for no new information.
        /// </summary>
        private static readonly Dictionary<(string Text, int Width), string> wrapCache = new();

        /// <summary>Locales that break a line between any two characters rather than only at spaces.</summary>
        private static bool BreaksAnywhere =>
            LocalizedContentManager.CurrentLanguageCode is LocalizedContentManager.LanguageCode.ja
                or LocalizedContentManager.LanguageCode.zh
                or LocalizedContentManager.LanguageCode.ko
                or LocalizedContentManager.LanguageCode.th;

        private static string WrapLikeSpriteText(string? text, int width)
        {
            if (string.IsNullOrEmpty(text) || width <= 0)
                return text ?? string.Empty;

            var key = (text!, width);
            if (wrapCache.TryGetValue(key, out string? cached))
                return cached;

            // the game redraws the same few strings, but a shop or a quest log can cycle through
            // many -- bound it rather than grow forever
            if (wrapCache.Count > 512)
                wrapCache.Clear();

            string wrapped = TextHitTest.WrapToWidth(text, width, BreaksAnywhere, line => SpriteText.getWidthOfString(line));
            wrapCache[key] = wrapped;
            return wrapped;
        }
        #endregion

        private sealed class Suppression : IDisposable
        {
            private readonly bool previous;

            public Suppression()
            {
                this.previous = suppressed;
                suppressed = true;
            }

            public void Dispose()
            {
                suppressed = this.previous;
            }
        }
    }
}
