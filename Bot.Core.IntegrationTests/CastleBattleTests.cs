using Bot.Core.Logging;
using Bot.Core.Services;
using Bot.Core.Tasks.Modules;
using NUnit.Framework;

namespace Bot.Core.IntegrationTests
{
    /// <summary>
    /// Castle Battle detection on screenshots of a terror rally (the same War list and deploy screen as the castle battle),
    /// llsa joining Asli's Titan Roc rally on 2026-11-07. The march tracker screenshots keep only the tracker (the map
    /// around it, with other players' names, is blacked out).
    /// </summary>
    [TestFixture]
    public class CastleBattleTests
    {
        [Test]
        public void FindsTheGreenJoinButton()
        {
            var buttons = CastleBattleTask.FindJoinButtons(Fixtures.Load("castle-war-rally-open.png"));

            Assert.That(buttons, Has.Count.EqualTo(1));
            Assert.That(buttons[0].Center.X, Is.InRange(630, 652));
            Assert.That(buttons[0].Center.Y, Is.InRange(380, 400));
            Assert.That(buttons[0].Defensive, Is.False, "red Rally header");
        }

        /// <summary>Castle battle 2026-11-07: a blue "Def. Lineup" row for our South Turret (names blacked out)</summary>
        [Test]
        public void DefLineupRowIsDefensive()
        {
            var buttons = CastleBattleTask.FindJoinButtons(Fixtures.Load("castle-war-def-lineup.png"));

            Assert.That(buttons, Has.Count.EqualTo(1));
            Assert.That(buttons[0].Defensive, Is.True);
        }

        // after joining the "+" turns grey; the deploy screen's small green Battle Bonus "+" is no join button
        [TestCase("castle-war-rally-joined.png")]
        [TestCase("castle-deploy-screen.png")]
        public void NoJoinButton(string screenshot)
        {
            Assert.That(CastleBattleTask.FindJoinButtons(Fixtures.Load(screenshot)), Is.Empty);
        }

        [TestCase("castle-march-rallying.png", CastleBattlePhase.JoinedWaitingToMarch)]
        [TestCase("castle-march-marching.png", CastleBattlePhase.MarchingToTarget)]
        [TestCase("castle-march-returning.png", CastleBattlePhase.MarchingBack)]
        public void ReadsTheMarchTracker(string screenshot, CastleBattlePhase expected)
        {
            var info = CastleBattleTask.ReadMarchTracker(Fixtures.Load(screenshot), new LogService(0));

            Assert.That((info.Busy, info.Total), Is.EqualTo((1, 6)), $"row '{info.FirstRow}'");
            Assert.That(CastleBattleTask.Classify(info), Is.EqualTo(expected), $"row '{info.FirstRow}'");
        }

        // the heal icon's percentage: orange 89% (2026-11-07 12:29), green 36% (12:07)
        [TestCase("castle-heal-89.png", 89)]
        [TestCase("castle-heal-36.png", 36)]
        public void ReadsTheHealPercentage(string screenshot, int expected)
        {
            Assert.That(CastleBattleTask.ReadHealPercent(Fixtures.Load(screenshot), new LogService(0)), Is.EqualTo(expected));
        }

        // Heal Injured rows (popup only, the map around it blacked out): the injured count per troop type
        [TestCase("castle-heal-popup-a.png", 40_811, 15_516, 24_680)]
        [TestCase("castle-heal-popup-b.png", 38_790, 15_516, 50_250)]
        public void ReadsTheInjuredPerTroopType(string screenshot, int infantry, int cavalry, int archers)
        {
            var injured = CastleBattleTask.ReadInjured(Fixtures.Load(screenshot), new LogService(0));

            Assert.That(injured, Is.EqualTo(new int?[] { infantry, cavalry, archers }));
        }

        // no injured cavalry: its row is gone and the archers move up; the empty third row must read as absent,
        // or the bot types into nothing and the input bar's OK lands on the bottom bar's Town
        [Test]
        public void MissingTroopTypeHasNoRow()
        {
            var injured = CastleBattleTask.ReadInjured(Fixtures.Load("castle-heal-popup-two-rows.png"), new LogService(0));

            Assert.That(injured, Is.EqualTo(new int?[] { 88_855, 56_777, null }));
        }

        [Test]
        public void TeamFlagsAlongTheTop()
        {
            Assert.That(CastleBattleTask.FlagPoint(1), Is.EqualTo(new System.Drawing.Point(63, 122)));
            Assert.That(CastleBattleTask.FlagPoint(2), Is.EqualTo(new System.Drawing.Point(137, 122)));
        }
    }
}
