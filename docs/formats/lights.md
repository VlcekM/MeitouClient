# Placed lights

Original LIGHT schema, building assembly, deferred light shader and on-state binary research.
Record table/formulas below. Engine WorldLights/WorldLamps: [render-lights](../render-lights.md).

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
- **Night flag, per light** (parts recursion `140553dc0`, builder `FUN_140815420`): `custom.x` = 1 when Building slot 59
  returns the null handle (type 0xb) or byte +0x240 is set. The light pass (`FUN_1402d8540`) draws a flagged light only
  while the daylight factor (`FUN_14066cb50`) is at most 0.1. +0x240 is `isOutsideFurniture` (KenshiLib 1ce4a16, **Observed**;
  the factory `FUN_14057cc70` stores its last argument there). So every building's outdoor lights (campfires, turret lamps, houses) are night-only.
  Meitou (`WorldLights.FromPlaced`): every light is flagged unless its building is indoors (`WorldLights.IsIndoors`; exterior layout items never are).
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
- Town buildings' power and on state are **Unknown**; Meitou applies only the night flag to them.

### Spot direction

**Verified** (decompilation and disassembly): the instance walk (`FUN_140553dc0`, call at `0x14055427f`) places a LIGHT instance's node at the instance
position and calls `Light::setDirection(instance rotation × Vector3::NEGATIVE_UNIT_Y)`. OgreMain's `Light::setDirection` turns the light's node so its local +Z
points that way in the parent's space, and `getDerivedDirection` is the derived orientation × +Z. So a spot points along the instance rotation applied to −Y,
turned by the holder's node. The walk's other branch (no holder node) instead sets the node's orientation outright, so +Z of that orientation; when
it is taken is **Unknown**. Meitou: `WorldObjectLayout.PlaceLight` (−Y for every light).

### Emissive parts

**Verified** (decompilation; constants read from the binary): a building part whose material has `material type` 4 (EMISSIVE) takes its shader `brightness`
(custom parameter 0) from the building's lights. At creation (`FUN_140558b40`) it is 1, or 0 when the building has lights and is not on (slot 100) or its
power factor (slot 109) is at most 0. The lights switch (slot 140, `FUN_14029aa10`) then sets every emissive part to 1 when switching on and 0 when
switching off. So a lamp's glowing glass is dark whenever its lights are off (a night lamp by day), and a building with no lights always glows. What glows: **Verified** (`objects.hlsl` EMISSIVE, `deferred.hlsl`): light added = albedo x brightness x (1 - the normal map's alpha) x 3.2, so an opaque alpha does not glow (the lens has a low alpha, the housing 1).
Meitou: `WorldLamps.EmissiveDark`, passed per instance to the object shader (`WorldObjectRenderer.Emit`, `BuildingLodShaders`); a night lamp counts as
on while the daylight factor is 0 (the switch follows slot 164's "not daytime"). In Hub and Stack at noon no lamp building has an EMISSIVE material
(**Observed**: the switch changes no pixel there).

### Carried lanterns

**Verified** (merged data, probe 2026-10-20; decompiled where an address is given) unless marked:

- **Records.** A worn record's `physics attachment` names a CHARACTER_PHYSICS_ATTACHMENT (`bone name`, `file male` /
  `file female` .phs, `light data` -> LIGHT). With a light: ITEM Lantern and Lantern of Radiance (`lantern hip`,
  `Bip01 Pelvis`), CONTAINER Garru Backpack (`Packbeast lantern`, `Bip01`; the Pack Beast's `backpack`), and two mod
  attachments on `Bip01 Spine2` that nothing references. All name `pack beast light`: point, F5F59C, 0.2 ± 0.05, r 300.
- **Belt slot.** The two lanterns are the only records with `slot` 14 (ATTACH_BELT), so the inventory's belt slot holds
  lanterns (**Observed**: Meitou lets an ITEM with slot 14 into `belt`; **Verified** in `FUN_14075faf0`:99-101 / `FUN_14074bc30` that every item type stores its `slot` and a section takes an item whose slot is its type, so an ITEM of any worn slot (shirt, hat, boots, body, legs, belt) goes to that section, and slot 7 or an unmapped one to none; base data: 307 ITEMs slot 7, the 2 lanterns slot 14). A CONTAINER's `encumbrance effect` is clamped to 0 or more (`FUN_140763810`:127-134, **Verified**); a missing field stays 1 in Meitou (the original's default is **Unknown**). The item's `charges` 200 use is **Unknown**.
- **Lights** (`ScythePhysicsT`, ctor `1407e71f0` reads `light data`; `vfunc_15`): one light per entry of a map, made by the
  building light builder with the night flag set, so the light pass draws them only at daylight <= 0.1. Each frame
  (`vfunc_2`) a light's node takes a physics body's position and orientation. How the map is filled (which body) is
  **Unknown**.
- **Files.** The .phs bodies are in the character's binding-pose model space (a human pelvis is at y 10.4): the belt
  lantern hangs from a spherical joint at the hip, the pack beast's at the top of a pole (y 31).
- **Meitou** (engine choice): the light sits at the body hanging from the last spherical joint (not `root`), held rigidly
  relative to the bone; no swing. The lantern meshes (in the .phs points) are not drawn; an animal's lantern comes from the bag it wears.

## Effects on buildings

- **Data** (**Verified**, merged base game, `meitou-tools particles effects` "building effect" lines): EFFECT instances sit on
  BUILDING Campfire and Cooking Pit (`small- fire1_small`) and on BUILDING_PARTs: torches (`small- fire2_torch`, Holy torches),
  furnace smoke, generator smoke/sparks, stone processor and windmill sparks, Peeler blood, Droid streetlight jet. None has a `sound`.
- **Placement** (**Observed**, 1.0.68 instance walk `140553dc0`, `FUN_1400f8db0`): the effect's node is a child of the holder's
  node at the instance position, **not** scaled by the building `scale` (unlike a LIGHT), turned by the instance rotation. It draws
  no roll from the building generator.
- **When on** (**Observed**, KenshiLib `Building::effects` 0x250, `effectsVisible` 0x268, `effectsActive` 0x269; update
  `1405626a0`): effects run while the building is visible, `effectsVisible` and complete. After the walk the exe calls
  `switchEffects(isComplete)` and `switchLights(isComplete)`; `switchPowerOn` switches lights and effects together. No night rule.
- **Meitou**: world buildings (not destroyed, state `power on` not false) and their exterior layout objects get their effects, run
  always on; interior layouts are left out. Player buildings get them once complete. The effects' sounds are not played: the
  buildings' own emitters play the fire.

## Expression reference

- Shading: ` x = saturate(d / radius) atten = x < 0.649 ? 0.2 + 0.8 (1 − x)³ : x < 0.8 ? 0.242 − 3 (x − 0.6)² : 3 (x − 1)² 1 at the light, 0.30 at R/2, 0.235 at 0.649 R, 0.12 at 0.8 R, 0 at R spot = saturate(((L·dir − spot.y) / (spot.x − spot.y))^spot.z) spot lights only colour = (π · N·L · diffuse · Fresnel-diffuse · albedo + GGX specular(diffuse)) · atten · spot · power power = powerScale · global (1.0) · effect `

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- The LIGHT record (Verified: fcs.def, the merged base game, the exe's light builder FUN_140815420)
- Counts (Observed, `WorldLights.ForWorld`, 2026-10-09)
- Shading (Verified: `deferred.hlsl` `light_fs`, `CalcPunctualLight`, the light pass FUN_1402d8540)
- When a light is on (Verified (decompiled) unless marked)
