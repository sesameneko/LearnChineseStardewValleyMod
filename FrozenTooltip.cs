using System.Reflection;
using Microsoft.Xna.Framework.Graphics;
using LanguageStudyStardewValleyMod.Patches;
using StardewModdingAPI;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>
    /// Pins the tooltip currently under the cursor so it stops following the mouse, letting the
    /// cursor move across it to hover individual words (Plan.md M2.1).
    ///
    /// This is the deliberate gesture: it suppresses every vanilla tooltip for as long as the pin
    /// lasts, so nothing else can replace what you're reading. <see cref="TooltipLinger"/> is the
    /// ambient counterpart and deliberately does the opposite -- see its notes on why it must never
    /// suppress. The drawing itself belongs to <see cref="TooltipReissue"/>.
    /// </summary>
    public static class FrozenTooltip
    {
        /// <summary>Whether a tooltip is currently pinned.</summary>
        public static bool IsFrozen { get; private set; }

        /// <summary>
        /// Whether the pin is a lock (toggled on, stays until toggled off) rather than a hold
        /// (held with a key, released when that key comes up).
        /// </summary>
        public static bool IsLocked { get; private set; }

        /// <summary>Whether a vanilla tooltip draw should be skipped right now.</summary>
        public static bool ShouldSuppressVanilla => IsFrozen && !TooltipReissue.IsReissuing;

        private static object[]? frozenArgs;
        private static TooltipBox frozenBox;

        /// <summary>Tells the reissue helper which method to draw through.</summary>
        public static void Initialise(MethodInfo method)
        {
            TooltipReissue.Initialise(method);
        }

        /// <summary>Pins whatever tooltip was captured this frame. Returns false if there wasn't one.</summary>
        /// <param name="locked">Whether the pin should persist until explicitly unpinned, rather than
        /// lasting only as long as the hold key is down.</param>
        public static bool Freeze(bool locked)
        {
            if (!TooltipReissue.IsReady)
                return false;

            if (!HoverTextPatches.TryGetLastTooltip(out object[] args, out TooltipBox box))
                return false;

            frozenArgs = args;
            frozenBox = box;
            IsFrozen = true;
            IsLocked = locked;
            return true;
        }

        /// <summary>Promotes a hold-pinned tooltip to a locked one, so releasing the hold key keeps it.</summary>
        public static void Lock()
        {
            if (IsFrozen)
                IsLocked = true;
        }

        public static void Unfreeze()
        {
            IsFrozen = false;
            IsLocked = false;
            frozenArgs = null;
        }

        /// <summary>Re-draws the pinned tooltip where it was when it got pinned.</summary>
        public static void Draw(SpriteBatch spriteBatch)
        {
            if (!IsFrozen || frozenArgs is null)
                return;

            DrawTrace.Note("frozenTooltip");

            if (!TooltipReissue.Draw(spriteBatch, frozenArgs, frozenBox))
            {
                ModEntry.Log("Couldn't re-draw the pinned tooltip, unfreezing.", LogLevel.Warn);
                Unfreeze();
            }
        }
    }
}
