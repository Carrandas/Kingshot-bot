using Bot.Core.Logging;
using Bot.Core.Services;
using Bot.Core.Tasks.Modules;
using NUnit.Framework;

namespace Bot.Core.IntegrationTests
{
    /// <summary>
    /// The map's Task List (UTC banner), 2026-11-07 18:40 UTC: "Bear Hunt - Trap 2" at 13:00 and "Trap 1" at 20:00 on
    /// 2026/11/08, as the game showed it. The map strip above the list (other players' names) is blacked out.
    /// </summary>
    [TestFixture]
    public class BearHuntTests
    {
        private static readonly DateTime Now = new(2026, 11, 7, 18, 40, 0, DateTimeKind.Utc);
        private static readonly TimeSpan Event = TimeSpan.FromMinutes(30);

        private static List<BearHuntSchedule.TaskListItem> Read(string name) =>
            BearHuntTask.ReadTaskList(Fixtures.Load(name), new LogService(0));

        [Test]
        public void SplitsTheListIntoHeadersAndRows()
        {
            var bands = BearHuntTask.FindListBands(Fixtures.Load("bearhunt-tasklist-both-traps.png"));

            // cut-off rows at the edges are left out: Castle Battle, the 11/08 header, Arsenal, Trap 2, Swordland, Trap 1
            Assert.That(bands.Select(b => b.Header), Is.EqualTo(new[] { false, true, false, false, false, false }));
        }

        [Test]
        public void TodayHeaderIsAHeader()
        {
            var items = Read("bearhunt-tasklist-today.png");

            Assert.That(items[0].IsHeader, Is.True);
            Assert.That(BearHuntSchedule.HeaderDate(items[0].Text, Now), Is.EqualTo(Now.Date), $"read '{items[0].Text}'");
        }

        [Test]
        public void ReadsBothTraps()
        {
            var items = Read("bearhunt-tasklist-both-traps.png");

            Assert.Multiple(() =>
            {
                Assert.That(BearHuntSchedule.NextBear(items, 2, Now, Event), Is.EqualTo(new DateTime(2026, 11, 8, 13, 0, 0, DateTimeKind.Utc)), Dump(items));
                Assert.That(BearHuntSchedule.NextBear(items, 1, Now, Event), Is.EqualTo(new DateTime(2026, 11, 8, 20, 0, 0, DateTimeKind.Utc)), Dump(items));
            });
        }

        /// <summary>The whole list as the task reads it while scrolling: today's rows, then tomorrow's</summary>
        [Test]
        public void ScrolledScreensTogether_GiveTomorrowsBear()
        {
            var items = Read("bearhunt-tasklist-today.png").Concat(Read("bearhunt-tasklist-trap2.png")).Concat(Read("bearhunt-tasklist-both-traps.png")).ToList();

            Assert.That(BearHuntSchedule.NextBear(items, 2, Now, Event), Is.EqualTo(new DateTime(2026, 11, 8, 13, 0, 0, DateTimeKind.Utc)), Dump(items));
        }

        private static string Dump(IEnumerable<BearHuntSchedule.TaskListItem> items) =>
            string.Join(" | ", items.Select(i => i.IsHeader ? $"[{i.Text}]" : $"{i.Text} @ {i.Time}"));
    }
}
