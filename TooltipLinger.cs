using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>
    /// Holds the last vanilla tooltip on screen for a moment after the cursor leaves what raised it,
    /// so its words can be hovered without pinning anything by hand. Moving onto the held tooltip
    /// keeps it; moving anywhere else lets it expire.
    ///
    /// The important property is that this only ever *adds* a draw, in frames where the game drew no
    /// tooltip of its own. <see cref="FrozenTooltip"/> works the other way round -- it suppresses
    /// every vanilla tooltip for as long as its pin lasts -- which is right for a deliberate gesture
    /// and wrong for ambient behaviour: as a default it would mean no tooltip anywhere could ever
    /// appear again. Vanilla tooltips are drawn during the menu and HUD passes and the overlays are
    /// drawn later (see ModEntry.DrawOverlays), so by the time this draws, whether the game already
    /// drew one is known rather than guessed.
    ///
    /// The one exception is <see cref="ShouldSuppressVanilla"/>: while the cursor is actually inside
    /// the held tooltip's rect it is also over whatever sits underneath, which would raise its own
    /// tooltip and replace this one. That suppression is scoped to the rect and to a live linger,
    /// not to a mode the whole UI is in.
    /// </summary>
    public static class TooltipLinger
    {
        /// <summary>How long the tooltip stays after the cursor leaves what raised it.</summary>
        private const double LingerMs = 1500;

        /// <summary>Whether the game drew a tooltip of its own this frame.</summary>
        public static bool VanillaDrewThisFrame { get; set; }

        private static object[]? args;
        private static TooltipBox box;

        /// <summary>When the cursor left the thing that raised the tooltip, or null while it's still on it.</summary>
        private static double? leftAt;

        /// <summary>The menu the held tooltip belongs to, so a stale clone doesn't outlive it.</summary>
        private static IClickableMenu? owningMenu;

        /// <summary>Whether a held tooltip is currently on screen.</summary>
        public static bool IsShowing { get; private set; }

        /// <summary>Whether the cursor is inside a live held tooltip, which both keeps it up and hides what's under it.</summary>
        public static bool IsHoveringClone => args != null && IsAlive && box.Contains(Game1.getMouseX(ui_scale: true), Game1.getMouseY(ui_scale: true));

        /// <summary>Whether a vanilla tooltip draw should be skipped right now.</summary>
        public static bool ShouldSuppressVanilla => !TooltipReissue.IsReissuing && IsHoveringClone;

        /// <summary>Whether the hold is still within its window (or being kept alive by the cursor).</summary>
        private static bool IsAlive => leftAt is not double left || Now() - left <= LingerMs;

        private static double Now() => Game1.currentGameTime?.TotalGameTime.TotalMilliseconds ?? 0;

        /// <summary>Records a real vanilla tooltip as the one to hold when the cursor leaves it.</summary>
        public static void Notice(object[] tooltipArgs, TooltipBox tooltipBox)
        {
            args = tooltipArgs;
            box = tooltipBox;
            owningMenu = Game1.activeClickableMenu;
            leftAt = null; // still on the thing that raised it
        }

        /// <summary>Per-frame reset, plus the invalidation that can't wait for a draw.</summary>
        public static void BeginFrame()
        {
            VanillaDrewThisFrame = false;

            // a tooltip re-issued over a closed or replaced menu is drawn from arguments that no
            // longer describe anything on screen
            if (args != null && !ReferenceEquals(owningMenu, Game1.activeClickableMenu))
                Clear();
        }

        /// <summary>Drops the held tooltip, e.g. on a click, which may well have changed what it describes.</summary>
        public static void Clear()
        {
            args = null;
            leftAt = null;
            owningMenu = null;
            IsShowing = false;
        }

        /// <summary>Draws the held tooltip, if the game drew none of its own and the window hasn't closed.</summary>
        public static void Draw(SpriteBatch spriteBatch)
        {
            IsShowing = false;

            // the deliberate pin wins: it's already drawing the tooltip, and drawing a second copy
            // was the shape of every layering bug in this feature so far
            if (FrozenTooltip.IsFrozen)
                return;

            // a real tooltip is on screen, so there's nothing to stand in for -- and the cursor is
            // still on whatever raised it, so the countdown hasn't started
            if (VanillaDrewThisFrame)
            {
                leftAt = null;
                return;
            }

            if (args is null)
                return;

            if (IsHoveringClone)
                leftAt = Now(); // reading it keeps it alive
            else
            {
                leftAt ??= Now();
                if (!IsAlive)
                {
                    Clear();
                    return;
                }
            }

            DrawTrace.Note("linger");

            if (TooltipReissue.Draw(spriteBatch, args, box))
                IsShowing = true;
            else
                Clear();
        }
    }
}
