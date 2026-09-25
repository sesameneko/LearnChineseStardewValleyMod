#!/usr/bin/env python3
"""Work pipeline for hand-authored Japanese word segmentation (see Plan.md's data task).

Sub-commands:
  status                    coverage per string table
  batch <Table> [n] [--offset k]
                            print the next n un-authored entries as an authoring worklist
  merge <Table> <file.tsv>  fold an authored batch into the literal-translations source
  skip  <Table> <file.txt>  record keys deliberately left unsegmented
  validate                  re-check every bundled segment file
  audit [contentDir]        check the pipeline's coverage against the game install

Authoring format (TSV, one entry per line, no header):
  key <TAB> english <TAB> text¦kana¦gloss‖text¦kana¦gloss‖...
Punctuation, whitespace and dialogue markup may be left out of the segments --
merge attaches them to a neighbour -- and kana may be left empty for a segment
with no kanji. Every word must still be there, in order; merge refuses a line
it can't line up with the source, so bad data never reaches the mod. Kana is the
source of truth for readings; kana_to_romaji.py derives the romaji "reading".
"""
import json, os, re, sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
JA = os.path.join(ROOT, "tools", "extracted-strings", "ja")
EN = os.path.join(ROOT, "tools", "extracted-strings", "en")
DATA_JA = os.path.join(ROOT, "tools", "extracted-strings", "data-ja")
DATA_EN = os.path.join(ROOT, "tools", "extracted-strings", "data-en")

# Assets under Content/Data are not one string per key: each value is a
# slash-delimited record, and only some of its fields are text the player ever
# reads. These are expanded into one pseudo-entry per displayed field, keyed
# "<record id>#<field index>", so the rest of the pipeline can treat them like
# any other table. Field indices are 0-based into the split record.
# "fields": None means the value is one piece of text, not a record -- split it
# on nothing. Several Data assets (letters, dialogue) are like that, and some of
# them contain slashes inside the prose, so splitting would truncate them.
DATA_TABLES = {
    # type/name/description/objective/...
    "Data_Quests": {"asset": "Quests", "fields": [1, 2, 3]},
    # englishName/description/price/defense/immunity/colorIndex/displayName
    "Data_Boots": {"asset": "Boots", "fields": [1, 6]},
    # englishName/reward/items/color/count/?/displayName
    "Data_Bundles": {"asset": "Bundles", "fields": [6]},
    # englishName/description/showHair/skipHairstyleOffset/?/displayName/?
    "Data_hats": {"asset": "hats", "fields": [1, 5]},
    # 14 stat fields, then displayName
    "Data_Monsters": {"asset": "Monsters", "fields": [14]},
    # alternating reaction line / item-id list, six pairs
    "Data_NPCGiftTastes": {"asset": "NPCGiftTastes", "fields": [0, 2, 4, 6, 8, 10]},
    # whole-value tables
    "Data_mail": {"asset": "mail", "fields": None},
    "Data_ExtraDialogue": {"asset": "ExtraDialogue", "fields": None},
    "Data_EngagementDialogue": {"asset": "EngagementDialogue", "fields": None},
}
# Whole asset families outside Strings/ and the flat Data/ records above, extracted
# with XnbStringTool into content-ja/ and content-en/, mirroring their path under
# Content/. Each file becomes one table named "<Family>-<file>" (Dialogue-Abigail);
# the hyphen matters, since authored() treats "<Table>_" as a split file of <Table>
# and there is already a Strings/Characters table.
#   "text"   -- every value is one string the game draws (after dialogue markup)
#   "script" -- every value is an event command script; the spoken text is lifted
#               out of it (see script_lines)
#   "mixed"  -- a festival file: most values are dialogue, a few are scripts
CONTENT_JA = os.path.join(ROOT, "tools", "extracted-strings", "content-ja")
CONTENT_EN = os.path.join(ROOT, "tools", "extracted-strings", "content-en")
CONTENT_FAMILIES = {
    "Dialogue": ("Characters/Dialogue", "text"),
    "Schedules": ("Strings/schedules", "text"),
    "TV": ("Data/TV", "text"),
    "Festivals": ("Data/Festivals", "mixed"),
    "Events": ("Data/Events", "script"),
}

# tracked source of truth; assets/segments/ja is generated from it by the
# csproj's CopySegmentData target and is gitignored
OUT = os.path.join(ROOT, "tools", "extracted-strings", "literal-translations")
SKIPS = os.path.join(ROOT, "tools", "segment-data", "skipped")

FIELD, SEG = "¦", "‖"

# The game itself uses ¦ (U+00A6) as a dialogue-variant separator -- e.g. the
# ${male text¦female text}$ form in ItemDeliveryQuest -- so a literal ¦ or ‖
# inside a segment's text is written \¦ / \‖ and split around here.
SPLIT_SEG = re.compile(r"(?<!\\)" + SEG)
SPLIT_FIELD = re.compile(r"(?<!\\)" + FIELD)

COMMENT = ("Word/phrase-level breakdown of Stardew Valley {table}.xnb strings, for in-context "
           "mouse-over translation of individual pieces of a sentence (not a general Japanese-"
           "English dictionary -- glosses are chosen for how each word/phrase functions in THIS "
           "specific sentence). Each entry: \"japanese\" (exact source text), \"english\" (a "
           "natural-ish literal translation, for context), and \"segments\" -- an ordered array of "
           "{{text, reading, gloss}} whose \"text\" fields concatenate back to exactly reproduce "
           "\"japanese\" (validated by tools/segment-data/segtool.py).")


def content_tables():
    """table name -> (asset path under Content/, kind), for every extracted content-family file."""
    out = {}
    for family, (folder, kind) in CONTENT_FAMILIES.items():
        directory = os.path.join(CONTENT_JA, folder)
        if not os.path.isdir(directory):
            continue
        for name in sorted(os.listdir(directory)):
            if name.endswith(".json"):
                out[f"{family}-{name[:-5]}"] = (f"{folder}/{name[:-5]}", kind)
    return out


def source(table):
    if table in DATA_TABLES:
        return data_source(table)
    content = content_tables()
    if table in content:
        return content_source(*content[table])

    with open(os.path.join(JA, table + ".json"), encoding="utf-8") as f:
        ja = json.load(f)["entries"]
    try:
        with open(os.path.join(EN, table + ".json"), encoding="utf-8") as f:
            en = json.load(f)["entries"]
    except FileNotFoundError:
        en = {}
    return ja, en


def data_source(table):
    """Expands a Content/Data asset's slash-delimited records into one entry per
    displayed field. A field that is empty or a placeholder ('.', 'null') is
    dropped, since the player never sees it."""
    spec = DATA_TABLES[table]
    with open(os.path.join(DATA_JA, spec["asset"] + ".json"), encoding="utf-8") as f:
        ja_records = json.load(f)["entries"]
    try:
        with open(os.path.join(DATA_EN, spec["asset"] + ".json"), encoding="utf-8") as f:
            en_records = json.load(f)["entries"]
    except FileNotFoundError:
        en_records = {}

    ja, en = {}, {}
    if spec["fields"] is None:
        for record_id, record in ja_records.items():
            ja[record_id] = record
            if record_id in en_records:
                en[record_id] = en_records[record_id]
        return ja, en

    for record_id, record in ja_records.items():
        parts = record.split("/")
        en_parts = en_records.get(record_id, "").split("/")
        for index in spec["fields"]:
            if index >= len(parts):
                continue
            text = parts[index]
            if not text or text in (".", "null"):
                continue
            key = f"{record_id}#{index}"
            ja[key] = text
            if index < len(en_parts):
                en[key] = en_parts[index]
    return ja, en


# A script is '/'-separated commands; spoken text is a double-quoted argument
# (speak Abigail "...", message "...", question fork1 "...#...#...", end dialogue
# ... "...", textAboveHead ...) -- except quickQuestion, whose prompt and answers
# are the bare text up to the first (break), '#'-separated, each drawn on its own.
QUOTED = re.compile(r'"([^"]*)"')
QUICK_QUESTION = re.compile(r"quickQuestion ([^/]*?)\(break\)")
SCRIPT_COMMAND = re.compile(r"(^|/)(pause|speak|move|faceDirection|viewport|playMusic|globalFade|end)\b")


def script_lines(script):
    """Every piece of text a script draws, in script order."""
    found = []
    for match in QUICK_QUESTION.finditer(script):
        found.append((match.start(), [part for part in match.group(1).split("#") if part]))
    for match in QUOTED.finditer(script):
        found.append((match.start(), [match.group(1)]))
    return [line for _, lines in sorted(found, key=lambda f: f[0]) for line in lines if line]


def is_script(value):
    return bool(SCRIPT_COMMAND.search(value))


def content_source(asset, kind):
    def load(root):
        try:
            with open(os.path.join(root, asset + ".json"), encoding="utf-8") as f:
                return json.load(f)["entries"]
        except FileNotFoundError:
            return {}

    ja_all, en_all = load(CONTENT_JA), load(CONTENT_EN)
    ja, en = {}, {}
    for key, value in ja_all.items():
        if kind == "text" or (kind == "mixed" and not is_script(value)):
            ja[key] = value
            if key in en_all:
                en[key] = en_all[key]
            continue

        # a script: one pseudo-entry per spoken line, keyed "<event id>#<n>". The id is
        # the key up to its first precondition, which is what the game itself calls it.
        lines = script_lines(value)
        en_lines = script_lines(en_all.get(key, ""))
        script_id = key.split("/")[0]
        if any(k.startswith(script_id + "#") for k in ja):
            script_id = key
        for n, line in enumerate(lines):
            ja[f"{script_id}#{n}"] = line
            # locales can disagree on how a script's lines are split; only pair the
            # English when the shapes match, rather than offset every line after a mismatch
            if len(en_lines) == len(lines):
                en[f"{script_id}#{n}"] = en_lines[n]
    return ja, en


def authored(table):
    """Every entry already authored for a table, across all of its files.

    Objects was authored before this tool existed and is split into
    Objects_Description.json / Objects_Name.json; newer tables get one
    <Table>.json each."""
    out = {}
    if not os.path.isdir(OUT):
        return out
    for name in sorted(os.listdir(OUT)):
        if name == table + ".json" or name.startswith(table + "_"):
            with open(os.path.join(OUT, name), encoding="utf-8") as f:
                # Objects_Name.json is a flat key -> translation map with no segments,
                # so it counts as translated but not as segmented
                out.update({k: v for k, v in json.load(f).items()
                            if k != "_comment" and isinstance(v, dict)})
    return out


def skipped(table):
    path = os.path.join(SKIPS, table + ".txt")
    if not os.path.exists(path):
        return set()
    with open(path, encoding="utf-8") as f:
        return {line.strip() for line in f if line.strip() and not line.startswith("#")}


def has_japanese(text):
    return any("぀" <= c <= "ヿ" or "一" <= c <= "鿿" for c in text)


def pending(table):
    ja, en = source(table)
    done, skip = authored(table), skipped(table)
    return [(k, v, en.get(k, "")) for k, v in ja.items()
            if k not in done and k not in skip and has_japanese(v)]


def tables():
    return sorted([f[:-5] for f in os.listdir(JA) if f.endswith(".json")] + list(DATA_TABLES) + list(content_tables()))


def cmd_status(args):
    tot_d = tot_p = tot_s = 0
    rows = []
    for t in tables():
        ja, _ = source(t)
        d, s = len(authored(t)), len(skipped(t))
        p = len(pending(t))
        n = len(ja)
        tot_d, tot_p, tot_s = tot_d + d, tot_p + p, tot_s + s
        rows.append((p, t, n, d, s, p))
    for _, t, n, d, s, p in sorted(rows, reverse=True):
        bar = "done" if p == 0 else f"{d}/{d + p}"
        print(f"{t:<26} entries={n:<5} authored={d:<5} skipped={s:<5} pending={p:<5} {bar}")
    print(f"\nTOTAL authored={tot_d} skipped={tot_s} pending={tot_p}")


def esc(text):
    """Newlines and tabs would break the one-entry-per-line TSV worklist."""
    return (text.replace("\\", "\\\\").replace("\n", "\\n").replace("\t", "\\t")
                .replace(FIELD, "\\" + FIELD).replace(SEG, "\\" + SEG))


def unesc(text):
    out, i = [], 0
    while i < len(text):
        if text[i] == "\\" and i + 1 < len(text):
            out.append({"n": "\n", "t": "\t", "\\": "\\", FIELD: FIELD, SEG: SEG}.get(text[i + 1], text[i + 1]))
            i += 2
        else:
            out.append(text[i]); i += 1
    return "".join(out)


def cmd_batch(args):
    table = args[0]
    n = int(args[1]) if len(args) > 1 and args[1].isdigit() else 40
    offset = int(args[args.index("--offset") + 1]) if "--offset" in args else 0
    items = pending(table)[offset:offset + n]
    for k, v, e in items:
        print(f"{k}\t{esc(v)}\t{esc(e)}")
    print(f"# {len(items)} entries; {len(pending(table)) - offset - len(items)} still pending after this batch",
          file=sys.stderr)


KANJI = re.compile(r"[一-鿿々〆]")


def parse_line(line):
    parts = line.rstrip("\n").split("\t")
    if len(parts) < 3:
        raise ValueError("expected 3 tab-separated columns")
    key, english, segs = parts[0].strip(), parts[1].strip(), parts[2]
    out = []
    for chunk in SPLIT_SEG.split(segs):
        if not chunk:
            continue
        bits = SPLIT_FIELD.split(chunk)
        # text¦gloss: the kana left empty for a kanji-free segment, with its separator dropped
        # too. With kanji the missing field is ambiguous (kana or gloss?), so that still fails.
        if len(bits) == 2 and not KANJI.search(bits[0]):
            bits = [bits[0], "", bits[1]]
        if len(bits) != 3:
            raise ValueError(f"segment {chunk!r} needs text{FIELD}kana{FIELD}gloss")
        text, kana = unesc(bits[0]), " ".join(bits[1].split())
        # the kana is a reading: a kanji in it means the field was filled with the text, or
        # with romaji-era habits; and a kanji segment with no kana would show no reading at all
        if KANJI.search(kana):
            raise ValueError(f"segment {text!r}: kana {kana!r} contains kanji")
        if KANJI.search(text) and not kana:
            raise ValueError(f"segment {text!r} has kanji but no kana")
        out.append({"text": text, "kana": kana, "gloss": bits[2].strip()})
    return key, english, out


# --- alignment: what merge fills in so the author doesn't have to type it ----------------
#
# Punctuation, whitespace and dialogue markup carry no meaning of their own, but every one
# of them has to land in some segment for the concatenation invariant to hold. Retyping them
# exactly was the main source of rejected lines, and put page breaks and lone "。" segments
# in the wrong places. So an author may leave them out: merge lines the segments up against
# the source and gives each skipped run to a neighbouring segment.

# dialogue markup: $h $s $1 $q..., %noturn / %fork, and the #...# of a page break; plus the
# item references a gift line carries ([166], [90 88 86 535]), which draw an icon, not a word
MARKUP = re.compile(r"\$[A-Za-z0-9]+|%[A-Za-z]+[0-9]*|\[[0-9 ]+\]")  # %kid1 is one token
# opening brackets and quotes belong to the word they open, not the one before
OPENERS = "（(「『【〈《[{“‘"
# a page break (#$b# / #$e#) followed by more Japanese inside one segment
BREAK_INSIDE = re.compile(r"#\$[be]#.*[぀-ヿ一-鿿]", re.DOTALL)


def japanese(text):
    return any("぀" <= c <= "ヿ" or "一" <= c <= "鿿" or c == "々" for c in text)


def attachable(gap):
    """True if a run of source text holds nothing a reader would hover: only punctuation,
    whitespace and markup. Letters, digits, Japanese, @ and {0}-style tokens don't qualify --
    those are words (or stand for one) and need a segment of their own."""
    rest = MARKUP.sub("", gap)
    return not any(c.isalnum() or c in "@{}" or japanese(c) for c in rest)


def align(source_text, segments):
    """Fits segments onto the source, attaching every skipped run of punctuation/markup to a
    neighbour. Returns the segments with their text extended, or raises ValueError naming
    the first segment that can't be placed."""
    out, pos = [], 0
    for seg in segments:
        text = seg["text"]
        at = source_text.find(text, pos) if text else -1
        if at < 0 or not attachable(source_text[pos:at]):
            raise ValueError(f"can't place segment {text!r} after {source_text[:pos]!r}")
        gap = source_text[pos:at]
        # an opening bracket starts the next word; everything else ends the previous one
        split = len(gap)
        while split > 0 and gap[split - 1] in OPENERS:
            split -= 1
        trailing, leading = gap[:split], gap[split:]
        if out:
            out[-1]["text"] += trailing
        else:
            leading = gap
        out.append(dict(seg, text=leading + text))
        pos = at + len(text)

    tail = source_text[pos:]
    if tail:
        if not out or not attachable(tail):
            raise ValueError(f"source continues past the last segment: {tail!r}")
        out[-1]["text"] += tail

    # a segment that is itself only punctuation/markup gets folded into its neighbour too,
    # so authors can't produce a lone "。" by including it either
    # -- except right after a page break: then it is a whole page of its own ("…！！！") and
    # needs its own hover, not the previous page's
    folded = []
    for seg in out:
        if (folded and not japanese(seg["text"]) and attachable(seg["text"])
                and not re.search(r"#\$[be]#\s*$", folded[-1]["text"])):
            folded[-1]["text"] += seg["text"]
        else:
            folded.append(seg)
    if len(folded) > 1 and not japanese(folded[0]["text"]) and attachable(folded[0]["text"]):
        folded[1]["text"] = folded[0]["text"] + folded[1]["text"]
        folded = folded[1:]

    # a segment that *starts* with the previous sentence's leftovers ("…$u#$b#あれ、") just
    # has them on the wrong side of the boundary; hand them back rather than reject the line
    for i in range(1, len(folded)):
        text = folded[i]["text"]
        cut = max((m.end() for m in re.finditer(r"#\$[be]#", text)), default=0)
        if cut and attachable(text[:cut]):
            folded[i - 1]["text"] += text[:cut]
            folded[i]["text"] = text[cut:]

    # a source that *opens* with a page break ("…$u#$b#…キミは") has no previous segment to own
    # it, so it becomes a markup-only segment of its own -- the page before is just "…"
    head = folded[0]["text"]
    cut = max((m.end() for m in re.finditer(r"#\$[be]#", head)), default=0)
    if cut and cut < len(head) and attachable(head[:cut]):
        folded[0] = dict(folded[0], text=head[cut:])
        folded.insert(0, {"text": head[:cut], "kana": "", "gloss": "(page break)"})

    for seg in folded:
        if BREAK_INSIDE.search(seg["text"]):
            raise ValueError(f"segment {seg['text']!r} has a page break inside it -- split it there")
    return folded


def kana_of(text):
    """The reading of a segment with no kanji: its own kana, minus punctuation and markup.
    Latin and digits the text keeps pass through, per the kana conventions."""
    rest = MARKUP.sub("", re.sub(r"\{\d+\}", "", text))  # a {0} token is substituted, not read
    kept = "".join(c if (japanese(c) or c == "ー" or c.isalnum() or c.isspace()) else " " for c in rest)
    return " ".join(kept.split())


def cmd_merge(args):
    table, path = args[0], args[1]
    ja, _ = source(table)
    existing = authored(table)
    added = rejected = 0
    with open(path, encoding="utf-8") as f:
        for lineno, line in enumerate(f, 1):
            if not line.strip() or line.startswith("#"):
                continue
            try:
                key, english, segs = parse_line(line)
            except ValueError as ex:
                print(f"  line {lineno}: {ex}", file=sys.stderr); rejected += 1; continue
            if key not in ja:
                print(f"  line {lineno}: no such key {key!r} in {table}", file=sys.stderr); rejected += 1; continue
            try:
                segs = align(ja[key], segs)
            except ValueError as ex:
                print(f"  line {lineno}: {key}: {ex}\n    source: {ja[key]!r}", file=sys.stderr)
                rejected += 1; continue
            for seg in segs:
                if not seg["kana"] and not KANJI.search(seg["text"]):
                    seg["kana"] = kana_of(seg["text"])
            joined = "".join(s["text"] for s in segs)
            if joined != ja[key]:  # align() guarantees this; kept as the last line of defence
                print(f"  line {lineno}: {key}: segments don't reproduce source\n"
                      f"    source: {ja[key]!r}\n    joined: {joined!r}", file=sys.stderr)
                rejected += 1; continue
            existing[key] = {"japanese": ja[key], "english": english, "segments": segs}
            added += 1
    write_table(table, existing)
    print(f"{table}: merged {added}, rejected {rejected}, total authored {len(existing)}, pending {len(pending(table))}")
    return 1 if rejected else 0


def write_table(table, entries):
    os.makedirs(OUT, exist_ok=True)
    ja, _ = source(table)
    order = {k: i for i, k in enumerate(ja)}
    doc = {"_comment": COMMENT.format(table=table)}
    for k in sorted(entries, key=lambda k: order.get(k, 1 << 30)):
        doc[k] = entries[k]
    with open(os.path.join(OUT, table + ".json"), "w", encoding="utf-8") as f:
        json.dump(doc, f, ensure_ascii=False, indent=2)
        f.write("\n")


def cmd_skip(args):
    table, path = args[0], args[1]
    os.makedirs(SKIPS, exist_ok=True)
    keys = skipped(table)
    with open(path, encoding="utf-8") as f:
        keys |= {l.strip() for l in f if l.strip() and not l.startswith("#")}
    with open(os.path.join(SKIPS, table + ".txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(sorted(keys)) + "\n")
    print(f"{table}: {len(keys)} key(s) marked as deliberately unsegmented")


def cmd_validate(args):
    bad = 0
    for name in sorted(os.listdir(OUT)):
        if not name.endswith(".json"):
            continue
        table = name[:-5]
        if table not in DATA_TABLES and "-" not in table:
            table = table.replace("_Description", "").replace("_Name", "")
        ja, _ = source(table)
        with open(os.path.join(OUT, name), encoding="utf-8") as f:
            doc = json.load(f)
        n = 0
        for key, entry in doc.items():
            if key == "_comment" or not isinstance(entry, dict):
                continue
            n += 1
            joined = "".join(s.get("text", "") for s in entry.get("segments", []))
            if joined != entry.get("japanese"):
                print(f"  {name}:{key}: segments != japanese"); bad += 1
            elif key in ja and ja[key] != entry["japanese"]:
                print(f"  {name}:{key}: japanese != game string"); bad += 1
            if not entry.get("english"):
                print(f"  {name}:{key}: missing english"); bad += 1
        print(f"{name}: {n} entries {'OK' if bad == 0 else ''}")
    print("FAILED" if bad else "all segment data valid")
    return 1 if bad else 0


# ---------------------------------------------------------------------------
# audit: is the pipeline's idea of "every table" still the game's?
#
# Everything above measures coverage against tools/extracted-strings/ -- i.e.
# against the set of assets somebody already chose to extract. That denominator
# is self-referential: `status` can report pending=0 while whole families of
# player-facing text (villager dialogue, events, festivals) have never been
# looked at, which is exactly what happened. `audit` re-derives the denominator
# from the game install instead, and fails on anything it can't account for, so
# an unconsidered asset family is a loud error rather than silence.
# ---------------------------------------------------------------------------

CONTENT_ENV = "STARDEW_CONTENT"
CONTENT_GUESSES = [
    "~/Library/Application Support/Steam/steamapps/common/Stardew Valley/Contents/Resources/Content",
    "~/.steam/steam/steamapps/common/Stardew Valley/Content",
    "~/.local/share/Steam/steamapps/common/Stardew Valley/Content",
    "C:/Program Files (x86)/Steam/steamapps/common/Stardew Valley/Content",
]

# Every localized asset carries a .ja-JP variant, textures included. These are
# the ones that hold no author-able text; each needs a reason on record, so that
# leaving something out is a decision rather than an omission.
EXCLUDED = {
    "Fonts/": "localized font, not text",
    "LooseSprites/": "localized texture (any text is baked into the image)",
    "Maps/": "localized tilesheet texture",
    "Minigames/": "localized texture",
    "TileSheets/": "localized texture",
    "Strings/credits": "List<string>, not a dictionary; scrolling credits have no hover target",
}

# Text the player does read, which the pipeline does not cover yet. Listed so
# audit stays green on what is already known while still failing on anything
# new -- and so the size of the gap is written down somewhere that is checked.
KNOWN_GAPS = {
    "Data/Achievements": "Dictionary<int,string> -- XnbStringTool can't read it yet",
    "Data/SecretNotes": "Dictionary<int,string> -- XnbStringTool can't read it yet",
}


def content_dir(override=None):
    for candidate in [override, os.environ.get(CONTENT_ENV)] + CONTENT_GUESSES:
        if candidate and os.path.isdir(os.path.expanduser(candidate)):
            return os.path.expanduser(candidate)
    return None


def covered_assets():
    """Game asset paths (relative to Content/, no locale suffix) the pipeline covers.

    Derived from what is actually extracted into the repo, not from a hand-kept
    list -- a table that was dropped from tools/extracted-strings/ must show up
    here as uncovered."""
    out = {}
    for name in sorted(os.listdir(JA)):
        if name.endswith(".json"):
            out["Strings/" + name[:-5]] = name[:-5]
    for table, spec in DATA_TABLES.items():
        out["Data/" + spec["asset"]] = table
    for table, (asset, _) in content_tables().items():
        out[asset] = table
    return out


def localized_assets(content):
    """Every asset the game ships a Japanese variant of, as paths under Content/."""
    found = []
    for dirpath, _, filenames in os.walk(content):
        for name in filenames:
            if name.endswith(".ja-JP.xnb"):
                rel = os.path.relpath(os.path.join(dirpath, name), content)
                found.append(rel.replace(os.sep, "/")[: -len(".ja-JP.xnb")])
    return sorted(found)


def classify(asset, covered):
    if asset in covered:
        return "covered", covered[asset]
    for table in (EXCLUDED, KNOWN_GAPS):
        for prefix, reason in table.items():
            if asset == prefix or (prefix.endswith("/") and asset.startswith(prefix)):
                return ("excluded" if table is EXCLUDED else "gap"), (prefix, reason)
    return "unclassified", ("", "")


def cmd_audit(args):
    content = content_dir(args[0] if args else None)
    if not content:
        print(f"can't find the game's Content folder -- pass it as an argument or set ${CONTENT_ENV}")
        return 2
    print(f"game content: {content}\n")

    covered = covered_assets()
    assets = localized_assets(content)
    buckets = {"covered": [], "excluded": [], "gap": [], "unclassified": []}
    for asset in assets:
        verdict, note = classify(asset, covered)
        buckets[verdict].append((asset, note))

    print(f"covered   {len(buckets['covered']):3} asset(s) authored via this pipeline")
    print(f"excluded  {len(buckets['excluded']):3} asset(s) with no author-able text")

    # group the gaps by the prefix they matched, so 52 dialogue files read as one line
    if buckets["gap"]:
        grouped = {}
        for _, (prefix, reason) in buckets["gap"]:
            count, _ = grouped.get(prefix, (0, reason))
            grouped[prefix] = (count + 1, reason)
        print(f"\nKNOWN GAPS -- player-facing text this pipeline does not cover ({len(buckets['gap'])} assets):")
        for prefix, (count, reason) in sorted(grouped.items(), key=lambda kv: -kv[1][0]):
            label = prefix if count == 1 else f"{prefix} ({count} files)"
            print(f"  {label}\n      {reason}")

    # a covered table that the install no longer has -- stale extraction, worth saying
    missing = sorted(set(covered) - set(assets))
    if missing:
        print("\nWARNING -- extracted but not in this install (stale or renamed):")
        for asset in missing:
            print(f"  {asset}")

    if buckets["unclassified"]:
        print(f"\nFAILED -- {len(buckets['unclassified'])} localized asset(s) are accounted for nowhere:")
        for asset, _ in buckets["unclassified"]:
            print(f"  {asset}")
        print("\nEach must be either extracted and authored, or added to EXCLUDED/KNOWN_GAPS\n"
              "in segtool.py with the reason. Silence is what let the dialogue gap sit unnoticed.")
        return 1

    print(f"\nall {len(assets)} localized assets accounted for"
          + (f" ({len(buckets['gap'])} as known gaps)" if buckets["gap"] else ""))
    return 0


CMDS = {"status": cmd_status, "batch": cmd_batch, "merge": cmd_merge, "skip": cmd_skip, "validate": cmd_validate, "audit": cmd_audit}

if __name__ == "__main__":
    if len(sys.argv) < 2 or sys.argv[1] not in CMDS:
        print(__doc__); sys.exit(2)
    sys.exit(CMDS[sys.argv[1]](sys.argv[2:]) or 0)
