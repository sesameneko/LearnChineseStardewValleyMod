# Language Study Mod — Roadmap

## Context

`LanguageStardewValleyMod` is currently a bare SMAPI scaffold (see `CLAUDE.md`) with no real behavior yet. The goal is to turn it into a mod that helps a player learn a new language while playing Stardew Valley, by translating the game's UI live. Concretely: hover over any game text and see an English translation in a tooltip. Three translation modes are wanted eventually — (1) exact literal replacement using the game's own localized strings, (2) AI-generated "explanatory" translations bundled as offline data (stretch goal, format-only for now), and (3) a dictionary/flashcards mode for inspecting and saving individual vocabulary. The initial language pair is Japanese (game UI) → English (translation), built so the pair is a config value rather than hardcoded.

Before committing to an approach, we researched how Stardew Valley/SMAPI actually work, since several assumptions needed verification (notably: "only one localized string set can be loaded at a time"). Findings below drove the architecture and the ordering of work.

## Confirmed technical constraints (from local + online research)

- **Single active locale, but a workaround exists.** `StardewValley.LocalizedContentManager.CurrentLanguageCode` governs which locale virtually all `Load<T>`/`LoadString` calls resolve against, and switching it triggers font/UI side effects. However, the content pipeline also supports loading a *specific* locale's variant of an asset via a locale-suffixed key (e.g. `Strings/StringsFromCSFiles.ja-JP` vs. the unsuffixed English default) without touching the active language. This means we can load both the ja and en variants of the same data assets at once and diff them, instead of flipping the game's active language at runtime.
- **Hover tooltips are centralized.** `IClickableMenu.drawHoverText(...)` and `drawToolTip(...)` are the funnel point for item tooltips and most button/component tooltips (anything using a component's `.hoverText`). A single Harmony patch here captures the text (and enough context to position a second tooltip) for a large share of cases.
- **Arbitrary label hover (headers, counters with no existing tooltip) has no equivalent hook.** It would require patching text-draw call sites themselves (e.g. `SpriteText.drawString`, `Utility.drawTextWithShadow`) to record `(string, screen rect)` per frame, then hit-testing the mouse against recorded rects. Meaningfully more invasive than the tooltip patch — deliberately deferred past milestone 1.
- **The GMCM interface must match the *installed* GMCM version, not upstream `develop`.** `IGenericModConfigMenuApi.cs` was refreshed in M0 from spacechase0's `develop` branch, which turned out to be ahead of the actually-installed GMCM (**1.16.0**, in this Mods folder). SMAPI's Pintail proxy requires *every* declared interface method to be mappable against the real mod API — one incompatible method (`AddComplexOptionWithGamepadSupport`, apparently added after 1.16.0) broke the *entire* proxy at runtime (`GetApi` logged an error and returned null), silently disabling all GMCM integration. Fixed by removing that method and everything declared after it in the upstream file (`SetTitleScreenOnlyForNextOptions`, `OnFieldChanged`, `OpenModMenu`, `OpenModMenuAsChildMenu`, `TryGetCurrentMenu`) — confirmed safe because Pintail reports the *first* unmappable method in declaration order, so everything before it (`Register` through `AddPageLink`, including `AddKeybind`/`AddKeybindList`) is confirmed to work against 1.16.0. **Don't re-sync this file from upstream `develop` again without testing `GetApi` actually succeeds in-game against the installed version** — GMCM's own repo tags stop at 1.8.1 (no tag for 1.16.0), so there's no easy "checkout the right version" shortcut; the installed DLL itself is the only ground truth, and it can't be reflected into directly on this machine either (references MonoGame types, which won't load — same arm64/x64 issue as elsewhere in this repo).
- **SMAPI keybind pattern**: define `public KeybindList ToggleTranslation { get; set; } = KeybindList.Parse("F2");` on `ModConfig`, check it in `helper.Events.Input.ButtonsChanged` via `.JustPressed()` — not `ButtonPressed`, which double-fires for multi-key binds.
- **No official extension point for adding a GameMenu (pause menu) tab.** Prior art (`ra1nyxin/rainyxinmain_StardewValleyMod`) patches the `GameMenu` constructor (postfix, add to `tabs`/`pages`), `getTabNumberFromName`, and `draw`. Alternative: depend on a higher-level framework (StardewUI, or KhloeLeclair's "Better Game Menu") instead of hand-rolling — worth a spike when this milestone starts, not decided now.
- **No decompiled 1.6.15 source tree exists locally.** Method signatures above come from a 1.5.6 decompile plus wiki docs and are believed stable but not confirmed against the exact installed build. First implementation task in milestone 1 should be a small spike (a debug console command) that logs both locale variants of a known asset key to confirm the suffix trick and exact API shape work on the installed 1.6.15 game before the rest of milestone 1 is built on top of it.

## High-level feature list

1. **Universal hover-translation tooltip** — the core feature. Three tiers of difficulty:
   - a. Augmenting text that already gets a vanilla tooltip (items, most buttons) — milestone 1.
   - b. Cursor-following tooltips need a *second* tooltip stacked above/below the original, not a replacement — part of milestone 1.
   - c. Arbitrary static labels with no existing tooltip (headers, counters) — later milestone, harder problem (see above).
2. **Three translation modes**:
   - Literal whole-string replacement (source-language string → its English data-asset equivalent).
   - Semi-literal/explanatory AI-generated translations, bundled offline (format defined now, generation pipeline out of scope).
   - Dictionary/flashcards — inspect individual vocab words, save to flashcards.
3. **Keyboard shortcuts** — at minimum, toggle translation on/off; architecture should allow adding more later (e.g. cycle translation mode).
4. **Config UI** — via Generic Mod Config Menu (soft dependency, already scaffolded).
5. **Pause-menu flashcards tab** — a new `GameMenu` tab for reviewing saved vocabulary.

## Build order

### M0 — Foundations
- Refresh `IGenericModConfigMenuApi.cs` to the current upstream interface (adds `AddKeybindList`, `AddPage`, etc.).
- Extend `ModConfig.cs`: `KeybindList ToggleTranslation`, `SourceLanguage`/`TargetLanguage` (string locale codes, default `"ja"`/`"en"`), `bool TranslationEnabled`.
- Wire `ModEntry.OnGameLaunched` to register the new config options via GMCM (section title, keybind list, bool toggle).
- Switch keybind handling to `helper.Events.Input.ButtonsChanged` (current `OnButtonPressed` stub is unused for this).
- Decide and create the mod's bundled-data folder layout now, even though only stubbed: e.g. `assets/translations/explanatory/<sourceLocale>.json` for mode 2, keyed by a hash of the original string, holding `{ "original": ..., "literal": ..., "natural": ... }` (matches the format already agreed with the user).

### M1 — Narrow slice: literal tooltip translation for item/object hover text
This is the milestone to prove the core mechanism; scope is deliberately limited to what already has a vanilla tooltip.
- **Spike done and fully confirmed (2026-09-18)**: ran `ls_spike_locale` (default asset `Strings/Objects`) twice — once with the in-game language set to English, once with it actually set to **Japanese** (the real production direction).
  - `Helper.GameContent.Load<Dictionary<string,string>>("Strings/Objects.ja-JP")` succeeded directly in both runs — 1529 well-formed entries (e.g. `MagicInk: '魔法のインク' -> 'Magic Ink'`). **Confirmed approach for any locale that has a suffixed file (i.e. everything except English): call `Helper.GameContent.Load` with the `.{suffix}` appended, no need to bypass SMAPI with `Game1.content.Load`.**
  - The `CurrentLanguageCode`-flip fallback (for English, which has no suffixed file) returned 1532 correct entries in both runs — matching the known 3-entry gap already documented in `tools/extracted-strings/README.md` (`Jelly_Flavored_(O)282_Name` etc. exist in `en` but not `ja`). With active language actually Japanese, the SMAPI log showed the real flip happening cleanly (`CurrentLanguageCode CHANGING from 'ja' to 'en'` ... `CHANGED from 'en' to 'ja'`), with correct content and no visible side effects. **The flip approach is safe to use in `TranslationIndex` for the no-suffix (English) side.**
- **Translation index**: a new `TranslationIndex` class built once (e.g. on `GameLoop.SaveLoaded` or `GameLoop.GameLaunched`) that loads the relevant `Data/Strings`-family assets in both locales and produces a `Dictionary<string, string>` mapping source-language text → target-language text, using the configured `SourceLanguage`/`TargetLanguage` codes (not hardcoded ja/en) so the pair can change later without touching this class's logic.
- **Harmony patch**: postfix on `IClickableMenu.drawHoverText` and `drawToolTip` to capture the hovered text each time it's called, look it up in the `TranslationIndex`, and (if found and translation is enabled) stash a "pending overlay" — text + anchor position + whether the original tooltip drew above or below the cursor.
- **Draw the second tooltip**: in `helper.Events.Display.RenderedActiveMenu` (and `RenderedHud` for non-menu HUD tooltips), draw the pending overlay as a second tooltip box, positioned on the opposite side of the cursor from the original (or clamped to screen bounds if that would go offscreen).
- **Toggle**: `ToggleTranslation` keybind flips `ModConfig.TranslationEnabled`; when off, skip the lookup/draw entirely.

Files to add/change: `ModConfig.cs`, `ModEntry.cs` (event wiring, `ApplyPatches`), new `TranslationIndex.cs`, new `Patches/HoverTextPatches.cs` (Harmony prefix/postfix + pending-overlay state), new `TooltipOverlay.cs` (the actual draw call). Follow the existing Harmony pattern from `StrayCatsStardewValleyMod/PetOverrides.cs` for patch setup style.

### M2 — Expand literal-translation coverage
- Extend `TranslationIndex` to cover more `Data/*` assets that feed hover/dialogue text beyond `Data/Strings` (e.g. `Data/Objects`, `Data/Crops`, `Data/NPCDispositions` display names, shop/bundle data) as they're found to be missing during play-testing.
- Handle known edge cases surfaced in research (e.g. gendered-string delimiter-splitting bugs in some locales) defensively — fall back to "no translation available" rather than a garbled string.

### M3 — Arbitrary label hover (headers, counters, untooltipped buttons)
- Design spike: patch core text-draw entry points (`SpriteText.drawString`, `Utility.drawTextWithShadow`, or targeted per-menu `draw()` overrides) to record `(string, screen rect)` per frame; hit-test mouse position each tick against recorded rects to decide what to show a tooltip for.
- Scope to a prioritized subset of menus first (inventory, shop, dialogue) rather than attempting universal coverage in one pass — this is the most open-ended milestone and should be re-scoped once M1/M2 reveal how much vanilla tooltip coverage already handles.

### M4 — Semi-literal / explanatory translation mode
- Load the bundled `assets/translations/explanatory/<sourceLocale>.json` (format already defined in M0) alongside `TranslationIndex`, keyed the same way (by original string) so lookups share one code path.
- Add a config/keybind to switch which mode's text is shown when both literal and explanatory data exist for a string; fall back to literal (or "no translation") when explanatory data is missing.
- No generation tooling in scope — data is produced externally and dropped into the assets folder.

### M5 — Dictionary / flashcards mode
- Core open problem: Japanese has no whitespace word boundaries, so "select an individual vocab word" out of a multi-word hovered string needs either a bundled tokenizer + dictionary (heavier) or a bundled vocab list matched by substring against hovered text (lighter, recommended starting point). Decide this at the start of the milestone, not now.
- Persistence: store flashcards globally (not per-save, since learned vocabulary isn't tied to a farm) via `helper.Data.WriteJsonFile`/`ReadJsonFile` on the mod's own data folder.
- UI: a review flow for saved cards (could reuse whatever UI approach M6 picks for the pause-menu tab).

### M6 — Pause-menu flashcards tab
- Spike whether to hand-roll the `GameMenu` Harmony patch (constructor postfix adding to `tabs`/`pages`, patch `getTabNumberFromName`, patch `draw` — pattern confirmed via `ra1nyxin/rainyxinmain_StardewValleyMod`) or depend on a higher-level framework (StardewUI or "Better Game Menu") for the extension point and/or the tab's own UI. Decide based on how much custom UI M5's review flow needs.
- Follow the existing soft-dependency pattern already used for GMCM (`Helper.ModRegistry.GetApi<T>(...)`, null-check, called from `OnGameLaunched`) for whichever framework is chosen.

## Verification

No test/lint/CI infrastructure exists in this repo (confirmed in `CLAUDE.md`) — verification is manual:
- `dotnet build` (auto-deploys via `Pathoschild.Stardew.ModBuildConfig` to the local Mods folder already confirmed at `~/Library/Application Support/Steam/steamapps/common/Stardew Valley/Contents/MacOS/Mods/`).
- Launch the game through SMAPI, start/load a save with the in-game language set to the configured source language (Japanese initially).
- For M1: hover inventory/shop items and confirm both the original tooltip and the new translation tooltip render without overlapping, in both possible cursor-relative tooltip positions; toggle the keybind and confirm translation stops/resumes; check the SMAPI console for the mod's log output and for any Harmony patch errors on startup.
- For each later milestone, extend manual play-testing to the specific UI surfaces that milestone targets.
