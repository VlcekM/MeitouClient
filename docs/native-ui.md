# Native UI and extensions

UI mode (--ui/Tab) is independent of graphics. Both modes use the installed MyGUI layouts,
skins and fonts for the in-game HUD, windows and Escape menu. Meitou customises the title,
scene transitions and settings; Faithful retains their original presentation where implemented.
Vulkan paints both, choice persisted. `Meitou.Ui` has no window/GPU/simulation dependency.
Host supplies snapshot text/availability and actions that enqueue commands. Extensions receive
read-only binding copies, never mutable world. [MyGUI](formats/mygui.md), [parity](faithful-ui-parity.md).

## Settings

`SettingsMenu` shares category/row geometry and input between both modes: Display, Graphics
(World/Lighting/Water/Effects), Interface, Controls and Advanced. Faithful uses MyGUI skins;
Meitou uses the [UI kit](meitou-ui.md)'s rounded rust plates and paper Save action. Tab or Options
opens it; Esc/the original Kenshi X/Save saves preferences and returns. Changes apply live, with the active
upscaler shown separately from the requested one. Terrain texture quality needs a restart.
The Display and Graphics rows are drawn from the launcher's catalogue (`LauncherSchema.Find` / `Order`): name, range, scale direction,
value text, choices (two choices: an Off | On switch), impact and order come from it, the Tab panel slider only reads and writes the value,
so the game and the launcher ([launcher](launcher.md#graphics)) show the same settings the same way. A slider missing from it keeps the panel's own.
Graphics (`SettingsMenu.Looks.cs`, `SettingsMenu.Presets.cs`; `--settings-showcase` captures it) has two rows above its tabs:
- **Look**: Faithful | Meitou (saved, `faithfulLook`; unknown, all switches on the game's side read Faithful). Faithful sets the game
  exactly as it ships (every Faithful / Meitou graphics switch off, look sliders at the game's value: the one the label names,
  "(0 = game)"; a strength under a feature's switch stays, since the switch off is the game's) and stays a starting point: every option stays free, and what differs from the game is marked and
  counted ("N changes from the game", Revert). Meitou shows the active **preset**: Meitou (built in), two examples (Hot and dry, Vanilla colours), the player's. A preset is Meitou plus the look settings it sets, keyed as `meitou.user.json` saves them, everything else following
  Meitou; files `presets/<name>.json` beside `meitou.user.json` (`format`, `name`, `meitou` version, `values`), the active one in
  `lookPreset`. Rows that differ from it count as unsaved changes (Revert, Save for the player's own). Save as
  names a new one; Share copies a code (`MT1:` and the deflated JSON in base64) to the clipboard, Paste adds one; Delete (own presets).
  Files and codes are read and written by `LookPresetFile` (Meitou.Core), the launcher's Look row too.
  The launcher writes a chosen look's values itself ([launcher](launcher.md#graphics)); at start the game only holds Low's look switches off.
  A quality tier's slider on a look tab (light shafts quality) is not part of the look. Every effect is a switch with its own state
  (off: the game's; on: Meitou's), its strength a slider under it that keeps its value while off (haze, heat haze, tone map and
  grade, night grading, bloom; `PostOptions.MeitouHeatHaze`, `ToneMapping`, `NightGrade`, `Bloom`, `SkyRenderer.MeitouHaze`).
  Bloom is an optional effect (`Toggle.Optional`), in neither the game nor the Meitou look: it reads Off | On, both looks have it off,
  and a look preset may turn it on (Hot and dry, at 0.13).
- **Quality**: Low / Medium / High / Ultra / Custom (derived from the values) sets the distance, foliage, grass, reflection,
  shadow, particle density, filtering, texture, ground relief and light-shaft quality sliders, the fifth cascade and the terrain relief
  switches (parallax and rock smoothing from Medium, displacement and outline refinement from High; Meitou only). High is the start
  default (texture quality: full size); distances that follow a switch's side (object and landmark distance, terrain error: Draw
  distances; shadow distance: Shadows) are multiples of that side's mark, so High reads High in either look. Low is `--low-end`
  (`WorldOptions.LowEnd*`, the same values): its distances, grass and particle density, texture quality, 1x filtering, shadows off,
  ground relief off, quarter-size particles, no anti-aliasing (not even FXAA) at 0.83 render scale; what `--low-end` leaves alone
  stays at High, and its VSync off and material distance are not Tab panel settings. Its Meitou switches (AO, light shafts, water,
  Meitou shadows) are held off without changing the preset: their rows say so, and turning one on keeps it on at Low
  (`keepAtLow`); leaving Low gives them back. The other tiers leave anti-aliasing and render scale alone. The tier last chosen is
  saved (`qualityTier`), and the derived name (`graphicsPreset`) for the launcher; a saved name the settings do not sit at is
  applied at start ([launcher](launcher.md#graphics)).
Tabs by subject: Lighting, Sky & weather, Colour, World, Characters (`SettingsMenu.GraphicsSection`). A tab lists features in their
`FeatureGroups` order (`GroupRank`, the launcher's too), each with all its settings, whether the look or the quality tier sets them
(`SettingsMenu.Role` keeps which: the effects and strengths the look, distances and detail the tier); e.g. Shadows holds the game's shadow
mode, distance, map size, filter and fifth cascade. A tab lists features
(`SettingsMenu.Groups.cs`): each is one row headed by its switch (or a title: Ambient light, Terrain relief) with
its own settings folded under it (a darker band with a brass edge), closed until its arrow or text is clicked. A setting that
differs from the preset has its name in brass and a Reset link back to the preset's value; a feature's header shows it for anything inside.
Only look rows are marked: a quality or display setting moved by hand shows as Custom on the Quality row, and choosing a tier puts it
back. Every on / off setting is a two-halves switch, the active half lit: a Faithful / Meitou effect reads Faithful | Meitou ("Faithful (off) |
Meitou (on)" where the game's side is off), an optional effect or a plain setting Off | On; the launcher's are the same, what each side does in their tooltips. Interior lamps is on Advanced: the
stealth sight counts its light, so no look or Faithful sets it. Each setting has one control: an effect a Faithful | Meitou switch,
named values a drop-down (the anti-aliasing method on Display, weather particles' size), amounts a slider running right = more
(terrain detail's error in pixels is drawn mirrored, `Slider.Reversed`). The `aa` and `particles` switches are not shown and their
saved state is not read (`SettingsMenu.HiddenSwitch`): the drop-downs cover them, and Faithful leaves both alone. Every Graphics row names its **performance impact** (No, Low, Medium,
High, or Faster with Meitou: a switch whose Meitou side saves work), ranked from the measured costs (`SettingsMenu.ImpactOf`; RTX 4090 at 1440p: High about 1 ms or more, Medium
a few tenths), not milliseconds, which differ by card. Advanced (**All gameplay changes**) and Fixes (**All fixes**) start with an
all-Faithful / all-Meitou switch over their own page's switches and sliders with both marks (attackers per target); luckyren Mode is left out.
The modal pauses simulation and captures world input; wheel/PageUp/PageDown scroll, Tab/arrows
focus/adjust, Enter activates. Binding capture rejects conflicting/reserved controls; Esc cancels,
Delete unbinds. Defaults and cache deletion ask first. The viewer retains its debug panel.
Settings use 42px rows with a concise description for every setting, 14px labels,
13px values/buttons and 11px hints, with a 17px label-to-description offset;
paper section headers break up the dark plate. `FontPixels` specifies em pixels,
with 2×2 oversampling, kerning and fractional glyph positioning for small text and motion.
Wheel/PageUp/PageDown scrolling eases toward a clamped target independently of frame rate;
clicking stops the glide. Opening/closing takes 140/100ms, page changes 120ms, hover
brightening eases in/out over 160ms and dropdowns fade over 100ms. Hover keeps the raised
button artwork; the sunken surface indicates a press or selection.
Motion changes opacity/position, keeping type size fixed and input aligned.
Window art is a continuous surface baked at 2x the logical size;
only buttons use nine-slice scaling. The dialog is 1120×720 logical pixels; selection uses
pressed surfaces without yellow focus rings/underlines. Weather/time previews are omitted.
Graphics includes the Meitou switches under their related tabs; Advanced contains cache actions.
Pages omit descriptive blurbs; field hints explain the setting's effect and useful constraints.
The sidebar shows the build's nearest Git tag and short commit, embedded during compilation
(`BuildGitTag`/`BuildGitCommit` can override them for source archives).
Click a slider's number to type a value (selected initially); Enter/Tab or clicking outside
applies it, Esc cancels. Decimals (dot/comma), scientific notation, numpad, Ctrl+A and cursor/
selection editing are supported. Invalid/non-finite input leaves the setting unchanged;
valid input is clamped to the slider range and uses its existing rounding rules.

## Extension API

Provisional ApiVersion1: IUiExtension registered explicitly with UiHost.Register, returning
IDisposable. No automatic mod-DLL scanning/loading; incompatible versions/duplicate module IDs
or keys rejected. Keys namespace extension-id:local-name. Failed registration rolls back;
disposal removes factories/styles/overrides/actions/contributions; re-register supported.

RegisterControl builds UiNodes; RegisterStyle supplies state colours/alignment/skin/font/scale/
TextPadding UiInsets; OverrideStyle scoped replacement. RegisterAction receives read-only frame
context/string argument; disabled controls never call it. Contribute supplies ordered slot tree
with optional UI-mode restriction. OverrideControl can extend context.CreateBaseControl(id).
Source/test interfaces are authoritative for signatures; samples in Ui extension tests.

Builtin `meitou` actions drive the original controls in both modes. UiSlots:
hud.toolbar,hud.selection,hud.squad,hud.overlay; first3 map original regions in both modes,
overlay viewport. Equal-order later contribution draws above earlier; shared slot does not automatically
insert into builtin layout. Override factory or choose explicit child placement.

## Layout, input and lifetime

Panels/labels/buttons/compound children: pixel/percentage/offset/right-bottom placement,
absolute/row/column flow, padding/gaps. Parent disabled propagates; clip drawing/hits to parent/
slot. Labels pass input through; panels block world in region. F7 suppresses all UI mouse regions.
Registration/build/action on UI thread; fresh tree each UiFrameContext, no blocking callbacks.
Painter only receives UiControls, never calls module code. Contribution/action exceptions raise
UiHost.Diagnostic; failed contribution omitted atomically, others continue. Modules own resources
outside registration. HUD uses actual snapshot aggregates, shared portrait rendering and original
screen input in both modes, including I/C/M/T/Y shortcuts, map pan/zoom/orders and modal pause.
Inventory/stat interactions and current gaps are tracked in parity ledger.
The painter collects vertices into one upload/rendering and merges adjacent batches with matching
texture, sampling mode and scissor, retaining painter order. A scissor becomes the full viewport only
when all batch vertices lie inside it. Full GPU barriers/shared state occur at pass boundaries.
Font paths are cached; text geometry is keyed by caption/position/scale/colour and capped
per font at 512 entries/1 MiB, and dropped when the font's glyph atlas grows or restarts (its
generation). Fonts are FreeType glyph caches ([fonts](formats/mygui.md)); a text batch records
its atlas texture per run, so an atlas replaced mid-batch still draws correctly. Shape vertices are written in blocks. Opaque union area is reused
until its rectangle sequence changes. Standalone debug-overlay flushes still own their pass.
Inventory drags draw the cell's icon/count at its footprint size and original grab point,
above windows without intercepting drops; the source hides until release/cancellation.
Escape/right click cancels; changing the source snapshot or closing its window cancels too.
[Host](engine.md), [saves](simulation.md#quicksave-and-quickload).
