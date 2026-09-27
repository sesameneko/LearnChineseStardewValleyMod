# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

A SMAPI mod for Stardew Valley that translates the game's UI for language learners: hover text to see a translation, hover a word for its gloss, click it to save a flashcard. Further reading:

- **`Plan.md`:** roadmap, milestones, and researched constraints such as the game's single-active-locale limitation. Read it before making architectural changes.
- **`HowItWorks.md`:** how each feature works (hover translation, word-position detection, flashcards, macron font, word data).
- **`PostMortems.md`:** the history behind the "Hard rules" below.
- **`TODOs.md`:** segment-data table priority. It overrides `Plan.md`'s ordering.

## Build and verify

```
dotnet build LanguageStudyStardewValleyMod.csproj
```

- Name the project explicitly. The root has both a `.sln` and a `.csproj`, so a bare `dotnet build` fails.
- `Pathoschild.Stardew.ModBuildConfig` deploys the built mod into SMAPI's `Mods` folder on every build; there is no separate deploy step. If it can't find the game, set `<GamePath>` or add a `game.config.json`.
- The mod itself has no tests. Verification is `dotnet build` plus launching the game (`scripts/run.sh` builds and launches SMAPI directly).
- The tools under `tools/` have xunit suites. Run `dotnet test` from inside each tool's directory.

## Development workflow

A game restart is slow, so avoid needing one:

- **Parameterize debug console commands** (`helper.ConsoleCommands.Add` in `ModEntry.cs`) so one session can try many variations. See `ls_spike_locale` for the pattern.
- **Put logic that doesn't need live game objects in plain classes with no `StardewValley`/`MonoGame` types** (lookups, string matching, hit-testing math, data parsing), and test them in `tools/ModLogic.Tests`. That project can't reference the mod (it links x64-only game assemblies), so it `<Compile Include>`s the source files directly. Any game type in such a file breaks it. Save game launches for Harmony patches, rendering and GMCM.

## Hard rules

- **Never draw UI overlays from `Display.Rendered`.** It draws onto the world render target, which ends up underneath every menu and at a different scale. Draw from `RenderedHud` / `RenderedActiveMenu`, and hit-test with `Game1.getMouseX(ui_scale: true)`. This bug has happened twice.
- **Draw overlays from exactly one pass per frame:** `ModEntry.DrawOverlays`, called from `RenderedActiveMenu` when a menu is open and from `RenderedHud` otherwise. Z-order in a Deferred batch is call order, so layer depth can't fix ordering bugs. Use `ls_draw_trace [frames]` rather than guessing.
- **Don't sync `IGenericModConfigMenuApi.cs` from upstream** without testing that `GetApi` still succeeds in-game. Pintail fails the whole proxy silently if any method doesn't exist in the installed GMCM (1.16.0). The file deliberately stops before `AddComplexOptionWithGamepadSupport`. Keep its `#nullable disable` so it stays diffable against upstream. Follow its `GetApi`-returns-null pattern for any other soft dependency.
- **Don't remove `<Compile Remove="tools/**/*.cs" />`** from the `.csproj`. Without it the tools compile into the mod.
- **Everything in `assets/` ships**, whatever its file type. Keep scratch files out.
- **Don't re-run `tools/segment-data/migrate_names.py.retired`.** It has already been applied, and re-running it would flatten hand-authored segments.
- Only the **StringBuilder** overload of `drawHoverText` is patched; the other tooltip paths all call it. Don't add patches on the others.

## Invariants

- **`TranslationMap` lookup order:** exact whole match, then paragraph by paragraph, then templates on the whole text. A template tried earlier swallows the following paragraphs.
- **Keep `KanaRomaji.cs` and `tools/segment-data/kana_to_romaji.py` in step.** A rule changed in one belongs in the other.
- **Keep `SegmentSource`** on any new path that builds segments with `with { ... }`. Drop it where the text no longer comes from one entry, as `SegmentIndex.Fill` does. Flashcard sentence pointers depend on it.
- **One segment schema** for every file in `assets/segments/ja/`: `{japanese, english, segments[{text, kana, gloss, reading}]}`. `SegmentDataLoader` silently skips anything else. `segtool.py merge` enforces that the segments reproduce the source string character for character.
- **Text word hover should ignore** is decided by content in `TextHitTest.IsHoverable`: a fallback-split word with no letters (the hotbar's 1-9, 0, -, =) is skipped; words from segment data never are.
- **Glyph-capture transpilers** leave a method alone and log a warning when the IL doesn't match. After a game update, check the `Glyph capture: ...` startup log line.
- **`TranslationIndex` is built on `SaveLoaded`, never during a draw**, because it temporarily changes the language code.
- Keybinds are checked with `.JustPressed()` in `OnButtonsChanged`, not `Input.ButtonPressed`, which fires twice for multi-key `KeybindList`s.

## Code map

- `ModEntry.cs`: entry point. Registers SMAPI events, console commands, and Harmony patches (`ApplyPatches`, using its own `FindOverload` helper because `drawHoverText` takes 25 arguments). `ModEntry.Log(...)` works from anywhere.
- Hover translation: `TranslationIndex` (loads the tables) → `TranslationMap` / `DataTextShapes` (pure) → `Patches/HoverTextPatches` → `TooltipLayout` (pure) / `TooltipOverlay`.
- Word hover: `Patches/GlyphCapturePatches`, `Patches/TextCapturePatches` → `GlyphHitTest` (pure), with `TextHitTest` as the fallback → `WordHoverOverlay`. Data comes from `SegmentDataLoader` / `SegmentIndex`, plus `ClockSegments` (pure) for the ja HUD clock.
- Font: `ExtendedFont` + `FontGlyphSynth` (pure) add macron vowels to `smallFont`. `FontSafeText` falls back to doubled vowels.
- Flashcards: `FlashcardDeck`, `FlashcardContext` (pure), `FlashcardStore`, `FlashcardCapture`, `FlashcardsPage`, `Patches/GameMenuPatches`.
- `ModConfig.cs` is the config (`config.json`); `manifest.json`'s `Version` should match releases.
- `tools/` holds standalone projects, not part of the mod:
  - `XnbStringTool` (+ `.Tests`): `.xnb` reader/writer, used because the game's assemblies won't load on arm64.
  - `ModLogic.Tests`: tests for the game-free classes.
  - `extracted-strings/`: en/ja string tables as JSON.
  - `segment-data/`: the `segtool.py` authoring pipeline for `assets/segments/ja/`, the hand-authored source of truth. Each folder has a `README.md`.
- **`segtool.py status` can't see asset families nobody extracted.** Run `segtool.py audit` after a game update. When adding scope, put the new asset in `EXCLUDED` or `KNOWN_GAPS` rather than leaving it unaccounted for.
