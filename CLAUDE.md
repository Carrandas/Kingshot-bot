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
| `KingshotAuto.exe --schedule` / `--once` | Start and press Run on schedule / Run once (`--cycle` = `--schedule`; `--launch` = Launch on every instance) |
| `KingshotAuto.exe --task MysticTrial --character llsa` | Start and press ▶ for one task on one character (name matched like the roster; runs even when the task's switch is off) |
| `dotnet test Bot.Core.Tests` | Unit tests (code only, no emulator) |
| `.\Build.ps1 -Version "x.x.x"` | Package a portable zip + NSIS installer into `dist/` |
| `.\Build.ps1 -Version "x.x.x" -SkipInstaller` | Portable zip only |

- SDK pinned in `global.json` (10.0.100, rollForward latestMinor).
- Code analysis: `SonarAnalyzer.CSharp` (in `Directory.Build.props`) reports SonarQube's C# rules as build warnings (`Sxxxx`). Don't add new ones; an intentionally empty `catch` gets its reason as a comment inside the braces.
- The build fails while KingshotAuto.exe is running (locked DLLs): ask the user to close the GUI first. To only check that the GUI compiles while it runs, build into a scratch folder: `dotnet build Bot.GUI/Bot.GUI.csproj -c Debug -o <scratch>`.
- Run the tests per project, not `dotnet test BotApp.sln`: the solution build includes Bot.GUI, whose output is locked while the GUI runs. The test project references only Bot.Core.

## Tests
- `Bot.Core.Tests` (NUnit 4): pure logic, e.g. `CharacterRoster` name matching, `DailyGate`/`Schedule`, `TaskRunHistory`, task registration and run order. The `InstanceManager*` fixtures drive LDPlayer and are `[Explicit]`: a plain `dotnet test` never touches the emulator.
  - Don't construct `TaskManager` in a test: its `InstanceManager` searches for the LDPlayer install and never finishes without one. Use `TaskManager.CreateTaskModules` / `GetRunOrder`.
  - `DailyGate` and `TaskRunHistory` save under `data/` in the test output folder; use a unique account key per test.

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
  LDPlayer/           # Emulator control (ADBControllerV2)
  Config/             # Configuration management
  Utils/              # OCRService, LocatorService (view detection/recovery), helpers
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

   The GUI's task list is built from the enum. The priority sets the run order (lower first): CollectOnlineRewards before AutoHunt (stamina for the Intel missions), SuggestionBox before ResidentWelcome (its zoom-out shrinks the envelope), ChangeAccount at both ends.
3. Common pattern, as in `JourneyTask` and `WaterEssenceTask`:
   - Start with `LocatorService.EnsureViewAsync(ViewType.BaseView, ...)` and finish with it again.
   - Use a private `MatchAsync` helper: `UnifiedTemplateMatchingService`, scale 1.0, a tight `searchArea`.
   - Tap with `ClickAsync` / `ClickCenterInRectAsync`.
   - Return `TaskExecutionDetails.Succeeded/Failed(...)`.
4. Many daily tasks start from the side panel (the arrow at the left edge, then the Daily tab at (370,268)). With "Hide after mission completion" on, a row disappears once its mission is done. A missing row usually means "already done", not a failure.
5. Number boxes (e.g. the Mystic Trial troop ratio): tap the box, then `TypeIntoFocusedFieldAsync` (clears it and types via adb), then the input bar's OK.
6. OCR of coloured text: mask the colour with HSV `InRange` into a white-on-black image first, then run `OCRService` with a whitelist and `PageSegMode.SingleLine` (`SingleBlock` for multi-line text, as in `SuggestionBoxTask.ReadText`).
7. Once-a-day tasks: the game resets at 00:00 UTC. Use `DailyGate` (saved in `data/daily_gates.json`, keyed per game account via `Schedule.AccountKey(AccountDetectionTask.GetCachedAccountId(...), account)`): `if (DailyGate.DoneToday(TaskType, key)) return TaskExecutionDetails.SkippedBecause("Already done today");` first, `DailyGate.MarkDone(TaskType, key)` after the run (CollectVip, ClaimMail, NomadicMerchant, TruegoldCrucible, SuggestionBox, MysticTrial). Older gates: AllianceTriumph (memory), CollectRewards (disk). A gate or timer skip must return `SkippedBecause` / `Skipped = true`, or the dashboard shows it as a real run.
   Any task with a gate or timer implements `IScheduledTask`: `GetNextRunUtc` (next run for the dashboard; deterministic, no file reads) and `ClearSchedule` (the ▶ button runs regardless).
8. Suggestion Box: the letters come from a fixed list. The built-in answer table is `KnownAnswers` in `SuggestionBoxTask`; learned answers go to `data/suggestion_box_answers.json`. Result: +3% right, 0% neutral (some letters have no +3% answer), -2% wrong.

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
