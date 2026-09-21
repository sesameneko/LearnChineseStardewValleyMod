using StardewModdingAPI.Utilities;

namespace LanguageStudyStardewValleyMod
{
    public sealed class ModConfig
    {
        public bool TranslationEnabled { get; set; } = true;

        public KeybindList ToggleTranslation { get; set; } = KeybindList.Parse("F2");

        /// <summary>Pins the tooltip under the cursor so individual words in it can be hovered (Plan.md M2.1).</summary>
        public KeybindList FreezeTooltip { get; set; } = KeybindList.Parse("F3");

        /// <summary>The game's UI language to translate from, as a locale code (e.g. "ja"). Not yet exposed in the config UI.</summary>
        public string SourceLanguage { get; set; } = "ja";

        /// <summary>The language to translate into, as a locale code (e.g. "en"). Not yet exposed in the config UI.</summary>
        public string TargetLanguage { get; set; } = "en";
    }
}
