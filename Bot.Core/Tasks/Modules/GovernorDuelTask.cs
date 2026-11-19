using Bot.Core.Logging;
using Bot.Core.Models;
using Bot.Core.Services;
using Bot.Core.Utils;
using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// Governor Duel, a timed event on the Events screen (its own tab): 10 challenges a day against a matched governor.
    /// The bottom button is Challenge (opponent shown, "9/10" under it) or Find Opponent (none yet); a fight ends on a
    /// victory/defeat card closed by a tap on blank space. When the day's challenges are used the button turns into
    /// "Today's Challenges Completed", then the Daily Milestone chest (1/3/5 wins) is claimed. Once a day (the
    /// challenges reset at 00:00 UTC). Never taps Edit Squad, the formations or Battle Report.
    /// Calibrated on llsa, 2026-11-19 (10 challenges: 6 wins, 4 defeats).
    /// </summary>
    public class GovernorDuelTask : BaseTaskWithCommonPatterns, IScheduledTask
    {
        public override TaskType TaskType => TaskType.GovernorDuel;
        public override string Name => "Governor Duel";

        // The city's Events icon, shared with Alliance Mobilization (animated, often with a red dot)
        private static readonly string EventsIconTemplate = Path.Combine("alliancemobilization", AllianceMobilizationTask.EventsButtonTemplate);
        internal const string TabTemplate = "tab.png";                  // "Governor Duel" label of the unselected tab
        internal const string TitleTemplate = "title.png";              // "Governor Duel" title on the page
        internal const string ChallengeTemplate = "challenge.png";      // "Challenge" on the button (count under it)
        internal const string FindOpponentTemplate = "find-opponent.png";
        internal const string TapToCloseTemplate = "tap-to-close.png";  // "Tap any blank space to close" under a fight's result
        internal const string CompletedTemplate = "completed.png";      // "Today's Challenges" where the button was
        internal const string RewardsTemplate = "rewards.png";          // "Rewards" banner of a claimed chest

        internal static readonly Rectangle TabStripArea = new(0, 90, 720, 110);
        internal static readonly Rectangle TitleArea = new(0, 190, 400, 90);
        internal static readonly Rectangle ButtonArea = new(180, 1130, 360, 120);
        internal static readonly Rectangle TapToCloseArea = new(120, 1140, 480, 100);
        internal static readonly Rectangle RewardsArea = new(200, 260, 320, 120);
        private static readonly Point BlankSpot = new(360, 1235);       // under "Tap any blank space to close"
        private static readonly Point MilestoneChest = new(634, 305);   // chest over "5/5" at the top right
        private static readonly Point FirstMilestone = new(190, 480);   // the "1" chest of the popup: a tap claims every lit chest
        private static readonly Point RewardsClose = new(360, 1180);    // "Tap anywhere to exit"

        // Every template: 1.00 on its own screen, <= 0.55 on the other Governor Duel / Events / city screens
        internal const double Threshold = 0.80;
        private const int MaxStripPages = 4;
        private const int MaxSteps = 40;          // 10 challenges x (find, challenge, result) with room for slow screens
        private const int MaxUnknownSteps = 6;

        private enum Screen { Unknown, Challenge, FindOpponent, Result, Completed }

        public DateTime? GetNextRunUtc(string accountKey, AccountSettings account) => DailyGate.NextRunUtc(TaskType, accountKey);

        public void ClearSchedule(string accountKey) => DailyGate.Clear(TaskType, accountKey);

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            try
            {
                int instance = account.InstanceNumber;
                string accountKey = Schedule.AccountKey(AccountDetectionTask.GetCachedAccountId(instance), account);
                if (DailyGate.DoneToday(TaskType, accountKey)) return TaskExecutionDetails.SkippedBecause("Already done today");
                if (!EventCalendar.MayRun(TaskType, DateTime.UtcNow))
                {
                    DailyGate.MarkDone(TaskType, accountKey);
                    return TaskExecutionDetails.SkippedBecause("Not on the event calendar today");
                }

                var locator = new LocatorService(logger, account, TaskType);
                await locator.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);
                string result = await RunOnEventsAsync(accountKey, instance, logger, cancellationToken);
                await locator.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);
                logger.LogInfo($"[{account.AccountName}] Governor Duel: {result}");
                return TaskExecutionDetails.Succeeded(result);
            }
            catch (OperationCanceledException)
            {
                return TaskExecutionDetails.Failed("Task was cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error in Governor Duel: {ex.Message}");
                return TaskExecutionDetails.Failed(ex.Message);
            }
        }

        private async Task<string> RunOnEventsAsync(string accountKey, int instance, LogService logger, CancellationToken ct)
        {
            if (!await OpenEventsAsync(instance, logger, ct)) return "Events screen did not open, trying again next cycle";
            if (!await OpenDuelTabAsync(instance, logger, ct))
            {
                DailyGate.MarkDone(TaskType, accountKey);
                return "Governor Duel not running, checking again after the daily reset";
            }

            var (challenges, done) = await ChallengeUntilDoneAsync(instance, logger, ct);
            if (!done) return $"{challenges} challenge(s), stopped on an unknown screen; trying again next cycle";
            bool claimed = await ClaimMilestonesAsync(instance, logger, ct);
            DailyGate.MarkDone(TaskType, accountKey);
            return $"{challenges} challenge(s), today's challenges completed{(claimed ? ", milestone rewards claimed" : "")}";
        }

        private async Task<bool> OpenEventsAsync(int instance, LogService logger, CancellationToken ct)
        {
            var icon = (found: false, rect: Rectangle.Empty, confidence: 0.0);
            for (int attempt = 0; attempt < 3 && !icon.found; attempt++)
            {
                if (attempt > 0) await Task.Delay(1000, ct);
                icon = await MatchAsync(EventsIconTemplate, AllianceMobilizationTask.EventsButtonArea, AllianceMobilizationTask.EventsThreshold, instance, logger);
            }
            if (!icon.found)
            {
                logger.LogWarning($"Governor Duel: no Events icon in the city (best {icon.confidence:F2})");
                return false;
            }
            await ClickCenterInRectAsync(instance, logger, icon.rect);
            await Task.Delay(2500, ct);
            return true;
        }

        private async Task<bool> IsOnDuelTabAsync(int instance, LogService logger) =>
            (await MatchAsync(TitleTemplate, TitleArea, Threshold, instance, logger)).found;

        /// <summary>The Events screen opens on its last tab; otherwise the strip is paged from its start (slow drags) for the label</summary>
        private async Task<bool> OpenDuelTabAsync(int instance, LogService logger, CancellationToken ct)
        {
            if (await IsOnDuelTabAsync(instance, logger)) return true;
            for (int i = 0; i < 3; i++)
            {
                await SwipeAsync(instance, logger, new Point(150, 150), new Point(620, 150), 400);
                await Task.Delay(500, ct);
            }
            for (int page = 0; page < MaxStripPages; page++)
            {
                var tab = await MatchAsync(TabTemplate, TabStripArea, Threshold, instance, logger);
                if (tab.found)
                {
                    await ClickCenterInRectAsync(instance, logger, tab.rect);
                    return await WaitForAsync(() => IsOnDuelTabAsync(instance, logger), TimeSpan.FromSeconds(4), TimeSpan.FromMilliseconds(500), ct);
                }
                await SwipeAsync(instance, logger, new Point(620, 150), new Point(360, 150), 1500);
                await Task.Delay(800, ct);
            }
            return false;
        }

        /// <summary>Challenge, close the result, Find Opponent, again, until the button says the day's challenges are completed</summary>
        private async Task<(int Challenges, bool Done)> ChallengeUntilDoneAsync(int instance, LogService logger, CancellationToken ct)
        {
            int challenges = 0, unknown = 0;
            for (int step = 0; step < MaxSteps && unknown < MaxUnknownSteps; step++)
            {
                var shot = await TakeScreenshotAsync(instance, logger);
                var (screen, button) = shot == null ? (Screen.Unknown, Rectangle.Empty) : ReadScreen(shot, instance, logger);
                if (screen == Screen.Completed) return (challenges, true);
                unknown = screen == Screen.Unknown ? unknown + 1 : 0;
                switch (screen)
                {
                    case Screen.Challenge:
                        await ClickCenterInRectAsync(instance, logger, button);
                        challenges++;
                        await Task.Delay(6000, ct);   // the fight plays ~5 s before its result card
                        break;
                    case Screen.FindOpponent:
                        await ClickCenterInRectAsync(instance, logger, button);
                        await Task.Delay(2500, ct);
                        break;
                    case Screen.Result:
                        await ClickAsync(instance, logger, BlankSpot);
                        await Task.Delay(2000, ct);
                        break;
                    default:
                        await Task.Delay(1500, ct);   // a fight still playing, or a screen still opening
                        break;
                }
            }
            logger.LogWarning($"Governor Duel: stopped after {challenges} challenge(s) without reaching the completed screen");
            return (challenges, false);
        }

        /// <summary>Which Governor Duel screen this is, with the button to tap on it</summary>
        private (Screen Screen, Rectangle Button) ReadScreen(byte[] shot, int instance, LogService logger)
        {
            if (MatchScreenshot(shot, TapToCloseTemplate, TapToCloseArea, Threshold, instance, logger).found) return (Screen.Result, Rectangle.Empty);
            if (MatchScreenshot(shot, CompletedTemplate, ButtonArea, Threshold, instance, logger).found) return (Screen.Completed, Rectangle.Empty);
            var challenge = MatchScreenshot(shot, ChallengeTemplate, ButtonArea, Threshold, instance, logger);
            if (challenge.found) return (Screen.Challenge, challenge.rect);
            var find = MatchScreenshot(shot, FindOpponentTemplate, ButtonArea, Threshold, instance, logger);
            return find.found ? (Screen.FindOpponent, find.rect) : (Screen.Unknown, Rectangle.Empty);
        }

        /// <summary>The Daily Milestone popup: one tap on a chest claims every lit one (a claimed chest only shows a tooltip)</summary>
        private async Task<bool> ClaimMilestonesAsync(int instance, LogService logger, CancellationToken ct)
        {
            await ClickAsync(instance, logger, MilestoneChest);
            await Task.Delay(1500, ct);
            await ClickAsync(instance, logger, FirstMilestone);
            bool claimed = await WaitForAsync(async () => (await MatchAsync(RewardsTemplate, RewardsArea, Threshold, instance, logger)).found,
                TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(500), ct);
            if (claimed)
            {
                await ClickAsync(instance, logger, RewardsClose);
                await Task.Delay(1500, ct);
            }
            return claimed;
        }

        /// <summary>Screenshot tests: the screen the challenge loop sees</summary>
        internal string Classify(byte[] shot, int instance, LogService logger) => ReadScreen(shot, instance, logger).Screen.ToString();

        internal bool HasTab(byte[] shot, int instance, LogService logger) =>
            MatchScreenshot(shot, TabTemplate, TabStripArea, Threshold, instance, logger).found;

        internal bool IsDuelTab(byte[] shot, int instance, LogService logger) =>
            MatchScreenshot(shot, TitleTemplate, TitleArea, Threshold, instance, logger).found;

        internal bool IsRewards(byte[] shot, int instance, LogService logger) =>
            MatchScreenshot(shot, RewardsTemplate, RewardsArea, Threshold, instance, logger).found;
    }
}
