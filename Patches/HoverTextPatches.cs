using System;
using System.Text;
using Microsoft.Xna.Framework.Graphics;
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

        /// <summary>
        /// The last miss already logged. A tooltip is redrawn every frame it's hovered, so without
        /// this one hover buries the log in dozens of identical lines.
        /// </summary>
        private static string? lastLoggedMiss;

        /// <summary>The last vanilla tooltip seen this frame, for FrozenTooltip to pin.</summary>
        private static string? lastText;
        private static string? lastTitle;
        private static TooltipBox lastBox;
        private static bool haveLastTooltip;

        /// <summary>The most recent vanilla tooltip, if one was drawn.</summary>
        public static bool TryGetLastTooltip(out string text, out string? title, out TooltipBox box)
        {
            text = lastText ?? "";
            title = lastTitle;
            box = lastBox;
            return haveLastTooltip;
        }

        /// <summary>Returns false to skip the vanilla draw, which is how tooltips are suppressed while one is frozen.</summary>
        public static bool Prefix_DrawHoverText()
        {
            if (FrozenTooltip.ShouldSuppressVanilla)
            {
                capturing = false;
                return false;
            }

            capturing = true;
            haveBox = false;
            return true;
        }

        public static void Postfix_DrawHoverText(SpriteBatch b, StringBuilder text, string boldTitleText)
        {
            if (!capturing)
                return; // the vanilla draw was suppressed, so there's nothing to capture

            capturing = false;

            try
            {
                if (!haveBox)
                    return;

                // don't overwrite the pinned tooltip with the re-issue of itself
                if (!FrozenTooltip.IsFrozen)
                {
                    lastText = text?.ToString() ?? "";
                    lastTitle = boldTitleText;
                    lastBox = capturedBox;
                    haveLastTooltip = true;
                }

                var mod = ModEntry.Instance;
                if (mod is null || !mod.Config.TranslationEnabled)
                    return;

                var map = mod.TranslationIndex.Map;
                string? rawBody = text?.ToString();
                bool haveTitle = map.TryLookup(boldTitleText, out string translatedTitle);
                bool haveBody = map.TryLookup(rawBody, out string translatedBody);

                if (mod.LogTranslationMisses && (!haveTitle || !haveBody))
                {
                    // the raw text of anything that didn't translate -- this is how M2's list of
                    // still-uncovered assets gets built, since the only way to know what a tooltip
                    // actually displays is to read what arrives here
                    string signature = $"{boldTitleText}\u0000{rawBody}";
                    if (signature != lastLoggedMiss)
                    {
                        lastLoggedMiss = signature;

                        if (!haveTitle && !string.IsNullOrWhiteSpace(boldTitleText))
                            ModEntry.Log($"[miss] title: '{boldTitleText}'");
                        if (!haveBody && !string.IsNullOrWhiteSpace(rawBody))
                            ModEntry.Log($"[miss] body:  '{rawBody!.Replace("\n", "\\n")}'");
                    }
                }

                string overlayText = TooltipLayout.ComposeOverlayText(translatedTitle, translatedBody);
                if (overlayText.Length == 0)
                    return;

                TooltipOverlay.Pending = new PendingTranslationTooltip(overlayText, capturedBox);
            }
            catch (Exception ex)
            {
                ModEntry.Log($"Error in drawHoverText postfix: {ex}", LogLevel.Error);
            }

            // Draw the word overlay here rather than at RenderedHud: a toolbar tooltip is drawn
            // *after* that event, so anything drawn there ends up beneath it whatever its layer
            // depth. This postfix runs immediately after the tooltip, into the same sprite batch,
            // and by now the tooltip's own text is recorded -- so it is both on top and complete.
            WordHoverOverlay.Draw(b);
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
