using StardewValley;
using StardewValley.Menus;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>
    /// Menus the mod's hover features stay out of entirely: no translation box, no word hover, no
    /// click-to-save, no freezing. Currently just Generic Mod Config Menu, whose text is mod
    /// settings rather than the game's -- nothing there is study material, and a click that saved a
    /// word would be a click the settings page never got.
    ///
    /// Vanilla tooltips still draw as normal; the mod just doesn't capture them.
    /// </summary>
    public static class HoverExclusion
    {
        /// <summary>Whether an excluded menu was open when this frame started drawing.</summary>
        public static bool InExcludedMenu { get; private set; }

        /// <summary>Re-checks the open menu. Called once per frame, before anything draws.</summary>
        public static void Update()
        {
            bool wasExcluded = InExcludedMenu;
            InExcludedMenu = IsExcludedMenuOpen();

            // a tooltip pinned or lingering from the menu underneath would otherwise stay on top
            if (InExcludedMenu && !wasExcluded)
            {
                FrozenTooltip.Unfreeze();
                TooltipLinger.Clear();
            }
        }

        /// <summary>
        /// GMCM's menu can be the active menu, a child of it (opened from another menu), or the
        /// title screen's submenu, so the whole chain is checked. Matched by namespace because GMCM
        /// is a soft dependency whose types this mod can't reference.
        /// </summary>
        private static bool IsExcludedMenuOpen()
        {
            IClickableMenu? menu = Game1.activeClickableMenu;
            if (menu is TitleMenu && TitleMenu.subMenu is not null)
                menu = TitleMenu.subMenu;

            for (int depth = 0; menu is not null && depth < 8; depth++, menu = menu.GetChildMenu())
            {
                if (menu.GetType().Namespace?.StartsWith("GenericModConfigMenu") == true)
                    return true;
            }

            return false;
        }
    }
}
