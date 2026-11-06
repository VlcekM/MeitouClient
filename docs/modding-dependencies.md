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
  ]
}
```

- `version`: `MAJOR.MINOR.PATCH`; a `-pre`/`+build` suffix is allowed and ignored when comparing. Optional; a mod without one satisfies
  only a dependency without a range.
- `dependencies`: other code mods by manifest `id` (the key that already names saves, Harmony and log lines; not the folder or `.mod`
  name). A string is a required dependency on any version. An object takes `id`, an optional `version` range and `optional`.
- Range: comparators separated by spaces, all must hold: `>=`, `>`, `<=`, `<`, `=`; a bare version is `=`. Example: `>=1.2.0 <2.0.0`.
- Refused at read: a mod listing itself, an id listed twice, a bad range, a non-boolean `optional`.
- `dependencies` needs `apiVersion` 2; an API 1 manifest that declares them is refused (an older game would ignore the key and load a
  private copy, the bug this fixes).

## Rules

`mods.cfg` stays the only load order; nothing reorders on its own.

| Dependency state | Required | Optional |
| --- | --- | --- |
| Loaded earlier, version in range | Shared | Shared |
| Loaded earlier, version out of range | Refused | Refused |
| Enabled but later in the list | Refused: "move X above it" | Refused (it would load with a private copy otherwise) |
| Not installed or not enabled | Refused | Left out |
| Enabled, earlier, but refused or not trusted | Refused with its reason (cascades) | Left out, logged |

Each refusal is a log line and a notice, as for other refusals. Dependency checks run before any of the mod's DLLs are opened.
Cycles cannot load: one side is always later.

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
- Also list the framework's `.mod` in your FCS header `dependencies`, so Kenshi's launcher and the header sort order the data too.
  Not enforced.

## Launcher

- A code mod's row reads "Requires ..." and, in the warning line, each problem the game would refuse at start (missing, disabled,
  later, version), refreshed as mods are toggled or moved. Same rules (`CodeModDependencies.Check`) as the game.
- **Arrange** (beside Refresh): best effort. Moves each mod after the installed mods it depends on (optional ones too), keeping the
  rest of the order; mods in a cycle keep their order after the rest. It enables nothing and cannot fix a version; problems that
  remain stay marked. Not saved until Save mod list or Play.
- Trust is per mod: allowing a dependent never allows its dependency.

## Compatibility

- `ModApi.Version` is 2 and `ModApi.MinimumVersion` 1: the game runs manifests naming 1 or 2. API 1 mods (no new keys) load exactly as
  before. An API 2 mod on an API 1 game is refused with the existing message.
- Saves: unchanged; each mod's sidecar stays under its own id.
- `meitou-tools mod-check --mod a --mod b`: the folders load in the order given, under the same rules, so pass a framework before its
  dependents.

## Trade-offs

| For | Against |
| --- | --- |
| One framework instance; authors build on each other instead of copying | A framework update can break dependents; semver ranges and refusals make that visible instead of silent |
| `mods.cfg` remains the single order for code and data; refusals say how to fix | Players must order mods; mitigated by the launcher's marks and Arrange |
| Per-mod isolation for third-party libraries is kept | Exports are whole assemblies; no finer visibility than `public`/`internal` |
| Small surface: two manifest keys, two `ModContext` members | API bump: API 2 mods do not run on older games |

Alternatives not taken: auto-sorting code mods (code and data would load in different orders); FCS header dependencies as the source
(file names, no versions); one shared load context (loses library isolation); exporting every DLL in a folder (leaks private
libraries); a service registry alone (the interface type must be shared anyway; can be added on top later).

## Evidence

**Verified** by `ModDependencyTests` (2026-11-06, Linux x64, .NET SDK 10.0.114): manifest keys and errors, range matching, list rules,
Arrange order, shared vs isolated assembly resolution, refusals before code is opened (missing, later, refused cascade, API 1 with
dependencies), absent optional dependency, `Dependency<T>`. **Unknown**: a real two-mod game run and the launcher marks drawn
(built, not run here).
