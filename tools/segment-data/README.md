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
    { "text": "植える", "reading": "ueru",    "gloss": "to plant" },
    { "text": "と",     "reading": "to",      "gloss": "if / when" },
    { "text": "育つ。", "reading": "sodatsu", "gloss": "grows" }
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
  reading and a gloss naming what the game substitutes, e.g. `(name)`.
- The gloss is what the word means **in this sentence**, not a dictionary entry.

There is no second format. Item names were once a flat
`"key": "English (reading)"` map; `migrate_names.py` converted them, and
`SegmentDataLoader` silently skips any entry whose value isn't an object, so
anything written in another shape simply won't load.

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
python3 tools/segment-data/segtool.py validate
```

`batch` prints `key <TAB> japanese <TAB> official-English` for entries not yet
authored (entries with no Japanese characters are skipped automatically; newlines
and tabs are escaped as `\n` / `\t`). Author one line per entry:

```
key <TAB> english <TAB> text¦reading¦gloss‖text¦reading¦gloss‖...
```

`¦` (U+00A6) separates the three fields of a segment, `‖` (U+2016) separates
segments. The game itself uses `¦` as a dialogue-variant separator -- the
`${male text¦female text}$` form in `ItemDeliveryQuest`, for instance -- so a
literal `¦` or `‖` inside a segment's text is written `\¦` / `\‖`; `batch`
already escapes them for you in the worklist it prints. `merge` refuses any line whose segments don't reproduce the source
string and reports which, so a bad batch can't reach the mod; fix and re-merge —
merging is idempotent per key.

`skip <Table> <file.txt>` records keys deliberately left unsegmented so `status`
stops counting them as pending.

## Priority

See `TODOs.txt` at the repo root, which overrides `Plan.md`'s ordering.
