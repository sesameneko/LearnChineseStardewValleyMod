# TODOs

## Open

- [ ] In-game checks
  - [ ] [Verify dialogue sentence translation](#verify-dialogue-sentence-translation)
  - [ ] [Verify achievements and notes](#verify-achievements-and-notes)
- [ ] Kana readings
  - [ ] [Review kana long vowels](#review-kana-long-vowels)
  - [ ] [Normalise kana script](#normalise-kana-script)
    - [ ] Survey every segment
    - [ ] Enforce in validate and merge
    - [ ] Convert existing data
- [ ] Segment data
  - [ ] [Consolidate redundant glosses](#consolidate-redundant-glosses)
    - [ ] Merge same-sense glosses
    - [ ] Measure compound redundancy
    - [ ] Decide on shared glossary
  - [ ] [Split sign markup segments](#split-sign-markup-segments)
- [ ] Code
  - [ ] [Optimize composite lookup](#optimize-composite-lookup)
    - [ ] N-gram index for substrings
    - [ ] Trie for whole entries
    - [ ] Narrow template attempts
  - [ ] [Quiet fallback log](#quiet-fallback-log)
  - [ ] [Fix line-wrap hit-testing](#fix-line-wrap-hit-testing)
  - [ ] [Remove debug logs](#remove-debug-logs)

## Details

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

### Review kana long vowels

`kana-review.tsv` holds ~15k long-vowel guesses in the *older* data, which was authored romaji-first. `romaji_to_kana.py` guessed each long vowel (ō is おう or おお). They have never been reviewed, and some are known wrong (ēto came out えいと). Newer data is authored in kana and isn't affected. Do this together with [Normalise kana script](#normalise-kana-script).

### Normalise kana script

Decided 2026-09-25, rule in `tools/segment-data/README.md`: **each part in its own script.** Kanji and hiragana are read in hiragana and katakana stays katakana, so `バス停` → `バスてい`. The data breaks it widely. Of the segments mixing katakana and kanji:

| Style | Segments | Example |
|---|---|---|
| all-katakana | ~2,030 | `サイロに入れた。` → `サイロニイレタ` |
| mixed | ~850 | `インテリアを飾ることができます。` → `インテリアをカザルコトガデキマス` (only the particle is hiragana) |
| all-hiragana | ~50 | `クリスタルの木の` → `くりすたるのきの` |

The survey counted only katakana+kanji segments; kanji-only ones are unchecked. Steps:

1. Survey every segment, not just katakana+kanji ones.
2. Add the check to `validate` and `merge`.
3. Convert: each kana run takes the script of the text it reads. **Not fully mechanical.** The all-katakana readings write long vowels with `ー` (`ヤギガカエルヨーニナル`, `ドーブツ`), and in a hiragana reading `ヨー` must become `よう`, not `よー`. Whether `ー` is う or お can't be derived (どう vs とお), so those need a word list or review, like [the long-vowel review](#review-kana-long-vowels). Do the two together.

Romaji is generated from kana. Regenerate it only for segments whose kana was authored, not for the older romaji-first data (see the README).

### Consolidate redundant glosses

Review redundancy in the segment data, and consider pointing repeats at a shared glossary instead of storing a copy in every sentence. Two kinds:

- **Exact repeats.** Measured 2026-09-25: 126,307 glossed segments hold only 54,761 distinct (text, kana, gloss) triples, so ~71,500 (57%) are copies. The top ones:

  | Segment | Gloss | Copies |
  |---|---|---|
  | を | (object marker) | 3,418 |
  | は | (topic marker) | 2,801 |
  | が | (subject marker) | 2,789 |
  | の | (possessive) | 1,113 |

- **Compounds.** A sentence glosses `a b c d` as one segment, while elsewhere `ab` and `cd` each have their own gloss. That's only truly redundant when gloss(abcd) == gloss(ab) + gloss(cd). Otherwise the grouping carries meaning (an idiom, a set phrase) and must stay. Unmeasured.

**Caveat:** glosses are deliberately *in context*, so a shared entry must be keyed by sense, never by text alone. を alone has 14 distinct glosses, e.g. "(object marker)", "(along)", "(from, off)", "(path marker)", and each is correct somewhere. Some of those are the same sense spelled differently ("(object)" vs "(object marker)"). Merging those first would both shrink the data and make hover labels consistent.

**Possible shape:** a glossary file of senses `{id, text, kana, gloss}`, with segments holding a sense id where they match one and inline fields where they don't. The loader expands ids on load, so `SegmentIndex` and the concatenation invariant are unaffected.

**Weigh the gain first:** bundled JSON size, load time and memory, against a more complex authoring format (`segtool` `merge`/`validate`/`batch` and the TSV worklists all assume inline fields). The consistency gain may matter more than the size.

### Split sign markup segments

`StringsFromMaps` `BusStop.1` is `` ` バス停^> ペリカンタウン ``, where `` ` `` and `>` draw as arrow glyphs and `^` is a line break. `merge` attached them to the neighbouring words, so hovering the sign outlines `` ` バス停^ `` and `> ペリカンタウン`, arrows included.

- Split the markup into its own gloss-less segments, and check other `StringsFromMaps` signs for the same thing.
- Its kana (`バステイ`) should be `バスてい`; see [Normalise kana script](#normalise-kana-script).

### Optimize composite lookup

`SegmentIndex.MatchComposite` is the word-hover lookup for text the game joins from several entries (quest descriptions, clothing + `可染性。`, dialogue pages). It runs only after every other lookup misses and is memoised per drawn string. But the first frame a new string is hovered pays for it: ~40ms for the 67-char Lewis parsnip quest and ~20ms for a string that matches nothing, against the full 15k-entry index. That's a visible hitch, and it grows with text length. Costs, biggest first:

1. `LongestRunFrom` scans every key with `IndexOf` at each run start (O(positions × entries)). Replace it with an n-gram index built at load (e.g. 3-gram → entry list), or a suffix array over the keys.
2. Every position tries every length as a whole-entry lookup, allocating a substring each time (O(n²) allocations). Walk a trie of the spaceless keys instead; it gives every entry starting at a position in one pass.
3. Each position runs every template whose anchor appears in the text. Only try one where its first literal actually starts at that position.

Alternatives: precompute dialogue pages at load by splitting entries at `#$b#` / `#$e#` (see [Verify dialogue sentence translation](#verify-dialogue-sentence-translation)), which moves the commonest case to an exact lookup; or run the composite off the draw thread.

### Quiet fallback log

The "fallback split" hover log fires on any text, and most of what it logged in the third session was English (GMCM labels, save names) and bare numbers (`25%`). Skip text with no Japanese characters, so every line it logs is a real gap.

### Fix line-wrap hit-testing

Fix issues detecting word positions on wrapped lines.

### Remove debug logs

Remove the debug logging added while troubleshooting.

## The pipeline

Details are in `tools/segment-data/README.md`.

```sh
python3 tools/segment-data/segtool.py batch <Table> 40      # worklist, TSV
# ...author a .tsv...
python3 tools/segment-data/segtool.py merge <Table> <file>  # align + validate
python3 tools/segment-data/kana_to_romaji.py write          # generate romaji
python3 tools/segment-data/segtool.py validate
```

Authoring format, one entry per line, tab-separated:

```
key <TAB> english <TAB> text¦kana¦gloss‖text¦kana¦gloss‖...
```

- Kana is the source of truth; romaji is generated. Write only the words.
- `merge` attaches punctuation and dialogue markup to a neighbouring segment, fills in kana for kanji-free segments, and rejects any line it can't line up with the source.
- Output goes to `tools/extracted-strings/literal-translations/`, the tracked source of truth. `assets/segments/` is generated from it at build time.

## Done

### M2 segmentation data (2026-09-25)

Every piece of Japanese text the game ships has hand-authored segment data: **15,768 entries, 10 keys deliberately skipped, 0 pending.** The live checks:

```sh
python3 tools/segment-data/segtool.py status     # pending=0 everywhere
python3 tools/segment-data/segtool.py validate   # the invariant holds
python3 tools/segment-data/segtool.py audit      # coverage vs the game install
```

`audit` checks two things:

1. **Assets:** all 207 Japanese-localized assets are covered or excluded with a reason, with no known gaps left.
2. **Text:** every Japanese character in a covered asset is held by an entry.

Run it after any game update. It fails loudly on anything new, which is what `status` could never do. `status` only measures against files somebody already chose to extract. That blind spot is how dialogue, events, festivals, TV and schedules sat unnoticed while everything read `pending=0` (post-mortem in `Plan.md`'s data task).

How it got here, for the record:

| Entries | Source |
|---|---|
| 9,039 | every `Strings/*` table + the `Data/` record tables |
| 6,519 | the five families that were never imported (dialogue, events, festivals, TV, schedules), by a multi-agent run |
| 92 | found by `audit`'s text check: Quests completion lines (field 9), dialogue inside skipped event scripts, Lewis's phone call |
| 105 | Achievements + SecretNotes, once `XnbStringTool` learned to read `Dictionary<int,string>` |

The 10 skipped keys are event scripts the game never draws whole; their spoken lines are extracted and authored as `<key>#<n>` entries.

### Objects item names

The 756 `Objects_Name.json` entries have been hand-split into word segments. 526 multi-word names now hover per word (`アメシストの指輪` → `アメシスト` / `の` / `指輪`). The other 230 are single lexical items (one-word names, fish and mineral names, and lexicalized kanji compounds like `黒曜石`) and are deliberately left whole.
