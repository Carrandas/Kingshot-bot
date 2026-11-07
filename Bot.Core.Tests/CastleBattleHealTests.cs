using Bot.Core.Tasks.Modules;
using NUnit.Framework;

namespace Bot.Core.Tests
{
    /// <summary>Which Heal Injured box gets the batch (0 infantry, 1 cavalry, 2 archers)</summary>
    [TestFixture]
    public class CastleBattleHealTests
    {
        [Test]
        public void FirstBatchGoesToTheMostInjured()
        {
            Assert.That(CastleBattleTask.PickHealTarget(new int?[] { 38_790, 15_516, 50_250 }, 1000, null), Is.EqualTo((2, 1000)));
        }

        [Test]
        public void StaysOnTheTypedTypeWhileItHasAFullBatch()
        {
            // archers have more now, but infantry still has a full batch: no retyping
            Assert.That(CastleBattleTask.PickHealTarget(new int?[] { 1_000, 15_516, 50_250 }, 1000, (0, 1000)), Is.EqualTo((0, 1000)));
        }

        [Test]
        public void MovesOnWhenTheTypeRunsOut()
        {
            Assert.That(CastleBattleTask.PickHealTarget(new int?[] { 400, 15_516, 9_000 }, 1000, (0, 1000)), Is.EqualTo((1, 1000)));
        }

        [Test]
        public void HealsWhatIsLeftOfTheLastType()
        {
            Assert.That(CastleBattleTask.PickHealTarget(new int?[] { 0, 0, 350 }, 1000, (1, 1000)), Is.EqualTo((2, 350)));
        }

        [Test]
        public void UnreadableCountsKeepTheLastType()
        {
            Assert.That(CastleBattleTask.PickHealTarget(new int?[] { null, null, null }, 900, (1, 1000)), Is.EqualTo((1, 900)));
        }
    }
}
