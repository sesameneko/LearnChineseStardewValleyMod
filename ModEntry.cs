using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using GenericModConfigMenu;
using HarmonyLib;
using LanguageStudyStardewValleyMod.Patches;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace LanguageStudyStardewValleyMod
{
    public class ModEntry : StardewModdingAPI.Mod
    {
        public static ModEntry Instance { get; private set; } = null!;

        public static void Log(string message, LogLevel level = LogLevel.Info)
        {
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            if (Instance == null) return;
            Instance.Monitor.Log(message, level);
        }

        private ModConfig currentConfig = null!;

        /// <summary>The live config, read by the Harmony patches.</summary>
        public ModConfig Config => this.currentConfig;

        /// <summary>Whether to log the raw text of hover tooltips that couldn't be translated (see the ls_log_misses command).</summary>
        public bool LogTranslationMisses { get; private set; }

        /// <summary>The source -> target text lookup the hover tooltips are translated through.</summary>
        public TranslationIndex TranslationIndex { get; private set; } = null!;

        public override void Entry(IModHelper helper)
        {
            Instance = this;
            helper.Events.Input.ButtonsChanged += this.OnButtonsChanged;
            helper.Events.GameLoop.UpdateTicked += this.OnTick;
            helper.Events.GameLoop.SaveCreating += this.OnSave;
            helper.Events.GameLoop.DayEnding += this.OnDayEnd;
            helper.Events.GameLoop.DayStarted += this.OnDayStart;
            helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
            helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
            helper.Events.Display.Rendering += this.OnRendering;
            helper.Events.Display.RenderedHud += this.OnRenderedHud;
            helper.Events.Display.RenderedActiveMenu += this.OnRenderedActiveMenu;

            this.TranslationIndex = new TranslationIndex(helper);

            ConfigureMod(helper.ReadConfig<ModConfig>());

            ApplyPatches();

            helper.ConsoleCommands.Add(
                "ls_spike_locale",
                "Spike for M1: tries to load a Strings/* asset in both the configured source and target locales, "
                + "via a couple of different APIs, and logs what worked. Usage: ls_spike_locale [assetName]  "
                + "(defaults to 'Strings/Objects')",
                this.OnSpikeLocaleCommand
            );

            helper.ConsoleCommands.Add(
                "ls_build_index",
                "Rebuilds the translation index, optionally for a different locale pair than the config's. "
                + "Usage: ls_build_index [sourceLocale] [targetLocale]  (e.g. ls_build_index ja en)",
                this.OnBuildIndexCommand
            );

            helper.ConsoleCommands.Add(
                "ls_log_misses",
                "Toggles logging the raw text of every hover tooltip that couldn't be translated, so the "
                + "gaps in the index can be found by playing rather than by guessing. Usage: ls_log_misses [on|off]",
                this.OnLogMissesCommand
            );

            helper.ConsoleCommands.Add(
                "ls_lookup",
                "Looks a piece of source-language text up in the translation index, the same way a hover would. "
                + "Usage: ls_lookup <text>",
                this.OnLookupCommand
            );

            Log("Language Study Mod initialized");
        }

        private void ConfigureMod(ModConfig newConfig)
        {
            currentConfig = newConfig;
        }

        private void OnSave(object? sender, SaveCreatingEventArgs e)
        {
        }

        private void ApplyPatches()
        {
            try
            {
                var harmony = new Harmony(this.ModManifest.UniqueID);

                // The StringBuilder overload is the single funnel point: drawToolTip and the string
                // overload of drawHoverText both call through to it (see HoverTextPatches).
                harmony.Patch(
                    original: FindOverload(nameof(IClickableMenu.drawHoverText), parameters => parameters[1].ParameterType == typeof(StringBuilder)),
                    prefix: new HarmonyMethod(typeof(HoverTextPatches), nameof(HoverTextPatches.Prefix_DrawHoverText)),
                    postfix: new HarmonyMethod(typeof(HoverTextPatches), nameof(HoverTextPatches.Postfix_DrawHoverText))
                );

                // ...and this is how the tooltip's exact screen rect gets captured, rather than
                // re-derived from vanilla's layout math.
                harmony.Patch(
                    original: FindOverload(nameof(IClickableMenu.drawTextureBox), parameters => parameters.Length == 11),
                    prefix: new HarmonyMethod(typeof(HoverTextPatches), nameof(HoverTextPatches.Prefix_DrawTextureBox))
                );
            }
            catch (Exception ex)
            {
                Log($"Failed to apply Harmony patches -- hover translation will be inactive. {ex}", LogLevel.Error);
            }
        }

        /// <summary>
        /// Finds one overload of a static IClickableMenu method by predicate, rather than by spelling
        /// out its full (20+ argument) parameter list for AccessTools.
        /// </summary>
        private static MethodInfo FindOverload(string name, Func<ParameterInfo[], bool> matches)
        {
            var method = typeof(IClickableMenu)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(candidate => candidate.Name == name && matches(candidate.GetParameters()));

            if (method is null)
                throw new InvalidOperationException($"Couldn't find a matching overload of IClickableMenu.{name} in this game version.");

            return method;
        }

        private void OnButtonsChanged(object? sender, ButtonsChangedEventArgs e)
        {
            if (!Context.IsWorldReady)
                return;

            if (currentConfig.ToggleTranslation.JustPressed())
            {
                currentConfig.TranslationEnabled = !currentConfig.TranslationEnabled;
                if (!currentConfig.TranslationEnabled)
                    TooltipOverlay.Clear();
                Log($"Translation {(currentConfig.TranslationEnabled ? "enabled" : "disabled")}.");
            }
        }

        private void OnTick(object? sender, UpdateTickedEventArgs updateTickedEventArgs)
        {
        }

        private void OnDayStart(object? sender, DayStartedEventArgs e)
        {
        }

        private void OnDayEnd(object? sender, DayEndingEventArgs e)
        {
        }

        #region M1: hover translation
        private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            // built here rather than on GameLaunched because the English side may briefly flip the
            // game's active language, which needs loaded content to flip against
            this.EnsureIndexBuilt();
        }

        private void EnsureIndexBuilt()
        {
            if (this.TranslationIndex.IsBuiltFor(currentConfig.SourceLanguage, currentConfig.TargetLanguage))
                return;

            this.TranslationIndex.Build(currentConfig.SourceLanguage, currentConfig.TargetLanguage);
        }

        /// <summary>Drops any tooltip captured last frame that was never drawn, so nothing goes stale.</summary>
        private void OnRendering(object? sender, RenderingEventArgs e)
        {
            TooltipOverlay.Clear();
        }

        /// <summary>
        /// Draws the translation tooltip captured during the HUD's draw (toolbar item hover, etc.).
        ///
        /// These two events rather than the single, later Display.Rendered: Rendered runs against the
        /// *world* render target, which the game composites *underneath* the UI one -- so the box was
        /// drawn beneath every menu, and offset from the cursor by the ratio between the world's
        /// zoomLevel and the UI's uiScale. RenderedHud/RenderedActiveMenu are the events SMAPI
        /// guarantees run in UI mode, in the same sprite batch (and coordinate space) as the vanilla
        /// tooltip we're stacking against. RenderedHud fires before menus draw, so a tooltip captured
        /// during a menu's draw is still pending and gets drawn by OnRenderedActiveMenu below.
        /// </summary>
        private void OnRenderedHud(object? sender, RenderedHudEventArgs e)
        {
            TooltipOverlay.Draw(e.SpriteBatch);
        }

        /// <summary>Draws the translation tooltip captured during the active menu's draw.</summary>
        private void OnRenderedActiveMenu(object? sender, RenderedActiveMenuEventArgs e)
        {
            TooltipOverlay.Draw(e.SpriteBatch);
        }

        private void OnBuildIndexCommand(string command, string[] args)
        {
            if (!Context.IsWorldReady)
            {
                Log("Load a save first, then run this command again.", LogLevel.Warn);
                return;
            }

            string source = args.Length > 0 ? args[0] : currentConfig.SourceLanguage;
            string target = args.Length > 1 ? args[1] : currentConfig.TargetLanguage;

            this.TranslationIndex.Build(source, target);
        }

        private void OnLogMissesCommand(string command, string[] args)
        {
            this.LogTranslationMisses = args.Length > 0
                ? args[0].Equals("on", StringComparison.OrdinalIgnoreCase)
                : !this.LogTranslationMisses;

            Log($"Logging of untranslated hover text is {(this.LogTranslationMisses ? "on" : "off")}.");
        }

        private void OnLookupCommand(string command, string[] args)
        {
            if (args.Length == 0)
            {
                Log("Usage: ls_lookup <text>", LogLevel.Warn);
                return;
            }

            string text = string.Join(" ", args);
            if (this.TranslationIndex.Map.TryLookup(text, out string translation))
                Log($"'{text}' -> '{translation}'");
            else
                Log($"'{text}' -> (no translation in the index; {this.TranslationIndex.Map.Count} entries loaded)", LogLevel.Warn);
        }
        #endregion

        #region M1 spike: confirm locale-suffixed asset loading works at runtime
        /// <summary>
        /// Tries to load <paramref name="assetName"/> in the given locale code (e.g. "ja"), using
        /// whichever of a few candidate approaches actually works, and logs the outcome of each
        /// attempt so we can see -- from inside the running game -- which one to build the real
        /// TranslationIndex on top of.
        /// </summary>
        private Dictionary<string, string>? TryLoadLocaleVariant(string assetName, string localeCode)
        {
            if (!TranslationIndex.LocaleSuffixes.TryGetValue(localeCode, out var suffix))
            {
                Log($"[spike] unknown locale code '{localeCode}' -- add it to TranslationIndex.LocaleSuffixes.", LogLevel.Warn);
                return null;
            }

            // Approach 1: ask for the locale-suffixed asset name directly (only meaningful for
            // locales that actually have a suffixed file on disk, i.e. everything except English).
            if (!string.IsNullOrEmpty(suffix))
            {
                string suffixedName = $"{assetName}.{suffix}";

                try
                {
                    var result = Helper.GameContent.Load<Dictionary<string, string>>(suffixedName);
                    Log($"[spike] SUCCESS: Helper.GameContent.Load(\"{suffixedName}\") -> {result.Count} entries", LogLevel.Info);
                    return result;
                }
                catch (Exception ex)
                {
                    Log($"[spike] failed: Helper.GameContent.Load(\"{suffixedName}\") -> {ex.GetType().Name}: {ex.Message}", LogLevel.Info);
                }

                try
                {
                    var result = Game1.content.Load<Dictionary<string, string>>(suffixedName);
                    Log($"[spike] SUCCESS: Game1.content.Load(\"{suffixedName}\") -> {result.Count} entries", LogLevel.Info);
                    return result;
                }
                catch (Exception ex)
                {
                    Log($"[spike] failed: Game1.content.Load(\"{suffixedName}\") -> {ex.GetType().Name}: {ex.Message}", LogLevel.Info);
                }
            }

            // Approach 2 (fallback, and the only option for English since it has no suffixed
            // file): temporarily flip the game's active language, load the bare asset name, then
            // restore whatever it was before. Confirmed to have real side effects (font reload,
            // UI re-layout) in the research this plan was based on, so this is deliberately a
            // last resort, not the default path.
            var originalLanguage = LocalizedContentManager.CurrentLanguageCode;
            try
            {
                if (!Enum.TryParse<LocalizedContentManager.LanguageCode>(localeCode, ignoreCase: true, out var targetLanguage))
                {
                    Log($"[spike] failed: '{localeCode}' isn't a recognized LocalizedContentManager.LanguageCode.", LogLevel.Info);
                    return null;
                }

                LocalizedContentManager.CurrentLanguageCode = targetLanguage;
                var result = Game1.content.Load<Dictionary<string, string>>(assetName);
                Log($"[spike] SUCCESS: flipped CurrentLanguageCode to {targetLanguage} and loaded \"{assetName}\" -> {result.Count} entries", LogLevel.Info);
                return result;
            }
            catch (Exception ex)
            {
                Log($"[spike] failed: language-flip approach for '{localeCode}' -> {ex.GetType().Name}: {ex.Message}", LogLevel.Info);
                return null;
            }
            finally
            {
                LocalizedContentManager.CurrentLanguageCode = originalLanguage;
            }
        }

        private void OnSpikeLocaleCommand(string command, string[] args)
        {
            if (!Context.IsWorldReady)
            {
                Log("Load a save first, then run this command again.", LogLevel.Warn);
                return;
            }

            string assetName = args.Length > 0 ? args[0] : "Strings/Objects";

            Log($"[spike] active game language: {LocalizedContentManager.CurrentLanguageCode}", LogLevel.Info);
            Log($"[spike] loading '{assetName}' as source='{currentConfig.SourceLanguage}' and target='{currentConfig.TargetLanguage}'...", LogLevel.Info);

            var source = TryLoadLocaleVariant(assetName, currentConfig.SourceLanguage);
            var target = TryLoadLocaleVariant(assetName, currentConfig.TargetLanguage);

            if (source is null || target is null)
            {
                Log("[spike] could not load both variants -- see the failures above.", LogLevel.Warn);
                return;
            }

            Log($"[spike] loaded {source.Count} '{currentConfig.SourceLanguage}' entries and {target.Count} '{currentConfig.TargetLanguage}' entries for '{assetName}'.", LogLevel.Info);

            int shown = 0;
            foreach (var key in source.Keys)
            {
                if (shown >= 5)
                    break;

                if (target.TryGetValue(key, out var targetValue))
                {
                    Log($"[spike]   {key}: '{source[key]}' -> '{targetValue}'", LogLevel.Info);
                    shown++;
                }
            }

            if (shown == 0)
                Log("[spike] no shared keys found between the two loaded dictionaries -- that's unexpected, worth investigating.", LogLevel.Warn);
        }
        #endregion

        #region GenericModConfigMenu
        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            // get Generic Mod Config Menu's API (if it's installed)
            var configMenu = this.Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
            if (configMenu is null)
                return;

            // register mod
            configMenu.Register(
                mod: this.ModManifest,
                reset: () => ConfigureMod(new ModConfig()),
                save: () =>
                {
                    this.Helper.WriteConfig(currentConfig);
                    if (Context.IsWorldReady)
                        this.EnsureIndexBuilt();
                }
            );

            configMenu.AddSectionTitle(
                mod: this.ModManifest,
                text: () => "Language Study"
            );

            configMenu.AddBoolOption(
                mod: this.ModManifest,
                name: () => "Translation Enabled",
                tooltip: () => "Whether hover translations are currently shown.",
                getValue: () => this.currentConfig.TranslationEnabled,
                setValue: value => this.currentConfig.TranslationEnabled = value
            );

            configMenu.AddKeybindList(
                mod: this.ModManifest,
                name: () => "Toggle Translation",
                tooltip: () => "Turns hover translations on/off.",
                getValue: () => this.currentConfig.ToggleTranslation,
                setValue: value => this.currentConfig.ToggleTranslation = value
            );
        }
        #endregion
    }
}
