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
    }
}
