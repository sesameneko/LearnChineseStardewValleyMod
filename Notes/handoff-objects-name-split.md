# Task: split the multi-word entries in `Objects_Name.json`

Self-contained brief. Everything you need is in this repo; read `tools/segment-data/README.md`
for the schema before starting, and `CLAUDE.md` for the project.

## What this is

`tools/extracted-strings/literal-translations/Objects_Name.json` holds hand-authored
word-boundary data for all **756** Stardew Valley item names, in the standard segment schema:

```json
"AmethystRing_Name": {
  "japanese": "アメシストの指輪",
  "english": "Amethyst Ring",
  "segments": [
    { "text": "アメシストの指輪", "reading": "ameshisuto no yubiwa",
      "gloss": "Amethyst Ring", "kana": "アメシストノユビワ" }
  ]
}
```

It was produced mechanically by `tools/segment-data/migrate_names.py` from an older flat
format, and that migration deliberately made **every name a single segment covering the whole
string**. That is right for a one-word name (`木材` = *mokuzai* = Timber) and wrong for a
multi-word one: `アメシストの指輪` should hover as `アメシスト` / `の` / `指輪`.

**Your job: split the multi-word ones into real word segments.** This is an editing pass, not a
translation pass — the Japanese, the reading and the English are already there and correct.

Every other table in the project is already finished (9,039 entries, `pending=0`). This is the
last outstanding segmentation work, tracked in `TODOs.txt` under "Objects item names — split the
multi-word ones".

## The invariant (non-negotiable)

The `text` fields of an entry's segments, concatenated in order, must reproduce `japanese`
**character for character** — no added or dropped spaces, punctuation or tokens. Positions are
computed at runtime by measuring prefixes, so a violation silently mis-places every highlight.
`segtool.py validate` enforces it.

## Triage — which entries to touch

I have already measured the file. Of the 756 single-segment entries:

| Signal | Count | Action |
|---|---|---|
| Japanese spans >1 script run (kanji/hiragana/katakana/latin/digit) | 395 | split |
| Reading contains a space (e.g. `jukusei tamago`, `katakuchi iwashi`) | 514 | split |
| **Union of the two — the worklist** | **530** | **split** |
| Neither signal (e.g. ドングリ *donguri*, リンゴ *ringo*) | 226 | leave as one segment |

The spaced-reading signal is the important one: it catches single-script names that are still two
words, like `熟成卵` (*jukusei tamago*) and `ツリビトアンコウ` (*tsuribito ankō*), which a
script-run test alone misses.

Treat the 530 as a worklist to review, not a mandate — a few will legitimately stay whole (a
single loanword whose romaji just happens to be spaced). Conversely, spot-check the 226: an
unbroken kanji run can still be compound (`高機能テレビリモコン` is in the worklist anyway, but
watch for others).

Generate the worklist with this (writes a TSV of key, japanese, reading, gloss):

```bash
cd /Users/benjaminconnick/Documents/Projects/LanguageStardewValleyMod
python3 - <<'PY' > /tmp/name-worklist.tsv
import json
d = json.load(open('tools/extracted-strings/literal-translations/Objects_Name.json'))
def cls(c):
    o = ord(c)
    if 0x3040 <= o <= 0x309f: return 'H'
    if 0x30a0 <= o <= 0x30ff or c == 'ー': return 'K'
    if 0x4e00 <= o <= 0x9fff: return 'J'
    if c.isdigit(): return 'D'
    if c.isascii() and c.isalpha(): return 'L'
    return 'P'
def nruns(s):
    out = []
    for c in s:
        k = cls(c)
        if not out or out[-1] != k: out.append(k)
    return len(out)
for k, v in d.items():
    if k == '_comment' or not isinstance(v, dict) or len(v['segments']) != 1: continue
    seg = v['segments'][0]
    if nruns(v['japanese']) > 1 or ' ' in seg['reading'].strip():
        print('\t'.join([k, v['japanese'], seg['reading'], seg['gloss']]))
PY
wc -l /tmp/name-worklist.tsv   # expect 530
```

## How to apply the edits — IMPORTANT

**Do not use `segtool.py merge` for this file.** Two hard reasons:

1. `Objects_Name` is not a table segtool can address (`source()` looks for
   `tools/extracted-strings/ja/Objects_Name.json`, which does not exist → crash). And
   `merge Objects …` is worse: `authored()` globs `Objects.json` *and* `Objects_*.json`
   together, then `write_table()` writes a single combined `OUT/Objects.json` — leaving the
   existing `Objects_Name.json` / `Objects_Description.json` in place as duplicates.
2. `merge` rebuilds each segment as `{text, reading, gloss}` only, so it would **drop the `kana`
   field** from every entry it touches. Kana is what actually renders — the game font has no
   macron glyph and substitutes `*` for `ō`/`ū` (see `SegmentIndex.cs` and `FontSafeText.cs`).

So: **edit `Objects_Name.json` directly**, in place, preserving the file's structure
(`_comment` first, then keys in the existing order, 2-space indent, `ensure_ascii=False`,
trailing newline). Write the segments as `{text, reading, gloss}` and let the kana step below
fill `kana` back in.

Work in batches of roughly 40–60 entries, re-reading and rewriting the JSON with a small Python
script each time, so a mistake is cheap to find. Keep a note of which keys you have done.

## Segmentation conventions

Follow `tools/segment-data/README.md`. The ones that matter here:

- **Particles are their own segment.** `アメシストの指輪` → `アメシスト` / `の` / `指輪`.
  Gloss `の` as `(possessive)` or `of` — whatever reads right in that name.
- **Compound nouns split on meaning, not on script.** `熟成卵` → `熟成` (*jukusei*, "matured")
  / `卵` (*tamago*, "egg"). `カタクチイワシ` → `カタクチ` / `イワシ`.
- **Readings carry over from the whole-name reading**, which is already space-separated in the
  right places most of the time — that is why the spaced-reading signal works. Split the reading
  on those spaces and match it to the text pieces; drop the space itself (readings are per
  segment, so no leading/trailing spaces inside a segment's `reading`).
- **Glosses are per segment and in context**, not dictionary entries. The entry's top-level
  `english` stays as the whole-name translation — leave it alone.
- **Tokens like `{0}` stay verbatim and get their own segment** with an empty reading and a
  gloss naming what they are: `熟した{0}卵` → `熟した` / `{0}` (gloss `(flavour)`) / `卵`.
- **Punctuation, brackets and spaces inside the name belong to a segment's `text`** — never drop
  them. `花火（緑）` keeps its full-width parens.

## After the edits

Run these in order, from the repo root:

```bash
# 1. regenerate kana for every segment from its reading (repo-wide, idempotent)
python3 tools/segment-data/romaji_to_kana.py write

# 2. check the concatenation invariant across all data
python3 tools/segment-data/segtool.py validate      # must end "all segment data valid"

# 3. confirm nothing regressed in coverage
python3 tools/segment-data/segtool.py status | tail -1   # expect pending=0

# 4. the mod must still build
dotnet build LanguageStudyStardewValleyMod.csproj
```

Step 1 rewrites `kana` on every segment in every file from its `reading`, and rewrites
`tools/segment-data/kana-review.tsv` with the readings whose long vowels are ambiguous
(`ō` is おう in *gakkō* but おお in *tōri*). Skim the new rows for your entries; a wrong guess is
invisible in game.

Sanity-check a couple of results by eye, e.g.:

```bash
python3 -c "import json;d=json.load(open('tools/extracted-strings/literal-translations/Objects_Name.json'));print(json.dumps(d['AmethystRing_Name'],ensure_ascii=False,indent=2))"
```

## Finishing up

- Update the stale `_comment` at the top of `Objects_Name.json` — it currently warns that "each
  name is currently a SINGLE segment ... those still need a hand pass to split". Replace that
  sentence with what is now true, including how many entries stayed whole and why.
- Update the same claim in `tools/segment-data/migrate_names.py`'s docstring (the "LIMITATION,
  deliberate" paragraph) so it reads as history rather than an open gap.
- Remove the "Objects item names — split the multi-word ones" section from `TODOs.txt` (or
  reduce it to a one-line note if any entries were left deliberately unsplit).
- Commit. This repo's standing rule is to commit completed work; one commit for the pass is fine,
  or one per batch if you prefer. End the message with:

  ```
  Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
  ```

## Verifying it in game (optional)

Word-hover draws a **green** outline for hand-segmented data and **amber** for the character-class
heuristic fallback. Build, launch via `scripts/run.sh`, and hover an item name in a tooltip:
before this pass a multi-word name highlighted as one green blob; after it, each word should
highlight separately. `assets/segments/` is gitignored and regenerated from
`literal-translations/` by the csproj's `CopySegmentData` target, so a plain `dotnet build`
is enough to deploy.
