using LanguageStudyStardewValleyMod;

namespace ModLogic.Tests;

public class TitleScreenTextTests
{
    public static TheoryData<string> Readings()
    {
        var data = new TheoryData<string>();
        foreach (string name in TitleScreenText.Buttons.Keys)
            data.Add(name);
        return data;
    }

    /// <summary>The pinyin is hand-written, so hold it to the stored form: one syllable per hanzi.</summary>
    [Theory]
    [MemberData(nameof(Readings))]
    public void PinyinIsOneSyllablePerHanzi(string name)
    {
        var reading = TitleScreenText.Buttons[name];
        string[] syllables = reading.Pinyin.Split(' ');
        Assert.Equal(reading.Hanzi.Length, syllables.Length);
        Assert.All(syllables, s => Assert.True(Pinyin.IsSyllable(s), $"'{s}' isn't a pinyin syllable"));
    }

    [Fact]
    public void CoversEveryMainButton()
    {
        Assert.Equal(new[] { "Co-op", "Exit", "Load", "New" }, TitleScreenText.Buttons.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void DescribesPinyinThenEnglish()
    {
        Assert.Equal("tuìchū\nExit (lit. withdraw, go out)", TitleScreenText.Buttons["Exit"].Describe(p => Pinyin.Display(p)));
        Assert.Equal("jia1zai4\nLoad", TitleScreenText.Buttons["Load"].Describe(p => Pinyin.ForFont(p, null)));
    }
}
