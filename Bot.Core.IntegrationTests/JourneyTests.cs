using Bot.Core.Logging;
using Bot.Core.Tasks.Modules;
using NUnit.Framework;

namespace Bot.Core.IntegrationTests
{
    /// <summary>
    /// Journey on saved screenshots. The expected results are what the user confirmed in the game.
    /// </summary>
    [TestFixture]
    public class JourneyTests
    {
        [Test]
        public void IdlePopup_ReadsSuppliesAfterMax()
        {
            // Idle Journey popup after max on llsa: counter 81/100, Start button shows 81
            var shot = Fixtures.Load("journey-idle-81.png");

            Assert.That(JourneyTask.ReadIdleSupplies(shot, new LogService(0)), Is.EqualTo(81));
        }
    }
}
