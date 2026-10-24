# UI screens

Original MyGUI stack/resources/layouts, MainBar/Overview and menu/inventory/trade/dialogue/build/
tutorial binary research. Catalog and named checks below. Current native compatibility:
[MyGUI](../formats/mygui.md), [parity](../faithful-ui-parity.md), [native API](../native-ui.md).
Input/action encoding: [ui-input](ui-input.md); gameplay backends are independent of UI appearance.

## Title artwork and location

**Unknown:** an exact playable-world location matching the cover/poster composition (wanderer,
flared ruined tower, moons). [Lo-Fi's wallpaper page](https://lofigames.com/kenshi-desktop-wallpapers/)
publishes the image without a location. **Observed:** [community discussion](https://www.reddit.com/r/Kenshi/comments/znnfwj)
describes it as artwork and suggests several different towers; this is not developer confirmation
that the scene is absent from the map.

**Verified against the installed base placement layers using `WorldLevelData.Buildings()` and
the merged building database:** the Watchtower record `4104-D-TwoStorey.mod` has placements
`2-rebirth-INGAME` in zone 19.13 at approximately X=-57608, Z=-86364 (nearest town: Burn's Tower)
and `2-Newwworld-INGAME` in zone 24.31 at X=-36274, Z=-707 (nearest town: Dust King Tower).
These are inspection candidates, not verified poster matches. Searching tower names/mesh paths
and their placements does not establish that every differently named ruin has been checked.

**Observed (file search of the install and `data/gui/images/kenshi_images.xml`):** the brush-stroke
"Kenshi" wordmark has no file of its own; it is painted into the title backgrounds
`data/gui/gfx/title_screen1/3/4.dds` (1920×1080). Meitou therefore ships the logo itself
(`assets/branding/kenshi-logo.png`).

## Flow and data

Main menu → start selection/character creation → HUD, with save/load/import/options and pause
state handled separately. Layout/widget resources do not imply all controls are implemented.
HUD consumes selection/inspection, medical/stats/skills/extended values, faction/state and squad
portraits; inspecting another faction fills the panels with its information only (the player's selection stays: a player character is always selected in the original per playtest notes, **Observed** only; whether it is dropped for a moment is **Unknown**). Player commands must still target own squad.
Overview separates squad/faction/map/AI/jobs/research; inventory/trade needs footprint/quantity/
charge/prices/ownership/fit validation; dialogue/build/tutorials have distinct state/controllers.

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
| Inventory variants | 140154cd0 generic, 140154f80 generic fixed, 140155220 animal, 140155910 research, 140155ee0 trader, 140155f90 building, 1401573d0 limbs ([limbs window](#limbs-window)) | | Window | container contents; which building opens which: [selected building](#selected-building). An animal's inventory and loot use `Kenshi_InventoryAnimalWindow` (main grid, `backpack_attach`, OPEN BAG, data panel; no equipment slots; Meitou swaps the layout by the shown character, #32) |
| CharacterTradingWindow / Box | 14072a880 / 14072a070 | Kenshi_CharacterSelectionWindow / Box | Middle | character picker for trade ([economy.md](economy.md#12-slaves-prisoners-animals)) |
| DialogueWindow | 140725740 | Kenshi_ConversationPanel | Middle | portraits, names, dialog line, money ([dialogue](dialogue.md#ui)) |
| DialogueSpeechBubble | 140725f30 | Kenshi_SpeechPanel | Dialog | floating speech ([dialogue](dialogue.md#ui)) |
| ContextMenuGUI | 1407977a0 | Kenshi_ContextMenu | Popup | verbs, see [ui-input.md](ui-input.md#context-menu-verbs) |
| BuildModeWindow | 1404e5210 | Kenshi_BuildWindow | Middle | categories, buildings, stats, Commands box, Confirm / Undo / Build, floor arrows |
| CharacterEditWindow | 1405f4dd0 | Kenshi_CharacterEditor | Back | character editor |
| MessageBox | 1406e70a0 | Kenshi_MessageBox | Info | message, buttons (Return = first, Esc = last) |
| MessageRoller | 140724af0 | Kenshi_MessagePanel | Popup | rolling event messages |
| Tutorial panel / list | 140975ca0 / 140977640 | Kenshi_TutorialPanel / Kenshi_TutorialsWindow | Window | tutorial text |
| OpenSaveFileDialog | 1401a0b10 | Kenshi_OpenSaveFileDialog | Window | file dialog; Meitou uses it for the editor's IMPORT only ([characters](../characters.md#import-and-export)) |
| InteriorModeButtonWindow | 14077a1f0 | Kenshi_InteriorMode | Back / Main / Window (several widgets) | interior mode toggle |

**Observed** (installed `data/gui/layout/Kenshi_AdvancedOptionsWindow.layout`, XML inspection): the layout contains only
Root, Datapanel and TooltipPanel, with no option controls. Loading the layout alone cannot populate the options.
**Observed** (user's original-game screenshot): the bottom panel explains the hovered option; it is not a Reset/Close button area.
**Observed** (installed `locale/en_GB/LC_MESSAGES/main.pot` and the eight non-English catalogs, de/es/fr/ja/ko/pt/ru/zh; no test checks it):
`kenshi/titlescreen.cpp:557..563` each identify a label and a longer description, in hunger/death/damage/production/research/building/nests
order, and all seven have translations. Runtime reads the template and translates the exact message key.

### Splash

**Verified** (decompile of `FUN_140916140`, `FUN_140914850`, `FUN_140913720`): a full-screen `WhiteSkin` widget coloured black, holding one ImageBox per
slide, `Kenshi_Splash_Logos` then `Kenshi_Splash_LoFi` (`lofi.png`, 677 x 700), all hidden at first. A slide's height is the image height x `DAT_1421337ec` x a
factor (100 for Logos, 0.7 for LoFi), capped at the screen height; its width keeps the image's aspect; it is centred (integer pixels). Each frame the current
slide is shown with alpha min(1, 3 x seconds since it started); a press (any of four input flags) is remembered; once the slide has been drawn at alpha 1
on more than three frames, a press or more than 4 s ends it: hidden at once (no fade out) and the next starts. `DAT_1421337ec` is **Unknown** (beside the
UI scale `DAT_1421337f0`; Meitou takes height / 1080, which leaves Logos at the screen height either way).

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
  shows only when the last save's `quick.save` exists. Version text: the literal "Kenshi 1.0.68 - x64 (Newland)" (Meitou: "Meitou - Faithful UI").
- **Credits** (`FUN_140914230`, `FUN_140913880`): `data/credits.txt` read once, empty lines dropped, a "-" line a blank one, untranslated, in
  `CreditsPanelContent` (white, "HCenter Top"); the content starts 0.85 of `CreditsPanel`'s height down and moves up 25 px a second of wall-clock
  time, with no loop or stop. Credits again restarts it. `CreditsPanel` is a Widget skinned `Kenshi_EditBox`: its text box covers the panel, not
  the template's Client ([MyGUI](../formats/mygui.md)).
- **Placement**: the start and import windows at 0.5, 0.05 of the screen (0.6, 0.05 for the start window from the in-game menu); the load and save
  windows centred, with the left edge at the screen's centre while the title exists; the advanced options under the start or import window.
  Those windows leave the title's buttons live (another button replaces them; Meitou: modal screens that pass clicks through, `MyGuiScreenHost.PassThrough`).
  Esc closes the top window, else the credits (Meitou).
- **Advanced options help**: the last option whose name was under the pointer, kept while the panel is open (Meitou: the name column only, so the
  pointer can reach the help's bar down the right side); a text taller than the panel scrolls with a
  vertical bar (**Observed**).
- **Exit** (`FUN_140914600`, message helper `FUN_140742130`): title "Exit", text "Are you sure you want to exit?", buttons Yes and Cancel, upper-cased;
  the helper translates its captions (Ok, Yes, No, Cancel, Abort) once into statics, and the French game shows "CANCEL" untranslated (**Observed**).
- **Start window**: "Choose your beginning", "Advanced options", "Begin", "Cancel"; the start's name. A DatapanelGUI in `DescriptionPanel`:
  "Difficulty:", "Cash:", "Play Style:" (not upper-cased) with the record's `difficulty`, "c."`money`, `style` as stored (French keeps "Default",
  "RPG"), then `description` as a wrapped line. Rows: pitch 0.94 / max(1, (int)(40 x panel height / screen height)) of the panel, from 0.03 of
  it; label at 3 % of the width, 96 % wide; value right-aligned in 93 %. First start "1980-gamedata.base" (Wanderer); the arrows wrap.
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
- Meitou (`LoadingScreen`, Faithful mode): as above. `tips.txt` is read as UTF-8, else Windows-1252, since the French, German and Spanish files are
  Windows-1252 (engine choice: how the original shows those bytes is **Unknown**). The Meitou mode keeps its own panel, spinner, bar and slide-out.

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
  original's queue holds the meshes and textures it loads in the background; terrain and foliage streaming are left out). It is drawn last on the
  loading screen and over the in-game UI.

## 4. HUD (MainBarGUI)

Layer Middle, per-frame update FUN_140728d50 (**Observed**):

### Squad bar

**Verified** (decompiled `FUN_14072d3b0`, `FUN_140415e00`, `FUN_1404131e0`, `FUN_140412ed0`; constant 10 read from the exe):
`SquadPanel`'s layout TabItems ("Squad 1..3") are removed at start (`removeAllItems`); a tab per squad is added at run
time, each page holding an `item_box_v.layout` ItemBox (vertical, fills the page) of `Kenshi_PortraitCharacter` cells. Cell
side = min(int(0.9 × box height), int(0.9 × box width) / n), n = members (at least 1; 10 when more than 9), clamped to 60..128 px
and rounded down to even. Cells fill rows left to right and wrap; the ItemBox scrolls past the last row. Matches the earlier
observation of frames at ~72% of the page at 1080p (the 128 px cap). Meitou scales the px limits by height / 1080 and scrolls one
row per wheel notch (the original's step per notch is **Unknown**).

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

So each character keeps one random angle for life. Most come out three-quarter, some nearly frontal; none are profiles. **Observed**:
the user's base-game save atlas shows a near-frontal face.

**States** (**Verified**, `FUN_140414dc0` sets the flags, `FUN_140415150` applies them to the `Kenshi_PortraitCharacter` cell). The cell has
`BackgroundImage`, `BackImage`, `PortraitImage`, `FrontImage`, `NameText` and the `BorderPanel` button. The image set is
`Kenshi_PortraitImage` in `kenshi_images.xml`.

| Flag | Set when | Shown as |
| --- | --- | --- |
| 1 | the character is selected | the `BorderPanel` button's selected state |
| 2 | the character is ≥ 1000 units from the camera and ≥ ~316 from the camera's focus, and either (a) it is not down (slot 6), the float at its AI `+0xb0` is > 0 and it is not imprisoned, or (b) `+0x279 & 7` | `Background` `Hurt` (red) instead of `Normal`, flashing |
| 4 | dead or unconscious (slot 107: `+0x5bc` / `+0x5b9`), carried (`+0x3d4`), or in a bed (`+0x2f8 == 1`) | `PortraitImage` alpha 0.25 (faded); this also stops the flash |
| 8 | in combat (`FUN_140436610(c, 1)`) | `BackOverlay` `Combat` |
| 0x80 | hunger (`+0x4b8`) < 1.25 | `BackOverlay` `Hunger`, only when not in combat |
| 0x10 | the inventory holds, in section 9 or 5, an item whose slot 94 answers non-null (`FUN_1405c8a10`). Only `LockedArmour` overrides slot 94. The item loader makes a `LockedArmour` for ARMOUR with `is locked` (`FUN_140580750`). That is, shackles. | `FrontOverlay` `Slave` |
| 0x20 | imprisoned (`+0x2f8 == 2`, cage or restraint) | `FrontOverlay` `Prison` (wins over Slave) |
| 0x40 | `+0x279 & 7` | `FrontOverlay` `Eaten` (wins over both) |

- **Flash** (PortraitData `+0x28`, a direction of ±1). When flag 2 turns on, the direction starts at −1. Each frame the background's alpha
  changes by direction × frame time × 2. At 0 the direction flips to +1, and at 1 it flips back to −1. That is a full 0 → 1 → 0 cycle
  every second. Without flag 2, or with flag 4, the alpha stays at 1.
- **Unknown:** what the AI `+0xb0` float means. AI slot 25 (the eat desire, [eating](character-stats.md#eating)) also suppresses itself while it is > 0, so it reads as
  "danger seen". Also unknown: what `+0x279 & 7` holds. Sections 9 and 5 are the ARMOUR `slot` values of the shackle records (`Prisoner Shackles` 9, `Hive Prisoner Shackles` 5; **Observed**).
- A portrait is re-rendered when its flags change, when its character changes, or when PortraitData `+0x30` / `+0x58` is set.
- A cell wider than 94 px switches `NameText` to a larger font.

Meitou (`PlayerInterface.PortraitStates.cs`):
- The serial is `CharacterCold.PortraitSerial`. It is read and written as `portrait_serial` (or taken from the handle's serial), and rolled at spawn otherwise.
- "In danger" is taken to mean "in a fight". The `Eaten` flag is never set.
- Shackles are worn ARMOUR with `is locked`, which the original makes a `LockedArmour`.
The HUD stats labels (`StatsText`: MARTIAL ARTS ...) are drawn in the painted face, Sentencia at its resized 22 pt, 15.3 px at 1080 p
(**Observed**: glyph shapes and widths in a base-game screenshot; [mygui.md](../formats/mygui.md) for the resize); Meitou uses
`Kenshi_PaintedTextFont_Medium` there. The values' face in the original is **Unknown**; Meitou draws them in the standard face at the HUD size.
JOBS and RANGED start enabled (user-reported original defaults).
HUD text size: `Kenshi_StandardFont_Medium` (the MyGUI default font, `core_settings.xml`) is Exo2 SemiBold 20 pt at 50 dpi, a 13.9 px
em (**Verified**, `kenshi_fonts.xml`). The base game draws HUD text at that size at 1080 p (**Observed**: user screenshots; Meitou's
former 16 px floor came out 10-15 % wider). Meitou uses it as the in-game minimum and for the HUD panel and body-part lines.
HUD text colours (**Observed**, ink sampled from a base-game screenshot): StatsText labels (MARTIAL ARTS ...) are grey ~#69625e,
taken as the skins' `0.4 0.380392 0.356863` (#66615b, also the orders buttons' text); the jobs hint is ~#483b30, which is
Secondary at the `JobsPanelInfo` EditBox's `Alpha` 0.5 (**Verified** property, `Kenshi_MainPanel.layout`): MyGUI multiplies a
widget's alpha into its children (`InheritsAlpha`, default true), and the text is drawn by the EditBox template's client.
**Verified** (`Kenshi_MainPanel.layout`): `OrdersChaseButton` is captioned JOBS; Meitou binds it
to Jobs; the saved `chase mode` flag is that switch (**Verified**, [perma-jobs](ai-tasks.md#perma-jobs)).

- Shortcut buttons INV, STA, MAP, TEC (research; user-requested caption; installed layout says TCH, **Verified** by reading `Kenshi_MainPanel.layout`), SQD, HLP; floor panel with text "Floor {1,num}"; four speed
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
  - Name: the character's name left and its faction's name right (the player's default faction name is "Nameless"), both Title.
  - State lines: `State:` Dead / Locked (lvl: N) / Sleeping / Unconscious (Rebooting for skeletons) / Defensive / Normal (Bad for the bad ones);
    `Goal:` the AI goal package's name, else Aimless (Meitou: when the AI is not running the character, its movement task's name, e.g. Moving, Attacking, else Aimless); `Encumbrance:` (int)((1 - f) × 100) "%" with f the encumbrance factor in 0..1, Bad from 50 %.
    Extended (when expanded): Athletics XP, Strength XP, Toughness XP, KO point, Run speed, Hunger Rate.
    Extended values (**Verified**, decompile of `FUN_140895350`, 2026-10-14; Meitou: `BodySystem.ExtendedOf`, `PlayerInterface.ExtendedLines`):
    Athletics XP `{n}%` = (int)(100 × `FUN_1408c5ec0`): the encumbrance factor, ×1.5 when the current speed is over 80 % of the run value V (the `xp rate athletics` constant is applied elsewhere).
    Strength XP `{n}%` = (int)(100 × `FUN_1408c60c0`), the carrying rate (includes `xp rate strength from walking`); with a weapon in hand the original shows a "Weapon weight" figure from a field at `+0x184` instead (**Unknown**, Meitou keeps the carrying rate).
    Toughness XP `{n}x` (the suffix is the `x` at `0x1416bc008`, **Verified** in the exe) = `FUN_1408c5f40`: 1, but 2 with the left arm crippled, +2 right arm crippled, +2 blood-loss KO, +2 when a counter at `+0xa4` is under 15 (**Unknown**, Meitou omits it).
    KO point = (int)`FUN_140643ae0` (negative HP, [character-stats.md](character-stats.md#hunger-blood-and-parts)). Run speed = the current top speed S × 0.22369362 (units per second to mph) rounded, `{S} (+{d}) mph` or `(-{d})` where d is S minus V in mph; plain when equal. Meitou adds an Interface setting "Speed units" (`UserConfig.SpeedUnit`, default mph): km/h multiplies by 0.36 instead. Hunger Rate `{n}%` = (int)(100 × `FUN_1408837a0`), the race's hunger rate × its situation factor.
    Each line also carries a tooltip with its breakdown (Encumbrance penalty, Full speed bonus, Carrying somebody, Crippled arms ...), not modelled ([Tooltips](#tooltips)).
  - Stats lines: Martial Arts when unarmed; Crossbows, Precision, Perception with a crossbow in ranged mode; else Attack and Defense; then Strength,
    Dexterity, Toughness and (not in ranged mode) Dodge. A value is the effective number, then its change in brackets: `{1} ({2})` all Bad when lower,
    `{1} (+{2})` all Good when higher, plain when equal. The code passes the labels in mixed case ("Martial Arts:"); the original shows them upper case in
    a light colour (**Observed**, screenshot), from a source **Unknown**.
  - `HealthText` words in priority: Dead, BEING EATEN ALIVE!!, Dying, Unconscious (N) (N = (int) of the medical float `+0xa0`, the KO timer, taken
    only while it is &gt; 0; "Rebooting" for skeletons; `FUN_14088a8f0`), Recovery coma, Playing dead..., Blood loss KO, Bleeding out,
    Crippled, Crippled arm, Critical, Limb trauma, Staying low, Starving (hunger &lt; 1), Bleeding, disguise words; else by the overall health h:
    Hungry (h ≥ 0.7 and hunger &lt; 2), Wounded (h &lt; 0.7), Slightly Injured (h &lt; 0.9), Normal. What h is exactly is **Unknown**; the worst part's
    fraction matches a screenshot (a leg at 77 % and "Slightly Injured", **Observed**).
- Side-panel rendering follows the [main UI font policy](../formats/mygui.md),
  with 20-pixel row spacing at 1080 lines for name, state, XP and combat stats. Extended
  labels use the same uppercase Main colour as combat stats (**Observed** in the
  supplied original Metaru screenshot).
- Medical rows (**Verified**, decompile): Blood (Oil for skeletons) first, then the body parts, Hunger last; each `LifeBarNDatapanel` gets the name centred
  and an arrow string right-aligned in Alert; the number goes in `LifeBarNValue`. Layers are sized as width fractions from the left (Grey is a full-width
  background). Part (H health, S a deduction, D a pending amount, Mx its maximum): Green (H − S)/Mx, Yellow (H + D − S)/Mx, Red (Mx − S)/Mx, White
  splint % × 0.01 + green, number floor(H − S + 0.1); arrows `<<   ` below 0 and degenerating, `<<!!   ` at the KO point, `!!!   ` destroyed. Blood:
  green blood / capacity, number floor(blood + 0.1), arrows a run of `<`, ceil(|bleed rate| / 0.05) of them (at most 100). Hunger (0..3): number
  (int)(hunger × 100), Green hunger − 2, Yellow hunger − 1, Red hunger; arrows `>   ` recovering, `<!   ` below 2, else `<   `. The parts come in the
  medical part array's order ([part order](character-stats.md#part-order), **Verified**): head, torso, arms, legs, left before right.
- Information panel: `ExtendingPanel` starts collapsed, its bottom moved onto the panel's bottom (behind the Overlapped `StatsPanel`), and slides over
  0.5 s to its layout place when `ExtendedStatsButton` is pressed (**Verified**). `BountyPanel` shows only for one selected character with a bounty or
  a status line (Imprisoned, Slave, Escaped slave, Committing Crime!, Leader...; **Verified**).
- Status rows (`FUN_1405d4260`, **Observed**; the panel's tooltip builder with a mode argument, which tooltip it is is **Unknown**; Meitou puts the slave rows ahead of the bounty rows): a character in slave state 1 / 2 / 3
  gets a title and a text, with `{1}` the owner's name ("someone" without one; the owner faction object's name `+0x1a8`, else the owner character's):
  "Slave" - "A slave belonging to {1}. Not considered a person with rights, a slave works for his master until death and nothing more. {2}"; "Escaped slave" - "A slave of {1} that has been seen
  escaping. Needs to lay low for a day and avoid getting spotted. {2}"; "Ex-slave" - "Was once a slave of {1} but has now escaped. The heat has died down but he still needs to avoid getting found out. {2}".
  `{2}` is empty for a character with no squad (`+0x250`), else the hint: for state 1 "Your character will have a permanent job assigned called [Obedient slave]. While this is active he will
  automatically work for his master. You can remove this job, but beware of the consequences...", for 2 and 3 "Keep your [Looks Like A Slave] level as low as possible, wear clothing or disguises and
  find a plastic surgeon to restore your hair. Don't get spotted." (3: "... You should be safe if you stay out of towns."). For state 2 and 3 the same tooltip adds rows "Situation:" ("Undiscovered" when the
  status `+0x110` is 0 or less, else "Seen!"), "Looks like a slave:" (`+0xf4` x 100 %) and "Time remaining:" (`FUN_140598b10` hours); the free-person text ("You are a free person.", "You are no slave.",
  "The faction that owns you.") belongs to the player's disguise tooltip. Other rows of the builder: "Leader", "War Leader", "Kidnapped", "Escaped Prisoner", "Committing Crime!", "Imprisoned:"
  (not built). Meitou: `PlayerInterface.SlaveRows` (the hints for player characters only; engine choice).
- Bounty panel (`FUN_1405d2da0`, **Observed**; colour strings **Verified**: their static initialisers `FUN_14158a2a0` `#afa68b` Main, `FUN_14158a320` `#492620` Title,
  `FUN_14158a460` `#444444` Greyed). The *local faction* (`FUN_140852010`): while imprisoned (`+0x2f8 == 2`) the owner of the cage at `+0x300`, else the faction of the
  status object's `+0x88` (**Unknown**; Meitou: the town the character stands in, nearest whose radius holds it).
  - `BountyText`: `Bounty:` in Title and the local bounty in Main; not wanted locally, both Greyed with the *largest* bounty (`FUN_140853740` takes the max, not the sum).
  - Tooltip (attached to `BountyPanel` in `FUN_14072d3b0`), rows per faction: name and `c.{1,num}` (Main for the local one, else Greyed); `-Wanted For: ` and the first
    four crimes joined by `, `, a row keyed `" "` with the rest (untagged for the local faction, Greyed otherwise). Then, imprisoned: "Imprisoned, sentence time:" `FOREVER`
    (length >= 2000), "Imprisoned, sentence time remaining:" ceil(length - elapsed) `{1,num} hr(s)` (both values Title), or "Imprisoned" / "Waiting to be released";
    otherwise a `-------------------------` row and "This character is wanted by the authorities. Turn them in for a reward." with, after a line break, `FUN_1405d1110`:
    "The bounty will expire in {1,num} hr(s)" (int(0.04 × amount − elapsed), Main), "Notorious: The bounty will never expire." (amount > 9999, Title) or "This character
    is not wanted by the local faction" (Main). Meitou puts the expiry on a row of its own (an inline tag only leads a row) and, in the Meitou UI, under each faction's rows.
- Inspecting another faction's character (**Observed** in the original, not traced in the code): a left click on a character that is not yours fills the information and medical panels with that character (name, faction, state, stats, extended stats, medical rows). Meitou keeps this as display state, `PlayerState.Inspected` set by `InspectCommand` (not hashed, not saved); your selection is kept (Meitou: someone is always selected, `PlayerState.Prune` picks the first living character of yours when every selected one is gone or dead); a click on your own character or the ground clears the inspection.
- Money panel: `MoneyLabelText` is set to "Money" (without the layout's colon; 25 % opacity when the amount would overlap it), `DayText` "Day: {1,num}", `TimeText` HH:MM (**Verified**). A "1x"
  seen next to the clock in a player's screenshot is not written by the vanilla code (**Observed**; most likely a mod).
- Squad tabs: one per player squad with members, captioned with the squad's name unchanged (**Verified**); the first player squad reads `Nameless_0`,
  its platoon's name (the faction's name and its platoon counter; **Observed**). Tab width min(header width / tabs, ButtonWidth × UI scale), in the skin's
  `HeaderPlace`. The bar shows the active squad (`FUN_1407f5520`: a cached platoon at player interface +0x2a8, else the selection's, else the faction's
  first; **Observed**); a tab makes its squad active. Meitou: selecting a character of another squad makes its squad active (engine reading of the cache).
  Editing squads: [Squads tab](#squads-tab).
- `FloorPanel` stays as laid out and `FloorText` is "Floor {1,num}" of the viewed floor each frame; `BuildPanel` is always shown (**Verified** /
  **Observed**).
- Orders panel (FUN_14072b540): buttons Block, Hold, Chase (`OrdersChaseButton`), Passive, Jobs, Ranged, Taunt,
  Sneak, run-speed arrows (`SpeedArrowLeft/Right`, see below), Medic, Rescue, Prospect, a behaviour slider (captions AGRESSIVE / CONFIDENT / SUBMISSIVE,
  **Verified** in `Kenshi_MainPanel.layout`) and a jobs list ([ai.md](ai.md#packages-and-goals)) with the hint "To add jobs, hold shift key when
  giving an order". Jobs list (`JobsPanel`, an ItemBox; class `JobsPanel` with `JobInfo` rows built as `DataPanelLine`s, RTTI): "assigned player jobs and
  orders for the selected character"; each row has an X that removes the job, top rows take priority and rows can be dragged to reorder (tutorial strings,
  **Verified** in the exe). Empty, `JobsPanelInfo` shows "[No jobs assigned]" and the hint. The row text is **Unknown**; Meitou shows each job
  as its type's name (with the building) and an X (`Kenshi_CloseButtonSkin`), dragged to reorder. Hold = hold position; Block = defensive mode ([combat.md](combat.md#techniques-block-and-dodge) has a +20 guarding-state term in the block chance; whether it is this stance is **Unknown** there) (+20 melee defence, no attacks; **Verified**
  against the tooltip string in the exe: "gain 20 to his melee defense skill ... will not attempt to make any
  attacks"); Passive = non-combat mode (tooltip strings; all the orders panel's tooltips: [Tooltips](#tooltips)).
- Speed selector: the `SpeedPanel` ImageBox shows an image of `Kenshi_CharacterSpeedSet` / `Icons`, indexed Walk, Jog, Run, Group;
  the index is the movement mode ([pathfinding.md](pathfinding.md) "Speed"). Each arrow steps it by -1 / +1 with wrap over all four,
  plays `General_Click` and applies the mode to every selected character; a refresh shows the lead's mode. Right after the panel is
  built the index is 0 (Walk) until the first refresh. **Verified**: `kenshi_images.xml`, the panel's name list in FUN_14072b540 (exe bytes
  `Run`/`Jog`/`Walk`), arrows FUN_140722580 / FUN_140722630, apply FUN_140722500, refresh FUN_140726930. While any selected
  character's state at +0xe0 is 1 the arrows re-apply without stepping (FUN_1407f3a20); what that state is is **Unknown**.

### Biome banner

**Verified** (`MainBarGUI::vfunc_10` `140728d50`, the HUD's per-frame update; save `FUN_140728af0` / `FUN_140728c10`) unless noted. `BiomeTextPanel` (full width, 0.24 to 0.36 of the screen height, hidden in the layout)
holds `BiomeTextBox` (`Kenshi_BannerTextFont`, centred, shadowed) between `BiomeNameTopLine` and `BiomeNameBottomLine`.
- **Region.** The BIOME_GROUP of the area sector (64 x 64, `(c + 147465) / 4608` per axis, `FUN_1408f56b0`) under the camera pivot (`DAT_142133310 + 0x58`, **Observed** as the pivot). A group named `NONE` (bytes at `1416c4bf4`) or with id `18003-gamedata.base` never shows.
- **Track.** While the LoadingWindow is up (`FUN_1406e2100`) the stored group is only set to the one under the pivot (a load announces nothing). Else, each frame with dt the real frame time (`DAT_142133790`, [game-loop](game-loop.md#frame-clocks)): `hold -= dt`, `cooldown -= dt`.
- **Show.** The group differs from the stored one, `cooldown <= 0` and `**(DAT_1421337c0 + 0x128)` is null (**Unknown** what that is): the text is the group's record name, `hold = 5`, `cooldown = 30`, the panel appears at alpha 0 and `New_Biome` is posted
  ([audio](../audio.md#interface)). The code also stores 480 when the group is the one shown before, then overwrites it (**Observed** dead store). Lines: the panel grows to the text height `ceil(0.65 h)` + 4 line heights; the top line
  starts at the text's left, the bottom one ends at its right (both centred when the text is narrower than the lines).
- **Fade.** Visible: alpha + 2 dt up to 1 while `hold > 0`; once `hold <= 0` alpha - dt, hidden below 0. On hiding the game stores the group and the time and tests its `arrival dialog` (`FUN_1408015d0`; effect **Unknown**, [dialogue](dialogue.md)).
- The stored group (`+0x1d8`) follows the pivot's every frame unless the `+0x128` test blocks it, so a region crossed during the cooldown is never announced.
- **Save.** CAMERA `biome_text_timer` = the cooldown, `biome_text_groud` = the stored group's string id (empty: none), set back on load. `area` (`FUN_140375360`) = the name of the group under the pivot, empty for `NONE`.

Meitou (`PlayerInterface.Biome.cs`, both UI modes): the group comes from the weather region under the pivot; the layout's own line positions (**engine choice**: the text-size layout above is not reproduced); the block is "a conversation is showing"; a new world or a load only takes the group under the pivot (a load keeps the saved cooldown, clamped at 0 on save where the original writes its raw falling value).

### Messages

**Verified** (`MessageRoller`, constructor `140724af0`, update `140724310`, show-next `140723590`) unless noted. Event messages ("Discovered {1}"...) show one at a time in
`Kenshi_MessagePanel.layout` (Popup layer; `Kenshi_MessageSkin`, a three-part paper strip, 0.359 x 0.065 of the screen; `MessageTextBox` centred, word-wrapped,
`Kenshi_StandardFont_Large` in ink `#140806`), queued. Its top-left is the absolute position of the HUD's `MessagePanel` (`14072d3b0`; lower centre, 0.32 / 0.71 of the
screen), of the build window's `MessagePanel` while that is open (`1404e5210`). Each message shows at alpha 1 for 4 s, then a MyGUI alpha fade to 0 at coef 3 (⅓ s); its end shows
the next (**Observed**: the delegate's body is not read). Nothing new comes up while the LoadingWindow is up. When an unknown widget (`DAT_1421337d0 + 0x38`) is visible the panel
sits above another one instead (**Unknown**).

Meitou (`PlayerInterface.Hud.cs`, both UI modes): its own notices (Saved, Loaded, errors) share the queue, errors in `Bad` red; always at the HUD's spot; real-time timers.

### Prospecting

**Verified** unless noted. `OrdersProspectButton` (`FUN_140721800`): with the window visible it hides it; else the selected character nearest the camera pivot (`FUN_1407f4ba0`, **Observed** over the selection) that has no task 150 gets one (`FUN_1405d20d0`,
not through the order dispatcher: no `Right_Click`) and `Prospect_Click` is posted. The key action `prospect` is unbound by default ([ui-input](ui-input.md)).
- **Task** `Task_Prospecting` 150 ([ai-tasks](ai-tasks.md)): the character stands, plays `medic`, progress += its update time until 1.0 (one game second; bar text "Prospecting"); not while swimming; no XP.
- **Result** (`FUN_14048e9e0`): at 1.0 the window gets the spot, the prospector's science (`CharStats +0xe0`, raw) and name. `FUN_14049f180` shows `Kenshi_ProspectingWindow` centred, titled "Prospecting Results", posts `Prospect_Results`, and
  `FUN_14049d850` fills it. With s = science / 100:
  - `OverviewDatapanel1`: `Zone:` the group's name; `Altitude:` `{n} m`, the ground height at the spot (**Observed**, `FUN_1409b32f0`); `Prospector Skill:` the science as an int; `Area Size:` `int((s x 6000 + 2000) x 0.2) m`.
  - `OverviewDatapanel2`: `Wind Speed:` `{x.x} mph` (wind speed x 0.22369362; the region lookup's position, the spot or the camera, is **Unknown**); `Environment:` the group's resources record `green`, `arid`, `swamp` (map keys 1, 2, 3, **Observed**) as `Green 40%`, `Arid 10%`... for those above 0 in `#afa68b`, the first on the label's line, `Dead` when none is.
  - `ResourcesList`: a `Kenshi_ProspectingWindowResourceLine` per resource in the order Water (5), Fertility (6), Stone (2), Iron (1), Copper (3); Carbon (4) is not listed. Caption the name, `ValueText` `int(100 x v)` with v = `uses_resources`
    ([buildings-production](buildings-production.md#resources)) at the spot, coloured `#a8b774` above 0.75, `#59231a` below 0.5, else `#140806`. The rows are radio `CheckboxButton`s, the first checked.
  - `Map` (`ProspectingWindow_Overview_1/2`, `FUN_14049cd60`): the checked resource over the square of half-size `s x 6000 + 2000` units around the spot, `CameraMarker` rotated to the camera heading, size `int(s x 246 + 6)` (`FUN_14048c970`).
    **Not modelled in Meitou.**
- The close button hides the window; hiding frees the rows (`FUN_14048d780`).

Meitou: `ProspectOrder` makes `CharacterTask.Prospect` ([simulation](../simulation.md#prospecting)); the window is the faithful screen `prospect` in both UI modes; wind is the host's weather state at the camera (engine choice).

### Selected building

How a building gets selected: [ui-input](ui-input.md#building-picking-and-selection). **Observed** (decompile) unless noted.

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
    true: the owner is the player's faction and the building's +0xc0 byte is clear, with an exception for `creates player town` buildings not read in
    full): a full-row button `Dismantle` (caption BadBright) and, while the construction state is not complete, `PAUSE CONSTRUCTION` (Main), captioned
    `[PAUSED]` (#777777) while paused. Dismantle is slot 84 (`FUN_14054db30`, `General_Click`): "You cannot delete this building while it has mounted
    buildings."; items in an inventory: "There are still items in the inventory, they will be lost.\nAre you sure you want to destroy this building?" (a
    research building with items refuses: "You cannot delete this building while there are still items in its inventory.\nPrecious research artifacts and
    stuff, don't lose them."); furniture: "Dismantling this building will destroy all furniture.\nDo you want to continue?"; else the message box
    "Dismantle" / "Are you sure?". What the confirmation's callback (`LAB_14002b666`) does is **Unknown**. Pause is slot 85 (`FUN_140546f40`): flips
    the paused byte, `General_Click`.
  - 6: `Open to public:` (label Main) coloured `Public` (Good) / `Private` (Bad) for another faction's building; for the player's own building in a
    town (+0x1f0, the town pointer the For sale rule reads) a `Kenshi_Button2` toggle of width 0.3 (Public in Good, Private in BadBright; slot 83 `FUN_140546ef0` flips the byte +0xc8 read by slot
    82, `General_Click`). `For sale:` with a button `c.{1,num}` (both Good) when slot 88 (`FUN_14029c500`) is true: a building of class 0xb with no
    parent whose town (+0x1f0) has its +0x2c byte clear and type 1, 2 or 3 (**Unknown** which these are), owned by a faction neither the player's nor
    allied, and its state's bool `sell home` set (true without a state). Price (`FUN_1407ad300`): int(f × target × the owner FACTION's float
    `building cost mult` × 400), f = 0.375 when slot 102 (+0x330) answers true, else 1. That slot reads the destroyed byte +0x1a1
    ([construction state](buildings-production.md#construction-state), **Verified** there against `FUN_1405595a0`). The button (slot 80
    `FUN_1407ad410`) asks "Do you want to buy this building for c.{1,num}?".
  - 4 is the medical panel's section (**Observed**: `MedicalDatapanel`'s constructor `FUN_140726a70` passes 4 to `FUN_1406fd7d0`; it finds the
    `LifeBarN` widgets and their `Value`, `Yellow`, `Green`, `White`, `Datapanel`, `Tooltip`, `Robot` and `Crushed` children (and one more whose name string was not read), and `HealthText`). The
    header word is slot 114 (`FUN_1402fe040`: Normal / Broken / Under construction with a tooltip); the rows slot 115 (`FUN_140302380`): `Condition`, or
    `Building Condition` when the building has doors (+0x1c0), the number int(progress) and the bar progress / target capped at 1 (`FUN_140547200`; the
    construction state at Building+0x160, [construction state](buildings-production.md#construction-state)), tooltip "The building is under construction
    and at {1,num}% completion." (fraction × 100) while not complete, else "The building is in good condition." at 1 or "The building is damaged.";
    then, while not complete, a row per material: its item's name, `{1,num}/{2,num}` delivered / required (as ints), the bar delivered / required, tooltip
    "All necessary materials are ready." or, in Title, "{1} more 'Building Materials' will be needed to complete this construction." with the
    missing amount (the number in Title, the rest in the default ink). A row's tooltip is set on its `LifeBarNTooltip` (`FUN_1407228a0`, the type 0
    setter). 7 gives nothing for buildings.
  **Observed** (the owner's screenshot of a town house, base game): the header word (`Normal`) and `LifeBar` rows `Door Condition` 10 and
  `Building Condition` 5, full yellow bars: the targets of the door and the house (their materials, all delivered). The name bar read the
  state's own name ("Big Shop") left and the owner faction ("Swampers") right; the info panel `Type:` (the record's name, "Swamp Shack") and
  `Town:`; the stats panel `Open/Close:` "The door is open", `Security:` "Unlocked (lvl 47)" and `Open to public:` "Public", values pale green.
  **Observed** (game data, not the decompile): many shop states carry a generic name (`Shop`, `Big Shop`, `General Shop`, `Bar`); what the
  building is lives in the SQUAD_TEMPLATE named by the state's `squad template`: `building name` ("The Dancing Skeleton") and, for a
  trader, its `vendors` VENDOR_LISTs ("medical and travel supplies"). The owner's screenshot shows "The Dancing Skeleton" in the name
  bar with `Type:` the record. Meitou names the building from `building name` when set, and, in the Meitou UI mode only, a trader's shop whose
  state name is only its layout's generic one ("Shop") from its first vendor list ("Medical and travel supplies"); the Faithful UI keeps "Shop".
  Meitou (`PlayerInterface.BuildingSelect.cs`): the header word (Normal, Broken, or a site's construction state); the condition rows as above, the bar
  in the yellow layer, a finished building's progress being its target (Meitou models no building damage) and the target of a world building or door
  the sum of its BUILDING record's materials; the player's own site gets Dismantle (after "Are you sure?" the selection dismantles it, `DismantleOrder`,
  engine choice for the unknown callback) and PAUSE CONSTRUCTION (`PauseSite`); the first door's lines come before the house's (Meitou does not focus the
  door object). Open to public is the state's `is public day` / `is public night` for the time of day (**Observed** reading), for another faction's
  buildings. A building of the player's (its site, or a town building whose fixtures the player's faction owns, `BuildingDetail.Owned`) gets the
  buttons: the door lines as full-row buttons, `[UPGRADE]` / `[DOWNGRADE]` on a site (`UpgradeSite` / `DowngradeSite`, same scope as the
  simulation's), and in a town the Open to public toggle (`SetBuildingPublic`: a site's `BuildSite.Public`, a town building's flag in the fixtures,
  both saved as `is public day`; a town building set Private drops out of the town AI's wander and bar places). **Observed** (owner's original-game Longhouse screenshot): clicking its price opens a confirmation titled with the building name, asking `Do you want to buy this building for c.{1,num}?`, with Yes / No buttons. For sale and its confirmation use the [house purchase contract](../simulation.md#town-house-purchases). Not built: Dismantle of a town building the player owns, the original's other consumers of +0xc8 (**Unknown**).
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
- **Beds, cages, containers, lamps** (Useable / Storage / Light slot 40 `FUN_140304e90`): 3 `Town:`, `Training:` (function 7), `Cost to use:`
  `Private` or `Pay c.{1,num}` (beds), `Lock state:` Locked / Unlocked, `Lock complexity:` `Level {1,num}`, `Empty`; 6 `Healing bonus:`
  `x{1,num}` (beds), `Max Skill Level:` (training); 0xf `Money:`. Turrets, machines, generators and batteries have their own lines
  (`FUN_14030d620`, `FUN_140300090`, `FUN_140307bf0` ...: Working / No power / Out of resource / Output full, power, workers, fuel).
- **Richness** (**Observed**, ProductionBuilding slot 40 `FUN_1402ffeb0` section 6 calls slot 186 `getGUIFertility` `FUN_140309a40` first): for function 1 or 27 with a
  functionality, a line by `world resource mining`: 1 `Iron Quality:`, 2 `Stone Richness:`, 3 `Copper Quality:`, 4 `Carbon Quality:`, 5 `Water Available:`, 6
  `Ground Fertility:` (other: no label); value `{1,num}` of int(yield × 100) (slot 92, no `%`), coloured r = clamp(0.8 − y), g = clamp(0.8 y), b 0.1; tooltip "The
  richness of natural {1} present in this location. Anything over 50% is considered good.", {1} the resource's name (`FUN_140a0c300`: Iron, Stone, Copper, Carbon,
  Water, Fertility) lower-cased. No owner test: rocks, NPC and player mines, farms and wells. Meitou: the player's machines and rocks (before mining too,
  `WorldSnapshot.InspectedRichness`); a mine without a resource shows no line; town buildings are not covered.
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

`Kenshi_GenericBuildingWindow` (**Verified**, layout read): Window `Kenshi_WindowCX` captioned "BUILDING NAME", layer Window; `CraftingPanel`
(`CraftingName`, `QueueButton` "QUEUE..."), `TitlesPanel` (INPUTS / OUTPUT), `Input1Panel` / `Input2Panel` (`InputNText`, `InputNStatusText`,
slot `inN`, `InputNProgressBar`), output (`out` slot, `OutputProgressBar`, `OutputText`, `OutputDataPanel`). The `inN` / `out` inventory
sections are the recipe item's `inventory footprint width` × `inventory footprint height` cells (**Verified**, `FUN_14029e7b0` code read). The grid
sits centred under its label and progress bar (**Observed**, owner's screenshot of an electrical workbench, 2026-10-24); the rule behind it and whether the bar
moves for a tall grid are **Unknown**. Meitou keeps the layout's top, centres the grid on the bar (a grid wider than the bar keeps the slot's left edge)
and pushes the bar under an overlapping grid.

Opening (**Observed**, owner's screenshots of the original, 2026-10-24): selecting a machine, bench or storage shows its window alone, with no inventory.
A right click that sends a character to a storage opens the character's inventory with the storage window beside it; once the inventory closes, the
storage window is back where it stood (the screen's lower left). Meitou: the same for a window already shown alone, and a window the player closed
opens again on such an order.

### Character stats runtime layout

**Observed** in the original Metaru screenshot supplied on 2026-10-15 (1920×1080):
the window is about 980×650, titled with the character name, with no derived-statistics
column. Attributes use grey painted uppercase labels, ordered Strength, Toughness,
Dexterity, Perception. Four skill columns use dark standard text and right-aligned
values, with larger red-brown category headings:

| Column | Groups and row order |
| --- | --- |
| 1 | Weapons: Katanas, Sabres, Hackers, Heavy weapons, Blunt, Polearms |
| 2 | Combat: Melee attack, Melee defence, Dodge, Martial arts; Ranged: Turrets, Crossbows, Precision shooting |
| 3 | Thievery: Stealth, Lockpicking, Thievery, Assassination; Athletics: Athletics, Swimming |
| 4 | Sciences: Field medic, Engineer, Robotics, Science; Trades: Weapon smith, Armour smith, Crossbow smith, Labouring, Farming, Cooking |

The skills parchment spans the remaining width; the description parchment spans
the full bottom width. Exact runtime resizing and race/mod variants are **Unknown**.

**Observed** in the supplied Metaru references: the bottom left has the stat name,
Stats Affected and Ways To Train headings with bullet rows. The right starts with
a thin Next Level bar and percentage, followed by Description and wrapped prose.
Help text is available from the installed [gettext catalog](../formats/gettext.md).
Medical body-part labels are larger than the stats body text. Meitou uses 16 em
pixels at 1080 lines for medical labels, arrows and values, centred over the full
bar height; sizing text to the inset datapanel shrinks it. Exact original font
hinting remains **Unknown**.

### Map

MapScreen (ctor `140490210`) in the Overview's MAP tab: `MapImage` (`GUI_Map.dds`) on a 4096² `MapScrollView` canvas. **Verified** by reading the
decompile unless marked; Meitou's adapter is `PlayerInterface.Map.cs`.

- **Projection:** the image spans world X and Z −147456…147456, world (0, 0) at its centre, +X right, **+Z down** (`14048cf70`; also checked by drawing
  the placed towns on `GUI_Map.dds`: Shark lands in the swamp, Stoat in the Great Desert, Mongrel on the Fog Islands).
- **Mouse:** left or middle press pans with the `hand` cursor, no threshold; release ends it. Right press does nothing; right **release** inside the view
  orders the selection to move (task 0x1d, the shared order dispatcher `1407f9e20`) to the point under the cursor, clamped to the image, Y = max(terrain,
  100). Shift is read but not used (no queueing); a town marker takes clicks like the map under it. Extra selected characters get a random offset
  rand·√(60·count)+5 around the point (Meitou leaves spreading to its move order).
- **Zoom:** scale = on-screen pixels per canvas pixel, from fit-the-whole-map (min(view w, h)/4096, the start) to 1.5 × UI scale. Buttons add or
  subtract the least scale about the centre; the wheel adds 0.000625 per wheel unit (0.075 a notch) about the cursor. Zoom level 0 below 0.4 × UI
  scale, 1 up to 0.75 ×, else 2. Markers keep their pixel size. Opening the map and the centre button centre on the camera focus.
- **Colours** (`kenshi_colours.xml`): MarkerAlly `#5cb473`, Neutral `#dfdfdf`, Enemy `#e65139`, Player `#8dcdff`, Movement `#79b44b`; PlayerSelected
  (0.652, 0.903, 1.0) is built in. Player faction: Player (Selected when selected); else Ally, Enemy (relation below −30 or a faction flag), Neutral (`14048f320`).
- **Camera marker:** `Camera` group, colour Player, every frame at the camera focus, turned by the angle between the level view direction and −Z
  (negative towards −X). Size 32 × 32 from the layout (**Observed**).
- **Characters:** `Character` group × UI scale, centred, every 1 s; the player's characters (plus ones campaigns and AI packages track, not modelled);
  hidden at zoom level 0. Downed or dead get nothing special (**Observed**).
- **Movement marker:** only after a map right click: at the order point, bottom-centre anchored, colour Movement, for 3 s of real time; opening the map
  hides it.
- **Towns** (`140491700`, `14048e230`): every placed TOWN except type 8 (NestMarker) and 10, only when its state is `discovered`. Group by type: Outpost
  (also Military, Prison), Town, Village, Ruin (Ruins), Workcamp (SlaveCamp), SmallPlace (POI). Levels shown: Town and recently discovered ones always,
  types 1 and 3–7 from level 1, POI at level 2. Size: group size × UI scale × 0.5 at level 0; Town × 0.75 / 1.0 at levels 1 / 2, others × 0.85.
  Explored: faction colour and the name; unexplored: Neutral and `unexplored name` (the name when empty). Label: `Kenshi_StandardFont_Medium`, black
  shadow, the marker's colour, centred round(3.2 × UI scale) below the icon, none at level 0. A `recently discovered` one gets a `NewDiscovered` child
  icon; closing the Overview on the map clears the flag (`14049c210`). Player outposts (`World.Buildings.Towns`) are not on Meitou's map yet.
- **Camps and nests:** a TOWN of type 0 is a `Nest` object (factory `140581770`), made by the area spawner (`1408f7310`) from the BIOME_GROUP `nests`
  (Meitou: the wild nests, [factions-squads-towns.md](factions-squads-towns.md) 6.3). Group "Nest" when the TOWN's faction is `not real`, else "Camp"
  (`14004bc6d`). Shown only when discovered and a player character is within √10616832 ≈ 3258; always level 2 (recently discovered does not lower it),
  × 0.85 (× 0.5 at level 0), no label, faction colour when explored. The marker goes when the nest does (Meitou keeps it: its nests are not destroyed).
  Saved as GAMESTATE_TOWN with `is nest` (`1403709d0`); the original's town loaders skip those. Meitou writes its own with `instance`
  `meitou-nest:<wild site>:<TOWN id>` and reads them back; the place is filled in when the nest next loads.
- **Discovery** (`14092f6b0`): a character within 400 units (XZ) sets `discovered` and `explored` (that it is a player character's is **Observed**);
  else, not yet discovered, within 8500 (17000 for type Town, halved for Nest and Ruins) sets `discovered` with the message "Discovered {1}", none for
  nests. The new game's messages show after the character editor's CONFIRM (**Observed**, vanilla play); Meitou checks nothing while `PlayerState.InCreation`.
  Meitou skips types 8 and 10 (whether the original does is **Unknown**) and checks with the population's zone refresh (every 15 ticks), a camp only after its nest has loaded; flags per placed town
  (`PlayerState.Towns`) and per camp (`PlayerState.Camps`), read and written as the GAMESTATE_TOWN bools.
- **Map items** (MAP_ITEM: `towns` with v0 = how many of that TOWN, 0 all, v1 = weight; `unlock count`, 0 all): only by right-clicking the item in a
  player character's inventory (`140714a20` → `14075f550`). Candidates per link: the placed towns of that record (not types 0, 8, 10) not yet
  discovered, v0 of them at random when 0 < v0 < their number; then `unlock count` weighted draws by v1 without repeats, or all when it is 0 or not
  below the candidates (`14075e980`). Each gets `discovered` and `recently discovered` (not `explored`); "{1,num} location(s) added to the map." and the
  item is used up; with none: "You already know all these locations, no new information can be learned." and it stays. Never camps or nests. Its
  tooltip (`1407b4e50`) runs the same pick: "[Right-click to learn new map locations]", "Towns:" and the names (a count for repeats), or "[Location
  already known]". The original seeds the draws from the item instance (field **Unknown**); Meitou from the world seed and the MAP_ITEM record.
- **Tooltip** (`14048fd10`, `140922e60`; [Tooltips](#tooltips), type 4): only an explored town or camp, in the Overview's `ToolTip` (`Kenshi_GenericTextBoxSkin`): the name, "Faction:"
  name, "Relations:" the player faction's relation, then population rows ("Residents max:", "Actual Num Residents:", "Roaming currentMax/desiredMax:",
  "Roaming Population:", "Num Roaming Squads:", "???" when the manager has no numbers) and, for towns, "Residents:" with a line per resident faction and
  "Hostile Occupiers:". Meitou shows the first three rows only.
- **Roads:** `1404931d0` would draw the level's road network (`4-roadlist`) as polylines (hidden roads `#a0a0a0`), but its only caller `1404939f0` is
  never referenced in the exe: the base game shows no roads (**Verified**, xrefs and a raw pointer scan). Meitou draws none.
- **Unknown:** where `NewDiscovered` sits on the marker (Meitou: the top-right corner, half size).

### Faction tab

Overview tab 1 (`FactionTab`), class `FactionsScreen` (RTTI; constructor `FUN_140493fe0`, update `FUN_140499960`). **Observed** (decompile, 2026-10-23)
unless marked; Meitou: `PlayerInterface.Faction.cs`.
- Captions: `lbFactionName` "Faction name", `lbFactionInfo` "Information". `FactionNameText` (EditBox) holds the player faction's name (PlayerInterface+8).
  Accept or losing the key focus renames (`FUN_1404923d0`): a new name that no faction has yet (`FUN_1402e7950`) is written to PlayerInterface+8 and the
  player faction's name (+0x1a8); a taken one shows the message box "Faction Name" / "This faction name already exists" and the box reverts. While the box
  has the key focus the hotkeys are off (`DAT_142133440` = 0, back to 1 on focus loss).
- `RelationsPanel` (ScrollView): one `Kenshi_FactionRelationsLine.layout` per faction (constructor `FUN_140491f60`: `FactionName` the faction's name,
  `FactionValue`, `LeftBar` Hurt skin, `RightBar` LifeBar skin), made once per faction of the player faction's relation map (FactionRelations +0x78) that
  passes `FUN_1407f4ac0` (present in a second map at FactionRelations +0x68, meaning **Unknown**), has its byte +0x1d0 clear (**Unknown**) and is not the
  record `GENERATION_TEMPLATE_FACTION` (**Verified**: the string id of a FACTION in `gamedata.base`, named "generation template"). Every 2 s of real time
  (+0x58) the values refresh; new lines are sorted by faction name (insertion sort, comparator `FUN_1409b3cd0`, case rule **Unknown**) and stacked
  with 3 × a UI scale (`DAT_1421337ec`) between them, each the canvas's width (the view less its vertical bar). A line (`FUN_140499630`): the number
  int(relation) in BadBright below -30, `#a8b774` above 60, else Main; below 0 the left bar's width is its layout width × -relation / 100, kept
  right-aligned at the middle, above 0 the right bar's × relation / 100 from the middle (the other bar zero). The wheel over a line scrolls the panel.
- `FactionInfoPanel` holds a DatapanelGUI ("mfactioninfo", `FUN_140494720`): `---Active Contracts---` (#444444) and the contract rows
  (`FUN_1404927f0`: a "Renew" / "Manage Contract" button per contract and a `-----------` rule), then `---Upcoming Events---` (#444444) and the
  rows of each entry of a list at `DAT_1421345b8` (`FUN_1407f2ef0`; what they are is **Unknown**).
- Meitou: the name box takes typing; accepting it (Enter) or losing the key focus sends `RenameFactionCommand`, which takes a name no other faction of the data has and posts "This faction name already exists" otherwise (the original's message box and the empty-name rule are not built / **Unknown**; the 64-character cap is an engine choice). The name is `PlayerState.FactionName` (null = the data's "Nameless"), saved as the CAMERA `pfaction name`, the player platoons' faction name and read back on load when it differs from the data's; the GAMESTATE_FACTION record's own name is not rewritten. The lines for every faction but the player's, the `not real` ones and the template (engine
  choice for the unknown filters), sorted ignoring case, rebuilt each frame, line height from the layout's 0.0222 of the screen; the two headings
  with nothing under them (no contracts or events are modelled).

### Squads tab

Overview tab 4 (`SquadsTab`), class `SquadManagementScreen` (RTTI; constructor `FUN_14049b7c0`, box `FUN_1404a15c0`). **Observed** (decompile, 2026-10-19)
unless marked; Meitou: `PlayerInterface.Squads.cs`, commands in `SquadManagement`.
- `SquadsPanel` lists one `Kenshi_SquadBox.layout` per player squad, in the faction's platoon order (+0x210/+0x218; a platoon's place is its +0x1f2, saved
  as `squad index`), each the list's width less a scroll bar (`Kenshi_ScrollBarV` width, else 17 × UI scale) and 0.28 of its height. Box parts:
  `SquadNameText` (editable, at most 32 characters; the text goes into the platoon's record name), `SquadSizeText` "{1,num}/{2,num}" against
  `max squad size` (another colour when full, **Unknown** which; Meitou: Bad), `SquadRemoveButton`, `PortraitsPanel` with a
  `Kenshi_SquadsPanel_PortraitCharacter` cell per member (Meitou shrinks cells to fit one row). `FactionSizeText`: Meitou shows the faction's count
  against `max faction size` (what the original writes is **Unknown**: the constructor sets it empty).
- The constructors pass the translated captions through a locale transform taken to be upper-casing (the box's "Delete" matches its layout's "DELETE"; not seen in a run): `NewSquadButton` "ADD SQUAD", `lbSquadsDismiss` "DISMISS" (the layout's
  "NEW SQUAD" / "DRAG HERE TO DISMISS" are replaced).
- ADD SQUAD (`FUN_140491b50`): below `max squads` boxes a new empty platoon of "blank squad" at the player interface's node position (`FUN_1407f4910`; Meitou:
  the camera's focus), else "Maximum number of squads reached.". DELETE (`FUN_140491d20`): "The squad needs to be empty before it can be removed.";
  the last squad: "You must have at least one squad.".
- Portrait drop (`FUN_14049b050`, accept test `FUN_14048caf0`; Shift = `DAT_142133449`): into another squad only below `max squad size` unless Shift is held;
  on a member with Shift the two swap places (`FUN_140792e60` within a squad); otherwise the character goes in at that place, at the end on empty space
  (`FUN_140796620`). The squad's first member is its leader (role 2; whoever led before gets role 0). A squad box dropped in the list takes that place
  (`FUN_140493cb0` → `FUN_1407f3440`, which renumbers +0x1f2).
- Dropped on `DismissCharPanel`: with one character in the faction "Cannot dismiss the only remaining character in the faction." (title "Dismiss
  character"), else "Dismiss character '{1}'?". Yes (`FUN_14048fa30`): deselected, faction `1233-gamedata.base` (Tech Hunters; else `nofac`), a new
  persistent platoon of `5006-squad.mod` ("Wandering Squad") whose AI packages it runs. Meitou's dismissed squad is not written to saves yet (it has no town).
- Meitou: a portrait clicked without dragging selects the character (engine choice); a box is dragged by its frame.

## 6. Inventory, trade, dialogue, build

- **Holding an item** (**Observed**, Ghidra decompile of `kenshi_x64.exe`; one item on the mouse at a time, `MouseInventory` `FUN_14070e640`):
  - Input: the item sections hook only pressed (`FUN_140716800`) and released (`FUN_1407169a0`); no double click, no inventory keys, no quantity dialog. Shift = `DAT_142133449`,
    Ctrl = `DAT_142133448` (key flags `FUN_14082b010`).
  - Left press with nothing held (`FUN_1407139a0`): the section must be enabled; for a building's window the [trading range](economy.md#trading-range) ("Out of trading range.",
    `gui_notrade`). Cell = (pointer - grid origin) / cell, clamped. Locked (shackles) items do not lift. In a trade an item flagged equipped on a non-player owner: "That's not for sale.".
    A single unit, or Shift or Ctrl held: the whole stack lifts; otherwise one unit is split off and the rest stays. `Item_Lift`; the grab offset is kept, clamped so the pointer stays at
    least half a cell inside the footprint (`FUN_140713560`).
  - Release, or a press while holding: the target is the window under the pointer, else under the item's centre. Over a window's section: placed; over a window elsewhere: back to its
    origin; over no window: given or dropped (below). After a swap or a partial place the other item or the rest stays on the mouse for the next press.
  - Place (`FUN_140715560`): the cell is round((item top-left - grid origin) / cell), clamped, then slid left, up and diagonally toward 0,0 until the footprint is inside
    (`FUN_14074bec0`); only a footprint larger than the section fails. Acceptance (`FUN_14074bc30`): a limited section (allowed-item list) takes only its records, a general one anything,
    otherwise the item's slot type must be the section's ("Invalid container type."); armour-only sections take ARMOUR; non-general sections also check the wearer's race ("Can't wear that
    item."); a section named `out` refuses everything; a non-empty CONTAINER into a CONTAINER's inventory: "Container cannot be placed here unless it is empty.". Under the footprint: two or
    more items refuse it; none, but the section's item-count cap reached, refuses it; one item locked (shackles) on either side: "Item is locked in place."; one of the same kind that stacks
    (same type, material and quality): merged up to the stack limit (partial charges combined), the stack at the drop cell, the rest on the mouse; any other: swapped, the old item on the mouse.
  - Over no window (`FUN_1407136a0`): a character picked under the pointer other than the source owner is given the item (auto-placed into its inventory, the rest into its bag; no trade;
    range checked; "No room for that item." or the container message; from a non-player owner "Got caught stealing." may follow); otherwise it is dropped on the ground only from the player's
    own windows or a window of a player-faction owner, else it goes back.
  - Back to origin (`FUN_14070d860`): its old cell if still free, else auto-added to that inventory, else that inventory is arranged and it is tried again, else it is dropped on the ground.
  - Sounds (**Verified**, decompile; all on the GUI object with `Inventory_Items` set from the item, `FUN_14070d230` / `FUN_14075e360`): a lift `Item_Lift` (`FUN_1407139a0`);
    a placement `Item_Drop` (`FUN_140715560`; equipping included; a swap posts no lift); `Sell_Item` instead for any move between a trade pair, buying too; a right-click move `Item_Lift`
    (`FUN_140713d20`; with Shift once), `Sell_Item` in a trade; a ground drop `Character_Drop_Ground`; nothing for giving to a character, a return to the origin, refusals, money becoming
    cats or hovering. `gui_notrade` / `gui_noequip` are posted (out of range, can't afford) but are in no bank. A window made or removed posts `Inventory_Open` / `Inventory_Close`
    (`FUN_1406e6820`, `FUN_1406e53d0`); OPEN BAG posts once more (`FUN_14070e120`). `Inventory_Items`: WEAPON and CROSSBOW by `material` (Weapon_Metal, Weapon_Wood), ARMOUR by
    `material type`, ITEM / CONTAINER / NEST_ITEM / MAP_ITEM by `inventory sound` (0-19), LIMB_REPLACEMENT Robotic_Component.
  - Stack limits (`FUN_14075e090`, `FUN_1407466b0`): 1 for WEAPON, ARMOUR, CONTAINER, MAP_ITEM, CROSSBOW, LIMB_REPLACEMENT; else the record's `stackable` through the section: a limited
    section starts from 1; then max(value, section minimum) x section multiplier.
  - Auto-add (`FUN_14074c8b0`): tops up stacks, then the first free spot row by row from the top, left to right, in limit-sized stacks. ARRANGE (`FUN_14070f740`, `General_Click`): every
    section (unless flagged) is emptied and re-added by auto-add (partial stacks merge) in a sorted order, pass 1 by the longest footprint side, pass 2 by area, pass 3 by width, all
    descending; the first pass where everything fits wins, else the old positions are restored.
  - Trade: a drag buys or sells in place, asking the fence question as the right click does; the quantity is capped by what the payer can afford (the rest stays on the mouse);
    "You can't afford that." / "The shopkeeper can't afford that." with `gui_notrade` restore the stack; a caught stolen sale: "Got caught selling stolen item." / "Attempted smuggling!".
    Taking from a non-player CAMPING building (`building category`) is not stealing.
  - Drawing: the held item like an inventory cell at pointer + grab offset, its source cell empty. One `WhiteSkin` rectangle on layer `Info`, alpha 0.5, the footprint's size at the snapped
    cell, shown only when the section takes the item and the footprint fits (what is under it is ignored: swaps are legal); no red or green.
  - Meitou (`PlayerInterface.Inventory.cs`, `Items/HeldItems.cs`): the held item is world state (`PlayerState.Held`, commands `LiftItem`, `PlaceHeld`, `GiveHeld`, `DropHeld`,
    `ReturnHeld`; [simulation](../simulation.md#items-and-eating)). Engine choices: the release places only after the pointer moved at least 5 px since the press (a click keeps the item for the
    next press); right click or Escape puts it back; a stock item goes only into the customer's windows and the customer's own items onto the stock; give range 1000 (60 for an item out of
    a non-player window) from the window's owner. Not built: the race check, section item-count caps, "That's not for sale." (no trader character window), "Got caught stealing." and
    "Attempted smuggling!" (looting's witness test stands in), CAMPING buildings.
- **Observed** in the original Metaru screenshot supplied on 2026-10-15: inventory
  icons have translucent black rectangular backings covering their cell footprints;
  empty clothing cells are pale. Emptiness alone does not establish the red/disabled
  slot state seen in the Wolf reference below; its runtime condition is **Unknown**.
- **Observed** in the vanilla screenshot supplied on 2026-10-15 (Wolf, Thieves Backpack, Armour King): the character title is the character's name; the bag title is its item name;
  the shop title is the trader's name. The character pockets contain 8 columns by 10 rows of square cells. The bag is left of the character, stock right of it, with an independent
  vertical scrollbar. Equipment icons occupy their cell footprints; empty clothing slots are red, other empty cells pale. Stack counts are plain numbers in the lower-right corner;
  stock prices are not printed over every icon. A white line over a black full-width track (about 3 px at 1080p) along the bottom edge of an item whose maximum charges are above 1 (food,
  medkits, splint kits, robot repair; stacks too, for their partly used unit); it shrinks: its width is charges / maximum (**Observed**, decompile; `ItemInstance.ChargeFraction`). The 2026-10-18
  vanilla trader screenshot (Swamper Barman) shows it full under food stacks. The stack count shows only from 2. The bag has a 10-by-14 grid with a fitted frame and an ARRANGE footer without the generic layout's editor capacity/type fields.
  **Observed** (vanilla screenshot supplied 2026-10-21, METARU, Medium Backpack): the bag window sits left of the character window, same top, with no caption buttons; it is married to
  the character window and closed only by OPEN BAG again. Meitou: `MyGuiScreenHost.Follow("bag", "inventory")` (a drag on either moves both; Escape or closing the inventory closes both). Engine choice: OPEN BAG's state is kept per character
  (`PlayerInterface.bagOpen`, not saved), so the bag reopens with that character's inventory or when they are selected again.
- <a id="limbs-window"></a>**Limbs window** (`LimbsInventoryLayout` `FUN_1401573d0`, `Kenshi_LimbReplacementWindow`; **Verified** in the decompile unless marked; code `PlayerInterface.Inventory.cs`,
  `ArrangeLimbSlots`, `PlaceLimbsWindow`):
  - LIMBS is enabled once the character has a limb holder (made the first time a limb changes, `FUN_140645690`); it toggles the window (`FUN_14070d1e0`), with `Inventory_Open` / `_Close`.
    Meitou: enabled while a limb is not the original (no limb returns to it).
  - Placed (`FUN_1406e4960`) with its right edge on the left edge of the inventory (its bag window when open), top at 5% of the smaller screen side; off the screen it goes to x 0 and
    pushes the inventory and bag right. Opened without a parent: it does not move with the inventory (**Observed**). Closing the inventory closes it (`FUN_1406e53d0`); Meitou also
    closes it when the inventory shows another character (the original's inventory is per character).
  - Slots: sections `left_arm_replace`, `right_arm_replace`, `left_leg_replace`, `right_leg_replace` (`FUN_1400d04d0`), arms 2 x 6 and legs 2 x 7 cells (`FUN_1401534f0`). A slot is
    enabled only for a stump or a replaced limb, else drawn with the skin's `disabled` tile and taking no drop (MyGUI gives it no focus, **Observed**). A fitted prosthetic shows its
    icon; a stump's race item never sits in the slot. Labels `-ARMS-` / `-LEGS-`; `figure.png` never changes. The slots are built on open (a limb lost meanwhile shows on reopening,
    **Observed**; Meitou refreshes live).
  The pin button (`Kenshi_PinButtonSkin` in the `Kenshi_WindowCXP` caption, drawn "O"; the skin's `*_checked` states while pinned) keeps the inventory window on the character shown
  when it was pressed, and another squad member's inventory opens in a second window, so several show at once (**Observed** by the owner in the original). Engine choices: selecting
  someone else after pinning opens their inventory left of the pinned one; I on a pinned character raises their window; unpinning gives the window back to the selection (closes it when
  the inventory is already open); a pinned window has no bag or limbs window. A pinned window can still be dragged (**Observed** by the owner). The snapshot carries every pinned character's grid (`ShowInventoryCommand`).
  Esc while trading closes the trader and the character's windows in one press, whichever was clicked last (engine choice, as for loot).
  The character parchment has separate label/value rows: Total Weight, a burden description, Disguise, Slave of, Looks like slave. **Unknown**: exact runtime sizing/rounding,
  burden description thresholds, and race/mod overrides of the pocket dimensions. Meitou uses 8 by 10 for new pockets and retains taller loaded inventories when saved items require it.
- **Trader window** (code `PlayerInterface.Trade.cs`):
  - Layout: `Kenshi_InventoryTraderWindow` (**Verified**: `FUN_140155ee0`, class `TraderInventoryLayout`, names the file; created by `FUN_1406e6af0` for a trading character or building).
  - Size (**Verified**, `FUN_140150570`): the grid is the trader's `backpack_content` section at the `Kenshi_InventorySlotSkin` size (25 x 25 px, read once from the skin). The grid is
    first sized columns x columns; the root window and `scrollview_backpack_content` grow by that size less the layout's grid size; then the grid takes its real rows, becomes the scroll
    view's canvas and is reparented into its client at 0,0. So the visible stock is square (as tall as wide) and the window scales with the column count. ARRANGE gets the caption "Arrange"
    and the inventory's arrange handler; `datapanel` holds the money.
  - The scroll view is the `Kenshi_ScrollView` template (**Verified**, `kenshi_templates.xml`): a `Kenshi_GenericTextBoxSkin` frame, client 10 px in from left and top, `Kenshi_ScrollBarV`
    20 px wide 5 px off the right (arrows 12 px, track between, thumb `Kenshi_SmallBarSkin` at least `MinTrackSize` 10); the horizontal bar is off (`VisibleHScroll` false).
    Meitou draws it with the host's generic ScrollView ([widget runtime](../formats/mygui.md#widget-runtime-2026-10-20)): the stock grid is the canvas, the bar shows only when the stock overflows.
  - Placement (**Observed**, `FUN_1406e4960` decompile and the 2026-10-17 screenshot): margin = 5 % of the smaller screen side; inventory windows open at y = margin, the first at x = margin
    (a bag window first when attached), each next one where the previous ends: the character window at the top left, the trader directly right of it, same top.
  - Stock grid size (**Observed**, `FUN_140953850`, the shop wrapper's constructor): the trader's `backpack_content` is one merged section, `columns` wide and `ceil(cells / columns)` high.
    Columns start at 18. A shop run by a building takes the widest storage section among the building's containers (at least 18) and the sum of their cells; a squad trader, or a building
    shop without cells, is 18 x 18 (the squad's backpack sections are collected but add no cells). The base game's buildings all store 18 x 18 (`storage size width/height`, the editor default;
    367 records with a size, none wider; `gamedata.base`), its backpacks 8 x 8 to 20 x 20. Meitou's squad stock is 18 x 18, the width the 2026-10-18 screenshot shows (18 columns, scrolled rows); building shops are
    merged as above ([economy](economy.md#building-shops-observed-decompile)). The 28 columns the 2026-10-17 screenshot seems to show are **Unknown** (a modded building, or a cell size other than 25 px).
  - Right click (**Observed**, `FUN_140716a40` wires the section widgets, `FUN_1407169a0` the release, `FUN_140714a20` the right-button handler, `FUN_140713d20` the move): with a second inventory
    window open it moves the item to the other window.
    - *Use first* (`FUN_14070d070`): only an item whose class returns itself from item slot 94 (offset 0x2f0) has a use. The default returns nothing (**Verified**: the Item, Weapon, Armour, Gear
      and MapItem vtables all share it, so food, medkits and ordinary gear never reach it); `LockedArmour` (shackles) does, and its right click opens the context menu on it (`FUN_1407a6bc0`, verbs such as
      Unlock Shackles, Use Tools, Use Strength: [locks](locks.md)). A right click on food does not eat it. Meitou offers the shackle verbs on the character's context menu instead.
    - *Fence prompt* (`FUN_140715290`): a stolen item (it has an owner of kind 1 or 0x22 other than the seller's faction) that a trader who is not a knowing fence would buy is held back
      while a modal asks "You have a {1}% chance of getting caught selling this stolen item.\nFence it anyway?" (title "Fencing Item"), {1} = 100 - floor(100 x chance + 0.5) with the chance of
      `FUN_140793490`; it is asked only when that chance is below 1. While it is open further clicks are swallowed; Yes repeats the click (or the drop), anything else drops it. The drag-and-drop sale
      asks the same.
    - *The move*: one unit (a stack of 2 or more is split, the new stack's quantity 1) goes to the first section of the target inventory that accepts the item's slot type and has room, which for
      worn gear is its equipment slot, and only if none takes it any general grid section (type 7); then `RClickAutoTrade` (`Sell_Item` for a sale) is posted. The order of two matching
      sections (weapon slots) is **Unknown**. Shift (**Verified**: scancodes 0x2a / 0x36 set `DAT_142133449`) repeats the move until one fails, so the whole stack. Failures use the codes of the
      messages below (no room, can't afford). Into another owner's character (`FUN_14070dcd0`): its open bag window first, then its character window when the bag has no room; sections of the
      item's slot type first (an equipped item out of a non-player window loses its owner tag), then general sections other than `out`.
    - *Own windows only* (owner of the player's faction): a blueprint (RESEARCH-type item) is learned and used up ("Research already known" / "Failed to learn from blueprint: Technology
      requirements not met"); a MAP_ITEM reveals its places ("{1,num} location(s) added to the map." and used up, else "You already know all these locations, no new information can be
      learned."); money (item function 14) gives "{1,num} cats added." and is removed (money moved across a player / non-player window boundary converts the same way). Otherwise inside its own
      inventory: a grid item goes to its matching slot (equipped), a worn one to the grid; failing that the own limbs window, then the own bag window.
    - Meitou: `PlayerInterface.InventoryRightClick` buys or sells one unit (Shift all); a bought weapon goes into a free `back` then `hip` cell and armour into its empty slot
      (`InventoryCommands.TryEquip`), the rest of the stack and everything else into the first free cell; a stolen item a non-fence shop may catch asks the prompt above (`TradeItemView.FenceOdds`).
      Window to window (loot, container, storage) the units go by `InventoryCommands.MoveInto` (the open bag window first, then slots, then the main grid). Own windows only: `ReadMapItem`, else
      `QuickMoveItem` (money, equip / take off, prosthetic onto its stump, main grid to the open bag). Not built: blueprints (which RESEARCH a BLUEPRINT record names is **Unknown**,
      [economy](economy.md)), the owner tag.
  - Reference screenshot (**Observed**, 2026-10-17, from a video): `Money:` label left and `c.442,269` right in the money box, stock icons at their footprints with plain counts bottom
    right, a vertical scrollbar, no other panels; the HUD stays. The character panel reads `Disguise: None`. Its text boxes and empty cells are dark, where the install's skins draw parchment; the cause (a UI mod or setting) is **Unknown**.
- **Character trading window** (`CharacterTradingWindow` `FUN_14072a880`, rows `FUN_14072a070`; DA 41 / 42 / 43 / 48, [economy 12](economy.md#12-slaves-prisoners-animals); code
  `PlayerInterface.CharacterTrade.cs`):
  - Layouts (**Verified**, install's `data/gui/layout`): `Kenshi_CharacterSelectionWindow`: full-screen `Root` (PanelEmpty, no mouse) holding `TradePanel` (Kenshi_FloatingPanelSkin,
    caption "Character Selection", 0.302 0.120 0.414 x 0.567 of the screen) with `CaptionTextBox` (title), the `CharacterSelectionItemBox` ScrollView (Kenshi_ScrollViewEmpty),
    `ConfirmButton` ("CONFIRM") and `CancelButton` ("CANCEL") at the bottom right, `lbTotalMoney` "Total:" + `MoneyAmountTextBox`, `SelectedTotalTextBox` ("0 / 0", bottom left),
    `lbBuyersMoney` "Buyer's money:" + `TotalMoneyBuyer`. `Kenshi_CharacterSelectionBox` (a row, Kenshi_FrameSkin, 0.3776 x 0.1806 of the screen): `Portrait` (ImageBox) with
    `NameTextBox` nested at its bottom, `PriceTextBox` above it, `StatsTextBox` holding `DataPanel1` and `DataPanel2` side by side, `CheckBox` (Kenshi_TickBoxSkin) at the right.
  - Runtime (**Observed**, decompile): the title and the confirm caption come from the handler ("Selling Slaves" / "Sell", "Buying Slaves" / "Buy"), cancel reads "Cancel"; the total
    is `c.{total}`, the count `{selected}/{rows}`, the buyer's money `c.{money}`. Refresh (`FUN_1407281c0`): confirm is enabled only with a row ticked and the total within the
    buyer's money; an unaffordable total gets a colour prefix (its colour **Unknown**; Meitou: Bad). No confirmation box: confirm runs the handler and closes the window, cancel closes it.
  - Meitou: the rows stack down the scroll view at the row layout's height and the view's width (engine choice; the original's row sizing is **Unknown**); the panels are label/value
    lines; the portraits are drawn into atlas cells after the squad bar's (and the conversation's two while no conversation is open); rows past the last cell show none.
- **Item tooltip** ([Tooltips](#tooltips); **Observed**, decompile): name, then `Value` (`c.{1,num}` with ` (+N%)`/` (-N%)` when the price factor is not 1) and `Sell value` (`FUN_14079e7a0`; one unit's price, slot 81 with `sell` 0 and 1, **Verified** in the decompile, not the stack's); weight `{1,num} kg`,
  quality, charges, damage, nutrition and kind lines (`FUN_1407ae130`); armour classes and resistances (`FUN_1407a1650`); `Stolen` / `Stolen from:` (`FUN_1407a0ea0`); `Buyback` (`FUN_1407a13a0`).
  Meitou: the rows listed under Tooltips ("Meitou item rows"); armour also gets (`ArmourTooltip`): the grade name in its `ItemGrade` colour (a crossbow only that, from its `level`),
  `[Heavy Armour Class]` / `[Metal Plate]`, Blunt resistance, Cut resistance, Cut resistance efficiency (1 - cut into stun), Harpoon resistance (flat pierce, `pts`), `-Weight` (two decimals), a
  `[Coverage]` list (Chest, Stomach, Right Arm, Left Arm; percentages) and a `[Skills]` list of the effects that are not neutral: Crossbows and Precision shooting (both the record's `ranged skill mult`), Dodge
  skill, Attack damage (`damage output mult`), Assassination skill, Melee attack bonus (`combat attk bonus`), Martial arts bonus (`unarmed bonus`). **Observed** on a screenshot of the Samurai Armour at all six
  grades (5, 20, 40, 60, 80, 95): the resistances equal `ArmourPiece.Create` to the printed digit at every grade. **Unknown**, so not shown: the Stealth effect and Combat speed effect lines (they grow with the
  grade: stealth 0.00, 0.02, 0.06, 0.12, 0.21, 0.29x and combat speed 0.85, 0.86, 0.88, 0.90, 0.92, 0.94x on that armour, whose records say stealth mult 1 and combat speed mult 0.94), the labels and rules
  for dexterity, athletics, defence, perception and ranged bonuses, the cut-offs between grades for other qualities, and weapons, food and tools (no stat lines yet).
- **Fencing prompt** (**Observed**, `FUN_140712920`), shown for a stolen item over the trader: stolen from the trader's own faction, `Fencing chance:` and `N%` (`FUN_140793490`) in Bad;
  stolen from others and the trader buys stolen goods, `Stolen goods?` / `No problem!` in Good; otherwise nothing. Stealing from a stock shows `Stealing chance:`; illegal goods
  `Illegal goods?` / `ILLEGAL GOODS!` (smuggling, not built).
- **Burden row** under Total Weight (**Verified**, `FUN_1405d4260`): by the carry speed factor, >= 0.95 `-Weightless`, >= 0.75 `-Lightweight`, >= 0.5 `-Moderate` (Main), >= 0.1 `-Heavy`,
  else `-Overloaded` (both Bad). The same function gives a trading character's panel its name, `Money:`, `Merchant Info:`, `Prices:` +/-N % and `Buys Stolen Goods` lines.
- Colour tags in code (**Verified**, static initialisers `FUN_14158a...`): #afa68b, #140806, #492620, #59231a, #bf4834, #a8b774, #444444, #777777, #14ffdc. The strings `DAT_141f49248` + 0x28 k (k = 0..8; initialisers `FUN_14158a2a0` + 0x40 k, **Verified** 2026-10-23) are #afa68b, #140806, #492620, #59231a, #bf4834, #a8b774, #a8b774 again, #444444, #777777.
- **Unknown**: when an equipment slot is disabled (its red `disabled` state; the slot's enabled flag is the section's, `FUN_14014e3d0`; both trade screenshots show red empty clothing
  slots), the trader window's panel lines beyond Money.
- Item placement failure messages (FUN_14070f7e0; the checks behind them are in [economy.md](economy.md#shops-and-transactions)): Out of trading range; No room for that item; You can't
  afford that; The shopkeeper can't afford that; That's not for sale; Got caught stealing; Smugglers only buy
  illegal goods. Stealing and fencing chance text: FUN_140712920. Item icon: FUN_140711200.
- Item icons (inventory and trader cells; code `ItemIcons`, `ItemIconStyle`, `ItemIconCache`, `PlayerInterface.AddItemCell`, `CharacterRenderer.Icons.cs`; the game's code is FUN_140711200
  building the cell and FUN_140847bf0 choosing the picture):
  - **Which kind** (**Verified**: FUN_140847bf0 decompiled, and against the saved pictures below). The game draws an item's icon from its mesh when the record is a WEAPON or its
    `auto icon image` is true (fcs.def default: true for CROSSBOW, ITEM, NEST_ITEM, WEAPON; false for ARMOUR, CONTAINER, LIMB_REPLACEMENT, MAP_ITEM; the base game's records all store it).
    Any other record shows the PNG its `icon` field names, else `default_icon.png` (`data/items/icons`; the game logs "[Icon] Missing image").
  - **Static files** (**Verified**, base game): 318 records (ITEM 268, ARMOUR 31, MAP_ITEM 17, CROSSBOW 2) name a PNG under `data/items/` (`.\data\items\icons\Fuel.png`, a few in
    `armour\pants\`); all 318 exist (**Observed**). The files are sized to the footprint at 25 px a cell (75x75 for 3x3, 150x25 for 6x1; **Observed**).
  - **The game's saved pictures**: it looks for the texture name in its `Icons` resource group first and draws only when it is not there, saving `<install>/data/icons/<name>`.
    The name is the item's string id, `.`, the instance's material string id and, only when the item has a colour record, its `dont colorise` is false and the material has a non-empty `color map`,
    `.` and the colour's string id (FUN_140845e80 and FUN_140847bf0, **Verified**). The folder is made on the player's machine, so it holds only items they have seen (**Observed**: 842 files,
    all of that form; 116 with a colour; also mod items; `data/icons/buildings/` holds BUILDING pictures, see "Building icons"). Every picture of a base-game record is of a drawn kind
    (test `Every_picture_the_game_saved_of_a_base_game_item_is_of_a_kind_it_draws`) except limbs.mod's eight severed limbs (ITEM with a static icon; why they are drawn is **Unknown**). The colour is
    mostly not the record's own `color` reference (15 of 116 are), so it is the instance's colour, which `ItemInstance` does not carry.
  - **How it is drawn** (**Verified** against FUN_1408473d0, FUN_140846060, FUN_140845f30 and FUN_140840380): a picture of 50 x 50 pixels a footprint cell, each side rounded up to a power of two (saved
    files: 5x4 256x256, 7x1 512x64, 8x2 512x128, 2x6 128x512, 3x3 256x256); a perspective camera, vertical field of view 45 degrees, aspect the footprint's, at (0, 0, 5 x `icon zoom`) looking at the origin;
    the item's node at (`icon offset H`, `icon offset V`, 0) turned by pitch (`icon offset pitch`), then yaw (`icon offset yaw` - 90), then roll (`icon offset roll`) about its own axes. A WEAPON uses the
    fixed pitch -90, yaw -90, roll 90, its `bare sword` mesh (the others `mesh`) and a node scale of (`scale thickness`, `scale length`, `scale width`) times `overall scale`, the item's times the material's.
    fcs.def defaults: zoom 2.7, V -8, yaw 90, the rest 0; WEAPON and CROSSBOW zoom 1.0 and V 0; WEAPON H -4. The material is a clone of `RTTIcons_Coloured` (mesh with vertex colours), `RTTIcons_DXT5N`
    (one item kind, 0x11) or `RTTIcons_Base`, double sided for `material type` 2, textured from the instance's material record. Lighting (rtticons.hlsl, lightingFunctions.hlsl): one white punctual
    light along the view axis, no ambient: colour = albedo x pi x N.L x 0.6 + GGX specular (roughness 1 - 0.99 x gloss, F0 0.4), stored linearly in an 8-bit target; the background is transparent.
    Gloss is the material's `specular mult` (default 1) x the diffuse alpha. The REFLECTION term is on: the albedo is lerped towards the cube's colour by gloss^2. The cube is the
    `reflectionMap` texture alias (default `cloudy_noon.jpg`, missing from the install); what the game binds there is **Unknown**. Not black: a black cube leaves every glossy
    saved picture 3-15 % dark (Tin Can, Drifter's armour 2156, 96014-rebirth.mod), while a constant grey of 0.5 fits them within 3 % and the gloss-0 materials (686, 2191) are
    unaffected (**Observed**, mean colour in `Icons_drawn_from_the_mesh_cover_the_same_part_of_the_picture_as_the_games_saved_ones`, greys 0.4-0.7 tried). The specular view vector is -normalize of the world position, with the normal and light negated too (rtticons.hlsl): it points from the item's origin, not to the camera. A submesh 0 with
    vertex colours takes `RTTIcons_Coloured`, which multiplies the albedo by them. A missing texture map is white; the dye's alpha is the COLOR_DATA top byte (0 in every record:
    no `paint factor`); the binormal is cross(T, N) x tangent w, stored binormals ignored; the cut-out is the material's `alpha threshold` / 255 (default 128); mip bias -0.5
    (rtticons.hlsl, rtticons.material, FUN_140846060, FUN_140841a50, **Verified** in the source and decompile; fitted against 22 saved pictures with a CPU model, mean error 3.4/255).
  - **Meitou's order** (`ItemIcons.Choose`): a drawn kind takes the game's saved picture of exactly that item, material and colour (`ItemIcons.ColourOf`, below), then Meitou's own saved one
    (`%LOCALAPPDATA%\Meitou\icons`, same names, emptied when its `version` file is not `GameHost.IconVersion`; an item without a material of its own is named by its first), else is drawn by `CharacterRenderer`
    (below); the other kinds take their static file, else `default_icon.png`; with nothing (or while the picture loads), the cell shows the item's name.
  - **Meitou's drawing** (`CharacterRenderer.Icons.cs`, `ItemIconStyle`): the item's mesh through the character shader's item mode with the lighting above (a negative `FogDistance` in the view
    constants selects it), into its own UNORM texture, drawn each frame until the textures it uses are resident, then kept (rows bottom first like every target: shown with `ImageFlipV`).
    The mesh is read on a worker with its full level only (`CharacterContent.IconMeshReady`); up to 16 icons (4 supersampled) draw
    and 8 upload a frame; one that gives up waiting for its textures is drawn plainly from then on. The final drawing is read back without stalling (`ReadbackBuffer`) and written as a PNG on a worker (`CharacterRenderer.IconSaveDirectory`).
    The game writes its pictures into the install; Meitou leaves the install alone.
    The `icons` switch (Item icons): Faithful as above; Meitou (default) draws each icon 4 x 4 supersampled into a half-float picture and averages it by coverage into the icon
    (`CharacterRenderer.EnhancedIcons`, the portraits' `PreviewResolve` with no tone curve or grade), shows none of the game's saved pictures and saves its own in `icons\supersampled`.
    Meitou applies the gloss multiplier (`PartMaterial.GlossMult`, the shader material's `shirtColour.w`), the reflection as a 0.5 grey cube, the origin view vector, the vertex colours, white for a
    missing map, the zero dye alpha and the binormal (`Model.Build` `tangentSign` for icon meshes, kept apart from the characters' as `CharacterContent.IconBones`). Mean colour against saved pictures of vanilla meshes: identical on Small Backpack, Cap and Ninja Pants, within 2/255 on Heart Protector,
    Rag Loincloth and 2167, within 3 % on Tin Can, 2156 and 96014. Samurai Boots (549) is 28/255 brighter than its picture (its
    record's mesh path is missing; which mesh the game drew is **Unknown**). Not done: generated tangents, the mip bias; Plated Drifter's Leather
    Pants (`specular mult` 4) fitted its picture better without the black reflection, not yet checked against the grey cube (**Unknown**). Many saved weapon pictures in a modded install come from mods' meshes (Iron Stick:
    Warrior Moon's `Iron Stick Bare.mesh`), so they are not a reference for the vanilla mesh. An icon is dyed as worn clothing is, by the record's own `color` when the item's `dont colorise`
    is false and the material has a `color map` (`ItemIcons.ColourOf`; the instance's colour is not carried). **Observed**: the game saved Thieves Backpack (rebirth.mod, `color` "black lighter")
    undyed, dyed with that colour (dark grey) and dyed 1297; an armour whose record names a `color` was saved with no colour part.
    **Verified** by `Cached_starter_icons_use_the_original_files_without_rendering_replacements` against the configured install: the sandbox preview's Rag Loincloth
    (`2308-clothes_v1.mod`, material `2312-clothes_v1.mod`) and Iron Club (`52295-rebirth.mod`, material `914-gamedata.base`) select their original cached PNG files directly.
    Their appearance is not evidence of the fallback renderer's accuracy; comparisons require the same item and material.
    **Observed** in installed `rtticons.material` / `rtticons.hlsl`: Base and Coloured variants discard fragments whose normal-map alpha is below 0.5;
    the DXT5N variant decodes X from alpha and Y from green without that transparency test. Meitou's fallback enables the Base normal-alpha cut-out at 0.5,
    separately from worn clothing's 0.6 threshold. `Icons_drawn_from_the_mesh_cover_the_same_part_of_the_picture_as_the_games_saved_ones` includes the starter icons and
    requires 99% silhouette agreement for the Rag Loincloth, whose transparent tears are missed by the opaque samples.
    Not done: the DXT5N variant (the item shader is used), bounding the icon textures
    (one per item and material seen, at most 512 x 512, colour only).
- **Building icons**: the build window's `BuildingImage` shows the chosen BUILDING's picture (FUN_1404e2040 calls FUN_14084af40 with 128 x 128, yaw -90, pitch 30; **Verified**, disassembly).
  - **Saved pictures**: looked up as `<string id>.png` in the `Icons` group (`data/icons/buildings` is one of its folders), drawn and saved to `data\icons\buildings\` only when missing
    (**Verified**). The install's folder holds 618 such files, all 128 x 128, 34 of them under 1 KB (nearly empty; **Observed**).
  - **How it is drawn** (**Verified**, FUN_14084af40, FUN_140845f30, FUN_140845bf0): the parts laid out as for the placement preview (`Materials_BuildingPreviewMaterial`) under a node
    pitched 30 degrees, then yawed -90, about its own axes; the entities' merged world box decides one more yaw of -90 when its half width (X) is less than 1.2 times its half depth (Z).
    The camera (vertical field of view 45, aspect 1, near 0.1, far 2000) looks down -Z from the box's centre and moves back until every corner is inside the frustum: by
    `half depth + max(half width, half height) / tan 22.5` (or the near distance). It renders through the `RTT_Deferred` workspace; its lights are **Unknown**.
  - **Meitou** (`BuildingIcons`, `BuildingIconStyle`, `WorldObjectRenderer.Icons.cs`): the saved picture, else the ghost's parts (interior ones left out; whether the preview keeps them is
    **Unknown**) drawn into a texture through the objects' program, framed from the loaded meshes' bounds (many files lack a bounds chunk), no weather, lit from the camera.
    Against twelve base-game pictures (headless render, not a test): silhouettes agree to 0.999 or better, mean brightness within 10 % on ten, 17 % darker on Outpost s-IV; Stairs (`1092`)
    (the only one of them that takes the second yaw, and its framing matches) draws near-white: its material's `Severe_C_10.dds` averages 226 grey while the saved picture is dark, so the game tints it somehow (**Unknown**). Not done: writing pictures to disk.
- Build window: categories, groups, research gating and the stats panel are in [buildings-production](buildings-production.md#build-menu);
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
| countA, then countA ids | u32 list taken from the manager object at +0x48 (written first), empty in the sample |
| countB, then countB ids | u32 list taken from the manager object at +0x28 (written second), 30 ids 4..33 in the sample |

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
- **Placement**: the label follows the character, 15 units above its node. Colour (0.9, 0.9, 0.9) by default, (0.11, 0.6, 0.11) or
  (1, 0.31, 0.31) when relation tests on the hit character and the camera's player succeed (red tested last, so it wins). Which
  tests these are (`FUN_140678830` with 3 or 4, `FUN_1407916d0`) is **Unknown**; guess: red for the player's side being hit.
- Wall (building) damage uses the same label with a plain `{1,num}` number, random scatter of +-20 units (`FUN_14029d2b0`).
- Rise speed, lifetime and fade are in the label manager's update (not found): **Unknown**. No healing floater exists among the
  callers of `FUN_14073faf0` (**Observed**).

Meitou: the Simple/Full text and the gate follow the above; floaters rise 30 px and fade over 1.4 s (engine choice). Colour: red
when a player's character is hit, green when one lands the blow, else grey. No healing floater, as in the original.

### Tooltips

Code: `UiTooltips` (data, layout), `PlayerInterface.Tooltips.cs` (hover runtime, item rows, HUD texts), `UiPainter.MeasureText`. Decompile of `kenshi_x64.exe`.

- **Classes** (**Verified**, constructors): `ToolTip` (`FUN_140920d10`) makes a `ToolTip` widget of 0.25 x 0.2 of the screen (floats 0x3e800000, 0x3e4ccccd), hidden, unless given a widget.
  `ToolTipFixed` (`FUN_140921440`, skin `Kenshi_GenericTextBoxSkin`, an anchor widget), `ToolTipInventory` (`FUN_140921510`, template `Kenshi_FloatingPanelLight` = `Kenshi_FloatingPanelSkin`
  holding a `Kenshi_GenericTextBox` inset 5 px), `ToolTipDynamic`, `ToolTipStatic` (empty `vfunc_8` / `vfunc_9`: it stays in its widget). Only the HUD builds a Fixed one (`FUN_14072d3b0`, anchor
  `TooltipPanel`), only the inventory code an Inventory one (`FUN_140716a40`).
- **User strings** (**Verified**, `ToolTip::vfunc_7` `140924290`): a widget's `tooltip_type` picks the content: 0 `tooltip_text` as one row (setter `FUN_140920fe0`; empty text shows none);
  1 `tooltip_size` rows of `tooltip_key_N` / `tooltip_val_N` (N from 0, **Observed**: the loop's start is not in the decompile); 2 `tooltip_data` a GameData record
  (`FUN_1409239d0`): its name, and for a TOWN or NEST record (types 13, 85) the town's name, `Faction:`, `Relations:` and the `Residents` rows (**Observed**; not the item tooltip); 4 `tooltip_data` a map
  place (`FUN_140922e60`, see [Map](#map)). Row text may start with an inline colour tag `#rrggbb` (the exe's static colours: `#afa68b` Main, `#140806`, `#492620` Title, `#59231a` Bad, `#bf4834` BadBright,
  `#a8b774` Good, `#444444`, `#777777`, `#14ffdc`; **Verified**, `FUN_14158a2a0` ...). `vfunc_4` (`140921bb0`) registers a widget for tooltips (**Observed**, not read in full). The skin of the plain `ToolTip` and `ToolTipDynamic` is given by their creators (`FUN_14049f5b0`, `FUN_1406e4f50`, not read): Meitou uses `Kenshi_GenericTextBoxSkin` (engine choice, original **Unknown**).
- **Item tooltips** are not user strings (**Observed**: the only ToolTipInventory constructor is in the inventory code): the Item classes' `vfunc_79` write rows into the Inventory tooltip (below).
- **Timing** (**Verified**, MyGUI 3.2 `ToolTipManager`): shown 0.5 s after the pointer rests on the same widget (moving before it shows restarts the wait); once shown it follows the pointer; it hides when the
  widget under the pointer changes or the mouse is captured (a press). The delay Kenshi sets is **Unknown** (`setDelayVisible` is in neither the strings nor the call graph dump). Meitou: the topmost control under the pointer decides, a press
  hides, a drag hides.
- **Layout** (**Observed**, `FUN_140922680`, `FUN_1409208d0`, `FUN_140920d10`, `vfunc_9`s). One row per line: a `PanelEmpty` holding a `Kenshi_WordWrapEmpty` EditBox (WordWrap, Static, MultiLine) with the key
  aligned Left Top and, when there is a value, a second one aligned Right Top. The row sits `0.025` (`0.04` for the Inventory one) x UI scale of the widget's width from the left; its width is the key text width +
  the value text width + 2 offsets; its height the taller text. The first row starts round(0.1 x widget height) from the top, the next ones round(0.01 x widget height) below the one before, and the panel ends round(0.1 x
  widget height) below the last row. `DAT_1421337f0` is the UI scale (it multiplies pixel sizes of 10, 16, 24, 200). Meitou takes the scale as height / 1080 and right-aligns every value to the panel's inner edge.
  The HUD's Fixed tooltip is built on the `TooltipPanel`'s first child, the parchment box (97.2 % of the panel height; **Verified**, the call in `FUN_14072d3b0` passes it to `FUN_140921440`), so its margins come from that box's size (7 px and 1 px at 1080 p) and the panel never gets lower than its own 72 px. Wrapping at the widget's width less the offsets is **Observed** from the row being the whole widget.
- **Placement** (**Observed**): `ToolTip::vfunc_9` puts the bottom right corner at the pointer, an axis that would go negative taking the pointer coordinate instead; `ToolTipDynamic` the same with the width of its
  widest row; `ToolTipInventory` sets the width to 0.17 x `DAT_141f1fb40` (the screen width, **Unknown** by name) and puts the top left at the point it is given, which `ToolTipInventory::vfunc_6` (**Verified**, decompiled) takes from the inventory window holding the item: its top right corner, or the window's left edge less the tooltip's width when the tooltip would pass the screen's right edge (also seen in a base-game screenshot); `vfunc_6` shows it at once from the inventory's mouse-move hook, without MyGUI's tooltip delay; `ToolTipFixed` moves its anchor so that
  the anchor's bottom edge stays and the height is at least the anchor's. Meitou keeps every tooltip on the screen (engine choice), places the Inventory one beside its window as above, shown at once (at the pointer if no window holds the item), and the Fixed one at `TooltipPanel`'s left and
  bottom edge (`position_real` 0.3198, 0.7130, 0.2083 x 0.0667), and draws a Static one inside its own widget (`AIToolTip`).
- **Skins:** Faithful: Floating `Kenshi_GenericTextBoxSkin`; Inventory and Fixed `Kenshi_FloatingPanelSkin` with `Kenshi_GenericTextBoxSkin` inside (inset 5, resp. 97.2 % of the height as the layout's `TooltipPanel`),
  rows in `Kenshi_StandardFont_Medium` measured with the painter's resource-font measuring; the exe's static tags become the palette's Main, Secondary (`#140806`), Title, Bad, BadBright, Good and Greyed, so a palette mod (Dark UI) stays readable (engine choice). Meitou UI: the same rows on one `rust` plate (radius 10, drop shadow; [Meitou UI](../meitou-ui.md)), colours of the kit
  (Title paper-light, Bad / BadBright bad-bright, Good good, Main and the rest main, greys greyed-bright).
- **Item rows** (**Observed**, `FUN_1407ae130`; weapon `FUN_1407b2a20`, armour `FUN_1407a1650`): the name, ` (N)` after it for a stack (Title); `Buyback`; a kind marker `[Ammunition]` (item function 16), `[Research Artifact]`,
  `[Unique]`, `[Food]` (3), `[Book]` (13), `Possibly Edible` and `-Only for:` the races (15); for food `Nutrition` `{n} nu` (the item's current charges +0x118, truncated; the
  constructor `FUN_14075faf0` sets them to `charges`) and `Ingredients` (the names of the record's `ingredients` references that resolve, joined by a 2-char separator, read as ", "; the row
  shows with an empty value for raw food, **Observed** in the 2026-10-22 vanilla Thinfish screenshot: `Nutrition 10 nu`, `Ingredients`), both without a leading `-`; `Stolen from:` the owner (Bad) or `Stolen` (`FUN_1407a0ea0`); for ammunition
  `Damage:` and `Shots` a/b; `Weight` as `{unit} kg`, for a stack `{N} x {unit} kg ({total} kg)`; `Value` and `Sell value` (`c.{n}`, the trader factor after it as ` (+N%)` / ` (-N%)` in Bad; `FUN_14079e7a0`; one unit's price, also for a stack: **Verified**, both call the item's slot 81 (vtable +0x288) with the sell flag 0 / 1, the unit price, never slot 82, the stack's); `Avg. Price` and
  `Price mark-up N%` for crop items; for ITEM records that are not food or ammunition `Charges` a/b when the maximum is above 1 and `Quality` n/100 when not 1. Labels carry a leading `-` (`-Weight`, `-Value`,
  `-Shots`, `-Quality`, ...; **Observed** from the string builders and the `-Weightless` burden rows). A weapon lists its class (`Katana` / `Sabre` / `Blunt weapon` / `Heavy weapons` / `Hacker` / `Crossbow` / `Polearm class`),
  `Cutting damage`, `Blunt damage`, `Blood loss`, `Armour penetration` (when not 0), `Attack bonus`, `Defence bonus`, `Indoors penalty` / `bonus` (Bad when negative, Good when positive, none at 0), the `Damage vs`
  robots / humans / animals / race rows, a `----------------` rule (#444444), then `Weight` as whole kg and the prices; no grade or quality row (**Verified**: the strings `FUN_1407b2a20`
  references). A weapon's grade is its model (`material sid`), whose name the base game's tooltip shows on its own line under the item's name (**Observed** by the owner in game; its colour is not decoded) and crafting recipes put before it (`Mk I Fragment Axe`). Armour lists `Light` / `Medium` / `Heavy Armour Class`, the material, `{n} Grade`,
  `[Weather Protections]`, the cut / blunt / harpoon resistances and the part coverage (`FUN_1407a1650`, `FUN_1407a33a0`). Map items add `[Right-click to learn new map locations]` and `Towns:` (`1407b4e50`).
  Value rows' key colour is `#140806` normally and `#afa68b` when `FUN_140710e10` returns non-null (a trade window?, **Unknown**).
- **Meitou item rows:** name and stack count, `Buyback` while moving the item back would undo a transfer of the open trade (its condition in the original is **Unknown**), the grade line right under it (a weapon's model name in the `ItemGrade` colour of its `level`'s grade, armour's / crossbow's / prosthetic's `{n} Grade`; a brief tooltip puts the model before the name instead), the kind marker (`[Food]`, `Possibly Edible`, `[Book]`, `[Ammunition]`), food's `Nutrition` and `Ingredients`, `Stolen from:` / `Stolen`, for a weapon the class, `Cutting damage`, `Blunt damage` (the
  stats' multipliers as two decimals, **Observed** reading), `Blood loss` (the record's bleed multiplier), `Armour penetration` (x 100 as a percentage), the attack, defence and indoors modifiers and the weight in whole kg;
  for armour the weight class; for a prosthetic (`FUN_1407a9080`, **Verified**) its grade (from its `level`), `-Max HP`, `-----STATS-----`, a row per stat multiplier not 1 at that level as int((m - 1) x 100) `+N%` / `N%`, `-Unarmed damage bonus` when not 0 and a `----------------` rule (Buyback and Stolen marks sit after the grade); the weight; `Value` and `Sell value` with the mark-up while trading (none on a buyback price, an exact refund) (outside a trade Meitou has no shop to price against; the original lists them always); `Charges` and `Quality`
  as above (quality only when above 0 and not 100). **Not shown (Unknown data or format):** research artifact / unique flags, ammunition `Damage:` / `Shots`,
  `Avg. Price` / mark-up, the `Damage vs` rows (their number format is in the helper `FUN_14079f650`, not read), armour weather protections and the grade-scaled skill lines (armour's other rows: `ArmourTooltip`, [Item tooltip](#6-inventory-trade-dialogue-build)), the race list of special food,
  containers' and limbs' own rows (`FUN_1407aa090`, `FUN_1407a9080`).
- **HUD texts** (**Verified**, the type 0 setter's callers `FUN_14072b540` and `FUN_14072d3b0`): per widget `OrdersBlockButton` (defensive mode, "gain 20 to his melee defense skill"), `OrdersHoldButton` ("HOLD POSITION ..."),
  `OrdersPassiveButton` ("In non-combat mode ..."), `OrdersChaseButton` (the Jobs toggle: "If this is disabled then the character won't do any of the jobs listed."), `OrdersTauntButton`, `OrdersRangedButton`,
  `OrdersSneakButton` (two strings joined by a line break), `OrdersMedicButton`, `OrdersRescueButton`, `OrdersProspectButton`, `ShortcutInventoryButton` ("Open character inventory"), `ShortcutStatsButton` ("Open
  character status window"), `ShortcutMapButton` ("Open world map"), `ShortcutTechButton` ("Open research window"), `ShortcutSquadButton` ("Open squad management window"), `ShortcutHelpButton` ("Open help window"),
  `BuildButton` ("Opens the BUILD menu ...") and the floor arrows (the texts name Page Up / Page Down). The texts are in `PlayerInterface.HudTips`; they all show in the Fixed tooltip. The wording of "Pause Game", "Normal
  game speed", "Faster game speed" and "Fastest game speed" is no tooltip: it sits with the key binding names of the controls list (`FUN_1403f0260`, **Observed**), and `FUN_14072d3b0` sets no text on the
  `TimeSpeedButton`s (**Verified**: it makes nine text tooltips, those above); the speed buttons have none. Which tooltip the Chase widget's caption belongs to is **Observed** from its Jobs binding.
- **Status tooltips:** a selected building's header word gets the building classes' texts (`vfunc_114` of the storage, production, farm and turret classes: "Building is in good condition.", "This machine is currently under
  construction.", "This machine has broken down as a result of damage. Send one of your engineers to repair it.", the farm and machine "Output full" / water / harvest / power texts; **Observed**, a title followed by its text
  in the exe's strings, Meitou uses them for every building). The door's `Security:` row: "The door is locked and will keep out intruders." / "The door is unlocked."; `Open/Close:` of a broken door: "It will no longer
  close and needs to be repaired." (`FUN_140304120`; the other door texts need door condition, which Meitou does not model).
- **Medical tooltips** (`FUN_14088a8f0`, **Observed**; each row's text goes on its `LifeBarNTooltip` widget, the health word's on `HealthText`):
  - Blood: "Racial bleed rate multiplier: x{1,num=fix,p=2}" (the race's `bleed rate`), Bad above 1, Good below.
  - A part, with h its health (+0x40), d a pending amount (+0x48), s the stun (+0x44) and m the maximum: h + d - s at or below -m: "Your {1} is totally destroyed." +
    " It's probably the reason you are dead." for a `death` part, else " Lucky it's not something you need to live."; h + d below 0: a `death` part above the KO
    point "This is a dangerous wound and is getting worse. Get some medical attention fast.  Death at {1}" (-m), while unconscious (`FUN_14028dda0`, taken)
    "This is a dangerous wound ... If you can regain consciousness before it goes below {1,num} then you could save yourself.\nIf it goes below that then you
    will have to hope you get rescued by somebody else.\n" (KO point, Bad) + "You will die at {2}"; at or below the KO point "This is a critical wound. If it
    doesn't get medical treatment you will die."; another part "This wound is getting worse. You need first aid.", replaced for a robotic one (`FUN_140643a70`)
    by "This limb is severely damaged and in need of repairs. Luckily though skeleton limbs don't degenerate."; from 0 and more than 20 short of m: "Your {1}
    needs bandaging." (robotic non-vital) or "Your {1} needs bandaging, it's slowly getting worse."; within 5 % of m (m × 0.95 - s ≤ h + d): "Your {1} is
    okay." (d + s ≤ 5) or "Your {1} is in the process of healing.", then, wear (+0x50) above 5, the wear lines (up to 40: "You have {1} points of wear damage,
    this detracts from your max health and can be fixed at a Skeleton Bed.", else "... The manual recommends you find a Skeleton Bed for maintenance.", Bad);
    else "Your {1} needs bandaging, but it's not urgent.". Then with 0 < h + d - s < 60 a line: "Your stats will be negatively affected." (`death`), "Your movement
    speed will be negatively affected." (leg), "Your combat and manual labour skills will be negatively affected." (arm); a leg when `FUN_140883fa0` is false "You
    won't be able to kick."; a lost limb's text becomes "This limb is gone forever.".
  - Hunger (h, the stomach store +0x64): h > 2 "You are well fed and currently have no problem."; 1 < h ≤ 2 "You are suffering from malnutrition, Your stats and
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
    and Normal "You are kind of healthy for now.". In a bed whose recovery rate is above 1: "\nRecovery rate: {1}x" appended.
  - Meitou (`PlayerInterface.MedicalTips.cs`): the Blood row, the part texts without wear, kick and lost-limb lines (d is the bandage's pending healing, the KO
    point the extended line's), the hunger texts without the stomach store (malnourished takes the Hungry word's sentence, starving the Starving word's: the stat
    penalty and the pass-out threshold are not modelled), and the health words Meitou shows.
- **Stats line tooltips** (**Observed**, `FUN_140892120` stats, `FUN_140895350` extended, `FUN_1405d4260` state): row tooltips with breakdowns. Labels seen:
  `Total equipment bonuses:`, `Encumbrance penalty:`, `Weather penalty:`, `Weapon indoors penalty:`, `Indoors bonus:`, `Defensive mode:`, `Defensive combat mode:`,
  `Leg injury penalty:`, `Equipment penalty:`, `Stat Multiplier:`, `Base hit chance:`, `Attack speed:`, `Block speed:`, `Damage:`, `Weapon cutting damage:`,
  `Blunt weapon damage:`, `Damage resistance:`, `Max carry weight:`, `Health KO point:`, `Wound degeneration speed:`, `Dodge skill:`, `Careful Shooting`,
  `Total:`, `-End total:`; the extended lines: Athletics XP (`Encumbrance penalty:`, `Full speed bonus:` when the speed is over 0.8 of the run value, then "This
  is the rate at which your athletics skill is increasing while running."), Strength XP (`Encumbrance bonus:`, `Carrying somebody:`, or `Encumbrance:` /
  `Weapon weight:`, then "This is the rate at which your strength is increasing."), Toughness XP (`Crippled left arm:`, `Crippled right arm:`, `Blood loss
  trauma:`, `Critical wounds:`, then "This is the rate at which your toughness increases when you get hurt."), KO point (the single text "When an injury to a
  body part goes below your KO point of {1,num} then you are down for good. Your body will enter a coma and won't regain consciousness until all critical body
  parts have healed above 0."), Run speed (`Stealth mode:`, `Total:`, ...), Hunger Rate; Defense: "Your defense skill is your chance of blocking enemy attacks.".
  Their values and order are not read. Meitou shows only the KO point's.
- **Other users:** the Overview's AI tab options (a Static tooltip in `AIToolTip`), map places (type 4), trade and inventory items. **Unknown** / not shown: the
  lockpick tooltip of a locked door, the medic and rescue allied-faction option texts.

## Unknowns

- Rise, lifetime and fade of the damage floaters, and the colour tests (section 8).
- Meaning of the two id lists in `__tutorials.data`.
- Why the code looks up ten life bars while the layout has nine (`LifeBar10` is missing; the constructor `FUN_140726a70` looks up `LifeBar1`, `LifeBar2`, ... until one is missing); the medical panel's overall health h; where the stats labels' upper case and the default squad name come from.
- Exact tab index of the AI tab and which layout widgets belong to which tab for tabs 4 to 6.
- Trade, dialogue and research logic (other subsystems).
- Layer of the secondary layouts without a `layer` attribute (Kenshi_OrdersPanelOrder, PortraitCharacter, SquadBox, SquadsPanel_PortraitCharacter, TutorialTooltipPanel, ItemListItem, CharacterSelectionBox): they are children of other widgets (**Observed**, not traced).
