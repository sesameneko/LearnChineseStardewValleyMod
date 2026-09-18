using LanguageStudyStardewValleyMod;

namespace ModLogic.Tests
{
    public class TooltipLayoutTests
    {
        private const int ViewportWidth = 1280;
        private const int ViewportHeight = 720;

        [Fact]
        public void Places_the_translation_above_the_vanilla_tooltip_when_there_is_room()
        {
            var original = new TooltipBox(400, 300, 200, 100);

            var placed = TooltipLayout.Place(original, 180, 60, ViewportWidth, ViewportHeight);

            Assert.Equal(400, placed.X);
            Assert.Equal(300 - 60 - TooltipLayout.Gap, placed.Y);
            Assert.True(placed.Y + placed.Height <= original.Y);
        }

        [Fact]
        public void Falls_back_to_below_the_vanilla_tooltip_when_there_is_no_room_above()
        {
            var original = new TooltipBox(400, 10, 200, 100);

            var placed = TooltipLayout.Place(original, 180, 60, ViewportWidth, ViewportHeight);

            Assert.Equal(10 + 100 + TooltipLayout.Gap, placed.Y);
            Assert.True(placed.Y >= original.Y + original.Height);
        }

        [Fact]
        public void Clamps_into_the_viewport_when_it_fits_neither_above_nor_below()
        {
            var original = new TooltipBox(0, 0, 200, ViewportHeight);

            var placed = TooltipLayout.Place(original, 180, 60, ViewportWidth, ViewportHeight);

            Assert.True(placed.Y >= 0);
            Assert.True(placed.Y + placed.Height <= ViewportHeight);
        }

        [Fact]
        public void Clamps_a_wide_box_back_inside_the_right_edge()
        {
            var original = new TooltipBox(ViewportWidth - 100, 300, 100, 80);

            var placed = TooltipLayout.Place(original, 400, 60, ViewportWidth, ViewportHeight);

            Assert.Equal(ViewportWidth - 400, placed.X);
        }

        [Fact]
        public void Never_leaves_a_box_off_the_left_edge_even_when_it_is_wider_than_the_screen()
        {
            var placed = TooltipLayout.Place(new TooltipBox(0, 300, 100, 80), ViewportWidth + 200, 60, ViewportWidth, ViewportHeight);

            Assert.Equal(0, placed.X);
        }

        [Fact]
        public void Overlay_text_puts_the_translated_title_above_the_translated_body()
        {
            string text = TooltipLayout.ComposeOverlayText("Acorn", "An oak tree grows if you plant it.");

            Assert.Equal("Acorn" + System.Environment.NewLine + "An oak tree grows if you plant it.", text);
        }

        [Theory]
        [InlineData("Acorn", null, "Acorn")]
        [InlineData(null, "A description.", "A description.")]
        [InlineData(null, null, "")]
        [InlineData("  ", "  ", "")]
        public void Overlay_text_degrades_to_whichever_half_was_translatable(string? title, string? body, string expected)
        {
            Assert.Equal(expected, TooltipLayout.ComposeOverlayText(title, body));
        }
    }
}
