using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bot.Core.Logging;
using Bot.Core.Models;

namespace Bot.Core.Services
{
    /// <summary>
    /// The bear hunt mode's loop: asks <see cref="BearHuntPlanner"/> which character is next, runs a Bear Hunt pass on
    /// it (the pass switches character when needed and decides itself whether to prepare, send the own rally or join),
    /// until the bear is over. The GUI's Stop cancels the token.
    /// </summary>
    public static class BearHuntRunner
    {
        private static readonly TimeSpan MaxSleep = TimeSpan.FromSeconds(15);   // re-plan at least this often while waiting
        private static readonly TimeSpan AfterFailure = TimeSpan.FromSeconds(5);

        /// <param name="runPass">Runs one Bear Hunt pass on the named character (null: whoever is active)</param>
        public static async Task RunAsync(AccountSettings account, DateTime startUtc, string? onlyCharacter,
            Func<string?, CancellationToken, Task<TaskExecutionDetails>> runPass, LogService logger, CancellationToken token)
        {
            var settings = account.BearHuntSettings;
            var endsUtc = startUtc + settings.EventLength;
            string? current = CharacterRoster.GetActiveName(account.InstanceNumber);

            while (DateTime.UtcNow < endsUtc)
            {
                token.ThrowIfCancellationRequested();
                var order = BearHuntPlanner.Order(Characters(account, onlyCharacter), settings.FirstCharacter);
                var pick = BearHuntPlanner.PickCharacter(DateTime.UtcNow, startUtc, settings, order, BearHuntStatus.Get, current, BearHuntStatus.NoRalliesUntilUtc);
                if (pick.Character == null) break;
                if (pick.Wait > TimeSpan.FromSeconds(1))
                {
                    await Task.Delay(pick.Wait < MaxSleep ? pick.Wait : MaxSleep, token);
                    continue;
                }

                await RunPassAsync(account, pick.Character, current, runPass, logger, token);
                current = pick.Character;
            }

            foreach (var entry in BearHuntStatus.All()) BearHuntStatus.Set(entry.Character, BearHuntPhase.Done, $"{entry.OwnRallies} own, {entry.Joins} joined");
            logger.LogInfo("🐻 Bear Hunt is over");
        }

        private static async Task RunPassAsync(AccountSettings account, string character, string? current,
            Func<string?, CancellationToken, Task<TaskExecutionDetails>> runPass, LogService logger, CancellationToken token)
        {
            if (current == null || !CharacterRoster.SameName(current, character))
                BearHuntStatus.Set(character, BearHuntPhase.Switching);
            // never scanned: the only "character" is the instance's name, and the pass runs on whoever is active
            var result = await runPass(account.Characters.Count > 0 ? character : null, token);
            token.ThrowIfCancellationRequested();
            if (!result.Success)
            {
                logger.LogWarning($"🐻 Bear Hunt pass on {character} failed: {result.Message}");
                await Task.Delay(AfterFailure, token);
            }
        }

        /// <summary>The ticked characters (or just <paramref name="onlyCharacter"/>); the instance's name when never scanned</summary>
        private static List<string> Characters(AccountSettings account, string? onlyCharacter)
        {
            if (onlyCharacter != null) return new List<string> { onlyCharacter };
            var names = account.Characters.Where(c => c.Enabled).Select(c => c.Name).ToList();
            return names.Count > 0 ? names : new List<string> { account.AccountName };
        }

        /// <summary>
        /// The bear to run for at <paramref name="nowUtc"/>: the saved start while its prep time or the bear is on, else null.
        /// A start that is long gone is moved on by the repeat days first (the app was off during a bear).
        /// </summary>
        public static DateTime? ActiveBear(BearHuntSettings settings, DateTime nowUtc)
        {
            RollForward(settings, nowUtc);
            if (settings.NextBearUtc is not DateTime start) return null;
            var stage = BearHuntPlanner.StageAt(nowUtc, start, settings);
            // joining in late still helps; past the last own rally there is little left to do, so don't start then
            bool worthStarting = stage == BearHuntStage.Prep || (stage == BearHuntStage.Running && nowUtc <= start + settings.LastSend);
            return worthStarting ? start : null;
        }

        /// <summary>Once a bear is over: the next one <see cref="BearHuntSettings.RepeatDays"/> later (0: cleared, to be entered by hand)</summary>
        public static void RollForward(BearHuntSettings settings, DateTime nowUtc)
        {
            while (settings.NextBearUtc is DateTime start && nowUtc >= start + settings.EventLength)
            {
                settings.NextBearUtc = settings.RepeatDays > 0 ? start.AddDays(settings.RepeatDays) : null;
            }
        }
    }
}
