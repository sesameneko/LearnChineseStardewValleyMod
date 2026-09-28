# How it works

Notes on how the mod's main pieces work, one section per piece, and the reasons behind the design. For a short map of the code and the rules for changing it, see `CLAUDE.md`. For bugs that were costly to find, and how they were found, see `PostMortems.md`.

## Language activation and sibling copies

The mod ships as one copy per study language: this codebase, with that language's data and its own manifest. A player may install several, so each copy runs only while the game language is its own. At most one copy is live at a time, and the copies never communicate.

### Activation

Each copy's study language is the manifest's `StudyLanguage` field, a game language code (`ja`, `zh`, …). `ModEntry.OnLanguageTick` polls `LocalizedContentManager.CurrentLanguageCode` every tick, and `ActivationTracker` (`LanguageActivation.cs`, no game types, unit-tested) turns that into activate and deactivate steps:

- **Active:** the Harmony patches are applied, the handlers run, and the index is built once a save is loaded. The segment data and flashcard deck are read on the first activation only.
- **Inactive:** every patch is removed with `UnpatchAll(<UniqueID>)`, and the handlers and console commands return straight away. The font isn't extended either.

It polls instead of using `Content.LocaleChanged` because `TranslationIndex.Build` flips the game to English and back inside one call. An event would fire during that flip; a poll never sees it.

Deactivation happens on the first tick the language stops matching, activation only on the second matching tick. When the player moves from one copy's language to another's, both copies see the change on the same tick, so the delay means the old copy has unpatched before the new one patches. Otherwise the new copy's glyph transpilers could see the old copy's inserted IL, fail to match, and warn.

`ExtendedFont` checks the language itself, when the font is requested, instead of reading `IsActive`. The font reloads inside the language change, a tick or two before the copy activates.

### The title-screen prompt

Once per launch, when the title screen has settled (`titleInPosition`, no submenu open), `LanguagePrompt` finds every installed copy through `ModRegistry` by its `StudyLanguage` field and asks `LanguageActivation.DecidePrompt`:

- If the game is already in any copy's language, nothing is shown, and that copy activates by itself.
- Only the copy with the lowest `UniqueID` asks, so one popup appears however many copies are installed.
- With one copy, a `ConfirmationDialog` offers to switch. With several, `LanguageChoiceMenu` lists each language plus Cancel. The labels are English names, since the game's current font may lack the native script.

A choice only sets `CurrentLanguageCode`, as the game's own language menu does (`LanguageSelectionMenu.ApplyLanguage`). `TitleMenu.OnLanguageChange` then saves it to the startup preferences, and the copy for that language activates on its own. No or Cancel closes the popup and nothing is remembered, so the next launch asks again.

### Making a copy for another language

SMAPI won't load two mods with the same assembly name ("…already loaded. Do you have two copies of this mod?"), and each copy's global data is keyed by its `UniqueID`. Each copy sets its own:

- `UniqueID`: `com.oldclovercat.<lang>languagestudy` (this one is `jp`)
- `Name`
- `<AssemblyName>` in the `.csproj`, with `EntryDll` in the manifest to match
- `StudyLanguage`
- `assets/segments/<lang>/`

The C# namespace stays the same. Types in different assemblies never collide, and the Harmony ID is the `UniqueID`. A console command name another copy already took is registered with a language suffix (`ls_lookup_zh`).

## Hover translation

Hovering something that shows a vanilla tooltip adds a second box with the tooltip's text in the target language. The text comes from the game's own string tables, loaded in both locales and joined on their keys.

### Capturing the tooltip

`Patches/HoverTextPatches.cs` patches only the **StringBuilder** overload of `IClickableMenu.drawHoverText`. `drawToolTip` and the `string` overload both call it (checked against the 1.6.15 IL), so every tooltip passes through it exactly once.

The mod doesn't recalculate where the tooltip went. `drawHoverText` draws its own background with `drawTextureBox` before anything else, so the first `drawTextureBox` call made while inside `drawHoverText` is the tooltip's box, and the patch records that rect. `TooltipLayout.cs` places the translation box relative to it: above if there's room, below if not, and clamped into the viewport either way. `TooltipOverlay.cs` draws the box and clears it in `Display.Rendering` so nothing is left over from a previous frame.

### Building the lookup

`TranslationIndex.cs` loads every `Strings/*` table, plus the per-NPC and per-festival families, in both locales. A non-English locale is loaded by its suffixed name (`Strings/Objects.ja-JP`). English has no suffixed file on disk, so the whole batch loads under one temporary change of `LocalizedContentManager.CurrentLanguageCode`. Because of that change, the index is built on `SaveLoaded` and never during a draw. Both halves were confirmed live with `ls_spike_locale`, with the game set to Japanese: the suffixed load works through `Helper.GameContent.Load` with no need to go around SMAPI, and the language-code change had no visible side effects.

`TranslationMap.cs` holds the joined table (no game types; unit-tested). The text on screen often isn't the stored string, and the map handles each way they can differ:

- **Variants and wrapping.** It splits on the game's `^` gender-variant delimiter. The game word-wraps tooltips by inserting newlines, in Japanese in the middle of a sentence, so the map keeps a whitespace-stripped index as well as a whitespace-collapsed one.
- **Token templates.** Templates like `日記 （{0}）` → `Journal ({0})` are filled in with `string.Format` when drawn. When counted, 761 of the 8,069 shared entries (about 9%) were templates whose two locales use the same set of tokens. Only those are registered. They're matched by regex when an exact lookup fails, and filled **by token index**, because locales order tokens differently (`攻撃 +{0}` vs `+{0} Attack`). 699 of the 761 register. The rest are refused, since a template that is all token, or has two tokens with no text between them, would match any text or split it arbitrarily. A captured value that is itself a known source string is translated too, so an item name filled into a sentence doesn't stay in Japanese. The most literal templates are tried first, each is screened with an ordinal `Contains` on its longest literal run, and results (misses included) are memoised. A hovered tooltip looks itself up every frame, and scanning all 699 templates without these guards cost about 2.7ms per frame. With them, an uncached miss takes about 0.065ms.
- **Lookup order.** Exact whole match first, then paragraph by paragraph, and only then templates on the whole text. A template's `{N}` capture will otherwise swallow a blank line and every paragraph after it. The secret-note header `ひみつのメモ #{0}` once matched an entire note that way.

### Achievements and secret notes

`Data/Achievements` and `Data/SecretNotes` are `Dictionary<int,string>` assets whose raw values never appear on screen as-is. `DataTextShapes.cs` (no game types) reshapes them into the text their Collections-page tooltips draw, following `CollectionsPage.createDescription` in the IL:

- An achievement's `name^description^…` record becomes the two paragraphs the tooltip shows.
- A note is cleaned the way the game cleans it (gift-reveal tags removed, `^` becomes a newline, `@` becomes a `{0}` template for the player's name) and split into one entry per paragraph. There's also a `ひみつのメモ #{0}` header template.

`TranslationIndex` loads these through a separate int-keyed path. Two `TranslationMap` behaviours exist for them. A note the game cut at 15 lines and ended with `(...)` is matched by prefix. A paragraph that is identical in both locales (a note signed `-Qi`) is carried through the paragraph pass unchanged.

### The HUD clock

The Japanese clock isn't in any string table. `DayTimeMoneyBox.draw` builds it in code, with a hardcoded ja branch: `{day}日 ({weekday})` and `{午前|午後} {h}:{mm}`, where noon and midnight are written as 0. `ClockSegments.cs` lists every date and time the clock can draw as an exact `SegmentIndex` entry rather than as a template, because the readings are irregular per value (1日 is ついたち, 4時 is よじ). `SegmentDataLoader` adds them when the source language is `ja`.

## Frozen tooltips

A frozen tooltip stays where it is with its content fixed, so the cursor can move across it and hover the words inside. `Z` locks and unlocks one, and holding `Right Shift` pins one until the key is released (`ModConfig.FreezeTooltip` / `HoldFreezeTooltip`). `FrozenTooltip.cs` holds the state, and `TooltipReissue.cs` draws it. Two facts about the game, from the 1.6.15 IL, make this possible:

- **Hover is polled, and nothing marks it handled.** `Game1.updateActiveMenu` calls the menu's `performHoverAction` every frame, and nested menus forward it by hand. Neither it nor `receiveLeftClick` returns anything. A handler writes its result to fields (`hoverText`, `hoverItem`) that `draw()` reads later in the frame. A Harmony prefix returning `false` is therefore the way to block hover, and one on the StringBuilder `drawHoverText` suppresses every vanilla tooltip.
- **`drawHoverText` can be re-issued at a fixed position.** Its `overrideX`/`overrideY` parameters (default `-1`, meaning "relative to the cursor") draw a pixel-identical vanilla tooltip, money line, buff icons and all, wherever they point. So the frozen tooltip is vanilla's own, re-drawn with the argument list `HoverTextPatches` recorded, rather than a hand-built copy.

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

### Matching drawn text to segment data

`SegmentIndex.TryGetSegments` finds the segment data for a drawn string. Much of what the game draws isn't stored verbatim, so the lookup tries these in order:

1. **Exact**, then **ignoring whitespace**.
2. **Token template.** 833 authored entries hold `{N}` tokens (the load-screen date `{2}年目、{0}日、{1}`, `手持ちのお金：{0}G`, `{0} 牧場`). They're matched with `TranslationMap`'s regex builder, and the captured values are substituted into the segments. A segment that is only a token, whose value is itself an entry (a season, an item name), takes that entry's segments and gloss.
3. **Prefix of a longer entry.** Mail's stored text ends in `%item … %%[#]title` commands that the game removes before drawing. Drawn text of 6+ characters that starts a longer entry uses that entry's segments, clipped. Shorter text too often starts something unrelated: a drawn `500` is the start of `50000Gをかせぐ`.
4. **Composite.** Quest descriptions (several `ItemDeliveryQuest` strings, a filled-in item and NPC name, two reward templates), clothing description + `可染性。`, and dialogue pages after the first are built from several stored pieces. The drawn text is tiled with known pieces laid end to end: whole entries of 2+ characters, templates placed mid-text, and runs of 6+ characters from inside a longer entry. The tiling covering the most characters wins, then the one with the fewest pieces. A tiling under 70% coverage is rejected as coincidence, and uncovered characters keep the character-class split. A template's captured value only counts as covered if it is itself an entry. Otherwise `{0} 牧場` placed at the start of a quest captured everything up to 牧場主 and labelled half the sentence "(farm name)".

Whatever is found is re-laid over the drawn text, so the segments reproduce the drawn lines exactly, including whitespace the game added (`手持ちのお金： 29,560G` has a space the template doesn't). Non-exact results are memoised per drawn string. The composite's first lookup of a new string costs about 20–40ms (see `TODOs.md`).

The HUD clock has no template anywhere, so it's handled by `ClockSegments` (see "The HUD clock" above).

Without segment data, words come from a character-class split (`TextHitTest.SplitSegments`), which groups runs of the same script. It merges an unbroken kanji run (`長時間快適` is really 長時間 + 快適) and a long hiragana run (`たちはきっといるはず` is really たち + は + きっと + いる + はず). This is why every string has hand-authored data rather than a runtime tokenizer: Japanese word boundaries can't be derived by rule.

### Text word hover ignores

`TextHitTest.IsHoverable` decides by content. A word from the character-class split that has no letters in it (kana and kanji count as letters) gets no label, because its label would only repeat it. That covers the hotbar's 1-9, 0, - and =. A word from segment data is always hoverable, numbers included, because it was authored with a reading (5000 ごせん), and the reading is the point.

The word-hover label (gloss, romaji, kana on three lines) is built by `WordHoverOverlay.Describe`. Its romaji is generated from each segment's `kana` at runtime by `KanaRomaji.cs`, a C# port of `tools/segment-data/kana_to_romaji.py`, not taken from the data's `reading`.

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

A card is made from whatever word hover last found under the mouse. Input is handled before each frame is drawn, so that's the word the player saw when they clicked. Only words with segment data can be saved, since the character-class split has no meaning or reading to put on a card. When a click saves a word, `ModEntry` suppresses it so the game underneath never sees it. This is a deliberate trade: a click on a word can't also advance dialogue, pick a question response or buy a shop row. A click that misses every word passes through as normal, and the `Click to Save Words` GMCM option turns the gesture off.

A card is identified by source language, word and kana: 上手 read じょうず and 上手 read うわて are separate cards. The word is trimmed of the punctuation segment data attaches to it (`ありがとう！` becomes `ありがとう`). A gloss that is one parenthesised note, like `(object marker)` or `(your name)`, marks a particle or placeholder, and those are refused.

Clicking a saved word again depends on the sentence. The same sentence removes that sentence from the card, and removing the last one deletes the card. A new sentence, or a new meaning, is added to the existing card. A click with no sentence on a card that already has some changes nothing. Deletes are permanent.

Each save or removal shows a HUD message and plays a sound, and the hover label puts a ★ before the gloss of a word already saved.

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

`FlashcardsPage.cs` is the tab itself. **Review** shows one card at a time. The back has the kana, romaji and meanings, plus the sentence page the word was on, with the word underlined and the literal and official English below it. Marking a card missed or known only adds to its counts: nothing is scheduled. The order (newest, oldest, or fewest correct) is picked at the top of the tab and kept in config. `Space` flips, `1` marks missed and `2` marks known. **Browse** lists every card and can delete them, with a second click to confirm. Only cards for the configured source language are shown. The tab can't be navigated with a controller or reached with the shoulder buttons. The tab draws inside `TextCapturePatches.SuppressRecording()`, so none of its own text can be hovered or saved.

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

## Word data

The "segment" data (containing each entry's Japanese text split into words, with kana, romaji and a gloss for each) is in `assets/segments/ja/`, one JSON file per game table. The mod loads it from there, and the tools in `tools/segment-data/` edit it there.

Note, **everything in `assets/` ships**, whatever its file type. Scratch files left there (TSV batches, backups) end up in the deployed mod and its release zip, so keep them elsewhere.
