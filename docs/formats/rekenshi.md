# RE_Kenshi mod compatibility

Meitou handles compatibility per mod. Ordinary mod data and assets load through the
normal readers. Meitou supports [file rebinds and sound banks](rekenshi-data.md) from
`RE_Kenshi.json`. RE_Kenshi plugin DLLs are ignored.

- **Built in.** Implement features that fit the engine as Meitou settings or fixes.
  When a supported mod is enabled, Meitou applies its settings once and shows a notice.
  `CoveredMods` tracks the mods with built-in support.
- **Ported.** Use the RE_Kenshi wishlist to prioritize ports to the [code-mod API](../modding.md).
  Maintainers distribute and update ports separately on Steam Workshop. Record endorsements
  in `CoveredMods.Endorsed`. Record the original Workshop version when compatibility was checked.
  The [launcher](../launcher.md) offers to subscribe to endorsed ports. Enable a port below
  the original mod when it needs the original's data.
