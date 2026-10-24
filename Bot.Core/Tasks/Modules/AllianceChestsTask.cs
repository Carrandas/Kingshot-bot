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

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// Alliance > Chests: Claim All on the Loot Chest and Alliance Gift tabs, then opens the big key chest while its
    /// red counter shows. A tab is only visited while its red badge shows, and Claim All only tapped while it is green
    /// (it greys out at the daily loot chest limit). Claiming is free; no step spends anything.
    /// </summary>
    public class AllianceChestsTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.AllianceChests;
        public override string Name => "Alliance Chests";

        private LocatorService? _locatorService;

        private const string AllianceMenuTemplate = "alliance-menu.png";   // Alliance button of the bottom bar
        private const string ChestsTileTemplate = "alliance-chests.png";   // Chests tile of the Alliance screen
        private const string ChestsTitleTemplate = "chests-title.png";     // "Chests" title of the chests screen
        private static readonly Rectangle AllianceMenuArea = new Rectangle(480, 1160, 150, 110);
        private static readonly Rectangle ChestsTileArea = new Rectangle(360, 560, 360, 260);
        private static readonly Rectangle ChestsTitleArea = new Rectangle(60, 0, 240, 80);
        private static readonly Point LootTabPoint = new Point(190, 400);
        private static readonly Point GiftTabPoint = new Point(528, 400);
        private static readonly Point BigChestPoint = new Point(360, 200);
        private static readonly Point BackArrowPoint = new Point(45, 40);
        // "Tap anywhere to exit" on the Rewards popup; the Chests title bar, nothing to hit there without a popup
        private static readonly Rectangle RewardsDismissArea = new Rectangle(300, 25, 120, 30);
        // Red counters: 300-450 red pixels with a badge, 0 without
        private static readonly Rectangle LootBadgeArea = new Rectangle(300, 352, 70, 36);
        private static readonly Rectangle GiftBadgeArea = new Rectangle(640, 352, 60, 36);
        private static readonly Rectangle BigChestBadgeArea = new Rectangle(420, 125, 70, 70);
        // Claim All buttons, green while something can be claimed: 7,000-18,000 green pixels, 0 when greyed out
        private static readonly Rectangle LootClaimAllArea = new Rectangle(220, 1170, 280, 80);
        private static readonly Rectangle GiftClaimAllArea = new Rectangle(525, 1160, 150, 65);
        private const int MinBadgePixels = 100;
        private const int MinGreenPixels = 2000;
        private const double Threshold = 0.90;     // tile and title: 1.00 present, <= 0.46 elsewhere
        private const int MaxBigChestTaps = 3;     // one tap opens all ready chests

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            try
            {
                _locatorService = new LocatorService(logger, account, TaskType);
                int instance = account.InstanceNumber;
                await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

                if (!await OpenChestsAsync(account, logger, instance, cancellationToken))
                {
                    await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);
                    return TaskExecutionDetails.Failed("Alliance chests screen did not open");
                }

                bool loot = await ClaimTabAsync(account, logger, instance, "Loot Chest", LootTabPoint, LootBadgeArea, LootClaimAllArea, cancellationToken);
                bool gift = await ClaimTabAsync(account, logger, instance, "Alliance Gift", GiftTabPoint, GiftBadgeArea, GiftClaimAllArea, cancellationToken);
                int opened = await OpenBigChestAsync(account, logger, instance, cancellationToken);

                // Chests screen > Alliance screen > base
                for (int i = 0; i < 2; i++)
                {
                    await ClickAsync(instance, logger, BackArrowPoint);
                    await Task.Delay(1500, cancellationToken);
                }
                await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

                string result = $"loot chests {(loot ? "claimed" : "none")}, alliance gifts {(gift ? "claimed" : "none")}, big chest {(opened > 0 ? "opened" : "not ready")}";
                logger.LogInfo($"[{account.AccountName}] Alliance Chests done: {result}");
                return TaskExecutionDetails.Succeeded(result);
            }
            catch (OperationCanceledException)
            {
                return TaskExecutionDetails.Failed("Task was cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error in Alliance Chests: {ex.Message}");
                return TaskExecutionDetails.Failed(ex.Message);
            }
        }

        /// <summary>
        /// Bottom bar Alliance > Chests tile.
        /// </summary>
        private async Task<bool> OpenChestsAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            var menu = await MatchAsync(AllianceMenuTemplate, AllianceMenuArea, instance, logger);
            if (!menu.found)
            {
                logger.LogWarning($"[{account.AccountName}] Alliance Chests: Alliance button not found ({menu.confidence:F2})");
                return false;
            }
            await ClickCenterInRectAsync(instance, logger, menu.rect);
            await Task.Delay(2000, cancellationToken);

            var tile = await MatchAsync(ChestsTileTemplate, ChestsTileArea, instance, logger);
            if (!tile.found)
            {
                logger.LogWarning($"[{account.AccountName}] Alliance Chests: Chests tile not found ({tile.confidence:F2})");
                return false;
            }
            await ClickCenterInRectAsync(instance, logger, tile.rect);
            await Task.Delay(2000, cancellationToken);

            return (await MatchAsync(ChestsTitleTemplate, ChestsTitleArea, instance, logger)).found;
        }

        /// <summary>
        /// Opens the tab when its badge shows and taps Claim All while it is green. Returns true when something was claimed.
        /// </summary>
        private async Task<bool> ClaimTabAsync(AccountSettings account, LogService logger, int instance, string tabName,
            Point tabPoint, Rectangle badgeArea, Rectangle claimAllArea, CancellationToken cancellationToken)
        {
            if (await CountPixelsAsync(badgeArea, instance, logger, red: true) < MinBadgePixels)
            {
                logger.LogInfo($"[{account.AccountName}] Alliance Chests: nothing on {tabName}");
                return false;
            }
            await ClickAsync(instance, logger, tabPoint);
            await Task.Delay(1500, cancellationToken);

            bool claimed = false;
            for (int i = 0; i < 2; i++)
            {
                if (await CountPixelsAsync(claimAllArea, instance, logger, red: false) < MinGreenPixels) break;
                await ClickCenterInRectAsync(instance, logger, claimAllArea);
                await Task.Delay(2500, cancellationToken);
                await ClickCenterInRectAsync(instance, logger, RewardsDismissArea);
                await Task.Delay(1500, cancellationToken);
                claimed = true;
            }
            logger.LogInfo($"[{account.AccountName}] Alliance Chests: {tabName} {(claimed ? "claimed" : "Claim All not available (daily limit?)")}");
            return claimed;
        }

        /// <summary>
        /// Taps the big key chest while its red counter shows. Returns the number of taps.
        /// </summary>
        private async Task<int> OpenBigChestAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            int taps = 0;
            while (taps < MaxBigChestTaps && await CountPixelsAsync(BigChestBadgeArea, instance, logger, red: true) >= MinBadgePixels)
            {
                await ClickAsync(instance, logger, BigChestPoint);
                taps++;
                await Task.Delay(2500, cancellationToken);
                await ClickCenterInRectAsync(instance, logger, RewardsDismissArea);
                await Task.Delay(1500, cancellationToken);
            }
            if (taps > 0) logger.LogInfo($"[{account.AccountName}] Alliance Chests: big chest tapped {taps} time(s)");
            return taps;
        }

        /// <summary>
        /// Counts the badge red (or button green) pixels in an area.
        /// </summary>
        private async Task<int> CountPixelsAsync(Rectangle area, int instance, LogService logger, bool red)
        {
            var shot = await TakeScreenshotAsync(instance, logger);
            if (shot == null) return 0;
            using var image = OpenCvSharp.Cv2.ImDecode(shot, OpenCvSharp.ImreadModes.Color);
            var roi = new OpenCvSharp.Rect(area.X, area.Y, area.Width, area.Height)
                .Intersect(new OpenCvSharp.Rect(0, 0, image.Width, image.Height));
            using var region = new OpenCvSharp.Mat(image, roi);
            using var hsv = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.CvtColor(region, hsv, OpenCvSharp.ColorConversionCodes.BGR2HSV);
            using var mask = new OpenCvSharp.Mat();
            if (red)
            {
                using var high = new OpenCvSharp.Mat();
                OpenCvSharp.Cv2.InRange(hsv, new OpenCvSharp.Scalar(0, 150, 150), new OpenCvSharp.Scalar(8, 255, 255), mask);
                OpenCvSharp.Cv2.InRange(hsv, new OpenCvSharp.Scalar(170, 150, 150), new OpenCvSharp.Scalar(180, 255, 255), high);
                OpenCvSharp.Cv2.BitwiseOr(mask, high, mask);
            }
            else
            {
                OpenCvSharp.Cv2.InRange(hsv, new OpenCvSharp.Scalar(40, 120, 120), new OpenCvSharp.Scalar(80, 255, 255), mask);
            }
            return OpenCvSharp.Cv2.CountNonZero(mask);
        }

        private async Task<(bool found, Rectangle rect, double confidence)> MatchAsync(
            string templateName, Rectangle area, int instance, LogService logger)
        {
            var screenshot = await TakeScreenshotAsync(instance, logger);
            if (screenshot == null) return (false, Rectangle.Empty, 0);

            if (_templateMatcher == null) _templateMatcher = new UnifiedTemplateMatchingService(logger);
            var path = Path.Combine(ImageTemplateFolder, templateName);
            var result = _templateMatcher.MatchTemplate(
                screenshot, path, instance,
                threshold: Threshold,
                scales: new[] { 1.0 },
                verboseLogging: false,
                searchArea: area);
            return (result.found, result.matchRect, result.confidence);
        }
    }
}
