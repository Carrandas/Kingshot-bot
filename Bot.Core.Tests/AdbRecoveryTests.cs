using Bot.Core.LDPlayer;
using NUnit.Framework;

namespace Bot.Core.Tests
{
    /// <summary>
    /// ADB recovery: which processes count as on the adb port, and LDPlayer's ADB debugging setting.
    /// </summary>
    [TestFixture]
    public class AdbRecoveryTests
    {
        private const string Netstat = """

            Active Connections

              Proto  Local Address          Foreign Address        State           PID
              TCP    0.0.0.0:55541          0.0.0.0:0              LISTENING       4120
              TCP    127.0.0.1:5037         127.0.0.1:55548        ESTABLISHED     900
              TCP    127.0.0.1:5555         0.0.0.0:0              LISTENING       7300
              TCP    127.0.0.1:55548        127.0.0.1:5555         ESTABLISHED     900
              TCP    [::]:5555              [::]:0                 LISTENING       7300
            """;

        [Test]
        public void ListeningPids_OnlyTheExactPort()
        {
            // 55541 (Overwolf on 2026-11-02) and connections to 5555 aren't listeners on it
            Assert.That(AdbRecoveryService.ListeningPids(Netstat, 5555), Is.EqualTo(new[] { 7300, 7300 }));
            Assert.That(AdbRecoveryService.ListeningPids(Netstat, 5554), Is.Empty);
        }

        [TestCase("dnplayer", true)]
        [TestCase("Ld9BoxHeadless", true)]
        [TestCase("VBoxNetNAT", true)]
        [TestCase("adb", true)]
        [TestCase("Overwolf", false)]
        public void IsEmulatorProcess(string name, bool expected)
        {
            Assert.That(AdbRecoveryService.IsEmulatorProcess(name), Is.EqualTo(expected));
        }

        [Test]
        public void AdbDebug_MissingKeyIsOff_AndIsAddedKeepingTheRest()
        {
            const string config = """{ "advancedSettings.cpuCount": 2, "statusSettings.isNewPlayer": true }""";

            Assert.That(AdbRecoveryService.AdbDebugEnabled(config), Is.False);
            var fixedConfig = AdbRecoveryService.WithAdbDebugEnabled(config);
            Assert.That(AdbRecoveryService.AdbDebugEnabled(fixedConfig), Is.True);
            Assert.That(fixedConfig, Does.Contain("\"advancedSettings.cpuCount\": 2"));
        }

        [TestCase("""{ "basicSettings.adbDebug": 1 }""", true)]
        [TestCase("""{ "basicSettings.adbDebug": 0 }""", false)]
        public void AdbDebug_ReadsTheSetting(string config, bool expected)
        {
            Assert.That(AdbRecoveryService.AdbDebugEnabled(config), Is.EqualTo(expected));
        }
    }
}
