# Sky, light and atmosphere

Part of the world view of `meitou-viewer` (and the game, which shares `src/Meitou.Rendering`); options, keys and the rest of the world mode are in [viewer.md](viewer.md#world-mode).

- **Sky, light and atmosphere** (`SkyRenderer`, `AtmosphereShaders`, `SkyClock`, `SkyXModel`, `KenshiHaze`, `KenshiLighting`,
  `AmbientMap`; facts in [formats/sky.md](formats/sky.md) and [formats/lighting.md](formats/lighting.md)): the sun follows the
  game's formula for the hour (latitude 54, sunrise 5, sunset 23). In game-sky mode (default) everything is in the game's own HDR
  units and numbers. The sky is SkyX's skydome evaluated per pixel with the game's options (wavelengths 0.57 / 0.48 / 0.44,
  exposure 1.4, 4 samples, HDR mode), the night glow, the game's starfield on its own dome mapping (or, with the Meitou `stars` switch, a procedural night sky: point stars and a Milky Way turning about the sun's axis, [formats/sky.md](formats/sky.md) "Meitou night sky") and the game's two planets (Moon, Moon2: fixed low over +x −z, lit by the sun); no sun disc (the game has none in
  the dome; its sun is the Mie glow), with `--clouds` or a cloudy weather a cloud layer (a stand-in). Terrain, objects and grass
  are lit by `kenshiLight`, the game's deferred lighting model: the sun colour taken from SkyX towards the sun the way the game's
  sky controller takes it, times the daylight factor and the per-biome ambient map's sun brightness; the image-based ambient
  from `mp_irradiance.dds` times the ambient map's colour; GGX sun specular and the `mp_specularity.dds` environment specular with
  the game's environment BRDF. No shadows (every face towards the sun is fully lit). Every world shader ends with
  `colour = atmoApply(colour, eye, position)`, the game's haze (sky.md, "Haze"). The post-processing applies the game's auto
  exposure from the measured mean luminance, so the screen brightness follows the game's `0.55 / adapted` with the CONSTANTS band.
  At night the game's haze is black and hides everything past a few thousand units; the `night` switch (Meitou, default) thins it to a quarter
  once the sun is down so the far land stays visible ([formats/sky.md](formats/sky.md), "Night haze"; `--faithful night` for the game's).
  The game's night land has only that flat ambient; the `planetshine` switch (Meitou, default; `--faithful planetshine` for the game's) makes the big planet the light
  once the sun is down: `Planetshine` in `NightSky.cs` gives its irradiance from its phase, size and mean albedo (times a strength, default 60), and
  `SkyRenderer.Prepare` publishes it in place of the sun's light (`uAtmoLight`, `uAtmoSunLight`, the `WorldLighting`, the shadows' direction), blended
  through twilight ([formats/sky.md](formats/sky.md), "Planetshine"); the ambient, the sky and the exposure stay the game's.
  New shaders include `AtmosphereShaders.Functions` (sky.md, "Using the atmosphere in a new shader"). `--simple-sky` / `B` give
  the old colour model, light and fog, with a fixed exposure.
  Cost: not re-measured after the 2026-10-05 rewrite (the sky's integral now runs per pixel, about 4 × 5 exponentials, instead of
  a table lookup; the haze runs the same 4-sample integral per pixel). Earlier figures (2026-10-04, table version): the sky pass
  0.06 to 0.09 ms GPU at 1280 × 720 (`MEITOU_SKY_BENCH=1` with `--screenshot` times 100 passes).
  With the Meitou `stars` switch at night (2026-10-10, 1920 × 1080, RTX 4070, minimum of five runs): the sky pass 0.31 ms against 0.12 ms for the game's starfield texture (0.24 ms without the faint star layer, which an integrated GPU skips); by day the stars are skipped (0.11 ms).
