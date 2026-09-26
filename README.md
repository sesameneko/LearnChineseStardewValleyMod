# Language Study — a Stardew Valley mod for learning Japanese

Language Study turns Stardew Valley into a Japanese reading practice tool. You play the game with its language set to Japanese, and the mod helps you read it. Hover a tooltip to see its English translation, hover a single word to see its reading and meaning, and click a word to save it as a flashcard you can review from the pause menu.

The goal is to let you play the game in the language you're learning without having to stop and look things up. Every piece of Japanese text the game ships has been hand-split into words with readings and glosses.

> **Status:** early development (v0.1.0). The core features work, but expect rough edges. Japanese → English is the only language pair so far.

## Installation

1. Install the mod loader, SMAPI, and learn how mods are installed by following the
   [Stardew Valley Wiki's modding guide for players](https://stardewvalleywiki.com/Modding:Player_Guide/Getting_Started).
2. Download the latest release from this repository's
   [Releases page](https://github.com/sesameneko/LearnJapaneseStardewValleyMod/releases) and unzip it into your `Mods` folder.
3. *(Optional but recommended)* Install [Generic Mod Config Menu](https://www.nexusmods.com/stardewvalley/mods/5098) to change the mod's settings and keybinds in game.
4. Launch the game through SMAPI and set the in-game language to **日本語 (Japanese)**.

### Building from source

With the .NET SDK and Stardew Valley installed:

```
dotnet build LanguageStudyStardewValleyMod.csproj
```

The build finds your game folder and copies the mod into `Mods` automatically.

## Features

- **Tooltip translation.** When you hover an item, button or other game tooltip, a second tooltip with the game's official English text appears next to it. This includes text the game fills in at draw time, such as `日記 （F）` → `Journal (F)`.
- **Word-by-word hover.** Hover any single word in Japanese text, including dialogue, menus, mail, quests, the HUD clock and a frozen tooltip, to see its English gloss, romaji and kana. The word boundaries come from hand-segmented data covering every piece of Japanese text in the game, not from an automatic tokenizer.
- **Frozen tooltips.** Lock the tooltip under your cursor in place so you can move the mouse across it and hover the words inside it.
- **Flashcards.** Left-click a hovered word to save it, along with the sentence you found it in. Review your cards from a new tab in the pause menu, and browse or delete them there too. Cards are stored outside the mod folder, so updating the mod doesn't erase them.
- **Readable romaji.** The game's font has no long-vowel marks, so the mod adds them (ā, ī, ū, ē, ō).

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

Bug reports and suggestions are welcome. Please [open an issue](https://github.com/sesameneko/LearnJapaneseStardewValleyMod/issues) and include:

- what you were doing and what you expected to happen,
- your SMAPI log: upload it at [smapi.io/log](https://smapi.io/log) and paste the link,
- for a missing or wrong translation, the exact Japanese text and where it appeared in the game (a screenshot helps).

Pull requests are welcome too. Before you start:

- **Keep testable logic out of game types.** Code that doesn't need a live game object goes in a plain class covered by the xunit tests in `tools/ModLogic.Tests` (run with `dotnet test` from that folder).
- **Fix translation and word data at its source.** Word segments, readings and glosses live in `tools/extracted-strings/literal-translations/`, and the tools for editing and validating them are in `tools/segment-data/` (see that folder's README). Don't edit the generated copy under `assets/segments/`.
