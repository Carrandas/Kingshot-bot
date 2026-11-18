using Bot.Core.Models;
using Bot.Core.Services;
using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace Bot.Core.Tests
{
    [TestFixture]
    public class AlchemySolverTests
    {
        [TestCase(177, BrewColour.Red)]
        [TestCase(2, BrewColour.Red)]
        [TestCase(165, BrewColour.Pink)]
        [TestCase(144, BrewColour.Purple)]
        [TestCase(107, BrewColour.Blue)]
        [TestCase(54, BrewColour.Green)]
        [TestCase(22, BrewColour.Yellow)]
        public void Classify(int hue, BrewColour expected) => Assert.That(AlchemySolver.Classify(hue), Is.EqualTo(expected));

        [Test]
        public void Plan_AvoidsColoursThatFailedInTheSlot()
        {
            // order 1 of llsa: blue kept in slot 0, green failed in slot 1, pink in slot 2
            var excluded = new Dictionary<int, HashSet<BrewColour>>
            {
                [1] = new() { BrewColour.Green },
                [2] = new() { BrewColour.Pink },
            };
            var plan = AlchemySolver.Plan(new[] { 1, 2 }, new[] { BrewColour.Green, BrewColour.Pink }, excluded);
            Assert.That(plan, Is.EqualTo(new[] { 1, 0 }));   // pink in slot 1, green in slot 2
        }

        [Test]
        public void Plan_NullWhenNoAssignmentFits()
        {
            var excluded = new Dictionary<int, HashSet<BrewColour>> { [0] = new() { BrewColour.Red, BrewColour.Blue } };
            Assert.That(AlchemySolver.Plan(new[] { 0 }, new[] { BrewColour.Red }, excluded), Is.Null);
        }

        [Test]
        public void Plan_SameColoursAreInterchangeable()
        {
            var plan = AlchemySolver.Plan(new[] { 0, 1, 2 }, new[] { BrewColour.Purple, BrewColour.Purple, BrewColour.Purple },
                new Dictionary<int, HashSet<BrewColour>>());
            Assert.That(plan, Is.EqualTo(new[] { 0, 1, 2 }));
        }

        [Test]
        public void AddFailures_OnlyTheSlotsThatStayedBrown()
        {
            var excluded = new Dictionary<int, HashSet<BrewColour>>();
            var row = new Dictionary<int, BrewColour> { [0] = BrewColour.Yellow, [1] = BrewColour.Pink, [2] = BrewColour.Blue };
            AlchemySolver.AddFailures(excluded, row, new[] { 0 });
            Assert.That(excluded.Keys, Is.EquivalentTo(new[] { 1, 2 }));
            Assert.That(excluded[1], Is.EquivalentTo(new[] { BrewColour.Pink }));
        }

        [Test]
        public void Merge_UnitesBothMaps()
        {
            var a = new Dictionary<int, HashSet<BrewColour>> { [0] = new() { BrewColour.Red } };
            var b = new Dictionary<int, HashSet<BrewColour>> { [0] = new() { BrewColour.Blue }, [1] = new() { BrewColour.Green } };
            var merged = AlchemySolver.Merge(a, b);
            Assert.That(merged[0], Is.EquivalentTo(new[] { BrewColour.Red, BrewColour.Blue }));
            Assert.That(merged[1], Is.EquivalentTo(new[] { BrewColour.Green }));
            Assert.That(a[0], Has.Count.EqualTo(1), "inputs untouched");
        }
    }

    [TestFixture]
    public class EventCalendarRuleTests
    {
        private static readonly DateTime Wednesday = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime Monday = new(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc);

        private static CalendarRead Week(params CalendarEvent[] events)
        {
            var read = new CalendarRead { ReadUtc = Wednesday, WeekStart = Monday };
            read.Events.AddRange(events);
            for (int i = read.Events.Count; i < EventCalendar.MinTrustedBars; i++)
                read.Events.Add(new CalendarEvent($"Filler event {i}", Monday, Monday.AddDays(6)));
            return read;
        }

        private static CalendarEvent On(string name, int first, int last) => new(name, Monday.AddDays(first), Monday.AddDays(last));

        [Test]
        public void WeekStart_IsMonday()
        {
            Assert.That(EventCalendar.WeekStartOf(Wednesday), Is.EqualTo(Monday));
            Assert.That(EventCalendar.WeekStartOf(new DateTime(2026, 11, 22, 23, 0, 0, DateTimeKind.Utc)), Is.EqualTo(Monday));
            Assert.That(EventCalendar.WeekStartOf(Monday), Is.EqualTo(Monday));
        }

        [Test]
        public void MayRun_WhileTheBarCoversToday()
        {
            var read = Week(On("Brew Workshop", 2, 4));
            Assert.That(EventCalendar.MayRun(TaskType.AlchemyJunction, Wednesday, read), Is.True);
            Assert.That(EventCalendar.MayRun(TaskType.AlchemyJunction, Wednesday.AddDays(3), read), Is.False, "Saturday");
            Assert.That(EventCalendar.MayRun(TaskType.AlchemyJunction, Wednesday.AddDays(-1), read), Is.False, "Tuesday");
        }

        [Test]
        public void MayRun_TrustedReadWithoutTheEvent_Skips() =>
            Assert.That(EventCalendar.MayRun(TaskType.AllianceMobilization, Wednesday, Week(On("Brew Workshop", 2, 4))), Is.False);

        [Test]
        public void MayRun_NoReadOldReadOrPoorRead_LeavesItToTheTask()
        {
            Assert.That(EventCalendar.MayRun(TaskType.AllianceMobilization, Wednesday, null), Is.True, "no read");
            Assert.That(EventCalendar.MayRun(TaskType.AllianceMobilization, Wednesday.AddDays(7), Week()), Is.True, "last week's read");
            var poor = new CalendarRead { ReadUtc = Wednesday, WeekStart = Monday };
            poor.Events.Add(On("Brew Workshop", 2, 4));
            Assert.That(EventCalendar.MayRun(TaskType.AllianceMobilization, Wednesday, poor), Is.True, "too few bars");
        }

        [Test]
        public void MayRun_TasksWithoutABarAlwaysRun() =>
            Assert.That(EventCalendar.MayRun(TaskType.Arena, Wednesday, Week()), Is.True);

        [TestCase("Viking", "Viking Vengeance", true)]
        [TestCase("Voyage of Light", "Voyage of Light", true)]
        [TestCase("l Voyage of Light", "Voyage of Light", true)]
        [TestCase("Alliance Mobili", "Alliance Mobilization", true)]
        [TestCase("Alliance Championship", "Alliance Mobilization", false)]
        [TestCase("Brewmaster Stall", "Brew Workshop", false)]
        [TestCase("Defea", "Defeat", false)]
        public void SameEvent(string label, string name, bool expected) =>
            Assert.That(EventCalendar.SameEvent(label, name), Is.EqualTo(expected));

        [TestCase("Viking... D", "Viking")]
        [TestCase("oN Hero Roulette D", "Hero Roulette")]
        [TestCase("Harbor Drill h", "Harbor Drill")]
        [TestCase("All Out D", "All Out")]
        [TestCase("i Defea", "Defea")]
        public void CleanLabel(string ocr, string expected) =>
            Assert.That(EventCalendar.CleanLabel(ocr), Is.EqualTo(expected));
    }
}
