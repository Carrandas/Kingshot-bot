---
name: finders-fortune-solver
description: FindersFortuneSolver - play the Kingshot "Finder's Fortune" hidden-object event on the LDPlayer emulator (a solver tool taps known items, Claude finds new ones), then claim Daily Missions and Finder's Guide rewards. Use when the user asks to play Finder's Fortune / the finder game / spend bones.
---

# Finder's Fortune solver

Use the solver tool for everything in `item-locations.txt`. When it stops on a new item, look at its screenshot, find the item, `learn` it, and the tool carries on. This loop cleared 6-7 stages per character in about 15 minutes, including two brand-new maps.

## Setup
- adb: `/c/LDPlayer/LDPlayer14/adb.exe -s emulator-5554`. Screenshot: `exec-out screencap -p > <scratchpad>/p.png`, then Read it.
- **Check `adb devices` and `shell wm size` (must be 720x1280) first.** LDPlayer has come back with a reset `vms/config/leidian0.config` (no `basicSettings.adbDebug`, 2560x1440). Fix:
  1. `ldconsole quitall`.
  2. Add `"basicSettings.adbDebug": 1,` back to the config.
  3. `ldconsole modify --index 0 --resolution 720,1280,320 --cpu 4 --memory 4096` (2 CPUs / 2 GB was too slow for the Bear Hunt: screenshots timed out, recoveries).
  4. `ldconsole launch --index 0`, then wait for `adb devices`.
  5. `ldconsole runapp --index 0 --packagename com.run.tower.defense`.

  Do this only while the bot is idle.
- KingshotAuto.exe running? Taps collide with its own. Fine while its log shows `Wait time remaining: N minutes` with plenty of time left (tail the newest `logs/logs*.txt`); otherwise ask the user to stop the cycle. Then pass `--bot-idle`.
- Open the event from the base screen: the "Finder's Fortune" icon at the top right (555,155). The tool expects the Finder's Fortune tab.
- If the screen changes without your taps (a Settings page, another tab), the user is using the emulator: stop tapping and ask.

## Solver tool
Build: `dotnet build tools/FindersFortuneSolver/FindersFortuneSolver.csproj -c Debug`. Exe: `tools/FindersFortuneSolver/bin/x64/Debug/net10.0/FindersFortuneSolver.exe`. Pipe through `grep -E "STATUS|learned|MISS" | tail -6`: the rest is OCR noise.

- `run --stages N --map NAME --no-flash --bot-idle`: plays while every name is known. Finishes the stage in progress and starts at most N new ones (10 bones each; default 0). The count restarts with each call, so pass what's still wanted. It never taps an unrecognised screen and never spends bells.
- `learn "<map>" "<item>" x y ["<item>" x y ...] --then-run --stages N --map NAME --no-flash --bot-idle`:
  - Taps each item, writes it to the file only if the game crossed it off (moving the line if it already exists), then goes straight back to playing.
  - Learn every unknown on screen in one call: a miss on one doesn't undo the others.
- Always pass `--no-flash`. The built-in flash wait costs more than it saves.
- Taps in one batch are 100 ms apart. Faster taps got dropped.
- `capture <png>` / `analyze <png>...`: read a live or saved screenshot, no taps.
- Spots over the Time's Up buttons (bottom ~100-110 px of the scene, y ≈ 890-1090) are tapped alone and only with 6 s or more left.

Statuses (the timer keeps running on UNKNOWN and MISS, so be quick):
- `DONE`: nothing to do, or `run` again.
- `UNKNOWN names=...`: Read the `shot=` file, find the items, `learn` them.
  - Can't see one? It glows after ~10-20 s: take a fresh screenshot and tap the glow.
  - The magnifier (655,1085) shows a hint at once (the badge counts hints).
  - The user may tap glowing items themselves; then just `run` again.
- `MISS item=...`: the saved spot didn't register twice (some items sit a little off between stages, e.g. Duck). Find it in the shot and `learn` the new spot.
- `TOO_LATE`: the item is over a Time's Up button and the timer is low. Wait for Time's Up (see Timeout).
- `NOT_SHOWING`: the name is already gone (often the user tapped it) or the screen changed. Take a screenshot and continue.
- `MAP_UNKNOWN`: pick the map from the shot, `run --map "<name>"`.
- `STOPPED_ON_OTHER_SCREEN`: Read the shot (the timer is paused):
  - Time's Up: see Timeout.
  - Chapter clear: see Rewards.
  - "Logged in on another device": stop and tell the user.
- `OUT_OF_BONES`: claim Daily Missions, or stop.

## Maps
- `item-locations.txt` (next to this file): `map | item | x,y | note`. `maps/` holds thumbnails the tool uses to recognise a map.
- The same chapter number uses a different map per character:

  | Character | Chapter 4 | Chapter 5 |
  |---|---|---|
  | llsa | Cabin? (yak, parrot, paw sign) | Archery Range (targets, bow banner) |
  | Asli | Smithy (forge, anvil) | Arena (crown tower, gate) |

- **Always pass `--map`.** Name matching alone picked "Training Camp?" for llsa's new Archery Range (shared names like Boots/Pliers). For a chapter you haven't seen, check its thumbnail on the map screen (bottom left) and choose a new name.
- Spots are fixed per map; each stage shows some of them, plus a few new ones.

## Game loop (the tool does steps 1-5)
1. Map screen: **Start Game** (486,1190) costs 10 bones (top right counter). A new chapter shows **Start** (486,1176) first. It can be greyed for a moment after a chapter clear; take a fresh screenshot.
2. "Stage X-Y" intro: tap (360,900).
3. Three names at the bottom; the progress bar shows the stage total (6-18 items, ~60 s).
4. A crossed-off name is replaced by a new one.
5. "All items found": **Continue** (360,920), then back to the map.

## Timeout (Time's Up)
- Screen: Give Up / Extra Time cards with buttons at y≈940, "Only N item(s) to find!" under the buttons.
  - **Give Up** (208,940) opens "Proceed?" and returns 5 bones; the stage resets.
  - **Extra Time** (510,940) opens "Tips": **Confirm** (520,773) spends 100 bells for +25 s and 1 hint. Tap (360,900) to resume, then `learn`/`run` again.
- Use Extra Time once per stage when the remaining items are few or known. Bells are only good for this event, so on the last day spend them freely; earlier, ask before a second one on the same stage.

## Rewards
- **Daily Missions** (644,405 on the Finder's Fortune tab):
  - One **Claim** (581,356) claims every finished mission; tap (360,1150) to close the popup, X (665,153) to leave.
  - "Log in" gives 60 bones; "Find N items" fill up while playing, so claim again after.
- **Chapter clear**:
  1. **Claim** (360,1125).
  2. "Finder's Way Upgrade" → **Go Claim** (360,802).
  3. Tap the new level's two reward icons (each followed by a tap at (360,1150)).
  4. Swipe the tab row right (150→650 at y=145), then the Finder's Fortune tab (136,145).
- **Finder's Guide** tab (538,145): tap each lit reward in the free left column, then tap (360,1150) to close the popup. The list scrolls after a claim, so screenshot before the next tap. The other columns are the paid pass.

## Interruptions
- "The account has been logged in on another device": **don't** press Reconnect. The user is on their phone; stop and tell them.
- Alliance notice banners scroll across the top; ignore them.
