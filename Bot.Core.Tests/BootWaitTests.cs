using Bot.Core.LDPlayer;
using NUnit.Framework;

namespace Bot.Core.Tests
{
    [TestFixture]
    public class BootWaitTests
    {
        private static readonly DateTime Now = new(2026, 11, 21, 14, 0, 0, DateTimeKind.Utc);

        [Test]
        public void AndroidStuck_RebootsADeadVmSoon_AndAStuckBootLater()
        {
            Assert.Multiple(() =>
            {
                // 2026-11-21: window up, vbox pid -1, "Android starting" for 12 minutes
                Assert.That(InstanceManager.AndroidStuck(Now.AddSeconds(-20), Now.AddSeconds(-20), Now), Is.True);
                Assert.That(InstanceManager.AndroidStuck(Now.AddSeconds(-5), Now.AddSeconds(-5), Now), Is.False);
                // the VM runs, Android still booting
                Assert.That(InstanceManager.AndroidStuck(Now.AddSeconds(-30), null, Now), Is.False);
                Assert.That(InstanceManager.AndroidStuck(Now.AddSeconds(-50), null, Now), Is.True);
                Assert.That(InstanceManager.AndroidStuck(null, null, Now), Is.False);
            });
        }
    }
}
