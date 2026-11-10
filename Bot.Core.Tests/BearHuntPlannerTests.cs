using Bot.Core.Models;
using Bot.Core.Services;
using NUnit.Framework;

namespace Bot.Core.Tests
{
    /// <summary>The bear hunt's timing and character turns (13:00 UTC bear, Asli first, then llsa)</summary>
    [TestFixture]
    public class BearHuntPlannerTests
    {
        private static readonly DateTime Start = new(2026, 11, 8, 13, 0, 0, DateTimeKind.Utc);
        private static readonly string[] Order = { "Asli", "llsa" };
        private BearHuntSettings _settings = null!;
        private Dictionary<string, BearHuntStatus.Entry> _state = null!;

        [SetUp]
        public void SetUp()
        {
            _settings = new BearHuntSettings();
            _state = new Dictionary<string, BearHuntStatus.Entry>();
            _noRalliesUntil = null;
        }

        private BearHuntStatus.Entry? State(string name) => _state.TryGetValue(name, out var e) ? e : null;

        private void Set(string name, bool prepared = true, DateTime? ownBack = null, DateTime? fullUntil = null) =>
            _state[name] = BearHuntStatus.Entry.New(name) with { Prepared = prepared, OwnBackUtc = ownBack, FullUntilUtc = fullUntil };

        private DateTime? _noRalliesUntil;

        private BearHuntPlanner.Pick Pick(DateTime now, string? current) =>
            BearHuntPlanner.PickCharacter(now, Start, _settings, Order, State, current, _noRalliesUntil);

        private static DateTime At(int minutes, int seconds = 0) => Start.AddMinutes(minutes).AddSeconds(seconds);

        [Test]
        public void Stages_FollowTheClock()
        {
            Assert.Multiple(() =>
            {
                Assert.That(BearHuntPlanner.StageAt(At(-11), Start, _settings), Is.EqualTo(BearHuntStage.Before));
                Assert.That(BearHuntPlanner.StageAt(At(-10), Start, _settings), Is.EqualTo(BearHuntStage.Prep));
                Assert.That(BearHuntPlanner.StageAt(At(0), Start, _settings), Is.EqualTo(BearHuntStage.Running));
                Assert.That(BearHuntPlanner.StageAt(At(29, 59), Start, _settings), Is.EqualTo(BearHuntStage.Running));
                Assert.That(BearHuntPlanner.StageAt(At(30), Start, _settings), Is.EqualTo(BearHuntStage.Over));
            });
        }

        [Test]
        public void OwnRally_NotAfter24m30s()
        {
            Assert.Multiple(() =>
            {
                Assert.That(BearHuntPlanner.CanSendOwnAt(At(-1), Start, _settings), Is.False, "the bear is not up yet");
                Assert.That(BearHuntPlanner.CanSendOwnAt(At(24, 30), Start, _settings), Is.True);
                Assert.That(BearHuntPlanner.CanSendOwnAt(At(24, 31), Start, _settings), Is.False, "it would arrive after the bear");
            });
        }

        [Test]
        public void ShouldSendOwn_OnlyWhenTheTroopsAreHome()
        {
            Set("Asli", ownBack: At(5, 30));
            Assert.Multiple(() =>
            {
                Assert.That(BearHuntPlanner.ShouldSendOwn(At(3), Start, _settings, State("Asli")), Is.False);
                Assert.That(BearHuntPlanner.ShouldSendOwn(At(5, 30), Start, _settings, State("Asli")), Is.True);
                Assert.That(BearHuntPlanner.ShouldSendOwn(At(0), Start, _settings, null), Is.True, "nothing sent yet");
            });
        }

        [Test]
        public void Prep_EveryCharacterOnce_ThenWaitOnTheFirst()
        {
            Assert.That(Pick(At(-10), null), Is.EqualTo(new BearHuntPlanner.Pick("Asli", TimeSpan.Zero)));
            Set("Asli");
            Assert.That(Pick(At(-9), "Asli"), Is.EqualTo(new BearHuntPlanner.Pick("llsa", TimeSpan.Zero)));
            Set("llsa");
            Assert.That(Pick(At(-8), "llsa"), Is.EqualTo(new BearHuntPlanner.Pick("Asli", TimeSpan.Zero)), "switch to the first character before the bear");
            Assert.That(Pick(At(-8), "Asli"), Is.EqualTo(new BearHuntPlanner.Pick("Asli", TimeSpan.FromMinutes(8))), "then wait there");
        }

        /// <summary>
        /// Own rallies that keep failing (bear icon not calibrated, or troops not home yet) must not make the bot hop
        /// between characters on every pass: count the switches in 10 minutes of 40 s passes with nothing to join.
        /// </summary>
        private int SwitchesWhileOwnRalliesFail(Func<BearHuntStatus.Entry, TimeSpan> retry)
        {
            Set("Asli");
            Set("llsa");
            var now = At(0);
            string current = "Asli";
            int switches = 0;
            while (now < At(10))
            {
                var pick = Pick(now, current);
                if (pick.Wait > TimeSpan.Zero) { now += pick.Wait; continue; }
                if (pick.Character != current) switches++;
                current = pick.Character!;
                var e = State(current)!;
                if (BearHuntPlanner.ShouldSendOwn(now, Start, _settings, e)) e = e with { OwnBackUtc = now + retry(e), OwnFailures = e.OwnFailures + 1 };
                now += TimeSpan.FromSeconds(40);
                _state[current] = e;
                _noRalliesUntil = now + TimeSpan.FromSeconds(30);   // the War list had nothing to join
            }
            return switches;
        }

        [Test]
        public void FailingOwnRallies_DoNotHopBetweenCharacters()
        {
            int oldRetry = SwitchesWhileOwnRalliesFail(_ => TimeSpan.FromSeconds(20));
            SetUp();
            int realFailure = SwitchesWhileOwnRalliesFail(e => BearHuntPlanner.OwnRetryAfter(e.OwnFailures, _settings));
            SetUp();
            int uncalibrated = SwitchesWhileOwnRalliesFail(_ => _settings.RallyRound);
            TestContext.Out.WriteLine($"switches in 10 min: 20 s retry {oldRetry}, real failure {realFailure}, uncalibrated {uncalibrated}");
            // each retry on llsa is now two switches (there and back to the main character)
            Assert.Multiple(() =>
            {
                Assert.That(realFailure, Is.LessThan(oldRetry), $"a 20 s retry hops on every pass ({oldRetry} switches)");
                Assert.That(realFailure, Is.LessThanOrEqualTo(6));
                Assert.That(uncalibrated, Is.LessThanOrEqualTo(4));
            });
        }

        /// <summary>The user's order (2026-11-10): Asli rally + joins, llsa rally + joins, back to Asli, who joins until her next rally</summary>
        [Test]
        public void Start_FirstCharacterSends_ThenTheSecond_ThenBackToTheFirst()
        {
            Set("Asli");
            Set("llsa");
            Assert.That(Pick(At(0), "Asli").Character, Is.EqualTo("Asli"), "Asli sends first");

            Set("Asli", ownBack: At(5, 30));
            Assert.That(Pick(At(0, 40), "Asli"), Is.EqualTo(new BearHuntPlanner.Pick("llsa", TimeSpan.Zero)), "llsa's own rally is due");

            // llsa's pass sent her rally and joined: back to Asli, even with llsa's marches all out
            Set("llsa", ownBack: At(6, 30), fullUntil: At(2, 5));
            Assert.Multiple(() =>
            {
                Assert.That(Pick(At(1, 45), "llsa"), Is.EqualTo(new BearHuntPlanner.Pick("Asli", TimeSpan.Zero)), "back to the main character");
                Assert.That(Pick(At(2, 30), "Asli"), Is.EqualTo(new BearHuntPlanner.Pick("Asli", TimeSpan.Zero)), "Asli keeps joining");
                Assert.That(Pick(At(5, 30), "Asli").Character, Is.EqualTo("Asli"), "Asli's own rally again");
            });
            Set("Asli", ownBack: At(11));
            Assert.That(Pick(At(5, 31), "Asli"), Is.EqualTo(new BearHuntPlanner.Pick("llsa", TimeSpan.Zero)), "then llsa's, within the switch time");
        }

        [Test]
        public void SwitchesEarly_ForAnOwnRallyDueOnTheOtherCharacter()
        {
            Set("Asli", ownBack: At(5, 30));
            Set("llsa", ownBack: At(6, 30));
            Assert.Multiple(() =>
            {
                Assert.That(Pick(At(4), "Asli"), Is.EqualTo(new BearHuntPlanner.Pick("Asli", TimeSpan.Zero)), "nothing due: keep joining");
                Assert.That(Pick(At(4), "llsa"), Is.EqualTo(new BearHuntPlanner.Pick("Asli", TimeSpan.Zero)), "nothing due on llsa: back to Asli");
                Assert.That(Pick(At(4, 31), "Asli"), Is.EqualTo(new BearHuntPlanner.Pick("Asli", TimeSpan.FromSeconds(59))), "Asli due: wait for her troops");
                Assert.That(Pick(At(5), "Asli"), Is.EqualTo(new BearHuntPlanner.Pick("Asli", TimeSpan.FromSeconds(30))), "on Asli: wait for her troops");
            });
        }

        [Test]
        public void AfterTheLastSend_OnlyJoins_AndStopsWhenOver()
        {
            Set("Asli", ownBack: At(25));
            Set("llsa", ownBack: At(26));
            Assert.Multiple(() =>
            {
                Assert.That(Pick(At(25), "Asli"), Is.EqualTo(new BearHuntPlanner.Pick("Asli", TimeSpan.Zero)), "no own rally left: join on");
                Assert.That(Pick(At(30), "Asli").Character, Is.Null);
            });
        }

        [Test]
        public void AllMarchesOut_WaitsOnTheCurrentCharacter_UntilAnOwnRallyIsDue()
        {
            Set("Asli", ownBack: At(10), fullUntil: At(3));
            Set("llsa", ownBack: At(3, 30));
            Assert.Multiple(() =>
            {
                Assert.That(Pick(At(2), "Asli"), Is.EqualTo(new BearHuntPlanner.Pick("Asli", TimeSpan.FromSeconds(30))), "until llsa's switch time");
                Assert.That(Pick(At(2, 30), "Asli"), Is.EqualTo(new BearHuntPlanner.Pick("llsa", TimeSpan.Zero)));
            });
        }

        [Test]
        public void NoRalliesToJoin_WaitsOnTheMainCharacter_UnlessAnOwnRallyIsDue()
        {
            Set("Asli", ownBack: At(6, 30));
            Set("llsa", ownBack: At(5, 30));
            _noRalliesUntil = At(3, 30);
            Assert.Multiple(() =>
            {
                Assert.That(Pick(At(3), "Asli"), Is.EqualTo(new BearHuntPlanner.Pick("Asli", TimeSpan.FromSeconds(30))));
                Assert.That(Pick(At(3), "llsa"), Is.EqualTo(new BearHuntPlanner.Pick("Asli", TimeSpan.Zero)), "back to the main character to wait");
                _noRalliesUntil = At(5);
                Assert.That(Pick(At(4), "Asli"), Is.EqualTo(new BearHuntPlanner.Pick("Asli", TimeSpan.FromSeconds(30))), "until llsa's switch time");
                Assert.That(Pick(At(4, 30), "Asli"), Is.EqualTo(new BearHuntPlanner.Pick("llsa", TimeSpan.Zero)), "llsa's own rally is due");
            });
        }

        [Test]
        public void JoinsAllowed_CappedByFreeQueuesAndMax()
        {
            Assert.Multiple(() =>
            {
                Assert.That(BearHuntPlanner.JoinsAllowed(1, 6, 6, keepOneForOwn: false), Is.EqualTo(5));
                Assert.That(BearHuntPlanner.JoinsAllowed(0, 8, 6, keepOneForOwn: false), Is.EqualTo(6));
                Assert.That(BearHuntPlanner.JoinsAllowed(0, 6, 6, keepOneForOwn: true), Is.EqualTo(5));
                Assert.That(BearHuntPlanner.JoinsAllowed(6, 6, 6, keepOneForOwn: false), Is.EqualTo(0));
                Assert.That(BearHuntPlanner.JoinsAllowed(0, 0, 6, keepOneForOwn: false), Is.EqualTo(6), "tracker not read: try");
            });
        }

        [Test]
        public void Order_PutsTheFirstCharacterFirst()
        {
            Assert.Multiple(() =>
            {
                Assert.That(BearHuntPlanner.Order(new[] { "llsa", "Asli" }, "Asi"), Is.EqualTo(new[] { "Asli", "llsa" }), "matched like the roster");
                Assert.That(BearHuntPlanner.Order(new[] { "llsa", "Asli" }, ""), Is.EqualTo(new[] { "llsa", "Asli" }));
            });
        }

        [Test]
        public void ActiveBear_StartsInPrep_AndRollsAPastBearForward()
        {
            _settings.NextBearUtc = Start;
            Assert.Multiple(() =>
            {
                Assert.That(BearHuntRunner.ActiveBear(_settings, At(-11)), Is.Null);
                Assert.That(BearHuntRunner.ActiveBear(_settings, At(-10)), Is.EqualTo(Start));
                Assert.That(BearHuntRunner.ActiveBear(_settings, At(20)), Is.EqualTo(Start), "late: still joins");
                Assert.That(BearHuntRunner.ActiveBear(_settings, At(25)), Is.Null, "past the last own rally");
            });
            BearHuntRunner.ActiveBear(_settings, At(31));
            Assert.That(_settings.NextBearUtc, Is.EqualTo(Start.AddDays(2)));
        }
    
        // OCR of War rows' leaders garbles the tag's brackets: "KFCIMAS SCHUIL24", "[KFC|Darth Vader" (2026-11-08)
        [TestCase("[KFC]llsa", "[KFC]llsa", true)]
        [TestCase("KFCIllsa", "[KFC]llsa", true)]
        [TestCase("[KFC|Ilsa", "[KFC]llsa", true)]
        [TestCase("KFCIMAS SCHUIL24", "[KFC]llsa", false)]
        [TestCase("[KFC|Darth Vader", "[KFC]Asli", false)]
        [TestCase("[KFC]Asli", "[KFC]llsa", false)]
        [TestCase("", "[KFC]llsa", false)]
        public void IsOwnRally_ToleratesGarbledTags(string read, string character, bool own) =>
            Assert.That(Bot.Core.Tasks.Modules.BearHuntTask.IsOwnRally(read, character), Is.EqualTo(own));
    }
}
