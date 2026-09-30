using System;
using System.Collections.Generic;
using System.Linq;
using LanguageStudyStardewValleyMod.Patches;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>
    /// Works out the target-language text of the dialogue box page on screen, for
    /// <see cref="DialogueBubbleOverlay"/>. Takes the key from the game rather than looking up the
    /// drawn text (see HowItWorks.md), in this order:
    ///
    /// 1. the Dialogue's translation key, with any {0} values recovered from the Japanese template;
    /// 2. for a line with no key during an event, the same command in the target-language script;
    /// 3. otherwise the whole raw text, looked up in the translation map.
    ///
    /// Then the page: the segment the page came from if the two entries share their '#' structure,
    /// the whole entry if not. Resolved on the update tick, once per page, and cached -- drawing
    /// only reads <see cref="Text"/>.
    /// </summary>
    public static class DialogueTranslation
    {
        /// <summary>The box <see cref="Text"/> was resolved for, or null if no dialogue box is open.</summary>
        public static DialogueBox? Box { get; private set; }

        /// <summary>The target-language text of the page on screen, or null when none was found.</summary>
        public static string? Text { get; private set; }

        /// <summary>How <see cref="Text"/> was found, or why it wasn't, for ls_dialogue.</summary>
        public static string Explanation { get; private set; } = "";

        /// <summary>What <see cref="Text"/> was resolved from, so it's worked out again only when the page changes.</summary>
        private static (DialogueBox Box, object? Dialogue, int Page, int Pages)? resolvedFor;

        /// <summary>Parsed source-language event scripts per asset, for finding which script a running event came from.</summary>
        private static readonly Dictionary<string, List<KeyValuePair<string, IReadOnlyList<string>>>> ParsedScripts = new();

        /// <summary>Drops everything cached, e.g. when the index is rebuilt or the copy deactivates.</summary>
        public static void Clear()
        {
            Box = null;
            Text = null;
            Explanation = "";
            resolvedFor = null;
            ParsedScripts.Clear();
        }

        /// <summary>Called every update tick: re-resolves if the box or its page has changed.</summary>
        public static void Update(TranslationIndex index)
        {
            if (Game1.activeClickableMenu is not DialogueBox box)
            {
                if (Box is not null)
                {
                    Box = null;
                    Text = null;
                    resolvedFor = null;
                }

                return;
            }

            var dialogue = box.characterDialogue;
            var state = (box, (object?)dialogue, dialogue?.currentDialogueIndex ?? 0, box.dialogues?.Count ?? 0);
            if (resolvedFor == state)
                return;

            resolvedFor = state;
            Box = box;

            try
            {
                (Text, Explanation) = dialogue is not null ? ForDialogue(index, dialogue) : ForStrings(index, box);
            }
            catch (Exception ex)
            {
                (Text, Explanation) = (null, $"error: {ex.GetType().Name}: {ex.Message}");
                ModEntry.Log($"Couldn't resolve the dialogue translation: {ex}", LogLevel.Trace);
            }
        }

        private static (string?, string) ForDialogue(TranslationIndex index, Dialogue dialogue)
        {
            if (!DialogueCapturePatches.TryGetSource(dialogue, out var source))
                return (null, "no record of this dialogue being parsed (built before the copy activated?)");

            int page = dialogue.currentDialogueIndex;
            int segment = page >= 0 && page < source.LineSegments.Length ? source.LineSegments[page] : -1;

            var (english, how) = EnglishEntry(index, source);
            if (english is null)
                return (null, how);

            string[]? englishSegments = DialoguePages.MatchingSegments(source.Master, english, source.Alternative);
            if (englishSegments is not null && DialoguePages.Page(englishSegments, segment, GenderSwitch, Token) is { } text)
                return (text, $"{how}; page {page} is segment {segment}");

            int alternative = DialoguePages.Alternatives(english).Length == DialoguePages.Alternatives(source.Master).Length ? source.Alternative : 0;
            string why = englishSegments is null ? "the entries' '#' structure differs" : $"page {page} has no segment ({segment})";
            return (DialoguePages.WholeEntry(english, alternative, GenderSwitch, Token), $"{how}; whole entry, because {why}");
        }

        /// <summary>The raw target-language entry for a dialogue, with its {n} values filled in, and how it was found.</summary>
        private static (string?, string) EnglishEntry(TranslationIndex index, DialogueSource source)
        {
            if (source.TranslationKey is { } key)
            {
                if (!index.TryGetByKey(key, target: true, out string english))
                    return (null, $"key '{key}' isn't in the target tables");

                if (index.TryGetByKey(key, target: false, out string template))
                {
                    (template, english) = DialoguePages.MatchingHalves(template, source.Master, english);
                    if (DialoguePages.TemplateArguments(template, source.Master) is { Count: > 0 } values)
                        english = DialoguePages.FillTemplate(english, values.Select(value => Translate(index, value)).ToList());
                }

                return (english, $"key '{key}'");
            }

            if (source.Event is { } line)
            {
                var (english, how) = EventText(index, line, source.Master);
                if (english is not null)
                    return (english, how);
            }

            return index.Map.TryLookup(source.Master, out string found)
                ? (found, "no key; raw text found in the translation map")
                : (null, "no key, not an event line, and the raw text isn't in the translation map");
        }

        private static (string?, string) ForStrings(TranslationIndex index, DialogueBox box)
        {
            if (!DialogueCapturePatches.TryGetSource(box, out var source))
                return (null, "no record of this box being built");

            var pages = box.dialogues;
            int page = Math.Clamp(source.PageCount - pages.Count, 0, Math.Max(0, source.PageCount - 1));

            string? english = null;
            string how;
            if (source.Event is { } line && EventText(index, line, source.Text) is ({ } fromEvent, var eventHow))
                (english, how) = (fromEvent, eventHow);
            else if (pages.Count > 0 && index.Map.TryLookup(pages[0], out string pageText))
                return (WithResponses(index, box, Clean(pageText)), "page text found in the translation map");
            else if (index.Map.TryLookup(source.Text, out string whole))
                (english, how) = (whole, "whole text found in the translation map");
            else
                return (null, "not an event line, and the text isn't in the translation map");

            string[] englishPages = english.Split('#');
            string text = englishPages.Length == source.PageCount
                ? Clean(englishPages[page])
                : string.Join("\n", englishPages.Select(Clean).Where(p => p.Length > 0));
            return (WithResponses(index, box, text), englishPages.Length == source.PageCount ? $"{how}; page {page}" : $"{how}; whole text, page counts differ");
        }

        /// <summary>A question box built from strings lists its answers as Response objects, in the source language.</summary>
        private static string WithResponses(TranslationIndex index, DialogueBox box, string text)
        {
            if (box.responses is not { Length: > 0 } responses)
                return text;

            return text + "\n\n" + string.Join("\n", responses.Select(response => "> " + Translate(index, response.responseText ?? "")));
        }

        /// <summary>
        /// The target-language text of the event command a line came from: the command holding the
        /// same text near the event's current one, the script it belongs to (found by its parsed
        /// commands, so forks work), and the same command in the target-language script.
        /// </summary>
        private static (string?, string) EventText(TranslationIndex index, EventLine line, string text)
        {
            if (DialoguePages.FindCommandWithText(line.Commands, line.CurrentCommand, text) is not var (command, argument))
                return (null, $"event line, but no command near #{line.CurrentCommand} holds its text");

            string asset = TranslationIndex.NormalizeAssetName(line.AssetName);
            if (!index.SourceTables.TryGetValue(asset, out var sourceScripts) || !index.TargetTables.TryGetValue(asset, out var targetScripts))
                return (null, $"event line, but '{asset}' isn't loaded");

            if (!ParsedScripts.TryGetValue(asset, out var parsed))
            {
                parsed = sourceScripts
                    .Select(pair => new KeyValuePair<string, IReadOnlyList<string>>(pair.Key, Event.ParseCommands(pair.Value, Game1.player)))
                    .ToList();
                ParsedScripts[asset] = parsed;
            }

            if (DialoguePages.FindScript(parsed, line.Commands, command) is not { } key)
                return (null, $"event line, but no script in '{asset}' matches the running event");

            if (!targetScripts.TryGetValue(key, out string? targetScript))
                return (null, $"event script '{key}' isn't in the target '{asset}'");

            var english = DialoguePages.EnglishArgument(Event.ParseCommands(targetScript, Game1.player), command, DialoguePages.SplitCommand(line.Commands[command]), argument);
            return english is null
                ? (null, $"event '{asset}' '{key}' command #{command} isn't the same command in the target script")
                : (english, $"event '{asset}' '{key}' command #{command}");
        }

        /// <summary>A value put into a template: an item or NPC name translates, anything else is kept as is.</summary>
        private static string Translate(TranslationIndex index, string value)
        {
            return index.Map.TryLookup(value, out string translation) ? Clean(translation) : value;
        }

        private static string Clean(string text)
        {
            return DialoguePages.Clean(text, GenderSwitch, Token);
        }

        private static string GenderSwitch(string text)
        {
            return Dialogue.applyGenderSwitch(Game1.player.Gender, text, altTokenOnly: false);
        }

        /// <summary>
        /// The tokens whose values are the same in every language: names the player typed, and a
        /// few facts. The rest (a random adjective, a book title) come out as "...".
        /// </summary>
        private static string? Token(string name)
        {
            var player = Game1.player;
            return name switch
            {
                "@" => player.Name,
                "farm" => player.farmName.Value,
                "pet" => player.getPetDisplayName(),
                "favorite" => player.favoriteThing.Value,
                "spouse" => player.spouse,
                "kid1" => player.getChildren().ElementAtOrDefault(0)?.displayName,
                "kid2" => player.getChildren().ElementAtOrDefault(1)?.displayName,
                "year" => Game1.year.ToString(),
                "season" => Game1.currentSeason is { Length: > 0 } season ? char.ToUpperInvariant(season[0]) + season.Substring(1) : null,
                _ => null,
            };
        }
    }
}
