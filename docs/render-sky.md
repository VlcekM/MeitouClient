# Sky, light and atmosphere

Part of the world view of `meitou-viewer` (and the game, which shares `src/Meitou.Game/Rendering`); options, keys and the rest of the world mode are in [viewer.md](viewer.md#options-and-modes).

- **Sky, light and atmosphere** (`SkyRenderer`, `AtmosphereShaders`, `SkyClock`, `SkyXModel`, `KenshiHaze`, `KenshiLighting`,
  `AmbientMap`; facts in [formats/sky.md](formats/sky.md) and [formats/lighting.md](formats/lighting.md)): the sun follows the
  game's formula for the hour (latitude 54, sunrise 5, sunset 23). In game-sky mode (default) everything is in the game's own HDR
  units and numbers. The sky is SkyX's skydome evaluated per pixel with the game's options (wavelengths 0.57 / 0.48 / 0.44,
  exposure 1.4, 4 samples, HDR mode), the night glow, the game's starfield on its own dome mapping (or, with the Meitou `stars` switch, a procedural night sky: point stars turning about the sun's axis, [formats/sky.md](formats/sky.md) "Meitou night sky") and the game's two planets (Moon, Moon2: fixed low over +x −z, lit by the sun); no sun disc (the game has none in
  the dome; its sun is the Mie glow), with `--clouds` or a cloudy weather the game's SkyX cloud layer (with the Meitou `clouds` switch a procedural volumetric layer, lit by the sun or planet, with temporal filtering and reflected clouds, [render-clouds.md](render-clouds.md)). Terrain, objects and grass
  are lit by `kenshiLight`, the game's deferred lighting model: the sun colour taken from SkyX towards the sun the way the game's
  sky controller takes it, times the daylight factor and the per-biome ambient map's sun brightness; the image-based ambient
  from `mp_irradiance.dds` times the ambient map's colour; GGX sun specular and the `mp_specularity.dds` environment specular with
  the game's environment BRDF. No shadows (every face towards the sun is fully lit). Every world shader ends with
  `colour = atmoApply(colour, eye, position)`, the game's haze (sky.md, "Haze"). The post-processing applies the game's auto
  exposure from the measured mean luminance, so the screen brightness follows the game's `0.55 / adapted` with the CONSTANTS band.
  At night the game's haze is black and hides everything past a few thousand units against a black sky; the `night` switch ("Night air", Meitou, default) gives the
  air a faint glow of its own instead (`NightAir` in `NightSky.cs`: airglow plus the planet's light scattered by the air, by the planet's phase): the haze, its ramp the game's,
  fades the far land into it (`hazeTarget`), and the sky pass adds the same colour above the horizon, falling off with the height (`atmoNightAir` in `atmoSky`, replacing SkyX's own
  night glow as it comes in, under the stars and planets), so hazed ridges and the sky meet without a seam; it comes in through deep twilight (the planetshine's window)
  ([formats/sky.md](formats/sky.md), "Night air"; `--faithful night` for the game's black). The planets are drawn through it (`planet · (1 - veil) + air`: the dark side shows the glow, the foot fades into the horizon haze; Faithful and the day unchanged, [formats/sky.md](formats/sky.md) "Planets in the air"). The water's glint has its own light (`WorldLighting.Glint`), the planet's disc radiance
  at night, so it does not take the land's boosted planet light.
  The game's night land has only that flat ambient; the `planetshine` switch (Meitou, default; `--faithful planetshine` for the game's) makes the big planet the light
  once the sun is down: `Planetshine` in `NightSky.cs` gives its irradiance from its phase, size and mean albedo (times a strength, default 60), and
  `SkyRenderer.Prepare` publishes it in place of the sun's light (`uAtmoLight`, `uAtmoSunLight`, the `WorldLighting`, the shadows' direction), blended
  through twilight ([formats/sky.md](formats/sky.md), "Planetshine"); the sky and the exposure stay the game's. The same switch raises a dark biome's
  ambient-map colour at night, hue kept, to luminance 0.84 (D6D6DB, the common biome ambient; `SkyRenderer.NightAmbientFloor`, weighted
  `1 - saturate(5 sunY)`, sent as `uAtmoMaps.z - 1`): the swamp's 827A47 (0.47, sun brightness 0.72; **Verified** BIOMES records) otherwise reads
  near black at night with or without GI. Lit surfaces, water foam and the probes' sky rays all read the floored map.
  The "Ambient light strength" slider (ours, default 1) multiplies the environment factor (`uAtmoLight.w`, the `WorldLighting` ambients), so the flat ambient,
  the probes' sky rays and the GI resolve all scale; the sun's bounce does not. "Night ambient light strength" (ours, default 1 = 1.2x the game's, `SkyRenderer.NightAmbientBase`) multiplies it further,
  weighted `1 - saturate(5 sunY)` like the exposure's night floor. Auto exposure does not undo either at night while the mean scene luminance stays under
  that floor (0.28, so the scale is pinned at 0.55 / 0.28); by day, inside the 0.8..1.2 band, it largely does.
  New shaders include `AtmosphereShaders.Functions` (sky.md, "Using the atmosphere in a new shader"). The old colour model
  (`--simple-sky` / `B`) was removed.
  Cost: not re-measured after the 2026-10-05 rewrite (the sky's integral now runs per pixel, about 4 × 5 exponentials, instead of
  a table lookup; the haze runs the same 4-sample integral per pixel). Earlier figures (2026-10-04, table version): the sky pass
  0.06 to 0.09 ms GPU at 1280 × 720.
  With the Meitou `stars` switch at night (2026-10-10, 1920 × 1080, RTX 4070, minimum of five runs): the sky pass 0.34 ms (measured with the Milky Way, since removed) against 0.12 ms for the game's starfield texture (0.26 ms without the faint star layer, which an integrated GPU skips); by day the stars are skipped (0.11 ms).
