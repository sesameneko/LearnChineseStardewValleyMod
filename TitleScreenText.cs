using System.Collections.Generic;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>A reading of a piece of Japanese text: its kana, Hepburn romaji and English.</summary>
    public sealed record TitleReading(string Kana, string Romaji, string English = "")
    {
        /// <summary>The lines to show, kana first, with the romaji passed through <paramref name="fontSafe"/>.</summary>
        public string Describe(System.Func<string, string> fontSafe)
        {
            string text = $"{this.Kana}\n{fontSafe(this.Romaji)}";
            return this.English == "" ? text : $"{text}\n{this.English}";
        }
    }

    /// <summary>
    /// Hand-written readings for the Japanese title screen, whose text is baked into the
    /// Minigames/TitleButtons.ja-JP texture rather than drawn from any string table -- so neither
    /// the translation index nor the segment data can see it.
    ///
    /// Per that texture in 1.6.15: the buttons read はじめから, つづきから, CO-OP and 終了. The game's
    /// own ja text calls co-op 協力プレイ (UI.json StartLocalMulti), so that's the reading given for
    /// the button drawn in English.
    ///
    /// Game-free so it can be unit-tested; the romaji is checked there against KanaRomaji.
    /// </summary>
    public static class TitleScreenText
    {
        /// <summary>Button readings, keyed by the name TitleMenu.setUpIcons gives each button.</summary>
        public static readonly IReadOnlyDictionary<string, TitleReading> Buttons = new Dictionary<string, TitleReading>
        {
            ["New"] = new("はじめから", "hajime kara", "New game (lit. from the beginning)"),
            ["Load"] = new("つづきから", "tsuzuki kara", "Load (lit. from where you left off)"),
            ["Co-op"] = new("きょうりょくプレイ", "kyōryoku purei", "Co-op (lit. cooperative play)"),
            ["Exit"] = new("しゅうりょう", "shūryō", "Exit (lit. end)"),
        };
    }
}
