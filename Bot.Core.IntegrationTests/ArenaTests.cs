using Bot.Core.Logging;
using Bot.Core.Tasks.Modules;
using NUnit.Framework;

namespace Bot.Core.IntegrationTests
{
    /// <summary>
    /// Arena's Challenge List on a saved screenshot (opponent names and avatars blacked out). The expected
    /// powers are the ones shown in the game; 63.3M is red (stronger than our 27.1M), the others green.
    /// </summary>
    [TestFixture]
    public class ArenaTests
    {
        [Test]
        public void ReadsOpponentPowersAndWhoIsWeaker()
        {
            var opponents = ArenaTask.ReadOpponents(Fixtures.Load("arena-challenge-list.png"), new LogService(0));

            Assert.That(opponents.Select(o => o.Power), Is.EqualTo(new long?[] { 21_400_000, 12_900_000, 63_300_000, 9_500_000, 7_200_000 }));
            Assert.That(opponents.Select(o => o.Weaker), Is.EqualTo(new[] { true, true, false, true, true }));
        }

        [Test]
        public void ReadsDailyChallengesLeft()
        {
            Assert.That(ArenaTask.ReadChallengesLeft(Fixtures.Load("arena-challenge-list.png"), new LogService(0)), Is.EqualTo(4));
        }
    }
}
