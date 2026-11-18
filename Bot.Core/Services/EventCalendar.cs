using Bot.Core.Models;
using Bot.Core.Utils;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bot.Core.Services
{
    /// <summary>One bar of the Events > Calendar week view: the event and its first and last UTC day</summary>
    public sealed record CalendarEvent(string Name, DateTime FirstDay, DateTime LastDay);

    /// <summary>What the calendar read saved: the week it showed (its Monday) and the bars on it</summary>
    public sealed class CalendarRead
    {
        public DateTime ReadUtc { get; set; }
        public DateTime WeekStart { get; set; }
        public List<CalendarEvent> Events { get; set; } = new();
    }

    /// <summary>
    /// The game's event calendar (Events > Calendar tab): a week view, Monday to Sunday, with one bar per event over the
    /// days it runs. The daily "Read events" task saves it; occasional event tasks (Voyage of Light, Alchemy Junction,
    /// Alliance Mobilization) ask <see cref="MayRun"/> and skip the days their event isn't on. The calendar only ever
    /// keeps a task from running when it has a read of this week with enough bars that lacks the event: no read, an
    /// old read or a poor one leaves the decision to the task's own check (its icon or tab).
    /// </summary>
    public static class EventCalendar
    {
        /// <summary>The bar label of each event task (prefix match: long labels end in "...")</summary>
        public static readonly IReadOnlyDictionary<TaskType, string> BarNames = new Dictionary<TaskType, string>
        {
            [TaskType.VoyageOfLight] = "Voyage of Light",
            [TaskType.AlchemyJunction] = "Brew Workshop",
            [TaskType.AllianceMobilization] = "Alliance Mobilization",
        };

        /// <summary>Fewer bars than this and the read is not trusted to say an event is missing (a full week shows ~20)</summary>
        internal const int MinTrustedBars = 6;

        private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "data", "event_calendar.json");
        private static readonly object _lock = new();
        private static CalendarRead? _read;
        private static bool _loaded;

        /// <summary>Raised after a new read is saved</summary>
        public static event Action? Changed;

        public static CalendarRead? Current
        {
            get
            {
                lock (_lock)
                {
                    if (!_loaded) { _read = Load(); _loaded = true; }
                    return _read;
                }
            }
        }

        public static void Save(CalendarRead read)
        {
            lock (_lock)
            {
                _read = read;
                _loaded = true;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    File.WriteAllText(FilePath, JsonSerializer.Serialize(read, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[EventCalendar] Error saving: {ex.Message}");
                }
            }
            Changed?.Invoke();
        }

        /// <summary>For tests: use this read instead of the saved one</summary>
        internal static void SetForTest(CalendarRead? read)
        {
            lock (_lock) { _read = read; _loaded = true; }
        }

        private static CalendarRead? Load()
        {
            try
            {
                return File.Exists(FilePath) ? JsonSerializer.Deserialize<CalendarRead>(File.ReadAllText(FilePath)) : null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EventCalendar] Error loading: {ex.Message}");
                return null;
            }
        }

        public static DateTime WeekStartOf(DateTime utc) => utc.Date.AddDays(-(((int)utc.DayOfWeek + 6) % 7));

        /// <summary>False only when this week's trusted read lacks the task's event today (see the class summary)</summary>
        public static bool MayRun(TaskType task, DateTime nowUtc) => MayRun(task, nowUtc, Current);

        internal static bool MayRun(TaskType task, DateTime nowUtc, CalendarRead? read)
        {
            if (!BarNames.TryGetValue(task, out var name) || read == null) return true;
            if (read.WeekStart != WeekStartOf(nowUtc) || read.Events.Count < MinTrustedBars) return true;
            var found = Find(read, name);
            return found.Count > 0 && found.Any(e => nowUtc.Date >= e.FirstDay && nowUtc.Date <= e.LastDay);
        }

        /// <summary>The calendar bars of the task's event in the saved read (empty when unknown)</summary>
        public static IReadOnlyList<CalendarEvent> For(TaskType task) =>
            BarNames.TryGetValue(task, out var name) && Current != null ? Find(Current, name) : Array.Empty<CalendarEvent>();

        private static List<CalendarEvent> Find(CalendarRead read, string name) =>
            read.Events.Where(e => SameEvent(e.Name, name)).ToList();

        /// <summary>
        /// "Viking..." matches "Viking Vengeance": letters only, one contains the other (6+ letters; the label OCR can
        /// pick up a stray letter from the icon in front of it)
        /// </summary>
        internal static bool SameEvent(string label, string name)
        {
            string a = Normalize(label), b = Normalize(name);
            if (a.Length < 6 || b.Length < 6) return a == b && a.Length > 0;
            return a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal);
        }

        private static string Normalize(string text) =>
            Regex.Replace(text.ToLowerInvariant(), "[^a-z]", "", RegexOptions.None, RegexTimeouts.Default);

        #region Reading a screenshot

        private const int Left = 15;           // Monday's column starts here; a bar from the edge started earlier
        private const double ColumnWidth = 98.6; // Mon 15-112 ... Sun 608-706
        private const int Top = 395;           // under the day header
        private const int Bottom = 1195;       // above "See in-game schedule"
        private const int MinBarPixels = 80;
        private const int MinBarHeight = 36;   // bars are ~50 px high; a cut-off one at the edge is skipped

        /// <summary>The bars fully on screen: label, first and last day column (0 = Monday)</summary>
        internal static List<(string Label, int FirstColumn, int LastColumn)> ReadBars(Mat image)
        {
            using var hsv = image.CvtColor(ColorConversionCodes.BGR2HSV);
            var bars = new List<(string, int, int)>();
            foreach (var (top, bottom) in BarBands(hsv))
            {
                int mid = (top + bottom) / 2;
                var (left, right) = Extent(hsv, mid);
                if (right - left < 60) continue;
                string label = MaskedTextOcr.Read(image, new System.Drawing.Rectangle(left, top, right - left, bottom - top),
                    new Scalar(0, 0, 225), new Scalar(180, 60, 255), "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz .'");
                label = CleanLabel(label);
                if (label.Length < 4) continue;
                bars.Add((label, Column(left + 5), Column(right - 15)));
            }
            return bars;
        }

        /// <summary>
        /// The OCR reads bits of the icon and the arrow as stray letters ("oN Hero Roulette D", "Viking... D"): keep the
        /// text before "...", and drop 1-2 letter words at either end
        /// </summary>
        internal static string CleanLabel(string text)
        {
            int dots = text.IndexOf("..", StringComparison.Ordinal);
            if (dots >= 0) text = text[..dots];
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            while (words.Count > 1 && words[0].Length <= 2) words.RemoveAt(0);
            while (words.Count > 1 && words[^1].Length <= 2) words.RemoveAt(words.Count - 1);
            return string.Join(' ', words).Trim('.', ' ');
        }

        internal static int Column(int x) => Math.Clamp((int)((x - Left) / ColumnWidth), 0, 6);

        /// <summary>A bar pixel: saturated, bright, not the tan group header (H 17, S 128) nor today's teal column</summary>
        private static bool IsBar(Vec3b p)
        {
            int h = p.Item0, s = p.Item1, v = p.Item2;
            if (s < 90 || v < 140) return false;
            bool header = h >= 15 && h <= 19 && s >= 118 && s <= 138;
            bool teal = h >= 84 && h <= 92 && s < 85;
            return !header && !teal;
        }

        private static IEnumerable<(int Top, int Bottom)> BarBands(Mat hsv)
        {
            int start = -1;
            for (int y = Top; y <= Bottom; y++)
            {
                bool on = y < Bottom && CountBar(hsv, y) >= MinBarPixels;
                if (on && start < 0) start = y;
                if (on || start < 0) continue;
                if (y - start >= MinBarHeight && start > Top) yield return (start, y - 1);
                start = -1;
            }
        }

        private static int CountBar(Mat hsv, int y)
        {
            int n = 0;
            for (int x = Left; x < 706; x += 2)
                if (IsBar(hsv.At<Vec3b>(y, x))) n++;
            return n * 2;
        }

        private static (int Left, int Right) Extent(Mat hsv, int y)
        {
            int left = -1, right = -1;
            for (int x = 10; x < 712; x++)
            {
                if (!IsBar(hsv.At<Vec3b>(y, x))) continue;
                if (left < 0) left = x;
                right = x;
            }
            return (left, right);
        }

        /// <summary>Adds a page's bars to the read, by label (scrolled pages overlap)</summary>
        internal static void AddBars(CalendarRead read, IEnumerable<(string Label, int FirstColumn, int LastColumn)> bars)
        {
            foreach (var (label, first, last) in bars)
            {
                if (read.Events.Any(e => Normalize(e.Name) == Normalize(label))) continue;
                read.Events.Add(new CalendarEvent(label, read.WeekStart.AddDays(first), read.WeekStart.AddDays(last)));
            }
        }

        #endregion
    }
}
