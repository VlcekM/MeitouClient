# UI input: key bindings, mouse, hotkeys, selection

Sources: static analysis of `kenshi_x64.exe` (Ghidra decompile dump, `FUN_<addr>` names below), the install's
`controls.cfg`, `settings.cfg`, `screens.cfg`, `data/gui/**`, `locale/**`, `MyGUI.log`, and a probe that compares the
exe's registered defaults against `controls.cfg`. Nothing was run. Screens and layouts are in
[ui-screens.md](ui-screens.md). Camera rig maths is in [../formats/camera.md](../formats/camera.md).
What the actions do once dispatched: pause and speeds in [game-loop.md](game-loop.md#speed-and-pause), quicksave in [../formats/save.md](../formats/save.md#autosave-and-quicksave), move orders in
[pathfinding.md](pathfinding.md) and [ai-tasks.md](ai-tasks.md), selection of squads in [factions-squads-towns.md](factions-squads-towns.md#1-object-model), the job and order system in [ai.md](ai.md#jobs-and-player-orders).
Labels: **Verified** (checked against files or bytes, say which), **Observed** (read from code, not tested),
**Unknown**.

## 1. Pipeline

The UI is MyGUI 3.2.3 (**Verified**, MyGUI.log) on Ogre, drawn by the `MYGUI` compositor pass. Input is OIS
(DirectInput), initialised in FUN_140829270 (non-exclusive, foreground, windowed mouse), which also loads
`controls.cfg` (FUN_140362430).

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

**Observed** (FUN_140360a80, FUN_140360da0): a held action's flag stays set between press and release; an event
action's flag is reset at the end of every frame.

**Observed**: the Controls manager is a plain (non-polymorphic) global object at 142133370.

## 2. Binding code encoding

**Verified** at three independent sites (registration immediates, the name formatter FUN_1403612e0, the key
handler FUN_140360b30):

$$code = scancode \;|\; 0x100\cdot[Shift] \;|\; 0x200\cdot[Ctrl] \;|\; 0x400\cdot[Alt]$$

- Low byte is the OIS/DirectInput scancode. Left/right variants of each modifier (0x2a/0x36, 0x1d/0x9d,
  0x38/0xb8) map to the same modifier bit.
- If the low byte is 0 the code is a mouse button: $code = (b+1)\cdot 4096$ with $b$ = 0,1,2, shown as
  `Mouse1..3` (Mouse1 = 0x1000, Mouse2 = 0x2000, Mouse3 = 0x3000).
- Key names come from a 256-entry pointer table at 141cf8e70 indexed by scancode (**Verified**, read from exe
  bytes): 0x01 `Escape`, 0x0c `-`, 0x0d `=`, 0x0e
  `Backspace`, 0x0f `Tab`, 0x1a `[`, 0x1b `]`, 0x1d `Left Control`, 0x29 `GRAVE`, 0x2a `Left Shift`, 0x33 `,`,
  0x34 `.`, 0x38 `Left Alt`, 0x39 `Space`, 0x3b `F1`, 0x4a `NUM-`, 0x4d `NUM6`, 0x4e `NUM+`, 0x57 `F11`,
  0x58 `F12`, 0xb7 `SYSRQ`, 0xc7 `Home`, 0xc9 `PgUp`, 0xd1 `PgDn`. 111 of the 256 entries are null (unnamed).
- Composed name (FUN_1403612e0, **Verified** against the composed names in the shipped `controls.cfg`, which
  has `Shift+F12`, `Ctrl+F6`, `Ctrl+Shift+F11`, `Shift+;`): prefixes in the order `Ctrl+`, `Alt+`, `Shift+`
  followed by the key name (each prefix is inserted at the front, shift first). Mouse buttons are `Mouse<n>`; a
  scancode with a null table entry prints as `Key_<n>`. The Alt+ prefix and the `Key_` form are
  **Observed** (read from the exe's string bytes, not present in the shipped file).

## 3. controls.cfg

**Verified** (1449-byte install file, loader FUN_140362430, writer FUN_1403614b0):

- Line 1: version integer (`1`). Then one `action=KeyName` line per binding, so an action may have several lines
  (81 binding lines for 73 actions in the shipped file).
- Loader: if the version is below 1 it puts `camera_zoom_in/out`, `floor_up/down`, `build_move_up/down` into a
  set that is consulted while applying the file, evidently so the file's old bindings for those actions are
  ignored and the registered defaults stay (**Observed**: the set is filled, the exact use of it was not
  followed). A missing or empty file makes it call the writer.
- Writer (walks both binding maps and writes every code that points at the action) emits only bound actions.
  Unbound by default: `toggle_faction`, `toggle_jobs`, `prospect` (**Verified**: registered with an event id and
  no key, and absent from the shipped `controls.cfg`).
- Not rebindable in the options UI: `toggle_taunt`, `toggle_crafting`, `toggle_help`, `editor_delete`
  (**Verified**: the options-tab builder FUN_1403f0260 references the name of every other action but these four).

## 4. Action table

Registered in FUN_1403636c0 via FUN_140363440(name, eventId, key1, key2, modifierMask, context). Held-state
actions have event id 0 and are registered with the held flag, which makes FUN_140362260 strip the modifier bits
from their codes, so they match with any modifier down (**Observed**). Defaults were checked against
`controls.cfg`: **Verified** 71 of 73 match; the registration immediates (key1, key2, event id) were re-read from
FUN_1403636c0 and compared with the 73 distinct action names and 81 binding lines of the shipped file; the two
mismatches are explained below.

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

The two mismatches (`build_move_up`, `build_move_down`): the exe registers NUM+/NUM- as secondaries for both
move and tilt, but `controls.cfg` carries them only for tilt. Explanation, **Verified** by three places: (1)
FUN_140362260 stores the action record into the map slot for the code (`map[code] = action`, overwriting any
previous owner and counting bindings on the action); (2) `build_tilt_*` are registered after `build_move_*`
with the same NUM+/NUM- codes in the same build-mode context; (3) the writer FUN_1403614b0 enumerates the maps and
writes each code under the action that now owns it, so move loses the keys and the shipped file shows exactly
this. The general rule is "the last registration of a code wins; there is no conflict check at registration".

## 5. Key and mouse handling

**Key listener** (vtable 14171c3f8, slot 1 press = FUN_14082b010, slot 2 release = FUN_14082a580; **Verified**:
slots from rtti.tsv, and the two bodies read - injectKeyPress / injectKeyRelease and the Controls calls; an
earlier note had keyboard and mouse vtables swapped):

1. If the listener's own field at +0x50 is non-zero the key only goes to FUN_1409134c0 (apparently the
   splash-skip path, **Unknown**). Otherwise the key is injected into MyGUI.
2. If the options key-capture row is active (FUN_1403e7220), the shift/ctrl/alt globals (142133449/48/4a) are
   updated and the key goes to the capture handler FUN_1403f0000 (section 10) instead of the binding table.
   Otherwise, when a MyGUI key-focus widget is an edit box, or a combo box that is *not* in drop-down mode (an
   editable combo), the binding table is skipped; a combo in drop-down mode does not skip it (**Observed**).
3. Return triggers the first button of the top message box, Esc the last button (**Observed**).
4. Esc otherwise (in game): close open windows/overview tabs if any (FUN_140917120); else cancel build placement
   (FUN_1404d4320) if build placement is active; else toggle the Esc menu (FUN_1409173c0). On the title screen
   it closes sub-panels (FUN_140914a90). **Observed**.
5. Otherwise call the binding lookup FUN_140360b30. It first records modifier state (shift 0x2a/0x36 -> flag
   +0xd9, ctrl 0x1d/0x9d -> +0xd8, alt 0x38/0xb8 -> +0xda), builds `modifiers|key`, looks it up (build-mode map
   first when mode +0xd4 == 1, then the general map); if that fails and a modifier is down, retries the bare key.
   A hit sets the flag byte of the action; for event actions (flag +4 == 0) it appends the action record to the
   queue at Controls+0x78 (the event id is read from it later). A hit is accepted when the action is a held one,
   or the code matches exactly, or the pressed modifiers are a subset of the action's accepted-modifier mask
   (0x100 = Shift). **Observed**: read twice (FUN_140360b30 and the registration of the masks), not tested.

**Mouse listener** (vtable 14171c420: moved = FUN_140829e30, pressed = FUN_14082a090, released = FUN_14082a1e0):

- Button $b$ becomes binding code $(b+1)\cdot 4096$ (after the swap setting: global 14213346b flips buttons 0 and
  1). **Verified** (FUN_14082a090 and FUN_14082a1e0 both compute it; the formatter FUN_1403612e0 prints
  `Mouse` followed by code / 4096).
- A press always runs the binding lookup (FUN_140360b30). It is injected into MyGUI (injectMousePress, after
  refreshing the focus widget) unless the options key-capture row is active. A release goes to the capture
  handler when capture is active, else to MyGUI unless a PlayerInterface placement predicate holds
  (FUN_1407f2530 on PlayerInterface+0xa8).
- Global 1421337b8 records "cursor over a GUI widget" (set from MyGUI's focus widget on move and press); it blocks
  world clicks (**Observed**: many readers, e.g. FUN_140800f10, FUN_140800250, FUN_1406b0f90).
- Move (all of this, including the MyGUI move injection, is skipped while flag 14213345d, the mouse-rotate
  state, is set): pixel position at 142133474/478, normalised (÷ viewport) at 14213346c/470, relative delta at
  14213347c/480 (scaled by two constants), wheel z/100 at 142133484. The wheel value is zeroed when the cursor
  is over a widget that either has a parent or has a main sub-widget; it is kept only over a parentless widget
  with no main sub-widget. **Observed**.

## 6. Event dispatch

GameLoop_InputActions (FUN_140788100; step 4 of [game-loop.md](game-loop.md#one-gameworld-update)) drains the queue each frame. **Observed**: it does nothing while the loading
window is visible (FUN_1406e2100 is a visibility test on the LoadingWindow, constructor 14091a280); the Esc menu
being visible (EscMenu, constructor 1409180e0) ignores every event; two further gates suppress most game
events: a byte at PlayerInterface+0x2f0 and a visibility flag of the widget at GUI+0x20 (FUN_1406e1fd0, not
identified; pause 0x12 is only blocked by the second). While the MainBar object exists every event except the
100..120 and 0x22 group first goes to the MainBar hotkeys (FUN_1407262b0):

| Event | Effect (**Observed**) |
|---|---|
| 1 | open the inventory window of the selected character (FUN_140724980 -> FUN_1406e6f10; target window inferred from the action name `toggle_inventory`) |
| 2 | open or, when already open, close the per-character window for the selection (FUN_1406e5f10; stats window inferred from `toggle_stats`) |
| 3 / 4 / 5 / 6 | request Overview tab Map / Faction / Research / Crafting (HUD field +0xd0 = 0/1/2/3) |
| 7 | toggle the visibility of one GUI window (FUN_1406e1f20; taken to be the Tutorials window, **Unknown** which) |
| any other id | forwarded to the orders panel handler FUN_1407226e0, which acts on 0x17..0x21 (orders buttons) and ignores the rest |
| 8 | not in the hotkey function: handled in the action switch by FUN_1406e2a20, which flips the GUI-hidden flag at GUI+0x330 ("toggle whole GUI") |

Game-side events: 9 editor, 10 build toggle, 0xb undo, 0xe quicksave and 0xf quickload (both use the save name
`quicksave`, [save.md](../formats/save.md#autosave-and-quicksave)), 0x11 focus character, 0x12 pause toggle, 0x13/0x14/0x15 speed 1.0 / 2.0 / 5.0 (cross-checked with
[game-loop.md](game-loop.md#speed-and-pause), **Verified**: the speed setters are called with exactly 0, 1.0, 2.0 and 5.0 there), 0x16 stop, 0x22 select all (without Shift clear first, then add every
member of the active squad), 0x23 next non-empty squad of the player faction (wraps), 0x24/0x25 floor +-1,
0x26/0x27 next/previous character, 0x28 free camera, 0x29 camera grid, 100..120 select the Nth member of the
active squad (Shift adds; the same key twice within 0.5 s is a double tap that centres on the character),
900 reload biomes, 0x386 fix interior furniture then rebuild navmesh ([pathfinding.md](pathfinding.md#generator-runtime-and-tool-time-navmesh-building)), 0x398..0x39a gizmo.

Screenshot (event 0x10) is handled in frameEnded: hides the camera grid widget for two frames and writes
`_screens/screenshot_<N>`; count and multiplier come from `screens.cfg` (**Observed**).

## 7. Mouse and selection

**Observed**:

- Box select (FUN_140800f10) starts when mouse_select is pressed with the cursor not over a GUI widget. On
  release a plane-bounded volume from the drag rectangle selects characters of every platoon of the player faction
  (list at faction+0x208; platoons and factions: [factions-squads-towns.md](factions-squads-towns.md#1-object-model)) that satisfy

  $$d_{camera}^2 \le 5.625\times10^{7}\;(d \le 7500)$$

  and lie in the frustum. Shift adds, otherwise the selection is cleared first (FUN_1407f3b50).
- Without a box, a click goes to FUN_140800250 (ray cast, hit list, select object). The command button issues
  move / interaction orders via FUN_1407fa850 / FUN_1407fc6e0 (the orders become `Task_Move` and the other tasks of [ai-tasks.md](ai-tasks.md#tasktype-to-task-class); walking: [pathfinding.md](pathfinding.md#path-queries)).
- Holding the command button (global 142133464 = mouse_command held) over the same target accumulates the frame
  time at PlayerInterface+0x200; once it exceeds 0.2 s the context menu opens (FUN_1407a6bc0, called from
  FUN_140800250). **Observed** (read once; the timer resets when the button is released). A couple of
  target kinds (virtual-call kind values 8 and 0x16 on the picked object) shortcut the 0.2 s test while the
  button is down; their meaning is **Unknown**.
- Manual text in the UI (strings): Shift + right click appends a job; Alt + right click steals, picks a lock or
  escapes a cage; a quick right click on terrain moves; on an enemy attacks (red cursor); left drag box
  selects; portrait click selects, double click centres and follows; middle mouse holds rotate, wheel zooms;
  in build mode `<` `>` rotate, left click places, right click finishes a link.

PlayerInterface (global 142134690): +0x2a0 player faction, +0x2a8 active platoon, +0x298 placement ghost,
+0x1e4 floor, +0x48 context menu. **Observed**.

### Context menu verbs (FUN_14079b2d0)

Keyed by the AI taskType numbers of [ai-tasks.md](ai-tasks.md); names matched (**Verified** by comparing the verb names with the `taskType` names of the table in ai-tasks.md for the numbers listed), shared entries
**Observed**: 2 Build; 3 Steal / Pick Up; 5 Attack Target; 12 Talk To; 16 Attack All; 17/45 Bodyguard; 25 First Aid;
26 Inventory / Trade / Loot; 28/65 Stand Up; 31 Follow; 60 Repairs; 61 Attack Unprovoked; 68/213/225 Pick Up;
69/70 Put Down; 72 Open; 73 Close; 76 Pick Lock; 77 Lock; 78/140 Unlock; 87 Use; 95 Repair; 96 Dismantle;
97 Training; 98/258 Sleep; 99 Put in Bed; 107 Get in; 108 Put in; 109 Knock Out; 110 Set Free; 111 Break Out;
123/226/290 Smash; 124/152 Auto-Haul; 136 Corpse Disposal; 146 Man Turret; 166 Capture; 185/285 Use Tools;
186/286 Use Strength; 201 Unlock Shackles; 207 Escape; 223 Throw out of home; 228 Stealth KO; 229 Stealth Kill;
235 Shoot At; 246 Kidnap; 249 Splint Injuries; 251/252 Escape (skill/strength); 273 Throw out of town.

## 8. Mouse pointer modes

FUN_1406e3d60 picks the pointer from a state number. Names are **Verified** against
`data/gui/pointer/kenshi_pointers.xml`; the state to name mapping is **Observed**: 0 arrow (invisible when a flag
is set), 1 med, 2 search, 3 lift, 4 pick_item, 5 attk, 7/8/10 talk / s_talk, 9 use, 0xb build, 0xc door,
0xd door_escape, 0xe lock, 0xf lockpick, 0x10 house, 0x11 green, 0x12 mine, 0x13 spanner, 0x14 light, 0x15 steal,
0x16 hand, 0x17 invalidmove, 0x18 scavenge, 0x19 knockout. State 6 is not handled. `invisible` is used with the
free camera on and the Esc menu hidden.

## 9. Strategy camera input

FUN_1406b0f90 (free camera FUN_1406b0960), **Observed**: WASD/arrows set movement flags; rotate_left/right add
$\pm9$ to the yaw rate, tilt +- add $\pm2$ to the pitch rate; mouse_rotate pins the cursor
(GetCursorPos/SetCursorPos) and uses the relative deltas. Edge scrolling (setting `Edge scrolling`) triggers when
normalised mouse x or y is below 0.01 or above 0.99; in pan mode it pans, in follow mode (mode field +0x30 not
0xb) it rotates by $\pm3$ yaw and $\pm1$ pitch.

## 10. settings.cfg and the options window

Loader FUN_1403e84f0, options window FUN_1403ec4f0, tab builder FUN_1403f0260. **Correction**: an existing
catalog row and an earlier version of [save.md](../formats/save.md) (now corrected, as is [game-loop.md](game-loop.md#speed-and-pause)) called FUN_1403f0260 the input handler; it is the options-tab builder.

- Tabs: General, Gameplay, Graphics, Audio, Controls, Active mods; a hidden Defaults button; editor-only keys
  under Tools. Each key row (DataPanelLine KeyConfig) shows primary and secondary binding buttons.
- Key capture (FUN_1403f0000, **Observed**): Esc (scancode 1) cancels. For a non-held action, presses of a bare
  Shift, Ctrl or Alt key (either side) are ignored and the live modifier globals (142133449 Shift, 142133448
  Ctrl, 14213344a Alt) are OR-ed into the code; for a held action (mouse_select, camera_*, ...) modifiers are
  not added. If the new code is already bound in that context its previous owner loses it (FUN_140361070 erases
  the map entry and decrements the owner's binding count, and the owner's options row text is refreshed); the
  slot's own previous code is erased the same way; then FUN_140362260 inserts the new binding. Mouse buttons
  are captured by the mouse release handler (it calls FUN_1403f0000 with the button code). Reset to defaults
  is FUN_1403e7550 (clears the maps by FUN_1403636c0's registration, then refreshes the key rows).
- Settings read by FUN_1403e84f0 (**Verified**: the same names are in the settings writer FUN_1403eca90 and
  `settings.cfg`): `mouse speed pan`, `mouse speed tilt` (default 6.0 each), `camera speed`, `camera zoom`
  (default 125.0), `invert X`, `invert Y`, `swap mouse buttons`, `hardware_mouse`, `Edge scrolling`. The loader
  fills a settings struct with pan, tilt, camera speed and zoom as its first four floats (fields 1, 2, 0, 3);
  the options sliders "Camera Move Speed" (min 150, max 1000), "Camera Rotate Speed X" and "Y" (2..50) and
  "Camera Zoom Speed" (10..200) are bound to 142133490, 142133494, 142133498 and 14213349c. The struct lives at
  142133490, so pan = Rotate Speed X and tilt = Rotate Speed Y (**Verified** 2026-10-06: `Blood`, the loader's field +0xE4,
  is the global 142133574 that the options and the combat code use, and `view distance` +0x18 is 1421334a8). The main
  loader does not read `mouse speed horizontal/vertical`, but the start-up set-up FUN_1408149a0 does (correction: an
  earlier version said nothing read them): it reads both into two floats that scale the raw mouse x / y movement in the
  input listener, and when `vertical` is missing it sets both to 1.5 and appends the default lines (**Verified**). All
  settings keys: [settings.md](../formats/settings.md).
- Option labels: mouse_select "Selection", mouse_command "Issue command", pause "Pause Game", screenshot "Take
  screenshot", speed_1/2/3 "Normal/Faster/Fastest game speed", floor_up "Up one floor", highlight "Highlight
  Items", mouse_rotate "Rotate camera".

## Unknowns

- Meaning of the listener's suspended field at +0x50 and of FUN_1409134c0 (possibly splash skip).
- What exactly the version-below-1 branch of the controls loader does with its action set.
- Identity of the widget behind FUN_1406e1fd0 (GUI+0x20) and of PlayerInterface+0x2f0, which gate most game events.
- Which window event 7 toggles (taken to be Tutorials); targets of events 1 and 2 inferred from the action names.
- Meaning of the target kinds 8 and 0x16 that shortcut the 0.2 s context-menu hold test.
- Press versus release timing of the command button, and the exact rotation marker condition.
- Conditions inside FUN_140917120 (which windows count as closable) and the meaning of the held/edge flags.
- Box select handling of non-player squads and animals.
- Whether the controls loader accepts every composed name the formatter writes (`Alt+`, `Key_<n>`) when reading
  a hand-edited file.

## Implementation outline

1. Model a `BindingCode` (scancode + modifier bits, mouse buttons as $(b+1)\cdot4096$) and a key-name table of
   256 entries; read and write `controls.cfg` (version line, repeated `action=Name`).
2. A `ControlsMap` with a general and a build-mode dictionary; per frame produce held flags and an ordered event
   queue, then clear (event flags each frame, held flags on release). Registration is "last writer of a code wins"; rebinding in the options UI removes the code from its previous owner. Event ids from section 4/6 become an enum.
3. Dispatch order: MainBar hotkeys, then game actions; suppress while the Esc menu or the loading window is visible, and
   while a text field or key capture owns the keyboard.
4. Selection: box select with the 7500-unit range and frustum test, Shift to add; number keys with double-tap.
5. Context menu verbs keyed on task type ([ai-tasks.md](ai-tasks.md)); pointer state enum.
6. Camera input feeds `Meitou.Data.World.KenshiCamera`; settings from `settings.cfg`. Use `GameDatabase` and
   `LoadOrder` only for data-driven lists (new game start-offs, see [ui-screens.md](ui-screens.md)).
