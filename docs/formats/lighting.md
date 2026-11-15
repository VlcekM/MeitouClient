# Scene lighting

Original deferred.hlsl/lighting materials, SkyX/exe setters, ambient map and DDS research.
Field/formula tables retained; [sky](sky.md), [shadows](shadows.md), [lights](lights.md),
[post](post-processing.md). Engine GI is separate from original fixed ambient.

## BRDF and inputs

**Verified** fullscreen deferred sun/ambient; separate point/spot passes. GBuffer YCoCg albedo,
checkerboard chroma, metalness/gloss/normal, translucency/emissive and depth. Unclamped HDR.
Dielectric F0=0.04; metal uses albedo for specular. Diffuse/sun/environment formula block below;
GGX alpha=roughness², roughness=1-0.99gloss, Schlick-like exponential Fresnel and visibility.
Specular cube mip7*(1-gloss), dominant-direction blend, RGB*alpha*10 and analytic environment BRDF.
Irradiance cube16² BC3/5mips reads mip3, RGB*alpha*4; specularity256² BC3/9mips. World cube-face
orientation not confirmed. Ambient map RGB=biome ambient, alpha=trunc(clamp(sun-brightness,0,2)*127.5);
1024² covers±147456 with camera-origin-adjusted mapping. Missing biome white/0x80.

**Verified** real-sun sky colour at direction+(0,0,0.04), /5.6, black belowY=-0.2.
SunDirection lighting clampsY>=0 then normalizes; daylight sun.w differs from real-sun direction.
Ambient factorclamp(5L.y+0.2,0.1,1) bottoms0.2 due clamp, not0.1. Ambient/env shared defaults
have no found setters; only read components apply. Shadows multiply sun terms, not ambient.
Exposure band/composite is authoritative in post reference. [render-sky](../render-sky.md).

### The main lighting pass (Verified: `deferred.material` `Main_Lighting_RTW`, `deferred.hlsl` `main_fs`) — layout/example

```
sun     = sunColour.rgb · sunColour.w · ambientParams.w · ambientMap.a · 2
diffuse = π · max(N·L, 0) · sun · shadow · (1 − 0.04)                       (translucent pixels also take light from behind)
envDiff = irradiance(N) · (1 − 0.04) · ambientMap.rgb · envColour.w · clamp(5 L.y + 0.2, 0.1, 1)
colour  = albedo · (diffuse + envDiff) + sunSpecular + envSpecular + albedo · emissive
```

### The irradiance and specular cubes (Verified: the files, `DdsReader`)

| Face | rgb · a · 4 |
|---|---|
| +X | (1.07, 1.21, 1.50) |
| −X | (1.01, 1.15, 1.46) |
| +Y (up) | (1.09, 1.20, 1.51) |
| −Y (down) | (1.11, 1.05, 0.96) |
| +Z | (1.33, 1.45, 1.80) |
| −Z | (1.19, 1.39, 1.79) |

## Expression reference

- The main lighting pass: ` sun = sunColour.rgb · sunColour.w · ambientParams.w · ambientMap.a · 2 diffuse = π · max(N·L, 0) · sun · shadow · (1 − 0.04) (translucent pixels also take light from behind) envDiff = irradiance(N) · (1 − 0.04) · ambientMap.rgb · envColour.w · clamp(5 L.y + 0.2, 0.1, 1) colour = albedo · (diffuse + envDiff) + sunSpecular + envSpecular + albedo · emissive `
- `sunColour`: `sunColour.rgb = getColorAt(sunDir + (0, 0, 0.04)) / (4 · exposure)`; `y = −0.2`; `nightmult = saturate(1 − 10 · max channel)`; `w = 0.5 c + (c − 0.5 c) · sunY`; `w = 0.5 c − 0.5 c · 10.8 · sunY`
- `ambientParams`, `envColour`: `ambientParams.w = 1`; `envColour.w = 1`
- The ambient map: `mapCoord = (world.xz + worldOffset.xz) · worldSize.xy + worldSize.zw`; `worldSize = (1 / 294912, 1 / 294912, 0.5, 0.5)`
- Exposure: `MIN_LUMINANCE = exposure min · lerp(night darkness, 1, saturate(5 · sunY))`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- The main lighting pass (Verified: `deferred.material` `Main_Lighting_RTW`, `deferred.hlsl` `main_fs`)
- `sunColour` (Verified: exe sky update, `SkyX_x64.dll`)
- `ambientParams`, `envColour` (Verified: no setter)
- The ambient map (Verified: exe builder, against `data/newland/land/overlaymaps/ambientmap.png`)
- The irradiance and specular cubes (Verified: the files, `DdsReader`)
- Exposure (Verified: exe sky update and sky creation, `post/hdrfp4.hlsl`)
