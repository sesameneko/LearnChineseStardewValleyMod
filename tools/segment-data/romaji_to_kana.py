#!/usr/bin/env python3
"""Convert the hand-authored Hepburn readings into kana, once.

Kana is the lossless form: kana -> romaji is deterministic, romaji -> kana is not,
so the data should hold kana and generate romaji from it. That makes a romaji/kana
toggle a view over one dataset rather than two datasets to keep in step.

What is and isn't recoverable from romaji:
  - 23% of readings need no conversion at all: the segment's own text is already kana.
  - 57% parse mechanically -- Hepburn is unambiguous once the apostrophe convention
    (kin'youbi) is followed, which this data does.
  - 20% contain a long vowel, which is genuinely lossy: "ō" is おう in gakkō but おお in
    tōri, and romaji has thrown that away. Those are converted with the common reading
    and written to a review file, since a wrong guess is invisible in game.

Usage:
  romaji_to_kana.py report            what would change, and what needs review
  romaji_to_kana.py write             add a "kana" field to every segment
  romaji_to_kana.py check "gakkō"     convert one reading, for spot checks
"""

import json
import os
import re
import sys
import glob

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DATA = os.path.join(ROOT, "tools", "extracted-strings", "literal-translations")
REVIEW = os.path.join(os.path.dirname(os.path.abspath(__file__)), "kana-review.tsv")

LONG = "\x01"  # marks "the preceding vowel was written with a macron"

MACRONS = {"ā": "a" + LONG, "ī": "i" + LONG, "ū": "u" + LONG, "ē": "e" + LONG, "ō": "o" + LONG}

# stray accents from authoring (shimeriké), which are not a romanisation of anything
ACCENTS = {"é": "e", "è": "e", "ê": "e", "á": "a", "à": "a", "â": "a",
           "í": "i", "ì": "i", "î": "i", "ó": "o", "ò": "o", "ô": "o",
           "ú": "u", "ù": "u", "û": "u", "ñ": "n", "ç": "c"}

# Hepburn -> hiragana, longest match first. Katakana is derived by codepoint shift.
TABLE = {
    "kya": "きゃ", "kyu": "きゅ", "kyo": "きょ", "gya": "ぎゃ", "gyu": "ぎゅ", "gyo": "ぎょ",
    "sha": "しゃ", "shu": "しゅ", "sho": "しょ", "shi": "し", "she": "しぇ",
    "ja": "じゃ", "ju": "じゅ", "jo": "じょ", "je": "じぇ", "ji": "じ",
    "cha": "ちゃ", "chu": "ちゅ", "cho": "ちょ", "che": "ちぇ", "chi": "ち",
    "tsu": "つ", "tso": "つぉ",
    "nya": "にゃ", "nyu": "にゅ", "nyo": "にょ",
    "hya": "ひゃ", "hyu": "ひゅ", "hyo": "ひょ",
    "bya": "びゃ", "byu": "びゅ", "byo": "びょ",
    "pya": "ぴゃ", "pyu": "ぴゅ", "pyo": "ぴょ",
    "mya": "みゃ", "myu": "みゅ", "myo": "みょ",
    "rya": "りゃ", "ryu": "りゅ", "ryo": "りょ",
    "fa": "ふぁ", "fi": "ふぃ", "fe": "ふぇ", "fo": "ふぉ", "fu": "ふ",
    "va": "ゔぁ", "vi": "ゔぃ", "vu": "ゔ", "ve": "ゔぇ", "vo": "ゔぉ",
    "ti": "てぃ", "di": "でぃ", "tu": "とぅ", "du": "どぅ",
    "dya": "でゃ", "dyu": "でゅ", "dyo": "でょ",
    "fya": "ふゃ", "fyu": "ふゅ", "fyo": "ふょ",
    "tsa": "つぁ", "tsi": "つぃ", "tse": "つぇ",
    "wi": "うぃ", "we": "うぇ", "wo": "を", "wa": "わ",
    "ka": "か", "ki": "き", "ku": "く", "ke": "け", "ko": "こ",
    "ga": "が", "gi": "ぎ", "gu": "ぐ", "ge": "げ", "go": "ご",
    "sa": "さ", "su": "す", "se": "せ", "so": "そ",
    "za": "ざ", "zu": "ず", "ze": "ぜ", "zo": "ぞ",
    "ta": "た", "te": "て", "to": "と",
    "da": "だ", "de": "で", "do": "ど",
    "na": "な", "ni": "に", "nu": "ぬ", "ne": "ね", "no": "の",
    "ha": "は", "hi": "ひ", "he": "へ", "ho": "ほ",
    "ba": "ば", "bi": "び", "bu": "ぶ", "be": "べ", "bo": "ぼ",
    "pa": "ぱ", "pi": "ぴ", "pu": "ぷ", "pe": "ぺ", "po": "ぽ",
    "ma": "ま", "mi": "み", "mu": "む", "me": "め", "mo": "も",
    "ya": "や", "yu": "ゆ", "yo": "よ",
    "ra": "ら", "ri": "り", "ru": "る", "re": "れ", "ro": "ろ",
    "a": "あ", "i": "い", "u": "う", "e": "え", "o": "お",
    "n": "ん",
}
KEYS = sorted(TABLE, key=len, reverse=True)

# Hiragana long vowels: the second kana that spells the length. おう is far more common
# than おお, えい than ええ, so those are the defaults and every one is flagged for review.
LONG_HIRA = {"a": "あ", "i": "い", "u": "う", "e": "い", "o": "う"}

KATAKANA = re.compile(r"[゠-ヿ]")
KANA_ONLY = re.compile(r"^[぀-ヿㇰ-ㇿｦ-ﾝー\s]+$")

# what may be sounded out: lowercase latin, the ん apostrophe, macrons and stray accents
ROMAJI_TOKEN = re.compile(r"^[A-Za-z'\-āīūēōĀĪŪĒŌéèêáàâíìîóòôúùûñç]+$")

# leading/trailing punctuation that isn't part of the reading ("shi...", "(ni)")
EDGE_PUNCT = re.compile(r"^(\W*)(.*?)(\W*)$", re.DOTALL)

# Particles are written as they sound, not as they're spelled: は reads "wa", へ "e", を "o".
# A standalone one of these inside a phrase is the particle -- a segment that *is* just the
# particle has pure-kana text and never reaches conversion.
PARTICLES = {"wa": "は", "e": "へ", "o": "を"}


def to_katakana(hira):
    out = []
    for ch in hira:
        out.append(chr(ord(ch) + 0x60) if "ぁ" <= ch <= "ゖ" else ch)
    return "".join(out)


def convert_word(romaji, katakana):  # noqa: D401
    """Returns (kana, flagged) for one whitespace-free reading."""
    s = romaji.lower()
    for macron, plain in MACRONS.items():
        s = s.replace(macron, plain)
    for accent, plain in ACCENTS.items():
        s = s.replace(accent, plain)

    out = []
    flagged = False
    i = 0
    while i < len(s):
        c = s[i]

        if c == "'":            # the ん-before-vowel marker, already consumed by "n"
            i += 1
            continue

        if c == LONG:           # handled when the vowel was emitted
            i += 1
            continue

        # sokuon: a doubled consonant is っ plus the syllable
        if (c not in "aiueon'" + LONG and i + 1 < len(s) and s[i + 1] == c):
            out.append("っ")
            i += 1
            continue
        if s.startswith("tch", i):
            out.append("っ")
            i += 1
            continue

        # ん: "n" not starting a な-row syllable, or written "n'"
        if c == "n" and (i + 1 >= len(s) or s[i + 1] not in "aiueoy" or s[i + 1 : i + 2] == "'"):
            out.append("ん")
            i += 1
            continue
        if c == "n" and i + 1 < len(s) and s[i + 1] == "'":
            out.append("ん")
            i += 2
            continue

        # a consonant with no vowel after it, at the end of the word: a cut-off ヒャッ
        if c not in "aiueon" and i + 1 >= len(s):
            out.append("っ")
            i += 1
            continue

        for key in KEYS:
            if s.startswith(key, i):
                out.append(TABLE[key])
                i += len(key)
                # a macron on that syllable's vowel spells the length out
                if i < len(s) and s[i] == LONG:
                    vowel = key[-1]
                    if katakana:
                        out.append("ー")
                    else:
                        out.append(LONG_HIRA.get(vowel, vowel))
                        flagged = True
                    i += 1
                break
        else:
            out.append(s[i])    # unmapped: pass through so it's visible in review
            flagged = True
            i += 1

    kana = "".join(out)
    return (to_katakana(kana) if katakana else kana), flagged


def convert(reading, text):
    """Returns (kana, flagged, source) for one segment's reading."""
    if text and KANA_ONLY.match(text):
        return text, False, "text"      # the segment is already kana; no guessing needed

    katakana = bool(text and KATAKANA.search(text))
    parts, flags = [], False
    for token in re.split(r"(\s+)", reading):
        if not token or token.isspace():
            parts.append(token)
            continue

        # only romaji is sounded out. A token with an uppercase letter is a name or acronym the
        # source keeps in latin (Qi, ID, IP), and one with digits or punctuation is a version or a
        # number ("2.0") -- transliterating those produced "qイ" and "2.っ".
        lead, core, trail = EDGE_PUNCT.match(token).groups()
        if not core or not ROMAJI_TOKEN.match(core):
            parts.append(token)
            continue

        # a capitalised token is either latin the source text itself keeps (Joja社, ミスターQi) or
        # the romaji of a katakana name (Gotoro -> ゴトロ). The text says which: if it contains the
        # token verbatim, it's latin on screen and stays latin.
        if any("A" <= ch <= "Z" for ch in core):
            if core in (text or ""):
                parts.append(token)
                continue
            kana, flagged = convert_word(core.replace("-", ""), katakana=True)
            parts.append(lead + kana + trail)
            flags = flags or flagged
            continue

        if core in PARTICLES:
            parts.append(lead + PARTICLES[core] + trail)
            flags = True  # the particle reading is an assumption worth eyeballing
            continue

        # the hyphen in an honorific (go-shinsetsu) or a suffix (tanken-ka) separates romaji
        # for readability; kana runs them together
        kana, flagged = convert_word(core.replace("-", ""), katakana)
        parts.append(lead + kana + trail)
        flags = flags or flagged
        continue
    return "".join(parts), flags, "katakana" if katakana else "hiragana"


def each_segment():
    for path in sorted(glob.glob(os.path.join(DATA, "*.json"))):
        doc = json.load(open(path, encoding="utf-8"))
        for key, entry in doc.items():
            if not isinstance(entry, dict):
                continue
            for seg in entry.get("segments", []):
                yield path, doc, key, entry, seg


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else "report"

    if mode == "check":
        for arg in sys.argv[2:]:
            print(arg, "->", convert(arg, "")[0])
        return

    stats = {"text": 0, "hiragana": 0, "katakana": 0, "none": 0}
    flagged = []
    docs = {}

    for path, doc, key, entry, seg in each_segment():
        docs[path] = doc
        reading = (seg.get("reading") or "").strip()
        if not reading:
            stats["none"] += 1
            continue
        kana, flag, source = convert(reading, seg.get("text", ""))
        stats[source] += 1
        if flag:
            flagged.append((key, seg.get("text", ""), reading, kana))
        if mode == "write":
            seg["kana"] = kana

    total = sum(stats.values())
    print(f"segments: {total}")
    print(f"  kana taken from the segment text: {stats['text']}")
    print(f"  converted as hiragana:            {stats['hiragana']}")
    print(f"  converted as katakana:            {stats['katakana']}")
    print(f"  no reading:                       {stats['none']}")
    print(f"  flagged for review:               {len(flagged)}")

    with open(REVIEW, "w", encoding="utf-8") as fh:
        fh.write("key\ttext\treading\tkana\n")
        for row in flagged:
            fh.write("\t".join(row) + "\n")
    print(f"review list -> {os.path.relpath(REVIEW, ROOT)}")

    if mode == "write":
        for path, doc in docs.items():
            with open(path, "w", encoding="utf-8") as fh:
                json.dump(doc, fh, ensure_ascii=False, indent=2)
                fh.write("\n")
        print(f"wrote {len(docs)} file(s)")


if __name__ == "__main__":
    main()
