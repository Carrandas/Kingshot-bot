using Bot.Core.Models;
using System;

namespace Bot.Core.Tasks
{
    /// <summary>
    /// A task with its own timer or daily gate. Lets the GUI show when the task has work again.
    /// Tasks without it simply run every cycle.
    /// </summary>
    public interface IScheduledTask
    {
        /// <summary>
        /// Earliest UTC time the task does work again for this game account (the same key its gate uses: the account ID,
        /// or "Account_{name}_{instance}"), or null when it isn't waiting. Deterministic: random spreads use their lower bound.
        /// </summary>
        DateTime? GetNextRunUtc(string accountKey, AccountSettings account);

        /// <summary>Forgets the last run for this game account, so the next run does its work (dashboard play button).</summary>
        void ClearSchedule(string accountKey);
    }

    public static class Schedule
    {
        /// <summary>The next 00:00 UTC, when the game's daily reset happens.</summary>
        public static DateTime NextUtcMidnight => DateTime.UtcNow.Date.AddDays(1);

        /// <summary>Gate key used by the tasks for the game account on an instance.</summary>
        public static string AccountKey(string? accountId, AccountSettings account) =>
            string.IsNullOrEmpty(accountId) ? $"Account_{account.AccountName}_{account.InstanceNumber}" : accountId;
    }
}
