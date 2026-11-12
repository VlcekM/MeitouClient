# RE_Kenshi mod compatibility

**Decision (owner, 2026-10-18): Meitou is not compatible with RE_Kenshi.** It does not load RE_Kenshi
plugins (`RE_Kenshi.json`, `startPlugin`), offers no KenshiLib facade and plans no plugin source ports. A
mod's ordinary data and assets still load through the normal readers; its DLLs are ignored.

The research behind it (2026-10-11) follows. External findings are **Observed** in upstream source or
documentation, not Verified by running plugins. Links track upstream branches and may change.

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
