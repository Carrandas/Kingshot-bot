using Bot.Core.Logging;
using Bot.Core.Models;
using Bot.Core.Services;
using NUnit.Framework;

namespace Bot.Core.Tests
{
    /// <summary>Which bear is due, the status line and the exclusive mode runner (no emulator, no TaskManager)</summary>
    [TestFixture]
    public class BearHuntModeTests
    {
        private static readonly DateTime Bear = new(2026, 11, 8, 13, 0, 0, DateTimeKind.Utc);

        private static AccountSettings Account(bool on, DateTime? next, bool hidden = false)
        {
            var account = new AccountSettings { AccountName = "A", InstanceNumber = 0 };
            if (on) account.EnabledTasks.Add(TaskType.BearHunt);
            if (hidden) account.HiddenTasks.Add(TaskType.BearHunt);
            account.BearHuntSettings.NextBearUtc = next;
            return account;
        }

        [TearDown]
        public void TearDown()
        {
            BearHuntStatus.Stop();
            CastleBattleStatus.Stop();
        }

        [Test]
        public void FindDue_StartsOnlyInsideThePrepWindow()
        {
            var account = Account(true, Bear);
            Assert.Multiple(() =>
            {
                Assert.That(BearHuntMode.FindDue(new[] { account }, Bear.AddMinutes(-30)).Account, Is.Null);
                var due = BearHuntMode.FindDue(new[] { account }, Bear.AddMinutes(-5));
                Assert.That(due.Account, Is.SameAs(account));
                Assert.That(due.StartUtc, Is.EqualTo(Bear));
            });
        }

        [Test]
        public void FindDue_SkipsAccountsWithTheSwitchOffOrTheTaskHidden()
        {
            var off = Account(false, Bear);
            var hidden = Account(true, Bear, hidden: true);
            var on = Account(true, Bear);
            var due = BearHuntMode.FindDue(new[] { off, hidden, on }, Bear.AddMinutes(-5));
            Assert.That(due.Account, Is.SameAs(on));
        }

        [Test]
        public void FindDue_ReportsARolledForwardStart()
        {
            var account = Account(true, Bear);
            var due = BearHuntMode.FindDue(new[] { account }, Bear.AddDays(1));
            Assert.Multiple(() =>
            {
                Assert.That(due.Account, Is.Null);
                Assert.That(due.SettingsChanged, Is.True);
                Assert.That(account.BearHuntSettings.NextBearUtc, Is.EqualTo(Bear.AddDays(2)));
            });
        }

        private static string Local(DateTime utc) => utc.ToString("HH:mm");
        private static string Relative(DateTime utc) => "rel";

        [Test]
        public void Summary_IdleTextFollowsTheSwitchAndPrepMinutes()
        {
            var settings = new BearHuntSettings { PrepMinutes = 12 };
            Assert.Multiple(() =>
            {
                Assert.That(BearHuntStatus.Summary(Bear, settings, true, Local, Relative), Does.StartWith("On: the bot stops the cycle 12 min before"));
                Assert.That(BearHuntStatus.Summary(Bear, settings, false, Local, Relative), Does.StartWith("Off."));
                Assert.That(BearHuntStatus.Summary(Bear, null, true, Local, Relative), Does.Contain("cycle 10 min before"));
            });
        }

        [Test]
        public void Summary_GettingReadyThenRunning()
        {
            var settings = new BearHuntSettings { EventMinutes = 45 };
            BearHuntStatus.Start(Bear, Bear + settings.EventLength);
            Assert.Multiple(() =>
            {
                Assert.That(BearHuntStatus.Summary(Bear.AddMinutes(-5), settings, true, Local, Relative), Is.EqualTo("Bear Hunt: getting ready, bear at 13:00 (rel)"));
                Assert.That(BearHuntStatus.Summary(Bear.AddMinutes(1), settings, true, Local, Relative), Is.EqualTo("Bear Hunt running until 13:45"));
                Assert.That(BearHuntStatus.Summary(Bear.AddMinutes(1), null, true, Local, Relative), Is.EqualTo("Bear Hunt running until 13:30"));
            });
        }

        [Test]
        public void ModeStatus_AnyRunningCoversBothModes()
        {
            Assert.That(ModeStatus.AnyRunning, Is.False);
            CastleBattleStatus.Start(DateTime.UtcNow.AddHours(1));
            Assert.That(ModeStatus.AnyRunning, Is.True);
            CastleBattleStatus.Stop();
            BearHuntStatus.Start(Bear, Bear.AddMinutes(30));
            Assert.That(ModeStatus.AnyRunning, Is.True);
        }

        private sealed class FakeHost : IModeHost
        {
            public bool Running { get; set; }
            public bool OnSchedule { get; set; }
            public int Stops { get; private set; }
            public int Restarts { get; private set; }
            public bool IsBotRunning => Running;
            public bool IsOnSchedule => OnSchedule;
            public Task StopCycleAsync() { Stops++; Running = false; return Task.CompletedTask; }
            public CancellationToken FreshToken() => CancellationToken.None;
            public void SetBusy(bool busy) { }
            public void RestartSchedule() => Restarts++;
        }

        [Test]
        public async Task Runner_StopsTheCycleRunsTheModeAndRestartsTheSchedule()
        {
            var host = new FakeHost { Running = true, OnSchedule = true };
            var runner = new ExclusiveModeRunner(host, new LogService());
            var steps = new List<string>();
            bool finished = await runner.RunAsync("Test", "*", () => steps.Add("begin"), _ => { steps.Add("body"); return Task.CompletedTask; }, () => { steps.Add("end"); return Task.CompletedTask; });
            Assert.Multiple(() =>
            {
                Assert.That(finished, Is.True);
                Assert.That(steps, Is.EqualTo(new[] { "begin", "body", "end" }));
                Assert.That(host.Stops, Is.EqualTo(1));
                Assert.That(host.Restarts, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task Runner_NoRestartAfterAFailureAndRefusesWhileAModeRuns()
        {
            var host = new FakeHost { OnSchedule = true };
            var runner = new ExclusiveModeRunner(host, new LogService());
            bool ended = false;
            bool finished = await runner.RunAsync("Test", "*", () => { }, _ => throw new InvalidOperationException("boom"), () => { ended = true; return Task.CompletedTask; });
            Assert.Multiple(() =>
            {
                Assert.That(finished, Is.False);
                Assert.That(ended, Is.True);
                Assert.That(host.Restarts, Is.EqualTo(0));
            });

            BearHuntStatus.Start(Bear, Bear.AddMinutes(30));
            bool ran = false;
            Assert.That(await runner.RunAsync("Test", "*", () => ran = true, _ => Task.CompletedTask, () => Task.CompletedTask), Is.False);
            Assert.That(ran, Is.False);
        }
    }
}
