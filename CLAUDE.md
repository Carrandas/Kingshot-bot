# CLAUDE.md

## Project: KingshotAuto
Windows desktop app for game automation using C# (.NET 10), WPF, LDPlayer emulator, OpenCV, and Tesseract OCR. Open source under the MIT license. It was previously a paid product; the licensing/activation, analytics/telemetry, and auto-update systems have all been removed, so a build contacts no servers and sends no data.

This file is committed: instructions for Claude Code working on this repo.

## Commands
| Command | Purpose |
|---------|---------|
| `dotnet restore BotApp.sln` | Restore dependencies |
| `dotnet build Bot.GUI/Bot.GUI.csproj -c Debug` | Debug build (output: `Bot.GUI/bin/x64/Debug/net10.0-windows/win-x64/KingshotAuto.exe`) |
| `dotnet run --project Bot.GUI/Bot.GUI.csproj` | Run app |
| `KingshotAuto.exe --schedule` / `--new-cycle` / `--once` | Start and press Run on schedule (continues the saved countdown to the next cycle, `data/cycle_next.txt`; a cycle at once when none is left) / New cycle (a cycle at once) / Run once (`--cycle` = `--schedule`; `--launch` = Launch on every instance) |
| `KingshotAuto.exe --task MysticTrial --character llsa` | Start and press ▶ for one task on one character (name matched like the roster; runs even when the task's switch is off) |
| `KingshotAuto.exe --castle-battle` | Start and press Castle Battle (stops the cycle; joins rallies and heals on every ticked character until Stop) |
| `dotnet test Bot.Core.Tests` | Unit tests (code only, no emulator) |
| `dotnet test Bot.Core.IntegrationTests` | Screenshot tests: task detection on saved screenshots |
| `.\Build.ps1 -Version "x.x.x"` | Package a portable zip + NSIS installer into `dist/` |
| `.\Build.ps1 -Version "x.x.x" -SkipInstaller` | Portable zip only |

- SDK pinned in `global.json` (10.0.100, rollForward latestMinor).
- Code analysis: `SonarAnalyzer.CSharp` (in `Directory.Build.props`) reports SonarQube's C# rules as build warnings (`Sxxxx`). **Keep Sonar issues and compiler warnings (including nullable `CS86xx`) at 0**: build before finishing a change and fix what it adds, in `Bot.Core`, `Bot.GUI` and the test projects. Don't add `NoWarn`s or `#pragma` suppressions without a comment giving the reason (e.g. WPF binds to an instance property, so S2325 stays). An intentionally empty `catch` gets its reason as a comment inside the braces.
  - Cyclomatic (S1541, max 10) and cognitive (S3776, max 15) complexity are enabled in `.editorconfig`. Keep new methods under both limits by extracting helpers.
  - Each warning shows twice in the build output (the WPF temp project repeats Bot.GUI's); count unique `file(line)` pairs.
- The build fails while KingshotAuto.exe is running (locked DLLs): ask the user to close the GUI first. To only check that the GUI compiles while it runs, build into a scratch folder: `dotnet build Bot.GUI/Bot.GUI.csproj -c Debug -o <scratch>`.
- Run the tests per project, not `dotnet test BotApp.sln`: the solution build includes Bot.GUI, whose output is locked while the GUI runs. The test projects reference only Bot.Core.

## Tests
- `Bot.Core.Tests` (NUnit 4): pure logic, e.g. `CharacterRoster` name matching, `DailyGate`/`Schedule`, `TaskRunHistory`, task registration and run order. The `InstanceManager*` fixtures drive LDPlayer and are `[Explicit]`: a plain `dotnet test` never touches the emulator.
  - Don't construct `TaskManager` in a test: its `InstanceManager` searches for the LDPlayer install and never finishes without one. Use `TaskManager.CreateTaskModules` / `GetRunOrder`.
  - `DailyGate` and `TaskRunHistory` save under `data/` in the test output folder; use a unique account key per test.
- `Bot.Core.IntegrationTests` (NUnit 4): task detection on saved 720x1280 screenshots in `Screenshots/` (PNG, the same bytes `TakeScreenshotAsync` gives the matchers). Make the detection step of a task `internal` (e.g. `AllianceMobilizationTask.CheckCards`, `EventsThreshold`; `InternalsVisibleTo` is set for both projects) and assert the result.
  - Expected results come from what the user confirmed in the game, never from what the code returned when the test was written.
  - Black out other players' names (chat lines) in a screenshot before adding it: the repo is public.
  - Check that a new test fails when the threshold is broken (e.g. lower `KeepThreshold` below the yellow gem's 0.42), then restore it.

## Project Structure
```
Bot.GUI/              # WPF UI (MVVM pattern)
  ViewModels/         # UI logic
  Views/              # XAML views
  Converters/         # WPF value converters (TaskTypeToDisplayNameConverter = task display names)
Bot.Core/             # Core automation logic
  Tasks/              # Task system (TaskManager, BaseTask, Modules/)
  Services/           # Business services (CycleManagement, etc.)
  ImageDetection/     # Template matching (Unified/Color/Grayscale services, DecodedScreenshotCache)
  LDPlayer/           # Emulator control (AdbControllerV2)
  Config/             # Configuration management
  Utils/              # OcrService, LocatorService (view detection/recovery), helpers
configs/              # default_config.example.json. The GUI keeps the live accounts/tasks in configs/default_config.json
                      #   next to the exe (Bot.GUI/bin/.../configs/); configs/config.json is optional and usually absent
templates/images/     # Template images for CV, one folder per task (copied to output as Content)
logs/                 # logs.txt (rolls over at 10 MB into logs_001.txt, ...) + log-<Instance>-dd-MM-yy.txt (3-day retention)
data/                 # next to the exe, created at runtime: daily_gates.json, task_history.json, other gate dates, suggestion_box_answers.json
```

## Tech Stack
- **OpenCVSharp4**: Template matching (CCoeffNormed) and image processing
- **Tesseract**: OCR for text extraction
- **Serilog**: Structured logging
- **AdvancedSharpAdbClient**: ADB communication

## Adding a task
1. Class in `Bot.Core/Tasks/Modules/`, inheriting `BaseTaskWithCommonPatterns`. Templates load from `templates/images/<tasktype lowercase>/`.
2. Register it in five places (`TaskRegistrationTests` checks the first three):
   - `TaskType` enum in `Bot.Core/Models/AccountSettings.cs`
   - `TaskManager.CreateTaskModules` in `TaskManager.cs`
   - the priority dictionary in `TaskManager.cs`
   - `GetUserFriendlyTaskName` in `RecoveryTask.cs`
   - `DisplayNames` in `Bot.GUI/Converters/TaskTypeToDisplayNameConverter.cs`

   The GUI's task list is built from the enum. The priority sets the run order (lower first): Journey early (the Idle Journey only runs while the game stays on the character), CollectOnlineRewards before AutoHunt (stamina for the Intel missions), SuggestionBox before ResidentWelcome (its zoom-out shrinks the envelope), ChangeAccount at both ends.
3. Common pattern, as in `JourneyTask` and `WaterEssenceTask`:
   - Start with `LocatorService.EnsureViewAsync(ViewType.BaseView, ...)` and finish with it again.
   - Use a private `MatchAsync` helper: `UnifiedTemplateMatchingService`, scale 1.0, a tight `searchArea`.
   - Tap with `ClickAsync` / `ClickCenterInRectAsync`.
   - Return `TaskExecutionDetails.Succeeded/Failed(...)`.
4. Many daily tasks start from the side panel (the arrow at the left edge, then the Daily tab at (370,268)). With "Hide after mission completion" on, a row disappears once its mission is done. A missing row usually means "already done", not a failure.
5. Number boxes (e.g. the Mystic Trial troop ratio): tap the box, then `TypeIntoFocusedFieldAsync` (clears it and types via adb), then the input bar's OK.
6. OCR of coloured text: mask the colour with HSV `InRange` into a white-on-black image first, then run `OcrService` with a whitelist and `PageSegMode.SingleLine` (`SingleBlock` for multi-line text, as in `SuggestionBoxTask.ReadText`).
7. Once-a-day tasks: the game resets at 00:00 UTC. Use `DailyGate` (saved in `data/daily_gates.json`, keyed per game account via `Schedule.AccountKey(AccountDetectionTask.GetCachedAccountId(...), account)`): `if (DailyGate.DoneToday(TaskType, key)) return TaskExecutionDetails.SkippedBecause("Already done today");` first, `DailyGate.MarkDone(TaskType, key)` after the run (CollectVip, ClaimMail, NomadicMerchant, TruegoldCrucible, SuggestionBox, MysticTrial, Arena; Arena can also wait for a UTC time of day, `ArenaSettings.RunAfterUtc`, and skips opponents with our alliance tag, `SkipAllianceMembers`, on by default). Older gates: AllianceTriumph (memory), CollectRewards (disk). A gate or timer skip must return `SkippedBecause` / `Skipped = true`, or the dashboard shows it as a real run.
   Any task with a gate or timer implements `IScheduledTask`: `GetNextRunUtc` (next run for the dashboard; deterministic, no file reads) and `ClearSchedule` (the ▶ button runs regardless).
8. Suggestion Box: the letters come from a fixed list. The built-in answer table is `KnownAnswers` in `SuggestionBoxTask`; learned answers go to `data/suggestion_box_answers.json`. Result: +3% right, 0% neutral (some letters have no +3% answer), -2% wrong.
9. Collect Bank (`CollectBankTask`, a bank-only mode of `CollectRewardsTask`): the Deals hub's bank vault on its own 24 h timer (`data/collect_rewards_bank_times.json`, due 24 h + 1 min after the last deposit; a visit that finds nothing to do looks again after 2 h). The store task (`CollectRewards`) is once a day and no longer touches the bank. The switch for Collect Bank is on the Bot tab and off until ticked.

## Castle Battle (beta)
- Its own mode, not a cycle task: the Bot tab's **⚔ Castle Battle** button (or `--castle-battle [--character <name>]`) stops the cycle and runs `CastleBattleTask` passes on every ticked character until Stop or the max hours (`MainViewModel.RunCastleBattleAsync`). `TaskType.CastleBattle` is a system task in the GUI, so it never shows in the cycle list.
- A pass: read the heal % and the Marching tracker. Below 70% with no march out, join one rally (the game allows one at a time). A red "Rally" row gets the offensive flag, a blue "Def. Lineup" row the defensive flag; the flags keep their own troops and ratio, so there is no Balance step. Then heal.
- Healing: the Heal Injured popup's right button alternates Heal and Help. The batch goes in one troop box (0 in the others: Cancel healing and the popup refill them to the max). Each pass first tries the character's batch + 100 (`CastleBattleSettings.HealAmounts`); Speedups after Help = too big: Cancel healing > Confirm, 100 fewer. Never tap Speedups, Finish (gems) or the Cancel spot unless Speedups shows (it is Quick Select otherwise).
- Not built yet: the garrison swap after a win (send the defensive flag via building > Garrison > Dispatch Troops, recall the offensive one ~3 s before it arrives), reinforcing, resources from the Backpack.

## Bear Hunt (beta)
- Two parts. The cycle task `BearHuntTask` (its Bot tab switch = the "Bear Hunt on" switch) reads the map's Task List once a day after reset (tap the UTC banner at the top of the map; date headers + "Bear Hunt - Trap N / UTC 13:00" rows, `BearHuntSchedule`) and saves `BearHuntSettings.NextBearUtc` for our trap. `MainViewModel.CheckBearScheduleAsync` (30 s timer) then starts the Bear Hunt mode 10 min before the bear (stops the cycle like Castle Battle, restarts the schedule after). Button **🐻 Bear Hunt** / `--bear-hunt [--character <name>]` starts it by hand.
- Mode: `BearHuntRunner` loop + `BearHuntPlanner` (pure, unit-tested) pick the character; each pass is `BearHuntTask` with `BearHuntStatus.BearStartUtc` set. Prep (once per character): Alliance > War > Auto-Join > Stop, recall every Marching-panel row with a return arrow (gatherers), then the battle pet skills (paw > Pet Skill > a red-badge skill > Quick Use > Use: all battle skills for 2 h). Bear up: own rally with the own flag (default 3, set 5/10/85 in the game once) when the troops are home (estimate sent + 5:30), never after start + 24:30; then join the top rallies of War with the join flag (default 4), up to 6 / free queues. First character goes first; switch early when another character's own rally is due.
- Own rally (calibrated on the bear of 2026-11-08): bear icon (left of the mail, "30min" under it; centres the map on the bear) > tap the bear (360,480) > "Lv. 1 Raging Bear" card > Rally > Hold a rally (5 min is ticked) > flag > Deploy. The trap opened ~5 min after the Task List time: with no bear icon yet, every own rally waits 20 s (`BearHuntStatus.BearNotUpUntil`), no switching. Joins only take War rows with "Raging Bear" under the members (Titan Roc rallies are skipped) and skip our own rally (leader name OCR, `IsOwnRally`).
- Live test notes (2026-11-08, llsa only): Bear Hunt passes skip the Alliance Help/Auto Heal auto tasks (base and back timed out with the bear crowd and recovery restarted the game); the bear is drawn a few seconds after a switch, so the bear tap retries; a join counts once the deploy screen closes. Failed steps save a screenshot to `logs/screenshots/bearhunt/` (kept while in beta). Not yet seen live: character switching, the scheduled start.
- Gear swap (optional, per character, Bear Hunt tab > Gear swap; `BearHuntSettings.GearSwaps`, `HeroGearSwapper`): in the prep the gear moves from the normal hero to the bear hero (Hero's Gear tab: Unequip All on one, Quick Equip on the other, which dresses the hero with the best free gear), after the bear a restore pass per prepared character moves it back (`BearHuntStatus.RestoringGear`; not run when the mode is stopped by hand). Heroes are found by the name in the Hero screen's title (OCR, `HeroCatalog`, 37 heroes): Heroes > first card > Gear tab, then the `>` arrow steps through the ring (~1 s each). Both buttons grey out when there is nothing to do, so a repeat is harmless.
- Shared with Castle Battle: `RallyTaskBase` (War list, join "+", deploy flags, Marching tracker OCR, cross-folder `MatchAsync`). The Task List reader returns every event row, so Castle Battle and other events can be scheduled from it later.

## Key Patterns
- **Config**: Singleton `ConfigurationManager`, JSON-serialized task settings per account.
- **Multi-instance**: Per-instance locks, cached screenshots (500 ms), progressive delays.
- **Screenshots**: ADB `exec:screencap` raw framebuffer (~270 ms). `DecodedScreenshotCache` shares decoded/gray/HSV Mats between matches on the same screenshot byte[].
- **Template matching**: Use search areas and pick thresholds from measured scores (e.g. "0.97-1.00 present, <= 0.47 elsewhere" in a comment next to the constant).
- **Delays**: The `Task.Delay` waits in tasks are tuned to game animations. Don't shorten them for speed.
- **Scrolling lists**: use slow, short drags (e.g. 200 px over 1.5 s). A fast swipe flings the list a varying distance (300-566 px seen), so rows can skip past unjudged (Alliance Mobilization).

## Live testing on LDPlayer
- adb: `/c/LDPlayer/LDPlayer14/adb.exe`, serial `emulator-5554`, screen 720x1280. In Git Bash prefix adb shell paths with `MSYS_NO_PATHCONV=1`.
- **Never tap/swipe on the emulator while the user's GUI bot is running.** Taps collide with the bot's. Screenshots (`exec-out screencap -p`) are fine.
- Watch a GUI run by tailing the newest of `logs/logs.txt` / `logs/logs_NNN.txt`. Normal noise you can ignore:
  - `Could not find 'x.png' (best confidence ...)`
  - `Could not parse march count` (AutoHunt falls back to 1)
  - `[Locator] Could not detect any known view`, when it recovers via the back-arrow loop
- Use screenshots for coordinates. LDPlayer's `getevent` touch coordinates don't map 1:1 to screen pixels.
- Don't modify the user's `configs/config.json` or `configs/default_config.json` for tests; use a scratch copy. The GUI keeps its config loaded and may overwrite edits, so ask the user to change task toggles in the GUI.
- To test one task: the ▶ button next to it on the Bot tab, with the character selected, or restart the GUI with `--task <TaskType> --character <name>` (no GUI clicks needed). It starts LDPlayer if needed, switches character and ignores the task's own timer/daily gate and its on/off switch (`BaseTaskWithCommonPatterns.RunByHand`). The log shows `▶ Single run: ...` and `▶ Single run of X done: ok|failed ...`.
- The Bot tab is a task dashboard (`TaskDashboardViewModel`): run history in `data/task_history.json`, next run from `IScheduledTask` and `CycleManagementService.NextCycleUtc`. Characters are linked to their game account ID (`accountId` in the config) after Account Detection, so per-character data needs Change Account ("Switch characters each cycle") to have run once.
- Settings > Tasks picks which tasks show on the Bot tab (`hiddenTasks` in the config); hidden tasks never run. The task switches on the Bot tab are `enabledTasks`.
- Debug screenshots for a task go under `logs/screenshots/<task>/` next to the exe; remove the saving before committing.
- Characters on the test login: [KFC]llsa (account 1104083889) and [KFC]Asli (34431520).
- Measuring template scores: Python 3.9 with `cv2` is installed (PIL is not). Score present vs absent screenshots with `cv2.matchTemplate(..., cv2.TM_CCOEFF_NORMED)`; Python needs Windows paths (`cygpath -m`).

## Gotchas
- **Native deps via Git LFS**: the Tesseract/Leptonica DLLs (`runtimes/`) are Git LFS objects. Run `git lfs install` before cloning (or `git lfs pull` after) or OCR will fail at runtime.
- **"Device emulator-5554 not found" while LDPlayer shows Android**: LDPlayer's ADB debugging is off (`"basicSettings.adbDebug": 1` missing from `vms/config/leidian0.config`), so the emulator opens no adb port. `AdbRecoveryService` switches it back on; by hand: LDPlayer Settings > Other settings > ADB debugging > Open local connection.
- **StaticResourceExtension error**: Check for duplicate converters between App.xaml and views.
- **Version**: Pass `-p:Version=x.x.x` to the build (single source of truth).
- **Debug vs Release**: Debug allocates a console for log output.

## Git
- Push to the fork only: `git push mine main` (Carrandas/Kingshot-bot). `origin` is the upstream KingshotAuto repo, where the user has no push rights.
- Commit or push only when the user asks.

## Security / Open-source notes
- Never commit secrets/keys.
- The app sends no telemetry and performs no license or online checks.
- This automates a third-party game and may violate that game's Terms of Service (see `SECURITY.md`). Use at your own risk.

## More Info
- `README.md`: overview, prerequisites, build/run
- `CONTRIBUTING.md`: how to contribute
- `docs/ARCHITECTURE.md`: detailed architecture, code patterns, task system
- `todo.md`: open work items
