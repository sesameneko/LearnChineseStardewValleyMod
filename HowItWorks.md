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

## Flashcards

Left-clicking a word saves it as a flashcard, and the pause menu gets a tab for reviewing them. The rules below are in `FlashcardDeck.cs` and `FlashcardContext.cs`. Neither uses any game types, and both are unit-tested in `tools/ModLogic.Tests`.

### Saving a word

A card is made from whatever word hover last found under the mouse. Input is handled before each frame is drawn, so that's the word the player saw when they clicked. Only words with segment data can be saved, since the character-class split has no meaning or reading to put on a card. When a click saves a word, `ModEntry` suppresses it so the game underneath never sees it. A click that misses every word passes through as normal.

A card is identified by source language, word and kana: 上手 read じょうず and 上手 read うわて are separate cards. The word is trimmed of the punctuation segment data attaches to it (`ありがとう！` becomes `ありがとう`). A gloss that is one parenthesised note, like `(object marker)` or `(your name)`, marks a particle or placeholder, and those are refused.

Clicking a saved word again depends on the sentence. The same sentence removes that sentence from the card, and removing the last one deletes the card. A new sentence, or a new meaning, is added to the existing card.

### Pointing back at the sentence

A card stores where its sentence is rather than a copy of it: `table:key@offset+length`, which is the segment file, the entry key, and the word's segment's position in that entry's Japanese text. `SegmentDataLoader` stamps this on every segment as it loads, and it survives the lookups word hover does. A name or number the game filled in has no pointer, and neither does the HUD clock, so words found there are saved without a sentence.

The position is a character offset, not a segment number, because re-splitting an entry's segments renumbers them while the game's text stays put. A pointer is shown only if its span still contains the card's word. A data update can make one stale: it is then hidden and logged, but kept in the file in case a later fix makes it valid again.

### Storage

The deck is saved to SMAPI's global data (`.smapi/mod-data/<mod id>/flashcards.json`) after every change. That makes it shared by every save file, and keeps it outside the mod folder, which a mod update replaces. If the file can't be read, saving is turned off for the session so a damaged deck isn't overwritten with an empty one.

### The pause-menu tab

The game has no way to add a pause-menu tab, so `Patches/GameMenuPatches.cs` patches it in. Its approach rests on three facts from the 1.6.15 IL:

- `GameMenu`'s constructor builds `tabs` and `pages` as matching lists, so a postfix appends one of each.
- Tab switching turns a tab's name into a page number with a hardcoded lookup that returns -1 for any name it doesn't know. A postfix maps ours.
- `draw` picks each tab's icon by the same hardcoded names and draws nothing for ours. The icon is drawn from the mod's own overlay pass instead.

`FlashcardsPage.cs` is the tab itself. **Review** shows one card at a time. The back has the kana, romaji and meanings, plus the sentence page the word was on, with the word underlined and the literal and official English below it. Marking a card missed or known only adds to its counts: nothing is scheduled. **Browse** lists every card and can delete them. The tab draws inside `TextCapturePatches.SuppressRecording()`, so none of its own text can be hovered or saved.

## Macron vowels in the font

The word-hover bubble shows romaji in Hepburn, which writes long vowels with a macron (`gakkō`). The game's small font has no glyphs for ā ī ū ē ō, and MonoGame draws any missing character as the font's default, `*`. So the mod adds them to the font as it loads.

### Building the glyphs

`ExtendedFont.cs` handles SMAPI's `AssetRequested` for `Fonts/SmallFont`. That catches the English and Japanese loads at startup and the reload on every language change. For each load it:

1. Reads the font's texture back from the GPU. The texture is DXT3-compressed, so `FontGlyphSynth.DecodeDxt3` decodes it to pixels.
2. Copies out the plain vowels (a i u e o, A I U E O) and draws a bar over each. `FontGlyphSynth.cs` contains no game types and is unit-tested.
3. Adds the ten new glyphs in a strip below the original texture and builds a new `SpriteFont` from the result. Every existing glyph is unchanged.

The bars are measured from the font itself, so they match its style:

- **Thickness:** one value for all ten, the median top-stroke thickness of the lowercase vowels. Measuring each letter separately gave bars that differed by a pixel.
- **Height:** one per case, just above the tallest vowel of that case. Accents in a typeface sit at a fixed height, and in this font u and i are shorter than a, e and o.
- **i:** its dot is removed, and the bar spans where the dot was. A bar the width of the stem looked like a dot.
- **Width:** the bar spans the letter, 1px narrower on each side for letters 6px or wider.

### Using them

`FontSafeText` replaces characters the font can't draw: `ō` becomes `oo`, `—` becomes `--`. It is given the font's character set (`ExtendedFont.DrawableCharacters`) and keeps anything in it. The romaji therefore keeps its macrons whenever the font has them. If the font couldn't be extended, the mod logs a warning, keeps the original font, and long vowels are written doubled.

Glosses are converted once, as the segment data loads, before the font exists. They still get the ASCII-only treatment.

### Checking it

- `ls_font_check [text]` reports which characters of the text the font can't draw. With no text, it checks everything in the segment data.
- `ls_font_info [chars]` logs the texture's size and format, and each character's position and offsets.
- `ls_font_export <path.png>` saves the font's texture as an image, so the generated glyphs can be seen. They are in the strip at the bottom.

## Where the word data lives

The segment data (each entry's Japanese text split into words, with kana, romaji and a gloss for each) is in `assets/segments/ja/`, one JSON file per game table. The mod loads it from there, and the tools in `tools/segment-data/` edit it there. There is no second copy.

It used to be authored in `tools/extracted-strings/literal-translations/` and copied into a gitignored `assets/segments/ja/` on every build. The copy was needed because of how ModBuildConfig builds the deployed mod folder (from its 4.1.1 package):

- `manifest.json`, `i18n/` and `assets/` come from the **project folder**.
- Everything else comes from the **build output**. Any `manifest.json`, `i18n/` or `assets/` found there is skipped, so the project's copies win.

So data kept outside `assets/` can't reach the mod's `assets/` folder by being copied into the build output. Keeping the one copy in `assets/` removed the build step and the chance of editing a stale copy.

The same rule means **everything in `assets/` ships**, whatever its file type. Scratch files left there (TSV batches, backups) end up in the deployed mod and its release zip, so keep them elsewhere.
