using System;
using System.Collections.Generic;
using System.Linq;
using Bot.Core.Models;

namespace Bot.Core.Services
{
    /// <summary>The Castle Battle that is due now: which instance's account and when it starts</summary>
    public sealed record CastleBattleDue(AccountSettings? Account, DateTime? StartUtc);

    /// <summary>
    /// When the scheduled Castle Battle mode runs (pure rules, unit-tested): from <see cref="CastleBattleSettings.PrepMinutes"/>
    /// before the Task List's start (recall, teleport) until the start + <see cref="CastleBattleSettings.MaxHours"/>.
    /// </summary>
    public static class CastleBattleMode
    {
        /// <summary>No point starting when this little of the battle is left</summary>
        private static readonly TimeSpan MinLeft = TimeSpan.FromMinutes(5);

        /// <summary>The battle the mode already ran for (in memory): stopped by hand, it doesn't start again 30 s later</summary>
        private static DateTime? _handledStartUtc;

        public static void MarkHandled(DateTime startUtc) => _handledStartUtc = startUtc;

        /// <summary>The first account with the Castle Battle on whose battle's prep time or run has come</summary>
        public static CastleBattleDue FindDue(IEnumerable<AccountSettings> accounts, DateTime nowUtc)
        {
            foreach (var account in accounts)
            {
                var settings = account.CastleBattleSettings;
                if (settings.Enabled && IsDue(settings, nowUtc) && settings.NextBattleUtc != _handledStartUtc) return new CastleBattleDue(account, settings.NextBattleUtc);
            }
            return new CastleBattleDue(null, null);
        }

        /// <summary>From the prep time until shortly before the end</summary>
        public static bool IsDue(CastleBattleSettings settings, DateTime nowUtc) =>
            settings.NextBattleUtc is DateTime start && nowUtc >= start - settings.Prep && nowUtc < start + settings.EventLength - MinLeft;

        /// <summary>When the bot starts the next known Castle Battle (its prep time); null when none is known</summary>
        public static DateTime? NextModeStartUtc(IEnumerable<AccountSettings> accounts, DateTime nowUtc) =>
            accounts.Select(a => a.CastleBattleSettings)
                .Where(s => s.Enabled && s.NextBattleUtc != null)
                .Select(s => s.NextBattleUtc!.Value - s.Prep)
                .Where(t => t > nowUtc)
                .Select(t => (DateTime?)t)
                .Min();

        /// <summary>The battle to run: the one asked for, else the saved one while it is due, else now (started by hand)</summary>
        public static DateTime ResolveStart(CastleBattleSettings settings, DateTime? requestedUtc, DateTime nowUtc) =>
            requestedUtc ?? (IsDue(settings, nowUtc) ? settings.NextBattleUtc!.Value : nowUtc);

        /// <summary>The prep (recall, teleport) runs when the mode starts before the battle; started by hand during it: no prep</summary>
        public static bool NeedsPrep(DateTime startUtc, DateTime nowUtc) => nowUtc < startUtc;

        /// <summary>Forgets the battle once it is over (the Task List gives the next one)</summary>
        public static void End(CastleBattleSettings settings, DateTime nowUtc)
        {
            if (settings.NextBattleUtc is DateTime start && nowUtc >= start + settings.EventLength - MinLeft) settings.NextBattleUtc = null;
        }
    }
}
