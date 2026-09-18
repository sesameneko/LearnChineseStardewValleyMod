using XnbStringTool;

namespace XnbStringTool.Tests;

/// <summary>
/// "Golden master" tests against the actual Stardew Valley install on this
/// machine. These are integration tests, not portable unit tests -- they
/// only make sense on a machine with the game installed, which is the only
/// place this tool is ever run. If the game folder isn't found, the
/// assertion below fails with a clear message rather than silently skipping,
/// since on this project's one target machine it is always expected to be
/// present.
/// </summary>
public class RealGameDataTests
{
    private const string StringsDir =
        "/Users/benjaminconnick/Library/Application Support/Steam/steamapps/common/Stardew Valley/Contents/Resources/Content/Strings";

    private static void RequireGameInstalled()
    {
        Assert.True(Directory.Exists(StringsDir),
            $"Expected the Stardew Valley Strings folder at '{StringsDir}'. " +
            "These tests only run on a machine with the game installed at this path.");
    }

    [Fact]
    public void Reads_the_smallest_english_table_exactly()
    {
        RequireGameInstalled();

        using var stream = File.OpenRead(Path.Combine(StringsDir, "Quests.xnb"));
        var result = XnbStringTable.Read(stream);

        Assert.True(result.WasCompressed);
        Assert.Equal(2, result.Entries.Count);
        Assert.Equal("Return to {0}.", result.Entries["ObjectiveReturnToNPC"]);
        Assert.Equal("You found the {0}. Better return it to {1}.", result.Entries["MessageFoundLostItem"]);
    }

    [Fact]
    public void Reads_the_japanese_variant_of_the_same_table()
    {
        RequireGameInstalled();

        using var stream = File.OpenRead(Path.Combine(StringsDir, "Quests.ja-JP.xnb"));
        var result = XnbStringTable.Read(stream);

        Assert.Equal(2, result.Entries.Count);
        Assert.Equal("{0}に報告しよう。", result.Entries["ObjectiveReturnToNPC"]);
        Assert.Equal("{0}を見つけた。{1}に返してあげよう。", result.Entries["MessageFoundLostItem"]);
    }

    [Fact]
    public void Reads_a_large_multi_block_table_without_truncation()
    {
        RequireGameInstalled();

        // StringsFromCSFiles decompresses to well over the 32KB LZX frame
        // size, so this exercises the multi-frame chunk loop, not just a
        // single Decompress() call.
        using var enStream = File.OpenRead(Path.Combine(StringsDir, "StringsFromCSFiles.xnb"));
        var en = XnbStringTable.Read(enStream);

        using var jaStream = File.OpenRead(Path.Combine(StringsDir, "StringsFromCSFiles.ja-JP.xnb"));
        var ja = XnbStringTable.Read(jaStream);

        Assert.True(en.Entries.Count > 1000, $"expected >1000 entries, got {en.Entries.Count}");
        Assert.True(Math.Abs(en.Entries.Count - ja.Entries.Count) < 10,
            $"en ({en.Entries.Count}) and ja ({ja.Entries.Count}) entry counts shouldn't differ by much");
        Assert.Equal("Abandoned JojaMart", en.Entries["AbandonedJojaMart"]);
    }

    [Fact]
    public void Almost_every_english_key_has_a_japanese_counterpart_in_Objects()
    {
        RequireGameInstalled();

        // This is the table M1 (item/object hover tooltips) depends on. The
        // key sets are NOT perfectly 1:1: a handful of templated flavor-text
        // keys (e.g. "Jelly_Flavored_(O)282_Name") exist in en but not ja --
        // a real property of the game data, not a bug in this tool. This
        // confirms that gap is small and known, so the mod's
        // TranslationIndex needs a "no translation available" fallback
        // rather than assuming every key round-trips.
        using var enStream = File.OpenRead(Path.Combine(StringsDir, "Objects.xnb"));
        var en = XnbStringTable.Read(enStream);

        using var jaStream = File.OpenRead(Path.Combine(StringsDir, "Objects.ja-JP.xnb"));
        var ja = XnbStringTable.Read(jaStream);

        var missing = en.Entries.Keys.Where(k => !ja.Entries.ContainsKey(k)).ToList();
        Assert.True(missing.Count <= 5, $"expected only a handful of known gaps, got {missing.Count}: {string.Join(", ", missing.Take(10))}");
    }

    [Fact]
    public void Real_compressed_file_round_trips_through_our_uncompressed_writer()
    {
        RequireGameInstalled();

        using var input = File.OpenRead(Path.Combine(StringsDir, "Objects.ja-JP.xnb"));
        var original = XnbStringTable.Read(input);

        using var rewritten = new MemoryStream();
        XnbStringTable.Write(rewritten, original.Entries);
        rewritten.Position = 0;
        var reread = XnbStringTable.Read(rewritten);

        Assert.Equal(original.Entries, reread.Entries);
    }
}
