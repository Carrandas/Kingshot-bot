using System;
using System.Collections.Generic;
using System.Linq;
using Bot.Core.Models;

namespace Bot.Core.Services
{
    /// <summary>Where the clock is relative to the bear: before the prep time, in it, during the 30 minutes, after</summary>
    public enum BearHuntStage { Before, Prep, Running, Over }

    /// <summary>
    /// The bear hunt's decisions, without the game: which character to be on and when to send its own rally.
    /// Own rallies come first: a character whose troops are home (or back within the switch time) gets the turn
    /// (one pass: own rally, then up to <see cref="BearHuntSettings.MaxJoins"/> joins).
    /// The first character is the main one: after another character's turn the bot goes back to it, and it keeps
    /// joining; with all its marches out it waits for them (joined rallies come back quickly) instead of switching.
    /// Asked by the user on 2026-11-10: Asli rally + 6 joins, llsa rally + 6 joins, back to Asli, join until her next rally.
    /// No own rally leaves after the bear's start + 24:30 (it would arrive after the bear has gone).
    /// </summary>
    public static class BearHuntPlanner
    {
        /// <summary>The next character to run a pass on (null: the bear is over), and how long to wait before that pass</summary>
        public readonly record struct Pick(string? Character, TimeSpan Wait);

        public static BearHuntStage StageAt(DateTime nowUtc, DateTime startUtc, BearHuntSettings settings)
        {
            if (nowUtc < startUtc - settings.Prep) return BearHuntStage.Before;
            if (nowUtc < startUtc) return BearHuntStage.Prep;
            return nowUtc < startUtc + settings.EventLength ? BearHuntStage.Running : BearHuntStage.Over;
        }

        /// <summary>An own rally may leave at this time: the bear is up and the rally still gets there in time</summary>
        public static bool CanSendOwnAt(DateTime timeUtc, DateTime startUtc, BearHuntSettings settings) =>
            timeUtc >= startUtc && timeUtc <= startUtc + settings.LastSend;

        /// <summary>Send the own rally now: it is allowed and the troops are home (no rally out, or its estimated return passed)</summary>
        public static bool ShouldSendOwn(DateTime nowUtc, DateTime startUtc, BearHuntSettings settings, BearHuntStatus.Entry? state) =>
            CanSendOwnAt(nowUtc, startUtc, settings) && (state?.OwnBackUtc == null || state.OwnBackUtc <= nowUtc);

        /// <summary>
        /// Rallies to join now: the free march queues, at most <paramref name="maxJoins"/>. Tracker not read (total 0): try up to the maximum, a full queue makes the join fail.
        /// </summary>
        public static int JoinsAllowed(int busy, int total, int maxJoins)
        {
            int free = total > 0 ? total - busy : maxJoins;
            return Math.Clamp(free, 0, Math.Max(0, maxJoins));
        }

        /// <summary>Marching time added to a joined rally's countdown (the "14km away" rows arrive in well under this)</summary>
        public static readonly TimeSpan JoinMarch = TimeSpan.FromSeconds(30);

        /// <summary>A rally that launches in <paramref name="rallyRemaining"/> still reaches the bear before the event ends (2026-11-10: late joins arrived after the bear)</summary>
        public static bool JoinArrivesInTime(DateTime nowUtc, TimeSpan rallyRemaining, DateTime eventEndUtc) =>
            nowUtc + rallyRemaining + JoinMarch <= eventEndUtc;

        private static readonly TimeSpan SwitchTime = TimeSpan.FromSeconds(60);

        /// <summary>
        /// When to try a failed own rally again: after the switch lead + a switch the first time (troops a bit later
        /// than estimated), a whole rally round once it failed before (something is wrong; don't hop between characters for it)
        /// </summary>
        public static TimeSpan OwnRetryAfter(int previousFailures, BearHuntSettings settings) =>
            previousFailures > 0 ? settings.RallyRound : settings.SwitchLead + SwitchTime;

        /// <summary>The characters in turn order: <paramref name="first"/> (matched like the roster) first, then the rest as listed</summary>
        public static List<string> Order(IEnumerable<string> names, string? first)
        {
            var list = names.ToList();
            int i = string.IsNullOrWhiteSpace(first) ? -1 : list.FindIndex(n => CharacterRoster.SameName(n, first));
            if (i > 0)
            {
                var lead = list[i];
                list.RemoveAt(i);
                list.Insert(0, lead);
            }
            return list;
        }

        /// <param name="noRalliesUntilUtc">The War list was empty: stay on the current character until then (unless an own rally is due)</param>
        public static Pick PickCharacter(DateTime nowUtc, DateTime startUtc, BearHuntSettings settings,
            IReadOnlyList<string> order, Func<string, BearHuntStatus.Entry?> state, string? current, DateTime? noRalliesUntilUtc = null)
        {
            if (order.Count == 0) return new Pick(null, TimeSpan.Zero);
            return StageAt(nowUtc, startUtc, settings) switch
            {
                BearHuntStage.Before => new Pick(order[0], startUtc - settings.Prep - nowUtc),
                BearHuntStage.Prep => PickInPrep(nowUtc, startUtc, order, state, current),
                BearHuntStage.Running => PickWhileRunning(nowUtc, startUtc, settings, order, state, current, noRalliesUntilUtc),
                _ => new Pick(null, TimeSpan.Zero)
            };
        }

        /// <summary>Every character calls its troops home once; then switch to the first one and wait there for the bear</summary>
        private static Pick PickInPrep(DateTime nowUtc, DateTime startUtc, IReadOnlyList<string> order, Func<string, BearHuntStatus.Entry?> state, string? current)
        {
            var unprepared = order.FirstOrDefault(n => state(n)?.Prepared != true);
            if (unprepared != null) return new Pick(unprepared, TimeSpan.Zero);
            bool onFirst = current != null && CharacterRoster.SameName(current, order[0]);
            return new Pick(order[0], onFirst ? startUtc - nowUtc : TimeSpan.Zero);
        }

        private static Pick PickWhileRunning(DateTime nowUtc, DateTime startUtc, BearHuntSettings settings,
            IReadOnlyList<string> order, Func<string, BearHuntStatus.Entry?> state, string? current, DateTime? noRalliesUntilUtc)
        {
            var due = OwnRalliesDue(startUtc, settings, order, state);
            var next = due.FirstOrDefault();
            if (next.Name != null && next.ReadyUtc <= nowUtc + settings.SwitchLead) return GoForOwnRally(nowUtc, current, next.Name, next.ReadyUtc);

            // the first character is the main one: the others only get a pass for their own rally (+ its joins), then back
            if (current != null && !CharacterRoster.SameName(current, order[0])) return new Pick(order[0], TimeSpan.Zero);

            // nothing to join anywhere: wait where we are (a switch finds the same empty list)
            if (noRalliesUntilUtc is DateTime quiet && quiet > nowUtc) return WaitHere(nowUtc, settings, order, current, next, quiet);
            return PickForJoins(nowUtc, settings, order, state, current, next);
        }

        /// <summary>Another character: switch now (the switch takes time); the current one: wait for its troops</summary>
        private static Pick GoForOwnRally(DateTime nowUtc, string? current, string name, DateTime readyUtc)
        {
            bool isCurrent = current != null && CharacterRoster.SameName(current, name);
            return new Pick(name, isCurrent ? Positive(readyUtc - nowUtc) : TimeSpan.Zero);
        }

        /// <summary>Keep joining on the current character; all its marches out: wait there for the first one back</summary>
        private static Pick PickForJoins(DateTime nowUtc, BearHuntSettings settings, IReadOnlyList<string> order,
            Func<string, BearHuntStatus.Entry?> state, string? current, (string? Name, DateTime ReadyUtc) next)
        {
            string here = current ?? order[0];
            return state(here)?.FullUntilUtc is DateTime full && full > nowUtc
                ? WaitHere(nowUtc, settings, order, here, next, full)
                : new Pick(here, TimeSpan.Zero);
        }

        /// <summary>Wait on the current character until it can join again, or the next own rally's switch time</summary>
        private static Pick WaitHere(DateTime nowUtc, BearHuntSettings settings, IReadOnlyList<string> order, string? current,
            (string? Name, DateTime ReadyUtc) next, DateTime quietUntilUtc)
        {
            var until = next.Name != null && next.ReadyUtc - settings.SwitchLead < quietUntilUtc ? next.ReadyUtc - settings.SwitchLead : quietUntilUtc;
            return new Pick(current ?? order[0], Positive(until - nowUtc));
        }

        /// <summary>Characters whose own rally can still leave in time, by when their troops are home (then turn order)</summary>
        private static List<(string? Name, DateTime ReadyUtc)> OwnRalliesDue(DateTime startUtc, BearHuntSettings settings,
            IReadOnlyList<string> order, Func<string, BearHuntStatus.Entry?> state)
        {
            return order
                .Select((n, i) => (Name: (string?)n, ReadyUtc: Max(startUtc, state(n)?.OwnBackUtc ?? startUtc), Index: i))
                .Where(c => CanSendOwnAt(c.ReadyUtc, startUtc, settings))
                .OrderBy(c => c.ReadyUtc).ThenBy(c => c.Index)
                .Select(c => (c.Name, c.ReadyUtc))
                .ToList();
        }

        private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

        private static TimeSpan Positive(TimeSpan span) => span > TimeSpan.Zero ? span : TimeSpan.Zero;
    }
}
