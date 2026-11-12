using System;
using Bot.Core.Models;
using Bot.Core.Services;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// The bank vault in the Deals hub: withdraw the finished 1-day deposit and deposit again. Its own timer (24h after the
    /// last deposit), apart from the store's once-a-day pass; the code is the store task's, in its bank-only mode.
    /// </summary>
    public class CollectBankTask : CollectRewardsTask
    {
        public override TaskType TaskType => TaskType.CollectBank;
        public override string Name => "Collect Bank";

        protected override bool BankOnly => true;

        // the hub's templates are the store task's
        protected override string GetImageFolderName() => TaskType.CollectRewards.ToString().ToLowerInvariant();

        public override DateTime? GetNextRunUtc(string accountKey, AccountSettings account) => NextBankUtc(accountKey);

        /// <summary>The ▶ button runs regardless: forget the deposit time and the last visit</summary>
        public override void ClearSchedule(string accountKey) => ForgetBank(accountKey);
    }
}
