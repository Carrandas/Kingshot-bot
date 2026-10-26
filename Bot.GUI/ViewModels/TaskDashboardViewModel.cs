using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Bot.Core.Models;
using Bot.Core.Services;
using Bot.Core.Tasks;
using Bot.Core.Tasks.Modules;
using Bot.GUI.Converters;

namespace Bot.GUI.ViewModels
{
    /// <summary>
    /// Bot tab: the cycle controls with the next cycle time, the characters of the selected instance, and per character
    /// every task with its last run, next run and a play button to run just that task.
    /// </summary>
    public class TaskDashboardViewModel : INotifyPropertyChanged
    {
        private readonly MainViewModel _main;
        private readonly CycleManagementService _cycleService;
        private readonly TaskManager _taskManager;
        private readonly ConfigViewModel _configVM;
        private readonly DispatcherTimer _timer;
        private bool _singleRunActive;
        private string? _followedName;

        /// <summary>Row of the task running right now on the shown character; the view scrolls it into view</summary>
        private TaskRow? _runningRow;
        public TaskRow? RunningRow { get => _runningRow; private set { if (_runningRow != value) { _runningRow = value; OnPropertyChanged(); } } }
        private string _singleRunText = string.Empty;

        public ObservableCollection<AccountSettings> Accounts => _configVM.Accounts;
        public ObservableCollection<CharacterItem> Characters { get; } = new();
        public ObservableCollection<TaskRow> Tasks { get; } = new();

        public ICommand RunOnScheduleCommand { get; }
        public ICommand RunOnceCommand { get; }
        public ICommand RunTaskCommand { get; }
        public ICommand ScanCharactersCommand { get; }

        public TaskDashboardViewModel(MainViewModel main, CycleManagementService cycleService, TaskManager taskManager, ConfigViewModel configVM)
        {
            _main = main;
            _cycleService = cycleService;
            _taskManager = taskManager;
            _configVM = configVM;

            RunOnScheduleCommand = new RelayCommand(_ => { _main.LaunchBotCommand.Execute(null); RefreshSoon(); }, _ => !IsBusy);
            RunOnceCommand = new RelayCommand(_ => { _main.RunOnceCommand.Execute(null); RefreshSoon(); }, _ => !IsBusy);
            RunTaskCommand = new AsyncRelayCommand(p => RunSingleAsync(p as TaskRow), p => !IsBusy && p is TaskRow);
            ScanCharactersCommand = new AsyncRelayCommand(_ => ScanCharactersAsync(), _ => !IsBusy && SelectedAccount != null);

            _cycleService.OnScheduleChanged += () => OnUi(Refresh);
            TaskRunHistory.Changed += () => OnUi(Refresh);

            // Relative times ("in 12 min") go stale: redraw twice a minute
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _timer.Tick += (_, _) => Refresh();
            _timer.Start();

            // A config reload refills Accounts with new objects: keep the same instance selected
            Accounts.CollectionChanged += (_, _) => OnUi(() =>
            {
                var instance = _selectedAccount?.InstanceNumber;
                SelectedAccount = Accounts.FirstOrDefault(a => a.InstanceNumber == instance) ?? Accounts.FirstOrDefault();
                OnPropertyChanged(nameof(ShowAccountPicker));
            });

            SelectedAccount = Accounts.FirstOrDefault();
        }

        private AccountSettings? _selectedAccount;
        public AccountSettings? SelectedAccount
        {
            get => _selectedAccount;
            set
            {
                if (_selectedAccount == value) return;
                if (_selectedAccount != null) { _selectedAccount.Characters.CollectionChanged -= OnCharactersChanged; _selectedAccount.HiddenTasks.CollectionChanged -= OnHiddenTasksChanged; }
                _selectedAccount = value;
                if (_selectedAccount != null) { _selectedAccount.Characters.CollectionChanged += OnCharactersChanged; _selectedAccount.HiddenTasks.CollectionChanged += OnHiddenTasksChanged; }
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowAccountPicker));
                RebuildCharacters();
            }
        }

        /// <summary>Only worth showing with more than one instance</summary>
        public bool ShowAccountPicker => Accounts.Count > 1;

        private CharacterItem? _selectedCharacter;
        public CharacterItem? SelectedCharacter
        {
            get => _selectedCharacter;
            set
            {
                if (_selectedCharacter == value) return;
                _selectedCharacter = value;
                OnPropertyChanged();
                RebuildTasks();
            }
        }

        public bool IsBusy => _cycleService.IsRunning || _singleRunActive;

        private string _cycleStatus = string.Empty;
        public string CycleStatus { get => _cycleStatus; private set { if (_cycleStatus != value) { _cycleStatus = value; OnPropertyChanged(); } } }

        private string _activeCharacterText = string.Empty;
        public string ActiveCharacterText { get => _activeCharacterText; private set { if (_activeCharacterText != value) { _activeCharacterText = value; OnPropertyChanged(); } } }

        public string SingleRunText { get => _singleRunText; private set { if (_singleRunText != value) { _singleRunText = value; OnPropertyChanged(); } } }

        private void OnCharactersChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => OnUi(RebuildCharacters);

        private void OnHiddenTasksChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => OnUi(RebuildTasks);

        private void RebuildCharacters()
        {
            var keep = _selectedCharacter?.Name;
            foreach (var c in Characters)
                if (c.Entry != null) c.Entry.PropertyChanged -= OnCharacterEntryChanged;
            Characters.Clear();
            if (_selectedAccount != null)
            {
                foreach (var entry in _selectedAccount.Characters)
                {
                    entry.PropertyChanged += OnCharacterEntryChanged;
                    Characters.Add(new CharacterItem(entry));
                }
                // Never scanned: one row for whoever is active in the game
                if (Characters.Count == 0) Characters.Add(new CharacterItem(null));
            }
            SelectedCharacter = Characters.FirstOrDefault(c => c.Name == keep) ?? Characters.FirstOrDefault(c => c.Entry?.Enabled != false) ?? Characters.FirstOrDefault();
            RebuildTasks();
        }

        private void OnCharacterEntryChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Enabled ticks and a new account ID are config changes
            if (e.PropertyName is nameof(CharacterEntry.Enabled) or nameof(CharacterEntry.AccountId))
            {
                _configVM.SaveConfigIfNotApplying();
                OnUi(Refresh);
            }
        }

        private void RebuildTasks()
        {
            Tasks.Clear();
            if (_selectedAccount == null) return;
            foreach (var type in Enum.GetValues<TaskType>().Where(t => TaskTypeToDisplayNameConverter.IsUserTask(t) && t != TaskType.ChangeAccount && !_selectedAccount.HiddenTasks.Contains(t)).OrderBy(TaskManager.GetRunOrder))
                Tasks.Add(new TaskRow(type, TaskTypeToDisplayNameConverter.GetDisplayName(type), _selectedAccount, () => { _configVM.SaveConfigIfNotApplying(); Refresh(); }));
            Refresh();
        }

        /// <summary>Game account key of the selected character, as the task gates and the history use it; null when not known yet</summary>
        private string? SelectedAccountKey()
        {
            if (_selectedAccount == null) return null;
            int instance = _selectedAccount.InstanceNumber;
            var entry = _selectedCharacter?.Entry;
            if (entry?.AccountId is { Length: > 0 } id) return id;

            // Not linked yet: only the character active right now has a known ID
            var activeName = CharacterRoster.GetActiveName(instance);
            bool isActive = entry == null || (activeName != null && CharacterRoster.SameName(activeName, entry.Name));
            var cached = AccountDetectionTask.GetCachedAccountId(instance);
            return isActive && cached.Length > 0 ? cached : entry == null ? Schedule.AccountKey(cached, _selectedAccount) : null;
        }

        /// <summary>
        /// Character active in the game: as Change Account last saw it, else the character whose saved account ID
        /// matches the one Account Detection found.
        /// </summary>
        private string? ActiveCharacterName()
        {
            if (_selectedAccount == null) return null;
            var name = CharacterRoster.GetActiveName(_selectedAccount.InstanceNumber);
            if (name != null) return name;
            var cached = AccountDetectionTask.GetCachedAccountId(_selectedAccount.InstanceNumber);
            return cached.Length > 0 ? _selectedAccount.Characters.FirstOrDefault(c => c.AccountId == cached)?.Name : null;
        }

        /// <summary>Change Account in the cycle: run the task list on every ticked character</summary>
        public bool SwitchCharacters
        {
            get => _selectedAccount?.EnabledTasks.Contains(TaskType.ChangeAccount) == true;
            set
            {
                if (_selectedAccount == null || value == SwitchCharacters) return;
                if (value) _selectedAccount.EnabledTasks.Add(TaskType.ChangeAccount);
                else _selectedAccount.EnabledTasks.Remove(TaskType.ChangeAccount);
                _configVM.SaveConfigIfNotApplying();
                Refresh();
            }
        }

        public void Refresh()
        {
            CommandManager.InvalidateRequerySuggested();
            OnPropertyChanged(nameof(IsBusy));

            var config = _configVM.GetCurrentBotConfig();
            int min = config.CycleManagement.MinWaitTimeBetweenCyclesMinutes, max = config.CycleManagement.MaxWaitTimeBetweenCyclesMinutes;
            DateTime? nextCycle = _cycleService.NextCycleUtc;
            bool cycleRunning = _cycleService.IsRunning && nextCycle == null;

            var current = _selectedAccount != null ? TaskRunHistory.GetCurrent(_selectedAccount.InstanceNumber) : null;
            string runningText = current != null ? $": {TaskTypeToDisplayNameConverter.GetDisplayName(current.Value)}" : "";

            if (_singleRunActive) CycleStatus = SingleRunText;
            else if (cycleRunning) CycleStatus = $"Cycle {_cycleService.CurrentCycle} running{runningText}";
            else if (nextCycle != null) CycleStatus = $"Next cycle at {Local(nextCycle.Value)} ({Relative(nextCycle.Value)})";
            else CycleStatus = $"Stopped. Run on schedule: a cycle now, then every {min}-{max} min after it finishes";

            if (_selectedAccount == null) return;
            OnPropertyChanged(nameof(SwitchCharacters));
            var activeName = ActiveCharacterName();
            ActiveCharacterText = activeName != null ? $"Active in game: {activeName}"
                : SwitchCharacters ? "Active character not seen yet" : "Active character unknown: switch characters is off";

            // Follow the run: when a task runs on another character than the one shown, show that character
            // (once per character switch, so picking another character by hand during a run sticks)
            if (current != null && activeName != null && activeName != _followedName)
            {
                _followedName = activeName;
                var running = Characters.FirstOrDefault(c => c.Entry != null && CharacterRoster.SameName(c.Entry.Name, activeName));
                if (running != null && running != _selectedCharacter)
                {
                    SelectedCharacter = running;  // rebuilds the rows and refreshes again
                    return;
                }
            }
            if (current == null) _followedName = null;

            var key = SelectedAccountKey();
            // Active character unknown (Change Account off): assume the shown one, so the running row still lights up
            bool selectedIsActive = _selectedCharacter?.Entry == null || activeName == null ||
                                    CharacterRoster.SameName(activeName, _selectedCharacter.Entry.Name);
            bool characterEnabled = _selectedCharacter?.Entry?.Enabled ?? true;
            if (current == null || !selectedIsActive) RunningRow = null;

            foreach (var row in Tasks)
            {
                row.IsRunningNow = current == row.Type && selectedIsActive;
                if (row.IsRunningNow) RunningRow = row;
                var record = key != null ? TaskRunHistory.Get(key, row.Type) : null;
                row.SetLastRun(record);

                if (row.IsRunningNow) { row.NextRunText = "running now"; continue; }
                if (!row.Enabled || !characterEnabled) { row.NextRunText = "not in the cycle"; continue; }

                DateTime? gate = key != null && _taskManager.GetTask(row.Type) is IScheduledTask scheduled
                    ? scheduled.GetNextRunUtc(key, _selectedAccount) : null;
                if (cycleRunning)
                    row.NextRunText = gate != null ? $"waits until {Local(gate.Value)}" : "this cycle";
                else if (nextCycle != null)
                    row.NextRunText = Local(gate != null && gate > nextCycle ? gate.Value : nextCycle.Value)
                                      + (gate != null && gate > nextCycle ? " (first cycle after)" : "");
                else
                    row.NextRunText = gate != null ? $"from {Local(gate.Value)}" : "next cycle";
            }
        }

        private async System.Threading.Tasks.Task RunSingleAsync(TaskRow? row)
        {
            if (row == null || _selectedAccount == null || IsBusy) return;
            var character = _selectedCharacter?.Entry?.Name;
            _singleRunActive = true;
            SingleRunText = $"Running {row.DisplayName}{(character != null ? $" on {character}" : "")}...";
            Refresh();
            try
            {
                var result = await _main.RunSingleTaskAsync(_selectedAccount, row.Type, character);
                SingleRunText = $"{row.DisplayName}: {(result.Success ? "done" : "failed")}{(string.IsNullOrEmpty(result.Message) ? "" : $" - {result.Message}")}";
            }
            finally
            {
                _singleRunActive = false;
                Refresh();
                CycleStatus = SingleRunText;  // keep the result visible until the next refresh
            }
        }

        private async System.Threading.Tasks.Task ScanCharactersAsync()
        {
            if (_selectedAccount == null || IsBusy) return;
            _singleRunActive = true;
            SingleRunText = "Scanning characters...";
            Refresh();
            try
            {
                var result = await _main.RunSingleTaskAsync(_selectedAccount, TaskType.ChangeAccount, null);
                SingleRunText = $"Characters: {result.Message}";
            }
            finally
            {
                _singleRunActive = false;
                RebuildCharacters();
                CycleStatus = SingleRunText;
            }
        }

        private void RefreshSoon()
        {
            // the cycle service sets IsRunning on its own thread a moment later
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            t.Tick += (_, _) => { t.Stop(); Refresh(); };
            t.Start();
        }

        internal static string Local(DateTime utc)
        {
            var local = utc.ToLocalTime();
            var today = DateTime.Now.Date;
            if (local.Date == today) return local.ToString("HH:mm");
            if (local.Date == today.AddDays(1)) return $"tomorrow {local:HH:mm}";
            if (local.Date == today.AddDays(-1)) return $"yesterday {local:HH:mm}";
            return local.ToString("dd MMM HH:mm");
        }

        internal static string Relative(DateTime utc)
        {
            var span = utc - DateTime.UtcNow;
            var minutes = (int)Math.Round(Math.Abs(span.TotalMinutes));
            string amount = minutes < 1 ? "<1 min" : minutes < 90 ? $"{minutes} min" : $"{Math.Abs(span.TotalHours):0.#} h";
            return span >= TimeSpan.Zero ? $"in {amount}" : $"{amount} ago";
        }

        private static void OnUi(Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) action();
            else dispatcher.BeginInvoke(action);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>A character on the selected instance, or (Entry null) "whoever is active" before the first scan</summary>
    public class CharacterItem
    {
        public CharacterItem(CharacterEntry? entry) { Entry = entry; }
        public CharacterEntry? Entry { get; }
        public string Name => Entry?.Name ?? "Active character";
        public bool HasEntry => Entry != null;
    }

    public class TaskRow : INotifyPropertyChanged
    {
        private readonly AccountSettings _account;
        private readonly Action _onEnabledChanged;

        public TaskRow(TaskType type, string displayName, AccountSettings account, Action onEnabledChanged)
        {
            Type = type;
            DisplayName = displayName;
            _account = account;
            _onEnabledChanged = onEnabledChanged;
        }

        public TaskType Type { get; }
        public string DisplayName { get; }

        /// <summary>In the cycle's task list (the same switch as on the Settings tab; it applies to every character)</summary>
        public bool Enabled
        {
            get => _account.EnabledTasks.Contains(Type);
            set
            {
                if (value == Enabled) return;
                if (value) _account.EnabledTasks.Add(Type); else _account.EnabledTasks.Remove(Type);
                _onEnabledChanged();  // save: the config only auto-saves EnabledTasks of the account selected on the Settings tab
                OnPropertyChanged();
            }
        }

        private string _lastRunText = "never";
        public string LastRunText { get => _lastRunText; private set { if (_lastRunText != value) { _lastRunText = value; OnPropertyChanged(); } } }

        private string _lastRunTooltip = string.Empty;
        public string LastRunTooltip { get => _lastRunTooltip; private set { if (_lastRunTooltip != value) { _lastRunTooltip = value; OnPropertyChanged(); } } }

        /// <summary>"ok", "failed" or "none": colours the status dot</summary>
        private string _lastRunState = "none";
        public string LastRunState { get => _lastRunState; private set { if (_lastRunState != value) { _lastRunState = value; OnPropertyChanged(); } } }

        private string _nextRunText = string.Empty;
        public string NextRunText { get => _nextRunText; set { if (_nextRunText != value) { _nextRunText = value; OnPropertyChanged(); } } }

        private bool _isRunningNow;
        public bool IsRunningNow { get => _isRunningNow; set { if (_isRunningNow != value) { _isRunningNow = value; OnPropertyChanged(); } } }

        public void SetLastRun(TaskRunHistory.Record? record)
        {
            if (record?.LastRunUtc is DateTime last)
            {
                LastRunText = $"{TaskDashboardViewModel.Local(last)} ({TaskDashboardViewModel.Relative(last)})";
                LastRunState = record.LastRunOk ? "ok" : "failed";
            }
            else
            {
                LastRunText = record?.LastCheckUtc != null ? "nothing to do yet" : "never";
                LastRunState = "none";
            }

            var tip = record?.LastRunUtc != null ? $"Last run: {(record.LastRunOk ? "ok" : "failed")}{(string.IsNullOrEmpty(record.LastRunMessage) ? "" : $" - {record.LastRunMessage}")}" : "No run recorded";
            if (record?.LastCheckUtc is DateTime check && check != record.LastRunUtc)
                tip += $"\nLast check {TaskDashboardViewModel.Local(check)}: {record.LastCheckMessage}";
            LastRunTooltip = tip;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
