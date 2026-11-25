using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.ImageDetection;
using Bot.Core.Services;
using OpenCvSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// Events (top right, under VIP) > Alliance Mobilization tab. First the daily free mission attempt ("+" with a red dot)
    /// and the character's own two missions in the top row (AllianceMobilizationTask.Own.cs: Terror and troop training
    /// are accepted, anything else is refreshed, <see cref="AllianceMobilizationSettings"/>). Then, R4 and higher, it
    /// refreshes the Open Missions the alliance doesn't want. Below the own row, an open mission card shows a
    /// "+points" coin; cards with a member's avatar are taken and are skipped. Bear, lion, gems, gift, fist (train troops)
    /// and fist with flag missions stay; any other open mission is tapped > Refresh mission > Refresh.
    ///
    /// SAFETY: refreshing an open mission is free (R4 and higher). The "Refresh 500 gems" buttons on refreshing slots are
    /// never tapped: only cards with a points coin are opened.
    /// </summary>
    public partial class AllianceMobilizationTask : BaseTaskWithCommonPatterns, IScheduledTask
    {
        public override TaskType TaskType => TaskType.AllianceMobilization;
        public override string Name => "Alliance Mobilization";


        internal const string EventsButtonTemplate = "events-button.png";
        private const string MobilizationTabTemplate = "mobilization-tab.png";     // trophy tab, selected (white background)
        // Events opens on a tab with a red dot (e.g. Armament Competition), so the trophy tab is often unselected (brown
        // background): 0.99 there, <= 0.69 elsewhere. The selected template scores 0.47 on it
        private const string MobilizationTabUnselectedTemplate = "mobilization-tab-unselected.png";
        private const string MobilizationTitleTemplate = "mobilization-title.png";
        private const string PointsCoinTemplate = "points-coin.png";               // "+points" coin under an open mission
        private const string RefreshMissionTemplate = "refresh-mission.png";       // orange button of the Open Mission popup
        private const string ConfirmRefreshTemplate = "confirm-refresh.png";       // teal Refresh button of the Confirmation popup
        private const string OpenMissionTitleTemplate = "open-mission-title.png";
        private static readonly string[] KeepTemplates =
        {
            "keep-bear.png", "keep-lion.png", "keep-gems.png", "keep-gift.png", "keep-fist.png", "keep-fist-flag.png"
        };

        internal static readonly Rectangle EventsButtonArea = new Rectangle(590, 80, 130, 200);
        private static readonly Rectangle TabArea = new Rectangle(30, 85, 690, 110);
        private static readonly Rectangle TitleArea = new Rectangle(20, 195, 460, 80);
        private static readonly Rectangle RefreshMissionArea = new Rectangle(60, 750, 320, 110);
        private static readonly Rectangle ConfirmRefreshArea = new Rectangle(360, 730, 300, 120);
        private static readonly Rectangle OpenMissionTitleArea = new Rectangle(220, 375, 280, 75);
        // Visible part of the mission list; a card's icon must lie inside it to be judged
        private const int ListTop = 645;
        private const int ListBottom = 1240;
        // x of the coin's left edge in the three grid columns; the own missions' coins sit at ~153 and ~473
        private static readonly int[] GridCoinX = { 101, 317, 528 };
        private const int CoinColumnTolerance = 15;
        // Card icon box relative to the coin's top-left corner
        private const int IconOffsetX = -27, IconOffsetY = -160, IconSize = 140;
        private static readonly Point BackArrowPoint = new Point(45, 40);
        private static readonly Point PopupClosePoint = new Point(642, 410);
        // Page down: a slow 200 px drag. A fast 400 px swipe flung the list 300-566 px, so a row could jump from below the list
        // straight past its top edge and never be judged (a card is judged while its icon is fully inside a 455 px band)
        private static readonly Point ScrollFrom = new Point(360, 1150);
        private static readonly Point ScrollTo = new Point(360, 950);
        private const int PageSwipeMs = 1500;
        // Scroll to top: fast swipes the other way
        private static readonly Point TopSwipeFrom = new Point(360, 750);
        private static readonly Point TopSwipeTo = new Point(360, 1150);

        private const double Threshold = 0.85;      // buttons, tab and titles: 1.00 present, <= 0.71 elsewhere
        private const double CoinThreshold = 0.80;  // 0.93-1.00 on open cards, <= 0.63 elsewhere
        // The Events icon is animated and can carry a red dot: 0.77-1.00 on 20 town screens (Ilsa with dot 0.79), <= 0.53 on every other screen
        internal const double EventsThreshold = 0.65;
        // Grayscale, so the blue/purple/gold card backgrounds don't matter: 0.82-1.00 on the icon (fist on gold 0.91,
        // bear on gold 0.82), <= 0.52 on every other icon
        private const double KeepThreshold = 0.70;   // the yellow gem scores 0.42 on keep-gems (blue gems 0.97-0.99): only blue gems are kept
        private const int MaxScrolls = 30;
        private const int MaxRefreshes = 30;

        // The refreshed slots come back after 30 minutes; don't run more often than that per character
        private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(25);
        private static readonly ConcurrentDictionary<string, DateTime> _lastRunUtc = new();
        // Only R4 and higher get the Refresh mission button. The first open card on a character settles it (a keep card is
        // opened and closed again); a character without the button skips the open missions until the app restarts.
        private static readonly ConcurrentDictionary<string, bool> _hasRights = new();

        public void ClearSchedule(string accountKey)
        {
            _lastRunUtc.TryRemove(accountKey, out _);
        }

        public DateTime? GetNextRunUtc(string accountKey, AccountSettings account)
        {
            return _lastRunUtc.TryGetValue(accountKey, out var last) && last + MinInterval > DateTime.UtcNow ? last + MinInterval : null;
        }

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            try
            {
                string accountId = AccountDetectionTask.GetCachedAccountId(account.InstanceNumber);
                string accountKey = string.IsNullOrEmpty(accountId) ? $"Account_{account.AccountName}_{account.InstanceNumber}" : accountId;
                var skipped = CheckSkip(accountKey);
                if (skipped != null) return skipped;

                var locatorService = new LocatorService(logger, account, TaskType);
                int instance = account.InstanceNumber;
                await locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

                var open = await OpenMobilizationAsync(account, logger, instance, cancellationToken);
                if (open != null)
                {
                    await locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);
                    return open;
                }

                var settings = account.AllianceMobilizationSettings ?? new AllianceMobilizationSettings();
                var (own, attempts) = await HandleOwnMissionsAsync(account, logger, instance, settings, cancellationToken);

                var (state, noRightsHit, completed) = await RefreshOpenMissionsAsync(account, logger, instance, accountKey, settings, attempts, cancellationToken);
                // A run that broke off (popup missing, screen changed) is retried on the next cycle
                if (completed) _lastRunUtc[accountKey] = DateTime.UtcNow;

                await ClickAsync(instance, logger, BackArrowPoint);
                await Task.Delay(1500, cancellationToken);
                await locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

                string result = own + DescribeResult(state, noRightsHit, settings.RefreshOpenMissions);
                if (!completed && !noRightsHit) return TaskExecutionDetails.Failed($"Stopped early, {result}");
                logger.LogInfo($"[{account.AccountName}] Alliance Mobilization done: {result}");
                return TaskExecutionDetails.Succeeded(result);
            }
            catch (OperationCanceledException)
            {
                return TaskExecutionDetails.Failed("Task was cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error in Alliance Mobilization: {ex.Message}");
                return TaskExecutionDetails.Failed(ex.Message);
            }
        }

        private static TaskExecutionDetails? CheckSkip(string accountKey)
        {
            if (!EventCalendar.MayRun(TaskType.AllianceMobilization, DateTime.UtcNow))
                return TaskExecutionDetails.SkippedBecause("Not on the event calendar today");
            if (_lastRunUtc.TryGetValue(accountKey, out var lastRun) && DateTime.UtcNow - lastRun < MinInterval)
                return TaskExecutionDetails.SkippedBecause($"Ran {(int)(DateTime.UtcNow - lastRun).TotalMinutes} min ago");
            return null;
        }

        /// <summary>
        /// The open missions: Terror / troop training ones worth MinPoints are accepted while attempts are left (any rank),
        /// the unwanted ones refreshed when switched on and the character isn't known to lack R4
        /// </summary>
        private async Task<(RefreshState state, bool noRights, bool completed)> RefreshOpenMissionsAsync(AccountSettings account, LogService logger, int instance,
            string accountKey, AllianceMobilizationSettings settings, int? attempts, CancellationToken cancellationToken)
        {
            bool rightsMissing = _hasRights.TryGetValue(accountKey, out var rights) && !rights;
            var state = new RefreshState
            {
                Settings = settings,
                CanRefresh = settings.RefreshOpenMissions && !rightsMissing,
                Attempts = attempts,
                NoRights = rightsMissing,
            };
            if (!state.CanRefresh && !state.CanAccept) return (state, rightsMissing, true);
            var (noRights, completed) = await RefreshMissionsAsync(account, logger, instance, accountKey, state, cancellationToken);
            return (state, noRights || state.NoRights, completed);
        }

        private static string DescribeResult(RefreshState state, bool noRightsHit, bool refreshOpen)
        {
            string accepted = state.Accepted > 0 ? $"{state.Accepted} open mission(s) accepted, " : "";
            if (!refreshOpen) return accepted + "open missions not refreshed (switched off)";
            return accepted + (noRightsHit
                ? "open missions: no Refresh mission button (needs R4 or higher)"
                : $"{state.Refreshed} open mission(s) refreshed");
        }

        /// <summary>
        /// Events button > Alliance Mobilization tab. Returns null once the screen is open, otherwise the task result.
        /// </summary>
        private async Task<TaskExecutionDetails?> OpenMobilizationAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            var events = await MatchAsync(EventsButtonTemplate, EventsButtonArea, EventsThreshold, instance, logger);
            if (!events.found)
            {
                logger.LogWarning($"[{account.AccountName}] Alliance Mobilization: Events button not found ({events.confidence:F2})");
                return TaskExecutionDetails.Failed("Events button not found");
            }
            await ClickCenterInRectAsync(instance, logger, events.rect);
            await Task.Delay(3000, cancellationToken);

            var tab = await MatchAsync(MobilizationTabTemplate, TabArea, Threshold, instance, logger);
            if (!tab.found)
            {
                var unselected = await MatchAsync(MobilizationTabUnselectedTemplate, TabArea, Threshold, instance, logger);
                if (unselected.found || unselected.confidence > tab.confidence) tab = unselected;
            }
            if (!tab.found)
            {
                logger.LogInfo($"[{account.AccountName}] Alliance Mobilization: no Mobilization tab, event not running ({tab.confidence:F2})");
                await ClickAsync(instance, logger, BackArrowPoint);
                await Task.Delay(1500, cancellationToken);
                return TaskExecutionDetails.Succeeded("Alliance Mobilization not running");
            }
            await ClickCenterInRectAsync(instance, logger, tab.rect);
            for (int i = 0; i < 4; i++)
            {
                await Task.Delay(1500, cancellationToken);
                if ((await MatchAsync(MobilizationTitleTemplate, TitleArea, Threshold, instance, logger)).found) return null;
            }
            logger.LogWarning($"[{account.AccountName}] Alliance Mobilization screen did not open");
            await ClickAsync(instance, logger, BackArrowPoint);
            await Task.Delay(1500, cancellationToken);
            return TaskExecutionDetails.Failed("Alliance Mobilization screen did not open");
        }

        /// <summary>
        /// Walks the mission list top to bottom: accepts the wanted open missions and refreshes every open mission whose
        /// icon is not on the keep list.
        /// </summary>
        private async Task<(bool noRights, bool completed)> RefreshMissionsAsync(AccountSettings account, LogService logger, int instance, string accountKey,
            RefreshState state, CancellationToken cancellationToken)
        {
            Mat? previousList = null;
            try
            {
                await ScrollToTopAsync(instance, logger, cancellationToken);
                for (int page = 0; page <= MaxScrolls; page++)
                {
                    var pageEnd = await RefreshPageAsync(account, logger, instance, accountKey, page, state, cancellationToken);
                    if (pageEnd != null) return pageEnd.Value;

                    // Scroll down; stop once the list no longer moves
                    await SwipeAsync(instance, logger, ScrollFrom, ScrollTo, PageSwipeMs);
                    await Task.Delay(1500, cancellationToken);
                    var after = await TakeScreenshotAsync(instance, logger);
                    if (after == null) break;
                    var list = ListRegion(after);
                    if (previousList != null && IsSame(previousList, list))
                    {
                        list.Dispose();
                        break;
                    }
                    previousList?.Dispose();
                    previousList = list;
                }
            }
            finally
            {
                previousList?.Dispose();
            }
            return (false, true);
        }

        private sealed class RefreshState
        {
            public AllianceMobilizationSettings Settings = new();
            public bool CanRefresh;
            public bool NoRights;
            public int? Attempts;      // null: unread, accept and watch for the purchase popup
            public int Refreshed;
            public int Accepted;
            public Rectangle? LastTarget;
            public readonly HashSet<Rectangle> Checked = new();   // open cards looked at on this page (accepted or not wanted)

            public bool CanAccept => Attempts != 0 && (Settings.KeepTerror || Settings.KeepTroopTraining);
        }

        private enum CardAction { Check, Probe, Refresh }

        /// <summary>
        /// Refreshes the open missions on the visible page. Returns null when the page is done (scroll on),
        /// otherwise the end result of the whole run.
        /// </summary>
        private async Task<(bool noRights, bool completed)?> RefreshPageAsync(
            AccountSettings account, LogService logger, int instance, string accountKey, int page, RefreshState state, CancellationToken cancellationToken)
        {
            state.Checked.Clear();
            // Re-scan the page after each refresh: the refreshed card turns into a "Mission refreshing" slot
            while (true)
            {
                var shot = await TakeScreenshotAsync(instance, logger);
                if (shot == null) return (false, false);
                using var gray = Cv2.ImDecode(shot, ImreadModes.Grayscale);

                bool rightsKnown = _hasRights.ContainsKey(accountKey);
                var cards = CheckCards(gray);
                logger.LogInfo($"[{account.AccountName}] Alliance Mobilization page {page}: {cards.Count} open card(s)");
                var (target, action) = SelectCard(account, logger, cards, rightsKnown, state);
                if (target == null) return null;

                var end = action switch
                {
                    CardAction.Check => await CheckOpenCardAsync(account, logger, instance, target.Value, state, cancellationToken),
                    CardAction.Probe => await ProbeRightsAsync(account, logger, instance, accountKey, target.Value, state, cancellationToken),
                    _ => await RefreshTargetAsync(account, logger, instance, accountKey, target.Value, state, cancellationToken),
                };
                if (end != null) return end;
            }
        }

        /// <summary>Rank check only: open the card, look for the Refresh mission button, close it again. Null: carry on with the page.</summary>
        private async Task<(bool noRights, bool completed)?> ProbeRightsAsync(
            AccountSettings account, LogService logger, int instance, string accountKey, Rectangle target, RefreshState state, CancellationToken cancellationToken)
        {
            var probe = await RefreshCardAsync(account, logger, instance, target, probeOnly: true, cancellationToken);
            if (probe == RefreshOutcome.Failed) return (false, false);
            _hasRights[accountKey] = probe != RefreshOutcome.NoRights;
            return probe == RefreshOutcome.NoRights ? NoRightsFound(state) : null;
        }

        /// <summary>No Refresh mission button: stop refreshing; walk on only when open missions can still be accepted</summary>
        private static (bool noRights, bool completed)? NoRightsFound(RefreshState state)
        {
            state.CanRefresh = false;
            state.NoRights = true;
            return state.CanAccept ? null : (true, true);
        }

        /// <summary>Refreshes one mission. Null: carry on with the page.</summary>
        private async Task<(bool noRights, bool completed)?> RefreshTargetAsync(
            AccountSettings account, LogService logger, int instance, string accountKey, Rectangle target, RefreshState state, CancellationToken cancellationToken)
        {
            if (state.Refreshed >= MaxRefreshes) return (false, true);
            // The same card again means the last refresh didn't take; don't loop on it
            if (target == state.LastTarget)
            {
                logger.LogWarning($"[{account.AccountName}] Alliance Mobilization: mission at {target.Location} still open after a refresh");
                return (false, false);
            }
            state.LastTarget = target;

            var outcome = await RefreshCardAsync(account, logger, instance, target, probeOnly: false, cancellationToken);
            if (outcome == RefreshOutcome.Failed) return (false, false);
            _hasRights[accountKey] = outcome != RefreshOutcome.NoRights;
            if (outcome == RefreshOutcome.NoRights) return NoRightsFound(state);
            state.Refreshed++;
            return null;
        }

        /// <summary>The first card to look at (accept check), refresh or rank-check; null when the page has none.</summary>
        private static (Rectangle? target, CardAction action) SelectCard(AccountSettings account, LogService logger, IReadOnlyList<CardCheck> cards, bool rightsKnown, RefreshState state)
        {
            foreach (var card in cards)
            {
                if (card.CutOff)
                {
                    logger.LogInfo($"[{account.AccountName}]   card at {card.Icon.Location}: cut off, judged on another page");
                    continue;
                }
                if (state.Checked.Contains(card.Icon)) continue;
                var action = ChooseAction(card.Keep, rightsKnown, state);
                logger.LogInfo($"[{account.AccountName}]   card at {card.Icon.Location}: {(card.Keep != null ? $"keep ({card.Keep})" : "refresh")}{(action == null ? ", nothing to do" : $", {action}")}");
                if (action != null) return (card.Icon, action.Value);
            }
            return (null, CardAction.Refresh);
        }

        private static CardAction? ChooseAction(string? keep, bool rightsKnown, RefreshState state)
        {
            if (keep != null && state.CanAccept && IsAcceptCandidate(keep, state.Settings)) return CardAction.Check;
            if (!state.CanRefresh) return null;
            if (keep == null) return CardAction.Refresh;
            return rightsKnown ? null : CardAction.Probe;
        }

        /// <summary>
        /// The Events screen remembers its scroll position; swipe the list back up until it stops moving.
        /// </summary>
        private async Task ScrollToTopAsync(int instance, LogService logger, CancellationToken cancellationToken)
        {
            Mat? previous = null;
            try
            {
                for (int i = 0; i < MaxScrolls; i++)
                {
                    var shot = await TakeScreenshotAsync(instance, logger);
                    if (shot == null) return;
                    var list = ListRegion(shot);
                    if (previous != null && IsSame(previous, list))
                    {
                        list.Dispose();
                        return;
                    }
                    previous?.Dispose();
                    previous = list;
                    await SwipeAsync(instance, logger, TopSwipeFrom, TopSwipeTo, 500);
                    await Task.Delay(1500, cancellationToken);
                }
            }
            finally
            {
                previous?.Dispose();
            }
        }

        private enum RefreshOutcome { Refreshed, HasRights, NoRights, Failed }

        /// <summary>
        /// Card > Refresh mission > Refresh (confirm). With probeOnly the popup is closed once the Refresh mission button is seen.
        /// </summary>
        private async Task<RefreshOutcome> RefreshCardAsync(AccountSettings account, LogService logger, int instance, Rectangle icon, bool probeOnly, CancellationToken cancellationToken)
        {
            await ClickCenterInRectAsync(instance, logger, icon);
            await Task.Delay(2000, cancellationToken);

            var refresh = await MatchAsync(RefreshMissionTemplate, RefreshMissionArea, Threshold, instance, logger);
            if (!refresh.found)
            {
                bool popupOpen = (await MatchAsync(OpenMissionTitleTemplate, OpenMissionTitleArea, Threshold, instance, logger)).found;
                if (popupOpen)
                {
                    logger.LogInfo($"[{account.AccountName}] Alliance Mobilization: no Refresh mission button ({refresh.confidence:F2}), needs R4 or higher");
                    await ClickAsync(instance, logger, PopupClosePoint);
                    await Task.Delay(1500, cancellationToken);
                    return RefreshOutcome.NoRights;
                }
                logger.LogWarning($"[{account.AccountName}] Alliance Mobilization: Open Mission popup did not open");
                return RefreshOutcome.Failed;
            }
            if (probeOnly)
            {
                logger.LogInfo($"[{account.AccountName}] Alliance Mobilization: Refresh mission button present, character can refresh");
                await ClickAsync(instance, logger, PopupClosePoint);
                await Task.Delay(1500, cancellationToken);
                return RefreshOutcome.HasRights;
            }
            return await TapRefreshAndConfirmAsync(account, logger, instance, refresh.rect, cancellationToken);
        }

        /// <summary>Refresh mission (on an open popup) > Refresh in the Confirmation popup; free for open and own missions alike</summary>
        private async Task<RefreshOutcome> TapRefreshAndConfirmAsync(AccountSettings account, LogService logger, int instance, Rectangle refreshButton, CancellationToken cancellationToken)
        {
            await ClickCenterInRectAsync(instance, logger, refreshButton);
            await Task.Delay(2000, cancellationToken);

            var confirm = await MatchAsync(ConfirmRefreshTemplate, ConfirmRefreshArea, Threshold, instance, logger);
            if (!confirm.found)
            {
                logger.LogWarning($"[{account.AccountName}] Alliance Mobilization: Confirmation popup not found ({confirm.confidence:F2})");
                await ClickAsync(instance, logger, PopupClosePoint);
                await Task.Delay(1500, cancellationToken);
                return RefreshOutcome.Failed;
            }
            await ClickCenterInRectAsync(instance, logger, confirm.rect);
            await Task.Delay(2000, cancellationToken);
            logger.LogInfo($"[{account.AccountName}] Alliance Mobilization: mission refreshed");
            return RefreshOutcome.Refreshed;
        }

        /// <summary>An open mission card on the visible list. Keep is the matching keep template (null = refresh).</summary>
        internal readonly record struct CardCheck(Rectangle Icon, bool CutOff, string? Keep);

        /// <summary>
        /// The open cards of the visible list, top to bottom: cut off at the list edge (judged on another page), keep or refresh.
        /// </summary>
        internal List<CardCheck> CheckCards(Mat gray)
        {
            var cards = new List<CardCheck>();
            foreach (var coin in FindCoins(gray))
            {
                var icon = new Rectangle(coin.X + IconOffsetX, coin.Y + IconOffsetY, IconSize, IconSize);
                bool cutOff = icon.Top < ListTop || icon.Bottom > ListBottom;
                cards.Add(new CardCheck(icon, cutOff, cutOff ? null : MatchKeepIcon(gray, icon)));
            }
            return cards;
        }

        /// <summary>
        /// Top-left corners of the points coins in the three grid columns of the visible list, top to bottom.
        /// </summary>
        private List<Point> FindCoins(Mat gray) => FindCoins(gray, ListTop, ListBottom, GridCoinX);

        /// <summary>Top-left corners of the points coins between top and bottom whose left edge sits in one of the columns</summary>
        private List<Point> FindCoins(Mat gray, int top, int bottom, int[] columnX)
        {
            var coins = new List<Point>();
            using var template = Cv2.ImRead(Path.Combine(ImageTemplateFolder, PointsCoinTemplate), ImreadModes.Grayscale);
            if (template.Empty()) return coins;
            var area = new Rect(0, top, gray.Width, bottom - top).Intersect(new Rect(0, 0, gray.Width, gray.Height));
            using var region = new Mat(gray, area);
            using var result = new Mat();
            Cv2.MatchTemplate(region, template, result, TemplateMatchModes.CCoeffNormed);
            for (int i = 0; i < 20; i++)
            {
                Cv2.MinMaxLoc(result, out _, out double max, out _, out OpenCvSharp.Point loc);
                if (max < CoinThreshold) break;
                var p = new Point(loc.X, loc.Y + area.Y);
                if (columnX.Any(x => Math.Abs(p.X - x) <= CoinColumnTolerance)) coins.Add(p);
                // Suppress this match before looking for the next one
                Cv2.Rectangle(result, new Rect(loc.X - 30, loc.Y - 30, 60, 60), new Scalar(-1), -1);
            }
            return coins.OrderBy(p => p.Y).ThenBy(p => p.X).ToList();
        }

        /// <summary>
        /// Name of the keep template that matches the card icon, or null if the mission should be refreshed.
        /// </summary>
        private string? MatchKeepIcon(Mat gray, Rectangle icon)
        {
            var area = new Rect(icon.X - 10, icon.Y - 10, icon.Width + 20, icon.Height + 20).Intersect(new Rect(0, 0, gray.Width, gray.Height));
            using var region = new Mat(gray, area);
            foreach (var name in KeepTemplates)
            {
                using var template = Cv2.ImRead(Path.Combine(ImageTemplateFolder, name), ImreadModes.Grayscale);
                if (template.Empty() || template.Width > region.Width || template.Height > region.Height) continue;
                using var result = new Mat();
                Cv2.MatchTemplate(region, template, result, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(result, out _, out double max);
                if (max >= KeepThreshold) return $"{name} {max:F2}";
            }
            return null;
        }

        private static Mat ListRegion(byte[] screenshot)
        {
            using var gray = Cv2.ImDecode(screenshot, ImreadModes.Grayscale);
            var area = new Rect(0, ListTop, gray.Width, ListBottom - ListTop).Intersect(new Rect(0, 0, gray.Width, gray.Height));
            return new Mat(gray, area).Clone();
        }

        // The countdown timers keep changing a few pixels, so compare the mean difference instead of exact equality
        private static bool IsSame(Mat a, Mat b)
        {
            if (a.Size() != b.Size()) return false;
            using var diff = new Mat();
            Cv2.Absdiff(a, b, diff);
            return Cv2.Mean(diff).Val0 < 3.0;
        }
    }
}
