using System;
using StardewModdingAPI;
using StardewValley;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>
    /// Click-to-save: a left-click on a hovered word adds it to the flashcard deck, or -- clicked
    /// again in the same sentence -- takes it back off.
    ///
    /// Only words whose boundaries came from segment data qualify: the fallback split has no gloss
    /// or reading to put on a card. Such clicks pass through to the game untouched.
    /// </summary>
    public static class FlashcardCapture
    {
        /// <summary>
        /// Handles a left-click. Returns true when it landed on a saveable word, in which case the
        /// caller suppresses it so the game underneath -- a dialogue box, a shop row -- never sees it.
        /// A refused word (a particle) still counts: the click was aimed at the word, not the game.
        /// </summary>
        public static bool TryHandleClick(string language)
        {
            if (!WordHoverOverlay.TryGetHoveredWord(out var segment) || string.IsNullOrWhiteSpace(segment.Gloss))
                return false;

            ContextRef? context = segment.Source is { } source ? ContextRef.From(source) : null;
            var (outcome, card) = FlashcardStore.Deck.Toggle(language, segment.Text, segment.Reading, segment.Gloss, context, DateTime.UtcNow);

            string word = card?.Text ?? FlashcardDeck.CardText(segment.Text);
            switch (outcome)
            {
                case SaveOutcome.Added:
                    Notify($"Flashcard added: {word} ({segment.Gloss})", "coin", HUDMessage.newQuest_type);
                    break;

                case SaveOutcome.ContextAdded:
                    Notify($"Context added to existing flashcard: {word}", "smallSelect", HUDMessage.newQuest_type);
                    break;

                case SaveOutcome.GlossAdded:
                    Notify($"Meaning added to existing flashcard: {word} ({segment.Gloss})", "smallSelect", HUDMessage.newQuest_type);
                    break;

                case SaveOutcome.AlreadySaved:
                    Notify($"Already a flashcard: {word}", "smallSelect", HUDMessage.newQuest_type);
                    return true;

                case SaveOutcome.ContextRemoved:
                    Notify($"Context removed from flashcard: {word} ({card!.Contexts.Count} left)", "trashcan", HUDMessage.error_type);
                    break;

                case SaveOutcome.CardRemoved:
                    Notify($"Flashcard removed: {word}", "trashcan", HUDMessage.error_type);
                    break;

                case SaveOutcome.RefusedGrammar:
                    Notify($"Not saved: {word} is grammar {segment.Gloss}, not a vocabulary word", "cancel", HUDMessage.error_type);
                    return true;

                case SaveOutcome.RefusedEmpty:
                    return false;
            }

            FlashcardStore.Save();
            return true;
        }

        private static void Notify(string message, string sound, int type)
        {
            Game1.playSound(sound);
            Game1.addHUDMessage(new HUDMessage(FontSafeText.Apply(message, ExtendedFont.DrawableCharacters(Game1.smallFont)), type));
            ModEntry.Log(message, LogLevel.Trace);
        }
    }
}
