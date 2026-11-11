using System.Drawing;
using Bot.Core.ImageDetection;
using Bot.Core.Logging;
using Bot.Core.Tasks.Modules;
using NUnit.Framework;

namespace Bot.Core.IntegrationTests
{
    /// <summary>
    /// The bear hunt's own rally, joins and pet buffs on screenshots of the 2026-11-08 Trap 1 bear (the user's own rally
    /// and joins on [KFC]Asli, the pet buffs on Quick Use). Everything but the part a test looks at is blacked out.
    /// </summary>
    [TestFixture]
    public class BearHuntScreenTests
    {
        private static bool Found(string screenshot, string template, Rectangle area, double threshold)
        {
            using var matcher = new UnifiedTemplateMatchingService(new LogService(0));
            var path = Path.Combine(AppContext.BaseDirectory, "templates", "images", "bearhunt", template);
            var result = matcher.MatchTemplate(Fixtures.Load(screenshot), path, 0, threshold: threshold,
                scales: new[] { 1.0 }, verboseLogging: false, searchArea: area);
            return result.found;
        }

        [TestCase("bearhunt-bear-icon.png", true)]      // 20:05:00, the trap just opened: "30min" under the bear
        [TestCase("bearhunt-before-open.png", false)]   // 20:04:59, "opening in 1s": the icon isn't there yet
        [TestCase("bearhunt-bear-card.png", false)]
        public void BearIcon(string screenshot, bool present) =>
            Assert.That(Found(screenshot, BearHuntTask.BearIconTemplate, BearHuntTask.BearIconArea, BearHuntTask.BearThreshold), Is.EqualTo(present));

        [Test]
        public void BearCard_TitleAndRally()
        {
            Assert.Multiple(() =>
            {
                Assert.That(Found("bearhunt-bear-card.png", BearHuntTask.BearTitleTemplate, BearHuntTask.BearCardArea, BearHuntTask.BearThreshold), Is.True);
                Assert.That(Found("bearhunt-bear-card.png", BearHuntTask.BearRallyTemplate, BearHuntTask.BearCardArea, BearHuntTask.BearThreshold), Is.True);
                Assert.That(Found("bearhunt-bear-icon.png", BearHuntTask.BearTitleTemplate, BearHuntTask.BearCardArea, BearHuntTask.BearThreshold), Is.False);
                Assert.That(Found("bearhunt-hold-rally.png", BearHuntTask.BearRallyTemplate, BearHuntTask.BearCardArea, BearHuntTask.BearThreshold), Is.False);
            });
        }

        [TestCase("bearhunt-hold-rally.png", true)]
        [TestCase("bearhunt-bear-card.png", false)]
        [TestCase("bearhunt-pet-confirm.png", false)]
        public void HoldARally(string screenshot, bool present) =>
            Assert.That(Found(screenshot, BearHuntTask.HoldRallyTemplate, BearHuntTask.HoldRallyArea, BearHuntTask.BearThreshold), Is.EqualTo(present));

        /// <summary>A War row joined during the bear: its green "+" and "Lv. 1 Raging Bear" under the members</summary>
        [Test]
        public void WarRow_TargetsTheBear()
        {
            var buttons = RallyTaskBase.FindJoinButtons(Fixtures.Load("bearhunt-war-bear-row.png"));

            Assert.That(buttons, Has.Count.EqualTo(1));
            Assert.Multiple(() =>
            {
                int y = buttons[0].Center.Y;
                Assert.That(Found("bearhunt-war-bear-row.png", BearHuntTask.BearTitleTemplate, BearHuntTask.BearRowArea(y), BearHuntTask.BearRowThreshold), Is.True);
                Assert.That(Found("bearhunt-war-bear-row.png", BearHuntTask.BearTitleTemplate, BearHuntTask.BearRowArea(y - 140), BearHuntTask.BearRowThreshold), Is.False, "another row's spot");
            });
        }

        [TestCase("bearhunt-pet-quick-use.png", true)]   // a battle skill selected, the skills ready
        [TestCase("bearhunt-pet-active.png", false)]     // just used: "Active: 02:00:00" instead of the buttons
        public void PetQuickUse(string screenshot, bool present) =>
            Assert.That(Found(screenshot, BearHuntTask.PetQuickUseTemplate, BearHuntTask.PetQuickUseArea, RallyTaskBase.Threshold), Is.EqualTo(present));

        [TestCase("bearhunt-pet-quick-use.png", true)]
        [TestCase("bearhunt-hold-rally.png", false)]
        public void PetBattleBadge(string screenshot, bool present) =>
            Assert.That(Found(screenshot, BearHuntTask.PetBattleBadgeTemplate, BearHuntTask.PetGridArea, RallyTaskBase.Threshold), Is.EqualTo(present));

        [TestCase("bearhunt-pet-confirm.png", true)]
        [TestCase("bearhunt-pet-quick-use.png", false)]
        public void PetConfirmation(string screenshot, bool present) =>
            Assert.That(Found(screenshot, BearHuntTask.PetConfirmTitleTemplate, BearHuntTask.PetConfirmTitleArea, RallyTaskBase.Threshold), Is.EqualTo(present));

        // War list of 2026-11-10 21:07 (other leaders blacked out): the "+" centres are at y 375 and 782.
        // Asli's own rally was nearly full (27,480 free, skip); the second had room (join).
        [TestCase(375, 1_775_230, 1_802_710, true)]
        [TestCase(782, 337_710, 1_679_210, false)]
        public void ReadsTheRallyTroops(int plusY, long current, long max, bool skip)
        {
            string text = BearHuntTask.ReadRallyTroops(Fixtures.Load("bearhunt-war-troops.png"), plusY);
            long? free = BearHuntTask.FreeTroops(text);

            Assert.Multiple(() =>
            {
                Assert.That(free, Is.EqualTo(max - current), text);
                Assert.That(free < BearHuntTask.MinFreeTroops, Is.EqualTo(skip));
            });
        }

        // The "Rallying: 00:03:02" countdowns of the same War list (read off the screenshot by eye)
        [TestCase(375, 3, 2)]
        [TestCase(782, 3, 45)]
        public void ReadsTheRallyTimer(int plusY, int minutes, int seconds)
        {
            using var image = OpenCvSharp.Cv2.ImDecode(Fixtures.Load("bearhunt-war-troops.png"), OpenCvSharp.ImreadModes.Color);
            string text = BearHuntTask.ReadRallyTimer(image, plusY);
            Assert.That(BearHuntTask.ParseRallyTimer(text), Is.EqualTo(new TimeSpan(0, minutes, seconds)), text);
        }

        [TestCase("176,610/1,481,610", 1_305_000L)]
        [TestCase("176.610 / 1.481.610", 1_305_000L)]
        [TestCase("", null)]
        [TestCase("1,481,610/176,610", null)]   // misread: more than the maximum
        public void FreeTroops(string text, long? free) => Assert.That(BearHuntTask.FreeTroops(text), Is.EqualTo(free));
    }
}
