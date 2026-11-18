using Bot.Core.Exceptions;
using Bot.Core.Logging;
using Bot.Core.Models;
using Bot.Core.Services;
using Bot.Core.Utils;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// One pass of the Viking Vengeance mode on the active character (<see cref="VikingVengeanceMode"/>).
    /// Send: recall the marches, wait until they are home, then Alliance > Members: every online member (the R5 card and
    /// the R4..R1 groups, opened one at a time) > Squad Help > Reinforce (only with enough capacity free) > Confirm >
    /// team flag > Deploy, until no march is free. Recall: call the troops home after the event.
    /// The member popup has a red Remove (kick) next to Squad Help: only the matched Squad Help is ever tapped.
    /// </summary>
    public class VikingVengeanceTask : RallyTaskBase
    {
        public override TaskType TaskType => TaskType.VikingVengeance;
        public override string Name => "Viking Vengeance";

        // like the Bear Hunt passes: no Alliance Help / Auto Heal side trips between the steps
        protected override bool SkipAutoTasks => VikingStatus.IsRunning;

        internal const string OnlineTemplate = "online.png";               // green "Online" under a member's avatar: 0.97-1.00, <= 0.66 elsewhere
        private const string SquadHelpTemplate = "squad-help.png";         // member popup button: 0.99, <= 0.43 elsewhere
        private const string ReinforceTitleTemplate = "reinforce-title.png"; // "Reinforce" popup: 1.00, <= 0.47 elsewhere
        private const string MembersTitleTemplate = "members-title.png";   // "Alliance Members" title: 1.00, <= 0.62 elsewhere
        private const double OnlineThreshold = 0.85;
        private const double SquadHelpThreshold = 0.90;

        private static readonly System.Drawing.Point MembersButtonPoint = new(197, 1215);   // Alliance screen, bottom left
        private static readonly Rectangle MembersTitleArea = new(60, 0, 360, 80);
        private static readonly Rectangle LeaderOnlineArea = new(40, 385, 230, 50);       // under the R5 card's avatar
        // rank group headers while all are collapsed (R4, R3, R2, R1); each is opened, scanned and closed again
        private static readonly int[] GroupHeaderY = { 500, 571, 640, 710 };
        private const int ListBottom = 1260;
        private static readonly Rectangle SquadHelpArea = new(0, 150, 720, 1050);       // the popup is anchored to the card
        private static readonly Rectangle ReinforceTitleArea = new(200, 330, 320, 80);
        internal static readonly Rectangle CapacityArea = new(195, 543, 395, 32);       // "184,710/1,040,000" in the bar
        private static readonly System.Drawing.Point ConfirmPoint = new(509, 742);
        private static readonly System.Drawing.Point CancelPoint = new(210, 742);
        // from the Online label's top left: the card's name line and a tap on the card
        private const int NameDx = 88, NameDy = -82, NameWidth = 200, NameHeight = 34;
        private const int CardTapDx = 188, CardTapDy = -32;
        private const int MaxPerGroup = 10;
        private static readonly TimeSpan MarchesHomeTimeout = TimeSpan.FromMinutes(6);

        private enum HelpResult { Skipped, Sent, NoMarch, Lost }

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            if (!VikingStatus.IsRunning) return TaskExecutionDetails.SkippedBecause("Runs in the Viking Vengeance mode only");
            int instance = account.InstanceNumber;
            string character = account.Characters.Count == 0 ? account.AccountName : CharacterRoster.GetActiveName(instance) ?? account.AccountName;
            try
            {
                var locator = new LocatorService(logger, account, TaskType);
                await locator.EnsureViewAsync(ViewType.MapView, instance, cancellationToken);
                string recalled = await RecallMarchesAsync(character, instance, logger, cancellationToken);
                if (VikingStatus.Phase == VikingPhase.Recall)
                {
                    VikingStatus.SetState(character, recalled);
                    return TaskExecutionDetails.Succeeded(recalled);
                }

                VikingStatus.SetState(character, "waiting for the troops");
                if (!await WaitForMarchesHomeAsync(MarchesHomeTimeout, instance, logger, cancellationToken))
                    logger.LogWarning($"[{character}] Viking Vengeance: marches still out after {MarchesHomeTimeout.TotalMinutes:F0} min, reinforcing with what is home");

                int sent = await ReinforceOnlineMembersAsync(account, character, instance, logger, cancellationToken);
                await locator.EnsureViewAsync(ViewType.MapView, instance, cancellationToken);
                string result = $"{recalled}; reinforced {sent} member(s)";
                VikingStatus.SetState(character, $"reinforced {sent} member(s)");
                logger.LogInfo($"[{character}] Viking Vengeance: {result}");
                return TaskExecutionDetails.Succeeded(result);
            }
            catch (OperationCanceledException)
            {
                return TaskExecutionDetails.Failed("Task was cancelled");
            }
            catch (Exception ex) when (ex is not BotLostException)
            {
                logger.LogError($"[{character}] Viking Vengeance: {ex.Message}");
                return TaskExecutionDetails.Failed(ex.Message);
            }
        }

        /// <summary>Alliance > Members, then the R5 card and each rank group; the number of reinforcements sent</summary>
        private async Task<int> ReinforceOnlineMembersAsync(AccountSettings account, string character, int instance, LogService logger, CancellationToken cancellationToken)
        {
            if (!await OpenMembersAsync(instance, logger, cancellationToken))
            {
                logger.LogWarning($"[{character}] Viking Vengeance: Alliance Members did not open");
                return 0;
            }

            int sent = 0;
            var leader = await MatchAsync(OnlineTemplate, LeaderOnlineArea, OnlineThreshold, instance, logger);
            var result = HelpResult.Skipped;
            if (leader.found)
            {
                result = await HelpMemberAsync(account, character, leader.rect, "R5", instance, logger, cancellationToken);
                if (result == HelpResult.Sent) sent++;
            }

            for (int g = 0; g < GroupHeaderY.Length && result is HelpResult.Skipped or HelpResult.Sent; g++)
            {
                var (groupSent, groupResult) = await HelpGroupAsync(account, character, g, instance, logger, cancellationToken);
                sent += groupSent;
                result = groupResult;
            }

            if (result == HelpResult.NoMarch) logger.LogInfo($"[{character}] Viking Vengeance: no free march left");
            await LeaveMembersAsync(instance, logger, cancellationToken);
            return sent;
        }

        /// <summary>Opens rank group <paramref name="group"/>, helps its online members (listed first), closes it again</summary>
        private async Task<(int sent, HelpResult last)> HelpGroupAsync(AccountSettings account, string character, int group, int instance, LogService logger, CancellationToken cancellationToken)
        {
            int headerY = GroupHeaderY[group];
            await ClickAsync(instance, logger, new System.Drawing.Point(360, headerY));
            await Task.Delay(1500, cancellationToken);

            int sent = 0;
            var last = HelpResult.Skipped;
            var screenshot = await TakeScreenshotAsync(instance, logger);
            var labels = screenshot == null ? new List<Rectangle>() : FindOnlineLabels(screenshot, headerY + 40);
            foreach (var label in labels.Take(MaxPerGroup))
            {
                await WaitIfPausedAsync(cancellationToken);
                last = await HelpMemberAsync(account, character, label, $"R{4 - group}", instance, logger, cancellationToken);
                if (last == HelpResult.Sent) sent++;
                if (last is HelpResult.NoMarch or HelpResult.Lost) break;
            }

            if (last != HelpResult.Lost)
            {
                await ClickAsync(instance, logger, new System.Drawing.Point(360, headerY));
                await Task.Delay(1000, cancellationToken);
            }
            return (sent, last == HelpResult.Sent ? HelpResult.Skipped : last);
        }

        /// <summary>One online member: card > Squad Help > Reinforce (enough free?) > Confirm > flag > Deploy</summary>
        private async Task<HelpResult> HelpMemberAsync(AccountSettings account, string character, Rectangle label, string group, int instance, LogService logger, CancellationToken cancellationToken)
        {
            var screenshot = await TakeScreenshotAsync(instance, logger);
            string name = screenshot == null ? "" : ReadMemberName(screenshot, label);
            string key = name.Length > 0 ? name : $"{group}@{label.Y}";
            if (IsOwnCharacter(account, name) || VikingStatus.WasHelped(character, key)) return HelpResult.Skipped;

            await ClickAsync(instance, logger, new System.Drawing.Point(label.X + CardTapDx, label.Y + CardTapDy));
            await Task.Delay(1500, cancellationToken);
            var squad = await MatchAsync(SquadHelpTemplate, SquadHelpArea, SquadHelpThreshold, instance, logger);
            if (!squad.found) return HelpResult.Skipped; // our own card opens no popup

            await ClickCenterInRectAsync(instance, logger, squad.rect);
            await Task.Delay(2000, cancellationToken);
            if (!(await MatchAsync(ReinforceTitleTemplate, ReinforceTitleArea, Threshold, instance, logger)).found)
            {
                // with every march out, Squad Help opens no Reinforce popup (2026-11-18, Asli after 6 reinforcements)
                if (await IsOnMembersAsync(instance, logger)) return HelpResult.NoMarch;
                logger.LogWarning($"[{character}] Viking Vengeance: no Reinforce popup for '{key}'");
                return HelpResult.Lost;
            }

            var settings = account.VikingVengeanceSettings;
            long? free = await FreeCapacityAsync(character, key, settings.MinFreeTroops, instance, logger, cancellationToken);
            if (free == null) return HelpResult.Skipped;

            await ClickAsync(instance, logger, ConfirmPoint);
            await Task.Delay(2500, cancellationToken);
            return await DeployToMemberAsync(character, key, free.Value, settings.Team, instance, logger, cancellationToken);
        }

        /// <summary>The Reinforce popup's free capacity when it is enough; otherwise Cancel (the member counts as done) and null</summary>
        private async Task<long?> FreeCapacityAsync(string character, string key, int minFree, int instance, LogService logger, CancellationToken cancellationToken)
        {
            var screenshot = await TakeScreenshotAsync(instance, logger);
            string capacity = screenshot == null ? "" : ReadCapacity(screenshot);
            long? free = BearHuntTask.FreeTroops(capacity);
            if (free >= minFree) return free;

            logger.LogInfo($"[{character}] Viking Vengeance: '{key}' has {(free?.ToString("N0") ?? $"? ('{capacity}')")} free, needs {minFree:N0}");
            await ClickAsync(instance, logger, CancelPoint);
            await Task.Delay(1200, cancellationToken);
            VikingStatus.MarkHelped(character, key);
            return null;
        }

        private async Task<HelpResult> DeployToMemberAsync(string character, string key, long free, int team, int instance, LogService logger, CancellationToken cancellationToken)
        {
            if (!(await MatchAsync(DeployButtonTemplate, DeployButtonArea, Threshold, instance, logger)).found)
            {
                logger.LogWarning($"[{character}] Viking Vengeance: no deploy screen for '{key}' (no free march?)");
                return await IsOnMembersAsync(instance, logger) ? HelpResult.NoMarch : HelpResult.Lost;
            }
            if (await DeployWithFlagAsync(team, instance, logger, cancellationToken))
            {
                VikingStatus.MarkHelped(character, key);
                logger.LogInfo($"[{character}] Viking Vengeance: reinforced '{key}' with flag {team} ({free:N0} free)");
                return HelpResult.Sent;
            }
            // Deploy stayed open: no march free
            await ClickAsync(instance, logger, BackPoint);
            await Task.Delay(1500, cancellationToken);
            return await IsOnMembersAsync(instance, logger) ? HelpResult.NoMarch : HelpResult.Lost;
        }

        private static bool IsOwnCharacter(AccountSettings account, string name) =>
            name.Length > 0 && account.Characters.Any(c => CharacterRoster.SameName(c.Name, name) || CharacterRoster.EndsWithName(name, c.Name));

        private async Task<bool> OpenMembersAsync(int instance, LogService logger, CancellationToken cancellationToken)
        {
            var menu = await MatchAsync(AllianceMenuTemplate, AllianceMenuArea, Threshold, instance, logger);
            if (!menu.found) return false;
            await ClickCenterInRectAsync(instance, logger, menu.rect);
            await Task.Delay(2500, cancellationToken);
            await ClickAsync(instance, logger, MembersButtonPoint);
            return await WaitForAsync(() => IsOnMembersAsync(instance, logger), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        private async Task<bool> IsOnMembersAsync(int instance, LogService logger) =>
            (await MatchAsync(MembersTitleTemplate, MembersTitleArea, Threshold, instance, logger)).found;

        /// <summary>Members > Alliance > map: two back taps (the locator finishes the way home)</summary>
        private async Task LeaveMembersAsync(int instance, LogService logger, CancellationToken cancellationToken)
        {
            for (int i = 0; i < 2; i++)
            {
                await ClickAsync(instance, logger, BackPoint);
                await Task.Delay(1500, cancellationToken);
            }
        }

        /// <summary>Top left corners of the green "Online" labels below <paramref name="top"/>, top to bottom, left before right</summary>
        internal List<Rectangle> FindOnlineLabels(byte[] screenshot, int top)
        {
            var path = Path.Combine(ImageTemplateFolder, OnlineTemplate);
            using var image = Cv2.ImDecode(screenshot, ImreadModes.Grayscale);
            using var template = Cv2.ImRead(path, ImreadModes.Grayscale);
            var labels = new List<Rectangle>();
            if (template.Empty() || top + template.Height >= ListBottom) return labels;
            var area = new Rect(0, top, image.Width, Math.Min(ListBottom, image.Height) - top);
            using var region = new Mat(image, area);
            using var result = new Mat();
            Cv2.MatchTemplate(region, template, result, TemplateMatchModes.CCoeffNormed);
            while (true)
            {
                Cv2.MinMaxLoc(result, out _, out double max, out _, out var loc);
                if (max < OnlineThreshold) break;
                labels.Add(new Rectangle(loc.X, loc.Y + top, template.Width, template.Height));
                // blank out this match's neighbourhood
                Cv2.Rectangle(result, new Rect(loc.X - template.Width, loc.Y - template.Height, template.Width * 2, template.Height * 2), Scalar.All(-1), -1);
            }
            return labels.OrderBy(r => r.Y / 40).ThenBy(r => r.X).ToList();
        }

        /// <summary>The member's name on the card (white on teal); "" when unreadable</summary>
        internal static string ReadMemberName(byte[] screenshot, Rectangle label) =>
            MaskedTextOcr.Read(screenshot, new Rectangle(label.X + NameDx, Math.Max(0, label.Y + NameDy), NameWidth, NameHeight),
                new Scalar(0, 0, 200), new Scalar(180, 60, 255), null);

        /// <summary>The Reinforce popup's "current/max" capacity line</summary>
        internal static string ReadCapacity(byte[] screenshot) =>
            MaskedTextOcr.Read(screenshot, CapacityArea, new Scalar(0, 0, 200), new Scalar(180, 60, 255), "0123456789,./");
    }
}
