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

        /// <summary>Whether to log each newly hovered word, and why its lookup failed if it did (see the ls_log_hovers command). Off by default.</summary>
        public bool LogHoveredWords { get; private set; }

        /// <summary>The source -> target text lookup the hover tooltips are translated through.</summary>
        public TranslationIndex TranslationIndex { get; private set; } = null!;

        /// <summary>Hand-segmented word boundaries, used by word hover in preference to the character-class fallback.</summary>
        public SegmentIndex Segments { get; private set; } = new();

        /// <summary>
        /// The game language this copy of the mod studies, e.g. "ja", from the manifest's
        /// StudyLanguage field. Empty if the field is missing, in which case the mod never activates.
        /// </summary>
        public string StudyLanguage { get; private set; } = "";

        /// <summary>The study language's English name, for messages.</summary>
        private string StudyLanguageName => LanguageActivation.DisplayName(this.StudyLanguage);

        /// <summary>
        /// Whether this copy is running: only while the game is in its study language. Inactive,
        /// it has no Harmony patches and its handlers return straight away, so the copies for other
        /// languages installed alongside it never meet it (see HowItWorks.md).
        /// </summary>
        public bool IsActive => this.activation.IsActive;

        private readonly ActivationTracker activation = new();

        /// <summary>Owns every patch, so deactivating can remove exactly this copy's.</summary>
        private Harmony harmony = null!;

        /// <summary>Whether the segment data and flashcard deck have been read, which happens on the first activation.</summary>
        private bool dataLoaded;

        public override void Entry(IModHelper helper)
        {
            Instance = this;
            this.StudyLanguage = LanguagePrompt.StudyLanguageOf(this.ModManifest) ?? "";
            if (this.StudyLanguage == "")
                Log($"manifest.json has no '{LanguageActivation.ManifestField}' field, so this copy doesn't know which language it studies and will stay inactive.", LogLevel.Error);

            this.harmony = new Harmony(this.ModManifest.UniqueID);

            // first, so a language change is acted on before anything else this tick
            helper.Events.GameLoop.UpdateTicked += this.OnLanguageTick;
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

            ExtendedFont.Register(helper, this.StudyLanguage);

            this.TranslationIndex = new TranslationIndex(helper);

            ConfigureMod(helper.ReadConfig<ModConfig>());

            this.AddCommand(
                "ls_spike_locale",
                "Tries to load a Strings/* asset in both the configured source and target locales, "
                + "via a couple of different APIs, and logs what worked. Usage: ls_spike_locale [assetName]  "
                + "(defaults to 'Strings/Objects')",
                this.OnSpikeLocaleCommand
            );

            this.AddCommand(
                "ls_build_index",
                "Rebuilds the translation index, optionally for a different locale pair than the config's. "
                + "Usage: ls_build_index [sourceLocale] [targetLocale]  (e.g. ls_build_index ja en)",
                this.OnBuildIndexCommand
            );

            this.AddCommand(
                "ls_log_misses",
                "Toggles logging the raw text of every hover tooltip that couldn't be translated, so the "
                + "gaps in the index can be found by playing rather than by guessing. Usage: ls_log_misses [on|off]",
                this.OnLogMissesCommand
            );

            this.AddCommand(
                "ls_log_hovers",
                "Toggles logging each newly hovered word and its label, plus -- when it fell back to the "
                + "heuristic split -- the drawn text and why the segment lookup missed. Usage: ls_log_hovers [on|off]",
                this.OnLogHoversCommand
            );

            this.AddCommand(
                "ls_probe_questlog",
                "Temporary diagnostic: reports when the journal's quest detail page opens and what text it holds "
                + "(name, description, objectives, and how the game wraps them). Usage: ls_probe_questlog [on|off]",
                this.OnProbeQuestLogCommand
            );

            this.AddCommand(
                "ls_dump_text",
                "Logs every string the word-hover capture recorded for the frame currently on screen, "
                + "with its position and font, plus what the hit-test last matched. "
                + "Usage: ls_dump_text [substring filter]",
                this.OnDumpTextCommand
            );

            this.AddCommand(
                "ls_draw_trace",
                "Logs the order in which the tooltip and this mod's overlays are drawn, for the next few frames. "
                + "Use it to diagnose anything appearing behind anything else -- z-order here is call order, which "
                + "no single log line shows. Usage: ls_draw_trace [frames]  (defaults to 5)",
                this.OnDrawTraceCommand
            );

            this.AddCommand(
                "ls_font_check",
                "Reports which characters of the given text the game's font can't draw -- those render as "
                + "the font's substitute character rather than failing, so they're easy to mistake for a data "
                + "bug. With no text, checks the loaded segment data. Usage: ls_font_check [text]",
                this.OnFontCheckCommand
            );

            this.AddCommand(
                "ls_font_info",
                "Logs smallFont's atlas (size, surface format) and the glyph metrics of the given characters -- "
                + "what extending the font with new glyphs depends on. Usage: ls_font_info [chars]  (defaults to aiueoAIUEO)",
                this.OnFontInfoCommand
            );

            this.AddCommand(
                "ls_font_export",
                "Saves smallFont's atlas as a PNG, to see what glyphs added by ExtendedFont look like. "
                + "Usage: ls_font_export <path.png>",
                this.OnFontExportCommand
            );

            this.AddCommand(
                "ls_lookup",
                "Looks a piece of source-language text up in the translation index, the same way a hover would. "
                + "Usage: ls_lookup <text>",
                this.OnLookupCommand
            );

            this.AddCommand(
                "ls_dialogue",
                "Reports how the dialogue translation bubble resolved the open dialogue box: where its text came from, "
                + "which raw segment each page maps to, and the target-language text found. Usage: ls_dialogue",
                this.OnDialogueCommand
            );

            if (this.renamedCommands)
                Log($"Another copy of the mod already registered the ls_* console commands, so this copy's end in _{this.CommandSuffix} (e.g. ls_lookup_{this.CommandSuffix}).");

            Log($"Language Study mod loaded; it runs while the game language is {this.StudyLanguageName}.");
        }

        #region Activation
        /// <summary>
        /// Polled every tick rather than driven by Content.LocaleChanged: TranslationIndex.Build
        /// flips the game language to English and back within a single call, and an event would
        /// deactivate this copy (and activate an English one) halfway through its own build.
        /// </summary>
        private void OnLanguageTick(object? sender, UpdateTickedEventArgs e)
        {
            bool matches = this.StudyLanguage != "" && LanguageActivation.Matches(this.StudyLanguage, LanguagePrompt.CurrentLanguage());
            switch (this.activation.Step(matches))
            {
                case ActivationChange.Activate:
                    this.Activate();
                    break;

                case ActivationChange.Deactivate:
                    this.Deactivate();
                    break;
            }

            LanguagePrompt.Poll(this.Helper, this.ModManifest);
        }

        private void Activate()
        {
            // plain file IO, but 20+ MB of it, so not until the copy is actually used
            if (!this.dataLoaded)
            {
                this.Segments = SegmentDataLoader.Load(this.Helper, this.StudyLanguage);
                FlashcardStore.Load(this.Helper);
                this.dataLoaded = true;
            }

            this.ApplyPatches();

            // update, not draw, so the index's language flip is safe here
            if (Context.IsWorldReady)
                this.EnsureIndexBuilt();

            Log($"Active: the game language is {this.StudyLanguageName}.");
        }

        private void Deactivate()
        {
            // the ID matters: without it Harmony removes every mod's patches
            this.harmony.UnpatchAll(this.harmony.Id);

            FrozenTooltip.Unfreeze();
            TooltipLinger.Clear();
            TooltipOverlay.Clear();
            TextCapturePatches.ConsumeFrame();
            DialogueTranslation.Clear();

            Log($"Inactive: the game language is no longer {this.StudyLanguageName}.");
        }

        /// <summary>Set when a command name was taken by another copy of the mod and this copy's got a suffix.</summary>
        private bool renamedCommands;

        private string CommandSuffix => this.StudyLanguage == "" ? "unknown" : this.StudyLanguage;

        /// <summary>
        /// Registers a console command that only runs while this copy is active. SMAPI rejects a
        /// name that's already registered, which another copy of the mod will have done with the
        /// same names, so on a clash the command is registered with this copy's language as a suffix.
        /// </summary>
        private void AddCommand(string name, string documentation, Action<string, string[]> callback)
        {
            void RunIfActive(string command, string[] args)
            {
                if (!this.IsActive)
                {
                    Log($"{command}: this copy of the mod is inactive, because the game language isn't {this.StudyLanguageName}.", LogLevel.Warn);
                    return;
                }

                callback(command, args);
            }

            try
            {
                this.Helper.ConsoleCommands.Add(name, documentation, RunIfActive);
            }
            catch (ArgumentException)
            {
                this.Helper.ConsoleCommands.Add($"{name}_{this.CommandSuffix}", documentation, RunIfActive);
                this.renamedCommands = true;
            }
        }
        #endregion

        private void ConfigureMod(ModConfig newConfig)
        {
            currentConfig = newConfig;
        }

        /// <summary>Writes the live config back to config.json, for settings changed outside GMCM (the flashcards tab's order).</summary>
        public void SaveConfig()
        {
            this.Helper.WriteConfig(currentConfig);
        }

        private void OnSave(object? sender, SaveCreatingEventArgs e)
        {
        }

        private void ApplyPatches()
        {
            try
            {
                var harmony = this.harmony;

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

                // draws the overlays just under the cursor rather than over it (see CursorPatches)
                var drawMouse = AccessTools.Method(typeof(IClickableMenu), "drawMouse");
                if (drawMouse is null)
                    Log("Couldn't find IClickableMenu.drawMouse -- the overlays will draw on top of the cursor in menus.", LogLevel.Warn);
                else
                {
                    harmony.Patch(
                        original: drawMouse,
                        prefix: new HarmonyMethod(typeof(CursorPatches), nameof(CursorPatches.Prefix_DrawMouse))
                    );
                }

                var drawMouseCursor = AccessTools.Method(typeof(Game1), "drawMouseCursor");
                if (drawMouseCursor is null)
                    Log("Couldn't find Game1.drawMouseCursor -- the overlays will draw on top of the mouse cursor.", LogLevel.Warn);
                else
                {
                    harmony.Patch(
                        original: drawMouseCursor,
                        prefix: new HarmonyMethod(typeof(CursorPatches), nameof(CursorPatches.Prefix_DrawMouseCursor))
                    );
                }

                ApplyTextCapturePatches(harmony);
            }
            catch (Exception ex)
            {
                Log($"Failed to apply Harmony patches -- hover translation will be inactive. {ex}", LogLevel.Error);
            }

            // separately, so a dialogue change in some game update costs only the translation bubble
            try
            {
                DialogueCapturePatches.Apply(this.harmony);
            }
            catch (Exception ex)
            {
                Log($"Failed to patch dialogue parsing -- dialogue boxes won't get a translation bubble. {ex}", LogLevel.Error);
            }

            // separately, so a pause-menu change in some game update costs only the tab
            try
            {
                GameMenuPatches.Apply(this.harmony);
            }
            catch (Exception ex)
            {
                Log($"Failed to patch the pause menu -- the flashcards tab won't appear. {ex}", LogLevel.Error);
            }
        }

        /// <summary>
        /// Patches the text-draw entry points so word hover can see what was drawn where.
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

                harmony.Patch(
                    target,
                    prefix: new HarmonyMethod(typeof(TextCapturePatches), patchName),
                    transpiler: new HarmonyMethod(typeof(GlyphCapturePatches), nameof(GlyphCapturePatches.Transpile_DrawString))
                );
            }

            harmony.Patch(
                original: AccessTools.Method(typeof(SpriteText), nameof(SpriteText.drawString)),
                prefix: new HarmonyMethod(typeof(TextCapturePatches), nameof(TextCapturePatches.Prefix_SpriteTextDrawString)),
                transpiler: new HarmonyMethod(typeof(GlyphCapturePatches), nameof(GlyphCapturePatches.Transpile_SpriteTextDrawString))
            );

            Log($"Glyph capture: SpriteText {On(GlyphCapturePatches.SpriteTextActive)}; DrawString string {On(GlyphCapturePatches.StringActive)}, "
                + $"string scaled {On(GlyphCapturePatches.StringScaledActive)}, StringBuilder {On(GlyphCapturePatches.BuilderActive)}, "
                + $"StringBuilder scaled {On(GlyphCapturePatches.BuilderScaledActive)}.");

            static string On(bool active) => active ? "on" : "OFF (measured fallback)";

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
            if (!this.IsActive || !Context.IsWorldReady)
                return;

            // the bubble icon sits on the box, and DialogueBox.receiveLeftClick doesn't check where a
            // click lands, so without this hovering the icon and clicking would turn the page
            if (e.Pressed.Contains(SButton.MouseLeft) && DialogueBubbleOverlay.IsCursorOverIcon())
            {
                this.Helper.Input.Suppress(SButton.MouseLeft);
                return;
            }

            // a click on a hovered word saves it as a flashcard, and goes no further: the game
            // underneath (a dialogue box, a shop row) never sees it
            if (currentConfig.ClickToSaveWords
                && e.Pressed.Contains(SButton.MouseLeft)
                && FlashcardCapture.TryHandleClick(this.StudyLanguage))
            {
                this.Helper.Input.Suppress(SButton.MouseLeft);
                return;
            }

            // a click can change what the held tooltip describes -- the item may be picked up,
            // consumed, or the menu replaced -- so it stops standing in for anything
            foreach (var button in e.Pressed)
            {
                if (button.IsUseToolButton() || button.IsActionButton() || button == SButton.MouseLeft || button == SButton.MouseRight)
                {
                    TooltipLinger.Clear();
                    break;
                }
            }

            if (currentConfig.HoldFreezeTooltip.JustPressed())
            {
                if (!FrozenTooltip.IsFrozen)
                    FrozenTooltip.Freeze(locked: false);
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
            if (!this.IsActive)
                return;

            // polled rather than handled in OnButtonsChanged so the pin also drops if the key stops
            // being reported as down without a release event (e.g. the window losing focus)
            if (FrozenTooltip.IsFrozen && !FrozenTooltip.IsLocked && !currentConfig.HoldFreezeTooltip.IsDown())
                FrozenTooltip.Unfreeze();

            QuestLogProbe.Poll(this.Helper);

            // here rather than while drawing: resolving an event line parses scripts
            DialogueTranslation.Update(this.TranslationIndex);
            DialogueBubbleOverlay.Update(this.Helper);
        }

        private void OnDayStart(object? sender, DayStartedEventArgs e)
        {
        }

        private void OnDayEnd(object? sender, DayEndingEventArgs e)
        {
        }

        #region Hover translation
        private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            if (!this.IsActive)
                return;

            // built here rather than on GameLaunched because the English side may briefly flip the
            // game's active language, which needs loaded content to flip against
            this.EnsureIndexBuilt();
        }

        private void EnsureIndexBuilt()
        {
            if (this.TranslationIndex.IsBuiltFor(this.StudyLanguage, currentConfig.TargetLanguage))
                return;

            this.TranslationIndex.Build(this.StudyLanguage, currentConfig.TargetLanguage);
            DialogueTranslation.Clear();
        }

        /// <summary>Drops any tooltip captured last frame that was never drawn, so nothing goes stale.</summary>
        private void OnRendering(object? sender, RenderingEventArgs e)
        {
            if (!this.IsActive)
                return;

            DrawTrace.BeginFrame();
            HoverExclusion.Update();
            TooltipLinger.BeginFrame();
            TooltipOverlay.Clear();
            TextCapturePatches.BeginFrame();
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
            if (this.IsActive && !this.CursorPatchIsDrawing && Game1.activeClickableMenu is null)
                this.DrawOverlays(e.SpriteBatch);
        }

        /// <summary>Draws this mod's overlays once the open menu -- and any tooltip it raised -- is on screen.</summary>
        private void OnRenderedActiveMenu(object? sender, RenderedActiveMenuEventArgs e)
        {
            if (this.IsActive && !this.CursorPatchIsDrawing)
                this.DrawOverlays(e.SpriteBatch);
        }

        /// <summary>The game tick the cursor patch last drew on, or null if it hasn't drawn yet.</summary>
        private int? lastCursorDrawTick;

        /// <summary>
        /// Whether the cursor patch is the one doing the drawing, so the render events should keep
        /// out of it.
        ///
        /// A recent tick rather than a flag set this frame: ls_draw_trace shows the render events
        /// firing *before* drawMouseCursor, so a frame-scoped flag is always false when they check
        /// it and gates nothing -- the first version of this was dead code that left three draws
        /// per frame. Remembering that the cursor path is live yields to it instead, and the short
        /// window re-arms these events within a couple of ticks if the game stops drawing a cursor
        /// (a cutscene, pan mode, the title screen).
        /// </summary>
        /// Nullable rather than an int.MinValue sentinel, which overflowed: before the cursor patch
        /// had ever run, "Game1.ticks - int.MinValue" wrapped to a large negative number, so this
        /// read true and gated the events off for good. The game only draws a cursor in-game
        /// (gameMode 3), so on the title screen that left nothing drawing at all -- which is how it
        /// was found: word highlighting disappeared from the load-game menu.
        private bool CursorPatchIsDrawing => this.lastCursorDrawTick is int tick && Game1.ticks - tick <= 2;

        /// <summary>
        /// Draws the overlays from the cursor patch, i.e. underneath the mouse cursor.
        ///
        /// This is the normal path; the render events above only draw when the game skipped its
        /// cursor draw (cutscenes, pan mode, the title screen), because drawing from both would put
        /// a second copy back on top of the cursor -- and paint the gloss label's translucent
        /// background two or three times over, which is darker than it should be.
        ///
        /// This was once rolled back on the theory that it made the overlays disappear. It didn't:
        /// word capture was simply switched off in those runs (see TextCapturePatches.Enabled).
        /// </summary>
        internal void DrawOverlaysBeforeCursor(SpriteBatch spriteBatch)
        {
            this.lastCursorDrawTick = Game1.ticks;
            this.DrawOverlays(spriteBatch);
        }

        /// <summary>
        /// Everything this mod puts on screen, drawn at the latest UI-mode pass available: the
        /// active menu's when a menu is open, the HUD's otherwise.
        ///
        /// One pass, not both: the menu pass comes later and paints over whatever the HUD pass put
        /// down. Note that this is *not* the same as once per frame -- ls_draw_trace shows the HUD
        /// pass firing twice per Display.Rendering -- which is why nothing here may rely on being
        /// the frame's last draw. See WordHoverOverlay.Draw for what that cost us.
        /// </summary>
        private void DrawOverlays(SpriteBatch spriteBatch)
        {
            DrawTrace.Note(Game1.activeClickableMenu is null ? "hudPass" : "menuPass");

            // before the tooltips, which can reach over the tab row
            GameMenuPatches.DrawTabIcon(spriteBatch);
            TitleScreenOverlay.Draw(spriteBatch);

            // exactly one of these draws a tooltip: the pin takes precedence and the linger stands
            // down for it, and the linger itself does nothing in a frame where the game drew its own
            FrozenTooltip.Draw(spriteBatch);
            TooltipLinger.Draw(spriteBatch);

            TooltipOverlay.Draw(spriteBatch);
            DialogueBubbleOverlay.Draw(spriteBatch);
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
            // again, even if a tooltip's postfix already drew it in this pass: by now the
            // translation tooltip has been drawn too, and the overlay has to sit on top of both
            WordHoverOverlay.Draw(spriteBatch);

            if (TextCapturePatches.DumpPending)
            {
                TextCapturePatches.DumpPending = false;
                this.DumpRecordedText("menu pass (where hit-testing happens)");
            }

            // everything recorded has now been hit-tested against, so it's safe to drop
            TextCapturePatches.ConsumeFrame();
        }

        private void OnFontCheckCommand(string command, string[] args)
        {
            var font = Game1.smallFont;
            if (font is null)
            {
                Log("The font isn't loaded yet.", LogLevel.Warn);
                return;
            }

            Log($"smallFont knows {font.Characters.Count} characters; missing ones are drawn as '{font.DefaultCharacter}'.");

            if (args.Length > 0)
            {
                string text = string.Join(" ", args);
                var missing = text.Where(c => !font.Characters.Contains(c)).Distinct().ToList();

                Log(missing.Count == 0
                    ? $"All {text.Length} characters of '{text}' are drawable."
                    : $"Not drawable: {string.Join(", ", missing.Select(c => $"'{c}' (U+{(int)c:X4})"))}");
                return;
            }

            // no argument: check what the segment data would actually put on screen
            var offenders = new Dictionary<char, int>();
            foreach (var segment in this.Segments.AllSegments())
            {
                string romaji = segment.Kana is null ? "" : FontSafeText.Apply(KanaRomaji.Convert(segment.Kana), ExtendedFont.DrawableCharacters(font));
                foreach (char c in romaji + (segment.Kana ?? "") + (segment.Gloss ?? ""))
                {
                    if (!font.Characters.Contains(c))
                        offenders[c] = offenders.GetValueOrDefault(c) + 1;
                }
            }

            if (offenders.Count == 0)
                Log("Every romaji, kana and gloss in the loaded segment data is drawable.");
            else
            {
                Log($"{offenders.Count} undrawable character(s) in the loaded segment data:", LogLevel.Warn);
                foreach (var (c, n) in offenders.OrderByDescending(pair => pair.Value))
                    Log($"  '{c}' (U+{(int)c:X4}) x{n}", LogLevel.Warn);
            }
        }

        private void OnFontInfoCommand(string command, string[] args)
        {
            var font = Game1.smallFont;
            if (font is null)
            {
                Log("The font isn't loaded yet.", LogLevel.Warn);
                return;
            }

            LogFontInfo(font, args.Length > 0 ? string.Join("", args) : "aiueoAIUEO");
        }

        private void OnFontExportCommand(string command, string[] args)
        {
            if (args.Length == 0)
            {
                Log("Usage: ls_font_export <path.png>", LogLevel.Warn);
                return;
            }

            string path = string.Join(" ", args);
            using (var stream = System.IO.File.Create(path))
                Game1.smallFont.Texture.SaveAsPng(stream, Game1.smallFont.Texture.Width, Game1.smallFont.Texture.Height);
            Log($"Saved the {Game1.smallFont.Texture.Width}x{Game1.smallFont.Texture.Height} atlas to {path}.");
        }

        private static void LogFontInfo(SpriteFont font, string chars)
        {
            var texture = font.Texture;
            Log($"smallFont: atlas {texture.Width}x{texture.Height} {texture.Format}, "
                + $"graphics profile {Game1.graphics.GraphicsDevice.GraphicsProfile}, "
                + $"{font.Characters.Count} glyphs, LineSpacing {font.LineSpacing}, Spacing {font.Spacing}, "
                + $"default '{font.DefaultCharacter}'");

            var glyphs = font.GetGlyphs();
            foreach (char c in chars.Distinct())
            {
                Log(glyphs.TryGetValue(c, out var glyph)
                    ? $"  '{c}': bounds {glyph.BoundsInTexture}, cropping {glyph.Cropping}, bearings "
                        + $"{glyph.LeftSideBearing}/{glyph.Width}/{glyph.RightSideBearing}"
                    : $"  '{c}': not in the font");
            }
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

            string source = args.Length > 0 ? args[0] : this.StudyLanguage;
            string target = args.Length > 1 ? args[1] : currentConfig.TargetLanguage;

            this.TranslationIndex.Build(source, target);
            DialogueTranslation.Clear();
        }

        private void OnLogMissesCommand(string command, string[] args)
        {
            this.LogTranslationMisses = args.Length > 0
                ? args[0].Equals("on", StringComparison.OrdinalIgnoreCase)
                : !this.LogTranslationMisses;

            Log($"Logging of untranslated hover text is {(this.LogTranslationMisses ? "on" : "off")}.");
        }

        private void OnLogHoversCommand(string command, string[] args)
        {
            this.LogHoveredWords = args.Length > 0
                ? args[0].Equals("on", StringComparison.OrdinalIgnoreCase)
                : !this.LogHoveredWords;

            Log($"Logging of hovered words is {(this.LogHoveredWords ? "on" : "off")}.");
        }

        private void OnProbeQuestLogCommand(string command, string[] args)
        {
            QuestLogProbe.Enabled = args.Length > 0
                ? args[0].Equals("on", StringComparison.OrdinalIgnoreCase)
                : !QuestLogProbe.Enabled;

            QuestLogProbe.Reset();
            Log($"Journal probe is {(QuestLogProbe.Enabled ? "on" : "off")}.");
        }

        private void OnDumpTextCommand(string command, string[] args)
        {
            if (!TextCapturePatches.Enabled)
            {
                Log("Word-hover capture is off -- set TextCapturePatches.Enabled back to true.", LogLevel.Warn);
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
            if (!this.IsActive || !this.dumpAtEndOfFrame)
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
                    + $"lineH={drawn.LineHeight,5:0.0} glyphs={(drawn.Glyphs is null ? "-" : drawn.Glyphs.Count.ToString())} "
                    + $"'{drawn.Text.Replace("\n", "\\n")}'");
                shown++;
            }

            if (shown == 0)
                Log("[dump] nothing matched -- is the text actually on screen right now?", LogLevel.Warn);
        }

        private void OnDialogueCommand(string command, string[] args)
        {
            if (Game1.activeClickableMenu is not DialogueBox box)
            {
                Log("No dialogue box is open.", LogLevel.Warn);
                return;
            }

            if (box.characterDialogue is { } dialogue)
            {
                if (DialogueCapturePatches.TryGetSource(dialogue, out var source))
                {
                    Log($"[dialogue] key {source.TranslationKey ?? "(none)"}, alternative {source.Alternative}, raw {SegmentIndex.Quote(source.Master)}");
                    if (source.Event is { } line)
                        Log($"[dialogue] built during event '{line.AssetName}' at command #{line.CurrentCommand} of {line.Commands.Length}: "
                            + (line.CurrentCommand >= 0 && line.CurrentCommand < line.Commands.Length ? SegmentIndex.Quote(line.Commands[line.CurrentCommand]) : "(out of range)"));

                    for (int i = 0; i < dialogue.dialogues.Count; i++)
                    {
                        int segment = i < source.LineSegments.Length ? source.LineSegments[i] : -1;
                        Log($"[dialogue] {(i == dialogue.currentDialogueIndex ? ">" : " ")} page {i} <- segment {segment}: {SegmentIndex.Quote(dialogue.dialogues[i].Text ?? "")}");
                    }
                }
                else
                    Log("[dialogue] this Dialogue has no parse record.");
            }
            else if (DialogueCapturePatches.TryGetSource(box, out var boxSource))
            {
                Log($"[dialogue] string box, {boxSource.PageCount} page(s) at creation, {box.dialogues.Count} left: {SegmentIndex.Quote(boxSource.Text)}");
                if (boxSource.Event is { } line)
                    Log($"[dialogue] built during event '{line.AssetName}' at command #{line.CurrentCommand} of {line.Commands.Length}");
            }
            else
                Log("[dialogue] this box has no record.");

            Log($"[dialogue] resolved: {DialogueTranslation.Explanation}");
            Log($"[dialogue] text: {(DialogueTranslation.Text is { } text ? SegmentIndex.Quote(text) : "(none -- the icon is hidden)")}");
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

        #region Spike: confirm locale-suffixed asset loading works at runtime
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
            Log($"[spike] loading '{assetName}' as source='{this.StudyLanguage}' and target='{currentConfig.TargetLanguage}'...", LogLevel.Info);

            var source = TryLoadLocaleVariant(assetName, this.StudyLanguage);
            var target = TryLoadLocaleVariant(assetName, currentConfig.TargetLanguage);

            if (source is null || target is null)
            {
                Log("[spike] could not load both variants -- see the failures above.", LogLevel.Warn);
                return;
            }

            Log($"[spike] loaded {source.Count} '{this.StudyLanguage}' entries and {target.Count} '{currentConfig.TargetLanguage}' entries for '{assetName}'.", LogLevel.Info);

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
                    if (this.IsActive && Context.IsWorldReady)
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

            configMenu.AddBoolOption(
                mod: this.ModManifest,
                name: () => "Click to Save Words",
                tooltip: () => "Left-clicking an underlined word saves it as a flashcard (click it again in the same sentence to remove it). "
                               + "While on, a click on a word doesn't reach the game.",
                getValue: () => this.currentConfig.ClickToSaveWords,
                setValue: value => this.currentConfig.ClickToSaveWords = value
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
