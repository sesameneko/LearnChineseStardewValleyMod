using System;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>A screen-space rectangle, in pixels.</summary>
    public readonly record struct TooltipBox(int X, int Y, int Width, int Height)
    {
        /// <summary>
        /// Whether a point is inside the box. The point has to be in the same space the box was
        /// captured in, i.e. UI pixels -- so hit-test with Game1.getMouseX(ui_scale: true), not the
        /// no-arg overload, which picks its space from Game1.uiMode.
        /// </summary>
        public bool Contains(float x, float y)
        {
            return x >= this.X && x < this.X + this.Width
                && y >= this.Y && y < this.Y + this.Height;
        }
    }

    /// <summary>
    /// Placement math for the translation tooltip that gets stacked alongside the vanilla one.
    ///
    /// Kept free of StardewValley/MonoGame types so it's unit-testable without launching the game
    /// (see tools/ModLogic.Tests); <see cref="TooltipOverlay"/> is the game-side half that measures
    /// the text and actually draws the box.
    /// </summary>
    public static class TooltipLayout
    {
        /// <summary>The gap left between the vanilla tooltip and ours.</summary>
        public const int Gap = 8;

        /// <summary>
        /// Places a box of the given size relative to the vanilla tooltip: above it by preference
        /// (the vanilla one normally hangs below-right of the cursor, so above keeps our box on the
        /// opposite side of the cursor), below it when there's no room above, and clamped into the
        /// viewport when there's no room either way.
        /// </summary>
        public static TooltipBox Place(TooltipBox original, int width, int height, int viewportWidth, int viewportHeight)
        {
            int y = original.Y - height - Gap;
            if (y < 0)
                y = original.Y + original.Height + Gap;

            if (y + height > viewportHeight)
                y = viewportHeight - height;
            if (y < 0)
                y = 0;

            int x = original.X;
            if (x + width > viewportWidth)
                x = viewportWidth - width;
            if (x < 0)
                x = 0;

            return new TooltipBox(x, y, width, height);
        }

        /// <summary>
        /// Combines the vanilla tooltip's title and body translations into the text of our box.
        /// Returns an empty string when neither could be translated, which is the caller's signal to
        /// draw nothing at all.
        /// </summary>
        public static string ComposeOverlayText(string? translatedTitle, string? translatedBody)
        {
            bool hasTitle = !string.IsNullOrWhiteSpace(translatedTitle);
            bool hasBody = !string.IsNullOrWhiteSpace(translatedBody);

            if (hasTitle && hasBody)
                return translatedTitle + Environment.NewLine + translatedBody;
            if (hasTitle)
                return translatedTitle!;
            if (hasBody)
                return translatedBody!;

            return "";
        }
    }
}
