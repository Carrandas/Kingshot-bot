using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.LDPlayer;
using Bot.Core.ImageDetection;
using Bot.Core.Services;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// Opens the in-game store (the cart/gems button at the top right) and claims the FREE
    /// "Claimable" chests that sit next to the paid bundles.
    ///
    /// SAFETY: the store sells things for real money. This task must never press a price button.
    /// It only ever taps: the store icon, the tab strip, a template-matched "Claimable" chest,
    /// the back arrow, and a blank spot on the screen border to dismiss a reward popup.
    /// Nothing is ever tapped by "searching for a button" inside the store content area.
    /// </summary>
    public class CollectRewardsTask : BaseTaskWithCommonPatterns
    {
        public override TaskType TaskType => TaskType.CollectRewards;
        public override string Name => "Collect Store Rewards";

        private LocatorService? _locatorService;

        // Template names (templates/images/collectrewards/)
        private const string StoreIconTemplate = "store-icon.png";
        private const string StoreBackTemplate = "store-back.png";
        private const string ClaimableTemplate = "claimable.png";     // "Claimable" label under the chest (Master's Collection, Hope Market)
        private const string ClaimableTemplate2 = "claimable2.png"; // same label on a purple banner (Custom Forging Set); banner colour differs per tab
        private const string DealsIconTemplate = "deals-icon.png";     // gift icon on the base view (opens the Deals hub)
        private const string DealsHeaderTemplate = "deals-header.png"; // "Deals" title bar of the hub
        private const string ClaimAllTemplate = "claim-all.png";       // green "Claim All" button (Hero Rally)
        private const string WithdrawTemplate = "withdraw.png";       // green "Withdraw" button on the Bank tab (completed deposit)
        private const string DepositAnchorTemplate = "deposit-anchor.png";             // "Deposit Period: 1 day(s)" label of an idle 5% vault
        private const string DepositDialogTitleTemplate = "deposit-dialog-title.png";  // header of the 1-day deposit dialog
        private const string DepositConfirmTemplate = "deposit-confirm.png";           // teal "Deposit" confirm button (NOT the orange TOP UP NOW)
        private const string HallHeaderTemplate = "hall-header.png";           // "Hall of Heroes" title of that Deals tab
        private const string HallPlusTemplate = "hall-plus.png";               // teal + next to the token counter (opens "A Hero's Witness")
        private const string WitnessHeaderTemplate = "witness-header.png";     // "A Hero's Witness" title
        private const string FreeButtonTemplate = "free-button.png";           // GREEN button labelled "Free"
        private const string FreeDealTemplate = "free-deal.png";           // GREEN "Free" button of the Free Weekly Deal Pack (orange price buttons never match)
        private const string FreeChestTemplate = "free-chest.png";   // chest with "Free" label (Daily Deals)
        private const string ClaimableTemplate3 = "claimable3.png";  // same label at the left under the banner chest (Kingdom Waystation)
        private const string CardsTabTemplate = "cards-tab.png";     // selected Weekly/Monthly Cards tab (icon on the white tab)
        private const string CardClaimTemplate = "card-claim.png";   // green "Claim" of a bought card's daily reward (price buttons are orange)

        // Search areas
        private static readonly Rectangle StoreIconArea = new Rectangle(560, 0, 160, 90);
        private static readonly Rectangle StoreBackArea = new Rectangle(0, 0, 100, 90);
        // The "Claimable" label under the free chest sits at slightly different heights on different tabs
        private static readonly Rectangle ClaimableArea = new Rectangle(560, 260, 160, 100);
        private static readonly Rectangle ClaimableLeftArea = new Rectangle(20, 370, 190, 90);
        // Weekly/Monthly Cards: the list is about three screens long, the Claim buttons sit under the cards that were bought
        private static readonly Rectangle CardsTabArea = new Rectangle(0, 90, 720, 100);
        private static readonly Rectangle CardClaimArea = new Rectangle(20, 190, 680, 1080);
        private const double CardsThreshold = 0.85;
        private const double CardClaimThreshold = 0.95; // green Claim 0.99, the grey "Claimed" it turns into 0.855
        private const int MaxCardScrolls = 4;
        private const int MaxCardClaims = 6;
        private static readonly Rectangle FreeDealArea = new Rectangle(380, 560, 320, 260);
        private const double FreeDealThreshold = 0.85;

        // The Daily Deals "Free" chest sits at the left of the banner
        private static readonly Rectangle FreeChestArea = new Rectangle(20, 340, 180, 160);

        // Deals hub
        private static readonly Rectangle DealsIconArea = new Rectangle(560, 150, 160, 200);
        private static readonly Rectangle DealsHeaderArea = new Rectangle(60, 0, 200, 90);
        private static readonly Rectangle ClaimAllArea = new Rectangle(100, 1100, 500, 170);
        // Free (left) column of the reward tracks. Purchase buttons live in the right-hand column / banner,
        // so glowing rewards are only ever looked for here.
        private static readonly Rectangle GlowArea = new Rectangle(0, 540, 310, 720);
        private static readonly int[] HubTabSlotX = { 160, 360, 560 };
        private const int HubTabY = 140;
        private const int MaxHubPages = 5;
        private const int MaxHubClaims = 8;
        private const int MaxGlowTapsPerTab = 4;
        private const double DealsIconThreshold = 0.80;
        private const double DealsHeaderThreshold = 0.80;
        private const double ClaimAllThreshold = 0.80;
        // Only the GREEN Withdraw button is ever tapped; the gray Deposit buttons spend gems and score <= 0.62 on this template
        private const double WithdrawThreshold = 0.85;

        // Bank: withdraw the finished 1-day vault, then deposit again. The vault lasts 24h, so the step has a 24h cooldown.
        // SAFETY: the dialog also holds an orange "TOP UP NOW" button that buys gems with real money; it is never tapped.
        private static readonly TimeSpan BankInterval = TimeSpan.FromHours(24);
        private static readonly Rectangle DepositAnchorArea = new Rectangle(0, 560, 720, 70);
        private static readonly Rectangle DepositDialogTitleArea = new Rectangle(0, 300, 720, 90);
        private static readonly Rectangle DepositConfirmArea = new Rectangle(0, 800, 720, 170);
        private const double DepositAnchorThreshold = 0.90;       // 1.0 on an idle vault, <= 0.71 on completed / in-deposit states
        private const double DepositDialogTitleThreshold = 0.85;  // 1.0 on the 1-day dialog, <= 0.22 elsewhere
        private const double DepositConfirmThreshold = 0.85;      // 1.0 on the teal button, 0.45 on TOP UP NOW
        // The Deposit button sits this far below / left of the "Deposit Period" label centre
        private const int DepositButtonDx = -12;
        private const int DepositButtonDy = 259;

        private static readonly ConcurrentDictionary<string, DateTime> _lastBankDeposit = new();
        private static readonly string BankTimeFile = Path.Combine(AppContext.BaseDirectory, "data", "collect_rewards_bank_times.json");
        private static bool _bankTimesLoaded;
        private static readonly Rectangle WithdrawArea = new Rectangle(0, 540, 720, 720);

        // The free store/hub rewards refresh at the daily reset (00:00 UTC), so a complete pass is done once per UTC day.
        // A pass that was cut short (left the store, claim cap, hub did not open) is not recorded and runs again next cycle.
        private static readonly ConcurrentDictionary<string, DateTime> _lastCompleteRun = new();
        private static readonly string DailyRunFile = Path.Combine(AppContext.BaseDirectory, "data", "collect_rewards_daily_runs.json");
        private static bool _dailyRunsLoaded;
        private bool _runComplete;

        // Hall of Heroes -> "A Hero's Witness": the daily free chest and the free attempt.
        // SAFETY: after the free attempt is used the same button turns into an orange gem-cost button (150 gems).
        // Only a button that matches the GREEN "Free" template is tapped, and it is re-checked before every single tap.
        private static readonly Rectangle HallHeaderArea = new Rectangle(0, 190, 400, 90);
        private static readonly Rectangle HallPlusArea = new Rectangle(200, 470, 140, 90);
        private static readonly Rectangle WitnessHeaderArea = new Rectangle(0, 0, 500, 90);
        private static readonly Rectangle FreeButtonArea = new Rectangle(380, 190, 260, 330);
        private const double HallThreshold = 0.85;
        private const double WitnessThreshold = 0.85;
        private const double FreeButtonThreshold = 0.85;
        private const int MaxWitnessClaims = 3;

        // Tap targets that are known to be safe (no purchase possible)
        private static readonly Rectangle BackArrowClickArea = new Rectangle(24, 20, 50, 40);
        private static readonly Point PopupDismissPoint = new Point(12, 700); // wood border left of the store panel

        // Tab strip (three visible tab slots; the strip scrolls sideways)
        private const int TabY = 140;
        private static readonly int[] TabSlotX = { 140, 335, 535 };
        private const int MaxTabPages = 6; // the strip scrolls ~2 tabs per swipe and has about 12 tabs
        private const int MaxClaimsPerRun = 6;
        // A slow drag scrolls the tab strip by about the distance dragged (~2 tabs). A fast swipe makes the
        // strip fling several tabs further and skip some, so the duration must stay long.
        private const int TabSwipeDurationMs = 2500;

        // The gem counter overlaps the icon, so the score drops to ~0.82 as the count changes; only the top-right corner is searched
        private const double StoreIconThreshold = 0.78;
        private const double StoreBackThreshold = 0.85;
        private const double FreeChestThreshold = 0.70;  // >=0.95 where present, <=0.33 elsewhere
        private const double ClaimableThreshold = 0.70; // label text: >=0.80 where present, <=0.20 elsewhere

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            try
            {
                _locatorService = new LocatorService(logger, account, TaskType);
                int instance = account.InstanceNumber;

                string accountKey = GetAccountKey(account, instance);
                if (AlreadyRanToday(accountKey))
                {
                    logger.LogInfo($"[{account.AccountName}] Collect Rewards already done today (UTC), skipping");
                    return TaskExecutionDetails.Succeeded("Skipped - already collected today");
                }
                _runComplete = true;

                logger.LogInfo($"[{account.AccountName}] Collect Rewards: navigating to base view...");
                if (!await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken))
                {
                    return TaskExecutionDetails.Failed("Failed to navigate to base view");
                }

                // Step 1: open the store
                logger.LogInfo($"[{account.AccountName}] Looking for the store icon...");
                int claims = 0, cardClaims = 0;
                // Reward animations can cover the icon for a moment
                var icon = await MatchAsync(StoreIconTemplate, StoreIconArea, StoreIconThreshold, instance, logger);
                for (int i = 0; i < 3 && !icon.found; i++)
                {
                    await Task.Delay(1500, cancellationToken);
                    icon = await MatchAsync(StoreIconTemplate, StoreIconArea, StoreIconThreshold, instance, logger);
                }
                if (!icon.found)
                {
                    logger.LogWarning($"[{account.AccountName}] Store icon not found ({icon.confidence:F2}), skipping the store");
                    claims += await CollectDealsHubAsync(account, logger, instance, cancellationToken);
                    return TaskExecutionDetails.Succeeded($"Claimed {claims} free reward(s), store skipped");
                }
                await ClickCenterInRectAsync(instance, logger, icon.rect);

                if (!await WaitForStoreAsync(instance, logger, cancellationToken, seconds: 6))
                {
                    logger.LogWarning($"[{account.AccountName}] Store did not open");
                    return TaskExecutionDetails.Failed("Store did not open");
                }

                // The store reopens on the tab used last, so scroll the strip back to its first tab before scanning right
                for (int i = 0; i < 4; i++)
                {
                    await SwipeAsync(instance, logger, new Point(150, 150), new Point(620, 150), 400);
                    await Task.Delay(500, cancellationToken);
                }

                // Step 2: walk the tabs and claim free chests
                for (int page = 0; page < MaxTabPages && claims < MaxClaimsPerRun; page++)
                {
                    foreach (int slotX in TabSlotX)
                    {
                        await WaitIfPausedAsync(cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (claims >= MaxClaimsPerRun) break;

                        await ClickRandomInRectAsync(instance, logger, new Rectangle(slotX - 40, TabY - 12, 80, 24));
                        await Task.Delay(1200, cancellationToken);

                        // Some tabs open a "Rewards Upgraded / Tap anywhere to exit" pitch popup that hides the store header:
                        // dismiss it first, and only give up if we are still not inside the store afterwards
                        if (!await IsInStoreAsync(instance, logger))
                        {
                            logger.LogInfo($"[{account.AccountName}] Store header hidden after opening a tab, trying to dismiss a popup...");
                            await DismissPopupIfAnyAsync(instance, logger, cancellationToken);
                        }

                        // Never act unless we are definitely still inside the store
                        if (!await IsInStoreAsync(instance, logger))
                        {
                            logger.LogWarning($"[{account.AccountName}] Left the store unexpectedly, stopping tab scan");
                            _runComplete = false;
                            page = MaxTabPages;
                            break;
                        }

                        // Two kinds of free item; each is claimed only when its template matches
                        var claimable = await MatchAsync(ClaimableTemplate, ClaimableArea, ClaimableThreshold, instance, logger);
                        if (!claimable.found)
                            claimable = await MatchAsync(ClaimableTemplate2, ClaimableArea, ClaimableThreshold, instance, logger);
                        if (!claimable.found)
                            claimable = await MatchAsync(ClaimableTemplate3, ClaimableLeftArea, ClaimableThreshold, instance, logger);
                        if (claimable.found)
                        {
                            logger.LogInfo($"[{account.AccountName}] Free claimable chest found (confidence {claimable.confidence:F3}), claiming...");
                            // The chest icon sits just above its "Claimable" label; tap the chest
                            await ClickAsync(instance, logger, new Point(claimable.rect.X + claimable.rect.Width / 2, claimable.rect.Y - 25));
                            claims++;
                            await FinishClaimAsync(instance, logger, claims, cancellationToken);
                            continue;
                        }

                        var freeChest = await MatchAsync(FreeChestTemplate, FreeChestArea, FreeChestThreshold, instance, logger);
                        if (freeChest.found)
                        {
                            logger.LogInfo($"[{account.AccountName}] Free daily chest found (confidence {freeChest.confidence:F3}), claiming...");
                            await ClickAsync(instance, logger, new Point(freeChest.rect.X + freeChest.rect.Width / 2, freeChest.rect.Y + freeChest.rect.Height / 2 - 10));
                            claims++;
                            await FinishClaimAsync(instance, logger, claims, cancellationToken);
                        }

                        var freeDeal = await MatchAsync(FreeDealTemplate, FreeDealArea, FreeDealThreshold, instance, logger);
                        if (freeDeal.found)
                        {
                            logger.LogInfo($"[{account.AccountName}] Free weekly deal found (confidence {freeDeal.confidence:F3}), claiming...");
                            await ClickCenterInRectAsync(instance, logger, freeDeal.rect);
                            claims++;
                            await FinishClaimAsync(instance, logger, claims, cancellationToken);
                        }

                        if ((await MatchAsync(CardsTabTemplate, CardsTabArea, CardsThreshold, instance, logger)).found)
                            cardClaims += await ClaimCardsAsync(account, logger, instance, cancellationToken);
                    }

                    if (page < MaxTabPages - 1 && claims < MaxClaimsPerRun)
                    {
                        var before = await TabStripFingerprintAsync(instance, logger);
                        await SwipeAsync(instance, logger, new Point(600, 150), new Point(240, 150), TabSwipeDurationMs);
                        await Task.Delay(800, cancellationToken);
                        if (SameTabStrip(before, await TabStripFingerprintAsync(instance, logger)))
                        {
                            logger.LogInfo($"[{account.AccountName}] Store tab strip reached its end after page {page + 1}");
                            break;
                        }
                    }
                }

                // Step 3: leave the store
                for (int i = 0; i < 3 && await IsInStoreAsync(instance, logger); i++)
                {
                    await ClickRandomInRectAsync(instance, logger, BackArrowClickArea);
                    await Task.Delay(1000, cancellationToken);
                }
                await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

                // the store cap may have left free items unclaimed; the hub has its own cap
                if (claims >= MaxClaimsPerRun) _runComplete = false;

                // Step 4: the Deals hub (Sign-in calendar, Hero Rally, Frontier Encounter, ...)
                claims += cardClaims + await CollectDealsHubAsync(account, logger, instance, cancellationToken);

                if (_runComplete)
                {
                    _lastCompleteRun[accountKey] = DateTime.UtcNow;
                    SaveDailyRuns();
                }
                else
                {
                    logger.LogInfo($"[{account.AccountName}] Collect Rewards pass was incomplete, it runs again next cycle");
                }

                logger.LogInfo($"[{account.AccountName}] Collect Rewards done: {claims} free reward(s) claimed");
                return TaskExecutionDetails.Succeeded($"Claimed {claims} free reward(s)");
            }
            catch (OperationCanceledException)
            {
                return TaskExecutionDetails.Failed("Task was cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error in Collect Rewards: {ex.Message}");
                return TaskExecutionDetails.Failed(ex.Message);
            }
        }

        private async Task FinishClaimAsync(int instance, LogService logger, int claimNumber, CancellationToken cancellationToken, bool inHub = false)
        {
            await Task.Delay(1500, cancellationToken);
            await SaveDebugScreenshotAsync(instance, logger, $"collectrewards_claim_{claimNumber}.png");
            await DismissPopupIfAnyAsync(instance, logger, cancellationToken, inHub);
        }

        /// <summary>
        /// Weekly/Monthly Cards tab: taps every green "Claim" (the daily reward of a bought card), scrolling down the list.
        /// Only the green Claim template is ever tapped; the orange price buttons do not match it.
        /// </summary>
        private async Task<int> ClaimCardsAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            int claims = 0;
            for (int scroll = 0; scroll <= MaxCardScrolls && claims < MaxCardClaims; scroll++)
            {
                var claim = await MatchAsync(CardClaimTemplate, CardClaimArea, CardClaimThreshold, instance, logger);
                while (claim.found && claims < MaxCardClaims)
                {
                    logger.LogInfo($"[{account.AccountName}] Card daily reward found (confidence {claim.confidence:F3}), claiming...");
                    await ClickCenterInRectAsync(instance, logger, claim.rect);
                    claims++;
                    await FinishClaimAsync(instance, logger, 200 + claims, cancellationToken);
                    if (!await IsInStoreAsync(instance, logger)) return claims;
                    claim = await MatchAsync(CardClaimTemplate, CardClaimArea, CardClaimThreshold, instance, logger);
                }
                if (scroll < MaxCardScrolls)
                {
                    await SwipeAsync(instance, logger, new Point(360, 1100), new Point(360, 400), 1000);
                    await Task.Delay(1200, cancellationToken);
                }
            }
            return claims;
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

        private async Task<bool> IsInStoreAsync(int instance, LogService logger)
        {
            var back = await MatchAsync(StoreBackTemplate, StoreBackArea, StoreBackThreshold, instance, logger);
            return back.found;
        }

        private async Task<bool> WaitForStoreAsync(int instance, LogService logger, CancellationToken cancellationToken, int seconds)
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < end)
            {
                if (await IsInStoreAsync(instance, logger)) return true;
                await Task.Delay(500, cancellationToken);
            }
            return false;
        }

        /// <summary>
        /// After a claim the game may show a "Rewards - tap anywhere to exit" popup. The popup dims the
        /// store, so the back-arrow template stops matching; in that case tap a blank border spot.
        /// </summary>
        private async Task DismissPopupIfAnyAsync(int instance, LogService logger, CancellationToken cancellationToken, bool inHub = false)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (inHub ? await IsInHubAsync(instance, logger) : await IsInStoreAsync(instance, logger)) return;

                logger.LogInfo("Popup detected after claim, dismissing...");
                await ClickAsync(instance, logger, PopupDismissPoint);
                await Task.Delay(1000, cancellationToken);
            }
        }

        /// <summary>
        /// Bank tab: withdraw a finished deposit, then deposit again in the 1-day 5% vault. Runs at most once per 24h.
        /// Only template-matched elements are tapped, and the dialog header is verified before confirming.
        /// </summary>
        private async Task<int> HandleBankAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            LoadBankTimes();
            string bankKey = GetAccountKey(account, instance);
            if (_lastBankDeposit.TryGetValue(bankKey, out var last) && DateTime.UtcNow - last < BankInterval)
                return 0;

            int actions = 0;

            var withdraw = await MatchAsync(WithdrawTemplate, WithdrawArea, WithdrawThreshold, instance, logger);
            if (withdraw.found)
            {
                logger.LogInfo($"[{account.AccountName}] Bank Withdraw button found (confidence {withdraw.confidence:F3}), withdrawing...");
                await ClickCenterInRectAsync(instance, logger, withdraw.rect);
                actions++;
                await FinishClaimAsync(instance, logger, 100 + actions, cancellationToken, inHub: true);
                await Task.Delay(800, cancellationToken);
            }

            // An idle 1-day vault shows "Deposit Period: 1 day(s)" above its Deposit button
            var anchor = await MatchAsync(DepositAnchorTemplate, DepositAnchorArea, DepositAnchorThreshold, instance, logger);
            if (!anchor.found) return actions;

            logger.LogInfo($"[{account.AccountName}] Idle 1-day bank vault found, opening the deposit dialog...");
            await ClickAsync(instance, logger, new Point(
                anchor.rect.X + anchor.rect.Width / 2 + DepositButtonDx,
                anchor.rect.Y + anchor.rect.Height / 2 + DepositButtonDy));
            await Task.Delay(1500, cancellationToken);

            var title = await MatchAsync(DepositDialogTitleTemplate, DepositDialogTitleArea, DepositDialogTitleThreshold, instance, logger);
            if (!title.found)
            {
                logger.LogWarning($"[{account.AccountName}] Deposit dialog for the 1-day vault not recognised, not confirming");
                await DismissPopupIfAnyAsync(instance, logger, cancellationToken, inHub: true);
                return actions;
            }

            var confirm = await MatchAsync(DepositConfirmTemplate, DepositConfirmArea, DepositConfirmThreshold, instance, logger);
            if (!confirm.found)
            {
                logger.LogWarning($"[{account.AccountName}] Teal Deposit button not found in the dialog, not confirming");
                return actions;
            }

            logger.LogInfo($"[{account.AccountName}] Confirming the deposit (confidence {confirm.confidence:F3})...");
            await ClickCenterInRectAsync(instance, logger, confirm.rect);
            await Task.Delay(2000, cancellationToken);
            actions++;

            _lastBankDeposit[bankKey] = DateTime.UtcNow;
            SaveBankTimes();
            logger.LogInfo($"[{account.AccountName}] Bank deposit done, next bank visit in {BankInterval.TotalHours:F0}h");
            await DismissPopupIfAnyAsync(instance, logger, cancellationToken, inHub: true);
            return actions;
        }

        // Keyed by game account, so a character switch on the same instance gets its own cooldowns
        private static string GetAccountKey(AccountSettings account, int instance)
        {
            string accountId = AccountDetectionTask.GetCachedAccountId(instance);
            return string.IsNullOrEmpty(accountId) ? $"Account_{account.AccountName}_{instance}" : accountId;
        }

        private static bool AlreadyRanToday(string accountKey)
        {
            LoadDailyRuns();
            LoadBankTimes();
            if (!_lastCompleteRun.TryGetValue(accountKey, out var lastRun) || lastRun.Date != DateTime.UtcNow.Date)
                return false;

            // The 24h bank vault can come due later in the same UTC day: allow one more pass for it,
            // otherwise the deposit slips to the next day's pass
            if (_lastBankDeposit.TryGetValue(accountKey, out var lastDeposit))
            {
                var bankDue = lastDeposit + BankInterval;
                if (DateTime.UtcNow >= bankDue && lastRun < bankDue) return false;
            }
            return true;
        }

        private static void LoadDailyRuns()
        {
            if (_dailyRunsLoaded) return;
            _dailyRunsLoaded = true;
            try
            {
                if (!File.Exists(DailyRunFile)) return;
                var times = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(DailyRunFile));
                if (times == null) return;
                foreach (var kvp in times) _lastCompleteRun[kvp.Key] = kvp.Value;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CollectRewardsTask] Error loading daily runs: {ex.Message}");
            }
        }

        private static void SaveDailyRuns()
        {
            try
            {
                var dir = Path.GetDirectoryName(DailyRunFile);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(DailyRunFile, JsonSerializer.Serialize(_lastCompleteRun.ToDictionary(k => k.Key, k => k.Value)));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CollectRewardsTask] Error saving daily runs: {ex.Message}");
            }
        }

        private static void LoadBankTimes()
        {
            if (_bankTimesLoaded) return;
            _bankTimesLoaded = true;
            try
            {
                if (!File.Exists(BankTimeFile)) return;
                var times = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(BankTimeFile));
                if (times == null) return;
                foreach (var kvp in times) _lastBankDeposit[kvp.Key] = kvp.Value;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CollectRewardsTask] Error loading bank times: {ex.Message}");
            }
        }

        private static void SaveBankTimes()
        {
            try
            {
                var dir = Path.GetDirectoryName(BankTimeFile);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(BankTimeFile, JsonSerializer.Serialize(_lastBankDeposit.ToDictionary(k => k.Key, k => k.Value)));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CollectRewardsTask] Error saving bank times: {ex.Message}");
            }
        }

        /// <summary>
        /// On the Hall of Heroes tab: opens "A Hero's Witness" via the + button and taps every green "Free" button once.
        /// </summary>
        private async Task<int> HandleHallOfHeroesAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            var hall = await MatchAsync(HallHeaderTemplate, HallHeaderArea, HallThreshold, instance, logger);
            if (!hall.found) return 0;

            var plus = await MatchAsync(HallPlusTemplate, HallPlusArea, HallThreshold, instance, logger);
            if (!plus.found) return 0;

            logger.LogInfo($"[{account.AccountName}] Hall of Heroes tab: opening A Hero's Witness...");
            await ClickCenterInRectAsync(instance, logger, plus.rect);

            var end = DateTime.UtcNow.AddSeconds(5);
            bool open = false;
            while (DateTime.UtcNow < end && !open)
            {
                await Task.Delay(500, cancellationToken);
                open = (await MatchAsync(WitnessHeaderTemplate, WitnessHeaderArea, WitnessThreshold, instance, logger)).found;
            }
            if (!open)
            {
                logger.LogWarning($"[{account.AccountName}] A Hero's Witness did not open");
                return 0;
            }

            int claims = 0;
            for (int i = 0; i < MaxWitnessClaims; i++)
            {
                // re-checked before EVERY tap: once used, the button turns into an orange gem-cost button that does not match
                var free = await MatchAsync(FreeButtonTemplate, FreeButtonArea, FreeButtonThreshold, instance, logger);
                if (!free.found) break;

                logger.LogInfo($"[{account.AccountName}] Free button found in A Hero's Witness (confidence {free.confidence:F3}), claiming...");
                await ClickCenterInRectAsync(instance, logger, free.rect);
                claims++;
                await Task.Delay(1500, cancellationToken);
                await SaveDebugScreenshotAsync(instance, logger, $"collectrewards_witness_{claims}.png");

                // reward popup ("tap anywhere to exit") hides the page header -> tap the blank border
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if ((await MatchAsync(WitnessHeaderTemplate, WitnessHeaderArea, WitnessThreshold, instance, logger)).found) break;
                    logger.LogInfo("Popup detected after claim, dismissing...");
                    await ClickAsync(instance, logger, PopupDismissPoint);
                    await Task.Delay(1000, cancellationToken);
                }
            }

            // back to the Deals hub
            for (int i = 0; i < 3 && !await IsInHubAsync(instance, logger); i++)
            {
                await ClickRandomInRectAsync(instance, logger, BackArrowClickArea);
                await Task.Delay(1000, cancellationToken);
            }
            return claims;
        }

        private async Task<bool> IsInHubAsync(int instance, LogService logger)
        {
            var header = await MatchAsync(DealsHeaderTemplate, DealsHeaderArea, DealsHeaderThreshold, instance, logger);
            return header.found;
        }

        /// <summary>
        /// Opens the Deals hub and, on every tab, claims what is free: the green "Claim All" button
        /// (template-matched) and any glowing reward in the Free column. Never taps price/unlock buttons.
        /// </summary>
        private async Task<int> CollectDealsHubAsync(AccountSettings account, LogService logger, int instance, CancellationToken cancellationToken)
        {
            int claims = 0;
            var icon = await MatchAsync(DealsIconTemplate, DealsIconArea, DealsIconThreshold, instance, logger);
            if (!icon.found)
            {
                logger.LogInfo($"[{account.AccountName}] Deals icon not found, skipping the Deals hub");
                _runComplete = false;
                return 0;
            }
            await ClickCenterInRectAsync(instance, logger, icon.rect);

            var end = DateTime.UtcNow.AddSeconds(6);
            bool open = false;
            while (DateTime.UtcNow < end && !open)
            {
                open = await IsInHubAsync(instance, logger);
                if (!open) await Task.Delay(500, cancellationToken);
            }
            if (!open)
            {
                logger.LogWarning($"[{account.AccountName}] Deals hub did not open");
                _runComplete = false;
                return 0;
            }

            // Scroll the tab strip back to its start (it opens on the tab that has something to claim)
            for (int i = 0; i < 3; i++)
            {
                await SwipeAsync(instance, logger, new Point(150, 150), new Point(620, 150), 400);
                await Task.Delay(500, cancellationToken);
            }

            for (int page = 0; page < MaxHubPages && claims < MaxHubClaims; page++)
            {
                foreach (int slotX in HubTabSlotX)
                {
                    await WaitIfPausedAsync(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (claims >= MaxHubClaims) break;

                    await ClickRandomInRectAsync(instance, logger, new Rectangle(slotX - 40, HubTabY - 12, 80, 24));
                    await Task.Delay(1200, cancellationToken);

                    if (!await IsInHubAsync(instance, logger))
                    {
                        logger.LogWarning($"[{account.AccountName}] Left the Deals hub unexpectedly, stopping");
                        _runComplete = false;
                        page = MaxHubPages;
                        break;
                    }

                    var claimAll = await MatchAsync(ClaimAllTemplate, ClaimAllArea, ClaimAllThreshold, instance, logger);
                    if (claimAll.found)
                    {
                        logger.LogInfo($"[{account.AccountName}] Claim All button found (confidence {claimAll.confidence:F3}), claiming...");
                        await ClickCenterInRectAsync(instance, logger, claimAll.rect);
                        claims++;
                        await FinishClaimAsync(instance, logger, 100 + claims, cancellationToken, inHub: true);
                    }

                    claims += await HandleBankAsync(account, logger, instance, cancellationToken);

                    for (int tap = 0; tap < MaxGlowTapsPerTab && claims < MaxHubClaims; tap++)
                    {
                        var glow = await FindGlowingRewardsAsync(instance, logger);
                        if (glow.Count == 0) break;

                        var target = glow[0];
                        logger.LogInfo($"[{account.AccountName}] Free reward (glowing) at ({target.X},{target.Y}), claiming...");
                        await ClickAsync(instance, logger, new Point(target.X + target.Width / 2, target.Y + target.Height / 2));
                        claims++;
                        await FinishClaimAsync(instance, logger, 100 + claims, cancellationToken, inHub: true);
                    }

                    if (claims < MaxHubClaims)
                        claims += await HandleHallOfHeroesAsync(account, logger, instance, cancellationToken);
                }

                if (page < MaxHubPages - 1 && claims < MaxHubClaims)
                {
                    var before = await TabStripFingerprintAsync(instance, logger);
                    await SwipeAsync(instance, logger, new Point(620, 150), new Point(260, 150), TabSwipeDurationMs);
                    await Task.Delay(800, cancellationToken);
                    if (SameTabStrip(before, await TabStripFingerprintAsync(instance, logger)))
                    {
                        logger.LogInfo($"[{account.AccountName}] Deals tab strip reached its end after page {page + 1}");
                        break;
                    }
                }
            }

            for (int i = 0; i < 3 && await IsInHubAsync(instance, logger); i++)
            {
                await ClickRandomInRectAsync(instance, logger, BackArrowClickArea);
                await Task.Delay(1000, cancellationToken);
            }
            await _locatorService!.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);
            if (claims >= MaxHubClaims) _runComplete = false;
            return claims;
        }

        /// <summary>
        /// Finds rewards with the pale-yellow "ready to claim" glow ring in the Free column.
        /// A ring is hollow, so filled yellow shapes (buttons) are rejected by the fill-ratio test.
        /// </summary>
        private async Task<List<Rectangle>> FindGlowingRewardsAsync(int instance, LogService logger)
        {
            var result = new List<Rectangle>();
            var shot = await TakeScreenshotAsync(instance, logger);
            if (shot == null) return result;

            try
            {
                using var ms = new MemoryStream(shot);
                using var bmp = new System.Drawing.Bitmap(ms);
                using var raw = OpenCvSharp.Extensions.BitmapConverter.ToMat(bmp);
                using var bgr = new OpenCvSharp.Mat();
                if (raw.Channels() == 4) OpenCvSharp.Cv2.CvtColor(raw, bgr, OpenCvSharp.ColorConversionCodes.BGRA2BGR);
                else raw.CopyTo(bgr);

                using var hsv = new OpenCvSharp.Mat();
                OpenCvSharp.Cv2.CvtColor(bgr, hsv, OpenCvSharp.ColorConversionCodes.BGR2HSV);
                using var mask = new OpenCvSharp.Mat();
                OpenCvSharp.Cv2.InRange(hsv, new OpenCvSharp.Scalar(22, 45, 250), new OpenCvSharp.Scalar(32, 255, 255), mask);

                var roi = new OpenCvSharp.Rect(GlowArea.X, GlowArea.Y,
                    Math.Min(GlowArea.Width, mask.Width - GlowArea.X), Math.Min(GlowArea.Height, mask.Height - GlowArea.Y));
                using var region = new OpenCvSharp.Mat(mask, roi);
                using var closed = new OpenCvSharp.Mat();
                using var kernel = OpenCvSharp.Cv2.GetStructuringElement(OpenCvSharp.MorphShapes.Rect, new OpenCvSharp.Size(5, 5));
                OpenCvSharp.Cv2.MorphologyEx(region, closed, OpenCvSharp.MorphTypes.Close, kernel);

                using var labels = new OpenCvSharp.Mat();
                using var stats = new OpenCvSharp.Mat();
                using var centroids = new OpenCvSharp.Mat();
                int count = OpenCvSharp.Cv2.ConnectedComponentsWithStats(closed, labels, stats, centroids);
                var candidates = new List<(Rectangle rect, int area)>();
                for (int i = 1; i < count; i++)
                {
                    int left = stats.At<int>(i, 0), top = stats.At<int>(i, 1);
                    int w = stats.At<int>(i, 2), h = stats.At<int>(i, 3), area = stats.At<int>(i, 4);
                    if (w < 70 || w > 140 || h < 70 || h > 140) continue;
                    double fill = (double)area / (w * h);
                    if (fill > 0.30) continue; // filled shape (e.g. a button), not a hollow glow ring
                    candidates.Add((new Rectangle(left + GlowArea.X, top + GlowArea.Y, w, h), area));
                }

                // merge overlapping detections of the same ring
                foreach (var c in candidates.OrderByDescending(c => c.area))
                {
                    var center = new Point(c.rect.X + c.rect.Width / 2, c.rect.Y + c.rect.Height / 2);
                    bool dup = result.Any(r => Math.Abs(r.X + r.Width / 2 - center.X) < 45 && Math.Abs(r.Y + r.Height / 2 - center.Y) < 45);
                    if (!dup) result.Add(c.rect);
                }
                result = result.OrderBy(r => r.Y).ToList();
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Glow detection failed: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// A coarse grey-level sample of the tab strip. Two equal fingerprints after a swipe mean the strip did not move,
        /// i.e. it is already at its end, so scanning further pages would only re-tap the same tabs.
        /// </summary>
        private async Task<int[]?> TabStripFingerprintAsync(int instance, LogService logger)
        {
            var shot = await TakeScreenshotAsync(instance, logger);
            if (shot == null) return null;
            try
            {
                using var ms = new MemoryStream(shot);
                using var bmp = new Bitmap(ms);
                var samples = new List<int>();
                for (int y = 100; y < 180 && y < bmp.Height; y += 6)
                    for (int x = 4; x < bmp.Width - 4; x += 6)
                    {
                        var c = bmp.GetPixel(x, y);
                        samples.Add((c.R + c.G + c.B) / 3);
                    }
                return samples.ToArray();
            }
            catch
            {
                return null;
            }
        }

        private static bool SameTabStrip(int[]? a, int[]? b)
        {
            if (a == null || b == null || a.Length != b.Length || a.Length == 0) return false;
            long diff = 0;
            for (int i = 0; i < a.Length; i++) diff += Math.Abs(a[i] - b[i]);
            return (double)diff / a.Length < 6.0; // moved strips differ by far more than this; unchanged ones differ by ~0-2
        }

        private async Task SaveDebugScreenshotAsync(int instance, LogService logger, string fileName)
        {
            try
            {
                var shot = await TakeScreenshotAsync(instance, logger);
                if (shot == null) return;
                var dir = Path.Combine(AppContext.BaseDirectory, "logs");
                Directory.CreateDirectory(dir);
                await File.WriteAllBytesAsync(Path.Combine(dir, fileName), shot);
            }
            catch
            {
                // debug aid only
            }
        }
    }
}
