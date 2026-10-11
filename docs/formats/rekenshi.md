# RE_Kenshi mod compatibility

Feasibility research, 2026-10-11. No runtime support is implemented by this investigation.
External findings below are **Observed** in upstream source or documentation, not Verified
by running plugins. Links track upstream branches and may change. Recommendations are proposals,
not adopted architecture decisions. Compatibility coverage and performance remain **Unknown**.

## What the existing interface requires

- **Observed:** RE_Kenshi's [Plugins.cpp](https://github.com/BFrizzleFoShizzle/RE_Kenshi/blob/master/Plugins.cpp)
  reads `RE_Kenshi.json` in mod directories. `PreloadPlugins` and `Plugins` are arrays of DLL
  filenames. The preload pass walks `availabelModsOrderedList` after `GameWorld::initModsList`;
  the postload pass walks `activeMods`. These are different lists: discovery must not assume
  that both passes only inspect enabled mods. DLLs load through `LoadLibraryW`, then the loader
  resolves the C++ export `?startPlugin@@YAXXZ`. Reproducing this entry point is only loading,
  not engine compatibility.
- **Observed:** [KenshiLib's README](https://github.com/BFrizzleFoShizzle/KenshiLib)
  requires the VS2010 toolchain, Boost 1.60 and KenshiLib/Ogre linkage for plugins. Its
  version-independent interface means access across supported Kenshi versions; it is not
  evidence of compatibility with another engine.
- **Observed:** [Functions.h](https://github.com/BFrizzleFoShizzle/KenshiLib/blob/RE_Kenshi_mods/Include/core/Functions.h)
  provides real-address lookup and function hooks, including an original-function pointer.
  [Character.h](https://github.com/BFrizzleFoShizzle/KenshiLib/blob/RE_Kenshi_mods/Include/kenshi/Character.h)
  exposes public fields with native offsets, virtual methods, inheritance and native containers.
  The interface permits direct memory access rather than requiring opaque handles and accessors.
- **Observed:** The official [examples](https://github.com/BFrizzleFoShizzle/KenshiLib_Examples)
  cover MyGUI widgets, dialogue conditions/effects, custom data, persistent world-state variables,
  cross-plugin exports and material changes. Support therefore extends beyond simulation callbacks.

## Meitou implications

**Observed (local source inspection):** `src/Meitou.Data/Fcs/FcsRecord.cs` stores an integer type
and named property dictionaries. This provides a useful representation for extension data;
it does not establish that every custom record survives loading, merging and saving correctly.
That must be tested with representative packages. The engine uses a managed simulation with
ordered tick phases and published snapshots, and its renderer is Vulkan ([engine](../engine.md),
[simulation](../simulation.md)). These are not Kenshi's native object layouts or hook locations.

**Assessment:** .NET can interoperate with native code; the language alone is not the blocker.
An unchanged DLL would require the binary interface it actually uses: exports, object layouts,
virtual tables, container/allocator conventions, lifetimes and callable hook targets. Direct
field writes bypass any replacement KenshiLib accessors. A native facade would need coherent
mirrors of exposed objects and synchronization at every relevant callback. Arbitrary executable
patches would add another problem. Whether any particular plugin needs those patches is **Unknown**.

## Feasible support levels

| Target | Assessment / proposed approach |
| --- | --- |
| Ordinary data/assets bundled with a plugin | Reuse existing readers; report missing runtime behavior rather than claim full support. |
| Content depending on a known extension | Implement that extension's data semantics in Meitou; retain existing field names and content files. |
| Existing plugin source | Port to explicit Meitou events/services. Some behavior can be reused, but native object/UI access needs adaptation. |
| Selected unchanged DLLs | Possible research target for a narrowly specified native facade; no demonstrated compatibility yet. |
| Arbitrary unchanged DLLs | Not a realistic general promise: it would constrain Meitou to extensive original-engine implementation details. |

**Observed:** [KenshiExtensionPlugin's modder documentation](https://github.com/Lucius64/KenshiExtensionPlugin/wiki/Modder-Features)
describes extra FCS fields, including weapon-model damage modifiers, race/faction idle stances,
animal armour slots and special combat techniques. Its [bug-fix documentation](https://github.com/Lucius64/KenshiExtensionPlugin/wiki/Bug-Fixes)
also describes changes to existing behavior. This suggests a useful distinction: dependent content
can potentially remain unchanged while Meitou supplies the extension behavior; the KEP DLL itself
need not run. Exact defaults, option gating, precedence, saves and version differences are **Unknown**
until each selected feature is checked against source and fixtures. Gameplay fixes must be explicit
compatibility options, not silently applied to every vanilla playthrough.

## Recommended investigation sequence

1. Inventory representative installed packages without executing DLLs: manifests, versions,
   dependencies, configuration, custom fields and persistent state. Report unsupported requirements.
2. Choose one KEP weapon-model modifier as a small data-compatibility experiment. Establish its
   defaults, interaction with vanilla values and enabled/disabled behavior; test a dependent mod
   unchanged. This is a candidate, not a claim that combat parity already exists.
3. Port a small behavioral plugin to establish the needed API: stable entity handles, ordered
   callbacks, queued simulation changes, separate UI work and versioned persistent state.
4. Check repeated-run and thread-count determinism, save/load preservation, and conflicting mods.
   Persistent extension data must never disappear silently when an extension is unavailable.
5. Only investigate native DLL compatibility against a named plugin/version and a measured list
   of imports, field accesses and hooks. A successful logging-only plugin would not validate gameplay.

**Unknown:** ecosystem-wide coverage, effort, plugin/save interoperability in either direction,
and whether a useful subset of real DLLs fits a small facade. No plugins were run or modified,
and no third-party implementation or game assets were added to the repository.
