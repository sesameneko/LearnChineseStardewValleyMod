using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Menus;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>
    /// Readings for the Japanese title screen (see <see cref="TitleScreenText"/>), in a speech
    /// bubble for whichever main button the cursor is over.
    ///
    /// Per TitleMenu in the 1.6.15 IL, the buttons are 58 * pixelZoom tall, with their bottom edge
    /// 8 * pixelZoom above the screen's. That bottom margin (24px) is far too short for a bubble, so
    /// the bubbles sit above the buttons and point down at them.
    /// </summary>
    public static class TitleScreenOverlay
    {
        private const float LayerDepth = 1f;

        /// <summary>Space between the button and the bubble's pointer.</summary>
        private const int Gap = 8;

        public static void Draw(SpriteBatch b)
        {
            if (ModEntry.Instance?.StudyLanguage != "ja"
                || Game1.activeClickableMenu is not TitleMenu title
                || TitleMenu.subMenu is not null
                || !title.titleInPosition
                || title.isTransitioningButtons)
                return;

            DrawButtonReading(b, title);
        }

        private static void DrawButtonReading(SpriteBatch b, TitleMenu title)
        {
            int mouseX = Game1.getMouseX(ui_scale: true);
            int mouseY = Game1.getMouseY(ui_scale: true);

            for (int i = 0; i < title.buttons.Count && i < title.buttonsToShow; i++)
            {
                var button = title.buttons[i];
                if (!button.containsPoint(mouseX, mouseY) || !TitleScreenText.Buttons.TryGetValue(button.name, out var reading))
                    continue;

                string text = Describe(reading);
                Vector2 size = Game1.smallFont.MeasureString(text);

                // keep it on screen: Exit sits close to the right edge on a narrow window
                float half = (size.X / 2) + 12;
                float centerX = Math.Clamp(button.bounds.Center.X, half, Math.Max(half, title.width - half));

                SpriteText.drawSmallTextBubble(
                    b,
                    text,
                    new Vector2(centerX, button.bounds.Y - Gap - 12),
                    maxWidth: -1,
                    layerDepth: LayerDepth,
                    drawPointerOnTop: false
                );
                return;
            }
        }

        /// <summary>The bubble text, with macrons kept where ExtendedFont added them and doubled vowels where not.</summary>
        private static string Describe(TitleReading reading)
        {
            var drawable = ExtendedFont.DrawableCharacters(Game1.smallFont);
            return reading.Describe(romaji => FontSafeText.Apply(romaji, drawable));
        }
    }
}
