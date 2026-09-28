#!/usr/bin/env python3
"""Pinyin normalisation and checking for segment readings (see README.md, "Readings").

A segment's "pinyin" is written one syllable per hanzi, space-separated, lowercase, with tone
marks and no mark for the neutral tone: 牧场 -> "mù chǎng", 东西 -> "dōng xi". Tone numbers
("mu4 chang3", "dong1 xi5" or "dong1 xi") and v / u: for ü are accepted on input and converted.

Every syllable is checked against the readings pypinyin lists for its own character, so a
syllable that no dictionary gives that hanzi is refused. Two allowances:
  - the neutral tone is accepted on any character (the second syllable of many words: 东西
    dōng xi, 衣服 yī fu), since dictionaries list it unevenly;
  - 一 and 不 take whatever tone sandhi gives them (yí gè, bú shì), which the convention
    writes as spoken.
儿 read as an erhua suffix is written "r" (一点儿 -> "yì diǎn r").

    python3 tools/segment-data/pinyin.py check 牧场 "mu4 chang3"
"""
import re, sys, unicodedata

try:
    from pypinyin import pinyin as _lookup, Style
    from pypinyin.pinyin_dict import pinyin_dict as _pinyin_dict
except ImportError:  # pragma: no cover
    sys.exit("segment-data needs pypinyin: pip3 install --user pypinyin")

HAN = re.compile(r"[㐀-䶿一-鿿豈-﫿〇]")

# tone mark -> (plain vowel, tone number)
_MARKS = {}
for plain, marked in {"a": "āáǎà", "e": "ēéěè", "i": "īíǐì", "o": "ōóǒò", "u": "ūúǔù", "ü": "ǖǘǚǜ"}.items():
    for n, c in enumerate(marked, 1):
        _MARKS[c] = (plain, n)
_MARK_OF = {(p, n): c for c, (p, n) in _MARKS.items()}

SANDHI_FREE = {"一", "不"}


def split_tone(syllable):
    """'chǎng' -> ('chang', 3); 'xi' -> ('xi', 5). Raises ValueError on two tone marks."""
    base, tone = [], 5
    for c in syllable:
        if c in _MARKS:
            if tone != 5:
                raise ValueError(f"{syllable!r} has two tone marks")
            plain, tone = _MARKS[c]
            base.append(plain)
        else:
            base.append(c)
    return "".join(base), tone


def mark(base, tone):
    """('chang', 3) -> 'chǎng', placing the mark by the standard rule: a or e if present,
    o in ou, otherwise the last vowel."""
    if tone == 5:
        return base
    for target in ("a", "e"):
        if target in base:
            i = base.index(target)
            break
    else:
        if "ou" in base:
            i = base.index("o")
        else:
            i = max(base.rfind(v) for v in "iouü")
            if i < 0:
                return base  # no vowel: m, n, ng, r
    return base[:i] + _MARK_OF[(base[i], tone)] + base[i + 1:]


def _syllable_inventory():
    out = {"r"}
    for value in _pinyin_dict.values():
        for reading in value.split(","):
            out.add(split_tone(unicodedata.normalize("NFC", reading))[0])
    return out


INVENTORY = _syllable_inventory()


def normalize(pinyin, text=""):
    """Author input -> stored form: lowercase, NFC, tone numbers turned into marks, v / u:
    into ü, whitespace collapsed. A token copied verbatim from the segment's text (Joja) is
    kept as it is. Doesn't validate; see check()."""
    out = []
    for token in unicodedata.normalize("NFC", pinyin).split():
        if token in text:
            out.append(token)
            continue
        token = token.lower().replace("u:", "ü").replace("v", "ü")
        m = re.fullmatch(r"([a-zü]+)([1-5])", token)
        out.append(mark(m.group(1), int(m.group(2))) if m else token)
    return " ".join(out)


def is_syllable(token):
    try:
        base, _ = split_tone(token)
    except ValueError:
        return False
    return base in INVENTORY


def readings(char):
    """(base, tone) pairs a dictionary gives one hanzi."""
    found = set()
    for reading in _lookup(char, style=Style.TONE, heteronym=True)[0]:
        found.add(split_tone(unicodedata.normalize("NFC", reading)))
    return found


def check(text, pinyin):
    """Problems with a segment's pinyin, as a list of messages (empty when fine).

    Tokens that aren't pinyin syllables must be copied from the text as-is (Joja, 2.0), per
    the conventions. When every hoverable character of the text is a hanzi, the syllables
    must match them one for one, and each must be a reading of its character."""
    problems = []
    hanzi = HAN.findall(text)
    tokens = pinyin.split()
    if hanzi and not tokens:
        return [f"{text!r} has hanzi but no pinyin"]
    if HAN.search(pinyin):
        return [f"pinyin {pinyin!r} contains hanzi"]

    syllables = []
    for token in tokens:
        if is_syllable(token):
            syllables.append(token)
        elif token not in text:
            problems.append(f"{token!r} is neither a pinyin syllable nor text copied from {text!r}")
    if problems:
        return problems

    # other letters or digits in the text (a number read aloud, a latin name) mean the
    # syllables can't be paired with hanzi one for one; only their validity is checked
    other = [c for c in HAN.sub("", text) if c.isalnum()]
    if other:
        return []
    if len(syllables) != len(hanzi):
        return [f"{text!r}: {len(hanzi)} hanzi but {len(syllables)} syllables in {pinyin!r}"]

    for char, syllable in zip(hanzi, syllables):
        base, tone = split_tone(syllable)
        if base == "r" and char == "儿":
            continue
        known = readings(char)
        bases = {b for b, _ in known}
        if base not in bases:
            options = "/".join(sorted(mark(b, t) for b, t in known))
            problems.append(f"{char} read {syllable!r}, but its readings are {options}")
        elif tone != 5 and char not in SANDHI_FREE and (base, tone) not in known:
            options = "/".join(sorted(mark(b, t) for b, t in known if b == base))
            problems.append(f"{char} read {syllable!r}: wrong tone, expected {options}")
    return problems


def disagreements(text, pinyin):
    """Syllables that differ from pypinyin's reading of the segment in context, as messages.

    Warn-only: check() accepts any dictionary reading of a character, so 好 read hào in 你好
    passes it. pypinyin's phrase dictionary knows that 你好 is nǐ hǎo, but it is wrong often
    enough on polyphones that its reading can't be a rule. Neutral tones and 一/不 sandhi,
    which it writes differently from the convention, aren't reported."""
    hanzi = HAN.findall(text)
    syllables = [t for t in pinyin.split() if is_syllable(t)]
    # a lone character gets pypinyin's most common reading with no context, which is noise
    # for exactly the characters worth checking (地 de, 得 de, 着 zhe)
    if len(hanzi) != len(syllables) or len(hanzi) < 2:
        return []
    guess = [unicodedata.normalize("NFC", r[0]) for r in _lookup("".join(hanzi), style=Style.TONE)]
    out = []
    for char, mine, theirs in zip(hanzi, syllables, guess):
        (base, tone), (their_base, their_tone) = split_tone(mine), split_tone(theirs)
        if base == "r" and char == "儿":
            continue
        if base != their_base or (tone != their_tone and 5 not in (tone, their_tone) and char not in SANDHI_FREE):
            out.append(f"{char} read {mine!r}, pypinyin reads {theirs!r} in {''.join(hanzi)!r}")
    return out


if __name__ == "__main__":
    if len(sys.argv) == 4 and sys.argv[1] == "check":
        stored = normalize(sys.argv[3], sys.argv[2])
        problems = check(sys.argv[2], stored)
        print(stored)
        print("\n".join(problems) if problems else "OK")
        for note in disagreements(sys.argv[2], stored):
            print("note: " + note)
        sys.exit(1 if problems else 0)
    print(__doc__)
    sys.exit(2)
