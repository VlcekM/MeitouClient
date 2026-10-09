# Lamps (the game's point and spot lights)

Status: 2026-10-09, branch `global-illumination`. The data side, meaning what a LIGHT record holds and where the lights are placed, is in
[formats/lights.md](formats/lights.md). This page covers how the renderer draws them. The claims say where they come from: *from the code*,
*measured* (how), or *open*.

The game draws these lights in both looks, so this is not a Faithful / Meitou switch. `--lights off` turns them off (the parity gates use
it), and the bench's `lights` A/B switch does the same.

## What is drawn

*From the code* (`WorldLamps`, `LampShaders`):

- **Which lights.** The outdoor lights of every placed building (`WorldLights.ForWorld`, without the interior layouts): 1302 in the base
  game (**Measured**, the `lamps` log line). They are read on a worker at start.
  - Interior lights are left out. The game gives its lights no shadows, so they would shine through the walls.
  - Every light is always on. No base-game building is a light building (formats/lights.md), and what the buildings' on state is for
    town buildings is **Unknown**.
- **Binning.** Each frame, the lights whose reach overlaps a 64 × 64 grid of 128-unit cells around the eye (8192 units across, at most
  1024 lights) are written to three textures, one set per frame slot:
  - `uLightCells` (RG32F): each cell's first entry and count;
  - `uLightIndex` (R32F): the light numbers, 1024 per row;
  - `uLightData` (RGBA32F): a row of 4 texels per light: position and radius; radiance and type; spot direction and the cosine of the
    outer half cone; the cosine of the inner half cone and the falloff.

  `uLightGrid` holds the grid's corner, its cell size and its cells per side. Its w is 0 when there is nothing to draw, and then the
  shaders skip the lamps entirely.
- **Shading.** `kenshiLight` adds every light of the pixel's cell, lit the way the sun is: diffuse `π · N·L · radiance · 0.96` times the
  albedo, plus the same GGX specular as the sun's. Here `radiance = colour · power · Attenuation(d, radius) [· SpotFactor]`, with the
  game's attenuation and spot cone (`WorldLights.Attenuation` / `SpotFactor`, ported to GLSL). There are no shadows, as in the game.
  - Pulsing and shimmering lights get the game's effect factor on the CPU, from a clock in seconds × 60. The unit of the game's clock
    value is **Unknown**.
- **Native and legacy.** The legacy programs take the uniform and the three samplers by name. The native frame block has `lightGrid` at
  8608 and the three bindless indices at 8624, 8628 and 8632 (`FrameConstants` is 8640 bytes).
- **Probe GI** ([render-gi.md](render-gi.md)). The probe trace binds the same textures (bindings 10–12, `uLightGrid` in its parameters)
  and adds the lights' diffuse light at each hit (`LAMP_DIFFUSE_ONLY`), so lamp light bounces too.

## Measured

- **Parity** (`image-diff`, 1280 × 720, `--view hub` and `--view swamp`, against `b84b372`):
  - with `--lights off`: mean 0.0000 for both views;
  - with `--gi --faithful gi --lights off`: mean 0.0000 for both views;
  - with the lamps at noon: the Hub mean 0.0000, the swamp mean 0.03.
- **Cost** (RTX 4070, Mongrel at 22:00, 1920 × 1080 with DLSS 0.67, `--ab lights`): +0.07 ms GPU per frame.
- **Picture** (Mongrel at 22:00): small blue-white pools under the lamp posts, mean difference 0.38. The lights' radii are 60–300 units,
  so they are local.

## Open

- What turns a building's lights on and off in the game (its on state, `power on`).
- Lanterns carried by characters and pack beasts, and the lights of weather effects, are not in the list (formats/lights.md).
- The Large Torch Post's light may sit about 25 units above its flame (scale applied twice, as in the exe); not checked in a picture yet.
