using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>A translation tooltip captured during this frame's draw, waiting to be rendered.</summary>
    /// <param name="Text">The already-translated text to show.</param>
    /// <param name="OriginalBox">The screen rect the vanilla tooltip was drawn into, so ours can avoid it.</param>
    public readonly record struct PendingTranslationTooltip(string Text, TooltipBox OriginalBox);

    /// <summary>
    /// Draws the translation tooltip captured by <see cref="Patches.HoverTextPatches"/>.
    ///
    /// The box is drawn by hand rather than by calling <c>IClickableMenu.drawHoverText</c>, because
    /// that method positions itself relative to the cursor (and does its own off-screen flipping),
    /// which would fight the explicit placement we want relative to the vanilla tooltip.
    /// </summary>
    public static class TooltipOverlay
    {
        /// <summary>Padding between the box edge and the text inside it.</summary>
        private const int Padding = 16;

        /// <summary>Width the text is wrapped to before the box is sized around it.</summary>
        private const int MaxTextWidth = 640;

        /// <summary>Set during the draw pass by the Harmony patch; consumed and cleared by <see cref="Draw"/>.</summary>
        public static PendingTranslationTooltip? Pending;

        /// <summary>Drops any tooltip captured in a previous frame that was never drawn.</summary>
        public static void Clear()
        {
            Pending = null;
        }

        /// <summary>Draws the pending translation tooltip (if any) and clears it.</summary>
        public static void Draw(SpriteBatch spriteBatch)
        {
            var pending = Pending;
            Pending = null;

            if (pending is null)
                return;

            try
            {
                var font = Game1.smallFont;
                string text = Game1.parseText(pending.Value.Text, font, MaxTextWidth);
                Vector2 size = font.MeasureString(text);

                int width = (int)size.X + (Padding * 2);
                int height = (int)size.Y + (Padding * 2);

                var box = TooltipLayout.Place(pending.Value.OriginalBox, width, height, Game1.uiViewport.Width, Game1.uiViewport.Height);

                IClickableMenu.drawTextureBox(
                    spriteBatch,
                    Game1.menuTexture,
                    new Rectangle(0, 256, 60, 60),
                    box.X, box.Y, box.Width, box.Height,
                    Color.White
                );

                Utility.drawTextWithShadow(
                    spriteBatch,
                    text,
                    font,
                    new Vector2(box.X + Padding, box.Y + Padding),
                    Game1.textColor
                );
            }
            catch (Exception ex)
            {
                ModEntry.Log($"Error drawing translation tooltip: {ex}", LogLevel.Error);
            }
        }
    }
}
