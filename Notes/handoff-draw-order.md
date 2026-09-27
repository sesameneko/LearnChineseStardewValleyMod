# Word-hover overlay draws *behind* the tooltip — cause found, fix untested in-game

**Status:** cause identified in the code and fixed; builds clean; **not yet verified in-game.**
Verify with the recipe under "How to verify" below.

## The bug

The word-hover debug overlay (a coloured outline around the hovered word, plus a text label
above it) is drawn *underneath* the tooltip it is annotating, so it is mostly invisible.

Only the **z-order** is wrong. The right word is identified, the label content is right, and the
label position is right — in the 02:49 screenshot the label reads `nansa) * flexibility`, the
right-hand half of `柔軟さ (jūnansa) — flexibility`; the left half is hidden behind the tooltip box.

## The cause: the pinned tooltip was drawn twice per frame

`FrozenTooltip.Draw` was called from *both* `OnRenderedHud` and `OnRenderedActiveMenu`. The word
overlay is guarded to one draw per frame (`WordHoverOverlay.DrawnThisFrame`), and it draws from
`drawHoverText`'s postfix — so with a menu open the per-frame sequence was:

1. HUD pass → `FrozenTooltip.Draw` → vanilla `drawHoverText` → postfix → **overlay drawn** (guard set)
2. the menu draws (its own tooltip suppressed by the freeze prefix)
3. menu pass → `FrozenTooltip.Draw` **again** → the tooltip is repainted **over** the overlay, and
   the postfix's second overlay call no-ops on the guard

Tooltip → overlay → tooltip. No layer depth can fix a call-order bug, which is why three depth
attempts in a row failed.

## The fix

`ModEntry.DrawOverlays` — one method that draws the frozen tooltip, the translation tooltip and the
word overlay, called from **exactly one** pass per frame: the active menu's when a menu is open, the
HUD's otherwise (`Game1.activeClickableMenu is null`). This mirrors the policy `DrawWordHover`
already used, and now everything shares it.

`WordHoverOverlay.TopLayerDepth` (the sort-mode reflection from attempt 4) is gone, replaced by a
plain `LayerDepth = 1f` constant with the reasoning recorded in its doc comment. It was run and did
not fix anything, which is itself evidence: the batch is Deferred, so depth is ignored and call
order decides.

## How to verify

New console command `ls_draw_trace [frames]` (default 5) logs one line per frame naming the draws in
order, e.g. `[trace] frame 3: menuPass -> frozenTooltip -> reissue -> wordOverlay`. Z-order here is
call order, which no single log line and no screenshot can show.

1. Hover an **Objects** item (wood, stone, a sapling) to get green outlines. Word capture is
   hardcoded on in `TextCapturePatches.Enabled`; it used to need `ls_word_hover on` every launch,
   which twice made a working build look broken.
2. `ls_draw_trace`. The trace must show `wordOverlay` **after** the last `frozenTooltip`/`reissue`
   of the frame, and exactly one of each per frame.
3. Repeat with the tooltip frozen (`Z`) and unfrozen, with a menu open (inventory) and without.

Done when the outline and the **full** label sit on top of the tooltip box in all four combinations.

## What is verified about the game (keep)

From disassembling the installed 1.6.15 build (`ikdasm` on `Stardew Valley.dll` and
`MonoGame.Framework.dll`), not from memory:

- **Tooltips draw at layerDepth 0.9–0.95**, both box and text.
- **Sort modes used across the game's `SpriteBatch.Begin` calls:** Deferred ×89, FrontToBack ×23,
  Immediate ×6, BackToFront ×2, Texture ×1.
- **`drawHoverText` is a single funnel:** `drawToolTip` → `drawHoverText(string)` →
  `drawHoverText(StringBuilder)`. Only the StringBuilder overload is patched.
- **SMAPI's `Display.Rendered` runs after `Game1._draw` returns**, i.e. against the *world* render
  target, which the game composites underneath the UI one. Never draw UI from it.

## What was tried and failed — do not repeat

1. **Draw from `Display.Rendered`.** Under all UI *and* offset from the cursor (world target scales
   by `zoomLevel`, UI by `uiScale`). Fixed by moving to `RenderedHud`/`RenderedActiveMenu`; that
   part is genuinely solved and should not be reverted.
2. **`layerDepth = 0f`.** Behind the tooltip.
3. **`layerDepth = 1f` at `RenderedHud`.** Behind. A toolbar tooltip draws *after* that event, so
   the overlay draw moved into `drawHoverText`'s postfix — same batch, immediately after the box.
   Still behind (because of the second frozen-tooltip draw, unknown at the time).
4. **Read the batch's real sort mode** and pick `0f`/`1f` from it (`c4f8ee5`). **Run, and failed.**
   Removed.

## Hypotheses not needed, but not excluded either

If the fix above turns out to be incomplete, these were next in line and are still unmeasured:

- **Different batch or render target.** `QuestLog` calls `SpriteBatch.End()`/`Begin()` with a
  scissor rectangle (3× each), so the game does re-begin batches mid-draw. Log `_beginCalled` and
  the batch's identity hash in the postfix and compare against the tooltip's batch.
- **Something legitimately draws last.** The mouse cursor is drawn late in `_draw`. The screenshot
  argues against it (the label is clipped by the tooltip *box*), but it is unconfirmed.

## Tooling

- **Console commands**, all in `ModEntry.cs`: `ls_draw_trace [frames]`,
  `ls_dump_text [filter]` (everything recorded this frame, dumped from *inside* the draw — a console
  command runs on the update tick and would read a half-filled frame), `ls_probe_questlog [on|off]`,
  `ls_build_index`, `ls_lookup`.
- **Driving the console without a terminal.** `scripts/run.sh` with its stdin on a fifo:
  `mkfifo f; sleep 86400 > f & scripts/run.sh < f > log`, then `echo "ls_draw_trace 600" > f`.
  Without this the SMAPI console is unreachable, because the game starts as a background process.
- **IL disassembly** for ground truth: `ikdasm "…/Contents/MacOS/Stardew Valley.dll" > sdv.il`
  (also `MonoGame.Framework.dll`). Prefer it over reasoning from memory — several wrong turns here
  came from plausible but incorrect assumptions about method behaviour.

## Gotchas

- A game restart is needed for every code change (~1 min).
- Keybinds are `Z` (freeze tooltip) and `G` (toggle translation). Not function keys — macOS needs Fn.
- Tools like the pickaxe live in `Strings/Tools` and have no segment data, so they fall back to the
  amber character-class heuristic. Amber is not a bug.
- `assets/segments/` is generated at build time from `tools/extracted-strings`; don't commit it.
