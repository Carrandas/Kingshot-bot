using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.LDPlayer;
using Bot.Core.ImageDetection;
using Bot.Core.Services;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Threading;
using System;
using Bot.Core.Exceptions;
using System.Linq;
using System.IO;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// Handles the complete startup sequence for the game, including:
    /// 1. Finding and launching the Kingshot app
    /// 2. Handling loading screens
    /// 3. Dismissing pop-ups and ads
    /// 4. Verifying the game state
    /// </summary>
    public class StartupTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.Startup;
        public override string Name => "Startup Check";
        
        // Specifies the folder containing image templates for startup sequence
        protected override string GetImageFolderName() => "startup";

        // Event for updating the GUI with current phase status
        public static event Action<int, string, string>? OnPhaseUpdate;
        
        private static readonly Rectangle LoadingScreenArea = new Rectangle(74, 173, 587, 197);  // Loading screen search area
        private static readonly Rectangle BaseViewDetectionArea = new Rectangle(589, 1157, 125, 108);  // Town/World button at the bottom right
        private static readonly Rectangle ReconnectButtonArea = new Rectangle(340, 720, 380, 160);     // Reconnect button of the "logged in on another device" dialog
        private const double ReconnectThreshold = 0.8;  // 1.00 on the dialog, 0.51 on its Contact Us button, 0.15 on the Welcome back popup
        
        /// <summary>
        /// Verifies if the LDPlayer instance is running before attempting startup
        /// Makes up to 3 attempts with 3-second delays between attempts
        /// </summary>
        public override async Task<bool> CanExecuteAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken = default)
        {
            logger.LogInfo($"[{account.AccountName}] 🔍 Checking if startup task can execute...");
            
            const int CHECK_DURATION_MS = 30000; // 30 seconds total
            const int CHECK_INTERVAL_MS = 3000;  // Check every 3 seconds
            var startTime = DateTime.UtcNow;
            var attemptCount = 0;
            
            // Startup task should always be able to execute if instance is running
            while ((DateTime.UtcNow - startTime).TotalMilliseconds < CHECK_DURATION_MS && !cancellationToken.IsCancellationRequested)
            {
                attemptCount++;
                logger.LogInfo($"[{account.AccountName}] Instance running check attempt {attemptCount} (elapsed time: {(DateTime.UtcNow - startTime).TotalSeconds:F1}s)...");
                
                var isRunning = await IsInstanceRunningAsync(account.InstanceNumber, logger, cancellationToken);
                if (isRunning)
                {
                    logger.LogInfo($"[{account.AccountName}] ✅ LDPlayer instance {account.InstanceNumber} is running");
                    return true;
                }
                
                logger.LogInfo($"[{account.AccountName}] ⏳ Instance not detected as running, waiting {CHECK_INTERVAL_MS/1000} seconds before retry...");
                await Task.Delay(CHECK_INTERVAL_MS, cancellationToken);
            }
            
            logger.LogError($"[{account.AccountName}] ❌ LDPlayer instance {account.InstanceNumber} is not running after {attemptCount} attempts ({(DateTime.UtcNow - startTime).TotalSeconds:F1}s)");
            return false;
        }
        
        /// <summary>
        /// Main startup sequence that handles all phases of game initialization:
        /// 1. Find and click Kingshot app
        /// 2. Handle loading screens
        /// 3. Handle main menu and pop-ups
        /// 4. Handle welcome back screen
        /// 5. Verify final game state
        /// </summary>
        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            logger.LogInfo($"[{account.AccountName}] 🔍 Starting complete startup sequence{(isReRun ? " (Re-run)" : "")}");
            UpdatePhaseStatus(account.InstanceNumber, account.AccountName, "Initializing");
            
            try
            {
                // Check for cancellation before starting
                if (cancellationToken.IsCancellationRequested)
                {
                    return CancelledResult(account, logger, "🛑 Startup sequence cancelled before start");
                }

                // Game already running on the base or map view: skip the app launch and its loading waits (~15 s)
                if (await IsInGameViewAsync(account, logger, cancellationToken))
                {
                    logger.LogInfo($"[{account.AccountName}] ✅ Game already running, skipping app launch and loading screen");
                    return await FinishStartupAsync(account, logger, cancellationToken, userNotifications);
                }

                // Phase 1: Find and click Kingshot app icon
                NotifyProgress(userNotifications, "Finding game app", 10);
                logger.LogInfo($"[{account.AccountName}] Starting Phase 1: Finding Kingshot app...");
                if (!await FindAndClickKingshotApp(account, logger, cancellationToken, userNotifications))
                {
                    logger.LogError($"[{account.AccountName}] ❌ Failed to find/click Kingshot app");
                    return FailDetection("Phase 1: Finding game app", "Kingshot app icon", recoveryNeeded: false);
                }

                // Check for cancellation after each major phase
                if (cancellationToken.IsCancellationRequested)
                {
                    return CancelledResult(account, logger, "🛑 Startup sequence cancelled after app launch");
                }

                // Phase 2: Wait for loading screen and let it disappear
                logger.LogInfo($"[{account.AccountName}] 🎯 Phase 2: Handling loading screen...");
                if (!await WaitForLoadingToCompleteInternal(account, logger, cancellationToken))
                {
                    // Non-critical, log and proceed if loading screen handling fails
                    logger.LogWarning($"[{account.AccountName}] ⚠️ Proceeding despite potential loading screen issues.");
                }

                // Check for cancellation
                if (cancellationToken.IsCancellationRequested)
                {
                    return CancelledResult(account, logger, "🛑 Startup sequence cancelled after loading screen");
                }

                // Post-loading loop: close ads, reconnect, until the base or map view shows
                var postLoad = await WaitForKnownViewAsync(account, logger, cancellationToken, userNotifications);

                if (cancellationToken.IsCancellationRequested)
                {
                    return CancelledResult(account, logger, "🛑 Startup sequence was cancelled");
                }

                if (!postLoad.InKnownView)
                {
                    logger.LogError($"[{account.AccountName}] ❌ Failed to reach a known view after {postLoad.Attempts} attempts ({(DateTime.UtcNow - postLoad.StartTime).TotalSeconds:F1}s)");
                    logger.LogInfo($"[{account.AccountName}] 🔄 Initiating recovery sequence...");
                    UpdatePhaseStatus(account.InstanceNumber, account.AccountName, "Recovery Needed");
                    return TaskExecutionDetails.FailedWith(
                        FailureCategory.GameState,
                        "Phase 3: Post-loading",
                        "Game did not reach a known view after loading",
                        customHint: "Game may be stuck on a dialog or ad - recovery will be attempted",
                        recoveryNeeded: true);
                }

                return await FinishStartupAsync(account, logger, cancellationToken, userNotifications);
            }
            catch (OperationCanceledException)
            {
                logger.LogInfo($"[{account.AccountName}] 🛑 Startup sequence was cancelled");
                UpdatePhaseStatus(account.InstanceNumber, account.AccountName, "Cancelled");
                return TaskExecutionDetails.Failed("Startup sequence was cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] 💥 Exception during startup sequence: {ex.Message}");
                logger.LogError($"[{account.AccountName}] Stack trace: {ex.StackTrace}");
                UpdatePhaseStatus(account.InstanceNumber, account.AccountName, "Error");
                return TaskExecutionDetails.FailedWith(
                    FailureCategory.Unknown,
                    "Startup error",
                    ex.Message,
                    recoveryNeeded: true);
            }
        }

        private static TaskExecutionDetails CancelledResult(AccountSettings account, LogService logger, string logMessage)
        {
            logger.LogInfo($"[{account.AccountName}] {logMessage}");
            UpdatePhaseStatus(account.InstanceNumber, account.AccountName, "Cancelled");
            return TaskExecutionDetails.Failed("Startup sequence was cancelled");
        }

        private sealed class PostLoadState
        {
            public DateTime StartTime = DateTime.UtcNow;
            public int Attempts;
            public bool InKnownView;
        }

        private const int POST_LOAD_DURATION_MS = 60000; // 1 minute timeout
        private static readonly Rectangle PostLoadViewArea = new Rectangle(589, 1157, 125, 108);  // 589,1157 to 714,1265
        private static readonly Rectangle PostLoadCloseArea = new Rectangle(0, 0, 720, 300);      // 0,0 to 720,300; also where the ad close buttons are searched

        private async Task<PostLoadState> WaitForKnownViewAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, IUserNotificationService? userNotifications)
        {
            var state = new PostLoadState();

            while ((DateTime.UtcNow - state.StartTime).TotalMilliseconds < POST_LOAD_DURATION_MS && !state.InKnownView && !cancellationToken.IsCancellationRequested)
            {
                if (await PostLoadStepAsync(account, logger, cancellationToken, userNotifications, state))
                {
                    break;
                }
            }
            return state;
        }

        private sealed record PostLoadDetection(
            bool BaseView, bool MapView, bool Reconnected,
            (bool found, Rectangle position, double confidence) Ad1,
            (bool found, Rectangle position, double confidence) Ad2,
            (bool found, Rectangle position, double confidence) Ad3,
            (bool found, Rectangle position, double confidence) Defence)
        {
            public bool AnyAd => Ad1.found || Ad2.found || Ad3.found || Defence.found;
        }

        /// <summary>One attempt of the post-loading sequence. True when the loop should end (a known view was reached).</summary>
        private async Task<bool> PostLoadStepAsync(
            AccountSettings account, LogService logger, CancellationToken cancellationToken, IUserNotificationService? userNotifications, PostLoadState state)
        {
            state.Attempts++;
            logger.LogInfo($"[{account.AccountName}] 🔄 Post-loading sequence attempt {state.Attempts} (elapsed time: {(DateTime.UtcNow - state.StartTime).TotalSeconds:F1}s)...");

            // Wait a bit for any cloud transitions to clear
            await Task.Delay(2000, cancellationToken);

            // Execute image checks sequentially to prevent resource thrashing
            logger.LogInfo($"[{account.AccountName}] 🔍 Running sequential checks for UI elements...");

            // Take one screenshot and reuse it for all checks
            var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
            if (screenshot == null)
            {
                logger.LogWarning($"[{account.AccountName}] Failed to take screenshot, skipping UI checks");
                await Task.Delay(1000, cancellationToken);
                return false;
            }

            var detection = await DetectPostLoadElementsAsync(account, logger, cancellationToken, userNotifications, state);
            if (detection.Reconnected)
            {
                return false;
            }

            // Check if base view was found early
            if (detection.BaseView)
            {
                logger.LogInfo($"[{account.AccountName}] ✅ Base view detected! Game is ready.");
                state.InKnownView = true;
                return true;
            }

            logger.LogInfo($"[{account.AccountName}] Sequential check results - Ad: {detection.Ad1.found}, Ad2: {detection.Ad2.found}, Ad3: {detection.Ad3.found}, Defence: {detection.Defence.found}, Map: {detection.MapView}, Base: {detection.BaseView}");

            // Process results based on priority
            // Priority 1: Ads and Defence
            if (detection.AnyAd)
            {
                await CloseDetectedAdAsync(account, logger, cancellationToken, detection);
                return false; // Go to next attempt to verify view
            }

            // Priority 2: Map/Base view (game is ready)
            if (detection.MapView)
            {
                logger.LogInfo($"[{account.AccountName}] ✅ Found map view! Skipping remaining checks.");
                state.InKnownView = true;
                return true;
            }

            // No UI elements found: Back closes sale popups (Eternal Legend Pack and the like) and leaves
            // screens such as the shop. Back on the bare base view opens "Quit game?", which the next Back cancels.
            if ((DateTime.UtcNow - state.StartTime).TotalMilliseconds < POST_LOAD_DURATION_MS)
            {
                logger.LogInfo($"[{account.AccountName}] ⏳ No UI elements detected, pressing Back before next attempt...");
                if (await GetCachedADBController(account.InstanceNumber, logger, cancellationToken) is AdbControllerV2 adb)
                    await adb.PressBackButtonAsync(cancellationToken);
                await Task.Delay(2000, cancellationToken);
            }
            return false;
        }

        private async Task<PostLoadDetection> DetectPostLoadElementsAsync(
            AccountSettings account, LogService logger, CancellationToken cancellationToken, IUserNotificationService? userNotifications, PostLoadState state)
        {
            // Use locator folder for map/base view images
            var locatorFolder = Path.Combine(AppContext.BaseDirectory, "templates", "images", "locator");
            var mapViewPath = Path.Combine(locatorFolder, "map-view.png");
            var baseViewPath = Path.Combine(locatorFolder, "base-view.png");

            // Check for base/map view (game ready state)
            bool baseViewFound = await WaitForImageAsync(baseViewPath, account.InstanceNumber, logger,
                cancellationToken, timeoutMs: 500, threshold: 0.65, searchArea: PostLoadViewArea);

            bool mapViewFound = false;
            if (!baseViewFound)
            {
                mapViewFound = await WaitForImageAsync(mapViewPath, account.InstanceNumber, logger,
                    cancellationToken, timeoutMs: 500, threshold: 0.7, searchArea: PostLoadViewArea);
            }

            var none = (found: false, position: Rectangle.Empty, confidence: 0.0);
            if (baseViewFound || mapViewFound)
            {
                return new PostLoadDetection(baseViewFound, mapViewFound, false, none, none, none, none);
            }

            // Only check for ads if game isn't ready
            // "The account has been logged in on another device": Reconnect takes the session back (signs the other device out)
            var reconnectResult = await WaitForImageWithPositionAsync("reconnect.png", account.InstanceNumber, logger,
                cancellationToken, timeoutMs: 500, threshold: ReconnectThreshold, searchArea: ReconnectButtonArea);
            if (reconnectResult.found)
            {
                logger.LogWarning($"[{account.AccountName}] Account was logged in on another device, pressing Reconnect (confidence: {reconnectResult.confidence:F3})");
                userNotifications?.ShowWarning($"{account.AccountName}: logged in on another device, reconnecting");
                await ClickRandomInRectAsync(account.InstanceNumber, logger, reconnectResult.position);
                await Task.Delay(5000, cancellationToken);
                state.StartTime = DateTime.UtcNow; // the game reloads: give the post-load sequence its full time again
                return new PostLoadDetection(false, false, true, none, none, none, none);
            }

            return await DetectAdsAsync(account, logger, cancellationToken);
        }

        private async Task<PostLoadDetection> DetectAdsAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            var none = (found: false, position: Rectangle.Empty, confidence: 0.0);
            var adResult1 = await WaitForImageWithPositionAsync("ad-close.png", account.InstanceNumber, logger,
                cancellationToken, timeoutMs: 500, threshold: 0.65, searchArea: PostLoadCloseArea);
            var adResult2 = none;
            var adResult3 = none;
            var defenceResult = none;

            if (!adResult1.found)
            {
                adResult2 = await WaitForImageWithPositionAsync("ad-close2.png", account.InstanceNumber, logger,
                    cancellationToken, timeoutMs: 500, threshold: 0.65, searchArea: PostLoadCloseArea);
            }

            if (!adResult1.found && !adResult2.found)
            {
                adResult3 = await WaitForImageWithPositionAsync("ad-close3.png", account.InstanceNumber, logger,
                    cancellationToken, timeoutMs: 500, threshold: 0.65, searchArea: PostLoadCloseArea);
            }

            if (!adResult1.found && !adResult2.found && !adResult3.found)
            {
                defenceResult = await WaitForImageWithPositionAsync("defence.png", account.InstanceNumber, logger,
                    cancellationToken, timeoutMs: 500, threshold: 0.65, searchArea: PostLoadCloseArea);
            }

            return new PostLoadDetection(false, false, false, adResult1, adResult2, adResult3, defenceResult);
        }

        private async Task CloseDetectedAdAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, PostLoadDetection detection)
        {
            logger.LogInfo($"[{account.AccountName}] 🖱️ Found ad close button or defence image, clicking on detected position...");

            // Find the highest confidence ad detection and click on its position
            var adResults = new[] { detection.Ad1, detection.Ad2, detection.Ad3, detection.Defence };
            var bestAd = adResults.Where(r => r.found).OrderByDescending(r => r.confidence).FirstOrDefault();

            if (bestAd.found)
            {
                logger.LogInfo($"[{account.AccountName}] Clicking on best ad match at {bestAd.position} with confidence {bestAd.confidence:F3}");
                await ClickRandomInRectAsync(account.InstanceNumber, logger, bestAd.position);
            }
            else
            {
                // Fallback to general close area if no position found
                logger.LogInfo($"[{account.AccountName}] No position found, clicking general close area");
                await ClickRandomInRectAsync(account.InstanceNumber, logger, PostLoadCloseArea);
            }

            await Task.Delay(2000, cancellationToken);
        }

        /// <summary>
        /// Phase 5 and 6: final verification that the bot is in a known state, then startup complete.
        /// </summary>
        private async Task<TaskExecutionDetails> FinishStartupAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, IUserNotificationService? userNotifications)
        {
            NotifyProgress(userNotifications, "Verifying game state", 90);
            logger.LogInfo($"[{account.AccountName}] Starting Phase 5: Final state verification...");
            if (!await VerifyGameState(account, logger, cancellationToken))
            {
                logger.LogError($"[{account.AccountName}] ❌ Failed to verify game state after recovery attempts.");
                UpdatePhaseStatus(account.InstanceNumber, account.AccountName, "Verification Failed");
                return FailGameState("Phase 5: Verification", "Game state could not be verified after startup", recoveryNeeded: true);
            }

            logger.LogInfo($"[{account.AccountName}] ✅ 🎉 STARTUP PHASE COMPLETE! 🎉");
            UpdatePhaseStatus(account.InstanceNumber, account.AccountName, "Startup Complete");

            await Task.Delay(GameCoordinates.Delays.AfterCompletion, cancellationToken); // Brief delay after completion

            return TaskExecutionDetails.Succeeded();
        }

        /// <summary>
        /// True when the game is already showing the base or map view (the same check the post-loading loop ends on).
        /// </summary>
        private async Task<bool> IsInGameViewAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            var viewDetectionArea = new Rectangle(589, 1157, 125, 108);
            var locatorFolder = Path.Combine(AppContext.BaseDirectory, "templates", "images", "locator");
            if (await WaitForImageAsync(Path.Combine(locatorFolder, "base-view.png"), account.InstanceNumber, logger,
                    cancellationToken, timeoutMs: 500, threshold: 0.65, searchArea: viewDetectionArea))
                return true;
            return await WaitForImageAsync(Path.Combine(locatorFolder, "map-view.png"), account.InstanceNumber, logger,
                cancellationToken, timeoutMs: 500, threshold: 0.7, searchArea: viewDetectionArea);
        }

        /// <summary>
        /// Phase 1: Launches the Kingshot app using dnconsole.exe runapp command
        /// </summary>
        private async Task<bool> FindAndClickKingshotApp(AccountSettings account, LogService logger, CancellationToken cancellationToken, IUserNotificationService? userNotifications = null)
        {
            try
            {
                // Send GUI notification
                userNotifications?.ShowStatus("Launching Kingshot App", NotificationType.Info);
                
                logger.LogInfo($"[{account.AccountName}] Launching Kingshot App");
                await Task.Delay(1000, cancellationToken); // CanExecuteAsync (and the cycle's boot wait) already confirmed the instance is up
                
                logger.LogInfo($"[{account.AccountName}] 🚀 Launching Kingshot app using dnconsole.exe...");
                
                var ldConsolePath = LDPlayerHelper.GetLDPlayerConsolePath();
                if (!File.Exists(ldConsolePath))
                {
                    logger.LogError($"[{account.AccountName}] ❌ dnconsole.exe not found at: {ldConsolePath}");
                    return false;
                }
                
                var startInfo = new ProcessStartInfo
                {
                    FileName = ldConsolePath,
                    Arguments = $"runapp --index {account.InstanceNumber} --packagename com.run.tower.defense",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                
                logger.LogInfo($"[{account.AccountName}] Executing: {ldConsolePath} runapp --index {account.InstanceNumber} --packagename com.run.tower.defense");
                
                using var process = Process.Start(startInfo);
                if (process == null) 
                { 
                    logger.LogError($"[{account.AccountName}] ❌ Failed to start dnconsole process");
                    return false; 
                }
                
                string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                string error = await process.StandardError.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                
                logger.LogInfo($"[{account.AccountName}] dnconsole exit code: {process.ExitCode}");
                if (!string.IsNullOrWhiteSpace(output)) 
                    logger.LogInfo($"[{account.AccountName}] dnconsole output: {output.Trim()}");
                if (!string.IsNullOrWhiteSpace(error)) 
                    logger.LogError($"[{account.AccountName}] dnconsole error: {error.Trim()}");
                
                if (process.ExitCode == 0)
                {
                    logger.LogInfo($"[{account.AccountName}] ✅ Successfully launched Kingshot app");
                    // Short wait only: the loading-screen check polls for up to 45 s, and the logo was already up after 5 s
                    await Task.Delay(1000, cancellationToken);
                    return true;
                }
                else
                {
                    logger.LogError($"[{account.AccountName}] ❌ Failed to launch Kingshot app (exit code: {process.ExitCode})");
                    return false;
                }
            }
            catch (OperationCanceledException) 
            {
                logger.LogInfo($"[{account.AccountName}] 🛑 App launch cancelled");
                return false;
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] 💥 Exception launching Kingshot app: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Phase 2: Handles the loading screen that appears when starting the game
        /// Uses polling approach to check for loading screen every 1 second for up to 15 seconds
        /// </summary>
        /// <summary>
        /// Returns the name of the loading screen template that matches, or null. loading-screen3.png is the current
        /// Kingshot logo (0.99-1.00 on the loading screen, <= 0.25 on popups, base view and shop); the older two are kept for other builds.
        /// </summary>
        private async Task<string?> FindLoadingScreenAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, int timeoutMs, string? only = null)
        {
            foreach (var name in only != null ? new[] { only } : new[] { "loading-screen3.png", "loading-screen.png", "loading-screen2.png" })
            {
                if (await WaitForImageAsync(name, account.InstanceNumber, logger, cancellationToken,
                        timeoutMs: timeoutMs, threshold: 0.5, useEnhancedMatching: true, searchArea: LoadingScreenArea))
                    return name;
            }
            return null;
        }

        private enum LoadingPhase { ScreenFound, Done, Failed, NotFound }

        private const int LoadingPollIntervalMs = 1000; // Check every 1000ms (1 second)
        private const int InitialLoadingWaitMs = 15000; // Initial 15 seconds wait time
        private const int ExtendedLoadingWaitMs = 30000; // Additional 30 seconds for extended search

        private async Task<bool> WaitForLoadingToCompleteInternal(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            var startTime = DateTime.UtcNow;

            // 1. Poll for loading screen appearance (up to 15 seconds)
            var (phase, detectedLoadingScreen) = await WaitForLoadingScreenAsync(account, logger, cancellationToken, startTime);
            if (phase == LoadingPhase.Failed) return false;
            if (phase == LoadingPhase.Done) return true;

            // 2. If loading screen was not found after 15 seconds, start looking for subsequent UI elements
            if (phase == LoadingPhase.NotFound)
            {
                logger.LogInfo($"[{account.AccountName}] Loading screen not detected after {InitialLoadingWaitMs}ms, starting extended search...");
                (phase, detectedLoadingScreen) = await ExtendedLoadingSearchAsync(account, logger, cancellationToken, startTime);
                if (phase != LoadingPhase.ScreenFound) return true;
            }

            // 3. If loading screen was found (either initially or during extended search), wait for it to disappear
            return await WaitForLoadingScreenToDisappearAsync(account, logger, cancellationToken, detectedLoadingScreen);
        }

        private async Task<(LoadingPhase phase, string screenName)> WaitForLoadingScreenAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, DateTime startTime)
        {
            while ((DateTime.UtcNow - startTime).TotalMilliseconds < InitialLoadingWaitMs && !cancellationToken.IsCancellationRequested)
            {
                var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshot == null)
                {
                    logger.LogError($"[{account.AccountName}] Failed to get screenshot for loading check");
                    return (LoadingPhase.Failed, string.Empty);
                }

                // Check for the loading screen variants
                var loadingScreenName = await FindLoadingScreenAsync(account, logger, cancellationToken, LoadingPollIntervalMs);
                if (loadingScreenName != null)
                {
                    logger.LogInfo($"[{account.AccountName}] {loadingScreenName} detected after {(DateTime.UtcNow - startTime).TotalMilliseconds:F0}ms, waiting for it to disappear...");
                    return (LoadingPhase.ScreenFound, loadingScreenName);
                }

                // The game was already running (runapp only brings it to the front): no loading screen comes
                if (await WaitForImageAsync(Path.Combine(AppContext.BaseDirectory, "templates", "images", "locator", "base-view.png"),
                        account.InstanceNumber, logger, cancellationToken, timeoutMs: 500, threshold: 0.65, searchArea: BaseViewDetectionArea))
                {
                    logger.LogInfo($"[{account.AccountName}] Base view already showing, no loading screen to wait for");
                    return (LoadingPhase.Done, string.Empty);
                }

                await Task.Delay(LoadingPollIntervalMs, cancellationToken);
            }
            return (LoadingPhase.NotFound, string.Empty);
        }

        /// <summary>The loading screen did not show in time: look for it again, or for the base/map view. Done when the flow can go on without waiting.</summary>
        private async Task<(LoadingPhase phase, string screenName)> ExtendedLoadingSearchAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, DateTime startTime)
        {
            // Define search areas for subsequent UI elements
            var viewDetectionArea = new Rectangle(589, 1157, 125, 108);  // 589,1157 to 714,1265
            var locatorFolder = Path.Combine(AppContext.BaseDirectory, "templates", "images", "locator");
            var mapViewPath = Path.Combine(locatorFolder, "map-view.png");
            var baseViewPath = Path.Combine(locatorFolder, "base-view.png");

            while ((DateTime.UtcNow - startTime).TotalMilliseconds < (InitialLoadingWaitMs + ExtendedLoadingWaitMs) && !cancellationToken.IsCancellationRequested)
            {
                var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshot == null)
                {
                    await Task.Delay(LoadingPollIntervalMs, cancellationToken);
                    continue;
                }

                // Check for both loading screens again
                var loadingScreenName = await FindLoadingScreenAsync(account, logger, cancellationToken, LoadingPollIntervalMs);
                if (loadingScreenName != null)
                {
                    logger.LogInfo($"[{account.AccountName}] {loadingScreenName} detected during extended search at {(DateTime.UtcNow - startTime).TotalMilliseconds:F0}ms");
                    return (LoadingPhase.ScreenFound, loadingScreenName);
                }

                // Check for base/map view indicators
                var mapViewFound = await WaitForImageAsync(
                    mapViewPath,
                    account.InstanceNumber,
                    logger,
                    cancellationToken,
                    timeoutMs: LoadingPollIntervalMs,
                    threshold: 0.5,
                    searchArea: viewDetectionArea
                );

                var baseViewFound = await WaitForImageAsync(
                    baseViewPath,
                    account.InstanceNumber,
                    logger,
                    cancellationToken,
                    timeoutMs: LoadingPollIntervalMs,
                    threshold: 0.65,
                    searchArea: viewDetectionArea
                );

                if (mapViewFound || baseViewFound)
                {
                    logger.LogInfo($"[{account.AccountName}] {(mapViewFound ? "Map" : "Base")} view detected during extended search, proceeding with normal flow");
                    return (LoadingPhase.Done, string.Empty);
                }

                await Task.Delay(LoadingPollIntervalMs, cancellationToken);
            }

            logger.LogInfo($"[{account.AccountName}] No loading screen or subsequent UI elements detected after extended search ({InitialLoadingWaitMs + ExtendedLoadingWaitMs}ms), proceeding with normal flow");
            return (LoadingPhase.Done, string.Empty);
        }

        private async Task<bool> WaitForLoadingScreenToDisappearAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, string detectedLoadingScreen)
        {
            const int MAX_LOADING_TIME = 120; // 2 minutes
            var loadingStartTime = DateTime.UtcNow;

            while (!cancellationToken.IsCancellationRequested)
            {
                var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshot == null)
                {
                    logger.LogError($"[{account.AccountName}] Failed to get screenshot for loading check");
                    return false;
                }

                // Only the loading screen that was seen: checking the other variants added ~2.5 s once it was gone
                var loadingScreenName = await FindLoadingScreenAsync(account, logger, cancellationToken, LoadingPollIntervalMs, detectedLoadingScreen);

                if (loadingScreenName == null)
                {
                    logger.LogInfo($"[{account.AccountName}] ✅ Loading screen no longer detected");
                    logger.LogInfo($"[{account.AccountName}] ✅ Loading complete after {(DateTime.UtcNow - loadingStartTime).TotalSeconds:F1} seconds");
                    return true;
                }

                if ((DateTime.UtcNow - loadingStartTime).TotalSeconds > MAX_LOADING_TIME)
                {
                    logger.LogError($"[{account.AccountName}] ❌ Loading timeout after {MAX_LOADING_TIME} seconds");
                    return false;
                }

                await Task.Delay(LoadingPollIntervalMs, cancellationToken);
            }

            return false;
        }

        public async Task<bool> WaitForLoadingToComplete(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            // Wait for loading screen to complete
            if (!await WaitForLoadingToCompleteInternal(account, logger, cancellationToken))
            {
                return false;
            }
            
            // After loading, run the same validation as the full startup task
            // This ensures ads are dismissed, popups are handled, and we're in the correct view
            var locator = new LocatorService(logger, account);
            if (!await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken))
            {
                logger.LogWarning($"[{account.AccountName}] Failed to validate view after loading completion");
                return false;
            }
            
            return true;
        }


        /// <summary>
        /// Phase 5: Verifies the final game state after startup sequence
        /// Makes up to 3 attempts to verify the game is in a known state
        /// If verification fails, attempts to recover by re-running phases 3 and 4
        /// </summary>
        private async Task<bool> VerifyGameState(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            const int maxTimeoutMs = 10000; // 10 seconds
            const int checkIntervalMs = 500; // 0.5 seconds
            var locator = new LocatorService(logger, account);
            var startTime = DateTime.UtcNow;

            while ((DateTime.UtcNow - startTime).TotalMilliseconds < maxTimeoutMs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                UpdatePhaseStatus(account.InstanceNumber, account.AccountName, $"Verifying State ({(DateTime.UtcNow - startTime).TotalSeconds:F1}s/10s)");

                try
                {
                    ViewType currentView = await locator.DetectCurrentViewAsync(account.InstanceNumber, cancellationToken);
                    if (currentView == ViewType.BaseView || currentView == ViewType.MapView)
                    {
                        logger.LogInfo($"[{account.AccountName}] ✅ Game state verified. In {currentView}.");
                        return true;
                    }
                    logger.LogWarning($"[{account.AccountName}] ⚠️ Detected view is {currentView}, but expected BaseView or MapView. Retrying...");
                }
                catch (BotLostException)
                {
                    logger.LogWarning($"[{account.AccountName}] ⚠️ Bot is not in a known view (Base or Map). Retrying...");
                }

                await Task.Delay(checkIntervalMs, cancellationToken);
            }

            logger.LogError($"[{account.AccountName}] ❌ Failed to verify game state after {maxTimeoutMs/1000} seconds");
            return await IsInCastleViewAsync(account, logger, cancellationToken); // Final fallback check
        }



        /// <summary>
        /// Updates the GUI with the current phase status
        /// </summary>
        private static void UpdatePhaseStatus(int instanceNumber, string accountName, string phase)
        {
            OnPhaseUpdate?.Invoke(instanceNumber, accountName, phase);
        }
        
        /// <summary>
        /// Initializes the startup task by creating a README file with instructions
        /// for required image templates and their usage
        /// </summary>
        protected override async Task OnInitializeAsync(LogService logger, CancellationToken cancellationToken)
        {
            var readmePath = Path.Combine(ImageTemplateFolder, "README.txt");
            if (!File.Exists(readmePath))
            {
                await File.WriteAllTextAsync(readmePath, 
                    "Startup Image Templates:\n\n" +
                    "Required images for startup sequence:\n\n" +
                    "1. kingshot-app.png - The game app icon on home screen\n" +
                    "2. loading-screen.png - Loading screen that appears when starting game\n" +
                    "3. loading-screen2.png - Alternative loading screen (optional)\n" +
                    "4. ad-close.png, ad-close2.png, ad-close3.png - Ad close buttons\n\n" +
                    "Instructions:\n" +
                    "- Take clear screenshots of each element\n" +
                    "- Crop to just the unique part you want to detect\n" +
                    "- Save with exact names above\n" +
                    "- Images should be at least 50x50 pixels\n\n" +
                    "Enhanced Startup Sequence:\n" +
                    "1. Find and click Kingshot app icon (unified template matching)\n" +
                    "2. Wait for loading screen to appear and disappear\n" +
                    "3. Detect and close any ads that appear\n" +
                    "4. Verify game is in base or map view\n" +
                    "5. Complete startup phase\n\n" +
                    "Enhanced Features:\n" +
                    "- Unified template matching service eliminates code duplication\n" +
                    "- Centralized coordinates in GameCoordinates class\n" +
                    "- Consistent error handling patterns\n" +
                    "- Configurable confidence thresholds and timeouts\n" +
                    "- Improved resource management", cancellationToken);
                logger.LogInfo($"Created startup images README at: {readmePath}");
            }
        }

        /// <summary>
        /// Helper method to verify if the bot is in the castle view
        /// Checks for specific castle view indicators in the game
        /// </summary>
        private async Task<bool> IsInCastleViewAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            var castleImageNames = new[] { "castle-view-1.png", "castle-view-2.png" };
            foreach (var imageName in castleImageNames)
            {
                if (await WaitForImageAsync(imageName, account.InstanceNumber, logger, cancellationToken, timeoutMs: 3000, threshold: 0.7))
                {
                    logger.LogInfo($"[{account.AccountName}] ✅ Confirmed in castle view with {imageName}.");
                    return true;
                }
            }
            logger.LogWarning($"[{account.AccountName}] ⚠️ Could not confirm castle view after checking all images.");
            return false;
        }


    }
} 