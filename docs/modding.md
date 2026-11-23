# Code mods (C#)

**Status: implemented, code-mod API version 3 (1 and 2 still run; 2 adds [dependencies](modding-dependencies.md), 3 the chooser surface).** Meitou's own in-process loader,
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
  and logged before the first world; a queued order is the player's, so it leaves out the player's characters getting up, `OrderGiver`),
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
  tick are applied. A throw from one of its task handlers, its `RegisterTasks`, its `IChooserOwner.Register` or its `IWorldReady.Ready` stops
  it the same way; its other hooks (observers, filters, the goals and job contributors it registered) keep their own rules. `IStateHashed` puts a
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
- **Visual effects:** `PlayEffect(effect, EffectAnchor anchor, seconds = 1, tint = null)` on `ModContext` plays a display-only particle
  burst, e.g. from a hit feed. `effect` is an EFFECT record's string id (its `particle system`) or a Particle Universe system name;
  `HasEffect(effect)` says whether one is loaded now (false before the particles load, while they are off, or off the host's thread), without
  a logged rejection. `seconds` is how long it emits, in (0, 30], on the particles' frame clock (still while paused, faster at higher
  speeds); `tint` replaces the particles' hue (rgb, 1 the full channel, up to 4), as a race's blood colour does. Anchors (`EffectAnchor`):
  - `At(point, turn = null)`: a fixed world point.
  - `Bone(characterId, bone, offset = default, turn = null)`: a bone as last drawn, its position and turn; `offset` and `turn` are in the
    bone's frame, the offset scaled with the character.
  - `Weapon(characterId)`: the melee weapon the character holds drawn (the gear blood's blade, docs/game/blood-decals.md "On weapons and
    gear"): origin at the weapon mesh's hilt end, +Y along the blade, X across its width, stretch = weapon length / 8.5.
  - `Building(id, offset = default, turn = null)`: a player-built or nest building of the snapshot (`WorldSnapshot.Buildings`,
    `NestBuildings`, by `BuildingSnapshot.Id`), its origin and turn. Town buildings of the level data and furniture are not covered (no
    host-side lookup by id); worn items are skinned to the skeleton and have no frame of their own: use a `Bone` (e.g. `Bip01 Spine2`).
  - `Projectile(id, offset = default, turn = null)`: a bolt (`ProjectileSnapshot.Id`, from the projectile feed below) where it is drawn,
    in flight or stuck in the ground: origin at its nose, +Y along its flight. Gone once the bolt leaves the snapshot.
  - `.Scaled(stretch)` on any anchor overrides its stretch, in (0, 100]; `.Sized(factor)` multiplies the stretch (the anchor's own, a
    blade's, or `Scaled`'s), in (0, 100], so an effect grows with a level while keeping the blade fit; `.Pulsing(amount, seconds)` swings
    that size by `amount` in [0, 1) either way once every `seconds` in [0.05, 60], from the effect's start.
  The system's origin is at the anchor, its axes turned to the anchor's frame; the stretch is a uniform system scale, as Particle Universe's
  (`ParticleSimulation.Stretch`: it multiplies the system's `scale` and `scale_velocity`, so every place, shape, particle size, speed and
  affector reach, and the technique's position; 1 changes nothing). A blade system authored along its +Y with its origin at the
  weapon's base (the original Elemental Enchantment mod's convention, **Observed** from its shipped `.pu` files: a box from y 1.5 to 8.5)
  thus covers about 18% of the weapon to the tip; a base-game system authored at its origin plays at the hilt end. The effect follows its
  anchor each frame (`ParticleRenderer.MoveBurst`); on a `Bone` or `Weapon` its particles stay in the anchor's frame, moving and turning
  with it (as every technique's `keep_local`, turned too: `ParticleSimulation.KeepLocal`), while point and building effects throw theirs into
  the world; while the anchor cannot be located (character not drawn, weapon sheathed, building
  gone, unknown bone) it pauses (its emission stops) and starts again where the anchor is, for the seconds left. `PlayEffect(effect, point,
  ...)` and `PlayEffect(effect, characterId, bone = null, ...)` are `At` and `Bone` (null: `Bip01 Spine2`). Each `PlayEffect` is a new
  burst; `KeepEffect(effect, anchor, seconds, tint = null)` is the mod's one for that effect and anchor (its scale, `Scaled` / `Sized` /
  `Pulsing`, aside): called again before it ends it is renewed to `seconds` from then, not restarted, taking the new scale in place (another
  tint restarts it), so a mod keeps an aura up by re-calling within 30 s and makes it flare after a hit by re-calling with a larger size;
  `StopEffect(effect, anchor)` ends it (its particles live out). Requests queue and start at the next frame, after the hit feed, through the
  renderer's own bursts (`ParticleRenderer.Burst`, as the blood and dust do): at most 16 started a frame, 64 alive (playing or paused) and
  128 waiting. A bad request (unknown id; anchor kind, point, offset or turn not usable; no character or bone; no building id; a negative
  bolt id; stretch, size, pulse, lifetime or tint out of range; full queue; a call off the host's thread) returns false and is dropped,
  logged once per mod and reason.
  Call it only on the host's thread (host-side callbacks: hit feed, UI, key actions, debug rows), never from a tick system. The simulation
  never reads it, so the state hash is untouched; without code mods the frame skips it; a new world drops all effects, and a mod that fails
  its `Initialize` loses its own. **Verified** by `ModVisualEffectsTests` (order, tint, lifetime, caps, rejection logging, a dropped mod's
  effects; each anchor kind's frame; pause and resume; keep, renew and stop; the old overloads; kept-local particles following the burst; the
  stretch scaling places, sizes and speeds) against a recording sink and frame source. The bolt anchor, `Sized`, `Pulsing` and the renewal in
  place are **Observed** by a scratch program against a recording sink (2026-11-23), not in the game.
- **Trails:** `KeepTrail(EffectAnchor anchor, TrailStyle style, seconds)` on `ModContext` keeps a display-only ribbon trail (as Ogre's
  `RibbonTrail`) on any anchor above, sampled each frame where the anchor is drawn. `TrailStyle`: `Span` 0 (default) is a ribbon through the
  anchor's point facing the camera, `Width` (default 2) wide at the head and `EndWidth` (null: the same) at the tail; `Span` above 0 is a
  band swept by the anchor's +Y from its origin that far times its stretch (on `Weapon`, 8.5 reaches the tip: a blade trail); `Seconds`
  (default 0.3, up to 5) is how long a place stays; `Colour` (rgb up to 4, alpha 0 to 1) at the head fades to `EndColour` (null: the same,
  alpha 0) at the tail; `Material` names a particle material (an Ogre `.material` of the particles, base game or a mod's) for its texture,
  blend and colour, null for Meitou's soft additive streak. The texture's v runs across the trail and u along it (0 head, 1 tail, one value
  per piece); a name the particles do not have draws Meitou's streak. `seconds` (up to 30) is how long it keeps taking places; renewed by
  the same mod and anchor (scale aside) it takes the new style and scale in place; `StopTrail(anchor)` stops it taking places (what it drew
  fades out). While the anchor is not drawn the trail takes nothing and starts a new run when it is back (no piece across the gap). At most
  64 trails at once and 64 places each; drawn after the bolt tracers in the particles' pass (`ParticleRenderer.Ribbons`, a quad on its own
  axes per piece), depth tested, unlit, Vulkan only (the Metal loop draws no mod effects). Host thread only, validated and logged once per
  mod and reason (anchor problems as `PlayEffect`'s, a style out of range, lifetime, more than 64, off the host's thread); the simulation
  never reads it; a new world drops all trails, a mod that fails its `Initialize` loses its own; nothing runs without code mods. Engine API,
  not in the original. **Observed** by a scratch program against a stand-in frame source (pieces per sample, band span, fade, gaps, stop,
  caps, rejection logging; the drawn quad's size and facing), not in the game.
- **Projectile feed:** `AddProjectileFeed(view)` on `ModContext`: once a frame on the host's thread, `view` gets the bolts' changes
  between the shown snapshots (`ProjectileEvent`: id, `ProjectileChange` `Fired` / `Landed` / `Gone`, shooter, position, direction, speed,
  `live ammo` mesh), in the snapshot's order, then the gone ones by id; a bolt first seen stuck is `Fired` then `Landed`; `Gone` covers a
  bolt that struck a body (the hit feed has the hit), ran out or was cleared, with its last seen place. Read from the published snapshot, so
  the simulation never sees it and nothing is hashed; frames that show the same snapshot get nothing, and a bolt shot and gone between two
  shown snapshots is never told. A new world (load, new game) is taken as it is, telling nothing of its bolts already there. The list is
  reused: copy what you keep. A throw is logged with the mod id and that feed gets no more. Without a feed the host does not compare
  snapshots. Engine API, not in the original; **Observed** by a scratch program over hand-made snapshot lists.
- **Glow:** `Glow(GlowTarget target, colour, seconds, strength = 1)` on `ModContext` makes a target give off light of its own (display
  only). Targets (`GlowTarget`): `Character(characterId)`, the whole drawn character (body, worn items, what it holds); `Weapon(characterId)`,
  the blade of the melee weapon it holds drawn (from 0.3 of the weapon mesh to the tip, the gear blood's mapping, docs/game/blood-decals.md
  "On weapons and gear"; never sheathed, worn, scabbard or ranged parts); `Worn(characterId, slot)`, its parts in one `AttachSlot` (the
  game's `ATTACH_*`: `Hat`, `Body`, `Legs`, `Boots`, ...). On a part several of a character's targets cover, the weapon's or the slot's wins
  over the whole character's. Buildings are not a target: the building renderer's instances carry their own data in the w of their rows and
  have no room set aside for a tint. `colour` is rgb (1 the full channel, up to 4), `seconds` in (0, 30] on the game clock, `strength` in
  [0, 4] (the level drawn is the brightest channel times it, in eighths, at most 15.9; 0 shows nothing). One glow a target: a newer request
  replaces it, fading from where the old one stood, so re-calling it before it ends keeps it up with no restart; each fades in and out over
  0.25 s; `StopGlow(target)` fades out early the one this mod set (false: none). The renderer adds it to the lit colour before fog, brighter
  at the silhouette, so bloom picks it up; it rides in bits 16-23 of the w of the instance matrix's four rows (the blood keeps the low 16
  bits; instance flag bit 31), the blade mask in the level byte's bit 7. Particles are separate: a burning blade is `Glow(GlowTarget.Weapon(id),
  ...)` and `KeepEffect(effect, EffectAnchor.Weapon(id), ...)`. Requests are validated and logged once per mod and reason (no character or
  an unknown kind, colour, lifetime or strength out of range, more than 256 targets, off the host's thread); host thread only; never read by
  the simulation; a new world clears all glows, a mod that fails its `Initialize` loses its own. **Verified** by `ModGlowsTests` (fade,
  replace, stop, expiry, rejection logging, drop, clear; which parts each target lights; the packing).
- **Tint:** `Tint(GlowTarget target, colour, seconds, amount = 1, throughWalls = false)` on `ModContext` blends the target's own surface colour
  (its albedo) towards `colour` (each channel 0 to 1) by `amount` (0 to 1) before it is lit, as the original Vital Sense's `coloroverride`
  (**Observed** from its shipped shaders). It shares the glow's store: one glow or tint a target, each replacing the other, the same fade,
  lifetime, caps, `StopGlow` and rules. Packed in the same bytes: a tint's colour bytes stay at most 254 (a glow's brightest channel is
  always 255), its share in the level byte's low 7 bits, the blade mask as for a glow. `throughWalls` (a whole-character target only, at
  most 64 characters) also draws the character, tinted, where the scene hides it (the original's `overrideDepth`, which drew the skin at
  depth 0): it is drawn alone into a mask as the hover outline's characters are (`CharacterRenderer.DrawHoverMasks`), and where the
  scene's depth is nearer, the mask's colour half-blended towards the tint at the tint's share is laid over the finished picture; no outline,
  nothing where it is seen. One mask per tint colour, at most 8 colours a frame (later ones are left out); Vulkan only. Rejected (logged
  once): a colour or amount above 1, through walls on another target or past 64 characters. **Observed**: the packing, validation and
  shader compilation by a scratch program; not seen drawn in the game.
- Drawing **Observed** in `--duel` through forced glows and effects, not a mod's call: `Character` lit one duellist whole in cyan (body and
  armour; at strength 1 it washes out the texture) while `Weapon` lit only the other's drawn katana blade, its hilt and back scabbard not;
  `kenshi_fire1_small` kept on `Bone(..., "Bip01 Head")` and on `Weapon` burned over the heads and at the hands, following both duellists.
  The original Elemental Enchantment's `SB_Prototype_Fire` kept on both duellists' `Weapon` (a katana: weapon length 21.15 world units,
  stretch 2.49) burned along each blade from near the hilt to the tip and stayed on it mid-swing; its `SB_Prototype_IceBlade` showed a
  faint cyan mist along a blade. Not seen: a `Worn` target, a `Building` anchor (no player or nest building in the duel).
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
- **Hit reactions:** a registered system implementing `IBlowReactionFilter` (`Meitou.Simulation.Combat`) is asked `Reaction(World, in
  BlowReactionAsk)` for each melee blow and bolt that lands on a victim it leaves on its feet (a downed victim is not asked): the tick, the
  `CombatHitSource`, attacker and victim, sequence and blow as in `CombatHit`, the `HitDamage` after armour, its stagger damage (cut + blunt +
  pierce + extra stun), the victim's stumble threshold and `Kind` as the game picked it: `Heavy` past the threshold, else `Light` for a blow and
  `None` for a bolt (only a heavy bolt staggers). It answers the `BlowReactionKind` to use: `None` (swing, defence and task go on), `Light` (the
  swing and defence cut off, a small stumble unless one plays) or `Heavy` (the big stumble, which knocks back); more kinds may come. Filters run
  serially where the hit lands, in system order, each seeing the earlier one's answer, and must answer from the world and the ask alone. A throw
  or an answer that is no kind keeps the reaction and disables the filter for that world; the fault enters the state hash. Engine API, not in
  the original. **Observed** by a scratch run (not in the repo): an unchanged answer keeps hash and log, `None` for every hit changes the duel
  the same on 1 and 4 threads, an invalid answer is asked once and hashed.
- **Combat speed:** a registered system implementing `ICombatSpeedFilter` (`Meitou.Simulation.Combat`) is asked `Rate(World, in
  CombatSpeedAsk)` for every attack and block play rate the game computes (`CombatSystem` rate: a swing's or a block's start, the chase
  lead, the HUD's figures): the tick, the character, `Kind` (`Attack` or `Block`; dodges play at 1 and are not asked) and the rate after the
  game's caps (1.2 attack, 4 block). It answers the rate to use, which may pass the caps. Filters run in system order, each seeing the rate the
  earlier ones left, on any thread and concurrently (the combat act phase is parallel), so a filter must be quick, thread-safe and a pure read
  of the ask and of world state the tick has settled. A throw or an answer that is not a finite number above 0 leaves the rate as it came; the
  filter is still asked for the rest of that tick and not from the next, and the fault (its first tick) enters the state hash. Without a filter
  nothing is asked. Engine API, not in the original. **Verified** by `CombatSpeedFilterTests` (a duel: attack and block rates asked, an unchanged
  answer leaves log and hash as they were on 1 and 4 threads, a doubled attack rate halves the swing; a NaN answer is asked in one tick only and
  hashed). Two more kinds, same rules: `Reload`, asked as 1 when a crossbow reload starts (the reload takes the game's time divided by the
  answer), and `Projectile`, a bolt's flight speed in units a second, asked as the shot is fired and for the aim's lead (turrets are not asked).
  **Observed** by a scratch run (not in the repo): both asked, an unchanged answer keeps hash and timing on 1 and 4 threads, a doubled reload
  rate and a halved bolt speed move the first hit earlier and later.
- **Armour:** a registered system implementing `IArmourFilter` (`Meitou.Simulation.Combat`) is asked `Piece(World, in ArmourAsk)` for each
  armour piece a character wears, every time a hit (melee, bolt, execution) is about to go through its stack: the tick, the hit's source, the
  wearer, the worn `ItemInstance` the piece was built from (`Fighter.ArmourItems`, same order as `Fighter.Armour`; null for a piece made
  without one), its index and the `ArmourPiece` with its defences. It answers the piece to use, so a mod lowers a worn item's protection or
  raises an enchanted one's; coverage stays the record's. Filters run serially where the hit lands, in system order, each seeing the piece as
  the earlier ones left it, and must answer from the world and the ask alone. A throw or a defence not finite or outside the game's range (cut
  0 to 0.9, blunt 0 to 0.8, pierce from 0, cut-into-stun 0 to 1) keeps the piece as it came and disables the filter for that world; the
  fault enters the state hash. Without a filter the fighter's own pieces are used and nothing is built. Engine API, not in the original.
  **Verified** by `ArmourFilterTests` (a duel: every worn piece asked per hit, zeroed defences stop nothing, an unchanged answer changes neither
  hits nor hash on 1 or 4 threads; an out-of-range answer is asked once, keeps the piece and is hashed).
- **Defences:** a registered system implementing `ICombatDefenceObserver` (`Meitou.Simulation.Combat`) receives `Defended(World, in
  CombatDefence)` once per melee blow the defender blocked or dodged, after its counts, XP, sound and combat log entry: the tick, the attacker,
  the defender, the attack's sequence and blow (as `CombatHit`'s), `Kind` (`Blocked`, also for a dodge technique that counts as a block, or
  `Dodged`), `Parry` (a weapon's parry, the one that throws sparks) and whether the defender was the attacker's chosen target. Only melee blows
  are reported. Observers are called serially in Commit, in system order; they may read the world and change their own state,
  and must not change characters or other world state in the call. One that throws is logged and disabled for that world, and the fault enters
  the state hash. Without an observer no payload is made. Engine API, not in the original. **Verified** by `CombatDefenceObserverTests`
  (a duel: one report per logged block or dodge, the same on 1 and 4 threads, the run unchanged; a throwing observer called once, its fault hashed).
  A registered system implementing `ICombatDefenceFilter` is asked `Defence(World, in DefenceChoiceAsk)` each time a melee fighter decides how
  to meet a blow on its way: the tick, defender, attacker, sequence and blow, the blow's side (technique direction), whether the defender is
  stumbling, the game's block and dodge chances in percent (an armed fighter rolls the block, one that cannot block the dodge) and `Technique`,
  the game's pick as an index into `CombatSystem.Techniques` (-1 none). It answers the technique to use: -1 (no defence, none tried again for a
  while, as after a failed roll) or a block or dodge technique, a block that is not a dodge only for a defender that can block. Filters run in
  system order, each seeing the earlier one's answer, in the combat act phase on any thread and concurrently: quick, thread-safe, a pure read of
  the ask and of world state the tick has settled; state of its own about a defender only from that defender's asks (each defender chooses on
  one thread). A throw or an answer against the rule keeps the technique; the filter is still asked for the rest of that tick, not from the
  next, and the fault (its first tick) enters the state hash. Engine API, not in the original. **Observed** by a scratch run (not in the repo):
  an unchanged answer keeps hash and log and is asked the same on 1 and 4 threads; -1 stops every block and dodge; always the dodge technique
  turns every stopped blow into a dodge, the same on 4 threads; an out-of-range answer is asked in one tick and hashed.
- **Target choice:** a registered system implementing `ITargetScoreFilter` (`Meitou.Simulation.Combat`) is asked `Score(World, in
  TargetScoreAsk)` for every candidate the game scores (`TargetScore.Score`; a dropped one is not asked): the tick, chooser, target, `Kind`
  (`TargetChoiceKind`: `Fight` a fighter's own choice, `Join` the attacker of a protectee or another joiner's pick, `Look` the aggro look,
  `Assault`; more may come), the `TargetCandidate` facts and the score. It answers the score to use; the best wins. Filters run in system
  order, each seeing the earlier one's answer, on any thread and concurrently: quick, thread-safe, a pure read of the ask and of world state the
  tick has settled. A throw or a non-finite answer keeps the score; the filter is still asked for the rest of that tick, not from the next, and
  the fault enters the state hash. `TargetCandidate.ChooserHasTarget` is true in a fighter's own choice while it fights someone (the original's
  `AI::chooseBestAttackTarget` given a valid old target), false in the other choices; no engine score reads it. Engine API, not in the original.
  **Observed** by a scratch run (not in the repo; a 1-against-2 fight, `Fight` asks only): an unchanged answer keeps hash and log and is asked
  the same on 1 and 4 threads; lowering one candidate's score changes the pick, the same on 4 threads; a throw or a NaN or infinite answer keeps
  the scores, is asked in one tick and is hashed.
- **Engagements:** a registered system implementing `ICombatEngageFilter` (`Meitou.Simulation.Combat`) is asked `Allows(World, in EngageAsk)`
  whether a fighter may take on a target: the tick, attacker, target, `Kind`, whether the target is the player's pick (an attack order the
  player gave, or the current target such an order named) and whether the fight is a defence. `EngageKind.Start` is every fight start
  (`CombatSystem`'s single start: the player's order, a defence, a goal's or squad's join, an arrest, an NPC order's assault, a loaded attack
  order), asked serially once the game's own tests pass; refused, the fighter does not fight. `Choice` is every candidate of a target choice (a
  fighter choosing whom to fight next, `CombatSystem.BestAttackerOf` for a joiner), asked on any thread; refused, it is passed over for the next
  best. More kinds may come. Filters run in system order until one refuses; quick, thread-safe, a pure read of the ask and of world state the
  tick has settled. A throw allows that ask; the filter is still asked for the rest of that tick, not from the next, and the fault enters the
  state hash. Without a filter nothing is asked. Engine API, not in the original. **Observed** by a scratch run (not in the repo): allowing all
  keeps hash and log on 1 and 4 threads with the same asks; refusing all leaves a duel without a blow; refusing one side's start leaves it
  without a swing; a throwing filter is asked in one tick, the duel as without it, the fault hashed.
- **Trade prices:** a registered system implementing `ITradePriceFilter` (`Meitou.Simulation.Trading`) is asked
  `Price(World, in TradeDeal, long price)` for every price the game asks: the trade window's stack and unit prices and the coins it takes,
  and an NPC shopper's purchase from a building's stock. The deal names the tick, the item record, the units, whether the customer sells,
  the customer (the player's character in the window, or the NPC shopper) and the shop's squad (-1 none); the filter returns the price to
  ask, so a mod gives a town's own faction a discount or marks prices up. Filters run in system order (mods in load order), each seeing the
  price the earlier ones left. A buy-back (undoing a trade of the open window) keeps its coins and is not asked. A filter must be a pure
  read, since the same price is asked again for the window and the payment; one that throws or answers a negative price is undone for that
  price and disabled for that world, and the fault enters the state hash. Without a filter nothing is built. Engine API, not in the original.
  `TradeDeal.Item` is the priced item itself (the window's stack, the shopper's unit; its `ModTag`, quality, level and maker), read only.
- **Sales:** a registered system implementing `ITradeObserver` (`Meitou.Simulation.Trading`) receives `Traded(World, in TradeSale)` once for
  every completed item deal, after the money and the goods moved: an NPC shopper's purchase of one unit from a building's stock or a shop counter
  the player built (`TradeSaleKind.Shopping`), and the trade window's buys and sells (`WindowBuy`, `WindowSell`), a click or an item dragged
  across. The sale names the tick, the kind, the item record, the units, the price paid, the buyer and the seller (`TradeParty`: the character,
  none for a container's stock; its squad, -1 none; its faction, -1 none; a shop counter's seller is the player's faction), the site sold from
  (the stock container's fixture id or the counter's site id; "" in the trade window, whose stock is the trader's shop), whether the goods
  reached the buyer (a shopper's unit from an NPC's stock can be paid for and leave the stock without reaching it, [NPC
  purchases](game/economy.md#npc-purchases)) and whether it is a buy-back (`Buyback`: it undoes an earlier deal of the open trade window at that
  deal's own coins, so a mod that counts sales can take the earlier deal back instead of counting a second one). A refused deal (no money, no
  room, caught fencing, smuggling) is not a sale. Slaves, animals, buildings, beds and bounties are not item deals and are not reported.
  Observers are called serially in system order (mods in load order); a trade made in the paused interface is told between ticks on the host's
  thread, with the coming tick's number. They may read the world and change their own hashed state, and gameplay changes go through commands. One
  that throws is disabled for that world and the fault enters the state hash. Without an observer nothing is built. Engine API, not in the
  original.
- **Storage use:** a registered system implementing `IStorageFilter` (`Meitou.Simulation.Items`) is asked `Allows(World, CharacterId who, string
  storage, StorageUse use, string? record)` for each storage the game's own searches would pick for a character: a hungry character's food store
  and a looter's (`Take`), a stash or ditch store for carried loot and resources (`Put`), a machine operator's source for its input (`Take`) and
  target for its output (`Put`), a townsperson's source for filling a town machine (`Take`), a builder's store of building materials (`Take`), a
  squad's ditch trip and a forager's store (`Put`). The storage is a player machine's site id or a container's fixture id; the record is the
  item's, or null when the search is not for one item (any food, whatever the character carries). A refused storage is passed over and the search
  goes on to the next. What the player does by hand (dragging items, a trade, an order to use a container) is never asked, so a mod keeps
  characters out of a store without locking the player out: "private chests", stores only one squad or faction uses, a quarantined store. The
  filter answers for every character the searches run for, the player's own working a job among them; it tells them apart by `who` (its squad,
  `Squad.Independent`, its faction). Filters run in system order (mods in load order); the first refusal wins. Some searches run on worker
  threads in the AI's parallel steps, so a filter must be quick, thread-safe and a pure read of its arguments and of world state the tick has
  settled (factions, squads, the storage's owner, its own system's serial step, the last finished tick). An order given while the game is paused
  (a build order's materials) asks it between ticks on the host's thread, with the coming tick's number. One that throws is answered as allowing
  for the rest of that tick and is not asked from the next tick on; the fault enters the state hash. Without a filter nothing is asked. Engine
  API, not in the original.
- **Inventory arrange:** a registered system implementing `IInventoryArrangeFilter` (`Meitou.Simulation.Items`) is asked
  `Arrange(World, in InventoryArrangeAsk)` before every arrange the game runs: a window's ARRANGE (a character's main grid or worn bag, a
  machine's or container's storage, the trade window's stock; `InventoryArrangeReason.Button`), making room for a held item going back
  (`MakeRoom`) and the trade window's stock grid being built (`StockShown`); more reasons may come. The ask names the tick, the root inventory,
  its window (`ItemOwner`: a character, `Stock`, a machine's or container's site), the reason and the grids (`ArrangeGrid`: a grid and its
  section, `main` and/or `backpack_content`). It answers null (the next filter's, finally the game's arrange) or an `InventoryArrangement` of
  `ItemPlacement(item, x, y, rotated = null)`: every item of the grids placed once, in its own grid, inside it and overlapping none; the
  grids' items are then listed in placement order after their other sections' items, as the game's arrange lists them.
  `InventoryArrangement.MergeFirst` has the engine merge the grids' partial stacks first (the game's top-up, each stack topping up those before
  it in list order) and ask the same filter again with `Merged` set; a null answer then undoes the merge. Filters run serially in system order
  until one arranges. A filter must be a pure read; one that throws or answers an invalid arrangement (or `MergeFirst` twice) is undone for that
  arrange, which the next filter or the game then makes, and is disabled for that world; the fault enters the state hash. Without a filter
  nothing is built. `InventoryCommands.Arrange(world, root, owner, reason, bag = null)` is that arrange (both grids, or only the bag or only the
  main grid), for a mod's own button too; `Arrange(root)` and `Arrange(root, bag)` stay the game's and never ask the filters. Engine API, not in
  the original. **Observed** with a scratch program (placement, order, rotation, each invalid answer, throw, merge, hash, inert without a
  filter); no test yet.
- **World ready:** a registered system implementing `IWorldReady` receives `Ready(World, bool fromSave)` once, between ticks before the first
  (`world.Start` then says how: `WorldStartKind.NewGame`, `Load` or `Import`, with `SaveFolder` the full path of the save read, null for a new
  game; `Unknown` for a world the game host did not start; not saved, hashed or read by the engine, so a mod reads a file of its own or the
  original mod's beside the save there):
  after a save has loaded (`IModSaveData.Read` done and settings of mods no longer running dropped; an import counts as a save) or a new game has
  placed the player's characters (`fromSave` false), once that world is the running one, so the context's `Enqueue`, `Tick` and `PostSound` reach
  it. Systems are told in system order (mods in load order); one may change the world as a serial phase does, so a mod sets its unsaved runtime
  settings (a held platoon, a sleeping town) back on here, also for a save made without its own saved state. One that throws is logged and the
  others are still told; a mod's system is also stopped, with a notice, as for a throw of one of its phases (Tick systems above). Engine API, not
  in the original.
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
  raised (a witness's or a fence's booking, a dialogue line assigning one: the speaker is the witness, the line's amount is added, and the code
  and victim are those of the target's pending crime, which the line marks booked, if it has one; or a mod's `AssignBounty`, below: no witness
  and no crime); `Lapsed`, a pending crime ended, booked or not
  (its timer ran out, a crime of another code replaced it, or the character was recruited, bought free, dismissed or stored off screen with its
  squad, the dead with it); `Expired`, a bounty run out; `Cleared`, a bounty removed otherwise (a fine or a bail paid, a release from prison,
  dialogue, the debug menu, a mod's `ClearBounty`). Each once: a crime already pending with the same code is only refreshed and not reported again, and an assault by a
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
  that had a witness (the arrest system's trigger). `CrimeSystem.AssignBounty(world, character, faction, amount)` adds a bounty as dialogue's
  assigned one does (the original's own step, which a new NPC's rolled bounty also takes): under the faction's booking faction (its `legal
  system`, else itself when it books bounties), the time of the last raise stamped, so it lasts 0.04 hours per cat and for ever from 10000; no
  pending crime is touched and no message posted. It returns the entry's new amount, -1 for an amount of 0 or less, a character not in the world
  or a faction with no booking faction. `CrimeSystem.ClearBounty(world, character, faction)` removes that entry and returns its amount (0:
  none); relations and recognition stay. Both are for serial phases and `IWorldReady`, once per event. Engine API, not in the original.
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
  Without an observer no payload is made and no death watch is kept. Engine API, not in the original. A mod that kills a character itself
  reports the death with its killer and site at once with `Deaths.Report(world, victim, killer, site)` (serial phases; nothing for a character
  not dead or already reported; else the sweep reports it with no killer).
- **Experience:** a registered system implementing `IExperienceFilter` (`Meitou.Simulation.Bodies`) is asked `Gain(World, in ExperienceGain)` for
  every experience gain the game makes to one of a character's own stats in the world (every `XpService` entry: combat, injuries, ranged and
  turrets, moving, sneaking, assassination, thievery, lockpicking, building, machine work, training dummies, first aid, mass combat) and for a
  direct `CharacterStats.Gain` or an `XpService` call without a source (`ExperienceSource.Other`): the tick, the character, the stat, the amount
  and its `ExperienceSource`. It returns the amount to add, so a mod scales experience by squad, faction, stat or source, or cancels it (0).
  Filters run in system order, each seeing the amount the earlier ones left, until one answers 0. For a gain through the core curve, the amount
  is the one the curve takes, after the XP settings (Squad XP or the skill's own, a mod's skill always Squad XP; Others' XP); only an amount in
  the curve's range (above 0, at most 20) is asked,
  since the curve adds nothing for any other (a raw amount above 20 is dropped and a filter cannot bring it back), and the answer is held as that
  setting is: to 20, and with no raise for a stat at or past a training dummy's cap. First aid and mass combat add straight, past the curve:
  their amount is the one added, and the answer is held as the setting is there too: no raise for the stat first aid trains (medic, or robotics
  with a robot kit) at or past 100, none needed for mass combat (nothing reads it). A character's stats are bound to it as it enters the world
  and cleared as it leaves, so its gains are filtered from its first step. Gains happen in the parallel phases too, so a filter runs on any
  thread, concurrently: it must be quick, thread-safe and a pure read of the gain and of world state the tick has settled (its own system's
  serial step, the last finished tick), and must not grant experience itself. The paused interface's commands between ticks (the dialogue
  window's choices, character trades, squad, item and trade commands; in the stock game a theft's or a fence's thievery) ask it on the host's
  thread, with the coming tick's number. A code mod's skill is never asked about: neither `ModSkill.Gain` nor the combat experience a weapon
  naming that skill trains into it (`meitou skill`, Weapon types below) passes the filters; only the game's own stats do. A filter that throws,
  or answers an amount that is negative or not finite, leaves that gain as it came and is logged once; it is still asked for the rest of that
  tick and not from the next, and the fault (with its tick) enters the state hash. Without a filter nothing is bound or built. Engine API, not in
  the original. The gain also carries `Level` (the stored level before it) and `Cap`: the core curve's top (101, or a training dummy's level;
  the curve adds amount x ((cap - level) / cap)^2), or for an amount added straight the level from which no raise is taken (first aid's 100,
  mass combat's infinity). A filter that overrides the default method `Adjust(World, in ExperienceGain)` returns the gain to make: its amount
  and cap are taken (a cap above 0, else a fault as above), and the hold rules then use that cap, so a level cap above 101 trains on its own
  curve. The default `Adjust` is `Gain`'s amount with the cap unchanged.
- **Need rates:** a registered system implementing `INeedRateFilter` (`Meitou.Simulation.Bodies`) is asked `Rate(World, in NeedRateAsk)` for
  each body update's rate of a need of every character in the world: the tick, the character, `NeedKind` (`Hunger`: the race's hunger rate
  times the situation factor, the multiplier on hours / (starvation time x hunger time option) the hunger meter loses; more kinds may come)
  and the rate. It answers the rate to use; the HUD's hunger figure is asked too. Runs in the parallel body step: quick, thread-safe, a pure
  read of the ask, the character's own state and world state the tick has settled. A throw or an answer that is negative or not finite keeps
  the rate; the filter is still asked for the rest of that tick and not from the next, and the fault enters the state hash. Without a filter
  nothing is asked. Engine API, not in the original.
- **Health changes:** a registered system implementing `IHealthChangeObserver` (`Meitou.Simulation.Bodies`) receives `HealthChanged(World, in
  HealthChange)` for every change of a body part's flesh, stun, bandage or splint (`HealthValue`, the part's index in `MedicalState.Parts`) or
  of the blood (part -1) of a character in the world, whatever made it, with before and after and a `HealthChangeKind`: `Hit` (melee, bolt,
  execution, a martial artist's punch on itself), `Body` (bleeding, blood regained, healing, stun recovering, a wound worsening), `FirstAid`
  (bandage, wound closing, splint), `Environment` (acid water and ground, acid rain, burning, gas, dust), `Eaten`, `Rack`, or `Other` (any
  change no step names: debug commands, dialogue, a mod's own change, off-screen recovery). One step's changes to one value are told as one.
  Calls run serially once per tick after the slow world and the deaths' sweep, in slot order, each character's changes in the order its steps
  made them, or as the character leaves the world (a character coming back is taken in afresh, its off-screen changes not told); a character's
  first look tells nothing. Callbacks may read the world and change their own hashed state, not characters or other world state. A throw
  disables the observer for that world, and the fault enters the state hash. Without an observer nothing is looked at. Engine API, not in the
  original. **Observed** by a scratch run (not in the repo): a duel's changes are the same list on 1 and 4 threads, and its hash and log are
  unchanged by the observer.
  `CrimeSystem.StealRoll(World, CharacterId thief, CharacterId watcher, float chance, int faction, CharacterId victim)` runs the game's steal
  roll for a mod's own theft (serial phases): the watcher (none or gone: nobody) misses the thief when a draw falls under `chance` (0 always
  caught, 1 or more never); caught, the game's outcome follows (a watcher in bed may only speak, else it gets up, says event 8 and remembers the
  thief, which gets a Theft, timer 30, against `faction` with `victim`), and thievery XP as for the game's own rolls. It sets no pending crime
  first (`SetPending` does). True when caught. **Observed** by a scratch run (not in the repo): the same result, pending crime and hash as the
  private roll called as the Disarm port called it, at chances 0, 0.5 and 1.
- **Removals:** a registered system implementing `IRemovalVeto` (`Meitou.Simulation.Bodies`) is asked `Keep(World, in RemovalAsk)` before the
  engine takes a character, body or platoon out of the world for a `RemovalKind`: `CorpseDecayed` (12 game hours), `CorpseUnloaded` (in a zone
  not loaded), `PlatoonDespawned` (a platoon the population does not keep leaves the loaded world: despawned as it unloads, or thinned member by
  member; kept, it unloads with its characters stored and a knocked-out member in the zones holds it loaded, as for a kept one) and
  `PlatoonOrphaned` (an off-screen platoon not kept and not wanted counts towards its 60 s despawn; kept, the timer starts again); more kinds
  may come. `RemovalAsk.Platoon` is the platoon's id (`world.Platoons.Find`) for a platoon kind, -1 otherwise, and `Character` none. True keeps
  it this time; it is asked again when the rule would take it next. Vetoes run in system order until one keeps it: serially for the corpse kinds
  and `PlatoonDespawned`, on any thread for `PlatoonOrphaned` (the off-screen step is parallel), so a veto is quick, thread-safe and a pure read
  of the ask and of world state the tick has settled. A throw lets the removal go ahead; the veto is still asked for the rest of that tick, not
  from the next, and the fault enters the state hash. A squad stored off screen takes its dead with it, unasked. Engine API, not in the
  original. **Observed** by a scratch run (not in the repo) of the roaming town without kept roamers: a veto keeping nothing keeps the hashes on
  1 and 4 threads; keeping `PlatoonDespawned` stores the leaving platoons' characters; keeping both kinds stops the orphan timer, the same
  hashes and asks on 1 and 4 threads; a throwing veto is asked once and hashed.
- **Items created:** a registered system implementing `IItemCreatedObserver` (`Meitou.Simulation.Items`) receives `Created(World, in
  ItemCreated)` for each item the world makes of an `ItemCreatedKind`: `Loot` (a camp's ground items, a town's artifacts for its buildings'
  containers), `Crafted` (a bench's finished item, `Character` the crafter), `ShopStock` (each unit and special item of a shop's fill, before
  it is stacked; `Squad` the shop's squad, -1 for a home stocked without one) and `SpawnEquipment` (a new character's kit and what it holds,
  once it is in the world); more kinds may come. The observer may set the item's `ModTag` (units tagged apart do not stack) and its own hashed
  state, and nothing else. Calls run serially in system order in the step that made the item. A throw disables the observer and enters the
  state hash. Without an observer nothing is walked. Engine API, not in the original.
- **Faction list:** `ArrangeFactionList(arrange)` arranges the Overview's Faction tab: `arrange` gets the rows the tab would show
  (`FactionListRow`: faction, name, the player's relation), sorted by name, and returns the rows to show, in order. It can reorder, rename
  (names go through the translation catalog like the tab's own), leave out, or add headings (faction -1: the name alone, no number or bar),
  for example to group factions under another. It runs on the host's thread whenever the tab is drawn, so it must not touch the world; mods chain in
  load order. A throw or a null is logged and that arranger is not called again.
- **Placement rules:** `AddPlacementRule(rule)` changes where build mode lets the player place a building. `rule.Waive(ask, failure)` may
  lift one of the game's own reasons for refusing a ghost (`PlacementFailure`: inside only, too close to a building, uneven ground, a building
  not owned, and the rest), and the check goes on to its next step; the town block (`TooCloseToTown`) is never asked about, since
  the simulation applies it again on Confirm, nor is research. `rule.Refuse(ask)` may refuse a ghost the game allows, with the mod's own text as
  build mode's message (`PlacementFailure.RefusedByMod`). `PlacementAsk` gives the building's build data, the ghost's position and turn, the
  building it stands in or on (null outdoors), the floor and whether it is a wall piece. A reason any mod lifts is lifted; a refusal by any mod
  stands. Asked on the host's thread for every ghost build mode shows (each frame it moves, each piece of a wall line, and the test channel's
  `build`), so it must be cheap and must not touch the world; with no rule registered the check is unchanged. A rule that throws is logged and
  not asked again. What a lifted reason allows is the mod's to answer for: furniture placed outdoors stands with no parent building, and
  whatever reads indoors (rain on farms and rain collectors, lights, beds) treats it as outdoors. Engine API, not in the original.
- **Combat moves:** `AddCombatMove(source)`: `source(world, id, tick)` answers a `CombatMove` (a `CombatTechnique` and its progress, 0 to
  1) or null, and the animation plays the first answer, in load order, as the character's technique (`CombatSystem.ModMove`);
  `CombatMove.Clip(animation, progress)` plays any ANIMATION clip instead, by record name (else string id), e.g. `medic` (the first-aid
  kneel) or `squat T` (the crouch), its time the progress times its length, as the crossbow's poses are. It is asked
  only when the game plays nothing of its own: not while the character attacks, blocks or dodges, is stunned or stumbling, holds a crossbow
  pose, is down, carried or getting up (as of the last finished tick). The move is animation only: it lands no blow, nobody blocks it, and
  the combat system may start its own swing over it at any tick. A technique the animation library has no clip for plays nothing. The
  source runs in the animation's parallel step for every stepping character, and the move enters the world: its layers are hashed and a
  relocating clip's root motion moves the character. So it must be cheap, thread-safe, read-only and deterministic: answer from state the
  tick has already settled (its own system's serial step, the last finished tick), never from call order or caches that change the answer,
  and never call `ModMove` itself. A throw, a move without a technique or animation, or a progress that is not finite is a fault: logged
  once with the mod id; the source still answers for the rest of that tick and answers nothing in that world from the next tick on, and the
  fault enters the state hash. The clip path is **Observed** by a scratch program (answer, clamp, fault, hash), not seen in the game.
- **Dialogue codes:** `AddDialogueCondition(code, handler)` and `AddDialogueEffect(code, handler)`, named from FCS data in a
  DIALOG_ACTION's `condition name` / `action name`. A condition gets `DialogueCheck` (world, speaker, the subject `who` picked, the
  condition) with `Matches(bool)` and `Compare(double)` that apply the vanilla value and `compare by` rules; it only reads (it also runs
  for interface previews). An effect gets `DialogueAct` (world, speaker, target, effect) in a serial step. An unregistered code
  computes "no" / does nothing, as an unknown vanilla code. A handler that throws is logged once with the mod id and code; that call computes
  "no" / does nothing more, and the handler is still asked later (the interface asks conditions too, so a drop would depend on what it asked).
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
- **Character editor rows:** `AddEditorRows(context => rows)` (`ModLooks`, `src/Meitou.Data/Characters`) adds rows to the character editor's
  tabs, after the game's own, in registration order (mods in load order). The source is asked on the interface's thread for the character shown
  (`LookRowContext`: its `Race`, `Female`, the `Look` record being edited, read only, and `Creation`, true in a new game's creation, false for a
  surgeon's, recruit's or bought slave's edit), again whenever the look changes, so it answers per race, sex or head: none leaves that
  character's editor as it was. A `LookRow` is `LookRow.Slider(tab, key, caption, min, max, default)`, `LookRow.Choice(tab, key, caption,
  choices, default)` or `LookRow.Colour(tab, key, caption, hue, saturation, brightness)` (each 0..1), drawn as the game's `Kenshi_Slider`,
  `Kenshi_ListSlider` and `Kenshi_MultiSlider` (three sliders, as the hair colour). A value lives on the look record (CHARACTER_APPEARANCE) under
  `mod id:key` (`ModLooks.KeyOf`): a slider's value or a choice's index as an int, a colour as a vec3 of hue, saturation and brightness. No other
  code reads those keys, and the editor's CONFIRM (`EditCharacter`), the save and EXPORT carry every key of the record, so the value is kept with
  the character with no save data of the mod's own; a row the source stops returning (another race, an unknown head) keeps its value on the
  record. RESET puts each shown row back at its default. Read a value with `ModLooks.Int(look, modId, key, fallback)`, `ModLooks.Hsb` or
  `ModLooks.Rgb` (null when the look has none) from a look record, e.g. `cold.Appearance?.Loadout?.Appearance.Record`. A source that throws, or
  answers a row without a caption, with a key that is empty, repeated or not letters, digits, spaces, `-`, `_` and `.`, a slider whose `min`
  is above its `max`, a list without choices or a default outside its range, is logged once and asked no more for the session.
  `AddLookRoll(context => ...)` writes a mod's values on a look: every generated character's, after the game's rolls (`LookRollContext`:
  `Look`, `Race`, `Female`, the `Character` record, `Faction`, `FromBodyFile` for a unique character's own body file), and the editor's
  RANDOMISE (`Tab`, the tab rolled, null for RANDOMISE ALL; `InEditor`). It writes only through `Set(key, value)` and `SetColour(key, hsb)`
  (its own keys) and draws only from its `Random`, a sequence of its own made from the look's seed and the mod's id, so the game's rolls and
  every other mod's stay as they are. Generation runs in the tick and on worker threads, so a roll must be quick, read no world state and write
  the same values for the same arguments; a throw keeps what it wrote and is logged once. A character generated before the mod was added has
  no value: read it with a fallback. Without a source or roll the editor and the generator are as before. Engine API, not in the original.
- **Look extras:** `AddLookExtras(context => extras)` (`ModLooks`, `src/Meitou.Data/Characters`) adds to how people are drawn. The source is
  asked each time a person's look is built (`CharacterAppearance.Build`: in the world, the editor's previews, a loaded save; not animals), with
  `LookExtrasContext`: the `Look` record (read only, so a value from the mod's editor rows is at hand), `Race`, `Female` and the `Head` drawn. It
  answers null, or `LookExtras` with `PoseWeights` (shape keys by pose name, set over the look's own; 0 takes one off) and `Tints`. A `LookTint`
  lays a colour over a disk of the skin: on `LookArea.Body` or `LookArea.Head` (the HEAD's `texture map`), centred at `Centre` (0..1 across and
  down the texture image), `Radius` a fraction of its width; the texel keeps its brightness (its largest channel) and takes the `Colour`'s hue and
  saturation by `Strength`, fading over the outer `Softness` of the radius and above `MaxLightness`; `Side` (`Both`, `Left`, `Right`: the
  character's own, by the side of the mesh) limits it to one half, as both eyes share one patch of the head texture. Every mod's extras are merged
  in registration order (pose weights set over one another), at most `ModLooks.MaxTints` (4) tints a look. The renderer reads them when it builds
  the look, so they cost nothing per frame: the body's material gets a companion record in the material table with the tints
  (`CharacterLookTints`), and the shaders get the tints' code only when a source is registered, so without one they are as before. A look is
  built again whenever it changes (an editor change, equipment, a load), so a source answers from the look alone. It may run on any thread: quick,
  no world state, the same answer for the same arguments; a throw, a pose weight that is not a number or a tint out of its ranges adds nothing to
  that look and is logged once. Engine API, not in the original.
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
  game's own actions train it only through a weapon that names it (Weapon types, below). Once added it has a stat number
  (`skill.Stat`, `ModSkills.FirstStat` 1000 up in registration order, so it follows the load order: keep the id, never the number), under which
  `CharacterStats`' indexer, `Stored` and `Gain` read, set and train it; stat modifiers do not apply to it. Levels live in
  `CharacterStats.ModSkills`, enter the state hash once one is set, and are saved in the character's STATS record
  ([save](formats/save.md#compatibility-exceptions)). A column holds 18 lines; past its 14 the rows share its height (room for 11, 8, 10 and 5
  mod skills). A bad id (1 to 48 letters, digits, `_`, `.`, `-`), an id another skill has (case-insensitive), a label over 32 characters or a
  full column refuses the mod. Not shown in the HUD's stat lines.
- **Weapon types:** data alone already makes a new weapon that borrows a class's behaviour: WEAPON `skill category` picks the skill it trains and
  uses and its attacks, blocks and dodges, and `skill category animation override` the class whose stance, guard and idle clips it plays (a rapier
  training Sabres and standing like a katana; nine base weapons do this); `length` sets its reach (a technique strikes from `attack distance` + half
  the length), `cut`, `blunt` and `pierce damage multiplier` its damage split, beside its bleed, armour penetration, indoors, weight and kind and
  race multipliers; new COMBAT_TECHNIQUE records with a class's flags give that class new attacks, their clips found by name in the ANIMATION_FILEs
  races link ([animation](animation.md#animation-files)); a RACE's `fists lv1` names the WEAPON its fists are (still Martial Arts); and a firearm is
  a CROSSBOW record with its own meshes, `ammo` item, `reload anim`, `num shots` and pierce damage (it trains Crossbows and is named a crossbow).
  A weapon of its own skill takes a code mod: WEAPON `meitou skill` (Meitou only, trimmed) names a skill added with `AddSkill` by its id, whichever
  mod added it, and the weapon then uses and trains that skill wherever a weapon uses its category's: the blow's damage, the techniques' `min skill`
  and `max skill`, executions and door bashing, the HUD's Current Skill line (the skill's label) and damage figure, and the weapon skill's share of
  combat experience (hits, glancing hits, blocks; the race's weapons entry as for any weapon skill), with no injury multiplier, as the game's weapon
  skills in those reads; the item tooltip's class line is the label and "class". Its attacks, stance and two-arm rule still follow its `skill
  category` and override, and attack and defence are Melee Attack and Melee Defence as for any weapon. A weapon of `skill category` 5 ignores the
  field: it fights as fists, whose rating, blow and experience are Martial Arts' with no weapon skill to replace, and its tooltip names no class. A
  character's level starts at the skill's `Start` until the mod sets it, NPCs' and animals' too (an animal's natural weapon naming a skill fights at
  that level, so set it). A name no running mod added (the mod removed, or a misspelled id) leaves the category's skill, as the original game, which
  loads the field as any other, does, and the load log says so once per id (`warning   weapon skill '<id>'`).
  A class with attacks of its own needs no code: WEAPON and COMBAT_TECHNIQUE `meitou class` (Meitou only, trimmed, case ignored) put the weapons and
  techniques of one name in a class, whose weapons pick their attacks, blocks and dodges from its techniques alone, as a creature's weapon does from
  its own (`WeaponClassNames`; the class's number is internal, from 1000000 up). Every mod naming a class shares it, so prefix the name with the
  mod's id (`mymod.whip`), as skill ids. Defence works as for any weapon: one that can block never dodges, so a class's dodges serve only its
  weapons with `can block` off (and fist-category ones), and a block stops only blows of its own direction, so a class that blocks needs one per
  direction (the stock sets have six: up, left, right, thrust, up rear, right rear). When no class attack fits the moment (the target beyond every
  class attack's reach, the skill outside their `min skill` and `max skill`, the wrong posture, a needed limb out), a person throws an unarmed one
  at its Martial Arts, as the original does for any weapon ([combat](game/combat.md#attack-technique-choice)), and it walks in only as close as the
  longest reach it had: give a class an attack reaching as far as the unarmed ones (`attack distance` 13 or more; every stock class has "heavy
  downcut v2" at 25) and ground attacks (`is prone`), or its wielders mix in kicks and punches. Blocks and dodges have no fallback, so a class with
  none of its own neither blocks nor dodges, but a stumble break-out with no stumble dodge of the class takes those of the weapon's own `skill
  category` (base data: "Dodge back fly" and "Dodge back (copy for stumble)", flagged for the six skill classes and Unarmed; a base category of 6, 7
  or 9 and up has none). The load log warns (`warning weapon class '<name>'`) of a class named only by techniques, only by weapons, or with no
  attack technique, which is usually a misspelled name. The technique's clip is found by name as any person's technique's; keep its `animal` at 0 (9
  and up files its clip among the animals'). Classes are made for people's weapons: an animal whose natural weapon names one picks from it too, and
  plays a clip only if its skeleton has one of that name; a race's `fists lv1` naming one is not used (fists are not a weapon in hand). Everything
  else (skill, stance, two-arm rule, the Current Skill line) follows the weapon's `skill category`, its override and `meitou skill`. Leave the
  techniques' weapon flags off: the original game, and Meitou without the class, read the weapon as its `skill category` and the techniques fit no
  weapon then.
  To enter the fields in the construction set, add `meitou skill: "" "A code mod's skill this weapon uses"` and `meitou class: "" "Weapon class"`
  under `[WEAPON]` and `meitou class: "" "Weapon class"` under `[COMBAT_TECHNIQUE]` in the install's `fcs.def`. Steam's file check and game updates
  put back the stock `fcs.def` and so remove the lines (add them again). The editor keeps a field `fcs.def` does not declare through a load and a
  save, so a record keeps its fields when someone without the lines saves it, unless its Cleanup window's "Remove deprecated fields" is run ([FCS
  data](formats/fcs-mod.md#editor-and-undeclared-fields)). Without the editor, write the records with `FcsWriter` (`src/Meitou.Data/Fcs`).
- **Character refresh:** `CharacterRefresh.Refresh(context.Population, cold, kind)` (`Meitou.Simulation.Bodies`) makes a character's derived
  state again after a mod changed what it is made from: `CharacterRefreshKind.Equipment` the fighter from the inventory (guard and encumbrance
  kept), the drawn weapon following the new kit, the worn gear's colours and the look, as every engine path that moves worn items does (a
  piece broken, taken off or put on by a mod); `Look` the look alone. Serial phases only (a system's `Inputs` or serial step, `IWorldReady`). A
  character without an inventory is left as it is. Engine API.
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
- **Task handlers** (`Meitou.Simulation.Ai`, [task starts](simulation.md#task-starts)): a registered system implementing `ITaskOwner` gets
  `RegisterTasks(TaskHandlers)` once when the world is made, in system order, and registers an `ITaskHandler` (`Start`, `End`, `Done`) per
  `CharacterTask`, per row (`Register(TaskType, handler)`, a mod's row from 1000 up) or per row on a site (`RegisterOnSite`). One handler per
  key: a later registration replaces an earlier one, so a mod system replaces a standard handler only when it sits after that handler's system.
  `Register(row, handler)` with a `TaskType` row of the mod's own (1000 up) sends every start of that row, whatever its `CharacterTask`, to the
  mod's `ITaskHandler`: `Start` (the row is eligible, the fight and the task before have ended, the provenance `CharacterCold.Running` is
  recorded), `End(task, TaskEnd)` when a serial start replaces it (a fight's too) or `TaskStart.End` ends it (`Replaced`, `Stopped`, `Done`), and
  `Done` (by default: the character's task is no longer that task; the player's order queue reads it). No handler runs for `TaskStart.Stand` (a
  knock-out or a death, a trade or a conversation stopping an AI walk or stand), for the AI's own starts in its parallel think
  (`TaskStart.StartOwn`) or for the act-out's own `Step` / `StepTo`, so a mod also checks its characters in its own serial step (alive, not down,
  `Running` still its row and running the task). Start each walk with `TaskStart.Walk`: a `StepTo` keeps the provenance of the task it steps
  from. A row's handler takes the place of the task's, so the engine's walk does not start by itself: call the task's own (`tasks.Of(task)`) from
  it to keep it. `Register(CharacterTask, handler)` replaces the engine's handler of that task for every row; a mod registers rows only. A row
  from 1000 up has one owning mod: claim every number you use as a row (`AddJobType` or `AddGoalType`; one number may be both of one mod's); a
  mod's `Register(row)` or `RegisterOnSite(row)` of a row another mod claimed, or registered first, throws. Handlers run in the serial phases
  only (`TaskStart.Start` throws in a parallel one). Handlers are registered only inside `RegisterTasks` (a `Register` elsewhere throws; a system
  added by `World.AddSystem` gets its `RegisterTasks` too). A throw from a mod system's `RegisterTasks` (a refused row included) is logged, that
  system's registrations are undone and the system is stopped (Tick systems above); the world is still made. The handlers a mod system
  registers in `RegisterTasks` are guarded (`ModTaskHandler`): a throw from
  one, whichever system started or ended the task, the engine's included, is a fault of the registering system (it stops, Tick systems above),
  and the task starts or ends without the handler from then on (**Observed**, by reading `TaskHandlers.RegisterMod`).
- **Goal claims** (`IGoalSource`, [claims](game/ai-tasks.md#perma-jobs) "Claims"): a registered system implementing it adds the (worker, target)
  pairs it acts out now in `Publish(World, SquadGoals)` with `goals.Add(squad, worker, GoalTarget.Of(body) or GoalTarget.At(site id))`. It is
  asked serially, in system order, each time a finder takes a snapshot (`SquadGoals.Of`) and once at the end of every tick; a pair stays claimed
  3 s after it was last published (`World.Claims`, `GoalClaims.Seconds`). The finders that skip what a squad mate already works on
  (`SquadGoals.Working`, `IsMine`: NPC looting, the medic and splint finders, captive exports, production errands) then skip the mod's targets
  too. It chooses nothing and starts nothing. `Publish` only reads the world, in a fixed order: the claims steer later starts, so they must be
  the same on every run. A throw is not caught as a mod fault: the world's end of tick and the engine's finders call it.
- **Mod jobs:** `AddJobType(type, name)` claims a task type number from 1000 up (`SimulationExtensions.FirstModCode`; the game's tasks stay
  below it; one mod per number, also refused when another mod holds it as a goal type; a mod may hold one number as both). The mod
  puts such jobs in `CharacterCold.Jobs` and runs them itself (an `IJobSource` its system registers, Chooser goals and jobs below); like the
  game's jobs, they are worked only while the character's jobs switch is on. Taking a mod job off the list does not stop a task the mod already
  started from it, so the mod checks that the job is still listed. The jobs panel names them `name`. A save keeps each in its place in the list
  under Meitou keys (`meitou mjob<k>`, [save](formats/save.md)), out of the original's `pjob` list, with its building id but not a character
  target; a load puts it back while the type is registered and drops it otherwise.
  `AddJobOffer(new ModJobOffer(type, buildings, caption, takers))` (engine API, not in the original; after the type's `AddJobType`, one offer per
  type) lets the player give such a job: the menu of a finished building of the player's faction whose record is in `buildings` (the one with
  its Dismantle, [ui-input](game/ui-input.md#context-menu-verbs); a built bed's too, a cage's own rows not) ends with `caption`, translated as
  the game's rows, for a selection holding a character `takers` allows (`ModJobTakers.People`, `Animals` or both), an animals-only selection
  included. Choosing it, Shift or not, queues the game's `AddJob`, which tests the building again and gives each selected living player
  character `takers` allows the job there: one per type, so a job of the type it holds moves to the new building in its place in the list (the
  mod checks the job's building as it checks that the job is listed; the same building changes nothing); the jobs switch turns on and the
  character plans again. Refused: a type the mod has not claimed, a second offer for a type, no building, a blank one or one that is no
  BUILDING of the loaded data, a caption that is blank, over 32 characters, holds a control character or is the label of one of the game's
  verbs, takers that are none or other than people and animals. **Observed** (2026-11-22, scratch world with a headless interface): an
  animals-only selection is shown the row and takes the job, a person selected with it does not for an animals offer, a second building moves
  the job in its place between two game jobs, and another record's, another faction's or an unfinished building gives nothing.
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
  tick. `Read` runs only when the save has data for the system. Vanilla save files stay byte-exact. `AddSaveWritten(folder => ...)` on
  `ModContext` is told the full path of each save folder once the game has written it (Save window, quicksave, autosave), on the host's thread
  while a tick may be in flight (it must not touch the world), so a mod writes a file of its own beside the save; a throw is logged and the
  listener is not called again. The folder a load or import read is `world.Start.SaveFolder` (World ready). Data kept per character is keyed by the
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
  A text box is `new UiNode(id, UiNodeKind.TextBox) { Text = text, Action = action, MaxLength = 32 }`: a one-line MyGUI `EditBox`
  (skin `Kenshi_EditBox` unless the style names one; `MaxLength` 0 is MyGUI's 2048). A click gives it the keys through the screens' edit focus
  (caret, selection, undo, clipboard: [edit boxes](game/ui-input.md#edit-boxes-and-keyboard-focus)) until a click elsewhere, Enter, or the box
  disappears or is disabled; meanwhile the interface's keys go to it, as for a window's box. Each change of its text runs `Action` with
  the new text in `UiActionContext.Text` (empty for clicks on other nodes). `Text` is the box's text: whenever the node's differs the box
  takes it, so a mod stores the text its action gets and echoes it, or sets another (empty to clear). Engine API, not in the original.
  A node's own looks override its style without registering one: `TextScale` (above 0, at most `UiHost.MaxTextScale`, 8), `TextColour`,
  `Opacity` (0 to 1, multiplied into its fill, picture, skin and text and into its children's; a transparent node still takes the pointer) and,
  on an image, `ImageCrop` (`UiImageCrop.Pixels(x, y, w, h)` or `UiImageCrop.Uv(u0, v0, u1, v1)`, not for a portrait). Inline `#RRGGBB` tags
  colour a node's text after them (`##` is a #), as in the game's texts. A non-finite or out-of-range look leaves the contribution out and is
  reported, as a non-finite placement is. `Animated` lays the overlay out every frame while the node shows (fades), instead of at least 20 times
  a second. A contribution to `UiSlots.Overlay` whose root sets `ShowWithHudHidden` is also drawn while the interface is hidden (F7, not the
  cinema's clean picture or recorded frames), laid out every frame with no bindings, without input or portraits. Pointer events:
  `Pointer` (`UiPointerEvents` flags `Release`, `Drag`, `Wheel`, `Hover`) makes the node's `Action` get them besides the left press, all through
  `UiActionContext`: `Kind` (`UiEventKind`: `Press`, `Text`, `Release`, `Drag`, `Wheel`, `Enter`, `Leave`, `Submit`), `Node` (the asking node's id),
  `Position`, `Start` (the press), `Delta` (since the last drag event), `Wheel` (steps, above 0 up) and `Over` (for a drag or release, the
  topmost overlay node's id under the pointer, a drop target). A left press on a node or a child is held by the nearest one asking for
  `Release` or `Drag` (while held the world does not see the pointer), which gets each move and the release wherever it happens; the wheel and
  hover go to the nearest asking. `Buttons` gives the nearest asking node the presses of the other buttons (right, middle, extra) as `Press`
  with `UiActionContext.Button` (the left for every other press); such a press is then the node's, not the game's (no context menu, no
  order). A window is made draggable by adding `Delta` to its placement. `UiNodeKind.ScrollView` lays out its
  children by its flow shifted by its offset and clipped to it; while they overflow it shows a `Kenshi_ScrollBarV`-skinned bar (14 pixels, at
  its right, unless `ScrollBar` is false); the wheel over it or a child that does not take the wheel, the arrows (`ScrollStep`, 24 pixels),
  the track (a page) and the thumb scroll it. Its offset is kept by the extension and node id across frames and clamped to its content, and
  is forgotten when the extension is disposed. `registration.ScrollOffset(nodeId)` reads it (pixels from the top of the content, as last laid
  out or set; 0 for one never scrolled) and `registration.ScrollTo(nodeId, offset)` sets it (finite; below 0 is 0; the next layout clamps it to
  the content), on the host's thread, for the extension's own scroll views (a screen control's too, which takes no wheel or bar input). A text
  box whose node sets `Focus` takes the keys when it shows with it and did not the frame before (so a node kept set focuses once; not a
  disabled box), the caret at the end; Enter in a mod's text box lets go of the keys and runs its `Action` as `Submit` with its text. Events run
  on the host's thread like any action; a throw is reported each time and leaves the press held until the release. With no overlay node
  nothing changes. Engine API, not in the original. **Observed** (scratch program, headless UiHost and MyGuiScreenHost): `Buttons`,
  `ScrollTo`/`ScrollOffset` with clamping, `Focus` once per request and `Submit`; no test, not seen in a running game.
- **Interface actions:** `context.Interface` (`ModInterface`) acts on the game's screens from the host's thread (a UI action, key action, menu
  row, hit feed): `OpenScreen(ModScreen kind, ScreenTarget target)` opens `CharacterEditor` (a recruit's edit, free with race and gender kept,
  of `target.Characters`; false while no listed character is in the snapshot yet, so a mod opening it on a character made this tick tries again
  on later frames, or while an editor or a modal screen is open), `Overview` (tab `target.Tab`: 0 map, 1 faction, 2 research, 3 crafting, 4
  squads) or `Build` (build mode, as B); `IsOpen(kind)` and `ScreenBounds(kind)` (the window's last placed rectangle in pixels, null while
  closed) read them; `BuildEntries()` lists the build window's buildings in its own order (`UiBuildEntry`: BUILDING id, name, category, group,
  group index; none while it is closed), `BuildChoice` is the building it holds chosen and `ChooseBuild(id)` chooses one as clicks on its
  category, group and variant (false when the window is closed or does not list it). A call off the host's thread or without an interface
  answers false, empty or null and is logged once per mod and problem. Display state only: nothing enters the state hash or a save. New
  `ModScreen` kinds are added at the end. Engine API, not in the original.
- **Game speed:** `context.Interface` also sets the game clock through the speed buttons' and pause key's own path, on the host's thread as
  above. `Speed` (0 while paused by the player), `Paused` (no ticks run: the player, a menu or the loading pause) and `Speeds` (1 and the
  player's two fast speeds) read it; `SetSpeed(speed)` is a speed button (0 the pause button; above `MaxSpeed`, 20, refused); `Pause(true)` is
  the pause key's pause (the speed it resumes at is stored) and `Pause(false)` its resume (only while the speed is 0). `HoldPause()` returns a
  `ModPauseHold` for a mod's window: the first hold of all mods keeps the player's speeds, each hold pauses the game when it runs, and
  releasing the last (`Release()` or `Dispose()`) puts the kept speeds back while the game is still paused, so a speed the player chose
  meanwhile stays; a new session drops the holds (`Held` false). Refused calls answer false, 0, none or null. The clock decides only how
  many fixed ticks a frame runs, so speed never changes a tick's result or the state hash. Engine API, not in the original. **Observed**
  with a scratch program against a stub interface (speeds, pause and resume, nested holds, a player's speed kept, a new session, off-thread
  refusal); no test yet.
- **Screen controls:** in `Register`, `registration.AddScreenControls(screen, anchorWidget, placement, context => node)` adds a `UiNode` tree
  to a faithful screen (its id as the interface registers it, e.g. `stats`, `inventory`, `dialogue`, `bench`, or its kind: a pinned inventory
  is `inventory`) or the HUD (`UiScreens.Hud`; the time controls are `TimeMoneyPanel`), laid out against the arranged widget named
  `anchorWidget` by `UiAnchorPlacement`: `Inside` its rectangle (clipped to it), or in a rectangle of its size just `Above`, `Below`, `Left` or
  `Right` of it (clipped to the screen only, so it may lie outside the window). The node's `Placement`, flow and styles work as in the overlay; it
  is drawn over the screen after its own controls, and a node's `Action` names an action of `RegisterAction`, run on a click (the screen's
  click sound first). A node (or a parent) asking `Pointer` `Release` or `Drag` holds a left press as on the overlay: its press runs at the
  button's down without the click sound, then each move (`Delta`) and the release reach the holder wherever the pointer goes (`Over` names
  overlay nodes only), and the screen sees none of it (no window drag); `Buttons` works as on the overlay. The wheel, hover and a scroll view's
  bar are the overlay's only. `UiScreens.ItemCells` as the anchor builds once per item cell of a character window (inventory, bag, limbs, loot), at
  most 128 cells per source and frame, with `Item` set; `UiScreens.Portraits` on the HUD builds once per squad bar portrait shown (anchor: its
  picture, clipped to the bar's page), with `Character` the member. On the overview anchored to `MapImage`, `Anchor` is the whole map image
  (it pans and zooms) and `Map` (`UiWorldMapView`, see "World map input") is set while the Map tab shows. `UiScreenContext` gives `Screen`, `Kind`, `AnchorWidget`, `Anchor` (the arranged
  rectangle), `Character` (a character window's own, the stats window's selected, the NPC talked to, the HUD's selected or lead; null for
  none), `Building` (a machine or storage window's site id), `Item` (`UiScreenItem`: the holder, an `ItemOwner`, and the `InventoryItemView`),
  `Snapshot`, `Tick`, `Enqueue` (from an action), `Frame` (registered controls), `Widget` (`UiScreenWidget`: the anchor's name, type, caption,
  action, argument, rectangle and enabled state as arranged) and `State` (see "Screen overrides"). Return null to add nothing. Nothing shows while the
  screen is closed or its layout lacks the widget (a UI mod's), and without a source the screens are as before. A text box node works as on
  the overlay (see "Interface"). The
  source runs on the host thread each time the screen is arranged (each frame it is open), so it must be cheap and must not touch the world.
  At most 256 controls of all sources per screen (the rest left out, logged once per screen); a source that throws or lays
  out a node not finite is logged once and dropped; an action that throws is logged. Engine API, not in the original. **Observed** with the
  installed HUD and stats layouts headless (placement, clicks, disposal); no test yet, not seen in a running game.
- **Screen overrides:** in `Register`, `registration.AddScreenOverrides(screen, widgetPattern, context => override)` changes a faithful screen's
  or the HUD's own widgets (screen as for screen controls). Each time the screen is arranged, after its own content and before the extensions'
  screen controls, the source is asked for each arranged widget whose name matches `widgetPattern` (exact, or `*` and `?` wildcards; at most
  512 widgets per source and frame), with `UiScreenContext` (`Widget` the widget, `AnchorWidget`/`Anchor` its name and rectangle, `Item` for an
  item cell, `Character` for a portrait or the screen's) and answers a `UiWidgetOverride` (null: no change): `Caption` (its text, at most 4096
  characters, inline `#RRGGBB` tags as in the game's texts), `TextColour` (RGBA, each 0 to 1, before the skin's) or `Visible` false (left out:
  not drawn, not clicked; its children are widgets of their own). Sources apply in registration order, each seeing the last one's changes.
  `UiScreenContext.State` is what a screen keeps to itself, by key: the stats window's `selected` (the stat its description shows: a stat's
  number as in its rows' `stat:N` actions, a mod skill's `mod:N`, empty for none) and `race-bonus-line` (the race XP bonus paragraph its
  description body `stat-description-body` ends with after a blank line, empty when it shows none); other screens publish none yet. It runs on
  the host thread and must be cheap and not touch the world; a source that throws or answers a caption too long or a colour out of range
  changes nothing that frame and is logged once and dropped. Without a source the screens are as before. Engine API, not in the original.
  **Observed** (scratch program, headless UiHost with stats-like controls, and screen-control drags and buttons); no test, not seen in a
  running game.
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
  `registration.AddMenuVerbs(context => rows)` is the same for every target: `UiMenuTargetContext.Target` (`UiMenuTarget`: `Kind`, the
  menu's `Name`, `Position`, and by kind `Character`, `GroundItem`, or the container's, door's or building's `Id` and `Record`) with the
  selection, cursor, `Shift`, `Tick` and `Enqueue`; it is asked only with a selection. Kinds: `Character` (after the character rows, same
  rules), `Container`, `Door` and `Building` (a player-built site, also a bed's or cage's, after the game's rows), `GroundItem` (the hold
  menu Pick Up plus the rows; a quick click still picks up) and `WorldPoint` (the ground under the cursor, `Position` the point; an untitled
  menu of the rows; a quick click still moves there). A target that gets no rows behaves as without the mod. Engine API, not in the original.
  **Observed** headless (rows, faults; not the menus in a running game).
- **World map input:** `registration.AddMapInput(input => taken)` gets `UiMapInput`s while the overview's Map tab shows: `Press` (a mouse
  button down over the map image or its markers, not over a mod's own control, which its screen takes), `Key` (a key no text box took;
  `Typing` says whether one has the keys) and `Closed` (the map stopped showing; every handler hears it). Each carries `Button` or `Key`,
  `Point`, `Shift`, `Ctrl`, `Alt`, the `View` (`UiWorldMapView`: the map image and viewport on screen, the zoom level and UI scale;
  `ScreenToMap`/`MapToScreen` for map points 0..1, `ScreenToWorld`/`WorldToScreen` and the static `MapToWorld`/`WorldToMap` for world
  X, Z), `Snapshot`, `Tick` and `Enqueue`. Returning true takes the press or key from the game (no pan, no move order, no hotkey); the
  first handler in registration order that takes it ends the round. It runs on the host thread and must not touch the world. A handler
  that throws is logged once and dropped. Draw on the map with `AddScreenControls("overview", "MapImage", ...)`. Engine API, not in the
  original.
- **Debug rows:** `AddDebugCommand(label, run, hasField)` adds a row to the F10 menu after the vanilla ones. `run` gets
  `DebugCommandContext` (field, selection, camera target, tick, `Enqueue`) on the host thread while a tick may be in flight, so it must
  not touch the world; it returns the status line, and a throw shows as the status.
- **Key actions:** `AddKeyAction(name, label, pressed, defaults)` (`ModKeyAction`, `src/Meitou.Game/Host/Input`) adds an action the player binds
  like the game's: a row `label [mod name]` on the settings' Controls page after the game's rows, in load order, rebound there as theirs are
  (a key taken from another action, the game's or a mod's, leaves it, with the same notice; Delete or Backspace unbinds). At start the game's
  bindings are laid first (the player's saved ones included), then each mod action's saved keys, in load order, then the `defaults` (keys
  `Key.G`, buttons `MouseButton.Button4`) of the actions with nothing saved, in load order; a key another action of the general key map
  holds by then is left out with a log line, so neither a default nor a returning mod takes a key the player chose (a key only build mode's
  map holds is no conflict, as on the Controls page: [build map](game/ui-input.md#4-action-table)). Escape, F6, Alt, Ctrl and Shift as
  defaults refuse the mod; Insert (the console on the Vulkan host), Delete and Backspace (the page's unbind keys) are left out with a log
  line, and the page refuses them for a mod's action as reserved. Restore defaults lays every action's defaults again, logging those left
  out. The player's
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
  A plain binding is one key or button, as the game's own bindings are in Meitou: the action fires whatever Shift, Ctrl or Alt is held, and the
  press tells which in its context. A mod's action may also take chords (`KeyModifiers`): `Binding.Of(Key.P).With(KeyModifiers.Ctrl |
  KeyModifiers.Shift)` as a default, or on the Controls page its key pressed with the modifiers held (a modifier alone waits; the game's rows
  stay plain). A chord fires only with exactly its modifiers held; its key going down that way is the chord's before photo mode, the
  interface's keys and the game's bindings (Ctrl+Shift+P is not photo mode's P, Ctrl+J not Rescue's J), and while that key stays down no plain
  binding of it fires. A chord is not taken while a text field has the keys, over the title or in photo mode. It is saved as `Ctrl+Shift+P`
  (modifiers in any order when read); the game's own actions take no chord (one in `bindings` is skipped as unknown). The host's own
  combinations never reach the bindings: Alt+Enter, Alt+F4, Shift+F9 and its Ctrl+F9 alias, Ctrl+C, Ctrl+V and Shift with F1, F2, F11 or
  F12 are left out as defaults with a log line and refused on the page; on the Metal host a chord of F7, F8, F11, F12 or Print Screen never fires. **Observed** (scratch
  program on `InputBindings`: laying, taking, resolving against photo mode's P, saving and reading back); the hosts' key path and the Controls
  page not run.
  Once per press, also while paused, `pressed(ModKeyContext)` (the `Snapshot` on screen, `Selected`, `Tick`, `Enqueue`, and `Ctrl`, `Shift`,
  `Alt`: either key of each held as it was pressed, so a mod can give Ctrl+key its own meaning) runs on the host thread at the control tick,
  so it must not touch the world, must not block, and queues commands for gameplay; `action.Held` is its state at the last control tick, for
  host-thread readers. A throw is logged with the mod id and the name and that action gets no more presses.
  `name` (1 to 48 letters, digits, `_ . -`, unique within the mod) keys the saved keys; a bad name, or a label that is empty or blank, over 48
  characters or on more than one line, refuses the mod. The actions are numbered `InputAction` from `InputBindings.FirstModAction` (1000) in
  load order, for this session only. Without key actions the bindings, the page and `meitou.user.json` are the game's. Engine API, not in the
  original.
- **Frame:** `AddFrame(frame => ...)` on `ModContext` calls the mod once a frame on the host's thread, after the combat hit feed and before the
  frame is drawn (not over the title), with `ModFrame`: `Snapshot`, `Tick`, `Selected`, `Shown` (the HUD's character), `Hovered` (under the
  pointer), `Pointer`, `Ctrl`/`Shift`/`Alt` held now and `Highlight` (the game's Highlight key), `Paused`, `Speed`, `ScreenSize`, `UiScale`,
  `UiVisible`, `CameraEye`, `CameraTarget`, `Project(point)` (a world point's screen pixel, null behind the camera), `OpenScreens` and
  `IsOpen(id)` (the game screens' layout ids), `Bindings(action)` (the player's keys for a game or mod action) and `Notify(text, error)` (the
  interface's notice line). Reads are live and read only; a tick may be in flight, so the callback must not touch the world (commands through
  `Enqueue`), must be cheap and must not keep the frame. A throw is logged and the callback is not called again. A key action's
  `ModKeyContext.Frame` is the same view at the press. Engine API, not in the original.
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
- **Item tooltip lines:** in `Register`, `registration.AddItemTooltipLines(context => lines)` adds `UiItemTooltipLine`s (a label and an
  optional value) to an item's tooltip, after the game's own rows and before the record's description. The source gets `UiItemTooltipContext`:
  the item's record string id, name, code-mod tag (`ItemInstance.ModTag`, so a mod shows what it keeps on the item: a condition, rolled
  affixes; a trader's stock item has none), quantity, level and quality. It runs on the host thread each time a tooltip is built, so it must be
  cheap and must not touch the world; it acts on the world only through `Enqueue`. The context also names the item: `Owner` (an `ItemOwner`:
  a character's inventory, a machine's or storage's site; null for a trader's stock, a crafted product's preview) and its `Index` there (-1
  without an owner; it names the item only until the inventory changes, so the mod's command checks the record and tag it expects), and
  `First`, true for the first build of the tooltip for the item as it is (another item shown before, or the item changed, its tag included),
  false while the same tooltip is built again each frame. So a mod rolls state on the first tooltip by queueing its own command when `First`
  is true and the item has no tag of its; its system tags the item (`ItemInstance.ModTag`) in a serial step. `Style` colours a line (see
  "Line style"). At most 8 lines from all sources together; a label or value is put on one line and cut to 80
  characters, a blank value is none. A source that throws or gives a line without a label is reported once and dropped for the session.
  **Verified** by `ItemTooltipLinesTests` (order, the context, disposal, the cap and cut, a throwing and an unlabelled source dropped).
- **Town panel lines:** in `Register`, `registration.AddTownPanelLines(context => lines)` adds lines to the HUD's town panel (the outpost the
  camera is in: name, Open to public, Research, Power, Battery charge; [town panel](game/ui-screens.md#town-panel)) under the game's own, in
  registration order (mods in load order), so several mods share it. A `UiTownLine` has a `Label` (in the labels' column, drawn as the game's
  labels), an optional `Value` (in the bars' column, centred, in the labels' look; blank is none) and `Tooltip`, and an optional `Press`: with a
  value the value is a button's caption, without one the whole line is one button captioned with the label (the game's neutral button, its
  caption in the panel's main colour). At most four lines show, of all mods together, so keep to one; the lines past them are left out, logged
  once per interface extension. The panel grows upward by one of its rows per line: its lower edge stays, and its own rows keep their distance
  from its top, so they move up with it. Return the same lines for an outpost every time, changing only their text (a line with nothing to say can
  show a dash), so the panel keeps its shape while the player looks at it. Each line is placed and sized as a copy of the Battery charge row; a
  layout the lines cannot be placed in (a UI mod's without the Power and Battery rows and bar as direct children of the panel, or with a child of
  the panel aligned other than to its top) shows none. A label or value is cut to 64 characters and put on one line (line breaks, `\n` and
  control characters become spaces), a tooltip is cut to 1,000; the panel shortens what does not fit its column, and colour tags work as in the
  game's texts. Labels, values and tooltips go through the translation catalog, as the game's own. The source runs on the host thread whenever the
  panel is arranged (each tick, each mouse move, at least 20 times a second while it shows), so it must be cheap and read only its context; a
  tick may be in flight, so it must not touch the world. It gets `UiTownContext`: `Town` (the `OutpostSnapshot`: `Number`, the
  `PlayerTown.Number` commands name it by, numbered again in load order when a save loads; `Name`, `Owner`, `Public`, `Power`), `Owned` (whether
  the player's faction owns it; the panel shows only the player's outposts today), the `Snapshot` on screen, `Tick` read when asked, and
  `Enqueue`, to be called only from a press. A press plays the panel's click and runs only while the panel still shows the outpost it was shown
  for; it changes the world only by queueing commands, which the mod's system applies in its serial `Inputs` step, checking that the outpost still
  exists (`world.Buildings.FindTown`) and, for a change only its owner may make, that its `Owner` is still the player's faction. A source that
  throws, or returns a line without a label, is logged once and dropped for the session; a press that throws is logged. The hook keeps no state:
  nothing of it enters the state hash or a save. Without a source the panel is as before. Engine API, not in the original.
- **Character panel lines and job panel buttons:** in `Register`, `registration.AddCharacterPanelLines(context => lines)` adds lines to the
  HUD's character panel under its stats lines (`StatsText`), and `registration.AddJobPanelButtons(context => buttons)` adds buttons beside the
  jobs list (`JobsPanel`), in registration order. Both sources get `UiCharacterContext`: the `Character` the panel shows (the `CharacterSnapshot`:
  skills, XP multipliers, jobs, orders, faction; for the character panel an inspected other faction's character too, for the jobs list the
  selected one), its `Id` and `Name`, the `Snapshot` on screen, `Tick` and `Enqueue` (from a press only). A `UiCharacterLine` has a `Label` (the
  stats labels' column and look), an optional `Value` (the value column; blank is none), an optional `Bar` (0 to 1, clamped; the game's progress
  bar in the value column with the value centred on it; not finite: the source is faulty), `Tooltip` and `Press` (as a town panel line's: a
  button for the value, or one button across the line without a value; a line with a press draws no bar). Lines take the rows after the stats
  lines at their pitch; those that do not fit in `StatsText` are not shown (at 1080 lines it holds about six rows, the game's stats take some),
  and a layout without `StatsText` (a UI mod's) shows none. A `UiJobButton` is a caption, a tooltip or null, and a press. The orders panel has
  no free room, so when a source gives buttons the jobs list and its empty-list hint start one job row (int(24 × height / 1080)) lower in
  their frame and the buttons share that row above the list, side by side across its width; a layout where the list or one of its parents is
  not placed in fractions of its parent (a UI mod's) gets none, and without buttons the panel is as before. Limits, cuts and faults as the town
  panel lines: at most four lines and nine buttons of all mods together (the rest left out, logged once per extension), texts cut to 64
  characters on one line, tooltips to 1,000, translated; a source that throws or gives a line without a label (a button without a caption or
  press) is logged once and dropped for the session; a press that throws is logged. A press plays the click and runs only while the panel
  still shows the character it was shown for. Sources run on the host thread whenever the HUD is arranged (each tick, each mouse move), so they
  must be cheap and must not touch the world. A `UiJobButton` with `Lit` shows in its skin's selected state (the tab a character is on).
  `AddJobPanelButtons(source, UiJobButtonLayout)` also places the buttons: `Rows` (the default, as above, wrapping into more rows above the
  list after `PerLine` buttons) or `Columns` (left of the list, which starts that many columns further right, `PerLine` buttons a column, each
  a job row high and `ColumnWidth` job rows wide; 0.5 to 6); the first registered source that gives a layout sets it for all, and a list
  that would keep less room than it gives up shows no buttons. **Verified** by `CharacterPanelLinesTests` (order, context, disposal, caps and cuts, faulty
  sources dropped; with the installed HUD layout: placement under `StatsText`, the list lowered and restored, presses for the shown
  character). Not yet seen in a running game. Engine API, not in the original.
- **Line style:** item tooltip lines, character panel lines and town panel lines take `Style = new UiLineStyle(Label, Value, Icon)` (an init
  property): `Label` and `Value` are text colours (RGB 0 to 1, alpha not used; not finite: none; clamped) over the panel's own (a button's
  caption takes the colour of the text it shows), and `Icon` an image file path (as a `UiNode`'s `Image`, e.g.
  `Path.Combine(context.Folder, "rank.png")`) drawn in a square of the row's height before the label, which moves right. Tooltips draw no
  icon. Engine API, not in the original.
- **Dialogue replies:** in `Register`, `registration.AddDialogueReplies(context => replies)` adds player replies to the open conversation,
  after the game's own choices, numbered on with them (the number keys pick them too). `UiDialogueContext` gives the `Conversation`
  (`ConversationView`: NPC, player and speaker ids, the line, the game's choices, the log), the `Npc`, `Player` and `Speaker` snapshots (record
  id, faction, name; null when not in the snapshot), the `Snapshot`, `Tick`, `Enqueue` and `Leave()` (queues `EndConversation`). Answer the
  `UiDialogueReply`s (text, press) to offer for that line, or none; the source is asked again only when the NPC, the line or the choices
  change, so it must not decide by time. A chosen reply runs its press on the host thread (it may open the mod's own window, queue commands
  or `Leave()`); the conversation goes on unless the press ends it. At most four replies of all mods together; a text is put on one line and
  cut to 200 characters. A source that throws or gives a reply without a text or press is logged once and dropped; a press that throws is
  logged. Without a source the conversation window is as before. Engine API, not in the original.
- **Settings:** `AddSettings(object)`, once per mod: the object's public get/set properties marked
  `[Setting(label, Hint, Min, Max, Step, Options, SubGroup, VisibleWhen)]` (`ModSettings.cs`) become one group, in declaration order (a
  sub-group's settings together at its first one's place), under the mod's row on the Settings screen's Mods page
  ([native UI](native-ui.md#settings)): `bool` an Off | On switch, `int`/`float`/`double` a slider (Min and Max required; Step 0 = 1 for `int`, a
  hundredth of the range otherwise), an enum or a `string` with Options a list, a `string` without Options a free text box (one line, at most
  `MaxLength` characters, 0 meaning 256, up to 4096; a click types in it, Enter, Tab or a click elsewhere keeps the text, Esc drops it; control
  characters become spaces, a longer text is cut, and a saved value that is too long or holds a control character is ignored). `SubGroup` puts a setting under a sub-group header of that name (its
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
- **Ammunition** (data; engine extension, not in the original): one kind per gun needs nothing new: a CROSSBOW loads the ITEM its `ammo` names (only
  the first entry is read), so a mod's gun naming its own cartridge loads only that and no base crossbow loads it. CROSSBOW reference list `meitou
  ammo` adds more kinds after `ammo`, in the order the gun prefers them (with `ammo` empty, the list alone; a crossbow with neither has infinite
  ammunition); an entry that is missing (a dependency mod not enabled) or not an ITEM record is left out with a warning in the log, and a crossbow
  with no `ammo` whose list leaves nothing loads nothing (with a warning), never turning infinite. A reload loads the first kind the shooter carries
  (its sections, then its backpack; the list's order wins) and takes its bolt; the `num shots` it loads keep that kind until spent, also through a
  swap to another crossbow. ITEM floats `meitou damage mult` and `meitou bleed mult` (default 1, 0 to 100), `meitou range mult` (default 1, 0.1 to
  10), `meitou speed mult` (default 1, 0.1 to 10) and the file `meitou live ammo` change a bolt of that item, with any crossbow whose list holds it
  (its `ammo` included): its pierce damage; the crossbow's range and shot speed times the kind's, for the bolt's flight and for the shooter's range
  test, line of fire and aim lead; the bleed multiplier of its hit (the original's 2 times it), before code mods' hit filters; and its mesh in
  flight and stuck in a body or the ground (the bolts drawn loaded on the crossbow keep its own `live ammo`). A value that is not finite reads 1 and
  one outside its bounds the nearest bound, each with a warning in the log. The tooltip's "Ammunition type:" names every kind; it and the
  ammunition's tooltip show neither the kinds' numbers nor which kind is loaded, and the player cannot choose one but by what it carries. An NPC
  made with such a crossbow gets one stack of `ammo` (else the list's first ITEM) unless it holds a kind of the list. Turrets are not affected.
  Loaded bolts are not saved, as before. Without these fields the ranged fight, its state hash and NPC kits are as before. The construction set does
  not offer the fields (its `fcs.def` declares none of them): write the records with `FcsWriter` (`src/Meitou.Data/Fcs`). A gun should still name
  its first kind in `ammo`, the field the original game reads.
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
  On `ModContext` a name ending in `.wav` is a loose file under the mod's folder (a relative path; one outside it or missing plays nothing,
  logged once), a 16-bit PCM WAV of at most 20 s, read once by the host: it plays as one Wwise sound made for it, on the mods' interface
  object or on a pooled object at the point or the character's place (it does not follow), through the SFX bus `59927CD0` when placed (the
  SFX slider and the pause ducking take it) with Meitou's attenuation (linear from full at the object to silence at 1000 units), on the
  master bus on the interface (no slider). `PostSound(sound, EffectAnchor at, volume = 1)` plays an event or loose file on an anchor: a
  point there, a bone or weapon on its character, a building or bolt where it is drawn now (host thread only); `volume` (0 to 4) scales a
  loose file, a bank event keeps its level; false when nothing was queued. **Observed** by a scratch program mixing a synthetic WAV offline
  (heard at the listener, distance attenuation, volume), not heard in the game.
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
- **Commands while paused:** while the clock is paused the world applies the interface's commands between ticks
  (`World.ApplyPausedInterfaceCommands`: selection, orders, inventory, trade and the other game interface commands) and a mod's command waits
  for the next tick, unless its type implements `IPausedCommand` (`Meitou.Simulation`, a marker). Such a command is taken in that step, in
  queue order with the game's, and given to the `Inputs` of every system implementing `IPausedCommandHandler` (and the game's command systems),
  in system order, inside the step's serial input phase; no other phase runs and time does not advance, so the handler only applies its
  commands. Running, it is applied at its tick as any command. Without a handler system no command is taken early. A handler that throws is
  disabled with its fault in the state hash, as in a tick. Deterministic as the game's paused commands: same commands, same order, between the
  same ticks. Engine API, not in the original. **Observed** (scratch program, a bare world): marked commands reach the handler in queue order
  without a tick, unmarked ones and those of a world without a handler wait for the next tick; no test.
- **Player squads on AI packages** (`Meitou.Simulation.Ai`, [rules](simulation.md#autonomous-player-squads)): `SetSquadAutonomy(squadId, on)` lets
  the AI run a player's squad; then `ChangeAi(squadId, packageId)` and `StartContract(squadId, packageId, hours)` give it packages as dialogue's
  `change AI` / `AI contract` give an NPC squad's (both are ignored for a player squad that is not autonomous);
  `SetSquadPackages(squadId, [new SquadPackage(slot, packageId), ...])` sets several by slot (slots 0 to 1000; the board tries the highest slot
  first, a slot's in the order given, and chooses at once; `ChangeAi` is its one-package case in slot 0; an empty list, a slot out of range or an
  unknown package refuses the whole command with a log line; saved with the squad as `meitou packages`, [save](formats/save.md#npc-platoons-meitou-mapping),
  hashed while set; taken by NPC squads too); and
  `SetSquadHome(squadId, building)` its home: a town building's key (`TownBuilding.Key`, `x.y:instance`) or a player-built building's id
  (`BuildSite.Id`); null clears it. A player-built home in one of the faction's outposts makes the outpost the squad's town:
  going home, town work on the outpost's own machines, wandering between its public buildings, the gate guard's gate (guarded from
  inside) and sitting and reigning on its chairs and thrones use it; on its own walks the squad never unlocks a door or gate the player
  locked, and gives a chair up to the player's characters ([rules](simulation.md#autonomous-player-squads)). The player's orders and
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
- **Squad AI options per squad** (`Meitou.Simulation.Factions`): `SetSquadOptions(squadId, options, Owner: context.Id)` gives a squad of the
  player's faction its own squad AI options (`Squad.OwnOptions`, the `SquadAi` flags: sit when idle, sleep when injured, ditch items, share
  food, feed animals, heal, help and rescue allies, shoot first, heal neutrals, heal all), which its members follow in place of the player's
  faction-wide ones (the AI tab still shows and sets the player's, so a led squad given its own no longer follows the tab); `null` gives it the
  player's again. Guards that should not sit, sleep in other people's beds or haul goods away, for
  example: `SquadAi.Feed | SquadAi.Share`. Every system that reads the options asks `SquadAiRules.For(world, cold)` (the character's squad's,
  else the player's), so a squad with none set behaves as in the original. A command naming another mod than the one that set them is
  ignored. Saved with the squad; a load without the owner mod running gives the squad the player's options again, with a notice. Cost: one
  squad lookup where an option is read (at decisions, and per character at the feed check). Engine API, not in the original.
- **Squad purses** (`Meitou.Simulation.Factions`): `SetSquadPurse(squadId, own, Owner: context.Id)` gives a squad of the player's faction its own
  purse (`Squad.OwnPurse`, its `Squad.Money`): its members pay an NPC-style purchase (a shopper buying from a building's stock) from it
  instead of the player's money, and a shop the squad owns keeps its takings in it, so a shopkeeper keeps its own earnings. The trade window
  pays from the player's money (into and out of this purse when the squad keeps a faction shop, below). Ending it pours what is left into the
  player's money. Saved with the squad; a load without the owner mod running ends it the same way, with a notice. Engine API, not in the
  original.
- **Shopkeepers of the player's faction** (`Meitou.Simulation.Trading`, engine rules: [simulation](simulation.md#shopkeepers-of-the-players-faction-meitou-only)):
  `SetSquadShop(squadId, on, Template, Owner: context.Id)` gives a squad of the player's faction a shop (a faction shop) the player's characters
  trade with in the ordinary trade window, as with an NPC trader. It trades while the squad is independent (`SetSquadAutonomy(...,
  Independent: true)`) and keeps its own purse: then every member that is awake and not fighting offers Trade in its right-click menu and trades
  on a quick right click. Put the keeper in a squad of its own to have one keeper.
  - Money: the shop's money is the squad's own purse, never the player's. The player pays into it and is paid from it at the ordinary prices,
    so trades with the faction's own shop make and lose no money for the faction; no fill or restock sets the purse. The command gives a squad
    without a purse one, empty and owned by no mod, so it stays with the squad when the mod is missing at a load; a purse the mod gave itself
    with `SetSquadPurse(..., Owner)` follows that command's rule. Fund the shop, if it should buy, by setting `Squad.Money` once `Squad.Shop` is
    set (that money is the mod's to give).
  - Stock: `Template` is the string id of a SQUAD_TEMPLATE for the terms (`buy mult`, buys stolen and illegal goods); an id that names none
    refuses the command (a log line, no shop). When the template is a trader's with vendor lists the stock is filled at once and restocked every
    `vendors refresh time` hours while a member lives: those goods are made from nothing, as an NPC trader's, and a restock replaces the stock,
    so goods the player sold to the shop are gone after it. Without a template the stock is the mod's: add items to `Squad.Shop.Stock`
    (`Inventory.Add`, items from `context.Population.Items.Create`) in a serial phase, one tick after the command.
  - NPC shoppers do not buy there (they buy only from shop buildings' containers; the player's shop counters are that route).
  - A second `on` for a squad that has a shop only notes the owner and gives back a purse that was ended; end the shop first to change the
    template. Off, a load without the owner mod running (with a notice) or the player removing the emptied squad ends the shop: its goods go to
    the members, and what does not fit onto the ground (nothing is lost); the purse stays.
  - Several mods each keep their own shopkeepers: a command naming another mod than the shop's owner is ignored (one naming no mod can change
    any shop, as for the other squad settings). Saved with the squad. Engine API, not in the original.
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
  added when no walkable point is found); platoon ids are given anew at every load, so `Platoon.ModTags[context.Id]` holds one tag per mod
  (keyed by the mod's id, so several mods tag the same platoon without replacing each other's), saved and hashed while set, and
  `world.Platoons.FindByTag(context.Id, tag)` returns the first platoon in id order with that mod's tag (it looks through every platoon: call
  it at a load or an event, not per platoon per tick); the older `Platoon.ModTag` is one string shared by every mod (`FindByTag(tag)`); for
  the player's own squads and outposts, whose ids and town numbers are also given anew at every load, `Squad.ModTags[context.Id]` and
  `PlayerTown.ModTags[context.Id]` hold one tag per mod the same way, saved with the squad and on the outpost's town state; `PopulationSystem.SpawnIntoPlayerSquad` makes one
  character of a CHARACTER or ANIMAL_CHARACTER record as a new game makes the player's (a look rolled for the player's faction, body,
  starting items, name; a unique claimed as any spawn claims it) and adds it, standing idle on the ground at a walkable point, to a living
  player squad, leading it when it has no leader. Only player squads: an NPC squad loaded from a save keeps only the save's members, so an
  added NPC would be lost at the next save or unload (`SpawnPlatoon` adds NPCs). `SpawnIntoPlayerSquad(world, squadId, recordId, at, age)`
  makes an ANIMAL_CHARACTER at that age, its stats multiplier and size ([animals](game/animals.md#ageing)), where the four-argument call
  makes the adult of age 1; a CHARACTER ignores the age. It returns the character's id, or none and adds nothing for another squad, another
  record type, a point that is not finite or not walkable, an age that is not finite or below 0, a unique already claimed, or the recruit's caps
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
- **Placing characters** (engine API, not in the original; serial phases and `IWorldReady`, once per event): `CharacterPlacement.Place(world,
  characters, at)` (`Meitou.Simulation.Movement`) puts living characters at a point at once (a start squad at its own place, a scene change, fast
  travel): the first on the walkable spot nearest it, the rest spread round it as a ground-click move order of that many spreads them, each on
  the ground there (the floor nearest the point's height), standing still with its fight, task, path and orders dropped as the Stop order drops
  them; carried characters come with their carrier. Outside a tick both state buffers are written, so a new game's camera, put on the first
  squad's leader after `IWorldReady`, sees the new place. A character carried or held in a bed, cage or on a pole is left where it is. It
  returns how many were put (0 for a point that is not finite or with no walkable spot near it). The zones round a far place load as after a
  long walk; nothing there is preloaded. `CharacterPlacement.WalkableNear(world, point, key)` is the spot search alone.
- **Placing buildings** (engine API, not in the original; serial phases and `IWorldReady`, once per event): `ConstructionSystem.PlaceSites(world,
  plans, complete)` makes a site of the player's faction for each `PlannedBuilding` as build mode's Confirm does (the town block, joining a player
  town or founding an outpost, walls joined, the town reassessed), without research's gate, a message or a build order. With `complete` each
  stands already, built with all its materials delivered (dismantling refunds them as for any finished building) and no completion message;
  doors, beds, seats and machines follow on the next step. Furniture is a second call whose plans name the building's site id as `Parent`, at
  absolute positions the mod chooses inside it. It returns the sites in plan order, null for a refused plan (not a BUILDING, a position that is
  not finite, blocked by a town), so a mod has the id for `SetSquadHome` and the outpost through `world.Buildings.FindTown(site.Town)` (tag it
  with `PlayerTown.ModTags`).
- **Town overrides** (engine API, not in the original; serial phases; [simulation](simulation.md) "Town overrides"): `PopulationSystem.CheckOverrides(world,
  site)` runs a town's `override town` list now, as its zone's load would; `ApplyTownVersion(world, site, townId)` puts a TOWN of the mod's choice
  in its place (null or the town's own id: back to its own record, residents made afresh; also for a held town or one of the player's faction); both
  refuse a town that is loaded or loading, or whose zone is active, and return false, changing nothing. `HoldTown(site, held)` keeps the zone load and the off-screen look from
  choosing a new version for a town the mod drives (not saved: hold it again in `IWorldReady`). A registered system implementing
  `ITownSwitchObserver` (`Meitou.Simulation.Population`) hears of every change as a `TownSwitch` (site, old and new TOWN, null the town's own, the
  unloaded platoons of the old version left homeless, off screen or at load); during that call it may move each with `RehomePlatoon(world, platoon,
  site)`, which puts an unloaded town platoon at another unloaded town at once as a guest (no travel; it keeps its characters, uniques and purse,
  leaves its shop and home, is not in the host's pools, does not stop the host making its own residents, is left alone by the host's later changes; saved, its members come
  back round the host's centre), and those still homeless when every observer has heard are despawned (with Towns change far away off, as in the
  game, they are refused when they would come back instead). `OffscreenSwitches` counts changes made off
  screen.
- **Driving NPCs** (engine API; serial phases): `PopulationSystem.HoldPlatoon` stops a roaming platoon's trips and its
  stand-in's travel (it still unloads, and despawns as any platoon but a mod's spawn). `HoldPlatoon(world, platoon, context.Id, held)` takes
  the hold in the mod's name: only that mod sends or releases it, another mod's hold (named or not) refuses it, and it is saved with its goal
  whenever the platoon is saved (PLATOON keys, [save](formats/save.md#npc-platoons-meitou-mapping): a mod's spawn, a campaign's, a squad once
  made; a town's roaming stand-in never made is not saved, and its town makes a new one); a load without that mod running releases it, with a
  log line. The
  unnamed `HoldPlatoon(world, platoon, held)` is not saved (find the platoon by its tag and hold it again after a load) and refuses a platoon
  a mod holds in its name. `PopulationSystem.SendPlatoon` sends a held platoon's stand-in off screen toward a point or a living character (by
  the roads, at the stand-in travel speed, stopping within the roam arrival radius; null stops it; cleared when the hold ends), with the mod's
  id for a named hold; a named hold follows its character again after a load when the character had a lasting id (Character ids, above) when
  it was sent, and again whenever that character leaves the world and comes back. `population.DespawnPlatoon(world, platoon)` (on the
  population system) takes a platoon out of the world for good as the game takes one it does not keep (its characters, made or not, vanish
  where they stand; its uniques are free again; a save's platoon is left out of the next save), and refuses a town's own, the player's, one
  being made, the parallel phase and the off-screen fights (a death observer told of a fight's dead). The hold is off screen
  only (**Observed**, by reading `AiSystem.MindAndCrowd` and `PopulationRoaming.StepTrip`): loaded, a held platoon's members run their package's full minds as any platoon's, and the mod steers them with orders (Ordering
  characters, below; a member whose package follows its leader follows an ordered leader), a chooser goal in the package, or a package change
  (`ChangeAi`, `StartContract`). The `EndContract(squadId)` command ends a running contract as its hours running out would (a `StartContract` of the
  same package after it starts it afresh; the game ignores a start of the contract already running), and `StartContract { AnyRange = true }`
  lets the contract's assault take its target wherever it is, not only within the manoeuvre mode's reach (about 6517 units for ROAM_FAR;
  saved with the contract). **Verified** by `ModHookTests` (synthetic world): a held stand-in, travel to a point, the contract's end and restart,
  and an any-range target taken past the reach. A chooser goal's act-out (below) drives its character with what the engine's own duties use:
  `TaskStart.Walk`, `CharacterOrders.Sneak` on `Orders` (speed, sight and spot meters follow it for any faction), `LockSystem.Picking.Step` (books the Burglary
  a witness answers), `LockSystem.WorkSpot(world, fixture, from)` (where to stand to work a fixture's lock and how near counts: a door's
  side nearer `from`, out from the leaf, within `DoorWorkReach`; any other fixture its own place within its `UseRange` plus `Reach`),
  `CrimeSystem.Pickpocket`, `CarrySystem.BeginPickUp` / `BeginPutDown`, `ArrestSystem.TakeOver(enslave: true)` (a guard takes the carried
  body to a cage as its faction's slave), `AggroSystem.Spotted` / `Unseen`. `CharacterCold.FedTo` is a free hunger level that comes before the
  game's rule (not saved), for a captive the mod's squad carries.
- **Platoon notices** (engine API, not in the original): a registered system implementing `IPlatoonObserver` (`Meitou.Simulation.Factions`)
  receives `PlatoonChanged(World, in PlatoonNotice)` for each platoon `Added` (a town's pool, a campaign, a split or a mod made it, or a town
  kept its squads on as stand-ins or loaded; not the platoons a save brings, though those whose characters are made at its first ticks are
  told `Loaded`), `Removed` (with `Gone`: `Despawned` by the engine, `LostFight` with `By` the winner's id, `NoMembers`, `Dropped` for a
  save's stand-in a town replaced, `ByMod`, `Other`; the platoon object is no longer in `World.Platoons`, and a loaded platoon removed gets no
  `Unloaded` first), `Loaded`, `Unloaded`, and `Arrived` when a held platoon's stand-in reaches the goal its mod sent it to (for a followed
  character, the route goal, which may trail the character by up to 200; off screen only; told once per sending, saved with a named hold). The
  platoon is to read: change it through the population's API. They are
  told once a tick on the tick thread after the slow world, in system order: the registry's changes in the order they happened, then the
  loads and unloads since the last tick in id order. The observer may read the world, change its own state and call the population's serial
  API (spawn, hold, send, despawn); what that changes is told the next tick. While one is registered, the world notes each change as it
  happens and looks at every platoon's loaded state once a tick; with none it notes nothing. One that throws is disabled for that world (a log
  line; the fault enters the state hash).
- **Off-screen fight rules** (engine API, not in the original; [simulation](simulation.md#off-screen-tier) "Fights far away"): a registered
  system implementing `IOffscreenFightRule` (`Meitou.Simulation.Population`) is asked `Judge(World, Platoon first, Platoon second)` before
  the switch makes two hostile stand-ins fight, `first` the one looking for a fight and its enemies within reach asked about in id order until
  one is not skipped: `Fight` (no say), `Skip` (they do not fight; the first looks for its next enemy within reach, and they may meet again at
  a later check), `FirstWins` or `SecondWins` (the engine settles the
  losses as for a win it drew). Every rule is asked: one `Skip` skips the fight; otherwise the winner the rules name wins when they agree, and
  the engine draws it when they disagree, so the answer does not depend on load order. Asked only while the switch is on, every 2 s, for
  enemies within 300 of each other. A rule reads the world and changes nothing in it; one that throws, answers no verdict, or adds or removes a
  platoon is disabled for that world (a log line; the fault enters the state hash), and a fight whose platoon was removed does not happen.
- **Roads** (engine API; any thread, read only): `PopulationSystem.Data.Roads` (`RoadNetwork`, `Meitou.Simulation.Towns`; null without a
  level) is the road graph off-screen squads travel by, built once per world from the loaded level's ROAD records, those mods add or change
  included, and never changed after. `JunctionCount` and `JunctionAt(j)` are its junctions, numbered from 0 in the order the roads first name
  them (roads naming one ROAD `start` or `end` number meet at one junction, but that record number is not the junction's), `Count` its roads, `RoadAt(r)` a road's record id, junctions and `length` (the cost a trip pays),
  `RoadsAt(j)` the roads ending at a junction, and `PointsOf(r)` its points (X/Z). `NearestRoad(at, within)` is the nearest road point within
  a distance (its road, share of the way along, point and distance), and `RoadsNear(at, radius, list)` fills a list with the roads that pass
  within a radius, in road order: the roads a town or base of that radius sits on. `Route(from, to, roadPreference)` is the trip the
  engine's own stand-ins take. Both searches look at the grid cells their distance covers that hold roads, so a large one costs more, up to
  the whole network.
- **Goal types in data**: a data mod lists an AI_TASK whose `enum` is a number of the mod's own (1000 up) in its AI_PACKAGE records. Without
  the code mod such a goal loads (`AiTask.From` keeps any number) and is never chosen: it has no behaviour, so its desire is 0. The code mod's
  system, in a serial step, finds who has the goal now:
  `AiSystem.WeighsGoal(world, slot, type)` (the lists the chooser weighs: the role's list of the running package, the obeyed squad's
  slave list, the race's `AI Goals`, in the hour's window), or per squad `PopulationSystem.GoalDuty(world, squad, role, type)` (public, from
  `world.Systems`; the running package's role list in the hour's window, a campaign squad's phase package included) or `GoalsOf`, the role from
  `AiSystem.RoleOf(squad, id, cold)`. Only the chooser moves them: a chooser goal of that type (below), which the chooser weighs against the
  package's other goals, or an order (Ordering characters, below).
- **Chooser goals and jobs** (`Meitou.Simulation.Ai`, [rules](simulation.md#engine-goals) "Code mods' goals"; **Observed**, by reading
  `ChooserRegistry`, `ModGoal`, `DutyGoals` and `AiSystem`): `AddGoalType(type, name)` and `AddJobType(type, name)` claim task type numbers from
  1000 up, one owning mod per number across both (a mod may claim one number as both, so it can be its goal type and its act-out's row); `name`
  is for log lines and notices (the HUD's Goal line shows the AI_TASK record's name). A registered system implementing `IChooserOwner` gets `Register(ChooserRegistry)` once when the world is
  made, in system order, and calls `Goal(type, IModGoal)` for a goal type and `Jobs(type, IJobSource)` for a job type its mod claimed. A type not
  claimed, another mod's or registered twice, or a call outside `Register`, throws, and a throw drops everything that system registered, with a
  log line, and stops the system (Tick systems above). A system added later (`World.AddSystem`) registers job contributors only.
  - **Who weighs a goal:** a character whose lists hold an AI_TASK of that `enum`: an NPC's running package role list, its race's `AI Goals`, its
    faction's `AI Goals` while its squad runs no contract, or the slave list of the squad it obeys; a player character's faction's `AI Goals`
    (an animal's race's too) and its autonomous squad's package roles (`AiSystem.OwnGoals`, `PlayerOwnGoals`). It is weighed beside the
    engine's goals by the record's tier (`classification`) and hours, the list's weight and the running goal's continuity (x1.25); the type has no
    commitment or crowding. Without the code mod the goal is inert.
  - **`Desire(in ModGoalAsk ask)`** is the whole desire (0 or below: not wanted), asked in parallel with other characters' asks, on the
    asker's own partition. The ask is a read-only `ref struct` that cannot be kept or captured: `Slot`, `Id`, `Type`, `Targeting`; the asker's own state this tick
    (`Self`, `ref readonly`), `Faction`, `Role`, `PlayerSide`, `IsAnimal`, `Carrying`, `InCombat`, `Orders`, `RunningRow`, `Running` (this goal
    holds it now); another character's state as of the last finished tick (`TryPrevious`, `FactionOf`); its squad (`SquadId`, `Leader`,
    `MemberCount`, `Member(k)`, `Town`, `Package`); `Relation` and `IsHostile` of two factions; `Tick`, `Hour`, `GameHours`, `Seed` (for
    `Rng.Hash`); `Record(stringId)`, the merged game data. Answer from it and from the mod's own state that only its serial steps change; work a
    costly answer out in the mod's serial step and keep it in its hashed state. `mod-check` runs the determinism check on it
    ([Determinism check](#determinism-check)).
  - **`Start(World, in ModGoalStart ask)`** (slot, id, squad, type, `targeting`): the winner stands idle on row 14 Idleness (not on the goal's
    number) and `Start` is called serially, in slot order, in the tick it wins or the next, while the chooser still holds the character for it. It
    finds its own target and starts the act-out with a row of the mod's own and `TaskSource.Goal` (`TaskStart.Walk`, `ProductionSystem.SitOn`),
    and answers true: the goal then holds the character, the chooser keeps weighing it with the continuity, and the AI runs the character while
    its task is the act-out (the task `Start` left running, or a goal's task started since). False starts nothing: the type weighs 0 for that
    character for four think periods. The HUD's Goal line shows the AI_TASK record's name while the goal holds the character.
  - **Ends:** `End(world, slot, id, type, ModGoalEnd why)`, serial, in the tick the end is seen or the next: `Displaced` (another goal won, or
    a job for a player character; the engine stops a task still the act-out's: a walk stands, another ends through its handler), `Order` (an
    order's task starts, or it changes faction), `Replanned` (a knock-out, death or pick-up, an order on the body, a new mind, the goal's plan
    over with its hours), `Taken` (it
    flees, fights or is caged, or another system's task replaced the act-out), `Gone` (it left the world). Drop what the act-out holds and leave
    the task alone. A fight takes the character over this way: `Displaced` when Self preservation or a combat goal wins on the chooser, `Taken`
    when another system starts the fight first. `End` comes once per `Start` that answered true, but not after `ModGoals.Done(world, slot, type)`,
    which ends the goal by the mod's rule (a task still the act-out's stops as for `Displaced`), nor for a dropped goal (Faults below).
    `ModGoals.Holds(world, slot, type)` tells the mod's serial step whether its act-out goes on (false once the goal is dropped); both throw in a
    parallel phase. Nothing is saved: after a load no goal holds anyone and no `End` comes for the goals held at the save, so a mod that saved
    its act-outs finds them by `Holds` being false; the chooser starts the goal again.
  - **Faults:** a `Desire` that throws or answers a number that is not finite at tick T is still asked by T's other threads and not from T + 1,
    and no start of it is asked from T; a `Start` or `End` that throws stops it at once. The first fault is logged with the mod id and the goal
    type, gives the player a notice and enters the state hash. A faulted goal, and the goals of a system the world stopped (Tick systems above),
    are dropped: the mod is called no more, and the characters they hold are let go with no `End` and no handler (a walk on the act-out stands)
    and choose again.
  - **Jobs:** the chooser's job passes ask the contributor of a job type for each listed job of that type, for any character with a job list (Mod
    jobs above). A contributor whose call throws is dropped for that world: the first fault is logged with the mod id, the player gets a notice,
    and the fault enters the state hash.
  - **Desire filters:** in `Register`, `chooser.DesireFilter(IGoalDesireFilter)` filters the chooser's desires of every task type, the
    engine's and the mods', for every character (a system added later may register one too). `Desire(World, in GoalDesireAsk)` gets the tick,
    the character, the type, a `GoalDesireKind` and the desire, and answers the desire to use: `Goal` (a list goal as the goal scorer weighs
    it, before the list's weight, crowding and continuity; a bed goal whose bed search found no bed is not asked), `PlanStep` (a row on the
    way of a plan, 0 or below a dead end, such as getting out of bed once healed), `Order` (the order queue's front, which leaves at 0 or below,
    and the second list), `Job` (a job of the list that could run, `Job` its index, asked as 1: 0 or below passes over it). Filters run in
    registration order, each seeing the desire the earlier ones left, on any thread (the think is parallel): quick, thread-safe, a pure read. A
    throw or a desire that is not finite keeps the desire; the filter is still asked for the rest of that tick and not from the next, and the
    fault enters the state hash. Raising a goal's desire from 0 can pick a goal that then finds nothing to act on. Engine API, not in the
    original. **Observed** by a scratch run (not in the repo): an unchanged answer keeps the town's hash on 1 and 4 threads; 0 for the bed
    goals leaves every bed empty at night.
- **Ordering characters** (`Meitou.Simulation`, [order queue](simulation.md#order-queue); **Observed**, by reading `World.GiveOrder` and
  `PlayerOrders.Give`): `world.GiveOrder(actor, row, target, site, point, queued)` gives a player character or an NPC an order through the one
  queue the player's orders use, with the parts the menu's verbs give. The row is one a system owns as an order (the rows `World.CanOrder`
  answers for; a mod's own row is not one). `TaskType.MoveCusOrdered` to `point` is the player's move command for that one character (a
  prisoner's waits queued in its cage, a sleeper gets up, a follow order ends, an animal a building keeps out goes past its first door); any
  other row is given as a saved order of that row is loaded, with its owner's detail (an attack is the player's pick, a lock order a walk to its
  row's work) and readying (`IOrderOwner.LoadDetail`, `Restored`). Without `queued` it replaces the queue. Serial phases only (it throws in a
  parallel one). It answers the check of the order's start when it is the only order queued and the character takes orders now: `Run`,
  `Blocked` (with the text the player's order would show) or `Gone` (it left the queue); else `Run` (queued). `Gone` too for an actor not in the
  world or a row no system owns (nothing queued). An NPC's front starts only while its chooser runs (it has a squad and is not carried, out
  cold or dead), else it waits queued; its start ends a goal holding the character (`ModGoalEnd.Order`), and the chooser takes the character
  back once the queue is empty. `CanOrder` asks the same without giving.
- **Moving from the old hooks** (the hooks are gone; a mod built on them gets the notice under [Versioning](#versioning)):
  - `IDecisionOwner` with `DecisionSystem.Register(source)`: a chooser goal (`ChooserRegistry.Goal`) or an order (`World.GiveOrder`);
    `DecisionSystem.RegisterJob(type, source)`: `ChooserRegistry.Jobs(type, source)`.
  - `INpcGoalOwner.RegisterGoals(NpcGoals)` with `Register(type, goal)`: `IChooserOwner.Register` with `ChooserRegistry.Goal(type, goal)`.
  - `INpcGoal.Desire(world, in NpcGoalAsk)`: `IModGoal.Desire(in ModGoalAsk)`, still asked in parallel, reading through the ask.
  - `INpcGoal.Start` setting `Engaged`, walking and answering true: `IModGoal.Start` walking and answering true; the goal holds the character.
  - Done by stopping the task, clearing `Engaged` and setting `Replan`: `ModGoals.Done(world, slot, type)`.
  - Releasing for a fight: nothing to do; `End(Displaced)` or `End(Taken)` is called (Ends above).
  - `CharacterCold.Engaged` to keep the AI off an NPC while the mod walks it: an order (`world.GiveOrder(id, TaskType.MoveCusOrdered, point: p)`)
    or a chooser goal whose `Start` walks it; the order or the goal holds the character.
  - A serial step taking a package's members for a goal of the mod's type (the duty route): a chooser goal of that type in the package's role
    list; the chooser decides when it wins.
  - `HoldPlatoon` to put a loaded platoon's members on the engine's routine and walk them: the hold keeps only its off-screen half (no trips,
    `SendPlatoon`); loaded members run their package, and the mod orders them (`World.GiveOrder`), adds a chooser goal to the package, or
    changes the package (`ChangeAi`, `StartContract`).
  - Player characters weigh a mod's goal type in their own lists and their autonomous squad's package.
  - `AiMind.ModGoalAsk`, `ModGoalStarted`, `ModGoalRow`, `ModGoalTick`, `ModGoalBackoff` and `AiBehaviour.Mod`: `ModGoals.Holds(world, slot,
    type)`.
  - `IDecisionSource`, `DecisionStep`, `DecisionSystem.Sources` and `JobSourceOf` are internal: a job type's contributor is the mod's own
    `IJobSource` (`ChooserRegistry.Jobs`).
  - `AiMind.Engine` and `EngineGoals`: gone with the engine minds (`HoldPlatoon` above).
  - Gone with nothing in their place: `PopulationSystem.PendingTalk` and `AnnouncementPatience`, `FirstAidSettings.AutoReach`,
    `Platoon.OrderCooldown`, `AiMind.CaptiveOffers`, `LockSystem.ReleaseTimeout`; the host's `ModHost.TryTakeFactoryNotice` is
    `CodeModFaults.TryTakeNotice`.
- **Workplace slots** (`Meitou.Simulation.Ai`; the system from `world.Systems` or `Find<AiSystem>()`): `AiSystem.WorkSlots(building)` answers a
  workplace's operator slots as `WorkplaceSlots(Held, Max)` (`Free`): `building` is its `TownBuilding.Key` (a town building's `x.y:instance`,
  an outpost machine's site id), `Held` the AI workers holding it and the player's operators on a job at an outpost machine, `Max` its
  functionality's `max operators`, as of the last package cadence pass; null for a building that is no workplace of a town or outpost the AI
  looked at then. Read only, in the serial phases. A mod's own worker holds no slot: a mod job checks `Free` before it hires. Engine API.
 
- **Seats** (`Meitou.Simulation.Production`; the system from `SystemBuildContext.Find<ProductionSystem>()` or `world.Systems`):
  `SitOn(world, slot, seat, row, source)` puts a character, the player's or an NPC, on a chair or throne through the player's Use order's
  act-out (the original's sitting is its operate task, [chairs](game/ai-tasks.md#town-goals)): it starts `CharacterTask.Operate` through
  `TaskStart.Start` with `row` (the mod's own, 1000 up; a lower one throws) and `source` (`TaskSource.Goal` for a goal's act-out), so
  the character walks there, sits on the seat's origin facing its way (the Bench facing switch applies), plays its sitting clip, gets hungry at
  the seat's `hunger rate` and trains its `stat used`. `seat` is a town seat's id (`world.Fixtures.SeatSpots`: the chairs and thrones of the
  loaded towns' buildings, with record, position and owning faction) or a complete building's in `world.Buildings.All` whose functionality is a
  chair or throne, of any faction. `CanSit(world, slot, seat)` gives the same answer without starting anything; it changes nothing in the world
  and logs nothing, so a mod picks a free seat with it. `SeatAnswer`: `Seated`; `NotASeat` (also a destroyed seat, a null or empty id);
  `Taken` (someone rests on it, the AI's sitters included but not a member of an AI-run squad living in an outpost, who gives the seat up, or another
  character is on an operate task for it, walking there included: one sitter per seat, whatever its `max operators`); `Unable` (dead, down, carried,
  carrying someone, fleeing, in a bed or cage, or not of the human repertoire: animals, crawlers, one staying low); `ShutOut` (at the call, its
  building is shut to the character as to a player character's walk:
  no lock is picked or cut for it, though one whose faction bashes doors may bash its way in; or the character stands in a shut building with no
  way out; a door locked during the walk is the walk's business). Any answer but `Seated` changes nothing and logs a line naming the row, once
  per row, seat and answer; a character already on that seat on that row is left as it is. Both run in the serial phases only and throw in a
  parallel one: a mod's serial step or `Inputs`, a chooser goal's `Start`, `IWorldReady`; never a goal's `Desire`, so
  work free seats out in the mod's serial step and keep them in its state. A character in a fight leaves it, as for any start (it may be drawn
  back in at once).
  Who may be seated: a player character, an NPC the AI holds now (`AiSystem.Holds(table.Next[slot], cold)`), or one
  its chooser goal's `Start` seats (the goal then holds the sitter). A character another system holds (a conversation, a trade, an arrest, a
  duty, another mod's goal) is taken from it, so do not seat one. Outside a goal the AI holds no
  character on the operate task, so a seated NPC stays seated, only Self preservation takes it over, and the AI takes it back
  once the seat ends; a goal's sitter is the goal's until another goal wins (`End(Displaced)`, the seat stopped). A player character seated
  outside a goal is decided again at its next choice (an order, a job, a goal that wins); one a goal seated stays while the goal holds it.
  How it ends: the walk reserves nothing from the AI's own sitters, and they always win: whenever one rests on the seat's origin (one that got
  there first, or one arriving later on a sitting-around or throne claim it already held) the task ends `Done` and the mod's character stands
  where it sat. Once it sits, a relaxing or staying-home member passes over that chair of its bar or home; another claim may still send a
  townsperson there, with that result. The task also ends `Done` when the seat
  or its building is gone (destroyed, dismantled, its town unloaded); `Replaced` by any start (an order, a fight, a job, another mod's seat; do
  not seat again a character another start took); `Stopped` by `TaskStart.End(world, slot, TaskEnd.Stopped)`, which is how the mod stands it
  up. Seating a character already on that row's operate task again (another seat) starts it anew with no `End`: the handler sees a second
  `Start`. A knock-out, a death or a lift stands it with no handler, and an unreachable seat is tried again and again as the Use order's, so the mod
  also checks its sitters in its own serial step (alive, not down or carried, `Running` still its row, running `Operate`) and gives up by its
  own clock. With a handler of its row (`Register(row, handler)`) that handler takes the operate act-out's place: call
  `tasks.Of(CharacterTask.Operate)`'s `Start` and `End` from it, or the character never walks or sits (its task is left as the one the start
  ended). Tasks are not saved: after a load the character stands where it sat and the townspeople may take the chair. At `IWorldReady` a
  town's seats and an NPC are often not in the world yet (a town's seats come as it loads, NPCs a few hundred ticks later), so a mod that
  keeps its sitters (by `LastingId`) seats them again from its serial step once `LastingIds.Find` finds the character and `CanSit` no longer
  answers `NotASeat` (one still at its seat sits at once). Without a call nothing changes; a seat that had no machine gets the one the Use order
  makes (`World.Machines`, hashed; a town seat's is not saved, a built chair's is saved with its building, as the Use order's). Engine API, not
  in the original.

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
right after the load and after every tick, and reports the first difference of each pair with the parts of the state that differ there
(characters, squads, platoons, the player, ..., the hashed systems, the hooks' faults: `World.StatePartNames`) and the `IStateHashed`
systems, a mod's by id and name. One-thread runs differing points at unseeded randomness or the clock; thread counts differing, at a
threading mistake; runs differing right after the load, at something a run leaves behind that the next load does not reset (a static
field, or game data the worlds share). On a difference after a tick it then takes a close look: it runs both again to that tick, hashes
each part after each serial step of it (inputs, schedule, think move and act, commit, slow world: `World.AfterPhase`), and names the
first step that differs, the characters that differ (slot, name, record, faction, squad; `CharacterTable.SlotHash`) and the fields of the
first that differ, both runs' values side by side (read by reflection from its hot and cold state and what that owns, floats to the
last bit). It says when a run comes out differently the second time, the new runs differ at an earlier tick, or the difference does not
come back. Last, it runs the game without mods: if that differs too, the fault is the game's.

- Combat hashes its tick's state only from its slow world on (it swaps its buffers there), so a combat difference shows at that step.
- The field listing holds fields the hash does not read too: across thread counts some can differ without being the cause.

- Walkability is the open terrain, not the navmesh (whose zones load on threads), paths are answered in the tick and the population works
  in it, so runs repeat; the world differs from play in where characters walk.
- The parallel phases cut slots into partitions of at least 32, so a world under 64 characters runs them as one partition and cannot show
  a threading mistake; the command warns.
- It sees only hashed state: a mod's unhashed state, or a patch that changes nothing hashed, passes.
- **Observed** (2026-11-04, win-x64, saves `autosave0`, `Gigachad`, `260`): a clean scratch mod passes; a mod rolling `Random.Shared` in
  `SlowWorld` differs from tick 1 in both comparisons and is named; a mod whose `Act` writes one field from every partition differs only
  across thread counts once the population passes 64 characters (tick 152) and is named; the game alone agrees.
- **Observed** (2026-11-20, win-x64, two saves of 22 and 75 characters, a scratch mod planting each fault from tick 100): a `Random.Shared`
  nudge to one character's field is named by step (slow world), character and field with both runs' values; a roll into the mod's hashed
  state by part and system; an unguarded write from every partition only across thread counts, the close look saying when the new runs
  differ at another tick or agree; a first run that changed faction relations makes the later runs differ right after loading, in
  `AggroSystem`. A clean 600-tick check takes as long as before.

## Versioning

- A mod compiled against a member or type the game removed or made internal fails where the runtime binds it (a `MissingMemberException`,
  `MemberAccessException`, `TypeLoadException` or `ReflectionTypeLoadException`, unwrapped from a reflection call, a static constructor or a
  parallel loop's `AggregateException`): at load (its types, its `IMod`, `Initialize`, its data phase), in a system factory, or at the first
  call that uses it: a system phase, a task handler, a registration, `IWorldReady`, a goal, a job contributor, an observer or filter, a combat
  move, an item use, a dialogue code, a wear rule or look hook, a hit feed, a faction list arranger, a key action, a debug row, a setting, its
  save data, an interface extension's registration. The mod gets one notice for the session, `Mod <id> was built for another version of the
  game and needs an update; it is stopped and the rest of the game goes on.` (`CodeModFaults.Tell`), in place of the fault's own notice; what
  faulted is refused, stopped, dropped or skipped by its own rule, and the game runs on (**Observed**, by reading `CodeModFaults.Tell` and its
  callers).
- The manifest's `apiVersion` must lie in `ModApi.MinimumVersion` (1) to `ModApi.Version` (3); otherwise the mod is refused at load with a
  message. 2 added `version`, `dependencies`, `loadAfter`, `incompatible`, `gameVersion`, `ModContext.Dependency<T>` and
  `IModDependents`. 3 added the chooser surface ("Chooser goals and jobs", `World.GiveOrder`) and removed `CharacterCold.Engaged`,
  `AiMind.Engine` / `EngineGoals` and `HoldPlatoon`'s in-world hold, so a mod on that surface declares 3 and an older game refuses it at load.
  An API 1 or 2 mod loads unchanged; one that uses a removed member gets the notice above. `gameVersion` ties a mod to the engine releases it was built against. The
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
