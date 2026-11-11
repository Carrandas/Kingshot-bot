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
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Bot.Core.ImageDetection;
using OpenCvSharp;
using Rect = OpenCvSharp.Rect;
using Tesseract;
using System.Text.Json;

namespace Bot.Core.Tasks.Modules
{
    public class TroopTrainingTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.TroopTraining;
        public override string Name => "Troop Training";

        // Side panel: the arrow at the left edge opens it, the one at its right edge closes it
        private const string PanelOpenArrowTemplate = "panel-open-arrow.png";
        private const string PanelCloseArrowTemplate = "panel-close-arrow.png";
        private static readonly Rectangle PanelOpenArrowArea = new Rectangle(0, 420, 60, 260);
        private static readonly Rectangle PanelCloseArrowArea = new Rectangle(400, 420, 120, 260);
        private const double PanelArrowThreshold = 0.88;
        private static readonly System.Drawing.Point TownTabPoint = new(80, 268);

        // "Troops Training" header on the Town tab: 0.998-1.000 with the tab open, <= 0.41 elsewhere.
        // At y=497 with two building queues above it; the rows below are placed relative to it.
        private const string TroopsHeaderTemplate = "troops-training-header.png";
        private static readonly Rectangle TroopsHeaderArea = new Rectangle(0, 240, 460, 660);
        private const double TroopsHeaderThreshold = 0.9;
        private const int TroopsHeaderY = 497;

        // Troop rows (Infantry, Cavalry, Archer) with the header at TroopsHeaderY: name line above, "Idle" or the timer below
        private static readonly string[] TroopTypes = { "Infantry", "Cavalry", "Archers" };
        private static readonly int[] TroopRowCenterY = { 565, 638, 711 };
        private const int TroopRowArrowX = 405;   // the arrow button at the end of the row opens the building

        // Building popup after the row's arrow: "Train" label under the hexagon button; 1.000 present, <= 0.41 elsewhere
        private const string TrainButtonTemplate = "train.png";
        private static readonly Rectangle TrainButtonArea = new Rectangle(150, 600, 450, 350);
        private const double TrainButtonThreshold = 0.8;

        // A "Completed" row's check button doesn't open the popup: the camera moves to the building and a hand points
        // at the bubble of trained troops above it (the same spot for every building). Tapping the bubble collects
        // them and the row turns "Idle". Hand: 0.72-1.00 present (it bobs), <= 0.42 elsewhere in the area (Train popup's hand included).
        private const string ReadyHandTemplate = "ready-hand.png";
        private static readonly Rectangle ReadyHandArea = new Rectangle(280, 230, 260, 250);
        private const double ReadyHandThreshold = 0.6;
        private static readonly System.Drawing.Point ReadyBubblePoint = new(357, 428);

        internal sealed class TroopStatusInfo
        {
            public string TroopType { get; set; } = string.Empty;
            /// <summary>Screen y of the row's centre in the side panel</summary>
            public int RowCenterY { get; set; }
            public bool IsReady { get; set; }
            /// <summary>"Completed": the trained troops wait in the building and must be collected before training again</summary>
            public bool AwaitsCollection { get; set; }
            public TimeSpan RemainingTime { get; set; } = TimeSpan.Zero;
            public DateTime FinishTimeUtc { get; set; } = DateTime.MinValue;
            public string RawOcrText { get; set; } = string.Empty;
        }

        private static TroopTrainingSettings GetTroopTrainingSettings(AccountSettings account)
        {
            // Use the new TroopTrainingSettings property directly
            return account.TroopTrainingSettings ?? new TroopTrainingSettings();
        }


        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            try
            {
                logger.LogInfo($"[{account.AccountName}] 🎯 Starting troop training check. IsReRun = {isReRun}");

                var locator = new LocatorService(logger, account);
                await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                {
                    return TaskExecutionDetails.Failed("Troop training task cancelled during view location.");
                }

                logger.LogInfo($"[{account.AccountName}] ✅ Bot confirmed in BaseView.");

                // Track which troops have been successfully trained in this session
                var session = new TrainingSession();
                logger.LogInfo($"[{account.AccountName}] 📝 Initialized progress tracking for this session");

                for (int loopAttempt = 0; loopAttempt < 5; loopAttempt++)
                {
                    var troopStatuses = await GetCurrentTroopStatuses(account, logger, cancellationToken);
                    if (troopStatuses == null)
                    {
                        return TaskExecutionDetails.Failed("Failed to get troop statuses.");
                    }

                    if (!await HandleTrainingRoundAsync(account, logger, cancellationToken, troopStatuses, session))
                    {
                        break;
                    }
                }

                var overallLatestFinishTimeUtc = session.LatestFinishTimeUtc;
                var processedTroopsThisSession = session.Processed;
                var anyTroopsProcessedSuccessfully = session.AnyProcessedSuccessfully;

                await PerformPostTaskActions(account, logger, cancellationToken);
                
                // Log session summary
                if (processedTroopsThisSession.Any())
                {
                    logger.LogInfo($"[{account.AccountName}] 📊 Session Summary: Successfully processed {processedTroopsThisSession.Count} troop types: {string.Join(", ", processedTroopsThisSession)}");
                }
                else
                {
                    logger.LogInfo($"[{account.AccountName}] 📊 Session Summary: No troops were processed in this session");
                }
                
                string message = anyTroopsProcessedSuccessfully ? $"Processed {processedTroopsThisSession.Count} troop types." : "No troops processed or ready.";
                return new TaskExecutionDetails(true, overallLatestFinishTimeUtc, message);
            }
            catch (BotLostException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error executing Troop Training task: {ex.Message}");
                return TaskExecutionDetails.Failed($"Error executing Troop Training task: {ex.Message}");
            }
        }

        private sealed class TrainingSession
        {
            public HashSet<string> Processed { get; } = new();
            public bool AnyProcessedSuccessfully;
            public DateTime? LatestFinishTimeUtc;
        }

        /// <summary>
        /// One round of the training loop. True: look at the troops again; false: nothing more to do. Troops still
        /// training are left for a later cycle (training takes many hours).
        /// </summary>
        private async Task<bool> HandleTrainingRoundAsync(
            AccountSettings account, LogService logger, CancellationToken cancellationToken,
            List<TroopStatusInfo> troopStatuses, TrainingSession session)
        {
            var readyToCollectAndRetrain = troopStatuses.Where(s => s.IsReady).ToList();
            var stillTraining = troopStatuses.Where(s => !s.IsReady && s.RemainingTime > TimeSpan.Zero).ToList();

            // Filter out troops that have already been processed this session
            var unprocessedReadyTroops = readyToCollectAndRetrain.Where(t => !session.Processed.Contains(t.TroopType)).ToList();

            if (readyToCollectAndRetrain.Any() && !unprocessedReadyTroops.Any())
            {
                logger.LogInfo($"[{account.AccountName}] ✅ Found {readyToCollectAndRetrain.Count} ready troops, but all have been processed this session: {string.Join(", ", readyToCollectAndRetrain.Select(t => t.TroopType))}");
                logger.LogInfo($"[{account.AccountName}] 📝 Already processed: {string.Join(", ", session.Processed)}");
            }

            if (unprocessedReadyTroops.Any())
            {
                await ProcessUnprocessedReadyTroopsAsync(account, logger, cancellationToken, unprocessedReadyTroops, session);
                return true;
            }

            foreach (var troop in stillTraining)
            {
                logger.LogInfo($"[{account.AccountName}] ⏳ {troop.TroopType} still training: {troop.RemainingTime} remaining (finishes {troop.FinishTimeUtc:u})");
            }

            session.LatestFinishTimeUtc = stillTraining.Any() ? stillTraining.Max(t => t.FinishTimeUtc) : null;
            return false;
        }

        private async Task ProcessUnprocessedReadyTroopsAsync(
            AccountSettings account, LogService logger, CancellationToken cancellationToken,
            List<TroopStatusInfo> unprocessedReadyTroops, TrainingSession session)
        {
            logger.LogInfo($"[{account.AccountName}] ✅ Found {unprocessedReadyTroops.Count} unprocessed troop types ready: {string.Join(", ", unprocessedReadyTroops.Select(t => t.TroopType))}");
            if (session.Processed.Any())
            {
                logger.LogInfo($"[{account.AccountName}] 📝 Already processed this session: {string.Join(", ", session.Processed)}");
            }

            // Validate all ready troops before processing
            var validReadyTroops = new List<TroopStatusInfo>();
            var validTroopTypes = new[] { "Infantry", "Cavalry", "Archers" };

            foreach (var troop in unprocessedReadyTroops)
            {
                if (!troop.IsReady)
                {
                    logger.LogError($"[{account.AccountName}] ❌ CRITICAL: Attempting to process {troop.TroopType} but it's not ready! Skipping.");
                    continue;
                }

                if (!validTroopTypes.Contains(troop.TroopType))
                {
                    logger.LogError($"[{account.AccountName}] ❌ CRITICAL: Invalid troop type '{troop.TroopType}'. Skipping.");
                    continue;
                }

                validReadyTroops.Add(troop);
            }

            if (!validReadyTroops.Any())
            {
                return;
            }

            logger.LogInfo($"[{account.AccountName}] 🎯 PROCESSING ALL READY TROOPS IN ONE OPERATION: {string.Join(", ", validReadyTroops.Select(t => t.TroopType))}");

            // Process all ready troops in a single operation (pick the first one to enter the training interface)
            bool success = await ProcessAllReadyTroops(account, logger, cancellationToken, validReadyTroops);

            if (!success)
            {
                logger.LogError($"[{account.AccountName}] ❌ Failed to process ready troops. None will be marked as processed.");
                return;
            }

            session.AnyProcessedSuccessfully = true;
            // Mark ALL valid ready troops as processed since we processed them all in one go
            foreach (var troopType in validReadyTroops.Select(troop => troop.TroopType))
            {
                session.Processed.Add(troopType);
                logger.LogInfo($"[{account.AccountName}] ✅ {troopType} retrained successfully and marked as processed.");
            }
            logger.LogInfo($"[{account.AccountName}] 📝 Session progress: {string.Join(", ", session.Processed)} completed");
        }

        private async Task<List<TroopStatusInfo>?> GetCurrentTroopStatuses(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            const int maxAttempts = 3;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var statuses = await TryReadTroopStatusesAsync(account, logger, cancellationToken, attempt, maxAttempts);
                if (statuses != null)
                {
                    return statuses;
                }
                if (attempt < maxAttempts) await Task.Delay(GameCoordinates.Delays.BetweenRetries, cancellationToken);
            }
            logger.LogError($"[{account.AccountName}] ❌ Failed to get troop statuses after {maxAttempts} attempts.");
            return null;
        }

        /// <summary>One try: opens the side panel on the Town tab and reads the three troop statuses. Null when that failed.</summary>
        private async Task<List<TroopStatusInfo>?> TryReadTroopStatusesAsync(
            AccountSettings account, LogService logger, CancellationToken cancellationToken, int attempt, int maxAttempts)
        {
            try
            {
                logger.LogInfo($"[{account.AccountName}] 🖱️ Opening side panel for status check (Attempt {attempt}/{maxAttempts})");
                if (!await OpenTownTabAsync(account, logger, cancellationToken))
                {
                    return null;
                }

                var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
                int? headerY = screenshot == null ? null : FindTroopsHeaderY(screenshot, account.InstanceNumber, logger);
                if (screenshot == null || headerY == null)
                {
                    logger.LogWarning($"[{account.AccountName}] ❌ 'Troops Training' section not found on the side panel");
                    return null;
                }

                var statuses = ReadAllTroopStatuses(logger, screenshot, headerY.Value);
                logger.LogInfo($"[{account.AccountName}] 📊 Troop status check complete (Attempt {attempt}).");
                return statuses;
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] ❌ Error in GetCurrentTroopStatuses attempt {attempt}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Opens the side panel unless it is open, then selects the Town tab (the panel reopens on the tab it was left
        /// on; daily tasks leave it on Daily). Checks first, so a retry doesn't close an open panel and tap the city.
        /// </summary>
        private async Task<bool> OpenTownTabAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            int instance = account.InstanceNumber;
            if (!(await MatchAsync(PanelCloseArrowTemplate, PanelCloseArrowArea, PanelArrowThreshold, instance, logger)).found)
            {
                var arrow = await MatchAsync(PanelOpenArrowTemplate, PanelOpenArrowArea, PanelArrowThreshold, instance, logger);
                if (!arrow.found)
                {
                    logger.LogWarning($"[{account.AccountName}] Side panel arrow not found, going back to the base view");
                    await new LocatorService(logger, account).EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);
                    return false;
                }
                await ClickCenterInRectAsync(instance, logger, arrow.rect);
                await Task.Delay(1500, cancellationToken);
            }
            await ClickAsync(instance, logger, TownTabPoint);
            await Task.Delay(1200, cancellationToken);
            return true;
        }

        /// <summary>Screen y of the "Troops Training" header on the side panel's Town tab, or null when it isn't shown.</summary>
        internal int? FindTroopsHeaderY(byte[] screenshot, int instance, LogService logger)
        {
            _templateMatcher ??= new UnifiedTemplateMatchingService(logger);
            var (found, rect, confidence) = _templateMatcher.MatchTemplate(
                screenshot, Path.Combine(ImageTemplateFolder, TroopsHeaderTemplate), instance,
                threshold: TroopsHeaderThreshold, scales: new[] { 1.0 }, verboseLogging: false, searchArea: TroopsHeaderArea);
            logger.LogInfo($"'Troops Training' header: found={found}, confidence={confidence:F3}, y={rect.Y}");
            return found ? rect.Y : null;
        }

        /// <summary>The hand pointing at a building's bubble of trained troops (after a "Completed" row's button)</summary>
        internal bool ShowsTroopsToCollect(byte[] screenshot, int instance, LogService logger)
        {
            _templateMatcher ??= new UnifiedTemplateMatchingService(logger);
            var (found, _, confidence) = _templateMatcher.MatchTemplate(
                screenshot, Path.Combine(ImageTemplateFolder, ReadyHandTemplate), instance,
                threshold: ReadyHandThreshold, scales: new[] { 1.0 }, verboseLogging: false, searchArea: ReadyHandArea);
            logger.LogInfo($"Troops-to-collect hand: found={found}, confidence={confidence:F3}");
            return found;
        }

        /// <summary>Reads "Idle" or the remaining time of each troop row; headerY is where the "Troops Training" header was found.</summary>
        internal List<TroopStatusInfo> ReadAllTroopStatuses(LogService logger, byte[] screenshot, int headerY)
        {
            // Simplified timer config
            var timerOcrConfig = new OcrConfiguration {
                CharacterWhitelist = "0123456789:",
                ScaleFactor = 4,
                PageSegMode = PageSegMode.SingleLine
            };
            using var timerOcrService = new OcrService(logger, timerOcrConfig);

            // Simplified word config - removed unused properties
            var wordOcrConfig = new OcrConfiguration {
                CharacterWhitelist = "ceidlmopt",
                ScaleFactor = 4,
                PageSegMode = PageSegMode.SingleLine
            };
            using var wordOcrService = new OcrService(logger, wordOcrConfig);

            var statuses = new List<TroopStatusInfo>();
            for (int i = 0; i < TroopTypes.Length; i++)
            {
                string troopType = TroopTypes[i];
                int rowY = TroopRowCenterY[i] + headerY - TroopsHeaderY;
                var statusLine = new Rectangle(150, rowY + 2, 150, 26);   // below the troop name

                // The timer is white on the green progress bar: read straight off the screen the colons came out as 7s
                byte[] timerImage = WhiteText(screenshot, statusLine);
                string timerText = timerOcrService.ExtractTextFromScreenArea(timerImage, new Rectangle(0, 0, statusLine.Width, statusLine.Height));
                var status = ParseTroopStatus(timerText, troopType, logger, isTimerCheck: true);

                if (!status.IsReady && status.RemainingTime == TimeSpan.Zero)
                {
                    logger.LogInfo($"[{troopType}] Not a timer, checking for words...");
                    string wordText = wordOcrService.ExtractTextFromScreenArea(screenshot, statusLine);
                    status = ParseTroopStatus(wordText, troopType, logger, isTimerCheck: false);
                }

                status.RowCenterY = rowY;
                statuses.Add(status);
            }
            return statuses;
        }

        /// <summary>The white pixels of an area (the timer digits) in white on black, as PNG bytes for the OCR.</summary>
        private static byte[] WhiteText(byte[] screenshot, Rectangle area)
        {
            using var image = Cv2.ImDecode(screenshot, ImreadModes.Color);
            using var region = new Mat(image, new Rect(area.X, area.Y, area.Width, area.Height));
            using var hsv = region.CvtColor(ColorConversionCodes.BGR2HSV);
            using var mask = hsv.InRange(new Scalar(0, 0, 190), new Scalar(180, 60, 255));
            using var bgr = mask.CvtColor(ColorConversionCodes.GRAY2BGR);
            return bgr.ToBytes(".png");
        }

        private TroopStatusInfo ParseTroopStatus(string ocrText, string troopType, LogService logger, bool isTimerCheck)
        {
            var status = new TroopStatusInfo
            {
                TroopType = troopType,
                RawOcrText = ocrText.Replace("\n", " ").Trim()
            };
            logger.LogInfo($"[{troopType}] Raw OCR text: '{status.RawOcrText}' (Timer Check: {isTimerCheck})");

            try
            {
                var timeMatch = Regex.Match(status.RawOcrText, @"(\d{1,2})\s?:\s?(\d{2})\s?:\s?(\d{2})", RegexOptions.None, Bot.Core.Utils.RegexTimeouts.Default);
                if (timeMatch.Success &&
                    int.TryParse(timeMatch.Groups[1].Value, out int h) &&
                    int.TryParse(timeMatch.Groups[2].Value, out int m) &&
                    int.TryParse(timeMatch.Groups[3].Value, out int s))
                {
                    status.RemainingTime = new TimeSpan(h, m, s);
                    status.FinishTimeUtc = DateTime.UtcNow.Add(status.RemainingTime);
                    status.IsReady = (status.RemainingTime < TimeSpan.FromSeconds(2));
                    if(status.IsReady) logger.LogInfo($"[{troopType}] Status: Timer is basically zero. Marking as Ready.");
                    else logger.LogInfo($"[{troopType}] Status: Parsed time {status.RemainingTime:hh\\:mm\\:ss}.");
                    return status;
                }

                if (isTimerCheck) return status;

                string[] readyKeywords = { "complete", "completed", "idle", "ready" };
                var words = status.RawOcrText.Split(new[] { ' ', ':', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                
                foreach (var word in words)
                {
                    var keyword = readyKeywords.FirstOrDefault(k => IsSimilar(word, k, 2));
                    if (keyword != null)
                    {
                        status.IsReady = true;
                        status.AwaitsCollection = keyword.StartsWith("complete", StringComparison.Ordinal);
                        status.RemainingTime = TimeSpan.Zero;
                        status.FinishTimeUtc = DateTime.UtcNow;
                        logger.LogInfo($"[{troopType}] Status: Ready (found word '{word}' similar to '{keyword}')");
                        return status;
                    }
                }

                logger.LogWarning($"[{troopType}] Could not determine a valid status from text: '{status.RawOcrText}'");
                status.IsReady = false;
                return status;
            }
            catch (Exception ex)
            {
                logger.LogError($"[{troopType}] Error parsing troop status: {ex.Message}");
                status.IsReady = false;
                return status;
            }
        }

        private static bool IsSimilar(string s1, string s2, int maxDistance)
        {
            string s1Lower = s1.ToLowerInvariant();
            string s2Lower = s2.ToLowerInvariant();

            int n = s1Lower.Length;
            int m = s2Lower.Length;
            int[,] d = new int[n + 1, m + 1];

            if (Math.Abs(n - m) > maxDistance) return false;
            if (n == 0) return m <= maxDistance;
            if (m == 0) return n <= maxDistance;

            for (int i = 0; i <= n; d[i, 0] = i++);
            for (int j = 0; j <= m; d[0, j] = j++);

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    int cost = (s2Lower[j - 1] == s1Lower[i - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }
            return d[n, m] <= maxDistance;
        }
        

        private enum PrimaryTrainingResult { Retry, Abort, Done }

        private async Task<bool> ProcessAllReadyTroops(AccountSettings account, LogService logger, CancellationToken cancellationToken, List<TroopStatusInfo> readyTroops)
        {
            if (!readyTroops.Any())
            {
                logger.LogWarning($"[{account.AccountName}] No ready troops provided to process");
                return false;
            }

            await CollectFinishedTroopsAsync(account, logger, cancellationToken, readyTroops);

            const int maxRetries = 2;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    logger.LogInfo($"[{account.AccountName}] 🔄 Processing ALL ready troops in one operation (Attempt {attempt}/{maxRetries})");
                    logger.LogInfo($"[{account.AccountName}] 🎯 Ready troops to process: {string.Join(", ", readyTroops.Select(t => t.TroopType))}");

                    // Use the first ready troop to enter the training interface
                    var primaryTroop = readyTroops[0];
                    var primaryResult = await TrainPrimaryTroopAsync(account, logger, cancellationToken, primaryTroop, attempt < maxRetries);
                    if (primaryResult == PrimaryTrainingResult.Retry) continue;
                    if (primaryResult == PrimaryTrainingResult.Abort) return false;

                    await TrainRemainingAndReturnAsync(account, logger, cancellationToken, readyTroops, primaryTroop);
                    return true;
                }
                catch (Exception ex)
                {
                    logger.LogError($"[{account.AccountName}] ❌ Error processing ready troops: {ex.Message}");
                    if (attempt < maxRetries) await Task.Delay(GameCoordinates.Delays.BetweenErrorRetries, cancellationToken);
                }
            }

            logger.LogError($"[{account.AccountName}] ❌ Failed to process ready troops after {maxRetries} attempts");
            return false;
        }

        /// <summary>
        /// Collects the trained troops of every "Completed" row (row button > the bubble the hand points at), then
        /// reopens the Town tab for the training step: the rows are "Idle" then and their arrows open the Train popup.
        /// </summary>
        private async Task CollectFinishedTroopsAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, List<TroopStatusInfo> troops)
        {
            var finished = troops.Where(t => t.AwaitsCollection).ToList();
            if (finished.Count == 0) return;

            int instance = account.InstanceNumber;
            foreach (var troop in finished)
            {
                if (!await OpenTownTabAsync(account, logger, cancellationToken)) return;
                await ClickAsync(instance, logger, new System.Drawing.Point(TroopRowArrowX, troop.RowCenterY));
                await Task.Delay(GameCoordinates.Delays.AfterMenuClick, cancellationToken);
                await Task.Delay(1000, cancellationToken);

                var screenshot = await TakeScreenshotAsync(instance, logger);
                if (screenshot == null || !ShowsTroopsToCollect(screenshot, instance, logger))
                {
                    logger.LogWarning($"[{account.AccountName}] No troops bubble to collect at the {troop.TroopType} building");
                    continue;
                }
                await ClickAsync(instance, logger, ReadyBubblePoint);
                await Task.Delay(2000, cancellationToken);
                logger.LogInfo($"[{account.AccountName}] 📥 Collected the trained {troop.TroopType}");
            }
            await OpenTownTabAsync(account, logger, cancellationToken);
        }

        /// <summary>Step 5 and the way back: trains the other ready troops, then returns to the base view.</summary>
        private async Task TrainRemainingAndReturnAsync(
            AccountSettings account, LogService logger, CancellationToken cancellationToken, List<TroopStatusInfo> readyTroops, TroopStatusInfo primaryTroop)
        {
            // Step 5: Process remaining ready troops (exclude the primary troop since it's already being trained)
            var remainingTroops = readyTroops.Where(t => !string.Equals(t.TroopType, primaryTroop.TroopType, StringComparison.OrdinalIgnoreCase)).ToList();

            if (remainingTroops.Any())
            {
                logger.LogInfo($"[{account.AccountName}] 🔄 Now processing remaining {remainingTroops.Count} ready troops (excluding {primaryTroop.TroopType} which is already being trained)...");

                foreach (var readyTroopType in remainingTroops.Select(readyTroop => readyTroop.TroopType))
                {
                    await TrainRemainingTroopAsync(account, logger, cancellationToken, readyTroopType);
                }
            }
            else
            {
                logger.LogInfo($"[{account.AccountName}] ℹ️ Only {primaryTroop.TroopType} was ready, and it's already being trained by entering the training interface");
            }

            // After processing all ready troops, we're done
            logger.LogInfo($"[{account.AccountName}] ✅ Completed processing all {readyTroops.Count} ready troops, finishing up");

            // Click back button to return to base view
            var backButtonArea = new Rectangle(24, 25, 27, 21);
            logger.LogInfo($"[{account.AccountName}] 🖱️ Clicking back button to return to base view...");
            await ClickRandomInRectAsyncWithRetry(account.InstanceNumber, logger, backButtonArea, cancellationToken, 2);
            await Task.Delay(GameCoordinates.Delays.AfterMenuClick, cancellationToken);

            logger.LogInfo($"[{account.AccountName}] ✅ Successfully processed all ready troops: {string.Join(", ", readyTroops.Select(t => t.TroopType))}");
        }

        /// <summary>Steps 0-4: opens the primary troop's training interface from the side menu and starts its training.</summary>
        private async Task<PrimaryTrainingResult> TrainPrimaryTroopAsync(
            AccountSettings account, LogService logger, CancellationToken cancellationToken, TroopStatusInfo primaryTroop, bool canRetry)
        {
            var rowArrow = new System.Drawing.Point(TroopRowArrowX, primaryTroop.RowCenterY);
            logger.LogInfo($"[{account.AccountName}] 🖱️ Opening the {primaryTroop.TroopType} building via the side panel arrow at {rowArrow}");
            if (!await ClickAsync(account.InstanceNumber, logger, rowArrow))
            {
                if (!canRetry) return PrimaryTrainingResult.Abort;
                await Task.Delay(GameCoordinates.Delays.BetweenRetries, cancellationToken);
                return PrimaryTrainingResult.Retry;
            }
            await Task.Delay(GameCoordinates.Delays.AfterMenuClick, cancellationToken);
            await Task.Delay(1000, cancellationToken);

            // The camera moves to the building and shows its Details / Train popup
            logger.LogInfo($"[{account.AccountName}] 🔍 Looking for 'Train' button...");
            bool trainButtonFound = await FindAndClickImageWithTimeoutAsync(TrainButtonTemplate, account.InstanceNumber, logger,
                threshold: TrainButtonThreshold,
                timeoutSeconds: 3,
                useEnhancedMatching: false,
                searchArea: TrainButtonArea,
                cancellationToken: cancellationToken);

            if (!trainButtonFound)
            {
                logger.LogError($"[{account.AccountName}] ❌ 'Train' button not found on the {primaryTroop.TroopType} building");
                return PrimaryTrainingResult.Abort;
            }

            // Wait 0.5 seconds after clicking train button
            await Task.Delay(500, cancellationToken);

            // Step 3: Look for minus-button.png to verify we're in training interface
            logger.LogInfo($"[{account.AccountName}] 🔍 Looking for minus-button.png to verify training interface...");
            bool minusButtonFound = await FindAndClickImageWithTimeoutAsync("minus-button.png", account.InstanceNumber, logger,
                threshold: 0.6,
                timeoutSeconds: 2,
                useEnhancedMatching: false,
                searchArea: null,
                cancellationToken: cancellationToken,
                click: false);  // only confirms the training screen: a tap takes one troop off the amount

            if (!minusButtonFound)
            {
                return PrimaryTrainingResult.Done;
            }

            logger.LogInfo($"[{account.AccountName}] ✅ Found minus-button.png, we're in the correct training area");
            await Task.Delay(500, cancellationToken);

            await SelectTierAsync(account, logger, cancellationToken, primaryTroop.TroopType);

            // Step 4: Click in the train box (409,1078 586,1136)
            var trainBoxArea = new Rectangle(409, 1078, 177, 58); // width = 586-409=177, height = 1136-1078=58
            if (!await ClickRandomInRectAsyncWithRetry(account.InstanceNumber, logger, trainBoxArea, cancellationToken, 2))
            {
                logger.LogError($"[{account.AccountName}] ❌ Failed to click train box area");
                return PrimaryTrainingResult.Abort; // Stop processing on error
            }
            await Task.Delay(GameCoordinates.Delays.AfterSettingAmount, cancellationToken);
            return PrimaryTrainingResult.Done;
        }

        private static Rectangle? GetTroopIconArea(string troopType)
        {
            switch (troopType.ToLower())
            {
                case "cavalry":
                    return new Rectangle(365, 1235, 30, 30);
                case "archers":
                    return new Rectangle(581, 1234, 30, 30);
                case "infantry":
                    return new Rectangle(134, 1229, 30, 30);
                default:
                    return null;
            }
        }

        /// <summary>Selects another ready troop type on the training screen and trains it.</summary>
        private async Task TrainRemainingTroopAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, string readyTroopType)
        {
            logger.LogInfo($"[{account.AccountName}] 🔍 Processing ready {readyTroopType}...");

            var iconArea = GetTroopIconArea(readyTroopType);
            if (iconArea == null)
            {
                logger.LogWarning($"[{account.AccountName}] ⚠️ Unknown troop type: {readyTroopType}, skipping");
                return;
            }

            if (!await ClickRandomInRectAsyncWithRetry(account.InstanceNumber, logger, iconArea.Value, cancellationToken, 1))
            {
                logger.LogWarning($"[{account.AccountName}] ⚠️ Failed to click {readyTroopType} icon");
                return;
            }

            logger.LogInfo($"[{account.AccountName}] ✅ Clicked ready {readyTroopType}, waiting 0.5 seconds...");
            await Task.Delay(500, cancellationToken);

            // For cavalry and archers, look for minus button before clicking train box
            if (readyTroopType.ToLower() == "cavalry" || readyTroopType.ToLower() == "archers")
            {
                logger.LogInfo($"[{account.AccountName}] 🔍 Looking for minus-button.png after {readyTroopType} click...");
                bool minusFound = await FindAndClickImageWithTimeoutAsync("minus-button.png", account.InstanceNumber, logger,
                    threshold: 0.6,
                    timeoutSeconds: 2,
                    useEnhancedMatching: false,
                    searchArea: null,
                    cancellationToken: cancellationToken,
                    click: false);

                if (minusFound)
                {
                    logger.LogInfo($"[{account.AccountName}] ✅ Found minus-button after {readyTroopType}, selecting the tier...");
                    await SelectTierAsync(account, logger, cancellationToken, readyTroopType);
                }
            }

            // Click train box area for this troop
            var trainBoxAreaFinal = new Rectangle(409, 1078, 177, 58);
            if (await ClickRandomInRectAsyncWithRetry(account.InstanceNumber, logger, trainBoxAreaFinal, cancellationToken, 1))
            {
                logger.LogInfo($"[{account.AccountName}] ✅ Successfully processed ready {readyTroopType}");
                await Task.Delay(500, cancellationToken);
            }
            else
            {
                logger.LogWarning($"[{account.AccountName}] ⚠️ Failed to click train box for {readyTroopType}");
            }
        }

        // Tier strip on the training screen: five tile slots 133 px apart. Opening the screen or switching
        // Barracks/Stable/Range selects the highest unlocked tier (X on llsa, with XI locked to its right).
        private static readonly int[] TierSlotX = { 77, 209, 342, 475, 608 };
        private const int TierTileY = 700;
        // Share of gold-glow pixels around a tile: 0.37-0.40 on the selected tile, <= 0.11 on the others
        private const double SelectedGlowThreshold = 0.25;

        /// <summary>
        /// Index in <see cref="TierSlotX"/> of the tier tile with the gold selection glow, or null when no tile
        /// clearly has it (not on the training screen).
        /// </summary>
        internal static int? FindSelectedTierSlot(byte[] screenshot)
        {
            using var image = Cv2.ImDecode(screenshot, ImreadModes.Color);
            using var hsv = image.CvtColor(ColorConversionCodes.BGR2HSV);
            using var glow = hsv.InRange(new Scalar(15, 60, 180), new Scalar(35, 255, 255));
            var scores = TierSlotX.Select(x => TierGlowScore(glow, x)).ToArray();
            int best = Array.IndexOf(scores, scores.Max());
            bool clear = scores[best] >= SelectedGlowThreshold
                && scores.Where((_, i) => i != best).All(s => s < SelectedGlowThreshold);
            return clear ? best : null;
        }

        /// <summary>Gold pixels in the band above the tile's hexagon and the strips beside it.</summary>
        private static double TierGlowScore(Mat glow, int x)
        {
            var bands = new[] { new Rect(x - 30, 626, 60, 14), new Rect(x - 66, 680, 8, 40), new Rect(x + 58, 680, 8, 40) };
            int total = 0, lit = 0;
            foreach (var band in bands)
            {
                using var part = new Mat(glow, band);
                lit += Cv2.CountNonZero(part);
                total += band.Width * band.Height;
            }
            return (double)lit / total;
        }

        /// <summary>
        /// With "Train second highest tier": selects the tile left of the selected (highest) one, e.g. IX next to X.
        /// Leaves the highest tier selected when the strip can't be read or nothing is left of it.
        /// </summary>
        private async Task SelectSecondHighestTierAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, string troopLabel)
        {
            var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
            int? selected = screenshot == null ? null : FindSelectedTierSlot(screenshot);
            if (selected == null || selected == 0)
            {
                logger.LogWarning($"[{account.AccountName}] ⚠️ {troopLabel}: no tier left of the selected one found (slot {selected?.ToString() ?? "none"}), training the selected tier");
                return;
            }

            int target = selected.Value - 1;
            logger.LogInfo($"[{account.AccountName}] 🎯 {troopLabel}: selecting the second highest tier (slot {target}, left of slot {selected})");
            await ClickAsync(account.InstanceNumber, logger, new System.Drawing.Point(TierSlotX[target], TierTileY));
            int? now = await WaitForSelectedTierAsync(account.InstanceNumber, logger, target, cancellationToken);
            if (now == target)
                logger.LogInfo($"[{account.AccountName}] ✅ {troopLabel}: second highest tier selected");
            else
                logger.LogWarning($"[{account.AccountName}] ⚠️ {troopLabel}: selected slot is {now?.ToString() ?? "unknown"} after the tap, expected {target}");
        }

        /// <summary>
        /// The selected tier slot after a tap. The glow can be unclear right after it (seen once on Cavalry, which
        /// still trained IX), so this looks up to three times until it shows the target.
        /// </summary>
        private async Task<int?> WaitForSelectedTierAsync(int instance, LogService logger, int target, CancellationToken cancellationToken)
        {
            int? now = null;
            for (int look = 0; look < 3 && now != target; look++)
            {
                await Task.Delay(look == 0 ? 1000 : 700, cancellationToken);
                var check = await TakeScreenshotAsync(instance, logger);
                now = check == null ? null : FindSelectedTierSlot(check);
            }
            return now;
        }

        /// <summary>Picks the tier to train: the second highest with that setting, else the game's default (highest).</summary>
        private async Task SelectTierAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, string troopType)
        {
            if (GetTroopTrainingSettings(account).TrainSecondHighestTier)
            {
                await SelectSecondHighestTierAsync(account, logger, cancellationToken, troopType);
            }
        }

        private async Task PerformPostTaskActions(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            try
            {
                logger.LogInfo($"[{account.AccountName}] 📸 Performing post-task actions...");
                
                var locator = new LocatorService(logger, account);
                await locator.EnsureViewAsync(ViewType.BaseView, account.InstanceNumber, cancellationToken);
                
                var close = await MatchAsync(PanelCloseArrowTemplate, PanelCloseArrowArea, PanelArrowThreshold, account.InstanceNumber, logger);
                if (close.found)
                {
                    logger.LogInfo($"[{account.AccountName}] Side panel still open, closing it.");
                    await ClickCenterInRectAsync(account.InstanceNumber, logger, close.rect);
                    await Task.Delay(GameCoordinates.Delays.AfterClick, cancellationToken);
                }

                logger.LogInfo($"[{account.AccountName}] ✅ Post-task actions completed");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] ❌ Error in post-task actions: {ex.Message}");
            }
        }

        private async Task<bool> ClickRandomInRectAsyncWithRetry(int instanceNumber, LogService logger, Rectangle rect, CancellationToken cancellationToken, int maxRetries = 3)
        {
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    if (await ClickRandomInRectAsync(instanceNumber, logger, rect))
                    {
                        return true;
                    }
                    if (attempt < maxRetries)
                    {
                        await Task.Delay(GameCoordinates.Delays.BetweenRetries, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError($"Click attempt {attempt} in {rect} exception: {ex.Message}");
                    if (ex.Message.Contains("device") && ex.Message.Contains("not found"))
                    {
                        logger.LogInfo("Detected ADB connection loss, attempting to recover...");
                        // V2 system handles connection recovery automatically
                        await Task.Delay(2000, cancellationToken);
                        await AdbMigrationHelper.GetConnectionAsync(instanceNumber, logger, cancellationToken);
                    }
                    if (attempt < maxRetries) await Task.Delay(GameCoordinates.Delays.BetweenErrorRetries, cancellationToken);
                }
            }
            logger.LogError($"Failed to click in {rect} after {maxRetries} attempts");
            return false;
        }

        protected override Task OnInitializeAsync(LogService logger, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }


        private async Task<bool> FindAndClickImageWithTimeoutAsync(string imageName, int instanceNumber, LogService logger, 
            double threshold = 0.6, int timeoutSeconds = 2, bool useEnhancedMatching = false, 
            Rectangle? searchArea = null, CancellationToken cancellationToken = default, bool click = true)
        {
            const int pollIntervalMs = 200; // Check every 200ms
            var startTime = DateTime.UtcNow;
            var timeoutSpan = TimeSpan.FromSeconds(timeoutSeconds);
            
            logger.LogInfo($"Looking for '{imageName}' with {timeoutSeconds}s timeout...");
            
            while (DateTime.UtcNow - startTime < timeoutSpan && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var screenshot = await TakeScreenshotAsync(instanceNumber, logger);
                    if (screenshot == null)
                    {
                        await Task.Delay(pollIntervalMs, cancellationToken);
                        continue;
                    }

                    if (_templateMatcher == null) _templateMatcher = new UnifiedTemplateMatchingService(logger);
                    var templatePath = Path.Combine(ImageTemplateFolder, imageName);

                    var (found, matchRect, confidence) = _templateMatcher.MatchTemplate(
                        screenshot,
                        templatePath,
                        instanceNumber,
                        threshold,
                        scales: useEnhancedMatching ? UnifiedTemplateMatchingService.StandardScales : new[] { 1.0 },
                        searchArea: searchArea
                    );

                    if (found)
                    {
                        var elapsed = DateTime.UtcNow - startTime;
                        logger.LogInfo($"Found '{imageName}' at {matchRect} with confidence {confidence:F3} after {elapsed.TotalSeconds:F1}s");
                        return !click || await ClickRandomInRectAsync(instanceNumber, logger, matchRect);
                    }

                    // Store the best confidence for potential error reporting
                    if (DateTime.UtcNow - startTime >= timeoutSpan)
                    {
                        logger.LogWarning($"Could not find '{imageName}' within {timeoutSeconds}s timeout (best confidence: {confidence:F3})");
                        break;
                    }

                    await Task.Delay(pollIntervalMs, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogError($"Error in FindAndClickImageWithTimeoutAsync for '{imageName}': {ex.Message}");
                    await Task.Delay(pollIntervalMs, cancellationToken);
                }
            }
            
            var totalElapsed = DateTime.UtcNow - startTime;
            logger.LogWarning($"Failed to find '{imageName}' after {totalElapsed.TotalSeconds:F1}s");
            return false;
        }

    }
}
