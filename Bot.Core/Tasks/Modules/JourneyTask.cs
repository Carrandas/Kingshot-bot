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
    /// Side panel > Daily tab > Realm Journey: claims the free Journey Supplies (08:00 and 16:00 UTC)
    /// and spends all supplies on an Idle Journey.
    ///
    /// SAFETY: the supplies popup also has a 💎1,000 "Daily Limit" buy button; it lies outside the Claim search
    /// area and is never tapped. Idle Journey only spends Journey Supplies.
    /// Re-opening the Journey screen stops a running Idle Journey, so the task skips while one is running
    /// (the town portrait shows an "N/30" counter). Supplies regenerate, so an Idle Journey starts only from MinIdleSupplies.
    /// </summary>
    public class JourneyTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.Journey;
        public override string Name => "Journey";

        private LocatorService? _locatorService;

        private const string PanelOpenArrowTemplate = "panel-open-arrow.png";
        private const string PanelCloseArrowTemplate = "panel-close-arrow.png";
        private const string JourneyRowIconTemplate = "journey-row-icon.png";   // signpost icon of the Realm Journey row
        private const string SuppliesRowIconTemplate = "supplies-row-icon.png"; // backpack icon of the Journey Supplies row
        private const string IdleRunningTemplate = "idle-running.png";          // horse portrait in town; stays after the journey ends
        private const string JourneyIdleButtonTemplate = "journey-idle-button.png";
        private const string JourneyBackTemplate = "journey-back.png";
        private const string SuppliesIconTemplate = "supplies-icon.png";        // backpack of the "20/100 +" counter
        private const string SuppliesHeaderTemplate = "supplies-header.png";    // "Claim For Free"
        private const string SuppliesClaimTemplate = "supplies-claim.png";      // green "Claim"
        private const string SuppliesCloseTemplate = "supplies-close.png";
        private const string IdleHeaderTemplate = "idle-header.png";            // "Idle Journey" popup title
        private const string IdleMaxTemplate = "idle-max.png";
        private const string IdleStartTemplate = "idle-start.png";              // teal "🎒 N" start button
        private const string IdleCloseTemplate = "idle-close.png";
        private const string RealmJourneySignTemplate = "realm-journey-sign.png"; // hub signpost; 1.00 even under the tutorial hand, <= 0.47 elsewhere
        private static readonly Rectangle PanelOpenArrowArea = new Rectangle(0, 420, 60, 260);
        private static readonly Rectangle PanelCloseArrowArea = new Rectangle(400, 420, 120, 260);
        private static readonly Rectangle PanelListArea = new Rectangle(0, 300, 120, 520);
        private static readonly Rectangle IdleRunningArea = new Rectangle(480, 960, 160, 160);
        private static readonly Rectangle JourneyIdleButtonArea = new Rectangle(560, 1130, 150, 100);
        private static readonly Rectangle JourneyBackArea = new Rectangle(0, 0, 110, 90);
        private static readonly Rectangle SuppliesIconArea = new Rectangle(440, 0, 120, 80);
        private static readonly Rectangle SuppliesHeaderArea = new Rectangle(160, 490, 240, 80);
        // Claim row only; the 💎1,000 button sits at y≈720
        private static readonly Rectangle SuppliesClaimArea = new Rectangle(480, 510, 190, 100);
        private static readonly Rectangle SuppliesCloseArea = new Rectangle(650, 370, 70, 90);
        private static readonly Rectangle IdleHeaderArea = new Rectangle(240, 340, 240, 80);
        private static readonly Rectangle IdleMaxArea = new Rectangle(560, 680, 100, 100);
        private static readonly Rectangle IdleStartArea = new Rectangle(190, 800, 200, 130);
        private static readonly Rectangle IdleCloseArea = new Rectangle(620, 330, 100, 90);
        private static readonly Rectangle RealmJourneySignArea = new Rectangle(440, 480, 280, 280);
        private static readonly Point DailyTabPoint = new Point(370, 268);
        private static readonly Point HubBackPoint = new Point(49, 37);
        private const int PanelRowTitleDx = 180;
        private const int SuppliesPlusDx = 122; // the green "+" right of the counter
        private const double PanelArrowThreshold = 0.88;
        private const double PanelRowThreshold = 0.93;  // other row icons score <= 0.81
        private const double IdleRunningThreshold = 0.85;
        private const double Threshold = 0.90;          // screen elements: 1.00 present, <= 0.66 elsewhere
        private const double SuppliesIconThreshold = 0.93; // 0.85 behind the popups
        private const int MinIdleSupplies = 10;
        // White "N/30" counter under the portrait while idling: ~210 white pixels, ~43 without it
        private const int IdleCounterMinWhitePixels = 120;
        private static readonly Rectangle IdleSuppliesTextArea = new Rectangle(455, 712, 110, 42); // number box of the Idle popup

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            try
            {
                _locatorService = new LocatorService(logger, account, TaskType);
                int instance = account.InstanceNumber;
                await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

                if (await IsIdleJourneyRunningAsync(instance, logger))
                {
                    logger.LogInfo($"[{account.AccountName}] Journey: Idle Journey still running, skipping");
                    return TaskExecutionDetails.Succeeded("Idle Journey running");
                }

                if (!await OpenJourneyAsync(account, logger, instance, cancellationToken))
                {
                    await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);
                    return TaskExecutionDetails.Succeeded("Journey row hidden or screen did not open");
                }

                bool claimed = await ClaimSuppliesAsync(account, logger, instance, cancellationToken);
                bool idling = await StartIdleJourneyAsync(account, logger, instance, cancellationToken);
                if (!idling)
                {
                    var back = await MatchAsync(JourneyBackTemplate, JourneyBackArea, Threshold, instance, logger);
                    if (back.found)
                    {
                        await ClickCenterInRectAsync(instance, logger, back.rect);
                        await Task.Delay(1500, cancellationToken);
                    }
                }
                // Back (or starting an Idle Journey) can land on the hub; its back arrow scores only 0.88 against journey-back
                if ((await MatchAsync(RealmJourneySignTemplate, RealmJourneySignArea, Threshold, instance, logger)).found)
                {
                    await ClickAsync(instance, logger, HubBackPoint);
                    await Task.Delay(1500, cancellationToken);
                }
                await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

                string result = $"supplies {(claimed ? "claimed" : "not available")}, idle journey {(idling ? "started" : "not started")}";
                logger.LogInfo($"[{account.AccountName}] Journey done: {result}");
                return TaskExecutionDetails.Succeeded(result);
            }
            catch (OperationCanceledException)
            {
                return TaskExecutionDetails.Failed("Task was cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error in Journey: {ex.Message}");
                return TaskExecutionDetails.Failed(ex.Message);
            }
        }

        /// <summary>
        /// Side panel > Daily tab > Realm Journey row (or the Journey Supplies row) > hub > Realm Journey signpost.
        /// </summary>
        private async Task<bool> OpenJourneyAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            if (!(await MatchAsync(PanelCloseArrowTemplate, PanelCloseArrowArea, PanelArrowThreshold, instance, logger)).found)
            {
                var arrow = await MatchAsync(PanelOpenArrowTemplate, PanelOpenArrowArea, PanelArrowThreshold, instance, logger);
                if (!arrow.found)
                {
                    logger.LogWarning($"[{account.AccountName}] Journey: side panel arrow not found");
                    return false;
                }
                await ClickCenterInRectAsync(instance, logger, arrow.rect);
                await Task.Delay(1500, cancellationToken);
            }
            await ClickAsync(instance, logger, DailyTabPoint);
            await Task.Delay(1200, cancellationToken);

            var row = await FindDailyPanelRowAsync(async () =>
            {
                var journey = await MatchAsync(JourneyRowIconTemplate, PanelListArea, PanelRowThreshold, instance, logger);
                return journey.found ? journey : await MatchAsync(SuppliesRowIconTemplate, PanelListArea, PanelRowThreshold, instance, logger);
            }, instance, logger, cancellationToken);
            if (!row.found)
            {
                logger.LogInfo($"[{account.AccountName}] Journey: rows hidden, nothing to do");
                var close = await MatchAsync(PanelCloseArrowTemplate, PanelCloseArrowArea, PanelArrowThreshold, instance, logger);
                if (close.found) await ClickCenterInRectAsync(instance, logger, close.rect);
                await Task.Delay(1000, cancellationToken);
                return false;
            }

            await ClickAsync(instance, logger, new Point(row.rect.X + row.rect.Width / 2 + PanelRowTitleDx, row.rect.Y + row.rect.Height / 2));
            bool signTapped = false;
            for (int i = 0; i < 7; i++)
            {
                await Task.Delay(1500, cancellationToken);
                if ((await MatchAsync(JourneyIdleButtonTemplate, JourneyIdleButtonArea, Threshold, instance, logger)).found) return true;
                if (signTapped) continue;
                // The row now opens a hub (Lostlands / Realm Journey) first; a transition plays after the signpost
                var sign = await MatchAsync(RealmJourneySignTemplate, RealmJourneySignArea, Threshold, instance, logger);
                if (sign.found)
                {
                    await ClickCenterInRectAsync(instance, logger, sign.rect);
                    signTapped = true;
                }
            }
            logger.LogWarning($"[{account.AccountName}] Journey screen did not open");
            return false;
        }

        /// <summary>
        /// The "+" next to the supplies counter opens the supplies popup; only its green Claim is tapped.
        /// </summary>
        private async Task<bool> ClaimSuppliesAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            // The screen can still be dimmed by an opening animation right after it loads
            var icon = await MatchAsync(SuppliesIconTemplate, SuppliesIconArea, SuppliesIconThreshold, instance, logger);
            for (int i = 0; i < 3 && !icon.found; i++)
            {
                await Task.Delay(1500, cancellationToken);
                icon = await MatchAsync(SuppliesIconTemplate, SuppliesIconArea, SuppliesIconThreshold, instance, logger);
            }
            if (!icon.found)
            {
                logger.LogWarning($"[{account.AccountName}] Journey: supplies counter not found");
                return false;
            }
            await ClickAsync(instance, logger, new Point(icon.rect.X + icon.rect.Width / 2 + SuppliesPlusDx, icon.rect.Y + icon.rect.Height / 2));
            await Task.Delay(1500, cancellationToken);

            if (!(await MatchAsync(SuppliesHeaderTemplate, SuppliesHeaderArea, Threshold, instance, logger)).found)
            {
                logger.LogWarning($"[{account.AccountName}] Journey: supplies popup did not open");
                return false;
            }

            bool claimed = false;
            var claim = await MatchAsync(SuppliesClaimTemplate, SuppliesClaimArea, Threshold, instance, logger);
            if (claim.found)
            {
                await ClickCenterInRectAsync(instance, logger, claim.rect);
                await Task.Delay(2000, cancellationToken);
                logger.LogInfo($"[{account.AccountName}] Journey: free supplies claimed");
                claimed = true;
            }
            else
            {
                logger.LogInfo($"[{account.AccountName}] Journey: free supplies not ready yet");
            }

            var close = await MatchAsync(SuppliesCloseTemplate, SuppliesCloseArea, Threshold, instance, logger);
            if (close.found)
            {
                await ClickCenterInRectAsync(instance, logger, close.rect);
                await Task.Delay(1500, cancellationToken);
            }
            return claimed;
        }

        /// <summary>
        /// Idle > max > start. Starting leaves the Journey screen by itself.
        /// </summary>
        private async Task<bool> StartIdleJourneyAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            var idle = await MatchAsync(JourneyIdleButtonTemplate, JourneyIdleButtonArea, Threshold, instance, logger);
            if (!idle.found) return false;
            await ClickCenterInRectAsync(instance, logger, idle.rect);
            await Task.Delay(1500, cancellationToken);

            if (!(await MatchAsync(IdleHeaderTemplate, IdleHeaderArea, Threshold, instance, logger)).found)
            {
                logger.LogWarning($"[{account.AccountName}] Journey: Idle Journey popup did not open");
                return false;
            }

            var max = await MatchAsync(IdleMaxTemplate, IdleMaxArea, Threshold, instance, logger);
            if (max.found)
            {
                await ClickCenterInRectAsync(instance, logger, max.rect);
                await Task.Delay(800, cancellationToken);
            }

            int supplies = await ReadIdleSuppliesAsync(instance, logger);
            var start = await MatchAsync(IdleStartTemplate, IdleStartArea, Threshold, instance, logger);
            if (supplies < MinIdleSupplies || !start.found)
            {
                // Too few supplies (or the start button is greyed out): wait for them to regenerate
                logger.LogInfo($"[{account.AccountName}] Journey: {(supplies < 0 ? "supplies unreadable" : $"{supplies} supplies")}, need {MinIdleSupplies} for an Idle Journey ({start.confidence:F2})");
                var close = await MatchAsync(IdleCloseTemplate, IdleCloseArea, Threshold, instance, logger);
                if (close.found) await ClickCenterInRectAsync(instance, logger, close.rect);
                await Task.Delay(1200, cancellationToken);
                return false;
            }

            await ClickAsync(instance, logger, new Point(360, start.rect.Y + start.rect.Height / 2));
            await Task.Delay(2500, cancellationToken);
            logger.LogInfo($"[{account.AccountName}] Journey: Idle Journey started with {supplies} supplies");
            return true;
        }

        /// <summary>
        /// The horse portrait stays in town after the journey ends; only the white "N/30" counter under it means it is still running.
        /// </summary>
        private async Task<bool> IsIdleJourneyRunningAsync(int instance, LogService logger)
        {
            var portrait = await MatchAsync(IdleRunningTemplate, IdleRunningArea, IdleRunningThreshold, instance, logger);
            if (!portrait.found) return false;

            var shot = await TakeScreenshotAsync(instance, logger);
            if (shot == null) return false;
            using var image = OpenCvSharp.Cv2.ImDecode(shot, OpenCvSharp.ImreadModes.Color);
            var roi = new OpenCvSharp.Rect(portrait.rect.X + 2, portrait.rect.Y + 48, 60, 22)
                .Intersect(new OpenCvSharp.Rect(0, 0, image.Width, image.Height));
            using var region = new OpenCvSharp.Mat(image, roi);
            using var mask = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.InRange(region, new OpenCvSharp.Scalar(215, 215, 215), new OpenCvSharp.Scalar(255, 255, 255), mask);
            int white = OpenCvSharp.Cv2.CountNonZero(mask);
            logger.LogInfo($"Journey: portrait {portrait.confidence:F2}, counter pixels {white}");
            return white >= IdleCounterMinWhitePixels;
        }

        /// <summary>
        /// Reads the number box of the Idle Journey popup (after max = all supplies). Returns -1 when unreadable.
        /// </summary>
        private async Task<int> ReadIdleSuppliesAsync(int instance, LogService logger)
        {
            var shot = await TakeScreenshotAsync(instance, logger);
            if (shot == null) return -1;

            // White digits with a dark outline on a grey box: keep only the white pixels, else the
            // OCR inversion + Otsu step makes the digits as dark as the box and nothing is read
            byte[] digits;
            using (var image = OpenCvSharp.Cv2.ImDecode(shot, OpenCvSharp.ImreadModes.Color))
            {
                var roi = new OpenCvSharp.Rect(IdleSuppliesTextArea.X, IdleSuppliesTextArea.Y, IdleSuppliesTextArea.Width, IdleSuppliesTextArea.Height)
                    .Intersect(new OpenCvSharp.Rect(0, 0, image.Width, image.Height));
                using var region = new OpenCvSharp.Mat(image, roi);
                using var hsv = new OpenCvSharp.Mat();
                OpenCvSharp.Cv2.CvtColor(region, hsv, OpenCvSharp.ColorConversionCodes.BGR2HSV);
                using var mask = new OpenCvSharp.Mat();
                OpenCvSharp.Cv2.InRange(hsv, new OpenCvSharp.Scalar(0, 0, 225), new OpenCvSharp.Scalar(180, 40, 255), mask);
                using var bgr = new OpenCvSharp.Mat();
                OpenCvSharp.Cv2.CvtColor(mask, bgr, OpenCvSharp.ColorConversionCodes.GRAY2BGR);
                digits = bgr.ToBytes(".png");
            }
            var ocrConfig = new OCRConfiguration
            {
                ScaleFactor = 2,
                AdaptiveC = 5,
                MedianBlurKernelSize = 1,
                CharacterWhitelist = "0123456789",
                PageSegMode = Tesseract.PageSegMode.SingleWord
            };
            using var ocr = new OCRService(logger, ocrConfig);
            string text = ocr.ExtractTextFromScreenArea(digits, new Rectangle(0, 0, IdleSuppliesTextArea.Width, IdleSuppliesTextArea.Height));
            var match = Regex.Match(text ?? "", @"\d+");
            logger.LogInfo($"Journey: idle supplies OCR '{text?.Trim()}'");
            return match.Success ? int.Parse(match.Value) : -1;
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
