# Simulation

Engine contracts for `Meitou.Simulation`; game facts live in [game/](README.md#gameplay-research).
Source authority: `World*.cs`, `StandardSystems.cs` and each subsystem. Implementation choices
below do not claim original-game parity. Retain Verified/Observed/Unknown scopes in linked research.

## Time model

Control: 30 Hz real time, input/UI/camera continue while paused. The interface's own commands (selection, order toggles, run speed, job lists,
crafting/research queues, squads, inventory, shops, building settings, the Stop order and every order, [order queue](#order-queue)) apply between ticks while paused
(`World.ApplyPausedInterfaceCommands`); orders only set tasks, nobody moves or fights until the next tick. Simulation: 1/30 s game time;
accumulate `min(realDt, 0.05)*speed` (the original's cap before the speed multiplier, slows below 20 fps, [game loop](game/game-loop.md#frame-clocks), **Verified**
`FUN_14082ac70`), speeds 0/1/2/5 (the 2 and 5 are Advanced settings, 1.5-4x and 5-20x in half steps). A safety budget of `ceil(2*speed)` ticks per frame stays above that cap (engine choice; a larger one let a heavy world snowball); excess is dropped and
`AchievedSpeed` reports it. Control ticks precede simulation ticks; a speed change applies
to the entire frame. `WorldSession` rejects unequal world/clock tick lengths, owns snapshots,
clocks, input and camera, and disposes systems/services. Of a frame's ticks only the last two publish
a snapshot (`RunTick(publish, views)`), the pair the host interpolates between; the earlier of them only its motion ([snapshots](#snapshots)). Advanced → Pause after loading (`PauseOnLoad`, a Faithful / Meitou switch, Meitou and on by default; Faithful runs a loaded save at once as the game does, [game loop](game/game-loop.md)) pauses the speed after a menu load or import; `--load` runs. Advanced → Pause while loading (`LoadingPause`, a Faithful / Meitou switch, Meitou and off by default) holds the world without touching the speed while the zones round the camera load, as the game does ([loading pause](game/game-loop.md#loading-pause)).

### Engine choices against the original's frame loop

**Verified** original behaviour (decompile), each a deliberate difference in Meitou; none changes code.
- **Character update budget** (`FUN_140786e30`, [game loop](game/game-loop.md)): the logic tick (slot 29, `FUN_1405ccd90`) runs for 8 characters a frame round robin; non-priority characters get the full update (slot 28, `FUN_1405d00b0`) for 6 a frame with the time accumulated since their last one; the rest only add the frame time to `+0xc8` while the in-view flag `+0x1a9` is clear (`FUN_1405c9f60`:151-152). Meitou steps every character every tick (timing granularity only). Animal growth (`FUN_1405cdf00`) is lumpy there, the total identical.
- **Medical clock** (`FUN_1405c7ae0`, `FUN_1405cfd90`, `FUN_1405c7d40`, `FUN_140652000`): not an engine choice, Meitou runs the original's clock: parts, blood, clotting and the KO timer get the character time its update budget adds up (`MedicalTimeBudget`, [character stats](game/character-stats.md#time)); hunger keeps game time.
- **Paused growth** (`FUN_140788a00` -> `FUN_140787230` -> `FUN_140786a40`): the character clock (dt x speed) is replaced by 0.01 when not above 0 and is not zeroed by pause; per paused frame one flagged character (`+0x1a9` or carried `+0x3d4`) still gets vfuncs 0xe0, 0xe8, 0x268, so an animal ages 0.01 game-s per rendered frame while paused. Meitou: nothing runs while paused (the host's frames do not reach the simulation; the difference is under a game minute of growth per real minute).
- **Zone linger timers** (`FUN_140a0b830`): the three timers (`+0x18`, `+0xc4`, `+0xc8`; defaults 4, 6, 5 s) count down with the real, capped, smoothed frame dt (`DAT_142133790`), independent of game speed. Meitou matches while running: `PopulationSystem.LingerSpeed` scales the lingers by the game speed. While paused the world runs no ticks, so its zones stay (the original's timers would run out, unloading zones nobody moves toward).
- **Birds**: a `BirdManager` thread of its own, kicked every frame with the real dt; Meitou flies flocks on the display's real time, outside the simulation: [birds](game/birds.md).

### Speed steps

**Engine choice.** `World.SpeedSteps` is 1 at 1x and headless, 2 at 2x, 5 at 5x (from `World.PhysicsSpeed`, set by the host before each tick). The staggered looks
take their period through `World.Every`, so each character looks as often per real second at every speed: Aggro's scan, the AI's think and package cadence,
TownDuty, Captive, Kidnap, Crime, FirstAid's auto look and the carry system's rescue, arm and scavenge. A period that also times something (Aggro's sightings,
the AI's away time) uses the stretched one. The body tick (`BodySystem.Act`) steps a character on its slot's turn with the ticks since its last step, unless
it is a player character, in combat, incapacitated or bleeding (those step every tick). At 5x reactions come later in game time, as in the original, whose
one update a frame covers frame time x speed ([game loop](game/game-loop.md#frame-clocks)). 1x and the goldens are unchanged.

### Snapshots

`World.Publish` reuses its `CharacterSnapshot` objects (**engine choice**): one per slot in each of four sets, filled in place (every property, every
publish) into a set none of `Snapshot`, `PreviousSnapshot` and the one before them uses, so the shown pair stays untouched while ticks run ahead and publish twice. A
paused publish and loading keep `PreviousSnapshot`'s set out of the ring with it. Contract: a published snapshot's characters stay as they are until
two more snapshots are published; hold them across frames, not across ticks (the host's per-id index and the HUD's selection are rebuilt when the
snapshot object changes). `WorldSnapshot` itself is new on each publish.
A frame's second-to-last tick publishes a motion snapshot (**engine choice**, `World.MotionOf`): its characters and projectiles are the tick's, every
other view the last snapshot's, as the host reads nothing else of `PreviousSnapshot` (its per-id index, the turrets' angles, the bolts, the splashes'
tick). **Observed** (play bench, `bench-bases-2026-10-27`, ~270-290 characters, two interleaved pairs, 2026-11-19; with the 64-slot chunks of
[threading](#threading)): the publish 3.7-4.3 -> 1.9-2.1 ms a frame in speed-x5 and paths, 2.0-2.4 -> 1.2-1.4 ms a tick at 1x (its characters
1.1-1.5 -> 0.45-0.6).

### Tick ahead

The game can run the next ticks on their own thread (`WorldSession.BeginTickAhead`, thread `Meitou.Tick`) during the world draw (`WorldFrame.Draw`, after
`DrawWorld`'s host prefix), and joins them right after (`EndTickAhead`), so the frame costs about the slower of the two instead of their sum. A batch runs as many ticks as a frame has owed
lately (smoothed, rounded down after adding 0.1; at least 1, at most the frame budget), so at 2x and 5x a frame's ticks overlap the draw instead of adding to it; only its
last two publish. The ticks are ahead of the clock: they count as the next ones owed (`AdvanceSimulation` takes them, then runs the rest), and the game
clock moves as each falls due. No new batch starts until the last is due; until then `CurrentSnapshot`/`PreviousSnapshot` hold the pair from before them, so the picture is where it was; `ShownTick` is the world tick on
screen. Between `BeginTickAhead` and `EndTickAhead` nothing may touch the `World`: the renderer's host callbacks (`Source`, `PoseClock`) read only
the session's snapshots, and the world reads the renderer only through the weather source (a reference and a float per region). Consequences
(**engine choice**): a command lands a batch later (one tick, 33 ms, at 1x; about a frame's ticks at 5x); blood marks and sounds come out with the tick, up to a batch before it
shows; a pause with ticks ahead takes them at once (the picture jumps, then the paused interface's republished snapshots show); a save takes them first
(`SettleAhead`); `ReplaceWorld` drops them. Display → World tick alongside drawing (`TickAhead`): Faithful never (the world, then the draw); Meitou, the default, once
a tick takes 4 ms (`WorldSession.TickMs`, smoothed over about ten ticks) until it is under 2 ms (a tick ahead is slower, sharing the cores); Always
every frame. Off whatever the setting in stills, the duel, recorded flights and the play benchmark (their ticks stay in step with the frame).
F12's `tick ahead` row is its wall time; `outside ticks` leaves it out.

The game speed is an input of one system: the ragdoll physics steps in real time (`World.PhysicsSpeed`, set by `WorldSession` before each tick, 1 without a clock; not hashed, not saved;
[ragdolls](game/ragdolls.md)), so a ragdoll's outcome, and the position of a body that follows it, differs between speeds, as in the original. Runs at one speed, headless runs and the goldens are unaffected.

**Verified** original clock: 1200/11 seconds per game hour ([game-loop.md](game/game-loop.md)).
`BodySystem.HoursPerTick=11/36000`; the medical tick takes `TickSeconds` plus the update budget's turns (`MedicalTimeBudget`, [character-stats](game/character-stats.md#time)). Population and assets split: **simulation = original**: a town site unloads with its zones (`TownUnloadGraceSeconds` = 0; the zones lapse 4-6 s after the camera / player timers, [character-stats](game/character-stats.md#time)), and its squads become pinned platoons; wild sites and roaming squads keep an engine-choice grace on game time (60 s, 10 s). **Asset cache = Meitou engine choice**: a left zone's navmesh stays loaded 120 s (real time, at most 60 zones, oldest dropped first; then the disk cache), so a return does not rebuild; render assets follow the camera, not the sim zones. **Observed** (save mec-tou-nu2, Release): a town's unload costs ~0.02 ms, a second spawn of a town 0.06-12 ms, the first spawn of a town with 8 squads 65 ms (JIT included).
`RequestedPause` is independent of remembered nonzero speed. Render previous/current states
with simulation alpha; camera uses control alpha. Positions are float world coordinates;
rendering is camera-relative.

### Far cadence

**Engine choice** (`WorldSettings.FarTickPeriod`, the game 4; 1 in tests, goldens and headless worlds without a `World.ViewPosition`, where nothing changes).
Each tick, after the grid (serial, from the last tick's positions), every character is near or far: near when within `World.NearRadius` of the camera eye (8000 without a host; the game's host sets
the character renderer's draw distance when the session is made plus its reach margin of 2000, so every drawn character steps every tick: the NPC View
Range of the install's `settings.cfg`, 3250 without one. `WorldSession.ReplaceWorld` carries it to the world swapped in, a loaded save or a new game), a player character the player leads, or
within `World.PlayerNearRadius` (1500) of one. A player squad the player does not lead (`Squad.Independent`) or a code mod put to sleep (`Squad.Dormant`)
counts as NPCs do; a sleeping one also gets no far step, so away from the camera and the led characters it stands still, and it asks for no zones round it. A far character steps once in `FarTickPeriod` ticks, on its slot's turn (`(Tick + slot) % period`), and its step covers the
ticks since its last (`World.StepTicks`, at most the period), so one that crosses the boundary loses no time. `World.Steps(slot)` is honoured by the parallel
per-character loops (AI think, movement think and move, combat body step, act and approach, first aid, body, animation), the ragdoll setup loop (a moving ragdoll steps
every tick: its pins follow the animation), the sight map's place probes and the decisions pass; their `dt` for a far character is `World.StepSeconds` where it
integrates time (movement, animation, the ranged timers, the body's medical step), while combat's tick timers (ready, stun, get-up ticks) stay absolute ticks, so a
far fighter acts on them at its next step. The serial per-character passes honour it too (the carry act-outs, the aggro chase bookkeeping, the pursuit schedule).
A sweep with its own cadence tests `World.Turn(slot, every)` (the slot's turn fell within the ticks its step covers), not `slot % every == phase`: a cadence that
shares a factor with the period starves slots (30 and 15 against 4: half the far slots never coincide), and the turn rule keeps a far character's sweep rate, so the
spread sweeps (AI think, aggro look, first aid, crime, captive, town duty, kidnap, scavenge) cost the same, on the step tick. A tick's publish gives a character the
cadence skipped the last publish's snapshot object again (`WorldPublish.Fresh`: a buffer fills only an object no other buffer of the ring shows, as the render
thread may be reading it; a paused or loading publish fills everyone). Contract: `NearRadius` must cover the renderer's reach, else a drawn character moves at
7.5 Hz; the Metal host sets the period but leaves the 8000 default. Known edge: a near fighter against a far one has an
opponent that reacts up to the period late. **Observed** (headless flight probe, ~1,150 loaded, 470-590 skipped a tick): the publish 0.8-1.0 -> 0.6-0.7 ms a
tick; the spread sweeps unchanged (their cost is per tick: town duty's scan of every slot, first aid's treated list); the ragdoll system is 0.6-2.9 ms of the
slow world's 1.5-4.9 (its physics step 0.4-0.5 ms with one moving ragdoll, its setup 50-70 us a moving whole body). Numbers: [bench](bench.md) "character flights".

## Threading

Tick order: Inputs → Schedule → Think → Move → Act → ordered Apply → SlowWorld → Publish.
Inputs/Schedule/Apply/SlowWorld are serial; each implemented parallel phase has a system barrier. Publish makes the character snapshots on the
workers in 64-slot chunks, so a few hundred characters still spread over them (each reads only the tick's state), listed in slot order.
Engine choice, to keep the publish small with ~1,400 characters: the context menu's figures (kidnap and stealth KO chances, shackles) and the AI goal text
go only to the selected, inspected and pointed character (`PlayerState.Pointed`, set by the host as the mouse moves; while paused, a move onto another
character publishes again) and the player's (goal text);
`CanStealthKo` tells the quick order and highlight for everyone. A body's part figures (aggregates, worst part, blood shown) are kept until a part's
`HealthPart.Version` moves. The research view refreshes at once when the finished, queued or paid lists or the bench level change, else at most once a
second (progress, bench stock, the focus zone). Cages and shown doors are new lists only when one changed.
A system's `BeforeAct` runs right before its own `Act`, with a barrier between. Combat's body step runs there: the medical tick (`TickMedical`), the arms, getting up,
and the fight's end on going down. Every fighter's body step therefore finishes before any fighter decides. Otherwise a decision that reads its target's knock-out
or fight would depend on which partition ran first. A decision reads another fighter's fight as that fighter's body step left it, because a decision's own fight end
clears the live flag.
`CharacterHot.Previous` is read-only; a partition writes only its own `Next` slots. Spawn/remove
only in serial phases after consumers finish; generation IDs prevent stale slot references.
Combat has separate double buffers swapped at the end of its SlowWorld, not at global publish. Its checks round a target (who swings at
it, who yields, attack slots, joining, fighters at it) walk `AttackerIndex`, the per-target attacker lists rebuilt in Pursuit's and Combat's
Schedule, filtered to the grid cells a search would cover, in the grid's order. Ragdoll physics steps on `Threads` workers (Bepu deterministic mode).

Effects sort by `(target,source,system,sequence)`; spatial-neighbour iteration order is fixed.
Commands apply by tick and queue order. Simplification: the original drains a character's command deque from its head each frame; the head is accepted or refused by `FUN_1405d0db0` and a refusal blocks everything behind it and is retried every frame (`FUN_1405d2c30`:15-41, **Verified**), while Meitou's `CommandQueue.TakeDue` removes every due command and a consumer that does not apply one drops it. In-tick orders use serial `World.Defer`; deferred orders
precede host orders for their due tick. No concurrent host enqueue may alter that ordering.
Randomness: stateless `Rng.Hash(seed,entityKey,purpose,counter)`; changing purpose/salt changes rolls.
Worker partitions are `threads*2`, minimum 16 slots (game 32: 128 left a 500-character battle 3 partitions); one worker means caller execution.
Async path and population results enter at tick boundaries and a navmesh zone goes live when its build ends, also in the middle of a tick; arrival
timing is not repeatable in play. What play reads that no tick decides (**Observed**, code reading, #1056):
- Services on their own threads: `PopulationSettings.Background` (town, wild and roamer builds), `PathService` without `SynchronousPaths`,
  `RagdollGeometry` without `Synchronous` (zone gathers; `ZoneRead` gates a zone's loading), `NavMeshService` (zone builds).
- The host's frame loop (`GameHost.UpdateNav`): it asks for the navmesh of `PopulationSystem.ActiveZones` and `NavmeshWalkability.RouteZones`
  (aged 30 real seconds), drops zones unwanted for 120 real seconds, and sends `RepathCommand` at most once a real second. The population's
  `ZoneGate` (the zone's mesh is live) therefore follows frame and build timing; it is read by the zone loading (`PopulationSystem.Accepted`),
  the town and wild loads and the roamers' `Ready`.
- Per-player inputs: `World.ViewPosition` (the camera eye) and `World.NearRadius` decide who the [far cadence](#far-cadence) steps, so two
  machines with another camera or NPC View Range step other characters from the first tick.

What a surface times, it times in world ticks: the world tells its walkability each tick's number and length (`IWalkability.TickStarts`), and the
navmesh forgets a goal found unreachable after `UnreachableSeconds` (10) of game time, so with synchronous paths the same tick gets the same route.

Determinism tests use synchronous services; the headless probes' building collision too
(`RagdollGeometry.Synchronous`): with zones gathered on a worker, whether a zone was in by a tick depended on the machine's load, and a sight ray it
blocks changed the look (**Observed**, #803's save, 300 + 1500 ticks: 2 runs in 48 under load ended on the hash every synchronous run gives). `PathService` runs 2-4 threads (a quarter of the cores) and drops a
request its character replaced before a thread took it up; F12's sim rows show the requests waiting.

`StateHash` is ordered, includes slot/generation/free-list and hashed subsystem state, normalizes
negative zero, and is a diagnostic rather than gameplay work. Golden scenarios must agree at
1/4/16 threads before pins change; pins are platform-specific. `GoldenHashTests.Scripted_scenarios_hash_as_pinned` emits the measured arrays on Windows; take pins from there. Linux runs reproduce the `production` and `build` pins bit for bit but not the wandering ones, and Wine/Proton reproduces neither reliably, so a pin taken from Linux needs one Windows CI run to confirm. The production fixture uses town 1 so its generator, battery and powered workshop share a grid. Snapshots expose living-character
identity, appearance, transform, animation, faction/squad/player/selection and body status;
selected characters also expose skills, inventory and path. Host reads double-buffered active
zones within the frame after ticks. Null appearances draw markers.

## World model

Dependencies: Core ← Data ← Simulation ← Engine ← Game; Navigation/Rendering are sibling
libraries. Characters use hot double buffers plus cold objects; squads/platoons use sorted
registries. `(slot,generation)` IDs and the free list participate in hashes. Static typed data
views are built over `GameRecord` once; missing fields use documented defaults. Population shares
factories/database/relations and lazy body/combat/item assembly. Research owns original formulas.

### Shared predicates

`CharacterFacts` holds one member per original predicate, named and cited after it; merge by original function, not by English word. **Verified** (by reading each copy
against the decompile): `Down` (`Character` slot 6, `FUN_1405c9e10`: out cold, dead, playing dead, carried; a whole ragdoll only ever exists for an incapacitated or carried character, so
the ragdoll test adds nothing) is one definition for the carry, dialogue, squad-intact, combat-mode and snapshot reads; `TargetDown` (state 7, `FUN_14099a5a0`) and the capture
finders' KO / crawling tags (`CaptiveSystem.Down`) are different originals and stay apart. `Perceives` stands in for "on the sensed list" (`FUN_140853960`) for finders that walk it
(the medic's, the capture and carry finders): an entry, else the sight test for one the capped block lacks (engine). `GettingUp` is `+0x278`. `RegardsEnemy` is the faction part only (engine). The UI reads
`CharacterSnapshot.Down`, `CombatMode`, `CanOpenInventory`, `Conscious` (Set Free's precondition 125, `FUN_14099a480`, **Verified**; also `LockSystem`'s), `Standing` (not dead or out cold), `Animal` (`SquadRegistry.IsAnimal`, the animal repertoire) and `HostileToPlayer` (the player faction's `Relations.IsHostile`, or fighting a player character; engine reading), computed once in `CharacterFacts` and published; it never re-derives them, so a replica client without systems gets them from the snapshot. The menu's own offer rules (`FUN_140796f80`, `FUN_1407a6bc0`, [ui-input](game/ui-input.md)) stay in the UI. Left as engine stand-ins, not merged: `CarrySystem.Friendly` and `CrimeSystem.Friendly` (faction-level
"regards as friendly": the full slot 126 also reads squad memory and DC 52, `DialogueSystem.RegardsFriendly`), `IsEnemy` (looker-free), the fight flags `Fighting.cs` / `CombatSystem.Threats` computing
the immediate-threat reach inline (Unknown whether they match `FUN_1404378f0`'s gate test).

### Task eligibility

`TaskStart.Eligible(world, cold, row)` is the one start-time test of the original's repertoire (`FUN_14060e840`, docs/game/ai-tasks.md "Planner"); a type with no TaskData row is allowed.
Meitou's `CharacterTask` is the act-out, the row is the `TaskType`: a pick-up is 225 for the order (246 kidnap), 148 for the rescue job, 238 foraging, 137 errands; operate 87 (97 training
dummy), haul 124, turret 146, prospect 150, build 2, door repair 95, dismantle 96, bed 98 / put in bed 99 / job 106, move 29, melee 5 (picked) or 16, a perma-job its own row, the implicit
jobs 242 / 153 / 275, pick lock 76, shackles 201 / 185 / 186 (putting shackles on has no row), set free 110, get in cage 107, open / close door 72 / 73, lock / unlock door 77 / 140, cut / break
lock 285 / 286, smash 226, item pick-up 3, container work 26 on a site (a walk only to it 29), put in cage 108, a warden's round 110 / 77 / 78. Used by `CarrySystem.CanCarry` (replaces its animal and imprisoned tests), every
start through `TaskStart.Start`, the lock orders' worker choice and the player's job `Runnable`. Consequences (the original's rows):
a crawler or one staying low (0x40, posture 1 or 2) can start only rows that register it (move, first aid, loot, talk, attacks, prospect, sleep; not carry, put down 69, stealth KO 228, Splint
Rigging 250, operate, build, repair, dismantle, put in bed, sit 275, ditch 242, set free 110, unlock door 140, put in cage 108, smash 226); an animal only 0x20 rows (so no bed, no carry,
no loot 26 / 177 / 42, no first aid, no operate, no medic, no lock, cage or door work but smashing 226; it may pick up items, 3 (Meitou: a player's animal only with the Advanced setting
"Animals pick up items", whose Faithful side is on and Meitou side (the default) off, `PlayerSystem.AnimalsPickUp`, and only into its bag, its grid being shut; a limb for its mouth always); job 289 is animal-only, so a human with that job never runs it).
Loot is 26 for the orders, 177 for NPC scavenging and 42 for an NPC arming itself. The engine's starts test their rows too ("Task starts"): a refused errand walk counts
as a walk that ended short (the errand gives up after its tries); a refused standing task (`TaskStart.Become`) gives way to 14 Idleness, which every repertoire registers (a crippled
character does not wander, 24, after a flight, a trade or at a roaming mill). Shoo 158, ransack 216 and liberate 276 have TaskData rows and are tested.
The goal pick tests the goal's own row too (`PlanContext.TryPlan`, after the engine-run goals): a root row the repertoire lacks plans nothing, as the original's planner
(docs/game/ai-tasks.md "Leaving the night cage"), so a caravan's pack animals under its bar package never relax (214, 0x1). The seat claims (`TownPlaceClaims`) skip a
member that cannot use the seat: relaxing needs 214, staying home and sitting around 221 (human only), so animals and crawlers take no chair (#968).
The rows of the dialogue's walks are the original's (**Verified**, the dialogue action switch `FUN_140680560`): action 2 orders 102 (`Task_TalktoPlayer`), so the
approach is 102; action 30 makes the target drop the carried person and orders the speaker 213 and 112, action 29 240 and 112, so the arrest's takeover lift is 213 and
its cage walk 112. 126, 205 and 206 have no TaskData, and nothing starts them. The AI's own starts (`TaskStart.StartOwn`) are not tested: its planner chose the row. Not yet routed through it: the trade and talk orders. A caging ends the task through
`TaskStart.End(Stopped)` (`FixtureRegistry.Imprison`), so production's and construction's `Able` and the beds' `CanWalk` test no cage: their rows do not register the caged repertoires. The medic finder keeps its own imprisoned refusal because rows 25 / 58 register the imprisoned repertoire.
A fight is row 5 when the player picked the target, 13 for a defence, else 16 (13 and 16 register the same repertoires).

### Task starts

`TaskStart.Start(world, slot, TaskRequest)` (serial phases) is the one start path. In order: the row must be `Eligible`; a goal's or reaction's start (`TaskSource.Goal`,
`Reaction`) that is no step of a follow row is refused while an NPC's order holds the character (`StartResult.Ordered`, `DecisionSystem.Ordered`, [order queue](#order-queue);
`TaskStart.Walk` refuses it before its destination test, so the order's walk is not stood); a fight ends (the original's new task drops the fight task,
[combat](game/combat.md#fight-lifecycle-and-orders)); the task before ends through its handler (`TaskEnd.Replaced`: it lets go of its target, while the movement, the task and
the task it goes back to are the new start's); the provenance is recorded in `CharacterCold.Running` (`RunningTask`: act-out, row, `TaskSource`, job index or implicit flag,
target, site, tick, resume task); the act-out starts.
`TaskStart.End(world, slot, reason)` ends a task and drops its provenance: `TaskEnd.Stopped` from outside the act-out (the Stop order, a squad mate lifted awake, a leg giving out and the dialogue's DA 6, 7, 34, 55 and 57 run the stop `BodySystem.Stop`, `FUN_1405c8330`, which keeps the order queue, its front waiting to start again (recruiting and dismissal do the same, their duties let go), [ragdolls](game/ragdolls.md#forced-crawl); then idle);
`TaskEnd.Done` the act-out's own end (a treatment, carry, site's or machine's work, haul trip, prospecting, a job's follow: the handler lets go, then the character takes up
`RunningTask.Resume`, engine; one the [decision step](#decision-step) decides stands idle; every end also sets the replan bit). Walks (bed, item work) end by standing; their provenance then no longer runs the task. A lock order's worker (lock, shackle, cage, door, force work)
stands at its work and its provenance runs `Idle` from then (`LockSystem.AtWork`), so the next start ends the work through the lock handler (a move order given at a cage walks away). Each `CharacterTask` has one
`ITaskHandler` (Start, End, Done), registered by its system (`ITaskOwner`) in `World.Tasks`, an open list later code may add to or replace; a task without one is set as is. Owners: movement
(GoTo, Follow, Idle, Wander), combat (Attack), first aid (Treat, Rest), carry (PickUp, PutDown, Loot, StealthKo), production (Operate, Haul, Prospect), construction (Build, Dismantle,
DeliverMaterials, FetchMaterials), beds (Sleep). A row may have its own handler, which takes that row's starts and ends whatever its act-out (the task before is ended through its running row's
handler): the lock system's rows (lock, shackle, cage, door and round work), the beds' put-in rows 99 / 106, the player system's rows 3 (item pick-up) and 26 on a site (container work), the arrest system's
put in cage 108 and combat's body orders 65, 52, 147 and 116 ([order queue](#order-queue)), all acted out by a walk (`GoTo`) or in place (`Idle`). `TaskRequest.Detail` carries an act-out's own parameters (a shackle job, a ground item, a round). A builder's
delivering, fetching and dismantling are steps of one act-out (`TaskStart.ActOut`): `RunningTask.Runs` and the debug check treat them as Build.
- Routed through it: move, follow and bodyguard (order and job), stop, every fight (`AttackOrder`: picked is an order, `Defensive` a reaction, else a goal), First Aid, Splint, the Medic
  button, medic, robotics and splint jobs, NPC healing, Rest, pick up, kidnap, loot, open inventory, stealth KO and put down orders, the lifts and drops of errands and town duties, rescue
  (job, button, NPC leaders), kidnappers' and guards' lifts (`BeginPickUp`), foraging, NPC scavenging and arming, the operate, turret, haul and prospect orders, the production list jobs
  (87, 97, 124, 152, 234, the engineer 125) and implicit jobs (242, 153, 275), the build, repair, door repair and dismantle orders, the bed orders and job 106, the lock, shackle, cage,
  door and force orders, item pick-up and container walks, put in cage; the engine's starts: crime fights and the arrest mates' fights
  (`AttackOrder.Source`: `Npc`; an NPC's attack on sight is its AI's goal fight, `Goal`), kidnappers' lifts (68) and 168's lifts (170), captive jobs'
  walks (the row of their task, `CaptiveSystem.RowOf`), the ditch's and the player side's food runs' walks (`TownDutySystem.RowOf`), the [engine goals'](#engine-goals) act-outs on their goal's row (smell hunts 176, NPC looting and arming, the wardens' goals, arrests 204, escapes, town goals), flight (35, then wander 24), food on the ground
  (259) and limbs (277), NPC arming walks (42), roaming leaders' walks (53) and mills (24); the engine's orders' act-outs start with `TaskSource.Order` ([order queue](#order-queue)). Engine walks go through `TaskStart.Walk` (`TaskRequest.Goal`: as given, or
  `Point` onto the floor, at `Mode`; one to a building shut to the walker is refused, `StartResult.Inaccessible`, as the original's plan walk:
  [locks](game/locks.md#doors)); engine standing tasks (idle, wander, a follow of the squad's leader) through `TaskStart.Become` (the movement's handler stops the character on it).
  Sources: a package goal's act-out (town duties, captive jobs, kidnap, smell, food, population) `Goal`; an engine rule (flight, crime) `Npc`.
- Not serial: the AI's think (parallel, its own slot) starts its goals' walks and standing tasks with `TaskStart.StartOwn`: the provenance (row: the goal's, source `Goal`) and the
  walk or stand are written on the slot only; no fight or task before it is ended and no handler runs (the AI acts out nothing for a fighting character, and its tasks hold nothing). Its repertoire
  is not tested (the AI's planner chose the row). It does nothing while the character's order holds it. Same timing as before at any thread count.
- Engine starts of `TaskSource.Npc` are not refused: those that are no order in the original test `DecisionSystem.Ordered` themselves ([order queue](#order-queue)).
- An act-out's own system moves its character on with `TaskStart.Step` (another task of the act-out, or back to a standing task), `TaskStart.StepTo` (its next walk) and
  `TaskStart.Stand` (stop, idle): any phase, its own slot, provenance kept (it then no longer runs the task). These are the walks' arrivals and give-ups, a failed or blocked path, a
  follow whose target or leader is gone, a builder's steps, a bed reached, a fight's end (`CombatSystem.EndFight`: idle, a follow row's provenance kept for `FollowResume`), an NPC's
  stand-down back to its routine (`AggroSystem`), a knock-out (`BodySystem`), a caged character held
  in place (`FixtureRegistry`).
- Engine starts end the fight and the task before as any start does (a flight's run stops the fight at once, a dialogue act's walk ends it). An errand's own walks would end a fight
  its character is in, so they wait for it: a ditch steps nothing while its member fights (a player character's food run ends instead; engine choice, the
  original's priority-3 reactions beat these goals).
- Provenance is the only job mark: `PermaJobs.ListJob` is the production pick's list job (its index) or implicit job it runs (not a follow row's fight step), which the implicit jobs, an NPC's rescue rank test and the circle
  break read; a list edit moves any job provenance's index (one whose job left the list keeps running with index -1). The task an act-out goes back to is `RunningTask.Resume`, for the
  characters the decision step does not decide: following or wandering when it did that, else idle (`TaskStart.Standing`); a medic or builder
  already at it keeps its own (`TaskStart.ResumeOf`).
- Engine: a fight ended in a serial phase clears both combat state arrays (`CombatSystem.Live` tells which one is current: the slow-world swap changes it).
- Debug builds check at each tick's end that every task change went through the start path (`TaskStart.CheckStarts`): `Start`, `End`, `StartOwn`, `Step`, `StepTo` and `Stand`
  stamp the task they set (`CharacterCold.TaskSet`, by `CharacterTable.Ticks`); a builder's switch between the steps of its act-out passes too. Exempt: a handler's own `Start` body
  (stamped once it returns), and a character spawned or loaded this tick (`PopulationSystem`, `PopulationStored`, `SavedPopulation` and `FixtureRegistry.Bind` set the first task of a
  new slot, which the check skips).
- **A fight under another task** (Meitou only; **Verified** by reading `CombatSystem.Act`, `PursuitSystem`): a start that overwrites a fighter's task without ending its fight (no start
  does any more) leaves `InCombat` and the fight target set. `CombatSystem.Act` does not read the task: the character keeps its target and
  re-picks, blocks and dodges, draws, and swings whenever a technique reaches the target; `PursuitSystem` and `CombatSystem.Move` steer only an `Attack` task, so the new task's walk
  moves it. The fight ends only when `Act` loses its task target or finds nothing left ([combat](game/combat.md#leaving-a-fight)), and `EndFight` then leaves the other task alone.

### Order queue

Each character's `CharacterCold.OrderQueue` (`OrderQueue`, the original's `+0x40` deque; [precedence](game/ai-tasks.md#precedence)) holds its orders as `OrderEntry` (row,
owning `IOrderOwner`, target, site, point, owner detail, and `TaskPoint`: the point the original's order goal is made at, a ground move's or a
building's on a quick right click, `OrderCommand.Clicked`, [orders' points](game/ai-tasks.md#slave-work-shout-and-capture)): the front is the order in hand.
Every order command (`OrderCommand`) goes through `PlayerOrders.Give`: without `Queued`
(Shift) the queue is cleared first, as the original's order giver `FUN_1405d20d0`; the order is queued; at the front it starts through its owner when `PlayerOrders.CanRun` says so (the row's repertoire, then
the owner's pure `IOrderOwner.CanRun`: `Run`, `Blocked` with its failure text, or `Gone` when its target or site no longer fits the verb; then the plan's
first step, `ListTests.OrderBlocked`: the first list B entry `RowStates` evaluates that fails (the walk left out) with no provider in the character's
repertoire blocks it with that state's line, `StateFailureLines`, so a Follow on a downed character waits silently until it is up; one with a provider is a step
the act-out carries out first, the row's list B tested once it held: `OrderEntry.RowRuns`).
- A blocked front shows its text (a bark, or a posted message) and stays queued; the character's task is left alone, so its jobs, goals and reactions go on (S5). `Gone` pops it.
  An order given to a character out cold waits queued (the chooser does not run while it is carried, out cold or dead, `DecisionSystem.Down`, for the player's
  characters too) and is wiped when it wakes outside a bed or cage (the wake's get-up order has the clear flag, below).
- **The body's orders** (`Combat.BodyOrders`, the original's engine order givers, [order givers](game/ai-tasks.md#order-givers); any character, the player's
  included; owner and row handler for 65, 52, 147 and 116; not saved; they release no duty, `OrderEntry.Interrupts`, and keep an NPC's routine). The combat body
  step (parallel, own slot) notes in `CharacterCold.BodyEvents` what the body did, and `BodyOrders.Pass` (combat's slow world, serial) acts on it:
  going down (knocked out or dead) wipes the queue, a loaded queue not restored yet and an AI's plan and goal, the jobs kept (`PlayerOrders.Wipe`; being picked up
  does the same in `CarrySystem.Link`); waking gives 65 Getting up (an animal, or waking standing) or 52 (waking staying low or crawling) with the clear flag,
  none in a bed or cage and, while carried, only once put down; a return to standing from staying low or crawling (Stand Up, a leg back up; not at a
  kneeling treatment; not in a bed or cage, engine) gives 65 with the clear flag unless one is queued. A flight
  waits while one of these runs (engine: the original's replan after it drops an uncommitted goal) (`PlayerOrders.BodyHolds`) and goes on after it; a ditch and a player-side food run end;
  an [engine goal](#engine-goals) ends when one starts (`AiSystem.ReplanDuty`, as the original's replan; no duty goal commits). A stagger outside a fight (`CombatSystem.Stagger`, not getting up, not at a turret;
  a squadless NPC too, whose ended front leaves the queue without a chooser, `PlayerOrders.DropEnded`, **Verified** slot 27 `FUN_1405c7c30` → `FUN_14050d920`) pushes 147 Stumbling to the front (`PlayerOrders.GiveFront`): the running front waits behind it unstarted and is started again from its
  start afterwards; 147 starts with no repertoire or list test (`OrderEntry.Forced`, `TaskRequest.Forced`); a blow during a running stumble adds another front
  147 that takes the running stumble over, the older one ending at once after it. A failed kidnap of a sleeper gives 116 with the clear flag
  (`CarrySystem.Kidnap`), which leaves the queue at once as the kidnap has got it out of bed. Each acts out standing in place (`Idle` under its row) until the
  body's hold ends: 65 / 52 the get-up's `GettingUpUntil` (set at the wake by the body step), 147 the stumble's `StaggeredUntil` (at once without a clip);
  65 and 52 start nothing in a bed or cage and leave.
- `PlayerOrders.Runs`: the front started and its task's provenance is an order's that runs the task (a move also needs `OrderMove`) and the handler is not `Done`. Lock work and item or
  container work done in place (`Idle`) report `Done` once the character holds no work.
- A front whose task ended leaves the queue (a move's arrival in the character's own partition, `PlayerOrders.Arrived`, which also starts a queued move at once as the move-only
  queue did; `TaskEnd.Done`; else the decision step, `PlayerOrders.Prepare`) and the next is tried at once. An interrupted move drops the queue (as `GoTo`/`Stop` dropped queued moves),
  so does `TaskEnd.Stopped` of a running front (a stop that keeps the queue sets the front back first, `PlayerOrders.Interrupt`); a start from another source replacing the running front pops it (a follow order's fight before its row runs does not). A move whose walk finds no path or is given
  up blocked leaves the queue alone and the next order is tried at the decision step (`PlayerOrders.WalkFailed`: the mover has no path, so state 1 holds).
- After `Give` the decision step ([below](#decision-step)) asks the front again as its source 1: a blocked one falls through to goals and jobs there.
- A done front leaves before it is asked (`PlayerOrders.PopDone`, `ListTests.OrderDone`: its row's list A, effect test and finished test, the original's
  order step, [finished tests](game/ai-tasks.md#finished-tests)), and so does one whose desire is 0 or less (`OrderDesire`: a first aid or splint order with no kit, a lift
  order once the body is carried) and a running one whose list B fails while its own row runs (`ListTests.OrderNeedsFail`; `OrderEntry.RowRuns`: list B held at its start, or since, the plan step's end, [precedence](game/ai-tasks.md#precedence)).
  A follow order (31) running its row ends so when the followed character is attacked (16) or goes down (7): the follower falls through to its goals (15 Protect allies
  joins the fight) and does not follow again (`PlayerOrders.FollowRowRuns`; the guards' fight and the fight-step provenance are only for a follow job, or a follow
  order given while its target is already threatened, whose fight is the step before its row);
  a running one ends its task (`TaskEnd.Done`) when its row is done or cannot run, while a desire of 0 leaves the act-out to its own end. The decision step tests this on every
  tick a character with a front steps (`PlayerOrders.PopsNow`) and decides it then. So a pick order on a lock already open leaves at once. A move leaves within 12 of its point
  and its walk goes on (`Task_Move`'s end stops the mover only once it is done), so the next order or job takes the walk over there.
- `PlayerOrders.Holds` (a pure read): the character holds an order, running, waiting or queued (the original's count `+0x60`; a front whose task ended is not counted). The
  implicit ditch job 242 runs only without one ([perma-jobs](game/ai-tasks.md#perma-jobs)). `PlayerOrders.Wins` (pure): the front runs or can start, so source 1 wins; the
  player animals' race-goal jobs (`CaptiveJob.PlayerSide`) end once one runs or can start (a blocked order does not end them).
- A row needing no melee threat (`PlayerOrders.NeedsNoThreat`: `!158` in list B, of the orders only 146 Turret gunner) is blocked while `CombatSystem.ImmediateThreat` holds; at a
  decision its running order ends (`TaskEnd.Stopped`) and waits unstarted, so the gunner falls through to self-defence and mans the turret again once the threat is gone (S3).
  An owner's `IOrderOwner.StillHolds` does the same for its own start conditions (combat's and first aid's orders). An operate or gunner order (87, 146)
  whose machine loses power ends and leaves the queue by its list B (state 93, `RowStates`), silently, as the original's plan step ends and pops it
  (`FUN_14050d970` → `FUN_14050d7d0`, **Verified** by decompile; a power cut during the walk ends it once the row starts there); given unpowered it waits
  with "It needs electricity.".
  A Loot order (26) walks through a fight; once its window is open the loot ends (`TaskEnd.Done`, closing it) while a threat is within 40 (`Task_Loot_Order`'s update,
  `CarrySystem.HoldWindow`, [carry-loot](game/carry-loot.md)).
- Group orders keep their pickers (the nearest or first character that can run it: pick up, loot, stealth KO, the turret, item pick-up, containers, lock work); only that character
  gets the order, none when nobody can run it. Per-character orders (move, follow, attack, first aid, splint, rest, the Medic and Rescue buttons, put down, operate, haul, prospect,
  build, repair, dismantle, beds, put in cage) queue blocked for whoever cannot run them.
- Not queued (they act at once): Shoot At (sets the turret's target), Get Out, Dispose Corpse (an errand), Talk and trade (their approach is a deferred `MoveOrder`), Stop (clears the queue).
- `World.CanOrder(row, actor, target, site, point)` / `CanOrder(OrderEntry, actor)` -> `(Can, Why)` is `PlayerOrders.CanRun` as a pure read between ticks (a `Gone` verdict is `false` with no text); the queue's start and bark use the same read. It is not a menu filter (decision D5): the original's menu offers its verbs (operate, build, dismantle, prospect, ...) without testing the character and lets the order fail with its text, so Meitou's does too; no verb is greyed. Planned native multiplayer can call it on the host to gate guest commands.
- Every `OrderCommand` also applies while paused (`World.ApplyPausedInterfaceCommands`, through every `IOrderOwner`'s inputs).
- **Any character** holds orders on the same path. An NPC's queue is asked by the [decision step](#decision-step) as source 1 alone while its chooser runs
  (`DecisionSystem.TakesOrders`: it has a squad, is not carried and is neither out cold nor dead; playing dead, posture 3, counts as awake; in a bed or a cage it runs;
  its mind plays no part), as the original's AI update
  runs the task system only with a platoon (`FUN_1405112b0`) and not while carried or down (`FUN_1405d00b0`); `Give` starts its front at once only then, else it waits queued.
  While the front runs (`PlayerOrders.Wins`) `CharacterCold.OrderWins` is set (by the decision step and `Give`, serial; hashed while set, not saved; cleared for a character the
  step decides), and `DecisionSystem.Ordered` (the flag while the queue holds an order) keeps everything else off the character, as the original's order step comes before every
  goal, 32 and committed goals included ([precedence](game/ai-tasks.md#precedence)): the AI (`AiSystem.Holds`, `Preempts`, `TaskStart.StartOwn`); a goal's or reaction's start
  ([task starts](#task-starts): the AI's, retaliation's, the duties' and the victims' deferred fights and walks); retaliation's own defence (it answers as a player character
  does); the flight step, which also ends a running flight; the stand-down after a fight; the free tests of the duty sweeps and routines (the ditch and the player faction's
  food runs, the player animals' race goals, 259, the shut-in release, NPC healing, arming and scavenging, rejoining the leader); the
  population's leader walk and arrival; and two engine reactions that are no order (`TaskSource.Npc`): a crime victim's attack on its culprit and a caught thief's
  watcher getting up from rest. The dialogue's acts ([dialogue](#dialogue)), the body's orders above and the arrest orders are orders of the receiver's queue. An
  owner's order may repeat (`IOrderOwner.Repeats`): when its task ends it waits unstarted at the front for its next step instead of leaving (22, 101, 248), until its
  row's done test, its desire or its owner's check ends it. A blocked front waits, the AI going on with its goals meanwhile, and is tried again on the re-ask (every `ProductionSystem.JobCheckTicks`). Once the
  front no longer runs the mind chooses again at once; when it starts the mind's plan, goal and commitment go. An NPC's queue is not saved, and an NPC keeps a replan bit only
  while it holds an order.
- **Duties let go** when an NPC's front starts, but for the body's orders (engine design; `PlayerOrders.TryFront`, serial, before the owner's `Start`): `World.ReleaseDuties` asks every system that
  is an `IDutyRelease` (found on the world's systems, nothing registered) to let the character go through its own give-up.
  The ditch (its record), the player side's food runs and the mod NPC goals (`NpcGoals`) drop their record and clear `Engaged`; a body carried
  for one is let go where it stands (the unlink, no knock-out); an [engine goal](#engine-goals) ends (`DutyEnd.Order`, through the AI; a town goal lets its carried body go the same way); a door way is dropped and a bash
  errand stopped. Neither the dialogue nor a trade holds anyone. The duty sweeps take no
  ordered character again (`DecisionSystem.Ordered` or `AiSystem.Holds` in their free tests).
- **Routine after an order** (engine design): an NPC its AI does not fully run (a held platoon's engine mind, or none: no squad) keeps the routine it was on at the start, wandering or
  following its leader (`CharacterCold.OrderRoutine`, `DecisionSystem.KeepRoutine`), and takes it up again once its order no longer holds it, if it then stands idle,
  awake and held by nothing (`DecisionSystem.BackToRoutine`; it waits while the order's task goes on, a move's walk after the move left the
  queue). A full mind chooses again instead.
- **Orders of several steps** (`IOrderSteps`, the arrest orders 240, 213, 112): the original plans the front again on every update, so the end of one task of its plan
  is not the order's. While the owner's act-out goes on between two tasks the front still runs (`PlayerOrders.Runs`), and the plan step asks the order row's list B
  only while a task of that row runs (a provider step's, or none between steps, is not asked).
- **Engine order givers** (rows, receivers and clear flags as the original's, [order givers](game/ai-tasks.md#order-givers)): DA 29 (`ArrestSystem.OrderArrest`: 240 then 112
  queued, else 4, which `CombatSystem` owns), DA 10 / 30 / 40 (`ArrestSystem.TakeOver`: 213 then 112 queued); the arrest system also gives DA 29's orders for its engine
  arrest in a world without dialogue events; 133 to a bailed, bought or released prisoner (`Locks.FreedPrisoner`, the release's only when the releaser's faction books bounties
  or is the player's; the bail's 128 is not given, [economy 11.5](game/economy.md#115-bail-and-sentence)); 35 to the eater of a meal getting up ([eating on the
  ground](#captives)). Each is given through `PlayerOrders.Give`.
- The failure text is said only for a character of the player's faction that is not an animal (`PlayerOrders.Show`; the order step's vfunc `0x2d8` call in `FUN_140510920`,
  [precedence](game/ai-tasks.md#precedence)); anyone else's blocked order waits silently.
- Owners that take a player character's order only (an NPC's leaves the queue, `Gone`), their act-outs being the player's: build, repair and dismantle (the sites, doors and
  towns they take are the player faction's, and the start's "I don't have any building materials." is said for anyone), the machine orders and prospecting (87, 97, 124,
  146, 150: a worked bench advances the player's research, hauls go to the player faction's storages, prospecting fills the player's prospecting window, the
  turret's gunner is the interface's), a container's (26, 76: the player's container window), opening an inventory (26 with the inventory detail: the player's loot window)
  and a door walk (72, 73, 76 on a door: the interface's one door target, `PlayerState.DoorTarget`), and the Medic, Robotics and Rescue buttons. Moves, follows, attacks,
  first aid, splint and rest, lifts, kidnap, loot, stealth KO, put down, beds, a ground pick-up (3), the rest of the lock work, put in cage (108) and the arrest orders
  (240, 213, 112, 4), 133 and 35 take any character; their
  act-outs post and say the worker's own messages for the player's side only.
- Saved for player characters (`Saving/OrderSave`, in the character's GAMESTATE_AI record next to the perma-jobs; format and evidence: [save](formats/save.md#order-queue)): the queue front first,
  started or not, as the original saves its deque as `tjob<i>`. A load reads the orders into `CharacterCold.SavedOrders` and starts nothing; `DecisionSystem.Run` makes them the queue (nothing
  started) the first time it meets the character, once every character and fixture they name is there, and the replan bit every load sets runs the front through source 1 like a new order.
  Left out (the queue pops a `Gone` order the same way): a row no system owns, a target character or site that is not in the world, a handle that names nothing, ground-item pick-ups (a runtime
  item id) and the Medic / Rescue buttons' one-shot looks. An owner with a detail (`IOrderOwner.SaveKind` / `LoadDetail`) codes it in Meitou's own key. Before the front is checked, each order's owner
  readies what its live command would (`IOrderOwner.Restored`): production makes the machine of a rock, town dummy or seat, which no save holds (without it
  the operate order was `Gone` and a miner stood idle after a load, #598). **Engine choice**: the original's loader
  re-adds each order into its second list (`+0x70`), not into the deque ([precedence](game/ai-tasks.md#precedence)). Hashed as the move-only queue was while it holds only that (nothing, a started order alone, started moves), else under its own mark.

### Decision step

`World.Decisions` (`DecisionSystem`) chooses every task of the characters it decides (`DecisionSystem.Decides`: `IsPlayer`) as the original's chooser does
([precedence](game/ai-tasks.md#precedence)); NPCs keep the AI ([AI](#ai)) for every source but the first: an NPC that holds an order is asked its queue here, source 1
alone, when due as a player character is, and a blocked front also on the re-ask ([order queue](#order-queue)). Serial, in slot order: in the slow world right after the last of retaliation, aggro and flight (after every
system when the world has none), and between ticks for orders only (`ApplyPausedInterfaceCommands`; the bit stays for the next tick). Nobody is decided while carried, out
cold or dead (`DecisionSystem.Down`, the character tick `FUN_1405d00b0` runs no chooser then); the replan bit waits.
- **Sources** (`IDecisionSource`: `CanRun` and `Desire` pure reads, `Start`): an open list sorted by step, then `Key` (the original's list order); systems register theirs
  (`IDecisionOwner`), later code may add more (`DecisionSystem.Register`). The first source that can run starts; one that runs the character's task already ends the step. Step 4 picks
  the best desire x weight (x1.25 for the goal running now, again below 1 while its task is not [finished](game/ai-tasks.md#finished-tests); a tie goes to the later-listed).
  1. Orders: the front of the queue ([order queue](#order-queue)): runs, starts, or (blocked) shows its text once (`Tried`), stays queued and the step goes on.
  2. Commitment: no source at this step (no player goal above priority 2 has the commit flag). The priority-2 stop before step 5 is the implicit 153
     job's (`ProductionSystem.BedCommitment`, key -1 of step 5; [implicit jobs](game/ai-tasks.md#perma-jobs)).
  3. Urgent jobs: the job list as in step 5, asking only the jobs urgent now: kind-2 234 Turret guard while the character senses an enemy (AI `+0xbc`,
     `SenseBlock.Enemies`). It beats Self preservation until a melee attacker comes within 40 (234's effect is planned through 146, `!158`). 187 Obedient
     Slave of an enslaved player character (slave state 1, not sneaking) is urgent at all times: the master squad's Slave goals decide it ([captives](#captives)). The original's list `+0x70` (a loaded queue and permanent orders whose row has no job kind, [precedence](game/ai-tasks.md#precedence)) is not modelled apart from the order queue.
  4. Priority-3 goals: 32 Self preservation (`RetaliationSystem`, weight 1: the attacker this tick that took the character as target and alerted it, within 181.7, not shooting at a
     gunner, passing a holder's post test; desire 1 within 40, else 0.7; a deferred defence, row 13); 243 Getting food (`TownDutySystem`, weight 0.5, the
     [eating](game/character-stats.md#eating) desire; a food run); a player animal's priority-3 race goals (`CaptiveSystem.RaceGoals`: 277 limb fetch 1, 278 limb play and 279 chase by
     their desires, x the race entry's weight); 259 Picking up food (`FeedSystem`, animals, weight 0.5); 16 attack on sight while on Hold (`AggroSystem`, 1 here,
     listed last as Hold adds it; the enemy this tick's look or a squad mate's found; desire 0 while Passive, sneaking outside a fight or not standing; a deferred fight, row 16).
     The player faction's 15 Protect allies (`RetaliationSystem.JoinFight`, key 15, weight 1, listed before Self preservation; finder, desire and plan in
     [precedence](game/ai-tasks.md#precedence)): the aggro looks' allies valued by their fight threat (`CombatSystem.FightThreat`), weighted by distance, a deferred defence, row 13, against `CombatSystem.BestAttackerOf`'s pick for the ally.
     The retaliation pass offers nothing to it; the step reaches it at the engine re-ask (idle or a job's task, every `JobCheckTicks`) or any replan, so a medic or worker on its job joins, one on an order does not.
     A player character woken by a failed kidnap (`CarrySystem.Kidnap`) or targeted by a failed stealth knock-out (`CarrySystem.StealthKo`) is told of the attacker (forced sense) and
     replans (`EngagedDefence.Engage`); `EngagedDefence` (key 32, desire 1 within 40 else 0.7, a deferred defence, row 13) answers for 5 s unless an order runs first (**Engine**
     stand-in for the threats list, [carry-loot](game/carry-loot.md#kidnapping), [combat](game/combat.md#stealth-knock-out)). A sleeper's kidnap also gives it order 116 with the clear flag
     ([order queue](#order-queue)). An NPC victim gets no fight from the kidnap (**Verified** `FUN_14034ffa0` pushes no attack order): it replans and its AI
     answers the kidnapper its squad has tagged TEMP ENEMY. A failed stealth knock-out's NPC victim keeps its deferred defensive attack (engine stand-in). An autonomous squad's urgent package goals: `SquadAutonomy.PackageGoals`
     ([autonomous player squads](#autonomous-player-squads)).
  5. Jobs: the job list in order (`IJobSource` contributors by type, `DecisionSystem.RegisterJob`; the original's keys N+8, N+7, ...): the jobs switch gates every type but 16, 31,
     45, 57, 58, 250 (`FUN_14050c1c0`, both passes; 289 is gated), a row the repertoire does not register is skipped, the first that can run starts and one that runs the task ends the
     step. So a higher job takes over a lower running one at the next decision; the running job keeps no priority beyond its place. With nothing found, a list job the task still runs
     ends when its contributor says so (`IJobSource.Stop`: production's operators, haulers and follow jobs; the other act-outs end by themselves). Contributors: production (87, 152,
     124, 97, 234 while not urgent, 125 through construction, 31 / 45 / 289 follows; `Runnable`), the carrying errands (138, 223, 273; while their lift or carry runs), first aid (58, 57,
     250; the finders, a treatment runs until it ends), carry (148, 238), beds (106), aggro (16; the enemy the look offered this tick, the row's desire). Finders are asked only up to the
     first job that runs. Then the implicit jobs (`ProductionSystem.ImplicitJobs`, the squad AI options' lowest keys): over idling, a standing task or a job's task, the pick of
     `ImplicitPick` starts and runs while it is the one the character is on; one that no longer holds ends.
  6. The priority-2 goals (a player animal's race goal 233, eating a downed body, `CaptiveSystem.RaceGoals`), then 14 Idleness (priority 1): a player character stands idle, its routine
     (player characters neither wander nor follow a leader by themselves). Idleness runs over idling, a fight and any task a plan still runs (an order's, a goal's, an engine act-out's:
     those end by their own rules), and gives up a wander or follow no plan runs. A task that ends (`TaskEnd.Done`) or a fight's end leaves the character idle and asks a decision, which
     lands where the original's re-plan would; nothing resumes. The default task (`+0x190`, tried after the priority-1 goals) is never reached: Meitou models no manoeuvre mode but
     ROAM_FAR, whose default task is idleness.
- **Triggers** (`CharacterCold.Replan`, `DecisionSystem.Request`; hashed while set; an NPC keeps the bit only while it holds an order): a task end (`TaskStart.End`, any reason), an order given or the queue cleared, a stagger outside a
  fight (not for a gunner), the melee alert while the fight is off unless the target gets up or is player-side and moving (horizontal speed above 1), a job list change or the jobs switch,
  a load, a sighting for row 16 (engine: the look stands in for the sensed list's update), a mate's or ally's defence offered, a failed kidnap's or knock-out's victim told of its attacker, a fight's end (a follow row then follows again,
  `MovementSystem.FollowAgain`: for an order in `PlayerOrders.Prepare`, for a job as its contributor). Also a front whose task ended, or one not tried yet. **Engine** re-ask (no
  periodic replan was found): every `ProductionSystem.JobCheckTicks` 10 (slot-staggered) a character not fighting whose task is a job's, 14 Idleness's or no start's, and one
  whose front order runs its task (the original's order step tests it on every AI update); jobs and implicit jobs are found on this re-ask and the triggers. The job step passes
  over a job the general list tests skip (`ListTests.JobSkipped`, [finished tests](game/ai-tasks.md#finished-tests)).
- **Engine starts outside the step** (a step of a running row, or a reaction to another's act; the step leaves a claimed character alone that tick, `DecisionSystem.Claim`): a follow
  job's (or a not yet running follow order's) defence of its followed character and a bodyguard's own defence within 40 (`RetaliationSystem.Guards`, `SelfDefends`; claimed).
- **Trace** (`DecisionSystem.Tracing`, off by default): each decision fills `CharacterCold.Decision` (`DecisionTrace`: tick, winner, one line per source asked with its answer: an
  order's failure text, "no attacker in range", a score, a job's index), published as `CharacterSnapshot.Decision` while tracing. Not saved or hashed.
- **Player-faction characters that are not `IsPlayer`** exist only in Meitou: the sandbox's and `--crowd`'s bystanders and tests' extras (a load, a new game or a recruit makes every
  other player-faction character `IsPlayer`; the original has no player-faction character the player does not command). They hold no jobs and get no orders from the interface, so
  the step leaves them to the NPC rules (retaliation's own defence, the attack-on-sight look) and the player faction's goals as idle-only passes: 243 (`TownDutySystem.LookPlayerFood`),
  259 (`FeedSystem`, an animal idle or following) and the animals' race goals (`CaptiveSystem.RunPlayerAnimalGoals`, list order). A follow order of theirs (one a script gives)
  follows again after its fight step at the decision step's order pass (`PlayerOrders.Prepare`, `MovementSystem.FollowAgain`), or at the NPC stand-down (`AggroSystem`) while
  the order does not hold it.
- Engine choices: errands' walks wait out a fight (NPC duties, the dialogue approach), while a player character's food run ends when a fight replaces it; the step's starts act from
  the next tick (deferred fights) or at once (walks).

## Systems and their order

`StandardSystems.Build` is the single wiring authority. Ordered systems:
Population, Player, AI, Pursuit, Movement, FirstAid, Body, Carry, Construction, Combat, Animation, Ragdoll
(only with `StandardSystemOptions.Ragdolls`), Feed, Retaliation, Aggro, Flee, Crime, Arrest, Locks, Trade, Dialogue (Arrest and
Locks come with the Crime part), with Production right after Construction. Tests selecting subsets retain this relative order.
Population spawns before later SlowWorld work; player selection precedes movement orders;
AI writes tasks before movement; pursuit schedules paths before movement answers them.
First aid reads patients in its own Act barrier and applies treatment during serial commit.
Body handles medical ticks before combat (`TickMedical=false`); carry sees knockouts before
combat and drops carried targets before attacking. Combat swaps before animation/retaliation;
crime sees deeds and final positions/carry links. Game combat uses `SelfApproach=false`.

### Spread sweeps

Engine choice: a periodic per-character pass looks at each character on its slot's turn (`World.Turn`: `Tick - slot` a multiple of the period, within the far cadence's step; the AI's think anchors at `Tick + slot`, so a slot's look and think interleave), as the original runs its AI
updates 8 characters a frame in turn ([game loop](game/game-loop.md#the-character-update-budget)), instead of all on one tick: Aggro, Crime,
FirstAid, NPC looting, Captive (`SweepEveryTicks` 30, the goals' look), the town goals' finders and Kidnap (`LookEveryTicks` 15).

## Population

`PopulationSystem`, `SquadFactory`, `CharacterAssembly`, `GeneratedAppearances`: activate the loaded zones
(the camera's 3 x 3 block and the box round the player's characters, [corpses](game/character-stats.md#corpses)), gate spawning on navmesh readiness, create residents/bar/roaming/wild/nest squads,
and assemble race appearance, gear, stats and bodies. Counts, roles, offsets, caps, spawn weights
and world-state gating follow [population research](game/factions-squads-towns.md).
Missing references become `SquadPlan.Problems`; world states are answered live (`WorldStates`, see Campaigns under the off-screen tier). Residents are housed by the original's building rules with housemates and
default residents ([town residents](game/factions-squads-towns.md#town-residents)). A town that unloads turns each squad it made into a pinned platoon holding a `StoredSquad`
(`PopulationStored.cs`): each living member's own cold state (name, look, body, kit, bounties, crime marks) with its place,
cleared of path, AI, carry, treatment, combat and stealth state, plus the squad's home building, shop, purse and AI package.
The town then holds squads and makes none of its own; the platoon is made of the same characters when the town's zone is
active again and leaves with the town's grace. A wild site with a living member of its squads in an active zone does not unload (nor counts its grace) wherever its own zone is, so a chase away from home does not end in a vanish (engine choice for the original's per-platoon unload by its own place); a nest held so makes no new squads. A town unloads with its zones whatever its squads do: one of its squads with a living member in the loaded box of an active zone, or carrying a player character, stays in the world as a loaded pinned platoon that unloads by its own place (`PopulationSystem.KeepLoaded`, [persistent platoons](game/factions-squads-towns.md#1-object-model)). This is the original's model: residents are persistent platoons kept as their
characters ([persistent platoons](game/factions-squads-towns.md#1-object-model)). A loaded town also stays while any zone of its coverage list is active, and asks for every zone of its list each frame with the largest camera or player timer among them, a copy that extends nothing, so its zones lapse with the last of those (**Verified**, `FUN_140929b20`, `FUN_140a09380`; [corrected: no permanent hold](game/character-stats.md#corpses); `PopulationSystem.AskTownZones`); the load trigger stays its own zone (s19-d036). Saves write stored squads as the town's platoons (`sqt` 1). Any platoon's
activation waits for the navmesh gate of its town's zone (a traveller: its own). A town made once (its pools' `started`) makes no residents again; its
badly hurt squads are made afresh within its pools (see "Off-screen tier"). `ActiveZones` is double-buffered. Zone loading/spawn planning stays off the
simulation thread; publish results at boundaries.
`World.PreparePopulation` queues nearby residents/wild squads, activates existing nearby roaming
platoons and drains completed builds during loading. It publishes matching snapshots without advancing
ticks, game hours, AI, movement or roaming timers; readiness includes navmesh-gated sites.
The original builds one queued squad request per rendered frame on the main thread (`FUN_140583310` pops one from the locked deque at world `+0x4a0` and builds it with `FUN_140581770`, **Verified**). Meitou, one tick standing for a frame: behind the loading screen all finished builds are taken; in play each tick finishes at most one roaming platoon build, else one squad of the finished wild builds (a wild build's site is marked loaded with its last squad); towns are built whole (`FUN_140583a10`, not this queue).

### Unique characters

The original's rules: [unique characters](game/factions-squads-towns.md#unique-characters). `World.Uniques` (`UniqueCharacters.cs`) is the
original's table (CHARACTER → claimed, live character, state 1/0/2, player flag), changed in serial phases only and hashed once it has an entry.
- **Create gate** in `SpawnSquad` (serial, so two squads built at once cannot both make one): a free unique member is claimed and made; a
  claimed one gets its first `unique replacement spawn` (look rolled then) or nobody, and the first member left leads. Animals go through the
  same gate (the original's `animals` / `animals2` loops call it, `FUN_140583a10`; six base ANIMAL_CHARACTERs are unique, e.g. Settler's Pup). The player's and sandbox squads skip the gate.
- **Handles**: bound whenever a unique enters the world (made, stored or saved squads re-made), which also claims it (engine choice: the original
  records only the handle there); dropped when a squad is stored or a corpse removed.
- **State**: deaths in the combat commit (player flag when the killer is the player's); a refresh at every population look marks the dead, the
  caged (player: the cage owner is the player's), the alive carried off by a non-ally, and frees one held or carried no more (engine summary of
  the original's ally/town tests). Off-screen losses (Meitou fights) die.
- **Release**: a squad leaving for good without being kept (Faithful despawn, a wild site unloading, a town squad remade by regeneration) frees its
  living uniques so its template makes them again, only when the entry is that character's (Meitou: knocked-out but alive counts as living); the
  dead stay claimed. Town squads stored on unload keep their claims.
- **Recruits**: a character leaving its squad for another (recruited, bought, split off) leaves the save's platoon record it was made from too
  (`SavedPopulation.Leave`); kept there, the record's unload kept it and the platoon made it again, a second copy of a recruited unique (#701).
- **Seed**: at the first population look, the uniques of each template's first `prisoners` template are imprisoned and unclaimed (kept when a
  save already has them).
- **Save**: CAMERA `usedUniques*` as the original (claimed entries; prisoners derived again). Handles are the written character's for player
  squads, else the loaded save's, else null (NPC handles are not tracked back from the writer).
- **Not built**, dead in the original: FACTION `special squads` (never spawned) and the `UniquePlatoon` missions that only they would run
  ([special squads](game/factions-squads-towns.md#unique-characters), **Verified**). Import is built ([save.md](formats/save.md#importing-a-game-savemanagerimportgame-fun_140378a30)).

### Off-screen tier

The original's rules: [off-screen world](game/factions-squads-towns.md#off-screen-world). It keeps only persistent platoons (residents) and
campaign squads off screen, moves them in straight lines (roads for long trips), never fights or heals there, and drives the rest by clock-based
budgets. The tier follows it, with Meitou choices marked.

**Engine choice (s18-f24).** The original examines one platoon per faction per frame (`FUN_1406b9970`: its loop bound is one; a per-faction cursor at +0x23c, wrapped past the end of the list, picks it; if that platoon has an unloaded half it runs its update and, when that returns false, activates it and returns, else the cursor advances), so each platoon is examined once per (platoon count of its faction) frames (**Verified**, decompile). Meitou steps and may activate every unloaded platoon on each `CheckEveryTicks` look (`PopulationRoaming.StepRoaming`); rates do not depend on it because each step measures its own elapsed time, only the activation latency differs.

**Town overrides** ([rule](game/factions-squads-towns.md#town-overrides)): `PopulationSystem.Replace` runs at each town load but those behind the loading
screen (`PrepareLoading`) and those a code mod holds; the winner (`TownOverrides.Choose`; never the replacement in place, so an unreplaced town may take its own TOWN, which then is the
replacement) goes into `TownSite.Replacement` (`ApplyVersion`; a code mod's choice of the town's own record is stored as none), and `TownSite.Town` (what every reader uses) becomes it with the placement's id and names
kept (engine choice) and the old faction when it names none; `SiteFactions` follows. The town then makes the replacement's residents even if it was made before.
Its unloaded own platoons whose TOWN is not the new one (a save's by `basetown`, one stored this session by `Platoon.BaseTown`, the TOWN it was stored from) are
told to `ITownSwitchObserver`s. With `OffscreenOverrides` on, those no observer moved are despawned with their uniques released, and one still in the
world then is despawned at its next activation; dropping them at the change keeps a town changed back from bringing an old garrison back (engine
choice). Off (Faithful), as the original, each is refused at its activation by spawn mode 3 (`RefuseReplacedHome`), which releases its uniques only
under a unique leader.
Until the new residents are made (`SiteState.Repopulate`, the original's `updatedTownOverrides`) each load makes them afresh without a
new choice, also for a held town; behind the loading screen and for a town of the player's faction only with `OffscreenOverrides` on or for a change made
away from a load (`SiteState.SwitchedAway`: off screen, by a code mod, or read from a save), else (the original's order) the gate comes first. Each such load resets the zone (`ResetZone`): the town's buildings there that are not the player's are registered again from the level (state, locks,
doors, contents; `FixtureRegistry.TownReplaced`; a save writes their owner and level `destroyed` into the old record of each, fixtures or not, `WriteResets`), its other buildings change owner, its homes in every zone are furnished anew ([town overrides](game/factions-squads-towns.md#town-overrides)), the zone's item slots are due at once and its loose items are removed.
Saves write `replacementTown` (and the town's `faction` while replaced) and read it back; a new game or a load clears them first. A town replaced but not
loaded since gets `meitou override pending` on its state (`PopulationSystem.OverridePendingKey`) with `OffscreenOverrides` on or after a change made away from a
load (`OverrideSaved`; Faithful leaves a load's own change unsaved, as the original's `updatedTownOverrides`), read back into `Repopulate` and `SwitchedAway`; a stored platoon's `basetown` is
its `Platoon.BaseTown` (a world squad's its platoon's), so one of an older version stays refused after a load.
Tests (the load rule only, with `OffscreenOverrides` off): `PopulationTests.A_town_takes_its_heaviest_qualifying_override_at_each_load_and_makes_its_residents`, `..._whose_residents_were_never_made_...`,
`TownOverrideSaveTests`.

Seven Faithful/Meitou switches (the fifth, campaign inheritance, under Campaigns below; `PopulationSettings`, live on `PopulationSystem`; Advanced settings (the fifth on the Fixes page), Meitou on by default; `PopulationOffscreen.cs`):
- **Squads keep their people** (`KeepRoamers`). Meitou: a roaming squad the population made keeps its characters as a `StoredSquad` from the
  first time it leaves, travels on as them and comes back as them (moved along with its stand-in); saves write it as a new platoon (`sqt` 2,
  `persistent` false). Faithful (the original): a platoon that is not a town's own is despawned as it leaves, and a stand-in out of range for
  60 s is despawned (the orphan timer); a save's despawned platoon leaves the save; loaded towns refill their pools. **Observed** (headless):
  Meitou brings back the same characters; Faithful replaces them from the pool, and save 2 loses 15 of its 15 travelling platoons in six minutes.
  A platoon kept only by this switch, the wild one or a code mod's spawn stays loaded while a member lies knocked out inside the loaded zones
  (not one the unload splits off as a captive), where the original's member-by-member thinning keeps such a member unless he lies out of view
  within 600 of the departed leader ([off-screen world](game/factions-squads-towns.md#off-screen-world)). **Observed** (headless, a reporter's
  save): with the leader sent 20000 away, his two knocked-out squad mates beside the player stay; before, both vanished.
- **Fights far away** (`OffscreenFights`). Meitou: every 2 s, an unloaded travelling or wild stand-in (not a town's own, not the player's) met
  by an enemy (`AggroSystem.IsEnemy` either way) within 300 fights the first such in id order; strength per member able to fight is 10 + the
  mean of melee attack, melee defence and toughness (25 for a squad never made); A wins with S_A² / (S_A² + S_B²); the loser is despawned and the
  winner loses its weakest members in the share min(1, (S_loser / S_winner)²) / 2. Faithful: none.
- **Healing far away** (`OffscreenHealing`). Meitou: when a kept squad comes back, `MedicalState.Recover` steps the hours it was away in one go:
  wounds treated and closed, flesh healing at the part rate, stun gone, blood back at the resting rate, a KO ended if vital parts allow; no
  hunger, degeneration or death. Faithful: it comes back as it left.
- **Wild squads stay** (`KeepWild`). Meitou: a wild site (sector or nest) that unloads keeps each squad with someone alive as a `StoredSquad`
  (nothing leaves a nest's pool) and makes them first, where they stood, when it loads; they count against the site like a save's wild
  platoons, heal as above, and a save writes them as wild platoons. Faithful (the original): dropped, a nest paying for its hurt squads, and a
  save's wild platoon despawned on leaving (the orphan timer); an occupier is kept either way ([occupation](#package-classes)). **Observed** (headless, synthetic sectors and nests): Meitou keeps 12 squads and
  brings back a renamed character; Faithful remakes the squads from the seed.
- **Towns keep their schedule** (`ResidentSchedule`). Meitou: a town squad that comes back after at least an hour unloaded (`ScheduleAfterHours`) is
  marked `Squad.Resume`; each member's first think picks where its plan's walk ends (its bed at night, the counter, its node, seat or gun; a ring of 4 by member index, walkable only), the serial step moves it there (Movement would overwrite a move from the parallel think) and
  the act-out goes on from there, so the town looks as if it had lived through the time; nothing runs while it is unloaded. Faithful (the
  original, which runs nothing for residents off screen): where they stood when it unloaded.
- **Towns change far away** (`OffscreenOverrides`, `PopulationSystem.StepOverrides`, state in `TownOverrides`). Meitou: at each look, after the zones'
  loads, every town whose residents are not in the world (status `Unloaded`: not loading, loaded or waiting out its unload grace), whose zone is not active
(a load a zone gate put off), not of the player's
  faction, not held by a mod and not behind the loading screen takes its winning override at once (town overrides above: old platoons dropped,
  `Repopulate`, zone reset, a `town` log line marked off screen, `OffscreenSwitches` counted), so campaigns, spawns, trade and the map see the new
  owner before the player arrives; a chain moves one step per look. Each town has its own tested states: those the entries of the TOWNs its own
  record and the version in its place reach through their lists test (a version they do not reach, a mod's or a save's, adds its own). A switch
  that would bring a town back to a version it held since the answers of its tested states last changed waits for their next change, so a pair of
  entries that both qualify moves once, not at every look (the original flips such a town at each load), and a change elsewhere does not move it;
  a load starts this afresh. A town with nothing to take is looked at again only once one of its answers or its TOWN changes (each TOWN's entries
  are read once). Engine choice. Faithful: only at the zone's load.

Implemented (`PopulationRoaming.cs`):

- **Step**: at every population look (15 ticks), every unloaded platoon that is not a town's own (or is one under a contract) steps by the look's game time in a parallel pass
  (`World.ParallelFor`, 64 stand-ins a job): the Faithful orphan timer and its travel. A step changes only its own platoon, so the result does not
  depend on the order (the roaming hashes agree at 1, 4 and 16 threads). Despawns, activations, loaded squads, pools and fights follow serially in
  id order. Town squads do no off-screen work (the original's GOHOME at home), except a hired one, which follows its employer
  ([off-screen world](game/factions-squads-towns.md#off-screen-world)).
- **Job**: the original re-reads it every unloaded step from the blackboard's current package (`unloaded func`, `FUN_1407ee790`, `FUN_140268840`, **Verified**), a campaign's
  while the war manager holds the platoon. Meitou takes it when the squad unloads (the package it runs then) and when a campaign lets the platoon go (its package's); a change to
  GOHOME or a patrol sends a roaming stand-in on its way at once. Job 0 (no branch of its own in the original: 2 for a non-persistent platoon, 4 for a persistent one) stays the engine's travel stand-in.
- **Speeds**: per job as the original (GOHOME 55, TRAVEL_TARGET 25, TRAVEL_TARGET_FAST 60 or 90 beyond 25000), the engine's travel stand-in (job
  NONE) 25 or 90, all capped at the original mover's 70 (`StandInSpeedCap`); straight lines. A short-range patrol keeps walking at 25 (the
  original's stands still). Destinations stay the engine's (towns, patrol points near home) except a TRAVEL_TARGET job, which heads for the saved mission town ([off-screen world](game/factions-squads-towns.md#off-screen-world)): the original keeps no roaming squad off screen,
  and saves give no usable target (**Observed**, 4 owner saves: `mission target` always type 0, `target town` on 1-3 platoons, `mission town`
  mostly a TOWN).
- **Roads** (`RoadNetwork`, `PopulationData.Roads` from the level's ROAD records; the same instance serves on-screen long routes,
  [pathfinding](game/pathfinding.md)): a stand-in, or a campaign's leader, takes the roads by the
  original's rule ([roads](game/factions-squads-towns.md#off-screen-world)), worked out when its goal changes and walked point by point; a
  campaign's followers go straight for their leader (engine choice: their goal moves every look). Hidden roads count. Engine reading: the
  join pull is clamp(w) × the point's forward distance. **Observed** (base data): 2109 roads, built in 33 ms; a route takes 0.5-16 ms; 193 of
  200 random town pairs go by road at preference 0.6; about 10 % of roads have a `length` well under their drawn length (0.17-0.44 ×, median
  1.00), which the original's cost uses as is, so a faction with preference 0.3 still takes the Hub-Stack road (walked 34000 vs 16900 straight).
- **Hand-off**: a stand-in that travelled comes back on the nearest walkable point (`WalkableSpots.Nearest`: rings of 4 to 128 units, eight
  directions each); its kept characters keep their offsets round it, each moved to a walkable point within 32 if needed, else onto it, and each
  stands on the surface at terrain level there, not a ruin's upper floor over it (`MovedSpot`, #731; engine choice). A town's
  own or wild platoon, or one that did not move, keeps its characters' exact places. The original (**Verified**): the activation
  (`FUN_1407ebdf0`) hands the active platoon the unloaded one's travel (its mover place +0x64 less a place the decompile hides; **Observed**:
  the unloaded platoon's constructor `FUN_1407ef040` keeps its place at unload at +0x80 beside the mover's) and its spawn place (the mover's); loading the characters (`FUN_140795b30`) moves each one of a kept platoon by that travel (`FUN_1405c8460`, += on its saved
  place); one inside walls goes to the spawn place (`FUN_1404ffad0`). A save writes an unloaded platoon's mover place (`FUN_140372ef0`) and its
  characters' records as they left (`FUN_14036bad0`), and the load starts the unloaded platoon at that saved place (`FUN_1407f85f0` ->
  `FUN_1407ef4b0`), so only the travel after the load moves them. Meitou the same: `SaveCapture` writes a travelled stand-in's place and its
  characters where they left.
- **Cost** (**Observed**, headless, save 2 with 2000 extra travelling stand-ins, warm): the step 0.08-0.1 ms per look at 1 or 8 threads (too little
  work for the threads to pay), pools and fights 0.23-0.26 ms per look (fights run every 4th look), about 0.03 ms a tick in all; save 2 alone
  0.08-0.1 ms per look. Not measured in the viewer's bench harness, which does not run the population.
- **Pools** (`PopulationPools.cs`): every town and nest has the original's two pools (residents `pop`/`popd`, roaming `pop2`/`popd2`),
  read from the save's town and nest states and written back, with `started`, `ded`, `tod`; the hour they last grew is not saved (set at load,
  as the original). Every 2 s (60 ticks) they grow 0.21818 per game hour up to the desired max (roaming only once residents are within 1 of full, then credited the time it waited), for every
  town with pools (the save's from the first look, a new one once made) and the save's nests from load (a rolled nest from its first load), loaded or not. Growth is not state-hashed (as the population's other site state).
  - **Towns**: one made from its data counts its resident squads (those with a home; bar squads not, engine choice after the census) into `pop`
    and `popd` and is `started`; a started town never makes residents again, even with every squad dead (it holds what is left). Town roaming
    pools stay as loaded (0 in every base save) and `RoamingShare` still caps roaming squads (see the census conflict in
    [off-screen world](game/factions-squads-towns.md#off-screen-world)).
  - **Regeneration**: on a town's load, each squad of its resident pool (`sqt` 1: residents and bar squads; platoon id order; of any faction on the town's side of player and non-player) that lost ≥ 60 % of its template's expected size
    (lost: that size less its count, a save's platoon's `char count` with its dead), while the losses fit `pop` less the count of all its pools' members and the losses
    marked so far, comes back made afresh from its template at its place and home, into
    the same platoon (its old characters dropped, its save record and name kept). A template whose members are all by chance gets up to
    8 rolls. A `regenerates` squad of that pool is marked at any loss, past the share and budget tests. The town's own roamers out of the world follow
    (their pool's budget, the running living shared), each marked one coming in rolled from its template into the same platoon. A nest's load runs the same pass over the
    save's platoons of the nest (its roaming pool: share and budget tests only), and a marked one comes back made afresh into the same platoon, round the nest, at its
    place ([off-screen world](game/factions-squads-towns.md#off-screen-world)). A save's `regenerates` platoon that comes in with only its dead stays as its corpses
    and is made afresh when it next comes in after leaving the world.
  - **Nests**: the pool starts at 0.7 × `nest resident population` (or the save's `pop2`); a load fills it to `floor(pop2)` members, unless a player character is within 1250 of the centre it picks (then
    it only weighs, as a top-up; engine choice, the original has no load fill: #731, a save loaded beside a nest made squads 150 from the player); one nest a tick in turn (tick mod nest count; the tick stands in for the original's frame),
    so each every N ticks, N = all nests: a loaded one with room makes one squad (rolls keyed after the load's), unless a player character is within 1250, then it only weighs.
    An entry must fit the free budget, except while the pool is empty with more than 4 free (the Fog Deathyard's `Fog hive nest`, expected
    size 1 + (12 + 24) / 2 = 19, against a budget of floor(0.7 × 25) = 17). A roll that finds no entry (all weights 0 or none fits the free budget) with no squad of the nest left alive sets `ded` for good, `tod`,
    and posts "<nest> has been wiped out" (engine choice: "intact" = someone alive; a squad handle that no longer resolves keeps the nest, `FUN_1408f8740`, so squads leaving the world are forgotten by `ForgetSquad`); so does a pool spent below one member with no one alive. A squad that lost someone takes its living out of `pop2`
    when the nest unloads. Dead squads lying there still count against the template weights. **Observed** (headless): a swamp raptor nest
    (N 20, pool 14) refills an 8-duck squad after its two squads die; drained to 1 with all dead it is wiped out and saved with `ded`, `tod`,
    `pop2` 1.008, `popd2` 20.
  - **Homeless squads** (`PopulationHomeless.cs`; [original rules](game/factions-squads-towns.md#off-screen-world)): every 2 s a squad whose
    leader patrols (a PATROL goal, in any package: the Border Zone's starving bandits, a town's patrol squads) walks to its patrol point: the leader to the point exactly (`AiSystem.ActPatrol`, kept as the home centre), the
    others following it; within 100 of it the next is drawn (the 2 s step stands in for the original's "move ended"); a town drawn with `patrol
    approaches towns` is aimed 200 outside its first gate that is not a ruin with gate code 0 or unknown (unknown = outside, engine choice) or wall
    with `interior` references, so bandits stop outside the Hub ([patrol task](game/factions-squads-towns.md#off-screen-world)).
    A squad belongs to the sector it stands in: one that walks into an unloaded sector leaves the world (after the roaming
    10 s grace outside the active zones, ≥ 1382.4 from every player character), kept there with
    `KeepWild` up to that sector's count, else dropped. The area tick refills the loaded sectors, all taken as one island (engine choice): while below the sum of their shares one sector is drawn by weight and, if it makes no squad (no spot), the next in the same look, as the original. Sectors a town stands in (re-read each refill, not Ruins or points of interest) and the one under the camera focus are never refilled.
    **Observed** (headless, Fog Islands, one zone loaded): a fogman squad walks a sector a few minutes; leaving, a fresh one is made.

Implemented (`PopulationCampaigns.cs`; [original rules](game/factions-squads-towns.md#campaigns),
[save](formats/save.md#war_savestate-type-9-faction-campaigns)). Almost all campaigns aim at player towns (outposts), so nothing rolls until
the player has one and stands in it; requests (victory/loss chains, ally help) can follow.

- **Data** (`Meitou.Data`, `CampaignData` from FACTION_CAMPAIGN, cached per inheritance mode): ranges × 10, `repeat limit` × 24, weighted
  trigger lists, phase packages. Fourth switch, **Raids as written** (`CampaignAncestorsOnce`): Meitou applies each ancestor's list entries
  once; Faithful twice, as the original (doubled weights and trigger chances).
- **War manager** (serial, in faction order at the population look; every rule reads the game clock, so the cadence does not matter): the
  6 h roll, the chance with the linear falloff and `raidFrequencyMult`, targets among `World.Buildings.Towns` (biome groups, no-go zones; only a player town
  with a player character within 1250, slot 70; none with occupiers for a non-hostile campaign; the town level from its finished buildings), requests with due times, redirects (a running campaign too: `Redirect`) and retries, the
  `trigger player ally` help, and DIALOGUE_LINE `trigger campaign` (`TriggerFromDialogue`, keyed off the speaker and tick). Rolls
  are keyed off the world seed, faction and roll hour, so a run repeats. The state hash includes the managers only once one has started or
  queued something, so worlds without a campaign hash as before.
- **Squads**: required men = int(`num forces` × `raidSizeMult`); the `special leader`'s platoon (or the template it leads, made near the
  faction's town), then for the 4 own towns nearest the target their free roaming platoons, then new unloaded stand-ins from `squads to use`
  within 700. Engine choice: homeless and a town's own platoons are not drafted. After the campaign a platoon is a roaming squad again, whose stand-in heads home off screen and whose loaded squad acts out its package:
  Meitou (`KeepRoamers`) with its people, Faithful under the normal despawn.
- **Phases** off screen as the original (0.8 × radius arrival, 4 / 12 / 1 h, the 20 % rule, 3 h removal); the leader travels to the target
  and the others to it at the `travel speed unloaded` speeds (serial, outside the parallel movers). With `OffscreenFights` (Meitou) campaign
  stand-ins fight like other travellers and a squad lost there leaves the list the 20 % rule counts (**Verified**, `FUN_1409c70f0`: the denominator is the platoons still registered). The fight timer is armed only in phase 1 of an assault's first 4 h, after the players' hold test passes (`FUN_1409c70f0` returns first), and reset at every phase change; phase 0's early end tests only the campaign's own target town. Faithful: an unloaded assault wins at 4 h. Engine choice: a
  campaign with no platoon left is defeated. End effects: the messages and the trigger roll or the 40-96 h retry; nothing else changes.
- **Loaded**: a campaign squad runs its blackboard with the phase package ([platoon blackboards](#ai)) and its members full minds that choose from
  the package's role lists with the normal chooser and planner ([campaigns](game/factions-squads-towns.md#campaigns)). Goal 215 Mission formation
  (`AiSystem.ActFormation`, `MissionFormation`): the lead platoon's members walk to the target town's own point, the others to the lead platoon's
  position one look old, run while 100 or more away, nearer at the campaign leader's last mode and speed (capped inside
  `CampaignPaceDistance`); the campaign leader keeps its `travel speed loaded` (WALK walk, JOG run mode, RUN and GROUPED free, NO_SPEED_CHANGE as
  it was) while every intact platoon is within 6000 of the lead, else it stands within 8294.4 of its point or walks. 45 Bodyguard guards the
  leader, 129 Approaching fast has the campaign's announcement said (serial, `AiSystem.SendAnnouncements`), the phase-2 retreats walk home by their
  own goals. They fight through the normal aggro; arrival, timers and the 20 % rule run. Off screen the trail is laid each look and follower i
  heads for its point i × slots × 150 / N back.
- **Assault**: in phase 1 a loaded campaign squad also fights (`Squad.War`, read by the aggro) everyone not of its faction in the target town or in no town (not animals, fake or bandit factions: [sensing hooks](game/ai.md#package-classes)) when its
  `pt2` package is a Package_WarAssault (20, 32), else the target faction when the campaign is hostile. Looting is [goal 177](#carrying-rescue-and-looting) where the package lists it, weighed by the members'
  full minds. A cannibal assault (class 32) goes to phase 2
  when three members of one platoon carry someone or 15 game minutes after that platoon's first carrier (`CampaignInstance.CarryStarts`, per platoon, cleared while it has none; saved by Meitou only); kidnapping: [below](#kidnapping).
- **Announcement**: the leader's goal 129 (`AiSystem.ActAnnounce`) says "has arrived" once per run and `dialog announcement` when the camera and a
  target-faction member are within 2500: its lines are said as speech bubbles 2 + 0.12 × length seconds apart, or DA 12's wait, and their `interrupt` makes the next talk
  with it the negotiation, also when its faction is hostile. Its end, or the player's talk cutting it short, marks it delivered in the leader's
  status town (`DialogueSystem.MessageDelivered`, state 76, not saved); 129 then ends and is planned again, so it repeats while the conditions hold.
  The campaign waits in phase 0, with no time limit, until the lead platoon's leader has delivered it and says nothing; an unloaded leader platoon
  waits too (`PopulationSystem.LeaderDelivered`). Dialogue DA 37 sets phase 1 (2 without a
  pt2 leader package), DA 38 phase 2; DA 39 (phase 3, unhandled in the original, unused in the data) is not built. **Observed** (headless, real
  data): Dust Bandits "Invasion with demands" shout their four lines 20 s apart; talking to the boss opens the negotiation; paying takes 2000
  and sends them back (phase 2), refusing starts the assault (phase 1, the squad's targets set), stabbing him sends them back.
- **World states** (`WorldStates`, `PopulationSystem.States`): WORLD_EVENT_STATEs answered live by the original's rule
  ([world states](game/factions-squads-towns.md#relations-and-world-states)) from the unique table and the relations (a copy of the table taken every tick, serial; the original reads it live, `FUN_1409a7e00`, **Verified**; the table's own scan `RefreshUniques` still runs every `CheckEveryTicks`); the squad factory,
  wild sites, towns and campaigns all ask it. A population given its own `IWorldStates` (the pinned tests) uses that instead.
- **Save**: WAR_SAVESTATE read and written with the original's keys (unknown keys kept; numbered keys past the new counts removed); a
  characterless campaign stand-in is written as a PLATOON with content file `platoon/` so `p<i>` resolves; a loaded campaign platoon travels.
  The phase, fight, arrival, announcement and carry clocks are Meitou keys ([WAR_SAVESTATE](formats/save.md#war_savestate-type-9-faction-campaigns)), so
  a loaded campaign goes on where it was (the original restarts its phase); the trail is laid again, the announcer is not kept.
- **Observed** (headless, real data, `raidFrequencyMult` 1000): Dust Bandits "Invasion with demands" against an outpost 1500 from Dust King
  Tower gathers 1 squad of 12 for 10 men, arrives after 0.28 h, survives a save and load in phase 1 (campaign, id, phase, squads), wins at 4 h,
  is removed 3 h later and queues its follow-up. The 9 owner saves' 103 war states each come out with the same keys and values.

## Player

`PlayerSystem` / Orders: selection, stop, move/queued move, attack, treat/rest/medic, carry/rescue,
loot, stances and squad toggles are commands. Resolve empty-ID orders against current selection.
New-game start data seeds squad/appearance/gear/money; the start's characters are then remade in the
character editor while `PlayerState.InCreation` ([characters](characters.md#character-editor)); original start
relation/research semantics remain partly unknown. Picking/HUD are host responsibilities, never simulation input.
Squads: `SquadManagement` orders the player's squads (`Squad.Index`), keeps `PlayerState.Squad` the active one and applies the squads screen's
commands with the original's limits ([Squads tab](game/ui-screens.md#squads-tab)); the snapshot's `PlayerSquads` lists the ones the player leads for
the UI ([independent squads](#autonomous-player-squads) are in `IndependentSquads`).

### Order toggles

Passive/Hold/Block/Taunt/Jobs state belongs to the character (medic and rescue are job-list entries, [job lists](#production)) and relevant
systems; the rules follow [combat](game/combat.md#fight-lifecycle-and-orders). A player's character defends itself (32 Self preservation, a [decision step](#decision-step) goal) only when no order of its runs or can start (a
bodyguard following also as a step of its row; a Loot order ends under a threat within 40) and against an attacker within √33000 ≈ 182; Passive does not stop that but keeps it
from joining others' defence and from attack on sight. Hold adds attack on sight (goal 16, another decision-step goal; not Passive, sneaking outside a fight, or prone), and every target of a
holder (sight, self-defence, re-pick) passes the post test (`Orders.HoldPost`); it walks to a target only within 80 (140 when the target is in a fight and heads for it) of
its hold point (`CharacterCold.HoldPoint`: where Hold was turned on, then each ground move's destination and each follow step's aim at the followed character's height (`MovementSystem.CarryHoldPoint`, as `Task_Follow`, [standing orders](game/combat.md#fight-lifecycle-and-orders)); not saved: a loaded holder holds where it stands, as setStandingOrder 12 does; **Observed**: the loader replays the jobs switch through it, Verified; Hold assumed alike).
A toggle (button or hotkey) sets every selected character to the opposite of the first one's state, so a mixed selection ends uniform (engine choice). Toggles and the run speed arrows also apply while paused ([time model](#time-model)).
New player characters start with Jobs and Ranged enabled. Jobs gates the job list and automatic rescue (the medic, robotics, splint, attacking-enemies, Bodyguard and Staying close jobs run regardless, as the original, [perma-jobs](game/ai-tasks.md#perma-jobs)); Hold does not stop a bodyguard or follower from following (#838, [standing orders](game/combat.md#fight-lifecycle-and-orders)); direct orders still run. The saved `chase mode` is this switch. Saved toggles override
defaults, including explicit false values. Jobs is GAMESTATE_AI `jobs` ([perma-jobs](game/ai-tasks.md#perma-jobs)); Meitou's older `meitou jobs enabled` is read, not written.
Sneak: sneaking speed cap and clips, quieter, smaller view factor; a hostile looker sees the sneaker only once its
spot meter fills ([perception](game/perception.md#spotting-a-sneaker)); the exposure feeds stealth XP.
Ranged (on for every NPC): a crossbow carrier takes the crossbow at any distance (Block too) and melees only when dry or with a threat
(anyone targeting it) within 40; off, it never shoots. Taunt and Passive act through the target score (`Combat.TargetScore`: Taunt +2 and its crowd term ×0.35,
Passive −1), which both the aggro look and the fighters' re-pick use.
New games select the squad's first member. The game host also selects the first member after
loading, before publishing the initial snapshot; the save reader itself retains saved selection.

## Movement and paths

`MovementSystem`, `PursuitSystem`, `IAgentWalkability`: schedule/query asynchronously, publish at
boundaries, follow ordered path points, steer/separate using previous state and project to ground.
New move orders preserve reusable paths; attack pursuit reschedules target paths. Turning can
hold translation while facing changes. Original speeds/encumbrance and research are in
[pathfinding.md](game/pathfinding.md); footprint clearance/water costs come from nav queries.
Heights use the character's (or clicked) Y to pick stacked floors; long paths have a provisional tail.
A character out of the camera's view steps on twice the tick time, as the original's off-screen update time does
([game loop](game/game-loop.md#update-time-off-screen)); without a camera (`World.View` null) the time is the tick's.
A walk's end is `CharacterCold.WalkEnd`: `Finished` (the path followed to its last point), `Failed` (no path, blocked past the limit, stopped at a locked door) or `None` (still walking, or another system stopped it); the AI's arrival reads it ([isAtLocation](game/ai-tasks.md#planner)).
Squad followers follow the original ([squads travelling together](game/pathfinding.md#squads-travelling-together)): `Formation.Layout` gives each
member a slot by its faction's `squad formation` (`World.FactionFormations`, RANDOM without data), kept in `Squad.FormationSlots` and rebuilt in
`MovementSystem.Inputs` (serial) when the formation, head or members change; the draws are keyed by squad and that key. A squad mate aims at its slot
turned onto `FollowHeading`, one second of the leader's travel ahead; anyone else (a follow order across squads, a contract's mission target) keeps
20..40 from its target. `FollowSpeedFor` is the original's desired speed. Engine choices: it is capped by the top speed only (not the speed mode);
a squad's slaves go with the head (Meitou's slave owner is a faction); CARAVAN side members are placed even without a SQUAD_2 member; the building-door
aim (`ShutOutAim`) counts a building as shut when all its doors are locked and whole, and takes the outer of the door's points (15 either side of the leaf) as the side away from the leader; within `DirectDistance` 50 it steers straight, farther, or for `FollowPathSeconds` 3 after a blocked step, it takes paths.
A `FollowOrder` (the context menu's Follow, order 31) makes a character follow another the same way (`CharacterCold.FollowOrder` replaces the leader; a leader following a squad mate makes that mate the formation's head) until a move order replaces it or the target is gone (then Idle); not saved.
Body contact (engine choice): standing bodies are solid over `BodyShare` 0.6 of the footprint (2.4 for a human). After its step, each
character takes half the overlap with each separation neighbour's last position (not one predicted from its velocity, which in a jam is wanted, not achieved), averaged over those it overlaps (a sum shakes a packed crowd); its own velocity into a body it already touches is dropped (pressing ranks would squash the front). A walker
shoves a stander aside; one in a bed or cage does not give; a sitter (resting or on a used seat, no path) is
never moved, by contact or separation (**Observed**: the original pushes no sitter), and is not solid (walkers steer past), lying and carried bodies are not in the way, a character closing on its spot
is not stopped, and an unwalkable result falls back to its own step.

## Navmesh

Own DotRecast builder/cache (`Meitou.Simulation.Navigation`), not Havok runtime. Default cells 2, height 0.5,
288-cell tiles, 8 a zone side (a building interior is one tile), agent height 18/max climb 5 (two cells connect over a shared free space of 18 − 5); ground/water/door
areas. A* checks portal clearance (radius per wall end, default radius 4), water factor and doors. `NavAdapter` exposes simulation interfaces;
`ZoneGate` holds population until ready. Collision and world placement rules:
[collision.md](formats/collision.md), [pathfinding.md](game/pathfinding.md).
Cache key: the zone's building hash (id, position with height, full rotation, destroyed, layout names of the buildings near it), the build settings and a content hash (`NavContentHash`, by name, size and write time: the data
files in load order, a fingerprint of each code mod's record changes in the [data phase](modding.md#data-phase), `seeds.def`, the height, blend, biome, map feature and overlay maps, every zone, level and interiors file of every
level layer, every collision file a record names (collision and boundary fields, any `.xml`) as the install resolves it (missing ones too), and the DotRecast version), so a mod
update that changes any of them builds the meshes again. The cache holds the zone's own pruning; the kept set of every loaded
zone is resolved again whenever one is published or unloaded (the border rule toward unloaded sides, the neighbours' kept edges, `KeepAt` points;
[zone borders](game/pathfinding.md#own-builder)); the flood, the interiors' carry and the fallback region never pass an exterior edge under 9 units
([narrow portals](game/pathfinding.md#own-builder)). Unloading a zone skips its queued build;
a running one is still cached but not published, and its task is cancelled. A zone's tiles build in parallel on every core but one under a
loading screen and on a quarter of the cores in play (`NavMeshService.Covered`): **Observed** (allocation trace, coming down into unbuilt zones) the tile
builds allocated ~13 GB in 5 s and, on every core, starved every simulation system and the render thread into 100-180 ms frames.
A tile's heightfield stage is ours (`NavTriangleRaster`, `NavSpanField`, `NavTileSpans`, `NavCompactField`: rasterization, the two span filters, the
compact field; adapted from DotRecast, same output) in arrays rented from `ArrayPool<T>.Shared` for the tile: a rented array is longer than its
count and is the tile's only until it is built. DotRecast's own allocates an `RcSpan` object per rasterized span, a builder object per compact span
and its per-column arrays (294² columns a tile, ~0.7 MB each, so on the large object heap) for every tile, the span objects referenced until the
tile's compact field is built. **Observed** (2026-11-19, Ryzen 5600X, 32 GB at 78-88 % memory load, play benchmark of
`bench-bases-2026-10-27` from an empty navmesh cache, ~165 s of play, 65-88 zones built; DotRecast's stage, then ours): 124 -> 79 GB allocated, gen0
pauses 11.5 -> 1.8 s, gen1 20.4 -> 1.7 s, blocking gen2s 2 -> 0, frames drawn 5203 -> 7012; with `System.GC.HighMemoryPercent` at 80, under
that load ([engine.md](engine.md) "GC"), blocking gen2s 27 -> 1 (22.4 -> 0.8 s) and frames over 500 ms 34 -> 3.
The cache files of the 78 zones built by both heightfield versions are byte-identical. DotRecast's region stage accounted for about 40 of the 79 GB.
The tile region stage adapts DotRecast's watershed and monotone partitioners. Each tile rents one array for region IDs and, for watershed,
distances. The unblurred distance buffer is reused for expansion. Each worker reuses its level stacks, expansion updates, sweep buffers, region
records, adjacency lists and compact span builder. DotRecast builds the contours and polygon mesh from the resulting compact spans.
**Observed** (2026-11-19, same benchmark, two interleaved pairs, DotRecast's region stage then ours): 73-74 -> 37 GB allocated (DotRecast types
29-30 GB -> 0.3), gen0 collections 658-679 -> 269-272, gen0+gen1 pauses 2.2 -> 1.6-1.7 s, frames and segment times within the runs' spread.
Zone cache files from cold runs of both versions are byte-identical, except zones 28,29 and 49,14, which come out in one of two versions from
run to run with either version.

## Animation

`AnimationSystem` produces named layers/weights/times; `AnimationLibrary` supplies race clips,
lengths, masks and selection data. Movement, held care/lying clips, melee/ranged actions and hit
reactions use researched animation rules. Root motion advances with clip time; relocation keeps
separation by footprint radii and drops blocked components. Rendering consumes snapshot layers;
shape/skinning belongs to the character renderer. [animation.md](animation.md).
`PursuitSystem` sets `CharacterCold.CombatRunning` by the original's rule (body gap over `CombatTuning.RunGap` 90, off under
`FootworkGap` 70, on while the target flees; footprints stand in for the radii, the navmesh-blocked chase is not modelled). Running, a
fighter keeps the plain walk/run clips, full speed and the drawn weapon; otherwise it takes the combat clips and the signed speed, capped
at `FootworkSpeed` 18 ([combat](game/combat.md#combat-stance)). A move or follow order ends the character's fight (`CombatSystem.Inputs`:
target, stance and drawn weapon dropped), as the original's new task and movement goal do; it fights again only when attacked once idle.

## Bodies

`BodySystem` / `FirstAidSystem`, `Meitou.Data.Gameplay.Bodies`: race/stat/part factories,
medical tick, blood/hunger/parts, knockout/death, healing, limb state, move speed/encumbrance and
moving XP. Combat hits use the same body state; avoid double medical ticks. Treat/rest/medic
orders find eligible still patients, walk to reach and apply aid serially. The medical time is the step's dt plus the
update budget's turns ([character-stats.md](game/character-stats.md#time), which owns the original rules).

Limbs: `HealthPart.Limb` / `Replacement` / `ReplacementItem` per slot; saves round-trip them ([save.md](formats/save.md#limbs-medical_state-limbs)).
A severed limb queues (character, limb, drops) on `CharacterTable.LimbsChanged`; between ticks the population system rebuilds the look
(`Loadout.Limbs`: stump or prosthetic mesh, natural limb hidden) and drops the race's severed-limb item (Ground items), except for an eaten
limb: the sever's third argument gates the item, 1 from a blow, the medical tick and the rack, 0 from being eaten (`FUN_14064f300`,
`FUN_14064f5c0`, `FUN_14029af90`, `FUN_140650370`, **Verified** by decompile). Prosthetics are
fitted and removed by holding them onto or off the limbs window's slots (a `*_replace` section takes a LIMB_REPLACEMENT of its side on a stump; lifting a fitted one leaves a
stump; [held item](#items-and-eating), rules in [character-stats.md](game/character-stats.md)) or by a right click (`QuickMoveItem`).

Ground items: `World.GroundItems`, loose items: the save's (zone `1-itemlist`, read and written by `Saving/GroundItemSave`,
[save.md](formats/save.md#zone-files)) and the severed limbs combat drops; at most `GroundItems.Limit` dropped in play (default 1024, up to 4096 by the Advanced setting "Dropped items kept"; the oldest of those goes; read ones
and a camp's are not capped); published with a revision. A dropped limb lies 5 units to its side of the character, an item dropped from the inventory 3 units ahead (nearer, down to its feet, while the spot is not one the character could stand on within 4 units of its own height: the simulation knows the height of walkable ground only, and off it answered the terrain under a walkway or a wall's top, **Observed** on a town platform: 37 to 40 below and 78 to 90 above within 2 units of the walkable edge; the original casts a ray down 99 from 25 above the point, [carry-loot](game/carry-loot.md#dropped-item-placement)), both at a random
yaw and on the ground under that spot (`World.Walkability.GroundHeight`, not the character's own height). The original (`FUN_14064edc0`, **Verified** by decompile) makes the RACE `severed limbs`
item of the cut slot (`SeveredLimbItem`, an `Item` subclass) at the character's `bone name 2` bone of that part: position and orientation from the bone, in-world mode set
through the item's vfunc +0x228 (`FUN_14075d9b0`; its vfunc +0x2d0 `FUN_14075d660` stores them at `+0x48` / `+0xb0`; then `FUN_14075db20` and `FUN_14075e5d0`, which creates the physics object and toggles it by the item's `persistent` key, 1 to 3 only (limbs: 0),
**Observed**), and its PhysX object (a dynamic capsule from the item's `physics file`, [collision](formats/collision.md#item-physics)) falls from there. Engine choice: the host draws that fall (`GameHost.UpdateFalls`, display only; the simulation keeps the rest point): from the stump's bone (`bone name 2`, sampled once its character is drawn), free fall at 98.1 from rest, a drift to the rest point, then a 0.25 s tip; the placement comes on landing (none in the animation sandbox, where the piece stays drawn). A dropped item that is not a limb is placed by the original's rule ([planted weapons](game/carry-loot.md#dropped-item-placement), `GameHost.PlacedPose`). A limb the host lays as it would rest
(`GameHost.LaidFlat`): its mesh bounds' thinnest axis up (the severed limb meshes are plain static meshes lying along +X, bounds read equal to their vertices, **Verified** on every `severed limbs` mesh of the install's
merged data), its yaw, tilted to the terrain slope under it (the heights round the drop point over the item's own size; not on a building's floor, where the simulation's ground is used flat), and set down so that none of a
5x5x5 lattice of points of its bounds is below the rendered terrain under it. A weapon, modelled along +Y, lies on the flat of its blade. Read items are drawn by their saved position and whole rotation. The host adds each as a runtime world-object
placement of its record's `ground mesh` / `mesh` (else `steel1.mesh`, as the original; a bare weapon's `bare sword` not modelled; `GroundItemSync`, `WorldObjectLayout.Item`). A right click on one (on its collision where it is drawn,
[collision](formats/collision.md#item-physics); pointer `pick_item`) gives `PickUpItemOrder`: the nearest able selected character walks to it and, within 15 units on the ground plane
(`PickUps.PickUpReach`) and under 20 of its height (`PickUps.PickUpHeight`, the original's `isAtLocation` height, so not through a floor), or once its walk to the item has finished whatever the height (`isAtLocation` also holds when the mover is done: an item on a high shelf is taken from the floor, #990), puts it in its inventory (`Inventory.Add`), or when it does not fit, wears it in its free equipment slot (`InventoryCommands.TryEquip`; a bag in a free `backpack_attach`, `TryWearBag`); what does not fit
stays on the ground. The Controls setting "Loot into backpack" (`World.TakeIntoBackpack`, off by default: the original has no such option) puts a player character's pick-ups into its backpack first, and its right-click takes from loot, containers and storage as an open bag window does (`BagFirst`). Another order or a stop ends the walk. A picked-up read item keeps its ITEM record's keys in its inventory record. Engine choices.

Town interiors' loose items (`Items/ItemSlots`, the original's rules in [loose items](game/factions-squads-towns.md#loose-items-in-buildings)): each ITEM_PLACEMENT_GROUP of a town
building's interior layout (`TownBuilding.ItemSlots`, id `<building>/item<n>`) is a slot registered with the town's fixtures. A due empty slot in an active zone gets one item
(weighted pick, a weapon with a manufacturer and model, armour with the grade quality of the owner faction's armour chances: engine reading) laid flat at the slot, turned at
random for `random yaw`, owned by the building's faction (`GroundItem.OwnedBy`) and not counted against the limit; when it leaves the ground the next is due in an int of
[min, max] game hours. Builders fetching materials skip items another faction owns; taking one is stealing, as any owned loose item. Engine choices: slot items are saved as plain
loose items (`ownedby` null); an empty slot's due game hour is saved as `meitou slot due item<n>` in its building's GAMESTATE_BUILDING (`ItemSlots.WriteDue`,
[save keys](formats/save.md)), and a slot saved empty waits for it (capped at its longest respawn time from the load) and claims nothing; on a reload each other slot
takes the nearest saved item of its group's records lying within 2 of it in XZ and 10 in height, and nearer to it in 3D than to the building's other slots (shelf spots
lie closer than 2, two in the Hub's bar 1.6 apart, **Observed**, and shelves stand above one another); a save from the original has no such key, so a slot emptied in
it is due at once after the load; upper-floor items are not hidden when a lower floor is viewed. The original, loading such a save, generates the interiors again beside those items.

## Items and eating

`Inventory`, `ItemFactory`, `FeedSystem`: grids, stack limits, backpacks, quantities/charges,
load and eating. ITEM `stackable` is an integer, not boolean; gear stacks cap at one.
Transfer/split/top-up through inventory APIs. Eating follows the original's auto-eat (own items, backpack,
squad mates' backpacks; bites of 50) and NPCs are fed by their hunger floor, not rations:
[character-stats.md](game/character-stats.md#eating). Container bonuses: [economy.md](game/economy.md).

Inventory moves validate snapshot identity, bounds, occupancy, stack limits and equipment
type/armour slot. Weapon sections are 10×2 (back) and 7×1 (hip); other equipment slots hold
one compatible item (the belt: an ITEM with `slot` 14, the lanterns, [lights](formats/lights.md#carried-lanterns)). These are engine constraints, not a complete original-game rule set.
Backpacks move intact between pockets and their attachment slot; a bag goes inside a bag only empty. Equipment moves refresh worn appearance and combat gear; pocket armour
does not grant worn bonuses. Race limits apply to every equipment and prosthetic slot of a character (drag, right click, trade, loot, captive clothing; `ItemInstance.WearableBy`, rule in [holding an item](game/ui-screens.md#6-inventory-trade-dialogue-build)); worn items are never taken off for it. `InventoryCommands.Arrange` and `Inventory.AddTo` follow the original's ARRANGE and auto-add; a limited storage's
stacks start from 1 (`Inventory.Limited`). Rules: [holding an item](game/ui-screens.md#6-inventory-trade-dialogue-build).

Held item (`Player/HeldItems.cs`, `HeldItems`, owned by `PlayerSystem`): `PlayerState.Held` is the item on the mouse with its origin (window `ItemOwner`: a character, the open trade's stock
or a site; its root inventory, section and cell) and the character holding it. `LiftItem` takes it out (the whole stack or one unit; a fitted prosthetic comes off; a machine slot's
units are made into an item), `PlaceHeld` places, merges or swaps it (the swapped item becomes the held one), `GiveHeld` adds it to a character in range, `DropHeld` puts it on the
ground (own windows only), `ReturnHeld` puts it back (old cell, else `Inventory.Add`, else after arranging, else the ground; for a live animal both adds leave its grid out and only its bag is arranged). Stock goes only to the customer and is bought when placed
(`TradeSystem.AffordHeld` / `PayHeld`, a stolen sale's `CaughtSelling`); out of the open loot it is marked stolen as a loot is and may butcher the body (`CarrySystem.TookHeld`); out of
another faction's container it is marked stolen. It goes back each tick when its window closed or its holder is down or gone (`KeepHeld`) and before a save (`World.ReturnHeldItem`,
`SaveCapture.Capture`). Hashed (record and quantity), not saved; the snapshot publishes `Held` and the serial of the last held command applied (`HeldSerial`).

## Combat

`CombatSystem`, `CombatDatabase`, `Fighter`: attack scheduling, equipment/techniques, stance,
block/dodge, armour and per-part damage, stun/KO/death, combat XP. Read previous combat state;
ordered effects apply hits. `TickMedical=false` with BodySystem, `SelfApproach=false` with pursuit.
A fighter faces its target only while it has no pursuit path; on one, movement steers (facing the target each tick
undid the turn to a waypoint over 90° off the line to it, and the walker froze).
Original formulas, constants and unresolved technique terms: [combat.md](game/combat.md).
Hits, pauses and initiative follow combat.md "Hit reactions" / "Pacing"; the combat system picks the stumble
clip (through the animation system's chooser) and holds the fighter for its length. No wait before the first swing, a swing with nobody in its attack zone waits 0.5 s and the loser of the
initiative roll 0.5 s; each blow (its packet times the hit's `power`) resolves against everyone in its attack zone, slaves in the original's case and walls and
doors included (`CombatSystem.Strike`, `ApplyZoneBuildings`, combat.md "Attack zone"). Fighters in their footwork face their target and side-step
([animation](animation.md#side-steps)). Engine choices: the initiative roll is drawn per pair and tick so both
sides agree; a target that would not swing back (stumbling, defending, not fighting, or in
Block) is simply attacked; one that lost the roll to a fighter becomes its target; a defence that starts late
joins its clip partway so the passed roll counts when "Late dodges and blocks" is on (Advanced setting, a balance
change: off by default and in both presets);
the attack technique, attack slots, the 5-fighter engaged list and circling follow combat.md "Attack technique choice" /
"Attack slots and circling" with the engine readings listed there (Advanced setting "Attackers per target", 1-5: Faithful 1, the
CONSTANTS value and the default; Meitou 3); the block break-out ignores the 10% lower bound; only a heavy bolt staggers.
Close in, fighters move by the original's force-based fight steering (combat.md "Fight steering"), not paths; from afar or without a clear line they walk paths to their technique reach (`CombatTuning.StandGap`). A landed or
blocked blow takes a defender fighting the attacker off the attacker's engaged list (`FUN_14060a2f0`), so it asks again.
Target choice (`CombatSystem.ChooseTarget`, combat.md "Target selection"): a fighter whose task target goes down ends its fight at once; one whose other target goes down
chooses again at once; while deciding it chooses again every 1 s (0.3 s on Hold), except for a player's pick. With no candidate, or when even its current target fails (a holder's
that left the post test, a player's beyond 400), it fights on without a target until nothing is left (combat.md "Leaving a fight"). Candidates are its attackers, its threats and, for a bodyguard (row 45), its protectee's attackers, each known to it or the protectee (a sense entry) (+10 for one the protectee does
not fight back that has under 2 attackers); a crippled one is left out of these lists, and their best must score above -100; not of its faction or squad (the current target excepted).
A squad mate's attackers are no candidates. With none, the enemies its senses saw or heard at their last look (`CharacterFacts.Senses`, perception.md "Senses"), the current target
among them with its stubbornness; an NPC's chase may give it up sooner (`AggroSystem`, 24 s unsensed). "Intended aggression ≤ 0" is a fight-off candidate not counting the chooser's faction an enemy (engine
reading); getting up is `CharacterCold.GettingUpUntil`, from waking for the ragdoll blend (only after a whole-body ragdoll) plus `CombatSystem.GetUpClipSeconds` (`standing up 3` to `GetUpEnd` at its play speed; none when carried or in a bed),
during which it neither walks, works nor fights (Task_GetUp, the act-out of the get-up order 65 / 52, [order queue](#order-queue); it counts as staggered for the work systems).
Only a fighter blocks or dodges. A landed blow on a shackled defender knocks it out 25% of the time (`PutDownKoSeconds` with 0.002: 3 s). A hit on a
character outside a fight that is getting up does nothing more; on any other (not a gunner) its task ends behind the Stumbling order 147 and starts again
afterwards (a lock's, site's or machine's progress stays with the object), and a picked stumble clip stops its walking and its work
(first aid, lockpicking, building, operating) until the clip ends (`CharacterCold.StaggeredUntil`). A fight's end puts no weapon away: a player's character sheathes once it is neither walking nor following on an order,
an NPC by its planner's UNEQUIP_WEAPON step (one without an AI at once).

### Ranged

Crossbow reload/steady aim, skill spread, bolt flight, ammo, obstruction and friendly hits are
simulation state; `Schedule` advances bolts. Targets without medical bodies do not stop bolts. Bolts and the line of fire stop at the
terrain heightmap and the buildings' collision triangles (the ragdolls' environment, `SolidHits`), not the walkable surfaces (a roof over
the shooter counted as ground). A building is there once its zone's collision has been read on a worker (about 0.1-0.2 s a zone, headless, Black Desert City),
and a zone loads only after that, as in the original ([zone loading](game/game-loop.md#zone-loading-and-collision)), so the loaded zones'
buildings are always there. Collision still being read (a zone not loaded) holds the line of fire's shot, and the closing ray, the melee alert
line and the sight ray (`SightRay`) count it as blocked; a flying bolt passes it, as the original's flight ray meets only what its scene holds
([flight collision](game/combat.md#shot-origin-and-flight-collision)).
A crossbow reload paused by the fight's end or a switch to melee resumes where it stopped (as the original's controller timer,
[combat.md](game/combat.md#turrets) fire gate); in a fight without a target the controller stays on (state 3) and the reload counts on.
Bolts and the line of fire hit each character's click hull, an axis-aligned box from the race's `hull size` (`CombatSystem.HullHalf`); the line of fire and the closing
ray follow the original's tests ([combat.md](game/combat.md#line-of-fire-and-the-hit-shape)). Engine bounds: the line of fire looks the loaded ammunition's range on (the crossbow's `range` for base data; at
least to the aim point), the closing ray `RangedTuning.RayReach` (2000); the original's rays are unbounded. Not modelled: the aim clip's blend before the steady time
counts, the near-camera gate on aiming, and the controller's own target choice (Meitou's crossbowmen choose as fighters do, every second).
Ranged state is hashed when used. Weapon/turret research: [combat.md](game/combat.md). A data mod's ammunition lists and per-ammunition bolt
fields (Meitou): [modding](modding.md#registration-surface), "Ammunition".

Turrets (`CombatSystem.Turrets.cs`, the gunners and gun state in `TurretManning`, facts: [combat.md](game/combat.md#turrets)): one serial step after the bolts fly finds the gunners and runs each manned turret in key
order: target choice, reload, aim point while loaded, turning the parts (`TurretRig`), and the fire decision (in range from the gunner, aimed within 1°, steady 0.4 s, line
along the barrel clear); harpoons are the gunner's bolts (hits, XP, crimes). A gunner is a player character operating its turret or a town's (`CharacterTask.Operate`, below; a town turret is a `townwork:` machine made on the
first order, `ProductionSystem.TownTurret`) or an NPC standing at the turret its squad claimed (AI turret goals); one per turret, the player's characters first (engine choice: a
player gunner takes a town turret from its NPC gunner, who stands by), then the lowest slot. A town turret's state is keyed by its building for either gunner. `CharacterCold.Gunning` names it (derived); the aggro system starts no fight with a
turret guard, and a gunner's self-defence takes no shooter (`AiSystem.SelfDefencePick`, the engine answer alike). Turret state (angles, aim, loaded harpoons, reload, target) is hashed once a turret was manned; only the loaded harpoons are saved
(`current shots`, as the exe; [save](formats/save.md#faction-trust-war-flags-bed-rent-and-turrets)), read when the turret is first manned after loading. Engine choices: town turrets have efficiency 1 and no research multiplier for any gunner, and are always powered (no NPC power grid); a player turret's efficiency is its machine's power grant
(0 unpowered) and has no mount bonus; the reload belongs to the gunner (a new gunner restarts it); every candidate within 1.5 × range counts as sensed and seen; "fights the
gunner" is the × 1.5 (`FUN_1405ca6f0`); a down target is "slot 0x368"; the turret's facing is its yaw; no Shoot first pass (no such option); the muzzle is the last turning
part's; the turret's own collision is not left out of the line or the flight; the shot sound is a pooled object at the muzzle. The snapshot carries the part angles
(`MannedTurret.Angles`) and the renderer turns the meshes by them ([render-objects](render-objects.md#turrets)). Meitou fix (Fixes page `Turret gunners turn`, on by
default): the turret step sets the gunner's yaw along the barrel every tick; off, it faces the turret as `Task_UseTurret` does (player: the building's place; NPC: its yaw).

### Animals

`ANIMAL_CHARACTER`, animal body/stats/size/fists and creature techniques come from typed data;
animals use faction hostility and carrying/damage rules where eligible. They age and grow, pack beasts spawn with their
bag on, DA 48 sells them, and the context menu's Follow keeps any player character beside another
([animals](game/animals.md#ageing)). Player animals feed themselves: food on the ground (259, `FeedSystem.GroundFood`), food from their outpost's stores
(243, also for people, `TownDutySystem.FoodWant`, [rules](game/character-stats.md#eating); both decision-step goals for player characters) and their race goals: limbs (277), limb play and chase
(278, 279) and eating downed non-allies (233) as [captive jobs](#captives) (`CaptiveSystem.RaceGoals`: decision-step goals, priority 3 at step 4, 233 priority 2 at step 6, by the original's desires x the entry's weight), ended by an order that runs. Corpses (230) are dropped from the player's animals by the original;
crops (232) find nothing for them (only the player's farms are machines, and the eater must not get along with the town). Unarmed humans use
martial-arts category 5. [animals.md](game/animals.md), [characters.md](characters.md).

### Hostility and relations

`AggroSystem`, `RetaliationSystem`, `FleeSystem`: previous-position sight/hearing, faction
relations, shared targets, player self-defence as a [decision step](#decision-step) goal ([order toggles](#order-toggles)), chase give-up and return to routine. The look keeps every
living character's senses (`SenseBlock`, read through `CharacterFacts.Senses`; [perception](game/perception.md#senses)): entries, the `numEnemies` / neutral / ally counts, the threats
list and the threat sums. The fights read them: target choice (`CombatSystem.ChooseTarget`), Self preservation's threat level and the immediate threat (`CombatSystem.ThreatLevel`,
`ImmediateThreat`), the urgent turret job (step 3), the chase give-up, playing dead, the arrival lines and IS_OUTNUMBERED. Then, for a player character that may attack on sight (Hold's 16, the 16 job), the
look picks the best `Combat.TargetScore` among the entries it saw or heard (the original's attack-on-sight finder), not the nearest, and offers it to it and to its free mates whose own look takes
it (`AggroSystem.Takes`). An NPC with a mind that senses a new enemy only asks itself, and its squad mates within twice their own R of it
([squad-shared sightings](game/perception.md)), to think at the next tick: the combat goals decide in the chooser ([AI](#ai)). An NPC without a mind starts no fight on sight. Without the Aggro part
the look keeps the senses only.
Being taken as a target alerts the same tick ([being targeted](game/combat.md#fight-lifecycle-and-orders)): the attacker is a threat of its target at once (`AggroSystem.Force`) and an NPC with a
mind thinks at the next tick, where its 32 Self preservation fights back (row 13) or runs ([AI](#ai)); one without a mind gets the engine's answer, that tick and every 15 ticks after. Others join
only by their own goals: an NPC by 143, 15 or 288 in the chooser, whose squad mates (and the attacker's) the alert asks to think
(`RetaliationSystem.AskProtectors`); a player character by its goal 15 ([decision step](#decision-step)); followers and bodyguards by their rows. So gate guards fight the Skimmers
(relation 0 to every faction, `ATTACK_ENEMIES_AND_NEUTRALS`) that attack townsfolk.
Orders defer to the next tick. Relations are live state and deed/witness effects have distinct
paths; preserve current ordering.
Relation events (`FactionDeeds`, built with the aggro part; [factions 3.5](game/factions-squads-towns.md#35-what-fires-relation-changes-observed-decompile)):
every landed melee blow and bolt, the cagings of the player, the police and kidnappers, and first aid on an unconscious or crawling member, for
any two factions, weighted by the victim's `faction importance` and the town it stands in (`PopulationData.TownAt`, its squad's
`Town`). The striker's rating is its goal's intended aggression (`FactionDeeds.Rating`: the mind's goal in a fight its AI started, else the fight's row, a defence 0). Engine choices: a death is credited only at the killing blow (a later
bleed-out has no killer); first-aid gratitude on every bandaging update. The attack order and looting change no relation. A
witness that regards the culprit as friendly (slot 126, squad tags included) and cares about the victim takes 20 off once: its squad drops TEMP_ALLY on the culprit and tags it TEMP_ENEMY, so it no longer regards it as friendly (`CrimeSystem.Betrayed`; **Verified**, the witness function `FUN_140854d10`'s ally branch, tags 3 and 4 before the relation call). Testing the faction relation alone, every witness took 20 off every sweep and a town's allies went to war with it within seconds (#948). Wars:
`PlayerRelations.DeclareWar` / `EndWar` (one direction, the speaker's table; the war flag is not saved). Campaigns and diplomats in the aggro
(`AggroSystem.Regards`, `FinderDrops`, `LeaveAlone`; [ai-tasks](game/ai-tasks.md#combat-goals)): an enemy Diplomat is tagged NEUTRAL by a player-side looker, and by
others inside its faction's `biomes`; 16 skips members of a campaign in phase 0 and of a unique platoon; every scored target choice (the fight's, the finders', the
assault's) leaves alone, for an NPC chooser, a non-hostile Diplomat or phase-0 member its squad holds no TEMP_ENEMY on, and for a player-faction chooser an unaware,
conscious one of another faction not after one of its friends; a turret scores a Diplomat that intends no aggression 0. Original threshold/sense/retreat facts:
[perception.md](game/perception.md), [AI](game/ai.md), [factions](game/factions-squads-towns.md).

## Carrying, rescue and looting

`CarrySystem`: serial links, pickup/put-down, dropping on KO/death/attack, rescue jobs and inventory
looting. Carried bodies follow the carrier and affect load. A lift or loot, and an item pick-up, walks to a floor point within 20 of the body's or item's height (`MovementSystem.FloorApproach`: the floor under it, else
the nearest floor point within 12 round it, asked at the body's height and then lower) and reaches it within 14 on that floor; a body or item on a counter, table or shelf, over the hole its furniture
cuts in the floor, is reached from the floor beside it, not from the storey above (engine choice). The player's Pick Up also lifts an awake own character that carries no one (`CarrySystem.CanBeLifted(World, …)`);
a Pick Up (Ctrl + right click) on a prisoner in a locked cage lifts no one and posts "The cage is locked." (engine choice; the original's menu offers no pick-up then);
lifted by a friendly carrier it leaves its fight and task and rides (`CharacterCold.CarriedAwake`) until put down. A body carried awake (lifted so, or come round on an enemy's
shoulder) neither fights nor treats anyone, itself included (engine choices; `FirstAidSystem.FindPatient` / `FindSplint` skip a carried medic); a body lifted down is still set
down when it comes round. Loot can raise witnessed theft and
stolen ownership marks. [carry-loot.md](game/carry-loot.md). Loot window (engine choice):
`OpenLootOrder` walks the looter over (one ordered mid-fight leaves the fight, as with `LootOrder`; one already in reach opens at once, paused too), then `PlayerState.OpenLoot` publishes the body's whole inventory
(`WorldSnapshot.OpenLoot`, the body's `ItemGrid`: grids, bag and worn gear); items are held across with the mouse ([held item](#items-and-eating)),
or right-clicked over (`TakeLootItem` / `PutLootItem`, `InventoryCommands.MoveInto`; taking counts as a loot for theft, locked-on items stay, moved gear re-dresses both); it closes as a container does, or when the
body comes round (another faction's); a carried body stays open (lootable, as the original's down test: carried counts) while the looter is in reach. The same window opens on the player's own standing character (Trade, `CarrySystem.CanBeOpened`); both characters' bags open with it, the windows in one
row (**Observed**, owner's screenshots of the original, 2026-10-24; Meitou: limbs, own bag, own character, theirs, their bag, centred, and a bag the window opened closes with it unless OPEN BAG was pressed); a death item taken butchers only a down body. An animal's inventory and loot windows use `Kenshi_InventoryAnimalWindow` (backpack slot,
main grid, grid size from its `inventory w` / `inventory h`; **Observed** in the original); its data panel follows the faction rule
([ui-screens](game/ui-screens.md)): weight and burden only for another faction's animal, the Disguise and slave rows too for the player's own (**Observed**, a Garru,
a Pack Beast, an Iron Spider and a Bonedog of the player's squad). A live animal's window (the player's own, or another faction's
not down; Trade on an own animal too) ends above the grid ([animals](game/animals.md#death-items)). It also runs the stealth knock-out
(`StealthKo` task, reusing `CarryTarget`/`LiftTime`): [combat](game/combat.md#stealth-knock-out).
- **NPC rescue to a bed** (goal 148 Find and rescue if there's beds, facts: [carry-loot 3.2](game/carry-loot.md#32-goals-and-their-target-finders)):
  on the `AutoEveryTicks` beat, after the rescue jobs and the leader rescue, an NPC the AI runs (not an engine mind, not a player character) whose
  goal lists hold 148 keeps the goal's factor for its think (`CarrySystem.RescueToBeds`, `AiMind.BedRescueWant`, hashed while set): 1 while it is not
  on Hold, carries nothing, the rescue finder (`FindRescue`) finds a downed ally and the bed search a bed for it, else 0. The bed search is the
  carrier's (`BedSystem.FindForCarrier`): it leaves out beds other carriers are on their way to, and a squad with no town takes only its own or an
  ally's beds within `BedSearchRange`, as its sleep search does, and none of the player's (engine choice). The think weighs 148 at that factor times
  its package weight as any goal (`AiBehaviour.RescueToBed`, its own plan) and stands while it is chosen; the sweep then lifts the body as row 148
  (`TaskSource.Goal`) once the AI holds the character (not in a talk, a trade or an arrest), unless someone lifts it already. The carrier's goals then
  take the body on ([town duties](#town-duties)): with 106 listed it lays it in a bed. Engine choices: the factor and the lift wait for the beat (up
  to 15 ticks); the finder runs only while anyone is down, and before the bed search (the same result); a character not able to lift at all
  (`CarrySystem.CouldLift`: no usable left arm, or the row not in its repertoire) does not want the goal, while a fight, a crouch or lying down only
  delay the lift (`CarrySystem.Free`, `CanCarry`), so a resident asleep in its bed wants it too, as the original's desire tests only Hold and
  carrying: once 148 outscores its sleep goal (the resident package's 239 scores 1 x 1 x1.25 = 1.25 against 148's 1.0 while it commits, and at most
  0.05 once its commitment has passed, [ai-tasks](game/ai-tasks.md) "Desire and commitment") its think gets it out of bed and the next beat lifts; a
  body goal 106 would not lay in a bed (a grudge tag, a bounty owed to a police-like rescuer, not regarded as friendly; `BedSystem.WouldPutIn`) is not
  lifted; a walk to the body that has not reached it in 60 game seconds (`CarrySettings.RescueWalkSeconds`) is given up and that rescuer leaves the
  body alone for 2 game hours (`RescueRefuseHours`, hashed while set), as the town goals' lifts do; a body that dies while the rescuer walks to it
  ends the walk, so this goal lifts no body that is already dead when the lift clip starts (one that comes round ends it as any lift's); a body this
  goal lifted that no carried-body goal takes on (no bed found after the lift, or a package without 106) stays on the shoulder, as any goal's end leaves it
  ([town duties](#town-duties)). The leader rescue (142) takes the same ability test, hold test and walk give-up.
- **NPC looting** (`LootGoals`, the finder `CarrySystem.LootTarget`, facts: [carry-loot 3.3](game/carry-loot.md#33-npc-looting)): goal 177 is an [engine goal](#engine-goals) of the
  chooser (desire 1 at posture 0, times its finder's factor; plan `[1, 3, 177]`, row 3 the loot). Every `AutoEveryTicks` on its slot's turn (slot mod the period, so the looks spread over the ticks, as the original's AI updates run 8 characters a frame in turn, [game loop](game/game-loop.md#the-character-update-budget)), an NPC with a mind of its own that weighs 177 (`AiSystem.WeighsGoal`) keeps its finder's pick for the think
  (engine: the original's finder runs at each score); the pick is the nearest dead body (not of its faction), else downed living one of a non-allied faction, within 100 and reachable (no shut gate route between, no building without an open door around it unless the looter is in it, `CarrySystem.Reachable`): the player's only with
  `PlayerState.BanditsLoot` (CAMERA `bl`, default on), only whom `shouldIScrewThisGuyOver` allows (classes, love/hate tags), not one its squad tagged short-term 6
  (`MemoryTag.Looted`). At the body (`Loot` task, `LiftTime` from arrival, `medic` clip) it takes after 8 s (animal 20): weapons when it has none equipped, food (functions 3, 15, the
  backpack's 3), and for a non-player-side or dead body, unless it is a Bandit or malnourished (floor < 2.8), every item and armour; a weaponless non-bandit grabs one weapon of a
  living player-side body at 0.8 s instead. Then the tag, and back to its task. On arrival it says event 33 or 32 and may get a pending Looting (`CrimeSystem.NpcLooting`,
  [looting crime](game/carry-loot.md#33-npc-looting)), which its witnesses answer as any NPC culprit's ([crimes](#crimes-and-bounties)). A police-like looter (`CrimeSystem.IsPoliceLike`) confiscates instead: past 0.8 s one unit of each stolen item of the body's inventory (`CarrySystem.Confiscate`), tagging it only when there was none. Engine choices: nearest per pass, the tag never expires.
  The chosen goal's start loots the plan's body (row 177's loot task, a goal's task); a body another member's act-out works is refused, and the goal is done when the loot
  ends (`DutyGoals.Done`); a loot whose body no path reaches (`CarrySystem.LootWalkFails`), or a pick-up whose walk ended short of its item, is a lost walk
  ([engine goals](#engine-goals)).
- **Unarmed NPCs** (`LootGoals`, the finder `CarrySystem.FindWeapon`, goal 42 "Don't be unarmed" with step 8 FIND_WEAPON, `AI::findWeapon`): an [engine goal](#engine-goals) too,
  desire 2.0, plan `[1, 3, 8, 42]` while the planner's state 4 (a weapon) fails, the finder on row 8; with a weapon the scorer drops it. On the same look an NPC without a
  weapon that weighs 42 keeps row 8's pick: the nearest free ground weapon within 100 (in a town only an unowned or enemy-owned one), else a dead body with a weapon,
  else a downed living non-ally with one (player side only with `BanditsLoot`, not one its squad loves). The start picks the ground weapon up (row 3, `PickUps`) or
  loots the body as above (row 42's loot task); the goal is done when the pick-up or loot ends. Engine choices: the body loot takes more than the weapon; the nearest
  body, not the sensed-list order; accessibility left to the walk.

## Ragdolls

`Ragdolls/RagdollSystem` (game behaviour: [ragdolls](game/ragdolls.md)) runs serially after animation
when the host passes `CharacterRigs` (the game does; tests and goldens do not). Engine choices:
- **Files**: a race's ragdoll `.phs` is read once per path (one a race and sex) and shared by every look; read per look, each character streamed in
  read it in the tick that first asked (`AnimationSystem` asks `HasRagdoll` of every character), most of the long ticks' file time (**Observed**, CPU trace
  of the play benchmark's flights, ~1,400 characters: 141 ticks over 25 ms before, 99 after).
- **Physics**: BepuPhysics2, one scene stepped on the calling thread (deterministic, 2 solver
  iterations, 4 substeps per tick, fallback batch threshold 1024: Bepu 2.4.0's fallback batch throws
  when its last contact on a dynamic body is removed with one on a kinematic body, which big battles hit at the default 64). Bodies and joints come from the race's ragdoll `.phs`
  (`RagdollDefinition`, scaled by the animal size); revolute → hinge + twist limit, spherical →
  ball socket + swing + twist limits, fixed → weld. Joined pairs do not collide.
- **Parts**: `Whole` when incapacitated, `CarryMode` when carried, a human's downed limbs by their
  LOCATIONAL_DAMAGE `collapse part` (not robotic ones). Bodies outside the mask are kinematic, driven
  by velocity to the animated pose. Animation holds its layers while the whole body is a ragdoll.
- **Carried bodies**: `CarryMode` simulates the forearms and the head only, as the original does ([ragdolls](game/ragdolls.md#bodies-kinematic-or-simulated)); everything else follows the `carry me` clip. Ragdoll bodies turn at most 7 rad/s and the head's neck joint is sprung ([ragdolls](game/ragdolls.md#limits-of-motion-and-joint-springs)).
- **Published pose**: `CharacterSnapshot.Ragdoll` (model-space `BonePose` overrides, weight for the
  get-up blend); the renderer poses with it. While `Whole`, the character's position follows the root.
- **Scene**: 32-unit tiles round each moving ragdoll, terrain sampled every 4 units (the heightmap's samples are 18 apart; the Bepu mesh build is nearly all of a tile's
  cost, 0.45 ms at 2-unit cells, **Observed**), plus building
  collision from `Meitou.Simulation.Navigation.RagdollGeometry` (per zone on a worker, doors and passable parts
  skipped; the 24 most recently asked zones besides the loaded ones, which are kept; the eviction looks only after a zone was added, the
  loaded zones changed or a gathering zone could not go yet: run on every ask, its count under all the dictionary's locks cost the sight checks 1.2-4 ms
  a tick, **Observed** 2026-11-19, play benchmark, interleaved pairs: AggroSystem 1.25-1.52 -> 0.51-0.59 ms a tick at 1x, 1.8-3.8 -> 0.65-0.77 after travel) and the map features' collision (rocks, cliff blocks, Rebirth's statue: each triangle in the zone its
  centre lies in, as features reach past a building's 600; outdoor bodies hit group 14, the features' group (**Verified**,
  [pathfinding](game/pathfinding.md#own-builder) "Map features"). **Observed** (`meitou-tools navmesh --ragdoll`): map
  features add 8,475 triangles to 21.19's 9,617 (Rebirth), 2,702 to 20.32's 12,926, 4,355 to 23.22's 19,844, 792 to 34.46, 304 to 18.39;
  a zone gathers in 70..200 ms. A tile has ground only until its zones are gathered. Tiles unused for a minute (1,800 ticks) go, so a camera flight back to a zone
  finds them. **Budgets** (engine choice; `RagdollSystem.TileBudget`, `StartBudget`, `RagdollPhysics.BuildingUnits`): a tick spends at most 8 tile units (a
  ground mesh 1, a tile's building mesh 4: a town tile's is 2 ms against the ground's 0.12, and a zone's pending tiles all got theirs in the tick its buildings
  were gathered; a tile asking for buildings not yet gathered costs nothing) and starts at most 4 whole bodies falling (a knockdown, a put-down, a stored
  squad's downed member coming back); one whose tiles the budget did not reach, or whose tiles still lack their buildings (`RagdollPhysics.Covered`), starts on a later tick, the character animating meanwhile; a save's frozen
  ragdoll is not counted, and a get-up, a limb's dangle or a lift never waits. **Observed** (headless flight probe, the
  stutter save's flights): a zone's downed characters re-ragdoll when it loads, asked for 80-100 tiles and 20 builds in one tick (45-60 ms; after the budgets
  the setup loop is at most 3.4 ms a tick), and 20 whole bodies then fall for up to 7 s (the step 0.4 -> 1.3 ms a tick); a build is 0.7 ms (bodies, joints, the
  ground probe, which reads heightmap blocks on demand: 5 ms for the first in a new area). **Threads** (`RagdollSystem.ThreadedBodies`): the step runs on the calling thread below 256 awake bodies and on the world's thread count above (**Observed**,
  the probe: single-threaded costs the same below 300 awake bodies, 0.4-0.8 ms, and 0.2 ms more at 1,100; the dispatcher's signals and waits are 0.3 ms a step,
  and its workers, preempted, stalled a step 16-85 ms in the probe's CPU trace). The step's remaining 12-21 ms spikes come with or without the dispatcher and
  with no tile or build work.
  The terrain is the full-resolution heightmap (blocks of 64 samples read on demand), not the simulation's `GroundHeights`:
  outside the area loaded at the start that is a coarse grid (a sample per 144 units) off the real ground by 2.4 units
  at the median and 15 at the 90th percentile over the land, so bodies met a ground the character was not standing on.
- **Falling through**: the ground is a one-sided mesh. A new whole-body ragdoll with a body centre under the terrain (a
  foot on a slope; 4% of starts) is lifted as one to 0.25 above it (up to 8 units); speed is capped at 200 units/s and
  spin at 30 rad/s and the contact margin is 8 units (Bepu, 30 Hz ticks: a thin limb moving more than its radius per tick
  tunnels); a dynamic body that crosses from above the terrain to more than 1 unit under it is put back on it.
- **Hash**: mask, frozen, age, blend and body poses in system order.

- **Save** (Meitou key, [ragdolls](game/ragdolls.md#saves)): a whole-body ragdoll's body poses, frozen flag and age (`Saving/RagdollSave`); the loaded
  character's first ragdoll starts from them when its root lies within 20 of where it stands, else (and for carried or limb ragdolls) from the animation.

Gaps: ragdolls collide with each other but not with characters that have none (as the original: a character's click hull is group 0, which
ragdoll group 3/4 skips, [ragdolls](game/ragdolls.md#collision-groups)); the building gather's
timing can change a live game's hash (goldens have no environment).

## Crimes and bounties

`CrimeSystem`: pending deeds, witnesses, booking, police eligibility, faction ledgers and expiry.
Carry/combat report deeds; serial sweep observes current positions. Bounty expiry uses
`0.04 hours/cat`, permanent at >=10000. Original categories, witness rules, imprisonment and
remaining uncertainties: [economy.md](game/economy.md). Expiry is paused while imprisoned. The police's response:
[Arrest and jail](#arrest-and-jail).
Witness sight: the original's sight test with places and light ([perception](game/perception.md#sight), `SightMap`); a
sneaking culprit is seen only once the witness's spot meter is full ([perception](game/perception.md#spotting-a-sneaker);
the aggro scan's meters, kept for every NPC looker, not saved).
NPC culprits: an NPC's non-defensive attack order, and each of its landed blows, sets a pending Assault (not an
animal's; the victim may be a player character), a landed blow between it and a police-like NPC sets the police-hit Assault on it as on a player character
(`CrimeSystem.Hit`), and an NPC looter its Looting ([NPC looting](#carrying-rescue-and-looting)); both are booked like a player's (**Verified**, the witness function `FUN_140854d10` ends in the booking call with no player test; a Bandit, Diplomat or LawEnforcement class is never booked).
Every witness that sees the culprit reacts, not only the first after the booking (the witness function runs per looker, `FUN_1408580d0`): with a
stop there, a police 1480 off in sight range booked a slave hunter's assault at Sho-Battai's gate and the gate guards 40 away never reacted
(**Observed**, headless, a player quicksave).
The setter itself refuses no NPC code (**Verified**, `FUN_140852c80`): any other crime a caller sets on an NPC (the cuffing task's Enslaving) is pending too and witnessed the same.
An Assault on a Diplomat standing in its faction's `biomes` is set as code 9, Terrorism ([economy 11.6](game/economy.md#116-booking-factions-and-police-hits-observed), `CrimeSystem.DiplomatVictim`). A police-like witness not fighting re-fires the witness event (41 for an Assault or code 9, 42 for a Looting) at it every
sweep, since the guards' lines (e.g. "Shek guard witness crime": DA 29, 33, 6) need it within 260 and not running; not a Trespassing
(an NPC gets one from a DA 33 in a private home), whose event-17 line only restarts a chase forever (#519). So a town's
guards turn on hungry bandits (Starving Bandits, -10 to all: not hostile, so no aggro target) attacking someone there.
Trespassing (`CrimeSystem.Trespasses`, [economy 11.2](game/economy.md#112-how-a-bounty-is-gained)): the sight pass sets it (`SightCrimes`, with
event 17 and the MY_INTRUDER tag); without one, a player character inside (`SightMap.BuildingOf`, not on the roof) a town building that a squad
lives in is a suspect each sweep and a witness that sees it and does not regard it as an enemy runs the same test (engine fallback). The test:
resident (`LockSystem.IsHouseLocked`, the lock-up round's flag; the original also sets it when a resident closes a lockable door) when locked up
and the building is not open (a shop by its record's designation, or `HomePublic`: the player's toggle, else template `public day`; open
7:00-22:59); police-like in another's
building when the residents' faction and leader do not regard the culprit as an ally and the residents have locked up, or the building is not
public and the culprit has a slave state. The witness rules above then fire event 17 and a police-like witness books 100. DA 33 CRIME_ALARM (`CrimeSystem.Alarm`): the speaker attacks the target, the target's crime timer is 30 and it is recently
wanted; a speaker of a town squad sets Trespassing on a target with nothing pending inside its private home, and raises the town alarm for
the target's pending crime, which the town's investigators answer ([town alarms](game/economy.md#town-alarms)).
Stealing (`CrimeSystem.Steal`, [carry-loot](game/carry-loot.md#theft-and-witnesses)): taking from another faction's container (right click: Theft;
drag: Looting; give to a character: Theft) sets the pending crime, then the steal roll; caught, the item stays. Pickpocketing (`CrimeSystem.Pickpocket`):
Loot on another faction's sleeper or prisoner opens its inventory (`CarrySystem.CanBePickpocketed`). Every take out of another faction's
character sets the call site's code (`CrimeSystem.TakeCode`: right click, drag, give) and rolls with the owner watching (a playing-dead owner
too); the take-all (`LootOrder`) keeps the Looting of `CrimeSystem.Loot`. Floors are `SightMap.FloorOf` / `FloorAt`. Engine choices: every squad
living in the building watches (Meitou may house more than one), the window closes when the sleeper wakes. A stolen ground pick-up inside
a building is a pending Theft with thievery XP 20, no roll.

## Cages, beds and locks

`Locks/`: `World.Fixtures` (`FixtureRegistry`) holds every door, `has lock` container, cage (function 8, pole/peeler
`Restrains`) and bed of a town (a destroyed building: no doors or own lock, `TownBuilding.Destroyed`), registered once when the population first spawns the town (`RegisterSite`), kept on
unload, hashed only once non-empty. Facts: [locks](game/locks.md), [economy 11.5/11.7](game/economy.md#115-bail-and-sentence).
- **Player orders on locks and cages** (`LockSystem.Force.cs`, `LockSystem.Cages.cs`; [locks](game/locks.md#player-orders-on-locks)): `ForceLockOrder` (Use Tools / Use Strength / Smash), `SetFreeOrder`, `GetInCageOrder`, `PutInCageOrder.Cage`,
  `PickLockOrder.Cage`; cages are published as `WorldSnapshot.Cages` and a prisoner's `PrisonView` names its cage and owner. Per-character state `ForcingLock`, `BashOrdered`, `Freeing`, `EnteringCage` is hashed only while set.
- **Ids** (engine): a building's own lock = its instance id; door i `<inst>#i`; interior item `<inst>/<layout index>`
  (`TownBuildings` collects them as `TownFixture`s); bed k `<inst>@bed<k>`. Owner: the placement's `owner faction ID`, else
  the town's faction. Lock levels roll from `Rng.Hash(seed, StableHash(id), Lock, 0)`: load order never changes them.
- **Doors**: a building no save has starts from its level-data GAMESTATE_BUILDING (`TownBuilding.LevelState`: lock levels and door
  state; only doors with a `state` key count as restored). `AiSystem` applies the template's `initial door state` when it assigns a home (first squad, not to restored
  doors). `IDoorGate` (the game's `NavAdapter` -> `NavDoors`, reset on `ReplaceWorld`) closes a building for every path while
  all its doors are effectively locked and none broken (engine: one nav door state per building; closed-unlocked doors stay passable).
  Motion (`LockSystem.Doors.cs`, serial `SlowWorld`): `StepDoors` moves opening/closing leaves by `door move speed` per second and
  locks a closed one that `wants lock`; locking a home's doors closes them (animated, the lock holds at once). Engine stand-in for
  Task_OpenDoor: every 3 ticks a walker within 40 of a closed door's leaf whose current or next path leg crosses the leaf's plane within its half
  width + 6 (`IDoorGate.DoorSpotOf`: the door collision's box via `NavDoorGeometry`; headless: none, so no walk-through opening) opens it; at an
  effectively locked one a walker within 15 of the leaf whose current leg crosses it stops ("It's locked." for the player, once per door while it stays within 40 of where it said it) unless it has a way through (a patroller has none at a gate): its own door it unlocks, another's it bashes when it wants to (drawing a melee weapon first), and on its way in an NPC, or a player's character on an order, cuts or picks
  it ([locked doors on the way](game/locks.md#doors)). Both reaches run from
  the leaf's nearest point, not its centre (a gate's leaf is 70..80 wide). The box's normal is the thinnest horizontal direction of the door
  collision's footprint (`NavDoorGeometry.Fit`), not the nearer building axis: **Verified** (that footprint for every BUILDING with `doors`, plain
  game; each leaf is one box), 9 of 47 leaves are turned in their building (Saloon and Double House 45°, Outpost s-II/s-IV and Outpost Stairs 42°,
  Ancient Lab Ramp Gate, Outpost Heavy Gate and Outpost Prison Ramp 30°, Swamp Dome 10°). The box is placed by the building's whole placement
  rotation, tilt included (`Fixture.Rotation`; door picking uses it too), not its yaw alone: **Verified** (level data placements), the Ancient Lab
  Ramp Gates in zones 41.35 and 62.25 stand 10° and 8° off level with the leaf 231 up, 42 and 33 units off it by the yaw alone. Both reaches measure
  the walker's height from the
  leaf's foot (`DoorSpot.Foot`, the box's bottom; within 40): measured from its centre, tall gates (Stack's leaf centre is 45 up) were never opened and walked through shut.
  Engine stand-in for the original's door faces, closed until `doa` 0.6 ([locks](game/locks.md#doors) "Navigation"): every tick a door less than 0.6 open is solid
  (`HoldAtLeaves`), so a character that moved into or through its leaf from one side within its width is put back at its footprint radius and waits while the leaf opens
  (**Observed**, real-data probe 2026-10-21: a walker at a Stack gate waits about 3 s and passes at 0.62). A door a walker opened stays open, as the
  original closes no door behind a walker ([locks](game/locks.md#doors)).
  The player's finished buildings register their doors (`RegisterPlayerBuilding`, site -1, unregistered when no longer finished;
  keys written by `BuildingSave`), and a cage or `has lock` building its own lock ([player-built locks](game/locks.md#lock-object)); the site menu has Open/Close Doors (`ToggleDoorsOrder`) and Lock/Unlock Doors
  (`ToggleDoorLocksOrder`), the door panel's buttons for all doors at once. Any door: `DoorOrder` (a quick right click,
  [ui-input](game/ui-input.md)) walks the nearest selected to the leaf's near side (12 out, reach 20) and opens or closes it, or picks another
  faction's locked lock (`PickingLock` on the door; a door is worked from that spot). `WorldSnapshot.DoorPicks` lists every door's leaf for picking
  (rebuilt when buildings register or go); `WorldSnapshot.Doors` publishes the player's doors and every door that is not closed and unlocked; the object renderer slides door parts by `DoorLeaf.LocalOffset` ([render-objects](render-objects.md)).
- **Containers** (`OpenContainerOrder`, [locks](game/locks.md) "Containers"): the opener opens it only within 20 of its height (engine).
- **Object walks** (`MovementSystem.GoToFloor`): move orders and the player's walks to a container, ground item, door, cage, forced lock or operated machine go onto the
  object's own floor (`FloorNear` its height within 20, the goal height set); without it a goal over a furniture hole resolved to the storey above.
- **Cages/beds**: `FindCage` (original weights, own x100, home x100, a police carrier x1e-5 for a cage inside a non-public building; another faction's cage only when `CanUse` ([locks](game/locks.md#beds)) passes; engine: skips a cage the carrier can't reach, `Reachable`: its building
  shut by another faction's doors, or a walk of that faction there failed in the last 24 game hours, `MarkUnreachable`;
  no path query runs in a tick), `Imprison` (state 2, `CharacterTable.Place` at the item's origin, cage
  locked, sentence; optional carrier; every caging is in `CagingsOf(tick)` and the `Caged` event, the bounty-claim hook),
  `LiftOut` (a `PickUpOrder` on the occupant of an unlocked cage, awake or down, engine; out of another faction's cage
  the lifter gets a pending code 7 and the freed one Escaping Prison + `Escaped`), `Release` (unlock, clear, erase the releaser's and jail's bounty entries for a booking releaser),
  `GetOut` (refused while locked; Escaping Prison unless served), `EnterBed`/`LeaveBed`.
- **Bashing gates** (`LockSystem.Gates`, `GatePlan`; rules in [locks](game/locks.md#cutting-and-breaking-285--185-cut-286--186-break), codes in
  [gate codes](game/pathfinding.md#gate-codes)): `LockSystem.Regions` holds the latest `GateRegionsCommand` (null: nothing is blocked). At each bash check an NPC
  attacker (as for doors) whose target's code differs from its own, behind a route that is not open (`RouteState`: the widest path over the gates, each gate's
  state from its door fixtures: broken/open 1, closed 2, locked 3, none registered (a town not spawned) 1; codes no gate joins count as 3, engine choices), plans row 4 with `GatePlan`: 90 from the
  codes, 122's finder the first gate on the route (nearest of the equally good), 123/290/81 at it (91: within 45 of the leaf, x2 standing; crowdLimit8: under
  8 bashers on it), restricted rows open (the attacker is an NPC). A plan that bashes sets `Bashing` to the gate's main door, and the door basher takes over,
  standing on the attacker's side of the leaf. Gate state 2 needs no plan (Meitou walks through closed unlocked doors). A locked gate of the attacker's own faction is unlocked, not bashed (`LockSystem.UnlocksOwn`: row 78 Unlocking door, list B 70 `isObjectMine`, then 122; the lock worked 3.3 s at the leaf, then the gate opens and stays open): **Observed** from the TaskData rows, so town guards get out after a target beyond their locked gate (#595); not for an animal target: the Tech Hunters' Ancient Lab gate in Mourn stays shut on the Great White Gorillo behind it (#645). Checked before the house doors. When no gate leads to the target, 270's wall fallback smashes the nearest wall of the attacker's region once it is within the wall's reach (`LockSystem.Smash`, [locks](game/locks.md#doors)).
  Coverage: `GateRegionTests` (labelling), `DoorTests.A_bandit_outside_a_locked_gate_plans_to_bash_it_and_none_once_it_is_broken` (the plan).
- **Beds** (`Locks/BedSystem`, serial; facts in [locks](game/locks.md#beds)): bed fixtures are the town interiors' beds and, registered with the player's
  buildings (`<site>@bed0`, site -1), the beds the player built; each keeps its `BedData` (function, clip, construction materials). `UseBedOrder` (258: a quick
  right click on a bed, or its held menu's "Sleep") sends every awake, non-carrying selected character (as the original; the first there takes it, the rest are refused); `PutInBedOrder` (99: "Put in Bed" on the bed's menu;
  a quick click while carrying, engine) sends carriers, the cheaper rent paid, the body let go
  and laid in. In reach (`use range` + 15, on the bed's floor within 20, or a walk ended within 60) it pays the rent (`Cost`/`Pay`, player only; the 24 h memo
  is engine state, not saved) and enters, its queued orders kept (as for a cage, [what clears the queue](game/ai-tasks.md#what-clears-the-queue)); the Sleep order (258) keeps running while it lies there, until replaced, also for a
  sleeper loaded lying in that bed (#684: else its follow job got it up); 3 short walks give the bed up (`MarkUnreachable`). In a bed the task is `CharacterTask.Sleep` (held still, the
  bed's clip, `Sleeping` on the HUD); any other task gets it up (`GetUp`; a knock-out or death in bed keeps it there). The player's bed search `FindBed`
  follows the original's weights over beds within `BedSearchRange` 3953 (engine, for the town). Job 106 (in the list with the rescue job; the decision step's, `BedSystem.PutInJob`): a carrier of
  a friendly, non-hostile body with no grudge tag against it, and no bounty owed to a police-like carrier, lays it in the searched bed. An NPC
  carrier's goal 106 is weighed with its other goals ([town duties](#town-duties)), with the same desire and search. Lifting a sleeper
  takes it out. The bed search tests no power, nor does the original's place test (**Observed**, [locks](game/locks.md#beds)). An unpowered bed
  refuses the sleeper or the carrier at the bed (`BedSystem.HasPower`); a bed that refuses an NPC carrier (power or rent) counts for its faction as
  one it cannot get to (`FixtureRegistry.MarkUnreachable`, 24 game hours), so the next search moves on (engine choice). The rescue job (148) and the
  one-shot Rescue start a lift only while the bed search finds the rescuer a bed (`CarrySystem.MayRescue`).
  Saves: `HeldSave` (a player-built bed also writes the original's `in what` = its building's handle; read back through `Bind`); a loaded sleeper's task is Sleep,
  and `Bind` puts it at its lie spot still sleeping (a standing task would get it up).

- **AI sleepers** (going to bed, 153/239, which plan into 98 Sleeping, `Task_UseBed`): arrived at their home's bed spot, the serial step (`AiSystem.LieDown`) lies
  them in that bed, else in the nearest free bed of the building within 30 (engine choice); with none free they rest on the floor there. Without a bed spot at
  home they go to the bed `AiSystem.SpareBed` finds ([locks](game/locks.md)); with none the goal is not wanted (desire 0).
  One already in a bed keeps it: the goal's target is where it lies, so its plan is 98 alone and it stays in; the goal takes its
  sleep as its own (provenance 153/239, so the plan step 98 is its order, as the original's `Task_UseBed`), also for one loaded
  in bed or laid in by the walk's end. A goal of any other behaviour gets them out (`LeaveBed`, in the think). A character a carrier laid in a bed (a
  put-in, the player's or an NPC's goal 106) is the AI's once it comes round, as no task of its own keeps it there and the goal it had before it went
  down is stale (`AiSystem.Holds`, engine choice): its think lets it lie on or gets it out, as any sleeper's. This includes a member of an autonomous
  player squad ([autonomous player squads](#autonomous-player-squads)) that the player laid in a bed to heal: its AI takes it when it wakes and gets
  it out unless it chooses to sleep. One the player ordered to sleep, or on the injured-sleep job, stays, as that task keeps it there.
  `CharacterCold.InSomething`/`InWhat` name the fixture; an imprisoned character is skipped by AI, movement (held still),
  aggro, answering, `CombatSystem.Engage` as attacker (it can still be attacked) and carrying. Sentence: `SentenceStart`
  (`World.GameHours`) + `SentenceLength`; served when start + length <= now; no bounty with the cage owner: none (served).
- **Lockpicking** (`Lockpicking`, serial; the player's order is in [Arrest and jail](#arrest-and-jail)): `Step` per task update (pending crime,
  timer on the lock, seeded roll by the lock's roll counter, XP), `Allowed` (min chance; engine: conscious, an arm, not
  carried), `Sheathe` (a weapon in hand put away first by every pick and cut, one update, [locks](game/locks.md#lockpicking-task_picklock-76-task_picklock_shackles-201)), `Break` (strength > 3 x hardness); `CutLock` / `Break` also serve doors, containers and cages (Use Tools / Use Strength, `LockSystem.Force.cs`, [locks](game/locks.md#player-orders-on-locks)).
  A player character picking a lock or shackles at the lock gets the floating bar (`CharacterSnapshot.Bar`, drawn in the
  `Kenshi_ProgressBarPanel` layout just above the name tag; anchor engine choice). No bar for cutting.
- **Bashing doors** (`LockSystem.Bash`, `DoorPlan`; rules in [locks](game/locks.md)): every 30 ticks an NPC attacking whose target is inside a building
  whose doors are all locked (`IsShut`; inside: nearer the door fixture's origin than its farthest door spot) while it is outside plans row 4 with the
  original's rows (85 → 72 / 81 / 287 → 82); a plan with a bash takes the building's main door (lowest door index with a spot) (`CharacterCold.Bashing`).
  Only door states and 90 DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS (`LockSystem.NotBlocked`, as for gates) are evaluated (the rest of row 4 is held); the desire is `scoreAggressiveAction` (no war-campaign case); an animal's natural weapon
  counts. The pursuit leaves it alone; it walks to 12 out from the leaf until within the door's reach (25, x2 standing, the leaf for the marker, no
  gateway 45) and swings at 0.6 of every `stonemine` cycle (`LockSystem.BashClipSeconds`, 1.5 s only without animation data; an animal swings its own first fitting attack technique's clip, restarted at 0.98, `CombatSystem.AnimalBashSeconds`); a character ahead within 10 ends it until the next check. A closed unlocked
  door is passed by the walk, so the bandit branch (bashing unlocked doors) and the break-out (287) are not acted out.
  Broken: lock unlocked, door broken and opening, the pursuit resumes. Engine: no crime for NPCs, the `Unlocked` sound for
  `Break`; headless (no door gate, no spots) nothing is bashed. Player characters never bash.

## Arrest and jail

`Crime/ArrestSystem` (serial `SlowWorld`, after `CrimeSystem`) and `Locks/LockSystem`. Facts: [economy 11.5, 11.7, 11.8](game/economy.md#117-arrest).
Arrests, the recognition meter and the marker are hashed only while set. What a save keeps of them: [Saves](#saves).
An arrest (`Crime.Arrest`) is the act-out of the guard's running order 240 Capture Target, 213 Lifting/Snatching or 112 Find and put in cage (`Arrest.Order`; the record
lives while that order is his started front; nothing else holds him, and another order's start leaves the arrest's carry alone). **Engine choice**: the act-out runs
the original's plan (fight 4 for state 7, strip 181, lift 240 or 213; then 112's cage walk and 108's put-in) as serial phases, not through the AI planner; each task of an ordered
one starts with the order's provenance. The chain's rows and states are in [AI tasks](game/ai-tasks.md#arrest-and-prison-rows). Goal 204 is an [engine goal](#engine-goals) (below); goals 203, 112 and 281 are not in `TaskBehaviours.Table`.
- **Triggers**: a police-like witness that books a crime acts at once (`CrimeSystem.BookingsOf`, the witnessed-crime
  events); every 15 ticks an awake NPC within 400 that sees a player character (`Visibility`/`SneakVisibility`) whose
  bounty with its faction reaches its class threshold spots it, at once while `RecentlyWanted` > 0 (250 game seconds after
  a booking or a witnessed crime; engine: the countdown), else after 45 s of being seen (engine: one meter per target,
  rate not decoded); only the nearest free police-like looker acts. MILITARY threshold: engine 2000 (original 4000 or 2000);
  the police-like NPCs of the five towns `CrimeInstallTests` loads are MILITARY (**Observed**).
  An escaped prisoner (`CharacterCold.Escaped`) that a disguise does not hide (`EscapeShows`) gets event 45 from the sight pass (`Perception.SightCrimes`,
  [sight](game/dialogue.md#ambient-conversations)); without it (no dialogue events) a police-like NPC in its own town that sees it arrests it (engine). `AggroSystem` no longer
  attacks wanted characters.
- **Spotting talks** (`ArrestSystem.Talk`, the dialogue system, while its event lines are on): the looker fires event 44 at the target
  (`DialogueSystem.FireEvent`, a bubble now) and the data decides ([economy 11.7](game/economy.md#117-arrest)): DA 29 arrests (`ArrestFromDialogue`,
  below; an animal target, or a guard outside his town, is attacked instead: `ArrestSystem.CapturesInTown`), "Small bounty" sends the leader to talk (DA 2) and its interrupt opens the fine window (DA 8 + 49 pay and clear; the rest DA 29), others
  say nothing more. The target is then left alone for 60 game s (`SpotRetrySeconds`, engine), longer while the guard still talks or walks up to talk (order 102 at
  his queue's front); paying ends it.
- **Arrest orders** (DA 29, `ArrestSystem.OrderArrest`): in his own town (the town his resident squad belongs to, `Ai.AiSystem.TownAt` by its radius; engine stand-in without
  town data: within 2 x the squad's home radius of its centre) a non-animal target gets the guard 240 (his queue cleared), then 112 queued behind it; otherwise 4 Attacking
  (cleared; refused with "My arm is broken!" when no arm works), and his free squad mates near the target attack it too (engine reading). Without dialogue events (e.g.
  headless tests) the engine gives the same orders where a spotting, an escapee seen or a booking witness would have fired its event; with them a booking witness is left to
  its witness event unless it is already fighting, then it is ordered at once. There is no engine fine: a fine is the dialogue's (DA 2's talk and its window).
- **Town needs of 240** ([arrest chain](game/ai-tasks.md#arrest-and-prison-rows)): while the target stands in no town (113) the order waits queued, its task ended and the fight
  called off (the fighters the target does not fight back leave it), and the guard goes on with his package; while the guard stands in no home town (95) he leaves the fight
  the same way and runs to his squad's home under the order (127 Retreating fast), then the capture goes on. Both only with town data.
- **Arrest**: attack until the target is down (combat ends the fight; nobody beats a downed target), lift
  (`CarrySystem.BeginPickUp`), `FindCage` from the guard over his town candidates (`CageCandidates`, as kidnapping and town duty; his squad's home building; other arrests' cages reserved), walk,
  and at the cage (`FixtureRegistry.AtCage`: within its `use range`, doubled standing, and under 15 above or below it) `LetGo` + `Imprison` (`prison time` CONSTANTS). The fight
  stopping with the target up re-orders it within 800, else he gives up; a downed target is not lifted at all with no free cage, nor from an ally who carries it (engine: guards passed bodies round a full station, #339). Once the guard
  carries the target, 240 leaves the queue (its desire, AI slot 59, is 0 while carrying) and 112 starts (on the arrest's carry); 112 leaves the
  queue with nothing carried or no free cage (its finder), the guard keeping what he carries for his own goals. After 3 walks that end short the cage is
  unreachable for his faction for 24 game hours (`MarkUnreachable`, as the kidnapper's and the town duty's).
- **Cage walks** (arrest, town duty 112 / 281; engine): a walk onto the cage's floor (`TaskStart.Walk` with the cage's position, `MovementSystem.GoToFloor`), so a cage on
  an upper storey is reached by the stairs. A flat goal resolves on the walker's own storey and ends under the cage, out of `AtCage`'s height. **Observed** (#314 save, Sho-Battai
  Police Station Tower, `Outpost s-IV` `43.11:6-Newwworld-INGAME`): its 19 cages stand on the storey at y 1092, 43 above the ground floor where the guards lift raiders; the
  guards carry all eight raiders up and cage them. Still flat (arrival by distance only, so they do not loop):
  the captive jobs' cage walks and confiscation (212). The player's Put in Cage order (`StepTo`) is flat too but tests `AtCage`: an upper-storey cage still fails it.
- **Release**: player and NPC prisoners alike are freed by the jailers' goal 211 below; Meitou has no other release of a served prisoner.
- **Wardens' goals** (`WardenGoals`, [engine goals](#engine-goals); finders and states: [locks](game/locks.md#wardens-goals)): 211 police free prisoners
  (`[1, 110, 211]`), 155 Lock all the doors (`[163, 162, 155]`), 197 Lock all the doors - stay outside (`[199, 198, 197]`), 86 Opening up (`[82, 78, 72, 86]`)
  and 195 Lock all my cages (`[82, 77, 195]`), each chosen by the chooser like any goal of the package. A serial look (`LookEveryTicks`, 15) keeps each
  member's finder pick of a goal it weighs: one prisoner, door or cage. The chosen goal's act-out is a round on that one target (`Round`: walk, then the
  owner's 3.3 s lock time; a door unlocked already only opens): 211 unlocks the cage and `Release`s in its faction's name, 155 / 197 close and lock the door
  from its inner / outer point (setting the house flag once no door of the home is open), 86 unlocks and opens it (clearing the flag when its finder finds
  none), 195 locks the cage. A round asks `DutyGoals.Runs` before each step and ends with `Done`; a failed walk ends it too, and the next think picks again.
  A target another member's act-out works is refused. 197's members inside the home get no plan, as in the original (its !107 provider 200 needs a target, which list B gets only from the goal's own empty hand, [locks](game/locks.md#doors) "Lock up from outside"). A loaded night's
  locked homes come back flagged (`meitou house locked`). **Gates** (180 Man the gate, `GateGoals`, desire 1,
  [locks](game/locks.md#doors)): a chooser goal held as any engine goal; its act-out runs the unlock round on its gate (`Blackboard.Gate`, else a gate home)
  while the gate's first door is closed or closing and whole (52, met by 72 and 78; the planner holds 52 true, so the act-out folds them in); a locked
  one only when the door is its faction's (78 needs isObjectMine) (#816); then it walks to the gate guard's place (`AiSystem.FrontOf`; its post
  without a gate) and stands; a gate shut again is opened again; a walk that ends short (a round's or its own) is a lost walk (see "Engine goals").
  An arrest's guard walking to a cage behind its faction's shut doors has them opened at once. Act-outs and locked homes are hashed only while any exist.
- **Lock orders** (`LockSystem`, context-menu verbs on a caged character): `PickLockOrder` (the prisoner its own cage, a mate
  walks to it; `Lockpicking.Step` each tick in reach, floaters "Pick success!"/"Pick failed"), `GetOutOrder` (refused while
  locked; any other than a kidnap victim whose sentence is not served, or whose cage owner is not real or a bandit faction, books a pending Escaping Prison (refused for a
  lawless owner) and sets `Escaped` unless the cage owner is not real; [locks](game/locks.md#cages-and-beds)).
  `Imprison` clears `Escaped`; being outside every town does (`ArrestSystem.DropTownStatusOutsideTowns`, as for `Freed`); an engine release (trade, recruit,
  liberators) does too, unless the cage owner is not real; a jailer's release (`Task_ReleasePrisoner`) leaves it.
- **HUD**: `CharacterSnapshot.Prison`; the State line shows the status panel's prison text.
- **Bail** (DA 43, [economy 11.5](game/economy.md#115-bail-and-sentence); only the Shek bounty desk and the Inquisitor offer it): the conversation
  closes and the window reopens as the bail list (engine stand-in for the trading window): "Pay Bounty", one choice "Name (c.N)" per prisoner of
  `ArrestSystem.BailOffers` (cages within the speaker's town extent, as `InOwnTown`; player-faction or not hostile to the speaker; `BailFor` not
  null: the cage's owner neither the player's faction nor lawless), `[Leave]`. A click pays (`PayBail(prisoner, payer)`): erases the jail's entry,
  `Release` (no escape). NPC prisoners are only freed, not recruited. `PayBailOrder` (no UI) is the same bail paid by a player character within 500 of the cage.
- **Put in Cage / claim** (`PutInCageOrder`, a cage's Put in while a selected character carries someone): the carrier walks to
  that cage, or with none named the best free cage within 1500 (`FindCage`; others' cages reserved), `LetGo` + `Imprison` as an arrest does, then `Claim`
  (11.4 caging tests; the slaver exception never applies): the cage owner's unclaimed entry, halved for a dead captive, is
  paid and marked claimed ("Received c.N reward."). A new order or 3 failed walks end it.
- **Spawn bounties** (`SpawnBounties`, 11.2): rolled for new NPCs (not the player's, not sandbox spawns) under `Spawn` keyed
  by the character id, one fuzz roll for all its factions; no `bounty factions` -> "the empire" (fcs.def), engine: the
  faction named United Cities. Inspecting an NPC shows its bounty in the HUD's bounty panel.
- **Bounty hunters** (engine identification: NPCs of an ADVENTURER faction with a `legal system`, in the base data exactly
  Bounty Hunters): in the spotting sweep they see the player's entry for their faction's booking faction (`legal system`,
  `CrimeFactions.BookingOf`); above 4000 and once recognised (the same meter) the nearest free one fires event 44 BOUNTY_SPOTTED at it
  (`FUN_140856f00`), and the data's line answers with DA 6 `ATTACK_CHASE_FOREVER`, order 101 DEFEAT_SQUAD ([economy 11.8](game/economy.md#118-bounty-spotting-and-bounty-hunters));
  with no line said (or the event lines off) nothing follows, as in the original, whose spotting only fires the event (`FUN_140856f00` ->
  `FUN_140685660`, **Verified**). There is no hunt goal and no cage (11.8); after a line the hunter leaves that target alone for 600 game seconds
  (`HuntRestSeconds`, engine). Hunts and cagings are hashed only while any exist; restarted after
  a load ([Saves](#saves)).
- **Goal 204 Arrest bounties** (`Crime/ArrestGoals`, an [engine goal](#engine-goals); facts: [economy 11.7](game/economy.md#117-arrest)): a serial look
  every 15 ticks keeps each member's finder pick (`FUN_14099bfc0`) and its free-cage and town answers; the chooser weighs 204 like any goal of the package
  and plans it (`[4 | 262, 181, 68, 203, 204]`, the fights providing 7). The act-out is the plan's front work on its target, each with the goal's
  provenance: the fight (4 or 262, deferred to the next tick; combat then takes the guard, `DutyEnd.Taken`), the strip (181, `ArrestSystem.Strip`, shared
  with ordered arrests) or the lift (68); a strip walk that ends short of the target or a lift with no path to it is a lost walk on the target. Each ends the act-out (`Done`), as the original's plan ends at row 203, which does not act: the chooser plans
  afresh, and a lifted target is 129 (carried by another faction), so the carrier's own goals (112 / 281) cage it. A body that leaves the shoulder before a
  cage ends 129, and the next score finds it again (uncarried, down, a bounty: `FUN_14099bfc0`): with 7 and 118 holding the new plan is the lift, so the guard
  lifts it again as a fresh 204 (**Verified**, `FUN_14059a380`, `FUN_14099bfc0`). A target another act-out or an ordered arrest works is refused. In
  `Gate guard  -turrets -arrests` 149 Man the turrets (weight 1.2, desire 1 idle within 1500 of a free turret, 0.3 farther) beats 204's 1.0, so only members with no free
  turret in reach arrest (**Verified**, `FUN_140598080`, `FUN_1408ccc70`, `gamedata_base.txt`). Picks and act-outs are hashed only while any exist.
- **Bounty desk** (dialogue, [economy 11.4](game/economy.md#114-claiming)): `DA_PAY_BOUNTY` (`ArrestSystem.PayBounty`) pays the carried body's
  unclaimed entry for the speaker's booking faction (halved dead) and marks it claimed; the hand-over of DA 10, 30 and 40 (`TakeOver`) runs
  after the line's other effects (data lists them in either order): the carrier lets go and the speaker is ordered 213 on the body (cleared; refused with "I can't carry anyone
  with this arm." when his left arm cannot carry) and 112 queued behind it, so he lifts it and carries it to a cage as above; a dead body's 112 waits queued (its `!99`).
- **Shackles** (`Locks/LockSystem.Shackles.cs`, [locks](game/locks.md)): ARMOUR `is locked` items get a `LockState` (`ItemInstance.Lock`);
  `LockedOn` (effectively locked, worn) refuses moving, unequipping and selling. `ShackleOrder` (verbs Unlock Shackles / Use Tools /
  Use Strength) sends the first eligible worker (the wearer or a mate within 25): pick (`Lockpicking.Step` rules), cut (needs a tool,
  `item function` 9; the best is consumed when hardness reaches 0) or break (strength > 3 x hardness). Another character's shackles: a pending
  code 7 against the wearer's slave owner faction (none for a wearer without an owner). The lock is saved on the item (`lock lvl`, `locked`, `broken`, `hardness`). Shackled characters show the portrait Slave overlay.
- **Spawn prisoners** (`PopulationSystem.MakePrisoners`; facts: [squad factory](game/factions-squads-towns.md#squad-factory)): a housed squad's `prisoners` templates are made
  at the owner squad's spot as squads of the template's faction (else the owner's), and each member in turn is imprisoned in a random free cage of the home building
  with the factory's FOREVER sentence (`SpawnSentence`, 9999999 h: never served, so no release round frees it; getting out is Escaping Prison); the first member left
  without a cage is destroyed (Meitou never makes it, `SpawnSquad`'s `unmade`, as a town spawns in the inputs phase, where no character may be removed; a unique
  stays claimed: `FUN_1405067e0`, the only erase, is called by the unload and spawn-mode-3 releases alone) and the rest of that squad stay free, as do later entries' squads but their first member. **Observed** (headless probe, base game): all 11 of `Tengu's vault prisoners` caged in the
  Ancient Citadel's `Prison` layout (49 Prisoner Cages and a Prisoner Pole, 50 cages); Agnu in the Ancient Lab's `Labs5` (8 cages). A caged prisoner is no aggro
  target (engine choice).
- **Putting shackles on** (`PutShacklesOrder`, verb "Put on Shackles"; engine stand-in for dragging them onto the target's armour slot, which the held item does not do yet): a
  selected character carrying unworn, unbroken shackles walks to a live NPC that is down or caged and wears none, and within `ShackleReach` puts them on, locked again
  (re-locking is an engine choice); what the target wears in that slot goes into its inventory. No crime is booked. Prisoner Shackles take the boots slot.
- **Slave state** (`CharacterCold.SlaveState` 0/1/2, `SlaveOwner`; facts: [carry-loot](game/carry-loot.md#unknowns) item 3): `DA_ENSLAVE_TARGETS_CARRIED_PERSON` (40) is the bounty
  desk's hand-over (`ArrestSystem.TakeOver(enslave: true)`), and the captive becomes the guard faction's slave once caged ("{1} has been enslaved" for the player's). A slave's
  sentence is never served; getting out of the cage makes it an escaped slave (2, recently wanted 250); a release or bail frees it (0); an owned slave caged in another owner's cage
  changes owner, except in a police station ([locks](game/locks.md#cages-and-beds)). Dialogue conditions 59, 61, 62 and 64 read it. Saved as `slavestate` and `meitou slave owner` (`SlaveSave`). An arrest with `Enslave` makes the slave at the
  caging; the slavers' own processing (182) of caged captives is [Captives](#captives). CHARACTER `slave` ("sets initial slave state"; **Verified**: fcs.def and the editor's `SlaveStateEnum` NOT_SLAVE 0, IS_SLAVE 1, ESCAPING_SLAVE 2, EX_SLAVE 3) is
  applied to every freshly made character (**Verified**, `FUN_14062b210`: gated only on there being no saved state, no record-type or squad test; the setter `FUN_1405a4940` stores it with no owner test), `PopulationSystem.SpawnSquad`; a saved one reads `slavestate`. The Slaves' "start slave" is 1, in locked Prisoner Shackles. Owner: the faction of the town the squad belongs to, the start town's for the start squads, else none (-1) (engine); 3 (ex-slave) is kept, shown with its owner, its looks-like-slave level floored at 0.01 and not doubled (`FUN_1405a51e0`, **Verified**: floor 0.01, state 2 doubled with a 0.1 floor); the other rules test it as the original does, non-zero tests count it as a slave. A player character starting in state 1 receives the obedient-slave job; the setter's "has been enslaved" message is not shown.
- **Slave squads** (`PopulationSystem.MakeSlaves`): a squad's planned `slaves` squads (the factory's role 4) are made at its spot as squads of their template's faction (else the
  owner's), every member a slave of the owner squad's faction, put into random free cages of the home after its prisoners (engine: their "Slave only squad" package's one goal is
  Slave get in Cage, 172; the rest stand by the owner). A squad of slaves whose template lists no AI package (Dialogue's `Garret slave`) is run by
  the AI as every squad is, with no home of its own: its members plan with their master's Slave AI Goals (Garret's: Follow slavemaster), so it walks
  with its master instead of the engine's wander (#938). **Observed** (probe on the #938 save, `--nav`): the slave follows Garret into the bar. A slave is police-like by its class like anyone (`FUN_140599600` tests no slave state, **Verified**; every Meitou caller mirrors an original caller of it); an owned slave starts no fights and is no aggro target but for its owner's
  enemies (engine: Heng's Outlaw slaves, uncaged without building data, drew its Samurai into fights). **Observed** (real-data
  probe 2026-10-21): Rebirth's 10 Rebirth Cage Guards squads make 34 caged Rebirth Slaves.
- **Slave catching** (`ArrestSystem.CatchSlave`, the spotting sweep; the packages' Capture Slaves, 167, in `addon- slave catching`, which Rebirth's gate guards and garrison inherit): an awake NPC of the owner faction in its own town that sees a player slave (daylight sight, full range; a sneaking one, like a sneaking escaped prisoner or bounty in the spotting sweep, only once that NPC's spot meter on it is full, `ArrestSystem.SeesSuspect`)
  with a view factor above 0.05 tags it escaping (2) by the original's test (`EscapingSlave`, [escaping slave spotted](game/ai-tasks.md#slave-work-shout-and-capture); with the sight pass `SightCrimes` tags it and fires 36 instead): sneaking, no
  shackles worn, walking a move order away from its master (its point farther than 250 from the owner and than the slave, not heading at it, and outdoors, past 433 or not
  looking like a slave), or fighting the looker's ally; the looker's own-town gate is the engine's, and so is tagging a slave whose master Meitou does not know when it
  stands outside that town. At an escaping slave the nearest police-like or slaver one fires event 36 ESCAPING_SLAVE_SPOTTED (the sight pass
  fires it itself), whose line's DA 6 `ATTACK_CHASE_FOREVER` orders it 101 DEFEAT_SQUAD; with no line nothing follows (as for event 44).
  The capture after the fight is the catchers' own package goals: 167 cuffs (chains) it, 168 lifts it and 112 cages it, the caging
  making it a slave (1) again ([captives](#captives)). A town here is `InOwnTown`'s extent, else no farther
  from its centre than its gate nearest the point plus 100 (Rebirth's gates stand about 1500 out, its radius is 184); without gates, the extent only. A caged slave's master is
  the resident squad of its cage's building when loaded (engine stand-in for `FUN_140330600` assigning the cage's owner handle). **Observed** (probe `obey`, #813 save, Rebirth):
  an obedient slave standing, working or ordered toward its master stays a slave; ordered to a point 330 off, away from its master (2200 off), it is tagged outdoors within 20
  ticks; sneaking, it is tagged once the Sentinel's spot meter fills (about 80 ticks).
- **Escape far from the owner** (`ArrestSystem.EscapeFarFromOwner`, on the spotting cadence; facts: [carry-loot](game/carry-loot.md#a-slave-far-from-its-owner-escapes)): any
  slave (state 1), player or NPC, outside every town (its status town, `CharacterCold.StatusTown`, [rule](game/carry-loot.md#the-status-town), as for `Freed`) and farther than about 3 536 from its owner becomes an escaped slave. The owner is the
  master squad's leader (`ObedientSlave.OwnerOf`); without a master squad or pending saved owner handle, a slave becomes escaped even inside town. Not one held in a cage that has an owner (`ArrestSystem.CagedByOwner`): the original's cage entry gives it the cage's owner handle (`FUN_140330600`), and Meitou's stand-in, the resident squad, is missing for a cage with none (Rebirth's Prisoner Poles; **Observed**, #647 save: the start prisoner turned escaped slave in its cage within 30 ticks).
  A master squad or saved owner still waiting to load is kept; measuring to an unloaded platoon's position is not modelled.
- **Slave trading** (DA 41 / 42, `CharacterTrading`; facts: [economy 12](game/economy.md#12-slaves-prisoners-animals)): the line ends the conversation and the window
  opens as it closes (a speech-bubble line opens it at once; only for a player-character target). Rows, prices and the confirm's effects as in the original, with these **engine**
  choices: the buying list (the original's per-owner set, whose owner is a character, is not modelled) is every character in slave state 1, not dead, that obeys the
  slaver's squad, is caged in a cage of that squad's home building (a shop's stock: Sho-Battai's is owned by the town's faction, not the slavers'), or is owned by the
  slaver's faction within 300 units of it; a faction-wide list offered other towns' and caravans' slaves and an enslaved shop guard lying in the street (#425); the selling
  buyer's money is the slaver's squad purse (`Squad.Money`, a trader's shop money); the confirm re-checks the window's gate (a row ticked, total within the buyer's money) and
  does nothing otherwise; a slave bought free out of a squad of others leads a new squad of the old one's template with a copy of its memory (`DialogueSystem.Separate`, as DA 28), or a new player
  squad for one of the player's; event 51 fires after it is out of its cage; the window closes when the slaver or the customer is no longer awake. It is neither saved nor hashed.
- A bought slave stands at its cage's exit spot, ordered 133 with its queue cleared (`Locks.FreedPrisoner`; its leave finds the hold already cleared:
  [locks](game/locks.md#cages-and-beds)). DA 48 opens the same window for animals ([buying animals](game/animals.md#buying-animals)). A sold character obeys the slaver's squad (187, [below](#captives)) and is chained: `Character::setChainedMode` (`FUN_14032e590`,
**Verified** by decompile) puts the ARMOUR `SHACKLES` (boots) on one wearing no locked shackles, `SHACKLES-BODY` when its boots section is disabled (Meitou: the race's `no shoes`),
what it wore there going into its inventory (engine choice; `DialogueSystem.Chain`). Its other callers (a dialogue action, `FUN_1405dbd80`, `FUN_1405dc310`, `FUN_140583a10`) are not traced.

## Kidnapping

`Kidnap/KidnapSystem` (serial `SlowWorld`, right after Carry; needs the population). Facts: [carry-loot 3.2](game/carry-loot.md#32-goals-and-their-target-finders)
(the finder, 254's plan), [ai-tasks](game/ai-tasks.md) (the desires), [campaigns](game/factions-squads-towns.md#campaigns) (which packages).
- **Goal 254** "Find and kidnap (with cages) urgent" is an [engine goal](#engine-goals) (`KidnapGoals`), registered with its plan rows 132 and 68. A member weighs it when its
  role, slave or race list holds it in the hour (`AiSystem.WeighsGoal`: an AI squad's running package, a campaign platoon's phase package: the cannibal raid's assault and
  retreat; no race or faction list holds it); a platoon a code mod holds and a squadless character never kidnap.
- **Desire** (**Verified**): 254 and 132 are 0 while the member carries or two squad mates do; 254 in its squad's home town is 0 while its running package's mission town is set elsewhere
  (`SquadTown.MissionTown`), else 1 with a free cage it may use (`HasCage`, the town candidates below) and 0 without; elsewhere 1. 68 is the lift desire
  (slot 59).
- **Victim** (132's finder, the serial look every 15 ticks on the slot's turn; `IsVictim`): the nearest within sight (`RangeOf`, the rescue range) that is alive, down, of a carriable
  race, not carried, caged or in a bed, not an animal, nobody's pick-up, and not of the kidnapper's faction or an ally. Player characters included; so is anyone lying in a
  bed, awake or not (the finder's bed branch, **Verified**): the lift is then a kidnap that rolls on arrival (`CarrySystem.Kidnap`: a hit knocks the sleeper out, a miss wakes it
  and raises the alarm).
- **Act-out**: plan `[1, 68, 132, 254]`; the start lifts the plan's victim (`CarrySystem.BeginPickUp`, row 68, back to standing) unless another member works it; the goal is done
  (`DutyGoals.Done`) when the pick-up ends, with the body on the shoulder or not; a lift whose walk no path gets to the victim (`CarrySystem.LiftWalkFails`) is a
  lost walk on it ([engine goals](#engine-goals)). A player victim's message "is carrying X away". The carrier then chooses again with all
  its goals and acts the winner out as anyone ([holding](#engine-goals)): its goals 112 / 281 ([town goals](#town-duties)) cage the body, 141 follows its leader with it.
  A kidnapper stumbling or getting up loses the goal (replanned) and chooses again. In the cannibal raid's assault (no cage goal listed) the carriers carry at most
  15 clock minutes or until 3 carry, then the campaign switches to `Cannibal Raid Retreat` (`PopulationCampaigns.CannibalsLoaded`), whose 112 cages at home
  ([carry-loot](game/carry-loot.md#32-goals-and-their-target-finders)).
- **Caging**: a victim that leaves its 254 carrier's shoulder into a cage (seen at the next kidnap step, `KidnapGoals`' lift record) is `CharacterCold.Kidnapped` in a cage of
  the carrier's own faction, and a player victim gets "has been kidnapped by F".
- **Prisoner**: `Kidnapped` = held without a sentence: it is never served (no release round), no bail is offered, and getting out is no escape crime. Set only in a cage of the kidnapper's own faction: in another faction's cage (one `canIUseThisBuilding` lets it use, a public barracks') the victim is that faction's prisoner and its goal-211 jailers free it once served, as the original, which has no such hold (#628; a prisoner with a slave state is never served there either, `FUN_1405c9f00`). The cage is locked; rescue is the usual
  lock work, or a lift out of an unlocked cage ([Cages](#cages-beds-and-locks)).
- **Save**: the cage state is the existing `in something` / `in what` (an interior cage's entry, written with its house's interior when the save has none, plus Meitou's keys), and `meitou kidnapped` ([save](formats/save.md)). Carry links are saved and restored
  (`CarrySystem.RestoreLinks`); the goal records, lifts and carries are not, so a loaded carrier chooses again and its town goal cages the body. Hashed only while any exist.
- What happens to the captive next: [Captives](#captives).
- An NPC kidnapping is no crime for witnesses, as in the original (its lift is 68, not `Task_Kidnapping`; [carry-loot](game/carry-loot.md#the-kidnap-victim-flag)),
  but for a sleeper taken from a bed, which the engine lifts through the kidnap roll.
  A carrier whose squad leaves the world lets its victim go where it stands and keeps the link, taking it up again when both are back ([Carrying](#carrying-rescue-and-looting)
  and the save's `carrying` link, `CarrySystem.DropForUnload`). The victim's own platoon splits it off into a squad of its own when it unloads ([split at unload](game/factions-squads-towns.md)), so a bounty carried away from its town stays on the carrier's shoulder. A split-off member made from a save's platoon leaves that record (`SavedMember.Gone`) and is saved with its new platoon, where it is (#622: kept in the old record it loaded only with its home squad).
- Activation of a player-owned empty platoon (**Verified**, `FUN_1404ffad0`: members are made from the template only when the owner has no player flag): it is removed, not filled (`PopulationRoaming.Activate`).
- Activation repair (**Verified**): a platoon's squad made in a player town's open ground by a faction that is not the player's has the members that came up inside walls moved to its place, with a red message "Character <name> of <squad> tried to spawn inside walls!" (**Verified**, `FUN_1404ffad0`: the place's gate code 0, a member alive, up, not in a bed or cage and with its own code above 0, once at the build; Meitou `PopulationRoaming.MoveOutOfPlayerWalls`, for every platoon load: roaming, town-pool and never-made town squads (a saved squad's characters keep their saved places), plain colour, nothing where the codes are unknown).
  The original's unload test (`ActivePlatoon::unloadCheck` `FUN_1404febd0`, **Verified** by decompile) holds a platoon loaded while a member carries (Character `+0x348`) a player-faction
  character (`+0x380` hand, `HandleManager::isPlayerFaction`), its 4.5 s countdown reset to 4, whatever the zone at the platoon's place (per carrier, not through the leader's zone; Meitou `PopulationRoaming.SquadCarriesPlayer`); with an NPC load it
  unloads as usual.

### Blood smell

`SmellSystem` (serial `SlowWorld`, every 30 ticks; facts: [perception](game/perception.md#blood-smell)): each zone's blood target is its corpses with flies, its
position their centroid ((0, 0) without corpses), an unloaded zone dropped once its target is 0; the value rises 1/60 and falls 1/240 per game second. Corpses get flies 4 game hours after
death while the weather there acts on no one (`BodySystem.Flies`, `CharacterCold.HasFlies`; none in a world without weather). The host draws the effect and plays the sound at them (`CorpseFlies`).
Goal 176 HUNTING_BLOODSMELL is an [engine goal](#engine-goals) (`SmellSystem.BloodHunt`): the same serial step keeps, for each animal-led squad whose leader weighs 176 now
(`AiSystem.WeighsGoal`: its role list in the hour's window), the zone with the best value (own x1, neighbour x0.8, other x0.5) above the leader's ANIMAL_CHARACTER `smell blood`
while the value holds (the squad data's zone, which the original's desire writes; here the serial step writes it and the desire reads it); the desire is 1 for an animal whose
squad keeps a zone, else 0. The chooser weighs it at the package record's tier and weight like any goal and plans `[176]` (list B !26, 60); the start runs the character to the
zone's position (row 176, `SpeedMode.Run`), the step re-aims it as the position moves, and the goal ends as every engine goal does. Only the squad leader has the goal in the
base data, so the members follow it ([perception](game/perception.md#blood-smell)). Engine: every loaded zone is a candidate; a hunter stops within 50; the leader's threshold
stands for the asker's.

### Town duties

The town goals are [engine goals](#engine-goals) of the one chooser (`Kidnap/TownGoals`, act-outs in `Kidnap/TownDutySystem`, serial `SlowWorld` after the kidnapping): an NPC with a
full mind of its own weighs each one its role, slave or race list holds in the hour by the original's desire, finder, tier and weight against all its other goals; no sweep, free
test or cap of their own. The finders run in a serial look on the member's slot turn every 15 ticks (every tick for a carrier no goal holds, which chooses at once; again after an
act-out ends) and keep their picks by slot (hashed while any); the think reads them. An act-out walks or lifts with `TaskSource.Goal` tasks on the goal's row, steps only while
its goal holds the member (`DutyGoals.Runs`: a fight, an order, an order on its body or a better goal ends the goal) and ends with `DutyGoals.Done` at the original's end.
An end never drops a carried body, an order's neither (the carry link persists whatever happens to the task, [carry-loot](game/carry-loot.md#lift-and-put-down-timing)). A carrier thinks with
all its goals as anyone ([holding](#engine-goals)), so whoever lifted a body (a lift goal, an arrest, a kidnap, a rescue, a save) takes its carried-body goals from its own lists.
Facts: [ai-tasks](game/ai-tasks.md) "Finders of the jobs", [carry-loot 3.2](game/carry-loot.md#32-goals-and-their-target-finders).
- **Lifts 273 / 223 / 138** (plan `[67, 68, goal]`; the goal row has an empty list A, so the plan ends with the lift and the carried body's goals take the body on). Row 68's and
  223's desire is AI slot 59 (`FUN_14059e1a0`, **Verified**): 0 while carrying, 1.0 for a character target, 0.5 for any other (`DAT_14167e8d8`); the scorer clears a fresh
  candidate's target, so a new 273 or 223 scores 0.5 and the one held 1.0. 273's (slot 60 `FUN_140597f20`, **Verified**) is that, 0 in a current town that is no Town (a nest:
  vfunc `0x268` null; the town builder `FUN_140581770` makes the cut-down class only for TOWN `type` 0, **Verified**), has no gates (`Town::hasGates`,
  `TownExit.HasGates`: in a town of the player's faction any building, elsewhere an `is gateway` one, [condition 49](game/dialogue.md)) or has its walls locked up (Town slot 96, `LockSystem.TownLockedUp`) for a member of the player's faction. 138's is 1.0, 0 on Hold or while carrying (**Verified**).
  - **273** (finder `FUN_14099c250`): in its home town, the town its occupation holds or a town of its faction (`Ownerships::isMyTown`), the nearest knocked-out character it
    senses within its sight in that town inside its walls (`amInsideTownWalls` > 0: its [gate code](game/pathfinding.md#gate-codes) is a walled region; where the codes are
    unknown, the built-up area: a placed town's radius, a player town's farthest building from its centre), not carried or caged (Meitou: nor in a bed; the original lifts a
    patient, issue 751), not of its faction or an ally.
  - **223** (finder `FUN_14099c110`): a knocked-out, carriable character inside the squad's home building (`SightMap.BuildingOf`), not carried, caged or in a bed, not friendly,
    sensed and within its sight.
  - **138** (finder `CorpseDisposal.FindCorpse`): a dead non-robot body in a cage of its faction in reach, else the nearest dead uncarried body in sight.
  - An intruder is taken only once a lift clip starts or it is carried: every member whose finder gives it walks to it and the first in reach lifts it; the others' starts are
    refused. **Observed** for 223 (original 1.0.65, the #1024 save, thralls of `ash dome mob` in their home): three thralls on the body's level converged on it and one lifted it
    1.8 s after the load. A lift with no path to the body (`CarrySystem.LiftWalkFails`) is a lost walk on the body (ignored 20 s); one whose walk takes over
    60 game seconds is given up by that member alone, who leaves the body for 2 game hours (`GiveUpLift`, engine choice).
- **Carried body: 112 / 281, 274 / 224, 106, 137**, weighed by the chooser with every other goal (a higher tier first: an urgent 112 or a 224 before 274). Their desires are read
  in the look for the body carried then.
  - **112** (a living body: list B `!99`) / **281** (any body), plan `[1, 108, goal]`: `CageDesire` (`FUN_1405a1140` with `FUN_1405a0f10`, **Verified**: 0.1 outside a town,
    0 while the running package's mission town is another (`SquadTown.MissionTown`: a plain package's is the home town), 0 for an animal, 1.4 police with a bounty-free slave or escapee, 0 friendly, 0.5 dead, 2.0 alive) times the cage finder's 1.0
    with a free cage (`FUN_1408d0d30`: the best free cage of its town candidates, an occupier's occupied town included, not another errand's). At the cage it is locked up with
    the usual put-in sentence (108 `Task_PutInSomething` sets none without a bounty, [economy 11.5](game/economy.md#115-bail-and-sentence); not `Kidnapped`, **Verified**
    `FUN_140358670`), with the relation event and the bounty claim. A cage taken or a lost walk (the cage ignored for 20 s, which the cage finder skips: `FUN_1408d0770` asks `FUN_1405b0290`) end the goal with
    the body on the shoulder. **Observed** (#314 save): a Sho-Battai police guard loaded carrying a raider cages it.
  - **274** (plan `[274]`, list A 164) and **224** (plan `[224]`, list A 137): desire 2.0 unless the carrier regards the body as friendly (`FUN_1405a13a0`, **Verified**; no
    town, animal or death test). 274 walks to its town's exit point (`TownExit`: 200 outward from the first gate outside the walls or wall with an interior, the same for every
    carrier, **Verified** `FUN_140927120`), 224 to the point past the home's first door (`AiSystem.LeavePoint`; a home with no door: its town's exit point, **Verified**
    `FUN_140335480`; **Observed**, original 1.0.65, #1024 save: the thrall put the body down at 105525, 125662, Meitou's exit point is 105520, 125641). States 164
    INTRUDER_IS_OUTSIDE_GATES (`FUN_140598fa0`) and 137 INTRUDER_IS_OUTSIDE (`FUN_140598ef0`), **Verified**: true with no intruder or a friendly one, false while the asker carries
    it. Both end with the task's own unlink (`FUN_1403356e0` calls `FUN_1405ce1e0`, **Verified**): the body falls where the carrier stands and is not knocked out again
    (**Observed**, original 1.0.65, #1024 save). They carry at a walk (`FUN_1403355e0`, **Verified**; `TownDutySystem.CarryOutMode`; [force speed](game/pathfinding.md#force-speed)).
    A walk that ends short is a lost walk with no target: the body stays on the shoulder and the carrier chooses again.
  - **106** (plan `[1, 99, 106]`): `BedSystem.PutInDesire` (2.0 for a body the carrier would lay in a bed, [carry-loot 3.2](game/carry-loot.md#32-goals-and-their-target-finders))
    times the bed search's (`BedSystem.FindForCarrier`); the bed system walks the carrier and lays the body in (`BedSystem.PutIn`, row 106, `TaskSource.Goal`).
  - **137** (list B 99 a dead body carried; plan `[1, 136, 137]`, else `[67, 68, 138, 137]`): desire 1.0 (slot 43, **Verified**) times the disposal finder's (`FUN_1408d1090`:
    `CorpseDisposal.Find`, skipping one whose operator places other errands hold or whose walk failed, 20 s). A member carrying no corpse lifts row 138's first; the plan ends
    with the lift and 137 is weighed again with it on the shoulder. At the disposal the body is fed in (removed with its items, the machine's label floats up). Disposals: the
    player's function-11 machines, the registered towns' placed function-11 buildings and their interiors' (`TownBuilding.Disposals`); chosen by the cage finder's rules (town
    reach, `canIUseThisBuilding`, weight x100 own / home, x1e-5 police in a private building). **Verified** desires and finders: [ai-tasks](game/ai-tasks.md#perma-jobs).
  - With none of them wanted the carrier keeps the body.
- **243 Getting food** (plan `[1, 244, 243]`; a ditch first when 244's room test 82 fails, the planner's 114 and 93): the desire of AI slots 26, 24, 25 ([eating](game/character-stats.md#eating):
  0 outside a town, in another faction's town, carrying food; the occupied branch; else an empty stomach and hunger below 2.1 for an NPC that is no slave, at most 2.4; 0.1 in a
  fight below 1.2). An NPC on the ordinary hunger floor 3 wants none; a `malnourished` squad's floor (1 + 1.2 x rand) is below 2.1 on most rolls and the free food refills only an
  empty stomach, so it wants food for part of every cycle: starving raiders occupying a town (`hostile occupy town`, `campaign victory occupy town` list 243) take food from its
  storages and eat it (**Verified**, `FUN_140597d30`, `FUN_1405c9d90`, the free food `FUN_1407ee2d0`, `gamedata_base.txt`). The finder: the nearest store with food of
  the blackboard's target town, else the current one, else the 3 x 3 zone cells round it (a player town's complete storage, else an unlocked container); 244 takes one item, not while a sensed ally
  fights (state 12). The player side's runs: [decision step](#decision-step).
- **131 Looting food** (plan `[1, 284, 131]`, desire 1.0): the same finder, not a container of its own faction (engine reading of the own-shop skip); 284 takes up to 10 units of
  the first record the store lists (`Task_EmptyMachine` with flag 1, `FUN_14033cd10`, **Verified**: one unit per pass while the store holds the record and there is room; the
  record is the first the store's inventory lists, food or not, **Verified** `FUN_140299990`). State 68 is state 66's evaluator (**Verified**,
  `FUN_140610250`): the looter carries the store's output item, never for a container.
- **184 Ditching loot** (desire 2.0, list A `!121`, plan `[1, 93, 184]`; `FUN_14059b040`, [rules](game/ai-tasks.md#arrest-and-prison-rows)): carrying an item stolen for it (state
  121: inventory or worn backpack; none for a lawless faction), the nearest storage (function 2 or 13) of its town within 6517 that takes one, where every stolen item that fits
  goes in. A member with a home town standing in a TOWN-type town reads the home town's storages, any owner; else the current town's, owned by its faction or the town's.
  Engine choice: only the stolen items go in, and the stolen mark stays on them.
- **212 Confiscating goods** (plan `[1, 3, 212]`; `AI::findPrisonerWithContraband`): in its own town, the first occupied cage (list order) whose prisoner's inventory holds an item
  stolen for it; at the cage it waits 0.8 s and confiscates as a police looter does ([NPC looting](#carrying-rescue-and-looting)).
- **158 / 283 Shooing** (plan `[161, goal]`, list A `!108`; `AI::findStrangersInMyBuilding`; 283's desire, slot 71, is 1.0 only while the home is private): the first character in
  the squad's home building that is neither allied nor an enemy, not down, caged, its faction's slave or law enforcement, or (an NPC squad) a player character there not sneaking
  or spotted; the member walks at it and sends event 9 (17 to an enemy) every 5 s (engine pacing; the dialogue data escalates); after 25 game minutes, once the stranger is
  getting away, event 60 and done; over once it is out.
- **216 Ransacking town** (plan `[156, 216]`, `[6, 156, 216]` with no weapon drawn: list B 5; `AI::findRandomBuildingInTown`): one look draws a random standing building with an
  interior in the squad's town as the blackboard gives it (the override's mission town, else the running package's), else where it stands, among those the planner gets it into
  (row 156's B list 59 `hasAnOpenDoor` and 90, **Observed**): `DoorPlan.PlansEntry` plans 216 with the exact state 59, so a closed unlocked door is opened (72) or, for a
  character that wants to (`AggressiveTowards`), bashed first (81: `LockSystem.BashForErrand`); a locked door nobody will bash or unlock leaves the building out. A closed gate on
  the route is planned the same way (`LockSystem.GateEntry`, `GatePlan.PlansEntry`). It runs into the building and the goal is done once inside.
- Not goals: the ditch the plan's step 93 asks for (rows 113, 114, 242; `AiMind.DitchRequest`) is held by that step (`AiSystem.RunsDitchStep`: the AI
  holds its walk and may choose another goal over it, which ends it); the player side's food runs hold their member with `Engaged`. A fight interrupts their walk
  (`TownDutySystem.IsErrandRow`), an order on the body ends them.
- Occupiers' **36 / 37** town goals walk to a random complete building of the player town (`AiSystem.OccupiedSpot`, engine choice; the original's 36 scatters round the
  occupied town's centre, [town goals](game/ai-tasks.md#town-goals)), 36 drawing again after every walk.
- Every act-out's walk that ends short (a lift with no path to its body too) is a lost walk (see "Engine goals"), the errands no goal holds (a ditch, a player-side food run) marking their store the same way. Engine choices: sight `AggroSystem.OutdoorSight`, arrival 40 (a cage: its `use range` + 15), a lift given up after 60 game s of walking.

## Captives

`Kidnap/CaptiveSystem` (serial `SlowWorld`, right after Kidnap; with the population and the AI). Facts: [ai-tasks, prisoner fate and slave rows](game/ai-tasks.md#prisoner-fate-and-slave-rows),
[ai, prisoner and slaver packages](game/ai.md#prisoner-and-slaver-packages). Each goal is an [engine goal](#engine-goals) (`CaptiveGoals`) of a member with a full mind whose role list (an obeying
slave's: its master's Slave list) or own lists hold it in its hours; its act-out is a job of the worker (`CaptiveJob` with its `Goal` and `Target`), begun by
`IDutyGoal.Start` on the plan's target (refused when the target is gone, no longer fits or is another job's), stepped only while `DutyGoals.Runs`, and done
(`DutyGoals.Done`) at the original's end. Cages are those of the squad's town (`Fixture.Site`) unless said otherwise. A walk to a cage ends within `use range` + 15, or a walk that ended within 200; a
job's walk that ends short of its place is a lost walk ([engine goals](#engine-goals); a player animal's job gives up after three, marking a cage unreachable for
the faction); its own faction's shut doors are unlocked first. A job gives up after 600 game s (engine).
A job's walk is its own, run as the job's row (`CaptiveSystem.WalksOwn`): one the worker was on when the job started, its AI's or an errand's, is replaced at once, as a
goal taken up ends the plan before it ([precedence](game/ai-tasks.md#precedence)). **Observed** (KenshiOracle state 0.5 hold, Rebirth, N = 1): the workmasters take their
gather jobs while walking home under 43, and a job that waited on that walk reached its cage only after about 90 of the hold's 110 s.
- **Look**: every 30 ticks on its slot's turn, and after its job ends, a serial look (`CaptiveGoals.Look`) keeps for each listed goal its desire and its finder's
  target (the job's while the goal holds the member: the original keeps the running goal's finder hand); the think reads only those picks and its own state; hashed
  while any. Desires (**Verified**): 135 and 282 1 once the kill timer is due (read first, creating it); 173 and 245 1 while the platoon has at most 3 slaves
  neither down nor held in anything (slot 63, `FUN_14059b940`); 217 in an export town; 188, 192 and 194 0.25 x the platoon's slave list; 218 that only in a
  prison or slave camp the leader stands in (slot 66, `FUN_140597c70`); 167, 183 and 260 1 while carrying nothing at posture 0, once on the job while carrying nothing (slot 61, then 59); 168 the rescue desire;
  278 and 279 their own; the rest 1. Of the rows on the way, 3 is 0 while down and 170 while carrying. The finder's factor is 1 with a target.
  168's plan is `[1, 25, 170, 168]`: 170 needs `!22`, so a slaver first-aids a hurt downed slave and then lifts it (`FirstAidSystem.TreatBeforeLift`; **Verified**, 170's
  list B in `FUN_140319a60`). 260 and 183 find the same fresh captive (`FUN_1408cc110`, `FUN_1408cd630`): in `Patrol map reavers` 183 (weight 1.1) wins, in
  `empire vault prison` the 1.0 tie goes to the later-listed 260 ([pick](game/ai-tasks.md#planner)); both plan 182 (**Verified**, data).
- **Plans** are the planner's. The rows the job carries out are registered: 134; 220; 231; 236; 3; 40; 182, 166 and 181; 170 and 25; 174; 159; 53; 107; 110. Their
  states are asked of `CaptiveGoals` for the target (character, cage, farm, limb; evaluators in [ai-tasks](game/ai-tasks.md#prisoner-fate-and-slave-rows)), the
  at-location test passing at the job's work or within its reach. Player characters and mod-held minds take none; a player animal's race goals are
  decision-step jobs (`CaptiveJob.PlayerSide`, `Engaged`, [animals](#animals)).
- **Kill** (135; the desire reads the town and creates its timer first, whatever the worker carries; its plan then draws a weapon, `DrawWeapon`: one in a back or hip slot, else a melee weapon of its inventory put in a free slot, else the start is refused): when the kill timer of the town the worker stands in (else its squad's: engine reading) has run out or all the faction's cages there are held; the best occupied cage by the cage finder's weights
  and usable test (`FindCage` in its occupied mode over `CageCandidates`: the worker's town, else its faction's nearest, plus its home's; x100 own faction, x100 home building; another faction's cage only when `CanUse` passes), not a robot. At the cage it holds `chop down static` (`CharacterCold.ActionClip`, which the animation system plays for an idle character) and strikes once per
  clip length at 0.55 of it (`CombatSystem.Execute`: the weapon packet landed through armour, no defence, no crime) until the captive dies. Events 10 (on picking) and 11 (each blow).
- **Eat** (139 always; 282 "Dinner time" at the kill rate): same target, weapon sheathed, `eat` held, `MedicalState.BeingEaten` each tick at lerp(2, 0.4, toughness/100) a second
  (limbs severed unless dismemberment is Never; `DeathCause.Eaten` and "{1} has been eaten alive." when nothing is left). Events 52 and 11 every 10 s (engine throttle).
- A captive's death by either resets the town's timer to now + uniform[1, 15) h; the first ask sets it. Timers and jobs are not saved (engine); hashed once any exists.
  **Observed** (headless probe, real data): a cannibal village kills a caged player character within 16 s of work once its timer is due; a fishman lab eats one in about 160 s.
- **Process** (260: an awake non-slave occupant anywhere in the town; 183: first the nearest free slave of state 1 or 2 in sight that needs processing, walked to with no cage (the characters within sight range that the watcher senses, `CharacterFacts.Perceives`, stand in for the sensed list: none before the watcher's first look, and a sneaker on its feet only once its look senses it; not caged ones; engine reading; **Observed** #759 save: a guard's first think after load took a sneaking escaped slave 480 away and followed her), else an occupant on the worker's floor and in its building within 180 in 3D, whose owner is an ally or none, and that needs processing: an escaped or ex-slave, or a slave not both shaven and looking like one): cages of the worker's faction or an ally,
  not the player's. 3 s of `medic`, then a human captive without locked shackles is cuffed first (182 needs 134 `isChained`, which only 166 Restraining gives, [ai-tasks](game/ai-tasks.md) planner table;
  166's 7 `isTargetDown` holds for a caged captive, `CharacterFacts.TargetDown`; a loose slave standing unshackled plans nothing, only attack rows giving 7): it is chained (`CharacterAssembly.Chain`) and becomes a slave of the worker's faction and squad, as `setChainedMode` makes the cuffer the owner, so that squad's medics treat it in its cage. **Observed** (probe, #508 and #509 saves):
  a sold slave caged unshackled was stripped again and again, and Rebirth's caged player slaves that had taken their shackles off were neither re-shackled nor bandaged; now both are chained once and then bandaged. Then the captive's worn weapons and armour (not locked shackles, slave garments or the worker faction's `slave clothing`) go to the worker's inventory (else the ground; engine for the original's
  storage), its faction's `slave clothing` is put on where free, event 35 (37 for a shaven captive), its head is shaven, and it becomes the worker's faction's slave unless it has an owner already (a recaptured escaped slave keeps its owner and master squad); its queued orders and perma-jobs are cleared. Kidnapped player characters are
  processed too, and so are the worker's own faction's people (**Observed** in the original, 1.0.68: a knocked-out Slaver Guard put in a Sho-Battai
  Slave Traders cage became a slave while still of their faction); the Fixes setting "Guards spare their own" (`CaptiveSettings.SpareOwnFaction`,
  off by default) skips an occupant of the worker's faction that is no slave, on 260 and 183 and in the gathering finders (173, 245, 217). **Observed** (probe): a player character caged next to the Slave Farm's Cage Wardens is enslaved about 7 s later. **Observed** in the original
  (1.0.68, #508's save): a player character who gets into an empty Slave Traders cage at Sho-Battai unshackled is processed once by the Slaver Boss
  ("has been enslaved") and left shaven, in slave trousers and in locked `SHACKLES`, and the boss does not come back, as the chaining above does.
- **Gather** (173, 245: while the squad has at most 3 slaves up and uncaged on its slave list, which is its slave members and the slaves of other
  squads that obey it, `CaptiveSystem.SlavesOf`; 217: in a town or outpost, and in one of the player's faction or an anti-slavery one only for its allies): the nearest occupied cage of the gatherer's faction in the 3x3 zone cells round it, of the town it stands in or of any town when it stands in none (173 and 245, **Verified** `FUN_1409fc450`; 217 needs the town) (173,
  245: its faction's, the first in finder order (no distance sort) whose occupant is a woman or of another race, with no test of the occupant being a player character, down or busy; 217: any but the player's). The captive becomes the gatherer's faction's slave (state 1), owned by
  the gatherer's squad, and stays in its own squad: `Task_GetSlave`'s queued message is replayed as `setChainedMode` only (`FUN_1407b65b0` kind 5 ->
  `FUN_14032e590`, **Verified**: no squad change), so it plans with the owner squad's Slave AI Goals (job 187, `AiSystem.ObeyedRoles`), mostly 171
  Follow slavemaster or 189 Worker. The cage is opened and the captive steps to its exit spot ([locks](game/locks.md#cages-and-beds)). Player captives are gathered too: 173's finder excludes player-owned cages, not player occupants
  (`FUN_1408db680` -> `FUN_1408db510`, **Observed**, 1.0.68 decompile); 217 still excludes player occupants (engine choice).
  **Observed** (KenshiOracle hold, Rebirth, 2026-11-09): the original's gathered slaves keep their squads all hour (Pao 40, Mu 44).
  **Observed** (probe): the Slave Farm's workmasters take the caged slaves out by day; at night their slaves' 172 puts them back.
- **Deliver** (192, the squad leader under a shipping package): its plan's 53 is the travel task (`TownTravel.StepGoal`, [town goals](game/ai-tasks.md#town-goals)): to the squad's town (the shipping package's mission town) by its waypoint at the functor's pace, done once state 36 holds; a walk with no path to the town is a lost walk. **Sell** (194,
  or 218 in a prison or slave camp): the finder (`FUN_1408ca950`, **Verified**) takes the nearest (to the leader) NPC of the faction of the town the leader stands in,
  or an ally, within the town's radius, and keeps it; none, the goal is unwanted. The leader walks within 40 of it and says event 39 (row 159). Each of its slaves then
  leaves for a "Wandering Squad" of its own, owned by the town's faction, caged in the nearest free cage of that faction or an ally (engine: the original's handover is a
  dialogue for event 39).
- **Get in the cage** (172, a Slave AI Goal): a slave not held walks to the nearest free cage of its owner's faction in the 3x3 zone cells round it, in any town, and gets in.
- **Packages** (`PackageClass` 8 to 11, rules in `AiSystem.Captives.cs` as the facts): cannibal retreat (1, 25; it ends when the effective leader's status town is of the squad's faction, so a wiped
  squad keeps it), cannibals at home (27), prisoner gathering (10, 24), slave shipping (11, 28); the cannibal patrol (4) does not start while the town's cages
  are full. A squad's slaves are its live slave members in slave state 1 (engine reading of the platoon's slave list).
- **Capture** (167, `CaptiveSystem.Capture.cs`; facts: [slave work, shout and capture](game/ai-tasks.md#slave-work-shout-and-capture)): started by a standing (posture 0) capturer with one working arm (the lift needs the left); the nearest (3D) downed, uncarried, uncaged character on its sensed list that is
  not of the capturer's side (engine reading of the sensed-class masks; the entries not tagged ally, then whoever within sight the capped block lacks that passes `SightMap.Sees`: `CharacterFacts.Perceives`, shared with the medic finder; so none in another building unless either stands on top; 168's carry the same, its mask excluding imprisoned instead of ally), no slave (state 1), not law enforcement, trader or diplomat and not shackled. The capturer walks to it, strips the `hip` and
  `back` items into its inventory (0.8 s of `medic`), cuffs it (10 s, engine choice; locked stock `SHACKLES` put on, what it wore in that slot goes to its inventory; event 37); the cuff makes it a slave (state 1) of the capturer's faction obeying its squad (`CaptiveSystem.Chain`, the original's `setChainedMode`, [slave work](game/ai-tasks.md#slave-work-shout-and-capture); the original's owner is the cuffer character). Neither 167 nor 168 needs a cage. **Carry** (168): a downed slave owned by the carrier's faction, the fresh captive included, is treated when it needs it (row 25) and lifted (row 170, `TaskSource.Goal`); the lift ends the goal (its desire is 0 while carrying), and the carrier's own goals take the slave on: 112 cages it, 141 follows the leader with it. 168 runs not on Hold (the raw bit, even sneaking: its desire is the rescue one, [goals](game/carry-loot.md#32-goals-and-their-target-finders)). A caged target that got up is beaten down first (196, an armed capturer: a `chop down static` blow per clip, `CombatSystem.Execute`), any other is given up. A target a job gave up on, or that someone else carries off or jails first (a capture only), is left alone for an hour (engine give-up list). **Observed** (probe, Rebirth, real data): a downed character of the player's faction
  beside the garrison leader is stripped, shackled, carried to a cage and enslaved by the Cage Wardens in about 1250 ticks; its squad then remembers the capturer as captor. **Observed** (probe, Stoat, real data): the Slave Traders cuff a downed player character, cage it in a cage of the slave shop and enslave it in about 500 ticks.
- **Liberate** (276): the nearest cage, in the town the worker stands in (else its squad's), of a faction neither its own nor allied, holding a live occupant that is not its
  enemy. 3 s of `medic` at the cage (engine), then `FixtureRegistry.Release` (no crime is booked for an NPC liberator).
- **Eating on the ground** (230 / 233, `CaptiveJobKind.EatBody`; also from the member's own lists): the nearest body within sight that
  the finder takes; the eater walks to it and eats it as a caged meal is eaten (`eat`, event 52, `MedicalState.BeingEaten`, which can kill a living one). The eat action has no dead test (`FUN_140353c50`, `FUN_1404356f0`, `FUN_140650370`, **Verified**): a corpse is bitten the
  same way until no part is left to eat and the passes run out (more than 5), when the original destroys the character with the reason "eaten" (`FUN_140799af0`); Meitou removes the corpse at once (`CaptiveSystem.StepEatBody`, as the body system removes a decayed one). A living one eaten away is not removed but left dead (**not modelled**). A meal that
  gets up (not a robot; the bite wakes one playing dead) is bitten once more, then the job ends and the eater is ordered 35 Running away on it, its queue cleared
  ([order givers](game/ai-tasks.md#order-givers); `CaptiveSystem.RunFrom`, given after the job loop): `FleeSystem` owns 35, runs it as `Task_Runaway` (from the sensed
  enemies, then hops) and drops it once its list A holds (no melee threat, no enemy sensed).
- **Fogman worship** (237 / 236, `CaptiveJobKind.Worship`): in the town nearest it (a standing nest counts, nest markers do not), the first cage whose occupant is being eaten (a running Eat job); within 60 it faces the cage,
  plays `bowing` and barks event 53 every 3-14 game s; ends once the cage is empty or the job grows old (the original runs on while the cage stands).
- **Dogs' limb play** (race goals 277-279): 277 sends an animal with an empty mouth (an adult only when its stomach is empty and hunger at most 2.75) to the nearest limb within
  200, which the pick-up takes into its mouth; 278 (a mouthful, not a player squad's adult over 0.95) sprints in random 1000-1400 legs while it holds it; 279 (any NPC animal, a player
  squad's only under 0.95) runs after the first allied animal in sight with something in its mouth. Engine: the play's first leg is random too. An NPC's 277 is the pick-up (row 3, `TaskSource.Goal`), done once it ends.
- **Crop raiding** (232, `CaptiveJobKind.EatCrops`): in a player town whose faction is its enemy (engine reading of the coexist test), a random farm with food; at it, `eat`, crime
  14 each tick, one plant per game second (`ProductionSystem.EatCrop`). NPC towns' farms are not machines in Meitou, so only the player's are eaten.
- **Escapes** (`EscapeGoals`, [engine goals](#engine-goals), facts: [ai-tasks](game/ai-tasks.md)): 256 Get out of cage (`[256]`, only while its cage's lock is
  not effectively locked), 207 Escape cage (`[207]`) and 202 Escaping (tier 3), chosen by the chooser, in a cage too ([caged](#engine-goals)). 202's plan
  is the planner's: its own shackles picked (201) or cut with a tool (185) in place (the shackle job), taken off (219, into the inventory, else the ground; engine
  choice), then out of the cage (133, 207 or 256, `FixtureRegistry.GetOut` / `GetOutLegit`); a locked cage plans nothing (76 and 285 are not acted out). The act-out
  works the plan's front row and is done when that work ends; 202 is planned afresh at each think and dropped once neither chained nor caged. A cage that stays
  shut refuses the start. Act-outs are hashed while any.
- **Shout** (188): a worker with slaves of its squad's list up and uncaged says event 38 at a random one, walks to it and stands within 40, following it
  when it moves on; it says the event again every 8 s (engine choice) until its worker speaks (`DialogueSystem.Engages`; the row's B list
  `isDialogDelivered` is read differently, [ai-tasks](game/ai-tasks.md)), and the chooser then picks a slave again with no pause. A worker with no
  event-38 line, or with the event lines off, never speaks: its job then ends only at `CaptiveSettings.GiveUpSeconds` (600 s) or when the slave goes
  (engine choice).
- **Work** (189, 190; `AiBehaviour.Work` in the AI, not a job: it competes by score like any goal): a worker of a town with a mine, research bench, refinery or crafting bench (BuildingFunction 1, 3, 4,
  10) walks to a random one (a pick of the character and `AiMind.WorkEpoch`, kept while it works there; 189 only one it can run, `AiSystem.RealWork`: a mine,
  or a machine running there with its inputs, [ai-tasks](game/ai-tasks.md#slave-work-shout-and-capture); each holds one of the workplace's
  `max operators` slots, `AiMind.WorkPost`) to within its functionality's `use range` of its position, or twice that once stopped (30 without one; `AiSystem.AtWork`, [work distance](game/buildings-production.md#work-distance)), faces it and plays its functionality's clip (`stonemine` by default) as
  `ActionClip` and operates the building's town machine ([NPC town machines](game/buildings-production.md#npc-town-machines)); the worker takes the building's `hunger rate` (`AiMind.WorkHungerRate`). **Observed** (probe, Rebirth, real data): 35 workers, slaves among them, stand at the mines of the Holy Nation's towns by day.
- **Obedient Slave** (187, [facts](game/ai-tasks.md#slave-work-shout-and-capture)): for the AI's slaves the Slave role list of the squad they obey, `CharacterCold.SlaveMaster` (after a load, the squad of the owner character its `slaver` handle names, [save](formats/save.md)), in place of their own role's list, whether that squad is another or their own (an empty Slave list leaves them their own role's), for its captive goals too (their look reads the same list, so a slave whose own package lists Slave get in Cage all day goes to its cage only in its master's hours).
  **A player character** has the job in its list (`Kidnap/ObedientSlave.cs`, `CaptiveSystem`): `FixtureRegistry.SetSlave` (every route to slave state 1: sale, arrest, processing, a cage, a delivery) destroys its jobs and order
  queue and adds the job toward the master squad's leader (`CharacterCold.SlaveMaster`; handing a slave to another master resets it, engine choice), and removes it when the state leaves 1 (freed, escaped, bail).
  While it is a slave and not sneaking, the job is urgent (step 3, `IJobSource.Urgent`): the best goal of the master squad's Slave list in its hour and repertoire by desire x finder x weight x crowd (x1.25 the one running) starts through `TaskStart` --
  **171** follows the owner (`MovementSystem.Follow`, row 171; the follower sets its sneaking from the leader's every update, **Verified**, so a following slave cannot sneak away), **189 / 190** walk to a random workplace of the town
  the slave's squad leader stands in (`AiSystem.WorkAt`, the AI's pick, 189 only one it can run) and stand at it at the work clip (`CharacterCold.SlaveWork`; a handler of rows 189 / 190 clears both when another task takes
  the slave), holding one of its operator slots meanwhile: both take the functionality's `hunger rate` (`BodySystem`), 189 works the town machine as the AI's plan does (`ProductionSystem.ObedientWorkers`: a full output into the inventory, else an empty input
  filled from what it carries, else operating; `ObedientJobs.ErrandFor`: walks to make room, to fetch an input from another machine's output, and to put the machine's carried output into a storage,
  [ai-tasks](game/ai-tasks.md#slave-work-shout-and-capture)), 190 operates it as 189's row 87 does whatever it holds (row 221 is `Task_OperateMachine` too: the work step and the operating XP, `ProductionSystem.ObedientWorkers`),
  **172** walks into the nearest free cage of the owner's faction (`CaptiveSystem.OwnCage`, `FixtureRegistry.Imprison`) and stays there until let out (the original has no own release: caged, its 171 / 189 / 190 plan nothing, **Verified**), **41** / **143** fight as the AI's combat goals (`AiSystem.ObedientFight`, a job fight),
  **35** runs from the sensed enemies (`FleeSystem.EnemyRunPoint`; only with no working arm, the player-faction veto), **14** stands. An order beats it (step 1) and it beats Self preservation and the other goals; the Jobs switch
  off stops it (as any job). Its senses side with the master (`AggroSystem.MasterSpares`, [sense tags](game/perception.md#sense-tags-who-is-an-enemy)): never the master's faction, nor anyone the master calls friendly; chained, the master's side never picks it as a fight target (`TargetScore.SparesSlave`).
  `SlaveMaster` is runtime only, so after a load the master comes back from the save's `slaver` handle or the job's saved handle (a character, `CaptiveSystem.RestoreSlavers`), else the slave obeys nobody; 41's finder and the senses read it.
- **Eaten**: a meal still awake gains toughness XP (`XpService.Continuous`, amount 10). **Process** (183) re-processes a slave of an allied owner that is not shaven or does not look like one (`CaptiveSystem.LooksLikeASlave`: the watcher's
  faction's slave clothing or garments on torso and legs, locked shackles or a locked torso piece on a shaven head), or an escaped or ex-slave (state 2, 3).
- **Status lines**: a slave, escaped slave or ex-slave selected shows the bounty panel with its status rows ("Slave" / "Escaped slave" / "Ex-slave" and the owner's text, the player-character hints) in its tooltip, ahead of the
  bounty rows (`CharacterSnapshot.Slave`, `TooltipRuntime.SlaveRows`; [ui-screens, information panel](game/ui-screens.md#4-hud-mainbargui)). The tooltip's "Looks like a slave", "Situation" and "Time remaining" rows are not built.
- **Squad memory**: a caged captive's squad remembers the captor (5, through `FactionDeeds.CagedHook`), a freed prisoner's squad the liberator (3 and TEMP ALLY, event 13), a sold slave's squad the customer
  (5), a bought one's the buyer (3).
- **Shipping off screen**: the shipping squads are town roaming squads and homeless spawns (base data), so the original despawns them as they leave (Faithful). A kept stand-in
  (`KeepRoamers`) whose package is a shipping one (TRAVEL_TARGET, [ai](game/ai.md#unknowns)) with a living slave goes on at 25 to the camp its package chose (none chosen: the
  nearest slave camp not hostile to it) and stays there, the original's TRAVEL_TARGET rule ([off-screen world](game/factions-squads-towns.md#off-screen-world)); the handover
  waits for it to load (`PopulationSystem.ShippingCamp`); a save's travelling platoon not made yet ships too, its slaves read from the save (slave role, `slavestate` 1), to the nearest
  such camp (the save's mission town is not read). Not
  shipped off screen: wild ones (a save's homeless platoons and the squads `KeepWild` keeps), which their sector makes again where they stood, so they stay there as other wild squads.
- Not built: goal 112 as such (captives are carried by the kidnapping system) and short-term tag 6 of the strip. Town workers run real machines:
  [NPC town machines](game/buildings-production.md#npc-town-machines).

## Buildings

Player construction contract (Faithful UI). Facts: [construction](game/buildings-production.md#construction),
[placement](game/buildings-production.md#placement), [build menu](game/buildings-production.md#build-menu),
[build tasks](game/ai-tasks.md#build-tasks), [save keys](formats/zones.md#placement-keys). Outposts have no
residents in the original ([the outpost](game/buildings-production.md#placement)) and are on the [map](game/ui-screens.md#map); raids on them: [campaigns](#off-screen-tier). Not built: the door x3 rule ("Wall termites" is disabled in the original).
Damage: melee blows on walls and doors (`CombatSystem.ApplyZoneBuildings`, the original's only damage,
[repair](game/buildings-production.md#work-distance)). Production, power and research: [Production](#production).

**Shared types** (stubs f4cb78c, filled by the simulation track):
- `Meitou.Data/Gameplay/Construction/BuildingBuildData.cs`: `BuildingBuildData.From/LoadAll` (build fields, material
  entries), `MaterialCost`, `Target` (sum of required), `Rate` (`build speed mult` / (1200/11)), `EstimatedHours(buildSpeed)`
  (the build window's estimate, **Verified** algebra: materials / (`build speed mult` x CONSTANTS `build speed` x option)).
- `.../ResearchGate.cs`: `StartResearch(db, start)` (the start-off's `research`, or the default start research when it lists none),
  `Unlocked(db, completed)` (union of `enable buildings`, BUILDING targets only), `DefaultStartId`.
- `.../PlacementRules.cs`: `PlacementFailure` (one value per original message), `PlacementTown`,
  `TownBlock(BuildingBuildData, Vector2 at, IReadOnlyList<PlacementTown>, string playerFactionId)`, `Message(PlacementFailure)`
  (the original's texts; `InvalidFloor` is "Invalid placing floor number." because the rest of that text is not in the
  docs, `NotAvailable` is the engine's "That building is not available yet.").
- `Meitou.Simulation/Construction/ConstructionCommands.cs`: `PlannedBuilding(Record, Position, Rotation, Floor, Parent)`;
  `PlaceBuildings(Plans)` (Confirm), `BuildOrder(Characters, Site, Repair)` (orders 2/95), `DismantleOrder(Characters, Site)`
  (96), `ConfirmDismantle(Site)` (the selection panel's Dismantle: the player's site at once, its furniture destroyed, refused while a non-furniture building stands in it;
  [selected building](game/ui-screens.md#selected-building)), `CancelSite(Site)` (engine: only a site with nothing delivered), `PauseSite(Site, Paused)`, `UpgradeSite(Site)` / `DowngradeSite(Site)`
  ([upgrades](game/buildings-production.md#upgrades)), `SetBuildingPublic(Building, Public)` (a site's `Public`, else an owned town building's
  flag in `World.Fixtures`). Sites are named by instance id.
- `.../ConstructionSnapshot.cs` + `WorldSnapshot`: `Buildings` (`BuildingSnapshot`: `Id`, `Record`, `Position` absolute,
  `Rotation`, `Floor`, `Parent`, `Owner` faction index, `Progress`/`Target` in material units, `Materials` (item,
  required, delivered), `Complete`, `Paused`, `Dismantling`, `Public`, `TownName`), `BuildingsRevision`, `UnlockedBuildings`.

**Simulation state.** `World.Buildings` (`Construction.BuildingRegistry`, sites as `BuildSite`): player-built sites in
creation order, by id, holding the snapshot's fields plus rate, builder counter and the destroyed flags. Owned town houses needing repair also register, with `LevelPlacement` and a zone-qualified id; completed restored houses stay registered for their scene overlay. Ids `<n>-zone<X>.<Y>-INGAME`, n from a per-zone counter (hashed) that starts above every such id
in the loaded save (generated interiors share the pattern; `Reserve(id)`); a number whose id a site holds or the zone's level building list
holds with a target (`LevelBuildingIds`, asked live so level-editor buildings count; 106 such level ids) is skipped. Engine choice: the id is
issued at placement from a counter of player sites, where the original issues it at the zone's next save from the zone container's counter, which
every record of every zone save and unload advances ([id rule](formats/zones.md#containers-and-ids)). `World.Research` (`Construction.ResearchState`): completed RESEARCH ids and the
`Unlocked` set (replaced, never changed in place). A new game completes the start-off's `research`, or the default start research
(`ResearchGate.DefaultStartId`, [build menu](game/buildings-production.md#build-menu)) when it lists none
(`PopulationSystem.StartPlayer`); a load takes the save's. Research benches change it ([Production](#production), which says when it is hashed).

**Town-house repairs.** `FixtureRegistry` registers an owned damaged/incomplete house on purchase or town activation.
Buying a ruin leaves it destroyed; an explicit Repair order from an able selected player character starts rebuilding,
applying the [ruin transition](game/buildings-production.md#construction-tick) once and restoring doors and indoor sight.
Build, DeliverMaterials and FetchMaterials use the same work and resource rules as new construction. Shift also adds
Engineer (125), which resumes available work after supplies arrive; a direct order stops when no source exists.
Jobs retain their queue priority and switch. Queued orders ([order queue](formats/save.md#order-queue)), persistent jobs and the house's progress,
delivered materials and ownership are ([save](formats/save.md#zone-files)). Normal damage to a complete house needs no materials.
Town houses cannot be dismantled, cancelled or upgraded through this seam. The original town and instance remain unchanged.
Building inside one names the level instance as parent (as any owned town building; build mode's floor is the level one), refused while it is being repaired.
**Verified** implementation coverage: `HouseRepairTests` and `HouseRepairUiTests` (both UI modes, original Storm House data).

**Outposts.** `World.Buildings.Towns` (`PlayerTown`: `Number` 1.., `Name`, `Position`, `Owner`, `Public`; never removed; renamed and ticked public by the [town panel](game/ui-screens.md#town-panel)) and
`BuildSite.Town` (a number, 0 none). `PlaceBuildings` gives each new site its town by the
[outpost rule](game/buildings-production.md#placement): the parent's (none in a level town's building); else the nearest player town within 5000 (X/Z);
else the town of the nearest player site within 2500; else a `creates player town` non-wall makes "Your Outpost" at its
position, and every player site whose nearest town (level towns and outposts) within 8000 is it moves in. After the
batch, `links together` sites still without a town take the batch's last player town, else the nearest town within 16000
of their mean, else a new outpost there; a level town of another faction leaves them without one (engine:
`BuildSite.Town` names player towns only). After each placement, removal and load the town's `Position` and `Override`
radius are [reassessed](game/buildings-production.md#placement) (`PlayerTown.Radius`, what campaigns, the AI's current town and
dialogue use). Hashed (towns, and `Town` when set) only once there is one.

**ConstructionSystem** (`StandardParts.Construction`, in `All`; `StandardSystemSet.Construction`) runs after Carry, before
Combat; all of it is serial. `ConstructionSettings`: `BuildingSpeed` (the option, 1), `Footprint` (record -> radius,
optional), `EdgeGap` (site, point -> gap to its footprint hull; the host's).
- `Inputs`: `PlaceBuildings` re-checks only the data half (a BUILDING, unlocked, `TownBlock`); a failing plan is dropped
  with `World.Post(Message)`. A total of 0 completes at once, so a free building is complete from the first snapshot.
  `BuildOrder` (player characters, awake, not carried or caged, with a working arm: row 2 needs state 38 `hasOneWorkingArm`, **Observed**, and the engineer job likewise; Build and Repair alike work a site that `NeedsWork`:
  unfinished, or complete with progress below its target, the original's damage) and `DismantleOrder` set the
  `CharacterTask`s `Build` / `Dismantle` and `CharacterCold.BuildSite`
  (the selection when the list is empty). Dismantling is refused, by the builder's bark when the order starts, with the original's texts while a site stands in the
  site ("I have to dismantle the furniture first." for interior furniture and nodes, else "I have to unmount the
  buildings first."). `CancelSite` removes an incomplete site with nothing delivered, else posts "Dismantle it to get the
  materials back."; `PauseSite` sets `Paused` (only the auto-job skips it). `PlaceBuildings`, `CancelSite`, `ConfirmDismantle` and `PauseSite`
  also apply while paused (`ApplyPausedInterfaceCommands`).
- `Schedule`: a worker walks to the site like a medic to a patient (goal = site XZ, free speed, asked again after a wait
  when the path failed). It has arrived within the [work distance](game/buildings-production.md#work-distance) (`SiteReach`:
  40, walls 60, twice that once stopped, from the footprint hull). A fetcher walks to its source instead (a storage within its
  `use range`; a ground item within `PickUps.PickUpReach`, twice that once stopped; a source that is gone sends it back
  to building). Work ends when the site is gone,
  no longer `NeedsWork` (building), the character is down, carried, carrying or caged, or another task replaced it (a fight
  too: its start ends the work, [task starts](#task-starts)).
- `SlowWorld`: arrived workers in slot order. *Build*: a stalled site (`sum(min(delivered, required)) <= progress`) turns
  into *DeliverMaterials* if the builder carries a missing item, else into *FetchMaterials* when `Source` finds one, else
  the work ends;
  otherwise progress += rate x `lerp(0.3, 2, e/100)` x dt x `BuildingSpeed` x CONSTANTS `build speed` x 0.75^k, e the
  engineering stat times its injury multiplier (engine choice), dt = `TickSeconds`, k the builders that already worked
  the site this tick (capped at 15, above
  100 it restarts), engineering XP (`XpService.Engineering`); a rate of 0 or less completes at once; at
  `progress >= target` the site completes (`Complete`, message "Building '<name>' construction complete." for the player's
  faction); a repair (complete, never stalled, no materials) stops at the target. *DeliverMaterials*: the bar fills at
  0.2 per second (`CharacterCold.BuildBar`); full, it hands over one unit of each missing type (inventory first, then
  backpack) and the task returns to *Build*.
  *FetchMaterials* (`CharacterCold.FetchSite` / `FetchItem` / `FetchRecord`, hashed with the site): `Source` follows the
  [original's finder](game/buildings-production.md#material-delivery) for each owed material in list order: a loose item
  of it within 1200 of the builder (engine: the nearest), else the nearest player storage holding it, else the nearest
  player machine whose output holds it, both within squared distance 4.246733e7 (the AI's default range mode; engine:
  no town filter, no remembered source, exact record). At a building it takes up to 2 units while it has room, a loose
  item whole (the rest stays); with something taken it walks back as *Build*, else the work ends. *Dismantle*: a complete site first
  becomes incomplete with progress at most target - 1; then progress -= dt x `lerp(0.05, 4, e/100)` x 4 x rate (no option
  or constant), XP as building; at 0 the delivered materials come back and the site is removed. The `medic` clip plays
  for building and delivering, `stonemine` for dismantling (`AnimationSystem.HeldClip`).
- Refund: entry i with something delivered gives `max(1, trunc(U x delivered))` units, U = m + (1 - m) x
  `Rng.Float(Rng.Hash(seed, StableHash(id), RngPurpose.Build, i))`, m = CONSTANTS `min dismantle materials percentage` /
  100; each unit is its own ground item (`GroundItems.Drop`, yaw 0) at x, z = position + (2u - 1) r, u from streams
  `2000 + (i << 16) + 2k` and `+1`, r = the footprint radius, at least `Reach` (engine: the original's bounding box is
  not in the sim). A destroyed flag (a save's, or a wall line hit to 0) turns a player-built building into a ruin on the next tick; a town house waits for Repair
  (**Verified** rule: progress 0.4 x target, delivered floor(delivered x U[0.3, 0.5) + 0.5)).
- Engineer job (125, run by the [job lists](#production)): `EngineerSite` scores the faction's sites that `NeedsWork`,
  not paused, destroyed, attached or being dismantled, `max(2 (1 - d^2/9000^2), 0.0001) / (n + 1)` (d 3D, n others
  working it), skipping a stalled site the character carries nothing for and `Source` cannot feed; `StartBuilding` sends
  it there. Without the job nobody builds unordered (the original has no other auto-build).
- `BuildingsRevision` bumps on add, complete, a complete building becoming a site (dismantling started), destroyed and
  remove. `Version` (progress, delivery, pause) only rebuilds the published list.

**Hash and determinism.** `World.StateHash` adds the registry (per-zone counters, then each site's fields in creation
order, then the outposts) only once it holds a site or an outpost, like `Fixtures`; `CharacterCold.BuildSite` with its bar and resume task hashes only
when set. Existing golden hashes are unchanged while there are no sites; the `build` scenario pins one with sites.
Decisions read only world state and command order, never collision or navmesh readiness (paths stay the usual
asynchronous input).

**Host seam: snapshot diff.** The host keeps the last `BuildingsRevision` it acted on (-1 after `ReplaceWorld`) and,
when it differs, diffs `Buildings` by id: added → render overlay (and navmesh if it qualifies), removed → out of both,
newly complete → navmesh and lamps (lamps leave when it is no longer complete). Progress does not bump the revision; the scaffold reads `Progress/Target` each frame (rule in
[construction look](game/buildings-production.md#construction-look)). An event queue would lose changes across dropped
ticks and loads; the diff does not. Navmesh membership (engine reading of the Observed rebuild on placing):
`PathMode != 0` and (`Complete` or `BuildThreshold == 0`). Rebuilt zones that publish go into the host's next
`RepathCommand` (with its zones, [long routes](game/pathfinding.md)).
- Overlay type: `BuildingPlacement` (`StateId` null for new buildings, `WorldY` = absolute Y). Town repairs retain the
  original state and instance ids, overriding only `Destroyed`; render and nav replace by global `Key`, never append a second house; removing the override lays its zone out again from the level data.
  A repaired town house replaces only its objects and interior in the resident zone; floors are invalidated and its nav overlay applies from repair start even below build threshold.
  House lamps and layout lamps replace the old lights, with none during reconstruction.
- Rendering: `WorldObjects.AddRuntime/RemoveRuntime(BuildingPlacement)` merged into `BuildZone`. Engine choice: a loaded
  zone takes the placement incrementally (no reload); the scaffold is an approximation ([render-objects](render-objects.md#player-buildings-and-the-placement-ghost)).
- Navigation: one thread-safe overlay store shared by every `ZoneGeometryGatherer` (`NearBuildings` and `BuildingHash`
  see it, so the cache key changes), `NavSystem.AddRuntimeBuilding/RemoveRuntimeBuilding` and `RebuildZone(zone)` for
  each zone within `BuildingReach`.

**Placement validation.** Host: `Meitou.Game/Building/PlacementCheck.Evaluate(PlacementQuery) -> PlacementFailure`,
pure over the query (build data, transform, floor, queued ghosts, snapshot characters and sites, towns) and services
(heightmap, collision cache, water level). It calls `PlacementRules.TownBlock` first and returns the first failure in
the original's order. The geometric half depends on asynchronously loaded collision, so the simulation trusts it and
re-checks only the town block. Ghost controls, tilt and the unconfirmed ghost queue (Undo) are host UI state.
Implemented host-side, in the original's order: town block, resource row (`PlacementRules.Readout` /
`ResourceBlock`: a farm's fertility factor or crop yield (x crop match), or a `world resource mining` building's
`ResourceField.Yield`, below 0.01; no readout without an areas map, or for iron/copper/carbon without known rocks; the
readout is also the ghost's popup, `BuildMode`, a floating tooltip centred over the collision box's top), site under the ghost, static building under it, interior
rule, footprint overlap with static buildings, sites and queued ghosts (convex hulls of the collision shape, SAT),
characters within 2.5 of the hull, LAND parts below the water level 98, slope (four rotated bounding-box corners, `max slope`,
`match slope` tilts fully) and normal.y < 0.2. **Indoors:** the ghost (not walls) stands on the first building floor the
cursor ray meets before the terrain, using the movement pick up to the viewed floor (the build window's floor arrows), untilted;
it queues with that building as parent (a site's id, a level building's instance id) and its floor. The building must be
a finished player site or a level building the player's faction owns (its state's `owner faction ID`, else its town's
faction; else the original's "don't own" / "incomplete" texts); the parent is left out of the overlap tests, the slope test is skipped (floor taken as
level), and interior-only furniture is refused only off a floor. Engine stand-in for the original's node boxes against the
building's walls: the footprint must lie within the building's indoor hull. **Not decided** (never fail, so the ghost may
show valid where the original refuses): snap nodes, usage nodes, blocked use nodes, the floor-number
check above floor 0, vertical margins, the 5 deg object-surface rule. A `ceiling placement` ghost hangs its origin 1 below the
lowest collision over the floor point (a slab's or roof's underside, from 5 up; its meshes hang below their origin, Ceiling Lamp
y -19..+4) and fails with the floor-number text when there is none. A site inside another (its parent) is picked through
the parent's roof and walls while the interior view shows the parent; a site in a level building takes the usual town rules. Messages: `PlacementRules.Message`, else the original's text table in `PlacementCheck`.

Build mode (B, `Kenshi_BuildWindow`, Faithful UI only): categories and groups list `WorldSnapshot.UnlockedBuildings` in
the original's order ([build menu](game/buildings-production.md#build-menu)) and the Commands box shows its text (`BuildMode.BuildCommandsText`); the window estimates hours as materials / (`build speed mult` x 6),
rounded up (rounding **Unknown**). Left click or Space queues a valid ghost, Backspace or Undo drops the last, Confirm sends
`PlaceBuildings`; leaving build mode drops the queue. The HUD panels the window covers are hidden meanwhile. Right click
on a site with characters selected gives `BuildOrder` (held 0.2 s: menu with Build, Dismantle -> `DismantleOrder`, Cancel
Construction -> `CancelSite` while nothing is delivered); the pointer is `build` over a site.

Walls (`Building/WallLine.cs`, facts in [walls](game/buildings-production.md#walls)): a `links together` normal wall, or
a snapped tower/gate, is laid as a line: first click (or Space) starts it at the ghost, which snaps to the nearest link
point of a queued wall or player wall site; each frame `WallLine.Layout` lays bend pieces at snapped ends and the
straight run to the cursor (end snapped to a start, or an end for a backwards line), and every piece runs `Evaluate`
as a `WallPiece` (own pitch for slope; `Ignore` skips connectors, wedges and walls at a snapped joint). The next click
queues every piece as its own ghost and chains a line from the far end; right click or Backspace drops the line. Each
piece becomes its own site; `ConstructionSystem` allows a subsection when a BUILDING listing it in `wall subsections`
is unlocked. `ConstructionSystem.AttachWalls` (after placing, and at the end of `BuildingSave.Read`) gives each short,
connector or wedge without parent or children a `WallParent`: the root of the first wall site, with the same complete
flag, that has a link point within sqrt 10 of one of the piece's link points or its middle (engine choice: the middle
catches crest shorts; a normal/single root wins over a subsection root; gates never parent). Orders, builders and the
auto-job act on the root; its progress sets the children's (`min(p/target, 1) x child target`) and completes them; its
removal removes them. So an attached piece costs nothing; the link is not saved or hashed. A destroyed attached piece
just follows its wall again (original **Unknown**). Level (NPC town) walls are snap candidates and, at a snapped joint, do not block a
piece as sites and ghosts do not (`PlacementCheck.Blocks`, [snapping](game/buildings-production.md#walls)). **Engine gaps**: indoor lines (radius 25), the steep-climb straight extension, the offset clamp and the arrows are not built; the stretch
is recomputed each frame from the signed overshoot. The snapshot carries `WallParent`; picking an attached piece in the
UI picks its wall (name, verbs and Cancel's nothing-delivered check are the wall's).

**Saves.** `Saving/BuildingSave.cs` writes each site as a GAMESTATE_BUILDING (`<id>-S23`) plus its entry in the zone
file's `0-buildinglist` (keys per [zones](formats/zones.md#placement-keys)); a zone without a file gets one, the slot
list and the handle (`C` = zone container, `CS` 11111, next free `I`) follow, and player-built entries the world no
longer has (a dismantled building) are removed. The instance Y is the height above the terrain, as in all game-made
`-zoneX.Y-INGAME` buildings of the samples ([placement height](formats/zones.md#placement-height)); a load prefers `world Y pos`. `is complete` is written
explicitly. The parent is `isInsideBuilding` plus Meitou's `meitou parent` (instance id, wins on load). On load it
reserves every `-zoneX.Y-INGAME` id of the save and reads the player faction's such buildings back, in zone and list
order (the player's start has none, base placements use other container names). `town` is the outpost's handle,
`residents` the null handle. Outposts go to `quick.save` as [town-list entries](formats/save.md#player-outposts)
(made when the save has no list); an outpost read back keeps its entry id, state and handle, a new one gets the next
town container `C` and a seeded `CS`; a load reads the
player faction's non-nest entries in list order and matches buildings by the handle's `C`/`CS`. The state's name
is "Town state <name>" and gives the name back; a new state is written `discovered` and `explored` (Meitou choice). `Saving/ResearchSave.cs` reads and writes the RESEARCH record's `finished<k>`
and `num finished`. Not compared with a real file that has a player-built building (the sample saves have none).
The first snapshot after a load carries every site, so the host seam needs nothing extra.

**Files.** Simulation track: `Meitou.Data/Gameplay/Construction/*`, `Meitou.Simulation/Construction/*`,
`StandardSystems.cs`, `World.cs`, `World.Interface.cs`, `WorldPublish.cs`, `Movement.cs`, `CharacterTable.cs` (cold fields),
`Rng.cs`, `PopulationSystem.Player.cs`, `AnimationSystem.cs` (held clips), `Saving/*`, `Meitou.Data/Save/SaveBuilder.cs`
(a comment). Host track:
`Meitou.Game/BuildingSync` (diff, overlays, rebuilds, repath), `BuildMode` (Kenshi_BuildWindow,
ghost, commands), `Building/PlacementCheck.cs`, `Building/BuildMenu.cs` (categories and groups from
`BuildingBuildData` and `UnlockedBuildings`), `KeyMap.cs` (build map), `ContextMenuWindow` and
`WorldPicking` (site verbs), `GameLoop` (hook), `Meitou.Game/Host` if needed,
`Meitou.Game/Ui/*`; `Meitou.Game/Rendering/Objects/WorldObjects.cs`, `ObjectStreamer.cs` and the object shader plumbing;
`Meitou.Simulation/Navigation/NavSystem.cs`, `NavMeshService.cs`, `ZoneGeometryGatherer*.cs`, new `NavRuntimeBuildings.cs`.

## Production

`Production/ProductionSystem` runs the player's complete buildings: mines, wells, refiners (function 4), farms, research and
crafting benches, generators and batteries. Facts: [production](game/buildings-production.md#production), [crafting](game/buildings-production.md#crafting),
[farming](game/buildings-production.md#farming), [research](game/buildings-production.md#research), [power](game/buildings-production.md#power).
- **Machines** (`Production/Machines.cs`, data `Construction/MachineData.cs`). A machine is made when a player site completes
  or loads complete, and dropped with the site; its items fall on the ground. It holds:
  - the recipe inputs and the output, as item counts plus a level or progress (engine choice: crops and water have no quality);
  - the storage grid of a `has inventory` building (a bench's books), as a real `Inventory`;
  - the farm's plants.
- **Yield.** Set at creation from the ground under the building: a resource's value (`ResourceField.Yield`; 0 stays 0 for the player), ore from the
  nearest foliage rock (`FoliageResourceNodes`, given by the host; none headless), and for a farm its
  fertility factor, crop match and water rate.
  - Fertility and the crop match come from the BIOME_GROUP's ENVIRONMENT_RESOURCES at the weather-area cell.
  - The altitude test uses the building's y.
  - **Observed** with the vanilla map: only the central biomes give farming ground.
- **Operating.** `OperateOrder` sets `CharacterTask.Operate`. The character walks within the building's `use range` ([work
  distance](game/buildings-production.md#work-distance)) and works it every tick; work is scaled by its skill factor and trains the stat.
  - Producers make output from their inputs. An item furnace burns its grid into Raw Iron while its Active button is on ([item furnace](game/buildings-production.md#item-furnace)).
  - Farms are harvested and cleared.
  - Research benches add to the research queue; crafting benches work their queue.
  - More characters than `max operators` are refused with "X is already in use.".
  - Farms tick whether anyone is there; rain tops up an outdoor farm's water (`FarmTick`, [farming](game/buildings-production.md#farming)). Unloaded zones: the *Production far away* switch (Meitou on: farms, automatic machines and generator fuel
    run everywhere, and an NPC farm outside the active zones grows as if watered, as the catch-up treats it (`WateredFarAway`; a dry town
    farm, e.g. UCTM's Heft, #918, would otherwise never grow); Faithful off: only in `World.ActiveZones`, and a farm back after over an hour runs `TimeSkip` from its `save time` stamp,
    `FarmState.LiveHours`); facts in [unloaded zones](game/buildings-production.md#unloaded-zones).
- **Rocks** (`ProductionSystem.Natural.cs`, UI `FurnitureUse`; facts in [resources](game/buildings-production.md#resources)). A foliage
  rock is a natural site, operated like a mine: a right click on it with characters selected is an `OperateOrder`, with Shift an Operating machine
  job. Its site id `natural:<BUILDING>@x,y,z` (the rock's position) is all the sim needs: the site is made from it (a BUILDING
  whose machine is operable, whatever its `world resource mining`; with a rock source, one of it must stand there), owned by no faction, so player-only lookups skip it. Its machine
  yields `ResourceField.NodeYield` (at least 0.1) and is kept in memory, not saved; a saved job keeps the id (`meitou pjob<i>`). The ore stays in
  the rock's output (5) until taken: a left click selects the rock and opens its machine window once it has been mined (**Observed** by the
  owner: the base game's ore is picked up by hand), or a storage within the haulers' 4.246733e7 bound takes it as for any machine. Engine choices: the rock is picked by its mesh's triangles where the instance places them (the original picks its building's collision); its radius, the longest half-extent of the mesh's bounds box scaled by the instance and at least `NodeRadius` 25, sizes its resource richness (the original takes the mesh's bounds the same way); without the mesh file a rock is picked as a sphere of 25 round its origin; the pointer over a rock is `mine` for a mine, else `use` (with characters selected, after sites and doors, as the click order is); level-placed `is foliage`
  buildings (not drawn, not in `FoliageResourceNodes`) cannot be mined. An NPC town's farms and producers are machines of their own from its registration, saved in their buildings' zone states (`TownWork`, [NPC town machines](game/buildings-production.md#npc-town-machines)).
- **Town training dummies** (`ProductionSystem.TownDummy`, fixtures `FixtureKind.Training`, UI `FurnitureUse`). A town building's BF_TRAINING
  furniture (its interior layout's, or the building itself) is a fixture `<building>@dummy<n>` (n in layout order) of the building's owner; like a rock it is no
  registry site, so it is neither drawn, saved nor built: the production system makes a complete site of it once its town has registered, and its machine is kept in
  memory, not saved. A right click with characters selected is an `OperateOrder` (Training, 97), with Shift the Training job, whoever owns the dummy
  ([UseableStuff verbs](game/ui-input.md#context-menu-verbs)); a saved order or job keeps the id. Picked as a bed is (its collision, else its middle within
  30 px); pointer `use`. Selected, its panel shows `Training:`, Workers and Max Skill Level ([Selected building](game/ui-screens.md#selected-building)). One user at a time across both sides: a resident pretend-operating it (221, town goals) counts as an operator, so an order or job finds it in use, and a dummy a character operates (walking there included) is skipped by the residents' staying-home and sitting-around claims, a held claim dropped (`ProductionSystem.OperatorsOf`, `TownPlaceClaims`).
- **Items.** `PutInMachine` and `TakeFromMachine` move items between a character within 40 of the building's footprint and the machine:
  inputs, output, or storage cells.
- **Research** (`Construction/ResearchState`). A queue paid at queue time from the benches' storage; a paid id is never
  charged again.
  - Bench level is the best `tech level` a complete player bench reached; it never falls (0 again on a load).
  - Several operators add with the 0.6^k falloff.
  - Partial progress is kept off the queue (in memory only).
  - Blueprints ([rules](game/buildings-production.md#blueprints)): `GameDatabase.AddGearBlueprints` makes the `.TECH.1` research after loading;
    `ItemFactory.Blueprint` makes the item (type `BLUEPRINT`); the own-window right click (`QuickMoves.QuickMove`) calls `ProductionSystem.Learn`.
- **Hash.** Machines and research join the world hash only once the player has one; golden hashes are unchanged. Yields read the
  biome map and rock source, so a replay without them (headless) hashes other yields than the host.
  RNG: `RngPurpose.Farm` (replanting, harvest rolls), `RngPurpose.Craft` (critical rolls).
- **Crafting** (`ProductionSystem.Crafting.cs`, data `Construction/CraftingData.cs`). `QueueCraft` (at most 20),
  `CancelCraft`, `RepeatCrafts`. The first craft binds the input sections to its ingredients (other items in a section
  fall on the ground); finished items wait in the output (section `out`) up to the option's store, then the bench reports
  output full. Benches listing fewer `consumes` than a recipe's ingredients get extra sections. A repeating queue puts
  each finished craft back at its end. Crafts no research lists are always available.
- **Research repeats.** A `repeats` research can be done repeats+1 times, each paid at `cost x repeat mult^n`.
- **Power** (`ProductionSystem.Power.cs`). Generators (fuelled or wind), batteries and consumers are machines; each
  town (sites grouped by `Town`, 0 included) shares power as the [original](game/buildings-production.md#power) after the
  operators, so grants follow last tick's operators. The factor scales production, crafting, research and farm growth;
  research `improve buildings` (`ResearchState.Upgrades`) scales rates and adds output and capacity. Engine choices: health
  is 1; wind generators give their blades' share of the region's wind ([rotating parts](game/buildings-production.md#rotating-parts)), loaded or not; battery mode is not saved. HUD: output, fuel use, charge,
  "No power" / "Low power". A level town's frame walks only its buildings and layout objects whose records can draw or generate
  (`LevelPowerParts`, made again when the town's list is swapped), and a layout object's saved entry has its zone's `imported` flag from when the
  save's entries were indexed (`SavedPiece.Fresh`: finding the zone's building list scans the zone file's records).
  **Observed** (play bench, `bench-bases-2026-10-27`, two interleaved pairs, 2026-11-19, about 50-60 drawing objects in each NPC town frame):
  `ProductionSystem` 1.3-1.7 -> 0.5-0.6 ms a tick at 5x, 8.9-10.2 -> 2.0-2.2 ms a frame in speed-x5 and paths; headless probe of the save at 5x
  (`--nav`, 3,300 ticks): state hashes identical.
- **Hauling** (`ProductionSystem.Hauling.cs`, `ProductionSettings.AutoHaul`, default on; facts in
  [buildings-production](game/buildings-production.md#hauling)). An operator at its machine first puts carried items
  into empty input room. It takes a full output only when a storage will accept it (else the machine stays full) and
  carries it to the nearest player storage (function 2 or 13) that accepts it and has room; or it fetches an empty input's item from the nearest storage holding it or machine whose output holds it (a well's water for a farm). A
  storage accepts an item when `itemtype limit` matches and `limit inventory` (a function 2 storage's only) is empty or lists it; `stackable bonus
  mult` scales its stacks. The player's drops into a storage follow the same rule ("does not take").
  Excess fetched inputs stay with the operator. A fetch's source and a storage taking output are within squared distance 4.246733e7 (3D)
  of the machine, the bound of the original's fill and per-item storage finders in the default ROAM_FAR mode (`FUN_14059d000`,
  `FUN_14059acd0`, [material delivery](game/buildings-production.md#material-delivery); they measure from the character, who stands at the
  machine) (#963, #965). Engine choices: ties by site id, a fetch takes what the inputs have room for; the trip is not saved. A crafting bench's output is its finished items: an operator
  takes those a storage accepts once the first craft's store is full, and a full bench whose items no storage takes is no work (the job passes
  on); a "Hauling to" job fetches finished items as it does a producer's output, the instances themselves (quality and model kept) (#762). Carried output
  goes only with a full output just taken, or while the output stays full: a stray unit sends no one on a trip (#388). A walker
  stopped within reach of a site whose height has no floor (furniture on an upper storey the navmesh lacks, the Hub's Storm House)
  counts as there (`ProductionSystem.OffFloor`).
- **Automatic machines** (`MachineData.IsAutomatic`, raw `max operators` 0; [facts](game/buildings-production.md#production)) produce
  every tick with nobody at them at s = 1 (producers only: refineries, mines, auto-looms, campfires), and their power demand is full. A character
  operating one (job 152, or the quick order) does not work it: it only hauls as above, every round whatever `AutoHaul` says.
- **Job lists** (`PermaJobs.cs`, `ProductionSystem.Jobs.cs`, `Saving/PermaJobSave`; facts in [perma-jobs](game/ai-tasks.md#perma-jobs)).
  `CharacterCold.Jobs` is the ordered list; `AddJob` (Shift + right click on a finished machine or storage) adds through the original's rules
  (remap, duplicate refusal, companion, switch on); `RemoveJob` is the list's X. Every job runs through the [decision step](#decision-step) (step 5, 234 also step 3): the first job of the
  list that can run starts, so a higher job takes over as soon as it can run and a blocked one passes to the next. The production pick's jobs below (`ProductionSystem.ListJobs`) run while
  `Runnable` holds: a job whose site (or the engineer finder's pick) is `JobRangeSq` 4.246733e7 (≈ 6517, 3D) or more from the character is skipped
  ([range](game/ai-tasks.md#perma-jobs); players are treated as ROAM_FAR and the home-town exception is left out), as is every job but Bodyguard / Staying close while the
  character gets up (`GettingUpUntil`), and Operating machine, Turret guard and Training without a working arm; a carrier or a carried character looks at none and keeps the one it is on;
  an order replaces the job's task, and the list resumes once the order is done. A job that ends leaves the character idle, decided again at once. The other jobs'
  contributors are further below:
  - 97 Training (Shift + right click on a training dummy, function 7, or the "Training" verb): the Operate task at the dummy; `Train` raises the building's `stat used` by the capped continuous
    XP entry (`XpService.Capped`, rate 2, cap = the building's `+0x1a4`, `MachineData.OutputValue` = `output rate` x `production mult` / `max operators` when above 1, **Verified** `FUN_14029e460`; [facts](game/ai-tasks.md#perma-jobs)). Blocked while the dummy has no free place or the character has no working arm.
  - 45 Bodyguard / 31 Staying close (`PermaJob.Character`; the "Follow" and "Bodyguard" verbs with Shift, `AddOrderJob` with the target): the character follows the job's character with a follow order
    (`MovementSystem.Follow`); blocked when it is gone, for 31 also while it is down; saved with the target's handle and found again from it after a load. Defending the followed
    character (`RetaliationSystem.Guards`; rows 45 / 31 need state 16, [precedence](game/ai-tasks.md#precedence)): a bodyguard (45, the verb always gives the job; `Guarding` is a running
    row 45, `CharacterCold.Running`) or a follower (31, 289; job or one-shot order) joins while its row runs (it follows) against an attacker within 40 of the target, and a bodyguard
    also against its own attacker within 40 (45's `!39`; farther ones are Self preservation's, which an order outranks). A defence or goal fight started while a follow row runs is a step of the row (`TaskStart.Start` keeps the provenance), so both follow
    again once that fight leaves them idle (`FollowResume`; the fight's end asks the decision step, `MovementSystem.FollowAgain`); any other start in between replaces the row. The jobs run with the switch off (`IgnoresSwitch`). Bodyguard given to an animal adds 289 (animal Staying close) instead of 31 (`PermaJobs.Add`): the same
    follow with the same down test, but it is not exempt, so it runs only with the jobs switch on (the row's destination-accessible-animal state 168 is not modelled).
    Adding a follow job or a one-shot follow removes what would close a circle (`MovementSystem.BreakCircle`: the other side's follow and its 31 / 45 jobs on the chain, [facts](game/animals.md#following)); a job starting does not.
  - 137 / 138 corpse disposal and 223 / 273 intruders (`ProductionSystem.Errands.cs`, `ErrandJobs`; a carry under way is stepped every `ErrandEveryTicks` 15): two corpse jobs, as the
    original's rows ([lift and delivery](game/ai-tasks.md#perma-jobs)): 138 lifts the body its finder gives (`CorpseDisposal.FindCorpse`, as the NPCs'; not on Hold, not while
    carrying) and is over with the lift; 137, while a disposal is found, delivers a carried dead body (list B 99) to the disposal `CorpseDisposal.Find` gives ([Town duties](#town-duties)'
    rules), and with nothing carried lifts first (its plan's 138); in its reach it lets go and the body is removed (items with it). A lift or delivery under way runs on for the job
    that started it. With 223 and 224 (or 273 and 274) it lifts
    a knocked-out, alive character of another faction in the player's building (with doors) it stands in and puts it down just outside the door (273: past the town's farthest building). Engine
    choices: the building test (a footprint round the origin), the friendly exclusion, the reach, 4 walks that end short before the body is given up, and 223's "home" (the building the carrier is in).
  - 234 Turret guard (Shift + quick right click on a turret: 146 becomes 234): mans the turret as an operator; blocked while someone else mans it, and while a melee attacker is within
    40 (146's `!158`). Urgent (step 3, before Self preservation) while the character senses an enemy.
  - 87 Operating machine: blocked while others hold every operator place or the machine has no work (no power; no queued research or craft; a
    farm growing with water, or with no water it holds or can get, or ripe with no plant left or no room; an empty input the operator cannot fill from its pack, a storage or a machine's output;
    a full output (a bench's first craft at its store) no storage takes). A farmer goes to a growing farm only to fill an empty input; he plays the farm's clip after the fill until his next job check, then goes on to his next job ([farming](game/buildings-production.md#farming)).
  - 124 Hauling to (`CharacterTask.Haul`): the nearest player machine within that 4.246733e7 bound (3D, engine choice: the original's 124 sources nothing) of the storage whose output (a bench's: its finished items) the storage takes is
    emptied into the hauler's pack and carried there; items the hauler already carries that some machine makes or crafts go first. Blocked with neither.
  - 152 Operating automatic machine (Shift + Auto-Haul on an automatic machine; its companion 124 lands on the same machine): the operator above at the machine, hauling only. Its done list is `!62` (machineHasEmptyInputs false, **Verified**, TaskData row 152): the job is finished, and the list moves on to the 124 companion, as soon as no input of the machine is empty (`ProductionSystem.Blocked`).
    Blocked unless it is mid-trip for it, carries an item an input has room for, an empty input's item is in a storage, or a full or carried output has a storage that takes it
    (engine reading of the row's goal "no empty inputs", `AutoWork`). 124 on an automatic machine runs the same way, blocked unless it carries an item an input has room for.
  - 125 Engineer (ordered as 2 Build or 95 Repair; Shift + quick right click on an unfinished site): builds `ConstructionSystem.EngineerSite`'s
    pick, whatever the job's target ([buildings](#buildings)); its tasks are *Build*, *DeliverMaterials* and *FetchMaterials*. Blocked with no site.
  - Implicit jobs (`ImplicitPick`, `ImplicitJobs`, the decision step's step 5 after the list): with no list job to run, the switch on, not sneaking and not a slave (Sleep and Sit also not while holding position), the squad AI options (`PlayerState.SquadAi`, the
    overview's AI tab, on by default) start, in this order: Ditch items (242: carried ITEMs of function 0 or 15, and food but the first, go to the nearest
    storage within the 4.246733e7 bound (3D) of the character that takes them (finder `FUN_1405a59a0`, **Verified**), as a Haul trip; only with no queued orders; re-tested every pass, so a trip whose storage no longer takes a carried item, or that gained a queued order, drops out or re-picks at once, **Verified** `FUN_14050e5b0`), Sleep when injured (153: a character in a player town or a player building whose rested level is at most 0.9 and whose organic
    first aid need is 0 (`WantsBed`, [the original's test](game/ai-tasks.md#perma-jobs); engine choice: a part within half a point of its maximum counts
    as full, as the treated pool can leave a hundredth unhealed, #500) lies in the bed the bed search finds, a `BedSystem` trip (task Sleep, the bed
    id in `OperateSite`), until its rested level passes 0.9; job lists are then looked at even with no machine; 257 gets up a rested player sleeper with no order and no job
    too, such as one loaded asleep: `GetsUpHealed`), Sit when idle (275: the nearest free player chair or
    throne within `SeatRange` 600). The one running is kept while it holds; any list job preempts it, but not a bed trip or sleep while its commitment
    runs (`BedCommitted`: 2 + r x 8 game hours from the job's start, `CharacterCold.BedCommitUntil`, not saved, as the original saves no current goal; over once rested or when the bed is gone; orders,
    urgent jobs and the priority-3 goals still take over, #1034). Distances are from the character, or its
    squad leader while it follows, so it gets up when the leader walks off. Sitters are put on the seat's origin facing its way;
    the medic job counts them as idle.
  - The list is reordered by dragging a row (`MoveJob`).
  - Jobs on characters: the Medic and Rescue buttons (H, J) start one look for a patient or body, with Shift they add 58 / 148 (+ 106); Shift with
    the First Aid, Splint Injuries, attack or Loot verb (context menu or quick order) adds the job the order becomes (`AddOrderJob`: 58, or 57 on a
    robot, 250, 16, 238). Each is the decision step's (switch on unless the type ignores it), the first in list order that finds a target:
    - 58 Medic / 57 Robotics (`FirstAidSystem.FindPatient`) and 250 Splint Rigging (`FindSplint`): the original's finders
      ([medic jobs](game/ai-tasks.md#medic-jobs)) over characters within sight (stand-in for the sensed list): score = need (untreated HP for
      the kits it has) × relation factor × nearness (1 - d²/1843²); the medic itself scores its need. 58 skips robot races. Splinting takes
      only its own faction, at skill min(medic, kit quality), and needs a splint kit in the medic's own inventory or backpack (as the original, [kit search](game/character-stats.md#first-aid-update)); its weight is 0.1 ([splints](game/character-stats.md#splints)). Engine: no same-building floor, no give-up list.
    - 148 rescue (`CarrySystem.FindRescue`; a player character its own faction, with Rescue Allies also allied factions; an NPC rescuer allied factions always; never animals, bed or cage occupants; `CarrySystem.Rescues`). An NPC with a loaded rescue job keeps the carry pass.
    - 238 Foraging animals (`CarrySystem.FindForage`): the nearest downed, uncarried animal within sight holding items, not of its own faction
      (engine choice), is looted (take-all and butchering).
    - 16 Attacking enemies: `AggroSystem` looks for the character as for an NPC (`HuntsEnemies`: the job and row 16's desire, not Passive, not sneaking outside a fight, standing) and
      offers the sighting to the step (`AttackJob`); never gives up a chase; after the fight it stands idle and is decided again.
  - Engine choices: the re-ask period; the ranges, a bed's clip (`sleeponfloor`) and a seat's facing (its own, not the table it faces).
  - Saves: GAMESTATE_AI `pjob<i>` / `pjobT<i>` / `jobs` as the original, plus `meitou pjob<i>` (the site id) for a building the handle cannot
    name; the older `meitou jobs enabled` is read, not written; `chase mode` is written and read as the switch. The squad AI options are the
    CAMERA bools ([squad AI options](game/ai-tasks.md#squad-ai-options)). A job's trip is not saved.
- **Squad AI options** (`PlayerState.SquadAi`, the overview's AI tab, defaults as the owner's saves): Ditch, Sleep, Sit above; Heal Allies
  (the medic jobs take allied factions' patients scoring ≥ 0.1), Help Allies (`RetaliationSystem.JoinFight`: the fighters of allied factions count in
  goal 15's finder, their weight squared, [precedence](game/ai-tasks.md#precedence)), Rescue Allies (above), Share Food (`FeedSystem`: a player character with no food takes it from a squad mate's backpack within 200,
  only with the option), Feed Animals (off: the player's animals do not eat), Shoot first (`SquadAiRules.ShootsFirstAt`: with no enemy in its
  sights a player turret gunner also takes dangerous neutrals at weight 0.6); Meitou-only Heal Neutrals and Heal All (player medics also treat downed non-hostile
  strangers, or any downed character; [squad AI options](game/ai-tasks.md#squad-ai-options)).
- **Saves** (`Saving/MachineSave`, `ResearchSave`). Each building's production and farm keys go into its
  GAMESTATE_BUILDING. Items go into the INVENTORY_STATE the building lists first (a new one `<site>-inventory`), in the original's
  [sections](formats/save.md#folder-and-state-mapping) `in<k + 1>`, `out` and `backpack_content` (a furnace's grid `in1`); older Meitou
  inventories (`in0`, `main`) migrate on load; one whose items are as loaded stays as saved ([machine inventories](formats/save.md#folder-and-state-mapping)).
  - A loaded farm's plants all take its growth (**not modelled**: the game adds 0.2 x U[0, 1) to each, [save](game/buildings-production.md#farming)).
- **UI** (Faithful, `ProductionWindows`).
  - Selecting a machine opens its window at the screen's left edge, vertically centred, an open inventory moved right of it (**Observed**, original screenshot 2026-10-26, mined iron rock): `Kenshi_GenericBuildingWindow`, or for a bench
    `Kenshi_InventoryResearchWindow`. Items are held in and out with the mouse (an input takes its record up to its capacity; `out` takes nothing).
  - The site menu gains Operate (also the quick right click); Shift + quick right click adds a job instead ([job lists](#production)).
  - The HUD shows the farm's crop yield and growth rate, workers, and crop bars (growth, condition, remaining plants and harvest deadline;
    [farm panel](game/ui-screens.md#selected-building)); the separate machine window shows its input/output stock.
  - A crafting bench's window shows the current craft and QUEUE... (opens the Craft tab); finished items are lifted out of `out`.
  - The overview's Craft tab lists stations, their crafts (click to queue; red: locked), the queue (click to cancel),
    Repeat Queue and the hovered craft's time and inputs.
  - The overview's research tab lists categories, blueprints (click to queue), the queue (click to cancel) and a
    description with the cost.
- **Engine gaps:**
  - A quick right click on the player's furnace walks the selection there and opens its window
    (the original's 26 LOOT_TARGET); on the player's turret the nearest able selected character operates it (146 USE_TURRET, `UseTurretOrder`): it
    walks to its gunner spot (as for town turrets below) and mans it from within the turret's `use range` of its position (doubled once stopped, at the
    spot's height; the original's arrival rule, [work distance](game/buildings-production.md), no settle slack), facing it holding `combatstance`, counts as its operator (so it draws power) and the
    combat system fires it ([Ranged](#ranged)); one gunner, engine choice; someone else at it: "is already in use".

### Prospecting

Facts: [Prospecting](game/ui-screens.md#prospecting), [Task_Prospecting](game/ai-tasks.md#prospecting). `ProductionSystem` (`ProductionSystem.Prospecting.cs`, `Production/Prospecting.cs`) owns it because it holds the `ResourceField`.
- `ProspectOrder` names one player character (the host picks the selected one nearest the camera pivot). If it is awake, not carried, carrying, caged or swimming (water state 3) it stops (a fight ends, [task starts](#task-starts)) and takes
  `CharacterTask.Prospect` with `CharacterCold.Prospecting` = 0 (hashed only while set, not saved). A character already prospecting is left alone; when done it is `Idle`.
- Each tick (`SlowWorld`, before the early returns) the progress grows by the tick's game seconds. At `ProspectSeconds` = 1 (**Verified**) the task ends (`Idle`) and a `ProspectReport` is published as `World.Prospected`: the
  prospector's name and science (raw stat), the spot (its position), the zone name and the crop coverages, and the five resource values from `ResourceField.Yield` at the spot. Another task (a move, an attack, a stop) or the character going down
  ends it with no report. The report is display only: nothing reads it, the state hash does not see it, a save does not hold it.
- Map: the report carries `ProspectMap`, sampled once at the end (terrain height `IWalkability.TerrainHeight`, the field resources by `ResourceField.FieldYield` per cell with the spot's record, the rocks in range by `NodeYield`); the window colours it per checked row. The report also carries the wind of the spot's region at that moment (`World.Weather.At`, 0 without weather). The window's camera marker is drawn each frame at the camera's
  target (the strategy camera's pivot) and turned by the level direction from the eye to it.
- Animation: the `medic` clip (`AnimationSystem.HeldClip`). HUD goal text "Prospecting". **Not modelled:** the progress bar over the character, the state field `+0x198`.
- Engine choice: the nearest-to-pivot rule uses the selection (the original's iterates the player interface's list, **Observed**).

## Trade

`TradeSystem`: trader-squad stock/purse/restock, open trade, buying/selling/fencing. Charge,
quality, culture, market, forbidden/stolen factors follow [economy.md](game/economy.md).
A squad shop holds one 18 x 18 stock grid (the building storage size and the vanilla trader window width); its trade window is a copy of it without the money items (`Shop.Window`, `ShopView`), which `ArrangeStock` arranges. A housed trader whose template makes no
prisoners or slaves and whose home has stock containers gets a building shop instead ([building shops](game/economy.md#building-shops-observed-decompile)): its goods lie in
those containers and its trade window is a copy of them written back as it changes (`Shop.ForBuilding`, `ShopView`), its own purse rule, the containers' locks opened at each fill. Stock cells stack 50 x an item's `stackable`. A housed squad with vendors that is no
trader has its home's containers filled instead (`FillHome`). A squad without a shop that a Trade line opens a trade with gets an empty one, never filled, or when housed its
home's stock containers for the trade (`StockFor`, [who trades](game/economy.md#who-trades)). A trader on a walk (a caravan's boss) stops for the customer's walk-up and its AI goes on once the trade opens (engine choice; the original holds no trader for a trade). Only members that trade
(`DialogueSystem.Trades`, [who trades](game/economy.md#who-trades)) publish `IsTrader`, the Trade verb and pointer.
Shop state is hashed; the open trade, the trader windows and the vendor list tables (`VendorTables`: per world and unsaved, as the original resets its list cache with every load, [economy](game/economy.md#building-shops-observed-decompile)) are not, except the buyback receipts (below). A shop is saved on its platoon (`Saving/ShopSave`: a squad shop's stock, fill count,
`inventory refresh time`, [economy](game/economy.md#building-shops-observed-decompile)) and kept by a saved platoon that leaves the world; a save without it (the original's) keeps a building shop's goods until that time and gives a squad shop a fresh fill ([economy](game/economy.md#building-shops-observed-decompile)).
Customer: the original buys into the character window the stock is dropped on (`FUN_140712490`, **Observed**). Meitou keeps one customer; the inventory window shows the lead
selected character, also while trading. A buy or sell names the window's character (`BuyItem.Into`, `SellItem.From`, a held item's window) and the trade passes to them within
`PassRange` 1000 of the trader (`TradeWith`; the original's unflagged item limit, [trading range](game/economy.md#trading-range)); farther nothing moves and "Out of trading range."
is posted. With no window named the trade follows the selection (`FollowSelection`). The view prices the goods of every player character within that reach for its seller (`TradeView.GoodsOf`),
so any of their windows shows sell prices and the fencing prompt and asks before a risky sale. An open trade stays open at any distance, through fights and knock-outs, as the original's, closing with every inventory window (`World.CloseInventories`: caught stealing or fencing, smuggling, a town alarm; [closing a trade](game/economy.md#trading-range)); opening needs the talk range (`DialogueSystem.InTalkRange`, the customer stops there, [opening a trade](game/economy.md#trading-range)), reached by the Talk's walk (`WalkUp`: follows the trader, dropped by another order). The context menu's Trade sends the selected standing character nearest the trader.
Transfers and money changes must succeed together. Stealing from containers: [crimes](#crimes-and-bounties). Smuggling: the refusal and event 8 (`TradeSystem.Smuggling`) and the held item's prompt ([economy](game/economy.md#fencing-stealing-and-smuggling)); not built: a smuggler's own answer (read by no caller seen).

Bags (engine choice): a bag trades whole, the same instance with its contents grid, and only empty ("That bag
isn't empty."), since its contents would otherwise go unpriced and unchecked for theft; a bag's contents are not shown as stock. A right click puts a bought bag into a free
backpack slot, else the main grid; a drag puts it where it is dropped, the backpack slot included, never into another bag. Other items: a right click tries `TryEquip`, then the grids.
A live animal's shut main grid takes no buy, only its bag slot and worn bag do ([animals](game/animals.md#pack-animals)).

Buyback (the original's: [economy](game/economy.md#buyback-during-an-open-trade)): a purchase or sale leaves a receipt on the moved units (`TradeReceipt`, the coins paid per
unit, kept through stack splits); moving them back in the same open trade costs or refunds exactly those coins and clears it. Units of the same trade and direction share a stack
(their coins join); they top up plain stacks only when no free cell is left, losing the receipt for those units. Closing or replacing the trade voids every receipt, moved items'
too; stacks it kept apart stay apart. Receipts are in the state hash but not saved, so a save made mid-trade reloads without them, with a different hash.

### Shopkeepers of the player's faction (Meitou only)

The original's squad factory fills a shop only for a faction that is not the player's (a faction test, `+0x250 == 0`, gates both fills,
[building shops](game/economy.md#building-shops-observed-decompile), **Observed**). Meitou gives one when a code mod asks (`SetSquadShop`; the
contract a mod relies on, the money rule included: [modding](modding.md#registration-surface)). Engine rules:

- The shop is always a squad shop (`Shop.Stock`), never a building shop, with the terms of the named template or of none (`TermsOf`); a player
  squad has no town (`Squad.Town` -1), so prices have no culture price or market factor.
- It is open while the squad is independent and keeps its own purse (`TradeSystem.FactionShopOpen`). A member publishes `IsTrader` while it is
  alive, uncaged, awake and not fighting (`TradeSystem.KeepsFactionShop`; a sleeper or fighter would refuse the trade, `CanOpen`): the
  own-character menu then offers the paid Trade (`OpenTrade`) before its other rows, and the quick right click on a standing one trades, as with a
  trader with no line to say.
- `Parties` lets a trader of the player's faction trade only while its squad's shop is open and the customer is not of its squad; `TradeWith`
  and `Sellers` leave the trader's squad out, so the shop never buys its own keeper's goods.
- A sale is never caught as fencing and nothing is illegal there (`FenceOdds` 1, `IsIllegal` false): the original's fence chance is 1 for a
  trader of the player's faction and its smuggling check reads only a trader of another faction
  ([fencing](game/economy.md#fencing-stealing-and-smuggling), both **Observed**).
- Fills and restocks (`StockFactionShop`, `Refill`): a template that is a trader's with vendor lists fills the stock when the shop is made and
  restocks it on the shop pass once its time is due and a member lives; `RefillSquad` leaves the purse alone and takes the fill's money stacks
  out of the stock.
- Ending (`SetSquadShop` off, `TradeSystem.DropUnownedShops` after a load without the owner mod, or `RemoveSquadCommand` on an emptied squad the
  player leads again): an item the player holds from the shop's window goes back into it, an open trade closes once the window is written back,
  and every stack goes into the living members' inventories in order (a live animal's shut grid takes none, `InventoryCommands.MainClosed`);
  what fits nowhere lies on the ground ahead of the first living member, or with none where the squad was made (`Squad.Position`), with a
  message saying how many stacks. A squad whose keeper died stays with its stock stored until the mod or one of those ends it.
- Saved on the player platoon ([save](formats/save.md#player-platoons-meitou-mapping); only for a squad of the player's faction), hashed with the
  squad (the shop, its owner and template).

## Dialogue

`DialogueSystem` (game facts: [dialogue](game/dialogue.md)). The context menu's Talk To (only when a dry run of the line pick, dice and memo put back, finds something to say) sends `TalkOrder`: the
talker walks up (60 units, engine choice; 60 s to arrive), the NPC going on with what it does, then it is turned once to face the talker, event 1 fires and the NPC's dialogue set (record, squad
template, leader role; `DialogueDatabase`) is chosen from by score, line checks and a seeded pick. The NPC line is said (text, tokens and
word swaps, effects at once); passing replies become choices, an NPC-side child a single `[...]`. The host opens `Kenshi_ConversationPanel`
modal, which pauses the game; `DialogueChoice` and `EndConversation` apply between ticks at speed 0; effects that order fights are deferred
to the next tick. A free hostile target is attacked instead; a caged one, hostile or not, is talked to and kept in its cage (#737).

- **Effects built**: locks, interrupts, `change relations`, trade, relations, the three attacks, take and give money, faction rank (DA 35, read by DC 10), the surgeon's editor
  (paid by the line, `EditCharacter.Paid`), assign and clear bounty, lock this dialogue, DA 37 / 38 (a campaign's phase), `trigger campaign` (`PopulationSystem.TriggerFromDialogue`: the rules in
  [campaigns](game/factions-squads-towns.md#campaigns); `world state` conditions read the live states), recruiting (DA 3 / 18, `DialogueSystem.Recruit`), crowd
  triggers, `give item`, `change AI` and `AI contract` (`Ai.ChangeAi`, `Ai.StartContract` with the target: `Blackboard.MissionTarget`, followed by goals aimed at the
  squad mission through `AiMind.FollowTarget`; on its end the leader's `contract end talk passive`), DA 25 / 53 (`FixtureRegistry.TagEscaped` / `FreeSlave`) and DA 29
  (`ArrestSystem.ArrestFromDialogue`), DA 43 (the bail list, [Arrest and jail](#arrest-and-jail)), DA 13, 14, 19-21, 23, 28, 54 (`DialogueSystem.Acts`); the rest is listed in [dialogue](game/dialogue.md#meitou-design).
- **Orders** (`DialogueSystem.Acts.Order`, `PlayerOrders.Give`, one-shot, the receiver's queue cleared unless noted; DA 6 / 7 / 34 / 55 / 57 stop the
  speaker first, `BodySystem.Stop`; rows and receivers in
  [dialogue](game/dialogue.md#effects)): DA 2 gives the speaker squad's leader 102, DA 16 / 17 the speaker 164 / 165 and DA 34 / 7 280 / 19 (`Dialogue.DialogueOrders`,
  `Ai.EngineOrders`); DA 6 / 57 101 and DA 55 248, none to a player-faction speaker holding an order (`PlayerOrders.Holds`); DA 33 67 to the target when the speaker's
  task is idle, DA 52 67 to the speaker for each other member, DA 56 29 to the target's position queued behind each member's orders. 102 and 165 act out as
  `CharacterTask.Talk` (`ApproachQueue`, `TownStalkers`: one task through the walk and the stand), 164 as the follow (`TalkFollowers`); a record goes once its order no longer runs, a
  talk act-out still on the character stood idle and the talk's follow target cleared (unless a later follow of the same character runs); 101 / 248 fight the member
  `AiSystem.AssaultTargetFor` picks (`CombatSystem.EngageOrder`) and repeat for the next until none is left. A building purchase gives each resident of the squads
  moving out 19 to the new home, or 22 without one (`AiSystem.Rehome`; the leader walks the patrol step's points, the others follow it).
  A conversation's or bubble's start turns its NPC or speaker once to face the other and stops no one.
- **Events from other systems**: `DialogueEvent` (deferred to the next tick) fires an event at an NPC as bubbles, or a given DIALOGUE (the contract end talk);
  first aid, shackles and the release rounds send them. The police's spotting fires 44 at once (`FireEvent`, which says whether a line was said).
- **Sight pass** (`Perception.SightSystem`, its own system after the dialogue, run while its event lines are on; [sight](game/dialogue.md#ambient-conversations)):
  one sensed character per update turn gets the sight events (3, 27, 34, 59, 66, 67) and the crime events (`SightCrimes`: 45, 36 / 40, 17, 9) in the
  original's order; the crime events cut into a running conversation (`DialogueSystem.CutIn`). Every event is refused by the starter test
  (`ConversationStarters`, [firing an event](game/dialogue.md#firing-an-event)). Crime and arrest test worlds have no dialogue, so no sight pass: their
  engine fallbacks stay (the escapee's arrest, slave catching's tag and 36, the witnesses' trespass).
- **Recruiting** ([facts](game/dialogue.md#recruiting)): at the `max faction size` cap the line "It looks like your squad is a little full." is
  posted and the conversation ends; otherwise the speaker leaves its squad (its leader passes to the first member left) and joins the player's
  active squad (the first selected player character's, else `Player.Squad`, else the talker's), or a new "blank squad" squad when that one has
  `max squad size` members: player faction, role Squad1, NPC class Adventurer (as every player-faction character, `CharacterAssembly`), player
  default orders, speed mode Free (2), no AI mind, stopped, its save link dropped (a save writes it as a new member of the player's platoon).
  Its dialogue becomes the player set (`DialogueDatabase.SetFor`). DA 3 ends the conversation and opens the editor in recruit mode
  (`EditKind.Recruit`: free, race and gender kept). Every piece a recruit carries loses its `uniform` faction (`ItemInstance.Uniform`, as the original: [recruiting](game/dialogue.md#recruiting)).
- **Conditions**: every DC is computed (slavery: 59, 61, 62, 64; faction trust 3; tech level 12; town levels 73, 74; strength 21 / 22
  `CombatSystem.Rating`; outnumbered 39 `BodySystem.Senses`; walls 49, 57, 58; training dummy 70); 6, 15, 34 and 51 always pass, as the original's; DC 69 HAS_ILLEGAL_ITEM (the gate guards' inspections) checks the culture's `illegal goods` ([dialogue](game/dialogue.md#conditions)), posting each find (not on the Talk menu's dry run, once per carrier and item per game hour: engine choice). IN_MY_BUILDING uses the squad's home building and the lock rounds' extent; IS_INDOORS the sight map's building.
- **Squad memory** (`SquadMemory`, `DialogueSystem.Memory`; [facts](game/dialogue.md#squad-memory)): per squad and character a time and long / short tags, the player faction
  sharing one long-term entry; DC 18, 20, 23 and 52-55 and the aggro scan (TEMP ENEMY / ALLY) read it, the effects and event-1 ends write it; it stays with a squad's platoon while the squad is out of the world and is saved on the platoon (`MemorySave`) ([engine choices](game/dialogue.md#meitou-design)).
- **Speech bubbles**: `WorldSnapshot.Speech` lists the lines on show (`SpeechBubble`: speaker, text, large font); `PlayerInterface.AddSpeech` draws them over the head. Sources and engine choices: [dialogue](game/dialogue.md#meitou-design).
- **Personality**: the save's `personality` when above 0, else rolled as the original does from the character's (else its faction's) PERSONALITY record, with Meitou's own random draw (`Personality`, [facts](game/dialogue.md#enums)).
- **Engine choices**: a childless NPC line shows `[Leave]`; a reply leading to another reply offers it alone; number keys pick choices. A choice whose picked text is empty and that has no cost is not offered (**Verified**, `FUN_140680130` list loop); with none left the original ends the conversation, Meitou keeps `[Leave]` for the first offer.
- **State**: last use per line, permanent chance rolls, event counters, squad memory, interrupts and the open conversation are hashed, not
  saved (as the original, except the squad memory, which it saves on the platoon; the faction global trust and war flags it saves under `meitou` keys, [save](formats/save.md#faction-trust-war-flags-bed-rent-and-turrets)). Locks are on `CharacterCold.DialogueLocks`, hashed while set, saved as `lok*` (`DialogueSave`).

## AI

`AiSystem`, blackboards/minds, package/task data: slot/class/time/weight selection, homes/beds/nodes,
patrol/bar/shop/sleep/retreat/contracts, combat stances and off-screen roaming jobs. Data and
TaskData semantics: [ai.md](game/ai.md), [ai-tasks.md](game/ai-tasks.md). Wars and prisoners: [campaigns](#off-screen-tier),
[hostility](#hostility-and-relations), [kidnapping](#kidnapping), [captives](#captives) and [arrest and jail](#arrest-and-jail), each with its gaps; residents
off screen only come back where their schedule puts them ([off-screen tier](#off-screen-tier)). Hash blackboards/minds/jobs when present.

### Package classes

`PackageClasses.Of` maps the `signal func` ([ai.md](game/ai.md#package-classes)); `Blackboard` runs the original's slot walk with the class rules of `AiSystem`
(`IPackageRules`: start, finished, started, ended, may lead). Built: plain, retreat, patrol, bar, contracts, and
(`AiSystem.Packages.cs`) the wandering trader (16), passing-by assault (29), occupation (30) and refugee (22: a slave-led squad of fewer than 3 flees to the nearest other town of its faction, a broken one joins a healthy squad there, `SquadManagement.Merge`) as decoded, and the campaign classes
(war 19, 20, 32 and the defence 31; only a campaign gives them in the data) with their finished tests ([ai](game/ai.md#package-classes)); they start only as a
campaign's override (engine choice: the original's can-start is always true, so a template slot listing one would start it; none does in the data). The war
classes finish once their platoon is out of its campaign, and the war assault (20, 32) marks every member "ordered" on the package cadence whoever moves the squad, and the first step after it stops running clears the marks
(`AiSystem.StepWar`, so a blow at one books no Assault, [economy](game/economy.md)); the defence's start sets its mission and target town (the player town nearest
the leader), and the war classes' start (`AiSystem.WarStart`) the campaign's target as mission town, the lead platoon as a follower's mission target
(`Blackboard.WarLead`) and the off-screen jobs 5 / 6 (`PlatoonJobs.WarJob`); a war assault's squad roams its target town (engine choice).
- **Current town** (`Blackboard.InTown`, on the package cadence before the choice): nearest town by X/Z, placed towns by `TownData.Radius`, player towns by their
  own (`PlayerTown.Radius`); entering one fires the leader's ENTER_TOWN; the town a squad starts in counts as entered (engine choice: no line for every squad a town makes).
- **Trader**: as the original (stay, chooser, states, leave rule); the leader's WANDERING_TRADER goal walks to the town by its plan's 53 (`TownTravel`: the
  squad's town, `SquadTown`, at the original's speed rule, with its detour and exits, docs/game/ai-tasks.md "Town goals"), members follow. Engine choices: a town's own squad never
  starts it (Meitou unloads a town's squads with it); a bar package of a squad that is not a town's own takes the bar of the placed town it is in, none in a player
  town. **Observed** (headless, Hub area, 150 000 ticks): a slaver squad reaches the Hub, waits 7.5 h until 06:00 and leaves for Skinne.
- **Assault and occupation**: the squad fights the town's faction (`Squad.War`) and roams the town (its wander centre; RUN_AROUND_TOWN_LOOKING_FOR_PEOPLE acts as
  patrolling); engine choice: the members' posts move into the town, so idle, node and bed goals stay there. The faction clocks (`AiSystem`, hashed once set) are
  not saved, as the original's. An occupier is persistent, as the original's: kept out of the world by every switch (site unload, sector move past its count, a save's
  platoon), saved `persistent`, and when it comes back in the player town it goes on occupying (`Blackboard.Resume`: mission town and enemy again, no line; the town is the one it is made in;
  its members' minds come after, so each takes its post round the town as its mind is made, not where it was saved);
  **Observed** (headless, an owner save with starving bandits occupying an outpost, Faithful switches): they occupy again on load, unload when the players leave
  and come back occupying;
  a save's wild platoon is AI-run like the wild sites' own squads. The occupation's intruder, cage and food goals: [Town duties](#town-duties); its bed goal (153, 1 to 9 h; not the leader's) takes a bed of the occupied town ([beds](game/locks.md#beds)); a member whose bed is taken rests beside it, one in a town without beds at its post. Campaigns read only the loaded occupiers (`AiSystem.Occupied`; they need a player within 1250 of the
  town, so it is loaded). **Observed** (headless, Stack area, a starving-bandit mob put into an outpost with 3 player characters): the assault starts, the
  occupation follows 0.1 h after the players are knocked out. No fight started in that run (the aggro's sight of sandbox characters there was not looked into).

### Autonomous player squads

Meitou only (the original's AI plans for no player squad; asked for as the RE_Kenshi "Squad Autonomy" plugin's base). `Squad.Autonomous`
(`SetSquadAutonomy`) opens a player's squad to `ChangeAi` / `StartContract` (dialogue's `change AI` on it too); `SquadAutonomy` holds the rules.
- **Minds**: only its `IsPlayer` members get one (`AiMind.Player`), of the package's role goals alone: Self preservation and race goals stay the
  [decision step](#decision-step)'s. A member moved to another squad loses its mind at the next package step. Off: board, saved AI and minds go,
  an AI-started task ends (one in a bed lies on).
- **Precedence** (the chooser's order, [precedence](game/ai-tasks.md#precedence)): orders, urgent package goals, jobs, the other package goals.
  `AiSystem.Holds` takes a player character only when no plan runs its task or the plan is 14 Idleness's or the think's own (`AiMind.Owns`: the
  tick and row of its last start; its own fight), so an order's, job's or reaction's task is never touched. On a job's task the think weighs the
  urgent tier only (`Restrict.Urgent`); a goal that plans sets `AiMind.Urgent` and the step-4 source `SquadAutonomy.PackageGoals` frees the
  character (idle; the next think acts the goal out) and counts as running, so step 5 does not take it back. A task for a goal that is not urgent
  yields to the jobs at the engine re-ask (`DecisionSystem.GoalsComeFirst`). **Engine choices**: the source's weight 0.5 and desire 1 (Self
  preservation wins); implicit jobs take only a standing character, as before.
- **Home** (`SetSquadHome`, `Squad.AssignedHome`): a town building (its town becomes `Blackboard.Town`, so town goals, turrets and workplaces are
  that town's) or a player-built building, stood in for by a `TownBuilding` with `RuntimeKey` = the site id and no spots. A player-built home that
  is not complete counts as none; one removed is forgotten. A purchase does not evict an autonomous squad. Beds: the home's layout beds, else the
  complete beds the player placed in it (`BuildSite.Parent`, one per member, `SquadAutonomy.HomeBed`), else the player bed search. A player-built
  home has no indoor volume: "inside" is within the home radius.
- **Outpost as town**: a complete player-built home in an outpost the squad's faction owns makes that outpost its town (`Blackboard.Outpost`, the
  player town's number, beside `Town` = -1; `Blackboard.HomeTown` is either kind; derived from the home on each settle, not hashed or saved).
  "At home town" is within its radius (`PlayerTown.Radius`) and going home with the home unreachable walks to its centre; a patrol package
  starts there after 8 hours in it, as in any town ([package classes](game/ai.md#package-classes)).
  The town goals' finders ask by `Blackboard.HomeTown` (`TownPlaceIndex.Of(CampaignTown)`); an outpost's
  places exist only while some squad has it as its town, and take only the outpost owner's buildings. **Engine choices** throughout:
  - **Locks**: on a walk the AI started for it (`AiMind.Owns`), a member keeps to the player's locks (`SquadAutonomy.KeepsPlayerLocks`): it has
    no way through a locked door or gate of a building the player owns (`LockSystem.WayThrough` gives none, so the planner finds the
    destination shut and a walk that meets one stops there, without "It's locked."), and nothing the AI does unlocks it. A closed, unlocked
    door it passes is opened as for any walker and left open. A player's order or job to the member goes through as for the player's
    characters.
  - **Wandering** (37): draws among the outpost's complete, standing, public player-built buildings with an interior that stand on their own
    (no wall or gate) and whose doors are not all locked, in the walker's zone, and walks to the drawn building's origin on any floor, ending
    within reach of it (a player-built building has no general nodes; an outpost's shop is walked to the same way). A walker whose building the
    player locks on its way draws again, so it does not walk on to the locked door.
  - **Gate guard** (180): with no gate home, the outpost's complete player-built wall gate (`is wall gate`) nearest the squad
    (`TownPlaceIndex.OutpostGates`); the guard stands 15 from its first door leaf on the outpost's side, facing out (`GatePost.Inside`), so it
    never passes through the player's gate, where a placed town's guard stands outside. The lock rounds never open or lock a building the
    player owns. A player-built home is never the squad's gate home (its stand-in is no gate), so a squad living in the player's gate guards
    the nearest gate this way and its other members do not flank it.
  - **Sitting** (275) and **reigning** (255), while the member stands within the outpost's radius (an outpost inside a placed town's radius
    offers none: the placed town is taken first): the complete chairs and thrones the player built there in a public building whose doors are
    not all locked, or standing on their own (`OutpostSeats`; owner the building each stands in, else its own; a chair facing its table as the
    player's chair does), never training dummies (the members are on the player's side), and not one a character operates by an order or
    job. The town and owner gates are a placed town's (player side: a town of the player's faction). The player's characters come first: the
    player's Use order and Sit job do not count a member's sitting as using the chair (`SquadAutonomy.YieldsSeat`), and a member whose piece a
    character takes that way gets up at once.
- **Outpost work**: the outpost's complete, standing buildings of the workers' functions (MINE, RESEARCH, REFINERY, CRAFTING) are its workplaces for
  goals 189 / 190, stand-ins known by their site id (`TownPlaceIndex.OutpostWorkplaces`), but a research bench while no research is current (the
  finder's `couldIOperate`, slot `0x500`, refuses it through `dontNeedWorkRightNow`, `FUN_1402995e0`, **Verified**); the player-flag gate lets the
  faction's members work there (`WorkAllowed`). A worker's research adds to the tick's bench contributions before the player's operators'
  (`ProductionSystem._townResearch`), and a queued craft is worked as by the player's smith. 189 takes and operates only a powered machine (state 93). A worker runs the player's own machine (`ProductionSystem.TownWorkOf` gives
  it for a stand-in; no `townwork:` machine is made) and empties and stores its output as a town worker does ([NPC town
  machines](game/buildings-production.md#npc-town-machines)). Operator
  slots are shared: the player's job operators fill the AI's (`ReserveWork`), the AI's holders count against the jobs' (`OperatorsOf`), and a
  member on a player job holds no AI slot.
- **Independent** (`SetSquadAutonomy(id, true, Independent: true)`, `Squad.Independent`; cleared by `SetSquadAutonomy(id, true)` and with
  autonomy; refused for the last squad the player leads): the player does not lead it. Its members stay the player's faction (counted by
  `max faction size`) but cannot be selected (`SelectCommand` skips them, the selection's upkeep drops them), so they take no orders from the
  interface, and a click on one inspects it as another faction's (the snapshot's `IndependentSquads`); its right-click menu keeps the own
  characters' Bodyguard, Follow and medic rows but offers Pick Up only while it is down and no inventory row (nor the free Trade, 26; a member of
  a squad keeping a [faction shop](#shopkeepers-of-the-players-faction-meitou-only) offers the paid Trade). Only the menu holds the
  inventory back: it is an own character's (`CarrySystem.CanBeOpened`), so an `OpenLootOrder` opens it beside the selection's and items move
  and are worn as between own characters, and a held item dropped on a member in the world is given to it (`HeldItems.Give`); it has
  no squad tab, is not on the squads screen or Tab, the squad commands (move, rename, reorder, remove) refuse it, and it does not count against `max squads`
  (`SquadManagement.LedSquads`). Saved as `meitou independent`.
- **Owner** (`SetSquadAutonomy(..., Owner:)`, `Squad.AutonomyOwner`, saved as `meitou autonomy owner`): the manifest id of the last code mod that
  switched it on naming itself (a command naming none keeps it). After a load, before the first tick, a squad whose owner is not running (not
  installed, refused or failed to start) is switched off and loses its home, with a notice (`SquadAutonomy.DropUnowned`, from
  `GameMods.DropUnownedAutonomy`); one with no owner stays as saved.
- **Engine choices**: the sleeping desire and state 72 (fully rested) read an autonomous member as an NPC (with the player stand-in its sleep
  goals would never plan).
- **Not built**: the outpost's turrets as goal targets (the player's turret job, `AddJob`, mans them with the player turret's power and
  research; the AI's town turret post applies neither), its guard posts (157), bars and relaxing (a player-built building has no nodes, bar
  designation or tables); a work building; the contract's mission target over a save (as NPC platoons).

### Planner

`TaskPlanner` runs the original's search ([ai-tasks](game/ai-tasks.md#planner)) over `TaskDataTable` (all 253 rows) for the
goals the engine acts out. The think orders each tier's goals by score, leaves out one whose list A holds, and takes the first
whose plan succeeds (`AiSystem.Choose`); `AiMind.Plan` holds it leaf first (hashed) and `AiMind.Step` its first step. A goal planned by
steps (`PlanContext.Stepped`: one whose row acts or has an effect, not acted out by an engine system) keeps its plan as the original does
([precedence](game/ai-tasks.md#precedence)): each think first runs the plan step (`PlanSteps.Run`: the running step ends by its class's finished
test and its list A, a walk once the next step can run, else by the goal's hours or its own list B; the next starts, a row that does not act
skipped while its list A holds; with none left the goal goes), and the current goal with a step left is kept without a new plan
(`AiSystem.Kept`). Engine choices: the original's per-frame test runs at the think, and a walk's end (followed or given up,
`MovementSystem.EndWalk`) asks the think at the next tick; a walk or stand step is planned again at each think to where the act-out walks
(`AiSystem.FollowsWalk`: the spots move with the package cadence's claims); a task an engine reaction or an NPC's engine start runs that is no
step of the plan (`TaskFinished.EngineRow`, `AiSystem.Displaced`) ends the goal, gives it no continuity and is the running task the finished
tests read, as the original's own goal and job for it would be. The goal's
act-out does the walk and the goal's own action; steps of their own are getting out of bed, sheathing and drawing a weapon (one think each,
then dropped from the plan, so a plan a committed goal keeps goes on to its next step instead of repeating it), going home (19, the GoHome walk),
entering its home building (156, the same walk), leaving it (200, to A + (A − B) × 1.1 of the home's first door,
the outer point taken as the side away from the building's origin, until outside), getting near a target (40, a walk to within 10) and the way
through a locked door the walk meets (80 / 78 unlock, 287 / 81 bash, 76 pick, 285 cut; `AiSystem.TakeDoorStep`, worked by the lock system, its list A
on the door, `RowStates` 52 / 54 / 56; [locks](game/locks.md#doors)). A plan
whose first step is neither the goal nor a step the engine carries out (`AiSystem.Carried`, or a row its [engine goal](#engine-goals) registered) fails. Engine choices:
- Each character plans with its `Repertoire.Of` category (4, 0x40 crippled or staying low, 0x10, 1, 0x20 animal; so a crippled NPC has no stand-up row and crawls away under threat). Restricted rows (lockpicking, cutting, opening gates) are open to every character whose faction is not the player's, as in the original ([planner](game/ai-tasks.md#planner); the orders-step flag is not planned here). With the held states below, none is ever needed on the way (their states 90, !54, !114 hold), so this changes no plan today; `IPlanWorld.Squadless` keeps its old name for it. A row without a finder wants its effect for its own target. At most 64 nodes per search.
- A node is made once per row and target (the original allows four copies), so the ancestor-repeat test never fires; a child already
  closed is skipped rather than accepted from the node.
- The kept plan's steps count as queued.
- Finders: the spots, beds, seats, turrets, towns and (rows 113 / 114 / 242) the ditch storage ([ditching](game/ai-tasks.md#ditching)) the act-outs already pick, at the floor point the walk ends on (`PlanContext.Find`), and an [engine goal](#engine-goals)'s rows' finders, desires and unevaluated states, asked of its system; other finders find nothing,
  so such goals do not plan. Rows on the way have their `TaskProfiles` desire, else 1. The exception is DEFEAT_SQUAD 101, whose plan is the goal alone: the finder's
  pick (`AiSystem.AssaultTargetFor`, [ai-tasks](game/ai-tasks.md#combat-goals)) is sent as an `AttackOrder` by the serial step and the combat system fights it.
  SELF_PRESERVATION 32 is the other exception: its plan is `[35, 32]` when `SelfPreservation.Runs` (a crippled NPC, or one with no working arm, under a threat), else `[13, 32]` on its own attacker (row 13 on itself, `AiSystem.SelfDefencePick`, started as the combat goals' fights below), else none;
  35 is acted out by `FleeSystem` on the AI's request ([ai-tasks](game/ai-tasks.md#combat-goals)). State 26 is wired (staying low, playing dead, in a bed; not crippled).
- States (`PlanContext.Holds`): location, distance, bed, sitting, home (within 20 of the home), home town, town, weapon drawn, weapon carried
  (`hasWeapon`), carrying, a first aid kit carried (23), the immediate threat (39, `CombatSystem.ImmediateThreat`), deep water (139), a leg down that could be fixed (103, `MedicalState.LegsMessedUp`), inside the home building (107, `AI::iAmInsideTargetBuilding` for a non-building target) and strangers in a private home (141, so a resident does not
  go to bed with one inside; [stranger states](game/ai-tasks.md#stranger-states)) are evaluated; power (93) is the target machine's (`ProductionSystem.MachinePowered`: a town's machine by its level town's generators;
  true with no machine; a seat target, the claimed piece, by the claim: [town goals](game/ai-tasks.md#town-goals)); moving, getting up, arms, buildings, doors, gates, machine operator slots and readiness are held true (movement and the lock rounds deal with doors, gate guards' gates included; the bashers' `DoorPlan` evaluates them); the destination's accessibility (59, 168: [locks](game/locks.md#doors)) and the package target (131) are evaluated; 123 and 136 are false, as the original's evaluator; machine empty inputs, output full and jammed (62 to 64) are evaluated for a `Targets.Machine` target (a player's
  machine; no finder gives one yet, the town workers' buildings are level data, [Blocked output](game/buildings-production.md#blocked-output)); every other state is false.

Combat goals (`AiBehaviour.Fight`, `AiSystem.Fights.cs`; [combat goals](game/ai-tasks.md#combat-goals)): 16, 21, 144 and 34 attack whom their finder picks (`AggroSystem.FindFoe`:
the looker's entries seen or heard at its last look, and its squad mates' within twice its range R, the shared sightings; 34 `FindTroubleMaker`), 143, 15 and 288 the attacker row 13 finds
(`CombatSystem.BestAttackerOf`, within 150) of the mate or ally with the highest fight threat (`CombatSystem.FightThreat`), and 32 Self preservation, when it does not run, its own attacker
(row 13 on itself, `AiSystem.SelfDefencePick`). They are chosen like every goal; the think writes only its own
mind's request (`AiMind.FightGoal`, `FightTarget`) and stands, and `SendFights` (serial, slot order) defers the fight for the next tick (`AttackOrder`, the goal's provenance; a defence for
the protect goals and 32), logs it with the aggro system's fights (not 32's) and asks the starter's squad mates to think; a later starter of the same pass picks again with the fights started so far counted
as attackers (the original's choosers run one after another). That fight is the AI's own (`AiMind.FightFrom`): `Holds` keeps its fighter, which thinks in it but acts nothing out
(`InFight`): a combat goal or 32's fight keeps the fight, Self preservation's run or a goal 35 asks the flee step, any other goal makes `LeaveFights` (serial) end the fight, and the next tick acts it out.
An engine duty a fight may interrupt (treating, a town errand's walk, a captive job's walk and its meal at a body, #623) is weighed by `Choose` with only the combat goals and 32; any other duty, and a fight the AI did not
start (a dialogue's, an assault's, a duty's), with 32 alone (`Preempts`; in a fight only its run changes anything); the fight's start ends the duty's task through its handler. Replan triggers
(`AiMind.Replan`, engine stand-ins): a sighting of the look (the looker and its mates within 2 R), a new enemy sensed by a fighter in its own fight, an alert (the attacked NPC with its fight
off, the squad mates of the attacker and the attacked), a mate's fight start, the end of its own fight (`CombatSystem.EndFight`) and of its walk (`MovementSystem.EndWalk`); else
the 15-tick cadence. A new enemy is acted on one tick after the look that sensed it (look at T, think at T+1, fight from T+2); so is an attack (alert at T, the attacker a threat at once). Any
other character the AI does not hold (fleeing, carried) starts no fight. The members of a loaded platoon a code mod holds (see "Platoon blackboards" below)
get an engine-run mind (`AiMind.Engine`, `AiSystem.EngineMinds`): the combat goals of their package for their role and of their own lists, and Self preservation, weighed on their routine and
in their own fights; nothing else is acted out ([combat goals](game/ai-tasks.md#combat-goals)). Every other non-player squad has full minds, one whose template lists no AI packages
included: its members weigh 14, 32 and their race's and faction's lists and stand idle otherwise ([squads without packages](game/factions-squads-towns.md#squads-without-packages)). An NPC without a mind (no squad, or between a dropped mind and the next package cadence) only
fights back, by the engine's answer (`RetaliationSystem`, at any distance; engine choice). An NPC whose order runs (`DecisionSystem.Ordered`) is left alone: its think acts
nothing, no goal pre-empts it and its fight requests are dropped; it thinks again as soon as the order no longer runs ([order queue](#order-queue)).

**Platoon blackboards** (**Verified**: every loaded platoon is visited by the faction slow step, whose Platoon slot 27 runs the blackboard `FUN_1402732b0` with the slot walk `FUN_140272570` and the war-manager check; wild, town-own, roaming, campaign and traveller platoons alike). `AiSystem.Runs` admits every non-player squad, so a roaming or campaign platoon's squad has a `Blackboard` (package choice on the package cadence, in-town tracking, saved `currentPackage` / contract / replacement applied through `Attach`); it claims no building of its own (`PlatoonOf`). Who moves the squad: a loaded platoon acts out its package's goals itself, as in the original, so its members get full minds and the population orders no loaded leader (`PopulationRoaming.StepTrip`): a caravan stopped in a public outpost shops at its counters (**Observed** in Meitou: two loaded platoons under `hang out in a bar` bought out a stocked counter), a patrol walks its sectors round its own town, a town's guards out of their town go back to their posts (**Observed** against the original with the harness: a slaver caravan stays together and carries its captives within about 1.5 game hours). One kind is left to the population (`AiSystem.PopulationRuns`), its members on the engine's routine while its package does not travel (`AiSystem.BoardMoves`: a `Travel` goal, or 192 Delivering slaves, the shipping package's leader goal, while a mission town, destination or refugee target is set): a platoon a code mod holds (`HoldPlatoon`), which the mod moves. A campaign's squad has full minds like any other (its phase package, below; a platoon a campaign drafts while loaded gets its blackboard on the next package cadence). Even there a member obeying a master squad (slave state 1, `AiSystem.ObeyedRoles`) gets a full mind and its master's Slave list (as every character has goals in the original), so a squad's captives follow their slaver instead of standing where they were enslaved (#904). The test does not depend on where a squad stands, so a free platoon walking into or out of a town never changes its kind of mind; a mod's hold or release does, and the minds are made again then (a member whose full mind goes stops what it had it doing and follows its leader again, `AiSystem.ToRoutine`; an idle follower of a squad the population moves follows again on the package step, `AiSystem.Rejoins`). A held squad still takes full minds while its own package travels and engine ones once that ends. Off screen every stand-in follows its package's job; at unload the stand-in's target is a travelling package's mission town. Engine choice: the patrol route of a patrol package (`AiSystem.LayRoute`: round the home town, then home; the original's `GoOutOnPatrol` is not decoded), and a guard post for a squad with no home building is the spot its mind was made at. A platoon leaving the loaded box (`ZoneActivation.InLoadedBox`) is released after the grace period, and the blackboard goes with the squad (its saved state to `stored.Ai`).

**Campaign phase package** (**Verified**, `FUN_140272570`, `FUN_140272240`, `FUN_14026a110`): on each visit a loaded campaign platoon whose war-manager record gives a package (pt1, pt2 or retreat; the leader platoon's `... leader`, the others' `... others` falling back to it, `CampaignData.PackageOf`) holds that package as the blackboard's override when it has none or another (`AiSystem.InstallPhasePackage` = `Blackboard.StartContract` with the 24 h contract time, saved as `contractjob` / `jobhr` and the engine key `meitou phase package`). The override ends by the finished test of its class, as the original's walk asks it on every visit (`Blackboard.PhaseContract`): the contract classes and a signal-0 package (built as `Contract_Timed`) at the 24 h, a retreat (1, 25) once its leader is in a town of its own faction, Retreat and refresh (5) once the squad is intact, so for an intact squad it is installed and ended on every visit and the template's packages run. With the campaign removed or the platoon released, nothing installs it again and it stays until its test passes, so a retreat outlives its campaign. The war classes 19 / 20 / 32 end once the platoon is out of its campaign, and the defence 31 after the 24 h while no hostile campaign runs. A contract that dialogue or a code mod starts ends at its hours whatever its class (engine choice; the original asks its class too). A save without the key (the original's) loads an override of a class only campaigns run in the data (1, 25, 19, 20, 32, 31) as a phase package all the same, so a retreat that outlived its campaign there keeps going; any other override without the key loads as a timed contract until its campaign gives it again (a Retreat and refresh or a signal-0 package after its campaign's removal ends at its saved `jobhr`). The campaign sets the squad's war targets (`PopulationCampaigns.StepCampaignLoaded`), so `StepPackage` leaves a campaign squad's alone. The blackboard hash covers the war start's lead, pace and point.

Scoring follows the original (`TaskProfiles`: desire, commitment, crowding per goal type); a desire
the original computes in a function of its own counts as 1. A committed goal holds only within its hours
([goal end](game/ai-tasks.md#precedence)); one lying in its bed with only the default task left lies on with no goal
(engine choice from the observed sleepers). Town goals ([ai-tasks.md](game/ai-tasks.md#town-goals)):
wandering in town walks to a general node of a public town building in the walker's zone
(public: the residents' template's `public day`, else the level state's `is public day`, else public; drawn
among those with a general node, engine choice), patrolling town from one random point round the town's centre to the next
(`AiSystem.Patrol`, as `Task_PatrolTown`; away from the squad's town its plan's 130 walks there first, `TownTravel`), shopping to a node of a
public shop that sells what the character's shopping list wants, else (not in a town of the player's faction) to a random public building, the desire being 1 for a shop and 0.4 for another building (as the original's finders, [ai-tasks](game/ai-tasks.md#town-goals); no faction test), then buys from its stock one unit a second ([NPC purchases](game/economy.md#npc-purchases)), relaxing under a bar package, sitting around (275) and staying home (43; the home is the bar under a bar package) sit on a seat claimed per
member on the package cadence, and the wanderer walks to the nearest town. A bar package (both kinds) reserves
one table per squad as the original (`AiSystem.PickTable`: public bars tried from a random one, first table no
other bar squad holds; the outdoors kind: a free table placed outdoors in the town; a town without bars gives
the normal kind none, as the original; none free: the squad keeps its home and does not relax); its members take the free seats nearest that table (engine choice),
at an outdoor table they stand at it. A home node goal takes a
node of its kind, a guard post a general node next (as the original), then any node but a shopkeeper's
(engine; only the shopkeeper goal falls back onto the counter). Static town guard (157) holds a guard post of its town
instead (`AiMind.TownPost`, `TownPlaceClaims.ClaimTownPosts`, on the package cadence): the one it holds while its goal stays 157,
else one drawn uniformly among the town's guard posts no other 157 member holds (the posts of 38 and 268 are not counted: engine
choice), as the original ([ai-tasks](game/ai-tasks.md#town-goals)); none free, it stands where the home node goals do (engine choice). The post is
not saved: a load draws again. Gates have no nodes in their layouts (the original's have 3 or 4 guard posts at run time): the gate guard
(180, the home gate, else the town's gate nearest the squad) stands 15 out from the gate's first door
leaf, on the side away from the town centre (the side is an engine choice), facing out; a member whose
guard or general node goal finds nothing in its home gate flanks the opening on that side, beyond the
leaf's edges (half width + 6, steps of 7), alternately left and right in two rows 8 apart (engine).
A gate's residents are made at the gate guard's spot (engine choice), not the gate building's origin, which on
Defensive Gates is inside the wall about 110 along it from the opening; headless (no door gate, no leaf) they
are made at the origin and keep that post. Seats are the home's
interior furniture with a CHAIR functionality (`HomeSpotKind.Chair`; never a standing node), free
in that building this pass; relaxing falls back to a bar node, then a general node; staying home (43) draws a free chair, training dummy or work piece (mine, research, refinery, crafting) of the home, kept only while 43 is the member's current goal and drawn anew at each package cadence otherwise (a candidate that is not current has its finder's target cleared, `FUN_14050df10`, **Verified**), and is not wanted without one ([ai-tasks](game/ai-tasks.md#town-goals); a given-up seat is tried again when the goal is renewed). Sitting around (275)
claims on its own (`Blackboard.SitClaims`, `TownPlaceClaims.ClaimSitting`): the nearest free chair, throne (BF_THRONE, `HomeSpotKind.ThroneSeat`, sat on like a chair) or, for a character not on the
player's side, training dummy (BF_TRAINING, `HomeSpotKind.Training`, stood at) in a building of the town it stands in, when that is its squad's home town or owned by the squad's faction and the
building is owned by the character's faction or the squad's; the throne and dummy pieces, and free-standing chairs, are `TownBuilding.Furniture`. It is not wanted
without one (nor outside a town), so a homeless squad does not sit where it stands. A seated character
is put on the seat's origin with no offset, facing the seat's way ([ai-tasks](game/ai-tasks.md) "Sitting"), and plays the functionality's clip ("sitting chair", pillows
"sitting idle"). A town's bars and shops are
the buildings whose residents (the squads living there, else the level data's `residents template`)
have designation BAR or a shop (`TownPlaceIndex.Refresh`). Walks to nodes, beds and seats pass the
spot's height and count as arrived only on that floor, and end within `MovementSystem.SpotArrive` (2) of
it, not `ArriveRadius` (10), without separation over the last 10 (engine choice: the barman stands at his node). Furniture cuts holes in a floor's navmesh and
the ground below shows through them (Observed: the Storm House at the Hub, counter, tables, stools, 22..28
below), so a spot whose floor is not under it is walked to (and residents spawned) at the nearest floor
point within 12 (`IWalkability.FloorNear`), and no step goes down into such a hole (`StandDrop`,
[pathfinding](game/pathfinding.md)). A home with no node makes its squads at its origin; for a building with an
interior (not a gate, whose guards stay on the ground under its walkway) they go on its lowest floor more than 12 above the terrain within 40 of the
origin, else the ground there (raised Swamp Houses and Cannibal Shacks: floor 25..55 above the origin, the ground under it within 10). A node with no
floor near it puts them on the nearest meshed storey below (`PopulationSystem.HomeFloor`, engine choices). **Observed** (headless probe of every
town, 600 ticks): characters on the ground under a building floor went from 278 to 128 of 9060; ~80 of those are gate guards, the rest mostly
homeless squads placed under unmeshed platforms. The Storm House's upper storey (+66) is on the navmesh except under its beds, whose
spots therefore stand on the floor below (Observed, 2026-10-18 probe). Engine choices: a committed goal chosen again after its commitment ran out
starts over with a new target; shops stand customers at a general
node, else a guard post, else any node; a home or seat given up
after three failed walks is tried again when a new goal is taken up, and a member that gave its seat up
leaves it to the next; a member keeps its seat while it still relaxes or sits (every squad's kept seats are reserved before any claims), so a
patron coming or going does not move every later sitter to another chair. Headless Hub, Squin and Stack bars (2026-10-21): each patron sits once
and stays for 12000 ticks, with or without that rule (the reported sit/stand churn is not reproduced). `hub squatters` squads (package `Squatters`,
signal 17, goals Stay home and Patrol hometown, no Relaxing) reserve no table, as a bar package takes one only when a goal of it asks for the target
([hang-around-bar package](game/ai-tasks.md)); staying home takes the furniture of the squad's own home ([ai-tasks](game/ai-tasks.md), 43).

Turret goals (149, 222, 234, 146) man a turret of the squad's town: a placed BUILDING with a
`BUILDING_FUNCTIONALITY` function 12 (`TownBuilding.Turret`: its `use range` and `gun turret`
GUN_DATA). In member order each slot claims the nearest free turret and runs to a spot behind its
gun, facing its way (**Verified**: the move-task start `FUN_140332390` sets a non-player mover to mode 2 when its target building's
use task, vfunc `0x418`, is 146, which `TurretBuilding` returns, `FUN_140440550`; `AiSystem.ActTurret`, `SpeedMode.Free`); the home-building goal (222) takes only a turret mounted on its home (the
state's `mounted` handle). Claim order and gunner spot are engine choices. The goals are done while
no enemy is sensed (list A `isNoEnemiesInVicinity`, [combat.md](game/combat.md#turrets)): false while the character's senses count an
enemy (`numEnemies`, `SenseBlock.Enemies`), so guards man the turrets only while enemies come. A guard at its turret fires it ([Ranged](#ranged)).

### Engine goals

Goals of the NPCs' packages that an engine system acts out, chosen by the one chooser as the original's (docs/game/ai-tasks.md "Precedence"). `DutyGoals`
(one per world, built in `World`'s constructor by `AiSystem.BindGoals` from the `IDutyGoalOwner` systems; null when none registered) holds each registered type: a
type of the original's that `TaskBehaviours` does not act out, registered by its own system with its `IDutyGoal` (desire, finder, states, start, end) and the rows of
its plan the act-out carries out besides the goal's own. Built: 135, 139, 282, 230, 232, 233, 237, 277, 278, 279, 260, 183, 167, 168, 173, 245, 217,
188, 192, 194, 218, 172 and 276 (`CaptiveSystem` through `CaptiveGoals`, [captives](#captives)); 176 (`SmellSystem`, [blood smell](#blood-smell)); 177 and 42 (`CarrySystem` through `LootGoals`, [looting and arming](#carrying-rescue-and-looting)); 211, 155, 197, 86 and 195 (`LockSystem` through `WardenGoals`,
[wardens' goals](#arrest-and-jail)); 180 (`LockSystem` through `GateGoals`, gates in [arrest and jail](#arrest-and-jail)); 204 (`ArrestSystem` through `ArrestGoals`, [arrest and jail](#arrest-and-jail)); 256, 207 and 202 (`LockSystem` through `EscapeGoals`, [escapes](#captives)); 254 (`KidnapSystem` through
`KidnapGoals`, [kidnapping](#kidnapping)); 273, 223, 138, 137, 112, 281, 224, 274, 106, 243, 131, 184, 212, 158, 283 and 216 (`TownDutySystem` through `TownGoals`, [town duties](#town-duties)).
- **Choosing**: a registered type has `AiBehaviour.Duty`. Its desire is the system's (`DutyGoals.Desire`: the desire times the finder's factor when the TaskData row has
  a finder and an effect; 0 for a player character's mind, an engine mind and a type in back-off), scored by the package record's tier and weight with the continuity
  and no commitment (no duty goal commits). The plan is the planner's: its rows' desires and finders are asked of the system, a finder may give a character, a
  fixture or a place (`PlanTarget.OfCharacter`, `OfFixture`, `OfPlace`), every state of a finder's target and any state the planner does not evaluate are asked of
  the system first (the latter before the held-true default), the registered rows pass the carried-steps test and are the act-out's in the plan step, and a goal
  whose plan is its own row alone that neither acts nor has an effect is dropped, as the scorer drops it (`FUN_14050df10`, **Verified**; not for a row with
  TaskData `+0x14`). The kept plan runs by `PlanSteps` (list A, list B, the hours), whose end ends the goal; a goal whose row neither acts nor has an effect (42,
  202, 254) is not kept and is planned afresh at each think.
- **Caged**: an NPC in its cage with a mind of its own (`AiSystem.CagedMind`: not fleeing, fighting, carried, down or ordered) weighs its goals at every tier,
  the planner's root row limited to the cage's repertoire (TaskData category bit 4); it acts the winner out only when it is an engine goal, else it stays held
  with no goal. A plan step on a door way through the lock system is told from a goal's own door row by its target kind (`AiSystem.IsDoorWay`).
- **Start**: the think stands on the goal's row and asks (`AiMind.DutyAsk`); the serial step (`SendRequests`) calls `IDutyGoal.Start` in slot
  order for each asker the AI still holds, with the plan; a start makes the goal hold the character (`AiMind.Duty`, the record, and `DutyTick`, the start tick of the
  task it left running; hashed), a refusal backs the type off for four think periods.
- **Holding**: the goal, not `Engaged`, holds the character. `AiSystem.Holds` is true while the provenance is the act-out (`AiSystem.OnDuty`: that task, or a goal's
  task started since; carrying included, not in a fight), so the think keeps choosing; another goal that wins sets `AiMind.DutyLeave`, the serial step ends the act-out
  and the next tick acts the winner out. An NPC carrying a body is held as anyone outside a fight (`Holds`; the original takes the chooser only from the carried one,
  [holds](game/ai-tasks.md#precedence)): it chooses with all its goals and acts the winner out (141 follows the leader with the body, a Post walks there); rows that
  need empty hands do not plan (list B `!47`: sleeping, sitting, the static guards, 36 patrolling town, the turret, assaults).
- **End** (`IDutyGoal.End` with `DutyEnd`, then the goal and plan leave the mind, which replans): displaced; an order's start (`IDutyRelease`, a faction change too); an
  order on the body (`AiSystem.ReplanDuty`), a knock-out or death, a wiped or ended plan, a new mind (replanned); anything else holding it (taken: engaged by another
  system, fleeing, fighting, carried, caged by another, a task another system started; a caged mind keeps the goal it acts out from the cage); gone. A displaced act-out whose provenance is still its own is stopped after `End` (a walk
  stands, another task but idle ends through its handler), so the AI holds the character for the winner; the other ends leave the task to what ended the goal. The
  system's own end is `DutyGoals.Done` (no `End`; a task of the act-out still running stops as a displaced one's). A walk of the act-out that ends short is a lost walk
  (`DutyGoals.LostWalk`; the gate, warden and town act-outs): its target goes into `CharacterCold.Ignored` for 20 s and the goal ends for a replan, with no back-off
  and no limit on tries (**Verified**, plan step `FUN_14050d970` → `notifyPathImpossible` `FUN_140796ed0`; only the finders that read `pathFailures` skip the
  target, not the warden ones `FUN_1408cf3c0`, `FUN_1408cb1c0`, `FUN_1408cb360`, `FUN_1408cc2a0`, nor 180, which has none). Records are hashed while any.

### Mod goals

Engine API for code mods, not in the original ([modding](modding.md), "NPC goals"). `NpcGoals` (one per world, built in `World`'s constructor by
`AiSystem.BindGoals` from `SimulationExtensions.GoalTypes` and the `INpcGoalOwner` systems; null when no mod claimed a type, and then none of
the paths below runs) holds each registered type from 1000 up with the index of the system that registered it. A type outside `TaskBehaviours`
has `AiBehaviour.None`, whose desire is 0; for a registered type `AiSystem.Desire` asks the mod instead (`ModDesire`: 0 for a player character's
mind, for a goal whose system is stopped, and for a type the mod refused to start for that character, until its back-off ends,
`AiMind.ModGoalBackoff`), and the default profile (desire "special", no commitment, crowd 1) leaves the score desire x weight with the continuity.
With no TaskData row the goal is its own one-step plan (`TryPlan`), never kept as a stepped plan. `Act` gives it `AiBehaviour.Mod`: the character
stands on row 14 (not the goal's number, which the mod may use as its own row and handler) and `AiMind.ModGoalAsk` asks the serial step
(`SendRequests`, after the engine goals: `NpcGoals.Step`). The step first drops the goals of systems the world stopped and marks their records
orphaned, then sweeps the records (`NpcGoals` keeps one per started character, by slot, hashed: the goal and the row and start tick of the task
`Start` left running): a record ends when its character is gone or dead, no longer engaged, or runs another row from 1000 up with
`TaskSource.Goal` started since, and its goal then ends on the mind it was started on; an orphaned record waits out a fight, an open conversation
or a trade, then releases the character (not engaged; a walk on the act-out stood still with no handler) while its provenance is the act-out's,
a fight's since or none. Last, `INpcGoal.Start` is called in slot order for each asker the AI still holds (one skipped replans): a start records
the character and sets `AiMind.ModGoalStarted`, `ModGoalRow` and `ModGoalTick`; a refusal backs that type off for four think periods
(`world.Every(ThinkEveryTicks)` x 4) and replans. `AiSystem.OnModTask` tells the act-out from another hold: the provenance is a goal's, of that row
(from 1000 up), started at or after that tick. With it, `ActivityText` names the goal by its AI_TASK record while the character walks or stands
on it, and `Preempts` lets the combat goals take over a goal below the urgent tier while the character walks on the act-out's walk carrying
nothing (as a captive job's walk; not while it stands, which a conversation's or trade's hold does not change). The first think that holds the
character again also ends the goal (goal and plan cleared, so no continuity) and drops the ended back-offs. Faults: a desire's fault at tick T stops
asking the desire from T + 1 (the threads of T still ask it, so the cut does not depend on the thread count) and its starts from T; a start's
fault stops both at once; each is logged once, gives a notice through the world's mod fault notices and is hashed (`AiSystem.Hash`). The minds'
new fields are hashed only while in use (a started goal, a back-off not yet dropped). Task rows from 1000 up have one owning mod
(`TaskHandlers.RegisterMod`, against `SimulationExtensions.TaskOwners`).

## New-game difficulty

`NewGameSettings` in the game host binds seven numeric options to each new world: body hunger/death/damage,
production/research, construction and nest density. Defaults (1), ranges and save keys follow
[the original](game/character-stats.md#new-game-advanced-options-that-change-these-mechanics).
The two new-game panels share one modal screen; expanding raises the start panel, and its Begin/Cancel remain usable.
Opening a fresh new-game dialog resets the draft; collapsing keeps it. Failed world replacement retains the running
world's options and body factory. Saves carry the original's CAMERA floats `ht cod gdm ps rs bs nnm`, so Meitou and
original saves are interchangeable; loading follows the original's rules exactly (the
[load rules](game/character-stats.md#new-game-advanced-options-that-change-these-mechanics), no clamping). `meitou difficulty <key>` floats of earlier Meitou saves are ignored and dropped on the next save.
Under the sliders are the original's two ticks, saved under its own CAMERA bools: Bandits loot the player (`bl`, default on,
`PlayerState.BanditsLoot`, set on a new world) and Easy prospecting (`ep`, default off, kept and saved only: the original never reads it).

## Saves

`Meitou.Data.Save` parses and writes folders and `Meitou.Simulation.Saving` maps world state. Capture
serially at a tick boundary, then finalize and write the detached save on a worker. Loads read on a worker.
`ReplaceWorld` publishes a successfully loaded world and preserves the running world on failure. [save.md](formats/save.md) owns file keys,
handles and original saved/derived/regenerated state.

Code map:
- **Capture.** `SaveCapture.PrepareBase` clones the loaded `SaveGame` on a worker (else builds a fresh one with `SaveBuilder`). The frame overlays
  live state and host fields. Characters from the loaded save keep their records and unmodelled keys. The write worker updates slot lists, record
  counters and other derived fields. Synchronous callers use `SaveCapture.Capture`, which updates those fields before returning.
- **Host hooks.** `GameSaves.CaptureOptions` supplies the strategy camera view. `GameSaves.SwapInSave` restores the camera and weather after
  `SaveLoader.Load`.
- **Load.** `SaveLoader.Load` makes characters before the first tick. The character table copies its previous state over the next one at
  `BeginTick`, so state that must reach a tick is not written into `Next` there. It goes through `World.Commands` like player input.

### Quicksave and quickload

F5/F9 and named save menus share mapping. Restore clock, player money/faction, relation/prosperity,
player squads, character transforms/names/roles, stats/medical parts, appearance, inventories,
bounties, bed/cage state and sentence, and registered buildings' locks and doors (into the zone files: every visited zone gets one, made from the level data when the
loaded save has none, [save.md](formats/save.md#zone-files)) and selection; the host restores and writes every region's season, weather, strength and wind ([BIOMES](formats/save.md#biomes-type-28-quicksave-weather-per-region)). NPC platoons whose `basetown` is a placed town load as their saved characters at their saved positions: town-bound ones (not travellers) are pinned to the town (no trips, AI-run, homed in the building their `homebuilding` handle names, else at the site) and a town holding any (other than never-made stand-ins) makes no squads of its own; travellers become roaming stand-ins made from the saved characters on activation. A town platoon the game never made (no file, `never been activated`) is made from its template on activation into the same platoon and saved back into its own record ([save.md](formats/save.md#npc-platoons-meitou-mapping)). A platoon that unloads keeps its members' state. Meitou saves write them back and the population's own squads as new platoons with their town and home building by handle ([save.md](formats/save.md#npc-platoons-meitou-mapping)). A save's nests are the wild layout's first, and its wild platoons load as themselves bound to their nest (`hometown`) or the sector they stand in, which make only the rest; Meitou saves write the nests made so far and the wild squads the original's way, the others rolled again from the saved seed ([save.md](formats/save.md#nests-town-list)); nest buildings and debris are not placed at all (`TownData.Debris` / `DebrisBuilding` are read, unused), so there are none to save. Shops, campaign phase clocks and whole-body ragdolls are saved (Trade, Campaigns, Ragdolls above). Orders/paths, animation,
combat engagement, wound-list/pain, base-game building state beyond locks and doors (interiors, inventories: a new zone file leaves them to the original's
generation), ground items and game speed are not fully restored. The camera view is ([saved camera](formats/camera.md#saved-camera)). Loaded hashes need not match captured hashes: compare carried fields and agreement
between repeated loads. QuickSaveTests checks carried fields and repeatable loading.

**Crime and jail** (keys in [save.md](formats/save.md#locks-cages-and-sentences)). Saved: bounty ledger (`bountyexp` written with a minute and a tick of slack over the minute-floored
save clock, so an entry does not lose an hour per save and load; `BountySave`), held state and sentence, the escaped flag, the recently-wanted
and recognition timers (player platoon characters, written only while set), the locks of interior cages and containers (an imprisoned character stays locked in), and the homes a
lock-up round locked (without the flag a loaded night's locks would never be undone). **Restarted, not saved** (engine choice; the guard, hunter, releaser and warden of each job is a
town NPC the population makes again with a new id, so a saved link would dangle): arrests (a downed target lies where it fell, is spotted again once awake, the saved markers make
that quick), cagings (the carry link is not saved: give the cage's Put in again), hunts and their rest timers, the gate guards' and wardens'
act-outs (chosen again), the unreachable-cage memo (relearned by a failed walk), lock picking (an order), pending crimes
(seconds long, victim id unsaveable). Spawn bounties are not saved: they are rolled when the population makes an NPC, never for a loaded character.

## Sandbox

Headless simulation/debug harnesses use selected StandardParts and synchronous services for
repeatability. Sandbox omits Aggro when its scripted foe must remain idle; benchmarks must
separate simulation work, startup and rendering.
`--sandbox-races` adds one character of every person race (the first CHARACTER record whose `race` list holds only that race; base game and
mods alike) in rings round the pair, for looking at races side by side.
The panel's **animation** drop-down (top right) forces a clip on the hero: the list holds every ANIMATION and COMBAT_TECHNIQUE record
name, typed letters filter it, the wheel, Up / Down and PageUp / PageDown scroll it, a click or Enter picks, Esc closes. The pick loops by the wall clock
from the moment it was picked (a technique keeps only its root height, as in the game), drawn by the host in place of the simulation's layers;
"simulation" gives the hero back. Nothing is sent to the simulation.
The panel's corner button (^) folds it to a lone down-arrow button that opens it again; the state is `sandboxPanelCollapsed` in `meitou.user.json`.

## Duel

The deterministic duel harness locates a scripted knockout, runs the pre-lap fight headless,
then replays the last 40 seconds and 4 after KO with cinematic keys. Replacing the world/clock
and resetting camera evaluation is required for repeatable recording.

## Battle

`--battle n unit m unit [rows] [--battle-map plateau|mesa|fog|sandbox|x,z] [--battle-width a[,b]] [--battle-no-fear]` (`BattleScenario`): two blocks of NPCs, a squad each (each one CHARACTER or
ANIMAL_CHARACTER record, its own faction's looks, the first fighter leading; a record that names no faction, as CHARACTER records rarely do, takes the first faction, not the
player's, that is an enemy both ways of the other side's, or of the player's, so the two sides' senses tag each other enemies as hostile groups' do) 40 units apart in ranks of 25 (or `rows` ranks of n / rows, the back rank taking the rest; `--battle-width` sets
each side's rank length instead, one value for both), 6 units apart
(times the race's `pathfind footprint radius` / 4 when larger, the front rank at least that far from the middle), on a plateau (default, -74028, 13428), the duel's mesa, a Fog Islands spot (-71560, -39953), the sandbox floor or a map spot, from 15:00 (or `--time`), with no towns
and no Aggro. The host orders every standing fighter without a combat target at the nearest standing enemy, once per world tick, so
same-faction matchups fight too. Once a second a fighter left out of a target with more than 5 after it (circling) turns on the nearest standing
enemy with room, as the original's target score counts a candidate's engaged fighters against it ([combat](game/combat.md#attack-slots-and-circling)). `--battle-no-fear` leaves out the flee system; squads do not run for being broken anyway (`FleeSystem.SquadBreak` is off). The battle ends the first tick a side has nobody standing. Backspace rebuilds the world the same way.
A counter at the top middle (with the interface, stills too) gives each side's fighters standing (not incapacitated) and alive (not dead) of how many it started with.

## The game host

`Meitou.Game/GameHost*.cs` adapts input to commands, drives WorldSession, navmesh upkeep,
interpolated CharacterDrawList, HUD, loading/transitions and saves. It does not duplicate system
order. Renderer state lives in Rendering; snapshots are the simulation-to-host boundary.

### Event log

**Engine** (a debugging aid; no original counterpart). `World.Events` (`EventLog`) collects lines in the serial phases; the host writes them to
log.txt between ticks. Each line is a category, the game day, hour and tick, then the text. Nothing reads them back: not saved or hashed.

- `fight`: a fight start (`CombatSystem.Engage`), with the attackers' faction, the target, the cause (`AttackOrder.Cause`, set where each order is
  made: an AI goal's row, self-defence, protecting an ally, a crime alarm, an arrest, a dialogue line, the player's order) and how the two factions
  stand. Fights started in one tick are one line per attacking faction, target and cause; a fight started again on the same target for the same
  cause within 60 game seconds is left out, as is a fight re-ordered while it runs.
- `relation`: a faction relation change (`PlayerRelations`, `DialogueSystem.ChangeRelation`), before and after, with its cause; a blow's or a
  bandage's (one per hit) only when it crosses the hostile or the ally line.
- `crime`, `arrest`, `kidnap`: the crime, arrest and abduction systems' events (pending, witnessed, booked, expired; the arrest steps; lifted,
  caged, dropped, gave up).
- `down`, `death`: a character knocked out, coming round or dying, found by comparing each tick boundary with the last (`DownLog`), with the
  death's cause and the last character that landed a blow on it (not a recorded killer: a bleed-out has none).
- `recruit`: a character joining the player (`DialogueSystem.JoinPlayer`) or dismissed.

### Action log

**Engine** (a debugging aid, off by default; `--action-log off|squad|everyone`, F10 "Action log"). `ActionLog` writes `log_actions.txt`
beside log.txt, so a log sent with a report has it at hand, kept like log.txt (`GameLog`, cut past 10 MiB). At each tick boundary
(`World.RunTick`'s end and the orders given while paused), every followed awake character whose task or task provenance
(`CharacterCold.Running`) changed gets a line: the task left, why (`TaskStart.End`'s reason,
else replaced, or ended by its own system), the task taken up and the start's source, row, job, target and site. Squad follows the player's
characters and adds the decision step's trace of that tick (it turns `DecisionSystem.Tracing` on); everyone adds every NPC and animal (in a town,
about 650 lines a minute), and for each NPC whose goal lists hold 148 a line whenever where that goal stands changes at a rescue beat
(`AiMind.BedRescueNote`, set by `CarrySystem.RescueToBeds`, neither saved nor hashed): "rescue 148, not eligible: ..." (not able to lift, nobody is
down, no downed ally its finder takes, the ally found is one it would not lay in a bed, no bed for it), "wanted, not chosen: its goal is ...",
"chosen, not free to lift: task ...", "chosen, the lift would not start" or "chosen, lifting". It only reads the world. Its lines, like
log_dialogue.txt's, only join a queue that a thread of its own writes to the file (`QueuedLog`), so the tick never waits on the disk; as the game ends
on an exception, the queue is written at once.

## Town house purchases

`BuyBuilding` (the panel's `For sale:` price, then the original's confirmation; [original rules](game/ui-screens.md#selected-building))
rechecks the sale flag, town kind, owner and its hostility, the quoted price and the money, then takes the money once.
Sellable: function 19, or no function and interior meshes; no walls, gates or `is inside building` (the original's class and
parent tests are not fully decoded). Ruined houses register without fixtures. The bought house's doors, own lock and
indoor build permission become the player's, every door is opened (`FUN_140297040`, **Verified**), NPC lock rounds on it stop, and it turns private. As the original's purchase
([what it does](game/ui-screens.md#selected-building)) the house is emptied: its interior layout's fixtures (cages, containers with
their contents, beds, dummies) and seats go, occupants let out first; its loose-item slots go with the items lying in them; the
renderer, navmesh and lamps lay it out without its interior layout (`BuildingPlacement.WithoutInterior`: `FixtureRegistry.PublishedEmptied`,
or a bought ruin's site placement). The pieces of its residents' camp standing on it (as a Hub squatters' ruin's campfire and camp beds) and
the camp's debris round it go too, the camp's pieces beside it when the town unloads, and a town never lays one round the player's buildings ([nest debris](formats/zones.md#nest-debris-and-loot), #857). Every squad living in it (engine reading: the original takes the owner faction's, whose town
housed them) moves to a random other house of its town that is for sale (by the original's finder), else has none; its members plan
again. Their town never houses residents in a building the player owns (`ResidentHomes.Assign`, `AiSystem.AssignHome`), so nobody
lives there to call the buyer a trespasser. Saved as `owner faction ID` with
`sell home` false; a loaded player owner that is not the level's counts as bought (saved again), is applied before the fixtures are
made and empties the house again (a container's contents from an older save are dropped at its place); a squad of an older save living
in it moves out when its AI attaches. Not built: the sound emitters of interior furniture (ovens; `WorldObjects.Emitters`, made once from
the level data) stay.

## Open questions and known gaps

**Engine gaps** are listed where they apply, not here: [unique characters](#unique-characters) and [campaigns](#off-screen-tier),
[locks](#cages-beds-and-locks),\n[slavery](#arrest-and-jail), [kidnapping](#kidnapping), [buildings](#buildings) and walls, [production](#production) (turrets),\n[prospecting](#prospecting), [AI](#ai) (planner finders).\nSmuggling and stealing: [trade](#trade). UI: [faithful-ui-parity](faithful-ui-parity.md).

- Navmesh: a running build is not stopped on unload (it finishes into the cache).
