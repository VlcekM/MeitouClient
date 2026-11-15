# Input and controls

Original OIS/GUI/action dispatch research, controls.cfg and exe byte/name-table checks. Source
scopes/action tables below. Meitou `InputState`/`InputBindings` consume presses once per control
tick; host picking/UI issue commands, not direct simulation mutation. [Simulation](../simulation.md#player).

## Binding encoding

**Verified** code=scancode | Shift0x100 | Ctrl0x200 | Alt0x400. Low-byte OIS scancode; left/right
modifiers share bits. Low-byte0 mouse=(buttonIndex+1)*4096 (Mouse1/2/3=0x1000/2000/3000).
Name formatter prefixes Ctrl+Alt+Shift, unnamed Key_n, mouse Mouse<n> (`Mouse1` in the install file). Key names are a256-entry
exe table;111 null. Preserve scancode semantics rather than substituting ASCII key codes.
Controls.cfg (install root) first line version1, repeated action=KeyName entries allow multiple bindings.
Missing/empty file writes defaults; version<1 treats changed camera/floor/build bindings specially
(exact application partly Observed). Default-unbound and UI-unrebindable actions remain in tables.
**Verified** (`FUN_140362430`, `FUN_1403622f0`): an action's first line clears its default bindings, each line then adds one, so an
action the file does not name keeps its defaults. Key names (exe table, **Verified** against the strings and the install's file):
letters, digits and punctuation are the character, `F1`..`F15`, `NUM0`..`NUM9` `NUM+` `NUM-` `NUM*` `NUM/` `NUM.`, `Left Control` `Right Control`
`Left Shift` `Right shift` `Left Alt` `Right Alt`, `Return` (main Enter), `Enter` (keypad, by its place in the table: **Observed**), `GRAVE`, `SYSRQ`, `PgUp` `PgDn`, `Mouse1..3`.

Meitou (`GameControls`, `InputBindings.UseGameControls`) reads the file at boot, never writes it, as the defaults of the actions it
names (the same rule; the version line, modifier names and the build/editor/developer actions are skipped; `camera_tilt+` is `PitchDown`,
steeper). Medic, Rescue and Highlight get Meitou's H, J and Right Alt back while no other action has them. Its config keeps only the
bindings that differ from those defaults; in a config that saved every action (older builds) the entries equal to the engine's own
defaults are ignored.
Restore defaults returns to controls.cfg.

## Dispatch and selection

Window/OIS state, modifier composition, binding lookup, action event and GUI/pointer mode are
separate stages. UI consumption/pause/loading/editor state can block commands. Press/release,
held movement, click/drag, double-click, floor/camera and context verbs have separate handlers;
do not trigger game actions solely from a matching key. Selection ownership and inspection differ:
inspecting non-player characters updates panels without giving orders to them. Context menus
choose verbs based on target type/state, faction, carrying and available interaction.

Meitou selection commands retain the current selection when no valid player characters are
targeted (empty ground or an empty drag box). Selecting valid members replaces or adds to it;
inspecting another faction keeps the selection and shows that character in the panels (see [UI screens](ui-screens.md#4-hud-mainbargui)).
While a selected character sneaks, an NPC that is up gets the verb "Stealth KO" with its chance beside it ([context menu](#context-menu-verbs)) and a quick right click on it gives
that instead of an attack ([combat](combat.md#stealth-knock-out)); the first sneaking selected character goes.

Original strategy camera actions/zoom flags are in [camera](../formats/camera.md). Engine defaults:
Space pause,F2/F3/F4=1/2/5,F5 save,F9 load; Tab is the original's change_squad (the settings open from the Esc menu, ToggleSettings is unbound by default); current complete keys in [viewer](../viewer.md)
and [engine](../engine.md). User bindings and viewer/cinematic bindings have separate lifetimes.
Settings-tab labels/type/ranges/restart semantics: [settings](../formats/settings.md).

### 1. Pipeline — layout/example

```
OIS keyboard/mouse -> MainListener (keyPressed/mouseMoved/mousePressed)
   -> inject into MyGUI (always)
   -> binding table (FUN_140360b30) unless a text field / key capture owns the keyboard
        -> sets held-state flags (polled by camera, selection code)
        -> appends event ids to a per-frame queue
   -> GameLoop_InputActions (FUN_140788100) drains the queue: MainBar hotkeys first, then game actions
   -> frameEnded (FUN_14082b6e0) clears the pressed flags of event actions and the queue (FUN_140360a80);
      held flags are cleared by the release handler (FUN_140360da0), which looks the released code up again
```

### 4. Action table

| Action | Default | Event id | Kind |
|---|---|---|---|
| mouse_select | Mouse1 | 0 | held |
| mouse_command | Mouse2 | 0 | held |
| mouse_rotate | Left Control (0x1d) or Mouse3 (two alternative bindings, not a chord; Right Control 0x9d is only a modifier, not a binding, **Verified** `FUN_1403636c0`, `FUN_140360b30`) | 0 | held |
| camera_forward / back / left / right | Up+W / Down+S / Left+A / Right+D | 0 | held |
| camera_tilt+ / tilt- | `,` / `.` | 0 | held |
| camera_rotate_left / right | Q / E | 0 | held |
| camera_zoom_in / out | Home / End | 0 | held |
| highlight | Left Alt | 0 | held |
| toggle_fps_camera | `;` | 0x28 | event |
| toggle_camera_grid | Shift+`;` | 0x29 | event; toggles the white rule-of-thirds widget "CameraGrid" (`FUN_1406e5fc0` mode 2, **Verified**); Meitou draws it under the interface, Shift+`;` fixed (a binding has no modifier) |
| floor_up / floor_down | PgUp / PgDn | 0x24 / 0x25 | event |
| speed_1 / 2 / 3 | F2 / F3 / F4 | 0x13 / 0x14 / 0x15 | event |
| stop_movement | R | 0x16 | event |
| pause | Space | 0x12 | event |
| toggle_block / hold / passive / ranged / sneak | NUM0 / NUM1 / NUM2 / NUM3 / NUM4 | 0x17 / 0x18 / 0x19 / 0x1b / 0x1c | event |
| toggle_taunt / cycle_run_speed | NUM5 / NUM6 | 0x1d / 0x1e | event |
| medic / rescue | NUM7 / NUM8 | 0x1f / 0x20 | event |
| toggle_jobs / prospect | unbound | 0x1a / 0x21 | event |
| screenshot | SYSRQ, F8 | 0x10 | event |
| toggle_bar | F7 | 8 | event |
| toggle_inventory / stats / map | I / C / M | 1 / 2 / 3 | event |
| toggle_faction / research / crafting | unbound / T / Y | 4 / 5 / 6 | event |
| toggle_help | F1 | 7 | event |
| quicksave / quickload | F5 / F9 | 0xe / 0xf | event |
| toggle_build | B | 10 | event |
| focus_char | F | 0x11 | event (accepts Shift) |
| select_0..8 / select_9 | keys 1..9 / key 0 | 100..109 | event (accepts Shift) |
| select_all | GRAVE | 0x22 | event (accepts Shift) |
| change_squad | Tab | 0x23 | event |
| character_next / prev | `]` / `[` | 0x26 / 0x27 | event |
| build_undo / build_apply | Backspace / Space | 0xb / 0xc | event, build map |
| build_rotate_left / right | `,` / `.` | - | build map |
| build_move_up / down | `=` / `-` (NUM+ / NUM- registered too, overwritten by the tilt pair) | - | build map |
| build_tilt_increase / decrease | `]` / `[` and NUM+ / NUM- (**Verified**: `FUN_1403636c0` registers all four with second keys 0x4e / 0x4a, move first, tilt after; `FUN_140362260` stores key to binding in a `std::map` with assignment, so the later tilt registration wins) | - | build map |
| editor_delete | Delete | - | event |
| reload_biomes | Ctrl+F6 | 900 | event; reloads textures / materials of loaded GameData of type 0x1c (`FUN_140a0a260`), then per zone map `FUN_1409f9560`; developer tool, not built |
| gizmo_move / rotate / scale | H / J / K | 0x398 / 0x399 / 0x39a | event; with an active object `FUN_14096d680` / `FUN_140970880(mode)` (functions not read); developer tools, not built |

Meitou's game view (`GameHotkeys`, `InputBindings`): every event action above is an `InputAction` with the table's default
(select_0..9 are `SelectMember1..10`), rebindable on the settings' Controls page; Medic and Rescue also keep H and J, and Meitou adds Rest (L)
Put Down (K) and Photo Mode (P, [viewer](../viewer.md#photo-mode)). `toggle_help` (F1, `InputAction.ToggleHelp`) opens or closes the Tutorials window (event 7, section 6). A numpad key bound to one of these
actions is the game's, not the cinema camera's ([viewer](../viewer.md#cinematic-camera)), unless Ctrl is held or the cinema's editor is open;
`--duel` keeps the whole numpad for the cinema. The orders hotkeys are not given from the free camera (engine choice).

### 6. Event dispatch

| Event | Effect (**Observed**) |
|---|---|
| 1 | toggle the inventory window of the lead (FUN_140724980 -> FUN_1406e6f10, which closes it when open; also the INV button's click, FUN_14072d3b0). **Verified**: nothing happens, opening or closing, unless the lead hand holds a character that resolves, is not dead (`+0x5bc`) and belongs to the player (its faction's `+0x250` set). Target window inferred from the action name `toggle_inventory` |
| 2 | open or, when already open, close the per-character window for the selection (FUN_1406e5f10; stats window inferred from `toggle_stats`) |
| 3 / 4 / 5 / 6 | request Overview tab Map / Faction / Research / Crafting (HUD field +0xd0 = 0/1/2/3) |
| 8 | not in the hotkey function: handled in the action switch by FUN_1406e2a20, which flips the GUI-hidden flag at GUI+0x330 ("toggle whole GUI") |

| Input | Effect |
|---|---|
| right click on a character with something to bandage (own or not, as the menu's First Aid; the quick click only on the player's own) | the selected characters treat it (`TreatOrder`); a lone selected character on itself treats itself |
| NUM7, `H` | Medic for the selection: each looks for a patient once; with Shift the medic job (58) is added to their lists, and robotics (57) when the active squad has a robot (above) |
| `L` | the selection rests (sits down; **no binding in the original**); any move order or `R` ends it |

| Input | Effect |
|---|---|
| Ctrl + right click on a knocked-out or dead character | the nearest selected character picks it up (`PickUpOrder`); on a body a selected character carries, puts it down (`PutDownOrder`) |
| right click on one of the player's own downed characters with nothing to bandage | picks it up ([quick right click](#quick-right-click-on-a-character)) |
| Alt + right click on a knocked-out or dead character | the nearest selected character takes all that fits from its inventory and backpack (`LootOrder`; no window) |
| left press on an item in an item window | lifts it onto the mouse (one unit of a stack; Shift or Ctrl: the whole stack); let go after a drag, or pressed again, it is placed, merged, swapped, given to the character under the pointer or dropped on the ground ([holding an item](ui-screens.md#6-inventory-trade-dialogue-build)); right click or Escape puts it back |
| right click on an item in an open trade window | buys one unit of a stock item, or sells one unit of the customer's own item (Shift: the whole stack as far as money and room go); see [ui-screens](ui-screens.md#6-inventory-trade-dialogue-build) |
| right click on an item with only the character's own windows open | a map item is read; money becomes cats (also money taken out of a loot by a right click or a take-all, as when it is dropped into the player's window; Meitou `CarrySystem.TakeLoot`); otherwise equipped, taken off, fitted on its stump or moved between the grid and the open bag (`QuickMoveItem`, [ui-screens](ui-screens.md#6-inventory-trade-dialogue-build)) |
| NUM8, `J` | Rescue for the selection: each looks for a body once; with Shift the rescue job (148) is added |
| `K` | the selected carriers put their bodies down |

### Movement marker

**Observed** (owner's base-game example): right-clicking a building targets its floor rather than terrain behind it.
Meitou movement picks loaded buildings' floor/stair collision up to the viewed floor before terrain; wall hits drop to the floor below
(up to 4 units across wall thickness). The flat downward faces of box/capsule/convex shapes (wound outward) are no floor: **Verified** (scratch ray probe,
Stationhouse `Storm_House_08_BASE_COL.xml` / `_Exterior_COL_Steps.xml`) rays clipping the foundation's or steps' sloped side dropped onto their undersides
(Y −60 / −74, floor top 15.5); triangle-mesh faces keep the file's winding (unknown, 82 of 435 building collision files), so either side counts there. Clicks, queued moves and held-button steering share this destination and marker.
A terrain hit that a building's drawn interior mask cuts (the renderer's rule, `InteriorVolume.CutsGround`; [interior cut](../render-interior-cut.md)) does not stop that pick: it looks on to where the ray leaves the room's box (`CutGroundReach`; the build ghost uses the same reach). Engine choice. **Verified** (scratch probe, feedback #712, Admag Watchtower `12.35:612-rebirth-INGAME`): its floor 0 lies at 1583-1589 under terrain at 1588-1630, so camera rays met the terrain first and a bed's ghost never reached floor 0; the game was not run.
Buildings without a readable interior mask/floor collision still use terrain; the original's pick filters: [building picking](#building-picking-and-selection).
Outside the rooms, the ray's first descent onto a kept navmesh surface more than 5 units above the terrain (a wall top, ramp, deck) wins
over the terrain, and its height is the goal's floor (engine choice). "Outside the rooms" means not under the building's collision
(`InteriorVolume.Covered`): an open deck or roof walk within a hull's height is picked too, except in an opened building where its floor is above the viewed one (hidden).
**Observed** (user, screenshots of the original): a click on the Longhouse's roof end at viewed floor 0 or 3, the character outside, walks onto it (floor 1). **Observed** (JC2, zone 20.29, scratch probe): a ray onto Defensive
Wall II `16-Newwworld-INGAME` gave Y 1218 (terrain 1103); before, the goal resolved to the blocked ground under the wall.

**Spread** (**Verified**, order 0x1d in `FUN_1407f9e20`): `srand(selection count)`; the selection's lead goes to the point, every other character to point + (sin a, 0, cos a) x r with r = u x √(60 x count) + 5 (`DAT_141685340`), then a = u x 2π, each u = rand() / 32768 (`FUN_1409b1c10`, `DAT_141742b48`). Meitou: `Formation.Scatter` (the MSVC generator replayed, lead = first in the order's list).

**Observed** (decompile): a ground move order shows an entity of `position.mesh` (a downward cone over a flat ring, 0.29 × 1.56 units)
scaled 8 on its scene node, at the ray's ground point (the terrain-click handler FUN_140800250, show FUN_1407f2660). Built in FUN_1407f9530:
a clone (`movemarker`) of `debug_marker`, pass 0 set to alpha blending (`setSceneBlending(0)`, SBT_TRANSPARENT_ALPHA). Each frame FUN_1407f92d0 sets the fragment constant
`colour` of pass 0 to (0.612, 0.694, 0.529, t) and of pass 1 to (0.306, 0.347, 0.2645, 0.6 t), then lowers t (1 at the click) by the scaled
frame time `DAT_142133794` ([game-loop.md](game-loop.md#the-three-time-values): 1 s of game time, 0.01 a frame at speed 0) and hides it at
t ≤ 0. Shown only with `Movement marker` ([settings.md](../formats/settings.md); global `DAT_142133511`, `Rotation marker` is `DAT_142133510`).
The rotation marker shows while the camera rotate button is held and the option is on, scaled max(3, -z x 0.06) by the zoom node's z (camera update `FUN_1406b0f90`, **Verified**).
It is an entity of `data/meshes/rotate.mesh` (made once by `FUN_1406b1480`, **Verified**; `CameraClass::rotationMarker`, KenshiLib 1ce4a16): a flat ring, bounds (-0.456, -0.013, -0.423) to
(0.461, 0.012, 0.423) (**Verified**, the mesh's bounds chunk), on a scene node that is a child of the camera's `center` node (the pivot, which carries the pitch) at local (0, 1, 0)
with `setInheritOrientation(false)`, so it lies flat in world axes about 1 unit above the pivot. Material: `UtilityT::getDebugMaterial` (`FUN_1409b7830`) with 0x9cb187 and its keep-pass-1
flag: a clone `debug_#9cb187X` of `debug_marker` with fragment `colour` set from ARGB 0x009cb187 on both passes, i.e. (0.612, 0.694, 0.529) with alpha 0, so the alpha-blended
behind-the-scene pass 1 adds nothing (**Observed** reading) and the visible part is the opaque pass 0.
**Verified** against `data/materials/debug.material` and `materials/forward/basic.program` / `basic.hlsl`: `debug_marker` pass 0 is
`depth_func less_equal`, pass 1 `depth_write off`, `depth_func greater`, `alpha_blend` (the part behind the scene); both `Basic_Lit_VP`
(`NORMALS`: `rgb *= saturate(N·sun) * contrast + 1 - contrast`, contrast 0.5) and `Basic_FP` (`colour × vertex colour`).
Meitou (`MarkerRenderer`) draws both passes after the tone map, so the colours show as given whatever the exposure, sorting fragments by
the scene depth, multisampled (up to 8×) and resolved over the picture: the mesh's rough band at the cone's widest part
and the notch on the stem are in `position.mesh` itself; on every ground move, following the cursor each frame while the button steers, gated by `Movement marker` (**Verified** `FUN_1407f2660`, `FUN_140800250` test `DAT_142133511` where they show it; settings.cfg when unset, then the Interface page's "Show movement marker"). The rotation marker is drawn in the visible pass only, while the Orbit binding is held, at the drawn camera's target + (0, 1, 0), scaled max(3, 0.06 x its distance) (the zoom node's z read as the boom), gated by `Rotation marker` (**Verified** `FUN_1406b0f90` tests `DAT_142133510`; "Show rotation marker"). Placed at draw time like the original's child of the pivot node, it keeps with a followed character, whose drawn position moves the pivot every frame; not under the title or the character editor (engine choice).

### Quick right click on a character

**Verified** (decompile of the hover and click handler `FUN_1407fb410`, called by the pick `FUN_140800250` for every character hit; helpers by their decompile, names from KenshiLib 1ce4a16). The pick
takes the nearest character the ray meets and that decides: a character hit counts as handled, so a quick right click on one gives its order or **nothing, and never a move**. The handler sets the
pointer every frame; the order goes out on the command release (`+0x2f3`) before the context menu's hold. The **lead** is the selected character nearest to the target (`FUN_1407f4ba0`; with
nothing selected the player faction's character nearest to it, `FUN_1407f2f70`: the pointer shows, no order is given). With only animals selected (`+0x268`) the click does nothing. Left press: select (`FUN_1407f9000`).
"Down" is vfunc `0x30` (`FUN_1405c9e10`): knocked out (`+0x5b9`), dead (`+0x5bc`), carried (`+0x3d4`), held by a limb or fight state (`FUN_1407d1440`) or crawling (vfunc `0x368` at least 3). Orders to **all** selected:
5 (with 16 queued after it), 25, 60, 235; the rest go to the **nearest selected** one that is not down and not an animal (`FUN_1407fae70`). A body carried by a character of another faction than the player's: nothing, whatever it is.

| Target (not carried by a non-player) | Pointer | Order |
|---|---|---|
| **Own**, dead (`+0x5bc`) | `search` | 26 loot |
| own, organic need score above 0.01 (medical `+0xac`, [first aid need](character-stats.md#first-aid-need-score)) | `med` | 25 first aid (job 58 with Shift) |
| own, else robot need above 0.01 (`+0xa8`) | `med` | 60 robot repair (job 57) |
| own, else not down | none | none |
| own, down, not carried (not the lead itself; the race's `carriable` flag `+0x7a` set) | `lift` | 68 lift |
| own, carried by the lead | `lift` | 69 put down |
| own, carried by another | none | none |
| **Hostile** (`FUN_14079cbe0`, below), down | `search` | 26 |
| hostile, standing, the lead sneaking (`FUN_1405c9750`) and the target not an animal: a slave (state 1) the lead is not an enemy of | `scavenge` | 26 |
| hostile, standing, lead sneaking, else | `knockout` | 228 stealth KO |
| hostile, standing, not sneaking, imprisoned (`+0x2f8` = 2), or fighting (`isInCombatMode`), or no talk | imprisoned `search`, else `attk` | 26, else 5 + 16 (235 for a lead on a turret) |
| hostile, standing, not sneaking, not fighting, with a dialogue to give (`FUN_14067aa50`) and a campaign force's member (`FUN_140796240`) or a slave (`FUN_14067ac20`) | `s_talk` (8) while it holds a line for the player (Dialogue `+0xc8` = KenshiLib `playerInterruptionDialog`, **Verified**), else `talk` (7) | 12 |
| **Other**, down (also carried by a player): bandit or slave class (`+0xdc` 8 / 6) or a not-real faction | `search` | 26 |
| other, down, else | `scavenge` | 26 |
| other, standing, the lead sneaking: imprisoned | `lockpick` | 76 then 201 |
| other, standing, lead sneaking: a slave the lead is not an enemy of | `scavenge` | 26 |
| other, standing, lead sneaking, else | `knockout` | 228 |
| other, standing, not sneaking (or an animal), with a dialogue (`+0x280`, `FUN_14067aa50`) | `s_talk` / `talk` as above | 12 |
| other, standing, no dialogue | none (`green` when the target regards the lead (the nearest selected) as friendly, vfunc `0x3f0` called on the target with the lead as argument, `FUN_1407fb410` **Verified**: it is set first and stays) | none |

**Hostile** (`FUN_14079cbe0`, **Verified**): never a character of the player's faction; the squad memory of the target holds the long-term enemy tag (4) on the active squad's leader; or it is targeting a
player's character (its target handle `+0x650` AI vfunc `0x28`, or the second target handle (status object `+0x28`) while the float `+0xa0` beside it is above 0.1); or among the sensed ones it considers enemies there
is one of the player's (sense bit 13, not ally, neutral or knocked out); else the player faction's relation to its faction is at most -30 (`FUN_1406b25d0`). Without a squad the answer is no.
Sense-tag bits (**Verified**, the builder `FUN_1408580d0`): 0 ally (not enemy, friendly), 1 neutral, 2 enemy, 3 knocked out (carried or down), 4 dead, 5 / 6 slave state 1 / 2, 7 class 1 or 2, 9 prisoner,
10 the looker's faction, 11 robot race, 12 the looker's current target, 13 the player's faction, 14 crawling, 15 carried. `FUN_140796240(c)` (**Verified**): the character's platoon is a campaign force of its faction (`Faction+0x88` is the `FactionWarMgr`, its `forces` map at `+0x30` is looked up by platoon, `FUN_140283500`; with the flag and NPC class 5 also a target-town list `+0x88`).
`FUN_14067ac20`: the dialogue exists and the character is a slave (status +0 = 1) with something to say.
**Order 12** (`Task_PlayerTalkto`, init `FUN_14034c5a0`, update `FUN_14034c7c0`, **Verified**): at once the player character's Dialogue fires the task's event (`TaskData +0x264`, default 1 PLAYER_TALK_TO_ME; an event 9 becomes 0x11 for an enemy target, `FUN_140335e00`) at the target (`FUN_140684990`); the update walks to the target (vfunc `0x3b0`) and the task ends once a conversation is open (`FUN_140671c60`). A shopkeeper's trade window is the reply DA_TRADE of that conversation ([economy](economy.md)): **order 12 on a trader walks over and talks, it does not open the trade at once.** The order check (`FUN_1405d1940`, **Verified**) refuses order 12 for an ordered character whose record says `dumb` (`FUN_1405e89c0`) with the message "{1} can't talk to people" (`TalkOrder` posts it and does nothing else).

Meitou (`PlayerInterface.QuickOrderFor`): the same table, with these reductions: `carriable` is not read; a campaign force's member is `CharacterSnapshot.WarParty` (platoon with a `Campaign`); `s_talk` is `World.WantsToTalk` (the dialogue system's `interrupts`); the lead's enemy test for a slave is the plain hostility test; a trader is talked to (`TalkOrder`; `OpenTrade` only when its keeper has no line to say). Hostility (`Hostile`): the faction hostile to the player's, or fighting a player's character. Ctrl / Alt over a downed character stay
Meitou extras (the original has none; Alt there is Highlight).

### Mouse pointers

**Verified** (read `data/gui/pointer/kenshi_pointers.xml`, `images/kenshi_images.xml`): 25 `ResourceImageSetPointer`s (default `arrow`, layer
`Pointer`, the last in `core_layer.xml`), each naming a `Size`, a hot spot `Point` (pixels from its top left) and an image set whose one frame
(group and index `Pointer`) is a crop of `Kenshi_UI.png`. An entry with an empty name and no values also exists. Some frames differ from the
pointer's `Size` (`lift` 34×30 in 35×35, `hand` 33×35 in 32×32, `pick_item` 32×32 in 35×35). **Verified** (MyGUI `ResourceImageSetPointer::setPosition`, read on GitHub): the image box is `Size` large at mouse − `Point`, so the frame is stretched to `Size`; screen pixels (no UI scale).

Meitou (`PointerView`, engine choices): the window's own cursor is hidden and the pointer drawn last in the frame (not in
screenshots or cinema takes). It follows the quick right-click order, so it never disagrees with the click: with a selection, over a
the [quick right click's table](#quick-right-click-on-a-character) (pointer given also without a selection, the lead then being the nearest of the player's characters): `talk`, `attk`, `knockout`, `med`, `lift`, `search`, `scavenge`, `lockpick`, `green`; a character with no order shows `arrow` with a white outline and a right click does nothing (hostility: [the table](#quick-right-click-on-a-character)), else a loose item (`pick_item`, or `steal` over another faction's item; a right click picks it up, without Alt only the hint and a walk to a stolen one: [ground items](../simulation.md)); Ctrl over a downed character
`lift`, Alt `search`; otherwise, over the interface, the settings panel or the loading screen, `arrow`. The free camera hides it (as mode 0's
`invisible`). Interface → Hardware mouse (`HardwareMouse` in meitou.user.json, default off as the game's `hardware_mouse`; settings.cfg is
not read) shows the Windows cursor instead, applied at once. Under it, shown only while Hardware mouse is on, Enhanced hardware mouse
(`EnhancedHardwareMouse`, default on; Meitou only) draws the pointer for the order beside the Windows cursor, its left edge 14 px at 1080 right of the
mouse and its middle 14 px below it, by the arrow's lower half (none for `arrow`: the walk, the interface, no order), and turns the cursor into the system hand over an enabled button
(`Button` or a control with an action), a settings-panel widget, or an order pointer other than `hand` and `invalidmove`. With frame generation the
beside icon is part of the interface, so it trails the cursor by a held-back frame.

### Pointer chance label

**Verified** (decompile of `FUN_1406e4820`, `FUN_1406ea070`, `FUN_1406e4f50`): the pointer setter `FUN_1406e4820` also keeps a lead and a target handle (GUI manager
`+0x2f0` / `+0x310`) and raises a flag (`+0x2e8`) that the plain setter `FUN_1406e3d60` clears, so any other pointer hides the label. Its callers: the character
hover `FUN_1407fb410` with `knockout` (0x19; the lead is the nearest selected, no label with nothing selected) and `lockpick` (0xf, a prisoner), and the building
hover `FUN_1407fc6e0` with `lockpick`. The GUI manager's frame update `FUN_1406ea070` (from the frame loop `FUN_140788a00`) shows, while the flag is set, a
DatapanelGUI on the `Popup` layer with one row, empty key and value `{1,num}%` = int(f x 100):
- `knockout`: f = the stealth KO chance (`FUN_1408852f0(lead, target, 1)`, [combat](combat.md#stealth-knock-out)) x 0.01, clamped to 0..1.
- `lockpick`: f = the lock's pick chance with the lead's lockpicking, clamped to 0..1; the minimum lockpick chance is not applied (only the verb list `FUN_1407f65e0` does).

Meitou (`PlayerInterface.AddPointerChance`): the `knockout` label, from the pointed character's published `StealthKoChance` (the selection's first sneaking
member, not the nearest); the `lockpick` label, from `Lockpicking.Chance` of the selected character nearest the lock (a caged character's cage, else the
container, else the door under the mouse). A floating tooltip 32 px at 1080 above the mouse, recomputed every frame (engine choices); drawn with the Hardware mouse too.

### Squad bar portraits

**Verified** (`FUN_140415e00` hooks the ItemBox of each squad tab to `MainBarGUI`; disassembly of `FUN_140413d80`, the eventNotifyItem handler, and `FUN_140413d30`, eventSelectItemAccept):
- Left press (notify 0, button 0): closes the context menu (`FUN_1407a6bc0`), selects the member (`FUN_1407f90b0` -> `FUN_1407f8f60`, Shift toggles, [above](#quick-right-click-on-a-character)), no camera move.
- Double click (accept, `FUN_140413d30`): the same with the focus flag set, which runs `FUN_1407f5400` (focus the camera on the character).
- **Right** press (button 1): closes the context menu, remembers the pressed index, copies the portrait's character hand into `MainBarGUI` `+0x1e8` and zeroes the hold timer `+0x208`.
- Right release over the same portrait with the hand still set (a press shorter than 0.25 s) calls `FUN_140413cd0` with the focus flag set: the same select-and-focus as the double click.
  After the menu opened, the release does not focus. No right-click menu on any other path of the bar.
- Meitou (`PlayerInterface.PortraitRightDown` / `MouseUp`): a press held `PortraitHoldSeconds` (0.25) opens the member's context menu; a quicker right release on the pressed portrait selects it
  (Shift toggles) and centres / flies the camera as the double click. With no verbs (nothing selected) no menu opens and the release focuses.

### Ground item labels

| state | plate | text |
|---|---|---|
| stealing, hovered | BackSelectedStealing | TextSelectedStealing |
| stealing | BackSelectedStealing | item colour |
| hovered | BackSelected | TextSelected |
| otherwise | item colour | item colour |

### Building picking and selection

The original's mouse code, kenshi_x64.exe 1.0.68. The panels a selected building fills: [ui-screens](ui-screens.md#selected-building).

**Buttons** (**Verified**: Controls `DAT_142133370`, actions registered in `FUN_1403636c0`): held flags `mouse_select` +0xf3,
`mouse_command` +0xf4, `highlight` (Left Alt) +0xf2 (`DAT_142133462`, the flag the steal and pick-lock tests read, so they follow the highlight binding, not Alt). Each frame `FUN_140801630` stores edges on PlayerInterface (**Observed**): +0x2f1 select
released, +0x2f2 select pressed, +0x2f3 command released, +0x2f4 command pressed; the two command edges are cleared while the context menu
is visible or was last frame. Then `FUN_140800f10` (drag box) and, unless a box is being dragged, the pick `FUN_140800250` run every frame, so
hover and clicks share one pick.

**Ray** (**Observed**, `FUN_1409b3910`, `FUN_1409b37a0`): a PhysX raycast through all shapes from the camera through the cursor, group mask
`0xffffde7f` (every [collision group](../formats/collision.md#collision-groups-observed-fun_14055f6e0-and-the-loaders) except 7, 8 and 13, the
interior-mask hulls); the hits are sorted nearest first (**Verified**, comparator `FUN_1409b1820` by disassembly: ascending distance at hit
+0x24). Buildings are therefore picked by their collision shapes; a part without collision cannot be clicked. The walk over the hits
(`FUN_140800250`, **Observed**) stops at the first decisive one:

| Hit groups | Result |
|---|---|
| 2, 6, 14 (terrain, foliage) | ground: ends the walk |
| 0, 1, 3, 4 (characters, ragdolls) | that character if visible (`FUN_1407fb410`); behind an already taken building only when the building hit was a passable part (23..26) |
| 3, 4, 28..31 with an item object | that item if visible (`FUN_1407fc440`); behind a taken building only when inside the building's bounding box |
| 5 (doors) | the door, by its collision (group 5, on the door's own node, [collision](../formats/collision.md#placement-and-groups)); it ends the walk with no visibility or floor test of its own (**Verified**, decompile: the group-5 arm takes the hit's object, `FUN_1409f8f20`, and stops). When a building was taken first, that building. Meitou: the same, by the door's collision with the leaf where it has slid to (`PickDoorAt`, `NavDoorGeometry.Shape`) |
| 9..12, 15..31 (building parts) | building candidate, rules below |

- Characters and ground items on a hidden floor are not visible either (**Verified**, decompile): a character in a building above the floor it shows (`FUN_1405c9f60`); a ground
  item above floor 0 above the floor its building shows, or above the current floor outside one (`FUN_1409fb540`, after each floor change in `FUN_1407f7230`). Meitou: neither
  drawn nor picked above the viewed floor of a building in the interior view (characters `GameHost.hiddenIndoors`; items `GameHost.hiddenGround`, which the renderer takes as its `HiddenItems`), a
  ground item's floor from where it is drawn.
- Parts of a building with an interior (`FUN_140291650`) count only when their floor is not above the floor that building shows; the floor of a
  hit comes from its group (`FUN_1409b1850`: 10/16/20/24/29 → 1, 11/17/21/25/30 → 2, 12/18/22/26/31 → 3, 27 roof → 4, else 0). So with a
  floor cut the ray passes the hidden roof and floors and reaches what is shown. When the viewed floor (PlayerInterface+0x1e4, PgUp/PgDn) is
  below the hit's floor, the walk skips ahead to the next hit on the same building at or below it. Such a hit ends the walk (**Verified**, decompile; but past a whole-shown
  interior of the player's, above), so nothing behind or below a shown floor or wall is taken. Meitou: `Hidden` leaves out what lies past one of a building in the interior view
  (`WorldObjectRenderer.ShownInteriorHit`, its floors up to the viewed one as the movement pick has them). A level building is still hit by its whole
  collision, roof included; a player site behind that hit, inside its hull, not `Hidden` and not above the viewed floor, takes the click instead (furniture in a bought house, #320).
- A turret or lamp mounted on the hit building (its slot 135 handle) met within 12 units behind the hit is taken instead (`FUN_1407f2af0`).

| Building | Pointer | Quick right click |
|---|---|---|
| not complete (+0x160, [construction](buildings-production.md#construction-state)) | `build` | 2 BUILD |
| broken (slot 97: +0x3b6 machines, +0x3ec doors, never for plain buildings; a machine is broken only while unfinished, [broken machines](buildings-production.md#power), so only doors reach it; **Verified**: the `else` arm of the complete-building branch of `FUN_1407fc6e0` sets pointer 0x13 and, on release, order 0x5f; the 90 % health spanner below is **Observed** only) | `spanner` | 95 REPAIR. Meitou: an own broken door (`PickDoorAt`, `DoorPointer`, `DoorVerbs` Repair) sends `BuildOrder` on the door ([repair](locks.md#cutting-and-breaking-285--185-cut-286--186-break)); Shift's Engineer job is not built (it works sites only) |
| door (+0x1a0) of an incomplete house (+0x368) | `build` | 2 BUILD on the house |
| otherwise | slot 130 below | slot 131 below, with the exceptions under the table |

| Class | Pointer (slot 130) | Order (slot 131) |
|---|---|---|
| Building, GatewayBuilding, WallBuilding | `arrow` | 29 MOVE_CUS_ORDERED |
| Crafting, Farm, Production, Generator, WindGenerator, RainCollector, Torture | `mine` for function 1 / 27, else `use` | 87 OPERATE_MACHINERY, 152 OPERATE_AUTOMATIC_MACHINERY when +0x3ac is 0 |
| FurnaceBuilding | as Farm | 26 LOOT_TARGET |
| ResearchBuilding | as Storage | 87 |
| TurretBuilding | as Storage | 146 USE_TURRET |
| StorageBuilding | `arrow` for function 16; own `use`; no inventory: cage `lockpick` if locked else `door_escape`, other `use`; inventory: `lockpick` if locked else `scavenge` | 26 |
| LightBuilding | `light` | as UseableStuff |
| UseableStuff | as Storage | another faction's with an inventory: 26, or when locked 76 PICK_LOCK (owner faction flag +0x1d0) else 29; by function: 2, 9, 13 → 26; 7 → 97 USE_TRAINING_DUMMY; 6, 25 → 258 USE_BED_ORDER; 8 → 76 when another faction's and locked, else 108 PUT_IN_CAGE; 11 → 136 FEED_CORPSE_INTO_MACHINE; 16 → 29; else 87 / 152 |

**Verified** (exe bytes): Building and Gateway/Wall slot 130 return 0, Building slot 131 returns 0x1d. The rest is **Observed**. Exceptions:

- A cage (its quick click is reached only when the context menu does not open at once, last point): `use` while the lead selected character
  (+0xf0) carries someone (+0x348). Another faction's empty cage: 108 PUT_IN_CAGE when carrying, else 107 USE_CAGE; the player's own cage:
  108 (slot 131). In another faction's cage holding one of yours, Alt + right click gives 76 PICK_LOCK when its lock is locked or
  207 GET_OUT_OF_CAGE_ESCAPE when not; without Alt the bar shows "Hold down the ALT key to pick the lock." / "...to escape the cage.".
- Another faction's non-cage building with a locked lock: Alt gives 76; without Alt the pick-lock message and a move to it.
- A bed's held menu: 99 "Put in Bed" when the last selected player character carries someone, else 258 "Sleep" (`FUN_1407a6bc0`, [locks](locks.md#beds)).
- No order while every selected character is unconscious ("Your selected character is unconscious", `FUN_1407f6f50`).
- Holding the command button 0.2 s on the same target opens the context menu instead (`FUN_1407a6bc0`). It opens on the press, without the
  hold, for a building whose function is 8 (BF_CAGE), or 22 (BF_DOOR) whose (first) door is locked and not the player's faction's, unless
  `DAT_142133449` (Shift held, [ai-tasks.md](ai-tasks.md)) is set (**Verified**, the tail of `FUN_140800250`: the 0.2 s `DAT_1416885d8` timer compare, function `+0x2f0` 8 or 0x16 with `FUN_1402ea220` on the door and the owner faction `+0x250` null, i.e. not a player faction). Meitou: `CageClick` / `DoorClick` open the menu on the press; with Shift the hold timer and the quick order apply.

### Context menu verbs

- **Rows**: the verbs are a set of order numbers, so each shows once, **in ascending order number**; an empty label drops it. An animals-only selection
  (PlayerInterface+0x268) keeps only 5, 31, 45, 61, 123, 226, 227 (`FUN_1407f23b0`). **Verified** (`data/gui/layout/Kenshi_ContextMenu.layout`): title
  `NameText`; per row a `Kenshi_Button1` (x 0.0208, w 0.7917 of the row) and a `Value` box (x 0.8125, w 0.1875). The box is made only when some row has a
  chance (`FUN_1407f65e0`): 76 (the lock's pick chance with the selection's lockpicking; a building's below `DAT_142133f28` shows 0), 201 (the shackles'
  lock), 228 ([stealth KO](combat.md#stealth-knock-out) chance), 246 (`FUN_140885600`); text `{1,num}%` clamped 0..100, colour (1 − p, p, 0). Without
  one the buttons span the row. The menu sits at the cursor, kept on screen. The pick chance is `FUN_140297dc0` (the lock's level against the lead's lockpicking, stat 0x25 via `FUN_140884ac0`, **Verified**) x 100, the lead the selected character nearest the target (`FUN_1407f4ba0`); a caged character as the target (`0x4c` on a character) shows it unclamped, a building's lock below the minimum 0. The pointer in mode 0xf (lockpick) shows the same number as text `{1,num}%` coloured (1 − p, p, 0), at most 1.0 before the x 100, and mode 0x19 (stealth KO) `FUN_1408852f0` x `DAT_141682ad8` (**Verified**, `FUN_1406ea070`). Meitou: `PickLockCaption` for the door, container, cage and caged-character Pick Lock rows (`DoorView.Level`, `CageSpot.Level`, `ContainerSpot.Level`, `CharacterSnapshot.LockpickSkill`); the pointer text is not built.
Stealth KO (228) is also offered on a down character (`FUN_1407a6bc0`:583-594: after 26 and the pick-up verb, while the lead sneaks and the target is no animal). The order runs the normal Task_StealthKO; on an already unconscious victim Meitou (engine reading, untraced in the original) skips the miss branch that turns the victim on the attacker and a hit re-rolls the KO time.
- **Open and close** (**Verified**, `FUN_1407a6bc0` `FUN_1407a7f10` `FUN_1407ac8c0` `FUN_140801630` decompiles; MyGUIEngine_x64.dll `WidgetInput::_riseMouseButtonReleased` disassembly): the menu opens on a held command button
  and remembers it (`ContextMenuGUI` byte +0: 1 = the command button was held at `FUN_1407a6bc0(menu, 1)`, else the select button, as for the inventory's held item menu `FUN_140415630`). Each frame
  `FUN_140801630` ends with the menu update `FUN_1407ac8c0`: **while that button is no longer held the menu hides** (`FUN_1407a6bc0(menu, 0, 0)` sets a hide flag +0x58 that empties the
  captions and takes mouse focus off the widgets). So the menu is never open without its button held: **releasing the right button closes it**, wherever the cursor is. It appears with its
  top left at the cursor (clamped to the screen, `DAT_142133474/478`), so a quick release finds the cursor on the title, which has no handler.
  The row's handler `FUN_1407a7f10` is registered at Widget +0x130 = `WidgetInput` (Widget +0x80) member +0xb0 = **eventMouseButtonReleased** (pressed is +0x118, click +0x148): a release of **any button** over a row gives its
  order (hold, drag onto a row, release; or a left press and release on a row while the right is held); it tests no button, clears the held flags (`DAT_142133464/466/46a`) and hides the menu.
  A press elsewhere does nothing (the pick `FUN_140800250` returns while the menu shows). No Esc, outside-click or other path hides it: every caller of `FUN_1407a6bc0(.., 0)` is the dispatch's end (`FUN_1407fa650`), the medic order
  `FUN_1407fac20`, the focus change `FUN_1406e91e0` (via `FUN_1407a7ef0`), an inventory item click `FUN_140413d80` and the update above.
  Meitou: `PlayerInterface.ContextMenuRelease`.
- **Click**: the order goes to the shared dispatch `FUN_1407fa650` with the menu's target, then the menu closes. The dispatch has **no Alt test**: the
  quick click's "Hold down the ALT key..." rules do not apply, so Loot on another faction's shop or container takes at once. With Shift the order becomes a
  job ([perma-jobs](ai-tasks.md#perma-jobs)); 26 with Shift on a building becomes 124 when the building has a production interface (slot 95), else
  nothing; 45, 223 and 273 are always jobs.
- **Empty**, every selected character down (slot 107): "Your selected character is unconscious" / "Your selected characters are unconscious".
- **Beds and cages**: a character in a bed or cage (+0x2f8 ≠ 0, +0x308 = 0) is replaced by its bed/cage; one with `occupant selection` (+0x3a8) opens
  the building's menu and the occupant's in a second menu beside it.

Labels (`FUN_14079b2d0`): 2 Build; 3 Pick Up, or Steal when the item's owner faction is not the player's and lacks flag +0x1d0; 5 Attack Target;
12 Talk To; 16 Attack All; 17, 45 Bodyguard; 25 First Aid; 26 Inventory (player's building), Trade (player's character), else Loot (with Shift only a building hand is special-cased: Trade on an own character adds perma-job 238 Foraging animals like Loot, `FUN_1407fa650`, **Verified**; the trader's Trade does not); 28, 65 Stand Up;
31 Follow; 60 Repairs; 61 Attack Unprovoked; 68, 213, 225 Pick Up; 69, 70 Put Down; 72 Open; 73 Close; 76 Pick Lock; 77 Lock; 78, 140 Unlock; 87 Use;
95 Repair; 96 Dismantle; 97 Training; 98, 258 Sleep; 99 Put in Bed; 107 Get in; 108 Put in; 109 Knock Out; 110 Set Free; 111 Break Out;
123, 226, 290 Smash; 124, 152 Auto-Haul; 136 Corpse Disposal; 146 Man Turret; 166 Capture; 185, 285 Use Tools; 186, 286 Use Strength;
201 Unlock Shackles; 207 Escape; 223 Throw out of home; 228 Stealth KO; 229 Stealth Kill; 235 Shoot At; 246 Kidnap; 249 Splint Injuries;
251 Escape (skill); 252 Escape (strength); 273 Throw out of town; any other none.

| Target | Verbs offered (in the builder's order) |
|---|---|
| Item (slot 5) | shackles (slot 94): 201, 185, 186; else with an inventory (slot 44): 3, 26; a plain item has no menu |
| Building | none when slot 102 (+0x1a1) is set and it is another faction's. Unfinished: 2. Broken (slot 97): 95. Else 95 when damaged (slot 101: +0x188 above +0x164), then: with a production interface (slot 95; StorageBuilding returns itself) 124 when its functionality (slot 118) is null (a storage), 152 when that has +0x3ac 0, else 87; otherwise by function (slot 94): 18 nothing, 8 cage (below), 6 or 25 with the lead carrying someone 99, else slot 131's order ([table](#building-picking-and-selection): 97 training dummy, 136 corpse machine, 146 turret, 258 bed, 26...). With an inventory: its lock locked: 76, 285, 286, else 26. A door (+0x1a0): unlocked: 77 when the player's, then 73 when open or broken else 72; locked: the player's 140, another's 76, 285, 286 and 226 (290 when slot 123 is set). Last 96 when slot 79 (the player's building, not +0xc0, not a "creates player town" one) |
| Cage (function 8) | another faction's: locked 76, 285, 286; unlocked and empty: 107 when the lead carries nobody, 108 when it carries someone (whose slot 73 is null); occupied 110. The player's: 77 or 78 by the lock, then empty 108 (as above), occupied 110 |
| Character, imprisoned | 12 when it can talk; 26 unless it is the player's and selected; its cage the player's: 207 when it is the lead, else 110 and the pick-up verb; another faction's: the pick-up verb when the cage is unlocked, 207 when it is the player's and selected, 228 when neither it nor the cage is the player's |
| Other faction's, not hostile (`FUN_14079cbe0`) | standing (not down, state 0, leg state ≠ 3), the lead not on a turret (+0x260 handle empty): 228 (lead `FUN_1405c9750`, its slot 73 null), 12, 61, 45, 31, and 225 when it is friendly to the lead (slot 126) and its AI state is not 5; on a turret 235. Otherwise: 26, the pick-up verb and 223 / 273 (`FUN_140793d80`), 228, and when not down 61, 45, 12. Then 69 when a selected character carries it |
| Hostile | standing: 12 (talk tests), not on a turret 228, 16, 5; on a turret 235. Otherwise 228, 26, the pick-up verb with 223 / 273, 5 when up in state 0. Then 69 as above |
| Every character | unless dead (+0x5bc), or up and the lead's enemy (slot 125): 60 "Repairs" (robot kit) and 25 "First Aid" (organic kit), two independent tests of `FUN_1407c05a0` with the kit kind 1 and 0 (calls in `FUN_1407a6bc0` **Verified**; the kind picks the robot / organic score, **Verified**: `FUN_1407c05a0` passes it on in its second register to `FUN_140643ca0(medical, kind)`, which returns `+0xa8` (robot) for 1 and `+0xac` (organic) for 0, [first aid need](character-stats.md); the need score `FUN_140643ca0` of the target's medical state above `DAT_141682ad8`, which Meitou reads as `AidNeedFloor`; so a robot with an organic hurt part gets both verbs, a skeleton only Repairs; Meitou: `BodyStatus.NeedsRepairs` / `NeedsOrganicAid`, one `TreatOrder` serves both kits), 249 (a splintable injury, `FUN_140646400` with 50); locked shackles: 201, 185, 186; carried by someone not of the player's: the list is emptied |

| Furniture | Native behavior checked |
| --- | --- |
| Stool, Old Stool, Sitting Box | Use, chair pose, facing the nearest table; Stop exits |
| Sitting Pillow | Use, floor-sitting pose, facing the table; Stop exits |
| Bench | Use, chair pose, table-behind facing quirk documented in [Seat facing](ai-tasks.md#town-goals); Stop exits |
| Throne, Hive Queen Throne, Outhouse | Use, chair pose and fixed placement-facing direction; Stop exits |
| Sitting spot (invisible) | No pointer Use target; the normal ground-move interaction applies |

Meitou (`FurnitureUse`, `FixtureRegistry.Seats.cs`): town chairs and thrones are derived fixture targets (not construction or render objects), picked on their collision at the furniture's placed rotation (separate from the sitter's facing); a seat with neither parts nor collision (the invisible spot) is not picked, though AI still sits there. Town and player-built seats show `use`; a quick click is order 87, holding opens Use, Shift adds the operating job, through the operator walk and settle. A seated NPC blocks the order; a seat a character sits on by an order is not claimed by NPCs. Player-built chairs face by their functionality flags towards complete qualifying furniture in the same parent building (layout tables included), else the same player outpost.

### Edit boxes and keyboard focus

- A mouse press gives key focus to the nearest widget that needs it; there is no traversal: `InputManager` handles no Tab or arrows, `Button` has no key handling and no key-focus state.
- **Static** boxes ignore every key. **ReadOnly** boxes take focus, selection and copy (Cut copies only); typing, Backspace, Delete, Enter and Paste do nothing. **Password** boxes copy and cut nothing but paste.
- Keys (Windows/Linux table; the Apple build uses Meta): Left/Right one character with **no** Ctrl word jump (3.2.2 has none; later versions do); Home/End line start/end, with Ctrl text start/end; Up/Down/PageUp/PageDown
  move by lines, so in a single-line box to the start or end; Shift extends the selection; Backspace/Delete erase the selection else one character; Enter adds a line in a multi-line box (Ctrl+Enter accepts) and accepts in any other
  (the `eventEditSelectAccept`); Ctrl+A select all, Ctrl+C / Ctrl+Insert copy, Ctrl+X / Shift+Delete cut, Ctrl+V / Shift+Insert paste, Ctrl+Z undo, Ctrl+Y redo (Shift is not looked at). With Ctrl held a key never types.
  Escape drops the key focus; Tab types a tab only with `TabPrinting`.
- Undo keeps 128 steps. A keystroke is one step; deleting a selection and inserting is one step; edits, pastes and cuts clear the redo steps, undo and copy do not; `setText` and a mode change clear both.

Meitou (`UiTextBuffer`, `MyGuiScreenHost.Keyboard.cs`, `PlayerInterface.Ui.cs`; the shortcuts above are all implemented, Ctrl on every system): the clipboard is the window's system clipboard (Silk keyboard `ClipboardText`; the boxes keep their own
without one); an empty Copy leaves it alone; pasted tabs become spaces and other control characters are dropped; the host gives a box its `MaxTextLength`, else 2048 (`UiTextBuffer` alone defaults to 512); the history is a stack of text
and caret snapshots with MyGUI's step rules. Read-only boxes can be focused by a click (caret at the end; Ctrl+A, Ctrl+C), take only edit keys, so Tab, Enter and the hotkeys work as without a focus; Esc still closes the window. Ctrl+C / Ctrl+V reach the free camera's code only
when no box is focused. A press in a one-line box places the caret at the character gap nearest the pointer and a drag selects (`UiPainter.EditCaretAt` measures the text as last drawn;
MyGUI 3.2.2 does both); in a wrapped or multi-line box the press only focuses. Not built: a password mask (no layout turns it on), `OverflowToTheLeft`, `TabPrinting`, pixel-exact Up/Down in wrapped multi-line boxes (they move by `\n` lines); a focused wrapped box (the read-only descriptions and message log) draws its caret and selection as one unwrapped line, so Ctrl+A shows a bar on the first line only.

### Dev console

Meitou only (no original counterpart: the install's `controls.cfg` and changelog name no console or debug-menu key, only `editor_toggle`, so Insert and Ctrl+Backquote are Meitou's choice; not on the macOS Metal path yet). Insert or Ctrl+Backquote opens a panel over the top of the screen (the backquote alone is the game's select-all); Esc or the same key closes it. While open it takes every key, as the debug menu does on a row with a typed field
(opening it closes the menu). The input line has a caret (Left/Right/Home/End/Delete, Ctrl+V pastes), Enter runs, Up/Down walk the history, Tab completes a command name or, for `give`, `spawn` and `tp`, a record or town name (one match is filled in whole, several as far as they agree and listed),
PageUp/PageDown scroll the last 100 output lines. Arguments with spaces are quoted. Every output line is also written to log.txt (`console  ...`). An unknown command or a bad argument prints an error line.
The target is the selected characters, or the point under the cursor.

World-changing commands enqueue `DebugCommand`s (applied in the next tick's Inputs phase), never touching the world from the UI thread; money, heal, maxstats, give, spawn, tp, tpcursor and research run the F10 menu's own code (`DebugApply`, `DebugGive`, `DebugMaxAll`), `kill` is the console's. The menu's other rows (damage, force hits, damage multiplier) have no command yet. `DevConsole` (text, history, completion and the command table of `ConsoleCommand(Name, Usage, Help, Run)` with an optional `Complete`)
is world-free, so a mod's `AddCommand` can register into it later.

| Command | Effect |
|---|---|
| `help [word]` | list the commands, or one command's usage; with no exact name, search names, usage and descriptions for the word |
| `godmode [on\|off]` | Set stats to 1000 and Auto heal together, for all player characters (no argument toggles) |
| `clear` | empty the scrollback |
| `version [--say]` | print `Meitou <tag> (<commit>)`; `--say` also has the first selected (else first living squad) character say it as a speech bubble 1 s later |
| `money <delta>` | add or (negative) take away cats, never below 0 |
| `heal` | heal the selected (else the inspected) characters fully |
| `maxstats` | every stat 100, same targets |
| `give "<item record>" [qty] [quality]` | as the menu's give: to the first selected; name matched as the menu does |
| `spawn "<character record>"` | a hostile character at the cursor |
| `tpcursor` | teleport the selected to the point under the cursor |
| `tp <x> <z>` | teleport the selected to world X Z (the camera follows the spot) |
| `tp <town name>` | teleport the selected to a placed town, name case-insensitive, exact else shortest prefix match; its position is the town's centre in the level data (`TownSite.Position`); nest markers are skipped |
| `kill` | kill the selected: `DebugKill` calls `MedicalState.Kill` (medical state dead, orders dropped), no entity is removed |
| `research` | finish every research |

## Expression reference

- Binding code encoding: `=`
- controls.cfg: `action=KeyName`
- Action table: `map[code] = action`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- Context menu verbs: [above](#context-menu-verbs)
