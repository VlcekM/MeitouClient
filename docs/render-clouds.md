# Volumetric clouds

Meitou `clouds`: `VolumetricClouds`, `VolumetricCloudShaders`, `VolumetricCloudRenderer`,
`SkyClouds`. Faithful retains [original planar clouds](formats/clouds.md);
`cloudshadows` is independent. Direct sky callers without a volume use the lit-layer fallback.

## Density and light

Half resolution;96 samples (integrated64), step<=500, transmission early-out<1%, slab-distance
cap500000. Shallow rays only integrate first48000/32000 units. Beer-Lambert extinction,
four key-light samples, forward/back lobes and approximate multiple scattering. Sun/planet key
and sky ambient feed HDR; source ceiling8, haze scale65000. Premultiplied colour/opacity composites
over atmosphere/stars/planets before weather fog, with geometry drawn over the sky.

## Limits and verification

Free camera inside/above slab does not occlude foreground geometry with clouds. Short shallow
march interval can omit distant clouds. Fog/haze/overcast tuning is Meitou appearance, not proven
original parity. Test horizon, sunset/night, rainy colour, clock/region cuts, reflection and
above-cloud views; compare temporal baseline noise. [Weather](formats/weather.md), [bench](bench.md).
