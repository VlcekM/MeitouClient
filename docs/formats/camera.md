# Camera

Sources: `kenshi_x64.exe` (the camera rig's set-up, its input, zoom, pitch, pan and ground-clearance steps, the settings loader;
decompiled outside the repository on 2026-10-05 and 2026-10-17), the install's `settings.cfg` and `controls.cfg`, `fcs.def`.
Implemented as facts in `Meitou.Data.World.KenshiCamera` and `Meitou.Game.Host.Cameras.StrategyCamera`. Nothing here was compared
against the running game; the signs and rates come from reading the code.

## The strategy camera (Verified: `kenshi_x64.exe`)

- **Zoom limits: 10 to 2000.** Each zoom step takes the boom's length, subtracts `zoom speed · input · min(length / 600, 0.75)` and
  clamps the result to **[10, 2000]**. Both limits are literals in read-only data: the 2000 is read by the zoom step and by the
  sound update (which normalises the `Camera_Zoom` RTPC by it, height above ground / 2000 × 100) and nothing writes it; no
  CONSTANTS field or setting reaches it (`fcs.def`'s CONSTANTS has no camera field, `settings.cfg` has none).
- **Pitch limits: view direction y in [−0.92, 0.2].** Before a pitch step the camera's real view direction is checked: a step
  downwards is refused once its y is at or below −0.92 (about 67° down), a step upwards once it is at or above 0.2 (about 11.5°
  up). The check comes before the step, so one step's overshoot is possible.
- **So the eye is at most 2000 × 0.92 = 1840 above the pivot** (`KenshiCamera.MaxHeightAbovePivot`).
- **The pivot rides on the ground** (next section). In normal play the eye is at most ~1840 above the ground (or roof) within 2000
  units of it; over a cliff edge the ground straight below the eye can be lower than the pivot's.
- The game's `view distance` setting (5000 in the install's `settings.cfg`, slider given 1500 to 12000) sets the far distance and
  the haze (D = view distance × 10), not the camera.

## The per-frame step (Verified: `kenshi_x64.exe`, camera input update `FUN_1406b0f90` and its helpers)

Once per drawn frame, in this order: zoom (`FUN_1406aeb00`), pan (`FUN_1406ae5b0`), turn and tilt (`FUN_1406afee0`), height
(`FUN_1406aecc0`). `dt` is the real frame time (the same global the sky uses, [../game/game-loop.md](../game/game-loop.md#frame-clocks)),
not the game speed, so the camera works while the game is paused. Several inputs are per frame, so the feel depends on the frame rate.

- **Settings** (`settings.cfg`, read into one block, sliders in the options screen "Controls"):

  | Key | Default | Slider | Meaning |
  |---|---|---|---|
  | `camera speed` | 500 | 150 to 1000 | pan speed (below) |
  | `mouse speed pan` / `mouse speed tilt` | 6 / 6 | 2 to 50 | turn / tilt rate (below) |
  | `camera zoom` | 125 | 10 to 200 | the zoom step's speed (the options screen's "Camera Zoom Speed") |
  | `mouse speed horizontal` / `vertical` | 1.5 / 1.5 | none | scale of the raw mouse movement in pixels |
  | `invert X` / `invert Y` | 0 / 1 | on/off | flip turn / tilt, mouse and keys alike (each stored as a ±1 factor) |
  | `Edge scrolling` | 1 | on/off | pointer at the edge pans |
  | `Rotation marker` | 1 | on/off | the green marker while rotating |

- **Pan.** Keys `camera_forward/back/left/right` (W/S/A/D and the arrows) give a vector of ±1 per axis; with edge scrolling on, a
  pointer within **1 %** of an edge (position / viewport size below 0.01 or above 0.99) adds ±1 for that edge, with no check of the
  interface. The vector is turned by the pivot's orientation (yaw and pitch), its height dropped and the rest normalised, so every
  direction moves at the same speed, but a diagonal is skewed by the pitch (sideways is unchanged, forward shrinks by cos pitch).
  Speed = **`camera speed · boom / 600`** units per second, with no cap or floor (500 at a boom of 600, 1667 at 2000, 125 at 150).
  Any pan clears the character the camera tracks (see Focus and tracking).
- **Turn.** Yaw input per frame = −mouse x · `mouse speed horizontal` while `mouse_rotate` is held and the pointer was not over an
  interface widget, + 9 for `camera_rotate_right` (E), − 9 for `camera_rotate_left` (Q). Input is a **weighted average of the last 20
  frames, weights 1, 0.5, 0.25, ... newest first** (`FUN_1402b2640`; the window starts as zeros), so a key ramps up and a stop coasts
  for a few frames. The yaw (about the world vertical) is `mouse speed pan · input · dt · invertX` degrees; the whole turn is dropped
  when `dt` > 0.2 s. A positive yaw turns the view to the left, so **Q turns the view to the right** (the camera moves left round
  its target: "Rotate camera left"), E to the left, and dragging the mouse right turns the view right (invert X off). At the
  default 6, a held Q/E turns **54° per second**.
- **Tilt.** Pitch input per frame = −mouse y · `mouse speed vertical` while rotating, − 2 for `camera_tilt+` (`,`), + 2 for
  `camera_tilt-` (`.`), averaged the same way but with **weights 1, 0.7, 0.49, ...**. The pitch about the pivot's X is
  `mouse speed tilt · input · dt · invertY` degrees (12° per second from a key at 6); with invert Y on (the default) `,` and mouse
  down both make the view steeper (the camera rises). The limits are the pitch limits above, checked before each step.
- **Rotating** pins the pointer where the drag began (restored each frame) and shows the marker, scaled `min(0.06 · boom, 3)`.

## Focus and tracking (Verified unless marked)

- **`focus_char` (F, event 0x11)** puts the pivot on the first selected character's position (its floor becomes the viewed floor),
  settles the pivot's height at once and clears any tracking. Boom, yaw and pitch stay. It does not follow.
- The save's `CAMERA` record keeps the pivot position and orientation, the boom and the tracked handle ([saved camera](#saved-camera)).

## Saved camera

**Verified** (writer `FUN_1406b0740`, reader `FUN_1407ade60` with `FUN_1406ae8e0`; `rot`/`zoom` of four saves decode to views inside the pitch
limits only in this order, and rebuilding `rot` from the decoded yaw and pitch reproduces them to 1e-4):
- `pos` (vec3): the pivot node's position. `rot` (vec4): the pivot's orientation, the Ogre quaternion copied as is, so stored **w, x, y, z**.
  It is yaw about Y then pitch about the pivot's X with no roll: `rot = Ry(yaw + π) · Rx(pitch)` with yaw 0 looking along −Z and pitch below
  the horizon. `zoom`: the camera node's local z, so −boom (samples −88 to −812). `alt`: a float at +0x60 of the camera, written, then reset to 0 on load (always 0 in the
  samples). `tracking`: the handle above.
- Load: orientation and zoom are set together, then `pos` is checked: x or z outside ±147456 puts the pivot at (500, ground height, 51000).
  Tracking is restored.
- Meitou (`KenshiCamera.ToSaved` / `FromSaved`, `SaveCamera`): the host saves the strategy camera's view and restores it on load. It never
  tracks, so it writes a null `tracking`. Its saves from before this kept `pos` 0 and `rot` (0, 0, 0, 1), which is impossible as w, x, y, z
  (a half turn of roll); `SaveCamera.HasView` takes that as "no view", and such a save puts the camera on the leader.

## In the viewer

`meitou-viewer --world` allows any camera position (`--distance`, `--pitch`, free flight). Within the game's camera heights it
draws the game's haze unchanged; above them it is the viewer's own choice what to show, since the game has no behaviour there
(sky.md "In the viewer"). The measure it uses, `SkyRenderer.EyeClearance`, is the eye's height above the highest ground or water
at the eye and on two rings round it out to 2000 (the longest boom): in-game that stays under ~1840 (plus a roof under the
pivot). The viewer's altitude band starts at 5100 above that and ends at 18700 (a viewer choice; the user's reference view
`--town "The Hub" --distance 40000 --pitch 3` is 3302 above it and stays exactly the game's).
