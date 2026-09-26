using System.Collections.Generic;
using System.Text;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>
    /// Converts a segment's kana reading to Hepburn romaji at runtime.
    ///
    /// Deliberately free of StardewValley/MonoGame types so it can be unit-tested. A C# port of
    /// tools/segment-data/kana_to_romaji.py, which generates the data's "reading" field from the
    /// same kana -- the two should agree, so a rule changed in one belongs in the other.
    ///
    /// Kana is the source of truth: kana to romaji is deterministic where romaji to kana is not.
    /// The one thing kana can't say is where a morpheme boundary splits a vowel pair -- 思う is
    /// omou, not omō -- so the few common words where that happens are listed in NotLong.
    ///
    /// Conventions the kana field follows (and this relies on):
    ///   - words separated by single spaces ("かんがえこんで しまう")
    ///   - particles written as spelled: は / へ / を; a standalone one reads wa / e / o
    ///   - katakana words stay katakana; ー lengthens the vowel before it
    ///   - latin and digits the on-screen text keeps (Joja, 2.0) pass through unchanged
    ///
    /// Output uses macrons (ō), which the game's font can't draw; pass it through
    /// <see cref="FontSafeText"/> before rendering.
    /// </summary>
    public static class KanaRomaji
    {
        // hiragana; katakana is folded onto it by codepoint shift first. Two-kana combinations are
        // tried before single kana.
        private static readonly Dictionary<string, string> Pairs = new()
        {
            ["きゃ"] = "kya", ["きゅ"] = "kyu", ["きょ"] = "kyo", ["ぎゃ"] = "gya", ["ぎゅ"] = "gyu", ["ぎょ"] = "gyo",
            ["しゃ"] = "sha", ["しゅ"] = "shu", ["しょ"] = "sho", ["しぇ"] = "she",
            ["じゃ"] = "ja", ["じゅ"] = "ju", ["じょ"] = "jo", ["じぇ"] = "je",
            ["ちゃ"] = "cha", ["ちゅ"] = "chu", ["ちょ"] = "cho", ["ちぇ"] = "che",
            ["にゃ"] = "nya", ["にゅ"] = "nyu", ["にょ"] = "nyo", ["ひゃ"] = "hya", ["ひゅ"] = "hyu", ["ひょ"] = "hyo",
            ["びゃ"] = "bya", ["びゅ"] = "byu", ["びょ"] = "byo", ["ぴゃ"] = "pya", ["ぴゅ"] = "pyu", ["ぴょ"] = "pyo",
            ["みゃ"] = "mya", ["みゅ"] = "myu", ["みょ"] = "myo", ["りゃ"] = "rya", ["りゅ"] = "ryu", ["りょ"] = "ryo",
            ["ふぁ"] = "fa", ["ふぃ"] = "fi", ["ふぇ"] = "fe", ["ふぉ"] = "fo", ["ふゅ"] = "fyu",
            ["ゔぁ"] = "va", ["ゔぃ"] = "vi", ["ゔぇ"] = "ve", ["ゔぉ"] = "vo",
            ["てぃ"] = "ti", ["でぃ"] = "di", ["とぅ"] = "tu", ["どぅ"] = "du", ["でゅ"] = "dyu",
            ["つぁ"] = "tsa", ["つぃ"] = "tsi", ["つぇ"] = "tse", ["つぉ"] = "tso",
            ["うぃ"] = "wi", ["うぇ"] = "we", ["うぉ"] = "wo", ["いぇ"] = "ye",
        };

        private static readonly Dictionary<char, string> Single = new()
        {
            ['あ'] = "a", ['い'] = "i", ['う'] = "u", ['え'] = "e", ['お'] = "o",
            ['か'] = "ka", ['き'] = "ki", ['く'] = "ku", ['け'] = "ke", ['こ'] = "ko",
            ['が'] = "ga", ['ぎ'] = "gi", ['ぐ'] = "gu", ['げ'] = "ge", ['ご'] = "go",
            ['さ'] = "sa", ['し'] = "shi", ['す'] = "su", ['せ'] = "se", ['そ'] = "so",
            ['ざ'] = "za", ['じ'] = "ji", ['ず'] = "zu", ['ぜ'] = "ze", ['ぞ'] = "zo",
            ['た'] = "ta", ['ち'] = "chi", ['つ'] = "tsu", ['て'] = "te", ['と'] = "to",
            ['だ'] = "da", ['ぢ'] = "ji", ['づ'] = "zu", ['で'] = "de", ['ど'] = "do",
            ['な'] = "na", ['に'] = "ni", ['ぬ'] = "nu", ['ね'] = "ne", ['の'] = "no",
            ['は'] = "ha", ['ひ'] = "hi", ['ふ'] = "fu", ['へ'] = "he", ['ほ'] = "ho",
            ['ば'] = "ba", ['び'] = "bi", ['ぶ'] = "bu", ['べ'] = "be", ['ぼ'] = "bo",
            ['ぱ'] = "pa", ['ぴ'] = "pi", ['ぷ'] = "pu", ['ぺ'] = "pe", ['ぽ'] = "po",
            ['ま'] = "ma", ['み'] = "mi", ['む'] = "mu", ['め'] = "me", ['も'] = "mo",
            ['や'] = "ya", ['ゆ'] = "yu", ['よ'] = "yo",
            ['ら'] = "ra", ['り'] = "ri", ['る'] = "ru", ['れ'] = "re", ['ろ'] = "ro",
            ['わ'] = "wa", ['ゐ'] = "i", ['ゑ'] = "e", ['を'] = "o", ['ん'] = "n", ['ゔ'] = "vu",
            // small vowels standing alone (ぁ in "あぁ", ぇ in "へぇ") just extend the sound
            ['ぁ'] = "a", ['ぃ'] = "i", ['ぅ'] = "u", ['ぇ'] = "e", ['ぉ'] = "o", ['ゃ'] = "ya", ['ゅ'] = "yu", ['ょ'] = "yo", ['ゎ'] = "wa",
        };

        private static readonly Dictionary<string, string> Particles = new() { ["は"] = "wa", ["へ"] = "e", ["を"] = "o" };

        // greetings that end in the particle は, fossilised into one word
        private static readonly Dictionary<string, string> Fixed = new() { ["こんにちは"] = "konnichiwa", ["こんばんは"] = "konbanwa" };

        // words where おう / うう straddle a morpheme boundary, so it is two sounds, not one long one
        private static readonly HashSet<string> NotLong = new() { "おもう", "かよう", "まよう", "すくう", "くう", "ぬう" };

        private const string LengtheningKana = "あうえおぁぅぇぉ";

        private static readonly Dictionary<string, string> Cache = new();

        /// <summary>The Hepburn romaji for a space-separated kana reading. Memoised: the overlay asks every frame.</summary>
        public static string Convert(string kana)
        {
            if (Cache.TryGetValue(kana, out string? cached))
                return cached;

            // any whitespace, including the ideographic space some kana carries over from the text
            var words = kana.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < words.Length; i++)
                words[i] = ConvertWord(words[i]);

            string result = string.Join(" ", words);
            Cache[kana] = result;
            return result;
        }

        private static string ConvertWord(string word)
        {
            if (Particles.TryGetValue(word, out string? particle))
                return particle;
            if (Fixed.TryGetValue(word, out string? fixedReading))
                return fixedReading;

            string hira = ToHiragana(word);
            bool longOk = !NotLong.Contains(hira);
            var output = new List<string>(); // romaji pieces, one per syllable
            bool geminate = false;           // a pending っ

            foreach (var (syllable, romaji) in Syllables(hira))
            {
                string roma = romaji;

                if (syllable == "っ")
                {
                    geminate = true;
                    continue;
                }

                if (syllable == "ー")
                {
                    if (output.Count > 0 && output[^1].Length > 0 && TryMacron(output[^1][^1], out char lengthened))
                        output[^1] = output[^1][..^1] + lengthened;
                    continue;
                }

                string prev = output.Count > 0 ? output[^1] : "";

                // ん before a vowel or y is written n' (kin'yōbi)
                if (prev == "n" && roma.Length > 0 && "aiueoy".IndexOf(roma[0]) >= 0)
                    output[^1] = "n'";

                // a vowel kana lengthening the previous syllable
                if (longOk && roma is "a" or "u" or "e" or "o" && prev.Length > 0
                    && IsLongPair(prev[^1], roma[0]) && LengtheningKana.Contains(syllable)
                    && TryMacron(prev[^1], out char macron))
                {
                    output[^1] = prev[..^1] + macron;
                    continue;
                }

                if (geminate)
                {
                    geminate = false;
                    if (roma.StartsWith("ch"))
                        roma = "t" + roma;
                    else if (roma.Length > 0 && char.IsLetter(roma[0]) && "aiueon".IndexOf(roma[0]) < 0)
                        roma = roma[0] + roma;
                }

                output.Add(roma);
            }

            // a trailing っ is a cut-off sound (ヒャッ); Hepburn has no letter for it
            return string.Concat(output);
        }

        private static string ToHiragana(string text)
        {
            var result = new StringBuilder(text.Length);
            foreach (char c in text)
                result.Append(c >= 'ァ' && c <= 'ヶ' ? (char)(c - 0x60) : c);
            return result.ToString();
        }

        /// <summary>Splits a kana word into (kana, romaji) syllables; non-kana characters pass through.</summary>
        private static IEnumerable<(string Kana, string Romaji)> Syllables(string word)
        {
            int i = 0;
            while (i < word.Length)
            {
                if (i + 1 < word.Length && Pairs.TryGetValue(word.Substring(i, 2), out string? pair))
                {
                    yield return (word.Substring(i, 2), pair);
                    i += 2;
                }
                else
                {
                    string kana = word[i].ToString();
                    yield return (kana, Single.TryGetValue(word[i], out string? single) ? single : kana);
                    i++;
                }
            }
        }

        // vowel pairs Hepburn writes as one long vowel. えい and いい stay as written (sensei, ii).
        private static bool IsLongPair(char first, char second) =>
            (first == second && first != 'i') || (first == 'o' && second == 'u');

        private static bool TryMacron(char vowel, out char macron)
        {
            macron = vowel switch { 'a' => 'ā', 'i' => 'ī', 'u' => 'ū', 'e' => 'ē', 'o' => 'ō', _ => '\0' };
            return macron != '\0';
        }
    }
}
