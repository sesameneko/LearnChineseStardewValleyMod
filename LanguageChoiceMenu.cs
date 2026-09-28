using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>
    /// A message above a column of buttons, one per option. It's the title-screen chooser
    /// <see cref="LanguagePrompt"/> shows when several copies of the mod are installed. The game's
    /// <see cref="ConfirmationDialog"/> only does yes/no, so this follows its look instead: a dimmed
    /// screen and a centred box.
    ///
    /// Shown as <c>TitleMenu.subMenu</c>. The title menu forwards input to it and draws it, so it
    /// needs none of the overlay passes.
    /// </summary>
    internal sealed class LanguageChoiceMenu : IClickableMenu
    {
        private const int Padding = 48;
        private const int ButtonHeight = 72;
        private const int ButtonGap = 16;
        private const int FirstButtonId = 7300;

        /// <summary>The button texture the game's options page uses.</summary>
        private static readonly Rectangle ButtonSource = new(432, 439, 9, 9);

        private readonly string message;
        private readonly IReadOnlyList<(string Label, Action Choose)> options;
        private readonly List<ClickableComponent> buttons = new();

        private string wrappedMessage = "";
        private int messageHeight;
        private int hovered = -1;

        /// <param name="options">The buttons, top to bottom. The last is also what Escape does.</param>
        public LanguageChoiceMenu(string message, IReadOnlyList<(string Label, Action Choose)> options)
        {
            this.message = message;
            this.options = options;
            this.Layout();

            if (Game1.options.SnappyMenus)
                this.snapToDefaultClickableComponent();
        }

        private void Layout()
        {
            this.width = Math.Min(800, Game1.uiViewport.Width - 64);
            this.wrappedMessage = Game1.parseText(this.message, Game1.dialogueFont, this.width - Padding * 2);
            this.messageHeight = (int)Game1.dialogueFont.MeasureString(this.wrappedMessage).Y;

            this.height = Padding + this.messageHeight + Padding / 2
                + this.options.Count * (ButtonHeight + ButtonGap) - ButtonGap
                + Padding;
            this.xPositionOnScreen = (Game1.uiViewport.Width - this.width) / 2;
            this.yPositionOnScreen = (Game1.uiViewport.Height - this.height) / 2;

            this.buttons.Clear();
            int top = this.yPositionOnScreen + Padding + this.messageHeight + Padding / 2;
            for (int i = 0; i < this.options.Count; i++)
            {
                var bounds = new Rectangle(this.xPositionOnScreen + Padding, top + i * (ButtonHeight + ButtonGap), this.width - Padding * 2, ButtonHeight);
                this.buttons.Add(new ClickableComponent(bounds, this.options[i].Label)
                {
                    myID = FirstButtonId + i,
                    upNeighborID = i > 0 ? FirstButtonId + i - 1 : -1,
                    downNeighborID = i < this.options.Count - 1 ? FirstButtonId + i + 1 : -1,
                });
            }

            this.allClickableComponents = this.buttons.ToList();
        }

        public override void snapToDefaultClickableComponent()
        {
            this.currentlySnappedComponent = this.buttons.FirstOrDefault();
            this.snapCursorToCurrentSnappedComponent();
        }

        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            this.Layout();
            if (Game1.options.SnappyMenus)
                this.snapToDefaultClickableComponent();
        }

        public override void performHoverAction(int x, int y)
        {
            this.hovered = this.buttons.FindIndex(button => button.containsPoint(x, y));
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            int index = this.buttons.FindIndex(button => button.containsPoint(x, y));
            if (index < 0)
                return;

            if (playSound)
                Game1.playSound("smallSelect");
            this.options[index].Choose();
        }

        public override void receiveKeyPress(Keys key)
        {
            // not base: its menu-key handling would exit the title menu itself, not just this
            if (key == Keys.Escape || Game1.options.doesInputListContain(Game1.options.menuButton, key))
                this.options[^1].Choose();
        }

        public override void draw(SpriteBatch b)
        {
            b.Draw(Game1.fadeToBlackRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), Color.Black * 0.5f);
            drawTextureBox(b, this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height, Color.White);

            Utility.drawTextWithShadow(b, this.wrappedMessage, Game1.dialogueFont, new Vector2(this.xPositionOnScreen + Padding, this.yPositionOnScreen + Padding), Game1.textColor);

            for (int i = 0; i < this.buttons.Count; i++)
            {
                var bounds = this.buttons[i].bounds;
                drawTextureBox(b, Game1.mouseCursors, ButtonSource, bounds.X, bounds.Y, bounds.Width, bounds.Height, i == this.hovered ? Color.Wheat : Color.White, 4f, false);

                string label = this.options[i].Label;
                var size = Game1.dialogueFont.MeasureString(label);
                var position = new Vector2(bounds.Center.X - size.X / 2, bounds.Center.Y - size.Y / 2 + 4);
                Utility.drawTextWithShadow(b, label, Game1.dialogueFont, position, Game1.textColor);
            }

            this.drawMouse(b);
        }
    }
}
