# Character renderer

The native Vulkan renderer for characters (stage 4 of [simulation.md](simulation.md)): `src/Meitou.Rendering/Characters/`. The
simulation (or the `--crowd` harness) fills a `CharacterDrawList` every frame; the renderer owns everything on the GPU side. Inputs it
reproduces are described in [characters.md](characters.md), [animation.md](animation.md) and [character-viewer.md](character-viewer.md).

## Test harness

`--world --town "The Hub" --crowd N [--crowd-seed S] [--crowd-time T]`: N characters generated with `CharacterGenerator` from the CHARACTER
records of the town's `residents` and `bar squads` (SQUAD_TEMPLATE `choosefrom list`, weighted), standing on a sunflower spiral round the
start point, even indices idling (`idle_stand_relax`) and odd ones walking in place (`walk lower` + `walk upper`). Stills use a fixed
time (default 0.35 s); the interactive viewer uses a clock. Without `--crowd` nothing of the renderer is created and the world image is
unchanged (image-diff 0 px against the pre-change baseline, `--upscaler off`).

Env (all read once at start, in `Characters/CharacterSwitches.cs`):

| Variable | Effect |
|---|---|
| `MEITOU_CHARACTER_LOG=1` | one line per part of every appearance built (levels, triangles, material) |
| `MEITOU_CHARACTER_LOD=faithful` | keep the mesh files' own LOD levels instead of the generated ones (Meitou mode, default; see Engine choices) |
| `MEITOU_CHARACTER_MORPH=0` | no face poses (the GPU morph of the body mesh) |
| `MEITOU_CHARACTER_MOTION=0` | leave the characters out of the temporal upscalers' motion vectors |
| `MEITOU_CROWD_ANIMATE=1` | the `--crowd` harness animates with a real-time clock even for stills (otherwise they hold `--crowd-time`) |

On exit a `characters ...` line gives counts per LOD level and timings (means of the last 10 frames, 40 for the shadow cascades; kept in rings of 64 samples).

## Design

- **Per appearance** (by reference): a `CharacterAsset` with the rig, body shape, parts (body, head, hair, beard, clothing, armour,
  weapons) and their materials. Meshes are cached by (path, bone count) and shared; textures through one `WorldTextureCache`.
- **Pose**: CPU, in parallel over characters, straight into the mapped bone storage buffer (binding 6; 64 B per bone skin matrix). Layers
  are added **cumulatively** (Kenshi switches every character skeleton to cumulative, [ogre-skeleton.md](formats/ogre-skeleton.md#blending-in-kenshis-ogre); the
  simulation keeps each body half at a total of 1); override bones in a second pass; a relocating clip's `Bip01` track keeps only its height
  (the simulation moves the character by the rest); body-shape scales and the posture libraries (at weight 1, on top) applied as in the removed viewer. Bone-attached items (weapons) take the bone's world matrix times the part offset.
- **Draw**: instanced per (mesh part, LOD level, sidedness). Per instance: a 4x4 matrix (vertex inputs 7-10) and a `uvec4`
  (bone base, material slot, flags). Materials are a table in an SSBO (binding 7, 256 B records, bindless texture indices), so one
  instance buffer and few draws cover a whole crowd. Sphere culling and LOD selection (`MeshLod`) on the CPU, per view.
- **Shading**: one shader with three modes (item, body, hair) that layers vests, head at uv+(0,1), skin tone, hair/beard overlays and
  dye, then the shared mesh lighting tail (`kenshiLight`, atmosphere), so characters are lit like the world.
- **Shadows**: the same instances are drawn into the existing cascades with a depth program that only does the cut-out test.

## Engine choices

- **Generated LOD** (Meitou mode, default; `MEITOU_CHARACTER_LOD=faithful` keeps the files' own levels): every mesh gets four reduced levels by edge collapse
  (`MeshSimplifier`: quadric error, open edges and skinning-weight changes penalised, no flips), at half, a quarter, an eighth and a twentieth of its triangles,
  used from 5, 11, 22 and 45 radii of the part (value = distance minus radius, the `distance_sphere` rule). The levels only re-point corners at existing vertices, so one
  vertex buffer, the weights and the morph slots serve every level. Reason: the files' own levels are few and far (`human_female` has none; the male body's only
  level starts at 200 units), so 500 characters were 3.5M triangles. Shadow cascades use LOD value x2 (`ShadowLodBias`).
- **Pose rate by distance**: characters beyond 120, 300 and 600 units are posed every 2nd, 4th and 8th frame (staggered by key); the last palette is reused.
  State per `Key` (`CharState`) holds the palette, the one before it, bone-attach matrices and last frame's model matrix.
- **Texture memory**: character textures are loaded on demand (deferred) with the world's mip streaming (`MipStreaming` through `WorldTextureCache.Mips`): a texture
  loads without the top levels its nearest user cannot show, and finer when something comes near. A governor scales every need up by 1.6x (up to 512x) while the cache
  is above 90% of its mark (`MarkMb`, which the `VramGuard` budget lowers), back down under 50%. A still waits for everything once the camera is known.
- LOD radius is bounded by the model's real extent (`min(stored radius, model.Radius)`): `human_female.mesh` stores 116 for a body about 20 units tall.
- No root motion: the walk loop's root translation starts and ends in the same place (**Observed** on `walk lower`), so characters walk in place and the simulation moves them.
- **Face poses**: the vertices any pose moves get a slot (1..n, in the vertex's colour X as raw bits; characters use no vertex colours); an appearance's offsets, the poses
  summed at their weights as Kenshi bakes them (`CharacterShape.BakeWeight`), live in one growing storage buffer (`MorphArena`, binding 8), identical sets stored once
  (squads share faces). The vertex program adds the offset before skinning; normals are not changed (the poses carry none, **Verified** in ogre-mesh.md). `MEITOU_CHARACTER_MORPH=0` turns it off.
- **Motion vectors**: `PostProcess.ObjectMotion` (shared with the grass through one hook): the near slice's characters drawn again with last frame's palette (binding 9) and
  model matrices (instance locations 12 to 15) into the motion target (red, green), where the near depth matches. `MEITOU_CHARACTER_MOTION=0` turns it off.
  A character not posed this frame has no bone motion.

## Findings

- **Observed**: `human_male.mesh` has 23 Ogre poses (cheekbones, mouth, nose, brow, eyes, jaw bite), all on the one submesh, none with
  normals, together moving 2986 of its 15504 vertices, largest offset 0.18 units.
- **Observed** (RTX 4070, shared and noisy; `--distance 260 --pitch 30`, 1600x900), before generated LOD: 500 characters 3.5M triangles per view, colour pass 2.7 ms,
  shadow 1.8 ms per cascade call, CPU update 1.2-1.8 ms (pose 0.65-1.1) and draw 0.4 ms; 459 MB of textures and 72 MB of meshes.
- **Observed**, after generated LOD, pose rate, morphs and motion: 500 characters 0.6M triangles, colour 0.5-0.8 ms, shadow 0.15-0.25 ms per cascade call, CPU update 0.9-1.5 ms
  (pose 0.3), draw 0.5 ms; 50 characters colour 0.1-0.5 ms, update 0.2 ms. At a far camera (900 units) textures drop from 461 to 143 MB. With a forced integrated
  GPU (`MEITOU_FORCE_INTEGRATED=1 MEITOU_VRAM_BUDGET_MB=3300`) the guard stops loading at its budget and the rest draw the stand-in.

## Left

A coarser or merged shadow caster for far characters; GPU culling; the guard's stand-in textures for characters (a small shared diffuse colour rather than grey);
generated LOD as a Faithful/Meitou switch (`Enhancements`, `--faithful`) instead of `MEITOU_CHARACTER_LOD` (needs the shared `Enhancements.cs` / `WorldFrame.cs` after the merge); normals for the face morph; the original's per-frame animation update budget (docs/game/game-loop.md) instead of the distance-based pose rate.

## Code layout

`CharacterRenderer` is a partial class: `CharacterRenderer.cs` (fields, `Update`, materials, `Settle`, teardown), `.Draw.cs` (culling, LOD choice, batches, the recorded draw job), `.Motion.cs`
(`AttachMotion`: the near slice's hook into the post chain, called once per frame by `WorldFrame`; `DrawMotion`), `.Stats.cs` (timing rings, the `characters ...` line). The texture
governor is `CharacterTextureBudget`. `CharacterContent` is disposable and owns the mesh buffers, textures and the morph arena. The program wrapper and the sphere test are
shared (`ReflectedProgram.cs`; the objects renderer still has its own copy of both). `CharacterShaders` names the set 0 bindings (bones 6, materials 7, morphs 8, last frame's bones 9) and
the material's texture slot count (20) and builds the GLSL from them; the push block's `depthIndex` is the near depth texture's bindless index in the motion pass and 0 otherwise
(the colour fragment's flat-colour branch on it is unreachable and kept as it is). `CharacterLayoutTests` pins the sizes of the structs the shaders read.
