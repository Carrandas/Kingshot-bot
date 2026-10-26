using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.ImageDetection;
using Bot.Core.Services;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// Bottom bar Alliance > Triumph button > Activity Triumph screen: claims last week's alliance reward (the big chest at the top, while it shows
    /// "Claimable Rewards") and the Personal Activity Triumph Chest (free, once per day at 800/800 points).
    ///
    /// SAFETY: the chest is only tapped while its red dot shows. Nothing on this screen costs gems.
    /// </summary>
    public class AllianceTriumphTask : BaseTaskWithCommonPatterns, IScheduledTask
    {
        public override TaskType TaskType => TaskType.AllianceTriumph;
        public override string Name => "Alliance Triumph";

        private LocatorService? _locatorService;

        private const string AllianceMenuTemplate = "alliance-menu.png";       // Alliance button of the bottom bar
        private const string TriumphButtonTemplate = "triumph-button.png";     // Triumph button at the bottom of the Alliance screen
        private const string TriumphTitleTemplate = "triumph-title.png";       // "Activity Triumph" screen title
        private const string PersonalChestTemplate = "personal-chest.png";     // unopened chest body (no hero portrait or points counter)
        private const string WeeklyClaimableTemplate = "weekly-claimable.png"; // "Claimable Rewards:" label; "Current Rewards:" once claimed
        private static readonly Rectangle AllianceMenuArea = new Rectangle(480, 1160, 150, 110);
        private static readonly Rectangle TriumphButtonArea = new Rectangle(240, 1140, 240, 140);
        private static readonly Rectangle TriumphTitleArea = new Rectangle(70, 0, 300, 80);
        private static readonly Rectangle PersonalChestArea = new Rectangle(20, 780, 230, 190);
        private static readonly Rectangle ChestRedDotArea = new Rectangle(680, 788, 24, 24);
        private static readonly Rectangle WeeklyClaimableArea = new Rectangle(80, 700, 560, 120);
        private static readonly Point WeeklyChestPoint = new Point(360, 460);
        private static readonly Point BackArrowPoint = new Point(45, 40);
        private static readonly Point RewardsDismissPoint = new Point(60, 640);  // "Tap anywhere to exit", away from buttons
        private const double Threshold = 0.90;          // screen elements and buttons: 1.00 present, <= 0.70 elsewhere
        // The chest shimmers and differs a little per account: ~0.90-1.00 unopened, <= 0.60 once opened
        private const double ChestThreshold = 0.80;
        private const int MinRedDotPixels = 60;
        private const double WeeklyThreshold = 0.85;    // 1.00 claimable, <= 0.67 elsewhere ("Current Rewards:" after the claim)

        // Both chests reset at 00:00 UTC (the weekly one on the weekly tally day); once the daily chest is claimed, skip until then
        private static readonly ConcurrentDictionary<string, DateTime> _lastClaimUtcDate = new();

        public void ClearSchedule(string accountKey)
        {
            _lastClaimUtcDate.TryRemove(accountKey, out _);
        }

        public DateTime? GetNextRunUtc(string accountKey, AccountSettings account) =>
            _lastClaimUtcDate.TryGetValue(accountKey, out var claimed) && claimed == DateTime.UtcNow.Date ? Schedule.NextUtcMidnight : null;

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            try
            {
                string accountId = AccountDetectionTask.GetCachedAccountId(account.InstanceNumber);
                string accountKey = string.IsNullOrEmpty(accountId) ? $"Account_{account.AccountName}_{account.InstanceNumber}" : accountId;
                if (_lastClaimUtcDate.TryGetValue(accountKey, out var claimed) && claimed == DateTime.UtcNow.Date)
                    return TaskExecutionDetails.SkippedBecause("Already claimed today");

                _locatorService = new LocatorService(logger, account, TaskType);
                int instance = account.InstanceNumber;
                await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

                if (!await OpenTriumphAsync(account, logger, instance, cancellationToken))
                {
                    await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);
                    return TaskExecutionDetails.Failed("Activity Triumph screen did not open");
                }

                bool weeklyClaimed = await ClaimWeeklyAsync(account, logger, instance, cancellationToken);
                bool claimedNow = await ClaimChestAsync(account, logger, instance, cancellationToken);
                if (claimedNow) _lastClaimUtcDate[accountKey] = DateTime.UtcNow.Date;

                // Activity Triumph screen > Alliance screen > base
                for (int i = 0; i < 2; i++)
                {
                    await ClickAsync(instance, logger, BackArrowPoint);
                    await Task.Delay(1500, cancellationToken);
                }
                await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

                string result = (weeklyClaimed ? "weekly reward claimed, " : "") + (claimedNow ? "personal chest claimed" : "personal chest not claimable");
                logger.LogInfo($"[{account.AccountName}] Alliance Triumph done: {result}");
                return TaskExecutionDetails.Succeeded(result);
            }
            catch (OperationCanceledException)
            {
                return TaskExecutionDetails.Failed("Task was cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error in Alliance Triumph: {ex.Message}");
                return TaskExecutionDetails.Failed(ex.Message);
            }
        }

        /// <summary>
        /// Bottom bar Alliance > Triumph button.
        /// </summary>
        private async Task<bool> OpenTriumphAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            var menu = await MatchAsync(AllianceMenuTemplate, AllianceMenuArea, Threshold, instance, logger);
            if (!menu.found)
            {
                logger.LogWarning($"[{account.AccountName}] Alliance Triumph: Alliance button not found ({menu.confidence:F2})");
                return false;
            }
            await ClickCenterInRectAsync(instance, logger, menu.rect);
            await Task.Delay(2000, cancellationToken);

            var button = await MatchAsync(TriumphButtonTemplate, TriumphButtonArea, Threshold, instance, logger);
            if (!button.found)
            {
                logger.LogWarning($"[{account.AccountName}] Alliance Triumph: Triumph button not found ({button.confidence:F2})");
                return false;
            }
            await ClickCenterInRectAsync(instance, logger, button.rect);
            for (int i = 0; i < 4; i++)
            {
                await Task.Delay(1500, cancellationToken);
                if ((await MatchAsync(TriumphTitleTemplate, TriumphTitleArea, Threshold, instance, logger)).found) return true;
            }
            logger.LogWarning($"[{account.AccountName}] Alliance Triumph screen did not open");
            return false;
        }

        /// <summary>
        /// Taps the big chest while the "Claimable Rewards" label shows (last week's alliance reward), then
        /// dismisses the Weekly Rewards popup.
        /// </summary>
        private async Task<bool> ClaimWeeklyAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            var label = await MatchAsync(WeeklyClaimableTemplate, WeeklyClaimableArea, WeeklyThreshold, instance, logger);
            if (!label.found) return false;

            await ClickAsync(instance, logger, WeeklyChestPoint);
            await Task.Delay(2500, cancellationToken);
            await ClickAsync(instance, logger, RewardsDismissPoint);
            await Task.Delay(1500, cancellationToken);
            if (!(await MatchAsync(TriumphTitleTemplate, TriumphTitleArea, Threshold, instance, logger)).found)
            {
                await ClickAsync(instance, logger, RewardsDismissPoint);
                await Task.Delay(1500, cancellationToken);
            }
            logger.LogInfo($"[{account.AccountName}] Alliance Triumph: weekly reward claimed ({label.confidence:F2})");
            return true;
        }

        /// <summary>
        /// Taps the Personal Activity Triumph Chest while its red dot shows, then dismisses the Rewards popup.
        /// </summary>
        private async Task<bool> ClaimChestAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            var chest = await MatchAsync(PersonalChestTemplate, PersonalChestArea, ChestThreshold, instance, logger);
            if (!chest.found || !await HasRedDotAsync(instance, logger))
            {
                logger.LogInfo($"[{account.AccountName}] Alliance Triumph: personal chest not claimable ({chest.confidence:F2})");
                return false;
            }

            await ClickCenterInRectAsync(instance, logger, chest.rect);
            await Task.Delay(2500, cancellationToken);
            await ClickAsync(instance, logger, RewardsDismissPoint);
            await Task.Delay(1500, cancellationToken);
            // A popup still covering the screen hides the title; one more dismiss tap
            if (!(await MatchAsync(TriumphTitleTemplate, TriumphTitleArea, Threshold, instance, logger)).found)
            {
                await ClickAsync(instance, logger, RewardsDismissPoint);
                await Task.Delay(1500, cancellationToken);
            }
            logger.LogInfo($"[{account.AccountName}] Alliance Triumph: personal chest claimed");
            return true;
        }

        private async Task<bool> HasRedDotAsync(int instance, LogService logger)
        {
            var shot = await TakeScreenshotAsync(instance, logger);
            if (shot == null) return false;
            using var image = OpenCvSharp.Cv2.ImDecode(shot, OpenCvSharp.ImreadModes.Color);
            var roi = new OpenCvSharp.Rect(ChestRedDotArea.X, ChestRedDotArea.Y, ChestRedDotArea.Width, ChestRedDotArea.Height)
                .Intersect(new OpenCvSharp.Rect(0, 0, image.Width, image.Height));
            using var region = new OpenCvSharp.Mat(image, roi);
            using var hsv = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.CvtColor(region, hsv, OpenCvSharp.ColorConversionCodes.BGR2HSV);
            using var mask = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.InRange(hsv, new OpenCvSharp.Scalar(0, 180, 200), new OpenCvSharp.Scalar(6, 255, 255), mask);
            int red = OpenCvSharp.Cv2.CountNonZero(mask);
            logger.LogInfo($"Alliance Triumph: chest red dot pixels {red}");
            return red >= MinRedDotPixels;
        }

        private async Task<(bool found, Rectangle rect, double confidence)> MatchAsync(
            string templateName, Rectangle area, double threshold, int instance, LogService logger)
        {
            var screenshot = await TakeScreenshotAsync(instance, logger);
            if (screenshot == null) return (false, Rectangle.Empty, 0);

            if (_templateMatcher == null) _templateMatcher = new UnifiedTemplateMatchingService(logger);
            var path = Path.Combine(ImageTemplateFolder, templateName);
            var result = _templateMatcher.MatchTemplate(
                screenshot, path, instance,
                threshold: threshold,
                scales: new[] { 1.0 },
                verboseLogging: false,
                searchArea: area);
            return (result.found, result.matchRect, result.confidence);
        }
    }
}
