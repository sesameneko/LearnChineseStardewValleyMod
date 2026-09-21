# Handoff — word-hover overlay draws *behind* the tooltip

**Status:** unsolved after three failed attempts. A fourth fix is committed but **never tested in-game**.

## The bug

The M3 word-hover debug overlay (a coloured outline around the hovered word, plus a text label
above it) is drawn *underneath* the tooltip it is annotating, so it is mostly invisible.

It is only the **z-order** that is wrong. Everything else about the feature works:

- the correct word is identified,
- the label content is correct,
- the label position is correct.

Proof of the last two: in the 02:49 screenshot the label reads `nansa) * flexibility`, which is the
right-hand half of `柔軟さ (jūnansa) — flexibility`. The left half is not missing — it is hidden
behind the tooltip box. So the overlay is being painted at the right coordinates, just too early or
too deep.

## Definition of done

With `ls_word_hover on`, hovering a word inside a tooltip (frozen with `Z` or not) shows the outline
and the full label **on top of** the tooltip box, not clipped by it or by the HUD.

## Where the code is

| File | Role |
|---|---|
| `WordHoverOverlay.cs` | Hit-tests and draws the outline + label. `TopLayerDepth()` is the current (untested) fix. |
| `Patches/HoverTextPatches.cs` | `Postfix_DrawHoverText` — where the overlay draw is triggered from. |
| `Patches/TextCapturePatches.cs` | Records every drawn string; not implicated, but it is how the overlay knows where text is. |
| `FrozenTooltip.cs` | Re-issues the pinned tooltip through vanilla `drawHoverText`. Its draw is called from `ModEntry.OnRenderedHud` / `OnRenderedActiveMenu`. |

## What is actually verified

These came from disassembling the installed 1.6.15 build (`ikdasm` on `Stardew Valley.dll` and
`MonoGame.Framework.dll`), not from memory:

- **Tooltips draw at layerDepth 0.9–0.95.** Both the box and its text.
- **The game uses several sprite sort modes** across its `SpriteBatch.Begin` calls: Deferred ×89,
  FrontToBack ×23, Immediate ×6, BackToFront ×2, Texture ×1. Which one is active *at the moment the
  overlay draws* has **never been measured** — see hypothesis 1.
- **`drawHoverText` is a single funnel.** `drawToolTip` → `drawHoverText(string)` →
  `drawHoverText(StringBuilder)`. The mod patches only the StringBuilder overload.
- **SMAPI's `Display.Rendered` runs after `Game1._draw` returns**, i.e. against the *world* render
  target, which the game composites underneath the UI one. Never draw UI from it. (Already
  documented in `CLAUDE.md`; this was failure #1.)

## What was tried, and why each failed

In order. Do not repeat these.

1. **Draw from `Display.Rendered`.** Overlay appeared under all UI *and* offset from the cursor,
   because that event runs against the world render target, which is scaled by `zoomLevel` while UI
   is scaled by `uiScale`. Fixed by moving to `RenderedHud` / `RenderedActiveMenu` — this part is
   genuinely solved and should not be reverted.
2. **`layerDepth = 0f`** (the implicit default). Behind the tooltip.
3. **`layerDepth = 1f`, still drawn at `RenderedHud`.** Still behind. Investigation showed a toolbar
   tooltip is drawn *after* `RenderedHud`, so the overlay was painted before the tooltip regardless
   of depth. Moved the overlay draw into `drawHoverText`'s postfix, which runs immediately after the
   box is drawn, into the same `SpriteBatch` that is passed to it as argument 0. **Still behind.**
4. **Read the sort mode** (`WordHoverOverlay.TopLayerDepth`, committed in `c4f8ee5`): reflect
   MonoGame's private `SpriteBatch._sortMode` and pick `0f` for BackToFront, `1f` otherwise.
   **Built and committed, never run.** Verify this before doing anything else.

The thing that makes attempt 3 confusing, and which the next person should sit with: the tooltip box
is drawn by `drawTextureBox` *inside* `drawHoverText`, so in a Deferred batch an overlay issued from
that method's postfix is strictly later in call order and must appear on top. It does not. So either
the batch is depth-sorted, or something draws the tooltip *again* afterwards.

## Ranked hypotheses, each with the experiment that settles it

**1. The batch is depth-sorted and attempt 4 is simply the fix.** Cheapest to check, and it is
already written. Log the value at draw time:

```csharp
ModEntry.Log($"sortMode={SortModeField?.GetValue(b)} depth={TopLayerDepth(b)}");
```

If it prints `Deferred`, depth is irrelevant and the cause is ordering → go to hypothesis 2.

**2. The tooltip is drawn twice per frame, and the second draw covers the overlay.**
`FrozenTooltip.Draw` is called from *both* `OnRenderedHud` and `OnRenderedActiveMenu`. The overlay is
guarded to one draw per frame by `WordHoverOverlay.DrawnThisFrame`. So if both events fire, the
sequence is: tooltip → overlay → **tooltip again**, with the second overlay draw suppressed by the
guard. That would produce exactly this symptom and would be invisible to any amount of layer-depth
work. Test: log each `FrozenTooltip.Draw` and each `WordHoverOverlay.Draw` with a frame counter and
check whether two tooltip draws bracket one overlay draw. This is the hypothesis I would start from
if hypothesis 1 prints Deferred.

**3. The overlay and the tooltip are in different batches or render targets.** The user suggested
this early and it was never properly excluded. `QuestLog` is known to call `SpriteBatch.End()` /
`Begin()` with a scissor rectangle (3× each), so the game does re-begin batches mid-draw. If
`drawHoverText` (or something between it and our postfix) ends the batch, our draw lands somewhere
else entirely. Test: log `_beginCalled` and the batch's identity hash in the postfix, and compare
with the batch the tooltip actually drew into.

**4. Something legitimately draws after everything.** The mouse cursor is drawn late in `_draw`; if
other late UI is drawn after our postfix, the overlay is correctly on top of the tooltip but under
that. The screenshot argues against this (the label is clipped by the *tooltip box*, not by the
cursor), but it would be worth confirming if 1–3 all fail.

## Tooling you already have

- **Console commands**, all in `ModEntry.cs`: `ls_word_hover [on|off]` (the overlay),
  `ls_dump_text [filter]` (everything recorded this frame, with positions — dumped from *inside* the
  draw, which matters: a console command runs on the update tick and will read a half-filled frame),
  `ls_probe_questlog [on|off]`, `ls_build_index`, `ls_lookup`.
- **Driving the console without a terminal.** `scripts/run.sh` launched with its stdin on a fifo lets
  you send commands to the running game: `mkfifo f; sleep 86400 > f & scripts/run.sh < f > log`, then
  `echo "ls_word_hover on" > f`. Without this the SMAPI console is unreachable, because the game is
  started as a background process.
- **IL disassembly** for ground truth about the installed build:
  `ikdasm "…/Contents/MacOS/Stardew Valley.dll" > sdv.il` (also `MonoGame.Framework.dll`). This is
  how every "verified" item above was established. Prefer it over reasoning from memory — several
  wrong turns here came from plausible but incorrect assumptions about method behaviour.

## Gotchas

- A game restart is needed for every code change (~1 min). Keep that in mind when choosing between
  "add a log line" and "think harder".
- Keybinds are `Z` (freeze tooltip) and `G` (toggle translation). Not function keys — macOS needs Fn.
- Hover an **Objects** item (wood, stone, a sapling) to get green outlines with glosses. Tools like
  the pickaxe live in `Strings/Tools` and have no segment data, so they fall back to the amber
  character-class heuristic. Amber is not a bug.
- `assets/segments/` is generated at build time from `tools/extracted-strings`; don't commit it.
