# Code mods (C#)

**Status: implemented, code-mod API version 2 (1 still runs; 2 adds [dependencies](modding-dependencies.md)).** Meitou's own in-process loader,
Lib.Harmony shipped by the game as the sanctioned patching library, and a registration surface kept to the cases where a patch would silently
break an invariant. No BepInEx.
RE_Kenshi plugin DLLs are never loaded: wishlist plugins get built in or ported to this API ([rekenshi](formats/rekenshi.md)). Code:
`src/Meitou.Game/Mods` (contract and loader), `GameMods` (host wiring), `SimulationExtensions` (engine hooks).

## Why this shape

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

## Trust

A workshop DLL is arbitrary code running as the user, as with RE_Kenshi. No sandboxing: .NET has none. Installing and enabling a code mod
is the consent, as in RimWorld: the game loads every enabled code mod's code, with no allow list or question. An older game's
`trustedCodeMods` in `meitou.user.json` is no longer read.

## Loading

- One non-collectible `AssemblyLoadContext` per mod (`ModLoadContext`), loaded once at startup; no unload or hot reload.
- Resolution by simple name: an assembly on the game's trusted platform list (`Meitou.*`, the framework, Bepu, `0Harmony`) comes from
  the game even when the mod folder has a copy; anything else comes from the mod folder, so two mods may ship different versions of a
  library. Listing a game assembly in `assemblies` refuses the mod. Native libraries resolve from the mod folder first.
- Between the two: an assembly a declared dependency exports comes from that dependency's context, so a framework loads once
  ([dependencies](modding-dependencies.md#assembly-resolution)).
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
  and logs. `IStateHashed` puts a system's state into `World.StateHash`. A mod system implementing a source interface `World` looks
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
- **Dialogue codes:** `AddDialogueCondition(code, handler)` and `AddDialogueEffect(code, handler)`, named from FCS data in a
  DIALOG_ACTION's `condition name` / `action name`. A condition gets `DialogueCheck` (world, speaker, the subject `who` picked, the
  condition) with `Matches(bool)` and `Compare(double)` that apply the vanilla value and `compare by` rules; it only reads (it also runs
  for interface previews). An effect gets `DialogueAct` (world, speaker, target, effect) in a serial step. An unregistered code
  computes "no" / does nothing, as an unknown vanilla code.
- **Equipment slots:** `AddEquipmentSlot(new ModSlot(section, fcsSlot, width, height, x, y))` (`ModSlots`, `src/Meitou.Simulation/Items`) adds
  an inventory section that takes the ARMOUR and ITEM records whose FCS `slot` is `fcsSlot`, so the items stay a data mod. `fcsSlot` is 4, 10
  (ATTACH_GLOVES), 11 (ATTACH_NECK) or 15 and up; the others map to vanilla sections. A vanilla section name, a name or `slot` another slot
  took, or a size outside 1 to 20 cells refuses the mod. The slot holds one item, is worn gear everywhere a vanilla clothing slot is (armour
  stats, look, trade, looting, locks), and shows in the character and loot windows as a copy of the belt slot at (`x`, `y`) layout pixels
  (UI scale 1) from the window's top-left corner, clipped to the window: the mod picks a free spot; the window does not grow. Animals get none.
  Saves keep the section name ([save](formats/save.md#compatibility-exceptions)). Races' `no hats`-style closing never applies.
- **Bone anchors:** `CharacterCold.DrawAnchor = new DrawAnchor(carrier, bone, offset, rotation)` draws a character on another's named bone
  as the renderer poses the carrier in the drawn frame (a rider on a mount; the rule and its limits:
  [character-renderer](character-renderer.md#bone-anchors-meitou-engine-choice)); null removes it. Set it in the tick from the mod's own
  system (the character's own partition in a parallel phase); it reaches the host with the snapshot it was set in, so no render-thread
  code is needed. Display only: not saved, not hashed, read by no system, so set it again after a load and keep the character's simulated
  position at its carrier's (that position culls it, and draws it while the carrier is not drawn or lacks the bone). The vanilla shoulder
  carry overrides it while the character is carried.
- **Persistence:** a mod system implementing `IModSaveData` (`Write`/`Read(Stream)`) is saved under its name in the mod's sidecar file
  (save), captured between ticks with the save and restored right after `SaveLoader.Load`, before the first
  tick. `Read` runs only when the save has data for the system. Vanilla save files stay byte-exact.
- **Interface:** `AddUi(IUiExtension)`: namespaced styles, controls, actions, overrides and slot contributions (`UiSlots`), registered
  with each new interface (`ConfigurePlayerInterface`, Vulkan and Metal). A picture from the mod's folder is an image node:
  `new UiNode(id, UiNodeKind.Image) { Image = Path.Combine(context.Folder, "face.png") }`; a CHARACTER record's portrait is
  `{ Portrait = "<string id>" }` on the same kind of node ([native UI](native-ui.md#layout-input-and-lifetime)).
- **Debug rows:** `AddDebugCommand(label, run, hasField)` adds a row to the F10 menu after the vanilla ones. `run` gets
  `DebugCommandContext` (field, selection, camera target, tick, `Enqueue`) on the host thread while a tick may be in flight, so it must
  not touch the world; it returns the status line, and a throw shows as the status.
- **Weather kinds:** a WEATHER or EFFECT `affect type` outside 1-5 is kept as its number, with ARMOUR `weather protection<N>` and RACE `weather
  immunity<N>` naming it the same way, so a mod defines a hazard in data and acts on it from its own system
  ([weather](formats/weather.md#effects-on-characters), "Undefined kinds": what the vanilla systems still do with such a kind). Nothing
  claims a number; take one from 1000 up, as the other mod codes.
- **Commands:** a mod's own `SimCommand` types, queued with `Enqueue` and handled in its system's `Inputs`.

## Harmony

- With code mods enabled the JIT warm-up (`JitWarmup`) starts only after `Initialize` returns. Tiered compilation is off, so a caller
  compiled before a patch keeps any inlined copy of its target.

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

## Versioning

- The manifest's `apiVersion` must lie in `ModApi.MinimumVersion` (1) to `ModApi.Version` (2); otherwise the mod is refused at load with a
  message. 2 added `version`, `dependencies`, `loadAfter`, `incompatible`, `gameVersion`, `ModContext.Dependency<T>` and
  `IModDependents`; an API 1 mod loads unchanged. `gameVersion` ties a mod to the engine releases it was built against.
- Harmony patches non-public members; compile-time references need public types or a publicizer. Open: which simulation types to make
  public, or ship publicized reference assemblies; ship reference assemblies per release so modders build without a game install.

## Evidence

**Observed** (2026-11-04, win-x64, a scratch mod and harness against the release build, outside the repo, run as the game runs: tiered
compilation and PGO off, server GC): only allowed mods with a valid
manifest load and each refusal gives its notice; the process holds one `0Harmony` and one `Meitou.Modding` though the mod folder had its
own `0Harmony.dll`; a prefix applies, and a throwing `Initialize` loses its patch and registrations; a mod system's state survives
capture and restore; sidecars survive `SaveGame.Write`/`Load` and `Clone`; spliced systems land at their anchors and the standard list is
unchanged. The dialogue hooks are built but not exercised. **Verified** by `ModdingTests`: game assemblies resolve from the game, anchors,
sidecar round trip, and a hashed mod system agreeing at 1 and 4 threads.
