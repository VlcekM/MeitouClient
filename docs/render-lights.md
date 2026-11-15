# Lamps (the game's point and spot lights)

The data side, meaning what a LIGHT record holds and where the lights are placed, is in
[formats/lights.md](formats/lights.md). This page covers how the renderer draws them. The claims say where they come from: *from the code*,
*measured* (how), or *open*.

The game draws these lights in both looks, so this is not a Faithful / Meitou switch. The bench's `lights` A/B switch turns them off (the former `--lights off` option, which the parity gates used, was removed).

## What is drawn

*From the code* (`WorldLamps`, `LampShaders`):

  - Lanterns characters carry (`CharacterAppearance.Lights`, [lights](formats/lights.md#carried-lanterns)) are posed with the character each frame
    (`CharacterRenderer.CollectLamps`) and binned after the others as `WorldLamps.Moving`; they are night-only.
- **Binning.** Each frame, the lights whose reach overlaps a 64 × 64 grid of 128-unit cells around the eye (8192 units across, at most
  1024 lights) are written to three textures, one set per frame slot:
  - `uLightCells` (RG32F): each cell's first entry and count;
  - `uLightIndex` (R32F): the light numbers, 1024 per row;
  - `uLightData` (RGBA32F): a row of37 texels per light: position/radius; radiance/type; spot direction/outer cosine;
    inner cosine/falloff/room edge count/emitter id; room min/max height; up to32 inward footprint planes (normal X/Z, offset, 0).
    Edge count0 means an exterior lamp; unused room texels are ignored.

  `uLightGrid` holds the grid's corner, its cell size and its cells per side. Its w is 0 when there is nothing to draw, and then the
  shaders skip the lamps entirely.
- **Shading.** `kenshiLight` adds every light of the pixel's cell, lit the way the sun is: diffuse `π · N·L · radiance · 0.96` times the
  albedo, plus the same GGX specular as the sun's. Here `radiance = colour · power · Attenuation(d, radius) [· SpotFactor]`, with the
  game's attenuation and spot cone (`WorldLights.Attenuation` / `SpotFactor`, ported to GLSL). There are no shadows, as in the game, unless the [`lampshadows`](#traced-lamp-shadows) switch is on.
- **Native and legacy.** The legacy programs take the uniform and the three samplers by name. The native frame block has `lightGrid` at
  8608 and the three bindless indices at 8624, 8628 and 8632 (`FrameConstants` is 8640 bytes).
- **Probe GI** ([render-gi.md](render-gi.md)). The probe trace binds the same textures (bindings 10–12, `uLightGrid` in its parameters)
  and adds the lights' diffuse light at each hit (`LAMP_DIFFUSE_ONLY`), so lamp light bounces too. Interior lamps additionally use
  a ray from the hit towards the lamp; intervening geometry blocks the bounce source. The lamp's owning fixture is excluded from its
  own shadow query (light points may be embedded in it); all other geometry still occludes. Direct raster lamps retain the game's unshadowed
  shading within the building mask. Interiors keep probe lighting; there is no ambient override.

## Traced lamp shadows

- **Inert** without ray queries or storage for RGBA16F, without the surface target (`GiResolve` above 0, so the `gi` switch and probes on, not `--no-gi`), by day when no lamp is in the grid, and on Metal (`gpu.Post` is null there).
- **Which lamps.** The pass includes `lampLight` itself and defines `LAMP_VISIBILITY` to note each lamp's `radiance * diffuseNl`, so the selection has the world's attenuation, spot cone and room clip by construction. The strongest 2 by their light on the drawn plane (attenuation, cone and the plane from the depth: a normal's bumps must not change a pixel's lamps; KenshiNative's choice of 2 on cost) are traced; weaker lamps stay unshadowed.
- **A lamp's own fixture.** Every ray stops 15 units short of the lamp (KenshiNative's near-skip) and at its radius. Interior lamps also pass through their emitter id's geometry (`GeometryRecord.Emitter`), as the probe trace does. Exterior lamps have no id: their placement is often the whole building, whose walls should block them.
- **Specular stays.** Only diffuse is removed. The surface target has no gloss, so the GGX term cannot be recomputed there; a lamp's highlight survives in its shadow. A dielectric's lamp specular is small; carrying gloss in the target's spare bits (10-15) would touch the world programs.
- **Casters.** Rays see everything the TLAS holds (`TracedSunShaders.LampMask`): terrain, objects (their cut-out parts by their own maps), foliage of radius 150 or more within 4000 units of the eye, and, built while the pass runs (lamps in the grid), the small foliage (radius 6 and up within 3000) and the grass (within 1200, or the [traced sun](render-shadows.md#traced-sun)'s reach) as for the traced sun, and the characters. Grass, small foliage and characters are absent from the probes' rays (`GiMask`).

## Measured

- **Parity** (`image-diff`, 1280 × 720, `--view hub` and `--view swamp`, against `b84b372`):
  - with the lamps off (then `--lights off`): mean 0.0000 for both views;
  - with `--faithful gi` and the lamps off (then `--gi --faithful gi --lights off`): mean 0.0000 for both views;
  - with the lamps at noon: the Hub mean 0.0000, the swamp mean 0.03.
- **Cost** (RTX 4070, Mongrel at 22:00, 1920 × 1080 with DLSS 0.67, `--ab lights`): +0.07 ms GPU per frame.
- **Picture** (Mongrel at 22:00): small blue-white pools under the lamp posts, mean difference 0.38. The lights' radii are 60–300 units,
  so they are local.

## Open

- The lights of weather effects are not in the list (formats/lights.md).

## Lamp tiles

The still world's lamp shadows (`Gi/LampTiles`, console `lamps tiles on|off`): every lamp of a pixel is shadowed, with no limit on their number. Characters are not in the tiles.

- **Tiles.** Each lamp near the eye holds a distance tile traced from the lamp, in one atlas (8192², R16), and carried lamps a tile traced whole each frame (16 at most, first come).
- **Mapping.** A cone on its plane, a point lamp or a cone over 150° by octahedral directions. Side from reach and cone for about 1.25 units per texel at the end of the reach, halved past 1500 and 3500 units from the eye.
- **Invalidation.** `GiScene` keeps a hash per 128-unit cell of the object and foliage instances, so anything placed, removed or moved (a zone streamed in, a building built or ruined, a door) retraces the lamps whose reach holds that cell, 3 M rays a frame at most. An object that has moved in the last 60 frames is out of the tiles (instance mask `Moving`, what the probes and the sun still see) and in the per-pixel rays, so what keeps moving retraces nothing and a door's shadow follows it. A tile that must be retraced waits 2 frames between traces within 600 units of the eye, 16 beyond.
- **Penumbra.** The source radius comes from the reach (0.005 of it, 1 to 2.5 units; the LIGHT record has no size field, **Verified** in `fcs.def` and [lights](formats/lights.md)). A spiral finds the blockers, a second one filters; a lookup r texels off counts only what stands nearer the lamp by what the surface can gain over r texels (its plane's depth at each lookup swung with the normal at a graze, and drew the ripples and the terrain's facets into the penumbra, **Observed**). Coarse levels of nearest blockers were tried and dropped: they read a slope's own near edge as a blocker, and drew squares (**Observed**, spotlight-cross).
- **Traced light.** The probes, the GI cache and the traced reflections read a lamp's tile at their hits, one filtered lookup: every lamp respects the still world in the bounce. A lamp with no tile keeps `giLampVisibility` (a ray for an interior lamp, none for the others).
