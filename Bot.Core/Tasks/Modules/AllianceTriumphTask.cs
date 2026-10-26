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
    /// Side panel > Daily tab > Alliance Triumph "Daily Activity Triumph" row (at the bottom of the list) >
    /// Activity Triumph screen: claims the Personal Activity Triumph Chest (free, once per day at 800/800 points).
    ///
    /// SAFETY: the chest is only tapped while its red dot shows. Nothing on this screen costs gems.
    /// </summary>
    public class AllianceTriumphTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.AllianceTriumph;
        public override string Name => "Alliance Triumph";

        private LocatorService? _locatorService;

        private const string PanelOpenArrowTemplate = "panel-open-arrow.png";
        private const string PanelCloseArrowTemplate = "panel-close-arrow.png";
        private const string TriumphRowIconTemplate = "triumph-row-icon.png";  // Daily Activity Triumph row
        private const string TriumphTitleTemplate = "triumph-title.png";       // "Activity Triumph" screen title
        private const string PersonalChestTemplate = "personal-chest.png";     // unopened chest body (no hero portrait or points counter)
        private const string TriumphBackTemplate = "triumph-back.png";
        private static readonly Rectangle PanelOpenArrowArea = new Rectangle(0, 420, 60, 260);
        private static readonly Rectangle PanelCloseArrowArea = new Rectangle(400, 420, 120, 260);
        private static readonly Rectangle PanelListArea = new Rectangle(0, 290, 120, 540);
        private static readonly Rectangle TriumphTitleArea = new Rectangle(70, 0, 300, 80);
        private static readonly Rectangle PersonalChestArea = new Rectangle(20, 780, 230, 190);
        private static readonly Rectangle ChestRedDotArea = new Rectangle(680, 788, 24, 24);
        private static readonly Rectangle TriumphBackArea = new Rectangle(0, 0, 110, 90);
        private static readonly Point DailyTabPoint = new Point(370, 268);
        private static readonly Point RewardsDismissPoint = new Point(60, 640);  // "Tap anywhere to exit", away from buttons
        private const int PanelRowTitleDx = 180;
        private const double PanelArrowThreshold = 0.88;
        private const double PanelRowThreshold = 0.93;  // other row icons score <= 0.80
        private const double Threshold = 0.90;          // screen elements: 1.00 present, <= 0.70 elsewhere
        // The chest shimmers and differs a little per account: ~0.90-1.00 unopened, <= 0.60 once opened
        private const double ChestThreshold = 0.80;
        private const int MinRedDotPixels = 60;

        // The daily chest resets at 00:00 UTC; once claimed, skip until then
        private static readonly ConcurrentDictionary<string, DateTime> _lastClaimUtcDate = new();
        private bool _rowHidden;

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            try
            {
                string accountId = AccountDetectionTask.GetCachedAccountId(account.InstanceNumber);
                string accountKey = string.IsNullOrEmpty(accountId) ? $"Account_{account.AccountName}_{account.InstanceNumber}" : accountId;
                if (_lastClaimUtcDate.TryGetValue(accountKey, out var claimed) && claimed == DateTime.UtcNow.Date)
                    return TaskExecutionDetails.Succeeded("Already claimed today");

                _locatorService = new LocatorService(logger, account, TaskType);
                int instance = account.InstanceNumber;
                await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

                _rowHidden = false;
                if (!await OpenTriumphAsync(account, logger, instance, cancellationToken))
                {
                    // a hidden row means today's chest is already claimed; a panel that failed to open is retried next cycle
                    if (_rowHidden) _lastClaimUtcDate[accountKey] = DateTime.UtcNow.Date;
                    await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);
                    return TaskExecutionDetails.Succeeded("Row hidden or screen did not open");
                }

                bool claimedNow = await ClaimChestAsync(account, logger, instance, cancellationToken);
                if (claimedNow) _lastClaimUtcDate[accountKey] = DateTime.UtcNow.Date;

                var back = await MatchAsync(TriumphBackTemplate, TriumphBackArea, Threshold, instance, logger);
                if (back.found)
                {
                    await ClickCenterInRectAsync(instance, logger, back.rect);
                    await Task.Delay(1500, cancellationToken);
                }
                await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

                string result = claimedNow ? "personal chest claimed" : "chest not claimable";
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
        /// Side panel > Daily tab, scrolled down to the Alliance Triumph row. The panel hides completed rows,
        /// so a row missing after scrolling means there is nothing to claim.
        /// </summary>
        private async Task<bool> OpenTriumphAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            if (!(await MatchAsync(PanelCloseArrowTemplate, PanelCloseArrowArea, PanelArrowThreshold, instance, logger)).found)
            {
                var arrow = await MatchAsync(PanelOpenArrowTemplate, PanelOpenArrowArea, PanelArrowThreshold, instance, logger);
                if (!arrow.found)
                {
                    logger.LogWarning($"[{account.AccountName}] Alliance Triumph: side panel arrow not found");
                    return false;
                }
                await ClickCenterInRectAsync(instance, logger, arrow.rect);
                await Task.Delay(1500, cancellationToken);
            }
            await ClickAsync(instance, logger, DailyTabPoint);
            await Task.Delay(1200, cancellationToken);

            var row = await FindDailyPanelRowAsync(() => MatchAsync(TriumphRowIconTemplate, PanelListArea, PanelRowThreshold, instance, logger), instance, logger, cancellationToken);
            if (!row.found)
            {
                logger.LogInfo($"[{account.AccountName}] Alliance Triumph: row hidden, nothing to claim ({row.confidence:F2})");
                _rowHidden = true;
                var close = await MatchAsync(PanelCloseArrowTemplate, PanelCloseArrowArea, PanelArrowThreshold, instance, logger);
                if (close.found) await ClickCenterInRectAsync(instance, logger, close.rect);
                await Task.Delay(1000, cancellationToken);
                return false;
            }

            await ClickAsync(instance, logger, new Point(row.rect.X + row.rect.Width / 2 + PanelRowTitleDx, row.rect.Y + row.rect.Height / 2));
            for (int i = 0; i < 4; i++)
            {
                await Task.Delay(1500, cancellationToken);
                if ((await MatchAsync(TriumphTitleTemplate, TriumphTitleArea, Threshold, instance, logger)).found) return true;
            }
            logger.LogWarning($"[{account.AccountName}] Alliance Triumph screen did not open");
            return false;
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
