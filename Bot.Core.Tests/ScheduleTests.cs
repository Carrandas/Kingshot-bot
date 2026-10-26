using Bot.Core.Models;
using Bot.Core.Services;
using Bot.Core.Tasks;
using NUnit.Framework;

namespace Bot.Core.Tests
{
    /// <summary>
    /// Daily gates, schedule keys and run history. These save under data/ in the test output folder, never the bot's;
    /// each test uses its own account key so tests don't see each other's state.
    /// </summary>
    [TestFixture]
    public class ScheduleTests
    {
        private static string NewKey() => "test-" + Guid.NewGuid().ToString("N");

        [Test]
        public void AccountKey_IsTheGameAccountId_OrFallsBackToNameAndInstance()
        {
            var account = new AccountSettings { AccountName = "Main", InstanceNumber = 3 };

            Assert.That(Schedule.AccountKey("34431520", account), Is.EqualTo("34431520"));
            Assert.That(Schedule.AccountKey(null, account), Is.EqualTo("Account_Main_3"));
            Assert.That(Schedule.AccountKey("", account), Is.EqualTo("Account_Main_3"));
        }

        [Test]
        public void NextUtcMidnight_IsTheNextGameReset()
        {
            var next = Schedule.NextUtcMidnight;

            Assert.That(next.TimeOfDay, Is.EqualTo(TimeSpan.Zero));
            Assert.That(next, Is.GreaterThan(DateTime.UtcNow));
            Assert.That(next - DateTime.UtcNow, Is.LessThanOrEqualTo(TimeSpan.FromDays(1)));
        }

        [Test]
        public void DailyGate_MarkDone_HoldsUntilTheReset_AndClearReopensIt()
        {
            var key = NewKey();

            Assert.That(DailyGate.DoneToday(TaskType.CollectVip, key), Is.False);
            Assert.That(DailyGate.NextRunUtc(TaskType.CollectVip, key), Is.Null, "not waiting before the first run");

            DailyGate.MarkDone(TaskType.CollectVip, key);
            Assert.That(DailyGate.DoneToday(TaskType.CollectVip, key), Is.True);
            Assert.That(DailyGate.NextRunUtc(TaskType.CollectVip, key), Is.EqualTo(Schedule.NextUtcMidnight));

            DailyGate.Clear(TaskType.CollectVip, key);
            Assert.That(DailyGate.DoneToday(TaskType.CollectVip, key), Is.False);
        }

        [Test]
        public void DailyGate_IsPerTaskAndPerAccount()
        {
            var asli = NewKey();
            var ilsa = NewKey();

            DailyGate.MarkDone(TaskType.NomadicMerchant, asli);

            Assert.That(DailyGate.DoneToday(TaskType.NomadicMerchant, ilsa), Is.False, "other character");
            Assert.That(DailyGate.DoneToday(TaskType.TruegoldCrucible, asli), Is.False, "other task");
        }

        [Test]
        public void RunHistory_ASkipOnlyUpdatesTheLastCheck()
        {
            var key = NewKey();

            TaskRunHistory.RecordRun(key, TaskType.ClaimMail, TaskExecutionDetails.Failed("No mail button"));
            TaskRunHistory.RecordRun(key, TaskType.ClaimMail, TaskExecutionDetails.SkippedBecause("Ran 5 min ago"));

            var record = TaskRunHistory.Get(key, TaskType.ClaimMail)!;
            Assert.That(record.LastRunOk, Is.False, "the skip is not a run");
            Assert.That(record.LastRunMessage, Is.EqualTo("No mail button"));
            Assert.That(record.LastCheckMessage, Is.EqualTo("Ran 5 min ago"));

            TaskRunHistory.RecordRun(key, TaskType.ClaimMail, TaskExecutionDetails.Succeeded("3 mails"));
            record = TaskRunHistory.Get(key, TaskType.ClaimMail)!;
            Assert.That(record.LastRunOk, Is.True);
            Assert.That(record.LastRunMessage, Is.EqualTo("3 mails"));
        }
    }
}
