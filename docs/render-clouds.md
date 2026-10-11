# Volumetric clouds

Meitou rendering choices, implemented in `VolumetricClouds`, `VolumetricCloudShaders`,
`VolumetricCloudRenderer` and `SkyRenderer.Clouds.cs`. Original-game facts remain in
[formats/clouds.md](formats/clouds.md). The game's single planar layer is retained by `--faithful clouds`;
`--faithful cloudshadows` independently disables ground shading. Meitou's existing `clouds` switch now
selects the volumetric renderer. The former lit texture layer remains the fallback for direct sky callers
that have not prepared a volume pass.

## Density and weather

- **Verified (tests)**: the slab lies at 12000..19000 world units, above `WorldLayout.MaxHeight`.
  `VolumetricCloudsTests` checks intersections from below, inside and above, parallel rays and the distance cap.
- **Engine choice**: generated, periodic 64-cubed noise, packed into an 8x8 atlas of 66-square padded slices.
  R holds four-octave smooth shape noise, G cellular erosion and B broad weather noise. Two filtered atlas
  reads provide trilinear sampling without introducing a 3D texture API or shipping game-derived assets.
  **Verified (tests)**: periodic noise at negative coordinates and each slice's wrapped borders.
- **Engine choice**: broad weather cells modulate coverage; a vertical profile shapes cloud bases and tops,
  with finer cellular erosion at the edges. Above density 0.75 coverage fills towards overcast. The mapping
  differs from the game's texture-alpha distribution. Existing WEATHER density, sky colour,
  wind, transitions and the `--clouds` override remain the inputs; no new FCS fields are required.
- **Engine choice**: wind uses the existing wrapped accumulated texture shift, scaled by 120000 world units
  (six times the weather wind, as with the previous cloud layer). Every density frequency divides this
  120000-unit period, so wrapping the wind does not jump the field (tested). Pause holds it stationary.

## Light and compositing

- **Engine choice**: half resolution in each dimension, up to 96 samples (64 on integrated GPUs), with
  steps capped at 500 units and early termination below 1% transmission. The cap limits long shallow rays
  to the first 48,000 units of their slab interval (32,000 on integrated GPUs), preventing coarse horizon
  samples; distant clouds beyond that interval are omitted. A 500,000-unit distance cap bounds the slab hit.
- **Engine choice**: Beer-Lambert extinction, four samples towards the key light, forward and backward
  scattering lobes and a broad multiple-scattering approximation. The existing sun/planet key and sky
  ambient provide HDR lighting. Cloud colour fades towards the same haze target used by the land (including the weather cloud colour),
  blending towards the sky target above the camera altitude band, with a 65000-unit scale. The
  source radiance is limited to the former lit-layer ceiling (8), preserving its hue.
- **Engine choice**: jittered samples accumulated in independent main-view and reflection histories,
  reprojected using the opacity-weighted distance and wind displacement. A 3x3 neighbourhood clips history;
  large changes in coverage, eye position/direction, light, ambient, size or frame continuity reset it. This is an approximate
  volume reprojection, not exact motion at every depth. Cloud filtering works even with the scene upscaler off.
- The half-resolution result is premultiplied colour plus opacity, composited over the atmosphere, stars
  and planets before the existing weather fog. Reflection preparation uses the mirrored eye and matrix.
  Geometry remains drawn over the sky: the game's camera stays below this layer; a free viewer above
  or inside it does not yet receive cloud occlusion over foreground terrain and objects.

## Shadows

- **Engine choice**: a 512-square transmission map spans 320000 world units around the eye, snapped to
  map texels. Rays start at the cloud base and sample the same density towards the real sun, omitting fine erosion
  for the coarse lighting integration (also used for cloud self-shadowing). World lighting
  projects its sun ray onto the base, then reads one filtered map sample. Maximum strength and low-sun fade
  remain `MeitouClouds.ShadowAt`. Outside the map, coverage supplies a uniform overcast approximation;
  points above the cloud base receive no cloud shadow. Night cloud shadows are disabled.
- Faithful clouds retain the previous texture shadow model when cloud shadows are enabled separately.
  The volume's map is generated before world and reflection shading. GI ray lighting and light shafts
  do not yet trace or sample this cloud shadow map.

## Verification and cost

- **Verified (tests)**: `VolumetricCloudNativeTests` renders without game assets and detects a visible
  difference from a clear sky. It exercises reflection and main histories, resizing, clear weather,
  Faithful toggling, shadow toggling and disposal under Vulkan synchronisation validation.
  The three shader pairs are covered by `ShaderCompilerTests`; the seven sky/world shader snapshots
  affected by the new composite and shadow lookup were refreshed explicitly. The remaining 22 snapshots are unchanged.
- **Observed (2026-10-11, RTX 4070)**: The Hub, 1280x720, distance 9000, pitch 5, yaw 300, hour 20.5,
  coverage 0.5, scene upscaler off, foliage/water/particles off: main cloud pass 1.15 ms and shadow map
  0.02 ms on the last available timestamp after eleven screenshot frames. These are individual pass
  samples, not a sustained benchmark. The sky cost line reports main clouds, reflection clouds and
  cloud shadows separately from the original sky pass.
- **Observed**: the viewer reports zero rendering validation errors but a single image-view leak during
  device destruction, also reproduced with Faithful clouds and cloud shadows (the volume is never created).
  The isolated cloud GPU test reports zero validation errors through resource cleanup.
- **Unknown**: sustained 1080p/1440p cost, integrated GPU performance and temporal artefacts during fast
  flight. Horizon rays and cloud edges warrant further visual tuning across weather conditions.

**Observed**, same view at hour 22.5 after correcting the directional haze blend: cloud pass 0.69 ms, shadow map 0.02 ms;
clouds occlude the planet and take on the warm dusk light. Hour 1: planet-lit cloud silhouettes cover stars and the planet,
and the water reflection path renders without validation errors. These screenshot checks are not a motion-stability benchmark.


**Verified (2026-10-11)**: final Release build has zero warnings/errors; the final validation run passed 1,010 tests
(the quick suite plus the cloud GPU lifecycle test). The focused shader/volume run passed 48 tests before the additional wind-wrap regression.
The full Release run passed 1,312 of 1,313 tests; only the shore-field timing benchmark failed under the full workload,
and it passed when rerun alone. No game assets or generated images are stored in the repository.

**Observed**, final Release noon screenshot after the wind-period fix, same camera and size, sync validation enabled:
main clouds 4.48 ms, shadow map 0.17 ms, sky composite 0.48 ms; zero rendering validation errors. This higher sample
underlines that the earlier screenshot timings are not a sustained performance budget. A controlled moving-camera
benchmark is still needed before claiming a fixed cloud cost.

## Horizon haze and weather regressions (2026-10-11)

- **Observed**: the initial volume replaced the planar clouds' horizon band without replacing its haze
  matching. Far terrain was still fogged, but its silhouettes stood out against a different sky colour.
- **Engine choice**: near the horizon, the volume composite now blends towards the land's haze target,
  including the existing weather cloud colour. The band is full below direction y 0.05 and fades out by
  0.15, respects haze strength, and fades out with the viewer's high-altitude physical haze. It comes in
  continuously with low cloud density. The regular weather fog remains applied once after the composite.
- **Verified (GPU regression)**: at haze strength 1, the volume sky within 2.3 degrees of the horizon
  matches a separate fully hazed terrain shader reference within 1/255, with no game assets needed.
- **Observed**: the interactive launch used `--clouds 0.5`, an intentional debug override which takes
  precedence over the scheduler's cloud density. This held coverage constant across clear and storm weather.
  Restarting without `--clouds` restores the existing weather scheduler's density and 30-second transitions;
  no change to WEATHER/FCS semantics or override precedence is needed.
- **Verified (GPU regression)**: changes in weather cloud density change the rendered sky; zero density
  clears it. An explicit coverage override wins, and clearing the override returns the same clear image.
  With no scheduler input, the selected `SkyWeather` record supplies the density as before.
**Verified (GPU regression)**: dense weather fog covers the volumetric and Faithful sky with byte-identical pixels.
**Observed**: a distant-horizon screenshot under the real "light rain" WEATHER record, without --clouds, renders clouds
and haze with zero rendering validation errors. Release build and the 1,012-check quick/cloud/shader run passed;
the additional dense-fog comparison also passed independently.

## Rainy overcast colour strip (2026-10-11)

- **Observed**: reproduced at camera `01FB34ECC7D7E8F344AC8C0CC6C82233C1CCD49D3C00A00C46`,
  `Kenshi_red_rain`, 13:00, with both water and particles disabled. The volume misses the lowest rays at
  its finite distance cap; the previous horizon blend retained 7% of the underlying sky at Meitou's 0.93
  haze strength. A blue-green strip remained under otherwise opaque, dark overcast clouds.
- **Engine choice**: horizon closure is now the greater of haze strength and the existing weather-driven
  horizon cloud alpha. Full overcast closes the sky completely, as the base layer does; partial cloud cover
  and the land's configured haze strength are retained. The fix is confined to the Meitou sky composite.
- **Verified (GPU regression)**: the new full-overcast check failed before the fix (expected first pixel
  RGB 30/36/38, actual 41/51/54) and passed after it. It compares the overcast horizon at haze strengths 1
  and 0.93. Clear weather, density overrides, the terrain-haze match and dense weather fog remain covered.
- **Observed**: the before/after rainy screenshots at the supplied camera show the strip removed; Vulkan
  rendering validation reports zero errors. The Release build and 1,012-check validation run pass.