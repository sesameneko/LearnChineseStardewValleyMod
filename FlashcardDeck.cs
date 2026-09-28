using System;
using System.Collections.Generic;
using System.Linq;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>
    /// One saved word. Serialised as-is into SMAPI's global data by the game side, so it's a
    /// plain get/set shape.
    ///
    /// Identity is (<see cref="Language"/>, <see cref="Text"/>, <see cref="Pinyin"/>): 行 read
    /// xíng and read háng are different cards, while 喜欢 saved from ten sentences with three
    /// slightly different glosses is one card carrying all of them.
    /// </summary>
    public sealed class Flashcard
    {
        /// <summary>The source language the word is in, e.g. "zh".</summary>
        public string Language { get; set; } = "";

        /// <summary>The word, without the punctuation and markup segment data attaches to it.</summary>
        public string Text { get; set; } = "";

        /// <summary>The reading as stored in the segment data ("mù chǎng"); see <see cref="LanguageStudyStardewValleyMod.Pinyin"/> for display.</summary>
        public string Pinyin { get; set; } = "";

        /// <summary>Every gloss the word was saved with, first first.</summary>
        public List<string> Glosses { get; set; } = new();

        /// <summary>Where the word was saved from, as <see cref="ContextRef"/> strings.</summary>
        public List<string> Contexts { get; set; } = new();

        /// <summary>When the card was first saved, UTC.</summary>
        public DateTime Created { get; set; }

        public int Passes { get; set; }

        public int Fails { get; set; }

        public bool Is(string language, string text, string pinyin)
        {
            return string.Equals(this.Language, language, StringComparison.Ordinal)
                   && string.Equals(this.Text, text, StringComparison.Ordinal)
                   && string.Equals(this.Pinyin, pinyin, StringComparison.Ordinal);
        }
    }

    /// <summary>The whole saved deck, across every language.</summary>
    public sealed class FlashcardDeckData
    {
        public int Version { get; set; } = 1;

        public List<Flashcard> Cards { get; set; } = new();
    }

    /// <summary>What a save gesture did.</summary>
    public enum SaveOutcome
    {
        /// <summary>A new card.</summary>
        Added,

        /// <summary>The card existed; this click's sentence was added to it.</summary>
        ContextAdded,

        /// <summary>The card existed with this context, or none; only the gloss was new.</summary>
        GlossAdded,

        /// <summary>The card already had exactly this, and something else keeps it alive, so nothing changed.</summary>
        AlreadySaved,

        /// <summary>The click matched a context the card had, which was removed; others remain.</summary>
        ContextRemoved,

        /// <summary>The click matched the card's last context (or its lack of one); the card is gone.</summary>
        CardRemoved,

        /// <summary>A grammatical particle or placeholder, which doesn't make a card.</summary>
        RefusedGrammar,

        /// <summary>Nothing left of the word once punctuation is trimmed.</summary>
        RefusedEmpty,
    }

    /// <summary>Review orders the player can pick.</summary>
    public enum CardOrder
    {
        NewestFirst,
        OldestFirst,
        FewestCorrect,
    }

    /// <summary>
    /// The deck's rules, apart from any game type so they can be unit-tested: what a click on a
    /// word does to the deck, which words can't be saved, and the orders cards are reviewed in.
    /// </summary>
    public sealed class FlashcardDeck
    {
        public FlashcardDeckData Data { get; }

        public FlashcardDeck(FlashcardDeckData? data = null)
        {
            this.Data = data ?? new FlashcardDeckData();
            this.Data.Cards ??= new List<Flashcard>();
        }

        public IEnumerable<Flashcard> CardsFor(string language) => this.Data.Cards.Where(card => card.Language == language);

        public Flashcard? Find(string language, string text, string pinyin) => this.Data.Cards.FirstOrDefault(card => card.Is(language, text, pinyin));

        /// <summary>Whether a hovered segment's word is in the deck, for marking it in the hover label.</summary>
        public bool Contains(string language, string segmentText, string? pinyin)
        {
            string text = CardText(segmentText);
            return text.Length > 0 && this.Find(language, text, pinyin ?? "") is not null;
        }

        /// <summary>
        /// Applies a click on a word: saves it, adds what's new about this sighting to the card it
        /// already has, or -- when this exact sighting is already on the card -- takes it back off.
        /// </summary>
        /// <param name="segmentText">The segment as drawn; trimmed to the word here.</param>
        /// <param name="context">The sentence it was clicked in, or null where it traces to no single entry.</param>
        public (SaveOutcome Outcome, Flashcard? Card) Toggle(string language, string segmentText, string? pinyin, string? gloss, ContextRef? context, DateTime now)
        {
            if (IsGrammarGloss(gloss))
                return (SaveOutcome.RefusedGrammar, null);

            string text = CardText(segmentText);
            if (text.Length == 0)
                return (SaveOutcome.RefusedEmpty, null);

            pinyin ??= "";
            gloss = string.IsNullOrWhiteSpace(gloss) ? null : gloss.Trim();
            string? contextId = context?.ToString();

            var card = this.Find(language, text, pinyin);
            if (card is null)
            {
                card = new Flashcard { Language = language, Text = text, Pinyin = pinyin, Created = now };
                if (gloss is not null)
                    card.Glosses.Add(gloss);
                if (contextId is not null)
                    card.Contexts.Add(contextId);
                this.Data.Cards.Add(card);
                return (SaveOutcome.Added, card);
            }

            bool glossIsNew = gloss is not null && !card.Glosses.Contains(gloss, StringComparer.Ordinal);

            if (contextId is not null && card.Contexts.Contains(contextId, StringComparer.Ordinal))
            {
                // the same sentence again, taken back -- but a new gloss is something new, not a repeat
                if (glossIsNew)
                {
                    card.Glosses.Add(gloss!);
                    return (SaveOutcome.GlossAdded, card);
                }

                card.Contexts.Remove(contextId);
                if (card.Contexts.Count > 0)
                    return (SaveOutcome.ContextRemoved, card);

                this.Data.Cards.Remove(card);
                return (SaveOutcome.CardRemoved, card);
            }

            if (contextId is not null)
            {
                card.Contexts.Add(contextId);
                if (glossIsNew)
                    card.Glosses.Add(gloss!);
                return (SaveOutcome.ContextAdded, card);
            }

            // no sentence to tell sightings apart (the clock, a composite with no single source)
            if (glossIsNew)
            {
                card.Glosses.Add(gloss!);
                return (SaveOutcome.GlossAdded, card);
            }

            if (card.Contexts.Count > 0)
                return (SaveOutcome.AlreadySaved, card);

            this.Data.Cards.Remove(card);
            return (SaveOutcome.CardRemoved, card);
        }

        public bool Delete(Flashcard card) => this.Data.Cards.Remove(card);

        public static void Record(Flashcard card, bool passed)
        {
            if (passed)
                card.Passes++;
            else
                card.Fails++;
        }

        /// <summary>
        /// A language's cards in review order. Ties fall back to oldest first, so the order is
        /// stable from one pass to the next.
        /// </summary>
        public List<Flashcard> Ordered(string language, CardOrder order)
        {
            var cards = this.CardsFor(language);
            return (order switch
            {
                CardOrder.NewestFirst => cards.OrderByDescending(card => card.Created),
                CardOrder.FewestCorrect => cards.OrderBy(card => card.Passes).ThenBy(card => card.Created),
                _ => cards.OrderBy(card => card.Created),
            }).ToList();
        }

        /// <summary>
        /// Whether a gloss marks a grammatical particle or a placeholder rather than a word: the
        /// segment data writes those as one parenthesised note -- "(object marker)", "(topic
        /// marker)", "(your name)", "(item)" -- where a real word has an English equivalent. A gloss
        /// that merely *contains* a note, "(my) parents (subject marker)", is a word and passes.
        /// </summary>
        public static bool IsGrammarGloss(string? gloss)
        {
            if (string.IsNullOrWhiteSpace(gloss))
                return false;

            string trimmed = gloss.Trim();
            return trimmed.Length >= 2 && trimmed[0] == '(' && trimmed.IndexOf(')') == trimmed.Length - 1;
        }

        /// <summary>
        /// The word in a segment as a card holds it. Segment data attaches punctuation to the word
        /// before it (を、 / よ。) and the game inserts line breaks when wrapping, neither of which is
        /// part of the word. Trims anything that isn't a letter or digit from both ends and drops
        /// whitespace; ー and 々 count as letters, so a long vowel or a repeated kanji stays.
        /// </summary>
        public static string CardText(string segmentText)
        {
            var builder = new System.Text.StringBuilder(segmentText.Length);
            foreach (char c in segmentText)
            {
                if (!char.IsWhiteSpace(c))
                    builder.Append(c);
            }

            int start = 0;
            int end = builder.Length;
            while (start < end && !char.IsLetterOrDigit(builder[start]))
                start++;
            while (end > start && !char.IsLetterOrDigit(builder[end - 1]))
                end--;

            return builder.ToString(start, end - start);
        }
    }

    /// <summary>
    /// One pass through the deck in the review tab: show a card's front, flip it, mark it passed or
    /// failed, move on. Pass/fail only counts -- nothing is rescheduled -- so a pass is simply each
    /// card once, in the order picked when it started.
    /// </summary>
    public sealed class ReviewSession
    {
        private readonly List<Flashcard> cards;

        public ReviewSession(IEnumerable<Flashcard> cards)
        {
            this.cards = cards.ToList();
        }

        public int Count => this.cards.Count;

        /// <summary>How many cards have been graded so far.</summary>
        public int Position { get; private set; }

        public int PassedThisSession { get; private set; }

        public bool IsFlipped { get; private set; }

        public bool IsFinished => this.Position >= this.cards.Count;

        public Flashcard? Current => this.IsFinished ? null : this.cards[this.Position];

        public void Flip()
        {
            if (!this.IsFinished)
                this.IsFlipped = !this.IsFlipped;
        }

        /// <summary>Records the answer and moves on. Only counts once the card has been flipped, so a stray key can't grade an unseen card.</summary>
        public bool Grade(bool passed)
        {
            if (this.Current is not { } card || !this.IsFlipped)
                return false;

            FlashcardDeck.Record(card, passed);
            if (passed)
                this.PassedThisSession++;

            this.Position++;
            this.IsFlipped = false;
            return true;
        }

        /// <summary>Drops a card deleted mid-session, keeping the position on the card that was next.</summary>
        public void Remove(Flashcard card)
        {
            int index = this.cards.IndexOf(card);
            if (index < 0)
                return;

            this.cards.RemoveAt(index);
            if (index < this.Position)
                this.Position--;
            else if (index == this.Position)
                this.IsFlipped = false;
        }
    }
}
