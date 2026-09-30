# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

A SMAPI mod for Stardew Valley that translates the game's UI for language learners: hover text to see a translation, hover a word for its gloss, click it to save a flashcard.

**This repo is the Chinese fork.** It was copied from the Japanese mod (`LearnJapaneseStardewValleyMod`) and is being turned into a **Simplified Chinese → English** mod for learners of Chinese: the game runs in 中文 (`zh-CN`) and the mod explains it in English. The migration is in progress, and its plan and status are in `TODOs.md` under "Chinese migration". Until it's finished:

- The data pipeline is already Chinese. `tools/segment-data/` authors `assets/segments/zh/` with pinyin readings, and `tools/extracted-strings/{zh,data-zh,content-zh}` hold the source text.
- Much of the runtime and the docs still describe Japanese: kana/romaji labels, `KanaRomaji.cs`, the ja HUD clock, macron-only font synthesis, and ja examples in comments and `HowItWorks.md`. Treat that as the inherited design, not as a statement of what zh needs, and check "Chinese migration" before changing it.
- The mod has its own identity (`com.oldclovercat.zhlanguagestudy`, deployed to `Mods/LanguageStudyChinese`) so it never overwrites the Japanese mod. Its manifest's `StudyLanguage` is `zh`, so it runs only while the game is in 中文, and it can be installed alongside a Japanese copy that has the same activation code (see "Language activation" in `HowItWorks.md`). A Japanese copy from before that change still patches in every language, so disable it (prefix its folder with `.`) when testing.
- `origin` is this fork's repo (`sesameneko/LearnChineseStardewValleyMod`). The Japanese repo is the `upstream-ja` remote, for pulling across fixes to shared code. Never push to it.

Further reading:

- **`HowItWorks.md`:** how each feature works and why it's built that way (hover translation, frozen tooltips, word-position detection and segment lookup, flashcards, macron font, word data). Read the relevant section before making architectural changes. Written for the Japanese mod; the mechanisms carry over, the language details don't.
- **`PostMortems.md`:** the history behind the "Hard rules" below.
- **`TODOs.md`:** open work, including things built but not yet verified in game.

## Build and verify

```
dotnet build LanguageStudyStardewValleyMod.csproj
```

- Name the project explicitly. The root has both a `.sln` and a `.csproj`, so a bare `dotnet build` fails.
- `Pathoschild.Stardew.ModBuildConfig` deploys the built mod into SMAPI's `Mods` folder on every build; there is no separate deploy step. If it can't find the game, set `<GamePath>` or add a `game.config.json`.
- To check that it compiles without deploying into the game, add `-p:EnableModDeploy=false -p:EnableModZip=false`.
- The mod itself has no tests. Verification is `dotnet build` plus launching the game (`scripts/run.sh` builds and launches SMAPI directly). Set the in-game language to 中文.
- The tools under `tools/` have xunit suites. Run `dotnet test` from inside each tool's directory.

## Development workflow

A game restart is slow, so avoid needing one:

- **Parameterize debug console commands** (`helper.ConsoleCommands.Add` in `ModEntry.cs`) so one session can try many variations. See `ls_spike_locale` for the pattern.
- **Put logic that doesn't need live game objects in plain classes with no `StardewValley`/`MonoGame` types** (lookups, string matching, hit-testing math, data parsing), and test them in `tools/ModLogic.Tests`. That project can't reference the mod (it links x64-only game assemblies), so it `<Compile Include>`s the source files directly. Any game type in such a file breaks it. Save game launches for Harmony patches, rendering and GMCM.
- **There's no decompiled 1.6.15 source.** For questions about game behaviour, disassemble the installed assembly with `ikdasm` (`ikdasm "…/Contents/MacOS/Stardew Valley.dll" > sdv.il`) and read the IL, rather than trusting older decompiles or the wiki.

## Hard rules

- **Never draw UI overlays from `Display.Rendered`.** It draws onto the world render target, which ends up underneath every menu and at a different scale. Draw from `RenderedHud` / `RenderedActiveMenu`, and hit-test with `Game1.getMouseX(ui_scale: true)`. This bug has happened twice.
- **Draw overlays from exactly one pass per frame:** `ModEntry.DrawOverlays`, called from `RenderedActiveMenu` when a menu is open and from `RenderedHud` otherwise. Z-order in a Deferred batch is call order, so layer depth can't fix ordering bugs. Use `ls_draw_trace [frames]` rather than guessing.
- **Don't sync `IGenericModConfigMenuApi.cs` from upstream** without testing that `GetApi` still succeeds in-game. Pintail fails the whole proxy silently if any method doesn't exist in the installed GMCM (1.16.0). The file deliberately stops before `AddComplexOptionWithGamepadSupport`. Keep its `#nullable disable` so it stays diffable against upstream. Follow its `GetApi`-returns-null pattern for any other soft dependency.
- **Don't remove `<Compile Remove="tools/**/*.cs" />`** from the `.csproj`. Without it the tools compile into the mod.
- **Everything in `assets/` ships**, whatever its file type. Keep scratch files out.
- Only the **StringBuilder** overload of `drawHoverText` is patched; the other tooltip paths all call it. Don't add patches on the others.

## Invariants

- **A copy is active only while the game language equals its manifest `StudyLanguage`.** Copies for other languages may be installed alongside it (see `HowItWorks.md`), so:
  - Harmony patches exist only while active. `ApplyPatches` runs on each activation, and deactivation calls `UnpatchAll(this.harmony.Id)`. Never call it without the ID: that removes every mod's patches.
  - Every new event handler returns early unless `IsActive`, and every new console command goes through `AddCommand`.
  - Activation is polled in `OnLanguageTick`, never driven by `LocaleChanged`, because `TranslationIndex.Build` flips the language mid-call.
- **`TranslationMap` lookup order:** exact whole match, then paragraph by paragraph, then templates on the whole text. A template tried earlier swallows the following paragraphs.
- **Pinyin is stored one syllable per hanzi, space-separated, with tone marks** (`牧场` → `mù chǎng`), and `tools/segment-data/pinyin.py` defines and checks it. Runtime code that displays or compares pinyin must read that form. Joining syllables into words (`mùchǎng`) is a display step, never a stored one. (`KanaRomaji.cs` is Japanese-only and will be removed in the migration. Its Python twin is already gone.)
- **Keep `SegmentSource`** on any new path that builds segments with `with { ... }`. Drop it where the text no longer comes from one entry, as `SegmentIndex.Fill` does. Flashcard sentence pointers depend on it.
- **One segment schema** for every file in `assets/segments/zh/`: `{chinese, english, segments[{text, pinyin, gloss}]}`. `SegmentDataLoader` silently skips anything else. `segtool.py merge` enforces that the segments reproduce the source string character for character, and that each segment's pinyin is a dictionary reading of its hanzi.
- **Text word hover should ignore** is decided by content in `TextHitTest.IsHoverable`: a fallback-split word with no letters (the hotbar's 1-9, 0, -, =) is skipped; words from segment data never are.
- **Glyph-capture transpilers** leave a method alone and log a warning when the IL doesn't match. After a game update, check the `Glyph capture: ...` startup log line.
- **`TranslationIndex` is built on `SaveLoaded` or on activation (an update tick), never during a draw**, because it temporarily changes the language code.
- Keybinds are checked with `.JustPressed()` in `OnButtonsChanged`, not `Input.ButtonPressed`, which fires twice for multi-key `KeybindList`s.

## Code map

- `ModEntry.cs`: entry point. Registers SMAPI events, console commands, and Harmony patches (`ApplyPatches`, using its own `FindOverload` helper because `drawHoverText` takes 25 arguments). `ModEntry.Log(...)` works from anywhere.
- Hover translation: `TranslationIndex` (loads the tables) → `TranslationMap` / `DataTextShapes` (pure) → `Patches/HoverTextPatches` → `TooltipLayout` (pure) / `TooltipOverlay`.
- Word hover: `Patches/GlyphCapturePatches`, `Patches/TextCapturePatches` → `GlyphHitTest` (pure), with `TextHitTest` as the fallback → `WordHoverOverlay`. Data comes from `SegmentDataLoader` / `SegmentIndex`, plus `ClockSegments` (pure) for the ja HUD clock.
- Dialogue bubble: `Patches/DialogueCapturePatches` (records each parsed dialogue's key and page-to-segment map) → `DialoguePages` (pure) → `DialogueTranslation` → `DialogueBubbleOverlay`.
- Font: `ExtendedFont` + `FontGlyphSynth` (pure) add macron vowels to `smallFont`. `FontSafeText` falls back to doubled vowels.
- Flashcards: `FlashcardDeck`, `FlashcardContext` (pure), `FlashcardStore`, `FlashcardCapture`, `FlashcardsPage`, `Patches/GameMenuPatches`.
- Activation: `LanguageActivation` (pure: activation tracker, prompt decision) → `LanguagePrompt` (finds sibling copies, shows the title-screen popup) / `LanguageChoiceMenu`.
- `ModConfig.cs` is the config (`config.json`); `manifest.json`'s `Version` should match releases.
- `tools/` holds standalone projects, not part of the mod:
  - `XnbStringTool` (+ `.Tests`): `.xnb` reader/writer, used because the game's assemblies won't load on arm64.
  - `ModLogic.Tests`: tests for the game-free classes.
  - `extracted-strings/`: en/zh string tables as JSON (`zh`, `data-zh`, `content-zh`). The ja copies remain only because `ModLogic.Tests` still reads them.
  - `segment-data/`: the `segtool.py` authoring pipeline for `assets/segments/zh/`, the hand-authored source of truth, and `pinyin.py`, which normalises and checks readings (needs `pip3 install --user pypinyin`). Each folder has a `README.md`.
- **`segtool.py status` can't see asset families nobody extracted.** Run `segtool.py audit` after a game update. When adding scope, put the new asset in `EXCLUDED` or `KNOWN_GAPS` rather than leaving it unaccounted for.
