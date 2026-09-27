# Post-mortems

Bugs that cost real time to find, what caused them, and how to avoid them next time. `CLAUDE.md` keeps each one as a one-line rule; the history is here.

## Overlays drawn from `Display.Rendered` (twice)

**Symptom:** the overlay appeared behind every menu, and offset from where it should have been.

**Cause:** SMAPI raises `Display.Rendered` after `Game1._draw` has fully returned, against the *world* render target. The game composites that target *underneath* the UI one. The world target also scales by `Game1.options.zoomLevel` while the UI scales by `uiScale`, so positions came out shifted by the ratio between the two.

**Fix:** draw from `Display.RenderedHud` / `Display.RenderedActiveMenu`. They run inside the draw, in UI mode, in the same sprite batch and coordinate space as the menus. Hit-test against `Game1.getMouseX(ui_scale: true)`: the no-arg `getMouseX()` picks its space from `Game1.uiMode`.

This was introduced once for the translation tooltip, which was first drawn from `Display.Rendered` because it's the one event that comes after every vanilla tooltip, and again for the word-hover overlay.

## Word overlay buried under the frozen tooltip

**Symptom:** the word-hover outline and label were drawn underneath the frozen tooltip instead of on top of it.

**Cause:** the overlays were drawn from both the HUD pass and the menu pass. The later pass redrew the frozen tooltip *after* the word overlay had already been drawn on top of it, and a once-per-frame guard (`WordHoverOverlay.DrawnThisFrame`) then suppressed the second, correctly ordered attempt. The game also raises `RenderedHud` twice per `Display.Rendering`, so a latch that assumes it knows which pass is last can't be right.

**What didn't work:** three separate layer-depth fixes. In a Deferred sprite batch, z-order is call order, so layer depth can't fix an ordering bug.

**Fix:** `ModEntry.DrawOverlays` is the single place the overlays are drawn. It's called from `RenderedActiveMenu` when a menu is open and from `RenderedHud` otherwise (`Game1.activeClickableMenu is null`), because the menu pass comes later and paints over anything the HUD pass drew.

**Lesson:** `ls_draw_trace [frames]` logs the per-frame draw sequence. Use it before guessing at draw-order bugs.

## GMCM integration silently disabled by an interface sync

**Symptom:** GMCM integration stopped working with no crash. `GetApi` logged an error and returned null.

**Cause:** `IGenericModConfigMenuApi.cs` was refreshed from upstream `develop`, which is ahead of the installed GMCM (1.16.0). SMAPI's Pintail proxy needs *every* declared method to map onto the real DLL, so one newer method (`AddComplexOptionWithGamepadSupport`) broke the whole proxy.

**Fix:** that method and everything declared after it upstream were removed (`SetTitleScreenOnlyForNextOptions`, `OnFieldChanged`, `OpenModMenu`, `OpenModMenuAsChildMenu`, `TryGetCurrentMenu`). Everything before it (`Register` through `AddPageLink`, including `AddKeybind`/`AddKeybindList`) is known to work against 1.16.0, because Pintail reports the *first* method it can't map, in declaration order.

**Why it's easy to repeat:** there's no shortcut to the right upstream version. GMCM's repo tags stop at 1.8.1, so there's no tag for 1.16.0. The installed DLL is the only ground truth, and it can't be reflected into on this Mac either, because it references MonoGame types that won't load under the arm64 .NET SDK. Test that `GetApi` succeeds in game after any change to the file.

## Coverage counters read "done" while whole asset families were missing

**Symptom:** villager dialogue, events, festivals, TV and schedules had no segment data, while every `segtool.py status` counter read `pending=0`.

**Cause:** `status` measures coverage against `tools/extracted-strings/`, i.e. against files somebody already chose to extract. It can't report an asset family nobody imported. `tables()` listed `extracted-strings/ja/` plus a hardcoded `DATA_TABLES`, so `pending=0` could only ever mean "nothing left in what we already imported", and the docs then described that as "every table". The scope itself had been set by a file-format test, "dictionary-shaped tables under `Content/Strings/`", inherited from what `XnbStringTool` had first been pointed at. `Characters/Dialogue/Pam.ja-JP.xnb` is a plain `Dictionary<string,string>` that parses with no tool changes. It was never rejected, just never looked at. The five missing families came to 6,519 entries, found on 2026-09-22 when villager dialogue showed up untranslated in play.

**Fix:** `segtool.py audit` walks the installed game's `Content/**/*.ja-JP.xnb` and classifies every localized asset as covered, excluded with a reason, or a known gap, and exits non-zero on anything that's in none of those. It then checks text coverage: every Japanese character must be held by an authored entry, or by a skipped script whose spoken lines were extracted. That second check found the Quests completion lines (a record field missing from `DATA_TABLES`) and the dialogue inside skipped event scripts.

## Duplicate assembly attributes from `tools/`

**Symptom:** the mod build failed with duplicate assembly attributes.

**Cause:** the SDK-style `.csproj` compiles every `.cs` file under its folder by default, which included each standalone tool project under `tools/`.

**Fix:** `<Compile Remove="tools/**/*.cs" />` in `LanguageStudyStardewValleyMod.csproj`.
