# TODOs

## Open

- [ ] [Chinese migration](#chinese-migration)
  - [x] Phase 0: groundwork (2026-09-28)
  - [ ] [Phase 1: author the zh segment data](#phase-1-author-the-zh-segment-data)
  - [ ] [Phase 2: runtime shows pinyin](#phase-2-runtime-shows-pinyin)
    - [ ] [Add the missing pinyin vowels to the zh font](#phase-2-runtime-shows-pinyin): ā ē ī ō ū, ǎ ě ǐ ǒ ǔ, ǖ ǘ ǚ ǜ
  - [ ] [Phase 3: tests, names and docs](#phase-3-tests-names-and-docs)
  - [ ] [Phase 4: verify in game](#phase-4-verify-in-game)
- [ ] In-game checks (inherited from the ja mod; redo in zh as part of phase 4)
  - [ ] [Verify dialogue sentence translation](#verify-dialogue-sentence-translation)
  - [ ] [Verify achievements and notes](#verify-achievements-and-notes)
  - [ ] [Verify flashcards](#verify-flashcards)
  - [ ] [Verify language activation](#verify-language-activation)
- [ ] Segment data
  - [ ] [Consolidate redundant glosses](#consolidate-redundant-glosses)
- [ ] Code
  - [ ] [Optimize composite lookup](#optimize-composite-lookup)
    - [ ] N-gram index for substrings
    - [ ] Trie for whole entries
    - [ ] Narrow template attempts
  - [ ] [Quiet fallback log](#quiet-fallback-log)
  - [ ] [Remove debug logs](#remove-debug-logs)
  - [ ] [Guard gendered-string splitting](#guard-gendered-string-splitting)
- [ ] Features
  - [ ] [Explanatory translation mode](#explanatory-translation-mode)

## Details

### Chinese migration

This repo is a copy of the Japanese mod, being turned into a **Simplified Chinese (zh-CN) → English** mod. The mechanisms (tooltip capture, glyph capture, segment lookup, frozen tooltips, flashcards) are language-independent. What changes is the data, the reading shown under each word (pinyin instead of kana and romaji), the font glyphs that reading needs, and a few places that hard-code Japanese.

#### Phase 0: groundwork (done 2026-09-28)

- **Identity.** `manifest.json` is `com.oldclovercat.zhlanguagestudy` (first `com.galacticrailroad.languagestudy.chinese`, renamed 2026-09-28 before any zh flashcards existed), "Language Study (Chinese)", and the `.csproj` builds `LanguageStudyChinese.dll` into `Mods/LanguageStudyChinese`. Before this, a build would have overwritten the Japanese mod's deployed folder and shared its flashcard store and Harmony ID.
- **Source text.** All 178 tables the ja pipeline covered are extracted in zh-CN to `tools/extracted-strings/{zh,data-zh,content-zh}` (see that README to regenerate). `segtool.py audit` accounts for every zh-CN asset. The only ones that aren't ja's are fonts and map textures.
- **Pipeline.** `segtool.py` authors `assets/segments/zh/` with the schema `{chinese, english, segments[{text, pinyin, gloss}]}`. The new `pinyin.py` normalises pinyin and checks it (one syllable per hanzi, each a dictionary reading of its character, via pypinyin), and warns where a multi-hanzi reading differs from pypinyin's phrase dictionary. Conventions are in `tools/segment-data/README.md`.
- **Runtime, minimal.** `ModConfig.SourceLanguage` defaults to `zh` (since replaced by the manifest's `StudyLanguage`), and `SegmentDataLoader` reads the new schema (pinyin goes into `TextSegment.Reading`, unmodified). Nothing displays pinyin yet.
- **Removed:** `assets/segments/ja/` (22 MB that would have shipped; the 9 files unit tests read are kept in `tools/ModLogic.Tests/fixtures/segments-ja/`), `kana_to_romaji.py`, `romaji_to_kana.py`, `kana-review.tsv`, `migrate_names.py.retired`, and the ja kana TODOs. All of it is in git history. The explanatory-translation stub is now `zh.json`.
- **Found:** the zh `SmallFont` (6,988 glyphs) has the 2nd- and 4th-tone vowels (á à é è í ì ó ò ú ù) and ü, but **not** the 1st tone (ā ē ī ō ū), the 3rd tone (ǎ ě ǐ ǒ ǔ), ǖ ǘ ǚ ǜ, or ★. The SpriteText font `Fonts/Chinese.fnt` has none of them.

#### Phase 1: author the zh segment data

**15,706 entries** across 178 tables (`segtool.py status`), 10 skipped event-script keys carried over from ja. The workflow is in `tools/segment-data/README.md`.

**Pilot done 2026-09-28:** 81 entries from every kind of table (Objects, UI, StringsFromCSFiles with the weekdays, Data_Quests, Data_mail, Data_Achievements, Dialogue-Abigail, Events-Mountain, Festivals-spring13, TV-TipChannel, Schedules-Emily). 15,625 remain. The pilot made `merge` treat command markup (`$q`/`$r`/`$p`/`$c`/`$d`/`$query …#`, `%item … %%`, `${ }$`) as markup rather than words, place straight `"` and `*` by parity, and lowered `LONG_SEGMENT` to 3. It also wrote down conventions for item names, potential complements, and known-wrong pypinyin notes. Next is the bulk run:

- Author in batches of about 80 entries, each in a fresh context. The Japanese data drifted into clause-sized segments over long sessions.
- Suggested order, most visible first: item names and descriptions (`Objects`, `BigCraftables`, `Tools`, `Weapons`, `Furniture`, `Shirts`, `Pants`), `UI`, `StringsFromCSFiles` (includes the weekdays 星期一…星期天 the HUD clock draws), `1_6_Strings`, then the `Data_*` tables, then dialogue, events, festivals, TV and schedules.
- Read `merge`'s pinyin `note:` lines. They're the likeliest wrong readings.
- `LONG_SEGMENT` is 3 hanzi, tuned on the pilot (see "Segment size" in that README).
- `StringsFromMaps` signs: author arrow markup (`` ` ``, `>`) as separate gloss-less segments, so hovering a sign doesn't outline the arrow. The ja data got this wrong.
- Finish with `segtool.py validate` and `segtool.py audit`, both clean.

#### Phase 2: runtime shows pinyin

**Done 2026-09-28:** the hover label and flashcards show pinyin. `TextSegment.Kana` is gone and `Reading` holds the stored pinyin; the pure `Pinyin` class joins it per word (`mùchǎng`, `xī'ān`, `yìdiǎnr`) and falls back to tone numbers (`mu4chang3`) for a word the font can't draw every mark of. `Flashcard.Pinyin` replaces `Kana` in card identity. `ClockSegments` builds the zh date (`1日 星期一`, checked in the 1.6.15 IL), and `KanaRomaji` is deleted. Until the marked ü below exist, a word with one shows tone numbers (the macron and caron synthesis should cover the 1st and 3rd tones; unverified in game on the zh font). Still to do:

- **Tone glyphs** (`FontGlyphSynth` / `ExtendedFont`). The zh `SmallFont` is missing 14 of the vowels pinyin needs (probed from `Fonts/SmallFont.zh-CN.xnb`, 1.6.15):

  | Tone | Missing | How to make them |
  |---|---|---|
  | 1st (macron) | ā ē ī ō ū | the existing macron synthesis, unchanged |
  | 3rd (caron) | ǎ ě ǐ ǒ ǔ | **done, not yet seen in game:** `FontGlyphSynth.FlipAccent` turns the font's own â ê î ô û accent upside down. Offline render of the real atlas looks right; the font's circumflex is round-topped, so the caron is round-bottomed and reads slightly like a breve at high zoom |
  | ü, all four tones | ǖ ǘ ǚ ǜ | new: macron, acute, caron and grave over the font's own ü, placed above its dots |

  The font already has the 2nd and 4th tones (á é í ó ú, à è ì ò ù) and ü, and `ǘ`/`ǜ` can copy the acute and grave strokes from á and à rather than drawing new ones. Capitals (Ā Ǎ …) are only needed if the display ever capitalises names, which it doesn't plan to. `FontGlyphSynth`'s tests cover the new marks the way they cover macrons. Afterwards, `ls_font_check āǎēěīǐōǒūǔǖǘǚǜ` should report nothing missing.

  **Tabled (2026-09-28): hand-drawn tone marks.** Rather than synthesising marks, hand-draw only the marks (ˉ ˊ ˇ ˋ, plus a set sized for over ü's dots) as small PNGs, and have `ExtendedFont` stamp each onto the font's own vowel in the atlas, centred with `InkSpan` and placed with `InkTop` as the macrons are. It gives hand-tuned marks without shipping the game's letter pixels, and still adapts if the font changes. The cost is no per-letter tuning. Full hand-drawn letters were also considered: fine for the one font that matters, but they'd need a hash of each base vowel to detect a changed font and fall back. Revisit if the synthesised marks look wrong in game.
- **Saved mark:** the zh font has no ★, so the label falls back to `[saved]`. Synthesise one or pick a glyph the zh font has.
- **Chinese_round font.** The game ships `Fonts/Chinese_round/SmallFont` and `SpriteFont1` (zh only, not in ja). If a player setting switches to it, `ExtendedFont`'s `Fonts/SmallFont` match won't catch it. Find out in the IL when it's used. It may need nothing: probed offline, it already has every pinyin vowel (ā á ǎ à … ǖ ǘ ǚ ǜ), unlike `Fonts/SmallFont.zh-CN`.
- **Fallback split** (`TextHitTest.SplitSegments`): it groups a hanzi run into one blob, which is wrong for Chinese. For zh, fall back to one hanzi per segment. Drop the kana classes from the zh path.
- **Lookup thresholds** (`SegmentIndex`): the prefix match (6+ characters), composite runs (6+) and minimum whole entries (2+) were tuned on Japanese. Chinese says the same in fewer characters, so re-check them against real zh strings once phase 1 data exists (unit tests in `SegmentIndexTests`). Found in the pilot: in the Sebastian delivery quest (`ItemDeliveryQuest.cs.13324` + `13612`), `－塞巴斯蒂安会很开心` is tiled from a 6-character run of the first entry's `…起来。－塞巴斯蒂安` rather than from its own template `\n－{0}会很开心`. So `－` gets the gloss "(result: up)", and `会很开心` gets none. With any other NPC's name, the template wins. A composite run shouldn't beat a template that covers the same text and more.
- **Quiet fallback log:** skip text with no hanzi rather than no Japanese (see [Quiet fallback log](#quiet-fallback-log)).

#### Phase 3: tests, names and docs

- Move `DataTextShapesTests` and `RealStringTableTests` to the zh extracted data, and the real-data cases in `SegmentIndexTests` and `FlashcardTests` from `tools/ModLogic.Tests/fixtures/segments-ja/` to `assets/segments/zh/`. Move the `FlashcardTests` samples to Chinese, and port `ClockSegmentsTests`. Then delete `tools/extracted-strings/{ja,data-ja,content-ja}` and the fixtures.
- Rename Japanese-specific identifiers: `SourceEntry.Japanese`, `ContextBlock.Japanese`, `ClockSegments`' `(Japanese, …)` tuples and the ja examples in comments (`SegmentIndex`, `TextHitTest`, `FlashcardDeck`, `FlashcardContext`, `TranslationMap`).
- Rewrite `HowItWorks.md` (hover label, readings, macron font, HUD clock, fallback split) and `README.md` for Chinese.
- Optionally rename the `.sln`/`.csproj`, which changes the build command in `CLAUDE.md`. (The repo is done: `origin` is `sesameneko/LearnChineseStardewValleyMod` since 2026-09-28, and the Japanese repo is `upstream-ja`.)

#### Phase 4: verify in game

With the game set to 中文 and the Japanese mod disabled (prefix its `Mods` folder with `.`):

- `ls_spike_locale` with the source set to `zh`: suffixed `zh-CN` loads and the language-code flip both work.
- `ls_build_index zh en`, then `ls_lookup` on an item name.
- The `Glyph capture: ...` startup line is `on` for every renderer, and `ls_dump_text` shows glyph counts on Chinese dialogue. SpriteText uses `Fonts/Chinese.fnt` here, and the transpilers were only tested with the Japanese font's line height.
- Tooltip translation, word hover on a tooltip, dialogue, mail, a quest and the HUD clock, with the pinyin tone marks drawing correctly.
- The inherited checks below: dialogue pages, achievements and notes, and flashcards.

### Verify dialogue sentence translation

Entries are keyed by the whole raw string, markup and all (`$h`, `#$b#`…), and the game draws one page at a time with the markup removed.

- Word hover handles this now: `SegmentIndex`'s composite lookup, with page 2 of Lewis's `Introduction` verified live 2026-09-25.
- `TranslationMap` has no equivalent, so a non-first page likely gets no sentence translation.
- Check live. If it misses, split entries at `#$b#` / `#$e#` on load and strip the markup. Page breaks always fall on a segment boundary (`merge` enforces it), so that split is clean.
- Doing the same split for `SegmentIndex` would make pages exact lookups (see [Optimize composite lookup](#optimize-composite-lookup)).

### Verify achievements and notes

Sentence translation for achievements and secret notes is wired up and unit-tested against the real data (`ModLogic.Tests`), but not tried live.

- Hover them in the Collections tab, including a long journal scrap for the `(...)` path.
- Or run `ls_lookup 新人牧場主`, which should give `Greenhorn (15k)`.

### Verify flashcards

Click-to-save and the pause-menu tab are built but haven't been tried live. Check:

- the tab icon's placement (left of the Inventory tab, and whether it clears the menu frame's corner) and look (a Lost Book on a menu tile, since vanilla's tab art has no blank frame)
- that suppressing the click really stops dialogue from advancing and shop rows from being bought
- the card back's layout at different UI scales
- that the ★ before a saved word's gloss renders in the hover label

### Verify language activation

Activation by game language and the title-screen prompt are built and the decision logic is unit-tested, but none of it has been tried live (see `HowItWorks.md`). Check:

- Game in 中文: `Active` in the log with no prompt, and hover, the flashcards tab and `G`/`Z` all work.
- Game in English, one copy: the popup appears once the title settles. Yes switches to Chinese, the choice survives a restart, and the mod activates. No leaves it inactive (no hover, no tab, commands report inactive). Switching to Chinese by hand then activates it, and switching back deactivates it.
- Two copies: install the Japanese mod (with this activation change) alongside this one, both enabled. Both load, one chooser appears, each choice activates only its own copy, Cancel leaves both inactive, and whichever copy loads second gets suffixed commands (`_zh` or `_ja`).
- The `Glyph capture: …` line shows every renderer on after activating, including after switching back and forth.
- The chooser with a controller: snapping between buttons, and B/Escape as Cancel.

### Consolidate redundant glosses

Review redundancy in the segment data, and consider pointing repeats at a shared glossary instead of storing a copy in every sentence. The figures below were measured on the **Japanese** data, and Chinese particles (的, 了, 是) will repeat the same way. Re-measure once phase 1 is done. Two kinds:

- **Exact repeats.** Measured 2026-09-25: 126,307 glossed segments hold only 54,761 distinct (text, kana, gloss) triples, so ~71,500 (57%) are copies. The top ones:

  | Segment | Gloss | Copies |
  |---|---|---|
  | を | (object marker) | 3,418 |
  | は | (topic marker) | 2,801 |
  | が | (subject marker) | 2,789 |
  | の | (possessive) | 1,113 |

- **Compounds.** A sentence glosses `a b c d` as one segment, while elsewhere `ab` and `cd` each have their own gloss. That's only truly redundant when gloss(abcd) == gloss(ab) + gloss(cd). Otherwise the grouping carries meaning (an idiom, a set phrase) and must stay. Unmeasured.

**Caveat:** glosses are deliberately *in context*, so a shared entry must be keyed by sense, never by text alone. を alone has 14 distinct glosses, e.g. "(object marker)", "(along)", "(from, off)", "(path marker)", and each is correct somewhere. Some of those are the same sense spelled differently ("(object)" vs "(object marker)"). Merging those first would both shrink the data and make hover labels consistent.

**Possible shape:** a glossary file of senses `{id, text, pinyin, gloss}`, with segments holding a sense id where they match one and inline fields where they don't. The loader expands ids on load, so `SegmentIndex` and the concatenation invariant are unaffected.

**Weigh the gain first:** bundled JSON size, load time and memory, against a more complex authoring format (`segtool` `merge`/`validate`/`batch` and the TSV worklists all assume inline fields). The consistency gain may matter more than the size.

### Optimize composite lookup

`SegmentIndex.MatchComposite` is the word-hover lookup for text the game joins from several entries (quest descriptions, clothing + `可染性。`, dialogue pages). It runs only after every other lookup misses and is memoised per drawn string. But the first frame a new string is hovered pays for it: ~40ms for the 67-char Lewis parsnip quest and ~20ms for a string that matches nothing, against the full 15k-entry index. That's a visible hitch, and it grows with text length. Costs, biggest first:

1. `LongestRunFrom` scans every key with `IndexOf` at each run start (O(positions × entries)). Replace it with an n-gram index built at load (e.g. 3-gram → entry list), or a suffix array over the keys.
2. Every position tries every length as a whole-entry lookup, allocating a substring each time (O(n²) allocations). Walk a trie of the spaceless keys instead; it gives every entry starting at a position in one pass.
3. Each position runs every template whose anchor appears in the text. Only try one where its first literal actually starts at that position.

Alternatives: precompute dialogue pages at load by splitting entries at `#$b#` / `#$e#` (see [Verify dialogue sentence translation](#verify-dialogue-sentence-translation)), which moves the commonest case to an exact lookup; or run the composite off the draw thread.

### Quiet fallback log

The "fallback split" hover log fires on any text, and most of what it logged in the third session was English (GMCM labels, save names) and bare numbers (`25%`). Skip text with no hanzi, so every line it logs is a real gap.

### Remove debug logs

Remove the debug logging added while troubleshooting.

### Guard gendered-string splitting

Some locales have known bugs in how the game's `^` gender-variant delimiter is used. Where splitting a string on `^` gives something malformed, show no translation rather than a garbled one. There's no sign this has been done.

### Explanatory translation mode

A second translation mode: offline, AI-generated semi-literal translations shown instead of the game's official English. Not built. The data format is defined in the stub `assets/translations/explanatory/zh.json`: entries keyed by a hash of the source string, each holding `original`, `literal` (a word-for-word gloss) and `natural`. Plan:

- Load it alongside `TranslationIndex`, keyed by the original string, so both modes share one lookup path.
- Add a config option or keybind to switch which mode is shown, falling back to the literal translation when there's no explanatory entry.
- Generating the data is out of scope. It's produced elsewhere and dropped into `assets/`.

## The pipeline

Details are in `tools/segment-data/README.md`.

```sh
python3 tools/segment-data/segtool.py batch <Table> 80      # worklist, TSV
# ...author a .tsv outside assets/...
python3 tools/segment-data/segtool.py merge <Table> <file>  # align + check pinyin
python3 tools/segment-data/segtool.py validate
```

Authoring format, one entry per line, tab-separated:

```
key <TAB> english <TAB> text¦pinyin¦gloss‖text¦pinyin¦gloss‖...
```

- Pinyin is one syllable per hanzi, with tone marks or numbers. `merge` normalises it to tone marks and refuses a syllable that isn't a reading of its character.
- `merge` attaches punctuation and dialogue markup to a neighbouring segment, fills in pinyin for hanzi-free segments, and rejects any line it can't line up with the source.
- Output goes to `assets/segments/zh/`, the tracked source of truth, which ships with the mod as-is.

## Done

### Glyph-accurate word hover (2026-09-26)

Word hover no longer works out word positions from wrapping and font measurements. Transpilers on the game's text renderers (`SpriteText.drawString` and the four `SpriteBatch.DrawString` overloads) record where each character is actually drawn, and `GlyphHitTest` hit-tests those positions. This fixed dialogue words being detected in the wrong place after line breaks. Verified in-game.

If a game update breaks a transpiler, the SMAPI log warns about it and that renderer falls back to the old measured layout. The `Glyph capture: …` startup line shows the state of each renderer.

### Segmentation data, Japanese mod (2026-09-25)

From before the fork: the ja data is not in this repo any more. Every piece of Japanese text the game ships has hand-authored segment data: **15,768 entries, 10 keys deliberately skipped, 0 pending.** The live checks:

```sh
python3 tools/segment-data/segtool.py status     # pending=0 everywhere
python3 tools/segment-data/segtool.py validate   # the invariant holds
python3 tools/segment-data/segtool.py audit      # coverage vs the game install
```

`audit` checks two things:

1. **Assets:** all 207 Japanese-localized assets are covered or excluded with a reason, with no known gaps left.
2. **Text:** every Japanese character in a covered asset is held by an entry.

Run it after any game update. It fails loudly on anything new, which is what `status` could never do. `status` only measures against files somebody already chose to extract. That blind spot is how dialogue, events, festivals, TV and schedules sat unnoticed while everything read `pending=0` (see `PostMortems.md`).

How it got here, for the record:

| Entries | Source |
|---|---|
| 9,039 | every `Strings/*` table + the `Data/` record tables |
| 6,519 | the five families that were never imported (dialogue, events, festivals, TV, schedules), by a multi-agent run |
| 92 | found by `audit`'s text check: Quests completion lines (field 9), dialogue inside skipped event scripts, Lewis's phone call |
| 105 | Achievements + SecretNotes, once `XnbStringTool` learned to read `Dictionary<int,string>` |

The 10 skipped keys are event scripts the game never draws whole; their spoken lines are extracted and authored as `<key>#<n>` entries.

### Objects item names, Japanese mod

From before the fork. The 756 `Objects_Name.json` entries have been hand-split into word segments. 526 multi-word names now hover per word (`アメシストの指輪` → `アメシスト` / `の` / `指輪`). The other 230 are single lexical items (one-word names, fish and mineral names, and lexicalized kanji compounds like `黒曜石`) and are deliberately left whole.
