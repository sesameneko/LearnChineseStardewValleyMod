using System;
using System.Linq;
using LanguageStudyStardewValleyMod.Patches;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Menus;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>
    /// The dialogue translation bubble: while a dialogue box is open and
    /// <see cref="DialogueTranslation"/> found the page's target-language text, a language icon
    /// sits on the box's top-left corner, and hovering it shows that text in a speech bubble.
    ///
    /// Top-left because the top-right is the portrait panel. The icon is the title screen's
    /// language button, and the bubble is the same SpriteText small bubble as the word labels, so
    /// the two look and wrap alike.
    /// </summary>
    public static class DialogueBubbleOverlay
    {
        /// <summary>The title screen's language button in Minigames/TitleButtons (per TitleMenu in the 1.6.15 IL).</summary>
        private static readonly Rectangle IconSource = new(52, 458, 27, 25);

        private const float LayerDepth = 1f;

        /// <summary>Space between the icon and the bubble's pointer.</summary>
        private const int Gap = 8;

        /// <summary>The widest the bubble's text wraps to, so a long page reads as a paragraph rather than one line across the screen.</summary>
        private const int MaxTextWidth = 900;

        private static Texture2D? texture;
        private static bool loggedTextureError;

        /// <summary>Loads the icon's texture on the update tick the first time a box needs it, rather than mid-draw.</summary>
        public static void Update(IModHelper helper)
        {
            if (texture is not null || DialogueTranslation.Text is null || loggedTextureError)
                return;

            try
            {
                texture = helper.GameContent.Load<Texture2D>("Minigames/TitleButtons");
            }
            catch (Exception ex)
            {
                loggedTextureError = true;
                ModEntry.Log($"Couldn't load the dialogue bubble's icon: {ex.Message}", LogLevel.Warn);
            }
        }

        /// <summary>
        /// The icon's screen rect, or null when it isn't showing: no dialogue box, no text found for
        /// its page, or the box still opening or closing.
        /// </summary>
        public static Rectangle? IconBounds()
        {
            if (DialogueTranslation.Box is not { } box
                || !ReferenceEquals(Game1.activeClickableMenu, box)
                || box.transitioning
                || DialogueTranslation.Text is null
                || texture is null)
                return null;

            // a question box grows upwards: DialogueBox.draw puts its top at y - (heightForQuestions - height)
            int top = box.isQuestion ? box.y - (box.heightForQuestions - box.height) : box.y;

            int zoom = TitleMenu.pixelZoom > 0 ? TitleMenu.pixelZoom : 3;
            int width = IconSource.Width * zoom;
            int height = IconSource.Height * zoom;
            return new Rectangle(box.x, Math.Max(0, top - height), width, height);
        }

        /// <summary>Whether the cursor is on the icon, so a click there can be kept from advancing the dialogue.</summary>
        public static bool IsCursorOverIcon()
        {
            return IconBounds() is { } icon && icon.Contains(Game1.getMouseX(ui_scale: true), Game1.getMouseY(ui_scale: true));
        }

        public static void Draw(SpriteBatch b)
        {
            if (IconBounds() is not { } icon || texture is null || DialogueTranslation.Text is not { } text)
                return;

            DrawTrace.Note("dialogueBubble");

            using (TextCapturePatches.SuppressRecording())
            {
                bool hovered = icon.Contains(Game1.getMouseX(ui_scale: true), Game1.getMouseY(ui_scale: true));
                b.Draw(texture, icon, IconSource, hovered ? Color.White : Color.White * 0.85f, 0f, Vector2.Zero, SpriteEffects.None, LayerDepth);

                if (hovered)
                    DrawBubble(b, Wrap(text), icon);
            }
        }

        /// <summary>Wraps each line separately, so the blank line between a question and its answers survives.</summary>
        private static string Wrap(string text)
        {
            int width = Math.Min(MaxTextWidth, Game1.uiViewport.Width - 64);
            return string.Join("\n", text.Split('\n').Select(line => line.Length == 0 ? "" : Game1.parseText(line, Game1.smallFont, width)));
        }

        /// <summary>
        /// Above the icon, pointing down at it; if the window is too short for that, below it and
        /// over the box. The geometry is drawSmallTextBubble's, as described on WordHoverOverlay.DrawLabel.
        /// </summary>
        private static void DrawBubble(SpriteBatch b, string text, Rectangle icon)
        {
            Vector2 size = Game1.smallFont.MeasureString(text);

            bool above = icon.Y - Gap - 12 - size.Y >= 8;
            float bottomCenterY = above
                ? icon.Y - Gap - 12
                : Math.Min(icon.Bottom + Gap + size.Y, Math.Max(size.Y + 8, Game1.uiViewport.Height - 20));

            float half = (size.X / 2) + 12;
            float centerX = Math.Clamp(icon.Center.X, half, Math.Max(half, Game1.uiViewport.Width - half));

            SpriteText.drawSmallTextBubble(
                b,
                text,
                new Vector2(centerX, bottomCenterY),
                maxWidth: -1, // wrapped already; parseText would re-wrap it
                layerDepth: LayerDepth,
                drawPointerOnTop: !above
            );
        }
    }
}
