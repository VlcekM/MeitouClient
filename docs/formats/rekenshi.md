# RE_Kenshi mod compatibility

**Decision (owner, 2026-11-10, replacing 2026-10-18's "not compatible"): compatibility is per mod, by
behaviour.** Meitou never loads RE_Kenshi plugin DLLs (`RE_Kenshi.json`, `startPlugin`) and offers no
KenshiLib facade. The public wishlist of RE_Kenshi mods decides which plugins get one of:

- **Built in:** a feature that fits the engine (item rotation) becomes a Meitou option or tweak. Enabling
  the RE_Kenshi mod switches the option on, with a note saying so.
- **Ported:** the plugin is rewritten against the [code-mod API](../modding.md). Ports live in this repo
  and ship with the game; when an enabled mod carries a plugin that has a port, the game runs the port (a
  DLL and `meitou.json` of its own) in the plugin's place.

A mod's ordinary data and assets load through the normal readers either way.

## What the existing interface requires

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
