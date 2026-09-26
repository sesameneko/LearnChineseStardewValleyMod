using System;
using System.Collections.Generic;
using System.Linq;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>
    /// A glyph's pixels, row-major. Each pixel is packed the way MonoGame's <c>Color.PackedValue</c>
    /// is -- R in the low byte, then G, B, and A in the high byte -- so the game side can convert
    /// with <c>new Color(packed)</c> and nothing here has to know about MonoGame.
    /// </summary>
    public sealed record GlyphBitmap(int Width, int Height, uint[] Pixels)
    {
        public uint this[int x, int y] => this.Pixels[y * this.Width + x];

        public static byte Alpha(uint pixel) => (byte)(pixel >> 24);
    }

    /// <summary>
    /// Builds new font glyphs out of a font's existing ones: macron vowels (ā ī ū ē ō) drawn as
    /// the base vowel with a bar over it, so they match the font's own weight and style.
    ///
    /// Deliberately free of StardewValley/MonoGame types so it can be unit-tested; ExtendedFont is
    /// the game-side half that reads the atlas and assembles the new SpriteFont.
    /// </summary>
    public static class FontGlyphSynth
    {
        /// <summary>Each macron vowel and the glyph it is drawn from.</summary>
        public static readonly IReadOnlyList<(char Macron, char Base)> MacronVowels = new[]
        {
            ('ā', 'a'), ('ī', 'i'), ('ū', 'u'), ('ē', 'e'), ('ō', 'o'),
            ('Ā', 'A'), ('Ī', 'I'), ('Ū', 'U'), ('Ē', 'E'), ('Ō', 'O'),
        };

        /// <summary>Alpha at or above which a pixel counts as ink when measuring a glyph's shape.</summary>
        private const byte InkAlpha = 128;

        /// <summary>
        /// Decodes a DXT3 (BC2) texture to packed pixels. The game's font atlases are stored this
        /// way, and GetData on them returns the compressed blocks, not pixels.
        ///
        /// Each 4x4 block is 16 bytes: 4-bit explicit alpha per pixel (8 bytes, low nibble first),
        /// then a colour block of two RGB565 endpoints and 2-bit indices (always the four-colour
        /// mode in DXT3, unlike DXT1).
        /// </summary>
        public static uint[] DecodeDxt3(byte[] data, int width, int height)
        {
            var pixels = new uint[width * height];
            int blocksWide = (width + 3) / 4;
            int blocksHigh = (height + 3) / 4;
            var palette = new uint[4];

            for (int by = 0; by < blocksHigh; by++)
            {
                for (int bx = 0; bx < blocksWide; bx++)
                {
                    int offset = (by * blocksWide + bx) * 16;
                    ulong alphaBits = BitConverter.ToUInt64(data, offset);
                    ushort c0 = BitConverter.ToUInt16(data, offset + 8);
                    ushort c1 = BitConverter.ToUInt16(data, offset + 10);
                    uint indices = BitConverter.ToUInt32(data, offset + 12);

                    var (r0, g0, b0) = Expand565(c0);
                    var (r1, g1, b1) = Expand565(c1);
                    palette[0] = Rgb(r0, g0, b0);
                    palette[1] = Rgb(r1, g1, b1);
                    palette[2] = Rgb((2 * r0 + r1) / 3, (2 * g0 + g1) / 3, (2 * b0 + b1) / 3);
                    palette[3] = Rgb((r0 + 2 * r1) / 3, (g0 + 2 * g1) / 3, (b0 + 2 * b1) / 3);

                    for (int i = 0; i < 16; i++)
                    {
                        int x = bx * 4 + i % 4;
                        int y = by * 4 + i / 4;
                        if (x >= width || y >= height)
                            continue;

                        uint alpha4 = (uint)(alphaBits >> (i * 4)) & 0xF;
                        uint alpha = alpha4 * 17; // 0..15 -> 0..255
                        uint color = palette[(indices >> (i * 2)) & 0x3];
                        pixels[y * width + x] = color | (alpha << 24);
                    }
                }
            }

            return pixels;
        }

        /// <summary>Copies a rectangle out of a packed-pixel atlas.</summary>
        public static GlyphBitmap Crop(uint[] atlas, int atlasWidth, int x, int y, int width, int height)
        {
            var pixels = new uint[width * height];
            for (int row = 0; row < height; row++)
                Array.Copy(atlas, (y + row) * atlasWidth + x, pixels, row * width, width);
            return new GlyphBitmap(width, height, pixels);
        }

        /// <summary>
        /// Removes the tittle from an i: all ink above the first fully empty row that has ink both
        /// above and below it. A glyph with no such gap (I, or an i drawn without a dot) is
        /// returned unchanged.
        /// </summary>
        public static GlyphBitmap StripDot(GlyphBitmap glyph)
        {
            bool seenInk = false;
            for (int y = 0; y < glyph.Height; y++)
            {
                bool rowHasInk = RowHasInk(glyph, y);
                if (rowHasInk)
                {
                    seenInk = true;
                    continue;
                }

                if (!seenInk || !Enumerable.Range(y + 1, glyph.Height - y - 1).Any(below => RowHasInk(glyph, below)))
                    continue;

                // y is the gap: clear everything above it
                var pixels = (uint[])glyph.Pixels.Clone();
                Array.Clear(pixels, 0, y * glyph.Width);
                return glyph with { Pixels = pixels };
            }

            return glyph;
        }

        /// <summary>
        /// Draws a macron over a glyph: a bar spanning its ink, as thick as its top stroke, one empty
        /// row above its topmost ink. The bitmap grows upward only if the bar doesn't fit in the
        /// empty rows it already has; <paramref name="extraRowsAbove"/> is how much, which the
        /// caller subtracts from the glyph's vertical offset so the base letter stays where it was.
        /// A glyph with no ink is returned unchanged.
        /// </summary>
        public static GlyphBitmap AddMacron(GlyphBitmap glyph, out int extraRowsAbove)
        {
            extraRowsAbove = 0;
            if (!TryInkBounds(glyph, out int left, out int top, out int right, out _))
                return glyph;

            int thickness = StrokeThickness(glyph);
            const int gap = 1;
            int barTop = top - gap - thickness;
            extraRowsAbove = Math.Max(0, -barTop);
            barTop += extraRowsAbove;

            int height = glyph.Height + extraRowsAbove;
            var pixels = new uint[glyph.Width * height];
            Array.Copy(glyph.Pixels, 0, pixels, extraRowsAbove * glyph.Width, glyph.Pixels.Length);

            uint ink = glyph.Pixels.MaxBy(Alpha);
            for (int y = barTop; y < barTop + thickness; y++)
                for (int x = left; x <= right; x++)
                    pixels[y * glyph.Width + x] = ink;

            return new GlyphBitmap(glyph.Width, height, pixels);
        }

        /// <summary>
        /// The thickness of a glyph's topmost stroke, which is what the bar sits parallel to: the
        /// smaller of the narrowest horizontal ink run in the top ink row and the narrowest
        /// vertical run starting from it. The first catches the arms of an open-topped u, whose
        /// vertical runs are the whole letter; the second the curve of an o, whose top row is
        /// one long horizontal run.
        /// </summary>
        public static int StrokeThickness(GlyphBitmap glyph)
        {
            if (!TryInkBounds(glyph, out _, out int top, out _, out _))
                return 1;

            int thinnest = int.MaxValue, run = 0;
            for (int x = 0; x <= glyph.Width; x++)
            {
                if (x < glyph.Width && Alpha(glyph[x, top]) >= InkAlpha)
                {
                    run++;

                    int down = 0;
                    while (top + down < glyph.Height && Alpha(glyph[x, top + down]) >= InkAlpha)
                        down++;
                    thinnest = Math.Min(thinnest, down);
                }
                else if (run > 0)
                {
                    thinnest = Math.Min(thinnest, run);
                    run = 0;
                }
            }

            return Math.Max(1, thinnest);
        }

        /// <summary>
        /// Shelf-packs glyphs of the given sizes into a strip the width of the atlas, left to right
        /// and wrapping to a new shelf when a row fills, with <paramref name="padding"/> pixels
        /// between and around them so sampling never bleeds from a neighbour. Returns each glyph's
        /// position relative to the strip's top-left and the strip's total height.
        /// </summary>
        public static (IReadOnlyList<(int X, int Y)> Positions, int Height) PackStrip(IReadOnlyList<(int Width, int Height)> sizes, int stripWidth, int padding)
        {
            var positions = new List<(int X, int Y)>(sizes.Count);
            int x = padding, y = padding, shelfHeight = 0;

            foreach (var (width, height) in sizes)
            {
                if (width + 2 * padding > stripWidth)
                    throw new ArgumentException($"A {width}px glyph doesn't fit a {stripWidth}px strip.");

                if (x + width + padding > stripWidth)
                {
                    x = padding;
                    y += shelfHeight + padding;
                    shelfHeight = 0;
                }

                positions.Add((x, y));
                x += width + padding;
                shelfHeight = Math.Max(shelfHeight, height);
            }

            return (positions, positions.Count == 0 ? 0 : y + shelfHeight + padding);
        }

        private static byte Alpha(uint pixel) => GlyphBitmap.Alpha(pixel);

        private static bool RowHasInk(GlyphBitmap glyph, int y)
        {
            for (int x = 0; x < glyph.Width; x++)
                if (Alpha(glyph[x, y]) >= InkAlpha)
                    return true;
            return false;
        }

        private static bool TryInkBounds(GlyphBitmap glyph, out int left, out int top, out int right, out int bottom)
        {
            left = top = int.MaxValue;
            right = bottom = -1;
            for (int y = 0; y < glyph.Height; y++)
            {
                for (int x = 0; x < glyph.Width; x++)
                {
                    if (Alpha(glyph[x, y]) < InkAlpha)
                        continue;
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }

            return right >= 0;
        }

        private static (int R, int G, int B) Expand565(ushort c)
        {
            int r = (c >> 11) & 0x1F, g = (c >> 5) & 0x3F, b = c & 0x1F;
            return ((r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2));
        }

        private static uint Rgb(int r, int g, int b) => (uint)(r | (g << 8) | (b << 16));
    }
}
