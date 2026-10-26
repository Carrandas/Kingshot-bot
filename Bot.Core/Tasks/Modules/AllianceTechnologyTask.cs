using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.Services;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Bot.Core.ImageDetection;
using System.Drawing;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Linq;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// Alliance (bottom bar) > Tech > the recommended (green thumb) technology > Contribute.
    /// The teal Contribute costs resources (10,000 bread); 25 attempts, one refills every ~10 minutes.
    ///
    /// SAFETY: the orange "Contribute 💎10" next to it spends gems and is never tapped. The teal button is only held
    /// while its colour is verified teal (grey = no attempts left).
    /// </summary>
    public class AllianceTechnologyTask : BaseTaskWithCommonPatterns, IScheduledTask
    {
        public override TaskType TaskType => TaskType.AllianceTechnology;
        public override string Name => "Alliance Technology";

        private readonly string _templateFolder;
        private LocatorService? _locatorService;

        private const string AllianceMenuTemplate = "alliance-menu.png";   // Alliance button of the bottom bar
        private const string AllianceTechTemplate = "alliance-tech.png";   // Tech tile of the Alliance screen
        private const string GreenThumbTemplate = "green-thumb.png";       // recommended marker on a tech icon
        private const string ContributeTemplate = "contribute.png";        // also matches the orange gem button in grayscale
        private const string CloseXTemplate = "close-x.png";               // X of the contribution dialog
        private static readonly Rectangle AllianceMenuArea = new Rectangle(480, 1160, 150, 110);
        private static readonly Rectangle AllianceTechArea = new Rectangle(360, 880, 150, 130);
        private static readonly Rectangle TechTreeArea = new Rectangle(0, 320, 720, 960);
        private static readonly Rectangle ContributeButtonArea = new Rectangle(370, 975, 290, 105); // teal button only
        private static readonly Rectangle CloseXArea = new Rectangle(590, 150, 110, 100);
        private static readonly Point ThumbToIconOffset = new Point(44, 50);
        private const double Threshold = 0.90;          // 0.96-1.00 present, <= 0.74 elsewhere
        private const int ContributeMinTealPixels = 5000; // ~13,200 teal, 0 grey, ~90 on the orange gem button
        private const int HoldMs = 4000;
        private const int MaxHolds = 10;

        // Default timer settings (used as fallback)
        private static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(10);

        public void ClearSchedule(string accountKey)
        {
            LoadExecutionTimes();
            if (_lastExecutionTimes.TryRemove(accountKey, out _)) SaveExecutionTimes();
        }

        public DateTime? GetNextRunUtc(string accountKey, AccountSettings account)
        {
            // no Load here: it re-reads (and on a read error clears) the gate the running task uses; shows nothing until the task ran once since start
            if (!_lastExecutionTimes.TryGetValue(accountKey, out var last)) return null;
            // WaitHours with a random +-10%; show the earliest
            var next = last.AddHours((account.AllianceTechnologySettings?.WaitHours ?? DefaultInterval.TotalHours) * 0.9);
            return next > DateTime.UtcNow ? next : null;
        }
        private static readonly Random _random = new Random();

        // Static dictionary to track last execution times per account
        private static readonly ConcurrentDictionary<string, DateTime> _lastExecutionTimes = new();
        private static readonly string TimeStorageFile = Path.Combine(
            AppContext.BaseDirectory, "data", "alliance_tech_times.json"
        );

        public AllianceTechnologyTask()
        {
            _templateFolder = Path.Combine(AppContext.BaseDirectory, "templates", "images", "alliancetech");
        }

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(
            AccountSettings account,
            LogService logger,
            CancellationToken cancellationToken,
            bool isReRun = false,
            IUserNotificationService? userNotifications = null)
        {
            try
            {
                string accountId = AccountDetectionTask.GetCachedAccountId(account.InstanceNumber);
                string accountKey = string.IsNullOrEmpty(accountId) ? $"Account_{account.AccountName}_{account.InstanceNumber}" : accountId;

                if (!ShouldExecute(accountKey, account))
                {
                    var nextExecution = GetNextExecutionTime(accountKey, account);
                    logger.LogInfo($"[{account.AccountName}] Too soon to run Alliance Technology. Next execution at: {nextExecution}");
                    return new TaskExecutionDetails(true, nextExecution, "Skipped - Too soon to execute") { Skipped = true };
                }

                _locatorService = new LocatorService(logger, account, TaskType);
                int instance = account.InstanceNumber;
                await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

                if (!await TapAsync(AllianceMenuTemplate, AllianceMenuArea, instance, logger, 2000, cancellationToken)
                    || !await TapAsync(AllianceTechTemplate, AllianceTechArea, instance, logger, 1500, cancellationToken))
                {
                    logger.LogWarning($"[{account.AccountName}] Alliance Technology: Alliance or Tech button not found");
                    await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);
                    return FailDetection("Opening Alliance > Tech", "alliance tech button");
                }

                // The tree scrolls to the recommended tech; its icon sits below-right of the thumb
                var thumb = await MatchAsync(GreenThumbTemplate, TechTreeArea, instance, logger);
                if (!thumb.found)
                {
                    logger.LogInfo($"[{account.AccountName}] Alliance Technology: no recommended tech ({thumb.confidence:F2})");
                    await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);
                    return new TaskExecutionDetails(true, message: "No recommended tech");
                }
                await ClickAsync(instance, logger, new Point(thumb.rect.X + thumb.rect.Width / 2 + ThumbToIconOffset.X, thumb.rect.Y + thumb.rect.Height / 2 + ThumbToIconOffset.Y));
                await Task.Delay(1500, cancellationToken);

                int holds = 0;
                while (holds < MaxHolds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var button = await MatchAsync(ContributeTemplate, ContributeButtonArea, instance, logger);
                    if (!button.found || !await IsTealAsync(button.rect, instance, logger)) break;

                    // Holding the button repeats the contribution until it is released or the attempts run out
                    var center = new Point(button.rect.X + button.rect.Width / 2, button.rect.Y + button.rect.Height / 2);
                    await SwipeAsync(instance, logger, center, center, HoldMs);
                    holds++;
                    await Task.Delay(1000, cancellationToken);
                }
                logger.LogInfo($"[{account.AccountName}] Alliance Technology: held Contribute {holds} time(s)");

                var close = await MatchAsync(CloseXTemplate, CloseXArea, instance, logger);
                if (close.found)
                {
                    await ClickCenterInRectAsync(instance, logger, close.rect);
                    await Task.Delay(1000, cancellationToken);
                }
                await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

                if (holds > 0) UpdateExecutionTime(accountKey);
                logger.LogInfo($"[{account.AccountName}] Alliance Technology done: {(holds > 0 ? "contributed" : "no attempts left")}");
                return new TaskExecutionDetails(true, message: holds > 0 ? "Contributed" : "No attempts left");
            }
            catch (OperationCanceledException)
            {
                logger.LogInfo($"[{account.AccountName}] Alliance Technology task was cancelled");
                return TaskExecutionDetails.Failed("Task was cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error during Alliance Technology task: {ex.Message}");
                return TaskExecutionDetails.FailedWith(
                    FailureCategory.Unknown,
                    "Unexpected error",
                    ex.Message,
                    recoveryNeeded: true);
            }
        }

        private async Task<bool> TapAsync(string templateName, Rectangle area, int instance, LogService logger, int delayMs, CancellationToken cancellationToken)
        {
            var match = await MatchAsync(templateName, area, instance, logger);
            if (!match.found) return false;
            await ClickCenterInRectAsync(instance, logger, match.rect);
            await Task.Delay(delayMs, cancellationToken);
            return true;
        }

        private async Task<bool> IsTealAsync(Rectangle buttonRect, int instance, LogService logger)
        {
            var shot = await TakeScreenshotAsync(instance, logger);
            if (shot == null) return false;
            using var image = OpenCvSharp.Cv2.ImDecode(shot, OpenCvSharp.ImreadModes.Color);
            var roi = new OpenCvSharp.Rect(ContributeButtonArea.X, ContributeButtonArea.Y, ContributeButtonArea.Width, ContributeButtonArea.Height)
                .Intersect(new OpenCvSharp.Rect(0, 0, image.Width, image.Height));
            using var region = new OpenCvSharp.Mat(image, roi);
            using var hsv = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.CvtColor(region, hsv, OpenCvSharp.ColorConversionCodes.BGR2HSV);
            using var mask = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.InRange(hsv, new OpenCvSharp.Scalar(80, 120, 150), new OpenCvSharp.Scalar(100, 255, 255), mask);
            int teal = OpenCvSharp.Cv2.CountNonZero(mask);
            logger.LogInfo($"Alliance Technology: Contribute teal pixels {teal}");
            return teal >= ContributeMinTealPixels;
        }

        private async Task<(bool found, Rectangle rect, double confidence)> MatchAsync(
            string templateName, Rectangle area, int instance, LogService logger)
        {
            var screenshot = await TakeScreenshotAsync(instance, logger);
            if (screenshot == null) return (false, Rectangle.Empty, 0);

            if (_templateMatcher == null) _templateMatcher = new UnifiedTemplateMatchingService(logger);
            var result = _templateMatcher.MatchTemplate(
                screenshot, Path.Combine(_templateFolder, templateName), instance,
                threshold: Threshold,
                scales: new[] { 1.0 },
                verboseLogging: false,
                searchArea: area);
            return (result.found, result.matchRect, result.confidence);
        }

        protected override Task OnInitializeAsync(LogService logger, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        #region Timer Management


        private bool ShouldExecute(string accountKey, AccountSettings account)
        {
            LoadExecutionTimes();
            if (!_lastExecutionTimes.TryGetValue(accountKey, out DateTime lastExecution))
            {
                return true; // First time execution
            }

            var nextExecution = GetNextExecutionTime(accountKey, account);
            return DateTime.UtcNow >= nextExecution;
        }

        private DateTime GetNextExecutionTime(string accountKey, AccountSettings account)
        {
            if (!_lastExecutionTimes.TryGetValue(accountKey, out DateTime lastExecution))
            {
                return DateTime.UtcNow; // First time execution
            }

            // Use the configured wait hours from the account settings
            var waitHours = account.AllianceTechnologySettings?.WaitHours ?? (int)DefaultInterval.TotalHours;

            // Add a small random variance (±10%) to prevent predictable timing
            var variance = _random.NextDouble() * 0.2 - 0.1; // -10% to +10%
            var actualWaitHours = waitHours * (1 + variance);

            return lastExecution.AddHours(actualWaitHours);
        }

        private void UpdateExecutionTime(string accountKey)
        {
            _lastExecutionTimes[accountKey] = DateTime.UtcNow;
            SaveExecutionTimes();
        }

        private void LoadExecutionTimes()
        {
            try
            {
                if (File.Exists(TimeStorageFile))
                {
                    var json = File.ReadAllText(TimeStorageFile);
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    var times = JsonSerializer.Deserialize<ConcurrentDictionary<string, DateTime>>(json, options);
                    if (times != null)
                    {
                        // Validate and clean up old entries
                        var now = DateTime.UtcNow;
                        foreach (var kvp in times)
                        {
                            // Only keep entries from the last 48 hours
                            if (now.Subtract(kvp.Value).TotalHours <= 48)
                            {
                                _lastExecutionTimes[kvp.Key] = kvp.Value;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AllianceTechnologyTask] Error loading execution times: {ex.Message}");
                _lastExecutionTimes.Clear();
            }
        }

        private void SaveExecutionTimes()
        {
            try
            {
                var directory = Path.GetDirectoryName(TimeStorageFile);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Validate times before saving
                var now = DateTime.UtcNow;
                var validTimes = _lastExecutionTimes
                    .Where(kvp => now.Subtract(kvp.Value).TotalHours <= 48)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(validTimes, options);
                File.WriteAllText(TimeStorageFile, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AllianceTechnologyTask] Error saving execution times: {ex.Message}");
            }
        }

        #endregion
    }
}