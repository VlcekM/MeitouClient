# MyGUI UI resources

Sources: the configured Kenshi install's `data/gui/`, the existing
[screen research](../game/ui-screens.md), and [MyGUI's upstream skin/template examples](https://github.com/MyGUI/mygui/tree/master/Docs/Papers).

## Layouts and resources

- **Verified** against every installed `data/gui/layout/**/*.layout` by
  `MyGuiTests.AllInstalledLayoutsAndHudResourcesCanBeRead`: layouts are XML with a
  `MyGUI type="Layout"` root and nested `Widget` elements. Attributes include `type`,
  `skin`, `name`, `layer`, `align`, `position` and `position_real`. Coordinates have
  four whitespace-separated numbers: left, top, width, height.
- **Observed** in `Kenshi_MainPanel.layout`: `position_real` values describe a widget
  relative to its parent, including children of fractional-size panels. Values may
  extend outside 0..1. Meitou evaluates these against the current viewport; exact
  MyGUI integer rounding and resize alignment are still **Unknown**.
- **Verified** by the installed HUD test: named controls include `TimeSpeedButton1`
  through `TimeSpeedButton4`, `MoneyAmountText`, `DayText`, `TimeText` and
  `LifeBar1` through `LifeBar9`; there is no `LifeBar10`.
- **Observed** in the installed HUD: anonymous `TabItem` children have editor squad
  captions, `LifeBar*Value` widgets have example numeric captions, and
  `JobsPanelInfo` has a caption containing literal backslash-n sequences. They need
  runtime population rather than displaying the source's example values.
- **Verified** by the installed resource test: `kenshi_init.xml` is a `MyGUI
  type="List"` document; its `List file="..."` entries resolve through the game's
  resource index. This loads skin, image, pointer, template, colour and layer files.
  `core_settings.xml` has nested `MyGUI` settings elements rather than named resources.
- **Observed** in installed `kenshi_init.xml`: it does not list `kenshi_fonts.xml`.
  The in-game resource loader explicitly adds that font catalog; reading only the
  startup list leaves named fonts unresolved and silently uses the fallback atlas.
- **Observed** in `common_skins.xml` / `kenshi_skins.xml`: `ResourceSkin` carries a
  reference `size` and a texture name; each `BasisSkin` carries `type`, `offset`,
  `align`, and named `State` elements. State offsets select atlas pixel rectangles.
  SubSkin corners and edges combine Left/Right/Top/Bottom with HStretch/VStretch/
  Stretch. SimpleText/EditText parts describe text rather than image slices.
- **Observed** in the install's `kenshi_skins.xml`: native UI styling can reuse
  `Kenshi_GenericWindowSkin`, `Kenshi_GenericWindowHeaderSkin`,
  `Kenshi_Button1Skin`, `Kenshi_SquadPanelSkin` and `Kenshi_InventorySlotSkin`.
  These all refer to the `Kenshi_UI.png` atlas; no textures are copied into Meitou.
- **Observed** in the install's `kenshi_fonts.xml`: standard variants use
  `Exo2-SemiBold.ttf` (Small/MediumSmall/Medium/Large sizes 14/16/20/24), painted
  variants use `Sentencia_1.ttf` (20/25/28). `Kenshi_UI_Messages` uses
  `MailartRubberstamp-Regular.ttf`, size 110; all specify resolution 50.
  All standard and painted fonts set `Antialias` false and `Hinting` use_native.
- **Verified** (decompile `FUN_1403e81a0`, run after loading and on the Font Size
  option): the game resets every loaded `ResourceTrueTypeFont` to
  `Size = (int)getDefaultHeight() + font size2` (settings.cfg, default 3), still in
  points at the resource's resolution; the default height is taken from the
  font as first loaded. MyGUI 3.2.3 (MyGUI.log) makes the default height ascent +
  descent in pixels. **Observed** (user screenshots, text widths): the heights fit
  the hhea ascender and descender each rounded to the nearest pixel at the fractional
  em (each at least the OS/2 Windows value at the rounded ppem). At 1080 p,
  Sentencia 25 pt draws at 22 pt (15.3 px) and Exo2 20 pt stays 20 pt (13.9 px).
  Above 1080 lines the size is scaled by height / 1080 and cut to whole points before that
  reset (**Observed**, 1440 p, 2026-10-22, the French editor beside vanilla's): Exo2 20 pt
  draws at `Effective(26)` = 25 pt, its race description within 1.2 % of vanilla's nine
  line widths (the reset of the scaled 26.67 pt gives 26 pt, +4 %; the 1080 size reset and
  then scaled, 26.67 pt, +9.5 %), 22 px lines as vanilla's; Sentencia 20 pt headings
  ("DESCRIPTION RACE", "STATS RACE") match vanilla's widths to the pixel (scaled after the
  reset: +7 %). The loading tip (`Kenshi_FloaterFont_Large`, Exo2 26 pt) is still +5.5 %
  wide (**Unknown** why). MailartRubberstamp 110 pt gives 108 pt at 1080 p and 144 pt at
  1440 p either way, its stamp matching vanilla's to 2 px. Rounding at the integer ppem
  (head flag bit 3) gives 20 pt for Sentencia and does not fit. Meitou: `KenshiFontSize`
  (default 3), scaled by `UiPainter.ScaledFontSize` (Faithful); the Faithful minimum text
  size is the standard font's size so found (`UiPainter.StandardTextPixels`).
- **Verified** against MyGUI 3.2.2 (`TextView`), **Observed** on the loading screen:
  text is never scaled to its widget. A line taller than its box overflows it and is
  clipped by the widget. Meitou's Faithful mode does the same for font resources
  (`UiPainter`). The Meitou mode still shrinks text with no minimum size to fit.
- **Observed** in the install's font files: `SubstituteCode` 0, `Distance` 2, `TabWidth` 4,
  `Codes` `32 126` and `8127 8217` for the English fonts; `locale/ja_JP` uses
  `ume-pgs5.ttf` with `32 255`, `12288 12543`, `19968 40959`, `65280 65519`. A locale's
  `kenshi_fonts.xml` redefines the same resource names.
- **Verified** against MyGUI `MyGUI3.2.2` (`ResourceTrueTypeFont.cpp`, `TextView.cpp`,
  `EditText.cpp`; the game's MyGUI.log says 3.2.3, which has no tag, and the file's later
  commits up to 2017 are texture spacing, device loss and speed, **Observed** in its git log)
  how the original builds and draws a font:
  - The face is opened at `FT_Set_Char_Size(Size*64, 0, Resolution, Resolution)`: points,
    fractional ppem. Load flags by `Hinting`: `use_native` `FT_LOAD_DEFAULT` (the
    bytecode interpreter), `force_auto` `FORCE_AUTOHINT`, `disable_auto` `NO_AUTOHINT`,
    `disable_all` `NO_HINTING`. Glyphs are rendered with `FT_LOAD_RENDER`: 8-bit
    coverage. **There is no monochrome rendering in 3.2** (`HintingMonochrome` and
    `FT_LOAD_TARGET_MONO` exist only in later versions): `Antialias` false writes white
    luminance with the coverage as alpha, true writes the coverage into both. The
    crisp look comes from hinting and integer positions.
  - No kerning. The pen advances by the hinted `advance.x / 64`; a glyph's quad is at
    pen + `horiBearingX / 64` and `floor(ascent - horiBearingY / 64 - OffsetHeight)`
    below the line top. MyGUI rounds no glyph position; a line's width is `(int)ceil`, so
    centred and right-aligned line starts are whole pixels.
  - The line height is the default height: ascent and descent from the FreeType size
    metrics (`ascender >> 6`, `-descender >> 6`), each raised to the OS/2 `usWin` and `sTypo`
    values times `y_ppem / unitsPerEm`.
  - `Codes`: every `range` adds code points; with none the Basic Multilingual Plane
    (`0 65535`); `hide` ranges are removed afterwards; space is always built; a code
    point the face lacks is dropped. A character outside the set draws the
    `SubstituteCode` glyph if that is in the set, else the face's glyph 0 (`.notdef`):
    Kenshi's 0 is not in 32-126, so the substitute is the `.notdef` box.
  - `TabWidth` is the tab's advance in pixels (0: eight spaces). `Distance` is the gap
    between glyphs in MyGUI's own texture (default 1): no layout effect. Inline `#RRGGBB`
    colour codes are handled by `TextView` (Meitou: `MyGuiTextTags`, [Inline colour tags](../game/ui-screens.md#inline-colour-tags)).
- Meitou draws text through `UiFont` (`src/Meitou.Game/Rendering/Ui/`): FreeType 2.13.2 (via
  FreeTypeSharp), one face per file, one size per `UiFont`, glyphs rasterised into an atlas
  on demand (any code point, UTF-16 pairs as one), the atlas grows to 2048 square and then
  starts over; text geometry caches are dropped when it moves. Measuring, wrapping and
  drawing read the same glyph records, so they agree for every script. `UiPainter.Mode`
  selects the look:
  - **Faithful** follows the rules above: the resource's `Hinting`, `Codes`,
    `SubstituteCode`, `TabWidth`, `OffsetHeight`; fractional size at the resource's
    resolution (1/16 pt steps) scaled by viewport height, so at 1080 lines exactly the
    original's request; MyGUI's line height; no kerning; glyphs on whole pixels. The
    TrueType interpreter is set to version 35 (full bytecode hinting; 2.13 defaults to
    version 40, which ignores most horizontal hints). **Observed**: Exo2 20 pt@50 dpi
    gives ppem 13.9, ascent 14, descent 3 and integer advances (9, 8, 13), which is
    `KenshiFontSize`'s default height. **Unknown**: the FreeType version of the original,
    so pixel-for-pixel equality of the hinted shapes is not claimed.
    `ume-pgs5.ttf`'s bytecode fails in 2.13.2 (`Invalid_Opcode`, both interpreters;
    **Observed**, `FT_LOAD_DEFAULT`/`NO_AUTOHINT`): the glyph falls back to the auto-hinter,
    the font after three failures. `Antialias` true is drawn like false (no luminance
    modulation); no Kenshi font sets it.
  - **Meitou** (default) keeps the earlier look: any glyph the face has (`Codes` are not a
    limit), the substitute, else '?'; whole pixel sizes; `Antialias` false fonts hinted
    lightly at 1x (`FT_LOAD_TARGET_LIGHT`) on whole pixels with unhinted advances, others
    (and explicit `FontPixels` sizes) unhinted at 2x oversampling, placed freely; the 1.25 em
    line; kerning from the font's `kern`/GPOS pairs (read with stb_truetype). `FontPixels`
    text uses the `Kenshi_StandardFont_Medium` resource's file, so a locale's font.
  Pre-built glyphs: the `Codes` ranges up to 1024 code points (Faithful), else ASCII and
  Latin-1; the rest on demand. Lines wrap at spaces and between CJK characters.
- Main in-game UI applies a user-requested minimum of 16 em pixels at 1080 lines,
  scaled with viewport height, including resource fonts and explicit pixel sizes.
  Larger headings retain their sizes. Short captions centre the line box and clip
  within the widget rather than shrinking the font. Loading-screen tips use the
  same baseline. Title, loading banners and settings retain independent typography.
  This is a Meitou display policy, not a
  claim about the original font resources; it applies in both UI modes (a size below the
  minimum is a whole-pixel request at 72 dpi, hinted as the mode says).
- **Verified** against all installed layouts by `AllInstalledLayoutsAndHudResourcesCanBeRead`:
  ResourceLayout expansion produces finite widget bounds. The source widgets are
  kept immutable; template Client regions receive application children, and
  template pieces resize against their original pixel reference dimensions.
- **Observed** in `kenshi_templates.xml`: window captions and Client regions are
  template children; window buttons carry `UserString Event` values `close` and
  `pin`. `Kenshi_ScrollBarH` uses its own name as its root skin. A template root's
  native skin must not be instantiated as another template with the same name.
- **Observed**: a Button whose ResourceSkin has no `SimpleText`/`EditText` part shows no caption (MyGUI draws text through that part):
  `MapCenterButton` has caption "O" on the image-only `Kenshi_CenterMapButtonSkin` and shows only its `> ● <` icon. Meitou skips such
  button captions; other widget types keep theirs (the new-game description needs it).
- **Unknown**: exact native widget skin registration, integer rounding, text/font
  sizing, inherited pick flags, localisation, tab button sizing and clipping
  equivalence with the original. No full MyGUI compatibility claim is made yet.

## Implementation (2026-10-13)

`Meitou.Data.Ui` reads layouts, properties and user strings, decodes skin atlas
states, follows resource lists once per resolved path (including cyclic lists),
and retains each original XML document in memory, including unsupported resource
types. XML external entities and DTDs are disabled. No original assets are stored
in the repository. Resource lookup uses the existing `AssetLocator` and enabled
mod resource folders; whole-mod UI parity has not been established.

Both `--ui faithful` / `--ui meitou` modes read `Kenshi_MainPanel.layout` and render
the install's SubSkin/MainSkin atlas pieces, with the same published simulation
snapshot and command queue. Mode differences and extension contracts:
[native UI](../native-ui.md). Gameplay bindings and fidelity gaps:
[Faithful parity](../faithful-ui-parity.md).

Both modes use the original inventory, stats, overview and Escape menu layouts,
including their ResourceLayout window frames. The screen host handles modal input,
close/pin events, window dragging, selected tabs, text entry and scrolling/selecting
dynamic lists. Synthetic tests cover these interactions and clipping. Stats reads
all trained skills; inventory renders equipment names and pocket cell footprints.
Escape opens the pause menu; its Options action opens Tab settings. Save/Load now
open the original dialogs, show real slots and CAMERA metadata, and queue named
save/load requests for the host's tick boundary. Original slots are load sources;
named writes use Meitou's save root. Exit, replacing a save and replacing the live
game use the original confirmation layout. Title/new-game layouts show installed
artwork/credits and NEW_GAME_STARTOFF data; Begin uses the existing spawn backend.
The map shows the installed image and supports pan/zoom/centre, player/camera
markers and right-click movement. Original map placement/zoom equivalence remains
unverified. Other overview gameplay bindings are unfinished.

The renderer stores targets using a lower-left convention, so top-left UI clip
rectangles are converted before issuing Vulkan scissors. **Verified** by
`UiRenderingTests` with pixel readback and Vulkan synchronization validation.

## Additional resource observations (2026-10-14)

- **Observed** in the installed `Kenshi_InventorySlotSkin`: `TileRect` states carry
  `TileSize`, `TileH` and `TileV` properties. The renderer repeats the atlas cell and
  crops partial cells at the bounds. Inventory presentation uses square cells at 25 px times viewport height / 1080; bag bounds follow the container dimensions, and trader stock
  keeps its full canvas behind a clipped viewport with wheel/arrow scrolling. This scaling policy is Meitou's approximation; the screenshot observations are in [UI screens](../game/ui-screens.md#6-inventory-trade-dialogue-build).
- **Observed** in installed image sets: Group selects a texture and a two-component
  image size; Index selects a named frame sequence; Frame point gives the atlas
  origin. Meitou draws the first frame. The only multi-frame index, `Kenshi_LoadingImage` `dots` (4 frames, rate 1), is named by no layout and no exe string (**Observed**, grep). `Kenshi_TitleScreen` points at
  `title_screen1.dds`; map marker groups point into `Kenshi_UI.png`.
- **Verified** against the installed `Kenshi_TextBoxEmptySkin` by the installed
  resource test: its SimpleText normal state declares colour `0 0 0` without an
  image offset. Text states therefore need independent property decoding even when
  they contribute no atlas rectangle. Synthetic tests verify state fallback and
  explicit widget colour overrides. Standard text templates use dark brown ink;
  runtime parchment paragraphs/list rows use that ink while dark panels can set
  light text explicitly. No original colour rules are inferred from the debug
  overlay's light text default.
- **Observed** in `Kenshi_GenericTextBoxSkin` and `Kenshi_Button1Skin`: SimpleText
  `offset` / `align` specify an inset text rectangle, separately from the atlas
  pieces. The painter now wraps, aligns and clips inside that rectangle, after any
  additional Meitou `TextPadding`. Padding never changes the widget's background
  or clickable bounds. `UiPaddingTests` checks resized skin clients, explicit
  insets and small-widget clipping. The extra paragraph/list insets are a Meitou
  presentation choice requested during review, not a newly verified original rule.

## Widget runtime (2026-10-20)

Code: `UiDocument.Widgets.cs` (layout), `MyGuiScreenHost.Widgets.cs` (input). **Verified** below means read against the MyGUI **3.2.2** tag's source (`ScrollBar.cpp`,
`ListBox.cpp`, `ScrollView.cpp`, `ScrollViewBase.cpp`, `ComboBox.cpp`, `MultiListBox.cpp`, `Window.cpp`, `ControllerRepeatClick.cpp`, `ResizingPolicy.h`); the install logs 3.2.3
(above), whether these classes differ is **Unknown**. No test covers it; the behaviour is by reading, not yet seen in the running game.

- **ScrollBar** (Verified, `ScrollBar.cpp`): parts `Start`, `End`, `TrackPlace`, `Track`, `FirstPart`, `SecondPart`; positions run `0 .. Range-1`. The thumb sits at
  `(line - thumb) * pos / (Range - 1)`, `line` the TrackPlace length; it is hidden when `Range < 2` or `line <= thumb`, FirstPart / SecondPart then take half the line each.
  Thumb size: set by the owner (`line * view / content`), else the skin's Track; at least `MinTrackSize` (the Track's user string, else the bar's; Kenshi: 10). Start / End step by `Page`,
  FirstPart / SecondPart by `ViewPage`, the wheel by `WheelPage` (all default 1), one step per event; held, they repeat after 0.5 s then every 0.1 s (`ControllerRepeatClick`
  defaults, set by `ScrollBar.initialiseOverride`; the Track never repeats). `MoveToClick` (default false): a press on the track parts puts the thumb's centre on the pointer and
  keeps dragging it. Dragging maps the thumb start back to a position rounded to the nearest step. `eventScrollChangePosition` comes from user changes, not setters; Meitou raises `scroll`
  (widget: the bar, or the owner of a template's `Slider` part; `Value`: the position) for bars no widget drives. A free bar's position starts at the skin's thumb (engine choice; the HUD's
  `BehaviorSlider`, `Range` 3, stays in the middle) unless `RangePosition` or a binding says. `VerticalAlignment` defaults true.
- **Kenshi templates** (Observed, `kenshi_templates.xml`): `Kenshi_ScrollBarV/H/VBig/HBig`, `Kenshi_SliderVBig/HBig` and `Kenshi_ScrollBar` all carry the parts above (the Big / Slider ones with `LE_TargetWidgetType`
  ScrollBar); a bar is recognised by type `ScrollBar` or that user string. `Kenshi_BehaviorSlider` is the only bar the installed layouts use directly (`Kenshi_MainPanel`, `Range` 3).
- **ListBox** (Verified, `ListBox.cpp`): lines are `HeightLine` tall (user string of the list skin: `Kenshi_ListBox` and `Kenshi_ListBoxEmpty` 18, `Kenshi_MultiSubListBox` 21; times the template scale;
  an ItemBox has none and keeps Meitou's 22), scrolled in pixels. VScroll shows when `lines * HeightLine - client height >= 1`, else it is hidden and the client takes its width. Its Page and ViewPage are
  one line, the thumb `line * visible / content`; the wheel moves exactly one line per event. Keyboard (host `ListKey`): Up / Down one row (from no selection both pick the first), Home / End,
  PageUp / PageDown a client's worth, a row not fully visible is scrolled to the top (clamped), Return accepts (`accept`). Selecting raises `select` (`Text`: item id, `Value`: row). The key routing
  is the keyboard code's: it calls `MyGuiScreenHost.ListKey(UiListKey)` and uses the result; the list last clicked has the keyboard. Rows keep Meitou's 3 px inset and ink; MyGUI's row
  widgets (hover, selection skins) are not reproduced.
- **ScrollView** (Verified, `ScrollView.cpp`, `ScrollViewBase.cpp`): `CanvasSize` (or `UiBindings.Canvas`) scrolls inside the template's `Client`; a bar shows when the canvas is larger than the client on its axis and
  `VisibleVScroll` / `VisibleHScroll` allow; the template is laid out with both bars, each hidden bar gives its thickness back to the client and the other bar; range = canvas - client; a smaller canvas
  sits by `CanvasAlign` (centred when unset; `Default` = Left Top); bar page 16 px, wheel 50 px (vertical first). MyGUI re-reads the sizes once per update, Meitou iterates to the fixed point.
  The source passes the cross-axis view size as ViewPage (as read); Meitou pages by the view along the axis (**Unknown** which is intended). Children of the client are placed against the scrolled canvas.
  A screen that scrolls a view itself lists it in `NativeScroll` (the map). Without a canvas nothing scrolls and the bars hide: the Overview's `RelationsPanel` (no `CanvasSize` in the layout) loses its static bars.
- **ItemBox** (**Observed**: the base game's empty crafting queue and squad boxes show no bars; MyGUI 3.2's ItemBox derives `ScrollViewBase`): laid out as a ScrollView, so its bars show only when a
  bound canvas overflows the client; with none (every ItemBox Meitou fills itself) both are hidden.
- **Template clients** (**Observed**, the title's credits): a template's `Client` part takes the layout's children only for the widgets that set it as
  their client (windows, views, edit boxes, lists); a plain Widget, TextBox, ImageBox or Button keeps its children itself (`MyGuiTemplates`).
- **Lines and long text** (**Observed**, the save list at 1440 lines): a list with a `SkinLine` lays its lines across the whole client and the
  line skin places the text (Meitou's 3 px inset and padding only without one); a resource font's line longer than its widget is cropped, never
  shortened with "..." (Faithful). The save list's lines are 28 px apart at 1440 lines: the template's `HeightLine` 21 times height / 1080, which
  Meitou applies to the load, save and import windows and the HUD (the squad tabs' `HeaderPlace` scales so too, **Observed**; whether every
  template does is **Unknown**).
- **EditBox** (`EditBox.cpp` with `ScrollViewBase.cpp`; **Observed** in the editor's race description, `Kenshi_WordWrap`, which sets no `VisibleVScroll`):
  the vertical bar shows only when the text is taller than the client (and `VisibleVScroll` is not false); hidden, its thickness goes to the client, so text
  wraps up to 6 px inside the frame (a Hive Prince's description); shown, its thickness comes off the template's client, so the text keeps 14 px clear of it (a
  skeleton's, 1440 p). Meitou measures the wrapped text with
  the painter (`UiBindings.TextHeight`) and scrolls it by the bar (`TextScroll`; page 16, wheel 50 px at the template scale, as the ScrollView's).
  Multi-line EditBox scroll bars are not done. An EditBox's `TextAlign` aligns its client's text (MyGUI `EditBox::setTextAlign`), as its `WordWrap`. A template part's `position_real` becomes pixels once, at the template's own size (`Kenshi_ListBoxEmpty`'s client sits 19 x 18 px in). A ListBox's lines are its `SkinLine` template's skin and font; the selected line is that skin's checked state (`Kenshi_ListBoxItemSkin`: lighter text, no bar). A second click on a line within 0.5 s accepts it (`eventListSelectAccept`). A checked button's caption takes the skin's `*_checked` text colour.
- **Slider** (Observed): no installed layout uses `Kenshi_Slider`, `Kenshi_ListSlider` or `Kenshi_MultiSlider(Small)`; they are supported as templates. `Kenshi_Slider`'s bar has `Range` 100 and no
  `MoveToClick` (so a track press pages by one), giving positions 0 to 99 although its example caption reads 100. Text of template parts is set with `Text["<owner>.NumberText"]`
  (also `ElementText`, `ElementValueText`). `Kenshi_ListSlider`'s Start / End arrows raise `step` (`Text`: start / end, `Value`: -1 / +1). The character editor's own slider tracks keep the old
  contract (a decorating control with the property `Slider`: its action with `Value` the pointer's share of the track, so the `Slider` UI sound keyed on `editor-slider:` still plays).
- **ComboBox** (Verified, `ComboBox.cpp`): the arrow and the client toggle the popup (the template's `List`, hidden in the layout); it is as wide as the combo, `min(lines * HeightLine + frame, MaxListLength)` tall
  (Kenshi: 200, times the template scale), below the combo, above when it does not fit; a click on an item selects (`select`) and closes, a press elsewhere or Esc closes. Down opens a focused combo, the wheel over a
  focused closed combo changes the selection one item. `ModeDrop`, `SmoothShow` (fade) and horizontal flows are not implemented. The install uses combos only in editor layouts (`Fog_Editor`, `Kenshi_LevelEditor`,
  `Kenshi_EffectsPanel`) and `Kenshi_OpenSaveFileDialog` (`CurrentFolder`), so this is widget support: items are `UiBindings.Lists[combo]`, the selection `SelectedItems[combo]`, the closed text the selected item (else `Text[combo]`).
- **MultiListBox** (Verified, `MultiListBox.cpp`): per column a header button (`SkinButton`, in `HeaderPlace`, its `Image` child shows `Up` / `Down` on the sorted column, `None` on the others; the arrows are in `Kenshi_MiscImages`
  group `Arrows`) and a sub-list (`SkinList`, in `Client`); Fixed columns keep their width, Fill columns share the rest (last one the remainder; `ResizingPolicy` is `Auto, Fixed, Fill`); one selection and one scroll
  position for all; only the last sub-list shows its bar. A header click sorts ascending by that column, again reverses; MyGUI's default comparator is a plain string `<` of the cells (Meitou: ordinal). The image is "Up"
  when `mSortUp`; that `mSortUp` means ascending is **Unknown** (assumed). Binding: `UiBindings.Columns[name]` (captions, widths, Fill), `Lists[name]` items with `UiListItem.Cells`, `Sorts[name]` the start sort; the host keeps the
  clicked sort. Header and sub-list skins are expanded once at load as hidden prototype children of the MultiListBox and cloned per column. Column resizing, separators (`SkinSeparator`, `WidthSeparator`) and Auto width are not done.
- **Game's save list** (Observed, decompile of `FUN_140480100`, the load / save window): two columns, `addColumn(<text>, 170)` and `addColumn(<text>, 180)`, `sortByColumn(1, true)` (`mSortUp` starts true, so reversed:
  newest first, arrow "Down"), column 0 Fill, column 1 Fixed; rows are the save names with `{1,datetime}` of the modified time in column 1. The two captions are localised strings (the pot has "Name" and "Date" from
  `savemanager.cpp`; which the window uses is **Unknown**). Meitou's `GamesList` (load and save) uses these columns, the name cell with the save's origin appended; `Kenshi_ImportGamePanel` is not registered.
- **Window** (Verified, `Window.cpp`; layouts): `Snap` (set by every Kenshi window template) snaps an edge to the viewport's within 10 px (`|left| <= 10`, `|right - width| < 10`, likewise top / bottom) while dragging,
  without clamping; Meitou lets a dragged window leave the screen but keeps its top edge and 48 px of it in view (`KeepVisible`), and places others fully on screen. `Movable` false (`Kenshi_MessageBox`, `Kenshi_AdvancedOptionsWindow`) stops the drag; `MinSize` / `MaxSize` (`Kenshi_OpenSaveFileDialog` 200 200) bound the
  size a resize gives. **Observed**: no Kenshi window template has a child with the user strings `Action` or `Scale` (MyGUI's resize / move markers), so the original has no user resizing; the generic grips (`Scale`
  = left, top, width, height multipliers of the pointer's travel, or `Action` Move / Left / Right / Top / Bottom) are supported for mods. On a viewport change a moved window keeps its distance to the edges its alignment
  names (Right / Bottom / centre; Left / Top by default; Stretch keeps both) and a pixel-positioned Stretch root grows by the change; an unmoved window stays centred. This is Meitou's approximation: MyGUI's own
  re-alignment of top-level windows on a render-window resize is **Unknown**.
