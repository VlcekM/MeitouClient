# Code mods in practice

What authors of published code mods ran into from the v0.16.1 nightlies to v0.17.5-nightly.20261112.2, beyond the contract in
[modding](modding.md) and [dependencies](modding-dependencies.md). Line numbers are at `d8daa17`.

## Versions

- Meitou's assembly version is its release number (`MeitouClient.dll` 0.17.2.0 in v0.17.2-nightly.20261109, 0.17.3.0 in v0.17.3,
  0.17.5.0 in v0.17.5-nightly.20261112.2; **Verified** from the DLLs), and .NET binds a reference only to that version or a higher
  one: build against the **oldest** release the mod supports. A build against a newer release does not load on an older one.
- One `net10.0` build against v0.17.3 covers the .NET 11 releases too: such a mod initializes and passes `mod-check` on v0.17.3
  (`net10.0`) and on v0.17.5-nightly.20261112.2 (`net11.0`). **Observed** (2026-11-12, win-x64). A `net11.0` mod needs .NET 11
  framework assemblies, which v0.17.3's runtime lacks.
- A mod built for the eleven-assembly releases (v0.17.1 and older) is refused by later ones with `not loaded: entry type X is not in
  its assemblies`. **Observed** (2026-11-09). One package can serve both layouts: an entry assembly built against the oldest classic
  `Meitou.Modding.dll` picks a classic or modern build by `typeof(IMod).Assembly`'s name, beside a forwarding `Meitou.Modding.dll`
  that only the modern game reads. **Observed** (2026-11-09, through `mod-check` on v0.16.1-nightly.20261106.2, v0.17.1 and
  v0.17.2-nightly.20261109).

## Members found by name

- `AccessTools.Method`/`Field`/`TypeByName`, reflection and `[HarmonyPatch("Name")]` return null or patch nothing when a member is
  renamed or moved: no compile error, no exception. **Observed** renames:
  - v0.17.2-nightly.20261109: PlayerInterface split into part classes (`AddNameTags` to `StealthView`, `AddItemCell` to `ItemCells`,
    `BindFaithfulScreen` to `ScreenRouter`, `AddMapContent` to `WorldMap`, `Verbs` to `ContextMenuWindow`, `AddRichnessLine` to
    `ProductionWindows`); enum members and record types PascalCase (`FcsRecordType.ITEM` to `Item`,
    `TaskType.TRAVEL_TO_TARGET_PACKAGE` to `TravelToTargetPackage`); fields `_x` or PascalCase (`zones` to `_zones`,
    `GameHost.session` to `Session`).
  - v0.17.3: `PlayerInterface.InventoryScale` to `UiScale`; `PlayerSystem.QuickMove` to `QuickMoves.QuickMove(World, QuickMoveItem)`;
    `CombatSystem.Projectile` from nested to a top-level struct.
- A postfix on a member that moved into a part class gets the part as `__instance`, not `PlayerInterface`. Looking a member up on "the
  first of these types that exists" misses one that moved while its old class stayed: search each type for the member.
- What works: resolve every name in `Initialize`; a missing one switches that part of the mod off with a log line (a throw would drop
  the whole mod); and check all names offline per supported release with a console program that references the game's DLLs
  (`Private="true"`), resolves each member and applies each patch with a scratch `Harmony`, then `UnpatchAll`. Harmony refuses a patch
  whose parameter names the target lacks, so this also catches signature changes.

## Threads and timing

- Commands a mod queues while the game is paused wait for the next real tick: `ApplyPausedInterfaceCommands` takes only the
  interface's own command types (`src/Meitou.Simulation/World.Interface.cs:22-40`). **Verified**.
- Town data read in an `AddSystem` factory is too early: the save loader applies town overrides and layouts after it. Read it on the
  first tick, in `Inputs`. **Observed** (v0.16.1-nightly.20261104).
- A prefix that cancels an event (a death) can see the same event again before the queued fix applies; a quiet period stops the
  repeat. Moving a carried or caged character needs `CarrySystem.LetGo` / `world.Fixtures.Release` first. **Observed**.

## Settings and saves

- One property the settings page cannot show makes `AddSettings` throw (`src/Meitou.Game/Mods/ModSettings.cs` lines 100, 214,
  224, 250, 290), which fails `Initialize`: the whole mod is refused. **Verified**. Mods check their settings class offline by
  building the group as the game does, with the internal constructor `ModSettingsGroup(modId, name, target)`.
- With `AddSettings` the player's values live in `meitou.user.json`. A settings file shipped in the mod folder is overwritten by
  every Workshop update, resetting players' choices. **Observed**.
- No per-character mod save slot exists; per-character data is keyed by what survives squad moves and reloads (portrait serial plus
  record id). **Observed**.

## Interface

- The `AddUi` overlay is arranged before the game's windows and only while the HUD is shown
  (`src/Meitou.Game/PlayerInterface.Ui.cs:513-521`), so those windows draw over it and take clicks first. **Verified**. To draw on
  top, mods postfix `PlayerInterface.AddFrameControls` and move their controls to the end. **Observed**.
- `UiSlots.Selection` is about 0.17 of the screen width; contributions there overlap the health bars. **Observed** (v0.17.3).
- The Kenshi fonts cover ASCII and U+1FBF to U+2019: no check marks or bullets. A selected list row is drawn light grey
  (`Kenshi_ListBoxItemSkin` checked 0.63 0.60 0.58). Inline `#rrggbb` colour tags in a wrapped box shift when the text has double
  spaces. **Observed**.
- A mod can ship its own picture: an ImageBox whose `ImageTexture` names a unique PNG in the mod folder (enabled mod folders are
  indexed). The painter throws on a missing file, so mods check that it exists at start. **Observed**.
- A hotkey: a postfix on `PlayerInterface.UiKey(key, ctrl, shift)` acting only when `__result` is false (edit boxes, windows and Esc
  come first) runs before the game's key bindings. **Observed** (v0.16.1 to v0.17.5 nightlies).

## Testing

- A mod console command lets an agent-channel script drive the mod (F10 rows cannot be scripted): postfix the internal
  `DevConsoleHost.RegisterConsoleCommands` (`src/Meitou.Game/DevTools/DevConsoleHost.cs:330`, called once on the first console
  toggle or agent poll, line 104), read its `DevConsole` field and `Register` a `ConsoleCommand`. **Observed** (v0.17.3).
- `--screenshot` runs load terrain only about 1.4 km around the save's camera and do not apply user settings such as the interface
  scale; pictures elsewhere need a live run. **Observed** (v0.16.1 nightlies, v0.17.3).
- Live runs that save write into the player's own save folder (`autosave0`-`2` included): back them up around a run. **Observed**.
- A local `Kenshi/mods/<Name>` replaces a subscribed Workshop copy of the same name; remove test folders after. Windows keeps a running
  game's DLLs locked, so delete a test mod only after the process exits. **Observed**.

## Shipping on the Workshop

- SteamCMD uploads (`workshop_build_item` for app 233860) work for code mods: first upload hidden, checked, then made public; a
  download of the item back shows what players get. SteamCMD cannot set tags, and an upload signs the desktop Steam client out.
  **Observed** (2026-11).

## Starting a mod with an AI coding agent

A prompt for a player to paste into a coding agent on the PC they play on, filling in the brackets. It asks for a feasibility
report before any code and carries the practice above.

```
I want to make a code mod for Meitou, the open-source C# rebuild of the Kenshi engine
(https://github.com/VlcekM/MeitouClient). Help me find out if my idea is possible, then build it.

My idea: [what the player sees and does, when it happens, what it must never do]
Inspired by / porting: [a Kenshi mod, RE_Kenshi plugin or another game's feature, with a link; or "nothing"]
I play Meitou version: [the launcher shows it; default: 0.17.3 or later]
Working name: [letters and digits, e.g. HideHeadgear]
I'll know it's done when: [the one or two things I want to see in my own game]

How to work:
1. First find my Kenshi folder and my Meitou install (the launcher keeps game versions in
   %LOCALAPPDATA%\Meitou\versions). Clone the Meitou source at the tag of the version I play
   (git clone --depth 1 --branch v<version>) and read docs/modding.md, docs/modding-practice.md
   and the wiki page "Writing code mods". Check whether the game or an existing mod already does this.
2. Then write me a short feasibility report and STOP: the verdict first (possible / possible with
   limits / not without an engine change), how each part would work (public mod API, or which
   internal game member it would patch with Harmony and how likely updates break it), what is not
   possible and the closest alternative, the decisions I need to make (with your recommendation),
   and how big it is. Mark every technical claim Verified (file:line at the tag), Observed or Unknown.
3. After I say go, build it in small steps, each with a check that can fail:
   - Build against the OLDEST Meitou version it must run on, targeting net10.0 (that also loads
     on the .NET 11 versions); reference the game DLLs with Private=false and never ship them.
   - Look up every internal game member by name in one place; if one is missing, switch that part
     off with a log line instead of crashing (by-name lookups fail silently when the game renames
     things, which happens often).
   - Never touch the world outside the simulation's serial steps: keys, F10 rows, UI and settings
     only read a snapshot and queue commands. Use the game's Rng with the mod's RngPurpose.
   - Settings on the game's Mods page (AddSettings), save data with a format number, an F10 status
     row, and one "ready" line in log.txt saying what is on.
   - Test cheapest first: a small offline check program that finds every member and applies every
     patch for real; then "meitou-tools mod-check <save> --mod <folder>" from the game folder; then
     a real game run.
4. Never change my game, my mod list (data\mods.cfg) or my saves without asking, and back them up
   first. Close the Meitou launcher before editing mods.cfg. Do not upload or post anything.
5. When done: install it (after asking), give me numbered test steps, a README, and tell me
   exactly what was tested and what was not. Never say it works in game until it ran in a game.
```
