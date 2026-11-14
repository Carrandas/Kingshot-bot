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
        public ICommand StartCastleBattleCommand { get; }
        public ICommand StartBearHuntCommand { get; }
        public ICommand StartKingshotCommand { get; }
        public ICommand RunNewCycleCommand { get; }
        public ICommand AddGearSwapCommand { get; }
        public ICommand RemoveGearSwapCommand { get; }
        /// <summary>Choices for the gear swap's heroes</summary>
        public string[] HeroNames { get; } = HeroCatalog.Names;

        /// <summary>Bear Hunt tab: one row per character with its phase</summary>
        public ObservableCollection<BearRow> BearRows { get; } = new();
        public int[] TrapOptions { get; } = { 1, 2 };
        /// <summary>Choices for the first character: the roster's names</summary>
        public ObservableCollection<string> CharacterNames { get; } = new();

        /// <summary>Castle Battle tab: one row per character with its phase</summary>
        public ObservableCollection<BattleRow> BattleRows { get; } = new();
        public int[] TeamOptions { get; } = { 1, 2, 3, 4, 5, 6, 7, 8 };

        public TaskDashboardViewModel(MainViewModel main, CycleManagementService cycleService, TaskManager taskManager, ConfigViewModel configVM)
        {
            _main = main;
            _cycleService = cycleService;
            _taskManager = taskManager;
            _configVM = configVM;

            RunOnScheduleCommand = new RelayCommand(_ => { _main.LaunchBotCommand.Execute(null); RefreshSoon(); }, _ => !IsBusy);
            RunOnceCommand = new RelayCommand(_ => { _main.RunOnceCommand.Execute(null); RefreshSoon(); }, _ => !IsBusy);
            RunNewCycleCommand = new RelayCommand(_ => { _main.LaunchNewCycleCommand.Execute(null); RefreshSoon(); }, _ => !IsBusy);
            RunTaskCommand = new AsyncRelayCommand(p => RunSingleAsync(p as TaskRow), p => !IsBusy && p is TaskRow);
            ScanCharactersCommand = new AsyncRelayCommand(_ => ScanCharactersAsync(), _ => !IsBusy && SelectedAccount != null);
            StartKingshotCommand = new AsyncRelayCommand(_ => StartKingshotAsync(), _ => CanStartKingshot);
            AddGearSwapCommand = new RelayCommand(_ => AddGearSwap(), _ => BearSettings != null);
            RemoveGearSwapCommand = new RelayCommand(RemoveGearSwap, p => p is HeroGearSwap);
            // allowed while the cycle runs: the battle stops it first
            StartCastleBattleCommand = new AsyncRelayCommand(_ => StartCastleBattleAsync(), _ => CanStartMode(SelectedAccount?.CastleBattleSettings.IsValid));
            CastleBattleStatus.Changed += () => OnUi(RefreshBattle);
            StartBearHuntCommand = new AsyncRelayCommand(_ => StartBearHuntAsync(), _ => CanStartMode(SelectedAccount?.BearHuntSettings.IsValid));
            BearHuntStatus.Changed += () => OnUi(RefreshBear);

            _cycleService.OnScheduleChanged += () => OnUi(Refresh);
            TaskRunHistory.Changed += () => OnUi(Refresh);

            // Relative times ("in 12 min") go stale: redraw twice a minute
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            timer.Tick += (_, _) => { Refresh(); RefreshBear(); };
            timer.Start();

            // A config reload refills Accounts with new objects: keep the same instance selected
            Accounts.CollectionChanged += (_, _) => OnUi(() =>
            {
                var instance = _selectedAccount?.InstanceNumber;
                SelectedAccount = Accounts.FirstOrDefault(a => a.InstanceNumber == instance) ?? Accounts.FirstOrDefault();
                OnPropertyChanged(nameof(ShowAccountPicker));
            });

            SelectedAccount = Accounts.FirstOrDefault();
        }

        /// <summary>Castle Battle and Bear Hunt exclude each other and a single run; a running cycle is stopped by them</summary>
        private bool CanStartMode(bool? settingsValid) =>
            settingsValid == true && !ModeStatus.AnyRunning && !_singleRunActive;

        private AccountSettings? _selectedAccount;
        public AccountSettings? SelectedAccount
        {
            get => _selectedAccount;
            set
            {
                if (_selectedAccount == value) return;
                if (_selectedAccount != null) { _selectedAccount.Characters.CollectionChanged -= OnCharactersChanged; _selectedAccount.HiddenTasks.CollectionChanged -= OnHiddenTasksChanged; _selectedAccount.CastleBattleSettings.PropertyChanged -= OnBattleSettingsChanged; _selectedAccount.BearHuntSettings.PropertyChanged -= OnBearSettingsChanged; }
                _selectedAccount = value;
                if (_selectedAccount != null) { _selectedAccount.Characters.CollectionChanged += OnCharactersChanged; _selectedAccount.HiddenTasks.CollectionChanged += OnHiddenTasksChanged; _selectedAccount.CastleBattleSettings.PropertyChanged += OnBattleSettingsChanged; _selectedAccount.BearHuntSettings.PropertyChanged += OnBearSettingsChanged; }
                OnPropertyChanged();
                OnPropertyChanged(nameof(BattleSettings));
                OnPropertyChanged(nameof(BearSettings));
                OnBattleSettingsChanged(null, new PropertyChangedEventArgs(null));
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

        public bool IsBusy => _cycleService.IsRunning || _singleRunActive || ModeStatus.AnyRunning;

        /// <summary>Start Kingshot also works while the bot waits between cycles (no cycle runs then)</summary>
        private bool CanStartKingshot => SelectedAccount != null && !_singleRunActive && !ModeStatus.AnyRunning
            && (!_cycleService.IsRunning || _cycleService.IsWaitingForNextCycle);

        /// <summary>Castle Battle settings of the selected instance (both characters share them)</summary>
        public CastleBattleSettings? BattleSettings => _selectedAccount?.CastleBattleSettings;

        /// <summary>0 = Cycle, 1 = Castle Battle, 2 = Bear Hunt; a running battle or bear hunt shows its tab</summary>
        private int _selectedTabIndex;
        public int SelectedTabIndex { get => _selectedTabIndex; set { if (_selectedTabIndex != value) { _selectedTabIndex = value; OnPropertyChanged(); } } }
        private bool _battleWasRunning;

        private string _battleStatusText = string.Empty;
        public string BattleStatusText { get => _battleStatusText; private set { if (_battleStatusText != value) { _battleStatusText = value; OnPropertyChanged(); } } }

        // the bot changes HealAmount from its own thread: handle it on the UI thread
        private void OnBattleSettingsChanged(object? sender, PropertyChangedEventArgs e) => OnUi(() =>
        {
            CommandManager.InvalidateRequerySuggested();
            if (sender != null && e.PropertyName != nameof(CastleBattleSettings.IsValid)) _configVM.SaveConfigIfNotApplying();
            if (e.PropertyName == nameof(CastleBattleSettings.HealAmounts)) RefreshBattle();
        });

        #region Bear Hunt

        /// <summary>Bear Hunt settings of the selected instance (all its characters share them)</summary>
        public BearHuntSettings? BearSettings => _selectedAccount?.BearHuntSettings;

        /// <summary>The Bear Hunt switch: the task's cycle switch (the cycle reads the next bear, the GUI starts the bear mode)</summary>
        public bool BearHuntEnabled
        {
            get => _selectedAccount?.EnabledTasks.Contains(TaskType.BearHunt) == true;
            set
            {
                if (_selectedAccount == null || value == BearHuntEnabled) return;
                if (value) _selectedAccount.EnabledTasks.Add(TaskType.BearHunt);
                else _selectedAccount.EnabledTasks.Remove(TaskType.BearHunt);
                _configVM.SaveConfigIfNotApplying();
                RebuildTasks();
                RefreshBear();
            }
        }

        private string _bearStatusText = string.Empty;
        public string BearStatusText { get => _bearStatusText; private set { if (_bearStatusText != value) { _bearStatusText = value; OnPropertyChanged(); } } }

        private string _nextBearText = string.Empty;
        public string NextBearText { get => _nextBearText; private set { if (_nextBearText != value) { _nextBearText = value; OnPropertyChanged(); } } }
        private bool _bearWasRunning;

        // the bot sets NextBearUtc from its own thread: handle it on the UI thread
        private void OnBearSettingsChanged(object? sender, PropertyChangedEventArgs e) => OnUi(() =>
        {
            CommandManager.InvalidateRequerySuggested();
            if (sender != null && e.PropertyName != nameof(BearHuntSettings.IsValid)) _configVM.SaveConfigIfNotApplying();
            RefreshBear();
        });

        private void AddGearSwap() => BearSettings?.GearSwaps.Add(new HeroGearSwap { Character = CharacterNames.FirstOrDefault() ?? string.Empty });

        private void RemoveGearSwap(object? parameter)
        {
            if (parameter is HeroGearSwap swap) BearSettings?.GearSwaps.Remove(swap);
        }

        private async System.Threading.Tasks.Task StartBearHuntAsync()
        {
            if (_selectedAccount == null) return;
            var run = _main.RunBearHuntAsync(_selectedAccount);
            RefreshSoon();
            await run;
            Refresh();
        }

        /// <summary>Bear Hunt tab: the status line, the next bear and one row per ticked character</summary>
        private void RefreshBear()
        {
            CommandManager.InvalidateRequerySuggested();
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(BearHuntEnabled));
            NextBearText = BearSettings?.NextBearUtc is DateTime next
                ? $"Next bear: {Local(next)} local ({Relative(next)})"
                : "Next bear: not known yet (the cycle reads the Task List once a day, or type it in)";
            BearStatusText = BuildBearStatus();

            var entries = BearHuntStatus.All();
            var names = Characters.Where(c => c.Entry?.Enabled != false).Select(c => c.Name).ToList();
            names.AddRange(entries.Select(e => e.Character).Where(n => !names.Exists(x => CharacterRoster.SameName(x, n))));
            while (BearRows.Count > names.Count) BearRows.RemoveAt(BearRows.Count - 1);
            for (int i = 0; i < names.Count; i++)
            {
                if (i == BearRows.Count) BearRows.Add(new BearRow());
                BearRows[i].Update(names[i], entries.FirstOrDefault(e => CharacterRoster.SameName(e.Character, names[i])));
            }
            bool running = BearHuntStatus.IsRunning;
            if (running) CycleStatus = BearStatusText;
            if (running && !_bearWasRunning) SelectedTabIndex = 2;
            _bearWasRunning = running;
        }

        private string BuildBearStatus() =>
            BearHuntStatus.Summary(DateTime.UtcNow, BearSettings, BearHuntEnabled, Local, Relative);

        /// <summary>Roster names for the first-character choice</summary>
        private void RefreshCharacterNames()
        {
            CharacterNames.Clear();
            foreach (var c in Characters.Where(c => c.Entry != null)) CharacterNames.Add(c.Name);
        }

        #endregion

        private async System.Threading.Tasks.Task StartCastleBattleAsync()
        {
            if (_selectedAccount == null) return;
            var run = _main.RunCastleBattleAsync(_selectedAccount);
            RefreshSoon();
            await run;
            Refresh();
        }

        /// <summary>Castle Battle tab: the status line and one row per ticked character</summary>
        private void RefreshBattle()
        {
            CommandManager.InvalidateRequerySuggested();
            OnPropertyChanged(nameof(IsBusy));
            BattleStatusText = CastleBattleStatus.EndsUtc is DateTime ends
                ? $"Castle Battle running until {Local(ends)} ({Relative(ends)})"
                : "Stopped. Castle Battle joins rallies and heals on every ticked character until Stop or the max hours.";

            var entries = CastleBattleStatus.All();
            var names = Characters.Where(c => c.Entry?.Enabled != false).Select(c => c.Name).ToList();
            names.AddRange(entries.Select(e => e.Character).Where(n => !names.Exists(x => CharacterRoster.SameName(x, n))));
            while (BattleRows.Count > names.Count) BattleRows.RemoveAt(BattleRows.Count - 1);
            for (int i = 0; i < names.Count; i++)
            {
                if (i == BattleRows.Count) BattleRows.Add(new BattleRow());
                var name = names[i];
                BattleRows[i].Update(name, entries.FirstOrDefault(e => CharacterRoster.SameName(e.Character, name)), BattleSettings?.HealAmountFor(name));
            }
            if (CastleBattleStatus.IsRunning) CycleStatus = BattleStatusText;
            if (CastleBattleStatus.IsRunning && !_battleWasRunning) SelectedTabIndex = 1;
            _battleWasRunning = CastleBattleStatus.IsRunning;
        }

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
            foreach (var entry in Characters.Select(c => c.Entry).Where(e => e != null))
                entry!.PropertyChanged -= OnCharacterEntryChanged;
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
            RefreshBattle();
            RefreshCharacterNames();
            RefreshBear();
        }

        private void OnCharacterEntryChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Enabled ticks and a new account ID are config changes
            if (e.PropertyName is nameof(CharacterEntry.Enabled) or nameof(CharacterEntry.AccountId))
            {
                _configVM.SaveConfigIfNotApplying();
                OnUi(Refresh);
                OnUi(RefreshBattle);
                OnUi(RefreshBear);
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
            if (isActive && cached.Length > 0) return cached;
            return entry == null ? Schedule.AccountKey(cached, _selectedAccount) : null;
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
            CycleStatus = BuildCycleStatus(min, max, nextCycle, cycleRunning, current);

            if (_selectedAccount == null) return;
            OnPropertyChanged(nameof(SwitchCharacters));
            var activeName = ActiveCharacterName();
            ActiveCharacterText = BuildActiveCharacterText(activeName);

            if (FollowRunningCharacter(current, activeName)) return;
            if (current == null) _followedName = null;

            RefreshTaskRows(_selectedAccount, current, activeName, cycleRunning, nextCycle);
        }

        private string BuildActiveCharacterText(string? activeName)
        {
            string noActiveText = SwitchCharacters ? "Active character not seen yet" : "Active character unknown: switch characters is off";
            return activeName != null ? $"Active in game: {activeName}" : noActiveText;
        }

        private void RefreshTaskRows(AccountSettings account, TaskType? current, string? activeName, bool cycleRunning, DateTime? nextCycle)
        {
            var key = SelectedAccountKey();
            // Active character unknown (Change Account off): assume the shown one, so the running row still lights up
            bool selectedIsActive = _selectedCharacter?.Entry == null || activeName == null ||
                                    CharacterRoster.SameName(activeName, _selectedCharacter.Entry.Name);
            bool characterEnabled = _selectedCharacter?.Entry?.Enabled ?? true;
            if (current == null || !selectedIsActive) RunningRow = null;

            foreach (var row in Tasks)
            {
                UpdateTaskRow(row, account, current, selectedIsActive, characterEnabled, key, cycleRunning, nextCycle);
            }
        }

        private string BuildCycleStatus(int min, int max, DateTime? nextCycle, bool cycleRunning, TaskType? current)
        {
            string runningText = current != null ? $": {TaskTypeToDisplayNameConverter.GetDisplayName(current.Value)}" : "";

            if (CastleBattleStatus.EndsUtc is DateTime battleEnds) return $"Castle Battle running until {Local(battleEnds)}";
            if (BearHuntStatus.IsRunning) return BuildBearStatus();
            if (_singleRunActive) return SingleRunText;
            if (cycleRunning) return $"Cycle {_cycleService.CurrentCycle} running{runningText}";
            return BuildWaitingStatus(min, max, nextCycle);
        }

        /// <summary>Nothing runs: whichever comes first, the scheduled Bear Hunt or the next cycle</summary>
        private string BuildWaitingStatus(int min, int max, DateTime? nextCycle)
        {
            var nextBear = BearHuntMode.NextModeStartUtc(Accounts, DateTime.UtcNow);
            if (nextBear != null && (nextCycle == null || nextBear < nextCycle))
                return $"Bear Hunt starts at {Local(nextBear.Value)} ({Relative(nextBear.Value)})" +
                       (nextCycle != null ? $", next cycle at {Local(nextCycle.Value)}" : "");
            if (nextCycle != null) return $"Next cycle at {Local(nextCycle.Value)} ({Relative(nextCycle.Value)})";
            return $"Stopped. Run on schedule: a cycle now, then every {min}-{max} min after it finishes";
        }

        /// <summary>
        /// Follow the run: when a task runs on another character than the one shown, show that character
        /// (once per character switch, so picking another character by hand during a run sticks).
        /// True when the selection changed (it rebuilds the rows and refreshes again).
        /// </summary>
        private bool FollowRunningCharacter(TaskType? current, string? activeName)
        {
            if (current == null || activeName == null || activeName == _followedName)
            {
                return false;
            }

            _followedName = activeName;
            var running = Characters.FirstOrDefault(c => c.Entry != null && CharacterRoster.SameName(c.Entry.Name, activeName));
            if (running == null || running == _selectedCharacter)
            {
                return false;
            }

            SelectedCharacter = running;
            return true;
        }

        private void UpdateTaskRow(
            TaskRow row, AccountSettings account, TaskType? current, bool selectedIsActive, bool characterEnabled,
            string? key, bool cycleRunning, DateTime? nextCycle)
        {
            row.IsRunningNow = current == row.Type && selectedIsActive;
            if (row.IsRunningNow) RunningRow = row;
            var record = key != null ? TaskRunHistory.Get(key, row.Type) : null;
            row.SetLastRun(record);

            if (row.IsRunningNow) { row.NextRunText = "running now"; return; }
            if (!row.Enabled || !characterEnabled) { row.NextRunText = "not in the cycle"; return; }

            DateTime? gate = key != null && _taskManager.GetTask(row.Type) is IScheduledTask scheduled
                ? scheduled.GetNextRunUtc(key, account) : null;
            row.NextRunText = BuildNextRunText(gate, cycleRunning, nextCycle);
        }

        private static string BuildNextRunText(DateTime? gate, bool cycleRunning, DateTime? nextCycle)
        {
            if (cycleRunning)
                return gate != null ? $"waits until {Local(gate.Value)}" : "this cycle";
            if (nextCycle != null)
            {
                bool gateAfterCycle = gate != null && gate > nextCycle;
                return Local(gateAfterCycle ? gate!.Value : nextCycle.Value) + (gateAfterCycle ? " (first cycle after)" : "");
            }
            return gate != null ? $"from {Local(gate.Value)}" : "next cycle";
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

        private async System.Threading.Tasks.Task StartKingshotAsync()
        {
            if (_selectedAccount == null || !CanStartKingshot) return;
            _singleRunActive = true;
            SingleRunText = "Starting LDPlayer and Kingshot...";
            Refresh();
            try
            {
                var result = await System.Threading.Tasks.Task.Run(() => _cycleService.StartKingshotAsync(_selectedAccount, System.Threading.CancellationToken.None));
                SingleRunText = result.Success ? "Kingshot started" : $"Start Kingshot failed - {result.Message}";
            }
            catch (Exception ex)
            {
                SingleRunText = $"Start Kingshot failed - {ex.Message}";
            }
            finally
            {
                _singleRunActive = false;
                Refresh();
                CycleStatus = SingleRunText;
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
            string amount;
            if (minutes < 1) amount = "<1 min";
            else if (minutes < 90) amount = $"{minutes} min";
            else amount = $"{Math.Abs(span.TotalHours):0.#} h";
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

    /// <summary>A character's line on the Castle Battle tab</summary>
    public class BattleRow : INotifyPropertyChanged
    {
        public string Character { get; private set; } = string.Empty;
        public string State { get; private set; } = string.Empty;
        public string Detail { get; private set; } = string.Empty;
        public string Joined { get; private set; } = string.Empty;
        public string Heals { get; private set; } = string.Empty;
        public string Updated { get; private set; } = string.Empty;
        public string HealBatch { get; private set; } = string.Empty;

        public void Update(string character, CastleBattleStatus.Entry? entry, int? healBatch)
        {
            HealBatch = healBatch?.ToString() ?? string.Empty;
            Character = character;
            SetEntry(entry);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }

        private void SetEntry(CastleBattleStatus.Entry? entry)
        {
            if (entry == null)
            {
                State = "-";
                Detail = Updated = string.Empty;
                Joined = Heals = "0";
                return;
            }
            State = CastleBattleStatus.Describe(entry.Phase);
            Detail = entry.Detail;
            Joined = entry.RalliesJoined.ToString();
            Heals = entry.HealRounds.ToString();
            Updated = entry.UpdatedUtc.ToLocalTime().ToString("HH:mm:ss");
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>A character's line on the Bear Hunt tab</summary>
    public class BearRow : INotifyPropertyChanged
    {
        public string Character { get; private set; } = string.Empty;
        public string State { get; private set; } = string.Empty;
        public string Detail { get; private set; } = string.Empty;
        public string Own { get; private set; } = string.Empty;
        public string Joined { get; private set; } = string.Empty;
        public string Updated { get; private set; } = string.Empty;

        public void Update(string character, BearHuntStatus.Entry? entry)
        {
            Character = character;
            entry ??= BearHuntStatus.Entry.New(character);
            State = BearHuntStatus.Describe(entry.Phase);
            Detail = entry.Detail;
            Own = entry.OwnRallies.ToString();
            Joined = entry.Joins.ToString();
            Updated = entry.UpdatedUtc.ToLocalTime().ToString("HH:mm:ss");
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
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

            LastRunTooltip = BuildLastRunTooltip(record);
        }

        private static string BuildLastRunTooltip(TaskRunHistory.Record? record)
        {
            string tip = "No run recorded";
            if (record?.LastRunUtc != null)
            {
                var runResult = record.LastRunOk ? "ok" : "failed";
                var runMessage = string.IsNullOrEmpty(record.LastRunMessage) ? "" : $" - {record.LastRunMessage}";
                tip = $"Last run: {runResult}{runMessage}";
            }
            if (record?.LastCheckUtc is DateTime check && check != record.LastRunUtc)
                tip += $"\nLast check {TaskDashboardViewModel.Local(check)}: {record.LastCheckMessage}";
            return tip;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
