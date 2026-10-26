using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.ImageDetection;
using Bot.Core.Services;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// Shop > Nomadic Merchant: buys every item priced in bread, wood, stone or iron. A bought item is replaced by a
    /// new one right away, so it keeps buying until all six slots cost gems, then uses the Free Refresh when it is
    /// offered and goes again. Gem-priced items and the paid refresh are never tapped.
    /// </summary>
    public class NomadicMerchantTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.NomadicMerchant;
        public override string Name => "Nomadic Merchant";

        private LocatorService? _locatorService;

        private const string GemPriceTemplate = "gem-price.png";        // gem icon in front of a price
        private const string FreeRefreshTemplate = "free-refresh.png";  // "Free Refresh" text on the green button
        private const string MerchantTabTemplate = "merchant-tab.png";  // selected "Nomadic Merchant" tab
        private static readonly Point ShopButtonPoint = new Point(420, 1230);   // bottom bar of the base view
        private static readonly Point MerchantTabPoint = new Point(100, 1240);
        private static readonly Point BackArrowPoint = new Point(45, 40);
        private static readonly Rectangle MerchantTabArea = new Rectangle(0, 1180, 230, 100);
        private static readonly Rectangle FreeRefreshArea = new Rectangle(490, 225, 220, 80);
        // Price strips of the 2x3 item grid; tapping the strip buys, tapping the icon only shows a tooltip
        private static readonly Rectangle[] PriceAreas =
        {
            new Rectangle(32, 633, 192, 50), new Rectangle(264, 633, 192, 50), new Rectangle(496, 633, 192, 50),
            new Rectangle(32, 920, 192, 50), new Rectangle(264, 920, 192, 50), new Rectangle(496, 920, 192, 50)
        };
        private const double GemThreshold = 0.85;      // 0.97-1.00 gem price, <= 0.47 bread/wood/stone/iron price
        private const double Threshold = 0.90;         // tab and Free Refresh: 1.00 present, <= 0.18 elsewhere
        // A slot can offer resource items many times in a row, so only taps that leave the card unchanged
        // (sold out, not affordable) count against it
        private const int MaxUnchangedTapsPerSlot = 3;
        private const int MaxBuysPerRound = 150;
        private const int CardAboveTheStrip = 170;     // item icon, amount and "Left:" line above the price strip
        private const double UnchangedMeanDiff = 3.0;  // mean absolute pixel difference of the card, 0-255
        private const int MaxRefreshes = 10;
        private const int MaxPopupsPerRound = 3;

        // The stock and the free refresh reset at 00:00 UTC; once everything left costs gems, skip until then
        private static readonly ConcurrentDictionary<string, DateTime> _doneUtcDate = new();
        private bool _allGemPriced;

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            try
            {
                string accountId = AccountDetectionTask.GetCachedAccountId(account.InstanceNumber);
                string accountKey = string.IsNullOrEmpty(accountId) ? $"Account_{account.AccountName}_{account.InstanceNumber}" : accountId;
                if (_doneUtcDate.TryGetValue(accountKey, out var done) && done == DateTime.UtcNow.Date)
                    return TaskExecutionDetails.Succeeded("Already done today");

                _locatorService = new LocatorService(logger, account, TaskType);
                int instance = account.InstanceNumber;
                await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

                if (!await OpenMerchantAsync(account, logger, instance, cancellationToken))
                {
                    await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);
                    return TaskExecutionDetails.Failed("Nomadic Merchant did not open");
                }

                int bought = 0, refreshes = 0;
                bool soldOut = false;
                while (true)
                {
                    bought += await BuyResourceItemsAsync(account, logger, instance, cancellationToken);
                    if (refreshes >= MaxRefreshes) break;
                    var refresh = await MatchAsync(FreeRefreshTemplate, FreeRefreshArea, Threshold, instance, logger);
                    if (!refresh.found)
                    {
                        soldOut = _allGemPriced;
                        break;
                    }
                    await ClickCenterInRectAsync(instance, logger, refresh.rect);
                    refreshes++;
                    logger.LogInfo($"[{account.AccountName}] Nomadic Merchant: free refresh {refreshes}");
                    await Task.Delay(2000, cancellationToken);
                }

                await ClickAsync(instance, logger, BackArrowPoint);
                await Task.Delay(2000, cancellationToken);
                await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

                // only gem items and no free refresh left: nothing more until the 00:00 UTC reset
                if (soldOut) _doneUtcDate[accountKey] = DateTime.UtcNow.Date;

                string result = $"{bought} item(s) bought, {refreshes} free refresh(es)";
                logger.LogInfo($"[{account.AccountName}] Nomadic Merchant done: {result}");
                return TaskExecutionDetails.Succeeded(result);
            }
            catch (OperationCanceledException)
            {
                return TaskExecutionDetails.Failed("Task was cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error in Nomadic Merchant: {ex.Message}");
                return TaskExecutionDetails.Failed(ex.Message);
            }
        }

        /// <summary>
        /// Shop button > Nomadic Merchant tab. The shop reopens on the last tab used, so the tab is tapped when it is not selected.
        /// </summary>
        private async Task<bool> OpenMerchantAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            await ClickAsync(instance, logger, ShopButtonPoint);
            await Task.Delay(2500, cancellationToken);
            for (int i = 0; i < 2; i++)
            {
                if ((await MatchAsync(MerchantTabTemplate, MerchantTabArea, Threshold, instance, logger)).found) return true;
                await ClickAsync(instance, logger, MerchantTabPoint);
                await Task.Delay(2000, cancellationToken);
            }
            bool open = (await MatchAsync(MerchantTabTemplate, MerchantTabArea, Threshold, instance, logger)).found;
            if (!open) logger.LogWarning($"[{account.AccountName}] Nomadic Merchant: tab not found");
            return open;
        }

        /// <summary>
        /// Taps the price of the first slot not priced in gems until every slot costs gems. A slot whose card does not
        /// change after a tap MaxUnchangedTapsPerSlot times is left alone.
        /// </summary>
        private async Task<int> BuyResourceItemsAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            var unchanged = new int[PriceAreas.Length];
            int bought = 0, popups = 0;
            _allGemPriced = false;
            while (bought < MaxBuysPerRound)
            {
                // A popup (e.g. the gem purchase confirmation) dims the grid so gem prices no longer match:
                // close it and look again instead of tapping a slot under it
                if (!(await MatchAsync(MerchantTabTemplate, MerchantTabArea, Threshold, instance, logger)).found)
                {
                    if (++popups > MaxPopupsPerRound) break;
                    logger.LogWarning($"[{account.AccountName}] Nomadic Merchant: screen covered by a popup, closing it");
                    await ClickAsync(instance, logger, BackArrowPoint);
                    await Task.Delay(1500, cancellationToken);
                    if (!(await MatchAsync(MerchantTabTemplate, MerchantTabArea, Threshold, instance, logger)).found) break;
                    continue;
                }

                int slot = -1;
                for (int i = 0; i < PriceAreas.Length && slot < 0; i++)
                {
                    if (unchanged[i] >= MaxUnchangedTapsPerSlot) continue;
                    var gem = await MatchAsync(GemPriceTemplate, PriceAreas[i], GemThreshold, instance, logger);
                    if (!gem.found)
                    {
                        slot = i;
                        logger.LogInfo($"[{account.AccountName}] Nomadic Merchant: slot {i + 1} not gem-priced ({gem.confidence:F2})");
                    }
                }
                if (slot < 0)
                {
                    // a slot skipped as unchanged (e.g. not affordable yet) may be buyable later today
                    _allGemPriced = unchanged.All(u => u < MaxUnchangedTapsPerSlot);
                    break;
                }

                var before = await TakeScreenshotAsync(instance, logger);
                await ClickCenterInRectAsync(instance, logger, PriceAreas[slot]);
                // The bought item flies to the resource bar and the new one fades in
                await Task.Delay(2000, cancellationToken);
                var after = await TakeScreenshotAsync(instance, logger);

                if (CardUnchanged(before, after, PriceAreas[slot]))
                {
                    unchanged[slot]++;
                    logger.LogWarning($"[{account.AccountName}] Nomadic Merchant: slot {slot + 1} did not change after the tap ({unchanged[slot]}/{MaxUnchangedTapsPerSlot})");
                }
                else
                {
                    unchanged[slot] = 0;
                    bought++;
                    logger.LogInfo($"[{account.AccountName}] Nomadic Merchant: bought slot {slot + 1}");
                }
            }
            if (bought >= MaxBuysPerRound)
                logger.LogWarning($"[{account.AccountName}] Nomadic Merchant: stopped after {MaxBuysPerRound} buys in one round");
            return bought;
        }

        /// <summary>
        /// True when the slot's card (item, amount, price) looks the same in both screenshots.
        /// </summary>
        private static bool CardUnchanged(byte[]? before, byte[]? after, Rectangle priceArea)
        {
            if (before == null || after == null) return false;
            using var a = OpenCvSharp.Cv2.ImDecode(before, OpenCvSharp.ImreadModes.Color);
            using var b = OpenCvSharp.Cv2.ImDecode(after, OpenCvSharp.ImreadModes.Color);
            var card = new OpenCvSharp.Rect(priceArea.X, priceArea.Y - CardAboveTheStrip, priceArea.Width, priceArea.Height + CardAboveTheStrip)
                .Intersect(new OpenCvSharp.Rect(0, 0, Math.Min(a.Width, b.Width), Math.Min(a.Height, b.Height)));
            using var diff = new OpenCvSharp.Mat();
            using var cardBefore = new OpenCvSharp.Mat(a, card);
            using var cardAfter = new OpenCvSharp.Mat(b, card);
            OpenCvSharp.Cv2.Absdiff(cardBefore, cardAfter, diff);
            var mean = OpenCvSharp.Cv2.Mean(diff);
            return (mean.Val0 + mean.Val1 + mean.Val2) / 3 < UnchangedMeanDiff;
        }

        private async Task<(bool found, Rectangle rect, double confidence)> MatchAsync(
            string templateName, Rectangle area, double threshold, int instance, LogService logger)
        {
            var screenshot = await TakeScreenshotAsync(instance, logger);
            if (screenshot == null) return (false, Rectangle.Empty, 0);

            if (_templateMatcher == null) _templateMatcher = new UnifiedTemplateMatchingService(logger);
            var path = Path.Combine(ImageTemplateFolder, templateName);
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
