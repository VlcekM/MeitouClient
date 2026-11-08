# Code-mod dependencies

Code-mod API 2: one code mod declares others it builds on and then uses their loaded assemblies (one copy of each type and its statics)
instead of a private copy. Code: `CodeModManifest`, `CodeModVersion`, `CodeModChecks`, `CodeModDependencies` (`Meitou.Core`),
`ModLoadContext`, `ModHost`, `ModContext.Dependency<T>` (`Meitou.Modding`), the launcher's mod list. Tests: `ModDependencyTests`.
General loader contract: [modding](modding.md).

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
- `dependencies`: other code mods by manifest `id` (not the folder or `.mod` name). A string is a required dependency on any version; an
  object takes `id`, an optional `version` range and `optional`.
- `loadAfter`: ids of code mods that, when enabled, must load earlier. Ordering only: nothing is imported or required.
- `incompatible`: code mods (id, or `{ id, version }` for some versions) this one must not run beside. Either side declaring it is enough;
  the one that loads second is refused. `optional` is refused here.
- `gameVersion`: the Meitou releases the mod was built against (its references bind to engine methods; `apiVersion` covers only the loader
  contract). The running version is the build's nearest game tag (`git describe --tags --abbrev=0 --match "v*"`, Meitou.Game.csproj;
  `launcher-v*` tags never count; release and nightly zips embed their own `v<version>`, package-release.ps1), compared as its release: pre-release and build are dropped, so `v0.16.1-nightly.20261106` counts as
  `0.16.1`. `>=0.16.1` takes every 0.16.1 nightly; `<0.17.0` refuses 0.17.0 nightlies. A range naming a pre-release refuses the manifest.
  Local builds carry the nearest `v*` tag too; only a build without one (no git history) passes every range.
- Range: comparators separated by spaces, all must hold: `>=`, `>`, `<=`, `<`, `=`; a bare version is `=`.
- Every field is checked for its JSON kind; a wrong kind (`"apiVersion": "2"`) is a malformed manifest that refuses that mod only. The
  pre-scan (`CodeModDependencies.ReadEntry`) never throws; for a malformed manifest it keeps the `id` if that still reads, so dependents
  name the broken mod.
- Refused at read: a mod listing itself in any list, a dependency listed twice, a bad range, a non-boolean `optional`.
- `dependencies`, `loadAfter`, `incompatible` and `gameVersion` need `apiVersion` 2; an API 1 manifest using one is refused (an older game
  would ignore the key).

## Versions

- Parsing: `MAJOR.MINOR.PATCH[-PRERELEASE][+BUILD]`, numbers without leading zeros; pre-release and build identifiers are dot-separated,
  non-empty `[0-9A-Za-z-]`; numeric pre-release identifiers have no leading zeros.
- Precedence (SemVer §11): a pre-release is lower than its release; pre-release identifiers compare one by one, numbers numerically and
  below text, more identifiers win a tie. Build metadata is ignored, by equality too.
- `dependencies` and `incompatible` ranges accept a pre-release only when a comparator names a pre-release of the same
  `MAJOR.MINOR.PATCH` (as npm and Cargo): `>=1.2.0` refuses `1.2.0-alpha`, `<2.0.0` refuses `2.0.0-alpha`. `gameVersion` instead
  compares releases (above).

## Rules

`mods.cfg` stays the only load order; nothing reorders on its own. One rule set (`CodeModChecks.Problems`) serves the loader, which
refuses on the first problem, and the launcher, which shows all. Both judge a mod against the mods that loaded (launcher: will load, i.e.
no problem and allowed to run code), never merely enabled ones, so a refused mod takes no id, conflicts with nothing and serves no
dependent.

| Dependency state | Required | Optional |
| --- | --- | --- |
| Loaded, version in range | Shared | Shared |
| Loaded, version out of range | Refused | Refused |
| Enabled but later in the list | Refused: "move X above it" | Refused (it would bind a private copy otherwise) |
| Not installed | Refused: "not installed" | Left out |
| Installed, not enabled | Refused: "not enabled" | Left out |
| Enabled earlier but refused | Refused: "which is refused: (its reason)", cascading | Left out, logged |

- The refused case names the last enabled mod before the dependent with that id (the one that would have served).
- Checked before any DLL is opened, in order: API range and API 2 keys, `gameVersion`, an id a loaded mod has, a conflict with a loaded mod,
  a `loadAfter` mod enabled later ("move X above it"), the dependencies. Each refusal is a log line and a notice. Cycles cannot load.
- The game passes the installed but disabled code mods to the loader (`ModHostOptions.Disabled`) only for the "not enabled" message.

## Failed initialization

- When a dependent's `Initialize` throws, the loader drops its pending registrations, `UnpatchAll`s its Harmony id, then calls
  `IModDependents.DependentFailed(id)` once on every mod it depends on, directly or transitively, whose entry implements it. A throw there
  is logged and ignored. The dependent is refused; its dependencies stay loaded.
- The loader cannot undo what a dependent did through a framework's API: a framework that takes registrations keys them by the caller's
  id and implements `IModDependents` to drop them.

## Assembly resolution

`ModLoadContext.Load(name)` checks, in order:

1. Game assemblies (trusted platform list).
2. The dependencies' exports: each loaded dependency's manifest `assemblies` and, transitively, what its dependencies export, returned as
   the already-loaded `Assembly`.
3. The mod's own folder.

- Only manifest `assemblies` are exported; other DLLs in a dependency's folder stay private.
- Listing an imported assembly in your own `assemblies` refuses the mod; a stray copy in your folder is ignored.
- Two dependencies exporting different assemblies under one simple name refuse the dependent; a diamond (one shared assembly) is fine.
- A framework DLL loads once, so the [Harmony](modding.md#harmony) caveat about one DLL in two mods does not apply to it.

## Using a dependency

Dependencies initialize first, so their registrations are committed when a dependent's `Initialize` runs.

```csharp
public void Initialize(ModContext c)
{
    var rivalry = c.Dependency<IRivalryApi>("rivalry.core")!;      // required: never null
    if (c.HasDependency("settlement.visitors")) UseVisitors(c);    // optional: test first
}

// Apart and not inlined: the JIT resolves the types a method names when it compiles it.
[MethodImpl(MethodImplOptions.NoInlining)]
static void UseVisitors(ModContext c) => c.Dependency<VisitorsMod>("settlement.visitors")!.AddBuyer(...);
```

- `Dependency<T>(id)`: the dependency's entry as `T` (its type or an interface it implements); null for an optional one that did not
  load. Throws for an id the manifest does not declare, or when the entry is not a `T`. `HasDependency(id)` tests presence.
- An optional mod's types may appear only in code that runs after `HasDependency`: not in `Initialize` itself, fields, signatures or
  lambdas it creates. Naming them in `Initialize` throws `FileNotFoundException` when the mod is absent (the dependent is refused), or
  binds a copy shipped in your folder; reference the framework without copying it (`Private="false"`).
- Public static members of a framework's assemblies also work directly once the types are shared.

## Writing a framework

- Put the public surface in one assembly listed in `assemblies`; keep helpers `internal` or in a private DLL.
- Expose an interface (`IRivalryApi`) implemented by the entry type, so dependents call `c.Dependency<IRivalryApi>(...)`.
- Version with SemVer: breaking the public surface is a major bump. Dependents pin a major: `>=1.2.0 <2.0.0`.
- World state lives in the framework's own systems (`AddSystem`, `IStateHashed`, `IModSaveData`), never in statics a dependent writes
  outside the tick; the [determinism contract](modding.md#determinism-contract) is unchanged.
- Registrations from dependents: take the caller's id (`c.Id`) with each, store them by owner, implement `IModDependents`. Do not run a
  dependent's callbacks before its `Initialize` returned.
- Also list the framework's `.mod` in your FCS header `dependencies`, so the data sorts too. Not enforced.

## Launcher

- A code mod's row reads "Requires ...", "Loads after ...", "Incompatible with ...", "Built for Meitou ..." and, as "Will not load:", each
  refusal of [Rules](#rules) (`CodeModChecks.Check`), plus a bad manifest. Rechecked
  when a mod is toggled or moved; a preset, a save's list or Arrange rechecks once after all rows moved.
- Preliminary: DLLs, the entry type and initialization are checked only at start. `gameVersion` is checked only by the game.
- **Arrange**: one graph of every constraint between **enabled** mods: code `dependencies` (optional ones too), `loadAfter` and FCS `.mod`
  header `dependencies`. Each mod keeps its place unless a mod it needs comes later; that mod moves up to just before it (its own
  prerequisites first), so data overrides keep their order (`[A needs C, B, C]` gives `[C, A, B]`). Disabled mods add no constraint. The
  moves are listed ("3 → 1  Framework") and applied only when accepted, then saved. A cycle, including a code
  constraint contradicting a data one, moves nothing and is shown edge by edge (`code: needs 'x'`, `data: its .mod header needs X.mod`).

## Compatibility

- `ModApi.Version` is 2 and `ModApi.MinimumVersion` 1. API 1 manifests load as before; an API 2 mod on an API 1 game is refused.
- `CodeModManifest` keeps its API 1 four-parameter constructor and four-part `Deconstruct` beside the six-part ones. `ModApi.Version` is a
  `const`, so an API 1 DLL keeps the 1 it was compiled with.

## Saves

- A save made with code mods records them in `meitou-mods/environment.json` ([save format](formats/save.md#mod-data)): the Meitou tag
  and the loaded code mods in load order (id, `version`, name). A game without code mods writes it only over a save that had one.
- On load the record is compared with the running mods; each change is a log line and a notice: a mod gone (its sidecar is kept unused),
  a version changed (a downgrade or a major update called out), a different order, a new mod. The save always loads. A record that cannot
  be read (newer format, damaged, an id listed twice) is logged and skipped; the comparison never throws out of the load.
- `meitou-tools mod-check --mod a --mod b`: the folders load in the order given, under the same rules.

## Evidence

- **Verified** by `ModDependencyTests` (2026-11-06, win-x64, .NET 10): shared assembly resolution with a stray copy present
  (`A_dependent_shares_the_dependency_s_loaded_assembly`); the not installed / not enabled / later / refused-cascade refusals
  (`The_loader_refuses_missing_disabled_later_and_refused_dependencies`); Arrange moving a dependency up and its cycle report
  (`Arrange_moves_a_dependency_up_and_reports_a_cycle`); `gameVersion` taking a nightly as its release and refusing a pre-release range
  (`Game_version_takes_a_nightly_as_its_release`); a refused mod blocking no later one, launcher and loader alike
  (`A_refused_mod_blocks_no_later_one_in_the_launcher_or_the_loader`).
- **Observed** (2026-11-06, scratch runs, not in the repo): a library compiled against master's `Meitou.Core` that calls the API 1
  constructor and `Deconstruct` ran against this build; a dependent naming an absent optional assembly's type directly fails with
  `FileNotFoundException` when called, while the `HasDependency` + `NoInlining` pattern runs (win-x64, .NET 10, tiered compilation on and
  off).
- **Unknown**: a real framework-plus-dependent game run (shared state, diamond, failed initialization, save and reload) and the launcher's
  rows as drawn.
