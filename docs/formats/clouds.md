# Original cloud layer

SkyX_x64.dll/controller and SkyX_Clouds.hlsl research; options/evidence below. Original is one
planar texture layer, not volumetric clouds or ground shadows. Meitou alternatives:
[render-clouds](../render-clouds.md), [weather](weather.md), [sky](sky.md).

## Plane, weather and fragment

**Verified** layer options height100,scale0.001,density-mult3; table retains struct offsets/constants.
Cloud density c and sky multiplier transition30 game-speed seconds; teleport snaps, c clamps0..1.
DensityOffset=1.4c-0.8, Darkness=pow(clamp(c-0.5,0,1),0.3). Wind parameter is accumulated offset,
not direction; integrate region velocity*dt, shader shift=offset*0.00005. Paused drift stops.

**Verified HLSL** direction d hits plane at d*height/d.y, UV0.1*d.xz/d.y. D1=saturate((Clouds.r+o)*3),
normal=negated decoded CloudsNormal with Y/Z swap. Bend ray by displacement*d.y*normal.xz and
raise plane byheight*(1-D1)*height-volume*d.y. Resample D=(Clouds.r(shiftedUV+(0.2,0.6))+o)*3
+CloudsTile.r(opposite-shift)*0.1, not saturated. Colour=zenithLight+sunRGB*(1-0.1D).
h=saturate(10*saturate(d.y-0.05)), D'=D+h; darken by1-saturate(D')*Darkness.
a=D'*saturate(1-tile+o); alpha=lerp(o+0.5,a,h). Horizon alpha=o+0.5 matches haze.
HDR output=saturate(colour)*sqrt(1.4), saturated alpha, blended over dome/planets.

## Compatibility and extensions

Normal map deforms sampling but does not light the layer. Declared sun/ambient parameters and
some options are not used; do not implement unused shader inputs as proven original features.
Coverage/statistics computed from textures are Observed, not screenshot parity. Meitou lit-layer,
volume noise/lighting/shadows and night key are engine choices with fallback boundaries in
[render-clouds](../render-clouds.md). Original weather changes scattering only through decoded
inputs, not arbitrary sky tint or physical cloud volume.

### Options (Verified (decompiled): `CloudLayer::CloudLayer` defaults, FUN_14066e380 values)

Kenshi's `CloudLayer::Options` is 0x30 bytes with the fields below; `DensityMultiplier`, `DensityOffset` and `Darkness` look
like Kenshi additions (**Observed**: from memory of the open-source SkyX 0.4, not checked against its source here).
The game creates the layer with:

| Option (offset) | Value | Shader constant | Used by Kenshi's shader |
|---|---|---|---|
| Height (0x00) | 100 | `uHeight` | yes |
| Scale (0x04) | 0.001 | `uScale` | yes |
| WindDirection (0x08) | (1, 1) | — | no (the manager's vector is used, below) |
| TimeMultiplier (0x10) | 0.125 | `uTime` = multiplier × SkyX time | no (the line is commented out) |
| DistanceAttenuation (0x14) | 0.05 | `uDistanceAttenuation` | yes |
| DetailAttenuation (0x18) | 1 | `uDetailAttenuation` | no |
| HeightVolume (0x1c) | 0.25 | `uCloudLayerHeightVolume` | yes |
| VolumetricDisplacement (0x20) | 0.01 | `uCloudLayerVolumetricDisplacement` | yes |
| DensityMultiplier (0x24) | 3 | `uDensityMultiplier` | yes |
| DensityOffset (0x28) | −0.8 at creation | `uDensityOffset` | yes |
| Darkness (0x2c) | 0 at creation | `uDarkness` | yes |

### Coverage by density (Observed: computed from the textures)

| `clouds density` c | `DensityOffset` | mean alpha | texels with alpha > 0.05 | > 0.5 | horizon band alpha | `Darkness` |
|---|---|---|---|---|---|---|
| 0 | −0.80 | 0.000 | 0 % | 0 % | 0 | 0 |
| 0.1 | −0.66 | 0.001 | 0.5 % | 0 % | 0 | 0 |
| 0.2 | −0.52 | 0.010 | 6.4 % | 0.1 % | 0 | 0 |
| 0.4 | −0.24 | 0.25 | 87 % | 13 % | 0.26 | 0 |
| 0.5 | −0.10 | 0.54 | 99.6 % | 49 % | 0.40 | 0 |
| 0.6 | 0.04 | 0.83 | 100 % | 91 % | 0.54 | 0.50 |
| 1 | 0.60 | 1.00 | 100 % | 100 % | 1 | 0.81 |

## Unknowns

- The dome mesh's texcoord layout (taken as the unit direction from the shader's use) and the dome's lower half (below the
  horizon the shader gives `alpha = o + 0.5`, hidden by terrain in practice).
- The exact render queue (6 from SkyX's struct order), which decides that the clouds cover the planets (queue 6, priorities 1 and 2;
  [sky.md](sky.md), "Planets"): the viewer draws the planets first.
- Whether the game's mip-less sampling looks noticeably different from the viewer's mipmapped one near the horizon (needs a game
  screenshot in a cloudy weather at a low pitch).

## Expression reference

- From the weather, FUN_14066f190, FUN_14066cc80, FUN_1409dc4e0): `DensityOffset = 1.4 c − 0.8`; `Darkness = pow(clamp(c − 0.5, 0, 1), 0.3)`
- The fragment shader: `o = uDensityOffset`; `m = uDensityMultiplier`; `uv = point.xz · uScale`; `w = offset · 0.00005`; `D1 = saturate((Clouds.r(uv + w) + o) · m)`; `D = (Clouds.r(uv' + w + (0.2, 0.6)) + o) · m + CloudsTile.r(uv' − w) · 0.1`; `h = saturate(10 · saturate(d.y − uDistanceAttenuation))`; `D' = D + h`; `a = D' · saturate(1 − tile + o)`; `alpha = lerp(o + 0.5, a, h)`
- Coverage by density: `h = 1`
- Lighting at sunset and night: `zenithLight = max(getColorAt(+Z) · skyMult, (0.001, 0.001, 0.0015)) · sunColour.g`
- Meitou cloud shadows: `uv = 0.1 · xz / 12000`; `uv = 0.1 · (eye.xz + d.xz / d.y · (12000 − eye.y)) / 12000`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- Geometry and pass (Verified (decompiled), SkyX_x64.dll)
- Options (Verified (decompiled): `CloudLayer::CloudLayer` defaults, FUN_14066e380 values)
- From the weather (Verified (decompiled), FUN_14066f190, FUN_14066cc80, FUN_1409dc4e0)
- The fragment shader (Verified: `SkyX_Clouds.hlsl`, Kenshi's version)
- Coverage by density (Observed: computed from the textures)
- Lighting at sunset and night (follows from the Verified formulas; not screenshot-checked)
