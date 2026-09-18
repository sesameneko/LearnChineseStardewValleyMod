# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

A SMAPI (Stardew Modding API) mod for Stardew Valley that translates the game's UI for language learners (hover a piece of text, see a translation). Started as a bare scaffold; real feature work is now underway per the milestones in **`Plan.md`** — read that first for the roadmap, the three translation modes, and the researched technical constraints (e.g. the game's single-active-locale limitation and how the plan works around it) before making architectural changes.

## Build

```
dotnet build LanguageStudyStardewValleyMod.csproj
```

The project file must be named explicitly — the repo root has both a `.sln` and a `.csproj`, so a bare `dotnet build` fails with "more than one project or solution file." (`scripts/run.sh` already does this correctly.)

This project uses `Pathoschild.Stardew.ModBuildConfig`, which automatically copies the built mod (DLL, `manifest.json`, `assets/`, etc.) into the local SMAPI `Mods` folder after every build, so `dotnet build` is normally sufficient to deploy the mod for testing — there is no separate publish/deploy step.

The build config resolves the Stardew Valley game path automatically on Windows/Linux/macOS. If it can't find the game, it requires a `<GamePath>` in the `.csproj` or a `game.config.json` alongside it — check the ModBuildConfig docs if the build fails with a "can't find the game" error.

There's no test/lint/CI config for the mod itself — `dotnet build` (and manually launching the game via SMAPI) is the only verification available for it. The standalone tools under `tools/` (see Architecture below) do have real xunit test suites, run via `dotnet test` from within each tool's own directory.

`scripts/run.sh` builds the mod and launches SMAPI directly (bypassing Steam's library UI) for faster manual restarts during live testing. Steam still needs to be running in the background.

## Development workflow

A full game restart (SMAPI + Stardew Valley boot, plus manual menu navigation) is slow, so prefer these two practices to cut down how often one is actually needed:

- **Parameterize debug/test console commands** (added via `helper.ConsoleCommands.Add`) so they take arguments — an asset name, a locale code, etc. — where it makes sense. That lets many test variations be tried within a single running game session instead of rebuilding and restarting for each one. See `ls_spike_locale` in `ModEntry.cs` for the pattern.
- **Keep real logic in plain, game-independent classes** wherever it doesn't actually need a live `StardewValley`/`MonoGame` object — lookup tables, string/segment matching, position or hit-testing math, JSON parsing for bundled data. Give that logic its own class with no dependency on game state, plus a companion xunit test project (see `tools/XnbStringTool.Tests` for the pattern already used in this repo). It can then be iterated on in milliseconds via `dotnet test`, reserving actual game launches for what can only be verified live: Harmony patch behavior, real rendering, and GMCM's UI.

## Architecture

- `ModEntry.cs` — the mod's entry point (`ModEntry : StardewModdingAPI.Mod`). SMAPI instantiates this and calls `Entry(IModHelper helper)` once at startup. Game-lifecycle hooks are wired here via `helper.Events` (`Input.ButtonsChanged`, `GameLoop.UpdateTicked`, `GameLoop.DayStarted`/`DayEnding`, `GameLoop.SaveCreating`, `GameLoop.GameLaunched`). New behavior is added by subscribing to more SMAPI events in `Entry` and implementing the corresponding handler method.
  - `ModEntry.Instance` is a static singleton set in `Entry`, letting `ModEntry.Log(...)` be called from anywhere in the codebase without threading a logger through.
  - Keybinds are checked in `OnButtonsChanged` via `.JustPressed()` (not `Input.ButtonPressed`, which double-fires for multi-key `KeybindList`s).
  - `ApplyPatches()` is the designated place for Harmony patches (Harmony is enabled via `<EnableHarmony>true</EnableHarmony>` in the `.csproj`). It currently patches `IClickableMenu.drawHoverText` and `drawTextureBox` for the M1 hover-translation feature (see `Patches/HoverTextPatches.cs`). Overloads are resolved via the local `FindOverload` reflection helper rather than `AccessTools` parameter lists, because `drawHoverText` takes 25 arguments.
  - Debug/test console commands are registered here via `helper.ConsoleCommands.Add`; see the "Development workflow" note above on parameterizing them.
- **M1 hover translation** (see `Plan.md`), split so the non-game half is unit-testable:
  - `TranslationMap.cs` — *pure, no game types.* The source-text → target-text lookup, built by joining two locale variants of a string table on their shared keys. Handles the game's `^` gender-variant delimiter and, on lookup, the newlines the game inserts when word-wrapping (including mid-sentence in Japanese, which is why it keeps a whitespace-stripped index as well as a whitespace-collapsed one).
  - `TooltipLayout.cs` — *pure, no game types.* Where the translation tooltip goes relative to the vanilla one (above by preference, below as fallback, clamped into the viewport), plus title/body text composition.
  - `TranslationIndex.cs` — the game-side loader that feeds `TranslationMap`. Loads each `Strings/*` table in both locales: the locale-suffixed asset name (`Strings/Objects.ja-JP`) for anything but English, and a *single* temporary `LocalizedContentManager.CurrentLanguageCode` flip for the whole batch for English (which has no suffixed file on disk). Built on `SaveLoaded`, never mid-draw, because of that flip.
  - `Patches/HoverTextPatches.cs` — the Harmony patches. Only the **StringBuilder** overload of `drawHoverText` is patched: `drawToolTip` and the `string` overload both funnel into it (verified against the installed 1.6.15 assembly's IL), so patching it alone catches every tooltip exactly once. The vanilla tooltip's exact screen rect is *captured* rather than recomputed — `drawHoverText` draws its own background via `drawTextureBox` first, so the first such call made while inside `drawHoverText` is the tooltip's box.
  - `TooltipOverlay.cs` — draws the captured translation as a second box in `Display.Rendered` (the one draw event that comes after every vanilla tooltip, menu or HUD), and is cleared in `Display.Rendering` so nothing goes stale.
- `ModConfig.cs` — the strongly-typed config class SMAPI serializes to/from the player's `config.json` (via `helper.ReadConfig<ModConfig>()` / `helper.WriteConfig(...)`). Current fields: `TranslationEnabled` (bool), `ToggleTranslation` (`KeybindList`, registered with GMCM), and `SourceLanguage`/`TargetLanguage` (locale codes, default `"ja"`/`"en"` — not yet exposed in the config UI).
- `IGenericModConfigMenuApi.cs` — a copy of the [Generic Mod Config Menu](https://www.nexusmods.com/stardewvalley/mods/5098) API interface (from spacechase0/StardewValleyMods), used so this mod can integrate with GMCM's in-game settings UI without taking a hard dependency on it. `ModEntry.OnGameLaunched` fetches this API via `Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>(...)` and returns early if GMCM isn't installed — the same pattern should be followed for any other soft mod dependency. The file has `#nullable disable` (with a narrow `#nullable enable` island around the one method that needs it) since it's meant to stay a trivially-diffable copy of upstream, not hand-annotated by us.
  - **Do not sync this file from upstream `develop` without testing `GetApi` still succeeds in-game.** SMAPI's proxy layer (Pintail) requires *every* method declared in this interface to be mappable against the real installed GMCM DLL — one method upstream has that the installed version doesn't breaks the *entire* proxy silently (`GetApi` logs an error and returns null; GMCM integration just stops working, no crash). This already happened once (see `Plan.md`'s constraints section for the fix and the reasoning behind which methods were safe to keep) — the installed GMCM here is 1.16.0, and the interface is deliberately missing everything from `AddComplexOptionWithGamepadSupport` onward because of it.
- `manifest.json` — SMAPI's mod manifest (unique ID, entry DLL, version, min API version). Keep `Version` here in sync with releases.
- `assets/` — bundled data the mod loads at runtime (wired into `.csproj` via a `CopyToOutputDirectory` `None` item, since arbitrary files aren't copied to the build output by default). Currently just `assets/translations/explanatory/ja.json`, a stub for the mode-2 semi-literal translation data described in `Plan.md`.
- `tools/` — standalone developer tooling, **not part of the mod** and explicitly excluded from `LanguageStudyStardewValleyMod.csproj`'s compilation (it globs `.cs` recursively by default, which caused a real duplicate-assembly-attribute build failure before the exclusion was added — don't remove the `<Compile Remove="tools/**/*.cs" />` item). Each subfolder is its own independent project:
  - `tools/XnbStringTool/` — a from-scratch reader/writer for the game's `.xnb` string-table format (reverse-engineered against MonoGame's own source; see its `README.md`). Reused instead of depending on the game's own assemblies, which are x64-only and won't load on this Mac's arm64 .NET SDK.
  - `tools/XnbStringTool.Tests/` — its xunit test suite, including "golden master" tests against the real installed game.
  - `tools/ModLogic.Tests/` — xunit tests for the mod's *game-independent* classes (`TranslationMap`, `TooltipLayout`). It can't project-reference the mod (net6.0, linked against x64-only game assemblies), so it `<Compile Include>`s those source files directly — which only works because they deliberately reference no `StardewValley`/`MonoGame` types. Keep it that way when adding logic: put anything testable in a game-free class and link it here.
  - `tools/extracted-strings/` — data extracted/translated with the tool above: readable JSON for every `en`/`ja` string table, plus hand-translated (from Japanese, not just a copy of the English) literal names and word/phrase-segmented descriptions for `Objects.xnb` — see its `README.md` for the schema.
