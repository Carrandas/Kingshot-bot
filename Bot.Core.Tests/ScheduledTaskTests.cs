using Bot.Core.Logging;
using Bot.Core.Models;
using Bot.Core.Tasks;
using Bot.Core.Tasks.Modules;
using NUnit.Framework;

namespace Bot.Core.Tests
{
    /// <summary>What the dashboard's "next run" shows for tasks with a gate or a timer (state saves under data/ in the test output folder)</summary>
    [TestFixture]
    public class ScheduledTaskTests
    {
        private sealed class NoServices : IServiceProvider
        {
            public object? GetService(Type serviceType) => null;
        }

        private static string NewKey() => "test-" + Guid.NewGuid().ToString("N");

        private static readonly AccountSettings Account = new() { AccountName = "Test", InstanceNumber = 0 };

        private static IEnumerable<TaskType> ScheduledTypes() =>
            TaskManager.CreateTaskModules(new LogService(0), new NoServices()).Where(m => m.Value is IScheduledTask).Select(m => m.Key);

        private static IScheduledTask Scheduled(TaskType type) =>
            (IScheduledTask)TaskManager.CreateTaskModules(new LogService(0), new NoServices())[type];

        // A game account the task never ran for: nothing to wait for, and clearing it changes nothing
        [TestCaseSource(nameof(ScheduledTypes))]
        public void UnknownAccount_IsNotWaiting(TaskType type)
        {
            var task = Scheduled(type);
            var key = NewKey();

            Assert.That(task.GetNextRunUtc(key, Account), Is.Null);
            Assert.DoesNotThrow(() => task.ClearSchedule(key));
            Assert.That(task.GetNextRunUtc(key, Account), Is.Null);
        }

        // Tasks whose only gate is the once-a-day DailyGate: done today = next run at the 00:00 UTC reset, the play button clears it
        [TestCase(TaskType.ClaimMail)]
        [TestCase(TaskType.CollectVip)]
        [TestCase(TaskType.NomadicMerchant)]
        [TestCase(TaskType.SuggestionBox)]
        [TestCase(TaskType.TruegoldCrucible)]
        [TestCase(TaskType.MysticTrial)]
        public void DailyGateTask_WaitsForTheReset_AndClearScheduleReopensIt(TaskType type)
        {
            var task = Scheduled(type);
            var key = NewKey();

            DailyGate.MarkDone(type, key);
            Assert.That(task.GetNextRunUtc(key, Account), Is.EqualTo(Schedule.NextUtcMidnight));

            task.ClearSchedule(key);
            Assert.Multiple(() =>
            {
                Assert.That(DailyGate.DoneToday(type, key), Is.False);
                Assert.That(task.GetNextRunUtc(key, Account), Is.Null);
            });
        }

        [Test]
        public void SuggestionBoxAnswerTable_IsCompleteAndUnambiguous()
        {
            var answers = SuggestionBoxTask.KnownAnswers;

            Assert.Multiple(() =>
            {
                Assert.That(answers, Is.Not.Empty);
                Assert.That(answers.All(a => a.Letter.Length > 20 && a.Answer.Length > 5), Is.True, "empty or too short letter/answer");
                Assert.That(answers.Select(a => a.Letter).Distinct().Count(), Is.EqualTo(answers.Length), "a letter listed twice");
                Assert.That(answers.Any(a => a.TryOther), Is.False, "built-in answers are final; TryOther is for learned ones");
            });
        }
    }
}
