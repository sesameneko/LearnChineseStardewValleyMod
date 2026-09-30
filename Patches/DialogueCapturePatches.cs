using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace LanguageStudyStardewValleyMod.Patches
{
    /// <summary>The event command a dialogue was built by, recorded while it runs: the asset, its live commands, and which one is current.</summary>
    public sealed record EventLine(string AssetName, string[] Commands, int CurrentCommand)
    {
        public static EventLine? Capture()
        {
            var e = Game1.CurrentEvent;
            if (e?.eventCommands is not { Length: > 0 } commands || string.IsNullOrEmpty(e.fromAssetName))
                return null;

            return new EventLine(e.fromAssetName, commands, e.currentCommand);
        }
    }

    /// <summary>
    /// Where one Dialogue's text came from. <see cref="LineSegments"/> gives, for each page in
    /// Dialogue.dialogues, the raw '#' segment of <see cref="Master"/>'s chosen alternative it was
    /// made from, or -1 (see <see cref="DialoguePages"/>).
    /// </summary>
    public sealed record DialogueSource(string? TranslationKey, string Master, int Alternative, int[] LineSegments, EventLine? Event);

    /// <summary>A DialogueBox built from plain strings rather than a Dialogue: its text as given, split into pages on '#'.</summary>
    public sealed record BoxSource(string Text, int PageCount, EventLine? Event);

    /// <summary>
    /// Records, as the game builds each dialogue, what the dialogue translation bubble needs to find
    /// the same page in the target language: the translation key and raw text, which raw segment
    /// each page came from, and the running event command if there's no key.
    ///
    /// The page mapping comes from watching Dialogue.parseDialogueString rather than re-parsing:
    /// it calls checkForSpecialCharacters once on each segment it's about to make a page of, so
    /// the (input, output) pairs recorded here say which segments it chose -- including the ones
    /// chosen by a roll of Game1.random ($c), which no re-parse could reproduce.
    ///
    /// Everything is best-effort: a patch never throws into the game, and a dialogue with no record
    /// just shows no bubble icon.
    /// </summary>
    public static class DialogueCapturePatches
    {
        private sealed class ParseInProgress
        {
            public ParseInProgress(string master)
            {
                this.Master = master;
            }

            public string Master { get; }
            public List<SpecialCharacterCall> Calls { get; } = new();
            public string? PendingInput { get; set; }
        }

        /// <summary>The parse currently running, if any. The game builds dialogue on the main thread only.</summary>
        private static ParseInProgress? parsing;

        private static readonly ConditionalWeakTable<Dialogue, DialogueSource> Dialogues = new();
        private static readonly ConditionalWeakTable<DialogueBox, BoxSource> Boxes = new();

        /// <summary>Set when a patch failed, so the error is logged once rather than per dialogue.</summary>
        private static bool loggedError;

        public static bool TryGetSource(Dialogue dialogue, out DialogueSource source)
        {
            return Dialogues.TryGetValue(dialogue, out source!);
        }

        public static bool TryGetSource(DialogueBox box, out BoxSource source)
        {
            return Boxes.TryGetValue(box, out source!);
        }

        public static void Apply(Harmony harmony)
        {
            harmony.Patch(
                original: AccessTools.Method(typeof(Dialogue), "parseDialogueString"),
                prefix: new HarmonyMethod(typeof(DialogueCapturePatches), nameof(Prefix_ParseDialogueString)),
                postfix: new HarmonyMethod(typeof(DialogueCapturePatches), nameof(Postfix_ParseDialogueString)),
                finalizer: new HarmonyMethod(typeof(DialogueCapturePatches), nameof(Finalizer_ParseDialogueString))
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(Dialogue), nameof(Dialogue.checkForSpecialCharacters)),
                prefix: new HarmonyMethod(typeof(DialogueCapturePatches), nameof(Prefix_CheckForSpecialCharacters)),
                postfix: new HarmonyMethod(typeof(DialogueCapturePatches), nameof(Postfix_CheckForSpecialCharacters))
            );

            harmony.Patch(
                original: AccessTools.Constructor(typeof(Dialogue), new[] { typeof(Dialogue) }),
                postfix: new HarmonyMethod(typeof(DialogueCapturePatches), nameof(Postfix_CopyDialogue))
            );

            foreach (var parameters in new[] { new[] { typeof(string) }, new[] { typeof(string), typeof(Response[]), typeof(int) }, new[] { typeof(List<string>) } })
            {
                var constructor = AccessTools.Constructor(typeof(DialogueBox), parameters);
                if (constructor is null)
                {
                    ModEntry.Log($"Couldn't find DialogueBox({string.Join(", ", parameters.Select(p => p.Name))}) -- those boxes won't get a translation bubble.", LogLevel.Warn);
                    continue;
                }

                harmony.Patch(constructor, postfix: new HarmonyMethod(typeof(DialogueCapturePatches), nameof(Postfix_StringBox)));
            }
        }

        public static void Prefix_ParseDialogueString(string masterString)
        {
            // the constructor's error fallback parses a second time, which starts over
            parsing = new ParseInProgress(masterString ?? "...");
        }

        public static void Prefix_CheckForSpecialCharacters(string str)
        {
            if (parsing is { } parse)
                parse.PendingInput = str;
        }

        public static void Postfix_CheckForSpecialCharacters(string __result)
        {
            if (parsing is { PendingInput: { } input } parse)
            {
                parse.Calls.Add(new SpecialCharacterCall(input, __result ?? ""));
                parse.PendingInput = null;
            }
        }

        public static void Postfix_ParseDialogueString(Dialogue __instance, string translationKey)
        {
            if (parsing is not { } parse)
                return;

            try
            {
                string master = parse.Master;
                int alternative = DialoguePages.AlternativeIndex(DialoguePages.Alternatives(master).Length, Game1.stats?.DaysPlayed ?? 0);
                string[] segments = DialoguePages.Segments(master, alternative);

                int[] callSegments = DialoguePages.SegmentsOfCalls(segments, parse.Calls);
                var lineTexts = __instance.dialogues.Select(line => line.Text ?? "").ToList();
                int[] lineSegments = DialoguePages.SegmentsOfLines(lineTexts, parse.Calls, callSegments);

                var source = new DialogueSource(translationKey, master, alternative, lineSegments, translationKey is null ? EventLine.Capture() : null);
                Dialogues.AddOrUpdate(__instance, source);
            }
            catch (Exception ex)
            {
                LogOnce(ex);
            }
        }

        /// <summary>Always runs, so a parse that threw can't leave later checkForSpecialCharacters calls recorded against it.</summary>
        public static void Finalizer_ParseDialogueString()
        {
            parsing = null;
        }

        /// <summary>Dialogue(Dialogue other) copies the pages, so it inherits the source too.</summary>
        public static void Postfix_CopyDialogue(Dialogue __instance, Dialogue other)
        {
            if (other is not null && Dialogues.TryGetValue(other, out var source))
                Dialogues.AddOrUpdate(__instance, source);
        }

        /// <summary>
        /// The string constructors split their text on '#' into DialogueBox.dialogues and drop pages
        /// from the front as the player advances, so the page count now tells later which page is up.
        /// </summary>
        public static void Postfix_StringBox(DialogueBox __instance)
        {
            try
            {
                if (__instance.dialogues is not { Count: > 0 } pages)
                    return;

                Boxes.AddOrUpdate(__instance, new BoxSource(string.Join("#", pages), pages.Count, EventLine.Capture()));
            }
            catch (Exception ex)
            {
                LogOnce(ex);
            }
        }

        private static void LogOnce(Exception ex)
        {
            if (loggedError)
                return;

            loggedError = true;
            ModEntry.Log($"Dialogue capture failed; some dialogue boxes won't get a translation bubble. {ex}", LogLevel.Warn);
        }
    }
}
