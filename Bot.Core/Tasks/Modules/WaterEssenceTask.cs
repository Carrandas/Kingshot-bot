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
using System.Text.RegularExpressions;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// Side panel > Daily tab > Water Essence Gathering: opens the Fountain of Life on My Island and taps the
    /// green water bubbles (the fountain and the building next to it) to collect the gathered water essence.
    /// Only goes once the row shows at least MinAvailable ("Available: 4,606/5,040"), so short trips are skipped.
    /// Collecting is free; no step spends anything.
    /// </summary>
    public class WaterEssenceTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.WaterEssence;
        public override string Name => "Water Essence";

        private LocatorService? _locatorService;

        private const string PanelOpenArrowTemplate = "panel-open-arrow.png";
        private const string PanelCloseArrowTemplate = "panel-close-arrow.png";
        private const string WaterRowIconTemplate = "water-row-icon.png";   // drop icon of the Fountain of Life row
        private const string IslandBackTemplate = "island-back.png";        // "< My Island" title
        private const string WaterBubbleTemplate = "water-bubble.png";      // green bubble with a water drop
        private static readonly Rectangle PanelOpenArrowArea = new Rectangle(0, 420, 60, 260);
        private static readonly Rectangle PanelCloseArrowArea = new Rectangle(400, 420, 120, 260);
        private static readonly Rectangle PanelListArea = new Rectangle(0, 300, 120, 520);
        private static readonly Rectangle IslandBackArea = new Rectangle(0, 0, 260, 80);
        // Island view below the title bar and left of the side buttons' lower half; the water counter at the top right scores <= 0.55
        private static readonly Rectangle WaterBubbleArea = new Rectangle(0, 120, 720, 880);
        private static readonly Point DailyTabPoint = new Point(370, 268);
        private const int PanelRowArrowDx = 359;  // teal arrow button at the right of the row
        // "Available: 4,606/5,040" line, relative to the row icon's top left; the text is centred so it moves with its length
        private static readonly Rectangle AvailableTextOffset = new Rectangle(76, 20, 290, 36);
        private const double PanelArrowThreshold = 0.88;
        private const double PanelRowThreshold = 0.93;  // 1.00 on the row
        private const double Threshold = 0.90;          // island title: 0.97-1.00 present, <= 0.47 elsewhere
        private const double BubbleThreshold = 0.65;    // 0.91-0.98 free, 0.66-0.83 under the tutorial hand, <= 0.55 elsewhere
        private const int MaxBubbleTaps = 4;            // two bubbles; a missed tap gets one retry each
        private const int MinAvailable = 1000;

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            try
            {
                _locatorService = new LocatorService(logger, account, TaskType);
                int instance = account.InstanceNumber;
                await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

                var (opened, available) = await OpenFountainAsync(account, logger, instance, cancellationToken);
                if (!opened)
                {
                    await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);
                    return TaskExecutionDetails.Succeeded(available >= 0 && available < MinAvailable
                        ? $"only {available} water essence available"
                        : "Fountain row hidden or island did not open");
                }

                int collected = await CollectBubblesAsync(account, logger, instance, cancellationToken);

                var back = await MatchAsync(IslandBackTemplate, IslandBackArea, Threshold, instance, logger);
                if (back.found)
                {
                    await ClickCenterInRectAsync(instance, logger, back.rect);
                    await Task.Delay(2000, cancellationToken);
                }
                await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

                string result = $"{collected} water bubble(s) collected ({(available >= 0 ? available.ToString() : "?")} available)";
                logger.LogInfo($"[{account.AccountName}] Water Essence done: {result}");
                return TaskExecutionDetails.Succeeded(result);
            }
            catch (OperationCanceledException)
            {
                return TaskExecutionDetails.Failed("Task was cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error in Water Essence: {ex.Message}");
                return TaskExecutionDetails.Failed(ex.Message);
            }
        }

        /// <summary>
        /// Side panel > Daily tab > Fountain of Life row arrow, when enough water essence is available.
        /// Returns the amount read from the row (-1 when unreadable; the island is opened anyway then).
        /// </summary>
        private async Task<(bool opened, int available)> OpenFountainAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            if (!(await MatchAsync(PanelCloseArrowTemplate, PanelCloseArrowArea, PanelArrowThreshold, instance, logger)).found)
            {
                var arrow = await MatchAsync(PanelOpenArrowTemplate, PanelOpenArrowArea, PanelArrowThreshold, instance, logger);
                if (!arrow.found)
                {
                    logger.LogWarning($"[{account.AccountName}] Water Essence: side panel arrow not found");
                    return (false, -1);
                }
                await ClickCenterInRectAsync(instance, logger, arrow.rect);
                await Task.Delay(1500, cancellationToken);
            }
            await ClickAsync(instance, logger, DailyTabPoint);
            await Task.Delay(1200, cancellationToken);

            var row = await FindDailyPanelRowAsync(() => MatchAsync(WaterRowIconTemplate, PanelListArea, PanelRowThreshold, instance, logger), instance, logger, cancellationToken);
            int available = row.found ? await ReadAvailableAsync(row.rect, instance, logger) : -1;
            if (!row.found || (available >= 0 && available < MinAvailable))
            {
                logger.LogInfo(row.found
                    ? $"[{account.AccountName}] Water Essence: {available} available, waiting for {MinAvailable}"
                    : $"[{account.AccountName}] Water Essence: row hidden, nothing to do");
                var close = await MatchAsync(PanelCloseArrowTemplate, PanelCloseArrowArea, PanelArrowThreshold, instance, logger);
                if (close.found) await ClickCenterInRectAsync(instance, logger, close.rect);
                await Task.Delay(1000, cancellationToken);
                return (false, available);
            }

            await ClickAsync(instance, logger, new Point(row.rect.X + row.rect.Width / 2 + PanelRowArrowDx, row.rect.Y + row.rect.Height / 2));
            // A cloud transition plays before the island shows
            for (int i = 0; i < 6; i++)
            {
                await Task.Delay(1500, cancellationToken);
                if ((await MatchAsync(IslandBackTemplate, IslandBackArea, Threshold, instance, logger)).found) return (true, available);
            }
            logger.LogWarning($"[{account.AccountName}] Water Essence: island did not open");
            return (false, available);
        }

        /// <summary>
        /// Taps the best-matching water bubble until none is left. The bubbles bob up and down, so each tap uses a fresh match.
        /// </summary>
        private async Task<int> CollectBubblesAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            int collected = 0;
            for (int i = 0; i < MaxBubbleTaps; i++)
            {
                var bubble = await MatchAsync(WaterBubbleTemplate, WaterBubbleArea, BubbleThreshold, instance, logger);
                if (!bubble.found)
                {
                    if (collected == 0) logger.LogInfo($"[{account.AccountName}] Water Essence: no water bubbles ({bubble.confidence:F2})");
                    break;
                }
                await ClickCenterInRectAsync(instance, logger, bubble.rect);
                collected++;
                await Task.Delay(1500, cancellationToken);
            }
            logger.LogInfo($"[{account.AccountName}] Water Essence: tapped {collected} water bubble(s)");
            return collected;
        }

        /// <summary>
        /// Reads the green "Available: N/5,040" line of the row. Returns -1 when unreadable.
        /// </summary>
        private async Task<int> ReadAvailableAsync(Rectangle rowIcon, int instance, LogService logger)
        {
            var shot = await TakeScreenshotAsync(instance, logger);
            if (shot == null) return -1;

            // Green text on the dark panel: keep only the green pixels as white on black, like the Journey supplies box
            var area = new Rectangle(rowIcon.X + AvailableTextOffset.X, rowIcon.Y + AvailableTextOffset.Y, AvailableTextOffset.Width, AvailableTextOffset.Height);
            byte[] text;
            using (var image = OpenCvSharp.Cv2.ImDecode(shot, OpenCvSharp.ImreadModes.Color))
            {
                var roi = new OpenCvSharp.Rect(area.X, area.Y, area.Width, area.Height)
                    .Intersect(new OpenCvSharp.Rect(0, 0, image.Width, image.Height));
                using var region = new OpenCvSharp.Mat(image, roi);
                using var hsv = new OpenCvSharp.Mat();
                OpenCvSharp.Cv2.CvtColor(region, hsv, OpenCvSharp.ColorConversionCodes.BGR2HSV);
                using var mask = new OpenCvSharp.Mat();
                OpenCvSharp.Cv2.InRange(hsv, new OpenCvSharp.Scalar(45, 120, 150), new OpenCvSharp.Scalar(85, 255, 255), mask);
                using var bgr = new OpenCvSharp.Mat();
                OpenCvSharp.Cv2.CvtColor(mask, bgr, OpenCvSharp.ColorConversionCodes.GRAY2BGR);
                text = bgr.ToBytes(".png");
                area = new Rectangle(0, 0, roi.Width, roi.Height);
            }
            var ocrConfig = new OCRConfiguration
            {
                ScaleFactor = 2,
                AdaptiveC = 5,
                MedianBlurKernelSize = 1,
                CharacterWhitelist = "Availbe:0123456789,./",
                PageSegMode = Tesseract.PageSegMode.SingleLine
            };
            using var ocr = new OCRService(logger, ocrConfig);
            string read = ocr.ExtractTextFromScreenArea(text, area);
            var match = Regex.Match(read ?? "", @"(\d[\d,.]*)\s*/");
            logger.LogInfo($"Water Essence: available OCR '{read?.Trim()}'");
            return match.Success && int.TryParse(match.Groups[1].Value.Replace(",", "").Replace(".", ""), out int value) ? value : -1;
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
