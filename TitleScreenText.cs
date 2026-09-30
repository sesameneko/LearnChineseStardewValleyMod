using System.Collections.Generic;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>
    /// A reading of a piece of Chinese text: its hanzi, stored pinyin and English. The hanzi is kept
    /// to check the pinyin against, but isn't shown: the button already displays it.
    /// </summary>
    public sealed record TitleReading(string Hanzi, string Pinyin, string English = "")
    {
        /// <summary>The lines to show, pinyin then English, with the stored pinyin passed through <paramref name="pinyinForFont"/>.</summary>
        public string Describe(System.Func<string, string> pinyinForFont)
        {
            string text = pinyinForFont(this.Pinyin);
            return this.English == "" ? text : $"{text}\n{this.English}";
        }
    }

    /// <summary>
    /// Hand-written readings for the Chinese title screen, whose text is baked into the
    /// Minigames/TitleButtons.zh-CN texture rather than drawn from any string table -- so neither
    /// the translation index nor the segment data can see it.
    ///
    /// Per that texture in 1.6.15: the buttons read 创建, 加载, 合作 and 退出. The pinyin is in the
    /// stored form (one syllable per hanzi) and matches how assets/segments/zh reads the same words.
    ///
    /// Game-free so it can be unit-tested.
    /// </summary>
    public static class TitleScreenText
    {
        /// <summary>Button readings, keyed by the name TitleMenu.setUpIcons gives each button.</summary>
        public static readonly IReadOnlyDictionary<string, TitleReading> Buttons = new Dictionary<string, TitleReading>
        {
            ["New"] = new("创建", "chuàng jiàn", "New game (lit. create)"),
            ["Load"] = new("加载", "jiā zài", "Load"),
            ["Co-op"] = new("合作", "hé zuò", "Co-op (lit. cooperate)"),
            ["Exit"] = new("退出", "tuì chū", "Exit (lit. withdraw, go out)"),
        };
    }
}
