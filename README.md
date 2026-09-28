# Language Study (Chinese): a Stardew Valley mod for learning Chinese

Language Study turns Stardew Valley into a Chinese reading practice tool. You play the game with its language set to Simplified Chinese, and the mod helps you read it. Hover a tooltip to see its English translation, hover a single word to see its pinyin and meaning, and click a word to save it as a flashcard you can review from the pause menu.

The goal is to let you play the game in the language you're learning without having to stop and look things up. Every piece of Chinese text the game ships is being hand-split into words with pinyin and glosses.

> **Status:** not yet playable. This is a port of the Japanese version of the mod, in progress. The Chinese word data is being authored and the pinyin display isn't built yet. See "Chinese migration" in `TODOs.md`.

## Installation

1. Install the mod loader, SMAPI, and learn how mods are installed by following the
   [Stardew Valley Wiki's modding guide for players](https://stardewvalleywiki.com/Modding:Player_Guide/Getting_Started).
2. Download the latest release from this repository's
   [Releases page](https://github.com/sesameneko/LearnChineseStardewValleyMod/releases) (none yet) and unzip it into your `Mods` folder.
3. *(Optional but recommended)* Install [Generic Mod Config Menu](https://www.nexusmods.com/stardewvalley/mods/5098) to change the mod's settings and keybinds in game.
4. Launch the game through SMAPI and set the in-game language to **中文 (Chinese)**.

### Building from source

With the .NET SDK and Stardew Valley installed:

```
dotnet build LanguageStudyStardewValleyMod.csproj
```

The build finds your game folder and copies the mod into `Mods` automatically.

## Features

- **Tooltip translation.** When you hover an item, button or other game tooltip, a second tooltip with the game's official English text appears next to it. This includes text the game fills in at draw time, such as an item name inside a quest.
- **Word-by-word hover.** Hover any single word in Chinese text, including dialogue, menus, mail, quests, the HUD clock and a frozen tooltip, to see its English gloss and pinyin. The word boundaries come from hand-segmented data covering every piece of Chinese text in the game, not from an automatic tokenizer.
- **Frozen tooltips.** Lock the tooltip under your cursor in place so you can move the mouse across it and hover the words inside it.
- **Flashcards.** Left-click a hovered word to save it, along with the sentence you found it in. Review your cards from a new tab in the pause menu, and browse or delete them there too. Cards are stored outside the mod folder, so updating the mod doesn't erase them.
- **Tone marks.** The game's Chinese font lacks the first- and third-tone vowels (ā, ǎ, …), so the mod adds them.

### Default controls

| Action | Key |
|---|---|
| Toggle translation on/off | `G` |
| Lock / unlock the tooltip under the cursor | `Z` |
| Freeze the tooltip while held | `Right Shift` |
| Save a hovered word as a flashcard | Left-click |
| Flashcard review: flip / missed / knew it | `Space` / `1` / `2` |

You can rebind all of these, and turn off click-to-save, in Generic Mod Config Menu or in the mod's `config.json`.

## Contributing & reporting issues

Bug reports and suggestions are welcome. Please [open an issue](https://github.com/sesameneko/LearnChineseStardewValleyMod/issues) and include:

- what you were doing and what you expected to happen,
- your SMAPI log: upload it at [smapi.io/log](https://smapi.io/log) and paste the link,
- for a missing or wrong translation, the exact Chinese text and where it appeared in the game (a screenshot helps).

Pull requests are welcome too. Before you start:

- **Keep testable logic out of game types.** Code that doesn't need a live game object goes in a plain class covered by the xunit tests in `tools/ModLogic.Tests` (run with `dotnet test` from that folder).
- **Fix translation and word data at its source.** Word segments, pinyin and glosses live in `assets/segments/zh/`, and the tools for editing and validating them are in `tools/segment-data/` (see that folder's README).
