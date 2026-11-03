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
        public void ReadsOpponentPowers_AllGreen()
        {
            var opponents = ArenaTask.ReadOpponents(Fixtures.Load("arena-one-challenge-left.png"), new LogService(0));

            Assert.That(opponents.Select(o => o.Power), Is.EqualTo(new long?[] { 23_700_000, 27_100_000, 22_800_000, 19_700_000, 24_600_000 }));
            Assert.That(opponents.Select(o => o.Weaker), Is.All.True);
        }

        /// <summary>NPC opponents (no server line, so the power sits lower): Asli's list on 2026-11-03.</summary>
        [Test]
        public void ReadsNpcOpponentPowers()
        {
            var opponents = ArenaTask.ReadOpponents(Fixtures.Load("arena-npc-opponents.png"), new LogService(0));

            Assert.That(opponents.Select(o => o.Power), Is.EqualTo(new long?[] { 60_000, 166_500, 60_000, 72_800, 60_000 }));
            Assert.That(opponents.Select(o => o.Weaker), Is.All.True);
        }

        [TestCase("arena-challenge-list.png", 4)]
        [TestCase("arena-npc-opponents.png", 5)]
        [TestCase("arena-one-challenge-left.png", 1)]
        public void ReadsDailyChallengesLeft(string screenshot, int expected)
        {
            Assert.That(ArenaTask.ReadChallengesLeft(Fixtures.Load(screenshot), new LogService(0)), Is.EqualTo(expected));
        }
    }
}
