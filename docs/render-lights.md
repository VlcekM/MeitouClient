# Lamps (the game's point and spot lights)

The data side, meaning what a LIGHT record holds and where the lights are placed, is in
[formats/lights.md](formats/lights.md). This page covers how the renderer draws them. The claims say where they come from: *from the code*,
*measured* (how), or *open*.

The game draws these lights in both looks, so this is not a Faithful / Meitou switch. The bench's `lights` A/B switch turns them off (the former `--lights off` option, which the parity gates used, was removed).

## What is drawn

*From the code* (`WorldLamps`, `LampShaders`):

- **Which lights.** The lights of every placed building and its exterior/interior layouts (`WorldLights.ForWorld`), read on a worker at start, plus the lights of buildings placed during play while they are complete (`WorldLamps.SetRuntime`, from the host's building diff, [simulation](simulation.md#buildings); placed as the world's, no interior layouts). The game switches a building's lights on when it completes ([construction tick](game/buildings-production.md#construction-tick)).
  Interior lamps enter the grid only with a loaded owner mask (matched by the building prefix of their placement id). Their direct light is
  clipped to its convex footprint and height range with a0.5-unit surface tolerance (see [zones](formats/zones.md#interior-masks)).
  Masks above32 edges are unsupported; direct lamps have no occluder shadows within that hull.
  - Night-only lamps (`WorldLightRule.Night`: every building not indoors, [lights](formats/lights.md#when-a-light-is-on)) are left out while the daylight factor is above 0.1;
    the rest are always on. The `interiorlamps` switch (Meitou, default) keeps interior-layout lamps on by day too; Faithful: night only.
    Player lamps placed during play are not interior lamps. A placed building's lamps are scaled by its power share (`SetRuntimePower`) and left out at 0. World lamps
    take no power factor: the town buildings' power and on state are **Unknown** ([lights](formats/lights.md#when-a-light-is-on)).
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
  - Pulsing and shimmering lights get the game's effect factor on the CPU, from a clock in seconds × 60. The unit of the game's clock
    value is **Unknown**.
- **Native and legacy.** The legacy programs take the uniform and the three samplers by name. The native frame block has `lightGrid` at
  8608 and the three bindless indices at 8624, 8628 and 8632 (`FrameConstants` is 8640 bytes).
- **Probe GI** ([render-gi.md](render-gi.md)). The probe trace binds the same textures (bindings 10–12, `uLightGrid` in its parameters)
  and adds the lights' diffuse light at each hit (`LAMP_DIFFUSE_ONLY`), so lamp light bounces too. Interior lamps additionally use
  a ray from the hit towards the lamp; intervening geometry blocks the bounce source. The lamp's owning fixture is excluded from its
  own shadow query (light points may be embedded in it); all other geometry still occludes. Direct raster lamps retain the game's unshadowed
  shading within the building mask. Interiors keep probe lighting; there is no ambient override.

## Traced lamp shadows

The `lampshadows` switch, off by default and in no look (`PostOptions.LampShadows`, `PostProcess.LampShadows.cs`, `Gi/LampShadowPass.cs`, `Gi/LampShadowShaders.cs`).
The game has none: this is Meitou's. Port of KenshiNative's traced lamp shadows and its disc sampling (`knLampSample`, near-skip, lamp radius), adapted from a term in the light shader to a pass over the forward scene.
*From the code*, none of it run on a GPU: look and cost are **Unknown**.

- **Pass.** After the GI resolve, before the shafts and fog volumes: no world shader changes, nothing runs or is allocated while the switch is off. A compute pass writes, per pixel of the render size, the diffuse light the blocked lamps gave per unit of albedo. A full-screen pass takes that, times the stored albedo and the haze's transmittance, off the scene colour (reverse-subtract blend), then a max blend with 0 keeps the scene above black, since the albedo is stored rounded. No spatial filter and no history of its own.
- **Inert** without ray queries or storage for RGBA16F, without the surface target (`GiResolve` above 0, so the `gi` switch and probes on, not `--no-gi`), by day when no lamp is in the grid, and on Metal (`gpu.Post` is null there).
- **Which lamps.** The pass includes `lampLight` itself and defines `LAMP_VISIBILITY` to note each lamp's `radiance * diffuseNl`, so the selection has the world's attenuation, spot cone and room clip by construction. The strongest `--lamp-shadow-lamps` (2 to 4, default 2, KenshiNative's choice on cost) are traced; weaker lamps stay unshadowed.
- **The ray.** One per lamp, from the surface raised along its normal by at least 0.5 units or two pixels' width, to a point of a 1-unit disc facing the surface: four sectors over each 2 x 2 block, turned by the R2 sequence per block and per lamp, moving over 16 frames under a temporal upscaler and fixed otherwise. Rays go through the frame's TLAS and pass cut-out texels (`solid`), like the probes'. Lamp data is this frame's, so carried lanterns are traced where they are drawn.
- **A lamp's own fixture.** Every ray stops 15 units short of the lamp (KenshiNative's near-skip) and at its radius. Interior lamps also pass through their emitter id's geometry (`GeometryRecord.Emitter`), as the probe trace does. Exterior lamps have no id: their placement is often the whole building, whose walls should block them.
- **Specular stays.** Only diffuse is removed. The surface target has no gloss, so the GGX term cannot be recomputed there; a lamp's highlight survives in its shadow. A dielectric's lamp specular is small; carrying gloss in the target's spare bits (10-15) would touch the world programs.
- **Not covered.** Foliage, grass, characters, impostors and water write 0 to the surface target, so they keep unshadowed lamp light (interior surfaces too with `interiorgi` Faithful). Only what the TLAS holds casts: terrain, objects, foliage of radius 150 or more within 4000 units of the eye; not characters or grass. The TLAS's meshes and 16-unit terrain grid are coarser than what is drawn: **Unknown** whether grazing rays show acne or leaks.

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
- The Large Torch Post's light may sit about 25 units above its flame (scale applied twice, as in the exe); not checked in a picture yet.
