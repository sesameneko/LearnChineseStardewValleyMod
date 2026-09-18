using XnbStringTool;

namespace XnbStringTool.Tests;

public class RoundTripTests
{
    private static Dictionary<string, string> WriteThenRead(Dictionary<string, string> entries)
    {
        using var stream = new MemoryStream();
        XnbStringTable.Write(stream, entries);
        stream.Position = 0;
        var result = XnbStringTable.Read(stream);
        return result.Entries;
    }

    [Fact]
    public void RoundTrips_a_typical_dictionary()
    {
        var original = new Dictionary<string, string>
        {
            ["Acorn_Name"] = "Acorn",
            ["Acorn_Description"] = "An oak tree will grow if planted.",
        };

        var result = WriteThenRead(original);

        Assert.Equal(original, result);
    }

    [Fact]
    public void RoundTrips_non_ascii_text()
    {
        var original = new Dictionary<string, string>
        {
            ["Acorn_Name"] = "ドングリ",
            ["Acorn_Description"] = "植えるとオークの木が育つ。",
            ["WithFormatToken"] = "{0}に報告しよう。",
        };

        var result = WriteThenRead(original);

        Assert.Equal(original, result);
    }

    [Fact]
    public void RoundTrips_an_empty_dictionary()
    {
        var original = new Dictionary<string, string>();

        var result = WriteThenRead(original);

        Assert.Empty(result);
    }

    [Fact]
    public void RoundTrips_strings_containing_newlines_and_quotes()
    {
        var original = new Dictionary<string, string>
        {
            ["Multiline"] = "Line one\nLine two\r\nLine three",
            ["Quoted"] = "She said \"hello\" and left.",
            ["Empty"] = "",
        };

        var result = WriteThenRead(original);

        Assert.Equal(original, result);
    }

    [Fact]
    public void Written_file_reports_itself_as_uncompressed()
    {
        using var stream = new MemoryStream();
        XnbStringTable.Write(stream, new Dictionary<string, string> { ["A"] = "B" });
        stream.Position = 0;

        var result = XnbStringTable.Read(stream);

        Assert.False(result.WasCompressed);
    }

    [Fact]
    public void Rejects_a_file_that_is_not_XNB()
    {
        using var stream = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });

        Assert.Throws<InvalidDataException>(() => XnbStringTable.Read(stream));
    }
}
