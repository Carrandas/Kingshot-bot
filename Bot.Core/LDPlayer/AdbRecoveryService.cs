using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Bot.Core.Logging;
using Bot.Core.Utils;

namespace Bot.Core.LDPlayer
{
    /// <summary>
    /// Service to automatically detect and fix common ADB connection issues
    /// </summary>
    public class AdbRecoveryService
    {
        private readonly LogService _logger;
        private readonly string _adbPath;
        private readonly string _ldConsolePath;
        
        public AdbRecoveryService(LogService logger)
        {
            _logger = logger;
            _adbPath = LDPlayerHelper.GetADBPath();
            _ldConsolePath = LDPlayerHelper.GetLDPlayerConsolePath();
        }
        
        /// <summary>
        /// Attempts multiple recovery strategies to fix ADB connection issues
        /// </summary>
        public async Task<bool> AttemptFullRecoveryAsync(int instanceNumber, CancellationToken cancellationToken)
        {
            _logger.LogInfo($"[ADBRecovery] Starting comprehensive recovery for instance {instanceNumber}", category: LogCategories.ADB);

            var strategies = new (Func<Task<bool>> Attempt, string SuccessMessage)[]
            {
                // Strategy 1: Kill all adb processes and restart
                (() => RestartADBServerAsync(cancellationToken), "ADB server restarted successfully"),
                // Strategy 2: Switch LDPlayer's ADB debugging back on (only when it was off)
                (() => EnableADBInSettingsAsync(instanceNumber, cancellationToken), "ADB debugging switched on in the LDPlayer settings"),
                // Strategy 3: Restart the LDPlayer instance
                (() => RestartInstanceAsync(instanceNumber, cancellationToken), $"Instance {instanceNumber} restarted"),
                // Strategy 4: Report another program on the adb port (never killed: it could be anything of the user's)
                (() => ReportPortConflictsAsync(instanceNumber, cancellationToken), "Port conflict reported"),
                // Strategy 5: Reset ADB keys
                (() => ResetADBKeysAsync(cancellationToken), "ADB keys reset"),
            };

            foreach (var (attempt, successMessage) in strategies)
            {
                if (await attempt())
                {
                    _logger.LogInfo($"[ADBRecovery] {successMessage}", category: LogCategories.ADB);
                    if (await TestConnectionAsync(instanceNumber, cancellationToken))
                        return true;
                }
            }

            _logger.LogError($"[ADBRecovery] All recovery strategies failed for instance {instanceNumber}", category: LogCategories.ADB);
            return false;
        }

        private async Task<bool> RestartADBServerAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Kill all adb.exe processes
                foreach (var adbProcess in Process.GetProcessesByName("adb"))
                {
                    try
                    {
                        adbProcess.Kill();
                        await adbProcess.WaitForExitAsync(cancellationToken);
                    }
                    catch { /* already exited or access denied: nothing to clean up */ }
                }
                
                await Task.Delay(1000, cancellationToken);
                
                // Start ADB server
                var startInfo = new ProcessStartInfo
                {
                    FileName = _adbPath,
                    Arguments = "start-server",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    await process.WaitForExitAsync(cancellationToken);
                    return process.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ADBRecovery] Failed to restart ADB server: {ex.Message}", category: LogCategories.ADB);
            }
            
            return false;
        }
        
        private async Task<bool> RestartInstanceAsync(int instanceNumber, CancellationToken cancellationToken)
        {
            try
            {
                // Quit the instance
                var quitInfo = new ProcessStartInfo
                {
                    FileName = _ldConsolePath,
                    Arguments = $"quit --index {instanceNumber}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using (var quitProcess = Process.Start(quitInfo))
                {
                    if (quitProcess != null)
                    {
                        await quitProcess.WaitForExitAsync(cancellationToken);
                    }
                }
                
                await Task.Delay(3000, cancellationToken);
                
                // Launch the instance again
                var launchInfo = new ProcessStartInfo
                {
                    FileName = _ldConsolePath,
                    Arguments = $"launch --index {instanceNumber}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using (var launchProcess = Process.Start(launchInfo))
                {
                    if (launchProcess != null)
                    {
                        await launchProcess.WaitForExitAsync(cancellationToken);
                    }
                }
                
                // Wait for boot
                await Task.Delay(15000, cancellationToken);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ADBRecovery] Failed to restart instance: {ex.Message}", category: LogCategories.ADB);
            }
            
            return false;
        }
        
        /// <summary>
        /// Logs a program other than the emulator listening on the instance's adb port (5555 for instance 0).
        /// It used to kill every process on a netstat line containing ":5554", which matched any port starting
        /// with 5554 and took down Overwolf and dnplayer itself. Always false: nothing was changed to retest.
        /// </summary>
        private async Task<bool> ReportPortConflictsAsync(int instanceNumber, CancellationToken cancellationToken)
        {
            try
            {
                var port = 5555 + (instanceNumber * 2);
                var netstatInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "netstat.exe"),
                    Arguments = "-ano -p TCP",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(netstatInfo);
                if (process == null) return false;
                var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);

                foreach (var pid in ListeningPids(output, port).Distinct())
                {
                    string name = ProcessName(pid);
                    if (!IsEmulatorProcess(name))
                        _logger.LogWarning($"[ADBRecovery] {name} (PID {pid}) listens on the adb port {port}; close it if adb keeps failing", category: LogCategories.ADB);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ADBRecovery] Failed to check port conflicts: {ex.Message}", category: LogCategories.ADB);
            }

            return false;
        }

        /// <summary>PIDs of the netstat -ano lines "TCP 127.0.0.1:5555 0.0.0.0:0 LISTENING 1234" for exactly this local port.</summary>
        internal static IEnumerable<int> ListeningPids(string netstatOutput, int port)
        {
            foreach (var line in netstatOutput.Split('\n'))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 5 && parts[3] == "LISTENING"
                    && parts[1].EndsWith($":{port}", StringComparison.Ordinal)
                    && int.TryParse(parts[4], out int pid))
                    yield return pid;
            }
        }

        internal static bool IsEmulatorProcess(string name) =>
            name.StartsWith("Ld", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("dnplayer", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("VBox", StringComparison.OrdinalIgnoreCase)
            || name.Equals("adb", StringComparison.OrdinalIgnoreCase);

        private static string ProcessName(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return process.ProcessName;
            }
            catch (ArgumentException)
            {
                return "an exited process";
            }
        }

        private async Task<bool> ResetADBKeysAsync(CancellationToken cancellationToken)
        {
            try
            {
                var adbKeyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".android");
                if (Directory.Exists(adbKeyPath))
                {
                    var keyFiles = new[] { "adbkey", "adbkey.pub" };
                    foreach (var keyFile in keyFiles)
                    {
                        var fullPath = Path.Combine(adbKeyPath, keyFile);
                        if (File.Exists(fullPath))
                        {
                            File.Delete(fullPath);
                            _logger.LogInfo($"[ADBRecovery] Deleted {keyFile}", category: LogCategories.ADB);
                        }
                    }
                }
                
                // Restart ADB to regenerate keys
                return await RestartADBServerAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ADBRecovery] Failed to reset ADB keys: {ex.Message}", category: LogCategories.ADB);
            }
            
            return false;
        }
        
        /// <summary>
        /// LDPlayer's Settings > Other settings > ADB debugging lives in vms/config/leidian{N}.config as
        /// "basicSettings.adbDebug" (1 = open local connection). Without it the emulator opens no adb port at all
        /// (seen 2026-11-02, when the instance config had been reset). ldconsole has no switch for it, so the
        /// instance is stopped, the key written, and the instance started again. False when it was already on.
        /// </summary>
        private async Task<bool> EnableADBInSettingsAsync(int instanceNumber, CancellationToken cancellationToken)
        {
            try
            {
                var configPath = Path.Combine(Path.GetDirectoryName(_ldConsolePath) ?? "", "vms", "config", $"leidian{instanceNumber}.config");
                if (!File.Exists(configPath) || AdbDebugEnabled(await File.ReadAllTextAsync(configPath, cancellationToken)))
                    return false;

                _logger.LogWarning($"[ADBRecovery] ADB debugging is off in {configPath}; switching it on", category: LogCategories.ADB);
                await RunLdConsoleAsync($"quit --index {instanceNumber}", cancellationToken);
                await Task.Delay(5000, cancellationToken);   // LDPlayer saves the config as it closes
                var config = await File.ReadAllTextAsync(configPath, cancellationToken);
                await File.WriteAllTextAsync(configPath, WithAdbDebugEnabled(config), cancellationToken);
                await RunLdConsoleAsync($"launch --index {instanceNumber}", cancellationToken);
                await Task.Delay(15000, cancellationToken);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ADBRecovery] Failed to enable ADB in settings: {ex.Message}", category: LogCategories.ADB);
            }

            return false;
        }

        internal static bool AdbDebugEnabled(string configJson) =>
            JsonNode.Parse(configJson)?[AdbDebugKey]?.GetValue<int>() is int value && value != 0;

        internal static string WithAdbDebugEnabled(string configJson)
        {
            var config = JsonNode.Parse(configJson)?.AsObject() ?? new JsonObject();
            config[AdbDebugKey] = 1;
            return config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }

        private const string AdbDebugKey = "basicSettings.adbDebug";

        private async Task RunLdConsoleAsync(string arguments, CancellationToken cancellationToken)
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = _ldConsolePath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process != null) await process.WaitForExitAsync(cancellationToken);
        }
        
        private async Task<bool> TestConnectionAsync(int instanceNumber, CancellationToken cancellationToken)
        {
            try
            {
                var deviceSerial = $"emulator-{5554 + (instanceNumber * 2)}";
                
                // Test ADB connection
                var testInfo = new ProcessStartInfo
                {
                    FileName = _adbPath,
                    Arguments = $"-s {deviceSerial} shell echo test",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(testInfo);
                if (process != null)
                {
                    var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                    var error = await process.StandardError.ReadToEndAsync(cancellationToken);
                    await process.WaitForExitAsync(cancellationToken);
                    
                    return process.ExitCode == 0 && output.Contains("test") && !error.Contains("error");
                }
            }
            catch { /* adb not reachable: the test failed */ }
            
            return false;
        }
    }
}