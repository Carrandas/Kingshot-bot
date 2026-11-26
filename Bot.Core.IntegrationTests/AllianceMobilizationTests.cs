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
        private static int Column(CardCheck card)
        {
            if (card.Icon.X < 250) return 0;
            return card.Icon.X < 460 ? 1 : 2;
        }

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

        // --- Own missions and the free attempt (llsa 2026-11-25) ---

        [Test]
        public void OwnRow_TwoOpenMissions_NoneOnceAcceptedAndRefreshing()
        {
            using var open = Cv2.ImDecode(Fixtures.Load("mobilization-own-missions.png"), ImreadModes.Grayscale);
            using var done = Cv2.ImDecode(Fixtures.Load("mobilization-own-accepted.png"), ImreadModes.Grayscale);
            var task = new AllianceMobilizationTask();

            Assert.Multiple(() =>
            {
                // fist +860 and Truegold +350
                Assert.That(task.FindOwnCoins(open).Select(p => p.X), Is.EqualTo(new[] { 153, 473 }).Within(3));
                // accepted fist (avatar + progress bar) and a refreshing slot with its "Refresh 500" gems button
                Assert.That(task.FindOwnCoins(done), Is.Empty);
                // a finished card still shows its coin (and the Completed button), so the claim runs first
                using var claimed = Cv2.ImDecode(Fixtures.Load("mobilization-own-claimed.png"), ImreadModes.Grayscale);
                Assert.That(task.FindOwnCoins(claimed).Select(p => p.X), Is.EqualTo(new[] { 473 }).Within(3));
            });
        }

        [Test]
        public void OwnRow_BonusBadges()
        {
            var shot = Fixtures.Load("mobilization-own-missions.png");
            using var gray = Cv2.ImDecode(shot, ImreadModes.Grayscale);
            var coins = new AllianceMobilizationTask().FindOwnCoins(gray);

            // fist 200% (+860 = 430 base), Truegold 120% (+350)
            Assert.That(coins.Select(c => AllianceMobilizationTask.ReadOwnBonus(shot, c)), Is.EqualTo(new int?[] { 200, 120 }));
            // the open grid's gift card has no badge
            Assert.That(AllianceMobilizationTask.ReadOwnBonus(shot, new System.Drawing.Point(101, 1120)), Is.EqualTo(100));
        }

        [TestCase(520, 200, 260)]   // Asli's +520 troop mission, completed as +260
        [TestCase(860, 200, 430)]
        [TestCase(350, 100, 350)]
        public void BasePoints(int shown, int bonus, int expected) =>
            Assert.That(AllianceMobilizationTask.BasePoints(shown, bonus), Is.EqualTo(expected));

        [Test]
        public void OpenCardPoints_ReadOffTheCard()
        {
            var shot = Fixtures.Load("mobilization-own-missions.png");
            using var gray = Cv2.ImDecode(shot, ImreadModes.Grayscale);
            var cards = new AllianceMobilizationTask().CheckCards(gray).Where(c => !c.CutOff).ToList();

            // gift +1,050, blue gems +360, lion (Terror) +200
            Assert.That(cards.Select(c => AllianceMobilizationTask.ReadCardPoints(shot, c.Icon)), Is.EqualTo(new int?[] { 1050, 360, 200 }));
        }

        [TestCase("mobilization-own-missions.png", true)]
        [TestCase("mobilization-own-accepted.png", false)]   // free attempt claimed: 7/11, no dot
        [TestCase("mobilization-hammer.png", false)]
        public void RedDotOnThePlus(string screenshot, bool dot)
        {
            Assert.That(AllianceMobilizationTask.HasRedDot(Fixtures.Load(screenshot)), Is.EqualTo(dot));
        }

        [TestCase("mobilization-own-missions.png", 7)]
        [TestCase("mobilization-own-accepted.png", 7)]
        [TestCase("mobilization-hammer.png", 9)]
        public void Attempts(string screenshot, int attempts)
        {
            Assert.That(AllianceMobilizationTask.ReadAttempts(Fixtures.Load(screenshot)), Is.EqualTo(attempts));
        }

        [TestCase("mobilization-exclusive-troops.png", "training", 860, "Accept")]
        [TestCase("mobilization-exclusive-truegold.png", "Truegold", 350, "Refresh")]
        [TestCase("mobilization-open-terror.png", "Terror", 200, "Refresh")]
        [TestCase("mobilization-exclusive-troops-260-synthetic.png", "training", 260, "Refresh")]   // "+2" pasted from the Terror popup: was read as 1260 (llsa 2026-11-26)   // Terror but under 300
        public void ExclusivePopup_DescriptionAndPointsDecide(string screenshot, string word, int points, string action)
        {
            var (description, read) = AllianceMobilizationTask.ReadMissionPopup(Fixtures.Load(screenshot));

            Assert.Multiple(() =>
            {
                Assert.That(description, Does.Contain(word), description);
                Assert.That(read, Is.EqualTo(points));
                Assert.That(AllianceMobilizationTask.DecideOwnMission(description, read, new Bot.Core.Models.AllianceMobilizationSettings()).ToString(), Is.EqualTo(action));
            });
        }

        [TestCase("accept-mission.png", 200, 740, 490, 130, "mobilization-exclusive-troops.png")]
        [TestCase("free-attempt.png", 180, 720, 360, 140, "mobilization-free-attempt.png")]
        [TestCase("attempt-purchases-title.png", 120, 360, 480, 100, "mobilization-attempt-purchases.png")]
        [TestCase("completed.png", 0, 640, 720, 600, "mobilization-own-completed.png")]
        public void PopupButtons_OnlyOnTheirScreen(string template, int x, int y, int w, int h, string presentOn)
        {
            using var matcher = new UnifiedTemplateMatchingService(new LogService(0));
            var path = Path.Combine(AppContext.BaseDirectory, "templates", "images", "alliancemobilization", template);
            var shots = new[]
            {
                "mobilization-own-missions.png", "mobilization-own-accepted.png", "mobilization-free-attempt.png", "mobilization-attempt-purchases.png",
                "mobilization-exclusive-troops.png", "mobilization-exclusive-truegold.png", "mobilization-open-terror.png", "mobilization-hammer.png",
                "mobilization-own-completed.png", "mobilization-own-claimed.png",
            };
            Assert.Multiple(() =>
            {
                foreach (var shot in shots)
                {
                    var result = matcher.MatchTemplate(Fixtures.Load(shot), path, 0, threshold: 0.85, scales: new[] { 1.0 },
                        verboseLogging: false, searchArea: new System.Drawing.Rectangle(x, y, w, h));
                    bool expected = shot == presentOn || (template == "accept-mission.png" && shot is "mobilization-exclusive-truegold.png" or "mobilization-open-terror.png");
                    Assert.That(result.found, Is.EqualTo(expected), $"{shot}: {result.confidence:F2}");
                }
            });
        }
    }
}
