using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.LDPlayer;
using Bot.Core.Services;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.IO;

namespace Bot.Core.Tasks.Modules
{
    public class ResidentWelcomeTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.ResidentWelcome;
        public override string Name => "Resident Welcome";

        
        // Tooltip for the UI
        public static string ToolTip => "Welcomes in new residents";

        protected override async Task OnInitializeAsync(LogService logger, CancellationToken cancellationToken)
        {
            _instanceLogger = logger;
            await base.OnInitializeAsync(logger, cancellationToken);
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
                logger.LogInfo($"[{account.AccountName}] Starting Resident Welcome task");

                // Ensure we're in base view
                var locatorService = new LocatorService(logger, account, TaskType.ResidentWelcome);
                await locatorService.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
                logger.LogInfo($"[{account.AccountName}] Ensured base view for resident welcome");

                // Execute zoom out to ensure proper visibility of resident icons
                await ZoomOutAsync(account, logger, cancellationToken);

                // Look for resident icon for 3 seconds
                logger.LogInfo($"[{account.AccountName}] Looking for resident icon...");
                var residentFound = await PollAndClickAsync("resident.png", "resident icon", account, logger, cancellationToken);
                if (residentFound == null) return TaskExecutionDetails.Failed("Task was cancelled");

                if (!residentFound.Value)
                {
                    logger.LogInfo($"[{account.AccountName}] No resident icon found, moving to next module");
                    return new TaskExecutionDetails(true, message: "No residents to welcome");
                }

                // Look for welcome-in button for 3 seconds
                logger.LogInfo($"[{account.AccountName}] Looking for welcome-in button...");
                var welcomeInFound = await PollAndClickAsync("welcome-in.png", "welcome-in button", account, logger, cancellationToken);
                if (welcomeInFound == null) return TaskExecutionDetails.Failed("Task was cancelled");

                if (!welcomeInFound.Value)
                {
                    logger.LogWarning($"[{account.AccountName}] Welcome-in button not found after clicking resident icon");
                    return TaskExecutionDetails.Failed("Welcome-in button not found");
                }

                logger.LogInfo($"[{account.AccountName}] Resident Welcome task completed successfully");
                return new TaskExecutionDetails(true, message: "Residents welcomed successfully");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error in Resident Welcome task: {ex.Message}");
                return TaskExecutionDetails.Failed($"Task failed: {ex.Message}");
            }
        }




        private static async Task ZoomOutAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            logger.LogInfo($"[{account.AccountName}] Executing zoom out for better resident visibility");
            var ldConsolePath = LDPlayerHelper.GetLDPlayerConsolePath();
            var dnConsolePath = LDPlayerHelper.GetDNPlayerConsolePath();
            var instanceManager = new InstanceManager(ldConsolePath, dnConsolePath, logger);
            var zoomSuccess = await instanceManager.ZoomOutAsync(account.InstanceNumber, cancellationToken);
            if (zoomSuccess)
            {
                logger.LogInfo($"[{account.AccountName}] Zoom out executed successfully");
                // Wait a moment for the zoom animation to complete
                await Task.Delay(1000, cancellationToken);
            }
            else
            {
                logger.LogWarning($"[{account.AccountName}] Zoom out execution failed, continuing without zoom");
            }
        }

        /// <summary>Looks for the image for 3 seconds and clicks it. True when clicked, false when not found, null when the task was cancelled.</summary>
        private async Task<bool?> PollAndClickAsync(string imageName, string description, AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            DateTime startTime = DateTime.UtcNow;

            while ((DateTime.UtcNow - startTime).TotalSeconds < 3)
            {
                await WaitIfPausedAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested) return null;

                if (await FindAndClickImageAsync(imageName, account.InstanceNumber, logger, threshold: 0.6))
                {
                    logger.LogInfo($"[{account.AccountName}] Found and clicked {description}");
                    return true;
                }

                await Task.Delay(500, cancellationToken); // Check every 0.5 seconds
            }
            return false;
        }

        protected override string GetImageFolderName()
        {
            return "residentcollect";
        }
    }
}