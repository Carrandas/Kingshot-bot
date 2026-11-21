using Bot.Core.Logging;
using Bot.Core.Tasks.Modules;
using NUnit.Framework;

namespace Bot.Core.IntegrationTests
{
    /// <summary>
    /// Voyage of Light (Deals hub): the bottom-left button is a green Claim All while ships are back, then a teal
    /// Dispatch All, and a grey Dispatch All while the ships are out (seen 2026-11-18). The grey one has the same
    /// shape, so only the colour check keeps it from being tapped.
    /// </summary>
    [TestFixture]
    public class VoyageOfLightTests
    {
        private static readonly LogService Logger = new(0);

        [TestCase("voyage-claim-all.png", VoyageOfLightTask.ClaimAllTemplate, true)]
        [TestCase("voyage-claim-all.png", VoyageOfLightTask.DispatchAllTemplate, false)]
        [TestCase("voyage-dispatch-all.png", VoyageOfLightTask.ClaimAllTemplate, false)]
        [TestCase("voyage-dispatch-all.png", VoyageOfLightTask.DispatchAllTemplate, true)]
        [TestCase("voyage-ships-out.png", VoyageOfLightTask.ClaimAllTemplate, false)]
        [TestCase("voyage-ships-out.png", VoyageOfLightTask.DispatchAllTemplate, false)]
        [TestCase("voyage-other-deals-tab.png", VoyageOfLightTask.ClaimAllTemplate, false)]
        [TestCase("voyage-other-deals-tab.png", VoyageOfLightTask.DispatchAllTemplate, false)]
        public void Button_OnlyWhenActive(string screenshot, string template, bool expected) =>
            Assert.That(new VoyageOfLightTask().FindActiveButton(Fixtures.Load(screenshot), template, 0, Logger).HasValue, Is.EqualTo(expected));

        [TestCase("voyage-claim-all.png", true)]
        [TestCase("voyage-ships-out.png", true)]
        [TestCase("voyage-other-deals-tab.png", false)]
        [TestCase("town-asli.png", false)]
        public void VoyageTab_ByTitle(string screenshot, bool expected) =>
            Assert.That(new VoyageOfLightTask().IsVoyageTab(Fixtures.Load(screenshot), 0, Logger), Is.EqualTo(expected));

        [TestCase("voyage-other-deals-tab.png", true)]
        [TestCase("town-asli.png", false)]
        public void ShipTab_InTheStrip(string screenshot, bool expected) =>
            Assert.That(new VoyageOfLightTask().HasShipTab(Fixtures.Load(screenshot), 0, Logger), Is.EqualTo(expected));

        // Sails Aloft Pack (the "+" of the token pill): only the green Free of the Exploration Gift, never the paid rows
        [TestCase("voyage-pack-free.png", true)]
        [TestCase("voyage-pack-claimed.png", false)]
        [TestCase("voyage-treasures-merge.png", false)]
        public void FreeGift_OnlyWhileFree(string screenshot, bool expected) =>
            Assert.That(new VoyageOfLightTask().FindFreeGift(Fixtures.Load(screenshot), 0, Logger).HasValue, Is.EqualTo(expected));

        // My Treasures as seen on Asli 2026-11-21: Merge on the top chests, Enable on the bottom ones, in press order
        [TestCase("voyage-treasures-merge.png", "Common merge,Exquisite enable")]
        [TestCase("voyage-treasures-enable.png", "Exquisite enable,Majestic enable")]
        [TestCase("voyage-treasures-done.png", "")]
        public void Treasures_ActiveButtons(string screenshot, string expected) =>
            Assert.That(string.Join(",", VoyageOfLightTask.ActiveTreasureButtons(Fixtures.Load(screenshot)).Select(b => b.Name)), Is.EqualTo(expected));

        [TestCase("voyage-claim-chests.png", true)]     // with the red dot (gift not claimed yet)
        [TestCase("voyage-ships-out.png", true)]
        [TestCase("voyage-treasures-merge.png", false)]
        [TestCase("voyage-pack-free.png", false)]
        public void TokenPlus_OnTheVoyagePage(string screenshot, bool expected) =>
            Assert.That(new VoyageOfLightTask().HasTokenPlus(Fixtures.Load(screenshot), 0, Logger), Is.EqualTo(expected));

        [TestCase("voyage-treasures-merge.png", true)]
        [TestCase("voyage-claim-chests.png", false)]
        [TestCase("voyage-pack-free.png", false)]
        public void TreasuresPage_ByText(string screenshot, bool expected) =>
            Assert.That(new VoyageOfLightTask().IsTreasuresPage(Fixtures.Load(screenshot), 0, Logger), Is.EqualTo(expected));
    }
}
