using System;
using System.Collections.Generic;
using System.Linq;
using Bot.Core.Models;

namespace Bot.Core.Services
{
    /// <summary>The bear that is due now: which instance's account, when it starts, and whether looking changed its saved settings</summary>
    public sealed record BearDue(AccountSettings? Account, DateTime? StartUtc, bool SettingsChanged);

    /// <summary>Domain rules of the Bear Hunt mode that are not UI: which bear is due, begin/end bookkeeping</summary>
    public static class BearHuntMode
    {
        /// <summary>
        /// The first account with the Bear Hunt switch on (and the task not hidden) whose bear's prep time or run has come.
        /// Looking rolls an old start forward, so <see cref="BearDue.SettingsChanged"/> says when the config needs saving.
        /// </summary>
        public static BearDue FindDue(IEnumerable<AccountSettings> accounts, DateTime nowUtc)
        {
            bool changed = false;
            foreach (var account in accounts.Where(a => a.EnabledTasks.Contains(TaskType.BearHunt) && !a.HiddenTasks.Contains(TaskType.BearHunt)))
            {
                var settings = account.BearHuntSettings;
                var before = settings.NextBearUtc;
                var start = BearHuntRunner.ActiveBear(settings, nowUtc);
                changed |= before != settings.NextBearUtc;
                if (start != null) return new BearDue(account, start, changed);
            }
            return new BearDue(null, null, changed);
        }

        /// <summary>When the bot starts the next scheduled bear (its prep time before the bear); null when none is known or it is already on</summary>
        public static DateTime? NextModeStartUtc(IEnumerable<AccountSettings> accounts, DateTime nowUtc) =>
            accounts.Where(a => a.EnabledTasks.Contains(TaskType.BearHunt) && !a.HiddenTasks.Contains(TaskType.BearHunt))
                .Select(a => a.BearHuntSettings.NextBearUtc - a.BearHuntSettings.Prep)
                .Where(t => t > nowUtc)
                .Min();

        /// <summary>The bear to run: the one asked for, else the saved one while it is on, else now (started by hand)</summary>
        public static DateTime ResolveStart(BearHuntSettings settings, DateTime? requestedUtc, DateTime nowUtc) =>
            requestedUtc ?? BearHuntRunner.ActiveBear(settings, nowUtc) ?? nowUtc;

        /// <summary>Publishes the bear's status; <paramref name="skipPrep"/> treats every character as prepared (a restart during the bear)</summary>
        public static void Begin(AccountSettings account, DateTime startUtc, string? onlyCharacter, bool skipPrep)
        {
            BearHuntStatus.Start(startUtc, startUtc + account.BearHuntSettings.EventLength);
            if (skipPrep) BearHuntRunner.Characters(account, onlyCharacter).ForEach(BearHuntStatus.MarkPrepared);
        }

        /// <summary>Clears the status and moves the saved bear on to the next one</summary>
        public static void End(BearHuntSettings settings, DateTime nowUtc)
        {
            BearHuntStatus.Stop();
            BearHuntRunner.RollForward(settings, nowUtc);
        }
    }
}
