using System.Linq;
using LanguageStudyStardewValleyMod;
using Xunit;

namespace ModLogic.Tests
{
    public class LanguageActivationTests
    {
        private static readonly Sibling Japanese = new("com.oldclovercat.jplanguagestudy", "ja");
        private static readonly Sibling Chinese = new("com.oldclovercat.zhlanguagestudy", "zh");
        private static readonly Sibling Spanish = new("com.oldclovercat.eslanguagestudy", "es");

        [Fact]
        public void TrackerActivatesOnlyAfterTwoMatchingTicks()
        {
            var tracker = new ActivationTracker();

            Assert.Equal(ActivationChange.None, tracker.Step(true));
            Assert.False(tracker.IsActive);
            Assert.Equal(ActivationChange.Activate, tracker.Step(true));
            Assert.True(tracker.IsActive);
            Assert.Equal(ActivationChange.None, tracker.Step(true));
        }

        [Fact]
        public void TrackerDeactivatesOnTheFirstMismatch()
        {
            var tracker = new ActivationTracker();
            tracker.Step(true);
            tracker.Step(true);

            Assert.Equal(ActivationChange.Deactivate, tracker.Step(false));
            Assert.False(tracker.IsActive);
            Assert.Equal(ActivationChange.None, tracker.Step(false));
        }

        [Fact]
        public void TrackerIgnoresAOneTickMatch()
        {
            var tracker = new ActivationTracker();

            Assert.Equal(ActivationChange.None, tracker.Step(false));
            Assert.Equal(ActivationChange.None, tracker.Step(true));
            Assert.Equal(ActivationChange.None, tracker.Step(false));
            Assert.Equal(ActivationChange.None, tracker.Step(true));
            Assert.False(tracker.IsActive);
        }

        [Fact]
        public void TrackerReactivatesAfterSwitchingBack()
        {
            var tracker = new ActivationTracker();
            tracker.Step(true);
            tracker.Step(true);
            tracker.Step(false);

            Assert.Equal(ActivationChange.None, tracker.Step(true));
            Assert.Equal(ActivationChange.Activate, tracker.Step(true));
        }

        [Theory]
        [InlineData("ja", "ja", true)]
        [InlineData("ja", "JA", true)]
        [InlineData("ja", "en", false)]
        [InlineData("mod", "mod", false)]
        public void Matches(string study, string current, bool expected)
        {
            Assert.Equal(expected, LanguageActivation.Matches(study, current));
        }

        [Fact]
        public void NoPromptWhenTheGameIsInThisCopysLanguage()
        {
            var decision = LanguageActivation.DecidePrompt(Japanese.ModId, new[] { Japanese }, "ja");

            Assert.IsType<LanguagePromptDecision.None>(decision);
        }

        [Fact]
        public void NoPromptWhenTheGameIsInAnySiblingsLanguage()
        {
            var decision = LanguageActivation.DecidePrompt(Japanese.ModId, new[] { Japanese, Chinese }, "zh");

            Assert.IsType<LanguagePromptDecision.None>(decision);
        }

        [Fact]
        public void TheOnlyCopyOffersToSwitch()
        {
            var decision = LanguageActivation.DecidePrompt(Japanese.ModId, new[] { Japanese }, "en");

            var offer = Assert.IsType<LanguagePromptDecision.SwitchToMine>(decision);
            Assert.Equal("ja", offer.Language);
        }

        [Fact]
        public void OnlyTheLowestModIdPrompts()
        {
            var siblings = new[] { Japanese, Chinese, Spanish };

            // "eslanguagestudy" sorts first
            Assert.IsType<LanguagePromptDecision.ChooseAmong>(LanguageActivation.DecidePrompt(Spanish.ModId, siblings, "en"));
            Assert.IsType<LanguagePromptDecision.None>(LanguageActivation.DecidePrompt(Japanese.ModId, siblings, "en"));
            Assert.IsType<LanguagePromptDecision.None>(LanguageActivation.DecidePrompt(Chinese.ModId, siblings, "en"));
        }

        [Fact]
        public void LeaderIsFoundCaseInsensitively()
        {
            var siblings = new[] { Japanese, Chinese };

            var decision = LanguageActivation.DecidePrompt(Japanese.ModId.ToUpperInvariant(), siblings, "en");

            Assert.IsType<LanguagePromptDecision.ChooseAmong>(decision);
        }

        [Fact]
        public void ChoicesAreOrderedByDisplayName()
        {
            var decision = LanguageActivation.DecidePrompt(Spanish.ModId, new[] { Spanish, Japanese, Chinese }, "en");

            var choice = Assert.IsType<LanguagePromptDecision.ChooseAmong>(decision);
            Assert.Equal(new[] { "zh", "ja", "es" }, choice.Siblings.Select(sibling => sibling.Language));
        }

        [Fact]
        public void NoSiblingsMeansNoPrompt()
        {
            Assert.IsType<LanguagePromptDecision.None>(LanguageActivation.DecidePrompt(Japanese.ModId, new Sibling[0], "en"));
        }

        [Theory]
        [InlineData("ja", "Japanese")]
        [InlineData("ZH", "Chinese")]
        [InlineData("xx", "xx")]
        public void DisplayName(string code, string expected)
        {
            Assert.Equal(expected, LanguageActivation.DisplayName(code));
        }
    }
}
