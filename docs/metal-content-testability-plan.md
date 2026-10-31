# Metal content testability plan

Status: **Milestones A-E implemented and manually accepted for content testing** (2026-10-23). A is
manually accepted, B's blood path is validated, and the user confirmed the latest normal Metal run
(Meitou water, low-res particles, object motion and reactive-mask paths). Remaining work is polish and
optimization: HDR/EDR presentation, fog-rate VRS, particle coverage, minor water shading differences,
the GI decision, and performance/default cleanup. This plan is about making the game's content and
systems easy to exercise through the native Metal host. Read it with [metal-backend](metal-backend.md);
Vulkan remains the reference path.

The content data and simulation are already shared. Metal loads the same world, population,
towns, items, dialogue, trade, crafting, construction, farming, combat and saves. The gaps below
are mostly host wiring in `GameHost.Metal.cs`, plus a few renderer-only features that affect
visual checks. This plan does not include GI work, and it does not change `--renderer vulkan`.

## Priority summary

| # | Area | Priority | Blocks content testing |
|---|---|---|---|
| 1 | Sandbox, duel, crowd and startup new-game state | P0 | Yes |
| 2 | F10 debug menu | P0 | Yes |
| 3 | Camera follow, tours and character flights | P0 | Yes |
| 4 | World event feedback | P0 | Yes |
| 5 | Blood, debris and luckyren Mode | P1 | Done; debris gameplay check passed |
| 6 | Deterministic stills and UI showcase | P1 | Yes for repeatable checks |
| 7 | Metal settings parity and no-op controls | P1 | Partly |
| 8 | Renderer-only content-check gaps | P2 | No |

P0 means "cannot easily reach or observe a large part of the game". P1 means "content can be
tested, but feedback or repeatability is compromised". P2 means "the game is testable, but the
picture is not at Vulkan parity yet".

## Goals

- A tester can start a normal game, a loaded save, a sandbox, a duel or a crowd and see the
  expected actors without hand-editing files.
- A tester can reach characters, items and combat states quickly and observe the result.
- Repeated runs of the same content check produce the same simulation frame.
- Settings that are visible in the menu either work on Metal or are clearly marked unavailable
  for Metal; no visible control silently does nothing.
- Vulkan behaviour and output stay unchanged.

## Non-goals

- Porting the full Vulkan post chain, GI, HDR/EDR or vendor upscalers. Those are tracked in
  [metal-backend](metal-backend.md) and item 8.
- Changing simulation rules or adding new content.
- Replacing the Metal-specific frame loop with the Vulkan loop. The host may call shared methods,
  but it keeps its Metal display and post chain.
- GI: Metal GI remains opt-in via `MEITOU_METAL_GI=1` and is outside this plan.

## Current baseline

Normal gameplay works on Metal today:

- world, terrain, objects, buildings, foliage and streaming
- population, towns, NPCs, animals, pathing and combat logic
- dialogue, trade, inventory, loot, squads and the map
- crafting, construction, farming, mining and prospecting
- save/load, menu New Game, title and character editor
- HUD panels, most interaction, audio, weather and day/night
- faithful water, shadows, particles and object/foliage streaming

Milestones A-C are wired in the Metal host:

- `SandboxSpawn`, `DuelStart`, `SpawnCrowd` and startup `FinishCreation`
- `FollowCamera`, `SandboxFollowCamera`, `EditorCamera`, `SquadTour`, `player.FlyTo` and
  `player.SkipFlight`
- `WorldNews`
- `DrawDebugMenu`, `DebugMenuKey` and `DebugMenuText` (Ctrl+F10; F10 remains settings)
- `SetUpBlood(IGpuContext)` and `UpdateBlood`/`UpdateDebris`/luckyren Mode
- the shared `PrepareStill` path for `--ticks`, `--move-to` and `--ui-showcase`; the Metal still
  loop freezes the simulation after preparation

Still absent or only partly wired:

- `Cinema` and the Metal frame profiler
- the settings callbacks listed in item 7

## Item 1: Sandbox, duel, crowd and startup new-game state

### Why

These modes are the fastest way to inspect animation, combat, character rendering, equipment and
NPC scale. Vulkan sets them up in `GameHost.BootPlayer`; Metal does not.

### Changes

- In `GameHost.Metal.cs`, mirror the Vulkan boot ordering:
  - set `crowd = o.Crowd; o.Crowd = 0` before `WorldFrame.CreateGpu` so a `--crowd` run uses the
    simulation, not only the renderer's posed harness;
  - after the session and population exist, choose the start target:
    - `--sandbox`: `SandboxSpawn()`
    - `--duel`: `DuelStart()`
    - ordinary `--new-game`: the existing `StartPlayer` path
  - after the start target is known, call `SpawnCrowd(target, crowd)` when requested and the
    population exists.
- For an interactive startup `--new-game`, enqueue `FinishCreation` as Vulkan does. Without it,
  `Player.InCreation` remains set and town discovery is skipped.
- Call the mode's per-frame work:
  - sandbox: `SandboxKey` on input, `SandboxTick`, `DrawSandbox`
  - duel: `DuelFrame`, `DuelHoldClock`, and Backspace to request a restart
- Keep `--crowd`'s renderer harness available as a diagnostic, but do not let it stand in for the
  simulated crowd on a normal `--crowd` run.
- The camera target and navmesh startup must follow the selected mode, not the generic scene
  focus.

### Acceptance

- `--sandbox 2 --sandbox-mode fight` shows two characters fighting; `G` starts the fight, `Z/X/C`,
  `P` and `Y` behave as in Vulkan.
- `--duel` shows the two Samurai; Backspace restarts a lap; the same lap reaches the same state
  hash.
- `--crowd 100 --new-game Wanderer` has simulated NPCs, not just the posed harness.
- Startup `--new-game` clears `InCreation`; town discovery works.
- No mode action is required for a normal save or a menu New Game.

### Suggested commit

`metal: wire sandbox, duel, crowd and startup creation state`

## Item 2: F10 debug menu

### Why

The debug menu is the shortest path to testing content that otherwise needs a long play session:
teleport, spawn, items, money, healing, skills and damage.

### Changes

- Reuse `GameHost.DebugMenu.cs` directly:
  - add `debugMenuOpen` state to the Metal loop;
  - route keys and text to `DebugMenuKey` / `DebugMenuText` while open;
  - call `DrawDebugMenu` over the finished picture;
  - ensure the menu takes every key while open, as Vulkan does.
- Resolve the F10 conflict explicitly. Recommended: keep **F10 = settings** on Metal and default
  the debug menu to **Ctrl+F10**. Honour a user rebinding of `InputAction.ToggleDebugMenu` if one
  exists, and document the Metal default in the input help.
- The menu uses the camera target and the selected/inspected characters; those already exist in
  the shared `PlayerInterface`.

### Acceptance

- Open the menu, teleport the selected squad, spawn a hostile NPC, give an item, add money, heal,
  max skills and apply damage.
- The menu closes on Esc/Ctrl+F10 and does not leak keys into the camera or UI.
- Opening settings still works with F10.

### Suggested commit

`metal: add the shared debug menu with a non-conflicting key`

## Item 3: Camera follow, tours and character flights

### Why

Selecting a character is not enough if the camera does not follow it. Without the follow path,
the normal new-game and save-load camera can stay behind while the leader walks away. Tours and
flights make it practical to inspect NPCs and squads across the map.

### Changes

- Call `FollowCamera()` once per frame after the camera is applied and before the world draw,
  matching Vulkan's ordering. This applies `player.Follow`, which is already set by:
  - double-tapping a squad member;
  - `FollowSelected()` after a load;
  - the new-game editor close.
- Call `EditorCamera()` while the character editor is open.
- Wire the Advanced camera tools:
  - `SquadTour` on numpad `/`;
  - `player.FlyTo` and `player.SkipFlight` for double-click flights and the same-member
    triple-press shortcut;
  - camera-code copy/paste in free camera (Ctrl+C/Ctrl+V).
- Keep `Cinema` (F6 editor/recording) as a later sub-item if it needs more display/post work; it
  is useful for content videos but not required for normal testing.

### Acceptance

- Select a squad member and double-tap their number: the camera follows while they walk.
- Load a save, order the leader to move: the camera follows without manual panning.
- Double-click a distant squad member: the camera flies or cuts to them consistently with the
  "Character flights" setting.
- Numpad `/` tours the squad and ends cleanly.
- Free-camera Ctrl+C/Ctrl+V round-trips a camera pose.

### Suggested commit

`metal: follow the selected character and wire squad navigation`

## Item 4: World event feedback

### Why

The simulation already posts events, but Metal never drains them to the HUD/log. A tester can
miss town discovery, knockouts, fights and wild-squad activity even though the simulation changed.

### Changes

- Call `WorldNews()` once per frame after the world tick and before drawing, guarded by the HUD
  being present, exactly as Vulkan does.
- Confirm the helper's dependencies are available on Metal:
  - `session.World.TakeMessages()`
  - `population.TakeTownLog()` / `TakeWildLog()`
  - `aggro.TakeLog()`
  - `CarrySystem.TakeKnockouts()`
- Keep the console log lines; they are the fallback when the HUD is hidden.

### Acceptance

- Walk a new squad into a town and see the discovery/exploration notice.
- Knock out an NPC and see the `*Thunk*` / `Failed!` notice.
- Start or observe a fight and see the relations/aggro line in the log.
- A town or wild-squad activity produces its log line.

### Suggested commit

`metal: drain world messages and event logs`

## Item 5: Blood, debris and luckyren Mode

### Why

Combat runs and blood decals/debris are wired on Metal, including luckyren Mode. Debris uses the shared
atmosphere-lit native path (MEP-3); the Hub/desert gameplay check passed, with explicit bone/limb visibility still an
optional luckyren check.

### Changes

- Change `GameHost.SetUpBlood(GpuContext context)` to accept `IGpuContext`; the renderers it
  constructs already use the neutral context, so Vulkan should be unaffected.
- Call `SetUpBlood(ctx)` on Metal after the world renderers exist and before the first frame.
- Call `UpdateBlood()` once per frame before `WorldFrame.Draw`; this also:
  - applies `config.LuckyrenMode` to `world.Luckyren`;
  - drains `world.BloodMarks`;
  - ages the decal store;
  - advances debris by game time.
- Leave `BloodForNewWorld` and `WriteDecals` in the shared save path; they will now have a store
  to use on Metal.
- Confirm `gpu.Decals`, `gpu.DecalStore` and `gpu.DebrisPieces` are disposed by `WorldFrame.Gpu`.

### Acceptance

- Damage a character: blood marks appear on the terrain.
- Kill a character with `luckyren Mode` on: blood, flying bones/limbs and the burst particles
  appear; gear drops.
- Save and load with decals: the saved decals return.
- Blood off: no decals or bursts; combat still works.
- Debris and decals disappear with the world on save/load/main-menu transitions without a leak.

### Suggested commit

`metal: enable blood decals, debris and luckyren`

## Item 6: Deterministic stills and UI showcase

### Why

Vulkan's still path lets a content check run a known number of simulation ticks and move a squad
before the picture. Metal used to wait roughly 40 real frames, so the same command could show a
different state; `--ui-showcase` was also absent. The shared preparation now runs first, then the
Metal warm-up frames render without advancing the simulation.

### Changes

- Extract the still preparation from `GameHost.Screenshot.cs` into a shared helper usable by both
  hosts:
  - select the player squad for `--ui-showcase`;
  - apply `--move-to` through a `MoveOrder` and advance the requested ticks;
  - advance `Math.Max(g.Ticks, g.UiShowcase ? 1 : 0)` control ticks and the matching simulation
    time;
  - handle title/editor panel setup where applicable.
- Call the helper from the Metal still path before its frame loop. Keep `--ui-panel` working.
- If full `--ui-showcase` needs the screenshot panel matrix, add it as a separate sub-item rather
  than blocking `--ticks`/`--move-to`.
- Consider a Metal-only CPU timeline in the stats panel if `FrameProfiler`'s Vulkan timestamp
  dependency is not worth generalizing yet. The important gate is deterministic content state,
  not the profiler chart.

### Acceptance

- Two runs of the same Metal `--screenshot --ticks N --move-to X Z` command end on the same
  simulation tick and character positions.
- `--ui-panel inventory`, `trade`, `talk` and `hud` still capture the expected panel.
- `--ui-showcase` writes the same set of panel pictures as Vulkan (or is explicitly marked
  unavailable while the rest works).

### Suggested commit

`metal: share deterministic still preparation`

## Item 7: Metal settings parity and no-op controls

### Why

The shared settings menu exposes many controls, but Metal supplies only a subset of the graphics
values and does not wire some callbacks. This makes content checks misleading: a toggle can look
enabled while nothing changes.

### Changes

- Add the missing graphics values to `CreateMetalSettings` where the Metal renderer supports
  them:
  - **Time of day** and **time speed**, using `session.Clock` (and `duelHour` for a duel). The
    shared Advanced page currently filters these labels out, so add the row there as well;
  - **weather particles** and **particle density**, using `gpu.WeatherParticles` and
    `gpu.ParticleDensity`.
- Wire the shared controls that already exist in `SettingsMenu` but have no Metal host hook:
  - **camera rotation speed** -> `CameraRotateSpeedChanged` and
    `session.Camera.Settings.RotateSpeedX/Y`;
  - **keep textures at characters** -> `gpu.Kept` from squad positions;
  - **towns keep their schedule** -> include `ResidentSchedule` in the `OffscreenChanged`
    tuple.
- For controls whose renderer path is absent, hide them on Metal or label them explicitly rather
  than presenting a no-op. The water reflection, Meitou water, luckyren, character flights and
  hardware-mouse controls are now wired.
- Keep the shared `SettingsMenu` pages (Interface, Controls, Advanced, Audio, Fixes); only the
  values supplied by Metal are reduced today.

### Acceptance

- Every visible setting changes the running Metal game immediately or is clearly marked
  unsupported.
- Time of day/time speed moves the sun and weather as expected.
- Camera speed, resident schedule, keep-textures and particle density each show
  an observable effect.
- Character flights and luckyren are not presented as working before their items land.
- Vulkan's settings and saved keys are unchanged.

### Suggested commit

`metal: complete the settings values and callbacks`

## Item 8: Renderer-only content-check gaps

### Why

These do not block gameplay, but they make Metal/Vulkan content comparison incomplete. Keep them
out of the P0 content-testing gate.

### Known gaps

- The shared water reflection pass now runs on Metal; the scene is mirrored into the water.
- Meitou water (FFT/shore/foam/reflection/refraction) now runs on Metal. Metal omits water foam sun
  shadows to fit its 16-sampler stage.
- Low-resolution particle targets are shared; the Metal near-plane close pass is still skipped.
- No fog-rate (fog shading-rate) target.
- Object motion (grass/characters) is shared; MetalFX gets the water reactive mask, while particle coverage remains absent.
- No HDR/EDR output.
- The Meitou cloud renderer is seam-neutral; compare its parity views with Vulkan.
- Metal GI remains experimental and opt-in; do not make it part of content testability.

### Work order

1. Water reflection only if a content pass genuinely needs mirrored scenery.
2. Meitou water and cloud/fog parity once the content gate is closed.
3. Fog rate as performance/visual parity work.
4. Particle coverage in the upscaler's reactive mask.
5. HDR/EDR at presentation time.

### Acceptance

- A fixed camera/time/weather comparison against Vulkan has no unexplained content difference
  (objects, foliage, characters, buildings, water surface) beyond the known renderer list.
- Any remaining difference is documented here or in [metal-backend](metal-backend.md).
- None of these changes alter Vulkan output.

## Execution order

### Milestone A: test access

Items **1, 2, 3, 4**.

Gate: normal game, loaded save, sandbox, duel and crowd are all usable; the debug menu and
follow/tour controls work; world events are visible.

### Milestone B: combat feedback

Item **5**.

Gate: blood, debris and luckyren appear, survive save/load where applicable, and do not leak.

### Milestone C: repeatable checks

Item **6**.

Gate: `--ticks` and `--move-to` produce the same state on repeated Metal runs; existing
`--ui-panel` still works.

### Milestone D: settings honesty

Item **7**.

Gate: no visible Metal setting is a no-op; unsupported controls are hidden or labelled.

### Milestone E: visual parity

Item **8**.

Gate: fixed-view Metal/Vulkan content comparison is documented; Vulkan remains unchanged.

## Verification

Run the normal Release build and the quick suite after each milestone:

```bash
dotnet build src/Meitou.Game -c Release
dotnet test tests/Meitou.Tests/Meitou.Tests.csproj -c Release \
  --filter "Category!=Slow&Category!=Bench"
```

Manual Metal checks:

```bash
# normal game and save/load
DYLD_LIBRARY_PATH=/opt/homebrew/lib dotnet run --project src/Meitou.Game -c Release -- \
  --renderer metal --windowed --size 1512x856 --load <save-folder>

# sandbox: animation and combat
DYLD_LIBRARY_PATH=/opt/homebrew/lib dotnet run --project src/Meitou.Game -c Release -- \
  --renderer metal --windowed --sandbox 2 --sandbox-mode fight

# duel
DYLD_LIBRARY_PATH=/opt/homebrew/lib dotnet run --project src/Meitou.Game -c Release -- \
  --renderer metal --windowed --duel

# simulated crowd
DYLD_LIBRARY_PATH=/opt/homebrew/lib dotnet run --project src/Meitou.Game -c Release -- \
  --renderer metal --windowed --crowd 100 --new-game Wanderer

# deterministic still
DYLD_LIBRARY_PATH=/opt/homebrew/lib dotnet run --project src/Meitou.Game -c Release -- \
  --renderer metal --screenshot /tmp/metal-content.png --windowed \
  --new-game Wanderer --ticks 120 --move-to 100 100
```

For each item, also run a short Vulkan smoke check of the same feature to confirm the shared
change did not alter the reference path. Do not run whole-device GPU captures; use the existing
targeted build/test and short runtime checks.

## Definition of done

- A tester can reach normal gameplay, sandbox, duel and crowd on Metal without editing files.
- The debug menu, follow camera, squad navigation, world event feed, blood/debris and luckyren
  Mode are available.
- Repeated stills can be made deterministic with `--ticks` and `--move-to`.
- Every visible Metal setting either works or is labelled unsupported.
- Buildings, foliage and character content are visible and consistent with Vulkan for the tested
  scenes.
- Release build 0 warnings / 0 errors; quick suite passes; Vulkan reference is unchanged.
- This document and [metal-backend](metal-backend.md) reflect what is implemented and what
  remains.

## Risks and guardrails

- **F10 conflict:** keep settings and debug on separate keys; do not silently steal settings.
- **`SetUpBlood` signature:** change it to `IGpuContext`, not a copy of the Vulkan path.
- **Crowd semantics:** the existing `CrowdHarness` is a renderer stand-in; keep it clearly
  separate from the simulated `--crowd` path.
- **Boot ordering:** sandbox/duel/crowd must be set up before the camera, navmesh and first
  streamer settle, as in Vulkan, or the first frame starts in the wrong place.
- **Metal-only changes:** keep them behind `MEITOU_METAL`; Vulkan must compile and run unchanged.
- **GI is out of scope:** do not enable experimental Metal GI for content testing.
