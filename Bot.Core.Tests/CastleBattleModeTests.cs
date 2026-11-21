using Bot.Core.Models;
using Bot.Core.Services;
using NUnit.Framework;
using Item = Bot.Core.Services.BearHuntSchedule.TaskListItem;

namespace Bot.Core.Tests
{
    [TestFixture]
    public class CastleBattleModeTests
    {
        private static readonly DateTime Start = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);

        [Test]
        public void TaskList_CastleBattleRow_IsTheNextBattle_SanctuaryIsNot()
        {
            // the Task List of 2026-11-20: Sanctuary Battle rows, then Castle Battle at UTC 12:00 on 2026/11/21
            var items = new List<Item>
            {
                new(true, "2026/11/21"),
                new(false, "Sanctuary Battle", "UTC\n05:35"),
                new(false, "Castle Battle", "UTC\n12:00"),
                new(true, "2026/11/22"),
                new(false, "Bear Hunt - Trap 2", "UTC\n13:00"),
            };
            var now = new DateTime(2026, 11, 20, 15, 52, 0, DateTimeKind.Utc);
            Assert.That(BearHuntSchedule.NextCastleBattle(items, now, TimeSpan.FromHours(5)), Is.EqualTo(Start));
        }

        [Test]
        public void IsDue_FromThePrepTimeUntilShortlyBeforeTheEnd()
        {
            var s = new CastleBattleSettings { NextBattleUtc = Start, PrepMinutes = 15, MaxHours = 5 };
            Assert.Multiple(() =>
            {
                Assert.That(CastleBattleMode.IsDue(s, Start.AddMinutes(-16)), Is.False);
                Assert.That(CastleBattleMode.IsDue(s, Start.AddMinutes(-15)), Is.True);
                Assert.That(CastleBattleMode.IsDue(s, Start.AddHours(4)), Is.True);
                Assert.That(CastleBattleMode.IsDue(s, Start.AddHours(5).AddMinutes(-4)), Is.False);
            });
        }

        [Test]
        public void FindDue_SkipsWhenOffOrAlreadyRun()
        {
            var account = new AccountSettings();
            account.CastleBattleSettings.NextBattleUtc = Start.AddDays(30);   // a start no other test marks as handled
            var at = Start.AddDays(30).AddMinutes(-10);
            account.CastleBattleSettings.Enabled = false;
            Assert.That(CastleBattleMode.FindDue(new[] { account }, at).Account, Is.Null);

            account.CastleBattleSettings.Enabled = true;
            Assert.That(CastleBattleMode.FindDue(new[] { account }, at).StartUtc, Is.EqualTo(Start.AddDays(30)));

            CastleBattleMode.MarkHandled(Start.AddDays(30));   // stopped by hand: not started again
            Assert.That(CastleBattleMode.FindDue(new[] { account }, at).Account, Is.Null);
        }

        [Test]
        public void ResolveStart_SavedBattleWhileDue_ElseNow_AndPrepOnlyBeforeTheStart()
        {
            var s = new CastleBattleSettings { NextBattleUtc = Start, PrepMinutes = 15 };
            var early = Start.AddMinutes(-10);
            var later = Start.AddDays(-1);
            Assert.Multiple(() =>
            {
                Assert.That(CastleBattleMode.ResolveStart(s, null, early), Is.EqualTo(Start));
                Assert.That(CastleBattleMode.ResolveStart(s, null, later), Is.EqualTo(later));
                Assert.That(CastleBattleMode.NeedsPrep(Start, early), Is.True);
                Assert.That(CastleBattleMode.NeedsPrep(later, later), Is.False);
            });
        }

        [TestCase("#196 X:588 Y:592", 588, 592)]
        [TestCase("#196X:5 Y:1100", 5, 1100)]
        public void ParseCoordinates_TheLastTwoNumbers(string text, int x, int y)
        {
            Assert.That(Bot.Core.Tasks.Modules.CastleBattleTask.ParseCoordinates(text), Is.EqualTo((x, y)));
        }

        [TestCase("X:588 Y:592")]   // no kingdom: which number is which is unsure
        [TestCase("#196 X:588 Y:5920")]
        [TestCase("")]
        public void ParseCoordinates_NullWhenUnsure(string text)
        {
            Assert.That(Bot.Core.Tasks.Modules.CastleBattleTask.ParseCoordinates(text), Is.Null);
        }

        [Test]
        public void HomeSpot_KeptWhenThePrepRunsAgainAfterTheTeleport()
        {
            var s = new CastleBattleSettings();
            Assert.That(s.SetHomeSpotFor("[KFC]llsa", 300, 410, Start), Is.True);
            Assert.That(s.SetHomeSpotFor("llsa", 588, 592, Start), Is.False, "same battle: the spot next to the castle is not saved");
            Assert.That(s.HomeSpotFor("[KFC]llsa")!.ToString(), Is.EqualTo("X:300 Y:410"));
            Assert.That(s.SetHomeSpotFor("llsa", 320, 400, Start.AddDays(14)), Is.True, "the next battle saves anew");
            Assert.That(s.HomeSpotFor("llsa")!.X, Is.EqualTo(320));
        }

        [Test]
        public void End_ForgetsTheBattleOnlyOnceItIsOver()
        {
            var s = new CastleBattleSettings { NextBattleUtc = Start, MaxHours = 5 };
            CastleBattleMode.End(s, Start.AddHours(1));
            Assert.That(s.NextBattleUtc, Is.EqualTo(Start));
            CastleBattleMode.End(s, Start.AddHours(5));
            Assert.That(s.NextBattleUtc, Is.Null);
        }
    }
}
