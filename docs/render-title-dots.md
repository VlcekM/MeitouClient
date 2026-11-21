# Light dots in the title scene (unsolved)

Investigation of 2026-11-21, stopped before a cause was found. Nothing here is a fix; the mechanism is unknown.

## The report

Sometimes, in the main menu, single light pixels speckle the scene. A restart was thought to fix it.

Setup: RTX 4070, driver 610.88, 1920x1080 borderless, vsync off, 144 fps limit, DLSS at render scale 0.67 with sharpness 0.3, DLSS frame generation on.
Meitou look with Hybrid GI and its radiance cache on, traced sun shadows, reflections, lamp shadows, light shafts 0.8, Meitou fog; depth of field and
bloom off. Release build of the working tree at `c2615db7d`.

## What the pictures show

**Observed** in the game's own F8 screenshots (`_screens/screenshot_155.png` to `158.png` of the install), so the dots are in the picture Meitou saves:

- Each dot is one display pixel, alone: its eight neighbours are unchanged.
- A dot is about 10 to 30 levels (8-bit) above its surroundings, on average +21 / +17 / +16 in red, green and blue. The amount does not follow the
  surface's brightness (correlation 0.06 over 1787 dots, base luminance 20 to 200): light is added, the surface's colour is not scaled.
- Dark dots come with them, fewer: on the ground right of the menu 1787 bright and 1011 dark single pixels, against 799 and 396 in the clean picture
  of the same view (ordinary texture detail).
- They lie on all geometry (terrain, rocks, the character, objects), about 0.3 % of the ground's pixels, and nowhere on the sky.
- No grid: their positions are flat modulo 2, 3 and 4 and do not follow the interleaved gradient noise. The 3 and 6 pixel peaks of a picture's
  autocorrelation are in clean pictures too (the 1.5x upscale).
- They do not move: of 869 dots in screenshot 157, 839 are at the same pixels in 158, taken 16 s later.
- They are the same from one run to the next: 693 of those 869 pixels are dots in screenshot 155 as well, another process and the same title camera.
- A restart does not prevent them. Screenshot 156 (20 s after the title's warm-up) is clean; 157 and 158 (same run, about 110 s after it, the Settings
  window open on its Advanced page) have them. Screenshot 155 was taken about 105 s after the warm-up of the run before.

## What did not reproduce them

**Observed** on the same PC, the same build and the user's settings file, each picture checked by counting pixels more than 8 levels above all eight
neighbours:

- Offscreen `--screenshot <png> --ui-panel title --size 1920x1080 --upscaler dlss`, with the usual caches and with an empty `--cache-root`: no dots in
  either, the pair differs only by ordinary noise. This path draws another view (the Hub's ground), not the title scene.
- The title loop itself, hidden (`--hidden-window --no-audio --size 1920x1080 --control-dir <dir>`, pictures by the control channel's
  `screenshot <png>`), with an empty `--cache-root` (warm-up 103 s, shaders compiled): clean 15 s and 75 s after the warm-up.
- The same with warm caches for six minutes, seven pictures: no dots on the character; the ground's count stays level after the first picture
  (640, then 868, 961, 942, 911, 854, 902).

So neither a cold shader or pipeline cache nor time in the menu brings them out under those conditions. The hidden runs were not the user's
conditions in three ways: vsync was on with no frame limit, the swapchain was 1920x1062, and nobody touched the interface.

Not tried: opening the Settings window in the hidden run (`ui-open settings[:<page>]`), vsync off with the 144 fps limit, switching single rows off
with `gfx "<row>" 0|1` once the dots are there.

## Where the code leaves room

Read, not tested. After the upscaler the chain runs at the display size: the fog volumes' late shadow, the late particles, depth of field, exposure,
bloom, the composite, then FXAA or SMAA and the heat haze (`PostProcess.End`, `RunUpscale`).

- The composite's sharpening keeps a pixel within the range of itself and its four neighbours, and its dither is 1/255: neither can make a pixel
  that stands 10 levels above all its neighbours.
- A dot one display pixel wide is narrower than a render pixel (1.5 display pixels), so it comes from a pass after the upscaler or from the
  upscaler's own output.
- `LateFogShadow` adds its filtered layer to the upscaler's output. It ignores what `halfUpsampled` returns: where no half-size texel's distance
  matches the pixel's, the weights fall under the 1e-6 floor and the layer there is near zero, so the pixel keeps the unshadowed fog's light.
- `ParticleLate` gives the four surfaces one value where they lie within 4 % of each other, so it has no per-pixel term on a flat surface.
- The per-frame sequences run on different counters: the jitter (18 phases at this scale) and GTAO on `PostProcess._frameIndex`, the light shafts'
  noise and the clouds on `GpuFrame.Number`, the Meitou shadows on their own `_meitouFrame`.
