# MyGUI UI resources

Sources: the configured Kenshi install's `data/gui/`, the existing
[screen research](../game/ui-screens.md), and [MyGUI's upstream skin/template examples](https://github.com/MyGUI/mygui/tree/master/Docs/Papers).

## Layouts and resources

- **Verified** against every installed `data/gui/layout/**/*.layout` by
  `MyGuiTests.AllInstalledLayoutsAndHudResourcesCanBeRead`: layouts are XML with a
  `MyGUI type="Layout"` root and nested `Widget` elements. Attributes include `type`,
  `skin`, `name`, `layer`, `align`, `position` and `position_real`. Coordinates have
  four whitespace-separated numbers: left, top, width, height.
- **Verified** by the installed HUD test: named controls include `TimeSpeedButton1`
  through `TimeSpeedButton4`, `MoneyAmountText`, `DayText`, `TimeText` and
  `LifeBar1` through `LifeBar9`; there is no `LifeBar10`.
- **Verified** by the installed resource test: `kenshi_init.xml` is a `MyGUI
  type="List"` document; its `List file="..."` entries resolve through the game's
  resource index. This loads skin, image, pointer, template, colour and layer files.
  `core_settings.xml` has nested `MyGUI` settings elements rather than named resources.
- **Verified** against MyGUI 3.2.2 (`TextView`), **Observed** on the loading screen:
  text is never scaled to its widget. A line taller than its box overflows it and is
  clipped by the widget. Meitou's Faithful mode does the same for font resources
  (`UiPainter`). The Meitou mode still shrinks text with no minimum size to fit.
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
    colour codes are handled by `TextView` (Meitou: `MyGuiTextTags`, Inline colour tags).
- Meitou draws text through `UiFont` (`src/Meitou.Game/Rendering/Ui/`): FreeType 2.13.2 (via
  FreeTypeSharp), one face per file, one size per `UiFont`, glyphs rasterised into an atlas
  on demand (any code point, UTF-16 pairs as one), the atlas grows to 2048 square and then
  starts over; text geometry caches are dropped when it moves. Measuring, wrapping and
  drawing read the same glyph records, so they agree for every script. `UiPainter.Mode`
  selects the look:
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

The renderer stores targets using a lower-left convention, so top-left UI clip
rectangles are converted before issuing Vulkan scissors. **Verified** by
`UiRenderingTests` with pixel readback and Vulkan synchronization validation.

## Additional resource observations (2026-10-14)

- **Verified** against the installed `Kenshi_TextBoxEmptySkin` by the installed
  resource test: its SimpleText normal state declares colour `0 0 0` without an
  image offset. Text states therefore need independent property decoding even when
  they contribute no atlas rectangle. Synthetic tests verify state fallback and
  explicit widget colour overrides. Standard text templates use dark brown ink;
  runtime parchment paragraphs/list rows use that ink while dark panels can set
  light text explicitly. No original colour rules are inferred from the debug
  overlay's light text default.

## Widget runtime (2026-10-20)

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
- **Slider** (Observed): no installed layout uses `Kenshi_Slider`, `Kenshi_ListSlider` or `Kenshi_MultiSlider(Small)`; they are supported as templates. `Kenshi_Slider`'s bar has `Range` 100 and no
  `MoveToClick` (so a track press pages by one), giving positions 0 to 99 although its example caption reads 100. Text of template parts is set with `Text["<owner>.NumberText"]`
  (also `ElementText`, `ElementValueText`). `Kenshi_ListSlider`'s Start / End arrows raise `step` (`Text`: start / end, `Value`: -1 / +1). The character editor's own slider tracks keep the old
  contract (a decorating control with the property `Slider`: its action with `Value` the pointer's share of the track, so the `Slider` UI sound keyed on `editor-slider:` still plays).
