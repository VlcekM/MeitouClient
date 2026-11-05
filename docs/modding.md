# Code mods (C#)

**Status: implemented, code-mod API version 1 (2026-11-04).** Meitou's own in-process loader, Lib.Harmony shipped by the game as the
sanctioned patching library, and a registration surface kept to the cases where a patch would silently break an invariant. No BepInEx.
The RE_Kenshi decision ([rekenshi](formats/rekenshi.md)) stands and its DLLs stay ignored. Code: `src/Meitou.Modding` (contract and
loader), `GameHost.Mods.cs` (host wiring), `SimulationExtensions` (engine hooks).

## Why this shape

- **Not BepInEx.** Its chainloader discovers plugins from its own folder, not `mods.cfg`, so code mods would not enable, disable or order
  with their data mod. It injects via Doorstop (proxy DLL on Windows, `LD_PRELOAD`/`DYLD_INSERT_LIBRARIES` on Unix), which fights the
  launcher and the signed macOS build when the host is ours. **Observed**
  ([BepInEx.Utility.IL2CPP r2](https://github.com/BepInEx/BepInEx.Utility.IL2CPP/releases/tag/r2), 2026-06): BepInEx 6 had no stable
  release; downstream projects built on `be.*` builds.
- **Harmony is not an alternative to an API.** It works on the game's JIT publish whether blessed or not, so the decision is only what the
  engine promises beyond raw patching. **Observed**: Lib.Harmony 2.4.2 added .NET 10 support
  ([releases](https://github.com/pardeike/Harmony/releases)).
- **No accessor mirrors.** Mods reference `Meitou.*` assemblies directly, as a patch needs the types anyway. Compatibility is per game
  version (RimWorld's model).

## Writing a mod

A class library for `net10.0` referencing the release's `Meitou.Modding.dll`, `Meitou.Simulation.dll`, `Meitou.Core.dll`,
`Meitou.Data.dll` (and `Meitou.Ui.dll` for interface work) and `0Harmony.dll`, all with `Private="false"`: the game supplies them.

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
  commas are allowed.
- `ModInfo.HasCode` reports it beside `HasNativePlugin`; the launcher marks such mods.
- Load set: the enabled mods of `mods.cfg` in order, filtered to those with a manifest. A disabled mod's DLL is never opened. Changes take
  effect at the next launch, like data mods.

## Trust

A workshop DLL is arbitrary code running as the user, as with RE_Kenshi. No sandboxing: .NET has none. The game loads a code mod's code
only when its mod name is in `trustedCodeMods` of `meitou.user.json` (`UserConfig.TrustedCodeMods`); otherwise its data loads, its
code does not, and the game shows a notice. The launcher's Play asks for each enabled code mod not yet allowed ([launcher](launcher.md)).
The game itself cannot ask: mods initialize on the boot worker before the interface exists.

## Loading

- One non-collectible `AssemblyLoadContext` per mod (`ModLoadContext`), loaded once at startup; no unload or hot reload.
- Resolution by simple name: an assembly on the game's trusted platform list (`Meitou.*`, the framework, Bepu, `0Harmony`) comes from
  the game even when the mod folder has a copy; anything else comes from the mod folder, so two mods may ship different versions of a
  library. Listing a game assembly in `assemblies` refuses the mod. Native libraries resolve from the mod folder first.
- **One Harmony, loaded by the game.** **Observed** ([pardeike/Harmony #741](https://github.com/pardeike/Harmony/issues/741), open since
  2025-10): outside Unity, Harmony loaded in several load contexts duplicates `HarmonySharedState`, and two mods can no longer patch the
  same method. The game references Lib.Harmony 2.4.3 (`0Harmony.dll` with MonoMod merged in) and the resolution rule hands every mod
  that copy.
- Refusals, each a log line and a notice, the game going on: not allowed, unreadable manifest, other `apiVersion`, an id another mod
  took, a missing DLL, an entry type that is absent, not an `IMod` or without a public parameterless constructor, a throwing
  `Initialize`.
- Constraint: the game stays a JIT publish, never `PublishTrimmed`, `PublishAot` or single-file. **Verified**
  (`tools/scripts/package-release.ps1`): the game publishes self-contained with `Meitou.*.dll` beside the executable, and `meitou-tools`
  with it. **Observed** (2026-11-04, win-x64 publish of the three programs into one folder): `0Harmony.dll` and `Meitou.Modding.dll` are
  there, the game's 228 files are byte-identical to a game-only publish, and the published `meitou-tools.exe mod-check` runs.

## Lifecycle

- `IMod.Initialize(ModContext)` runs once per session on the boot worker (`BootSimulation`), after the data load and mod merge and before
  the first world's systems, in `mods.cfg` order. Registering is only allowed inside it. A throw drops the mod's registrations and
  removes its Harmony patches (`UnpatchAll` of its id).
- Worlds are built again for every new game and load, so systems are registered as factories and made per world.
- `ModContext`: `Id`, `Name`, `Folder`, `Manifest`, the merged `Data` (`GameDatabase`), `Population` (`PopulationData`), `Log`/`LogError`,
  `RngPurpose`, `Harmony`, `Tick` and `Enqueue(SimCommand)` for the running world (dropped and logged before the first world).
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
  and logs. `IStateHashed` puts a system's state into `World.StateHash`. A mod system implementing a source interface `World` looks
  up by first match (`IAnimationSource`, `IProjectileSource`...) and placed before the standard one would replace it.
- **Resolved combat:** a registered system implementing `ICombatHitObserver` receives `HitResolved(World, in CombatHit)` once per
  landed melee/projectile contact, after its ordinary medical, XP, reaction, crime and log work. A fully absorbed contact counts;
  blocks, dodges, misses, a contact that selects no medical part and a captive's execution blows (`CombatSystem.Execute`) do not. The
  value payload has delivered damage, armour absorption, selected medical-part index, original attacker/victim generations,
  attack/projectile sequence, approach and pre-hit life/incapacitation flags. Positions are the victim's root, not a measured surface
  collision; pre-damage part health is not supplied. Callbacks run serially in system order, may read the world and change their own
  hashed state, and queue later commands for gameplay changes; they must not mutate world state or call combat recursively. A
  throwing callback is logged and disabled for that world; others continue. Melee callbacks run in Commit, projectile callbacks in
  `CombatSystem.Schedule` (`FlyProjectiles`); both precede every `SlowWorld` of the tick, so a system draining its own hit queue
  there needs no anchor. Faulted registrations enter the state hash. Without observers, no payload or event queue is made and no
  additional rolls occur.
- **Dialogue codes:** `AddDialogueCondition(code, handler)` and `AddDialogueEffect(code, handler)`, named from FCS data in a
  DIALOG_ACTION's `condition name` / `action name`. A condition gets `DialogueCheck` (world, speaker, the subject `who` picked, the
  condition) with `Matches(bool)` and `Compare(double)` that apply the vanilla value and `compare by` rules; it only reads (it also runs
  for interface previews). An effect gets `DialogueAct` (world, speaker, target, effect) in a serial step. An unregistered code
  computes "no" / does nothing, as an unknown vanilla code.
- **Record types:** `AddRecordType(type, name)` claims a number (not a vanilla `itemType`); `Records(type)` lists the merged records.
  Survival of such records through the FCS merge and saves is **Unknown** until tested.
- **Persistence:** a mod system implementing `IModSaveData` (`Write`/`Read(Stream)`) is saved under its name in the mod's sidecar file
  ([save](formats/save.md#mod-data)), captured between ticks with the save and restored right after `SaveLoader.Load`, before the first
  tick. `Read` runs only when the save has data for the system. Vanilla save files stay byte-exact.
- **Interface:** `AddUi(IUiExtension)`: namespaced styles, controls, actions, overrides and slot contributions (`UiSlots`), registered
  with each new interface (`ConfigurePlayerInterface`, Vulkan and Metal).
- **Debug rows:** `AddDebugCommand(label, run, hasField)` adds a row to the F10 menu after the vanilla ones. `run` gets
  `DebugCommandContext` (field, selection, camera target, tick, `Enqueue`) on the host thread while a tick may be in flight, so it must
  not touch the world; it returns the status line, and a throw shows as the status.
- **Settings:** `AddSettings(object)`, once per mod: the object's public get/set properties marked `[Setting(label, Hint, Min, Max, Step, Options)]`
  (`ModSettings.cs`) become one group, in declaration order, on the Settings screen's Mod settings page ([native UI](native-ui.md#settings)): `bool`
  an Off | On switch, `int`/`float`/`double` a slider (Min and Max required; Step 0 = 1 for `int`, a hundredth of the range otherwise), an enum or a
  `string` with Options a list. Another type, a number without a range, a list whose value is not one of its options, a struct (the page would
  change a copy) or no marked property refuses the mod. The player's values are kept in `meitou.user.json` (`modSettings`: mod id, key, invariant
  text; a default is not kept) and set on the object before `AddSettings` returns, a number clamped to its range, a value that is not one of the
  setting's (or not finite) ignored with a log line; the values the object had are the defaults Reset and Restore defaults put back as they were,
  not snapped to the step (Restore defaults also drops every kept mod value). The page sets a changed property at once on the interface's thread,
  outside the tick, and raises the returned group's `Changed`; a throw from the setter or a handler is logged and shown as the page's notice. A
  slider's arrow keys move one Step. A mod copies what its simulation needs in a serial step (its system's `Inputs`). **Verified** by
  `ModdingTests`; the page **Observed** drawn (2026-11-04, Windows, Vulkan, `--settings-showcase` changed locally to open it with a scratch group);
  using it in play **Unknown**.
- **Commands:** a mod's own `SimCommand` types, queued with `Enqueue` and handled in its system's `Inputs`.

## Harmony

- With code mods enabled the JIT warm-up (`JitWarmup`) starts only after `Initialize` returns. Tiered compilation is off, so a caller
  compiled before a patch keeps any inlined copy of its target.
- **Observed** ([Harmony docs](https://harmony.pardeike.net/articles/patching-edgecases.html)): an inlined method cannot be patched; the
  patch silently does nothing. Patch methods large enough not to be inlined, or their callers. `Rng` helpers are `AggressiveInlining`.
- **Observed** (2026-11-04, scratch run): Harmony finds a patch method again by module identity, so the same DLL file loaded by two mods
  makes their patches resolve to one copy. A helper library shared by several mods must not hold patch methods.

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
difference it runs the game without mods: if that differs too, the fault is the game's. Running the command is consent to run the mods'
code, so `trustedCodeMods` is not consulted.

- Walkability is the open terrain, not the navmesh (whose zones load on threads), paths are answered in the tick and the population works
  in it, so runs repeat; the world differs from play in where characters walk.
- The parallel phases cut slots into partitions of at least 32, so a world under 64 characters runs them as one partition and cannot show
  a threading mistake; the command warns.
- It sees only hashed state: a mod's unhashed state, or a patch that changes nothing hashed, passes.
- **Observed** (2026-11-04, win-x64, saves `autosave0`, `Gigachad`, `260`): a clean scratch mod passes; a mod rolling `Random.Shared` in
  `SlowWorld` differs from tick 1 in both comparisons and is named; a mod whose `Act` writes one field from every partition differs only
  across thread counts once the population passes 64 characters (tick 152) and is named; the game alone agrees.

## Versioning

- `ModApi.Version` (1) must equal the manifest's `apiVersion`; a mismatch refuses the mod at load with a message.
- Harmony patches non-public members; compile-time references need public types or a publicizer. Open: which simulation types to make
  public, or ship publicized reference assemblies; ship reference assemblies per release so modders build without a game install.

## Optional combat target policies

A system registered through `ModContext.AddSystem` may implement `ICombatTargetRules`.
The first policy claiming an attacker/target pair wins; an unclaimed query preserves ordinary
combat. Queries must be deterministic, read-only and thread-safe because pursuit/combat read
in serial and parallel phases. The policy owns and hashes its persistent state.
A claim may allow a living incapacitated target, never a corpse, caged or carried target.
`Part` is an exact medical index: invalid or missing parts reject contact without choosing
another part. A queued contact resolves the actual victim's policy, even if its attacker has
retargeted. `AimPoint` and `GroundTarget` are optional aiming/technique inputs.
`WeaponInstance.SourceItem` identifies its originating equipped inventory item when available;
weapon statistics and ordinary assembly defaults are unchanged.

## Runtime character presentation

`CharacterDrawList.Context` exposes the live renderer context during render-thread fills, independent
of particle/weather systems. It is null on a headless or unattached list; do not retain or use it on
simulation/background threads.
`Disposing` notifies resource owners once on the render thread before the context is released,
including worlds without particle effects. Cleanup handlers are isolated and subscriptions cleared.

`CharacterDrawList.Preparations` queues asset and shader preparation without visible actors, poses or
picking state. Refill it each frame; `Clear()` empties it with `Items`. This is best-effort background
work: existing renderer `ContentPending`/`IsPrepared` retain their loading-screen completion semantics.
`CharacterDrawList.IsPrepared(appearance, geometry)` instead checks one exact uploaded appearance/geometry
identity and its compiled custom programs (when supplied); null geometry checks the native appearance.
This render-thread query neither starts nor waits for work; pending, failed, retired and unattached
requests return false. Use it to keep a previous presentation until every member of a replacement
family is ready. It does not promise full texture residency. Disposal clears the renderer callback.
Actual actors, distant loads and missing live custom programs use the build budget first; preparation
uses the remainder, including shader creation by identity. If a live program cannot fit, speculative
work waits; actual draws retain their lazy compilation fallback. A single asset/program preparation
completes atomically and may exceed the time budget; another waits. Prepared generated resources remain
cached until retired, and current preparation requests protect geometry/programs from retirement exactly
like draw requests. Ordinary empty lists add no work.

`CharacterInstance.WorldBounds` optionally supplies a validated `CharacterWorldBounds` sphere for
posed/deformed geometry. Centre and radius are already world-space: the host adds no bind height or
scale. Loading/reach, view/fog culling, picking and actor projected-size cutoffs use it. Mesh LOD and
shadow size cutoffs retain each part's own radius; posed shared parts use the supplied centre, while
bone-attached parts use their actual attachment transform. Null or the invalid default struct preserves
ordinary character bounds; explicit nonfinite/nonpositive values are rejected by the bounds constructor.

`CharacterAppearance.Snapshot()` copies its mutable parts, layers, lights, attachment-point,
pose-weight and notes collections. Content records remain shared; treat loaded content as read-only.
Use the original appearance as a cache identity when worker code reads a detached snapshot.

`CharacterInstance.Geometry` accepts immutable `RuntimeCharacterGeometry` variants; call `Retire()`
once a variant is no longer published. Generated GPU meshes and shader programs retire after frames
using them finish. Reuse `RuntimeCharacterShaderSet` identities across variants: it supplies complete
colour/depth/motion GLSL stages, compiled on the render thread. `RuntimeCharacterShaderSources`
exposes the current host prelude, atmosphere/depth helpers, scattering constants and default stages.
These programs use the host's character instance/descriptor ABI; colour receives the host GI wrapper.

`CharacterInstance.RuntimeShaderData` copies an opaque matrix prefix and additional instance flags.
The prefix sits immediately before that actor's `BoneBase` in current and previous palettes, independent
of other actors; new/changed layouts and motion snaps use current values as previous. A custom program
is active only with supplied shader data (an empty prefix is valid). Native actors retain their layout.
Instance bits 0 and 12–29 are host-owned; `BodyFlags`/part `DrawFlags` cannot overwrite them. Part
`MaterialFlags`/`BodyMaterialFlags` add raw material bits without clearing ordinary material flags.
Nullable part `DoubleSided` and geometry `BodyDoubleSided` override material culling; null inherits it.
Part `CastsShadows` defaults to true; false omits only its shadow depth passes, retaining colour, motion and portraits.
`PreserveRawVertexChannels` (or `PreserveRawBodyVertexChannels`) opts a supplied mesh into full-float
tangent/colour channels without native curvature/visibility rewriting; the producer owns that metadata.
Portraits use the standard shader without producer flags; set a generated part's `ShowInPortrait=false`
when it needs the custom program. Existing parts default to visible. `MinimumScreenPixels` (finite,
nonnegative; default 0) hides a generated part below the actor's projected diameter in world, shadow,
motion and picking passes; actual portraits use `ShowInPortrait` independently. Hover masks and
editor stages retain the world shader and visibility rules.

An ordinary `DebrisPiece` may set `AlbedoTint` to a linear RGB multiplier. It applies to the
texture and the untextured fallback. Leave it null to use the material as authored.

## Native ragdoll handoffs

`BonePose.AbsolutePositions` lets every set bone supply its model-space position. Optional
`Components` is an immutable array indexed by bone handle, of the same length as `Set`; equal
ids identify a connected piece within that pose. Interpolation keeps the destination topology.

`RagdollSystem.TryCapture(world, id, out handoff)` copies native body/bone world transforms,
velocities, a model-space pose and its placement without changing ownership. Pending and
already-applied launches are distinguished; do not apply an already-consumed velocity twice.
A body's optional `AssignedLaunch` records its initial clamped linear/angular velocity only when
this tick's rebuild actually launched that actor. It is absent for previously dynamic actors whose
motion was kept. `HasMotion` is false for virtual/null-handle or kinematic pinned bodies; keep
their pose data, but do not inherit their placeholder velocity. Manually constructed body snapshots
default to `HasMotion=true` for compatibility. A replacement launch can add replacement-minus-assigned velocity without scaling
unrelated captured motion; subsequent gravity/contact effects remain in the captured velocity.
`TryClaim(world, id, owner, out handoff)` atomically captures/removes native actors and suppresses
native re-creation. Owner identity is by reference: another owner is refused, while a repeated
claim by the same owner returns its original handoff. `ReleaseClaim(id, owner)` resumes native
rules on the next tick; `ReturnClaim(world, id, owner, pose, placement)` copies a complete external
pose into the native get-up blend before resuming those rules.

Call these APIs in serial phases on the owning World's simulation thread, never from rendering
or parallel jobs. Handoffs contain copied data, not live physics buffers; treat snapshots as
immutable. When the handoff API is unused, the ordinary ragdoll hash is unchanged; active claims contribute
their character identities. Mods remain responsible for their own physics state and persistence.

## Platforms

- Managed DLLs run unchanged on the Windows, Linux and macOS builds. Harmony's detours come from MonoMod.Core, whose support for .NET Core
  on Linux and macOS is **Observed** only as a fix note in [MonoMod's releases](https://github.com/MonoMod/MonoMod/releases) and for arm64
  only in a third-party write-up, so patches off `win-x64` are **Unknown** until tested on each build.
- **Observed** (Harmony 3.0 RC notes): native async methods cannot be patched. Irrelevant on .NET 10; a constraint under .NET 11
  runtime-async.

## Evidence

**Observed** (2026-11-04, win-x64, a scratch mod and harness against the release build, outside the repo, run as the game runs: tiered
compilation and PGO off, server GC): only allowed mods with a valid
manifest load and each refusal gives its notice; the process holds one `0Harmony` and one `Meitou.Modding` though the mod folder had its
own `0Harmony.dll`; a prefix applies, and a throwing `Initialize` loses its patch and registrations; a mod system's state survives
capture and restore; sidecars survive `SaveGame.Write`/`Load` and `Clone`; spliced systems land at their anchors and the standard list is
unchanged. The dialogue hooks are built but not exercised. **Verified** by `ModdingTests`: game assemblies resolve from the game, anchors,
sidecar round trip, and a hashed mod system agreeing at 1 and 4 threads.
