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
- **Translation index** *(done)*: `TranslationIndex.cs` loads all 30 `Strings/*` tables in both configured locales (suffixed asset name for the source side, one batched `CurrentLanguageCode` flip for the English side) and `TranslationMap.cs` joins them on shared keys into a source-text -> target-text lookup. Built on `GameLoop.SaveLoaded`; rebuildable in-session with the `ls_build_index [source] [target]` console command, and inspectable with `ls_lookup <text>`.
- **Harmony patch** *(done)*: `Patches/HoverTextPatches.cs`. Patching turned out to need only the **StringBuilder** overload of `IClickableMenu.drawHoverText` -- reading the installed 1.6.15 assembly's IL (via `ikdasm`) confirmed `drawToolTip` and the `string` overload of `drawHoverText` both call through to it, so one patch catches every tooltip exactly once instead of double-counting.
  - The vanilla tooltip's screen rect is *captured*, not recomputed: `drawHoverText` draws its own background with `IClickableMenu.drawTextureBox` before anything else, so a second patch records the first `drawTextureBox` call made while inside `drawHoverText`. That sidesteps replicating vanilla's layout math, which varies with the money/buff/craft-ingredient extras.
- **Draw the second tooltip** *(done)*: `TooltipOverlay.cs`, from `Display.Rendered` rather than `RenderedActiveMenu`/`RenderedHud` -- it's the single event that comes after *every* vanilla tooltip, whichever of the two drew it. Placement (above the vanilla box by preference, below as fallback, clamped into the viewport) is in `TooltipLayout.cs`, kept game-type-free and unit-tested.
- **Toggle** *(done)*: `ToggleTranslation` flips `ModConfig.TranslationEnabled`; when off, the patch returns before any lookup or draw.
- **Tests** *(done)*: `tools/ModLogic.Tests` (25 tests, `dotnet test`) covers the map join, the `^` gender-variant split, the word-wrap normalization (including mid-sentence Japanese wrapping, where whitespace has to be dropped rather than collapsed), and every tooltip placement branch -- plus a real-data test built from the extracted `ja`/`en` `Objects` tables.

**Still to verify live** (nothing in this milestone can be checked without launching the game): that both tooltips render without overlapping in both placement branches, that the `SaveLoaded` language flip has no visible side effect with Japanese active, and that the Harmony patches apply cleanly on the installed build.

Files added/changed: `ModEntry.cs` (event wiring, `ApplyPatches`, three console commands), new `TranslationMap.cs`, `TooltipLayout.cs`, `TranslationIndex.cs`, `Patches/HoverTextPatches.cs`, `TooltipOverlay.cs`, and the new `tools/ModLogic.Tests` project. `ModConfig.cs` needed no change — M0 had already added everything M1 reads.

### M2 — Expand literal-translation coverage
- Extend `TranslationIndex` to cover more `Data/*` assets that feed hover/dialogue text beyond `Data/Strings` (e.g. `Data/Objects`, `Data/Crops`, `Data/NPCDispositions` display names, shop/bundle data) as they're found to be missing during play-testing.
  - **`TranslationIndex.StringTables` has the same blind spot the segmentation pipeline did**: it lists only `Strings/*`, so whole-sentence translation misses villager dialogue, events, festivals, TV and schedules exactly as word-hover does. See the data-task section below for the full audit and `segtool.py audit` for the check that now enforces it. Adding the flat families (`Characters/Dialogue/<npc>`, `Strings/schedules/<npc>`, `Data/TV/*`) to that array is cheap and independent of the authoring work — they join on shared keys like any other table.
- **Token-substituted templates** *(done 2026-09-21)* — found in M1 play-testing, the largest single known gap. The journal button's tooltip renders as `日記 （F）`, but the table holds `UI/QuestButton_Hover: '日記 （{0}）' -> 'Journal ({0})'`: the game `string.Format`s the keybind in at draw time, so the rendered text can never match the stored template. **761 of the 8069 shared `ja`/`en` entries are templates whose two locales use the same set of `{N}` tokens** — i.e. roughly 9% of the data was unreachable by whole-string matching alone.
  - Implemented in `TranslationMap` as one mechanism for all of them: at index-build time a pair whose two locales use the **same set** of `{N}` tokens is also registered as a template — the source turned into an anchored regex (literal parts escaped, each token a named capture group, a repeated token a back-reference), kept alongside the untouched target template. On a lookup miss the text is matched against those and the captures substituted into the target **by token index**, since token order differs between locales (`攻撃 +{0}` vs `+{0} Attack`). **699 of the 761 register**; the rest are deliberately refused as unmatchable — a template that is all token, or has two adjacent tokens with no literal between them, would either match any text at all or split it arbitrarily.
  - A captured value that is itself a known source string is translated too, so an item name interpolated into a sentence doesn't stay in the source language.
  - Templates are tried **most-literal-first**, so a specific template wins over a loose one regardless of which table loaded first.
  - Also wired into the per-paragraph pass (`TryLookupOne` is the shared "match one whole string" path), so a concatenated tooltip can have a template as one of its paragraphs.
  - Two performance guards, both needed because a hovered tooltip re-queries **every frame**: results are memoised (misses included), and before running any regex a template is ruled out by an ordinal `Contains` on its longest literal run. Measured over the real tables: an uncached miss went from 2.7ms to 0.065ms, a cached lookup is ~0.0002ms.
  - Tests: 13 new in `tools/ModLogic.Tests` (80 total), including three built from the real extracted `ja`/`en` tables — the actual `日記 （F）` → `Journal (F)` case, the registered template count, and a check that template matching doesn't hijack a string that has an exact translation.
  - **Still not covered**: the word-level segment hover (`SegmentIndex`) has the same gap — its data is keyed by the template text, so a formatted string falls back to the character-class heuristic. Separate fix, since a token's substituted value has to be spliced into the segment list to keep the character-for-character invariant.
- Handle known edge cases surfaced in research (e.g. gendered-string delimiter-splitting bugs in some locales) defensively — fall back to "no translation available" rather than a garbled string.
- Use `ls_log_misses` (added in M1) while play-testing to build the list of what's still uncovered — it logs the raw text of any tooltip that failed to translate, deduped per distinct tooltip.

### M2.1 — Frozen tooltips: per-word hover inside a pinned tooltip
A self-contained vertical slice, **not a dependency of M3 and not dependent on M2**. A hotkey "freezes" the tooltip currently under the cursor: it stays pinned in place with its content fixed, so the cursor is free to move across it and hover individual words, each showing its own small definition tooltip. Pressing the hotkey again unfreezes.

This is worth doing before M3 because it reaches the same end goal (hovering a single word) while **sidestepping M3's hard problem entirely**. M3 has to reverse-engineer text geometry out of the game's draw calls; a frozen tooltip's text is static and its box rect is something M1's patch already captures, so the geometry is known up front instead of reconstructed per frame.

**Research done (2026-09-21, against the installed 1.6.15 assembly):**
- **Hover is polled, not evented, and nothing "consumes" it.** `Game1.updateActiveMenu` calls `activeClickableMenu.performHoverAction(mouseX, mouseY)` every frame; nesting is manual delegation (`GameMenu` forwards to its current page, and ~190 other call sites do likewise by hand). Both `performHoverAction` and `receiveLeftClick` return `void` — there is no `handled` flag anywhere. A handler's result is written to fields (`hoverText`, `hoverItem`, …) that `draw()` reads later in the same frame, and overlapping components are arbitrated purely by which assignment executes last.
- **No consumption model does *not* mean a mod can't block hover.** A Harmony prefix sits above the whole dispatch: returning `false` from a prefix on `performHoverAction` stops the menu computing hover at all, and returning `false` from one on `drawHoverText` stops any vanilla tooltip drawing. The lack of consumption only matters to code that tries to compete with vanilla hover *without* patching it.
- **`drawHoverText` can be re-issued at fixed coordinates.** It takes `overrideX`/`overrideY` parameters (both defaulting to `-1`, i.e. "position relative to the cursor"). Passing explicit values renders a *pixel-identical* vanilla tooltip — money line, buff icons, craft ingredients and all — pinned wherever we want. The frozen tooltip therefore needs no hand-built replica of vanilla's appearance.

**Build order:**
- Freeze state: on the hotkey, stop overwriting the text + box rect that `HoverTextPatches` already captures each frame. No-op the hotkey when nothing is currently hovered, so an empty box can't be frozen.
- Suppress vanilla tooltips while frozen: prefix returning `false` on the **StringBuilder** overload of `IClickableMenu.drawHoverText` — the same single funnel M1 patches, so one prefix suppresses every tooltip in the game.
- Re-issue the frozen tooltip ourselves via `drawHoverText` with `overrideX`/`overrideY` set to the frozen position.
- Optionally also prefix `performHoverAction` to return `false` while frozen, so the menu underneath stops animating button scales and changing `hoverItem` as the cursor crosses the frozen box. Cosmetic, not required.
- Word hit-testing: compute segment x-ranges **once** on freeze (not per frame) by measuring cumulative prefixes in the same font. No runtime tokenizer needed — `tools/extracted-strings/literal-translations` already guarantees that concatenating a segment's `text` values reproduces the source string character-for-character.
- Per-word definition tooltip: reuse `TooltipOverlay`.
- **First milestone deliverable is a debug rectangle per word**, before any definition lookup is wired up — that proves freeze + suppression + per-word hit-testing in isolation.

**Known open question:** where the *text* origin sits inside the box. Vanilla computes it internally, so mapping a word to a rect needs that offset from the box corner. Plan A is to let vanilla draw the box and calibrate the constant once with the debug rectangles above; tooltips with an item icon or buff rows shift the origin, so this may need a case per tooltip shape. Plan B, if calibration gets fiddly, is to draw the replica ourselves — then the layout is ours and every word's rect is known by construction, at the cost of matching vanilla's appearance by hand.

### Data task — hand-segmented word boundaries for the remaining string tables
**Not a code milestone; a data-production gap that caps how good word-level hover can get.** Word boundaries for Japanese can't be derived by rule — hiragana carries particles, inflections and whole words with no orthographic break — so the mod reads them from hand-segmented data rather than tokenizing at runtime.

- **What exists today** (2026-09-22): `tools/extracted-strings/literal-translations/` holds **9,039 hand-authored entries / ~200k Japanese characters**, covering every `Strings/*` table and the nine `Content/Data` assets in `segtool.py`'s `DATA_TABLES`. 11 keys are deliberately skipped (event command scripts the game never draws as one string). `segtool.py status` reports pending=0 across all of it and `validate` passes.
- **What's missing** — found 2026-09-22 when villager dialogue turned up untranslated in play (Pam's `Introduction`, Willy's cutscenes). **Five whole asset families were never in scope**, totalling ~8,900 drawn pages / ~291k Japanese characters — *roughly 1.5x everything authored to date*:

  | Asset family | Files | Keys | Drawn pages | JA chars |
  |---|---|---|---|---|
  | `Characters/Dialogue/*` | 52 | 3,301 | 4,874 | 161,892 |
  | `Data/Events/*` | 44 | 258 scripts → 2,064 spoken lines | 2,660 | 80,793 |
  | `Data/Festivals/*` | 9 | 743 | 1,025 | 30,996 |
  | `Data/TV/*` | 2 | 96 | 96 | 10,827 |
  | `Strings/schedules/*` | 30 | 191 | 207 | 6,139 |

  Plus `Data/Achievements` and `Data/SecretNotes`, which are `Dictionary<int,string>` and still unreadable by `XnbStringTool` (this one *was* tracked). Everything else the game ships a `.ja-JP` variant of is a texture or font.

  These split into two shapes of work. `Characters/Dialogue`, `Strings/schedules` and `Data/TV` are flat `key -> text` dictionaries — the same shape as every table already done, so they feed `segtool.py batch` directly with no new tooling. `Data/Events` and the 25 script-valued `Data/Festivals` keys are not: each value is a `/`-separated command script with dialogue embedded as `speak <npc> "..."` / `message "..."` arguments, so they need an extractor that lifts the quoted spoken strings out and keys them by the *spoken* text (what the game actually draws) rather than by the script key.

- **Why it was missed, and what now prevents a repeat.** The scope criterion was *"dictionary-shaped tables under `Content/Strings/`"* — a file-format test inherited from what `XnbStringTool` had been pointed at. `Characters/Dialogue/Pam.ja-JP.xnb` is a plain `Dictionary<string,string>` and parses with no tool changes; it was never rejected, just never looked at. Worse, `segtool.py`'s `tables()` enumerated `os.listdir(extracted-strings/ja/)` plus a hardcoded `DATA_TABLES` — so **coverage was measured against the set of files somebody had already chosen to extract.** `pending=0` could only ever mean "nothing left in what we already imported"; it was structurally incapable of naming a family nobody had considered, and the docs then promoted that signal to "every table". **`segtool.py audit` now re-derives the denominator from the game install** — it walks `Content/**/*.ja-JP.xnb`, classifies each asset as covered (from what is actually extracted in the repo) / excluded-with-a-reason / known-gap, and **exits non-zero on anything accounted for nowhere**. Run it after any game update; adding an asset to `EXCLUDED` or `KNOWN_GAPS` is now an explicit decision on record rather than an omission.
- **What happens without it** (observed live, 2026-09-21): word hover falls back to a character-class heuristic that groups runs of the same script. It blobs an unbroken kanji run (`長時間快適` — really 長時間 + 快適) and, worse, a long hiragana run (`たちはきっといるはず` — really たち + は + きっと + いる + はず). Usable as a fallback, not as the product.
- **The invariant any new data must satisfy**: concatenating a description's segment `text` values must reproduce its `japanese` field character-for-character. This is what lets the mod compute each segment's on-screen position by measuring prefixes instead of tokenizing, and it is already checked programmatically for all 744 `Objects` descriptions.
- **Out of scope here**: the generation pipeline itself, exactly as with M4's explanatory data. The data is produced externally and dropped into the folder; this entry exists so the coverage gap is tracked rather than rediscovered every time a non-item string segments badly.

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
