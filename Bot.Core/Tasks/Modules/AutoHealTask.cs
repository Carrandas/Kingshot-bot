using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.ImageDetection;
using Bot.Core.LDPlayer;
using Bot.Core.Services;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;

namespace Bot.Core.Tasks.Modules
{
    public class AutoHealTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.AutoHeal;
        public override string Name => "Auto Heal";
        protected override string GetImageFolderName() => "autoheal";
        
        private new readonly UnifiedTemplateMatchingService _templateMatcher = new UnifiedTemplateMatchingService(new LogService());
        private readonly string _templateFolder;
        private const double TEMPLATE_MATCH_THRESHOLD = 0.65;

        public AutoHealTask(LogService logger)
        {
            _templateFolder = Path.Combine(AppContext.BaseDirectory, "templates", "images", "autoheal");
        }

        private enum TapResult { Tapped, NotFound, GaveUpEarly }

        /// <summary>Looks for the template for up to 5 seconds and taps its center. With giveUpEarly the search stops after 4.5 seconds.</summary>
        private async Task<TapResult> TapTemplateAsync(
            object adbController, AccountSettings account, LogService logger, string templatePath, Rectangle searchArea,
            string description, string logSuffix, bool giveUpEarly, CancellationToken cancellationToken)
        {
            var startTime = DateTime.UtcNow;

            while ((DateTime.UtcNow - startTime).TotalSeconds < 5 && !cancellationToken.IsCancellationRequested)
            {
                var screenshot = await AdbMigrationHelper.TakeScreenshotAsync(adbController, logger, cancellationToken);
                var (isFound, rect, confidence) = _templateMatcher.MatchTemplate(
                    screenshot,
                    templatePath,
                    account.InstanceNumber,
                    TEMPLATE_MATCH_THRESHOLD,
                    searchArea: searchArea);

                if (isFound)
                {
                    var clickPoint = rect.GetCenter();
                    logger.LogInfo($"[AutoHeal] Found {description} at ({clickPoint.X}, {clickPoint.Y}) with confidence {confidence:F3}{logSuffix}");
                    await AdbMigrationHelper.TapAsync(adbController, clickPoint.X, clickPoint.Y, logger, cancellationToken);
                    await Task.Delay(500, cancellationToken); // Short delay after click
                    return TapResult.Tapped;
                }

                // If it's the first template (heal.png) and we can't find it, exit early
                if (giveUpEarly && (DateTime.UtcNow - startTime).TotalSeconds >= 4.5)
                {
                    return TapResult.GaveUpEarly;
                }

                await Task.Delay(200, cancellationToken);
            }
            return TapResult.NotFound;
        }

        /// <summary>The template was missing: let the locator restore the map view and look once more. Returns the failure, or null when the template was tapped.</summary>
        private async Task<TaskExecutionDetails?> RecoverAndRetryTapAsync(
            object adbController, AccountSettings account, LogService logger, string templatePath, Rectangle searchArea,
            string description, CancellationToken cancellationToken)
        {
            logger.LogError($"[{account.AccountName}] Error: Failed to find {Path.GetFileName(templatePath)}, calling locator module for recovery");
            var healLocator = new LocatorService(logger, account);
            try
            {
                await healLocator.EnsureViewAsync(ViewType.MapView, account.InstanceNumber, cancellationToken);
                logger.LogInfo($"[{account.AccountName}] Locator service completed view recovery, retrying {description} detection");

                // Retry the template matching after locator service
                var retryResult = await TapTemplateAsync(adbController, account, logger, templatePath, searchArea, description, " after locator recovery", false, cancellationToken);
                if (retryResult != TapResult.Tapped)
                {
                    logger.LogError($"[{account.AccountName}] Could not find {description} even after locator recovery");
                    return FailDetection($"Step: Finding {description}", description, recoveryNeeded: true);
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Locator service failed: {ex.Message}");
                return FailDetection($"Step: Finding {description}", description, recoveryNeeded: true);
            }
            return null;
        }

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(
            AccountSettings account,
            LogService logger,
            CancellationToken cancellationToken,
            bool isReRun = false,
            IUserNotificationService? userNotifications = null)
        {
            try
            {
                logger.LogInfo($"[AutoHeal] Starting auto heal process for account {account.AccountName}");

                // Validate template directory exists
                if (!Directory.Exists(_templateFolder))
                {
                    return FailConfiguration("Step 1: Setup", $"Template directory not found: {_templateFolder}");
                }

                // Ensure we're in map view
                NotifyProgress(userNotifications, "Navigating to map", 10);
                var locator = new LocatorService(logger, account);
                if (!await locator.EnsureViewAsync(ViewType.MapView, account.InstanceNumber, cancellationToken))
                {
                    return FailNavigation("Step 2: Navigating to map view", "Could not reach map view");
                }

                var adbController = await AdbMigrationHelper.GetConnectionAsync(account.InstanceNumber, logger, cancellationToken);
                if (adbController == null)
                {
                    return FailConnection("Step 3: Connecting to emulator", "Failed to establish ADB connection");
                }

                // Define healing sequence with specific search areas
                var healSequence = new[]
                {
                    (Path.Combine(_templateFolder, "heal.png"), new Rectangle(480, 880, 238, 200), "heal icon"),
                    (Path.Combine(_templateFolder, "heal-button.png"), new Rectangle(414, 786, 208, 202), "heal button"),
                    (Path.Combine(_templateFolder, "help-button.png"), new Rectangle(460, 1000, 258, 200), "help button")
                };

                foreach (var (templatePath, searchArea, description) in healSequence)
                {
                    if (!File.Exists(templatePath))
                    {
                        logger.LogError($"[AutoHeal] Template not found: {templatePath}");
                        return TaskExecutionDetails.Failed($"Template not found: {Path.GetFileName(templatePath)}");
                    }

                    // Give each template up to 5 seconds to appear
                    var tapResult = await TapTemplateAsync(adbController, account, logger, templatePath, searchArea, description, "", description == "heal icon", cancellationToken);

                    if (tapResult == TapResult.GaveUpEarly)
                    {
                        logger.LogInfo("[AutoHeal] Heal icon not found, skipping healing process");
                        return TaskExecutionDetails.Succeeded("No healing needed");
                    }

                    if (tapResult == TapResult.NotFound && description != "heal icon")
                    {
                        var failure = await RecoverAndRetryTapAsync(adbController, account, logger, templatePath, searchArea, description, cancellationToken);
                        if (failure != null) return failure;
                    }
                }

                logger.LogInfo("[AutoHeal] Successfully completed healing sequence");
                return TaskExecutionDetails.Succeeded("Healing completed successfully");
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("[AutoHeal] Task was cancelled");
                return TaskExecutionDetails.Failed("Task was cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError($"[AutoHeal] Error during healing process: {ex.Message}");
                return TaskExecutionDetails.FailedWith(
                    FailureCategory.Unknown,
                    "Unexpected error",
                    ex.Message,
                    recoveryNeeded: true);
            }
        }
    }
} 