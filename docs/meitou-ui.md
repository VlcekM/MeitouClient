# Meitou UI kit

Design reference for the Meitou UI mode ([native-ui](native-ui.md)): the game's materials (rust,
worn paper, charcoal button plates, Exo 2 SemiBold, `kenshi_colours.xml`) as floating plates with
soft corners, used by the Meitou title and settings and, in the Meitou UI mode, by the in-game tooltips (one nine-sliced `rust` plate, radius 10; [ui-screens](game/ui-screens.md#tooltips)). In-game HUD and windows currently use
the original layouts in both modes. Values: [tokens.json](meitou-ui/tokens.json). Live
components: [reference/](meitou-ui/reference/) (web, React 18): run
`tools/scripts/meitou-ui-textures.ps1` once (crops the textures from the install into git-ignored
`reference/textures/`), then open `reference/index.html`. Online copy (private):
https://claude.ai/artifact/U49EczXEMYKM29AyPSGJPm.
The project site ([site/](../site/), Astro + nginx container) uses a texture-free web variant in
`site/src/styles/meitou.css`: same tokens, plates/paper/buttons drawn in CSS, no sprites.

## Surfaces

| Surface | Build | Text |
| --- | --- | --- |
| Plate (windows, tooltips, dialogue, clock) | `rust` under `plate-tint`, 1px `plate-rim` inset, `radius-card`, `plate-drop` | `main` |
| Paper card (cards, primary action, save card) | `paper-inner` stretched whole, `paper-border` inset, `radius-card` | `secondary` |
| Button plate (buttons, selected tab) | Button1Skin 9-slice (6) at 70% brightness, clipped to `radius-btn`; raised skin at 85% on hover, pressed skin for selection/press | `main`, `paper-light` on hover |
| Message banner | MessageSkin (caps 82 / 53), square | `secondary` |

- Never stretch the uncropped GenericTextBoxSkin: its 5px alpha rim (insets L5 R5 T5 B4) reads as a
  halo. Use `paper-inner` and draw the edge (2px `paper-edge`, 1px faint line, 10px `paper-wear`).
- One paper (primary) action per screen; everything else is a plate.
- No window frames. Plates float over the world; behind a stack that must read over the scene, a
  vignette: `radial-gradient(ellipse 620px 470px at 4% 100%, rgba(8,6,5,.93) 25%, rgba(8,6,5,.6) 55%, transparent 80%)`.

## Layout and states

- Radii: buttons/tabs 7, cards/windows/tooltips 10, portraits/badges 4, bars/items 3.
- Main menu (1600×900): stack 320 wide, 56 from left, 36 from bottom; save card, 8 gap, plate
  buttons (New game, Load game, Import game, Options, Credits) 38 tall 6 apart, Exit 4 further. Icon column 24, 12 to the label; menu sprites fit a
  24×20 area (not upscaled) and centre by their visible pixels (alpha ≥ 40, stray blobs from
  neighbouring sprites dropped), not by the transparent padding of the atlas crops.
- Title, both modes: game data or level file problems of the load (missing mod, changed record type...) show bottom right as
  `N game data problems, see log.txt` (13, `InkDanger`); log.txt lists a count by kind and the first 20.
- Type: Exo 2 600. 24 window title, 22 primary label (upper, 0.08em), 20 headings, 16 body,
  14 menu items (upper, 0.1em) and stat rows, 12 captions, 10 key badges (upper).
- Focus 2px `brass-light` ring offset 2. Meitou-only keyboard focus (Tab, Shift+Tab over a window's buttons) draws it round the button in the Meitou UI mode, also on the original layouts' windows ([ui-input](game/ui-input.md#edit-boxes-and-keyboard-focus)); the Faithful mode shows none. Disabled `greyed-bright`, plate at 50%. Paper brightens 10%
  on hover and drops 1px on press. Settings/title hover brightening eases in/out over 160ms.

## Copy

Nouns for labels, verbs for buttons, title case in source (menu styles uppercase it). Numbers in
full (`64 / 80`), money `c.4,250`, true minus `−14`, details joined with ` · `, no percentages,
emoji or exclamation marks.

## Textures

Atlas `data/gui/gfx/Kenshi_UI.png`; rectangles from `data/gui/skins/kenshi_skins.xml`
(**Verified**: read from the XML). The script lists every crop; notable:
GenericTextBoxSkin 341,446,186,102; Button1Skin 612,766 (pushed 741,766) 118×45; MessageSkin
761,251,170,70; rust 1342,120,492,546; menu icons from the order sprite 0,0,256,170.
**Observed** (visual inspection of the cropped `reference/textures/rust.png`): rust includes a
T-shaped metal joint. Settings use its uninterrupted right sheet (atlas 1620,136,200,510),
baked as a continuous window surface rather than stretching the joint across the dialog.
**Observed** (read installed `kenshi_skins.xml`): `Kenshi_CloseButtonSkin` is 31×32;
normal/highlighted/pushed atlas crops start at (697/733/769,542). Settings reuse this skin.
**Observed** (installed `kenshi_skins.xml`): `Kenshi_Button1Skin` normal/highlighted use
the same raised atlas pieces; pushed selects the separate sunken pieces at x741.
**Observed** (installed `kenshi_images.xml`): the order sprite's named pointer images include
arrows, talk, light, lock/lockpick, door/escape, house, tools, mining, lifting, pickup, medical,
hand, search, use/gears, steal, knockout, scavenge and invalid. They are action cursors, not
dedicated save/load icons. `Kenshi_SaveGamePanel.layout` uses a plain captioned Save button;
no dedicated save image was found in these resources or by inspecting the atlas.
