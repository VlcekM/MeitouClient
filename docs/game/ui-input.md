# Input and controls

Original OIS/GUI/action dispatch research, controls.cfg and exe byte/name-table checks. Source
scopes/action tables below. Meitou `InputState`/`InputBindings` consume presses once per control
tick; host picking/UI issue commands, not direct simulation mutation. [Simulation](../simulation.md#player).

## Binding encoding

**Verified** code=scancode | Shift0x100 | Ctrl0x200 | Alt0x400. Low-byte OIS scancode; left/right
modifiers share bits. Low-byte0 mouse=(buttonIndex+1)*4096 (Mouse1/2/3=0x1000/2000/3000).
Name formatter prefixes Ctrl+Alt+Shift, unnamed Key_n, mouse Mouse_n. Key names are a256-entry
exe table;111 null. Preserve scancode semantics rather than substituting ASCII key codes.
Controls.cfg first line version1, repeated action=KeyName entries allow multiple bindings.
Missing/empty file writes defaults; version<1 treats changed camera/floor/build bindings specially
(exact application partly Observed). Default-unbound and UI-unrebindable actions remain in tables.

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

Binding maps: general map at Controls+0x28, build-mode map at +0x50 (checked first while mode field +0xd4 == 1,
**Observed**). Actions with `build_` prefix use context 1.

| Action | Default | Event id | Kind |
|---|---|---|---|
| mouse_select | Mouse1 | 0 | held |
| mouse_command | Mouse2 | 0 | held |
| mouse_rotate | Left Control or Mouse3 (two alternative bindings, not a chord) | 0 | held |
| camera_forward / back / left / right | Up+W / Down+S / Left+A / Right+D | 0 | held |
| camera_tilt+ / tilt- | `,` / `.` | 0 | held |
| camera_rotate_left / right | Q / E | 0 | held |
| camera_zoom_in / out | Home / End | 0 | held |
| highlight | Left Alt | 0 | held |
| toggle_fps_camera | `;` | 0x28 | event |
| toggle_camera_grid | Shift+`;` | 0x29 | event |
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
| build_move_up / down | `=` / `-` (+ NUM+ / NUM- registered) | - | build map |
| build_tilt_increase / decrease | `]` / `[` (+ NUM+ / NUM-) | - | build map |
| editor_toggle | Shift+F12 | 9 | event |
| editor_delete | Delete | - | event |
| reload_biomes | Ctrl+F6 | 900 | event |
| rebuild_navmesh | Ctrl+Shift+F11 | 0x386 | event |
| gizmo_move / rotate / scale | H / J / K | 0x398 / 0x399 / 0x39a | event |

Meitou's build map (`PlayerInterface.Build.cs`) is live only in build mode and takes its keys before anything else: the
documented speeds, Shift as the slow modifier of the up/down move; the numpad aliases are not mapped. `[` `]` tilt there, not
character_next / prev. Left click queues the ghost.

Meitou's game view (`PlayerInterface.Hotkeys.cs`, `InputBindings`): every event action above is an `InputAction` with the table's default
(select_0..9 are `SelectMember1..10`), rebindable on the settings' Controls page; Medic and Rescue also keep H and J, and Meitou adds Rest (L)
and Put Down (K). `toggle_help` (F1, `InputAction.ToggleHelp`) opens or closes the Tutorials window (event 7, section 6). A numpad key bound to one of these
actions is the game's, not the cinema camera's ([viewer](../viewer.md#cinematic-camera)), unless Ctrl is held or the cinema's editor is open;
`--duel` keeps the whole numpad for the cinema. The orders hotkeys are not given from the free camera (engine choice).

Mouse buttons (Meitou only, **Observed**; `GameHost.PlayerButton`): `Select` (default left) and `Command` (right) are rebindable, as is `Orbit` (middle, Ctrl).
The interface and world logic keep seeing "left" and "right": a physical button bound to Select arrives as left, to Command as right; any other of left,
right, middle (e.g. Orbit's) arrives as middle (map pan, no click reaches the world under the HUD). An unbound Select / Command falls back to left / right.
The settings page itself always takes the physical buttons. Binding a mouse button already used swaps: the other action gets the rebound action's old button.

### 6. Event dispatch

GameLoop_InputActions (FUN_140788100; step 4 of [game-loop.md](game-loop.md#update-threads-and-budget)) drains the queue each frame. **Observed**: it does nothing while the loading
window is visible (FUN_1406e2100 is a visibility test on the LoadingWindow, constructor 14091a280); the Esc menu
being visible (EscMenu, constructor 1409180e0) ignores every event; two further gates suppress most game
events: a byte at PlayerInterface+0x2f0 and a visibility flag of the widget at GUI+0x20 (FUN_1406e1fd0, not
identified; pause 0x12 is only blocked by the second). While the MainBar object exists every event except the
100..120 and 0x22 group first goes to the MainBar hotkeys (FUN_1407262b0):

| Event | Effect (**Observed**) |
|---|---|
| 1 | toggle the inventory window of the lead (FUN_140724980 -> FUN_1406e6f10, which closes it when open; also the INV button's click, FUN_14072d3b0). **Verified**: nothing happens, opening or closing, unless the lead hand holds a character that resolves, is not dead (`+0x5bc`) and belongs to the player (its faction's `+0x250` set). Target window inferred from the action name `toggle_inventory` |
| 2 | open or, when already open, close the per-character window for the selection (FUN_1406e5f10; stats window inferred from `toggle_stats`) |
| 3 / 4 / 5 / 6 | request Overview tab Map / Faction / Research / Crafting (HUD field +0xd0 = 0/1/2/3) |
| 7 | toggle the visibility of the window at GUI+0x30 (FUN_1406e1f20): the Tutorials window (**Observed**: the GUI constructor `FUN_1406e4f50` stores the `Kenshi_TutorialsWindow` object of constructor `140977640` there; +0x28 holds the tutorial panel, +0x20 the object of `FUN_140725740`) |
| any other id | forwarded to the orders panel handler FUN_1407226e0 (**Observed**), which calls the panel buttons' own click handlers: 0x17 Block, 0x18 Hold, 0x19 Passive, 0x1a Jobs, 0x1b Ranged, 0x1c Sneak, 0x1d Taunt, 0x1e the right speed arrow (`FUN_140722630`, so cycle_run_speed steps up and wraps), 0x1f Medic, 0x20 Rescue, 0x21 Prospect; it ignores the rest |
| 8 | not in the hotkey function: handled in the action switch by FUN_1406e2a20, which flips the GUI-hidden flag at GUI+0x330 ("toggle whole GUI") |

Squad events (**Observed**): select_0..9 (100..109, `FUN_1407f90b0`) and select_all 0x22 (`FUN_1407f8d00`) act on the active squad's members
([squad tabs](ui-screens.md#4-hud-mainbargui)); change_squad 0x23 (`FUN_1407f6380`) with more than one squad makes the next one in order (wrapping) that has
members active, and leaves the selection alone. character_next / prev 0x26 / 0x27 (`FUN_1407f9130(+1 / -1)`, **Observed**): nothing unless the
active squad has at least two members; when the focus character (`DAT_142133a50`) is a living one of the player's faction, the next (previous) member
of its own squad, wrapping, is selected alone (`FUN_1407f9000`); otherwise the current selected character is selected alone and focused, or with no
selection the first entry of a list at PlayerInterface+0x2c0 (**Unknown** which). Meitou (`StepCharacter`): the HUD's character (the inspected one
first); dead counts as not living (the original's test `FUN_1406215b0` is **Unknown**).

Meitou's first aid and rest bindings (2026-10-13, `PlayerInterface`, engine choices; simulation side in [simulation.md](../simulation.md#bodies)). The original's own: `medic` (NUM7, event 0x1f) and `rescue` (NUM8, 0x20) are
orders-panel toggles forwarded to FUN_1407226e0 (the Medic and Rescue buttons of the panel, [ui-screens.md](ui-screens.md#4-hud-mainbargui)), which dispatch orders 58 and 148: one-shot, or a job with Shift ([perma-jobs](ai-tasks.md#perma-jobs), **Observed**); the targeted order is the context menu
verb 25 "First Aid" (pointer mode 1 `med`, [pointers](#mouse-pointers); Meitou's context menu also provides it). Meitou binds both to the numpad as the original and keeps `H` / `J` as aliases (section 4):

| Input | Effect |
|---|---|
| right click on a character with something to bandage (own or not, as the menu's First Aid; the quick click only on the player's own) | the selected characters treat it (`TreatOrder`); a lone selected character on itself treats itself |
| NUM7, `H` | Medic for the selection: each looks for a patient once; with Shift the medic job (58) is added to their lists |
| `L` | the selection rests (sits down; **no binding in the original**); any move order or `R` ends it |

| Input | Effect |
|---|---|
| Ctrl + right click on a knocked-out or dead character | the nearest selected character picks it up (`PickUpOrder`); on a body a selected character carries, puts it down (`PutDownOrder`) |
| right click on one of the player's own downed characters with nothing to bandage | picks it up ([quick right click](#quick-right-click-on-a-character)) |
| Alt + right click on a knocked-out or dead character | the nearest selected character takes all that fits from its inventory and backpack (`LootOrder`; no window) |
| right click on anyone else's knocked-out or dead character | the nearest selected walks over and its whole inventory opens in a second character window beside the inventory (worn gear in the equipment slots, its bag in a bag window): items are held with the mouse out of it or into it, onto equipment slots too (`OpenLootOrder`, [simulation](../simulation.md#carrying-rescue-and-looting); engine choice, the original's loot window is **Unknown**) |
| left press on an item in an item window | lifts it onto the mouse (one unit of a stack; Shift or Ctrl: the whole stack); let go after a drag, or pressed again, it is placed, merged, swapped, given to the character under the pointer or dropped on the ground ([holding an item](ui-screens.md#6-inventory-trade-dialogue-build)); right click or Escape puts it back |
| right click on an item in an open trade window | buys one unit of a stock item, or sells one unit of the customer's own item (Shift: the whole stack as far as money and room go); see [ui-screens](ui-screens.md#6-inventory-trade-dialogue-build) |
| right click on an item while the inventory and another item window are open | one unit moves to the other window, where it first fits (stacks of it, then free cells), in both directions; Shift: the whole stack (**Observed**, release handler `FUN_140714a20`: the one-unit move `FUN_140713d20`, repeated while Shift is held until it fails; target order in [ui-screens](ui-screens.md#6-inventory-trade-dialogue-build)). Meitou: between the lead's inventory or bag and the shown machine (its inputs and output), storage, container, or the open loot (`PutInMachine` / `TakeFromMachine` / `TakeLootItem` / `PutLootItem` with a count, `InventoryCommands.MoveInto`) |
| right click on an item with only the character's own windows open | a map item is read; money becomes cats (also money taken out of a loot by a right click or a take-all, as when it is dropped into the player's window; Meitou `CarrySystem.TakeLoot`); otherwise equipped, taken off, fitted on its stump or moved from the grid into the open bag (`QuickMoveItem`, [ui-screens](ui-screens.md#6-inventory-trade-dialogue-build)) |
| NUM8, `J` | Rescue for the selection: each looks for a body once; with Shift the rescue job (148) is added |
| `K` | the selected carriers put their bodies down |

### Movement marker

**Observed** (owner's base-game example): right-clicking a building targets its floor rather than terrain behind it.
Meitou movement picks loaded buildings' floor/stair collision up to the viewed floor before terrain; wall hits drop to the floor below
(up to 4 units across wall thickness). The flat downward faces of box/capsule/convex shapes (wound outward) are no floor: **Verified** (scratch ray probe,
Stationhouse `Storm_House_08_BASE_COL.xml` / `_Exterior_COL_Steps.xml`) rays clipping the foundation's or steps' sloped side dropped onto their undersides
(Y −60 / −74, floor top 15.5); triangle-mesh faces keep the file's winding (unknown, 82 of 435 building collision files), so either side counts there. Clicks, queued moves and held-button steering share this destination and marker.
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
the near slice's scene depth, multisampled (up to 8×) and resolved over the picture: the mesh's rough band at the cone's widest part
and the notch on the stem are in `position.mesh` itself; on every ground move, following the cursor each frame while the button steers, always on (settings.cfg is not read yet). The rotation marker is drawn in the visible pass only, while the Orbit binding is held, at the camera rig's target + (0, 1, 0), scaled max(3, 0.06 x distance) (the zoom node's z read as the rig distance), always on.

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

**Observed** (FUN_1406e3d60, which calls `PointerManager::setPointer`): pointer mode → name: 0 `arrow` (`invisible` with the free camera
and the Esc menu hidden), 1 `med`, 2 `search`, 3 `lift`, 4 `pick_item`, 5 `attk`, 7 `talk`, 8 and 10 `s_talk`, 9 `use`, 0xb `build`,
0xc `door`, 0xd `door_escape`, 0xe `lock`, 0xf `lockpick`, 0x10 `house`, 0x11 `green`, 0x12 `mine`, 0x13 `spanner`, 0x14 `light`,
0x15 `steal`, 0x16 `hand`, 0x17 `invalidmove`, 0x18 `scavenge`, 0x19 `knockout`; 6 has no case. It is called through thunk `140053acb`.
The modes a building hover sets are in [building picking](#building-picking-and-selection) (`house` 0x10 is returned by none of them); a ground
hit with no walkable point sets `invalidmove` (`FUN_140800250`). Which other hover state sets each mode is **Unknown**.

Meitou (`PlayerInterface.Pointer.cs`, engine choices): the window's own cursor is hidden and the pointer drawn last in the frame (not in
screenshots or cinema takes). It follows the quick right-click order, so it never disagrees with the click: with a selection, over a
the [quick right click's table](#quick-right-click-on-a-character) (pointer given also without a selection, the lead then being the nearest of the player's characters): `talk`, `attk`, `knockout`, `med`, `lift`, `search`, `scavenge`, `lockpick`, `green`; a character with no order shows `arrow` with a white outline and a right click does nothing (hostility: [the table](#quick-right-click-on-a-character)), else a loose item (`pick_item`, a right click picks it up: [ground items](../simulation.md)); Ctrl over a downed character
`lift`, Alt `search`; otherwise, over the interface, the settings panel or the loading screen, `arrow`. The free camera hides it (as mode 0's
`invisible`). Interface → Hardware mouse (`HardwareMouse` in meitou.user.json, default off as the game's `hardware_mouse`; settings.cfg is
not read) shows the Windows cursor instead, applied at once.

### Character picking

**Observed** (`FUN_140800250`): the original picks characters with the same physics ray as buildings ([building picking](#building-picking-and-selection)): hits in groups 0, 1,
3, 4 (3 and 4 are ragdoll shapes, [ragdolls](ragdolls.md#collision-groups); what 0 and 1 are is **Unknown**). Meitou (engine choice, [phs](../formats/phs.md)): clicks, quick orders, the hover
label, pointer and outline take the nearest character whose posed mesh the ray meets. Each mesh keeps, from its bind-pose vertices, one
box per bone (vertices by their strongest weight; an unskinned part one box), grown 0.4 units; the boxes are posed by the live bone palette,
so people and animals pick over their whole drawn body, a ragdoll where it lies. **Verified** (sandbox probe, 2026-10-16): a person hits from the feet to 18 units
up and misses at 22; a bonedog hits up to 10. A character the renderer does not draw falls back to a capsule 18 units tall.
A building seen from outside (its interior not shown, [interior masks](../formats/zones.md#interior-masks)) hides what is inside or behind it from these picks and from
ground-item picks: the ray stops where it meets the building's `interior mask` collision (engine choice, standing in for the original's building hits in the same ray).

### Building sign highlight

**Observed** (owner's original-game report): holding Alt highlights building signs in yellow.
**Observed** (merged installed game data, reader survey): BUILDING `is sign` is true on `993-gamedata.base`
(Sign Medic) and `3436-otto.mod` (sign3_bar); category `sign` alone is insufficient (`972-gamedata.base`, Weapon Sign, has the flag false).
Meitou's rebindable `Highlight` action defaults to either Alt key. While held in the focused gameplay view,
real parts whose owning BUILDING has `is sign` receive a yellow tint, including layout objects and both LODs
during a transition. Release restores their materials. Loading, menus, hidden UI, free camera and cinema suppress it.
Rendering details: [objects](../render-objects.md#building-sign-highlight).

### Squad bar portraits

**Verified** (`FUN_140415e00` hooks the ItemBox of each squad tab to `MainBarGUI`; disassembly of `FUN_140413d80`, the eventNotifyItem handler, and `FUN_140413d30`, eventSelectItemAccept):
- Left press (notify 0, button 0): closes the context menu (`FUN_1407a6bc0`), selects the member (`FUN_1407f90b0` -> `FUN_1407f8f60`, Shift toggles, [above](#quick-right-click-on-a-character)), no camera move.
- Double click (accept, `FUN_140413d30`): the same with the focus flag set, which runs `FUN_1407f5400` (focus the camera on the character).
- **Right** press (button 1): closes the context menu and remembers the pressed index; right release over the same portrait calls `FUN_140413cd0` with the focus flag set: the same
  select-and-focus as the double click, at once, whatever the hold time. **There is no portrait context menu in the original**, and no right-click menu on any other path of the bar.
- So the report in GitHub issue #137 (the base game lacks it) is wrong: vanilla focuses on a right click. Meitou (`PlayerInterface.MouseDown` / `MouseUp`): a right release on the pressed portrait selects it
  (Shift toggles) and centres / flies the camera as the double click. Meitou switch "Portrait menu" (Advanced settings, on by default; not in the original): a held right press opens the
  member's context menu, as on the character in the world; a quick release still selects and focuses.

### Ground item labels

**Observed** (owner's original-game report): holding Alt shows a small name label over each loose item.
**Verified** (`Kenshi_ItemDropLabel.layout`, `kenshi_colours.xml`): root `Kenshi_GenericTextBoxFlatSmallSkin` 0.0521 x 0.0259 of the screen, layer Back,
pointer `pick`; `ItemNameText` (`Kenshi_TextboxStandardText`, Kenshi_StandardFont_Medium) centred. Colours: `DropItemLabelBack/TextDefault` #ffffff,
`...Selected` #66ff66, `...SelectedStealing` #ff6666.
**Observed** (`ItemManager::ItemLabel`, `FUN_1401649e0` / `FUN_140162950`): the root's width is fitted to the text; a quantity above 1 is appended
(exact text **Unknown**); the label widget takes a mouse click. Range and anchor **Unknown**.
**Verified** (`FUN_1401649e0` registers three handlers on the label root; `FUN_1401638f0` is the one at Widget +0x130 = eventMouseButtonReleased, see the context menu section):
focus gained / lost only recolour (`FUN_140162a20` / `FUN_140162a90`); a **left** release (button id 0; no other button is tested, the label has no press or right-click handler) on a
label whose item is of a pickable kind (hand type 2, 3, 4, 0x2e, 0x56, 0x66, 0x6b or 0x6f) gives the pick-up order 3 through `FUN_1407fb3a0` -> `FUN_1407fae70`, the same call as the world
right click on the item (`FUN_1407fc440`). A stolen item (label flag +0xc0) without Alt held (`DAT_142133462`) shows "Hold down the ALT key to steal items" and gives no order (the world
right click gives that hint and a move, order 29/0x1d). **Unknown**: what a right click over a label does (the label takes the mouse focus, so the world handler probably does not run); whether
a left click on the item itself in the world does anything (its press is the select edge, `FUN_140800250`, not traced for items). Meitou: a left release over a label, pressed on it, picks the item up
for the selection (`PlayerInterface.LabelPickUp`); the label exists only while Highlight is held, so the steal hint cannot arise; the right click on a label stays as on the item (an extra, its source **Unknown**).
**Observed** (decompile: ctor `FUN_1401649e0`, hover `FUN_140162950`, colour `FUN_1401628e0` / `FUN_1401625c0`, colour load `FUN_140162e60`) colours, plate = root tint, text = `ItemNameText`:

| state | plate | text |
|---|---|---|
| stealing, hovered | BackSelectedStealing | TextSelectedStealing |
| stealing | BackSelectedStealing | item colour |
| hovered | BackSelected | TextSelected |
| otherwise | item colour | item colour |

Item colour: types WEAPON, ARMOUR, ITEM, CONTAINER, NEST_ITEM, MAP_ITEM, CROSSBOW, LIMB_REPLACEMENT only, else BackDefault. A plain ITEM:
`ItemArtifact` when item byte +0x180 is set (taken as the record's `artifact`, **Unknown**), else `ItemStandard`; the others by level (vfunc 0x2b8;
Meitou uses armour's quality) <16 Grade0, <36 1, <51 2, <66 3, ≤80 4, else 5. `ItemGrade5` and `TextDefault` are not in kenshi_colours.xml (exe defaults #fceb0d,
#ffffff); `ItemGrade4` #fceb0d there overrides the exe's #de9c00, `ItemStandard` #e0cccc, `ItemArtifact` #0dfceb. Stealing: the item's owner (vfunc 0x2a8 hand
unless NULL_ITEM, resolved by `FUN_14079d410`, else vfunc 0x1d8 via `FUN_140792910`) has a faction with +0x250 (player's) and +0x1d0 (`not real`) both clear.
**Observed** (`Item` vfuncs 75/76 save/load, slots 85/59): vfunc 0x2a8 is item+0x140, the save's `ownedby`; vfunc 0x1d8 is item+0x80, `insideBuilding` (only a
BUILDING handle resolves there). So an item with a null `ownedby` is never stolen, wherever it lies; the building only stands in for an owner that no longer
exists. The pick-up verb (`FUN_14079b2d0` case 3) tests `ownedby` alone: Steal when it resolves to such a faction, else Pick Up.
**Observed** (saves "Beep Fan Club 1", METARU, survey of their `1-itemlist`): 332 / 62 items have only `insideBuilding` (town interiors), 99 / 0 a CHARACTER `ownedby`,
of which 84 name a character the save no longer holds (one of them inside a building); no other `ownedby` type.
Meitou (`PlayerInterface.GroundItems.cs`): with the Highlight gating above, every item within 500 units of the camera's focus not hidden by a
building, name only, coloured as above; a label picks like its item (pointer `pick_item`, right click picks it up, held a menu with Steal or Pick Up). A click on a label is the item's alone: Meitou's Alt / Ctrl loot and lift of a downed body (an extra; in the original Alt is only Highlight) does not take it, so an item lying beside a corpse can be picked up with Alt held (**Observed**: the label is a widget that takes the click, `FUN_140162950`; whether the original prefers the item over a body behind a label is **Unknown**, nothing was traced). Overlapping
labels are stacked above the nearer one. No quantity. The owner (`GroundItem.OwnedBy`, `Saving/GroundItemSave`) is read once at load: the `ownedby` character's
platoon faction, else the `insideBuilding` building's `owner faction ID` (also for the verb, engine choice), none for a `not real` faction; items dropped or
placed in play have none. Picking up another faction's item marks it stolen from that faction (`StolenFrom`); standing inside a building it also sets a pending Theft against the building's faction ([economy](economy.md#fencing-stealing-and-smuggling)).

### Hover outline

Meitou only (Advanced → Hover outline, `HoverOutline`, default on; the game has none). The character under the cursor is drawn alone into a
mask (colour pass, live pose, picture levels, own depth planes round it) right after the portraits, then after the tone map an outline
(about 2.5 px at 1080p, 60 % strength fading outward) is blended round the silhouette, through walls, and its visible part tinted at 11 %, visible meaning its
linear depth within 1 % + 1 unit of the near slice's scene depth (jittered, render size). Outline and tint take the pointer's order's colour: attack red (a hostile character as the pointer counts it above), treat green, talk gold, carry/loot blue, else white (no selection or no order). No name or goal label is shown over a character (**Observed** by the owner in play, not traced in the exe: the original shows none for ordinary NPCs, only the outline; Meitou had a plain name and goal label until 2026-10-17). Holding the Highlight key (Alt, which also lights up signs) outlines the same way every character (the squad's too, people and animals) within 500 units of the camera's focus, each in the colour hovering it would give (own hurt green, downed or carried blue, trader or someone to talk to gold, hostile red, else white; all white with no selection); one mask per colour, overlapping outline rectangles joined. Off in the free camera, with the interface hidden and in
screenshots and cinema takes. Not in the Shift+F1 switch list (a game setting, like Attackers per target).

### Building picking and selection

The original's mouse code, kenshi_x64.exe 1.0.68. The panels a selected building fills: [ui-screens](ui-screens.md#selected-building).

**Buttons** (**Verified**: Controls `DAT_142133370`, actions registered in `FUN_1403636c0`): held flags `mouse_select` +0xf3,
`mouse_command` +0xf4, `highlight` (Left Alt) +0xf2. Each frame `FUN_140801630` stores edges on PlayerInterface (**Observed**): +0x2f1 select
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

- A building must be visible (**Verified**, `FUN_140800250`, `FUN_140547140`): its flag (+0x19d) is set, and its floor (+0xa4) is not above
  the floor shown by the building it stands in (`FUN_14092add0`; the town keeps the shown floor per building, 4 = whole building), so
  furniture on a hidden floor cannot be picked, whoever owns it. The exception: past a hit on a whole-shown interior of the player's, the objects inside it are
  taken though not visible. The shown floor (`FUN_1407f7230`) is the current floor for a building a player character is in, back to 4
  for one left; the flag of an interior's furniture follows the current floor itself (`FUN_14054a8f0`, +0xa4 against the viewed floor, PlayerInterface +0x1e4). Meitou
  (containers and beds): one above the viewed floor of a building in the interior view is skipped, its floor from its height (`InteriorVolume.FloorAt`); not modelled:
  the flag's own test against the current floor in a building left within 10 s (Meitou shows every floor then) and the player's whole-shown interior exception.
- Characters and ground items on a hidden floor are not visible either (**Verified**, decompile): a character in a building above the floor it shows (`FUN_1405c9f60`); a ground
  item above floor 0 above the floor its building shows, or above the current floor outside one (`FUN_1409fb540`, after each floor change in `FUN_1407f7230`). Meitou: neither
  drawn nor picked above the viewed floor of a building in the interior view (characters `GameHost.hiddenIndoors`; items `GameHost.hiddenGround`, which the renderer takes as its `HiddenItems`), a
  ground item's floor from where it is drawn.
- Parts of a building with an interior (`FUN_140291650`) count only when their floor is not above the floor that building shows; the floor of a
  hit comes from its group (`FUN_1409b1850`: 10/16/20/24/29 → 1, 11/17/21/25/30 → 2, 12/18/22/26/31 → 3, 27 roof → 4, else 0). So with a
  floor cut the ray passes the hidden roof and floors and reaches what is shown. When the viewed floor (PlayerInterface+0x1e4, PgUp/PgDn) is
  below the hit's floor, the walk skips ahead to the next hit on the same building at or below it. Such a hit ends the walk (**Verified**, decompile; but past a whole-shown
  interior of the player's, above), so nothing behind or below a shown floor or wall is taken. Meitou: `Hidden` leaves out what lies past one of a building in the interior view
  (`WorldObjectRenderer.ShownInteriorHit`, its floors up to the viewed one as the movement pick has them).
- A turret or lamp mounted on the hit building (its slot 135 handle) met within 12 units behind the hit is taken instead (`FUN_1407f2af0`).
- Outdoor farms have no collision ([collision](../formats/collision.md)), yet the original selects them; how is **Unknown** (not in the
  `FarmBuilding` constructor). Meitou (engine choice, `BuildingFootprints.Plot`): a building without collision (a player's site of any kind,
  a level farm) is hit where the ground under the cursor lies inside the hull of its parts' render-mesh bounds, just before the ground.

**Building hover and click** (`FUN_1407fc6e0`, **Observed**). Every frame over a building it sets the pointer; on a command release (a quick
right click) it gives the order to the selected characters (the shared order dispatch `FUN_1407fa650` / `FUN_1407fae70` / `FUN_1407f9e20`,
target the building, its parent building (slot 59) and the hit's x, z at the zone or terrain height, `FUN_1409b32f0`, not a building
floor); on a select release without a command release in
the same frame it selects. With an animals-only selection (+0x268: no selected character is a CharacterHuman) only the selection runs. Tests
in order (task numbers are [ai-tasks](ai-tasks.md) taskTypes):

**Observed** (user's original-game comparison, purchased ruined town house): hovering shows no right-click action pointer,
and a quick right click does not start repair. Hold right click and choose Repair to begin reconstruction. Meitou keeps
that activation menu-only; once the ruin becomes an unfinished construction site, quick Build orders apply.

| Building | Pointer | Quick right click |
|---|---|---|
| not complete (+0x160, [construction](buildings-production.md#construction-state)) | `build` | 2 BUILD |
| broken (slot 97: +0x3b6 machines, +0x3ec doors, never for plain buildings; a machine is broken only while unfinished, [broken machines](buildings-production.md#power), so only doors reach it; **Verified**: the `else` arm of the complete-building branch of `FUN_1407fc6e0` sets pointer 0x13 and, on release, order 0x5f; the 90 % health spanner below is **Observed** only) | `spanner` | 95 REPAIR. Meitou: an own broken door (`PickDoorAt`, `DoorPointer`, `DoorVerbs` Repair) sends `BuildOrder` on the door ([repair](locks.md#cutting-and-breaking-285--185-cut-286--186-break)); Shift's Engineer job is not built (it works sites only) |
| door (+0x1a0) of an incomplete house (+0x368) | `build` | 2 BUILD on the house |
| door | own: `lock` if its lock is locked, else `door`; another faction's: `lockpick` if locked (with a tooltip, text **Unknown**), else `door`; `spanner` below 90 % health | another faction's locked door without Alt: "Hold down the ALT key to pick the lock." and a move to the door; otherwise 72 OPEN_DOOR, or 73 CLOSE_DOOR when open (+0x380 = 1) |
| function 18 (BF_FLUFF), or slot 107 (+0x198, meaning **Unknown**) is 7, 9 or 12, with an inventory (without one, or when `FUN_14004a0cf` refuses another faction's, only the selection runs) | `search` when own (or the owner faction has flag +0x1d0), else `scavenge` | 26 LOOT_TARGET; a shop (function 9) of another faction without Alt: "Hold down the ALT key to steal items" and 29 MOVE |
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

- Occupied beds and cages whose BUILDING_FUNCTIONALITY has `occupant selection` (+0x3a8, read by `FUN_1402a0900`): the hover and both clicks
  go to the occupant (`FUN_1407fb410`). An occupant of another faction ends it there; for one of the player's, the building logic continues and a
  left click selects the occupant. Meitou: cages (`PlayerInterface.PickWhereAt`, where the ray meets the cage before any other character); engine choices: a player's occupant's right
  click goes to it as well, and the press opens only the occupant's menu, which carries the cage's verbs (`PrisonerVerbs`), not the cage's menu beside it (below); beds not modelled.
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

**Selection** (`FUN_1407f9000`, `FUN_1407f7f20`, **Observed**). A building does not join the squad selection (+0x208): it goes into the one
object slot +0x248 (saved as `selected_gui_object`, [save](../formats/save.md)) that also holds an inspected character of another faction.
The selected characters stay selected and keep taking orders. A building with doors is selected as its first door (slot 124 → door
list +0x1c0), except in the editor. The focus handle `DAT_1421345d0` becomes that object and `FUN_1406e91e0` points the HUD panels at it.
Selecting calls slot 38 (`FUN_1405504a0`): the BUILDING's `select sound` (BuildingSound) as switch `Buildings_Click` with event
`Building_Click`; when the building is complete and the player's faction's, its window opens (`FUN_1406e6640`; window by class in
[ui-screens](ui-screens.md#selected-building)). Clicking the selected building again reopens its inventory window if it was closed. No
highlight or outline call was found for a selected building (**Unknown** whether the renderer marks it).

The object stays selected until something replaces or clears it: a left click on another building or another faction's character replaces it.
Selecting one of the player's characters without Shift (click, drag box, number keys, `[` / `]`) runs `FUN_1407f3b50`, which clears the
squad selection, the object slot and the focus. With Shift the object slot stays but the focus (the HUD) moves to the character. A click on the
ground and Esc do not clear it (**Observed** by absence: `FUN_140800250` has no such path, and `FUN_140917120` does not use the focus handle).

Shift toggles (**Verified**): on a single player character a Shift-click in the world (`FUN_140800250` -> `FUN_1407fb410` -> `FUN_1407f9000`), a Shift-click on a squad-bar portrait
(`FUN_140413cd0`) or Shift with a number key (`FUN_140788100` -> `FUN_1407f90b0` -> `FUN_1407f8f60`) takes it out of the selection when it is in it and the selection holds two or more
(`FUN_1407f7f20`'s removal branch); alone it stays; one not in it joins. Box select and select_all only add. Whether the Squads window's portrait click toggles is **Unknown**
(Meitou: it only adds). Meitou: `SelectCommand.Toggle`.

Meitou (`PlayerInterface.BuildingSelect.cs`, engine choices): a left click with no character under the cursor selects the nearest building the ray
meets before the ground, among the player's sites and the level data's buildings (not `is node` / `is foliage`). Picking is a ray against a vertical
prism over the convex hull of the record's collision shapes (`BuildingFootprints`) as the first test, then, for a record with collision files, against its collision
triangles (`PlayerInterface.CollisionPick.cs`, double sided, nearest hit), so ground, characters and neighbours beside a courtyard or concave building are not taken for it. There are
no interior floors (a part without collision cannot be clicked, as in the original). Only hits before the ground count, for a click and for the hammer pointer alike (a building's hull and
collision reach below the ground; without the limit the ray, after meeting the ground, found them and the pointer showed the hammer on bare ground beside a building). The character selection stays. The HUD panels show the building as in
[ui-screens](ui-screens.md#selected-building) (the simulation publishes the selected building's doors, `InspectBuildingCommand`); no outline
is drawn (as the original). Selecting or inspecting a character clears it, Shift
included (the original keeps the slot); a site that disappears clears it. Selecting plays the building's select sound ([audio](../audio.md#interface)). A house is not focused as its first door, but its panels list the
first door's lines before the house's, as the door's panel does ([ui-screens](ui-screens.md#selected-building)). World (town) buildings act through their
fixtures: containers (town storages included, `search` / `scavenge` / `lockpick`), beds, cages and doors are picked, pointed at and ordered like the player's. Not built: the
machines of a town building (crafting and research benches, generators, a town turret for the player, the furniture of a bought house): machines exist only for the sites of `World.Buildings` (player-built ones and town ruins taken over),
so they have no window, pointer or order. A door (picked by its collision with the leaf
where it has slid to, `DoorSpot.Slide` × open amount, `PlayerInterface.Doors.cs`) shows `door` / `lock` / `lockpick` as in the table and a quick right click gives `DoorOrder` (the nearest selected
walks to the door's near side and opens or closes it, "It's locked." at a locked one; another faction's locked door: Alt picks the lock, else the hint
and a walk there). The right button's target, for the pointer and the click alike, is the nearest along the ray of: a character with a quick order or a menu, a ground item (its collision where it is drawn, as the original makes it ([collision](../formats/collision.md#item-physics)); one without `has collision` only by its label), a door (its collision where the leaf has slid to; one without cannot be picked), a container (its record's collision, as a building; one without cannot be picked (engine choice: how the original selects a building without collision, as a farm, is **Unknown**, see the farms above), nor one above the viewed floor of a building in the interior view, which is not drawn), a bed (its record's collision, as a building; a pixel magnet without one; not above the viewed floor, as a container), a cage (as a container), a rock and a site that is an object; a plain building takes it only when none is there (`PlayerInterface.PickAt`, as the original walks its hits nearest first; an item hit behind a container, bed or cage that lies inside its bounds takes the click, as in the original (its collision's footprint and height for the original's box); what a building seen from outside hides, and what lies past a shown floor or wall of one in the interior view, is left out). Ctrl or Alt over a downed character comes first. Sites of the player's get the `build` pointer only when unfinished; finished ones the class pointer of the table (`mine` for a machine of function 1 or 27, `use` for other machines, furnaces and turrets, `search` for a storage, none for a plain building, as in the original; a quick right click on a furnace walks there and opens its window, on a turret it mans it, [simulation](../simulation.md#production)); a right click on a finished plain building gives way to a container, a ground item, a door or a character under the cursor, so a chest inside a house can be targeted (the building's menu, with Dismantle, opens on a right click held on the building itself; a quick right click on a finished plain building is a move to the hit point's x, z at the terrain height, as the original's target (the goal then takes the walkable surface nearest that height, its floor inside, `Walkability.GroundHeight`; the original's goal snap is about 30, [pathfinding](pathfinding.md)); a level or player building with an interior is picked by its floors instead, even when the hidden roof above is nearer (`FloorSurfaces.MoveHit`: a wall or roof hit drops onto the floor below, upper floors above the viewed one are passed through); on a finished storage of the player's it walks the selection there and, once one of them is within 120, opens the storage window with the character's inventory beside it, with the `search` pointer; a left click on a storage shows its window alone).

### Context menu verbs

**Observed** (kenshi_x64 1.0.68 decompile: list builder `FUN_1407a6bc0`, `ContextMenuGUI::show` `FUN_1407a7fe0`, labels `FUN_14079b2d0`, row click
`FUN_1407a7f10`). Order numbers are [ai-tasks](ai-tasks.md) taskTypes; "the lead" is the last selected character of the player's faction.

- **Rows**: the verbs are a set of order numbers, so each shows once, **in ascending order number**; an empty label drops it. An animals-only selection
  (PlayerInterface+0x268) keeps only 5, 31, 45, 61, 123, 226, 227 (`FUN_1407f23b0`). **Verified** (`data/gui/layout/Kenshi_ContextMenu.layout`): title
  `NameText`; per row a `Kenshi_Button1` (x 0.0208, w 0.7917 of the row) and a `Value` box (x 0.8125, w 0.1875). The box is made only when some row has a
  chance (`FUN_1407f65e0`): 76 (the lock's pick chance with the selection's lockpicking; a building's below `DAT_142133f28` shows 0), 201 (the shackles'
  lock), 228 ([stealth KO](combat.md#stealth-knock-out) chance), 246 (`FUN_140885600`); text `{1,num}%` clamped 0..100, colour (1 − p, p, 0). Without
  one the buttons span the row. The menu sits at the cursor, kept on screen. The pick chance is `FUN_140297dc0` (the lock's level against the lead's lockpicking, stat 0x25 via `FUN_140884ac0`, **Verified**) x 100, the lead the selected character nearest the target (`FUN_1407f4ba0`); a caged character as the target (`0x4c` on a character) shows it unclamped, a building's lock below the minimum 0. The pointer in mode 0xf (lockpick) shows the same number as text `{1,num}%` coloured (1 − p, p, 0), at most 1.0 before the x 100, and mode 0x19 (stealth KO) `FUN_1408852f0` x `DAT_141682ad8` (**Verified**, `FUN_1406ea070`). Meitou: `PickLockCaption` for the door, container, cage and caged-character Pick Lock rows (`DoorView.Level`, `CageSpot.Level`, `ContainerSpot.Level`, `CharacterSnapshot.LockpickSkill`); the pointer text is not built.
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
12 Talk To; 16 Attack All; 17, 45 Bodyguard; 25 First Aid; 26 Inventory (player's building), Trade (player's character), else Loot; 28, 65 Stand Up;
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
| Character, carried | by a player character: 69 is added first when the lead is the carrier or the carried, then the target's own branch runs as for a down body (it is down, vfunc `0x30`): Loot / Trade (26) stays, and again 69 last when a selected character carries it (**Verified**, `FUN_1407a6bc0` start and branch ends). The pick-up verb and 223 need an uncarried target in the non-hostile branches (`+0x3d4` tested) and are dropped whenever a selected character carries anyone; the hostile branch tests only the latter, but its verb is Kidnap (246), which has no snatch flag ([carry-loot](carry-loot.md#carry-and-release)), so Meitou offers none for a carried body. By anyone not of the player's: the list is emptied (**Verified**, tail of `FUN_1407a6bc0`) |
| Character, imprisoned | 12 when it can talk; 26 unless it is the player's and selected; its cage the player's: 207 when it is the lead, else 110 and the pick-up verb; another faction's: the pick-up verb when the cage is unlocked, 207 when it is the player's and selected, 228 when neither it nor the cage is the player's |
| Player's character | not selected (not slot 34, not in bed): 26, 45, 31, then 69 when a selected character carries it, else the pick-up verb; selected: 69 when a selected character carries someone, 28 when its leg state (slot 109) is 1; slot 34 or in bed, not selected: 69 / the pick-up verb, 26, 31 |
| Other faction's, not hostile (`FUN_14079cbe0`) | standing (not down, state 0, leg state ≠ 3), the lead not on a turret (+0x260 handle empty): 228 (lead `FUN_1405c9750`, its slot 73 null), 12, 61, 45, 31, and 225 when it is friendly to the lead (slot 126) and its AI state is not 5; on a turret 235. Otherwise: 26, the pick-up verb and 223 / 273 (`FUN_140793d80`), 228, and when not down 61, 45, 12. Then 69 when a selected character carries it |
| Hostile | standing: 12 (talk tests), not on a turret 228, 16, 5; on a turret 235. Otherwise 228, 26, the pick-up verb with 223 / 273, 5 when up in state 0. Then 69 as above |
| Every character | unless dead (+0x5bc), or up and the lead's enemy (slot 125): 60 "Repairs" (robot kit) and 25 "First Aid" (organic kit), two independent tests of `FUN_1407c05a0` with the kit kind 1 and 0 (calls in `FUN_1407a6bc0` **Verified**; the kind picks the robot / organic score, **Verified**: `FUN_1407c05a0` passes it on in its second register to `FUN_140643ca0(medical, kind)`, which returns `+0xa8` (robot) for 1 and `+0xac` (organic) for 0, [first aid need](character-stats.md); the need score `FUN_140643ca0` of the target's medical state above `DAT_141682ad8`, which Meitou reads as `AidNeedFloor`; so a robot with an organic hurt part gets both verbs, a skeleton only Repairs; Meitou: `BodyStatus.NeedsRepairs` / `NeedsOrganicAid`, one `TreatOrder` serves both kits), 249 (a splintable injury, `FUN_140646400` with 50); locked shackles: 201, 185, 186; carried by someone not of the player's: the list is emptied |

The pick-up verb (`FUN_140796f80`, **Verified** by re-reading the decompile 2026-10-23): outside the gate below it is always 225. The gate: a character of another faction than the lead's, whose faction lacks
flag +0x1d0 (`not real`) and whose +0x1a0 → +0x88 object (**Observed**: the town it stands in; its writer is not in the decompile, `FUN_140852240` / `FUN_140852a30` compare it with the character's town
`FUN_1407956a0` +0x30) is set and whose faction has +0x250 clear. Inside the gate: 225 when the TARGET regards the lead as friendly (slot 126, vfunc +0x3f0 called on the target, the lead the argument; Meitou `World.RegardsFriendly`, disguise not read) and is not a trader and
`FUN_140796240` is false. The trader test (`FUN_1405cff70`) reads the `is trader` flag of the target's SQUAD TEMPLATE, so every member of a trader squad counts, guards included
([economy](economy.md#who-trades)); `FUN_140796240` is the faction war manager's forces-map lookup for the target's platoon, so a campaign force's member never passes this test (Meitou:
`CharacterSnapshot.WarParty`);
else, when the bounty is below 1 and its NPC class (+0x1a0 → +0xdc) is not 8 (Bandit): 246 Kidnap, or 225 while imprisoned (+0x2f8 = 2); every other case 225. The bounty
(`FUN_140854080(+0xf0, 0)`, **Verified**) is the amount (+8, claimed or not, [economy](economy.md#111-ledger)) of the ledger entry of the faction `FUN_140852010` picks with none given:
the prison owner's booking faction when caged, else the booking faction (legal system, or itself when it books) of the faction owning the +0x88 town, not the target's own faction. Meitou
(`World.VerbFactsOf`, `PlayerInterface.KidnapOffered`): the town is the one the target's point is in (`AiSystem.TownAt`, player towns included); the bounty is the larger of the town owner's own entry
and its booking faction's, as Meitou books under the witness's own faction ([CrimeFactions.BookingOf](economy.md#112-how-a-bounty-is-gained)); outside every town the verb is Pick Up.
Kidnap's chance column is `Character::getKidnappingChance` ([carry-loot](carry-loot.md#kidnapping)). `FUN_140793d80` (**Verified**) adds one of two exclusive verbs by the +0x88 town:
not set, or its faction not the player's: 223 when the target's home (slot 59) is a building handle whose faction is the player's; set and the player's: 273 when the target's `amInsideTownWalls`
(slot 35) is above 0 and the LEAD does not regard the target as friendly (slot 126 called on the lead, so friendliness, not enmity). The "+0x88 is the town it stands in" reading is **Observed**.
**Dispatch** (`FUN_1407fa650` → `FUN_1407f9e20`, **Observed**): 45, 0x111 and 0xdf go through the job giver `FUN_1407f5a90` (sound `Permanent_Job`), every other order through
`FUN_1407f9e20`, which per selected character swaps 5 / 16 / 0x3d for 0x107 / 0x106 / 0x10a when `FUN_1407f2840(character)` is true (**Unknown** which characters: Meitou reads it as a
ranged-weapon wielder, not built), and ends with the sound `Right_Click`. 235 Shoot At is Task_UseTurret's "target" order for the lead on a turret.
A right click on the ground (`PlayerInterface::playerMove`) is order 29 through the same path; how an order replaces a fight: [combat](combat.md#fight-lifecycle-and-orders).
**Unknown**: slots 34, 102 and the +0x260 handle (read here as the lead's turret, as it swaps every attack for 235); `FUN_1405c9750` (KenshiLib `Character::isStealthMode`) returns
the sneak flag +0xd4 unless the fight is on (`isInCombatMode`, CombatClass +0x130); where a cage opens its menu at once. 28 Stand Up is AI task 28
(`Task_UnCrouch`, [staying low](ragdolls.md#staying-low)); Meitou stands the selection at once.

Meitou (`PlayerInterface.ContextMenu.cs`; one method per target: characters, `ContainerVerbs`, `DoorVerbs`, `SiteVerbs` in `PlayerInterface.Build.cs`, `CageVerbs` in `PlayerInterface.Cages.cs`):
the same labels and order (`VerbOrders`; Meitou's own verbs after), the animals-only filter, the chance column (Stealth KO, Kidnap), Shift on Use giving the job (and on the character verbs listed under [Ordering](ai-tasks.md#perma-jobs)).
Containers and doors open their menu on a held press (a quick click as before); cages, and another faction's locked doors, open it on the press (above). Built: Put Down, Talk To, Attack Target (hostile), Attack
Unprovoked, Stealth KO, Follow (`FollowOrder`, [simulation](../simulation.md)), Loot, Trade (the player's own character, standing or down: the nearest selected walks over and its inventory opens beside the
selection's, as the loot window; both offered when `CharacterSnapshot.CanOpenInventory`, the simulation's own `CarrySystem.Openable`, holds: a carried or playing-dead body too, beside Put Down for its carrier), Pick Up and Kidnap (the same pick-up order; Pick Up on the player's own character standing too, when it carries no one: `FUN_1407a6bc0`'s own-faction
branch adds 26, 45, 31 and, with no selected carrier of it, the pick-up verb for a carriable race not carrying (+0x348) nor carried (+0x3d4), with no down test, **Observed**; Kidnap when the gates above pass (another faction's town, a real faction, not friendly or a trader squad's member or in a campaign force, no bounty, not a Bandit), with its chance, also on a sleeper (with Stealth KO, and Attack Unprovoked, Bodyguard, Talk To when not hostile; Loot on a sleeper or a caged character opens it as a pickpocket, [carry-loot](carry-loot.md#theft-and-witnesses); [carry-loot](carry-loot.md#kidnapping)),
[carry-loot](carry-loot.md)), First Aid, Splint Injuries, Unlock Shackles / Use Tools / Use Strength (shackles), Pick Lock / Escape / Pick Up / Set Free on a caged
character, Inventory / Loot / Pick Lock / Use Tools / Use Strength on containers (Loot uses the Alt path: another faction's shop stock is stolen from at once, [locks](locks.md#containers-meitou)),
Open / Close / Lock / Unlock (all the building's doors) / Pick Lock / Use Tools / Use Strength / Smash on doors ([locks](locks.md#player-orders-on-locks)), Build, Repair (owned ruined/damaged houses; Shift adds Engineer), Use, Auto-Haul (storage: one haul trip, the Hauling to job with Shift, `FUN_1407fa650`),
Inventory (storage, furnace), Man Turret (with Shift, Inventory on a storage adds Hauling to and Man Turret the Turret guard job), Dismantle, Auto-Haul 152 on an automatic machine (in place of Use; the 152 job with Shift, else a one-shot haul round). **Bodyguard** (45, `FollowOrder.Guard`): follows like 31 and fights whoever attacks the target (engine reading); the menu always adds it as the perma-job, Shift or not (`FUN_1407fa650`, [jobs](ai-tasks.md#perma-jobs)). **Attack All** (16, **engine reading**): every standing hostile within 800 of the clicked one is a target and each selected character attacks the nearest. **Shoot At** (235): replaces the
attack verbs when the lead mans a turret; `ShootAtOrder` makes every selected gunner fire at the target until it is gone ([combat](combat.md#turrets)). **Cages** are published
(`WorldSnapshot.Cages`) and picked like beds: Pick Lock / Use Tools / Use Strength while locked, Put in (108, the carried captive to that cage) or Get in (107) while free, Set Free (110) while occupied by a conscious, uncarried prisoner (the player's own cage locked or not, another faction's only unlocked; the prisoner's own menu offers it on the same terms; precondition 125 is **Verified** (`FUN_14099a480`); hiding the verb for it is an engine choice, how `FUN_1407a6bc0` treats an unmet 125 is **Unknown**).
Engine additions: Trade with a trader, Put in Cage, Put on Shackles, Cancel Construction, the door buttons, Mine. Training (97) sets the training job on a dummy. Stand Up (28) on a selected character staying low. **Throw out of home** (223) on another faction's character that is down or asleep in one of the player's
complete buildings with doors (Meitou's stand-in for the target's building handle, slot `0x1d8`, being the player's) adds the Remove Home Intruder job and its companion 224. **Throw out of town** (273, always the job `PICKUP_INTRUDERS_TOWN`) instead when the town the target stands in (`AiSystem.TownAt`) is the player's, its position is inside
the walls (`TownDutySystem.AmInsideTownWalls`: gate code, else the built-up area) and the lead (the last selected, as for the turret test) does not regard it as friendly; no 223 there.
**Corpse Disposal** (136) on the player's corpse machine (function 11): with Shift the Corpse disposal job (137, finder 138), else each selected character carrying a dead body
takes it to the nearest free incinerator (`DisposeCorpseOrder`; the order's condition 99 read as carrying a dead body, **engine reading**). Not built: Lock / Unlock of a cage (Pick Lock stands in), item menus beyond a ground item's Pick Up / Steal, the occupant's second menu and the unconscious message.

### Edit boxes and keyboard focus

**Verified** against the MyGUI 3.2.2 source (tag `MyGUI3.2.2`: `EditBox`, `InputManager`, `Button`, `TextIterator`; MIT). Kenshi's own `MyGUI.log` says "MyGUI version 3.2.3" (**Observed**; upstream has no 3.2.3 tag, so the 3.2.2 source is the nearest public one and any difference is **Unknown**).
- A mouse press gives key focus to the nearest widget that needs it; there is no traversal: `InputManager` handles no Tab or arrows, `Button` has no key handling and no key-focus state.
- **Static** boxes ignore every key. **ReadOnly** boxes take focus, selection and copy (Cut copies only); typing, Backspace, Delete, Enter and Paste do nothing. **Password** boxes copy and cut nothing but paste.
- Keys (Windows/Linux table; the Apple build uses Meta): Left/Right one character with **no** Ctrl word jump (3.2.2 has none; later versions do); Home/End line start/end, with Ctrl text start/end; Up/Down/PageUp/PageDown
  move by lines, so in a single-line box to the start or end; Shift extends the selection; Backspace/Delete erase the selection else one character; Enter adds a line in a multi-line box (Ctrl+Enter accepts) and accepts in any other
  (the `eventEditSelectAccept`); Ctrl+A select all, Ctrl+C / Ctrl+Insert copy, Ctrl+X / Shift+Delete cut, Ctrl+V / Shift+Insert paste, Ctrl+Z undo, Ctrl+Y redo (Shift is not looked at). With Ctrl held a key never types.
  Escape drops the key focus; Tab types a tab only with `TabPrinting`.
- Copy with no selection clears MyGUI's clipboard entry. Typed text and pastes delete the selection first, then insert; a box already at `MaxTextLength` (default 2048) rejects the insert, else the text is cut back from the end
  (`OverflowToTheLeft`: from the start, **Observed**: read from function names). `TextIterator`: CR+LF is one break, and a box that is not multi-line (nor word-wrapped) turns each break into a space; tabs and other controls pass through.
- Undo keeps 128 steps. A keystroke is one step; deleting a selection and inserting is one step; edits, pastes and cuts clear the redo steps, undo and copy do not; `setText` and a mode change clear both.
- **Observed** (grep of the installed `data/gui/layout`): `MaxTextLength` is set once (the character editor's `NameText`, 30), `Password` only `false`, `ReadOnly` `true` on 7 boxes (descriptions, the message log), `OverflowToTheLeft` only in the interior-mode layout.

Meitou (`UiTextBuffer`, `MyGuiScreenHost.Keyboard.cs`, `PlayerInterface.Keyboard.cs`; the shortcuts above are all implemented, Ctrl on every system): the clipboard is the window's system clipboard (Silk keyboard `ClipboardText`; the boxes keep their own
without one); an empty Copy leaves it alone; pasted tabs become spaces and other control characters are dropped; the host gives a box its `MaxTextLength`, else 2048 (`UiTextBuffer` alone defaults to 512); the history is a stack of text
and caret snapshots with MyGUI's step rules. Read-only boxes can be focused by a click (caret at the end; Ctrl+A, Ctrl+C), take only edit keys, so Tab, Enter and the hotkeys work as without a focus; Esc still closes the window. Ctrl+C / Ctrl+V reach the free camera's code only
when no box is focused. A press in a one-line box places the caret at the character gap nearest the pointer and a drag selects (`UiPainter.EditCaretAt` measures the text as last drawn;
MyGUI 3.2.2 does both); in a wrapped or multi-line box the press only focuses. Not built: a password mask (no layout turns it on), `OverflowToTheLeft`, `TabPrinting`, pixel-exact Up/Down in wrapped multi-line boxes (they move by `\n` lines); a focused wrapped box (the read-only descriptions and message log) draws its caret and selection as one unwrapped line, so Ctrl+A shows a bar on the first line only.

**Button traversal (Meitou only)**: Tab / Shift+Tab walk the top window's editable boxes and its enabled, visible buttons (not close/pin, scroll bar parts, tab headers or list rows) in layout order; the first Tab goes to the first edit box, else the first
button. Buttons are reached on modal windows and on windows with an editable box; any other window leaves Tab to change-squad. Enter presses the focused button (else the `DefaultAction` button), Space too; the press runs as a click on the button's middle. A click or a
new window ends it (Esc is unchanged). Look: the Meitou UI mode draws a 2 px `brass-light` ring offset 2 ([meitou-ui](../meitou-ui.md)); the Faithful mode draws nothing (the original has no key-focus look: **Unknown**, `Button` has no such state), so the focus is invisible there.
The settings panel and the native Meitou UI have their own keyboard focus ([native-ui](../native-ui.md)).

### Debug menu

Meitou only (no original counterpart). F10 (`ToggleDebugMenu`, rebindable) opens a cheat list; while open it takes every key (Up/Down, typing, Enter, Esc).
Rows send `DebugCommand`s, applied in the next tick's Inputs phase: teleport the selection to the camera target, spawn a character record there
(own squad, first faction hostile to the player), give an item record (`name x5 q80`; quality 0-100: armour's quality, crossbows' and limbs' level, a weapon the `PLAYER_WEAPONS` model of the highest level at or below it (else the lowest) and that level; what does not fit drops at their feet) to the first selected, add or remove money (signed number),
heal fully (`MedicalState.Heal`), every stat to 100, one cut of n damage (Enter/Right opens a body part list: random as a blow picks, or a part of the first target by template id; others without it are skipped), force hits to a part (the same list from the first target; every blow, melee, bolt or execution, then lands on that template id, else on a part of that name on another body, while a blow could pick it; Off restores the roll; `BodyOptions.ForcedHitPart`, not saved), set the Global damage multiplier of the running world (0 to 100; blows and XP at once, a crossbow when next equipped; not saved), finish every RESEARCH record and repetition (queue emptied). Heal, stats and damage fall back to the inspected character. Names match a record's string id,
its exact name, else the shortest name containing the text. Replaces the game's F10 key list (the viewer keeps its own).

## Unknowns

- Meaning of the listener's suspended field at +0x50 and of FUN_1409134c0 (possibly splash skip).
- What exactly the version-below-1 branch of the controls loader does with its action set.
- Identity of the widget behind FUN_1406e1fd0 (GUI+0x20) and of PlayerInterface+0x2f0, which gate most game events.
- Targets of events 1 and 2, inferred from the action names.
- Building selection: the meaning of slot 107 (+0x198) and `FUN_14004a0cf` in the loot branch, the lockpick tooltip text,
  whether the renderer marks the selected building, and what clears the selection when the building is destroyed.
- Press versus release timing of the command button.
- Whether a held, steering right click shows it again each order.
- Conditions inside FUN_140917120 (which windows count as closable) and the meaning of the held/edge flags.
- Box select handling of non-player squads and animals.
- Whether the controls loader accepts every composed name the formatter writes (`Alt+`, `Key_<n>`) when reading
  a hand-edited file.

## Expression reference

- Binding code encoding: `=`
- controls.cfg: `action=KeyName`
- Action table: `map[code] = action`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- Context menu verbs: [above](#context-menu-verbs)
