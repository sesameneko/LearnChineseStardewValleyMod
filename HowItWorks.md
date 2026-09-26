# How it works

Notes on how the mod's main pieces work, one section per piece. For the roadmap and the reasons behind the design, see `Plan.md`. For a file-by-file map of the code, see `CLAUDE.md`.

## Word-position detection

Word hover has to know which word is under the mouse. The game keeps no record of where its text ends up: every frame it draws each string one character at a time and then forgets where they went. So the mod watches the text being drawn and records where each character lands.

### Recording positions while the game draws

The game has two text renderers, and both are instrumented:

| Renderer | Used by | Method patched |
|---|---|---|
| **SpriteText**, a bitmap font | dialogue boxes, the quest log, shops | `SpriteText.drawString` |
| **SpriteFont**, MonoGame's font | tooltips and most menu text | the four `SpriteBatch.DrawString` overloads the game calls |

Each of those methods has a loop that draws a string one character at a time and moves a "pen" position along as it goes. `Patches/GlyphCapturePatches.cs` uses Harmony transpilers to insert two calls into each loop:

1. **Where a character is drawn:** the pen position at that moment is the character's left edge.
2. **At the loop's `i++`:** every path through the loop ends here, and the pen now stands past the character. That is its right edge.

Together these give one `GlyphCell` per character that was actually drawn: the character's index in the string, plus a box one line tall. Neighbouring cells on a line touch, so the mouse is always over some character and never in a gap between two.

`Patches/TextCapturePatches.cs` already recorded each drawn string at the start of these methods (with prefixes). The cells are attached to that record, so each recorded string carries its own list of cells for the frame.

Characters that are never drawn never get a cell. That covers line breaks, characters the font has no glyph for, and dialogue the typewriter effect hasn't revealed yet, so none of them can be hovered.

### Finding the word under the mouse

`GlyphHitTest.cs` contains no game types and is unit-tested in `tools/ModLogic.Tests`. `WordHoverOverlay` uses it as follows:

1. **Find the cell under the mouse.** Strings are checked in reverse draw order, so text drawn on top wins.
2. **Map that character to a word.** The segment data (hand-authored word boundaries) describes the text with line breaks removed, so the character's index is adjusted to skip them. When there's no segment data for the text, a character-class split (kanji runs, kana runs, and so on) is used instead.
3. **Draw the outline.** It is drawn around that word's cells on the hovered line only. A word the renderer split across two lines gets outlined only on the line under the mouse.

Nothing in this path measures text or works out where lines break. The positions are exactly what the renderer did.

### Why not calculate the layout instead?

The first version recorded only each string's text and its starting position, then worked out the rest itself: it re-wrapped the text, measured prefixes with the font, and assumed a line height. That worked for tooltips, which arrive with their line breaks already inserted. It drifted on dialogue, because `SpriteText` wraps the text internally, and its rules (from the 1.6.15 IL) are more involved than they look:

- It deletes `\n` from the string and starts a new line on `^` instead.
- It breaks a line when the next word would reach `x + width - 4`, not `x + width`.
- In Japanese, Chinese and Thai, it wraps in units matched by `Game1.asianSpacingRegex`. That keeps small kana, `ー` and closing punctuation attached to the character before them.
- It skips characters missing from the font without moving the pen.
- It only draws up to the typewriter position.

Each of these rules could be copied into the mod, but any gap between the copy and the real renderer shows up as a word outlined in the wrong place. Reading the positions back removes the copy entirely.

### When a game update breaks it

Transpilers depend on the exact shape of the patched method's IL. Each one looks for recognisable instruction patterns rather than fixed offsets:

- the loop condition, e.g. `i < text.Length`
- the `i++` just before it
- the pen variable, the `Vector2` whose X is reset at each line break
- for scaled `DrawString`, the matrix that converts positions to screen coordinates

If a pattern is missing, the transpiler leaves the method unpatched and logs a warning. Text from that renderer then falls back to the old calculated layout (`TextHitTest`, `TextHitTest.WrapToWidth`). Word hover gets less accurate for that renderer but keeps working.

To check the state after a game update:

- The SMAPI log's `Glyph capture: …` startup line says `on` or `OFF (measured fallback)` for each renderer.
- `ls_dump_text` shows `glyphs=N` for each recorded string. `-` means that string's renderer is on the fallback. `0` on text that is visible on screen means the capture isn't recording.
