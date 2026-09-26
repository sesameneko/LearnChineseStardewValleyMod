using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>
    /// Adds glyphs the game's small font lacks -- the macron vowels romaji needs -- by rebuilding
    /// the font as it loads.
    ///
    /// Hooked on AssetRequested rather than done once at launch because the game loads the font
    /// twice at startup (the base asset, then Fonts/SmallFont.ja-JP when the language is applied)
    /// and again on every language change; each load goes through here and is extended.
    ///
    /// The glyph work itself is in <see cref="FontGlyphSynth"/>; this half reads the atlas back
    /// from the GPU, appends the new glyphs in a strip below it, and builds a new SpriteFont.
    /// Anything going wrong leaves the original font in place, in which case FontSafeText keeps
    /// writing long vowels doubled.
    /// </summary>
    internal static class ExtendedFont
    {
        public static void Register(IModHelper helper)
        {
            helper.Events.Content.AssetRequested += OnAssetRequested;
        }

        private static void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
        {
            if (!e.NameWithoutLocale.IsEquivalentTo("Fonts/SmallFont"))
                return;

            // late, so any other mod's edit to the font is what gets extended
            e.Edit(asset =>
            {
                var font = asset.GetData<SpriteFont>();
                try
                {
                    var extended = Extend(font, out string summary);
                    if (extended is null)
                        return;

                    asset.ReplaceWith(extended);
                    ModEntry.Log($"Extended {e.Name}: {summary}", LogLevel.Trace);
                }
                catch (Exception ex)
                {
                    ModEntry.Log($"Couldn't extend {e.Name}; long vowels will be written doubled. {ex.GetType().Name}: {ex.Message}", LogLevel.Warn);
                }
            }, AssetEditPriority.Late);
        }

        /// <summary>The font with the macron vowels added, or null if it has nothing to add.</summary>
        private static SpriteFont? Extend(SpriteFont font, out string summary)
        {
            summary = "";
            var glyphs = font.GetGlyphs();

            var toAdd = FontGlyphSynth.MacronVowels
                .Where(pair => !glyphs.ContainsKey(pair.Macron) && glyphs.ContainsKey(pair.Base))
                .ToList();
            if (toAdd.Count == 0)
                return null;

            var texture = font.Texture;
            uint[] atlas = ReadPixels(texture);

            var bases = toAdd.ToDictionary(
                pair => pair.Base,
                pair =>
                {
                    var source = glyphs[pair.Base].BoundsInTexture;
                    return FontGlyphSynth.Crop(atlas, texture.Width, source.X, source.Y, source.Width, source.Height);
                });

            // the shapes the bars go over: an i without its dot, but with the bar spanning where
            // the dot was rather than just its stem
            var shapes = new Dictionary<char, GlyphBitmap>();
            var spans = new Dictionary<char, (int Left, int Right)?>();
            foreach (var (baseChar, bitmap) in bases)
            {
                spans[baseChar] = FontGlyphSynth.InkSpan(bitmap);
                shapes[baseChar] = baseChar == 'i' ? FontGlyphSynth.StripDot(bitmap) : bitmap;
            }

            // one weight for every bar, measured on the lowercase vowels
            int thickness = FontGlyphSynth.BarThickness(shapes.Where(b => char.IsLower(b.Key)).Select(b => b.Value));

            // one height per case, in line coordinates: above the tallest vowel of that case
            int LineInkTop(char c) => glyphs[c].Cropping.Y + (FontGlyphSynth.InkTop(shapes[c]) ?? 0);
            var caseTop = shapes.Keys
                .GroupBy(char.IsUpper)
                .ToDictionary(group => group.Key, group => group.Min(LineInkTop));

            var newGlyphs = new List<(char Character, GlyphBitmap Bitmap, SpriteFont.Glyph Base, int ExtraRowsAbove)>();
            foreach (var (macron, baseChar) in toAdd)
            {
                var glyph = glyphs[baseChar];
                int inkTop = caseTop[char.IsUpper(baseChar)] - glyph.Cropping.Y;
                var bitmap = FontGlyphSynth.AddMacron(shapes[baseChar], thickness, out int extra, spans[baseChar], inkTop);
                newGlyphs.Add((macron, bitmap, glyph, extra));
            }

            var (positions, stripHeight) = FontGlyphSynth.PackStrip(newGlyphs.Select(g => (g.Bitmap.Width, g.Bitmap.Height)).ToList(), texture.Width, padding: 1);

            // the original atlas, then the strip below it
            int height = texture.Height + stripHeight;
            var pixels = new Color[texture.Width * height];
            for (int i = 0; i < atlas.Length; i++)
                pixels[i] = new Color(atlas[i]);

            var extendedTexture = new Texture2D(Game1.graphics.GraphicsDevice, texture.Width, height);

            var bounds = new List<Rectangle>();
            var cropping = new List<Rectangle>();
            var characters = new List<char>();
            var kerning = new List<Vector3>();

            foreach (var glyph in glyphs.Values)
                AddGlyph(glyph.Character, glyph.BoundsInTexture, glyph.Cropping, glyph);

            for (int n = 0; n < newGlyphs.Count; n++)
            {
                var (character, bitmap, baseGlyph, extra) = newGlyphs[n];
                var (x, y) = positions[n];
                y += texture.Height;

                for (int row = 0; row < bitmap.Height; row++)
                    for (int col = 0; col < bitmap.Width; col++)
                        pixels[(y + row) * texture.Width + x + col] = new Color(bitmap[col, row]);

                var crop = baseGlyph.Cropping;
                AddGlyph(character, new Rectangle(x, y, bitmap.Width, bitmap.Height), new Rectangle(crop.X, crop.Y - extra, crop.Width, crop.Height), baseGlyph);
            }

            extendedTexture.SetData(pixels);

            // MonoGame builds its character lookup assuming ascending order
            var order = Enumerable.Range(0, characters.Count).OrderBy(i => characters[i]).ToList();

            var extended = new SpriteFont(
                extendedTexture,
                order.Select(i => bounds[i]).ToList(),
                order.Select(i => cropping[i]).ToList(),
                order.Select(i => characters[i]).ToList(),
                font.LineSpacing,
                font.Spacing,
                order.Select(i => kerning[i]).ToList(),
                font.DefaultCharacter);

            summary = $"added {string.Concat(newGlyphs.Select(g => g.Character))} "
                + $"(atlas {texture.Width}x{texture.Height} {texture.Format} -> {texture.Width}x{height} Color)";
            return extended;

            void AddGlyph(char character, Rectangle bound, Rectangle crop, SpriteFont.Glyph metrics)
            {
                characters.Add(character);
                bounds.Add(bound);
                cropping.Add(crop);
                kerning.Add(new Vector3(metrics.LeftSideBearing, metrics.Width, metrics.RightSideBearing));
            }
        }

        /// <summary>The atlas as packed pixels, decoding it if it's compressed.</summary>
        private static uint[] ReadPixels(Texture2D texture)
        {
            switch (texture.Format)
            {
                case SurfaceFormat.Dxt3:
                    // one byte per pixel: 16 bytes per 4x4 block
                    var blocks = new byte[((texture.Width + 3) / 4) * ((texture.Height + 3) / 4) * 16];
                    texture.GetData(blocks);
                    return FontGlyphSynth.DecodeDxt3(blocks, texture.Width, texture.Height);

                case SurfaceFormat.Color:
                    var colors = new Color[texture.Width * texture.Height];
                    texture.GetData(colors);
                    return colors.Select(c => c.PackedValue).ToArray();

                default:
                    throw new NotSupportedException($"The font atlas is {texture.Format}, which can't be read back.");
            }
        }
    }
}
