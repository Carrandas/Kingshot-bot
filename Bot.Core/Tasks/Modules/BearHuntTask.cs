using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.Services;
using Bot.Core.Exceptions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using OpenCvSharp;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// In the cycle (its switch is the Bear Hunt switch), once a day after the 00:00 UTC reset: reads the map's Task
    /// List (tap the UTC banner) for the next bear of our trap; the GUI starts the Bear Hunt mode 10 minutes before it.
    /// In the Bear Hunt mode, one pass for the active character (the mode picks the character, see <see cref="BearHuntPlanner"/>):
    /// 1. prep time (10 min before): Alliance > War > Auto-Join > Stop, and recall every march with a return arrow
    ///    in the Marching panel (gatherers), once per character;
    /// 2. bear up and the own troops home: bear icon (bottom left) > rally on the trap with the own team flag (5/10/85);
    ///    none after the start + 24:30;
    /// 3. join the top rallies of Alliance > War with the join team flag, up to 6 / the free march queues.
    /// BETA: the bear icon and the trap's rally button are not calibrated yet (no live bear seen). Those steps save a
    /// screenshot under logs/screenshots/bearhunt/ and skip.
    /// </summary>
    public class BearHuntTask : RallyTaskBase, IScheduledTask
    {
        public override TaskType TaskType => TaskType.BearHunt;
        public override string Name => "Bear Hunt";

        // Own rally (captured on the bear of 2026-11-08): bear icon > the bear on the map > Rally > Hold a rally (5 min) > deploy
        internal const string BearIconTemplate = "bear-icon.png";     // bear head above the "30min" timer, left of the mail: 0.98-1.00, <= 0.51 elsewhere
        internal const string BearTitleTemplate = "bear-title.png";   // "Raging Bear" on the bear's card: 1.00; War list rows 0.84, <= 0.60 elsewhere
        internal const string BearRallyTemplate = "bear-rally.png";   // the card's orange Rally: 1.00, <= 0.67 elsewhere
        internal const string HoldRallyTemplate = "hold-rally.png";   // "Hold a rally" popup button: 1.00, <= 0.40 elsewhere (Titan Roc's popup 0.84)
        internal const double BearThreshold = 0.92;
        private const int BearTapAttempts = 4;
        internal const double BearRowThreshold = 0.75;   // "Raging Bear" under a War row's members: 0.83-0.84 (the card's text, other background)
        private static readonly TimeSpan NotUpRetry = TimeSpan.FromSeconds(20);    // no bear icon yet: look again after this
        private const string AutoJoinStopTemplate = "autojoin-stop.png";  // the Auto-Join popup's orange Stop: 1.00, <= 0.38 elsewhere
        private const string RecallArrowTemplate = "recall-arrow.png";    // Marching panel's return arrow: 0.94-1.00 on gathering rows, <= 0.82 on a rally's eye
        private const string RecallTitleTemplate = "recall-title.png";    // "Recall squad?" popup: 1.00, <= 0.32 elsewhere
        private const string TaskListTitleTemplate = "tasklist-title.png"; // "Task List" (UTC banner): 1.00, <= 0.35 on other popups
        // Shared with Auto Join Rally
        private static readonly string AutoJoinOnTemplate = Path.Combine("autojoinrally", "autojoin-on.png");     // "Auto-joining..." button
        private static readonly string AutoJoinTitleTemplate = Path.Combine("autojoinrally", "autojoin-title.png");

        // Battle pet skills (prep): paw > Pet Skill > a red-badge skill > Quick Use > "Use all available Battle Skills?" > Use
        internal const string PetBattleBadgeTemplate = "pet-battle-badge.png"; // red crossed-swords badge: 0.99-1.00 in the grid, <= 0.57 elsewhere
        internal const string PetQuickUseTemplate = "pet-quick-use.png";       // green Quick Use: 1.00, <= 0.48 elsewhere (gone while the skills are active)
        internal const string PetConfirmTitleTemplate = "pet-confirm-title.png"; // "Confirmation": 0.99-1.00, <= 0.40 elsewhere
        private static readonly string PawButtonTemplate = Path.Combine("petskill", "paw-button.png");
        private static readonly string PetTitleTemplate = Path.Combine("petskill", "pet-skill-title.png");
        private static readonly Rectangle PawButtonArea = new Rectangle(600, 780, 120, 240);     // base view ~y 800, map view y 931
        private static readonly Rectangle PetTitleArea = new Rectangle(200, 110, 320, 90);
        internal static readonly Rectangle PetGridArea = new Rectangle(60, 230, 600, 530);
        internal static readonly Rectangle PetQuickUseArea = new Rectangle(40, 1020, 340, 140);
        internal static readonly Rectangle PetConfirmTitleArea = new Rectangle(200, 340, 320, 110);
        private static readonly System.Drawing.Point PetBadgeToIcon = new System.Drawing.Point(69, 60);
        private static readonly System.Drawing.Point PetConfirmUsePoint = new System.Drawing.Point(510, 812);
        private static readonly System.Drawing.Point PetClosePoint = new System.Drawing.Point(668, 153);

        internal static readonly Rectangle BearIconArea = new Rectangle(400, 760, 320, 420);    // base view y 928; map view y 1024 on 2026-11-08, y 833 on 2026-11-10 (0.99; the old top of 880 missed it)
        private static readonly System.Drawing.Point BearPoint = new System.Drawing.Point(360, 480); // the bear, with the map centred on it
        internal static readonly Rectangle BearCardArea = new Rectangle(0, 250, 720, 1000);
        internal static readonly Rectangle HoldRallyArea = new Rectangle(100, 600, 520, 400);
        private static readonly Rectangle AutoJoinButtonArea = new Rectangle(200, 1170, 320, 100);
        private static readonly Rectangle AutoJoinTitleArea = new Rectangle(240, 110, 240, 80);
        private static readonly Rectangle AutoJoinPopupButtonsArea = new Rectangle(40, 1020, 640, 140);
        private static readonly System.Drawing.Point AutoJoinClosePoint = new System.Drawing.Point(667, 147);
        private static readonly Rectangle RecallArrowArea = new Rectangle(180, 230, 70, 380);   // the panel's 6 rows, 61 px apart
        private const double RecallArrowThreshold = 0.90;
        private static readonly Rectangle RecallTitleArea = new Rectangle(200, 400, 320, 100);
        private static readonly System.Drawing.Point RecallConfirmPoint = new System.Drawing.Point(512, 789);
        private const int MaxRecalls = 8;
        // Task List: UTC banner at the top of the map, the list below "UTC Time", the close X
        private static readonly System.Drawing.Point UtcBannerPoint = new System.Drawing.Point(245, 28);
        private static readonly Rectangle TaskListTitleArea = new Rectangle(0, 320, 260, 80);
        private static readonly System.Drawing.Point TaskListClosePoint = new System.Drawing.Point(683, 357);
        private const int TaskListTop = 460, TaskListBottom = 1275;
        private const int MaxListScrolls = 5;
        private static readonly System.Drawing.Point ListDragFrom = new System.Drawing.Point(360, 1150);
        private static readonly System.Drawing.Point ListDragTo = new System.Drawing.Point(360, 750);

        private static readonly TimeSpan FullRetry = TimeSpan.FromSeconds(20);      // all marches out: look again after this (joined rallies come back quickly)
        private static readonly TimeSpan NoRalliesRetry = TimeSpan.FromSeconds(30); // War list empty: look again after this

        // Bear Hunt mode: Alliance Help would go to the base and back every pass; with the bear crowd on the map those
        // transitions time out and recovery restarted the game (2026-11-08)
        protected override bool SkipAutoTasks => BearHuntStatus.IsRunning;

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            int instance = account.InstanceNumber;
            var settings = account.BearHuntSettings;
            string character = StatusName(account);
            if (!settings.IsValid) return TaskExecutionDetails.Failed("Bear Hunt settings: the teams must be flags 1-8, the trap 1 or 2");
            // in the cycle: only look up the next bear
            if (BearHuntStatus.BearStartUtc is not DateTime start) return await ReadScheduleOnceADayAsync(account, logger, cancellationToken);

            try
            {
                var locator = new LocatorService(logger, account, TaskType);
                await locator.EnsureViewAsync(ViewType.MapView, instance, cancellationToken);
                var stage = BearHuntPlanner.StageAt(DateTime.UtcNow, start, settings);
                var state = BearHuntStatus.Get(character);
                var parts = new List<string>();

                if ((stage is BearHuntStage.Prep or BearHuntStage.Running) && state?.Prepared != true)
                {
                    parts.Add(await PrepareAsync(character, instance, logger, cancellationToken));
                    await locator.EnsureViewAsync(ViewType.MapView, instance, cancellationToken);
                }
                if (stage == BearHuntStage.Running)
                {
                    parts.Add(await RunBearAsync(character, start, settings, locator, instance, logger, cancellationToken));
                }
                return TaskExecutionDetails.Succeeded(string.Join("; ", parts.Where(p => p.Length > 0)));
            }
            catch (OperationCanceledException)
            {
                return TaskExecutionDetails.Failed("Task was cancelled");
            }
            catch (Exception ex) when (ex is not BotLostException)
            {
                logger.LogError($"[{character}] Bear Hunt: {ex.Message}");
                return TaskExecutionDetails.Failed(ex.Message);
            }
        }

        /// <summary>The name the status uses: the active character, or the instance's name when the roster was never scanned (as the runner)</summary>
        private static string StatusName(AccountSettings account) =>
            account.Characters.Count == 0 ? account.AccountName : CharacterRoster.GetActiveName(account.InstanceNumber) ?? account.AccountName;

        /// <summary>Own rally when the troops are home, then joins</summary>
        private async Task<string> RunBearAsync(string character, DateTime start, BearHuntSettings settings, LocatorService locator,
            int instance, LogService logger, CancellationToken cancellationToken)
        {
            bool sent = false;
            if (BearHuntPlanner.ShouldSendOwn(DateTime.UtcNow, start, settings, BearHuntStatus.Get(character)))
            {
                sent = await SendOwnRallyAsync(character, settings, instance, logger, cancellationToken);
                await locator.EnsureViewAsync(ViewType.MapView, instance, cancellationToken);
            }

            // the own rally does not take one of the march queues (own rally + 6 joins, the user on 2026-11-10): no queue is kept free for it
            int joined = await JoinRalliesAsync(character, settings, instance, logger, cancellationToken);
            await locator.EnsureViewAsync(ViewType.MapView, instance, cancellationToken);
            return $"{(sent ? "own rally sent, " : "")}joined {joined}";
        }

        #region Prep

        /// <summary>Auto-join off (it would send the troops elsewhere), then the marches home</summary>
        private async Task<string> PrepareAsync(string character, int instance, LogService logger, CancellationToken cancellationToken)
        {
            BearHuntStatus.Set(character, BearHuntPhase.Preparing);
            string autoJoin = await SwitchAutoJoinOffAsync(character, instance, logger, cancellationToken);
            string marches = await RecallMarchesAsync(character, instance, logger, cancellationToken);
            string pets = await UsePetBuffsAsync(character, instance, logger, cancellationToken);
            BearHuntStatus.MarkPrepared(character);
            logger.LogInfo($"[{character}] Bear Hunt: prepared ({autoJoin}; {marches}; {pets})");
            return $"{autoJoin}; {marches}; {pets}";
        }

        /// <summary>Paw > Pet Skill > Quick Use on a battle skill: every battle skill (rally capacity, attack...) on for 2 hours</summary>
        private async Task<string> UsePetBuffsAsync(string character, int instance, LogService logger, CancellationToken cancellationToken)
        {
            var paw = await MatchAsync(PawButtonTemplate, PawButtonArea, Threshold, instance, logger);
            if (!paw.found) return "pet buffs: paw button not found";
            await ClickCenterInRectAsync(instance, logger, paw.rect);
            await Task.Delay(2000, cancellationToken);
            if (!(await MatchAsync(PetTitleTemplate, PetTitleArea, Threshold, instance, logger)).found) return "pet buffs: panel did not open";
            try
            {
                string result = await QuickUseBattleSkillsAsync(instance, logger, cancellationToken);
                logger.LogInfo($"[{character}] Bear Hunt: {result}");
                return result;
            }
            finally
            {
                if ((await MatchAsync(PetTitleTemplate, PetTitleArea, Threshold, instance, logger)).found)
                {
                    await ClickAsync(instance, logger, PetClosePoint);
                    await Task.Delay(1500, cancellationToken);
                }
            }
        }

        private async Task<string> QuickUseBattleSkillsAsync(int instance, LogService logger, CancellationToken cancellationToken)
        {
            var badge = await MatchAsync(PetBattleBadgeTemplate, PetGridArea, Threshold, instance, logger);
            if (!badge.found) return "pet buffs: no battle skill in the grid";
            await ClickAsync(instance, logger, new System.Drawing.Point(badge.rect.X + PetBadgeToIcon.X, badge.rect.Y + PetBadgeToIcon.Y));
            await Task.Delay(1500, cancellationToken);

            var quick = await MatchAsync(PetQuickUseTemplate, PetQuickUseArea, Threshold, instance, logger);
            if (!quick.found) return "pet buffs: no Quick Use (already active or on cooldown)";
            await ClickCenterInRectAsync(instance, logger, quick.rect);
            await Task.Delay(1500, cancellationToken);
            if (!(await MatchAsync(PetConfirmTitleTemplate, PetConfirmTitleArea, Threshold, instance, logger)).found)
                return "pet buffs: confirmation did not open";
            await ClickAsync(instance, logger, PetConfirmUsePoint);
            await Task.Delay(2500, cancellationToken);
            return "battle pet skills used";
        }

        /// <summary>Alliance > War > "Auto-joining..." > the popup's Stop (the cycle's Auto Join Rally switches it on again)</summary>
        private async Task<string> SwitchAutoJoinOffAsync(string character, int instance, LogService logger, CancellationToken cancellationToken)
        {
            if (!await OpenWarAsync(character, instance, logger, cancellationToken)) return "auto-join: War screen did not open";
            try
            {
                var on = await MatchAsync(AutoJoinOnTemplate, AutoJoinButtonArea, Threshold, instance, logger);
                if (!on.found) return "auto-join already off";

                await ClickCenterInRectAsync(instance, logger, on.rect);
                await Task.Delay(2000, cancellationToken);
                if (!(await MatchAsync(AutoJoinTitleTemplate, AutoJoinTitleArea, Threshold, instance, logger)).found)
                    return "auto-join: popup did not open";

                string result = await TapAutoJoinStopAsync(character, instance, logger, cancellationToken);
                await ClickAsync(instance, logger, AutoJoinClosePoint);
                await Task.Delay(1500, cancellationToken);
                return result;
            }
            finally
            {
                await LeaveWarAsync(instance, logger, cancellationToken);
            }
        }

        private async Task<string> TapAutoJoinStopAsync(string character, int instance, LogService logger, CancellationToken cancellationToken)
        {
            var stop = await MatchAsync(AutoJoinStopTemplate, AutoJoinPopupButtonsArea, Threshold, instance, logger);
            if (!stop.found)
            {
                logger.LogWarning($"[{character}] Bear Hunt: Auto-Join's Stop button not found ({stop.confidence:F2})");
                return "auto-join: Stop not found";
            }
            await ClickCenterInRectAsync(instance, logger, stop.rect);
            await Task.Delay(1500, cancellationToken);
            return "auto-join switched off";
        }

        /// <summary>
        /// Gatherers out would miss the bear: every Marching panel row with a return arrow > Recall squad? > Confirm.
        /// Rallies (an eye instead of the arrow) stay.
        /// </summary>
        private async Task<string> RecallMarchesAsync(string character, int instance, LogService logger, CancellationToken cancellationToken)
        {
            int recalled = 0;
            for (int i = 0; i < MaxRecalls; i++)
            {
                var arrow = await MatchAsync(RecallArrowTemplate, RecallArrowArea, RecallArrowThreshold, instance, logger);
                if (!arrow.found) break;
                await ClickCenterInRectAsync(instance, logger, arrow.rect);
                await Task.Delay(1200, cancellationToken);
                if (!(await MatchAsync(RecallTitleTemplate, RecallTitleArea, Threshold, instance, logger)).found)
                {
                    logger.LogWarning($"[{character}] Bear Hunt: no Recall popup after the return arrow");
                    break;
                }
                await ClickAsync(instance, logger, RecallConfirmPoint);
                await Task.Delay(1500, cancellationToken);
                recalled++;
            }
            return recalled > 0 ? $"recalled {recalled} marches" : "no marches to recall";
        }

        #endregion

        #region Own rally

        /// <summary>Bear icon > the bear > Rally > Hold a rally > own team flag > Deploy</summary>
        private async Task<bool> SendOwnRallyAsync(string character, BearHuntSettings settings, int instance, LogService logger, CancellationToken cancellationToken)
        {
            BearHuntStatus.Set(character, BearHuntPhase.SendingOwnRally);
            var reached = await ReachDeployScreenAsync(character, instance, logger, cancellationToken);
            if (reached != true)
            {
                if (reached == false) OwnRallyFailed(character, settings);
                return false;
            }

            await ClickAsync(instance, logger, FlagPoint(settings.OwnTeam));
            await Task.Delay(1500, cancellationToken);
            await ClickAsync(instance, logger, DeployPoint);
            await Task.Delay(2500, cancellationToken);
            if ((await MatchAsync(DeployButtonTemplate, DeployButtonArea, Threshold, instance, logger)).found)
            {
                await SaveStepScreenshotAsync("deploy-stayed", instance, logger);
                logger.LogWarning($"[{character}] Bear Hunt: Deploy did not send the own rally (troops not home?)");
                await ClickAsync(instance, logger, BackPoint);
                await Task.Delay(1500, cancellationToken);
                OwnRallyFailed(character, settings);
                return false;
            }

            var back = DateTime.UtcNow + settings.RallyRound;
            BearHuntStatus.OwnRallySent(character, back);
            logger.LogInfo($"[{character}] Bear Hunt: own rally sent with team {settings.OwnTeam}, back around {back.ToLocalTime():HH:mm:ss}");
            return true;
        }

        /// <summary>The bear icon (centres the map on the bear), the bear, its card's Rally, Hold a rally (5 min is ticked), until the deploy screen shows</summary>
        /// <returns>true: on the deploy screen; false: a step failed; null: no bear on the map yet</returns>
        private async Task<bool?> ReachDeployScreenAsync(string character, int instance, LogService logger, CancellationToken cancellationToken)
        {
            var icon = await MatchAsync(BearIconTemplate, BearIconArea, Threshold, instance, logger);
            if (!icon.found)
            {
                logger.LogInfo($"[{character}] Bear Hunt: no bear icon yet ({icon.confidence:F2}), the trap may still be opening");
                BearHuntStatus.BearNotUpUntil(DateTime.UtcNow + NotUpRetry);
                return null;
            }
            await ClickCenterInRectAsync(instance, logger, icon.rect);
            await Task.Delay(2500, cancellationToken);
            return await TapThroughToDeployAsync(character, instance, logger, cancellationToken);
        }

        private async Task<bool> TapThroughToDeployAsync(string character, int instance, LogService logger, CancellationToken cancellationToken)
        {
            if (!await OpenBearCardAsync(instance, logger, cancellationToken))
                return await RallyStepFailedAsync(character, "the bear's card did not open", "no-bear-card", instance, logger, cancellationToken);
            var rally = await MatchAsync(BearRallyTemplate, BearCardArea, BearThreshold, instance, logger);
            if (!rally.found)
                return await RallyStepFailedAsync(character, "no Rally on the bear's card", "no-rally-button", instance, logger, cancellationToken);
            await ClickCenterInRectAsync(instance, logger, rally.rect);
            await Task.Delay(2000, cancellationToken);

            var hold = await MatchAsync(HoldRallyTemplate, HoldRallyArea, BearThreshold, instance, logger);
            if (hold.found)
            {
                await ClickCenterInRectAsync(instance, logger, hold.rect);
                await Task.Delay(2500, cancellationToken);
            }
            if ((await MatchAsync(DeployButtonTemplate, DeployButtonArea, Threshold, instance, logger)).found) return true;
            return await RallyStepFailedAsync(character, "no deploy screen after Hold a rally", "no-deploy", instance, logger, cancellationToken);
        }

        /// <summary>Tap the bear until its card opens: right after a character switch the map draws the bear a few seconds late</summary>
        private async Task<bool> OpenBearCardAsync(int instance, LogService logger, CancellationToken cancellationToken)
        {
            for (int attempt = 0; attempt < BearTapAttempts; attempt++)
            {
                await Task.Delay(attempt == 0 ? 1000 : 2500, cancellationToken);
                await ClickAsync(instance, logger, BearPoint);
                await Task.Delay(2000, cancellationToken);
                if ((await MatchAsync(BearTitleTemplate, BearCardArea, BearThreshold, instance, logger)).found) return true;
            }
            return false;
        }

        private async Task<bool> RallyStepFailedAsync(string character, string what, string step, int instance, LogService logger, CancellationToken cancellationToken)
        {
            await SaveStepScreenshotAsync(step, instance, logger);
            logger.LogWarning($"[{character}] Bear Hunt: {what} (screenshot saved)");
            await Task.Delay(500, cancellationToken);   // the caller's EnsureViewAsync closes the card or popup (BackPoint is the avatar on the map)
            return false;
        }

        #endregion

        /// <summary>Not sent: try again later, backing off when it failed before</summary>
        private static void OwnRallyFailed(string character, BearHuntSettings settings)
        {
            int failures = BearHuntStatus.Get(character)?.OwnFailures ?? 0;
            BearHuntStatus.OwnRallyFailed(character, DateTime.UtcNow + BearHuntPlanner.OwnRetryAfter(failures, settings));
        }

        #region Join rallies

        /// <summary>Alliance > War, then join the top rallies with a green "+" while march queues are free. Returns the joins.</summary>
        private async Task<int> JoinRalliesAsync(string character, BearHuntSettings settings, int instance, LogService logger, CancellationToken cancellationToken)
        {
            var march = await ReadMarchTrackerAsync(instance, logger);
            int allowed = BearHuntPlanner.JoinsAllowed(march.Busy, march.Total, settings.MaxJoins);
            logger.LogInfo($"[{character}] Bear Hunt: marching {march.Busy}/{march.Total}, joining up to {allowed}");
            if (allowed == 0)
            {
                MarkFull(character);
                return 0;
            }
            if (!await OpenWarAsync(character, instance, logger, cancellationToken)) return 0;

            BearHuntStatus.Set(character, BearHuntPhase.Joining);
            int joined = await JoinTopRalliesAsync(character, settings.JoinTeam, allowed, instance, logger, cancellationToken);
            await LeaveWarAsync(instance, logger, cancellationToken);

            // fewer joins than queues: the list ran out (the same for every character), so wait instead of switching
            BearHuntStatus.MarkFull(character, null);
            if (joined < allowed)
            {
                BearHuntStatus.MarkNoRallies(DateTime.UtcNow + NoRalliesRetry);
                BearHuntStatus.Set(character, BearHuntPhase.Joining, "no rally to join");
            }
            return joined;
        }

        private async Task<int> JoinTopRalliesAsync(string character, int team, int allowed, int instance, LogService logger, CancellationToken cancellationToken)
        {
            int joined = 0;
            // a "+" that opened no deploy screen (the rally just joined stays green for a moment, or it just left) is skipped
            var tried = new List<int>();
            for (int attempt = 0; attempt < allowed + 6 && joined < allowed; attempt++)
            {
                var buttons = await FindJoinButtonsWithScrollAsync(instance, logger, cancellationToken, tried);
                if (buttons.Count == 0) break;
                var bear = await FirstBearRowAsync(buttons, character, tried, instance, logger);
                if (bear == null) continue;     // only other rallies on screen (now in tried): the next call scrolls on
                tried.Add(bear.Value.Y);
                if (!await JoinAsync(character, bear.Value, team, instance, logger, cancellationToken)) continue;
                joined++;
                BearHuntStatus.CountJoin(character);
                await Task.Delay(2000, cancellationToken);  // let the list refresh: the joined "+" turns grey
            }
            logger.LogInfo($"[{character}] Bear Hunt: joined {joined} rallies with team {team}");
            return joined;
        }

        /// <summary>
        /// The first "+" whose row targets the bear ("Lv. 1 Raging Bear" ~50 px below the "+") and isn't our own rally
        /// (its "+" opens nothing); Titan Roc and other rallies are skipped
        /// </summary>
        /// <param name="skipped">Gets the rows passed over (not the bear, our own rally)</param>
        private async Task<System.Drawing.Point?> FirstBearRowAsync(List<JoinButton> buttons, string character, List<int> skipped, int instance, LogService logger)
        {
            // one screenshot for the whole list view: nothing is tapped between the rows
            var screenshot = await TakeScreenshotAsync(instance, logger);
            if (screenshot == null)
            {
                skipped.AddRange(buttons.Select(b => b.Center.Y));
                return null;
            }
            using var image = Cv2.ImDecode(screenshot, ImreadModes.Color);
            foreach (var center in buttons.Select(b => b.Center))
            {
                if (!ShouldSkipRow(screenshot, image, center.Y, character, instance, logger)) return center;
                skipped.Add(center.Y);
            }
            return null;
        }

        /// <summary>Not the bear, our own rally, or too full for our troops to matter</summary>
        private bool ShouldSkipRow(byte[] screenshot, Mat image, int plusY, string character, int instance, LogService logger)
        {
            if (!MatchScreenshot(screenshot, BearTitleTemplate, BearRowArea(plusY), BearRowThreshold, instance, logger).found) return true;
            string leader = ReadRallyLeader(image, plusY);
            if (IsOwnRally(leader, character))
            {
                logger.LogInfo($"[{character}] Bear Hunt: skipping our own rally ({leader})");
                return true;
            }
            long? free = FreeTroops(ReadRallyTroops(image, plusY));
            if (free < MinFreeTroops)
            {
                logger.LogInfo($"[{character}] Bear Hunt: skipping {leader}'s rally, only {free:N0} troops free");
                return true;
            }
            return false;
        }

        /// <summary>Where a War row's target line ("Lv. 1 Raging Bear") is, from its "+"</summary>
        internal static Rectangle BearRowArea(int plusY) => new Rectangle(240, Math.Min(plusY + 20, 1280 - 70), 340, 70);

        /// <summary>
        /// The leader read on a War row is this character. OCR garbles the tag's brackets ("KFCIMAS SCHUIL24", "[KFC|Darth Vader"),
        /// so the name must end the read text with at most a tag (+ bracket) of 5 characters before it.
        /// </summary>
        internal static bool IsOwnRally(string leader, string character) => CharacterRoster.EndsWithName(leader, character);

        // Dark text on a beige card (V cap 110 on a War row, 140 in the Task List: its beige is a little darker), white text on the Task List's date bars
        private static readonly Scalar DarkLow = new Scalar(0, 0, 0);
        private static readonly Scalar RallyDarkHigh = new Scalar(180, 255, 110);
        private static readonly Scalar ListDarkHigh = new Scalar(180, 255, 140);
        private static readonly Scalar HeaderWhiteLow = new Scalar(0, 0, 235);
        private static readonly Scalar HeaderWhiteHigh = new Scalar(180, 50, 255);

        /// <summary>The rally leader's name ("[KFC]llsa", dark text) on the row's first line, ~115 px above its "+"</summary>
        internal static string ReadRallyLeader(Mat image, int plusY) => ReadRallyLine(image, plusY - 132, 330, null);

        /// <summary>One line of a War row's card: dark text at <paramref name="top"/> (screen y), "" when that is above the screen</summary>
        private static string ReadRallyLine(Mat image, int top, int width, string? whitelist) =>
            top < 0 ? "" : MaskedTextOcr.Read(image, new Rectangle(285, top, width, 44), DarkLow, RallyDarkHigh, whitelist);

        /// <summary>
        /// A rally with less room than this is skipped: the join would send a few troops instead of our ~125,500
        /// (2026-11-10: joined with ~100 troops to a 1,775,230/1,802,710 rally)
        /// </summary>
        internal const long MinFreeTroops = 100_000;

        /// <summary>The row's troop line ("176,610/1,481,610", dark text) under the leader name, ~70 px above its "+"</summary>
        internal static string ReadRallyTroops(Mat image, int plusY) => ReadRallyLine(image, plusY - 92, 300, "0123456789,./");

        internal static string ReadRallyTroops(byte[] screenshot, int plusY)
        {
            using var image = Cv2.ImDecode(screenshot, ImreadModes.Color);
            return ReadRallyTroops(image, plusY);
        }

        /// <summary>Max minus current of "176,610/1,481,610"; null when it can't be read (then the row is joined)</summary>
        internal static long? FreeTroops(string text)
        {
            var m = System.Text.RegularExpressions.Regex.Match(text, @"([\d,.]+)\s*/\s*([\d,.]+)", System.Text.RegularExpressions.RegexOptions.None, RegexTimeouts.Default);
            if (!m.Success) return null;
            static long? Number(string s) => long.TryParse(s.Replace(",", "").Replace(".", ""), out var n) ? n : null;
            long? current = Number(m.Groups[1].Value), max = Number(m.Groups[2].Value);
            return current is long c && max is long x && x >= c ? x - c : null;
        }

        private static void MarkFull(string character)
        {
            BearHuntStatus.MarkFull(character, DateTime.UtcNow + FullRetry);
            BearHuntStatus.Set(character, BearHuntPhase.QueuesFull);
        }

        #endregion

        #region Schedule

        // The Task List is the same for every character of the instance: one gate per instance, not per game account
        private static string GateKey(AccountSettings account) => $"BearHunt_instance{account.InstanceNumber}";

        public DateTime? GetNextRunUtc(string accountKey, AccountSettings account) => DailyGate.NextRunUtc(TaskType, GateKey(account));

        /// <summary>The ▶ button reads the list again (the dashboard passes the account key; the gate is per instance)</summary>
        public void ClearSchedule(string accountKey) => _forceRead = true;

        private bool _forceRead;

        /// <summary>Once a day after the 00:00 UTC reset: the next bear of our trap from the Task List</summary>
        private async Task<TaskExecutionDetails> ReadScheduleOnceADayAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken)
        {
            var settings = account.BearHuntSettings;
            string key = GateKey(account);
            if (!_forceRead && DailyGate.DoneToday(TaskType, key))
                return TaskExecutionDetails.SkippedBecause($"Already read today; next bear {(settings.NextBearUtc != null ? settings.NextBearUtcText + " UTC" : "unknown")}");
            _forceRead = false;

            int instance = account.InstanceNumber;
            var locator = new LocatorService(logger, account, TaskType);
            await locator.EnsureViewAsync(ViewType.MapView, instance, cancellationToken);
            var items = await ReadTaskListAsync(instance, logger, cancellationToken);
            await locator.EnsureViewAsync(ViewType.MapView, instance, cancellationToken);
            if (items == null) return TaskExecutionDetails.Failed("The Task List did not open");

            DailyGate.MarkDone(TaskType, key);
            var next = BearHuntSchedule.NextBear(items, settings.Trap, DateTime.UtcNow, settings.EventLength);
            if (next == null)
            {
                logger.LogInfo($"Bear Hunt: no Trap {settings.Trap} bear in the Task List ({items.Count} rows read)");
                return TaskExecutionDetails.Succeeded($"No Trap {settings.Trap} bear listed");
            }
            settings.NextBearUtc = next;
            logger.LogInfo($"Bear Hunt: next Trap {settings.Trap} bear at {next:yyyy-MM-dd HH:mm} UTC ({next.Value.ToLocalTime():HH:mm} local)");
            return TaskExecutionDetails.Succeeded($"Next bear {next:MM-dd HH:mm} UTC");
        }

        /// <summary>UTC banner > Task List, read while scrolling down slowly; null when it didn't open</summary>
        private async Task<List<BearHuntSchedule.TaskListItem>?> ReadTaskListAsync(int instance, LogService logger, CancellationToken cancellationToken)
        {
            await ClickAsync(instance, logger, UtcBannerPoint);
            await Task.Delay(2000, cancellationToken);
            if (!(await MatchAsync(TaskListTitleTemplate, TaskListTitleArea, Threshold, instance, logger)).found) return null;

            var items = new List<BearHuntSchedule.TaskListItem>();
            for (int i = 0; i <= MaxListScrolls; i++)
            {
                if (i > 0)
                {
                    await SwipeAsync(instance, logger, ListDragFrom, ListDragTo, 1500);
                    await Task.Delay(1200, cancellationToken);
                }
                var screenshot = await TakeScreenshotAsync(instance, logger);
                if (screenshot != null) items.AddRange(ReadTaskList(screenshot));
            }
            await ClickAsync(instance, logger, TaskListClosePoint);
            await Task.Delay(1200, cancellationToken);
            return items;
        }

        /// <summary>The headers and rows fully on screen, top to bottom</summary>
        internal static List<BearHuntSchedule.TaskListItem> ReadTaskList(byte[] screenshot)
        {
            var items = new List<BearHuntSchedule.TaskListItem>();
            using var image = Cv2.ImDecode(screenshot, ImreadModes.Color);
            foreach (var (header, top, bottom) in FindListBands(image))
            {
                int h = bottom - top;
                if (header)
                {
                    // white text on the tan (date) or orange ("Today") bar
                    items.Add(new(true, MaskedTextOcr.Read(image, new Rectangle(150, top, 420, h), HeaderWhiteLow, HeaderWhiteHigh, null)));
                    continue;
                }
                // dark brown text on beige: the title, and "UTC / 13:00" at the right
                string title = MaskedTextOcr.Read(image, new Rectangle(160, top, 400, h), DarkLow, ListDarkHigh, null, Tesseract.PageSegMode.SingleBlock);
                string time = MaskedTextOcr.Read(image, new Rectangle(560, top + 8, 125, h - 16), DarkLow, ListDarkHigh, "UTC0123456789:", Tesseract.PageSegMode.SingleBlock);
                items.Add(new(false, title, time));
            }
            return items;
        }

        /// <summary>
        /// Rows and headers of the Task List by the colour at x=675: background sat ~25 (V 232), row ~35 (V 221),
        /// date header ~74, "Today" header ~130. Bands cut off by the list's top or bottom edge are left out.
        /// </summary>
        internal static List<(bool Header, int Top, int Bottom)> FindListBands(byte[] screenshot)
        {
            using var image = Cv2.ImDecode(screenshot, ImreadModes.Color);
            return FindListBands(image);
        }

        private static List<(bool Header, int Top, int Bottom)> FindListBands(Mat image)
        {
            var bands = new List<(bool, int, int)>();
            using var hsv = image.CvtColor(ColorConversionCodes.BGR2HSV);
            int runStart = TaskListTop, runKind = -1;
            for (int y = TaskListTop; y <= TaskListBottom; y++)
            {
                int kind = y == TaskListBottom ? -2 : BandKind(hsv.At<Vec3b>(y, 675));
                if (kind == runKind) continue;
                if (runKind >= 0 && runStart > TaskListTop && y < TaskListBottom && y - runStart >= (runKind == 1 ? 30 : 90))
                    bands.Add((runKind == 1, runStart, y));
                runKind = kind;
                runStart = y;
            }
            return bands;
        }

        /// <summary>1 = header, 0 = row, -1 = background</summary>
        private static int BandKind(Vec3b px)
        {
            if (px.Item1 >= 55) return 1;
            return px.Item1 >= 30 && px.Item2 < 228 ? 0 : -1;
        }

        #endregion

        /// <summary>Calibration material for the steps not built yet: logs/screenshots/bearhunt/&lt;step&gt;-HHmmss.png next to the exe</summary>
        private Task SaveStepScreenshotAsync(string step, int instance, LogService logger) =>
            SaveDebugScreenshotAsync(instance, logger, $"{step}-{DateTime.Now:HHmmss}.png", Path.Combine("screenshots", "bearhunt"));
    }
}
