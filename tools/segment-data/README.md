# segment-data

Tooling for authoring the hand-segmented word-boundary data the mod reads at
runtime (see `Plan.md`'s data task for why runtime tokenization isn't an option
for Japanese).

## The schema — one format, everywhere

Every file in `../extracted-strings/literal-translations/` is a JSON object keyed
by the game's own string key. `_comment` is a header string; every other value is
an object:

```json
"Acorn_Description": {
  "japanese": "植えるとオークの木が育つ。",
  "english": "If you plant it, an oak tree grows.",
  "segments": [
    { "text": "植える", "kana": "うえる",   "gloss": "to plant", "reading": "ueru" },
    { "text": "と",     "kana": "と",       "gloss": "if / when", "reading": "to" },
    { "text": "育つ。", "kana": "そだつ",   "gloss": "grows",    "reading": "sodatsu" }
  ]
}
```

- `japanese` must equal the game's source string exactly.
- `english` is a natural-ish literal translation of the whole string, for context.
- `segments` are ordered; **concatenating every `text` must reproduce `japanese`
  character-for-character.** Position on screen is computed by measuring segment
  prefixes against the rendered string, so a violation would silently mis-place
  every highlight. `merge` rejects it and `validate` re-checks it.
- Particles (は/が/を/に/の/と …) are their own segment with a bracketed
  functional gloss like `(subject marker)`. Trailing punctuation is folded into
  the preceding word. `{0}`-style tokens are their own segment with an empty
  kana and a gloss naming what the game substitutes, e.g. `(name)`.
- The gloss is what the word means **in this sentence**, not a dictionary entry.

## Readings: kana is the source of truth, romaji is generated

Each segment's reading is **authored as `kana`**. The romaji `reading` is derived
from it by `kana_to_romaji.py`. The direction matters. Kana -> romaji is
deterministic. Romaji -> kana is not: ō is おう in gakkō but おお in tōri, and
romaji has thrown that away. The game's font also draws kana natively but has
no macron glyph, so kana is what the mod shows (`WordHoverOverlay` prefers
`kana` and falls back to `reading`).

The kana field follows these conventions, which the converter relies on:

- the reading of the segment's Japanese only; no punctuation or dialogue markup
- words separated by single spaces: `かんがえこんで しまう`, `に ちがいない`
- hiragana for kanji and hiragana; katakana words stay katakana (`カクテル`); `ー`
  as written
- particles as spelled, not as pronounced: `は` / `へ` / `を` (the converter reads
  a standalone one as wa / e / o)
- long vowels spelled the way the word is actually spelled (`がっこう`, `とおり`,
  `おねえさん`); getting this right is the whole point of authoring kana
- latin and digits the on-screen text keeps (`Joja`, `2.0`) copied as they are;
  empty for a segment that is only markup, symbols, a `{0}` token or `@`

`merge` rejects kana containing kanji, and a segment whose text has kanji but no
kana.

Then run:

```
python3 tools/segment-data/kana_to_romaji.py write    # fill "reading" where missing
python3 tools/segment-data/kana_to_romaji.py report   # round-trip vs readings already in the data
python3 tools/segment-data/kana_to_romaji.py check "がっこう"
```

Kana can't say where a vowel pair straddles a morpheme boundary (思う is omou,
湖 みずうみ is mizuumi), so the converter lengthens by default. A short
`NOT_LONG` word list handles the common exceptions. The romaji is a secondary
view, so a rare wrong macron there is tolerable in a way a wrong kana is not.

**Data authored before this switch** has a hand-written romaji `reading` and a
`kana` generated from it by `romaji_to_kana.py`. That script guessed at every
long vowel and logged each guess in `kana-review.tsv`. For those segments the
romaji is the original and the kana may be wrong (e.g. ēto -> えいと). Review
that file before regenerating any of their readings with
`kana_to_romaji.py write --overwrite`, which would overwrite hand-written romaji
with romaji derived from possibly-wrong kana.

There is no second format. Item names were once a flat
`"key": "English (reading)"` map; `migrate_names.py.retired` converted them, and
`SegmentDataLoader` silently skips any entry whose value isn't an object, so
anything written in another shape simply won't load. That script is retired --
kept for the record, renamed off `.py` and stripped of its shebang, because it
has already been applied and re-running it would overwrite `Objects_Name.json`'s
hand-authored word segments with one whole-string segment per name.

## Content/Data assets

Most displayed text lives in `Content/Strings/*`, but some lives in
`Content/Data/*`, where a value is not one string but a slash-delimited record --
only some of whose fields the player ever reads:

```
"9": "Social/JP-name/JP-description/./null/25/0/-1/true"
       type ^name    ^description   ^objective
```

`segtool.py`'s `DATA_TABLES` maps such a table to its asset and the indices of
its displayed fields, and expands each record into one pseudo-entry per field,
keyed `"<record id>#<field index>"` (e.g. `9#1`). Fields that are empty or a
placeholder (`.`, `null`) are dropped. Everything downstream -- `batch`, `merge`,
the invariant check, `validate` -- then treats them like any other entry, and the
mod doesn't care either, because `SegmentIndex` is keyed by the source *text*
rather than by the key.

Source JSON for these lives in `../extracted-strings/data-ja` and `data-en`,
extracted with `../XnbStringTool` the same way as the `Strings` tables.

## Content families (dialogue, events, festivals, TV, schedules)

Five asset families live outside `Strings/` as one file per NPC / location /
festival. They are extracted to `../extracted-strings/content-ja` and
`content-en`, mirroring their path under `Content/`, and each file is its own
table named `<Family>-<file>`: `Dialogue-Abigail`, `Schedules-Emily`,
`TV-TipChannel`, `Festivals-spring13`, `Events-Town`. (A hyphen, not an
underscore: `authored()` reads `<Table>_*.json` as split files of `<Table>`, and
`Characters_Dialogue_*` would have been swallowed by the `Characters` table.)

`CONTENT_FAMILIES` says what shape each family's values are. Dialogue,
schedules and TV are plain text. Events are command scripts, and festivals are
mostly dialogue with a few scripts mixed in (told apart by `is_script`). A script
is expanded like a `DATA_TABLES` record: one pseudo-entry per line the game draws,
keyed `<event id>#<n>`. Those lines are the double-quoted arguments (`speak`,
`message`, `question`, `textAboveHead`, ...) plus the bare `#`-separated prompt and
answers of a `quickQuestion`. English is paired by position only when both
locales' scripts yield the same number of lines.

Dialogue markup (`$h`, `#$b#`, `#$e#`, `@`, `%`...) stays in the source string,
so it has to stay in the segments. Fold it into the end of the segment before it,
like punctuation. That way a page break always falls on a segment boundary.

## Where the files live

`../extracted-strings/literal-translations/` is the **tracked source of truth**.
`assets/segments/ja/` is gitignored and generated from it by the csproj's
`CopySegmentData` target on every build — never edit that copy.

## Authoring a table

```
python3 tools/segment-data/segtool.py status            # coverage per table
python3 tools/segment-data/segtool.py batch Tools 50    # next worklist, as TSV
#   ...author into a .tsv...
python3 tools/segment-data/segtool.py merge Tools batch.tsv
python3 tools/segment-data/kana_to_romaji.py write      # generate the romaji readings
python3 tools/segment-data/segtool.py validate
python3 tools/segment-data/segtool.py audit       # coverage vs. the game install
```

`batch` prints `key <TAB> japanese <TAB> official-English` for entries not yet
authored (entries with no Japanese characters are skipped automatically; newlines
and tabs are escaped as `\n` / `\t`). Author one line per entry:

```
key <TAB> english <TAB> text¦kana¦gloss‖text¦kana¦gloss‖...
```

`¦` (U+00A6) separates the three fields of a segment, `‖` (U+2016) separates
segments. The game itself uses `¦` as a dialogue-variant separator -- the
`${male text¦female text}$` form in `ItemDeliveryQuest`, for instance -- so a
literal `¦` or `‖` inside a segment's text is written `\¦` / `\‖`; `batch`
already escapes them for you in the worklist it prints.

### What merge fills in for you

Write the words; `merge` supplies the rest.

- **Punctuation, whitespace and dialogue markup may be left out.** `merge` lines
  the segments up against the source. Each skipped run (`。`, `…`, `、`, spaces,
  `$h`, `#$e#`, `%noturn`) goes to the end of the previous segment, except
  opening brackets and quotes (`（`, `「`), which go to the start of the next.
  So `絶品だ¦ぜっぴん だ¦is superb` becomes `絶品だ。`. A page break therefore always lands
  on a segment boundary, and a segment that is only punctuation is folded into
  its neighbour even if you wrote one.
- **Kana may be left empty for a segment with no kanji.** `merge` takes the
  segment's own kana, minus punctuation and markup: `ありがとう¦¦thank you`. The
  one exception is a segment of several words that includes the particle は, へ
  or を. Write its kana out spaced (`には¦に は¦…`), or the generated romaji
  reads "niha".
- **Item references and decorative symbols count as markup.** `[166]` or
  `[90 88 86 535]` on a gift line, and symbols like `♡`, are attached the same way.
- **Two small slips are repaired rather than rejected.** A kanji-free segment
  written `text¦gloss` (the empty kana dropped along with its separator) is read
  as `text¦¦gloss`. A segment that starts with the previous sentence's leftovers
  (`…$u#$b#あれ、`) has them moved back onto the segment before.
- **Every word must still be written, in order.** Letters, digits, Japanese, `@`
  and `{0}` tokens are never filled in. They are words, or stand for one, so
  they need their own segment. A page break written *inside* a segment
  (`…！$h#$e#パースニップ`) is rejected: split it there.

`merge` refuses any line it can't line up with the source and reports which
segment failed, so a bad batch can't reach the mod. Fix it and re-merge;
merging is idempotent per key.

`skip <Table> <file.txt>` records keys deliberately left unsegmented so `status`
stops counting them as pending.

## Priority

See `TODOs.txt` at the repo root, which overrides `Plan.md`'s ordering.
