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
    public class CastleBattleTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.CastleBattle;
        public override string Name => "Castle Battle";

        // This folder
        private const string DeployButtonTemplate = "deploy-button.png";   // deploy screen: 1.00 there, <= 0.54 on War/Balance/map
        private const string HealTitleTemplate = "heal-title.png";         // "Heal Injured": 1.00, <= 0.31 elsewhere
        private const string HealButtonHealTemplate = "heal-btn-heal.png";
        private const string HealButtonHelpTemplate = "heal-btn-help.png";
        private const string HealButtonSpeedupsTemplate = "heal-btn-speedups.png";
        private const string HealCancelTitleTemplate = "heal-cancel-title.png";  // "Cancel healing" confirmation: 1.00, <= 0.38 elsewhere
        private const string BattleHealIconTemplate = "heal-icon-battle.png"; // the battle's heal icon (handshake + green "+"): 1.00, <= 0.41 elsewhere
        // Shared with Auto Join Rally / Auto Heal
        private static readonly string AllianceMenuTemplate = Path.Combine("autojoinrally", "alliance-menu.png");
        private static readonly string WarTileTemplate = Path.Combine("autojoinrally", "war-tile.png");
        private static readonly string WarTitleTemplate = Path.Combine("autojoinrally", "war-title.png");
        private static readonly string HealIconTemplate = Path.Combine("autoheal", "heal.png");

        private static readonly Rectangle AllianceMenuArea = new Rectangle(480, 1160, 150, 110);
        private static readonly Rectangle WarTileArea = new Rectangle(0, 600, 360, 160);
        private static readonly Rectangle WarTitleArea = new Rectangle(60, 0, 200, 80);
        private static readonly Rectangle DeployButtonArea = new Rectangle(400, 1160, 310, 100);
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

        // War > Rally list: the join "+" sits at the right of each row; green = can join (2,500 green pixels), grey = joined/full (0)
        private static readonly Rect JoinColumn = new Rect(560, 150, 160, 1000);
        // Marching tracker, top left of the map: "Marching 1/6", then one row per march ("Rallying" / target "X:650, Y:600" / ...)
        private static readonly Rectangle MarchCountArea = new Rectangle(175, 200, 80, 30);
        private static readonly Rectangle MarchRowArea = new Rectangle(58, 244, 190, 30);
        private static readonly Rectangle HealPercentArea = new Rectangle(512, 978, 92, 26);   // under the heal icon
        private const int HealBlocksJoinPercent = 70;   // at 70% or more: only heal, no rallies

        private static readonly System.Drawing.Point BackPoint = new System.Drawing.Point(45, 40);
        private static readonly System.Drawing.Point DeployPoint = new System.Drawing.Point(548, 1215);

        private const double Threshold = 0.85;      // screen elements 1.00 present, <= 0.66 elsewhere
        private const double HealThreshold = 0.65;  // as Auto Heal
        private const int MaxJoinsPerPass = 1;      // the game allows one rally at a time (a second join gives an error)
        private const int MaxScrolls = 3;
        // slow, short drags: a fast swipe flings the list past rows unjudged
        private static readonly System.Drawing.Point ListScrollFrom = new System.Drawing.Point(360, 950);
        private static readonly System.Drawing.Point ListScrollTo = new System.Drawing.Point(360, 650);
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

        /// <summary>Types the batch when it changed, then Heal. Returns the amount now in the box.</summary>
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

        /// <summary>Infantry amount box > type > OK of the input bar</summary>
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

        /// <summary>What the Marching tracker says: busy/total march queues and the first row's text</summary>
        internal sealed record MarchInfo(int Busy, int Total, string FirstRow)
        {
            public bool HasFreeQueue => Total == 0 || Busy < Total;
        }

        private async Task<MarchInfo> ReadMarchesAsync(string character, int instance, LogService logger)
        {
            var screenshot = await TakeScreenshotAsync(instance, logger);
            if (screenshot == null) return new MarchInfo(0, 0, "");
            var info = ReadMarchTracker(screenshot, logger);
            logger.LogInfo($"[{character}] Castle Battle: marching {info.Busy}/{info.Total}, first row '{info.FirstRow}' ({Classify(info)})");
            return info;
        }

        /// <summary>OCR of the Marching tracker; 0/0 and "" when it isn't shown (no marches)</summary>
        internal static MarchInfo ReadMarchTracker(byte[] screenshot, LogService logger)
        {
            string count = ReadWhiteText(screenshot, MarchCountArea, "0123456789/", logger);
            string row = ReadWhiteText(screenshot, MarchRowArea, null, logger);
            var m = Regex.Match(count, @"(\d)\s*/\s*(\d)", RegexOptions.None, RegexTimeouts.Default);
            return m.Success
                ? new MarchInfo(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), row)
                : new MarchInfo(0, 0, row);
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

        /// <summary>White text masked into white-on-black, then one line of OCR</summary>
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

        private static string ReadWhiteText(byte[] screenshot, Rectangle area, string? whitelist, LogService logger) =>
            ReadMaskedText(screenshot, area, new Scalar(0, 0, 200), new Scalar(180, 60, 255), whitelist, logger);

        /// <summary>The text colour masked into white-on-black, then one line of OCR</summary>
        private static string ReadMaskedText(byte[] screenshot, Rectangle area, Scalar low, Scalar high, string? whitelist, LogService logger)
        {
            byte[] masked;
            using (var image = Cv2.ImDecode(screenshot, ImreadModes.Color))
            {
                var roi = new Rect(area.X, area.Y, area.Width, area.Height).Intersect(new Rect(0, 0, image.Width, image.Height));
                using var region = new Mat(image, roi);
                using var hsv = region.CvtColor(ColorConversionCodes.BGR2HSV);
                using var mask = hsv.InRange(low, high);
                using var bgr = mask.CvtColor(ColorConversionCodes.GRAY2BGR);
                masked = bgr.ToBytes(".png");
            }
            var config = new OcrConfiguration { ScaleFactor = 2, PageSegMode = Tesseract.PageSegMode.SingleLine };
            if (whitelist != null) config.CharacterWhitelist = whitelist;
            using var ocr = new OcrService(logger, config);
            string text = ocr.ExtractTextFromScreenArea(masked, new Rectangle(0, 0, area.Width, area.Height)) ?? "";
            return Regex.Replace(text, @"\s+", " ", RegexOptions.None, RegexTimeouts.Default).Trim();
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
                var buttons = (await FindJoinButtonsWithScrollAsync(instance, logger, cancellationToken))
                    .Where(b => !tried.Exists(y => Math.Abs(y - b.Center.Y) < 40)).ToList();
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

        private async Task<bool> OpenWarAsync(string character, int instance, LogService logger, CancellationToken cancellationToken)
        {
            var menu = await MatchAsync(AllianceMenuTemplate, AllianceMenuArea, Threshold, instance, logger);
            if (!menu.found)
            {
                logger.LogWarning($"[{character}] Castle Battle: Alliance button not found ({menu.confidence:F2})");
                return false;
            }
            await ClickCenterInRectAsync(instance, logger, menu.rect);
            await Task.Delay(2000, cancellationToken);

            var tile = await MatchAsync(WarTileTemplate, WarTileArea, Threshold, instance, logger);
            if (!tile.found)
            {
                logger.LogWarning($"[{character}] Castle Battle: War tile not found ({tile.confidence:F2})");
                await ClickAsync(instance, logger, BackPoint);
                await Task.Delay(1500, cancellationToken);
                return false;
            }
            await ClickCenterInRectAsync(instance, logger, tile.rect);
            for (int i = 0; i < 5; i++)
            {
                await Task.Delay(1000, cancellationToken);
                if (await IsOnWarAsync(instance, logger)) return true;
            }
            logger.LogWarning($"[{character}] Castle Battle: War screen did not open");
            return false;
        }

        private async Task<bool> IsOnWarAsync(int instance, LogService logger) =>
            (await MatchAsync(WarTitleTemplate, WarTitleArea, Threshold, instance, logger)).found;

        /// <summary>War screen > Alliance screen > map</summary>
        private async Task LeaveWarAsync(int instance, LogService logger, CancellationToken cancellationToken)
        {
            for (int i = 0; i < 2; i++)
            {
                await ClickAsync(instance, logger, BackPoint);
                await Task.Delay(1500, cancellationToken);
            }
        }

        /// <summary>A green join "+" and whether its row is a blue "Def. Lineup" (else a red attack "Rally")</summary>
        internal readonly record struct JoinButton(System.Drawing.Point Center, bool Defensive);

        /// <summary>Join buttons on screen; when there are none, scrolls the list down slowly (full rallies show a grey "+") a few times</summary>
        private async Task<List<JoinButton>> FindJoinButtonsWithScrollAsync(int instance, LogService logger, CancellationToken cancellationToken)
        {
            var buttons = await FindJoinButtonsAsync(instance, logger);
            for (int i = 0; i < MaxScrolls && buttons.Count == 0; i++)
            {
                await SwipeAsync(instance, logger, ListScrollFrom, ListScrollTo, 1500);
                await Task.Delay(1000, cancellationToken);
                buttons = await FindJoinButtonsAsync(instance, logger);
            }
            return buttons;
        }

        private async Task<List<JoinButton>> FindJoinButtonsAsync(int instance, LogService logger)
        {
            var screenshot = await TakeScreenshotAsync(instance, logger);
            return screenshot == null ? new List<JoinButton>() : FindJoinButtons(screenshot);
        }

        /// <summary>The green join "+" buttons on the War > Rally list, top to bottom (grey ones are skipped)</summary>
        internal static List<JoinButton> FindJoinButtons(byte[] screenshot)
        {
            var result = new List<JoinButton>();
            using var image = Cv2.ImDecode(screenshot, ImreadModes.Color);
            using var region = new Mat(image, JoinColumn.Intersect(new Rect(0, 0, image.Width, image.Height)));
            using var hsv = region.CvtColor(ColorConversionCodes.BGR2HSV);
            using var mask = hsv.InRange(new Scalar(40, 120, 120), new Scalar(80, 255, 255));
            using var labels = new Mat();
            using var stats = new Mat();
            using var centroids = new Mat();
            int count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids);
            for (int i = 1; i < count; i++)
            {
                int w = stats.At<int>(i, (int)ConnectedComponentsTypes.Width);
                int h = stats.At<int>(i, (int)ConnectedComponentsTypes.Height);
                int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
                // the button: 59x57, 2,506 green pixels
                if (area < 1800 || w is < 45 or > 75 || h is < 45 or > 75) continue;
                int x = stats.At<int>(i, (int)ConnectedComponentsTypes.Left) + w / 2 + JoinColumn.X;
                int y = stats.At<int>(i, (int)ConnectedComponentsTypes.Top) + h / 2 + JoinColumn.Y;
                result.Add(new JoinButton(new System.Drawing.Point(x, y), IsDefensiveRow(image, y)));
            }
            return result.OrderBy(b => b.Center.Y).ToList();
        }

        /// <summary>
        /// The row header above a join button (~170 px up): blue "Def. Lineup" (HSV hue 110, sat 134) or red "Rally"
        /// (hue 3, sat 159). Looks along x=100 between 120 and 260 px above the button for the first saturated pixel.
        /// </summary>
        private static bool IsDefensiveRow(Mat image, int plusY)
        {
            using var hsv = image.CvtColor(ColorConversionCodes.BGR2HSV);
            for (int y = plusY - 120; y >= Math.Max(0, plusY - 260); y--)
            {
                var px = hsv.At<Vec3b>(y, 100);
                if (px.Item1 < 100 || px.Item2 < 150) continue;
                if (px.Item0 is >= 95 and <= 125) return true;
                if (px.Item0 <= 12 || px.Item0 >= 168) return false;
            }
            return false;
        }

        /// <summary>"+" > team flag (its saved troops and ratio) > Deploy. True when the game went back to the War list.</summary>
        private async Task<bool> JoinAsync(string character, System.Drawing.Point plus, int team, int instance, LogService logger, CancellationToken cancellationToken)
        {
            await ClickAsync(instance, logger, plus);
            await Task.Delay(2000, cancellationToken);
            if (!(await MatchAsync(DeployButtonTemplate, DeployButtonArea, Threshold, instance, logger)).found)
            {
                logger.LogWarning($"[{character}] Castle Battle: no deploy screen after the join button");
                await BackToWarAsync(instance, logger, cancellationToken);
                return false;
            }

            await ClickAsync(instance, logger, FlagPoint(team));
            await Task.Delay(1500, cancellationToken);
            await ClickAsync(instance, logger, DeployPoint);
            await Task.Delay(2500, cancellationToken);
            if (await IsOnWarAsync(instance, logger))
            {
                logger.LogInfo($"[{character}] Castle Battle: joined a rally with team {team}");
                return true;
            }
            logger.LogWarning($"[{character}] Castle Battle: Deploy did not go back to the War list (no free march?)");
            await BackToWarAsync(instance, logger, cancellationToken);
            return false;
        }

        /// <summary>Team flags 1-8 along the top of the deploy screen</summary>
        internal static System.Drawing.Point FlagPoint(int team) => new System.Drawing.Point(63 + 74 * (Math.Clamp(team, 1, 8) - 1), 122);

        private async Task BackToWarAsync(int instance, LogService logger, CancellationToken cancellationToken)
        {
            for (int i = 0; i < 3 && !await IsOnWarAsync(instance, logger); i++)
            {
                await ClickAsync(instance, logger, BackPoint);
                await Task.Delay(1500, cancellationToken);
            }
        }

        #endregion

        private async Task<(bool found, Rectangle rect, double confidence)> MatchAsync(
            string templateName, Rectangle area, double threshold, int instance, LogService logger)
        {
            var screenshot = await TakeScreenshotAsync(instance, logger);
            if (screenshot == null) return (false, Rectangle.Empty, 0);

            if (_templateMatcher == null) _templateMatcher = new UnifiedTemplateMatchingService(logger);
            // names with a folder ("autoheal/heal.png") come from another task's folder
            var path = templateName.Contains(Path.DirectorySeparatorChar)
                ? Path.Combine(AppContext.BaseDirectory, "templates", "images", templateName)
                : Path.Combine(ImageTemplateFolder, templateName);
            var result = _templateMatcher.MatchTemplate(
                screenshot, path, instance,
                threshold: threshold,
                scales: new[] { 1.0 },
                verboseLogging: false,
                searchArea: area);
            return (result.found, result.matchRect, result.confidence);
        }
    }
}
