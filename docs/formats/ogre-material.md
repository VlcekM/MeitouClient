# Ogre material scripts (`.material`, `.program`)

Sources:
- **OGRE source** (MIT): Kenshi runs Ogre 2.0, whose v1 material system reads text scripts with the
  generic ScriptLexer / ScriptParser / ScriptCompiler and per-object translators. The rules below follow
  ogre-next branch `v2-0`: `OgreScriptLexer.cpp`, `OgreScriptParser.cpp`, `OgreScriptCompiler.cpp`,
  `OgreScriptTranslator.cpp`, `OgreTextureUnitState.cpp`, `OgreResourceGroupManager.cpp`.
  `Meitou.Data.Ogre.OgreScript*` / `OgreMaterial*` follow the same structure.
- Rules taken from the source are **Observed (source)**: they describe Ogre v2-0, not checked against
  Kenshi's own `OgreMain_x64.dll`. Where a synthetic test pins our port to the rule, the test is named.
- **Verified 2026-10-04** (`OgreMaterialTests.Compiles_every_base_game_material_script` and the two
  tests after it, `meitou-tools materials`): every base-game `.program` and `.material` compiles; the
  counts in "Base game" below come from those runs.

The same grammar is used by `.compositor`, `.particle` and `.os` scripts; only materials and GPU
programs are interpreted here.

## Lexing (Observed (source); `Lexer_*` tests)

- Files are bytes; no encoding handling. Base-game scripts are ASCII except two with Latin-1 bytes in
  comments (`CaelumSample.material`, `SkyX.material`); none has a BOM (Verified by scanning). Both
  `\r\n` and `\n` line ends occur. We read files as Latin-1 so every byte maps to one character.
- Tokens: words, `"quoted phrases"`, `$variables`, `{`, `}`, `:`, and newlines. Runs of newlines collapse
  into one newline token. Spaces, tabs and `\r` separate tokens; `\r` and `\n` also count as newlines.
- A word ends at whitespace, a newline, `{`, `}` or `:`. **But** the first character of a word may itself be
  `{`, `}` or `:`: `}x` is one word, while `x}` is `x` and `}`.
- Comments: `//` to end of line, `/* ... */`. They start **only between tokens**: inside a word `//` is
  just text (`a.dds//x` is one word). A colon ends a word, so in `http://x` the `//` does start a comment.
- A block comment emits no newline, so text on both sides of a multi-line `/* */` joins into one line.
- Quotes: `\"` inside quotes is a literal quote; any other `\c` stays as `\c`. A quote may span lines. An
  unclosed quote is an error that stops the whole file.
- Line numbers count `\r`, `\n`, and `\r\n` once.

## Concrete tree (Observed (source); `A_property_runs_to_the_end_of_its_line`)

- A word at the start of a line starts a node; every other token on that line becomes its child. A line
  ends the node unless the next non-newline token is `{`, in which case the block belongs to it.
- So there is **no property separator**: `pass { lighting off depth_write off }` is one property
  `lighting` with three values (which the translator then rejects). Base-game files keep one property per
  line.
- At the start of a line, quotes, variables, `{` and `:` are ignored silently.
- `import <target> from <file>`: the word in the middle is not checked. A quoted target is unquoted with
  the wrong length (the `import` token's), so `import "Foo" from ...` asks for `Foo"`; only the file name
  is unquoted correctly. Malformed `import` / `set` / `:` lines stop the file.
- `set $name value`: a variable assignment (value: one word or quoted phrase).
- `name : Base1 Base2`: the words after `:` are bases. **Base names keep their quotes**, so a quoted base
  is never found.

## Objects and properties (Observed (source); `Compiles_objects_properties_and_values`)

A node whose last two children are `{` and `}` is an **object**: `class [name] [values...] [: bases] {}`.
Otherwise it is a **property**: `name values...`. The name is the first word after the class, except
for `pass` inside a `compositor`, `emitter`/`affector` inside a `particle_system` and `texture_source`
inside a `texture_unit`. Values are the words before `:` or `{`, e.g. `hlsl` in `vertex_program X hlsl`.
`abstract material X {}` marks X abstract: usable as a base, never created itself.

An unclosed `{` makes Ogre read the object as a property and ignore it; we report `UnclosedBrace`.

## Compiling a file (Observed (source))

Each file is compiled on its own, in three passes:

1. **Imports** (`Imports_bring_bases_from_other_files`): `import * from "x.material"` loads `x.material`
   (by bare resource name, like any resource), processes its own imports and inheritance, and makes all its
   top-level objects available **as bases only**; `import Name from ...` makes just that object
   available. Imported objects are not created by the importing file. A missing file is ignored
   silently (we report `ImportNotFound`). Ogre has no guard against cyclic imports (it would recurse
   forever); we skip a file already being loaded.
2. **Inheritance** (`Derived_material_overlays_base_and_its_own_properties_win`,
   `Unnamed_objects_match_by_position_and_only_against_unnamed_base_objects`,
   `Wildcard_names_apply_to_every_matching_base_object`): for each object, each base in order is looked
   up among the file's own top-level objects, then the imported ones (the last object of that name
   wins). **Nothing else is searched**: a base defined only in another, un-imported file is an error
   (`ObjectBaseNotFound`) and the object is built without it. The base is overlaid onto the object:
   - Base properties are copied **in front of** the object's own, so translation sees the base value
     first and the object's own value last (and the last wins).
   - Base child objects are paired with the object's child objects: first by same class and same
     non-empty name; then, for those still unpaired, by position, but only with **unnamed** base objects
     of the same class (searching from the position of the last name match). A named child that matches
     no name can therefore still take an unnamed base object. A child name containing `*` is a wildcard:
     it is copied once per matching base object, taking that object's name.
   - Paired objects are overlaid recursively. Unpaired base objects are copied in, after the last
     paired child (or at the front).
   - Base variables are copied when the object does not define them.
3. **Variables** (`Variables_expand_from_object_scope_bases_and_globals`): `$name` in a value is replaced
   by the text of the nearest `set` in the enclosing objects, else a top-level `set` (the **first**
   top-level `set` of a name wins; inside objects the last one does). The text is re-lexed into words, so
   `set $c "1 0 0"` gives three values. A quoted phrase inside a value is an error that stops the file.
   Undefined variables are reported and dropped. Abstract objects are not expanded (their copies are).

Then each top-level, non-abstract object is created by the translator for its class. Base-game scripts
use **no** variables and **no** `abstract` (Verified by search); both are implemented for mods.

## Material model (Observed (source); `Pass_accessors_follow_the_translator`, `Pass_defaults_match_ogre`)

`material` > `technique` > `pass` > `texture_unit`, plus `*_program_ref` objects inside passes. The
translators apply properties in order, so the last valid one wins; an invalid one is ignored (an error is
logged). Names and keywords are case-sensitive.

| Level | Property | Meaning, default |
| --- | --- | --- |
| material | `receive_shadows`, `transparency_casts_shadows` | bool; true / false |
| material | `set_texture_alias alias texture` | after the material is built, every texture unit whose alias matches gets that texture. The **first** one of an alias wins (std::map insert), and base properties come first, so a derived material cannot change an alias its base sets (`First_texture_alias_wins_...`) |
| material | `lod_values` | LOD distances |
| technique | `scheme` | default `Default` |
| technique | `lod_index`, `shadow_caster_material`, `gpu_vendor_rule`, `gpu_device_rule` | |
| pass | `ambient`, `diffuse`, `emissive` | `r g b [a]` (alpha 1) or `vertexcolour`; defaults white, white, black |
| pass | `specular` | `r g b shininess` or `r g b a shininess` or `vertexcolour [shininess]`; default black, 0 |
| pass | `scene_blend` | `add` (one one), `modulate` (dest_colour zero), `colour_blend` (src_colour one_minus_src_colour), `alpha_blend` (src_alpha one_minus_src_alpha), or two factors; default one zero. `replace` is **rejected** by v2-0 |
| pass | `depth_check`, `depth_write`, `lighting`, `colour_write` | bool; all true |
| pass | `depth_func`, `alpha_rejection func [0-255]` | compare functions `always_fail always_pass less less_equal equal not_equal greater_equal greater`; defaults less_equal, always_pass 0 |
| pass | `cull_hardware` | `clockwise` (default), `anticlockwise`, `none` |
| pass | `cull_software` | `back` (default), `front`, `none` |
| pass | `polygon_mode` | `points`, `wireframe`, `solid` (default) |
| pass | `depth_bias`, `fog_override`, `iteration`, `max_lights`, `illumination_stage`, ... | kept raw |
| pass | `vertex_program_ref Name { param_named ... }` | each one replaces the previous; same for `fragment_`, `geometry_`, `shadow_caster_*_` |
| texture_unit | `texture name [1d/2d/3d/cubic/2darray] [mipmaps/unlimited] [alpha] [format] [gamma]` | |
| texture_unit | `texture_alias` | alias for `set_texture_alias`; if absent, the unit's name (TextureUnitState::setName) |
| texture_unit | `cubic_texture name combinedUVW` / `name separateUV` (files `name_fr/_bk/_lf/_rt/_up/_dn.ext`) / 6 names | |
| texture_unit | `anim_texture base n duration` (files `base_0.ext`...) or `frame1 ... duration` | |
| texture_unit | `content_type compositor <node> <texture> [index]` / `shadow` | filled at run time, no file |
| texture_unit | `tex_address_mode`, `filtering`, `tex_coord_set`, `mipmap_bias`, `tex_border_colour`, `binding_type`, `env_map`, ... | kept raw |

GPU programs: `vertex_program Name lang { source file  entry_point f  target ...  default_params { } }`
(also `fragment_`, `geometry_`, `tessellation_hull_`, `tessellation_domain_`, `compute_program`); a
`unified` program lists `delegate` programs and uses the first one the render system supports.

## Loading order (Observed (source); Kenshi's handling Unknown)

- Ogre parses scripts per resource group, pattern by pattern (`*.program`, `*.material`, `*.particle`,
  `*.compositor`, `*.os`), and for each pattern location by location. `FileSystem=` locations from
  `resources.cfg` are **not recursive** (when added the usual way; Kenshi's code not checked). Every
  matching file is parsed, including files with the same name in two folders.
  `OgreMaterialLibrary.LoadConfigured` follows this, taking groups in `resources.cfg` order. Ogre's
  `initialiseAllResourceGroups` walks its group map, a `std::map` keyed by name
  (`OgreResourceGroupManager.h`), so all-groups initialisation goes alphabetically; whether Kenshi
  initialises groups that way or one by one is **Unknown**.
- Materials and programs are looked up by name across all groups. A repeated name makes Ogre's
  `ResourceManager::add` throw (no collision listener in v2-0), which would end that file's compilation.
  The game works with 10 repeated material names, so Kenshi either catches this or installs a listener:
  **Unknown** which definition wins. `OgreMaterialLibrary` keeps the first and reports
  `DuplicateDefinition`.

## Base game (Verified 2026-10-04)

- 164 `.material` and 17 `.program` files under data/ (181 compiled, 0 failures). 252 top-level
  materials, 242 distinct names; 320 distinct GPU programs (124 vertex, 204 fragment definitions, one
  tessellation hull and domain); 11 `shared_params`; one `compositor_node` (in `interiormask.material`).
- Only diagnostics: 20 repeated names. Materials: `01-Default` in 7 files, `NoMaterial` in 3,
  `Standard_3` and `Material#131` in 2 each, all exporter leftovers. Programs: the two `Shaders.program`
  files (`character/meshes/`, `items/armour/meshes/`) define the same 9 programs; `atmospherefog.material`
  redefines `Deferred_VP_HLSL` from `deferred.material` (a comment there says importing it did not work).
- Every base resolves; every `vertex_program_ref` / `fragment_program_ref` names a defined program.
  31 materials and 71 programs use `:` inheritance; 13 `import` lines, all `import * from` except
  `import Deferred_RTT_Lighting from "deferred.material"`. 3 materials use `set_texture_alias` (Caelum samples).
- After inheritance: 258 techniques, 271 passes, 462 texture units. Most used properties: `texture` 268,
  `tex_address_mode` 221, `depth_write` 143, `filtering` 138, `scene_blend` 137, `lighting` 104,
  `content_type` 100 (render targets: `rt_interiormask` in particles, `global_gbuffer`, `rt_reflection`,
  HDR), `texture_alias` 84.
- Folders: 147 of the `resources.cfg` locations exist (17 listed folders do not; `Zip=./data/meshes/OgreCore.zip`
  holds Ogre's own `OgreCore.material` / `OgreProfiler.material`, not read yet). 166 of the 181 scripts are in
  them; outside: `materials/caelum/` (Caelum sky plugin samples, probably registered by the plugin:
  **Unknown**) and `character/meshes/Shaders.program`.
- The Kenshi shaders are in `materials/deferred/` (e.g. `StaticObject` in `objects.material`, `Building*`,
  `Character`, `Hair`, foliage, terrain), `materials/forward/`, `materials/post/`, `particles/materials/`.

## Mesh materials (Verified 2026-10-04, `Base_game_mesh_materials_are_mostly_not_script_materials`)

Submesh material names mostly **do not** name a script material:

- 3,748 submeshes use 114 distinct names; 12 are defined, covering 2,400 submeshes (64.0%).
- `StaticObject` alone covers 1,843 submeshes. It is the base building/object shader whose texture units
  have placeholder textures (`black.dds`, `flat.dds`).
- The other 11 defined names (`01-Default`, `08-Default`, `Material#84`, `NoMaterial`...) come from
  leftover 3ds Max exports next to the meshes (fixed-function passes naming `.bmp`/`.psd`/`.jpg` files).
- The 102 undefined names are exporter defaults (`default` 486 submeshes, `defaultMat`, `N-Default`,
  `Material#N`, `Standard_N`, `lambert18`, `blinn3`), an empty name (89 submeshes) and a few descriptive
  ones (`Coloured`, `Emissive`, `severed_leg_left`...).
- Not defined elsewhere either (Verified by text search on 2026-10-04): `Coloured`, `OutpostConcrete01`,
  `Mask`, `aaaaa`, `Rebel_Interior`, `Foundry_Temp`, `severed_leg_left`, `drifter_coat_mat`, `CaptureMat`
  appear in no `.material`, `.program`, `.compositor`, `.particle`, `.os`, `.pu`, `.xml`, `.layout` or
  `.cfg` file. Of these only `OutpostConcrete01` occurs in the game data (`gamedata.base`,
  `Newwworld.mod`), as part of texture paths (`OutpostConcrete01_DIF.dds`, `_NML.dds`) in records.
- No submesh has texture aliases (chunk 0x4200), so Ogre's per-mesh material cloning is not involved.

So Kenshi must assign materials itself. **Observed** (from `fcs.def`, not checked in the engine):
`MATERIAL_SPEC` records hold texture maps and a "material type" (`BuildingShader`: default, alpha,
foliage, dual, emissive), matching the script materials `Building`, `Building_Alpha`, `Building_Dual`,
`Building_Emissive` (all `: StaticObject`); clothing and weapons have their own material records.
How the engine picks the script material and fills the units (presumably by `texture_alias` / unit
name: `diffuseMap`, `normalMap`, `metalnessMap`...) is **Unknown**.

## Textures (Verified 2026-10-04, `Base_game_material_textures_mostly_exist`)

- 289 file references from texture units, 155 distinct names; 124 (80.0%) exist as a file of that
  name (case-insensitive) somewhere under data/; 110 of 140 within the `resources.cfg` folders only.
- The missing ones: exporter leftovers (`ATLAS1.psd`, `Door_Texture.bmp`...), Caelum samples
  (`TudorHouse.jpg`, `Starfield.jpg`...), `blask.dds` and `cloudy_noon.jpg` in `rtticons.material`,
  names with a folder (`textures/woo001.jpg`, `texture\wall1.dds`), a few particle textures
  (`Haboob_Finger.png`, `SteamRise01.png`, `TwisterDust_Large.png`) and two unquoted names with spaces:
  `texture Copy of Dplate2.jpg` reads as texture `Copy` (other words go to the type/format slots).
- 173 units name no file: 100 `content_type` units plus units given a texture at run time (e.g.
  `Building_Dual`'s `diffuseMap2`, presumably filled from a `MATERIAL_SPEC`'s `texture map 2`).

## Open questions

- Which duplicate material/program definition Kenshi keeps, and whether it catches Ogre's exception.
- Whether Kenshi's Ogre build matches the v2-0 lexer/parser quirks above (test with a mod).
- How the engine maps `MATERIAL_SPEC` records onto script materials and texture units.
- Whether Caelum's folder and `OgreCore.zip` are registered at run time, and in which order groups are
  initialised.
