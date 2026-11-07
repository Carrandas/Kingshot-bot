using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Bot.Core.Services
{
    /// <summary>Where a character is in the castle battle loop (the dashboard shows it per character)</summary>
    public enum CastleBattlePhase
    {
        Starting,
        Healing,
        WaitingForRally,
        JoiningRally,
        JoinedWaitingToMarch,
        MarchingToTarget,
        Garrisoned,
        MarchingBack,
        SendingDefense,
        Switching
    }

    /// <summary>
    /// Live state of the castle battle mode: the phase of each character and the counters, for the Bot tab.
    /// In memory only; cleared when a battle starts.
    /// </summary>
    public static class CastleBattleStatus
    {
        public sealed record Entry(string Character, CastleBattlePhase Phase, string Detail, DateTime UpdatedUtc, int RalliesJoined, int HealRounds);

        private static readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Raised on every change, from the bot's thread</summary>
        public static event Action? Changed;

        public static DateTime? StartedUtc { get; private set; }
        public static DateTime? EndsUtc { get; private set; }
        public static bool IsRunning => StartedUtc != null;

        public static void Start(DateTime endsUtc)
        {
            _entries.Clear();
            StartedUtc = DateTime.UtcNow;
            EndsUtc = endsUtc;
            Changed?.Invoke();
        }

        public static void Stop()
        {
            StartedUtc = null;
            EndsUtc = null;
            Changed?.Invoke();
        }

        public static void Set(string character, CastleBattlePhase phase, string detail = "")
        {
            _entries.AddOrUpdate(character,
                c => new Entry(c, phase, detail, DateTime.UtcNow, 0, 0),
                (_, e) => e with { Phase = phase, Detail = detail, UpdatedUtc = DateTime.UtcNow });
            Changed?.Invoke();
        }

        public static void CountJoin(string character) => Update(character, e => e with { RalliesJoined = e.RalliesJoined + 1 });

        public static void CountHeal(string character) => Update(character, e => e with { HealRounds = e.HealRounds + 1 });

        private static void Update(string character, Func<Entry, Entry> change)
        {
            _entries.AddOrUpdate(character, c => change(new Entry(c, CastleBattlePhase.Starting, "", DateTime.UtcNow, 0, 0)), (_, e) => change(e));
            Changed?.Invoke();
        }

        public static Entry? Get(string character) => _entries.TryGetValue(character, out var e) ? e : null;

        public static IReadOnlyList<Entry> All() => _entries.Values.OrderBy(e => e.Character, StringComparer.OrdinalIgnoreCase).ToList();

        private static readonly Dictionary<CastleBattlePhase, string> Descriptions = new()
        {
            [CastleBattlePhase.Starting] = "Starting",
            [CastleBattlePhase.Healing] = "Healing",
            [CastleBattlePhase.WaitingForRally] = "Waiting for a rally",
            [CastleBattlePhase.JoiningRally] = "Joining a rally",
            [CastleBattlePhase.JoinedWaitingToMarch] = "Joined rally, waiting to march",
            [CastleBattlePhase.MarchingToTarget] = "Marching to the target",
            [CastleBattlePhase.Garrisoned] = "Garrisoned",
            [CastleBattlePhase.MarchingBack] = "Marching back",
            [CastleBattlePhase.SendingDefense] = "Sending defense",
            [CastleBattlePhase.Switching] = "Switching character",
        };

        public static string Describe(CastleBattlePhase phase) => Descriptions.TryGetValue(phase, out var text) ? text : phase.ToString();
    }
}
