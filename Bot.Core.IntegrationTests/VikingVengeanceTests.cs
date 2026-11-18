using System.Drawing;
using Bot.Core.Tasks.Modules;
using NUnit.Framework;

namespace Bot.Core.IntegrationTests
{
    /// <summary>
    /// Viking Vengeance's reinforcing (Asli, 2026-11-18): Alliance Members with the R3 group open (three members online),
    /// the R4 group with only our own card online, the member popup (Squad Help next to the red Remove) and the
    /// Reinforce popup empty and after our 184,710 troops arrived. The members' names are blacked out.
    /// </summary>
    [TestFixture]
    public class VikingVengeanceTests
    {
        [Test]
        public void OnlineLabels_R3Open_ThreeMembersTopToBottom()
        {
            var labels = new VikingVengeanceTask().FindOnlineLabels(Fixtures.Load("viking-members-r3-open.png"), 611);
            Assert.That(labels.Select(l => (l.X, l.Y)), Is.EqualTo(new[] { (62, 712), (404, 712), (62, 860) }));
        }

        [Test]
        public void OnlineLabels_R4_OnlyOurOwnCard() =>
            Assert.That(new VikingVengeanceTask().FindOnlineLabels(Fixtures.Load("viking-members-r4-own-card.png"), 540).Select(l => (l.X, l.Y)),
                Is.EqualTo(new[] { (62, 642) }));

        [Test]
        public void OnlineLabels_NoneAboveTheGroup() =>
            // the header and the R5 card ("4 hour(s) ago") are above the scan
            Assert.That(new VikingVengeanceTask().FindOnlineLabels(Fixtures.Load("viking-members-r3-open.png"), 900), Is.Empty);

        [TestCase("viking-reinforce-empty.png", 1_040_000)]
        [TestCase("viking-reinforce-partly.png", 1_040_000 - 184_710)]
        public void Capacity_FreeTroops(string screenshot, long expected)
        {
            string text = VikingVengeanceTask.ReadCapacity(Fixtures.Load(screenshot));
            Assert.That(BearHuntTask.FreeTroops(text), Is.EqualTo(expected), $"read '{text}'");
        }
    }
}
