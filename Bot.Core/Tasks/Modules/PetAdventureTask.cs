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
    /// Pet Adventure: opens the side panel (Daily tab) -> Pet Adventure, claims the finished chests,
    /// sends the pets out with "Hunt All", then opens Ally Treasure, shares our own chest and claims
    /// the chests shared by the alliance.
    ///
    /// SAFETY: only template-matched buttons are tapped (Claim All, Hunt All, Share, the Alliance Shares tab,
    /// the popup X and the back arrow). "Tap anywhere to exit" is only tapped while that text is on screen.
    /// </summary>
    public class PetAdventureTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.PetAdventure;
        public override string Name => "Pet Adventure";

        private LocatorService? _locatorService;

        // Template names (templates/images/petadventure/)
        private const string PanelOpenArrowTemplate = "panel-open-arrow.png";   // ">" tab on the left screen edge (panel closed)
        private const string PanelCloseArrowTemplate = "panel-close-arrow.png"; // "<" tab next to the open panel
        private const string PetRowIconTemplate = "pet-row-icon.png";           // paw-map icon of the Pet Adventure row (Daily tab)
        private const string PetHeaderTemplate = "pet-header.png";              // "Pet Adventure" title bar
        private const string PetClaimAllTemplate = "pet-claim-all.png";         // green "Claim All" (finished chests)
        private const string HuntAllTemplate = "hunt-all.png";                  // teal "Hunt All"
        private const string AllyTreasureTemplate = "ally-treasure.png";        // chest button at the bottom right
        private const string AllyHeaderTemplate = "ally-header.png";            // "Ally Treasure" popup title
        private const string ShareButtonTemplate = "share-button.png";          // teal "Share" (My Shares tab)
        private const string AllianceSharesTabTemplate = "alliance-shares-tab.png"; // unselected "Alliance Shares" tab
        private const string AllyClaimAllTemplate = "ally-claim-all.png";       // green "Claim All" in the popup
        private const string PopupCloseTemplate = "popup-close.png";            // X of the Ally Treasure popup
        private const string TapAnywhereTemplate = "tap-anywhere.png";          // "Tap anywhere to exit" under a Rewards popup

        // Search areas
        private static readonly Rectangle PanelArrowArea = new Rectangle(0, 420, 60, 260);
        private static readonly Rectangle PanelCloseArrowArea = new Rectangle(400, 420, 120, 260);
        private static readonly Rectangle PanelListArea = new Rectangle(0, 300, 120, 520);
        private static readonly Rectangle PetHeaderArea = new Rectangle(40, 0, 360, 80);
        private static readonly Rectangle BottomButtonArea = new Rectangle(100, 1100, 520, 160);
        private static readonly Rectangle AllyTreasureArea = new Rectangle(540, 1100, 180, 160);
        private static readonly Rectangle AllyHeaderArea = new Rectangle(160, 170, 400, 100);
        private static readonly Rectangle ShareButtonArea = new Rectangle(460, 330, 240, 160); // first row only
        private static readonly Rectangle SharesTabArea = new Rectangle(340, 250, 380, 110);
        private static readonly Rectangle AllyClaimAllArea = new Rectangle(140, 960, 440, 180);
        private static readonly Rectangle PopupCloseArea = new Rectangle(600, 170, 120, 100);
        private static readonly Rectangle TapAnywhereArea = new Rectangle(100, 1100, 520, 140);

        // Fixed tap targets
        private static readonly Point DailyTabPoint = new Point(370, 268);
        private static readonly Rectangle BackArrowClickArea = new Rectangle(24, 20, 50, 40);
        // The row title sits to the right of the row icon
        private const int PetRowTitleDx = 180;

        // Present: >= 0.96, elsewhere: <= 0.78 (row icon), <= 0.71 (others)
        private const double Threshold = 0.88;

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            try
            {
                _locatorService = new LocatorService(logger, account, TaskType);
                int instance = account.InstanceNumber;
                int actions = 0;

                var opened = await OpenPetAdventureAsync(account, logger, instance, cancellationToken);
                if (opened == null)
                {
                    // "Hide after mission completion" removes the row once there is nothing left to claim or send
                    await CloseSidePanelAsync(instance, logger, cancellationToken);
                    logger.LogInfo($"[{account.AccountName}] Pet Adventure row is hidden (completed), nothing to do");
                    return TaskExecutionDetails.Succeeded("Pet Adventure already completed");
                }
                if (opened == false)
                {
                    await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);
                    return TaskExecutionDetails.Failed("Could not open Pet Adventure");
                }

                // 1. Finished chests
                var claimAll = await MatchAsync(PetClaimAllTemplate, BottomButtonArea, instance, logger);
                if (claimAll.found)
                {
                    logger.LogInfo($"[{account.AccountName}] Pet Adventure: Claim All ({claimAll.confidence:F2})");
                    await ClickCenterInRectAsync(instance, logger, claimAll.rect);
                    await DismissRewardsAsync(instance, logger, cancellationToken);
                    actions++;
                }

                // 2. Send the pets out
                var huntAll = await MatchAsync(HuntAllTemplate, BottomButtonArea, instance, logger);
                if (huntAll.found)
                {
                    logger.LogInfo($"[{account.AccountName}] Pet Adventure: Hunt All ({huntAll.confidence:F2})");
                    await ClickCenterInRectAsync(instance, logger, huntAll.rect);
                    await Task.Delay(2000, cancellationToken);
                    await DismissRewardsAsync(instance, logger, cancellationToken);
                    actions++;
                }
                else
                {
                    logger.LogInfo($"[{account.AccountName}] Pet Adventure: no Hunt All button (no attempts left or pets out)");
                }

                // 3. Ally Treasure
                actions += await HandleAllyTreasureAsync(account, logger, instance, cancellationToken);

                // 4. Back to the map / base
                for (int i = 0; i < 3 && (await MatchAsync(PetHeaderTemplate, PetHeaderArea, instance, logger)).found; i++)
                {
                    await ClickRandomInRectAsync(instance, logger, BackArrowClickArea);
                    await Task.Delay(1500, cancellationToken);
                }
                await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

                logger.LogInfo($"[{account.AccountName}] Pet Adventure done: {actions} action(s)");
                return TaskExecutionDetails.Succeeded($"Pet Adventure: {actions} action(s)");
            }
            catch (OperationCanceledException)
            {
                return TaskExecutionDetails.Failed("Task was cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error in Pet Adventure: {ex.Message}");
                return TaskExecutionDetails.Failed(ex.Message);
            }
        }

        /// <summary>
        /// Opens the side panel on the map or base view, selects the Daily tab and taps the Pet Adventure row.
        /// Returns null when the row is hidden (the panel hides completed missions).
        /// </summary>
        private async Task<bool?> OpenPetAdventureAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            if ((await MatchAsync(PetHeaderTemplate, PetHeaderArea, instance, logger)).found) return true;

            // The panel arrow is on both the map and the base view; try the current view first
            var arrow = await MatchAsync(PanelOpenArrowTemplate, PanelArrowArea, instance, logger);
            var panelOpen = await MatchAsync(PanelCloseArrowTemplate, PanelCloseArrowArea, instance, logger);
            foreach (var view in new[] { ViewType.MapView, ViewType.BaseView })
            {
                if (arrow.found || panelOpen.found) break;
                await _locatorService!.EnsureViewAsync(view, instance, cancellationToken);
                arrow = await MatchAsync(PanelOpenArrowTemplate, PanelArrowArea, instance, logger);
                panelOpen = await MatchAsync(PanelCloseArrowTemplate, PanelCloseArrowArea, instance, logger);
            }

            if (!panelOpen.found)
            {
                if (!arrow.found)
                {
                    logger.LogWarning($"[{account.AccountName}] Pet Adventure: side panel arrow not found");
                    return false;
                }
                await ClickCenterInRectAsync(instance, logger, arrow.rect);
                await Task.Delay(1500, cancellationToken);
                if (!(await MatchAsync(PanelCloseArrowTemplate, PanelCloseArrowArea, instance, logger)).found)
                {
                    logger.LogWarning($"[{account.AccountName}] Pet Adventure: side panel did not open");
                    return false;
                }
            }

            await ClickAsync(instance, logger, DailyTabPoint);
            await Task.Delay(1200, cancellationToken);

            var row = await MatchAsync(PetRowIconTemplate, PanelListArea, instance, logger);
            if (!row.found)
            {
                // The list scrolls; Pet Adventure may sit below the fold
                await SwipeAsync(instance, logger, new Point(220, 760), new Point(220, 420), 800);
                await Task.Delay(1000, cancellationToken);
                row = await MatchAsync(PetRowIconTemplate, PanelListArea, instance, logger);
            }
            if (!row.found)
            {
                return null;
            }

            await ClickAsync(instance, logger, new Point(row.rect.X + row.rect.Width / 2 + PetRowTitleDx, row.rect.Y + row.rect.Height / 2));
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(500, cancellationToken);
                if ((await MatchAsync(PetHeaderTemplate, PetHeaderArea, instance, logger)).found) return true;
            }
            logger.LogWarning($"[{account.AccountName}] Pet Adventure screen did not open");
            return false;
        }

        private async Task CloseSidePanelAsync(int instance, LogService logger, CancellationToken cancellationToken)
        {
            var close = await MatchAsync(PanelCloseArrowTemplate, PanelCloseArrowArea, instance, logger);
            if (!close.found) return;
            await ClickCenterInRectAsync(instance, logger, close.rect);
            await Task.Delay(1000, cancellationToken);
        }

        /// <summary>
        /// Ally Treasure popup: share our own chest (My Shares), then claim the alliance chests (Alliance Shares).
        /// </summary>
        private async Task<int> HandleAllyTreasureAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            var treasure = await MatchAsync(AllyTreasureTemplate, AllyTreasureArea, instance, logger);
            if (!treasure.found)
            {
                logger.LogInfo($"[{account.AccountName}] Ally Treasure button not found");
                return 0;
            }
            await ClickCenterInRectAsync(instance, logger, treasure.rect);

            bool open = false;
            for (int i = 0; i < 8 && !open; i++)
            {
                await Task.Delay(500, cancellationToken);
                open = (await MatchAsync(AllyHeaderTemplate, AllyHeaderArea, instance, logger)).found;
            }
            if (!open)
            {
                logger.LogWarning($"[{account.AccountName}] Ally Treasure popup did not open");
                return 0;
            }

            int actions = 0;
            var share = await MatchAsync(ShareButtonTemplate, ShareButtonArea, instance, logger);
            if (share.found)
            {
                logger.LogInfo($"[{account.AccountName}] Ally Treasure: sharing our chest ({share.confidence:F2})");
                await ClickCenterInRectAsync(instance, logger, share.rect);
                await Task.Delay(1500, cancellationToken);
                await DismissRewardsAsync(instance, logger, cancellationToken);
                actions++;
            }

            var tab = await MatchAsync(AllianceSharesTabTemplate, SharesTabArea, instance, logger);
            if (tab.found)
            {
                await ClickCenterInRectAsync(instance, logger, tab.rect);
                await Task.Delay(1500, cancellationToken);
            }

            var claimAll = await MatchAsync(AllyClaimAllTemplate, AllyClaimAllArea, instance, logger);
            if (claimAll.found)
            {
                logger.LogInfo($"[{account.AccountName}] Ally Treasure: Claim All ({claimAll.confidence:F2})");
                await ClickCenterInRectAsync(instance, logger, claimAll.rect);
                await DismissRewardsAsync(instance, logger, cancellationToken);
                actions++;
            }

            var close = await MatchAsync(PopupCloseTemplate, PopupCloseArea, instance, logger);
            if (close.found)
            {
                await ClickCenterInRectAsync(instance, logger, close.rect);
                await Task.Delay(1000, cancellationToken);
            }
            return actions;
        }

        /// <summary>
        /// Taps "Tap anywhere to exit" while a Rewards popup is shown (the text itself is the tap target).
        /// </summary>
        private async Task DismissRewardsAsync(int instance, LogService logger, CancellationToken cancellationToken)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                await Task.Delay(1000, cancellationToken);
                var tap = await MatchAsync(TapAnywhereTemplate, TapAnywhereArea, instance, logger);
                if (!tap.found) return;
                await ClickCenterInRectAsync(instance, logger, tap.rect);
            }
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
