using Bot.Core.Services;
using NUnit.Framework;

namespace Bot.Core.Tests
{
    [TestFixture]
    public class CycleScheduleStoreTests
    {
        [Test]
        public void Remaining_OnlyWhileTheCountdownIsInTheFuture()
        {
            var now = new DateTime(2026, 11, 12, 22, 0, 0, DateTimeKind.Utc);
            Assert.That(CycleScheduleStore.Remaining(now.AddMinutes(78), now), Is.EqualTo(TimeSpan.FromMinutes(78)));
            Assert.That(CycleScheduleStore.Remaining(now.AddMinutes(-1), now), Is.Null);   // ran out while the app was off: a cycle is due now
            Assert.That(CycleScheduleStore.Remaining(now, now), Is.Null);
            Assert.That(CycleScheduleStore.Remaining(null, now), Is.Null);
        }

        [Test]
        public void SaveAndLoad_KeepTheTimeAndClearRemovesIt()
        {
            string old = CycleScheduleStore.FilePath;
            CycleScheduleStore.FilePath = Path.Combine(Path.GetTempPath(), $"cycle_next_{Guid.NewGuid():N}.txt");
            try
            {
                Assert.That(CycleScheduleStore.Load(), Is.Null);
                var next = new DateTime(2026, 11, 12, 23, 18, 30, DateTimeKind.Utc);
                CycleScheduleStore.Save(next);
                Assert.That(CycleScheduleStore.Load(), Is.EqualTo(next));
                Assert.That(CycleScheduleStore.Load()!.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
                CycleScheduleStore.Save(null);
                Assert.That(CycleScheduleStore.Load(), Is.Null);
            }
            finally
            {
                File.Delete(CycleScheduleStore.FilePath);
                CycleScheduleStore.FilePath = old;
            }
        }
    }
}
