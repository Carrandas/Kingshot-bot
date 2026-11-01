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
    /// Alliance > War > Auto-Join at the bottom: turns rally auto-join on ("Enable") or, when it is already on, tops its
    /// 8 hour timer up again ("Restart"). The popup's troop, queue and type settings are left as the player set them.
    /// Free; no step spends anything.
    /// </summary>
    public class AutoJoinRallyTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.AutoJoinRally;
        public override string Name => "Auto Join Rally";

        private const string AllianceMenuTemplate = "alliance-menu.png";  // Alliance button of the bottom bar
        private const string WarTileTemplate = "war-tile.png";            // War tile of the Alliance screen
        private const string WarTitleTemplate = "war-title.png";          // "War" title of the war screen
        private const string AutoJoinOffTemplate = "autojoin-off.png";    // "Auto-Join" button: auto-join is off
        private const string AutoJoinOnTemplate = "autojoin-on.png";      // "Auto-joining..." button: auto-join is on
        private const string PopupTitleTemplate = "autojoin-title.png";   // "Auto-Join" title of the popup
        private const string EnableTemplate = "enable.png";               // right popup button while off
        private const string RestartTemplate = "restart.png";             // the same button while on
        private static readonly Rectangle AllianceMenuArea = new Rectangle(480, 1160, 150, 110);
        private static readonly Rectangle WarTileArea = new Rectangle(0, 600, 360, 160);
        private static readonly Rectangle WarTitleArea = new Rectangle(60, 0, 200, 80);
        private static readonly Rectangle AutoJoinButtonArea = new Rectangle(200, 1170, 320, 100);
        private static readonly Rectangle PopupTitleArea = new Rectangle(240, 110, 240, 80);
        private static readonly Rectangle PopupButtonArea = new Rectangle(380, 1040, 260, 110);
        private static readonly Point PopupClosePoint = new Point(667, 147);
        private static readonly Point BackArrowPoint = new Point(45, 40);
        private const double Threshold = 0.85;   // all templates: 1.00 present, <= 0.66 elsewhere (Enable vs Restart 0.57)

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            try
            {
                var locator = new LocatorService(logger, account, TaskType);
                int instance = account.InstanceNumber;
                await locator.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

                if (!await OpenWarAsync(account, logger, instance, cancellationToken))
                {
                    await locator.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);
                    return TaskExecutionDetails.Failed("Alliance war screen did not open");
                }

                string? result = await SwitchOnAsync(account, logger, instance, cancellationToken);

                // War screen > Alliance screen > base
                for (int i = 0; i < 2; i++)
                {
                    await ClickAsync(instance, logger, BackArrowPoint);
                    await Task.Delay(1500, cancellationToken);
                }
                await locator.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

                if (result == null) return TaskExecutionDetails.Failed("Auto-join could not be switched on");
                logger.LogInfo($"[{account.AccountName}] Auto Join Rally done: {result}");
                return TaskExecutionDetails.Succeeded(result);
            }
            catch (OperationCanceledException)
            {
                return TaskExecutionDetails.Failed("Task was cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error in Auto Join Rally: {ex.Message}");
                return TaskExecutionDetails.Failed(ex.Message);
            }
        }

        /// <summary>
        /// Bottom bar Alliance > War tile.
        /// </summary>
        private async Task<bool> OpenWarAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            var menu = await MatchAsync(AllianceMenuTemplate, AllianceMenuArea, instance, logger);
            if (!menu.found)
            {
                logger.LogWarning($"[{account.AccountName}] Auto Join Rally: Alliance button not found ({menu.confidence:F2})");
                return false;
            }
            await ClickCenterInRectAsync(instance, logger, menu.rect);
            await Task.Delay(2000, cancellationToken);

            var tile = await MatchAsync(WarTileTemplate, WarTileArea, instance, logger);
            if (!tile.found)
            {
                logger.LogWarning($"[{account.AccountName}] Auto Join Rally: War tile not found ({tile.confidence:F2})");
                return false;
            }
            await ClickCenterInRectAsync(instance, logger, tile.rect);
            await Task.Delay(2000, cancellationToken);

            return (await MatchAsync(WarTitleTemplate, WarTitleArea, instance, logger)).found;
        }

        /// <summary>
        /// Opens the Auto-Join popup and taps Enable or Restart, whichever shows. Returns what was done, or null when the
        /// popup or its button was not found. Closes the popup again.
        /// </summary>
        private async Task<string?> SwitchOnAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            var on = await MatchAsync(AutoJoinOnTemplate, AutoJoinButtonArea, instance, logger);
            var button = on.found ? on : await MatchAsync(AutoJoinOffTemplate, AutoJoinButtonArea, instance, logger);
            if (!button.found)
            {
                logger.LogWarning($"[{account.AccountName}] Auto Join Rally: Auto-Join button not found ({button.confidence:F2})");
                return null;
            }
            logger.LogInfo($"[{account.AccountName}] Auto Join Rally: auto-join is {(on.found ? "on" : "off")}");
            await ClickCenterInRectAsync(instance, logger, button.rect);
            await Task.Delay(2000, cancellationToken);

            if (!(await MatchAsync(PopupTitleTemplate, PopupTitleArea, instance, logger)).found)
            {
                logger.LogWarning($"[{account.AccountName}] Auto Join Rally: Auto-Join popup did not open");
                return null;
            }

            string? result = null;
            var enable = await MatchAsync(EnableTemplate, PopupButtonArea, instance, logger);
            var action = enable.found ? enable : await MatchAsync(RestartTemplate, PopupButtonArea, instance, logger);
            if (action.found)
            {
                await ClickCenterInRectAsync(instance, logger, action.rect);
                await Task.Delay(2000, cancellationToken);
                // Both leave the popup open with Restart showing once auto-join runs
                if ((await MatchAsync(RestartTemplate, PopupButtonArea, instance, logger)).found)
                    result = enable.found ? "auto-join enabled" : "auto-join timer restarted";
                else
                    logger.LogWarning($"[{account.AccountName}] Auto Join Rally: no Restart button after the tap");
            }
            else
            {
                logger.LogWarning($"[{account.AccountName}] Auto Join Rally: no Enable or Restart button ({action.confidence:F2})");
            }

            await ClickAsync(instance, logger, PopupClosePoint);
            await Task.Delay(1500, cancellationToken);
            return result;
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
