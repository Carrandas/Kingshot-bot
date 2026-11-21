using Bot.Core.Logging;
using Bot.Core.Models;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// The extras of the Voyage of Light page (calibrated on Asli 2026-11-21):
    /// - the green "+" next to the token count opens the Sails Aloft Pack shop; its first row, Exploration Gift, has a
    ///   free token once a day (green Free, grey Claimed after). The rows below cost real money: only the Free button
    ///   (template + colour) is ever tapped there.
    /// - My Treasures: four chests. The top two (Common, Premium) get Merge (3 into 1 of the next tier; the popup starts
    ///   at the max), the bottom two (Exquisite, Majestic) get Enable (open; the popup starts at 1, the arrow sets the max).
    ///   Active buttons are coloured (saturation 135-150), greyed ones ~25.
    /// </summary>
    public partial class VoyageOfLightTask
    {
        internal const string PlusTemplate = "plus.png";                    // green "+" of the token pill
        internal const string PackHeaderTemplate = "pack-header.png";       // "Sails Aloft Pack" title
        internal const string FreeButtonTemplate = "free-button.png";       // green "Free" of the Exploration Gift
        internal const string TreasuresTextTemplate = "treasures-text.png"; // "Tidal Treasures can be opened directly or merged..."
        private const string MergeConfirmTemplate = "merge-confirm.png";    // teal Merge of the merge popup
        private const string EnableConfirmTemplate = "enable-confirm.png";  // teal Enable of the open popup

        private static readonly Rectangle PlusArea = new Rectangle(610, 330, 100, 80);
        private static readonly Rectangle PackHeaderArea = new Rectangle(60, 0, 320, 80);
        private static readonly Rectangle FreeButtonArea = new Rectangle(400, 225, 230, 100);
        private static readonly Rectangle TreasuresTextArea = new Rectangle(60, 280, 600, 100);
        private static readonly Rectangle MergeConfirmArea = new Rectangle(360, 1010, 330, 120);
        private static readonly Rectangle EnableConfirmArea = new Rectangle(190, 780, 340, 120);

        private static readonly Point MyTreasuresTab = new Point(518, 245);
        private static readonly Point VoyageSubTab = new Point(200, 245);
        private static readonly Point RewardCloseTap = new Point(360, 110);   // "Tap anywhere to exit"; a harmless spot without the popup
        private static readonly Point EnableMaxButton = new Point(648, 746);
        private static readonly Rectangle PackBackArrow = new Rectangle(24, 20, 50, 40);

        // the four chest buttons, in the order to press them: a Common merge can make a Premium merge possible
        internal static readonly (string Name, Rectangle Button, bool Merge)[] TreasureButtons =
        {
            ("Common merge", new Rectangle(212, 732, 120, 44), true),
            ("Premium merge", new Rectangle(535, 732, 120, 44), true),
            ("Exquisite enable", new Rectangle(140, 1166, 120, 44), false),
            ("Majestic enable", new Rectangle(462, 1166, 120, 44), false),
        };

        private const double ExtrasThreshold = 0.85;
        private const int MaxTreasureSteps = 8;

        /// <summary>The free Exploration Gift (once a day) and the My Treasures merges/opens; never fails the task</summary>
        private async Task<string> CollectExtrasAsync(AccountSettings account, string accountKey, int instance, LogService logger, CancellationToken cancellationToken)
        {
            var done = new List<string>();
            try
            {
                if (!DailyGate.DoneToday(TaskType, accountKey))
                {
                    var gift = await ClaimFreeGiftAsync(instance, logger, cancellationToken);
                    if (gift != GiftResult.NotSeen) DailyGate.MarkDone(TaskType, accountKey);
                    if (gift == GiftResult.Claimed) done.Add("free gift");
                }
                done.AddRange(await OpenTreasuresAsync(instance, logger, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning($"[{account.AccountName}] Voyage of Light extras: {ex.Message}");
            }
            return done.Count == 0 ? "" : $"; {string.Join(", ", done)}";
        }

        private enum GiftResult { NotSeen, Claimed, AlreadyClaimed }

        private async Task<GiftResult> ClaimFreeGiftAsync(int instance, LogService logger, CancellationToken cancellationToken)
        {
            var plus = await MatchAsync(PlusTemplate, PlusArea, ExtrasThreshold, instance, logger);
            if (!plus.found)
            {
                logger.LogWarning($"Voyage of Light: token \"+\" not found ({plus.confidence:F2})");
                return GiftResult.NotSeen;
            }
            await ClickCenterInRectAsync(instance, logger, plus.rect);
            if (!await WaitForAsync(async () => (await MatchAsync(PackHeaderTemplate, PackHeaderArea, ExtrasThreshold, instance, logger)).found,
                    TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(500), cancellationToken))
            {
                logger.LogWarning("Voyage of Light: the Sails Aloft Pack did not open");
                return GiftResult.NotSeen;
            }

            var shot = await TakeScreenshotAsync(instance, logger);
            var free = shot == null ? null : FindFreeGift(shot, instance, logger);
            bool claimed = free != null;
            if (claimed)
            {
                await ClickCenterInRectAsync(instance, logger, free!.Value);
                await Task.Delay(2500, cancellationToken);
                await ClickAsync(instance, logger, RewardCloseTap);
                await Task.Delay(1500, cancellationToken);
                logger.LogInfo("Voyage of Light: free Exploration Gift claimed");
            }
            // back to the voyage page (the pack page has its own back arrow at the same spot as the Deals one)
            if ((await MatchAsync(PackHeaderTemplate, PackHeaderArea, ExtrasThreshold, instance, logger)).found)
            {
                await ClickRandomInRectAsync(instance, logger, PackBackArrow);
                await Task.Delay(1500, cancellationToken);
            }
            if (claimed) return GiftResult.Claimed;
            return shot != null && IsGiftClaimed(shot, instance, logger) ? GiftResult.AlreadyClaimed : GiftResult.NotSeen;
        }

        /// <summary>The green "+" of the token pill (1.00 with or without its red dot, ~0.20 on My Treasures / the pack page)</summary>
        internal bool HasTokenPlus(byte[] shot, int instance, LogService logger) =>
            MatchScreenshot(shot, PlusTemplate, PlusArea, ExtrasThreshold, instance, logger).found;

        /// <summary>The green Free button of the Exploration Gift (null when claimed or absent)</summary>
        internal Rectangle? FindFreeGift(byte[] shot, int instance, LogService logger)
        {
            var match = MatchScreenshot(shot, FreeButtonTemplate, FreeButtonArea, ExtrasThreshold, instance, logger);
            return match.found && MeanSaturation(shot, match.rect) >= MinButtonSaturation ? match.rect : null;
        }

        /// <summary>The pack page shows with a grey button where Free was</summary>
        private bool IsGiftClaimed(byte[] shot, int instance, LogService logger) =>
            MatchScreenshot(shot, PackHeaderTemplate, PackHeaderArea, ExtrasThreshold, instance, logger).found &&
            MeanSaturation(shot, new Rectangle(440, 255, 145, 40)) < MinButtonSaturation;

        private async Task<List<string>> OpenTreasuresAsync(int instance, LogService logger, CancellationToken cancellationToken)
        {
            var done = new List<string>();
            await ClickAsync(instance, logger, MyTreasuresTab);
            if (!await WaitForAsync(async () => (await MatchAsync(TreasuresTextTemplate, TreasuresTextArea, ExtrasThreshold, instance, logger)).found,
                    TimeSpan.FromSeconds(4), TimeSpan.FromMilliseconds(500), cancellationToken))
            {
                logger.LogWarning("Voyage of Light: My Treasures did not open");
                return done;
            }

            for (int step = 0; step < MaxTreasureSteps; step++)
            {
                var shot = await TakeScreenshotAsync(instance, logger);
                if (shot == null || !IsTreasuresPage(shot, instance, logger)) break;
                var next = ActiveTreasureButtons(shot).FirstOrDefault();
                if (next.Name == null) break;
                if (!await PressTreasureAsync(next.Button, next.Merge, instance, logger, cancellationToken)) break;
                logger.LogInfo($"Voyage of Light: {next.Name}");
                done.Add(next.Name.ToLowerInvariant());
            }

            await ClickAsync(instance, logger, VoyageSubTab);
            await Task.Delay(1000, cancellationToken);
            return done;
        }

        private async Task<bool> PressTreasureAsync(Rectangle button, bool merge, int instance, LogService logger, CancellationToken cancellationToken)
        {
            await ClickCenterInRectAsync(instance, logger, button);
            string confirm = merge ? MergeConfirmTemplate : EnableConfirmTemplate;
            Rectangle area = merge ? MergeConfirmArea : EnableConfirmArea;
            (bool found, Rectangle rect, double confidence) popup = default;
            for (int i = 0; i < 8 && !popup.found; i++)
            {
                if (i > 0) await Task.Delay(500, cancellationToken);
                popup = await MatchAsync(confirm, area, ExtrasThreshold, instance, logger);
            }
            if (!popup.found)
            {
                logger.LogWarning($"Voyage of Light: no {(merge ? "merge" : "open")} popup ({popup.confidence:F2})");
                return false;
            }
            if (!merge)
            {
                await ClickAsync(instance, logger, EnableMaxButton);   // the open popup starts at 1
                await Task.Delay(800, cancellationToken);
            }
            await ClickCenterInRectAsync(instance, logger, popup.rect);
            await Task.Delay(3000, cancellationToken);
            await ClickAsync(instance, logger, RewardCloseTap);
            await Task.Delay(1500, cancellationToken);
            return true;
        }

        internal bool IsTreasuresPage(byte[] shot, int instance, LogService logger) =>
            MatchScreenshot(shot, TreasuresTextTemplate, TreasuresTextArea, ExtrasThreshold, instance, logger).found;

        /// <summary>The chest buttons that show in colour, in press order</summary>
        internal static List<(string Name, Rectangle Button, bool Merge)> ActiveTreasureButtons(byte[] shot) =>
            TreasureButtons.Where(b => MeanSaturation(shot, b.Button) >= MinButtonSaturation).ToList();
    }
}
