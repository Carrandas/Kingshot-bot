using Bot.Core.ImageDetection;
using Bot.Core.Logging;
using Bot.Core.Tasks.Modules;
using NUnit.Framework;
using OpenCvSharp;
using CardCheck = Bot.Core.Tasks.Modules.AllianceMobilizationTask.CardCheck;

namespace Bot.Core.IntegrationTests
{
    /// <summary>
    /// Alliance Mobilization on saved screenshots. The expected results are what the user confirmed in the game,
    /// not what the code returned when the test was written.
    /// </summary>
    [TestFixture]
    public class AllianceMobilizationTests
    {
        // Columns of the card grid: 0 left, 1 middle, 2 right
        private static int Column(CardCheck card) => card.Icon.X < 250 ? 0 : card.Icon.X < 460 ? 1 : 2;

        private static string Describe(CardCheck card) => $"col {Column(card)} {(card.Keep != null ? "keep" : "refresh")}";

        private static List<CardCheck> CheckCards(string screenshot)
        {
            using var gray = Cv2.ImDecode(Fixtures.Load(screenshot), ImreadModes.Grayscale);
            return new AllianceMobilizationTask().CheckCards(gray);
        }

        [Test]
        public void HammerIsRefreshed_GiftIsKept()
        {
            var cards = CheckCards("mobilization-hammer.png");

            Assert.That(cards.Where(c => !c.CutOff).Select(Describe), Is.EqualTo(new[]
            {
                "col 2 keep",     // gift
                "col 2 refresh",  // hammer: was skipped live before the slow scroll fix
            }));
        }

        [Test]
        public void YellowGemIsRefreshed_BlueGemsAndGiftsAreKept()
        {
            var cards = CheckCards("mobilization-yellow-gem.png");

            Assert.That(cards.Where(c => !c.CutOff).Select(Describe), Is.EqualTo(new[]
            {
                "col 0 keep",     // gift +500
                "col 2 keep",     // gift +1,050
                "col 0 keep",     // blue gems +670
                "col 2 refresh",  // yellow gem +340: only blue gems are kept
            }));
            // The top row shows only its coins; those cards are judged after a scroll
            Assert.That(cards.Where(c => c.CutOff).Select(c => c.Icon.Top), Has.All.LessThan(645));
        }

        [TestCase("town-asli.png", true)]
        [TestCase("town-ilsa-events-dot.png", true)]      // red dot on the animated icon: scored 0.79
        [TestCase("mobilization-hammer.png", false)]      // already on the Events screen
        [TestCase("popup-welcome-back.png", false)]       // dark popup background
        public void EventsButton(string screenshot, bool present)
        {
            using var matcher = new UnifiedTemplateMatchingService(new LogService(0));
            var template = Path.Combine(AppContext.BaseDirectory, "templates", "images", "alliancemobilization",
                AllianceMobilizationTask.EventsButtonTemplate);

            var result = matcher.MatchTemplate(Fixtures.Load(screenshot), template, 0,
                threshold: AllianceMobilizationTask.EventsThreshold, scales: new[] { 1.0 }, verboseLogging: false,
                searchArea: AllianceMobilizationTask.EventsButtonArea);

            Assert.That(result.found, Is.EqualTo(present), $"confidence {result.confidence:F2}");
        }
    }
}
