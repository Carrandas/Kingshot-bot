using Bot.Core.Logging;
using Bot.Core.Tasks.Modules;
using NUnit.Framework;

namespace Bot.Core.IntegrationTests
{
    /// <summary>
    /// Collect VIP's "is the VIP screen in front" check. Towns, popups and other screens must never count as the VIP
    /// screen (the task taps the title bar to close "popups" while the check fails). The positive case needs a VIP
    /// screenshot (to add after the next VIP claim).
    /// </summary>
    [TestFixture]
    public class CollectVipTests
    {
        [TestCase("town-asli.png")]
        [TestCase("town-ilsa-events-dot.png")]
        [TestCase("popup-welcome-back.png")]
        [TestCase("training-panel-llsa-idle.png")]
        [TestCase("mobilization-hammer.png")]
        public void VipScreen_NotOnOtherScreens(string screenshot) =>
            Assert.That(new CollectVipTask().IsVipScreen(Fixtures.Load(screenshot), 0, new LogService(0)), Is.False);
    }
}
