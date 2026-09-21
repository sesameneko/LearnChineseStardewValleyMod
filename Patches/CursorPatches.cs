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
        public static void Prefix_DrawMouseCursor()
        {
            DrawTrace.Note("beforeCursor");
            ModEntry.Instance?.DrawOverlaysBeforeCursor();
        }
    }
}
