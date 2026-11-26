using Bot.Core.Logging;
using Bot.Core.Models;
using Bot.Core.Services;
using Bot.Core.Tasks;
using Bot.Core.Tasks.Modules;
using NUnit.Framework;

namespace Bot.Core.Tests
{
    /// <summary>Champion's Way: the once-per-event gate, the kill count text, the longer run timeout and the calendar bar name</summary>
    [TestFixture]
    public class ChampionsWayTests
    {
        private static string NewKey() => "test-" + Guid.NewGuid().ToString("N");

        [Test]
        public void EventRunGate_HoldsUntilTheEventsLastDay_AndClearReopensIt()
        {
            var key = NewKey();
            var today = DateTime.UtcNow.Date;
            Assert.That(EventRunGate.Done(TaskType.ChampionsWay, key, DateTime.UtcNow), Is.False);
            Assert.That(EventRunGate.NextRunUtc(TaskType.ChampionsWay, key, DateTime.UtcNow), Is.Null);

            EventRunGate.MarkDone(TaskType.ChampionsWay, key, today.AddDays(2));
            Assert.Multiple(() =>
            {
                Assert.That(EventRunGate.Done(TaskType.ChampionsWay, key, today.AddDays(2).AddHours(23)), Is.True, "the event's last day");
                Assert.That(EventRunGate.Done(TaskType.ChampionsWay, key, today.AddDays(3)), Is.False, "the next event");
                Assert.That(EventRunGate.NextRunUtc(TaskType.ChampionsWay, key, DateTime.UtcNow), Is.EqualTo(today.AddDays(3)));
            });

            EventRunGate.Clear(TaskType.ChampionsWay, key);
            Assert.That(EventRunGate.Done(TaskType.ChampionsWay, key, DateTime.UtcNow), Is.False);
        }

        [Test]
        public void EventRunGate_IsPerAccount()
        {
            var asli = NewKey();
            var ilsa = NewKey();
            EventRunGate.MarkDone(TaskType.ChampionsWay, asli, DateTime.UtcNow.Date);
            Assert.That(EventRunGate.Done(TaskType.ChampionsWay, ilsa, DateTime.UtcNow), Is.False);
        }

        [Test]
        public void ClearSchedule_ReopensTheGate()
        {
            var task = (IScheduledTask)TaskManager.CreateTaskModules(new LogService(0), new NoServices())[TaskType.ChampionsWay];
            var key = NewKey();
            EventRunGate.MarkDone(TaskType.ChampionsWay, key, DateTime.UtcNow.Date.AddDays(1));
            Assert.That(task.GetNextRunUtc(key, new AccountSettings()), Is.EqualTo(DateTime.UtcNow.Date.AddDays(2)));

            task.ClearSchedule(key);
            Assert.That(task.GetNextRunUtc(key, new AccountSettings()), Is.Null);
        }

        [Test]
        public void Settings_DefaultToFlag8AndStaminaItems()
        {
            var account = new AccountSettings();
            Assert.Multiple(() =>
            {
                Assert.That(account.ChampionsWaySettings.RallyFlag, Is.EqualTo(8));
                Assert.That(account.ChampionsWaySettings.UseStaminaItems, Is.True);
            });
        }

        [Test]
        public void Settings_ChangeIsReportedThroughTheAccount()
        {
            var account = new AccountSettings();
            string? changed = null;
            account.PropertyChanged += (_, e) => changed = e.PropertyName;
            account.ChampionsWaySettings.RallyFlag = 3;
            Assert.That(changed, Is.EqualTo("ChampionsWaySettings.RallyFlag"));
        }

        [TestCase("0", 0)]
        [TestCase("1", 1)]
        [TestCase(" 10 ", 10)]
        [TestCase("", null)]
        [TestCase("ab", null)]
        public void ParseKills(string text, int? expected) =>
            Assert.That(ChampionsWayTask.ParseKills(text), Is.EqualTo(expected));

        [Test]
        public void RunTimeout_IsLongerThanTheDefaultForChampionsWayOnly()
        {
            var modules = TaskManager.CreateTaskModules(new LogService(0), new NoServices());
            Assert.Multiple(() =>
            {
                Assert.That(TaskManager.RunTimeout(modules[TaskType.ChampionsWay]), Is.EqualTo(TimeSpan.FromMinutes(50)));
                Assert.That(TaskManager.RunTimeout(modules[TaskType.ClaimMail]), Is.EqualTo(TimeSpan.FromMinutes(15)));
            });
        }

        [TestCase("Champion's Way", true)]
        [TestCase("Champio", true)]                 // the label cut off by the bar's end
        [TestCase("Alliance Championship", false)]
        public void CalendarBar(string label, bool expected) =>
            Assert.That(EventCalendar.SameEvent(label, EventCalendar.BarNames[TaskType.ChampionsWay]), Is.EqualTo(expected));

        private sealed class NoServices : IServiceProvider
        {
            public object? GetService(Type serviceType) => null;
        }
    }
}
