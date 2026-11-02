using Bot.Core.Config;
using Bot.Core.Logging;
using Bot.Core.Models;
using Bot.Core.Services;
using Bot.Core.Tasks.Modules.AutoHunt.Models;
using Bot.Core.ImageDetection;
using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.Core.Tasks.Modules.AutoHunt.Processors
{
    /// <summary>
    /// Processor for handling king targets
    /// </summary>
    public class KingTargetProcessor : BaseTargetProcessor
    {
        // Dependencies that need to be injected
        private readonly Func<int, LogService, Task<byte[]?>> _takeScreenshot;
        private readonly Func<int, LogService, Point, Task<bool>> _click;
        private readonly Func<int, LogService, Rectangle, Task<bool>> _clickRandomInRect;
        private readonly Func<string, int, LogService, double, Rectangle?, Task<bool>> _findAndClickImage;
        private readonly Func<string, int, LogService, CancellationToken, int, double, Rectangle?, Task<bool>> _waitForImage;
        private readonly Func<AccountSettings, LogService, Task<string>> _readPredictionText;
        private readonly Func<byte[]?, Rectangle, AccountSettings, LogService, bool, CancellationToken, Task<StaminaCheckResult>> _checkForStaminaLow;
        private readonly Func<AccountSettings, LogService, AutoHuntSettings, Task<bool>> _checkMaxMarch;
        private readonly Func<AccountSettings, LogService, Task<bool>> _isTargetMarching;
        private readonly Func<HuntTarget, AccountSettings, LogService, CancellationToken, Task> _handleMarchingTarget;

        public override string TargetType => "king";
        public override bool RequiresMarch => true;

        public KingTargetProcessor(
            UnifiedTemplateMatchingService templateMatcher,
            Func<int, LogService, Task<byte[]?>> takeScreenshot,
            Func<int, LogService, Point, Task<bool>> click,
            Func<int, LogService, Rectangle, Task<bool>> clickRandomInRect,
            Func<string, int, LogService, double, Rectangle?, Task<bool>> findAndClickImage,
            Func<string, int, LogService, CancellationToken, int, double, Rectangle?, Task<bool>> waitForImage,
            Func<AccountSettings, LogService, Task<string>> readPredictionText,
            Func<byte[]?, Rectangle, AccountSettings, LogService, bool, CancellationToken, Task<StaminaCheckResult>> checkForStaminaLow,
            Func<AccountSettings, LogService, AutoHuntSettings, Task<bool>> checkMaxMarch,
            Func<AccountSettings, LogService, Task<bool>> isTargetMarching,
            Func<HuntTarget, AccountSettings, LogService, CancellationToken, Task> handleMarchingTarget) 
            : base(templateMatcher)
        {
            _takeScreenshot = takeScreenshot;
            _click = click;
            _clickRandomInRect = clickRandomInRect;
            _findAndClickImage = findAndClickImage;
            _waitForImage = waitForImage;
            _readPredictionText = readPredictionText;
            _checkForStaminaLow = checkForStaminaLow;
            _checkMaxMarch = checkMaxMarch;
            _isTargetMarching = isTargetMarching;
            _handleMarchingTarget = handleMarchingTarget;
        }

        public override int GetPriority() => 1; // High priority for kings

        public override bool CanProcess(HuntTarget target, AutoHuntSessionState sessionState)
        {
            return base.CanProcess(target, sessionState) && sessionState.CanAttackKing;
        }

        public override async Task<TargetProcessResult> ProcessAsync(
            HuntTarget target,
            AutoHuntSessionState sessionState,
            AccountSettings account,
            LogService logger,
            CancellationToken cancellationToken,
            string accountId)
        {
            try
            {
                // Click king
                if (!await _clickRandomInRect(account.InstanceNumber, logger, target.MatchLocation))
                    return TargetProcessResult.Failed("Failed to click king target");
                    
                await Task.Delay(2000, cancellationToken);

                // Click view button
                var viewFailure = await ClickViewButtonAsync(target, account, logger, cancellationToken, accountId);
                if (viewFailure != null) return viewFailure;
                await Task.Delay(1500, cancellationToken);

                // Click attack button
                if (!await _findAndClickImage("attack-button.png", account.InstanceNumber, logger, 0.6, null))
                    return TargetProcessResult.Failed("Failed to click attack button");
                await Task.Delay(1000, cancellationToken);

                // Check prediction text before proceeding with king attack
                var prediction = await _readPredictionText(account, logger);
                if (IsPredictedToFail(prediction))
                {
                    logger.LogInfo($"[{account.AccountName}] King attack predicted to fail - '{prediction}', blocking target area for session");
                    await BlockPredictedFailureAsync(target, account, logger, cancellationToken, accountId);
                    return TargetProcessResult.Failed("Prediction indicates failure");
                }

                // Look for both deploy button and max march
                var deployFailure = await ClickDeployButtonAsync(account, logger, cancellationToken);
                if (deployFailure != null) return deployFailure;

                // Check for confirmation dialog and stamina
                var confirmFailure = await ConfirmDeploymentAsync(account, logger, cancellationToken);
                if (confirmFailure != null) return confirmFailure;

                await Task.Delay(2000, cancellationToken);

                // Check for max march
                var marchSettings = GetAutoHuntSettings(account);
                if (await _checkMaxMarch(account, logger, marchSettings))
                {
                    logger.LogInfo($"[{account.AccountName}] Max march detected after king deployment");
                }

                return TargetProcessResult.Successful("King target processed successfully");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error processing king target: {ex.Message}");
                return TargetProcessResult.Failed($"Exception: {ex.Message}");
            }
        }

        // These methods use the injected dependencies
        protected override Task<byte[]?> TakeScreenshotAsync(int instanceNumber, LogService logger) => _takeScreenshot(instanceNumber, logger);
        protected override Task<bool> ClickAsync(int instanceNumber, LogService logger, Point point) => _click(instanceNumber, logger, point);
        protected override Task<bool> ClickRandomInRectAsync(int instanceNumber, LogService logger, Rectangle rect) => _clickRandomInRect(instanceNumber, logger, rect);
        protected override Task<bool> FindAndClickImageAsync(string imageName, int instanceNumber, LogService logger, double threshold = 0.6, Rectangle? searchArea = null) => _findAndClickImage(imageName, instanceNumber, logger, threshold, searchArea);
        protected override Task<bool> WaitForImageAsync(string imageName, int instanceNumber, LogService logger, CancellationToken cancellationToken, int timeoutMs = 5000, double threshold = 0.6, Rectangle? searchArea = null) => _waitForImage(imageName, instanceNumber, logger, cancellationToken, timeoutMs, threshold, searchArea);

        /// <summary>
        /// Clicks the view button. When it is missing, works out why: the king is already marching,
        /// or the screen went back to hunt mode. Returns the failure, or null once the button was clicked.
        /// </summary>
        private async Task<TargetProcessResult?> ClickViewButtonAsync(
            HuntTarget target, AccountSettings account, LogService logger, CancellationToken cancellationToken, string accountId)
        {
            if (await _findAndClickImage("view-button.png", account.InstanceNumber, logger, 0.6, null))
            {
                return null;
            }

            // Check if it's because there's already a march
            if (await _isTargetMarching(account, logger))
            {
                logger.LogInfo($"[{account.AccountName}] Target already marching - area remains blocked: {target.TargetArea}");
                await _handleMarchingTarget(target, account, logger, cancellationToken);
                SkipMapViewChecks.AddOrUpdate(accountId, true, (_, __) => true);
                return TargetProcessResult.Failed("Target already marching");
            }

            // Check for stamina icon to see if we're back in hunt mode
            logger.LogInfo($"[{account.AccountName}] No view button found for king target, checking for stamina icon...");
            var staminaScreenshot = await _takeScreenshot(account.InstanceNumber, logger);
            if (staminaScreenshot != null)
            {
                var (staminaFound, _, _) = _templateMatcher.MatchTemplate(
                    staminaScreenshot,
                    Path.Combine(_imageTemplateFolder, "hunt-stamina.png"),
                    account.InstanceNumber,
                    threshold: 0.8,
                    searchArea: AutoHuntConstants.StaminaArea,
                    isUIElement: false
                );

                if (staminaFound)
                {
                    logger.LogInfo($"[{account.AccountName}] Found stamina icon, likely back in hunt mode - will look for targets again");
                    SkipMapViewChecks.AddOrUpdate(accountId, true, (_, __) => true);
                    return TargetProcessResult.Failed("Back in hunt mode");
                }
            }

            return TargetProcessResult.Failed("No view button found");
        }

        private static bool IsPredictedToFail(string prediction)
        {
            return prediction.Contains("not likely to prevail", StringComparison.OrdinalIgnoreCase) ||
                   prediction.Contains("almost certain to fail", StringComparison.OrdinalIgnoreCase) ||
                   prediction.Contains("certain to fail", StringComparison.OrdinalIgnoreCase);
        }

        private async Task BlockPredictedFailureAsync(
            HuntTarget target, AccountSettings account, LogService logger, CancellationToken cancellationToken, string accountId)
        {
            // Track this area as a prediction failure
            var predictionFailedAreas = PredictionFailedAreas.GetOrAdd(accountId, _ => new HashSet<Rectangle>());
            predictionFailedAreas.Add(target.TargetArea);
            logger.LogInfo($"[{account.AccountName}] PREDICTION BLOCK: Blocked area {target.TargetArea} for king target due to failed prediction (session-only)");

            // Ensure the area remains in the regular blocked areas
            var settings = GetAutoHuntSettings(account);
            settings.AddUsedTargetArea(accountId, target.TargetArea);
            SaveAutoHuntSettings(account, settings);

            // Click deploy-back to return to hunt view
            if (await _findAndClickImage("deploy-back.png", account.InstanceNumber, logger, 0.6, null))
            {
                logger.LogInfo($"[{account.AccountName}] Clicked deploy-back button to return to hunt view");
                await Task.Delay(1000, cancellationToken);
            }
        }

        /// <summary>Waits up to 3 seconds for the deploy button (clicked) or a max march notice. Returns the failure, or null once deploy was clicked.</summary>
        private async Task<TargetProcessResult?> ClickDeployButtonAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            DateTime startTime = DateTime.UtcNow;

            while ((DateTime.UtcNow - startTime).TotalSeconds < 3 && !cancellationToken.IsCancellationRequested)
            {
                var deployScreenshot = await _takeScreenshot(account.InstanceNumber, logger);
                if (deployScreenshot == null) continue;

                // Check for deploy button first
                var (deployFound, deployRect, _) = _templateMatcher.MatchTemplate(
                    deployScreenshot,
                    Path.Combine(_imageTemplateFolder, "deploy-button.png"),
                    account.InstanceNumber,
                    threshold: 0.6,
                    searchArea: AutoHuntConstants.DeployButtonArea
                );

                if (deployFound)
                {
                    logger.LogInfo($"[{account.AccountName}] Found deploy button, proceeding with attack");
                    if (!await _clickRandomInRect(account.InstanceNumber, logger, deployRect))
                        return TargetProcessResult.Failed("Failed to click deploy button");
                    return null;
                }

                // Check for max march
                var (maxMarchFound, _, _) = _templateMatcher.MatchTemplate(
                    deployScreenshot,
                    Path.Combine(_imageTemplateFolder, "max-march.png"),
                    account.InstanceNumber,
                    threshold: 0.6
                );

                if (maxMarchFound)
                {
                    logger.LogInfo($"[{account.AccountName}] Max march detected after attack button, clicking dismiss area");
                    await _clickRandomInRect(account.InstanceNumber, logger, AutoHuntConstants.MaxMarchClickArea);
                    var maxMarchSettings = GetAutoHuntSettings(account);
                    maxMarchSettings.LastMaxMarchTime = DateTime.UtcNow;
                    SaveAutoHuntSettings(account, maxMarchSettings);
                    return TargetProcessResult.Failed("Max march reached");
                }

                await Task.Delay(200, cancellationToken);
            }

            logger.LogWarning($"[{account.AccountName}] Could not find deploy button or max march notification");
            return TargetProcessResult.Failed("No deploy button found");
        }

        /// <summary>Handles the stamina warning and confirmation dialog after deploying. Returns the failure, or null.</summary>
        private async Task<TargetProcessResult?> ConfirmDeploymentAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            await Task.Delay(1000, cancellationToken);
            var confirmScreenshot = await _takeScreenshot(account.InstanceNumber, logger);
            if (confirmScreenshot == null)
            {
                return null;
            }

            var staminaLowArea = new Rectangle(285, 122, 155, 41);
            var staminaResult = await _checkForStaminaLow(confirmScreenshot, staminaLowArea, account, logger, false, cancellationToken);
            if (staminaResult == StaminaCheckResult.StaminaDepleted)
            {
                return TargetProcessResult.StaminaEmpty("Stamina depleted during king attack");
            }
            if (staminaResult == StaminaCheckResult.StaminaClaimed_Retry)
            {
                logger.LogInfo($"[{account.AccountName}] Stamina claimed, retrying king deployment");
            }

            // Check for confirmation dialog
            var (confirmationFound, _, _) = _templateMatcher.MatchTemplate(
                confirmScreenshot,
                Path.Combine(_imageTemplateFolder, "confirmation.png"),
                account.InstanceNumber,
                threshold: 0.6
            );

            if (confirmationFound)
            {
                logger.LogInfo($"[{account.AccountName}] Found confirmation dialog, clicking confirm points");
                await _click(account.InstanceNumber, logger, new Point(212, 714));
                await Task.Delay(500, cancellationToken);
                await _click(account.InstanceNumber, logger, new Point(456, 770));
                await Task.Delay(1000, cancellationToken);
            }
            return null;
        }
    }
}