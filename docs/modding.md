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
  unpatch), and code compiled against 2.4.3 runs unchanged on the 3.0.0-rc.5 `0Harmony.dll`. 3.0.0-rc.7 patches the same way on the .NET 11.0.0
  release (2026-11-18, same setup).
- **No accessor mirrors.** Mods reference `Meitou.*` assemblies directly, as a patch needs the types anyway. Compatibility is per game
  version (RimWorld's model).

## Writing a mod

A class library matching the release's runtime: `net10.0` for legacy macOS, `net11.0` for standard builds on every platform
(`net10.0` everywhere before the .NET 11 move, 2026-11-11), referencing that platform release's `MeitouClient.dll`
(the game: the mod contract `Meitou.Modding`, the interface `Meitou.Game.Ui`), `Meitou.Simulation.dll`, `Meitou.Core.dll`,
`Meitou.Data.dll` and `0Harmony.dll`, all with `Private="false"`: the game supplies them. Releases after v0.17.1 ship these four Meitou
assemblies, not the earlier eleven, and most namespaces follow folders (`Meitou.Simulation.Population`, `Meitou.Game.Rendering.Terrain`):
a code mod built against v0.17.1 or earlier must be rebuilt. The references come from a release folder or, without a game install, from
the release's [reference package](#reference-package).

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

### Reference package

Every full release pushes `Meitou.References` to nuget.org, versioned as the release (`tools/scripts/package-references.ps1`; `release.yml`
pushes it after publishing through nuget.org's trusted publishing, and a release goes out without it when it fails to build; nightlies
push none). A mod's project references it and needs nothing else:

```xml
<PackageReference Include="Meitou.References" Version="<game version>" />
```

For `net11.0` (standard builds) and `net10.0` (legacy macOS builds), its `assemblies/<framework>/ref/` folder holds the compiler's reference
assemblies of the four Meitou assemblies (their public API; the internals two of them keep for the game's tools and tests stay out of a
mod's reach) beside the game's own builds of its other managed libraries (`0Harmony`, Bepu, DotRecast, Silk.NET and the rest), which the
compiler needs for any member typed in one of them. The Meitou assemblies carry the release's version like the game's: .NET binds a mod
only to an assembly of at least the version it was compiled against, so a mod does not load on a release older than the package it was built against.
The game never loads these copies: the Meitou ones are reference assemblies .NET refuses to run, and the libraries are always taken from
the game ([Loading](#loading)). The Metal backend's types (macOS builds only) are not in the package.

- NuGet imports the package's `build/Meitou.References.targets` into the project, which references every assembly of the set for the
  mod's `TargetFramework`, with `Private="false"`. The assemblies are not under the package's `lib/` or `ref/`, which NuGet would reference
  by itself; another `TargetFramework` (`net9.0`, `net11.0-windows`) is an error naming the two.
- `publicized/` beside `ref/` holds the four Meitou assemblies with every type and member public (compiler-generated members and explicit
  interface implementations left as they are), method bodies replaced by a throw and resources dropped, made by `tools/Meitou.ReferencePack` with
  BepInEx.AssemblyPublicizer (MIT, on AsmResolver, MIT); the targets take its libraries from `ref/`. `<MeitouPublicized>true</MeitouPublicized>`,
  set anywhere in the project, makes the targets reference this set and compile in `Meitou.IgnoresAccessChecksTo.cs`, which declares
  `IgnoresAccessChecksTo` for the four Meitou assemblies in the mod's own assembly: .NET then lets the mod reach their non-public members.
  A mod that already declares the attribute itself (a publicizer package does) leaves `MeitouPublicized` unset.
- Internals change between releases without notice: a mod using them should name its `gameVersion` in `meitou.json`.
- `THIRD_PARTY_NOTICES.md` and `licenses/` in the package carry the libraries' licences.
- **Observed** (2026-11-18, win-x64, a package built locally and restored from a folder feed): a `net11.0` and a `net10.0` mod implementing
  `IMod`, calling `ModContext.Harmony` and reading `VulkanDevice.Vk` (a Silk.NET type) compile with the `PackageReference` alone, and no
  package file is copied to their output; loading a Meitou file of the package throws `BadImageFormatException`. A mod calling an internal
  static of `Meitou.Data` and naming an internal type of `Meitou.Simulation` compiles only with `MeitouPublicized`, and the call then runs
  against the real `net11.0` assemblies.

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
  same method. The game references Lib.Harmony 3.0.0-rc.7 (`0Harmony.dll` with MonoMod merged in) and the resolution rule hands every mod
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
  removes its Harmony patches (`UnpatchAll` of its id). Records are not changed in it: much is built from them by then, and the renderer reads
  them on other threads at the same time; change them in the [data phase](#data-phase).
- Worlds are built again for every new game and load, so systems are registered as factories and made per world.
- `ModContext`: `Id`, `Name`, `Folder`, `Manifest`, the merged `Data` (`GameDatabase`), `Population` (`PopulationData`), `Log`/`LogError`,
  `RngPurpose`, `Harmony`, `Tick`, `Enqueue(SimCommand)` and the `PostSound` calls ("Sounds" below) for the running world (a command dropped
  and logged before the first world),
  `Dependency<T>(id)` and `HasDependency(id)` for declared dependencies ([dependencies](modding-dependencies.md#using-a-dependency)).
- `RngPurpose` is `StableHash(id) | 0x80000000` cast to `RngPurpose`, so it never equals a vanilla purpose.

### Data phase

- An entry type that implements `IModDataPhase`, directly or through a base class (generic or not) defined in the same assembly, gets
  `EditData(ModDataContext)` once per session while the game data is read: after every data file and translation is merged
  (`GameDatabase.Load`) and before the loader's own passes (wall masters, gear blueprints, docs/formats/fcs-mod.md), so before anything is
  built from the records, in `mods.cfg` order and before any mod's `Initialize`. One thread; nothing else reads the data yet. Which mods have
  one is read from the assemblies' metadata (no code run); a base class in another assembly (a framework's) does not give a mod a data phase,
  and only the mods found that way are called, whatever other mods are enabled. A loaded entry that is an `IModDataPhase` the scan did not
  find gets a log line saying its `EditData` does not run.
- `ModDataContext`: `Id`, `Name`, `Folder`, `Manifest`, `Log`/`LogError`, the merged `Data` to read (the files, translations
  and the earlier mods' changes), and changes in the form of a `.mod` file's records: `Edit(id)` (a change of a loaded record, returned to
  fill), `Add(id, type, name)`, `Remove(id)` and `Apply(FcsRecord)`, allowed only during `EditData` (later they throw). They are queued; when
  `EditData` returns they are copied and checked whole (an id on each, no null name, value, reference target or instance), then merged in
  order by the
  record rules of a data file listed after every loaded one ([fcs-mod](formats/fcs-mod.md#merging); the loader's byte rules for reading a
  file do not apply), counted as made by the mod's own `.mod` file (`ModifiedBy`, problems in `Issues`), which is not listed again in
  `GameDatabase.Files`. Writing to `Data` directly is unsupported: it is not checked, counted or in the navmesh key. Not offered: `Harmony`
  (patch in `Initialize`), saved settings and `Dependency<T>` (a framework cannot serve its dependents here). `Edit` and `Remove` take only a
  record loaded before the mod: change a record the mod adds on the object `Add` returned, and check `Data.Find` before changing one an
  earlier mod may have removed.
- A throw in `EditData`, or a change that fails the check, merges none of the mod's changes and leaves the mod out for the session (a notice,
  as a refused mod). A mod whose required dependency was left out or skipped there, directly or through others, is skipped and then refused.
  Changes are never undone: when a mod (or a dependency) is refused later, in `Initialize` or its checks, its merged changes stay for the
  session.
- Merged after the translations: a record a mod adds or renames is not translated, not even by its own translation file, and a rename sets
  the shown name only: on a record a translation renamed, the name the engine finds records by (`BaseName`, which it uses for the records it
  finds by a fixed id or name) keeps its earlier value, as when a mod's data file renames a record the core translation renamed. The gear
  blueprints (`<id>.TECH.1`) and wall masters are made after the phase, so they cannot be changed in it; an `<id>.TECH.1` a mod adds is kept
  in place of the blueprint.
- Without any enabled code mod whose entry declares `IModDataPhase`, nothing changes: the data loads as before and mods load in
  `BootSimulation` as described above. With one, every enabled code mod's assemblies load and its entry type is constructed while the data is
  read, before the scene, renderer and population exist (an entry constructor must not need them), including a mod refused later.
  `Initialize` keeps its order and place, each mod checked again against the mods initialized before it, so a dependency that fails to
  initialize refuses its dependents as without a data phase (an optional one that failed is dropped from `Dependency`, though the mod's
  assemblies stay bound to its exports). One difference: of two mods with one id, or two incompatible ones, the later is refused once the
  earlier's code loaded, even if the earlier then fails to initialize.
- `GameDatabase.CodeEdits` holds a fingerprint of each mod's merged changes (the same on every machine and in every language for the same
  changes); the navmesh cache key takes them ([simulation](simulation.md#navmesh)) as it stamps the data files, so a mod whose changes differ
  from one start to the next rebuilds the navmesh at every start.
- The game's own runs read the data with the phase; `--no-population` runs it but no `Initialize` (no population is made),
  `--list-starts` runs it before listing the starts, and
  `--data-check`, the launcher and the developer tools read the data files without it.
- Built from the records before `Initialize` (so only the data phase reaches them): the start's place and the scene's objects, the
  renderer's tables, the level data's building swaps, ruins and layouts, the navmesh, the population's towns, factions, relations and
  biome groups, the town buildings, sight and resource rocks, rigs and animations. Made on first use after `Initialize`
  (`PopulationData.Ai`, `Dialogue`, `Combat`, `Items`, `Bodies`, `Surgeons`) or read at each use (squad templates when a squad is made).

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
- **Crimes:** a registered system implementing `ICrimeObserver` (`Meitou.Simulation.Crime`) receives `CrimeNoted(World, in CrimeEvent)` for each
  change the crime rules make to a character's pending crime or bounties ([rules](simulation.md#crimes-and-bounties)), by `CrimeEventKind`:
  `Pending`, a crime set on a culprit (every code the game sets, among them the assaults it notices: an unprovoked attack order, an NPC's landed
  blow or aimed bolt, a stealth knock-out swing, a hit between the police and another); `Witnessed`, the first witness to react; `Booked`, a bounty
  raised (a witness's or a fence's booking, or a dialogue line assigning one: the speaker is the witness, the line's amount is added, and the code
  and victim are those of the target's pending crime, which the line marks booked, if it has one); `Lapsed`, a pending crime ended, booked or not
  (its timer ran out, a crime of another code replaced it, or the character was recruited, bought free, dismissed or stored off screen with its
  squad, the dead with it); `Expired`, a bounty run out; `Cleared`, a bounty removed otherwise (a fine or a bail paid, a release from prison,
  dialogue, the debug menu). Each once: a crime already pending with the same code is only refreshed and not reported again, and an assault by a
  culprit with another crime pending only raises that crime's timer. So every `Pending` is followed by one `Lapsed` unless the character leaves the
  world first (a body that decays or is unloaded, a squad despawned: its crime goes with it; a dead character's crime otherwise stays frozen), and
  every bounty raised in the world is `Booked` before it is `Expired` or `Cleared`; an ending is per faction entry, so several bookings may precede
  one. A character brings the bounties it has when it enters the world (a save's, a squad loaded again, an NPC's rolled spawn bounty) with no event,
  and character ids are given anew at every load, so a mod reads `CharacterCold.Bounties` and `CharacterCold.Pending` for a character it has not
  followed (new characters since its last look by `CharacterCold.SpawnedTick`), and ignores an end it never saw begin. The event names the tick, the
  culprit, the `CrimeCode` (none for an expiry or a clearing, whose entry holds several), the faction (the victim's, the witness's, the booking
  faction's or the entry's), the bounty in all for a booking, an expiry or a clearing, the witness, and the pending crime's victim, its faction and
  the building robbed (`VictimHolder`; none, -1 and null otherwise). Observers run serially in system order once the crime system has made the
  change, inside the step that made it. During a tick that is on the thread running it (the combat commit, the crime and sight sweeps, task starts,
  the carry, lock, arrest and prison steps). The paused interface's commands run between ticks on the host's thread, and their events carry the
  coming tick's number: the dialogue window pauses the game, so a bounty a line assigns or clears, a line's crime alarm, recruiting and buying a
  character free normally arrive this way, as do character trades, squad commands (a dismissal), a theft by hand and a fence's sale while paused.
  Observers may read the world and change their own hashed state, and gameplay changes go through commands. One that throws is disabled for that
  world and the fault enters the state hash. Without an observer no event is built. `CrimeSystem.BookingsOf(tick)` still lists the tick's bookings
  that had a witness (the arrest system's trigger). Engine API, not in the original.
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
- **Deaths:** a registered system implementing `ICharacterDeathObserver` (`Meitou.Simulation.Bodies`) receives `Died(World, in CharacterDeath)`
  once per death: every loaded character's, whatever killed it (a blow, bleeding out, hunger, a wound worsening, a death item taken, being
  eaten, the rack, the debug kill, another system's kill), and each member of an unloaded platoon an off-screen fight kills (the winner's
  losses and the whole losing platoon; a winner whose members were never made loses no one, a loser never made counts its size). The payload
  names the tick, the dead character (none off screen, where the member is no character), its record ("" for an off-screen member never made),
  faction (the platoon's off screen) and platoon (-1 none), the medical `DeathCause` (`None` off screen), the `DeathSite` (melee, bolt,
  execution, butchered, eaten, rack, off-screen fight, or other; `Blow` and `OffScreen` read it), and the killer with its faction: the attacker
  of a killing melee blow, bolt or execution (it may have left the world since; its faction is then -1), the butcher of a body that died of a
  death item taken, the eater of a captive eaten alive; none (and -1) for bleeding, hunger, a wound worsening, the rack or the debug kill,
  whoever hit the character before (no credit rule). An off-screen death names no killer but the winning platoon's faction. Observers run
  serially, never during a parallel phase, in system order: at once where a blow, a butchering, a meal, the rack or an off-screen fight killed;
  else in a sweep in slot order after the parallel phases (the body's tick, and the inputs' debug kill) and again after the slow world, or as
  the dead character leaves the world, whichever comes first. So the order never depends on the thread count, but it is not the order of death
  within a tick (a debug kill in the inputs is told after the bolts of the schedule). During a tick the call runs on the thread running the
  tick; the paused interface's commands between ticks (the dialogue window's choices, character trades, squad, item and trade commands) tell
  theirs at once on the host's thread, with the coming tick's number: in the stock game, a death item taken by hand. The dead character is
  still in the world's table during the call (a member of a squad being stored off screen has its in-world state, such as its AI and pending
  crime, already cleared); a body loaded dead is never reported. Callbacks may read the world and change their own hashed state, and must not
  change characters or other world state (gameplay changes go through commands). One that throws, or that removes the dead character itself,
  is logged and disabled for that world (the removal it made stands, and is not made twice); faulted registrations enter the state hash.
  Without an observer no payload is made and no death watch is kept. Engine API, not in the original.
- **Experience:** a registered system implementing `IExperienceFilter` (`Meitou.Simulation.Bodies`) is asked `Gain(World, in ExperienceGain)` for
  every experience gain the game makes to a character in the world (every `XpService` entry: combat, injuries, ranged and turrets, moving, sneaking,
  assassination, thievery, lockpicking, building, machine work, training dummies, first aid, mass combat) and for a direct `CharacterStats.Gain` or
  an `XpService` call without a source (`ExperienceSource.Other`): the tick, the character, the stat, the amount and its `ExperienceSource`. It
  returns the amount to add, so a mod scales experience by squad, faction, stat or source, or cancels it (0). Filters run in system order, each
  seeing the amount the earlier ones left, until one answers 0. For a gain through the core curve, the amount is the one the curve takes, after the
  Squad XP and Others' XP setting; only an amount in the curve's range (above 0, at most 20) is asked, since the curve adds nothing for any other (a
  raw amount above 20 is dropped and a filter cannot bring it back), and the answer is held as that setting is: to 20, and with no raise for a stat
  at or past a training dummy's cap. First aid and mass combat add straight, past the curve: their amount is the one added, and the answer is held
  as the setting is there too: no raise for the stat first aid trains (medic, or robotics with a robot kit) at or past 100, none needed for mass
  combat (nothing reads it). A character's stats are bound to it as it enters the world and cleared as it leaves, so its gains are filtered from its
  first step. Gains happen in the parallel phases too, so a filter runs on any thread, concurrently: it must be quick, thread-safe and a pure read
  of the gain and of world state the tick has settled (its own system's serial step, the last finished tick), and must not grant experience itself.
  The paused interface's commands between ticks (the dialogue window's choices, character trades, squad, item and trade commands; in the stock game
  a theft's or a fence's thievery) ask it on the host's thread, with the coming tick's number. Mod skills (`ModSkill.Gain`) are not asked. A filter
  that throws, or answers an amount that is negative or not finite, leaves that gain as it came and is logged once; it is still asked for the rest
  of that tick and not from the next, and the fault (with its tick) enters the state hash. Without a filter nothing is bound or built. Engine API,
  not in the original.
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
- **World states:** `AddWorldState(record)` claims a WORLD_EVENT_STATE of the merged data (`ModWorldState`, `src/Meitou.Simulation/Factions`; a
  record of another type, or one another mod claimed, refuses the mod, and so loses all its registrations, not only the claim: two mods that
  claim the same state never run together). The mod's system sets its answer in a world with `state.Set(world, bool)` through the handle
  `AddWorldState` returned, in a serial phase (or `IWorldReady`), drops it with `Clear(world)` and reads it with `Value(world)` (null: none
  set), also only in the tick's serial phases (interface code reads a copy the system publishes). Only that handle sets values: one made any
  other way (its constructor, another mod's record) sets nothing and logs once. While a value is set, every reader of the state in a world with
  the population gets it in place of the record's conditions: dialogue lines' world states, SQUAD_TEMPLATE and spawn-list `world state` gates
  (`SpawnPlatoon` too), TOWN `override town` candidates, FACTION_CAMPAIGN triggers and nests
  ([world states](game/factions-squads-towns.md#relations-and-world-states)), from the population's next refresh of the states: that tick's
  slow-world step when the setting system runs before the population's, otherwise the next tick's (`SystemAnchor.End` is after it). It
  changes decisions made from then on; one already made stays: a nest laid out (nests are laid once per world), a town already replaced by an
  override, a squad already spawned, a campaign already started, and a new game's starting squads, planned before `IWorldReady`.
  Without a value, and wherever the mod does not run (refused, removed, or the original game reading the same data), the record's own
  conditions answer again, for decisions made from then on. Meitou answers a record without conditions as holding and one with a
  contradiction (`NPC is` and `NPC is NOT` the same CHARACTER at the same state) as never holding, which is how a data author makes a state
  that stays off without the code; this follows from the original's rule (**Observed** by code there) but was not tested in the original.
  Several states combine as the readers' lists already AND their entries.
  The base game's own states may be claimed. While the mod has a value set, the original's content that tests the state (its dialogue, squads,
  town overrides, campaigns, nests) follows the mod, and what it decides then is written into the save like any other decision: a town
  replaced by its override stays replaced (in the save's town replacements), squads it spawned and campaigns it started stay, after the mod
  is removed and when the original game loads that save. Setting a story state is therefore a change to the player's world, not a preview.
  Values live in `World.ModStatesOrNull` (read-only; made at the first value), enter the state hash while any is set, and are saved in the
  mod's sidecar under the entry `meitou world states` ([save](formats/save.md#mod-data)), which no mod system may be named. At a load they
  are restored after the mod's own systems' `IModSaveData.Read` (which sees none of its mod's values yet; an earlier mod's are already back)
  and before `IWorldReady`; a value of a state the mod no longer claims is dropped. Claiming changes nothing else in the mod's sidecar: it is
  written as a mod's file without claims is (the present systems' entries, or, when the mod has no saveable system in this world or a
  system's write fails, the earlier entries of the systems it still registers), plus the states' entry. Without claimed states nothing is
  made, hashed or saved. Engine API, not in the original. A dialogue line can also test a mod's own value directly through a condition code
  (`AddDialogueCondition`).
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
  go through this step; their AI packages run them.
- **Task handlers** (`Meitou.Simulation.Ai`, [task starts](simulation.md#task-starts)): a registered system implementing `ITaskOwner` gets
  `RegisterTasks(TaskHandlers)` once when the world is made, in system order, and registers an `ITaskHandler` (`Start`, `End`, `Done`) per
  `CharacterTask`, per row (`Register(TaskType, handler)`, a mod's row from 1000 up) or per row on a site (`RegisterOnSite`). One handler per
  key: a later registration replaces an earlier one, so a mod system replaces a standard handler only when it sits after that handler's system.
  Handlers run in the serial phases only (`TaskStart.Start` throws in a parallel one).
- **Goal claims** (`IGoalSource`, [claims](game/ai-tasks.md#perma-jobs) "Claims"): a registered system implementing it adds the (worker,
  target) pairs it acts out now in `Publish(World, SquadGoals)` with `goals.Add(squad, worker, GoalTarget.Of(body) or GoalTarget.At(site id))`.
  It is asked each time a finder takes a snapshot (`SquadGoals.Of`, serial) and once at the end of every tick; a pair stays claimed 3 s after
  it was last published (`World.Claims`). The finders that skip what a squad mate already works on (NPC looting, the medic and splint finders,
  captive gathering, production errands) then skip the mod's targets too. `Publish` only reads the world, in a fixed order: the claims steer
  later starts, so they must be the same on every run.
  Neither hook is caught as a mod fault outside the mod system's own phases: a throw from a handler or `Publish` called by another system's
  step escapes that step.
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
  tick. `Read` runs only when the save has data for the system. Vanilla save files stay byte-exact. Data kept per character is keyed by the
  characters' lasting ids (Character ids, below), not by `CharacterId`, which is new for every world.
- **Character ids:** `world.LastingIds.GetOrGive(character)` gives a character a `LastingId` (a non-zero 64-bit value) the first time any mod asks
  and returns the same one after: it stays with the character for life, across saves and loads, squad moves, recruiting, being taken captive and
  renames, and while an NPC's platoon leaves the world and comes back, where `CharacterId` is new for every world. All mods share the one id, so
  each keys its own data by it. `world.LastingIds.Find(id)` is the character in the world now, dead or alive, else none (never given in this line of
  saves, its squad off screen, or gone for good); `world.LastingIds.KeptIds()` lists the ids of every character the game still keeps, dead or alive
  (in the world, in a squad stored off screen, in a loaded save's record still kept), so an id a mod holds that is not among them belongs to a
  character gone for good (killed and removed, its platoon despawned or wiped out) and its data may be dropped; it scans the world, so call it now
  and then (after a load, once a game day), not per character per tick. `CharacterCold.LastingId` and `CharacterSnapshot.LastingId` read the id
  without giving one (none until asked), the snapshot's for interface sources. `GetOrGive` runs on the thread that ticks the world, between ticks or
  in a serial phase; it throws in a parallel phase and during a save, from the capture until the next tick (which covers `IModSaveData.Write`),
  whether or not the character has an id, so saving never changes the world's ids or loses one. A mod therefore gives the id when it first stores
  data for a character and reads `CharacterCold.LastingId` in its `Write` (a throw there is logged and that mod's data is not written). Ids are
  deterministic: the high half is chosen once from the world seed and the game hours, the low half counts up, and both are kept with the save, so no
  two characters of one game's line of saves share one. Two saves branched from one may give the same id to different characters made after the
  branch, and nothing tells branches apart, so a mod that keeps data outside the save (one file across saves) cannot rely on ids there. Saved in
  `meitou-mods/character-ids.json` ([save](formats/save.md#character-ids)); after a load a player character has its id when `IModSaveData.Read` and
  `IWorldReady` run, an NPC when its platoon is made. A character the save does not write loses its id, which is never given again. An import starts
  with none. Until a mod asks (or a save with ids loads) nothing is held, hashed or written; then the ids enter the state hash. Engine API, not in
  the original.
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
  itself (in its `IModSaveData` to keep them across a load). A marker may draw a picture of its own instead: `Image` (an init property, set
  as `new UiMapMarker(...) { Image = "icons/camp.png" }`) names an image file by a path relative to the mod's folder (`context.Folder`); a
  rooted path or one leading out of the folder is refused. The file must be a PNG or DDS file of at most 8 MB and 1024 pixels a side. It is drawn
  tinted as a group's is and stretched to `Group`'s image size when `Group` names one of the set, else to 32 by 32 pixels (`Group` empty), times
  the size; it shows once read (a frame or two after it first appears), and its texture is freed once no marker names it. A mod may name at
  most 64 different files in a session. A marker whose image is refused, missing or cannot be decoded is left out alone, with one log line per
  file and reason; the mod's other markers stay, and a refused file is looked at again a second later (a file the mod writes while the game
  runs). Several mods may each use their own files at once. A source that throws or gives a marker with a group the marker set lacks (with an
  image: a group neither empty nor in the set), a point, tint or size not finite, a size not above 0 or a zoom level outside 0 to 2 is logged
  once and dropped.
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
- **Key actions:** `AddKeyAction(name, label, pressed, defaults)` (`ModKeyAction`, `src/Meitou.Game/Host/Input`) adds an action the player binds
  like the game's: a row `label [mod name]` on the settings' Controls page after the game's rows, in load order, rebound there as theirs are
  (a key taken from another action, the game's or a mod's, leaves it, with the same notice; Delete or Backspace unbinds). At start the game's
  bindings are laid first (the player's saved ones included), then each mod action's saved keys, in load order, then the `defaults` (keys
  `Key.G`, buttons `MouseButton.Button4`) of the actions with nothing saved, in load order; a key another action holds by then is left out with
  a log line, so neither a default nor a returning mod takes a key the player chose. Escape, F6, Alt, Ctrl and Shift as defaults refuse the
  mod; Insert (the console on the Vulkan host), Delete and Backspace (the page's unbind keys) are left out with a log line, and the page
  refuses them for a mod's action as reserved. Restore defaults lays every action's defaults again, logging those left out. The player's
  changes are kept in `meitou.user.json` beside the game's (`modBindings`: mod id, `name`, keys as in `bindings`; an empty value unbinds): an
  action whose keys were not set on the page since the start keeps the entry it had then (none stays none, even when a saved key was left
  out); one set there (rebound, unbound, or its key taken for another action) is written as its keys, or dropped when they are the mod's
  defaults in any order; Restore defaults drops them; a running mod's entries for actions it no longer has are dropped while it still has a
  key action (a mod updated to have none keeps them); a mod not running keeps its entries; and `modBindings` is not written while it is empty.
  A key a mod's action took from a game action stays off that game action after the mod is removed, as the game's own saved bindings say. A
  press reaches it only when the game view gets the key: not while a text field, the console, the settings, the inspector or the interior
  editor has the keys, nor over the title or a loading cover; it does arrive under a window without text input, with the F10 menu open while
  it takes no text, in the character editor, and for the mouse's extra buttons in photo mode, as the game's own actions do; a numpad key bound
  to one is not the cinema camera's. On the Metal host F7, F8, F11, F12 and Print Screen are the view's own keys and never reach an action.
  A binding is one key or button with no modifier, as the game's own bindings are in Meitou: the action fires whatever Shift, Ctrl or Alt is
  held, and the press does not say which (the host's own combinations, Alt+Enter, Ctrl+F9 and on the Vulkan host Shift with F1, F2, F11 or
  F12, never reach the bindings).
  Once per press, also while paused, `pressed(ModKeyContext)` (the `Snapshot` on screen, `Selected`, `Tick`, `Enqueue`) runs on the host
  thread at the control tick, so it must not touch the world, must not block, and queues commands for gameplay; `action.Held` is its state at
  the last control tick, for host-thread readers. A throw is logged with the mod id and the name and that action gets no more presses.
  `name` (1 to 48 letters, digits, `_ . -`, unique within the mod) keys the saved keys; a bad name, or a label that is empty or blank, over 48
  characters or on more than one line, refuses the mod. The actions are numbered `InputAction` from `InputBindings.FirstModAction` (1000) in
  load order, for this session only. Without key actions the bindings, the page and `meitou.user.json` are the game's. Engine API, not in the
  original.
- **Squad AI tab options:** in `Register`, `registration.AddSquadAiOptions(context => options)` adds tick buttons to the squad overview's AI
  tab, after the game's own and above Follow Formation; the tab's lines shrink to fit. The source runs on the host thread on every layout pass
  while the tab is open, so it must be cheap; a tick may be in flight, so it must not touch the world. It gets `UiSquadAiContext` (the
  `Snapshot` on screen, `Tick` read when asked, `Enqueue`) and returns `UiSquadAiOption`s: caption, tooltip, whether it shows ticked, and the
  press. The option's state is the mod's own: a press queues a command, its system applies it in a serial step and saves it (`IModSaveData`),
  and the system publishes the value (a volatile field, set again after a load) for the source to read. A source that throws, or returns an
  option without a caption or press, is logged once and dropped for the session; a press that throws is logged.
- **Building panel buttons:** in `Register`, `registration.AddBuildingActions(context => buttons)` adds button lines to the panel of a selected
  building, placed piece or piece of furniture, after the game's own stats lines (Open/Close, Security, Open to public), one line each, in
  registration order (mods in load order), so several mods share the panel. Each is drawn as the game's own neutral action lines are (the
  button across the line, its caption in the panel's main colour). The panel holds about six lines at the stock layout and 1080 pixels, the
  building's own included; lines past its lower edge are cut off, neither drawn nor clickable, so keep to one or two. The
  source runs on the host thread whenever the panel is arranged, which while a building is selected can be every frame (each tick, each mouse
  move, at least 20 times a second), so it must be cheap and read only its context; a tick may be in flight, so it must not touch the world.
  It gets `UiBuildingContext`: `Building` (`UiSelectedBuilding`: `Instance`, the key the simulation
  knows it by and `world.Player.InspectedBuilding` holds; `Record`, its record's string id or null; `SiteId`, the player's building id or null;
  the panel's `Name`; `Owned`, whether the player's faction owns it, as the game's own buttons show), the `Snapshot` on screen, `Tick` read
  when asked, and `Enqueue`. It returns `UiBuildingAction`s (caption, tooltip or null, press); return none to add nothing for that building.
  Captions and tooltips go through the translation catalog, as the game's own (a text the catalog lacks shows as given). A press plays the
  panel's click and runs only while the building it was shown for is still selected and still exists (the game's own buttons' check); it changes
  the world only by queueing commands, which the mod's system applies in its serial `Inputs` step (`Instance` names the building). By then the
  building may be gone, sold or dismantled (a press in the same frame as the game's Dismantle), so the system checks that `Instance` still exists
  and still qualifies before acting. A source that throws, or returns a
  button without a caption or press, is logged once and dropped for the session; a press that throws is logged. Without a source the panel
  is as before. Engine API, not in the original.
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
- **Carry capacity:** `cold.Stats.CarryModifiers.Set(key, term, amount)` / `Remove(key, term)` / `RemoveAll(key)` (`CarryModifiers`,
  `src/Meitou.Simulation/Bodies`) change a character's carrying capacity under keys of the mod's own, for race traits and gear (a frame pack's +20 kg)
  without touching strength, which also drives damage, blood and training. `CarryTerm.Scale` amounts add a fraction of the game's capacity
  (0.25: a quarter more), `CarryTerm.Kilograms` amounts add kilograms; the capacity becomes the game's own times (1 + the scales) plus the
  kilograms, never below 0 (each sum is held within plus or minus 1,000,000 kg and 1,000 times). A capacity of 0 (scales adding up to -1 or
  less) leaves a character carrying anything unable to move. The game's own is `carry weight mult` x strength x its injury multiplier +
  `encumbrance base` ([stats](game/character-stats.md), "Encumbrance and carrying"), so the modifiers act after strength and its injuries. Every
  reader takes them: the encumbrance factor (run speed, hunger, the strength and athletics training it drives, combat), the inventory window's
  maximum and burden word, and the "Max carry weight" lines of the HUD and the stats window. The window's maximum has no `carry weight mult`
  (as in the game), so a scale adds fewer kilograms there than it gives the factor. Keys are shared by every mod: prefix yours with your mod id
  (`context.Id + ".pack"`), and amounts of different keys add up. It enters the state hash once one is set, and is not saved: keep it in the
  mod's `IModSaveData` (or work it out again from worn gear) and set it again after a load, for example in `IWorldReady`. Change it, and even
  read `CarryModifiers` (it is made on first use), only from the mod's system outside the parallel phases. A new body starts without modifiers.
  Engine API, not in the original.
- **Sounds:** `world.PostSound(eventName, characterId)` plays a named sound event on a character, `world.PostSound(eventName, point)` at a world
  point and `world.PostInterfaceSound(eventName)` on the interface (heard alike wherever the camera is), each with an optional switch
  (`switchGroup`, `switchValue`); `ModContext` has the same three calls for code off the tick (an interface press), sent to the running world.
  The event is any the loaded banks hold, by name: the game's ([audio](audio.md): `Impact`, `Character_Lock`, `Notifications`...) or a mod
  bank's loaded through `RE_Kenshi.json` `SoundBanks` ([mod sound banks](audio.md#mod-sound-banks)). Display only: the call queues the event
  for the host as the game's own sounds are queued (nothing without audio, as headless), the simulation never reads it and it is not hashed,
  so a system may call it in any phase and from any thread. The host plays it in the frame, never setting a switch on an object of the game's,
  so the game's own voices, footsteps and impacts keep their switches: a character event without a switch goes on the character's own sound
  object and follows it; one with a switch goes on one of eight pooled objects of the mods' own, placed where the character is; a point event
  goes on one of those placed at the point; an interface event on an unplaced object of the mods' own. A character event plays only while the
  character is near enough to have a sound object (`CharacterSounds.TrackRadius`) and within 1000 units of the camera, a point event within
  the same 1000 units. The pooled objects are shared by every mod and reused round robin, and a switch stays on an object until changed, so
  set the switch on every post that needs one. Post one-shot events: a looping one is never stopped, and on a pooled object it can be moved
  by a later post. An unknown name plays nothing (the player gives up on it after ten seconds). Several mods post freely. An empty name or a
  point that is not finite throws, also before the first world; an empty character id plays nothing. Engine API, not in the original.
- **Status effects:** `AddStatusEffect(new StatusEffect(id, name) { Description, Tone, Stacking, MaxStacks, Modifiers })` (`StatusEffects`,
  `src/Meitou.Simulation/Bodies`) adds a named effect (a poison, a drug, a blessing) the mod's systems put on a character with `effect.Apply(world,
  character, hours, stacks)` (positive infinity: until removed) and take off with `Remove(world, character)`; `On(world, character)` or `On(stats)`
  reads it (ticks left, -1 forever, and stacks) and `stats.EffectsOrNull` lists every effect a character holds. Applying it again follows
  `Stacking`: `Refresh` starts the time again, `Extend` adds the hours, `Stack` adds stacks up to `MaxStacks` and starts the time again (a stack
  count read from a save is held to the same rule). While it lasts its `Modifiers` (stat, amount per stack; the list is copied at registration) are
  set through the character's [stat modifiers](#registration-surface) under the key `effect:<id>`, so they work and display as any modifier (a mod's
  own modifier key must not start with `effect:`). It ends by itself on the tick its time runs out, counted in ticks of game time after the slow
  world, only while its character is in the world (an NPC whose platoon is off screen keeps its time; a corpse's counts on). The HUD's State line
  shows the effects of a selected or inspected character: where it would read Normal, the first effect's name (in registration order) in its tone's
  colour, with "+N" for N more; its tooltip lists every effect with its stacks, game time left and description, after the line's own text. Any other
  state (dead, caged, unconscious, sleeping, aiming or reloading, defensive, a weather notice) keeps its word, so the effects are then in the
  tooltip only, and in a cage without a lock, which has no State line, nowhere. Saved with the character in its STATS record
  ([save](formats/save.md#compatibility-exceptions)), so it follows the character through squad moves and recruiting, and set again with its amounts
  at a load. An effect of a mod not running now is kept and saved again, not shown, adding nothing, its time standing still;
  `StatusEffects.Forget(stats, id)` drops any id, registered or not (an effect a mod renamed, or one of a mod that is gone). Effects enter the state
  hash while a character holds one. Apply, Remove and Forget run from the mod's system outside the parallel phases, as stat modifiers. `Forget`
  drops any mod's effect, by design (it is the cure for orphans), so a mod forgets only ids it knows to be its own or orphaned. Refused: a bad id (1
  to 48 letters, digits, `_`, `.`, `-`), an id another effect has (case-insensitive), a blank name or one over 32 characters, `MaxStacks` below 1,
  an unknown tone or stacking, a null list or description, or a modifier on a stat without storage, on a storage another of its modifiers names
  (Medic, HiveMedic and Vet share one), or whose amount, or amount times `MaxStacks`, is not finite. With no effect registered the tick step does
  nothing, and a save without effect keys makes, saves and hashes nothing. Engine API, not in the original.
- **Commands:** a mod's own `SimCommand` types, queued with `Enqueue` and handled in its system's `Inputs`. Every system's `Inputs` gets every due
  command (`World.RunInputs`), so a system picks its own by type. A type is its load context's: each mod loads its assemblies into its own context
  ([loading](#loading)), so another mod's command type, even from a copy of the same DLL in another mod folder, is a different type and does not
  match. A mod whose commands another mod's system should handle shares the type through a [dependency](modding-dependencies.md); otherwise each
  mod handles its own commands in a system of its own.
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

## Data mods

Some shapes need no code: a race's anatomy of any length, extra or missing parts, and parts of a new type such as a tail are plain records
([bodies of other shapes](game/character-stats.md#bodies-of-other-shapes-meitou)).

## Fixed record ids

The engine finds these records by string id, or by name (the name before any translation, `GameRecord.BaseName`; marked "by name"). A data
mod that changes the game (a conversion) edits them in place rather than removing or renaming them. "Original" means the original game uses the
same id or name: it is a literal string in `kenshi_x64.exe` 1.0.68 (**Observed**), and the linked doc gives its use and how that was
established. Where the last column is filled, it is what Meitou does without the record.

| Id | Record | Used for | Source | Without it |
| --- | --- | --- | --- | --- |
| `204-gamedata.base` | FACTION Nameless | the player's faction | original ([factions](game/factions-squads-towns.md)) | a new game throws |
| `45550-gamedata.base` | SQUAD_TEMPLATE "startoff- Wanderer squad" | a start that makes nobody | original ([ui-screens](game/ui-screens.md)) | |
| `5359-gamedata.base` | RESEARCH `_Default Start` | a start that lists no research | original ([buildings-production](game/buildings-production.md)) | |
| `5369-gamedata.base` | DIALOGUE_PACKAGE | a player character's dialogue without its own or a faction's | original ([dialogue](game/dialogue.md)) | |
| `1233-gamedata.base`, `5006-squad.mod` | FACTION Tech Hunters, SQUAD_TEMPLATE "Wandering Squad" | a dismissed character's faction and squad | original ([ui-screens](game/ui-screens.md)) | without `1233`, the faction `nofac` |
| `nofac` | FACTION No Faction | a dismissed character's faction without `1233`; a building's owner when none | original ([buildings-production](game/buildings-production.md)) | |
| `3943-gamedata.base` | FACTION Wildlife | a spawned squad's faction when nothing names one | original ([factions](game/factions-squads-towns.md)) | no faction |
| `518-gamedata.base` | FACTION Cannibals | their cages' tutorial | original ([ui-screens](game/ui-screens.md)) | |
| `5344-gamedata.base` | AI_PACKAGE "Slave only squad" | a slave split off a squad | original ([factions](game/factions-squads-towns.md)) | |
| `1866-gamedata.base` | ITEM Raw Iron | the furnace's iron measure | original ([buildings-production](game/buildings-production.md)) | |
| `580-gamedata.base` | ITEM Building Materials | a BUILDING's `build materials` cost | original ([buildings-production](game/buildings-production.md)) | |
| `584-gamedata.base` | ITEM Fabrics | armour's `fabrics amount` | original ([buildings-production](game/buildings-production.md)) | |
| `RESEARCH_TEMPLATE`, `BLUEPRINT_ITEM_ARMOUR`, `BLUEPRINT_ITEM_GEAR`, `BLUEPRINT_ITEM` | RESEARCH, ITEMs | the gear blueprints' research and their look | original ([buildings-production](game/buildings-production.md)) | research without the template's values; the look `BLUEPRINT_ITEM`, without it no blueprint item |
| `1057-gamedata.base`, `1058-gamedata.base` | WEAPON_MANUFACTURER and model | the weapon factory's fallback | original ([factions](game/factions-squads-towns.md)) | the picked manufacturer |
| `913-gamedata.base` | weapon model | a weaponsmith's model name with none found | original ([ui-screens](game/ui-screens.md)) | no name |
| `917-gamedata.base` | WEAPON_MANUFACTURER | a CHARACTER without `weapon level` | original ([characters](characters.md)) | no weapon (a loadout note) |
| `5460-weather.mod`, `18003-gamedata.base` | WEATHER Default, BIOME_GROUP NONE | a season without weather; the region never named | original ([weather](formats/weather.md), [ui-screens](game/ui-screens.md)) | |
| `360-gamedata.base`, `742-gamedata.base` | MATERIAL_SPEC | a building part's and a town's material fallback | original ([runtime-materials](formats/runtime-materials.md)) | |
| `5370-platform_otto.mod` | BUILDING_PART | the character editor's platform | original ([characters](characters.md)) | no platform |
| `55393-Newwworld.mod`, `95425-sfx.mod` | EFFECT, AMBIENT_SOUND | corpse flies | original ([character-stats](game/character-stats.md)) | no flies |
| `GLOBAL CONSTANTS` (by name) | CONSTANTS | every game constant | original ([character-stats](game/character-stats.md)) | the first CONSTANTS record |
| `blank squad` (by name) | SQUAD_TEMPLATE | a new squad of the player's faction | original ([localisation](formats/localisation.md)) | the squad names the template `blank squad` with no record |
| `player_town_template` | TOWN | the player's outposts | original ([save](formats/save.md)) | foliage cleared 2000 round an outpost |
| `blank personality` | PERSONALITY | a character with no personality of its own or its faction's | original ([dialogue](game/dialogue.md)) | none |
| `4442-Newwworld.mod` | BIOMES ForestLand | its ground relief at a third of the depth | Meitou (render choice) | nothing scaled |
| `44428-rebirth.mod` | ITEM Empty Rum Bottle | its bottle drawn empty | Meitou (render choice) | drawn as other bottles |
| `3384-TwoStorey.mod` | BUILDING Storm House | the bar behind the character editor | Meitou | no bar (a log line) |
| `44856-rebirth.mod` | SQUAD_TEMPLATE | that bar's barman and guard | Meitou | none |
| `18919-Newwworld.mod` | TOWN The Hub | that bar's patrons (its `bar squads`) | Meitou | none |

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
  `IModDependents`; an API 1 mod loads unchanged. `gameVersion` ties a mod to the engine releases it was built against. The
  [data phase](#data-phase) (`IModDataPhase`) adds no manifest key; a mod using it names the releases that have it in `gameVersion`.
- Harmony patches non-public members; compiling against them takes the [reference package](#reference-package)'s publicized set, or a
  publicizer package (BepInEx.AssemblyPublicizer.MSBuild with `Publicize="true"`) on a release folder's assemblies. Compatibility is per game
  version ([Why this shape](#why-this-shape)): public names change too, as after v0.17.1, and internals change without notice.

## Platforms

- Managed DLLs run unchanged on the Windows, Linux and macOS builds.
- **Observed** (Harmony 3.0 RC notes): native async methods cannot be patched. The game runs on .NET 11, so this applies to any
  method compiled as runtime-async.
- **Observed** (2026-11-18, scratch app, win-x64, .NET 11.0.0, Lib.Harmony 3.0.0-rc.7): patching a generic method instantiation
  (`Method<int>`) throws `NotSupportedException`.

## Evidence

**Observed** (2026-11-04, win-x64, a scratch mod and harness against the release build, outside the repo, run as the game runs: tiered
compilation and PGO off, server GC): only allowed mods with a valid
manifest load and each refusal gives its notice; the process holds one `0Harmony` and one `Meitou.Modding` though the mod folder had its
own `0Harmony.dll`; a prefix applies, and a throwing `Initialize` loses its patch and registrations; a mod system's state survives
capture and restore; sidecars survive `SaveGame.Write`/`Load` and `Clone`; spliced systems land at their anchors and the standard list is
unchanged. The dialogue hooks are built but not exercised. **Verified** by `ModdingTests`: game assemblies resolve from the game, anchors,
sidecar round trip, and a hashed mod system agreeing at 1 and 4 threads.
