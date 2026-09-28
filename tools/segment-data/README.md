# segment-data

Tooling for authoring the hand-segmented word data the mod reads at runtime: each
Chinese string the game draws, split into words, with pinyin and an English gloss for
each. Chinese is written without spaces, and a word-boundary tokenizer (jieba and the
like) guesses at exactly the cases a learner needs right: 种出 is 种 + 出 here, and 行
is xíng or háng depending on the word. So every string is authored by hand, as it was
for the Japanese mod this was forked from (see "Matching drawn text to segment data" in
`HowItWorks.md`).

`pinyin.py` needs pypinyin: `pip3 install --user pypinyin`.

## The schema: one format, everywhere

Every file in `assets/segments/zh/` is a JSON object keyed by the game's own string key.
`_comment` is a header string, and every other value is an object:

```json
"Acorn_Description": {
  "chinese": "可以种出一棵橡树。",
  "english": "Can plant out one oak tree.",
  "segments": [
    { "text": "可以",  "pinyin": "kě yǐ",     "gloss": "can" },
    { "text": "种",    "pinyin": "zhòng",     "gloss": "plant" },
    { "text": "出",    "pinyin": "chū",       "gloss": "(result: out)" },
    { "text": "一",    "pinyin": "yì",        "gloss": "one" },
    { "text": "棵",    "pinyin": "kē",        "gloss": "(measure word for trees)" },
    { "text": "橡树。", "pinyin": "xiàng shù", "gloss": "oak tree" }
  ]
}
```

- `chinese` must equal the game's source string exactly.
- `english` is a literal translation of the whole string **from the Chinese**, for
  context. It isn't the official English (the mod shows that separately), and it
  should show how the Chinese says it.
- `segments` are ordered, and **concatenating every `text` must reproduce `chinese`
  character for character.** Positions on screen are matched by that invariant, so a
  violation would put every highlight in the wrong place. `merge` rejects it and
  `validate` re-checks it.
- The gloss is what the word means **in this sentence**, not a dictionary entry.

## Segmentation conventions

A segment is a word, roughly as a learner's dictionary (CC-CEDICT, Pleco) would list
it. When unsure, ask whether a learner would look it up as one item.

- **Dictionary words stay whole:** 遥控器, 橡树, 不可思议, 我们, 已经, 因为. So do
  four-character idioms (一模一样) and names of people and places (皮埃尔,
  德米特里厄斯, 鹈鹕镇), including places the game builds from ordinary words
  (社区中心 `Community Center`).
- **Item names and titles are split into their words**, because they're descriptions
  rather than names: 高级 / 电视 / 遥控器, 红叶 / 卷心菜, 旅行 / 商人. A name that is
  itself one dictionary word stays whole (苋菜, 紫水晶), and so does a coined game term
  whose parts don't add up to its meaning (星之果实 `stardrop`, 收集包 `bundle`).
- **Productive combinations are split.** A verb plus a result or direction complement
  that a dictionary wouldn't list is two segments: 种 / 出, 拿 / 过来. One it does list
  stays whole: 打开, 看见, 找到, 起来 (when lexicalised). A potential complement is
  three: 用 / 不 / 惯, 过 / 不 / 了 (`liǎo`). Its 不 is neutral (`bu`) and glossed as
  a word, `not (can't)`, since it can be saved like one.
- **Grammar words are their own segment, with a bracketed functional gloss:**
  的 `(possessive)` or `(modifier marker)`, 地 `(adverb marker)`, 得 `(complement
  marker)`, 了 `(completed action)` or `(change of state)`, 着 `(ongoing)`, 过
  `(experienced)`, 吗 `(yes/no question)`, 呢, 吧 `(suggestion)`, 啊/呀 `(exclamation)`,
  把 `(object marker)`, 被 `(passive marker)`. The flashcard feature refuses to save a
  word whose whole gloss is one parenthesised note, so this is also what keeps
  particles out of the deck.
- **Numbers and measure words are separate:** 一 / 棵, 三 / 个. The measure word's
  gloss says what it counts: `(measure word for trees)`.
- **Separable verbs** are one segment when together (帮忙, 睡觉) and split when
  something comes between them (帮 / 个 / 忙).
- **Reduplication** is one segment: 看看, 慢慢.
- **Punctuation and markup never get their own segment.** They're folded into the
  word before (see "What merge fills in").
- **Tokens** (`{0}`, `@`) are their own segment, with empty pinyin and a gloss naming
  what the game puts there: `(your name)`, `(item)`.

Word-split Chinese averages about 1.5 hanzi per segment, and almost nothing but names
and idioms reaches four. So segments over three hanzi are counted as a sign of
clause-splitting (see "Segment size").

## Readings: pinyin

A segment's `pinyin` is **one syllable per hanzi, space-separated, lowercase, with tone
marks**: `橡树` → `xiàng shù`. `pinyin.py` defines the format and `merge` checks every
segment against it:

- Tone numbers are accepted on input and converted: `xiang4 shu4` → `xiàng shù`. So are
  `v` and `u:` for ü (`nv3` → `nǚ`).
- **The neutral tone has no mark and no number:** 东西 `dōng xi`, 我们 `wǒ men`,
  的 `de`.
- **一 and 不 are written as spoken** (the tone sandhi textbooks mark): 一个 `yí gè`,
  一起 `yì qǐ`, 不是 `bú shì`, 不好 `bù hǎo`. Third-tone sandhi is **not** written:
  你好 is `nǐ hǎo`.
- **Erhua:** 儿 as a suffix is `r`, so 一点儿 is `yì diǎn r`, and the display joins
  it to the syllable before. 儿 as a word (儿子) is `ér`.
- **Latin and digits** the text keeps are copied as they are: `Joja超市` →
  `Joja chāo shì`. A number written in digits may instead be read aloud (`500金` →
  `wǔ bǎi jīn`), which is more useful to a learner. Both forms pass.
- Pinyin is left empty for a segment that is only a token, markup or symbols. `merge`
  fills it for a hanzi-free segment you leave empty.
- Proper names use the ordinary lowercase reading of their characters (皮埃尔 `pí āi
  ěr`). Capitalisation is a display question.

What `merge` checks:

1. Every token is a real pinyin syllable, or text copied from the segment.
2. Where the segment is all hanzi (plus punctuation and markup), there is **exactly
   one syllable per hanzi.**
3. **Each syllable is a dictionary reading of its hanzi** (pypinyin's heteronym
   list). A wrong character reading (你 as `wǒ`) or a wrong tone (石 as `shì`) is
   refused. The neutral tone is allowed on any character, and 一 and 不 take any of
   their tones.

It then prints a `note:` (warning only) where a multi-hanzi segment's reading differs
from pypinyin's phrase dictionary: 你好 written `nǐ hào` passes check 3, because 好
does have a `hào` reading, but gets a note. pypinyin is wrong often enough on
polyphones (银行 as `yín xíng`) that its opinion can't be a rule. Read each note and
fix the ones that are right. Notes seen to be wrong, so leave these readings alone:
塞 in transliterated names (塞巴斯蒂安 `sài`, not `sāi`), 挣 "earn" (挣取 `zhèng`,
not `zhēng`), and interjections (哇哦 `wā ò`).

```
python3 tools/segment-data/pinyin.py check 橡树 "xiang4 shu4"   # normalise and check one segment
```

## Content/Data assets

Most displayed text lives in `Content/Strings/*`, but some lives in `Content/Data/*`,
where a value is not one string but a slash-delimited record, only some of whose
fields the player ever reads:

```
"9": "Social/<name>/<description>/./null/25/0/-1/true"
       type   ^name  ^description  ^objective
```

`segtool.py`'s `DATA_TABLES` maps such a table to its asset and the indices of its
displayed fields, and expands each record into one pseudo-entry per field, keyed
`"<record id>#<field index>"` (e.g. `9#1`). Fields that are empty or a placeholder
(`.`, `null`) are dropped. Everything downstream (`batch`, `merge`, the invariant
check, `validate`) then treats them like any other entry. The mod doesn't care
either, because `SegmentIndex` is keyed by the source *text* rather than by the key.

Source JSON for these lives in `../extracted-strings/data-zh` and `data-en`.

## Content families (dialogue, events, festivals, TV, schedules)

Five asset families live outside `Strings/` as one file per NPC, location or festival.
They are extracted to `../extracted-strings/content-zh` and `content-en`, mirroring
their path under `Content/`, and each file is its own table named `<Family>-<file>`:
`Dialogue-Abigail`, `Schedules-Emily`, `TV-TipChannel`, `Festivals-spring13`,
`Events-Town`. (The separator is a hyphen, not an underscore, because `authored()`
reads `<Table>_*.json` as split files of `<Table>`, and `Characters_Dialogue_*` would
have been swallowed by the `Characters` table.)

`CONTENT_FAMILIES` says what shape each family's values are. Dialogue, schedules and
TV are plain text. Events are command scripts, and festivals are mostly dialogue with a
few scripts mixed in (told apart by `is_script`). A script is expanded like a
`DATA_TABLES` record: one pseudo-entry per line the game draws, keyed `<event id>#<n>`.
Those lines are the double-quoted arguments (`speak`, `message`, `question`,
`textAboveHead`, ...) plus the bare `#`-separated prompt and answers of a
`quickQuestion`. English is paired by position only when both locales' scripts yield
the same number of lines.

Dialogue markup (`$h`, `#$b#`, `#$e#`, `@`, `%`...) stays in the source string, so it
has to stay in the segments. It is folded into the end of the segment before it, like
punctuation, so a page break always falls on a segment boundary.

## Where the files live

`assets/segments/zh/` is the **tracked source of truth**, and ships with the mod as-is:
ModBuildConfig deploys the project's whole `assets/` folder, whatever the file type.
Keep scratch files (TSV batches, backups) out of it, or they ship too.

## Authoring a table

```
python3 tools/segment-data/segtool.py status            # coverage per table
python3 tools/segment-data/segtool.py batch Tools 50    # next worklist, as TSV
#   ...author into a .tsv, outside assets/...
python3 tools/segment-data/segtool.py merge Tools batch.tsv
python3 tools/segment-data/segtool.py validate
python3 tools/segment-data/segtool.py audit             # coverage vs. the game install
```

`batch` prints `key <TAB> chinese <TAB> official-English` for entries not yet authored
(entries with no hanzi are skipped automatically; newlines and tabs are escaped as `\n`
/ `\t`). Author one line per entry:

```
key <TAB> english <TAB> text¦pinyin¦gloss‖text¦pinyin¦gloss‖...
```

For example:

```
Acorn_Description	Can plant out one oak tree.	可以¦kě yǐ¦can‖种¦zhòng¦plant‖出¦chū¦(result: out)‖一¦yì¦one‖棵¦kē¦(measure word for trees)‖橡树¦xiàng shù¦oak tree
```

`¦` (U+00A6) separates the three fields of a segment, and `‖` (U+2016) separates
segments. The game itself uses `¦` as a dialogue-variant separator (the
`${male text¦female text}$` form in `ItemDeliveryQuest`, for instance), so a literal `¦`
or `‖` inside a segment's text is written `\¦` / `\‖`. `batch` already escapes them in
the worklist it prints.

### What merge fills in for you

Write the words, and `merge` supplies the rest.

- **Punctuation, whitespace and dialogue markup may be left out.** `merge` lines the
  segments up against the source. Each skipped run (`。`, `，`, `……`, spaces, `$h`,
  `#$e#`, `%noturn`) goes to the end of the previous segment, except opening brackets
  and quotes (`（`, `“`, `《`), which go to the start of the next. So `橡树¦xiàng
  shù¦oak tree` becomes `橡树。`. A page break therefore always lands on a segment
  boundary, and a segment that is only punctuation is folded into its neighbour even
  if you wrote one. The exception is a segment right after a page break: that one is a
  whole page of its own (`……！`) and keeps its own hover. A source that *opens* with a
  page break gets a markup-only first segment.
- **Pinyin may be left empty for a segment with no hanzi**, and `merge` copies the
  latin and digits it keeps: `Joja¦¦Joja`.
- **Item references and decorative symbols count as markup.** `[166]` or
  `[90 88 86 535]` on a gift line, and symbols like `♡`, are attached the same way.
- **So are commands the game never draws**, arguments and all: a question and its
  answers (`$q 32 null#`, `$r 32 0 Event_Rain_1#`), a random or conditional choice
  (`$c .5#`, `$p 17#`, `$d joja#`, `$query PLAYER_NPC_RELATIONSHIP …#`), a mail
  attachment (`%item id (O)434 1 %%`), and the `${` `}$` around a gendered pair. The
  text on each side of a choice (`#`, `|`, `^`, the `_` of a `$y` question) is drawn,
  so write the words of every alternative, in source order.
- **Straight quotes and asterisks** (`"交界处"`, `*唉*`) open or close depending on
  how many came before, and go to the start or the end of a word accordingly.
- **Two small slips are repaired rather than rejected.** A hanzi-free segment written
  `text¦gloss` (the empty pinyin dropped along with its separator) is read as
  `text¦¦gloss`. A segment that starts with the previous sentence's leftovers
  (`…$u#$b#那个，`) has them moved back onto the segment before.
- **Every word must still be written, in order.** Letters, digits, hanzi, `@` and
  `{0}` tokens are never filled in. They are words, or stand for one, so they need
  their own segment. A page break written *inside* a segment (`…！$h#$e#防风草`) is
  rejected: split it there.

`merge` refuses any line it can't line up with the source, or whose pinyin fails a
check, and reports which segment failed, so a bad batch can't reach the mod. Fix it
and re-merge. Merging is idempotent per key.

### Segment size is warned about, not enforced

The alignment check can't tell a word from a clause: a whole sentence as one segment
still reproduces the source. In the Japanese mod this is how a quarter of the tables
ended up clause-split. One long authoring session on 2026-09-21 drifted batch by batch
from about 2.6 to about 10 characters per segment, and every batch merged cleanly.

So `merge` reports the share of the batch's segments over 3 hanzi (markup and
punctuation aren't counted). Above 10% it prints a `WARNING` with the longest
examples. `validate` does the same per table and lists the tables over the line at the
end (`validate -v` adds examples). Neither changes the exit code. Long names and idioms
are legitimate, so a names-heavy table may cross the line honestly.

The limit was tuned on the 2026-09-28 pilot (81 entries from every kind of table).
Authored by word, 1.1% of its segments were over 3 hanzi, all of them names and
chengyu. The same entries with pairs of segments merged came to 19%, and split by
clause to 70%. With a limit of 4, the merged pairs came to 4% and would never have
warned.

A batch that trips the warning should be re-split before the next one is written,
since the drift compounds. Keep authoring runs short too: the clean large-scale
Japanese data came from jobs of about 80 entries in fresh contexts.

`skip <Table> <file.txt>` records keys deliberately left unsegmented so `status` stops
counting them as pending. The existing skips are event-script keys, which are the same
in every locale.

## Priority

See "Chinese migration" in `TODOs.md` at the repo root.
