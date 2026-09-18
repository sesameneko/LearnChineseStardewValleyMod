using System;
using System.Collections.Generic;
using GenericModConfigMenu;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

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

        /// <summary>
        /// Locale suffixes the game's own Content/Strings files are actually published under
        /// (confirmed by inspecting the installed game's Content/Strings folder). English has
        /// no suffixed variant on disk -- it's the unsuffixed/default file -- so getting the
        /// English text for a non-English active locale can't use the suffix trick and instead
        /// needs the CurrentLanguageCode-flip fallback below.
        /// </summary>
        private static readonly Dictionary<string, string> LocaleSuffixes = new()
        {
            ["en"] = "",
            ["ja"] = "ja-JP",
            ["zh"] = "zh-CN",
            ["ru"] = "ru-RU",
            ["pt"] = "pt-BR",
            ["es"] = "es-ES",
            ["de"] = "de-DE",
            ["th"] = "th-TH",
            ["fr"] = "fr-FR",
            ["ko"] = "ko-KR",
            ["it"] = "it-IT",
            ["tr"] = "tr-TR",
            ["hu"] = "hu-HU",
        };

        private ModConfig currentConfig = null!;

        public override void Entry(IModHelper helper)
        {
            Instance = this;
            helper.Events.Input.ButtonsChanged += this.OnButtonsChanged;
            helper.Events.GameLoop.UpdateTicked += this.OnTick;
            helper.Events.GameLoop.SaveCreating += this.OnSave;
            helper.Events.GameLoop.DayEnding += this.OnDayEnd;
            helper.Events.GameLoop.DayStarted += this.OnDayStart;
            helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;

            ConfigureMod(helper.ReadConfig<ModConfig>());

            ApplyPatches();

            helper.ConsoleCommands.Add(
                "ls_spike_locale",
                "Spike for M1: tries to load a Strings/* asset in both the configured source and target locales, "
                + "via a couple of different APIs, and logs what worked. Usage: ls_spike_locale [assetName]  "
                + "(defaults to 'Strings/Objects')",
                this.OnSpikeLocaleCommand
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
            // add Harmony patches here, e.g.:
            // var harmony = new Harmony(this.ModManifest.UniqueID);
            // harmony.Patch(
            //     original: AccessTools.Method(typeof(SomeType), nameof(SomeType.SomeMethod)),
            //     prefix: new HarmonyMethod(typeof(SomeOverrides), nameof(SomeOverrides.Prefix_SomeMethod))
            // );
        }

        private void OnButtonsChanged(object? sender, ButtonsChangedEventArgs e)
        {
            if (!Context.IsWorldReady)
                return;

            if (currentConfig.ToggleTranslation.JustPressed())
            {
                currentConfig.TranslationEnabled = !currentConfig.TranslationEnabled;
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

        #region M1 spike: confirm locale-suffixed asset loading works at runtime
        /// <summary>
        /// Tries to load <paramref name="assetName"/> in the given locale code (e.g. "ja"), using
        /// whichever of a few candidate approaches actually works, and logs the outcome of each
        /// attempt so we can see -- from inside the running game -- which one to build the real
        /// TranslationIndex on top of.
        /// </summary>
        private Dictionary<string, string>? TryLoadLocaleVariant(string assetName, string localeCode)
        {
            if (!LocaleSuffixes.TryGetValue(localeCode, out var suffix))
            {
                Log($"[spike] unknown locale code '{localeCode}' -- add it to LocaleSuffixes.", LogLevel.Warn);
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
                save: () => this.Helper.WriteConfig(currentConfig)
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
