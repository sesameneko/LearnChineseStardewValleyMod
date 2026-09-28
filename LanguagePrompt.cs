using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>
    /// Offers, once per launch on the title screen, to switch the game into a language one of the
    /// installed copies of this mod studies. The decision is <see cref="LanguageActivation.DecidePrompt"/>;
    /// this half finds the sibling copies and shows the popup.
    ///
    /// A choice does nothing but set the game language, exactly as the game's own language menu
    /// does (<c>LanguageSelectionMenu.ApplyLanguage</c>). The title menu saves it to the startup
    /// preferences, and each copy's own poll then activates or deactivates it. The copies never
    /// need to talk to each other.
    /// </summary>
    internal static class LanguagePrompt
    {
        /// <summary>Set once the title screen has been checked this launch, whether or not a popup was shown.</summary>
        private static bool checkedThisLaunch;

        /// <summary>The game's current language code, e.g. "ja", or "mod" for a custom language.</summary>
        public static string CurrentLanguage()
        {
            return LocalizedContentManager.CurrentLanguageCode.ToString();
        }

        /// <summary>The study language a copy of this mod declares in its manifest, or null if it isn't one.</summary>
        public static string? StudyLanguageOf(IManifest manifest)
        {
            return manifest.ExtraFields.TryGetValue(LanguageActivation.ManifestField, out object? value)
                && value?.ToString() is { Length: > 0 } language
                    ? language
                    : null;
        }

        /// <summary>Every installed copy of this mod, this one included.</summary>
        public static List<Sibling> FindSiblings(IModHelper helper)
        {
            var siblings = new List<Sibling>();
            foreach (var mod in helper.ModRegistry.GetAll())
            {
                if (StudyLanguageOf(mod.Manifest) is { } language)
                    siblings.Add(new Sibling(mod.Manifest.UniqueID, language));
            }

            return siblings;
        }

        /// <summary>Called every tick. Waits for the title screen to settle, then decides once whether to ask.</summary>
        public static void Poll(IModHelper helper, IManifest manifest)
        {
            if (checkedThisLaunch)
                return;

            // after the intro, so the popup isn't drawn over the logo animation, and never on top
            // of a submenu the player already opened
            if (Game1.activeClickableMenu is not TitleMenu title || !title.titleInPosition || TitleMenu.subMenu is not null)
                return;

            checkedThisLaunch = true;

            var siblings = FindSiblings(helper);
            switch (LanguageActivation.DecidePrompt(manifest.UniqueID, siblings, CurrentLanguage()))
            {
                case LanguagePromptDecision.SwitchToMine offer:
                    string name = LanguageActivation.DisplayName(offer.Language);
                    TitleMenu.subMenu = new ConfirmationDialog(
                        $"{manifest.Name}: the game must be in {name} to use this mod. Switch?",
                        onConfirm: _ => SwitchTo(offer.Language),
                        onCancel: _ => Close()
                    );
                    break;

                case LanguagePromptDecision.ChooseAmong choice:
                    var options = choice.Siblings
                        .Select(sibling => (LanguageActivation.DisplayName(sibling.Language), (Action)(() => SwitchTo(sibling.Language))))
                        .Append(("Cancel", (Action)Close))
                        .ToList();
                    TitleMenu.subMenu = new LanguageChoiceMenu(
                        "Several Language Study mods are installed. Which language do you want to study? The game will switch to it.",
                        options
                    );
                    break;
            }
        }

        private static void SwitchTo(string language)
        {
            if (!Enum.TryParse<LocalizedContentManager.LanguageCode>(language, ignoreCase: true, out var code))
            {
                ModEntry.Log($"Can't switch the game to '{language}': it isn't one of the game's language codes.", LogLevel.Error);
                Close();
                return;
            }

            ModEntry.Log($"Switching the game language to {LanguageActivation.DisplayName(language)}.");

            // the game's own order: apply the language, then close the menu
            LocalizedContentManager.CurrentLanguageCode = code;
            Close();
        }

        private static void Close()
        {
            TitleMenu.subMenu = null;
        }
    }
}
