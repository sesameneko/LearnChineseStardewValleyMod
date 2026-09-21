using System;
using Microsoft.Xna.Framework.Graphics;
using LanguageStudyStardewValleyMod.Patches;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>
    /// Pins the tooltip currently under the cursor so it stops following the mouse, letting the
    /// cursor move across it to hover individual words (Plan.md M2.1).
    ///
    /// Vanilla tooltips are suppressed while frozen by a prefix on drawHoverText, and the frozen one
    /// is re-issued through that same vanilla method with explicit overrideX/overrideY -- so the box
    /// is drawn by the game's own code rather than replicated, and its text goes through the normal
    /// draw path, which means the word-hover capture picks it up like any other text.
    /// </summary>
    public static class FrozenTooltip
    {
        /// <summary>Whether a tooltip is currently pinned.</summary>
        public static bool IsFrozen { get; private set; }

        /// <summary>Set while we re-issue the frozen tooltip, so the suppression prefix lets ours through.</summary>
        public static bool IsReissuing { get; private set; }

        /// <summary>Whether a vanilla tooltip draw should be skipped right now.</summary>
        public static bool ShouldSuppressVanilla => IsFrozen && !IsReissuing;

        private static string frozenText = "";
        private static string? frozenTitle;
        private static TooltipBox frozenBox;

        /// <summary>Pins whatever tooltip was captured this frame. Returns false if there wasn't one.</summary>
        public static bool Freeze()
        {
            if (!HoverTextPatches.TryGetLastTooltip(out string text, out string? title, out TooltipBox box))
                return false;

            frozenText = text;
            frozenTitle = title;
            frozenBox = box;
            IsFrozen = true;
            return true;
        }

        public static void Unfreeze()
        {
            IsFrozen = false;
            frozenText = "";
            frozenTitle = null;
        }

        /// <summary>Re-draws the pinned tooltip where it was when it got pinned.</summary>
        public static void Draw(SpriteBatch spriteBatch)
        {
            if (!IsFrozen)
                return;

            try
            {
                IsReissuing = true;

                IClickableMenu.drawHoverText(
                    spriteBatch,
                    frozenText,
                    Game1.smallFont,
                    boldTitleText: frozenTitle,
                    overrideX: frozenBox.X,
                    overrideY: frozenBox.Y
                );
            }
            catch (Exception ex)
            {
                ModEntry.Log($"Error drawing frozen tooltip, unfreezing: {ex}", LogLevel.Error);
                Unfreeze();
            }
            finally
            {
                IsReissuing = false;
            }
        }
    }
}
