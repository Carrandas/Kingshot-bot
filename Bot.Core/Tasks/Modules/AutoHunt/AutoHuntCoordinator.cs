using Bot.Core.Config;
using Bot.Core.Logging;
using Bot.Core.Tasks.Modules.AutoHunt.Models;
using Bot.Core.Tasks.Modules.AutoHunt.Services;
using Bot.Core.Tasks;
using Bot.Core.Models;
using Bot.Core.ImageDetection;
using Bot.Core.Services;
using Bot.Core.Utils;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace Bot.Core.Tasks.Modules.AutoHunt
{
    /// <summary>
    /// Coordinator that orchestrates the AutoHunt process using the new modular architecture
    /// </summary>
    public class AutoHuntCoordinator : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.AutoHunt;
        public override string Name => "Auto Hunt Coordinator";

        // Static session state storage - using account ID for better isolation
        private static readonly ConcurrentDictionary<string, AutoHuntSessionState> SessionStates = new();
        private static readonly ConcurrentDictionary<string, bool> SkipMapViewChecks = new();
        private static readonly ConcurrentDictionary<string, HashSet<Rectangle>> PredictionFailedAreas = new();

        // A hunt march takes ~30-60 s; 12 waits of 20 s cap the extra time at 4 minutes per run
        private const int BlockedTargetWaitMs = 20000;
        private const int MaxBlockedTargetWaits = 12;
        private const int MaxConsecutiveFailures = 3;

        // Services
        private readonly ITargetDetectionService _targetDetectionService;

        public AutoHuntCoordinator(
            ITargetDetectionService targetDetectionService,
            IMarchManagementService marchManagementService,
            IStaminaManagementService staminaManagementService,
            IHuntModeNavigationService huntModeNavigationService,
            IAutoHuntVisualDebugger visualDebugger)
        {
            _targetDetectionService = targetDetectionService;
        }

        public async Task<TaskExecutionDetails> ExecuteAutoHuntAsync(
            AccountSettings account, 
            LogService logger, 
            CancellationToken cancellationToken, 
            bool isReRun = false, 
            IUserNotificationService? userNotifications = null)
        {
            try
            {
                // Initialize the template matcher if not already done
                if (_templateMatcher == null)
                {
                    _templateMatcher = new UnifiedTemplateMatchingService(logger);
                }
                // Get unique account ID for this account
                var accountId = await GetAccountIdAsync(account);
                logger.LogInfo($"[{account.AccountName}] Using account ID: '{accountId}' for blocked area tracking");

                // Ensure map view first
                logger.LogInfo($"[{account.AccountName}] 🧭 Ensuring bot is in MapView for initial march check...");
                var locator = new LocatorService(logger, account);
                await locator.EnsureViewAsync(ViewType.MapView, account.InstanceNumber, cancellationToken);

                // Wait for map view to fully stabilize
                await WaitIfPausedAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested) return TaskExecutionDetails.Failed("Task was cancelled");
                await Task.Delay(2000, cancellationToken);

                // Check available marches before doing anything else
                int initialMarches = await GetAvailableMarchesDirectly(account, logger);
                logger.LogInfo($"[{account.AccountName}] Initial march check: {initialMarches} marches available");
                if (initialMarches <= 0)
                {
                    return TaskExecutionDetails.Failed("No marches available at start, skipping AutoHunt module");
                }

                // Get settings and clear used areas for this account when starting
                var huntSettings = GetAutoHuntSettings(account);
                huntSettings.ClearUsedTargetAreas(accountId);
                SaveAutoHuntSettings(account, huntSettings);
                logger.LogInfo($"[{account.AccountName}] Cleared used areas for account ID: {accountId}");

                // Get or create session state
                var sessionState = SessionStates.GetOrAdd(accountId, 
                    new AutoHuntSessionState { InstanceNumber = account.InstanceNumber });

                // Initial max march check
                if (await CheckMaxMarchDirectly(account, logger))
                {
                    return TaskExecutionDetails.Failed("Max march detected at start, waiting 2 minutes");
                }

                await WaitIfPausedAsync(cancellationToken);
                if (!cancellationToken.IsCancellationRequested)
                {
                    var early = await RunHuntLoopAsync(account, logger, cancellationToken, accountId, sessionState);
                    if (early != null) return early;

                    // Analyze blocked areas to determine next action
                    LogBlockedAreaSummary(account, logger, accountId);
                }

                return new TaskExecutionDetails(true, message: "AutoHunt completed");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error during AutoHunt: {ex.Message}");
                return TaskExecutionDetails.Failed($"Error during AutoHunt: {ex.Message}");
            }
        }

        private enum HuntFlow { Next, EndLoop, Finish }

        private readonly record struct HuntOutcome(HuntFlow Flow, TaskExecutionDetails? Result = null);

        private sealed class HuntLoopState
        {
            public int BlockedTargetWaits;
            public int ConsecutiveFailures;
        }

        /// <summary>The inner hunt loop. Returns the task result when the hunt ends early, null when it ran out of targets.</summary>
        private async Task<TaskExecutionDetails?> RunHuntLoopAsync(
            AccountSettings account, LogService logger, CancellationToken cancellationToken, string accountId, AutoHuntSessionState sessionState)
        {
            var state = new HuntLoopState();
            while (!cancellationToken.IsCancellationRequested)
            {
                await WaitIfPausedAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested) break;

                var outcome = await HuntIterationAsync(account, logger, cancellationToken, accountId, sessionState, state);
                if (outcome.Flow == HuntFlow.Finish) return outcome.Result;
                if (outcome.Flow == HuntFlow.EndLoop) break;
            }
            return null;
        }

        private async Task<HuntOutcome> HuntIterationAsync(
            AccountSettings account, LogService logger, CancellationToken cancellationToken, string accountId,
            AutoHuntSessionState sessionState, HuntLoopState state)
        {
            // Check if we are already in hunt mode from a previous action
            bool isAlreadyInHuntMode = SkipMapViewChecks.GetOrAdd(accountId, false);

            int availableMarches = await CountMarchesForIterationAsync(account, logger, cancellationToken, accountId, isAlreadyInHuntMode);
            if (!isAlreadyInHuntMode && availableMarches == -1)
            {
                return new HuntOutcome(HuntFlow.Finish, new TaskExecutionDetails(true, message: "Stamina depleted while waiting for marches"));
            }

            // Enter hunt mode if not already in it
            if (!isAlreadyInHuntMode && !await EnterHuntModeAndTapIntelAsync(account, logger, cancellationToken))
            {
                return new HuntOutcome(HuntFlow.Finish, TaskExecutionDetails.Failed("Could not enter hunt mode"));
            }

            // Detect targets - directly implemented in coordinator
            var targets = await DetectAllTargetsDirectly(account, logger, cancellationToken, accountId);
            if (targets.Any())
            {
                var targetsOrderedByConfidence = targets.OrderByDescending(t => t.Confidence).ToList();
                logger.LogInfo($"[{account.AccountName}] Target selection order by confidence: {string.Join(", ", targetsOrderedByConfidence.Select(t => $"{t.Type}({t.Confidence:F3})"))}");
            }

            // Prioritize target
            var context = new TargetPrioritizationContext(sessionState, availableMarches, accountId);
            var target = _targetDetectionService.PrioritizeTarget(targets, context, out bool noMarchesAvailable);

            if (target == null)
            {
                return await HandleNoTargetAsync(account, logger, cancellationToken, accountId, noMarchesAvailable, state);
            }

            // Process the target
            var result = await ProcessTargetByType(target, sessionState, account, logger, cancellationToken, accountId);
            return await HandleTargetResultAsync(account, logger, cancellationToken, accountId, target, result, availableMarches, state);
        }

        private async Task<HuntOutcome> HandleTargetResultAsync(
            AccountSettings account, LogService logger, CancellationToken cancellationToken, string accountId,
            HuntTarget target, TargetProcessResult result, int availableMarches, HuntLoopState state)
        {
            if (result.StaminaDepleted)
            {
                logger.LogInfo($"[{account.AccountName}] Stamina depleted, ending hunt session");
                await ReturnToMapViewDirectly(account, logger, cancellationToken);
                return new HuntOutcome(HuntFlow.Finish, new TaskExecutionDetails(true, message: "Stamina depleted, moving to next module"));
            }

            if (!result.Success && target.RequiresMarch)
            {
                if (await ReleaseFailedTargetAsync(account, logger, cancellationToken, accountId, target, result, state))
                {
                    return new HuntOutcome(HuntFlow.EndLoop);
                }
            }
            else if (result.Success)
            {
                state.ConsecutiveFailures = 0;
            }

            if (result.Success && target.RequiresMarch)
            {
                availableMarches--;
                logger.LogInfo($"[{account.AccountName}] March used. Remaining: {availableMarches}");

                if (availableMarches <= 0)
                {
                    logger.LogInfo($"[{account.AccountName}] No more marches available, waiting for marches...");
                }
            }
            return new HuntOutcome(HuntFlow.Next);
        }

        private async Task<bool> EnterHuntModeAndTapIntelAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            if (!await EnterHuntModeDirectly(account, logger, cancellationToken))
            {
                return false;
            }
            await Task.Delay(2000, cancellationToken);
            await TapIntelAgentAsync(account, logger, cancellationToken);
            return true;
        }

        private async Task<int> CountMarchesForIterationAsync(
            AccountSettings account, LogService logger, CancellationToken cancellationToken, string accountId, bool isAlreadyInHuntMode)
        {
            if (!isAlreadyInHuntMode)
            {
                // Check marches at the start of every iteration
                return await WaitForAvailableMarchesDirectly(account, logger, cancellationToken);
            }

            logger.LogInfo($"[{account.AccountName}] Already in hunt mode, skipping map view and march wait checks.");
            int availableMarches = await GetAvailableMarchesDirectly(account, logger);
            logger.LogInfo($"[{account.AccountName}] Current available marches: {availableMarches}");

            // Reset the flag for the next iteration
            SkipMapViewChecks.AddOrUpdate(accountId, false, (_, __) => false);
            return availableMarches;
        }

        private async Task<HuntOutcome> HandleNoTargetAsync(
            AccountSettings account, LogService logger, CancellationToken cancellationToken, string accountId, bool noMarchesAvailable, HuntLoopState state)
        {
            if (noMarchesAvailable)
            {
                logger.LogInfo($"[{account.AccountName}] Found targets but no marches available, waiting for marches...");
                return new HuntOutcome(HuntFlow.Next);
            }

            // Targets with a march on the way stay blocked until their tick is cleared, and new
            // intel missions only show up once those are done: wait for the marches instead of leaving
            var predictionFailed = PredictionFailedAreas.GetOrAdd(accountId, _ => new HashSet<Rectangle>());
            if (state.BlockedTargetWaits < MaxBlockedTargetWaits &&
                GetAutoHuntSettings(account).GetUsedTargetAreas(accountId).Any(area => !predictionFailed.Contains(area)))
            {
                state.BlockedTargetWaits++;
                logger.LogInfo($"[{account.AccountName}] Only targets with a march on the way left, waiting {BlockedTargetWaitMs / 1000}s for them to finish ({state.BlockedTargetWaits}/{MaxBlockedTargetWaits})");
                await ReturnToMapViewDirectly(account, logger, cancellationToken);
                await Task.Delay(BlockedTargetWaitMs, cancellationToken);
                return new HuntOutcome(HuntFlow.Next);
            }

            logger.LogInfo($"[{account.AccountName}] No suitable targets available, returning to map view");
            await ReturnToMapViewDirectly(account, logger, cancellationToken);
            return new HuntOutcome(HuntFlow.EndLoop);
        }

        /// <summary>No march went out for this target. Returns true when too many attacks failed in a row and the hunt should end.</summary>
        private async Task<bool> ReleaseFailedTargetAsync(
            AccountSettings account, LogService logger, CancellationToken cancellationToken, string accountId,
            HuntTarget target, TargetProcessResult result, HuntLoopState state)
        {
            // No march went out (e.g. the deploy screen did not open while every march was busy):
            // release the area, unless the target is marching already or was predicted to fail
            bool keepBlocked = result.Message == "Target already marching" || result.Message.StartsWith("Prediction");
            if (!keepBlocked)
            {
                var settings = GetAutoHuntSettings(account);
                settings.RemoveUsedTargetArea(accountId, target.TargetArea);
                SaveAutoHuntSettings(account, settings);
                logger.LogInfo($"[{account.AccountName}] {target.Type} not attacked ({result.Message}), area released");
            }

            if (++state.ConsecutiveFailures < MaxConsecutiveFailures)
            {
                return false;
            }

            logger.LogInfo($"[{account.AccountName}] {state.ConsecutiveFailures} failed attacks in a row, ending the hunt for this run");
            await ReturnToMapViewDirectly(account, logger, cancellationToken);
            return true;
        }

        private static void LogBlockedAreaSummary(AccountSettings account, LogService logger, string accountId)
        {
            var finalSettings = GetAutoHuntSettings(account);
            var blockedAreas = finalSettings.GetUsedTargetAreas(accountId);
            var predictionFailedAreas = PredictionFailedAreas.GetOrAdd(accountId, _ => new HashSet<Rectangle>());

            if (!blockedAreas.Any())
            {
                logger.LogInfo($"[{account.AccountName}] 🎯 AutoHunt completed. No blocked areas.");
                return;
            }

            var nonPredictionBlocks = blockedAreas.Where(area => !predictionFailedAreas.Contains(area)).ToList();
            if (nonPredictionBlocks.Any())
            {
                logger.LogInfo($"[{account.AccountName}] 🎯 AutoHunt completed. {nonPredictionBlocks.Count} temporary blocks, {predictionFailedAreas.Count} prediction failures.");
                logger.LogInfo($"[{account.AccountName}] 📋 Moving to next module (blocked areas will expire individually).");
            }
            else
            {
                logger.LogInfo($"[{account.AccountName}] 🚫 All {predictionFailedAreas.Count} blocked areas are prediction failures. Moving to next module.");
            }
        }

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(
            AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            return await ExecuteAutoHuntAsync(account, logger, cancellationToken, isReRun, userNotifications);
        }

        protected override string GetImageFolderName()
        {
            return "autohunt";
        }

        private async Task<TargetProcessResult> ProcessTargetByType(
            HuntTarget target,
            AutoHuntSessionState sessionState,
            AccountSettings account,
            LogService logger,
            CancellationToken cancellationToken,
            string accountId)
        {
            // Block the area immediately when we start processing any target that requires a march
            if (target.RequiresMarch)
            {
                var settings = GetAutoHuntSettings(account);
                settings.AddUsedTargetArea(accountId, target.TargetArea);
                SaveAutoHuntSettings(account, settings);
            }

            // Route to appropriate processor
            return target.Type switch
            {
                "king" => await ProcessKingTarget(target, account, logger, cancellationToken, accountId),
                "bear" or "bear-elite" => await ProcessBearTarget(target, sessionState, account, logger, cancellationToken, accountId),
                "scout" => await ProcessScoutTarget(target, account, logger, cancellationToken, accountId),
                "attack" => await ProcessAttackTarget(target, sessionState, account, logger, cancellationToken, accountId),
                _ => TargetProcessResult.Failed($"Unknown target type: {target.Type}")
            };
        }

        #region Target Processors

        /// <summary>
        /// Clicks the view button of the target. When it is missing, works out why: the target is already marching,
        /// or the screen went back to hunt mode. Returns the failure, or null once the button was clicked.
        /// </summary>
        private async Task<TargetProcessResult?> ClickViewButtonAsync(
            HuntTarget target, AccountSettings account, LogService logger, string accountId, string targetLabel, bool checkMarching)
        {
            if (await FindAndClickImageAsync("view-button.png", account.InstanceNumber, logger))
            {
                return null;
            }

            // Check if it's because there's already a march
            if (checkMarching && await IsTargetMarchingDirectly(account, logger))
            {
                logger.LogInfo($"[{account.AccountName}] Target already marching - area remains blocked: {target.TargetArea}");
                SkipMapViewChecks.AddOrUpdate(accountId, true, (_, __) => true);
                return TargetProcessResult.Failed("Target already marching");
            }

            // Check for stamina icon to see if we're back in hunt mode
            logger.LogInfo($"[{account.AccountName}] No view button found for {targetLabel} target, checking for stamina icon...");
            var staminaScreenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
            if (staminaScreenshot != null)
            {
                var (staminaFound, _, _) = _templateMatcher.MatchTemplate(
                    staminaScreenshot,
                    Path.Combine(ImageTemplateFolder, "hunt-stamina.png"),
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

        /// <summary>The attack is predicted to fail: remember the area as a prediction failure and leave the deploy screen.</summary>
        private async Task BlockPredictedFailureAsync(HuntTarget target, AccountSettings account, LogService logger, CancellationToken cancellationToken, string accountId)
        {
            // Track as prediction failure
            var predictionFailedAreas = PredictionFailedAreas.GetOrAdd(accountId, _ => new HashSet<Rectangle>());
            predictionFailedAreas.Add(target.TargetArea);

            // Click deploy-back to return to hunt view
            if (await FindAndClickImageAsync("deploy-back.png", account.InstanceNumber, logger))
            {
                logger.LogInfo($"[{account.AccountName}] Clicked deploy-back button to return to hunt view");
                await Task.Delay(1000, cancellationToken);
            }
        }

        /// <summary>Reads the stamina warning after a click. True when the stamina is gone; a claimed refill only gets logged.</summary>
        private async Task<bool> IsStaminaDepletedAsync(AccountSettings account, LogService logger, string retryMessage)
        {
            var staminaCheckScreenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
            if (staminaCheckScreenshot == null)
            {
                return false;
            }

            var staminaResult = await CheckForStaminaLowDirectly(staminaCheckScreenshot, account, logger);
            if (staminaResult == StaminaCheckResult.StaminaDepleted)
            {
                return true;
            }
            if (staminaResult == StaminaCheckResult.StaminaClaimed_Retry)
            {
                logger.LogInfo($"[{account.AccountName}] {retryMessage}");
            }
            return false;
        }

        /// <summary>Clicks equalize when the settings ask for it. False: the button could not be clicked.</summary>
        private async Task<bool> ClickEqualizeIfEnabledAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            var huntSettings = GetAutoHuntSettings(account);
            if (!huntSettings.UseEqualize)
            {
                logger.LogInfo($"[{account.AccountName}] Equalize disabled - skipping equalize button");
                return true;
            }

            logger.LogInfo($"[{account.AccountName}] Equalize enabled - clicking equalize button");
            if (!await FindAndClickImageAsync("equalize.png", account.InstanceNumber, logger))
                return false;
            await Task.Delay(1000, cancellationToken);
            return true;
        }

        private async Task<TargetProcessResult> ProcessKingTarget(
            HuntTarget target,
            AccountSettings account,
            LogService logger,
            CancellationToken cancellationToken,
            string accountId)
        {
            try
            {
                // Click king
                if (!await ClickRandomInRectAsync(account.InstanceNumber, logger, target.MatchLocation))
                    return TargetProcessResult.Failed("Failed to click king target");
                await Task.Delay(2000, cancellationToken);

                // Click view button
                var viewFailure = await ClickViewButtonAsync(target, account, logger, accountId, "king", true);
                if (viewFailure != null) return viewFailure;
                await Task.Delay(1500, cancellationToken);

                // Click attack button
                if (!await FindAndClickImageAsync("attack-button.png", account.InstanceNumber, logger))
                    return TargetProcessResult.Failed("Failed to click attack button");
                await Task.Delay(1000, cancellationToken);

                // Check prediction text before proceeding
                var prediction = await ReadPredictionText(account, logger);
                if (IsPredictedToFail(prediction))
                {
                    logger.LogInfo($"[{account.AccountName}] King attack predicted to fail - '{prediction}', blocking target area for session");
                    await BlockPredictedFailureAsync(target, account, logger, cancellationToken, accountId);
                    return TargetProcessResult.Failed("Prediction indicates failure");
                }

                // Look for deploy button or max march
                if (!await HandleDeployment(account, logger, cancellationToken))
                {
                    return TargetProcessResult.Failed("Deployment failed");
                }

                // Handle confirmation and stamina checks
                var staminaResult = await HandleConfirmationAndStamina(account, logger, cancellationToken);
                if (staminaResult.StaminaDepleted)
                {
                    return TargetProcessResult.StaminaEmpty("Stamina depleted during king attack");
                }

                await Task.Delay(2000, cancellationToken);

                // Final max march check
                if (await CheckMaxMarchDirectly(account, logger))
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

        private async Task<TargetProcessResult> ProcessBearTarget(
            HuntTarget target,
            AutoHuntSessionState sessionState,
            AccountSettings account,
            LogService logger,
            CancellationToken cancellationToken,
            string accountId)
        {
            try
            {
                // Click bear
                if (!await ClickRandomInRectAsync(account.InstanceNumber, logger, target.MatchLocation))
                    return TargetProcessResult.Failed("Failed to click bear target");
                await Task.Delay(1000, cancellationToken);

                // Click view button
                var viewFailure = await ClickViewButtonAsync(target, account, logger, accountId, "bear", true);
                if (viewFailure != null) return viewFailure;
                await Task.Delay(1000, cancellationToken);

                // Click attack button
                if (!await FindAndClickImageAsync("attack-button.png", account.InstanceNumber, logger))
                    return TargetProcessResult.Failed("Failed to click attack button");
                await Task.Delay(1000, cancellationToken);

                // Check prediction text before equalize
                var initialPrediction = await ReadPredictionText(account, logger);
                if (IsPredictedToFail(initialPrediction))
                {
                    sessionState.CanAttackBear = false;
                    logger.LogInfo($"[{account.AccountName}] Bear attack predicted to fail before equalize - '{initialPrediction}', blocking target area for session");
                    await BlockPredictedFailureAsync(target, account, logger, cancellationToken, accountId);
                    return TargetProcessResult.Failed("Prediction indicates failure before equalize");
                }

                // Click equalize (if enabled in settings)
                if (!await ClickEqualizeIfEnabledAsync(account, logger, cancellationToken))
                    return TargetProcessResult.Failed("Failed to click equalize button");

                // Check prediction text after equalize
                var prediction = await ReadPredictionText(account, logger);
                if (IsPredictedToFail(prediction))
                {
                    sessionState.CanAttackBear = false;
                    logger.LogInfo($"[{account.AccountName}] Bear attack predicted to fail after equalize - '{prediction}', blocking target area for session");
                    await BlockPredictedFailureAsync(target, account, logger, cancellationToken, accountId);
                    return TargetProcessResult.Failed("Prediction indicates failure after equalize");
                }

                // Click deploy
                if (!await FindAndClickImageAsync("deploy-button.png", account.InstanceNumber, logger, threshold: 0.6, searchArea: AutoHuntConstants.DeployButtonArea))
                    return TargetProcessResult.Failed("Failed to click deploy button");

                // Handle confirmation and stamina checks
                var staminaResult = await HandleConfirmationAndStamina(account, logger, cancellationToken);
                if (staminaResult.StaminaDepleted)
                {
                    return TargetProcessResult.StaminaEmpty("Stamina depleted during bear attack");
                }

                await Task.Delay(2000, cancellationToken);

                // Final max march check
                if (await CheckMaxMarchDirectly(account, logger))
                {
                    logger.LogInfo($"[{account.AccountName}] Max march detected after bear deployment");
                }

                return TargetProcessResult.Successful("Bear target processed successfully");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error processing bear target: {ex.Message}");
                return TargetProcessResult.Failed($"Exception: {ex.Message}");
            }
        }

        private async Task<TargetProcessResult> ProcessScoutTarget(
            HuntTarget target,
            AccountSettings account,
            LogService logger,
            CancellationToken cancellationToken,
            string accountId)
        {
            try
            {
                // Click scout
                if (!await ClickRandomInRectAsync(account.InstanceNumber, logger, target.MatchLocation))
                    return TargetProcessResult.Failed("Failed to click scout target");
                await Task.Delay(1000, cancellationToken);

                // Click view button
                var viewFailure = await ClickViewButtonAsync(target, account, logger, accountId, "scout", false);
                if (viewFailure != null) return viewFailure;
                await Task.Delay(1000, cancellationToken);

                // Click rescue
                if (!await FindAndClickImageAsync("rescue.png", account.InstanceNumber, logger))
                    return TargetProcessResult.Failed("Failed to click rescue button");

                // Check for stamina
                await Task.Delay(1000, cancellationToken);
                if (await IsStaminaDepletedAsync(account, logger, "Stamina claimed, retrying scout rescue"))
                {
                    return TargetProcessResult.StaminaEmpty("Stamina depleted during scout rescue");
                }

                logger.LogInfo($"[{account.AccountName}] No stamina-low detected after rescue, continuing module");
                return TargetProcessResult.Successful("Scout target processed successfully");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error processing scout target: {ex.Message}");
                return TargetProcessResult.Failed($"Exception: {ex.Message}");
            }
        }

        private async Task<TargetProcessResult> ProcessAttackTarget(
            HuntTarget target,
            AutoHuntSessionState sessionState,
            AccountSettings account,
            LogService logger,
            CancellationToken cancellationToken,
            string accountId)
        {
            try
            {
                // Click attack
                if (!await ClickRandomInRectAsync(account.InstanceNumber, logger, target.MatchLocation))
                    return TargetProcessResult.Failed("Failed to click attack target");
                await Task.Delay(1000, cancellationToken);

                // Click view button
                var viewFailure = await ClickViewButtonAsync(target, account, logger, accountId, "attack", true);
                if (viewFailure != null) return viewFailure;
                await Task.Delay(1000, cancellationToken);

                // Click conquer
                if (!await FindAndClickImageAsync("conquer.png", account.InstanceNumber, logger))
                    return TargetProcessResult.Failed("Failed to click conquer button");
                await Task.Delay(1000, cancellationToken);

                // Check for stamina
                if (await IsStaminaDepletedAsync(account, logger, "Stamina claimed, retrying attack deployment"))
                {
                    return TargetProcessResult.StaminaEmpty("Stamina depleted during attack");
                }

                // Try to find and click quick-deploy
                if (!await FindAndClickQuickDeployDirectly(account, logger, cancellationToken))
                    return TargetProcessResult.Failed("Failed to find quick deploy");
                await Task.Delay(1000, cancellationToken);

                // Click fight
                if (!await FindAndClickImageAsync("fight.png", account.InstanceNumber, logger))
                    return TargetProcessResult.Failed("Failed to click fight button");

                // Wait for battle result
                await Task.Delay(3000, cancellationToken);

                var battleResult = await WaitForBattleResultAsync(account, logger, cancellationToken);
                if (battleResult == null)
                {
                    logger.LogError($"[{account.AccountName}] Could not detect battle result via OCR after 20 seconds");
                    return TargetProcessResult.Failed("Could not detect battle result");
                }
                bool wasVictory = battleResult.Value;

                // Click result area
                await ClickRandomInRectAsync(account.InstanceNumber, logger, AutoHuntConstants.VictoryDefeatClickArea);

                if (!wasVictory)
                {
                    sessionState.CanAttackAttack = false;
                    logger.LogInfo($"[{account.AccountName}] Attack target defeated, disabling for this session");
                }

                return wasVictory ? 
                    TargetProcessResult.Successful("Attack target victory") : 
                    TargetProcessResult.Failed("Attack target defeat");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error processing attack target: {ex.Message}");
                return TargetProcessResult.Failed($"Exception: {ex.Message}");
            }
        }

        /// <summary>Reads the battle result text via OCR for up to 20 seconds. True: victory, false: defeat, null: no result seen.</summary>
        private async Task<bool?> WaitForBattleResultAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            DateTime startTime = DateTime.UtcNow;

            logger.LogInfo($"[{account.AccountName}] Waiting 5 seconds before checking battle result...");
            await Task.Delay(5000, cancellationToken);

            logger.LogInfo($"[{account.AccountName}] Now checking for battle result using OCR (up to 20 seconds)...");

            // Battle result text area: 211,360 to 515,454
            var battleResultArea = new Rectangle(211, 360, 304, 94);

            int checkCount = 0;
            while ((DateTime.UtcNow - startTime).TotalSeconds < 20)
            {
                checkCount++;
                var battleScreenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (battleScreenshot == null) continue;

                var result = ReadBattleResult(account, logger, battleScreenshot, battleResultArea, checkCount, startTime);
                if (result.HasValue) return result;

                // Log progress every 3 checks or every 2 seconds
                if (checkCount % 3 == 0 || (DateTime.UtcNow - startTime).TotalSeconds >= checkCount * 2)
                {
                    var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                    logger.LogInfo($"[{account.AccountName}] Battle result OCR progress: {elapsed:F1}s elapsed, {checkCount} checks completed");
                }

                await Task.Delay(500, cancellationToken);
            }
            return null;
        }

        private static bool? ReadBattleResult(AccountSettings account, LogService logger, byte[] battleScreenshot, Rectangle battleResultArea, int checkCount, DateTime startTime)
        {
            try
            {
                var ocrService = new OcrService(logger);
                var text = ocrService.ExtractTextFromScreenArea(battleScreenshot, battleResultArea);
                if (string.IsNullOrEmpty(text))
                {
                    return null;
                }

                text = text.ToLower().Trim();
                logger.LogInfo($"[{account.AccountName}] Battle result OCR check #{checkCount}: '{text}'");

                // Check for victory text
                if (text.Contains("victory"))
                {
                    logger.LogInfo($"[{account.AccountName}] ✅ Victory detected via OCR: '{text}' after {checkCount} checks in {(DateTime.UtcNow - startTime).TotalSeconds:F1}s");
                    return true;
                }

                // Check for defeat text
                if (text.Contains("defeat"))
                {
                    logger.LogInfo($"[{account.AccountName}] ❌ Defeat detected via OCR: '{text}' after {checkCount} checks in {(DateTime.UtcNow - startTime).TotalSeconds:F1}s");
                    return false;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning($"[{account.AccountName}] OCR error on check #{checkCount}: {ex.Message}");
            }
            return null;
        }

        #endregion

        #region Target Detection

        private async Task<List<HuntTarget>> DetectAllTargetsDirectly(
            AccountSettings account,
            LogService logger,
            CancellationToken cancellationToken,
            string accountId)
        {
            var targets = new List<HuntTarget>();
            var bestConfidences = new Dictionary<string, List<(Rectangle location, double confidence)>>();

            // Get settings for blocked areas
            var settings = GetAutoHuntSettings(account);
            var blockedAreas = settings.GetUsedTargetAreas(accountId);
            LogBlockedAreas(account, logger, accountId, blockedAreas);

            // First check if we're in hunt mode
            logger.LogInfo($"[{account.AccountName}] Waiting up to 5 seconds for hunt mode indicators...");
            bool huntModeFound = await WaitForHuntMode(account, logger, cancellationToken);

            if (!huntModeFound)
            {
                logger.LogInfo($"[{account.AccountName}] No hunt mode indicators found (stamina or compass), skipping target detection");
                return targets;
            }

            logger.LogInfo($"[{account.AccountName}] Found hunt mode indicators, processing targets...");

            // Process ticks first if we're in hunt mode
            bool foundAnyTick = await ProcessTicks(account, logger, cancellationToken, accountId, settings);

            // Scan for targets
            await ScanForTargets(account, logger, cancellationToken, bestConfidences, huntModeFound);

            // Process the best detections
            targets = ProcessBestDetections(account, logger, bestConfidences, settings, accountId);

            // Handle case where no targets found
            if (!targets.Any())
            {
                await HandleNoTargetsFound(account, logger, cancellationToken, bestConfidences, foundAnyTick);
            }
            else
            {
                logger.LogInfo($"[{account.AccountName}] Found {targets.Count} valid targets after scanning");
            }

            return targets;
        }

        private async Task<bool> WaitForHuntMode(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            bool huntModeFound = false;
            DateTime startTime = DateTime.UtcNow;

            while ((DateTime.UtcNow - startTime).TotalSeconds < 5 && !huntModeFound && !cancellationToken.IsCancellationRequested)
            {
                huntModeFound = await IsInHuntMode(account, logger);
                if (!huntModeFound)
                {
                    await Task.Delay(500, cancellationToken);
                }
            }

            return huntModeFound;
        }

        private async Task<bool> IsInHuntMode(AccountSettings account, LogService logger)
        {
            var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
            if (screenshot == null) return false;

            // Check for castle-hunt icon
            var (castleHuntFound, _, castleHuntConf) = _templateMatcher.MatchTemplate(
                screenshot,
                Path.Combine(ImageTemplateFolder, "castle-hunt.png"),
                account.InstanceNumber,
                threshold: 0.8,  // High confidence threshold as requested
                searchArea: AutoHuntConstants.TargetSearchArea  // Use full target search area
            );

            logger.LogInfo($"[{account.AccountName}] Castle-hunt icon search: found={castleHuntFound}, confidence={castleHuntConf:F3}, area={AutoHuntConstants.TargetSearchArea}");

            if (castleHuntFound)
            {
                logger.LogInfo($"[{account.AccountName}] Found castle-hunt icon with confidence {castleHuntConf:F3} - confirmed in hunt mode");
                return true;
            }

            // The castle sprite changes with the castle skin/level, the stamina meat icon in the top-right header does not.
            // hunt-stamina.png is a copy of stamina.png: that name is forced through the colour matcher, which scores ~0.2 here
            var (staminaFound, _, staminaConf) = _templateMatcher.MatchTemplate(
                screenshot,
                Path.Combine(ImageTemplateFolder, "hunt-stamina.png"),
                account.InstanceNumber,
                threshold: 0.8,
                searchArea: new Rectangle(480, 0, 240, 90)
            );

            logger.LogInfo($"[{account.AccountName}] Stamina icon search: found={staminaFound}, confidence={staminaConf:F3}");
            if (staminaFound)
            {
                logger.LogInfo($"[{account.AccountName}] Found stamina icon - confirmed in hunt mode");
                return true;
            }

            return false;
        }

        private async Task<bool> ProcessTicks(
            AccountSettings account, 
            LogService logger, 
            CancellationToken cancellationToken, 
            string accountId, 
            AutoHuntSettings settings)
        {
            logger.LogInfo($"[{account.AccountName}] Looking for ticks to process (scanning up to 3 seconds)...");
            bool foundTick;
            bool foundAnyTick = false;
            DateTime tickScanStart = DateTime.UtcNow;
            TimeSpan timeSinceLastTick = TimeSpan.Zero;

            do
            {
                foundTick = false;
                var tickScreenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (tickScreenshot == null) break;

                var (found, matchRect, confidence) = _templateMatcher.MatchTemplate(
                    tickScreenshot,
                    Path.Combine(ImageTemplateFolder, "tick.png"),
                    account.InstanceNumber,
                    threshold: 0.8,
                    searchArea: AutoHuntConstants.TargetSearchArea
                );

                if (found)
                {
                    foundTick = true;
                    foundAnyTick = true;
                    await ProcessTickFound(account, logger, matchRect, confidence, accountId, settings, cancellationToken);
                    await Task.Delay(500, cancellationToken);
                    tickScanStart = DateTime.UtcNow;
                }
                else
                {
                    await Task.Delay(200, cancellationToken);
                }

                timeSinceLastTick = DateTime.UtcNow - tickScanStart;
            } while ((foundTick || timeSinceLastTick.TotalSeconds < 1.5) && !cancellationToken.IsCancellationRequested);

            if (!foundTick)
            {
                logger.LogInfo($"[{account.AccountName}] No ticks found after scanning for {timeSinceLastTick.TotalSeconds:F1} seconds");
            }

            return foundAnyTick;
        }

        private async Task ProcessTickFound(
            AccountSettings account, 
            LogService logger, 
            Rectangle matchRect, 
            double confidence, 
            string accountId, 
            AutoHuntSettings settings, 
            CancellationToken cancellationToken)
        {
            logger.LogInfo($"[{account.AccountName}] Found tick at {matchRect} with confidence {confidence:F3}");

            if (await ClickRandomInRectAsync(account.InstanceNumber, logger, matchRect))
            {
                logger.LogInfo($"[{account.AccountName}] Successfully clicked tick");
                await Task.Delay(500, cancellationToken);
                
                // Remove overlapping used areas
                RemoveOverlappingUsedAreas(account, logger, matchRect, accountId, settings);
                
                // Click the confirm point
                if (await ClickAsync(account.InstanceNumber, logger, AutoHuntConstants.TickConfirmPoint))
                {
                    logger.LogInfo($"[{account.AccountName}] Clicked tick confirm point");
                }
            }
        }

        private static void RemoveOverlappingUsedAreas(
            AccountSettings account, 
            LogService logger, 
            Rectangle matchRect, 
            string accountId, 
            AutoHuntSettings settings)
        {
            var tickArea = new Rectangle(
                matchRect.X - AutoHuntConstants.TARGET_AREA_PADDING,
                matchRect.Y - AutoHuntConstants.TARGET_AREA_PADDING,
                matchRect.Width + (AutoHuntConstants.TARGET_AREA_PADDING * 2),
                matchRect.Height + (AutoHuntConstants.TARGET_AREA_PADDING * 2)
            );

            var usedAreas = settings.GetUsedTargetAreas(accountId);
            var overlappingAreas = usedAreas.Where(used => 
                !(tickArea.Left > used.Right || 
                  tickArea.Right < used.Left || 
                  tickArea.Top > used.Bottom || 
                  tickArea.Bottom < used.Top)).ToList();

            foreach (var area in overlappingAreas)
            {
                settings.RemoveUsedTargetArea(accountId, area);
                logger.LogInfo($"[{account.AccountName}] Removed used area restriction at {area} due to tick click");
            }
            
            SaveAutoHuntSettings(account, settings);
        }

        private static void RecordDetection(
            AccountSettings account, LogService logger,
            Dictionary<string, List<(Rectangle location, double confidence)>> bestConfidences,
            string imageName, Rectangle matchRect, double confidence)
        {
            if (!bestConfidences.ContainsKey(imageName))
            {
                bestConfidences[imageName] = new List<(Rectangle, double)>();
            }

            // Check if we already have a similar location
            bool isDuplicate = bestConfidences[imageName].Any(existing =>
                Math.Abs(existing.location.X - matchRect.X) < AutoHuntConstants.TARGET_AREA_PADDING &&
                Math.Abs(existing.location.Y - matchRect.Y) < AutoHuntConstants.TARGET_AREA_PADDING);

            if (!isDuplicate)
            {
                bestConfidences[imageName].Add((matchRect, confidence));
                logger.LogInfo($"[{account.AccountName}] Found potential {imageName} at {matchRect} with confidence {confidence:F3}");
            }
        }

        private async Task ScanForTargets(
            AccountSettings account, 
            LogService logger, 
            CancellationToken cancellationToken,
            Dictionary<string, List<(Rectangle location, double confidence)>> bestConfidences, 
            bool huntModeFound)
        {
            logger.LogInfo($"[{account.AccountName}] Finished processing ticks, now scanning for targets (up to 5 seconds)...");

            // king.png is still scanned, but only so that ProcessBestDetections can exclude it (and anything on top of it):
            // the six-sided King item must never be clicked.
            var targetTypes = new[]
            {
                ("king.png", true),
                ("bear.png", true),
                ("bear-elite.png", true), // orange sparkling beast pin: same hunt flow as a normal bear
                ("scout.png", false),
                ("attack.png", false)
            };

            DateTime startTime = DateTime.UtcNow;
            while ((DateTime.UtcNow - startTime).TotalSeconds < 5 && !cancellationToken.IsCancellationRequested)
            {
                var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshot == null) continue;

                foreach (var (imageName, _) in targetTypes)
                {
                    // Skip certain targets if not in hunt mode
                    if (!huntModeFound && (imageName == "king.png" || imageName == "scout.png" || imageName == "attack.png"))
                    {
                        continue;
                    }

                    var (_, matchRect, confidence) = _templateMatcher.MatchTemplate(
                        screenshot,
                        Path.Combine(ImageTemplateFolder, imageName),
                        account.InstanceNumber,
                        threshold: 0.8, // real pins score 0.91-0.99; a 0.695 "scout" was a miss-tap
                        searchArea: AutoHuntConstants.TargetSearchArea
                    );

                    // Track all detections with confidence > 0.5
                    if (confidence > 0.5)
                    {
                        RecordDetection(account, logger, bestConfidences, imageName, matchRect, confidence);
                    }
}

                // If we have found at least one high-confidence target, we can break early
                if (bestConfidences.Any(kv => kv.Value.Any(v => v.confidence >= 0.8)))
                {
                    break;
                }

                await Task.Delay(200, cancellationToken);
            }
        }

        private List<HuntTarget> ProcessBestDetections(
            AccountSettings account, 
            LogService logger,
            Dictionary<string, List<(Rectangle location, double confidence)>> bestConfidences,
            AutoHuntSettings settings, 
            string accountId)
        {
            var targets = new List<HuntTarget>();
            var targetTypes = new[]
            {
                ("king.png", true),
                ("bear.png", true),
                ("bear-elite.png", true), // orange sparkling beast pin: same hunt flow as a normal bear
                ("scout.png", false),
                ("attack.png", false)
            };

            // Where the (six-sided) King item is on screen: never click it, nor anything overlapping it.
            // Small margin only: with 25 px a bear 18 px next to the King was skipped every scan
            var kingZones = bestConfidences
                .Where(kv => kv.Key == "king.png")
                .SelectMany(kv => kv.Value)
                .Where(d => d.confidence >= 0.6)
                .Select(d => Rectangle.Inflate(d.location, 5, 5))
                .ToList();

            foreach (var (imageName, detections) in bestConfidences)
            {
                if (imageName == "king.png")
                {
                    if (detections.Count > 0) logger.LogInfo($"[{account.AccountName}] King item detected - ignoring it (never clicked)");
                    continue;
                }

                var sortedDetections = detections.OrderByDescending(d => d.confidence).ToList();

                foreach (var (matchRect, confidence) in sortedDetections)
                {
                    if (confidence < 0.8) continue;

                    if (kingZones.Any(z => z.IntersectsWith(matchRect)))
                    {
                        logger.LogInfo($"[{account.AccountName}] Skipping {imageName} at {matchRect}: it overlaps the King item");
                        continue;
                    }

                    var targetArea = new Rectangle(
                        matchRect.X - AutoHuntConstants.TARGET_AREA_PADDING,
                        matchRect.Y - AutoHuntConstants.TARGET_AREA_PADDING,
                        matchRect.Width + (AutoHuntConstants.TARGET_AREA_PADDING * 2),
                        matchRect.Height + (AutoHuntConstants.TARGET_AREA_PADDING * 2)
                    );

                    var usedAreas = settings.GetUsedTargetAreas(accountId);
                    bool areaAlreadyUsed = IsAreaAlreadyUsed(targetArea, usedAreas);

                    if (!areaAlreadyUsed)
                    {
                        logger.LogInfo($"[{account.AccountName}] Adding valid {imageName} at {matchRect} with confidence {confidence:F3}");
                        targets.Add(new HuntTarget
                        {
                            Type = imageName.Replace(".png", ""),
                            RequiresMarch = targetTypes.First(t => t.Item1 == imageName).Item2,
                            MatchLocation = matchRect,
                            TargetArea = targetArea,
                            Confidence = confidence
                        });
                    }
                    else
                    {
                        LogBlockedTarget(account, logger, imageName, matchRect, targetArea, usedAreas, accountId);
                    }
                }
            }

            return targets;
        }

        private static bool IsAreaAlreadyUsed(Rectangle targetArea, List<Rectangle> usedAreas)
        {
            return usedAreas.Any(used => 
                !(targetArea.Left > used.Right || 
                  targetArea.Right < used.Left || 
                  targetArea.Top > used.Bottom || 
                  targetArea.Bottom < used.Top));
        }

        private static void LogBlockedTarget(
            AccountSettings account, 
            LogService logger, 
            string imageName, 
            Rectangle matchRect, 
            Rectangle targetArea, 
            List<Rectangle> usedAreas, 
            string accountId)
        {
            logger.LogInfo($"[{account.AccountName}] BLOCKED: Skipping {imageName} at {matchRect} (blocked area: {targetArea}) - overlaps with previously clicked area for account {accountId}");
            
            var overlappingArea = usedAreas.FirstOrDefault(used => 
                !(targetArea.Left > used.Right || 
                  targetArea.Right < used.Left || 
                  targetArea.Top > used.Bottom || 
                  targetArea.Bottom < used.Top));
            
            if (overlappingArea != Rectangle.Empty)
            {
                logger.LogInfo($"[{account.AccountName}] -> Overlaps with used area: {overlappingArea}");
            }
        }

        private async Task HandleNoTargetsFound(
            AccountSettings account, 
            LogService logger, 
            CancellationToken cancellationToken,
            Dictionary<string, List<(Rectangle location, double confidence)>> bestConfidences, 
            bool foundAnyTick)
        {
            // Log potential targets that didn't meet the threshold
            var lowConfidenceTargets = bestConfidences
                .SelectMany(kv => kv.Value.Select(v => (type: kv.Key, location: v.location, confidence: v.confidence)))
                .OrderByDescending(t => t.confidence)
                .ToList();

            if (lowConfidenceTargets.Any())
            {
                logger.LogInfo($"[{account.AccountName}] No valid targets found, but detected these potential targets (confidence < 0.8):");
                foreach (var (type, location, confidence) in lowConfidenceTargets)
                {
                    logger.LogInfo($"[{account.AccountName}] - {type} at {location} with confidence {confidence:F3}");
                }
            }
            else
            {
                logger.LogInfo($"[{account.AccountName}] No targets detected at all during scanning period");
            }

            // If no targets found and no ticks were processed, check for level-up
            if (!foundAnyTick)
            {
                await HandleLevelUpPopup(account, logger, cancellationToken);
            }
        }

        private async Task HandleLevelUpPopup(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            logger.LogInfo($"[{account.AccountName}] No ticks or targets found, checking for level-up...");
            
            var levelUpScreenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
            if (levelUpScreenshot != null)
            {
                var (levelUpFound, _, _) = _templateMatcher.MatchTemplate(
                    levelUpScreenshot,
                    Path.Combine(ImageTemplateFolder, "level-up.png"),
                    account.InstanceNumber,
                    threshold: 0.6,
                    searchArea: AutoHuntConstants.LevelUpArea
                );

                if (levelUpFound)
                {
                    logger.LogInfo($"[{account.AccountName}] Level-up detected, clicking dismiss point");
                    if (await ClickAsync(account.InstanceNumber, logger, AutoHuntConstants.LevelUpDismissPoint))
                    {
                        logger.LogInfo($"[{account.AccountName}] Clicked level-up dismiss point");
                        await Task.Delay(500, cancellationToken);
                    }
                }
            }
        }

        private static void LogBlockedAreas(AccountSettings account, LogService logger, string accountId, List<Rectangle> blockedAreas)
        {
            if (blockedAreas.Any())
            {
                logger.LogInfo($"[{account.AccountName}] Currently blocked areas for account {accountId}: {blockedAreas.Count} areas");
                foreach (var area in blockedAreas)
                {
                    logger.LogInfo($"[{account.AccountName}] -> Blocked: {area}");
                }
            }
            else
            {
                logger.LogInfo($"[{account.AccountName}] No blocked areas for account {accountId}");
            }
        }

        #endregion

        #region March Management

        private const int MaxMarchQueues = 6;

        /// <summary>
        /// Available marches from the OCR text ("3/5", "3"): digits and '/' are kept, the first number counts. The map HUD has no
        /// march counter at that spot (the box sits on the VIP badge), so noise like '77' is capped at the game's queue limit;
        /// the max-march popup catches the rest. Null when no number can be read.
        /// </summary>
        internal static int? ParseMarchCount(string? text)
        {
            var cleanText = new string((text ?? "").Where(c => char.IsDigit(c) || c == '/').ToArray());
            if (cleanText.Length == 0 || !int.TryParse(cleanText.Split('/')[0], out var marches)) return null;
            return Math.Min(marches, MaxMarchQueues);
        }

        private async Task<int> GetAvailableMarchesDirectly(AccountSettings account, LogService logger)
        {
            var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
            if (screenshot == null) return 0;

            // Use OCR to read march count from UI
            try
            {
                var ocrService = new OcrService(logger);
                var text = ocrService.ExtractTextFromScreenArea(screenshot, AutoHuntConstants.MarchCountArea);
                
                logger.LogInfo($"[{account.AccountName}] OCR extracted text: '{text}' from area {AutoHuntConstants.MarchCountArea}");
                
                var cleanText = new string(text.Where(c => char.IsDigit(c) || c == '/').ToArray());
                var marches = ParseMarchCount(text);
                if (marches.HasValue)
                {
                    if (marches.Value == MaxMarchQueues && int.TryParse(cleanText.Split('/')[0], out var raw) && raw > MaxMarchQueues)
                        logger.LogInfo($"[{account.AccountName}] OCR march count {raw} is not plausible, using {MaxMarchQueues}");
                    else
                        logger.LogInfo($"[{account.AccountName}] Successfully parsed {marches.Value} marches from OCR text: '{cleanText}'");
                    return marches.Value;
                }

                logger.LogWarning($"[{account.AccountName}] Could not parse march count from OCR: '{text}' (cleaned: '{cleanText}')");
                
                // Fallback: assume at least 1 march is available if we can't read it
                logger.LogInfo($"[{account.AccountName}] Using fallback: assuming 1 march available");
                return 1;
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error reading march count: {ex.Message}");
                // Fallback: assume at least 1 march is available
                logger.LogInfo($"[{account.AccountName}] Using error fallback: assuming 1 march available");
                return 1;
            }
        }

        private async Task<int> WaitForAvailableMarchesDirectly(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            DateTime startTime = DateTime.UtcNow;
            const int maxWaitSeconds = 120;
            int availableMarches = 0;

            while ((DateTime.UtcNow - startTime).TotalSeconds < maxWaitSeconds && !cancellationToken.IsCancellationRequested)
            {
                await WaitIfPausedAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested) return -1;

                availableMarches = await GetAvailableMarchesDirectly(account, logger);
                
                if (availableMarches > 0)
                {
                    logger.LogInfo($"[{account.AccountName}] Found {availableMarches} available marches");
                    return availableMarches;
                }

                // Check for stamina depletion while waiting
                var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshot != null)
                {
                    var staminaResult = await CheckForStaminaLowDirectly(screenshot, account, logger);
                    if (staminaResult == StaminaCheckResult.StaminaDepleted)
                    {
                        return -1; // Signal stamina depletion
                    }
                }

                logger.LogInfo($"[{account.AccountName}] No marches available, waiting... ({(DateTime.UtcNow - startTime).TotalSeconds:F0}s elapsed)");
                await Task.Delay(5000, cancellationToken);
            }

            logger.LogWarning($"[{account.AccountName}] Timeout waiting for marches after {maxWaitSeconds} seconds");
            return 0;
        }

        private async Task<bool> CheckMaxMarchDirectly(AccountSettings account, LogService logger)
        {
            var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
            if (screenshot == null) return false;

            var (maxMarchFound, _, _) = _templateMatcher.MatchTemplate(
                screenshot,
                Path.Combine(ImageTemplateFolder, "max-march.png"),
                account.InstanceNumber,
                threshold: 0.6
            );

            if (maxMarchFound)
            {
                logger.LogInfo($"[{account.AccountName}] Max march detected, clicking dismiss area");
                await ClickRandomInRectAsync(account.InstanceNumber, logger, AutoHuntConstants.MaxMarchClickArea);
                
                var settings = GetAutoHuntSettings(account);
                settings.LastMaxMarchTime = DateTime.UtcNow;
                SaveAutoHuntSettings(account, settings);
                
                return true;
            }

            return false;
        }

        private async Task<bool> FindAndClickQuickDeployDirectly(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            logger.LogInfo($"[{account.AccountName}] Looking for quick-deploy button (waiting up to 3 seconds)...");

            DateTime startTime = DateTime.UtcNow;
            bool quickDeployFound = false;
            Rectangle deployRect = Rectangle.Empty;

            while ((DateTime.UtcNow - startTime).TotalSeconds < 3 && !quickDeployFound && !cancellationToken.IsCancellationRequested)
            {
                var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshot == null)
                {
                    await Task.Delay(200, cancellationToken);
                    continue;
                }

                var (found, rect, confidence) = _templateMatcher.MatchTemplate(
                    screenshot,
                    Path.Combine(ImageTemplateFolder, "quick-deploy.png"),
                    account.InstanceNumber,
                    threshold: 0.6,
                    searchArea: AutoHuntConstants.QuickDeployArea
                );

                if (found)
                {
                    quickDeployFound = true;
                    deployRect = rect;
                    logger.LogInfo($"[{account.AccountName}] Found quick-deploy button with confidence {confidence:F3} after {(DateTime.UtcNow - startTime).TotalSeconds:F1}s");
                    break;
                }

                // Log progress every second
                var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                if (elapsed >= 1 && elapsed % 1 < 0.2) // Log roughly every second
                {
                    logger.LogInfo($"[{account.AccountName}] Still looking for quick-deploy button... ({elapsed:F1}s elapsed)");
                }

                await Task.Delay(200, cancellationToken);
            }

            if (quickDeployFound)
            {
                logger.LogInfo($"[{account.AccountName}] Found quick-deploy button, clicking");
                return await ClickRandomInRectAsync(account.InstanceNumber, logger, deployRect);
            }

            logger.LogWarning($"[{account.AccountName}] Quick-deploy button not found after 3 seconds");
            return false;
        }

        #endregion

        #region Stamina Management

        /// <summary>Why the OCR text says the stamina is gone, or null when it does not.</summary>
        private static string? FindStaminaDepletion(string text)
        {
            // Check for traditional stamina low messages
            if (text.Contains("stamina") && text.Contains("low"))
            {
                return "Stamina low detected";
            }

            if (text.Contains("not enough") || text.Contains("insufficient"))
            {
                return "Insufficient stamina detected";
            }

            // Check for stamina restoration/replenishment dialogs
            if (text.Contains("stamina-replenishing") ||
                text.Contains("restore") && text.Contains("stamina") ||
                text.Contains("governor stamina") ||
                text.Contains("storehouse feast"))
            {
                return "Stamina restoration dialog detected";
            }
            return null;
        }

        private static async Task<StaminaCheckResult> CheckForStaminaLowDirectly(
            byte[]? screenshot,
            AccountSettings account,
            LogService logger)
        {
            if (screenshot == null) return StaminaCheckResult.NoStaminaIssue;

            try
            {
                var ocrService = new OcrService(logger);

                // Create full screen rectangle instead of using limited area
                // Standard emulator resolution is 720x1280, but we'll use a large rectangle to cover the full screen
                var fullScreenArea = new Rectangle(0, 0, 720, 1280);
                var text = ocrService.ExtractTextFromScreenArea(screenshot, fullScreenArea);

                if (!string.IsNullOrEmpty(text))
                {
                    text = text.ToLower().Trim();
                    logger.LogInfo($"[{account.AccountName}] Full screen OCR text for stamina check: '{text}'");

                    var depletionReason = FindStaminaDepletion(text);
                    if (depletionReason != null)
                    {
                        logger.LogWarning($"[{account.AccountName}] {depletionReason}: '{text}'");
                        return StaminaCheckResult.StaminaDepleted;
                    }
                }

                return StaminaCheckResult.NoStaminaIssue;
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error checking stamina: {ex.Message}");
                return StaminaCheckResult.NoStaminaIssue;
            }
        }

        #endregion

        #region Hunt Mode Navigation


        private async Task<bool> EnterHuntModeDirectly(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            logger.LogInfo($"[{account.AccountName}] Looking for hunt button...");
            if (await WaitForImageAsync("hunt-button.png", account.InstanceNumber, logger, cancellationToken,
                timeoutMs: 2000, threshold: 0.6, searchArea: AutoHuntConstants.HuntButtonArea))
            {
                // Found the hunt button, now click it
                if (!await FindAndClickImageAsync("hunt-button.png", account.InstanceNumber, logger,
                    threshold: 0.6, searchArea: AutoHuntConstants.HuntButtonArea))
                {
                    logger.LogError($"[{account.AccountName}] Found hunt button but failed to click it");
                    return false;
                }
                return true;
            }
            else
            {
                logger.LogError($"[{account.AccountName}] Error: Failed to find hunt-button.png, calling locator module for recovery");
                var huntLocator = new LocatorService(logger, account);
                try
                {
                    await huntLocator.EnsureViewAsync(ViewType.MapView, account.InstanceNumber, cancellationToken);
                    logger.LogInfo($"[{account.AccountName}] Locator service completed view recovery, retrying hunt button detection");
                    
                    // Retry after locator service with timeout
                    if (await WaitForImageAsync("hunt-button.png", account.InstanceNumber, logger, cancellationToken,
                        timeoutMs: 2000, threshold: 0.6, searchArea: AutoHuntConstants.HuntButtonArea))
                    {
                        // Found the hunt button, now click it
                        if (!await FindAndClickImageAsync("hunt-button.png", account.InstanceNumber, logger,
                            threshold: 0.6, searchArea: AutoHuntConstants.HuntButtonArea))
                        {
                            logger.LogError($"[{account.AccountName}] Found hunt button after recovery but failed to click it");
                            return false;
                        }
                        return true;
                    }
                    else
                    {
                        logger.LogError($"[{account.AccountName}] Could not find hunt button even after locator recovery");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError($"[{account.AccountName}] Locator service failed: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// The agent with the monocle at the top left of the Intel screen adds a new batch of missions when tapped.
        /// </summary>
        private async Task TapIntelAgentAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            if (!GetAutoHuntSettings(account).TapIntelAgent) return;

            if (await FindAndClickImageAsync("intel-agent.png", account.InstanceNumber, logger,
                threshold: 0.8, searchArea: AutoHuntConstants.IntelAgentArea))
            {
                logger.LogInfo($"[{account.AccountName}] Tapped the Intel agent for extra missions");
                await Task.Delay(2500, cancellationToken);
            }
        }

        private async Task ReturnToMapViewDirectly(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            logger.LogInfo($"[{account.AccountName}] Returning to map view");
            
            // The hunt screen is closed with the back arrow in the top-left corner (close-details.png never existed)
            if (await FindAndClickImageAsync("back-arrow.png", account.InstanceNumber, logger, threshold: 0.8,
                searchArea: new Rectangle(0, 0, 120, 90)))
            {
                logger.LogInfo($"[{account.AccountName}] Clicked back arrow to leave the hunt screen");
                await Task.Delay(1000, cancellationToken);
            }
            
            // Use locator service to ensure map view
            var locator = new LocatorService(logger, account);
            await locator.EnsureViewAsync(ViewType.MapView, account.InstanceNumber, cancellationToken);
        }

        private async Task<bool> IsTargetMarchingDirectly(AccountSettings account, LogService logger)
        {
            var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
            if (screenshot == null) return false;

            // Check for "Marching" text in the marching text area
            try
            {
                var ocrService = new OcrService(logger);
                var text = ocrService.ExtractTextFromScreenArea(screenshot, AutoHuntConstants.MarchingTextArea);
                
                if (!string.IsNullOrEmpty(text) && text.ToLower().Contains("marching"))
                {
                    logger.LogInfo($"[{account.AccountName}] Detected marching text: '{text}'");
                    return true;
                }
                
                return false;
            }
            catch (Exception ex)
            {
                logger.LogWarning($"[{account.AccountName}] Error checking for marching text: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Helper Methods

        private async Task<bool> HandleDeployment(
            AccountSettings account, 
            LogService logger, 
            CancellationToken cancellationToken)
        {
            DateTime startTime = DateTime.UtcNow;
            bool buttonFound = false;
            
            while ((DateTime.UtcNow - startTime).TotalSeconds < 3 && !buttonFound && !cancellationToken.IsCancellationRequested)
            {
                var deployScreenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (deployScreenshot == null) continue;

                // Check for deploy button
                var (deployFound, deployRect, _) = _templateMatcher.MatchTemplate(
                    deployScreenshot,
                    Path.Combine(ImageTemplateFolder, "deploy-button.png"),
                    account.InstanceNumber,
                    threshold: 0.6,
                    searchArea: AutoHuntConstants.DeployButtonArea
                );

                if (deployFound)
                {
                    logger.LogInfo($"[{account.AccountName}] Found deploy button, proceeding with attack");
                    buttonFound = true;
                    if (!await ClickRandomInRectAsync(account.InstanceNumber, logger, deployRect))
                        return false;
                    break;
                }

                // Check for max march
                var (maxMarchFound, _, _) = _templateMatcher.MatchTemplate(
                    deployScreenshot,
                    Path.Combine(ImageTemplateFolder, "max-march.png"),
                    account.InstanceNumber,
                    threshold: 0.6
                );

                if (maxMarchFound)
                {
                    logger.LogInfo($"[{account.AccountName}] Max march detected, clicking dismiss area");
                    await ClickRandomInRectAsync(account.InstanceNumber, logger, AutoHuntConstants.MaxMarchClickArea);
                    var maxMarchSettings = GetAutoHuntSettings(account);
                    maxMarchSettings.LastMaxMarchTime = DateTime.UtcNow;
                    SaveAutoHuntSettings(account, maxMarchSettings);
                    return false;
                }

                await Task.Delay(200, cancellationToken);
            }

            return buttonFound;
        }

        private async Task<TargetProcessResult> HandleConfirmationAndStamina(
            AccountSettings account, 
            LogService logger, 
            CancellationToken cancellationToken)
        {
            await Task.Delay(1000, cancellationToken);
            var confirmScreenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
            if (confirmScreenshot != null)
            {
                var staminaResult = await CheckForStaminaLowDirectly(confirmScreenshot, account, logger);
                
                if (staminaResult == StaminaCheckResult.StaminaDepleted)
                {
                    return TargetProcessResult.StaminaEmpty("Stamina depleted");
                }
                else if (staminaResult == StaminaCheckResult.StaminaClaimed_Retry)
                {
                    logger.LogInfo($"[{account.AccountName}] Stamina claimed, continuing with deployment");
                }

                // Check for confirmation dialog
                var (confirmationFound, _, _) = _templateMatcher.MatchTemplate(
                    confirmScreenshot,
                    Path.Combine(ImageTemplateFolder, "confirmation.png"),
                    account.InstanceNumber,
                    threshold: 0.6
                );

                if (confirmationFound)
                {
                    logger.LogInfo($"[{account.AccountName}] Found confirmation dialog, clicking confirm points");
                    await ClickAsync(account.InstanceNumber, logger, new Point(212, 714));
                    await Task.Delay(500, cancellationToken);
                    await ClickAsync(account.InstanceNumber, logger, new Point(456, 770));
                    await Task.Delay(1000, cancellationToken);
                }
            }

            return TargetProcessResult.Successful("Confirmation handled");
        }

        private static async Task<string> GetAccountIdAsync(AccountSettings account)
        {
            // Use account name as ID since AccountDetectionTask is not available
            return $"{account.InstanceNumber}_{account.AccountName}";
        }

        private static AutoHuntSettings GetAutoHuntSettings(AccountSettings account)
        {
            
            if (!account.TaskSettings.TryGetValue("AutoHunt", out var settingsJson) || string.IsNullOrEmpty(settingsJson))
            {
                return new AutoHuntSettings();
            }

            try
            {
                return JsonSerializer.Deserialize<AutoHuntSettings>(settingsJson) ?? new AutoHuntSettings();
            }
            catch (Exception)
            {
                return new AutoHuntSettings();
            }
        }

        private static void SaveAutoHuntSettings(AccountSettings account, AutoHuntSettings settings)
        {
            var settingsJson = JsonSerializer.Serialize(settings);
            account.TaskSettings["AutoHunt"] = settingsJson;
            // Configuration is auto-saved via ConfigurationManager.Instance
        }

        private async Task<string> ReadPredictionText(AccountSettings account, LogService logger)
        {
            // Use OCR to read prediction text - placeholder for now
            var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
            if (screenshot != null)
            {
                try
                {
                    var ocrService = new OcrService(logger);
                    var text = ocrService.ExtractTextFromScreenArea(screenshot, AutoHuntConstants.OcrPredictionArea);
                    return text ?? string.Empty;
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"[{account.AccountName}] Failed to read prediction text: {ex.Message}");
                    return string.Empty;
                }
            }
            return string.Empty;
        }

        protected override Task OnInitializeAsync(LogService logger, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }


        #endregion
    }
}