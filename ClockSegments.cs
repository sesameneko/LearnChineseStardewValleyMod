using System.Collections.Generic;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>
    /// Segment data for the Chinese HUD clock (DayTimeMoneyBox), which no string table holds.
    ///
    /// The game builds the date in code, with a hardcoded branch per locale -- for zh, per
    /// DayTimeMoneyBox.draw in the installed 1.6.15 IL: <c>{day}日 {weekday}</c>, the weekday from
    /// Game1.shortDayDisplayNameFromDayOfSeason (StringsFromCSFiles Game1.cs.3042-3048, 星期天 to
    /// 星期六). The time line is digits only, so it has no words to hover and needs no data.
    /// Every date is enumerated as an exact entry rather than a template, so each can carry the
    /// day's own reading.
    ///
    /// Game-free so it can be unit-tested.
    /// </summary>
    public static class ClockSegments
    {
        /// <summary>Weekdays in the order the game indexes them (day % 7, so day 1 is Monday).</summary>
        private static readonly (string Chinese, string Pinyin, string English)[] Weekdays =
        {
            ("星期天", "xīng qī tiān", "Sunday"),
            ("星期一", "xīng qī yī", "Monday"),
            ("星期二", "xīng qī èr", "Tuesday"),
            ("星期三", "xīng qī sān", "Wednesday"),
            ("星期四", "xīng qī sì", "Thursday"),
            ("星期五", "xīng qī wǔ", "Friday"),
            ("星期六", "xīng qī liù", "Saturday"),
        };

        private static readonly string[] Digits = { "", "yī", "èr", "sān", "sì", "wǔ", "liù", "qī", "bā", "jiǔ" };

        /// <summary>Every date string the zh clock can draw, with its segments.</summary>
        public static IEnumerable<(string Chinese, IReadOnlyList<TextSegment> Segments)> All()
        {
            for (int day = 1; day <= 28; day++)
                yield return Date(day);
        }

        /// <summary>Adds every clock string to an index; returns how many were accepted.</summary>
        public static int AddTo(SegmentIndex index)
        {
            int added = 0;
            foreach (var (chinese, segments) in All())
            {
                if (index.TryAdd(chinese, segments))
                    added++;
            }

            return added;
        }

        /// <summary>The date line for a day of the season, e.g. "1日 星期一".</summary>
        public static (string Chinese, IReadOnlyList<TextSegment> Segments) Date(int day)
        {
            var weekday = Weekdays[day % 7];

            var segments = new[]
            {
                new TextSegment($"{day}日 ", Number(day) + " rì", $"day {day} (of the month)"),
                new TextSegment(weekday.Chinese, weekday.Pinyin, weekday.English),
            };

            return (SegmentIndex.Concat(segments), segments);
        }

        /// <summary>The pinyin of a number from 1 to 99, one syllable per hanzi: 21 is "èr shí yī".</summary>
        public static string Number(int n)
        {
            int tens = n / 10, units = n % 10;
            string reading = tens switch
            {
                0 => "",
                1 => "shí",
                _ => Digits[tens] + " shí",
            };

            return units == 0 ? reading : (reading.Length == 0 ? Digits[units] : reading + " " + Digits[units]);
        }
    }
}
