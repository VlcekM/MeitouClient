# Overnight research: summary

Two parts, both done:

- **Part A:** a full decompile of the game binaries, with tables and a naming pass. It lives outside the repo in
  `R:\VlcekM\MeitouClient-re\ghidra\`.
- **Part B:** ten new research docs on this branch (`docs`, worktree `R:\VlcekM\MeitouClient-docs`), each written
  by one Sonnet research agent and then checked by a second Sonnet agent whose job was to refute every **Verified**
  claim.

Docs-only change. `dotnet build` and `dotnet test` pass (see the log).

**How to merge:** on master, run `git merge docs`. The branch adds `PROGRESS.md` and `DECISIONS.md` at the root
for this run. Delete them after merging, or keep them as a record. The branch also edits `ROADMAP.md` (phases
4-7) and `docs/README.md` (new rows), so a conflict there is possible if master changed those lines meanwhile.

## Part A: decompile dump (outside the repo)

- `dump/kenshi/` and `dump/kenshi-named/` hold all 166,720 functions of `kenshi_x64.exe`. `dump/ogre/` has 40,194
  and `dump/skyx/` has 1,277. There are 0 failures: 11 slow functions needed a 900 s timeout.
- Each dump has `index.tsv`, `callgraph.tsv`, `strings.tsv`, `data_xrefs.tsv` and `rtti.tsv`. There are 2,820 RTTI
  classes, which are the game's own C++ class names.
- The naming pass in `project-named` gave names to about 16,000 functions:
  - vtable slots, as `<Class>::vfunc_<n>`;
  - 805 functions that read FCS fields, as `uses_<field>` / `read_<field>_n<k>`;
  - 137 names from the docs.
  The full list is in `dump/renames.tsv`.
- `ghidra/catalog.tsv` is the docs' address catalog merged with the research agents' rows: 918 addresses with
  names and one-line descriptions.
- Start with `ghidra/dump/README.md`, which has the layout, grep recipes and how to reproduce the dumps.

## Part B: per subsystem

The counts come from each verifier's report. Its columns are:
- **confirmed**: Verified claims the verifier re-checked and kept;
- **downgraded**: claims moved to Observed or Unknown;
- **fixed**: errors corrected.

| Subsystem | Doc(s) | Verified, main facts | Main unknowns | confirmed / downgraded / fixed |
| --- | --- | --- | --- | --- |
| Save format | [formats/save.md](docs/formats/save.md) | Saves are in `%LOCALAPPDATA%\kenshi\save\<name>\`; all files are FCS type-15; handle encoding (building C = Y*64+X+1); six state records per character; faction relation matrix rule (explicit, else min of the defaults, self 100). Checked on 70 real save files. | Two long `quick.save` slot lists; player-built bases, prisoners, bounties (not in the sample); load with a changed mod list | 42 / 2 / 19 |
| Game loop and time | [game/game-loop.md](docs/game/game-loop.md) | Variable timestep: dt capped at 0.05 s and smoothed over 20 frames; speeds 1/2/5 (F2-F4, Space pauses); clock 109.09 s per game hour, 100 days per year; character update budget; zone activation box +-340 | New-game start time; zone unload timers; physics substeps at 2x | 38 / 3 / 8 |
| Characters | [game/character-stats.md](docs/game/character-stats.md) | Stat numbering 1-38 and offsets (accessor and save writer agree); race stat multipliers 1.2 good / 0.8 bad; XP constant 0.02; hunger thresholds; KO point formula; LOCATIONAL_DAMAGE table; limb replacement stat mapping; encumbrance formula | Units of the medical update dt; pain source; some XP events | 16 / 2 / 12 |
| Combat | [game/combat.md](docs/game/combat.md) | CONSTANTS combat fields and scalings; record counts; armour and weapon quality fields; HealthPartStatus save keys; LOCATIONAL_DAMAGE multipliers; difficulty damage and death-chance globals | Attacker technique choice, attack-slot AI and targeting; KO wake-up; turret and bow maths; no comparison with in-game numbers yet | 8 / 3 / 11 |
| AI | [game/ai.md](docs/game/ai.md), [game/ai-tasks.md](docs/game/ai-tasks.md) | AI_TASK and AI_PACKAGE counts; goal weight = val2 x 0.01 (0 = 1.0); role-list fill rules; signal-to-package-class table (24 cases); task factory table (290 types); TaskData registry (253); scorer constants | Condition list roles; tiers 0 and 4; the planner's search and costs; dialogue effect details | 24 / 6 / 20 |
| Squads, factions, towns | [game/factions-squads-towns.md](docs/game/factions-squads-towns.md) | Relation layout and save keys; initial relation rule; ally >= 50, hostile <= -30; relation change table by event kind; area cells (64x64, 4608 units); nest cap 0.7N | Raids and campaigns; resident squad creation; nest respawn; who enforces max squad sizes | 22 / 1 / 4 |
| Pathfinding | [game/pathfinding.md](docs/game/pathfinding.md) | Havok AI 2014.2 tagfile tiles (3,995 files) in `navtiles`; seeds.def; tile uid (Z<<8)\|X; navmesh thread; path query order and result codes; water cost modifier; speed constants | Interior filter and uids; generator job types; tagfile face/edge layout | 22 / 4 / 13 |
| Economy | [game/economy.md](docs/game/economy.md) | Item value function (weapon logistic curve, armour quadratic, slot factors); armour class and material price tables; CONSTANTS price block; trader and town market factors; shop sizing and restock; bounty lifetime 0.04 x amount; slave and animal prices | Market factors on load; the stolen predicate; shop weapon quality roll | 36 / 5 / 12 |
| Buildings and production | [game/buildings-production.md](docs/game/buildings-production.md) | BuildingFunction 0-30 and MiningResource enums; CONSTANTS build, production and research values; build time formula; production rate table; power generation and consumption; farm data | Build rate in the building update; fuel burn unit; crafting quality curve; option slider defaults | 14 / 2 / 11 |
| UI and input | [game/ui-input.md](docs/game/ui-input.md), [game/ui-screens.md](docs/game/ui-screens.md) | `controls.cfg` (73 actions; 71 defaults match the exe); binding code bits; key-name table; MyGUI 3.2.3, 52 layouts and their layers; context menu verbs; settings names | Tutorial id lists; some event targets; tab widget details | 30 / 4 / 14 |

Each doc ends with `## Unknowns` and an `## Implementation outline`, which gives the order to build the subsystem
in Meitou.

## Status
- [x] B0 worktree + branch `docs` from master f127922
- [x] B1 research workflow: 10 subsystems, 20 Sonnet agents, 0 errors
- [x] B2: catalog merged (918 rows); `docs/README.md` rows; ROADMAP research status; `overview.md` / `fcs-mod.md`
      now point to `save.md`; cross-doc consistency pass (14 contradictions resolved against the decompile, 7 broken links fixed, labels normalised); build and test; commit

## Log
- 01:25 UTC B1 workflow launched (run wf_62e10660-789, 4 subsystems at a time, research then verifier per
  subsystem). Script: C:\Users\Admin\.claude\projects\R--VlcekM-MeitouClient\07543453-875f-4071-afd0-2aa900997a26\workflows\scripts\meitou-docs-research-wf_62e10660-789.js
  Agents used dump/kenshi + renames-current.tsv, or dump/kenshi-named once `dump/kenshi-named/COMPLETE` existed.
- 08:15 UTC research done+verified: combat, characters, save, gameloop.
- ~09:50 UTC workflow finished: all 10 subsystems researched and verified.
- dotnet build: 0 warnings, 0 errors. dotnet test: 205 passed, 0 failed, 0 skipped (KENSHI_PATH set).
