using System.Collections.Generic;
using System.Text;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>
    /// Turns a segment's stored pinyin into what the hover label and flashcards show.
    ///
    /// The data stores one syllable per hanzi, space-separated, with tone marks (牧场 is
    /// "mù chǎng"; see tools/segment-data/pinyin.py, which defines and checks the form). Joining
    /// the syllables of a word is a display step: "mùchǎng", with an apostrophe before a syllable
    /// that starts with a, o or e (xī'ān), and an erhua "r" attached (yìdiǎnr). A token that isn't
    /// a syllable -- latin or digits the text keeps, Joja or 2.0 -- stays separated by spaces.
    ///
    /// The game's fonts don't have every tone mark (the zh SmallFont lacks tones 1 and 3 on all
    /// vowels and every marked ü, unless ExtendedFont adds them), and XNA draws a missing glyph as
    /// '*'. So <see cref="ForFont"/> falls back to tone numbers for the whole word: "mu4chang3" still
    /// says the tones, where a mark silently dropped or starred would not.
    ///
    /// Game-free so it can be unit-tested.
    /// </summary>
    public static class Pinyin
    {
        /// <summary>Marked vowel -> (plain vowel, tone).</summary>
        private static readonly Dictionary<char, (char Plain, int Tone)> Marks = BuildMarks();

        private static Dictionary<char, (char, int)> BuildMarks()
        {
            var marks = new Dictionary<char, (char, int)>();
            foreach (var (plain, marked) in new[]
            {
                ('a', "āáǎà"), ('e', "ēéěè"), ('i', "īíǐì"), ('o', "ōóǒò"), ('u', "ūúǔù"), ('ü', "ǖǘǚǜ"),
                ('n', "\0ńňǹ"), ('m', "\0ḿ\0\0"),
            })
            {
                for (int tone = 0; tone < 4; tone++)
                {
                    if (marked[tone] != '\0')
                        marks[marked[tone]] = (plain, tone + 1);
                }
            }

            return marks;
        }

        /// <summary>The display form with tone marks: "mù chǎng" -> "mùchǎng". Empty for no reading.</summary>
        public static string Display(string? stored) => Join(stored, numbered: false);

        /// <summary>The display form with tone numbers: "mù chǎng" -> "mu4chang3"; the neutral tone has none.</summary>
        public static string ToneNumbers(string? stored) => Join(stored, numbered: true);

        /// <summary>
        /// <see cref="Display"/> if the font can draw all of it, otherwise <see cref="ToneNumbers"/>.
        /// With no <paramref name="drawable"/> set, anything non-ASCII is assumed missing.
        /// </summary>
        public static string ForFont(string? stored, IReadOnlySet<char>? drawable)
        {
            string display = Display(stored);
            foreach (char c in display)
            {
                if (c > 127 && drawable?.Contains(c) != true)
                    return ToneNumbers(stored);
            }

            return display;
        }

        /// <summary>Whether a stored token is a pinyin syllable (lowercase letters, ü and tone marks) rather than copied text.</summary>
        public static bool IsSyllable(string token)
        {
            if (token.Length == 0)
                return false;

            foreach (char c in token)
            {
                if (!(c is >= 'a' and <= 'z' || c == 'ü' || Marks.ContainsKey(c)))
                    return false;
            }

            return true;
        }

        private static string Join(string? stored, bool numbered)
        {
            if (string.IsNullOrWhiteSpace(stored))
                return string.Empty;

            var result = new StringBuilder(stored!.Length);
            bool previousWasSyllable = false;
            foreach (string token in stored.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries))
            {
                bool syllable = IsSyllable(token);
                if (result.Length > 0)
                {
                    if (!syllable || !previousWasSyllable)
                        result.Append(' ');
                    else if (!numbered && token != "r" && Plain(token[0]) is 'a' or 'o' or 'e')
                        result.Append('\'');
                }

                result.Append(syllable && numbered ? Numbered(token) : token);
                previousWasSyllable = syllable;
            }

            return result.ToString();
        }

        private static char Plain(char c) => Marks.TryGetValue(c, out var mark) ? mark.Plain : c;

        /// <summary>"chǎng" -> "chang3"; "de" -> "de".</summary>
        private static string Numbered(string syllable)
        {
            int tone = 0;
            var plain = new StringBuilder(syllable.Length + 1);
            foreach (char c in syllable)
            {
                if (Marks.TryGetValue(c, out var mark))
                {
                    plain.Append(mark.Plain);
                    tone = mark.Tone;
                }
                else
                    plain.Append(c);
            }

            return tone == 0 ? plain.ToString() : plain.Append(tone).ToString();
        }
    }
}
