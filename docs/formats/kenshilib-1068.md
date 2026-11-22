# KenshiLib names at Steam 1.0.68 addresses

[KenshiLib](https://github.com/BFrizzleFoShizzle/KenshiLib) (1ce4a16) names the original's functions for 1.0.65; our analysis reads
the Steam 1.0.68 exe. [`kenshilib-1068.tsv`](kenshilib-1068.tsv) gives **7,134** of KenshiLib's functions their Steam 1.0.68
address, made by [`tools/scripts/kenshilib-1068.py`](../../tools/scripts/kenshilib-1068.py) (about 15 s). The virtual address in the
1.0.68 exe is `0x140000000 +` the RVA.

## Method

1. A function's GOG 1.0.65 RVA (its KenshiLib header comment) becomes its Steam 1.0.65 RVA through RE_Kenshi's parallel tables
   `RE_Kenshi/RVAs/GOG_1.0.65.br` and `Steam_1.0.65.br` (entry *i* is the same function in both).
2. Its first 32 bytes in RE_Kenshi's 1.0.65 exe (`RE_Kenshi/Kenshi_x64.exe`) are compared with the Steam 1.0.68 exe (`kenshi_x64.exe`)
   within 64 KB of the same RVA, with the bytes that differ between builds masked: rel32 call / jmp / jcc targets and RIP-relative
   displacements. Only a single match is taken; none or several retries with 64, 128, 16 and 12 bytes, and a function still not
   unique is left out.
3. The class is the header's `class` / `struct` / `namespace` block around the declaration (nested as `Outer::Inner`), not the file
   name: several headers hold more than one class.

**Verified** (Steam 1.0.68 sha256 `a596ab4e407c67b58599c54ffb32dc1bf2b64510cdebd3fa9359ef05a576aeb1`; RE_Kenshi 0.3.5's 1.0.65 exe
sha256 `504b362cde850d56afb1cea6f5b7b0ee014d9dd7b47e188599d91c804502cd3e`): of 7,313 KenshiLib functions with an RVA, 7,134 match
once, 152 several times (identical small overrides and getters) and 27 nowhere; no 1.0.68 address is given twice. An address holds
the code of the 1.0.65 function KenshiLib names; the name stays **Observed**, as for every KenshiLib name in these docs.

Columns: `name` (`Class::method`), `steam_1065_rva`, `steam_1068_rva`, `found_by` (`bytes-<n>`: the bytes compared), `signature`
(KenshiLib's declaration with parameter names and defaults removed; overloads differ by it), `header`.

## Where a mod hears what happens

The original's functions at which the things a mod usually reacts to happen, and how a Meitou code mod hears the same
(**Observed**, by reading Meitou 2b263f7's source and [modding](../modding.md)):

| When | Original (KenshiLib) | 1.0.65 | 1.0.68 | Meitou |
| --- | --- | --- | --- | --- |
| Every tick | `GameWorld::charsUpdate()` | 0x7862F0 | 0x786E30 | a tick system, `AddSystem` ([tick systems](../modding.md#registration-surface)) |
| A paused frame | `GameWorld::charsUpdatePaused()` | 0x7866F0 | 0x787230 | no tick; the paused interface's commands reach a system's `Inputs` between ticks (`World.ApplyPausedInterfaceCommands`) |
| A key | `InputHandler::keyDownEvent(OIS::KeyCode)` | 0x360AD0 | 0x360B30 | `AddKeyAction` |
| Save | `PlayerInterface::serialise(GameData*)` | 0x7F32C0 | 0x7F3E60 | `IModSaveData.Write`, the mod's sidecar file |
| Load | `PlayerInterface::loadFromSerialise(GameData*)` | 0x7F3410 | 0x7F3FB0 | `IModSaveData.Read` |
| Load chosen on the title screen | `TitleScreen::loadGame(MyGUI::Widget*)` | 0x913F30 | 0x914E00 (1) | `IWorldReady.Ready(world, fromSave: true)` |
| The world ends | `GameWorld::~GameWorld()` | 0x86C780 | 0x86D650 | a system that is `IDisposable` is disposed with its world (`WorldSession`) |
| A character leaves the world | `Character::~Character()` | 0x621FF0 | 0x622A80 | no observer; a mod goal's `End` says `Gone` |
| Selected / unselected | `Character::select()` / `unselect()` | 0x5C8A60 / 0x5C8A90 | 0x5C94F0 / 0x5C9520 | no observer; `PlayerInterface.SelectedIds()` (internal) |
| A death | `Character::declareDead()` | 0x7A5660 | 0x7A6200 | `ICharacterDeathObserver.Died` |
| Picks someone up | `Character::pickupObject(Character*)` | 0x5CF500 | 0x5CFF90 | no observer; `CharacterCold.Carrying` |
| Is picked up | `Character::getPickedUp(Character*)` | 0x5CE300 | 0x5CED90 | no observer; `CharacterCold.CarriedBy`; a mod goal's `End` says `Replanned` |
| Money taken | `Character::takeMoney(int)` | 0x795A50 | 0x7965F0 | `ITradeObserver` (deals), `IGoodsTakenObserver` (thefts); the purses `PlayerState.Money`, `Squad.Money` |
| One character recruited | `PlayerInterface::recruit(Character*, bool)` | 0x693010 | 0x692820 | no observer; the dialogue system's DA_JOIN_SQUAD_WITH_EDIT / DA_JOIN_SQUAD_FAST |
| Changes faction | `Character::setFaction(Faction*, ActivePlatoon*)` | 0x5CB030 | 0x5CBAC0 | no observer; `CharacterCold.Faction`; a mod goal's `End` says `Order` |
| A knock-out | `MedicalSystem::knockout(float)` | 0x643EF0 | 0x644980 | no observer; `MedicalState.Unconscious` |
| First aid gives XP | `CharStats::xpFirstAid(Character*, float, StatsEnumerated)` | 0x8C5260 | 0x8C6130 | `IExperienceFilter.Gain` |

(1) Three functions with identical code stand together there; this one is placed by the shift of the functions around it. The
other 17 rows are single matches.
