# Volumetric clouds

Meitou `clouds`: `VolumetricClouds`, `VolumetricCloudShaders`, `VolumetricCloudRenderer`,
`SkyRenderer.Clouds.cs`. Faithful retains [original planar clouds](formats/clouds.md);
`cloudshadows` is independent. Direct sky callers without a volume use the lit-layer fallback.

## Density and light

**Verified tests:** slab12000..19000, intersections below/inside/above/parallel, distance cap;
periodic64³ noise packed8x8 padded66² slices, negative coordinates and borders. R shape/G erosion/
B weather noise; two atlas reads interpolate slices. Weather density/sky colour/wind/transitions
are original inputs; engine coverage differs (overcast above0.75). Wind shift period120000,
frequencies divide it; wrapping cannot jump density, pause freezes motion.

Half resolution;96 samples (integrated64), step<=500, transmission early-out<1%, slab-distance
cap500000. Shallow rays only integrate first48000/32000 units. Beer-Lambert extinction,
four key-light samples, forward/back lobes and approximate multiple scattering. Sun/planet key
and sky ambient feed HDR; source ceiling8, haze scale65000. Premultiplied colour/opacity composites
over atmosphere/stars/planets before weather fog, with geometry drawn over the sky.

## Temporal, fog and shadows

Independent main/reflection histories reproject opacity-weighted distance and wind;3x3 clips
history. Reset on coverage/camera/light/ambient/size/continuity changes. Mirrored-eye preparation
is required for reflection. Reprojection approximates a volume, not motion at every depth.
Upscaler motion: the velocity pass gives sky pixels the far plane's motion (the turn alone); a cloud pixel (`UpscaleShaders.CloudMotion`)
takes its opacity-weighted point's parallax through the slice's reprojection plus the wind's shift, blended by opacity (x2.5) into the
motion target after the history resolve (Vulkan chain only). Whether this ends the DLSS streaks on clouds in play is **Unknown** (offscreen
orbits move the camera too little to show either); the pass is **Observed** to write (a forced motion moves the sky).
Fog-hidden rays can skip marching; full-sky skip requires every direction conservatively hidden.
Clock rewind/weather changes must refresh sky/fog histories and not retain stale colour.

Cloud shadows:512² map over320000 units, texel-snapped, integrates density toward real sun.
World receivers project to cloud base and sample transmission. Coarse integration omits erosion;
outside map uses overcast approximation; above-base points/night receive no shadow. Strength
scales by the eye's distance outside the nearest placed fog block /2000 (0 inside; largest
plane distance, spheres/beams ignored; Meitou `cloudfog` switch), else the swamp/Deadlands get too dark. Prepare
map before world/reflection shading. GI rays and shafts do not yet sample this map.

## Limits and verification

Free camera inside/above slab does not occlude foreground geometry with clouds. Short shallow
march interval can omit distant clouds. Fog/haze/overcast tuning is Meitou appearance, not proven
original parity. Test horizon, sunset/night, rainy colour, clock/region cuts, reflection and
above-cloud views; compare temporal baseline noise. [Weather](formats/weather.md), [bench](bench.md).
