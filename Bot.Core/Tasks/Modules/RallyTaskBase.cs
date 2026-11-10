using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.ImageDetection;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// Shared by the rally modes (Castle Battle, Bear Hunt): Alliance > War > Rally list with its green join "+",
    /// the deploy screen's team flags, the Marching tracker at the top left of the map, and template matching with
    /// templates from other task folders ("castlebattle/deploy-button.png").
    /// </summary>
    public abstract class RallyTaskBase : BaseTaskWithCommonPatterns
    {
        protected static readonly string DeployButtonTemplate = Path.Combine("castlebattle", "deploy-button.png");   // deploy screen: 1.00 there, <= 0.54 on War/Balance/map
        protected static readonly string AllianceMenuTemplate = Path.Combine("autojoinrally", "alliance-menu.png");
        private static readonly string WarTileTemplate = Path.Combine("autojoinrally", "war-tile.png");
        private static readonly string WarTitleTemplate = Path.Combine("autojoinrally", "war-title.png");

        protected static readonly Rectangle AllianceMenuArea = new Rectangle(480, 1160, 150, 110);
        private static readonly Rectangle WarTileArea = new Rectangle(0, 600, 360, 160);
        private static readonly Rectangle WarTitleArea = new Rectangle(60, 0, 200, 80);
        protected static readonly Rectangle DeployButtonArea = new Rectangle(400, 1160, 310, 100);

        // War > Rally list: the join "+" sits at the right of each row; green = can join (2,500 green pixels), grey = joined/full (0)
        private static readonly Rect JoinColumn = new Rect(560, 150, 160, 1000);
        // Marching tracker, top left of the map: "Marching 1/6", then one row per march ("Rallying" / target "X:650, Y:600" / ...)
        private static readonly Rectangle MarchCountArea = new Rectangle(175, 200, 80, 30);
        private static readonly Rectangle MarchRowArea = new Rectangle(58, 244, 190, 30);

        protected static readonly System.Drawing.Point BackPoint = new System.Drawing.Point(45, 40);
        protected static readonly System.Drawing.Point DeployPoint = new System.Drawing.Point(548, 1215);

        protected const double Threshold = 0.85;      // screen elements 1.00 present, <= 0.66 elsewhere
        private const int MaxScrolls = 3;
        // slow, short drags: a fast swipe flings the list past rows unjudged
        private static readonly System.Drawing.Point ListScrollFrom = new System.Drawing.Point(360, 950);
        private static readonly System.Drawing.Point ListScrollTo = new System.Drawing.Point(360, 650);

        #region March tracker

        /// <summary>What the Marching tracker says: busy/total march queues and the first row's text</summary>
        internal sealed record MarchInfo(int Busy, int Total, string FirstRow)
        {
            public bool HasFreeQueue => Total == 0 || Busy < Total;
        }

        private protected async Task<MarchInfo> ReadMarchTrackerAsync(int instance, LogService logger)
        {
            var screenshot = await TakeScreenshotAsync(instance, logger);
            return screenshot == null ? new MarchInfo(0, 0, "") : ReadMarchTracker(screenshot, logger);
        }

        /// <summary>OCR of the Marching tracker; 0/0 and "" when it isn't shown (no marches)</summary>
        internal static MarchInfo ReadMarchTracker(byte[] screenshot, LogService logger)
        {
            string count = ReadWhiteText(screenshot, MarchCountArea, "0123456789/", logger);
            string row = ReadWhiteText(screenshot, MarchRowArea, null, logger);
            var m = Regex.Match(count, @"(\d)\s*/\s*(\d)", RegexOptions.None, RegexTimeouts.Default);
            return m.Success
                ? new MarchInfo(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), row)
                : new MarchInfo(0, 0, row);
        }

        protected static string ReadWhiteText(byte[] screenshot, Rectangle area, string? whitelist, LogService logger) =>
            ReadMaskedText(screenshot, area, new Scalar(0, 0, 200), new Scalar(180, 60, 255), whitelist, logger);

        /// <summary>The text colour masked into white-on-black, then one line of OCR</summary>
        protected static string ReadMaskedText(byte[] screenshot, Rectangle area, Scalar low, Scalar high, string? whitelist, LogService logger,
            Tesseract.PageSegMode mode = Tesseract.PageSegMode.SingleLine)
        {
            byte[] masked;
            using (var image = Cv2.ImDecode(screenshot, ImreadModes.Color))
            {
                var roi = new Rect(area.X, area.Y, area.Width, area.Height).Intersect(new Rect(0, 0, image.Width, image.Height));
                using var region = new Mat(image, roi);
                using var hsv = region.CvtColor(ColorConversionCodes.BGR2HSV);
                using var mask = hsv.InRange(low, high);
                using var bgr = mask.CvtColor(ColorConversionCodes.GRAY2BGR);
                masked = bgr.ToBytes(".png");
            }
            var config = new OcrConfiguration { ScaleFactor = 2, PageSegMode = mode };
            if (whitelist != null) config.CharacterWhitelist = whitelist;
            using var ocr = new OcrService(logger, config);
            string text = ocr.ExtractTextFromScreenArea(masked, new Rectangle(0, 0, area.Width, area.Height)) ?? "";
            // single line: one space between words; a block keeps its line breaks
            return mode == Tesseract.PageSegMode.SingleLine
                ? Regex.Replace(text, @"\s+", " ", RegexOptions.None, RegexTimeouts.Default).Trim()
                : text.Trim();
        }

        #endregion

        #region War screen

        protected async Task<bool> OpenWarAsync(string character, int instance, LogService logger, CancellationToken cancellationToken)
        {
            var menu = await MatchAsync(AllianceMenuTemplate, AllianceMenuArea, Threshold, instance, logger);
            if (!menu.found)
            {
                logger.LogWarning($"[{character}] {Name}: Alliance button not found ({menu.confidence:F2})");
                return false;
            }
            await ClickCenterInRectAsync(instance, logger, menu.rect);
            await Task.Delay(2000, cancellationToken);

            var tile = await MatchAsync(WarTileTemplate, WarTileArea, Threshold, instance, logger);
            if (!tile.found)
            {
                logger.LogWarning($"[{character}] {Name}: War tile not found ({tile.confidence:F2})");
                await ClickAsync(instance, logger, BackPoint);
                await Task.Delay(1500, cancellationToken);
                return false;
            }
            await ClickCenterInRectAsync(instance, logger, tile.rect);
            for (int i = 0; i < 5; i++)
            {
                await Task.Delay(1000, cancellationToken);
                if (await IsOnWarAsync(instance, logger)) return true;
            }
            logger.LogWarning($"[{character}] {Name}: War screen did not open");
            return false;
        }

        protected async Task<bool> IsOnWarAsync(int instance, LogService logger) =>
            (await MatchAsync(WarTitleTemplate, WarTitleArea, Threshold, instance, logger)).found;

        /// <summary>War screen > Alliance screen > map</summary>
        protected async Task LeaveWarAsync(int instance, LogService logger, CancellationToken cancellationToken)
        {
            for (int i = 0; i < 2; i++)
            {
                await ClickAsync(instance, logger, BackPoint);
                await Task.Delay(1500, cancellationToken);
            }
        }

        private async Task BackToWarAsync(int instance, LogService logger, CancellationToken cancellationToken)
        {
            for (int i = 0; i < 3 && !await IsOnWarAsync(instance, logger); i++)
            {
                await ClickAsync(instance, logger, BackPoint);
                await Task.Delay(1500, cancellationToken);
            }
        }

        #endregion

        #region Join buttons

        /// <summary>A green join "+" and whether its row is a blue "Def. Lineup" (else a red attack "Rally")</summary>
        internal readonly record struct JoinButton(System.Drawing.Point Center, bool Defensive);

        /// <summary>
        /// Join buttons on screen, minus the rows at <paramref name="skipped"/> (screen y of "+"s already tried or passed over);
        /// when none are left, scrolls the list down slowly (full rallies show a grey "+") a few times. A scroll clears
        /// <paramref name="skipped"/>: the rows moved, so those positions now hold other rallies (2026-11-10: a fresh rally's "+"
        /// landed near a joined one's old spot after the scroll and the joins stopped at 2).
        /// </summary>
        private protected async Task<List<JoinButton>> FindJoinButtonsWithScrollAsync(int instance, LogService logger, CancellationToken cancellationToken, List<int>? skipped = null)
        {
            var buttons = NotSkipped(await FindJoinButtonsAsync(instance, logger), skipped);
            for (int i = 0; i < MaxScrolls && buttons.Count == 0; i++)
            {
                await SwipeAsync(instance, logger, ListScrollFrom, ListScrollTo, 1500);
                await Task.Delay(1000, cancellationToken);
                skipped?.Clear();
                buttons = NotSkipped(await FindJoinButtonsAsync(instance, logger), skipped);
            }
            return buttons;
        }

        private static List<JoinButton> NotSkipped(List<JoinButton> buttons, List<int>? skipped) =>
            skipped == null ? buttons : buttons.Where(b => !skipped.Exists(y => Math.Abs(y - b.Center.Y) < 40)).ToList();

        private async Task<List<JoinButton>> FindJoinButtonsAsync(int instance, LogService logger)
        {
            var screenshot = await TakeScreenshotAsync(instance, logger);
            return screenshot == null ? new List<JoinButton>() : FindJoinButtons(screenshot);
        }

        /// <summary>The green join "+" buttons on the War > Rally list, top to bottom (grey ones are skipped)</summary>
        internal static List<JoinButton> FindJoinButtons(byte[] screenshot)
        {
            var result = new List<JoinButton>();
            using var image = Cv2.ImDecode(screenshot, ImreadModes.Color);
            using var region = new Mat(image, JoinColumn.Intersect(new Rect(0, 0, image.Width, image.Height)));
            using var hsv = region.CvtColor(ColorConversionCodes.BGR2HSV);
            using var mask = hsv.InRange(new Scalar(40, 120, 120), new Scalar(80, 255, 255));
            using var labels = new Mat();
            using var stats = new Mat();
            using var centroids = new Mat();
            int count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids);
            for (int i = 1; i < count; i++)
            {
                int w = stats.At<int>(i, (int)ConnectedComponentsTypes.Width);
                int h = stats.At<int>(i, (int)ConnectedComponentsTypes.Height);
                int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
                // the button: 59x57, 2,506 green pixels
                if (area < 1800 || w is < 45 or > 75 || h is < 45 or > 75) continue;
                int x = stats.At<int>(i, (int)ConnectedComponentsTypes.Left) + w / 2 + JoinColumn.X;
                int y = stats.At<int>(i, (int)ConnectedComponentsTypes.Top) + h / 2 + JoinColumn.Y;
                result.Add(new JoinButton(new System.Drawing.Point(x, y), IsDefensiveRow(image, y)));
            }
            return result.OrderBy(b => b.Center.Y).ToList();
        }

        /// <summary>
        /// The row header above a join button (~170 px up): blue "Def. Lineup" (HSV hue 110, sat 134) or red "Rally"
        /// (hue 3, sat 159). Looks along x=100 between 120 and 260 px above the button for the first saturated pixel.
        /// </summary>
        private static bool IsDefensiveRow(Mat image, int plusY)
        {
            using var hsv = image.CvtColor(ColorConversionCodes.BGR2HSV);
            for (int y = plusY - 120; y >= Math.Max(0, plusY - 260); y--)
            {
                var px = hsv.At<Vec3b>(y, 100);
                if (px.Item1 < 100 || px.Item2 < 150) continue;
                if (px.Item0 is >= 95 and <= 125) return true;
                if (px.Item0 <= 12 || px.Item0 >= 168) return false;
            }
            return false;
        }

        /// <summary>"+" > team flag (its saved troops and ratio) > Deploy. True when the game went back to the War list.</summary>
        protected async Task<bool> JoinAsync(string character, System.Drawing.Point plus, int team, int instance, LogService logger, CancellationToken cancellationToken)
        {
            await ClickAsync(instance, logger, plus);
            await Task.Delay(2000, cancellationToken);
            if (!(await MatchAsync(DeployButtonTemplate, DeployButtonArea, Threshold, instance, logger)).found)
            {
                logger.LogWarning($"[{character}] {Name}: no deploy screen after the join button");
                await BackToWarAsync(instance, logger, cancellationToken);
                return false;
            }

            await ClickAsync(instance, logger, FlagPoint(team));
            await Task.Delay(1500, cancellationToken);
            await ClickAsync(instance, logger, DeployPoint);
            await Task.Delay(2500, cancellationToken);
            // the deploy screen closing is the success; the War list can take longer to show (bear hunt 2026-11-08: joined, but not back on War after 2.5 s)
            if (!(await MatchAsync(DeployButtonTemplate, DeployButtonArea, Threshold, instance, logger)).found)
            {
                logger.LogInfo($"[{character}] {Name}: joined a rally with team {team}");
                if (!await IsOnWarAsync(instance, logger))
                {
                    await Task.Delay(2000, cancellationToken);
                    await BackToWarAsync(instance, logger, cancellationToken);
                }
                return true;
            }
            logger.LogWarning($"[{character}] {Name}: Deploy stayed open (no free march?)");
            await BackToWarAsync(instance, logger, cancellationToken);
            return false;
        }

        /// <summary>Team flags 1-8 along the top of the deploy screen</summary>
        internal static System.Drawing.Point FlagPoint(int team) => new System.Drawing.Point(63 + 74 * (Math.Clamp(team, 1, 8) - 1), 122);

        #endregion

        protected async Task<(bool found, Rectangle rect, double confidence)> MatchAsync(
            string templateName, Rectangle area, double threshold, int instance, LogService logger)
        {
            var screenshot = await TakeScreenshotAsync(instance, logger);
            if (screenshot == null) return (false, Rectangle.Empty, 0);

            if (_templateMatcher == null) _templateMatcher = new UnifiedTemplateMatchingService(logger);
            // names with a folder ("autoheal/heal.png") come from another task's folder
            var path = templateName.Contains(Path.DirectorySeparatorChar)
                ? Path.Combine(AppContext.BaseDirectory, "templates", "images", templateName)
                : Path.Combine(ImageTemplateFolder, templateName);
            var result = _templateMatcher.MatchTemplate(
                screenshot, path, instance,
                threshold: threshold,
                scales: new[] { 1.0 },
                verboseLogging: false,
                searchArea: area);
            return (result.found, result.matchRect, result.confidence);
        }

        /// <summary>True when the task folder holds this template (Bear Hunt's are captured on the first live event)</summary>
        protected bool HasTemplate(string templateName) =>
            File.Exists(templateName.Contains(Path.DirectorySeparatorChar)
                ? Path.Combine(AppContext.BaseDirectory, "templates", "images", templateName)
                : Path.Combine(ImageTemplateFolder, templateName));
    }
}
