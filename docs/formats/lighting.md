# Scene lighting

How Kenshi lights the world it has drawn into the G-buffer: the sun, the ambient (image-based) light, the per-biome
ambient map, and the exposure that turns the HDR result into the screen image. The sky itself is in [sky.md](sky.md), the
render order and the composite in [post-processing.md](post-processing.md). Labels as in [../README.md](../README.md).

Sources: the shipped shaders `data/materials/deferred/deferred.hlsl`, `deferred.material`, `gbuffer.hlsl`,
`common/lightingFunctions.hlsl`, `common/common.program` (read for facts only), `kenshi_x64.exe` (the sky controller's
update, its creation, the ambient map builder, the deferred shared-parameter update) and `SkyX_x64.dll`
(`AtmosphereManager::getColorAt`, `SkyX::setLightingMode`), decompiled outside the repository on 2026-10-04.

## The main lighting pass (Verified: `deferred.material` `Main_Lighting_RTW`, `deferred.hlsl` `main_fs`)

One full-screen pass computes the sun and the ambient light for every G-buffer pixel; point and spot lights are added by
separate passes (`DeferredLight`). Inputs per pixel: albedo (stored as YCoCg with the chroma checkerboarded over two pixels and
rebuilt with an edge filter: unscaled, 8 bits), metalness, gloss, normal, a translucency/emissive byte, the depth. Per frame:
`sunDirection`, `sunColour`, `envColour`, `ambientParams`, `worldSize` / `worldOffset`, and the textures `ambientmap.png`, the
cube maps `mp_irradiance.dds` (diffuse) and `mp_specularity.dds` (specular), and the shadow map.

For a dielectric (metalness 0) the result is

```
sun     = sunColour.rgb · sunColour.w · ambientParams.w · ambientMap.a · 2
diffuse = π · max(N·L, 0) · sun · shadow · (1 − 0.04)                       (translucent pixels also take light from behind)
envDiff = irradiance(N) · (1 − 0.04) · ambientMap.rgb · envColour.w · clamp(5 L.y + 0.2, 0.1, 1)
colour  = albedo · (diffuse + envDiff) + sunSpecular + envSpecular + albedo · emissive
```

- `1 − 0.04`: the diffuse share left by a dielectric's specular colour 0.04 (`1 − mean(specColour)`); a metal's albedo goes to
  its specular colour instead.
- `L` is `sunDirection`, towards the sun. The sky controller sets it from SkyX's sun direction with **y clamped to 0** and
  renormalised (**Verified**, exe sky update), so at night the sun "lies on the horizon": `N·L` still lights faces that look
  towards it, but `sun` is zero by then (below), and the ambient factor `clamp(5 L.y + 0.2, 0.1, 1)` bottoms out at **0.2**, not
  0.1. It is 1 from a sun height of 0.16 (9 degrees) up.
- `irradiance(N)`: `mp_irradiance.dds` looked up in the normal's direction at mip level 3, `rgb · a · 4`.
- Sun specular: a GGX lobe (`D = α² / (π (cos²θh (α² − 1) + 1)²)`, `α = roughness²`, `roughness = 1 − 0.99 · gloss`), a Fresnel
  term `F0 + (1 − F0) · 2^((−5.55473 c − 6.98316) c)` with `c = L·H`, a visibility `1 / (c² (1 − k²) + k²)` with `k = α / 2`, all
  times `N·L` and the light colour, divided by π.
- Environment specular: `mp_specularity.dds` at mip `7 (1 − gloss)` along a direction between the normal and the reflected
  view (Frostbite's "dominant direction", `lerp(N, R, gloss (√gloss + roughness))`), `rgb · a · 10`, times Lazarov's analytic
  environment BRDF for the gloss and `N·V`, then times `ambientMap.rgb · envColour.w · clamp(5 L.y + 0.2, 0.1, 1)` like the
  diffuse part.
- The result goes into the HDR target (`R11G11B10_FLOAT`) unclamped; the atmosphere haze is blended over it afterwards
  ([sky.md](sky.md#haze-distance-fog-how-vanilla-does-it)).

### `sunColour` (Verified: exe sky update, `SkyX_x64.dll`)

Each frame the sky controller asks SkyX for its colour in a direction next to the sun and divides it down:

- `sunColour.rgb = getColorAt(sunDir + (0, 0, 0.04)) / (4 · exposure)` with SkyX's exposure 1.4, so `/ 5.6`. The offset is a fixed
  +0.04 in world Z (`getColorAt` normalises the direction), about 2.3 degrees from the sun, so the colour includes much of
  the Mie glow around the sun. When the sun is lower than `y = −0.2` the colour is (0, 0, 0).
- `getColorAt` is SkyX's CPU copy of the skydome shader: the same 4-sample O'Neil integral from SkyX's fixed camera
  (`inner + 0.01 · thickness`), with the game's options. In HDR mode, which the game selects (`setLightingMode(1)`; mode 0 is the
  `1 − exp(−exposure · x)` LDR form), it returns `exposure · (rayleighPhase · Rayleigh + miePhase · Mie)` plus the night glow
  `nightmult · ((0.05, 0.05, 0.1) (2 − 0.75 saturate(−sunY)) (1 − dirY)³)^2.2`, `nightmult = saturate(1 − 10 · max channel)`.
- `sunColour.w` is a daylight scale: with `c` a sky controller value (1.0, set when the sky is created; no other writer found
  in the sky code, **Observed**), `w = 0.5 c + (c − 0.5 c) · sunY` while the sun is up (0.5 at the horizon, 1 at the zenith),
  `w = 0.5 c − 0.5 c · 10.8 · sunY` (sunY negative) below, i.e. `0.5 + 5.4 sunY`, clamped at 0: gone when the sun is 0.093
  (5.3 degrees) under the horizon.
- So the sun's light is `sunColour.rgb · w · ambientMap.a · 2`, and a white surface facing the sun reflects
  `π · 0.96 ·` that.

### `ambientParams`, `envColour` (Verified: no setter)

`common.program`'s `AmbientParams` defaults: `ambientParams (0.89, 0.66, 0.16, 1)`, `envColour (1, 1, 1, 1)`. Neither name occurs
in `kenshi_x64.exe` or any DLL of the install (byte search for the strings), and the exe's other writes to the `AmbientParams`
block are `worldSize` (below), `zenithLight` and `nadirLight` (the sky update; read only by `SkyX_Clouds.hlsl`). So in the
lighting pass `ambientParams.w = 1` and `envColour.w = 1`; `envColour.rgb`, `groundLight` and `ambientParams.xyz` are not read
by `main_fs`.

### The ambient map (Verified: exe builder, against `data/newland/land/overlaymaps/ambientmap.png`)

`ambientmap.png` is built by the game from the biome map and the BIOMES records, an image named `ambientmap` in
`data/newland/land/overlaymaps`: for each pixel of the biome map (sampled down to the ambient map's size) the BIOMES record with that
`index` colour gives

- **rgb** = the record's `ambient light` (an int, 0xRRGGBB);
- **alpha** = `sun brightness` clamped to 0..2, × 0.5 × 255, truncated to a byte; the shader's `ambientMap.a · 2` gives the sun
  brightness back (1 → 127 → 0.996);
- a biome-map colour without a record: white, alpha 0x80.

The shipped `ambientmap.png` (1024², RGBA) matches: its 14 colours are exactly the records' (`D6D6DB` / alpha 0x7F the most
common, `D6D6DB` / 0x30 for Ashlands' `sun brightness` 0.38, `827A47` / 0x5B for the swamps' 0.72, `FFFFFF` / 0xA8 for Serpentine
Wastes' 1.32, ...). Merged load order values: most biomes have `ambient light` `FFFFFF` or `D6D6DB` and `sun brightness` 1;
the swamps `827A47` (a dark olive), Skinner's Roam `A19EA3`, Artery `BEBEBE`.

The texture covers the world: `mapCoord = (world.xz + worldOffset.xz) · worldSize.xy + worldSize.zw` with
`worldSize = (1 / 294912, 1 / 294912, 0.5, 0.5)` adjusted for the camera-relative origin, i.e. the square ±147456 (**Verified**,
the deferred shared-parameter update). Clamped, filtered (Ogre's default bilinear; **Observed**, the material sets no filtering).

### The irradiance and specular cubes (Verified: the files, `DdsReader`)

`mp_irradiance.dds`: a 16² BC3 cube with 5 mips. It is a fixed environment: it does not change with the time of day (only the
`clamp(5 L.y + 0.2, 0.1, 1)` factor and the biome's ambient colour scale it). `rgb · a · 4` per face, averaged at mip 3 (2² texels):

| Face | rgb · a · 4 |
|---|---|
| +X | (1.07, 1.21, 1.50) |
| −X | (1.01, 1.15, 1.46) |
| +Y (up) | (1.09, 1.20, 1.51) |
| −Y (down) | (1.11, 1.05, 0.96) |
| +Z | (1.33, 1.45, 1.80) |
| −Z | (1.19, 1.39, 1.79) |

So a surface facing up gets about (1.05, 1.15, 1.45) of blue-white ambient light (× 0.96), a surface facing down a warmer
ground bounce. `mp_specularity.dds`: 256² BC3 cube, 9 mips. Which way the cube's faces are oriented in Kenshi's world (D3D's
cube convention against Ogre's right-handed world) is **Unknown**; the faces differ by at most 25%.

## Exposure (Verified: exe sky update and sky creation, `post/hdrfp4.hlsl`)

The composite scales the HDR image by `0.55 / adapted`, `adapted` the scene's mean Rec. 601 luminance (linear) smoothed
over about 2 s and clamped to `[MIN_LUMINANCE, MAX_LUMINANCE]` ([post-processing.md](post-processing.md#exposure-and-tone-mapping-verified)).
The exe sets both: `MAX_LUMINANCE` = CONSTANTS `exposure max` (1.2 in GLOBAL CONSTANTS after all mods, the same in the base game's own load order; Verified by loading both) once at creation, and every frame
`MIN_LUMINANCE = exposure min · lerp(night darkness, 1, saturate(5 · sunY))` with `exposure min` 0.8 and `night darkness` 0.35
(sunY: the sun's real height). By day the band is 0.8..1.2, a scale of ×0.69 to ×0.46; at night it opens down to 0.28, up to
×1.96, which is what keeps the night readable. So **`night darkness` is the night's exposure floor** (resolves an Unknown in
[sky.md](sky.md)).

## What the viewer does

See [viewer.md](../viewer.md#atmosphere): the world view lights in these units (sun, irradiance cube, ambient map, the same
GGX and environment terms with the viewer's gloss), draws the sky with SkyX's own formula, and applies the game's exposure from
the measured mean luminance. Not reproduced: shadows (every surface facing the sun is fully lit, so the viewer's scenes are
somewhat brighter on average than the game's and its auto exposure sits lower), the temporal adaptation (screenshots use the
steady state), point lights, translucency, and dust on objects.
