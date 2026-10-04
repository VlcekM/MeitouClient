# Model viewer (`tools/Meitou.ModelViewer`)

A standalone viewer for the meshes of the install (FCS lookups follow the full load order, including enabled and workshop mods): OpenGL 3.3 core through Silk.NET, its own GLSL shaders
(`Shaders.cs`; Kenshi's HLSL is only read for facts), CPU-decoded textures
([formats/dds.md](formats/dds.md)), GPU skinning and skeletal animation. Status labels as in
[README.md](README.md).

## Running

```
dotnet run --project tools/Meitou.ModelViewer -- <mesh> [options]
dotnet run --project tools/Meitou.ModelViewer -- human_male --anim ninjarun
dotnet run --project tools/Meitou.ModelViewer -- bush01 --screenshot out.png --size 800x600 --yaw 35 --pitch 20
dotnet run --project tools/Meitou.ModelViewer -- mask2 --info
```

`<mesh>` is a bare file name (`.mesh` optional) or a path (absolute, relative to the install, or relative to the
working directory). The install comes from `KENSHI_PATH` / `meitou.local.json`. `--help` lists the options and
keys (orbit / pan / zoom with the mouse, `W` wireframe, `M` next material candidate, `Left`/`Right`
animations, `Space` pause, `P` screenshot into the temp folder...). `--screenshot` renders into a hidden window's 4× multisampled
framebuffer and writes a PNG; with `--anim` the camera frames the posed mesh.

## Finding files

1. An existing path, then the path relative to the install (FCS stores `.\data\...` paths).
2. The bare name in the `resources.cfg` folders (Ogre's lookup, not recursive; first folder wins).
3. The bare name anywhere under `data/`.

**Observed**: bare names are not unique with identical content. `katana05.mesh` exists as
`data/meshes/katana05.mesh` (v1.100) and `data/items/weapons/mesh/katana05.mesh` (v1.8, the path the WEAPON
record names); the files differ (different MD5) though both are the same sheathed katana. Rule 2 picks the
first; which one the game loads is **Unknown** (the runtime material builder reduces texture paths to bare
names, see [formats/runtime-materials.md](formats/runtime-materials.md); whether meshes are treated the same was
not traced).

## How mesh textures are resolved

Mesh material names mostly don't name script materials, and `StaticObject` (the most common one) only has
placeholder textures ([formats/ogre-material.md](formats/ogre-material.md#mesh-materials-verified-2026-10-04)).
The game builds materials at run time from FCS records ([formats/runtime-materials.md](formats/runtime-materials.md),
decompiled). The viewer approximates that, best first (`MaterialResolver`):

1. `--texture` (and `--normal`) from the command line.
2. **FCS records whose filename fields name the mesh** (bare name, case-insensitive), each giving one texture
   set for the whole mesh:

   | Record type, field | Textures from | Notes |
   | --- | --- | --- |
   | FOLIAGE_MESH `mesh`, MAP_FEATURES `mesh` | same record: `texture map`, `normal map`, `texture map 2`, `normal map 2`, `tile X/Y` | mode from `material type` / `texture mode` (MapFeatureMode) |
   | FOLIAGE_MESH `leaves mesh` | `leaves texture`, `leaves normal`, `leaves alpha threshold` | transparent, double-sided (fcs.def) |
   | WILDLIFE_BIRDS `mesh` | `texture` | |
   | ATTACHMENT `mesh` / `mesh female` (hair) | `texture map` / `texture map female`; grey and alpha from `hair diffuse channel` / `hair alpha channel`, cut at `alpha rejection` | tint is a fixed brown in the viewer (game: hair colour) |
   | RACE `male mesh` / `female mesh` | `body texture male/female`, `nm male/female`; head from the first `heads male/female` HEAD record (`texture map`, `normal map`) | |
   | ARMOUR, CONTAINER, ITEM, LIMB_REPLACEMENT, MAP_ITEM, NEST_ITEM, CHARACTER_PHYSICS_ATTACHMENT, CROSSBOW, BUILDING_PART, FARM_PART, BUILDING `distant mesh` | `material` references -> MATERIAL_SPEC / MATERIAL_SPECS_CLOTHING / MATERIAL_SPECS_WEAPON | a MATERIAL_SPEC with its own `material` list, and MATERIAL_SPECS_COLLECTION, are random choices (fcs.def); all choices are listed, likeliest first |
   | BUILDING_PART without a `material` | the `material` of BUILDINGs (or parts) listing it in `parts` | **Observed**: 223 parts with a mesh have no own material; the preview code falls back to the base building's material (runtime-materials.md) |
   | WEAPON `mesh` / `bare sword` / `sheath` | MATERIAL_SPECS_WEAPON "models" of every WEAPON_MANUFACTURER whose `weapon types` lists the weapon | |
   | ITEM with its own `texture map` (1 record) | same record | |

   Found by listing every filename field per record type (all types with `.mesh` fields are covered) and every
   reference list pointing at MATERIAL_SPEC* / HEAD / COLOR_DATA records (scratch probes, 2026-10-04).
3. The submesh's **script material**, if its first pass names a texture that exists and isn't a placeholder
   (`black.dds`, `flat.dds`, `white.dds`...). Covers the exporter leftovers (`01-Default`...).
4. Otherwise untextured (flat grey).

`M` / `--material n` cycles through the candidates (e.g. the 16 weapon models of a katana).

### Facts used, and how sure

- **Verified (decompiled, runtime-materials.md)**: textures go into units by unit name (`diffuseMap`,
  `normalMap`, `metalnessMap`, `diffuseMap2`...); TRANSPARENCY clips on the **normal map's alpha** with
  `threshold = alpha threshold / 255` (default 128); BuildingShader ALPHA -> TRANSPARENCY, FOLIAGE ->
  TRANSPARENCY + DOUBLESIDED, EMISSIVE -> normal-map alpha is glow; COLOURING (diffuse × vertex colour)
  whenever a building-part or map-feature mesh has vertex colours; DUAL only with vertex colours; triplanar
  map features use the `Triplanar` template.
- **Verified (decoded channels)**: the cut-out mask is the normal map's alpha, the diffuse alpha is gloss:
  `Trees&VegAtlas02.dds` alpha is a noisy gloss map while `Trees&VegAtlas02_N.dds` alpha holds crisp leaf
  silhouettes; `Arc-Leaves_DIF.dds` alpha never exceeds 19 while `Arc-Leaves_NML.dds` alpha spans 0–254.
- **Observed (Kenshi HLSL, read for facts)**: `objects.hlsl` blends dual sets as `lerp(set2, set1, vertex alpha)`
  (vertex alpha 1 = first set); `triplanar.hlsl` projects `world position / 5000 × tiling`; the character
  shader samples the head at `uv + (0, 1)` (head UVs sit in v −1..0 of the body mesh) and adds it to the
  body, both with border addressing and a transparent black border (`character.material`), and reads the body
  normal as `.wy` (X in alpha, Y in green). The viewer does the same. Screenshots of `human_male` show a
  correct face only with this.
- **Observed (viewer heuristic)**: a normal map whose mean blue is below 180 and whose R, G, B means agree is
  treated as swizzled (catches the character body maps; building/weapon maps have mean blue ≈ 240–255).
- **Observed (screenshots)**: weapons share a UV layout: MATERIAL_SPECS_WEAPON textures (e.g. `01_HI.dds`,
  `12_HI.dds`) map sensibly onto both `katana05` and `chopper01_bare` (blade steel, edge strip and wrapped grip
  where expected).
- **Unknown**: which channel ItemShader ALPHA uses for clothing worn on a character (runtime-materials.md:
  worn items get no TRANSPARENCY flag at all; ground items get TRANSPARENCY + DOUBLESIDED). The viewer clips
  on normal alpha like ground items.
- **Unknown**: `leaves alpha threshold` scale. With `/255`, `ThinTree01_Leaves` with `TreesAtlas01.dds` would be
  invisible (its normal map is often empty in the records; the viewer then draws it opaque).
- Not followed: ARMOUR shown on the ground uses a temporary record built from `vest texture` / `vest normalmap` (runtime-materials.md); the viewer only follows ARMOUR's `material` references.
- Not reproduced: TERRAIN-mode features (biome textures), dust, construction scaffold, colour masks / dyes,
  metalness, the character's hair/beard overlays, skin tone, blood.

## Skinning and animation

- Weights come from bone assignments: per vertex the 4 largest, renormalised (Ogre's rule). Assignments past
  the skeleton's bone count are dropped with a warning (`whistler.mesh`: 6,917 of them).
- Skeleton choice: `--skeleton`; else for meshes used by ARMOUR / CONTAINER / ATTACHMENT / LIMB_REPLACEMENT
  the body skeleton (`male_skeleton`, or `female_skeleton` for a `... female` field), since Kenshi shares the
  body's skeleton instance with worn items by bone index ([formats/ogre-skeleton.md](formats/ogre-skeleton.md#binding-worn-meshes-to-the-character));
  else the mesh's link, else `<mesh>.skeleton` beside it. A missing skeleton leaves the mesh in its binding pose.
- Pose maths follow Kenshi's modified Ogre (ogre-skeleton.md, "Bone maths"): keyframes relative to the
  binding pose, rotations nlerped along the shortest path, scale not inherited, a child's offset scaled by
  the Y of its parent's derived scale. Skin matrix = posed derived transform × inverse binding transform.
- One animation at a time at weight 1; no blending, override bones, layer split or ANIMATION record
  preprocessing ([animation.md](animation.md)). Upper/lower animations (`walk upper`, `walk lower`) therefore
  move only part of the body.

Verified with screenshots (2026-10-04): `human_male` binding pose and `ninjarun` at 0.2 s (skeleton lines on
the skinned mesh), `pack_beast` `walk`, `Iron Clad Jacket_F` on `female_skeleton` (its own link is broken).

## Conventions (Verified by screenshots)

Ogre's conventions match OpenGL's defaults: right-handed, Y up, counter-clockwise front faces. Texture rows are
uploaded as stored (top row first) and UVs used unchanged; text-free proof is the human body map (shorts on the
hips, feet at the bottom, face on the head). Nothing renders inside out with back-face culling on.
Kenshi's units: a human is about 19.5 units tall, a katana mesh about 1.6 (weapons are scaled when attached; how
is Unknown).

## Limitations

- One texture set per mesh from FCS; per-submesh only for script materials.
- No LOD, poses, shadows, metalness, specular maps beyond diffuse-alpha gloss, or sRGB handling.
- The swizzled-normal decision is a heuristic, not what the game does (the game picks by shader template).
