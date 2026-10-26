using System;
using Bot.Core.Models;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace Bot.Core.Tests
{
    [TestFixture]
    public class InstanceDetailedStatusTests
    {
        [Test]
        public void FromList2Parts_ValidInput_ReturnsCorrectStatus()
        {
            // Arrange
            var parts = new[] { "0", "LDPlayer", "2032678", "1704928", "1", "7456", "3500" };

            // Act
            var status = InstanceDetailedStatus.FromList2Parts(parts);

            // Assert
            ClassicAssert.IsNotNull(status);
            ClassicAssert.AreEqual(0, status.Index);
            ClassicAssert.AreEqual("LDPlayer", status.Title);
            ClassicAssert.AreEqual(2032678, status.TopWindowHandle);
            ClassicAssert.AreEqual(1704928, status.BindWindowHandle);
            ClassicAssert.IsTrue(status.AndroidStarted);
            ClassicAssert.AreEqual(7456, status.ProcessId);
            ClassicAssert.AreEqual(3500, status.VBoxProcessId);
            ClassicAssert.IsTrue(status.IsRunning);
            ClassicAssert.IsTrue(status.IsFullyBooted);
        }

        [Test]
        public void FromList2Parts_AndroidNotStarted_ReturnsCorrectStatus()
        {
            // Arrange
            var parts = new[] { "1", "LDPlayer-1", "852422", "590830", "0", "3772", "3180" };

            // Act
            var status = InstanceDetailedStatus.FromList2Parts(parts);

            // Assert
            ClassicAssert.IsNotNull(status);
            ClassicAssert.AreEqual(1, status.Index);
            ClassicAssert.IsTrue(status.IsRunning); // Process is running
            ClassicAssert.IsFalse(status.AndroidStarted); // But Android not started
            ClassicAssert.IsFalse(status.IsFullyBooted); // Therefore not fully booted
            ClassicAssert.AreEqual("Emulator Running, Android Starting", status.StatusDescription);
        }

        [Test]
        public void FromList2Parts_NotRunning_ReturnsCorrectStatus()
        {
            // Arrange
            var parts = new[] { "2", "LDPlayer-2", "0", "0", "0", "0", "0" };

            // Act
            var status = InstanceDetailedStatus.FromList2Parts(parts);

            // Assert
            ClassicAssert.IsNotNull(status);
            ClassicAssert.AreEqual(2, status.Index);
            ClassicAssert.IsFalse(status.IsRunning);
            ClassicAssert.IsFalse(status.AndroidStarted);
            ClassicAssert.IsFalse(status.IsFullyBooted);
            ClassicAssert.IsFalse(status.HasValidHandles);
            ClassicAssert.AreEqual("Not Running", status.StatusDescription);
        }

        [Test]
        public void FromList2Parts_InsufficientParts_ReturnsNull()
        {
            // Arrange
            var parts = new[] { "0", "LDPlayer", "123" }; // Only 3 parts, need 7

            // Act
            var status = InstanceDetailedStatus.FromList2Parts(parts);

            // Assert
            ClassicAssert.IsNull(status);
        }

        [Test]
        public void FromList2Parts_InvalidData_ReturnsNull()
        {
            // Arrange
            var parts = new[] { "invalid", "LDPlayer", "not_a_number", "1704928", "1", "7456", "3500" };

            // Act
            var status = InstanceDetailedStatus.FromList2Parts(parts);

            // Assert
            ClassicAssert.IsNull(status);
        }

        [Test]
        public void ReadinessLevel_NotRunning_ReturnsNotRunning()
        {
            // Arrange
            var parts = new[] { "0", "LDPlayer", "0", "0", "0", "0", "0" };
            var status = InstanceDetailedStatus.FromList2Parts(parts);

            // Act
            var readiness = status.ReadinessLevel;

            // Assert
            ClassicAssert.AreEqual(InstanceReadiness.NotRunning, readiness);
        }

        [Test]
        public void ReadinessLevel_RunningButAndroidNotStarted_ReturnsBooting()
        {
            // Arrange
            var parts = new[] { "0", "LDPlayer", "1000", "2000", "0", "1234", "5678" };
            var status = InstanceDetailedStatus.FromList2Parts(parts);

            // Act
            var readiness = status.ReadinessLevel;

            // Assert
            ClassicAssert.AreEqual(InstanceReadiness.Booting, readiness);
        }

        [Test]
        public void ReadinessLevel_AndroidStartedButNoHandles_ReturnsStarting()
        {
            // Arrange
            var parts = new[] { "0", "LDPlayer", "0", "0", "1", "1234", "5678" };
            var status = InstanceDetailedStatus.FromList2Parts(parts);

            // Act
            var readiness = status.ReadinessLevel;

            // Assert
            ClassicAssert.AreEqual(InstanceReadiness.Starting, readiness);
        }

        [Test]
        public void ReadinessLevel_FullyBooted_ReturnsReady()
        {
            // Arrange
            var parts = new[] { "0", "LDPlayer", "1000", "2000", "1", "1234", "5678" };
            var status = InstanceDetailedStatus.FromList2Parts(parts);

            // Act
            var readiness = status.ReadinessLevel;

            // Assert
            ClassicAssert.AreEqual(InstanceReadiness.Ready, readiness);
        }

        [Test]
        public void NeedsRecovery_RunningTooLongWithoutAndroid_ReturnsTrue()
        {
            // Arrange
            var parts = new[] { "0", "LDPlayer", "1000", "2000", "0", "1234", "5678" };
            var status = InstanceDetailedStatus.FromList2Parts(parts);
            
            // Simulate old timestamp (6 minutes ago)
            status.LastUpdated = DateTime.UtcNow.AddMinutes(-6);

            // Act
            var needsRecovery = status.NeedsRecovery;

            // Assert
            ClassicAssert.IsTrue(needsRecovery);
        }

        [Test]
        public void NeedsRecovery_RecentUpdate_ReturnsFalse()
        {
            // Arrange
            var parts = new[] { "0", "LDPlayer", "1000", "2000", "0", "1234", "5678" };
            var status = InstanceDetailedStatus.FromList2Parts(parts);
            
            // Recent update (1 minute ago)
            status.LastUpdated = DateTime.UtcNow.AddMinutes(-1);

            // Act
            var needsRecovery = status.NeedsRecovery;

            // Assert
            ClassicAssert.IsFalse(needsRecovery);
        }

        [Test]
        public void ToString_ReturnsFormattedString()
        {
            // Arrange
            var parts = new[] { "0", "LDPlayer", "2032678", "1704928", "1", "7456", "3500" };
            var status = InstanceDetailedStatus.FromList2Parts(parts);

            // Act
            var stringValue = status.ToString();

            // Assert
            ClassicAssert.IsNotNull(stringValue);
            StringAssert.Contains("Instance 0", stringValue);
            StringAssert.Contains("LDPlayer", stringValue);
            StringAssert.Contains("Fully Ready", stringValue);
            StringAssert.Contains("PID: 7456", stringValue);
            StringAssert.Contains("VBox: 3500", stringValue);
        }
    }
}