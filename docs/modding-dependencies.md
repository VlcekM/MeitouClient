# Code-mod dependencies

**Status: proposed with code-mod API 2.** One code mod declares others it builds on; it then uses their loaded assemblies (one copy of
each type and its statics) instead of a private copy. Code: `CodeModManifest`, `CodeModVersion`, `CodeModDependencies` (`Meitou.Core`),
`ModLoadContext`, `ModHost`, `ModContext.Dependency<T>` (`Meitou.Modding`), the launcher's mod list. Tests: `ModDependencyTests`.
General loader contract: [modding](modding.md).

## Problem it solves

- Under API 1, `ModLoadContext` resolves every non-game assembly from the requesting mod's own folder. A mod that uses another mod's
  library ships a copy, which loads a second time: its types are distinct from the running framework's, its statics are a second set,
  and the framework's registrations are invisible to it. **Verified** by `A_dependent_shares_the_dependency_s_loaded_assembly`
  (the isolated context gets a different `Assembly`).
- Without a way to share, modders copy a framework's source into each mod (copies drift) or ship everything as one bundle (others
  cannot build on it).

## Manifest

```json
{
  "id": "nemesis.faction",
  "version": "0.3.0",
  "apiVersion": 2,
  "assemblies": ["NemesisFaction.dll"],
  "entry": "NemesisFaction.NemesisMod",
  "dependencies": [
    "rivalry.core",
    { "id": "settlement.visitors", "version": ">=1.2.0 <2.0.0", "optional": true }
  ],
  "loadAfter": ["better.ai"],
  "incompatible": ["old.nemesis", { "id": "rival.overhaul", "version": "<3.0.0" }],
  "gameVersion": ">=0.16.0 <0.17.0"
}
```

- `version`: a [SemVer 2.0.0](https://semver.org/spec/v2.0.0.html) version ([Versions](#versions)). Optional; a mod without one
  satisfies only a dependency without a range.
- `dependencies`: other code mods by manifest `id` (the key that already names saves, Harmony and log lines; not the folder or `.mod`
  name). A string is a required dependency on any version. An object takes `id`, an optional `version` range and `optional`.
- `loadAfter`: ids of code mods that, when enabled, must load earlier. Ordering only: nothing is imported, nothing is required, and an
  absent or disabled one is fine. Use it to run after a mod without binding to its assemblies (no assembly-name conflicts).
- `incompatible`: code mods (id, or `{ id, version }` to name only some versions) this one must not run beside. Either side declaring it
  is enough; the one that loads second is refused. `optional` is refused here.
- `gameVersion`: the Meitou releases the mod was built against (its references bind to engine methods directly; `apiVersion` covers only
  the loader contract). Compared with the running build's tag (`v0.16.1-nightly.20261106` → `0.16.1-nightly.20261106`) by plain SemVer
  precedence, without the pre-release opt-in below, so `>=0.16.0 <0.17.0` takes a 0.16.x nightly. An untagged (local) build passes.
- Range: comparators separated by spaces, all must hold: `>=`, `>`, `<=`, `<`, `=`; a bare version is `=`. Example: `>=1.2.0 <2.0.0`.
- Every field is checked for its JSON kind; a wrong kind (`"apiVersion": "2"`) is a malformed manifest (`InvalidDataException`) that
  refuses that mod only. The loader's and launcher's manifest pre-scan (`CodeModDependencies.ReadManifest`) never throws: an unreadable
  manifest reads as none there (the launcher shows why), and the loader reports it when it reaches that mod.
- Refused at read: a mod listing itself in any list, a dependency listed twice, a bad range, a non-boolean `optional`.
- `dependencies`, `loadAfter`, `incompatible` and `gameVersion` need `apiVersion` 2; an API 1 manifest using one is refused (an older
  game would ignore the key, e.g. load a private copy of a framework).

## Versions

- Parsing: `MAJOR.MINOR.PATCH[-PRERELEASE][+BUILD]`, numbers without leading zeros; pre-release and build identifiers are
  dot-separated, non-empty `[0-9A-Za-z-]`; numeric pre-release identifiers have no leading zeros.
- Precedence (SemVer §11): a pre-release is lower than its release (`1.2.0-beta < 1.2.0`); pre-release identifiers compare one by one,
  numbers numerically and below text, more identifiers win a tie. Build metadata is ignored, by equality too (`=1.2.0` takes `1.2.0+abc`).
- A range accepts a pre-release only when one of its comparators names a pre-release of the same `MAJOR.MINOR.PATCH`, as npm and Cargo
  do: `>=1.2.0` refuses `1.2.0-alpha`, `<2.0.0` refuses `2.0.0-alpha`, `>=1.2.0-beta` takes `1.2.0-beta.2` but not `1.3.0-beta`.

## Rules

`mods.cfg` stays the only load order; nothing reorders on its own.

| Dependency state | Required | Optional |
| --- | --- | --- |
| Loaded earlier, version in range | Shared | Shared |
| Loaded earlier, version out of range | Refused | Refused |
| Enabled but later in the list | Refused: "move X above it" | Refused (it would load with a private copy otherwise) |
| Not installed or not enabled | Refused | Left out |
| Enabled, earlier, but refused or not trusted | Refused with its reason (cascades) | Left out, logged |

Before any DLL is opened, in order (`CodeModChecks`, shared with the launcher): API range and API 2 keys, `gameVersion`, a taken id, a
conflict with a mod already loaded, a `loadAfter` mod enabled later in the list ("move X above it"), then the dependencies above.
Each refusal is a log line and a notice, as for other refusals. Cycles cannot load: one side is always later.

## Failed initialization

- When a dependent's `Initialize` throws, the loader drops its pending registrations and `UnpatchAll`s its Harmony id, then calls
  `IModDependents.DependentFailed(id)` on every mod it depends on, directly or transitively, whose entry implements it. A throw there is
  logged and ignored. The dependent is refused; its dependencies stay loaded.
- Limit: the loader cannot undo what a dependent did through a framework's API. A framework that takes registrations must key them by
  the caller's mod id and implement `IModDependents` to drop them (pattern below). A framework that does not is left holding them.

## Assembly resolution

`ModLoadContext.Load(name)` checks, in order:

1. Game assemblies (trusted platform list): the game's, unchanged.
2. The dependencies' exports: each loaded dependency's own manifest `assemblies` and, transitively, what its dependencies export.
   Returned as the already-loaded `Assembly`.
3. The mod's own folder, unchanged.

- Only manifest `assemblies` are exported. Other DLLs in a dependency's folder stay private, so two mods may still ship different
  versions of a third-party library.
- Listing an imported assembly in your own `assemblies` refuses the mod; a stray copy in your folder is ignored (the dependency wins).
- Two dependencies exporting different assemblies under one simple name refuse the dependent. A diamond (both export the same loaded
  assembly) is fine.
- A framework DLL is loaded once, so the [Harmony](modding.md#harmony) caveat about one DLL in two mods no longer applies to it.

## Using a dependency

Dependencies initialize first, so their registrations are committed when a dependent's `Initialize` runs.

```csharp
public void Initialize(ModContext c)
{
    var rivalry = c.Dependency<IRivalryApi>("rivalry.core")!;          // required: never null
    if (c.Dependency<VisitorsMod>("settlement.visitors") is { } visitors) // optional: null when absent
        visitors.AddBuyer(...);
}
```

- `Dependency<T>(id)`: the dependency's entry instance as `T` (its own type or an interface it implements); null for an optional one
  that did not load. Throws for an id the manifest does not declare, or when the entry is not a `T`. `HasDependency(id)` tests presence.
- Public static members of a framework's assemblies also work directly once the types are shared.

## Writing a framework (pattern for modders and agents)

- Put the public surface in one assembly listed in `assemblies`; keep helpers others must not bind to `internal` or in a private DLL.
- Expose an interface (`IRivalryApi`) implemented by the entry type, so dependents call `c.Dependency<IRivalryApi>(...)` and the
  framework can change its classes freely.
- Version with semver: breaking the public surface is a major bump. Dependents pin a major: `>=1.2.0 <2.0.0`.
- State that goes into the world (ledgers, counters) lives in the framework's own systems (`AddSystem`, `IStateHashed`, `IModSaveData`),
  never in statics a dependent writes from outside the tick; the [determinism contract](modding.md#determinism-contract) is unchanged.
- Registrations from dependents (callbacks, rules, catalogue entries): take the caller's id (`c.Id`) with each, store them by owner, and
  implement `IModDependents` on the entry to remove an owner's entries. Do not run a dependent's callbacks before its `Initialize`
  returned; the framework's own systems run only once loading is over.
- Also list the framework's `.mod` in your FCS header `dependencies`, so Kenshi's launcher and the header sort order the data too.
  Not enforced.

## Launcher

- A code mod's row reads "Requires ...", "Loads after ...", "Incompatible with ..." and "Built for Meitou ..." and, as "Will not load:",
  each refusal the game would make without opening code (`CodeModChecks.Check`, the loader's rules): bad manifest, API, taken id,
  conflict, `loadAfter` order, dependencies (missing, disabled, later, version), and a dependency that will not load itself (its own
  problem, or set to data only), cascading. Refreshed as mods are toggled, moved or allowed.
- Preliminary: a row without warnings can still be refused at start (DLLs, entry type, initialization, a dependency refused for those).
  `gameVersion` is checked only by the game, since the launcher can start several installed versions.
- **Arrange** (beside Refresh): one graph of every constraint between **enabled** mods: code `dependencies` (optional ones too, since a
  present one must load first) and FCS `.mod` header `dependencies`. It proposes the order closest to the current one (each step takes
  the earliest mod whose prerequisites are placed); disabled mods add no constraint and keep their place among the rest. The proposal
  lists each move ("3 → 1  Framework") and applies only when accepted; it is not saved until Save mod list or Play. A cycle, including
  a code constraint that contradicts a data one, moves nothing and is shown edge by edge with its reason (`code: needs 'x'`,
  `data: its .mod header needs X.mod`). It enables nothing and cannot fix a version; problems that remain stay marked.
- Trust is per mod: allowing a dependent never allows its dependency.

## Compatibility

- `ModApi.Version` is 2 and `ModApi.MinimumVersion` 1: the game runs manifests naming 1 or 2. API 1 manifests (no new keys) load as
  before. An API 2 mod on an API 1 game is refused with the existing message.
- Binary: the API 1 public surface of `CodeModManifest` is kept, the four-parameter constructor and the four-part `Deconstruct` beside
  the six-part ones. **Verified** (2026-11-06, scratch run, not in the repo): a library compiled against master's `Meitou.Core` that calls
  both ran against this build. Other API 1 members are unchanged; `ModApi.Version` is a `const`, so an API 1 DLL keeps the 1 it was
  compiled with.
- Saves: see below; each mod's sidecar stays under its own id.

## Saves

- Each save made with code mods records them in `meitou-mods/environment.json` ([save format](formats/save.md#mod-data)): the Meitou
  tag and the loaded code mods in load order (id, `version`, name). A game without code mods writes it only over a save that had one,
  so the record then says none ran.
- On load the record is compared with the running mods; each change is a log line and a notice: a mod gone (its sidecar is kept
  unused), a version changed (a downgrade or a major update called out), a different order, a mod new to the save. The save always
  loads: data mods and sidecars behave as before. A record that cannot be read (newer format, damaged) is logged and skipped.
- `meitou-tools mod-check --mod a --mod b`: the folders load in the order given, under the same rules, so pass a framework before its
  dependents.

## Trade-offs

| For | Against |
| --- | --- |
| One framework instance; authors build on each other instead of copying | A framework update can break dependents; semver ranges and refusals make that visible instead of silent |
| `mods.cfg` remains the single order for code and data; refusals say how to fix | Players must order mods; mitigated by the launcher's marks and Arrange |
| Per-mod isolation for third-party libraries is kept | Exports are whole assemblies; no finer visibility than `public`/`internal` |
| Small surface: five manifest keys, two `ModContext` members, one framework interface | API bump: API 2 mods do not run on older games |

Alternatives not taken: auto-sorting code mods (code and data would load in different orders); FCS header dependencies as the source
(file names, no versions); one shared load context (loses library isolation); exporting every DLL in a folder (leaks private
libraries); a service registry alone (the interface type must be shared anyway; can be added on top later).

## Evidence

**Verified** by `ModDependencyTests` (2026-11-06, Linux x64, .NET SDK 10.0.114): manifest keys and errors, range matching (pre-release
below its release), list rules, Arrange order and its cycle report, shared vs isolated assembly resolution, refusals before code is opened (missing, later, refused cascade, API 1 with
dependencies), absent optional dependency, `Dependency<T>`. **Observed** (2026-11-06, scratch run, not in the repo): `loadAfter`, `incompatible` and `gameVersion` refusals at load and in the
launcher check (a 0.16.1 nightly passes `>=0.16.0 <0.17.0`, fails `>=0.17.0`), the data-only cascade, `DependentFailed` reaching
direct and transitive dependencies, `environment.json` round trip and its change lines. **Unknown**: a real framework-plus-dependent
game run (shared types and state, diamond, failed initialization, save and reload) and the launcher drawn (built, not run here).
