# KenshiHarness

Lets a program (a coding agent) see and play the **original** Kenshi, to check what the game does in a
situation before or after implementing it in MeitouClient. An RE_Kenshi plugin (`KenshiHarness.dll`)
inside the game serves commands on a named pipe; `kh.exe` sends one command and prints the reply.

Needs Kenshi 1.0.65 with [RE_Kenshi](https://github.com/BFrizzleFoShizzle/RE_Kenshi) (KenshiLib 0.5.0); the
Re_Dev cheat menu (`kep-devtools`) is optional. GPL-3.0-or-later, as KenshiLib requires of its plugins.

## Build and install

    .\build.ps1 -Zig <zig.exe> -Out <folder outside the repo> -Install <Kenshi folder>

Installs `<Kenshi>\mods\KenshiHarness` (plugin, `kh.exe`, an empty `.mod`) and adds it to `data\mods.cfg`.
The game locks the DLL: `kh stop` before reinstalling.

## Use

Run the installed `kh.exe` (`<Kenshi>\mods\KenshiHarness\kh.exe`). Commands run one at a time, in the order they arrive. Exit code 0 ok, 1 the command failed
(reason on stderr), 2 no game. Coordinates are window pixels. The game needs no focus and the real
mouse is not touched; `kh start` gives the focus back to the window that had it.

| Command | Does |
| --- | --- |
| `start [save]` | Starts Kenshi, presses the launcher's OK, waits for the main menu, loads the save |
| `load <save>` | Loads a save (a folder name under the game's save path) and waits until it is in play |
| `stop` | Ends the game process |
| `ping` | Frame counter; answers even while the game renders nothing (loading) |
| `cancel` | Ends the command in progress (a long `wait`), which then fails |
| `shot <file.png>` | Screenshot of the frame, GUI included |
| `ui [filter] [--all]` | The GUI as text: class, `"caption"`, `name=`, `@x,y` (where a click lands), size |
| `state [--near=<radius>] [--brief]` | JSON: clock, speed, camera, squad (position, `screen` point, health parts, stats); `--near` adds everyone else around the camera's centre |
| `click <x> <y> [left\|right\|middle]` | A click; `--double`, `--hold=<ms>`, `--shift`, `--ctrl`, `--alt` |
| `click <caption or name> [button]` | A click on a widget; `--nth=<n>` when several match |
| `mouse <point>`, `mouse down\|up [button]` | Moves the cursor; presses or releases a button where it is |
| `drag <x1> <y1> <x2> <y2> [button]`, `scroll <clicks> [point]` | Drag (box select), wheel (up positive) |
| `key <name>[+<name>] [down\|up]` | A key or chord: `space`, `f6`, `ctrl+s`, `shift down` (names: `src/input.c`) |
| `type <text>` | Text into the edit box that has the key focus (click it first) |
| `speed <n>` | 0 pauses, otherwise unpauses at that multiplier |
| `wait <seconds>`, `wait frames <n>`, `wait hours <game hours>` | Lets the game run |
| `camera <x> <y> <z>`, `camera <character>` | Moves the camera's centre, keeping angle and zoom |
| `select <character>...`, `select none` | The selection |
| `window back [handle]`, `window front` | Gives away or takes the keyboard focus |
| `peek <address> [bytes]`, `sym <dll> <export>`, `fault` | For working on the harness: memory dump (`peek ou+580*+2B0 24`), an export's address, the fault guard's test |

`--stall=<seconds>` (any command): how long the game may render no frame before the command fails (default 30).

`state` units: `hours` is the game clock (day = hours / 24, as the GUI shows it), positions are world units, `hunger` is
the GUI's figure / 100, `parts` are `[health, stun damage, full health]`, `stats` are the raw skill levels.

### Playing

- Left click on a character's `screen` point selects it; right click on the ground orders a move.
- The context menu shows only while the right button is held, and releasing over an entry picks it:
  `kh mouse <x> <y>`, `kh mouse down right`, `kh wait 1`, `kh ui` (the entries), `kh mouse Follow`, `kh mouse up right`.
- GUI windows, tab buttons, drop-down lists (once opened) and close buttons (`"[close]"`) are all in `ui`.
- Re_Dev: `kh key f6` toggles its window (`toggle_devtools` in `controls.cfg`); its tabs, buttons, search
  boxes and combo boxes are ordinary widgets, e.g. `kh click "Search for squads"`, `kh type "dust bandit squad"`,
  `kh click Spawn --nth=0`.
- A game loaded from a save runs unpaused: `kh speed 0` first for a fixed starting point.
- RE_Kenshi opens its settings window at the main menu and after a load: `kh click "[close]" --nth=0`.

## The reference instance

- Other plugins change behaviour. For a vanilla reference KEP's fixes are all off
  (`mods\KenshiExtensionPlugin\plugin\kep_settings.json`; the original is beside it as `.orig`). RE_Kenshi's own
  switches are in `RE_Kenshi.ini` (`FixRNG` is on).
- The game is 1.0.65, our decompile 1.0.68.

## How it works

- `src/harness.c`: the pipe, the Ogre frame listener commands run from (on the game's thread, a step a frame), the
  fault guard (a bad pointer fails the command, not the game), the wire format.
- `src/input.c`: input is not sent through Windows. Events are built and handed to the listeners the game's OIS
  keyboard and mouse call, so the game's own handlers do the rest.
- `src/ui.c`: MyGUI's widget tree through `MyGUIEngine_x64.dll`'s exports and RTTI.
- `src/game.c`, `state.c`, `control.c`: the game through `KenshiLib.dll`'s exports (the game's functions by C++ name),
  with member offsets from KenshiLib's headers, each checked with `kh peek`.
- `src/abi.c`: the game's VC++ 2010 `std::string`, `lektor` and RTTI layouts.

Adding a reading to `state`: find the member in KenshiLib's `Include/kenshi/*.h`, check it with `kh peek`, add it in
`state.c`. Nothing is an address: exports are looked up by name and layouts are checked at run time where they can be.
