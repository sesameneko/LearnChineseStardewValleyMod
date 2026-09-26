using System;
using System.Collections.Generic;
using StardewModdingAPI;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>
    /// Keeps the flashcard deck on disk. The deck is global rather than per save -- vocabulary
    /// isn't tied to a farm -- so it lives in SMAPI's global app data
    /// (<c>.smapi/mod-data/&lt;mod id&gt;/flashcards.json</c>) rather than the mod folder, which a
    /// mod update replaces wholesale.
    ///
    /// Written after every change: a change is one click, and the file is small.
    /// </summary>
    public static class FlashcardStore
    {
        private const string Key = "flashcards";

        private static IModHelper? helper;

        public static FlashcardDeck Deck { get; private set; } = new();

        /// <summary>Context pointers already reported as unresolvable, so each is logged once.</summary>
        private static readonly HashSet<string> reportedStale = new(StringComparer.Ordinal);

        public static void Load(IModHelper modHelper)
        {
            helper = modHelper;
            try
            {
                Deck = new FlashcardDeck(modHelper.Data.ReadGlobalData<FlashcardDeckData>(Key));
                ModEntry.Log($"Flashcards loaded: {Deck.Data.Cards.Count} card(s).", LogLevel.Trace);
            }
            catch (Exception ex)
            {
                // don't start a fresh deck over one we merely failed to read: the next save would
                // overwrite it, so saving stays off until the file is fixed
                ModEntry.Log($"Couldn't read the flashcard deck -- flashcards won't be saved this session so the file isn't overwritten: {ex.Message}", LogLevel.Error);
                helper = null;
            }
        }

        public static void Save()
        {
            if (helper is null)
                return;

            try
            {
                helper.Data.WriteGlobalData(Key, Deck.Data);
            }
            catch (Exception ex)
            {
                ModEntry.Log($"Couldn't save the flashcard deck: {ex.Message}", LogLevel.Error);
            }
        }

        /// <summary>
        /// A card's contexts that still resolve against the loaded segment data. The rest are
        /// hidden but kept in the file -- a later data fix may make them valid again -- and logged
        /// once each.
        /// </summary>
        public static List<(ContextRef Ref, SourceEntry Entry)> ResolveContexts(Flashcard card, SourceEntries entries)
        {
            var resolved = new List<(ContextRef, SourceEntry)>();
            foreach (string id in card.Contexts)
            {
                if (ContextRef.TryParse(id, out var context) && entries.TryResolve(context, card.Text, out var entry))
                {
                    resolved.Add((context, entry));
                    continue;
                }

                if (reportedStale.Add(id))
                    ModEntry.Log($"Flashcard '{card.Text}': context '{id}' no longer matches the segment data; hiding it.", LogLevel.Debug);
            }

            return resolved;
        }
    }
}
