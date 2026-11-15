# Factions, squads and towns

Original FACTION/Platoon/population/town research: binary functions, FCS and merged database/save
checks. Tables/expressions retain fields and event values. Runtime: [simulation](../simulation.md);
[AI](ai.md), [economy](economy.md), [save format](../formats/save.md).

## Relations and world states

**Verified** initial A→B: explicit A.relations v0, otherwise min(A.default,B.default); self100.
Hostile<=-30, ally>=50; directional flags/coexistence and event changes are distinct. Storage,
trust/known/player-faction branches and dialogue/reaction events remain scoped Observed where
not independently checked. Start-off relation overrides are not fully decoded.

## Squad factory

Generic weighted refs: v0=0 means100; exclude nonpositive weights and false world states,
first cumulative weight exceeding uniform roll wins; single candidate no roll. SpawnInfoList
instead drops v0<=0 and budgets expected squad sizes using its separate sentinel/range rules.
Do not reuse one zero-weight convention for both.

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
- Death (`FUN_1407a6200`): state 0; player set (never cleared here) when the killer's faction is the player's. The dead unique's faction's
  "prosperity" (`FactionUniqueSquadManager` +8, see Special squads below) drops by 100, floor 0; the record's `unique` bool gates it (**Verified**, `FUN_1407a6200`). Meitou: `UniqueCharacters.Died` queues the faction of each unique that dies (once, on the change to dead), `PopulationSystem.StepProsperity` applies it at the next look.
- Imprisoned in a cage or restraint (`FUN_140330600`): state ≠ 0 → 2, player set when the cage's owner is the player's. `DA_ENSLAVE_TARGETS_CARRIED_PERSON` / `DA_ARREST_TARGETS_CARRIED_PERSON` (0x28 / 0x1e), which
  take a carried unique off its carrier, do the same, player set when the carrier is the player's (`FUN_140680560`).
- New game and every load (`FUN_1409a8970`): map cleared, then the unique leader/squad/squad2 of each SQUAD_TEMPLATE's first `prisoners`
  template → state 2 unclaimed (base game: Cat of `Dust King Prisoner`, `Tengu's vault prisoners`, `agnu`). Import marks every imported unique
  claimed (`FUN_140378a30`).

**Queries**:
- WORLD_EVENT_STATE (`FUN_1409a7e00`): `NPC is` needs state = v0, `NPC is NOT` state ≠ v0; with `player involvement` the player flag must
  also be set (`NPC is` with v0 ≠ 1, `NPC is NOT` with v0 = 1) (**Verified** by code).

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
- Candidates: the current TOWN's `override town` entries. One qualifies when each distinct `world state` entry (WORLD_EVENT_STATE) equals its v0; its weight is
  how many there are, so an entry with none never qualifies. The replacement in place is skipped. The heaviest wins; equals keep the first in list order.
- `updatedTownOverrides`: while a town is in it, the check answers "repopulate" at once (no new choice), so each load trims the zone again. The pass that places
  residents after zones load (`FUN_140a0e760`, ZoneManager state 2) places them for the collected towns not of the player's faction and erases them (`FUN_140a22ef0`).
  Nothing else touches it, and it is not saved.

Loot/building inventories, artifacts, shop stock, prisoners/slaves and placed debris derive
from different reference lists. Preserve slot/count/quality/owner fields and forbidden-item
rules rather than treating every list as uniform random loot. Story NPCs: [unique characters](#unique-characters);
campaigns: [campaigns](#campaigns).

### 1. Object model

| Game class (RTTI) | Role | Notes |
| --- | --- | --- |
| `FactionRelations` / `PlayerFactionRelations` | the relation table of one faction towards all others | section 3 |
| `Platoon` -> `ActivePlatoon` (characters exist) / `UnloadedPlatoon` (abstract) / `UniquePlatoon` | a squad; `UniquePlatoon` only from an UNIQUE_SQUAD_TEMPLATE ([special squads](#unique-characters)) | per-frame stepping is in [game-loop.md](game-loop.md#update-threads-and-budget); creation is section 5 |
| `TownBase` -> `Town`, `Nest` | a TOWN record placed in the world; nests are TOWN records of `type` 0 spawned by the game | section 7; `Town`/`Nest` share almost all virtuals, the nest overrides slot 103 (wiped out) |
| `BasePopulationManager` -> `TownPopulationManager`, `NestPopulationManager` | per town: two pools (residents, roaming) with a population budget and a `SpawnInfoList` | section 6.4 |
| `SpawnInfoList` | the weighted list of squad templates a pool or biome group may spawn | section 6.1 |
| `AreaSector`, `AreaBiomeGroup` | one cell of a 64 x 64 grid of 4608-unit cells, and the BIOME_GROUP data it belongs to | section 6.2 |
| `BuildingItemGroup` | an ITEM_PLACEMENT_GROUP placed in a building: one item slot with a respawn timer | [loose items](#loose-items-in-buildings) |

**Keep test** (`FUN_1407ec570`, **Verified**): kept when `persistent` (+0x115); else when spawn mode (+0x118) is not 3, the squad (if any) has members, and the faction's war manager holds a record for the platoon (`FUN_140283500`, a find in its forces map) whose state (+0xc0) is not 2. A campaign force is thus kept until its campaign enters phase 2 (victory, retreat, cannibal carriers, `FUN_14028fdf0`); then it is destroyed on leaving the loaded zones like any unkept platoon. Spawn mode 3 is set by the activation (`FUN_1404ffad0`) when the platoon's `basetown` TOWN (unset counts as the town's own) does not fit its home town now (own TOWN only while the town has no replacement, else the replacement): activation is refused, a unique template leader releases the template's uniques, any other leaves the persistent flag cleared (`FUN_1407ebdd0`); the platoon then ends by the 60 s orphan timer. Meitou `PopulationSystem.Kept` / `CampaignKept` / `RefuseReplacedHome`; the Meitou keep switches still keep the rest (not mode 3); saves write `basetown` as the town's current TOWN. Platoons of older Meitou saves (CAMERA has the seed key, record lacks the `meitou basetown` marker) wrote the placement's own id, so they are taken to fit and keep their residents; vanilla saves get the original's test.

### 3.1 Storage (**Observed**)

| Offset | Type | Meaning |
| ---: | --- | --- |
| +0 | byte | "allied" flag: forces `isAlly` true (set for the faction itself; other setters not found) |
| +3 | byte | coexistence flag (set from the FACTION `coexistence` list) |
| +4 | float | **relation**, -100 .. +100 |
| +8 | float | accumulated positive trust |
| +0xc | float | accumulated negative trust |

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
| caging | `Task_PutInSomething` `FUN_140358670`, after the bounty claim | kind = task `+0x7c`: 12 for the cage task, 9 (no row) for the bed (factory `FUN_14032ebd0`) against the putter's faction |
| first aid | `FUN_140649a00`, `FUN_14064d800` | kind 8 to the medic's faction when the patient's medical `+0x161` (unconscious) or `+0x160` (a leg-collapse part down, `FUN_140649320`; **Verified**) is set; in `FUN_140649a00` only on an update whose loop filled every wound. Meitou: `Unconscious` or `Crawling`, not dead |
| ally witness | `FUN_140854d10` | a witness that regards the suspect as friendly (vfunc `0x3f0`) and sees it wrong its own faction: slot 4, -20 (skipped when the witness platoon's `+0x210` object has `+0xa8`); for every crime code but 2, 10, 11, 14, 16; repeats on each witness pass |
| missions | `MissionClass` vfuncs `FUN_14039f580` / `FUN_14039f660` | trust (slot 6) and a direct add (slot 4) of the mission giver's faction about the player. **Dead in 1.0.68** (**Verified**): the only maker `FUN_14039f4b0` is reached only by an uncalled thunk, so the mission list is always empty and these never apply |
| dialogue | `FUN_140680560` | DA 4 slot 4, DA 5 slot 9, DA 13 slot 8, DA 14 slot 7, always the **speaker's** faction about the target's ([dialogue](dialogue.md#effects)) |

- No other caller of slot 7 or 8 was found: wars start and end only by dialogue (`FactionWarMgr` campaigns do not call them).
- Kind 8 is not a drift: nothing moves relations back over time.
- `FUN_140673a20(self, other, tag)` (not a witness function) sets a long-term tag (`CharacterPerceptionTags_LongTerm`, read by DC 23) in
  `self`'s squad memory (`platoon+0x98`, `FUN_140673930`, time-stamped): 1 MY_INTRUDER (trespass), 5 MY_CAPTOR (cage), 7 / 8 (the victim's
  squad tags the striker DEFEATED_MY_SQUAD_ONCE and the striker's tags the victim SQUAD_LOST_TO_ME_ONCE, on a downing blow when
  `FUN_140793160` finds the victim's squad all down), 9 KILLED_MY_FRIEND (death). Meitou keeps the memory ([dialogue](dialogue.md#squad-memory)) and writes 5 for cagings and arrests, 7, 8 and 9 for blows (`FactionDeeds.Remember`); trespass (1) is not written.

Meitou: `FactionDeeds` (blows, cagings, first aid), `CrimeSystem` (the ally witness), `PlayerRelations.DeclareWar` / `EndWar` / `Add`
(the messages); [simulation](../simulation.md#hostility-and-relations).

### 3.6 Factions met

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

### 4. Squad templates

| Field | Meaning in the exe |
| --- | --- |
| `squad` / `squad2` (CHARACTER, v0, v1) | the two member groups, see 5.1 for the counts (`squad2` is the "second half": member role 1) |
| `choosefrom list` + `num random chars` / `num random chars max` | N random characters from the list, weighted by v0 (chance, 0 counts as 100) |
| `animals` / `animals2` (ANIMAL_CHARACTER, v0, v1, v2 = age 0..100) | animal members, 5.1 |
| `slaves`, `prisoners` (SQUAD_TEMPLATE) | whole squads created recursively, 5.2 |
| `housemates` (SQUAD_TEMPLATE, min, max) | further squads spawned with this one (`FUN_14057f820`) |
| `faction` | owner faction; if absent the spawn code asks the owning town's faction, else with no town takes the first faction in load order whose `squads` or `squad default` lists the template (`FUN_1402e7d10`); when that gives nothing, or a named faction does not resolve, the faction `Wildlife` (`3943-gamedata.base`) (**Verified**, `SpawnInfoList::vfunc_0` = `FUN_1408fe6b0`; Meitou `PopulationData.SpawnFactionOf`) |
| `dont multiply` | exempts the template from the *Squad size multiplier* setting |
| `AI packages`, `blood smell mult`, `patrol approaches towns`, `force speed` (speed modes), `personality`, `dialog *`, `nest`, ... | copied onto the platoon / its characters (`FUN_14057f4c0`, `read_AI_packages_n4`; the package system is in [ai.md](ai.md#other-records-that-touch-the-ai)) |
| `building`, `building dislike`, `building designation`, `layout interior/exterior`, `public day/night`, `public beds`, `bed usage cost`, `sell home`, `building ruined`, `initial door state` | how the squad's home building is chosen and set up (resident squads) |
| `vendors`, `is trader`, `vendor money`, `vendors fill total amount`, `vendors refresh time`, `buy mult`, `buys stolen`, `buys illegal`, `special items`, `special map items`, `*artifacts base value`, `regenerates` | shop and contents (section 8 for loot; the trading economy is in [economy.md](economy.md#shops-and-transactions)) |

### Loose items in buildings

**Verified** by code (`BuildingItemGroup`: constructor `FUN_1405534f0`, timer `vfunc_28` `FUN_140549e60`, spawn `FUN_140547ed0`, pick `FUN_1409540d0`, grade
`FUN_14063cea0` / `FUN_140952320`, save `vfunc_17` `FUN_1405567b0`) and a census of the merged install data and `interiors.level`:

- A placed group is one item slot at its object's position and rotation, with a handle to its item and a respawn deadline in game hours (0 at start). Each update with no
  item: once the clock reaches the deadline (and the owner test below) it makes one. With an item whose handle is still a WEAPON, ARMOUR,
  ITEM, CONTAINER, NEST_ITEM, MAP_ITEM, CROSSBOW or LIMB_REPLACEMENT that is not taken: nothing. Otherwise the handle clears and the deadline becomes now + an int in
  [min, max] hours. The slot's state is the spawned object's handle type (`FUN_140547ed0`), so a BLUEPRINT (0x5a) is let go on the next update and the slot fills again at
  the deadline though the blueprint still lies there (Meitou `ItemSlots.Kept`: it lies as a loose item from the start).
- **Owner test** (`FUN_140549e60`, **Verified**): the slot spawns unless its building's owner hand (Building `+0xd0`, type at `+0xd8`) resolves to a loaded character that belongs to no squad
  (`+0x1d8` null); an unresolved owner (not loaded, none) or one with a squad spawns. The hand is written by the owner setter `FUN_140553210(building, character)` (the character's own hand,
  then its squad's leader hand when it has a squad), called when a squad is given its home building (`FUN_1407ec9e0`), when a building is created for a character (`FUN_140583a10`) and by the
  resident spawn (`FUN_1407b65b0`: the character's own owner hand, else the character); nothing else sets it. So in play the owner is a squad leader or a creating character, always in a squad, and
  the test never withholds a slot: Meitou's `ItemSlots.Step` is the same without the owner (a squadless owner needs a squad removed while its building stays, which no found path does).
- Saved per group (in its generated interior): `base data sid`, `floornum`, `isInsideBuilding` handle, `is inside building`, `respawn time`, then its item.

Meitou: [ground items](../simulation.md#items-and-eating).

### Town residents

**Placement order** (**Verified**, `FUN_14057f820`:86-103, `FUN_140583a10`:413-420, `FUN_1409fcd20`:197-205): the platoon point of a resident squad is a random free node of its building tried in the order type 0 shopkeeper, 2 guard post, 1 general, else the building origin (Meitou `PopulationHomes.SpotIn`); each member is placed apart at a random general node, else a shopkeeper node, else the platoon point (Meitou `MemberSpot`, one draw per member). A bar squad asks the town's node map for a random node and, when none comes back, uses the town's own position with no offset (Meitou: a node of one of its bars, else the town's position).

- **Table** (`FUN_140935d10`, `FUN_140936e60`): the faction's `residents` then the town's; with `residents override` the
  town's only. A template already listed is skipped (first wins). Per entry: v0 = count, v1 = priority (100 counts as 0,
  outside 0..5 an error box). `default resident`: the town's, else (no override) the faction's. `bar squads`: the
  faction's (TownType 2 only, no override) then the town's; with override the town's only. **Verified**: a town whose
  faction object is null (no `faction` reference, or one that matches no FACTION: `FUN_1409fe7a0` sets it only when it
  resolves, the base constructor leaves it null) builds nothing: `FUN_140936e60` returns before the table, the default
  resident and the built flag (`residents override` is read only after that). The bar squads come from the same table
  (`FUN_1409fcd20` calls it first and loops over its entry count), so they are empty too. Base data: 4 of the 22 faction-less TOWN
  records have residents (housecamp raider variants, Skinhouse overrides); they get none. Meitou: the helpers in `PopulationSystem.Build.cs`.
- **When**: at zone populate (`FUN_140a0e760`) per non-player town, the zone's buildings sorted by handle serial, a signed int, lowest first
  (**Verified**, comparator `0x140a089d0`: hand +0x18, the field `FUN_1400d1c20` fills from `rand()`).
- **Candidates**: buildings of the town ([membership](../formats/zones.md#town-membership)) with an `interior` reference, not player-owned, holding no residents template
  or the default one (**Verified**, `FUN_14092e870`: so a home of an entry whose template is also the default resident is taken again by a
  later entry, and the first entry's squad is lost: Holy Farm's `farm village residential`, priority 1, loses its L-House to the priority 0
  `holy materials storage`); walls (`links together`, a vfunc override) never, gates (`is wall gate`) only for entries naming
  `building`s. Level data agrees: 1946 walls, none with a resident. Player-owned is ownership now: a house the player bought
  houses nobody, and its squads moved out when it was bought ([purchase](ui-screens.md#selected-building)).
- **Driver** (`FUN_1409370d0`): re-owns the buildings to the town's faction; priority 5 down to 0, each entry with count
  left gets one building per pass, passes repeat while one placed and has more. An entry with no building left makes
  no more squads (Stack: civilians v0 9, 2 made). A squad counts only when the housing succeeds (**Verified**): a chosen building that refuses
  (a gate not offering the template's `layout interior`, which the chooser does not test for gates) stays free, is chosen again, and the entry
  houses nobody more. Then every free building whose interiors take the default resident's
  `layout interior` gets it (`FUN_14092e4b0`).
  **Verified** (base data under these rules; the original 1.0.65 in a new game, KenshiHarness): the Grid's Workshop Complex (rebirth) has three
  Metal Warehouses, `workshop empty` v0 2 and no default resident, so the highest-serial one, `15.47:4-rebirth-INGAME` at the map marker, keeps
  its level state's empty interior and stands bare while `15.47:2` is furnished (#730).
- **Chooser** (`FUN_14092e870`, **Verified**): a template with `layout interior` needs a building offering it (own layouts or
  `shares interiors with`; gates exempt); without one, an entry naming buildings takes only those. Weight 1, x 0.4 when
  `layout exterior` is not offered, x 10 named in `building`, x 0.05 in `building dislike`; highest wins, first on a tie.
  Offered, names compared exactly (case matters: Barracks UC's exterior `banners` matches no `Banners` layout): an interior by an interior layout
  record of that name whose `associated building` is the building or one it shares interiors with (`FUN_14055ad40`); an exterior by an
  `interiors.level` record with the string id `EXT-<building id>-<name>` for one of those buildings (`FUN_14055b330`, which reads neither the
  record's name nor `is exterior`). The loader finds a layout by string id too (`Layout::loadLayout` `FUN_1405512a0`, the id from vfunc 14:
  exterior `FUN_14054c080` `EXT-<building id>-<name>`, interior `FUN_14054bda0` `<building id>-<name>`, each falling back to the buildings in
  `shares interiors with`, vfunc 15), so the interior test (by name) and the loader (by id) can disagree; base data: they agree for all 907
  named level-data interiors after housing, while 34 of 342 named exteriors find no record and show nothing: Storm House `Mechanical Sign` x13
  (the id reads `Mechanical SIgn`), Outpost s-III `Banners` x12 (`banners`), Old Storm House `Slave Sign` x7 (the record's id names the Storm
  House), Y-House `banners`, Bughouse `Barracks` (`EXT-3793-TwoStorey.mod-Barracks` is the Old Storm House's by id). Meitou:
  `BuildingLayouts.Names` for housing, `BuildingLayouts.Find` for loading (exteriors by id; interiors by name and `associated building`).
- <a id="whose-template-a-home-carries"></a>**Whose template a home carries**: a building keeps one residents handle (+0xd0) and one squad
  template (+0xf0), and `FUN_140553210(building, platoon)` overwrites both with the platoon's (the building designation only while the building
  has none). A home whose template has `housemates` is the home of several platoons, so it carries the template of whichever called last.
  - **Read by** (**Verified**): the squads made for a building (`FUN_14057f820`), the chooser's free test, and
    [For sale](ui-screens.md#selected-building) (`FUN_14029c500`: the template's `sell home`).
  - Meitou: `FixtureRegistry.ResidentsTemplate`: the boot's housing (`ResidentRuins.Apply` writes it into the level state as `residents template`
    and `squad template`, and empties both for a town building nobody is housed in, since the original drops the level state's), a loaded save's
    state, a town override's housing, then the template of each squad made from its template with that home (`StampResidents`: a remade squad
    `PopulationSystem.Regenerate`, a save's never-made one `MakeUnmade`). A save writes it under both keys and names the platoon of that template as
    `residents` (`WriteResidents`, `SaveCapture.NameResidents`). A new game's squads are all made at once, housemates first, so every home starts
    with its own template.
- **Squads** (`FUN_14057f820`): `housemates` (v0, or Rand(v0, v1) when v1 > 0) first, then the squad. Platoon point: a
  random free node of NodeType 0 (SHOPKEEPER), else 2 (GUARD_POST), else 1 (GENERAL), else the building. Characters
  (`FUN_140583a10`): a GENERAL node, else SHOPKEEPER, at node + (0, 10, 0), else the platoon point. The squad's home is the building.
- **New game vs level data**: level-data building states carry `residents template` / `squad template`, but the state reader `FUN_140552b70`
  looks the `residents template` up only when the string is empty (**Verified**, disassembly `0x14055312d`: `cmp [node+0x38], 0; jne` past the
  lookup), so a level building starts with none and every town is housed afresh from its table. The outcome is the same in every new game: a
  level building's handle serial is its level state's `handleS` (**Verified**: all 412 / 473 level buildings of the saves METARU / redev carry
  it), and the rules here over the buildings in that order give the saved `residents template` of every home of the unmodded saves: METARU 380
  of 380, redev 420 of 420, autosave1 360 of 360, autosave2 38 of 38 (a home saved under its housemate's template counted as its own, see
  Layouts); the level data's own names match 232 / 248 / 221 / 38. Destroyed buildings stay candidates (the Hub's ruins house squatters).
- **Meitou**: `ResidentHomes.Assign` takes the buildings in that serial order (ties and stateless buildings by key), so the boot can lay out the
  [ruins the homes make](buildings-production.md#ruined-by-residents) and name the homes' layouts, display `name` and `squad template` as above (`ResidentRuins.Apply`, before anything reads the
  levels: renderer, lamps, effects, navmesh and `NavmeshTool`, homes). Later changes lay a
  building out afresh (`TownBuildings.Relay`, `FixtureRegistry.Layouts.cs`): its beds, nodes, fixtures and item slots come from the new names, which its
  placement carries to the renderer, navmesh overlay, lamps, effects and indoor sight, and a save writes into its state. A town override's own zone
  (not the player's buildings) takes the replacement's housing: a home its template's layouts, `name` and `squad template`, any other building its level files' (as the original reads
  the zone back from them); its other zones keep theirs (**Observed** headless: Stoat's half-destroyed override relays 15 of its zone's 219
  buildings, whose fixtures and item slots then match a fresh registration). A loaded save's building states name the layouts the original furnished them with, so they win
  ([zone files](../formats/save.md#zone-files)), as do their `name` and `squad template` for the selected-building panel. Not followed: sound emitters (laid out once from the boot's names); bar squads
  stand in the town's bars and take the nearest bar as home (their templates, e.g. `Bar thugs`, have designation 0, **Verified** in the data); a loaded town keeps its saved squads, homes and bar squads and rolls no new bar squads.
- **Squad factory sections** (`FUN_140583a10` = `RootObjectFactory::createRandomSquad`, **Verified**, whole function read 2026-11; the platoon itself is made by `FUN_14057f4c0` / `FUN_1407f4420`):
  1. No lists at all (`leader`, `squad`, `squad2`, `animals`, `animals2`, `choosefrom list` all empty): no platoon content; with a home building and `vendors` (or the building designation's lists) or `special items` / `special map items`
     the building filler `FUN_14095b210` stocks the home once (specials on) and the home's template is stored, then it returns null. Meitou `TradeSystem.StockEmptyHome` (26 base templates: lost libraries, ghost bar, Flotsam safehouse).
  2. `roaming military` forces mode 2; `dont multiply` makes the size multiplier 1; the platoon gets its home and, with a home building, the member origin moves to a GENERAL else SHOPKEEPER node + (0, 10, 0).
  3. `leader` (first entry only; a missing record is logged, no member and the index stays 0), then `num random chars` (between min and max when min < max) picks through `FUN_14057fd40`; the first member made with no leader yet is role 2, later ones 0.
  5. `slaves`: each a platoon of its own (the slave template's `faction`, else the owner's), its squad made recursively, every member role 4, owned by the squad; `prisoners`: only with a home, into free cages.

### Off-screen world

**Verified** by code (kenshi-named dump) unless marked. Who is kept off screen: [persistent platoons](#1-object-model).

- **No world pass**: work is spread over round robins. Each faction steps one platoon of its list per frame (`FUN_1406b9970`, unloaded
  ones only; a per-faction cursor at +0x23c, wrapped past the end, advances one per frame; Meitou steps all of them per look, [off-screen tier](../simulation.md#off-screen-tier)); one faction per frame runs its war manager (`FUN_1406b97c0` → `FUN_1409cb310`; the caller `FUN_1402e74f0` adds the frame time to every faction, then runs the one at the global cursor `DAT_14212e314` and wraps it, **Verified**; Meitou: `PopulationSystem.StepWarManager`, one per tick); one town and one nest per frame get growth /
  spawn / wipe (`TownList::vfunc_4`); the area tick every 2 s (new nests, homeless squads), sector housekeeping every 10 s. Each unloaded
  step measures its own elapsed game time, and growth and campaign timers read the game clock, so no rate depends on these periods.
  - TRAVEL_TARGET / _FAST: to the mission target: the blackboard's override package's, else the campaign's target, else the current
    package's saved `mission target`, else a fallback (`FUN_140269cd0`); no move when it is the town the squad is in (**Observed**); a campaign's `travel speed unloaded` WALK / JOG / RUN gives 25 / 55 / 90;
    otherwise (GROUPED, NO_SPEED_CHANGE, no campaign) FAST 60 (90 beyond 25000), TRAVEL_TARGET 25. The mover caps speed at 70 (**Observed**: no other writer of the cap). `FUN_140269cd0` (**Verified**): `+0x210` the override target; else the war manager's campaign target when it is a town (hand type 0xd) or nest (0x55); else the blackboard's saved mission target `+0x48`; else (caller flag) the platoon's Ownerships home town `+0x30`; Meitou `PopulationSystem.MissionGoal` for every TRAVEL job outside a campaign: `Platoon.Target` (set to the package's mission town where the squad unloads, saved as `meitou mission town`), a shipping squad with slaves and none chosen the nearest camp, else the origin; it stays once inside that town's radius.
- **Meetings**: nothing looks at other platoons; there is no off-screen combat. Off-screen outcomes are campaign phases and despawning.
- **Despawn cost** (**Verified**, `FUN_1406bab60`, `FUN_140796b30`): a non-kept platoon that leaves while not intact (the platoon flag, below)
  is taken out of its pool (platoon +0xa8: 1 residents, 2 roaming) and, while that pool is above 0, lowers it by its living members (no floor);
  a `regenerates` platoon (+0xd9) is charged nothing and only marked to be remade. The platoon's faction's prosperity also drops by 10, floor 0, when its +0xa4 is above 0, pool or not (`FUN_1406bab60`, **Verified**; +0xa4 is the count `FUN_14079acf0` compares with 4, read as the member count: **Observed**). Meitou: roamers and a save's wild platoons that despawn Faithful (`PayRoamerDespawn`, both costs) and nests
  (`PayNestDespawn`). Not intact is the squad-intact rule for all of them (`SquadIntact`); the pool is only charged while above 0, no floor; the prosperity drop applies to every wild site's squad that leaves not intact (nest or sector).
- **Town wipe** (**Verified**, `TownPopulationManager::vfunc_4` `FUN_1408f7a80`): once started, roaming `pop` <= 3, <= 25 roaming living and no intact resident platoon
  (more than 200 unresolvable handles in the set, or any at all, also keep it) sets `ded`, saved, never cleared. `setDead` is empty for towns (only nests remove themselves), so
  nothing is shown; a dead town skips the remake pass at activation and its `regenerates` squads die. The loop over the resident set (**Verified**): a platoon that resolves and is intact
  (`isIntact` `FUN_1407ac7b0`: false once `dead`, else the flag +0x100, which a loaded platoon recomputes with the squad-intact rule `FUN_1407a5be0`: a member held in a cage or carried by a
  non-ally keeps it intact, and a platoon of the player's faction is always intact, `FUN_14079acf0`) keeps the town at once; one that resolves and is not intact is skipped; a handle that does not resolve to a
  platoon counts, and with any counted (or over 200) the town is kept. The flag is saved as `intact`. Meitou (`CheckTownWipes`, each roam check): resident = any town squad or pinned platoon with a home that is
  intact (a loaded squad's AI flag, an unloaded platoon's flag; for the unloaded Meitou also wants someone alive, as its off-screen fights kill members the flag never saw), except a `roaming military` template's squads, which are roaming-pool members (counted with the roaming living); a town still loading is not judged.
  The check runs only while the zone at the town's position is loaded (zone byte +0xb1, **Verified** `FUN_1408f6760`). There is no faction test anywhere on the path (**Verified**: the town list's tick `FUN_14092d850`
  -> each town `FUN_14092cd20` -> `FUN_1408f6760` runs the check for every town whose zone is loaded), so Meitou judges the player's towns too.
- **`regenerates`** (SQUAD_TEMPLATE, 18 true; `Platoon::declareDead`, **Verified**): when the last member dies and the home town is not `ded`, the platoon is not declared
  dead but set to CM_REFRESH (remade on next activation). Meitou: a loaded pinned platoon whose members are all dead (`DeclareDead`), a town squad that unloads with no one alive, or an
  empty pinned platoon at the town's load, is marked to regenerate (whatever the budget).
- **Nest furniture** (**Verified** by code unless marked): when an area loads (`FUN_1409fdbd0` → `FUN_1408f5e70`; "area load" **Observed**), each of its nests runs `Nest` slot 64
  (`FUN_1409278a0`), which queues its TOWN record at its position, owner none; the queue (`FUN_140928410`, "delayedSpawningChecks") runs `TownBase` slot 113
  (`FUN_140938440`) once the zone is loaded. That seeds `srand` from the nest's X/Z and places: `debris` (ITEM / NEST_ITEM, `FUN_140937510`, within
  `size radius`); `debris building` round `num centrepoints` centres (none when 0); `loot spawn` ITEM lists. Nest buildings are not saved
  ([save](../formats/save.md)), so they come back from the seed each load (**Observed**). The fog nests' Prisoner Poles come from here (`Fog Deathyard`
  6, `ruin- fogman resident` 4, `Cannibals` 5; `Nest of Beak Things` has `Beak Thing Egg` debris). Towns with `spawn in town centre` (`Cannibal Village`,
  `Deadhive Overrun` and overrides) queue the same at their own position from `Town::setup` (`FUN_1409353c0` → `FUN_140929340`), run on load (`FUN_14092f260`).
  - Centres (disassembly): each a random direction (X, Z uniform in [-1, 1], normalised) × uniform [0, `size radius`] from the nest, snapped to the navmesh
    (`FUN_140927600`); one within 140 of an earlier centre is put 190 from it, along the line between them.
  - `debris` (`spawnDebris` `FUN_140937510`, disassembly for the arguments): per entry v0 to v1 clusters (`Rand_IntInRange`: lo + a uniform step over
    [lo, hi], clamped, so lo when lo > hi), scale v2 × 0.01 (v2 0: 1). A NEST_ITEM without `physics file`: a point within `size radius`, then `cluster min`
    to `cluster max` meshes, each within `cluster range` × scale of the one before, yaw random, scale ± 0.1, its `ground mesh` (else `mesh`); with
    `number of meshes` n > 1 the bare name less its last character plus an int in [1, n] (`FUN_14092bdc0`: `bone1.mesh`, 6 → `bone1`..`bone6`). Drawn
    only (a static batch); every variant takes the item's own `material`, as the named mesh does (engine reading: the data names only the first). With a `physics file` (`Beak Thing Egg`): as many real items within `cluster range` of the entry's point, each persisted to
    the nest. Then the item's own `cluster` list from the last one placed (v0 to v1 clusters within v2; the egg's `Bone Fragment=15,0,0` is 15).
  - `loot spawn` (VENDOR_LIST): the list for the nest's faction (`FUN_1409592a0`), v0 to v1 items rolled as an item slot's (`FUN_1409540d0`), each at
    a point within `size radius` snapped to the navmesh, none below height 1; persisted to the nest.
  - The real items (physics debris, loot) are made only when the zone's `ZoneMapContent::newGameFirstTimeLoaded` (+0xa8, read through
    `getZoneMapLocation`) is set: a zone with no save file. The meshes and buildings come every load.
- **Homeless spot and template weights** (`FUN_1408f50d0`, `FUN_1408f4d70`, `FUN_1408f7e80`, `FUN_1406b9d10`, **Verified** by decompile). The spot (also the patrol's point, same function): up to 4 x 10 tries of a random point of the
  sector, pushed out of towns (2 x radius own, 1 x another faction's) and, if within 1382.4 of the camera focus, away from it; accepted only when `FUN_1408f4d70` passes:
  the terrain height there (`FUN_1409b32f0`) is **not below 95** (-99, "no terrain", passes), so nothing spawns more than 5 units under the water level (100), and the
  `areasmap.tga` pixel there (1152 units a pixel, `trunc((coord + 147456) / 1152)`, the map object being built from -147456 to 147456 by `FUN_1408fb580`) is not black (black is the NONE group); then, for a loaded sector, the point is
  set to the terrain height, snapped to the navmesh from there (`FUN_1403a3ac0`, box half-extent 600, a door-and-water hit filter; the query being
  nearest-point is **Observed**, from the call shape) and refused inside a non-Ruins town or where the face data's high nibble is above 0. The navmesh covers the water, so walkability alone is not this test.
  So a spot starts on the surface nearest the terrain, not the highest. Meitou (`PopulationWild.SpotGround`, sector and nest spots): `SpawnFloor` 95,
  `BiomeMap.IsPainted`, and a walkable surface within the stand range of the terrain (`GroundLevel`), the gate code read there; a spot with only raised
  floors over it (a ruin's storeys, a rock's top) is refused and drawn again rather than moved (engine choice), and each member is made on the ground
  surface at its own offset (`BuiltSquad.Grounded`). **Observed** headless (#731 save, `Nest of Beak Things` beside the Crater's Ancient Citadel): a
  spot under the citadel has navmesh only at 1910 and 1969 over terrain 1606.
  Weight of a `homeless spawns` entry: `max(0, v0 - k x n)` (times `FUN_1402dd530`, a constant 1), n = the living platoons of that faction and template in the **whole world**,
  loaded or not (`FUN_1406b9d10`), k = (sum of the list's v0) / (loaded zones x P x `homeless spawn amount`), the zone count being the size of the zone manager's loaded-zone map
  (`+0x168128`, read by the zone loops; Meitou divides by ceil(P x amount) instead of P x amount, since a sector makes that many at once: engine choice). So a template is held to its share v0 / sum of the loaded world's squads. Meitou: `WorldShareOf` (living squads, unloaded platoons, kept wild squads, town residents of the same template included as in the original; the zones are the active ones).
  Frequency of a kind is therefore its share of the list: on the Leviathan Coast with the user's mods the five leviathan templates are 11 of 138 (**Observed**, data).
- **Patrol task** (`Task_Patrol`, loaded; **Verified** by code and disassembly unless marked): the platoon's `PatrolInfo` (+0x1e8: biome
  group, has-home flag and home town position, sector range near/far, `patrol approaches towns`, a smell factor) is made once; the
  start sets the range to (1, 2), or (0, 1) when the platoon has a home town, then orders a move whose destination (`CharMovement`
  slot 20, mode 3) is drawn by `FUN_1408fc730` from the leader's position:
  The picker is reached only through `CharMovement::setPatrolInput` (`FUN_14065eaa0`) in mode 3, which `FUN_1405c6b30` sets when the caller passes the platoon's `PatrolInfo` (+0x1e8); the only callers that do are `Task_Patrol`'s start, update and resume (`FUN_140335fd0`, `FUN_140336120`, `FUN_140336210`, its vtable at `Task_Patrol::vftable`), while the three that pass none (`FUN_140336370`, `FUN_1403364b0`, `FUN_1403365b0`) are the town patrols; off screen uses `FUN_1407ee790` (**Verified**, Ghidra references). So every loaded character acting goal 22 PATROL walks sector to sector, homeless or not, in any package (a town guard included, bound by its home's tether below), and goals 36 Patrolling town and 216 Ransacking (`Task_PatrolTownViolent`) never do. Meitou tests the squad leader's goal (members follow; engine reading). Meitou: `PopulationSystem.PatrolStep` for the homeless sectors' squads and `PatrolLoadedSquads` for all others (a town's, a platoon's).
  2. Weight (`FUN_1408f5ad0`): 1 for a sector whose zone is not active. Active: n = its counted platoons + 0.5 × the others (the area
     tick's sets: non-persistent platoons not homed to a nest are counted); 10 when n = 0, a flat 5 when n < 2, 1 at n = 2, 1 / n above
     (**Verified**: constants and branches of the `FUN_1408f5ad0` disassembly). Plus the zone's smell × the smell factor, but that factor
     is `blood smell mult` read from the template's bool fields, so it is always 0 (**Observed**). With a home town, × 5 when the
     sector's Chebyshev distance d from the home's sector equals near or far, × 10 between, × 0 outside: a homed patrol stays within one
     sector of its home's sector, and homeless squads have one (above).
- **Traders**: restocked only while active, when the clock passes the platoon's `inventory refresh time`; an unloaded one catches up at
  its first active tick.
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

**Roll** (`FUN_1409cb310`): each run updates every active campaign, then the requests, then, when the clock passes `updatetime`, sets it
to now + 6 h and, only if the faction has no active campaign and no pending request, rolls (**Verified** +6; gate **Observed**).
- Each `territorial triggers` campaign of the list with a target gets chance c; it passes when c > uniform[0,1); one passer is drawn,
  weighted by c (**Verified** by disassembly).
- c (`CampaignData::vfunc_1`), 0 on any failure: against a player-faction target, the player's living characters ≥ `target population min` and a Town
  target's level (+0x388, the save's `plevel`, below) ≥ `target tech level >=` ("-below town level"); now > last start + `repeat limit` × 24 h; world states and special leader (below); d = XZ
  distance from the faction's nearest own town to the target: f = 1 when `range far` ≤ 0 or d ≤ near × 10, 0 beyond far × 10, else
  linear from 1 to 0 between; f × `raidFrequencyMult` unless `ignores chance mults`; c = `chance per day` × 0.25 × f (**Verified**).
- Player town level (+0x388, `FUN_14048c200`, **Verified** by code; 0 for a new outpost; recomputed when towns merge, `FUN_140931840`, and in `FUN_1404d1870`). Placement-time reassessment merges overlapping same-owner towns: the larger-radius town survives (ties keep the reassessed one) and receives the other town's buildings (`FUN_14054d080`, `FUN_140931840`, `FUN_140927060`):
  over the town's player buildings H = with an interior (Building +0x1f0) that are neither parts nor gateways, T = turrets (BF 12), G = gates (BF 21), R = the
  highest research functionality `tech level` (BF 3). 1 when R > 1, H > 1, G > 0 or T > 3; with G > 0, 2 when R > 3, H > 4 or T > 4, 3 when R > 3, H > 4 and T > 6. Sources: own towns except nests (and, for
  factions with `fundamental type` 1 or `offers bounties`, villages, points of interest, prisons, slave camps); the first source in list
  order with one of its 5 nearest candidates within `range far` is the home (**Observed**).

**Start** (`FUN_1409c9830`): home missing or disabled → the nearest own town to the target. Required men = int(`num forces` ×
`raidSizeMult`) (**Verified** by disassembly). Gathering stops once men > required (each platoon adds its living members):
1. the `special leader`'s platoon, taken as leader without any eligibility test (**Verified**, `FUN_1409c5040` / join `FUN_1409c4720`: it only returns when the platoon is already in this campaign; the free-platoon passes below do test); if not spawned, the squad template naming it `leader`, spawned within 700 of the nearest own town;
2. the faction's free, idle, intact homeless platoons (of a `squads to use` template when the list is set);
3. for each of the 4 own towns nearest the target: such platoons homed there, then new squads drawn from `squads to use` by val0, within
   700 of the town (at the leader once there is one), up to 100 per town; none when the list is empty.

**Requests** (`FUN_1409c9410`): victory/loss trigger rolls, retries, ally help and DIALOGUE_LINE `trigger campaign` (to the speaker's
faction; target: talking to the player's side, the player-faction town nearest the speaker (XZ, nests excluded, `FUN_140927bb0`),
and nothing is queued when the player owns none, so a homeless player gets no dialogue campaign, e.g. no crab tournament; another party:
its platoon's ownerships' town (`FUN_1407956a0` +0x30, none included: it wins); a line with no or an unresolvable target: the speaker's current town, speaker +0x1a0 → +0x88 (**Verified** that it is the default, set first and replaced only when the target resolves as a character or squad;
that +0x88 is the current town **Observed**, as `FUN_14028a3f0` reads it); Meitou `TriggerFromDialogue`) queue a campaign with a due time, target, home and
target faction. A weighted list [v0, v1, v2] fires one entry when uniform[0,100) < Σ v2 (v2 = percent), due after uniform[v0, v1) hours
(**Verified** for dialogue by disassembly, **Observed** for the rest). `faction override` hands the request to that faction; a duplicate
(same campaign and target) pending is dropped, and an active one too unless the caller's flag is set, which a handed-over request always loses (**Verified**, `FUN_1409c9410`). A request the faction queued itself walks that faction's active campaigns in order, dropped at the first whose `trig` byte is already set, each before it having got it set meanwhile. Due requests check only world states and the leader (no chance, range, cooldown
or population); a failed start retries each run; a gone target is redirected or delayed 5-10 h; delays and starts share one counter (+0x58, **Verified**, `FUN_1409cb310`): a delay drops the request when the counter is then above 10, a start when it is then above 100 (101 start tries when never delayed)
(the 5-10 h delay is **Observed**). Gone (**Verified**, `FUN_1409cb310`): the town does not resolve, or it is a player town that has occupiers while the
campaign is not hostile, or that is not active (slot 70); a redirected request does nothing more that run and starts at the next. Requests can aim at NPC towns. A requested campaign joins the receiving faction's list for good.

**Phases** (`CampaignInstance::vfunc_10`, every war-manager run, loaded or not; times are game hours since the phase began):
- Any phase but ended: the leader platoon loaded with every member down → defeat.
- 0 travel: arrived strictly within 0.8 × town radius (XZ; `Town::getRadius` `FUN_140926eb0`, **Verified**: a placed town's `town radius mult` × its type base ([towns](buildings-production.md#placement), not `size radius`, which only the nest debris scatter `FUN_140938440` reads), a player town's [reassessed radius](buildings-production.md#placement), at least 1250; a nest marker is never inside, `FUN_140926d50`) or 400 of a non-town target; then the `dialog announcement` waits, "{1} has arrived at
  {2}", and phase 1, or straight to ended without victory or defeat when there is no `pt2 AI leader`. A loaded, non-hostile, non-DEFEND
  campaign within 1.8 × radius of a town with hostile occupiers (+0x128) is defeated at once (a trader or ally does not walk into an occupied town).
- 2 ended: removed after 3 h (platoons unmapped).
- Unloaded no fight starts, so an unloaded assault always wins at 4 h: a message and its follow-up, nothing happens to the town (the
  earlier "an empty outpost falls" reading was this).
- Packages: phase 0 `pt1 AI`, 1 `pt2 AI`, 2 `retreat AI`; the leader platoon uses `... leader`, others `... others` (else the leader's).
  Installed (`FUN_140272570`, **Verified**) at the platoon's next AI update: when the phase's package differs from the blackboard's override
  (+0x210) the old one is torn down (dialogue event 30 on the leader) and the new one built by its `signal func` (0 → 33 Contract_Timed),
  timed for 24 h and run ahead of the template's packages until its finished test passes (WarBase: the platoon is out of the campaign).
  Classes by `signal func` (**Verified**): 19 Package_WarBase (pt1 "Military Assemble leader/forces"; activate sets job 5 TRAVEL_TARGET
  for the leader, 6 TRAVEL_TARGET_FAST for the others, and the target and mission target from the campaign), 20 Package_WarAssault (pt2
  assault, slave raid, loot food; speed mode 2, the base's 4), 32 Package_WarAssault_Cannibal; retreat packages use 1, 5 or 0 with
  `clears existing jobs`. Goals (**Observed**, data): pt1 leader 215 TRAVEL_TO_TARGET_PACKAGE, 143, 129 MAKE_ANNOUNCEMENT_FAST; pt1
  others 215, 15; pt2 16 (attack enemies), 15, 216, 270, 276 or 131; cannibal 216, 254, 270.
  - Enemies: the base package, in a hostile campaign's phase 1, tags each seen target-faction character with squad memory 4 (TEMP_ENEMY,
    as DA 21); WarAssault tags every seen character except animals (Character vfunc 0x248), those of another town (the town hands differ; none passes) and those of factions with
    `not real` (+0x1d0) set or `fundamental type` (+0x34) 8, Bandit (**Verified**, `FUN_14028a3f0`; [sensing hooks](ai.md#package-classes)). Meitou `AggroSystem.WarEnemy`.
  - Cannibal (`FUN_14028fdf0`, the package's update, **Verified**): per platoon running the package, not per campaign. It counts its own squad's members with `+0x348` set (carrying) and
    keeps its own start stamp (package `+0xb0`): set at the first carrier, zeroed again when nobody carries or the platoon has no squad. With a carrier, the campaign record of that platoon
    goes to phase 2 (time stamped, `+0xc0` = 2) at 3 carriers or when the stamp's age in clock hours x 60 is above 15 (strictly; hours **Observed**). Meitou `PopulationCampaigns.CannibalsLoaded`.
  - Dialogue (`FUN_140680560`, **Verified**): DA 37 sets phase 1 (2 without a pt2 leader package), DA 38 phase 2, DA 39 phase 3, which no
    phase handles (only a leader wipe ends it; the data never uses DA 39).

### 9. Settings and constants that matter

| Name | Value | Effect |
| --- | --- | --- |
| `Squad size multiplier` (`settings.cfg`) | 1 | M in 5.1 |
| `Global population multiplier` | 1 | P in 6.2 |
| `npc range` | settings.cfg value | relevance range, see [game-loop.md](game-loop.md) |
| `raidFrequencyMult` / `raidSizeMult` (`settings.cfg`; options "Town raid events: frequency" 0.25-3, "size" 0.25-4) | 1 | campaign roll chance; required men = int(`num forces` × size) ([campaigns](#campaigns)) |
| area tick | 2 s scaled | 6.2 |
| min distance of a homeless spawn from the player | 1382.4 | 6.2 |

- **Member placement does not snap to the ground** (**Verified**, `FUN_1405ca680`, `FUN_14065e940`, `FUN_14065e870`, `FUN_140145e50`): placing a squad member stores the position in `+0x48` and calls the movement's vfunc `0xb8`, which rejects a non-finite position and otherwise stores it as given and hands it to the nav agent (it finds the nearest nav polygon for it and does not move the character). No walkable snap; walls are handled only by `FUN_1404ffad0`'s "tried to spawn inside walls!" log and the player-town re-placement. Meitou places members on the walkable ground of the spot it picked (engine choice).

## Unknowns

- Nest respawn delay after "wiped out", the default of `DAT_142133588` (the Number of nests multiplier option; **Unknown**, the exe image holds zeros there), the rule that makes `TownPopulationManager::vfunc_4`
  declare a town wiped out (**Observed** gates: roaming pool cap <= 3 and <= 25 roaming members).
- Campaigns: TownBase slots 92 / 96, the disabled-town flag, what besides the two calls recomputes the town level,
  the "fought a player squad" test, the announcement wait's event-2 state, task 215's follower steering, the A* road cost, `Platoon::vfunc_25` (sends phase 0 to 2), whether spawned campaign squads are `persistent`, DIPLOMATIC_ASSAULTS.
- Unique characters: the `Character +0x279 & 7` gate and the 0.1 roll against a town list at platoon unload, platoon +0xac, the unit of
  `replacement time` (constant 2618.18 from `FUN_14066cd50`), UNIQUE_SQUAD_TEMPLATE `persistent` use and `missions` (DIPLOMATIC_ASSAULTS),
  the polarity of the import handle test; other readers of `unique` (`FUN_14067b070` dialogue conditions, `FUN_1407609e0` / `FUN_140761550`) not opened.

## Expression reference

- Changing relations: `value += delta`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- 3.2 Initial values (**Verified**)
- 3.3 Thresholds (**Verified**: -30 and 50 appear in the predicate functions, in the threshold-message function `FUN_1406b28f0` and in the declare-war gate `FUN_1406b3000`; re-read independently)
- 3.5 Relation event sources (**Observed**: decompile of every slot-5 call site found; `faction importance` **Verified** by probe)
- Campaigns: `FUN_1409cb310` (roll), `CampaignData::vfunc_0/1/2` (`1409c5e80`, `1409c5740`, `1409c4ed0`), loaders `read_chance_per_day_n9`
  `1409c8bd0` / `read_is_hostile_n11` `1409c7e40` / `read_campaigns_n3` `1409cabd0`, start `FUN_1409c9830` with `uses_leader` `1409c5040` and
  `FUN_1409c4150`, requests `FUN_1409c9410` / `FUN_1409d27a0`, instance update `CampaignInstance::vfunc_10` `1409c70f0`, end `vfunc_2` / `vfunc_3`,
  redirect `FUN_1409d62f0`, town gates `FUN_140929eb0` (slot 70), `FUN_14048c200` (level), `FUN_140922e60` (occupiers), Town constructor `FUN_140934ae0`; enum names
  from the editor (`WarCampaignEnum`, `MoveSpeed`); data census with a throwaway tool over the merged base files
- Unique characters: manager `FUN_140354560` / `FUN_140349950`, gate `FUN_1405836e0`, transitions `FUN_1407a6200`, `FUN_140330600`, `FUN_140680560`,
  `FUN_1405cf470`, `FUN_1409a8120`, `FUN_1404ff760`, `FUN_1409a8970`, queries `FUN_1409a7e00`, `FUN_1409c4ed0`, `FUN_1409c5040`; special squads
  `FUN_1402ddc90`, `FUN_1402dd680`, `FUN_1402de9b0`, `FUN_14057c6f0`; save `FUN_1409a8660` / `FUN_1409aa270`, `FUN_1402deea0` / `FUN_1402df0c0`;
  `fcs.def` texts; throwaway survey of the merged base files and census of 9 saves (302 usedUniques entries, 3,502 PLATOON records)
