using System;
using System.Reflection;
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

        private static object[]? frozenArgs;
        private static TooltipBox frozenBox;

        /// <summary>Indices of drawHoverText's overrideX/overrideY parameters, resolved by name once.</summary>
        private static int overrideXIndex = -1;
        private static int overrideYIndex = -1;
        private static MethodInfo? drawHoverText;

        /// <summary>Tells the patches which method to re-issue through, and where its override args sit.</summary>
        public static void Initialise(MethodInfo method)
        {
            drawHoverText = method;

            var parameters = method.GetParameters();
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].Name == "overrideX")
                    overrideXIndex = i;
                else if (parameters[i].Name == "overrideY")
                    overrideYIndex = i;
            }

            if (overrideXIndex < 0 || overrideYIndex < 0)
                ModEntry.Log("drawHoverText has no overrideX/overrideY in this game version -- tooltips can't be frozen in place.", LogLevel.Warn);
        }

        /// <summary>Pins whatever tooltip was captured this frame. Returns false if there wasn't one.</summary>
        public static bool Freeze()
        {
            if (drawHoverText is null || overrideXIndex < 0 || overrideYIndex < 0)
                return false;

            if (!HoverTextPatches.TryGetLastTooltip(out object[] args, out TooltipBox box))
                return false;

            frozenArgs = args;
            frozenBox = box;
            IsFrozen = true;
            return true;
        }

        public static void Unfreeze()
        {
            IsFrozen = false;
            frozenArgs = null;
        }

        /// <summary>Re-draws the pinned tooltip where it was when it got pinned.</summary>
        public static void Draw(SpriteBatch spriteBatch)
        {
            if (!IsFrozen || frozenArgs is null || drawHoverText is null)
                return;

            try
            {
                IsReissuing = true;
                DrawTrace.Note("frozenTooltip");

                // the captured arguments verbatim, with only the sprite batch and position replaced,
                // so the pinned tooltip is drawn by the game exactly as it was
                var args = (object[])frozenArgs.Clone();
                args[0] = spriteBatch;
                args[overrideXIndex] = frozenBox.X;
                args[overrideYIndex] = frozenBox.Y;

                drawHoverText.Invoke(null, args);
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
