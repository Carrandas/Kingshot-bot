using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.ImageDetection;
using Bot.Core.Services;
using Bot.Core.Exceptions;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// One pass of the castle battle for the active character (the Bot tab's Castle Battle mode repeats it on every
    /// ticked character until Stop):
    /// 1. heal: Heal icon > Heal > Help, repeated until the icon is gone;
    /// 2. read the Marching tracker (top left of the map) for the character's state: rallying, marching, garrisoned, back;
    /// 3. Alliance > War > Rally: join every rally with a green "+" (grey = already in or full) with the offensive team
    ///    flag (red "Rally") or the defensive one (blue "Def. Lineup"); each flag keeps its own troops and ratio.
    /// Not in the cycle's task list.
    /// </summary>
    public class CastleBattleTask : RallyTaskBase
    {
        public override TaskType TaskType => TaskType.CastleBattle;
        public override string Name => "Castle Battle";

        // This folder
        private const string HealTitleTemplate = "heal-title.png";         // "Heal Injured": 1.00, <= 0.31 elsewhere
        private const string HealButtonHealTemplate = "heal-btn-heal.png";
        private const string HealButtonHelpTemplate = "heal-btn-help.png";
        private const string HealButtonSpeedupsTemplate = "heal-btn-speedups.png";
        private const string HealCancelTitleTemplate = "heal-cancel-title.png";  // "Cancel healing" confirmation: 1.00, <= 0.38 elsewhere
        private const string BattleHealIconTemplate = "heal-icon-battle.png"; // the battle's heal icon (handshake + green "+"): 1.00, <= 0.41 elsewhere
        private static readonly string HealIconTemplate = Path.Combine("autoheal", "heal.png");

        private static readonly Rectangle HealIconArea = new Rectangle(480, 880, 238, 200);
        private static readonly Rectangle HealTitleArea = new Rectangle(200, 160, 320, 90);
        // right button of Heal Injured, Heal or Help in turn; Finish (gems) is left of it at (308,937)
        private static readonly Rectangle HealHelpButtonArea = new Rectangle(425, 898, 185, 75);
        private static readonly System.Drawing.Point HealHelpPoint = new System.Drawing.Point(517, 937);
        private static readonly System.Drawing.Point HealClosePoint = new System.Drawing.Point(597, 198);
        // Heal Injured rows: Truegold Infantry, Cavalry, Archers (amount box, and the "name count" title)
        private static readonly System.Drawing.Point[] HealBoxes =
        {
            new System.Drawing.Point(541, 405), new System.Drawing.Point(541, 548), new System.Drawing.Point(541, 690)
        };
        private static readonly Rectangle[] HealRowTitleAreas =
        {
            new Rectangle(228, 326, 300, 34), new Rectangle(228, 470, 300, 34), new Rectangle(228, 613, 300, 34)
        };
        private static readonly System.Drawing.Point NumberOkPoint = new System.Drawing.Point(640, 1217);      // OK of the input bar
        private static readonly System.Drawing.Point CancelHealingPoint = new System.Drawing.Point(135, 937);
        private static readonly System.Drawing.Point CancelConfirmPoint = new System.Drawing.Point(512, 789);
        private static readonly Rectangle HealCancelTitleArea = new Rectangle(200, 400, 320, 100);

        private static readonly Rectangle HealPercentArea = new Rectangle(512, 978, 92, 26);   // under the heal icon
        private const int HealBlocksJoinPercent = 70;   // at 70% or more: only heal, no rallies
        private const double HealThreshold = 0.65;  // as Auto Heal
        private const int MaxJoinsPerPass = 1;      // the game allows one rally at a time (a second join gives an error)
        private const int HealStep = 100;             // a batch the help no longer covers (allies went offline): 100 fewer
        private const int MinHealAmount = 100;
        private const int MaxHealRounds = 50;         // Heal / Help taps per pass, then rallies get their turn (18 allies online covered ~1,000 a batch)

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            int instance = account.InstanceNumber;
            string character = CharacterRoster.GetActiveName(instance) ?? account.AccountName;
            var settings = account.CastleBattleSettings;
            if (!settings.IsValid) return TaskExecutionDetails.Failed("Castle Battle settings: the teams must be flags 1-8");

            try
            {
                var locator = new LocatorService(logger, account, TaskType);
                await locator.EnsureViewAsync(ViewType.MapView, instance, cancellationToken);

                // rallies first while the infirmary has room (heal icon below HealBlocksJoinPercent): they leave on a timer;
                // at or above it only heal, since a full infirmary loses troops
                int? healPercent = await ReadHealPercentAsync(instance, logger);
                var march = await ReadMarchesAsync(character, instance, logger);
                int joined = 0;
                if (healPercent >= HealBlocksJoinPercent)
                {
                    logger.LogInfo($"[{character}] Castle Battle: heal icon at {healPercent}%, healing before joining");
                    CastleBattleStatus.Set(character, CastleBattlePhase.Healing, $"{healPercent}% injured");
                }
                else
                {
                    joined = await JoinRalliesAsync(account, character, settings, march, logger, cancellationToken);
                    await locator.EnsureViewAsync(ViewType.MapView, instance, cancellationToken);
                }

                int healed = await HealAsync(character, settings, instance, logger, cancellationToken);
                await locator.EnsureViewAsync(ViewType.MapView, instance, cancellationToken);
                if (joined > 0) CastleBattleStatus.Set(character, CastleBattlePhase.JoinedWaitingToMarch, $"joined {joined}");
                else ReportMarchState(character, await ReadMarchesAsync(character, instance, logger));

                return TaskExecutionDetails.Succeeded($"healed {healed} rounds, joined {joined} rallies");
            }
            catch (OperationCanceledException)
            {
                return TaskExecutionDetails.Failed("Task was cancelled");
            }
            catch (Exception ex) when (ex is not BotLostException)
            {
                logger.LogError($"[{character}] Castle Battle: {ex.Message}");
                return TaskExecutionDetails.Failed(ex.Message);
            }
        }

        #region Heal

        private enum HealButton { None, Heal, Help, Speedups }

        /// <summary>
        /// Heal icon > "Heal Injured" popup, then up to 50 rounds of Heal > Help, starting at the saved batch + 100 (Castle Battle tab).
        /// Help heals the batch at once while the allies' auto-help covers it ("900 Soldiers healed!", the button turns
        /// back to Heal). When it no longer does (the button turns into Speedups: allies went offline), Cancel healing >
        /// Confirm and 100 fewer from then on. Speedups and the gold Finish (gems) are never tapped. Returns the batches healed.
        /// </summary>
        private async Task<int> HealAsync(string character, CastleBattleSettings settings, int instance, LogService logger, CancellationToken cancellationToken)
        {
            if (!await OpenHealPopupAsync(character, instance, logger, cancellationToken)) return 0;
            CastleBattleStatus.Set(character, CastleBattlePhase.Healing);

            // each pass first tries 100 more than last time; a batch the help doesn't cover drops it by 100 again
            int amount = Math.Max(MinHealAmount, settings.HealAmountFor(character) + HealStep);
            int healed = 0;
            (int Type, int Value)? typed = null;
            for (int round = 0; round < MaxHealRounds; round++)
            {
                var button = await ReadHealButtonAsync(instance, logger);
                if (button == HealButton.None) break;
                if (button == HealButton.Heal)
                {
                    typed = await StartHealAsync(amount, typed, instance, logger, cancellationToken);
                    continue;
                }
                // Speedups already showing: healing from before this pass that the help didn't cover
                bool covered = button == HealButton.Help && await HelpCoveredAsync(character, amount, instance, logger, cancellationToken);
                if (covered)
                {
                    healed++;
                    CastleBattleStatus.CountHeal(character);
                    continue;
                }
                typed = null;   // Cancel healing refills the boxes: type them again
                if (!await CancelHealingAsync(instance, logger, cancellationToken)) continue;   // healing finished after all
                amount = Math.Max(MinHealAmount, amount - HealStep);
                logger.LogInfo($"[{character}] Castle Battle: heal amount lowered to {amount}");
            }

            // only an amount that healed (or one lowered after a failure) is kept: an untried +100 is not
            if (healed > 0 || amount < settings.HealAmountFor(character)) settings.SetHealAmountFor(character, amount);
            logger.LogInfo($"[{character}] Castle Battle: healed {healed} batches of {amount}");
            await CloseHealPopupAsync(instance, logger, cancellationToken);
            return healed;
        }

        /// <summary>
        /// Puts the batch on one troop type (infantry, cavalry or archers; 0 in the other boxes): the one typed last while
        /// it has a full batch left, else the one with the most injured. Types only when that changed, then Heal. Returns what is in the boxes now.
        /// </summary>
        private async Task<(int Type, int Value)?> StartHealAsync(int amount, (int Type, int Value)? typed, int instance, LogService logger, CancellationToken cancellationToken)
        {
            var screenshot = await TakeScreenshotAsync(instance, logger);
            var injured = screenshot != null ? ReadInjured(screenshot, logger) : new int?[3];
            var want = PickHealTarget(injured, amount, typed);
            if (typed != want) await TypeHealAmountAsync(want.Type, want.Value, injured, instance, logger, cancellationToken);
            await ClickAsync(instance, logger, HealHelpPoint);
            await Task.Delay(700, cancellationToken);
            return want;
        }

        /// <summary>
        /// The box and amount for the next batch: the type typed last while it has a full batch left (retyping three boxes
        /// costs ~15 s), else the one with the most injured; a type with fewer injured than the batch gets what is left.
        /// </summary>
        internal static (int Type, int Value) PickHealTarget(int?[] injured, int amount, (int Type, int Value)? typed)
        {
            int type = typed is { } last && (injured[last.Type] ?? amount) >= amount ? last.Type : Array.IndexOf(injured, injured.Max());
            if (type < 0 || injured[type] == null) type = typed?.Type ?? 0;   // counts didn't read: keep the last type
            int value = injured[type] is int count && count > 0 ? Math.Min(amount, count) : amount;
            return (type, value);
        }

        /// <summary>Injured infantry, cavalry and archers from the popup rows ("Truegold Infantry 38,790"); null where unreadable</summary>
        internal static int?[] ReadInjured(byte[] screenshot, LogService logger)
        {
            var counts = new int?[HealRowTitleAreas.Length];
            for (int i = 0; i < counts.Length; i++)
            {
                string text = ReadMaskedText(screenshot, HealRowTitleAreas[i], new Scalar(0, 0, 0), new Scalar(180, 255, 110), null, logger);
                var m = Regex.Match(text, @"(\d[\d,.]*)\s*$", RegexOptions.None, RegexTimeouts.Default);
                if (m.Success && int.TryParse(m.Groups[1].Value.Replace(",", "").Replace(".", ""), out int n)) counts[i] = n;
            }
            return counts;
        }

        /// <summary>Help, then: did the allies' help cover the batch (button back to Heal) or not (Speedups)?</summary>
        private async Task<bool> HelpCoveredAsync(string character, int amount, int instance, LogService logger, CancellationToken cancellationToken)
        {
            await ClickAsync(instance, logger, HealHelpPoint);
            // the allies' helps arrive over a second or two: only Speedups that stays is a batch too big
            for (int i = 0; i < 5; i++)
            {
                await Task.Delay(700, cancellationToken);
                if (await ReadHealButtonAsync(instance, logger) != HealButton.Speedups) return true;
            }
            logger.LogInfo($"[{character}] Castle Battle: {amount} troops is too many to heal for free");
            return false;
        }

        private async Task<bool> OpenHealPopupAsync(string character, int instance, LogService logger, CancellationToken cancellationToken)
        {
            var icon = await FindHealIconAsync(instance, logger);
            if (icon.found) await ClickCenterInRectAsync(instance, logger, icon.rect);
            else return false;
            await Task.Delay(1500, cancellationToken);
            if ((await MatchAsync(HealTitleTemplate, HealTitleArea, Threshold, instance, logger)).found) return true;
            logger.LogWarning($"[{character}] Castle Battle: the heal icon did not open Heal Injured");
            return false;
        }

        /// <summary>The battle or the normal heal icon; looks a few times, as the icon can change while it animates</summary>
        private async Task<(bool found, Rectangle rect)> FindHealIconAsync(int instance, LogService logger)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0) await Task.Delay(700);
                var icon = await MatchAsync(BattleHealIconTemplate, HealIconArea, Threshold, instance, logger);
                if (!icon.found) icon = await MatchAsync(HealIconTemplate, HealIconArea, HealThreshold, instance, logger);
                if (icon.found) return (true, icon.rect);
            }
            return (false, Rectangle.Empty);
        }

        /// <summary>The right button of the open Heal Injured popup; None when the popup is gone or the button is none of these</summary>
        private async Task<HealButton> ReadHealButtonAsync(int instance, LogService logger)
        {
            if (!(await MatchAsync(HealTitleTemplate, HealTitleArea, Threshold, instance, logger)).found) return HealButton.None;
            // button texts: 0.99 on their own button, <= 0.41 on the others
            if ((await MatchAsync(HealButtonSpeedupsTemplate, HealHelpButtonArea, Threshold, instance, logger)).found) return HealButton.Speedups;
            if ((await MatchAsync(HealButtonHelpTemplate, HealHelpButtonArea, Threshold, instance, logger)).found) return HealButton.Help;
            if ((await MatchAsync(HealButtonHealTemplate, HealHelpButtonArea, Threshold, instance, logger)).found) return HealButton.Heal;
            return HealButton.None;
        }

        /// <summary>The batch in one box (0 = infantry, 1 = cavalry, 2 = archers), 0 in the other two</summary>
        private async Task TypeHealAmountAsync(int type, int amount, int?[] injured, int instance, LogService logger, CancellationToken cancellationToken)
        {
            // the popup (and Cancel healing) can fill boxes to their max: zero the others first, so the total never jumps.
            // A type with no injured has no row (the rows below move up): skip rows without a count, or the input bar's
            // OK lands on the bottom bar's Town and closes the popup. No count read at all: zero all three as before.
            bool anyRead = injured.Any(n => n != null);
            for (int i = 0; i < HealBoxes.Length; i++)
            {
                if (i != type && (!anyRead || injured[i] != null)) await TypeIntoBoxAsync(HealBoxes[i], 0, instance, logger, cancellationToken);
            }
            await TypeIntoBoxAsync(HealBoxes[type], amount, instance, logger, cancellationToken);
        }

        private async Task TypeIntoBoxAsync(System.Drawing.Point box, int value, int instance, LogService logger, CancellationToken cancellationToken)
        {
            await ClickAsync(instance, logger, box);
            await Task.Delay(1200, cancellationToken);
            await TypeIntoFocusedFieldAsync(instance, logger, value.ToString());
            await Task.Delay(800, cancellationToken);
            await ClickAsync(instance, logger, NumberOkPoint);
            await Task.Delay(1000, cancellationToken);
        }

        /// <summary>Cancel healing > Confirm (refunds the resources). False when the confirmation didn't show.</summary>
        private async Task<bool> CancelHealingAsync(int instance, LogService logger, CancellationToken cancellationToken)
        {
            // that spot is Quick Select (fills every box to its max) once healing is done: tap it only while healing runs
            if (await ReadHealButtonAsync(instance, logger) != HealButton.Speedups) return false;
            await ClickAsync(instance, logger, CancelHealingPoint);
            bool shown = false;
            for (int i = 0; i < 6 && !shown; i++)
            {
                await Task.Delay(500, cancellationToken);
                shown = (await MatchAsync(HealCancelTitleTemplate, HealCancelTitleArea, Threshold, instance, logger)).found;
            }
            if (!shown)
            {
                logger.LogWarning("Castle Battle: no Cancel healing confirmation");
                return false;
            }
            await ClickAsync(instance, logger, CancelConfirmPoint);
            await Task.Delay(1200, cancellationToken);
            return true;
        }

        private async Task CloseHealPopupAsync(int instance, LogService logger, CancellationToken cancellationToken)
        {
            for (int i = 0; i < 3 && (await MatchAsync(HealTitleTemplate, HealTitleArea, Threshold, instance, logger)).found; i++)
            {
                await ClickAsync(instance, logger, HealClosePoint);
                await Task.Delay(1000, cancellationToken);
            }
        }

        internal static int CountTeal(byte[] screenshot, Rectangle area)
        {
            using var image = Cv2.ImDecode(screenshot, ImreadModes.Color);
            using var region = new Mat(image, new Rect(area.X, area.Y, area.Width, area.Height));
            using var hsv = region.CvtColor(ColorConversionCodes.BGR2HSV);
            using var mask = hsv.InRange(new Scalar(80, 120, 150), new Scalar(100, 255, 255));
            return Cv2.CountNonZero(mask);
        }

        #endregion

        #region March state

        private async Task<MarchInfo> ReadMarchesAsync(string character, int instance, LogService logger)
        {
            var info = await ReadMarchTrackerAsync(instance, logger);
            logger.LogInfo($"[{character}] Castle Battle: marching {info.Busy}/{info.Total}, first row '{info.FirstRow}' ({Classify(info)})");
            return info;
        }

        /// <summary>Phase from the tracker's first row. Unknown texts are logged by the caller so they can be added.</summary>
        internal static CastleBattlePhase Classify(MarchInfo info)
        {
            string text = info.FirstRow.ToLowerInvariant();
            if (info.Busy == 0 && text.Length == 0) return CastleBattlePhase.WaitingForRally;
            if (text.Contains("rally")) return CastleBattlePhase.JoinedWaitingToMarch;
            if (text.Contains("garris") || text.Contains("station")) return CastleBattlePhase.Garrisoned;
            if (text.Contains("return")) return CastleBattlePhase.MarchingBack;
            if (Regex.IsMatch(text, @"x\s*[:;.]?\s*\d", RegexOptions.None, RegexTimeouts.Default)) return CastleBattlePhase.MarchingToTarget;
            return info.Busy == 0 ? CastleBattlePhase.WaitingForRally : CastleBattlePhase.MarchingToTarget;
        }

        private static void ReportMarchState(string character, MarchInfo info)
        {
            var phase = Classify(info);
            string detail = info.Total > 0 ? $"marches {info.Busy}/{info.Total}" : "";
            CastleBattleStatus.Set(character, phase, detail);
        }

        /// <summary>The percentage under the heal icon, from a fresh screenshot</summary>
        private async Task<int?> ReadHealPercentAsync(int instance, LogService logger)
        {
            var screenshot = await TakeScreenshotAsync(instance, logger);
            return screenshot == null ? null : ReadHealPercent(screenshot, logger);
        }

        /// <summary>
        /// The percentage under the heal icon (green "36%", orange "89%"); null when there is no icon (nothing injured)
        /// or it doesn't read.
        /// </summary>
        internal static int? ReadHealPercent(byte[] screenshot, LogService logger)
        {
            string text = ReadMaskedText(screenshot, HealPercentArea, new Scalar(10, 120, 170), new Scalar(85, 255, 255), "0123456789%", logger);
            var m = Regex.Match(text, @"(\d{1,3})", RegexOptions.None, RegexTimeouts.Default);
            return m.Success && int.Parse(m.Groups[1].Value) <= 100 ? int.Parse(m.Groups[1].Value) : null;
        }

        #endregion

        #region Join rallies

        /// <summary>Alliance > War, then join every rally with a green "+" while march queues are free. Returns the joins.</summary>
        private async Task<int> JoinRalliesAsync(AccountSettings account, string character, CastleBattleSettings settings, MarchInfo march, LogService logger, CancellationToken cancellationToken)
        {
            int instance = account.InstanceNumber;
            var phase = Classify(march);
            if (!march.HasFreeQueue || phase != CastleBattlePhase.WaitingForRally)
            {
                logger.LogInfo($"[{character}] Castle Battle: already busy ({CastleBattleStatus.Describe(phase)}), not joining");
                CastleBattleStatus.Set(character, phase);
                return 0;
            }
            if (!await OpenWarAsync(character, instance, logger, cancellationToken)) return 0;

            int joined = 0;
            int free = march.Total > 0 ? march.Total - march.Busy : MaxJoinsPerPass;
            // a "+" that opened no deploy screen (the rally just joined stays green for a moment, or it just left) is skipped
            var tried = new List<int>();
            for (int attempt = 0; attempt < MaxJoinsPerPass + 3 && joined < Math.Min(free, MaxJoinsPerPass); attempt++)
            {
                var buttons = await FindJoinButtonsWithScrollAsync(instance, logger, cancellationToken, tried);
                logger.LogInfo($"[{character}] Castle Battle: {buttons.Count} rallies to join ({buttons.Count(b => b.Defensive)} Def. Lineup)");
                if (buttons.Count == 0) break;
                tried.Add(buttons[0].Center.Y);
                if (!await JoinButtonAsync(character, settings, buttons[0], instance, logger, cancellationToken)) continue;
                joined++;
                CastleBattleStatus.CountJoin(character);
                await Task.Delay(2000, cancellationToken);  // let the list refresh: the joined "+" turns grey
            }
            if (joined == 0) CastleBattleStatus.Set(character, CastleBattlePhase.WaitingForRally);

            await LeaveWarAsync(instance, logger, cancellationToken);
            return joined;
        }

        /// <summary>Blue "Def. Lineup" row: defending our building, so the defensive team flag; red "Rally": the offensive one</summary>
        private Task<bool> JoinButtonAsync(string character, CastleBattleSettings settings, JoinButton button, int instance, LogService logger, CancellationToken cancellationToken)
        {
            int team = button.Defensive ? settings.DefensiveTeam : settings.OffensiveTeam;
            CastleBattleStatus.Set(character, button.Defensive ? CastleBattlePhase.SendingDefense : CastleBattlePhase.JoiningRally);
            return JoinAsync(character, button.Center, team, instance, logger, cancellationToken);
        }

        #endregion
    }
}
