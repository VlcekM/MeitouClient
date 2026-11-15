# UI screens

Original MyGUI stack/resources/layouts, MainBar/Overview and menu/inventory/trade/dialogue/build/
tutorial binary research. Catalog and named checks below. Current native compatibility:
[MyGUI](../formats/mygui.md), [parity](../faithful-ui-parity.md), [native API](../native-ui.md).
Input/action encoding: [ui-input](ui-input.md); gameplay backends are independent of UI appearance.

## Title artwork and location

**Observed (file search of the install and `data/gui/images/kenshi_images.xml`):** the brush-stroke
"Kenshi" wordmark has no file of its own; it is painted into the title backgrounds
`data/gui/gfx/title_screen1/3/4.dds` (1920×1080). Meitou therefore ships the logo itself
(`assets/branding/kenshi-logo.png`).

## Flow and data

Keep original widget IDs/skin/fonts/resources for GUI mods. Meitou styled mode is independent;
do not treat one mode's visual audit as the other's coverage. Table catalog and HUD/icon notes
retain new measured fields and current implementation limits.

### 2. Screen catalog

Addresses are the class constructor or layout loader (**Observed** unless noted). Layout names are **Verified**: each
`.layout` string is referenced by exactly the listed function in strings.tsv, and 49 of the 52 layout files are
referenced at all. Class names are RTTI names (**Verified** in rtti.tsv) except Splash, MapScreen and MessageBox,
which are descriptive (RTTI knows `MessageBoxManager::Box`; the constructor of a MessageBox is FUN_1406e70a0).

| Screen (class) | Address | Layout | Layer | Data shown |
|---|---|---|---|---|
| TitleScreen | 140918ef0 | Kenshi_MainMenu | Main | Continue, New game, Load game, Import game, Options, Credits, Exit (captions upper-cased, [Title screen and its windows](#title-screen-and-its-windows)); version text "Kenshi 1.0.68 - x64 (Newland)" |
| Splash | 140916140 (constructor), 140913720 (per-frame fade) | no layout file: widgets built in code from the image sets `Kenshi_Splash_Logos` (texture `physSplash.jpg`, 1024 x 768) and `Kenshi_Splash_LoFi` in `kenshi_images.xml` | - | logos, see [Splash](#splash) |
| EscMenu | 1409180e0 | Kenshi_MainMenuPopupPanel | Main | New game, Save game, Load game, Options, Exit game, Resume |
| Options | 1403ec4f0 | Kenshi_OptionsPanel | Window | `OptionsTab` TabControl, `CloseButton` "Close", `DefaultButton`, `TooltipPanel`, caption "Options"; the tabs are built by `FUN_1403f0260` (**Verified**, its string literals in order): General (Tutorials with Reset Tutorials, User Save Location, Auto save, Show character names, Show movement / rotation marker, Edge scrolling, Language censorship, Show damage floaters, Font Size, Fast zone hopping), Gameplay (Squad size multiplier, Difficulty settings, Town attacks frequency, Dismemberment), Graphics (Full Screen, Border, View Distance, Terrain Detail, grass / foliage / NPC / objects / feature / distant-town ranges, Water reflection, Shadows, Blood, Decal range / resolution, Harpoon limit, HeatHaze), Audio (the six volumes, Music Frequency), Controls (Invert X / Y, Swap mouse buttons, Hardware Mouse, camera speeds, then Key bindings: Selection, Issue command, Pause, ... Highlight Items, ... Build mode keys, Tools: Toggle game editor, Rebuild navmesh, Reload biome data), Defaults, Active mods; each panel is a `DatapanelGUI` (vtable `+0xc0` sets its panel number 1, 0x22, 0x17, 0x18, 0x19, 0) whose rows are `DataPanelLine_*` objects. **Verified** (`FUN_1403e7550`, the Defaults button's handler): Defaults puts every key binding back (`FUN_1403636c0` on the input manager, then each key line is refreshed), nothing else. **Verified** (`FUN_1403f0000`, key lines): a click makes the line wait (the panel holds it, `FUN_1403e7220`); Esc cancels, a modifier alone is ignored (it joins the next key's code), any other key or mouse button (taken on its release, swapped by `swap mouse buttons`) becomes the binding, and an action that held it gets this line's old one (a swap, keys and buttons alike). **Verified** (`FUN_14082b010` key 1, then the in-game close-all routine `FUN_140917120`, which calls the panel's hide `FUN_1403ee820`): Esc on the options window runs the same hide as the Close button (`FUN_1403eb3c0`, which adds the click sound and the font re-apply `FUN_1403e81a0` before it): a key line waiting is cancelled, then `settings.cfg` is written (`FUN_1403eca90`) and the window hidden; nothing is discarded (every row applies when changed) and no sound plays. General's User Save Location is a tick (`FUN_1403f0260`; tooltip "Save games to user directory instead of the kenshi install path. Current location: {1}") that moves the save root, see [save.md](../formats/save.md#where-saves-live). The `settings.cfg` reader `FUN_1403e84f0` names the options (Dismemberment, Show names, Edge scrolling, view and terrain ranges, grass, shadows, the six volumes, Auto save, squad / population / raid multipliers...) |
| NewGameWindow | 140917530 | Kenshi_NewGamePanel | Window | start-off title, description, Difficulty, Cash, Play Style; Begin / Advanced options / Cancel |
| NewGameOptionsWindow | 1409151d0 | Kenshi_AdvancedOptionsWindow | Window | difficulty sliders, see [combat.md](combat.md), [character-stats.md](character-stats.md#new-game-advanced-options-that-change-these-mechanics), [buildings-production.md](buildings-production.md#construction-tick) and [factions-squads-towns.md](factions-squads-towns.md#population-and-contents) |
| LoadGameMenu | 140480b50 | Kenshi_LoadGamePanel | Window | save list, info panel, "Reset squad positions" |
| SaveGameMenu | 140481ed0 | Kenshi_SaveGamePanel | Window | name edit box |
| ImportGameMenu | 140481130 | Kenshi_ImportGamePanel | Window | ticks: positions, buildings, research, dead NPCs, relations; Advanced options; the import kind of the save request is described in [save.md](../formats/save.md#save-requests-fun_14047c310) |
| LoadingWindow | 14091a280 | Kenshi_LoadingScreen | Top | picture, "LOADING" with dots, a tip ([Loading screen](#loading-screen)) |
| MainBarGUI (HUD) | 14072d3b0 | Kenshi_MainPanel | Middle | see section 4 |
| Overview (ManagementScreen) | 14049f5b0 | Kenshi_OverviewWindow | Window | tabs Map, Faction, Research, Craft, Squads, Messages ("DIALOGUE"), AI |
| MapScreen | 140490210 | in Overview | - | world map, markers ([Map](#map)) |
| ProspectingWindow | 14049c2e0 | Kenshi_ProspectingWindow | Window | resource lines (`Kenshi_ProspectingWindowResourceLine`) |
| CharacterStatsWindow | 1408ba700 | Kenshi_StatsWindow | Window | Attributes, Skills1..4, Statistics, Description1/2 data panels |
| Inventory (character) | 1401538e0 | inventory layouts | Window | slots: back, shirt, head, legs, boots, armour, main weapon, hip weapon, belt, backpack_attach, ARRANGE, LIMBS |
| Inventory variants | 140154cd0 generic, 140154f80 generic fixed, 140155220 animal, 140155910 research, 140155ee0 trader, 140155f90 building, 1401573d0 limbs ([limbs window](#limbs-window)) | | Window | container contents; which building opens which: [selected building](#selected-building). An animal's inventory and loot use `Kenshi_InventoryAnimalWindow` (main grid, `backpack_attach`, OPEN BAG, data panel; no equipment slots; Meitou swaps the layout by the shown character, #32); the player's own animal's inventory has no main grid ([animals](animals.md#death-items)) |
| CharacterTradingWindow / Box | 14072a880 / 14072a070 | Kenshi_CharacterSelectionWindow / Box | Middle | character picker for trade ([economy.md](economy.md#12-slaves-prisoners-animals)) |
| DialogueWindow | 140725740 | Kenshi_ConversationPanel | Middle | portraits, names, dialog line, money ([dialogue](dialogue.md#ui)) |
| DialogueSpeechBubble | 140725f30 | Kenshi_SpeechPanel | Dialog | floating speech ([dialogue](dialogue.md#ui)) |
| ContextMenuGUI | 1407977a0 | Kenshi_ContextMenu | Popup | verbs, see [ui-input.md](ui-input.md#context-menu-verbs) |
| BuildModeWindow | 1404e5210 | Kenshi_BuildWindow | Middle | categories, buildings, stats, Commands box, Confirm / Undo / Build, floor arrows |
| CharacterEditWindow | 1405f4dd0 | Kenshi_CharacterEditor | Back | character editor |
| MessageBox | 1406e70a0 | Kenshi_MessageBox | Info | message, buttons (Return = first, Esc = last) |
| MessageRoller | 140724af0 | Kenshi_MessagePanel | Popup | rolling event messages |
| Tutorial panel / list | 140975ca0 / 140977640 | Kenshi_TutorialPanel / Kenshi_TutorialsWindow | Window | tutorial text |
| OpenSaveFileDialog | 1401a0b10 | Kenshi_OpenSaveFileDialog | Window | file dialog; Meitou uses it for the editor's IMPORT only (characters) |
| InteriorModeButtonWindow | 14077a1f0 | Kenshi_InteriorMode | Back / Main / Window (several widgets) | interior mode toggle |

### Splash

**Verified** (decompile of `FUN_140916140`, `FUN_140914850`, `FUN_140913720`): a full-screen `WhiteSkin` widget coloured black, holding one ImageBox per
slide, `Kenshi_Splash_Logos` then `Kenshi_Splash_LoFi` (`lofi.png`, 677 x 700), all hidden at first. A slide's height is the image height x `DAT_1421337ec` (the GUI's horizontal scale = screen width / 1920, the width of `Kenshi_UI.png`; `FUN_1406e3c30` sets it and the vertical scale `DAT_1421337f0` = height / 1080) x a
factor (100 for Logos, 0.7 for LoFi), capped at the screen height; its width keeps the image's aspect; it is centred (integer pixels). Each frame the current
slide is shown with alpha min(1, 3 x seconds since it started); a press (any of four input flags) is remembered; once the slide has been drawn at alpha 1
on more than three frames, a press or more than 4 s ends it: hidden at once (no fade out) and the next starts. The scale is **Verified** (`FUN_1406e3c30`, atlas 1920 x 1080 read from the install); Meitou uses it (`LoadingScreen.SplashSlideHeight`), which differs from height / 1080 only off 16:9.

Meitou plays it in the Faithful mode on a start that opens on the title, over the boot (`LoadingScreen.StartSplash`) when the Interface setting "Splash
screens" (`UserConfig.SplashScreens`) is on its Faithful side (the default is Meitou: no splash), never in the Meitou mode; the loading screen shows if the boot outlasts it. The two slides take
8 s or more, the boot 3 to 4 s (Observed, 2026-10-22): skipped, the title shows that much sooner. Presses skip only once input exists, after the world's boot (engine limit). The Faithful title is opaque, so the boot's view is not warmed
behind it (the original loads a world only for a game; [engine](../engine.md) "Loading").

### Title screen and its windows

**Verified** (decompile 2026-10-22: TitleScreen `FUN_140918ef0`, its update `FUN_140913880`; LoadSaveWindow `FUN_140480100`, LoadGameMenu
`FUN_140480b50`, SaveGameMenu `FUN_140481ed0`, ImportGameMenu `FUN_140481130`; NewGameWindow `FUN_140917530`, refresh `FUN_140916620`), **Observed**
against French 1440-line screenshots. Every caption the code sets is the catalog's message upper-cased (boost::locale to_upper) unless noted.

- **Title buttons**: "Continue", "New game", "Load game", "Import game", "Options", "Credits", "Exit". Each first closes the start, load and import
  windows and the credits (`FUN_140914a90`); Load and Import close their own window when it is open, New game leaves its window as it is. Continue
  loads the save named by `continue=` (`FUN_140914c40`) and shows only when that save's `quick.save` exists ([save.md](../formats/save.md#where-saves-live)); Meitou loads the newest save by time: the same save, because every save request (the Save window, quicksave, and the autosave through `FUN_14047b920`, all kind 1) writes `continue=`, so it names the last save written (**Verified** `FUN_14047c310`, `FUN_14047be70`); it differs only when files are touched outside the game or another install wrote a newer save. Version text: the literal "Kenshi 1.0.68 - x64 (Newland)" (Meitou: "Meitou - Faithful UI").
- **Escape on the title** (**Verified**, key handler `FUN_14082b010`, key code 1, when no message box or edit box takes it): `FUN_140914a90` closes the start, load and import windows
  and the save-progress window; only when it closed none of them is the quit flag set (the same flag the Exit button sets, no confirmation). Hiding the credits panel
  (done last in that function) is not counted, so Escape with only the credits showing hides them and quits. Meitou: `PlayerInterface.EscapeKey`, with the title the only open screen; with a window
  open the generic Escape closes it.
- **Deleting a save** (`LoadSaveWindow::keyPressed` `FUN_14047fd00`, **Verified**): Delete or Backspace with a save selected in the load, save or import window (the handler `FUN_14047fd00` is installed by the shared base constructor `FUN_140480100` and tests no window; it fires only while the list has key focus); the save in use gets "Delete Save" / "This save
  file is currently in use and cannot be deleted" (OK); any other a Yes/No "Are you sure you want to delete this save game?" whose yes runs `deleteSelectedSave`. No button. Saving asks "Overwrite save {1}?" only on a byte-exact name match (`FUN_14047d830`, no case folding; **Verified**), so on a case-insensitive file system a differently-cased name saves into the existing folder unasked. Meitou: "in use" is the save the running game was loaded from or last written to (quicksaves and autosaves included).
- **Credits** (`FUN_140914230`, `FUN_140913880`): `data/credits.txt` read once, empty lines dropped, a "-" line a blank one, untranslated, in
  `CreditsPanelContent` (white, "HCenter Top"); the content starts 0.85 of `CreditsPanel`'s height down and moves up 25 px a second of wall-clock
  time, with no loop or stop. Credits again restarts it. `CreditsPanel` is a Widget skinned `Kenshi_EditBox`: its text box covers the panel, not
  the template's Client ([MyGUI](../formats/mygui.md)).
- **Placement**: the start and import windows at 0.5, 0.05 of the screen (0.6, 0.05 for the start window from the in-game menu); the load and save
  windows centred, with the left edge at the screen's centre while the title exists; the advanced options under the start or import window.
  Meitou mode centres all four (start and import together with their options panel).
  Those windows leave the title's buttons live (another button replaces them; Meitou: modal screens that pass clicks through, `MyGuiScreenHost.PassThrough`).
  Esc closes the top window, else the credits (Meitou).
- **Start window**: "Choose your beginning", "Advanced options", "Begin", "Cancel"; the start's name. A DatapanelGUI in `DescriptionPanel`:
  "Difficulty:", "Cash:", "Play Style:" (not upper-cased) with the record's `difficulty`, "c."`money`, `style` as stored (French keeps "Default",
  "RPG"), then `description` as a wrapped line. Rows: pitch 0.94 / max(1, (int)(40 x panel height / screen height)) of the panel, from 0.03 of
  it; label at 3 % of the width, 96 % wide; value right-aligned in 93 %. First start "1980-gamedata.base" (Wanderer); the arrows wrap.
  The window's Advanced options keep their values between openings and after a load (**Verified**, [character-stats.md](character-stats.md#new-game-advanced-options-that-change-these-mechanics)).
- **Load, save, import**: title "Load game" / "Save game" / "Import game"; buttons "Load", "Save", "Import", "Cancel"; column headers "Name" (fill)
  and "Date" (180 screen pixels, so 1440-line dates are cut off) and "Reset squad positions", not upper-cased. Rows: every folder holding
  `quick.save`, its name as is; the date is the folder's last write in the user's short date and time with seconds; newest first, names compared
  without case and dates by time (`FUN_14047cdb0`). Nothing is selected on opening and the info box (a DatapanelGUI, not a picture) is empty; a
  selection shows Date, File version ("Ancient" when empty), Faction name, Funds ("c."), Members size, Location, Day, at the DatapanelGUI's default
  row count 55 (`FUN_1406fce20`; the start window sets 40, the advanced options 30). The Load button is always live (Meitou's
  Fixes page "Load button", on by default, keeps it off until a readable save is selected).
- **Import** (`FUN_14047bad0`, `FUN_140378a30`): ticks reset squad positions (off), buildings, research, dead NPCs, relations (on), flags 0x1, 0x4,
  0x8, 0x10, 0x20 (0x2 always added); Import is live once a save is selected; Advanced options opens its own options window, filled from the save's
  difficulty block. Importing resets the world as a new game does (no start), restores the clock, money, faction name, town discovery and the player's
  squads (their platoon files copied whole), and, when ticked, the player's buildings and their zones, research, dead and imprisoned uniques and
  faction relations; with the reset tick the squads go to (-43500, 0, -6625). The full rules: [save.md](../formats/save.md#importing-a-game-savemanagerimportgame-fun_140378a30). Meitou: built (`SaveImport`); the Advanced options take the selected save's difficulty when opened or when a row is selected while open.

### New game

**Verified** (decompilation: `FUN_14036cb80` and `FUN_140872e00`, `resetGame` and `newGameWithCharEdit`, names from KenshiLib 1ce4a16; relations, start
towns, discovered towns, research and scatter also **Observed** in the original against Son of a Captain, Nobodies, Holy Nation Citizen, The Freedom Seekers,
Empire Citizen and The Cannibal Hunters). The "Meitou" sentences are engine choices, not claims about the original.

- **Relations**: a new game's relations start from the data's (every faction is made anew, `FUN_1409ef230`); each `faction relations` entry then sets that faction's
  relation with the player, both ways, to its v0.
- **Place**: `force start pos` gives (`start pos X`, `start pos Z`). Otherwise a town is drawn among every placement of every listed `town`
  (so a record placed many times, like Waystation, gives any of them). A list with no placed town gives the message "Cannot find any towns from the
  game start town list.", then, as an empty list does, a draw among the TOWN and VILLAGE placements whose faction is real, not BANDIT and not hostile
  to the player (-30 or below); with none, (-43500, -6625). Meitou stands the squad on a walkable spot near that place.
- **Discovered**: every town of type TOWN in the biome group of the start place.
- **Squad**: a SQUAD_TEMPLATE entry makes a squad of its own; a CHARACTER entry makes v0 characters (0 counts as 1), each at the place plus up to 50
  on x and z, joining the player's current squad. Nobody made: the Wanderer squad `45550-gamedata.base`. Meitou makes one squad of them all, each
  template keeping its own layout.
- **Research**: the start's `research`, or `5359-gamedata.base` when it lists none.
- **Clock**: day 1 (set by the reset), 10:00.

### Loading screen

**Verified** (decompilation: constructor `FUN_14091a280`, show/hide `FUN_140916ca0`, picture `FUN_1409163e0`, tip `FUN_140913640`, per frame `FUN_1409133d0`):

- The layout is used as it is; nothing is moved or resized but `LoadingText`.
- `LoadingText`: the catalog's "Loading" upper-cased (through the global locale) and left-aligned. It is placed so that the text with two dots is centred
  horizontally in `LoadingPanel`. One dot is added per second of real time, back to none after three. Its font `Kenshi_UI_Messages` (MailartRubberstamp)
  has only the codes A-Z and the dot.
- Tips: every non-empty line of `tips.txt` (resource group General, so a locale's own copy) is kept, with a written `
` read as a line break. Each
  showing puts one at random in `MessageText`, or nothing when there are none. `MessageText` wraps through its template (`Kenshi_GenericTextBox`).
- Picture: each showing takes a random group, `Int(0, n − 1)` with the global `rand`, of the image set `Kenshi_LoadingScreen`
  (`kenshi_images.xml`). The group is set by name and MyGUI resolves a name to its first group. The base data names the `loading_screen9` and
  `loading_screen10` groups `LoadingScreen6`, so vanilla shows `loading_screen1..5` at 1/8 each and `loading_screen6` at 3/8. Pictures 0, 9 and 10
  ship but never show.
- Hiding is immediate, with no fade.
- The bar at the top right is the background loading bar below, not part of this window.

### Background loading bar

**Verified** (decompilation, the resource loader's update `FUN_14044bf40`; **Observed** in a vanilla screenshot, also without RE_Kenshi):

- Widget: a `ProgressBar`, skin `Kenshi_ProgressBarFill`, created once by `createWidgetRealT` at real coordinates (0.9, 0, 0.1, 0.02), align
  Left Top, layer `Top`, no name. So it sits at the top right over the loading screen and over the game alike.
- Counters: each update counts the loader's pending requests (two queues). A rise adds to a total, a fall adds to a done count, and both reset to 0
  when nothing is pending. The bar's range is the total and its position the done count.
- Visibility: the bar shows when the total is above 0 and a wait counter is 0, tested before the counter steps. The counter goes back to 6 when the
  total is 0 and counts down otherwise, so the bar appears after six updates with work pending and hides as soon as the queue empties.
- A second pair of counters over two other queues is kept the same way but not drawn.
- Meitou (`ResourceLoadBar`, Faithful mode): stepped once a drawn frame. Pending means the objects' plus the characters' loads (engine choice: the
  original's queue holds the meshes and textures it loads in the background; terrain and foliage streaming are left out). The characters' part counts only
  the looks the renderer is going to build (the build step's own rule: near the eye or a kept point, or a portrait), not those of far NPCs it leaves
  unbuilt until the eye comes near, which would keep the bar up for the whole session. It is drawn last on the
  loading screen and over the in-game UI.

## 4. HUD (MainBarGUI)

### Portrait cells

**Camera** (**Verified** by decompiling the portrait render `FUN_14084b890`). The character sits alone at the origin of a separate scene,
unrotated (it faces +Z), in its `idle stance` at time 0 with the `postures`, `shoulder set` and `neck set` layers at the appearance's
percentages. The camera looks at the `Bip01 Head` bone plus the race's `portrait offset x/y/z`. The offset is scaled by the character's
size when it has a race. The eye is `(0, 0, portrait distance)` turned first about X by a pitch, then about Y by a yaw, both from the
character's `portrait serial` `s` (an unsigned int):

- yaw = (s mod 500 / 1000 - 0.25) π, so −45° to +45° about the front;
- pitch = (s mod 252 / 1000 - 0.083) π, about −15° to +30°.

The camera keeps a fixed Y yaw axis.

**Where the serial comes from** (**Verified**):
- A new character gets `(int)(random[0,1) × 4e9)` (`FUN_1406223f0`, Character `+0x664`).
- Loading reads GAMESTATE_CHARACTER `portrait_serial` (`CharacterAnimal::vfunc_18`), or takes the handle's serial when the key is missing.
- Saving writes it back (`FUN_1406280a0`).

**States** (**Verified**, `FUN_140414dc0` sets the flags, `FUN_140415150` applies them to the `Kenshi_PortraitCharacter` cell). The cell has
`BackgroundImage`, `BackImage`, `PortraitImage`, `FrontImage`, `NameText` and the `BorderPanel` button. The image set is
`Kenshi_PortraitImage` in `kenshi_images.xml`.

| Flag | Set when | Shown as |
| --- | --- | --- |
| 1 | the character is selected | the `BorderPanel` button's selected state |
| 2 | the character is ≥ 1000 units from the camera and ≥ ~316 from the camera's focus, and either (a) it is not down (slot 6), the float at its AI `+0xb0` is > 0 and it is not imprisoned, or (b) `+0x279 & 7` (being eaten, [Health word choice](#tooltips)) | `Background` `Hurt` (red) instead of `Normal`, flashing |
| 4 | dead or unconscious (slot 107: `+0x5bc` / `+0x5b9`), carried (`+0x3d4`), or in a bed (`+0x2f8 == 1`) | `PortraitImage` alpha 0.25 (faded); this also stops the flash |
| 8 | in combat mode (`FUN_140436610(c, 1, 1)`, **Verified** disassembly `1404150bc`: a melee fight, or shooting or manning a turret at a target that is up; [Combat mode](#4-hud-mainbargui)) | `BackOverlay` `Combat` |
| 0x80 | hunger (`+0x4b8`) < 1.25 | `BackOverlay` `Hunger`, only when not in combat |
| 0x10 | the inventory holds, in section 9 or 5, an item whose slot 94 answers non-null (`FUN_1405c8a10`). Only `LockedArmour` overrides slot 94. The item loader makes a `LockedArmour` for ARMOUR with `is locked` (`FUN_140580750`). That is, shackles. | `FrontOverlay` `Slave` |
| 0x20 | imprisoned (`+0x2f8 == 2`, cage or restraint) | `FrontOverlay` `Prison` (wins over Slave) |
| 0x40 | `+0x279 & 7` | `FrontOverlay` `Eaten` (wins over both) |

- **Flash** (PortraitData `+0x28`, a direction of ±1). When flag 2 turns on, the direction starts at −1. Each frame the background's alpha
  changes by direction × frame time × 2. At 0 the direction flips to +1, and at 1 it flips back to −1. That is a full 0 → 1 → 0 cycle
  every second. Without flag 2, or with flag 4, the alpha stays at 1.
- A portrait is re-rendered when its flags change, when its character changes, or when PortraitData `+0x30` / `+0x58` is set.
- A cell wider than 94 px switches `NameText` to a larger font.

Meitou (`PortraitBar`):
- The serial is `CharacterCold.PortraitSerial`. It is read and written as `portrait_serial` (or taken from the handle's serial), and rolled at spawn otherwise.
- "In danger" is taken to mean "in a fight". Being eaten is `MedicalState.EatenCountdown` (the original's counter, set by each bite, one less per medical tick).

- Shortcut buttons INV, STA, MAP, TEC (research; user-requested caption; installed layout says TCH, **Verified** by reading `Kenshi_MainPanel.layout`), SQD, HLP (its click runs the same toggle as F1, the Tutorials window: FUN_14072d3b0 delegate to FUN_1406e1f20, **Verified** by decompile); floor panel with text "Floor {1,num}"; four speed
  buttons ([game-loop.md](game-loop.md#speed-and-pause): pause, 1x, 2x, 5x); money / day / time panel (money `c.{1,num}`; the clock text is described in [game-loop.md](game-loop.md#frame-clocks), money in [economy.md](economy.md#units-stacks-and-traders)); squad tab control; medical panel; orders panel;
  information, tooltip, PAUSED, loading and build panels; town / outpost panel; biome name panel with fading text
  ([Biome banner](#biome-banner)).
- Medical panel (the body parts it shows are in [character-stats.md](character-stats.md#hunger-blood-and-parts)): nine `LifeBar1`..`LifeBar9` widgets in the layout but the code looks up `LifeBar10`, giving the
  MyGUI.log warning "Widget with name 'LifeBar10' not found. [Kenshi_MainPanel.layout]" (**Verified**: log line
  and `Kenshi_MainPanel.layout`, which has LifeBar1..9 only). Life-bar skins in `data/gui/skins` (**Verified**):
  `Kenshi_LifeBarSkin`, `...HurtSkin`, `...HealedSkin`, `...StunnedSkin`, `...CrushedSkin`, `...RobotSkin`,
  `...SplintSkin`; each `LifeBarN` holds the layers `LifeBarNGrey` (Stunned skin), `...Red` (Hurt), `...Yellow` (Healed), `...White` (Splint),
  `...Green` (current), `...Crushed`, `...Robot` (**Verified**, layout).
- Text panels (**Verified**, decompile of the HUD update and its line-panel class "DatapanelGUI", 2026-10-14): `NameText`, `StateText`, `StatsText`
  and `ExtendedStatsText` are empty widgets in which the code builds a list of lines, each a label (from 3 % of the width, 96 % wide) and a value (same
  start, 93 % wide, aligned right), in `Kenshi_TextboxStandardText` (Exo2 SemiBold 20, default colour #140806, the palette's Secondary). Colours are
  inline `#rrggbb` from `data/gui/colours/kenshi_colours.xml`: Main #afa68b, Title #492620, Bad #59231a, BadBright #bf4834, Good #a8b774, Alert #000000.
- Text ink and UI mods: in the base game the text colour of `Kenshi_TextboxStandardText`, `Kenshi_EditBoxStandardText`, `Kenshi_TextboxStandardText_Large`
  and the `SimpleText` part of `Kenshi_GenericTextBoxSkin`, `...FlatSkin` and `...FlatSmallSkin` is #140806, the palette's Secondary (**Verified**, read from
  `data/gui/templates/kenshi_templates.xml` and `skins/kenshi_skins.xml`). The "Palette UI" mods (Dracula, Catppuccin, Tokyo Night, ...) recolour these
  templates and skins together with a dark `Kenshi_UI.png` but leave Secondary unchanged (**Observed**, PR #114). Meitou's code-drawn text therefore takes the
  colour of the template it sits on when that differs from #140806, else the palette's Secondary, so palette-only mods still apply (`MyGuiColours.TemplateInk`).
  - Name: the character's name left and its faction's name right (the player's default faction name is "Nameless"), both Title.
  - `HealthText` words in priority: Dead, BEING EATEN ALIVE!!, Dying, Unconscious (N) (N = (int) of the medical float `+0xa0`, the KO timer, taken
    only while it is &gt; 0; "Rebooting" for skeletons; `FUN_14088a8f0`), Recovery coma, Playing dead..., Blood loss KO, Bleeding out,
    Crippled, Crippled arm, Critical, Limb trauma, Staying low, Starving (hunger &lt; 1), Bleeding, disguise words; else by the overall health h:
    Hungry (h ≥ 0.7 and hunger &lt; 2), Wounded (h &lt; 0.7), Slightly Injured (h &lt; 0.9), Normal. h is medical `+0xa4 worstDamage` (**Verified**: `FUN_14088a8f0` tests it against 0.7 and 0.9), the worst part's
    fraction ([character-stats.md](character-stats.md#hunger-blood-and-parts); a leg at 77 % and "Slightly Injured" in a screenshot, **Observed**).
- Medical rows (**Verified**, decompile): Blood (Oil for skeletons) first, then the body parts, Hunger last; each `LifeBarNDatapanel` gets the name centred
  and an arrow string right-aligned in Alert; the number goes in `LifeBarNValue`. Layers are sized as width fractions from the left (Grey is a full-width
  background). Part (H flesh +0x40, S stun +0x44, D bandage +0x48, R rig +0x4c, Mx maximum; **Verified** `FUN_14088a8f0`): Green (H − S)/Mx, Yellow
  (H + D − S)/Mx, Red (Mx − S)/Mx, White clamp(green + R × 0.01, 0, 1) with the unclamped green (drawn past the green only), number floor(H − S + 0.1).
  The row setter (`FUN_140721a70`, its arguments read from the call's disassembly at `14088c075`..`14088c0e1`, **Verified**) also takes a robot flag and the limb state
  (`MedicalSystem::getLimbState`: 0 original, 1 stump, 2 replaced, 3 crushed): `Robot` is shown at the green's width when the race is robotic or the limb state is 2;
  `Crushed` is shown at full width when the state is 1 or 3; a stump's White is 0. The Blood row passes RACE `is robot` (race `+0x7c`) as its robot flag and limb state 0
  (**Verified**: disassembly of the call at `14088aada`..`14088abc5`), so a robot race's row (label "Oil") shows `Kenshi_LifeBarRobotSkin` (grey-brown, about RGB 112,107,102 in
  `Kenshi_UI.png`) over the olive green (`Kenshi_LifeBarSkin`, about 126,121,81).
  Arrows, first match: destroyed (H + D − S ≤ −Mx) `!!!   `; H + D < 0: not vital and not robotic `<<   `, vital and below the KO point `<<!!   `
  (none at or above it), robotic non-vital none; H + D ≥ 0 with Mx − H − D > 20 (not a robotic non-vital part) `<   `; else none. The rig is in none
  of these tests, only in the white layer. Blood:
  green blood / capacity, number floor(blood + 0.1), arrows a run of `<`, ceil(|bleed rate| / 0.05) of them (at most 100). Hunger (0..3): number
  (int)(hunger × 100), Green hunger − 2, Yellow hunger − 1, Red hunger; arrows, first match: `>   ` stomach (+0x64) > 0 (digesting), `<!   `
  hunger < 2, none in a bed (character `inSomething` +0x2f8 = IN_BED), else `<   `. A `<<!!   ` branch for hunger < 1 sits after the < 2 test
  and is unreachable. The row is skipped for robot races (race +0x7c, KenshiLib 1ce4a16, Observed). The parts come in the
  medical part array's order (part order, **Verified**): head, torso, arms, legs, left before right.
- Information panel: `ExtendingPanel` starts collapsed, its bottom moved onto the panel's bottom (behind the Overlapped `StatsPanel`), and slides over
  0.5 s to its layout place when `ExtendedStatsButton` is pressed (**Verified**). `BountyPanel` shows only for one selected character
  (`MainBarGUI` `FUN_1407211b0`, **Verified**) that is Diplomatic Status, has a bounty (`FUN_140853740` 1 or more), a slave state, leads a war campaign, has a pending crime
  (`+0x148`), the escaped flag or the bare kidnap victim flag (`+0xe9`, carried or not). Meitou: `TooltipRuntime.ShowsBountyPanel`.
- Status entry (section 0x10 of the same builder, **Observed** by decompile), first match: the leader of its campaign (`Character::isLeadingAWarCampaign` `FUN_1409c4b10`: its platoon's leader and the campaign's
  leader) "War Leader" ("This is the Commander in charge of an entire army.") when `CampaignData::_isHostile` (+0x12, KenshiLib 1ce4a16), else "Leader" ("This is the leader of this group.");
  kidnapped (Character slot 0x350) "Kidnapped" ("This character has been kidnapped, law enforcers will try to help"); `isDiplomaticStatus` `FUN_140796240` "Diplomatic Status" ("This is a special
  person with diplomatic importance. Anyone who attacks this guy will be in serious trouble with his faction."; `isDiplomaticStatus(factorInBiome)`, **Verified** by disassembly: the
  character's NPC type (state broadcast `+0x1a0` -> `+0xdc`, KenshiLib `NPCType`) is 5 OT_DIPLOMAT (the CHARACTER `NPC class`), and with the flag also
  `FactionWarMgr::isBiomeHomeTerritory` for the biome at its position; this entry passes 0, as do four of the other five callers); then for slave state 0: a pending crime (Character `+0x148` code, `+0x180` timer) "Committing Crime!" with int(timer), text
  "This character is currently committing {1}, make sure nobody witnesses it." while timer <= 3, else "...has been caught committing {1}! He might end up with a bounty on his head if he
  can't vanish." plus " Current bounty is c.{2,num}" with the local faction's bounty (`getActualBounty`) above 0; the escaped flag (`+0xe8`) "Escaped Prisoner", text "... Leave town to
  clear this status. Expires in {1}" with `{1,num} hr(s)` of the constant 99999 (text only: no timer clears the flag, leaving every town does, [locks](locks.md)); a bounty: the bounty entry; else the slave states above. Crime, escaped and slave entries list the bounty rows
  (`FUN_1405d1400`) before their text. "Imprisoned:" belongs to the Lockpicking section. Meitou: `World.StatusOf`, `PlayerInterface.StatusLine` / `StatusRows` / `SlaveRows` (slave hints for
  player characters only; engine choice); the entry's title is the `BountyText` line; Diplomatic Status is `CharacterCold.NpcClass` Diplomat.
  - `BountyText`: `Bounty:` in Title and the local bounty in Main; not wanted locally, both Greyed with the *largest* bounty (`FUN_140853740` takes the max, not the sum).
  - Tooltip (attached to `BountyPanel` in `FUN_14072d3b0`), rows per faction: name and `c.{1,num}` (Main for the local one, else Greyed); `-Wanted For: ` and the first
    four crimes joined by `, `, a row keyed `" "` with the rest (untagged for the local faction, Greyed otherwise). Then, imprisoned: "Imprisoned, sentence time:" `FOREVER`
    (length >= 2000), "Imprisoned, sentence time remaining:" ceil(length - elapsed) `{1,num} hr(s)` (both values Title), or "Imprisoned" / "Waiting to be released";
    otherwise a `-------------------------` row and "This character is wanted by the authorities. Turn them in for a reward." with, after a line break, `FUN_1405d1110`:
    "The bounty will expire in {1,num} hr(s)" (int(0.04 × amount − elapsed), Main), "Notorious: The bounty will never expire." (amount > 9999, Title) or "This character
    is not wanted by the local faction" (Main). Meitou puts the expiry on a row of its own (an inline tag only leads a row) and, in the Meitou UI, under each faction's rows.
- Squad tabs: one per player squad with members, captioned with the squad's name unchanged (**Verified**); the first player squad reads `Nameless_0`,
  its platoon's name (the faction's name and its platoon counter; **Observed**). Tab width min(header width / tabs, ButtonWidth × UI scale), in the skin's
  `HeaderPlace`, whose 37 px height also scales: the tabs are about 49 px tall at 1440 lines (**Observed**, owner screenshots 2026-11-07,
  same save in both games; the caption font matched). Meitou: the HUD's `TemplateScale` is height / 1080. The bar shows the active squad (`FUN_1407f5520`: a cached platoon at player interface +0x2a8, else the selection's, else the faction's
  first; **Observed**); a tab makes its squad active. The tab refresh (`FUN_140416140`, **Verified**) rebuilds the row from the squads with members and a valid
  platoon link only, so an empty squad has no tab even when active; first, when the active squad has no members, the first squad in order with members becomes
  active (none with members: it stays, no tab is checked). Meitou: `SquadManagement.KeepActive`, each slow tick; selecting a character of another squad makes its squad active (engine reading of the cache).
  Editing squads: [Squads tab](#squads-tab).
- `FloorPanel` stays as laid out and `FloorText` is "Floor {1,num}" of the viewed floor each frame; `BuildPanel` is always shown (**Verified** /
  **Observed**).
- Speed selector: the `SpeedPanel` ImageBox shows an image of `Kenshi_CharacterSpeedSet` / `Icons`, indexed Walk, Jog, Run, Group;
  the index is the movement mode ([pathfinding.md](pathfinding.md) "Speed"). Each arrow steps it by -1 / +1 with wrap over all four,
  plays `General_Click` and applies the mode to every selected character; a refresh shows the lead's mode. Right after the panel is
  built the index is 0 (Walk) until the first refresh. **Verified**: `kenshi_images.xml`, the panel's name list in FUN_14072b540 (exe bytes
  `Run`/`Jog`/`Walk`), arrows FUN_140722580 / FUN_140722630, apply FUN_140722500, refresh FUN_140726930. While any selected
  character's posture (+0xe0, vfunc 0x368 = FUN_1405c7e10) is 1, `PS_STAYING_LOW`, the arrows do not step (FUN_1407f3a20, **Verified**): the displayed (lead's) mode is
  applied to everyone, the low uncrippled ones standing up instead ([ragdolls](ragdolls.md#staying-low)). Meitou: `RunSpeedCommand` in `Orders`.

### Town panel

`TownPanel` of `Kenshi_MainPanel` (bottom right; class `TownPanel`, MainBarGUI +0x128, KenshiLib 1ce4a16). **Verified** by code unless noted:
- **Widgets** (constructor `FUN_140724e10`): `OutpostNameEditBox`, `OpenToPublicCheckBox`, the `Research` / `Power` / `Battery` `ProgressBar`s (range 100) with their
  `...Caption` boxes; the labels get `Open to public:`, `Research:`, `Power:`, `Battery charge:` through the translator.
- **Town change** (`FUN_140722de0`): the name box gets the town's name, the tick its +0x380; both are enabled only for a town whose faction is the player's (+0x250).
- **Each frame** (`FUN_140727380`): Research = the research manager's current research (+0x58) name and its progress x 100, else `None` and 0. Power = the town's +0x474 /
  +0x47c x 100 (+0x47c <= 0: 100 when +0x474 > 0, else 0), caption `{1,num}/{2,num}` of (int)+0x474, (int)+0x47c. Battery = +0x48c / +0x490 x 100, caption (int)+0x48c /
  (int)+0x490, then ` >>` when +0x488 > 0, else `<< ` before it when +0x484 > 0 (exe strings). Field meanings: [Power](buildings-production.md#power).
- **Actions**: the tick flips Town +0x380 and plays `General_Click` (`FUN_140721e50`); the name box's accept sets the town's name (`FUN_140722d60`), no other check. +0x380 is
  the town state's saved `public` ([outposts](../formats/save.md#player-outposts)) and makes the town a trader destination and stay ([ai](ai.md#package-classes)).
- Meitou (`TownPanel`): the subtree is registered as a screen panel (drawn under the windows, not raised, skipped by Esc and Tab) so the box takes typing; the
  position is the camera pivot; Enter or losing the focus renames (`RenameTown`; an empty name is ignored, engine choice); the tick sends `SetTownPublic`; both only for
  the player's own outposts (`PlayerTown.Public`, saved as `public`). A public outpost is a wandering trader's destination as a public placed town is, and keeps it for its stay.

### Biome banner

**Verified** (`MainBarGUI::vfunc_10` `140728d50`, the HUD's per-frame update; save `FUN_140728af0` / `FUN_140728c10`) unless noted. `BiomeTextPanel` (full width, 0.24 to 0.36 of the screen height, hidden in the layout)
holds `BiomeTextBox` (`Kenshi_BannerTextFont`, centred, shadowed) between `BiomeNameTopLine` and `BiomeNameBottomLine`.
- **Track.** While the LoadingWindow is up (`FUN_1406e2100`) the stored group is only set to the one under the pivot (a load announces nothing). Else, each frame with dt the real frame time (`DAT_142133790`, [game-loop](game-loop.md#frame-clocks)): `hold -= dt`, `cooldown -= dt`.
- **Fade.** Visible: alpha + 2 dt up to 1 while `hold > 0`; once `hold <= 0` alpha - dt, hidden below 0. On hiding the game stores the group and the time and tests its `arrival dialog` (`FUN_1408015d0` -> `triggerAreaArrivalDialogue`: squad banter, [dialogue](dialogue.md#arrival-banter)).
- The stored group (`+0x1d8`) follows the pivot's every frame unless the `+0x128` test blocks it, so a region crossed during the cooldown is never announced.
- **Save.** CAMERA `biome_text_timer` = the cooldown, `biome_text_groud` = the stored group's string id (empty: none), set back on load. `area` (`FUN_140375360`) = the name of the group under the pivot, empty for `NONE`.

### Messages

Meitou (`MessageRoller`, both UI modes): its own notices (Saved, Loaded, errors) share the queue, errors in `Bad` red; always at the HUD's spot; real-time timers.

### Prospecting

**Verified** unless noted. `OrdersProspectButton` (`FUN_140721800`): with the window visible it hides it; else the selected character nearest the camera pivot (`FUN_1407f4ba0`, **Observed** over the selection) that has no task 150 gets one (`FUN_1405d20d0`,
not through the order dispatcher: no `Right_Click`) and `Prospect_Click` is posted. The key action `prospect` is unbound by default ([ui-input](ui-input.md)).
- **Task** `Task_Prospecting` 150 ([ai-tasks](ai-tasks.md)): the character stands, plays `medic`, progress += its update time until 1.0 (one game second; bar text "Prospecting"); not while swimming; no XP.
- **Result** (`FUN_14048e9e0`): at 1.0 the window gets the spot, the prospector's science (`CharStats +0xe0`, raw) and name. `FUN_14049f180` shows `Kenshi_ProspectingWindow` centred, titled "Prospecting Results", posts `Prospect_Results`, and
  `FUN_14049d850` fills it. With s = science / 100:
  - `ResourcesList`: a `Kenshi_ProspectingWindowResourceLine` per resource in the order Water (5), Fertility (6), Stone (2), Iron (1), Copper (3); Carbon (4) is not listed. Caption the name, `ValueText` `int(100 x v)` with v = `uses_resources`
    ([buildings-production](buildings-production.md#resources)) at the spot, coloured `#a8b774` above 0.75, `#59231a` below 0.5, else `#140806`. The rows are radio `CheckboxButton`s, the first checked.
  - `Map` (`FUN_14049d850` sets it up, `FUN_14049cd60` draws it; constants from the disassembly): an image `int(s x 246 + 6)` pixels a side over the square of half-size `s x 6000 + 2000` units round the spot, rows from -Z, each pixel
    sampled at its cell's centre with the resources record of the spot's group (looked up once, `FUN_140a12ff0` reads the record it is passed). Ground: grey `(h - 500) / 9500 x 255` for terrain height h above 500, else black. Water, Fertility, Stone: where h >= 0 and `uses_resources` v > 0.01 (capped at 1), with a = (1 - v) v and g the
    grey, red `(1 - a) g + 255 a`, green `(1 - v²) g + 255 v²`, blue g (bytes B, G, R of `PF_R8G8B8` / `PF_A8R8G8B8`). Iron, Copper: each rock of the resource within 0.6 x the side of the spot paints its pixel (and the three to
    +X/+Z when the image is over 40 wide) red 255 a, green 255 v², blue the grey there, v its own yield (`vfunc_92`) clamped to 0..1. Redrawn for the checked row. `CameraMarker` (`FUN_14048c970`, every frame while the window shows, from `FUN_1406ea070`, and at the fill): centred
    on the position (`Node::getPosition`) of the camera's centre node, the pivot it orbits (`DAT_142133310` is the `CameraClass`, +0x58 `center`), mapped through the map square to the image's pixels;
    turned (`RotatingSkin::setAngle`) by the angle between `NEGATIVE_UNIT_Z` and the camera's facing (`FUN_1406ae860` on the player interface's camera +0x30: centre minus the camera's node position, y zeroed,
    normalised), negative when the facing's x is below 0. **Verified** (disassembly); the class and field names KenshiLib 1ce4a16 (**Observed**).
- The close button hides the window; hiding frees the rows (`FUN_14048d780`).

Meitou: `ProspectOrder` makes `CharacterTask.Prospect` ([simulation](../simulation.md#prospecting)); the window is the faithful screen `prospect` in both UI modes; the report carries the wind and the map, the marker follows the camera's pivot and heading every frame.

### Selected building

- **HUD panels.** The text panels are DatapanelGUIs bound to the focus object (`FUN_1406e91e0`), so a selected building (a house: its first
  door) fills the panels a character would. Each panel has a section number (HUD constructor `FUN_14072d3b0`, panel slot 24): `NameText`
  ("Name") 5, `StateText` ("Info") 3, `StatsText` ("Stats 1") 6, `ExtendedStatsText` ("Stats 2") 7, `MedicalPanel` 0x10. A panel
  asks the object's slot 40 for its section (`FUN_1406fa7f0`). Building slot 40 (`FUN_140301840`) answers:
  - 5: slot 113 (`FUN_1402fdea0`): the building's name left; right the owner faction's name, the player's faction always, another
    faction only when the building is in a town that passes slot 0x340 and town+0x111 is clear, else empty.
  - 3: `Type:` the record's name when it differs from the building's name, `Town:` the town's name, then the upgrade lines (slot 116, generic
    `FUN_140303030`, **Verified**): with an upgrade target ([upgrades](buildings-production.md#upgrades)) a full-row button `[UPGRADE]` (Good:
    colour tag + "[" + the uppercased translation of "Upgrade" + "]"), whose tooltip is a list of pairs `Upgrades To:` target name, a row per
    material still owed (the target's required less the current state's delivered, > 0: six spaces + item name, `+` amount) and, for a building
    with power, `Power` with the `power output` change (Good, Bad when negative); without a target the line is removed (`FUN_1406fd460`). With a
    downgrade target while the construction state is not complete, `[DOWNGRADE]` (Main) with the tooltip `Downgrade To:` name. WallBuilding's
    slot 116 (`FUN_1402ff760`) has only the upgrade, keyed `Upgradeable to:`. Then the construction buttons (slot 117 `FUN_1402fea10`, shown when slot 79 `FUN_14029c380` is
    true, **Observed**: the owner is the player's faction and `isFoliage` (+0xc0, KenshiLib 1ce4a16) is clear; then true when the building it is furniture of (slot `0x438`) is
    the player's; else false only for a building with no such parent (slot `0x1d8` handle of type 11) whose town (slot 15) exists, is not the player's and whose record has
    `creates player town`): a full-row button `Dismantle` (caption BadBright) and, while the construction state is not complete, `PAUSE CONSTRUCTION` (Main), captioned
    `[PAUSED]` (#777777) while paused. Dismantle is slot 84 (`FUN_14054db30`, `General_Click`): "You cannot delete this building while it has mounted
    buildings."; items in an inventory: "There are still items in the inventory, they will be lost.\nAre you sure you want to destroy this building?" (a
    research building with items refuses: "You cannot delete this building while there are still items in its inventory.\nPrecious research artifacts and
    stuff, don't lose them."); furniture: "Dismantling this building will destroy all furniture.\nDo you want to continue?"; else the message box
    "Dismantle" / "Are you sure?". Yes (`LAB_14002b666`: a jump to slot 86 `Building::confirmDismantle` `FUN_14054fea0`, answer 2; **Observed**): the interior object's buildings are destroyed
    (`FUN_14054f4d0(+0x1f0, 1)`, by its string `_destroyAllInternalBuildings`), then slot 73 (`addDismantleProgress`) with `FLT_MAX`, so it is dismantled at once with the [refund](buildings-production.md#work-distance); then
    slot 25, each building of `FUN_14054b9d0`'s list is destroyed ("Building::confirmDismantle"), the selection is cleared if it held it, and the town update `FUN_14054d080` runs. Pause is slot 85 (`FUN_140546f40`): flips
    the paused byte, `General_Click`.
- **Workers** (`FUN_1403075b0`, **Verified** strings and branch): with raw `max operators` (+0x3ac) 0, `Workers:` `-` with the tooltip "This machine
  is fully automatic." (wind generators); else `{1,num}/{2,num}` operators / +0x3ac, tooltip "The machine is fully staffed." when equal, "The machine is
  under-staffed." (Title tag) when fewer, none when more; the value `#140806`, `#59231a` (Bad) when nobody operates it and +0x3ac >= 1.
- **Door** (DoorStuff slot 40 `FUN_140304120`), then the house's own lines of the same section: 4 `Door Condition`, the number int(progress) of the
  door's own construction state, tooltip "The door is in good condition." while that number is not negative, else "The door is damaged." (Title);
  6 `Open/Close:` with "The door is open / closed / broken" and `Security:` `Locked` / `Unlocked` and ` (lvl {1,num})`, with tooltips ([Tooltips](#tooltips)).
  Value colours: open, closed and Unlocked in `#a8b774`, broken, a closed door that is locked, and Locked in BadBright. For the player's own door both
  lines are `Kenshi_Button2` buttons: Open/Close runs `FUN_140546fb0`, Security `FUN_140547060` ([locks](locks.md#doors); **Verified**: the
  delegates' thunks `140018c69` / `140051faa` jump to them, exe bytes); for others plain text. `Kenshi_Button2` is a template, not a skin: its root
  widget has `Kenshi_Button1Skin` (**Verified**, `data/gui/templates/kenshi_templates.xml`). Meitou: the same lines, the buttons on the player's own
  finished building (`ToggleDoorsOrder` / `ToggleDoorLocksOrder`), over the whole row with no label. The original's button width argument
  (1.0 here, 0.3 for the public toggle, 0.4 for For sale) is the button's share of the row, at its right; at 1.0 the key is not shown
  (**Observed**: the owner's screenshots of a Defensive Gate III and a bought town house, `HudLine.ButtonWidth` in Meitou). Meitou engine choices: a moving door reads by where
  it heads (opening: open, closing: closed), and an open door that wants its lock reads `Locked` with "The door will lock when it closes.".
- **Production building** (**Verified** decompile of ProductionBuilding's slots, the strings `OK` / `-` / `+` read from the exe bytes; the formulas match the owner's
  screenshot of a worked Iron Resource: Iron Quality 100, Workers 1/3, Efficiency 40%, Max efficiency 120%). Section 6 (`FUN_1402ffeb0`) is slot 186 (richness),
  155 (Workers, `FUN_1403075b0`), 154 (`FUN_140306050`), 153 (power, `FUN_140307bf0`); section 4 is slot 114 (header word), 115 (Condition rows), 153.
  - Slot 154 (shared by Storage, Research, Furnace, Crafting, Turret, Light, Generator, WindGenerator, RainCollector and Torture buildings; farms below): share =
    operators / raw `max operators` (1 when 0), work = slot 109 (health × power) × share × 100, max = slot 119
    (`FUN_140546d10`: yield slot 92 × `FUN_140297b70`, `output rate` +0x1a8 × the research production mult). `Efficiency:` int(max × work)`%`, value `#140806`
    when work >= 90 else `#59231a`; `Max efficiency:` int(max × 100)`%` in `#140806`; labels Main. Both carry a rows tooltip that starts with slot 156:
    ProductionBuilding's `FUN_14030a130` (also Farm, Furnace, Crafting, Generator, WindGenerator, Torture): `Max possible efficiency:` int(rate × 100)% (Main) and, for function 1 / 27 with a resource, `Iron / Stone / Copper / Carbon / Water
    under ground:` or `Ground Fertility:` with n = int((1 − yield) × rate × 100) shown as `+` −n% when n < 0, else −n% (`#59231a` when yield < 1, else `#140806`);
    Useable / Storage / Light / Research `FUN_140305d70` only the rate row, Turret `FUN_14030cb40` only `Max possible efficiency:` int(slot 119 × 100)%,
    RainCollector `FUN_14030b460` only `Rain strength:` int(r × 100)% (Main, Bad at r <= 0), r (`FUN_140298400`) = the `wetness` (WEATHER loader
    `FUN_1409de7b0`, +0x94) of the current weather of the region at the building when slot 59 is null or +0x240 is set and that weather's `affect type` is not 2
    (acid), else 0. RainCollector's slot 109 (`FUN_140298470`, **Verified**) is r × the common slot 109, so its Efficiency and its production (slot 150
    `FUN_140298700` reads slot 109) are × r; Max efficiency is not. Its slot 114 (`FUN_140301410`): when not broken, complete and not output full (status 1),
    `Collecting` (Good, "Rain is being collected.") while r > 0, else `Not Raining` (Bad, "It is not raining. Nothing to collect."); otherwise the common chain.
    Efficiency then adds `Damage:`, `Power:` (only for a consumer, `power output` < 0) and `Num workers:` (raw max = operators), each `OK` (Good) or `-`
    int((1 − kept) × rate × 100)% (Bad), and `Total:` the line's value (Main); Max efficiency adds only `Total:`. Slot 109 (`FUN_140296e00`) = 0 when slot 164
    fails, else the construction fraction (`FUN_140547200`, progress / target capped at 1) × granted / demand (1 without demand). Slot 164 (**Verified**) is
    `FUN_1400f6d50` (always true) on every class but LightBuilding, whose `FUN_140297890` is true when slot 59 (`isIndoors`, KenshiLib 1ce4a16; [lights](../formats/lights.md#when-a-light-is-on))
    is set, else only while it is not daytime (`FUN_14066cb20`, sunrise < hour < sunset): a lamp outdoors shows Efficiency 0% by day (and draws no power, slot
    163 `FUN_140299630` asking slot 164 first). The Damage and Power rows read their own values, not slot 109.
  - Who shows it (**Verified**, each class's slot 40): ProductionBuilding's `FUN_1402ffeb0` (also Generator, WindGenerator, RainCollector, Torture), Research
    `FUN_140309680` and Crafting `FUN_1402ff070` in section 6 as 155, 154, 153; Useable / Storage / Light `FUN_140304e90` (only with a town, `FUN_1400f6c20`) in
    section 6 as 155, 154, 153 (power), except for function 6 (BF_BED; slot 94 `FUN_1400f6b30` is the function +0x158, 27 for a mine whose `FUN_1405946f0` fails)
    `Healing bonus:` `x{1,num}` of the float +0x1a4 instead of all three, and for function 7 (BF_TRAINING, +0x158) `Max Skill Level:` int(+0x1a4) − 1 with the
    tooltip "This is the maximum skill a person can train up to using this equipment. For anyone higher it is a waste of time." instead of 154; BF_SKELETON_BED
    (25) gets 155, 154 and 153 and no Healing bonus (**Verified**, the section 6 branch of `FUN_140304e90`). +0x1a4 = `output rate` × `production mult` ÷ raw `max operators` when above 1 (`FUN_14029e460`; Bed 8, Camp Bed 4, dummies MkI–III 6/11/16
    by the probe of the merged data). Turret `FUN_14030c2a0`: section 6 the gun's slot 14 lines then 154, section 7 (`ExtendedStatsText`) 155 then 154; Furnace
    `FUN_14030b750` draws its own section 0xf (`Total Metals:`, the `Active:` toggle) and hands every other section to `FUN_1402ffeb0`, so 155, 154, 153 as a
    production building.
  - Slot 114 (`FUN_140300090`), first match, all but Working in Bad with a tooltip: Broken, Under construction, Switched off (off with demand), No power, Production
    queue empty (status 3), Out of resource (status 0 and slot 149), Output full (status 1), No power (demand > 0, granted <= 0), No workers (raw max > 0,
    nobody), Working with the tooltip of slot 168 (`FUN_1403094b0`, every class; **Verified**): P = `power output` + research increase (`FUN_1402976a0`);
    P > 0 gives share = slot 166 (output now; ProductionBuilding `FUN_1402976e0`: construction fraction × P while `power on` and not an empty battery) / P,
    else demand (slot 163) > 0 gives granted / demand, else none; a share at most 0.9 reads "This machine is working fine, but due to low power is only
    operating at {1}{2,num}% {3}of its full potential." ({1} `#492620`, {2} int(share × 100), {3} `#140806`), otherwise "This machine is working fine.".
    Generator (`FUN_140300b40`) and WindGenerator (`FUN_1403011e0`) states run this word chain first and then override it (a generator reaches Switched off,
    No workers or Working: `FUN_140299370` is true for output > 0 without a store). Generator: `Out of fuel` (Bad, "It has ran out of fuel. Find some more fuel
    and fill it up.") when slot 149, over any word; then, while `power on` and store +0x3c8 > 0 and the building's town has a grid (town slot 0x268): charge
    +0x3c4 at least `FUN_140297650` (store + research, × the construction fraction) `Fully Charged` (Good, "This battery is fully charged."); charge <= 0
    `Empty` (Bad, "This battery is flat, it needs to be charged up."); else the grid's charging flow +0x488 > 0 `Charging` (Good, "This battery is being
    charged."), its draining flow +0x484 > 0 `Draining` ("This battery is in use and being drained of its power."), else `Stored` ("This battery is not in
    use."). WindGenerator: `No Wind` (Bad, "There is currently not enough wind to drive the machine.") when it has no rotor or the rotor's speed
    (`FUN_1404cbd90`) is <= 0, over any word. Slot 166 of a generator (`FUN_140297760`) = construction fraction × P while `power on`, not (slot 149 with a fuel
    inventory) and not an empty store, else 0; a wind generator's (`FUN_140297810`) = rotor speed × that, 0 below 1. So a Working generator's tooltip share is
    its construction fraction (wind: × the rotor speed). Meitou: the tooltip on every Working word (consumers by their grant, generators as above); the
    battery words read the town grid's flows (`Machine.GridCharging` / `GridDraining`, from `TownPower`).
  - Rocks (**Observed**, same screenshot): the name bar's right is the player's faction ("Nameless"); the medical rows are `Condition` 2, full: the target of
    `build materials` 2. Owner (**Verified**, decompile): the foliage system (`FUN_1406d2410`, "building type") makes a rock with no owner, town NO_TOWN (the
    town list's `noTown` +0x180f8, KenshiLib 1ce4a16; `TownNull`, constructor `FUN_140934fe0`: TOWN `NO_TOWN` "Nowhere", explored, owner `nofac` via
    `FactionManager::getEmptyFaction` `FUN_1402e7b00`), so the factory gives it the town's faction `nofac` ("No Faction"). `Task_OperateMachine` slot 1
    (`FUN_14035b430`, the task's start at the machine) on a building with `is foliage` (+0xc0), not a chair, run by a character of the player's faction (+0x250) sets
    the building's faction to the character's (slot 20) unless it is already a player faction, and then, when the town at its position (`FUN_140928890`) is a
    player's, its town to that one. So slot 113 reads the player's faction once a player character starts working the rock; before, "No Faction" (**Verified**:
    every class branch of the factory builds the town hand from the town's own handle, `FUN_1407925c0`, and NO_TOWN's is type 0xd, registered in
    `DAT_142133fc8`, the registry `FUN_1400f6c20` → `FUN_1409f9100` asks for a type 0xd hand; NO_TOWN is explored, +0x181 = 1, and not secret).
    No rock state was seen in the original's saves (**Observed**); Meitou saves a worked rock ([rock machines](../formats/save.md)). Meitou: `ProductionSystem` sets the rock's owner on arrival, its town by the outposts' radius
    (`RichnessView.Owner` / `TownName`); `Town:` shows only that town.
  Meitou (`ProductionWindows`, `MachineSnapshot.Efficiency` and `Panel`): the lines per class as above, unmined rocks too (0 workers), the turret's
  section 7 in `ExtendedStatsText` while the panel is expanded; the turret's gun lines from `WorldSnapshot.InspectedTurret` (`CombatSystem.TurretPanelOf`; a
  player turret's M is 1 as in Meitou's shot); health is `Machine.Health` (progress / target),
  which also scales production (`WorkFactor`) and the sound efficiency; slot 164 is `Machine.Active` (a BF_LIGHT machine outdoors by day, from `World.Daytime`
  which the host sets from its clock, is inactive: no demand, efficiency 0; indoors = `is interior furniture` and a parent building). Mines and rocks get the header Output full / No workers /
  Working (plus the power words) and no `Output:` line; other machines keep Meitou's status words and `Output:` line.
- **Window.** Selecting a complete building of the player's faction opens its window (Building slot 38 → `FUN_1406e6640`; one window per
  building, made on first use by slot 48). Classes from RTTI vtable names, layouts **Verified** (each layout string is referenced only by
  the base constructor listed):

| Building classes | Window class | Layout |
|---|---|---|
| Storage, Useable, Turret, Light (when they have an inventory) | BuildingContainerInventoryLayout | `Kenshi_InventoryGenericWindow` (`FUN_140154cd0`) |
| Farm, Production, Generator, WindGenerator, RainCollector, Torture | ProductionInventoryLayout | `Kenshi_GenericBuildingWindow` (`FUN_140155f90`) |
| Furnace | FurnaceInventoryLayout | `Kenshi_GenericBuildingWindow`, plus `OutputDataPanel` |
| Crafting | CraftingInventoryLayout | `Kenshi_GenericBuildingWindow`, `QueueButton` captioned "Queue...", `CraftingName` |
| Research | research inventory (`FUN_140155910`) | `Kenshi_InventoryResearchWindow` |
| Building (houses), Door, Gateway, Wall | none (slot 48 returns 0) | - |

Generic inventory sizing (**Verified**, Kenshi 1.0.68 decompile `FUN_14014e530`, fixed `FUN_14014e870`): set the grid to cells × skin cell size;
grow the frame only by positive differences from the layout's initial grid. Generic storage and research grids centre in `Content` on both axes;
fixed backpack grids centre horizontally and retain their top offset. No 80%-of-screen height cap or added scrollbar. `BackpackInventoryLayout`
(`FUN_140155d30`) uses `Kenshi_InventoryGenericFixedWindow`; Meitou uses it for player, pinned and looted bags. Ordinary containers use the generic
storage layout, with ARRANGE for unrestricted grids, rather than the research layout. Meitou preserves the frame's natural dimensions even when
larger than the viewport; visible pixels remain clipped to the screen.

Character main-grid alignment (**Verified**, `FUN_14014eee0`): after setting section dimensions, keep `main`'s original bottom edge by shifting its
top and `InventoryMainTitle` together. The character frame keeps its layout size. Meitou applies this before drawing the cells, including pinned
and looted character windows; resizing the grid downwards alone can otherwise overlap ARRANGE.

`Kenshi_InventoryResearchWindow` (**Verified**, layout read): `ResearchButton` "SHOW RESEARCH" over `backpack_content`, `ArrangeButton` "ARRANGE" under it
(HCenter Bottom). Meitou: ARRANGE arranges the bench's grid as an inventory's (`ArrangeMachine`, `InventoryCommands.Arrange`; #639).

`Kenshi_GenericBuildingWindow` (**Verified**, layout read): Window `Kenshi_WindowCX` captioned "BUILDING NAME", layer Window; `CraftingPanel`
(`CraftingName`, `QueueButton` "QUEUE..."), `TitlesPanel` (layout INPUTS / OUTPUT, but the constructor `FUN_140155f90` **Verified** overwrites them with the translated `Input` / `Inputs` by the input count, hidden at 0, and `Output`), `Input1Panel` / `Input2Panel` (`InputNText`, `InputNStatusText`,
slot `inN`, `InputNProgressBar`), output (`out` slot, `OutputProgressBar`, `OutputText`, `OutputDataPanel`). The `inN` / `out` inventory
sections are the recipe item's `inventory footprint width` × `inventory footprint height` cells (**Verified**, `FUN_14029e7b0` code read); a crafting bench's `out` is as large as the largest thing it makes
([Crafting](buildings-production.md#crafting)). The grid geometry is **Verified** by the original layout and the decompiled layout setup routines (Kenshi 1.0.68):

- `FUN_1401536c0` gets the cell dimensions from `Kenshi_InventorySlotSkin` (25 × 25 in the clean GOG skin).
  Meitou reads both dimensions from the loaded skin too, including UI mod overrides, for grids, items and pointer placement.
- `FUN_140155f90` removes the crafting row for non-crafting buildings, moves the titles and contents up, and shortens the frame by that row's height.
- `FUN_140150e20` (production), `FUN_1401524f0` (crafting) and `FUN_140151a30` (furnace) size each section to its cell dimensions, grow its panel by
  the positive difference from the layout slot, stack inputs vertically, and put the output column immediately beside them. The outer frame
  changes by the difference between the initial and final content dimensions; crafting/furnace width never shrinks below the initial frame.
- Grids and bars centre independently in their content panels. A panel's growth moves its bottom-aligned bar with it, retaining the layout gap.
  Output-only production retains one panel column, with its header aligned over it. No previous frame participates in sizing.

Meitou: `InventoryGeometry` resolves real positions once, preserves child alignment during panel growth, then binds the complete geometry before
placing the window. `ProductionWindows` supplies the section dimensions for every production/crafting/furnace window (#671).

Crafting window contents (**Verified**, `CraftingBuilding::updateInventoryWindow` `FUN_1402b6110` and the `BuildInventoryLayout` setters `FUN_14014db60` ... `FUN_14014e330`; field
names KenshiLib 1ce4a16, **Observed**). Each `inN` and the `out` section get a plain `ImageBox` child made by the layout constructor (`FUN_140155f90`): top-left aligned, no mouse
focus, alpha 0.35, depth 1, hidden; given an item it takes the item's icon texture and its footprint size (`inventory footprint width/height` x the cell size) and shows only
while its section is empty, so a faded picture of the expected item fills the empty slot. Per input k: `InputNText` the ingredient's name (`-` with none); `InputNStatusText`
"Empty!" (`#afa68b`) when the section is empty and its amount is 0, "OK" (Good) when empty with amount above 0 or when the first item there is the ingredient, else "Invalid item!" (Bad);
`InputNProgressBar` the amount x 100 (0 with no ingredient); the ghost the ingredient's item, shown when the section is empty and the input is enabled. Output: with a current output
entry (building +0x448: progress, record) whose progress is below 1, the `out` ghost is the Item held by the queue's front entry (`crafting` deque; each queued craft holds the
ready-made Item it will produce; shown while `out` is empty), `OutputText` that Item's name, `OutputProgressBar` progress x 100 and `CraftingName` `#afa68b` + the entry record's
name; otherwise ghost none, `OutputText` `-`, bar 0 and `CraftingName` `#afa68b` + an empty string. The layout's setup (`FUN_1401524f0`) writes "Nothing Queued" into
`CraftingName` (so it shows only until the first update) and `OutputTitleText` the translated "Output" upper-cased.
Meitou (`ProductionWindows`): these texts and ghosts for crafting benches; a section holds only its ingredient, so "Invalid item!" never shows; the output ghost is the queue front's item (its record and model).

### Character stats runtime layout

| Column | Groups and row order |
| --- | --- |
| 1 | Weapons: Katanas, Sabres, Hackers, Heavy weapons, Blunt, Polearms |
| 2 | Combat: Melee attack, Melee defence, Dodge, Martial arts; Ranged: Turrets, Crossbows, Precision shooting |
| 3 | Thievery: Stealth, Lockpicking, Thievery, Assassination; Athletics: Athletics, Swimming |
| 4 | Sciences: Field medic, Engineer, Robotics, Science; Trades: Weapon smith, Armour smith, Crossbow smith, Labouring, Farming, Cooking |

**Levels and Next Level** (**Verified** decompile): a row's value is `floorf(stat × character vfunc 0x398)` (`FUN_1408aaa10`), the bar and its
`{1,num}%` the fraction `stat - floorf(stat)` (`FUN_1408ab4d0`); both read the raw stat (`FUN_140884ac0` with flag 1, no injury factor). So
the number only rises when the bar fills.

**Stats Affected numbers** (**Verified** decompile of `FUN_1408b1250`, one branch per stat; constants from the loader `FUN_14086b2b0`, which writes them as floats from
`0x142133dd0`): the left panel is a DataPanel of label/value rows, `Stats Affected:` and `Ways To Train:` in Main, each row label "-" + the text; most rows have no value.
Values: Strength (1) `Max carry weight:` `{1,num} kg` = (int)(strength × its multiplier `FUN_140338090` + `encumbrance base`), `Strength XP rate (walking):` (int)(`FUN_1408c60c0`
× 100) `%`, `Martial arts damage` (int) the four damage terms of the blow in hand (`FUN_1408868b0` with no target: no kind or race multiplier, the gear's `damage output mult` applies; the weapon's for an armed character despite the label), `Strength XP rate (combat):` (int)(`FUN_1408c5ec0`(stats, w) × 100) `%`, the Athletics XP
function (CharStats `+0x190` clamped at 0, x1.5 when w > 0.8 x the run value V) with w the weapon's `getItemWeightSingle` (item slot `0x248`, `+0x120`; the hand's weapon, else the first of
`hip` / `back`; 0 without), so heavy weapons train at 1.5x (**Verified**, decompile and the KenshiLib slot). Toughness (0x15) `Damage resistance:`
(int)(`FUN_140644770` × 100) `%`, `Martial arts damage`, `Toughness current XP rate:` `{1,num}` of `FUN_1408c5f40`, `Knockout time:` lerp(t, `knockout mult 1`, `knockout mult 99`)
and `Wound deterioration speed:` lerp(t, `degeneration mult 1`, `degeneration mult 99`), both `{x.xx}x` with t = toughness × CharStats `+0x34` / 100 unclamped, `Knockout point:`
(int) `FUN_140643ae0`, label and value Main, `Amount of damage it takes to stagger you:` (int) t × `stumble damage max`. Dexterity (0x12) `Attack speed:` / `Block speed:` CharStats
`+0x188` / `+0x18c` as `{x.xx}`. Dodge (0x20) `Dodge XP rate:` CharStats `+0x190` (the encumbrance factor) × 100 as a float `{1,num}%`. Athletics (0x11) `Max run speed:` (int) the
run value V (`FUN_140883db0`, units per second, no unit), `Current run speed:` (int) CharStats `+0x17c`, `Athletics XP bonus:` (int)(`FUN_1408c5ec0`(stats, `+0x17c`) × 100) `%`.
Swimming (0x17) `Max swim speed:` / `Current swim speed:` (int) `FUN_140883e30` / `FUN_140885eb0`. The other skills, from the effective level L (`FUN_140884ac0`) through `FUN_140889990`: the work rate R = lerp(L/100, 0.5, 1.4) (`FUN_1408852a0`, unclamped) as `{1,num,p=2}x`
for Assassination `Stealthy knock-outs:`, Engineering `Building things:` / `Repairing things:`, Science `Research rate:`, Labouring `Mining:` / `Using machinery:`, Farming
`Harvesting speed:`, Cooking `Cooking:` and the smiths' `Crafting weapons / armour / crossbows / crossbow bolts (speed):`; Medic (9) `First aid skills:` lerp(L/100, 1, 15) x
`medic speed mult` (constant 0x16) as `{1,num,p=2}x`; Turrets (0xb) and Crossbows (0x23) `Accuracy:` and `Rate of fire:` both (int)((L + perception (0x18)) x 0.005 x 100) `%`
(`FUN_140435160`); stat 0x24 `Friendly fire avoidance:` (int)(min(L x 0.015, 1) x 100) `%` (`FUN_140440300`); Farming `Harvesting yield:` (int)(clamp(lerp(L/100, 0.5, 2), 0,

### Map

- **Projection:** the image spans world X and Z −147456…147456, world (0, 0) at its centre, +X right, **+Z down** (`14048cf70`; also checked by drawing
  the placed towns on `GUI_Map.dds`: Shark lands in the swamp, Stoat in the Great Desert, Mongrel on the Fog Islands).
- **Mouse:** left or middle press pans with the `hand` cursor, no threshold; release ends it. Right press does nothing; right **release** inside the view
  orders the selection to move (task 0x1d, the shared order dispatcher `1407f9e20`) to the point under the cursor, clamped to the image, Y = max(terrain,
  100). Shift is read but not used (no queueing); a town marker takes clicks like the map under it. Extra selected characters get a random offset
  rand·√(60·count)+5 around the point (Meitou: `Formation.Scatter`, see [ui-input](ui-input.md)).
- **Zoom:** scale = on-screen pixels per canvas pixel, from fit-the-whole-map (min(view w, h)/4096, the start) to 1.5 × UI scale. Buttons add or
  subtract the least scale about the centre; the wheel adds 0.000625 per wheel unit (0.075 a notch) about the cursor. Zoom level 0 below 0.4 × UI
  scale, 1 up to 0.75 ×, else 2. Markers keep their pixel size. Opening the map and the centre button centre on the camera focus.
- **Colours** (`kenshi_colours.xml`): MarkerAlly `#5cb473`, Neutral `#dfdfdf`, Enemy `#e65139`, Player `#8dcdff`, Movement `#79b44b`; PlayerSelected
  (0.652, 0.903, 1.0) is built in. Player faction: Player (Selected when selected); else Ally, Enemy (relation below −30 or a faction flag), Neutral (`14048f320`).
- **Movement marker:** only after a map right click: at the order point, bottom-centre anchored, colour Movement, for 3 s of real time; opening the map
  hides it.
- **Camps and nests:** a TOWN of type 0 is a `Nest` object (factory `140581770`), made by the area spawner (`1408f7310`) from the BIOME_GROUP `nests`
  (Meitou: the wild nests, [factions-squads-towns.md](factions-squads-towns.md) 6.3). Group "Nest" when the TOWN's faction is `not real`, else "Camp"
  (`14004bc6d`). Shown only when discovered and a player character is within √10616832 ≈ 3258; always level 2 (recently discovered does not lower it),
  × 0.85 (× 0.5 at level 0), no label, faction colour when explored. The marker goes when the nest does (Meitou keeps it: its nests are not destroyed).
  Saved as GAMESTATE_TOWN with `is nest` (`1403709d0`); the original's town loaders skip those. Meitou writes its own with `instance`
  `meitou-nest:<wild site>:<TOWN id>` and reads them back; the place is filled in when the nest next loads.
- **Discovery** (`FUN_14092f6b0`): once a second of real time (the unscaled frame time adds into `DAT_142135448`, reset after a run; **Verified**), not while paused (`FUN_140787940`), and only with player characters. Meitou: `PopulationSystem.TickDiscover` adds `TickSeconds / World.PhysicsSpeed` per tick (the host's speed; a pause runs no tick). Viewers: the
  player's characters and the camera focus, the camera only inside the zone grid (kept within 2250 of the nearest character, `FUN_1406ae990`; Meitou: the focus too, pulled in to 2250 of the nearest character by `AddFocusViewer`, its camera being unclamped). An explored town is skipped (vfunc 0x340 reads the `explored` byte at +0x181, `FUN_14092f6b0`:317-319, **Verified**). A viewer counts only for towns filed under its [zone](../formats/terrain.md#world-coordinates)
  (`FUN_1409b2d90`) or the eight round it. Filing: a town's own zone when it joins the town list (`FUN_14092d690` → `FUN_140929d40`); on a new game,
  load or import also the zones its level GAMESTATE_TOWN records list ([keys](../formats/zones.md#placement-keys); `FUN_14036efd0`), inside the grid
  and with both keys (a count below 1 lists none), a record stopping once the town has 6, none from a record with one more than 2 zones from the
  town's own; nests (placed TOWN type 0 too) only their own. Meitou (`TownSite.Coverage`): the first 6 of a town's records merged, none when one is
  too far; no base-game town differs (**Observed**). Each town not yet explored except type 8 (NestMarker): a viewer closer than 400 units (XZ) sets
  `discovered` and `explored`; else, not yet discovered, one closer than 8500 (17000 for type Town, halved for Nest and Ruins) sets `discovered` with
  the message "Discovered {1}" (its `unexplored name`), none for
  nests. The new game's messages show after the character editor's CONFIRM (**Observed**, vanilla play); Meitou checks nothing while `PlayerState.InCreation`.
  Meitou checks with the population's zone refresh (every 15 ticks), a camp only after its nest has loaded; flags per placed town
  (`PlayerState.Towns`) and per camp (`PlayerState.Camps`), read and written as the GAMESTATE_TOWN bools.
- **Tooltip** (`14048fd10`, `140922e60`; [Tooltips](#tooltips), type 4): only an explored town or camp, in the Overview's `ToolTip` (`Kenshi_GenericTextBoxSkin`): the name, "Faction:"
  name, "Relations:" the player faction's relation, then population rows ("Residents max:", "Actual Num Residents:", "Roaming currentMax/desiredMax:",
  "Roaming Population:", "Num Roaming Squads:", "???" when the manager has no numbers) and, for towns, "Residents:" with a line per resident faction and
  "Hostile Occupiers:". Meitou shows the first three rows only.
- **Roads:** `1404931d0` would draw the level's road network (`4-roadlist`) as polylines (hidden roads `#a0a0a0`), but its only caller `1404939f0` is
  never referenced in the exe: the base game shows no roads (**Verified**, xrefs and a raw pointer scan). Meitou draws none.

### Faction tab

- Captions: `lbFactionName` "Faction name", `lbFactionInfo` "Information". `FactionNameText` (EditBox) holds the player faction's name (PlayerInterface+8).
  Accept or losing the key focus renames (`FUN_1404923d0`): a new name that no faction has yet (`FUN_1402e7950`) is written, exactly as typed (no trim), to PlayerInterface+8 and the
  player faction's name (+0x1a8); a taken one shows the message box "Faction Name" / "This faction name already exists" and the box reverts. While the box
  has the key focus the hotkeys are off (`DAT_142133440` = 0, back to 1 on focus loss).
- `FactionInfoPanel` holds a DatapanelGUI ("mfactioninfo", `FUN_140494720`): `---Active Contracts---` (#444444), then the contract rows (`FUN_1404927f0`), which first clear the
  panel (its vfunc +8), so the heading never shows (**Observed** in a base screenshot: the panel starts with Upcoming Events); then `---Upcoming Events---` (#444444) and the events (`FUN_1407f2ef0`). **Verified** by decompile unless marked; class and field names KenshiLib 1ce4a16 (**Observed**).
  - **Upcoming Events** (with the player-only flag set): for each faction of the list at `DAT_1421345b8`, its `FactionWarMgr` (`FUN_1407ac9c0`). When it has active campaigns or
    queued campaign requests: a heading `#afa68b` `-{faction name}-`. Per active campaign whose target faction is a player faction (`isPlayer`) and whose phase is not 2 RETREATING or 3
    VICTORY: the campaign's name in `#492620`, then `Target:` with `#afa68b` {target faction name}`- `{target's name, or `???` when it does not resolve}, then a spacer. Per queued request
    (the player filter is not applied here): its campaign's `display name` and the time until it starts, both `#444444`. Times (`TimeOfDay::getTotalTimeString` `FUN_1407a6940`):
    negative `Expired`, above 24 h `{1,num} Days, {2,num}hrs`, above 1 h `{1,num}hrs`, else `{1,num} mins`; days `floor(h/24)` with `(uint)h % 24` hours, hours `floor(h)`, minutes `floor(h x 60)` (**Verified**, decompiled `FUN_1407a6940`).
- Meitou: the name box takes typing; accepting it (Enter) or losing the key focus sends `RenameFactionCommand`, which takes a name no other faction of the data has; a taken name opens the original's box instead (`FUN_140742130` with button mask 1, its first button "Ok", `PlayerInterface.MessageBox`), and the simulation still posts "This faction name already exists" for one the UI did not catch (there is no empty-name test in the handler (**Verified**, `FUN_1404923d0` goes from the equal-name test straight to the name lookup), so an empty name is accepted; the 64-character cap is an engine choice). The name is `PlayerState.FactionName` (null = the data's "Nameless"), saved as the CAMERA `pfaction name`, the player platoons' faction name and read back on load when it differs from the data's; the GAMESTATE_FACTION record's own name is not rewritten. The lines for every faction met but the player's, the `not real` ones and the template, sorted
  ignoring case, rebuilt each frame on the view's Client, line height from the layout's 0.0222 of the screen; the info rows from 0.03 of the panel down: the contracts
  `World.ActiveContracts` (one entry per hired squad, its package being the squad's), the Upcoming Events heading and `PopulationSystem.UpcomingEvents` (Meitou's phase 2 stands for RETREATING and VICTORY).

### Research tab

Overview tab 2 (`ResearchTab`), part of `ManagementScreen` (constructor `FUN_14049f5b0`, refresh `FUN_140495e00` every 0.5 s while shown). **Verified** by decompile unless marked
(2026-10-27); strings read from the exe. Meitou: `ProductionWindows`; the research rules are in [buildings-production](buildings-production.md#research).
- **Time** (`FUN_140831550`): the research's hours over the current rate (when above 0) x (1 - its progress share), rounded up to at least 1; "<" before it under an hour;
  "{1,num} hr" / "{1,num} hrs"; the description adds " (at 1x rate)" when the rate is 0.
- **Description** (`ResearchDescriptionPanel1`, the research text in `...Panel2`), the hovered or clicked research: the name in Title with "lvl {1,num}"; "ETA:" in Title with
  the time; "Needs research bench level {1,num}" in Bad when the bench is too low (`FUN_140830a30`); unpaid (`Research::hasPaidFor`), per `cost` item "Cost:" with
  "{item} x{n}" in Good when the benches hold it (`hasArtifact`), else Bad; paid, "Cost:" with "[Paid]" in Good; "Requirements:" with the first required RESEARCH and a
  label-less line below with the rest, joined by ", " (#444444 finished, Good queued, else Bad; "-" for none); then one line per record of `enable buildings`, `enable weapon type`,
  `enable weapon model`, `enable armour`, `enable item`, `enable crossbow`, `enable backpack`, `enable robotics`, labelled "New Building:", "New Weapon Class:", "New Weapons
  Grade:", "New Armour Type:", "New Items:", "New Crossbows:", "New Backpacks:", "New Robot Limbs:", the name in Good (`FUN_14048ebc0`).

### Craft tab

- **Queue entry description** (`FUN_1402bcf60`, **Verified** unless marked): `State:` "In production..." and, by the bench's status (building `+0x468`), a second `State:` row in Bad: 0 "Out of resources",
  1 "Output full", 3 "Upgrade required" (any other status adds none). An entry whose bench hand (entry `+0x18`, copied from the building in `FUN_1402b5ca0`) is null reads `State:`
  "Not assigned to a craft bench" in Bad instead and skips the status row; Meitou's entries always have a bench, so it never shows. Status 3 is set by `FUN_1402b5680` for an empty queue, or for a
  zero work factor (slot 109) when the town has no power surplus; `FUN_1402b51c0` returns it to 2 once `+0x3c0` is above 0 (**Observed**; Meitou's `Machine.Status` is 0 / 1 / 2 only, so 3 never shows).
- Only when the bench has an operator (the hand at building `+0x4d0`, written by every operator's tick and never cleared, so the last one; Meitou: `Machine.Smith`, derived, not saved): `-Current Smith:`
  name + " (skill: {1,num}{2})" with the effective level of the bench's stat (`FUN_140884ac0`, as the Current Skill line) and, when the stat multiplier with all six terms (`FUN_140884060`:
  (int)((1 - m) x 100)) is not 0, {2} = Bad " (" + {1,num=fix,p=0} of the negated value + ")" and then the Secondary colour. Without an operator the function ends after `ETA:`.
- Meitou: the rows go to `QueueDescriptionPanel2` and a name and ingredient list to Panel 1; the smith's level comes from `World.EffectiveSkill` (the Current Skill total), while the craft itself still rolls
  with the stat x its default multiplier (`ProductionSystem.CraftStep`).

### Squads tab

- The constructors pass the translated captions through a locale transform taken to be upper-casing (the box's "Delete" matches its layout's "DELETE"; not seen in a run): `NewSquadButton` "ADD SQUAD", `lbSquadsDismiss` "DISMISS" (the layout's
  "NEW SQUAD" / "DRAG HERE TO DISMISS" are replaced).
- ADD SQUAD (`FUN_140491b50`): below `max squads` boxes a new empty platoon of "blank squad" at the player interface's node position (`FUN_1407f4910`; Meitou:
  the camera's focus), else "Maximum number of squads reached.". DELETE (`FUN_140491d20`): "The squad needs to be empty before it can be removed.";
  the last squad: "You must have at least one squad.".
- Meitou: a portrait clicked without dragging selects the character (engine choice); a box is dragged by its frame.

## 6. Inventory, trade, dialogue, build

- **Rotating an item** (Meitou, optional: Advanced → Item rotation, `UserConfig.RotateItems`, default off; the original has no rotation; after
  ErasmianFolly's GetRotatedIdiot mod): the Rotate item control (`InputAction.RotateItem`, default the middle button, which stays the camera's
  orbit anywhere else) turns the item on the mouse a quarter (`RotateHeld`), else the grid item under the pointer in a character's window, one of the
  player's or the open loot's body (`RotateItem`, which the simulation applies to any character's inventory, as the mod does; `InventoryCommands.Rotate`: at its own cell when the turned footprint is free there, else the nearest
  cell with room, refused when none). A turned item (`ItemInstance.Rotated`) has its footprint's sides swapped and its icon drawn turned
  clockwise; square items do not turn. Only the grids hold turned items (`main` of a character, stock, storage or container, and
  `backpack_content`): any other section sets the item upright, and an item put back from the mouse lies as it did when lifted. Saved as bool
  `meitou rotated` ([save.md](../formats/save.md#zone-files)).
  - Input: the item sections hook only pressed (`FUN_140716800`) and released (`FUN_1407169a0`); no double click, no inventory keys, no quantity dialog. Shift = `DAT_142133449`,
    Ctrl = `DAT_142133448` (key flags `FUN_14082b010`).
  - While held (**Verified**, `FUN_140713560` keeps the item at `MouseInventory+0x30` and its source section, found by name, at `+0x70`; `FUN_140749160` adds that item to the
    section's sum whenever it is recomputed): the item still weighs on its owner. Meitou: `Inventory.ContentWeight(HeldItem)`.
  - Left press with nothing held (`FUN_1407139a0`): the section must be enabled; for a building's window the trading range ("Out of trading range.",
    `gui_notrade`). Cell = (pointer - grid origin) / cell, clamped. Locked (shackles) items do not lift. In a trade an item flagged equipped on a non-player owner: "That's not for sale.".
    A single unit, or Shift or Ctrl held: the whole stack lifts; otherwise one unit is split off and the rest stays. `Item_Lift`; the grab offset is kept, clamped so the pointer stays at
    least half a cell inside the footprint (`FUN_140713560`).
  - Release, or a press while holding: the target is the window under the pointer, else under the item's centre. Over a window's section: placed; over a window elsewhere: back to its
    origin; over no window: given or dropped (below). After a swap or a partial place the other item or the rest stays on the mouse for the next press.
  - Sounds (**Verified**, decompile; all on the GUI object with `Inventory_Items` set from the item, `FUN_14070d230` / `FUN_14075e360`): a lift `Item_Lift` (`FUN_1407139a0`);
    a placement `Item_Drop` (`FUN_140715560`; equipping included; a swap posts no lift); `Sell_Item` instead for any move between a trade pair, buying too; a right-click move `Item_Lift`
    (`FUN_140713d20`; with Shift once), `Sell_Item` in a trade; a ground drop `Character_Drop_Ground`; nothing for giving to a character, a return to the origin, refusals, money becoming
    cats or hovering. `gui_notrade` / `gui_noequip` are posted (out of range, can't afford) but are in no bank. A window made or removed posts `Inventory_Open` / `Inventory_Close`
    (`FUN_1406e6820`, `FUN_1406e53d0`); OPEN BAG posts once more (`FUN_14070e120`). `Inventory_Items`: WEAPON and CROSSBOW by `material` (Weapon_Metal, Weapon_Wood), ARMOUR by
    `material type`, ITEM / CONTAINER / NEST_ITEM / MAP_ITEM by `inventory sound` (0-19), LIMB_REPLACEMENT Robotic_Component.
  - Stack limits (`FUN_14075e090`, `FUN_1407466b0`): 1 for WEAPON, ARMOUR, CONTAINER, MAP_ITEM, CROSSBOW, LIMB_REPLACEMENT; else the record's `stackable` through the section: a limited
    section starts from 1; then max(value, section minimum) x section multiplier.
  - Loading a saved inventory (`FUN_14074c0e0`, **Verified** by decompile; the INVENTORY_STATE's items in file order): each item is looked up by its `section` name; it goes to its saved `inventory x` / `inventory y`
    only if the section exists and accepts it (`FUN_14074bc30`: an owned section asks the `RaceLimiter`, so a worn item the wearer's race may not wear is refused and auto-added; **Observed** in 1.0.68, #704: a Small Backpack worn by a bonedog loads into its main grid with its contents), the footprint lies inside the section's width and height and `FUN_1407466f0` finds no item of that section overlapping it. Otherwise it is
    auto-added to the inventory (`FUN_140745c80` to `FUN_14074bd20`: the sections that accept it, the backpack's last, each by `FUN_14074c8b0`), and when nothing has room it is not added (lost). No saved
    cell is ever repaired by sliding. A CONTAINER's contents are a second INVENTORY_STATE read the same way into the bag's own grid, so the contents of a bag carried in `main` and of the worn one are
    separate grids that both name their items' section `backpack_content`: a window lists only the worn backpack's (a count of cells from both would overlap), **Verified** on the siwa-test save
    (Trak: 70 items, 18 of them in the Grand sac carried in `main`, the others in the worn bag; no overlap within a grid, 31 overlapping pairs in the save when both bags' items are listed together).
  - Meitou (`SavedKit.Fill`): the same rules, the rejected items auto-added after the others are placed; `Inventory.Shown` is what the windows list (own items and the worn backpack's contents).
  - Trade: a drag buys or sells in place, asking the fence question as the right click does; the quantity is capped by what the payer can afford (the rest stays on the mouse);
    "You can't afford that." / "The shopkeeper can't afford that." with `gui_notrade` restore the stack; a caught stolen sale: "Got caught selling stolen item." / "Attempted smuggling!".
    Taking from a non-player CAMPING building (`building category`) is not stealing.
  - Drawing: the held item like an inventory cell at pointer + grab offset, its source cell empty. One `WhiteSkin` rectangle on layer `Info`, alpha 0.5, the footprint's size at the snapped
    cell, shown only when the section takes the item and the footprint fits (what is under it is ignored: swaps are legal); no red or green.
  - Meitou (`InventoryWindows`, `Player/HeldItems.cs`): the held item is world state (`PlayerState.Held`, commands `LiftItem`, `PlaceHeld`, `GiveHeld`, `DropHeld`,
    `ReturnHeld`; [simulation](../simulation.md#items-and-eating)). Engine choices: the release places only after the pointer moved at least 5 px since the press (a click keeps the item for the
    next press); right click or Escape puts it back; a stock item goes into a player character's window and such a window's own items onto the stock, the trade passing to that character within 1000 ([simulation](../simulation.md#trade)); give range 1000 (60 for an item out of
    a non-player window) from the window's owner; let go over a squad bar portrait, it is given to that member (same range and room rules; its own portrait puts it back). Race limits: `ItemInstance.WearableBy` (an animal only with its race listed). Not built: section item-count caps (no section is known to set one, above), "That's not for sale." (no trader character window),
    CAMPING buildings.
- <a id="limbs-window"></a>**Limbs window** (`LimbsInventoryLayout` `FUN_1401573d0`, `Kenshi_LimbReplacementWindow`; **Verified** in the decompile unless marked; code `InventoryWindows`,
  `ArrangeLimbSlots`, `PlaceLimbsWindow`):
  - Slots: sections `left_arm_replace`, `right_arm_replace`, `left_leg_replace`, `right_leg_replace` (`FUN_1400d04d0`), arms 2 x 6 and legs 2 x 7 cells (`FUN_1401534f0`). A slot is
    enabled only for a stump or a replaced limb, else drawn with the skin's `disabled` tile and taking no drop (MyGUI gives it no focus, **Observed**). A fitted prosthetic shows its
    icon; a stump's race item never sits in the slot. Labels `-ARMS-` / `-LEGS-`; `figure.png` never changes. The slots are built on open (a limb lost meanwhile shows on reopening,
    **Observed**; Meitou refreshes live).
  The pin button (`Kenshi_PinButtonSkin` in the `Kenshi_WindowCXP` caption, drawn "O"; the skin's `*_checked` states while pinned) keeps the inventory window on the character shown
  when it was pressed, and another squad member's inventory opens in a second window, so several show at once (**Observed** by the owner in the original). Opening uses the next free inventory position (the original manager's rule): selecting
  someone else after pinning opens their inventory after the existing windows; I on a pinned character raises their window; unpinning gives the window back to the selection (closes it when
  the inventory is already open); a pinned window has its own bag window (OPEN BAG; a bag open when pinning stays open beside it) but no limbs window. A pinned window can still be dragged (**Observed** by the owner). A right click moves the item to the other character's window as below; with several open, Meitou picks the one on top (engine choice). The snapshot carries every pinned character's grid (`ShowInventoryCommand`).
  Esc while trading closes the trader and the character's windows in one press, whichever was clicked last (engine choice, as for loot).
  With a loot or container window open, the inventory window shows its looter while that one is selected, else the lead selected character. The original shows the lead even when another selected member looted (**Observed** by a player, #389); Meitou's choice keeps the looter's items beside the loot (`InventoryWindows.InventoryOwner`).
  The data panel is the same for every character window, the loot window and a pinned one (**Verified**: the window update `FUN_14070f3c0` calls the owner object's slot 40 (+0x140, `Character::vftable`, `FUN_1405d4260`, mask 0xf) with the window's DatapanelGUI, created by the layout `FUN_14014eee0` as `Character_Inventory:<name>` / `FUN_14014f570` for animals; the title is the owner's name). Meitou fills it for `inventory`, pinned and `loot` alike; the burden word of a body comes from the grid's `Burden` (a body has no HUD view).
  **Which lines** (**Verified**, `FUN_1405d4260` disassembly, mask 0xf = 0x1405d42ef..0x1405d7455; status object S = Character slot 14 (`+0x70`), S+0 slave state): every character: `Total Weight:` and the burden row (blank label, value `-Weightless` ... `-Overloaded`, thresholds above). Then the character's faction (slot 11, `+0x58`): **not the player's** (faction `+0x250` null): only the trader lines when the character is a trader (`Money:`, `Merchant Info:`, `Prices:`, `Buys Stolen Goods`; Meitou: not built) and the panel ends, so a looted body or a stranger shows weight and burden only. **The player's faction**:
  - `Disguise:` value = the uniform faction's name (S+0x108 non-null) in Main, else `None`; tooltip `Not currently in disguise.` at S+0x100 (disguise level) <= 0, else the rows `Clothing disguise:`, `In secure location:`, `Same faction:`, `Slave owner recognition:`, `Someone knows me personally:`, `Disguise blown:` (seconds, S+0x110) and `Total:` (level x 100). A second row follows with a 4-space label: `-` at level <= 0, else the percent (`{n}%` of S+0x100 x 100, Main) or `Blown!` (Bad) when S+0x110 > 0. Meitou: `InventoryLayout.DisguiseLines` from `CharacterSnapshot.Disguise` ([disguise](perception.md#disguise)).
  - `Slave of:` S+0 <= 0: value `-`, label and value Greyed, tooltip `You are a free person.`. S+0 > 0 (1 slave, 2 escaped; 3 ex-slave): the owner faction's name (S+0xe0, name at +0x1a8) in Bad, tooltip `The faction that owns you.`; **without an owner the line is not added**.
  - `Looks like slave:` S+0 <= 0: `-` Greyed, tooltip `You are no slave.`. Else `{n}%` with n = int(S+0xf4 x 100), Bad when S+0xf4 > 0.1 (else the default colour); tooltip `How much you look like an escaped slave...` followed, for slave states, by the rows `Clothing:` (S+0xf0), `Minimum (Escaping):` / `Minimum (Ex-Slave):` (S+0xec set: 10 / 1 %, floors) and `Total:` (Meitou: the first text only).
- **Trader window** (code `TradeWindows`):
  - Layout: `Kenshi_InventoryTraderWindow` (**Verified**: `FUN_140155ee0`, class `TraderInventoryLayout`, names the file; created by `FUN_1406e6af0` for a trading character or building).
  - Size (**Verified**, `FUN_140150570`): the grid is the trader's `backpack_content` section at the `Kenshi_InventorySlotSkin` size (25 x 25 px, read once from the skin). The grid is
    first sized columns x columns; the root window and `scrollview_backpack_content` grow by that size less the layout's grid size; then the grid takes its real rows, becomes the scroll
    view's canvas and is reparented into its client at 0,0. So the visible stock is square (as tall as wide) and the window scales with the column count. ARRANGE gets the caption "Arrange"
    and the inventory's arrange handler; `datapanel` holds the money.
  - The scroll view is the `Kenshi_ScrollView` template (**Verified**, `kenshi_templates.xml`): a `Kenshi_GenericTextBoxSkin` frame, client 10 px in from left and top, `Kenshi_ScrollBarV`
    20 px wide 5 px off the right (arrows 12 px, track between, thumb `Kenshi_SmallBarSkin` at least `MinTrackSize` 10); the horizontal bar is off (`VisibleHScroll` false).
    Meitou draws it with the host's generic ScrollView ([widget runtime](../formats/mygui.md#widget-runtime-2026-10-20)): the stock grid is the canvas, the bar shows only when the stock overflows.
- **Character trading window** (`CharacterTradingWindow` `FUN_14072a880`, rows `FUN_14072a070`; DA 41 / 42 / 43 / 48, [economy 12](economy.md#12-slaves-prisoners-animals); code
  `TradeWindows`):
  - Layouts (**Verified**, install's `data/gui/layout`): `Kenshi_CharacterSelectionWindow`: full-screen `Root` (PanelEmpty, no mouse) holding `TradePanel` (Kenshi_FloatingPanelSkin,
    caption "Character Selection", never drawn: a plain MyGUI `Widget` has no text (**Observed**), 0.302 0.120 0.414 x 0.567 of the screen) with `CaptionTextBox` (title), the `CharacterSelectionItemBox` ScrollView (Kenshi_ScrollViewEmpty),
    `ConfirmButton` ("CONFIRM") and `CancelButton` ("CANCEL") at the bottom right, `lbTotalMoney` "Total:" + `MoneyAmountTextBox`, `SelectedTotalTextBox` ("0 / 0", bottom left),
    `lbBuyersMoney` "Buyer's money:" + `TotalMoneyBuyer`. `Kenshi_CharacterSelectionBox` (a row, Kenshi_FrameSkin, 0.3776 x 0.1806 of the screen): `Portrait` (ImageBox) with
    `NameTextBox` nested at its bottom, `PriceTextBox` above it, `StatsTextBox` holding `DataPanel1` and `DataPanel2` side by side, `CheckBox` (Kenshi_TickBoxSkin) at the right.
- **Item tooltip** ([Tooltips](#tooltips); **Observed**, decompile): name, then `Value` (`c.{1,num}` with ` (+N%)`/` (-N%)` when the price factor is not 1) and `Sell value` (`FUN_14079e7a0`; one unit's price, slot 81 with `sell` 0 and 1, **Verified** in the decompile, not the stack's); weight `{1,num} kg`,
  quality, charges, damage, nutrition and kind lines (`FUN_1407ae130`); armour classes and resistances (`FUN_1407a1650`); `Stolen` / `Stolen from:` (`FUN_1407a0ea0`); `Buyback` (`FUN_1407a13a0`).
  Meitou: the rows listed under Tooltips ("Meitou item rows"), and:
  - **Crossbow** (`FUN_1407b0750`, the Crossbow item's first tooltip function; **Verified** rows, labels and number formats in the decompile; units read from the exe's constants: `s`, `m`, `m/s`, `x`):
    grade (shared code), `Buyback`, `Stolen`, then `Crossbow class` (Main) and rows with a hyphen label (Ink) and a Main value: `Damage:` `min-max` (ints, the gun's pierce ends after the
    quality lerp, `bow damage 1/99` and the global damage multiplier), `Accuracy deviation:` `{p=2} degrees`, `Reload time:` `min-maxs` (2 decimals), `Range:` range × 0.1 as `Nm`
    (0 decimals), `Velocity:` shot speed × 0.23 as `Nm/s`, `Blood loss:` `{p=2}x` (the item's bleed multiplier), `Required skill:` the `accuracy perfect skill` int; a 15-dash rule (Grey);
    `Ammunition type:` the ammo item's name, `???` with none; `Damage vs robots / humans / animals` and `Damage vs <race>` (signed percent rows as the weapon's, `FUN_14079f650`);
    a 16-dash rule; `Weight` whole kg truncated; then the shared prices (`FUN_14079e7a0`). Meitou builds all of it from the gun at the item's level × 0.01
    (**Observed**: the original reads the item's own gun; its quality input is not traced, Meitou's simulation uses the level too); `race damage` rows have no crossbow field in Meitou.
- **Fencing prompt** (**Observed**, `FUN_140712920`, which writes it to a global string), shown for a stolen item over the trader, and in the hovered item's tooltip while trading as
  `FENCING CHANCE:` `N%` (Bad) after the kind markers, before `Stolen from:` (**Observed**, a vanilla screenshot, #330; Meitou for the goods the shop rolls on): stolen from the trader's own faction, `Fencing chance:` and `N%` (`FUN_140793490`) in Bad;
  else (another faction, or an owner that does not resolve), when the trader's squad does not fence it (it buys stolen goods and the owner is not of that squad, [economy](economy.md#fencing-stealing-and-smuggling)),
  `Stolen goods?` / `No problem!` in Good; otherwise `Fencing chance:` too (the branch falls through, **Verified** in the decompile). Meitou (`TradeSystem.Publish`, `TradeItemView.FenceChance`): `Fencing chance:` for every stolen
  good the shop rolls on (`TradeSystem.Fences`), whoever it was stolen from, with the roll's own odds shown capped at 100% (the original prints `int(odds x 100)`, so 110% for a good nobody looks for); `Stolen goods?` when the shop does not roll. Differences left
  (**Observed**): the original prints a chance for an owner of the trader's faction even when its squad does not roll, and prints nothing for an owner whose faction has `+0x250` set; Meitou follows the roll. Stealing from a stock shows `Stealing chance:`; illegal goods
  checked first, against the culture of the trader's faction: `ILLEGAL GOODS!` / `NO!` (`DAT_141707808`) in Bad, or for a trader with `+0xed` (buys illegal) `Illegal goods?` / `No problem!` in Good (**Verified**, decompiled; whose faction is read is **Observed**). Meitou reads the trader's town's faction, as the sale check.
- **Burden row** under Total Weight (**Verified**, `FUN_1405d4260`): by the carry speed factor, >= 0.95 `-Weightless`, >= 0.75 `-Lightweight`, >= 0.5 `-Moderate` (Main), >= 0.1 `-Heavy`,
  else `-Overloaded` (both Bad). The same function gives a trading character's panel its name, `Money:`, `Merchant Info:`, `Prices:` +/-N % and `Buys Stolen Goods` lines.
- Colour tags in code (**Verified**, static initialisers `FUN_14158a...`): #afa68b, #140806, #492620, #59231a, #bf4834, #a8b774, #444444, #777777, #14ffdc. The strings `DAT_141f49248` + 0x28 k (k = 0..8; initialisers `FUN_14158a2a0` + 0x40 k, **Verified** 2026-10-23) are #afa68b, #140806, #492620, #59231a, #bf4834, #a8b774, #a8b774 again, #444444, #777777.
- **Disabled slots** (red `disabled` state; the section's byte `+0xd0`, `FUN_14014e3d0`): `CharacterHuman::validateInventorySections` (`FUN_1406218f0`, **Observed**) enables every
  section, then disables and empties `shirt` for a race with `no shirts`, `head` with `no hats`, and `boots` with `no shoes` or while both legs' `getLimbState` is set (lost or
  replaced). **Verified** (decompile): "empties" destroys every item in the section (`FUN_140749c20` with its delete flag, "clearAllItems"); for boots with shoes allowed the first worn item is first
  taken off through character vtable slot 53 (`FUN_1405ca740`), which places it in the world just ahead of the character (the scalar of the offset is not readable) and removes it from the
  inventory: worn boots are dropped, not packed. With `no shoes` nothing is taken off, the items are destroyed. Callers (vtable slot 130 `+0x410`, **Observed**: a scan of the `+0x410` virtual calls, whose other hits are other classes' slots): the human inventory build
  (`FUN_14062c4e0`, slot `0x2c0`, before the clothing is added) and the severing of a leg (`FUN_14064edc0`, parts 2 and 3); **Verified** (decompile): also the character editor's close
  (`FUN_1405f5d10`) for each human of its list when not in mode 1 (the window's byte `+0x1c0` is mode == 1, set in `FUN_1405f4dd0`), right after `starting health`, so a race
  switched at a new game loses what it wore in the sections it closes. The other `InventorySection::setEnabled` callers are the furnace, crafting benches and animals. Meitou:
  `InventoryCommands.SectionClosed`, `CharacterSnapshot.ClosedSlots`, drawn disabled and refusing drops and every automatic equip (`TryEquip`, `Transfer`, `MoveInto`: trade, pick-up, loot, scavenge, storage, slave clothing);
  `InventoryCommands.ValidateSections` runs when a leg is severed (the limb queue of `PopulationSystem`) and for the start's squad when the creation ends (`CharacterEditing.EndCreation`):
  boots dropped 3 ahead of the character (as a dropped held item, engine offset), the rest destroyed.
- Item placement failure messages (FUN_14070f7e0; the checks behind them are in [economy.md](economy.md#shops-and-transactions)): Out of trading range; No room for that item; You can't
  afford that; The shopkeeper can't afford that; That's not for sale; Got caught stealing; Smugglers only buy
  illegal goods. Stealing and fencing chance text: FUN_140712920. Item icon: FUN_140711200.
- Item icons (inventory and trader cells; code `ItemIcons`, `ItemIconStyle`, `ItemIconCache`, `ItemCells.AddItemCell`, `CharacterRenderer.Icons.cs`; the game's code is FUN_140711200
  building the cell and FUN_140847bf0 choosing the picture):
  - **Which kind** (**Verified**: FUN_140847bf0 decompiled, and against the saved pictures below). The game draws an item's icon from its mesh when the record is a WEAPON or its
    `auto icon image` is true (fcs.def default: true for CROSSBOW, ITEM, NEST_ITEM, WEAPON; false for ARMOUR, CONTAINER, LIMB_REPLACEMENT, MAP_ITEM; the base game's records all store it).
    Any other record shows the PNG its `icon` field names, else `default_icon.png` (`data/items/icons`; the game logs "[Icon] Missing image"). The name is looked up by its bare name in
    the `Icons` resource group alone (`resourceExists("Icons", name)`, FUN_140847bf0, **Verified**): `Water.png` is also `data/materials/water.png` in General, a water normal map.
  - **Static files** (**Verified**, base game): 318 records (ITEM 268, ARMOUR 31, MAP_ITEM 17, CROSSBOW 2) name a PNG under `data/items/` (`.\data\items\icons\Fuel.png`, a few in
    `armour\pants\`); all 318 exist (**Observed**). The files are sized to the footprint at 25 px a cell (75x75 for 3x3, 150x25 for 6x1; **Observed**).
  - **Meitou's order** (`ItemIcons.Choose`): a drawn kind takes the game's saved picture of exactly that item, material and colour (`ItemIcons.ColourOf`, below), then Meitou's own saved one
    (`%LOCALAPPDATA%\Meitou\icons`, same names, emptied when its `version` file is not `GameHost.IconVersion`; an item without a material of its own is named by its first), else is drawn by `CharacterRenderer`
    (below); the other kinds take their static file, else `default_icon.png`; with nothing (or while the picture loads), the cell shows the item's name.
- **Building icons**: the build window's `BuildingImage` shows the chosen BUILDING's picture (FUN_1404e2040 calls FUN_14084af40 with 128 x 128, yaw -90, pitch 30; **Verified**, disassembly).
  - **Saved pictures**: looked up as `<string id>.png` in the `Icons` group (`data/icons/buildings` is one of its folders), drawn and saved to `data\icons\buildings\` only when missing
    (**Verified**). The install's folder holds 618 such files, all 128 x 128, 34 of them under 1 KB (nearly empty; **Observed**).
- Build window: categories, groups, research gating and the stats panel are in buildings-production;
  placement checks and messages (`FUN_1404e27e0`) and the build-map controls in [placement](buildings-production.md#placement).

### 7. Tutorials

34 top-level `Tutorial_*` classes (85 with nested ones; **Verified**: rtti.tsv class names). `__tutorials.data`
(**Verified**: file bytes in the install - 136 bytes, little-endian u32 words `0xBAAAAAAD, 34, 0, 30, 4..33` - and
the writer FUN_140974500, whose log string names the class `TutorialManager::save`; the loader FUN_140974a50 reads
the same layout, **Observed**). The file is written to `./__tutorials.data` in the working directory:

| Field | Type |
|---|---|
| magic | u32 0xBAAAAAAD (bytes AD AA AA BA) |
| total | u32 = number of tutorial items held by the manager (34, equal to the top-level class count) |
| countA, then countA ids | the **active** list (manager +0x48: triggered, not dismissed; written first), empty in the sample |
| countB, then countB ids | the **pending** list (manager +0x28, written second), 30 ids 4..33 in the sample; an id in neither list is finished (the sample: tutorials 0-3 seen) |

- **Load** (**Verified**, `FUN_140974a50`): wrong magic is "Corrupted file" (treated as no file); ids outside 0..33 are skipped; items with an id at or
  above the file's total go to pending. No file: everything pending, and the manager is *fresh*.

**Manager** (**Verified**, decompile; KenshiLib 1ce4a16 names): 34 items, the id their position (`TutorialManager.Names`). Each frame (nothing while the
`tutorials` option is off) the queued drops leave pending; every 2 real seconds each pending item's trigger is asked in order, and those that hold
become active (a panel line that flashes, sound `Thing_Complete`); a change saves the file. The triggers (`TutorialManager.Holds`): 0-1 no
character editor and not loading; 2 about 22 s of calm (no PC in combat mode, unconscious or in a bed or prison: the counter +2 per poll, above
20); 3 item 2 ended and a PC in an outpost or town; 4 a PC needs first aid, 5 the same once 4 ended; 6 a PC in combat; 7 more PCs than at its first
poll (the newest kept as the recruit); 8 stolen goods on a PC; 9 a PC knocked out, 10 one out and one awake; 11 a PC imprisoned by any faction
but the Cannibals (`518-gamedata.base`), 12 by the Cannibals; 13 sneaking; 14 the steal pointer; 15 the selected NPC has a bounty; 16 PC hunger
below 2; 17 a PC playing dead; 18 a player production building; 19 a PC slave; 20 a faction uniform on a PC; 21 a full machine; 22 the selected
building is for sale; 23 a complete production building; 24 and 30 build mode is placing; 25 a complete crafting building; 26 the management
screen on tab 3 unless a crafting building is complete (then it drops itself); 27 the same with tab 2 and a research bench; 28 a complete research
bench; 29 a blueprint on a PC (quirk: any backpack wearer passes); 31 a player building at construction 0; 32 the prospecting window; 33 the
selected character has a stump. Reset Tutorials: nothing active, all pending, the file deleted; pages are kept. ACTIVATE (Tutorials window) puts
an item back from page 0 (the recruit and prison items only clear their memory).

**Pages** (**Verified**: the item and page constructors; texts matched to `main.pot`): each page is a `kenshi/TutorialsData.cpp` source line, a
skippable byte (Next reads SKIP when 0, CONTINUE when 1, FINISH on the last page) and its class; the table is `TutorialCatalog.Items`. Most pages
wait for the button; 17 move on by themselves while the window shows them (`TutorialAdvance`): camera moved and zoomed then 5 s; a player
character selected (the first page drops the selection); the selected character moves; the camera follows a character; the map tab seen then
5 s; Alt (town signs) then 5 s; first aid given then 5 s; the recruit selected; the stats window; the squad tab (4); a knocked-out PC selected; a
PC picking a cage lock then 5 s; a prisoner of that cage free; tab 2 or 3; tab 3; a complete production building selected; the selected
construction complete. The 5 s count real time. Pages also set a pulsing yellow highlight over a HUD widget (`HighlightItem`, alpha 0.1-0.4 at
0.4/s).

**Panel** (**Verified**, `TutorialGUI`): the lines (`Kenshi_TutorialTooltipPanel`, 25 px x the UI scale) stack up from the bottom of a top-left list
panel 0.130 x 0.463 of the screen, newest lowest; a new line flashes white, fading in 1 s. The name opens the window (`Kenshi_TutorialPanel`'s
`TutorialWindow`, at its layout place, the top-left corner) on the item's current page; the X, DISMISS and FINISH dismiss it. New game with a
fresh manager: three queued messages, "Hold middle mouse button to rotate the camera", "Use mouse wheel to zoom", "Now create your first character.
Use the 'Confirm' button when finished." (every new game of the process). The Tutorials window (F1) lists all 34 by title, pages read-only.

Meitou (`Tutorials.cs`, `TutorialPanel`, `TutorialCatalog`): as above, the file in the working directory; with no Obedient Slave job no box is drawn (engine choice; the original leaves a stale box, below).
Highlight targets (**Verified**, `_setHightlightCoords`; Meitou finds them by widget name): portraits `SquadPanel`, stats `NamePanel`, its bottom `StatsPanel` and its
"Power" / "Workers" lines, medical `MedicalPanel`, status `StatusPanel`, the bars `LifeBar{n+1}` (body 1, limbs 4, blood 0, power 1, materials 1 or 2 with a door,
condition 0 or 1 with a door), `SpeedButtonsPanel`, `TimeMoneyPanel`, `BuildPanel`, `BountyPanel`, `ShortcutsPanel` and its squad / stats / tech buttons,
`OrdersPanel`, the order buttons (commands), `JobsPanel` and the first job's X, the medic and rescue buttons, the management screen's tab 3 button and page,
the building window's `InputColumn`, the build window's list. Engine readings: the commands box spans the seven order buttons, the crafting items box is the
whole tab page, and a box follows its widget every frame;
the obedience job row (item 28, **Verified** `_setHightlightCoords` `FUN_140972a50` case 0x1c; field names KenshiLib 1ce4a16, **Observed**): the first entry of the Jobs panel's order list
(`OrdersPanel::orders`, the selected character's jobs in list order) whose Tasker's TaskData key is 187 (`_SLAVE_OBEDIENCE`, "Obedient Slave"); the box is that row's whole widget (the
`OrdersItem` at the same index in `ordersItemBox`). No such job (or no row at that index): the target widget is set to none and no coordinates are written, yet `startHighlight` (`FUN_140974260`) still shows the box and fades it in, and `update` (`FUN_1409767b0`) only ties its visibility to a non-null target, so the box pulses at the previous highlight's coordinates (**Verified**, decompiled). The original sets coordinates only when a highlight starts (and from the stats panel page's hook `FUN_140982b50`), not every frame.
"in a town" is a town radius holding a PC; the steal pointer (Meitou has none) is a selection over another faction's ground item.

### 8. Damage floaters

The options `floaters` (Off 0, Simple info 1 default, Full info 2; [settings](../formats/settings.md)) control the numbers that
float over a character when a blow lands. Tooltip: damage "and in brackets the amount of damage that was stopped by armour";
Full info "separate numbers for each damage type". Built at the end of `MedicalSystem` hit handling `FUN_1406508d0`
(**Verified**, decompile; label class `ScreenLabel`, creator `FUN_14073faf0`, fonts `Kenshi_FloaterFont_Small/Medium/Large`):

- **Gate**: setting above 0, and the hit character within squared distance 1.6e7 (4000 units) of the camera.
- **Amounts**: taken after armour, toughness and the global damage multiplier (cut, blunt, pierce, stun; stun is the armour's
  cut-to-stun part). The bracketed "stopped" amounts are armour only, before toughness and the multiplier: cut x cut reduction,
  blunt x blunt reduction, pierce absorbed by the flat defence.
- **Simple**: `N` = whole-number total of the four amounts (no part name), plus ` (-A)` with A the total stopped, when A is above 0.
- **Full**: the hit part's name, `": "`, then `C` = cut (cut + pierce when pierce is above 1) with ` (-a)` when its stopped amount
  is above 0; when blunt is 1 or more also `" + "`, `B` = blunt + stun and its own ` (-b)`. Numbers are truncated to integers.
- Wall (building) damage uses the same label with a plain `{1,num}` number, random scatter of +-20 units (`FUN_14029d2b0`).
- **Look and motion** (**Verified**, `ScreenLabel` update `FUN_1406ea9f0`, constants read from the binary): a plain MyGUI TextBox (template
  `Kenshi_TextboxStandardText`, so no shadow) on the `Dialog` layer, font from a table by the creator's 4th argument (0 `Kenshi_FloaterFont_Small`,
  1 `_Medium`, 2 `_Large`; `FUN_14157ff60`); a hit passes 1 when the option is Simple info, else 0. Its size is the text's plus 20 x the UI scale; its
  top left goes to the screen point of the anchor. The anchor rises in world space at the speed table's entry by the 5th argument (0, 4, 8 units/s;
  a hit passes 1: 4/s); past 8 units of rise the alpha is 1 - (rise - 8) x 0.25, and at 12 the label goes: 3 s in all. The rise adds the game's frame time (`DAT_142133798`, the frame time x the world's speed `+0x700`, `FUN_140788a00`), so a label stands still while the game is paused and goes faster at the fast speeds; Meitou the same. No healing floater exists
  among the callers of `FUN_14073faf0` (**Observed**). The stealth KO's "*Thunk*" / "Failed!" is another caller ([combat](combat.md#stealth-knock-out)).

Meitou (`PlayerInterface.AddFloaters`): text, font, motion and fade as above; colour: red when a player's character is hit, green when one lands the
blow, else grey. No healing floater, as in the original.

**Layer** (**Verified**, the install's `core_layer.xml`): floaters and speech bubbles are on `Dialog`, above `Wallpaper`, `Back` (name tags, task bars) and
`Overlapped` but below `Middle` (the HUD, `Kenshi_MainPanel`) and every later layer, so under the HUD and all windows, the map included. The option `floaters` gates
only the hit's numbers: of the label creator's callers only `FUN_1406508d0` reads it (`DAT_14213350c`; **Verified**, decompile). Meitou
(`PlayerInterface.AddFrameControls`): both drawn just above the name tags; Off hides the hit numbers and a door's damage number, the other texts show.

### Tooltips

Code: `UiTooltips` (data, layout), `TooltipRuntime` (hover runtime, item rows, HUD texts), `UiPainter.MeasureText`. Decompile of `kenshi_x64.exe`.

- **Classes** (**Verified**, constructors): `ToolTip` (`FUN_140920d10`) makes a `ToolTip` widget of 0.25 x 0.2 of the screen (floats 0x3e800000, 0x3e4ccccd), hidden, unless given a widget.
  `ToolTipFixed` (`FUN_140921440`, skin `Kenshi_GenericTextBoxSkin`, an anchor widget), `ToolTipInventory` (`FUN_140921510`, template `Kenshi_FloatingPanelLight` = `Kenshi_FloatingPanelSkin`
  holding a `Kenshi_GenericTextBox` inset 5 px), `ToolTipDynamic`, `ToolTipStatic` (empty `vfunc_8` / `vfunc_9`: it stays in its widget). Only the HUD builds a Fixed one (`FUN_14072d3b0`, anchor
  `TooltipPanel`), only the inventory code an Inventory one (`FUN_140716a40`).
- **Layout** (**Observed**, `FUN_140922680`, `FUN_1409208d0`, `FUN_140920d10`, `vfunc_9`s). One row per line: a `PanelEmpty` holding a `Kenshi_WordWrapEmpty` EditBox (WordWrap, Static, MultiLine) with the key
  aligned Left Top and, when there is a value, a second one aligned Right Top. The row sits `0.025` (`0.04` for the Inventory one) x UI scale of the widget's width from the left; its width is the key text width +
  the value text width + 2 offsets; its height the taller text. The first row starts round(0.1 x widget height) from the top, the next ones round(0.01 x widget height) below the one before, and the panel ends round(0.1 x
  widget height) below the last row. `DAT_1421337f0` is the UI scale (it multiplies pixel sizes of 10, 16, 24, 200). Meitou takes the scale as height / 1080 and right-aligns every value to the panel's inner edge.
  The HUD's Fixed tooltip is built on the `TooltipPanel`'s first child, the parchment box (97.2 % of the panel height; **Verified**, the call in `FUN_14072d3b0` passes it to `FUN_140921440`), so its margins come from that box's size (7 px and 1 px at 1080 p) and the panel never gets lower than its own 72 px. Wrapping at the widget's width less the offsets is **Observed** from the row being the whole widget.
- **Skins:** Faithful: Floating `Kenshi_GenericTextBoxSkin`; Inventory and Fixed `Kenshi_FloatingPanelSkin` with `Kenshi_GenericTextBoxSkin` inside (inset 5, resp. 97.2 % of the height as the layout's `TooltipPanel`),
  rows in `Kenshi_StandardFont_Medium` measured with the painter's resource-font measuring; the exe's static tags become the palette's Main, Secondary (`#140806`), Title, Bad, BadBright, Good and Greyed, so a palette mod (Dark UI) stays readable (engine choice). Meitou UI: the same rows on one `rust` plate (radius 10, drop shadow; [Meitou UI](../meitou-ui.md)), colours of the kit
  (Title paper-light, Bad / BadBright bad-bright, Good good, Main and the rest main, greys greyed-bright).
- **Backpack rows** (CONTAINER, `FUN_1407aa090`, **Verified** decompile; constants and colour tags from `.rdata`): the name (Title), then ` (` + `empty` + `)` when its inventory
  (vfunc `0x160`) holds no items; `Buyback`, `Stolen`; `-Weight` `{1,num} kg` (#140806 both) of int(vfunc `0x248`, the shared getter of Item `+0x120`): the bag's own weight,
  without its contents (**Verified**: the section sum below adds them apart); the prices; `------------` (12 dashes, no tag); `-Encumbrance reduction` (Good) `{1,num}%` (Title) of
  int((1 - `encumbrance effect`) × 100), the field read as 0 when not positive; `-Combat skills bonus` (#140806) only when the int is not 0, `{1,num}` Bad or Good `+`;
  then `-Athletics effect`, `-Combat speed effect`, `-Stealth skill effect`, `-Dodge skill effect` (Main) from `athletics mult`, `combat speed mult` and twice `stealth mult`
  (negative read as 1), each `FUN_14079d450`(v, 1): `{1,num=fix,p=2}` + `x`, Bad below 1, Good above, Main at 1. The description follows (second tooltip function).
  Meitou: `ContainerRows`, the name suffix by `BagItems`, the weight `weight kg`.
- **Section weight** (`FUN_140749160`, **Verified** decompile): a section's weight (`+0xb0`) is the sum of its items' vfunc `0x250` (count × `+0x120`); a CONTAINER (type `0x2e`)
  adds its contents (`FUN_1407473d0`, the sum of its inventory's section weights) times `encumbrance effect` only when the section is named `backpack_attach`, in full anywhere
  else (main grid, a storage, the ground, a bag in a bag); then × the section's multiplier `+0xac`. The item on the mouse still counts in the section it was lifted from
  ([Holding an item](#6-inventory-trade-dialogue-build)). Meitou: `ItemInstance.Weight`, `Inventory.ContentWeight(HeldItem)`.
- **HUD texts** (**Verified**, the type 0 setter's callers `FUN_14072b540` and `FUN_14072d3b0`): per widget `OrdersBlockButton` (defensive mode, "gain 20 to his melee defense skill"), `OrdersHoldButton` ("HOLD POSITION ..."),
  `OrdersPassiveButton` ("In non-combat mode ..."), `OrdersChaseButton` (the Jobs toggle: "If this is disabled then the character won't do any of the jobs listed."), `OrdersTauntButton`, `OrdersRangedButton`,
  `OrdersSneakButton` (two strings joined by a line break), `OrdersMedicButton`, `OrdersRescueButton`, `OrdersProspectButton`, `ShortcutInventoryButton` ("Open character inventory"), `ShortcutStatsButton` ("Open
  character status window"), `ShortcutMapButton` ("Open world map"), `ShortcutTechButton` ("Open research window"), `ShortcutSquadButton` ("Open squad management window"), `ShortcutHelpButton` ("Open help window"),
  `BuildButton` ("Opens the BUILD menu ...") and the floor arrows (the texts name Page Up / Page Down). The texts are in `PlayerInterface.HudTips`; they all show in the Fixed tooltip. The wording of "Pause Game", "Normal
  game speed", "Faster game speed" and "Fastest game speed" is no tooltip: it sits with the key binding names of the controls list (`FUN_1403f0260`, **Observed**), and `FUN_14072d3b0` sets no text on the
  `TimeSpeedButton`s (**Verified**: it makes nine text tooltips, those above); the speed buttons have none. Which tooltip the Chase widget's caption belongs to is **Observed** from its Jobs binding.
  - Blood: "Racial bleed rate multiplier: x{1,num=fix,p=2}" (the race's `bleed rate`), Bad above 1, Good below.
  - Hunger: the Hunger Rate rows (`FUN_14088a2b0`, below) first, then the sentence as a row of its own (**Verified**, decompile; Meitou does both). With h the stomach store (+0x64): h > 2 "You are well fed and currently have no problem."; 1 < h ≤ 2 "You are suffering from malnutrition, Your stats and
    strength will be affected, you need to get some food as soon as possible. Stat penalty is {1}" (`FUN_1406457e0`, `x` suffix, Bad), with food stored "You are
    suffering from malnutrition, but you have food so you are recovering. Stat penalty is {1}"; h ≤ 1 "You are starving to death. Get some food fast. When the red
    bar goes below {1} you will pass out. When it reaches zero, you die.", passed out (`FUN_140644320`) "You have passed out from starvation. You're all alone and
    going to die. Nobody cares." (squad under 2) or "... One of your squad mates will have to feed you or you're going to die.", with food stored "You are slowly
    recovering from starvation." / "You have passed out from starvation. You have food though, so hopefully you will recover."; skeletons "Skeletons don't need to eat.".
  - Health word / tooltip: Dead "Gone forever. RIP."; BEING EATEN ALIVE!! "Something is eating you alive! AAAAAAHHHHHHHHHH!!"; Dying "If you don't get medical
    attention, you will die." or, blood at 0 with a destroyed limb, "Severe limb damage has now led to critical blood loss. You will die without medical attention.";
    Unconscious (N) / Rebooting "Knocked out cold."; Recovery coma "You've suffered too much damage. You won't be getting up again until all critical body parts have
    recovered above 0."; Playing dead... "This person has regained consciousness but is staying down, waiting for all the enemies to leave the area."; Blood loss KO
    "Unconscious from blood loss. It will recover over time, bed rest will help."; Bleeding out "Unconscious from blood loss and bleeding to death. Hope that the
    bleeding stops before it's too late."; Crippled "Your legs are incapable of supporting you."; Crippled arm "Your sword arm is crippled and you are unable to
    fight."; Critical "Critical injuries will continue to degenerate without medical attention."; Limb trauma "One or more limbs have been seriously crippled by
    critical injury."; Staying low "You are staying down, hoping to avoid drawing attention."; Starving "You are starving to death. Get some food fast."; Bleeding
    "Losing blood, will collapse if blood goes below 0."; Disguise Blown! / Discovered! with "Cooldown {1,num} second(s)"; Hungry "You are hungry and suffering from
    malnutrition. Your stats and strength will be affected, you need to get some food as soon as possible.", Malnourished "... but you are fed and slowly
    recovering. Your stats and strength will be affected until you recover fully."; In Disguise "You are currently disguised as {1}"; Slightly Injured, Wounded
    and Normal "You are kind of healthy for now.".
- **Stats line tooltips:** the HUD stats and extended lines' row tooltips are described under [section 4](#4-hud-mainbargui) ("Stat tooltips").
