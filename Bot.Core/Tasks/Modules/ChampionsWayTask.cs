using Bot.Core.Exceptions;
using Bot.Core.Logging;
using Bot.Core.Models;
using Bot.Core.Services;
using Bot.Core.Utils;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// Champion's Way, a 3-day event on the Events screen: hunt the Elite Rebel with own rallies. The page's "Find Elite
    /// Rebel" (costs one event mace) centres the map on a Lv. 1 Elite Rebel spawned next to the city; its card's Rally (25
    /// stamina) > Hold a rally (3 min is ticked) > flag 8 (20 stamina instead of 25) > Deploy. One rebel at a time: the
    /// next is found once the rally is home. The kill counter under the skull fills chests at 1/3/5/7/10 kills; a lit chest
    /// is tapped to claim it. Once per event per character: done at 10 kills (a run that stops short leaves the gate open).
    /// Auto-join is stopped for the rallies (joined rallies take the march queues) and switched on again after, when it was on.
    /// Never taps Redeem (turns the maces into chests) or the purchase popups.
    /// Calibrated on 2026-11-25 (two kills by hand: Lv. 1 Elite Rebel both times, ~4 min per kill).
    /// </summary>
    public class ChampionsWayTask : RallyTaskBase, IScheduledTask, ILongRunningTask
    {
        public override TaskType TaskType => TaskType.ChampionsWay;
        public override string Name => "Champion's Way";

        internal const string TabTemplate = "tab.png";                // "Champion's Way" label of the unselected tab
        internal const string TitleTemplate = "title.png";            // "Champion's Way" title on the page
        internal const string FindRebelTemplate = "find-rebel.png";   // "Find Elite Rebel" on the page's button
        internal const string AttackRebelTemplate = "attack-rebel.png"; // the same button while a found rebel is still up (no mace spent)
        internal const string RebelTitleTemplate = "rebel-title.png"; // "Elite Rebel" on the rebel's card ("Lv. 1 " in front)
        internal const string RallyTemplate = "rally.png";            // the card's orange Rally
        // "Deploy" over the stamina cost: 0.99-1.00 on the deploy screen, <= 0.37 elsewhere (Castle Battle's deploy template scores 0.28 here)
        internal const string DeployTemplate = "deploy.png";
        // Deploy without enough stamina opens "Get More" (Governor Stamina): the free Comforting Embrace "Claim" (+50, the pet
        // skill), else (ChampionsWaySettings.UseStaminaItems) the stamina item's green "Use" (+10 a tap, one item). A Use tap pops up
        // an "x18" bulk button with a gems "x18 5,400" bubble under it, next to "Buy & Use": none of those are tapped.
        // Templates 0.99-1.00 present, <= 0.46 elsewhere
        internal const string GetMoreTemplate = "getmore-title.png";
        internal const string StaminaClaimTemplate = "stamina-claim.png";
        internal const string StaminaUseTemplate = "stamina-use.png";
        internal static readonly Rectangle GetMoreArea = new(200, 100, 320, 90);
        internal static readonly Rectangle StaminaClaimArea = new(480, 350, 200, 820);
        private static readonly Point GetMoreClosePoint = new(668, 145);
        private static readonly Point RewardsExitPoint = new(360, 1240);   // under the Get More popup: no button there
        private static readonly string HoldRallyTemplate = Path.Combine("bearhunt", BearHuntTask.HoldRallyTemplate);

        internal static readonly Rectangle TitleArea = new(20, 200, 400, 80);
        internal static readonly Rectangle FindRebelArea = new(200, 1130, 320, 110);
        internal static readonly Rectangle CardArea = new(0, 250, 720, 1000);
        internal static readonly Rectangle KillsArea = new(58, 1052, 76, 38);   // the white count under the skull, left of the bar
        private static readonly Point RebelPoint = new(360, 560);              // the rebel, with the map centred on it
        private static readonly Point ExitPoint = new(360, 650);               // "Tap anywhere to exit"; on the page only the rebel art

        // Chests over the kill bar: ticks at 1/3/5/7/10 kills. A lit chest glows (751 yellow pixels around it, <= 148 unlit or open)
        internal static readonly int[] ChestMilestones = { 1, 3, 5, 7, 10 };
        private static readonly int[] ChestX = { 165, 270, 376, 481, 628 };
        private const int ChestTop = 945, ChestBottom = 1020, ChestHalfWidth = 45, ChestY = 990;
        internal const int LitChestPixels = 300;

        internal const int TargetKills = 10;      // the last chest
        private const int MaxStaminaRefills = 4;   // Get More rounds per rally (a claim or two items each)
        private const int ItemsPerRefill = 2;      // +10 each: one 20-stamina rally (flag 8)
        private const int MaxMissedKills = 2;     // rallies in a row that didn't raise the count: stop
        private static readonly TimeSpan HuntTime = TimeSpan.FromMinutes(40);       // no new rally after this
        private static readonly TimeSpan KillTimeout = TimeSpan.FromMinutes(8);   // rally (3 min) + the march there, with room
        private static readonly TimeSpan FreeQueueWait = TimeSpan.FromMinutes(3);

        public TimeSpan RunTimeout => TimeSpan.FromMinutes(50);

        private enum RallyOutcome { Sent, NoButton, NoRebel, NoRally, NoDeploy, NoStamina, NotSent }

        public DateTime? GetNextRunUtc(string accountKey, AccountSettings account) => EventRunGate.NextRunUtc(TaskType, accountKey, DateTime.UtcNow);

        public void ClearSchedule(string accountKey) => EventRunGate.Clear(TaskType, accountKey);

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            int instance = account.InstanceNumber;
            string character = CharacterRoster.GetActiveName(instance) ?? account.AccountName;
            try
            {
                string accountKey = Schedule.AccountKey(AccountDetectionTask.GetCachedAccountId(instance), account);
                if (EventRunGate.Done(TaskType, accountKey, DateTime.UtcNow)) return TaskExecutionDetails.SkippedBecause("Done for this Champion's Way");
                if (!EventCalendar.MayRun(TaskType, DateTime.UtcNow)) return TaskExecutionDetails.SkippedBecause("Not on the event calendar today");

                var locator = new LocatorService(logger, account, TaskType);
                await locator.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);
                string result = await RunAsync(account, character, accountKey, locator, logger, cancellationToken);
                await locator.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);
                logger.LogInfo($"[{character}] Champion's Way: {result}");
                return TaskExecutionDetails.Succeeded(result);
            }
            catch (OperationCanceledException)
            {
                return TaskExecutionDetails.Failed("Task was cancelled");
            }
            catch (Exception ex) when (ex is not BotLostException)
            {
                logger.LogError($"[{character}] Champion's Way: {ex.Message}");
                return TaskExecutionDetails.Failed(ex.Message);
            }
        }

        private async Task<string> RunAsync(AccountSettings account, string character, string accountKey, LocatorService locator, LogService logger, CancellationToken ct)
        {
            int instance = account.InstanceNumber;
            if (!await OpenEventsScreenAsync(instance, logger, ct)) return "Events screen did not open, trying again next cycle";
            var page = await OpenTabAndReadAsync(instance, logger, ct);
            if (page == null)
            {
                await SaveDebugScreenshotAsync(instance, logger, $"no-tab-{DateTime.Now:HHmmss}.png", Path.Combine("screenshots", "championsway"));
                if (TodayOnCalendar()) return "Champion's Way tab not found although the calendar shows the event, trying again next cycle";
                EventRunGate.MarkDone(TaskType, accountKey, DateTime.UtcNow.Date);
                return "Champion's Way not running, checking again after the daily reset";
            }
            int claimed = await ClaimChestsAsync(page.Value.Kills, instance, logger, ct);
            if (page.Value.Kills >= TargetKills)
            {
                EventRunGate.MarkDone(TaskType, accountKey, EventLastDay());
                return $"{page.Value.Kills} kills, done for this event{ClaimedText(claimed)}";
            }

            await locator.EnsureViewAsync(ViewType.BaseView, instance, ct);
            string autoJoin = await SwitchAutoJoinOffAsync(character, instance, logger, ct);
            logger.LogInfo($"[{character}] Champion's Way: {autoJoin}");
            bool restore = autoJoin == AutoJoinSwitchedOff;
            try
            {
                var hunt = await HuntAsync(character, page.Value.Kills, account.ChampionsWaySettings, locator, instance, logger, ct);
                claimed += hunt.Claimed;
                if (hunt.Kills is >= TargetKills)
                    EventRunGate.MarkDone(TaskType, accountKey, EventLastDay());
                string kills = hunt.Kills is int k ? $"{k} kills" : "kill count unreadable";
                return $"{hunt.Rallies} rally(ies), {kills}{(hunt.Kills is >= TargetKills ? ", done for this event" : $" ({hunt.Stop})")}{ClaimedText(claimed)}";
            }
            catch (OperationCanceledException)
            {
                restore = false;   // stopped by hand or timed out: no more taps
                throw;
            }
            finally
            {
                if (restore) await RestoreAutoJoinAsync(account, character, logger);
            }
        }

        private static string ClaimedText(int chests) => chests > 0 ? $", {chests} chest(s) claimed" : "";

        /// <summary>The calendar bar's last day for the event running today; else today + 2 (a 3-day event)</summary>
        private static DateTime EventLastDay() => TodaysBar()?.LastDay ?? DateTime.UtcNow.Date.AddDays(2);

        private static bool TodayOnCalendar() => TodaysBar() != null;

        private static CalendarEvent? TodaysBar()
        {
            var today = DateTime.UtcNow.Date;
            return EventCalendar.For(TaskType.ChampionsWay).FirstOrDefault(e => today >= e.FirstDay && today <= e.LastDay);
        }

        /// <summary>Events icon > the Champion's Way tab; the kill count read off the page. Null when the screen or the tab isn't there.</summary>
        private async Task<(int? Kills, byte[] Shot)?> OpenPageAsync(int instance, LogService logger, CancellationToken ct) =>
            await OpenEventsScreenAsync(instance, logger, ct) ? await OpenTabAndReadAsync(instance, logger, ct) : null;

        /// <summary>On the Events screen: the Champion's Way tab and its kill count. Null when the tab isn't there.</summary>
        private async Task<(int? Kills, byte[] Shot)?> OpenTabAndReadAsync(int instance, LogService logger, CancellationToken ct)
        {
            if (!await OpenEventsTabAsync(TabTemplate, Threshold, () => IsOnPageAsync(instance, logger), instance, logger, ct)) return null;
            await Task.Delay(1000, ct);
            var shot = await TakeScreenshotAsync(instance, logger);
            if (shot == null) return null;
            int? kills = ReadKills(shot);
            logger.LogInfo($"Champion's Way: kill count {(kills?.ToString() ?? "unreadable")}");
            return (kills, shot);
        }

        private async Task<bool> IsOnPageAsync(int instance, LogService logger) =>
            (await MatchAsync(TitleTemplate, TitleArea, Threshold, instance, logger)).found;

        /// <summary>Where the hunt stands: rallies sent, the last kill count read, chests claimed, and why it stopped</summary>
        private sealed class HuntState
        {
            public int Rallies;
            public int? Kills;
            public int Missed;
            public int Claimed;
            public string? Stop;
            public DateTime Until;
            public ChampionsWaySettings Settings = new();

            public bool Done => Kills is >= TargetKills;

            /// <summary>The count after a rally: a rally that didn't raise it counts as missed</summary>
            public void AfterRally(int? kills)
            {
                bool rose = kills > Kills || (kills != null && Kills == null);
                Missed = rose ? 0 : Missed + 1;
                Kills = kills ?? Kills;
            }

            public string? StopReason()
            {
                if (DateTime.UtcNow > Until) return "time is up";
                return Missed >= MaxMissedKills ? "the kill count did not rise" : null;
            }
        }

        /// <summary>Rally after rally from the map until 10 kills, a step fails, the count stops rising or the time is up</summary>
        private async Task<HuntState> HuntAsync(string character, int? kills, ChampionsWaySettings settings, LocatorService locator, int instance, LogService logger, CancellationToken ct)
        {
            var hunt = new HuntState { Kills = kills, Until = DateTime.UtcNow + HuntTime, Settings = settings };
            while (!hunt.Done && hunt.Stop == null)
            {
                hunt.Stop = hunt.StopReason() ?? await HuntOnceAsync(character, hunt, locator, instance, logger, ct);
            }
            if (hunt.Done && hunt.Rallies > 0) await ClaimLastChestAsync(hunt, locator, instance, logger, ct);
            await locator.EnsureViewAsync(ViewType.BaseView, instance, ct);
            return hunt;
        }

        /// <summary>The 10th kill came with the last rally's watch: the page once more for the last chest</summary>
        private async Task ClaimLastChestAsync(HuntState hunt, LocatorService locator, int instance, LogService logger, CancellationToken ct)
        {
            await locator.EnsureViewAsync(ViewType.MapView, instance, ct);
            if (await OpenPageAsync(instance, logger, ct) != null)
                hunt.Claimed += await ClaimChestsAsync(hunt.Kills, instance, logger, ct);
        }

        /// <summary>One rally: a free queue, the page (count, chests), the rally, its return, the count again. Returns why to stop, or null.</summary>
        private async Task<string?> HuntOnceAsync(string character, HuntState hunt, LocatorService locator, int instance, LogService logger, CancellationToken ct)
        {
            await locator.EnsureViewAsync(ViewType.MapView, instance, ct);
            if (!await WaitForFreeQueueAsync(character, instance, logger, ct)) return "no free march queue";
            var page = await OpenPageAsync(instance, logger, ct);
            if (page == null) return "Champion's Way page did not open";
            hunt.Kills = page.Value.Kills ?? hunt.Kills;
            hunt.Claimed += await ClaimChestsAsync(hunt.Kills, instance, logger, ct);
            if (hunt.Done) return null;

            var outcome = await SendRallyAsync(character, hunt.Settings, instance, logger, ct);
            if (outcome != RallyOutcome.Sent) return Describe(outcome);
            hunt.Rallies++;
            logger.LogInfo($"[{character}] Champion's Way: rally {hunt.Rallies} sent on the Elite Rebel ({hunt.Kills?.ToString() ?? "?"} kills so far)");
            return await AfterRallyAsync(hunt, locator, instance, logger, ct);
        }

        /// <summary>Back on the page once the rally left, until the rebel is down. Returns why to stop, or null.</summary>
        private async Task<string?> AfterRallyAsync(HuntState hunt, LocatorService locator, int instance, LogService logger, CancellationToken ct)
        {
            await Task.Delay(45_000, ct);   // a rally waits up to 3 minutes for joiners, it starts early when full
            await locator.EnsureViewAsync(ViewType.MapView, instance, ct);   // the deploy screen can leave a bubble or card over the map
            var page = await OpenPageAsync(instance, logger, ct);
            if (page == null) return "Champion's Way page did not open after the rally";
            var (kills, gone) = await WaitForKillAsync(hunt.Kills, page.Value, instance, logger, ct);
            if (!gone) return $"the Elite Rebel was still up {KillTimeout.TotalMinutes:0} min after the rally";
            hunt.AfterRally(kills);
            return null;
        }

        private static string Describe(RallyOutcome outcome) => outcome switch
        {
            RallyOutcome.NoButton => "no Find / Attack Elite Rebel button",
            RallyOutcome.NoRebel => "the rebel's card did not open",
            RallyOutcome.NoRally => "no Rally on the rebel's card",
            RallyOutcome.NoDeploy => "no deploy screen after Rally",
            RallyOutcome.NoStamina => "out of stamina, trying again next cycle",
            _ => "Deploy did not send the rally",
        };

        /// <summary>
        /// After the rally left, on the page: watched until the count rises or its button is back to Find Elite Rebel (the rebel
        /// is gone; "Attack Elite Rebel" while it is up). The Marching tracker can't tell: it read 0/0 on llsa 30 s after the
        /// rally left (2026-11-25). Gone = false when the rebel was still up at the timeout: no second rally on it.
        /// </summary>
        private async Task<(int? Kills, bool Gone)> WaitForKillAsync(int? before, (int? Kills, byte[] Shot) page, int instance, LogService logger, CancellationToken ct)
        {
            var until = DateTime.UtcNow + KillTimeout;
            int? kills = page.Kills;
            byte[] shot = page.Shot;
            while (!(kills > before) && !IsFindButton(shot, instance, logger))
            {
                if (DateTime.UtcNow > until) return (kills, false);
                await Task.Delay(15_000, ct);
                shot = await TakeScreenshotAsync(instance, logger) ?? shot;
                kills = ReadKills(shot) ?? kills;
            }
            logger.LogInfo($"Champion's Way: the Elite Rebel is down, kill count {kills?.ToString() ?? "unreadable"}");
            return (kills, true);
        }

        /// <summary>From the page: Find Elite Rebel > the rebel > Rally > Hold a rally > flag 8 > Deploy</summary>
        private async Task<RallyOutcome> SendRallyAsync(string character, ChampionsWaySettings settings, int instance, LogService logger, CancellationToken ct)
        {
            var shot = await TakeScreenshotAsync(instance, logger);
            var button = shot == null ? null : RebelButton(shot, instance, logger);
            if (button == null) return await StepFailedAsync(character, RallyOutcome.NoButton, "no-find-button", instance, logger);
            await ClickCenterInRectAsync(instance, logger, button.Value);
            await Task.Delay(3500, ct);

            if (!await OpenRebelCardAsync(instance, logger, ct)) return await StepFailedAsync(character, RallyOutcome.NoRebel, "no-rebel-card", instance, logger);
            var rally = await MatchAsync(RallyTemplate, CardArea, Threshold, instance, logger);
            if (!rally.found) return await StepFailedAsync(character, RallyOutcome.NoRally, "no-rally-button", instance, logger);
            await ClickCenterInRectAsync(instance, logger, rally.rect);
            await Task.Delay(2000, ct);

            var hold = await MatchAsync(HoldRallyTemplate, BearHuntTask.HoldRallyArea, BearHuntTask.BearThreshold, instance, logger);
            if (hold.found)
            {
                await ClickCenterInRectAsync(instance, logger, hold.rect);
                await Task.Delay(2500, ct);
            }
            if (!(await MatchAsync(DeployTemplate, DeployButtonArea, Threshold, instance, logger)).found)
                return await StepFailedAsync(character, RallyOutcome.NoDeploy, "no-deploy", instance, logger);

            if (settings.RallyFlag > 0) await SelectFlagAsync(settings.RallyFlag, instance, logger, ct);
            return await DeployAsync(character, settings, instance, logger, ct);
        }

        /// <summary>
        /// Deploy; on "Get More" (stamina short) the free claim or stamina items and Deploy again, else back out.
        /// Sent only when the deploy screen closed without the stamina popup (2026-11-25: the popup over it was taken for a sent rally).
        /// </summary>
        private async Task<RallyOutcome> DeployAsync(string character, ChampionsWaySettings settings, int instance, LogService logger, CancellationToken ct)
        {
            for (int attempt = 0; attempt <= MaxStaminaRefills; attempt++)
            {
                await ClickAsync(instance, logger, DeployPoint);
                await Task.Delay(2500, ct);
                var shot = await TakeScreenshotAsync(instance, logger);
                if (shot == null) continue;
                bool stamina = MatchScreenshot(shot, GetMoreTemplate, GetMoreArea, Threshold, instance, logger).found;
                if (!stamina && !MatchScreenshot(shot, DeployTemplate, DeployButtonArea, Threshold, instance, logger).found) return RallyOutcome.Sent;
                if (!stamina) break;
                if (attempt == MaxStaminaRefills || !await RefillStaminaAsync(character, settings, shot, instance, logger, ct))
                {
                    await ClickAsync(instance, logger, GetMoreClosePoint);
                    await Task.Delay(1500, ct);
                    await ClickAsync(instance, logger, BackPoint);   // the deploy screen
                    await Task.Delay(1500, ct);
                    logger.LogWarning($"[{character}] Champion's Way: {Describe(RallyOutcome.NoStamina)}");
                    return RallyOutcome.NoStamina;
                }
            }
            var failed = await StepFailedAsync(character, RallyOutcome.NotSent, "deploy-stayed", instance, logger);
            await ClickAsync(instance, logger, BackPoint);
            await Task.Delay(1500, ct);
            return failed;
        }

        /// <summary>
        /// On Get More: the free Comforting Embrace (+50), else two stamina items (+20) when allowed; then the popup is closed
        /// (back on the deploy screen). False when neither was possible.
        /// </summary>
        private async Task<bool> RefillStaminaAsync(string character, ChampionsWaySettings settings, byte[] shot, int instance, LogService logger, CancellationToken ct)
        {
            var claim = MatchScreenshot(shot, StaminaClaimTemplate, StaminaClaimArea, Threshold, instance, logger);
            if (claim.found)
            {
                await ClickCenterInRectAsync(instance, logger, claim.rect);
                await Task.Delay(2500, ct);
                await ClickAsync(instance, logger, RewardsExitPoint);   // "Tap anywhere to exit"
                await Task.Delay(1500, ct);
                logger.LogInfo($"[{character}] Champion's Way: claimed the free Comforting Embrace stamina");
            }
            else if (!settings.UseStaminaItems || !await UseStaminaItemsAsync(character, shot, instance, logger, ct))
            {
                return false;
            }
            await ClickAsync(instance, logger, GetMoreClosePoint);
            await Task.Delay(1500, ct);
            return true;
        }

        /// <summary>Taps the stamina item's green Use (one item, +10 each tap; it stays put while the x18 bubble shows beside it)</summary>
        private async Task<bool> UseStaminaItemsAsync(string character, byte[] shot, int instance, LogService logger, CancellationToken ct)
        {
            var use = MatchScreenshot(shot, StaminaUseTemplate, StaminaClaimArea, Threshold, instance, logger);
            if (!use.found) return false;
            var point = new Point(use.rect.X + use.rect.Width / 2, use.rect.Y + use.rect.Height / 2);
            for (int i = 0; i < ItemsPerRefill; i++)
            {
                await ClickAsync(instance, logger, point);
                await Task.Delay(1500, ct);
            }
            logger.LogInfo($"[{character}] Champion's Way: used {ItemsPerRefill} stamina items (+{ItemsPerRefill * 10})");
            return true;
        }

        internal bool HasStaminaUse(byte[] shot, int instance, LogService logger) =>
            MatchScreenshot(shot, StaminaUseTemplate, StaminaClaimArea, Threshold, instance, logger).found;

        /// <summary>Tap the rebel until its card opens (the map can draw it a moment late)</summary>
        private async Task<bool> OpenRebelCardAsync(int instance, LogService logger, CancellationToken ct)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0) await Task.Delay(2000, ct);
                await ClickAsync(instance, logger, RebelPoint);
                await Task.Delay(2000, ct);
                if ((await MatchAsync(RebelTitleTemplate, CardArea, Threshold, instance, logger)).found) return true;
            }
            return false;
        }

        private async Task<RallyOutcome> StepFailedAsync(string character, RallyOutcome outcome, string step, int instance, LogService logger)
        {
            await SaveDebugScreenshotAsync(instance, logger, $"{step}-{DateTime.Now:HHmmss}.png", Path.Combine("screenshots", "championsway"));
            logger.LogWarning($"[{character}] Champion's Way: {Describe(outcome)} (screenshot saved)");
            return outcome;   // the caller's EnsureViewAsync closes the card or popup
        }

        /// <summary>All march queues busy: wait a little (joined rallies come home quickly); false when none frees up</summary>
        private async Task<bool> WaitForFreeQueueAsync(string character, int instance, LogService logger, CancellationToken ct)
        {
            if ((await ReadMarchTrackerAsync(instance, logger)).HasFreeQueue) return true;
            logger.LogInfo($"[{character}] Champion's Way: every march queue is busy, waiting up to {FreeQueueWait.TotalMinutes:0} min");
            return await WaitForAsync(async () => (await ReadMarchTrackerAsync(instance, logger)).HasFreeQueue, FreeQueueWait, TimeSpan.FromSeconds(10), ct);
        }

        /// <summary>Taps every lit chest up to the kill count (a claimed chest opens a Rewards popup, closed by a tap)</summary>
        private async Task<int> ClaimChestsAsync(int? kills, int instance, LogService logger, CancellationToken ct)
        {
            var shot = await TakeScreenshotAsync(instance, logger);
            if (shot == null) return 0;
            int claimed = 0;
            foreach (int milestone in LitChests(shot).Where(m => kills == null || m <= kills))
            {
                int i = Array.IndexOf(ChestMilestones, milestone);
                await ClickAsync(instance, logger, new Point(ChestX[i], ChestY));
                await Task.Delay(2500, ct);
                await ClickAsync(instance, logger, ExitPoint);
                await Task.Delay(1500, ct);
                claimed++;
                logger.LogInfo($"Champion's Way: chest at {milestone} kill(s) claimed");
            }
            return claimed;
        }

        /// <summary>Back on: Auto Join Rally's own Enable step (from the city). Never throws: the result is only logged.</summary>
        private static async Task RestoreAutoJoinAsync(AccountSettings account, string character, LogService logger)
        {
            try
            {
                var autoJoin = new AutoJoinRallyTask();
                await autoJoin.InitializeAsync(logger);
                string? result = await autoJoin.SwitchOnFromBaseAsync(account, logger, CancellationToken.None);
                logger.LogInfo($"[{character}] Champion's Way: {result ?? "auto-join could not be switched on again"}");
            }
            catch (Exception ex) when (ex is not BotLostException)
            {
                logger.LogWarning($"[{character}] Champion's Way: switching auto-join on again failed: {ex.Message}");
            }
        }

        #region Screen reading (screenshot tested)

        /// <summary>The kill count under the skull (white digits with a dark outline); null when unreadable</summary>
        internal static int? ReadKills(byte[] screenshot)
        {
            using var image = Cv2.ImDecode(screenshot, ImreadModes.Color);
            foreach (var mode in new[] { Tesseract.PageSegMode.SingleLine, Tesseract.PageSegMode.SingleWord })
            {
                string text = MaskedTextOcr.Read(image, KillsArea, new Scalar(0, 0, 200), new Scalar(180, 50, 255), "0123456789", mode);
                if (ParseKills(text) is int kills) return kills;
            }
            return null;
        }

        internal static int? ParseKills(string text)
        {
            var m = Regex.Match(text ?? "", @"\d+", RegexOptions.None, RegexTimeouts.Default);
            return m.Success && int.TryParse(m.Value, out int kills) && kills <= 99 ? kills : null;
        }

        /// <summary>The milestones whose chest glows (ready to claim)</summary>
        internal static List<int> LitChests(byte[] screenshot)
        {
            using var image = Cv2.ImDecode(screenshot, ImreadModes.Color);
            using var hsv = image.CvtColor(ColorConversionCodes.BGR2HSV);
            var lit = new List<int>();
            for (int i = 0; i < ChestX.Length; i++)
            {
                var roi = new Rect(ChestX[i] - ChestHalfWidth, ChestTop, 2 * ChestHalfWidth, ChestBottom - ChestTop);
                using var region = new Mat(hsv, roi);
                using var mask = region.InRange(new Scalar(15, 80, 200), new Scalar(40, 255, 255));
                if (Cv2.CountNonZero(mask) >= LitChestPixels) lit.Add(ChestMilestones[i]);
            }
            return lit;
        }

        internal bool IsPage(byte[] shot, int instance, LogService logger) =>
            MatchScreenshot(shot, TitleTemplate, TitleArea, Threshold, instance, logger).found;

        internal bool HasTab(byte[] shot, int instance, LogService logger) =>
            MatchScreenshot(shot, TabTemplate, EventsTabStripArea, Threshold, instance, logger).found;

        /// <summary>The page says Find Elite Rebel: no rebel of ours is up</summary>
        internal bool IsFindButton(byte[] shot, int instance, LogService logger) =>
            MatchScreenshot(shot, FindRebelTemplate, FindRebelArea, Threshold, instance, logger).found;

        /// <summary>The page's Find Elite Rebel, or Attack Elite Rebel while the last rebel found is still up</summary>
        internal Rectangle? RebelButton(byte[] shot, int instance, LogService logger)
        {
            foreach (var template in new[] { FindRebelTemplate, AttackRebelTemplate })
            {
                var match = MatchScreenshot(shot, template, FindRebelArea, Threshold, instance, logger);
                if (match.found) return match.rect;
            }
            return null;
        }

        internal bool IsStaminaPopup(byte[] shot, int instance, LogService logger) =>
            MatchScreenshot(shot, GetMoreTemplate, GetMoreArea, Threshold, instance, logger).found;

        internal bool HasFreeStamina(byte[] shot, int instance, LogService logger) =>
            MatchScreenshot(shot, StaminaClaimTemplate, StaminaClaimArea, Threshold, instance, logger).found;

        internal bool IsDeployScreen(byte[] shot, int instance, LogService logger) =>
            MatchScreenshot(shot, DeployTemplate, DeployButtonArea, Threshold, instance, logger).found;

        internal bool IsRebelCard(byte[] shot, int instance, LogService logger) =>
            MatchScreenshot(shot, RebelTitleTemplate, CardArea, Threshold, instance, logger).found
            && MatchScreenshot(shot, RallyTemplate, CardArea, Threshold, instance, logger).found;

        #endregion
    }
}
