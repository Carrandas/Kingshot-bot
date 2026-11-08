using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Bot.Core.Services
{
    /// <summary>Where a character is in the bear hunt (the Bear Hunt tab shows it per character)</summary>
    public enum BearHuntPhase
    {
        Waiting,
        Preparing,
        Prepared,
        Switching,
        SendingOwnRally,
        OwnRallyOut,
        Joining,
        QueuesFull,
        Done
    }

    /// <summary>
    /// Live state of the bear hunt mode, per character: prepared or not, when its own rally is back, joins.
    /// The planner reads it to pick the next character. In memory only; cleared when a bear hunt starts.
    /// Keys are matched like the roster ("Asi" and "Asli" are one character).
    /// </summary>
    public static class BearHuntStatus
    {
        public sealed record Entry(string Character, BearHuntPhase Phase, string Detail, DateTime UpdatedUtc,
            bool Prepared, int OwnRallies, DateTime? OwnBackUtc, int Joins, DateTime? FullUntilUtc, int OwnFailures = 0)
        {
            public static Entry New(string character) => new(character, BearHuntPhase.Waiting, "", DateTime.UtcNow, false, 0, null, 0, null);
        }

        private static readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _lock = new();

        /// <summary>Raised on every change, from the bot's thread</summary>
        public static event Action? Changed;

        public static DateTime? BearStartUtc { get; private set; }
        public static DateTime? EndsUtc { get; private set; }
        public static bool IsRunning => EndsUtc != null;

        /// <summary>
        /// The War list had no rally left to join: the list is the alliance's, so another character won't find one
        /// either. Stay put until then.
        /// </summary>
        public static DateTime? NoRalliesUntilUtc { get; private set; }

        public static void MarkNoRallies(DateTime untilUtc)
        {
            NoRalliesUntilUtc = untilUtc;
            Changed?.Invoke();
        }

        public static void Start(DateTime bearStartUtc, DateTime endsUtc)
        {
            _entries.Clear();
            NoRalliesUntilUtc = null;
            BearStartUtc = bearStartUtc;
            EndsUtc = endsUtc;
            Changed?.Invoke();
        }

        public static void Stop()
        {
            BearStartUtc = null;
            EndsUtc = null;
            Changed?.Invoke();
        }

        public static void Set(string character, BearHuntPhase phase, string detail = "") =>
            Update(character, e => e with { Phase = phase, Detail = detail });

        public static void MarkPrepared(string character) => Update(character, e => e with { Prepared = true, Phase = BearHuntPhase.Prepared, Detail = "" });

        public static void OwnRallySent(string character, DateTime backUtc) =>
            Update(character, e => e with { OwnRallies = e.OwnRallies + 1, OwnBackUtc = backUtc, OwnFailures = 0, Phase = BearHuntPhase.OwnRallyOut, Detail = $"back ~{backUtc.ToLocalTime():HH:mm:ss}" });

        /// <summary>The own rally could not leave (troops not home yet?): try again at <paramref name="retryUtc"/></summary>
        public static void OwnRallyFailed(string character, DateTime retryUtc) => Update(character, e => e with { OwnBackUtc = retryUtc, OwnFailures = e.OwnFailures + 1 });

        /// <summary>
        /// No bear on the map yet (the trap opened ~5 min after the Task List time on 2026-11-08): every own rally waits
        /// until <paramref name="untilUtc"/>. The same for all characters, so the planner doesn't switch over it.
        /// </summary>
        public static void BearNotUpUntil(DateTime untilUtc)
        {
            lock (_lock)
            {
                foreach (var (key, e) in _entries)
                {
                    if (e.OwnBackUtc == null || e.OwnBackUtc < untilUtc) _entries[key] = e with { OwnBackUtc = untilUtc, Detail = "bear not up yet" };
                }
            }
            Changed?.Invoke();
        }

        public static void CountJoin(string character) => Update(character, e => e with { Joins = e.Joins + 1 });

        /// <summary>No free march or nothing to join: leave this character alone until <paramref name="untilUtc"/></summary>
        public static void MarkFull(string character, DateTime? untilUtc) => Update(character, e => e with { FullUntilUtc = untilUtc });

        private static void Update(string character, Func<Entry, Entry> change)
        {
            lock (_lock)
            {
                string key = KeyFor(character);
                var old = _entries.TryGetValue(key, out var e) ? e : Entry.New(character);
                _entries[key] = change(old) with { UpdatedUtc = DateTime.UtcNow };
            }
            Changed?.Invoke();
        }

        private static string KeyFor(string character) =>
            _entries.Keys.FirstOrDefault(k => CharacterRoster.SameName(k, character)) ?? character;

        public static Entry? Get(string character) =>
            _entries.Values.FirstOrDefault(e => CharacterRoster.SameName(e.Character, character));

        public static IReadOnlyList<Entry> All() => _entries.Values.OrderBy(e => e.Character, StringComparer.OrdinalIgnoreCase).ToList();

        private static readonly Dictionary<BearHuntPhase, string> Descriptions = new()
        {
            [BearHuntPhase.Waiting] = "Waiting",
            [BearHuntPhase.Preparing] = "Calling troops home",
            [BearHuntPhase.Prepared] = "Ready for the bear",
            [BearHuntPhase.Switching] = "Switching character",
            [BearHuntPhase.SendingOwnRally] = "Sending own rally",
            [BearHuntPhase.OwnRallyOut] = "Own rally out",
            [BearHuntPhase.Joining] = "Joining rallies",
            [BearHuntPhase.QueuesFull] = "All marches out",
            [BearHuntPhase.Done] = "Done",
        };

        public static string Describe(BearHuntPhase phase) => Descriptions.TryGetValue(phase, out var text) ? text : phase.ToString();
    }
}
