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
    /// Side panel (left screen edge) > Daily tab: the Online Rewards chest (refills every few minutes) and the Gourmet Feast.
    /// Kept apart from Collect Store Rewards so it can run every cycle.
    ///
    /// SAFETY: only template-matched elements are tapped (panel arrows, the row icons, the chest bubble, the green Claim);
    /// the chest screen has no buttons, so the tap that leaves it hits the chest picture.
    /// </summary>
    public class CollectOnlineRewardsTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.CollectOnlineRewards;
        public override string Name => "Collect Online Rewards";

        private LocatorService? _locatorService;

        private const string PanelOpenArrowTemplate = "panel-open-arrow.png";
        private const string PanelCloseArrowTemplate = "panel-close-arrow.png";
        private const string OnlineRowIconTemplate = "online-row-icon.png";   // gift icon of the Online Rewards row
        private const string GourmetRowIconTemplate = "gourmet-row-icon.png"; // drumstick icon of the Gourmet Feast row
        private const string OnlineChestBubbleTemplate = "online-chest-bubble.png"; // chest speech bubble over the building
        private const string OnlineChestBubbleNightTemplate = "online-chest-bubble-night.png";
        private const string NextChestTemplate = "next-chest.png";           // "Next Chest Ready In:" on the Online Rewards screen
        private const string GourmetHeaderTemplate = "gourmet-header.png";   // "Gourmet Feast" popup title
        private const string GourmetClaimTemplate = "gourmet-claim.png";     // green "Claim" of the Gourmet Feast popup
        private static readonly Rectangle PanelOpenArrowArea = new Rectangle(0, 420, 60, 260);
        private static readonly Rectangle PanelCloseArrowArea = new Rectangle(400, 420, 120, 260);
        // Icon column of the rows only; other row icons score up to 0.84
        private static readonly Rectangle PanelListArea = new Rectangle(0, 300, 120, 520);
        private static readonly Rectangle OnlineChestBubbleArea = new Rectangle(150, 250, 420, 450);
        private static readonly Rectangle NextChestArea = new Rectangle(150, 1000, 420, 140);
        private static readonly Rectangle GourmetHeaderArea = new Rectangle(160, 720, 400, 140);
        private static readonly Rectangle GourmetClaimArea = new Rectangle(180, 880, 360, 140);
        private static readonly Point DailyTabPoint = new Point(370, 268);
        private static readonly Point OnlineChestTapPoint = new Point(360, 640);
        private static readonly Point OnlineChestPanTarget = new Point(360, 456); // bubble position after the row pans the camera
        private const int PanelRowTitleDx = 180;
        private const double PanelArrowThreshold = 0.88;
        private const double PanelRowThreshold = 0.93;
        private const double DailyPanelThreshold = 0.88;
        private const double OnlineChestBubbleThreshold = 0.85; // 0.90-1.0 day or night, <= 0.63 elsewhere

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            try
            {
                _locatorService = new LocatorService(logger, account, TaskType);
                int claims = await CollectAsync(account, logger, account.InstanceNumber, cancellationToken);
                logger.LogInfo($"[{account.AccountName}] Collect Online Rewards done: {claims} reward(s) claimed");
                return TaskExecutionDetails.Succeeded($"Claimed {claims} reward(s)");
            }
            catch (OperationCanceledException)
            {
                return TaskExecutionDetails.Failed("Task was cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error in Collect Online Rewards: {ex.Message}");
                return TaskExecutionDetails.Failed(ex.Message);
            }
        }

        /// <summary>
        /// Side panel (left screen edge) > Daily tab: claims the Online Rewards chest and the Gourmet Feast.
        /// The panel hides completed rows, so a missing row means there is nothing to claim.
        /// </summary>
        private async Task<int> CollectAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            int claims = 0;
            foreach (var (rowIcon, name) in new[] { (OnlineRowIconTemplate, "Online Rewards"), (GourmetRowIconTemplate, "Gourmet Feast") })
            {
                await _locatorService!.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);
                if (!await OpenDailyPanelAsync(instance, logger, cancellationToken))
                {
                    logger.LogWarning($"[{account.AccountName}] Side panel did not open, skipping the Daily rewards");
                    return claims;
                }

                var row = await FindDailyPanelRowAsync(() => MatchAsync(rowIcon, PanelListArea, PanelRowThreshold, instance, logger), instance, logger, cancellationToken);
                if (!row.found)
                {
                    logger.LogInfo($"[{account.AccountName}] {name}: row hidden, nothing to claim");
                    await CloseDailyPanelAsync(instance, logger, cancellationToken);
                    continue;
                }

                logger.LogInfo($"[{account.AccountName}] {name}: opening ({row.confidence:F2})");
                await ClickAsync(instance, logger, new Point(row.rect.X + row.rect.Width / 2 + PanelRowTitleDx, row.rect.Y + row.rect.Height / 2));
                await Task.Delay(1500, cancellationToken);

                if (rowIcon == OnlineRowIconTemplate)
                {
                    // The row pans the camera to the building with the chest bubble; tapping the bubble claims the chest
                    // The camera pan takes a moment; at night the town (and the bubble) is darker
                    var bubble = (found: false, rect: Rectangle.Empty, confidence: 0.0);
                    for (int i = 0; i < 3 && !bubble.found; i++)
                    {
                        bubble = await MatchAsync(OnlineChestBubbleTemplate, OnlineChestBubbleArea, OnlineChestBubbleThreshold, instance, logger);
                        if (!bubble.found)
                            bubble = await MatchAsync(OnlineChestBubbleNightTemplate, OnlineChestBubbleArea, OnlineChestBubbleThreshold, instance, logger);
                        if (!bubble.found) await Task.Delay(1000, cancellationToken);
                    }
                    if (bubble.found)
                    {
                        await ClickCenterInRectAsync(instance, logger, bubble.rect);
                    }
                    else
                    {
                        // A pointing hand covers the bubble after the row tap; the pan always centres the bubble at the same spot
                        logger.LogInfo($"[{account.AccountName}] Online Rewards: bubble hidden by the pointer ({bubble.confidence:F2}), tapping the pan target");
                        await ClickAsync(instance, logger, OnlineChestPanTarget);
                    }
                    await Task.Delay(2000, cancellationToken);
                    if (!(await MatchAsync(NextChestTemplate, NextChestArea, DailyPanelThreshold, instance, logger)).found)
                    {
                        logger.LogWarning($"[{account.AccountName}] Online Rewards: chest screen did not open");
                        continue;
                    }
                    logger.LogInfo($"[{account.AccountName}] Online Rewards: chest claimed");
                    claims++;
                    // The chest screen ("Next Chest Ready In") has no buttons; a tap exits it
                    for (int i = 0; i < 3; i++)
                    {
                        if (!(await MatchAsync(NextChestTemplate, NextChestArea, DailyPanelThreshold, instance, logger)).found) break;
                        await ClickAsync(instance, logger, OnlineChestTapPoint);
                        await Task.Delay(1500, cancellationToken);
                    }
                }
                else
                {
                    if (!(await MatchAsync(GourmetHeaderTemplate, GourmetHeaderArea, DailyPanelThreshold, instance, logger)).found)
                    {
                        logger.LogWarning($"[{account.AccountName}] Gourmet Feast popup not found");
                        continue;
                    }
                    var claim = await MatchAsync(GourmetClaimTemplate, GourmetClaimArea, DailyPanelThreshold, instance, logger);
                    if (claim.found)
                    {
                        await ClickCenterInRectAsync(instance, logger, claim.rect);
                        await Task.Delay(2000, cancellationToken);
                        claims++;
                    }
                    else
                    {
                        logger.LogInfo($"[{account.AccountName}] Gourmet Feast: no Claim button");
                    }
                }
            }
            await _locatorService!.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);
            return claims;
        }

        private async Task<bool> OpenDailyPanelAsync(int instance, LogService logger, CancellationToken cancellationToken)
        {
            if (!(await MatchAsync(PanelCloseArrowTemplate, PanelCloseArrowArea, PanelArrowThreshold, instance, logger)).found)
            {
                var arrow = await MatchAsync(PanelOpenArrowTemplate, PanelOpenArrowArea, PanelArrowThreshold, instance, logger);
                if (!arrow.found) return false;
                await ClickCenterInRectAsync(instance, logger, arrow.rect);
                await Task.Delay(1500, cancellationToken);
                if (!(await MatchAsync(PanelCloseArrowTemplate, PanelCloseArrowArea, PanelArrowThreshold, instance, logger)).found) return false;
            }
            await ClickAsync(instance, logger, DailyTabPoint);
            await Task.Delay(1200, cancellationToken);
            return true;
        }

        private async Task CloseDailyPanelAsync(int instance, LogService logger, CancellationToken cancellationToken)
        {
            var close = await MatchAsync(PanelCloseArrowTemplate, PanelCloseArrowArea, PanelArrowThreshold, instance, logger);
            if (!close.found) return;
            await ClickCenterInRectAsync(instance, logger, close.rect);
            await Task.Delay(1000, cancellationToken);
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
