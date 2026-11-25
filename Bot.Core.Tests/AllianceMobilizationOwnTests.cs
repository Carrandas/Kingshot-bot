using Bot.Core.Models;
using Bot.Core.Tasks.Modules;
using NUnit.Framework;
using Action = Bot.Core.Tasks.Modules.AllianceMobilizationTask.OwnMissionAction;

namespace Bot.Core.Tests
{
    [TestFixture]
    public class AllianceMobilizationOwnTests
    {
        private const string Troops = "Raise 120,000 Power by training/promoting troops";
        private const string Terror = "Defeat Terror 5 time(s)";
        private const string Truegold = "Use 50 Truegold to upgrade the building";
        private const string Heal = "Heal 5,000 troops";

        [TestCase(Troops, 860, "Accept")]
        [TestCase(Terror, 300, "Accept")]
        [TestCase(Terror, 299, "Refresh")]      // kept kind, too few points
        [TestCase(Truegold, 350, "Refresh")]
        [TestCase(Heal, 900, "Refresh")]        // "troops" but not training
        [TestCase(Troops, null, "Accept")]      // points unread: the kind decides
        [TestCase("", 860, "Leave")]            // description unread
        public void DefaultSettings(string description, int? points, string expected)
        {
            Assert.That(AllianceMobilizationTask.DecideOwnMission(description, points, new AllianceMobilizationSettings()).ToString(), Is.EqualTo(expected));
        }

        [Test]
        public void SwitchedOffKinds_AreRefreshed_NothingKept_LeavesThemAlone()
        {
            var noTerror = new AllianceMobilizationSettings { KeepTerror = false };
            var nothing = new AllianceMobilizationSettings { KeepTerror = false, KeepTroopTraining = false };
            Assert.Multiple(() =>
            {
                Assert.That(AllianceMobilizationTask.DecideOwnMission(Terror, 900, noTerror), Is.EqualTo(Action.Refresh));
                Assert.That(AllianceMobilizationTask.DecideOwnMission(Troops, 900, noTerror), Is.EqualTo(Action.Accept));
                Assert.That(AllianceMobilizationTask.DecideOwnMission(Truegold, 350, nothing), Is.EqualTo(Action.Leave));
            });
        }

        [TestCase("7/10", 7)]
        [TestCase("0/11", 0)]
        [TestCase("10 / 11", 10)]
        [TestCase("", null)]
        public void ParseAttempts(string text, int? expected) =>
            Assert.That(AllianceMobilizationTask.ParseAttempts(text), Is.EqualTo(expected));

        [TestCase("+860", 860)]
        [TestCase("+1,050", 1050)]
        [TestCase("+", null)]
        public void ParsePoints(string text, int? expected) =>
            Assert.That(AllianceMobilizationTask.ParsePoints(text), Is.EqualTo(expected));
    }
}
