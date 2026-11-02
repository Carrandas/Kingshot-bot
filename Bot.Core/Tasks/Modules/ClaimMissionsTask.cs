using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.LDPlayer;
using Bot.Core.Exceptions;
using Bot.Core.ImageDetection;
using Bot.Core.Services;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using Tesseract;

namespace Bot.Core.Tasks.Modules
{
    public class ClaimMissionsTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.ClaimMissions;
        public override string Name => "Claim Missions (in beta)";

        
        // Tab image detection confidence threshold
        private const double TAB_CONFIDENCE_THRESHOLD = 0.85; // the unselected tabs score ~0.97, other tab labels ~0.66
        
        // Claim button confidence threshold (lower due to UI variations)
        private const double CLAIM_BUTTON_CONFIDENCE_THRESHOLD = 0.85; // the teal "Go" buttons score ~0.69 against claim.png
        
        // Notification detection areas for OCR (expanded by 15 pixels for better detection)
        private static readonly Rectangle ChapterNotificationArea = new Rectangle(201, 1135, 44, 41);
        private static readonly Rectangle MissionScrollNotificationArea = new Rectangle(44, 1010, 39, 35);
        
        // Wait time for images to appear
        private const int ImageWaitTimeSeconds = 2500; // 2.5 seconds in milliseconds

        protected override async Task OnInitializeAsync(LogService logger, CancellationToken cancellationToken)
        {
            _instanceLogger = logger;
            await base.OnInitializeAsync(logger, cancellationToken);
        }

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            try
            {
                _instanceLogger = logger;
                var locatorService = new LocatorService(logger, account, TaskType);

                logger.LogInfo($"[{account.AccountName}] Starting claim missions process...");

                // Step 0: the previous task may have left another screen open (e.g. VIP), the scroll is only on the base view
                if (!await locatorService.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken))
                {
                    logger.LogError($"[{account.AccountName}] Failed to navigate to base view");
                    return TaskExecutionDetails.Failed("Failed to navigate to base view");
                }

                // Step 1: Look for missions scroll button but don't click yet
                logger.LogInfo($"[{account.AccountName}] Looking for missions scroll button...");
                bool missionsScrollFound = await WaitForImageAsync("missions-scroll.png", account.InstanceNumber, logger, ImageWaitTimeSeconds, cancellationToken);
                
                if (!missionsScrollFound)
                {
                    logger.LogWarning($"[{account.AccountName}] Missions scroll button not found after {ImageWaitTimeSeconds}ms");
                    return TaskExecutionDetails.Failed("Missions scroll button not found");
                }

                // Step 1.5: Check for notifications on the mission scroll icon
                logger.LogInfo($"[{account.AccountName}] Checking mission scroll icon for notifications...");
                bool scrollNotificationsFound = await DetectNotificationNumber(MissionScrollNotificationArea, account.InstanceNumber, logger, "MissionScroll");
                
                if (!scrollNotificationsFound)
                {
                    logger.LogInfo($"[{account.AccountName}] No notifications found on mission scroll icon - skipping missions module");
                    return TaskExecutionDetails.Succeeded("No missions notifications found - module skipped");
                }

                // Step 2: Click missions scroll button since notifications were detected
                logger.LogInfo($"[{account.AccountName}] Mission notifications detected - clicking missions scroll button...");
                bool missionsScrollClicked = await FindAndClickImageAsync("missions-scroll.png", account.InstanceNumber, logger, threshold: 0.7);
                
                if (!missionsScrollClicked)
                {
                    logger.LogWarning($"[{account.AccountName}] Failed to click missions scroll button");
                    return TaskExecutionDetails.Failed("Failed to click missions scroll button");
                }

                await Task.Delay(1000, cancellationToken); // Wait for missions interface to load

                // Step 3: Verify missions interface loaded by checking for chapter, daily, or missions-scroll
                logger.LogInfo($"[{account.AccountName}] Verifying missions interface loaded...");
                bool interfaceLoaded = await VerifyMissionsInterfaceAsync(account.InstanceNumber, logger, cancellationToken);
                
                if (!interfaceLoaded)
                {
                    logger.LogWarning($"[{account.AccountName}] Missions interface did not load properly");
                    return TaskExecutionDetails.Failed("Missions interface did not load");
                }

                // Step 4: Check and Process Chapter tab if notifications detected
                bool chapterClaimAllUsed = await ProcessChapterTabAsync(account, logger, cancellationToken);

                // Handle case where Chapter claim-all takes bot back to base view
                if (chapterClaimAllUsed)
                {
                    var reopenFailure = await ReopenAfterChapterClaimAllAsync(account, logger, cancellationToken);
                    if (reopenFailure != null) return reopenFailure;
                }

                // Step 5/6: Daily and Growth. The notification dots moved with the 2-tab layout, so both tabs are
                // always visited; a tab without claimable missions just finds no claim buttons.
                foreach (var (tabImage, otherTabImage, tabName) in new[] { ("daily.png", "growth.png", "Daily"), ("growth.png", "daily.png", "Growth") })
                {
                    if (await OpenMissionsTabAsync(tabImage, otherTabImage, tabName, account.InstanceNumber, logger))
                    {
                        await Task.Delay(500, cancellationToken);
                        await ProcessMissionsTabAsync(tabName, account.InstanceNumber, logger, cancellationToken);
                    }
                    else
                    {
                        logger.LogWarning($"[{account.AccountName}] Failed to open {tabName} tab, skipping");
                    }
                }

                // Step 7: Exit missions menu
                logger.LogInfo($"[{account.AccountName}] Looking for exit menu button...");
                bool exitClicked = await WaitForAndClickImageAsync("exit-menu.png", account.InstanceNumber, logger, ImageWaitTimeSeconds, cancellationToken);
                
                if (!exitClicked)
                {
                    logger.LogWarning($"[{account.AccountName}] Exit menu button not found, missions might still be open");
                }

                logger.LogInfo($"[{account.AccountName}] Claim missions process completed successfully");
                return TaskExecutionDetails.Succeeded();
            }
            catch (OperationCanceledException)
            {
                logger.LogInfo($"[{account.AccountName}] Claim missions task was cancelled");
                return TaskExecutionDetails.Failed("Task was cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error during claim missions: {ex.Message}");
                return TaskExecutionDetails.Failed(ex.Message);
            }
        }

        /// <summary>Processes the Chapter tab when it shows notifications. True when claim-all was used.</summary>
        private async Task<bool> ProcessChapterTabAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            logger.LogInfo($"[{account.AccountName}] Checking Chapter tab for notifications...");
            if (!await DetectNotificationNumber(ChapterNotificationArea, account.InstanceNumber, logger, "Chapter"))
            {
                logger.LogInfo($"[{account.AccountName}] Skipping Chapter tab - no notifications");
                return false;
            }

            logger.LogInfo($"[{account.AccountName}] Processing Chapter tab - notifications found");
            if (!await FindAndClickTabAsync("chapter.png", account.InstanceNumber, logger, "Chapter"))
            {
                logger.LogWarning($"[{account.AccountName}] Failed to click Chapter tab, skipping");
                return false;
            }

            await Task.Delay(500, cancellationToken);
            return await ProcessMissionsTabAsync("Chapter", account.InstanceNumber, logger, cancellationToken);
        }

        /// <summary>Chapter claim-all took the bot back to the base view: open the missions menu again. Returns a result when the remaining tabs have to be skipped.</summary>
        private async Task<TaskExecutionDetails?> ReopenAfterChapterClaimAllAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            logger.LogInfo($"[{account.AccountName}] Chapter claim-all used - waiting 2 seconds and re-opening missions menu...");
            await Task.Delay(2000, cancellationToken);

            // Try to re-open missions menu
            if (!await ReopenMissionsMenuAsync(account, logger, cancellationToken))
            {
                logger.LogWarning($"[{account.AccountName}] Failed to re-open missions menu after Chapter claim-all - skipping remaining tabs");
                return TaskExecutionDetails.Succeeded("Chapter completed, remaining tabs skipped due to navigation failure");
            }

            // Verify missions interface loaded again
            if (!await VerifyMissionsInterfaceAsync(account.InstanceNumber, logger, cancellationToken))
            {
                logger.LogWarning($"[{account.AccountName}] Missions interface did not load after Chapter claim-all - skipping remaining tabs");
                return TaskExecutionDetails.Succeeded("Chapter completed, remaining tabs skipped due to interface failure");
            }
            return null;
        }

        /// <summary>
        /// Opens a missions tab. The templates show the unselected tab, so when the tab is already the open one
        /// it is recognised by the other tab showing as unselected.
        /// </summary>
        private async Task<bool> OpenMissionsTabAsync(string tabImage, string otherTabImage, string tabName, int instanceNumber, LogService logger)
        {
            if (await FindAndClickTabAsync(tabImage, instanceNumber, logger, tabName))
                return true;

            if (await IsImagePresentAsync(otherTabImage, instanceNumber, logger))
            {
                logger.LogInfo($"{tabName} tab is already open");
                return true;
            }

            return false;
        }

        private async Task<bool> VerifyMissionsInterfaceAsync(int instanceNumber, LogService logger, CancellationToken cancellationToken)
        {
            // exit-menu.png is the X in the header, present on every tab layout
            var imagesToCheck = new[] { "exit-menu.png", "growth.png", "daily.png", "chapter.png" };
            
            var startTime = DateTime.UtcNow;
            while ((DateTime.UtcNow - startTime).TotalMilliseconds < ImageWaitTimeSeconds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                foreach (var image in imagesToCheck)
                {
                    if (await IsImagePresentAsync(image, instanceNumber, logger))
                    {
                        logger.LogInfo($"Found {image} - missions interface confirmed");
                        return true;
                    }
                }
                
                await Task.Delay(200, cancellationToken);
            }
            
            return false;
        }

        private async Task<bool> ProcessMissionsTabAsync(string tabName, int instanceNumber, LogService logger, CancellationToken cancellationToken)
        {
            logger.LogInfo($"Processing {tabName} missions...");

            // First check for claim-all button
            bool claimAllUsed = await TryClaimAllAsync(tabName, instanceNumber, logger, cancellationToken);

            // If claim-all was not found or confidence was too low, look for individual claim buttons
            if (!claimAllUsed)
            {
                await ClaimIndividuallyAsync(tabName, instanceNumber, logger, cancellationToken);
            }

            // Return true if claim-all was used
            return claimAllUsed;
        }

        private async Task<bool> TryClaimAllAsync(string tabName, int instanceNumber, LogService logger, CancellationToken cancellationToken)
        {
            logger.LogInfo($"Looking for claim-all button in {tabName} tab...");
            if (!await WaitForImageAsync("claim-all.png", instanceNumber, logger, ImageWaitTimeSeconds, cancellationToken))
            {
                return false;
            }

            logger.LogInfo($"Found claim-all button in {tabName} tab, verifying confidence...");

            // Double-check with strict 0.8 confidence before clicking
            if (!await ClickVerifiedClaimAsync("claim-all.png", "Using template matching for claim-all.png (grayscale mode)",
                    "claim-all button", "Claim-all button", "will try individual claims", instanceNumber, logger))
            {
                return false;
            }

            await Task.Delay(1000, cancellationToken);

            // Look for reward-image after claim-all and keep clicking until not detected
            logger.LogInfo($"Looking for reward-image after claim-all in {tabName} tab...");
            await ProcessRewardImageAsync(instanceNumber, logger, cancellationToken, tabName);
            return true;
        }

        /// <summary>
        /// Matches the claim template again with the strict 0.8 threshold and clicks it.
        /// False when there is no screenshot, the confidence is too low or the click failed.
        /// </summary>
        private async Task<bool> ClickVerifiedClaimAsync(
            string imageName, string modeMessage, string buttonLabel, string capitalizedLabel, string belowThresholdAction,
            int instanceNumber, LogService logger)
        {
            var screenshot = await TakeScreenshotAsync(instanceNumber, logger);
            if (screenshot == null || _templateMatcher == null)
            {
                return false;
            }

            var templatePath = Path.Combine(ImageTemplateFolder, imageName);
            logger.LogInfo(modeMessage);
            var (found, matchRect, confidence) = _templateMatcher.MatchTemplate(
                screenshot,
                templatePath,
                instanceNumber,
                threshold: 0.8,
                scales: UnifiedTemplateMatchingService.StandardScales,
                verboseLogging: true  // Enable verbose logging to confirm color matching
            );

            if (!found || confidence < CLAIM_BUTTON_CONFIDENCE_THRESHOLD)
            {
                logger.LogWarning($"{capitalizedLabel} confidence {confidence:F3} is below {CLAIM_BUTTON_CONFIDENCE_THRESHOLD} threshold, {belowThresholdAction}");
                return false;
            }

            logger.LogInfo($"Verified {buttonLabel} with confidence {confidence:F3} >= {CLAIM_BUTTON_CONFIDENCE_THRESHOLD}, clicking...");
            return await ClickRandomInRectAsync(instanceNumber, logger, matchRect);
        }

        private async Task ClaimIndividuallyAsync(string tabName, int instanceNumber, LogService logger, CancellationToken cancellationToken)
        {
            logger.LogInfo($"Looking for individual claim buttons in {tabName} tab...");

            int claimCount = 0;
            const int maxClaimAttempts = 50; // Increased to allow more claims
            const int timeoutMs = 3000; // 3 seconds timeout when no buttons found
            DateTime lastClaimTime = DateTime.UtcNow;

            while (claimCount < maxClaimAttempts)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Poll for claim button with a short wait time
                if (await WaitForImageAsync("claim.png", instanceNumber, logger, 1000, cancellationToken))
                {
                    logger.LogInfo($"Found claim button #{claimCount + 1} in {tabName} tab, verifying confidence...");

                    // Double-check with strict 0.8 confidence before clicking
                    if (await ClickVerifiedClaimAsync("claim.png", "Using color matching for claim.png (should show 'Using color matching mode' in next log)",
                            "claim button", "Claim button", "skipping", instanceNumber, logger))
                    {
                        claimCount++;
                        lastClaimTime = DateTime.UtcNow; // Reset timer on successful click

                        // Wait 0.5s after clicking before looking for next claim button
                        await Task.Delay(500, cancellationToken);
                        continue;
                    }
                }

                // No claim button found or failed to click, check if we've exceeded timeout
                if ((DateTime.UtcNow - lastClaimTime).TotalMilliseconds >= timeoutMs)
                {
                    logger.LogInfo($"No claim buttons found for {timeoutMs}ms in {tabName} tab after {claimCount} claims, moving on");
                    break;
                }

                // Wait a bit before checking again
                await Task.Delay(200, cancellationToken);
            }

            if (claimCount >= maxClaimAttempts)
            {
                logger.LogWarning($"Reached maximum claim attempts ({maxClaimAttempts}) in {tabName} tab");
            }
            else if (claimCount > 0)
            {
                logger.LogInfo($"Completed individual claims in {tabName} tab: {claimCount} claims processed");

                // Check for reward-image only after all claim buttons have been processed
                logger.LogInfo($"No more claim buttons found in {tabName} tab, looking for reward-image...");
                await ProcessRewardImageAsync(instanceNumber, logger, cancellationToken, $"{tabName} after {claimCount} claims");
            }
            else
            {
                logger.LogInfo($"No claim buttons found in {tabName} tab");
            }
        }

        private async Task<bool> WaitForImageAsync(string imageName, int instanceNumber, LogService logger, int timeoutMs, CancellationToken cancellationToken)
        {
            var startTime = DateTime.UtcNow;
            double bestConfidence = 0.0;
            
            while ((DateTime.UtcNow - startTime).TotalMilliseconds < timeoutMs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                var (found, confidence) = await IsImagePresentWithConfidenceAsync(imageName, instanceNumber, logger);
                if (confidence > bestConfidence)
                {
                    bestConfidence = confidence;
                }
                
                if (found)
                {
                    return true;
                }
                
                await Task.Delay(200, cancellationToken); // Check every 200ms
            }
            
            // Log best confidence when image not found
            bool isClaimImage = imageName.Equals("claim.png", StringComparison.OrdinalIgnoreCase) || 
                               imageName.Equals("claim-all.png", StringComparison.OrdinalIgnoreCase);
            if (isClaimImage)
            {
                logger.LogInfo($"{imageName} not found after {timeoutMs}ms - best confidence: {bestConfidence:F3} (threshold: {CLAIM_BUTTON_CONFIDENCE_THRESHOLD})");
                
                // Debug screenshot saving disabled
            }
            
            return false;
        }

        private async Task<bool> WaitForAndClickImageAsync(string imageName, int instanceNumber, LogService logger, int timeoutMs, CancellationToken cancellationToken)
        {
            bool found = await WaitForImageAsync(imageName, instanceNumber, logger, timeoutMs, cancellationToken);
            
            if (found)
            {
                // Use same confidence logic as IsImagePresentAsync
                bool isClaimImage = imageName.Equals("claim.png", StringComparison.OrdinalIgnoreCase) || 
                                   imageName.Equals("claim-all.png", StringComparison.OrdinalIgnoreCase);
                double confidence = isClaimImage ? CLAIM_BUTTON_CONFIDENCE_THRESHOLD : GameCoordinates.Thresholds.StandardConfidence;
                
                return await FindAndClickImageAsync(imageName, instanceNumber, logger, threshold: confidence, useEnhancedMatching: true);
            }
            
            return false;
        }

        private async Task<bool> IsImagePresentAsync(string imageName, int instanceNumber, LogService logger)
        {
            var (found, _) = await IsImagePresentWithConfidenceAsync(imageName, instanceNumber, logger);
            return found;
        }

        private async Task<(bool found, double confidence)> IsImagePresentWithConfidenceAsync(string imageName, int instanceNumber, LogService logger)
        {
            try
            {
                var screenshot = await TakeScreenshotAsync(instanceNumber, logger);
                if (screenshot == null)
                {
                    return (false, 0.0);
                }

                if (_templateMatcher == null)
                {
                    _templateMatcher = new UnifiedTemplateMatchingService(logger);
                }

                var templatePath = Path.Combine(ImageTemplateFolder, imageName);
                if (!File.Exists(templatePath))
                {
                    logger.LogError($"Image template not found: {templatePath}");
                    return (false, 0.0);
                }

                // Use custom confidence for claim.png and claim-all.png
                bool isClaimImage = imageName.Equals("claim.png", StringComparison.OrdinalIgnoreCase) || 
                                   imageName.Equals("claim-all.png", StringComparison.OrdinalIgnoreCase);
                double threshold = isClaimImage ? CLAIM_BUTTON_CONFIDENCE_THRESHOLD : GameCoordinates.Thresholds.StandardConfidence;

                var result = _templateMatcher.MatchTemplate(
                    screenshot,
                    templatePath,
                    instanceNumber,
                    threshold: threshold,
                    scales: UnifiedTemplateMatchingService.StandardScales,
                    verboseLogging: isClaimImage  // Enable verbose logging for claim images to confirm color matching
                );

                return (result.found, result.confidence);
            }
            catch (Exception ex)
            {
                logger.LogError($"Error checking for image '{imageName}': {ex.Message}");
                return (false, 0.0);
            }
        }

        /// <summary>
        /// Finds and clicks a tab using image detection with confidence threshold
        /// </summary>
        private async Task<bool> FindAndClickTabAsync(string tabImageName, int instanceNumber, LogService logger, string tabName)
        {
            try
            {
                var screenshot = await TakeScreenshotAsync(instanceNumber, logger);
                if (screenshot == null)
                {
                    logger.LogError($"Failed to take screenshot for {tabName} tab detection");
                    return false;
                }

                if (_templateMatcher == null)
                {
                    _templateMatcher = new UnifiedTemplateMatchingService(logger);
                }

                var templatePath = Path.Combine(ImageTemplateFolder, tabImageName);
                if (!File.Exists(templatePath))
                {
                    logger.LogError($"Tab image template not found: {templatePath}");
                    return false;
                }

                logger.LogInfo($"Looking for {tabName} tab using image detection...");
                
                // Use enhanced matching with UI element flag for chapter tab (enables color matching)
                bool isChapterTab = tabImageName.Equals("chapter.png", StringComparison.OrdinalIgnoreCase);
                var result = _templateMatcher.MatchTemplate(
                    screenshot,
                    templatePath,
                    instanceNumber,
                    threshold: TAB_CONFIDENCE_THRESHOLD,
                    scales: UnifiedTemplateMatchingService.StandardScales,
                    verboseLogging: true,
                    searchArea: null,
                    isUIElement: isChapterTab  // Enable color matching for chapter tab only
                );

                if (result.found && result.confidence >= TAB_CONFIDENCE_THRESHOLD)
                {
                    logger.LogInfo($"Found {tabName} tab with confidence {result.confidence:F3}, clicking...");
                    return await ClickRandomInRectAsync(instanceNumber, logger, result.matchRect);
                }
                else
                {
                    logger.LogWarning($"{tabName} tab not found or confidence {result.confidence:F3} below threshold {TAB_CONFIDENCE_THRESHOLD}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"Error finding and clicking {tabName} tab: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Re-opens the missions menu after Chapter claim-all takes bot back to base view
        /// </summary>
        private async Task<bool> ReopenMissionsMenuAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            logger.LogInfo($"[{account.AccountName}] Looking for missions scroll button to re-open menu...");
            return await WaitForAndClickImageAsync("missions-scroll.png", account.InstanceNumber, logger, ImageWaitTimeSeconds, cancellationToken);
        }

        /// <summary>Tries the OCR configurations in turn; returns the text of the first one that finds digits, or null.</summary>
        private static async Task<string?> ReadNotificationNumberAsync(
            byte[] screenshot, Rectangle area, string tabName, LogService logger, bool debugMode, string debugFolder)
        {
            // Test reduced OCR configurations for better performance
            var configs = new[]
            {
                new OcrConfiguration { CharacterWhitelist = "0123456789", ScaleFactor = 2.0f, PageSegMode = PageSegMode.SingleChar },
                new OcrConfiguration { CharacterWhitelist = "0123456789", ScaleFactor = 3.0f, PageSegMode = PageSegMode.SingleChar },
            };

            for (int i = 0; i < configs.Length; i++)
            {
                var config = configs[i];

                try
                {
                    using var ocrService = new OcrService(logger, config);
                    var text = ocrService.ExtractTextFromScreenArea(screenshot, area);

                    // Check if any digits were found; stop after the first valid result to improve performance
                    if (!string.IsNullOrWhiteSpace(text) && text.Any(char.IsDigit))
                    {
                        return text.Trim();
                    }

                    // Save debug preprocessed image for this config if enabled
                    if (debugMode)
                    {
                        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                        await SaveDebugPreprocessedImageAsync(screenshot, area, config,
                            Path.Combine(debugFolder, $"{timestamp}_config{i + 1}_scale{config.ScaleFactor}_seg{config.PageSegMode}.png"), logger);
                    }
                }
                catch (Exception configEx)
                {
                    // Only log OCR failures in debug mode
                    if (debugMode)
                    {
                        logger.LogWarning($"[{tabName}] OCR config {i + 1} failed: {configEx.Message}");
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Detects if there are notification numbers in the specified area using OCR with enhanced debugging
        /// </summary>
        private async Task<bool> DetectNotificationNumber(Rectangle area, int instanceNumber, LogService logger, string tabName)
        {
            try
            {
                // Take screenshot
                var screenshot = await TakeScreenshotAsync(instanceNumber, logger);
                if (screenshot == null)
                {
                    logger.LogWarning($"Failed to take screenshot for {tabName} notification detection");
                    return false;
                }

                // Save debug images if enabled
                bool debugMode = false; // Disabled to reduce log spam
                string debugFolder = Path.Combine(AppContext.BaseDirectory, "debug", "ocr", tabName.ToLower());
                
                if (debugMode)
                {
                    Directory.CreateDirectory(debugFolder);
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                    
                    // Save original screenshot crop
                    await SaveDebugImageAsync(screenshot, area, Path.Combine(debugFolder, $"{timestamp}_original.png"), logger);
                }

                string? bestResult = await ReadNotificationNumberAsync(screenshot, area, tabName, logger, debugMode, debugFolder);
                bool anyNumbersDetected = bestResult != null;

                // Log simplified results
                if (anyNumbersDetected)
                {
                    logger.LogInfo($"[{tabName}] Notification detected: '{bestResult}'");
                }
                else
                {
                    logger.LogInfo($"[{tabName}] No notifications detected");

                    // Additional debugging for failed detection (only in debug mode)
                    if (debugMode)
                    {
                        await LogDetailedAreaAnalysis(screenshot, area, tabName, logger);
                    }
                }

                return anyNumbersDetected;
            }
            catch (Exception ex)
            {
                logger.LogError($"Error detecting {tabName} notifications: {ex.Message}");
                return true; // Default to processing if OCR fails
            }
        }

        private async Task ClickRewardImageAsync(int instanceNumber, LogService logger, CancellationToken cancellationToken, int clickCount, double confidence)
        {
            logger.LogInfo($"Detected reward-image (click #{clickCount}) with confidence {confidence:F3}, clicking it...");

            if (await FindAndClickRewardImageAsync(instanceNumber, logger))
            {
                logger.LogInfo($"Successfully clicked reward-image #{clickCount}");
                await Task.Delay(500, cancellationToken); // Wait 0.5s after clicking
            }
            else
            {
                logger.LogWarning($"Failed to click reward-image on attempt #{clickCount}");
            }
        }

        private const double InitialRewardWaitSeconds = 3.0; // Wait up to 3 seconds for initial detection

        /// <summary>The reward image was not detected: logs how long it still waits and tells whether the wait is over.</summary>
        private static bool IsRewardWaitOver(
            LogService logger, string context, bool hasEverBeenDetected, DateTime startTime, DateTime lastDetectionTime,
            double bestConfidence, double confidence, int clickCount)
        {
            var timeSinceStart = DateTime.UtcNow - startTime;
            var timeSinceLastDetection = DateTime.UtcNow - lastDetectionTime;
            var inContext = string.IsNullOrEmpty(context) ? "" : $" in {context}";

            if (!hasEverBeenDetected)
            {
                // Never found reward-image, check if we should keep waiting for initial detection
                if (timeSinceStart.TotalSeconds >= InitialRewardWaitSeconds)
                {
                    logger.LogInfo($"reward-image not found after {InitialRewardWaitSeconds} seconds{inContext} (best confidence: {bestConfidence:F3}, threshold: 0.6), continuing...");
                    return true;
                }

                var remainingWait = InitialRewardWaitSeconds - timeSinceStart.TotalSeconds;
                logger.LogInfo($"reward-image not detected (confidence: {confidence:F3}), waiting {remainingWait:F1} more seconds for initial detection...");
                return false;
            }

            if (timeSinceLastDetection.TotalSeconds >= 5.0)
            {
                // Haven't seen it for 5 seconds, we're done
                logger.LogInfo($"reward-image not detected for 5 seconds after {clickCount} clicks{inContext}, finished processing");
                return true;
            }

            // Still within the 5-second wait period
            var remainingWaitAfterDetection = 5.0 - timeSinceLastDetection.TotalSeconds;
            logger.LogInfo($"reward-image not detected, waiting {remainingWaitAfterDetection:F1} more seconds...");
            return false;
        }

        /// <summary>
        /// Processes reward-image.png by clicking it repeatedly until it's no longer detected.
        /// Waits 5 seconds for it to disappear, checking every 0.5 seconds.
        /// If detected again during the wait, clicks it and restarts the timer.
        /// </summary>
        private async Task ProcessRewardImageAsync(int instanceNumber, LogService logger, CancellationToken cancellationToken, string context = "")
        {
            int clickCount = 0;
            var startTime = DateTime.UtcNow;
            var lastDetectionTime = DateTime.UtcNow;
            bool hasEverBeenDetected = false;

            logger.LogInfo($"Starting reward-image processing{(string.IsNullOrEmpty(context) ? "" : $" for {context}")}...");
            
            double bestConfidence = 0.0;
            
            while (!cancellationToken.IsCancellationRequested)
            {
                // Check if reward-image is present (from claimmissions folder)
                var (isPresent, confidence) = await IsRewardImagePresentAsync(instanceNumber, logger);
                
                // Track best confidence seen
                if (confidence > bestConfidence)
                {
                    bestConfidence = confidence;
                }
                
                if (isPresent)
                {
                    hasEverBeenDetected = true;
                    lastDetectionTime = DateTime.UtcNow;
                    clickCount++;
                    await ClickRewardImageAsync(instanceNumber, logger, cancellationToken, clickCount, confidence);
                }
                else if (IsRewardWaitOver(logger, context, hasEverBeenDetected, startTime, lastDetectionTime, bestConfidence, confidence, clickCount))
                {
                    // Not detected and nothing left to wait for
                    break;
                }
                
                // Wait 0.5 seconds before next check
                await Task.Delay(500, cancellationToken);
            }
            
            if (hasEverBeenDetected)
            {
                logger.LogInfo($"Completed reward-image processing: {clickCount} total clicks{(string.IsNullOrEmpty(context) ? "" : $" in {context}")}");
            }
        }

        protected override string GetImageFolderName()
        {
            return "claimmissions";
        }

        /// <summary>
        /// Saves a debug image of the cropped area for analysis
        /// </summary>
        private static async Task SaveDebugImageAsync(byte[] screenshot, Rectangle area, string filePath, LogService logger)
        {
            try
            {
                using var ms = new MemoryStream(screenshot);
                using var fullBitmap = new Bitmap(ms);
                using var croppedBitmap = CropBitmap(fullBitmap, area);
                croppedBitmap.Save(filePath);
                logger.LogInfo($"Debug image saved: {filePath}");
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Failed to save debug image {filePath}: {ex.Message}");
            }
        }

        /// <summary>
        /// Saves a debug image showing the preprocessed version used by OCR
        /// </summary>
        private static async Task SaveDebugPreprocessedImageAsync(byte[] screenshot, Rectangle area, OcrConfiguration config, string filePath, LogService logger)
        {
            try
            {
                using var ms = new MemoryStream(screenshot);
                using var fullBitmap = new Bitmap(ms);
                using var croppedBitmap = CropBitmap(fullBitmap, area);
                using var preprocessedBitmap = PreprocessImageForDebug(croppedBitmap, config);
                preprocessedBitmap.Save(filePath);
                logger.LogInfo($"Debug preprocessed image saved: {filePath}");
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Failed to save debug preprocessed image {filePath}: {ex.Message}");
            }
        }

        /// <summary>
        /// Crops a bitmap to the specified area
        /// </summary>
        private static Bitmap CropBitmap(Bitmap source, Rectangle cropArea)
        {
            try
            {
                var validCropArea = Rectangle.Intersect(cropArea, new Rectangle(0, 0, source.Width, source.Height));
                
                if (validCropArea.IsEmpty || validCropArea.Width <= 0 || validCropArea.Height <= 0)
                {
                    return new Bitmap(1, 1);
                }

                return source.Clone(validCropArea, source.PixelFormat);
            }
            catch (Exception)
            {
                return new Bitmap(1, 1);
            }
        }

        /// <summary>
        /// Applies the same preprocessing as OcrService for debug visualization
        /// </summary>
        private static Bitmap PreprocessImageForDebug(Bitmap bmp, OcrConfiguration config)
        {
            try
            {
                using var sourceMat = OpenCvSharp.Extensions.BitmapConverter.ToMat(bmp);
                
                // Convert to Grayscale
                using var gray = new OpenCvSharp.Mat();
                OpenCvSharp.Cv2.CvtColor(sourceMat, gray, OpenCvSharp.ColorConversionCodes.BGR2GRAY);

                // Scale the image
                using var scaled = new OpenCvSharp.Mat();
                if (config.ScaleFactor > 1.0f)
                {
                    OpenCvSharp.Cv2.Resize(gray, scaled, new OpenCvSharp.Size(0, 0), config.ScaleFactor, config.ScaleFactor, OpenCvSharp.InterpolationFlags.Cubic);
                }
                else
                {
                    gray.CopyTo(scaled);
                }

                // Invert the image
                using var inverted = new OpenCvSharp.Mat();
                OpenCvSharp.Cv2.BitwiseNot(scaled, inverted);

                // Apply Otsu's thresholding
                using var thresholded = new OpenCvSharp.Mat();
                OpenCvSharp.Cv2.Threshold(inverted, thresholded, 0, 255, OpenCvSharp.ThresholdTypes.Binary | OpenCvSharp.ThresholdTypes.Otsu);

                // Dilate the text
                using var kernel = OpenCvSharp.Cv2.GetStructuringElement(OpenCvSharp.MorphShapes.Rect, new OpenCvSharp.Size(2, 2));
                using var dilated = new OpenCvSharp.Mat();
                OpenCvSharp.Cv2.Dilate(thresholded, dilated, kernel);

                return OpenCvSharp.Extensions.BitmapConverter.ToBitmap(dilated);
            }
            catch (Exception)
            {
                return new Bitmap(bmp); // Return copy of original on error
            }
        }

        /// <summary>
        /// Performs detailed analysis of the notification area when OCR fails
        /// </summary>
        private static async Task LogDetailedAreaAnalysis(byte[] screenshot, Rectangle area, string tabName, LogService logger)
        {
            try
            {
                using var ms = new MemoryStream(screenshot);
                using var fullBitmap = new Bitmap(ms);
                using var croppedBitmap = CropBitmap(fullBitmap, area);

                // Basic image properties
                logger.LogInfo($"[{tabName}] Area analysis - Size: {croppedBitmap.Width}x{croppedBitmap.Height}, Format: {croppedBitmap.PixelFormat}");

                // Color analysis
                var dominantColors = AnalyzeDominantColors(croppedBitmap);
                logger.LogInfo($"[{tabName}] Dominant colors: {string.Join(", ", dominantColors.Select(c => $"#{c.R:X2}{c.G:X2}{c.B:X2}"))}");

                // Contrast analysis
                var contrast = CalculateContrast(croppedBitmap);
                logger.LogInfo($"[{tabName}] Image contrast score: {contrast:F2}");

                // Pixel intensity distribution
                var (minIntensity, maxIntensity, avgIntensity) = AnalyzeIntensity(croppedBitmap);
                logger.LogInfo($"[{tabName}] Intensity - Min: {minIntensity}, Max: {maxIntensity}, Avg: {avgIntensity:F1}");

                // Check if area appears to contain any text-like shapes
                bool hasTextLikeShapes = DetectTextLikeShapes(croppedBitmap);
                logger.LogInfo($"[{tabName}] Contains text-like shapes: {hasTextLikeShapes}");
            }
            catch (Exception ex)
            {
                logger.LogWarning($"[{tabName}] Failed to perform detailed area analysis: {ex.Message}");
            }
        }

        /// <summary>
        /// Analyzes the dominant colors in the image
        /// </summary>
        private static List<Color> AnalyzeDominantColors(Bitmap bitmap)
        {
            var colorCounts = new Dictionary<Color, int>();
            
            for (int x = 0; x < bitmap.Width; x++)
            {
                for (int y = 0; y < bitmap.Height; y++)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    if (colorCounts.ContainsKey(pixel))
                        colorCounts[pixel]++;
                    else
                        colorCounts[pixel] = 1;
                }
            }
            
            return colorCounts.OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key).ToList();
        }

        /// <summary>
        /// Calculates the contrast of the image
        /// </summary>
        private static double CalculateContrast(Bitmap bitmap)
        {
            double sumSquares = 0;
            double sum = 0;
            int pixelCount = 0;
            
            for (int x = 0; x < bitmap.Width; x++)
            {
                for (int y = 0; y < bitmap.Height; y++)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    var intensity = (pixel.R + pixel.G + pixel.B) / 3.0;
                    sum += intensity;
                    sumSquares += intensity * intensity;
                    pixelCount++;
                }
            }
            
            double mean = sum / pixelCount;
            double variance = (sumSquares / pixelCount) - (mean * mean);
            return Math.Sqrt(variance); // Standard deviation as contrast measure
        }

        /// <summary>
        /// Analyzes the intensity distribution of the image
        /// </summary>
        private static (int min, int max, double avg) AnalyzeIntensity(Bitmap bitmap)
        {
            int min = 255, max = 0;
            double sum = 0;
            int pixelCount = 0;
            
            for (int x = 0; x < bitmap.Width; x++)
            {
                for (int y = 0; y < bitmap.Height; y++)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    var intensity = (pixel.R + pixel.G + pixel.B) / 3;
                    min = Math.Min(min, intensity);
                    max = Math.Max(max, intensity);
                    sum += intensity;
                    pixelCount++;
                }
            }
            
            return (min, max, sum / pixelCount);
        }

        /// <summary>
        /// Detects if the image contains text-like shapes using edge detection
        /// </summary>
        private static bool DetectTextLikeShapes(Bitmap bitmap)
        {
            try
            {
                using var mat = OpenCvSharp.Extensions.BitmapConverter.ToMat(bitmap);
                using var gray = new OpenCvSharp.Mat();
                OpenCvSharp.Cv2.CvtColor(mat, gray, OpenCvSharp.ColorConversionCodes.BGR2GRAY);
                
                using var edges = new OpenCvSharp.Mat();
                OpenCvSharp.Cv2.Canny(gray, edges, 50, 150);
                
                // Count edge pixels
                int edgePixels = OpenCvSharp.Cv2.CountNonZero(edges);
                int totalPixels = edges.Width * edges.Height;
                double edgeRatio = (double)edgePixels / totalPixels;
                
                // If more than 5% of pixels are edges, likely contains shapes
                return edgeRatio > 0.05;
            }
            catch
            {
                return false;
            }
        }
        
        private async Task<(bool found, double confidence)> IsRewardImagePresentAsync(int instanceNumber, LogService logger)
        {
            var screenshot = await TakeScreenshotAsync(instanceNumber, logger);
            if (screenshot == null) return (false, 0.0);
            
            string rewardImagePath = Path.Combine(ImageTemplateFolder, "reward-image.png");
            if (!File.Exists(rewardImagePath))
            {
                logger.LogError($"Image template not found: {rewardImagePath}");
                return (false, 0.0);
            }
            
            var result = _templateMatcher.MatchTemplate(
                screenshot,
                rewardImagePath,
                instanceNumber,
                0.6
            );
            
            return (result.found, result.confidence);
        }
        
        private async Task<bool> FindAndClickRewardImageAsync(int instanceNumber, LogService logger)
        {
            var screenshot = await TakeScreenshotAsync(instanceNumber, logger);
            if (screenshot == null) return false;
            
            string rewardImagePath = Path.Combine(ImageTemplateFolder, "reward-image.png");
            if (!File.Exists(rewardImagePath))
            {
                logger.LogError($"Image template not found: {rewardImagePath}");
                return false;
            }
            
            var result = _templateMatcher.MatchTemplate(
                screenshot,
                rewardImagePath,
                instanceNumber,
                0.6
            );
            
            if (result.found)
            {
                var clickPos = result.matchRect.GetCenter();
                await ClickAsync(instanceNumber, logger, clickPos);
                return true;
            }
            
            return false;
        }

    }
}