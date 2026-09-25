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
            Assert.Equal(28 + (21 * 6) - 5, count); // 6:00 through 2:00, ten-minute steps
        }

        [Theory]
        [InlineData(1, "1日 (月)", "ついたち", "Monday (lit. moon)")]
        [InlineData(7, "7日 (日)", "なのか", "Sunday (lit. sun)")]
        [InlineData(14, "14日 (日)", "じゅうよっか", "Sunday (lit. sun)")]
        [InlineData(17, "17日 (水)", "じゅうしちにち", "Wednesday (lit. water)")]
        [InlineData(28, "28日 (日)", "にじゅうはちにち", "Sunday (lit. sun)")]
        public void Date_matches_the_game_format_and_reads_the_day_correctly(int day, string drawn, string dayKana, string weekday)
        {
            Assert.True(ClockIndex().TryGetSegments(drawn, out var segments));
            Assert.Equal(new[] { $"{day}日 ", drawn.Substring(drawn.IndexOf('(')) }, segments.Select(s => s.Text));
            Assert.Equal(dayKana, segments[0].Kana);
            Assert.Equal(weekday, segments[1].Gloss);
        }

        [Theory]
        [InlineData(600, "午前 6:00", "ごぜん", "ろくじ", "6 o'clock")]
        [InlineData(1150, "午前 11:50", "ごぜん", "じゅういちじ ごじゅっぷん", "11:50")]
        [InlineData(1200, "午後 0:00", "ごご", "れいじ", "12 o'clock")]
        [InlineData(1910, "午後 7:10", "ごご", "しちじ じゅっぷん", "7:10")]
        [InlineData(2400, "午前 0:00", "ごぜん", "れいじ", "12 o'clock")]
        [InlineData(2600, "午前 2:00", "ごぜん", "にじ", "2 o'clock")]
        public void Time_matches_the_game_format(int timeOfDay, string drawn, string periodKana, string clockKana, string clockGloss)
        {
            Assert.Equal(drawn, ClockSegments.Time(timeOfDay).Japanese);

            Assert.True(ClockIndex().TryGetSegments(drawn, out var segments));
            Assert.Equal(2, segments.Count);
            Assert.Equal(periodKana, segments[0].Kana);
            Assert.Equal(clockKana, segments[1].Kana);
            Assert.Equal(clockGloss, segments[1].Gloss);
        }

        [Fact]
        public void Maps_onto_the_single_drawn_line()
        {
            Assert.True(ClockIndex().TryGetSegments("午後 1:30", out var segments));
            Assert.True(SegmentIndex.TryGetSegmentsForLine(segments, new[] { "午後 1:30" }, 0, out var line));
            Assert.Equal(new[] { "午後 ", "1:30" }, line.Select(s => s.Text));
        }
    }
}
