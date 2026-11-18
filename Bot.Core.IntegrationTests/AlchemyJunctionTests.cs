using Bot.Core.Logging;
using Bot.Core.Services;
using Bot.Core.Tasks.Modules;
using NUnit.Framework;
using OpenCvSharp;
using System.Collections.Generic;
using System.Linq;

namespace Bot.Core.IntegrationTests
{
    /// <summary>
    /// Alchemy Junction screens played on 2026-11-18 (Asli orders 1-7, llsa 1-13). Expected values are what the screens
    /// showed: the jars, the green platforms, the splashes of the older rows and the brew count in the pill.
    /// </summary>
    [TestFixture]
    public class AlchemyJunctionTests
    {
        private static readonly LogService Logger = new(0);

        private static Mat Hsv(string screenshot) => AlchemyBoard.Hsv(Fixtures.Load(screenshot));

        [TestCase("alchemy-city-asli.png", true)]
        [TestCase("alchemy-city-llsa.png", true)]
        [TestCase("alchemy-missions-done.png", false)]
        [TestCase("alchemy-board-29.png", false)]
        public void EventIcon_ByItsPurpleHair(string screenshot, bool expected)
        {
            using var hsv = Hsv(screenshot);
            var icon = AlchemyJunctionTask.FindEventIcon(hsv);
            Assert.That(icon.HasValue, Is.EqualTo(expected));
            if (expected) Assert.That((icon!.Value.X, icon.Value.Y), Is.EqualTo((672, 432)).Within(15));
        }

        [Test]
        public void NewOrder_ThreeEmptySlots_ThreeJars()
        {
            using var hsv = Hsv("alchemy-board-29.png");
            Assert.That(AlchemyBoard.ReadSlots(hsv).Select(s => s.Green), Is.EqualTo(new[] { false, false, false }));
            Assert.That(AlchemyBoard.ReadTray(hsv).Select(j => j.Colour),
                Is.EqualTo(new[] { BrewColour.Blue, BrewColour.Yellow, BrewColour.Pink }));
        }

        [Test]
        public void Placing_ReadsTheJarsAlreadyOnTheRow()
        {
            using var hsv = Hsv("alchemy-board-placing.png");
            var slots = AlchemyBoard.ReadSlots(hsv);
            Assert.That(slots, Has.Count.EqualTo(3));
            Assert.That(slots.Select(s => AlchemyBoard.PlacedJar(hsv, s.X)),
                Is.EqualTo(new BrewColour?[] { BrewColour.Pink, BrewColour.Purple, null }));
            Assert.That(AlchemyBoard.ReadTray(hsv).Select(j => j.Colour), Is.EqualTo(new[] { BrewColour.Yellow }));
        }

        /// <summary>Order 13 of llsa: two failed rows above the current one, red and pink kept</summary>
        [Test]
        public void History_SplashesRuleColoursOut()
        {
            using var hsv = Hsv("alchemy-board-history.png");
            var slots = AlchemyBoard.ReadSlots(hsv);
            Assert.That(slots.Select(s => s.Green), Is.EqualTo(new[] { true, false, false, false, true, false }));
            var history = AlchemyBoard.ReadHistory(hsv, slots.Count);
            var expected = new Dictionary<int, HashSet<BrewColour>>
            {
                [0] = new() { BrewColour.Blue },
                [1] = new() { BrewColour.Red, BrewColour.Blue },
                [2] = new() { BrewColour.Yellow, BrewColour.Green },
                [3] = new() { BrewColour.Green, BrewColour.Yellow },
                [4] = new() { BrewColour.Purple },
                [5] = new() { BrewColour.Pink, BrewColour.Purple },
            };
            Assert.That(history.Keys.OrderBy(k => k), Is.EqualTo(expected.Keys));
            foreach (var (slot, colours) in expected) Assert.That(history[slot], Is.EquivalentTo(colours), $"slot {slot}");
            Assert.That(AlchemyBoard.ReadTray(hsv).Select(j => j.Colour),
                Is.EqualTo(new[] { BrewColour.Blue, BrewColour.Yellow, BrewColour.Green, BrewColour.Purple }));
        }

        /// <summary>Asli's order 7 after a day: green and pink placed, purple left, no brews (the workshop page said 2; a tap opened the shop)</summary>
        [Test]
        public void Resume_PlacedJarsHistoryAndNoBrews()
        {
            var shot = Fixtures.Load("alchemy-board-resume.png");
            using var hsv = AlchemyBoard.Hsv(shot);
            var slots = AlchemyBoard.ReadSlots(hsv);
            Assert.That(slots.Select(s => s.Green), Is.EqualTo(new[] { false, false, false, true, true }));
            Assert.That(slots.Take(3).Select(s => AlchemyBoard.PlacedJar(hsv, s.X)),
                Is.EqualTo(new BrewColour?[] { BrewColour.Green, BrewColour.Pink, null }));
            var history = AlchemyBoard.ReadHistory(hsv, slots.Count);
            Assert.That(history[0], Is.EquivalentTo(new[] { BrewColour.Purple }));
            Assert.That(history[1], Is.EquivalentTo(new[] { BrewColour.Green }));
            Assert.That(history[2], Is.EquivalentTo(new[] { BrewColour.Pink }));
            Assert.That(AlchemyBoard.ReadTray(hsv).Select(j => j.Colour), Is.EqualTo(new[] { BrewColour.Purple }));
            using var image = Cv2.ImDecode(shot, ImreadModes.Color);
            Assert.That(AlchemyBoard.ReadBrews(image), Is.EqualTo(0));
        }

        /// <summary>The move the task makes on each saved board (unique account keys: nothing saved from other tests)</summary>
        [Test]
        public void NextMove_FinishesTheResumedRow()
        {
            using var hsv = Hsv("alchemy-board-resume.png");
            var move = AlchemyJunctionTask.NextMove("test-resume", hsv);
            Assert.That(move, Is.Not.Null);
            Assert.That(move!.Value.JarX, Is.EqualTo(228).Within(20));
            Assert.That(move.Value.LastOpen, Is.True);
            Assert.That(move.Value.Row, Is.EquivalentTo(new Dictionary<int, BrewColour> { [0] = BrewColour.Green, [1] = BrewColour.Pink, [2] = BrewColour.Purple }));
        }

        [Test]
        public void NextMove_AvoidsExcludedColours()
        {
            using var hsv = Hsv("alchemy-board-history.png");
            var move = AlchemyJunctionTask.NextMove("test-history", hsv);
            Assert.That(move, Is.Not.Null);
            Assert.That(move!.Value.LastOpen, Is.False);
            var placed = move.Value.Row.Single();
            Assert.That(placed.Key, Is.EqualTo(1), "leftmost open slot");
            Assert.That(placed.Value, Is.Not.EqualTo(BrewColour.Red).And.Not.EqualTo(BrewColour.Blue), "red and blue failed in slot 1");
        }

        [Test]
        public void NextMove_NewOrderStartsAtSlotZero()
        {
            using var hsv = Hsv("alchemy-board-29.png");
            var move = AlchemyJunctionTask.NextMove("test-new", hsv);
            Assert.That(move, Is.Not.Null);
            Assert.That(move!.Value.LastOpen, Is.False);
            Assert.That(move.Value.Row.Keys, Is.EquivalentTo(new[] { 0 }));
        }

        [TestCase("alchemy-brew-pack-shop.png", true)]
        [TestCase("alchemy-board-resume.png", false)]
        public void BrewPackShop(string screenshot, bool expected) =>
            Assert.That(new AlchemyJunctionTask().IsBrewPackShop(Fixtures.Load(screenshot), 0, Logger), Is.EqualTo(expected));

        [TestCase("alchemy-board-29.png", 29)]
        [TestCase("alchemy-board-purple.png", 10)]
        [TestCase("alchemy-board-placing.png", 79)]
        [TestCase("alchemy-board-history.png", 0)]
        public void BrewCount(string screenshot, int expected)
        {
            using var image = Cv2.ImDecode(Fixtures.Load(screenshot), ImreadModes.Color);
            Assert.That(AlchemyBoard.ReadBrews(image), Is.EqualTo(expected));
        }

        [Test]
        public void Bonanza_ClaimAllButton()
        {
            var point = new AlchemyJunctionTask().FindBonanzaClaim(Fixtures.Load("alchemy-bonanza-claim-all.png"), 0, Logger);
            Assert.That(point, Is.Not.Null);
            Assert.That(point!.Value.Y, Is.InRange(1160, 1240));
        }

        /// <summary>No Claim All with one reward left: the free column's framed cell (level 14, y 870-1000)</summary>
        [Test]
        public void Bonanza_FramedCell()
        {
            var point = new AlchemyJunctionTask().FindBonanzaClaim(Fixtures.Load("alchemy-bonanza-one-left.png"), 0, Logger);
            Assert.That(point, Is.Not.Null);
            Assert.That(point!.Value.Y, Is.InRange(870, 1000));
        }

        [TestCase("alchemy-milestone-claimable.png", true)]
        [TestCase("alchemy-milestone-claimed.png", false)]
        public void Milestone_OnlyTheLitCard(string screenshot, bool expected)
        {
            using var hsv = Hsv(screenshot);
            var x = AlchemyJunctionTask.FindClaimableMilestone(hsv);
            Assert.That(x.HasValue, Is.EqualTo(expected));
            if (expected) Assert.That(x!.Value, Is.InRange(205, 335));
        }
    }
}
