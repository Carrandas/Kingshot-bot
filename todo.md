# KingshotAuto – todo / known errors

Last updated: 2026-10-23 (second-character run). Everything below is **uncommitted** unless noted.

Unattended test runs: start the app with `KingshotAuto.exe --launch` (presses Launch on every instance once).

## Errors found (live runs)

| # | Task | Error (log text) | Status |
|---|------|------------------|--------|
| 1 | CollectVip | `VIP plus button not found` | **Fixed, verified 2026-10-23**: new `vip-plus.png` (no red dot, 0.985) with search area; the 5 s waits catch the late Rewards popups; new `vip-rewards2.png`; returns to base on failure. Full flow ran: plus → Use once → ×N bubble → close → back |
| 2 | ClaimMissions | `Missions scroll button not found` / `Missions interface did not load` | **Fixed, verified 2026-10-23**: navigates to base first. Missions screen now has only Growth + Daily tabs: interface check uses `exit-menu.png`, both tabs are always visited, and an already-open tab is detected. Claim threshold is 0.85 because the teal "Go" buttons scored 0.69 |
| 3 | AutoHunt | `Template not found: close-details.png` | **Fixed**: uses the top-left `back-arrow.png` (verified) |
| 4 | AutoHunt | `Could not find 'view-button.png'` | **Fixed**: it was a false target. The target cutoff went from 0.68 to 0.8 (a 0.695 "scout" was not a pin) |
| 4b | AutoHunt | `No hunt mode indicators found` – hunt never ran | **Fixed, verified 2026-10-23**: `castle-hunt.png` no longer matches (0.43); the stamina icon in the header is used instead (`hunt-stamina.png`, grayscale, 0.85-0.98). The no-view branches use it too |
| 5 | Locator | `Could not detect any known view - bot may be lost` | Mostly gone with #3/#4/#4b; recovers when it happens |
| 6 | March counter | OCR at (550,68) reads `''` or `77` | **Mitigated**: the map HUD has no march counter there (it is the VIP badge). Values > 6 are capped at 6; the max-march popup handles the real limit |
| 7 | Startup | `Game did not reach a known view after loading` | Open (low) – not seen 2026-10-23 |
| 8 | Config | `Error loading config … enabledTasks[0]` + `Configuration failed to load during initialization` | **Fixed**: both loaders use the TaskType name converter; a missing `configs/config.json` is info only |
| 9 | Stop/Launch | Several Launch clicks started parallel runners | **Fixed**: one runner per instance, extra clicks are ignored; Stop cancels the running tasks |
| 10 | ConquestCollect | Blind fixed-coordinate taps, 4 h cooldown | **Rewritten 2026-10-23**: templates + green-colour check on both Claim buttons; Rewards dismissed on empty sky (the Conquer button sits under the banner). Nothing-to-claim path verified live; claim path verified by hand, bot run pending |
| 11 | CollectRewards | Hall of Heroes tab visited twice | Open (harmless) |
| 12 | CollectRewards | Store pitch popup ends the tab scan | Fixed 2026-10-19, popup not seen live since |
| 13 | AllianceTechnology | `Exit cross not found` | **Fixed 2026-10-23**: rewritten (see Alliance Technology below); new `close-x.png` |
| 14 | AutoAllianceHelp / Farming | `Could not find 'alliance-help.png'` | Info only |
| 16 | AutoClaimHero | `Could not find back button` after a free recruit whose result screen has no `rewards.png` banner | **Mitigated**: falls back to the locator. Need a screenshot of that result screen for a proper template |
| 17 | Recovery | Blind `ad-close.png` tap at 0.609 on an unknown screen | **Fixed**: recovery taps need 0.8 now (real X buttons score ~0.9+) |
| 18 | CollectVip | `vip-chest.png` (8×6 red-dot template) matches the open chest lid at 0.878 every run | Open (low) – costs 5 s, re-cut the template |
| 19 | All template matching | Screenshots were converted with `RGBA2BGR` but Windows bitmaps are BGRA: red and blue were swapped before grayscale, so colourful templates scored low (Journey supplies 0.72, VIP claim/plus 0.39/0.64, Crucible rows) | **Fixed 2026-10-23**: `BGRA2BGR` in all 4 matchers. Checked every template on 9 screenshots: only real matches went up |
| 20 | CollectRewards | Bank 24 h cooldown keyed by instance number, so a second character on the same instance skipped the bank | **Fixed 2026-10-23**: keyed by account ID |
| 21 | ClaimMissions | `Chapter tab not found` warning | Info only – the Missions screen no longer has a Chapter tab. The Chapter notification OCR still reads a false '8' there |
| 23 | Startup | "The account has been logged in on another device" dialog: Back does nothing, Startup timed out after 300 s | **Fixed 2026-10-25, verified live**: Startup presses Reconnect (`startup/reconnect.png`, 1.00 on the dialog). Mid-run the dialog still sends tasks into recovery ("bot is lost") |
| 22 | AutoHunt | `Skipping bear.png ...: it overlaps the King item` – a bear 18 px next to the King was never hunted (2 intel missions left) | **Fixed 2026-10-25**: King no-click zone margin 25 → 5 px. The King itself is still never tapped (on purpose). Verify live |

## Verified live 2026-10-23

- Bank: Withdraw + 1-day 5% deposit, 24 h cooldown set
- CollectVip full flow (see #1), Daily Free Bundle claim
- ClaimMissions: Daily claim-all + reward popups; the Daily point chests and the red dot are cleared
- Store/gems red dot is gone after Collect Rewards
- AutoHunt: ticks, bear / attack / scout targets, King item skipped

## To verify live

- [ ] Conquest Collect bot claim path (chest Claim green again; it stays grey for a while after a claim)

- [ ] Free weekly deal claim (`free-deal.png`)
- [ ] Farming Lv 7 / Lv 6 fallback when no full Lv 8 node is found

## Farming (tuned 2026-10-23 from a recording)

- Flow per resource: map search > Bread/Wood/Stone/Iron > Lv 8 > "Only search for full Resources" ticked > Search > Gather > remove non-blue heroes > Deploy
- [x] Non-blue (orange/purple) hero cards removed before Deploy, verified live on the farm account (2 removed per march, 4 marches sent)
- [x] "Only search for full Resources" is ticked when its green tick is missing (tap point verified live)
- Gem purchase of the gathering boost removed (never buy)
- [x] "Already gathering" icon skip verified live on 34431520 (own rows 0.99-1.00, Iron 0.68 -> sent)
- Removed the cap "wanted 4 minus marches out": rallies and returning marches use slots too, so Iron was never sent. Each resource is still sent at most once per run
- Hero "-" button: up to 3 looks per slot (the deploy screen can still be sliding in; once all 3 were missed at 0.26-0.33). Still missing -> screenshot in `logs/screenshots/farming/`
- [ ] Store popup dismissal (#12)

## Pet Adventure (new task, 2026-10-23)

- Flow: side panel > Daily tab > Pet Adventure row > Claim All > Hunt All > Ally Treasure (Share, Alliance Shares tab, Claim All) > close > back
- [x] Hidden row ("Hide after mission completion") = nothing to do: verified live
- [ ] Claim / Hunt All / Ally Treasure path: verify after the daily reset
- [x] Daily tab rows Recruit Heroes / Watchtower Intel: already covered by Auto Claim Hero (daily hero shards) and AutoHunt (intel missions)

## Collect Store Rewards / Collect Online Rewards split (2026-10-23)

- Collect Rewards renamed "Collect Store Rewards" (store, Deals, Bank; once a day). Store icon: 3 retries, threshold 0.78 (gem counter overlap), skips to Deals when missing
- New "Collect Online Rewards": Daily tab > Online Rewards (row pans to the chest bubble) + Gourmet Feast (popup > Claim)
- A pointing hand covers the chest bubble after the row tap; fallback taps the pan target (360,456), then checks "Next Chest Ready In"
- [ ] Verify a bot run of the pan-target fallback (manual tap worked); verify Gourmet Feast when its row reappears

## Journey (new task, 2026-10-23)

- Flow: Daily tab > Realm Journey row > supplies "+" > green Claim (free at 08:00/16:00 UTC) > close > Idle > max > start (leaves the screen by itself)
- Never taps the 1,000-gem "Daily Limit" button (outside the Claim search area)
- Skips while the town horse portrait shows its "N/30" counter (re-entering stops the idle journey); the portrait stays a while after it ends
- Idle Journey only starts with >= 10 supplies (OCR of the Idle popup number box)
- [x] Running skip and hidden Journey section (no supplies, nothing to claim) verified live
- [x] Claim + idle start verified live 2026-10-23 on the second character (claimed 10, started with 30). Needed fix #19 and a white-digit mask for the OCR (`SingleWord`, scale 2; the default inversion made the outlined digits vanish)

## Alliance Technology (rewritten, 2026-10-23)

- Flow: bottom bar Alliance > Tech > recommended tech (green thumb) > hold the teal "Contribute" (10,000 bread; 25 attempts, ~1 refill per 10 min) > close X
- Never taps the orange "Contribute 💎10" (gems): the teal colour is checked before every hold (the grayscale template matches both)
- [x] Navigation, grey-button skip and exit verified live (0/25 attempts)
- [x] Hold path verified live 11:08 (1 refilled attempt used, button turned grey, exit OK)

## Alliance Triumph (new task, 2026-10-23)

- Flow: Daily tab > scroll down > Alliance Triumph "Daily Activity Triumph" row > Personal Activity Triumph Chest (800/800) > dismiss Rewards > back
- Chest is only tapped while its red dot shows (and the unopened chest template matches); once a day, resets 00:00 UTC
- [x] Templates, red-dot gate and Rewards dismiss tested against the recording (claimed manually today)
- [x] Full run verified live 2026-10-23 on the second character (scroll, red dot 170 px, claim, dismiss, back)
- Chest template re-cut to the chest body only: the hero portrait and the "N/800" counter differ per account (0.88 before). Threshold 0.80: ~0.90 unopened, <= 0.60 opened

## Planned (to work out and discuss)

- [ ] **Per-task scheduling.** Partly done: daily gates and timers per task, `IScheduledTask` reports the next run, run history in `data/task_history.json` (branch `poc/task-dashboard`). Originally: every task ran on every cycle (Collect Store Rewards runs again even though the store is once a day). Each task needs its own schedule type:
  - once a day (e.g. store, Nomadic Merchant, VIP, Alliance Triumph; resets 00:00 UTC)
  - several times a day, at an interval (e.g. hunt, farming, alliance help)
  - at fixed clock times (e.g. Journey free supplies at 08:00/16:00 UTC)
  - Keep last run + result per task per character, so a restart doesn't repeat done tasks
- [ ] **New main screen (GUI).** In progress on branch `poc/task-dashboard` (2026-10-26): Bot tab = cycle controls + next cycle time, character list, per task last run / next run / play button; Settings > Tasks chooses which tasks show (hidden = never runs). Left: reorderable run order. The current screen only shows the running task. Replace it with a per-character overview of all tasks:
  - last run time + result (OK / failed)
  - next planned run (from the schedule above)
  - manual "run now" button per task
  - enable checkbox per task
  - reorderable task list (run order)
  - one view per character (tabs or a character selector)
- [x] **Character list** (2026-10-25). Change Account scans the Characters screen (one row per "Town Center Level" label, name/kingdom by masked OCR, active row = green tick) and keeps `characters` per account; Task Settings > Characters has a tick per character (new ones ticked). It runs first (switches away from an unticked character) and last (next ticked character not run yet this cycle, then restarts the list)
  - [ ] Verify a full cycle live: Asli -> Ilsa -> "all enabled characters done"
  - [ ] Lists longer than the panel (rows below y 1100) need a scroll: logged as a warning, not handled
  - OCR reads "]" of the alliance tag as l/J or drops it; capital I and l are the same glyph. Names are compared without the tag, I/l folded

## Quality (nothing in place yet)

- [ ] **Unit tests.** There are none: `Bot.Core.Tests/` has files but no .csproj and is not in the solution. Start with the pure logic: `CharacterRoster` name matching, daily gates / `GetNextRunUtc`, task ordering, OCR text parsing (march count, Suggestion Box answers)
- [ ] **Integration tests on screenshots.** Saved screenshots per screen (present / absent / popup variants) with the expected template scores and tap points, so a task's detection can be checked without the emulator. Catches threshold regressions like the BGRA swap (#19)
- [ ] **Code quality monitoring.** No static analysis or CI at all: add a build + test workflow (GitHub Actions on the fork), .NET analyzers / `dotnet format`, and a quality dashboard (SonarQube / SonarCloud) for bugs, smells and coverage

## Not done yet

- [ ] Red dots not handled: Events, Deals, Flamedragon, pets (paw), scales, bottom bar (Heroes, Backpack, Shop, Alliance)
- [ ] Human-like random delays
- [ ] Safety rule for every new claim: tap only template-matched free/claim elements, never price or gem-cost buttons
- [ ] Commit the work (only when asked)

## Truegold Crucible (new task, 2026-10-23)
- [x] Rows hidden once done -> panel closed, nothing tapped (verified live)
- [x] 7x Refine verified live 2026-10-23 on the second character (gems unchanged)
- [x] Discount Super Refine x1 verified live 2026-10-23 (badge gone afterwards)
