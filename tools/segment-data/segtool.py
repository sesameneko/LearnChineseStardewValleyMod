#!/usr/bin/env python3
"""Work pipeline for hand-authored Japanese word segmentation (see Plan.md's data task).

Sub-commands:
  status                    coverage per string table
  batch <Table> [n] [--offset k]
                            print the next n un-authored entries as an authoring worklist
  merge <Table> <file.tsv>  fold an authored batch into assets/segments/ja/<Table>.json
  skip  <Table> <file.txt>  record keys deliberately left unsegmented
  validate                  re-check every bundled segment file

Authoring format (TSV, one entry per line, no header):
  key <TAB> english <TAB> text¦reading¦gloss‖text¦reading¦gloss‖...
The concatenated segment texts must reproduce the source string exactly; merge
refuses any line that doesn't, so bad data never reaches the mod.
"""
import json, os, sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
JA = os.path.join(ROOT, "tools", "extracted-strings", "ja")
EN = os.path.join(ROOT, "tools", "extracted-strings", "en")
# tracked source of truth; assets/segments/ja is generated from it by the
# csproj's CopySegmentData target and is gitignored
OUT = os.path.join(ROOT, "tools", "extracted-strings", "literal-translations")
SKIPS = os.path.join(ROOT, "tools", "segment-data", "skipped")

FIELD, SEG = "¦", "‖"

COMMENT = ("Word/phrase-level breakdown of Stardew Valley {table}.xnb strings, for in-context "
           "mouse-over translation of individual pieces of a sentence (not a general Japanese-"
           "English dictionary -- glosses are chosen for how each word/phrase functions in THIS "
           "specific sentence). Each entry: \"japanese\" (exact source text), \"english\" (a "
           "natural-ish literal translation, for context), and \"segments\" -- an ordered array of "
           "{{text, reading, gloss}} whose \"text\" fields concatenate back to exactly reproduce "
           "\"japanese\" (validated by tools/segment-data/segtool.py).")


def source(table):
    with open(os.path.join(JA, table + ".json"), encoding="utf-8") as f:
        ja = json.load(f)["entries"]
    try:
        with open(os.path.join(EN, table + ".json"), encoding="utf-8") as f:
            en = json.load(f)["entries"]
    except FileNotFoundError:
        en = {}
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
    return sorted(f[:-5] for f in os.listdir(JA) if f.endswith(".json"))


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
    return text.replace("\\", "\\\\").replace("\n", "\\n").replace("\t", "\\t")


def unesc(text):
    out, i = [], 0
    while i < len(text):
        if text[i] == "\\" and i + 1 < len(text):
            out.append({"n": "\n", "t": "\t", "\\": "\\"}.get(text[i + 1], text[i + 1]))
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
    for chunk in segs.split(SEG):
        if not chunk:
            continue
        bits = chunk.split(FIELD)
        if len(bits) != 3:
            raise ValueError(f"segment {chunk!r} needs text{FIELD}reading{FIELD}gloss")
        out.append({"text": unesc(bits[0]), "reading": bits[1].strip(), "gloss": bits[2].strip()})
    return key, english, out


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
            joined = "".join(s["text"] for s in segs)
            if joined != ja[key]:
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
        table = name[:-5].replace("_Description", "").replace("_Name", "")
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


CMDS = {"status": cmd_status, "batch": cmd_batch, "merge": cmd_merge, "skip": cmd_skip, "validate": cmd_validate}

if __name__ == "__main__":
    if len(sys.argv) < 2 or sys.argv[1] not in CMDS:
        print(__doc__); sys.exit(2)
    sys.exit(CMDS[sys.argv[1]](sys.argv[2:]) or 0)
