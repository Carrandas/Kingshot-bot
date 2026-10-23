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
using System.Collections.Concurrent;
using System.Text.Json;
using System.Linq;

namespace Bot.Core.Tasks.Modules
{
    public class CollectVipTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.CollectVip;
        public override string Name => "Collect Vip";

        private LocatorService? _locatorService;
        
        // Static dictionary to track last collection times per instance
        private static readonly ConcurrentDictionary<int, DateTime> _lastCollectionTimes = new();
        private static readonly string TimeStorageFile = Path.Combine(
            AppContext.BaseDirectory, "data", "vip_collection_times.json"
        );
        
        // Collection interval when both VIP rewards are collected
        private static readonly TimeSpan VipRewardsInterval = TimeSpan.FromHours(12);
        
        // Track if VIP rewards were collected
        private bool _vipRewardsClicked = false;
        private bool _vipRewards2Clicked = false;
        
        // Search areas for specific UI elements
        private static readonly Rectangle VipChestSearchArea = new Rectangle(580, 235, 97, 96);  // 580,235 to 677,331
        private static readonly Rectangle VipClaimSearchArea = new Rectangle(480, 780, 180, 80); // 480,780 to 660,860
        // teal + next to the VIP XP bar; the same + exists on other screens (Hall of Heroes), so only search here
        private static readonly Rectangle VipPlusSearchArea = new Rectangle(470, 230, 130, 110);

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
                _locatorService = new LocatorService(logger, account, TaskType);
                
                // Reset VIP reward tracking flags
                _vipRewardsClicked = false;
                _vipRewards2Clicked = false;
                
                // Load collection times
                LoadCollectionTimes();
                
                // Check if enough time has passed since last collection
                if (!ShouldCollect(account.InstanceNumber))
                {
                    var nextCollection = GetNextCollectionTime(account.InstanceNumber);
                    logger.LogInfo($"[{account.AccountName}] Too soon to collect VIP rewards. Next collection at: {nextCollection}");
                    return new TaskExecutionDetails(true, nextCollection, "Skipped - Too soon to collect");
                }

                logger.LogInfo($"[{account.AccountName}] Starting VIP collection process...");

                // Step 1: Navigate to base mode
                logger.LogInfo($"[{account.AccountName}] Navigating to base view...");
                if (!await _locatorService.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken))
                {
                    logger.LogError($"[{account.AccountName}] Failed to navigate to base view");
                    return TaskExecutionDetails.Failed("Failed to navigate to base view");
                }

                // Step 2: Find and click VIP button (with color template matching)
                logger.LogInfo($"[{account.AccountName}] Looking for VIP button...");
                if (!await FindAndClickImageWithColorAsync("vip.png", account.InstanceNumber, logger, cancellationToken))
                {
                    logger.LogWarning($"[{account.AccountName}] VIP button not found");
                    return TaskExecutionDetails.Failed("VIP button not found");
                }

                await Task.Delay(1000, cancellationToken); // Wait for VIP interface to load

                // Step 2.5: Check if VIP interface loaded properly by looking for VIP chest or VIP claim
                logger.LogInfo($"[{account.AccountName}] Checking if VIP interface loaded...");
                var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                if (screenshot != null && _templateMatcher != null)
                {
                    // First check for VIP chest
                    var vipChestPath = Path.Combine(ImageTemplateFolder, "vip-chest.png");
                    bool vipChestFound = false;
                    if (File.Exists(vipChestPath))
                    {
                        var vipChestResult = _templateMatcher.MatchTemplate(
                            screenshot,
                            vipChestPath,
                            account.InstanceNumber,
                            threshold: GameCoordinates.Thresholds.StandardConfidence,
                            scales: UnifiedTemplateMatchingService.StandardScales,
                            verboseLogging: false,
                            searchArea: VipChestSearchArea
                        );
                        vipChestFound = vipChestResult.found;
                    }
                    
                    // If VIP chest not found, check for VIP claim
                    bool vipClaimFound = false;
                    if (!vipChestFound)
                    {
                        var vipClaimPath = Path.Combine(ImageTemplateFolder, "vip-claim.png");
                        if (File.Exists(vipClaimPath))
                        {
                            var vipClaimResult = _templateMatcher.MatchTemplate(
                                screenshot,
                                vipClaimPath,
                                account.InstanceNumber,
                                threshold: GameCoordinates.Thresholds.StandardConfidence,
                                scales: UnifiedTemplateMatchingService.StandardScales,
                                verboseLogging: false,
                                searchArea: VipClaimSearchArea
                            );
                            vipClaimFound = vipClaimResult.found;
                        }
                    }
                    
                    // If neither VIP chest nor VIP claim found, click back button
                    if (!vipChestFound && !vipClaimFound)
                    {
                        logger.LogWarning($"[{account.AccountName}] VIP interface did not load properly (neither chest nor claim found) - clicking deploy-back button");
                        if (await FindAndClickButtonImageAsync("deploy-back.png", account.InstanceNumber, logger))
                        {
                            logger.LogInfo($"[{account.AccountName}] Successfully clicked deploy-back button");
                            return TaskExecutionDetails.Failed("VIP interface did not load - returned to base");
                        }
                        return TaskExecutionDetails.Failed("VIP interface did not load and could not return to base");
                    }
                }

                // Step 3: Find and click VIP chest (with color template matching in specific area)
                logger.LogInfo($"[{account.AccountName}] Looking for VIP chest in area {VipChestSearchArea}...");
                // vip-chest.png is the red dot on the daily sign-in chest; no dot means it was already claimed today
                bool chestClicked = await FindAndClickImageWithColorAsync("vip-chest.png", account.InstanceNumber, logger, cancellationToken, VipChestSearchArea);
                if (!chestClicked)
                {
                    logger.LogInfo($"[{account.AccountName}] VIP chest has no red dot (already claimed), continuing with the daily bundle...");
                }
                else
                {
                    await Task.Delay(1000, cancellationToken); // Wait for chest interface to load

                    // Step 3.5: Check for vip-rewards.png after clicking vip-chest (with 5-second timeout, the popup can take >2 s to appear)
                    logger.LogInfo($"[{account.AccountName}] Looking for VIP rewards template: templates\\images\\collectvip\\vip-rewards.png (up to 5 seconds)...");
                    bool vipRewardsFound = false;
                    var vipRewardsStartTime = DateTime.UtcNow;
                    int attemptCount = 0;

                    while (!vipRewardsFound && (DateTime.UtcNow - vipRewardsStartTime).TotalSeconds < 5)
                    {
                        attemptCount++;
                        logger.LogInfo($"[{account.AccountName}] Searching for vip-rewards.png - Attempt {attemptCount}");
                        
                        if (await FindAndClickImageAsync("vip-rewards.png", account.InstanceNumber, logger, threshold: 0.8, useEnhancedMatching: true))
                        {
                            logger.LogInfo($"[{account.AccountName}] ✅ VIP rewards found and clicked at attempt {attemptCount}");
                            _vipRewardsClicked = true; // Track that we clicked vip-rewards
                            vipRewardsFound = true;
                            await Task.Delay(1000, cancellationToken); // Wait for rewards to be collected
                        }
                        else if (!vipRewardsFound)
                        {
                            logger.LogInfo($"[{account.AccountName}] VIP rewards not found in attempt {attemptCount}, waiting 200ms before retry");
                            await Task.Delay(200, cancellationToken); // Wait 200ms before retry
                        }
                    }
                    
                    if (!vipRewardsFound)
                    {
                        logger.LogInfo($"[{account.AccountName}] ❌ VIP rewards (templates\\images\\collectvip\\vip-rewards.png) not found after {attemptCount} attempts in 5 seconds, continuing...");
                    }
                }

                // Step 4: Find and click VIP claim button (regular template matching in specific area)
                logger.LogInfo($"[{account.AccountName}] Looking for VIP claim button in area {VipClaimSearchArea}...");
                if (!await FindAndClickImageInAreaAsync("vip-claim.png", account.InstanceNumber, logger, VipClaimSearchArea))
                {
                    logger.LogWarning($"[{account.AccountName}] VIP claim button not found in search area, continuing to next step...");
                }
                else
                {
                    await Task.Delay(1000, cancellationToken); // Wait for claim to process

                    // Step 4.5: Check for vip-rewards2.png after clicking vip-claim (5 s, the popup animates in late)
                    logger.LogInfo($"[{account.AccountName}] Looking for VIP rewards 2 (up to 5 seconds)...");
                    bool vipRewards2Found = false;
                    var vipRewards2StartTime = DateTime.UtcNow;
                    
                    while (!vipRewards2Found && (DateTime.UtcNow - vipRewards2StartTime).TotalSeconds < 5)
                    {
                        if (await FindAndClickImageAsync("vip-rewards2.png", account.InstanceNumber, logger, threshold: 0.8, useEnhancedMatching: true))
                        {
                            logger.LogInfo($"[{account.AccountName}] VIP rewards 2 clicked");
                            _vipRewards2Clicked = true; // Track that we clicked vip-rewards2
                            vipRewards2Found = true;
                            await Task.Delay(1000, cancellationToken); // Wait for rewards to be collected
                        }
                        else if (!vipRewards2Found)
                        {
                            await Task.Delay(200, cancellationToken); // Wait 200ms before retry
                        }
                    }
                    
                    if (!vipRewards2Found)
                    {
                        logger.LogInfo($"[{account.AccountName}] VIP rewards 2 not found after 5 seconds, continuing...");
                    }
                }

                // Step 5: Find and click VIP plus button
                logger.LogInfo($"[{account.AccountName}] Looking for VIP plus button...");
                if (!await FindAndClickImageAsync("vip-plus.png", account.InstanceNumber, logger, threshold: 0.85, searchArea: VipPlusSearchArea))
                {
                    logger.LogWarning($"[{account.AccountName}] VIP plus button not found");
                    // don't leave the VIP screen open for the next task
                    await FindAndClickButtonImageAsync("deploy-back.png", account.InstanceNumber, logger);
                    await _locatorService.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
                    return TaskExecutionDetails.Failed("VIP plus button not found");
                }

                await Task.Delay(1000, cancellationToken); // Wait for plus interface to load

                // Step 6: use the VIP XP items we own. Tap the green Use once; a "xN" (use all) bubble then appears
                // to its left, and tapping that bubble uses every remaining item of the row in one go.
                // SAFETY: the orange "Buy & Use" rows (and their orange gem-cost bubbles) spend gems and are never tapped;
                // the bubble is only tapped when the spot is verified green.
                logger.LogInfo($"[{account.AccountName}] Starting VIP use step...");
                int useCount = 0;
                const int maxUseRounds = 4; // one round per owned item row (10 / 100 / 1K XP ...)

                for (int round = 0; round < maxUseRounds; round++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var use = await FindVipUseButtonAsync(account.InstanceNumber, logger);
                    if (!use.found)
                    {
                        logger.LogInfo($"[{account.AccountName}] No green VIP use button left, done");
                        break;
                    }

                    var useCenter = new Point(use.rect.X + use.rect.Width / 2, use.rect.Y + use.rect.Height / 2);
                    logger.LogInfo($"[{account.AccountName}] Tapping Use once (round {round + 1})...");
                    await ClickAsync(account.InstanceNumber, logger, useCenter);
                    await Task.Delay(1000, cancellationToken); // the bubble appears next to the row

                    // bubble: ~200px left of Use; verify it is green (not an orange gem-cost bubble) along its top edge,
                    // above the "VIP XP×10 obtained" toast that covers the middle of the row
                    var bubbleTap = new Point(useCenter.X - 200, useCenter.Y);
                    var probe = new Point(useCenter.X - 200, useCenter.Y - 24);
                    if (await IsGreenAtAsync(account.InstanceNumber, logger, probe))
                    {
                        logger.LogInfo($"[{account.AccountName}] Green use-all bubble found, tapping it...");
                        await ClickAsync(account.InstanceNumber, logger, bubbleTap);
                        useCount++;
                        await Task.Delay(1500, cancellationToken); // toast + the list re-lays out
                    }
                    else
                    {
                        logger.LogInfo($"[{account.AccountName}] No green use-all bubble next to this row (single item or already used)");
                        await Task.Delay(1500, cancellationToken);
                    }
                }

                logger.LogInfo($"[{account.AccountName}] VIP use step finished: used {useCount} stack(s) with the bubble");

                // Step 7: Close level dialog
                logger.LogInfo($"[{account.AccountName}] Looking for level close button...");
                if (!await FindAndClickImageAsync("level-close.png", account.InstanceNumber, logger))
                {
                    logger.LogWarning($"[{account.AccountName}] Level close button not found, continuing...");
                }

                await Task.Delay(500, cancellationToken); // Wait for close to process

                // Step 8: Click deploy back button
                logger.LogInfo($"[{account.AccountName}] Looking for deploy back button...");
                if (!await FindAndClickButtonImageAsync("deploy-back.png", account.InstanceNumber, logger))
                {
                    logger.LogWarning($"[{account.AccountName}] Deploy back button not found, continuing...");
                }

                await Task.Delay(500, cancellationToken); // Wait for navigation

                // Update collection time only if both VIP rewards were clicked
                if (_vipRewardsClicked && _vipRewards2Clicked)
                {
                    UpdateCollectionTime(account.InstanceNumber);
                    logger.LogInfo($"[{account.AccountName}] ✅ Both VIP rewards collected - applying 12-hour cooldown");
                }
                else
                {
                    logger.LogInfo($"[{account.AccountName}] ℹ️ Not all VIP rewards were collected - no cooldown applied");
                    logger.LogInfo($"[{account.AccountName}] VIP Rewards: {(_vipRewardsClicked ? "Collected" : "Not found")}, VIP Rewards 2: {(_vipRewards2Clicked ? "Collected" : "Not found")}");
                }

                logger.LogInfo($"[{account.AccountName}] VIP collection process completed successfully");
                return TaskExecutionDetails.Succeeded();
            }
            catch (OperationCanceledException)
            {
                logger.LogInfo($"[{account.AccountName}] VIP collection task was cancelled");
                return TaskExecutionDetails.Failed("Task was cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error during VIP collection: {ex.Message}");
                return TaskExecutionDetails.Failed(ex.Message);
            }
        }


        /// <summary>Finds the green "Use" button of an owned VIP XP item row.</summary>
        private async Task<(bool found, Rectangle rect)> FindVipUseButtonAsync(int instanceNumber, LogService logger)
        {
            var screenshot = await TakeScreenshotAsync(instanceNumber, logger);
            if (screenshot == null) return (false, Rectangle.Empty);

            if (_templateMatcher == null) _templateMatcher = new UnifiedTemplateMatchingService(logger);
            var path = Path.Combine(ImageTemplateFolder, "vip-use.png");
            var result = _templateMatcher.MatchTemplate(
                screenshot, path, instanceNumber,
                threshold: 0.80,
                scales: new[] { 1.0 },
                verboseLogging: false,
                searchArea: new Rectangle(440, 300, 280, 800));
            return (result.found, result.matchRect);
        }

        /// <summary>True when the average colour around the point is clearly green (hue 80-160, saturation >= 0.5; the orange gem bubbles are ~23).</summary>
        private async Task<bool> IsGreenAtAsync(int instanceNumber, LogService logger, Point point)
        {
            var screenshot = await TakeScreenshotAsync(instanceNumber, logger);
            if (screenshot == null) return false;

            try
            {
                using var ms = new MemoryStream(screenshot);
                using var bmp = new Bitmap(ms);
                if (point.X < 4 || point.Y < 4 || point.X >= bmp.Width - 4 || point.Y >= bmp.Height - 4) return false;

                long r = 0, g = 0, b = 0; int n = 0;
                for (int dx = -3; dx <= 3; dx++)
                    for (int dy = -3; dy <= 3; dy++)
                    {
                        var c = bmp.GetPixel(point.X + dx, point.Y + dy);
                        r += c.R; g += c.G; b += c.B; n++;
                    }
                var avg = Color.FromArgb((int)(r / n), (int)(g / n), (int)(b / n));
                float hue = avg.GetHue();
                float sat = avg.GetSaturation();
                logger.LogInfo($"Colour probe at ({point.X},{point.Y}): hue={hue:F0} sat={sat:F2}");
                return hue >= 80 && hue <= 160 && sat >= 0.5f;
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Colour probe failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Find and click image using color template matching (enhanced matching)
        /// </summary>
        private async Task<bool> FindAndClickImageWithColorAsync(string imageName, int instanceNumber, LogService logger, CancellationToken cancellationToken, Rectangle? searchArea = null)
        {
            try
            {
                var screenshot = await TakeScreenshotAsync(instanceNumber, logger);
                if (screenshot == null)
                {
                    logger.LogWarning($"Failed to get screenshot when looking for '{imageName}'");
                    return false;
                }

                if (_templateMatcher == null)
                {
                    _templateMatcher = new UnifiedTemplateMatchingService(logger);
                }

                var templatePath = Path.Combine(ImageTemplateFolder, imageName);
                if (!File.Exists(templatePath))
                {
                    logger.LogError($"Image template not found: {templatePath}");
                    return false;
                }

                // Use enhanced matching with color template matching
                var result = _templateMatcher.MatchTemplate(
                    screenshot,
                    templatePath,
                    instanceNumber,
                    threshold: GameCoordinates.Thresholds.StandardConfidence,
                    scales: UnifiedTemplateMatchingService.StandardScales,
                    verboseLogging: false,
                    searchArea: searchArea
                );

                if (result.found)
                {
                    logger.LogInfo($"Found '{imageName}' at {result.matchRect} with confidence {result.confidence:F3}");
                    return await ClickCenterInRectAsync(instanceNumber, logger, result.matchRect);
                }

                logger.LogWarning($"Could not find '{imageName}' with color matching (best confidence: {result.confidence:F3})");
                return false;
            }
            catch (Exception ex)
            {
                logger.LogError($"Error in FindAndClickImageWithColorAsync for '{imageName}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Find and click image from the buttons folder (shared UI elements)
        /// </summary>
        private async Task<bool> FindAndClickButtonImageAsync(string imageName, int instanceNumber, LogService logger)
        {
            try
            {
                var screenshot = await TakeScreenshotAsync(instanceNumber, logger);
                if (screenshot == null)
                {
                    logger.LogWarning($"Failed to get screenshot when looking for button '{imageName}'");
                    return false;
                }

                if (_templateMatcher == null)
                {
                    _templateMatcher = new UnifiedTemplateMatchingService(logger);
                }

                // Use buttons folder instead of collectvip folder
                var templatePath = Path.Combine(AppContext.BaseDirectory, "templates", "images", "buttons", imageName);
                if (!File.Exists(templatePath))
                {
                    logger.LogError($"Button template not found: {templatePath}");
                    return false;
                }

                var result = _templateMatcher.MatchTemplate(
                    screenshot,
                    templatePath,
                    instanceNumber,
                    threshold: GameCoordinates.Thresholds.StandardConfidence,
                    scales: UnifiedTemplateMatchingService.StandardScales,
                    verboseLogging: false
                );

                if (result.found)
                {
                    logger.LogInfo($"Found button '{imageName}' at {result.matchRect} with confidence {result.confidence:F3}");
                    return await ClickCenterInRectAsync(instanceNumber, logger, result.matchRect);
                }

                logger.LogWarning($"Could not find button '{imageName}' (best confidence: {result.confidence:F3})");
                return false;
            }
            catch (Exception ex)
            {
                logger.LogError($"Error in FindAndClickButtonImageAsync for '{imageName}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Find and click image using regular template matching in a specific area
        /// </summary>
        private async Task<bool> FindAndClickImageInAreaAsync(string imageName, int instanceNumber, LogService logger, Rectangle searchArea)
        {
            try
            {
                var screenshot = await TakeScreenshotAsync(instanceNumber, logger);
                if (screenshot == null)
                {
                    logger.LogWarning($"Failed to get screenshot when looking for '{imageName}'");
                    return false;
                }

                if (_templateMatcher == null)
                {
                    _templateMatcher = new UnifiedTemplateMatchingService(logger);
                }

                var templatePath = Path.Combine(ImageTemplateFolder, imageName);
                if (!File.Exists(templatePath))
                {
                    logger.LogError($"Image template not found: {templatePath}");
                    return false;
                }

                // Use regular template matching with search area
                var result = _templateMatcher.MatchTemplate(
                    screenshot,
                    templatePath,
                    instanceNumber,
                    threshold: GameCoordinates.Thresholds.StandardConfidence,
                    scales: UnifiedTemplateMatchingService.StandardScales,
                    verboseLogging: false,
                    searchArea: searchArea
                );

                if (result.found)
                {
                    logger.LogInfo($"Found '{imageName}' at {result.matchRect} with confidence {result.confidence:F3}");
                    return await ClickCenterInRectAsync(instanceNumber, logger, result.matchRect);
                }

                logger.LogWarning($"Could not find '{imageName}' in search area {searchArea} (best confidence: {result.confidence:F3})");
                return false;
            }
            catch (Exception ex)
            {
                logger.LogError($"Error in FindAndClickImageInAreaAsync for '{imageName}': {ex.Message}");
                return false;
            }
        }

        protected override string GetImageFolderName()
        {
            return "collectvip";
        }
        
        private bool ShouldCollect(int instanceNumber)
        {
            if (!_lastCollectionTimes.TryGetValue(instanceNumber, out DateTime lastCollection))
            {
                return true; // First time collection
            }

            var nextCollection = GetNextCollectionTime(instanceNumber);
            return DateTime.UtcNow >= nextCollection;
        }

        private DateTime GetNextCollectionTime(int instanceNumber)
        {
            if (!_lastCollectionTimes.TryGetValue(instanceNumber, out DateTime lastCollection))
            {
                return DateTime.UtcNow; // First time collection
            }

            // Use 12-hour interval when VIP rewards were collected
            return lastCollection.Add(VipRewardsInterval);
        }

        private void UpdateCollectionTime(int instanceNumber)
        {
            _lastCollectionTimes[instanceNumber] = DateTime.UtcNow;
            SaveCollectionTimes();
        }

        private void LoadCollectionTimes()
        {
            try
            {
                if (File.Exists(TimeStorageFile))
                {
                    var json = File.ReadAllText(TimeStorageFile);
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    var times = JsonSerializer.Deserialize<ConcurrentDictionary<int, DateTime>>(json, options);
                    if (times != null)
                    {
                        // Validate and clean up old entries
                        var now = DateTime.UtcNow;
                        foreach (var kvp in times)
                        {
                            // Only keep entries from the last 24 hours
                            if (now.Subtract(kvp.Value).TotalHours <= 24)
                            {
                                _lastCollectionTimes[kvp.Key] = kvp.Value;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CollectVipTask] Error loading collection times: {ex.Message}");
                _lastCollectionTimes.Clear();
            }
        }

        private void SaveCollectionTimes()
        {
            try
            {
                var directory = Path.GetDirectoryName(TimeStorageFile);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Validate times before saving
                var now = DateTime.UtcNow;
                var validTimes = _lastCollectionTimes
                    .Where(kvp => now.Subtract(kvp.Value).TotalHours <= 24)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(validTimes, options);
                File.WriteAllText(TimeStorageFile, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CollectVipTask] Error saving collection times: {ex.Message}");
            }
        }
    }
}