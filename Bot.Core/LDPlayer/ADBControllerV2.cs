using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
using System.Text;
using Bot.Core.ImageDetection;
using Bot.Core.Logging;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.DeviceCommands;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;
using System.Collections.Concurrent;
using OpenCvSharp;

namespace Bot.Core.LDPlayer
{
    /// <summary>
    /// Enhanced ADB controller with persistent connections and performance optimizations.
    /// Inspired by wosbot-master's ddmlib approach with C# AdvancedSharpAdbClient.
    /// </summary>
    public class ADBControllerV2 : IDisposable
    {
        private readonly DeviceData _device;
        private readonly AdbClient _adbClient;
        private readonly LogService _logger;
        private readonly int _instanceNumber;
        private bool _disposed = false;
        
        // Devices (by serial) that cannot stream raw screencap over exec:, so the file pull is used from then on.
        // Static because a new controller is created for every connection
        private static readonly ConcurrentDictionary<string, bool> _execScreencapUnavailable = new();
        private static readonly ConcurrentDictionary<string, int> _execScreencapFailures = new();
        private const int MAX_EXEC_SCREENCAP_FAILURES = 3;
        // Shorter than the caller's 5 s screenshot timeout so a stalled exec: still leaves time for the file pull
        private const int EXEC_SCREENCAP_TIMEOUT_MS = 2500;
        // screencap raw header: width, height, pixel format (+ colour space on Android 9+)
        private const int RAW_FORMAT_RGBA_8888 = 1;
        // Encoding on the PC is the cheap part, so favour speed over size
        private const int HOST_PNG_COMPRESSION = 1;
        
        // Command batching for performance
        private readonly List<string> _batchedCommands = new();
        private readonly SemaphoreSlim _commandSemaphore = new(1, 1);
        
        // Connection health tracking
        private DateTime _lastSuccessfulOperation = DateTime.UtcNow;
        private int _consecutiveFailures = 0;
        private const int MAX_CONSECUTIVE_FAILURES = 3;
        
        // Performance metrics
        private static readonly ConcurrentDictionary<string, (long TotalTime, int Count)> _performanceMetrics = new();

        public ADBControllerV2(DeviceData device, AdbClient adbClient, LogService logger, int instanceNumber)
        {
            _device = device;
            _adbClient = adbClient ?? throw new ArgumentNullException(nameof(adbClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _instanceNumber = instanceNumber;
            
            _logger.LogInfo($"[ADBv2] Controller created for instance {instanceNumber}: {device.Serial}");
        }

        /// <summary>
        /// Enhanced screenshot capture with buffer reuse (inspired by wosbot-master's approach)
        /// </summary>
        public async Task<byte[]> TakeScreenshotAsync(CancellationToken cancellationToken = default)
        {
            var startTime = DateTime.UtcNow;
            
            try
            {
                if (!IsUsable())
                {
                    throw new InvalidOperationException($"Device {_device.Serial} is not responsive");
                }

                byte[] screenshotBytes;
                if (_execScreencapUnavailable.ContainsKey(_device.Serial))
                {
                    screenshotBytes = await CaptureViaFilePullAsync(cancellationToken);
                }
                else
                {
                    screenshotBytes = await CaptureWithFallbackAsync(cancellationToken);
                }

                RecordSuccessfulOperation();
                RecordPerformanceMetric("TakeScreenshot", DateTime.UtcNow - startTime);

                _logger.LogInfo($"[ADBv2] Screenshot captured for instance {_instanceNumber} ({screenshotBytes.Length} bytes)");
                return screenshotBytes;
            }
            catch (Exception ex)
            {
                RecordFailedOperation(ex);
                _logger.LogError($"[ADBv2] Screenshot failed for instance {_instanceNumber}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Streams the raw framebuffer from screencap over the exec: service and encodes the PNG
        /// on the PC. Compressing the PNG on the emulator took about 1 s per screenshot on
        /// LDPlayer; the raw transfer plus a host encode takes about a third of that.
        /// Falls back to the file pull if that does not work.
        /// </summary>
        private async Task<byte[]> CaptureWithFallbackAsync(CancellationToken cancellationToken)
        {
            string failure;
            try
            {
                using var execTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                execTimeout.CancelAfter(EXEC_SCREENCAP_TIMEOUT_MS);

                var raw = await CaptureRawViaExecAsync(execTimeout.Token);
                var png = EncodeRawScreencap(raw);
                if (png != null)
                {
                    _execScreencapFailures.TryRemove(_device.Serial, out _);
                    return png;
                }

                // Data came back but not in a layout we can read: this will not get better on retry
                _logger.LogWarning($"[ADBv2] exec: screencap returned an unsupported raw format on instance {_instanceNumber}, using file pull from now on");
                _execScreencapUnavailable[_device.Serial] = true;
                return await CaptureViaFilePullAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                failure = $"timed out after {EXEC_SCREENCAP_TIMEOUT_MS} ms";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failure = ex.Message;
            }

            var pulled = await CaptureViaFilePullAsync(cancellationToken);

            // The file pull works, so the device itself is fine: exec: is what failed
            int failures = _execScreencapFailures.AddOrUpdate(_device.Serial, 1, (_, count) => count + 1);
            if (failures >= MAX_EXEC_SCREENCAP_FAILURES)
            {
                _logger.LogWarning($"[ADBv2] exec: screencap failed {failures} times on instance {_instanceNumber} ({failure}), using file pull from now on");
                _execScreencapUnavailable[_device.Serial] = true;
            }
            else
            {
                _logger.LogWarning($"[ADBv2] exec: screencap failed on instance {_instanceNumber} ({failure}), used file pull instead");
            }
            return pulled;
        }

        /// <summary>
        /// Turns raw screencap output into a 4-channel PNG (the same layout the device's own
        /// PNG has), or returns null if the header does not describe RGBA_8888 pixels.
        /// The decoded pixels are handed to the template matchers' cache so they are not decoded again.
        /// </summary>
        private static byte[]? EncodeRawScreencap(byte[] raw)
        {
            if (raw.Length < 12) return null;
            int width = BitConverter.ToInt32(raw, 0);
            int height = BitConverter.ToInt32(raw, 4);
            int format = BitConverter.ToInt32(raw, 8);
            if (format != RAW_FORMAT_RGBA_8888 || width <= 0 || height <= 0) return null;

            long pixelBytes = (long)width * height * 4;
            long headerSize = raw.Length - pixelBytes;
            if (headerSize != 12 && headerSize != 16) return null;

            using var rgba = Mat.FromPixelData(height, width, MatType.CV_8UC4, raw.AsSpan((int)headerSize).ToArray());
            using var bgra = new Mat();
            Cv2.CvtColor(rgba, bgra, ColorConversionCodes.RGBA2BGRA);
            var png = bgra.ImEncode(".png", new ImageEncodingParam(ImwriteFlags.PngCompression, HOST_PNG_COMPRESSION));

            var bgr = new Mat();
            Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
            DecodedScreenshotCache.Add(png, bgr);
            return png;
        }

        private async Task<byte[]> CaptureRawViaExecAsync(CancellationToken cancellationToken)
        {
            using var socket = _adbClient.CreateAdbSocket();
            await socket.SetDeviceAsync(_device, cancellationToken);
            await socket.SendAdbRequestAsync("exec:screencap", cancellationToken);
            await socket.ReadAdbResponseAsync(cancellationToken);

            // The device closes the stream once screencap has written the whole frame
            using var buffer = new MemoryStream(4 * 1024 * 1024);
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = await socket.ReadAsync(chunk.AsMemory(), cancellationToken)) > 0)
            {
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }

        private async Task<byte[]> CaptureViaFilePullAsync(CancellationToken cancellationToken)
        {
            // The file is overwritten on every capture, so it is not removed afterwards
            var tempPath = $"/sdcard/temp_screenshot_{_instanceNumber}.png";

            var receiver = new ConsoleOutputReceiver();
            await _adbClient.ExecuteRemoteCommandAsync($"screencap -p {tempPath}", _device, receiver, cancellationToken);

            using var buffer = new MemoryStream(1024 * 1024);
            using (var syncService = new SyncService(_device))
            {
                await syncService.PullAsync(tempPath, buffer, null, false, cancellationToken);
            }
            return buffer.ToArray();
        }

        /// <summary>
        /// Enhanced tap functionality with random coordinates (similar to wosbot-master's tapAtRandomPoint)
        /// </summary>
        public async Task<bool> TapAsync(int x, int y, CancellationToken cancellationToken = default)
        {
            return await TapRandomInRectAsync(x, y, x, y, cancellationToken);
        }

        /// <summary>
        /// Random tap within rectangle area (directly inspired by wosbot-master)
        /// </summary>
        public async Task<bool> TapRandomInRectAsync(int x1, int y1, int x2, int y2, CancellationToken cancellationToken = default)
        {
            var startTime = DateTime.UtcNow;
            
            try
            {
                if (!IsUsable())
                {
                    return false;
                }

                var random = new Random();
                var minX = Math.Min(x1, x2);
                var maxX = Math.Max(x1, x2);
                var minY = Math.Min(y1, y2);
                var maxY = Math.Max(y1, y2);
                
                var randomX = minX + random.Next(maxX - minX + 1);
                var randomY = minY + random.Next(maxY - minY + 1);
                
                var command = $"input tap {randomX} {randomY}";
                await ExecuteShellCommandAsync(command, cancellationToken);
                
                RecordSuccessfulOperation();
                RecordPerformanceMetric("TapRandom", DateTime.UtcNow - startTime);
                
                _logger.LogInfo($"[ADBv2] Tap sent to ({randomX},{randomY}) on instance {_instanceNumber}");
                return true;
            }
            catch (Exception ex)
            {
                RecordFailedOperation(ex);
                _logger.LogError($"[ADBv2] Tap failed for instance {_instanceNumber}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Multiple taps with delay (inspired by wosbot-master's tapCount functionality)
        /// </summary>
        public async Task<bool> TapRandomInRectMultipleAsync(int x1, int y1, int x2, int y2, int tapCount, int delayMs, CancellationToken cancellationToken = default)
        {
            var startTime = DateTime.UtcNow;
            
            try
            {
                if (!IsUsable())
                {
                    return false;
                }

                var random = new Random();
                var minX = Math.Min(x1, x2);
                var maxX = Math.Max(x1, x2);
                var minY = Math.Min(y1, y2);
                var maxY = Math.Max(y1, y2);
                
                for (int i = 1; i <= tapCount; i++)
                {
                    var randomX = minX + random.Next(maxX - minX + 1);
                    var randomY = minY + random.Next(maxY - minY + 1);
                    
                    var command = $"input tap {randomX} {randomY}";
                    await ExecuteShellCommandAsync(command, cancellationToken);
                    
                    _logger.LogInfo($"[ADBv2] Tap {i}/{tapCount} sent to ({randomX},{randomY}) on instance {_instanceNumber}");
                    
                    if (i < tapCount && delayMs > 0)
                    {
                        await Task.Delay(delayMs, cancellationToken);
                    }
                }
                
                RecordSuccessfulOperation();
                RecordPerformanceMetric("TapMultiple", DateTime.UtcNow - startTime);
                
                return true;
            }
            catch (Exception ex)
            {
                RecordFailedOperation(ex);
                _logger.LogError($"[ADBv2] Multiple tap failed for instance {_instanceNumber}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Swipe gesture (inspired by wosbot-master's swipe functionality)
        /// </summary>
        public async Task<bool> SwipeAsync(int startX, int startY, int endX, int endY, int durationMs = 500, CancellationToken cancellationToken = default)
        {
            var startTime = DateTime.UtcNow;
            
            try
            {
                if (!IsUsable())
                {
                    return false;
                }

                var command = $"input swipe {startX} {startY} {endX} {endY} {durationMs}";
                await ExecuteShellCommandAsync(command, cancellationToken);
                
                RecordSuccessfulOperation();
                RecordPerformanceMetric("Swipe", DateTime.UtcNow - startTime);
                
                _logger.LogInfo($"[ADBv2] Swipe executed from ({startX},{startY}) to ({endX},{endY}) on instance {_instanceNumber}");
                return true;
            }
            catch (Exception ex)
            {
                RecordFailedOperation(ex);
                _logger.LogError($"[ADBv2] Swipe failed for instance {_instanceNumber}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Press back button (similar to wosbot-master's pressBackButton)
        /// </summary>
        public async Task<bool> PressBackButtonAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await ExecuteShellCommandAsync("input keyevent KEYCODE_BACK", cancellationToken);
                _logger.LogInfo($"[ADBv2] Back button pressed on instance {_instanceNumber}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ADBv2] Back button failed for instance {_instanceNumber}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Check if app is installed (similar to wosbot-master's isAppInstalled)
        /// </summary>
        public async Task<bool> IsAppInstalledAsync(string packageName, CancellationToken cancellationToken = default)
        {
            try
            {
                var result = await ExecuteShellCommandAsync($"pm list packages | grep {packageName}", cancellationToken);
                return !string.IsNullOrWhiteSpace(result);
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ADBv2] Package check failed for {packageName} on instance {_instanceNumber}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Check if app is in foreground (similar to wosbot-master's isPackageRunning)
        /// </summary>
        public async Task<bool> IsAppInForegroundAsync(string packageName, CancellationToken cancellationToken = default)
        {
            try
            {
                // Try multiple dumpsys commands like wosbot-master does
                var commands = new[]
                {
                    "dumpsys window windows",
                    "dumpsys window displays", 
                    "dumpsys activity activities"
                };

                foreach (var command in commands)
                {
                    var result = await ExecuteShellCommandAsync(command, cancellationToken);
                    
                    if (command.Contains("window"))
                    {
                        if ((result.Contains("mCurrentFocus") || result.Contains("mFocusedApp")) && 
                            result.Contains($"{packageName}/"))
                        {
                            _logger.LogInfo($"[ADBv2] App {packageName} detected in foreground on instance {_instanceNumber}");
                            return true;
                        }
                    }
                    else if (command.Contains("activity"))
                    {
                        if (result.Contains("mResumedActivity") && result.Contains($"{packageName}/"))
                        {
                            _logger.LogInfo($"[ADBv2] App {packageName} detected in foreground on instance {_instanceNumber}");
                            return true;
                        }
                    }
                }
                
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ADBv2] Foreground check failed for {packageName} on instance {_instanceNumber}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Launch app (similar to wosbot-master's launchApp)
        /// </summary>
        public async Task<bool> LaunchAppAsync(string packageName, CancellationToken cancellationToken = default)
        {
            try
            {
                await ExecuteShellCommandAsync($"monkey -p {packageName} -c android.intent.category.LAUNCHER 1", cancellationToken);
                _logger.LogInfo($"[ADBv2] App {packageName} launched on instance {_instanceNumber}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ADBv2] App launch failed for {packageName} on instance {_instanceNumber}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Command batching for performance (batch multiple commands together)
        /// </summary>
        public void BatchCommand(string command)
        {
            lock (_batchedCommands)
            {
                _batchedCommands.Add(command);
            }
        }

        /// <summary>
        /// Execute all batched commands at once
        /// </summary>
        public async Task<bool> ExecuteBatchedCommandsAsync(CancellationToken cancellationToken = default)
        {
            var startTime = DateTime.UtcNow;
            List<string> commandsToExecute;
            
            lock (_batchedCommands)
            {
                commandsToExecute = new List<string>(_batchedCommands);
                _batchedCommands.Clear();
            }

            if (!commandsToExecute.Any())
            {
                return true;
            }

            try
            {
                // Execute commands as a single shell script
                var combinedCommand = string.Join(" && ", commandsToExecute);
                await ExecuteShellCommandAsync(combinedCommand, cancellationToken);
                
                RecordPerformanceMetric("BatchedCommands", DateTime.UtcNow - startTime);
                _logger.LogInfo($"[ADBv2] Executed {commandsToExecute.Count} batched commands on instance {_instanceNumber}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ADBv2] Batched commands failed for instance {_instanceNumber}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Enhanced connection health check (similar to wosbot-master's device.isOnline())
        /// </summary>
        public async Task<bool> IsConnectedAndResponsiveAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (_consecutiveFailures >= MAX_CONSECUTIVE_FAILURES)
                {
                    return false;
                }

                // Quick responsiveness test
                await ExecuteShellCommandAsync("echo ping", cancellationToken, timeoutMs: 5000);
                return true;
            }
            catch
            {
                ADBConnectionManagerV2.MarkConnectionUnhealthy(_instanceNumber, _logger);
                return false;
            }
        }

        /// <summary>
        /// Core shell command execution
        /// </summary>
        private async Task<string> ExecuteShellCommandAsync(string command, CancellationToken cancellationToken = default, int timeoutMs = 15000)
        {
            await _commandSemaphore.WaitAsync(cancellationToken);
            
            try
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);
                
                var receiver = new ConsoleOutputReceiver();
                await _adbClient.ExecuteRemoteCommandAsync(command, _device, receiver, combinedCts.Token);
                
                return receiver.ToString();
            }
            finally
            {
                _commandSemaphore.Release();
            }
        }

        /// <summary>
        /// Performance and health tracking
        /// </summary>
        private void RecordSuccessfulOperation()
        {
            _lastSuccessfulOperation = DateTime.UtcNow;
            _consecutiveFailures = 0;
        }

        private void RecordFailedOperation(Exception ex)
        {
            _consecutiveFailures++;

            // A cancelled action says nothing about the connection
            if (ex is not OperationCanceledException)
            {
                ADBConnectionManagerV2.MarkConnectionUnhealthy(_instanceNumber, _logger);
            }
        }

        /// <summary>
        /// Cheap local check used before each action. Actions used to send an "echo ping"
        /// first, which doubled the round trips; a real failure now marks the connection
        /// unhealthy instead, and GetCachedADBController still pings every few seconds.
        /// </summary>
        private bool IsUsable() => _consecutiveFailures < MAX_CONSECUTIVE_FAILURES;

        private static void RecordPerformanceMetric(string operation, TimeSpan duration)
        {
            _performanceMetrics.AddOrUpdate(operation, 
                (duration.Ticks, 1),
                (key, existing) => (existing.TotalTime + duration.Ticks, existing.Count + 1));
        }

        /// <summary>
        /// Fix screencap line ending issues (common problem with screencap -p)
        /// </summary>
        private static byte[] FixScreencapLineEndings(byte[] data)
        {
            // Convert \r\n to \n for PNG compatibility
            var result = new List<byte>();
            
            for (int i = 0; i < data.Length; i++)
            {
                if (i < data.Length - 1 && data[i] == 0x0D && data[i + 1] == 0x0A)
                {
                    result.Add(0x0A); // Add only \n
                    i++; // Skip the \r
                }
                else
                {
                    result.Add(data[i]);
                }
            }
            
            return result.ToArray();
        }

        /// <summary>
        /// Get performance statistics
        /// </summary>
        public static Dictionary<string, (double AvgMs, int Count)> GetPerformanceStats()
        {
            var stats = new Dictionary<string, (double AvgMs, int Count)>();
            
            foreach (var kvp in _performanceMetrics)
            {
                var avgTicks = kvp.Value.TotalTime / kvp.Value.Count;
                var avgMs = new TimeSpan(avgTicks).TotalMilliseconds;
                stats[kvp.Key] = (avgMs, kvp.Value.Count);
            }
            
            return stats;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _commandSemaphore?.Dispose();
                _disposed = true;
                _logger.LogInfo($"[ADBv2] Controller disposed for instance {_instanceNumber}");
            }
        }
    }
}