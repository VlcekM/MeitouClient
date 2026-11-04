# Code mods (C#)

**Status: shape decided (owner, 2026-11-04), not implemented.** Meitou's own in-process loader, Lib.Harmony shipped by the game as the
sanctioned patching library, and a registration surface kept to the cases where a patch would silently break an invariant. No BepInEx. The
RE_Kenshi decision ([rekenshi](formats/rekenshi.md)) stands and its DLLs stay ignored.

## Why this shape

- **Not BepInEx.** Its chainloader discovers plugins from its own folder, not `mods.cfg`, so code mods would not enable, disable or order with
  their data mod. It injects via Doorstop (proxy DLL on Windows, `LD_PRELOAD`/`DYLD_INSERT_LIBRARIES` on Unix), which fights the launcher and the
  signed macOS build when the host is ours and can load assemblies itself. **Observed** (2026-11): BepInEx 6 still had no stable release;
  downstream projects built on `be.*` builds in 2026-06.
- **Harmony is not an alternative to an API.** The game is a JIT, self-contained, untrimmed, non-single-file publish, so Harmony works on it
  whether blessed or not. The decision is only what the engine promises beyond raw patching. **Observed**: Lib.Harmony 2.4.2 added .NET 10
  support ([releases](https://github.com/pardeike/Harmony/releases)).
- **No accessor mirrors.** Mods reference `Meitou.Simulation` and the other `Meitou.*` assemblies directly, as a patch needs the types anyway.
  Compatibility is per game version (RimWorld's model); a refactor may break a mod and that is accepted.

## Discovery

- A mod carries code when its folder (next to the `.mod`) has `meitou.json`:
  `{ "id", "apiVersion", "assemblies": ["Foo.dll"], "entry": "Foo.FooMod" }`. It may sit beside a `RE_Kenshi.json`.
- `ModInfo` reports it beside `HasNativePlugin`; the launcher shows which mods carry code.
- Load set: the enabled mods of `mods.cfg` (`ModList.ReadEnabled`) in order, filtered to those with a manifest. A disabled mod's DLL is never
  opened. Enabling and disabling take effect at the next launch, like data mods.

## Loading

- One non-collectible `AssemblyLoadContext` per mod, loaded once at startup. No unload, no hot reload: systems are held by reference and
  registered delegates would keep a collectible context alive.
- Resolution by simple name: an assembly already in the default context (`Meitou.*`, the framework, shared third-party such as Bepu, and
  `0Harmony`) comes from there, so a mod never holds a private copy of engine types; anything else comes from the mod folder, so two mods may
  ship different versions of the same library.
- **One Harmony, loaded by the game.** **Observed** ([pardeike/Harmony #741](https://github.com/pardeike/Harmony/issues/741), open since
  2025-10): outside Unity, Harmony loaded in several load contexts duplicates `HarmonySharedState`, and two mods can no longer patch the same
  method. The game therefore references Lib.Harmony, pins one version per release, and the resolution rule hands every mod that copy even when
  the mod ships its own `0Harmony.dll`. A mod gets a `Harmony` instance whose id is its manifest id.
- Constraint: the game stays a JIT publish, never `PublishTrimmed`, `PublishAot` or single-file. **Verified** (`tools/scripts/package-release.ps1`):
  the game and viewer publish self-contained with `Meitou.*.dll` beside the executables. The launcher's AOT is unaffected; it loads no mod code.
- Mods compile against the release's `Meitou.*.dll`, `0Harmony.dll` and a small `Meitou.Modding.dll` contract assembly (lifecycle,
  registration, persistence). Open: ship reference assemblies per release so modders build without a game install.
- Trust: a workshop DLL is arbitrary code running as the user, as with RE_Kenshi. The launcher marks code mods and the game confirms each new one
  on first run. No sandboxing: .NET has none, and claiming one would be worse than saying so.

## Lifecycle

- `IMod.Initialize(ModContext)` in `Meitou.Modding`; the type comes from the manifest's `entry`, no reflection scan.
- Runs after the data load and mod merge, inside the wiring at `GameHost.Saving.cs` before `StandardSystems.Build` returns, in `mods.cfg` order
  (later mods see earlier registrations, like data overrides). Patches applied in `Initialize` are in place before the first tick.
- `ModContext`: the merged `GameData`, the mod folder, a logger, a mod-scoped `Rng` purpose salt (hash of the manifest id), the mod's `Harmony`
  instance, `Enqueue(SimCommand)`, and the registration surface below.
- A throwing `Initialize` disables that mod for the session and logs; it does not stop the game.

## Registration surface

Only where a Harmony patch would break an invariant without an error. Everything else is Harmony; a new registration point is added when a
patch in the wild keeps breaking, not ahead of demand.

- **Tick systems:** `AddSystem(ITickSystem, Anchor)`, `Anchor` = before/after a `StandardParts` member. `StandardSystems.Build` stays the single
  wiring authority and splices mod systems at the anchors; `IStateHashed` puts a system's state into `World.StateHash`. A prefix on a
  parallel-phase method that writes another partition's slots corrupts the hash silently. Open: anchors from the start, or append-only first.
- **Data-referenced codes:** dialogue condition and effect codes above the vanilla range (`DialogueSystem.Condition` switches on numeric codes)
  and custom FCS record types, registered by number so FCS data can name them. A transpiler on a `switch` breaks on every edit of that method.
  `FcsRecord` keeps unknown int types with their properties, so loading works; survival through merge and save is **Unknown** until tested.
- **Persistence:** `ISaveable.Write/Read(Stream)` into a sidecar file per mod in the save folder, keyed by manifest id. Vanilla save files stay
  byte-exact ([save](formats/save.md)); a save loaded without the mod keeps the sidecar and the original game ignores it, so saves stay portable.
  Harmony has no persistence story; the alternative is patching the byte-exact writer.
- **UI:** `IUiExtension` and `UiRegistration` (`Meitou.Ui`) exist: namespaced styles, controls and actions, overrides of vanilla ones, and slot
  contributions (`UiSlots`), owned and disposable. `PlayerInterface.RegisterUiExtension` is the host entry. **Verified** (code): present, not yet
  reachable from a mod.
- **Commands:** console commands (`GameConsole`) and the mod's own `SimCommand` types, handled in its system's `Inputs`.

## Versioning

- The manifest's `apiVersion` is checked against the game's; a mismatch refuses the mod at load with a message instead of a
  `MissingMethodException` mid-game.
- Harmony patches non-public members; compile-time references need public types or a publicizer. Open: which simulation types to make public,
  or ship publicized reference assemblies.

## Determinism contract

Mod systems and patches alike obey [simulation](simulation.md) "Threading": parallel phases write only their partition's `Next`, read `Previous`
and the grid, send cross-character effects through `EffectBuffer`; randomness through `Rng.Hash` with the mod's salt; no `World` access outside
the tick (tick-ahead runs on `Meitou.Tick` during the draw); outside input goes through `Enqueue`. Violations break golden hashes and any future
replay. Nothing in the API can stop a patch, so the guard is a debug mode that compares `StateHash` across a repeated run. Open: running a mod
system twice catches API systems only; a patched vanilla method needs the whole tick replayed from a copy of `World`.

## Platforms

- Managed DLLs run unchanged on the Windows, Linux and macOS builds. Harmony's detours come from MonoMod.Core: .NET Core on x64 Linux and macOS
  **Observed** in MonoMod's release notes; arm64 (the `osx-arm64` release) is reported merged in MonoMod v25.x by a third-party write-up only, so
  code mods there are **Unknown** until a patch is tested on that build.
- **Observed** (Harmony 3.0 RC notes): native async methods cannot be patched (`NotSupportedException`). Irrelevant on .NET 10; a constraint if
  the game moves to .NET 11 runtime-async.
