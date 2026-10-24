using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.ImageDetection;
using Bot.Core.Services;
using System;
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
        private const int MaxTapsPerSlot = 15;         // a slot that stays non-gem (sold out, not affordable) is left alone
        private const int MaxRefreshes = 10;

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            try
            {
                _locatorService = new LocatorService(logger, account, TaskType);
                int instance = account.InstanceNumber;
                await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

                if (!await OpenMerchantAsync(account, logger, instance, cancellationToken))
                {
                    await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);
                    return TaskExecutionDetails.Failed("Nomadic Merchant did not open");
                }

                int bought = 0, refreshes = 0;
                while (true)
                {
                    bought += await BuyResourceItemsAsync(account, logger, instance, cancellationToken);
                    if (refreshes >= MaxRefreshes) break;
                    var refresh = await MatchAsync(FreeRefreshTemplate, FreeRefreshArea, Threshold, instance, logger);
                    if (!refresh.found) break;
                    await ClickCenterInRectAsync(instance, logger, refresh.rect);
                    refreshes++;
                    logger.LogInfo($"[{account.AccountName}] Nomadic Merchant: free refresh {refreshes}");
                    await Task.Delay(2000, cancellationToken);
                }

                await ClickAsync(instance, logger, BackArrowPoint);
                await Task.Delay(2000, cancellationToken);
                await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

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
        /// Taps the price of the first slot not priced in gems until every slot costs gems (or was tapped MaxTapsPerSlot times).
        /// </summary>
        private async Task<int> BuyResourceItemsAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            var taps = new int[PriceAreas.Length];
            int bought = 0;
            while (true)
            {
                int slot = -1;
                for (int i = 0; i < PriceAreas.Length && slot < 0; i++)
                {
                    if (taps[i] >= MaxTapsPerSlot) continue;
                    if (!(await MatchAsync(GemPriceTemplate, PriceAreas[i], GemThreshold, instance, logger)).found) slot = i;
                }
                if (slot < 0) break;

                await ClickCenterInRectAsync(instance, logger, PriceAreas[slot]);
                taps[slot]++;
                bought++;
                logger.LogInfo($"[{account.AccountName}] Nomadic Merchant: bought slot {slot + 1}");
                // The bought item flies to the resource bar and the new one fades in
                await Task.Delay(2000, cancellationToken);
            }
            if (taps.Any(t => t >= MaxTapsPerSlot))
                logger.LogWarning($"[{account.AccountName}] Nomadic Merchant: a slot stayed unchanged after {MaxTapsPerSlot} taps");
            return bought;
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
