# UI screens and MyGUI layouts

Sources: `data/gui/**` layouts and resource lists (parsed by a probe), `MyGUI.log`, the exe's RTTI and string
table, the decompile dump (`FUN_<addr>` of `kenshi_x64.exe`), `locale/**`, `data/tips.txt`,
`__tutorials.data`, and a probe listing NEW_GAME_STARTOFF records in the merged database. Input handling is in
[ui-input.md](ui-input.md). Labels: **Verified** / **Observed** / **Unknown**.
Game systems behind the screens: [game-loop.md](game-loop.md) (speed, clock), [character-stats.md](character-stats.md) (stats window, medical panel), [combat.md](combat.md), [ai.md](ai.md) (orders, AI tab),
[economy.md](economy.md) (money, trade, bounty windows), [buildings-production.md](buildings-production.md) (build and research), [factions-squads-towns.md](factions-squads-towns.md) (squads tab, new-game start-offs)
and [../formats/save.md](../formats/save.md) (load, save and import windows).

## 1. Stack and resources

- MyGUI 3.2.3 (**Verified**, MyGUI.log). `kenshi_init.xml` pulls in `core_layer.xml`, `common_skins.xml`,
  `kenshi_skins.xml`, `kenshi_colours.xml`, `kenshi_images.xml`, `kenshi_pointers.xml`, `core_settings.xml`,
  `kenshi_templates.xml`.
- Layers, back to front (**Verified**, core_layer.xml): Wallpaper, Back, Overlapped, Dialog, Middle,
  MiddleFront, Modal, Main, Window, Popup, FadeMiddle, Info, ToolTip, DragAndDrop, FadeBusy, Fade, Statistic,
  Top, Pointer.
- Fonts: `Kenshi_StandardFont_*`, `Kenshi_PaintedTextFont_*`, `Kenshi_FloaterFont_*`, `Kenshi_BannerTextFont`,
  `Kenshi_UI_Messages`.
- Most root widgets use `position_real`, so layouts are resolution independent (**Observed**).
- 52 layout files; 49 are referenced by the exe. Unreferenced (legacy): `ColourPanel`, `Kenshi_EffectsPanel`,
  `infoboxmain` (**Verified**, string search in the exe).
- Localisation: English UI text is the gettext msgid; `locale/<lang>/LC_MESSAGES/main.po` translates it
  (FUN_14009ae80 builds, FUN_1400a95c0 translates). **Verified** for de_DE: "Continue" -> "Fortfahren",
  "New game" -> "Neues Spiel", "Pick Lock" -> "Schloss knacken". Loading tips come from `data/tips.txt`.
  Money format string `c.{1,num}`; save list date `{1,datetime}`.
- Wwise UI sounds (via AK::SoundEngine::PostEvent): General_Click, Hover, Create_or_Load_Game, Inventory_Open /
  Close / Items, Item_Lift / Drop, Sell_Item, Right_Click, Map_Open / Close, Tech_Open / Close, Building_*,
  Stats_*, Change_Level, Taunt, Medic, Prospect_Click, Stealth, New_Biome (**Observed**).

## 2. Screen catalog

Addresses are the class constructor or layout loader (**Observed** unless noted). Layout names are **Verified**: each
`.layout` string is referenced by exactly the listed function in strings.tsv, and 49 of the 52 layout files are
referenced at all. Class names are RTTI names (**Verified** in rtti.tsv) except Splash, MapScreen and MessageBox,
which are descriptive (RTTI knows `MessageBoxManager::Box`; the constructor of a MessageBox is FUN_1406e70a0).

| Screen (class) | Address | Layout | Layer | Data shown |
|---|---|---|---|---|
| TitleScreen | 140918ef0 | Kenshi_MainMenu | Main | Continue, New game, Load game, Import game, Options, Credits, Exit; version text "Kenshi 1.0.68 - x64 (Newland)" |
| Splash | 140916140 (constructor), 140913720 (per-frame fade) | no layout file: widgets built in code from the image sets `Kenshi_Splash_Logos` (texture `physSplash.jpg`, 1024 x 768) and `Kenshi_Splash_LoFi` in `kenshi_images.xml` | - | logos; alpha rises at 3 per second (full in 1/3 s), a slide ends on any input after it is fully visible or after 4 s |
| EscMenu | 1409180e0 | Kenshi_MainMenuPopupPanel | Main | New game, Save game, Load game, Options, Exit game, Resume |
| NewGameWindow | 140917530 | Kenshi_NewGamePanel | Window | start-off title, description, Difficulty, Cash, Play Style; Begin / Advanced options / Cancel |
| NewGameOptionsWindow | 1409151d0 | Kenshi_AdvancedOptionsWindow | Window | difficulty sliders, see [combat.md](combat.md), [character-stats.md](character-stats.md#new-game-advanced-options-that-change-these-mechanics), [buildings-production.md](buildings-production.md#global-constants-used-here) and [factions-squads-towns.md](factions-squads-towns.md#63-nests) |
| LoadGameMenu | 140480b50 | Kenshi_LoadGamePanel | Window | save list, info panel, "Reset squad positions" |
| SaveGameMenu | 140481ed0 | Kenshi_SaveGamePanel | Window | name edit box |
| ImportGameMenu | 140481130 | Kenshi_ImportGamePanel | Window | ticks: positions, buildings, research, dead NPCs, relations; Advanced options; the import kind of the save request is described in [save.md](../formats/save.md#save-requests-fun_14047c310) |
| LoadingWindow | 14091a280 | Kenshi_LoadingScreen | Top | message text with a tip |
| MainBarGUI (HUD) | 14072d3b0 | Kenshi_MainPanel | Middle | see section 4 |
| Overview (ManagementScreen) | 14049f5b0 | Kenshi_OverviewWindow | Window | tabs Map, Faction, Research, Craft, Squads, Messages ("DIALOGUE"), AI |
| MapScreen | 140490210 | in Overview | - | world map, markers |
| ProspectingWindow | 14049c2e0 | Kenshi_ProspectingWindow | Window | resource lines (`Kenshi_ProspectingWindowResourceLine`) |
| CharacterStatsWindow | 1408ba700 | Kenshi_StatsWindow | Window | Attributes, Skills1..4, Statistics, Description1/2 data panels |
| Inventory (character) | 1401538e0 | inventory layouts | Window | slots: back, shirt, head, legs, boots, armour, main weapon, hip weapon, belt, backpack_attach, ARRANGE, LIMBS |
| Inventory variants | 140154cd0 generic, 140154f80 generic fixed, 140155220 animal, 140155910 research, 140155ee0 trader, 140155f90 building, 1401573d0 limbs | | Window | container contents |
| CharacterTradingWindow / Box | 14072a880 / 14072a070 | Kenshi_CharacterSelectionWindow / Box | Middle | character picker for trade ([economy.md](economy.md#12-slaves-prisoners-animals)) |
| DialogueWindow | 140725740 | Kenshi_ConversationPanel | Middle | portraits, names, dialog line, money (dialogue effects and AI contracts: [ai.md](ai.md#dialogue-and-ai)) |
| DialogueSpeechBubble | 140725f30 | Kenshi_SpeechPanel | Dialog | floating speech |
| ContextMenuGUI | 1407977a0 | Kenshi_ContextMenu | Popup | verbs, see [ui-input.md](ui-input.md#7-mouse-and-selection) |
| BuildModeWindow | 1404e5210 | Kenshi_BuildWindow | Middle | categories, buildings, stats, Commands box, Confirm / Undo / Build, floor arrows |
| CharacterEditWindow | 1405f4dd0 | Kenshi_CharacterEditor | Back | character editor |
| MessageBox | 1406e70a0 | Kenshi_MessageBox | Info | message, buttons (Return = first, Esc = last) |
| MessageRoller | 140724af0 | Kenshi_MessagePanel | Popup | rolling event messages |
| Tutorial panel / list | 140975ca0 / 140977640 | Kenshi_TutorialPanel / Kenshi_TutorialsWindow | Window | tutorial text |
| OpenSaveFileDialog | 1401a0b10 | Kenshi_OpenSaveFileDialog | Window | file dialog |
| InteriorModeButtonWindow | 14077a1f0 | Kenshi_InteriorMode | Back / Main / Window (several widgets) | interior mode toggle |

Layer values for MainPanel (Middle), MainMenu (Main), MainMenuPopupPanel / EscMenu (Main), LoadingScreen (Top), ContextMenu (Popup),
SpeechPanel (Dialog), MessagePanel (Popup), MessageBox (Info), CharacterEditor (Back) and the Window layer of the New game, Advanced options, Load, Save, Import, Overview, Prospecting, Stats, Inventory and Tutorial windows are **Verified** from the `layer` attributes of the layout files. Kenshi_InteriorMode has several widgets on Back, Main and Window.

## 3. Main menu and new game flow

- Continue is shown only when a save exists (FUN_14036b680) and the continue name is non-empty (**Observed**).
  Credits are read from `data/credits.txt`. Exit asks "Are you sure you want to exit?" (FUN_140914600).
- New game: left and right arrows cycle NEW_GAME_STARTOFF records (read by FUN_140916620). Fields: ints `money`,
  `start pos X`, `start pos Z`; bool `force start pos`; strings `description`, `difficulty`, `style`; references
  to squad, town, faction relations, research, `force race` (the squads, towns and relations behind them: [factions-squads-towns.md](factions-squads-towns.md#3-relations)).
- **Verified** (probe `probes/ui-verify` on the merged database, `LoadOrder.FromInstall`): 13 NEW_GAME_STARTOFF
  records, 8 from `gamedata.base` and 5 from the bundled `data/rebirth.mod`. Fields are (difficulty string, money,
  style string): gamedata.base: Rock Bottom (Very Hard, 0, RPG, start 66484 / -90000, `force start pos` true),
  Son of a Captain (Normal, 0, RPG), The Cannibal Hunters (Dodgy, 9, Action RPG), The Holy Sword (Harder, 100,
  Wanted Criminal), The Wandering Trader (Easy, 0, Trading RPG), Wanderer (Default, 1000, RPG), Nobodies (Easy, 0,
  RPG), The Freedom Seekers (Easy/Hard combination, 4000, Real-time strategy, start -31700 / 10975);
  rebirth.mod: Guy with a dog (Normal, 13), The Slaves (Hard, 0), Holy Nation Citizen (Default, 200), The Hive
  Exile (Hard, 0), Empire Citizen (Default, 750), all RPG with start 4000 / 4000. Only Rock Bottom has `force
  start pos`; `force race` appears on three rebirth records: The Slaves, Holy Nation Citizen and The Hive Exile
  (reference lists present: squad on all, town on all but Rock Bottom, faction relations on
  Son of a Captain, The Holy Sword, The Wandering Trader and Holy Nation Citizen, research on The Freedom Seekers).
- Advanced options (the nine floats they set are explained in [character-stats.md](character-stats.md#new-game-advanced-options-that-change-these-mechanics)): Hunger time, Chance of death, Global damage multiplier, Production / Research / Building
  speed, Number of nests multiplier, Bandits loot the player, Easy prospecting.
- Load window: multi-column list of saves, info panel from FUN_14047e390 / FUN_14047e780 (labels File version,
  Faction name, Funds, Members size, Location). Save-file layout is in [save.md](../formats/save.md).

## 4. HUD (MainBarGUI)

Layer Middle, per-frame update FUN_140728d50 (**Observed**):

- Shortcut buttons INV, STA, MAP, TCH (research), SQD, HLP; floor panel with text "Floor {1,num}"; four speed
  buttons ([game-loop.md](game-loop.md#speed-and-pause): pause, 1x, 2x, 5x); money / day / time panel (money `c.{1,num}`; the clock text is described in [game-loop.md](game-loop.md#the-clock), money in [economy.md](economy.md#2-money-cats)); squad tab control; medical panel; orders panel;
  information, tooltip, PAUSED, loading and build panels; town / outpost panel; biome name panel with fading text
  (biome at the camera position).
- Medical panel (the body parts it shows are in [character-stats.md](character-stats.md#body-parts-and-injuries)): nine `LifeBar1`..`LifeBar9` widgets in the layout but the code looks up `LifeBar10`, giving the
  MyGUI.log warning "Widget with name 'LifeBar10' not found. [Kenshi_MainPanel.layout]" (**Verified**: log line
  and `Kenshi_MainPanel.layout`, which has LifeBar1..9 only). Life-bar skins in `data/gui/skins` (**Verified**):
  `Kenshi_LifeBarSkin`, `...HurtSkin`, `...HealedSkin`, `...StunnedSkin`, `...CrushedSkin`, `...RobotSkin`,
  `...SplintSkin`; the panel code FUN_140726a70 also references the colour words Green, Yellow, White, Crushed
  and Robot (**Observed**; their use is not decoded).
- Orders panel (FUN_14072b540): buttons Block, Hold, Chase (`OrdersChaseButton`), Passive, Jobs, Ranged, Taunt,
  Sneak, run-speed arrows (`SpeedArrowLeft/Right`; the layout has a symbol image `Run`, the labels Jog / Walk
  were not found), Medic, Rescue, Prospect, a behaviour slider (captions AGRESSIVE / CONFIDENT / SUBMISSIVE,
  **Verified** in `Kenshi_MainPanel.layout`) and a jobs list ([ai.md](ai.md#jobs-and-player-orders)) with the hint "To add jobs, hold shift key when
  giving an order". Hold = hold position; Block = defensive mode ([combat.md](combat.md#technique-choice-block-and-dodge) has a +20 guarding-state term in the block chance; whether it is this stance is **Unknown** there) (+20 melee defence, no attacks; **Verified**
  against the tooltip string in the exe: "gain 20 to his melee defense skill ... will not attempt to make any
  attacks"); Passive = non-combat mode (tooltip strings).

## 5. Overview window

- Tabs in index order: Map, Faction, Research, Craft, Squads, Messages, AI (**Observed**; HUD requests 0..3 map
  to the first four, see ui-input.md section 6).
- Map: `GUI_Map.dds` is 8192 x 8192, DXT1 (**Verified**, DDS header: height 0x2000, width 0x2000, fourCC DXT1).
  The MapScreen constructor FUN_140490210 stores the Vector4 $(-147456,-147456,147456,147456)$ (**Verified**: float
  literals in the constructor), matching the world extent in [../formats/terrain.md](../formats/terrain.md). If
  the image spans exactly those bounds (**Observed**, not checked against marker placement),

  $$\text{world units per pixel} = \frac{2\cdot147456}{8192} = 36$$

  Markers (`Kenshi_MapMarkers`): Ally, Neutral, Enemy, Player, PlayerSelected (names in `kenshi_images.xml`
  and the exe, **Observed**). Overlays MapOverlaysMin / Mid / Max / Characters (strings referenced by
  FUN_140490210, **Verified** in `strings.tsv`). Zoom +/- and centre buttons (`MapZoomInButton`, `MapZoomOutButton`,
  `MapCenterButton` in the layout files, **Verified** by parsing them). Right-click on a map item learns locations (**Observed**).
- Squads tab (the caps `max squads`, `max squad size`, `max faction size` are in [factions-squads-towns.md](factions-squads-towns.md#9-settings-and-constants-that-matter)): faction size text, new squad button, dismiss panel. Messages: "Maximum number of squads
  reached.", "The squad needs to be empty before it can be removed.", "You must have at least one squad."
  Cell views `Kenshi_SquadBox` and `Kenshi_SquadsPanel_PortraitCharacter`.
- AI tab (FUN_140494ce0; the toggles inject jobs into the AI chooser, [ai.md](ai.md#scoring)): Ditch items, Sleep when injured, Sit when idle, Heal Allies and similar options.
- Research tab (FUN_140497930; [buildings-production.md](buildings-production.md#research-and-tech)): requirements, "Right-click to learn tech", unlock categories (buildings,
  weapons, armour, items, crossbow, backpack, robotics).

## 6. Inventory, trade, dialogue, build

- Item placement failure messages (FUN_14070f7e0; the checks behind them are in [economy.md](economy.md#8-buying-selling-fencing)): Out of trading range; No room for that item; You can't
  afford that; The shopkeeper can't afford that; That's not for sale; Got caught stealing; Smugglers only buy
  illegal goods. Stealing and fencing chance text: FUN_140712920. Item icon: FUN_140711200.
- Build window ([buildings-production.md](buildings-production.md#construction) for the rules it enforces): groups come from BUILDING `building category` and `building group`; placement error messages in
  FUN_1404e27e0 (**Observed**).

## 7. Tutorials

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

Each id is the integer at +8 of a tutorial item. The meaning of the two lists (for instance seen / hidden) is
**Unknown**; the sample's ids 4..33 are all below `total`.

## Unknowns

- Meaning of the two id lists in `__tutorials.data`.
- Body part to LifeBar mapping and why the code looks up ten bars while the layout has nine (`LifeBar10` is missing).
- Exact tab index of the AI tab and which layout widgets belong to which tab for tabs 4 to 6.
- Trade, dialogue and research logic (other subsystems).
- Layer of the secondary layouts without a `layer` attribute (Kenshi_OrdersPanelOrder, PortraitCharacter, SquadBox, SquadsPanel_PortraitCharacter, TutorialTooltipPanel, ItemListItem, CharacterSelectionBox): they are children of other widgets (**Observed**, not traced).

## Implementation outline

1. Parse MyGUI layout XML and the skin/resource lists to build a widget tree (or map each named layout to a native
   UI control set); layer names give draw order.
2. Implement screens as a stack: Splash, Title, NewGame (data from NEW_GAME_STARTOFF via `GameDatabase` and
   `LoadOrder`), Loading with tips, then the HUD plus Overview, Inventory, Build, Stats, Dialogue windows.
3. Use gettext `.po` files for text (msgid = English string).
4. Map projection from the 8192-pixel map and the 36 units/pixel formula; read saves per [save.md](../formats/save.md).
5. Tutorials: reproduce the `__tutorials.data` header and lists as opaque until their meaning is known.
