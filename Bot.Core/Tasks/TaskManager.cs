using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Diagnostics;
using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Tasks.Modules;
using Bot.Core.Exceptions;
using Bot.Core.Services;
using Bot.Core.LDPlayer;
using System.IO;
using Bot.Core.Utils;
using System.Text.Json;
using Bot.Core.ImageDetection;
using Bot.Core.Config;

namespace Bot.Core.Tasks
{
    public class TaskManager
    {
        private readonly LogService _logger;
        private readonly IUserNotificationService? _userNotifications;
        private readonly ConcurrentDictionary<TaskType, ITask> _availableTasks = new();
        private readonly ConcurrentDictionary<int, bool> _instanceStartupComplete = new();
        private static readonly ConcurrentDictionary<int, byte> _activeInstanceRuns = new();
        private readonly StartupTask _startupTask;
        private readonly List<AccountSettings> _accounts = new();
        private readonly InstanceManager _instanceManager;

        public TaskManager(LogService logger, IServiceProvider serviceProvider, IUserNotificationService? userNotifications = null)
        {
            _logger = logger;
            _userNotifications = userNotifications;
            
            // Purge all logs at the beginning of a new session
            // LogService.PurgeAllLogs(); // Disabled to prevent file access errors
            
            // Initialize startup task
            _startupTask = new StartupTask();
            _availableTasks[TaskType.Startup] = _startupTask;
            
            // Initialize LDPlayer instance manager with path and logger
            var ldConsolePath = LDPlayerHelper.GetLDPlayerConsolePath();
            var dnConsolePath = LDPlayerHelper.GetDNPlayerConsolePath();
            _instanceManager = new InstanceManager(ldConsolePath, dnConsolePath, logger);

            // Initialize available task modules with new unified base class
            foreach (var (type, task) in CreateTaskModules(logger, serviceProvider)) _availableTasks[type] = task;
            
            _logger.LogInfo($"TaskManager initialized with {_availableTasks.Count} tasks plus startup task");
        }

        /// <summary>Every task module except Startup, by type. Static so the tests can check the registration without LDPlayer.</summary>
        internal static Dictionary<TaskType, ITask> CreateTaskModules(LogService logger, IServiceProvider serviceProvider)
        {
            var tasks = new Dictionary<TaskType, ITask>();
            tasks[TaskType.AccountDetection] = new AccountDetectionTask();
            tasks[TaskType.AutoHunt] = new AutoHuntTask();
            tasks[TaskType.Farming] = new FarmingTask();
            tasks[TaskType.TroopTraining] = new TroopTrainingTask();
            tasks[TaskType.ConquestCollect] = new ConquestCollectTask();
            tasks[TaskType.AutoAllianceHelp] = new AllianceHelpTask();
            tasks[TaskType.Recovery] = new RecoveryTask();
            tasks[TaskType.ClaimMail] = new ClaimMailTask();
            tasks[TaskType.ChangeAccount] = new ChangeAccountTask();
            tasks[TaskType.AutoHeal] = new AutoHealTask(logger);
            tasks[TaskType.AutoBuild] = new AutoBuildTask();
            tasks[TaskType.AutoClaimHero] = new AutoClaimHeroTask(
                (serviceProvider.GetService(typeof(UnifiedTemplateMatchingService)) as UnifiedTemplateMatchingService)!);
            tasks[TaskType.AutoShield] = new AutoShieldTask(ConfigurationManager.Instance, serviceProvider);
            tasks[TaskType.CollectVip] = new CollectVipTask();
            tasks[TaskType.ClaimMissions] = new ClaimMissionsTask();
            tasks[TaskType.ResidentWelcome] = new ResidentWelcomeTask();
            tasks[TaskType.AllianceTechnology] = new AllianceTechnologyTask();
            tasks[TaskType.AutoTechnology] = new AutoTechnologyTask();
            tasks[TaskType.AutoRally] = new AutoRallyTask();
            tasks[TaskType.CollectRewards] = new CollectRewardsTask();
            tasks[TaskType.PetAdventure] = new PetAdventureTask();
            tasks[TaskType.CollectOnlineRewards] = new CollectOnlineRewardsTask();
            tasks[TaskType.Journey] = new JourneyTask();
            tasks[TaskType.TruegoldCrucible] = new TruegoldCrucibleTask();
            tasks[TaskType.AllianceTriumph] = new AllianceTriumphTask();
            tasks[TaskType.WaterEssence] = new WaterEssenceTask();
            tasks[TaskType.NomadicMerchant] = new NomadicMerchantTask();
            tasks[TaskType.AllianceChests] = new AllianceChestsTask();
            tasks[TaskType.SuggestionBox] = new SuggestionBoxTask();
            tasks[TaskType.AllianceMobilization] = new AllianceMobilizationTask();
            tasks[TaskType.PetSkill] = new PetSkillTask();
            tasks[TaskType.MysticTrial] = new MysticTrialTask();
            tasks[TaskType.Arena] = new ArenaTask();
            tasks[TaskType.AutoJoinRally] = new AutoJoinRallyTask();
            return tasks;
        }

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInfo("Initializing Task Manager...");
            
            // Initialize startup task first
            _logger.LogInfo("Initializing startup task...");
            await _startupTask.InitializeAsync(_logger, cancellationToken);
            _logger.LogInfo("Startup task initialized successfully");

            // Initialize other tasks
            foreach (var task in _availableTasks.Values)
            {
                await task.InitializeAsync(_logger, cancellationToken);
            }
            
            _logger.LogInfo($"Task Manager initialized with {_availableTasks.Count + 1} tasks");
        }

        public async Task<bool> RunTasksForAccountAsync(AccountSettings account, CancellationToken cancellationToken = default, bool runStartup = true)
        {
            // one task loop per instance: the cycle service and the instance Start button both land here,
            // and two loops on one emulator tap over each other. Static so it holds across TaskManager instances.
            if (!_activeInstanceRuns.TryAdd(account.InstanceNumber, 0))
            {
                _logger.LogWarning($"[{account.AccountName}] Tasks already running for instance {account.InstanceNumber}, ignoring second run");
                return false;
            }

            try
            {
                return await RunTasksForAccountCoreAsync(account, cancellationToken, runStartup);
            }
            finally
            {
                TaskRunHistory.SetCurrent(account.InstanceNumber, null);
                _activeInstanceRuns.TryRemove(account.InstanceNumber, out _);
            }
        }

        private async Task<bool> RunTasksForAccountCoreAsync(AccountSettings account, CancellationToken cancellationToken, bool runStartup)
        {
            // Create instance-specific logger if not provided
            using var contextLogger = LogService.ForInstance(account.InstanceNumber, account.AccountName);
            
            try
            {
                contextLogger.LogInfo($"🎯 Starting tasks for account {account.AccountName} (Instance {account.InstanceNumber})");

                if (runStartup)
                {
                    if (!await RunStartupAsync(account, contextLogger, cancellationToken)) return false;
                }
                else
                {
                    contextLogger.LogInfo($"🔄 Skipping startup sequence for persistent account {account.AccountName} (subsequent cycle)");
                }

                // Run all configured tasks in sequence
                contextLogger.LogInfo($"📋 Found {account.EnabledTasks.Count} enabled tasks: {string.Join(", ", account.EnabledTasks)}");
                
                await ExecuteTasksForAccount(account, cancellationToken, runStartup);

                contextLogger.LogInfo($"✅ All tasks completed successfully");
                return true;
            }
            catch (OperationCanceledException)
            {
                contextLogger.LogInfo($"Task sequence was cancelled");
                throw;
            }
            catch (Exception ex)
            {
                contextLogger.LogError($"❌ Error running tasks: {ex.Message}");
                contextLogger.LogError($"❌ Stack trace: {ex.StackTrace}");
                return false;
            }
        }

        private enum StartupOutcome { Success, Retry, Halt }

        /// <summary>Startup sequence with a 5 minute timeout, a reboot on timeout and recovery on request. False: stop the account's tasks.</summary>
        private async Task<bool> RunStartupAsync(AccountSettings account, LogService contextLogger, CancellationToken cancellationToken)
        {
            // --- STARTUP SEQUENCE WITH TIMEOUT AND RECOVERY ---
            bool initialStartupAttempt = true;

            while (true) // Loop for startup attempts
            {
                var outcome = await RunStartupAttemptAsync(account, contextLogger, initialStartupAttempt, cancellationToken);
                if (outcome != StartupOutcome.Retry)
                {
                    return outcome == StartupOutcome.Success;
                }
                initialStartupAttempt = false;
            }
        }

        private async Task<StartupOutcome> RunStartupAttemptAsync(AccountSettings account, LogService contextLogger, bool initialStartupAttempt, CancellationToken cancellationToken)
        {
            const int startupTimeoutMs = 300_000; // 5 minutes
            using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var startupTask = _startupTask.ExecuteAsync(account, contextLogger, startupCts.Token, false, _userNotifications);
            var timeoutTask = Task.Delay(startupTimeoutMs, startupCts.Token);

            contextLogger.LogInfo($"🚀 Running startup sequence{(initialStartupAttempt ? "" : " again")} with a {startupTimeoutMs / 1000}s timeout...");

            var completedTask = await Task.WhenAny(startupTask, timeoutTask);

            if (completedTask == timeoutTask)
            {
                await startupCts.CancelAsync();
                contextLogger.LogError($"❌ Startup sequence for instance {account.InstanceNumber} timed out after {startupTimeoutMs / 1000} seconds.");
                return await RebootAfterStartupTimeoutAsync(account, contextLogger, cancellationToken);
            }

            var startupResult = await startupTask;
            contextLogger.LogInfo($"🚀 Startup sequence result: Success={startupResult.Success}, Message='{startupResult.Message}', RecoveryNeeded={startupResult.RecoveryNeeded}");

            if (startupResult.Success)
            {
                return StartupOutcome.Success; // Startup successful
            }

            // Startup failed, check for recovery
            contextLogger.LogError($"❌ Startup sequence failed: {startupResult.Message}");
            if (startupResult.RecoveryNeeded)
            {
                return await RecoverFromStartupFailureAsync(account, contextLogger, cancellationToken);
            }
            contextLogger.LogError($"❌ Startup failed but did not request recovery. Stopping task execution for this account.");
            return StartupOutcome.Halt;
        }

        private async Task<StartupOutcome> RebootAfterStartupTimeoutAsync(AccountSettings account, LogService contextLogger, CancellationToken cancellationToken)
        {
            contextLogger.LogInfo($"🔄 Attempting to reboot instance {account.InstanceNumber}...");

            var rebootSuccess = await _instanceManager.RebootInstanceAsync(account.InstanceNumber, cancellationToken);
            if (!rebootSuccess)
            {
                contextLogger.LogError($"❌ Failed to reboot instance {account.InstanceNumber}. Halting tasks.");
                return StartupOutcome.Halt;
            }

            contextLogger.LogInfo($"✅ Instance {account.InstanceNumber} reboot command sent. Waiting for it to be ready...");
            var isReady = await _instanceManager.WaitUntilFullyBootedAsync(account.InstanceNumber, cancellationToken, 120);
            if (isReady)
            {
                contextLogger.LogInfo($"✅ Instance {account.InstanceNumber} is ready after reboot. Retrying startup sequence...");
                return StartupOutcome.Retry; // Retry the while loop for startup
            }
            contextLogger.LogError($"❌ Instance {account.InstanceNumber} did not become ready after reboot. Halting tasks.");
            return StartupOutcome.Halt;
        }

        private async Task<StartupOutcome> RecoverFromStartupFailureAsync(AccountSettings account, LogService contextLogger, CancellationToken cancellationToken)
        {
            contextLogger.LogInfo($"🚨 Startup requested recovery. Attempting recovery for {account.AccountName}...");
            if (!_availableTasks.TryGetValue(TaskType.Recovery, out var recoveryTask))
            {
                contextLogger.LogError($"❌ Recovery task not found in available tasks.");
                return StartupOutcome.Halt;
            }

            contextLogger.LogInfo($"🔧 Found recovery task, executing...");
            var recoveryResult = await recoveryTask.ExecuteAsync(account, contextLogger, cancellationToken);
            contextLogger.LogInfo($"🔧 Recovery result: Success={recoveryResult.Success}, Message='{recoveryResult.Message}'");

            if (!recoveryResult.Success)
            {
                contextLogger.LogError($"❌ Recovery failed: {recoveryResult.Message}. Halting tasks for this account.");
                return StartupOutcome.Halt;
            }

            // After recovery, loop back to try startup again
            return StartupOutcome.Retry;
        }

        public async Task<Dictionary<string, bool>> RunTasksForMultipleAccountsAsync(
            IEnumerable<AccountSettings> accounts,
            CancellationToken cancellationToken = default)
        {
            var results = new ConcurrentDictionary<string, bool>();
            var accountsList = accounts.ToList();
            _logger.LogInfo($"Starting task execution for {accountsList.Count} accounts.");

            foreach (var account in accountsList)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInfo("Task execution for multiple accounts cancelled.");
                    break;
                }
                var success = await RunTasksForAccountAsync(account, cancellationToken);
                results[account.AccountName] = success;
            }

            return new Dictionary<string, bool>(results);
        }

        private const int MaxFarmingRetries = 3; // farming retries are capped to prevent infinite loops
        private const int MaxTaskRestarts = 5; // a task that asks for a restart on every run would otherwise restart the list forever

        /// <summary>The task list of one run; the index and the list change when tasks are retried or the list restarts.</summary>
        private sealed class TaskListRun
        {
            public IList<TaskType> Tasks;
            public int Index;
            public int FarmingRetryCount;
            public int TaskRestartCount;

            public TaskListRun(IList<TaskType> tasks)
            {
                Tasks = tasks;
            }

            public void Append(TaskType taskType)
            {
                var taskList = Tasks.ToList();
                taskList.Add(taskType);
                Tasks = taskList;
            }
        }

        private async Task ExecuteTasksForAccount(AccountSettings account, CancellationToken cancellationToken, bool runStartup = true)
        {
            try
            {
                // Use TaskSequence if populated, otherwise use EnabledTasks with proper ordering
                IList<TaskType> tasksToRun;
                if (account.TaskSequence.Any())
                {
                    tasksToRun = account.TaskSequence;
                }
                else
                {
                    tasksToRun = GetProperlyOrderedTasks(account.EnabledTasks.Where(t => account.HiddenTasks?.Contains(t) != true), runStartup, account.InstanceNumber).ToList();
                }

                if (!tasksToRun.Any())
                {
                    _logger.LogWarning($"[{account.AccountName}] No tasks to run - both TaskSequence and EnabledTasks are empty!");
                    return;
                }

                // A new task list run: every enabled character gets its turn again (ChangeAccount restarts happen inside the loop below)
                Tasks.Modules.ChangeAccountTask.ClearInstanceTracking(account.InstanceNumber);

                _logger.LogInfo($"[{account.AccountName}] Executing {tasksToRun.Count} tasks: {string.Join(", ", tasksToRun)}");
                _userNotifications?.ShowStatus($"Starting {tasksToRun.Count} tasks for {account.AccountName}: {string.Join(", ", tasksToRun)}");

                var run = new TaskListRun(tasksToRun);
                for (run.Index = 0; run.Index < run.Tasks.Count; run.Index++)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    var taskType = run.Tasks[run.Index];
                    if (IsTaskSkipped(account, taskType)) continue;

                    var task = GetTaskInstance(taskType);
                    if (task == null)
                    {
                        _logger.LogError($"[{account.AccountName}] ❌ Task {taskType} is enabled but not registered in TaskManager! Skipping.");
                        continue;
                    }

                    if (!await RunTaskStepAsync(account, task, taskType, run, cancellationToken)) break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"[{account.AccountName}] Error in task execution loop: {ex.Message}");
            }
        }

        private static bool IsTaskSkipped(AccountSettings account, TaskType taskType)
        {
            // Skip disabled tasks (only check if we're using TaskSequence, but never skip system tasks)
            var systemTasks = new[] { TaskType.Startup, TaskType.Recovery, TaskType.AccountDetection };
            if (account.TaskSequence.Any() && !account.EnabledTasks.Contains(taskType) && !systemTasks.Contains(taskType)) return true;
            return account.HiddenTasks?.Contains(taskType) == true; // hidden under Settings > Tasks: never runs
        }

        /// <summary>Runs one task of the list and handles its result. False: stop the task list.</summary>
        private async Task<bool> RunTaskStepAsync(AccountSettings account, ITask task, TaskType taskType, TaskListRun run, CancellationToken cancellationToken)
        {
            _logger.LogInfo($"[{account.AccountName}] Starting task: {taskType}");
            ShowRunningStatus(account, taskType);
            try
            {
                // Add timeout for each task
                using var taskCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                taskCts.CancelAfter(TimeSpan.FromMinutes(15)); // 15 minute timeout per task

                var taskStartTime = DateTime.UtcNow;
                TaskRunHistory.SetCurrent(account.InstanceNumber, taskType);
                var result = await task.ExecuteAsync(account, _logger, taskCts.Token, false, _userNotifications);
                TaskRunHistory.SetCurrent(account.InstanceNumber, null);
                var taskDuration = DateTime.UtcNow - taskStartTime;
                RecordResult(account, taskType, result);

                LogTaskDuration(account, taskType, taskDuration);
                return await HandleTaskResultAsync(account, task, taskType, result, run, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInfo($"[{account.AccountName}] Task {taskType} cancelled by user");
                return false;
            }
            catch (OperationCanceledException)
            {
                _logger.LogError($"[{account.AccountName}] Task {taskType} timed out after 15 minutes");
                // Try recovery after timeout
                await TryRecoveryAfterFailureAsync(account, taskType, cancellationToken);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[{account.AccountName}] Error executing task {taskType}: {ex.Message}");
                // Try recovery after any unhandled exception
                await TryRecoveryAfterFailureAsync(account, taskType, cancellationToken);
                return false;
            }
        }

        private async Task TryRecoveryAfterFailureAsync(AccountSettings account, TaskType taskType, CancellationToken cancellationToken)
        {
            var recoveryTask = GetTaskInstance(TaskType.Recovery) as RecoveryTask;
            if (recoveryTask != null)
            {
                recoveryTask.SetLastFailedTask(taskType);
                await recoveryTask.ExecuteAsync(account, _logger, cancellationToken, false, _userNotifications);
            }
        }

        private void ShowRunningStatus(AccountSettings account, TaskType taskType)
        {
            // Show task-specific status for AutoShield
            var shieldTimeRemaining = taskType == TaskType.AutoShield ? ShieldTimeManager.GetRemainingTime(account.InstanceNumber) : null;
            if (shieldTimeRemaining.HasValue && shieldTimeRemaining.Value.TotalSeconds > 0)
            {
                var shieldText = shieldTimeRemaining.Value.TotalHours >= 1
                    ? $"{shieldTimeRemaining.Value.TotalHours:F1}h"
                    : $"{shieldTimeRemaining.Value.TotalMinutes:F0}m";
                _userNotifications?.ShowStatus($"Running {taskType} for {account.AccountName} (Shield: {shieldText})");
            }
            else
            {
                _userNotifications?.ShowStatus($"Running {taskType} for {account.AccountName}");
            }
        }

        private void LogTaskDuration(AccountSettings account, TaskType taskType, TimeSpan taskDuration)
        {
            // Log performance warning for tasks that take longer than 5 minutes
            if (taskDuration.TotalMinutes > 5)
            {
                _logger.LogWarning($"[{account.AccountName}] Performance Warning: Task {taskType} took {taskDuration.TotalMinutes:F1} minutes to complete");
            }
            else
            {
                _logger.LogInfo($"[{account.AccountName}] Task {taskType} completed in {taskDuration.TotalSeconds:F1} seconds");
            }
        }

        private async Task<bool> HandleTaskResultAsync(
            AccountSettings account, ITask task, TaskType taskType, TaskExecutionDetails result, TaskListRun run, CancellationToken cancellationToken)
        {
            if (!result.Success)
            {
                return await HandleFailedTaskAsync(account, task, taskType, result, run, cancellationToken);
            }

            if (result.RequiresTaskRestart && run.TaskRestartCount < MaxTaskRestarts)
            {
                run.TaskRestartCount++;
                _logger.LogInfo($"[{account.AccountName}] Task {taskType} requested task restart. Restarting from loading screen...");

                // Wait for loading screen to appear and disappear
                var startupTask = GetTaskInstance(TaskType.Startup) as StartupTask;
                if (startupTask != null)
                {
                    await startupTask.WaitForLoadingToComplete(account, _logger, cancellationToken);
                }

                // Reset task index to start after Startup task
                run.Index = -1; // Will be incremented to 0 in next loop iteration
                return true;
            }

            // Include task-specific message if provided
            var successMessage = !string.IsNullOrEmpty(result.Message)
                ? $"Task {taskType} completed for {account.AccountName}: {result.Message}"
                : $"Task {taskType} completed successfully for {account.AccountName}";
            _userNotifications?.ShowSuccess(successMessage);
            return true;
        }

        private async Task<bool> HandleFailedTaskAsync(
            AccountSettings account, ITask task, TaskType taskType, TaskExecutionDetails result, TaskListRun run, CancellationToken cancellationToken)
        {
            // Check for special "SKIP_AND_RETRY_LATER" message from farming task
            if (result.Message == "SKIP_AND_RETRY_LATER")
            {
                await HandleSkipAndRetryLaterAsync(account, taskType, run, cancellationToken);
                return true; // Skip to next task
            }

            // Check if this is just a "cannot execute" case (like ChangeAccount already executed)
            if (result.Message == "Task cannot execute")
            {
                _logger.LogInfo($"[{account.AccountName}] Task {taskType} skipped: {result.Message}");
                // Don't show error notification for expected skips
            }
            else
            {
                // Use enhanced error message if available
                var userMessage = result.FailureCategory.HasValue
                    ? result.GetUserMessage()
                    : result.Message;
                _logger.LogWarning($"[{account.AccountName}] Task {taskType} failed: {result.Message}");
                _userNotifications?.ShowError(
                    $"Task {taskType} failed for {account.AccountName}: {userMessage}",
                    result.RecoveryNeeded,
                    result.TroubleshootingHint);
            }

            // If task needs recovery, try recovery before continuing
            return !result.RecoveryNeeded || await RecoverAndRetryTaskAsync(account, task, taskType, cancellationToken);
        }

        /// <summary>Runs the recovery task and retries the failed task once. False: recovery or the retry failed, stop the task list.</summary>
        private async Task<bool> RecoverAndRetryTaskAsync(AccountSettings account, ITask task, TaskType taskType, CancellationToken cancellationToken)
        {
            _logger.LogInfo($"[{account.AccountName}] Task {taskType} requested recovery, attempting recovery...");
            _userNotifications?.ShowStatus($"Attempting recovery for {taskType} on {account.AccountName}...");
            var recoveryTask = GetTaskInstance(TaskType.Recovery) as RecoveryTask;
            if (recoveryTask == null)
            {
                return true;
            }

            recoveryTask.SetLastFailedTask(taskType);
            var recoveryResult = await recoveryTask.ExecuteAsync(account, _logger, cancellationToken, false, _userNotifications);
            if (!recoveryResult.Success)
            {
                _logger.LogError($"[{account.AccountName}] Recovery failed, stopping task execution");
                return false;
            }

            // After successful recovery, retry the failed task
            _logger.LogInfo($"[{account.AccountName}] Recovery successful, retrying task {taskType}");
            _userNotifications?.ShowSuccess($"Recovery successful for {account.AccountName}, retrying {taskType}");
            var retryResult = await task.ExecuteAsync(account, _logger, cancellationToken, isReRun: true, _userNotifications);
            if (!retryResult.Success)
            {
                _logger.LogError($"[{account.AccountName}] Task {taskType} failed again after recovery");
                return false;
            }
            return true;
        }

        private async Task HandleSkipAndRetryLaterAsync(AccountSettings account, TaskType taskType, TaskListRun run, CancellationToken cancellationToken)
        {
            if (taskType == TaskType.Farming)
            {
                await DelayFarmingAsync(account, run, cancellationToken);
                return;
            }

            // Handle other tasks that might use SKIP_AND_RETRY_LATER in the future
            _logger.LogInfo($"[{account.AccountName}] Task {taskType} delayed - will retry after other tasks");
            _userNotifications?.ShowStatus($"{taskType} delayed for {account.AccountName}", NotificationType.Warning);
            run.Append(taskType);
        }

        private async Task DelayFarmingAsync(AccountSettings account, TaskListRun run, CancellationToken cancellationToken)
        {
            run.FarmingRetryCount++;
            if (run.FarmingRetryCount >= MaxFarmingRetries)
            {
                _logger.LogWarning($"[{account.AccountName}] Farming task reached maximum retry limit ({MaxFarmingRetries}), skipping for this cycle");
                _userNotifications?.ShowWarning($"Farming skipped for {account.AccountName} - marches still active after {MaxFarmingRetries} attempts");
                return; // Skip farming entirely
            }

            _logger.LogInfo($"[{account.AccountName}] Farming delayed due to active march timers - retry {run.FarmingRetryCount}/{MaxFarmingRetries}");
            _userNotifications?.ShowStatus($"Farming delayed for {account.AccountName} - waiting for marches to return (retry {run.FarmingRetryCount}/{MaxFarmingRetries})", NotificationType.Warning);

            // Check if there are other tasks to run first
            var remainingTasks = run.Tasks.Skip(run.Index + 1).ToList();
            var nonFarmingTasks = remainingTasks.Where(t => t != TaskType.Farming).ToList();

            if (nonFarmingTasks.Any())
            {
                // Add farming to the end of the queue to retry later
                run.Append(TaskType.Farming);
                _logger.LogInfo($"[{account.AccountName}] Added Farming to end of task queue for retry after other tasks");
                return;
            }

            // No other tasks, wait a bit and retry farming
            _logger.LogInfo($"[{account.AccountName}] No other tasks available, waiting 60 seconds before retrying farming");
            _userNotifications?.ShowStatus($"Waiting for marches to return for {account.AccountName} - no other tasks to run", NotificationType.Info);

            if (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
            }

            // Retry farming immediately
            run.Index--; // Retry the current task
        }

        /// <summary>Task run history for the dashboard, keyed like the task gates. Account Detection also ties the account ID to the active character.</summary>
        private static void RecordResult(AccountSettings account, TaskType taskType, TaskExecutionDetails result)
        {
            var accountId = Tasks.Modules.AccountDetectionTask.GetCachedAccountId(account.InstanceNumber);
            if (taskType == TaskType.AccountDetection && result.Success)
                CharacterRoster.LinkAccountId(account, accountId);
            if (taskType is TaskType.Startup or TaskType.Recovery or TaskType.AccountDetection or TaskType.ChangeAccount) return;
            TaskRunHistory.RecordRun(Schedule.AccountKey(accountId, account), taskType, result);
        }

        /// <summary>The registered task object, e.g. to ask an <see cref="IScheduledTask"/> for its next run.</summary>
        public ITask? GetTask(TaskType taskType) => _availableTasks.TryGetValue(taskType, out var task) ? task : null;

        /// <summary>
        /// Runs one task on one character, for the dashboard's play button: startup (skipped quickly when the game is open),
        /// a switch to the character if another one is active, Account Detection, then the task. The instance must be running.
        /// Shares the one-run-per-instance guard with the cycle, so it never runs next to a task list.
        /// </summary>
        public async Task<TaskExecutionDetails> RunSingleTaskAsync(AccountSettings account, TaskType taskType, string? characterName, CancellationToken cancellationToken)
        {
            if (!_activeInstanceRuns.TryAdd(account.InstanceNumber, 0))
                return TaskExecutionDetails.Failed($"Tasks already running on instance {account.InstanceNumber}");

            using var contextLogger = LogService.ForInstance(account.InstanceNumber, account.AccountName);
            try
            {
                contextLogger.LogInfo($"▶ Single run: {taskType}{(characterName != null ? $" on {characterName}" : "")}");
                var earlyResult = await PrepareSingleRunAsync(account, taskType, characterName, contextLogger, cancellationToken);
                if (earlyResult != null) return earlyResult;

                var detection = GetTask(TaskType.AccountDetection)!;
                var detectionResult = await detection.ExecuteAsync(account, _logger, cancellationToken, false, _userNotifications);
                RecordResult(account, TaskType.AccountDetection, detectionResult);

                var task = GetTask(taskType);
                if (task == null) return TaskExecutionDetails.Failed($"{taskType} is not registered");

                return await ExecuteByHandAsync(account, task, taskType, contextLogger, cancellationToken);
            }
            finally
            {
                TaskRunHistory.SetCurrent(account.InstanceNumber, null);
                _activeInstanceRuns.TryRemove(account.InstanceNumber, out _);
            }
        }

        /// <summary>Startup and the switch to the character. Returns the result when the run ends here (failed, or the character scan was all that was asked), otherwise null.</summary>
        private async Task<TaskExecutionDetails?> PrepareSingleRunAsync(
            AccountSettings account, TaskType taskType, string? characterName, LogService contextLogger, CancellationToken cancellationToken)
        {
            if (!await RunStartupAsync(account, contextLogger, cancellationToken))
                return TaskExecutionDetails.Failed("Startup failed");

            // Change Account on its own: only scan the Characters screen (the dashboard's character list)
            if (taskType == TaskType.ChangeAccount && GetTask(TaskType.ChangeAccount) is Tasks.Modules.ChangeAccountTask scanner)
            {
                return await ScanCharactersAsync(account, scanner, contextLogger, cancellationToken);
            }

            if (characterName != null && GetTask(TaskType.ChangeAccount) is Tasks.Modules.ChangeAccountTask changeAccount)
            {
                return await SwitchToCharacterAsync(account, changeAccount, characterName, contextLogger, cancellationToken);
            }
            return null;
        }

        private async Task<TaskExecutionDetails> ExecuteByHandAsync(
            AccountSettings account, ITask task, TaskType taskType, LogService contextLogger, CancellationToken cancellationToken)
        {
            // A run by hand ignores the task's own timer or daily gate ("Ran 3 min ago"): that is what the button is for
            if (task is IScheduledTask scheduled)
            {
                ClearTaskSchedule(account, scheduled, taskType, contextLogger);
            }

            _userNotifications?.ShowStatus($"Running {taskType} for {account.AccountName}");
            using var taskCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            taskCts.CancelAfter(TimeSpan.FromMinutes(15));
            TaskRunHistory.SetCurrent(account.InstanceNumber, taskType);
            BaseTaskWithCommonPatterns.RunByHand.Value = true;  // flows into this run only
            var result = await task.ExecuteAsync(account, _logger, taskCts.Token, false, _userNotifications);
            RecordResult(account, taskType, result);
            contextLogger.LogInfo($"▶ Single run of {taskType} done: {(result.Success ? "ok" : "failed")} {result.Message}");
            return result;
        }

        private async Task<TaskExecutionDetails> ScanCharactersAsync(
            AccountSettings account, Tasks.Modules.ChangeAccountTask scanner, LogService contextLogger, CancellationToken cancellationToken)
        {
            var scanResult = await scanner.ScanAsync(account, contextLogger, cancellationToken);
            contextLogger.LogInfo($"Characters: {scanResult.Message}");
            if (scanResult.Success)
                RecordResult(account, TaskType.AccountDetection,
                    await GetTask(TaskType.AccountDetection)!.ExecuteAsync(account, _logger, cancellationToken, false, _userNotifications));
            return scanResult;
        }

        /// <summary>Switches to the named character; returns the failure, or null when the character is active.</summary>
        private async Task<TaskExecutionDetails?> SwitchToCharacterAsync(
            AccountSettings account, Tasks.Modules.ChangeAccountTask changeAccount, string characterName, LogService contextLogger, CancellationToken cancellationToken)
        {
            var switchResult = await changeAccount.SwitchToCharacterAsync(account, contextLogger, characterName, cancellationToken);
            contextLogger.LogInfo($"Character: {switchResult.Message}");
            if (!switchResult.Success) return switchResult;
            if (switchResult.RequiresTaskRestart && _startupTask is StartupTask startup)
                await startup.WaitForLoadingToComplete(account, contextLogger, cancellationToken);
            return null;
        }

        private static void ClearTaskSchedule(AccountSettings account, IScheduledTask scheduled, TaskType taskType, LogService contextLogger)
        {
            var accountKey = Schedule.AccountKey(Tasks.Modules.AccountDetectionTask.GetCachedAccountId(account.InstanceNumber), account);
            scheduled.ClearSchedule(accountKey);
            contextLogger.LogInfo($"▶ {taskType}: timer cleared for {accountKey}, running regardless");
        }

        public void ResetStartupStatus()
        {
            _instanceStartupComplete.Clear();
            _logger.LogInfo("All instance startup statuses have been reset.");
        }


        public void SetAccounts(IEnumerable<AccountSettings> accounts)
        {
            _accounts.Clear();
            _accounts.AddRange(accounts);
        }

        private ITask? GetTaskInstance(TaskType taskType)
        {
            if (_availableTasks.TryGetValue(taskType, out var task))
            {
                return task;
            }
            return null;
        }

        // Task execution order (lower runs first)
        private static readonly Dictionary<TaskType, int> TaskPriority = new Dictionary<TaskType, int>
        {
            { TaskType.AccountDetection, -1 }, // AccountDetection must run first (after Startup)
            { TaskType.AutoShield, 0 },        // AutoShield should run first to ensure protection
            { TaskType.Journey, 0 },           // early: the Idle Journey only runs while the game stays on this character
            { TaskType.CollectOnlineRewards, 1 }, // before AutoHunt: the online rewards include stamina for the intel missions
            { TaskType.PetSkill, 1 },          // before AutoHunt: Comforting Embrace gives 50 stamina
            { TaskType.AutoHunt, 2 },          // AutoHunt should run after shield
            { TaskType.Farming, 3 },           // Farming should run after AutoHunt
            { TaskType.AutoAllianceHelp, 4 },
            { TaskType.TroopTraining, 5 },
            { TaskType.ConquestCollect, 6 },
            { TaskType.ClaimMail, 7 },
            { TaskType.AutoHeal, 8 },
            { TaskType.AutoBuild, 9 },
            { TaskType.AutoClaimHero, 10 },    // Add AutoClaimHero to the priority list
            { TaskType.AllianceTechnology, 11 },
            { TaskType.AutoTechnology, 12 },   // Auto Technology after AllianceTechnology
            { TaskType.CollectVip, 13 },
            { TaskType.ClaimMissions, 14 },
            { TaskType.SuggestionBox, 14 },    // before ResidentWelcome: its zoom-out shrinks the envelope bubble
            { TaskType.ResidentWelcome, 15 },
            { TaskType.CollectRewards, 16 },
            { TaskType.PetAdventure, 17 },
            { TaskType.TruegoldCrucible, 19 },
            { TaskType.AllianceTriumph, 20 },
            { TaskType.WaterEssence, 21 },
            { TaskType.NomadicMerchant, 22 },
            { TaskType.AllianceChests, 23 },
            { TaskType.AllianceMobilization, 24 },
            { TaskType.MysticTrial, 25 },
            { TaskType.Arena, 25 },
            { TaskType.AutoJoinRally, 26 },
            { TaskType.AutoRally, 26 },
            { TaskType.ChangeAccount, 999 }    // ChangeAccount should always run last
        };

        /// <summary>Position in the cycle's task list (lower runs first); tasks not in the list run just before Change Account.</summary>
        public static int GetRunOrder(TaskType task) => TaskPriority.TryGetValue(task, out var order) ? order : 998;

        private IEnumerable<TaskType> GetProperlyOrderedTasks(IEnumerable<TaskType> enabledTasks, bool runStartup = true, int instanceNumber = -1)
        {
            var taskList = enabledTasks.ToList();
            
            // Auto-enable AccountDetection as a system task only on first run/startup
            // On subsequent cycles for persistent accounts, only run if account ID is not cached
            bool shouldAddAccountDetection = false;
            if (!taskList.Contains(TaskType.AccountDetection))
            {
                if (runStartup)
                {
                    // First run - always add AccountDetection
                    shouldAddAccountDetection = true;
                    _logger.LogInfo("Auto-enabled AccountDetection system task (first run)");
                }
                else
                {
                    // Subsequent cycle - only add if no cached account ID exists for this instance
                    // This handles cases where the account might have changed manually
                    var cachedId = Tasks.Modules.AccountDetectionTask.GetCachedAccountId(instanceNumber);
                    if (string.IsNullOrEmpty(cachedId))
                    {
                        shouldAddAccountDetection = true;
                        _logger.LogInfo("Auto-enabled AccountDetection system task (no cached ID found)");
                    }
                    else
                    {
                        _logger.LogInfo("Skipping AccountDetection - account ID already cached for subsequent cycle");
                    }
                }
                
                if (shouldAddAccountDetection)
                {
                    taskList.Insert(0, TaskType.AccountDetection);
                }
            }
            

            // Filter out system tasks (but keep AccountDetection since it's auto-enabled above) and sort by priority
            var ordered = taskList
                .Where(task => task != TaskType.Startup && task != TaskType.Recovery)
                .OrderBy(task => GetRunOrder(task))
                .ToList();

            // ChangeAccount also runs first: it switches away from a disabled character before any task runs on it
            if (ordered.Contains(TaskType.ChangeAccount))
            {
                ordered.Insert(0, TaskType.ChangeAccount);
            }
            return ordered;
        }
    }

} 