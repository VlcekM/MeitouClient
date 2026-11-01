# Factions, squads and towns

Original FACTION/Platoon/population/town research: binary functions, FCS and merged database/save
checks. Tables/expressions retain fields and event values. Runtime: [simulation](../simulation.md);
[AI](ai.md), [economy](economy.md), [save format](../formats/save.md).

## Relations and world states

**Verified** initial A→B: explicit A.relations v0, otherwise min(A.default,B.default); self100.
Hostile<=-30, ally>=50; directional flags/coexistence and event changes are distinct. Storage,
trust/known/player-faction branches and dialogue/reaction events remain scoped Observed where
not independently checked. Start-off relation overrides are not fully decoded.

WORLD_EVENT_STATE (`FUN_1409a7e00`, **Observed** by code): holds when every `NPC is` CHARACTER has unique state v0 (0 dead, 1 alive,
2 imprisoned), no `NPC is NOT` one has its v0, each `player ally` FACTION is (v0 1) or is not (v0 0) the player's ally and each `player
enemy` hostile to it. With `player involvement`, an `NPC is` other than alive or an `NPC is NOT` alive also needs the unique's player
flag. A unique with no entry counts as alive, so a new game answers by the data alone; nothing is stored. A `world state` list entry
(`FUN_1409a9a70`: spawn gates, weighted picks, campaigns) passes when the state's answer equals (v0 == 1); a missing record is skipped.

## Squad factory

**Observed/Verified sentinel checks:** per entry count=v0 when v1=0 or100; otherwise inclusive
v0..v1, reversed range givesv0. Apply size multiplier/truncation/min1 unless dont-multiply.
Create leader role2, random choosefrom then squad role0/squad2 role1, animals/animals2, slaves
as recursive squads (**Verified** `FUN_140583a10`: after the recursive call every member of the slave squad from index 0, the leader included, gets member
type 4 through `FUN_1406215c0` (status `+0x25c`), whatever the slave template says; the squad's leader pointer is not touched), prisoners into available home cages. First member can become leader.
**Prisoners** (`FUN_140583a10`, **Observed**): only with a home building; its cages (`BuildingFinder_CagesWithinBuilding`) are counted, then each `prisoners` entry makes a
platoon of the entry's `faction` (else the owner's) whose members each go into a random remaining cage (`FUN_140330600`, lock set) with `Character+0x190 = 9999999` (meaning
**Unknown**); with no cage left, one member is removed and placing stops. A housed trader that made prisoner or slave squads still stocks its home containers ([economy](economy.md#building-shops-observed-decompile)).
Offsets `(3*(i%8),3*floor(i/8)+(i%2))`, animals2 spacing5 (spawn places). Followers' slots by FACTION `squad formation`: [squads travelling together](pathfinding.md#squads-travelling-together); Meitou's: [simulation](../simulation.md#movement-and-paths). Animal age=max(0,r*R-0.1+v2/100) (**Verified** `FUN_140583a10` animals and animals2 loops: r uniform 0..1 per animal, R = the squad TEMPLATE's float `animal age random`, read once, not the animal record's, which has none; 888 of 961 base templates 0.1, the rest 0 to 0.5; a template lacking the key: Meitou 0, the `fcs.def` default **Unknown**).
Unique members go through one gate: [unique characters](#unique-characters).

Generic weighted refs: v0=0 means100; exclude nonpositive weights and false world states,
first cumulative weight exceeding uniform roll wins; single candidate no roll. SpawnInfoList
instead drops v0<=0 and budgets expected squad sizes using its separate sentinel/range rules.
Do not reuse one zero-weight convention for both.

### Squad leader

`ActivePlatoon` (KenshiLib 1ce4a16, **Observed**) keeps `squadleader` (`+0xa0`, set by `setSquadLeader`, which gives it member type 2) and `backupLeader`
(`+0xa8`). The effective leader (`getSquadLeader`, `FUN_140792b90`, **Verified**) is the leader while it is not down (`FUN_1405c9e10`: knocked out, dead, ragdolled or carried, or leg
state 3+) and not `inSomething` (`+0x2f8`, bed or cage), else the backup under the same test, else the first member of the container list (animals included) that is not down and
stands in a loaded or requested zone, cached as the backup; else the leader itself, usable or not. That member scan tests the *leader's* `+0x2f8`, not the member's (so a leader in a
bed or cage is kept). The stored leader is never replaced there. Who becomes `squadleader` after the leader dies is **Unknown**. Meitou (`PopulationSystem.EffectiveLeader`, `Squad.Backup`):
the same rule for a loaded platoon's place and the unload anchor; down = out (unconscious or dead) or carried, a limb hold and playing dead are not modelled. A campaign's announcement is task 129 in the leader's AI package, and animals get only
`dialog animal`, so an animal never announces. Meitou keeps one leader: a squad without one (death, leaving, a save or stand-in without a leader) takes its
first member that is not an animal, else its first (`SquadRegistry.Successor`, engine choice). A player squad edited on the squads screen still makes its index 0 the leader.

## Unique characters

**Data** (merged base files, throwaway survey): 155 of 639 CHARACTERs have `unique`; 13 of them have exactly one `unique replacement spawn`
(no record has more, no non-unique has one). Uniques are named by SQUAD_TEMPLATE `leader` 99, `squad` 54, `choosefrom list` 51, `squad2` 1,
FACTION_CAMPAIGN `special leader` 57, WORLD_EVENT_STATE `NPC is` 113 / `NPC is NOT` 60 and dialogue conditions; 34 by more than one template
(Dust King leads 3; recruits sit in several towns' recruit lists); 9 by none. Templates with a unique leader are listed by TOWN `residents` (99)
and `bar squads` (33). Two UNIQUE_SQUAD_TEMPLATEs exist (`General Epson Army`, `UNIQUE bandit test`), each in one FACTION's `special squads`.

**State** (`UniqueNPCManager`: one global map CHARACTER record → entry, `FUN_140354560`; **Verified** by code, meanings of `state` from `fcs.def`
`NPC is`; the 9 sample saves hold 302 entries, all state 1 / player false):

| Field | Meaning |
| --- | --- |
| claimed | the CHARACTER record once a squad created it; null or no entry = free to create (`FUN_1405921b0`) |
| handle | the live character, recorded when it gets its handle (`Character` slot 16, `FUN_140623920`) |
| state | 1 alive (also for a new or absent entry), 0 dead, 2 imprisoned/captured |
| player | the player was involved (killed, imprisoned, holds it) |

**Transitions** (**Verified** by code unless marked):
- Create (`FUN_1405836e0`, used by `FUN_140583a10` for `leader`, `choosefrom list`, `squad`, `squad2`, `animals` and `animals2` alike, **Verified**; the
  `unique` bool is read with no record-type test, so the six unique base ANIMAL_CHARACTERs are gated too): a claimed unique is replaced by
  its first `unique replacement spawn` (created without the gate, recorded by slot 16 like any unique if it is one; all 13 targets are
  non-unique except `Protector Of The Flame`, which names itself, so it can be duplicated), or by nobody; a free one is claimed (handle
  reset) and created.
  So a dead unique (claimed stays set) never comes back, a v0 > 1 entry yields one copy plus replacements, and a missing leader does not stop the
  squad (the first member made leads). The platoon's +0xac is set when a unique member is made (use **Unknown**).
- Death (`FUN_1407a6200`): state 0; player set (never cleared here) when the killer's faction is the player's. The dead unique's faction's
  "prosperity" (`FactionUniqueSquadManager` +8, see Special squads below) drops by 100, floor 0; the record's `unique` bool gates it (**Verified**, `FUN_1407a6200`). Meitou: `UniqueCharacters.Died` queues the faction of each unique that dies (once, on the change to dead), `PopulationSystem.StepProsperity` applies it at the next look.
- Imprisoned in a cage or restraint (`FUN_140330600`): state ≠ 0 → 2, player set when the cage's owner is the player's. `DA_ENSLAVE_TARGETS_CARRIED_PERSON` / `DA_ARREST_TARGETS_CARRIED_PERSON` (0x28 / 0x1e), which
  take a carried unique off its carrier, do the same, player set when the carrier is the player's (`FUN_140680560`).
- Periodic refresh (`FUN_1405cf470`, from the character's slot-80 update `FUN_1405d00b0`): state 1 and carried by a non-ally → 2 (player = carrier
  is the player's); state 2, not carried, not imprisoned, and in the player's faction or allied with its squad's → 1, player cleared; state 0, in
  the player's faction and not dead → 1 (**Observed**: the ally/town tests are summarised).
- Unload of an active platoon (`FUN_1407ef4b0` → `FUN_1409a8120`), for each unique member in state 2 or away from its ownership town: dead, down,
  carried by a non-friend or `+0x279 & 7` → state 0; free and well → entry erased (`FUN_1405067e0`, only if state ≠ 0 and the stored handle's container (C, CS) is
  this platoon: a unique registered to another squad is not released) and the platoon re-creates its members from the template on activation (+0x118 = 1), which claims the unique again;
  imprisoned away from friends → 2 (**Observed**; a 0.1 roll against the faction's town list in the free branch is **Unknown**).
- An activating platoon in spawn mode 3 (home no longer valid) whose template leader is unique releases the template's unique
  leader/squad/squad2 entries registered to it (`FUN_1404ffad0` → `FUN_1404ff760`) (**Observed**).
- New game and every load (`FUN_1409a8970`): map cleared, then the unique leader/squad/squad2 of each SQUAD_TEMPLATE's first `prisoners`
  template → state 2 unclaimed (base game: Cat of `Dust King Prisoner`, `Tengu's vault prisoners`, `agnu`). Import marks every imported unique
  claimed (`FUN_140378a30`).

**Queries**:
- WORLD_EVENT_STATE (`FUN_1409a7e00`): `NPC is` needs state = v0, `NPC is NOT` state ≠ v0; with `player involvement` the player flag must
  also be set (`NPC is` with v0 ≠ 1, `NPC is NOT` with v0 = 1) (**Verified** by code).
- Campaign check (`FUN_1409c4ed0`): passes with no `special leader` or one in state 1 (a never-made one counts). Gathering (`FUN_1409c5040`),
  leaders in list order, first usable wins: a registered handle → its platoon; else the SQUAD_TEMPLATEs naming it `leader` (more than one only
  logs "ERROR: unique NPC with more than 1 squad referencing it", the first is used) are spawned if the unique is free. Lord Mirage (a
  non-unique `special leader`) has no entry, so `UC town racists fort` (the template he leads) can be spawned for every campaign;
  Yayoi has no template. `specific target NPC`s count when state ≠ 0 and the handle
  has a non-zero position (`FUN_1409c5e80`, `FUN_1409a7d90`) (**Observed**).
- Towns have no extra rule: a `residents`/`bar squads` template with a unique leader passes the create gate above, so while the unique is
  claimed elsewhere or dead the squad comes with the replacement or without the leader (**Observed**).

**Special squads** (FACTION `special squads` → UNIQUE_SQUAD_TEMPLATE, v0 = number to keep per `fcs.def`): `FUN_1402ddc90` makes per entry
{template, v0, timer, live platoon handles}. The update `FUN_1402dd680` (each war-manager run, `FUN_1406b97c0`, with the faction's accumulated
time) adds 0.1·dt to prosperity while it is below the cap (no clamp in the add) and counts timers down; both start at the faction's `max prosperity` (`FUN_1402ddc90` → `FUN_1402e0ab0`, **Verified**; the constructor's 1000 is only a default; the period of dt is **Unknown**, no reader of prosperity found; Meitou: `PopulationSystem.StepProsperity`, game seconds since the last look; the save's `prosperity` replaces the start; hashed while below the cap). When a timer is 0 and fewer than v0 live, it returns: **nothing spawns
them** (**Verified**: by code, the only spawn-near-own-town helper `FUN_1402dda60` has no callers; 0 of 3,502 PLATOON records in 9 saves are
`special`, every `sqtime` is 0). A `UniquePlatoon` (`FUN_14057c6f0`: UNIQUE_SQUAD_TEMPLATE, or a saved PLATOON with `special`) adds a
mission ticker (every 10 s pick a `missions` DIPLOMATIC_ASSAULTS entry by its `conditions`: `FUN_1409a7930`; not decoded), saves
`missionTimer` and `missionRepeatsCount` per mission (slot 23), and on destruction unless kept tells the manager, which drops its handle and
sets the timer to 2618.18 × `replacement time` / 24 (`FUN_1402de9b0`; 2618.18 is the seconds of a game day, so the unit is game hours, **Observed**). No UniquePlatoon is
ever made in vanilla (special squads are never spawned, no shipped save has a `special` PLATOON), and SINGLE_DIPLOMATIC_ASSAULT `repeat timer hours min/max`, `delivery AI
package`, `dialogue announce` are not strings in the exe (**Verified**): the whole mission path is dead, so Meitou only carries its records and save keys.

## Population and contents

Area-sector grid uses the4608-zone coordinate system with9-unit offset; biome populations,
homeless/roaming pools, nests, town residents/bar/roam squads and campaigns have separate caps,
spawn distances/timers/owner rules. Nest debris and regenerating squads are stateful. Towns
resolve faction, homes/parts/material replacement, residents, interiors, contents and culture.
World-state replacement towns and faction ownership affect both population and building data ([town overrides](#town-overrides)).

### Town overrides

**Verified** by decompile (`ZoneManager::checkForRepopulateTown` `FUN_1409ff5a0`, KenshiLib 1ce4a16 tier B; the setter `FUN_1409fe7a0`; zone load `FUN_1409ffad0`;
`Town::getGameData` `140951f70`); Meitou: [simulation](../simulation.md#off-screen-tier).
- A town reads everything from `getGameData`: its replacement (Town +0x338, saved `replacementTown`) when set, else its own TOWN. So a replacement's own
  `override town` list chains on (Shark: Grayflayer, then "all ded").
- Checked only when the town's zone loads, for each placed town of the zone (nothing runs at the death). Skipped for a town of the player's faction and while a
  ZoneManager byte (+8) is set: cleared when the loading screen finishes (`FUN_140a0f820`), so towns loaded behind it keep their TOWN (**Observed**; setter not found).
- Candidates: the current TOWN's `override town` entries. One qualifies when each distinct `world state` entry (WORLD_EVENT_STATE) equals its v0; its weight is
  how many there are, so an entry with none never qualifies. The replacement in place is skipped. The heaviest wins; equals keep the first in list order.
- Applying: replacement = the winner; the town re-reads `no-foliage range`, `type`, `is public`, and takes the winner's `faction` when it names one and it differs;
  resident data is set up again; the town goes into the ZoneManager set `updatedTownOverrides` (+0x168148). The zone file is then trimmed (`FUN_1409fe340`): of the
  `0-buildinglist` entries (only those with a field at +0x88 of -1, meaning **Unknown**), the first GAMESTATE_BUILDING state decides: not the player's and naming
  this town (`townC` / `townCS`) drops the entry; an ITEM_PLACEMENT_GROUP state drops it whatever the town. Every `1-itemlist` item goes, and the zone counts as
  fresh, so the residents are made again. Only the town's own zone is trimmed. Existing platoons are untouched.
- `updatedTownOverrides`: while a town is in it, the check answers "repopulate" at once (no new choice), so each load trims the zone again. The pass that places
  residents after zones load (`FUN_140a0e760`, ZoneManager state 2) places them for the collected towns not of the player's faction and erases them (`FUN_140a22ef0`).
  Nothing else touches it, and it is not saved.
- Data (merged base files, throwaway census): 117 TOWNs have `override town`, 234 entries; the replacements are TOWNs named `... (override) ...`.
  With every unique alive and no player ally, one qualifies: Fort Simion becomes Fort Yabuta ("Yabuta is alive" 1, "Player is ally of Rebel Farmers" 0) at its
  first load after the loading screen (**Observed**, census through Meitou's `WorldStates`).

**Sector and nest counts** (**Verified** by code): a platoon is filed in the area sector of its position (`FUN_1408f6c80`, from the platoon constructor and
`Platoon::vfunc_28`): persistent platoons (`+0x115`, or in a live campaign: `FUN_1407ec570`) and squads of a nest object (`FUN_1408f68a0`: its `vfunc 0x270` non-null) into a set (+0x38) that is not
counted, the rest into the counted set (+0x60, size +0x70). The sector tick (`FUN_1408f6d20`, once per sector per tick) moves one platoon of the counted set to a third set (+0x88, size +0x98) when
the platoon's intact flag is 0 (`FUN_1407ac7b0` → `FUN_1407a5be0`: the squad-intact rule of [perception](perception.md), which a squad with no able member fails, so a wiped or hurt one counts), and drops from the third
set one whose squad object is gone (+0x1d8 null, unloaded); nothing moves back. A sector's load is +0x70 + 0.5 x +0x98 against its group's cap (**Verified**); the homeless draw's "below its share" test reads +0x70 alone.
A wiped platoon is destroyed only at its next unload (`declareDead`), so it keeps counting until then (**Observed**). Meitou (`PopulationHomeless.RefillSectors`): the same by the current intact test (no one-per-tick
drip; a squad intact again counts 1), for a sector's squads but a save's persistent ones and occupiers. A nest's or town's pool is the set
of platoons whose `hometown` is it, by `sqt` (1 residents, 2 roaming), counted in living characters ([save keys](../formats/save.md#npc-platoons-meitou-mapping)).

Loot/building inventories, artifacts, shop stock, prisoners/slaves and placed debris derive
from different reference lists. Preserve slot/count/quality/owner fields and forbidden-item
rules rather than treating every list as uniform random loot. Story NPCs: [unique characters](#unique-characters);
campaigns: [campaigns](#campaigns).

### 1. Object model

| Game class (RTTI) | Role | Notes |
| --- | --- | --- |
| `Faction` | one per FACTION record (103 in the base game, the player's included); owns its record (+0x240), a `FactionRelations` (+0x78), the platoon lists, `FactionUniqueSquadManager` | `FUN_1407fe270` is the loader from the record (**Observed**). Player flag: the field at +0x250 is non-null only for the player's faction (**Observed**, also in [game-loop.md](game-loop.md)) |
| `FactionRelations` / `PlayerFactionRelations` | the relation table of one faction towards all others | section 3 |
| `Platoon` -> `ActivePlatoon` (characters exist) / `UnloadedPlatoon` (abstract) / `UniquePlatoon` | a squad; `UniquePlatoon` only from an UNIQUE_SQUAD_TEMPLATE ([special squads](#unique-characters)) | per-frame stepping is in [game-loop.md](game-loop.md#update-threads-and-budget); creation is section 5 |
| `TownBase` -> `Town`, `Nest` | a TOWN record placed in the world; nests are TOWN records of `type` 0 spawned by the game | section 7; `Town`/`Nest` share almost all virtuals, the nest overrides slot 103 (wiped out) |
| `BasePopulationManager` -> `TownPopulationManager`, `NestPopulationManager` | per town: two pools (residents, roaming) with a population budget and a `SpawnInfoList` | section 6.4 |
| `SpawnInfoList` | the weighted list of squad templates a pool or biome group may spawn | section 6.1 |
| `AreaSector`, `AreaBiomeGroup` | one cell of a 64 x 64 grid of 4608-unit cells, and the BIOME_GROUP data it belongs to | section 6.2 |
| `BuildingItemGroup` | an ITEM_PLACEMENT_GROUP placed in a building: one item slot with a respawn timer | [loose items](#loose-items-in-buildings) |
| `FactionWarMgr`, `CampaignData` (always `StartInvasionData`) / `CampaignInstance` (always `GatherAndMove`) | faction campaigns (FACTION_CAMPAIGN) | [campaigns](#campaigns); DIPLOMATIC_ASSAULTS not decoded |

**Persistent platoons** (**Verified** by code): a zone's unload (`ZoneMapContent::deactivate`, `FUN_1409fe980`) touches no platoon; each platoon checks
itself (`FUN_1404febd0`) and after 4 s outside every loaded zone (`FUN_1406bab60`, `FUN_1407ec570`) becomes an `UnloadedPlatoon` (`FUN_1407ef4b0`, its characters
copied into an in-memory record container, no file written) when its `persistent` flag (+0x115) is set or it is in a live campaign, else it is destroyed
(`FUN_1406ba9d0`). Town residents are made persistent (`FUN_14057f820`, with `sqt` 1); pool refills, homeless squads and `FUN_1409c4150` are not. A
non-persistent unloaded platoon is activated after 60 s (`FUN_1407ee790`) and then destroyed, so it lasts about 64 s out of range. An unloaded platoon
becomes active through a dwell timer (+0x78, **Verified** `FUN_1407ebf30`, `FUN_1407ef040`): 5 s when it becomes unloaded; while the zone at its
position is ready, outside the zone's box the timer is set to 2 s, inside it counts down, and the platoon activates below 0. The box
(`FUN_140a09770`) is the zone's bounds widened by 10, each side facing one of the four neighbours that exists and is neither loaded nor requested
moved in by 170 (about 178.5 for the stand-in stop test). Its characters are rebuilt from the kept records (Meitou: not at game load, which
activates at once behind the loading screen); at game load each PLATOON is made active or
unloaded by its zone (`FUN_1407f85f0`). `declareDead` (`FUN_140797090`, **Verified**) runs once from the platoon's position function (`FUN_1404fed50`, every step of a platoon of a non-player
faction) when no member is a character that is not dead: with the template's `regenerates` (+0xd9) and a town that is not `ded` it only marks the platoon to be remade at its next activation
(and may run again); else it logs "Platoon::declareDead: <name>" to death.log, takes the platoon out of its town's pool set (lowering the pool by its living count, 0 here), sets the saved
`dead` flag (+0x1f0) and clears `persistent` (destroyed at its next unload, its PLATOON record deleted by `FUN_1407ec5e0`; a campaign's platoon stays with its campaign, `FUN_1407ec570`); a save before then leaves it out unless the player is near ([save.md](../formats/save.md#npc-platoons-meitou-mapping)).
Meitou: `Platoon.Dead` (saved as `dead`, read at load), set in `StepLoaded`; the platoon leaves the pool counts at once, is not kept and is despawned when it unloads (its dead lie where they fell).
No distance, age or emptiness rule removes a persistent platoon. A wiped-out town refills only through its pools, with non-persistent squads;
no path re-running the initial resident placement was found (**Unknown** whether one exists).

**Active platoon out of zone** (**Verified** `FUN_14079c600`, called by the platoon step `FUN_1405003c0` each frame once `FUN_1404febd0`'s timer has run out): a platoon that is
kept (`FUN_1407ec570`) is unloaded as above (it stays active only while a member has `Character+0x1a9`, "in view"). One that is not kept is not destroyed whole: each member is tested
with `FUN_1407c06f0`, **out of zone** when its zone is neither loaded nor requested or it lies outside that zone's box trimmed by 170 (`FUN_140a09770`, as the activation box above; a
member with `+0x1a9` never is). An out-of-zone member is destroyed (`FUN_140799af0`, reason "wandered out of zone"); when the anchor (`FUN_140792b90`: the leader, else a member that is up
in a loaded or requested zone) is out of zone, so is every in-zone member not in view within 600 of it (squared 360000, `DAT_141714bd4`; 3D distance; reason "leader wandered out of zone").
The platoon stays active while any member is left, and the test repeats every frame; with none left it is destroyed as above. Before that, a platoon with 2 or more members whose members include one carried (`+0x3d4`), imprisoned
(`+0x2f8` = 2) or flagged `+0x320` runs the split pass `FUN_14079c1c0` (what +0x320 means is **Unknown**; see below). Meitou (`PopulationSystem.ThinOutOfZone`, Faithful
unload of a non-kept roaming squad, after its grace): the same per-member rule on living members, with the effective leader as anchor, except the in-view exemption (render state) and the split;
when no living member would stay the squad is despawned whole as before. Pool payment (`FUN_140796b30`,
by the platoon's cached living count) is **Unknown** for a partly destroyed platoon: Meitou pays only when the squad goes whole.

**Split at unload** (**Verified** `FUN_14079c1c0`, run for any platoon with 2+ members and no pointer at +0xe8): each member is skipped when its chain-owner handle (+0x330, a character or platoon) resolves to an
active owner (a chained slave whose owner exists); else it is split off when imprisoned (`+0x2f8` = 2) or carried (+0x3d4) by a resolvable character in a clause that is only partly read (**Unknown**). The split makes a new
squad of the same faction and template record at the member's place through the squad factory `FUN_1407f4420`, moves the member into it, marks it persistent when the old platoon is persistent or the member is unique, and
copies the old squad's mode fields; when the old platoon is persistent and has just this one member left it is reused instead. The new squad's blackboard gets the AI package `CAPTURED_NPC` ("captured NPC": goals
self-preservation, not unarmed, flee home, attack, "get out of cage - opportunistic escape"; `unloaded func` 4), or `5344-gamedata.base` ("Slave only squad") when the member's slave state is 1 (`FUN_1405c8aa0`); with no
blackboard the member is destroyed ("_putTheSpecialCharactersInNewSquads"). So a prisoner stays in the world as a squad of its own when its captors unload. Meitou: **not built** (the package could be given by name as dialogue's `change AI` does, but the new squad needs a platoon outside every town pool: a Meitou
platoon always has an origin town it counts against and, when roaming, runs trips; a platoon left with a caged member only stays put as a stand-in, `Platoon.Imprisoned`).

**The player's dead** (**Verified** by decompile; Meitou `SquadManagement.BuryDead`): the death handler (`FUN_1407a6200`, the "{1} is dead." strings) moves a
character whose faction is the player's (+0x250) out of its platoon (vfunc 0x18) into the faction's `__DEAD_SQUAD__` platoon (vfunc 0x10); NPC platoons keep their
dead. `FUN_1407f4960` makes that platoon the first time through the squad factory `FUN_1407f4420` with no template, sets its record name to `__DEAD_SQUAD__` and keeps its
handle in the game state (+0x2d0); the CAMERA loader (`FUN_1407f3fb0`) finds it again by that name. It is no squad tab, and import skips it ([save](../formats/save.md)).
**Unknown**: its file's string id, and `FUN_1407ebdd0(platoon, 0)` called on it and on an emptied platoon. The handler's `FUN_140797090` call when the dead is the
platoon's `+0xa0` member does not fit the `declareDead` reading above; unresolved.

### 3.1 Storage (**Observed**)

| Offset | Type | Meaning |
| ---: | --- | --- |
| +0 | byte | "allied" flag: forces `isAlly` true (set for the faction itself; other setters not found) |
| +2 | byte | at-war flag (set by declare-war, cleared by end-war; nothing found reads it, and the save has no key for it, **Observed**) |
| +3 | byte | coexistence flag (set from the FACTION `coexistence` list) |
| +4 | float | **relation**, -100 .. +100 |
| +8 | float | accumulated positive trust |
| +0xc | float | accumulated negative trust |

On the faction's FactionRelations object itself: +0x10 int rank, +0x18 float **global trust** (starts 0; slot 9 adds, DC_REPUTATION reads it as an int; not saved, **Observed**; the player table's saved `global trust` is read and written at +0x14, relation to +0x18 **Unknown**). Meitou: `FactionRelations.GlobalTrust` / `AddGlobalTrust`.

### 3.2 Faction rank

- The player's rank in a faction is an int at `FactionRelations` +0x10 (the faction's +0x78), 0 by default. **Verified** by code: saved as
  GAMESTATE_FACTION int `rank` (writer `FUN_1406b37b0`, reader `FUN_14003c2ea` / `FUN_1406b4dc0`; key string at 0x141700eb8), present on
  every non-player faction (102 per save in two saves).
- Read by DC_FACTION_RANK (10) and raised by DA_INCREASE_FACTION_RANK (35) of the **speaker's** faction ([dialogue](dialogue.md#effects)).
- **Verified** (probe of the merged data): only the Shinobi Thieves use it. Joining (`*You clasp hands* Welcome /BROTHER/...`) pays 10,000
  (DA 8), raises the rank to at most 1 (DA 35 val0 1) and relations +100 (DA 4); rank 0 lines turn the player away ("Talk to the boss",
  "I don't know you"), rank 1 lines open the shop and smuggling talk, and rank > 0 the free plastic surgeon.
- The FACTION loader (`Faction::setup` `FUN_1407fe270`) reads strings `rank0`, `rank1`, ... while the key exists into `Faction::ranks` (+0x18), **Verified**: United Cities 8
  (Foreigner .. General), Holy Nation 6, Shinobi Thieves 4; no reader of the titles exists (display data, **Observed**). `num ranks`, `num negative ranks` and `negative rank0`..`5`
  are not strings in the exe: never read.
- Meitou: `FactionRelations.Rank` / `RaiseRank`, loaded from and written to `rank`.

### 3.3 Thresholds (**Verified**: -30 and 50 appear in the predicate functions, in the threshold-message function `FUN_1406b28f0` and in the declare-war gate `FUN_1406b3000`; re-read independently)

| Predicate | Rule | Where |
| --- | --- | --- |
| hostile(A -> B) | B != A and relation(A -> B) <= -30 (default value when no entry) | `FUN_1406b25d0`, `FUN_1406b26d0` (identical bodies); called by `FUN_14092c5e0` (town choice) and `FUN_1409a7e00` (world state) |
| "default-hostile faction" | the faction's own table default (+0x60) is **strictly below** -30 (**Observed**) | inline in `FUN_1404374d0` (target choice, together with fundamental type 0 or 8) and in `FUN_14092c5e0` (combined with `ally()`); this is not the pair relation |
| ally(A -> B) | B == A, or allied flag, or relation >= 50 | `FUN_1406b2630` |
| coexists(A -> B) | B == A or coexistence flag; reads only A's own table, so it is directional (**Verified**, body). Read by the attack-on-sight finder `FUN_14059f3a0` (looker -> target) and ATTACK_ENEMIES_AND_NEUTRALS `FUN_14059f720`; Meitou `AggroSystem.AttacksOnSight` / `IsNeutral`. Systems with no looker use the symmetric `AggroSystem.IsEnemy` (engine choice) | `FUN_1406b23c0` |
| value as a fraction | relation x 0.01 (and its negative) | `FUN_1406b2460`, `FUN_1406b24c0` |

### 3.4 Changing relations (**Observed**; the event table cross-checked with the dialogue action numbers)

Vtables: `FactionRelations::vftable` 0x14171a958, `PlayerFactionRelations::vftable` 0x14171aa18 (slots 0/1 save/load, 10 the entry lookup).
The player's table has **empty** slots 2 to 5 (`FUN_140812e40`..`e70` return at once): no event or direct add ever changes the player's own
row; slots 6 to 9 are shared. Each slot works on the calling faction's entry about faction B; when B is the player's faction, slots 5, 7 and 8
post the threshold message of `FUN_1406b28f0` (section 3.3) with the calling faction's name.

| Slot | Function | Effect |
| ---: | --- | --- |
| 3 | `FUN_1406b3190` | relation -= 35 (if > -100), clamp |
| 4 | `FUN_1406b2ea0` | relation += a x b, clamp to [-100, 100] (direct add; `DA_AFFECT_RELATIONS`, slave trading +/-4, missions, the witness's -20 of 3.5) |
| 5 | `FUN_1406b2d20` | event of kind k and amount x, see below; does nothing when B is null or the owning faction has `not real` |
| 6 | `FUN_1406b2560` | a > 0: trust += a x b, else negative trust += a x b |
| 7 | `FUN_1406b2f90` | end war: clear war flag; if relation <= -25 set it to -25 (just above hostile) |
| 8 | `FUN_1406b3000` | declare war: only if relation >= -30 (not hostile yet): log "War breaks out between {1} and {2}" (owner, B) if the flag was clear, set the flag, set relation to -35 when it is -35 or more |
| 9 | `FUN_1406b2530` | global trust (+0x18) += a, no clamp (`DA_AFFECT_REPUTATION`, called on the **speaker's** faction) |

Kind rows (the move `FUN_1406b2730`: a fall applies only while relation > floor, a rise only while relation < ceiling, then clamp to [-100, 100]):

| Kind | delta | floor | ceiling | Fired by (3.5) |
| ---: | --- | ---: | ---: | --- |
| 1 | -0.05 x | -20 | 100 | a landed blow |
| 2 | -2.5 x | -50 | 100 | a downing blow by a low-rated striker |
| 3 | -10 x | -50 | 100 | a downing blow |
| 4 | -20 x | -100 | 100 | a killing blow (with 2 or 3) |
| 5 | -20 x | -75 | 100 | a death with a known killer |
| 8 | +0.15 x x dt | -100 | +5 | first aid to an unconscious or leg-collapsed (crawling) member (dt = `DAT_142133794`, scaled frame time) |
| 12 | -10 x | -60 | 100 | put in a cage |
| others | none | | | the bed put-in passes 9 |

### 3.5 What fires relation changes (**Observed**, decompile)

Found by a scan of every `CALL [reg+0x28]` preceded by a constant kind, and of the vcalls through `Faction+0x78`; only these call slot 5.
Every event is the **victim's** faction's table about the **doer's** faction, for any two factions (NPC fights included), with amount
**x = w x victim `faction importance`** (`FUN_140796020`; `Character+0x41c`, CHARACTER float read by `FUN_14062b210`; **Verified** by probe:
all 639 merged base CHARACTER records have it, 102 are 0, 371 are 1, uniques up to 100). w = 1 when the town the victim stands in
(status `+0x88`) is its own one (ownership object `+0x30`), 0.6 when that town's faction is `ally()` of the victim's (same faction
included), else 0.025 (outside every town too).

| Deed | Where | Events |
| --- | --- | --- |
| melee blow lands | `FUN_1404394f0` (Character slot 62) via `FUN_140999f00` | kind 1 when the striker's status float `+0xac` > 0.1 (and the victim's combat byte `+0x130` clear); after the damage, if the victim is unconscious or dead (`+0x5b9`/`+0x5bc`): kind 2 if striker `+0xac` <= 0.15 else 3, plus kind 4 if dead. Every later blow on a downed body repeats it |
| bolt lands | `FUN_140439c90` via `FUN_140999d50` | as melee, thresholds 0.1; with no shooter nothing; a bolt whose flag (param 4, read as "aimed") is clear gives no kind 1 and kind 2 |
| death | `FUN_1407a6200` (sets `+0x5bc`, runs inside the damage, so before the blow's own events) | kind 5 against the killer recorded in the character's handle at `+0x2c0` (**Unknown** setter), and squad tag 9 |
| caging | `Task_PutInSomething` `FUN_140358670`, after the bounty claim | kind = task `+0x7c`: 12 for the cage task, 9 (no row) for the bed (factory `FUN_14032ebd0`) against the putter's faction |
| first aid | `FUN_140649a00`, `FUN_14064d800` | kind 8 to the medic's faction when the patient's medical `+0x161` (unconscious) or `+0x160` (a leg-collapse part down, `FUN_140649320`; **Verified**) is set; in `FUN_140649a00` only on an update whose loop filled every wound. Meitou: `Unconscious` or `Crawling`, not dead |
| ally witness | `FUN_140854d10` | a witness that regards the suspect as friendly (vfunc `0x3f0`) and sees it wrong its own faction: slot 4, -20 (skipped when the witness platoon's `+0x210` object has `+0xa8`); for every crime code but 2, 10, 11, 14, 16; repeats on each witness pass |
| missions | `MissionClass` vfuncs `FUN_14039f580` / `FUN_14039f660` | trust (slot 6) and a direct add (slot 4) of the mission giver's faction about the player. **Dead in 1.0.68** (**Verified**): the only maker `FUN_14039f4b0` is reached only by an uncalled thunk, so the mission list is always empty and these never apply |
| dialogue | `FUN_140680560` | DA 4 slot 4, DA 5 slot 9, DA 13 slot 8, DA 14 slot 7, always the **speaker's** faction about the target's ([dialogue](dialogue.md#effects)) |

- No other caller of slot 7 or 8 was found: wars start and end only by dialogue (`FactionWarMgr` campaigns do not call them).
- Kind 8 is not a drift: nothing moves relations back over time.
- Status `+0xac` (Character slot 61 `FUN_140598b90` returns it) is summed by the perception (`FUN_140854370`) as a threat value; its setter is
  **Unknown**. Meitou rates every striker above both thresholds (kinds 1 and 3; engine choice).
- `FUN_140673a20(self, other, tag)` (not a witness function) sets a long-term tag (`CharacterPerceptionTags_LongTerm`, read by DC 23) in
  `self`'s squad memory (`platoon+0x98`, `FUN_140673930`, time-stamped): 1 MY_INTRUDER (trespass), 5 MY_CAPTOR (cage), 7 / 8 (the victim's
  squad tags the striker DEFEATED_MY_SQUAD_ONCE and the striker's tags the victim SQUAD_LOST_TO_ME_ONCE, on a downing blow when
  `FUN_140793160` finds the victim's squad all down), 9 KILLED_MY_FRIEND (death). Meitou keeps the memory ([dialogue](dialogue.md#squad-memory)) and writes 5 for cagings and arrests, 7, 8 and 9 for blows (`FactionDeeds.Remember`); trespass (1) is not written.

Meitou: `FactionDeeds` (blows, cagings, first aid), `CrimeSystem` (the ally witness), `PlayerRelations.DeclareWar` / `EndWar` / `Add`
(the messages); [simulation](../simulation.md#hostility-and-relations).

### 3.6 Factions met

**Observed** (decompile). The player faction's relation object (`PlayerFactionRelations`, RTTI) keeps a set of factions at +0x68; the faction tab
lists only those ([ui-screens](ui-screens.md#faction-tab)). `FUN_1407f7e90` adds one (none for null):
- When the factions are set up (`FUN_1406b40d0`, the player faction only): each faction its FACTION `relations` list names (base data: Dust Bandits
  alone, **Observed** by a probe of the merged data).
- On a new game (`FUN_140872e00`): each faction of the start-off's `faction relations`.
- A town or nest explored (`FUN_14092f6b0`, [map](ui-screens.md#map)): its owner.
- A player-faction character's sense update (`FUN_14085a8d0` → `FUN_14085a070` → `FUN_1408593d0`): the faction of everyone
  [gathered](perception.md#summary) but itself and of those its squad saw or noticed lately (`FUN_140860900`), seen or not. Characters sensed before
  are reassessed too, at any distance, with the faction they had when last seen or heard (`FUN_1408580d0`). Noticing someone (`FUN_140858f40`) makes
  it sensed: whom the character attacks (`FUN_1405cb0a0`); whoever shoots at it, at each shot (`FUN_14043a730` → `FUN_140999d50`; aiming alone does
  not, `FUN_1404371f0`), and on a hit by a bolt meant for it (`FUN_1404380e0`) while it is not fighting (`FUN_140439c90`); a melee attacker as it
  starts an attack on it (`FUN_1402b4570`), or as it makes it its target if, for a player-faction character, the two are on one floor, in the same
  building or both outside, with a line of sight (`FUN_140665580`, `FUN_140665650`; both `FUN_140999f00` via `FUN_1405c9290`); the attacker it picks
  to fight for itself or someone it senses (`FUN_14099c3b0`, below); the target of some of its dialogue actions (`FUN_140680560`); a failed
  knocker-out or kidnapper (`FUN_14034cd80`, `FUN_14034ffa0`, `FUN_1403552d0`), or a kidnapper, not its ally, that puts it down while it is
  conscious (`FUN_1405ce1e0`). That pick is the target finder of the GOAP task 13 "Attacking attackers of" (`FUN_140319a60`). The planner tries the
  task (`FUN_14051bae0`) when the character's self-preservation goal, which every character has (`FUN_140621b70`), needs the aggression towards it
  to end, when a protect goal finds an ally or squadmate in need (`FUN_14099ca10`, `FUN_14099d510`), and for goals that require it, such as
  following or guarding someone; player characters plan it without an order (`FUN_14061d350`, `FUN_1405e59b0`). The finder runs in full
  (`FUN_14060f460` → `FUN_14060f1d0`), its answer kept per task and subject until the caches are emptied at the start and end of each AI update
  (`FUN_1405112b0`). The update runs for 8 characters a frame in turn ([game loop](game-loop.md#the-character-update-budget)), never in the
  character editor; a carried or unconscious one only lets its senses fade (`FUN_140859220`; the get-up test `FUN_140598c20` fails for both), a dead
  one has no update.
- Selecting an object (`FUN_1407f7f20`, which inserts it itself, none for no owner; only when it was not selected, a building with a door as that
  door): its owner, at any distance.
- A reset (`FUN_14036cb80` → `FUN_1407fa4e0`, before every new game, load and import) empties it. A new game and a load then set the factions up and
  replace the set with the `known` list of the player's GAMESTATE_FACTION (`FUN_1409ef230` → `FUN_14036f2e0` → `FUN_1406b5190`; an id no faction has
  is dropped): on a new game the level data's, before the start-off's factions are added (none in the base game's level files, **Observed**), on a
  load the save's. Without one the setup's stay. An import replaces it only with Relations (`FUN_1409ef4e0`). Saved as that record's reference list
  `known` (`FUN_1406b3c80`, values 0).

Meitou: `PlayerState.KnownFactions`, saved as `known` in faction order after the ids the game data lacks (kept, where a load drops them: **engine
choice**). A new game starts from the `known` list of the level data's player faction state when there is one, the only field of it read
(`PopulationData.KnownAtNewGame`). `PopulationSystem.MeetFactions` runs each player character's update every 15 ticks by slot and counts a
character's current faction and the fight target while there is one; a shot at one that senses meets the shooter's at once (`CombatSystem.ShotAt`,
crossbows and turrets); inside town walls is Meitou's gate code above 0 at the character's position now, not a kept value (all **engine choice**). The sight range is
perception's R ([perception](perception.md#summary)): 500 inside town walls, else 1500 x race `vision range mult`, both x lerp(daylight, 0.5, 1) and x weather.
Not modelled: reassessing those sensed before (other than the fight target), the squad's shared list, and a shot
at one that is down (the original keeps the shooter sensed while it fades, `FUN_140859220`, and meets it if the character gets up first); the other
ways to notice someone are taken to act within the sight range the pass covers (**engine reading**). Selecting meets a faction too: `SelectCommand`
the player's, `InspectCommand` the character's, `InspectBuildingCommand` the owner of the building's first door, else of its first fixture (a
building with no fixtures is left out). A save whose `known` list is empty (older Meitou saves wrote one) loads the setup's and the owners of the towns and
camps it has explored (**engine choice**).

### 4. Squad templates

| Field | Meaning in the exe |
| --- | --- |
| `leader` (CHARACTER) | **Only the first entry**, one character (**Observed**, `FUN_140583a10`) |
| `squad` / `squad2` (CHARACTER, v0, v1) | the two member groups, see 5.1 for the counts (`squad2` is the "second half": member role 1) |
| `choosefrom list` + `num random chars` / `num random chars max` | N random characters from the list, weighted by v0 (chance, 0 counts as 100) |
| `animals` / `animals2` (ANIMAL_CHARACTER, v0, v1, v2 = age 0..100) | animal members, 5.1 |
| `slaves`, `prisoners` (SQUAD_TEMPLATE) | whole squads created recursively, 5.2 |
| `housemates` (SQUAD_TEMPLATE, min, max) | further squads spawned with this one (`FUN_14057f820`) |
| `faction` | owner faction; if absent the spawn code asks the owning town's faction, else with no town takes the first faction in load order whose `squads` or `squad default` lists the template (`FUN_1402e7d10`); when that gives nothing, or a named faction does not resolve, the faction `Wildlife` (`3943-gamedata.base`) (**Verified**, `SpawnInfoList::vfunc_0` = `FUN_1408fe6b0`; Meitou `PopulationData.SpawnFactionOf`) |
| `world state` | AND gate for the spawn |
| `roaming military` | forces squad mode 2 ("roaming") even with a home town (**Verified**, `FUN_140583a10` before `FUN_1409c44b0`). The mode (1 residents, 2 roaming) only picks the town pool set the platoon is registered in (`FUN_1407ec8d0`); it still gets its home and stands in the town (67 base templates; 15 of them are in `residents`, `default resident` or `bar squads` lists, e.g. Holy Guards, Holy Army Patrol). Meitou: only the town wipe reads it (no resident there, counted with the roaming living); the pool budgets and the regeneration pass are unchanged (**Unknown** which pool the original's budget sums take them in) |
| `dont multiply` | exempts the template from the *Squad size multiplier* setting |
| `AI packages`, `blood smell mult`, `patrol approaches towns`, `force speed` ([speed modes](pathfinding.md#force-speed)), `personality`, `dialog *`, `nest`, ... | copied onto the platoon / its characters (`FUN_14057f4c0`, `read_AI_packages_n4`; the package system is in [ai.md](ai.md#other-records-that-touch-the-ai)) |
| `building`, `building dislike`, `building designation`, `layout interior/exterior`, `public day/night`, `public beds`, `bed usage cost`, `sell home`, `building ruined`, `initial door state` | how the squad's home building is chosen and set up (resident squads) |
| `vendors`, `is trader`, `vendor money`, `vendors fill total amount`, `vendors refresh time`, `buy mult`, `buys stolen`, `buys illegal`, `special items`, `special map items`, `*artifacts base value`, `regenerates` | shop and contents (section 8 for loot; the trading economy is in [economy.md](economy.md#shops-and-transactions)) |

### Building swaps

FACTION `buildings replacements` (BUILDINGS_SWAP, type 103, 40 records): `Faction::setup` builds one swap per record (`FUN_1407fd120`): a set of `to replace` BUILDINGs and
a weighted choice of `replace with` (weight v0). `RootObjectFactory::createBuilding` asks `Faction::getBuildingReplacement` `FUN_1407f4770` (**Verified**) for buildings whose
flag `+0xa8` wants it: the first set holding the building answers nothing when `replace with` is empty (the building is not made, e.g. "no carpets"), its one entry, or a weighted pick
(a C `rand()` roll in [0, total weight], the first cumulative key above it; not seeded by anything the game saves); no set: the building itself. The flag is not a placement field:
`createBuilding` `FUN_14057cc70` reads it from the zone's content object (the zone slot `FUN_140a08ae0`, 0x168 bytes each), where `+0xa8` is `ZoneMapContent::newGameFirstTimeLoaded`
([save.md](../formats/save.md); **Observed**, the identity rests on that one offset; the decompile's `Vector3 *` here is one byte wide, as the position floats read at `+0` / `+4` / `+8` show, so `[0xa8]` is the byte at `+0xa8`), so swaps happen only while a zone's buildings are made fresh in a new game, never for a zone loaded
from a save. The faction asked is the passed owner faction, else the owning town's, with a further town-based override for non-player owners whose exact conditions (the
`createBuilding` parameters `param_8`, `param_14`, the "creates player town" flag) are **Unknown**. Not modelled (the level builds every building as placed; the swap would touch the
town building list, fixtures, turrets and the drawn world alike, so it needs one shared resolution of the level's buildings first).

### Loose items in buildings

**Verified** by code (`BuildingItemGroup`: constructor `FUN_1405534f0`, timer `vfunc_28` `FUN_140549e60`, spawn `FUN_140547ed0`, pick `FUN_1409540d0`, grade
`FUN_14063cea0` / `FUN_140952320`, save `vfunc_17` `FUN_1405567b0`) and a census of the merged install data and `interiors.level`:

- Interior layouts (`interiors.level` INSTANCE_COLLECTIONs) place ITEM_PLACEMENT_GROUP records like furniture: 4987 objects in the layouts of 31 associated buildings
  (15307 slots over 129 towns once the level buildings take their layouts), each with its own state (`floornum`, `respawn time`, `is inside building`, `isInsideBuilding`
  handle). The 21 groups have a vendor list's lists: `items`, `weapons`, `clothing`, `weapon manufacturers` (v0 the weight, 5..500), plus `random yaw` (true in all),
  `min respawn time` / `max respawn time` (24 / 48 in 19, 72 / 72 in 2) and `placeholder` "".
- A placed group is one item slot at its object's position and rotation, with a handle to its item and a respawn deadline in game hours (0 at start). Each update with no
  item: once the clock reaches the deadline (and a test on the building's town `+0x1d8`, **Unknown**) it makes one. With an item whose handle is still a WEAPON, ARMOUR,
  ITEM, CONTAINER, NEST_ITEM, MAP_ITEM, CROSSBOW or LIMB_REPLACEMENT that is not taken: nothing. Otherwise the handle clears and the deadline becomes now + an int in
  [min, max] hours. The slot's state is the spawned object's handle type (`FUN_140547ed0`), so a BLUEPRINT (0x5a) is let go on the next update and the slot fills again at
  the deadline though the blueprint still lies there (Meitou `ItemSlots.Kept`: it lies as a loose item from the start).
- Making one: a weighted pick over the group's lists (a roll in [0, total weight) against running sums); a WEAPON pick takes a manufacturer from `weapon manufacturers`
  and the weapon is made by it (**Verified**, `FUN_1409540d0` / `FUN_140580750`): with no manufacturer no item is made and the slot stays empty (tried again next update); a model from its `weapon models` (v1 weight) whose v0 is the level, and a manufacturer with no model or a level-0 model is replaced by manufacturer `1057-gamedata.base` with model `1058-gamedata.base` (level from the 1057 entry for 1058, plus the 1057 `overall level`; shops use the same factory). Every item gets the quality of a weighted grade (0..5 -> 5, 20, 40, 60, 80, 95); the grade weights' source is **Unknown** (the item-list
  object is cached per group record and a value of the building, probably its faction). The item gets a value of the group (**Observed**: likely the owner faction) and is put
  at the slot. A picked BLUEPRINT or MAP_ITEM record is also taken out of the group's pool (`FUN_140969100` from `FUN_1409540d0`); how it comes back is **Unknown** (not built).
- Saved per group (in its generated interior): `base data sid`, `floornum`, `isInsideBuilding` handle, `is inside building`, `respawn time`, then its item.

Meitou: [ground items](../simulation.md#items-and-eating).

### Town residents

**Observed** (kenshi-named dump) unless marked; Meitou: `PopulationHomes.cs`.

- **Table** (`FUN_140935d10`, `FUN_140936e60`): the faction's `residents` then the town's; with `residents override` the
  town's only. A template already listed is skipped (first wins). Per entry: v0 = count, v1 = priority (100 counts as 0,
  outside 0..5 an error box). `default resident`: the town's, else (no override) the faction's. `bar squads`: the
  faction's (TownType 2 only, no override) then the town's; with override the town's only. **Verified**: a town whose
  faction object is null (no `faction` reference, or one that matches no FACTION: `FUN_1409fe7a0` sets it only when it
  resolves, the base constructor leaves it null) builds nothing: `FUN_140936e60` returns before the table, the default
  resident and the built flag (`residents override` is read only after that). The bar squads come from the same table
  (`FUN_1409fcd20` calls it first and loops over its entry count), so they are empty too. Base data: 4 of the 22 faction-less TOWN
  records have residents (housecamp raider variants, Skinhouse overrides); they get none. Meitou: the helpers in `PopulationSystem.Build.cs`.
- **When**: at zone populate (`FUN_140a0e760`) per non-player town, the zone's buildings sorted by handle serial (random-looking).
- **Candidates**: buildings of the town ([membership](../formats/zones.md#town-membership)) with an `interior` reference, not player-owned, holding no residents template
  or the default one; walls (`links together`, a vfunc override) never, gates (`is wall gate`) only for entries naming
  `building`s. Level data agrees: 1946 walls, none with a resident.
- **Driver** (`FUN_1409370d0`): re-owns the buildings to the town's faction; priority 5 down to 0, each entry with count
  left gets one building per pass, passes repeat while one placed and has more. An entry with no building left makes
  no more squads (Stack: civilians v0 9, 2 made). Then every free building whose interiors take the default resident's
  `layout interior` gets it (`FUN_14092e4b0`).
- **Chooser** (`FUN_14092e870`): a template with `layout interior` needs a building offering it (own layouts or
  `shares interiors with`; gates exempt); without one, an entry naming buildings takes only those. Weight 1, x 0.4 when
  `layout exterior` is not offered, x 10 named in `building`, x 0.05 in `building dislike`; highest wins, first on a tie.
- **Squads** (`FUN_14057f820`): `housemates` (v0, or Rand(v0, v1) when v1 > 0) first, then the squad. Platoon point: a
  random free node of NodeType 0 (SHOPKEEPER), else 2 (GUARD_POST), else 1 (GENERAL), else the building. Characters
  (`FUN_140583a10`): a GENERAL node, else SHOPKEEPER, at node + (0, 10, 0), else the platoon point. The squad's home is the building.
- **Bar squads** (`FUN_1409fcd20`, **Verified** by decompile and constants): n = v0 > 2 ? (int)(v1 x 0.5 x 0.0099999998 x v0) : 0 (the second factor is the double nearest the
  float 0.01, so v1 100, v0 4 gives 1, not 2), then +1 per Rand(0, 100) <= v1, stopping
  at the first failure, at most v0. Each at a random node of the town's node map (`FUN_14038b000`), else the town
  position; no home. That map (NodeList +0x50) is filled by `FUN_14038b650` with nodes the game creates itself next to objects
  (record `1186-gamedata.base`, "Node Guardpost", node type 2); which objects is **Unknown** (Meitou: GENERAL nodes of its bars).
- **New game vs level data**: level-data building states carry `residents template` / `squad template`; save 2 against
  them has the same multiset per town but shuffled across buildings (223 same, 125 moved), so a new game rerolls homes
  (**Observed**). Destroyed buildings stay candidates (the Hub's ruins house squatters).
- **Meitou engine choices**: the level data's assignment is kept where it fits the rules (interiors are drawn from it,
  not relaid to the template's layout); on a tie a building already laid out as the template wants wins; bar squads
  stand in the town's bars and take the nearest bar as home (their templates, e.g. `Bar thugs`, have designation 0, **Verified** in the data); a loaded town keeps its saved squads, homes and bar squads and rolls no new bar squads.

### Off-screen world

**Verified** by code (kenshi-named dump) unless marked. Who is kept off screen: [persistent platoons](#1-object-model).

- **No world pass**: work is spread over round robins. Each faction steps one platoon of its list per frame (`FUN_1406b9970`, unloaded
  ones only); one faction per frame runs its war manager (`FUN_1406b97c0` → `FUN_1409cb310`); one town and one nest per frame get growth /
  spawn / wipe (`TownList::vfunc_4`); the area tick every 2 s (new nests, homeless squads), sector housekeeping every 10 s. Each unloaded
  step measures its own elapsed game time, and growth and campaign timers read the game clock, so no rate depends on these periods.
- **Unloaded step** (`UnloadedPlatoon::vfunc_2`, `0x1407ee790`): activation test, the 60 s orphan timer, then the mover is stopped and nothing more happens (**Verified**) for a platoon of the
  player's faction, one with no zone record at its place (off the grid), one in a loaded or requested zone and inside that zone's box trimmed by 178.5 (`FUN_140a09880`, `DAT_141718a0c`: it waits to be
  activated), and one that is imprisoned (the platoon flag +0x1f1, set at each unload to whether a member is in state 2 and saved as `imprisoned`, **Verified**
  `FUN_1407ef4b0`); otherwise the job: a campaign's if the war manager holds the platoon, else the package's `unloaded func`
  ([ai.md](ai.md)); job 0 becomes 2 for a non-persistent platoon and 4 (GOHOME) for a persistent one. No character data is read or changed:
  no healing, needs or fights (**Observed**).
  - GOHOME: to the home only once within 2000, else to the object of a platoon handle (+0xb8, meaning **Unknown**) at 55.
  - TRAVEL_TARGET / _FAST: to the mission target: the blackboard's override package's, else the campaign's target, else the current
    package's saved `mission target`, else a fallback (`FUN_140269cd0`); no move when it is the town the squad is in (**Observed**); a campaign's `travel speed unloaded` WALK / JOG / RUN gives 25 / 55 / 90;
    otherwise (GROUPED, NO_SPEED_CHANGE, no campaign) FAST 60 (90 beyond 25000), TRAVEL_TARGET 25. The mover caps speed at 70 (**Observed**: no other writer of the cap).
  - PATROL_SHORTRANGE: picks a point in a neighbouring 4608 sector of the same biome group (sometimes a town, `patrol approaches towns`)
    but sets no speed, so it does not move (**Observed**). PATROL_TOWN, PATROL_LONGRANGE: no branch, no movement.
  - Movement is a straight line, no navmesh. Roads (`0x14065f940`, **Verified** unless marked): for a destination over 10000 away with
    w = max(0, 1 - `road preference`) < 1, the entry and exit are the nearest projected points on any road within 20000 (X/Z; roads with
    the +8 flag skipped), scored by distance minus a pull towards the travel direction (reversed for the exit) of clamp(w, 0.2, 0.9). The road is
    taken when the two access legs together, and the legs plus w × the A* road length (`FUN_14045da60`; cost **Unknown**), are each no longer
    than the straight line; the mover then heads for the first waypoint, keeping the final destination. Road data: ROAD records (`start`, `end`,
    `length`, `width`, `weight`, `hidden`), their instances the polyline points; `start`/`end` are junction numbers, each road one edge costing its
    `length` (not its polyline; `weight` unread), the A* heuristic the straight distance to the goal, and `hidden` never read by routing (**Verified**).
- **Meetings**: nothing looks at other platoons; there is no off-screen combat. Off-screen outcomes are campaign phases and despawning.
- **Pool growth** (`FUN_1408f4c20`): each pool's current max (`pop`) grows `+0.21818` per game hour (5.24 a day) up to its desired max
  (`popd`), for every town whether loaded or not; roaming grows only once residents are within 1 of full, and
  keeps its time stamp meanwhile, so the waiting time is credited when it next grows (**Verified**, `FUN_1408f6760`, `FUN_1408f4c20`). Towns never spawn squads over
  time by themselves (only nests call the pool spawner). Once per town load (`0x14092a020`, **Verified**) a squad that lost ≥ 0.6 × its spawn size (double compare), if
  the budget allows, is marked to be made afresh from its template on its next activation. The same pass runs over the roaming pool (its own budget, shared running total) and a flag at `+0xd9` forces a resident squad's mark past both tests; neither is modelled (flag's source **Unknown**).
- **Despawn cost** (**Verified**, `FUN_1406bab60`, `FUN_140796b30`): a non-kept platoon that leaves while not intact (the platoon flag, below)
  is taken out of its pool (platoon +0xa8: 1 residents, 2 roaming) and, while that pool is above 0, lowers it by its living members (no floor);
  a `regenerates` platoon (+0xd9) is charged nothing and only marked to be remade. The platoon's faction's prosperity also drops by 10, floor 0, when its +0xa4 is above 0, pool or not (`FUN_1406bab60`, **Verified**; +0xa4 is the count `FUN_14079acf0` compares with 4, read as the member count: **Observed**). Meitou: roamers and a save's wild platoons that despawn Faithful (`PayRoamerDespawn`, both costs) and nests
  (`PayNestDespawn`, still floored at 0 with "lost someone" as not intact). Not applied: the prosperity drop for nest squads and sector-made homeless squads (the original's covers every non-kept platoon).
- **Town pools in saves** (**Observed**, saves 1, 2, wade): every town has `pop` = `popd`, `started` true when populated, `popd2` 0 (none of
  304), while `read_roaming_population_n4` (`0x1408f98d0`) would set pool 2 to the faction's `roaming population` (no caller found:
  **Unknown** when it runs). `pop` is near the living members of the town's resident platoons (`is resident`) of the town's own faction:
  save 2 Squin 36 = 36, the Hub 12 vs 10, Stack 93 vs 95 (Squin without a Tech Hunters resident shop of 4).
- **Town wipe** (**Verified**, `TownPopulationManager::vfunc_4` `FUN_1408f7a80`): once started, roaming `pop` <= 3, <= 25 roaming living and no intact resident platoon
  (more than 200 unresolvable handles in the set, or any at all, also keep it) sets `ded`, saved, never cleared. `setDead` is empty for towns (only nests remove themselves), so
  nothing is shown; a dead town skips the remake pass at activation and its `regenerates` squads die. The loop over the resident set (**Verified**): a platoon that resolves and is intact
  (`isIntact` `FUN_1407ac7b0`: false once `dead`, else the flag +0x100, which a loaded platoon recomputes with the squad-intact rule `FUN_1407a5be0`: a member held in a cage or carried by a
  non-ally keeps it intact, and a platoon of the player's faction is always intact, `FUN_14079acf0`) keeps the town at once; one that resolves and is not intact is skipped; a handle that does not resolve to a
  platoon counts, and with any counted (or over 200) the town is kept. The flag is saved as `intact`. Meitou (`CheckTownWipes`, each roam check): resident = any town squad or pinned platoon with a home that is
  intact (a loaded squad's AI flag, an unloaded platoon's flag; for the unloaded Meitou also wants someone alive, as its off-screen fights kill members the flag never saw), except a `roaming military` template's squads, which are roaming-pool members (counted with the roaming living); a town still loading is not judged.
  The check runs only while the zone at the town's position is loaded (zone byte +0xb1, **Verified** `FUN_1408f6760`). The wipe gates have no faction test; Meitou keeping a town of the player's faction is an
  engine choice (the town list's caller was not read).
- **`regenerates`** (SQUAD_TEMPLATE, 18 true; `Platoon::declareDead`, **Verified**): when the last member dies and the home town is not `ded`, the platoon is not declared
  dead but set to CM_REFRESH (remade on next activation). Meitou: a loaded pinned platoon whose members are all dead (`DeclareDead`), a town squad that unloads with no one alive, or an
  empty pinned platoon at the town's load, is marked to regenerate (whatever the budget).
- **Nests** (`NestPopulationManager::vfunc_4`): pool 2 starts at 0.7 × `nest resident population` and grows as above. While the nest's
  zone is loaded it spawns one squad per visit, and it is visited once every N frames, N = every nest in the list, loaded or not (`TownList::vfunc_4`'s nest cursor, **Verified**) (weight `max(0, v0 − existing) × faction wealth`; an entry passes when its size fits the free budget `floor(pop2)` − living, or
  the pool is empty and more than 4 are free, so a squad bigger than the budget can open a nest: `FUN_1408f7e80`, **Verified** by code) unless a
  player-faction unit is within 1250. With no candidate and no intact squad the nest is `ded` ("wiped out", game hour stored as `tod`,
  **Observed**) and never spawns again. New nests come only from the area tick, up to `num nests` per biome group; whether a dead nest
  frees its count is **Unknown**. A group with `nests at fixed markers only` picks among its NEST_MARKER towns, leaving out every marker whose
  nearest town (by XZ, markers skipped) is within 2500 (3D) (**Verified**, `FUN_1408f6350`, 6,250,000 squared).
- **Homeless squads** (area tick `FUN_1408f9240`, every 2 s, per island): the island's load (sum over its loaded sectors of intact +
  0.5 × not-intact squads, [counts](#off-screen-world) above; clamped to [0, 2] for a sector with its own town or the town flag `FUN_1408f62e0` / +0x30) is compared with `FUN_1408f5ca0`, the sum of P × `homeless spawn
  amount` over the loaded zones of the island; while below, one sector is picked by weight max(0, P × amount + 1 − load), halved when its own town stands in it (`FUN_1408f62e0`; only a
  sector the flag leaves free, e.g. Ruins), (w + 1) × 2 when its load is 0 (**Verified**, `FUN_1408f9240`), and one squad is made there (`FUN_1408f8060`) if its intact count is below P × amount, retrying other sectors when that
  fails: the draw takes each sector out of the tree and loops while `FUN_1408f8060` returns 0 (no template left, no spot `FUN_1408f50d0`, or the spot inside a town, `FUN_140926d50`; 1
  only after the squad is created; **Verified**), so one 2 s tick ends with a squad or an empty tree. Meitou: the sectors in weighted draw order without replacement, built in turn by one job until one makes a squad. Sectors a town stands in never enter the pick: `Town::setup` (`FUN_1409353c0`) sets sector byte +0x30 for every sector of the
  town's zone list (or of its position), +0x31 when the Town int at +0xd8 is 1 (its type, **Observed**), unless the type (+0xd8) is 4 (Ruins) or 9 (point of interest), so a
  Ruins town does not block its sectors and a nest marker (type 8, which `setup` handles itself) does; the pick, the spot fallbacks `FUN_1408f6350` / `FUN_1408fc1a0` and
  the patrol's town branch (below) read it (**Verified**, decompile; whether `setup` runs for the Null type (10) is **Unknown**). Meitou gives a sector
  in such a town's zone list (else its position's zone) weight 0, re-read each refill so town overrides apply (the patrol's town draw does not read the flag: it
  lists a sector's towns but nest markers, points of interest and Null, Ruins included). The sector in view is never drawn either: the draw loop redraws a sector equal to
  `DAT_142135230`, which `FUN_1408f6940` (every frame, from the zone update `FUN_140a12c70`) sets to the sector at the GameWorld +0x580 object's position (`FUN_140785f90` → `FUN_1407f2960`: its scene node;
  **Verified**). +0x580 is `DAT_142134690`, the camera focus object of [corpses](character-stats.md#corpses) (same address, **Verified**; its constructor `FUN_1407f6b30` is `PlayerInterface`).
  Meitou: weight 0 for the zone of the focus the zone loader uses (`FocusCommand`, else the first player character); a sector's load-time fill is not subject to it (engine choice). So a loaded sector is refilled as its squads die or walk out (**Verified** by code). The squad is given a home town: the
  sector's own town when its faction is the squad's, else the faction's nearest town, or its nearest nest when that is 9216 nearer, none
  beyond 50000 (`createRandomUnloadedSquad` → `Ownerships::setHomeTown`, KenshiLib 1ce4a16 names; **Verified** by code). It tethers the
  patrol (below). Saved homeless platoons have no `basetown`/`hometown` ([save.md](../formats/save.md)); whether the home is saved
  elsewhere is **Unknown**. Meitou decides it at the squad's first patrol, from where it stands (engine choice, so saved squads get one).
- **Patrol task** (`Task_Patrol`, loaded; **Verified** by code and disassembly unless marked): the platoon's `PatrolInfo` (+0x1e8: biome
  group, has-home flag and home town position, sector range near/far, `patrol approaches towns`, a smell factor) is made once; the
  start sets the range to (1, 2), or (0, 1) when the platoon has a home town, then orders a move whose destination (`CharMovement`
  slot 20, mode 3) is drawn by `FUN_1408fc730` from the leader's position:
  1. A coin flip takes the four diagonal or the four straight neighbouring sectors; those of the same biome group with sector flag
     +0x32 clear (its writer not found: **Unknown**) and weight > 0 enter a cumulative pick.
  2. Weight (`FUN_1408f5ad0`): 1 for a sector whose zone is not active. Active: n = its counted platoons + 0.5 × the others (the area
     tick's sets: non-persistent platoons not homed to a nest are counted); 10 / n when n = 0 (an infinite weight; the pick then
     runs off its map, so the result is undefined), 5 / n when n < 2, else 1. Plus the zone's smell × the smell factor, but that factor
     is `blood smell mult` read from the template's bool fields, so it is always 0 (**Observed**). With a home town, × 5 when the
     sector's Chebyshev distance d from the home's sector equals near or far, × 10 between, × 0 outside: a homed patrol stays within one
     sector of its home's sector, and homeless squads have one (above).
  3. With `patrol approaches towns` and the drawn sector's town flag +0x30 (above), the towns in it (`TownList::getTownsInArea`): each
     player town enters at even odds with weight 1 and each other town at 1 in 4 with weight 0.2, hostility unchecked; a drawn town's
     `getPositionForWaypoint` (`TownBase`, `FUN_140927120`, vtable +0x38) is the target. Else a placed spot in that sector
     (`FUN_1408f50d0`); with no neighbour, `FUN_1408fc1a0` takes the first active sector of the group (no town flag, fewer than 2
     excluded platoons) where a spot is placed, else a random sector of the group; it ignores the tether.
  **The town waypoint is outside the town** (**Verified**, decompile): `getPositionForWaypoint` caches its point (`TownPositionCacher`,
  Town +0xc0) and, for a town with a building list (vtable +0x2f0: Town +0x3b0 set), takes `Town::getPositionOutsideTownGates(200)`
  (`FUN_140929560`): one pass over the building list, returning at the first entry that is either a gateway (`isGate`) with
  `amInsideTownWalls` 0 and a door (`getDoor`), whose door point +0x3bc is moved 200 along a normalised direction (its origin is dropped
  by the decompile; outward from the town is **Observed** from the name), or a wall (`isAWall`) whose BUILDING record has `interior` references (`myInterior` +0x1f0, created only then by `Building::setup` `FUN_140551b20`; **Verified**, decompile), whose position
  is moved the same way; neither: the town position. A town without buildings: a door point of the object under the town position
  (`FUN_1409b2ab0`, a ray from 1000 above), else the position. So bandits never aim inside a walled town, the Hub included (its two
  `Defensive Gate IV` are ruins in the level data; a ruin has no door ([locks](locks.md#doors)), so the wall branch applies, skipping the first wall, a `Defensive Wall IV Connector` without `interior`; **Observed**).
  The move: `CharMovement::setPatrolInput` (`FUN_14065eaa0`, vtable +0xa0) in mode 3 (a `PatrolInfo` with a biome group) draws the point
  above and sends the character to it exactly; it redraws (up to 10 times) only a point whose ground is present and below 95 when the
  byte at +0x79 of the object at Character +0x2e0 is set (a race flag, water below 95: **Observed**), else accepts it (**Verified**). The
  1250 in `FUN_1405c6b30` is the order's scatter radius, used only by other modes: there the destination is jittered ±radius/2 in X/Z, and
  a point with a building within 300 is rejected (retry, up to 10) unless that building is public, or its owner is the mover's ally or the
  player faction, and it lies in the mover's own town (**Observed**, decompile; the earlier "radius 1250 (Observed)" reading was wrong).
  A loaded patrol draws again when the task's update finds the move ended (`FUN_1405c6b30` mode 1 returns unless the mover reports
  idle, reached or failed), so the squad walks up to 200 outside the gate, stops, and turns to the next sector or town. Off screen a new
  point is picked within 500 of the last (`FUN_1407ee790`).
  Meitou (`PopulationHomeless.PatrolPoint`): the weights above with n the homeless squads standing in the zone and 10 for an empty active
  one (engine choice), the tether, re-pick within 500; with no neighbour, a point of the home's sector, else of its own (engine choice).
  A drawn town's point is the outside point above (`OutsideTown`: the first non-ruined `is wall gate` building with gate code 0 or unknown (unknown = outside, engine choice) or `links together` building with `interior` references, its origin
  moved 200 outward from the town position); the squad mills a 50 disc round it and re-picks within 100 (origin, outward and both radii: engine choices).
  `patrol approaches towns` is set on 933 of 961 squad templates of the merged load order, the Starving Bandit and Dust Bandit roamers
  among them, so their patrols target NPC towns as above; no other town check was found (**Verified**, record dump).
- **Traders**: restocked only while active, when the clock passes the platoon's `inventory refresh time`; an unloaded one catches up at
  its first active tick.
- **Prisoners** (**Observed**): frozen with their platoon; sentences are absolute timestamps and release is a loaded AI task.
- **Campaigns**: [below](#campaigns); they run whether loaded or not.

### Campaigns

Raids, assaults, tax/tribute and trader visits, ally reinforcements: one `FactionWarMgr` per faction (Faction +0x88), stepped in the round
robin above. **Verified** by code (kenshi-named dump; disassembly where noted) unless marked. Save layout: [WAR_SAVESTATE](../formats/save.md#war_savestate-type-9-faction-campaigns).

**Data** (**Verified**: merged base files, 179 FACTION_CAMPAIGN records). Content is aimed at the player: raids and assaults on player
towns, hunts after story kills, visits, ally help; chains via victory/loss triggers. No NPC-town-against-NPC-town campaign exists.
- Rolled ones: a FACTION's `campaigns` list (77 links, 24 factions; ref values ignored) with `territorial triggers` (60 of them).
  `chance per day` 0.05-2 (mostly 0.1-0.5); `range near`/`range far` in tenths of a unit (e.g. 2500/9000; 50000/90000 = the whole map).
- `key` = the editor's `WarCampaignEnum`: 0 ASSAULT_TOWN (159), 1 CONQUER_TOWN (none), 2 DEFEND_TOWN (13: "Ally ... (trigger)"),
  3 TRADER_VISIT (7: merchant visits, crab champion).
- `travel speed loaded/unloaded` = `MoveSpeed`: WALK 0, JOG 1, RUN 2, GROUPED 3, NO_SPEED_CHANGE 4 (data mostly 4/1).
- `num forces` 2-80 (characters); `repeat limit` days (4 typical, 9999+ = once); `target population min` 0 (163) or 1-6.
- `repetition limit` (33 records) belongs to dialogue and is unread here; `lock campaign` (one DIALOGUE_LINE) is unread by the exe
  (**Observed**: no such string). `victory AI leader/others` (13) are unread: the loader builds only `pt1 AI`, `pt2 AI`, `retreat AI`.
- `inherits from` (144 records, up to 2 parents; AI packages live on BASE parents): every ancestor is applied twice, by the loader
  and again by the record reader (**Observed**). Appended lists (`squads to use`, `special leader`, victory/loss triggers) thus count an
  ancestor's entries twice (double weight); AI packages and `trigger player ally` are inherited (the child wins where set); `world state`
  merges by query. Own-record only: scalars and bools, ranges, chance, `key`, `num forces`, `faction override`, speeds, `dialog announcement`.

**Roll** (`FUN_1409cb310`): each run updates every active campaign, then the requests, then, when the clock passes `updatetime`, sets it
to now + 6 h and, only if the faction has no active campaign and no pending request, rolls (**Verified** +6; gate **Observed**).
- Each `territorial triggers` campaign of the list with a target gets chance c; it passes when c > uniform[0,1); one passer is drawn,
  weighted by c (**Verified** by disassembly).
- c (`CampaignData::vfunc_1`), 0 on any failure: against a player-faction target, the player's living characters ≥ `target population min` and a Town
  target's level (+0x388, the save's `plevel`, below) ≥ `target tech level >=` ("-below town level"); now > last start + `repeat limit` × 24 h; world states and special leader (below); d = XZ
  distance from the faction's nearest own town to the target: f = 1 when `range far` ≤ 0 or d ≤ near × 10, 0 beyond far × 10, else
  linear from 1 to 0 between; f × `raidFrequencyMult` unless `ignores chance mults`; c = `chance per day` × 0.25 × f (**Verified**).
- Target (`CampaignData::vfunc_0`): only towns of the player faction, those in the faction's `biomes` (BIOME_GROUP), else any player town;
  dropped when Town slot 70 is false, when the town has hostile occupiers (+0x128) and the campaign is not hostile, or in the faction's `no-go zones`
  unless `ignores nogo zones`. Slot 70 (`FUN_140929eb0`, **Verified**; the zone flag `FUN_140a09360` is read as loaded, **Observed**): the town's zone list has a loaded zone (else the zone at
  the town's cell), and, for a town whose faction is the player's, some member of the faction's loaded platoons has this town as its current town (Character slot 15 `FUN_1405c89b0`: the
  `+0x88` of the object at Character `+0x1a0`, the field the AI's current-town rule `FUN_1402688d0` writes from the leader, [ai](ai.md#package-classes)); a town of another faction is active when the zone test
  passes. So rolls (and redirects) only aim at a player town the player is at. Meitou: `TownActive`, the AI's `TownAt` (nearest town whose radius holds the character) for each
  player character, no zone test for a player town (its zone list is not modelled; the character keeps its own zone loaded). +0x128 is the count of the town's
  occupier set (+0x118, filled by [Package_OccupyTown](ai.md#package-classes); **Verified**: the town panel's "Hostile Occupiers:" line, `FUN_140922e60`).
- Player town level (+0x388, `FUN_14048c200`, **Verified** by code; 0 for a new outpost; recomputed when towns merge, `FUN_140931840`, and in `FUN_1404d1870`):
  over the town's player buildings H = with an interior (Building +0x1f0) that are neither parts nor gateways, T = turrets (BF 12), G = gates (BF 21), R = the
  highest research functionality `tech level` (BF 3). 1 when R > 1, H > 1, G > 0 or T > 3; with G > 0, 2 when R > 3, H > 4 or T > 4, 3 when R > 3, H > 4 and T > 6. Sources: own towns except nests (and, for
  factions with `fundamental type` 1 or `offers bounties`, villages, points of interest, prisons, slave camps); the first source in list
  order with one of its 5 nearest candidates within `range far` is the home (**Observed**).
- World states (`vfunc_2`): every `world state` query equals its val0, and no `special leader` or one with unique state 1 (**Observed**).

**Start** (`FUN_1409c9830`): home missing or disabled → the nearest own town to the target. Required men = int(`num forces` ×
`raidSizeMult`) (**Verified** by disassembly). Gathering stops once men > required (each platoon adds its living members):
1. the `special leader`'s platoon; if not spawned, the squad template naming it `leader`, spawned within 700 of the nearest own town;
2. the faction's free, idle, intact homeless platoons (of a `squads to use` template when the list is set);
3. for each of the 4 own towns nearest the target: such platoons homed there, then new squads drawn from `squads to use` by val0, within
   700 of the town (at the leader once there is one), up to 100 per town; none when the list is empty.

Too few: "Assault to {1} canceled due to faction weakness" (when the target is the player's), nothing else. Success: "{1} on the way to
{2}" (not hostile) or "Warning: {1} moving towards {2}", last start = now, and each `trigger player ally` campaign (needs a `faction
override`) whose faction is the player's ally (allied flag or relation ≥ 50) is requested there with no delay. No relation, war or world
state change. Drafted and new platoons stay kept while the campaign is not ended (`FUN_1407ec570`); whether new ones are `persistent` is
**Unknown**.

**Requests** (`FUN_1409c9410`): victory/loss trigger rolls, retries, ally help and DIALOGUE_LINE `trigger campaign` (to the speaker's
faction; target: talking to the player's side, the player-faction town nearest the speaker (XZ, nests excluded, `FUN_140927bb0`),
and nothing is queued when the player owns none, so a homeless player gets no dialogue campaign, e.g. no crab tournament; another party:
its platoon's ownerships' town (`FUN_1407956a0` +0x30, none included: it wins); a line with no or an unresolvable target: the speaker's current town, speaker +0x1a0 → +0x88 (**Verified** that it is the default, set first and replaced only when the target resolves as a character or squad;
that +0x88 is the current town **Observed**, as `FUN_14028a3f0` reads it); Meitou `TriggerFromDialogue`) queue a campaign with a due time, target, home and
target faction. A weighted list [v0, v1, v2] fires one entry when uniform[0,100) < Σ v2 (v2 = percent), due after uniform[v0, v1) hours
(**Verified** for dialogue by disassembly, **Observed** for the rest). `faction override` hands the request to that faction; a duplicate
(same campaign and target) pending or active is dropped. Due requests check only world states and the leader (no chance, range, cooldown
or population); a failed start retries each run; a gone target is redirected or delayed 5-10 h; delays and starts share one counter (+0x58, **Verified**, `FUN_1409cb310`): a delay drops the request when the counter is then above 10, a start when it is then above 100 (101 start tries when never delayed)
(the 5-10 h delay is **Observed**). Gone (**Verified**, `FUN_1409cb310`): the town does not resolve, or it is a player town that has occupiers while the
campaign is not hostile, or that is not active (slot 70); a redirected request does nothing more that run and starts at the next. Requests can aim at NPC towns. A requested campaign joins the receiving faction's list for good.

**Phases** (`CampaignInstance::vfunc_10`, every war-manager run, loaded or not; times are game hours since the phase began):
- Any phase but ended: the leader platoon loaded with every member down → defeat.
- 0 travel: arrived strictly within 0.8 × town radius (XZ; `Town::getRadius` `FUN_140926eb0`, **Verified**: a placed town's `town radius mult` × its type base ([towns](buildings-production.md#placement), not `size radius`, which only the nest debris scatter `FUN_140938440` reads), a player town's [reassessed radius](buildings-production.md#placement), at least 1250; a nest marker is never inside, `FUN_140926d50`) or 400 of a non-town target; then the `dialog announcement` waits, "{1} has arrived at
  {2}", and phase 1, or straight to ended without victory or defeat when there is no `pt2 AI leader`. A loaded, non-hostile, non-DEFEND
  campaign within 1.8 × radius of a town with hostile occupiers (+0x128) is defeated at once (a trader or ally does not walk into an occupied town).
- 1 at target: victory when the leader is back in its home town (two null towns compare equal); else by `key`: ASSAULT_TOWN wins after
  4 h, earlier 0.2 h after its first fight once the fight or the standing player characters are gone and the target town has every house door open (Town slot 92 `FUN_1408cc930`:
  no building with an interior has a closed first door, state `+0x380` 0, the building's first door being its slot 124) and its walls are not locked up (slot 96 `FUN_140929820`: at least one gate with a first door, all in state other than 1 (open); a gate whose door fails `FUN_140547930` (door `+0x378` word differs from the building's slot 35 value, meaning **Unknown**) is skipped)
  (**Verified**, `FUN_1409c70f0`; Meitou reads each building's first door fixture, a broken one as open, and does not filter gates by `FUN_140547930` or buildings by the interior flag `0x80000` of `BuildingFinder_OnesWithInteriors`, whose exact set is **Unknown**);
  while > 2 player characters hold the town, more than half up, nothing else is checked; DEFEND_TOWN after 12 h when no hostile campaign
  against the player runs; TRADER_VISIT after 1 h unless trading (**Observed**). Then any key: defeat when < 20 % of its platoons are
  intact (the platoon flag +0x100, `FUN_1407ac7b0`: the squad-intact rule of [perception](perception.md) `FUN_1407a5be0`, recomputed only
  while loaded and kept from unload; false once `dead` +0x1f0; both saved, `intact` and `dead`, **Verified** `FUN_1407ed0f0`; a platoon with no save starts true in Meitou).
- 2 ended: removed after 3 h (platoons unmapped).
- Unloaded no fight starts, so an unloaded assault always wins at 4 h: a message and its follow-up, nothing happens to the town (the
  earlier "an empty outpost falls" reading was this).
- Redirect (`FUN_1409d62f0`, **Verified** by decompile; where the update calls it is **Unknown**): when the target town no longer resolves, or its faction is the player's and slot 70 is false (the player has left it),
  a timer (+0xf0, started the first time this is found, restarted after each look) lets the war manager look for a new target every 0.2 h with the campaign's target rule
  (`CampaignData::vfunc_0`, [above](#campaigns)); a different town found replaces the target ("{1} has redirected to {2}", {1} from `FUN_1409d3fe0` read as the campaign's name (**Observed**), {2} the town) and the home stays. None found keeps the old one. Meitou:
  `PopulationCampaigns.Redirect` (no look for a campaign with no target faction, an engine choice).
- Packages: phase 0 `pt1 AI`, 1 `pt2 AI`, 2 `retreat AI`; the leader platoon uses `... leader`, others `... others` (else the leader's).
  Installed (`FUN_140272570`, **Verified**) at the platoon's next AI update: when the phase's package differs from the blackboard's override
  (+0x210) the old one is torn down (dialogue event 30 on the leader) and the new one built by its `signal func` (0 → 33 Contract_Timed),
  timed for 24 h and run ahead of the template's packages until its finished test passes (WarBase: the platoon is out of the campaign).
  Classes by `signal func` (**Verified**): 19 Package_WarBase (pt1 "Military Assemble leader/forces"; activate sets job 5 TRAVEL_TARGET
  for the leader, 6 TRAVEL_TARGET_FAST for the others, and the target and mission target from the campaign), 20 Package_WarAssault (pt2
  assault, slave raid, loot food; speed mode 2, the base's 4), 32 Package_WarAssault_Cannibal; retreat packages use 1, 5 or 0 with
  `clears existing jobs`. Goals (**Observed**, data): pt1 leader 215 TRAVEL_TO_TARGET_PACKAGE, 143, 129 MAKE_ANNOUNCEMENT_FAST; pt1
  others 215, 15; pt2 16 (attack enemies), 15, 216, 270, 276 or 131; cannibal 216, 254, 270.
  - Kidnapping (**Verified**, data query of the install's AI_PACKAGE records for task enum 254 "Find and kidnap (with cages) urgent"; 132 and 246 are in no
    base package): the leader goal list of `Cannibal Raid Retreat` (the `retreat AI leader` of "BASE Basic Invasion", "slave raider silent", its
    "non slave trade" copy and "BASE- standard cannibal raid"), `Cannibal raid assault town` (the pt2 leader of the cannibal raid), `Cannibals at home`, the squad
    goals of `Cannibal patrol map (short range)` and `Assault and kidnap` (a DIALOGUE_LINE's `delivery AI package`). So raiders (bandit invasions, slave
    raiders, cannibals) kidnap when their leader retreats from a raid, cannibals also during their assault, on patrol and at home. The task (254, final state 153 got a
    victim, then 154 put the carried one in a cage) needs a free squad member to lift a downed one and a cage of its faction. The target is the nearest
    down, uncarried, uncaged, non-animal character of the sensed classes (and, unlike rescue, a sleeper in a bed); the class bits are **Unknown**
    ([finder](carry-loot.md#32-goals-and-their-target-finders)). Not every raid kidnaps: the Dust Bandit "Invasion with demands" and the Hive, Ninja and Crab
    raids retreat with other packages (**Verified**, same query). The retreat package's mission town is the platoon's home town, else the nearest town of its
    faction ([ai, prisoner packages](ai.md#prisoner-and-slaver-packages)); what the captors then do: [ai-tasks](ai-tasks.md#prisoner-fate-and-slave-rows). Meitou:
    [kidnapping](../simulation.md#kidnapping), [captives](../simulation.md#captives).
  - Enemies: the base package, in a hostile campaign's phase 1, tags each seen target-faction character with squad memory 4 (TEMP_ENEMY,
    as DA 21); WarAssault tags every seen character except animals (Character vfunc 0x248), those of another town (the town hands differ; none passes) and those of factions with
    `not real` (+0x1d0) set or `fundamental type` (+0x34) 8, Bandit (**Verified**, `FUN_14028a3f0`; [sensing hooks](ai.md#package-classes)). Meitou `AggroSystem.WarEnemy`.
  - Cannibal (`FUN_14028fdf0`, the package's update, **Verified**): per platoon running the package, not per campaign. It counts its own squad's members with `+0x348` set (carrying) and
    keeps its own start stamp (package `+0xb0`): set at the first carrier, zeroed again when nobody carries or the platoon has no squad. With a carrier, the campaign record of that platoon
    goes to phase 2 (time stamped, `+0xc0` = 2) at 3 carriers or when the stamp's age in clock hours x 60 is above 15 (strictly; hours **Observed**). Meitou `PopulationCampaigns.CannibalsLoaded`.
  - Dialogue (`FUN_140680560`, **Verified**): DA 37 sets phase 1 (2 without a pt2 leader package), DA 38 phase 2, DA 39 phase 3, which no
    phase handles (only a leader wipe ends it; the data never uses DA 39).
- Moving: unloaded, the leader travels to the target (TRAVEL_TARGET), the others to the leader (TRAVEL_TARGET_FAST), speeds as the
  unloaded step above. Loaded (`GatherAndMove`, **Verified**): each run the leader's trail gains its position when over 150 from the newest
  point (seeded with slots + 1 points within ±100; slots 5, or N + 1 past N platoons) and drops its oldest while longer than N; follower i's
  point is i × slots × 150 / N back along it. Whether followers steer to it or to the leader platoon's position is **Unknown** (the task
  prefers the handle's). Task 215 (`Task_TravelToMissionTarget_ArmyFormation`): followers 100 or more from their point JOG, closer keep a
  straight move at it at the leader's current speed (× 0.9 under 70) and never wander (**Verified**, `FUN_140349e70`; Meitou: the squad leader takes the
  campaign leader's speed mode, capped by `CharacterCold.PaceCap` = the campaign leader's velocity now, × 0.9 under 70, cleared at 100 or more,
  on leaving the campaign and while fighting; reading the speed value as the velocity is an engine reading); the leader uses `travel speed loaded` while every intact platoon (loaded, or unloaded on job 5/6) is within
  6000, else stops within 8294.4 of its target (plain distance), else walks. Speed modes: WALK default, JOG 55, RUN and GROUPED 999 (no
  cap), NO_SPEED_CHANGE unchanged.
- Announcement (task 129 in the pt1 leader package, **Verified**): "{1} has arrived at {2}" once; task 129 needs the leader not down (!101)
  and at the package target (131 `atPackageTarget`, [TaskData](ai-tasks.md); its radius is **Unknown**, Meitou uses the 0.8 × radius arrival); with the leader within 2500 of the camera
  and a target-faction member within 2500 of it, `dialog announcement` fires as event 2 ANNOUNCEMENT with no target; phase 0 then waits on
  the leader's event-2 state (which state: **Unknown**). Talking to a campaign member (event 1) is refused without a pending `interrupt`.
  "Invasion with demands" (data): the announcement sets the negotiation as `interrupt`; paying 2000 (DA 8 + DA 38) retreats and requests it
  again in 42-56 h; refusing is DA 37 (attack); stabbing the speaker is DA 21 + DA 38 and the "EXTRA ANGRY" campaign in 20-50 h.

**End effects** (the only callees): victory: "{1} against {2} has ended" (hostile) and one `victory trigger` roll. Defeat: "{1} was defeated
on their way to {2}" (not hostile), "{1} against {2} has been defeated" (its leader fought a player squad) or "{1} to {2} has failed"; a
`loss trigger` roll when it reached the town or fought the player, else a hostile one is requested again in uniform[40, 96) h. No relation,
world state, ownership, building or town state changes; looting or occupation are only what the loaded `pt2` packages do. In
every message {1} is the campaign's name and {2} the target town's (the cancel message has only {1}; **Observed**).

### 9. Settings and constants that matter

| Name | Value | Effect |
| --- | --- | --- |
| `Squad size multiplier` (`settings.cfg`) | 1 | M in 5.1 |
| `Global population multiplier` | 1 | P in 6.2 |
| `npc range` | settings.cfg value | relevance range, see [game-loop.md](game-loop.md) |
| `raidFrequencyMult` / `raidSizeMult` (`settings.cfg`; options "Town raid events: frequency" 0.25-3, "size" 0.25-4) | 1 | campaign roll chance; required men = int(`num forces` × size) ([campaigns](#campaigns)) |
| CONSTANTS `max squads` / `max squad size` / `max faction size` | 10 / 20 / 30 | stored by the constants loader (`FUN_14086b2b0`; the squad screen reads them as `DAT_142133f4c` / `DAT_142133f48`); enforced by the squads screen (ADD SQUAD, portrait drops, [ui-screens.md](ui-screens.md#squads-tab)) and recruiting ([dialogue.md](dialogue.md)) (**Observed**) |
| area tick | 2 s scaled | 6.2 |
| min distance of a homeless spawn from the player | 1382.4 | 6.2 |

## Unknowns

- What sets the character status threat float `+0xac` and the killer handle `+0x2c0` (3.5); what the
  allied byte (+0) and the `trust` floats feed (dialogue conditions, "trustworthy" behaviour).
- What `building designation` changes beyond the bar (it does not enter the chooser); the town node map bar squads use.
- Nest respawn delay after "wiped out", the default of `DAT_142133588` (the Number of nests multiplier option; **Unknown**, the exe image holds zeros there), the rule that makes `TownPopulationManager::vfunc_4`
  declare a town wiped out (**Observed** gates: roaming pool cap <= 3 and <= 25 roaming members).
- Campaigns: TownBase slots 92 / 96, the disabled-town flag, what besides the two calls recomputes the town level,
  the "fought a player squad" test, the announcement wait's event-2 state, task 215's follower steering, the A* road cost, `Platoon::vfunc_25` (sends phase 0 to 2), whether spawned campaign squads are `persistent`, DIPLOMATIC_ASSAULTS.
- `faction gen` at game start.
- Unique characters: the `Character +0x279 & 7` gate and the 0.1 roll against a town list at platoon unload, platoon +0xac, the unit of
  `replacement time` (constant 2618.18 from `FUN_14066cd50`), UNIQUE_SQUAD_TEMPLATE `persistent` use and `missions` (DIPLOMATIC_ASSAULTS),
  the polarity of the import handle test; other readers of `unique` (`FUN_14067b070` dialogue conditions, `FUN_1407609e0` / `FUN_140761550`) not opened.
- Town overrides: what sets the ZoneManager +8 byte; the buildinglist entry field tested in `FUN_1409fe340` ([town overrides](#town-overrides)).
- The direction of the town waypoint's 200 offset and whether `Town::setup` runs for the Null town type ([patrol task](#off-screen-world)).
- Patrol routes: `Task_PatrolTown` and `GoOutOnPatrol*` were not decoded here (`Task_Patrol` is above; class mapping: [ai-tasks.md](ai-tasks.md#tasktype-to-task-class); packages: [ai.md](ai.md#signal-functions-and-the-package-classes)); no fixed route data
  was found in FCS records (squad templates only name `AI packages`; `patrol approaches towns` is a bool).

## Expression reference

- Changing relations: `value += delta`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- 3.1 Storage (**Observed**)
- 3.2 Initial values (**Verified**)
- 3.3 Thresholds (**Verified**: -30 and 50 appear in the predicate functions, in the threshold-message function `FUN_1406b28f0` and in the declare-war gate `FUN_1406b3000`; re-read independently)
- 3.4 Changing relations (**Observed**; the event table cross-checked with the dialogue action numbers)
- 3.5 Relation event sources (**Observed**: decompile of every slot-5 call site found; `faction importance` **Verified** by probe)
- 5.1 Counts and layout (**Observed**; two functions share the count rules, see the sentinel note)
- 5.2 Procedural squads from a FACTION_TEMPLATE (**Observed**)
- Campaigns: `FUN_1409cb310` (roll), `CampaignData::vfunc_0/1/2` (`1409c5e80`, `1409c5740`, `1409c4ed0`), loaders `read_chance_per_day_n9`
  `1409c8bd0` / `read_is_hostile_n11` `1409c7e40` / `read_campaigns_n3` `1409cabd0`, start `FUN_1409c9830` with `uses_leader` `1409c5040` and
  `FUN_1409c4150`, requests `FUN_1409c9410` / `FUN_1409d27a0`, instance update `CampaignInstance::vfunc_10` `1409c70f0`, end `vfunc_2` / `vfunc_3`,
  redirect `FUN_1409d62f0`, town gates `FUN_140929eb0` (slot 70), `FUN_14048c200` (level), `FUN_140922e60` (occupiers), Town constructor `FUN_140934ae0`; enum names
  from the editor (`WarCampaignEnum`, `MoveSpeed`); data census with a throwaway tool over the merged base files
- Unique characters: manager `FUN_140354560` / `FUN_140349950`, gate `FUN_1405836e0`, transitions `FUN_1407a6200`, `FUN_140330600`, `FUN_140680560`,
  `FUN_1405cf470`, `FUN_1409a8120`, `FUN_1404ff760`, `FUN_1409a8970`, queries `FUN_1409a7e00`, `FUN_1409c4ed0`, `FUN_1409c5040`; special squads
  `FUN_1402ddc90`, `FUN_1402dd680`, `FUN_1402de9b0`, `FUN_14057c6f0`; save `FUN_1409a8660` / `FUN_1409aa270`, `FUN_1402deea0` / `FUN_1402df0c0`;
  `fcs.def` texts; throwaway survey of the merged base files and census of 9 saves (302 usedUniques entries, 3,502 PLATOON records)
