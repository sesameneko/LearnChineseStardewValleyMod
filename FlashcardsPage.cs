using System;
using System.Collections.Generic;
using System.Linq;
using LanguageStudyStardewValleyMod.Patches;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>
    /// The pause-menu flashcards tab (Plan.md M6), added to <see cref="GameMenu"/> by
    /// <see cref="GameMenuPatches"/>. Two views:
    ///
    /// - Review: one card at a time, Japanese word on the front; flip for kana, romaji, meanings,
    ///   and the sentences it was saved from, then mark it missed or known. Nothing is scheduled --
    ///   the answer only counts -- so a pass is every card once, in the order picked at the top.
    /// - Browse: every card in a scrolling list with its counts; click one for its back, delete it.
    ///
    /// Word hover is off in here: the tab's text isn't recorded, so there's nothing to hover or
    /// click-save, and a click always means the tab's own buttons.
    /// </summary>
    public sealed class FlashcardsPage : IClickableMenu
    {
        private enum View { Review, Browse }

        private View view = View.Review;

        private ReviewSession session = null!;

        /// <summary>The card whose back the browse view is showing; null for the list.</summary>
        private Flashcard? browsing;

        /// <summary>The first list row shown.</summary>
        private int scrollRow;

        /// <summary>The card whose delete button has been clicked once and is asking to be confirmed.</summary>
        private Flashcard? confirmingDelete;

        /// <summary>The laid-out back of the card last drawn, rebuilt only when the card changes.</summary>
        private (Flashcard Card, int Width, List<ContextBlock> Blocks)? backLayout;

        private readonly ClickableComponent reviewTab;
        private readonly ClickableComponent browseTab;
        private readonly ClickableComponent orderButton;
        private readonly ClickableComponent primaryButton;
        private readonly ClickableComponent secondaryButton;
        private readonly ClickableTextureComponent upArrow;
        private readonly ClickableTextureComponent downArrow;

        private const int RowHeight = 56;
        private const int ButtonHeight = 56;
        private const int MaxContextsShown = 2;

        private static readonly Color MutedText = new(110, 80, 50);
        private static readonly Color FaintText = new(150, 125, 95);
        private static readonly Color HighlightText = new(180, 40, 30);
        private static readonly Color DeleteTint = new(255, 150, 150);
        private static readonly Color CardFill = new(250, 246, 236);

        private string Language => ModEntry.Instance.Config.SourceLanguage;

        public FlashcardsPage(int x, int y, int width, int height)
            : base(x, y, width, height)
        {
            this.reviewTab = new ClickableComponent(Rectangle.Empty, "review");
            this.browseTab = new ClickableComponent(Rectangle.Empty, "browse");
            this.orderButton = new ClickableComponent(Rectangle.Empty, "order");
            this.primaryButton = new ClickableComponent(Rectangle.Empty, "primary");
            this.secondaryButton = new ClickableComponent(Rectangle.Empty, "secondary");
            this.upArrow = new ClickableTextureComponent(Rectangle.Empty, Game1.mouseCursors, new Rectangle(421, 459, 11, 12), 4f);
            this.downArrow = new ClickableTextureComponent(Rectangle.Empty, Game1.mouseCursors, new Rectangle(421, 472, 11, 12), 4f);

            this.Layout();
            this.RestartSession();
        }

        #region layout

        private Rectangle content;
        private Rectangle cardArea;

        /// <summary>Places every control relative to the menu box; rerun when the window resizes.</summary>
        private void Layout()
        {
            int left = this.xPositionOnScreen + spaceToClearSideBorder + borderWidth;
            int top = this.yPositionOnScreen + spaceToClearTopBorder + 16;
            int right = this.xPositionOnScreen + this.width - spaceToClearSideBorder - borderWidth;
            int bottom = this.yPositionOnScreen + this.height - borderWidth - 16;
            this.content = new Rectangle(left, top, right - left, bottom - top);

            this.reviewTab.bounds = new Rectangle(left, top, 160, ButtonHeight);
            this.browseTab.bounds = new Rectangle(left + 172, top, 160, ButtonHeight);
            this.orderButton.bounds = new Rectangle(right - 300, top, 300, ButtonHeight);

            int buttonsTop = bottom - ButtonHeight;
            this.cardArea = new Rectangle(left, top + ButtonHeight + 16, right - left, buttonsTop - 16 - (top + ButtonHeight + 16));
            this.secondaryButton.bounds = new Rectangle(left, buttonsTop, 260, ButtonHeight);
            this.primaryButton.bounds = new Rectangle(right - 260, buttonsTop, 260, ButtonHeight);

            this.upArrow.bounds = new Rectangle(right - 44, this.cardArea.Top, 44, 48);
            this.downArrow.bounds = new Rectangle(right - 44, this.cardArea.Bottom - 48, 44, 48);
        }

        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            base.gameWindowSizeChanged(oldBounds, newBounds);
            this.Layout();
            this.backLayout = null;
        }

        private int VisibleRows => Math.Max(1, this.cardArea.Height / RowHeight);

        #endregion

        #region state

        private void RestartSession()
        {
            this.session = new ReviewSession(FlashcardStore.Deck.Ordered(this.Language, ModEntry.Instance.Config.FlashcardOrder));
        }

        private List<Flashcard> ListedCards() => FlashcardStore.Deck.Ordered(this.Language, ModEntry.Instance.Config.FlashcardOrder);

        private void CycleOrder()
        {
            var config = ModEntry.Instance.Config;
            config.FlashcardOrder = config.FlashcardOrder switch
            {
                CardOrder.NewestFirst => CardOrder.OldestFirst,
                CardOrder.OldestFirst => CardOrder.FewestCorrect,
                _ => CardOrder.NewestFirst,
            };
            ModEntry.Instance.SaveConfig();
            this.RestartSession();
            this.scrollRow = 0;
        }

        private static string OrderLabel(CardOrder order) => order switch
        {
            CardOrder.NewestFirst => "Order: Newest",
            CardOrder.OldestFirst => "Order: Oldest",
            _ => "Order: Fewest correct",
        };

        private void Grade(bool passed)
        {
            if (this.session.Grade(passed))
            {
                Game1.playSound(passed ? "coin" : "smallSelect");
                FlashcardStore.Save();
            }
        }

        private void Flip()
        {
            if (this.session.Current is null)
                return;
            this.session.Flip();
            Game1.playSound("shwip");
        }

        private void Delete(Flashcard card)
        {
            FlashcardStore.Deck.Delete(card);
            FlashcardStore.Save();
            this.session.Remove(card);
            this.confirmingDelete = null;
            if (this.browsing == card)
                this.browsing = null;
            Game1.playSound("trashcan");
        }

        private void Scroll(int rows)
        {
            int max = Math.Max(0, this.ListedCards().Count - this.VisibleRows);
            int next = Math.Clamp(this.scrollRow + rows, 0, max);
            if (next != this.scrollRow)
                Game1.playSound("shiny4");
            this.scrollRow = next;
        }

        #endregion

        #region input

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (this.reviewTab.containsPoint(x, y))
            {
                this.view = View.Review;
                this.confirmingDelete = null;
                Game1.playSound("smallSelect");
                return;
            }

            if (this.browseTab.containsPoint(x, y))
            {
                this.view = View.Browse;
                this.browsing = null;
                this.confirmingDelete = null;
                Game1.playSound("smallSelect");
                return;
            }

            if (this.orderButton.containsPoint(x, y))
            {
                this.CycleOrder();
                Game1.playSound("drumkit6");
                return;
            }

            if (this.view == View.Review)
                this.ClickReview(x, y);
            else
                this.ClickBrowse(x, y);
        }

        private void ClickReview(int x, int y)
        {
            if (this.session.Count == 0)
                return;

            if (this.session.IsFinished)
            {
                if (this.primaryButton.containsPoint(x, y))
                {
                    this.RestartSession();
                    Game1.playSound("smallSelect");
                }
                return;
            }

            if (!this.session.IsFlipped)
            {
                if (this.cardArea.Contains(x, y) || this.primaryButton.containsPoint(x, y))
                    this.Flip();
                return;
            }

            if (this.primaryButton.containsPoint(x, y))
                this.Grade(passed: true);
            else if (this.secondaryButton.containsPoint(x, y))
                this.Grade(passed: false);
        }

        private void ClickBrowse(int x, int y)
        {
            if (this.browsing is { } card)
            {
                if (this.secondaryButton.containsPoint(x, y))
                {
                    this.browsing = null;
                    this.confirmingDelete = null;
                    Game1.playSound("smallSelect");
                }
                else if (this.primaryButton.containsPoint(x, y))
                {
                    if (this.confirmingDelete == card)
                        this.Delete(card);
                    else
                    {
                        this.confirmingDelete = card;
                        Game1.playSound("smallSelect");
                    }
                }
                return;
            }

            if (this.upArrow.containsPoint(x, y))
            {
                this.Scroll(-1);
                return;
            }

            if (this.downArrow.containsPoint(x, y))
            {
                this.Scroll(1);
                return;
            }

            var cards = this.ListedCards();
            for (int row = 0; row < this.VisibleRows && this.scrollRow + row < cards.Count; row++)
            {
                var rowCard = cards[this.scrollRow + row];
                Rectangle bounds = this.RowBounds(row);
                if (!bounds.Contains(x, y))
                    continue;

                if (this.DeleteBounds(bounds).Contains(x, y))
                {
                    if (this.confirmingDelete == rowCard)
                        this.Delete(rowCard);
                    else
                    {
                        this.confirmingDelete = rowCard;
                        Game1.playSound("smallSelect");
                    }
                    return;
                }

                this.browsing = rowCard;
                this.confirmingDelete = null;
                Game1.playSound("smallSelect");
                return;
            }

            this.confirmingDelete = null;
        }

        public override void receiveScrollWheelAction(int direction)
        {
            if (this.view == View.Browse && this.browsing is null)
                this.Scroll(direction > 0 ? -1 : 1);
        }

        public override void receiveKeyPress(Keys key)
        {
            if (this.view == View.Review)
            {
                switch (key)
                {
                    case Keys.Space:
                        if (this.session.IsFinished && this.session.Count > 0)
                            this.RestartSession();
                        else
                            this.Flip();
                        return;
                    case Keys.D1:
                    case Keys.NumPad1:
                        this.Grade(passed: false);
                        return;
                    case Keys.D2:
                    case Keys.NumPad2:
                        this.Grade(passed: true);
                        return;
                }
            }
            else if (this.browsing is null)
            {
                if (key == Keys.Up)
                    this.Scroll(-1);
                else if (key == Keys.Down)
                    this.Scroll(1);
                else if (key == Keys.PageUp)
                    this.Scroll(-this.VisibleRows);
                else if (key == Keys.PageDown)
                    this.Scroll(this.VisibleRows);
            }
            else if (key == Keys.Back)
                this.browsing = null;
        }

        #endregion

        #region drawing

        public override void draw(SpriteBatch b)
        {
            // nothing drawn here is recorded for word hover: the tab's own clicks always win
            using (TextCapturePatches.SuppressRecording())
            {
                this.DrawButton(b, this.reviewTab.bounds, "Review", selected: this.view == View.Review);
                this.DrawButton(b, this.browseTab.bounds, "Browse", selected: this.view == View.Browse);
                this.DrawButton(b, this.orderButton.bounds, OrderLabel(ModEntry.Instance.Config.FlashcardOrder));

                if (this.view == View.Review)
                    this.DrawReview(b);
                else
                    this.DrawBrowse(b);
            }
        }

        private void DrawReview(SpriteBatch b)
        {
            if (this.session.Count == 0)
            {
                this.DrawEmpty(b);
                return;
            }

            if (this.session.Current is not { } card)
            {
                DrawPanel(b, this.cardArea);
                string done = $"Pass complete: {this.session.PassedThisSession} of {this.session.Count} known.";
                DrawCentered(b, done, Game1.dialogueFont, this.cardArea.Center.Y - 40, this.cardArea, Game1.textColor);
                DrawCentered(b, "Space or the button below starts another pass.", Game1.smallFont, this.cardArea.Center.Y + 20, this.cardArea, MutedText);
                this.DrawButton(b, this.primaryButton.bounds, "Start again");
                return;
            }

            DrawPanel(b, this.cardArea, fill: CardFill);

            string progress = $"{this.session.Position + 1} / {this.session.Count}";
            Utility.drawTextWithShadow(b, progress, Game1.smallFont, new Vector2(this.cardArea.Right - 24 - Game1.smallFont.MeasureString(progress).X, this.cardArea.Top + 20), MutedText);

            if (!this.session.IsFlipped)
            {
                DrawCentered(b, card.Text, Game1.dialogueFont, this.cardArea.Center.Y - 48, this.cardArea, Game1.textColor, scale: 1.5f);
                DrawCentered(b, "Click or press Space to flip", Game1.smallFont, this.cardArea.Bottom - 56, this.cardArea, FaintText);
                this.DrawButton(b, this.primaryButton.bounds, "Flip (Space)");
                return;
            }

            this.DrawBack(b, card, this.cardArea);
            this.DrawButton(b, this.secondaryButton.bounds, "1: Missed it");
            this.DrawButton(b, this.primaryButton.bounds, "2: Knew it");
        }

        private void DrawEmpty(SpriteBatch b)
        {
            DrawPanel(b, this.cardArea);
            DrawCentered(b, "No flashcards yet.", Game1.dialogueFont, this.cardArea.Center.Y - 60, this.cardArea, Game1.textColor);
            DrawCentered(b, "Click an underlined word anywhere in the game to save it here.", Game1.smallFont, this.cardArea.Center.Y + 10, this.cardArea, MutedText);
        }

        private void DrawBrowse(SpriteBatch b)
        {
            if (this.browsing is { } card)
            {
                DrawPanel(b, this.cardArea);
                this.DrawBack(b, card, this.cardArea);
                this.DrawButton(b, this.secondaryButton.bounds, "Back");
                bool confirming = this.confirmingDelete == card;
                this.DrawButton(b, this.primaryButton.bounds, confirming ? "Really delete?" : "Delete card", tint: confirming ? DeleteTint : null);
                return;
            }

            var cards = this.ListedCards();
            if (cards.Count == 0)
            {
                this.DrawEmpty(b);
                return;
            }

            string count = cards.Count == 1 ? "1 card" : $"{cards.Count} cards";
            Utility.drawTextWithShadow(b, count, Game1.smallFont, new Vector2(this.browseTab.bounds.Right + 24, this.browseTab.bounds.Y + 14), MutedText);

            for (int row = 0; row < this.VisibleRows && this.scrollRow + row < cards.Count; row++)
                this.DrawRow(b, cards[this.scrollRow + row], this.RowBounds(row));

            if (cards.Count > this.VisibleRows)
            {
                if (this.scrollRow > 0)
                    this.upArrow.draw(b);
                if (this.scrollRow + this.VisibleRows < cards.Count)
                    this.downArrow.draw(b);
            }
        }

        private Rectangle RowBounds(int row) => new(this.cardArea.X, this.cardArea.Y + (row * RowHeight), this.cardArea.Width - 56, RowHeight - 4);

        private Rectangle DeleteBounds(Rectangle row) => new(row.Right - 52, row.Y + 4, 48, row.Height - 8);

        private const int MaxRowWordChars = 8;

        private void DrawRow(SpriteBatch b, Flashcard card, Rectangle bounds)
        {
            bool hovered = bounds.Contains(Game1.getMouseX(ui_scale: true), Game1.getMouseY(ui_scale: true));
            drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), bounds.X, bounds.Y, bounds.Width, bounds.Height, hovered ? Color.Wheat : Color.White, 4f, false);

            var font = Game1.smallFont;
            float textY = bounds.Y + ((bounds.Height - font.LineSpacing) / 2f);
            int x = bounds.X + 16;

            // a saved phrase can be a whole sentence; the list shows its start, the card itself shows it all
            string word = card.Text.Length > MaxRowWordChars ? card.Text.Substring(0, MaxRowWordChars) + "..." : card.Text;
            Utility.drawTextWithShadow(b, Truncate(word, font, 184), font, new Vector2(x, textY), Game1.textColor);
            x += 200;
            Utility.drawTextWithShadow(b, Truncate(card.Kana, font, 170), font, new Vector2(x, textY), MutedText);
            x += 180;

            string stats = $"+{card.Passes} -{card.Fails}";
            float statsWidth = font.MeasureString(stats).X;
            float glossRoom = bounds.Right - 72 - statsWidth - 16 - x;
            Utility.drawTextWithShadow(b, Truncate(Safe(string.Join("; ", card.Glosses)), font, glossRoom), font, new Vector2(x, textY), Game1.textColor);
            Utility.drawTextWithShadow(b, stats, font, new Vector2(bounds.Right - 72 - statsWidth, textY), MutedText);

            Rectangle delete = this.DeleteBounds(bounds);
            bool confirming = this.confirmingDelete == card;
            drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9), delete.X, delete.Y, delete.Width, delete.Height, confirming ? Color.Red : Color.White, 4f, false);
            b.Draw(Game1.mouseCursors, new Vector2(delete.Center.X - 18, delete.Center.Y - 18), new Rectangle(337, 494, 12, 12), Color.White, 0f, Vector2.Zero, 3f, SpriteEffects.None, 1f);
        }

        /// <summary>
        /// The back of a card: the word, its reading, its meanings, then the sentences it was saved
        /// from with the word picked out, each followed by the hand-authored literal English and the
        /// game's own English in a fainter colour.
        /// </summary>
        private void DrawBack(SpriteBatch b, Flashcard card, Rectangle area)
        {
            var small = Game1.smallFont;
            int left = area.X + 32;
            int width = area.Width - 64;
            float y = area.Y + 24;

            Utility.drawTextWithShadow(b, card.Text, Game1.dialogueFont, new Vector2(left, y), Game1.textColor);
            float wordWidth = Game1.dialogueFont.MeasureString(card.Text).X;

            string reading = card.Kana;
            string romaji = Safe(KanaRomaji.Convert(card.Kana));
            if (romaji.Length > 0 && romaji != card.Kana)
                reading += "   " + romaji;
            Utility.drawTextWithShadow(b, reading, small, new Vector2(left + wordWidth + 24, y + 16), MutedText);
            y += Game1.dialogueFont.LineSpacing + 8;

            foreach (string line in Game1.parseText(Safe(string.Join("; ", card.Glosses)), small, width).Split('\n'))
            {
                Utility.drawTextWithShadow(b, line, small, new Vector2(left, y), Game1.textColor);
                y += small.LineSpacing;
            }

            var blocks = this.ContextBlocks(card, width);
            if (blocks.Count == 0)
                return;

            y += 12;
            b.Draw(Game1.staminaRect, new Rectangle(left, (int)y, width, 2), FaintText * 0.6f);
            y += 14;

            foreach (var block in blocks)
            {
                if (y + small.LineSpacing > area.Bottom - 16)
                    break;

                foreach (var line in block.Japanese)
                {
                    if (y + small.LineSpacing > area.Bottom - 16)
                        break;
                    DrawHighlightedLine(b, small, line, left, y);
                    y += small.LineSpacing;
                }

                foreach (var (text, color) in block.English)
                {
                    if (y + small.LineSpacing > area.Bottom - 16)
                        break;
                    Utility.drawTextWithShadow(b, text, small, new Vector2(left + 16, y), color);
                    y += small.LineSpacing;
                }

                y += 12;
            }
        }

        /// <summary>One saved sentence, laid out: wrapped Japanese lines with the word's range on each, then English lines.</summary>
        private sealed record ContextBlock(List<HighlightedLine> Japanese, List<(string Text, Color Color)> English);

        private readonly record struct HighlightedLine(string Text, int HighlightStart, int HighlightLength);

        /// <summary>Lays out a card's contexts, once per card and width -- measuring every character per frame would be wasteful.</summary>
        private List<ContextBlock> ContextBlocks(Flashcard card, int width)
        {
            if (this.backLayout is { } cached && cached.Card == card && cached.Width == width)
                return cached.Blocks;

            var small = Game1.smallFont;
            var blocks = new List<ContextBlock>();
            var resolved = FlashcardStore.ResolveContexts(card, ModEntry.Instance.Segments.Entries);

            foreach (var (context, entry) in resolved.Take(MaxContextsShown))
            {
                var (page, pageIndex, highlight) = ContextText.PageAround(entry.Japanese, context.Offset, context.Length);
                if (highlight is { } span)
                {
                    // the pointer covers the whole authored segment ("誕生日を"); pick out just the word
                    int at = page.IndexOf(card.Text, span.Start, StringComparison.Ordinal);
                    if (at >= 0 && at < span.Start + span.Length)
                        highlight = (at, card.Text.Length);
                }

                var japanese = WrapHighlighted(page, highlight, small, width);

                var english = new List<(string, Color)>();
                int pages = ContextText.PageCount(entry.Japanese);
                if (!string.IsNullOrWhiteSpace(entry.English))
                    AddWrapped(english, ContextText.EnglishPage(entry.English!, pageIndex, pages), MutedText, small, width - 16);

                string? official = OfficialEnglish(entry.Japanese, page);
                if (official is not null)
                    AddWrapped(english, "Game: " + official, FaintText, small, width - 16);

                blocks.Add(new ContextBlock(japanese, english));
            }

            if (resolved.Count > MaxContextsShown)
            {
                var more = new List<(string, Color)> { ($"...and {resolved.Count - MaxContextsShown} more sentence(s)", FaintText) };
                blocks.Add(new ContextBlock(new List<HighlightedLine>(), more));
            }

            this.backLayout = (card, width, blocks);
            return blocks;
        }

        /// <summary>
        /// The game's own English for a context, from the translation index: the whole entry if it
        /// was indexed that way, else just the page shown.
        /// </summary>
        private static string? OfficialEnglish(string rawJapanese, string page)
        {
            var map = ModEntry.Instance.TranslationIndex.Map;
            if (map.TryLookup(rawJapanese, out string whole))
                return ContextText.Clean(whole);
            if (map.TryLookup(page, out string pageOnly))
                return ContextText.Clean(pageOnly);
            return null;
        }

        private static void AddWrapped(List<(string, Color)> lines, string text, Color color, SpriteFont font, int width)
        {
            foreach (string line in Game1.parseText(Safe(text), font, width).Split('\n'))
                lines.Add((line, color));
        }

        /// <summary>
        /// Wraps Japanese by character -- it has no spaces to wrap at -- and tracks which part of the
        /// highlighted word lands on each line, since the word itself can wrap.
        /// </summary>
        private static List<HighlightedLine> WrapHighlighted(string text, (int Start, int Length)? highlight, SpriteFont font, int width)
        {
            var lines = new List<HighlightedLine>();
            int hlStart = highlight?.Start ?? -1;
            int hlEnd = highlight is { } h ? h.Start + h.Length : -1;

            int lineStart = 0;
            while (lineStart < text.Length)
            {
                int end = lineStart + 1;
                while (end < text.Length && font.MeasureString(text.Substring(lineStart, end - lineStart + 1)).X <= width)
                    end++;

                int start = Math.Max(hlStart, lineStart);
                int stop = Math.Min(hlEnd, end);
                lines.Add(stop > start
                    ? new HighlightedLine(text.Substring(lineStart, end - lineStart), start - lineStart, stop - start)
                    : new HighlightedLine(text.Substring(lineStart, end - lineStart), 0, 0));

                lineStart = end;
            }

            return lines;
        }

        private static void DrawHighlightedLine(SpriteBatch b, SpriteFont font, HighlightedLine line, float x, float y)
        {
            if (line.HighlightLength == 0)
            {
                Utility.drawTextWithShadow(b, line.Text, font, new Vector2(x, y), Game1.textColor);
                return;
            }

            string before = line.Text.Substring(0, line.HighlightStart);
            string word = line.Text.Substring(line.HighlightStart, line.HighlightLength);
            string after = line.Text.Substring(line.HighlightStart + line.HighlightLength);

            float beforeWidth = font.MeasureString(before).X;
            float wordWidth = font.MeasureString(word).X;

            Utility.drawTextWithShadow(b, before, font, new Vector2(x, y), Game1.textColor);
            Utility.drawTextWithShadow(b, word, font, new Vector2(x + beforeWidth, y), HighlightText);
            b.Draw(Game1.staminaRect, new Rectangle((int)(x + beforeWidth), (int)(y + font.LineSpacing - 6), (int)wordWidth, 3), HighlightText * 0.8f);
            Utility.drawTextWithShadow(b, after, font, new Vector2(x + beforeWidth + wordWidth, y), Game1.textColor);
        }

        /// <summary>
        /// A menu-tile panel. With <paramref name="fill"/>, its parchment interior is painted over
        /// in that colour inside the frame -- the card under review is white, like a paper card.
        /// </summary>
        private static void DrawPanel(SpriteBatch b, Rectangle area, Color? fill = null)
        {
            drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), area.X, area.Y, area.Width, area.Height, Color.White, 1f, false);

            if (fill is { } color)
            {
                const int frame = 16; // the menu tile's border at scale 1
                b.Draw(Game1.staminaRect, new Rectangle(area.X + frame, area.Y + frame, area.Width - (frame * 2), area.Height - (frame * 2)), color);
            }
        }

        private void DrawButton(SpriteBatch b, Rectangle bounds, string label, bool selected = false, Color? tint = null)
        {
            bool hovered = bounds.Contains(Game1.getMouseX(ui_scale: true), Game1.getMouseY(ui_scale: true));
            Color color = tint ?? (selected ? Color.Wheat : hovered ? new Color(255, 240, 210) : Color.White);
            drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9), bounds.X, bounds.Y, bounds.Width, bounds.Height, color, 4f, false);

            var font = Game1.smallFont;
            Vector2 size = font.MeasureString(label);
            Utility.drawTextWithShadow(b, label, font, new Vector2(bounds.Center.X - (size.X / 2), bounds.Center.Y - (size.Y / 2) + 2), Game1.textColor);
        }

        private static void DrawCentered(SpriteBatch b, string text, SpriteFont font, float y, Rectangle area, Color color, float scale = 1f)
        {
            text = Game1.parseText(text, font, (int)((area.Width - 64) / scale));
            foreach (string line in text.Split('\n'))
            {
                Vector2 size = font.MeasureString(line) * scale;
                Utility.drawTextWithShadow(b, line, font, new Vector2(area.Center.X - (size.X / 2), y), color, scale);
                y += font.LineSpacing * scale;
            }
        }

        private static string Truncate(string text, SpriteFont font, float width)
        {
            if (width <= 0)
                return "";
            if (font.MeasureString(text).X <= width)
                return text;

            for (int length = text.Length - 1; length > 0; length--)
            {
                string cut = text.Substring(0, length) + "...";
                if (font.MeasureString(cut).X <= width)
                    return cut;
            }

            return "";
        }

        /// <summary>English and romaji in smallFont, whose missing glyphs would draw as '*'.</summary>
        private static string Safe(string text) => FontSafeText.Apply(text, ExtendedFont.DrawableCharacters(Game1.smallFont));

        #endregion
    }
}
