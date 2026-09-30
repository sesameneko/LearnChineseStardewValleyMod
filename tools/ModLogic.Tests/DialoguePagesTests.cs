using System.Text.Json;
using LanguageStudyStardewValleyMod;

namespace ModLogic.Tests
{
    public class DialoguePagesTests
    {
        private static readonly string ExtractedDir =
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "extracted-strings");

        private static Dictionary<string, string> LoadContent(string locale, string asset)
        {
            string path = Path.GetFullPath(Path.Combine(ExtractedDir, $"content-{locale}", asset + ".json"));
            Assert.True(File.Exists(path), $"Expected extracted strings at '{path}' (regenerate with tools/XnbStringTool).");

            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);

            var entries = new Dictionary<string, string>();
            foreach (var property in document.RootElement.GetProperty("entries").EnumerateObject())
                entries[property.Name] = property.Value.GetString()!;
            return entries;
        }

        private static IEnumerable<string> DialogueAssets()
        {
            string folder = Path.Combine(ExtractedDir, "content-ja", "Characters", "Dialogue");
            return Directory.GetFiles(folder, "*.json").Select(path => "Characters/Dialogue/" + Path.GetFileNameWithoutExtension(path));
        }

        private static string NoGender(string text) => text;

        private static string? Tokens(string name) => name == "@" ? "Ben" : null;

        /// <summary>
        /// A stand-in for parseDialogueString on entries with no conditional commands: every text
        /// segment of 2+ characters becomes a page, $b marks the previous page as continued, and the
        /// substitution is the identity. Returns null for entries that use anything else.
        /// </summary>
        private static (List<string> Lines, List<SpecialCharacterCall> Calls)? SimulateParse(string[] segments)
        {
            var lines = new List<string>();
            var calls = new List<SpecialCharacterCall>();

            foreach (string segment in segments)
            {
                if (segment.Length < 2)
                    continue;

                calls.Add(new SpecialCharacterCall(segment, segment));
                if (segment == "$b")
                {
                    if (lines.Count > 0)
                        lines[^1] += "{";
                }
                else if (segment is "$e" or "$k")
                    continue;
                else if (segment.StartsWith('$'))
                    return null;
                else
                    lines.Add(segment);
            }

            return (lines, calls);
        }

        [Fact]
        public void Alternative_follows_the_week()
        {
            Assert.Equal(0, DialoguePages.AlternativeIndex(1, 100));
            Assert.Equal(0, DialoguePages.AlternativeIndex(2, 6));
            Assert.Equal(1, DialoguePages.AlternativeIndex(2, 7));
            Assert.Equal(2, DialoguePages.AlternativeIndex(3, 20));
        }

        [Fact]
        public void Pages_map_back_to_their_segments_across_a_break()
        {
            string[] segments = "こんにちは、@。#$b#元気？$h".Split('#');
            var calls = new List<SpecialCharacterCall>
            {
                new("こんにちは、@。", "こんにちは、ベン。"),
                new("$b", "$b"),
                new("元気？$h", "元気？$h"),
            };

            int[] callSegments = DialoguePages.SegmentsOfCalls(segments, calls);
            int[] lineSegments = DialoguePages.SegmentsOfLines(new[] { "こんにちは、ベン。{", "元気？$h" }, calls, callSegments);

            Assert.Equal(new[] { 0, 2 }, lineSegments);
        }

        [Fact]
        public void A_chance_branch_maps_to_the_segment_the_game_picked()
        {
            // $c 0.5#A#B: the game rolled for B, so only B was substituted and made a page
            string[] segments = "$c 0.5#ええと。#そうだね。".Split('#');
            var calls = new List<SpecialCharacterCall> { new("$c 0.5", "$c 0.5"), new("そうだね。", "そうだね。") };

            int[] lineSegments = DialoguePages.SegmentsOfLines(new[] { "そうだね。" }, calls, DialoguePages.SegmentsOfCalls(segments, calls));

            Assert.Equal(new[] { 2 }, lineSegments);
        }

        [Fact]
        public void A_mail_branch_page_is_matched_despite_its_mail_prefix()
        {
            string[] segments = "$1 letter#初めて。#$e#また？".Split('#');
            var calls = new List<SpecialCharacterCall> { new("$1 letter", "$1 letter"), new("初めて。", "初めて。") };

            int[] lineSegments = DialoguePages.SegmentsOfLines(new[] { "letter}初めて。" }, calls, DialoguePages.SegmentsOfCalls(segments, calls));

            Assert.Equal(new[] { 1 }, lineSegments);
        }

        [Fact]
        public void Pages_with_no_text_or_no_call_are_unknown()
        {
            var calls = new List<SpecialCharacterCall> { new("やあ。", "やあ。") };
            int[] lineSegments = DialoguePages.SegmentsOfLines(new[] { "", "やあ。", "他の文。" }, calls, new[] { 0 });

            Assert.Equal(new[] { -1, 0, -1 }, lineSegments);
        }

        [Fact]
        public void Cleans_markup_out_of_an_english_page()
        {
            Assert.Equal("Hi Ben. Want this?", DialoguePages.Clean("Hi @. Want this?[395]$h", NoGender, Tokens));
            Assert.Equal("Ugh...", DialoguePages.Clean("%Ugh...$s", NoGender, Tokens));
            Assert.Equal("I'll see you later.", DialoguePages.Clean("I'll see you later.%noturn", NoGender, Tokens));
            Assert.Equal("It's ... today.", DialoguePages.Clean("It's %adj today.$12", NoGender, Tokens));
            Assert.Equal("Costs $500.", DialoguePages.Clean("Costs $500.", NoGender, Tokens));
        }

        [Fact]
        public void Question_pages_list_their_answers()
        {
            string[] segments = "$q 101 null#Do you like it?#$r 101 10 yes#Yes!#$r 101 -10 no#No.".Split('#');

            Assert.Equal("Do you like it?\n\n> Yes!\n> No.", DialoguePages.Page(segments, 1, NoGender, Tokens));
            Assert.Null(DialoguePages.Page(segments, 0, NoGender, Tokens));
        }

        [Fact]
        public void Whole_entry_lists_the_text_pages()
        {
            Assert.Equal("Hi.\nBye.\n...", DialoguePages.WholeEntry("Hi.$h#$b#Bye.#$e#...", 0, NoGender, Tokens));
            Assert.Equal("Want one?\n\n> Sure.", DialoguePages.WholeEntry("$q 1 null#Want one?#$r 1 0 a#Sure.", 0, NoGender, Tokens));
        }

        [Fact]
        public void Recovers_template_arguments()
        {
            Assert.Equal(new[] { "カボチャ" }, DialoguePages.TemplateArguments("{0}をありがとう！", "カボチャをありがとう！"));
            Assert.Equal(new[] { "A", "B" }, DialoguePages.TemplateArguments("{1}と{0}", "BとA"));
            Assert.Equal(Array.Empty<string>(), DialoguePages.TemplateArguments("やあ", "やあ"));
            Assert.Null(DialoguePages.TemplateArguments("{0}をありがとう！", "全然違う"));
            Assert.Equal("Thanks for the Pumpkin! {1}", DialoguePages.FillTemplate("Thanks for the {0}! {1}", new[] { "Pumpkin" }));
        }

        [Fact]
        public void Gendered_entries_take_the_same_half()
        {
            Assert.Equal(("やったね！", "Yay!"), DialoguePages.MatchingHalves("やったね！/やったわ！", "やったね！", "Yay!/Hooray!"));
            Assert.Equal(("{0}だわ", "It's {0}, dear"), DialoguePages.MatchingHalves("{0}だね/{0}だわ", "春だわ", "It's {0}/It's {0}, dear"));
            Assert.Equal(("1/2だ", "Half/whole"), DialoguePages.MatchingHalves("1/2だ", "1/2だ", "Half/whole"));
        }

        [Fact]
        public void Splits_event_commands_like_the_game()
        {
            Assert.Equal(new[] { "speak", "Sam", "Hi @.#$b#I wanted to talk..." }, DialoguePages.SplitCommand("speak Sam \"Hi @.#$b#I wanted to talk...\""));
            Assert.Equal(new[] { "message", "He said \"no\"" }, DialoguePages.SplitCommand("message \"He said \\\"no\\\"\""));
        }

        [Fact]
        public void Finds_the_command_nearest_the_current_one()
        {
            var commands = new[] { "pause 500", "speak Sam \"やあ。\"", "pause 100", "speak Sam \"じゃあね。\"" };

            Assert.Equal((3, 2), DialoguePages.FindCommandWithText(commands, 2, "じゃあ\nね。"));
            Assert.Equal((1, 2), DialoguePages.FindCommandWithText(commands, 1, "やあ。"));
            Assert.Null(DialoguePages.FindCommandWithText(commands, 1, "無い"));
        }

        [Fact]
        public void English_argument_must_be_the_same_command()
        {
            var japanese = DialoguePages.SplitCommand("speak Sam \"やあ。\"");

            Assert.Equal("Hi.", DialoguePages.EnglishArgument(new[] { "pause 1", "speak Sam \"Hi.\"" }, 1, japanese, 2));
            Assert.Null(DialoguePages.EnglishArgument(new[] { "pause 1", "speak Lewis \"Hi.\"" }, 1, japanese, 2));
            Assert.Null(DialoguePages.EnglishArgument(new[] { "pause 1" }, 1, japanese, 2));
        }

        /// <summary>
        /// Every real event script should be found again from its own parsed commands, and every
        /// speak line in it should have an English counterpart spoken by the same actor.
        /// </summary>
        [Fact]
        public void Real_event_speak_lines_have_english_counterparts()
        {
            static IReadOnlyList<string> Parse(string script) => SplitScript(script);

            int speak = 0, found = 0;
            foreach (string path in Directory.GetFiles(Path.Combine(ExtractedDir, "content-ja", "Data", "Events"), "*.json"))
            {
                string asset = "Data/Events/" + Path.GetFileNameWithoutExtension(path);
                var japanese = LoadContent("ja", asset);
                var english = LoadContent("en", asset);
                var parsed = japanese.ToDictionary(pair => pair.Key, pair => Parse(pair.Value));

                foreach (var (key, commands) in parsed)
                {
                    for (int i = 0; i < commands.Count; i++)
                    {
                        if (!commands[i].StartsWith("speak ", StringComparison.Ordinal))
                            continue;

                        speak++;
                        Assert.Equal(key, DialoguePages.FindScript(parsed.Select(p => new KeyValuePair<string, IReadOnlyList<string>>(p.Key, p.Value)), commands, i));

                        var args = DialoguePages.SplitCommand(commands[i]);
                        if (english.TryGetValue(key, out string? englishScript) && DialoguePages.EnglishArgument(Parse(englishScript), i, args, 2) is not null)
                            found++;
                    }
                }
            }

            Assert.True(speak > 1000, $"only {speak} speak commands found");
            Assert.True(found >= speak * 0.99, $"{found} of {speak} speak lines have an English counterpart");
        }

        /// <summary>Event.ParseCommands without its token parsing: '/' outside quotes.</summary>
        private static IReadOnlyList<string> SplitScript(string script)
        {
            var commands = new List<string>();
            var current = new System.Text.StringBuilder();
            bool quoted = false;
            foreach (char c in script)
            {
                if (c == '"')
                    quoted = !quoted;
                if (c == '/' && !quoted)
                {
                    commands.Add(current.ToString());
                    current.Clear();
                }
                else
                    current.Append(c);
            }

            commands.Add(current.ToString());
            return commands;
        }

        /// <summary>
        /// Across every villager's dialogue: for each entry the stand-in parser can handle, each page
        /// maps back to the segment it was made from, and the English entry has a page for it.
        /// </summary>
        [Fact]
        public void Real_dialogue_pages_find_their_english_page()
        {
            int pages = 0, mapped = 0, translated = 0;

            foreach (string asset in DialogueAssets())
            {
                var japanese = LoadContent("ja", asset);
                var english = LoadContent("en", asset);

                foreach (var (key, raw) in japanese)
                {
                    if (!english.TryGetValue(key, out string? englishRaw))
                        continue;

                    for (int alternative = 0; alternative < DialoguePages.Alternatives(raw).Length; alternative++)
                    {
                        string[] segments = DialoguePages.Segments(raw, alternative);
                        if (SimulateParse(segments) is not var (lines, calls))
                            continue;

                        int[] lineSegments = DialoguePages.SegmentsOfLines(lines, calls, DialoguePages.SegmentsOfCalls(segments, calls));
                        string[]? englishSegments = DialoguePages.MatchingSegments(raw, englishRaw, alternative);

                        for (int line = 0; line < lines.Count; line++)
                        {
                            pages++;
                            if (lineSegments[line] < 0)
                                continue;

                            Assert.Equal(lines[line].TrimEnd('{'), segments[lineSegments[line]]);
                            mapped++;

                            if (englishSegments is not null && DialoguePages.Page(englishSegments, lineSegments[line], NoGender, Tokens) is not null)
                                translated++;
                        }
                    }
                }
            }

            Assert.True(pages > 4000, $"only {pages} pages simulated");
            Assert.Equal(pages, mapped);
            Assert.True(translated >= pages * 0.99, $"{translated} of {pages} pages have an English page");
        }
    }
}
