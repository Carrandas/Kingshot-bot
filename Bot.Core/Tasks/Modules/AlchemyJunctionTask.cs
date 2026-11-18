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
    /// Alchemy Junction, a timed event (icon in the right-hand column of the city): claim the Daily Missions' Magic Brews
    /// and the free Brewtiful Bonanza rewards, then spend every brew in the Brew Workshop minigame (<see cref="AlchemySolver"/>)
    /// and claim the brew milestones. Never taps the green "+" (buys brews), a mission's "Go", the Brewmaster Stall or
    /// the locked Bonanza columns. Brews come from missions other tasks finish during the day, so it looks again every
    /// few hours; without the icon (event over) it waits for the next reset.
    /// </summary>
    public class AlchemyJunctionTask : BaseTaskWithCommonPatterns, IScheduledTask
    {
        public override TaskType TaskType => TaskType.AlchemyJunction;
        public override string Name => "Alchemy Junction";

        private const string HeaderTemplate = "header.png";             // "Alchemy Junction" title bar
        private const string SkipTemplate = "skip.png";                 // intro dialogue
        private const string StageTemplate = "stage.png";               // "Stage N" button of the workshop
        private const string ClaimTemplate = "claim.png";               // green Claim of a finished daily mission
        private const string ClaimAllTemplate = "claim-all.png";        // Bonanza, shown with several rewards to claim
        private const string OrderTemplate = "order.png";               // "Order N" title of the minigame
        internal const string BrewPackTemplate = "brew-pack.png";       // "Magic Brew Pack" shop (real money): a jar tapped with 0 brews opens it

        private static readonly Rect IconArea = new(610, 110, 105, 700);   // the city's right-hand icon column
        private static readonly Rectangle HeaderArea = new(60, 0, 330, 90);
        private static readonly Rectangle SkipArea = new(540, 60, 180, 80);
        private static readonly Rectangle StageArea = new(200, 1120, 320, 120);
        private static readonly Rectangle ClaimArea = new(480, 560, 220, 720);
        private static readonly Rectangle ClaimAllArea = new(180, 1130, 360, 130);
        private static readonly Rectangle OrderArea = new(200, 10, 320, 90);

        // the icon's purple hair: 456-460 px in a 42x39 box on the city shots; the minigame's chef is a wider or flatter blob
        private const int MinIconHair = 350, MaxIconHair = 600;
        private const double ScreenThreshold = 0.90;    // titles/buttons 0.99-1.00, <= 0.61 elsewhere
        private const double ClaimAllMinSaturation = 70;

        // The tab strip scrolls (Brewmaster Stall, the shop, can sit where Daily Missions was): tabs are found by their
        // label (an unselected tab) and checked by the page title (the selected tab shows only its icon)
        internal const string WorkshopTab = "tab-workshop.png", WorkshopTitle = "title-workshop.png";
        internal const string BonanzaTab = "tab-bonanza.png", BonanzaTitle = "title-bonanza.png";
        internal const string MissionsTab = "tab-missions.png", MissionsTitle = "title-missions.png";
        private static readonly Rectangle TabStripArea = new(0, 90, 720, 110);
        private static readonly Rectangle PageTitleArea = new(0, 190, 460, 90);   // labels and titles 1.00 where shown, <= 0.54 elsewhere
        private static readonly System.Drawing.Point BackArrow = new(48, 40);
        private static readonly System.Drawing.Point DismissPoint = new(360, 1180);   // "Tap anywhere to exit"
        private static readonly System.Drawing.Point MilestoneChest = new(58, 145);
        private static readonly System.Drawing.Point MilestoneClose = new(655, 365);
        private const int MilestoneCardY = 555;

        private const int MaxSteps = 250;
        private static readonly TimeSpan Recheck = TimeSpan.FromHours(4);

        private static readonly AccountJsonStore<DateTime> NextRuns = new("alchemy_junction_times.json");
        private static readonly AccountJsonStore<Dictionary<int, HashSet<BrewColour>>> SavedExclusions = new("alchemy_junction_exclusions.json");

        public DateTime? GetNextRunUtc(string accountKey, AccountSettings account) =>
            NextRuns.TryGet(accountKey, out var next) && next > DateTime.UtcNow ? next : null;

        public void ClearSchedule(string accountKey) => NextRuns.Remove(accountKey);

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            try
            {
                int instance = account.InstanceNumber;
                string accountKey = Schedule.AccountKey(AccountDetectionTask.GetCachedAccountId(instance), account);
                var due = GetNextRunUtc(accountKey, account);
                if (due != null) return TaskExecutionDetails.SkippedBecause($"Next Alchemy Junction check at {due:HH:mm} UTC");
                if (!EventCalendar.MayRun(TaskType, DateTime.UtcNow))
                {
                    NextRuns.Set(accountKey, Schedule.NextUtcMidnight);
                    return TaskExecutionDetails.SkippedBecause("Not on the event calendar today");
                }

                var locator = new LocatorService(logger, account, TaskType);
                await locator.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);
                string result = await RunEventAsync(accountKey, instance, logger, cancellationToken);
                await LeaveAsync(instance, logger, cancellationToken);
                await locator.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);
                logger.LogInfo($"[{account.AccountName}] Alchemy Junction: {result}");
                return TaskExecutionDetails.Succeeded(result);
            }
            catch (OperationCanceledException)
            {
                return TaskExecutionDetails.Failed("Task was cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error in Alchemy Junction: {ex.Message}");
                return TaskExecutionDetails.Failed(ex.Message);
            }
        }

        private async Task<string> RunEventAsync(string accountKey, int instance, LogService logger, CancellationToken ct)
        {
            if (!await OpenEventAsync(instance, logger, ct))
            {
                // the calendar shows the event today: the icon was missed (moved, covered), look again soon
                if (EventCalendar.For(TaskType).Any(e => DateTime.UtcNow.Date >= e.FirstDay && DateTime.UtcNow.Date <= e.LastDay))
                {
                    logger.LogWarning("Alchemy Junction: on the calendar today but the event did not open, trying again in 1 h");
                    NextRuns.Set(accountKey, DateTime.UtcNow + TimeSpan.FromHours(1));
                    return "event icon not found (on the calendar today), trying again in 1 h";
                }
                NextRuns.Set(accountKey, Schedule.NextUtcMidnight);
                return "Alchemy Junction not running, checking again after the daily reset";
            }

            int missions = await ClaimMissionsAsync(instance, logger, ct);
            int rewards = await ClaimBonanzaAsync(instance, logger, ct);
            var play = await PlayWorkshopAsync(accountKey, instance, logger, ct);
            NextRuns.Set(accountKey, DateTime.UtcNow + Recheck);
            return $"{missions} mission(s) and {rewards} Bonanza reward(s) claimed; {play}";
        }

        private async Task<bool> OpenEventAsync(int instance, LogService logger, CancellationToken ct)
        {
            var shot = await TakeScreenshotAsync(instance, logger);
            if (shot == null) return false;
            System.Drawing.Point? icon;
            using (var hsv = AlchemyBoard.Hsv(shot)) icon = FindEventIcon(hsv);
            if (icon == null) return false;
            await ClickAsync(instance, logger, icon.Value);
            if (!await WaitForAsync(() => IsOnEventAsync(instance, logger), TimeSpan.FromSeconds(8), TimeSpan.FromMilliseconds(700), ct))
                return false;
            var skip = await MatchAsync(SkipTemplate, SkipArea, ScreenThreshold, instance, logger);
            if (skip.found)
            {
                await ClickCenterInRectAsync(instance, logger, skip.rect);
                await Task.Delay(2000, ct);
            }
            return true;
        }

        /// <summary>
        /// The event icon (a chef with purple hair) in the city's right column: the largest purple blob there. The icon is
        /// animated and its label scrolls, so a template scores 0.63-0.98; the hair's colour is steady.
        /// </summary>
        internal static System.Drawing.Point? FindEventIcon(Mat hsv)
        {
            using var region = new Mat(hsv, IconArea);
            using var mask = region.InRange(new Scalar(135, 111, 141), new Scalar(158, 255, 255));
            using var labels = new Mat();
            using var stats = new Mat();
            using var centroids = new Mat();
            int count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids);
            int best = -1, bestArea = 0;
            for (int i = 1; i < count; i++)
            {
                int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
                if (area > bestArea && IsHairShaped(stats, i)) { bestArea = area; best = i; }
            }
            if (best < 0 || bestArea < MinIconHair || bestArea > MaxIconHair) return null;
            // the hair sits at the icon's top right: tap the face below-left of it
            return new System.Drawing.Point(IconArea.X + (int)centroids.At<double>(best, 0) - 15, IconArea.Y + (int)centroids.At<double>(best, 1) + 10);
        }

        private static bool IsHairShaped(Mat stats, int i)
        {
            int w = stats.At<int>(i, (int)ConnectedComponentsTypes.Width), h = stats.At<int>(i, (int)ConnectedComponentsTypes.Height);
            return w is >= 32 and <= 55 && h is >= 30 and <= 50;
        }

        private async Task<bool> IsOnEventAsync(int instance, LogService logger) =>
            (await MatchAsync(HeaderTemplate, HeaderArea, ScreenThreshold, instance, logger)).found;

        /// <summary>Opens a tab by its label; drags the strip back to its start when the label is out of sight</summary>
        private async Task<bool> SelectTabAsync(string tab, string title, int instance, LogService logger, CancellationToken ct)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if ((await MatchAsync(title, PageTitleArea, ScreenThreshold, instance, logger)).found) return true;
                var label = await MatchAsync(tab, TabStripArea, ScreenThreshold, instance, logger);
                if (label.found)
                {
                    await ClickCenterInRectAsync(instance, logger, label.rect);
                    if (await WaitForAsync(async () => (await MatchAsync(title, PageTitleArea, ScreenThreshold, instance, logger)).found,
                            TimeSpan.FromSeconds(4), TimeSpan.FromMilliseconds(600), ct)) return true;
                    continue;
                }
                await SwipeAsync(instance, logger, new System.Drawing.Point(150, 150), new System.Drawing.Point(620, 150), 500);
                await Task.Delay(1000, ct);
            }
            logger.LogWarning($"Alchemy Junction: tab {tab} not found");
            return false;
        }

        /// <summary>Daily Missions: tap each green Claim (the list moves up after a claim)</summary>
        private async Task<int> ClaimMissionsAsync(int instance, LogService logger, CancellationToken ct)
        {
            if (!await SelectTabAsync(MissionsTab, MissionsTitle, instance, logger, ct)) return 0;
            int claimed = 0;
            for (int i = 0; i < 6; i++)
            {
                var claim = await MatchAsync(ClaimTemplate, ClaimArea, ScreenThreshold, instance, logger);
                if (!claim.found) break;
                await ClickCenterInRectAsync(instance, logger, claim.rect);
                claimed++;
                await Task.Delay(1800, ct);
                await DismissPopupAsync(instance, logger, ct);
            }
            return claimed;
        }

        /// <summary>Brewtiful Bonanza: Claim All when it shows, else the free column's highlighted reward one at a time</summary>
        private async Task<int> ClaimBonanzaAsync(int instance, LogService logger, CancellationToken ct)
        {
            if (!await SelectTabAsync(BonanzaTab, BonanzaTitle, instance, logger, ct)) return 0;
            await Task.Delay(800, ct);
            int claimed = 0;
            for (int i = 0; i < 10; i++)
            {
                var shot = await TakeScreenshotAsync(instance, logger);
                if (shot == null) break;
                var target = FindBonanzaClaim(shot, instance, logger);
                if (target == null) break;
                await ClickAsync(instance, logger, target.Value);
                claimed++;
                await Task.Delay(2200, ct);
                await DismissPopupAsync(instance, logger, ct);
            }
            return claimed;
        }

        /// <summary>Where to tap to claim a Bonanza reward: Claim All, or a free-column cell with a white glowing frame</summary>
        internal System.Drawing.Point? FindBonanzaClaim(byte[] shot, int instance, LogService logger)
        {
            var all = MatchScreenshot(shot, ClaimAllTemplate, ClaimAllArea, ScreenThreshold, instance, logger);
            if (all.found && VoyageOfLightTask.MeanSaturation(shot, all.rect) >= ClaimAllMinSaturation)
                return new System.Drawing.Point(all.rect.X + all.rect.Width / 2, all.rect.Y + all.rect.Height / 2);
            using var hsv = AlchemyBoard.Hsv(shot);
            int? y = FindFramedCell(hsv);
            return y == null ? null : new System.Drawing.Point(200, y.Value);
        }

        /// <summary>
        /// The free column (x 140-262, y 690-1240) of the Bonanza: a claimable reward has a white frame (60-370 white
        /// pixels per 20 px band over ~120 px; claimed or locked cells have none). Centre y of the first one.
        /// </summary>
        internal static int? FindFramedCell(Mat hsv)
        {
            const int top = 690, bin = 20, bins = 27;   // y 690-1230; the panel's own white edge sits at 1240+
            int start = -1;
            for (int b = 0; b <= bins; b++)
            {
                bool on = b < bins && WhitePixels(hsv, top + b * bin, bin) >= 15;
                if (on && start < 0) start = b;
                if (on || start < 0) continue;
                if (b - start >= 4) return top + (start + b) * bin / 2;
                start = -1;
            }
            return null;
        }

        private static int WhitePixels(Mat hsv, int y0, int height)
        {
            int white = 0;
            for (int y = y0; y < y0 + height; y++)
                for (int x = 140; x < 262; x++)
                {
                    var p = hsv.At<Vec3b>(y, x);
                    if (p.Item1 < 45 && p.Item2 > 235) white++;
                }
            return white;
        }

        /// <summary>A rewards popup hides the title bar; tap it away</summary>
        private async Task DismissPopupAsync(int instance, LogService logger, CancellationToken ct)
        {
            for (int i = 0; i < 2 && !await IsOnEventAsync(instance, logger); i++)
            {
                await ClickAsync(instance, logger, DismissPoint);
                await Task.Delay(1500, ct);
            }
        }

        private async Task<string> PlayWorkshopAsync(string accountKey, int instance, LogService logger, CancellationToken ct)
        {
            if (!await SelectTabAsync(WorkshopTab, WorkshopTitle, instance, logger, ct)) return "Brew Workshop tab not found";
            var stage = await MatchAsync(StageTemplate, StageArea, ScreenThreshold, instance, logger);
            if (!stage.found) return "Stage button not found";
            await ClickCenterInRectAsync(instance, logger, stage.rect);
            if (!await WaitForAsync(() => IsOnBoardAsync(instance, logger), TimeSpan.FromSeconds(8), TimeSpan.FromMilliseconds(700), ct))
                return "minigame did not open";

            await ClaimMilestonesAsync(instance, logger, ct);
            var (orders, brews) = await SolveAsync(accountKey, instance, logger, ct);
            await ClaimMilestonesAsync(instance, logger, ct);
            await ClickAsync(instance, logger, BackArrow);
            await Task.Delay(1500, ct);
            return $"{brews} brew(s) used, {orders} order(s) finished";
        }

        private async Task<bool> IsOnBoardAsync(int instance, LogService logger) =>
            (await MatchAsync(OrderTemplate, OrderArea, ScreenThreshold, instance, logger)).found;

        /// <summary>The brew chest: tap each card whose top edge is lit yellow (claimable), then close</summary>
        private async Task ClaimMilestonesAsync(int instance, LogService logger, CancellationToken ct)
        {
            await ClickAsync(instance, logger, MilestoneChest);
            await Task.Delay(1800, ct);
            for (int i = 0; i < 5; i++)
            {
                var shot = await TakeScreenshotAsync(instance, logger);
                if (shot == null) break;
                using var hsv = AlchemyBoard.Hsv(shot);
                int? x = FindClaimableMilestone(hsv);
                if (x == null) break;
                await ClickAsync(instance, logger, new System.Drawing.Point(x.Value, MilestoneCardY));
                await Task.Delay(2000, ct);
            }
            if (!await IsOnBoardAsync(instance, logger))
            {
                await ClickAsync(instance, logger, MilestoneClose);
                await Task.Delay(1500, ct);
            }
        }

        /// <summary>
        /// Milestone popup cards along y 500: claimable H 28 / V 255 (yellow), locked H 11 / V 211, claimed V 136.
        /// Centre x of the first claimable card (a run of >= 60 px), null when none.
        /// </summary>
        internal static int? FindClaimableMilestone(Mat hsv)
        {
            int start = -1;
            for (int x = 110; x <= 650; x++)
            {
                bool on = x < 650 && IsLitCard(hsv.At<Vec3b>(500, x));
                if (on && start < 0) start = x;
                if (on || start < 0) continue;
                if (x - start >= 60) return (start + x) / 2;
                start = -1;
            }
            return null;
        }

        private static bool IsLitCard(Vec3b p) => p.Item0 >= 22 && p.Item0 <= 34 && p.Item2 > 240 && p.Item1 > 120;

        /// <summary>One jar per step until the brews run out; returns the orders finished and the brews used</summary>
        private async Task<(int orders, int brews)> SolveAsync(string accountKey, int instance, LogService logger, CancellationToken ct)
        {
            int orders = 0, used = 0, idle = 0;
            for (int step = 0; step < MaxSteps && idle < 8; step++)
            {
                ct.ThrowIfCancellationRequested();
                var outcome = await StepAsync(accountKey, instance, logger, ct);
                if (outcome == StepOutcome.OutOfBrews) break;
                if (outcome == StepOutcome.Wait)
                {
                    idle++;
                    await Task.Delay(2000, ct);
                    continue;
                }
                idle = 0;
                used++;
                if (outcome == StepOutcome.Solved) orders++;
            }
            return (orders, used);
        }

        private enum StepOutcome { Placed, Solved, Wait, OutOfBrews }

        private async Task<StepOutcome> StepAsync(string accountKey, int instance, LogService logger, CancellationToken ct)
        {
            var shot = await TakeScreenshotAsync(instance, logger);
            if (shot == null || !MatchScreenshot(shot, OrderTemplate, OrderArea, ScreenThreshold, instance, logger).found) return StepOutcome.Wait;
            using var image = Cv2.ImDecode(shot, ImreadModes.Color);
            int? brews = AlchemyBoard.ReadBrews(image);
            if (brews == 0) return StepOutcome.OutOfBrews;
            if (brews == null) return StepOutcome.Wait;   // never tap without a readable count: an empty pot may offer to buy

            using var hsv = image.CvtColor(ColorConversionCodes.BGR2HSV);
            var move = NextMove(accountKey, hsv);
            if (move == null) return StepOutcome.Wait;
            await ClickAsync(instance, logger, new System.Drawing.Point(move.Value.JarX, AlchemyBoard.TrayTapY));
            await Task.Delay(1500, ct);
            if (await LeftTheShopAsync(instance, logger, ct)) return StepOutcome.OutOfBrews;
            if (!move.Value.LastOpen) return StepOutcome.Placed;

            await Task.Delay(3500, ct);
            return await JudgeRowAsync(accountKey, move.Value, instance, logger);
        }

        internal bool IsBrewPackShop(byte[] shot, int instance, LogService logger) =>
            MatchScreenshot(shot, BrewPackTemplate, HeaderArea, ScreenThreshold, instance, logger).found;

        /// <summary>The brew count was misread and the jar opened the Magic Brew Pack shop: back out, buy nothing</summary>
        private async Task<bool> LeftTheShopAsync(int instance, LogService logger, CancellationToken ct)
        {
            if (!(await MatchAsync(BrewPackTemplate, HeaderArea, ScreenThreshold, instance, logger)).found) return false;
            logger.LogWarning("Alchemy Junction: the Magic Brew Pack shop opened (no brews left), backing out");
            await ClickAsync(instance, logger, BackArrow);
            await Task.Delay(1500, ct);
            return true;
        }

        /// <summary>A planned tap: the tray jar for the leftmost open slot, and the row as it will be once placed</summary>
        internal readonly record struct Move(int JarX, bool LastOpen, Dictionary<int, BrewColour> Row, int SlotCount);

        internal static Move? NextMove(string accountKey, Mat hsv)
        {
            var slots = AlchemyBoard.ReadSlots(hsv);
            var jars = AlchemyBoard.ReadTray(hsv);
            if (slots.Count == 0 || jars.Count == 0) return null;

            var (row, open) = ReadRow(hsv, slots);
            if (open.Count == 0 || open.Count != jars.Count) return null;   // the row is being judged, or a jar is moving

            var history = AlchemyBoard.ReadHistory(hsv, slots.Count);
            // a fresh order (no older row, nothing kept or placed) owes nothing to what was saved for the last one
            if (history.Count == 0 && row.Count == 0 && slots.All(s => !s.Green)) SavedExclusions.Remove(StoreKey(accountKey, slots.Count));
            var excluded = AlchemySolver.Merge(history, Saved(accountKey, slots.Count));
            var colours = jars.Select(j => j.Colour).ToList();
            var plan = AlchemySolver.Plan(open, colours, excluded);
            if (plan == null)
            {
                SavedExclusions.Remove(StoreKey(accountKey, slots.Count));   // stale (another order): start over
                plan = Enumerable.Range(0, open.Count).ToArray();
            }
            row[open[0]] = colours[plan[0]];
            return new Move(jars[plan[0]].X, open.Count == 1, row, slots.Count);
        }

        /// <summary>Saved per character and row length, so a 5-slot order's exclusions never reach a 6-slot one</summary>
        private static string StoreKey(string accountKey, int slotCount) => $"{accountKey}:{slotCount}";

        /// <summary>The jars already standing on the current row's brown slots, and the slots still empty</summary>
        private static (Dictionary<int, BrewColour> Row, List<int> Open) ReadRow(Mat hsv, List<BoardSlot> slots)
        {
            var row = new Dictionary<int, BrewColour>();
            var open = new List<int>();
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].Green) continue;
                var placed = AlchemyBoard.PlacedJar(hsv, slots[i].X);
                if (placed == null) open.Add(i);
                else row[i] = placed.Value;
            }
            return (row, open);
        }

        private static Dictionary<int, HashSet<BrewColour>> Saved(string accountKey, int slotCount) =>
            SavedExclusions.TryGet(StoreKey(accountKey, slotCount), out var saved) ? saved : new();

        /// <summary>After the last jar: all green = solved (forget the exclusions), else save the colours that failed</summary>
        private async Task<StepOutcome> JudgeRowAsync(string accountKey, Move move, int instance, LogService logger)
        {
            var after = await TakeScreenshotAsync(instance, logger);
            if (after == null) return StepOutcome.Placed;
            using var hsv = AlchemyBoard.Hsv(after);
            var slots = AlchemyBoard.ReadSlots(hsv);
            if (slots.Count > 0 && slots.All(s => s.Green))
            {
                SavedExclusions.Remove(StoreKey(accountKey, move.SlotCount));
                await Task.Delay(3000);
                return StepOutcome.Solved;
            }
            if (slots.Count != move.SlotCount)
            {
                SavedExclusions.Remove(StoreKey(accountKey, move.SlotCount));   // the board moved on: nothing to learn
                return StepOutcome.Placed;
            }
            var excluded = Saved(accountKey, move.SlotCount);
            var green = Enumerable.Range(0, slots.Count).Where(i => slots[i].Green).ToList();
            AlchemySolver.AddFailures(excluded, move.Row, green);
            SavedExclusions.Set(StoreKey(accountKey, move.SlotCount), excluded);
            return StepOutcome.Placed;
        }

        /// <summary>Back out of the event to the city</summary>
        private async Task LeaveAsync(int instance, LogService logger, CancellationToken ct)
        {
            for (int i = 0; i < 3 && (await IsOnEventAsync(instance, logger) || await IsOnBoardAsync(instance, logger)); i++)
            {
                await ClickAsync(instance, logger, BackArrow);
                await Task.Delay(1500, ct);
            }
        }
    }
}
