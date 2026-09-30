using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StardewModdingAPI;
using StardewValley;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>
    /// Loads the game's own string tables in two locales at once and joins them into a
    /// <see cref="TranslationMap"/> (source-language text -> target-language text).
    ///
    /// Built once per session rather than per hover, because the target-language half may need a
    /// temporary <see cref="LocalizedContentManager.CurrentLanguageCode"/> flip (see below) -- which
    /// must never happen mid-draw.
    /// </summary>
    public sealed class TranslationIndex
    {
        /// <summary>
        /// Locale suffixes the game's own Content/Strings files are actually published under
        /// (confirmed by inspecting the installed game's Content/Strings folder). English has no
        /// suffixed variant on disk -- it's the unsuffixed/default file -- so getting the English
        /// text while a non-English locale is active can't use the suffix trick and instead needs
        /// the CurrentLanguageCode-flip fallback.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> LocaleSuffixes = new Dictionary<string, string>
        {
            ["en"] = "",
            ["ja"] = "ja-JP",
            ["zh"] = "zh-CN",
            ["ru"] = "ru-RU",
            ["pt"] = "pt-BR",
            ["es"] = "es-ES",
            ["de"] = "de-DE",
            ["th"] = "th-TH",
            ["fr"] = "fr-FR",
            ["ko"] = "ko-KR",
            ["it"] = "it-IT",
            ["tr"] = "tr-TR",
            ["hu"] = "hu-HU",
        };

        /// <summary>
        /// Every Strings/* asset that is a plain Dictionary&lt;string,string&gt; in both locales
        /// (the list is the one extracted by tools/XnbStringTool -- see tools/extracted-strings).
        /// Item names/descriptions, the first target, live in Objects/BigCraftables/
        /// Tools/Weapons/Furniture/Shirts/Pants; the rest are included because they're free.
        /// Followed by the per-NPC/per-festival asset families outside Strings/ (the lists mirror
        /// what tools/extracted-strings/content-ja holds; `segtool.py audit` names any the game adds).
        /// </summary>
        private static readonly string[] StringTables = new[]
        {
            // item-ish tables first: on a duplicate source string, the first table added wins
            "Strings/Objects",
            "Strings/BigCraftables",
            "Strings/Tools",
            "Strings/Weapons",
            "Strings/Furniture",
            "Strings/Shirts",
            "Strings/Pants",
            "Strings/EnchantmentNames",
            "Strings/UI",
            "Strings/StringsFromCSFiles",
            "Strings/1_6_Strings",
            "Strings/Buildings",
            "Strings/BundleNames",
            "Strings/Characters",
            "Strings/Events",
            "Strings/FarmAnimals",
            "Strings/Lexicon",
            "Strings/Locations",
            "Strings/MovieConcessions",
            "Strings/MovieReactions",
            "Strings/Movies",
            "Strings/NPCNames",
            "Strings/Notes",
            "Strings/Quests",
            "Strings/SimpleNonVillagerDialogues",
            "Strings/SpecialOrderStrings",
            "Strings/SpeechBubbles",
            "Strings/StringsFromMaps",
            "Strings/WorldMap",
            "Strings/animationDescriptions",
        }
        // villager dialogue, festival chatter, TV and schedule lines -- flat tables that join on
        // shared keys like any other. Data/Events is left out: every value is a command script,
        // which never matches drawn text as a whole (its spoken lines are covered by the
        // segment data instead, where tools/segment-data lifts them out of the scripts). It is
        // loaded, though, for the dialogue bubble to look lines up in: see KeyedOnlyTables.
        .Concat(Family("Characters/Dialogue",
                "Abigail", "Alex", "Caroline", "Clint", "Demetrius", "Dwarf", "Elliott", "Emily", "Evelyn",
                "George", "Gil", "Gus", "Haley", "Harvey", "Jas", "Jodi", "Kent", "Krobus", "Leah", "Leo",
                "LeoMainland", "Lewis", "Linus", "Marnie", "MarriageDialogue", "MarriageDialogueAbigail",
                "MarriageDialogueAlex", "MarriageDialogueElliott", "MarriageDialogueEmily",
                "MarriageDialogueHaley", "MarriageDialogueHarvey", "MarriageDialogueKrobus",
                "MarriageDialogueLeah", "MarriageDialogueMaru", "MarriageDialoguePenny",
                "MarriageDialogueSam", "MarriageDialogueSebastian", "MarriageDialogueShane", "Maru",
                "Mister Qi", "Pam", "Penny", "Pierre", "Robin", "Sam", "Sandy", "Sebastian", "Shane",
                "Vincent", "Willy", "Wizard", "rainy"))
        .Concat(Family("Strings/schedules",
                "Abigail", "Alex", "Caroline", "Clint", "Demetrius", "Elliott", "Emily", "Evelyn", "George",
                "Gus", "Haley", "Harvey", "Jas", "Jodi", "Leah", "Leo", "Lewis", "Linus", "Marnie", "Maru",
                "Pam", "Penny", "Pierre", "Robin", "Sam", "Sandy", "Sebastian", "Shane", "Vincent", "Willy"))
        .Concat(Family("Data/Festivals",
                "FestivalDates", "fall16", "fall27", "spring13", "spring24", "summer11", "summer28",
                "winter25", "winter8"))
        .Concat(Family("Data/TV", "CookingChannel", "TipChannel"))
        .ToArray();

        /// <summary>
        /// Tables loaded only so the dialogue translation bubble can look entries up by key, and
        /// never joined into <see cref="Map"/>: event scripts are command lists that never match
        /// drawn text as a whole, and the two dialogue tables are left out of the map as they
        /// always were, so tooltips behave as before.
        /// </summary>
        private static readonly string[] KeyedOnlyTables = new[] { "Data/ExtraDialogue", "Data/EngagementDialogue" }
            .Concat(Family(EventsFolder,
                "AbandonedJojaMart", "AnimalShop", "ArchaeologyHouse", "Backwoods", "BathHouse_Pool", "Beach", "BoatTunnel",
                "BusStop", "CommunityCenter", "DesertFestival", "ElliottHouse", "Farm", "FarmHouse", "FishShop", "Forest",
                "HaleyHouse", "HarveyRoom", "Hospital", "IslandFarmHouse", "IslandHut", "IslandNorth", "IslandSouth",
                "IslandWest", "JoshHouse", "LeahHouse", "ManorHouse", "Mine", "Mountain", "QiNutRoom", "Railroad", "Saloon",
                "SamHouse", "SandyHouse", "ScienceHouse", "SebastianRoom", "SeedShop", "Sewer", "Sunroom", "Temp", "Tent",
                "Town", "Trailer", "Trailer_Big", "WizardHouse", "Woods"))
            .ToArray();

        private const string EventsFolder = "Data/Events";

        /// <summary>
        /// The two <c>Dictionary&lt;int,string&gt;</c> assets. Their raw values are records and notes
        /// that are never drawn as-is, so <see cref="Build"/> reshapes them with
        /// <see cref="DataTextShapes"/> into the Collections-page tooltip text before joining.
        /// </summary>
        private static readonly string[] IntKeyedTables = { Achievements, SecretNotes };

        private const string Achievements = "Data/Achievements";
        private const string SecretNotes = "Data/SecretNotes";

        private static IEnumerable<string> Family(string folder, params string[] names)
        {
            return names.Select(name => $"{folder}/{name}");
        }

        private readonly IModHelper helper;

        public TranslationIndex(IModHelper helper)
        {
            this.helper = helper;
        }

        /// <summary>The lookup built by the last successful <see cref="Build"/>; empty until then.</summary>
        public TranslationMap Map { get; private set; } = new();

        /// <summary>Every table loaded by the last <see cref="Build"/>, by asset name ("Characters/Dialogue/Abigail"), in the source language.</summary>
        public IReadOnlyDictionary<string, Dictionary<string, string>> SourceTables { get; private set; } = new Dictionary<string, Dictionary<string, string>>();

        /// <summary>The same tables in the target language.</summary>
        public IReadOnlyDictionary<string, Dictionary<string, string>> TargetTables { get; private set; } = new Dictionary<string, Dictionary<string, string>>();

        /// <summary>
        /// Looks an entry up by a game translation key ("Characters\\Dialogue\\Abigail:Mon") or an
        /// asset name and key, in the source or target language.
        /// </summary>
        public bool TryGetByKey(string translationKey, bool target, out string text)
        {
            text = "";
            int colon = translationKey.IndexOf(':');
            if (colon <= 0)
                return false;

            return this.TryGetEntry(translationKey.Substring(0, colon), translationKey.Substring(colon + 1), target, out text);
        }

        public bool TryGetEntry(string assetName, string key, bool target, out string text)
        {
            text = "";
            var tables = target ? this.TargetTables : this.SourceTables;
            return tables.TryGetValue(NormalizeAssetName(assetName), out var table) && table.TryGetValue(key, out text!);
        }

        /// <summary>The game writes asset names with either slash; the tables here are keyed with '/'.</summary>
        public static string NormalizeAssetName(string assetName)
        {
            return assetName.Replace('\\', '/');
        }

        /// <summary>The locale pair <see cref="Map"/> was built for, or null if it hasn't been built.</summary>
        public string? BuiltSourceLanguage { get; private set; }
        public string? BuiltTargetLanguage { get; private set; }

        /// <summary>Whether <see cref="Map"/> is already built for this exact locale pair.</summary>
        public bool IsBuiltFor(string sourceLanguage, string targetLanguage)
        {
            return this.BuiltSourceLanguage == sourceLanguage && this.BuiltTargetLanguage == targetLanguage;
        }

        /// <summary>
        /// (Re)builds the lookup for a locale pair. Must be called outside of drawing, since the
        /// English side may temporarily flip the game's active language.
        /// </summary>
        public void Build(string sourceLanguage, string targetLanguage)
        {
            if (sourceLanguage == targetLanguage)
            {
                ModEntry.Log($"Source and target language are both '{sourceLanguage}' -- nothing to translate.", LogLevel.Warn);
                this.Map = new TranslationMap();
                this.BuiltSourceLanguage = sourceLanguage;
                this.BuiltTargetLanguage = targetLanguage;
                return;
            }

            var sourceTables = this.LoadAllTables(sourceLanguage);
            var targetTables = this.LoadAllTables(targetLanguage);

            var map = new TranslationMap();
            foreach (string assetName in StringTables)
            {
                if (sourceTables.TryGetValue(assetName, out var source) && targetTables.TryGetValue(assetName, out var target))
                    map.AddTable(source, target);
            }

            this.AddShapedDataTables(map, sourceTables, targetTables);

            this.Map = map;
            this.SourceTables = sourceTables;
            this.TargetTables = targetTables;
            this.BuiltSourceLanguage = sourceLanguage;
            this.BuiltTargetLanguage = targetLanguage;

            ModEntry.Log($"Translation index built: {map.Count} '{sourceLanguage}' -> '{targetLanguage}' strings from {sourceTables.Count} tables.");
        }

        /// <summary>Joins Achievements and SecretNotes in the shape their tooltips are drawn in.</summary>
        private void AddShapedDataTables(TranslationMap map, Dictionary<string, Dictionary<string, string>> sourceTables, Dictionary<string, Dictionary<string, string>> targetTables)
        {
            if (sourceTables.TryGetValue(Achievements, out var sourceAchievements) && targetTables.TryGetValue(Achievements, out var targetAchievements))
                map.AddTable(DataTextShapes.AchievementTexts(sourceAchievements), DataTextShapes.AchievementTexts(targetAchievements));

            if (sourceTables.TryGetValue(SecretNotes, out var sourceNotes) && targetTables.TryGetValue(SecretNotes, out var targetNotes))
            {
                var (source, target) = DataTextShapes.SecretNoteParagraphs(sourceNotes, targetNotes, out int skipped);
                map.AddTable(source, target);
                if (skipped > 0)
                    ModEntry.Log($"{skipped} secret note(s) left out of the translation index: their two locales split into different numbers of paragraphs.", LogLevel.Trace);
            }

            if (sourceTables.TryGetValue("Strings/Locations", out var sourceLocations) && targetTables.TryGetValue("Strings/Locations", out var targetLocations))
            {
                foreach (var (sourceTemplate, targetTemplate) in DataTextShapes.NoteHeaderTemplates(sourceLocations, targetLocations))
                    map.AddPair(sourceTemplate, targetTemplate);
            }
        }

        /// <summary>
        /// Loads every string table in one locale. For a locale with a suffixed file on disk this is
        /// a plain suffixed load; for English (no suffixed file) the game's active language is
        /// flipped once for the whole batch and restored afterwards, rather than per asset.
        /// </summary>
        private Dictionary<string, Dictionary<string, string>> LoadAllTables(string localeCode)
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

            if (!LocaleSuffixes.TryGetValue(localeCode, out string? suffix))
            {
                ModEntry.Log($"Unknown locale code '{localeCode}' -- add it to TranslationIndex.LocaleSuffixes.", LogLevel.Warn);
                return result;
            }

            if (!string.IsNullOrEmpty(suffix))
            {
                this.LoadEach(result, localeCode, assetName => $"{assetName}.{suffix}", this.helper.GameContent.Load<Dictionary<string, string>>, this.helper.GameContent.Load<Dictionary<int, string>>);
                return result;
            }

            if (!Enum.TryParse<LocalizedContentManager.LanguageCode>(localeCode, ignoreCase: true, out var language))
            {
                ModEntry.Log($"'{localeCode}' isn't a recognized LocalizedContentManager.LanguageCode.", LogLevel.Warn);
                return result;
            }

            var originalLanguage = LocalizedContentManager.CurrentLanguageCode;
            try
            {
                LocalizedContentManager.CurrentLanguageCode = language;
                this.LoadEach(result, localeCode, assetName => assetName, Game1.content.Load<Dictionary<string, string>>, Game1.content.Load<Dictionary<int, string>>);
            }
            finally
            {
                LocalizedContentManager.CurrentLanguageCode = originalLanguage;
            }

            return result;
        }

        /// <summary>
        /// Loads every string- and int-keyed table through one locale's loaders. Int keys become their
        /// decimal text, so everything downstream deals in one shape.
        /// </summary>
        private void LoadEach(
            Dictionary<string, Dictionary<string, string>> result, string localeCode, Func<string, string> localized,
            Func<string, Dictionary<string, string>> loadStringKeyed, Func<string, Dictionary<int, string>> loadIntKeyed)
        {
            foreach (string assetName in StringTables.Concat(KeyedOnlyTables))
            {
                var table = this.TryLoad(() => loadStringKeyed(localized(assetName)), assetName, localeCode);
                if (table != null)
                    result[assetName] = table;
            }

            foreach (string assetName in IntKeyedTables)
            {
                var table = this.TryLoad(
                    () => loadIntKeyed(localized(assetName)).ToDictionary(pair => pair.Key.ToString(CultureInfo.InvariantCulture), pair => pair.Value),
                    assetName, localeCode);
                if (table != null)
                    result[assetName] = table;
            }
        }

        private Dictionary<string, string>? TryLoad(Func<Dictionary<string, string>> load, string assetName, string localeCode)
        {
            try
            {
                return load();
            }
            catch (Exception ex)
            {
                ModEntry.Log($"Couldn't load '{assetName}' for locale '{localeCode}': {ex.GetType().Name}: {ex.Message}", LogLevel.Trace);
                return null;
            }
        }
    }
}
