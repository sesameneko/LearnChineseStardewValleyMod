using System.Linq;
using LanguageStudyStardewValleyMod;

namespace ModLogic.Tests
{
    public class ClockSegmentsTests
    {
        private static SegmentIndex ClockIndex()
        {
            var index = new SegmentIndex();
            ClockSegments.AddTo(index);
            return index;
        }

        [Fact]
        public void Every_clock_string_satisfies_the_concatenation_invariant()
        {
            // TryAdd rejects any entry whose segments don't reproduce its text
            int count = ClockSegments.All().Count();
            Assert.Equal(count, ClockSegments.AddTo(new SegmentIndex()));
            Assert.Equal(28, count);
        }

        [Theory]
        [InlineData(1, "1日 星期一", "yī rì", "xīng qī yī", "Monday")]
        [InlineData(7, "7日 星期天", "qī rì", "xīng qī tiān", "Sunday")]
        [InlineData(10, "10日 星期三", "shí rì", "xīng qī sān", "Wednesday")]
        [InlineData(14, "14日 星期天", "shí sì rì", "xīng qī tiān", "Sunday")]
        [InlineData(20, "20日 星期六", "èr shí rì", "xīng qī liù", "Saturday")]
        [InlineData(28, "28日 星期天", "èr shí bā rì", "xīng qī tiān", "Sunday")]
        public void Date_matches_the_game_format_and_reads_the_day(int day, string drawn, string dayPinyin, string weekdayPinyin, string weekday)
        {
            Assert.Equal(drawn, ClockSegments.Date(day).Chinese);

            Assert.True(ClockIndex().TryGetSegments(drawn, out var segments));
            Assert.Equal(new[] { $"{day}日 ", drawn.Substring(drawn.IndexOf(' ') + 1) }, segments.Select(s => s.Text));
            Assert.Equal(dayPinyin, segments[0].Reading);
            Assert.Equal(weekdayPinyin, segments[1].Reading);
            Assert.Equal(weekday, segments[1].Gloss);
        }

        [Fact]
        public void Maps_onto_the_single_drawn_line()
        {
            Assert.True(ClockIndex().TryGetSegments("3日 星期三", out var segments));
            Assert.True(SegmentIndex.TryGetSegmentsForLine(segments, new[] { "3日 星期三" }, 0, out var line));
            Assert.Equal(new[] { "3日 ", "星期三" }, line.Select(s => s.Text));
        }
    }
}
