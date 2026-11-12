using Bot.Core.Models;
using NUnit.Framework;

namespace Bot.Core.Tests
{
    [TestFixture]
    public class BearHuntSettingsTests
    {
        private static readonly DateTime Trap1 = DateTime.UtcNow.AddHours(5);
        private static readonly DateTime Trap2 = DateTime.UtcNow.AddHours(9);

        [Test]
        public void SetScanned_NextBearIsTheChosenTrap()
        {
            var s = new BearHuntSettings { Trap = 1 };
            s.SetScanned(Trap1, Trap2);
            Assert.That(s.NextBearUtc, Is.EqualTo(Trap1));
        }

        [Test]
        public void ChangingTheTrap_TakesTheOtherScannedBear()
        {
            var s = new BearHuntSettings { Trap = 1 };
            s.SetScanned(Trap1, Trap2);
            s.Trap = 2;
            Assert.That(s.NextBearUtc, Is.EqualTo(Trap2));
            s.Trap = 1;
            Assert.That(s.NextBearUtc, Is.EqualTo(Trap1));
        }

        [Test]
        public void ChangingTheTrap_KeepsTheTypedTimeWithoutAScan()
        {
            var s = new BearHuntSettings { Trap = 1, NextBearUtc = Trap1 };
            s.Trap = 2;
            Assert.That(s.NextBearUtc, Is.EqualTo(Trap1));
        }

        [Test]
        public void ChangingTheTrap_IgnoresAScannedBearThatIsOver()
        {
            var s = new BearHuntSettings { Trap = 1, RepeatDays = 0 };
            s.SetScanned(Trap1, DateTime.UtcNow.AddHours(-3));
            s.Trap = 2;
            Assert.That(s.NextBearUtc, Is.EqualTo(Trap1));
        }

        [Test]
        public void ChangingTheTrap_RollsAnOldScanForwardByTheRepeatDays()
        {
            // the scan is two days old: both bears are over, the next ones are the same times 2 days on
            var now = DateTime.UtcNow;
            var old1 = new DateTime(now.Year, now.Month, now.Day, 20, 0, 0, DateTimeKind.Utc).AddDays(-2);
            var old2 = old1.AddHours(-7);
            var s = new BearHuntSettings { Trap = 1, RepeatDays = 2 };
            s.SetScanned(old1, old2);
            s.Trap = 2;
            Assert.That(s.NextBearUtc, Is.GreaterThan(now - s.EventLength));
            Assert.That(s.NextBearUtc!.Value.TimeOfDay, Is.EqualTo(old2.TimeOfDay));
            Assert.That((s.NextBearUtc.Value - old2).TotalDays % 2, Is.EqualTo(0));
        }
    }
}
