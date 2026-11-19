using Bot.Core.Models;
using Bot.Core.Services;
using NUnit.Framework;

namespace Bot.Core.Tests
{
    /// <summary>When the Viking Vengeance mode starts, the send order (main character last) and the Task List row</summary>
    [TestFixture]
    public class VikingVengeanceModeTests
    {
        private static readonly DateTime Viking = new(2026, 11, 19, 20, 0, 0, DateTimeKind.Utc);

        private static AccountSettings Account(DateTime? next, bool enabled = true, string main = "", params string[] characters)
        {
            var account = new AccountSettings { AccountName = "A", InstanceNumber = 0 };
            foreach (var name in characters) account.Characters.Add(new CharacterEntry { Name = name, Enabled = true });
            account.VikingVengeanceSettings.NextVikingUtc = next;
            account.VikingVengeanceSettings.Enabled = enabled;
            account.VikingVengeanceSettings.MainCharacter = main;
            return account;
        }

        [Test]
        public void FindDue_FromTheTwentyMinutePrepUntilShortlyBeforeTheEnd()
        {
            var account = Account(Viking);
            Assert.Multiple(() =>
            {
                Assert.That(VikingVengeanceMode.FindDue(new[] { account }, Viking.AddMinutes(-21)).Account, Is.Null);
                Assert.That(VikingVengeanceMode.FindDue(new[] { account }, Viking.AddMinutes(-20)).StartUtc, Is.EqualTo(Viking));
                Assert.That(VikingVengeanceMode.FindDue(new[] { account }, Viking.AddMinutes(20)).Account, Is.SameAs(account));
                Assert.That(VikingVengeanceMode.FindDue(new[] { account }, Viking.AddMinutes(31)).Account, Is.Null);
            });
        }

        [Test]
        public void FindDue_NotWhenSwitchedOffOrUnknown()
        {
            Assert.Multiple(() =>
            {
                Assert.That(VikingVengeanceMode.FindDue(new[] { Account(Viking, enabled: false) }, Viking).Account, Is.Null);
                Assert.That(VikingVengeanceMode.FindDue(new[] { Account(null) }, Viking).Account, Is.Null);
            });
        }

        [Test]
        public void SendOrder_MainCharacterLast()
        {
            Assert.Multiple(() =>
            {
                Assert.That(VikingVengeanceMode.SendOrder(Account(Viking, true, "Asli", "Asli", "llsa"), null), Is.EqualTo(new[] { "llsa", "Asli" }));
                Assert.That(VikingVengeanceMode.SendOrder(Account(Viking, true, "", "Asli", "llsa"), null), Is.EqualTo(new[] { "Asli", "llsa" }));
                Assert.That(VikingVengeanceMode.SendOrder(Account(Viking, true, "Asli", "Asli", "llsa"), "llsa"), Is.EqualTo(new[] { "llsa" }));
            });
        }

        [Test]
        public void ResolveStart_SavedWhileDueElseNow()
        {
            var settings = Account(Viking).VikingVengeanceSettings;
            var now = Viking.AddMinutes(-5);
            Assert.Multiple(() =>
            {
                Assert.That(VikingVengeanceMode.ResolveStart(settings, null, now), Is.EqualTo(Viking));
                Assert.That(VikingVengeanceMode.ResolveStart(settings, null, Viking.AddHours(-3)), Is.EqualTo(Viking.AddHours(-3)), "by hand before the window: a test starting now");
            });
        }

        [Test]
        public void End_ForgetsTheEventOnceOver()
        {
            var settings = Account(Viking).VikingVengeanceSettings;
            VikingVengeanceMode.End(settings, Viking.AddMinutes(10));
            Assert.That(settings.NextVikingUtc, Is.EqualTo(Viking), "stopped by hand early: still known");
            VikingVengeanceMode.End(settings, Viking.AddMinutes(35));
            Assert.That(settings.NextVikingUtc, Is.Null);
        }

        [Test]
        public void TaskList_VikingRowTakesTheHeaderDate()
        {
            // the row's exact wording is not seen yet (first listed on 2026-11-19): any title with "Viking" counts
            var items = new List<BearHuntSchedule.TaskListItem>
            {
                new(true, "Today"),
                new(false, "Bear Hunt - Trap 2", "UTC 13:00"),
                new(true, "2026/11/19"),
                new(false, "Viking Vengeance", "UTC 20:00"),
            };
            var now = new DateTime(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc);
            Assert.Multiple(() =>
            {
                Assert.That(BearHuntSchedule.NextViking(items, now, TimeSpan.FromMinutes(35)), Is.EqualTo(Viking));
                Assert.That(BearHuntSchedule.NextBear(items, 2, now, TimeSpan.FromMinutes(30)), Is.EqualTo(new DateTime(2026, 11, 18, 13, 0, 0, DateTimeKind.Utc)));
            });
        }
    }
}
