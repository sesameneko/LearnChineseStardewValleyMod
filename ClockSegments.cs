using System.Collections.Generic;

namespace LanguageStudyStardewValleyMod
{
    /// <summary>
    /// Segment data for the Japanese HUD clock (DayTimeMoneyBox), which no string table holds.
    ///
    /// The game builds both lines in code, with a hardcoded branch per locale -- for ja, per
    /// DayTimeMoneyBox.draw in the installed 1.6.15 IL:
    ///  - date: <c>{day}日 ({weekday})</c>, the weekday from Game1._shortDayDisplayName[day % 7]
    ///    (StringsFromCSFiles Game1.cs.3042-3048, 日 月 火 水 木 金 土);
    ///  - time: <c>{午前|午後} {h}:{mm}</c>, 午前 before 12:00 and from 24:00 on, the hour on a
    ///    12-hour clock where ja writes noon and midnight as 0 rather than 12.
    /// So the frames live here, hardcoded to match. Every string the clock can draw is enumerated
    /// as an exact entry rather than a token template: the readings are irregular per value
    /// (1日 is ついたち, 4時 is よじ), which a template's shared segment can't carry.
    ///
    /// Game-free so it can be unit-tested.
    /// </summary>
    public static class ClockSegments
    {
        /// <summary>Weekdays in the order the game indexes them (day % 7, so day 1 is Monday).</summary>
        private static readonly (string Kanji, string Kana, string English)[] Weekdays =
        {
            ("日", "にち", "Sunday (lit. sun)"),
            ("月", "げつ", "Monday (lit. moon)"),
            ("火", "か", "Tuesday (lit. fire)"),
            ("水", "すい", "Wednesday (lit. water)"),
            ("木", "もく", "Thursday (lit. wood)"),
            ("金", "きん", "Friday (lit. metal)"),
            ("土", "ど", "Saturday (lit. earth)"),
        };

        /// <summary>The kana reading of <c>{n}日</c> as a day of the month, irregular up to 10 and at 14, 20 and 24.</summary>
        private static readonly Dictionary<int, string> IrregularDays = new()
        {
            [1] = "ついたち",
            [2] = "ふつか",
            [3] = "みっか",
            [4] = "よっか",
            [5] = "いつか",
            [6] = "むいか",
            [7] = "なのか",
            [8] = "ようか",
            [9] = "ここのか",
            [10] = "とおか",
            [14] = "じゅうよっか",
            [20] = "はつか",
            [24] = "にじゅうよっか",
        };

        /// <summary>Digit readings as they're said before 日 as a date: 7 is しち and 9 is く (17日 is じゅうしちにち).</summary>
        private static readonly string[] DayUnits = { "", "いち", "に", "さん", "よん", "ご", "ろく", "しち", "はち", "く" };

        /// <summary>The kana reading of <c>{h}時</c>; 4, 7 and 9 take their on'yomi-only forms.</summary>
        private static readonly string[] Hours =
        {
            "れいじ", "いちじ", "にじ", "さんじ", "よじ", "ごじ",
            "ろくじ", "しちじ", "はちじ", "くじ", "じゅうじ", "じゅういちじ",
        };

        /// <summary>The kana reading of whole tens of minutes; the clock only ever steps by ten.</summary>
        private static readonly string[] TensOfMinutes = { "", "じゅっぷん", "にじゅっぷん", "さんじゅっぷん", "よんじゅっぷん", "ごじゅっぷん" };

        /// <summary>Every date and time string the ja clock can draw, with its segments.</summary>
        public static IEnumerable<(string Japanese, IReadOnlyList<TextSegment> Segments)> All()
        {
            for (int day = 1; day <= 28; day++)
                yield return Date(day);

            // 6:00 am through 2:00 am the next morning, as Game1.timeOfDay runs 600 to 2600
            for (int timeOfDay = 600; timeOfDay <= 2600; timeOfDay += 10)
            {
                if (timeOfDay % 100 < 60)
                    yield return Time(timeOfDay);
            }
        }

        /// <summary>Adds every clock string to an index; returns how many were accepted.</summary>
        public static int AddTo(SegmentIndex index)
        {
            int added = 0;
            foreach (var (japanese, segments) in All())
            {
                if (index.TryAdd(japanese, segments))
                    added++;
            }

            return added;
        }

        /// <summary>The date line for a day of the season, e.g. "1日 (月)".</summary>
        public static (string Japanese, IReadOnlyList<TextSegment> Segments) Date(int day)
        {
            var weekday = Weekdays[day % 7];
            string kana = IrregularDays.TryGetValue(day, out string? irregular) ? irregular : DayNumber(day) + "にち";

            var segments = new[]
            {
                new TextSegment($"{day}日 ", null, $"day {day} (of the month)", kana),
                new TextSegment($"({weekday.Kanji})", null, weekday.English, weekday.Kana),
            };

            return (SegmentIndex.Concat(segments), segments);
        }

        /// <summary>The time line for a <c>Game1.timeOfDay</c> value, e.g. 1310 -> "午後 1:10".</summary>
        public static (string Japanese, IReadOnlyList<TextSegment> Segments) Time(int timeOfDay)
        {
            int hour24 = timeOfDay / 100;
            int minutes = timeOfDay % 100;
            bool morning = timeOfDay < 1200 || timeOfDay >= 2400;
            int hour = hour24 % 12;

            // ja writes noon and midnight as 0; English readers expect 12
            int englishHour = hour == 0 ? 12 : hour;

            string clock = $"{hour}:{minutes:00}";
            string english = minutes == 0
                ? $"{englishHour} o'clock"
                : $"{englishHour}:{minutes:00}";

            var segments = new[]
            {
                morning
                    ? new TextSegment("午前 ", null, "a.m. (lit. before noon)", "ごぜん")
                    : new TextSegment("午後 ", null, "p.m. (lit. after noon)", "ごご"),
                new TextSegment(clock, null, english, Hours[hour] + (minutes == 0 ? "" : " " + TensOfMinutes[minutes / 10])),
            };

            return (SegmentIndex.Concat(segments), segments);
        }

        /// <summary>The kana reading of a day number from 1 to 99, for the regular days.</summary>
        private static string DayNumber(int n)
        {
            int tens = n / 10;
            string reading = tens switch
            {
                0 => "",
                1 => "じゅう",
                _ => DayUnits[tens] + "じゅう",
            };

            return reading + DayUnits[n % 10];
        }
    }
}
