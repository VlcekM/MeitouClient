# Runtime materials

Original getObjectMaterial140841a50, shadow-variant140840da0, script templates and FCS callers.
Flag/caller/parameter tables retained. [Scripts](ogre-material.md), [characters](../characters.md),
[weather](weather.md). Names/flags are format facts, not copied original implementation.

## Builder contract

**Verified decompiled** strip texture field directories (below); empty diffuse→white.dds, normal→flat.dds;
DUAL normal2→flat.dds/metal2→black.dds, diffuse2 no default. Clone/cache template with textures,
flags/extra integer/biome identity (exact name separators Observed, not pinned). Technique0/pass0
sets texture UNIT names diffuseMap/normalMap/metalnessMap, not texture_alias; missing units log,
not auto-create. Explicit flag-driven scaffold/dual/interior/dust units append as in table.

**Verified** (`FUN_1409b4610`, used by the item mesh, material texture and colour map paths `FUN_140537690`, `FUN_140841a50`, `FUN_140536210`, `FUN_14044a0d0`): FCS file fields are cut to the text after the last `/` or `\`, so lookups are by bare name. **Verified** (OgreMain `ResourceGroup::addToIndex` `FUN_1802869a0`, `openResource` `0x18027f160`): a group keeps an exact-case index (a later registration overwrites) and, for case-insensitive archives, a lower-case one; `openResource` tries the exact-case name first. So `Boots.dds` (Expanded Vanilla Armor's Police boots) is not hidden by the `boots.dds` of vanilla and other mods, whichever mod registers later; Meitou's `OgreScriptResources.Find` follows this. **Observed** (the original, same mod list, compared by the user): the female "Mercenary Plated Chausses" draw Expanded Vanilla Armor's identically named, later-registered `Police/PlatePantsF.mesh` with the item's texture, as these rules predict and as Meitou does; the Police boots draw right.

DOUBLESIDED disables hardware/software culling. Flags select shader defines and caster variant;
not every material supports every flag. Parameters: tiling default(1,1,1) if either tile axis0;
threshold=alpha-threshold/255, default128; gloss=specular-mult; scaffold scale; emissive custom0;
interior viewport; dust BIOMES ground colour/noise only in supporting shader branches.
Caller template/flags and dye colour-mask texture rules must be preserved per item/body/hair/
building/feature. Script defaults remain when a field is absent; a guessed shader input is not
proof the game supplies it. Unknown paths and custom-parameter interpretation remain scoped.

### Hair and beards

**Verified**, native 1.0.68 `FUN_140532600`: the attachment's float `mipmap bias` is passed to
`TextureUnitState::setTextureMipmapBias`; float `specular` is passed to the hair shader's `specularMult`.
`character.hlsl` `hair_fs` writes gloss = selected diffuse-channel brightness × `specularMult`, clips the selected alpha channel
against `threshold`, and writes the normalized interpolated normal without a back-face sign change. `Hair` disables culling (`character.material`).
**Verified** (vanilla records and a native 1.0.68 D3D11 editor frame), `Haircut08-hobo` and `ark_beard09`: both use diffuse blue, alpha A, trilinear filtering,
mipmap bias -2 and specular 0; their shader alpha thresholds are 120/255 and 70/255.
Vanilla attachment census (slots 2 and 13): 102 records, all specular 0; mip bias -2 in 86, -1 in one, 0 in 15.
Meitou carries these fields per attachment: hair gloss uses the material multiplier; the fragment texture bias adds to the upscaler sampler bias
in colour, shadow and motion cut-outs; mip streaming reserves the corresponding finer levels. Hair keeps its interpolated normal on both sides; other shading modes retain their back-face handling.
The Metal colour pass also needs the [discard/depth workaround](../metal-backend.md#the-shader-chain).

### The builder: `getObjectMaterial` (FUN_140841a50)

| Flag | Unit name | Texture |
   | --- | --- | --- |
   | CONSTRUCTION (0x4) | `scaffold` | `construction.dds` |
   | DUAL_TEXTURE (0x2) | `diffuseMap2`, `normalMap2`, `metalnessMap2` | `texture map 2`, `normal map 2`, `metalness map 2` |
   | CLIP_INTERIOR (0x200) | `interiorMask` | content type compositor, texture `rt_interiormask`, index 0 |
   | DUST (0x40) | `dust` | `Turbulent.dds` |

| Program | Parameter | Value |
   | --- | --- | --- |
   | vertex | `tiling` | (`tile X`, `tile Y`, 0); (1, 1, 1) if either is 0 |
   | fragment | `scaffoldTiling` | `scaffolding tex scale` (float) |
   | fragment | `threshold` | `alpha threshold` / 255 (int field, default 128 when absent) |
   | fragment | `glossMult` | `specular mult` |
   | fragment, EMISSIVE | `brightness` | auto constant, type 87 = `ACT_CUSTOM` index 0 (per-renderable custom parameter; **Observed (source)** for the enum value) |
   | fragment, CLIP_INTERIOR | `viewport` | auto constant, type 110 = `ACT_VIEWPORT_SIZE` (**Observed (source)**) |
   | fragment, DUST | `dustColour` | `ground colour` (ARGB int) of the `BIOMES` record at the object's position (**Observed**: biome lookup through the terrain object). The shader reads the dust noise from the `dust` unit (`Turbulent.dds`) and uses the colour as the dust's albedo ([weather.md](weather.md)); only `objects.hlsl` and `triplanar.hlsl` have a DUST branch (**Verified** by searching `data/materials`), so a material built without the DUST flag never gets dust: by the caller table below that is every map feature (**Unknown** whether the Triplanar template turns DUST on some other way; nothing in the scripts does), item and character |

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

### Callers: which template and which flags

"Has vertex colours" below means the mesh's first submesh's vertex declaration (or the shared one) has a
`VES_DIFFUSE` element (FUN_14083ff20, **Verified (decompiled)**).

| Use | Function | Template | Flags (**Verified (decompiled)**) |
| --- | --- | --- | --- |
| Building parts in the world | FUN_14057a920 | `StaticObject` | COLOURING if vertex colours; `material type` (BuildingShader): ALPHA (1) -> 0x8, FOLIAGE (2) -> 0x18, DUAL (3) -> 0x2 only with vertex colours, EMISSIVE (4) -> 0x20; always DUST (0x40); INSTANCED (0x80) and INTERIOR (0x100) from caller arguments |
| Buildings under construction | FUN_140557be0 | `StaticObject` | 0xC (CONSTRUCTION, TRANSPARENCY), 0xF if DUAL; INTERIOR unless the building is of one particular kind (type 0xb) or a flag on the object is set; also sets `upperPos` and `scaffoldTiling` (from `building height`, `scaffolding tex scale`) on the pass and shadow caster |
| Building preview / icon | FUN_140848950 | `StaticObject` | as building parts without DUST/INSTANCED/INTERIOR; part material chosen from `material match` / `material` / the base building's material, falling back to a record in `742-gamedata.base` |
| Map features | FUN_140843920 | by `texture mode` (MapFeatureMode): UV_MAPPED, DUAL_TEXTURE, FOLIAGE, EMISSIVE -> `StaticObject`; TRIPLANAR, DUAL_TRIPLANAR -> `Triplanar`; TERRAIN -> `Feature_Terrain_DX11` through the terrain material builder (FUN_140a14c90) with the one biome of `biomemap.png` at a given point; see [foliage.md](foliage.md#materials-sizes-and-grass) | COLOURING if vertex colours; DUAL_TEXTURE / DUAL_TRIPLANAR -> 0x2 only with vertex colours (else a warning "is dual texture but the mesh does not have vertex colours"); FOLIAGE -> 0x18; EMISSIVE -> exactly 0x20 (then `brightness` set to the constant 1.0 instead of the auto constant); always CLIP_INTERIOR (0x200) |
| Foliage leaves | FUN_1406d1380 (copies the FOLIAGE_MESH `leaves *` fields into `texture map` etc.) | FUN_140843920 with mode 4 (FOLIAGE) | 0x18, always CLIP_INTERIOR (0x200) |
| Items on the ground, weapons | FUN_140844030 (via FUN_140446e60) | `StaticObject` | `MATERIAL_SPECS_CLOTHING`: 0x18 if `material type` (ItemShader) > 0, plus COLOURING (0x1) when the mesh has vertex colours (the ground item, FUN_1407626e0, passes FUN_14083ff20; e.g. Building Material's `Item_Building_Materials.mesh` is tinted by them); `MATERIAL_SPECS_WEAPON`: 0; `ARMOUR`: a temporary record whose `texture map` / `normal map` are the armour's `vest texture` / `vest normalmap`, flags 0 |
| Items worn on a character | FUN_140536210 (`AppearanceBase::setupItemMaterial`) | `Skinned` | 0x10 if ItemShader is DOUBLE_SIDED (2), else 0 (ALPHA gives no flag here) |
| Clothing attachments with their own mesh | FUN_140535f20 | `StaticObject` | 0, record from the item's `material` reference |
| Farm plants | FUN_1400e70a0 | `FarmPlants` | 0, plus shared parameters `FarmParams<n>` (`plantData`) |
| Inventory icons (render to texture) | FUN_140846060 | `RTTIcons_Base`, `RTTIcons_Coloured` (mesh with vertex colours) or `RTTIcons_DXT5N` (one item kind, 0x11) | 0x10 if ItemShader is DOUBLE_SIDED |

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- The builder: `getObjectMaterial` (FUN_140841a50)
- Shadow caster variant (FUN_140840da0, Verified (decompiled))
- Colour masks (clothing, Verified (decompiled))
