# Factions, squads, towns and population spawning

Sources:

- **Binary**: `kenshi_x64.exe`, the Steam build (decompile dump, naming pass applied; addresses like `FUN_1406b2d20`
  are functions of that dump, `Class::vfunc_n` are MSVC vtable slots from RTTI). Areas read: the
  `FactionRelations` / `PlayerFactionRelations` / `Faction` classes (0x1406b2xxx - 0x1406bxxxx, 0x1407fe270), the
  squad factory (`RootObjectFactory::createRandomSquad`, 0x140583a10, named in its own log strings), the area-sector
  population code (0x1408f5xxx - 0x1408fexxx), the town and nest classes (0x140926xxx - 0x14093xxxx), world-state
  evaluation (0x1409a7e00, 0x1409a9570, 0x1409a9a70), `BuildingItemGroup` (0x140549xxx, 0x1405534f0).
- **Data**: `data/gamedata.base` plus the three core mods through `GameDatabase.Load(LoadOrder.FromInstall)` (probe
  `R:\VlcekM\MeitouClient-re\probes\factions`); field meanings from `fcs.def` (descriptions are the designers' text
  and sometimes stale, see "Data corrections"); enum names and numbers from the editor's own enums (the decompiled
  `forgotten construction set.exe`: `TownType`, `CharacterTypeEnum`, `SquadMemberType`, `DialogActionEnum`,
  `BuildingDesignation`, `CrimeEnum`).
- Counts below are for the **merged** base-game database (a record that a mod modifies counts once), so they are lower
  than the per-file counts in [fcs-mod.md](../formats/fcs-mod.md#record-types).

Related: [zones.md](../formats/zones.md) (town placements, GAMESTATE_TOWN), [save.md](../formats/save.md) (the saved
relation matrix, platoons, towns), [game-loop.md](game-loop.md#factions-and-squads) (how factions and platoons are
stepped per frame), [characters.md](../characters.md#generating-a-character) (what happens to each character of a
squad), [weather.md](../formats/weather.md) (BIOME_GROUP regions from `areasmap.tga`).
The AI that runs squads and the `AI packages` they carry is in [ai.md](ai.md) and [ai-tasks.md](ai-tasks.md); walking, `squad formation` and the 4608-unit grid in [pathfinding.md](pathfinding.md);
trade, shop stock, `trade culture` and bounties in [economy.md](economy.md); the RACE fields and body model of a spawned character in [character-stats.md](character-stats.md);
the new-game start-offs in [ui-screens.md](ui-screens.md#3-main-menu-and-new-game-flow); building designations and production in [buildings-production.md](buildings-production.md).

Labels: **Verified** = checked against game data or by two independent places in the binary (said how);
**Observed** = one reading of one function (or a pattern in data); **Unknown**.

## 1. Object model

| Game class (RTTI) | Role | Notes |
| --- | --- | --- |
| `Faction` | one per FACTION record (103 in the base game, the player's included); owns its record (+0x240), a `FactionRelations` (+0x78), the platoon lists, `FactionUniqueSquadManager` | `FUN_1407fe270` is the loader from the record (**Observed**). Player flag: the field at +0x250 is non-null only for the player's faction (**Observed**, also in [game-loop.md](game-loop.md)) |
| `FactionRelations` / `PlayerFactionRelations` | the relation table of one faction towards all others | section 3 |
| `Platoon` -> `ActivePlatoon` (characters exist) / `UnloadedPlatoon` (abstract) / `UniquePlatoon` | a squad | per-frame stepping is in [game-loop.md](game-loop.md#factions-and-squads); creation is section 5 |
| `TownBase` -> `Town`, `Nest` | a TOWN record placed in the world; nests are TOWN records of `type` 0 spawned by the game | section 7; `Town`/`Nest` share almost all virtuals, the nest overrides slot 103 (wiped out) |
| `BasePopulationManager` -> `TownPopulationManager`, `NestPopulationManager` | per town: two pools (residents, roaming) with a population budget and a `SpawnInfoList` | section 6.4 |
| `SpawnInfoList` | the weighted list of squad templates a pool or biome group may spawn | section 6.1 |
| `AreaSector`, `AreaBiomeGroup` | one cell of a 64 x 64 grid of 4608-unit cells, and the BIOME_GROUP data it belongs to | section 6.2 |
| `BuildingItemGroup` | an ITEM_PLACEMENT_GROUP placed in a building: one item slot with a respawn timer | section 9 |
| `FactionWarMgr`, `CampaignData`/`CampaignInstance` | faction campaigns (FACTION_CAMPAIGN, DIPLOMATIC_ASSAULTS) | out of scope; only listed |

## 2. FACTION record: what the exe reads

103 FACTION records (`Nameless`, `204-gamedata.base`, is the player's; the other 102 are NPC factions).

Fields **read by the exe** (field loader `FUN_1407fe270`, relation init `FUN_1406b3f80`/`FUN_1406b40d0`, others by
the functions named): `default relation`, `relations`, `coexistence`, `trustworthy`, `allow slaves weapons`, `anti
slavery`, `fundamental type`, `road preference`, `not real`, `offers bounties`, `run away ratio of squad size`, `run
away ratio relative to enemy`, `races`, `trade culture`, `buildings replacements`, `max prosperity`,
`special squads` (`FUN_1402ddc90`), `roaming population`, `residents`, `bar squads`, `default resident`, `squads`,
`squad default`, `campaigns`, `biomes`, `no-go zones`, `heals strangers`, `squad formation`.

Fields **never referenced**: `business relations`, `enemy classification`, `effect of anger`, `effect of happy`,
`emotion fade rate`. **Observed**: none of these key strings exists in the exe's string table
(`strings.tsv`), so no code looks them up; they are editor-only (the game's hostility threshold is hard-coded, section
3.3). The base values (`enemy classification` -10 for 99 of 103 factions, `business relations` -5) therefore have no
effect.

Data summary (**Verified**, probe `schema FACTION`): `default relation` is non-zero for 21 factions (18 of them <= -30);
`roaming population` is 50 for 96 of 103; `not real` is true
for 30 (wildlife, ruins, slaves, "No Faction", ...); `fundamental type` (CharacterTypeEnum: 0 NONE, 1 LAW_ENFORCEMENT,
2 MILITARY, 3 TRADER, 4 CIVILIAN, 5 DIPLOMAT, 6 SLAVE, 7 SLAVER, 8 BANDIT, 9 ADVENTURER) is 8 for 38, 4 for 25, 0 for 11,
9 for 11, 2 for 8, 7 for 4, 1 for 2, 3 for 2, 6 for 2.

## 3. Relations

### 3.1 Storage (**Observed**)

Each faction has a `FactionRelations` object. It holds an unordered map from *other faction* to an entry, a default
value, and the faction's own pointer. Entry layout (offsets from the entry as returned by `FactionRelations::vfunc_10`
= `FUN_1406b4c60`):

| Offset | Type | Meaning |
| ---: | --- | --- |
| +0 | byte | "allied" flag: forces `isAlly` true (set for the faction itself; other setters not found) |
| +2 | byte | at-war flag (set by declare-war, cleared by end-war) |
| +3 | byte | coexistence flag (set from the FACTION `coexistence` list) |
| +4 | float | **relation**, -100 .. +100 |
| +8 | float | accumulated positive trust |
| +0xc | float | accumulated negative trust |

Table-level: float default relation (+0x60 of the table, a copy of the FACTION `default relation`), `trustworthy`
(+0x14), global trust (+0x18; the save writes it as `global trust`). The saved matrix keeps `relation<k>`, `trust<k>`,
`trustNeg<k>` per entry (save writer `FUN_1406b3300`, loader `FUN_1406b38d0`), which matches the three floats above (**Verified**: the
key names written/read by those two functions are the same three, read from / stored to the offsets +4/+8/+0xc, and
`FactionRelations::vfunc_6` (`FUN_1406b2560`) accumulates into +8 / +0xc by the sign of its argument).

A missing entry is created on first access with relation = the table's default value (`vfunc_10`).
The **player's faction has no table**: `PlayerFactionRelations::vfunc_10` (`FUN_1406b3240`) answers "player towards X" by
asking X's table about the player, and "player towards player" is a static entry (flag 1, relation 100). This matches
[save.md](../formats/save.md#factions-gamestate_faction-type-37-war_savestate-type-9-two-instance_collections)
(the 103rd faction has no relation table, every other table has an entry about it).

### 3.2 Initial values (**Verified**)

For every pair A -> B: relation = the value of the entry in A's FACTION `relations` list (v0, an int; v1 and v2 unused,
all 0 or 100 in data) if there is one, else `min(default relation of A, default relation of B)`; A -> A = 100.
Two independent confirmations: [save.md](../formats/save.md) checked it against a saved world (checked against a saved world there), and the init function `FUN_1406b40d0` does exactly this (it copies B's default into A's entry, lowers it to A's
default when that is smaller, then writes the explicit list values and the self entry 100 plus the allied flag).
`FUN_1406b40d0` also sets the coexistence flag of each faction listed in `coexistence`.
`NEW_GAME_STARTOFF.faction relations` can override relations at game start (new-game setup; **Unknown**; [ui-screens.md](ui-screens.md#3-main-menu-and-new-game-flow) counts the start-off records: 4 of the 13 carry a faction relations list).

Base-game result (probe `rel`, **Verified** for the counts, which apply the rule above to the 102 NPC factions): of
10,302 ordered NPC pairs, 3,356 start hostile (value <= -30), 62 start allied (>= 50; 56 strictly above 50), the rest
neutral. 18 factions have `default relation` <= -30. Explicit lists hold 221 NPC -> NPC entries (224 non-self entries in all: the player faction's own list has 1 and 2 other entries target it; [save.md](../formats/save.md) counts 223 for the factions that have a table) with values in
{-100, -50, 0, 15, 20, 50, 60, 70, 75, 100}. `coexistence` has 81 entries, 32 of them mutual.

### 3.3 Thresholds (**Verified**: -30 and 50 appear in the predicate functions, in the threshold-message function `FUN_1406b28f0` and in the declare-war gate `FUN_1406b3000`; re-read independently)

| Predicate | Rule | Where |
| --- | --- | --- |
| hostile(A -> B) | B != A and relation(A -> B) <= -30 (default value when no entry) | `FUN_1406b25d0`, `FUN_1406b26d0` (identical bodies); called by `FUN_14092c5e0` (town choice) and `FUN_1409a7e00` (world state) |
| "default-hostile faction" | the faction's own table default (+0x60) is **strictly below** -30 (**Observed**) | inline in `FUN_1404374d0` (target choice, together with fundamental type 0 or 8) and in `FUN_14092c5e0` (combined with `ally()`); this is not the pair relation |
| ally(A -> B) | B == A, or allied flag, or relation >= 50 | `FUN_1406b2630` |
| coexists(A -> B) | B == A or coexistence flag | `FUN_1406b23c0` |
| value as a fraction | relation x 0.01 (and its negative) | `FUN_1406b2460`, `FUN_1406b24c0` |

The messages "{1} are now hostile towards you" / "no longer hostile" appear when the player's faction value crosses
-30, "{1} are now your allies" / "no longer your ally" when it crosses 50 (`FUN_1406b28f0`, called only when the other
faction is the player's). Note the small asymmetry: the message fires for > 50, `ally()` accepts >= 50.

### 3.4 Changing relations (**Observed**; the event table cross-checked with the dialogue action numbers)

All writers go through the table's virtual slots; the player's relation changes are applied to the *other* faction's
table (about the player).

| Slot | Function | Effect |
| ---: | --- | --- |
| 3 | `FUN_1406b3190` | relation -= 35 (if > -100), clamp |
| 4 | `FUN_1406b2ea0` | relation += a x b, clamp to [-100, 100] (direct add; `DA_AFFECT_RELATIONS`, slave trading +/-4) |
| 5 | `FUN_1406b2d20` | event of kind k and amount x, see below; does nothing when the owning faction has `not real` |
| 6 | `FUN_1406b2560` | a > 0: trust += a x b, else negative trust += a x b |
| 7 | `FUN_1406b2f90` | end war: clear war flag; if relation <= -25 set it to -25 (just above hostile) |
| 8 | `FUN_1406b3000` | declare war: only if relation >= -30 (not hostile yet): log "War breaks out between {1} and {2}" if the flag was clear, set the flag, set relation to -35 |
| 9 | `FUN_1406b2530` | global trust += a (`DA_AFFECT_REPUTATION`) |

Dialogue effects map straight onto the slots: `DA_AFFECT_RELATIONS` (4) -> slot 4, `DA_AFFECT_REPUTATION` (5) -> slot 9,
`DA_DECLARE_WAR` (13) -> slot 8, `DA_END_WAR` (14) -> slot 7 (`FUN_140680560`, **Verified**: the action ids come from the
editor's enum and the four slot calls in that function use exactly these ids). The UI text "Relations with {1} improved
by {2}" / "decreased by {2}" is printed there too.

Event kinds of slot 5 (`value += delta` only while `value > floor` for a negative delta, or `< ceiling` for a positive
one, so a hit can overshoot the floor by up to |delta|; then clamp to [-100, 100]):

| Kind | delta | floor | ceiling |
| ---: | --- | ---: | ---: |
| 1 | -0.05 x | -20 | 100 |
| 2 | -2.5 x | -50 | 100 |
| 3 | -10 x | -50 | 100 |
| 4 | -20 x | -100 | 100 |
| 5 | -20 x | -75 | 100 |
| 8 | +0.15 x x dt | -100 | +5 |
| 12 | -10 x | -60 | 100 |
| others | none | | |

(dt = the scaled frame time, `DAT_142133794`.) Kind 8 is a continuous, time-scaled positive drift capped at +5 (what
raises it is **Unknown**); kinds 3, 4, 5 and 12 line up with `CrimeEnum` STEALING, MURDER, ASSAULT and ESCAPE_PRISON but
the call sites were not traced, so the mapping is **Unknown** (it is not certain that the kind is a crime id; [economy.md](economy.md#112-how-a-bounty-is-gained) lists the same numbers as crime codes: 3 Theft, 4 Murder, 5 Assault and 0xc Escaping Prison, with their bounty amounts). After
every change, if the other faction is the player's, the threshold messages above are evaluated.

World events such as a faction's `trustworthy` are not changed after load (**Observed**: only read).

### 3.5 World states (WORLD_EVENT_STATE)

152 records; conditions are lists of refs with a wanted value: `NPC is` (CHARACTER: 1 alive, 0 dead, 2 imprisoned),
`NPC is NOT`, `player ally` (FACTION, 1/0), `player enemy`, `player involvement`, `town okay` (TOWN). A state is true
when **all** its conditions hold (`FUN_1409a7e00`, **Observed**): `player ally` is compared with `ally(faction -> player)`
and `player enemy` with `hostile(faction -> player)` (the section 3.3 predicates); the NPC conditions with the unique-NPC
manager (`FUN_1409a9570` builds the per-state query object). `FUN_1409a9a70(record)` checks a record's `world state`
list: every referenced state must equal its wanted value (1 = true, 0 = false). Used as an AND gate on SQUAD_TEMPLATE
spawns, TOWN nests and TOWN override towns (sections 6, 7). Counts: 84 states use `NPC is`, 37 `NPC is NOT`, 20
`player ally`, 8 `player enemy`; 121 SQUAD_TEMPLATEs carry `world state` refs.

## 4. Squad templates

SQUAD_TEMPLATE (959 merged records; UNIQUE_SQUAD_TEMPLATE has the same squad fields plus `missions`, `persistent`,
`replacement time`).

| Field | Meaning in the exe |
| --- | --- |
| `leader` (CHARACTER) | **Only the first entry**, one character (**Observed**, `FUN_140583a10`) |
| `squad` / `squad2` (CHARACTER, v0, v1) | the two member groups, see 5.1 for the counts (`squad2` is the "second half": member role 1) |
| `choosefrom list` + `num random chars` / `num random chars max` | N random characters from the list, weighted by v0 (chance, 0 counts as 100) |
| `animals` / `animals2` (ANIMAL_CHARACTER, v0, v1, v2 = age 0..100) | animal members, 5.1 |
| `slaves`, `prisoners` (SQUAD_TEMPLATE) | whole squads created recursively, 5.2 |
| `housemates` (SQUAD_TEMPLATE, min, max) | further squads spawned with this one (`FUN_14057f820`) |
| `faction` | owner faction; if absent the spawn code asks the owning town's faction, else searches every faction's `squads` / `squad default` for the template (`FUN_1402e7d10`), else falls back to the faction `Wildlife` (`3943-gamedata.base`) (**Observed**, `SpawnInfoList::vfunc_0` = `FUN_1408fe6b0`) |
| `world state` | AND gate for the spawn |
| `roaming military` | forces squad mode 2 ("roaming") even with a home town |
| `dont multiply` | exempts the template from the *Squad size multiplier* setting |
| `AI packages`, `blood smell mult`, `patrol approaches towns`, `force speed`, `personality`, `dialog *`, `nest`, ... | copied onto the platoon / its characters (`FUN_14057f4c0`, `read_AI_packages_n4`; the package system is in [ai.md](ai.md#other-records-that-touch-the-ai)) |
| `building`, `building dislike`, `building designation`, `layout interior/exterior`, `public day/night`, `public beds`, `bed usage cost`, `sell home`, `building ruined`, `initial door state` | how the squad's home building is chosen and set up (resident squads) |
| `vendors`, `is trader`, `vendor money`, `vendors fill total amount`, `vendors refresh time`, `buy mult`, `buys stolen`, `buys illegal`, `special items`, `special map items`, `*artifacts base value`, `regenerates` | shop and contents (section 8 for loot; the trading economy is in [economy.md](economy.md#9-shops-stock-money-and-restock)) |

Template stats (**Verified**, probe): 959 templates; `squad` has 853 refs , `squad2` 127, `animals` 169, `animals2` 23, `leader` 569 (24 with v1 = 100); `world state` 121. `building designation` is the editor's BuildingDesignation (0 NONE, 1 SHOP, 2
BARRACKS, 3 BAR, 4 HOSPITAL, 5 ARMOURY, 6 TREASURE, 7 PRISON, 8 HQ, 9 RESIDENTIAL, 10 SLAVE_STORAGE, 11
RESIDENTIAL_SMALL).

## 5. Creating a squad

`RootObjectFactory::createRandomSquad` = `FUN_140583a10` builds the characters of a platoon from a template; the
platoon object itself (faction, template, position, AI packages, designation, owner town) comes from `FUN_14057f4c0`
(**Observed**; a wrapper that creates the `Platoon`, sets mode 2 for `roaming military`, copies the packages and the
home building, and is called by the area spawner, the population spawner, the housemates code and `FUN_1409c4150`).

### 5.1 Counts and layout (**Observed**; two functions share the count rules, see the sentinel note)

For each member list entry (v0, v1), count n:

- n = v0 when v1 = 0 **or** v1 = 100;
- otherwise n = a uniform integer in [v0, v1] (`Rand_IntInRange` = `FUN_1409b3b40`: `v0 + floor(r x (v1 - v0 + 1))`, r in
  [0, 1) from the C `rand()` / 32768, then clamped to [v0, v1]; with v1 < v0 the result is v0).
  so a range with v1 < v0 always gives v0;
- then n' = trunc(n x M), at least 1 when n > 0, where M = the `Squad size multiplier` setting (default 1) unless the
  template has `dont multiply` (then M = 1).

100 is a sentinel ("not a range"), not a maximum: **Verified** by data (62 `squad` entries have v1 = 100 with v0 between
0 and 20, none has v0 >= 100) and by the two functions that test `v1 != 0 and v1 != 100`: `FUN_140583a10` and the
expected-size function `FUN_1408fe0f0` (section 6.1).

Order of creation and roles (the role is the editor's `SquadMemberType`: 0 SQUAD_1, 1 SQUAD_2, 2 SQUAD_LEADER, 4
SQUAD_SLAVE; stored per character, also the `squad mem type` of the platoon files in [save.md](../formats/save.md)):

1. the first `leader` character (role 2; position = the squad position, no offset);
2. `num random chars` .. `num random chars max` characters from `choosefrom list` (count = min when min >= max, else a
   uniform integer in [min, max]; each a weighted pick, weight v0, 0 = 100, skipping entries whose `world state` is false,
   `FUN_14057fd40`); role 0, or 2 when no leader exists yet;
3. `squad` entries, role 0;  4. `squad2` entries, role 1;
5. `animals` (role 0, the first one becomes the leader when nothing is);  6. `animals2` (role 1);
7. `slaves`: each listed template is created as a separate squad by a recursive call, its characters get role 4 and become
   slaves owned by the squad; 8. `prisoners`: created the same way and put into cages found inside the home building
   (`BuildingFinder_CagesWithinBuilding`); none if there are no cages.

Position of member number i (i counts every member so far, the leader is 0, slot 1 is the first follower): offset from
the squad position of **(3 x (i mod 8), 3 x floor(i / 8) + (i mod 2))** in X/Z (animals2: spacing 5 instead of 3), i.e.
rows of 8 characters 3 units apart with every odd one pushed 1 unit back.

Animal age (0 = baby, 1 = adult) = max(0, r x `animal age random` - 0.1 + v2 / 100), r uniform in [0, 1); v2 = 100 is an
adult. Log message for a template reference that does not resolve: "Missing squad leader/squad/squad2/animals/animals2/
slaves/prisoners data reference (Index: i) for squad '<name>'" (error level 4 for the first five).

A story-unique `leader` (CHARACTER with `unique`) goes through the unique-replacement spawn (`FUN_1405836e0`, uses the
`usedUniques` list that the save keeps): **Observed** from its name and callers, behaviour **Unknown**. The first call
chain of a squad flags the platoon as unique-containing when such a character was created (`+0xac` = 1).

Re-creation: a platoon with state 1 at +0x118 is flagged for regeneration; when the player cannot see it
it is reset to state 0 and its content is rebuilt from the template (`FUN_1404ffad0`, **Observed**).
The regeneration check itself is `BasePopulationManager::vfunc_5` = `FUN_14092a020`: for each squad of the pool whose
alive count is below its size, if the missing number m satisfies m >= 0.6 x size and m <= (pool maximum - current
population), the squad is flagged (state 1) and m is added to the running population; for the residents pool a flag at
+0xd9 skips the 0.6 rule (**Observed**; `regenerates` in fcs.def).

### 5.2 Procedural squads from a FACTION_TEMPLATE (**Observed**)

`generation info` (a FACTION_TEMPLATE) lets the game build a SQUAD_TEMPLATE at runtime (`FUN_1402e3430`; only 4
FACTION_TEMPLATE records exist, used by the BIOME_GROUP `faction gen` of new-game procedural factions): squad size is a
uniform integer in [`squad size min`, `squad size max`], leader count in [`leader levels min`, `leader levels max`] with
leader stats raised by a uniform value in [`leader increase min`, `leader increase max`] per tier, gear picked with
`armour min/max/cap`, `weapon level`, `weapon models`, `clothing`, `races`, `combat stats min/max` (stronger when the
squad is smaller when `strength/size balanced`). Only the skeleton is decoded here (the characters' gear rolls are in
[characters.md](../characters.md)); how `faction gen` is consumed at game start is **Unknown**.

## 6. Where squads come from

### 6.1 SpawnInfoList and weighted picks

`SpawnInfoList::vfunc_0` (`FUN_1408fe6b0`) turns a ref list (key + owner) into entries: for each ref with **v0 > 0**
(entries with v0 = 0 are dropped here) an entry {faction, owner town, template, weight = v0, expected size}. The total
weight is kept. Expected size (`FUN_1408fe0f0`) = 1 if the template has a leader, plus for each of `squad`, `squad2`,
`animals`, `animals2` entries: v0 when v1 is 0 or 100 (**not** scaled by the multiplier), else the range case
(v0 + v1) / 2 (integer), multiplied by the *Squad size multiplier* only when that is below 1, and then at least 1 (the
entry is skipped when v0 and v1 are both <= 0); the result is what the pool budgets count (**Observed**,
`FUN_1408fe0f0`; the multiplier and the minimum of 1 apply only to range entries).

Generic weighted pick of a ref list (`Zones_CollectionRuleB` = `FUN_14057fd40`; `FUN_140580310` returns the record): for
each entry the weight is the chosen value of the entry (v0 unless stated), **0 counts as 100**; entries whose target has
`world state` refs that are not all true, or whose weight is not > 0, are skipped; the total is the sum; one roll in
[0, total) picks the entry whose running sum first exceeds it (a single candidate is taken without a roll). Used for
`choosefrom list`, BIOME_GROUP `nests`, FACTION/TOWN `residents`-style lists in the editor, and by the item groups.

### 6.2 Area sectors: homeless / roaming squads and the biome population

The world is a 64 x 64 grid of **area sectors**, one per 4608-unit zone cell (the game computes the cell as
`trunc((coordinate + 147465) / 4608)`, clamped to 0..63; note 147465, not 147456, a 9-unit offset; first index X, second
Z; one cell record is 0xb8 bytes). Each cell points to its `AreaBiomeGroup`, built from the BIOME_GROUP whose `index`
colour matches the cell in `areasmap.tga` ([weather.md](../formats/weather.md)) by `Weather_AssignZonesToRegions` =
`FUN_1408fd6e0`; cells without a group get the group `NONE` (`18003-gamedata.base`). The group reads (**Verified**:
loader and users agree on the +0x88 slot, [game-loop.md](game-loop.md) names the same code): `num nests` (int),
`nests at fixed markers only`, `homeless spawn amount` (float, stored at +0x88), `homeless spawns` (a `SpawnInfoList`,
weight = v0; fcs.def says v1, the data and the code use v0), `nests` (a weighted list, weight v0).

Settings (`settings.cfg`, settings loader; **Verified** by two places: the loader stores them at settings
+0x48 / +0x4c and `npc range` at +0x38 = `DAT_1421334c8`, and the users read `DAT_1421334d8` / `DAT_1421334dc`):
`Global population multiplier` P (default 1; options-slider range 0.5..4) and `Squad size multiplier` M (default 1; slider 0.5..3, **Observed** in the options-menu builder `FUN_1403f0260`); the loader stores P at +0x48 and M at +0x4c.

**Tick** (`FUN_1408f9240`, per area "island", every **2 s of scaled time**, skipped while two UI-state checks hold (**Observed** that one is the loading window being visible, [game-loop.md](game-loop.md#zones)) and
while paused): first the nest spawner (6.3). Then for the island:

- current population C = sum over its sectors of (a + 0.5 b), where a and b are the two squad counters of the sector's
  tracking object (+0x70, +0x98); sectors with a town nearby (or flagged) are clamped to [0, 2] each;
- target T = P x sum of `homeless spawn amount` of the group of every **loaded** zone map of that island
  (`FUN_1408f5ca0`);
- if T <= C nothing happens; otherwise every eligible sector gets a weight
  w = max(0, P x amount + 1 - (a + 0.5 b)), halved when a town is nearby, and (w + 1) x 2 when the sector is empty; a
  sector is drawn by a weighted roll (retrying other sectors while its own target is already met) and
  `FUN_1408f8060` tries to spawn one squad there (**Observed**; constants as read, the intent "fill emptier zones
  first" is an inference).

**Which template** (`FUN_1408f7e80`): each entry i of the group's `homeless spawns` (and, near a non-player town, of that
town's roaming pool) gets weight
`max(0, (v0_i - k x count_i) x 1) x factor(template)` where count_i = squads of that faction and template that exist now
(for the town branch only those of that town), k = (sum of v0) / (sectors x P x amount) in the homeless branch (1 in the
town pool branch), and factor = 1 (`FUN_1402dd530` returns constant 1.0). The entry's `world state` conditions must hold;
in the town branch the entry's expected size must not exceed the pool's free capacity. One entry is then drawn by roll.
In other words the v0 values are *target shares*: a template that is already over its share gets weight 0.

**Placement** (`FUN_1408f50d0`, 4 rounds of up to 10 tries): a random point inside the sector rectangle; pushed out of
the nearest town to at least 2 x its radius, and out of any other town of a different faction to at least 1 x its radius
(direction away from the town centre); pushed out to at least **1382.4** units from the player's view position; must
pass the walkability test (walkability helper); ground height from the terrain; rejected when a player-owned object is
near (helper). The squad is created by `FUN_14057f4c0` with mode 99 and the town as owner; a spawn is also
refused when the sector's nearest town belongs to the player's faction.

### 6.3 Nests

A nest is a TOWN record with `type` 0 (TownType 0 NEST); **Verified** against data: all 75 `nests` refs of the 36
BIOME_GROUPs that have any target a TOWN of type 0 (23 distinct nests of the 40 type-0 records; the others are
placed on the map or spawned by squad templates through `SQUAD_TEMPLATE.nest`, 47 templates).

Nest spawner (`FUN_1408f7310`, run first in every area tick, **Observed**): for each biome group whose current nest
count is below `num nests` x `DAT_142133588` (the new-game option *Number of nests multiplier*, slider 0.5 to 4: **Observed**, [character-stats.md](character-stats.md#new-game-advanced-options-that-change-these-mechanics) and [combat.md](combat.md#global-tuning-the-constants-record) bind this address to that slider), pick a nest TOWN from the group's
`nests` list (weighted pick, weight v0, default 100, `world state` gate), pick a free position in the group's area
(position helper; groups with `nests at fixed markers only` use the pre-placed `TOWN_NEST_MARKER` towns, TownType 8),
create the `Nest` and stop; one nest per
tick. The base data asks for 531 nests in total (sum of `num nests`).

Nest population: `NestPopulationManager` (`FUN_14092e1d0`): the **roaming pool** gets the nest's `nest resident
population` N (int) and a float cap of **0.7 x N**; the residents pool is 0. The nest
draws its squads from the TOWN `roaming squads` list (entry = SQUAD_TEMPLATE, v0 = weight). `NestPopulationManager::vfunc_4` (`FUN_1408f9210`; a nest
that is not wiped out) calls the population spawner `FUN_1408f8740` for pool 2: if the pool's current members (count
via a helper) are below floor(0.7 N), it builds the weighted candidates (6.2's rule with k = 1), refuses to spawn
if a player-faction object is within 1250 of the spawn point (the argument of a proximity helper, whether it is squared
is **Unknown**) or the point is within sqrt(1250) = 35.4 units of the player view point, and otherwise creates one
squad (mode 99, owner = the nest, pool type 2) and marks the manager started. When all squads are dead the nest
manager reports it wiped out (`Nest::vfunc_103` = `FUN_14092f010`: message "{1} has been wiped out", stores the time
for the respawn; the saved BIOMES record keeps `<k>_<id>_respawn` and `_count` per nest entry). The respawn delay
itself was not decoded (**Unknown**).

### 6.4 Town residents and roaming squads

TOWN `residents` (SQUAD_TEMPLATE, v0 = number of squads, v1 = priority) and `residents override`: if the town has no
residents, or `residents override` is false, the owning FACTION's `residents` list is the default (override true =
the town's list replaces it, false = it adds to it). `FUN_140936e60` and `FUN_140935d10`
build the town's resident table (**Observed**):

- an entry = template, count v0, priority, set of preferred BUILDING records (`building`), set of disliked ones (`building
  dislike`), and the template's `building designation` is registered for the town;
- priority: v1 = 100 (the editor's default) counts as 0; otherwise v1 must be 0..5, else an error box "Don't set resident
  priority higher than 5" is shown. **Verified** against data: TOWN `residents` v1 is only 0..5, FACTION `residents` v1 is 0..4 or 100 (9);
- v0 = 0 entries ("default filler" in fcs.def) are dropped by the spawn list (6.1);
- `bar squads` (squad, count v0, chance v1 %): the FACTION's default list is used only for towns of TownType 2 (TOWN) or
  when the entry comes from the town record itself (**Observed**).

The two pools of a town (`read_roaming_population_n4` = `FUN_1408f98d0`): pool 1 = residents, pool 2 = roaming squads
(`roaming squads` of the TOWN), both `SpawnInfoList`s; pool 2's budget is the FACTION `roaming population` (default 50:
"adds this number to the max population count for every town to allow short-range roaming squads"), pool 1's budget
starts at 0 and is raised by the resident counts. Budgets are saved as `pop`/`popd`/`pop2`/`popd2` in GAMESTATE_TOWN
([save.md](../formats/save.md#towns-gamestate_town-type-94-gamestate_town_instance_list-type-38-state-type-39)).
How and when the initial resident squads of a town are created at zone load, and how resident squads pick a home
building, is **Unknown** (the building-assignment code was not traced). Roaming squads leave and come back (the walking is in [pathfinding.md](pathfinding.md)); their AI
packages (`GoOutOnPatrol`, `Task_PatrolTown`, `Task_TravelToTargetTown`, ...) are not decoded; the destination chooser
`FUN_14092c5e0` (**Observed**) sorts the candidate towns by distance, skips towns hostile to the squad's faction (3.3), and
picks one of the nearest 4 at random.

### 6.5 Unique squads and campaigns (survey only)

FACTION `special squads` (UNIQUE_SQUAD_TEMPLATE, v1 = number to maintain, scaled by faction/town health and population)
are held by the faction's `FactionUniqueSquadManager` (`FUN_1402ddc90`); a wiped squad is replaced after the template's
`replacement time` (24 h default per fcs.def) (**Observed** from fcs.def and the manager's struct;
the replacement logic was not decoded). FACTION_CAMPAIGN, `campaigns`, `biomes`, `no-go zones`,
DIPLOMATIC_ASSAULTS and `raidSizeMult` / `raidFrequencyMult` in `settings.cfg` drive assaults and raids by the
`FactionWarMgr`; **Unknown**.

## 7. Towns

TOWN (346 merged records): `type` (TownType: 0 NEST, 1 OUTPOST, 2 TOWN, 3 VILLAGE, 4 RUINS, 5 SLAVE_CAMP, 6 MILITARY,
7 PRISON, 8 NEST_MARKER, 9 POI, 10 NULL; histogram **Verified** by probe: 40, 32, 80, 29, 106, 13, 13, 1, 2, 24, 6),
`faction`, `is public`, `is secret`, `size radius`, `town radius mult`, `no-foliage range`, `distant mesh`,
`residents`, `residents override`, `roaming squads`, `bar squads`, `default resident`, `override town` + `world state`,
`debris`, `debris building`, `loot spawn`, `num centrepoints`, `nest resident population`, the artifact value ranges,
`trade culture`, `material`, `unexplored name`, `spawn in town centre`. Placed towns are listed in
`leveldata.level` ([zones.md](../formats/zones.md#leveldatalevel)).

Setup (`Zones_TownSetup` = `FUN_1409353c0`, **Observed**):

- `town radius mult` is clamped to [0.1, 5] (values below 0.1 become 1); the usual value is 1;
- `no-foliage range` is read; **type 8 (NEST_MARKER) sets it to 0**;
- the two population pools are created (6.4), the faction's `trade culture` and the town's are combined into price
  multipliers (`ITEMS_CULTURE.trade prices`, ITEM v0 % with 0 meaning 100, see [economy.md](economy.md#71-culture-items_culture), and `forbidden items`);
- the map cell(s) the town covers are flagged as inhabited unless the type is 4 (RUINS) or 9 (POI); a type-1 OUTPOST also
  gets a second flag (used by the placement rules);
- the town's `is public` flag is stored.

**Override towns** (`FUN_1409ff5a0`, evaluated from `ZoneMapContent::vfunc_4`): for a town that is not the player's, the
`override town` list is scanned; a candidate whose own `world state` conditions all hold (3.5) and that is not already the
current replacement becomes the replacement (`replacementTown` in the save); the town is then repopulated from it
("use it to change and devastate factions"). Which candidate wins when several qualify, and the exact repopulation, are
**Unknown**.

## 8. Debris, loot and building contents

`FUN_140938440` (called for nests, and for other towns when `spawn in town centre` is true; **Observed**) scatters decoration
and loot around the centre:

- the random generator is seeded **from the town position** (`srand` of a hash of x ^ 0xDEADBEEF and z ^ 0xDEADBEEF), so the
  layout is the same every time (the debris is never saved, it is regenerated);
- `debris` (NEST_ITEM; v0, v1, v2): `Rand_IntInRange(v0, v1)` clusters of that item, mesh scale v2 / 100 (0 means 1);
  each cluster holds `cluster min`..`cluster max` copies within `cluster range` (40 default, from the NEST_ITEM) of a
  point within `size radius` of the centre;
- `debris building` (BUILDING; v0 = count, v1/v2 = cluster range min/max): `num centrepoints` (default 4) points are chosen;
  a new point within 140 units of an earlier one is pushed away; for each entry the count is v0 + a uniform integer in
  [-v0/2, v0/2] when v0 > 1, v0 = 0 gives a coin-flip 1, then the buildings are placed around the point (they must have
  no navmesh effect: camp beds, stools, campfires, torch posts);
- `loot spawn` (VENDOR_LIST, v0, v1): only for nests; `Rand_IntInRange(v0, v1)` items are rolled from the vendor list and
  dropped at random ground positions inside the area (height above the water line required); the picker is the same as the
  item-group picker below.

**Item groups** (ITEM_PLACEMENT_GROUP, 21 merged records; lists `items`, `weapons`, `clothing`, `containers`, `maps`,
`blueprints`, `armour blueprints`, `weapon manufacturers`; `min/max respawn time` 24/48 h for 19 of 21, 72/72 for 2;
`random yaw` true for all): a placed group is a `BuildingItemGroup` = one item slot. Picker `FUN_1409540d0` (**Observed**):
all list entries are merged into one weighted table, weight = v0, one roll over the total
decides the entry; a WEAPON entry needs a second weighted pick from `weapon manufacturers`. Respawn
(`BuildingItemGroup::vfunc_28` = `FUN_140549e60`, per frame while the building is loaded): when the slot's item is
gone (taken, destroyed, or not a valid item type), the group sets the next respawn time to **now + Rand_IntInRange(min,
max) hours** (absolute game hours) and marks the slot empty; when the time has passed and the building is loaded, it
creates a new item (`FUN_140547ed0`). The remaining time is saved as `respawn time`.

Building contents by role (shop stock, restock and vendor money: [economy.md](economy.md#9-shops-stock-money-and-restock); money items in loot lists: [economy.md](economy.md#13-loot-and-money-items)): the FACTION `item spawns armoury/bar/HQ/resident/resident small/treasure` (VENDOR_LIST, v0 %) fill
containers by the building's designation, overridden by the squad's own `vendors`. Traders refill `vendors fill total
amount` items every `vendors refresh time` hours (24 default). Artifacts: a TOWN with `gear/item artifacts max value` > 0
needs a resident whose template has an equal or higher `*artifacts base value`, else the artifacts are lost (fcs.def; the
placement code was not decoded).

## 9. Settings and constants that matter

| Name | Value | Effect |
| --- | --- | --- |
| `Squad size multiplier` (`settings.cfg`) | 1 | M in 5.1 |
| `Global population multiplier` | 1 | P in 6.2 |
| `npc range` | settings.cfg value | relevance range, see [game-loop.md](game-loop.md) |
| CONSTANTS `max squads` / `max squad size` / `max faction size` | 10 / 20 / 30 | stored by the constants loader (`FUN_14086b2b0`); **the code that enforces them was not found**, but the Squads tab shows "Maximum number of squads reached." ([ui-screens.md](ui-screens.md#5-overview-window)), so the squad cap is at least checked in the UI (**Observed**) |
| area tick | 2 s scaled | 6.2 |
| min distance of a homeless spawn from the player | 1382.4 | 6.2 |

## Data corrections

- fcs.def says the weight of BIOME_GROUP `nests` and `homeless spawns` is "val1"; the code and data use **v0** (nests default 100;
  homeless v0 1..250, 30 of 653 entries above 40, v1 always 0). **Verified** (probe; `FUN_1408fe6b0` copies the first int).
- fcs.def says TOWN `debris` "val0+1 is min/max num to spawn"; the code uses v0 and v1 as the min and max count (**Observed**).
- `enemy classification`, `business relations`, `effect of anger/happy` and `emotion fade rate` are editor-only (section 2).
- `squad` / `squad2` / `leader` v1 = 100 is a placeholder, not a count (5.1).

## Unknowns

- What sets relation event kinds 1..12 (call sites of `FactionRelations::vfunc_5`), and what the allied byte (+0) and the
  `trust` floats feed (dialogue conditions, "trustworthy" behaviour).
- When and where a town's initial resident squads are created (zone activation), how resident squads choose a home
  building (`building`, `building dislike`, `building designation`), housemates timing, and `bar squads` spawn.
- Nest respawn delay after "wiped out", the default of `DAT_142133588` (the Number of nests multiplier option; **Unknown**, the exe image holds zeros there), the rule that makes `TownPopulationManager::vfunc_4`
  declare a town wiped out (**Observed** gates: roaming pool cap <= 3 and <= 25 roaming members).
- Faction campaigns (FACTION_CAMPAIGN), raids and assaults (`raidSizeMult`, `raidFrequencyMult`), the unique squad
  replacement logic, `faction gen` at game start, `NEW_GAME_STARTOFF.faction relations`.
- Who enforces CONSTANTS `max squads`, `max squad size`, `max faction size`.
- Which override town wins among several qualifying candidates; what exactly repopulating a town from its override does.
- Whether the 1250 radius of the proximity helper is squared; the sector counters a/b (`+0x70`, `+0x98`) beyond "squad counts".
- Patrol routes: the patrol tasks (`Task_Patrol`, `Task_PatrolTown`, `GoOutOnPatrol*`) were not decoded here (class mapping: [ai-tasks.md](ai-tasks.md#tasktype-to-task-class); packages: [ai.md](ai.md#signal-functions-and-the-package-classes)); no fixed route data
  was found in FCS records (squad templates only name `AI packages`; `patrol approaches towns` is a bool).

## Implementation outline

Order that gets a populated, hostile-aware world with the least decoded behaviour:

1. **Data layer** (exists): `GameDatabase`, `LoadOrder`, `WorldLevelData` (towns, placements). Add typed views over FACTION,
   SQUAD_TEMPLATE, TOWN, BIOME_GROUP, NEST_ITEM, ITEM_PLACEMENT_GROUP and WORLD_EVENT_STATE with the field names above, and
   a `RefList` view that exposes v0/v1/v2 and the sentinel rules (v1 in {0, 100}).
2. **FactionRelations**: the table with the entry layout of 3.1, initialisation of 3.2 (verify against the sample saves with
   [save.md](../formats/save.md)), `IsHostile` (<= -30), `IsAlly` (>= 50 or flag), the "default-hostile faction" test (table default < -30, 3.3), the slot semantics of 3.4 and the dialogue
   actions. Do not read `enemy classification` and friends.
3. **WorldState**: AND-evaluation of WORLD_EVENT_STATE against the unique-NPC state and the relation predicates.
4. **Squad factory**: `CreateRandomSquad(template, position)` exactly as 5.1 (count rule with sentinel, multiplier, role order,
   grid offsets, animal age), producing `CharacterSpec`s that the character generator in [characters.md](../characters.md)
   fills in; the platoon wrapper sets faction, packages and home.
5. **Spawn pools**: `SpawnInfoList`, the weighted pick (weight 0 = 100, world state gate), pool budgets and the
   spawner of 6.4/6.3 (including the 1250 / 35.4 / 1382.4 distances), then the area-sector tick of 6.2 with the target/current
   sums, and the nest spawner of 6.3.
6. **Towns**: `TownSetup` clamps and flags, the override-town evaluator, resident tables (priority rule), bar squads.
7. **Debris and items**: deterministic position-seeded debris and loot, `ItemPlacementGroup` with the respawn clock in absolute
   game hours.
8. Defer: campaigns, raids, unique-squad replacement, patrols (need the AI task layer of [ai.md](ai.md) and [ai-tasks.md](ai-tasks.md), scheduled as in [game-loop.md](game-loop.md)).
Ids and ranges worth encoding as constants with tests: threshold -30 / 50, event table of 3.4, the 3 x (i mod 8) layout,
2 s area tick, 1382.4 player distance, `0.7 x N` nest cap, 0.6 regeneration ratio.
