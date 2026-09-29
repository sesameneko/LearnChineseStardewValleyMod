using System;
using System.Collections.Generic;
using System.Linq;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>An installed copy of this mod: its manifest ID and the game language it studies.</summary>
    public sealed record Sibling(string ModId, string Language);

    /// <summary>What the title screen should ask, if anything.</summary>
    public abstract record LanguagePromptDecision
    {
        private LanguagePromptDecision()
        {
        }

        /// <summary>Ask nothing: the game is already in a studied language, or another copy asks.</summary>
        public sealed record None : LanguagePromptDecision;

        /// <summary>This is the only copy installed: offer to switch the game to its language.</summary>
        public sealed record SwitchToMine(string Language) : LanguagePromptDecision;

        /// <summary>Several copies are installed: let the player pick one language, or none.</summary>
        public sealed record ChooseAmong(IReadOnlyList<Sibling> Siblings) : LanguagePromptDecision;
    }

    /// <summary>
    /// Decides when this copy of the mod is active, and what to ask on the title screen.
    ///
    /// The mod ships as one copy per study language, and a player may install several. A copy is
    /// active only while the game language is its own, so at most one is live at a time and the
    /// copies never need to talk to each other. The title-screen prompt only helps the player get
    /// the game into one of those languages. Deliberately free of game types so it can be tested
    /// in tools/ModLogic.Tests.
    /// </summary>
    public static class LanguageActivation
    {
        /// <summary>The name of the manifest field holding a copy's study language, as a game language code.</summary>
        public const string ManifestField = "StudyLanguage";

        /// <summary>English names, since the prompt is drawn while the game is in some other language, whose font may lack the native script.</summary>
        private static readonly IReadOnlyDictionary<string, string> DisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = "English",
            ["ja"] = "Japanese",
            ["zh"] = "Chinese",
            ["ru"] = "Russian",
            ["pt"] = "Portuguese",
            ["es"] = "Spanish",
            ["de"] = "German",
            ["th"] = "Thai",
            ["fr"] = "French",
            ["ko"] = "Korean",
            ["it"] = "Italian",
            ["tr"] = "Turkish",
            ["hu"] = "Hungarian",
        };

        /// <summary>The English name of a game language code, or the code itself if it isn't a built-in language.</summary>
        public static string DisplayName(string code)
        {
            return DisplayNames.TryGetValue(code, out string? name) ? name : code;
        }

        /// <summary>
        /// Whether the game's current language is the one a copy studies. The game reports a
        /// custom (content-pack) language as "mod", which no copy can study.
        /// </summary>
        public static bool Matches(string studyLanguage, string currentLanguage)
        {
            return !currentLanguage.Equals("mod", StringComparison.OrdinalIgnoreCase)
                && studyLanguage.Equals(currentLanguage, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// What the title screen should ask. Every copy runs this against the same set of
        /// siblings, and only the leader (the lowest mod ID) gets a prompt back, so one popup
        /// appears however many copies are installed.
        /// </summary>
        public static LanguagePromptDecision DecidePrompt(string myId, IReadOnlyCollection<Sibling> siblings, string currentLanguage)
        {
            if (siblings.Count == 0 || siblings.Any(sibling => Matches(sibling.Language, currentLanguage)))
                return new LanguagePromptDecision.None();

            var leader = siblings.OrderBy(sibling => sibling.ModId, StringComparer.OrdinalIgnoreCase).First();
            if (!leader.ModId.Equals(myId, StringComparison.OrdinalIgnoreCase))
                return new LanguagePromptDecision.None();

            if (siblings.Count == 1)
                return new LanguagePromptDecision.SwitchToMine(leader.Language);

            return new LanguagePromptDecision.ChooseAmong(siblings
                .OrderBy(sibling => DisplayName(sibling.Language), StringComparer.OrdinalIgnoreCase)
                .ThenBy(sibling => sibling.ModId, StringComparer.OrdinalIgnoreCase)
                .ToList());
        }
    }

    public enum ActivationChange
    {
        None,
        Activate,
        Deactivate,
    }

    /// <summary>
    /// Turns a per-tick "is the game in my language" poll into activate/deactivate steps.
    ///
    /// Deactivates on the first tick the language stops matching, but activates only after it has
    /// matched for two ticks in a row. When the player switches from one copy's language to
    /// another's, every copy sees the change on the same tick, so the delay lets the old copy
    /// remove its Harmony patches before the new one adds its own. Without it, the new copy's glyph
    /// transpilers could run over the old copy's modified IL, fail to match, and log warnings.
    /// </summary>
    public sealed class ActivationTracker
    {
        private bool matchedLastTick;

        public bool IsActive { get; private set; }

        public ActivationChange Step(bool languageMatches)
        {
            bool matchedBefore = this.matchedLastTick;
            this.matchedLastTick = languageMatches;

            if (this.IsActive && !languageMatches)
            {
                this.IsActive = false;
                return ActivationChange.Deactivate;
            }

            if (!this.IsActive && languageMatches && matchedBefore)
            {
                this.IsActive = true;
                return ActivationChange.Activate;
            }

            return ActivationChange.None;
        }
    }
}
