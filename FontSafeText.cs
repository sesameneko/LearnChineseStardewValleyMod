using System.Text;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>
    /// Rewrites authored text into characters the game's font can actually draw.
    ///
    /// Deliberately free of StardewValley/MonoGame types so it can be unit-tested; see
    /// SegmentDataLoader for where it's applied.
    ///
    /// The game's fonts carry the glyphs its own text needs and nothing more, and XNA silently
    /// substitutes a SpriteFont's DefaultCharacter for anything missing -- '*' in this game. So
    /// Hepburn readings rendered as "ganj*sa" and "j*nansa" rather than ganjōsa and jūnansa. The
    /// data is right and stays right: macrons are the correct romanisation and matter if these
    /// readings are ever shown outside the game, so the substitution happens on the way in rather
    /// than by flattening 4500 authored readings on disk.
    ///
    /// Long vowels become doubled ones -- ō to oo -- rather than dropping the length, which loses
    /// the distinction between similar words. Doubling isn't always how the word is typed (がんじょう
    /// is "ganjou"), but the romaji alone can't tell us which kana produced the macron, and showing
    /// the length is worth more to a reader than matching an input convention.
    /// </summary>
    public static class FontSafeText
    {
        /// <summary>Returns text with every character the font lacks replaced by one it has.</summary>
        public static string Apply(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return text ?? string.Empty;

            // the overwhelmingly common case: nothing to do, so don't allocate
            bool needsWork = false;
            foreach (char c in text!)
            {
                if (c > 127)
                {
                    needsWork = true;
                    break;
                }
            }

            if (!needsWork)
                return text;

            var result = new StringBuilder(text.Length + 4);

            foreach (char c in text)
            {
                switch (c)
                {
                    // macron vowels: long vowels, shown by doubling
                    case 'ā': result.Append("aa"); break;
                    case 'ī': result.Append("ii"); break;
                    case 'ū': result.Append("uu"); break;
                    case 'ē': result.Append("ee"); break;
                    case 'ō': result.Append("oo"); break;
                    case 'Ā': result.Append("Aa"); break;
                    case 'Ī': result.Append("Ii"); break;
                    case 'Ū': result.Append("Uu"); break;
                    case 'Ē': result.Append("Ee"); break;
                    case 'Ō': result.Append("Oo"); break;

                    // accents that reached a gloss from an English loanword
                    case 'é': case 'è': case 'ê': result.Append('e'); break;
                    case 'á': case 'à': case 'â': result.Append('a'); break;
                    case 'í': case 'ì': case 'î': result.Append('i'); break;
                    case 'ó': case 'ò': case 'ô': result.Append('o'); break;
                    case 'ú': case 'ù': case 'û': result.Append('u'); break;
                    case 'ñ': result.Append('n'); break;
                    case 'ç': result.Append('c'); break;

                    // typographic punctuation, which authoring tools insert without being asked
                    case '—': case '–': result.Append("--"); break;
                    case '‘': case '’': result.Append('\''); break;
                    case '“': case '”': result.Append('"'); break;
                    case '…': result.Append("..."); break;
                    case ' ': result.Append(' '); break;

                    default: result.Append(c); break;
                }
            }

            return result.ToString();
        }
    }
}
