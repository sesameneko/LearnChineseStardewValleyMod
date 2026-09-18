using System;
using System.Text;
using StardewModdingAPI;

namespace LanguageStudyStardewValleyMod.Patches
{
    /// <summary>
    /// Captures the text of every vanilla hover tooltip as it's drawn, plus the exact screen rect it
    /// was drawn into, and hands the result to <see cref="TooltipOverlay"/> to render a translation
    /// box alongside it.
    ///
    /// Only the StringBuilder overload of <c>IClickableMenu.drawHoverText</c> is patched: the game's
    /// other tooltip entry points (<c>drawToolTip</c> and the string overload of
    /// <c>drawHoverText</c>) both funnel into it, so patching it alone catches everything exactly
    /// once (verified against the installed 1.6.15 assembly's IL).
    ///
    /// The tooltip's rect is captured rather than recomputed: <c>drawHoverText</c> draws its own
    /// background with <c>IClickableMenu.drawTextureBox</c> before anything else, so the first such
    /// call made while inside <c>drawHoverText</c> *is* the tooltip's box. That avoids having to
    /// replicate the vanilla layout math (which varies with money/buff/craft-ingredient extras).
    /// </summary>
    public static class HoverTextPatches
    {
        /// <summary>Whether we're currently inside a drawHoverText call, i.e. whether drawTextureBox calls are ours to notice.</summary>
        private static bool capturing;

        private static bool haveBox;
        private static TooltipBox capturedBox;

        public static void Prefix_DrawHoverText()
        {
            capturing = true;
            haveBox = false;
        }

        public static void Postfix_DrawHoverText(StringBuilder text, string boldTitleText)
        {
            capturing = false;

            try
            {
                if (!haveBox)
                    return;

                var mod = ModEntry.Instance;
                if (mod is null || !mod.Config.TranslationEnabled)
                    return;

                var map = mod.TranslationIndex.Map;
                map.TryLookup(boldTitleText, out string translatedTitle);
                map.TryLookup(text?.ToString(), out string translatedBody);

                string overlayText = TooltipLayout.ComposeOverlayText(translatedTitle, translatedBody);
                if (overlayText.Length == 0)
                    return;

                TooltipOverlay.Pending = new PendingTranslationTooltip(overlayText, capturedBox);
            }
            catch (Exception ex)
            {
                ModEntry.Log($"Error in drawHoverText postfix: {ex}", LogLevel.Error);
            }
        }

        public static void Prefix_DrawTextureBox(int x, int y, int width, int height)
        {
            if (!capturing || haveBox)
                return;

            capturedBox = new TooltipBox(x, y, width, height);
            haveBox = true;
        }
    }
}
