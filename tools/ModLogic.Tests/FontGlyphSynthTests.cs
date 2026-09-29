using LanguageStudyStardewValleyMod;

namespace ModLogic.Tests;

public class FontGlyphSynthTests
{
    private const uint Ink = 0xFFFFFFFF;

    /// <summary>A bitmap from rows of '#' (opaque white) and '.' (transparent).</summary>
    private static GlyphBitmap Art(params string[] rows) =>
        new(rows[0].Length, rows.Length, rows.SelectMany(row => row.Select(c => c == '#' ? Ink : 0u)).ToArray());

    private static string[] Render(GlyphBitmap glyph) =>
        Enumerable.Range(0, glyph.Height)
            .Select(y => new string(Enumerable.Range(0, glyph.Width).Select(x => GlyphBitmap.Alpha(glyph[x, y]) >= 128 ? '#' : '.').ToArray()))
            .ToArray();

    // a two-pixel-stroke o with room above it, as lowercase glyphs have in the game's font
    private static readonly GlyphBitmap O = Art(
        "......",
        "......",
        "......",
        ".####.",
        "######",
        "##..##",
        "######",
        ".####.");

    [Fact]
    public void MacronSitsOneRowAboveTheInkInsetAPixel()
    {
        var result = FontGlyphSynth.AddMacron(O, 2, out int extra);

        Assert.Equal(0, extra);
        Assert.Equal(new[]
        {
            ".####.",
            ".####.",
            "......",
            ".####.",
            "######",
            "##..##",
            "######",
            ".####.",
        }, Render(result));
    }

    [Fact]
    public void GrowsUpwardWhenThereIsNoRoom()
    {
        var capital = Art(
            "#..#",
            "#..#",
            "####");

        var result = FontGlyphSynth.AddMacron(capital, 1, out int extra);

        Assert.Equal(2, extra); // one-pixel stroke + one-row gap
        Assert.Equal(new[] { "####", "....", "#..#", "#..#", "####" }, Render(result));
    }

    [Fact]
    public void InkTopOverridePlacesTheBarAtASharedHeight()
    {
        var shortU = Art(
            "....",
            "....",
            "#..#",
            "####");

        // a taller sibling's ink starts two rows higher, at this bitmap's row 0
        var result = FontGlyphSynth.AddMacron(shortU, 1, out int extra, inkTop: 0);

        Assert.Equal(2, extra);
        Assert.Equal(new[] { "####", "....", "....", "....", "#..#", "####" }, Render(result));
    }

    [Fact]
    public void BarTakesTheGlyphsMostOpaqueInk()
    {
        var glyph = new GlyphBitmap(2, 3, new uint[] { 0, 0, 0, 0, 0x80FFFFFF, 0xF0EEEEEE });

        var result = FontGlyphSynth.AddMacron(glyph, 1, out _);

        Assert.Equal(0xF0EEEEEEu, result[0, 0]);
        Assert.Equal(0xF0EEEEEEu, result[1, 0]);
    }

    [Fact]
    public void EmptyGlyphIsUnchanged()
    {
        var empty = Art("...", "...");

        Assert.Same(empty, FontGlyphSynth.AddMacron(empty, 1, out int extra));
        Assert.Equal(0, extra);
    }

    [Fact]
    public void StripDotRemovesTheTittleOnly()
    {
        var i = Art(
            ".##.",
            "....",
            ".##.",
            ".##.",
            "####");

        Assert.Equal(new[] { "....", "....", ".##.", ".##.", "####" }, Render(FontGlyphSynth.StripDot(i)));
    }

    [Fact]
    public void BarOverADotlessIUsesTheGivenSpan()
    {
        var i = Art(
            "####",
            "....",
            "....",
            "....",
            ".#..",
            ".#..");

        var span = FontGlyphSynth.InkSpan(i);
        var result = FontGlyphSynth.AddMacron(FontGlyphSynth.StripDot(i), 1, out int extra, span);

        Assert.Equal(0, extra);
        Assert.Equal(new[] { "....", "....", "####", "....", ".#..", ".#.." }, Render(result));
    }

    [Fact]
    public void FlipAccentTurnsACircumflexIntoACaron()
    {
        // the zh font's â, simplified: a faint row above the accent, then the gap
        var circumflex = Art(
            "......",
            "..##..",
            ".#..#.",
            "#....#",
            "......",
            ".####.",
            "#...##",
            ".#####");

        var result = FontGlyphSynth.FlipAccent(circumflex);

        Assert.NotNull(result);
        Assert.Equal(new[]
        {
            "......",
            "#....#",
            ".#..#.",
            "..##..",
            "......",
            ".####.",
            "#...##",
            ".#####",
        }, Render(result!));
    }

    [Fact]
    public void FlipAccentCarriesTheFaintEdgesWithIt()
    {
        // alpha below the ink threshold: the antialiased tip above the accent must end up below it
        const uint Faint = 0x40FFFFFF;
        var glyph = new GlyphBitmap(1, 5, new[] { Faint, Ink, 0u, 0u, Ink });

        var result = FontGlyphSynth.FlipAccent(glyph)!;

        Assert.Equal(new[] { 0u, Ink, Faint, 0u, Ink }, result.Pixels);
    }

    [Fact]
    public void FlipAccentNeedsAGapBetweenAccentAndLetter()
    {
        var joined = Art(".##.", "#..#", "####");

        Assert.Null(FontGlyphSynth.FlipAccent(joined));
    }

    [Fact]
    public void BarThicknessIsTheMedianAcrossGlyphs()
    {
        var thin = Art(".##.", "#..#", ".##.");
        var thick = Art("####", "####", "#..#");

        Assert.Equal(2, FontGlyphSynth.BarThickness(new[] { thin, thick, thick }));
    }

    [Fact]
    public void StripDotLeavesAGlyphWithoutAGapAlone()
    {
        var capitalI = Art("###", ".#.", "###");

        Assert.Same(capitalI, FontGlyphSynth.StripDot(capitalI));
    }

    [Fact]
    public void StrokeThicknessOfAClosedTopIsItsVerticalRun()
    {
        var e = Art(
            ".####.",
            "##..##",
            "######",
            "##....",
            ".####.");

        Assert.Equal(1, FontGlyphSynth.StrokeThickness(e));
    }

    [Fact]
    public void StrokeThicknessOfAnOpenTopIsItsArmWidth()
    {
        var u = Art(
            "##..##",
            "##..##",
            "##..##",
            ".####.");

        Assert.Equal(2, FontGlyphSynth.StrokeThickness(u));
    }

    [Fact]
    public void PackStripWrapsOntoANewShelf()
    {
        var (positions, height) = FontGlyphSynth.PackStrip(new[] { (4, 5), (4, 3), (4, 6) }, stripWidth: 12, padding: 1);

        Assert.Equal(new[] { (1, 1), (6, 1), (1, 7) }, positions);
        Assert.Equal(14, height);
    }

    [Fact]
    public void DecodesADxt3Block()
    {
        // alpha: pixel 0 = 0xF, pixel 1 = 0x8, rest 0; colours: c0 white, c1 black; indices:
        // pixel 0 -> c0, pixel 1 -> c1, pixel 2 -> 2/3 white, pixel 3 -> 1/3 white
        var block = new byte[16];
        block[0] = 0x8F;
        block[8] = 0xFF; block[9] = 0xFF;           // c0 = 0xFFFF
        block[10] = 0x00; block[11] = 0x00;         // c1 = 0x0000
        block[12] = 0b11_10_01_00;                  // indices for pixels 0..3

        var pixels = FontGlyphSynth.DecodeDxt3(block, 4, 4);

        Assert.Equal(0xFFFFFFFFu, pixels[0]);
        Assert.Equal(0x88000000u, pixels[1]);
        Assert.Equal(0x00AAAAAAu, pixels[2]);
        Assert.Equal(0x00555555u, pixels[3]);
    }

    [Fact]
    public void CropCopiesTheRectangle()
    {
        var atlas = Enumerable.Range(0, 16).Select(n => (uint)n).ToArray(); // 4x4

        var glyph = FontGlyphSynth.Crop(atlas, 4, 1, 2, 2, 2);

        Assert.Equal(new uint[] { 9, 10, 13, 14 }, glyph.Pixels);
    }
}
