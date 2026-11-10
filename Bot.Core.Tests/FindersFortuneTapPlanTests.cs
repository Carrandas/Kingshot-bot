using Bot.Core.FindersFortune;
using NUnit.Framework;

namespace Bot.Core.Tests
{
    [TestFixture]
    public class FindersFortuneTapPlanTests
    {
        private static readonly (int X, int Y) WaterJug = (122, 1015); // under Give Up
        private static readonly (int X, int Y) Anchor = (390, 1020);   // under Extra Time
        private static readonly (int X, int Y) Shovel = (512, 765);    // under Confirm
        private static readonly (int X, int Y) Wrench = (167, 568);    // over no button

        private static IReadOnlyList<(int X, int Y)> Plan(int seconds, bool red, params (int X, int Y)[] spots) =>
            FindersFortuneTapPlan.NextBatch(spots, s => s, seconds, red);

        [TestCase(122, 1015)]
        [TestCase(390, 1020)]
        [TestCase(512, 765)]
        [TestCase(588, 735)] // Cloth, on the edge of Confirm
        [TestCase(90, 955)]  // Canteen, under Give Up on "Time's Up!"
        [TestCase(570, 985)] // Wheel, under Extra Time on "Time's Up!"
        public void IsOverProceedButton_CoversSmithySpotsUnderTheButtons(int x, int y)
        {
            Assert.That(FindersFortuneTapPlan.IsOverProceedButton(x, y), Is.True);
        }

        [Test]
        public void NextBatch_NeverSendsAnchorAndShovelTogether()
        {
            Assert.That(Plan(40, false, Anchor, Shovel), Has.Count.EqualTo(1));
        }

        [Test]
        public void NextBatch_SendsSafeSpotsBeforeDangerousOnes()
        {
            Assert.That(Plan(40, false, Anchor, Wrench), Is.EqualTo(new[] { Wrench }));
        }

        [TestCase(5, false)]
        [TestCase(25, true)] // "2s" misread as 25: the red timer still stops it
        public void NextBatch_HoldsBackDangerousSpotsNearTheEnd(int seconds, bool red)
        {
            Assert.That(Plan(seconds, red, WaterJug), Is.Empty);
        }

        [Test]
        public void NextBatch_AllowsADangerousSpotWithTimeToSpare()
        {
            Assert.That(Plan(6, false, WaterJug), Is.EqualTo(new[] { WaterJug }));
        }
    }
}
