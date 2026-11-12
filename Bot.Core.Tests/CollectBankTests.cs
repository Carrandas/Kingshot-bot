using Bot.Core.Models;
using Bot.Core.Tasks.Modules;
using NUnit.Framework;

namespace Bot.Core.Tests
{
    [TestFixture]
    public class CollectBankTests
    {
        private static readonly DateTime Now = new(2026, 11, 12, 22, 0, 0, DateTimeKind.Utc);

        [Test]
        public void NeverVisited_DueNow() =>
            Assert.That(CollectRewardsTask.BankNextUtc(null, null, Now), Is.Null);

        [Test]
        public void AfterADeposit_SleepsForTwentyFourHours()
        {
            var deposit = Now.AddHours(-1);
            Assert.That(CollectRewardsTask.BankNextUtc(deposit, null, Now), Is.EqualTo(deposit.AddHours(24).AddMinutes(1)));
        }

        [Test]
        public void TwentyFourHoursLater_DueAgain()
        {
            var deposit = Now.AddHours(-24).AddMinutes(-2);
            Assert.That(CollectRewardsTask.BankNextUtc(deposit, null, Now), Is.Null);
        }

        [Test]
        public void AVisitThatFoundNothing_LooksAgainInTwoHours()
        {
            var deposit = Now.AddHours(-25);   // overdue, but the vault was still running when we looked
            var check = Now.AddMinutes(-10);
            Assert.That(CollectRewardsTask.BankNextUtc(deposit, check, Now), Is.EqualTo(check.AddHours(2)));
            Assert.That(CollectRewardsTask.BankNextUtc(deposit, check, check.AddHours(2).AddSeconds(1)), Is.Null);
        }

        [Test]
        public void BankTask_HasItsOwnTypeAndTimer()
        {
            var task = new CollectBankTask();
            Assert.That(task.TaskType, Is.EqualTo(TaskType.CollectBank));
            Assert.That(new CollectRewardsTask().TaskType, Is.EqualTo(TaskType.CollectRewards));
        }
    }
}
