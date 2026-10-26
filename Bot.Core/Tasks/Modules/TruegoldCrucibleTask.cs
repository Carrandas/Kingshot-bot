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
    /// Side panel > Daily tab > Truegold Crucible: uses the daily Refines (7 per day, they cost bread/wood/stone/iron)
    /// and the daily -50% Super Refine x1 (10 of the refined truegold material instead of 20).
    ///
    /// SAFETY: Refine is only tapped while the button is teal (grey = none left today). Super Refine x1 is only tapped
    /// while the "-50%" Daily Deals badge is shown, once per run; "Super Refine x N" is never tapped.
    /// No step spends gems.
    /// </summary>
    public class TruegoldCrucibleTask : BaseTaskWithCommonPatterns, IScheduledTask
    {
        public override TaskType TaskType => TaskType.TruegoldCrucible;
        public override string Name => "Truegold Crucible";

        private LocatorService? _locatorService;

        private const string PanelOpenArrowTemplate = "panel-open-arrow.png";
        private const string PanelCloseArrowTemplate = "panel-close-arrow.png";
        private const string CrucibleRowIconTemplate = "crucible-row-icon.png";   // Truegold Crucible row ("Refinements: 7/7")
        private const string DiscountRowIconTemplate = "discount-row-icon.png";   // Super Refinement Discount row
        private const string RemainingTodayTemplate = "remaining-today.png";      // "Remaining today:" above the Refine button
        private const string RefineButtonTemplate = "refine-button.png";
        private const string DiscountBadgeTemplate = "discount-badge.png";        // red "-50%" badge; "Best Deals" once used
        private const string SuperRefineX1Template = "super-refine-x1.png";
        private const string CrucibleBackTemplate = "crucible-back.png";
        private static readonly Rectangle PanelOpenArrowArea = new Rectangle(0, 420, 60, 260);
        private static readonly Rectangle PanelCloseArrowArea = new Rectangle(400, 420, 120, 260);
        private static readonly Rectangle PanelListArea = new Rectangle(0, 300, 120, 520);
        private static readonly Rectangle RemainingTodayArea = new Rectangle(200, 680, 260, 70);
        private static readonly Rectangle RefineButtonArea = new Rectangle(210, 730, 300, 100);
        private static readonly Rectangle DiscountBadgeArea = new Rectangle(30, 820, 120, 110);
        private static readonly Rectangle SuperRefineX1Area = new Rectangle(370, 1110, 310, 110);
        private static readonly Rectangle CrucibleBackArea = new Rectangle(0, 0, 110, 90);
        private static readonly Point DailyTabPoint = new Point(370, 268);
        private const int PanelRowTitleDx = 180;
        private const int MaxRefines = 7;
        private const double PanelArrowThreshold = 0.88;
        private const double PanelRowThreshold = 0.93;  // other row icons score <= 0.85
        private const double Threshold = 0.90;          // screen elements: 0.99-1.00 present, <= 0.73 elsewhere
        // The grey (used up) Refine button still scores 0.92, so the teal colour decides: ~11,700 teal pixels vs 0
        private const int RefineMinTealPixels = 5000;

        // The refines and the -50% Super Refine reset at 00:00 UTC; once the panel hides both rows, skip until then
        private static readonly ConcurrentDictionary<string, DateTime> _doneUtcDate = new();

        public void ClearSchedule(string accountKey)
        {
            _doneUtcDate.TryRemove(accountKey, out _);
        }

        public DateTime? GetNextRunUtc(string accountKey, AccountSettings account) =>
            _doneUtcDate.TryGetValue(accountKey, out var done) && done == DateTime.UtcNow.Date ? Schedule.NextUtcMidnight : null;
        private bool _rowsHidden;

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            try
            {
                string accountId = AccountDetectionTask.GetCachedAccountId(account.InstanceNumber);
                string accountKey = string.IsNullOrEmpty(accountId) ? $"Account_{account.AccountName}_{account.InstanceNumber}" : accountId;
                if (_doneUtcDate.TryGetValue(accountKey, out var done) && done == DateTime.UtcNow.Date)
                    return TaskExecutionDetails.SkippedBecause("Already done today");

                _locatorService = new LocatorService(logger, account, TaskType);
                int instance = account.InstanceNumber;
                await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

                _rowsHidden = false;
                if (!await OpenCrucibleAsync(account, logger, instance, cancellationToken))
                {
                    // hidden rows mean today's refines and discount are used; a panel that failed to open is retried next cycle
                    if (_rowsHidden) _doneUtcDate[accountKey] = DateTime.UtcNow.Date;
                    await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);
                    return TaskExecutionDetails.Succeeded("Crucible rows hidden or screen did not open");
                }

                int refines = await RefineAsync(account, logger, instance, cancellationToken);
                bool superRefined = await SuperRefineDiscountAsync(account, logger, instance, cancellationToken);

                var back = await MatchAsync(CrucibleBackTemplate, CrucibleBackArea, Threshold, instance, logger);
                if (back.found)
                {
                    await ClickCenterInRectAsync(instance, logger, back.rect);
                    await Task.Delay(1500, cancellationToken);
                }
                await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

                string result = $"{refines} refine(s), discount super refine {(superRefined ? "used" : "not available")}";
                logger.LogInfo($"[{account.AccountName}] Truegold Crucible done: {result}");
                return TaskExecutionDetails.Succeeded(result);
            }
            catch (OperationCanceledException)
            {
                return TaskExecutionDetails.Failed("Task was cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error in Truegold Crucible: {ex.Message}");
                return TaskExecutionDetails.Failed(ex.Message);
            }
        }

        /// <summary>
        /// Side panel > Daily tab > Truegold Crucible row (or the Super Refinement Discount row). Both open the Crucible.
        /// </summary>
        private async Task<bool> OpenCrucibleAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            if (!(await MatchAsync(PanelCloseArrowTemplate, PanelCloseArrowArea, PanelArrowThreshold, instance, logger)).found)
            {
                var arrow = await MatchAsync(PanelOpenArrowTemplate, PanelOpenArrowArea, PanelArrowThreshold, instance, logger);
                if (!arrow.found)
                {
                    logger.LogWarning($"[{account.AccountName}] Truegold Crucible: side panel arrow not found");
                    return false;
                }
                await ClickCenterInRectAsync(instance, logger, arrow.rect);
                await Task.Delay(1500, cancellationToken);
            }
            await ClickAsync(instance, logger, DailyTabPoint);
            await Task.Delay(1200, cancellationToken);

            var row = await FindDailyPanelRowAsync(async () =>
            {
                var crucible = await MatchAsync(CrucibleRowIconTemplate, PanelListArea, PanelRowThreshold, instance, logger);
                return crucible.found ? crucible : await MatchAsync(DiscountRowIconTemplate, PanelListArea, PanelRowThreshold, instance, logger);
            }, instance, logger, cancellationToken);
            if (!row.found)
            {
                logger.LogInfo($"[{account.AccountName}] Truegold Crucible: rows hidden, nothing to do");
                _rowsHidden = true;
                var close = await MatchAsync(PanelCloseArrowTemplate, PanelCloseArrowArea, PanelArrowThreshold, instance, logger);
                if (close.found) await ClickCenterInRectAsync(instance, logger, close.rect);
                await Task.Delay(1000, cancellationToken);
                return false;
            }

            await ClickAsync(instance, logger, new Point(row.rect.X + row.rect.Width / 2 + PanelRowTitleDx, row.rect.Y + row.rect.Height / 2));
            for (int i = 0; i < 4; i++)
            {
                await Task.Delay(1500, cancellationToken);
                if ((await MatchAsync(RemainingTodayTemplate, RemainingTodayArea, Threshold, instance, logger)).found) return true;
            }
            logger.LogWarning($"[{account.AccountName}] Truegold Crucible screen did not open");
            return false;
        }

        /// <summary>
        /// Taps Refine while it is teal and the Crucible screen is still showing (a popup would cover "Remaining today").
        /// </summary>
        private async Task<int> RefineAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            int refines = 0;
            for (int i = 0; i < MaxRefines; i++)
            {
                if (!(await MatchAsync(RemainingTodayTemplate, RemainingTodayArea, Threshold, instance, logger)).found)
                {
                    logger.LogWarning($"[{account.AccountName}] Truegold Crucible: screen left or covered, stopping refines");
                    break;
                }
                var button = await MatchAsync(RefineButtonTemplate, RefineButtonArea, Threshold, instance, logger);
                if (!button.found || !await IsRefineActiveAsync(button.rect, instance, logger)) break;

                await ClickCenterInRectAsync(instance, logger, button.rect);
                refines++;
                await Task.Delay(2000, cancellationToken);
            }
            logger.LogInfo($"[{account.AccountName}] Truegold Crucible: {refines} refine(s)");
            return refines;
        }

        /// <summary>
        /// Super Refine x1 at the daily -50% price; skipped once the badge has turned into "Best Deals".
        /// </summary>
        private async Task<bool> SuperRefineDiscountAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            if (!(await MatchAsync(RemainingTodayTemplate, RemainingTodayArea, Threshold, instance, logger)).found) return false;
            var badge = await MatchAsync(DiscountBadgeTemplate, DiscountBadgeArea, Threshold, instance, logger);
            if (!badge.found)
            {
                logger.LogInfo($"[{account.AccountName}] Truegold Crucible: no -50% discount ({badge.confidence:F2})");
                return false;
            }
            var x1 = await MatchAsync(SuperRefineX1Template, SuperRefineX1Area, Threshold, instance, logger);
            if (!x1.found) return false;

            await ClickCenterInRectAsync(instance, logger, x1.rect);
            await Task.Delay(2500, cancellationToken);
            bool used = !(await MatchAsync(DiscountBadgeTemplate, DiscountBadgeArea, Threshold, instance, logger)).found;
            logger.LogInfo($"[{account.AccountName}] Truegold Crucible: discount super refine {(used ? "used" : "badge still shown")}");
            return used;
        }

        private async Task<bool> IsRefineActiveAsync(Rectangle buttonRect, int instance, LogService logger)
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
            OpenCvSharp.Cv2.InRange(hsv, new OpenCvSharp.Scalar(80, 120, 150), new OpenCvSharp.Scalar(100, 255, 255), mask);
            int teal = OpenCvSharp.Cv2.CountNonZero(mask);
            logger.LogInfo($"Truegold Crucible: Refine button teal pixels {teal}");
            return teal >= RefineMinTealPixels;
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
