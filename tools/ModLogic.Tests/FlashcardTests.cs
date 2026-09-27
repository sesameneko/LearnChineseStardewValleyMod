using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LanguageStudyStardewValleyMod;

namespace ModLogic.Tests
{
    public class FlashcardTests
    {
        private static readonly DateTime T0 = new(2026, 9, 26, 0, 0, 0, DateTimeKind.Utc);

        private static readonly ContextRef InBirthday = new("Dialogue-Abigail", "AcceptBirthdayGift_Negative", 2, 3);
        private static readonly ContextRef InOther = new("Dialogue-Abigail", "Other", 0, 3);

        // ---- context pointers ----

        [Fact]
        public void Context_pointer_round_trips_through_its_string_form()
        {
            Assert.Equal("Dialogue-Abigail:AcceptBirthdayGift_Negative@2+3", InBirthday.ToString());
            Assert.True(ContextRef.TryParse(InBirthday.ToString(), out var parsed));
            Assert.Equal(InBirthday, parsed);
        }

        [Fact]
        public void Context_pointer_survives_keys_holding_its_own_delimiters()
        {
            // a real key: Strings/StringsFromCSFiles has "Saloon_Arcade_PK_NewGame+"
            var context = new ContextRef("StringsFromCSFiles", "Saloon_Arcade_PK_NewGame+", 10, 4);
            Assert.True(ContextRef.TryParse(context.ToString(), out var parsed));
            Assert.Equal(context, parsed);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("no-colon@1+2")]
        [InlineData("t:k")]
        [InlineData("t:k@x+2")]
        [InlineData("t:k@1+0")]
        [InlineData(":k@1+2")]
        public void Malformed_context_pointers_are_rejected(string? text)
        {
            Assert.False(ContextRef.TryParse(text, out _));
        }

        [Fact]
        public void A_pointer_resolves_only_while_its_span_still_holds_the_word()
        {
            var entries = new SourceEntries();
            entries.Add("Dialogue-Abigail", "AcceptBirthdayGift_Negative", "私の誕生日を覚えてて", "My birthday...");

            Assert.True(entries.TryResolve(InBirthday, "誕生日", out var entry));
            Assert.Equal("My birthday...", entry.English);

            // the data changed under the card: the span now holds something else
            Assert.False(entries.TryResolve(InBirthday, "行く", out _));
            // or runs off the end, or the entry is gone
            Assert.False(entries.TryResolve(InBirthday with { Offset = 50 }, "誕生日", out _));
            Assert.False(entries.TryResolve(InOther, "誕生日", out _));
        }

        // ---- context text ----

        private const string Birthday = "私の誕生日を覚えててくれたのね…ありがとう！ @！$h#$b#えっと…（クンクン）… 何これ？$s";

        [Fact]
        public void Context_shows_the_page_holding_the_word_without_markup()
        {
            int offset = Birthday.IndexOf("誕生日", StringComparison.Ordinal);
            var (page, pageIndex, highlight) = ContextText.PageAround(Birthday, offset, 3);

            Assert.Equal("私の誕生日を覚えててくれたのね…ありがとう！ (name)！", page);
            Assert.Equal(0, pageIndex);
            Assert.Equal((2, 3), highlight);
        }

        [Fact]
        public void Context_for_a_word_on_a_later_page_is_that_page()
        {
            int offset = Birthday.IndexOf("何これ", StringComparison.Ordinal);
            var (page, pageIndex, highlight) = ContextText.PageAround(Birthday, offset, 3);

            Assert.Equal("えっと…（クンクン）… 何これ？", page);
            Assert.Equal(1, pageIndex);
            Assert.Equal("何これ", page.Substring(highlight!.Value.Start, highlight.Value.Length));
        }

        [Fact]
        public void English_is_paged_alongside_when_the_page_counts_agree()
        {
            const string english = "You remembered my birthday for me... thank you! @!#$b#Um... (sniff sniff)... what is this?";
            Assert.Equal("Um... (sniff sniff)... what is this?", ContextText.EnglishPage(english, 1, ContextText.PageCount(Birthday)));

            // one page in English against two in Japanese: no telling which part goes with which
            Assert.Equal("Whole thing.", ContextText.EnglishPage("Whole thing.", 1, 2));
        }

        [Theory]
        [InlineData("ほら、見て！$h", "ほら、見て！")]
        [InlineData("#$1 Abigail1#ねえ、@。", "ねえ、(name)。")]
        [InlineData("どうする？#$r 958699 30 event_idea1#いいね", "どうする？いいね")]
        [InlineData("$h ほら", "ほら")]
        public void Clean_strips_dialogue_commands(string raw, string expected)
        {
            Assert.Equal(expected, ContextText.Clean(raw));
        }

        // ---- what a card holds ----

        [Theory]
        [InlineData("誕生日", "誕生日")]
        [InlineData("ありがとう！ ", "ありがとう")]
        [InlineData("を、", "を")]
        [InlineData("（クンクン）… ", "クンクン")]
        [InlineData("覚え\nてて", "覚えてて")]
        [InlineData("コーヒー。", "コーヒー")]
        [InlineData("…！", "")]
        public void Card_text_is_the_word_without_attached_punctuation(string segment, string expected)
        {
            Assert.Equal(expected, FlashcardDeck.CardText(segment));
        }

        [Theory]
        [InlineData("(object marker)", true)]
        [InlineData(" (topic marker) ", true)]
        [InlineData("(your name)", true)]
        [InlineData("(my) parents (subject marker)", false)]
        [InlineData("(I've) gotten used (to it)", false)]
        [InlineData("birthday", false)]
        [InlineData(null, false)]
        [InlineData("", false)]
        public void Grammar_glosses_are_single_parenthesised_notes(string? gloss, bool expected)
        {
            Assert.Equal(expected, FlashcardDeck.IsGrammarGloss(gloss));
        }

        // ---- the save gesture ----

        [Fact]
        public void Clicking_a_new_word_saves_it_with_its_gloss_and_sentence()
        {
            var deck = new FlashcardDeck();
            var (outcome, card) = deck.Toggle("ja", "誕生日", "たんじょうび", "birthday", InBirthday, T0);

            Assert.Equal(SaveOutcome.Added, outcome);
            Assert.Equal("誕生日", card!.Text);
            Assert.Equal(new[] { "birthday" }, card.Glosses);
            Assert.Equal(new[] { InBirthday.ToString() }, card.Contexts);
            Assert.Equal(T0, card.Created);
        }

        [Fact]
        public void Particles_and_placeholders_are_refused()
        {
            var deck = new FlashcardDeck();
            Assert.Equal(SaveOutcome.RefusedGrammar, deck.Toggle("ja", "を", "を", "(object marker)", InBirthday, T0).Outcome);
            Assert.Equal(SaveOutcome.RefusedEmpty, deck.Toggle("ja", "…！", "", "ellipsis", InBirthday, T0).Outcome);
            Assert.Empty(deck.Data.Cards);
        }

        [Fact]
        public void The_same_word_in_a_new_sentence_adds_that_sentence_to_its_card()
        {
            var deck = new FlashcardDeck();
            deck.Toggle("ja", "誕生日", "たんじょうび", "birthday", InBirthday, T0);
            var (outcome, card) = deck.Toggle("ja", "誕生日。", "たんじょうび", "birthday", InOther, T0.AddDays(1));

            Assert.Equal(SaveOutcome.ContextAdded, outcome);
            Assert.Single(deck.Data.Cards);
            Assert.Equal(2, card!.Contexts.Count);
            Assert.Equal(new[] { "birthday" }, card.Glosses);
        }

        [Fact]
        public void A_new_gloss_is_kept_alongside_the_old()
        {
            var deck = new FlashcardDeck();
            deck.Toggle("ja", "木", "き", "tree", InBirthday, T0);
            var (outcome, card) = deck.Toggle("ja", "木", "き", "wood", InOther, T0);

            Assert.Equal(SaveOutcome.ContextAdded, outcome);
            Assert.Equal(new[] { "tree", "wood" }, card!.Glosses);
        }

        [Fact]
        public void Clicking_a_saved_sentence_again_removes_it_and_the_last_one_removes_the_card()
        {
            var deck = new FlashcardDeck();
            deck.Toggle("ja", "誕生日", "たんじょうび", "birthday", InBirthday, T0);
            deck.Toggle("ja", "誕生日", "たんじょうび", "birthday", InOther, T0);

            var (outcome, card) = deck.Toggle("ja", "誕生日", "たんじょうび", "birthday", InBirthday, T0);
            Assert.Equal(SaveOutcome.ContextRemoved, outcome);
            Assert.Equal(new[] { InOther.ToString() }, card!.Contexts);

            Assert.Equal(SaveOutcome.CardRemoved, deck.Toggle("ja", "誕生日", "たんじょうび", "birthday", InOther, T0).Outcome);
            Assert.Empty(deck.Data.Cards);
        }

        [Fact]
        public void A_word_with_no_sentence_toggles_the_whole_card()
        {
            var deck = new FlashcardDeck();
            Assert.Equal(SaveOutcome.Added, deck.Toggle("ja", "午前", "ごぜん", "a.m.", null, T0).Outcome);
            Assert.Equal(SaveOutcome.CardRemoved, deck.Toggle("ja", "午前", "ごぜん", "a.m.", null, T0).Outcome);
            Assert.Empty(deck.Data.Cards);
        }

        [Fact]
        public void A_sentence_free_click_never_removes_a_card_that_has_sentences()
        {
            var deck = new FlashcardDeck();
            deck.Toggle("ja", "誕生日", "たんじょうび", "birthday", InBirthday, T0);

            Assert.Equal(SaveOutcome.AlreadySaved, deck.Toggle("ja", "誕生日", "たんじょうび", "birthday", null, T0).Outcome);
            Assert.Single(deck.Data.Cards);
        }

        [Fact]
        public void Readings_and_languages_keep_cards_apart()
        {
            var deck = new FlashcardDeck();
            deck.Toggle("ja", "上手", "じょうず", "skilful", InBirthday, T0);
            deck.Toggle("ja", "上手", "うわて", "superior", InBirthday, T0);
            deck.Toggle("zh", "上手", "じょうず", "skilful", InBirthday, T0);

            Assert.Equal(3, deck.Data.Cards.Count);
            Assert.Equal(2, deck.CardsFor("ja").Count());
            Assert.True(deck.Contains("ja", "上手、", "うわて"));
            Assert.False(deck.Contains("ja", "上手", "かみて"));
        }

        [Fact]
        public void Cards_order_by_age_or_by_fewest_passes()
        {
            var deck = new FlashcardDeck();
            var a = deck.Toggle("ja", "一", "いち", "one", null, T0).Card!;
            var b = deck.Toggle("ja", "二", "に", "two", null, T0.AddMinutes(1)).Card!;
            var c = deck.Toggle("ja", "三", "さん", "three", null, T0.AddMinutes(2)).Card!;
            a.Passes = 3;
            b.Passes = 0;
            c.Passes = 1;

            Assert.Equal(new[] { c, b, a }, deck.Ordered("ja", CardOrder.NewestFirst));
            Assert.Equal(new[] { a, b, c }, deck.Ordered("ja", CardOrder.OldestFirst));
            Assert.Equal(new[] { b, c, a }, deck.Ordered("ja", CardOrder.FewestCorrect));
        }

        [Fact]
        public void The_deck_round_trips_through_json()
        {
            var deck = new FlashcardDeck();
            deck.Toggle("ja", "誕生日", "たんじょうび", "birthday", InBirthday, T0);

            string json = JsonSerializer.Serialize(deck.Data);
            var loaded = new FlashcardDeck(JsonSerializer.Deserialize<FlashcardDeckData>(json));

            Assert.True(loaded.Contains("ja", "誕生日", "たんじょうび"));
            Assert.Equal(InBirthday.ToString(), loaded.Data.Cards[0].Contexts[0]);
        }

        // ---- review ----

        [Fact]
        public void A_review_pass_counts_answers_only_after_the_card_is_flipped()
        {
            var one = new Flashcard { Text = "一" };
            var two = new Flashcard { Text = "二" };
            var session = new ReviewSession(new[] { one, two });

            Assert.False(session.Grade(true)); // not flipped yet
            session.Flip();
            Assert.True(session.Grade(true));
            Assert.Same(two, session.Current);
            Assert.False(session.IsFlipped);

            session.Flip();
            session.Grade(false);

            Assert.True(session.IsFinished);
            Assert.Equal((1, 0), (one.Passes, one.Fails));
            Assert.Equal((0, 1), (two.Passes, two.Fails));
            Assert.Equal(1, session.PassedThisSession);
        }

        [Fact]
        public void Deleting_a_card_mid_review_keeps_the_place()
        {
            var cards = new[] { new Flashcard { Text = "一" }, new Flashcard { Text = "二" }, new Flashcard { Text = "三" } };
            var session = new ReviewSession(cards);
            session.Flip();
            session.Grade(true);

            session.Remove(cards[0]);
            Assert.Same(cards[1], session.Current);
            Assert.Equal(2, session.Count);

            session.Remove(cards[1]);
            Assert.Same(cards[2], session.Current);
        }

        // ---- provenance through the segment index ----

        [Fact]
        public void Segments_keep_their_source_through_a_lookup_of_wrapped_text()
        {
            var source = new SegmentSource("T", "k", 0, 3);
            var index = new SegmentIndex();
            index.TryAdd("誕生日を祝う", new TextSegment[]
            {
                new("誕生日", null, "birthday", "たんじょうび", source),
                new("を", null, "(object marker)", "を", source with { Offset = 3, Length = 1 }),
                new("祝う", null, "to celebrate", "いわう", source with { Offset = 4, Length = 2 }),
            });

            // the game wrapped it mid-word
            Assert.True(index.TryGetSegments("誕生日を祝\nう", out var segments));
            Assert.Equal(source, segments[0].Source);
            Assert.Equal(source with { Offset = 4, Length = 2 }, segments[^1].Source);
        }

        [Fact]
        public void A_token_the_game_filled_in_has_no_source_but_its_neighbours_do()
        {
            var source = new SegmentSource("T", "k", 0, 3);
            var index = new SegmentIndex();
            index.TryAdd("{0}に会った", new TextSegment[]
            {
                new("{0}", null, "(name)", null, source),
                new("に", null, "(target)", "に", source with { Offset = 3, Length = 1 }),
                new("会った", null, "met", "あった", source with { Offset = 4, Length = 3 }),
            });

            Assert.True(index.TryGetSegments("ハーヴェイさんに会った", out var segments));
            Assert.Null(segments[0].Source);
            Assert.Equal(source with { Offset = 4, Length = 3 }, segments[^1].Source);
        }

        [Fact]
        public void Every_authored_segment_resolves_back_to_its_own_text()
        {
            // the loader's offset arithmetic, over a real file: each segment's span must be exactly its text
            string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "assets", "segments", "ja", "Dialogue-Abigail.json");
            using var document = JsonDocument.Parse(File.ReadAllText(path));

            int checkedCount = 0;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                    continue;

                string japanese = property.Value.GetProperty("japanese").GetString()!;
                int offset = 0;
                foreach (var segment in property.Value.GetProperty("segments").EnumerateArray())
                {
                    string text = segment.GetProperty("text").GetString()!;
                    Assert.Equal(text, japanese.Substring(offset, text.Length));
                    offset += text.Length;
                    checkedCount++;
                }
            }

            Assert.True(checkedCount > 100);
        }
    }
}
