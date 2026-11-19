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
        // The city's Events icon, shared with Alliance Mobilization: animated and often with a red dot (0.77 with the dot on
        // bearhunt/events-icon.png, below its 0.90: the read of 2026-11-19 missed it), so the tolerant template and threshold
        private static readonly string EventsIconTemplate = Path.Combine("alliancemobilization", AllianceMobilizationTask.EventsButtonTemplate);
        private static readonly string CalendarUtcTemplate = Path.Combine("bearhunt", "calendar-utc.png"); // "UTC Time:" banner of the Calendar tab
        private static readonly Rectangle CalendarUtcArea = new(120, 220, 240, 70);
        private static readonly System.Drawing.Point CalendarTab = new(135, 145);
        private static readonly System.Drawing.Point EventsBack = new(48, 40);
        private static readonly System.Drawing.Point CalendarDragFrom = new(360, 1100);
        private static readonly System.Drawing.Point CalendarDragTo = new(360, 650);
        private const double CalendarThreshold = 0.90;   // banner 0.99 on the Calendar tab (<= 0.32 elsewhere)
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
                logger.LogInfo($"Read events: calendar {read.WeekStart:MM-dd} to {read.WeekStart.AddDays(6):MM-dd}: " +
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

        /// <summary>The icon a few times (the city's icons slide in after the map), with the score and a screenshot on a miss</summary>
        private async Task<(bool found, Rectangle rect, double confidence)> FindEventsIconAsync(int instance, LogService logger, CancellationToken ct)
        {
            var icon = (found: false, rect: Rectangle.Empty, confidence: 0.0);
            for (int attempt = 0; attempt < 3 && !icon.found; attempt++)
            {
                if (attempt > 0) await Task.Delay(1000, ct);
                icon = await MatchAsync(EventsIconTemplate, AllianceMobilizationTask.EventsButtonArea, AllianceMobilizationTask.EventsThreshold, instance, logger);
            }
            if (!icon.found)
            {
                logger.LogWarning($"Read events: no Events icon in the city (best {icon.confidence:F2})");
                await SaveStepScreenshotAsync("calendar-no-events-icon", instance, logger);
            }
            return icon;
        }

        private async Task<CalendarRead?> ReadCalendarPagesAsync(int instance, LogService logger, CancellationToken ct)
        {
            var icon = await FindEventsIconAsync(instance, logger, ct);
            if (!icon.found) return null;
            await ClickCenterInRectAsync(instance, logger, icon.rect);
            await Task.Delay(2500, ct);
            await ClickAsync(instance, logger, CalendarTab);
            bool open = await WaitForAsync(async () => (await MatchAsync(CalendarUtcTemplate, CalendarUtcArea, CalendarThreshold, instance, logger)).found,
                TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(700), ct);
            if (!open) return null;

            var read = new CalendarRead { ReadUtc = DateTime.UtcNow };
            byte[]? previous = null;
            for (int page = 0; page < MaxCalendarPages; page++)
            {
                var shot = await TakeScreenshotAsync(instance, logger);
                if (shot == null || (previous != null && SameScreen(previous, shot))) break;   // the end of the list
                using (var image = Cv2.ImDecode(shot, ImreadModes.Color))
                {
                    if (page == 0) read.WeekStart = ViewStart(image, logger);
                    EventCalendar.AddBars(read, EventCalendar.ReadBars(image));
                }
                previous = shot;
                await SwipeAsync(instance, logger, CalendarDragFrom, CalendarDragTo, 1500);
                await Task.Delay(1200, ct);
            }
            await ClickAsync(instance, logger, EventsBack);
            await Task.Delay(1500, ct);
            return read;
        }

        /// <summary>The date of the view's first column, from today's teal header cell</summary>
        private static DateTime ViewStart(Mat image, LogService logger)
        {
            int? today = EventCalendar.TodayColumn(image);
            if (today == null) logger.LogWarning($"Read events: today's column not found in the calendar header, taking column {EventCalendar.DefaultTodayColumn}");
            return DateTime.UtcNow.Date.AddDays(-(today ?? EventCalendar.DefaultTodayColumn));
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
