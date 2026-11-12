# Ragdolls

Runtime behaviour of character ragdolls in `kenshi_x64.exe`. The `.phs` file layout is in
[phs](../formats/phs.md); carrying/looting links in [carry-loot](carry-loot.md). Addresses are
functions of `kenshi_x64.exe`. Units are world units (0.1 m). Everything is **Observed** (static
reading of the cited function) unless marked otherwise. Our implementation and its engine choices:
[simulation](../simulation.md#ragdolls). **Verified** by `RagdollJointTests`: the human revolute
joints' bases and limits hold ≥99% of the male skeleton's clip poses (knees and elbows bend the way
the file allows).

## Middleware and scene

- **PhysX 2.8** (`NxCreatePhysicsSDK` version `0x2080400`, NxuStream loaders; the install ships
  PhysX system software 9.10.0513). **Verified**: import and constant in `FUN_1407dbee0`.
- Havok `hkb*`/`hka*`/`hkp*` ragdoll class names exist only as reflection data (`?@14210c…`, no code
  references); no ragdoll code uses them.
- Scene (`FUN_1407dbee0`): gravity (0, −98.1, 0); max timestep 1/60, max iterations 16, time-step
  method 1 (variable); flags 0x44; hardware simulation tried first, software on failure.
  SDK parameters: skin width 0.25, default sleep linear velocity² 2.25, angular velocity² 1.96,
  dynamic and static friction scaling 4.0. Four materials share dynamic friction 1.0, static 20.0,
  restitution 0.05 (the default material too); ragdoll shapes use the one at physics `+0x360`.
- PhysX call meanings (flags, damping, sleep, forces) come from NxActor/NxScene vtable slots matched
  to the published 2.8 SDK headers.
- Stepping (`PhysicsActual::vfunc_2` `FUN_1407dd060`, background thread): `simulate(min(dt, 0.04))`
  (0 while paused; `DAT_1416fb690` = 0.04, **Verified** from the exe's data), flush, blocking fetch. **Verified**: dt is the frame's **real** elapsed time, not scaled by the game speed:
  `FUN_140788a00` stores it as `DAT_142133790` and hands that to the physics kick (`FUN_1403beeb0`), while the speed-scaled value (`DAT_142133794`, `DAT_142133798`) goes to characters and weather.
  So at 2x and 5x ragdolls fall and settle at the real pace of 1x; the ragdoll's own timers (freeze after 2 to 7 s, the get-up blend) run on the scaled value (`FUN_1405b8180` passes
  `DAT_142133794` to `FUN_1407d3c00`, the blend `FUN_1407d28e0` adds `DAT_142133798`), so at 5x they run out 5 times sooner in real time. Meitou: `RagdollPhysics.StepOf`, a tick's step is
  tick seconds / `World.PhysicsSpeed` (the host sets the clock's speed before each tick; 1 headless), clamped to 0.04; the timers stay in game seconds. The real steps add up until they make one of 1x (engine choice: every 2nd tick at 2x, every 5th at 5x), so
  the physics costs the same per real second at every speed; a step's cost hardly depends on its length (**Observed**, a few ragdolls 0.5 ms a tick at 1x and 5x). The buoyancy (`FUN_1407d3c00`) is an
  acceleration handed to the actor (`+0x240`, mode 5, not multiplied by dt) that the physics step integrates, so `Buoy` uses the real step too; only the freeze timer's half speed in water
  (`param_3` x `DAT_14167e8d8`) is on the scaled dt.

## Collision groups

A ragdoll's shapes get group **3**, or **4** while character slot 103 (`0x338`, `FUN_1405c8740`:
true roughly when inside a building) is true; `FUN_1407d3c00` swaps it as that changes (not in carry
mode). Groups 3/4 are also items ([collision](../formats/collision.md#collision-groups-observed-fun_14055f6e0-and-the-loaders)).
Pairs disabled in `FUN_1407dbee0` (third argument hidden on a few calls, assumed 0 like the rest):

| Group | Does not collide with |
|---|---|
| 0, 1 | 6, 9–12, 14, 15–18, 23–26 |
| 2 | 1, 4, 6, 14, 28–31 |
| 3 | 0, 1, 5, 6, 7, 23–26, 28–31 |
| 4 | as 3, plus 2 and 14 |
| 5 (doors) | 0, 1, 3, 4, 6, 14, 23–26 |
| 7 | every group 0–31 |
| 28–31 | 0, 1, 3, 4, 14, 23–26, 6 (last pair's group assumed) |

So ragdolls hit each other, items, building floors/stairs/furniture/roofs (9–22, 27), but not doors,
foliage group 6 or passable parts. A living character's only PhysX shape is its click hull in group 0, which ragdolls skip, and a ragdolled, carried or dead
body is left out of the movement avoidance list, so it blocks no walker
([pathfinding](pathfinding.md#bodies-in-the-way)).

Meitou's building floors: **Observed** (`--load` mec-tou-nu2, a knocked-out character on The Hub's gate walkway, root 1636 over terrain 1511, per-part heights against the collision triangles at freeze):
with the zone's building collision gathered the 13 parts end 0 below the floor (none by more than 0.3), so the group set, the shapes and the transforms are fine. When the body started while its tile
still had ground only (the zone is gathered on a worker the first time a ragdoll moves near it, 10 ticks here), 2 parts ended up to 3.7 units under the floor. A whole body now starts only
when every tile near it has its buildings (`RagdollPhysics.Covered`); the character animates until then.

Run-time buildings (**Observed**, `--scene` staging on The Hub with a Storm House placed through `NavSystem.AddRuntimeBuilding`, collision triangles in a 80-unit box at the placement): the ragdoll's
zone gather had no run-time placements (built, bought or ruined houses: the navmesh's `NavRuntimeBuildings` overlay) and kept a zone's result until evicted, so such a building was not there for bodies:
609 triangles before, after adding and after removing it. Now the gatherer takes the overlay and a change drops the zone's result and bumps `IRagdollEnvironment.Generation`; a tile built at an older generation
asks again and swaps its building mesh when the new one is ready. **Unknown**: whether this is what the player saw on town gates (a gate's own floors were fine in every warm and long run here: 0 parts under the
floor on The Hub's gate, wall walkway, ramp and watchtower, over 2400 ticks with a tile swept and rebuilt).

## RagdollPart and requests

`enum RagdollPart { NONE=0, WHOLE=1, RIGHT_ARM=2, LEFT_ARM=4, HEAD=8, RIGHT_LEG=16, LEFT_LEG=32,
CARRY_MODE=2048 }` (**Verified**: `fcs_enums.def`; LOCATIONAL_DAMAGE `collapse part` uses it). The exe
also uses 0x8000 internally as "release all".

- The active mask is AnimationClass `+0x2f8` (`FUN_1407d1390` all-bits test, `FUN_1407d13e0` WHOLE,
  `FUN_1407d13c0` CARRY_MODE). "Ragdolled or carried" `FUN_1407d1440` = Character `+0x3d4` or the
  RagdollAnimation WHOLE/CARRY flags.
- `FUN_1405cbd60(character, on, parts)` queues `{on, parts}` in Character `+0x3e0`; a queued
  opposite request for the same parts cancels both, a duplicate is dropped. Only when the character
  has a visual or is dead (slot 31) and, for "on", is not in furniture (Character `+0x2f8`, the
  bed/cage state of [carry-loot](carry-loot.md)).
  Meitou (`RagdollSystem.Wanted`): a bed or cage occupant never ragdolls. **Observed** (harness `cage-in --record` in a real world, a placed Prisoner Cage and Prisoner Pole; debug `ko` and `stealth-ko`): the KO'd occupant keeps its fixture position (cage: the `foetal` clip; pole: standing bound), no ragdoll, no slide. A report of a KO'd caged character ragdolling out of the cage was not reproduced (**Unknown**).

- The queue is drained at the sync point `FUN_1407d2380` → `FUN_1405d2c30` → setter `FUN_1405d0db0`,
  which keeps a request queued while the skeleton is not ready or a get-up blend runs.
- The setter, for WHOLE or CARRY_MODE: unlinks a carried body (also when the carrying arm goes limp),
  stops movement, puts the weapon away (slot 90), loads RACE `male ragdoll` / `female ragdoll`, then
  AnimationClass slot 10 (`FUN_1405b9a10`; Human `FUN_1405ba050` also records the arm bits).
  Notify slot 132: humans holding a weapon in a limp arm put it away (`FUN_1405cc230`).
- RACE `male carry ragdoll` / `female carry ragdoll` are **Verified** unused: no string reference in
  the exe. RACE `carry bone` is read by `FUN_1407d3850` (falls back to the skeleton root).
- Animals ragdoll through the same path and fields (`female ragdoll` only for the appearance's
  female flag), but get no partial limb ragdolls.
- Off requests clear bits; at mask 0 the physics ragdoll is destroyed (`FUN_1407d3000`) and the
  animation tracks are re-enabled.

## Triggers

All callers of `FUN_1405cbd60` / `FUN_1405ce9c0` / `FUN_1405d2d20` (part arguments **Verified** in
the disassembly where the decompile hides them):

| Trigger | Request | Where |
|---|---|---|
| Death | on WHOLE unless carried | `FUN_1407a6200`; dead-body tick `CharacterHuman::vfunc_29` re-requests while not ragdolled |
| Death while down | on WHOLE, or CARRY_MODE if carried | `CharacterAnimal::vfunc_64` |
| Knockout, bleed-out (blood ≤ 0 / −25) | on WHOLE | damage `FUN_14064f300` |
| Medical tick while unconscious (`+0x161`) | on WHOLE, or CARRY_MODE if carried | `FUN_140649320` |
| Limb below its collapse level | on with the part's `collapse part` bits, humans only, not for a robotic limb on that side; asked when a non-vital part's h − s + rig falls under 0 (blow `FUN_14064f300`, tick `FUN_140649f50`, `FUN_140666780`) | `FUN_140649870` |
| Leg bits with no leg crippled | off, each medical tick (so a leg between 0 and the KO point is ragdolled for one tick only: Meitou holds the bits only while a leg is down) | `FUN_140649320` |
| Limb recovered | off for arm/leg bits no longer collapsed | `FUN_140649320` |
| Picked up | on CARRY_MODE (replaces a queued WHOLE) | `FUN_1405ce9c0(1)` |
| Put down / dropped | off CARRY_MODE; the medical tick then re-requests WHOLE | `FUN_1405ce9c0(0)` |
| Get up | off WHOLE unless carried | `Task_GetUp::vfunc_1` → `FUN_1405c69f0` |
| Furniture use, entity rebuild, destruction | release all | `FUN_140330600`, `FUN_1405b93a0`, `FUN_140799af0` |

The "on" tail of `FUN_1405ce9c0` also resets the AI task system (`FUN_1405074a0`: the planned tasks are wiped, **Verified**); Meitou `CarrySystem.Link` calls `AiMind.ResetPlan`.

No path from hits, knockback, stagger or explosions: a conscious character is never thrown
([combat](combat.md): knockback is root motion). Animals play `VO_Creature_Die` on a WHOLE request.

### Forced crawl

**Observed** (decompile). Posture (slot 109/110, field `+0xe0`, setter `FUN_1405c7e20`; KenshiLib 1ce4a16 `ProneState`):
0 `PS_NORMAL`, 1 `PS_STAYING_LOW` (voluntary crawl, [below](#staying-low)), 2 `PS_CRIPPLED`, 3 `PS_PLAYING_DEAD`, 4 `PS_KO`.
The medical tick `FUN_140649320` (while conscious) sets the crippled flag (medical `+0x160` = Character `+0x5b8`, [combat](combat.md#crippled-flag)) and posture 2 when **any one** part with `collapse part` 0x10/0x20 tests down,
and keeps those leg ragdoll bits on; the character tick `FUN_1405d00b0` sets posture 2 exactly while the flag is set (else 0, unless the character stays low). So it lasts exactly while a leg is down.
- **Verified** (decompile of `FUN_140649320`, `FUN_140649870`, `FUN_1405c7e20`, `FUN_1405d00b0`): a leg collapse of a conscious human has **no stun and no fall clip**. The tick requests WHOLE only for the unconscious flag `+0x161`
  (a vital part down, blood, the KO timer), and the leg bits `0x10`/`0x20` are never part of that; the posture setter writes the posture, the repertoire bit and (0 to 2) 3.0 at Character `+0x4f8`.
  That is the medical KO timer (`MedicalSystem` at +0x458, `knockoutTimer` +0xa0, KenshiLib 1ce4a16), and the same `FUN_140649320` then sets `+0x161` for a timer above 0, so posture 0 to 2 is a
  3 s knock-out: **Verified** in game for a leg cut off standing (issue #134, [cut-off and leg loss](character-stats.md#cut-off-and-leg-loss)); for a leg only crushed or worn below the KO point it
  follows from the same path (**Observed**, not measured; Meitou gives that case no knock-out).
  The body does not fall: a partial ragdoll simulates only the masked bodies, the rest follow the animation ([bodies](#bodies-kinematic-or-simulated)), and posture 2 picks the crawl clips the same tick.
- The sever's own knock-out (`FUN_14064edc0`, a leg lost at posture 0) is overwritten by that 3 s one ([cut-off and leg loss](character-stats.md#cut-off-and-leg-loss)), then it wakes into the crawl. A character that collapses and is out cold at once is a vital part or blood knock-out in the same blow.
- **Observed**: the first tick that sets the flag (not in furniture, not carried) also calls `FUN_1405c8330`. It acts on the AI (`Character +0x648`: its vfunc `0x60`) and the orders receiver (`+0x650 +0x20`, the same object that holds the order queue `+0x40`, [ai-tasks](ai-tasks.md)): it sets the receiver's stop flag `+0x26c`, deletes its queued tasks (`FUN_14050d1b0` on `+0x300`) and its active ones (`FUN_140507530`). So the running task, queued orders and fight go; whether the AI's goal list is also dropped is **Unknown**, and no replan is requested there (the chooser finds the task gone). Skipped when the AI's current task has the flag at task data `+0x22`: 28 Standing, 65 Get up, 147 Stumbling.
  Meitou: `MedicalState.CollapseOnset` (set by `Tick` when a leg is down where the last update had none; the first tick after a load only learns it), taken by `BodySystem.SlowWorld` the same tick (the medical update is parallel): `OrderQueue.Clear()` and `TaskStart.End(Stopped)` (ends the task and fight, stops, asks the decision step; an NPC's AI picks again from idle). Spared: carried, in a bed or cage, stumbling or getting up (`StaggeredUntil`; Meitou has no task 28).
  So a character splinting or bandaging **itself** stops when that leg first goes down mid-treatment, while another medic's treatment of it goes on (as the original, kept on purpose; issue #518; **Observed** headless).
- **Observed**: going from posture ≠ 0 to 0 with the crippled flag clear (a leg back up, a stun worn off) makes `FUN_1405c7e20` queue the get-up task 65 (`standing up 3`, [Getting up](#getting-up)) unless one is queued.
- Leg test `FUN_140644450`: down when a stump or crushed (limb state 1/3), when h − s < −(base × mult ×
  scale) − wear (severed), or when h − s + b (bandage, `+0x48`; not the rig `+0x4c`) is under the KO point
  `FUN_140643ae0` (−10..−85 by toughness), not under 0. Animals (slot `0x248`, `asAnimal`) never collapse.
  Between 0 and the KO point a leg only limps (leg factor, [speed](character-stats.md)).
- Posture ≠ 0 picks category 5 GROUNDED lower clips (`AnimHuman_LowerCombat` `FUN_14051fc60`); base data
  (rebirth.mod): `crawling move`, `crawl idle up` (any combat mode), `crawl idle down` (out of combat), `crawl use`.
- Defence techniques must have `is prone` equal to posture ≠ 0 (`FUN_140887970`): a crawler has no block or
  dodge (no prone one exists). The attack pick (`FUN_1408876d0`) applies the same test (**Verified**: disassembly 1408877d4-1408877f9 rejects burden above the maximum, then the prone mismatch, whatever the burden; the decompile prints a spurious `<` or `==` split): a crawler
  attacks only with `ground attack L/R`. Nothing in the fight code ends a crawler's fight.
- Speed (`FUN_140886460`, **Verified**): a final override after the water and sneak caps, 5.0 crippled, 7.0 at posture 1. The crippled flag
  (medical +0x160) is set only by the medical tick, so nobody is crippled before their first one (Meitou: `Speed.Crippled`, `MedicalState.Crawling`).
- The get-up task (65, `FUN_14033ca10` / `FUN_140334a20`, **Verified**) of a character with posture > 0 ends at
  once and sets posture 1 (voluntary crawl), which the medical tick does not turn back into 2. The planner's
  `isTargetDown` (`FUN_14099a5a0`, **Verified**) counts as down: no target, carried, unconscious, dead, in a bed or cage, or posture ≥ 2 (a non-character target is up); `iAmProne` is posture ≠ 0. Meitou: `ProductionSystem.TargetDown` (Staying close).
- Meitou: `HealthPart.LegDown`, `MedicalState.Crawling` (KO point cached each medical tick), `AnimationSystem.Crawl`
  (engine choices: `crawl use` replaces the medic clip; crawl legs weighed at no less than −100). The ×0.5
  sight factor for a crawler ([perception](perception.md)) is not applied to crawlers who are not sneaking.

### Staying low

**Observed** (character tick `FUN_1405d00b0`, `Character::setStandingOrder` `FUN_1405caa50`,
`Character::ImStealingDoYouNotice` `FUN_140794810`, run speed `FUN_140886460`; field names KenshiLib 1ce4a16):
- Set only on getting up: while totally ragdolled (`AnimationClass::isTotallyRagdoll`, WHOLE) and not carried, a posture
  3/4 character whose AI can get up becomes 3 when `Character::isItSafeToGetUp` fails, else 1 when sneaking (`+0xd4`),
  else 0. `playerWantsMeToGetUp` (`+0xec`, set for each selected character by every player order, `FUN_1407f9e20`,
  cleared each tick) turns that into 0 (1 only from 3 while sneaking). The exe tooltip says the same: a sneaker who wakes
  stays down and crawls. No other setter of 1 was found. Meitou (`PlayerState.Ordered`): every order command that names characters sets it, the job giver's orders (45, 0x111, 0xdf) excepted; the original loops over the whole selection (the flag is set before any order-specific code, `FUN_1407f9e20`:96), Meitou only over the recipients.
- Standing (not WHOLE): crippled (vfunc 0x360) gives 2; otherwise 1 is kept, anything else becomes 0, and 1 becomes 0
  too while `_isLiterallyUnderMeleeAttackRightNowForSure` (`+0x2b0`). So the forced crawl ends a voluntary one.
- Standing orders: run, jog, walk, group speed (0, 1, 2, 16) on a posture-1 uncrippled character set 0 instead (the speed arrows do not step while anyone selected is in posture 1, **Verified** `FUN_1407f3a20`, [ui-screens](ui-screens.md)); stealth
  off (4) sets 0 and ends sneaking. A theft witness (`ImStealingDoYouNotice`) in posture 1 or 3 gets 0.
- Effects are those of posture ≠ 0: grounded clips, prone techniques only, stealth ×2, sight ×0.5, target-score terms; the crippled task repertoire 0x40, which the
  posture setter `FUN_1405c7e20` sets for posture 1 and 2 only (**Verified**; [planner](ai-tasks.md#planner)); run
  speed S = 7 (5 with a leg down, [pathfinding](pathfinding.md#speed-and-orders)).
- Stand Up (order 28, also shown for 65): `Character::addOrder` queues AI task 28 "Standing", built as `Task_UnCrouch`
  ([ai-tasks](ai-tasks.md)): it plays `squat T` backwards (the start `FUN_140345fb0` and the update `FUN_140346090` issue it at rate -1, **Verified**) and sets posture 0 once the clip's progress is under 0.1 (`DAT_14167fad0`, the 0.1 of `Task_Follow`'s catch-up floor), then ends (the matching `Task_Crouch` plays it at +1 and sets the crouched stance byte once its progress passes 0.9, `FUN_140346440`: no crouch task in the base data). Meitou also acts the goals CROUCH (27) and STAND_UP (28) out for modded packages (`AiBehaviour.Crouch`): `AnimationSystem.FinishCrouch` sets `CharacterCold.Crouched` (the stance byte, `StanceOf`) when `squat T` forwards passes 0.9 and clears it under 0.1 backwards or when the goal ends. `AnimationSystem.FinishStandUp` plays the clip from its end backwards (held clip rate -1, a layer that starts backwards starts at the clip's end) and clears `StayingLow` at the 0.1 mark; with no clip, or a character that is moving, at once. While getting up the
  tick queues task 52 `GET_UP_STAY_DOWN_THOUGH` (posture 1/2) or 65 `GET_UP_STAND_UP` (posture 0). Animals always post 65 (one branch, no posture computation, no 52). Meitou: `CharacterFacts.GetUpGoal` (derived from `GettingUpUntil` and the posture; no consumer yet).
- The posture is not saved: neither the character writer (`FUN_1406280a0`) nor the medical writer (`FUN_14064ea50`) has a
  key for it; the health panel (`MedicalSystem::getMedicalGUIData`) shows "Staying low" for 1 and "Playing dead..." for 3.
- Meitou: `MedicalState.StayingLow` (not saved), `MedicalState.Prone` (posture ≠ 0) read wherever posture ≠ 0 is; set on
  waking in `CombatSystem.Act`, cleared by a leg down, a speed order, sneak off, Stand Up (`StandUpCommand`, `CharacterCold.StandingUp`: `squat T` backwards, see below), the melee alert (`Fighting`, `AI::underMeleeAttack`), a caught thief's watcher (`CrimeSystem.Steal`) and a
  player order to a selected character still getting up (`PlayerSystem.WantsToGetUp`, the orders that name characters).

### Playing dead

**Observed** (`FUN_1405d00b0`, `Character::isItSafeToGetUp` `FUN_1405ca450`, perception update `FUN_1408580d0`;
KenshiLib 1ce4a16 names): posture 3 is chosen on getting up when `isItSafeToGetUp` fails, and kept while it does (the test
runs each tick while WHOLE). Safe: the character's `SensoryData::numEnemies` (`+0x94`) is 0, or its state broadcast's slave
state is 1 (a slave), or `numConsciousAllies` (`+0x4c`) is above 0 (sensed allies in posture 0, not unconscious or dead). A
player order gets it up (1 when sneaking), as does a thief's watcher noticing.
- Counts (`FUN_1408593d0`, per sensed character): `numEnemies` (`+0x94`) and `numNeutrals` (`+0x98`) skip the unconscious
  (vfunc 0x30), the dead (`+0x5bc`) and the carried (`+0x3d4`), so downed enemies do not count; allies count in
  `FUN_1408580d0` (above).
- Toughness: the Toughness tooltip lists "Get up and fight instead of playing dead" under **Ways To Train**, not as an
  effect. `Task_GetUp`'s start (`FUN_14033ca10`) with posture 0 and `playerWantsMeToGetUp` calls `FUN_1408c6780`: no
  enemy or neutral sensed: dialogue event 0x17; else with no conscious ally sensed: toughness gains
  core(f x (`numEnemies` + 10) x 0.075 x race[toughness]) ([character-stats](character-stats.md)) and event 0x19; else
  event 0x18. Without the order the get-up only says 0x17 / 0x18.
- Meitou: `MedicalState.PlayingDead` (not saved): a knockout that ends while `MedicalContext.PlayDead` is set keeps the
  character `Unconscious` (so everyone treats it as down) with no timer. The get-up line (`DialogueSystem.SeeGettingUp`) is 23 / 25 / 24 by these counts. `BodySystem.Schedule` sets it serially from the last
  tick by the character's senses' counts (`BodySystem.Senses`, [senses](perception.md#senses); the character looks while playing dead or about to wake, faded counts would always read safe); the waking window is Act's longest step, so a far character is asked too. A vital part or
  the blood ends a knockout on the tick it heals, which no window foresees: one whose senses faded (`SenseBlock.Faded`, not carried
  or a slave) is held down, unconscious, until its next look (`MedicalContext.GetUpUnasked`, engine choice), then asked; a player
  order standing it up grants the toughness (`BodySystem.GotUpByOrder`); animals never play dead. Rechecked every 15 ticks
  (engine choice). `MedicalState.StandUp` ends it. Health header: `HudText.HealthWord`.

## Bodies: kinematic or simulated

The ragdoll (`FUN_1407e4ed0`, `ScytheRagdollPhysicsT`) is created at the scene node position
(character position if zero/invalid; for WHOLE outdoors snapped to terrain height when below it or
over 500 above), scaled by character slot 114 (1.0 for humans, age-lerped size for animals).
Each new body (`ScytheRagdollPhysicsT::vfunc_8` `FUN_1407e2ae0`): kinematic, FILTER_SLEEP_VEL,
linear and angular damping 0.5, collision on. `AnimationClass::vfunc_11` (`FUN_1405b8720`) then
marks each bone **pinned** (kinematic, follows animation) or **simulated** and calls
`FUN_1407d1d20(ragdoll, 1, pinned, bone)` (**Verified** argument order in the disassembly):

| Mask | Simulated bones | Pinned |
|---|---|---|
| WHOLE | all | none |
| LEFT_ARM / RIGHT_ARM | that UpperArm, Forearm | Clavicle and the rest |
| LEFT_LEG / RIGHT_LEG | that Thigh, Calf | Foot and the rest |
| HEAD | Head, Neck | the rest |
| CARRY_MODE, human | both Forearms, Head | the rest (animated carried pose) |
| CARRY_MODE, animal | all but the carry bone | carry bone |

The carry bone is a skeleton bone, not a body name: its body is the one the ragdoll file attaches to that bone (**Observed**, Pet Dog: bone `Bip01`, its ragdoll body is the unnamed "Actor"). Meitou matches by bone (`RagdollSystem.Simulates`); matching the name pinned nothing and the whole dog fell to the ground under its carrier (issue #176, parity s15-f24).

**Verified** (disassembly of `ScythePhysicsT::vfunc_2` `FUN_1407d4cd0`) the carried animal's pin each frame: the pinned actor (controller `+0x130`) is made kinematic and set to orientation
carrier node rotation x the attach bone's derived rotation x its binding-pose inverse (the bone's turn from its binding pose), position the attach bone's world position
(`FUN_1405b1c50`, the carrier's position when zero) + the bone's world rotation x the vector at controller `+0x160`. The attach (`vfunc_15` `FUN_1407d5290`) subtracts from that vector a
per-bone vector of the carrier's `AnimationClass` (`FUN_1405b83a0`, map entry `+0x28`, zero when the bone is missing); its starting value and meaning are **Unknown**.
Meitou (`RagdollSystem.AnimalAttach`): places the animal's node so that its carry bone's actor has exactly that pose (the actor has no rotation of its own, [phs](../formats/phs.md); Meitou's body is its shape, placed on it), with the vector taken as zero (engine choice); the rest hangs. Headless probe (Bonedog on a
Greenlander, issue #307): the dog's `Bip01` on the shoulder bone, its head level with it out to the carrier's left, the front paws 8 to 11 below; with the human rule it stood 6 above
the shoulder, over the head.

**Verified** (decompile of `FUN_1405b8720`, the `+0x5a` carry flag branch): a carried human keeps its animated "carry me" pose and only the two forearms and the head are simulated; every other bone,
legs included, is pinned to the animation. The carried body's scene node is **not** moved to a ragdoll body (`FUN_1407d3c00` takes the root-body position only for WHOLE or a carried animal): the
carry placement of [carry-loot](carry-loot.md) sets it each frame. The pick-up attach (`FUN_1405ced90`) clears the active mask (`+0x2f8` = 0) and the CARRY_MODE request then rebuilds the ragdoll from the
animated pose; put-down / drop (`FUN_1405ce9c0(0)`) requests CARRY_MODE off, the medical tick requests WHOLE again for a still-unconscious body. Meitou: `RagdollSystem` (`Simulates`, `Rebuild`) does the
same; the limbs that swing are the forearms and the head, so a body's upper arms and legs follow only the `carry me` clip (`play speed` 0.006: almost still).
Where the release leaves the scene node, which the new WHOLE ragdoll is built at, is **Unknown** (`FUN_1405ce1e0` not traced past the detach and the clip stop). Meitou (engine choice)
starts the pinned bodies of a body set down or dropped from the placement it hung in (`Ragdoll.HungIn`), so it falls from the shoulder (issue #327). Re-placing the `carry me` pose upright at the body's new position hung the trunk up to 10 units under the floor.
**Observed**, headless put-down on the upper floor of an Outpost s-IV in zone 47.16, before and after the change: 5 of 13 bodies started 1 to 8 under the floor and froze there (the
two-sided floor held them under it); now all 13 start 10 to 15 above it and rest 0.3 to 2.4 above it.
The animal's pinned body is matched by **bone**, not by actor name: `FUN_1405b8720` keys its pin map by the bone names it hands to `disableBone`. **Verified** (probe of the shipped
`.phs` files): Bonedog `dog1.phs`, River Raptor, Small Spider, Spider and Skimmer name their root actor `Actor` (bone `Bip01`), and the Bonedog, River Raptor and Small Spider have no `carry bone`, so
their root fallback `Bip01` is that actor; matched by name, nothing would be pinned and the carried body would fall off the shoulder. Meitou: `RagdollSystem.CarryBoneOf` (a bone handle).

Activating a simulated body: clear kinematic, ENERGY_SLEEP_TEST, linear damping 0.05, angular 0.01,
sleep energy threshold 5.0, wake 0.4 if asleep. No joint motors or springs are set: a "powered"
pose exists only as pinned kinematic bones.

### Limits of motion and joint springs

- **Verified** (`FUN_1401a8be0` body desc fill, constant read): a body's `maxAngularVelocity` is -1 (the SDK global), and `FUN_1407dbee0` sets only the SDK parameters 1, 2, 3, 5, 6 (skin width, the two
  sleep thresholds, the two friction scalings), never `NX_MAX_ANGULAR_VELOCITY`; so every ragdoll body turns at PhysX 2.8's default global cap of 7 rad/s (**Observed**: the default's value is from the SDK
  header, not from the binary). Meitou: `RagdollPhysics.MaxSpin` 7 (was 30, an engine choice): a swinging forearm or head cannot whip.
- **Verified** (`FUN_1401a81b0`, the revolute branch): spring 1 enabled sets the revolute spring flag with the file's spring, damper and target (degrees); `hu_m_ragdoll_5` has one, on Head -> Neck
  (10000, damper 0, target 0, [phs](../formats/phs.md)). The head is therefore held at the neck's pose by a stiff undamped spring (about 20 Hz on a 1-unit sphere of mass 1) and sags only a hundredth of a radian under its
  weight; it does not flop to its limits. Meitou: `RagdollSystem.HingeSpring` (a Bepu `TwistServo` of the same frequency and damping ratio on the moving inertia).
- **Verified** pinned bodies (`FUN_1407d1550` -> `FUN_1401a9ea0`, `FUN_1401a9f00`): a pinned body gets position and orientation **set** (separate setters, not a kinematic move) and its linear and angular velocity zeroed,
  every frame; a joint to a simulated limb therefore sees only the position error of the jump, never the pinned body's speed. Meitou: `RagdollSystem.Pin` does the same (**Observed** in the sandbox carry scene: the joints stay stable, the forearms swing slightly more after the lift than with the velocity drive it replaced).

### Joint frames, zero angle and body properties

- **Verified** (decompile of `FUN_1401a81b0` and its common part `FUN_1401a7e90`): a joint's two actors get the same local normal and local axis (the file's, turned by the insert rotation) and the anchor
  is set globally from the actors' poses at build time. All actors are created with one orientation (the insert rotation), so a revolute angle is 0 exactly when every body shares one orientation. The
  global-axis setter runs only for a joint to the world. There is no joint to the world in the human files.
- **Verified** (`FUN_1407d26b0`, `FUN_1407d1550`, `FUN_1407d3a30`): that shared orientation is the binding pose. Each ragdoll bone keeps its binding-pose derived orientation (`OldBone::_getBindingPoseInverseOrientation` and its
  inverse, at bone-record `+0x68` / `+0x58`); a body's orientation is node rotation x bone derived orientation x binding inverse (the bone's turn from its binding pose), its position node + (node rotation x bone
  position) x scale, and a simulated body writes the bone back as parent inverse x actor x binding. So a revolute angle is the relative turn of two bones from the binding pose, about the file's axis, and the ranges are
  measured from it. Meitou already does this (`RagdollBody.BoneInBody`, `RagdollJoint.BasisA/B`); no change to the reference angle was needed. The spine's clip-fit outliers
  ([below](#rest-poses-of-the-spine)) are therefore the file's own limits, not a different zero.
- **Verified** (`FUN_1401a81b0`, file read with the repo's reader plus a raw dump of `hu_m_ragdoll_5.phs`): a revolute limit is low = `+0x5c`, high = `+0x58` in radians, restitution 0, hardness 1 (hard). The
  projection mode is applied only when field `+0xdc` is 1; it is 0 with distance and angle 0 in every joint of the file (no projection). No joint is breakable (max force and torque 0), none has motor or collision enabled
  (so jointed pairs do not collide) and only Head -> Neck has a spring.
- **Verified** (`FUN_1401a8be0`): a body gets the file's mass (1), mass-space inertia 0 (PhysX computes it from the shape) and density 0; angular damping is the file's x 10 (0.5) and linear 0 until the ragdoll callback
  and the activation set 0.5 / 0.5 and then 0.05 / 0.01; solver iteration count = max(file, 16); body flags 0x900; no centre of mass offset in the ragdoll files. All 13 bodies weigh the same (a head as much as a torso).
- **Verified** (`FUN_1407dbee0`): scene time step method variable with max step 1/60 and max 16 iterations (a 1/30 s frame is two 1/60 steps, 0.04 s is three); SDK dynamic and static friction scaling 4.0 (parameters 5, 6) over
  materials of dynamic 1.0, static 20.0, restitution 0.05, so the contact friction a ragdoll meets is the material's times 4. **Observed**: that the scaling multiplies the material's coefficients is from PhysX 2.8's
  documentation, not the binary. Meitou used friction 1 (`RagdollPhysics.Friction`).
- Differences that stay: Bepu has one friction coefficient (no static one, so 4 stands for the dynamic coefficient only), its limits are soft constraints solved with 2 iterations in each of 4 substeps against PhysX's
  hard limits and 16 iterations (a pose measured at the limits overshoots them by under 1 degree on average, **Observed**, `ragdoll-dump` on 13 bodies), and shape skin width 0.25 is not modelled.

## Initial state

`FUN_1407d1800` places every body at its bone's current animated world pose and gives simulated
bodies zero velocity. The setter passes the character's movement velocity (Y zeroed, dropped if
|v|² > 100), but slot 10 never uses it (**Verified**: its register is reused after the clamp). No
hit impulse exists. `FUN_1407d2d10` (per-limb speed caps) and `FUN_1407d2290` have no callers.

Meitou ("Advanced ragdolls", Advanced settings, on; Faithful off; `BodyOptions.AdvancedRagdolls`): a new whole-body (not carried) ragdoll starts with every new body at one speed: the
`CharacterCold.Blow` that a downing melee hit or bolt left (`CombatSystem.Knockdown`: the victim's last-tick velocity plus a push along the blow of
0.5 x (cut + blunt + pierce) units/s, at least 12.5, uncapped, with 0.2 of it upward; executions set it too; also kept as `CharacterCold.Knockdown` for a cut-off head), else the character's last-tick velocity (a bleed-out while running). A blow's push
is focused on the struck part (below). Bodies kept from a partial ragdoll keep their own speed; a ragdoll restored from a save starts as saved.

### Advanced ragdolls (Meitou)

Engine feature behind the same switch (`BodyOptions.AdvancedRagdolls`; Faithful: the original's hard limits and no pull), tuned by eye (**Unknown** against real motion):
- **Soft limits**, every ragdoll: each limit gets an inner one 25% of its range in from either end (`SoftBand`; a spherical joint's swing cone too), a firm critically damped spring (20 Hz, ratio 1)
  that stops the joint a quarter of its range short of the hard stop ([Rest poses of the spine](#rest-poses-of-the-spine): the weaker 5 Hz ratio 3 one let backs creep into the stops).
- **Relaxed pull**, whole and carried ragdolls of an incapacitated character (not a conscious one's collapsed limbs, which hang loose): a capped `TwistServo` on each joint towards
  60% of the way from the binding pose to the middle of a hinge's range (knees about 50 degrees, elbows about 45, hips about 24 degrees of flexion (**Observed**: the hip's clip poses run −130 to +50 with a median of −17, so negative is flexion), spine at its binding pose; a shoulder's twist to 0, its swing left to
  gravity). Unconscious 1.5 Hz with at most 40 torque, dead 0.8 Hz with at most 12 (muscles go slack at death); the head keeps only the file's spring. Re-set when the state
  changes (a knocked-out body dying), removed at the freeze. Gravity beats it, so it shapes the rest pose rather than holding one.
- **Spin on impact**: a new ragdoll started by a blow (`CharacterCold.Blow`'s push) spins its upper arms along the push and its thighs against it (the feet held the ground),
  0.15 rad/s per unit/s of push, at most 6, each limb by its own roll of 0.6 to 1.4 (`RngPurpose.RagdollSpin`).
- **Focused push**: the struck part's LOCATIONAL_DAMAGE `bone name` (else `bone name 2`; its nearest ancestor with a body when the bone has none) names the struck body
  (`RagdollBlow.Bone`, `RagdollSystem.Struck`). The struck body takes the whole push times the ragdoll's mass over its own (at most 4x: an impulse on it
  alone), every other body 0.4 of it, so the body is thrown from the hit part and the joints drag the rest; no start cap (the physics holds each body to `RagdollPhysics.MaxSpeed`, 200). No bone: the whole push on every body.

### Rest poses of the spine

Reported: knocked-out and dead bodies often end with a rounded ("arched") back. **Observed** in the hidden game (`ragdoll-dump`; Siwa plus 12 spawned Dust Bandits of `t_flats_shore`, standing still ("stand") or
ordered to run for 40 ticks ("run"), then `ko`, dumped 300 ticks later). "Rounded" = lumbar twist minus thoracic twist above 40 degrees (forward flexion is positive for `Spine1 -> Bip01` and negative for
`Spine1 -> Spine2` and `Spine2 -> Neck`; limits +-37 and +-30). One run of 13 bodies per cell: the rest pose depends strongly on small differences, so read the counts as indications.

| Setup | stand: rounded of 13, mean abs curve | run |
|---|---|---|
| Faithful (Advanced off), friction 1 | 2, 22 | 7, 39 |
| Faithful, friction 4 | 1, 28 | 5, 38 |
| Faithful, friction 4, 2 substeps x 8 iterations (nearer PhysX's hard limits) | 5, 40 | 8, 46 |
| Advanced as first built (soft limit 5 Hz ratio 3, relaxed pull) | 6, 37 | 11, 52 |
| Advanced, soft limits only | 12, 62 | |
| Advanced, relaxed pull only | 5, 30 | |
| Advanced, soft limit 20 Hz ratio 1, friction 4 (now) | 1, 35 | 4, 33 |

- The curled kneeling pose (hips -100 to -110, knees 140 to 157, lumbar +38 to +41, thoracic -31, both at their stops) is what a standing or running body does when its legs fold: a knee flexes under a forward-leaning
  body. The joints, zero angle and limits are the original's ([above](#joint-frames-zero-angle-and-body-properties)), and the closer Meitou's solver gets to hard limits the more often a collapse ends on the stops (5 to 8
  of 13). So the pose is very probably the original's own, which also starts at rest with no impulse. **Unknown**: how often the original ends in it (PhysX's solver, static friction 80 and shape skin are not reproduced).
- Advanced ragdolls made it worse: the soft limit was a weak overdamped spring (5 Hz, ratio 3), under which a collapsing back creeps into the stops (alone: 12 of 13 rounded). It is now a firm one (20 Hz, ratio 1)
  a quarter of the range short of each stop, so the back stops rounding at about half of the file's range. Edge: ranges of the other joints shrink the same way, so most bodies end with hips near -85 and knees under 90.
  Not tried further: a stronger spine pull (150 torque at 3 Hz) changed nothing measurable.
- The hinge joints agree with the clips for the limbs (the angle `RagdollJointTests` checks: 0.0 to 0.8% of 696 clip poses of the male skeleton outside the ranges, 3 degrees of slack) but not for the back: `Spine1 -> Bip01` 10%
  outside (its clip poses run from -8 to +54, median +15, against +-37), `Spine1 -> Spine2` 1.3%, `Spine2 -> Neck` 4.2%, `Head -> Neck` 19%. With the reference **Verified** as the binding pose (above) these are the file's own
  limits, narrower than the animators' poses: the original pushes such a pose back to the limit the moment its ragdoll starts. The earlier guess that Meitou's zero angle differed is disproved.
- **Unknown**: whether bodies of one character collide with each other in the original beyond jointed pairs (groups 3 and 4 do not disable themselves, no actor-pair flag was found; Meitou lets non-jointed pairs collide).

## Per frame

Driver `FUN_1405b8180` (AnimationClass), update `FUN_1407d3c00`:

- Root: for WHOLE (or a carried animal) the scene node is set to the root body's position
  (`FUN_1407d1490`: the body named at RagdollAnimation `+0x48`, else the first simulated one);
  otherwise the node keeps the movement position. The body's position is the actor's origin (the phs actor `position`), not its shape's centre. Meitou:
  `RagdollDefinition.RootAt`. **Observed**: whale2.phs (Big-Bones) is about 1.3 x its skeleton, so its Bip01 capsule centre lies 29 from the actor; following
  the centre left a looter 5 m off a downed Cleanser Unit.
- Bones (`FUN_1407d3a30`): a simulated body writes its orientation into its manually controlled Ogre
  bone (plus position for a bone without parent); a pinned body is moved to the animated pose.
  Meitou (`RagdollBody.BoneInBody`): the bone sits at its actor's origin, so a parentless bone is drawn there and the other bones keep the skeleton's offsets; a pinned or new
  body puts its actor on the animated bone (engine choice for the pinned case: the same convention both ways, so no jump on entering a ragdoll). Where the file and skeleton
  differ (whale2: Bip01 actor 24 from the bone) the shapes keep their offsets from the actor, not from the skeleton's bone.
- Carried animal: the carry-bone body is moved kinematically to the carrier's attach bone.
- Partial (non-WHOLE) ragdolls are woken every frame (wake 20) and never freeze.
- Water (only when byte `+0x79` of the character's race data is set): when the root is at or below
  y 100 (water level), each body below it gets an upward
  acceleration 98.1 + ln(100 − y + 1), and the freeze timer runs at half speed. Meitou: `RagdollSystem.Buoy` (each body under the level; the original's "below it" read as below the water level).
- Character simulation position `+0x48` = root body position while ragdolled or carried
  (`CharacterAnimal::vfunc_28` via `FUN_1407d16f0`): looting, carrying and AI distances use it.

## Water state of a ragdolled body

**Verified** (decompile, `FUN_1405d00b0` Character::threadedUpdatePeriodic, `FUN_1405c7fd0`): every update, a ragdolled or carried character whose movement position differs from the body position (Character `+0x48`) casts a ray straight down from the body (raised by a constant) against the physics world; a hit above 0 is stored as the stand height `+0x698` (a miss keeps the last) and the body position is pushed into the movement. `FUN_1405c7fd0` reads `+0x698` for the water state (ground + 2 at or under the water level 100: 15 x size deep, 6 x size waist) and a carried body follows its carrier. Ray mask: `0x7de04` outdoors (location type 0xb) or when the building test passes (groups 2 terrain, 9-12 floors, 14 walkable foliage, 15-18 stairs), else `0x7f9e00` (groups 9-12 and 15-22: no terrain or foliage, furniture 19-22 included); ragdoll bodies (groups 28-31) collide with neither 6 nor 14. Meitou: `RagdollSystem.StandOn` takes the ragdoll environment's ground height (terrain or the building floor near the body) as the probe, `CharacterCold.StandHeight`, and sets `WaterState` for a non-carried whole-body ragdoll; the carried body's state is its carrier's in `WeatherSystem`. **Observed**: the two masks are folded into `IRagdollEnvironment.GroundHeight`; walkable foliage is not probed.

## Sleep and limits

Freeze = every body put to sleep and updates skipped (`FUN_1407d1f70`), WHOLE only:
- after 2 s of ragdoll, within the next 5 s when ≥ 60% of bodies sleep; after 5 s a downward ray
  from 20 above the node against groups 2, 9–12, 15–22 (mask 0x7f9e04) decides (`FUN_1407d2440`);
- at once when the terrain tile under it is not loaded (`FUN_1405b8180`). `FUN_1405d2c30` drains the character's animation request queue and then calls `FUN_1405b8180` (**Verified**; the earlier note that this is the AI task manager was wrong). Meitou: a whole ragdoll whose root is outside `World.ActiveZones` freezes at once (`RagdollSystem`).

The frozen flag clears when a body wakes (`FUN_1407d2050`). `FUN_1407d4270` (reset timers, wake all
with 0.4) runs when bone flags are applied and when a frozen ragdoll is carried. A root
more than 200 below the terrain is lifted to it and frozen (`FUN_1407d4470`). Global flag
`DAT_1421349c9` (the paused flag, [game-loop](game-loop.md#speed-and-pause)) disables freezing and buoyancy. No distance LOD or ragdoll count cap was found
(`DAT_142133080` is a live-actor counter only). Meitou (engine choice, `RagdollSystem.LimbReach`): a collapsed limb of a character still up dangles only
within 300 units of the camera (330 once dangling); farther it follows the animation. Headless runs (no camera) dangle at any distance.

## Getting up

`Task_GetUp` (`FUN_14033ca10` start, `FUN_140334a20` update):
- Clip: humans `standing up 3`; `crawl idle down` with leg state 1–2; `swim idle` in deep water
  (`FUN_1405c7fd0` level 3); animals their state-matching idle (`AnimationClass::vfunc_25`). No
  face-up/face-down variant.
- `FUN_1407d36e0` → `FUN_1407d3130`: heading = root→`Bip01 Head` direction flattened to the ground
  (rotation from +Z); each bone blends from its ragdoll pose to the clip's first key over 0.75 s
  (`FUN_1407d28e0`: nlerp rotation, lerp translation), then the bones return to animation.
- The task ends (**Verified** decompile of `FUN_140334a20`) once the ragdoll blend object (AnimationClass `+0x2f0`) is absent or its blending
  flag (`+0x61`, `FUN_1407d1400`) is clear, and either the task's posture flag (`+0x78`, set at the start from posture > 0) is set or the clip
  passed 0.86. So a waker staying low or crawling (tasks 52) is let go when the 0.75 s blend ends, at once without one; posture 0 (65) stands
  through the clip. Its end clears `+0x278` (getting up) and sets the posture (vfunc 0x370).
- Also run when a crawler's leg comes back up (posture 2 to 0, `FUN_1405c7e20`; **Observed**): no ragdoll, so no blend, and the character stands through `standing up 3` (clip past 0.86). Meitou: `CombatSystem.Act` holds it
  (`GetUpClipSeconds`) and `AnimationSystem` plays the clip (`CharacterAnimation.WasCrawling`); posture 1 to 0 (Stand Up) does the same since the setter queues 65 for every return to posture 0 (**Verified**, `FUN_1405c7e20`, `FUN_14033ca10` plays the clip whatever the posture) Checked offscreen in `--sandbox`: leg 80 below the KO point crawls the next tick, a heal 2 s later plays `standing up 3`.
- Events 23 / 24 / 25 fire from this start only when the posture flag is clear (**Verified**, `FUN_14033ca10`): a wake into posture 1 or 3 fires nothing, the later stand-up (posture 1 to 0) and a crawler's leg coming back (2 to 0) do. Meitou: `DialogueSystem.SeeGettingUp` remembers a character that is not standing (out cold, staying low, crawling) and fires when it stands.
- The start (when it found a clip) copies the character's position into the Hold point (CharStats `+0x130`, **Verified** `FUN_14033ca10`).
- Meitou (`CombatSystem.Act`): a waker staying low or crawling is held `RagdollSystem.GetUpBlend` after a whole-body ragdoll, else not at all,
  and plays no standing-up clip (`AnimationSystem`); the Hold point is moved where it wakes while Hold is on.
- Meitou hold (`CombatSystem.Act`, measured headless): a posture-0 waker is held `GetUpClipSeconds` (clip length x 0.86 / play speed; `standing up 3` is 8.67 s at speed 1, so 7.45 s)
  plus `GetUpBlend` only when it wakes out of a whole-body ragdoll (the same test that starts the clip at -0.75 s in `AnimationSystem`), so the hold ends on the tick the played clip passes 0.86.
  The earlier constant added the blend with no ragdoll, 23 ticks past the clip. The posture flag `+0x78` is no animation event: `FUN_14033ca10` sets it from the posture (> 0) at the start, so a posture-0 waker is released only by the clip. **Observed** in `--sandbox` (a real ragdoll KO of the hero, the full load order, whose `standing up 3` plays at about 4x): the earlier constant held 246 ticks while the clip ended at 65, a 181-tick gap; now the hold is 78 ticks and a move order at wake+150 takes effect within 5 ticks. One carried or waking in a bed plays no clip (engine choice) and is not held. **Unknown**: whether the original
  runs Task_GetUp for a waker in a bed or carried (the start returns at once when a state at `+0x2f8` is set, meaning not decoded).

## Saves

No ragdoll key or pose is saved. After loading, the medical tick and the dead-body tick re-request
WHOLE from the loaded animated pose.

Meitou adds GAMESTATE_CHARACTER string `meitou ragdoll` for a whole-body (not carried) ragdoll: `frozen age n`, then per body (definition order)
position x y z and rotation x y z w, invariant floats; absent otherwise (a world without ragdolls keeps the loaded key); the original ignores it. **Observed** (headless, real data, a knocked-out
human frozen after 61 ticks): save → load gives the same 13 body poses (offset 0), frozen flag and age; without the key it falls again from the clip.

## Unknowns

- Meaning of groups 1, 2, 7, 28–31 and the terrain's group (0: the click hulls, [combat](combat.md#line-of-fire-and-the-hit-shape)).
- Exact condition of character slot 103 (group 4) and the downward-ray branch of `FUN_1407d2440`.
- Which body is the root (RagdollAnimation `+0x48` key); likely the pelvis.
- `FUN_1405ce9c0`'s branch for Character `+0x388` 1 / 0x5b (calls `FUN_1405b4fa0` on put-down).
- Purpose of the linked Havok libraries.
- Which LOCATIONAL_DAMAGE parts set HEAD in `collapse part` in the shipped data.
