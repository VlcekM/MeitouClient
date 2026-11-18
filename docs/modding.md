# Code mods (C#)

**Status: implemented, code-mod API version 2 (1 still runs; 2 adds [dependencies](modding-dependencies.md)).** Meitou's own in-process loader,
Lib.Harmony shipped by the game as the sanctioned patching library, and a registration surface kept to the cases where a patch would silently
break an invariant. No BepInEx.
RE_Kenshi plugin DLLs are never loaded: wishlist plugins get built in, or ported to this API as workshop mods of their own ([rekenshi](formats/rekenshi.md)). Code:
`src/Meitou.Game/Mods` (contract and loader), `GameMods` (host wiring), `SimulationExtensions` (engine hooks).

## Why this shape

- **Not BepInEx.** Its chainloader discovers plugins from its own folder, not `mods.cfg`, so code mods would not enable, disable or order
  with their data mod. It injects via Doorstop (proxy DLL on Windows, `LD_PRELOAD`/`DYLD_INSERT_LIBRARIES` on Unix), which fights the
  launcher and the signed macOS build when the host is ours. **Observed**
  ([BepInEx.Utility.IL2CPP r2](https://github.com/BepInEx/BepInEx.Utility.IL2CPP/releases/tag/r2), 2026-06): BepInEx 6 had no stable
  release; downstream projects built on `be.*` builds.
- **Harmony is not an alternative to an API.** It works on the game's JIT publish whether blessed or not, so the decision is only what the
  engine promises beyond raw patching. **Observed** (2026-11-11, scratch app, win-x64, tiered compilation off): Lib.Harmony 2.4.3
  throws `PlatformNotSupportedException` ("CoreCLR version 11.0.0 is not supported") on .NET 11; 3.0.0-rc.5 patches (prefix, postfix,
  unpatch), and code compiled against 2.4.3 runs unchanged on the 3.0.0-rc.5 `0Harmony.dll`.
- **No accessor mirrors.** Mods reference `Meitou.*` assemblies directly, as a patch needs the types anyway. Compatibility is per game
  version (RimWorld's model).

## Writing a mod

A class library matching the release's runtime: `net10.0` for legacy macOS, `net11.0` for standard builds on every platform
(`net10.0` everywhere before the .NET 11 move, 2026-11-11), referencing that platform release's `MeitouClient.dll`
(the game: the mod contract `Meitou.Modding`, the interface `Meitou.Game.Ui`), `Meitou.Simulation.dll`, `Meitou.Core.dll`,
`Meitou.Data.dll` and `0Harmony.dll`, all with `Private="false"`: the game supplies them. Releases after v0.17.1 ship these four Meitou
assemblies, not the earlier eleven, and most namespaces follow folders (`Meitou.Simulation.Population`, `Meitou.Game.Rendering.Terrain`):
a code mod built against v0.17.1 or earlier must be rebuilt.

```csharp
public sealed class MyMod : IMod
{
    public void Initialize(ModContext c)
    {
        c.AddSystem("counter", ctx => new CounterSystem(), SystemAnchor.AfterPart(StandardParts.Combat));
        c.AddDialogueCondition(1000, check => check.Matches(/* computed answer */ true));
        c.Harmony.PatchAll(typeof(MyMod).Assembly);
    }
}
```

Folder: `mods/MyMod/MyMod.mod` (the data mod, may be empty of records), `meitou.json`, `MyMod.dll`.

## Discovery

- A mod carries code when its folder has `meitou.json`: `{ "id", "apiVersion", "assemblies": ["Foo.dll"], "entry": "Foo.FooMod" }`
  (`CodeModManifest`). `id`: letters, digits, `. _ -`, at most 64; it keys the mod's save data, Harmony instance and log lines.
  `assemblies`: DLL file names in the mod folder, loaded in order. `entry`: the full name of the `IMod` type. Comments and trailing
  commas are allowed. API 2 adds optional `version`, `dependencies`, `loadAfter`, `incompatible` and `gameVersion`
  ([dependencies](modding-dependencies.md)).
- `ModInfo.HasCode` reports it beside `HasNativePlugin`; the launcher marks such mods.
- Load set: the enabled mods of `mods.cfg` in order, filtered to those with a manifest. A disabled mod's DLL is never opened. Changes take
  effect at the next launch, like data mods.
- A code mod whose feature Meitou has built in (`BuiltInMods`, [rekenshi](formats/rekenshi.md): the `player-only-xp` port) is left out
  of the load set and counted as disabled.

## Trust

A workshop DLL is arbitrary code running as the user, as with RE_Kenshi. No sandboxing: .NET has none. Installing and enabling a code mod
is the consent, as in RimWorld: the game loads every enabled code mod's code, with no allow list or question. An older game's
`trustedCodeMods` in `meitou.user.json` is no longer read.

## Loading

- One non-collectible `AssemblyLoadContext` per mod (`ModLoadContext`), loaded once at startup; no unload or hot reload.
- Resolution by simple name: an assembly on the game's trusted platform list (`Meitou.*`, the framework, Bepu, `0Harmony`) comes from
  the game even when the mod folder has a copy; anything else comes from the mod folder, so
  two mods may ship different versions of a library. Listing a game assembly in `assemblies` refuses the mod. Native libraries resolve from the mod folder first.
- Between the two: an assembly a declared dependency exports comes from that dependency's context, so a framework loads once
  ([dependencies](modding-dependencies.md#assembly-resolution)).
- **One Harmony, loaded by the game.** **Observed** ([pardeike/Harmony #741](https://github.com/pardeike/Harmony/issues/741), open since
  2025-10): outside Unity, Harmony loaded in several load contexts duplicates `HarmonySharedState`, and two mods can no longer patch the
  same method. The game references Lib.Harmony 3.0.0-rc.5 (`0Harmony.dll` with MonoMod merged in) and the resolution rule hands every mod
  that copy.
- Refusals, each a log line and a notice, the game going on: not allowed, unreadable manifest, other `apiVersion`, an id another mod
  took, a missing DLL, an entry type that is absent, not an `IMod` or without a public parameterless constructor, a throwing
  `Initialize`.
- Constraint: the game stays a JIT publish, never `PublishTrimmed`, `PublishAot` or single-file. **Verified**
  (`tools/scripts/package-release.ps1`): the game publishes self-contained with `MeitouClient.dll` (holding `Meitou.Modding`) and the
  other `Meitou.*.dll` beside the executable, and `meitou-tools` with it. **Observed** (2026-11-04, win-x64 publish of the three programs
  into one folder, before `Meitou.Modding.dll` was merged into the game): `0Harmony.dll` and `Meitou.Modding.dll` were there, the game's 228 files are byte-identical to a game-only publish, and the published `meitou-tools.exe mod-check` runs. Since the four-project layout
  (2026-11-08) the viewer and `meitou-tools` reference the game and bring copies of its runtimeconfig and deps, so the game publishes last;
  **Observed** (win-x64, tools then game into one folder): `MeitouClient.runtimeconfig.json` is the game's own.

## Lifecycle

- `IMod.Initialize(ModContext)` runs once per session on the boot worker (`BootSimulation`), after the data load and mod merge and before
  the first world's systems, in `mods.cfg` order. Registering is only allowed inside it. A throw drops the mod's registrations and
  removes its Harmony patches (`UnpatchAll` of its id).
- Worlds are built again for every new game and load, so systems are registered as factories and made per world.
- `ModContext`: `Id`, `Name`, `Folder`, `Manifest`, the merged `Data` (`GameDatabase`), `Population` (`PopulationData`), `Log`/`LogError`,
  `RngPurpose`, `Harmony`, `Tick` and `Enqueue(SimCommand)` for the running world (dropped and logged before the first world),
  `Dependency<T>(id)` and `HasDependency(id)` for declared dependencies ([dependencies](modding-dependencies.md#using-a-dependency)).
- `RngPurpose` is `StableHash(id) | 0x80000000` cast to `RngPurpose`, so it never equals a vanilla purpose.

## Registration surface

Only where a Harmony patch would break an invariant without an error; everything else is Harmony. A new point is added when a patch
in the wild keeps breaking, not ahead of demand. Mod codes start at `SimulationExtensions.FirstModCode` (1000); a code another mod
holds refuses the second mod.

- **Tick systems:** `AddSystem(name, factory, anchor)`. `SystemAnchor.Before(part)` goes before the first system of that
  `StandardParts` member, `AfterPart(part)` after its last, `End` after all; a part the world does not build puts the system at the
  end; systems at one place keep registration order. `StandardSystems.Build` builds the standard list first, then splices
  (`Splice`), so the list without mods is the standard one. Captives and beds have no anchor of their own. A factory gets
  `SystemBuildContext` (`Data`, the standard systems, `Find<T>()`); one that throws or returns null leaves its system out of that world
  and logs. A phase of a mod's system that throws (a bug, or a game method the mod was built against that has since changed) is caught and
  logged once with the mod id, the system's name and the phase, and the player gets a notice; the system's tick phases (paused orders'
  `Inputs` too) stop from the next serial step, and the fault enters the state hash (`ModSystemFaults`). A throw in a parallel phase lets
  that phase's other partitions finish, so that tick's partial work depends on the thread count, and none of the system's effects of that
  tick are applied. The system's other hooks (observers, filters, task and decision sources) keep their own rules. `IStateHashed` puts a
  system's state into `World.StateHash`. A mod system implementing a source interface `World` looks
  up by first match (`IAnimationSource`, `IProjectileSource`...) and placed before the standard one would replace it.
- **Resolved combat:** a registered system implementing `ICombatHitObserver` receives `HitResolved(World, in CombatHit)` once per
  landed melee/projectile contact and execution blow on a captive (`CombatSystem.Execute`, `CombatHitSource.Execution`), after its
  ordinary medical, XP, reaction, crime and log work. A fully absorbed contact counts; blocks, dodges, misses and a contact that selects
  no medical part do not. The value payload has delivered damage, armour absorption, selected medical-part index, original
  attacker/victim generations, attack/projectile sequence, approach and pre-hit life/incapacitation flags. Positions are the victim's root, not a measured surface
  collision; pre-damage part health is not supplied. Callbacks run serially in system order, may read the world and change their own
  hashed state, and queue later commands for gameplay changes; they must not mutate world state or call combat recursively. A
  throwing callback is logged (the mod id and the system's registered name) and disabled for that world; others continue. Melee callbacks run in Commit, projectile callbacks in
  `CombatSystem.Schedule` (`ProjectileFlight.Fly`), both before every `SlowWorld` of the tick; execution callbacks run in the captives'
  `SlowWorld`, which has no anchor, so a system draining its own hit queue in `SlowWorld` sees them the same tick only at
  `SystemAnchor.End`, else the next tick. Faulted registrations enter the state hash. Without observers or a hit feed, no payload or event
  queue is made; either way no additional rolls occur (the pre-hit flags are reads).
- **Hit feed:** `AddCombatHitFeed(view)`, for display (blood, gore, hit markers) without racing the tick. Once a frame on the host's thread,
  after the blood and before the frame is drawn (Vulkan and Metal), `view` gets the hits of the ticks since the last frame, oldest first, the
  same payloads observers get; a frame without hits does not call it. With a feed registered the host gives each world a display-only queue
  (`World.CombatHitFeed`, like `BloodMarks`) that the simulation never reads, so it cannot change a run; a new world (load, new game) gets a
  new queue, dropping the old world's hits. A tick may be in flight, so `view` must not touch the world; the list is reused, so copy what you
  keep. A throw is logged with the mod id and that feed gets no more hits for the session.
- **Hits before they land:** a registered system implementing `ICombatHitFilter` receives `HitLanding(World, ref PendingHit)` for each melee
  blow about to hit (after the defender's block and dodge are decided), bolt about to strike a body and execution blow on a captive, before
  armour and toughness: the tick, the `CombatHitSource`, attacker and victim, the sequence and blow as in `CombatHit`, whether the victim
  was the chosen target, and the `DamagePacket` as it leaves the weapon (cut, blunt, pierce, stun, bleed multiplier, armour penetration).
  The filter may change `Packet` (a changed value finite and, but for armour penetration, not negative) or set `Cancelled`; nothing else it
  changes is taken back. A cancelled melee blow is a miss in every respect, a cancelled bolt flies on past the victim as past a friend, and
  a cancelled execution blow does no damage: the captive jobs that strike it keep striking, and the kill job's scream still plays, until one
  lands. A melee blow's "hit dealt" experience follows the filtered packet. Filters run in system order, each seeing the hit as the earlier
  ones left it, until one cancels. They run serially where the hit lands (melee in Commit, bolts in `CombatSystem.Schedule`, executions in
  the captives' `SlowWorld`), may read the world and change their own hashed state, must not change characters or other world state or call
  combat, and must answer from the world and the hit alone. The one-punch debug cheat ignores the packet but keeps a cancel. Hits on doors
  and walls are not filtered. A filter that throws or breaks the packet's rule is undone for that hit, logged and disabled for that world;
  faulted registrations enter the state hash. Without a filter no payload is made.
- **Trade prices:** a registered system implementing `ITradePriceFilter` (`Meitou.Simulation.Trading`) is asked
  `Price(World, in TradeDeal, long price)` for every price the game asks: the trade window's stack and unit prices and the coins it takes,
  and an NPC shopper's purchase from a building's stock. The deal names the tick, the item record, the units, whether the customer sells,
  the customer (the player's character in the window, or the NPC shopper) and the shop's squad (-1 none); the filter returns the price to
  ask, so a mod gives a town's own faction a discount or marks prices up. Filters run in system order (mods in load order), each seeing the
  price the earlier ones left. A buy-back (undoing a trade of the open window) keeps its coins and is not asked. A filter must be a pure
  read, since the same price is asked again for the window and the payment; one that throws or answers a negative price is undone for that
  price and disabled for that world, and the fault enters the state hash. Without a filter nothing is built. Engine API, not in the original.
- **World ready:** a registered system implementing `IWorldReady` receives `Ready(World, bool fromSave)` once, between ticks before the
  first: after a save has loaded (`IModSaveData.Read` done and settings of mods no longer running dropped; an import counts as a save) or a
  new game has placed the player's characters (`fromSave` false). Systems are told in system order (mods in load order); one may change
  the world as a serial phase does, so a mod sets its unsaved runtime settings (a held platoon, a sleeping town) back on here, also for a
  save made without its own saved state. One that throws is logged and the others are still told. Engine API, not in the original.
- **Goods taken:** a registered system implementing `IGoodsTakenObserver` (`Meitou.Simulation.Crime`) receives
  `GoodsTaken(World, in GoodsTaken)` when one of the player's characters takes goods that are not its own by hand: a held item dragged or
  given out of an owned container or another character, a click take out of an owned container or a town machine, a right-click loot take off
  a body (not the take-all loot, NPCs' own looting or money). The report names the tick,
  the taker, the owning faction (-1 none), the character taken from (none for a container or machine), the site ("" for a character), the
  item record, the units and whether the taker was caught (the goods stayed). It includes takes the game makes no crime of, such as the
  player's own characters taking from a container of the player's faction, so a mod can judge them by its own law. Observers are called in
  system order once the goods have moved (the units that moved) or the taker was caught; they may read the world and change their own state,
  and gameplay changes go through commands.
  One that throws is disabled for that world and the fault enters the state hash. Without an observer nothing is built. Engine API, not in
  the original.
- **Items used up:** a registered system implementing `IItemUseObserver` receives `ItemUsedUp(World, in ItemUsed)` inside the step that
  used an item, once the item has changed: the tick, the `ItemUseKind`, the item record's string id, the user (none for a machine, a
  research order or an upgrade pulling from the building's own stock), the building it went into or came from (a construction, machine or
  research bench site, a cut fixture; else null), the amount (charges for `Eaten`, `FirstAid` and `Ammunition`, where one bolt shot is one
  charge; else units) and whether a whole unit left the inventory or storage. The kinds: food bites (each bite, the eater's own, its
  backpack's or a squad mate's backpack's food; a limb counts as the bite it gave), first-aid, robot and splint kit charges spent, crossbow
  shots, building materials delivered or pulled from the building itself (a later dismantle's refund is not reported), tools broken
  cutting locks or shackles, maps read, blueprints learned, machine inputs (production, crafting, generator fuel, farm water, furnace
  smelting), research costs, and the units of the clicked stack a mod's item use took out of the user's inventory, whatever the handler
  did with them (charges or other items it spends are not seen). Every use runs in a serial step; callbacks run in system order, may read
  the world and change their own hashed state, and must not change items, characters or other world state, since the step may still be
  walking them: gameplay changes go through commands. Without an observer no payload is made. A throwing callback is logged and disabled
  for that world; faulted registrations enter the state hash.
- **Relation changes:** a registered system implementing `IRelationChangeObserver` receives `RelationChanged(World, in RelationChange)`
  once per change of a faction relation, after the change and its threshold message: the two factions (the player's row comes as the other
  faction's view of the player), the value before and after, and a `RelationCause`: its type (a deed, a witnessed crime, a captive sold or a
  slave bought free, dialogue, war declared or ended, a debug command), the deed's kind, the acting character (none for a debug command) and
  the deed's victim. One killing blow reports each of its events (killed, hit, knockout, killing blow); first aid reports each update. An
  ended war counts when its flag clears even if the value does not. Loads, a new game's start values, global trust, rank and direct
  `FactionRelations` writes are not reported. Callbacks run serially in system order inside the serial step that made the change; they may
  read the world, change their own hashed state and change relations (a change made through `PlayerRelations` during a callback is not
  reported again), and must not change characters or other world state. A throwing callback is logged and disabled for that world, as for
  combat; faulted registrations enter the state hash.
- **Faction list:** `ArrangeFactionList(arrange)` arranges the Overview's Faction tab: `arrange` gets the rows the tab would show
  (`FactionListRow`: faction, name, the player's relation), sorted by name, and returns the rows to show, in order. It can reorder, rename
  (names go through the translation catalog like the tab's own), leave out, or add headings (faction -1: the name alone, no number or bar),
  for example to group factions under another. It runs on the host's thread whenever the tab is drawn, so it must not touch the world; mods chain in
  load order. A throw or a null is logged and that arranger is not called again.
- **Combat moves:** `AddCombatMove(source)`: `source(world, id, tick)` answers a `CombatMove` (a `CombatTechnique` and its progress, 0 to
  1) or null, and the animation plays the first answer, in load order, as the character's technique (`CombatSystem.ModMove`). It is asked
  only when the game plays nothing of its own: not while the character attacks, blocks or dodges, is stunned or stumbling, holds a crossbow
  pose, is down, carried or getting up (as of the last finished tick). The move is animation only: it lands no blow, nobody blocks it, and
  the combat system may start its own swing over it at any tick. A technique the animation library has no clip for plays nothing. The
  source runs in the animation's parallel step for every stepping character, and the move enters the world: its layers are hashed and a
  relocating clip's root motion moves the character. So it must be cheap, thread-safe, read-only and deterministic: answer from state the
  tick has already settled (its own system's serial step, the last finished tick), never from call order or caches that change the answer,
  and never call `ModMove` itself. A throw, a move without a technique or a progress that is not finite is a fault: logged once with the mod
  id; the source still answers for the rest of that tick and answers nothing in that world from the next tick on, and the fault enters the
  state hash.
- **Dialogue codes:** `AddDialogueCondition(code, handler)` and `AddDialogueEffect(code, handler)`, named from FCS data in a
  DIALOG_ACTION's `condition name` / `action name`. A condition gets `DialogueCheck` (world, speaker, the subject `who` picked, the
  condition) with `Matches(bool)` and `Compare(double)` that apply the vanilla value and `compare by` rules; it only reads (it also runs
  for interface previews). An effect gets `DialogueAct` (world, speaker, target, effect) in a serial step. An unregistered code
  computes "no" / does nothing, as an unknown vanilla code.
- **Record types:** `AddRecordType(type, name)` claims a number (not a vanilla `itemType`); `Records(type)` lists the merged records.
- **Equipment slots:** `AddEquipmentSlot(new ModSlot(section, fcsSlot, width, height, x, y))` (`ModSlots`, `src/Meitou.Simulation/Items`) adds
  an inventory section that takes the ARMOUR and ITEM records whose FCS `slot` is `fcsSlot`, so the items stay a data mod. `fcsSlot` is 4, 10
  (ATTACH_GLOVES), 11 (ATTACH_NECK) or 15 and up; the others map to vanilla sections. A vanilla section name, a name or `slot` another slot
  took, or a size outside 1 to 20 cells refuses the mod. The slot holds one item, is worn gear everywhere a vanilla clothing slot is (armour
  stats, look, trade, looting, locks), and shows in the character and loot windows as a copy of the belt slot at (`x`, `y`) layout pixels
  (UI scale 1) from the window's top-left corner, clipped to the window: the mod picks a free spot; the window does not grow. Animals get none.
  Saves keep the section name ([save](formats/save.md#compatibility-exceptions)). Races' `no hats`-style closing never applies.
- **Wear rules:** `AddWearRule((item, race, animal) => bool)` (`WearRules`, `src/Meitou.Data/Gameplay`) answers "may a wearer of this race
  wear this item?" with the item's record id, the wearer's RACE id and whether it is an animal, after the item's own `races` / `races exclude`
  lists pass ([RaceLimiter](game/ui-screens.md#6-inventory-trade-dialogue-build)). False refuses the item wherever those lists are checked:
  equipping (an NPC drawing a weapon, picking up, scavenging, a purchase worn at once), wearing a bag, dragging or moving an item into a slot
  (trade included), the worn clothing and armour of a loaded save, and the clothing, armour, backpack and robot limbs a generated character
  rolls. A generated character's weapons, a loaded save's back and hip weapons and a quick move's bag or prosthetic are checked against
  neither. Weapon slots, bags and mod slots are asked too, so a rule meant for clothing checks the item's type (`ctx.Data`); no item record
  changes, so it works with other mods' gear. It may run on any thread: quick, no world state, the same answer for the same arguments. A
  throw counts as yes and is logged once; with no rule the race lists alone decide.
- **Medical meters:** `AddMedicalMeter(label)` returns a `ModMeter` (`ModMeters`, `src/Meitou.Simulation/Bodies`): a bar on the medical panel after
  Hunger, in registration order. The mod's system sets a character's `MeterReading(Green, Value) { Yellow, Red, Arrows, Tooltip }` in the tick with
  `meter.Set(cold, reading)` (the character's own partition in a parallel phase) and hides it with `Clear`; layers are width fractions clamped to
  0..1, drawn as a part's green, yellow and red. A selected or inspected character's readings are published with the snapshot; not saved, not
  hashed, read by no system, so the mod keeps its value (`IModSaveData`) and sets the reading again after a load. The layout has nine rows,
  which a human fills; each meter adds a copy of the ninth, and more than nine rows share the nine rows' height. A character with more rows than
  the bars (a race of more parts) scrolls through them with the wheel ([medical panel](game/ui-screens.md)). A label another meter has
  (case-insensitive), one over 32 characters or a fifth meter of all mods together refuses the mod. A building's rows never show meters.
- **Task bars:** `cold.ModTaskBar = new TaskBar(caption, progress)` shows the floating bar the game draws over a character picking a lock, with
  the mod's caption (a message of the game's catalog shows translated, as the game's own; any other as given) and its fill (0..1, clamped; not a
  finite number shows empty), over any character within the name tags' range (another character's only while it is drawn); null removes it. It
  wins over the game's own bars (lockpicking, cutting, training) while set. One bar per character: the last system to set it in the tick wins,
  so a mod clears only its own (`if (cold.ModTaskBar == mine) cold.ModTaskBar = null;`). Set it in the tick from the mod's own system (the
  character's own partition in a parallel phase). Display only: not saved, not hashed, read by no system, so the mod keeps its progress and sets
  the bar again after a load. A carried character shows no bar.
- **Skills:** `AddSkill(new ModSkill(id, label, column) { Description, Affects, Training, Start })` (`ModSkills`, `src/Meitou.Simulation/Bodies`)
  lists the skill at the foot of a stats-window column (`SkillColumn`: Weapons, Combat, Thievery, Sciences, under that column's last heading)
  with its whole level, and a click shows the description pane with the mod's lines and the next-level bar. The mod's systems read
  `skill.Level(stats)` (`Start`, default 1, until set), `Set` it, or `Gain(stats, amount)` on the game's curve (`CharacterStats.CoreGain`, no
  race multiplier) in the tick: a character's own in its partition of a parallel phase, another's only outside the parallel phases; the
  game's own actions never train it. Levels live in
  `CharacterStats.ModSkills`, enter the state hash once one is set, and are saved in the character's STATS record
  ([save](formats/save.md#compatibility-exceptions)). A column holds 18 lines; past its 14 the rows share its height (room for 11, 8, 10 and 5
  mod skills). A bad id (1 to 48 letters, digits, `_`, `.`, `-`), an id another skill has (case-insensitive), a label over 32 characters or a
  full column refuses the mod. Not shown in the HUD's stat lines.
- **Bone anchors:** `CharacterCold.DrawAnchor = new DrawAnchor(carrier, bone, offset, rotation)` draws a character on another's named bone
  as the renderer poses the carrier in the drawn frame (a rider on a mount; the rule and its limits:
  [character-renderer](character-renderer.md#bone-anchors-meitou-engine-choice)); null removes it. Set it in the tick from the mod's own
  system (the character's own partition in a parallel phase); it reaches the host with the snapshot it was set in, so no render-thread
  code is needed. Display only: not saved, not hashed, read by no system, so set it again after a load and keep the character's simulated
  position at its carrier's (that position culls it, and draws it while the carrier is not drawn or lacks the bone). The vanilla shoulder
  carry overrides it while the character is carried.
- **Bone meshes:** `cold.SetBoneMesh(new BoneMesh(key, mesh, bone, offset, rotation))` hangs a rigid mesh on the character's named bone (a saddle
  on a mount) as the renderer poses it in the drawn frame, its origin moved by `offset` and turned by `rotation` in the bone's frame, offset and
  mesh scaled with the character; `RemoveBoneMesh(key)` takes it off, and a second `SetBoneMesh` with the key replaces it, so several mods share a
  character. The look and worn gear stay as they are (an animal keeps its bag). `mesh` is a file name found as a record's mesh is (the install and
  the mods' folders, by bare name), drawn with its own material as a loose mesh: no shadow, no outline or picking
  ([character-renderer](character-renderer.md#bone-anchors-meitou-engine-choice)). Set it in the tick from the mod's system, as `DrawAnchor`;
  display only: not saved, not hashed, read by no system, so set it again after a load.
- **Decision sources** (`Meitou.Simulation.Ai`, [rules](simulation.md#decision-step)): what each of the player's characters does next (the
  order queue, jobs, goals, idling) is picked by the decision step from an open list of `IDecisionSource`s. A registered system implementing
  `IDecisionOwner` gets `RegisterSources(DecisionSystem)` once when the world is made and adds its own with `Register(source)`, so a mod adds
  a behavior without patching. Sources are asked in `Step` order (`DecisionStep`: 1 orders, 2 commitment, 3 urgent jobs, 4 goals, 5 jobs,
  6 fallback) and by `Key` within a step, equal keys in registration order (mods in load order); in step 4 the best desire x weight wins,
  elsewhere the first that can run. `CanRun` and `Desire` are pure reads of the world; `Start` starts the task (`TaskStart.Walk`, a row of
  the mod's own from 1000 up). To run before a character idles, register at step 6 with a key below Idleness (`2000 + TaskType.Idle`).
  Every player character is asked, so a source checks that the character is one of its own squads'. Several mods can each add sources.
  `RegisterJob(type, source)` is the other kind: one contributor per job type, for the types a mod claimed with `AddJobType`. NPCs do not
  go through this step; their AI packages run them (a mod's goals for them: NPC goals, below).
- **Task handlers** (`Meitou.Simulation.Ai`, [task starts](simulation.md#task-starts)): a registered system implementing `ITaskOwner` gets
  `RegisterTasks(TaskHandlers)` once when the world is made, in system order, and registers an `ITaskHandler` (`Start`, `End`, `Done`) per
  `CharacterTask`, per row (`Register(TaskType, handler)`, a mod's row from 1000 up) or per row on a site (`RegisterOnSite`). One handler per
  key: a later registration replaces an earlier one, so a mod system replaces a standard handler only when it sits after that handler's system.
  `Register(row, handler)` with a `TaskType` row of the mod's own (1000 up) sends every start of that row, whatever its `CharacterTask`, to the
  mod's `ITaskHandler`: `Start` (the row is eligible, the fight and the task before have ended, the provenance `CharacterCold.Running` is
  recorded), `End(task, TaskEnd)` when a serial start replaces it (a fight's too) or `TaskStart.End` ends it (`Replaced`, `Stopped`, `Done`), and
  `Done` (by default: the character's task is no longer that task; the player's order queue reads it). No handler runs for `TaskStart.Stand` (a
  knock-out, a trade or a conversation stopping an AI walk or stand), for the AI's own starts in its parallel think (`TaskStart.StartOwn`) or for
  the act-out's own `Step` / `StepTo`, so a mod also checks its characters in its own serial step (alive, not down, `Running` still its row and
  running the task). A row's handler takes the place of the task's, so the engine's walk does not start by itself: call the task's own
  (`tasks.Of(task)`) from it to keep it. `Register(CharacterTask, handler)` replaces the engine's handler of that task for every row; a mod
  registers rows only. Handlers run in the serial phases only (`TaskStart.Start` throws in a parallel one). A throw from `RegisterTasks` is not
  caught (the world is not made); one from a handler is caught as a fault of the mod's own system only when that system's phase started or ended
  the task (the system stops), and is not caught when an engine system did.
- **Goal claims** (`IGoalSource`, [claims](game/ai-tasks.md#perma-jobs) "Claims"): a registered system implementing it adds the (worker, target)
  pairs it acts out now in `Publish(World, SquadGoals)` with `goals.Add(squad, worker, GoalTarget.Of(body) or GoalTarget.At(site id))`. It is
  asked serially, in system order, each time a finder takes a snapshot (`SquadGoals.Of`) and once at the end of every tick; a pair stays claimed
  3 s after it was last published (`World.Claims`, `GoalClaims.Seconds`). The finders that skip what a squad mate already works on
  (`SquadGoals.Working`, `IsMine`: NPC looting, the medic and splint finders, captive exports, production errands) then skip the mod's targets
  too. It chooses nothing and starts nothing. `Publish` only reads the world, in a fixed order: the claims steer later starts, so they must be
  the same on every run. A throw is not caught as a mod fault: the world's end of tick and the engine's finders call it.
- **Mod jobs:** `AddJobType(type, name)` claims a task type number from 1000 up (`SimulationExtensions.FirstModCode`; the game's tasks stay
  below it; one mod per type). The mod puts such jobs in `CharacterCold.Jobs` and runs them itself (an `IJobSource` of its system); like the
  game's jobs, they are worked only while the character's jobs switch is on. Taking a mod job off the list does not stop a task the mod already
  started from it, so the mod checks that the job is still listed. The jobs panel names them `name`. A save keeps each in its place in the list
  under Meitou keys (`meitou mjob<k>`, [save](formats/save.md)), out of the original's `pjob` list, with its building id but not a character
  target; a load puts it back while the type is registered and drops it otherwise.
- **Item use:** `AddItemUse(record, handler)`, one mod per item record string id. A right click on such an item in a player character's own
  inventory window (or its bag), with no other character's window open and no trade, runs `handler(ItemUse)` (world, user, item, that
  character's inventory) in the player system's serial step, before the click would equip or move it (`ItemUses`, `src/Meitou.Simulation/Player`),
  for an item a quick move could act on (movable or limb section, not locked on). The click's token holds the tag the window showed, and
  neither the handler nor the quick move acts on an item that no longer carries it: a stale click never reaches a differently tagged item in
  the same cell (two items alike in record, cell, quantity, quality and tag are not told apart).
  True: the click is used (the handler removes a used-up item, `Inventory.RemoveOne`); false: the click equips or moves as usual. A throw is
  logged and counts as used. `ItemInstance.ModTag` is a free string a mod sets on an item (the character a note names); it is copied with the
  item, keeps differently tagged items out of one stack, is hashed when set, and is saved as `meitou mod tag`
  ([save](formats/save.md#zone-files); a retagged ground item rewrites its zone). A tooltip hint is the item's own description.
  **Verified** by `ModHookTests` (synthetic world): the click and its guards (stale tag, locked on), fall-through, tag stacking, hash and save key.
- **Persistence:** a mod system implementing `IModSaveData` (`Write`/`Read(Stream)`) is saved under its name in the mod's sidecar file
  ([save](formats/save.md#mod-data)), captured between ticks with the save and restored right after `SaveLoader.Load`, before the first
  tick. `Read` runs only when the save has data for the system. Vanilla save files stay byte-exact.
- **Interface:** `AddUi(IUiExtension)`: namespaced styles, controls, actions, overrides and slot contributions (`UiSlots`), registered
  with each new interface (`ConfigurePlayerInterface`, Vulkan and Metal). A picture from the mod's folder is an image node:
  `new UiNode(id, UiNodeKind.Image) { Image = Path.Combine(context.Folder, "face.png") }`; a CHARACTER record's portrait is
  `{ Portrait = "<string id>" }` on the same kind of node ([native UI](native-ui.md#layout-input-and-lifetime)).
- **Map markers:** `UiRegistration.AddMapMarkers(markers)` puts markers on the world map (Overview's Map tab). `markers` gets `UiMapContext`
  (the snapshot the map is drawn from and its zoom level, 0 for the whole map to 2) on the host's thread each frame the map shows, so it
  must not touch the world, and returns `UiMapMarker`s: a world point (X, Z), an image group of the game's map marker set
  (`Kenshi_MapMarkers`: Town, Village, Outpost, Ruin, Camp, Nest, Workcamp, SmallPlace, Character, Movement, NewDiscovered; names are
  exact), a tint, an optional label (shown from zoom level 1) and tooltip (rows split by new lines, a tab between a row's label and value,
  as a place's), the least zoom level it shows at and a size against the group's image. They are drawn after the towns, camps and outposts
  and before the tracked squads and the player's characters, centred on their point and sized as a place's are. A mod keeps its markers
  itself (in its `IModSaveData` to keep them across a load). A source that throws or gives a marker with a group the marker set lacks, a
  point, tint or size not finite, a size not above 0 or a zoom level outside 0 to 2 is logged once and dropped.
- **Right-click menu rows:** in `Register`, `registration.AddCharacterMenuVerbs(context => rows)` adds rows to a character's right-click menu
  after the game's own. The source runs on the host thread whenever the game asks for the menu, which includes every frame a character is under
  the cursor (whether it offers a menu decides what a right click there does), so it must be cheap; a tick may be in flight, so it must not
  touch the world. It gets `UiMenuContext`: the target's snapshot, the selection and the cursor as the menu is built, `Shift` and `Tick` read
  when asked (so a chosen row sees them as the player chooses it), and `Enqueue`; a chosen `UiMenuVerb` queues commands. Return no rows to keep
  a plain right click (a row makes the character offer a menu). Shift does not turn a mod row into a job; the row reads `Shift` itself. Menus
  that stay empty or fixed get no mod rows: no selection, a body the first selected carries (Put Down only), a body someone outside the player's
  faction carries; an animals-only selection keeps only the original's animal verbs. A source that throws, or returns a row without a caption
  or action, is logged once and dropped for the session; a row's action that throws is logged.
- **Debug rows:** `AddDebugCommand(label, run, hasField)` adds a row to the F10 menu after the vanilla ones. `run` gets
  `DebugCommandContext` (field, selection, camera target, tick, `Enqueue`) on the host thread while a tick may be in flight, so it must
  not touch the world; it returns the status line, and a throw shows as the status.
- **Squad AI tab options:** in `Register`, `registration.AddSquadAiOptions(context => options)` adds tick buttons to the squad overview's AI
  tab, after the game's own and above Follow Formation; the tab's lines shrink to fit. The source runs on the host thread on every layout pass
  while the tab is open, so it must be cheap; a tick may be in flight, so it must not touch the world. It gets `UiSquadAiContext` (the
  `Snapshot` on screen, `Tick` read when asked, `Enqueue`) and returns `UiSquadAiOption`s: caption, tooltip, whether it shows ticked, and the
  press. The option's state is the mod's own: a press queues a command, its system applies it in a serial step and saves it (`IModSaveData`),
  and the system publishes the value (a volatile field, set again after a load) for the source to read. A source that throws, or returns an
  option without a caption or press, is logged once and dropped for the session; a press that throws is logged.
- **Settings:** `AddSettings(object)`, once per mod: the object's public get/set properties marked
  `[Setting(label, Hint, Min, Max, Step, Options, SubGroup, VisibleWhen)]` (`ModSettings.cs`) become one group, in declaration order (a
  sub-group's settings together at its first one's place), under the mod's row on the Settings screen's Mods page
  ([native UI](native-ui.md#settings)): `bool` an Off | On switch, `int`/`float`/`double` a slider (Min and Max required; Step 0 = 1 for `int`, a
  hundredth of the range otherwise), an enum or a `string` with Options a list. `SubGroup` puts a setting under a sub-group header of that name (its
  own fold, at its first setting's place); `VisibleWhen` names a public `bool` property of the same object and hides the setting while it is false,
  so a switch can hide its dependents (the page reads it every frame; a throw from it is logged and the setting stays shown). Both are optional; a
  `VisibleWhen` that is not such a property refuses the mod; a game older than both fails reading the attribute, so
  a mod using them sets `gameVersion`. Another type, a number without a range, a list whose value is not one of its options, a
  struct (the page would change a copy) or no marked property refuses the mod. The player's values are kept in `meitou.user.json` (`modSettings`: mod
  id, key, invariant text; a default is not kept) and set on the object before `AddSettings` returns, a number clamped to its range, a value that is
  not one of the setting's (or not finite) ignored with a log line; the values the object had are the defaults Reset and Restore defaults put back as
  they were, not snapped to the step (Restore defaults also drops every kept mod value). The page sets a changed property at once on the interface's
  thread, outside the tick, and raises the returned group's `Changed`; a throw from the setter or a handler is logged and shown as the page's
  notice. A value the mod sets itself with `ModSetting.Set` or `Reset` (a window's place, an in-game slider) raises `Changed` the same way and is
  kept in `meitou.user.json` like the page's; call them on the interface's thread. A slider's arrow keys move one Step. A mod copies what its simulation needs in a serial step (its system's `Inputs`). **Verified** by
  `ModdingTests`; the page **Observed** drawn (2026-11-04, Windows, Vulkan, `--settings-showcase` changed locally to open it with a scratch group).
- **Weather kinds:** a WEATHER or EFFECT `affect type` outside 1-5 is kept as its number, with ARMOUR `weather protection<N>` and RACE `weather
  immunity<N>` naming it the same way, so a mod defines a hazard in data and acts on it from its own system
  ([weather](formats/weather.md#effects-on-characters), "Undefined kinds": what the vanilla systems still do with such a kind). Nothing
  claims a number; take one from 1000 up, as the other mod codes.
- **Stat modifiers:** `cold.Stats.Modifiers.Set(key, stat, amount)` / `Remove(key, stat)` / `RemoveAll(key)` (`StatModifiers`) add a temporary
  amount to a character's stat (a drug's +10 strength, a sickness's -5 dexterity) under the mod's own key. Every gameplay read of the stat
  (`CharacterStats` indexer, `Strength`...) adds it before the injury, hunger, gear and animal-age multipliers, and the stats window shows it;
  training (`Gain`, the XP difference terms), saves, the debug stat boost, taming and the debug editor use `Stored(stat)`, so it is never trained in or
  saved. It enters the state hash once one is set, and is not persisted: keep it in the mod's `IModSaveData` and set it again after a load.
  Medic, HiveMedic and Vet share one storage. Change it from the mod's system outside the parallel phases. The body's look reads the stored value;
  a new body (a race change at character creation) starts without modifiers. **Verified** by `StatsTests` (reads, save, gain, hash).
- **Commands:** a mod's own `SimCommand` types, queued with `Enqueue` and handled in its system's `Inputs`.
- **Player squads on AI packages** (`Meitou.Simulation.Ai`, [rules](simulation.md#autonomous-player-squads)): `SetSquadAutonomy(squadId, on)` lets
  the AI run a player's squad; then `ChangeAi(squadId, packageId)` and `StartContract(squadId, packageId, hours)` give it packages as dialogue's
  `change AI` / `AI contract` give an NPC squad's (both are ignored for a player squad that is not autonomous), and
  `SetSquadHome(squadId, building)` its home: a town building's key (`TownBuilding.Key`, `x.y:instance`) or a player-built building's id
  (`BuildSite.Id`); null clears it. A player-built home in one of the faction's outposts makes the outpost the squad's town: its patrols and
  going home and town work on the outpost's own machines use it ([rules](simulation.md#autonomous-player-squads)). The player's orders and
  jobs come first. `SetSquadAutonomy(squadId, true, Independent: true)` makes it a squad the player does not lead: not selectable, no squad
  tab, no squad commands, not counted against `max squads` (for staff, caravans or companions with a will of their own; orders that name its
  characters still apply; refused for the last squad the player leads; `SetSquadAutonomy(squadId, true)` leads it again). Its menu has no
  inventory row, but a mod may open the inventory: a row of its own (`AddCharacterMenuVerbs`) queues `OpenLootOrder(selection, member)`, and
  the window works as an own character's (handing out a uniform, a dress code). **Observed** (2026-11-17, release 0.21.0, a test mod's row on an
  independent member): the window opened, armour moved into its slot was worn, and an item dropped on the member in the world went into its
  grid. Pass your mod's id as `Owner` (`SetSquadAutonomy(squadId, true, Owner: context.Id)`): a load without your mod running (not installed, refused or failed to
  start) gives the squad back to the player (autonomy, independence and home cleared, with a notice), so removing the mod never strands it; a
  squad switched on with no owner, as in a save from before `Owner`, stays as saved until a command names one (send `Independent: true` again
  with it to keep the squad independent). The switch, package, home and owner are saved with the squad. Off drops the package, the members'
  minds and the independence. Ids: `SquadManagement.PlayerSquads(world)`, `world.Buildings.All`, `PopulationData.Sites[i].Buildings`.
  **Observed** (2026-11-13, headless, two saves, 'Guard building, residents'): members without a job patrol, go home and lie in the home's beds, a
  move order takes them at once and they resume after it, and a re-saved game loads autonomous.
- **Squads outside the faction size count** (`Meitou.Simulation.Factions`): `SetSquadUncounted(squadId, on, Owner: context.Id)` keeps a squad of
  the player's faction out of the `max faction size` count (`Squad.Uncounted`; recruiting, slave trading, `SpawnIntoPlayerSquad` and the squads
  screen's count use `SquadManagement.CappedHeads`), for townspeople, staff or followers a mod adds beside the player's own characters; the
  dismiss rule still counts everyone (`FactionHeads`). Several mods can each mark their own squads. Saved with the squad; a load without the
  owner mod running counts the squad again, with a notice. Engine API, not in the original.
- **Squad purses** (`Meitou.Simulation.Factions`): `SetSquadPurse(squadId, own, Owner: context.Id)` gives a squad of the player's faction its own
  purse (`Squad.OwnPurse`, its `Squad.Money`): its members pay an NPC-style purchase (a shopper buying from a building's stock) from it
  instead of the player's money, and a shop the squad owns keeps its takings in it, so a shopkeeper keeps its own earnings. The trade window
  still uses the player's money. Ending it pours what is left into the player's money. Saved with the squad; a load without the owner mod
  running ends it the same way, with a notice. Engine API, not in the original.
- **Off-screen squads:** squads the player does not lead (`SetSquadAutonomy(..., Independent: true)`) are stepped as NPCs are when far from the
  camera and the led characters ([far cadence](simulation.md#far-cadence)), so a town's residents cost what NPCs cost. `SetSquadDormant(squadId,
  on, Owner: context.Id)` (`Meitou.Simulation.Factions`) puts a squad of the player's faction to sleep: its members ask for no zones round them,
  so their place unloads when nothing else keeps it, and while the far cadence runs they stand still away from the camera and the led
  characters (a town that sleeps while the player is away; the mod catches it up itself when it wakes, for example in `IWorldReady` or when
  the player comes near). Saved with the squad; a load without the owner mod running wakes it, with a notice. Engine API, not in the original.
- **World hooks** (engine API, not in the original; serial phases): `PopulationSystem.RequestCampaign` queues a
  FACTION_CAMPAIGN against a town as a dialogue trigger does (the request pass then applies the game's gates); `PopulationSystem.SpawnPlatoon`
  adds an unloaded platoon of a SQUAD_TEMPLATE at a walkable point in a ring round a point, with a purse, that loads like a roaming one, is
  kept off screen whatever the keep switches say (and stays loaded while a member lies knocked out in the loaded zones,
  [roaming](simulation.md#off-screen-tier)), is saved also before it first loads and loads back as the same roaming squad (-1 and nothing
  added when no walkable point is found); `Platoon.ModTag` is a free string, saved when set, that finds a mod's platoon again after a load,
  when platoon ids are given anew; for the player's own squads and outposts, whose ids and town numbers are also given anew at every load,
  `Squad.ModTags[context.Id]` and `PlayerTown.ModTags[context.Id]` hold one tag per mod (keyed by the mod's id, so several mods tag the same
  squad or town without replacing each other's), saved with the squad and on the outpost's town state; `PopulationSystem.SpawnIntoPlayerSquad` makes one
  character of a CHARACTER or ANIMAL_CHARACTER record as a new game makes the player's (a look rolled for the player's faction, body,
  starting items, name; a unique claimed as any spawn claims it) and adds it, standing idle on the ground at a walkable point, to a living
  player squad, leading it when it has no leader. Only player squads: an NPC squad loaded from a save keeps only the save's members, so an
  added NPC would be lost at the next save or unload (`SpawnPlatoon` adds NPCs). It returns the character's id, or none and adds nothing
  for another squad, another record type, a point that is not finite or not walkable, a unique already claimed, or the recruit's caps
  (`max squad size`, `max faction size`); `PopulationSystem.JoinPlayerSquad(world, character, squadId)` moves a living character that is not
  the player's (a traveller who settles) into a living player squad as a recruit joins (the player's faction, orders and class; no AI, jobs or
  uniform marks; written as a new member at the next save), leading it when it has no leader, and returns false, changing nothing, for
  another squad or character or at the same caps; a squad kept out of the faction size count (`SetSquadUncounted`) has only the squad cap
  for both; the `VisitTown` command sends a squad running a wandering trader package to a
  player town for a stay of N hours, counted from its first step there after the command (on the way it stops at no other town; it leaves a
  town that is not public at once, as any trader); `BedSystem.PlayerBedRent` prices the player's indoor beds for other factions: set, an NPC must afford it from its squad purse and
  pays the player once it lies in the bed, and a visitor in a player town picks the nearest bed it can afford before the NPC bed search (null
  keeps the original: NPCs never pay). Non-finite delays, hours, points and radii are refused. **Verified** by `ModHookTests` (synthetic
  world): the spawn ring, purse and refusals, a never-loaded spawn kept and reloaded with its tag, and the campaign queue.
- **Driving NPCs** (engine API; serial phases): `PopulationSystem.HoldPlatoon` stops a roaming platoon's trips and its
  stand-in's travel (not saved: find it by its tag and hold again after a load; it still unloads, and despawns as any platoon but a mod's
  spawn). `PopulationSystem.SendPlatoon` sends a held platoon's stand-in off screen toward a point or a living character (by the roads, at the
  stand-in travel speed, stopping within the roam arrival radius; null stops it; not saved, cleared when the hold ends); once loaded the mod
  walks its members. A hold on a platoon that is in the world changes its members' minds on the next package step: they stop acting out their package (a sleeper gets up, one sitting stands, the followers follow their leader again and the leader stands; one in a fight or knocked out rejoins once it stands idle) and keep only its combat goals, and releasing the hold gives the full minds back. A held squad whose package itself travels (a wandering trader's, a mission's) is still walked by that package while it has somewhere to go. The `EndContract(squadId)` command ends a running contract as its hours running out would (a `StartContract` of the
  same package after it starts it afresh; the game ignores a start of the contract already running), and `StartContract { AnyRange = true }`
  lets the contract's assault take its target wherever it is, not only within the manoeuvre mode's reach (about 6517 units for ROAM_FAR;
  saved with the contract). **Verified** by `ModHookTests` (synthetic world): a held stand-in, travel to a point, the contract's end and restart,
  and an any-range target taken past the reach. `CharacterCold.Engaged` keeps the AI
  off one member (self-preservation still answers an attack); the mod then drives it with what the engine's own duties use: `TaskStart.Walk`,
  `CharacterOrders.Sneak` on `Orders` (speed, sight and spot meters follow it for any faction), `LockSystem.Picking.Step` (books the Burglary
  a witness answers), `CrimeSystem.Pickpocket`, `CarrySystem.BeginPickUp` / `BeginPutDown`, `ArrestSystem.TakeOver(enslave: true)` (a guard
  takes the carried body to a cage as its faction's slave), `AggroSystem.Spotted` / `Unseen`. `CharacterCold.FedTo` is a free hunger level
  that comes before the game's rule (not saved), for a captive the mod's squad carries.
  `Engaged` is one shared switch with no owner: dialogue, trading, arrests, kidnapping, captive jobs, town duties and lock rounds set and clear
  it too. So engage only a character the AI holds now (`AiSystem.Holds(table.Next[slot], cold)`, public), clear only an `Engaged` your mod set
  (then set `cold.Ai.Replan` so it chooses at once), and take characters back after a load: `Engaged` is hashed but not saved, and neither are
  the AI's minds.
- **NPC behaviors without the chooser** (what upstream's town duties, kidnappers and lock rounds do; the duty route): a data mod lists an
  AI_TASK whose `enum` is a number of the mod's own (1000 up) in its AI_PACKAGE records. Without the code mod such a goal loads (`AiTask.From`
  keeps any number) and is never chosen: it has no behaviour, so its desire is 0. The code mod's system, in a serial step, finds who has the
  goal now: `PopulationSystem.GoalDuty(world, squad, role, type)` (public, from `world.Systems`; the running package's role list in the hour's
  window, a campaign squad's phase package included) or `GoalsOf`, the role being the leader's for `squad.Leader`, else by `cold.Role`
  (`SquadRole`). It takes members the AI holds as above, walks them with its own row (`TaskStart.Walk(world, slot, point, mode, row,
  TaskSource.Goal)`, `TaskStart.Become` to stand), optionally with a task handler of that row, publishes its targets as goal claims, releases
  them when done, and keeps its state in `IModSaveData`. Such a duty does not weigh against the package's other goals (weight, tier, hours):
  the mod decides when to take a member; NPC goals (below) let the chooser decide.
- **NPC goals** (`Meitou.Simulation.Ai`, [rules](simulation.md#mod-goals)): `AddGoalType(type, name)` claims a task type number from 1000 up for
  NPCs (one mod per type; `name` is for log lines), as `AddJobType` does for jobs (the two are claimed apart). A registered system implementing
  `INpcGoalOwner` gets `RegisterGoals(NpcGoals)` once when the world is made (in system order, only when some mod claimed a goal type) and calls
  `Register(type, goal)` for a type its mod claimed; a type not claimed, another mod's or registered twice throws, and a throw drops that system's
  goals with a log line. An NPC whose running package (or race's `AI Goals`) lists an AI_TASK of that `enum` then weighs it in the AI's chooser
  beside the package's other goals: by the record's tier (`classification`) and hours, the package's weight and the running goal's continuity
  (x1.25); the mod's type has no commitment or crowding. `INpcGoal.Desire(world, in NpcGoalAsk)` (slot, id, squad, type, `targeting`) is the whole
  desire: asked in the parallel think on the asker's own partition, it must be a cheap, deterministic, pure read (the character's own slot and
  cold state, `Previous` for others, the squad's blackboard, the mod's state that only its serial steps change; randomness through `Rng.Hash` with
  the mod's purpose; scratch lists `[ThreadStatic]`); work out costly answers in the mod's own serial step and keep them in its hashed state. The
  winner stands while `Start(world, in NpcGoalAsk)` is called in the AI's serial step of the same tick, in slot order, for a character the AI
  still holds: it engages the character (`Engaged = true`) and starts its task with a row of its own from 1000 up (`TaskStart.Walk`, `Become`;
  `TaskSource.Goal`), and answers true; false starts nothing and the next think chooses again. A started goal stays the mind's goal: the HUD's Goal
  line shows the AI_TASK record's name (also while the AI holds it), and below the urgent tier the combat goals may take the character over, a
  fight whose start ends the mod's task (its row handler's `End`, `Replaced`) while `Engaged` stays set: the mod then releases or waits for the
  fight's end. The mod clears its own `Engaged` and sets `Replan` when done, and the goal is weighed afresh; a package change does not stop an
  act-out started, so the mod checks the goal is still listed (`GoalDuty`). Player characters (an autonomous squad's) and the members of a squad
  the population moves (engine minds) never weigh it. A `Desire` that throws or answers a number that is not finite, or a `Start` that throws, is
  logged once with the mod id and the goal type, the goal is never asked again in that world (a desire fault from the next tick: the rest of its
  tick still asks it), and the fault enters the state hash. Nothing is saved: minds choose again after a load, so the mod takes its characters back
  in `IWorldReady`. Without the code mod the goal is inert again. With no goal type claimed nothing is built. Engine API, not in the original.

## Data mods

Some shapes need no code: a race's anatomy of any length, extra or missing parts, and parts of a new type such as a tail are plain records
([bodies of other shapes](game/character-stats.md#bodies-of-other-shapes-meitou)).

## Harmony

- With code mods enabled the JIT warm-up (`JitWarmup`) starts only after `Initialize` returns. Tiered compilation is off, so a caller
  compiled before a patch keeps any inlined copy of its target. Started that late, it compiles the game's code (its renderers first) before the
  simulation's: the title's first frames are near. **Observed** (one code mod, 655 mods): the title's first warm-up frame went from 1.8 to 1.0 s.
- **Observed** ([Harmony docs](https://harmony.pardeike.net/articles/patching-edgecases.html)): an inlined method cannot be patched; the
  patch silently does nothing. Patch methods large enough not to be inlined, or their callers. `Rng` helpers are `AggressiveInlining`.
- **Observed** (2026-11-04, scratch run): Harmony finds a patch method again by module identity, so the same DLL file loaded by two mods
  makes their patches resolve to one copy. A helper library copied into several mods must not hold patch methods; a framework shared
  as a [dependency](modding-dependencies.md) loads once and may.

## Determinism contract

Mod systems and patches obey [simulation](simulation.md) "Threading": parallel phases write only their partition's `Next`, read
`Previous` and the grid, send cross-character effects through `EffectBuffer`; randomness through `Rng.Hash` with the mod's purpose;
no `World` access outside the tick (tick-ahead runs on `Meitou.Tick` during the draw); outside input goes through `Enqueue`. Violations
break golden hashes and any future replay. Nothing in the API can stop a patch; the check below finds most offenders.

### Determinism check

`meitou-tools mod-check <save folder> [--ticks n] [--threads n] [--mod <mod folder>]...` (`tools/Meitou.Tools/ModCheck.cs`, shipped in
the release zips beside the game) loads a save
headless with the enabled code mods, or only the `--mod` folders (their code; their records join the data only when enabled), and runs
it three times: twice on one thread and once on `--threads` (default 4), for `--ticks` (default 600). It compares `World.StateHash`
after every tick and reports the first differing tick of each pair and the `IStateHashed` systems that differ there, a mod's by id and
name. One-thread runs differing points at unseeded randomness or the clock; thread counts differing, at a threading mistake. On a
difference it runs the game without mods: if that differs too, the fault is the game's.

- Walkability is the open terrain, not the navmesh (whose zones load on threads), paths are answered in the tick and the population works
  in it, so runs repeat; the world differs from play in where characters walk.
- The parallel phases cut slots into partitions of at least 32, so a world under 64 characters runs them as one partition and cannot show
  a threading mistake; the command warns.
- It sees only hashed state: a mod's unhashed state, or a patch that changes nothing hashed, passes.
- **Observed** (2026-11-04, win-x64, saves `autosave0`, `Gigachad`, `260`): a clean scratch mod passes; a mod rolling `Random.Shared` in
  `SlowWorld` differs from tick 1 in both comparisons and is named; a mod whose `Act` writes one field from every partition differs only
  across thread counts once the population passes 64 characters (tick 152) and is named; the game alone agrees.

## Versioning

- The manifest's `apiVersion` must lie in `ModApi.MinimumVersion` (1) to `ModApi.Version` (2); otherwise the mod is refused at load with a
  message. 2 added `version`, `dependencies`, `loadAfter`, `incompatible`, `gameVersion`, `ModContext.Dependency<T>` and
  `IModDependents`; an API 1 mod loads unchanged. `gameVersion` ties a mod to the engine releases it was built against.
- Harmony patches non-public members; compile-time references need public types or a publicizer. Open: which simulation types to make
  public, or ship publicized reference assemblies; ship reference assemblies per release so modders build without a game install.

## Platforms

- Managed DLLs run unchanged on the Windows, Linux and macOS builds.
- **Observed** (Harmony 3.0 RC notes): native async methods cannot be patched. The game runs on .NET 11, so this applies to any
  method compiled as runtime-async.

## Evidence

**Observed** (2026-11-04, win-x64, a scratch mod and harness against the release build, outside the repo, run as the game runs: tiered
compilation and PGO off, server GC): only allowed mods with a valid
manifest load and each refusal gives its notice; the process holds one `0Harmony` and one `Meitou.Modding` though the mod folder had its
own `0Harmony.dll`; a prefix applies, and a throwing `Initialize` loses its patch and registrations; a mod system's state survives
capture and restore; sidecars survive `SaveGame.Write`/`Load` and `Clone`; spliced systems land at their anchors and the standard list is
unchanged. The dialogue hooks are built but not exercised. **Verified** by `ModdingTests`: game assemblies resolve from the game, anchors,
sidecar round trip, and a hashed mod system agreeing at 1 and 4 threads.
