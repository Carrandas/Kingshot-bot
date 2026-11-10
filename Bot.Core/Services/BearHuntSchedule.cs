using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Bot.Core.Utils;

namespace Bot.Core.Services
{
    /// <summary>
    /// The map's Task List (tap the UTC banner at the top of the map): date headers ("Today", "2026/11/08") and one
    /// row per event with its UTC time ("Bear Hunt - Trap 2" ... "UTC 13:00"). Finds the next bear of our trap.
    /// </summary>
    public static class BearHuntSchedule
    {
        /// <summary>A header (date) or an event row (title and the time column's text) as OCR read it</summary>
        public sealed record TaskListItem(bool IsHeader, string Text, string Time = "");

        private static readonly Regex DateRegex = new(@"(\d{4})\s*/\s*(\d{1,2})\s*/\s*(\d{1,2})", RegexOptions.None, RegexTimeouts.Default);
        private static readonly Regex TimeRegex = new(@"(\d{1,2})\s*[:.]\s*(\d{2})", RegexOptions.None, RegexTimeouts.Default);
        // OCR reads the trap's "1" as l, I or | now and then
        private static readonly Regex BearRegex = new(@"bear.*trap\s*([12lI|])", RegexOptions.IgnoreCase, RegexTimeouts.Default);

        /// <summary>
        /// The earliest bear of <paramref name="trap"/> in the list that is not over yet at <paramref name="nowUtc"/>
        /// (one still running counts: joining late helps). Rows take the date of the header above them; rows before
        /// any header are today's.
        /// </summary>
        public static DateTime? NextBear(IEnumerable<TaskListItem> items, int trap, DateTime nowUtc, TimeSpan eventLength)
        {
            var date = nowUtc.Date;
            DateTime? best = null;
            foreach (var item in items)
            {
                if (item.IsHeader)
                {
                    date = HeaderDate(item.Text, nowUtc) ?? date;
                    continue;
                }
                if (BearTime(item, trap, date) is DateTime start && start + eventLength > nowUtc && (best == null || start < best))
                    best = start;
            }
            return best;
        }

        /// <summary>"Today" or "2026/11/08"; null when the text is neither</summary>
        internal static DateTime? HeaderDate(string text, DateTime nowUtc)
        {
            if (text.Contains("today", StringComparison.OrdinalIgnoreCase)) return nowUtc.Date;
            if (text.Contains("tomorrow", StringComparison.OrdinalIgnoreCase)) return nowUtc.Date.AddDays(1);
            var m = DateRegex.Match(text);
            return m.Success && DateTime.TryParseExact($"{m.Groups[1].Value}-{m.Groups[2].Value.PadLeft(2, '0')}-{m.Groups[3].Value.PadLeft(2, '0')}",
                "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
                ? DateTime.SpecifyKind(d.Date, DateTimeKind.Utc) : null;
        }

        /// <summary>The start of a "Bear Hunt - Trap N" row of our trap on <paramref name="date"/>; null for other rows</summary>
        internal static DateTime? BearTime(TaskListItem row, int trap, DateTime date)
        {
            var bear = BearRegex.Match(row.Text);
            if (!bear.Success || TrapNumber(bear.Groups[1].Value) != trap) return null;
            var time = TimeRegex.Match(row.Time);
            if (!time.Success) return null;
            int hours = int.Parse(time.Groups[1].Value), minutes = int.Parse(time.Groups[2].Value);
            if (hours > 23 || minutes > 59) return null;
            return DateTime.SpecifyKind(date.Date, DateTimeKind.Utc).AddHours(hours).AddMinutes(minutes);
        }

        private static int TrapNumber(string text) => text == "2" ? 2 : 1;
    }
}
