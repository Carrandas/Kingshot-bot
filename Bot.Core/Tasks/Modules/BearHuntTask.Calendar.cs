using Bot.Core.Logging;
using Bot.Core.Models;
using Bot.Core.Services;
using Bot.Core.Utils;
using OpenCvSharp;
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>"Read events", part two: the Events > Calendar week view, saved for the occasional event tasks</summary>
    public partial class BearHuntTask
    {
        private static readonly string EventsIconTemplate = Path.Combine("bearhunt", "events-icon.png");   // city, right column
        private static readonly string CalendarUtcTemplate = Path.Combine("bearhunt", "calendar-utc.png"); // "UTC Time:" banner of the Calendar tab
        private static readonly Rectangle EventsIconArea = new(580, 90, 140, 130);
        private static readonly Rectangle CalendarUtcArea = new(120, 220, 240, 70);
        private static readonly System.Drawing.Point CalendarTab = new(135, 145);
        private static readonly System.Drawing.Point EventsBack = new(48, 40);
        private static readonly System.Drawing.Point CalendarDragFrom = new(360, 1100);
        private static readonly System.Drawing.Point CalendarDragTo = new(360, 650);
        private const double CalendarThreshold = 0.90;   // icon 0.99 on the city (<= 0.63 elsewhere), banner 0.99 (<= 0.32)
        private const int MaxCalendarPages = 8;

        /// <summary>Reads the calendar once (after the Task List); a failure is logged and never fails the Task List read</summary>
        private async Task ReadEventCalendarAsync(AccountSettings account, LogService logger, CancellationToken ct)
        {
            try
            {
                int instance = account.InstanceNumber;
                var locator = new LocatorService(logger, account, TaskType);
                await locator.EnsureViewAsync(ViewType.BaseView, instance, ct);
                var read = await ReadCalendarPagesAsync(instance, logger, ct);
                await locator.EnsureViewAsync(ViewType.BaseView, instance, ct);
                if (read == null)
                {
                    logger.LogWarning("Read events: the event calendar did not open");
                    return;
                }
                EventCalendar.Save(read);
                logger.LogInfo($"Read events: calendar of the week {read.WeekStart:MM-dd}: " +
                    string.Join(", ", read.Events.Select(e => $"{e.Name} {e.FirstDay:ddd}-{e.LastDay:ddd}")));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Read events: calendar read failed: {ex.Message}");
            }
        }

        private async Task<CalendarRead?> ReadCalendarPagesAsync(int instance, LogService logger, CancellationToken ct)
        {
            var icon = await MatchAsync(EventsIconTemplate, EventsIconArea, CalendarThreshold, instance, logger);
            if (!icon.found) return null;
            await ClickCenterInRectAsync(instance, logger, icon.rect);
            await Task.Delay(2500, ct);
            await ClickAsync(instance, logger, CalendarTab);
            bool open = await WaitForAsync(async () => (await MatchAsync(CalendarUtcTemplate, CalendarUtcArea, CalendarThreshold, instance, logger)).found,
                TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(700), ct);
            if (!open) return null;

            var read = new CalendarRead { ReadUtc = DateTime.UtcNow, WeekStart = EventCalendar.WeekStartOf(DateTime.UtcNow) };
            byte[]? previous = null;
            for (int page = 0; page < MaxCalendarPages; page++)
            {
                var shot = await TakeScreenshotAsync(instance, logger);
                if (shot == null || (previous != null && SameScreen(previous, shot))) break;   // the end of the list
                using (var image = Cv2.ImDecode(shot, ImreadModes.Color))
                    EventCalendar.AddBars(read, EventCalendar.ReadBars(image));
                previous = shot;
                await SwipeAsync(instance, logger, CalendarDragFrom, CalendarDragTo, 1500);
                await Task.Delay(1200, ct);
            }
            await ClickAsync(instance, logger, EventsBack);
            await Task.Delay(1500, ct);
            return read;
        }

        /// <summary>The list didn't move: the calendar part of both screens is the same (the clock above it ticks)</summary>
        private static bool SameScreen(byte[] a, byte[] b)
        {
            using var ma = Cv2.ImDecode(a, ImreadModes.Grayscale);
            using var mb = Cv2.ImDecode(b, ImreadModes.Grayscale);
            var roi = new Rect(0, 400, 720, 790);
            using var ra = new Mat(ma, roi);
            using var rb = new Mat(mb, roi);
            using var diff = new Mat();
            Cv2.Absdiff(ra, rb, diff);
            return Cv2.Mean(diff).Val0 < 2.0;
        }
    }
}
