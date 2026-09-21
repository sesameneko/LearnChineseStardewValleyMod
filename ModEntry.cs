using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using GenericModConfigMenu;
using HarmonyLib;
using LanguageStudyStardewValleyMod.Patches;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.BellsAndWhistles;
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

        /// <summary>Hand-segmented word boundaries, used by word hover in preference to the character-class fallback.</summary>
        public SegmentIndex Segments { get; private set; } = new();

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
            helper.Events.Display.Rendered += this.OnRenderedDiagnostic;

            this.TranslationIndex = new TranslationIndex(helper);

            ConfigureMod(helper.ReadConfig<ModConfig>());

            // plain file IO, so it needs no game state and can happen before the game is up
            this.Segments = SegmentDataLoader.Load(helper, currentConfig.SourceLanguage);

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
                "ls_word_hover",
                "Toggles the M3 proof of concept: outlines the individual word under the cursor in any "
                + "UI text. Usage: ls_word_hover [on|off]",
                this.OnWordHoverCommand
            );

            helper.ConsoleCommands.Add(
                "ls_probe_questlog",
                "Temporary diagnostic: reports when the journal's quest detail page opens and what text it holds "
                + "(name, description, objectives, and how the game wraps them). Usage: ls_probe_questlog [on|off]",
                this.OnProbeQuestLogCommand
            );

            helper.ConsoleCommands.Add(
                "ls_dump_text",
                "Logs every string the word-hover capture recorded for the frame currently on screen, "
                + "with its position and font, plus what the hit-test last matched. Requires ls_word_hover on. "
                + "Usage: ls_dump_text [substring filter]",
                this.OnDumpTextCommand
            );

            helper.ConsoleCommands.Add(
                "ls_draw_trace",
                "Logs the order in which the tooltip and this mod's overlays are drawn, for the next few frames. "
                + "Use it to diagnose anything appearing behind anything else -- z-order here is call order, which "
                + "no single log line shows. Usage: ls_draw_trace [frames]  (defaults to 5)",
                this.OnDrawTraceCommand
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
                var drawHoverText = FindOverload(nameof(IClickableMenu.drawHoverText), parameters => parameters[1].ParameterType == typeof(StringBuilder));
                harmony.Patch(
                    original: drawHoverText,
                    prefix: new HarmonyMethod(typeof(HoverTextPatches), nameof(HoverTextPatches.Prefix_DrawHoverText)),
                    postfix: new HarmonyMethod(typeof(HoverTextPatches), nameof(HoverTextPatches.Postfix_DrawHoverText))
                );

                // the frozen tooltip is re-issued through this same method, so it needs the handle
                FrozenTooltip.Initialise(drawHoverText);

                // ...and this is how the tooltip's exact screen rect gets captured, rather than
                // re-derived from vanilla's layout math.
                harmony.Patch(
                    original: FindOverload(nameof(IClickableMenu.drawTextureBox), parameters => parameters.Length == 11),
                    prefix: new HarmonyMethod(typeof(HoverTextPatches), nameof(HoverTextPatches.Prefix_DrawTextureBox))
                );

                ApplyTextCapturePatches(harmony);
            }
            catch (Exception ex)
            {
                Log($"Failed to apply Harmony patches -- hover translation will be inactive. {ex}", LogLevel.Error);
            }
        }

        /// <summary>
        /// Patches the text-draw entry points for the M3 word-hover proof of concept.
        ///
        /// The game calls exactly four of MonoGame's DrawString overloads; patching those four
        /// records each SpriteFont draw once (the overloads that delegate are ones the game never
        /// calls directly, so nothing is double-counted). SpriteText is a separate bitmap-font
        /// renderer that never touches DrawString, so it needs its own patch -- and it is what
        /// DialogueBox, QuestLog and ShopMenu use.
        /// </summary>
        private void ApplyTextCapturePatches(Harmony harmony)
        {
            foreach (var (textType, scaled, patchName) in new[]
                     {
                         (typeof(string), false, nameof(TextCapturePatches.Prefix_DrawString)),
                         (typeof(string), true, nameof(TextCapturePatches.Prefix_DrawStringScaledVector)),
                         (typeof(StringBuilder), false, nameof(TextCapturePatches.Prefix_DrawStringBuilder)),
                         (typeof(StringBuilder), true, nameof(TextCapturePatches.Prefix_DrawStringBuilderScaledVector)),
                     })
            {
                var target = typeof(SpriteBatch)
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(candidate =>
                    {
                        if (candidate.Name != nameof(SpriteBatch.DrawString))
                            return false;

                        var parameters = candidate.GetParameters();
                        if (parameters.Length != (scaled ? 9 : 4) || parameters[1].ParameterType != textType)
                            return false;

                        // Match the Vector2-scale flavour, NOT the float-scale one the game calls
                        // directly. The float overload is a thin wrapper that just widens the scale
                        // and delegates here, so the JIT inlines it and a prefix on it never runs --
                        // which silently lost every Utility.drawTextWithShadow draw (i.e. most menu
                        // body text). This overload holds the real glyph loop, so it is both
                        // un-inlinable and the single point every scaled draw passes through.
                        return !scaled || parameters[6].ParameterType == typeof(Vector2);
                    });

                if (target is null)
                {
                    Log($"Couldn't find SpriteBatch.DrawString({textType.Name}, scaled: {scaled}) -- word hover will miss some text.", LogLevel.Warn);
                    continue;
                }

                harmony.Patch(target, prefix: new HarmonyMethod(typeof(TextCapturePatches), patchName));
            }

            harmony.Patch(
                original: AccessTools.Method(typeof(SpriteText), nameof(SpriteText.drawString)),
                prefix: new HarmonyMethod(typeof(TextCapturePatches), nameof(TextCapturePatches.Prefix_SpriteTextDrawString))
            );

            // temporary: see QuestLogProbe. The 3-arg overload is the wrapping one menus use.
            harmony.Patch(
                original: AccessTools.Method(typeof(Game1), nameof(Game1.parseText), new[] { typeof(string), typeof(SpriteFont), typeof(int) }),
                postfix: new HarmonyMethod(typeof(QuestLogProbe), nameof(QuestLogProbe.Postfix_ParseText))
            );
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

            if (currentConfig.HoldFreezeTooltip.JustPressed())
            {
                if (!FrozenTooltip.IsFrozen && FrozenTooltip.Freeze(locked: false))
                    Log("Tooltip frozen while held -- move the cursor over it to hover individual words.");
            }

            if (currentConfig.FreezeTooltip.JustPressed())
            {
                if (FrozenTooltip.IsFrozen && !FrozenTooltip.IsLocked)
                {
                    // pinned by the hold key: keep it up once that key is released
                    FrozenTooltip.Lock();
                    Log("Tooltip locked.");
                }
                else if (FrozenTooltip.IsFrozen)
                {
                    FrozenTooltip.Unfreeze();
                    Log("Tooltip unfrozen.");
                }
                else if (FrozenTooltip.Freeze(locked: true))
                    Log("Tooltip locked -- move the cursor over it to hover individual words.");
                else
                    Log("Nothing to freeze: hover a tooltip first.", LogLevel.Warn);
            }

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
            // polled rather than handled in OnButtonsChanged so the pin also drops if the key stops
            // being reported as down without a release event (e.g. the window losing focus)
            if (FrozenTooltip.IsFrozen && !FrozenTooltip.IsLocked && !currentConfig.HoldFreezeTooltip.IsDown())
                FrozenTooltip.Unfreeze();

            QuestLogProbe.Poll(this.Helper);
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
            DrawTrace.BeginFrame();
            TooltipOverlay.Clear();
            TextCapturePatches.BeginFrame();
            WordHoverOverlay.DrawnThisFrame = false;
        }

        /// <summary>
        /// Draws this mod's overlays when there's no menu open, i.e. for toolbar and world hovers.
        ///
        /// This event and RenderedActiveMenu rather than the single, later Display.Rendered:
        /// Rendered runs against the *world* render target, which the game composites *underneath*
        /// the UI one -- so the box was drawn beneath every menu, and offset from the cursor by the
        /// ratio between the world's zoomLevel and the UI's uiScale. These two are the events SMAPI
        /// guarantees run in UI mode, in the same sprite batch (and coordinate space) as the vanilla
        /// tooltip we're stacking against.
        /// </summary>
        private void OnRenderedHud(object? sender, RenderedHudEventArgs e)
        {
            // with a menu open the menu's own pass comes later and would draw straight over these
            if (Game1.activeClickableMenu is null)
                this.DrawOverlays(e.SpriteBatch);
        }

        /// <summary>Draws this mod's overlays once the open menu -- and any tooltip it raised -- is on screen.</summary>
        private void OnRenderedActiveMenu(object? sender, RenderedActiveMenuEventArgs e)
        {
            this.DrawOverlays(e.SpriteBatch);
        }

        /// <summary>
        /// Everything this mod puts on screen, drawn at the latest UI-mode pass available: the
        /// active menu's when a menu is open, the HUD's otherwise.
        ///
        /// Exactly one of those passes, never both. Calling this from both was what buried the word
        /// overlay underneath the tooltip it annotates: the sequence per frame was pinned tooltip
        /// (HUD pass) -> overlay drawn on top of it (from drawHoverText's postfix) -> pinned tooltip
        /// *again* (menu pass), with the overlay's once-per-frame guard suppressing the second,
        /// correctly-ordered attempt. That is a call-order bug, and no amount of layer depth fixes
        /// one -- 0f, 1f and reading the batch's real sort mode were all tried first, and all failed.
        /// </summary>
        private void DrawOverlays(SpriteBatch spriteBatch)
        {
            DrawTrace.Note(Game1.activeClickableMenu is null ? "hudPass" : "menuPass");
            FrozenTooltip.Draw(spriteBatch);
            TooltipOverlay.Draw(spriteBatch);
            this.DrawWordHover(spriteBatch);
        }

        /// <summary>
        /// Draws the word-hover debug overlay once per frame, at the latest UI-mode point available:
        /// the active menu's pass when a menu is open (by then its text is recorded too), otherwise
        /// the HUD's.
        ///
        /// It must NOT be drawn from Display.Rendered even though that would see every recorded
        /// string: Rendered runs against the world render target, so UI-space rects come out
        /// offset (world scales by zoomLevel, UI by uiScale) and composited underneath the UI.
        /// </summary>
        private void DrawWordHover(SpriteBatch spriteBatch)
        {
            // no-ops when a tooltip's postfix already drew it this frame
            WordHoverOverlay.Draw(spriteBatch);

            if (TextCapturePatches.DumpPending)
            {
                TextCapturePatches.DumpPending = false;
                this.DumpRecordedText("menu pass (where hit-testing happens)");
            }

            // everything recorded has now been hit-tested against, so it's safe to drop
            TextCapturePatches.ConsumeFrame();
        }

        private void OnDrawTraceCommand(string command, string[] args)
        {
            if (args.Length == 0 || !int.TryParse(args[0], out int frames) || frames <= 0)
                frames = 5;

            DrawTrace.Arm(frames);
            Log($"Tracing the draw order for the next {frames} frames.");
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

        private void OnProbeQuestLogCommand(string command, string[] args)
        {
            QuestLogProbe.Enabled = args.Length > 0
                ? args[0].Equals("on", StringComparison.OrdinalIgnoreCase)
                : !QuestLogProbe.Enabled;

            QuestLogProbe.Reset();
            Log($"Journal probe is {(QuestLogProbe.Enabled ? "on" : "off")}.");
        }

        private void OnWordHoverCommand(string command, string[] args)
        {
            TextCapturePatches.Enabled = args.Length > 0
                ? args[0].Equals("on", StringComparison.OrdinalIgnoreCase)
                : !TextCapturePatches.Enabled;

            TextCapturePatches.ResetCounters();

            Log($"Word-hover debug overlay is {(TextCapturePatches.Enabled ? "on" : "off")}.");
        }

        private void OnDumpTextCommand(string command, string[] args)
        {
            if (!TextCapturePatches.Enabled)
            {
                Log("Word-hover capture is off -- run 'ls_word_hover on' first.", LogLevel.Warn);
                return;
            }

            this.dumpFilter = args.Length > 0 ? string.Join(" ", args) : null;
            TextCapturePatches.DumpPending = true;
            Log("[dump] armed -- will report from inside the next drawn frame.");
        }

        /// <summary>Filter for the armed dump, if one was given.</summary>
        private string? dumpFilter;

        /// <summary>Whether to dump again at the very end of the frame, to catch text drawn after the menu pass.</summary>
        private bool dumpAtEndOfFrame;

        /// <summary>
        /// Diagnostic only -- never draws here (see the CLAUDE.md note on Display.Rendered). This is
        /// the last point in the frame, so comparing its count against the menu pass's shows whether
        /// text is being drawn after the point where hit-testing happens.
        /// </summary>
        private void OnRenderedDiagnostic(object? sender, RenderedEventArgs e)
        {
            if (!this.dumpAtEndOfFrame)
                return;

            this.dumpAtEndOfFrame = false;
            this.DumpRecordedText("end of frame");
        }

        /// <summary>Reports the recorded text at the same point in the frame as the hit-test runs.</summary>
        private void DumpRecordedText(string where)
        {
            string? filter = this.dumpFilter;
            var recorded = TextCapturePatches.DrawnThisFrame;

            Log($"[dump] counters since enable: SpriteFont prefix fired {TextCapturePatches.SpriteFontCalls}x, "
                + $"SpriteText {TextCapturePatches.SpriteTextCalls}x; rejected not-uiMode {TextCapturePatches.RejectedNotUiMode}, "
                + $"blank {TextCapturePatches.RejectedBlank}; recorded {TextCapturePatches.RecordedTotal}; "
                + $"frame-start signals {TextCapturePatches.FrameStarts} vs consumes {TextCapturePatches.FrameConsumes}");

            Log($"[dump@{where}] {recorded.Count} string(s) recorded so far this frame"
                + (filter is null ? "" : $", filtered by '{filter}'")
                + $"; last hit-test match: {WordHoverOverlay.LastHitWord ?? "(none)"}");

            int shown = 0;
            for (int i = 0; i < recorded.Count; i++)
            {
                var drawn = recorded[i];
                if (filter != null && !drawn.Text.Contains(filter, StringComparison.Ordinal))
                    continue;

                string renderer = drawn.IsBitmapFont ? "SpriteText" : "SpriteFont";
                Log($"[dump] #{i,-3} {renderer,-10} x={drawn.X,7:0.0} y={drawn.Y,7:0.0} scale={drawn.Scale:0.00} "
                    + $"lineH={drawn.LineHeight,5:0.0} '{drawn.Text.Replace("\n", "\\n")}'");
                shown++;
            }

            if (shown == 0)
                Log("[dump] nothing matched -- is the text actually on screen right now?", LogLevel.Warn);
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

            configMenu.AddKeybindList(
                mod: this.ModManifest,
                name: () => "Lock Tooltip",
                tooltip: () => "Pins the tooltip under the cursor until pressed again, so you can move the mouse onto it and hover individual words.",
                getValue: () => this.currentConfig.FreezeTooltip,
                setValue: value => this.currentConfig.FreezeTooltip = value
            );

            configMenu.AddKeybindList(
                mod: this.ModManifest,
                name: () => "Hold to Freeze Tooltip",
                tooltip: () => "Pins the tooltip under the cursor for as long as this is held down.",
                getValue: () => this.currentConfig.HoldFreezeTooltip,
                setValue: value => this.currentConfig.HoldFreezeTooltip = value
            );
        }
        #endregion
    }
}
