using Bot.Core.ImageDetection;
using Bot.Core.Logging;
using Bot.Core.Models;
using Bot.Core.Services;
using Bot.Core.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// Voyage of Light, a timed event in the Deals hub (ship tab): Claim All the returned ships, then Dispatch All.
    /// Both are the same button spot at the bottom left (green Claim All turns into teal Dispatch All); "Complete All"
    /// next to it costs event tokens and is never tapped. A voyage takes 8 h. Without the tab (event over) it waits
    /// for the next 00:00 UTC reset.
    /// </summary>
    public class VoyageOfLightTask : BaseTaskWithCommonPatterns, IScheduledTask
    {
        public override TaskType TaskType => TaskType.VoyageOfLight;
        public override string Name => "Voyage of Light";

        private static readonly string DealsIconTemplate = Path.Combine("collectrewards", "deals-icon.png");     // gift icon on the base view
        private static readonly string DealsHeaderTemplate = Path.Combine("collectrewards", "deals-header.png"); // "Deals" title bar of the hub
        private const string ShipTabTemplate = "ship-tab.png";      // ship icon of the unselected tab
        private const string TitleTemplate = "title.png";           // "Voyage of Light" title on the tab
        internal const string ClaimAllTemplate = "claim-all.png";
        internal const string DispatchAllTemplate = "dispatch-all.png";

        private static readonly Rectangle DealsIconArea = new Rectangle(560, 150, 160, 200);
        private static readonly Rectangle DealsHeaderArea = new Rectangle(60, 0, 200, 90);
        private static readonly Rectangle TabStripArea = new Rectangle(0, 90, 720, 100);
        private static readonly Rectangle TitleArea = new Rectangle(0, 270, 400, 80);
        private static readonly Rectangle ButtonArea = new Rectangle(30, 1140, 300, 110);
        private static readonly Rectangle BackArrowClickArea = new Rectangle(24, 20, 50, 40);

        private const double DealsThreshold = 0.80;
        private const double ShipTabThreshold = 0.85;  // 0.997 unselected, 0.74 selected (the title shows then), <= 0.56 on the base view
        private const double TitleThreshold = 0.90;    // 1.00 on the tab, <= 0.23 on other Deals tabs
        private const double ButtonThreshold = 0.90;   // own button 0.99; the other label <= 0.54. Grey Dispatch All also scores 0.99 (gray match)
        private const double MinButtonSaturation = 70; // mean HSV saturation: green 133, teal 127, greyed 23
        private const int MaxStripPages = 3;
        private const int TabSwipeDurationMs = 2500;

        private static readonly TimeSpan VoyageDuration = TimeSpan.FromHours(8);
        private static readonly TimeSpan Recheck = TimeSpan.FromHours(1);

        private static readonly string TimeFile = Path.Combine(AppContext.BaseDirectory, "data", "voyage_of_light_times.json");
        private static readonly object _fileLock = new();
        private static ConcurrentDictionary<string, DateTime>? _nextRunUtc;

        public DateTime? GetNextRunUtc(string accountKey, AccountSettings account) =>
            NextRuns.TryGetValue(accountKey, out var next) && next > DateTime.UtcNow ? next : null;

        public void ClearSchedule(string accountKey)
        {
            if (NextRuns.TryRemove(accountKey, out _)) Save();
        }

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            try
            {
                int instance = account.InstanceNumber;
                string accountKey = Schedule.AccountKey(AccountDetectionTask.GetCachedAccountId(instance), account);
                var due = GetNextRunUtc(accountKey, account);
                if (due != null)
                    return TaskExecutionDetails.SkippedBecause($"Next voyage check at {due:HH:mm} UTC");
                if (!EventCalendar.MayRun(TaskType, DateTime.UtcNow))
                {
                    SetNextRun(accountKey, Schedule.NextUtcMidnight);
                    return TaskExecutionDetails.SkippedBecause("Not on the event calendar today");
                }

                var locator = new LocatorService(logger, account, TaskType);
                await locator.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

                if (!await OpenDealsAsync(instance, logger, cancellationToken))
                {
                    await locator.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);
                    return TaskExecutionDetails.Failed("Deals hub did not open");
                }

                string result;
                if (!await OpenVoyageTabAsync(instance, logger, cancellationToken))
                {
                    SetNextRun(accountKey, Schedule.NextUtcMidnight);
                    result = "Voyage of Light not running, checking again after the daily reset";
                }
                else
                {
                    result = await ClaimAndDispatchAsync(account, accountKey, instance, logger, cancellationToken);
                }

                await LeaveDealsAsync(instance, logger, cancellationToken);
                await locator.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);
                logger.LogInfo($"[{account.AccountName}] Voyage of Light: {result}");
                return TaskExecutionDetails.Succeeded(result);
            }
            catch (OperationCanceledException)
            {
                return TaskExecutionDetails.Failed("Task was cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error in Voyage of Light: {ex.Message}");
                return TaskExecutionDetails.Failed(ex.Message);
            }
        }

        private async Task<bool> OpenDealsAsync(int instance, LogService logger, CancellationToken cancellationToken)
        {
            var icon = await MatchAsync(DealsIconTemplate, DealsIconArea, DealsThreshold, instance, logger);
            if (!icon.found)
            {
                logger.LogWarning($"Voyage of Light: Deals icon not found ({icon.confidence:F2})");
                return false;
            }
            await ClickCenterInRectAsync(instance, logger, icon.rect);
            return await WaitForAsync(async () => (await MatchAsync(DealsHeaderTemplate, DealsHeaderArea, DealsThreshold, instance, logger)).found,
                TimeSpan.FromSeconds(6), TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        private async Task<bool> IsOnVoyageTabAsync(int instance, LogService logger) =>
            (await MatchAsync(TitleTemplate, TitleArea, TitleThreshold, instance, logger)).found;

        /// <summary>The hub opens on a tab with something to claim; otherwise find the ship tab from the start of the strip.</summary>
        private async Task<bool> OpenVoyageTabAsync(int instance, LogService logger, CancellationToken cancellationToken)
        {
            await Task.Delay(800, cancellationToken);
            if (await IsOnVoyageTabAsync(instance, logger)) return true;

            for (int i = 0; i < 3; i++)
            {
                await SwipeAsync(instance, logger, new Point(150, 150), new Point(620, 150), 400);
                await Task.Delay(500, cancellationToken);
            }

            for (int page = 0; page < MaxStripPages; page++)
            {
                var tab = await MatchAsync(ShipTabTemplate, TabStripArea, ShipTabThreshold, instance, logger);
                if (tab.found)
                {
                    await ClickCenterInRectAsync(instance, logger, tab.rect);
                    return await WaitForAsync(() => IsOnVoyageTabAsync(instance, logger),
                        TimeSpan.FromSeconds(4), TimeSpan.FromMilliseconds(500), cancellationToken);
                }
                await SwipeAsync(instance, logger, new Point(620, 150), new Point(260, 150), TabSwipeDurationMs);
                await Task.Delay(800, cancellationToken);
            }
            return false;
        }

        private async Task<string> ClaimAndDispatchAsync(AccountSettings account, string accountKey, int instance, LogService logger, CancellationToken cancellationToken)
        {
            bool claimed = await TapActiveButtonAsync(ClaimAllTemplate, instance, logger);
            if (claimed)
            {
                logger.LogInfo($"[{account.AccountName}] Voyage of Light: Claim All");
                await Task.Delay(2500, cancellationToken);
            }

            // the button turns into Dispatch All after the claim; a reward popup can delay it
            bool dispatched = false;
            for (int i = 0; i < 3 && !dispatched; i++)
            {
                dispatched = await TapActiveButtonAsync(DispatchAllTemplate, instance, logger);
                if (!dispatched && claimed) await Task.Delay(1500, cancellationToken);
                else break;
            }

            if (dispatched)
            {
                await Task.Delay(2500, cancellationToken);
                SetNextRun(accountKey, DateTime.UtcNow + VoyageDuration);
                return $"{(claimed ? "claimed and " : "")}dispatched, next in {VoyageDuration.TotalHours:F0}h";
            }

            SetNextRun(accountKey, DateTime.UtcNow + Recheck);
            return claimed ? "claimed, nothing to dispatch" : "ships still out";
        }

        /// <summary>Taps the button when it shows in colour; the greyed (disabled) one matches the same shape</summary>
        private async Task<bool> TapActiveButtonAsync(string template, int instance, LogService logger)
        {
            var shot = await TakeScreenshotAsync(instance, logger);
            if (shot == null) return false;
            var button = FindActiveButton(shot, template, instance, logger);
            if (button == null) return false;
            await ClickCenterInRectAsync(instance, logger, button.Value);
            return true;
        }

        /// <summary>Where the Claim All / Dispatch All button is, when it shows in colour (null when absent or greyed out)</summary>
        internal Rectangle? FindActiveButton(byte[] shot, string template, int instance, LogService logger)
        {
            var match = MatchScreenshot(shot, template, ButtonArea, ButtonThreshold, instance, logger);
            if (!match.found) return null;
            double saturation = MeanSaturation(shot, match.rect);
            if (saturation >= MinButtonSaturation) return match.rect;
            logger.LogInfo($"Voyage of Light: {template} greyed out (saturation {saturation:F0})");
            return null;
        }

        /// <summary>The "Voyage of Light" title of the tab is showing</summary>
        internal bool IsVoyageTab(byte[] shot, int instance, LogService logger) =>
            MatchScreenshot(shot, TitleTemplate, TitleArea, TitleThreshold, instance, logger).found;

        /// <summary>The unselected ship tab in the Deals tab strip</summary>
        internal bool HasShipTab(byte[] shot, int instance, LogService logger) =>
            MatchScreenshot(shot, ShipTabTemplate, TabStripArea, ShipTabThreshold, instance, logger).found;

        internal static double MeanSaturation(byte[] screenshot, Rectangle rect)
        {
            using var lease = DecodedScreenshotCache.Acquire(screenshot);
            var saturation = lease.HsvChannels[1];
            var roi = new OpenCvSharp.Rect(rect.X, rect.Y, rect.Width, rect.Height)
                .Intersect(new OpenCvSharp.Rect(0, 0, saturation.Width, saturation.Height));
            if (roi.Width <= 0 || roi.Height <= 0) return 0;
            using var region = new OpenCvSharp.Mat(saturation, roi);
            return OpenCvSharp.Cv2.Mean(region).Val0;
        }

        private async Task LeaveDealsAsync(int instance, LogService logger, CancellationToken cancellationToken)
        {
            for (int i = 0; i < 3 && (await MatchAsync(DealsHeaderTemplate, DealsHeaderArea, DealsThreshold, instance, logger)).found; i++)
            {
                await ClickRandomInRectAsync(instance, logger, BackArrowClickArea);
                await Task.Delay(1000, cancellationToken);
            }
        }

        private static void SetNextRun(string accountKey, DateTime utc)
        {
            NextRuns[accountKey] = utc;
            Save();
        }

        private static ConcurrentDictionary<string, DateTime> NextRuns
        {
            get
            {
                if (_nextRunUtc != null) return _nextRunUtc;
                lock (_fileLock)
                {
                    if (_nextRunUtc != null) return _nextRunUtc;
                    var loaded = new ConcurrentDictionary<string, DateTime>();
                    try
                    {
                        if (File.Exists(TimeFile))
                        {
                            var data = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(TimeFile));
                            if (data != null)
                                foreach (var kvp in data) loaded[kvp.Key] = DateTime.SpecifyKind(kvp.Value, DateTimeKind.Utc);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[VoyageOfLightTask] Error loading voyage times: {ex.Message}");
                    }
                    _nextRunUtc = loaded;
                    return loaded;
                }
            }
        }

        private static void Save()
        {
            lock (_fileLock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(TimeFile)!);
                    File.WriteAllText(TimeFile, JsonSerializer.Serialize(NextRuns.ToDictionary(k => k.Key, k => k.Value)));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[VoyageOfLightTask] Error saving voyage times: {ex.Message}");
                }
            }
        }
    }
}
