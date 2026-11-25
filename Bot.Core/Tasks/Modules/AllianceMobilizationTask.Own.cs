using Bot.Core.Logging;
using Bot.Core.Models;
using Bot.Core.Utils;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;
using PageSegMode = Tesseract.PageSegMode;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// The character's own part of Alliance Mobilization (no R4 needed), calibrated on llsa 2026-11-25:
    /// - The "+" next to "Attempts: 7/10" with a red dot: Purchase popup > green Free ("Daily Free"). Without the dot the
    ///   "+" opens Mission Attempt Purchases (gems), so it is only tapped with the dot and only Free is tapped.
    /// - The top row's two own missions ("Exclusive", points coin under the card): the Exclusive Mission popup has
    ///   Refresh mission + Accept. The description decides: "Defeat Terror 5 time(s)" and "Raise 120,000 Power by
    ///   training/promoting troops" are accepted (one attempt each) when worth at least MinPoints; anything else is
    ///   refreshed (free, a new one comes in 2 minutes). Accepted cards (avatar + progress bar) and refreshing slots
    ///   ("Refresh 500" gems) show no coin, so they are never opened.
    /// </summary>
    public partial class AllianceMobilizationTask
    {
        // Green "Completed" over a finished mission's card (own row, or an accepted open card): tapping it claims the points
        // and the slot starts refreshing (5 min; its "Refresh 500" gems button then sits just below, y 812-880). 1.00, <= 0.36 elsewhere
        private const string CompletedTemplate = "completed.png";
        internal static readonly Rectangle CompletedArea = new(0, 640, 720, 600);
        private const string AcceptMissionTemplate = "accept-mission.png";              // teal Accept of the Exclusive Mission popup
        private const string FreeAttemptTemplate = "free-attempt.png";                  // green Free of the Purchase popup: 1.00, <= 0.50 elsewhere
        private const string AttemptPurchasesTitleTemplate = "attempt-purchases-title.png"; // gems popup: 1.00, <= 0.28 elsewhere

        private static readonly Point PlusButtonPoint = new(265, 545);
        internal static readonly Rectangle RedDotArea = new(274, 514, 20, 22);
        private static readonly Rectangle FreeAttemptArea = new(180, 720, 360, 140);
        private static readonly Rectangle PurchaseTitleArea = new(120, 360, 480, 100);
        private static readonly Point PurchaseClosePoint = new(655, 405);    // X of both purchase popups, far from the "50" gems button (y 863)
        // Accept: 1.00 on the Exclusive popup (right of Refresh mission), 0.98 on the Open Mission popup (centred), <= 0.51 elsewhere
        private static readonly Rectangle AcceptArea = new(200, 740, 490, 130);
        internal static readonly Rectangle AttemptsArea = new(168, 528, 72, 36);  // "7/10" of "Attempts: 7/10" (starts at x 174, the colon ends at 165), white
        internal static readonly Rectangle DescriptionArea = new(285, 480, 365, 230);
        internal static readonly Rectangle PopupPointsArea = new(350, 722, 110, 40);  // "+860" right of the popup's coin
        // The own row's coins (top-left corner) at (153, 837) and (473, 837) with the list at its top; the open grid's at x 101/317/528
        private const int OwnRowTop = 790, OwnRowBottom = 890;
        private static readonly int[] OwnCoinX = { 153, 473 };
        private const int OwnCardOffsetX = 47, OwnCardOffsetY = -90;   // card centre from the coin

        internal enum OwnMissionAction { Accept, Refresh, Leave }

        /// <summary>
        /// Free attempt, then the own missions. Returns the summary for the result ("" when nothing was done) and the
        /// attempts left (null when unread).
        /// </summary>
        private async Task<(string Summary, int? Attempts)> HandleOwnMissionsAsync(AccountSettings account, LogService logger, int instance, AllianceMobilizationSettings settings, CancellationToken cancellationToken)
        {
            await ScrollToTopAsync(instance, logger, cancellationToken);
            bool free = await ClaimFreeAttemptAsync(account, logger, instance, cancellationToken);
            int claimed = await ClaimCompletedAsync(account, logger, instance, cancellationToken);
            string done = (free ? "free attempt claimed, " : "") + (claimed > 0 ? $"{claimed} reward(s) claimed, " : "");
            if (!settings.KeepTerror && !settings.KeepTroopTraining)
                return (done.Length > 0 ? done.TrimEnd(',', ' ') + "; " : "", null);

            var shot = await TakeScreenshotAsync(instance, logger);
            if (shot == null) return ("", null);
            List<Point> coins;
            using (var gray = Cv2.ImDecode(shot, ImreadModes.Grayscale)) coins = FindOwnCoins(gray);
            int? attempts = ReadAttempts(shot);
            logger.LogInfo($"[{account.AccountName}] Alliance Mobilization: {coins.Count} own mission(s) open, attempts {attempts?.ToString() ?? "unread"}");

            var (accepted, refreshed) = await HandleOwnMissionsAsync(account, logger, instance, settings, coins, attempts, cancellationToken);
            int? left = attempts is { } a ? Math.Max(0, a - accepted) : null;
            return ($"{done}own missions: {accepted} accepted, {refreshed} refreshed; ", left);
        }

        private async Task<(int Accepted, int Refreshed)> HandleOwnMissionsAsync(AccountSettings account, LogService logger, int instance,
            AllianceMobilizationSettings settings, List<Point> coins, int? attempts, CancellationToken cancellationToken)
        {
            int accepted = 0, refreshed = 0;
            foreach (var coin in coins)
            {
                var done = await HandleOwnMissionAsync(account, logger, instance, settings, coin, attempts, cancellationToken);
                if (done == OwnMissionAction.Accept)
                {
                    accepted++;
                    if (attempts > 0) attempts--;
                }
                else if (done == OwnMissionAction.Refresh) refreshed++;
            }
            return (accepted, refreshed);
        }

        /// <summary>Taps every green Completed on the visible list (claims the mission's points). Returns how many.</summary>
        private async Task<int> ClaimCompletedAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            int claimed = 0;
            for (int i = 0; i < 4; i++)
            {
                var completed = await MatchAsync(CompletedTemplate, CompletedArea, Threshold, instance, logger);
                if (!completed.found) break;
                await ClickCenterInRectAsync(instance, logger, completed.rect);
                await Task.Delay(2500, cancellationToken);   // the coins fly to the points counter
                claimed++;
                logger.LogInfo($"[{account.AccountName}] Alliance Mobilization: completed mission claimed");
            }
            return claimed;
        }

        /// <summary>"+" with a red dot > Free. True when the free attempt was claimed.</summary>
        private async Task<bool> ClaimFreeAttemptAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            var shot = await TakeScreenshotAsync(instance, logger);
            if (shot == null || !HasRedDot(shot)) return false;

            await ClickAsync(instance, logger, PlusButtonPoint);
            await Task.Delay(2000, cancellationToken);
            var free = await MatchAsync(FreeAttemptTemplate, FreeAttemptArea, Threshold, instance, logger);
            if (!free.found)
            {
                logger.LogWarning($"[{account.AccountName}] Alliance Mobilization: no Free button behind the red dot ({free.confidence:F2}), closed");
                await ClickAsync(instance, logger, PurchaseClosePoint);
                await Task.Delay(1500, cancellationToken);
                return false;
            }
            await ClickCenterInRectAsync(instance, logger, free.rect);
            await Task.Delay(2000, cancellationToken);
            logger.LogInfo($"[{account.AccountName}] Alliance Mobilization: free mission attempt claimed");
            return true;
        }

        /// <summary>Opens one own mission and accepts, refreshes or closes it. Returns what was done.</summary>
        private async Task<OwnMissionAction> HandleOwnMissionAsync(AccountSettings account, LogService logger, int instance,
            AllianceMobilizationSettings settings, Point coin, int? attempts, CancellationToken cancellationToken)
        {
            string name = account.AccountName;
            await ClickAsync(instance, logger, new Point(coin.X + OwnCardOffsetX, coin.Y + OwnCardOffsetY));
            await Task.Delay(2000, cancellationToken);
            var shot = await TakeScreenshotAsync(instance, logger);
            var accept = await MatchAsync(AcceptMissionTemplate, AcceptArea, Threshold, instance, logger);
            if (shot == null || !accept.found)
            {
                logger.LogWarning($"[{name}] Alliance Mobilization: Exclusive Mission popup did not open ({accept.confidence:F2})");
                await ClosePopupAsync(instance, logger, cancellationToken);
                return OwnMissionAction.Leave;
            }

            var (description, points) = ReadMissionPopup(shot);
            var action = DecideOwnMission(description, points, settings);
            string worth = points is { } p ? $"+{p}" : "+?";
            logger.LogInfo($"[{name}] Alliance Mobilization: own mission \"{description}\" {worth}: {action}");
            if (action == OwnMissionAction.Accept && attempts == 0)
            {
                logger.LogInfo($"[{name}] Alliance Mobilization: no attempts left, mission not accepted");
                action = OwnMissionAction.Leave;
            }

            switch (action)
            {
                case OwnMissionAction.Accept:
                    return await AcceptMissionAsync(account, logger, instance, accept.rect, cancellationToken);
                case OwnMissionAction.Refresh:
                    return await RefreshOwnMissionAsync(account, logger, instance, cancellationToken);
                default:
                    await ClosePopupAsync(instance, logger, cancellationToken);
                    return OwnMissionAction.Leave;
            }
        }

        /// <summary>Refresh mission > Refresh, on the open Exclusive Mission popup</summary>
        private async Task<OwnMissionAction> RefreshOwnMissionAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            var refresh = await MatchAsync(RefreshMissionTemplate, RefreshMissionArea, Threshold, instance, logger);
            if (refresh.found && await TapRefreshAndConfirmAsync(account, logger, instance, refresh.rect, cancellationToken) == RefreshOutcome.Refreshed)
                return OwnMissionAction.Refresh;
            logger.LogWarning($"[{account.AccountName}] Alliance Mobilization: own mission not refreshed ({refresh.confidence:F2})");
            await ClosePopupAsync(instance, logger, cancellationToken);
            return OwnMissionAction.Leave;
        }

        /// <summary>
        /// An open mission below the own row (any rank): open it, accept a Terror / troop training one worth MinPoints
        /// (same rule as the own missions), close anything else. Null: carry on with the page.
        /// </summary>
        private async Task<(bool noRights, bool completed)?> CheckOpenCardAsync(AccountSettings account, LogService logger, int instance,
            Rectangle icon, RefreshState state, CancellationToken cancellationToken)
        {
            string name = account.AccountName;
            state.Checked.Add(icon);
            await ClickCenterInRectAsync(instance, logger, icon);
            await Task.Delay(2000, cancellationToken);
            var shot = await TakeScreenshotAsync(instance, logger);
            var title = await MatchAsync(OpenMissionTitleTemplate, OpenMissionTitleArea, Threshold, instance, logger);
            if (shot == null || !title.found)
            {
                logger.LogWarning($"[{name}] Alliance Mobilization: Open Mission popup did not open ({title.confidence:F2})");
                return (false, false);
            }

            var (description, points) = ReadMissionPopup(shot);
            var action = DecideOwnMission(description, points, state.Settings);
            string worth = points is { } p ? $"+{p}" : "+?";
            logger.LogInfo($"[{name}] Alliance Mobilization: open mission \"{description}\" {worth}: {(action == OwnMissionAction.Accept ? "accept" : "leave")}");
            var accept = await MatchAsync(AcceptMissionTemplate, AcceptArea, Threshold, instance, logger);
            if (action != OwnMissionAction.Accept || !accept.found)
            {
                await ClosePopupAsync(instance, logger, cancellationToken);
                return null;
            }
            if (await AcceptMissionAsync(account, logger, instance, accept.rect, cancellationToken) == OwnMissionAction.Accept)
            {
                state.Accepted++;
                if (state.Attempts > 0) state.Attempts--;
            }
            else state.Attempts = 0;   // purchase popup or no change: don't try again this run
            return null;
        }

        /// <summary>Fist (troop training) or lion (Terror) card, when that kind is kept; the popup text decides</summary>
        private static bool IsAcceptCandidate(string keep, AllianceMobilizationSettings settings) =>
            (settings.KeepTroopTraining && keep.StartsWith("keep-fist.png", StringComparison.Ordinal))
            || (settings.KeepTerror && keep.StartsWith("keep-lion.png", StringComparison.Ordinal));

        /// <summary>Accept; a gems purchase popup (no attempts left) or a popup still open is closed</summary>
        private async Task<OwnMissionAction> AcceptMissionAsync(AccountSettings account, LogService logger, int instance, Rectangle acceptButton, CancellationToken cancellationToken)
        {
            await ClickCenterInRectAsync(instance, logger, acceptButton);
            await Task.Delay(2000, cancellationToken);
            if ((await MatchAsync(AttemptPurchasesTitleTemplate, PurchaseTitleArea, Threshold, instance, logger)).found)
            {
                logger.LogInfo($"[{account.AccountName}] Alliance Mobilization: no attempts left (purchase popup), closed");
                await ClickAsync(instance, logger, PurchaseClosePoint);
                await Task.Delay(1500, cancellationToken);
                await ClosePopupAsync(instance, logger, cancellationToken);
                return OwnMissionAction.Leave;
            }
            if ((await MatchAsync(AcceptMissionTemplate, AcceptArea, Threshold, instance, logger)).found)
            {
                logger.LogWarning($"[{account.AccountName}] Alliance Mobilization: popup still open after Accept, closed");
                await ClosePopupAsync(instance, logger, cancellationToken);
                return OwnMissionAction.Leave;
            }
            logger.LogInfo($"[{account.AccountName}] Alliance Mobilization: own mission accepted");
            return OwnMissionAction.Accept;
        }

        private async Task ClosePopupAsync(int instance, LogService logger, CancellationToken cancellationToken)
        {
            await ClickAsync(instance, logger, PopupClosePoint);
            await Task.Delay(1500, cancellationToken);
        }

        /// <summary>
        /// Accept a kept kind (Terror, troop training) worth at least MinPoints (unread points count as enough), refresh
        /// anything else; leave it when the description is unreadable or nothing is kept.
        /// </summary>
        internal static OwnMissionAction DecideOwnMission(string? description, int? points, AllianceMobilizationSettings settings)
        {
            if (!settings.KeepTerror && !settings.KeepTroopTraining) return OwnMissionAction.Leave;
            string text = description ?? "";
            if (text.Count(char.IsLetter) < 5) return OwnMissionAction.Leave;
            bool enough = points == null || points >= settings.MinPoints;
            return IsKeptKind(text, settings) && enough ? OwnMissionAction.Accept : OwnMissionAction.Refresh;
        }

        // "train", not "troop": a heal-troops mission must not count as training
        private static bool IsKeptKind(string description, AllianceMobilizationSettings settings) =>
            (settings.KeepTerror && description.Contains("terror", StringComparison.OrdinalIgnoreCase))
            || (settings.KeepTroopTraining && description.Contains("train", StringComparison.OrdinalIgnoreCase));

        /// <summary>The red dot on the "+" (only there while the daily free attempt is unclaimed)</summary>
        internal static bool HasRedDot(byte[] screenshot)
        {
            using var image = Cv2.ImDecode(screenshot, ImreadModes.Color);
            var roi = new Rect(RedDotArea.X, RedDotArea.Y, RedDotArea.Width, RedDotArea.Height).Intersect(new Rect(0, 0, image.Width, image.Height));
            using var region = new Mat(image, roi);
            using var mask = region.InRange(new Scalar(0, 0, 200), new Scalar(90, 90, 255));
            return Cv2.CountNonZero(mask) >= 15;
        }

        /// <summary>Coins of the own row (list at its top), left to right</summary>
        internal List<Point> FindOwnCoins(Mat gray) => FindCoins(gray, OwnRowTop, OwnRowBottom, OwnCoinX).OrderBy(p => p.X).ToList();

        /// <summary>Attempts left ("7" of "7/10"); null when unreadable</summary>
        internal static int? ReadAttempts(byte[] screenshot) =>
            ParseAttempts(MaskedTextOcr.Read(screenshot, AttemptsArea, new Scalar(0, 0, 200), new Scalar(180, 60, 255), "0123456789/"));

        internal static int? ParseAttempts(string text)
        {
            var m = Regex.Match(text ?? "", @"(\d+)\s*/\s*\d+", RegexOptions.None, RegexTimeouts.Default);
            return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : null;
        }

        /// <summary>The Exclusive Mission popup's description and points (dark text on beige)</summary>
        internal static (string Description, int? Points) ReadMissionPopup(byte[] screenshot)
        {
            using var image = Cv2.ImDecode(screenshot, ImreadModes.Color);
            var dark = (Low: new Scalar(0, 0, 0), High: new Scalar(180, 255, 130));
            string description = MaskedTextOcr.Read(image, DescriptionArea, dark.Low, dark.High, null, PageSegMode.SingleBlock);
            description = Regex.Replace(description, @"\s+", " ", RegexOptions.None, RegexTimeouts.Default).Trim();
            string points = MaskedTextOcr.Read(image, PopupPointsArea, dark.Low, dark.High, "+0123456789,");
            return (description, ParsePoints(points));
        }

        /// <summary>
        /// The "+200" right of an open card's coin (dark text on the card's white bottom); null when unreadable. Lets a
        /// Terror / troop card under MinPoints be skipped without opening it (pages overlap, so it would be opened twice).
        /// </summary>
        internal static int? ReadCardPoints(byte[] screenshot, Rectangle icon)
        {
            var area = new Rectangle(icon.X - IconOffsetX + 38, icon.Y - IconOffsetY - 4, 110, 44);
            return ParsePoints(MaskedTextOcr.Read(screenshot, area, new Scalar(0, 0, 0), new Scalar(180, 255, 130), "+0123456789,"));
        }

        internal static int? ParsePoints(string text)
        {
            string digits = new((text ?? "").Where(char.IsDigit).ToArray());
            return digits.Length is > 0 and <= 6 && int.TryParse(digits, out int n) ? n : null;
        }
    }
}
