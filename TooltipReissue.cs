using System;
using System.Reflection;
using System.Text;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>
    /// Re-draws a captured vanilla tooltip through the game's own <c>IClickableMenu.drawHoverText</c>,
    /// at an explicit position.
    ///
    /// Drawing it by the game's own code rather than replicating it keeps every extra the vanilla
    /// layout can carry -- title divider, item icon, buff rows, crafting ingredients -- and puts the
    /// text through the normal draw path, so the word-hover capture picks it up like any other text.
    ///
    /// This is mechanism only, with no opinion about when a tooltip should be shown. Two features
    /// want it for different reasons (<see cref="FrozenTooltip"/> pins one deliberately,
    /// <see cref="TooltipLinger"/> holds the last one open briefly), and both need to know whether a
    /// draw in flight is ours -- which is what <see cref="IsReissuing"/> is for.
    /// </summary>
    public static class TooltipReissue
    {
        /// <summary>Whether a re-issue is in progress, i.e. whether the tooltip being drawn is ours.</summary>
        public static bool IsReissuing { get; private set; }

        private static MethodInfo? drawHoverText;

        /// <summary>Indices of drawHoverText's overrideX/overrideY parameters, resolved by name once.</summary>
        private static int overrideXIndex = -1;
        private static int overrideYIndex = -1;

        /// <summary>Whether the game exposes what's needed to re-issue a tooltip at a fixed position.</summary>
        public static bool IsReady => drawHoverText != null && overrideXIndex >= 0 && overrideYIndex >= 0;

        /// <summary>Tells this which method to re-issue through, and where its override args sit.</summary>
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

            if (!IsReady)
                ModEntry.Log("drawHoverText has no overrideX/overrideY in this game version -- tooltips can't be held in place.", LogLevel.Warn);
        }

        /// <summary>
        /// Copies an argument list for safekeeping. The StringBuilder is duplicated because the game
        /// reuses and clears its own between frames, which would otherwise empty a held tooltip.
        /// </summary>
        public static object[] CopyArgs(object[] args)
        {
            var copy = (object[])args.Clone();

            for (int i = 0; i < copy.Length; i++)
            {
                if (copy[i] is StringBuilder builder)
                    copy[i] = new StringBuilder(builder.ToString());
            }

            return copy;
        }

        /// <summary>Re-draws the given tooltip at the given position. Returns false if it couldn't.</summary>
        public static bool Draw(SpriteBatch spriteBatch, object[] args, TooltipBox box)
        {
            if (!IsReady || args.Length == 0)
                return false;

            try
            {
                IsReissuing = true;

                // the captured arguments verbatim, with only the sprite batch and position replaced,
                // so the tooltip is drawn by the game exactly as it was
                var call = (object[])args.Clone();
                call[0] = spriteBatch;
                call[overrideXIndex] = box.X;
                call[overrideYIndex] = box.Y;

                drawHoverText!.Invoke(null, call);
                return true;
            }
            catch (Exception ex)
            {
                ModEntry.Log($"Error re-drawing a tooltip: {ex}", LogLevel.Error);
                return false;
            }
            finally
            {
                IsReissuing = false;
            }
        }
    }
}
