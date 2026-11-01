# Native UI and extensions

UI mode (--ui/Tab) is independent of graphics. Both modes use the installed MyGUI layouts,
skins and fonts for the in-game HUD, windows and Escape menu. Meitou customises the title,
scene transitions and settings; Faithful retains their original presentation where implemented.
Vulkan paints both, choice persisted. `Meitou.Ui` has no window/GPU/simulation dependency.
Host supplies snapshot text/availability and actions that enqueue commands. Extensions receive
read-only binding copies, never mutable world. [MyGUI](formats/mygui.md), [parity](faithful-ui-parity.md).

## Settings

`SettingsMenu` shares category/row geometry and input between both modes: Display, Graphics, Interface, Controls, Advanced, Audio, Gameplay,
Fixes. Faithful uses MyGUI skins; Meitou the [UI kit](meitou-ui.md)'s rust plates and paper Save. Tab or Options opens it;
Esc/X/Save saves and returns. Changes apply live (the active upscaler shown beside the requested one); texture quality needs a
restart. Display and Graphics rows come from the launcher's catalogue (`LauncherSchema.Find` / `Order`: name, range, direction,
value text, choices, impact, order), so the game and the [launcher](launcher.md#graphics) show them alike; the Tab panel slider
only reads and writes the value.

Graphics (`SettingsMenu.Looks.cs`, `.Presets.cs`, `.Groups.cs`; `--settings-showcase` with `--screenshot` captures it) has two
rows above its tabs:
- **Look**: Faithful | Meitou (`faithfulLook`). Faithful is the game as it ships: every Faithful / Meitou effect switch off,
  look sliders at the game's value (the one the label names, "(0 = game)"), a strength under a switch kept. It is a starting
  point: changes from it are counted ("N changes from the game", Revert). It leaves the pure-performance switches alone (Mesh
  welding, Far impostors, Foliage levels, Character levels: `PerformanceSwitches`), which save memory or time without changing
  the picture. Meitou shows the active **preset** (`lookPreset`): Meitou (built in), examples (Hot and dry, Vanilla colours),
  the player's. A preset is Meitou plus the look values it sets, keyed as `meitou.user.json` saves them; changes from it are
  unsaved (Revert, Save for one's own; Save as, Delete). Files `presets/<name>.json` beside `meitou.user.json` (`format`,
  `name`, `meitou` = the build's tag, `values`); Share copies `MT1:` + base64 of the deflated JSON, Paste adds one (at most
  64 KiB inflated; names at most 40 characters, `LookPresetFile`, shared with the launcher). A preset whose file fails to write is
  not added. Unsaved `faithfulLook` is guessed from the effect switches (tier switches and bloom ignored) and not saved;
  `--faithful all` / `--meitou all` set it, a partial list leaves it guessed.
- **Quality**: Low / Medium / High / Ultra / Custom, always the tier the settings sit at (`CurrentPreset`; a moved slider makes
  it Custom). It sets the distance, foliage, grass, reflection, shadow, particle density, filtering, texture, ground relief and
  shaft quality sliders, the fifth cascade and the relief switches (rock smoothing from Medium, outline refinement from High;
  Meitou only). High is the start default; distances that follow a switch's side (Draw distances, Shadows) are multiples of that
  side's mark, so a tier reads the same in either look. Low is `--low-end` (`WorldOptions.LowEnd*`): also shadows and relief
  off, quarter-size particles, no anti-aliasing at 0.83 render scale. Low holds the costly look switches off (AO, light shafts,
  water, Meitou shadows, parallax, displacement) without changing the preset; turning one on keeps it on at Low (`keepAtLow`),
  leaving Low gives them back. The tier's name is saved (`graphicsPreset`); a saved name the settings do not sit at (one the
  launcher chose) is applied at start.

Effects with a strength are a switch plus a slider that keeps its value while off (haze, heat haze, tone map and grade, night
grading, bloom). Bloom is optional (`Toggle.Optional`): off in both looks, Off | On, a preset may turn it on. Settings layout 2
(`UserConfig.Migrate`) drops layout 1's strengths saved at the game's value with their switch off, and a tone map of 0 (the
clamp is now the switch's off side). **Open:** presets and saved values are keyed by labels; renaming one loses them (no stable ids).

Tabs by subject: Lighting, Sky & weather, Colour, World, Characters (`GraphicsSection`). Each lists features in `FeatureGroups`
order (`GroupRank`, the launcher's too): a row headed by its switch or a title, its settings folded under it until opened,
whether the look or the tier sets them (`Role`). A look row that differs from the preset has a brass name and Reset (a header for
anything inside); a moved quality row shows as Custom. On / off settings are two-halves switches (Faithful | Meitou for an effect,
Off | On otherwise); named values are drop-downs, amounts sliders with right = more (terrain error mirrored, `Slider.Reversed`).
The `aa` and `particles` switches are hidden and not read back (`HiddenSwitch`): drop-downs cover them. Interior lamps is on
Advanced (stealth counts its light). Each Graphics row names its **impact** (No, Low, Medium, High, or Faster with Meitou;
`ImpactOf`, ranked from measured costs, RTX 4090 1440p: High about 1 ms or more, Medium a few tenths). Advanced and Fixes start
with an all-Faithful / all-Meitou switch over their page's switches and two-mark sliders (luckyren Mode left out).
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
Advanced contains cache actions.
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
