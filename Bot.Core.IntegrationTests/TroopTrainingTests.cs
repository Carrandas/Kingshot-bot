using Bot.Core.Logging;
using Bot.Core.Tasks.Modules;
using NUnit.Framework;

namespace Bot.Core.IntegrationTests
{
    /// <summary>
    /// Troop Training on saved screenshots of llsa's training screen: tiers VII-X unlocked, XI locked.
    /// The expected results are what the user confirmed in the game.
    /// </summary>
    [TestFixture]
    public class TroopTrainingTests
    {
        // Slots 0-4 show VII, VIII, IX, X and the locked XI
        [TestCase("training-infantry-x-selected.png", 3)]
        [TestCase("training-cavalry-x-selected.png", 3)]
        [TestCase("training-archer-x-selected.png", 3)]
        [TestCase("training-infantry-ix-selected.png", 2)]
        public void FindSelectedTierSlot_FindsTheGlowingTile(string screenshot, int expectedSlot)
        {
            Assert.That(TroopTrainingTask.FindSelectedTierSlot(Fixtures.Load(screenshot)), Is.EqualTo(expectedSlot));
        }

        [Test]
        public void SidePanel_AllIdle_OnLlsa()
        {
            // Side panel > Town on llsa: Infantry, Cavalry and Archer all "Idle"
            var shot = Fixtures.Load("training-panel-llsa-idle.png");
            var task = new TroopTrainingTask();
            var logger = new LogService(0);

            int? headerY = task.FindTroopsHeaderY(shot, 0, logger);
            Assert.That(headerY, Is.EqualTo(497));

            var statuses = task.ReadAllTroopStatuses(logger, shot, headerY!.Value);
            Assert.That(statuses.Select(s => (s.TroopType, s.IsReady)), Is.EqualTo(new[]
            {
                ("Infantry", true), ("Cavalry", true), ("Archers", true)
            }));
        }

        [Test]
        public void SidePanel_ReadsTimers_OnAsli()
        {
            // Side panel > Town on Asli: all three training, 07:36:48 / 07:36:53 / 07:36:58 left
            var shot = Fixtures.Load("training-panel-asli-training.png");
            var task = new TroopTrainingTask();
            var logger = new LogService(0);

            int? headerY = task.FindTroopsHeaderY(shot, 0, logger);
            Assert.That(headerY, Is.EqualTo(497));

            var statuses = task.ReadAllTroopStatuses(logger, shot, headerY!.Value);
            Assert.That(statuses.Select(s => (s.TroopType, s.IsReady, s.RemainingTime)), Is.EqualTo(new[]
            {
                ("Infantry", false, new TimeSpan(7, 36, 48)),
                ("Cavalry", false, new TimeSpan(7, 36, 53)),
                ("Archers", false, new TimeSpan(7, 36, 58))
            }));
        }

        // Side panel > Town on llsa with trained troops waiting ("Completed"): they must be collected before training.
        // "completed": after collecting Infantry by hand; "collected": after collecting Cavalry too.
        [TestCase("training-panel-llsa-completed.png", false, true, true)]
        [TestCase("training-panel-llsa-collected.png", false, false, true)]
        public void SidePanel_CompletedRowsAwaitCollection(string screenshot, bool infantry, bool cavalry, bool archers)
        {
            var shot = Fixtures.Load(screenshot);
            var task = new TroopTrainingTask();
            var logger = new LogService(0);

            int? headerY = task.FindTroopsHeaderY(shot, 0, logger);
            Assert.That(headerY, Is.EqualTo(497));

            var statuses = task.ReadAllTroopStatuses(logger, shot, headerY!.Value);
            Assert.That(statuses.Select(s => (s.TroopType, s.IsReady, s.AwaitsCollection)), Is.EqualTo(new[]
            {
                ("Infantry", true, infantry), ("Cavalry", true, cavalry), ("Archers", true, archers)
            }));
        }

        // After a "Completed" row's button: a hand points at the Barracks' troops bubble. The Train popup's hand
        // (after an "Idle" row's arrow) points elsewhere and must not count.
        [TestCase("training-troops-to-collect.png", true)]
        [TestCase("training-train-popup.png", false)]
        [TestCase("training-panel-llsa-completed.png", false)]
        public void ShowsTroopsToCollect(string screenshot, bool expected)
        {
            Assert.That(new TroopTrainingTask().ShowsTroopsToCollect(Fixtures.Load(screenshot), 0, new LogService(0)), Is.EqualTo(expected));
        }

        [Test]
        public void SidePanel_HeaderNotFoundOnTheTrainingScreen()
        {
            var shot = Fixtures.Load("training-infantry-x-selected.png");
            Assert.That(new TroopTrainingTask().FindTroopsHeaderY(shot, 0, new LogService(0)), Is.Null);
        }

        [Test]
        public void FindSelectedTierSlot_NullOutsideTheTrainingScreen()
        {
            // llsa's town, no tier strip
            Assert.That(TroopTrainingTask.FindSelectedTierSlot(Fixtures.Load("town-ilsa-events-dot.png")), Is.Null);
        }
    }
}
