using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>One call the game made to Dialogue.checkForSpecialCharacters while parsing: the raw segment in, the substituted text out.</summary>
    public readonly record struct SpecialCharacterCall(string Input, string Output);

    /// <summary>
    /// The pure half of the dialogue translation bubble: which raw segment of a dialogue entry a
    /// page on screen came from, and what the same segment says in English.
    ///
    /// Per Dialogue.parseDialogueString in the 1.6.15 IL, the game picks one "||" alternative by
    /// days played, splits it on '#', and makes a page (DialogueLine) from each text segment of 2+
    /// characters. Which segments become pages depends on game state and on Game1.random ($c, $1,
    /// $q, $d...), so rather than mirror those decisions, <c>DialogueCapturePatches</c> records the
    /// segments the game actually fed to checkForSpecialCharacters and <see cref="SegmentsOfLines"/>
    /// matches its pages back to them. The English entry is split the same way and the same segment
    /// taken, which only works while the two locales share their '#' structure -- true of all but a
    /// handful of entries (see DialoguePagesTests), which fall back to the whole entry.
    ///
    /// Game-free so it can be tested against the real tables in tools/ModLogic.Tests.
    /// </summary>
    public static class DialoguePages
    {
        /// <summary>Separates alternatives the game chooses between by week (Dialogue.multipleDialogueDelineator).</summary>
        public const string AlternativeDelimiter = "||";

        /// <summary>The '%' tokens checkForSpecialCharacters substitutes, longest first so %kid1 isn't read as %k... and %firstnameletter as %farm.</summary>
        private static readonly string[] PercentTokens =
        {
            "firstnameletter", "revealtaste", "favorite", "noturn", "spouse", "season", "place", "noun", "name",
            "band", "book", "farm", "fork", "kid1", "kid2", "time", "year", "adj", "pet",
        };

        private static readonly Regex GiftList = new(@"\[[^\]]*\]", RegexOptions.Compiled);

        /// <summary>checkEmotions' markers ($h, $s, $u, $l, $a, $neutral) and $k anywhere, and a portrait index ($12) at the end.</summary>
        private static readonly Regex EmotionMarker = new(@"\$(?:neutral|[hsulak](?![a-z]))|\$\d+\s*$", RegexOptions.Compiled);

        private static readonly Regex Placeholder = new(@"\{(\d+)\}", RegexOptions.Compiled);

        public static string[] Alternatives(string raw)
        {
            return raw.Split(AlternativeDelimiter);
        }

        /// <summary>Which alternative the game shows: parseDialogueString takes (DaysPlayed / 7) % count.</summary>
        public static int AlternativeIndex(int count, uint daysPlayed)
        {
            return count <= 1 ? 0 : (int)(daysPlayed / 7 % (uint)count);
        }

        /// <summary>The raw segments of the alternative the game shows, split the way parseDialogueString splits them.</summary>
        public static string[] Segments(string raw, int alternative)
        {
            string[] alternatives = Alternatives(raw);
            return alternatives[Math.Clamp(alternative, 0, alternatives.Length - 1)].Split('#');
        }

        /// <summary>
        /// Where each recorded call's input sits among the segments, or -1. Searched forward from the
        /// last match, because the game calls in segment order, then from the start, because $q can
        /// splice in another entry. Command segments ($b, $q...) never become a page themselves, so
        /// they aren't looked up.
        /// </summary>
        public static int[] SegmentsOfCalls(IReadOnlyList<string> segments, IReadOnlyList<SpecialCharacterCall> calls)
        {
            var result = new int[calls.Count];
            int cursor = 0;

            for (int c = 0; c < calls.Count; c++)
            {
                string input = calls[c].Input;
                int found = -1;
                if (!input.StartsWith('$'))
                {
                    found = IndexOf(segments, input, cursor);
                    if (found < 0)
                        found = IndexOf(segments, input, 0);
                }

                result[c] = found;
                if (found >= 0)
                    cursor = found + 1;
            }

            return result;
        }

        private static int IndexOf(IReadOnlyList<string> segments, string value, int from)
        {
            for (int i = from; i < segments.Count; i++)
            {
                if (segments[i] == value)
                    return i;
            }

            return -1;
        }

        /// <summary>
        /// Which raw segment each page came from, or -1 where that's unknown (pages with no text,
        /// such as $action's, and anything the game built without a checkForSpecialCharacters call).
        ///
        /// A page's text at the end of parsing is its call's output, sometimes with a "{" after it
        /// ($b) or "mailId}" before it ($1 without the mail), so a page is matched to the next
        /// unused call whose output it contains.
        /// </summary>
        public static int[] SegmentsOfLines(IReadOnlyList<string> lineTexts, IReadOnlyList<SpecialCharacterCall> calls, IReadOnlyList<int> callSegments)
        {
            var result = new int[lineTexts.Count];
            int cursor = 0;

            for (int line = 0; line < lineTexts.Count; line++)
            {
                result[line] = -1;
                string text = lineTexts[line];
                if (string.IsNullOrEmpty(text))
                    continue;

                for (int c = cursor; c < calls.Count; c++)
                {
                    if (calls[c].Input.StartsWith('$') || calls[c].Output.Length == 0 || !text.Contains(calls[c].Output, StringComparison.Ordinal))
                        continue;

                    result[line] = callSegments[c];
                    cursor = c + 1;
                    break;
                }
            }

            return result;
        }

        /// <summary>
        /// The English entry's raw segments for the same alternative, or null when its structure
        /// differs from the Japanese entry's (a different number of alternatives or segments), since
        /// then the same index doesn't mean the same page.
        /// </summary>
        public static string[]? MatchingSegments(string japanese, string english, int alternative)
        {
            string[] japaneseAlternatives = Alternatives(japanese);
            string[] englishAlternatives = Alternatives(english);
            if (japaneseAlternatives.Length != englishAlternatives.Length || alternative < 0 || alternative >= englishAlternatives.Length)
                return null;

            string[] japaneseSegments = japaneseAlternatives[alternative].Split('#');
            string[] englishSegments = englishAlternatives[alternative].Split('#');
            return japaneseSegments.Length == englishSegments.Length ? englishSegments : null;
        }

        /// <summary>The answers a question page offers: the text after each $r command that follows it.</summary>
        public static IReadOnlyList<string> ResponsesAfter(IReadOnlyList<string> segments, int question)
        {
            var responses = new List<string>();
            for (int i = question + 1; i + 1 < segments.Count && segments[i].StartsWith("$r ", StringComparison.Ordinal); i += 2)
                responses.Add(segments[i + 1]);
            return responses;
        }

        /// <summary>
        /// The English page for one segment, with any answers it offers listed underneath. Null when
        /// the segment is a command or has no text left once cleaned.
        /// </summary>
        public static string? Page(IReadOnlyList<string> segments, int segment, Func<string, string> genderSwitch, Func<string, string?> token)
        {
            if (segment < 0 || segment >= segments.Count || segments[segment].StartsWith('$'))
                return null;

            string page = Clean(segments[segment], genderSwitch, token);
            if (page.Length == 0)
                return null;

            var responses = ResponsesAfter(segments, segment).Select(response => Clean(response, genderSwitch, token)).Where(response => response.Length > 0).ToList();
            return responses.Count == 0 ? page : page + "\n\n" + string.Join("\n", responses.Select(response => "> " + response));
        }

        /// <summary>
        /// Every text page of the shown alternative, one per line: the fallback when the page on
        /// screen can't be pinned to a segment. Answers ($r) come after their question; other
        /// command segments, whose text is mixed with conditions, are left out.
        /// </summary>
        public static string? WholeEntry(string english, int alternative, Func<string, string> genderSwitch, Func<string, string?> token)
        {
            string[] segments = Segments(english, alternative);
            var pages = new List<string>();

            for (int i = 0; i < segments.Length; i++)
            {
                if (segments[i].StartsWith("$r ", StringComparison.Ordinal))
                {
                    i++; // its answer text, already listed under the question
                    continue;
                }

                if (segments[i].Length < 2 || segments[i].StartsWith('$'))
                    continue;

                if (Page(segments, i, genderSwitch, token) is { } page)
                    pages.Add(page);
            }

            return pages.Count == 0 ? null : string.Join("\n", pages);
        }

        /// <summary>
        /// Turns a raw English segment into the text a player would have read, following what the
        /// game does to the Japanese (checkForSpecialCharacters, checkEmotions,
        /// prepareCurrentDialogueForDisplay): gender switches, then gift lists, emotion and
        /// portrait markers, the no-portrait '%' prefix, and '@' and '%' tokens. A token
        /// <paramref name="token"/> can't resolve (a random adjective, say) becomes "...".
        /// </summary>
        public static string Clean(string segment, Func<string, string> genderSwitch, Func<string, string?> token)
        {
            string text = genderSwitch(segment);
            text = GiftList.Replace(text, "");
            text = EmotionMarker.Replace(text, "");

            if (text.StartsWith('%') && !PercentTokens.Any(name => text.AsSpan(1).StartsWith(name, StringComparison.Ordinal)))
                text = text.Substring(1);

            var result = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '@')
                {
                    result.Append(token("@") ?? "@");
                    continue;
                }

                if (text[i] == '%' && PercentTokens.FirstOrDefault(name => string.CompareOrdinal(text, i + 1, name, 0, name.Length) == 0) is { } name)
                {
                    if (name is not ("noturn" or "fork" or "revealtaste"))
                        result.Append(token(name) ?? "...");
                    i += name.Length;
                    continue;
                }

                result.Append(text[i]);
            }

            return result.ToString().Trim();
        }

        /// <summary>
        /// The values a LoadString call put into a template's {0}, {1}... placeholders, found by
        /// matching the template against the formatted text. Empty when the template has none; null
        /// when the text isn't that template. Lets the English be formatted with the same values
        /// without patching every FromTranslation overload to see its arguments.
        /// </summary>
        public static IReadOnlyList<string>? TemplateArguments(string template, string formatted)
        {
            var matches = Placeholder.Matches(template);
            if (matches.Count == 0)
                return template == formatted ? Array.Empty<string>() : null;

            var pattern = new StringBuilder("^");
            var seen = new HashSet<int>();
            int last = 0;
            int highest = -1;

            foreach (Match match in matches)
            {
                pattern.Append(Regex.Escape(template.Substring(last, match.Index - last)));
                int n = int.Parse(match.Groups[1].Value);
                pattern.Append(seen.Add(n) ? $"(?<a{n}>.*?)" : $@"\k<a{n}>");
                highest = Math.Max(highest, n);
                last = match.Index + match.Length;
            }

            pattern.Append(Regex.Escape(template.Substring(last))).Append('$');

            var result = Regex.Match(formatted, pattern.ToString(), RegexOptions.Singleline);
            if (!result.Success)
                return null;

            var values = new string[highest + 1];
            for (int n = 0; n <= highest; n++)
                values[n] = result.Groups[$"a{n}"].Success ? result.Groups[$"a{n}"].Value : "";
            return values;
        }

        /// <summary>
        /// For an entry loaded with Game1.LoadStringByGender, which splits on '/' and takes the first
        /// half for a male speaker and the last otherwise: the half of each locale's entry that the
        /// formatted text came from. Both entries are returned whole when the text isn't one half
        /// (the entry wasn't loaded by gender, or '/' is just part of it).
        /// </summary>
        public static (string Japanese, string English) MatchingHalves(string japanese, string formatted, string english)
        {
            if (!japanese.Contains('/') || !english.Contains('/') || TemplateArguments(japanese, formatted) is not null)
                return (japanese, english);

            string[] japaneseHalves = japanese.Split('/');
            string[] englishHalves = english.Split('/');
            if (TemplateArguments(japaneseHalves[0], formatted) is not null)
                return (japaneseHalves[0], englishHalves[0]);
            if (TemplateArguments(japaneseHalves[^1], formatted) is not null)
                return (japaneseHalves[^1], englishHalves[^1]);
            return (japanese, english);
        }

        /// <summary>Fills a template's {n} placeholders, leaving any it has no value for as they are. Unlike string.Format, stray braces don't throw.</summary>
        public static string FillTemplate(string template, IReadOnlyList<string> values)
        {
            return Placeholder.Replace(template, match =>
            {
                int n = int.Parse(match.Groups[1].Value);
                return n < values.Count ? values[n] : match.Value;
            });
        }

        /// <summary>Whether two strings are the same text once whitespace is ignored -- the game inserts line breaks when it wraps.</summary>
        public static bool SameText(string? a, string? b)
        {
            if (a is null || b is null)
                return false;

            static string Squash(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
            return Squash(a) == Squash(b);
        }

        #region Event scripts
        /// <summary>
        /// Splits an event command into its arguments the way ArgUtility.SplitBySpaceQuoteAware does:
        /// on spaces outside double quotes, dropping the quotes, with \" as a literal quote.
        /// </summary>
        public static IReadOnlyList<string> SplitCommand(string command)
        {
            var args = new List<string>();
            var current = new StringBuilder();
            bool quoted = false;
            bool any = false;

            for (int i = 0; i < command.Length; i++)
            {
                char c = command[i];
                if (c == '\\' && i + 1 < command.Length && command[i + 1] == '"')
                {
                    current.Append('"');
                    any = true;
                    i++;
                }
                else if (c == '"')
                {
                    quoted = !quoted;
                    any = true;
                }
                else if (c == ' ' && !quoted)
                {
                    if (any)
                        args.Add(current.ToString());
                    current.Clear();
                    any = false;
                }
                else
                {
                    current.Append(c);
                    any = true;
                }
            }

            if (any)
                args.Add(current.ToString());
            return args;
        }

        /// <summary>
        /// Finds the command that holds <paramref name="text"/> as one of its arguments, nearest
        /// <paramref name="around"/> first (the event's current command, which may already have
        /// moved on by the time the box is built). Returns the command and argument index.
        /// </summary>
        public static (int Command, int Argument)? FindCommandWithText(IReadOnlyList<string> commands, int around, string text, int reach = 3)
        {
            for (int distance = 0; distance <= reach; distance++)
            {
                foreach (int i in distance == 0 ? new[] { around } : new[] { around - distance, around + distance })
                {
                    if (i < 0 || i >= commands.Count)
                        continue;

                    var args = SplitCommand(commands[i]);
                    for (int a = 1; a < args.Count; a++)
                    {
                        if (SameText(args[a], text))
                            return (i, a);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Which script of an event asset the running event was parsed from: the one whose parsed
        /// commands match the live ones. Forks swap in another entry's commands, so this compares
        /// the commands rather than trusting the event's id.
        /// </summary>
        public static string? FindScript(IEnumerable<KeyValuePair<string, IReadOnlyList<string>>> parsedScripts, IReadOnlyList<string> live, int index)
        {
            string? loose = null;
            foreach (var (key, commands) in parsedScripts)
            {
                if (commands.Count != live.Count || index >= commands.Count || commands[index] != live[index])
                    continue;

                if (commands.SequenceEqual(live))
                    return key;
                loose ??= key;
            }

            return loose;
        }

        /// <summary>
        /// The English text of the matching command, or null unless it's recognisably the same
        /// command: the same number of arguments, and the same command name and actor before the
        /// text (so a translation that reordered the script isn't shown against the wrong line).
        /// </summary>
        public static string? EnglishArgument(IReadOnlyList<string> englishCommands, int command, IReadOnlyList<string> japaneseArgs, int argument)
        {
            if (command < 0 || command >= englishCommands.Count)
                return null;

            var englishArgs = SplitCommand(englishCommands[command]);
            if (englishArgs.Count != japaneseArgs.Count || argument >= englishArgs.Count)
                return null;

            for (int a = 0; a < argument; a++)
            {
                if (englishArgs[a] != japaneseArgs[a])
                    return null;
            }

            return englishArgs[argument];
        }
        #endregion
    }
}
