# Architecture

Detailed architecture documentation for KingshotAuto.

## Core Components

### Task System (Command + Strategy Pattern)
The task system is the heart of the automation framework:

```
ITask (Interface)
├── BaseTaskWithCommonPatterns (Abstract Base)
│   ├── Unified ADB operations, screenshot caching
│   ├── Template matching and error recovery
│   └── Common automation patterns
└── Concrete Tasks (Tasks/Modules/), about 30
    ├── System: StartupTask, AccountDetectionTask, ChangeAccountTask, RecoveryTask
    ├── Map/combat: AutoHuntTask (+ AutoHunt/ coordinator), AutoRallyTask, FarmingTask, AutoShieldTask, AutoHealTask
    ├── Town: AutoBuildTask, TroopTrainingTask, AutoTechnologyTask, SuggestionBoxTask, ResidentWelcomeTask
    ├── Daily rewards: CollectOnlineRewardsTask, CollectRewardsTask, NomadicMerchantTask, PetAdventureTask,
    │   JourneyTask, TruegoldCrucibleTask, WaterEssenceTask, ConquestCollectTask, CollectVipTask,
    │   ClaimMailTask, ClaimMissionsTask, AutoClaimHeroTask
    └── Alliance: AllianceHelpTask, AllianceTechnologyTask, AllianceTriumphTask, AllianceChestsTask, AllianceMobilizationTask
```

**Adding new tasks:** see [CONTRIBUTING.md](../CONTRIBUTING.md#how-to-add-a-new-automation-task). A task is registered in five places: the `TaskType` enum, `_availableTasks` and the priority dictionary in `TaskManager`, `RecoveryTask.GetUserFriendlyTaskName`, and the GUI's `TaskTypeToDisplayNameConverter`.

### Task Order
`TaskManager` sorts the enabled tasks by a priority dictionary (lower runs first; `TaskManager.GetRunOrder`). The order matters in a few places:
- `AccountDetection` runs first, since other tasks key their state on the account ID.
- `CollectOnlineRewards` runs before `AutoHunt`: the online rewards include the stamina the Intel missions spend.
- `SuggestionBox` runs before `ResidentWelcome`, whose zoom-out shrinks the Suggestion Box envelope.
- `ChangeAccount` runs at both ends of the list (see Multiple Characters).

Only one task loop runs per instance at a time: `TaskManager` keeps a per-instance run guard, so the cycle service and the instance's Start button can't run two loops on the same emulator.

### Multiple Characters
One game login can hold several characters. `ChangeAccountTask` runs the task list once on every enabled character:
- At the start it reads the game's Characters screen. If the active character is disabled, it switches to an enabled one.
- At the end it switches to the next enabled character that hasn't run yet this cycle, and the list restarts.

The character list is kept in `AccountSettings.Characters` (`CharacterRoster`). Names come from OCR, so they're compared loosely: the alliance tag is ignored, and I/l/1/| count as the same letter.

### Daily Gates, Timers and Saved State
The game resets at **00:00 UTC**. Gates are keyed per game account (`AccountDetectionTask.GetCachedAccountId`, or `Schedule.AccountKey`), so each character has its own:
- **`DailyGate`** (once a day, saved to `data/daily_gates.json`): Collect VIP, Nomadic Merchant, Truegold Crucible, Suggestion Box (only after it answered letters; "no letters" sets no gate). A task calls `DailyGate.DoneToday` first and `DailyGate.MarkDone` after its run.
- Own daily state: Collect Store Rewards (saved to disk), Alliance Triumph (in memory, reset when the app restarts)
- Own timers: Alliance Mobilization every 25 minutes (a refreshed mission comes back after 30; skips a character below R4 until the app restarts), Claim Mail 3-6 hours, Conquest Collect every `WaitHours` (default 4), Alliance Technology every 10 hours, Journey's free supplies at 08:00 and 16:00 UTC

A gate skip returns `TaskExecutionDetails.SkippedBecause(...)` (`Skipped = true`): a success that did no work.

Gated tasks implement **`IScheduledTask`**:
- `GetNextRunUtc(accountKey, account)`: when the task has work again, for the dashboard. It is deterministic: random spreads report their lower bound, and it never reads the gate files (some loaders clear the dictionary on a read error).
- `ClearSchedule(accountKey)`: forgets the gate, so a single run from the dashboard does the work.

Saved state lives in `data/` next to the executable:
- `daily_gates.json`, `task_history.json` (see Task Dashboard)
- `collect_rewards_daily_runs.json` and `collect_rewards_bank_times.json`
- `suggestion_box_answers.json`, the Suggestion Box answers learned on top of the built-in table in `SuggestionBoxTask`

### Task Dashboard (Bot tab)
`TaskDashboardViewModel` / `TaskDashboardView` in the GUI:
- **Run history**: `TaskManager` records every task result in `TaskRunHistory` (`data/task_history.json`), keyed by game account and task. A skip (`Skipped`) only updates the "last check", never the last real run. `TaskRunHistory.GetCurrent(instance)` is the task running now.
- **Characters**: `ChangeAccountTask` records the active character (`CharacterRoster.SetActive`); after Account Detection, `CharacterRoster.LinkAccountId` stores the game account ID on that `CharacterEntry` (`accountId` in the config). The dashboard finds a character's history and gates through that ID.
- **Next run**: the next cycle (`CycleManagementService.NextCycleUtc`), or later when the task's `IScheduledTask.GetNextRunUtc` says so.
- **Single runs** (▶): `CycleManagementService.RunSingleTaskAsync` starts the instance, then `TaskManager.RunSingleTaskAsync` runs startup, `ChangeAccountTask.SwitchToCharacterAsync` if another character is active, Account Detection, `ClearSchedule`, and the task. It shares the one-run-per-instance guard with the cycle; the GUI disables ▶ while anything runs. A single run of Change Account only scans the Characters screen (`ChangeAccountTask.ScanAsync`).
- **Run once** starts the cycle service with `MaxCycles = 1`.
- The task rows follow `TaskManager.GetRunOrder` (the run order). `AccountSettings.HiddenTasks` (Settings > Tasks) hides tasks from the list, and `TaskManager` never runs them.

### Multi-Instance Management (Resource Pool Pattern)
```
CycleManagementService (Orchestrator)
├── InstanceManager (LDPlayer lifecycle)
├── ADBConnectionManager (Connection pooling)
├── TaskManager (Task orchestration)
└── SystemResourceMonitor (Resource throttling)
```

Key features: Semaphore-based throttling, progressive startup delays, circuit breaker pattern, connection pooling with health monitoring.

### Configuration System (Singleton Pattern)
```
ConfigurationManager (Singleton)
├── Template System (farming_focused, combat_focused, alliance_focused)
├── Multi-Account Support (independent settings per account)
├── Validation Framework
└── Hot Reloading
```

Structure: `accounts[]`, `enabledTasks[]`, `taskSettings` (JSON), `cycleManagement`, `farmingTargets`

### Image Detection & Template Matching
```
UnifiedTemplateMatchingService
├── Multi-scale matching (0.8x-1.2x scales)
├── Dual color space (BGR + HSV for UI elements)
├── Thread-safe caching (LRU)
└── Parallel processing
```

### Device Management (Connection Pool)
```
ADBConnectionManager (Static Service)
├── Connection pooling per instance
├── Health monitoring & automatic recovery
├── Dynamic throttling limits
└── Circuit breaker protection
```

## Service Layer

### Cycle Management Flow
1. Start cycle with random wait time (20-40 min configurable)
2. Process accounts (parallel or sequential based on `TotalRunningInstances`)
3. Per account: Start instance → Establish ADB → Run tasks (on every enabled character) → Shutdown (optional)
4. Wait between cycles → Repeat. During the wait the GUI shows each instance as "Done, next cycle in N min".

### Error Recovery Hierarchy
1. Task-level retry (BaseTaskWithCommonPatterns)
2. Recovery task execution (RecoveryTask for UI recovery)
3. Instance reboot (InstanceManager)
4. ADB server restart (ADBConnectionManager)
5. Circuit breaker activation (CycleManagementService)

## Code Patterns

### Task Execution Pattern
```csharp
// All tasks follow this in ExecuteTaskLogicAsync:
try {
    // 1. Initialize/validate state
    // 2. Ensure correct view (MapView, BaseView, etc.)
    // 3. Execute core task logic with proper delays
    // 4. Handle specific error conditions
    return new TaskExecutionDetails(true, message: "Task completed");
}
catch (Exception ex) {
    logger.LogError($"Error in task: {ex.Message}");
    return TaskExecutionDetails.Failed($"Task failed: {ex.Message}");
}
```

### Configuration Access Pattern
```csharp
var config = ConfigurationManager.Instance.GetConfig();
var account = config.Accounts.FirstOrDefault(a => a.AccountName == accountName);

// Task-specific settings
if (!account.TaskSettings.TryGetValue("TaskName", out var settingsJson))
    return new TaskSettings();
var settings = JsonSerializer.Deserialize<TaskSettings>(settingsJson);
```

### Multi-Instance Safety Pattern
```csharp
// Always use instance-specific operations
var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
var success = await ClickAsync(account.InstanceNumber, logger, clickPoint);
var cacheKey = $"template_{account.InstanceNumber}_{templateName}";
```

### Template Matching Usage
Recent tasks use a private `MatchAsync` helper around `UnifiedTemplateMatchingService`, at scale 1.0 and with a tight search area:
```csharp
private const double Threshold = 0.90;   // 1.00 present, <= 0.18 elsewhere
private static readonly Rectangle TabArea = new Rectangle(0, 1180, 230, 100);

var result = _templateMatcher.MatchTemplate(screenshot, Path.Combine(ImageTemplateFolder, "tab.png"), instance,
    threshold: Threshold, scales: new[] { 1.0 }, verboseLogging: false, searchArea: TabArea);
if (result.found) await ClickCenterInRectAsync(instance, logger, result.matchRect);
```
Thresholds come from measured scores: score the template where it is present and where it isn't, and put the threshold in the gap. Write those scores in a comment next to the constant.

Older tasks use `FindAndClickImageAsync("button.png", instance, logger, threshold: 0.6, searchArea: ..., isUIElement: true)`.

### OCR of Coloured Text
The OCR preprocessing (grayscale, invert, Otsu) loses coloured or outlined text. Mask the text colour with HSV `InRange` into a white-on-black image first, then run `OCRService` on it with a whitelist and a page segmentation mode (`SingleLine`, `SingleWord` or `SingleBlock`). See `JourneyTask.ReadIdleSuppliesAsync` and `SuggestionBoxTask.ReadText`.

## Versioning

Version is set via the build parameter `-p:Version=x.x.x` (single source of truth). Releases are distributed as GitHub Releases; the app has no in-app auto-update.
