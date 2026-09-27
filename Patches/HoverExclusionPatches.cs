using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace LanguageStudyStardewValleyMod.Patches
{
    /// <summary>
    /// Text the game draws that word hover should leave alone: it isn't worth a gloss, and the
    /// label popping up over it just gets in the way.
    ///
    /// Each rule names the game method that draws the text and a test on the font it's drawn in.
    /// The method is patched to mark when it's running, and text recorded meanwhile is checked
    /// against its rule. Matching on the font as well as the method matters: a method that draws
    /// excluded text usually draws other text too (Toolbar.draw also draws the hovered item's
    /// tooltip), and that must stay hoverable.
    ///
    /// Excluded text is still recorded, flagged <see cref="DrawnText.Excluded"/>, so it still
    /// covers whatever was drawn beneath it -- hovering it shows nothing rather than reaching
    /// through to text underneath.
    /// </summary>
    public static class HoverExclusionPatches
    {
        private sealed record Rule(Type Type, string Method, Func<SpriteFont?, bool> Font, string What);

        private static readonly Rule[] Rules =
        {
            // Toolbar.draw writes slotText[i] (1-9, 0, -, =) in tinyFont over each slot
            new(typeof(Toolbar), nameof(Toolbar.draw), font => font == Game1.tinyFont, "hotbar slot numbers"),
        };

        /// <summary>The rules whose methods are running right now, innermost last.</summary>
        private static readonly List<Rule> active = new();

        /// <summary>Whether text drawn now, in this font, is excluded from word hover.</summary>
        public static bool IsExcluded(SpriteFont? font)
        {
            for (int i = active.Count - 1; i >= 0; i--)
            {
                if (active[i].Font(font))
                    return true;
            }

            return false;
        }

        public static void Apply(Harmony harmony)
        {
            foreach (var rule in Rules)
            {
                var method = AccessTools.Method(rule.Type, rule.Method, new[] { typeof(SpriteBatch) });
                if (method is null)
                {
                    ModEntry.Log($"Couldn't find {rule.Type.Name}.{rule.Method} -- {rule.What} will be hoverable.", LogLevel.Warn);
                    continue;
                }

                // a finalizer rather than a postfix, so an exception in the game's draw can't
                // leave the rule switched on for everything drawn afterwards
                harmony.Patch(
                    method,
                    prefix: new HarmonyMethod(typeof(HoverExclusionPatches), nameof(Prefix_Enter)),
                    finalizer: new HarmonyMethod(typeof(HoverExclusionPatches), nameof(Finalizer_Leave))
                );
            }
        }

        private static void Prefix_Enter(MethodBase __originalMethod, out int __state)
        {
            __state = active.Count;
            foreach (var rule in Rules)
            {
                if (rule.Type == __originalMethod.DeclaringType && rule.Method == __originalMethod.Name)
                    active.Add(rule);
            }
        }

        private static Exception? Finalizer_Leave(Exception? __exception, int __state)
        {
            if (active.Count > __state)
                active.RemoveRange(__state, active.Count - __state);
            return __exception;
        }
    }
}
