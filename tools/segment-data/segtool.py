#!/usr/bin/env python3
"""Work pipeline for hand-authored Chinese word segmentation (see tools/segment-data/README.md).

Sub-commands:
  status                    coverage per string table
  batch <Table> [n] [--offset k]
                            print the next n un-authored entries as an authoring worklist
  merge <Table> <file.tsv>  fold an authored batch into the assets/segments/zh source
  skip  <Table> <file.txt>  record keys deliberately left unsegmented
  validate [-v]             re-check every bundled segment file; also warns (never fails)
                            on tables whose segments look clause-sized, -v lists examples
  audit [contentDir]        check the pipeline's coverage against the game install

Authoring format (TSV, one entry per line, no header):
  key <TAB> english <TAB> text¦pinyin¦gloss‖text¦pinyin¦gloss‖...
Punctuation, whitespace and dialogue markup may be left out of the segments --
merge attaches them to a neighbour -- and pinyin may be left empty for a segment
with no hanzi. Every word must still be there, in order; merge refuses a line
it can't line up with the source, or whose pinyin isn't a reading of its hanzi
(pinyin.py), so bad data never reaches the mod. It also warns (without failing)
when a batch's segments look clause-sized rather than word-sized, and where the
pinyin differs from pypinyin's reading in context.
"""
import json, os, re, sys

import pinyin as py

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SRC = os.path.join(ROOT, "tools", "extracted-strings", "zh")
EN = os.path.join(ROOT, "tools", "extracted-strings", "en")
DATA_SRC = os.path.join(ROOT, "tools", "extracted-strings", "data-zh")
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
    # type/name/description/objective/.../reaction: field 9 is what the NPC says on completion
    "Data_Quests": {"asset": "Quests", "fields": [1, 2, 3, 9]},
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
    # Dictionary<int,string>, readable since XnbStringTool learned int keys.
    # name^description^isVisible^prerequisite^iconIndex
    "Data_Achievements": {"asset": "Achievements", "fields": [0, 1], "sep": "^"},
    # a whole note; ^ is a line break when drawn
    "Data_SecretNotes": {"asset": "SecretNotes", "fields": None},
}
# Whole asset families outside Strings/ and the flat Data/ records above, extracted
# with XnbStringTool into content-zh/ and content-en/, mirroring their path under
# Content/. Each file becomes one table named "<Family>-<file>" (Dialogue-Abigail);
# the hyphen matters, since authored() treats "<Table>_" as a split file of <Table>
# and there is already a Strings/Characters table.
#   "text"   -- every value is one string the game draws (after dialogue markup)
#   "script" -- every value is an event command script; the spoken text is lifted
#               out of it (see script_lines)
#   "mixed"  -- a festival file: most values are dialogue, a few are scripts
CONTENT_SRC = os.path.join(ROOT, "tools", "extracted-strings", "content-zh")
CONTENT_EN = os.path.join(ROOT, "tools", "extracted-strings", "content-en")
CONTENT_FAMILIES = {
    "Dialogue": ("Characters/Dialogue", "text"),
    "Schedules": ("Strings/schedules", "text"),
    "TV": ("Data/TV", "text"),
    "Festivals": ("Data/Festivals", "mixed"),
    "Events": ("Data/Events", "script"),
}

# tracked source of truth, shipped with the mod as-is. SEGTOOL_OUT points merge/validate at a
# scratch folder instead, so parallel authors can check batches without racing on the real files
OUT = os.environ.get("SEGTOOL_OUT") or os.path.join(ROOT, "assets", "segments", "zh")
SKIPS = os.path.join(ROOT, "tools", "segment-data", "skipped")

FIELD, SEG = "¦", "‖"

# The game itself uses ¦ (U+00A6) as a dialogue-variant separator -- e.g. the
# ${male text¦female text}$ form in ItemDeliveryQuest -- so a literal ¦ or ‖
# inside a segment's text is written \¦ / \‖ and split around here.
SPLIT_SEG = re.compile(r"(?<!\\)" + SEG)
SPLIT_FIELD = re.compile(r"(?<!\\)" + FIELD)

COMMENT = ("Word/phrase-level breakdown of Stardew Valley {table}.xnb strings, for in-context "
           "mouse-over translation of individual pieces of a sentence (not a general Chinese-"
           "English dictionary -- glosses are chosen for how each word/phrase functions in THIS "
           "specific sentence). Each entry: \"chinese\" (exact source text), \"english\" (a "
           "natural-ish literal translation, for context), and \"segments\" -- an ordered array of "
           "{{text, pinyin, gloss}} whose \"text\" fields concatenate back to exactly reproduce "
           "\"chinese\" (validated by tools/segment-data/segtool.py).")


def content_tables():
    """table name -> (asset path under Content/, kind), for every extracted content-family file."""
    out = {}
    for family, (folder, kind) in CONTENT_FAMILIES.items():
        directory = os.path.join(CONTENT_SRC, folder)
        if not os.path.isdir(directory):
            continue
        for name in sorted(os.listdir(directory)):
            if name.endswith(".json"):
                out[f"{family}-{name[:-5]}"] = (f"{folder}/{name[:-5]}", kind)
    return out


def source(table):
    """(zh, en) entries for a table. A key deliberately skipped because its value is an event
    script (Strings/Locations has a few) still has spoken lines the player reads, so those are
    lifted out into "<key>#<n>" pseudo-entries exactly as for Data/Events."""
    zh, en = raw_source(table)
    for key in skipped(table):
        if key in zh and is_script(zh[key]):
            lines, en_lines = script_lines(zh[key]), script_lines(en.get(key, ""))
            for n, line in enumerate(lines):
                zh[f"{key}#{n}"] = line
                if len(en_lines) == len(lines):
                    en[f"{key}#{n}"] = en_lines[n]
    return zh, en


def raw_source(table):
    if table in DATA_TABLES:
        return data_source(table)
    content = content_tables()
    if table in content:
        return content_source(*content[table])

    with open(os.path.join(SRC, table + ".json"), encoding="utf-8") as f:
        zh = json.load(f)["entries"]
    try:
        with open(os.path.join(EN, table + ".json"), encoding="utf-8") as f:
            en = json.load(f)["entries"]
    except FileNotFoundError:
        en = {}
    return zh, en


def data_source(table):
    """Expands a Content/Data asset's slash-delimited records into one entry per
    displayed field. A field that is empty or a placeholder ('.', 'null') is
    dropped, since the player never sees it."""
    spec = DATA_TABLES[table]
    with open(os.path.join(DATA_SRC, spec["asset"] + ".json"), encoding="utf-8") as f:
        zh_records = json.load(f)["entries"]
    try:
        with open(os.path.join(DATA_EN, spec["asset"] + ".json"), encoding="utf-8") as f:
            en_records = json.load(f)["entries"]
    except FileNotFoundError:
        en_records = {}

    zh, en = {}, {}
    if spec["fields"] is None:
        for record_id, record in zh_records.items():
            zh[record_id] = record
            if record_id in en_records:
                en[record_id] = en_records[record_id]
        return zh, en

    sep = spec.get("sep", "/")
    for record_id, record in zh_records.items():
        parts = record.split(sep)
        en_parts = en_records.get(record_id, "").split(sep)
        for index in spec["fields"]:
            if index >= len(parts):
                continue
            text = parts[index]
            if not text or text in (".", "null"):
                continue
            key = f"{record_id}#{index}"
            zh[key] = text
            if index < len(en_parts):
                en[key] = en_parts[index]
    return zh, en


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

    zh_all, en_all = load(CONTENT_SRC), load(CONTENT_EN)
    zh, en = {}, {}
    for key, value in zh_all.items():
        if kind == "text" or (kind == "mixed" and not is_script(value)):
            zh[key] = value
            if key in en_all:
                en[key] = en_all[key]
            continue

        # a script: one pseudo-entry per spoken line, keyed "<event id>#<n>". The id is
        # the key up to its first precondition, which is what the game itself calls it.
        lines = script_lines(value)
        en_lines = script_lines(en_all.get(key, ""))
        script_id = key.split("/")[0]
        if any(k.startswith(script_id + "#") for k in zh):
            script_id = key
        for n, line in enumerate(lines):
            zh[f"{script_id}#{n}"] = line
            # locales can disagree on how a script's lines are split; only pair the
            # English when the shapes match, rather than offset every line after a mismatch
            if len(en_lines) == len(lines):
                en[f"{script_id}#{n}"] = en_lines[n]
    return zh, en


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


def has_chinese(text):
    return bool(py.HAN.search(text))


def pending(table):
    zh, en = source(table)
    done, skip = authored(table), skipped(table)
    return [(k, v, en.get(k, "")) for k, v in zh.items()
            if k not in done and k not in skip and has_chinese(v)]


def tables():
    return sorted([f[:-5] for f in os.listdir(SRC) if f.endswith(".json")] + list(DATA_TABLES) + list(content_tables()))


def cmd_status(args):
    tot_d = tot_p = tot_s = 0
    rows = []
    for t in tables():
        zh, _ = source(t)
        d, s = len(authored(t)), len(skipped(t))
        p = len(pending(t))
        n = len(zh)
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
        # text¦gloss: the pinyin left empty for a hanzi-free segment, with its separator dropped
        # too. With hanzi the missing field is ambiguous (pinyin or gloss?), so that still fails.
        if len(bits) == 2 and not py.HAN.search(bits[0]):
            bits = [bits[0], "", bits[1]]
        if len(bits) != 3:
            raise ValueError(f"segment {chunk!r} needs text{FIELD}pinyin{FIELD}gloss")
        text = unesc(bits[0])
        pinyin = py.normalize(bits[1], text)
        problems = py.check(text, pinyin)
        if problems:
            raise ValueError(f"segment {text!r}: " + "; ".join(problems))
        out.append({"text": text, "pinyin": pinyin, "gloss": bits[2].strip()})
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
# %revealtaste:Haley:221 (a secret note revealing a gift taste) is one token too.
# Commands whose ASCII arguments run up to the next # are never drawn either: a question and
# its answers ($q 17/18 Sun_old#, $r 17 0 Sun_17#), a random ($c .5#) or conditional ($p 17#,
# $d joja#, $query PLAYER_NPC_RELATIONSHIP ...#) choice, a mail attachment (%item id (O)434 1 %%)
# and the braces of a gendered ${male^female}$ pair. They must come before the bare $x form.
# Letter formatting ([textcolor black], [letterbg ...]) and event-script commands left inside a
# string between two spoken lines (/pause 500/speak MrQi ") are never drawn either.
MARKUP = re.compile(r"\[(?:textcolor|letterbg)[^\]]*\]|\"?(?:/[A-Za-z][^/\"㐀-鿿]*)+\"|"
                    r"\$(?:query|[qrpcd1])[ ][ -\"$-~]*|%item[^%]*%%|\$\{|\}\$|"
                    r"%revealtaste(:[A-Za-z]+:[0-9A-Za-z()]+)?|\$[A-Za-z0-9]+|%[A-Za-z]+[0-9]*|\[[0-9 ]+\]")  # %kid1 is one token
# opening brackets and quotes belong to the word they open, not the one before
OPENERS = "（(「『【〈《[{“‘"
# marks that open and close alike; which one it is depends on how many came before
SYMMETRIC = "\"*"
# a page break (#$b# / #$e#) followed by more Chinese inside one segment
BREAK_INSIDE = re.compile(r"#\$[be]#.*" + py.HAN.pattern, re.DOTALL)


def chinese(text):
    return bool(py.HAN.search(text))


def attachable(gap):
    """True if a run of source text holds nothing a reader would hover: only punctuation,
    whitespace and markup. Letters, digits, Chinese, @ and {0}-style tokens don't qualify --
    those are words (or stand for one) and need a segment of their own."""
    rest = MARKUP.sub("", gap)
    return not any(c.isalnum() or c in "@{}" or chinese(c) for c in rest)


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
        # (a straight " or the * of *唉* opens when an even number of them come before it)
        split = len(gap)
        while split > 0 and (gap[split - 1] in OPENERS or
                             gap[split - 1] in SYMMETRIC and source_text[:pos + split - 1].count(gap[split - 1]) % 2 == 0):
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
        if (folded and not chinese(seg["text"]) and attachable(seg["text"])
                and not re.search(r"#\$[be]#\s*$", folded[-1]["text"])):
            folded[-1]["text"] += seg["text"]
        else:
            folded.append(seg)
    if len(folded) > 1 and not chinese(folded[0]["text"]) and attachable(folded[0]["text"]):
        folded[1]["text"] = folded[0]["text"] + folded[1]["text"]
        folded = folded[1:]

    # a segment that *starts* with the previous sentence's leftovers ("…$u#$b#那个，") just
    # has them on the wrong side of the boundary; hand them back rather than reject the line
    for i in range(1, len(folded)):
        text = folded[i]["text"]
        cut = max((m.end() for m in re.finditer(r"#\$[be]#", text)), default=0)
        if cut and attachable(text[:cut]):
            folded[i - 1]["text"] += text[:cut]
            folded[i]["text"] = text[cut:]

    # a source that *opens* with a page break ("…$u#$b#…你") has no previous segment to own
    # it, so it becomes a markup-only segment of its own -- the page before is just "…"
    head = folded[0]["text"]
    cut = max((m.end() for m in re.finditer(r"#\$[be]#", head)), default=0)
    if cut and cut < len(head) and attachable(head[:cut]):
        folded[0] = dict(folded[0], text=head[cut:])
        folded.insert(0, {"text": head[:cut], "pinyin": "", "gloss": "(page break)"})

    for seg in folded:
        if BREAK_INSIDE.search(seg["text"]):
            raise ValueError(f"segment {seg['text']!r} has a page break inside it -- split it there")
    return folded


# Segment-size check. Segments are meant to be words, but nothing in align() can tell a
# word from a clause: a whole sentence as one segment still reproduces the source. On
# 2026-09-21 one long authoring session drifted from ~2.6 to ~10 characters per segment
# over a few hours, batch by batch, and merge accepted all of it (Data_mail, Notes,
# MovieReactions...). That was the Japanese data. Long names and set phrases are legitimate
# (德米特里厄斯, 一模一样), so this only warns. The Japanese tables sat at 0-3% of segments
# over the limit when split by word and 25-60% when split by clause.
# Tuned on the zh pilot (81 entries across every table kind, 2026-09-28): 1.5 hanzi per
# segment, and every segment of 4+ was a name or a chengyu. Over 3 hanzi, that data is at
# 1.1%, the same data with pairs of segments merged at 19% and split by clause at 70%. Over
# 4, the pairs-merged drift scored 4% and would never have warned.
LONG_SEGMENT = 3          # hanzi, markup and punctuation not counted
LONG_SHARE_ALERT = 0.10   # share of long segments at which a batch/table looks clause-split


def segment_size(text):
    return len(py.HAN.findall(MARKUP.sub("", text)))


def size_report(label, pairs, listing=0):
    """Warns about long segments. pairs: (key, segment text). Prints nothing for a
    clean set; returns True if the share crosses LONG_SHARE_ALERT."""
    sizes = [(k, t, segment_size(t)) for k, t in pairs if segment_size(t)]
    if not sizes:
        return False
    long = [(k, t, n) for k, t, n in sizes if n > LONG_SEGMENT]
    if not long:
        return False
    share = len(long) / len(sizes)
    avg = sum(n for _, _, n in sizes) / len(sizes)
    alert = share > LONG_SHARE_ALERT
    print(f"  {'WARNING' if alert else 'note'}: {label}: {len(long)} of {len(sizes)} segments "
          f"({share:.0%}) over {LONG_SEGMENT} hanzi, avg {avg:.1f} per segment"
          + (" -- this looks split by clause, not by word; particles belong in their own segments"
             if alert else ""), file=sys.stderr)
    for k, t, n in sorted(long, key=lambda x: -x[2])[:listing if alert else 0]:
        print(f"    {n:3}  {k}: {t.strip()!r}", file=sys.stderr)
    return alert


def pinyin_of(text):
    """The reading of a segment with no hanzi: the latin and digits it keeps, as they are,
    minus punctuation and markup (Joja, 2.0). Empty for markup and symbols."""
    rest = MARKUP.sub("", re.sub(r"\{\d+\}", "", text))  # a {0} token is substituted, not read
    kept = "".join(c if (c.isalnum() or c in ".,'-" or c.isspace()) else " " for c in rest)
    return " ".join(w.strip(".,'-") for w in kept.split() if w.strip(".,'-"))


def cmd_merge(args):
    table, path = args[0], args[1]
    zh, _ = source(table)
    existing = authored(table)
    added = rejected = 0
    merged = []  # (key, segment text) of this batch, for the size check
    with open(path, encoding="utf-8") as f:
        for lineno, line in enumerate(f, 1):
            if not line.strip() or line.startswith("#"):
                continue
            try:
                key, english, segs = parse_line(line)
            except ValueError as ex:
                print(f"  line {lineno}: {ex}", file=sys.stderr); rejected += 1; continue
            if key not in zh:
                print(f"  line {lineno}: no such key {key!r} in {table}", file=sys.stderr); rejected += 1; continue
            try:
                segs = align(zh[key], segs)
            except ValueError as ex:
                print(f"  line {lineno}: {key}: {ex}\n    source: {zh[key]!r}", file=sys.stderr)
                rejected += 1; continue
            for seg in segs:
                if not seg["pinyin"] and not py.HAN.search(seg["text"]):
                    seg["pinyin"] = pinyin_of(seg["text"])
                for note in py.disagreements(seg["text"], seg["pinyin"]):
                    print(f"  note: line {lineno}: {key}: {note}", file=sys.stderr)
            joined = "".join(s["text"] for s in segs)
            if joined != zh[key]:  # align() guarantees this; kept as the last line of defence
                print(f"  line {lineno}: {key}: segments don't reproduce source\n"
                      f"    source: {zh[key]!r}\n    joined: {joined!r}", file=sys.stderr)
                rejected += 1; continue
            existing[key] = {"chinese": zh[key], "english": english, "segments": segs}
            merged += [(key, s["text"]) for s in segs]
            added += 1
    write_table(table, existing)
    print(f"{table}: merged {added}, rejected {rejected}, total authored {len(existing)}, pending {len(pending(table))}")
    size_report(f"this batch ({path})", merged, listing=15)  # warns only; never fails a merge
    return 1 if rejected else 0


def write_table(table, entries):
    os.makedirs(OUT, exist_ok=True)
    zh, _ = source(table)
    order = {k: i for i, k in enumerate(zh)}
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
    coarse = []  # tables whose segment sizes look clause-split; reported, not failed
    for name in sorted(os.listdir(OUT)) if os.path.isdir(OUT) else []:
        if not name.endswith(".json"):
            continue
        table = name[:-5]
        if table not in DATA_TABLES and "-" not in table:
            table = table.replace("_Description", "").replace("_Name", "")
        zh, _ = source(table)
        with open(os.path.join(OUT, name), encoding="utf-8") as f:
            doc = json.load(f)
        n = 0
        pairs = [(k, s.get("text", "")) for k, e in doc.items() if isinstance(e, dict)
                 for s in e.get("segments", [])]
        for key, entry in doc.items():
            if key == "_comment" or not isinstance(entry, dict):
                continue
            n += 1
            joined = "".join(s.get("text", "") for s in entry.get("segments", []))
            if joined != entry.get("chinese"):
                print(f"  {name}:{key}: segments != chinese"); bad += 1
            elif key in zh and zh[key] != entry["chinese"]:
                print(f"  {name}:{key}: chinese != game string"); bad += 1
            for seg in entry.get("segments", []):
                for problem in py.check(seg.get("text", ""), seg.get("pinyin", "")):
                    print(f"  {name}:{key}: {problem}"); bad += 1
            if not entry.get("english"):
                print(f"  {name}:{key}: missing english"); bad += 1
        print(f"{name}: {n} entries {'OK' if bad == 0 else ''}")
        if size_report(name, pairs, listing=5 if "-v" in args else 0):
            coarse.append(name)
    print("FAILED" if bad else "all segment data valid")
    if coarse:
        print(f"{len(coarse)} table(s) look split by clause rather than by word (warning only; "
              f"`validate -v` lists examples): {', '.join(coarse)}", file=sys.stderr)
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

# Every localized asset carries a .zh-CN variant, textures included. These are
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
    for name in sorted(os.listdir(SRC)):
        if name.endswith(".json"):
            out["Strings/" + name[:-5]] = name[:-5]
    for table, spec in DATA_TABLES.items():
        out["Data/" + spec["asset"]] = table
    for table, (asset, _) in content_tables().items():
        out[asset] = table
    return out


def localized_assets(content):
    """Every asset the game ships a Chinese variant of, as paths under Content/."""
    found = []
    for dirpath, _, filenames in os.walk(content):
        for name in filenames:
            if name.endswith(".zh-CN.xnb"):
                rel = os.path.relpath(os.path.join(dirpath, name), content)
                found.append(rel.replace(os.sep, "/")[: -len(".zh-CN.xnb")])
    return sorted(found)


def classify(asset, covered):
    if asset in covered:
        return "covered", covered[asset]
    for table in (EXCLUDED, KNOWN_GAPS):
        for prefix, reason in table.items():
            if asset == prefix or (prefix.endswith("/") and asset.startswith(prefix)):
                return ("excluded" if table is EXCLUDED else "gap"), (prefix, reason)
    return "unclassified", ("", "")


def raw_text(table):
    """A table's Chinese exactly as extracted, before any field selection or script expansion."""
    if table in DATA_TABLES:
        path = os.path.join(DATA_SRC, DATA_TABLES[table]["asset"] + ".json")
    elif table in content_tables():
        path = os.path.join(CONTENT_SRC, content_tables()[table][0] + ".json")
    else:
        path = os.path.join(SRC, table + ".json")
    with open(path, encoding="utf-8") as f:
        return json.load(f)["entries"]


def text_gaps():
    """(table, key, text) for Chinese that no authored entry covers.

    Compares character counts per table between the raw asset and what source() hands to
    authoring, then checks that everything source() hands over is authored or deliberately
    skipped."""
    zh_char = py.HAN
    gaps = []
    for table in tables():
        zh, _ = source(table)
        done, skip = authored(table), skipped(table)
        raw = sum(len(zh_char.findall(v)) for v in raw_text(table).values())
        # skipped scripts appear in source() both whole and as extracted lines; count them once
        offered = sum(len(zh_char.findall(v)) for k, v in zh.items()
                      if not (k in skip and is_script(v)))
        if offered < raw:
            gaps.append((table, "(extraction)", f"{raw - offered} Chinese chars never offered for authoring"))
        for key, text in zh.items():
            if key not in done and key not in skip and zh_char.search(text):
                gaps.append((table, key, text))
    return gaps


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

    # asset coverage isn't text coverage: a Data record field nobody listed in DATA_TABLES
    # (Quests' completion line) or the dialogue inside a skipped event script is text in a
    # "covered" asset that no entry holds. Every Chinese character of every covered table must
    # sit in an authored entry, or in a skipped script whose spoken lines were extracted.
    uncovered = text_gaps()
    if uncovered:
        print(f"\nFAILED -- Chinese text in covered assets that no entry holds ({len(uncovered)}):")
        for table, key, text in uncovered[:40]:
            print(f"  {table} {key}: {text[:60]!r}")
        print("\nExtend the table's extraction (DATA_TABLES fields, script expansion), author it,\n"
              "or skip it with a reason.")
        return 1
    print("text: every Chinese character in covered assets is held by an entry")

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
