using StardewModdingAPI.Utilities;

namespace LanguageStudyStardewValleyMod
{
    public sealed class ModConfig
    {
        public bool TranslationEnabled { get; set; } = true;

        // letter keys rather than function keys: on macOS F-keys need Fn by default.
        // Both are unbound in vanilla (which reserves WASD/C/X/V/Y/F/M/E/T/Tab/Escape/1-0) and
        // avoid keys popular mods claim (F1 Lookup Anything, B Chests Anywhere, P CJB Cheats,
        // I Item Spawner, U Automate). Left-hand keys, since the right hand is on the mouse.
        public KeybindList ToggleTranslation { get; set; } = KeybindList.Parse("G");

        /// <summary>Locks the tooltip under the cursor on/off so individual words in it can be hovered (Plan.md M2.1).</summary>
        public KeybindList FreezeTooltip { get; set; } = KeybindList.Parse("Z");

        /// <summary>Pins the tooltip under the cursor only for as long as this is held down (Plan.md M2.1).</summary>
        public KeybindList HoldFreezeTooltip { get; set; } = KeybindList.Parse("RightShift");

        /// <summary>The game's UI language to translate from, as a locale code (e.g. "ja"). Not yet exposed in the config UI.</summary>
        public string SourceLanguage { get; set; } = "ja";

        /// <summary>The language to translate into, as a locale code (e.g. "en"). Not yet exposed in the config UI.</summary>
        public string TargetLanguage { get; set; } = "en";
    }
}
