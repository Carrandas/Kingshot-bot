using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bot.Core.Logging;
using Bot.Core.Models;

namespace Bot.Core.Services
{
    /// <summary>What the Viking Vengeance passes do: send reinforcements (recall first), or call the troops home after the event</summary>
    public enum VikingPhase
    {
        Send,
        Recall
    }

    /// <summary>
    /// Live state of the Viking Vengeance mode: the phase, and per character the members it reinforced (so a pass never
    /// helps the same member twice: the Reinforce popup still offers Confirm with our own troops counted in).
    /// In memory only; cleared when the mode starts.
    /// </summary>
    public static class VikingStatus
    {
        private static readonly ConcurrentDictionary<string, HashSet<string>> _helped = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, string> _state = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Raised on every change, from the bot's thread</summary>
        public static event Action? Changed;

        public static DateTime? StartUtc { get; private set; }
        public static DateTime? EndsUtc { get; private set; }
        public static bool IsRunning => EndsUtc != null;
        public static VikingPhase Phase { get; private set; } = VikingPhase.Send;

        public static void Start(DateTime startUtc, DateTime endsUtc)
        {
            _helped.Clear();
            _state.Clear();
            StartUtc = startUtc;
            EndsUtc = endsUtc;
            Phase = VikingPhase.Send;
            Changed?.Invoke();
        }

        public static void Stop()
        {
            StartUtc = null;
            EndsUtc = null;
            Changed?.Invoke();
        }

        public static void SetPhase(VikingPhase phase)
        {
            Phase = phase;
            Changed?.Invoke();
        }

        public static bool WasHelped(string character, string member)
        {
            lock (_helped) return _helped.TryGetValue(character, out var set) && set.Contains(member);
        }

        public static void MarkHelped(string character, string member)
        {
            lock (_helped) _helped.GetOrAdd(character, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(member);
            Changed?.Invoke();
        }

        public static void SetState(string character, string state)
        {
            _state[character] = state;
            Changed?.Invoke();
        }

        /// <summary>One line per character for the GUI: its last state</summary>
        public static IReadOnlyList<(string Character, string State)> States() =>
            _state.Select(kv => (kv.Key, kv.Value)).OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The Viking Vengeance that is due now: which instance's account and when it starts</summary>
    public sealed record VikingDue(AccountSettings? Account, DateTime? StartUtc);

    /// <summary>
    /// The Viking Vengeance mode: every character recalls its marches and reinforces the online members (the main
    /// character last), the game stays open on the main character until the event is over, then every character
    /// recalls its troops (main first) and the normal cycle takes over again.
    /// </summary>
    public static class VikingVengeanceMode
    {
        /// <summary>No point starting when this little of the event is left</summary>
        private static readonly TimeSpan MinLeft = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan MaxSleep = TimeSpan.FromSeconds(30);

        /// <summary>The first account with Viking Vengeance on whose event's prep time or run has come</summary>
        public static VikingDue FindDue(IEnumerable<AccountSettings> accounts, DateTime nowUtc)
        {
            foreach (var account in accounts)
            {
                var settings = account.VikingVengeanceSettings;
                if (settings.Enabled && IsDue(settings, nowUtc)) return new VikingDue(account, settings.NextVikingUtc);
            }
            return new VikingDue(null, null);
        }

        /// <summary>From the prep time until shortly before the end</summary>
        public static bool IsDue(VikingVengeanceSettings settings, DateTime nowUtc) =>
            settings.NextVikingUtc is DateTime start && nowUtc >= start - settings.Prep && nowUtc < start + settings.EventLength - MinLeft;

        /// <summary>When the bot starts the next known Viking Vengeance (its prep time); null when none is known</summary>
        public static DateTime? NextModeStartUtc(IEnumerable<AccountSettings> accounts, DateTime nowUtc) =>
            accounts.Select(a => a.VikingVengeanceSettings)
                .Where(s => s.Enabled && s.NextVikingUtc != null)
                .Select(s => s.NextVikingUtc!.Value - s.Prep)
                .Where(t => t > nowUtc)
                .Select(t => (DateTime?)t)
                .Min();

        /// <summary>The event to run: the one asked for, else the saved one while it is due, else now (started by hand: a test)</summary>
        public static DateTime ResolveStart(VikingVengeanceSettings settings, DateTime? requestedUtc, DateTime nowUtc) =>
            requestedUtc ?? (IsDue(settings, nowUtc) ? settings.NextVikingUtc!.Value : nowUtc);

        /// <summary>The ticked characters with the main one last (it keeps the game open until the end)</summary>
        public static List<string> SendOrder(AccountSettings account, string? onlyCharacter)
        {
            var names = BearHuntRunner.Characters(account, onlyCharacter);
            string main = account.VikingVengeanceSettings.MainCharacter;
            var mainName = names.FirstOrDefault(n => main.Length > 0 && CharacterRoster.SameName(n, main));
            if (mainName == null) return names;
            names.Remove(mainName);
            names.Add(mainName);
            return names;
        }

        /// <param name="runPass">Runs one Viking Vengeance pass on the named character (null: whoever is active)</param>
        public static async Task RunAsync(AccountSettings account, DateTime startUtc, string? onlyCharacter,
            Func<string?, CancellationToken, Task<TaskExecutionDetails>> runPass, LogService logger, CancellationToken token)
        {
            var settings = account.VikingVengeanceSettings;
            var endsUtc = startUtc + settings.EventLength;
            var order = SendOrder(account, onlyCharacter);
            bool scanned = account.Characters.Count > 0;

            VikingStatus.SetPhase(VikingPhase.Send);
            foreach (var character in order)
                await RunPassAsync(scanned ? character : null, character, runPass, logger, token);

            logger.LogInfo($"⚔ Viking Vengeance: reinforcements sent, the game stays open on {order[^1]} until {endsUtc.ToLocalTime():HH:mm}");
            VikingStatus.SetState(order[^1], $"waiting until {endsUtc.ToLocalTime():HH:mm}");
            while (DateTime.UtcNow < endsUtc)
            {
                var left = endsUtc - DateTime.UtcNow;
                await Task.Delay(left < MaxSleep ? left : MaxSleep, token);
            }

            VikingStatus.SetPhase(VikingPhase.Recall);
            for (int i = order.Count - 1; i >= 0; i--)
                await RunPassAsync(scanned ? order[i] : null, order[i], runPass, logger, token);
            logger.LogInfo("⚔ Viking Vengeance is over, the troops are on their way home");
        }

        private static async Task RunPassAsync(string? passName, string character,
            Func<string?, CancellationToken, Task<TaskExecutionDetails>> runPass, LogService logger, CancellationToken token)
        {
            var result = await runPass(passName, token);
            token.ThrowIfCancellationRequested();
            if (!result.Success) logger.LogWarning($"⚔ Viking Vengeance pass on {character} failed: {result.Message}");
        }

        public static void Begin(AccountSettings account, DateTime startUtc) =>
            VikingStatus.Start(startUtc, startUtc + account.VikingVengeanceSettings.EventLength);

        /// <summary>Clears the status and forgets the event once it is over (the Task List gives the next one)</summary>
        public static void End(VikingVengeanceSettings settings, DateTime nowUtc)
        {
            VikingStatus.Stop();
            if (settings.NextVikingUtc is DateTime start && nowUtc >= start + settings.EventLength - MinLeft) settings.NextVikingUtc = null;
        }
    }
}
