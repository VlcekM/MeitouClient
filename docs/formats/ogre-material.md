# Ogre material and program scripts

Script compiler/source tests and original resource registration/loading binary research.
Lexical/import/inheritance rules are compatibility behaviour, not conventional C-like parsing.
`OgreMaterialLibrary` resolves scripts; runtime cloning: [runtime-materials](runtime-materials.md).

## Compilation and resources

Concrete nodes/properties are line-based: one property consumes remaining line tokens; object
block follows class/name/values/bases, abstract objects serve only as bases. Per-file passes:
imports → ordered inheritance/child pairing → variable expansion → translator. Base properties
copy before own properties; class/name/index matching governs overridden children. Scope/global
variable rules and malformed input differ from normal text substitution.

Material/technique/pass/texture-unit model retains script defaults for blend/cull/depth/alpha/
program/texture/filter/address parameters. Runtime sets named texture UNITs, not aliases.
Resource registration order/group and collision handling matter: resources.cfg, base/mod folders,
translations, imports and listeners are not one flattened filesystem search. Preserve declared
base-name/path/group identities and initialization timing. Original programs/HLSL are reference
facts only; own rendering compiles own GLSL. Unsupported script/resource features remain explicit.

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

### Material model (Observed (source); `Pass_accessors_follow_the_translator`, `Pass_defaults_match_ogre`)

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

## Open questions

- Whether Kenshi's Ogre build matches the v2-0 lexer/parser quirks above (the DLL is 2.0 "Tindalos", but the
  quirks themselves are untested; test with a mod).
- Whether the SkyX plugin registers resource locations of its own.
- The terrain and character-body material builders (see [runtime-materials.md](runtime-materials.md)).

Answered (see the sections above): which duplicate definition wins (the first parsed; Kenshi's collision
listener returns false), recursion (none for `resources.cfg`; mod folders are scanned and every sub-folder
added), `OgreCore.zip` (registered) and Caelum (not registered), group initialisation order (`GUI`, then
ordinal name order), and how records map onto materials.

## Expression reference

- `resources.cfg` unless marked): `ConfigFile::load("resources.cfg", "\t:=", trimWhitespace = true)`; `addResourceLocation(path, type, group, recursive = false, readOnly = type != "FileSystem")`; `Zip=./data/meshes/OgreCore.zip`
- Mods and translations unless marked): `readOnly = false`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- Material model (Observed (source); `Pass_accessors_follow_the_translator`, `Pass_defaults_match_ogre`)
- `resources.cfg` (Verified (decompiled) unless marked)
- Mods and translations (Verified (decompiled) unless marked)
- Lookup (Observed (source) for Ogre's rules)
- Group initialisation (Verified (decompiled))
- Loading listener (Verified (decompiled))
- Base game (Verified 2026-10-04)
- Mesh materials (Verified 2026-10-04, `Base_game_mesh_materials_are_mostly_not_script_materials`)
- Textures (Verified 2026-10-04, `Base_game_material_textures_mostly_exist`)
