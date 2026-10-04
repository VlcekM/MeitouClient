# Runtime materials (how Kenshi builds materials from FCS records)

Kenshi does not use the material names stored in meshes for buildings, items, clothing or map features.
It takes a script material as a template, clones it under a new name and fills it from an FCS material
record (`MATERIAL_SPEC`, `MATERIAL_SPECS_CLOTHING`, `MATERIAL_SPECS_WEAPON`, or a `MAP_FEATURES` record).
Shader variants are made by cloning the HLSL GPU programs with extra preprocessor defines.

Sources: decompilation of `kenshi_x64.exe` (Steam build installed 2026-10; Ghidra 12.1.4), addresses
below are function entry points in that image. The game's own source-file name for this code is
`Re_usea_ma_tron.cpp`, and the central function reports errors as `getObjectMaterial()`. Status labels as
in [ogre-material.md](ogre-material.md): **Verified (decompiled)** means read directly in the function
named; **Observed** means inferred from the decompile with some uncertainty (lossy argument recovery, unnamed
helpers); **Observed (source)** means taken from the ogre-next `v2-0` source, not from Kenshi's DLL.

See [ogre-material.md](ogre-material.md) for the script format and resource lookup rules.

## The builder: `getObjectMaterial` (FUN_140841a50)

Inputs: a template (base) material name, the material record, a flag word (below), an optional extra
integer that only makes the clone name unique, and (for dust) a world position.

Steps (**Verified (decompiled)** unless marked):

1. Read the record's texture fields and reduce each to its bare file name (everything after the last `/`
   or `\`, FUN_1409b4610). Defaults when the field is empty: `texture map` -> `white.dds`,
   `normal map` -> `flat.dds`. With the DUAL flag also `texture map 2` (no default), `normal map 2`
   (default `flat.dds`), `metalness map 2` (default `black.dds`).
2. Make a clone name from the base name, `_`, the flag word in decimal, `:` and the texture names, plus the
   extra integer and the dust biome when used. (The exact separators are a cache key and were not pinned;
   **Observed**.) If a material of that name exists it is reused; otherwise the base material is looked up
   (an exception "Could not find material" if missing) and cloned under the new name.
3. On technique 0, pass 0 of the clone, set textures **by texture unit name** (FUN_140845490 uses
   `Pass::getTextureUnitState(name)`, i.e. the `texture_unit <name>` of the script, not `texture_alias`),
   as 2D textures:
   - `diffuseMap` <- `texture map`
   - `normalMap` <- `normal map`
   - `metalnessMap` <- `metalness map`, only if the field is non-empty (else the script's own texture,
     `black.dds` in `StaticObject`, stays)

   A missing unit is not created; the game logs "Error: texture X does not exist in material Y".
4. Units **created** by the code (appended to pass 0, then filled the same way):

   | Flag | Unit name | Texture |
   | --- | --- | --- |
   | CONSTRUCTION (0x4) | `scaffold` | `construction.dds` |
   | DUAL_TEXTURE (0x2) | `diffuseMap2`, `normalMap2`, `metalnessMap2` | `texture map 2`, `normal map 2`, `metalness map 2` |
   | CLIP_INTERIOR (0x200) | `interiorMask` | content type compositor, texture `rt_interiormask`, index 0 |
   | DUST (0x40) | `dust` | `Turbulent.dds` |

5. DOUBLESIDED (0x10): hardware and software culling set to none.
6. GPU program variants: see below.
7. Shader parameters (all via `setNamedConstant` with missing names ignored):

   | Program | Parameter | Value |
   | --- | --- | --- |
   | vertex | `tiling` | (`tile X`, `tile Y`, 0); (1, 1, 1) if either is 0 |
   | fragment | `scaffoldTiling` | `scaffolding tex scale` (float) |
   | fragment | `threshold` | `alpha threshold` / 255 (int field, default 128 when absent) |
   | fragment | `glossMult` | `specular mult` |
   | fragment, EMISSIVE | `brightness` | auto constant, type 87 = `ACT_CUSTOM` index 0 (per-renderable custom parameter; **Observed (source)** for the enum value) |
   | fragment, CLIP_INTERIOR | `viewport` | auto constant, type 110 = `ACT_VIEWPORT_SIZE` (**Observed (source)**) |
   | fragment, DUST | `dustColour` | `ground colour` (ARGB int) of the `BIOMES` record at the object's position (**Observed**: biome lookup through the terrain object) |

8. Shadow caster: if technique 0 has a shadow caster material, a variant of it is built (FUN_140840da0)
   and set as the clone's shadow caster.

### Flag word

Bit meanings from the define names the code adds (FUN_140841a50, **Verified (decompiled)**):

| Bit | Name (define) | Vertex program | Fragment program |
| --- | --- | --- | --- |
| 0x1 | `COLOURING` | yes (0x1 or 0x2 give `COLOURING`) | yes |
| 0x2 | `DUAL_TEXTURE` | (as COLOURING) | yes |
| 0x4 | `CONSTRUCTION` | yes | yes |
| 0x8 | `TRANSPARENCY` | | yes |
| 0x10 | `DOUBLESIDED` | | yes |
| 0x20 | `EMISSIVE` | | yes |
| 0x40 | `DUST` | | yes |
| 0x80 | `INSTANCED` | yes | |
| 0x100 | `INTERIOR` | | yes |
| 0x200 | `CLIP_INTERIOR` | | yes |

Vertex mask 0x87, fragment mask 0x37F (constants at 0x141fd5cd0 / 0x141fd5ccc). If `flags & mask` is non-zero,
the pass's program is replaced by `<program name>_<flags & mask>`; if no program of that name exists yet it is
made by FUN_1408409e0: for a `unified` program take the delegate the render system uses, require `hlsl`
(else an exception "Invalid shader"), clone it under the new name and append the defines (comma separated,
e.g. `COLOURING,CONSTRUCTION,INSTANCED`) to its `preprocessor_defines` (FUN_140840670). Defines are added in
this order: vertex `COLOURING, CONSTRUCTION, INSTANCED`; fragment `DUST, DUAL_TEXTURE, TRANSPARENCY,
INTERIOR, CLIP_INTERIOR, COLOURING, EMISSIVE, DOUBLESIDED, CONSTRUCTION`. The shader sources under
`data/materials/deferred/` (`objects.hlsl`, `triplanar.hlsl`...) contain these `#ifdef`s (Observed by
text search).

`fcs.def` says ALPHA "uses normal map alpha for transparency" and EMISSIVE "normal map alpha channel becomes
emissive glow amount"; consistent with that, the shadow caster's `alpha` unit is fed the **normal map**
(below).

### Shadow caster variant (FUN_140840da0, Verified (decompiled))

Only `flags & 0x8C` (TRANSPARENCY, CONSTRUCTION, INSTANCED) matters; if none is set the base shadow caster is
used unchanged. Otherwise it is cloned (name from the caster name, `_`, the masked flags, the alpha texture,
the extra integer) and in **every** technique's pass 0: vertex program gets `TEXCOORDS`, `INSTANCED` (0x80),
`CONSTRUCTION` (0x4); if `flags & 0xC`, the fragment program gets `TEXCOORDS`, `ALPHA` (0x8),
`CONSTRUCTION` (0x4); with TRANSPARENCY a unit `alpha` (created if absent) gets the record's normal map and
`threshold` is set; with CONSTRUCTION a unit `construction` gets `construction.dds` and `threshold` is set.

## Callers: which template and which flags

"Has vertex colours" below means the mesh's first submesh's vertex declaration (or the shared one) has a
`VES_DIFFUSE` element (FUN_14083ff20, **Verified (decompiled)**).

| Use | Function | Template | Flags (**Verified (decompiled)**) |
| --- | --- | --- | --- |
| Building parts in the world | FUN_14057a920 | `StaticObject` | COLOURING if vertex colours; `material type` (BuildingShader): ALPHA (1) -> 0x8, FOLIAGE (2) -> 0x18, DUAL (3) -> 0x2 only with vertex colours, EMISSIVE (4) -> 0x20; always DUST (0x40); INSTANCED (0x80) and INTERIOR (0x100) from caller arguments |
| Buildings under construction | FUN_140557be0 | `StaticObject` | 0xC (CONSTRUCTION, TRANSPARENCY), 0xF if DUAL; INTERIOR unless the building is of one particular kind (type 0xb) or a flag on the object is set; also sets `upperPos` and `scaffoldTiling` (from `building height`, `scaffolding tex scale`) on the pass and shadow caster |
| Building preview / icon | FUN_140848950 | `StaticObject` | as building parts without DUST/INSTANCED/INTERIOR; part material chosen from `material match` / `material` / the base building's material, falling back to a record in `742-gamedata.base` |
| Map features | FUN_140843920 | by `texture mode` (MapFeatureMode): UV_MAPPED, DUAL_TEXTURE, FOLIAGE, EMISSIVE -> `StaticObject`; TRIPLANAR, DUAL_TRIPLANAR -> `Triplanar`; TERRAIN -> `Feature_Terrain_DX11` through the terrain material builder (FUN_140a14c90) with the one biome of `biomemap.png` at a given point; see [foliage.md](foliage.md#terrain-mode-meshes) | COLOURING if vertex colours; DUAL_TEXTURE / DUAL_TRIPLANAR -> 0x2 only with vertex colours (else a warning "is dual texture but the mesh does not have vertex colours"); FOLIAGE -> 0x18; EMISSIVE -> exactly 0x20 (then `brightness` set to the constant 1.0 instead of the auto constant); always CLIP_INTERIOR (0x200) |
| Items on the ground, weapons | FUN_140844030 (via FUN_140446e60) | `StaticObject` | `MATERIAL_SPECS_CLOTHING`: 0x18 if `material type` (ItemShader) > 0, plus 0x1 from a caller flag; `MATERIAL_SPECS_WEAPON`: 0; `ARMOUR`: a temporary record whose `texture map` / `normal map` are the armour's `vest texture` / `vest normalmap`, flags 0 |
| Items worn on a character | FUN_140536210 (`AppearanceBase::setupItemMaterial`) | `Skinned` | 0x10 if ItemShader is DOUBLE_SIDED (2), else 0 (ALPHA gives no flag here) |
| Clothing attachments with their own mesh | FUN_140535f20 | `StaticObject` | 0, record from the item's `material` reference |
| Farm plants | FUN_1400e70a0 | `FarmPlants` | 0, plus shared parameters `FarmParams<n>` (`plantData`) |
| Inventory icons (render to texture) | FUN_140846060 | `RTTIcons_Base`, `RTTIcons_Coloured` (mesh with vertex colours) or `RTTIcons_DXT5N` (one item kind, 0x11) | 0x10 if ItemShader is DOUBLE_SIDED |

Which record a building part passes in (FUN_14054cd30, **Verified (decompiled)**): the part's first `material`,
else the building's base material (the BUILDING's first `material`, else its town's: TOWN `material`, default
`742-gamedata.base`), else `360-gamedata.base`; a MATERIAL_SPEC with its own `material` list is a collection and
one entry is picked by val0 weight. Details, and how the town is found, in
[zones.md](zones.md#materials-the-local-town-material).

The script materials `Building`, `Building_Alpha`, `Building_Dual`, `Building_Emissive`
(`buildings.material`) are **not referenced** by the executable (no such strings, Verified by search); the
engine gets those looks from `StaticObject` plus defines.

### Colour masks (clothing, Verified (decompiled))

Worn items (FUN_140536210), when the item has a colour/dye record and its material record has a non-empty
`color map`: the `colorMap` unit of the clone (the `Skinned` script defines it, default
`colormap_default.dds`) gets the `color map` texture; fragment parameters `color1` and `color2` get the dye
record's `color 1` / `color 2` (ARGB ints). **Observed**: their alpha is then replaced by the item's
`paint factor 1` / `paint factor 2` (the two float reads are stored at the alpha offset of the two colour
values on the stack). A vertex parameter `hideSide` gets (1, 0) or (0, 1) in a special two-handed/locked case
(**Observed**, condition not fully decoded). Inventory icons (FUN_140846060) set a unit named `colourMap` from
`color map` and parameters `colour1` / `colour2` from the colour record's `color 1` / `color 2` (no paint
factors), unless the item says `dont colorise`.

## Unknown

- Terrain/biome materials beyond what terrain.md and foliage.md ("TERRAIN-mode meshes") describe.
- Characters' bodies, heads and hair (`Character`, `Hair`... scripts): the parameters Kenshi sets (skin tone, hair
  channels and colour) and the shader layering are in [../characters.md](../characters.md); the material
  cloning for them was not traced.
- Which call sites pass the INSTANCED / INTERIOR arguments, and the exact clone-name format.
