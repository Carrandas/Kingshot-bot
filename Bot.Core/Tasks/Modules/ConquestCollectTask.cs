using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.ImageDetection;
using Bot.Core.Services;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Linq;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// Conquest (bottom bar) > green Claim under the chest > Idle Income popup > Claim > dismiss Rewards > back.
    /// Idle income piles up for at most 9 hours, so the task runs every WaitHours (default 4).
    ///
    /// SAFETY: only the two green Claim buttons are tapped (colour checked). The big "Conquer" button (starts a battle)
    /// sits under the "Tap anywhere to exit" banner, so the Rewards screen is dismissed on empty sky instead.
    /// </summary>
    public class ConquestCollectTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.ConquestCollect;
        public override string Name => "Conquest Collection";

        // Static dictionary to track last collection times per account
        private static readonly ConcurrentDictionary<string, DateTime> _lastCollectionTimes = new();
        private static readonly string TimeStorageFile = Path.Combine(
            AppContext.BaseDirectory, "data", "conquest_collection_times.json"
        );

        private const string ConquestMenuTemplate = "conquest-menu.png";       // Conquest button of the bottom bar
        private const string ConquestTitleTemplate = "conquest-title.png";     // back arrow + "Conquest"
        private const string ChestClaimTemplate = "chest-claim.png";           // Claim under the chest (grey once claimed)
        private const string IdleIncomeTitleTemplate = "idle-income-title.png";
        private const string IdleClaimTemplate = "idle-claim.png";             // green Claim of the Idle Income popup
        private const string RewardsBannerTemplate = "rewards-banner.png";
        private static readonly Rectangle ConquestMenuArea = new Rectangle(0, 1150, 200, 130);
        private static readonly Rectangle ConquestTitleArea = new Rectangle(0, 0, 260, 90);
        private static readonly Rectangle ChestClaimArea = new Rectangle(540, 870, 160, 100);
        private static readonly Rectangle IdleIncomeTitleArea = new Rectangle(220, 250, 280, 90);
        private static readonly Rectangle IdleClaimArea = new Rectangle(250, 870, 220, 110);
        private static readonly Rectangle RewardsBannerArea = new Rectangle(230, 270, 260, 100);
        private static readonly Point RewardsDismissPoint = new Point(80, 330);  // sky; away from Conquer and Claim
        private static readonly Point BackArrowPoint = new Point(48, 38);
        private const double Threshold = 0.90;            // 0.99-1.00 present, <= 0.57 elsewhere
        // The grey (claimed) chest button still scores 0.96, so the green colour decides: ~2,900 / ~5,900 px vs 0
        private const int ClaimMinGreenPixels = 1000;

        // Default collection interval settings (used as fallback)
        private static readonly TimeSpan DefaultCollectionInterval = TimeSpan.FromHours(4);
        private static readonly Random _random = new Random();

        private LocatorService? _locatorService;

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
                if (string.IsNullOrEmpty(accountId))
                    accountId = $"Account_{account.AccountName}_{account.InstanceNumber}";

                if (!ShouldCollect(accountId, account))
                {
                    var nextCollection = GetNextCollectionTime(accountId, account);
                    logger.LogInfo($"[{account.AccountName}] Too soon to collect conquest rewards. Next collection at: {nextCollection}");
                    return new TaskExecutionDetails(true, nextCollection, "Skipped - Too soon to collect");
                }

                int instance = account.InstanceNumber;
                _locatorService = new LocatorService(logger, account, TaskType);
                await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

                if (!await OpenConquestAsync(account, logger, instance, cancellationToken))
                {
                    await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);
                    return FailDetection("Opening Conquest", "conquest screen");
                }

                bool claimed = await ClaimIdleIncomeAsync(account, logger, instance, cancellationToken);

                if ((await MatchAsync(ConquestTitleTemplate, ConquestTitleArea, instance, logger)).found)
                {
                    await ClickAsync(instance, logger, BackArrowPoint);
                    await Task.Delay(1500, cancellationToken);
                }
                await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

                // A grey chest button means it was just claimed elsewhere: wait the full interval either way
                UpdateCollectionTime(accountId);
                string result = claimed ? "idle income claimed" : "nothing to claim";
                logger.LogInfo($"[{account.AccountName}] Conquest collection done: {result}");
                return TaskExecutionDetails.Succeeded(result);
            }
            catch (OperationCanceledException)
            {
                return TaskExecutionDetails.Failed("Task was cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error during conquest collection: {ex.Message}");
                return TaskExecutionDetails.Failed($"Error during conquest collection: {ex.Message}");
            }
        }

        private async Task<bool> OpenConquestAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            var menu = await MatchAsync(ConquestMenuTemplate, ConquestMenuArea, instance, logger);
            if (!menu.found)
            {
                logger.LogWarning($"[{account.AccountName}] Conquest button not found ({menu.confidence:F2})");
                return false;
            }
            await ClickCenterInRectAsync(instance, logger, menu.rect);
            for (int i = 0; i < 4; i++)
            {
                await Task.Delay(1500, cancellationToken);
                if ((await MatchAsync(ConquestTitleTemplate, ConquestTitleArea, instance, logger)).found) return true;
            }
            logger.LogWarning($"[{account.AccountName}] Conquest screen did not open");
            return false;
        }

        /// <summary>
        /// Green chest Claim > Idle Income popup > green Claim > dismiss the Rewards screen.
        /// </summary>
        private async Task<bool> ClaimIdleIncomeAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            var chest = await MatchAsync(ChestClaimTemplate, ChestClaimArea, instance, logger);
            if (!chest.found || !await IsGreenAsync(chest.rect, instance, logger))
            {
                logger.LogInfo($"[{account.AccountName}] Conquest: chest Claim not active ({chest.confidence:F2})");
                return false;
            }
            await ClickCenterInRectAsync(instance, logger, chest.rect);
            await Task.Delay(1500, cancellationToken);

            if (!(await MatchAsync(IdleIncomeTitleTemplate, IdleIncomeTitleArea, instance, logger)).found)
            {
                logger.LogWarning($"[{account.AccountName}] Conquest: Idle Income popup did not open");
                return false;
            }
            var claim = await MatchAsync(IdleClaimTemplate, IdleClaimArea, instance, logger);
            if (!claim.found || !await IsGreenAsync(claim.rect, instance, logger))
            {
                logger.LogWarning($"[{account.AccountName}] Conquest: Idle Income Claim not found ({claim.confidence:F2})");
                await ClickAsync(instance, logger, RewardsDismissPoint);  // outside the popup closes it
                await Task.Delay(1000, cancellationToken);
                return false;
            }
            await ClickCenterInRectAsync(instance, logger, claim.rect);
            await Task.Delay(2500, cancellationToken);

            // "Tap anywhere to exit"; a second tap only if the Rewards screen is still up
            for (int i = 0; i < 2; i++)
            {
                if (!(await MatchAsync(RewardsBannerTemplate, RewardsBannerArea, instance, logger)).found) break;
                await ClickAsync(instance, logger, RewardsDismissPoint);
                await Task.Delay(1500, cancellationToken);
            }
            logger.LogInfo($"[{account.AccountName}] Conquest: idle income claimed");
            return true;
        }

        private async Task<bool> IsGreenAsync(Rectangle buttonRect, int instance, LogService logger)
        {
            var shot = await TakeScreenshotAsync(instance, logger);
            if (shot == null) return false;
            using var image = OpenCvSharp.Cv2.ImDecode(shot, OpenCvSharp.ImreadModes.Color);
            var roi = new OpenCvSharp.Rect(buttonRect.X, buttonRect.Y, buttonRect.Width, buttonRect.Height)
                .Intersect(new OpenCvSharp.Rect(0, 0, image.Width, image.Height));
            using var region = new OpenCvSharp.Mat(image, roi);
            using var hsv = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.CvtColor(region, hsv, OpenCvSharp.ColorConversionCodes.BGR2HSV);
            using var mask = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.InRange(hsv, new OpenCvSharp.Scalar(40, 120, 120), new OpenCvSharp.Scalar(75, 255, 255), mask);
            int green = OpenCvSharp.Cv2.CountNonZero(mask);
            logger.LogInfo($"Conquest: Claim green pixels {green}");
            return green >= ClaimMinGreenPixels;
        }

        private async Task<(bool found, Rectangle rect, double confidence)> MatchAsync(
            string templateName, Rectangle area, int instance, LogService logger)
        {
            var screenshot = await TakeScreenshotAsync(instance, logger);
            if (screenshot == null) return (false, Rectangle.Empty, 0);

            if (_templateMatcher == null) _templateMatcher = new UnifiedTemplateMatchingService(logger);
            var result = _templateMatcher.MatchTemplate(
                screenshot, Path.Combine(ImageTemplateFolder, templateName), instance,
                threshold: Threshold,
                scales: new[] { 1.0 },
                verboseLogging: false,
                searchArea: area);
            return (result.found, result.matchRect, result.confidence);
        }

        private bool ShouldCollect(string accountId, AccountSettings account)
        {
            LoadCollectionTimes();
            if (!_lastCollectionTimes.TryGetValue(accountId, out DateTime lastCollection))
            {
                return true; // First time collection
            }

            var nextCollection = GetNextCollectionTime(accountId, account);
            return DateTime.UtcNow >= nextCollection;
        }

        private DateTime GetNextCollectionTime(string accountId, AccountSettings account)
        {
            if (!_lastCollectionTimes.TryGetValue(accountId, out DateTime lastCollection))
            {
                return DateTime.UtcNow; // First time collection
            }

            // Use the configured wait hours from the account settings
            var waitHours = account.ConquestCollectSettings?.WaitHours ?? (int)DefaultCollectionInterval.TotalHours;

            // Add a small random variance (±10%) to prevent predictable timing
            var variance = _random.NextDouble() * 0.2 - 0.1; // -10% to +10%
            var actualWaitHours = waitHours * (1 + variance);

            return lastCollection.AddHours(actualWaitHours);
        }

        private void UpdateCollectionTime(string accountId)
        {
            _lastCollectionTimes[accountId] = DateTime.UtcNow;
            SaveCollectionTimes();
        }

        private void LoadCollectionTimes()
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
                            // Only keep entries from the last 24 hours
                            if (now.Subtract(kvp.Value).TotalHours <= 24)
                            {
                                _lastCollectionTimes[kvp.Key] = kvp.Value;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ConquestCollectTask] Error loading collection times: {ex.Message}");
                _lastCollectionTimes.Clear();
            }
        }

        private void SaveCollectionTimes()
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
                var validTimes = _lastCollectionTimes
                    .Where(kvp => now.Subtract(kvp.Value).TotalHours <= 24)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(validTimes, options);
                File.WriteAllText(TimeStorageFile, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ConquestCollectTask] Error saving collection times: {ex.Message}");
            }
        }

        protected override Task OnInitializeAsync(LogService logger, CancellationToken cancellationToken)
        {
            LoadCollectionTimes();
            return Task.CompletedTask;
        }
    }
}
