using Bot.Core.Logging;
using Bot.Core.Tasks.Modules;
using NUnit.Framework;

namespace Bot.Core.IntegrationTests
{
    /// <summary>
    /// Governor Duel screens played on llsa, 2026-11-19 (10 challenges). Expected values are what the screens showed;
    /// the opponents' names are blacked out.
    /// </summary>
    [TestFixture]
    public class GovernorDuelTests
    {
        private static readonly LogService Logger = new(0);

        [TestCase("governorduel-challenge.png", "Challenge")]          // opponent matched, "Challenge 10/10"
        [TestCase("governorduel-find-opponent.png", "FindOpponent")]   // "Match not yet found"
        [TestCase("governorduel-result-win.png", "Result")]            // "You are victorious!"
        [TestCase("governorduel-result-defeat.png", "Result")]         // "You were defeated"
        [TestCase("governorduel-completed.png", "Completed")]          // "Today's Challenges Completed"
        [TestCase("governorduel-tab-strip.png", "Unknown")]            // Strongest Governor tab
        [TestCase("governorduel-rewards.png", "Unknown")]              // milestone rewards popup
        [TestCase("town-ilsa-events-dot.png", "Unknown")]
        public void Screen(string screenshot, string expected) =>
            Assert.That(new GovernorDuelTask().Classify(Fixtures.Load(screenshot), 0, Logger), Is.EqualTo(expected));

        [TestCase("governorduel-tab-strip.png", true)]
        [TestCase("governorduel-challenge.png", false)]   // selected: only its icon shows
        public void Tab(string screenshot, bool expected) =>
            Assert.That(new GovernorDuelTask().HasTab(Fixtures.Load(screenshot), 0, Logger), Is.EqualTo(expected));

        [TestCase("governorduel-challenge.png", true)]
        [TestCase("governorduel-completed.png", true)]
        [TestCase("governorduel-tab-strip.png", false)]
        public void Title(string screenshot, bool expected) =>
            Assert.That(new GovernorDuelTask().IsDuelTab(Fixtures.Load(screenshot), 0, Logger), Is.EqualTo(expected));

        [TestCase("governorduel-rewards.png", true)]
        [TestCase("governorduel-completed.png", false)]
        public void Rewards(string screenshot, bool expected) =>
            Assert.That(new GovernorDuelTask().IsRewards(Fixtures.Load(screenshot), 0, Logger), Is.EqualTo(expected));
    }
}
