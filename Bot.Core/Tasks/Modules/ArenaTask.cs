using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.ImageDetection;
using Bot.Core.Services;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// Side panel > Daily tab > Arena (Arena of Glory): uses the day's challenges (5) on the weakest opponent of the
    /// Challenge List, with a Free Refresh when nobody is weaker. Quick Deploy > Fight, then "Tap anywhere to exit".
    /// Optionally only from a set UTC time on (<see cref="ArenaSettings"/>), late in the day.
    /// </summary>
    public class ArenaTask : BaseTaskWithCommonPatterns, IScheduledTask
    {
        public override TaskType TaskType => TaskType.Arena;
        public override string Name => "Arena";

        private const string PanelOpenArrowTemplate = "panel-open-arrow.png";
        private const string PanelCloseArrowTemplate = "panel-close-arrow.png";
        private const string RowIconTemplate = "arena-row-icon.png";
        private const string ArenaTitleTemplate = "arena-title.png";               // "Arena of Glory"
        private const string ChallengeListTemplate = "challenge-list-title.png";
        private const string SquadSettingsTemplate = "squad-settings-title.png";
        private const string FreeRefreshTemplate = "free-refresh.png";            // a paid refresh shows a price instead
        private const string TapToExitTemplate = "tap-to-exit.png";               // bottom of the Victory/Failed screen
        private const string VictoryTemplate = "victory-title.png";

        private static readonly Rectangle PanelOpenArrowArea = new Rectangle(0, 420, 60, 260);
        private static readonly Rectangle PanelCloseArrowArea = new Rectangle(400, 420, 120, 260);
        private static readonly Rectangle PanelListArea = new Rectangle(0, 300, 120, 520);
        private static readonly Rectangle TitleArea = new Rectangle(80, 0, 260, 80);
        private static readonly Rectangle ChallengeListArea = new Rectangle(200, 150, 320, 90);
        private static readonly Rectangle FreeRefreshArea = new Rectangle(200, 1000, 320, 100);
        private static readonly Rectangle TapToExitArea = new Rectangle(200, 1190, 320, 70);
        private static readonly Rectangle VictoryArea = new Rectangle(180, 320, 360, 120);
        // "Daily challenges: N", up to the + button at x 450. The whole line: a lone digit doesn't read, and the colon alone reads as a "1"
        private static readonly Rectangle ChallengesLeftArea = new Rectangle(200, 950, 248, 40);
        private static readonly System.Drawing.Point DailyTabPoint = new System.Drawing.Point(370, 268);
        private static readonly System.Drawing.Point BackPoint = new System.Drawing.Point(48, 40);
        private static readonly System.Drawing.Point ChallengePoint = new System.Drawing.Point(360, 1215);
        private static readonly System.Drawing.Point CloseListPoint = new System.Drawing.Point(667, 191);
        private static readonly System.Drawing.Point FreeRefreshPoint = new System.Drawing.Point(360, 1047);
        private static readonly System.Drawing.Point QuickDeployPoint = new System.Drawing.Point(190, 1200);
        private static readonly System.Drawing.Point FightPoint = new System.Drawing.Point(528, 1200);
        private static readonly System.Drawing.Point ExitResultPoint = new System.Drawing.Point(360, 1225);
        // Challenge List rows: swords button at x 624, rows 133 px apart; the power value sits left of the points
        private const int FirstRowY = 360, RowStep = 133, Rows = 5, SwordsX = 624;
        private const int PowerX = 180, PowerWidth = 105, PowerHalfHeight = 18;
        private const int PanelRowTitleDx = 180;
        private const int MaxChallenges = 10;            // 5 a day; a cap in case the count can't be read down
        private const int BattleTimeoutSeconds = 120;
        private const double PanelArrowThreshold = 0.88;
        private const double PanelRowThreshold = 0.93;   // row icon 1.00, half-scrolled 0.85, other rows <= 0.82
        private const double Threshold = 0.90;           // screen titles/buttons 1.00 present, <= 0.58 elsewhere

        public void ClearSchedule(string accountKey) => DailyGate.Clear(TaskType, accountKey);

        public DateTime? GetNextRunUtc(string accountKey, AccountSettings account)
        {
            var doneUntil = DailyGate.NextRunUtc(TaskType, accountKey);
            var runAfter = account.ArenaSettings?.RunAfterTime();
            if (runAfter == null) return doneUntil;
            var start = (doneUntil ?? DateTime.UtcNow).Date + runAfter.Value;
            return doneUntil == null && DateTime.UtcNow >= start ? null : start;
        }

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            try
            {
                string accountKey = Schedule.AccountKey(AccountDetectionTask.GetCachedAccountId(account.InstanceNumber), account);
                string? skip = SkipReason(account, accountKey);
                if (skip != null) return TaskExecutionDetails.SkippedBecause(skip);

                var locator = new LocatorService(logger, account, TaskType);
                int instance = account.InstanceNumber;
                await locator.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

                var opened = await OpenArenaAsync(account, logger, instance, cancellationToken);
                if (opened != true)
                {
                    // a hidden row means today's challenges are used; a screen that failed to open is retried next cycle
                    if (opened == null) DailyGate.MarkDone(TaskType, accountKey);
                    await locator.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);
                    return opened == null
                        ? TaskExecutionDetails.Succeeded("Arena row hidden, nothing to do")
                        : TaskExecutionDetails.Failed("Arena screen did not open");
                }

                var (battles, won, finished) = await FightChallengesAsync(account, logger, instance, cancellationToken);
                if (finished) DailyGate.MarkDone(TaskType, accountKey);
                await LeaveArenaAsync(instance, logger, cancellationToken);
                await locator.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

                string result = $"{battles} battle(s), {won} won";
                logger.LogInfo($"[{account.AccountName}] Arena done: {result}");
                return finished ? TaskExecutionDetails.Succeeded(result) : TaskExecutionDetails.Failed($"Stopped early: {result}");
            }
            catch (OperationCanceledException)
            {
                return TaskExecutionDetails.Failed("Task was cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error in Arena: {ex.Message}");
                return TaskExecutionDetails.Failed(ex.Message);
            }
        }

        private string? SkipReason(AccountSettings account, string accountKey)
        {
            if (DailyGate.DoneToday(TaskType, accountKey)) return "Already done today";
            var runAfter = account.ArenaSettings?.RunAfterTime();
            if (runAfter != null && DateTime.UtcNow.TimeOfDay < runAfter.Value && !RunByHand.Value)
                return $"Runs after {runAfter.Value:hh\\:mm} UTC";
            return null;
        }

        /// <summary>
        /// Side panel > Daily tab > Arena row. True = Arena of Glory open, null = row hidden (done today), false = failed.
        /// </summary>
        private async Task<bool?> OpenArenaAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            if (!(await MatchAsync(PanelCloseArrowTemplate, PanelCloseArrowArea, PanelArrowThreshold, instance, logger)).found)
            {
                var arrow = await MatchAsync(PanelOpenArrowTemplate, PanelOpenArrowArea, PanelArrowThreshold, instance, logger);
                if (!arrow.found)
                {
                    logger.LogWarning($"[{account.AccountName}] Arena: side panel arrow not found");
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
                logger.LogInfo($"[{account.AccountName}] Arena: row hidden, nothing to do");
                var close = await MatchAsync(PanelCloseArrowTemplate, PanelCloseArrowArea, PanelArrowThreshold, instance, logger);
                if (close.found) await ClickCenterInRectAsync(instance, logger, close.rect);
                await Task.Delay(1000, cancellationToken);
                return null;
            }

            await ClickAsync(instance, logger, new System.Drawing.Point(row.rect.X + row.rect.Width / 2 + PanelRowTitleDx, row.rect.Y + row.rect.Height / 2));
            if (await WaitForAsync(ArenaTitleTemplate, TitleArea, 4, instance, logger, cancellationToken)) return true;
            logger.LogWarning($"[{account.AccountName}] Arena screen did not open");
            return false;
        }

        /// <summary>
        /// Challenge after challenge on the weakest opponent until the count reads 0.
        /// finished: the count reached 0; false when a screen went missing.
        /// </summary>
        private async Task<(int battles, int won, bool finished)> FightChallengesAsync(
            AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            int battles = 0, won = 0;
            int? left = null;
            while (battles < MaxChallenges)
            {
                if (!await OpenChallengeListAsync(instance, logger, cancellationToken))
                {
                    logger.LogWarning($"[{account.AccountName}] Arena: Challenge List did not open");
                    return (battles, won, false);
                }
                left = AfterBattle(left, await ReadChallengesLeftAsync(instance, logger));
                logger.LogInfo($"[{account.AccountName}] Arena: {left?.ToString() ?? "?"} challenge(s) left");
                if (left == 0) return (battles, won, true);
                if (left == null) return (battles, won, false);   // never tap a swords button that might cost gems

                var target = await PickOpponentAsync(account, logger, instance, cancellationToken);
                if (target == null) return (battles, won, false);

                var result = await FightAsync(account, logger, instance, target.Value, cancellationToken);
                if (result == null) return (battles, won, false);
                battles++;
                if (result == true) won++;
            }
            return (battles, won, false);
        }

        /// <summary>
        /// Each battle uses a challenge, so the count can only go down: a higher read is an OCR slip ("12" for 2).
        /// </summary>
        internal static int? AfterBattle(int? before, int? read)
        {
            if (before == null || read == null) return read;
            return Math.Min(read.Value, Math.Max(before.Value - 1, 0));
        }

        /// <summary>
        /// The weakest opponent's row (0-4). Nobody weaker than us: one Free Refresh first, when it's free.
        /// </summary>
        private async Task<int?> PickOpponentAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            var opponents = await ReadOpponentsAsync(instance, logger);
            if (!opponents.Any(o => o.Weaker)
                && (await MatchAsync(FreeRefreshTemplate, FreeRefreshArea, Threshold, instance, logger)).found)
            {
                logger.LogInfo($"[{account.AccountName}] Arena: nobody weaker, Free Refresh");
                await ClickAsync(instance, logger, FreeRefreshPoint);
                await Task.Delay(2000, cancellationToken);
                opponents = await ReadOpponentsAsync(instance, logger);
            }

            var weakest = opponents.Where(o => o.Power != null).OrderBy(o => o.Power).FirstOrDefault();
            if (weakest == null)
            {
                logger.LogWarning($"[{account.AccountName}] Arena: could not read the opponents' power");
                return null;
            }
            logger.LogInfo($"[{account.AccountName}] Arena: powers {string.Join(", ", opponents.Select(o => o.Text))}; fighting row {weakest.Row + 1} ({weakest.Text})");
            return weakest.Row;
        }

        /// <summary>
        /// Swords > Squad Settings > Quick Deploy > Fight > result. True = won, false = lost, null = a screen went missing.
        /// </summary>
        private async Task<bool?> FightAsync(AccountSettings account, LogService logger, int instance, int row, CancellationToken cancellationToken)
        {
            await ClickAsync(instance, logger, new System.Drawing.Point(SwordsX, FirstRowY + row * RowStep));
            if (!await WaitForAsync(SquadSettingsTemplate, TitleArea, 4, instance, logger, cancellationToken))
            {
                logger.LogWarning($"[{account.AccountName}] Arena: Squad Settings did not open");
                return null;
            }
            await ClickAsync(instance, logger, QuickDeployPoint);
            await Task.Delay(1500, cancellationToken);
            await ClickAsync(instance, logger, FightPoint);

            for (int waited = 0; waited < BattleTimeoutSeconds; waited += 2)
            {
                await Task.Delay(2000, cancellationToken);
                if ((await MatchAsync(TapToExitTemplate, TapToExitArea, Threshold, instance, logger)).found)
                {
                    bool victory = (await MatchAsync(VictoryTemplate, VictoryArea, Threshold, instance, logger)).found;
                    logger.LogInfo($"[{account.AccountName}] Arena: {(victory ? "won" : "lost")}");
                    await ClickAsync(instance, logger, ExitResultPoint);
                    await Task.Delay(2500, cancellationToken);
                    return victory;
                }
                if ((await MatchAsync(ChallengeListTemplate, ChallengeListArea, Threshold, instance, logger)).found)
                {
                    logger.LogInfo($"[{account.AccountName}] Arena: back on the Challenge List, result not seen");
                    return false;
                }
            }
            logger.LogWarning($"[{account.AccountName}] Arena: no battle result after {BattleTimeoutSeconds}s");
            return null;
        }

        /// <summary>
        /// The Challenge List, from the list itself or from Arena of Glory (a first-visit tutorial can swallow a tap).
        /// </summary>
        private async Task<bool> OpenChallengeListAsync(int instance, LogService logger, CancellationToken cancellationToken)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if ((await MatchAsync(ChallengeListTemplate, ChallengeListArea, Threshold, instance, logger)).found) return true;
                if ((await MatchAsync(ArenaTitleTemplate, TitleArea, Threshold, instance, logger)).found)
                    await ClickAsync(instance, logger, ChallengePoint);
                await Task.Delay(2500, cancellationToken);
            }
            return (await MatchAsync(ChallengeListTemplate, ChallengeListArea, Threshold, instance, logger)).found;
        }

        private async Task LeaveArenaAsync(int instance, LogService logger, CancellationToken cancellationToken)
        {
            if ((await MatchAsync(ChallengeListTemplate, ChallengeListArea, Threshold, instance, logger)).found)
            {
                await ClickAsync(instance, logger, CloseListPoint);
                await Task.Delay(1500, cancellationToken);
            }
            if ((await MatchAsync(ArenaTitleTemplate, TitleArea, Threshold, instance, logger)).found)
            {
                await ClickAsync(instance, logger, BackPoint);
                await Task.Delay(2000, cancellationToken);
            }
        }

        internal sealed record Opponent(int Row, string Text, long? Power, bool Weaker);

        private async Task<List<Opponent>> ReadOpponentsAsync(int instance, LogService logger)
        {
            var screenshot = await TakeScreenshotAsync(instance, logger);
            return screenshot == null ? new List<Opponent>() : ReadOpponents(screenshot, logger);
        }

        /// <summary>
        /// Power of the 5 Challenge List opponents: green when weaker than us, red when stronger.
        /// </summary>
        internal static List<Opponent> ReadOpponents(byte[] screenshot, LogService logger)
        {
            var opponents = new List<Opponent>();
            using var image = Cv2.ImDecode(screenshot, ImreadModes.Color);
            using var ocr = new OcrService(logger, new OcrConfiguration
            {
                ScaleFactor = 3,
                CharacterWhitelist = "0123456789.,KMB",
                PageSegMode = Tesseract.PageSegMode.SingleLine
            });
            for (int row = 0; row < Rows; row++)
            {
                var rect = new Rect(PowerX, FirstRowY + row * RowStep - PowerHalfHeight, PowerWidth, 2 * PowerHalfHeight);
                using var region = new Mat(image, rect);
                using var hsv = region.CvtColor(ColorConversionCodes.BGR2HSV);
                using var text = hsv.InRange(new Scalar(0, 110, 110), new Scalar(180, 255, 255));  // the coloured digits; the beige box is pale
                using var green = hsv.InRange(new Scalar(60, 110, 110), new Scalar(90, 255, 255));  // hue 76; red is hue 0
                bool weaker = Cv2.CountNonZero(green) * 2 > Cv2.CountNonZero(text);

                using var bgr = text.CvtColor(ColorConversionCodes.GRAY2BGR);
                string read = (ocr.ExtractTextFromScreenArea(bgr.ToBytes(".png"), new Rectangle(0, 0, rect.Width, rect.Height)) ?? "").Trim();
                opponents.Add(new Opponent(row, read, ParsePower(read), weaker));
            }
            return opponents;
        }

        /// <summary>
        /// "21.4M" = 21,400,000, "166,500" = 166,500, "850K", "1.2B"; null when it doesn't read as a power.
        /// M and B values always show one decimal, so a dot the OCR dropped ("95M" for 9.5M) is put back.
        /// </summary>
        internal static long? ParsePower(string text)
        {
            var match = Regex.Match(text.Replace(" ", ""), @"^(\d[\d.,]*)([KMB]?)$", RegexOptions.None, RegexTimeouts.Default);
            if (!match.Success) return null;
            string number = match.Groups[1].Value;
            string suffix = match.Groups[2].Value;
            double multiplier = suffix switch { "K" => 1e3, "M" => 1e6, "B" => 1e9, _ => 1 };
            return double.TryParse(DecimalNumber(number, suffix), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
                ? (long)Math.Round(value * multiplier)
                : null;
        }

        private static string DecimalNumber(string number, string suffix)
        {
            if (suffix.Length == 0) return number.Replace(",", "").Replace(".", "");  // thousands separators
            number = number.Replace(",", ".");
            bool droppedDot = suffix is "M" or "B" && !number.Contains('.') && number.Length >= 2;
            return droppedDot ? number[..^1] + "." + number[^1..] : number;
        }

        private async Task<int?> ReadChallengesLeftAsync(int instance, LogService logger)
        {
            var screenshot = await TakeScreenshotAsync(instance, logger);
            return screenshot == null ? null : ReadChallengesLeft(screenshot, logger);
        }

        /// <summary>The N of "Daily challenges: N" (dark text) under the Challenge List.</summary>
        internal static int? ReadChallengesLeft(byte[] screenshot, LogService logger)
        {
            using var image = Cv2.ImDecode(screenshot, ImreadModes.Color);
            using var region = new Mat(image, new Rect(ChallengesLeftArea.X, ChallengesLeftArea.Y, ChallengesLeftArea.Width, ChallengesLeftArea.Height));
            using var hsv = region.CvtColor(ColorConversionCodes.BGR2HSV);
            using var mask = hsv.InRange(new Scalar(0, 0, 0), new Scalar(180, 255, 120));
            using var bgr = mask.CvtColor(ColorConversionCodes.GRAY2BGR);
            using var ocr = new OcrService(logger, new OcrConfiguration
            {
                ScaleFactor = 2,
                CharacterWhitelist = "Dailychenges:0123456789",
                PageSegMode = Tesseract.PageSegMode.SingleLine
            });
            string read = ocr.ExtractTextFromScreenArea(bgr.ToBytes(".png"), new Rectangle(0, 0, ChallengesLeftArea.Width, ChallengesLeftArea.Height)) ?? "";
            var match = Regex.Match(read, @":\s*(\d{1,2})\s*$", RegexOptions.None, RegexTimeouts.Default);
            return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        }

        private async Task<bool> WaitForAsync(string template, Rectangle area, int checks, int instance, LogService logger, CancellationToken cancellationToken)
        {
            for (int i = 0; i < checks; i++)
            {
                await Task.Delay(i == 0 ? 1500 : 2000, cancellationToken);
                if ((await MatchAsync(template, area, Threshold, instance, logger)).found) return true;
            }
            return false;
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
