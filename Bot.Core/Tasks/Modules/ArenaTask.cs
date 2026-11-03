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
        // "Daily challenges: N", up to the + button at x 446. The whole line: a lone digit doesn't read, and the colon alone reads as a "1"
        private static readonly Rectangle ChallengesLeftArea = new Rectangle(200, 950, 242, 40);
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
        private const int NpcPowerDy = 30;               // NPC rows: power at y 390 instead of 360
        private const int MinPowerPixels = 150;          // coloured pixels: a power value 595-1085, a name or empty spot 0
        private const int ListBottomY = 930;
        private static readonly (float Scale, Tesseract.PageSegMode Psm)[] PowerOcrModes =
        {
            (3, Tesseract.PageSegMode.SingleLine), (4, Tesseract.PageSegMode.SingleLine), (2, Tesseract.PageSegMode.SingleLine)
        };
        private const int GlyphGap = 8, MinGlyphPixels = 3;   // gaps of 6 and 10 read all test screenshots, 3 not; a dot is 16 px
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
            var ocrs = PowerOcrModes.Select(mode => new OcrService(logger, new OcrConfiguration
            {
                ScaleFactor = mode.Scale,
                CharacterWhitelist = "0123456789.,KMB",
                PageSegMode = mode.Psm
            })).ToList();
            try
            {
                for (int row = 0; row < Rows; row++)
                    opponents.Add(ReadOpponent(image, row, ocrs));
            }
            finally
            {
                ocrs.ForEach(ocr => ocr.Dispose());
            }
            return opponents;
        }

        private static Opponent ReadOpponent(Mat image, int row, List<OcrService> ocrs)
        {
            int y = FirstRowY + row * RowStep;
            using var hsv = PowerBoxHsv(image, y);
            using var text = ColouredText(hsv);
            if (Cv2.CountNonZero(text) < MinPowerPixels)
            {
                // NPC opponents ("Common Guardsman") have no server line: the power sits a line lower
                using var lower = PowerBoxHsv(image, y + NpcPowerDy);
                using var lowerText = ColouredText(lower);
                if (Cv2.CountNonZero(lowerText) > Cv2.CountNonZero(text))
                {
                    lower.CopyTo(hsv);
                    lowerText.CopyTo(text);
                }
            }
            using var green = hsv.InRange(new Scalar(60, 110, 110), new Scalar(90, 255, 255));  // hue 76; red is hue 0
            bool weaker = Cv2.CountNonZero(green) * 2 > Cv2.CountNonZero(text);

            using var spread = SpreadGlyphs(text, out bool[] separators);
            using var bgr = spread.CvtColor(ColorConversionCodes.GRAY2BGR);
            byte[] png = bgr.ToBytes(".png");
            string read = "";
            foreach (var ocr in ocrs)
            {
                // another scale when a read doesn't parse
                read = PlaceSeparators((ocr.ExtractTextFromScreenArea(png, new Rectangle(0, 0, spread.Width, spread.Height)) ?? "").Trim(), separators);
                var power = ParsePower(read);
                if (power != null) return new Opponent(row, read, power, weaker);
            }
            return new Opponent(row, read, null, weaker);
        }

        /// <summary>
        /// The glyphs of the mask redrawn with a gap between each: the font sets "1", "." and "M" so tight that
        /// Tesseract read "27.1M" as "27." or "27M". separators: per glyph, left to right, whether it is a dot or comma.
        /// </summary>
        internal static Mat SpreadGlyphs(Mat mask, out bool[] separators)
        {
            using var labels = new Mat();
            using var stats = new Mat();
            using var centroids = new Mat();
            int count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids, PixelConnectivity.Connectivity4);
            var glyphs = Enumerable.Range(1, count - 1)
                .Select(i => (Label: i, Box: new Rect(stats.At<int>(i, 0), stats.At<int>(i, 1), stats.At<int>(i, 2), stats.At<int>(i, 3))))
                .Where(g => stats.At<int>(g.Label, 4) >= MinGlyphPixels)
                .OrderBy(g => g.Box.X)
                .ToList();
            int tallest = glyphs.Count == 0 ? 0 : glyphs.Max(g => g.Box.Height);
            separators = glyphs.Select(g => g.Box.Height * 2 < tallest).ToArray();   // a dot is 5 px high, a digit 19

            var spread = new Mat(mask.Rows, glyphs.Sum(g => g.Box.Width) + GlyphGap * (glyphs.Count + 1), MatType.CV_8UC1, Scalar.Black);
            int x = GlyphGap;
            foreach (var (label, box) in glyphs)
            {
                using var glyph = new Mat();
                Cv2.Compare(labels, label, glyph, CmpTypes.EQ);
                using var source = new Mat(glyph, box);
                using var target = new Mat(spread, new Rect(x, box.Y, box.Width, box.Height));
                source.CopyTo(target);
                x += box.Width + GlyphGap;
            }
            return spread;
        }

        /// <summary>
        /// Tesseract skips a lone dot ("214M" for 21.4M). When it read one character per glyph that isn't a dot or
        /// comma, the separators go back where the image has them: a decimal dot before a K/M/B, else a thousands comma.
        /// </summary>
        internal static string PlaceSeparators(string read, bool[] separators)
        {
            string chars = new string(read.Where(c => c != '.' && c != ',' && c != ' ').ToArray());
            if (chars.Length == 0 || chars.Length != separators.Count(s => !s)) return read;
            char separator = char.IsLetter(chars[^1]) ? '.' : ',';
            int next = 0;
            return string.Concat(separators.Select(s => s ? separator : chars[next++]));
        }

        /// <summary>The power box centred on y, cut off at the bottom of the list (the 5th row is half hidden).</summary>
        private static Mat PowerBoxHsv(Mat image, int y)
        {
            int top = y - PowerHalfHeight;
            int height = Math.Min(2 * PowerHalfHeight, ListBottomY - top);
            using var region = new Mat(image, new Rect(PowerX, top, PowerWidth, height));
            return region.CvtColor(ColorConversionCodes.BGR2HSV);
        }

        /// <summary>The coloured digits (green or red) in white on black; the beige box and the grey names are pale.</summary>
        private static Mat ColouredText(Mat hsv) => hsv.InRange(new Scalar(0, 110, 110), new Scalar(180, 255, 255));

        /// <summary>
        /// "21.4M" = 21,400,000, "166,500" = 166,500, "850K", "1.2B"; null when it doesn't read as a power.
        /// A dot the OCR dropped is put back from the image (<see cref="PlaceSeparators"/>), not guessed here:
        /// guessing turned "27M" (27.1M with the 1 lost) into 2.7M, the weakest opponent on the list.
        /// </summary>
        internal static long? ParsePower(string text)
        {
            var match = Regex.Match(text.Replace(" ", ""), @"^(\d[\d.,]*)([KMB]?)$", RegexOptions.None, RegexTimeouts.Default);
            if (!match.Success) return null;
            string number = match.Groups[1].Value;
            string suffix = match.Groups[2].Value;
            // "29." is a cut-off 29.xM: without a suffix only thousands are a power, grouped ("166,500") or with the comma lost ("60000")
            if (suffix.Length == 0 && !Regex.IsMatch(number, @"^(\d{1,3}([.,]\d{3})+|\d{4,})$", RegexOptions.None, RegexTimeouts.Default)) return null;
            double multiplier = suffix switch { "K" => 1e3, "M" => 1e6, "B" => 1e9, _ => 1 };
            // without a suffix the separators are thousands; with one, the decimal point
            number = suffix.Length == 0 ? number.Replace(",", "").Replace(".", "") : number.Replace(",", ".");
            return double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
                ? (long)Math.Round(value * multiplier)
                : null;
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
            // a letter after the number is the + button's edge ("Dailychallenges:1 l")
            var match = Regex.Match(read, @":\s*(\d{1,2})(?!\d)[^\d]*$", RegexOptions.None, RegexTimeouts.Default);
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
