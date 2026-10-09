# Placed lights

Point and spot lights in the world: the LIGHT record (FCS type 88), where the game places its instances, how the deferred light pass
shades them and when they are shown. Written for the viewer's ray-traced GI ([render-gi.md](../render-gi.md)), which needs a world light
list. Code: `Meitou.Data.World.WorldLights` (the list and the shading formulas), `WorldObjectLayout.Building(..., lights)` (placement).

Sources: `fcs.def` / `fcs_enums.def`; the mod editor's decompiled `OgreSceneImporter` (how it makes LIGHT records from a scene) and
`BuildingFunction` / `BuildingClassType` enums; `data/materials/deferred/deferred.hlsl` (`light_fs`) and `deferred.material`
(`DeferredLight`, `DeferredLight_Spot`), `common/lightingFunctions.hlsl` (`CalcPunctualLight`), read for facts only; `kenshi_x64.exe`
decompiled and disassembled outside the repository (Ghidra 12.1.4, 2026-10-09). Functions are named by entry address as in
[zones.md](zones.md#from-placements-to-meshes). Counts: the base game's four files merged, with the install's level data, 2026-10-09.

## The LIGHT record (Verified: fcs.def, the merged base game, the exe's light builder FUN_140815420)

The merged base game has **31** LIGHT records (the record-type table in fcs-mod.md says 76: a count over the files, not checked further). Fields:

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

Typical values (Observed, base game):

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

`inner` > `outer` occurs (fcs.def's default 45/40; `labs fluorescent light` 45/40), which makes the shader's spot term
divide by a negative number (Unknown what it draws; `WorldLights.SpotFactor` orders the cones, a Meitou choice).

## Where lights are placed

**Building and part instances (Verified (decompiled), FUN_140553dc0).** A BUILDING or BUILDING_PART lists LIGHT instances (fcs.def
`lights`, stored as the record's instances, [fcs-mod.md](fcs-mod.md#record)); the mod editor creates them when it imports an
Ogre scene with lights (`OgreSceneImporter`: one LIGHT record and one instance per scene light). In the base game 22 BUILDING_PART
records hold 34 light instances and 6 BUILDING records 6 (Observed). After a holder's parts are created (the roll order in
[zones.md](zones.md#choosing-the-parts)), each LIGHT instance, in instance-id order:

- gets a scene node: a child of the **holder part's node** when the holder made an entity, else of the building's node;
- node position = instance position × the BUILDING `scale`. Under a part node, which carries the scale itself, the scale applies
  twice (Ogre's inherited scale): the Large Torch Post (`scale` 2) has its light 4 × 12.4 units up rather than 2 × (Verified
  (decompiled) that the scaled position is set under the part's node; the in-game result was not checked);
- spot direction = instance rotation × (0, −1, 0) in the holder's frame (Verified: disassembly, `Quaternion * NEGATIVE_UNIT_Y`;
  the no-node branch reaches the same by rotating Ogre's default +Z by 90° about X). So spots point down unless rotated, and the
  lamp posts' rotations tilt them outward (Observed: `lamp_post3` lights at x = ∓3.8 tilted ∓29.5° about Z);
- **two random draws** from the building's seeded generator: `brightness + rand in [−variance/2, variance/2)` becomes the power
  (FUN_140815420; the range is `±variance × 0.5`, constant 0.5 at 0x141684440, Verified by disassembly), then the wrapper
  FUN_140553be0 draws the same range again and keeps it. Both are the MSVC `rand()` float helper of
  [zones.md](zones.md#choosing-the-parts). (Before 2026-10-09 the docs counted one draw; the second shifts only later choices
  inside the same building, and no base-game placement changed parts with the fix.)
- the holder's `building floor` is stored in the light's custom parameter (y), for the interior cut-away (Observed: use not traced).

**Exterior and interior layouts.** Layout objects ([zones.md](zones.md#building-layouts)) are buildings of their own and bring their
lights. Interior layouts are most of the world's lights (furniture lamps).

**Elsewhere (not in the list).**
- EFFECT records list LIGHT instances too (3 in the base game: `weather_lightning1`, `Lightning_Bolt`, `Twister-of-fire01`), made by
  the effect system (`Effects_CreateEffectLightPath`; EFFECT `light fade in/out` fields); weather effects, not placed lights.
- CHARACTER_PHYSICS_ATTACHMENT `light data` names a LIGHT for lights inside a `.phs` attachment (lanterns on pack beasts and hips,
  all `pack beast light`); made by `ScythePhysicsT` on characters. Moving lights, Unknown placement.
- No MAP_FEATURES record or `features.dat` entry has lights (Observed).

## Counts (Observed, `WorldLights.ForWorld`, 2026-10-09)

- **1,302** lights on placed buildings and their doors (destroyed buildings skipped), **5,159** with the layouts' objects, of which
  **3,857** are interior (Electrical Torch Post strip lights 2,052, Ceiling Lamps 797, Torch Posts 526 indoors). 1,900 are spots.
- Exterior lights by holder: Torch Post 208, Quad Streetlamp 180, City Lamp Post 161, Spotlight 89, Campfire 89, Large Torch Post 85,
  Large Wall Light 75, Small Wall Light 98, Searchlight 35, Holy Street Torch 34, turrets with lights 75, others.
- **The Hub** (town centre (−50979, 1533, 2932)): 17 lights within 3000 units, 3 of them exterior (Holy Street Torches, the nearest
  about 100 units from the centre at height +54). Mongrel 59, Heft 52, Rebirth 45, Sho-Battai 39 exterior lights within 3000.
- No base-game BUILDING has `function` BF_LIGHT (below), so every base-game light follows its building's on state.

## Shading (Verified: `deferred.hlsl` `light_fs`, `CalcPunctualLight`, the light pass FUN_1402d8540)

Each visible light with power > 0 inside the view frustum (its sphere of `radius`) is drawn as a volume (sphere, or cone for spots)
added onto the lit scene. Per pixel at distance d (zero beyond the radius, and for N·L < 0.01):

```
x      = saturate(d / radius)
atten  = x < 0.649 ? 0.2 + 0.8 (1 − x)³
       : x < 0.8   ? 0.242 − 3 (x − 0.6)²
       :             3 (x − 1)²                       1 at the light, 0.30 at R/2, 0.235 at 0.649 R, 0.12 at 0.8 R, 0 at R
spot   = saturate(((L·dir − spot.y) / (spot.x − spot.y))^spot.z)    spot lights only
colour = (π · N·L · diffuse · Fresnel-diffuse · albedo + GGX specular(diffuse)) · atten · spot · power
power  = powerScale · global (1.0) · effect
```

- The diffuse term is exactly the sun's (`CalcPunctualLight`, [lighting.md](lighting.md)): **the same units**. The sun's light is
  `sunColour.rgb · w · ambientMap.a · 2`, a light's is `diffuse · power`.
- `spot` is filled by the game, not by Ogre's auto constants; Ogre's convention (cos(inner/2), cos(outer/2), falloff) is assumed
  (Observed: the light volume uses tan(outer/2), so the angles are full cone angles).
- `global` is a member of the light renderer initialised to 1.0 (FUN_1402d96d0); no other writer was found.
- **Effects** (FUN_1402d8540): PULSE `0.5 + 0.5 sin 4t`, SHIMMER `0.95 + 0.05 sin 30t · sin 17t`, with t the clock value at +0xa0 of
  the time object × 60 (unit Unknown). FLICKER has **no case** in the pass (factor 1); the second stored roll may drive it
  elsewhere (Unknown). A factor ≤ 0 skips the light.

## When a light is on (Verified (decompiled) unless marked)

- The building class decides: BUILDING `function` BF_LIGHT (15; `BuildingFunction` order from the editor) makes a `LightBuilding`
  (class BCTYPE_LIGHT, 11; the class factory's switch on `function`, FUN_14057cc70). Such a building's lights are visible from creation
  and flagged night-only (custom parameter x = 1), and the light pass skips flagged lights while the **daylight factor** is above 0.1.
  The building also asks for power only at night (`LightBuilding` slot 164).
- Any other building's lights (flag x = 0; **all base-game lights**) start hidden and follow the building's on state
  (`setLightsOn`, slot 140, called with the flag at +0x160 at creation and by the operate/power switch, slot 99), drawn **day and night**.
  What that state is for ordinary town buildings is **Unknown**; zone states carry `power on` (4,863 states) and `is public night`.
  `WorldLight.PowerOn` passes `power on` through.
- **Daylight factor** (FUN_14066cb50): 0 before sunrise and after sunset, 1 between, ramping linearly over 1/12 of the time unit (5 minutes if it is hours, Observed) after sunrise and
  before sunset. The two bounds are fields of the time object; that they are the CONSTANTS `sunrise` 5 / `sunset` 23
  ([sky.md](sky.md#time-and-the-constants-record-verified-the-merged-load-order-2026-10-04)) is Observed (not traced).
- No limit on the number of lights was found in the pass (it loops over all registered lights, culling by frustum only).

## Viewer use (Meitou)

`WorldLights.ForWorld(db, levels, heightmap.HeightAt, layouts)` returns every light (about 0.5 s); `ForBuilding` gives one placement's
for per-zone streaming. To match the game in our units, add per light `π · max(N·L, 0) · Colour · Intensity · Attenuation(d, Radius)`
(× `SpotFactor` for spots), the same expression as the sun's diffuse term; no extra scale. Treating `WhenBuildingOn` lights as on at
night (and optionally fading them in with `1 − DaylightFactor`) is a Meitou choice for the GI, since the game's on state is Unknown.
If a light visibly floats above its lamp (Large Torch Post, `scale` 2, 85 exterior instances), dividing the instance position by `scale`
once is the Meitou override for the doubled scale.
