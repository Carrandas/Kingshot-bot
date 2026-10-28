using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.Config;
using Bot.Core.LDPlayer;
using Bot.Core.Exceptions;
using Bot.Core.Services;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using Bot.Core.ImageDetection;
using System.Linq;

namespace Bot.Core.Tasks.Modules
{
    public class ClaimMailTask : BaseTaskWithCommonPatterns, IScheduledTask
    {
        public override TaskType TaskType => TaskType.ClaimMail;
        public override string Name => "Claim Mail";

        // Mail menu tab areas: Wars | Alliance | System | Reports | Starred
        private static readonly Rectangle WarsMailTab = new Rectangle(45, 100, 90, 35);
        private static readonly Rectangle AllianceMailTab = new Rectangle(180, 100, 90, 35);
        private static readonly Rectangle SystemMailTab = new Rectangle(315, 100, 90, 35);
        private static readonly Rectangle ReportsMailTab = new Rectangle(450, 100, 90, 35);
        private static readonly Rectangle CloseMailButton = new Rectangle(671, 33, 18, 19);
        // "Tap anywhere to exit" on the Rewards popup; the Mail title bar, nothing to hit there without a popup
        private static readonly Point RewardsDismissPoint = new Point(360, 40);

        // Search areas for mail and claim buttons
        private static readonly Rectangle MailIconSearchArea = new Rectangle(618, 1004, 78, 77);  // 618-696, 1004-1081
        private static readonly Rectangle ClaimButtonSearchArea = new Rectangle(387, 1196, 88, 78);  // 387-475, 1196-1274

        public DateTime? GetNextRunUtc(string accountKey, AccountSettings account) => DailyGate.NextRunUtc(TaskType, accountKey);
        public void ClearSchedule(string accountKey) => DailyGate.Clear(TaskType, accountKey);

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            try
            {
                // Once a day: the game resets at 00:00 UTC
                string accountKey = Schedule.AccountKey(AccountDetectionTask.GetCachedAccountId(account.InstanceNumber), account);
                if (DailyGate.DoneToday(TaskType, accountKey))
                    return TaskExecutionDetails.SkippedBecause("Already done today");

                logger.LogInfo($"[{account.AccountName}] 📬 Starting mail claim task. IsReRun = {isReRun}");

                var locator = new LocatorService(logger, account);
                await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                {
                    return TaskExecutionDetails.Failed("Mail claim task cancelled during view location.");
                }

                logger.LogInfo($"[{account.AccountName}] ✅ Bot confirmed in BaseView.");

                // Open mail menu
                if (!await OpenMailMenu(account, logger, cancellationToken))
                {
                    return TaskExecutionDetails.Failed("Failed to open mail menu.");
                }

                // Process each mail tab
                await ProcessMailTab(account, logger, cancellationToken, WarsMailTab, "Wars");
                await ProcessMailTab(account, logger, cancellationToken, AllianceMailTab, "Alliance");
                await ProcessMailTab(account, logger, cancellationToken, SystemMailTab, "System");
                await ProcessMailTab(account, logger, cancellationToken, ReportsMailTab, "Reports");

                // Close mail menu
                logger.LogInfo($"[{account.AccountName}] 🚪 Closing mail menu...");
                if (!await ClickRandomInRectAsync(account.InstanceNumber, logger, CloseMailButton))
                {
                    logger.LogWarning($"[{account.AccountName}] ⚠️ Failed to click close button, attempting fallback close.");
                    await ClickRandomInRectAsync(account.InstanceNumber, logger, new Rectangle(0, 0, 50, 50)); // Fallback to top-left corner
                }

                await Task.Delay(GameCoordinates.Delays.AfterClick, cancellationToken);
                
                DailyGate.MarkDone(TaskType, accountKey);
                
                logger.LogInfo($"[{account.AccountName}] ✅ Mail claim task completed.");
                return TaskExecutionDetails.Succeeded();
            }
            catch (BotLostException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error executing Mail Claim task: {ex.Message}");
                return TaskExecutionDetails.Failed($"Error executing Mail Claim task: {ex.Message}");
            }
        }


        private async Task<bool> OpenMailMenu(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            const int POLL_INTERVAL_MS = 200;
            const int MAX_WAIT_TIME_MS = 5000;
            var startTime = DateTime.UtcNow;
            bool mailIconClicked = false;

            logger.LogInfo($"[{account.AccountName}] 🔍 Looking for mail icon in specified area...");

            while ((DateTime.UtcNow - startTime).TotalMilliseconds < MAX_WAIT_TIME_MS && !cancellationToken.IsCancellationRequested)
            {
                if (await WaitForImageAsync("mail.png", account.InstanceNumber, logger, cancellationToken,
                    timeoutMs: POLL_INTERVAL_MS, threshold: 0.7, useEnhancedMatching: true, searchArea: MailIconSearchArea))
                {
                    if (await FindAndClickImageAsync("mail.png", account.InstanceNumber, logger, threshold: 0.7, useEnhancedMatching: true, searchArea: MailIconSearchArea))
                    {
                        logger.LogInfo($"[{account.AccountName}] ✅ Found and clicked mail icon after {(DateTime.UtcNow - startTime).TotalMilliseconds:F0}ms.");
                        mailIconClicked = true;
                        break;
                    }
                }
                await Task.Delay(POLL_INTERVAL_MS, cancellationToken);
            }

            if (!mailIconClicked)
            {
                logger.LogError($"[{account.AccountName}] Error: Failed to find mail-icon.png, calling locator module for recovery");
                var locator = new LocatorService(logger, account);
                try
                {
                    await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
                    logger.LogInfo($"[{account.AccountName}] Locator service completed view recovery, retrying mail icon detection");
                    
                    // Retry after locator service
                    var retryStartTime = DateTime.UtcNow;
                    bool retryMailIconClicked = false;
                    
                    while ((DateTime.UtcNow - retryStartTime).TotalMilliseconds < MAX_WAIT_TIME_MS && !cancellationToken.IsCancellationRequested)
                    {
                        if (await WaitForImageAsync("mail.png", account.InstanceNumber, logger, cancellationToken,
                            timeoutMs: POLL_INTERVAL_MS, threshold: 0.7, useEnhancedMatching: true, searchArea: MailIconSearchArea))
                        {
                            if (await FindAndClickImageAsync("mail.png", account.InstanceNumber, logger, threshold: 0.7, useEnhancedMatching: true, searchArea: MailIconSearchArea))
                            {
                                logger.LogInfo($"[{account.AccountName}] ✅ Found and clicked mail icon after locator recovery in {(DateTime.UtcNow - retryStartTime).TotalMilliseconds:F0}ms.");
                                retryMailIconClicked = true;
                                break;
                            }
                        }
                        await Task.Delay(POLL_INTERVAL_MS, cancellationToken);
                    }
                    
                    if (!retryMailIconClicked)
                    {
                        logger.LogError($"[{account.AccountName}] Failed to find or click mail icon even after locator recovery");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError($"[{account.AccountName}] Locator service failed: {ex.Message}");
                    return false;
                }
            }

            await Task.Delay(1000, cancellationToken); // Wait for mail menu to open
            return true;
        }

        private async Task<bool> ProcessMailTab(AccountSettings account, LogService logger, CancellationToken cancellationToken, Rectangle tabArea, string tabName)
        {
            try
            {
                logger.LogInfo($"[{account.AccountName}] 📨 Processing {tabName} mail tab...");

                // Click the mail tab
                if (!await ClickRandomInRectAsync(account.InstanceNumber, logger, tabArea))
                {
                    logger.LogError($"[{account.AccountName}] ❌ Failed to click {tabName} mail tab.");
                    return false;
                }

                // A tab with many mails (Reports at 99+) takes a while to load; a claim tap before that is lost
                await Task.Delay(1500, cancellationToken);

                // Look for and click the "Read & Claim All" button
                const int POLL_INTERVAL_MS = 200;
                const int MAX_WAIT_TIME_MS = 5000;
                var startTime = DateTime.UtcNow;
                bool claimFound = false;

                while ((DateTime.UtcNow - startTime).TotalMilliseconds < MAX_WAIT_TIME_MS && !cancellationToken.IsCancellationRequested)
                {
                    if (await WaitForImageAsync("claim.png", account.InstanceNumber, logger, cancellationToken,
                        timeoutMs: POLL_INTERVAL_MS, threshold: 0.7, useEnhancedMatching: true, searchArea: ClaimButtonSearchArea))
                    {
                        if (await FindAndClickImageAsync("claim.png", account.InstanceNumber, logger, threshold: 0.7, useEnhancedMatching: true, searchArea: ClaimButtonSearchArea))
                        {
                            logger.LogInfo($"[{account.AccountName}] ✅ Found and clicked claim button in {tabName} tab after {(DateTime.UtcNow - startTime).TotalMilliseconds:F0}ms.");
                            claimFound = true;

                            // Close the Rewards popup (if any) so the next tab tap is not swallowed by it
                            await Task.Delay(2500, cancellationToken);
                            await ClickAsync(account.InstanceNumber, logger, RewardsDismissPoint);
                            break;
                        }
                    }
                    await Task.Delay(POLL_INTERVAL_MS, cancellationToken);
                }

                if (!claimFound)
                {
                    logger.LogInfo($"[{account.AccountName}] ℹ️ No claim button found in {tabName} tab after {MAX_WAIT_TIME_MS}ms. This may be normal if no mail to claim.");
                }

                await Task.Delay(500, cancellationToken); // Wait for claim animation
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error processing {tabName} mail tab: {ex.Message}");
                return false;
            }
        }

        protected override Task OnInitializeAsync(LogService logger, CancellationToken cancellationToken)
        {
            var readmePath = Path.Combine(ImageTemplateFolder, "README.txt");
            if (!File.Exists(readmePath))
            {
                File.WriteAllText(readmePath,
                    "Mail Claim Image Templates:\n\n" +
                    "Required images:\n\n" +
                    "1. mail.png - The mail icon in the game UI (search area: 618,1004 to 696,1081)\n" +
                    "2. claim.png - The claim button in mail interface (search area: 392,1203 to 483,1272)\n\n" +
                    "Instructions:\n" +
                    "- Take clear screenshots of each element\n" +
                    "- Crop to just the unique part you want to detect\n" +
                    "- Save with exact names above\n" +
                    "- Images should be clear and distinctive\n\n" +
                    "Enhanced Features:\n" +
                    "- Polling-based image detection\n" +
                    "- Restricted search areas for better accuracy\n" +
                    "- Caches claim button location for optimization\n" +
                    "- Robust error handling\n" +
                    "- Detailed logging");
            }
            return Task.CompletedTask;
        }
    }
} 