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
    /// Paw button (base view, right side above the mail) > Pet Skill: uses the gathering skills that are ready.
    /// Comforting Embrace (+50 stamina) and Scent Mastery (pet food) have a 23 h cooldown, Gift of the King 1d 11h,
    /// so the task runs every cycle and only taps Use when the button is green. Other skills are left alone.
    /// The cooldown overlay changes an icon too much to match it in both states, so the task taps every icon with the
    /// green hammer badge (gathering skills; the battle skills have a red one) and identifies the skill by its name.
    /// </summary>
    public class PetSkillTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.PetSkill;
        public override string Name => "Pet Skill";

        private const string PawButtonTemplate = "paw-button.png";
        private const string TitleTemplate = "pet-skill-title.png";
        private const string BadgeTemplate = "gathering-badge.png";
        // Skill names without the level, in the description box once the icon is selected
        private static readonly (string Template, string Name)[] Skills =
        {
            ("name-comforting-embrace.png", "Comforting Embrace"),
            ("name-scent-mastery.png", "Scent Mastery"),
            ("name-gift-of-the-king.png", "Gift of the King"),
        };
        private static readonly Rectangle PawButtonArea = new Rectangle(600, 780, 120, 140);
        private static readonly Rectangle TitleArea = new Rectangle(200, 110, 320, 90);
        private static readonly Rectangle GridArea = new Rectangle(60, 230, 600, 530);
        private static readonly Rectangle NameArea = new Rectangle(40, 780, 640, 60);
        private static readonly Rectangle UseButtonArea = new Rectangle(230, 1050, 260, 75);
        private static readonly System.Drawing.Point BadgeToIconCenter = new System.Drawing.Point(62, 61);
        private static readonly System.Drawing.Point UsePoint = new System.Drawing.Point(360, 1088);
        private static readonly System.Drawing.Point CloseRewardsPoint = new System.Drawing.Point(360, 1180); // "Tap anywhere to exit", clear of Beast Cage
        private static readonly System.Drawing.Point ClosePanelPoint = new System.Drawing.Point(668, 153);
        private const double Threshold = 0.80;       // paw, title, names: 1.00 present, <= 0.52 elsewhere
        private const double BadgeThreshold = 0.75;  // green hammer: 0.86-1.00 (selected cell lowest), < 0.60 elsewhere
        private const int MaxBadges = 12;
        // Green Use button: ~18200 green pixels when ready; 0 on cooldown ("On cooldown: hh:mm:ss" in red) or with nothing selected
        private const int UseButtonMinGreenPixels = 5000;

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            try
            {
                var locator = new LocatorService(logger, account, TaskType);
                int instance = account.InstanceNumber;
                await locator.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

                if (!await OpenPanelAsync(account, logger, instance, cancellationToken))
                {
                    await locator.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);
                    return TaskExecutionDetails.Failed("Pet Skill panel did not open");
                }

                var icons = await FindGatheringIconsAsync(instance, logger);
                logger.LogInfo($"[{account.AccountName}] Pet Skill: {icons.Count} gathering skill(s) in the grid");

                var used = new List<string>();
                foreach (var icon in icons)
                {
                    if (!(await MatchAsync(TitleTemplate, TitleArea, Threshold, instance, logger)).found
                        && !await OpenPanelAsync(account, logger, instance, cancellationToken))
                        break;

                    await ClickAsync(instance, logger, icon);
                    await Task.Delay(1500, cancellationToken);

                    string? name = await IdentifySkillAsync(instance, logger);
                    if (name == null) continue;

                    int green = await CountUseButtonGreenAsync(instance, logger);
                    if (green < UseButtonMinGreenPixels)
                    {
                        logger.LogInfo($"[{account.AccountName}] Pet Skill: {name} on cooldown");
                        continue;
                    }
                    await ClickAsync(instance, logger, UsePoint);
                    await Task.Delay(2000, cancellationToken);
                    await ClickAsync(instance, logger, CloseRewardsPoint);
                    await Task.Delay(1500, cancellationToken);
                    used.Add(name);
                    logger.LogInfo($"[{account.AccountName}] Pet Skill: used {name}");
                }

                if ((await MatchAsync(TitleTemplate, TitleArea, Threshold, instance, logger)).found)
                {
                    await ClickAsync(instance, logger, ClosePanelPoint);
                    await Task.Delay(1500, cancellationToken);
                }
                await locator.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

                string result = used.Count > 0 ? $"Used {string.Join(", ", used)}" : "No skill ready";
                logger.LogInfo($"[{account.AccountName}] Pet Skill done: {result}");
                return TaskExecutionDetails.Succeeded(result);
            }
            catch (OperationCanceledException)
            {
                return TaskExecutionDetails.Failed("Task was cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error in Pet Skill: {ex.Message}");
                return TaskExecutionDetails.Failed(ex.Message);
            }
        }

        private async Task<bool> OpenPanelAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            var paw = await MatchAsync(PawButtonTemplate, PawButtonArea, Threshold, instance, logger);
            if (!paw.found)
            {
                logger.LogWarning($"[{account.AccountName}] Pet Skill: paw button not found");
                return false;
            }
            await ClickCenterInRectAsync(instance, logger, paw.rect);
            await Task.Delay(2000, cancellationToken);
            return (await MatchAsync(TitleTemplate, TitleArea, Threshold, instance, logger)).found;
        }

        /// <summary>Centres of the icons with the green hammer badge, row by row.</summary>
        private async Task<List<System.Drawing.Point>> FindGatheringIconsAsync(int instance, LogService logger)
        {
            var icons = new List<System.Drawing.Point>();
            var screenshot = await TakeScreenshotAsync(instance, logger);
            if (screenshot == null) return icons;

            using var image = Cv2.ImDecode(screenshot, ImreadModes.Color);
            using var grid = new Mat(image, new Rect(GridArea.X, GridArea.Y, GridArea.Width, GridArea.Height));
            using var badge = Cv2.ImRead(Path.Combine(ImageTemplateFolder, BadgeTemplate), ImreadModes.Color);
            using var result = new Mat();
            Cv2.MatchTemplate(grid, badge, result, TemplateMatchModes.CCoeffNormed);

            for (int i = 0; i < MaxBadges; i++)
            {
                Cv2.MinMaxLoc(result, out _, out double max, out _, out OpenCvSharp.Point loc);
                if (max < BadgeThreshold) break;
                icons.Add(new System.Drawing.Point(GridArea.X + loc.X + BadgeToIconCenter.X, GridArea.Y + loc.Y + BadgeToIconCenter.Y));
                // Blank this badge so the next best is another one
                Cv2.Rectangle(result, new Rect(loc.X - 30, loc.Y - 30, 60, 60), Scalar.All(-1), -1);
            }
            return icons.OrderBy(p => p.Y / 50).ThenBy(p => p.X).ToList();
        }

        private async Task<string?> IdentifySkillAsync(int instance, LogService logger)
        {
            foreach (var (template, name) in Skills)
            {
                if ((await MatchAsync(template, NameArea, Threshold, instance, logger)).found)
                    return name;
            }
            return null;
        }

        private async Task<int> CountUseButtonGreenAsync(int instance, LogService logger)
        {
            var screenshot = await TakeScreenshotAsync(instance, logger);
            if (screenshot == null) return 0;
            using var image = Cv2.ImDecode(screenshot, ImreadModes.Color);
            using var button = new Mat(image, new Rect(UseButtonArea.X, UseButtonArea.Y, UseButtonArea.Width, UseButtonArea.Height));
            using var hsv = button.CvtColor(ColorConversionCodes.BGR2HSV);
            using var mask = hsv.InRange(new Scalar(40, 120, 120), new Scalar(80, 255, 255));
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
