using System;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace LanguageStudyStardewValleyMod.Patches
{
    /// <summary>
    /// Adds the flashcards tab to the pause menu. The game has no extension point for
    /// this, so it's hand-rolled against the installed 1.6.15 IL:
    ///
    /// - <c>GameMenu(bool)</c> builds <c>tabs</c> and <c>pages</c> as parallel lists -- every other
    ///   constructor chains to it -- so a postfix appends one of each.
    /// - Tab clicks go <c>tabs[i].name</c> → <c>getTabNumberFromName</c> → <c>changeTab</c>, which then
    ///   indexes <c>pages</c> by the number it gets back. The lookup is a hardcoded switch returning -1
    ///   for an unknown name, so a postfix maps ours to its index.
    /// - <c>draw</c> picks each tab's icon by the same hardcoded names and draws nothing for any
    ///   other, so the icon is drawn by <see cref="DrawTabIcon"/> from the mod's overlay pass. The
    ///   tab's hover label needs nothing: <c>performHoverAction</c> shows any tab's <c>label</c>.
    /// </summary>
    public static class GameMenuPatches
    {
        public const string TabName = "languagestudy.flashcards";

        /// <summary>Beyond the vanilla tab IDs (12340-12349).</summary>
        private const int TabId = 12360;

        private static bool warnedMismatch;

        public static void Apply(Harmony harmony)
        {
            harmony.Patch(
                original: AccessTools.Constructor(typeof(GameMenu), new[] { typeof(bool) }),
                postfix: new HarmonyMethod(typeof(GameMenuPatches), nameof(Postfix_Constructor))
            );
            harmony.Patch(
                original: AccessTools.Method(typeof(GameMenu), nameof(GameMenu.getTabNumberFromName)),
                postfix: new HarmonyMethod(typeof(GameMenuPatches), nameof(Postfix_GetTabNumberFromName))
            );
        }

        public static void Postfix_Constructor(GameMenu __instance)
        {
            try
            {
                // tabs and pages are matched by position; if another mod has broken that, adding
                // ours would open the wrong page
                if (__instance.tabs.Count != __instance.pages.Count || __instance.tabs.Count == 0)
                {
                    if (!warnedMismatch)
                    {
                        ModEntry.Log($"Not adding the flashcards tab: the pause menu has {__instance.tabs.Count} tabs but {__instance.pages.Count} pages (another mod may have changed it).", LogLevel.Warn);
                        warnedMismatch = true;
                    }
                    return;
                }

                var last = __instance.tabs[^1];
                var tab = new ClickableComponent(new Rectangle(last.bounds.X + 64, last.bounds.Y, 64, 64), TabName, "Flashcards")
                {
                    myID = TabId,
                    leftNeighborID = last.myID,
                    downNeighborID = last.downNeighborID,
                    tryDefaultIfNoDownNeighborExists = true,
                    fullyImmutable = true,
                };
                last.rightNeighborID = TabId;

                __instance.tabs.Add(tab);
                __instance.pages.Add(new FlashcardsPage(__instance.xPositionOnScreen, __instance.yPositionOnScreen, __instance.width, __instance.height));
            }
            catch (Exception ex)
            {
                ModEntry.Log($"Couldn't add the flashcards tab: {ex}", LogLevel.Error);
            }
        }

        public static void Postfix_GetTabNumberFromName(GameMenu __instance, string name, ref int __result)
        {
            if (name == TabName)
                __result = __instance.tabs.FindIndex(tab => tab.name == TabName);
        }

        /// <summary>
        /// Draws the tab's icon, raised like the vanilla ones when it isn't the open tab. Called from
        /// the overlay pass, which comes after the menu has drawn.
        ///
        /// The icon is a Lost Book on a plain menu tile: vanilla's tab art is one sprite per tab,
        /// frame included, with no blank frame to put a new icon on.
        /// </summary>
        public static void DrawTabIcon(SpriteBatch b)
        {
            if (Game1.activeClickableMenu is not GameMenu menu || menu.invisible)
                return;

            int index = menu.tabs.FindIndex(tab => tab.name == TabName);
            if (index < 0)
                return;

            Rectangle bounds = menu.tabs[index].bounds;
            int y = bounds.Y + (menu.currentTab == index ? 8 : 0);

            IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), bounds.X, y, 64, 64, Color.White, 0.5f, false);
            b.Draw(Game1.objectSpriteSheet, new Vector2(bounds.X + 8, y + 8), Game1.getSourceRectForStandardTileSheet(Game1.objectSpriteSheet, 102, 16, 16), Color.White, 0f, Vector2.Zero, 3f, SpriteEffects.None, 0.0001f);
        }
    }
}
