# Faithful UI parity

Scope: reproduce original screens and interactions using available game systems.
Missing gameplay systems are tracked independently. Loading a layout alone is
not feature parity. This ledger describes the implementation, not verified claims
about undocumented original behaviour. Original-file facts are in
[MyGUI](formats/mygui.md) and [UI screens](game/ui-screens.md).

Both UI modes share this in-game HUD, windows, Escape menu and input. Meitou keeps
its custom title, transitions and settings ([native-ui](native-ui.md)); sharing
the implementation does not resolve the fidelity gaps below.

## UI implementation and gaps

| Area | Implemented | Remaining UI work |
| --- | --- | --- |
| MyGUI resources | Layout/resource lists, skin atlas pieces including tiled slots, text colours and text client insets, ResourceLayout expansion, ImageTexture and first-frame image sets (Kenshi uses no animated one), inline `#rrggbb` colour tags, pointer resources (`MyGuiPointers`); localisation ([locale overlay](formats/localisation.md): language folder registered between base and mods, so its fonts and layouts win); font resources through FreeType (source, size, resolution, native hinting, `Codes` ranges, substitute glyph, tab/offset, full Unicode text; [fonts](formats/mygui.md)) | Exact resize rounding |
| Window/input | Original frames, caption, close, pin, drag, modality, tooltips (generic runtime: 0.5 s delay, original metrics and skins; [Tooltips](game/ui-screens.md#tooltips)), tab selection, editable caret/selection, edit-box clipboard (system clipboard) and undo, Tab between edit fields and buttons (Meitou-only traversal; Enter/Space press), Enter default action, dynamic lists with scrolling and clipping; generic ScrollBar (arrows with repeat, page clicks, thumb drag, wheel), sliders, ListBox rows by `HeightLine`, ScrollView canvas and bars, EditBox vertical bar, ComboBox popup, MultiListBox columns with header sort (save / load dialogs), list keys (`ListKey`), window Snap / Movable / MinSize / MaxSize, viewport-change alignment, generic resize grips for mods (**Observed**: Kenshi's windows have none; [widget runtime](formats/mygui.md#widget-runtime-2026-10-20)) | The interface sounds without a trigger (docs/audio.md); ComboBox `SmoothShow` fade (set by `Kenshi_ComboBox`; no Kenshi layout uses `ModeDrop`), MultiListBox column resizing, MyGUI's own top-level re-alignment on resize (**Unknown**) |
| HUD | Clock, money, speed/pause, selection, squad bar portraits (still picture, name, selection frame; click selects, double click centres; the wheel scrolls their rows), a squad tab per squad with members (click or Tab switches the bar), medic/rescue with the jobs list (rows with X to remove, dragged to reorder; Shift + right click on a machine, storage, turret, training dummy or site, or Shift on a menu verb that has a job, adds the job), order toggles (Block, Hold, Jobs on `OrdersChaseButton`, Passive, Ranged, Taunt, Sneak; checked state), the Prospect button and move speed arrows with the speed icon (the original's only speed caption), inventory/stats/map/tech/squad shortcuts; the tutorial panel and the Tutorials window (F1, HLP; [Tutorials](game/ui-screens.md#7-tutorials)); the game view's hotkeys through the rebinding table (orders on NUM0..NUM8, `[` `]`, the window keys; [ui-input](game/ui-input.md#4-action-table)); name and faction, State (Dead, the cage lock, Unconscious, Sleeping, Defensive, Normal, the weather notice) / Goal / Encumbrance, the stats lines, the extended lines, the health word by the original's choice (Dying, BEING EATEN ALIVE!!, Recovery coma, Bleeding out, Crippled, Critical, Limb trauma...), medical rows in the original order with their numbers, layers (splints, crushed and lost limbs) and arrows; the information panel collapsed with its ^ toggle; the floor caption with PageUp/PageDown floor stepping and the build button; the biome banner ([Biome banner](game/ui-screens.md#biome-banner)); the floating progress bar (`Kenshi_ProgressBarPanel`); tooltips of the orders, shortcut, build and floor widgets, the medical rows, the health word and the KO point line, door and building status words; a selected building's lines, condition rows, For sale line, public toggle and the player's Dismantle / pause / door buttons; the town panel ([Town panel](game/ui-screens.md#town-panel)) (the layout's behaviour slider is dead in the original: [orders panel](game/ui-screens.md#4-hud-mainbargui)) | The ranged State line's progress fill, the disguise health words (no disguise simulation), Throw out of town (273, **Unknown** test); the context menu has only the verbs Meitou can give (see ui-input.md section 7) |
| Inventory | Original equipment/pocket, bag and limb windows in both UI modes; square cells, equipment footprints, pale empty cells and translucent black item backings, name captions and weight rows; bags sized to their contents; item icons in the instance's colour (static files, the game's `data/icons` cache, else drawn from the item's mesh; the name box while loading); the held item (lift one unit or the stack, place, merge, swap; a prosthetic onto its stump), paused moves and Arrange commands; item tooltips with the armour, prosthetic and food rows ([Tooltips](game/ui-screens.md#tooltips)) | The tooltip rows listed as not shown under Tooltips (grade-scaled skill lines...); exact original resizing |
| Stats | Original frame/panels sized to the supplied reference; category headings, ordered rows, separate values, painted attributes; installed descriptions, affected-stat and training text; fractional-level progress and racial XP bonus | Stats Affected numbers of the skills (the attributes', Dodge's, Athletics' and Swimming's are shown; [ui-screens](game/ui-screens.md#character-stats-runtime-layout)), exact row skins and help spacing |
| Overview | All original tabs; live messages and the said dialogue lines ([Dialogue log](game/ui-screens.md#dialogue-log)); the Faction tab's name and relation lines ([Faction tab](game/ui-screens.md#faction-tab)); the Squads tab (squad boxes, add, rename, delete, reorder, move/swap and dismiss members; [Squads tab](game/ui-screens.md#squads-tab)); the AI tab's nine option ticks and the Follow Formation combo box, with tooltips ([Squad AI options](game/ai-tasks.md#squad-ai-options)); the Research and Craft tabs ([Production](simulation.md#production) UI); installed map image, pan/zoom/centre, player/camera markers, discovered towns, camps and nests ([Map](game/ui-screens.md#map)), the player's outposts and right-click move orders; the projection and zoom of [Map](game/ui-screens.md#map), faction renaming, contracts and upcoming events ([Faction tab](game/ui-screens.md#faction-tab)) | none known |
| Escape menu | Original layout, pause/resume, Options via Tab settings, exit confirmation, new game, original named save/load dialogs | Menu and dialog keyboard shortcuts beyond defaults (the options are Meitou's own Tab settings, by design) |
| Frontend | Original splash ([Splash](game/ui-screens.md#splash), Interface setting), title image/buttons and credits; the original loading screen ([Loading screen](game/ui-screens.md#loading-screen)) and the background loading bar ([Background loading bar](game/ui-screens.md#background-loading-bar)); start selection and Begin using existing spawn backend; the advanced options panel with seven numeric options ([New-game difficulty](simulation.md#new-game-difficulty)); save metadata/slot selection/name editing and overwrite confirmation; load confirmation; the character editor after a new game ([characters](characters.md#character-editor)) | Continue-slot persistence (currently newest listed slot, hidden if unreadable) |
| Trade | Both modes: `Kenshi_InventoryTraderWindow` (**Verified** as the trader layout) sized by the original's growth rule, character and trader windows side by side from the top-left margin, the scroll view's frame and template scrollbar (arrows, page clicks, thumb drag, wheel), money line, 18 x 18 stock, ARRANGE for the stock; hold stock onto the pocket or bag grid to buy, own items onto stock to sell; right click buys or sells one unit (Shift the stack); tooltips with Value, Sell value (mark-up %), weight, quality and the item rows; the fencing prompt while holding a stolen item over the stock ([ui-screens](game/ui-screens.md#6-inventory-trade-dialogue-build)) | Disabled (red) slot states, a Meitou-styled variant (in-game windows use the original layouts in both modes) |
| Build | `Kenshi_BuildWindow` (Faithful only) opened by B or the HUD build button: categories and groups from the unlocked buildings, name, the building's picture ([Building icons](game/ui-screens.md#6-inventory-trade-dialogue-build)), variant arrows, cost and hours, Commands text, Confirm / Undo, floor arrows, the failure message in the message panel; the covered HUD panels hide while it is open; ghost and site orders ([simulation.md](simulation.md#buildings)) | Descriptions without a record text, the original's list order and Commands text (**Unknown**), a Meitou-styled menu |
| Other screens | XML is readable; confirmation message boxes used by menu flows; `Kenshi_ConversationPanel` ([Dialogue](simulation.md#dialogue)); `Kenshi_ProspectingWindow` (zone, altitude, skill, area, wind, environment, the resource rows, the map with its camera marker; both modes); the import window; `Kenshi_OpenSaveFileDialog` (Import character); the tutorial panel and window | - |

## Fidelity against the documented facts

Faithful mode follows the facts in [UI screens](game/ui-screens.md) and
[UI input](game/ui-input.md); none of this is checked against the running original.

Brought in line with a documented fact:

| What | Now | Source |
| --- | --- | --- |
| HUD money | `c.1,234`, no space after the point | format string `c.{1,num}` (ui-screens.md section 1) |
| Save/load info panel | empty until a save is selected, then the seven lines of the original's data panel: Date, File version, Faction name, Funds, Members size, Location, Day ([Title screen and its windows](game/ui-screens.md#title-screen-and-its-windows)) | FUN_14047e390 / FUN_14047e780 |
| Continue | hidden, not greyed, when no readable save exists | section 3 |
| Esc on a message box | presses its last button (Return already pressed the first) | ui-input.md section 5 |
| T / Y | open the overview on Research / Craft (M on Map); faction has no default key | events 3..6, ui-input.md sections 4 and 6 |

Engine choices that remain; the original is **Unknown** for each:

- Message box captions other than the Exit dialog's ([Title screen and its windows](game/ui-screens.md#title-screen-and-its-windows)), window titles ("INVENTORY - name"; "Prospecting Results" is the exe's text, shown upper-cased like the other titles), confirmation texts other than "Are you sure you want to exit?".
- The faction tab lists every faction but the player's, the `not real` ones and the generation template (the original's filter is **Unknown**).
- Tooltips: the HUD's texts show in a Fixed tooltip on the `TooltipPanel` rectangle; the Floating and Inventory ones use the metrics of [Tooltips](game/ui-screens.md#tooltips).
- Medical rows: the parts in the original's part array order ([part order](game/character-stats.md#part-order)).
- The sandbox keeps Z X C Y P G; with `--sandbox` the faithful C (stats) and Y (craft) shortcuts give way to it.

Deliberate differences from the original:

- The title's version text reads "Meitou - Faithful UI" (the original's is "Kenshi 1.0.68 - x64 (Newland)").
- The message box (Exit and the other confirmations) centres its buttons and draws its text in the light Main colour, for readability (the original:
  buttons where the layout puts them, left of centre, and dark text on the dark window).

`FaithfulInteractionTests.EveryWidgetTheFaithfulModeFillsExistsInTheOriginalLayout` [Slow] binds the HUD and every faithful screen against the installed layouts. It
fails on any text, action, list or progress bound to a widget the layout lacks. It also prints the absent widgets that are only hidden or styled, and the named widgets
that keep their editor caption (about 150): the static labels the original also shows as laid out ("ARRANGE", "BLOCK", "HEAD"), plus fields the test's state leaves
empty (the outpost panel, the biome, dialogue names, the character trade window), which the mode fills when that state exists.

## Gameplay backends

Each screen above is only as complete as the system behind it. Backend state is described once, in
[simulation.md](simulation.md); its gaps are listed in [Open questions and known gaps](simulation.md#open-questions-and-known-gaps).
The ones that limit these screens:

| Screen | Backend gaps |
| --- | --- |
| Trade | Smuggling ([Trade](simulation.md#trade)) |
| Dialogue | Conditions on state Meitou lacks answer "no" ([Dialogue](simulation.md#dialogue)) |
| New game | The seven numeric options are Meitou-encoded ([New-game difficulty](simulation.md#new-game-difficulty)) |
| Import | The salvage of a save without a squad, the "town too close" removal, the old-save tick disabling ([rules](formats/save.md#importing-a-game-savemanagerimportgame-fun_140378a30)) |

## Validation

`MyGuiScreenTests` exercises template client placement, resize alignment,
immutability, drag capture, editing, close, modal interception, lists, clipping,
tab visibility and cycle rejection. The installed-layout test expands and arranges
all layouts using the configured install; it skips when no install is available.
Render captures are smoke checks. No side-by-side or complete interaction parity
gate has passed yet.
`UiRenderingTests` reads back GPU pixels to check top-left clipping against the
renderer's lower-left targets, with synchronization validation enabled.
`FaithfulInteractionTests` clicks installed title/confirmation/inventory controls:
frontend requests go to the host, and the held item only moves at a simulation
tick. `InventoryCommandTests` checks stale drags, occupied cells, stack limits,
incompatible-equipment rejection and failed-arrangement rollback. `SaveCatalogTests` checks
source identity, header metadata, and named-slot validation. `UiStateTests` covers
editing, map zoom/pan and runtime visibility overrides; `MyGuiColourTests` covers
skin state text colours and dark parchment list text.

The targeted UI/catalog/inventory tests pass.
