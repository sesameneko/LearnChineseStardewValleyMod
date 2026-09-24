#!/usr/bin/env python3
"""Generate each segment's Hepburn "reading" from its authored "kana".

Kana is the source of truth (see README.md): kana -> romaji is deterministic where romaji ->
kana is not, so segments are authored with kana and the romaji is derived here. The one thing
kana can't say is where a morpheme boundary splits a vowel pair -- 思う is omou, not omō -- so
the few common words where that happens are listed in NOT_LONG.

Conventions the kana field follows (and this script relies on):
  - words separated by single spaces ("かんがえこんで しまう")
  - particles written as spelled: は / へ / を; a standalone one reads wa / e / o
  - katakana words stay katakana; ー lengthens the vowel before it
  - latin and digits the on-screen text keeps (Joja, 2.0) pass through unchanged

Usage:
  kana_to_romaji.py check "かな" ...    convert readings, for spot checks
  kana_to_romaji.py report              compare against readings already in the data
  kana_to_romaji.py write [--overwrite] fill in "reading" where it's missing
                                        (--overwrite regenerates every reading from kana)
"""

import glob
import json
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DATA = os.path.join(ROOT, "tools", "extracted-strings", "literal-translations")

# hiragana; katakana is folded onto it by codepoint shift first. Two-kana combinations are
# tried before single kana.
PAIRS = {
    "きゃ": "kya", "きゅ": "kyu", "きょ": "kyo", "ぎゃ": "gya", "ぎゅ": "gyu", "ぎょ": "gyo",
    "しゃ": "sha", "しゅ": "shu", "しょ": "sho", "しぇ": "she",
    "じゃ": "ja", "じゅ": "ju", "じょ": "jo", "じぇ": "je",
    "ちゃ": "cha", "ちゅ": "chu", "ちょ": "cho", "ちぇ": "che",
    "にゃ": "nya", "にゅ": "nyu", "にょ": "nyo", "ひゃ": "hya", "ひゅ": "hyu", "ひょ": "hyo",
    "びゃ": "bya", "びゅ": "byu", "びょ": "byo", "ぴゃ": "pya", "ぴゅ": "pyu", "ぴょ": "pyo",
    "みゃ": "mya", "みゅ": "myu", "みょ": "myo", "りゃ": "rya", "りゅ": "ryu", "りょ": "ryo",
    "ふぁ": "fa", "ふぃ": "fi", "ふぇ": "fe", "ふぉ": "fo", "ふゅ": "fyu",
    "ゔぁ": "va", "ゔぃ": "vi", "ゔぇ": "ve", "ゔぉ": "vo",
    "てぃ": "ti", "でぃ": "di", "とぅ": "tu", "どぅ": "du", "でゅ": "dyu",
    "つぁ": "tsa", "つぃ": "tsi", "つぇ": "tse", "つぉ": "tso",
    "うぃ": "wi", "うぇ": "we", "うぉ": "wo", "いぇ": "ye",
}
SINGLE = {
    "あ": "a", "い": "i", "う": "u", "え": "e", "お": "o",
    "か": "ka", "き": "ki", "く": "ku", "け": "ke", "こ": "ko",
    "が": "ga", "ぎ": "gi", "ぐ": "gu", "げ": "ge", "ご": "go",
    "さ": "sa", "し": "shi", "す": "su", "せ": "se", "そ": "so",
    "ざ": "za", "じ": "ji", "ず": "zu", "ぜ": "ze", "ぞ": "zo",
    "た": "ta", "ち": "chi", "つ": "tsu", "て": "te", "と": "to",
    "だ": "da", "ぢ": "ji", "づ": "zu", "で": "de", "ど": "do",
    "な": "na", "に": "ni", "ぬ": "nu", "ね": "ne", "の": "no",
    "は": "ha", "ひ": "hi", "ふ": "fu", "へ": "he", "ほ": "ho",
    "ば": "ba", "び": "bi", "ぶ": "bu", "べ": "be", "ぼ": "bo",
    "ぱ": "pa", "ぴ": "pi", "ぷ": "pu", "ぺ": "pe", "ぽ": "po",
    "ま": "ma", "み": "mi", "む": "mu", "め": "me", "も": "mo",
    "や": "ya", "ゆ": "yu", "よ": "yo",
    "ら": "ra", "り": "ri", "る": "ru", "れ": "re", "ろ": "ro",
    "わ": "wa", "ゐ": "i", "ゑ": "e", "を": "o", "ん": "n", "ゔ": "vu",
    # small vowels standing alone (ぁ in "あぁ", ぇ in "へぇ") just extend the sound
    "ぁ": "a", "ぃ": "i", "ぅ": "u", "ぇ": "e", "ぉ": "o", "ゃ": "ya", "ゅ": "yu", "ょ": "yo", "ゎ": "wa",
}
PARTICLES = {"は": "wa", "へ": "e", "を": "o"}
MACRON = {"a": "ā", "i": "ī", "u": "ū", "e": "ē", "o": "ō"}

# vowel pairs Hepburn writes as one long vowel. えい and いい stay as written (sensei, ii).
LONG_PAIRS = {("a", "a"), ("u", "u"), ("e", "e"), ("o", "o"), ("o", "u")}

# words where おう / うう straddle a morpheme boundary, so it is two sounds, not one long one
NOT_LONG = {"おもう", "かよう", "まよう", "すくう", "くう", "ぬう"}


def to_hiragana(text):
    return "".join(chr(ord(c) - 0x60) if "ァ" <= c <= "ヶ" else c for c in text)


def syllables(word):
    """Splits a kana word into (kana, romaji) syllables; non-kana characters pass through."""
    out, i = [], 0
    while i < len(word):
        pair = word[i:i + 2]
        if pair in PAIRS:
            out.append((pair, PAIRS[pair]))
            i += 2
        else:
            out.append((word[i], SINGLE.get(word[i], word[i])))
            i += 1
    return out


def convert_word(word):
    if word in PARTICLES:
        return PARTICLES[word]

    hira = to_hiragana(word)
    long_ok = hira not in NOT_LONG
    out = []          # romaji pieces, one per syllable
    geminate = False  # a pending っ

    for kana, roma in syllables(hira):
        if kana in ("っ", "ッ"):
            geminate = True
            continue

        if kana == "ー":
            if out and out[-1] and out[-1][-1] in MACRON:
                out[-1] = out[-1][:-1] + MACRON[out[-1][-1]]
            continue

        prev = out[-1] if out else ""
        # ん before a vowel or y is written n' (kin'yōbi)
        if prev.endswith("n") and prev == "n" and roma[:1] in "aiueoy" and roma:
            out[-1] = "n'"

        # a vowel kana lengthening the previous syllable
        if (long_ok and roma in ("a", "u", "e", "o") and prev and prev[-1] in "aueo"
                and (prev[-1], roma) in LONG_PAIRS and kana in "あうえおぁぅぇぉ"):
            out[-1] = prev[:-1] + MACRON[prev[-1]]
            continue

        if geminate:
            geminate = False
            if roma.startswith("ch"):
                roma = "t" + roma
            elif roma[:1].isalpha() and roma[:1] not in "aiueon":
                roma = roma[0] + roma
        out.append(roma)

    # a trailing っ is a cut-off sound (ヒャッ); Hepburn has no letter for it
    return "".join(out)


def convert(kana):
    return " ".join(convert_word(w) for w in kana.split())


def each_segment():
    for path in sorted(glob.glob(os.path.join(DATA, "*.json"))):
        with open(path, encoding="utf-8") as f:
            doc = json.load(f)
        for key, entry in doc.items():
            if isinstance(entry, dict):
                for seg in entry.get("segments", []):
                    yield path, doc, key, seg


def normalize(romaji):
    return " ".join(romaji.lower().replace("-", " ").split())


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else "report"

    if mode == "check":
        for arg in sys.argv[2:]:
            print(arg, "->", convert(arg))
        return 0

    if mode == "report":
        # a round trip over data that already has both fields -- how close generated
        # romaji comes to hand-authored romaji
        same = differ = 0
        samples = []
        for path, _, key, seg in each_segment():
            if seg.get("kana") and seg.get("reading"):
                generated = convert(seg["kana"])
                if normalize(generated) == normalize(seg["reading"]):
                    same += 1
                else:
                    differ += 1
                    if len(samples) < 40:
                        samples.append(f"  {seg['text']!r}: kana={seg['kana']!r} authored={seg['reading']!r} generated={generated!r}")
        total = same + differ
        print(f"segments with both fields: {total}; generated == authored: {same} ({100 * same / max(total, 1):.1f}%)")
        print("\n".join(samples))
        return 0

    if mode == "write":
        overwrite = "--overwrite" in sys.argv
        docs, filled = {}, 0
        for path, doc, key, seg in each_segment():
            docs[path] = doc
            if seg.get("kana") and (overwrite or not seg.get("reading")):
                seg["reading"] = convert(seg["kana"])
                filled += 1
        for path, doc in docs.items():
            with open(path, "w", encoding="utf-8") as f:
                json.dump(doc, f, ensure_ascii=False, indent=2)
                f.write("\n")
        print(f"generated {filled} reading(s)")
        return 0

    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main())
