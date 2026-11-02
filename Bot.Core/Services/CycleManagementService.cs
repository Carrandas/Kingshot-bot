using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bot.Core.Config;
using Bot.Core.Logging;
using Bot.Core.LDPlayer;
using Bot.Core.Models;
using Bot.Core.Tasks;
using Bot.Core.Utils;

namespace Bot.Core.Services
{
    public sealed class CycleManagementService : IDisposable
    {
        private readonly LogService _logger;
        private readonly TaskManager _taskManager;
        private readonly InstanceManager _instanceManager;
        private readonly SystemResourceMonitor _resourceMonitor;

        private CancellationTokenSource? _cycleGlobalCts;
        private int _currentCycle = 0;
        private bool _isRunning = false;
        private SemaphoreSlim _instanceSemaphore = new SemaphoreSlim(1); // Will be initialized with config.TotalRunningInstances
        
        // Throttling mechanisms to prevent overloading
        private SemaphoreSlim _instanceStartupSemaphore = new SemaphoreSlim(1, 1); // Will be initialized with config.TotalRunningInstances
        private int _startupFailureCount = 0;
        private DateTime _lastStartupFailure = DateTime.MinValue;
        private const int STARTUP_FAILURE_THRESHOLD = 3; // Circuit breaker threshold
        private const int CIRCUIT_BREAKER_COOLDOWN_MINUTES = 5;
        
        // Persistent account management
        private readonly Dictionary<string, CancellationTokenSource> _persistentAccountTokens = new Dictionary<string, CancellationTokenSource>();
        private readonly Dictionary<string, Task> _persistentAccountTasks = new Dictionary<string, Task>();

        public event Action<int, TimeSpan>? OnCycleStarted;
        public event Action<int, bool>? OnCycleCompleted;
        public event Action<TimeSpan>? OnWaitingBetweenCycles;
        public event Action<string>? OnStatusUpdate;

        public bool IsRunning => _isRunning;
        public static bool IsPaused => PauseGate.IsPaused;
        public int CurrentCycle => _currentCycle;

        /// <summary>When the wait between cycles ends; null while a cycle runs or the service is stopped</summary>
        public DateTime? NextCycleUtc { get; private set; }

        /// <summary>Raised when the cycle state the dashboard shows changes (cycle started/finished, waiting, stopped)</summary>
        public event Action? OnScheduleChanged;

        /// <summary>
        /// Runs one task on one character (dashboard play button): starts the instance if needed and leaves it running afterwards.
        /// </summary>
        public async Task<TaskExecutionDetails> RunSingleTaskAsync(AccountSettings account, TaskType taskType, string? characterName, CancellationToken cancellationToken)
        {
            if (_isRunning) return TaskExecutionDetails.Failed("The cycle is running");

            _logger.LogInfo($"🚀 Ensuring LDPlayer instance {account.InstanceNumber} is running for a single {taskType} run...");
            if (!await _instanceManager.StartInstanceAsync(account.InstanceNumber, cancellationToken))
                return TaskExecutionDetails.Failed($"Could not start instance {account.InstanceNumber}");
            if (await AdbConnectionManager.GetConnectionAsync(account.InstanceNumber, _logger, cancellationToken) == null)
                return TaskExecutionDetails.Failed($"No ADB connection to instance {account.InstanceNumber}");

            return await _taskManager.RunSingleTaskAsync(account, taskType, characterName, cancellationToken);
        }


        public CycleManagementService(LogService logger, TaskManager taskManager)
        {
            _logger = logger;
            _taskManager = taskManager;
            var ldConsolePath = LDPlayerHelper.GetLDPlayerConsolePath();
            var dnConsolePath = LDPlayerHelper.GetDNPlayerConsolePath();
            _instanceManager = new InstanceManager(ldConsolePath, dnConsolePath, logger);
            _resourceMonitor = new SystemResourceMonitor(logger);

            // Subscribe to resource monitor events
            _resourceMonitor.OnSystemOverloaded += () => {
                _logger.LogWarning("🚨 System overloaded - reducing operation intensity");
            };
            _resourceMonitor.OnSystemRecovered += () => {
                _logger.LogInfo("✅ System resources recovered - resuming normal operations");
            };
        }

        public async Task PauseAsync()
        {
            if (PauseGate.IsPaused)
            {
                _logger.LogInfo("Cycle management is already paused");
                return;
            }

            _logger.LogInfo("⏸️ Pausing - the bot stops at its next tap");
            PauseGate.Pause();
            OnStatusUpdate?.Invoke("Paused");
        }

        public async Task ResumeAsync()
        {
            if (!PauseGate.IsPaused)
            {
                _logger.LogInfo("Cycle management is not paused");
                return;
            }

            _logger.LogInfo("▶️ Resuming cycle management - bot will continue operations");
            PauseGate.Resume();
            OnStatusUpdate?.Invoke("Running");
        }

        private async Task WaitIfPausedAsync(CancellationToken cancellationToken)
        {
            if (PauseGate.IsPaused)
            {
                _logger.LogInfo("⏸️ Bot is paused - waiting for resume command...");
                
                // Use a combination of WaitHandle.WaitAny and cancellation token
                var waitHandles = new WaitHandle[] { PauseGate.WaitHandle, cancellationToken.WaitHandle };
                
                await Task.Run(() => 
                {
                    while (PauseGate.IsPaused && !cancellationToken.IsCancellationRequested)
                    {
                        WaitHandle.WaitAny(waitHandles, 1000); // Check every second
                    }
                }, cancellationToken);
                
                if (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInfo("▶️ Bot resumed from pause");
                }
            }
        }

        public async Task StartCycleAsync(BotConfig config, CancellationToken externalCancellationToken)
        {
            if (_isRunning)
            {
                _logger.LogInfo("Cycle is already running");
                return;
            }

            _isRunning = true;
            _cycleGlobalCts = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken);
            var cycleToken = _cycleGlobalCts.Token;

            try
            {
                _logger.LogInfo("🔄 Starting cycle management mode");
                var random = new Random();
                var minWait = config.CycleManagement.MinWaitTimeBetweenCyclesMinutes;
                var maxWait = config.CycleManagement.MaxWaitTimeBetweenCyclesMinutes;
                var waitMinutes = random.Next(minWait, maxWait + 1);

                // Separate accounts into persistent and cycling groups
                var persistentAccounts = config.Accounts.Where(a => a.IsEnabled && a.DoNotShutdown).ToList();
                var cyclingAccounts = config.Accounts.Where(a => a.IsEnabled && !a.DoNotShutdown).ToList();

                var effectiveMaxConcurrent = ConfigureConcurrency(config, persistentAccounts, cyclingAccounts, minWait, maxWait, waitMinutes);

                // Start persistent accounts (they run continuously)
                await StartPersistentAccountsAsync(persistentAccounts, config, cycleToken);

                while (!cycleToken.IsCancellationRequested)
                {
                    // Check for pause before starting a new cycle
                    await WaitIfPausedAsync(cycleToken);
                    if (cycleToken.IsCancellationRequested) break;

                    if (!await RunSingleCycleAsync(config, cyclingAccounts, effectiveMaxConcurrent, waitMinutes, cycleToken))
                    {
                        break;
                    }

                    // Generate a new random wait time for each cycle
                    waitMinutes = random.Next(minWait, maxWait + 1);
                    await WaitBetweenCyclesAsync(waitMinutes, cycleToken);
                }
                _logger.LogInfo($"🏁 Cycle management completed or stopped after potentially {_currentCycle} cycles.");
            }
            catch (OperationCanceledException)
            {
                _logger.LogInfo("🛑 Cycle management was cancelled globally.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Error in main cycle management loop: {ex.Message} {ex.StackTrace}");
            }
            finally
            {
                _isRunning = false;
                NextCycleUtc = null;
                OnScheduleChanged?.Invoke();
                _cycleGlobalCts?.Dispose();
                _cycleGlobalCts = null;
                _instanceSemaphore.Dispose();
                _logger.LogInfo("Cycle management service stopped and cleaned up.");
            }
        }

        /// <summary>
        /// Sizes the instance and startup semaphores and the ADB limits; returns the effective maximum of concurrent instances.
        /// </summary>
        private int ConfigureConcurrency(BotConfig config, List<AccountSettings> persistentAccounts, List<AccountSettings> cyclingAccounts, int minWait, int maxWait, int waitMinutes)
        {
            // TotalRunningInstances is the absolute maximum instances that can be open at once
            // Both persistent and cycling accounts share this same pool
            var effectiveMaxConcurrent = Math.Min(config.TotalRunningInstances, config.MaxConcurrentInstances);
            _instanceSemaphore = new SemaphoreSlim(effectiveMaxConcurrent);

            // Also update the startup semaphore to respect the same limit
            _instanceStartupSemaphore?.Dispose();
            _instanceStartupSemaphore = new SemaphoreSlim(effectiveMaxConcurrent, effectiveMaxConcurrent);

            // Update ADB connection limits
            AdbConnectionManager.UpdateConnectionLimits(config.TotalRunningInstances);

            _logger.LogInfo($"📊 Configuration: {config.Accounts.Count} accounts ({persistentAccounts.Count} persistent, {cyclingAccounts.Count} cycling), {effectiveMaxConcurrent} max concurrent instances, startup delay: {config.InstanceStartupDelayMs}ms, wait: {minWait}-{maxWait}min (random: {waitMinutes}), Shutdown: {config.CycleManagement.ShutdownEmulatorsAfterCycle}, Resource throttling: {config.EnableResourceThrottling}, ADB limit: {AdbConnectionManager.GetMaxConcurrentConnections()}");
            _logger.LogInfo($"🎫 Semaphore status: {_instanceSemaphore.CurrentCount}/{effectiveMaxConcurrent} instance slots, {_instanceStartupSemaphore.CurrentCount}/{effectiveMaxConcurrent} startup slots available");

            // Validate resource allocation - persistent and cycling accounts share the same instance pool
            if (persistentAccounts.Count > 0 && cyclingAccounts.Count > 0 && config.TotalRunningInstances == 1)
            {
                _logger.LogWarning($"⚠️ WARNING: With TotalRunningInstances = 1, persistent accounts will block cycling accounts from running. Only {config.TotalRunningInstances} instance can be open at once.");
            }

            return effectiveMaxConcurrent;
        }

        /// <summary>
        /// Runs one cycle over the cycling accounts. Returns false when the maximum number of cycles is reached.
        /// </summary>
        private async Task<bool> RunSingleCycleAsync(BotConfig config, List<AccountSettings> cyclingAccounts, int effectiveMaxConcurrent, int waitMinutes, CancellationToken cycleToken)
        {
            _currentCycle++;
            NextCycleUtc = null;
            OnScheduleChanged?.Invoke();
            _logger.LogInfo($"🔄 === Starting Cycle {_currentCycle} ===");
            OnCycleStarted?.Invoke(_currentCycle, TimeSpan.FromMinutes(waitMinutes));

            var accountCycleResults = new Dictionary<string, bool>();
            var accountTasks = new List<Task<bool>>();

            // Check circuit breaker before starting accounts
            await WaitOutCircuitBreakerAsync(cycleToken);

            // Process cycling accounts with progressive startup delays to prevent overloading
            if (effectiveMaxConcurrent == 1 || cyclingAccounts.Count <= 1)
            {
                await ProcessCyclingAccountsSequentiallyAsync(config, cyclingAccounts, effectiveMaxConcurrent, accountCycleResults, cycleToken);
            }
            else
            {
                await StartCyclingAccountsInParallelAsync(config, cyclingAccounts, effectiveMaxConcurrent, accountCycleResults, accountTasks, cycleToken);
            }

            // Wait for all accounts to finish processing (only needed for parallel execution)
            if (config.TotalRunningInstances > 1 && accountTasks.Count > 0)
            {
                await Task.WhenAll(accountTasks);
            }

            bool allAccountsSuccessfulInCycleThisIteration = !accountCycleResults.Any(kvp => !kvp.Value);
            var successfulAccountsCount = accountCycleResults.Count(kvp => kvp.Value);
            _logger.LogInfo($"📊 Cycle {_currentCycle} results: {successfulAccountsCount}/{config.Accounts.Count} accounts processed successfully according to their task results.");

            OnCycleCompleted?.Invoke(_currentCycle, allAccountsSuccessfulInCycleThisIteration);

            if (config.CycleManagement.MaxCycles > 0 && _currentCycle >= config.CycleManagement.MaxCycles)
            {
                _logger.LogInfo($"🏁 Reached maximum cycles ({config.CycleManagement.MaxCycles}), stopping cycle management");
                return false;
            }
            return true;
        }

        private async Task WaitOutCircuitBreakerAsync(CancellationToken cycleToken)
        {
            if (!IsCircuitBreakerTripped())
            {
                return;
            }

            _logger.LogWarning($"🚨 Circuit breaker tripped due to startup failures. Waiting {CIRCUIT_BREAKER_COOLDOWN_MINUTES} minutes before retry.");
            await Task.Delay(TimeSpan.FromMinutes(CIRCUIT_BREAKER_COOLDOWN_MINUTES), cycleToken);
            ResetCircuitBreaker();
        }

        private async Task WaitBetweenCyclesAsync(int waitMinutes, CancellationToken cycleToken)
        {
            if (waitMinutes <= 0 || cycleToken.IsCancellationRequested)
            {
                return;
            }

            _logger.LogInfo($"⏳ Waiting {waitMinutes} minutes before next cycle");
            NextCycleUtc = DateTime.UtcNow.AddMinutes(waitMinutes);
            OnScheduleChanged?.Invoke();
            OnWaitingBetweenCycles?.Invoke(TimeSpan.FromMinutes(waitMinutes));

            for (int minute = 0; minute < waitMinutes; minute++)
            {
                if (cycleToken.IsCancellationRequested) break;
                await Task.Delay(TimeSpan.FromMinutes(1), cycleToken);
                _logger.LogInfo($"⏳ Wait time remaining: {waitMinutes - minute - 1} minutes");
            }
        }

        /// <summary>
        /// Waits for the pause gate and, when throttling is on, for the system to have room for another account.
        /// Returns false when the cycle was cancelled.
        /// </summary>
        private async Task<bool> PrepareNextAccountAsync(BotConfig config, int accountIndex, CancellationToken cycleToken)
        {
            if (cycleToken.IsCancellationRequested) return false;

            // Check for pause before processing each account
            await WaitIfPausedAsync(cycleToken);
            if (cycleToken.IsCancellationRequested) return false;

            // Check system resources before starting next account
            if (config.EnableResourceThrottling)
            {
                if (!_resourceMonitor.IsSafeToStartOperation())
                {
                    _logger.LogWarning($"⚠️ System resources insufficient for account {accountIndex + 1}. {_resourceMonitor.GetResourceReport()}");
                    await _resourceMonitor.WaitForResourceAvailabilityAsync(cycleToken, 30);
                }

                var resourceDelay = _resourceMonitor.GetRecommendedDelayMs();
                if (resourceDelay > 0)
                {
                    _logger.LogInfo($"⏳ Resource-based delay: waiting {resourceDelay}ms due to system load");
                    await Task.Delay(resourceDelay, cycleToken);
                }
            }
            return true;
        }

        private async Task ProcessCyclingAccountsSequentiallyAsync(
            BotConfig config, List<AccountSettings> cyclingAccounts, int effectiveMaxConcurrent,
            Dictionary<string, bool> accountCycleResults, CancellationToken cycleToken)
        {
            // Sequential processing - one account at a time
            _logger.LogInfo($"🔄 Processing {cyclingAccounts.Count} cycling accounts sequentially (sharing {effectiveMaxConcurrent} total slots with persistent accounts)");
            for (int accountIndex = 0; accountIndex < cyclingAccounts.Count; accountIndex++)
            {
                var account = cyclingAccounts[accountIndex];
                if (!await PrepareNextAccountAsync(config, accountIndex, cycleToken)) break;

                // Progressive delay between account startups (configurable delay per account)
                if (accountIndex > 0)
                {
                    var progressiveDelay = accountIndex * config.InstanceStartupDelayMs;
                    _logger.LogInfo($"⏳ Progressive delay: waiting {progressiveDelay}ms before starting account {accountIndex + 1}/{cyclingAccounts.Count}");
                    await Task.Delay(progressiveDelay, cycleToken);
                }

                // Process account directly (no parallel execution)
                _logger.LogInfo($"🎯 Processing cycling account {accountIndex + 1}/{cyclingAccounts.Count}: {account.AccountName}");

                // Wait for a slot to become available (same logic as parallel execution)
                _logger.LogInfo($"🎫 Account {account.AccountName} waiting for instance semaphore slot...");
                await _instanceSemaphore.WaitAsync(cycleToken);
                bool success = false;

                try
                {
                    _logger.LogInfo($"✅ Account {account.AccountName} acquired instance semaphore slot");
                    success = await ProcessAccountAsync(account, config, cycleToken, accountCycleResults);
                }
                finally
                {
                    _instanceSemaphore.Release();
                    _logger.LogInfo($"🔓 Account {account.AccountName} released instance semaphore slot");
                }

                _logger.LogInfo($"✅ Cycling account {account.AccountName} completed with result: {(success ? "Success" : "Failed")}");
            }
        }

        private async Task StartCyclingAccountsInParallelAsync(
            BotConfig config, List<AccountSettings> cyclingAccounts, int effectiveMaxConcurrent,
            Dictionary<string, bool> accountCycleResults, List<Task<bool>> accountTasks, CancellationToken cycleToken)
        {
            // Parallel processing with semaphore control
            _logger.LogInfo($"🔄 Processing {cyclingAccounts.Count} cycling accounts in parallel (sharing {effectiveMaxConcurrent} total slots with persistent accounts)");
            for (int accountIndex = 0; accountIndex < cyclingAccounts.Count; accountIndex++)
            {
                var account = cyclingAccounts[accountIndex];
                if (!await PrepareNextAccountAsync(config, accountIndex, cycleToken)) break;

                // Start processing this account and add the task to a list
                var currentIndex = accountIndex; // Capture for closure
                var accountTask = Task.Run(
                    () => RunParallelAccountAsync(account, currentIndex, cyclingAccounts.Count, config, accountCycleResults, cycleToken),
                    cycleToken);

                accountTasks.Add(accountTask);
            }
        }

        private async Task<bool> RunParallelAccountAsync(
            AccountSettings account, int index, int accountCount, BotConfig config,
            Dictionary<string, bool> accountCycleResults, CancellationToken cycleToken)
        {
            // Progressive delay between account startups (inside task for parallel execution)
            if (index > 0)
            {
                var progressiveDelay = index * config.InstanceStartupDelayMs;
                _logger.LogInfo($"⏳ Account {account.AccountName} progressive delay: waiting {progressiveDelay}ms before starting (position {index + 1}/{accountCount})");
                await Task.Delay(progressiveDelay, cycleToken);
            }

            // Wait for a slot to become available
            _logger.LogInfo($"🎫 Account {account.AccountName} waiting for instance semaphore slot...");
            await _instanceSemaphore.WaitAsync(cycleToken);
            _logger.LogInfo($"✅ Account {account.AccountName} acquired instance semaphore slot");

            return await ProcessAccountAsync(account, config, cycleToken, accountCycleResults);
        }

        private async Task<bool> ProcessAccountAsync(AccountSettings account, BotConfig config, CancellationToken cycleToken, Dictionary<string, bool> accountCycleResults)
        {
            bool accountOverallSuccess = false;
            try
            {
                _logger.LogInfo($"🎯 Processing account {account.AccountName} (Instance {account.InstanceNumber}) in Cycle {_currentCycle}");

                if (cycleToken.IsCancellationRequested)
                {
                    _logger.LogInfo($"🛑 Account {account.AccountName} processing cancelled before start due to global token.");
                    return false;
                }

                // Instance semaphore slot already acquired by parallel task runner
                bool instanceStarted = await StartAccountInstanceAsync(account, cycleToken);

                if (cycleToken.IsCancellationRequested)
                {
                    _logger.LogInfo($"🛑 Account {account.AccountName} processing cancelled after instance start attempt.");
                    if (instanceStarted && config.CycleManagement.ShutdownEmulatorsAfterCycle)
                        await _instanceManager.StopInstanceAsync(account.InstanceNumber, CancellationToken.None);
                    return false;
                }

                if (instanceStarted)
                {
                    accountOverallSuccess = await RunAccountTasksAsync(account, cycleToken);
                }
                else
                {
                    _logger.LogError($"❌ Failed to start LDPlayer instance {account.InstanceNumber} for account {account.AccountName}. Skipping tasks.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Error processing account {account.AccountName}: {ex.Message}");
                if (ex.InnerException != null)
                    _logger.LogError($"Inner exception: {ex.InnerException.Message}");
            }
            finally
            {
                accountCycleResults[account.AccountName] = accountOverallSuccess;

                // Release the shared instance slot back to the pool
                _instanceSemaphore.Release();
                _logger.LogInfo($"🎫 Cycling account {account.AccountName} released instance slot");

                if (config.CycleManagement.ShutdownEmulatorsAfterCycle && !cycleToken.IsCancellationRequested)
                {
                    await ShutdownAccountInstanceAsync(account);
                }
            }

            return accountOverallSuccess;
        }

        private async Task<bool> StartAccountInstanceAsync(AccountSettings account, CancellationToken cycleToken)
        {
            // Throttle instance startup to prevent system overload
            _logger.LogInfo($"🚀 Ensuring LDPlayer instance {account.InstanceNumber} is running for account {account.AccountName}...");
            await _instanceStartupSemaphore.WaitAsync(cycleToken);
            bool instanceStarted = false;

            try
            {
                _logger.LogInfo($"🔒 Acquired instance startup slot for {account.AccountName} (max {_instanceStartupSemaphore.CurrentCount + 1} concurrent)");
                instanceStarted = await _instanceManager.StartInstanceAsync(account.InstanceNumber, cycleToken);

                if (!instanceStarted)
                {
                    RecordStartupFailure();
                    _logger.LogWarning($"❌ Failed to start instance {account.InstanceNumber} for {account.AccountName}");
                }
                else
                {
                    _logger.LogInfo($"✅ Instance startup successful for {account.AccountName}");
                }
            }
            finally
            {
                _instanceStartupSemaphore.Release();
                _logger.LogInfo($"🔓 Released instance startup slot for {account.AccountName}");
            }
            return instanceStarted;
        }

        private async Task<bool> RunAccountTasksAsync(AccountSettings account, CancellationToken cycleToken)
        {
            _logger.LogInfo($"✅ LDPlayer instance {account.InstanceNumber} confirmed running/started for {account.AccountName}.");

            // No extra wait: the boot wait has already checked ADB and taken a screenshot

            // Ensure ADB connection is established before running tasks
            _logger.LogInfo($"🔌 Ensuring ADB connection for instance {account.InstanceNumber}...");
            var adbController = await AdbConnectionManager.GetConnectionAsync(account.InstanceNumber, _logger, cycleToken);
            if (adbController == null)
            {
                _logger.LogError($"❌ Failed to establish ADB connection for instance {account.InstanceNumber}");
                return false;
            }
            _logger.LogInfo($"✅ ADB connection established for instance {account.InstanceNumber}");

            var success = await _taskManager.RunTasksForAccountAsync(account, cycleToken);

            if (cycleToken.IsCancellationRequested)
            {
                _logger.LogInfo($"🛑 Account {account.AccountName} processing cancelled during/after tasks.");
                return false;
            }
            if (success)
            {
                _logger.LogInfo($"✅ Account {account.AccountName} (Instance {account.InstanceNumber}) completed its tasks successfully in Cycle {_currentCycle}");
            }
            else
            {
                _logger.LogError($"❌ Account {account.AccountName} (Instance {account.InstanceNumber}) processing failed or was incomplete.");
            }
            return success;
        }

        private async Task ShutdownAccountInstanceAsync(AccountSettings account)
        {
            try
            {
                _logger.LogInfo($"🔄 Shutting down LDPlayer instance {account.InstanceNumber} after cycle completion...");
                await _instanceManager.StopInstanceAsync(account.InstanceNumber, CancellationToken.None);
                _logger.LogInfo($"✅ LDPlayer instance {account.InstanceNumber} shut down successfully");

                // Clear ChangeAccount tracking for this instance since it's shut down
                Tasks.Modules.ChangeAccountTask.ClearInstanceTracking(account.InstanceNumber);
                _logger.LogInfo($"🔄 Cleared ChangeAccount tracking for instance {account.InstanceNumber}");

                // Reset farming march cache for this instance since it's shut down
                Models.FarmingSettings.ResetMarchesSent(account.InstanceNumber);
                _logger.LogInfo($"🔄 Reset farming march cache for instance {account.InstanceNumber}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Error shutting down LDPlayer instance {account.InstanceNumber}: {ex.Message}");
            }
        }

        public async Task StopCycleAsync()
        {
            if (!_isRunning)
                return;

            _logger.LogInfo("🛑 Requesting to stop cycle management service...");
            OnStatusUpdate?.Invoke("Stopping cycle management...");

            try
            {
                // Cancel ongoing operations
                if (_cycleGlobalCts != null) await _cycleGlobalCts.CancelAsync();
                
                // Wait briefly for tasks to acknowledge cancellation
                await Task.Delay(500, _cycleGlobalCts?.Token ?? CancellationToken.None);
                
                // Force cleanup
                _isRunning = false;
                
                // Reset pause state when stopping
                if (PauseGate.IsPaused)
                {
                    _logger.LogInfo("Resetting pause state during cycle stop");
                    PauseGate.Resume(); // Ensure any waiting threads are released
                }
                
                // Stop persistent accounts
                await StopPersistentAccountsAsync();
                
                _cycleGlobalCts?.Dispose();
                _cycleGlobalCts = null;
                
                // Only stop instances that are actually running
                var activeInstances = await GetActiveInstanceIndexesAsync();
                if (activeInstances.Any())
                {
                    _logger.LogInfo($"Stopping {activeInstances.Count} active instances: {string.Join(", ", activeInstances)}");
                    var tasks = activeInstances.Select(index =>
                        _instanceManager.StopInstanceAsync(index, CancellationToken.None));
                    await Task.WhenAll(tasks);
                }
                else
                {
                    _logger.LogInfo("No active instances found to stop.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error during cycle stop: {ex.Message}");
            }
        }

        private async Task<HashSet<int>> GetActiveInstanceIndexesAsync()
        {
            var activeInstances = new HashSet<int>();
            try
            {
                var runningInstances = await _instanceManager.GetRunningInstancesAsync(CancellationToken.None);
                foreach (var instanceLine in runningInstances)
                {
                    var parts = instanceLine.Split(',');
                    if (parts.Length >= 1 && int.TryParse(parts[0], out int index))
                    {
                        activeInstances.Add(index);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error getting running instances list: {ex.Message}");
            }
            return activeInstances;
        }

        // Keep the old synchronous method for backward compatibility but make it call the async version
        public void StopCycle()
        {
            // Use proper async pattern with timeout to avoid deadlock
            using var cts = new CancellationTokenSource(5000);
            try
            {
                // Use GetAwaiter().GetResult() instead of Wait() to avoid deadlock issues
                StopCycleAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                // Log but don't throw during shutdown
                System.Diagnostics.Debug.WriteLine($"Error during cycle shutdown: {ex.Message}");
            }
        }

        /// <summary>
        /// Circuit breaker helper methods to prevent cascade failures
        /// </summary>
        private bool IsCircuitBreakerTripped()
        {
            if (_startupFailureCount >= STARTUP_FAILURE_THRESHOLD)
            {
                var timeSinceLastFailure = DateTime.UtcNow - _lastStartupFailure;
                return timeSinceLastFailure < TimeSpan.FromMinutes(CIRCUIT_BREAKER_COOLDOWN_MINUTES);
            }
            return false;
        }

        private void RecordStartupFailure()
        {
            _startupFailureCount++;
            _lastStartupFailure = DateTime.UtcNow;
            _logger.LogWarning($"🚨 Startup failure recorded. Count: {_startupFailureCount}/{STARTUP_FAILURE_THRESHOLD}");
        }

        private void ResetCircuitBreaker()
        {
            _startupFailureCount = 0;
            _lastStartupFailure = DateTime.MinValue;
            _logger.LogInfo("🔄 Circuit breaker reset - ready for normal operation");
        }

        private async Task StartPersistentAccountsAsync(List<AccountSettings> persistentAccounts, BotConfig config, CancellationToken cycleToken)
        {
            if (persistentAccounts.Count == 0)
            {
                _logger.LogInfo("📋 No persistent accounts configured - all accounts will cycle normally");
                return;
            }

            _logger.LogInfo($"🔒 Starting {persistentAccounts.Count} persistent accounts (Do Not Shutdown enabled)");

            foreach (var account in persistentAccounts)
            {
                if (cycleToken.IsCancellationRequested) break;

                // Create a cancellation token for this persistent account
                var accountCts = CancellationTokenSource.CreateLinkedTokenSource(cycleToken);
                _persistentAccountTokens[account.AccountName] = accountCts;

                // Start the persistent account task
                var persistentTask = Task.Run(() => RunPersistentAccountAsync(account, config, accountCts.Token), cycleToken);
                _persistentAccountTasks[account.AccountName] = persistentTask;

                _logger.LogInfo($"🚀 Started persistent account: {account.AccountName} (Instance {account.InstanceNumber})");

                // Small delay between starting persistent accounts
                await Task.Delay(2000, cycleToken);
            }
        }

        private async Task RunPersistentAccountAsync(AccountSettings account, BotConfig config, CancellationToken cancellationToken)
        {
            _logger.LogInfo($"🔒 Starting persistent mode for account {account.AccountName} (Instance {account.InstanceNumber})");

            try
            {
                // Acquire a slot from the shared instance semaphore (persistent accounts share slots with cycling accounts)
                _logger.LogInfo($"🎫 Persistent account {account.AccountName} waiting for available instance slot...");
                await _instanceSemaphore.WaitAsync(cancellationToken);
                _logger.LogInfo($"✅ Persistent account {account.AccountName} acquired instance slot");

                if (!await StartPersistentInstanceAsync(account, cancellationToken))
                {
                    return;
                }

                // Add initialization delay
                await Task.Delay(2000, cancellationToken);

                // Establish ADB connection
                _logger.LogInfo($"🔌 Establishing ADB connection for persistent account {account.AccountName}...");
                var adbController = await AdbConnectionManager.GetConnectionAsync(account.InstanceNumber, _logger, cancellationToken);
                if (adbController == null)
                {
                    _logger.LogError($"❌ Failed to establish ADB connection for persistent account {account.InstanceNumber}");
                    return;
                }

                await Task.Delay(1000, cancellationToken);

                // Continuous task execution loop
                var taskCycle = 0;
                while (!cancellationToken.IsCancellationRequested)
                {
                    taskCycle++;
                    _logger.LogInfo($"🔄 Persistent account {account.AccountName} - Task Cycle #{taskCycle}");

                    if (!await RunPersistentTaskCycleAsync(account, config, taskCycle, cancellationToken))
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInfo($"🛑 Persistent account {account.AccountName} operation cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Fatal error in persistent account {account.AccountName}: {ex.Message}");
            }
            finally
            {
                _logger.LogInfo($"🔒 Persistent account {account.AccountName} shutting down");

                // Release the instance slot back to the shared pool
                _instanceSemaphore.Release();
                _logger.LogInfo($"🎫 Persistent account {account.AccountName} released instance slot");

                // Note: We don't shutdown the instance here as it's persistent
                // The instance will only shutdown when the entire service stops
            }
        }

        private async Task<bool> StartPersistentInstanceAsync(AccountSettings account, CancellationToken cancellationToken)
        {
            // Start and maintain the instance
            await _instanceStartupSemaphore.WaitAsync(cancellationToken);

            try
            {
                var instanceStarted = await _instanceManager.StartInstanceAsync(account.InstanceNumber, cancellationToken);
                if (instanceStarted)
                {
                    _logger.LogInfo($"✅ Persistent account instance {account.InstanceNumber} started successfully");
                }
                else
                {
                    _logger.LogError($"❌ Failed to start persistent account instance {account.InstanceNumber}");
                }
                return instanceStarted;
            }
            finally
            {
                _instanceStartupSemaphore.Release();
            }
        }

        /// <summary>
        /// One pass of the persistent account's task loop. Returns false when the loop should end.
        /// </summary>
        private async Task<bool> RunPersistentTaskCycleAsync(AccountSettings account, BotConfig config, int taskCycle, CancellationToken cancellationToken)
        {
            try
            {
                // Check for pause
                await WaitIfPausedAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested) return false;

                // Run tasks continuously - only run startup on the first cycle
                var runStartup = taskCycle == 1;
                if (runStartup)
                {
                    _logger.LogInfo($"🚀 Running first-time startup sequence for persistent account {account.AccountName}");
                }
                var success = await _taskManager.RunTasksForAccountAsync(account, cancellationToken, runStartup);

                if (success)
                {
                    _logger.LogInfo($"✅ Persistent account {account.AccountName} - Task Cycle #{taskCycle} completed successfully");
                }
                else
                {
                    _logger.LogWarning($"⚠️ Persistent account {account.AccountName} - Task Cycle #{taskCycle} had issues");
                }

                // Wait before next task cycle (configurable delay)
                var waitMinutes = config.CycleManagement.PersistentAccountWaitMinutes;
                if (waitMinutes > 0)
                {
                    var waitTime = TimeSpan.FromMinutes(waitMinutes);
                    _logger.LogInfo($"⏳ Persistent account {account.AccountName} waiting {waitTime.TotalMinutes} minutes before next task cycle");
                    await Task.Delay(waitTime, cancellationToken);
                }
                else
                {
                    _logger.LogInfo($"🔄 Persistent account {account.AccountName} continuing immediately to next task cycle (no wait configured)");
                }
                return true;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInfo($"🛑 Persistent account {account.AccountName} task cycle cancelled");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Error in persistent account {account.AccountName} task cycle #{taskCycle}: {ex.Message}");

                // Wait longer on error
                await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
                return true;
            }
        }

        private async Task StopPersistentAccountsAsync()
        {
            _logger.LogInfo($"🛑 Stopping {_persistentAccountTasks.Count} persistent accounts...");

            // Cancel all persistent account tokens
            foreach (var kvp in _persistentAccountTokens)
            {
                try
                {
                    await kvp.Value.CancelAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error cancelling persistent account {kvp.Key}: {ex.Message}");
                }
            }

            // Wait for all persistent tasks to complete
            var timeout = TimeSpan.FromSeconds(30);
            try
            {
                await Task.WhenAll(_persistentAccountTasks.Values).WaitAsync(timeout, _cycleGlobalCts?.Token ?? CancellationToken.None);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("⚠️ Timeout waiting for persistent accounts to stop");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error stopping persistent accounts: {ex.Message}");
            }

            // Cleanup
            foreach (var kvp in _persistentAccountTokens)
            {
                kvp.Value.Dispose();
            }
            _persistentAccountTokens.Clear();
            _persistentAccountTasks.Clear();

            _logger.LogInfo("✅ All persistent accounts stopped");
        }

        public void Dispose()
        {
            _logger.LogInfo("Disposing CycleManagementService.");
            StopCycle();
            _cycleGlobalCts?.Dispose();
            _instanceSemaphore.Dispose();
            _instanceStartupSemaphore?.Dispose();
            _resourceMonitor?.Dispose();

            // Cleanup persistent account resources
            foreach (var kvp in _persistentAccountTokens)
            {
                kvp.Value.Dispose();
            }
            _persistentAccountTokens.Clear();
            _persistentAccountTasks.Clear();
        }
    }
} 