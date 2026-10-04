# Camera

How far and how high the game's camera can go: what the haze and lighting (which assume an eye near the ground, see
[sky.md](sky.md#haze-distance-fog-how-vanilla-does-it)) can be relied on for, and where the viewer's own choices begin.
Labels as in [../README.md](../README.md).

Sources: `kenshi_x64.exe` (the camera rig's set-up, its zoom, pitch, movement and ground-clearance steps, the settings loader;
decompiled outside the repository on 2026-10-05), the install's `settings.cfg` and `controls.cfg`, `fcs.def`. Implemented as
facts in `Meitou.Data.World.KenshiCamera`.

## The strategy camera (Verified: `kenshi_x64.exe`)

- **A pivot and a boom.** The camera hangs off two scene nodes: `camera_center` (the pivot, which yaws and pitches) and its child
  `camera_node` (the camera itself), placed on the pivot's local Z axis at a distance, looking at the pivot. On creation and on
  reset the boom is **150** units and the pivot is pitched **30°** about X (a virtual call on the camera with 5.0 follows, presumably its near clip: **Observed**).
- **Zoom limits: 10 to 2000.** Each zoom step takes the boom's length, subtracts `zoom speed · input · min(length / 600, 0.75)` and
  clamps the result to **[10, 2000]**. Both limits are literals in read-only data: the 2000 is read by the zoom step and by the
  sound update (which normalises the `Camera_Zoom` RTPC by it, height above ground / 2000 × 100) and nothing writes it; no
  CONSTANTS field or setting reaches it (`fcs.def`'s CONSTANTS has no camera field, `settings.cfg` has none).
- **Pitch limits: view direction y in [−0.92, 0.2].** Before a pitch step the camera's real view direction is checked: a step
  downwards is refused once its y is at or below −0.92 (about 67° down), a step upwards once it is at or above 0.2 (about 11.5°
  up). The check comes before the step, so one step's overshoot is possible.
- **So the eye is at most 2000 × 0.92 = 1840 above the pivot** (`KenshiCamera.MaxHeightAbovePivot`).
- **The pivot stays on the ground.** Each frame the pivot's height is set from rays cast straight down at the pivot (terrain,
  and buildings it stands on or in), and the eye is kept above the ground under it by `min(0.2 · boom, 20) + 10`. So in normal
  play the eye is at most ~1840 above the ground (or roof) within 2000 units of it; over a cliff edge the ground straight
  below the eye can be lower than the pivot's.
- **Settings.** `settings.cfg` `camera speed=500` and `camera zoom=125` are read into one settings block; `camera zoom` is read
  into the slot that the zoom step multiplies (the options screen's "Camera Zoom Speed"): a speed, not a limit (**Observed**: that
  the block's base is the address the zoom step reads was inferred from the neighbouring fields, not traced).
- **Free camera** (`toggle_fps_camera`, `;` in `controls.cfg`, "Free camera mode"): flies with a velocity and is only kept a
  little above the ground; no ceiling was found in its update (**Observed**; a debug-style mode, not the normal camera).
- The game's `view distance` setting (5000 in the install's `settings.cfg`, slider given 1500 to 12000) sets the far distance and
  the haze (D = view distance × 10), not the camera.

## In the viewer

`meitou-viewer --world` allows any camera position (`--distance`, `--pitch`, free flight). Within the game's camera heights it
draws the game's haze unchanged; above them it is the viewer's own choice what to show, since the game has no behaviour there
(sky.md "In the viewer"). The measure it uses, `SkyRenderer.EyeClearance`, is the eye's height above the highest ground or water
at the eye and on two rings round it out to 2000 (the longest boom): in-game that stays under ~1840 (plus a roof under the
pivot). The viewer's altitude band starts at 4000 above that and ends at 15000 (a viewer choice; the user's reference view
`--town "The Hub" --distance 40000 --pitch 3` is 3302 above it and stays exactly the game's).
