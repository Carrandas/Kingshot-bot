using System.Text.Json;
using Bot.Core.Models;
using Bot.Core.Services;
using NUnit.Framework;

namespace Bot.Core.Tests
{
    [TestFixture]
    public class TeamFlagsTests
    {
        [Test]
        public void ThreeOfSixSet_RepeatFromTheFirst()
        {
            var s = new BearHuntSettings { JoinTeam = 4, JoinTeam2 = 5, JoinTeam3 = 6 };
            Assert.That(Enumerable.Range(0, 6).Select(s.JoinFlag), Is.EqualTo(new[] { 4, 5, 6, 4, 5, 6 }));
        }

        [Test]
        public void UnsetSlotsInBetween_AreSkipped()
        {
            var s = new VikingVengeanceSettings { Team = 0, Team2 = 2, Team3 = 0, Team4 = 7 };
            Assert.Multiple(() =>
            {
                Assert.That(s.Flags, Is.EqualTo(new[] { 2, 7 }));
                Assert.That(Enumerable.Range(0, 4).Select(s.Flag), Is.EqualTo(new[] { 2, 7, 2, 7 }));
                Assert.That(s.IsValid, Is.True);
            });
        }

        [Test]
        public void NoSlotSet_FallsBackAndIsInvalid()
        {
            var s = new BearHuntSettings { JoinTeam = 0 };
            Assert.Multiple(() =>
            {
                Assert.That(s.JoinFlag(2), Is.EqualTo(4));
                Assert.That(s.IsValid, Is.False);
            });
        }

        [Test]
        public void OldConfigWithOneFlag_KeepsItInSlotOne()
        {
            var bear = JsonSerializer.Deserialize<BearHuntSettings>("{\"joinTeam\":2}")!;
            var viking = JsonSerializer.Deserialize<VikingVengeanceSettings>("{\"team\":5}")!;
            Assert.Multiple(() =>
            {
                Assert.That(bear.JoinFlags, Is.EqualTo(new[] { 2 }));
                Assert.That(viking.Flags, Is.EqualTo(new[] { 5 }));
            });
        }

        [Test]
        public void JoinsOut_LeavesOutOurOwnRallyWhileItIsAway()
        {
            var now = DateTime.UtcNow;
            var away = BearHuntStatus.Entry.New("a") with { OwnRallies = 1, OwnBackUtc = now.AddMinutes(3) };
            Assert.Multiple(() =>
            {
                Assert.That(BearHuntPlanner.JoinsOut(3, away, now), Is.EqualTo(2));
                Assert.That(BearHuntPlanner.JoinsOut(3, away with { OwnBackUtc = now.AddMinutes(-1) }, now), Is.EqualTo(3));
                Assert.That(BearHuntPlanner.JoinsOut(3, away with { OwnFailures = 1 }, now), Is.EqualTo(3));
                Assert.That(BearHuntPlanner.JoinsOut(0, away, now), Is.EqualTo(0));
                Assert.That(BearHuntPlanner.JoinsOut(2, null, now), Is.EqualTo(2));
            });
        }
    }
}
