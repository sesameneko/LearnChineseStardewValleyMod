using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace LanguageStudyStardewValleyMod.Patches
{
    /// <summary>
    /// Draws this mod's overlays immediately before the mouse cursor, which is the latest point in
    /// the frame that is still underneath it.
    ///
    /// The game's own <c>Game1.DrawOverlays</c> ends like this (1.6.15 IL):
    ///
    ///     callvirt  Game1::drawMouseCursor()
    ///     callvirt  ModHooks::OnRendered(RenderSteps(12), spriteBatch, ...)
    ///     callvirt  SpriteBatch::End()
    ///     call      Game1::PopUIMode()
    ///
    /// The render-step hook SMAPI turns into <c>Display.Rendered*</c> is raised *after* the cursor,
    /// so anything drawn from those events sits on top of it -- which is why the pinned tooltip and
    /// the gloss label covered the cursor. A prefix here runs just before it instead. The
    /// <c>PopUIMode</c> at the end of that method is also the proof that we're inside a UI-mode
    /// push, in <c>Game1.spriteBatch</c>, i.e. the same coordinate space the overlays hit-test in.
    ///
    /// The game skips the cursor call entirely when <c>gameMode != 3</c>, during <c>freezeControls</c>
    /// or <c>panMode</c>, and unless <c>displayHUD || eventUp ||</c> the location is the Summit --
    /// hence the fallback draw still wired to the render events (see ModEntry.DrawOverlays).
    /// </summary>
    public static class CursorPatches
    {
        /// <summary>
        /// The HUD-level cursor, drawn when no menu is open.
        ///
        /// Skipped while a menu is open, because the menu has already drawn its own cursor by now
        /// (see Prefix_DrawMouse) -- drawing here as well put the overlays back on top of it, which
        /// is what made a frozen tooltip sit above the cursor in the pause menu while the same
        /// tooltip sat correctly below it on the toolbar.
        /// </summary>
        public static void Prefix_DrawMouseCursor()
        {
            if (Game1.activeClickableMenu != null)
                return;

            DrawTrace.Note("beforeCursor");
            ModEntry.Instance?.DrawOverlaysBeforeCursor(Game1.spriteBatch);
        }

        /// <summary>
        /// The menu-level cursor. Menus draw their own rather than going through
        /// <c>Game1.drawMouseCursor</c> -- 53 call sites in 1.6.15 -- and they do it at the end of
        /// their own draw, i.e. after any tooltip they raised and before the HUD pass. So this is
        /// the point that is last-but-one with a menu open.
        /// </summary>
        public static void Prefix_DrawMouse(SpriteBatch b)
        {
            DrawTrace.Note("beforeMenuCursor");
            ModEntry.Instance?.DrawOverlaysBeforeCursor(b);
        }
    }
}
