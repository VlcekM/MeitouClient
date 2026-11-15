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
a snapshot (`RunTick(publish)`), the pair the host interpolates between. Advanced → Pause after loading (`PauseOnLoad`, a Faithful / Meitou switch, Meitou and on by default; Faithful runs a loaded save at once as the game does, [game loop](game/game-loop.md)) pauses the speed after a menu load or import; `--load` runs.

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

## Threading

Tick order: Inputs → Schedule → Think → Move → Act → ordered Apply → SlowWorld → Publish.
Inputs/Schedule/Apply/SlowWorld are serial; each implemented parallel phase has a system barrier. Publish makes the character snapshots on the
workers in 256-slot chunks (each reads only the tick's state), listed in slot order.
Engine choice, to keep the publish small with ~1,400 characters: the context menu's figures (kidnap and stealth KO chances, shackles) and the AI goal text
go only to the selected, inspected and pointed character (`PlayerState.Pointed`, set by the host as the mouse moves) and the player's (goal text);
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

### Task starts

`TaskStart.Start(world, slot, TaskRequest)` (serial phases) is the one start path. In order: the row must be `Eligible`; a fight ends (the original's new task drops the fight task,
[combat](game/combat.md#fight-lifecycle-and-orders)); the task before ends through its handler (`TaskEnd.Replaced`: it lets go of its target, while the movement, the task and
the task it goes back to are the new start's); the provenance is recorded in `CharacterCold.Running` (`RunningTask`: act-out, row, `TaskSource`, job index or implicit flag,
target, site, tick, resume task); the act-out starts.
`TaskStart.End(world, slot, reason)` ends a task and drops its provenance: `TaskEnd.Stopped` from outside the act-out (the Stop order, a squad mate lifted awake, a leg giving out: `BodySystem.Collapse` also clears the order queue, [ragdolls](game/ragdolls.md#forced-crawl); then idle);
`TaskEnd.Done` the act-out's own end (a treatment, carry, site's or machine's work, haul trip, prospecting, a job's follow: the handler lets go, then the character takes up
`RunningTask.Resume`, engine; one the [decision step](#decision-step) decides stands idle; every end also sets the replan bit). Walks (bed, item work) end by standing; their provenance then no longer runs the task. A lock order's worker (lock, shackle, cage, door, force work)
stands at its work and its provenance runs `Idle` from then (`LockSystem.AtWork`), so the next start ends the work through the lock handler (a move order given at a cage walks away). Each `CharacterTask` has one
`ITaskHandler` (Start, End, Done), registered by its system (`ITaskOwner`) in `World.Tasks`, an open list later code may add to or replace; a task without one is set as is. Owners: movement
(GoTo, Follow, Idle, Wander), combat (Attack), first aid (Treat, Rest), carry (PickUp, PutDown, Loot, StealthKo), production (Operate, Haul, Prospect), construction (Build, Dismantle,
DeliverMaterials, FetchMaterials), beds (Sleep). A row may have its own handler, which takes that row's starts and ends whatever its act-out (the task before is ended through its running row's
handler): the lock system's rows (lock, shackle, cage, door and round work), the beds' put-in rows 99 / 106, the player system's row 3 (item pick-up, container walks) and the arrest system's
put in cage 108, all acted out by a walk (`GoTo`) or in place (`Idle`). `TaskRequest.Detail` carries an act-out's own parameters (a shackle job, a ground item, a round). A builder's
delivering, fetching and dismantling are steps of one act-out (`TaskStart.ActOut`): `RunningTask.Runs` and the debug check treat them as Build.
- Routed through it: move, follow and bodyguard (order and job), stop, every fight (`AttackOrder`: picked is an order, `Defensive` a reaction, else a goal), First Aid, Splint, the Medic
  button, medic, robotics and splint jobs, NPC healing, Rest, pick up, kidnap, loot, open inventory, stealth KO and put down orders, the lifts and drops of errands and town duties, rescue
  (job, button, NPC leaders), kidnappers' and guards' lifts (`BeginPickUp`), foraging, NPC scavenging and arming, the operate, turret, haul and prospect orders, the production list jobs
  (87, 97, 124, 152, 234, the engineer 125) and implicit jobs (242, 153, 275), the build, repair, door repair and dismantle orders, the bed orders and job 106, the lock, shackle, cage,
  door and force orders, the wardens' rounds, item pick-up and container walks, put in cage; the engine's starts: crime, arrest and dialogue fights (`AttackOrder.Source`: `Npc`, a kidnap
  victim's `Reaction`, an NPC's attack on sight `Npc`), arrest and takeover walks and lifts (203, 205, 206), kidnappers' walks to the cage (112) and lifts (132, 168), captive jobs'
  walks (the row of their task, `CaptiveSystem.RowOf`), town duties' walks, lifts and drops (the goal of their kind, `TownDutySystem.RowOf`), smell hunts (176), dialogue acts (run away
  280, go home 19, summon 67, follow while talking 164, stalk 165, approach 126, a bought slave's way out 1), a trade's close (wander 24), flight (35, then wander 24), food on the ground
  (259) and limbs (277), NPC arming walks (42), roaming and campaign leaders' walks (53, 215) and mills (24). Engine walks go through `TaskStart.Walk` (`TaskRequest.Goal`: as given, or
  `Point` onto the floor, at `Mode`); engine standing tasks (idle, wander, a follow of the squad's leader) through `TaskStart.Become` (the movement's handler stops the character on it).
  Sources: a package goal's act-out (town duties, captive jobs, kidnap, smell, food, population) `Goal`; an engine rule or dialogue act (arrest, flight, dialogue, trade) `Npc`.
- An act-out's own system moves its character on with `TaskStart.Step` (another task of the act-out, or back to a standing task), `TaskStart.StepTo` (its next walk) and
  `TaskStart.Stand` (stop, idle): any phase, its own slot, provenance kept (it then no longer runs the task). These are the walks' arrivals and give-ups, a failed or blocked path, a
  follow whose target or leader is gone, a builder's steps, a bed reached, a fight's end (`CombatSystem.EndFight`: idle, a follow row's provenance kept for `FollowResume`), an NPC's
  stand-down back to its routine (`AggroSystem`), a knock-out (`BodySystem`), a caged character held
  in place (`FixtureRegistry`).
- Engine starts end the fight and the task before as any start does (a flight's run stops the fight at once, a dialogue act's walk ends it). An errand's own walks would end a fight
  its character is in, so they wait for it: a town duty steps nothing while its member fights (a player character's food run ends instead), an approaching, following or stalking talker does not walk (engine choice; the
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
owning `IOrderOwner`, target, site, point, owner detail): the front is the order in hand. Every order command (`OrderCommand`) goes through `PlayerOrders.Give`: without `Queued`
(Shift) the queue is cleared first, as `addOrder`; the order is queued; at the front it starts through its owner when `PlayerOrders.CanRun` says so (the row's repertoire, then
the owner's pure `IOrderOwner.CanRun`: `Run`, `Blocked` with its failure text, or `Gone` when its target or site no longer fits the verb).
- `PlayerOrders.Runs`: the front started and its task's provenance is an order's that runs the task (a move also needs `OrderMove`) and the handler is not `Done`. Lock work and item or
  container work done in place (`Idle`) report `Done` once the character holds no work.
- A front whose task ended leaves the queue (a move's arrival in the character's own partition, `PlayerOrders.Arrived`, which also starts a queued move at once as the move-only
  queue did; `TaskEnd.Done`; else the decision step, `PlayerOrders.Prepare`) and the next is tried at once. An interrupted move drops the queue (as `GoTo`/`Stop` dropped queued moves),
  so does `TaskEnd.Stopped`; a start from another source replacing the running front pops it (a follow row's fight step does not). A walk that finds no path still clears the whole
  queue (`MovementSystem`; a gap: the original keeps a failed order queued with its text, but Meitou only learns of the failure after the path search, and re-asking it would ask for
  the path again every `JobCheckTicks`).
- After `Give` the decision step ([below](#decision-step)) asks the front again as its source 1: a blocked one falls through to goals and jobs there.
- `PlayerOrders.Holds` (a pure read): the character holds an order, running, waiting or queued (the original's count `+0x60`; a front whose task ended is not counted). The
  implicit ditch job 242 runs only without one ([perma-jobs](game/ai-tasks.md#perma-jobs)). `PlayerOrders.Wins` (pure): the front runs or can start, so source 1 wins; the
  player animals' race-goal jobs (`CaptiveJob.PlayerSide`) end once one runs or can start (a blocked order does not end them).
- A row needing no melee threat (`PlayerOrders.NeedsNoThreat`: `!158` in list B, of the orders only 146 Turret gunner) is blocked while `CombatSystem.ImmediateThreat` holds; at a
  decision its running order ends (`TaskEnd.Stopped`) and waits unstarted, so the gunner falls through to self-defence and mans the turret again once the threat is gone (S3).
  An owner's `IOrderOwner.StillHolds` does the same for its own start conditions: an operate or gunner order (87, 146) whose machine has no power
  ([state 93](game/ai-tasks.md)) ends, waits unstarted and says "It needs electricity." again.
  A Loot order (26) walks through a fight; once its window is open the loot ends (`TaskEnd.Done`, closing it) while a threat is within 40 (`Task_Loot_Order`'s update,
  `CarrySystem.HoldWindow`, [carry-loot](game/carry-loot.md)).
- Group orders keep their pickers (the nearest or first character that can run it: pick up, loot, stealth KO, the turret, item pick-up, containers, lock work); only that character
  gets the order, none when nobody can run it. Per-character orders (move, follow, attack, first aid, splint, rest, the Medic and Rescue buttons, put down, operate, haul, prospect,
  build, repair, dismantle, beds, put in cage) queue blocked for whoever cannot run them.
- `World.CanOrder(row, actor, target, site, point)` / `CanOrder(OrderEntry, actor)` -> `(Can, Why)` is `PlayerOrders.CanRun` as a pure read between ticks (a `Gone` verdict is `false` with no text); the queue's start and bark use the same read. It is not a menu filter (decision D5): the original's menu offers its verbs (operate, build, dismantle, prospect, ...) without testing the character and lets the order fail with its text, so Meitou's does too; no verb is greyed. Planned native multiplayer can call it on the host to gate guest commands.
- Every `OrderCommand` also applies while paused (`World.ApplyPausedInterfaceCommands`, through every `IOrderOwner`'s inputs).

### Decision step

`World.Decisions` (`DecisionSystem`) chooses every task of the characters it decides (`DecisionSystem.Decides`: `IsPlayer`) as the original's chooser does
([precedence](game/ai-tasks.md#precedence)); NPCs keep the AI ([AI](#ai)). Serial, in slot order: in the slow world right after the last of retaliation, aggro and flight (after every
system when the world has none), and between ticks for orders only (`ApplyPausedInterfaceCommands`; the bit stays for the next tick).
- **Sources** (`IDecisionSource`: `CanRun` and `Desire` pure reads, `Start`): an open list sorted by step, then `Key` (the original's list order); systems register theirs
  (`IDecisionOwner`), later code may add more (`DecisionSystem.Register`). The first source that can run starts; one that runs the character's task already ends the step. Step 4 picks
  the best desire x weight (x1.25 for the goal running now; a tie goes to the later-listed).
  1. Orders: the front of the queue ([order queue](#order-queue)): runs, starts, or (blocked) shows its text once (`Tried`), stays queued and the step goes on.
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
- **Triggers** (`CharacterCold.Replan`, `DecisionSystem.Request`; hashed while set): a task end (`TaskStart.End`, any reason), an order given or the queue cleared, a stagger outside a
  fight (not for a gunner), the melee alert while the fight is off unless the target gets up or is player-side and moving (horizontal speed above 1), a job list change or the jobs switch,
  a load, a sighting for row 16 (engine: the look stands in for the sensed list's update), a mate's or ally's defence offered, a failed kidnap's or knock-out's victim told of its attacker, a fight's end (a follow row then follows again,
  `MovementSystem.FollowAgain`: for an order in `PlayerOrders.Prepare`, for a job as its contributor). Also a front whose task ended, or one not tried yet. **Engine** re-ask (no
  periodic replan was found): every `ProductionSystem.JobCheckTicks` 10 (slot-staggered) a character not fighting whose task is a job's, 14 Idleness's or no start's, or an order's of a
  row needing no melee threat (the gunner); jobs and implicit jobs are found on this re-ask and the triggers.
- **Engine starts outside the step** (a step of a running row, or a reaction to another's act; the step leaves a claimed character alone that tick, `DecisionSystem.Claim`): a follow
  row's defence of its followed character and a bodyguard's own defence within 40 (`RetaliationSystem.Guards`, `SelfDefends`; claimed).
- **Trace** (`DecisionSystem.Tracing`, off by default): each decision fills `CharacterCold.Decision` (`DecisionTrace`: tick, winner, one line per source asked with its answer: an
  order's failure text, "no attacker in range", a score, a job's index), published as `CharacterSnapshot.Decision` while tracing. Not saved or hashed.
- **Player-faction characters that are not `IsPlayer`** exist only in Meitou: the sandbox's and `--crowd`'s bystanders and tests' extras (a load, a new game or a recruit makes every
  other player-faction character `IsPlayer`; the original has no player-faction character the player does not command). They hold no jobs and get no orders from the interface, so
  the step leaves them to the NPC rules (retaliation's own defence, the attack-on-sight look) and the player faction's goals as idle-only passes: 243 (`TownDutySystem.LookPlayerFood`),
  259 (`FeedSystem`, an animal idle or following) and the animals' race goals (`CaptiveSystem.RunPlayerAnimalGoals`, list order). A follow row of theirs (an order a script gives)
  follows again after its fight step at the NPC stand-down (`AggroSystem`, `MovementSystem.FollowAgain`).

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

## Population

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
screen (`PrepareLoading`); the winner goes into `TownSite.Replacement`, and `TownSite.Town` (what every reader uses) becomes it with the placement's id and names
kept (engine choice) and the old faction when it names none; `SiteFactions` follows. The town then makes the replacement's residents even if it was made before;
its old squads stay. Until those residents are made (`SiteState.Repopulate`, the original's `updatedTownOverrides`, not saved) each load makes them afresh without a
new choice. Each such load resets the zone (`ResetZone`): the town's buildings there that are not the player's are registered again from the level (state, locks,
doors, contents; `FixtureRegistry.TownReplaced`; a save writes their owner and level `destroyed` into the old record of each, fixtures or not, `WriteResets`), its other buildings change owner only, the zone's item slots are due at once and its loose items are removed.
Saves write `replacementTown` (and the town's `faction` while replaced) and read it back; a new game or a load clears them first.
Tests: `PopulationTests.A_town_takes_its_heaviest_qualifying_override_at_each_load_and_makes_its_residents`, `..._whose_residents_were_never_made_...`,
`TownOverrideSaveTests`.

Six Faithful/Meitou switches (the fifth, campaign inheritance, under Campaigns below; `PopulationSettings`, live on `PopulationSystem`; Advanced settings (the fifth on the Fixes page), Meitou on by default; `PopulationOffscreen.cs`):
- **Healing far away** (`OffscreenHealing`). Meitou: when a kept squad comes back, `MedicalState.Recover` steps the hours it was away in one go:
  wounds treated and closed, flesh healing at the part rate, stun gone, blood back at the resting rate, a KO ended if vital parts allow; no
  hunger, degeneration or death. Faithful: it comes back as it left.
- **Towns keep their schedule** (`ResidentSchedule`). Meitou: a town squad that comes back after at least an hour unloaded (`ScheduleAfterHours`) is
  marked `Squad.Resume`; each member's first think picks where its plan's walk ends (its bed at night, the counter, its node, seat or gun; a ring of 4 by member index, walkable only), the serial step moves it there (Movement would overwrite a move from the parallel think) and
  the act-out goes on from there, so the town looks as if it had lived through the time; nothing runs while it is unloaded. Faithful (the
  original, which runs nothing for residents off screen): where they stood when it unloaded.

Implemented (`PopulationRoaming.cs`):

- **Pools** (`PopulationPools.cs`): every town and nest has the original's two pools (residents `pop`/`popd`, roaming `pop2`/`popd2`),
  read from the save's town and nest states and written back, with `started`, `ded`, `tod`; the hour they last grew is not saved (set at load,
  as the original). Every 2 s (60 ticks) they grow 0.21818 per game hour up to the desired max (roaming only once residents are within 1 of full, then credited the time it waited), for every
  town with pools (the save's from the first look, a new one once made) and the save's nests from load (a rolled nest from its first load), loaded or not. Growth is not state-hashed (as the population's other site state).
  - **Towns**: one made from its data counts its resident squads (those with a home; bar squads not, engine choice after the census) into `pop`
    and `popd` and is `started`; a started town never makes residents again, even with every squad dead (it holds what is left). Town roaming
    pools stay as loaded (0 in every base save) and `RoamingShare` still caps roaming squads (see the census conflict in
    [off-screen world](game/factions-squads-towns.md#off-screen-world)).

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
- **Assault**: in phase 1 a loaded campaign squad also fights (`Squad.War`, read by the aggro) everyone not of its faction in the target town or in no town (not animals, fake or bandit factions: [sensing hooks](game/ai.md#package-classes)) when its
  `pt2` package is a Package_WarAssault (20, 32), else the target faction when the campaign is hostile. Looting is [goal 177](#carrying-rescue-and-looting) where the package lists it. A cannibal assault (class 32) goes to phase 2
  when three members of one platoon carry someone or 15 game minutes after that platoon's first carrier (`CampaignInstance.CarryStarts`, per platoon, cleared while it has none; saved by Meitou only); kidnapping: [below](#kidnapping).
- **World states** (`WorldStates`, `PopulationSystem.States`): WORLD_EVENT_STATEs answered live by the original's rule
  ([world states](game/factions-squads-towns.md#relations-and-world-states)) from the unique table and the relations (a copy of the table taken every tick, serial; the original reads it live, `FUN_1409a7e00`, **Verified**; the table's own scan `RefreshUniques` still runs every `CheckEveryTicks`); the squad factory,
  wild sites, towns and campaigns all ask it. A population given its own `IWorldStates` (the pinned tests) uses that instead.

## Player

`PlayerSystem` / Orders: selection, stop, move/queued move, attack, treat/rest/medic, carry/rescue,
loot, stances and squad toggles are commands. Resolve empty-ID orders against current selection.
New-game start data seeds squad/appearance/gear/money; the start's characters are then remade in the
character editor while `PlayerState.InCreation` ([characters](characters.md#character-editor)); original start
relation/research semantics remain partly unknown. Picking/HUD are host responsibilities, never simulation input.
Squads: `SquadManagement` orders the player's squads (`Squad.Index`), keeps `PlayerState.Squad` the active one and applies the squads screen's
commands with the original's limits ([Squads tab](game/ui-screens.md#squads-tab)); the snapshot's `PlayerSquads` lists them for the UI.

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
during which it neither walks, works nor fights (Task_GetUp; it counts as staggered for the work systems).
Only a fighter blocks or dodges. A landed blow on a shackled defender knocks it out 25% of the time (`PutDownKoSeconds` with 0.002: 3 s). A hit on a
character outside a fight that is getting up does nothing more; on any other (not a gunner) it restarts the task (timer, lift or knock-out swing, and a
new path from where it stands; a lock's, site's or machine's progress stays with the object), and a picked stumble clip stops its walking and its work
(first aid, lockpicking, building, operating) until the clip ends (`CharacterCold.StaggeredUntil`). A fight's end puts no weapon away: a player's character sheathes once it is neither walking nor following on an order,
an NPC by its planner's UNEQUIP_WEAPON step (one without an AI at once).

### Ranged

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
(`MannedTurret.Angles`) and the renderer turns the meshes by them (render-objects). Meitou fix (Fixes page `Turret gunners turn`, on by
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
relations, shared targets, player self-defence as a [decision step](#decision-step) goal (order toggles), chase give-up and return to routine. The look keeps every
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
`Town`). Engine choices: every striker rates above the 0.1 / 0.15 thresholds; a death is credited only at the killing blow (a later
bleed-out has no killer); first-aid gratitude on every bandaging update. The attack order and looting change no relation. A
witness that regards the culprit as friendly takes 20 off on every sweep (`CrimeSystem.BetrayalDelta`). Wars:
`PlayerRelations.DeclareWar` / `EndWar` (one direction, the speaker's table; the war flag is not saved). Original threshold/sense/retreat facts:
[perception.md](game/perception.md), [AI](game/ai.md), [factions](game/factions-squads-towns.md).

## Carrying, rescue and looting

- **NPC looting** (`CarrySystem.Scavenge.cs`, facts: [carry-loot 3.3](game/carry-loot.md#33-npc-looting)): every `AutoEveryTicks` on its slot's turn (slot mod the period, so the looks spread over the ticks, as the original's AI updates run 8 characters a frame in turn, [game loop](game/game-loop.md#the-character-update-budget)), an NPC the AI runs whose package lists goal 177 for its role
  in the hour (`PopulationSystem.GoalDuty`) loots the nearest dead body (not of its faction), else downed living one of a non-allied faction, within 100 and reachable (no shut gate route between, no building without an open door around it unless the looter is in it, `CarrySystem.Reachable`): the player's only with
  `PlayerState.BanditsLoot` (CAMERA `bl`, default on), only whom `shouldIScrewThisGuyOver` allows (classes, love/hate tags), not one its squad tagged short-term 6
  (`MemoryTag.Looted`). At the body (`Loot` task, `LiftTime` from arrival, `medic` clip) it takes after 8 s (animal 20): weapons when it has none equipped, food (functions 3, 15, the
  backpack's 3), and for a non-player-side or dead body, unless it is a Bandit or malnourished (floor < 2.8), every item and armour; a weaponless non-bandit grabs one weapon of a
  living player-side body at 0.8 s instead. Then the tag, and back to its task. On arrival it says event 33 or 32 and may get a pending Looting (`CrimeSystem.NpcLooting`,
  [looting crime](game/carry-loot.md#33-npc-looting)), which its witnesses answer as any NPC culprit's ([crimes](#crimes-and-bounties)). A police-like looter (`CrimeSystem.IsPoliceLike`) confiscates instead: past 0.8 s one unit of each stolen item of the body's inventory (`CarrySystem.Confiscate`), tagging it only when there was none. Engine choices: nearest per pass, the tag never expires.
- **Unarmed NPCs** (`CarrySystem.Arm.cs`, goal 42 "Don't be unarmed" with step 8 FIND_WEAPON, `AI::findWeapon`): on the same cadence an NPC without a weapon, with a working arm,
  whose package lists 42, goes for the nearest free ground weapon within 100 (in a town only an unowned or enemy-owned one) and picks it up, else a dead body with a weapon,
  else a downed living non-ally with one (player side only with `BanditsLoot`, not one its squad loves), which it loots as above. Engine choices: the body loot takes
  more than the weapon; the nearest body, not the sensed-list order; accessibility left to the walk.

## Ragdolls

`Ragdolls/RagdollSystem` (game behaviour: [ragdolls](game/ragdolls.md)) runs serially after animation
when the host passes `CharacterRigs` (the game does; tests and goldens do not). Engine choices:
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
animal's; the victim may be a player character), and an NPC looter its Looting ([NPC looting](#carrying-rescue-and-looting)); both are booked like a player's (**Verified**, the witness function `FUN_140854d10` ends in the booking call with no player test; a Bandit, Diplomat or LawEnforcement class is never booked).
Every witness that sees the culprit reacts, not only the first after the booking (the witness function runs per looker, `FUN_1408580d0`): with a
stop there, a police 1480 off in sight range booked a slave hunter's assault at Sho-Battai's gate and the gate guards 40 away never reacted
(**Observed**, headless, a player quicksave).
The setter itself refuses no NPC code (**Verified**, `FUN_140852c80`): any other crime a caller sets on an NPC (the cuffing task's Enslaving) is pending too and witnessed the same. A police-like witness not fighting re-fires the witness event (41 for an Assault, 42 for a Looting) at it every
sweep, since the guards' lines (e.g. "Shek guard witness crime": DA 29, 33, 6) need it within 260 and not running; not a Trespassing
(an NPC gets one from a DA 33 in a private home), whose event-17 line only restarts a chase forever (#519). So a town's
guards turn on hungry bandits (Starving Bandits, -10 to all: not hostile, so no aggro target) attacking someone there.
Trespassing (`Trespasses`, [economy 11.2](game/economy.md#112-how-a-bounty-is-gained)): a player character inside
(`SightMap.BuildingOf`, not on the roof) a town building that a squad lives in is a suspect each sweep; a witness that sees it and
regards its faction as neither ally nor enemy sets the pending crime when it lives there, its squad has locked up
(`LockSystem.IsHouseLocked`, the lock-up round's flag; the original also sets it when a resident closes a lockable door) and the
building is not open (template `public day`, kept as `Blackboard.HomePublic`; open 7:00-22:59); or when it is police-like, the
building is not public, its residents' faction is not an ally, and the culprit is a slave or the residents have locked up. The
witness rules above then fire event 17 and a police-like witness books 100. The MY_INTRUDER memory tag is not kept (nothing reads
it). DA 33 CRIME_ALARM (`CrimeSystem.Alarm`): the speaker attacks the target, the target's crime timer is 30 and it is recently
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
- **Beds** (`Locks/BedSystem`, serial; facts in [locks](game/locks.md#beds)): bed fixtures are the town interiors' beds and, registered with the player's
  buildings (`<site>@bed0`, site -1), the beds the player built; each keeps its `BedData` (function, clip, construction materials). `UseBedOrder` (258: a quick
  right click on a bed, or its held menu's "Sleep") sends every awake, non-carrying selected character (as the original; the first there takes it, the rest are refused); `PutInBedOrder` (99: "Put in Bed" on the bed's menu;
  a quick click while carrying, engine) sends carriers, the cheaper rent paid, the body let go
  and laid in. In reach (`use range` + 15, on the bed's floor within 20, or a walk ended within 60) it pays the rent (`Cost`/`Pay`, player only; the 24 h memo
  is engine state, not saved) and enters, its orders dropped; the Sleep order (258) keeps running while it lies there, until replaced, also for a
  sleeper loaded lying in that bed (#684: else its follow job got it up); 3 short walks give the bed up (`MarkUnreachable`). In a bed the task is `CharacterTask.Sleep` (held still, the
  bed's clip, `Sleeping` on the HUD); any other task gets it up (`GetUp`; a knock-out or death in bed keeps it there). The player's bed search `FindBed`
  follows the original's weights over beds within `BedSearchRange` 3953 (engine, for the town). Job 106 (in the list with the rescue job; the decision step's, `BedSystem.PutInJob`): a carrier of
  a friendly, non-hostile body with no grudge tag against it, and no bounty owed to a police-like carrier, lays it in the searched bed. Lifting a sleeper takes it out. Engine difference: no power test. The rescue job (148) and the one-shot Rescue start a lift only while the bed search finds the rescuer a bed (`CarrySystem.MayRescue`).
  Saves: `HeldSave` (a player-built bed also writes the original's `in what` = its building's handle; read back through `Bind`); a loaded sleeper's task is Sleep,
  and `Bind` puts it at its lie spot still sleeping (a standing task would get it up).

- **AI sleepers** (going to bed, 153/239, which plan into 98 Sleeping, `Task_UseBed`): arrived at their home's bed spot, the serial step (`AiSystem.LieDown`) lies
  them in that bed, else in the nearest free bed of the building within 30 (engine choice); with none free they rest on the floor there. Without a bed spot at
  home they go to the bed `AiSystem.SpareBed` finds ([locks](game/locks.md)); with none the goal is not wanted (desire 0).
  One already in a bed keeps it: the goal's target is where it lies, so its plan is 98 alone and it stays in; the goal takes its
  sleep as its own (provenance 153/239, so the plan step 98 is its order, as the original's `Task_UseBed`), also for one loaded
  in bed or laid in by the walk's end. A goal of any other behaviour gets them out (`LeaveBed`, in the think).
  `CharacterCold.InSomething`/`InWhat` name the fixture; an imprisoned character is skipped by AI, movement (held still),
  aggro, answering, `CombatSystem.Engage` as attacker (it can still be attacked) and carrying. Sentence: `SentenceStart`
  (`World.GameHours`) + `SentenceLength`; served when start + length <= now; no bounty with the cage owner: none (served).
- **Lockpicking** (`Lockpicking`, serial; the player's order is in [Arrest and jail](#arrest-and-jail)): `Step` per task update (pending crime,
  timer on the lock, seeded roll by the lock's roll counter, XP), `Allowed` (min chance; engine: conscious, an arm, not
  carried), `Break` (strength > 3 x hardness); `CutLock` / `Break` also serve doors, containers and cages (Use Tools / Use Strength, `LockSystem.Force.cs`, [locks](game/locks.md#player-orders-on-locks)).
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
**Engine choice**: the arrest (`ArrestSystem`) runs the original's chain (fight 7, strip 181, lift 240, then the cage errand of 112) as its serial phases, not through the AI planner, because dialogue, bounty-duty and slave arrests all start it; the chain's rows and states are in [AI tasks](game/ai-tasks.md#arrest-and-prison-rows). Goals 203, 112 and 281 are not in `TaskBehaviours.Table`.
- **Spotting talks** (`ArrestSystem.Talk`, the dialogue system, while its event lines are on): the looker fires event 44 (escapee: 45) at the target
  (`DialogueSystem.FireEvent`, a bubble now) and the data decides ([economy 11.7](game/economy.md#117-arrest)): DA 29 arrests (`ArrestFromDialogue`,
  below; an animal target, or a guard outside his town, is attacked instead: `ArrestSystem.CapturesInTown`), "Small bounty" sends the leader to talk (DA 2) and its interrupt opens the fine window (DA 8 + 49 pay and clear; the rest DA 29), others
  say nothing more. The target is then left alone for 60 game s (`SpotRetrySeconds`, engine), longer while the guard still talks or walks up; paying ends it.
- **Town leash** (`ArrestSystem.OffLeash`, [arrest chain](game/ai-tasks.md#arrest-and-prison-rows)): a dialogue capture ends when its guard stands in no home town or its target in no town.
- **Arrest**: attack until the target is down (combat ends the fight; nobody beats a downed target), lift
  (`CarrySystem.BeginPickUp`), `FindCage` from the guard over his town candidates (`CageCandidates`, as kidnapping and town duty; his squad's home building; other arrests' cages reserved), walk,
  and at the cage (`FixtureRegistry.AtCage`: within its `use range`, doubled standing, and under 15 above or below it) `LetGo` + `Imprison` (`prison time` CONSTANTS). The fight
  stopping with the target up re-orders it within 800, else he gives up; no free cage: he drops the captive (engine); a downed target is not lifted at all with no free cage, nor from an ally who carries it (engine: guards passed bodies round a full station, #339). After 3 walks that end short the cage is
  unreachable for his faction for 24 game hours (`MarkUnreachable`, as the kidnapper's and the town duty's) and he drops the captive (engine).
- **Release**: player and NPC prisoners alike are freed by the release rounds below.
- **Rounds** (`LockSystem.Rounds.cs`, serial; a sweep every 30 ticks starts at most 4, at most 8 run; the worker is
  `Engaged`; each lock takes the owner's 3.3 s): **release** (211): an awake prisoner whose sentence is served is freed by an NPC of the
  cage's faction with goal 211 in its role list and hours (the finder has no booking test, skips an occupant the jailer regards as an enemy, and only that goal frees, **Verified** `FUN_1408cf3c0`; police-like NPCs without it never do),
  driven by each such jailer (`FUN_1408cf3c0`, **Verified**: the first cage of the jailer's town and faction in the 3 x 3 zone cells round it, no radius), who walks there, unlocks and `Release`s in its faction's name; engine: in a cage of a faction that books bounties, no NPC of the faction holds the goal,
  3 failed walks or 120 s, and the system frees it; the cages of non-booking factions wait for a 211 holder. **Lock-up** (155, 197): while
  a member has the goal in its hours a free member with it locks every door of the home (155 only with every member
  inside, engine: within the home's farthest node + 10; neither with a stranger inside; a 197 worker prefers a member already outside and otherwise leaves the home first, then works each door within 45 of its outer point, [locks](game/locks.md)); when no member's goal is in its
  hours, a free member with 86 Opening up in its hours unlocks the home while it counts as locked (87: a round locked it, or every door is locked and whole, as from the level state; squads without 86 stay locked, as the original). The house flag is set at each door the round locks (worker's faction's lock, no door of the home open or opening) and cleared by the unlock round (engine: the round unlocks rather than opens; a loaded night's locked homes come back flagged, `meitou house locked`). **Wardens**
  (195): occupied unlocked cages of the faction in its town are locked. **Keys** (engine): a worker whose target is behind
  its own faction's or home's shut doors unlocks them standing before it walks (the sim doesn't know door positions); an
  arrest's guard walking to such a cage has them opened at once. Rounds and locked homes are hashed only while any exist.
- **Lock orders** (`LockSystem`, context-menu verbs on a caged character): `PickLockOrder` (the prisoner its own cage, a mate
  walks to it; `Lockpicking.Step` each tick in reach, floaters "Pick success!"/"Pick failed"), `GetOutOrder` (refused while
  locked; any other than a kidnap victim books a pending Escaping Prison (refused for a lawless owner) whatever the sentence, and sets `Escaped` unless the cage owner is not real).
  `Imprison` clears `Escaped`; being outside every town does (`ArrestSystem.DropTownStatusOutsideTowns`, as for `Freed`); `Release` does too, unless the cage owner is not real or the releaser is an NPC of a faction that books no bounties.
- **HUD**: `CharacterSnapshot.Prison`; the State line shows the status panel's prison text.
- **Put in Cage / claim** (`PutInCageOrder`, a cage's Put in while a selected character carries someone): the carrier walks to
  that cage, or with none named the best free cage within 1500 (`FindCage`; others' cages reserved), `LetGo` + `Imprison` as an arrest does, then `Claim`
  (11.4 caging tests; the slaver exception never applies): the cage owner's unclaimed entry, halved for a dead captive, is
  paid and marked claimed ("Received c.N reward."). A new order or 3 failed walks end it.
- **Spawn bounties** (`SpawnBounties`, 11.2): rolled for new NPCs (not the player's, not sandbox spawns) under `Spawn` keyed
  by the character id, one fuzz roll for all its factions; no `bounty factions` -> "the empire" (fcs.def), engine: the
  faction named United Cities. Inspecting an NPC shows its bounty in the HUD's bounty panel.
- **Bounty hunters** (engine identification: NPCs of an ADVENTURER faction with a `legal system`, in the base data exactly
  Bounty Hunters): in the spotting sweep they see the player's entry for their faction's booking faction (`legal system`,
  `CrimeFactions.BookingOf`); above 4000 and once recognised (the same meter) the nearest free one and his squad attack
  (`Engaged`), re-ordered while the target is up and within 3000. When it is down, carried, caged or out of reach the hunt
  ends: no cage (11.8), and that hunter leaves that target alone for 600 game seconds (engine). Hunts and cagings are
  hashed only while any exist; restarted after a load ([Saves](#saves)).
- **Bounty desk** (dialogue, [economy 11.4](game/economy.md#114-claiming)): `DA_PAY_BOUNTY` (`ArrestSystem.PayBounty`) pays the carried body's
  unclaimed entry for the speaker's booking faction (halved dead) and marks it claimed; `DA_ARREST_TARGETS_CARRIED_PERSON` (`TakeOver`) runs
  after the line's other effects (data lists them in either order): the carrier lets go and the speaker starts an arrest in the lift
  phase, so it carries the body to a cage as above (none: dropped).
- **Shackles** (`Locks/LockSystem.Shackles.cs`, [locks](game/locks.md)): ARMOUR `is locked` items get a `LockState` (`ItemInstance.Lock`);
  `LockedOn` (effectively locked, worn) refuses moving, unequipping and selling. `ShackleOrder` (verbs Unlock Shackles / Use Tools /
  Use Strength) sends the first eligible worker (the wearer or a mate within 25): pick (`Lockpicking.Step` rules), cut (needs a tool,
  `item function` 9; the best is consumed when hardness reaches 0) or break (strength > 3 x hardness). Another character's shackles: a pending
  code 7 against the wearer's slave owner faction (none for a wearer without an owner). The lock is saved on the item (`lock lvl`, `locked`, `broken`, `hardness`). Shackled characters show the portrait Slave overlay.
- **Spawn prisoners** (`PopulationSystem.MakePrisoners`; facts: [squad factory](game/factions-squads-towns.md#squad-factory)): a housed squad's `prisoners` templates are made
  as squads of the template's faction (else the owner's) and each member is imprisoned in a random free cage of the home building. Engine choices: members beyond the free cages
  are not made; they are held without a sentence (`Kidnapped`: no release round, no bail, getting out is no escape). Observed: Cat (`Dust King Prisoner`) caged in the Dust King
  Tower's watchtower; Tengu's Vault and the Tower of Abuse have no cages in their homes, so none are made. A caged prisoner is no aggro target (engine choice).
- **Slave squads** (`PopulationSystem.MakeSlaves`): a squad's planned `slaves` squads (the factory's role 4) are made at its spot as squads of their template's faction (else the
  owner's), every member a slave of the owner squad's faction, put into random free cages of the home after its prisoners (engine: their "Slave only squad" package's one goal is
  Slave get in Cage, 172; the rest stand by the owner). A slave is police-like by its class like anyone (`FUN_140599600` tests no slave state, **Verified**; every Meitou caller mirrors an original caller of it); an owned slave starts no fights and is no aggro target but for its owner's
  enemies (engine: Heng's Outlaw slaves, uncaged without building data, drew its Samurai into fights). **Observed** (real-data
  probe 2026-10-21): Rebirth's 10 Rebirth Cage Guards squads make 34 caged Rebirth Slaves.

## Kidnapping

`Kidnap/KidnapSystem` (serial `SlowWorld`, right after Carry; needs the population). Facts: [carry-loot 3.2](game/carry-loot.md#32-goals-and-their-target-finders)
(the finder), [campaigns](game/factions-squads-towns.md#campaigns) (which packages). The goal-254 rule, per member, every 15 ticks, while any downed character exists:
- **Duty** (`PopulationSystem.KidnapDuty`): the member's role list in its campaign's package for the current phase (a loaded campaign squad is driven by the campaign, so its phase,
  not the AI, names the package: the retreat of the cannibal-type raids, the cannibal assault), or in its AI squad's running package (cannibals on patrol and at home), in the hour's window.
- **Lift**: the free member (awake, not fighting, fleeing or held, able to carry, not a player character) lifts it with `CarrySystem.BeginPickUp`, then goes back to following its leader or wandering; a marching carrier (a campaign's or a cannibal retreat's) that was idle (a fight just ended) wanders, so the retreat's milling round its own town walks it home (engine choice).
  An AI squad's member starts only when a free cage it may use is among its candidates (the original's "all cages full" stops the goal); a campaign member always does.
- **Carry**: a campaign carrier marches home with its squad (a retreating squad mills round its own town again, not the raid target, engine choice). Within 1.5 x the radius of a town of its faction it looks for a cage (the candidates are `FixtureRegistry.CageCandidates`: the cages of the town it stands in, else of its faction's nearest town within 3953, within 1.1 x that town's radius, plus its squad's home town; the towns' fixtures are registered, no spawn needed) and
  takes the best free one in range (`FindCage`, the original's weights and usable test; other carriers' cages reserved; so Stoat's slavers use its slave shop's United Cities cages while the Cities regard them as no enemy), walks there and within `use range` + 15 lets go and imprisons
  the victim (`Imprison`, `CharacterCold.Kidnapped`). Home with no free cage, three failed walks, or an AI carrier waiting 60 s: it drops the victim, who is then left alone for 2 game hours
  (engine choices; the original's give-up rule is **Unknown**). A carrier that goes down drops its load (Carry). Zones follow a carried player character, so its platoon stays loaded all the way.
- **Prisoner**: `Kidnapped` = held without a sentence: it is never served (no release round), no bail is offered, and getting out is no escape crime. Set only in a cage of the kidnapper's own faction: in another faction's cage (one `canIUseThisBuilding` lets it use, a public barracks') the victim is that faction's prisoner and its goal-211 jailers free it once served, as the original, which has no such hold (#628; a prisoner with a slave state is never served there either, `FUN_1405c9f00`). The cage is locked; rescue is the usual
  lock work, or a lift out of an unlocked cage ([Cages](#cages-beds-and-locks)). Messages for player victims ("is carrying X away", "has been kidnapped by F").
- **Save**: the cage state is the existing `in something` / `in what` (Meitou keys for interior cages), and `meitou kidnapped` ([save](formats/save.md)). Carry links are not saved: a save mid-journey
  leaves the victim down where it was and the raiders (retreating campaign squads) lift it again. The kidnap records themselves are not saved. Hashed only while one runs.
- **Marching home**: a campaign carrier cages in the first own town on the way with a free cage and drops the victim only in its campaign's home town without one (`Abduction.HomeSite`; engine reading of the original's cage finder, which weighs the home x100 but does not require it, [ai-tasks](game/ai-tasks.md)); a member of an AI squad that lists a cannibal retreat package (signals 1, 25) lifts
  without a cage in range and cages the same way, its home its squad's home town, which the retreat package walks the squad to (130 TRAVEL_TO_TARGET_TOWN_FAST as going home at a run, [Captives](#captives)).
- What happens to the captive next: [Captives](#captives).
- **Not built**: kidnapping as a crime for witnesses, a carrier's platoon unloading with an NPC victim (the carry is dropped when the platoon is stored). The victim's own platoon splits it off into a squad of its own when it unloads ([split at unload](game/factions-squads-towns.md)), so a bounty carried away from its town stays on the carrier's shoulder. A split-off member made from a save's platoon leaves that record (`SavedMember.Gone`) and is saved with its new platoon, where it is (#622: kept in the old record it loaded only with its home squad).
- Activation of a player-owned empty platoon (**Verified**, `FUN_1404ffad0`: members are made from the template only when the owner has no player flag): it is removed, not filled (`PopulationRoaming.Activate`).

### Blood smell

`SmellSystem` (serial `SlowWorld`, every 30 ticks; facts: perception): each zone's blood target is its corpses with flies, its
position their centroid ((0, 0) without corpses), an unloaded zone dropped once its target is 0; the value rises 1/60 and falls 1/240 per game second. Corpses get flies 4 game hours after
death while the weather there acts on no one (`BodySystem.Flies`, `CharacterCold.HasFlies`; none in a world without weather). The host draws the effect and plays the sound at them (`CorpseFlies`). An animal squad whose package lists 176 hunts
the zone with the best value (own x1, neighbour x0.8, other x0.5) above its ANIMAL_CHARACTER `smell blood`, keeps it while the value holds, and its members run to the
position. Engine: every loaded zone is a candidate; a hunter stops within 50.

### Town duties

- **273 Remove Intruder**: a free member standing in its home town, the town its occupation holds or a town of its faction lifts the nearest knocked-out character in that town's
  walls (`amInsideTownWalls` > 0: its [gate code](game/pathfinding.md#gate-codes) is a walled region, so an unwalled town removes none; where the codes are unknown, the built-up area: a
  placed town's radius, a player town's farthest building from its centre), not carried or caged (a body in a bed is lifted, `FUN_14099c250`), not of its faction or an ally.
  A body that dies on the way is caged only with **281** listed (else let go). Any free member carrying a body it holds no errand for (a kidnapper's or arrester's victim that died: they end their errand without letting go when 281 is listed; a carry a save restored) with 281 (any body) or 112 (a living one) in its package takes it to a cage the same way (`LookCarriers`, **Verified** `FUN_1405a1140`: no dead test); with no cage it keeps the body. **Observed** (#314 save): a Sho-Battai police guard loaded carrying a raider cages it.
  Carrying it: with **112** (a living body) or **281** (any body) listed and the carried-body desire above 0 (`CageDesire`: 0 for an animal or one the carrier regards as friendly, 0.1 outside a town, 1.4 police with a bounty-free slave, 0.5 dead, 2.0 alive; [ai-tasks](game/ai-tasks.md)), the best free cage in that town (the occupied town's cages are usable), where it is locked up with the usual put-in sentence (108 `Task_PutInSomething`
  sets none without a bounty, [economy 11.5](game/economy.md#115-bail-and-sentence); not `Kidnapped`, **Verified** `FUN_140358670`); else with **274** listed, to the town's exit point (`TownExit`: 200 outward from the first gate outside the walls or wall with an interior, the same for every carrier; no walkability test, **Verified** `FUN_140927120`), put down; else put down. A body put down short of its goal is left alone for 2 game hours.
  A lift whose walk to the body takes over 60 game seconds is given up the same way (engine choice: a body the carrier cannot reach would hold it forever).
- **223 Remove Home Intruder** (checked before 273): a free member of a squad with a home building lifts the nearest knocked-out, carriable character inside that building
  (`SightMap.BuildingOf`), not carried, caged or in a bed, not of its faction or an ally; within its sight stands in for the sensed list. With **224** listed it carries it to the point past the
  home's first door (`AiSystem.LeavePoint`, the same point as Leave Building's) and puts it down; without 224, or with no door known, it goes on as a 273 body (engine choice: the original's
  carrier keeps it). A body that dies on the way is still carried out. Facts: [ai-tasks](game/ai-tasks.md) "Finders of the jobs".
- **243 Getting food**: a member holding no food walks to the nearest store with food in the town (a player town's complete storage, else an unlocked container) and takes one item.
  Meitou's NPCs are fed by their hunger floor, so the food is not eaten.
- **158 / 283 Shooing** (`AI::findStrangersInMyBuilding`; 283 only while the home is private): the first character in the squad's home building that is neither allied nor an
  enemy, not down, caged, its faction's slave or law enforcement, or (an NPC squad) a player character there not sneaking or spotted; the member walks at it and sends event 9
  (17 to an enemy) every 5 s (engine pacing; the dialogue data escalates); after 25 game minutes, once the stranger is getting away, event 60 and done; over once it is out.
- **131 Looting food** (`AI::findSomethingToLootFoodFrom`, shared with 243): a member holding no food in any town (not only its own) walks to the nearest such store, not a
  container of its own faction (engine reading of the own-shop skip), and takes up to 10 units of the first food record it lists (`Task_EmptyMachine` 284 with flag 1, `FUN_14033cd10`, **Verified**: one unit per pass while the store still holds the record and there is room; the record is the first one the store's inventory lists, food or not, **Verified** `FUN_140299990`: distinct records in item order).
- **212 Confiscating goods** (`AI::findPrisonerWithContraband`): in its town, the first occupied cage (list order) whose prisoner's inventory (not its bag) holds an item stolen for it; at the cage it waits 0.8 s and
  confiscates as a police looter does ([NPC looting](#carrying-rescue-and-looting)); engine choice: whatever the runner's class (base data: only law-enforcement packages list 212).
- Occupiers' **36 / 37** town goals walk to a random complete building of the player town (`AiSystem.OccupiedSpot`, engine choice; the original's 36 scatters round the
  occupied town's centre, [town goals](game/ai-tasks.md#town-goals)), 36 drawing again after every walk.
- Engine choices: sight `AggroSystem.OutdoorSight`, arrival 40 (a cage: its `use range` + 15), 4 short walks before giving up; nothing is hashed until one runs.

## Captives

- **Weighed by the AI** (`CaptiveSystem.Offers.cs`, `AiBehaviour.Captive`): the sweep takes a free member's first goal that finds a target, unscored. A member awake but
  not free (resting on a seat the AI gave it: a home chair under 43, a bar seat, a throne; in a job; on another system's duty) keeps, on its sweep turn and when its job
  ends, the captive goals in its hours whose job would start (`TryStart` as a probe: nothing drawn, fetched, said or remembered) with the original's desire (188 and 192
  0.25 x the platoon's slave list, the limb play and chase their own, the rest 1; `AiMind.CaptiveOffers`, hashed, not saved). Its think weighs them with its other goals
  ([precedence](game/ai-tasks.md#precedence): a seated goal's commitment holds its tier, then scores at most 0.05). One that wins gets it up, and the AI's serial step
  starts that goal's job on the state of now (`SendGoalJobs`; none: it chooses again). Sleepers are not offered any (engine choice).
- **The job's goal is the mind's**: a job that starts (the sweep's or the AI's) makes its goal the worker's current AI goal, with a one-step plan and no commitment, as
  the original's chooser holds the goal it runs (`AiSystem.TakeCaptiveGoal`). The goal it ran before ends with its commitment, so the think after the job chooses anew
  rather than resuming it (a workmaster's 43 taken at load would otherwise seat it after its first job).
- **Kill** (135; the desire reads the town and creates its timer first, whatever the worker carries; its plan then draws a weapon, `DrawWeapon`: one in a back or hip slot, else a melee weapon of its inventory put in a free slot, else the goal does not plan): when the kill timer of the town the worker stands in (else its squad's: engine reading) has run out or all the faction's cages there are held; the best occupied cage by the cage finder's weights
  and usable test (`FindCage` in its occupied mode over `CageCandidates`: the worker's town, else its faction's nearest, plus its home's; x100 own faction, x100 home building; another faction's cage only when `CanUse` passes), not a robot. At the cage it holds `chop down static` (`CharacterCold.ActionClip`, which the animation system plays for an idle character) and strikes once per
  clip length at 0.55 of it (`CombatSystem.Execute`: the weapon packet landed through armour, no defence, no crime) until the captive dies. Events 10 (on picking) and 11 (each blow).
- **Eat** (139 always; 282 "Dinner time" at the kill rate): same target, weapon sheathed, `eat` held, `MedicalState.BeingEaten` each tick at lerp(2, 0.4, toughness/100) a second
  (limbs severed unless dismemberment is Never; `DeathCause.Eaten` and "{1} has been eaten alive." when nothing is left). Events 52 and 11 every 10 s (engine throttle).
- **Get in the cage** (172, a Slave AI Goal): a slave not held walks to the nearest free cage of its owner's faction in the 3x3 zone cells round it, in any town, and gets in.
- **Liberate** (276): the nearest cage, in the town the worker stands in (else its squad's), of a faction neither its own nor allied, holding a live occupant that is not its
  enemy. 3 s of `medic` at the cage (engine), then `FixtureRegistry.Release` (no crime is booked for an NPC liberator).
- **Fogman worship** (237 / 236, `CaptiveJobKind.Worship`): in the town nearest it (a standing nest counts, nest markers do not), the first cage whose occupant is being eaten (a running Eat job); within 60 it faces the cage,
  plays `bowing` and barks event 53 every 3-14 game s; ends once the cage is empty or the job grows old (the original runs on while the cage stands).
- **Dogs' limb play** (race goals 277-279): 277 sends an animal with an empty mouth (an adult only when its stomach is empty and hunger at most 2.75) to the nearest limb within
  200, which the pick-up takes into its mouth; 278 (a mouthful, not a player squad's adult over 0.95) sprints in random 1000-1400 legs while it holds it; 279 (any NPC animal, a player
  squad's only under 0.95) runs after the first allied animal in sight with something in its mouth. Engine: the play's first leg is random too.
- **Crop raiding** (232, `CaptiveJobKind.EatCrops`): in a player town whose faction is its enemy (engine reading of the coexist test), a random farm with food; at it, `eat`, crime
  14 each tick, one plant per game second (`ProductionSystem.EatCrop`). NPC towns' farms are not machines in Meitou, so only the player's are eaten.
- **Shout** (188): a worker with slaves of its squad's list up and uncaged says event 38 at a random one, walks to it and stands within 40, following it
  when it moves on; it says the event again every 8 s (engine choice) until its worker speaks (`DialogueSystem.Engages`; the row's B list
  `isDialogDelivered` is read differently, [ai-tasks](game/ai-tasks.md)), and the next sweep picks a slave again with no pause. A worker with no
  event-38 line, or with the event lines off, never speaks: its job then ends only at `CaptiveSettings.GiveUpSeconds` (600 s) or when the slave goes
  (engine choice).
- **Work** (189, 190; `AiBehaviour.Work` in the AI, not a job: it competes by score like any goal): a worker of a town with a mine, research bench, refinery or crafting bench (BuildingFunction 1, 3, 4,
- **Obedient Slave** (187, [facts](game/ai-tasks.md#slave-work-shout-and-capture)): for the AI's slaves the Slave role list of the owner's squad, which slave members already run; a slave in another squad runs the list of `CharacterCold.SlaveMaster` (after a load, the squad of the owner character its `slaver` handle names, [save](formats/save.md)), for its captive jobs too (the captive sweep reads the same list, so a slave whose own package lists Slave get in Cage all day goes to its cage only in its master's hours).
  **A player character** has the job in its list (`Kidnap/ObedientSlave.cs`, `CaptiveSystem`): `FixtureRegistry.SetSlave` (every route to slave state 1: sale, arrest, processing, a cage, a delivery) destroys its jobs and order
  queue and adds the job toward the master squad's leader (`CharacterCold.SlaveMaster`; handing a slave to another master resets it, engine choice), and removes it when the state leaves 1 (freed, escaped, bail).
  While it is a slave and not sneaking, the job is urgent (step 3, `IJobSource.Urgent`): the best goal of the master squad's Slave list in its hour by desire x weight (x1.25 the one running) starts through `TaskStart` --
  **171** follows the owner (`MovementSystem.Follow`, row 171; the follower sets its sneaking from the leader's every update, **Verified**, so a following slave cannot sneak away), **189 / 190** walk to a random workplace of the town
  the slave's squad leader stands in (`AiSystem.WorkAt`, the AI's pick) and stand at it at the work clip (a handler of rows 189 / 190 clears the clip when another task takes the slave), **14** stands. An order beats it (step 1)
  and it beats Self preservation and the other goals. **Gaps**: 172 (the night cage), 41, 143 and 35 are not acted out for a player character; the work produces nothing and takes no hunger rate; the Jobs switch off
  stops it (as any job); `SlaveMaster` is runtime only, so after a load the master comes back from the save's `slaver` handle or the job's saved handle (a character), else the slave obeys nobody; the sense/enemy relations of a slave with the job are not modelled.
- **Eaten**: a meal still awake gains toughness XP (`XpService.Continuous`, amount 10). **Process** (183) re-processes a slave of an allied owner that is not shaven or does not look like one (`CaptiveSystem.LooksLikeASlave`: the watcher's
  faction's slave clothing or garments on torso and legs, locked shackles or a locked torso piece on a shaven head), or an escaped or ex-slave (state 2, 3).
- **Status lines**: a slave or escaped slave selected shows the bounty panel with its status rows ("Slave" / "Escaped slave" and the owner's text, the player-character hints) in its tooltip, ahead of the
  bounty rows (`CharacterSnapshot.Slave`, `PlayerInterface.SlaveRows`; [ui-screens, information panel](game/ui-screens.md#4-hud-mainbargui)). Ex-slave (state 3), "Looks like a slave", "Situation" and "Time remaining" rows are not built.
- **Squad memory**: a caged captive's squad remembers the captor (5, through `FactionDeeds.CagedHook`), a freed prisoner's squad the liberator (3 and TEMP ALLY, event 13), a sold slave's squad the customer
  (5), a bought one's the buyer (3).
- Not built: goal 112 as such (captives are carried by the kidnapping system), short-term tag 6 of the strip, and the real operation of NPC town machines by
  workers: it would need a `Machine` (recipe, input and output inventories, progress) for every level building with a functionality, made when its town registers, and their
  states written into the zone files' GAMESTATE_BUILDING as the original's are; production would also feed the shops' stock, whose origin from machines is **Unknown**. Left out
  for now: workers act out the work (clip, hunger rate) with no output.

## Buildings

Player construction contract (Faithful UI). Facts: [construction](game/buildings-production.md#construction),
[placement](game/buildings-production.md#placement), build menu,
build tasks, [save keys](formats/zones.md#placement-keys). Outposts have no
residents in the original ([the outpost](game/buildings-production.md#placement)) and are on the [map](game/ui-screens.md#map); raids on them: [campaigns](#off-screen-tier). Not built: the door x3 rule ("Wall termites" is disabled in the original).
Damage: melee blows on walls and doors (`CombatSystem.ApplyZoneBuildings`, the original's only damage,
[repair](game/buildings-production.md#work-distance)). Production, power and research: [Production](#production).

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
in the loaded save (generated interiors share the pattern; `Reserve(id)`). The 106 `-zoneX.Y-INGAME` ids of the level data
are not reserved by the simulation (**engine gap**, the host may call `World.Buildings.Reserve`); a collision with a base
placement is the host's to avoid. `World.Research` (`Construction.ResearchState`): completed RESEARCH ids and the
`Unlocked` set (replaced, never changed in place). A new game completes the start-off's `research`, or the default start research
(`ResearchGate.DefaultStartId`, build menu) when it lists none
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
- `Schedule`: a worker walks to the site like a medic to a patient (goal = site XZ, free speed, asked again after a wait
  when the path failed). It has arrived within the [work distance](game/buildings-production.md#work-distance) (`SiteReach`:
  40, walls 60, twice that once stopped, from the footprint hull). A fetcher walks to its source instead (a storage within its
  `use range`; a ground item within `PickUps.PickUpReach`, twice that once stopped; a source that is gone sends it back
  to building). Work ends when the site is gone,
  no longer `NeedsWork` (building), the character is down, carried, carrying or caged, or another task replaced it (a fight
  too: its start ends the work, [task starts](#task-starts)).
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
construction look). An event queue would lose changes across dropped
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

Build mode (B, `Kenshi_BuildWindow`, Faithful UI only): categories and groups list `WorldSnapshot.UnlockedBuildings` in
database order (the original's order is **Unknown**); the window estimates hours as materials / (`build speed mult` x 6),
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
just follows its wall again (original **Unknown**). **Engine gaps**: level (NPC town) walls are not snap candidates;
indoor lines (radius 25), the steep-climb straight extension, the offset clamp and the arrows are not built; the stretch
is recomputed each frame from the signed overshoot. The snapshot carries `WallParent`; picking an attached piece in the
UI picks its wall (name, verbs and Cancel's nothing-delivered check are the wall's).

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
- **Operating.** `OperateOrder` sets `CharacterTask.Operate`. The character walks within the building's `use range` ([work
  distance](game/buildings-production.md#work-distance)) and works it every tick; work is scaled by its skill factor and trains the stat.
  - Producers make output from their inputs. An item furnace burns its grid into Raw Iron while its Active button is on ([item furnace](game/buildings-production.md#item-furnace)).
  - Farms are harvested and cleared.
  - Research benches add to the research queue; crafting benches work their queue.
  - More characters than `max operators` are refused with "X is already in use.".
  - Farms tick whether anyone is there; rain tops up an outdoor farm's water (`FarmTick`, [farming](game/buildings-production.md#farming)). Unloaded zones: the *Production far away* switch (Meitou on: farms, automatic machines and generator fuel
    run everywhere; Faithful off: only in `World.ActiveZones`, and a farm back after over an hour runs `TimeSkip` from its `save time` stamp,
    `FarmState.LiveHours`); facts in [unloaded zones](game/buildings-production.md#unloaded-zones).
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
  "No power" / "Low power".
- **Hauling** (`ProductionSystem.Hauling.cs`, `ProductionSettings.AutoHaul`, default on; facts in
  [buildings-production](game/buildings-production.md#hauling)). An operator at its machine first puts carried items
  into empty input room. It takes a full output only when a storage will accept it (else the machine stays full) and
  carries it to the nearest player storage (function 2 or 13) that accepts it and has room; or it fetches an empty input's item from the nearest storage holding it or machine whose output holds it (a well's water for a farm). A
  storage accepts an item when `itemtype limit` matches and `limit inventory` (a function 2 storage's only) is empty or lists it; `stackable bonus
  mult` scales its stacks. The player's drops into a storage follow the same rule ("does not take").
  Excess fetched inputs stay with the operator. Engine choices: storage within `HaulRange` 3000 of the machine, ties
  by site id, a fetch takes what the inputs have room for; the trip is not saved. A crafting bench's output is its finished items: an operator
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
  - 137 / 138 corpse disposal and 223 / 273 intruders (`ProductionSystem.Errands.cs`, `ErrandJobs`; a carry under way is stepped every `ErrandEveryTicks` 15): a character carrying nothing with 138 and 137 lifts the nearest dead body (a caged one first)
    when a free corpse-disposal machine (function 11) exists and walks it there; in the machine's reach it lets go and the body is removed (items with it). With 223 and 224 (or 273 and 274) it lifts
    a knocked-out, alive character of another faction in the player's building (with doors) it stands in and puts it down just outside the door (273: past the town's farthest building). Engine
    choices: the building test (a footprint round the origin), the friendly exclusion, the reach, 4 walks that end short before the body is given up, and 223's "home" (the building the carrier is in).
  - 234 Turret guard (Shift + quick right click on a turret: 146 becomes 234): mans the turret as an operator; blocked while someone else mans it, and while a melee attacker is within
    40 (146's `!158`). Urgent (step 3, before Self preservation) while the character senses an enemy.
  - 87 Operating machine: blocked while others hold every operator place or the machine has no work (no power; no queued research or craft; a
    farm growing with no water it holds or can get, or ripe with no plant left or no room; an empty input the operator cannot fill from its pack, a storage or a machine's output;
    a full output (a bench's first craft at its store) no storage takes). A farmer at a growing farm with water tends it: it plays the farm's clip (engine reading of task 87, [farming](game/buildings-production.md#farming)).
  - 124 Hauling to (`CharacterTask.Haul`): the nearest player machine within `HaulRange` of the storage whose output (a bench's: its finished items) the storage takes is
    emptied into the hauler's pack and carried there; items the hauler already carries that some machine makes or crafts go first. Blocked with neither.
  - 152 Operating automatic machine (Shift + Auto-Haul on an automatic machine; its companion 124 lands on the same machine): the operator above at the machine, hauling only. Its done list is `!62` (machineHasEmptyInputs false, **Verified**, TaskData row 152): the job is finished, and the list moves on to the 124 companion, as soon as no input of the machine is empty (`ProductionSystem.Blocked`).
    Blocked unless it is mid-trip for it, carries an item an input has room for, an empty input's item is in a storage, or a full or carried output has a storage that takes it
    (engine reading of the row's goal "no empty inputs", `AutoWork`). 124 on an automatic machine runs the same way, blocked unless it carries an item an input has room for.
  - 125 Engineer (ordered as 2 Build or 95 Repair; Shift + quick right click on an unfinished site): builds `ConstructionSystem.EngineerSite`'s
    pick, whatever the job's target ([buildings](#buildings)); its tasks are *Build*, *DeliverMaterials* and *FetchMaterials*. Blocked with no site.
  - Implicit jobs (`ImplicitPick`, `ImplicitJobs`, the decision step's step 5 after the list): with no list job to run, the switch on, not sneaking and not a slave (Sleep and Sit also not while holding position), the squad AI options (`PlayerState.SquadAi`, the
    overview's AI tab, on by default) start, in this order: Ditch items (242: carried ITEMs of function 0 or 15, and food but the first, go to the nearest
    storage within `HaulRange` that takes them, as a Haul trip; only with no queued orders; re-tested every pass, so a trip whose storage no longer takes a carried item, or that gained a queued order, drops out or re-picks at once, **Verified** `FUN_14050e5b0`), Sleep when injured (153: a body part half a point or more below full
    (engine choice: the treated pool can leave a hundredth unhealed, #500) lies in the bed the bed search finds, a `BedSystem` trip (task Sleep, the bed
    id in `OperateSite`), until healed; job lists are then looked at even with no machine; 257 gets up a healed player sleeper with no order and no job
    too, such as one loaded asleep: `GetsUpHealed`), Sit when idle (275: the nearest free player chair or
    throne within `SeatRange` 600). The one running is kept while it holds; any list job preempts it. Distances are from the character, or its
    squad leader while it follows, so it gets up when the leader walks off. Sitters are put on the seat's origin facing its way;
    the medic job counts them as idle.
  - The list is reordered by dragging a row (`MoveJob`).
  - Engine choices: the re-ask period; the injury test, the ranges, a bed's clip (`sleeponfloor`) and a seat's facing (its own, not the table it faces).
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
  GAMESTATE_BUILDING. Items go into an INVENTORY_STATE `<site>-inventory`, listed first by the building, with sections
  `in<k>`, `out` and `main` (a furnace's grid `in1`).
  - A loaded farm's plants all take its growth.
- **UI** (Faithful, `ProductionWindows`).
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
  - Plants are not drawn.

### Prospecting

Facts: [Prospecting](game/ui-screens.md#prospecting), Task_Prospecting. `ProductionSystem` (`ProductionSystem.Prospecting.cs`, `Production/Prospecting.cs`) owns it because it holds the `ResourceField`.
- `ProspectOrder` names one player character (the host picks the selected one nearest the camera pivot). If it is awake, not carried, carrying, caged or swimming (water state 3) it stops (a fight ends, [task starts](#task-starts)) and takes
  `CharacterTask.Prospect` with `CharacterCold.Prospecting` = 0 (hashed only while set, not saved). A character already prospecting is left alone; when done it is `Idle`.
- Each tick (`SlowWorld`, before the early returns) the progress grows by the tick's game seconds. At `ProspectSeconds` = 1 (**Verified**) the task ends (`Idle`) and a `ProspectReport` is published as `World.Prospected`: the
  prospector's name and science (raw stat), the spot (its position), the zone name and the crop coverages, and the five resource values from `ResourceField.Yield` at the spot. Another task (a move, an attack, a stop) or the character going down
  ends it with no report. The report is display only: nothing reads it, the state hash does not see it, a save does not hold it.
- Map: the report carries `ProspectMap`, sampled once at the end (terrain height `IWalkability.TerrainHeight`, the field resources by `ResourceField.FieldYield` per cell with the spot's record, the rocks in range by `NodeYield`); the window colours it per checked row. The report also carries the wind of the spot's region at that moment (`World.Weather.At`, 0 without weather). The window's camera marker is drawn each frame at the camera's
  target (the strategy camera's pivot) and turned by the level direction from the eye to it.

## Trade

Buyback (the original's: economy): a purchase or sale leaves a receipt on the moved units (`TradeReceipt`, the coins paid per
unit, kept through stack splits); moving them back in the same open trade costs or refunds exactly those coins and clears it. Units of the same trade and direction share a stack
(their coins join); they top up plain stacks only when no free cell is left, losing the receipt for those units. Closing or replacing the trade voids every receipt, moved items'
too; stacks it kept apart stay apart. Receipts are in the state hash but not saved, so a save made mid-trade reloads without them, with a different hash.

## Dialogue

- **Effects built**: locks, interrupts, `change relations`, trade, relations, the three attacks, take and give money, faction rank (DA 35, read by DC 10), the surgeon's editor
  (paid by the line, `EditCharacter.Paid`), assign and clear bounty, lock this dialogue, DA 37 / 38 (a campaign's phase), `trigger campaign` (`PopulationSystem.TriggerFromDialogue`: the rules in
  [campaigns](game/factions-squads-towns.md#campaigns); `world state` conditions read the live states), recruiting (DA 3 / 18, `DialogueSystem.Recruit`), crowd
  triggers, `give item`, `change AI` and `AI contract` (`Ai.ChangeAi`, `Ai.StartContract` with the target: `Blackboard.MissionTarget`, followed by goals aimed at the
  squad mission through `AiMind.FollowTarget`; on its end the leader's `contract end talk passive`), DA 25 / 53 (`FixtureRegistry.TagEscaped` / `FreeSlave`) and DA 29
  (`ArrestSystem.ArrestFromDialogue`), DA 43 (the bail list, [Arrest and jail](#arrest-and-jail)), DA 7, 13, 14, 19-21, 23, 28, 33, 34, 52, 54, 56 (`DialogueSystem.Acts`), DA 16 (`TalkFollowers`); the rest is listed in dialogue.
- **Recruiting** ([facts](game/dialogue.md#recruiting)): at the `max faction size` cap the line "It looks like your squad is a little full." is
  posted and the conversation ends; otherwise the speaker leaves its squad (its leader passes to the first member left) and joins the player's
  active squad (the first selected player character's, else `Player.Squad`, else the talker's), or a new "blank squad" squad when that one has
  `max squad size` members: player faction, role Squad1, NPC class Adventurer (as every player-faction character, `CharacterAssembly`), player
  default orders, speed mode Free (2), no AI mind, stopped, its save link dropped (a save writes it as a new member of the player's platoon).
  Its dialogue becomes the player set (`DialogueDatabase.SetFor`). DA 3 ends the conversation and opens the editor in recruit mode
  (`EditKind.Recruit`: free, race and gender kept). Every piece a recruit carries loses its `uniform` faction (`ItemInstance.Uniform`, as the original: [recruiting](game/dialogue.md#recruiting)).
- **Conditions**: most DCs are computed (slavery: 59, 61, 62, 64; faction trust 3; tech level 12; town levels 73, 74); those needing state Meitou lacks (AI flags, strength, perception counters, walls,
  training equipment) compute "no"; DC 69 HAS_ILLEGAL_ITEM (the gate guards' inspections) checks the culture's `illegal goods` ([dialogue](game/dialogue.md#conditions)), posting each find (not on the Talk menu's dry run, once per carrier and item per game hour: engine choice). IN_MY_BUILDING uses the squad's home building and the lock rounds' extent; IS_INDOORS the sight map's building.
- **Squad memory** (`SquadMemory`, `DialogueSystem.Memory`; [facts](game/dialogue.md#squad-memory)): per squad and character a time and long / short tags, the player faction
  sharing one long-term entry; DC 18, 20, 23 and 52-55 and the aggro scan (TEMP ENEMY / ALLY) read it, the effects and event-1 ends write it; it stays with a squad's platoon while the squad is out of the world and is saved on the platoon (`MemorySave`) (engine choices).
- **Speech bubbles**: `WorldSnapshot.Speech` lists the lines on show (`SpeechBubble`: speaker, text, large font); `PlayerInterface.AddSpeech` draws them over the head. Sources and engine choices: dialogue.
- **Personality**: the save's `personality` when above 0, else rolled as the original does from the character's (else its faction's) PERSONALITY record, with Meitou's own random draw (`Personality`, [facts](game/dialogue.md#enums)).
- **Engine choices**: a childless NPC line shows `[Leave]`; a reply leading to another reply offers it alone; number keys pick choices. A choice whose picked text is empty and that has no cost is not offered (**Verified**, `FUN_140680130` list loop); with none left the original ends the conversation, Meitou keeps `[Leave]` for the first offer.
- **State**: last use per line, permanent chance rolls, event counters, squad memory, interrupts and the open conversation are hashed, not
  saved (as the original, except the squad memory, which it saves on the platoon; the faction global trust and war flags it saves under `meitou` keys, [save](formats/save.md#faction-trust-war-flags-bed-rent-and-turrets)). Locks are on `CharacterCold.DialogueLocks`, hashed while set, saved as `lok*` (`DialogueSave`).

## AI

`AiSystem`, blackboards/minds, package/task data: slot/class/time/weight selection, homes/beds/nodes,
patrol/bar/shop/sleep/retreat/contracts, combat stances and off-screen roaming jobs. Data and
TaskData semantics: [ai.md](game/ai.md), [ai-tasks.md](game/ai-tasks.md). Wars and prisoners are incomplete; residents
off screen only come back where their schedule puts them ([off-screen tier](#off-screen-tier)). Hash blackboards/minds/jobs when present.

### Package classes

`PackageClasses.Of` maps the `signal func` ([ai.md](game/ai.md#package-classes)); `Blackboard` runs the original's slot walk with the class rules of `AiSystem`
(`IPackageRules`: start, finished, started, ended, may lead). Built: plain, retreat, patrol (route an engine choice), bar, contracts, and
(`AiSystem.Packages.cs`) the wandering trader (16), passing-by assault (29), occupation (30) and refugee (22: a slave-led squad of fewer than 3 flees to the nearest other town of its faction, a broken one joins a healthy squad there, `SquadManagement.Merge`) as decoded. Not built (never start but as a lone slot-0
package): the war classes (19, 20, 32) and 31, which run only in campaigns; the campaign drives its platoons itself.
- **Current town** (`Blackboard.InTown`, on the package cadence before the choice): nearest town by X/Z, placed towns by `TownData.Radius`, player towns by their
  own (`PlayerTown.Radius`); entering one fires the leader's ENTER_TOWN; the town a squad starts in counts as entered (engine choice: no line for every squad a town makes).

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
- **Not built**: a player outpost is no `Blackboard.Town`, so its turrets, machines and nodes are not goal targets (the player's jobs run them);
  a work building; the contract's mission target over a save (as NPC platoons).

### Planner

`TaskPlanner` runs the original's search ([ai-tasks](game/ai-tasks.md#planner)) over `TaskDataTable` (all 253 rows) for the
goals the engine acts out. The think orders each tier's goals by score, leaves out one whose list A holds, and takes the first
whose plan succeeds (`AiSystem.Choose`); `AiMind.Plan` holds it leaf first and `AiMind.Step` (hashed) its first step. The goal's
act-out does the walk and the goal's own action; steps of their own are getting out of bed, sheathing and drawing a weapon (one think each,
then dropped from the plan, so a plan a committed goal keeps goes on to its next step instead of repeating it), going home (19, the GoHome walk),
entering its home building (156, the same walk), leaving it (200, to A + (A − B) × 1.1 of the home's first door,
the outer point taken as the side away from the building's origin, until outside) and getting near a target (40, a walk to within 10). A plan
whose first step is neither the goal nor a step the engine carries out (`AiSystem.Carried`) fails. Engine choices:
- Each character plans with its `Repertoire.Of` category (4, 0x40 crippled or staying low, 0x10, 1, 0x20 animal; so a crippled NPC has no stand-up row and crawls away under threat). Restricted rows (lockpicking, cutting, opening gates) are open to every character whose faction is not the player's, as in the original ([planner](game/ai-tasks.md#planner); the orders-step flag is not planned here). With the held states below, none is ever needed on the way (their states 90, !54, !114 hold), so this changes no plan today; `IPlanWorld.Squadless` keeps its old name for it. A row without a finder wants its effect for its own target. At most 64 nodes per search.
- A node is made once per row and target (the original allows four copies), so the ancestor-repeat test never fires; a child already
  closed is skipped rather than accepted from the node.
- Only the step being carried out counts as queued, and only while it can run (Meitou's steps do not end on their own).
- Finders: the spots, beds, seats, turrets, towns and (rows 113 / 114 / 242) the ditch storage (ditching) the act-outs already pick, at the floor point the walk ends on (`PlanContext.Find`); other finders find nothing,
  so such goals do not plan. Rows on the way have their `TaskProfiles` desire, else 1. The exception is DEFEAT_SQUAD 101, whose plan is the goal alone: the finder's
  pick (`AiSystem.AssaultTargetFor`, [ai-tasks](game/ai-tasks.md#combat-goals)) is sent as an `AttackOrder` by the serial step and the combat system fights it.
  SELF_PRESERVATION 32 is the other exception: its plan is `[35, 32]` when `SelfPreservation.Runs` (a crippled NPC, or one with no working arm, under a threat), else `[13, 32]` on its own attacker (row 13 on itself, `AiSystem.SelfDefencePick`, started as the combat goals' fights below), else none;
  35 is acted out by `FleeSystem` on the AI's request ([ai-tasks](game/ai-tasks.md#combat-goals)). State 26 is wired (staying low, playing dead, in a bed; not crippled).
- States (`PlanContext.Holds`): location, distance, bed, sitting, home (within 20 of the home), home town, town, weapon drawn, weapon carried
  (`hasWeapon`), carrying, a first aid kit carried (23), the immediate threat (39, `CombatSystem.ImmediateThreat`), deep water (139), a leg down that could be fixed (103, `MedicalState.LegsMessedUp`), inside the home building (107, `AI::iAmInsideTargetBuilding` for a non-building target) and strangers in a private home (141, so a resident does not
  go to bed with one inside; [stranger states](game/ai-tasks.md#stranger-states)) are evaluated; moving, getting up, arms, power, buildings, doors, gates, accessibility, machine operator slots and readiness are
  held true (movement and the lock-up round deal with doors; the bashers' `DoorPlan` evaluates them); machine empty inputs, output full and jammed (62 to 64) are evaluated for a `Targets.Machine` target (a player's
  machine; no finder gives one yet, the town workers' buildings are level data, [Blocked output](game/buildings-production.md#blocked-output)); every other state is false.

**Campaign phase package** (**Verified**, `FUN_140272570`, `FUN_140272240`, `FUN_14026a110`): on each visit a loaded campaign platoon whose war-manager record gives a package (pt1, pt2 or retreat; the leader platoon's `... leader`, the others' `... others` falling back to it, `CampaignData.PackageOf`) holds that package as the blackboard's override when it has none or another, for 24 game hours (`AiSystem.InstallPhasePackage` = `Blackboard.StartContract`, saved as `contractjob` / `jobhr`); when the 24 h run out the override ends, the template slots choose on that visit and the same package is installed afresh on the next. `PopulationCampaigns.StepCampaignLoaded` still orders the march; the war classes 19 / 20 / 32 (own start and finish tests, out of the campaign) are not built, so only the timed behaviour of the override is modelled. The campaign sets the squad's war targets, so `StepPackage` skips a campaign squad.

Turret goals (149, 222, 234, 146) man a turret of the squad's town: a placed BUILDING with a
`BUILDING_FUNCTIONALITY` function 12 (`TownBuilding.Turret`: its `use range` and `gun turret`
GUN_DATA). In member order each slot claims the nearest free turret and walks to a spot behind its
gun, facing its way; the home-building goal (222) takes only a turret mounted on its home (the
state's `mounted` handle). Claim order and gunner spot are engine choices. The goals are done while
no enemy is near (list A `isNoEnemiesInVicinity`, [combat.md](game/combat.md#turrets)): the planner holds it false while an enemy
(the relations) is within `AiSystem.EnemyVicinity` 1000 (engine choice: the original's radius is Unknown), so guards man the turrets only
while enemies come. A guard at its turret fires it ([Ranged](#ranged)).

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

`Meitou.Data.Save` parses/writes folders; `Meitou.Simulation.Saving` maps world state. Capture
serially at a boundary, write/read on workers; `ReplaceWorld` publishes a successfully loaded
world and preserves the running world on failure. [save.md](formats/save.md) owns file keys,
handles and original saved/derived/regenerated state.

Code map:
- **Host hooks.** `SaveCaptureOptions` carries what only the host knows: `Bodies`, `View` (the strategy camera). `GameHost.CaptureOptions`
  fills them in. `GameHost.SwapInSave` applies host-side state after `SaveLoader.Load`: the camera and the weather.
- **Load.** `SaveLoader.Load` makes characters before the first tick. The character table copies its previous state over the next one at
  `BeginTick`, so state that must reach a tick is not written into `Next` there. It goes through `World.Commands` like player input.

### Quicksave and quickload

**Crime and jail** (keys in [save.md](formats/save.md#locks-cages-and-sentences)). Saved: bounty ledger (`bountyexp` written with a minute and a tick of slack over the minute-floored
save clock, so an entry does not lose an hour per save and load; `BountySave`), held state and sentence, the escaped flag, the recently-wanted
and recognition timers (player platoon characters, written only while set), the locks of interior cages and containers (an imprisoned character stays locked in), and the homes a
lock-up round locked (without the flag a loaded night's locks would never be undone). **Restarted, not saved** (engine choice; the guard, hunter, releaser and warden of each job is a
town NPC the population makes again with a new id, so a saved link would dangle): arrests (a downed target lies where it fell, is spotted again once awake, the saved markers make
that quick), cagings (the carry link is not saved: give the cage's Put in again), hunts and their rest timers, release, lock-up and warden rounds (the 30-tick sweep finds
them again), the unreachable-cage memo (relearned by a failed walk), lock picking (an order), pending crimes
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

## Town house purchases

`BuyBuilding` (the panel's `For sale:` price, then the original's confirmation; [original rules](game/ui-screens.md#selected-building))
rechecks the sale flag, town kind, owner and its hostility, the quoted price and the money, then takes the money once.
Sellable: function 19, or no function and interior meshes; no walls, gates or `is inside building` (the original's class and
parent tests are not fully decoded). Ruined houses register without fixtures. The bought house's doors, own lock and
indoor build permission become the player's, every door is opened (`FUN_140297040`, **Verified**), NPC lock rounds on it stop, and it turns private. As the original's purchase
([what it does](game/ui-screens.md#selected-building)) the house is emptied: its interior layout's fixtures (cages, containers with
their contents, beds, dummies) and seats go, occupants let out first; its loose-item slots go with the items lying in them; the
renderer, navmesh and lamps lay it out without its interior layout (`BuildingPlacement.WithoutInterior`: `FixtureRegistry.PublishedEmptied`,
or a bought ruin's site placement). Every squad living in it (engine reading: the original takes the owner faction's, whose town
housed them) moves to a random other house of its town that is for sale (by the original's finder), else has none; its members plan
again. Their town never houses residents in a building the player owns (`ResidentHomes.Assign`, `AiSystem.AssignHome`), so nobody
lives there to call the buyer a trespasser. Saved as `owner faction ID` with
`sell home` false; a loaded player owner that is not the level's counts as bought (saved again), is applied before the fixtures are
made and empties the house again (a container's contents from an older save are dropped at its place); a squad of an older save living
in it moves out when its AI attaches. Not built: the sound emitters of interior furniture (ovens; `WorldObjects.Emitters`, made once from
the level data) stay.

## Open questions and known gaps

**Engine gaps** are listed where they apply, not here: [unique characters](#unique-characters) and [campaigns](#off-screen-tier),
[ranged](#ranged) (bolts before a building's collision loads), [locks](#cages-beds-and-locks),
[slavery](#arrest-and-jail), [kidnapping](#kidnapping), [buildings](#buildings) and walls, [production](#production) (turrets,
plants), [prospecting](#prospecting), [dialogue](#dialogue) (conditions on state Meitou lacks), [AI](#ai) (wars, prisoners, planner finders).
Smuggling and stealing: [trade](#trade). UI: [faithful-ui-parity](faithful-ui-parity.md).

- Navmesh: the cache key does not see a changed collision file; a running build is not stopped on unload (it finishes into the cache).
