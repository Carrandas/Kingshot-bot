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
            var s = new BearHuntSettings { Trap = 1 };
            s.SetScanned(Trap1, DateTime.UtcNow.AddHours(-3));
            s.Trap = 2;
            Assert.That(s.NextBearUtc, Is.EqualTo(Trap1));
        }
    }
}
