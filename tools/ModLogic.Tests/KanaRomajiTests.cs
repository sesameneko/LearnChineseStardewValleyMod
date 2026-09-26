using LanguageStudyStardewValleyMod;

namespace ModLogic.Tests;

public class KanaRomajiTests
{
    [Theory]
    [InlineData("うえる", "ueru")]
    [InlineData("がっこう", "gakkō")]
    [InlineData("とおり", "tōri")]
    [InlineData("せんせい", "sensei")]
    [InlineData("いい", "ii")]
    [InlineData("おもう", "omou")]
    [InlineData("きんようび", "kin'yōbi")]
    [InlineData("まっちゃ", "matcha")]
    [InlineData("ヒャッ", "hya")]
    [InlineData("コーヒー", "kōhī")]
    [InlineData("ティー", "tī")]
    [InlineData("しゃしん", "shashin")]
    [InlineData("こんにちは", "konnichiwa")]
    [InlineData("わたし は がっこう へ いく", "watashi wa gakkō e iku")]
    [InlineData("ほん を よむ", "hon o yomu")]
    [InlineData("Joja", "Joja")]
    [InlineData("2.0", "2.0")]
    public void ConvertsKanaToHepburn(string kana, string expected)
    {
        Assert.Equal(expected, KanaRomaji.Convert(kana));
    }

    [Fact]
    public void FontSafedOutputHasNoMacrons()
    {
        Assert.Equal("gakkoo", FontSafeText.Apply(KanaRomaji.Convert("がっこう")));
    }
}
