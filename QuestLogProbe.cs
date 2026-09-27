using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Quests;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>
    /// Temporary diagnostic for the journal (QuestLog), which became the de-facto test case for
    /// word hover. Reports when the quest *detail* page opens and what text it holds, so
    /// that "is the text even on screen?" stops being something we infer from draw-call counts.
    ///
    /// Most of this needs no Harmony: QuestLog's questPage/_shownQuest/_objectiveText are protected
    /// fields that SMAPI's reflection helper can read straight off the live menu. Only the wrapped
    /// text needs a hook, because wrapping happens inside Game1.parseText at draw time.
    ///
    /// Delete this file once word-hover text capture is trustworthy.
    /// </summary>
    public static class QuestLogProbe
    {
        /// <summary>Whether to report. Off by default; toggled by the ls_probe_questlog command.</summary>
        public static bool Enabled { get; set; }

        private static string? lastState;
        private static string? lastParsedInput;

        public static void Reset()
        {
            lastState = null;
            lastParsedInput = null;
        }

        /// <summary>
        /// Polled each tick: reports the journal's page state whenever it changes, plus the detail
        /// page's own text straight from the quest object.
        /// </summary>
        public static void Poll(IModHelper helper)
        {
            if (!Enabled)
                return;

            try
            {
                if (Game1.activeClickableMenu is not QuestLog questLog)
                {
                    Report("(journal closed)");
                    return;
                }

                int questPage = helper.Reflection.GetField<int>(questLog, "questPage").GetValue();
                if (questPage < 0)
                {
                    Report("journal open, showing the quest LIST (questPage = -1)");
                    return;
                }

                var quest = helper.Reflection.GetField<IQuest>(questLog, "_shownQuest").GetValue();
                var objectives = helper.Reflection.GetField<List<string>>(questLog, "_objectiveText").GetValue();

                string name = quest?.GetName() ?? "(null quest)";
                string description = quest?.GetDescription() ?? "(no description)";
                string objectiveText = objectives is null || objectives.Count == 0
                    ? "(none)"
                    : string.Join(" | ", objectives);

                Report($"journal open, showing quest DETAIL (questPage = {questPage})"
                       + $"\n    name:        '{name}'"
                       + $"\n    description: '{description}'"
                       + $"\n    objectives:  {objectiveText}");
            }
            catch (Exception ex)
            {
                ModEntry.Log($"[questlog] probe failed: {ex.GetType().Name}: {ex.Message}", LogLevel.Warn);
                Enabled = false;
            }
        }

        private static void Report(string state)
        {
            if (state == lastState)
                return;

            lastState = state;
            ModEntry.Log($"[questlog] {state}");
        }

        /// <summary>
        /// Reports what the game wraps text into at draw time. This is the string that actually gets
        /// handed to a draw call, so comparing it against what the capture recorded says whether the
        /// text was drawn-but-missed or never drawn at all.
        /// </summary>
        public static void Postfix_ParseText(string text, string __result)
        {
            if (!Enabled || Game1.activeClickableMenu is not QuestLog)
                return;

            try
            {
                if (text == lastParsedInput)
                    return; // called every frame while the page is up

                lastParsedInput = text;

                string wrapped = __result?.Replace("\n", "\\n") ?? "(null)";
                ModEntry.Log($"[questlog] parseText wrapped '{text?.Replace("\n", "\\n")}' -> '{wrapped}'");
            }
            catch (Exception ex)
            {
                ModEntry.Log($"[questlog] parseText probe failed: {ex.GetType().Name}: {ex.Message}", LogLevel.Warn);
            }
        }
    }
}
