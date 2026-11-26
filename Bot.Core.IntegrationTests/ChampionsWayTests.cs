using Bot.Core.Logging;
using Bot.Core.Tasks.Modules;
using NUnit.Framework;

namespace Bot.Core.IntegrationTests
{
    /// <summary>
    /// Champion's Way screens on 2026-11-25 (rallies on the Elite Rebel by hand). Expected values are what the screens showed;
    /// the chat line is blacked out.
    /// </summary>
    [TestFixture]
    public class ChampionsWayTests
    {
        private static readonly LogService Logger = new(0);

        [TestCase("championsway-page-0.png", 0)]
        [TestCase("championsway-page-1-lit.png", 1)]
        [TestCase("championsway-page-1-claimed.png", 1)]
        public void Kills(string screenshot, int expected) =>
            Assert.That(ChampionsWayTask.ReadKills(Fixtures.Load(screenshot)), Is.EqualTo(expected));

        [TestCase("championsway-page-0.png", new int[0])]
        [TestCase("championsway-page-1-lit.png", new[] { 1 })]          // the first chest glows
        [TestCase("championsway-page-1-claimed.png", new int[0])]       // opened after the claim
        public void LitChests(string screenshot, int[] expected) =>
            Assert.That(ChampionsWayTask.LitChests(Fixtures.Load(screenshot)), Is.EqualTo(expected));

        [TestCase("championsway-page-0.png", true)]
        [TestCase("championsway-page-1-lit.png", true)]
        [TestCase("championsway-page-attack.png", true)]   // "Attack Elite Rebel": the rebel found before is still up
        [TestCase("championsway-tab-strip.png", false)]   // Alliance Mobilization page
        [TestCase("championsway-rewards.png", false)]
        public void Page(string screenshot, bool expected)
        {
            var task = new ChampionsWayTask();
            var shot = Fixtures.Load(screenshot);
            Assert.That(task.IsPage(shot, 0, Logger), Is.EqualTo(expected));
            Assert.That(task.RebelButton(shot, 0, Logger) != null, Is.EqualTo(expected));
        }

        [TestCase("championsway-page-0.png", true)]
        [TestCase("championsway-page-attack.png", false)]   // the rebel is up
        public void FindButton(string screenshot, bool expected) =>
            Assert.That(new ChampionsWayTask().IsFindButton(Fixtures.Load(screenshot), 0, Logger), Is.EqualTo(expected));

        [TestCase("championsway-tab-strip.png", true)]
        [TestCase("championsway-page-0.png", false)]   // selected: only its icon shows
        public void Tab(string screenshot, bool expected) =>
            Assert.That(new ChampionsWayTask().HasTab(Fixtures.Load(screenshot), 0, Logger), Is.EqualTo(expected));

        [TestCase("championsway-deploy.png", true)]       // "Deploy" over the stamina cost 25
        [TestCase("championsway-rebel-card.png", false)]
        [TestCase("championsway-page-0.png", false)]
        public void DeployScreen(string screenshot, bool expected) =>
            Assert.That(new ChampionsWayTask().IsDeployScreen(Fixtures.Load(screenshot), 0, Logger), Is.EqualTo(expected));

        [TestCase("championsway-stamina-getmore.png", true, true, true)]     // 21/200, Comforting Embrace "Claim"
        [TestCase("championsway-stamina-cooldown.png", true, false, true)]   // 71/200 after the claim: "Skill on cooldown"
        [TestCase("championsway-stamina-used.png", true, false, true)]       // after one Use: the x18 bubbles beside the buttons
        [TestCase("championsway-deploy.png", false, false, false)]
        public void StaminaPopup(string screenshot, bool popup, bool freeClaim, bool use)
        {
            var task = new ChampionsWayTask();
            var shot = Fixtures.Load(screenshot);
            Assert.That(task.IsStaminaPopup(shot, 0, Logger), Is.EqualTo(popup));
            Assert.That(task.HasFreeStamina(shot, 0, Logger), Is.EqualTo(freeClaim));
            Assert.That(task.HasStaminaUse(shot, 0, Logger), Is.EqualTo(use));
        }

        [TestCase("championsway-rebel-card.png", true)]   // "Lv. 1 Elite Rebel", Rally 25
        [TestCase("championsway-page-0.png", false)]
        [TestCase("championsway-rewards.png", false)]
        public void RebelCard(string screenshot, bool expected) =>
            Assert.That(new ChampionsWayTask().IsRebelCard(Fixtures.Load(screenshot), 0, Logger), Is.EqualTo(expected));
    }
}
