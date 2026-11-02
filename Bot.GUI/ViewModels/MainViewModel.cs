using System.ComponentModel;
using System.Runtime.CompilerServices;
using Bot.Core.LDPlayer;
using Bot.Core.Logging;
using Bot.Core.Config;
using Bot.Core.Models;
using Bot.Core.Tasks;
using Bot.Core.Services;
using Bot.Core.Utils;
using System.Linq;
using System.Windows.Input;
using System;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using System.IO;

namespace Bot.GUI.ViewModels
{
    public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
    {
        // Singleton instance to prevent multiple service initializations
        private static MainViewModel? _instance;
        private static readonly object _lock = new object();
        
        public InstanceViewModel InstanceVM { get; }
        public ConfigViewModel ConfigVM { get; }

        private string _currentLogMessage = string.Empty;
        public string CurrentLogMessage
        {
            get => _currentLogMessage;
            set { 
                _currentLogMessage = value; 
                OnPropertyChanged(); 
            }
        }

        public ICommand StopCommand { get; }
        public ICommand LaunchBotCommand { get; }
        public ICommand PauseResumeCommand { get; }
        
        private bool _isBotPaused = false;
        public bool IsBotPaused
        {
            get => _isBotPaused;
            set { 
                _isBotPaused = value; 
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(PauseResumeButtonText));
            }
        }
        
        public string PauseResumeButtonText => _isBotPaused ? "▶  Resume" : "⏸  Pause";
        
        // Check if bot is running from either cycle management or individual instances
        public bool IsBotRunning 
        { 
            get 
            { 
                lock (_individualInstanceLock)
                {
                    return _cycleService.IsRunning || _hasRunningIndividualInstances;
                }
            } 
        }

        private readonly InstanceManager _instanceManager;
        private readonly GuiLogService _logger;
        private readonly TaskManager _taskManager;
        private readonly CycleManagementService _cycleService;
        private readonly IUserNotificationService _userNotifications;
        private BotConfig? _currentConfig;
        private CancellationTokenSource _botCancellationTokenSource;
        private bool _isStoppingEverything = false;
        
        // Track individual instance execution state
        private volatile bool _hasRunningIndividualInstances = false;
        private bool _autoLaunchDone; // the --launch argument fires only once per app start
        private readonly object _individualInstanceLock = new object();

        public static MainViewModel Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new MainViewModel();
                        }
                    }
                }
                return _instance;
            }
        }

        private MainViewModel()
        {
            _botCancellationTokenSource = new CancellationTokenSource();
            _logger = new GuiLogService(AppendLog);
            _userNotifications = new UserNotificationService(_logger, _logger);
            CurrentLogMessage = "";
            
            try
            {
                _userNotifications.ShowStatus("Initializing KingshotAuto...", NotificationType.Info);
                
                // LogService.PurgeAllLogs(); // Disabled to prevent file access errors
                
                _userNotifications.ShowStatus("Searching for LDPlayer installation...", NotificationType.Info);
                var (ldConsolePath, dnConsolePath) = FindLdPlayerConsolePaths();

                _userNotifications.ShowStatus("Setting up services...", NotificationType.Info);
                
                var services = new ServiceCollection();
                var serviceProvider = services.BuildServiceProvider();
                
                if (ldConsolePath != null && dnConsolePath != null)
                {
                    _instanceManager = new InstanceManager(ldConsolePath, dnConsolePath, _logger);
                    _logger.LogInfo($"Found LDPlayer at: {ldConsolePath}", category: LogCategories.System);
                }
                else
                {
                    // Create a dummy instance manager that will handle the not-found case
                    _instanceManager = new InstanceManager("", "", _logger);
                }

                _taskManager = new TaskManager(_logger, serviceProvider, _userNotifications);
                _cycleService = new CycleManagementService(_logger, _taskManager);
                
                _cycleService.OnCycleStarted += OnCycleStarted;
                _cycleService.OnCycleCompleted += OnCycleCompleted;
                _cycleService.OnWaitingBetweenCycles += OnWaitingBetweenCycles;
                _cycleService.OnStatusUpdate += OnCycleStatusUpdate;
                
                _userNotifications.ShowStatus("Loading configuration...", NotificationType.Info);
                _currentConfig = BotConfig.LoadFromFile("configs/default_config.json", _logger);
                // Create a default account if none exists
                if (_currentConfig.Accounts.Count == 0)
                {
                    _currentConfig.Accounts.Add(new AccountSettings { AccountName = "Default Account", InstanceNumber = 0 });
                }
                InstanceVM = new InstanceViewModel(_instanceManager, this, _currentConfig.Accounts[0]);
                ConfigVM = new ConfigViewModel(_currentConfig, _logger);
                
                // Subscribe to configuration changes to refresh instance list
                ConfigVM.ConfigurationChanged += InitializeGuiFromConfig;
                
                _userNotifications.ShowSuccess("KingshotAuto initialized successfully! Ready to use.");
                
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in MainViewModel constructor: {ex}", category: LogCategories.System);
                _userNotifications.ShowError($"Failed to initialize application: {ex.Message}");

                throw; // Rethrow to prevent partial initialization
            }
            
            _ = Task.Run(InitializeTaskManagerAsync, CancellationToken.None);

            InitializeGuiFromConfig();

            StopCommand = new AsyncRelayCommand(_ => ExecuteStopCommandAsync());

            LaunchBotCommand = new AsyncRelayCommand(_ => LaunchCycle(runOnce: false));
            RunOnceCommand = new AsyncRelayCommand(_ => LaunchCycle(runOnce: true));

            PauseResumeCommand = new AsyncRelayCommand(_ => ExecutePauseResumeCommandAsync());

            Dashboard = new TaskDashboardViewModel(this, _cycleService, _taskManager, ConfigVM);
        }

        private (string? ldConsolePath, string? dnConsolePath) FindLdPlayerConsolePaths()
        {
            try
            {
                var ldConsolePath = LDPlayerHelper.GetLDPlayerConsolePath();
                var dnConsolePath = LDPlayerHelper.GetDNPlayerConsolePath();
                _userNotifications.ShowSuccess("LDPlayer found and ready");
                return (ldConsolePath, dnConsolePath);
            }
            catch (FileNotFoundException ex)
            {
                _userNotifications.ShowError("LDPlayer not found on this system!",
                    troubleshootingHint: "You can manually set the LDPlayer path in the settings if auto-detection fails.");
                _logger.LogError($"LDPlayer not found: {ex.Message}", category: LogCategories.System);
                return (null, null);
            }
        }

        private async Task InitializeTaskManagerAsync()
        {
            try
            {
                await _taskManager.InitializeAsync(CancellationToken.None);
                _logger.LogInfo("TaskManager initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error initializing TaskManager: {ex.Message}");
            }
        }

        private async Task ExecuteStopCommandAsync()
        {
            _logger.LogInfo("StopCommand executed");
            try { await StopEverythingAsync(); }
            catch (Exception ex) { _logger.LogError($"Error in StopCommand: {ex}"); }
        }

        private async Task ExecutePauseResumeCommandAsync()
        {
            _logger.LogInfo("PauseResumeCommand executed");
            try {
                if (!IsBotRunning)
                {
                    _userNotifications.ShowWarning("Cannot pause/resume - bot is not running", "Please start the bot first");
                    return;
                }

                if (CycleManagementService.IsPaused)
                {
                    await _cycleService.ResumeAsync();
                    IsBotPaused = false;
                    _userNotifications.ShowSuccess("Bot resumed successfully");
                    UpdateInstanceStatusToPausedOrRunning(false);
                }
                else
                {
                    await _cycleService.PauseAsync();
                    IsBotPaused = true;
                    _userNotifications.ShowStatus("Bot paused - it stops at its next tap", NotificationType.Info);
                    UpdateInstanceStatusToPausedOrRunning(true);
                }
            } catch (Exception ex) {
                _logger.LogError($"Error in PauseResumeCommand: {ex}");
                _userNotifications.ShowError($"Failed to pause/resume bot: {ex.Message}");
            }
        }

        public TaskDashboardViewModel Dashboard { get; }
        public ICommand RunOnceCommand { get; }

        /// <summary>Starts the cycle service: repeating cycles, or a single cycle (Run once).</summary>
        private async Task LaunchCycle(bool runOnce)
        {
                _logger.LogInfo($"LaunchBotCommand executed{(runOnce ? " (run once)" : "")}");
                try {
                    // Clean up any existing ADB connections before starting
                    await AdbConnectionManager.CloseAllConnections(_logger);
                    
                    _currentConfig = ConfigVM.GetCurrentBotConfig();
                    if (_currentConfig == null)
                    {
                        _logger.LogError("LaunchBotCommand: Current config is null (from ConfigVM). Cannot start.");
                        _userNotifications.ShowError("Configuration not available", troubleshootingHint: "Cannot start bots without proper configuration.");
                        return;
                    }

                    if (_botCancellationTokenSource.IsCancellationRequested)
                    {
                        _botCancellationTokenSource.Dispose();
                        _botCancellationTokenSource = new CancellationTokenSource();
                    }

                    _logger.LogInfo("LaunchBotCommand: Starting cycle service (cycle mode is always enabled).");
                    _userNotifications.ShowStatus("Launching cycle management...", NotificationType.Info);
                    
                    _userNotifications.ShowStatus($"Cycle settings: {_currentConfig.CycleManagement.MinWaitTimeBetweenCyclesMinutes}-{_currentConfig.CycleManagement.MaxWaitTimeBetweenCyclesMinutes}min intervals, shutdown: {_currentConfig.CycleManagement.ShutdownEmulatorsAfterCycle}", NotificationType.Info);
                    
                    _currentConfig.CycleManagement.MaxCycles = runOnce ? 1 : 0;
                    _ = Task.Run(async () => await _cycleService.StartCycleAsync(_currentConfig, _botCancellationTokenSource.Token), _botCancellationTokenSource.Token);
                }
                catch (Exception ex) {
                    _logger.LogError($"Error in LaunchBotCommand: {ex}", category: LogCategories.System);
                    _userNotifications.ShowError($"Failed to launch bots: {ex.Message}");
                }
        }

        /// <summary>Dashboard play button: one task on one character. Stop cancels it like a cycle.</summary>
        public async Task<TaskExecutionDetails> RunSingleTaskAsync(AccountSettings account, TaskType taskType, string? characterName)
        {
            if (IsBotRunning) return TaskExecutionDetails.Failed("The bot is already running");
            if (_botCancellationTokenSource.IsCancellationRequested)
            {
                _botCancellationTokenSource.Dispose();
                _botCancellationTokenSource = new CancellationTokenSource();
            }

            SetIndividualInstanceRunning(true);
            try
            {
                return await Task.Run(() => _cycleService.RunSingleTaskAsync(account, taskType, characterName, _botCancellationTokenSource.Token), _botCancellationTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                return TaskExecutionDetails.Failed("Cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Single run of {taskType} failed: {ex}", category: LogCategories.TaskExecution);
                return TaskExecutionDetails.Failed(ex.Message);
            }
            finally
            {
                SetIndividualInstanceRunning(false);
            }
        }

        private DateTime _lastInitializeGuiCall = DateTime.MinValue;
        private const int INITIALIZE_GUI_DEBOUNCE_MS = 100;

        public void InitializeGuiFromConfig() 
        {
            // Debounce rapid successive calls to prevent infinite loops
            var now = DateTime.Now;
            if ((now - _lastInitializeGuiCall).TotalMilliseconds < INITIALIZE_GUI_DEBOUNCE_MS)
            {
                _logger.LogInfo("InitializeGuiFromConfig call debounced");
                return;
            }
            _lastInitializeGuiCall = now;

            _logger.LogInfo("InitializeGuiFromConfig called");
            try {
                // Set global flag to prevent config saves during reload
                ConfigViewModel.SetGlobalConfigReloading(true);
                
                // Get current config using the same method but without triggering a reload
                _currentConfig = ConfigVM.GetCurrentBotConfig();
                if (_currentConfig == null) {
                    _userNotifications.ShowWarning("Configuration issue", "GUI might not reflect current settings.");
                    return;
                }
                
                _logger.LogInfo($"Loaded config with {_currentConfig.Accounts.Count} accounts for GUI instance display.");

                InstanceVM.Instances.Clear();
                _logger.LogInfo("Cleared InstanceVM.Instances");
                foreach (var acc in _currentConfig.Accounts)
                {
                    var instanceItem = new InstanceItemViewModel(acc, _instanceManager, this);
                    instanceItem.Status = BotRuntimeStatus.Stopped;
                    InstanceVM.Instances.Add(instanceItem);
                    _logger.LogInfo($"Adding instance to GUI: {acc.AccountName} (Instance {acc.InstanceNumber})");
                }
                _logger.LogInfo($"InstanceVM.Instances now has {InstanceVM.Instances.Count} items");

                AutoLaunchFromCommandLine(_currentConfig);

            } catch (Exception ex) {
                _logger.LogError($"Error in InitializeGuiFromConfig: {ex}", category: LogCategories.Configuration);
                _userNotifications.ShowError($"Failed to load configuration: {ex.Message}");
            }
            finally {
                // Clear global flag after reload is complete
                ConfigViewModel.SetGlobalConfigReloading(false);
            }
        }

        private static bool HasArg(string[] args, string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);

        private static string? ArgValue(string[] args, string name)
        {
            int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        /// <summary>Command line switches for unattended runs: --schedule / --cycle / --once, --task, --launch. Only the first one that applies runs.</summary>
        private void AutoLaunchFromCommandLine(BotConfig config)
        {
            if (_autoLaunchDone) return;

            var args = Environment.GetCommandLineArgs();
            _autoLaunchDone = TryAutoStartCycle(args) || TryAutoRunTask(args, config) || TryAutoLaunchInstances(args);
        }

        // "--schedule" (or the older "--cycle") presses Run on schedule, "--once" presses Run once, for unattended runs
        private bool TryAutoStartCycle(string[] args)
        {
            if (!(HasArg(args, "--schedule") || HasArg(args, "--cycle") || HasArg(args, "--once")))
            {
                return false;
            }

            var runOnce = HasArg(args, "--once");
            _logger.LogInfo(runOnce ? "Auto-starting one cycle (--once)" : "Auto-starting the cycle service on schedule (--schedule)");
            // The first call comes from the constructor, before the commands exist: run it once that has finished
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                (runOnce ? RunOnceCommand : LaunchBotCommand)?.Execute(null));
            return true;
        }

        // "--task <TaskType> [--character <name>]" presses ▶ for one task on the first instance, e.g. --task MysticTrial --character llsa
        private bool TryAutoRunTask(string[] args, BotConfig config)
        {
            if (ArgValue(args, "--task") is not string taskArg || config.Accounts.FirstOrDefault() is not AccountSettings taskAccount)
            {
                return false;
            }

            AutoRunSingleTask(args, taskArg, taskAccount);
            return true;
        }

        // "--launch" on the command line presses Launch on every instance once, for unattended runs
        private bool TryAutoLaunchInstances(string[] args)
        {
            if (!HasArg(args, "--launch"))
            {
                return false;
            }

            foreach (var instance in InstanceVM.Instances)
            {
                _logger.LogInfo($"Auto-launching instance {instance.InstanceNumber} (--launch)");
                instance.StartInstanceCommand.Execute(null);
            }
            return true;
        }

        private void AutoRunSingleTask(string[] args, string taskArg, AccountSettings taskAccount)
        {
            if (!Enum.TryParse<TaskType>(taskArg, true, out var taskType))
            {
                _logger.LogWarning($"--task: unknown task '{taskArg}'");
                return;
            }

            var characterArg = ArgValue(args, "--character");
            var character = characterArg == null ? null
                : taskAccount.Characters.FirstOrDefault(c => CharacterRoster.SameName(c.Name, characterArg))?.Name ?? characterArg;
            _logger.LogInfo($"Auto-running {taskType}{(character != null ? $" on {character}" : "")} (--task)");
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(async () =>
                await RunSingleTaskAsync(taskAccount, taskType, character));
        }

        public void AppendLog(string message)
        {
            // This method now handles user notifications from the UserNotificationService
            // Pass the formatted message to the GUI
            // Ensure thread safety for UI property updates
            if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == true)
            {
                CurrentLogMessage = message;
                ParseAndUpdateCurrentTask(message);
            }
            else
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    CurrentLogMessage = message;
                    ParseAndUpdateCurrentTask(message);
                });
            }
        }
        
        private void ParseAndUpdateCurrentTask(string message)
        {
            try
            {
                // Parse LDPlayer status messages first (higher priority for status determination)
                if (message.Contains("Parsed status: Instance "))
                {
                    ParseLDPlayerStatus(message);
                    return;
                }
                
                // Parse USER notification format: |USER|NotificationType|timestamp|message
                if (message.StartsWith("|USER|"))
                {
                    var parts = message.Split('|');
                    if (parts.Length >= 4)
                    {
                        var actualMessage = string.Join("|", parts.Skip(3)); // Rejoin in case message contains |

                        if (ShowCycleWaitOnInstances(actualMessage))
                        {
                            return;
                        }

                        var (taskType, accountName) = ParseTaskAnnouncement(actualMessage);

                        // Update instance if we found task and account info
                        if (!string.IsNullOrEmpty(taskType) && !string.IsNullOrEmpty(accountName))
                        {
                            UpdateInstanceTask(taskType, accountName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Silently ignore parsing errors to avoid disrupting the logging flow
                _logger.LogWarning($"Failed to parse task status from message: {ex.Message}", category: LogCategories.UserAction);
            }
        }

        /// <summary>"Waiting {n} minutes before next cycle": the cycle is over, so the last task shown is finished. True when the message was one.</summary>
        private bool ShowCycleWaitOnInstances(string actualMessage)
        {
            var waitMatch = System.Text.RegularExpressions.Regex.Match(actualMessage, @"Waiting ([\d.,]+) minutes before next cycle", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
            if (!waitMatch.Success)
            {
                return false;
            }

            var minutes = (int)Math.Round(double.Parse(waitMatch.Groups[1].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture));
            foreach (var inst in InstanceVM.Instances)
                inst.LastTask = $"Done, next cycle in {minutes} min";
            return true;
        }

        private static (string? taskType, string? accountName) ParseTaskAnnouncement(string actualMessage)
        {
            // Pattern 1: "Running {taskType} for {accountName}"
            if (actualMessage.Contains("Running ") && actualMessage.Contains(" for "))
            {
                return ParseRunningMessage(actualMessage);
            }
            // Pattern 2: "Starting {count} tasks for {accountName}: {taskList}"
            if (actualMessage.Contains("Starting ") && actualMessage.Contains(" tasks for "))
            {
                return ParseStartingTasksMessage(actualMessage);
            }
            // Pattern 3: "Attempting recovery for {taskType} on {accountName}..."
            if (actualMessage.Contains("Attempting recovery for ") && actualMessage.Contains(" on "))
            {
                return ParseRecoveryMessage(actualMessage);
            }
            return (null, null);
        }

        private static (string? taskType, string? accountName) ParseRunningMessage(string actualMessage)
        {
            var runningIndex = actualMessage.IndexOf("Running ");
            var forIndex = actualMessage.IndexOf(" for ", runningIndex);

            if (runningIndex < 0 || forIndex <= runningIndex)
            {
                return (null, null);
            }

            var taskType = actualMessage.Substring(runningIndex + 8, forIndex - runningIndex - 8).Trim();
            var remainingText = actualMessage.Substring(forIndex + 5);

            // Extract account name (everything before " (Shield:" if present, or entire remaining text)
            var accountName = remainingText;
            var shieldIndex = remainingText.IndexOf(" (Shield:");
            if (shieldIndex > 0)
            {
                accountName = remainingText.Substring(0, shieldIndex).Trim();
            }
            return (taskType, accountName);
        }

        private static (string? taskType, string? accountName) ParseStartingTasksMessage(string actualMessage)
        {
            var forIndex = actualMessage.IndexOf(" tasks for ");
            var colonIndex = actualMessage.IndexOf(": ", forIndex);

            if (forIndex <= 0)
            {
                return (null, null);
            }

            var startIndex = forIndex + 11; // Length of " tasks for "
            var endIndex = colonIndex > 0 ? colonIndex : actualMessage.Length;
            return ("Starting tasks", actualMessage.Substring(startIndex, endIndex - startIndex).Trim());
        }

        private static (string? taskType, string? accountName) ParseRecoveryMessage(string actualMessage)
        {
            var recoveryIndex = actualMessage.IndexOf("Attempting recovery for ");
            var onIndex = actualMessage.IndexOf(" on ", recoveryIndex);

            if (recoveryIndex < 0 || onIndex <= recoveryIndex)
            {
                return (null, null);
            }

            var taskType = actualMessage.Substring(recoveryIndex + 24, onIndex - recoveryIndex - 24).Trim() + " (Recovery)";
            var remainingText = actualMessage.Substring(onIndex + 4);
            return (taskType, remainingText.Replace("...", "").Trim());
        }

        private void UpdateInstanceTask(string taskType, string accountName)
        {
            var instance = InstanceVM.Instances.FirstOrDefault(i =>
            {
                if (_currentConfig?.Accounts == null) return false;
                var account = _currentConfig.Accounts.FirstOrDefault(a => a.InstanceNumber == i.InstanceNumber);
                return account?.AccountName == accountName;
            });

            if (instance != null)
            {
                // Update task information (status is now handled by LDPlayer status parsing)
                instance.LastTask = taskType;
            }
        }

        private void ParseLDPlayerStatus(string message)
        {
            try
            {
                // Debug: Log the message we're trying to parse
                _logger.LogInfo($"[DEBUG] Parsing LDPlayer status: '{message}'", category: LogCategories.UserAction);
                
                // Parse: "Parsed status: Instance 1 (Tuna): Fully Ready | PID: 758244 | VBox: 755772 | Windows: 3542156/5967260 | Updated: 20:22:19"
                var match = System.Text.RegularExpressions.Regex.Match(message, 
                    @"Parsed status: Instance (\d+) \(([^)]+)\): ([^|]+)", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
                
                if (match.Success)
                {
                    var instanceNumber = int.Parse(match.Groups[1].Value);
                    var accountName = match.Groups[2].Value.Trim();
                    var status = match.Groups[3].Value.Trim();
                    
                    _logger.LogInfo($"[DEBUG] Parsed: Instance {instanceNumber}, Account: '{accountName}', Status: '{status}'", category: LogCategories.UserAction);
                    
                    var instance = InstanceVM.Instances.FirstOrDefault(i => i.InstanceNumber == instanceNumber);
                    if (instance != null)
                    {
                        _logger.LogInfo($"[DEBUG] Found matching instance {instanceNumber}, current status: {instance.Status}", category: LogCategories.UserAction);
                        
                        // Update status based on LDPlayer state
                        if (status == "Fully Ready")
                        {
                            // Reset manual stop flag when LDPlayer shows as running
                            instance.IsManuallyStopped = false;
                            // Set to Running regardless of current status for debugging
                            instance.Status = BotRuntimeStatus.Running;
                            _logger.LogInfo($"[DEBUG] Set instance {instanceNumber} to Running", category: LogCategories.UserAction);
                        }
                        else if (status == "Not Running")
                        {
                            instance.Status = BotRuntimeStatus.Stopped;
                            if (string.IsNullOrEmpty(instance.LastTask) || 
                                instance.LastTask == "Initializing..." || 
                                instance.LastTask.Contains("Starting tasks"))
                            {
                                instance.LastTask = "Idle";
                            }
                            _logger.LogInfo($"[DEBUG] Set instance {instanceNumber} to Stopped", category: LogCategories.UserAction);
                        }
                    }
                    else
                    {
                        _logger.LogWarning($"[DEBUG] Could not find instance {instanceNumber} in GUI", category: LogCategories.UserAction);
                    }
                }
                else
                {
                    _logger.LogWarning($"[DEBUG] Regex did not match message: '{message}'", category: LogCategories.UserAction);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to parse LDPlayer status: {ex.Message}", category: LogCategories.UserAction);
            }
        }

        private async Task RunBotTasksForInstance(InstanceItemViewModel instance, CancellationToken cancellationToken)
        {
            try
            {
                _currentConfig = ConfigVM.GetCurrentBotConfig();
                if (_currentConfig == null)
                {
                    _userNotifications.ShowError($"Cannot run tasks for instance {instance.InstanceNumber}", troubleshootingHint: "Configuration not loaded");
                    return;
                }

                var accountSettings = _currentConfig.Accounts.FirstOrDefault(a => a.InstanceNumber == instance.InstanceNumber);
                if (accountSettings == null)
                {
                    _userNotifications.ShowError($"No account configuration found for instance {instance.InstanceNumber}", troubleshootingHint: "Check your configuration settings");
                    return;
                }
                
                instance.LastTask = "Initializing...";
                _userNotifications.ShowStatus($"Starting bot tasks for {accountSettings.AccountName} (Instance {instance.InstanceNumber})", NotificationType.Info);
                _userNotifications.ShowProgress("Connecting to device", 10, "Initializing ADB connection...");
                
                // Set status to Running before starting tasks
                instance.Status = BotRuntimeStatus.Running;
                _userNotifications.ShowProgress("Bot tasks", 50, "Running automation tasks...");
                var success = await _taskManager.RunTasksForAccountAsync(accountSettings, cancellationToken: cancellationToken);
                
                if (success)
                {
                    instance.LastTask = "All tasks completed";
                    instance.Status = BotRuntimeStatus.Stopped;
                    _userNotifications.ShowSuccess($"Instance {instance.InstanceNumber}: All tasks completed successfully");
                }
                else
                {
                    instance.LastTask = "Tasks failed";
                    instance.Status = BotRuntimeStatus.Stopped;
                    _userNotifications.ShowError($"Instance {instance.InstanceNumber}: Bot tasks failed", 
                        troubleshootingHint: "Check the log files for detailed error information. Common issues: ADB connection, LDPlayer not responding, or app not found.");
                }
            }
            catch (OperationCanceledException)
            {
                instance.LastTask = "Cancelled";
                instance.Status = BotRuntimeStatus.Stopped;
                _userNotifications.ShowWarning($"Instance {instance.InstanceNumber}: Bot tasks cancelled");
                _logger.LogInfo($"Bot tasks cancelled for instance {instance.InstanceNumber}", category: LogCategories.TaskExecution);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error running bot tasks for instance {instance.InstanceNumber}: {ex}", category: LogCategories.TaskExecution);
                instance.LastTask = $"Error: {ex.Message}";
                instance.Status = BotRuntimeStatus.Stopped;
                _userNotifications.ShowError($"Instance {instance.InstanceNumber}: Bot error occurred", 
                    troubleshootingHint: $"Error details: {ex.Message}");
            }
        }

        private async Task StopEverythingAsync()
        {
            // Prevent multiple simultaneous stop attempts
            if (_isStoppingEverything) return;
            _isStoppingEverything = true;

            _logger.LogInfo("StopEverything called", category: LogCategories.UserAction);
            _userNotifications.ShowStatus("Stopping all operations...", NotificationType.Warning);
            
            try {
                // First cancel all ongoing operations
                await _botCancellationTokenSource.CancelAsync();
                _logger.LogInfo("Cancel token triggered", category: LogCategories.System);

                // Reset pause state when stopping
                if (CycleManagementService.IsPaused)
                {
                    _logger.LogInfo("Resetting pause state during stop", category: LogCategories.System);
                    PauseGate.Resume(); // a single run (▶) has no cycle stop to release the waiting tap
                    IsBotPaused = false;
                }

                // Then stop cycle management (run in background)
                if (_cycleService.IsRunning)
                {
                    _logger.LogInfo("Stopping cycle management", category: LogCategories.System);
                    
                    // Use the async version directly
                    await _cycleService.StopCycleAsync();
                }
                
                // Wait a moment for operations to stop
                await Task.Delay(500, CancellationToken.None);
                
                // Close all ADB connections in background
                _logger.LogInfo("Closing all ADB connections", category: LogCategories.ADB);
                await AdbConnectionManager.CloseAllConnections(_logger);

                // Update instance statuses
                if (_instanceManager != null)
                {
                    foreach (var instance in InstanceVM.Instances)
                    {
                        instance.Status = BotRuntimeStatus.Stopped;
                    }
                }
                
                // Reset task statuses
                _logger.LogInfo("Resetting startup status for all instances", category: LogCategories.System);
                _taskManager.ResetStartupStatus();

                // Create new cancellation token source for future operations
                _botCancellationTokenSource.Dispose();
                _botCancellationTokenSource = new CancellationTokenSource();

                _userNotifications.ShowSuccess("All operations stopped successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in StopEverything: {ex}");
                _userNotifications.ShowError("Error stopping bot", troubleshootingHint: ex.Message);
            }
            finally
            {
                _isStoppingEverything = false;
            }
        }

        private void UpdateInstanceStatusToPausedOrRunning(bool isPaused)
        {
            try
            {
                foreach (var instance in InstanceVM.Instances)
                {
                    if (instance.Status == BotRuntimeStatus.Running || instance.Status == BotRuntimeStatus.Paused)
                    {
                        instance.Status = isPaused ? BotRuntimeStatus.Paused : BotRuntimeStatus.Running;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error updating instance statuses: {ex.Message}");
            }
        }

        public async System.Threading.Tasks.Task RunBotTasksForSpecificInstance(InstanceItemViewModel instance)
        {
            try
            {
                if (instance == null)
                {
                    _userNotifications.ShowError("Cannot run tasks", troubleshootingHint: "Instance is null");
                    return;
                }
                
                _logger.LogInfo($"RunBotTasksForSpecificInstance called for {instance.AccountName}");
                
                // Mark individual instance as running
                SetIndividualInstanceRunning(true);
                
                // Get the current config
                _currentConfig = ConfigVM.GetCurrentBotConfig();
                if (_currentConfig == null)
                {
                    _userNotifications.ShowError($"Cannot run tasks for instance {instance.InstanceNumber}", troubleshootingHint: "Configuration not loaded");
                    return;
                }

                // Find the account settings for this instance
                var accountSettings = _currentConfig.Accounts.FirstOrDefault(a => a.InstanceNumber == instance.InstanceNumber);
                if (accountSettings == null)
                {
                    _userNotifications.ShowError($"No account configuration found for instance {instance.InstanceNumber}", troubleshootingHint: "Check your configuration settings");
                    return;
                }

                // Create a new cancellation token source for this instance; kept on the instance so Stop can cancel it
                using var singleRunCts = new CancellationTokenSource();
                instance.RunCts = singleRunCts;

                // Run the tasks
                try
                {
                    await RunBotTasksForInstance(instance, singleRunCts.Token);
                }
                finally
                {
                    instance.RunCts = null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error running specific instance: {ex.Message}", category: LogCategories.TaskExecution);
                _userNotifications.ShowError($"Error running instance {instance?.AccountName}", troubleshootingHint: ex.Message);
            }
            finally
            {
                // Mark individual instance as no longer running
                SetIndividualInstanceRunning(false);
            }
        }

        private void OnCycleStarted(int cycleNumber, TimeSpan waitTime)
        {
            _userNotifications.ShowStatus($"Cycle {cycleNumber} started", NotificationType.Info);
        }

        private void OnCycleCompleted(int cycleNumber, bool success)
        {
            if (success)
                _userNotifications.ShowSuccess($"Cycle {cycleNumber} completed successfully");
            else
                _userNotifications.ShowError($"Cycle {cycleNumber} completed with errors");
        }

        private void OnWaitingBetweenCycles(TimeSpan waitTime)
        {
            _userNotifications.ShowStatus($"Waiting {waitTime.TotalMinutes:F1} minutes before next cycle", NotificationType.Info);
        }

        private void OnCycleStatusUpdate(string status)
        {
            // Synchronize pause state based on cycle service status
            if (status == "Paused" && !IsBotPaused)
            {
                IsBotPaused = true;
            }
            else if (status == "Running" && IsBotPaused)
            {
                IsBotPaused = false;
            }
        }

        // Helper methods to track individual instance execution state
        private void SetIndividualInstanceRunning(bool isRunning)
        {
            lock (_individualInstanceLock)
            {
                if (_hasRunningIndividualInstances != isRunning)
                {
                    _hasRunningIndividualInstances = isRunning;
                    _logger.LogInfo($"Individual instance execution state changed: {isRunning}");
                }
            }
        }

        public void Dispose()
        {
            try
            {
                // Unsubscribe from events to prevent memory leaks
                if (ConfigVM != null)
                {
                    ConfigVM.ConfigurationChanged -= InitializeGuiFromConfig;
                }
                
                if (_cycleService != null)
                {
                    _cycleService.OnCycleStarted -= OnCycleStarted;
                    _cycleService.OnCycleCompleted -= OnCycleCompleted;
                    _cycleService.OnWaitingBetweenCycles -= OnWaitingBetweenCycles;
                    _cycleService.OnStatusUpdate -= OnCycleStatusUpdate;
                    _cycleService.Dispose();
                }
                
                _botCancellationTokenSource?.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error disposing MainViewModel: {ex.Message}");
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
} 