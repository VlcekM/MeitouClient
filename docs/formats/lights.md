# Placed lights

## Instances and lighting

LIGHT records instantiate through building/part/layout transforms with colour/radius/power/spot/
effect parameters and rolled power. Per-instance node/state/power ownership matters; mesh emissive
material is not the same as a lamp. Visible positive-power sphere/cone volumes add diffuse/GGX
in sun-light units; radius clips, NdotL<0.01 skips. Attenuation/spot equation block retained.
PULSE=0.5+0.5sin4t, SHIMMER=0.95+0.05sin30t*sin17t; clock t units unknown. No flicker case found
in the decoded pass, factor1 there; another writer may affect it.

Original pass has no discovered hard count cap; engine spatial selection/budgets are separate. When lights are on: below.

### The LIGHT record (Verified: fcs.def, the merged base game, the exe's light builder FUN_140815420)

| Field | List | Meaning |
|---|---|---|
| `type` | int | `LightType`: 0 POINT, 1 SPOT. The game makes an Ogre point light (0) or spotlight (2) of it |
| `diffuse` | int 0xRRGGBB | Light colour. Unpacked as bytes / 255 (Ogre `ColourValue::setAsARGB`), used as is in the linear HDR light pass (no sRGB decode) |
| `specular` | int 0xRRGGBB | Not in fcs.def, read by the exe as the specular colour; set only on `Streetlight-Droidtype-OmniLIGHT` (FFFFFF). The light shader declares `specularColour` but does not use it (it uses `diffuseColour` for both terms) |
| `brightness` | float | Base power (Ogre `setPowerScale`); 0.2 to 1 in the base game, 8 and 15 on two effect lights |
| `variance` | float | Each instance's power is `brightness + U(−variance/2, +variance/2)` (below) |
| `radius` | float | The light's reach, Ogre `setAttenuation(radius, 1, 0, 0)`; the shader uses only the range. 25 to 750 (3000 on lightning) |
| `inner`, `outer` | float, degrees | Spot cone full angles (`setSpotlightRange` after degree→radian; the light volume's cone uses tan(outer/2)). Set on point lights too, unused there |
| `falloff` | float | Spot falloff exponent between the cones ("1 linear, >1 soft, <1 hard", fcs.def) |
| `effect` | int | `LightEffect`: 0 NONE, 1 PULSE, 2 FLICKER, 3 SHIMMER (below) |
| `landscape`, `buildings`, `characters` | bool | "Cast shadows" switches. **Dead data**: the light builder always calls `setCastShadows(false)` and no code references the strings (FCS sets all three from the scene's `castShadows`) |

| LIGHT | Type | diffuse | brightness ± variance/2 | radius | inner/outer | effect | Used by |
|---|---|---|---|---|---|---|---|
| `torch_light` | point | FFCE5E | 0.4 | 70 | — | FLICKER | Torch Post, Large Torch Post |
| `HolyStreetTorch` | point | FFCE5E | 0.7 ± 0.05 | 150 | — | FLICKER | Holy Street / Sinner Torch |
| `campfire light` | point | FFCE5E | 0.6 | 140 | — | SHIMMER | Campfire, Cooking Pit |
| `strip_light_blue` | point | 9DEBF9 | 0.8 ± 0.2 | 60 | — | — | Electrical Torch Post |
| `street_light_for lampposts` | spot | A8FFE2 | 0.6 ± 0.125 | 170 | 75/120 | — | City Lamp Post, Light Post II |
| `LIGHT` (14380-Newwworld) | spot | CEFDF0 | 0.35 | 100 | 74.7/120 | — | Quad Streetlamp (4 per lamp) |
| `indoor_lamp_light` | spot | E9FF9B | 0.7 ± 0.1 | 120 | 90/100 | — | Ceiling Lamp, Ceiling Fan Lamp |
| `street_light_yellow` | spot | F1E8B4 | 0.6 ± 0.15 | 300 | 40/90 | — | Spotlight, Wall Spotlight |
| `spotlight powerful` | spot | F1FFC4 | 1 ± 0.1 | 750 | 60/80 | — | Searchlight |

### Shading (Verified: `deferred.hlsl` `light_fs`, `CalcPunctualLight`, the light pass FUN_1402d8540) — layout/example

```
x      = saturate(d / radius)
atten  = x < 0.649 ? 0.2 + 0.8 (1 − x)³
       : x < 0.8   ? 0.242 − 3 (x − 0.6)²
       :             3 (x − 1)²                       1 at the light, 0.30 at R/2, 0.235 at 0.649 R, 0.12 at 0.8 R, 0 at R
spot   = saturate(((L·dir − spot.y) / (spot.x − spot.y))^spot.z)    spot lights only
colour = (π · N·L · diffuse · Fresnel-diffuse · albedo + GGX specular(diffuse)) · atten · spot · power
power  = powerScale · global (1.0) · effect
```

### When a light is on

**Verified** (decompiled) unless marked:

- **Function.** `function` is on the BUILDING_FUNCTIONALITY a BUILDING references (`functionality`), not on the BUILDING.
  Probe of the merged data (Observed, 2026-10-20): every lamp building with a LIGHT is BF_LIGHT 15 (Light Post I-III,
  City Lamp Post, Torch Posts, wall lights, Spotlight, Searchlight, Ceiling Lamp) except Campfire (4), Cooking Pit (11),
  turrets (12) and the hydroponic farms (1). Draws: Light Post I/II/III 4/3/2, Spotlight 4, Searchlight 8, Ceiling Lamp 2.
- **Slot 59** (`isIndoors`, KenshiLib 1ce4a16; Building override `FUN_140547530`): the null handle for a door (+0x1a0) or a building whose
  +0xf8 is clear; +0xf8 is the BUILDING's `is interior furniture` (`FUN_14029e0c0`). Else the building it stands in: the RootObject
  `isInsideBuilding` hand (+0x80, set by the constructor `FUN_14055ca90` from its interior layout's building or the indoors building
  it was made in), else its layout's building, else for class 8 (BCTYPE_TURRET) its mounted building. Probe of the merged data: Ceiling Lamp, Ceiling
  Fan Lamp, Lamp Indoor Gas, Flourescent Light, wall lights, Spotlight, Searchlight and the torch posts are interior furniture; Light
  Posts, City Lamp Post, Quad Streetlamp and street torches are not. Meitou (`WorldLights.IsIndoors`, `Machine.Indoors`): interior
  furniture in an interior layout or on a site with a parent.
- **Daylight factor** (`FUN_14066cb50`): 0 outside sunrise..sunset, ramps over 1/12 of a day (2 hours) after sunrise and
  before sunset, 1 between. `GameClock_IsDaytime` (`FUN_14066cb20`): strictly between them.
- **Lights switch** (Building slot 140, offset 0x460): stores +0x26a, shows each light while on and +0x19d (the building's
  visibility, set by slot 32 from the floor shown). Called on with completion (slot 72), off by dismantling (slot 73),
  with the power switch (slot 99).
- **Each update** (slot 28, `UseableStuff::vfunc_28`) of a complete building: every light's power = its rolled power x
  slot 109; visibility flips (slot 140) when slot 109 crosses 0. Slot 109 = 0 when slot 164 is false, else
  granted / demand (1 with no demand) x health; plain Building and doors return 1.
- **Slot 164** (consumer active): 1 for every functional class; `LightBuilding` (`FUN_140297890`): 1 when slot 59 is a valid handle, else
  "not daytime". A light building outdoors thus draws power (slot 163 asks slot 164) and lights only at night. Its `mounted` handle is +0x440.
  Meitou: `Machine.Active`, daytime from `World.Daytime` (the host's clock).

### Carried lanterns

**Verified** (merged data, probe 2026-10-20; decompiled where an address is given) unless marked:

- **Records.** A worn record's `physics attachment` names a CHARACTER_PHYSICS_ATTACHMENT (`bone name`, `file male` /
  `file female` .phs, `light data` -> LIGHT). With a light: ITEM Lantern and Lantern of Radiance (`lantern hip`,
  `Bip01 Pelvis`), CONTAINER Garru Backpack (`Packbeast lantern`, `Bip01`; the Pack Beast's `backpack`), and two mod
  attachments on `Bip01 Spine2` that nothing references. All name `pack beast light`: point, F5F59C, 0.2 ± 0.05, r 300.
- **Files.** The .phs bodies are in the character's binding-pose model space (a human pelvis is at y 10.4): the belt
  lantern hangs from a spherical joint at the hip, the pack beast's at the top of a pole (y 31).
- **Meitou** (engine choice): the light sits at the body hanging from the last spherical joint (not `root`), held rigidly
  relative to the bone; no swing. The lantern meshes (in the .phs points) are not drawn; an animal's lantern comes from the bag it wears.

## Effects on buildings

- **Data** (**Verified**, merged base game, `meitou-tools particles effects` "building effect" lines): EFFECT instances sit on
  BUILDING Campfire and Cooking Pit (`small- fire1_small`) and on BUILDING_PARTs: torches (`small- fire2_torch`, Holy torches),
  furnace smoke, generator smoke/sparks, stone processor and windmill sparks, Peeler blood, Droid streetlight jet. None has a `sound`.
- **Meitou**: world buildings (not destroyed, state `power on` not false) and their exterior layout objects get their effects, run
  always on; interior layouts are left out. Player buildings get them once complete. The effects' sounds are not played: the
  buildings' own emitters play the fire.

## Expression reference

- Shading: ` x = saturate(d / radius) atten = x < 0.649 ? 0.2 + 0.8 (1 − x)³ : x < 0.8 ? 0.242 − 3 (x − 0.6)² : 3 (x − 1)² 1 at the light, 0.30 at R/2, 0.235 at 0.649 R, 0.12 at 0.8 R, 0 at R spot = saturate(((L·dir − spot.y) / (spot.x − spot.y))^spot.z) spot lights only colour = (π · N·L · diffuse · Fresnel-diffuse · albedo + GGX specular(diffuse)) · atten · spot · power power = powerScale · global (1.0) · effect `

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- The LIGHT record (Verified: fcs.def, the merged base game, the exe's light builder FUN_140815420)
- Counts (Observed, `WorldLights.ForWorld`, 2026-10-09)
- When a light is on (Verified (decompiled) unless marked)
