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

    /// <summary>The romaji is hand-written for its spacing and capitals, but should say what KanaRomaji would.</summary>
    [Theory]
    [MemberData(nameof(Readings))]
    public void RomajiAgreesWithKanaRomaji(string name)
    {
        var reading = TitleScreenText.Buttons[name];
        Assert.Equal(KanaRomaji.Convert(reading.Kana), reading.Romaji.Replace(" ", "").ToLowerInvariant());
    }

    [Fact]
    public void CoversEveryMainButton()
    {
        Assert.Equal(new[] { "Co-op", "Exit", "Load", "New" }, TitleScreenText.Buttons.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void DescribesKanaThenRomajiThenEnglish()
    {
        Assert.Equal("しゅうりょう\nshuuryoo\nExit (lit. end)", TitleScreenText.Buttons["Exit"].Describe(r => FontSafeText.Apply(r)));
        Assert.Equal("はじめから\nhajime kara\nNew game (lit. from the beginning)", TitleScreenText.Buttons["New"].Describe(r => r));
    }
}
