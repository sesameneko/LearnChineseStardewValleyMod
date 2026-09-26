using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>
    /// A pointer from a flashcard to the sentence its word was saved from, so the deck stores
    /// where a sentence is rather than a copy of it. Written as <c>table:key@offset+length</c>:
    /// segment file, entry key, and the word's span in that entry's <c>japanese</c> string.
    ///
    /// A character span rather than a segment index because re-splitting an entry's segments
    /// (planned -- see TODOs.md) shifts every index after the split, while the Japanese text comes
    /// from the game and doesn't move.
    /// </summary>
    public readonly record struct ContextRef(string Table, string Key, int Offset, int Length)
    {
        public static ContextRef From(SegmentSource source) => new(source.Table, source.Key, source.Offset, source.Length);

        /// <summary>The entry this points into, as <see cref="SourceEntries"/> keys it.</summary>
        public string EntryId => SourceEntries.IdOf(this.Table, this.Key);

        public override string ToString() => $"{this.Table}:{this.Key}@{this.Offset.ToString(CultureInfo.InvariantCulture)}+{this.Length.ToString(CultureInfo.InvariantCulture)}";

        /// <summary>
        /// Parses <see cref="ToString"/>'s form. The table is a file name and holds no ':', so it
        /// ends at the first one; a key can hold '+' or ':' (Saloon_Arcade_PK_NewGame+), so the span is
        /// read from the *last* '@'.
        /// </summary>
        public static bool TryParse(string? text, out ContextRef result)
        {
            result = default;
            if (string.IsNullOrEmpty(text))
                return false;

            int colon = text.IndexOf(':');
            int at = text.LastIndexOf('@');
            if (colon <= 0 || at <= colon + 1)
                return false;

            string span = text.Substring(at + 1);
            int plus = span.IndexOf('+');
            if (plus <= 0
                || !int.TryParse(span.Substring(0, plus), NumberStyles.None, CultureInfo.InvariantCulture, out int offset)
                || !int.TryParse(span.Substring(plus + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int length)
                || length <= 0)
                return false;

            result = new ContextRef(text.Substring(0, colon), text.Substring(colon + 1, at - colon - 1), offset, length);
            return true;
        }
    }

    /// <summary>One authored entry: its source text and the hand-written literal translation.</summary>
    public sealed record SourceEntry(string Japanese, string? English);

    /// <summary>
    /// Every authored entry by <c>table:key</c>, which is what a <see cref="ContextRef"/> resolves
    /// against. Separate from <see cref="SegmentIndex"/>, which is keyed by text and keeps only the
    /// first of several entries sharing a string.
    /// </summary>
    public sealed class SourceEntries
    {
        private readonly Dictionary<string, SourceEntry> entries = new(StringComparer.Ordinal);

        public int Count => this.entries.Count;

        public static string IdOf(string table, string key) => table + ":" + key;

        public void Add(string table, string key, string japanese, string? english)
        {
            this.entries[IdOf(table, key)] = new SourceEntry(japanese, english);
        }

        public bool TryGet(ContextRef context, out SourceEntry entry) => this.entries.TryGetValue(context.EntryId, out entry!);

        /// <summary>
        /// Resolves a pointer, checking it still says what the card says: the span must exist and
        /// hold <paramref name="word"/>. A game or data update can move or remove an entry, and a
        /// pointer that now lands on some other word must not be shown as this card's context.
        /// </summary>
        public bool TryResolve(ContextRef context, string word, out SourceEntry entry)
        {
            if (!this.TryGet(context, out entry))
                return false;

            if (context.Offset < 0 || context.Offset + context.Length > entry.Japanese.Length)
                return false;

            string span = entry.Japanese.Substring(context.Offset, context.Length);
            return span.Contains(word, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Turns a raw authored entry into what a flashcard shows as context: the page holding the word,
    /// with the game's dialogue markup taken out, and where the word sits in that cleaned text.
    ///
    /// Entries are stored raw, the way the game reads them -- a villager line is
    /// <c>ええ、そうよ。$h#$b#また来てね！</c>, and the game draws each <c>#$b#</c>-separated part as its
    /// own page, without the commands. A card shows the page the word was on, since the whole entry
    /// can run to several screens.
    /// </summary>
    public static class ContextText
    {
        /// <summary>The page breaks the game splits dialogue at.</summary>
        private static readonly Regex PageBreak = new(@"#\$[bBeE]#", RegexOptions.CultureInvariant);

        /// <summary>
        /// Commands that take arguments up to the next '#': a question or response option
        /// (<c>#$r 958699 30 event_idea1#</c>), a once-only flag (<c>#$1 Abigail1#</c>), a
        /// branch (<c>$d kent#</c>), and similar. Listed by name rather than matched as "any code
        /// then a space": an emotion code can be followed by a space and more of the line (<c>$h ほら</c>).
        /// </summary>
        private static readonly Regex ArgumentCommand = new(@"#?\$(?:query|action|[1dqrpcyt])\s[^#]*#?", RegexOptions.CultureInvariant);

        /// <summary>Portrait/emotion codes: <c>$h</c>, <c>$s</c>, <c>$9</c>, <c>$u</c>, ...</summary>
        private static readonly Regex EmotionCode = new(@"\$[a-zA-Z0-9]+", RegexOptions.CultureInvariant);

        /// <summary>
        /// The cleaned page of <paramref name="raw"/> holding characters
        /// <paramref name="offset"/>..+<paramref name="length"/>, and where they landed in it
        /// (null when the markup cleaning swallowed them).
        /// </summary>
        public static (string Page, int PageIndex, (int Start, int Length)? Highlight) PageAround(string raw, int offset, int length)
        {
            // find the raw page the span starts in
            int pageStart = 0;
            int pageIndex = 0;
            int pageEnd = raw.Length;
            foreach (Match brk in PageBreak.Matches(raw))
            {
                if (brk.Index + brk.Length <= offset)
                {
                    pageStart = brk.Index + brk.Length;
                    pageIndex++;
                    continue;
                }

                pageEnd = brk.Index;
                break;
            }

            string rawPage = raw.Substring(pageStart, pageEnd - pageStart);
            int relStart = Math.Clamp(offset - pageStart, 0, rawPage.Length);
            int relEnd = Math.Clamp(offset + length - pageStart, relStart, rawPage.Length);

            // clean with a sentinel either side of the span, so its position survives the cleaning
            const char open = '\uE000';
            const char close = '\uE001';
            string marked = rawPage.Substring(0, relStart) + open + rawPage.Substring(relStart, relEnd - relStart) + close + rawPage.Substring(relEnd);
            string cleaned = Clean(marked);

            int a = cleaned.IndexOf(open);
            int b = cleaned.IndexOf(close);
            string page = cleaned.Replace(open.ToString(), "").Replace(close.ToString(), "");

            (int, int)? highlight = a >= 0 && b > a + 1 ? (a, b - a - 1) : null;
            return (page, pageIndex, highlight);
        }

        /// <summary>
        /// Page <paramref name="pageIndex"/> of an English translation, cleaned -- or the whole thing
        /// when its page count differs from the Japanese, since then there's no telling which
        /// English page goes with which.
        /// </summary>
        public static string EnglishPage(string english, int pageIndex, int japanesePageCount)
        {
            string[] pages = PageBreak.Split(english);
            if (pages.Length == japanesePageCount && pageIndex < pages.Length)
                return Clean(pages[pageIndex]);

            return Clean(PageBreak.Replace(english, " "));
        }

        /// <summary>How many pages the game draws <paramref name="raw"/> as.</summary>
        public static int PageCount(string raw) => PageBreak.Matches(raw).Count + 1;

        /// <summary>
        /// Strips dialogue commands and portrait codes, shows the player-name token as a
        /// placeholder, and trims. Deliberately loose: this is for reading, not for matching
        /// against what the game drew.
        /// </summary>
        public static string Clean(string text)
        {
            string result = PageBreak.Replace(text, " ");
            result = ArgumentCommand.Replace(result, "");
            result = EmotionCode.Replace(result, "");
            // what's left of a random-choice command ($c .5#one#other) is its alternatives
            result = result.Replace("#", " / ");
            result = result.Replace("@", "(name)").Replace("^", " / ");
            return result.Trim();
        }
    }
}
