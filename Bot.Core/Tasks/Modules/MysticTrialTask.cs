using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.ImageDetection;
using Bot.Core.Services;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// Side panel > Daily tab > Mystic Trial: in every open building (coloured, with a green name plate; the closed
    /// ones are grey) claims the Raid rewards and uses the day's Challenge attempts with a 50/20/30 troop ratio.
    /// A lost battle uses an attempt (5 a day) and is retried; a won one goes on to the next stage for free.
    /// The Daily row hides once everything is done, and the attempts refresh at the 00:00 UTC reset: one run a day.
    /// </summary>
    public class MysticTrialTask : BaseTaskWithCommonPatterns, IScheduledTask
    {
        public override TaskType TaskType => TaskType.MysticTrial;
        public override string Name => "Mystic Trial";

        private const string PanelOpenArrowTemplate = "panel-open-arrow.png";
        private const string PanelCloseArrowTemplate = "panel-close-arrow.png";
        private const string RowIconTemplate = "mystic-row-icon.png";
        private const string MapTitleTemplate = "map-title.png";          // "Mystic Trial" (the building screens show their own name)
        private const string RaidButtonTemplate = "raid-button.png";
        private const string ClaimButtonTemplate = "claim-button.png";    // Raid Rewards popup
        private const string ChallengeButtonTemplate = "challenge-button.png";
        private const string BalanceButtonTemplate = "balance-button.png";
        private const string BalanceTitleTemplate = "balance-title.png";
        private const string BalanceFullTemplate = "balance-full.png";    // "100%/100%"; a dropped digit shows e.g. 82%
        private const string CapacityPopupTemplate = "capacity-popup.png"; // "Confirmation": squads not full, continue anyway?
        private const string DeployButtonTemplate = "deploy-button.png";
        private const string VictoryTemplate = "victory-title.png";
        private const string FailedTemplate = "failed-title.png";

        private static readonly Rectangle PanelOpenArrowArea = new Rectangle(0, 420, 60, 260);
        private static readonly Rectangle PanelCloseArrowArea = new Rectangle(400, 420, 120, 260);
        private static readonly Rectangle PanelListArea = new Rectangle(0, 300, 120, 520);
        private static readonly Rectangle TitleArea = new Rectangle(80, 0, 220, 80);
        private static readonly Rectangle BottomButtonArea = new Rectangle(200, 1170, 320, 90);  // Raid / Challenge
        private static readonly Rectangle ClaimButtonArea = new Rectangle(200, 970, 320, 90);
        private static readonly Rectangle BalanceButtonArea = new Rectangle(270, 1130, 120, 145);
        private static readonly Rectangle DeployButtonArea = new Rectangle(400, 1170, 300, 90);
        private static readonly Rectangle BalanceTitleArea = new Rectangle(270, 285, 180, 70);
        private static readonly Rectangle BalanceFullArea = new Rectangle(240, 370, 230, 70);
        private static readonly Rectangle CapacityPopupArea = new Rectangle(230, 410, 260, 80);
        private static readonly Rectangle ResultTitleArea = new Rectangle(230, 300, 260, 110);
        private static readonly Rectangle ChallengeColourArea = new Rectangle(210, 1180, 300, 75);
        private static readonly Rectangle RetryColourArea = new Rectangle(378, 962, 290, 78);
        private static readonly Rectangle NextColourArea = new Rectangle(372, 824, 302, 78);
        private static readonly Rectangle MapArea = new Rectangle(0, 100, 720, 980);   // between the title bar and the progress bar
        private static readonly System.Drawing.Point DailyTabPoint = new System.Drawing.Point(370, 268);
        // Back arrow, top left on every Mystic Trial screen (a template of it scores 0.76 on Knowledge Nexus's light background)
        private static readonly System.Drawing.Point BackPoint = new System.Drawing.Point(48, 40);
        private static readonly System.Drawing.Point RetryPoint = new System.Drawing.Point(522, 1000);
        private static readonly System.Drawing.Point NextPoint = new System.Drawing.Point(522, 862);
        private static readonly System.Drawing.Point CloseResultPoint = new System.Drawing.Point(360, 1180);  // "Tap anywhere to close"
        private static readonly System.Drawing.Point ConfirmBalancePoint = new System.Drawing.Point(360, 980);
        private static readonly System.Drawing.Point NumberOkPoint = new System.Drawing.Point(640, 1217);   // OK of the number input bar
        private static readonly System.Drawing.Point CloseCapacityPopupPoint = new System.Drawing.Point(637, 447);
        private const int RatioAttempts = 3;
        // Balance popup percentage boxes; lowered first so the total never goes over 100%
        private static readonly (string Name, System.Drawing.Point Box, int Percent)[] Ratio =
        {
            ("Cavalry", new System.Drawing.Point(594, 677), 20),
            ("Archer", new System.Drawing.Point(594, 823), 30),
            ("Infantry", new System.Drawing.Point(594, 530), 50),
        };
        private const int PlateToBuildingDy = -110;
        private const int PanelRowTitleDx = 180;
        private const int MaxBattles = 15;               // 5 lost ones end the day; won ones are free, so cap the run
        private const int MaxRounds = 6;                 // Challenge taps: a new chapter needs another one
        private const int BattleTimeoutSeconds = 90;
        private const double PanelArrowThreshold = 0.88;
        private const double PanelRowThreshold = 0.93;   // row icon 1.00, other rows <= 0.78
        private const double Threshold = 0.90;           // screen elements 0.98-1.00 present, <= 0.84 elsewhere (the grey Challenge; 82% total 0.70)
        // Teal buttons: Challenge ~16,900 / Retry ~17,800 / Next ~19,100 teal pixels; 0 once grey (no attempts left)
        private const int ActiveMinTealPixels = 5000;
        // Green name plate of an open building (~210x68, hue 84-87); the water around is hue 93-95
        private const int PlateMinWidth = 150, PlateMaxWidth = 270, PlateMinHeight = 45, PlateMaxHeight = 90;

        public void ClearSchedule(string accountKey) => DailyGate.Clear(TaskType, accountKey);

        public DateTime? GetNextRunUtc(string accountKey, AccountSettings account) => DailyGate.NextRunUtc(TaskType, accountKey);

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            try
            {
                string accountKey = Schedule.AccountKey(AccountDetectionTask.GetCachedAccountId(account.InstanceNumber), account);
                if (DailyGate.DoneToday(TaskType, accountKey))
                    return TaskExecutionDetails.SkippedBecause("Already done today");

                var locator = new LocatorService(logger, account, TaskType);
                int instance = account.InstanceNumber;
                await locator.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

                var opened = await OpenMysticTrialAsync(account, logger, instance, cancellationToken);
                if (opened != true)
                {
                    // a hidden row means today's raids and attempts are used; a screen that failed to open is retried next cycle
                    if (opened == null) DailyGate.MarkDone(TaskType, accountKey);
                    await locator.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);
                    return opened == null
                        ? TaskExecutionDetails.Succeeded("Mystic Trial row hidden, nothing to do")
                        : TaskExecutionDetails.Failed("Mystic Trial screen did not open");
                }

                var buildings = await FindOpenBuildingsAsync(instance, logger);
                logger.LogInfo($"[{account.AccountName}] Mystic Trial: {buildings.Count} open building(s)");

                int raids = 0, battles = 0;
                foreach (var building in buildings)
                {
                    var (raided, fought) = await RunBuildingAsync(account, logger, instance, building, cancellationToken);
                    if (raided) raids++;
                    battles += fought;
                    if (!await BackToMapAsync(logger, instance, cancellationToken))
                    {
                        logger.LogWarning($"[{account.AccountName}] Mystic Trial: lost the map, stopping");
                        break;
                    }
                }
                DailyGate.MarkDone(TaskType, accountKey);

                if ((await MatchAsync(MapTitleTemplate, TitleArea, Threshold, instance, logger)).found)
                {
                    await ClickAsync(instance, logger, BackPoint);
                    await Task.Delay(2000, cancellationToken);
                }
                await locator.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

                string result = $"{buildings.Count} building(s), {raids} raid(s) claimed, {battles} battle(s)";
                logger.LogInfo($"[{account.AccountName}] Mystic Trial done: {result}");
                return TaskExecutionDetails.Succeeded(result);
            }
            catch (OperationCanceledException)
            {
                return TaskExecutionDetails.Failed("Task was cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error in Mystic Trial: {ex.Message}");
                return TaskExecutionDetails.Failed(ex.Message);
            }
        }

        /// <summary>
        /// Side panel > Daily tab > Mystic Trial row. True = map open, null = row hidden (done today), false = failed.
        /// </summary>
        private async Task<bool?> OpenMysticTrialAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            if (!(await MatchAsync(PanelCloseArrowTemplate, PanelCloseArrowArea, PanelArrowThreshold, instance, logger)).found)
            {
                var arrow = await MatchAsync(PanelOpenArrowTemplate, PanelOpenArrowArea, PanelArrowThreshold, instance, logger);
                if (!arrow.found)
                {
                    logger.LogWarning($"[{account.AccountName}] Mystic Trial: side panel arrow not found");
                    return false;
                }
                await ClickCenterInRectAsync(instance, logger, arrow.rect);
                await Task.Delay(1500, cancellationToken);
            }
            await ClickAsync(instance, logger, DailyTabPoint);
            await Task.Delay(1200, cancellationToken);

            var row = await FindDailyPanelRowAsync(
                () => MatchAsync(RowIconTemplate, PanelListArea, PanelRowThreshold, instance, logger),
                instance, logger, cancellationToken);
            if (!row.found)
            {
                logger.LogInfo($"[{account.AccountName}] Mystic Trial: row hidden, nothing to do");
                var close = await MatchAsync(PanelCloseArrowTemplate, PanelCloseArrowArea, PanelArrowThreshold, instance, logger);
                if (close.found) await ClickCenterInRectAsync(instance, logger, close.rect);
                await Task.Delay(1000, cancellationToken);
                return null;
            }

            await ClickAsync(instance, logger, new System.Drawing.Point(row.rect.X + row.rect.Width / 2 + PanelRowTitleDx, row.rect.Y + row.rect.Height / 2));
            for (int i = 0; i < 4; i++)
            {
                await Task.Delay(1500, cancellationToken);
                if ((await MatchAsync(MapTitleTemplate, TitleArea, Threshold, instance, logger)).found) return true;
            }
            logger.LogWarning($"[{account.AccountName}] Mystic Trial screen did not open");
            return false;
        }

        /// <summary>
        /// Tap points of the open buildings: above each green name plate, top to bottom.
        /// </summary>
        private async Task<List<System.Drawing.Point>> FindOpenBuildingsAsync(int instance, LogService logger)
        {
            var buildings = new List<System.Drawing.Point>();
            var screenshot = await TakeScreenshotAsync(instance, logger);
            if (screenshot == null) return buildings;

            using var image = Cv2.ImDecode(screenshot, ImreadModes.Color);
            using var map = new Mat(image, new Rect(MapArea.X, MapArea.Y, MapArea.Width, MapArea.Height));
            using var hsv = map.CvtColor(ColorConversionCodes.BGR2HSV);
            using var mask = hsv.InRange(new Scalar(78, 150, 150), new Scalar(89, 255, 255));
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(5, 5));
            Cv2.MorphologyEx(mask, mask, MorphTypes.Open, kernel);
            using var labels = new Mat();
            using var stats = new Mat();
            using var centroids = new Mat();
            int count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids);
            for (int i = 1; i < count; i++)
            {
                int w = stats.At<int>(i, (int)ConnectedComponentsTypes.Width);
                int h = stats.At<int>(i, (int)ConnectedComponentsTypes.Height);
                if (w < PlateMinWidth || w > PlateMaxWidth || h < PlateMinHeight || h > PlateMaxHeight) continue;
                int x = stats.At<int>(i, (int)ConnectedComponentsTypes.Left) + w / 2;
                int y = stats.At<int>(i, (int)ConnectedComponentsTypes.Top) + h / 2;
                buildings.Add(new System.Drawing.Point(MapArea.X + x, MapArea.Y + y + PlateToBuildingDy));
            }
            return buildings.OrderBy(p => p.Y).ToList();
        }

        /// <summary>
        /// One building: claim the Raid rewards, then Challenge until the attempts are gone. Returns (raid claimed, battles).
        /// </summary>
        private async Task<(bool raided, int battles)> RunBuildingAsync(AccountSettings account, LogService logger, int instance, System.Drawing.Point building, CancellationToken cancellationToken)
        {
            await ClickAsync(instance, logger, building);
            await Task.Delay(2500, cancellationToken);
            if ((await MatchAsync(MapTitleTemplate, TitleArea, Threshold, instance, logger)).found)
            {
                logger.LogWarning($"[{account.AccountName}] Mystic Trial: building at ({building.X}, {building.Y}) did not open");
                return (false, 0);
            }

            bool raided = false;
            var raid = await MatchAsync(RaidButtonTemplate, BottomButtonArea, Threshold, instance, logger);
            if (raid.found)
            {
                await ClickCenterInRectAsync(instance, logger, raid.rect);
                await Task.Delay(2000, cancellationToken);
                var claim = await MatchAsync(ClaimButtonTemplate, ClaimButtonArea, Threshold, instance, logger);
                if (claim.found)
                {
                    await ClickCenterInRectAsync(instance, logger, claim.rect);
                    await Task.Delay(2000, cancellationToken);
                    raided = true;
                    logger.LogInfo($"[{account.AccountName}] Mystic Trial: raid rewards claimed");
                }
            }

            int battles = 0, won = 0;
            for (int round = 0; round < MaxRounds && battles < MaxBattles; round++)
            {
                var challenge = await MatchAsync(ChallengeButtonTemplate, BottomButtonArea, Threshold, instance, logger);
                if (!challenge.found || await CountTealAsync(ChallengeColourArea, instance, logger) < ActiveMinTealPixels)
                {
                    logger.LogInfo($"[{account.AccountName}] Mystic Trial: no attempts left here");
                    break;
                }
                await ClickCenterInRectAsync(instance, logger, challenge.rect);
                await Task.Delay(2500, cancellationToken);
                if (!await DeployAsync(account, logger, instance, cancellationToken)) break;

                bool backOnStage = false;
                while (battles < MaxBattles)
                {
                    var result = await WaitForResultAsync(instance, logger, cancellationToken);
                    if (result == BattleResult.StageScreen)
                    {
                        // Next after the last stage of a chapter opens the stage screen of the new one: Challenge again
                        backOnStage = true;
                        break;
                    }
                    if (result == BattleResult.Timeout)
                    {
                        logger.LogWarning($"[{account.AccountName}] Mystic Trial: no battle result after {BattleTimeoutSeconds}s");
                        break;
                    }
                    battles++;
                    if (result == BattleResult.Victory) won++;
                    await Task.Delay(1500, cancellationToken);  // the buttons fade in after the title

                    // Won: Next (the next stage, free). Lost: Retry, grey once the attempts are used up.
                    var (colourArea, point) = result == BattleResult.Victory ? (NextColourArea, NextPoint) : (RetryColourArea, RetryPoint);
                    if (await CountTealAsync(colourArea, instance, logger) < ActiveMinTealPixels) break;
                    await ClickAsync(instance, logger, point);
                    await Task.Delay(2000, cancellationToken);
                }
                if (!backOnStage)
                {
                    await CloseResultScreenAsync(instance, logger, cancellationToken);
                    break;
                }
            }
            logger.LogInfo($"[{account.AccountName}] Mystic Trial: {battles} battle(s), {won} won");
            return (raided, battles);
        }

        /// <summary>
        /// Deploy screen: set the ratio and Deploy. False when the squads are not full or a screen is missing.
        /// </summary>
        private async Task<bool> DeployAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            if (!await SetTroopRatioAsync(account, logger, instance, cancellationToken)) return false;
            var deploy = await MatchAsync(DeployButtonTemplate, DeployButtonArea, Threshold, instance, logger);
            if (!deploy.found)
            {
                logger.LogWarning($"[{account.AccountName}] Mystic Trial: Deploy button not found");
                return false;
            }
            await ClickCenterInRectAsync(instance, logger, deploy.rect);
            await Task.Delay(1500, cancellationToken);
            if ((await MatchAsync(CapacityPopupTemplate, CapacityPopupArea, Threshold, instance, logger)).found)
            {
                // squads not full: don't fight short of troops
                logger.LogWarning($"[{account.AccountName}] Mystic Trial: squads not full after Deploy, skipping this building");
                await ClickAsync(instance, logger, CloseCapacityPopupPoint);
                await Task.Delay(1500, cancellationToken);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Balance > 50% infantry, 20% cavalry, 30% archers (typed into the boxes) > Confirm.
        /// </summary>
        private async Task<bool> SetTroopRatioAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            var balance = await MatchAsync(BalanceButtonTemplate, BalanceButtonArea, Threshold, instance, logger);
            if (!balance.found)
            {
                logger.LogWarning($"[{account.AccountName}] Mystic Trial: deploy screen not shown");
                return false;
            }
            await ClickCenterInRectAsync(instance, logger, balance.rect);
            await Task.Delay(1500, cancellationToken);
            if (!(await MatchAsync(BalanceTitleTemplate, BalanceTitleArea, Threshold, instance, logger)).found)
            {
                logger.LogWarning($"[{account.AccountName}] Mystic Trial: Balance popup did not open");
                return false;
            }

            bool full = false;
            for (int attempt = 0; attempt < RatioAttempts && !full; attempt++)
            {
                foreach (var (name, box, percent) in Ratio)
                {
                    await ClickAsync(instance, logger, box);
                    await Task.Delay(1500, cancellationToken);
                    await TypeIntoFocusedFieldAsync(instance, logger, percent.ToString());
                    await Task.Delay(1000, cancellationToken);
                    await ClickAsync(instance, logger, NumberOkPoint);
                    await Task.Delay(1500, cancellationToken);
                    logger.LogInfo($"[{account.AccountName}] Mystic Trial: {name} {percent}%");
                }
                full = (await MatchAsync(BalanceFullTemplate, BalanceFullArea, Threshold, instance, logger)).found;
                if (!full) logger.LogWarning($"[{account.AccountName}] Mystic Trial: ratio total is not 100%, typing it again");
            }
            if (!full) return false;
            await ClickAsync(instance, logger, ConfirmBalancePoint);
            await Task.Delay(2000, cancellationToken);
            return true;
        }

        private enum BattleResult { Victory, Failed, StageScreen, Timeout }

        /// <summary>
        /// Waits for the Victory/Failed screen, or the stage screen (Challenge button) that Next opens at a new chapter.
        /// </summary>
        private async Task<BattleResult> WaitForResultAsync(int instance, LogService logger, CancellationToken cancellationToken)
        {
            for (int waited = 0; waited < BattleTimeoutSeconds; waited += 2)
            {
                await Task.Delay(2000, cancellationToken);
                if ((await MatchAsync(VictoryTemplate, ResultTitleArea, Threshold, instance, logger)).found) return BattleResult.Victory;
                if ((await MatchAsync(FailedTemplate, ResultTitleArea, Threshold, instance, logger)).found) return BattleResult.Failed;
                if ((await MatchAsync(ChallengeButtonTemplate, BottomButtonArea, Threshold, instance, logger)).found) return BattleResult.StageScreen;
            }
            return BattleResult.Timeout;
        }

        private async Task<bool> BackToMapAsync(LogService logger, int instance, CancellationToken cancellationToken)
        {
            for (int i = 0; i < 4; i++)
            {
                if ((await MatchAsync(MapTitleTemplate, TitleArea, Threshold, instance, logger)).found) return true;
                if (await CloseResultScreenAsync(instance, logger, cancellationToken)) continue;
                await ClickAsync(instance, logger, BackPoint);
                await Task.Delay(2500, cancellationToken);
            }
            return (await MatchAsync(MapTitleTemplate, TitleArea, Threshold, instance, logger)).found;
        }

        /// <summary>
        /// Closes a Victory/Failed screen ("Tap anywhere to close"). Only then: on the stage screen that spot is Challenge.
        /// </summary>
        private async Task<bool> CloseResultScreenAsync(int instance, LogService logger, CancellationToken cancellationToken)
        {
            if (!(await MatchAsync(VictoryTemplate, ResultTitleArea, Threshold, instance, logger)).found
                && !(await MatchAsync(FailedTemplate, ResultTitleArea, Threshold, instance, logger)).found)
                return false;
            await ClickAsync(instance, logger, CloseResultPoint);
            await Task.Delay(2000, cancellationToken);
            return true;
        }

        private async Task<int> CountTealAsync(Rectangle area, int instance, LogService logger)
        {
            var screenshot = await TakeScreenshotAsync(instance, logger);
            if (screenshot == null) return 0;
            using var image = Cv2.ImDecode(screenshot, ImreadModes.Color);
            using var region = new Mat(image, new Rect(area.X, area.Y, area.Width, area.Height));
            using var hsv = region.CvtColor(ColorConversionCodes.BGR2HSV);
            using var mask = hsv.InRange(new Scalar(80, 120, 150), new Scalar(100, 255, 255));
            return Cv2.CountNonZero(mask);
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
