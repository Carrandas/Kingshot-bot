using Bot.Core.Models;
using Bot.Core.Tasks;
using Bot.Core.Tasks.Modules;
using NUnit.Framework;

namespace Bot.Core.Tests
{
    /// <summary>
    /// Arena: reading the opponents' power and the optional "run after" UTC time.
    /// </summary>
    [TestFixture]
    public class ArenaTests
    {
        [TestCase("21.4M", 21_400_000L)]
        [TestCase("7.2M", 7_200_000L)]
        [TestCase("166,500", 166_500L)]
        [TestCase("60000", 60_000L)]       // the cut-off 5th row loses its comma
        [TestCase("850K", 850_000L)]
        [TestCase("1.2B", 1_200_000_000L)]
        [TestCase("12,9M", 12_900_000L)]
        public void ParsePower_ReadsTheShownValue(string text, long expected)
        {
            Assert.That(ArenaTask.ParsePower(text), Is.EqualTo(expected));
        }

        // glyphs left to right, true = a dot or comma in the image
        [TestCase("214M", new[] { false, false, true, false, false }, "21.4M")]
        [TestCase("21.4M", new[] { false, false, true, false, false }, "21.4M")]
        [TestCase("166500", new[] { false, false, false, true, false, false, false }, "166,500")]
        [TestCase("27M", new[] { false, false, true, false, false }, "27M")]   // a glyph lost: left as read
        public void PlaceSeparators_FromTheImage(string read, bool[] separators, string expected)
        {
            Assert.That(ArenaTask.PlaceSeparators(read, separators), Is.EqualTo(expected));
        }

        [TestCase("")]
        [TestCase("M")]
        [TestCase("21.4X")]
        [TestCase("29.")]      // 29.xM with the M cut off: picked as the weakest and lost
        [TestCase("29")]
        public void ParsePower_RejectsOtherText(string text)
        {
            Assert.That(ArenaTask.ParsePower(text), Is.Null);
        }

        [TestCase(null, 4, 4)]
        [TestCase(3, 2, 2)]
        [TestCase(3, 12, 2)]   // "Daily challenges: 2" read as 12
        [TestCase(1, 10, 0)]
        [TestCase(3, null, null)]
        public void ChallengesLeft_OnlyGoesDown(int? before, int? read, int? expected)
        {
            Assert.That(ArenaTask.AfterBattle(before, read), Is.EqualTo(expected));
        }

        [Test]
        public void RunAfter_IsOffByDefault()
        {
            var settings = new ArenaSettings();

            Assert.That(settings.RunAfterEnabled, Is.False);
            Assert.That(settings.RunAfterUtc, Is.EqualTo("21:30"));
            Assert.That(settings.RunAfterTime(), Is.Null);
        }

        [TestCase("22:00", 22, 0)]
        [TestCase("7:30", 7, 30)]
        public void RunAfter_ParsesTheTimeWhenOn(string text, int hours, int minutes)
        {
            var settings = new ArenaSettings { RunAfterEnabled = true, RunAfterUtc = text };

            Assert.That(settings.RunAfterTime(), Is.EqualTo(new TimeSpan(hours, minutes, 0)));
        }

        [TestCase("24:00")]
        [TestCase("late")]
        public void RunAfter_IgnoresAnInvalidTime(string text)
        {
            var settings = new ArenaSettings { RunAfterEnabled = true, RunAfterUtc = text };

            Assert.That(settings.RunAfterTime(), Is.Null);
        }

        [Test]
        public void NextRun_WithoutRunAfter_IsNowUntilDoneThenTheReset()
        {
            string key = "test-" + Guid.NewGuid().ToString("N");
            var task = new ArenaTask();
            var account = new AccountSettings();

            Assert.That(task.GetNextRunUtc(key, account), Is.Null);
            DailyGate.MarkDone(TaskType.Arena, key);
            Assert.That(task.GetNextRunUtc(key, account), Is.EqualTo(Schedule.NextUtcMidnight));
        }

        [Test]
        public void NextRun_WithRunAfter_IsThatTimeTodayOrTomorrow()
        {
            string key = "test-" + Guid.NewGuid().ToString("N");
            var task = new ArenaTask();
            var later = DateTime.UtcNow.AddMinutes(30);
            if (later.Date != DateTime.UtcNow.Date) Assert.Ignore("Too close to midnight UTC");
            var account = new AccountSettings();
            account.ArenaSettings.RunAfterEnabled = true;
            account.ArenaSettings.RunAfterUtc = later.ToString("HH:mm");
            var todayStart = DateTime.UtcNow.Date + new TimeSpan(later.Hour, later.Minute, 0);

            Assert.That(task.GetNextRunUtc(key, account), Is.EqualTo(todayStart));
            DailyGate.MarkDone(TaskType.Arena, key);
            Assert.That(task.GetNextRunUtc(key, account), Is.EqualTo(todayStart.AddDays(1)));
        }
    }
}
