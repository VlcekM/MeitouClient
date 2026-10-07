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

Env: `MEITOU_CHARACTER_LOG=1` prints one line per built part. On exit a `characters ...` line gives counts per LOD level and timings.

## Design

- **Per appearance** (by reference): a `CharacterAsset` with the rig, body shape, parts (body, head, hair, beard, clothing, armour,
  weapons) and their materials. Meshes are cached by (path, bone count) and shared; textures through one `WorldTextureCache`.
- **Pose**: CPU, in parallel over characters, straight into the mapped bone storage buffer (binding 6; 64 B per bone skin matrix). Layers
  are averaged per the animation rules; override bones in a second pass; body-shape scales and the posture libraries applied
  as in the removed viewer. Bone-attached items (weapons) take the bone's world matrix times the part offset.
- **Draw**: instanced per (mesh part, LOD level, sidedness). Per instance: a 4x4 matrix (vertex inputs 7-10) and a `uvec4`
  (bone base, material slot, flags). Materials are a table in an SSBO (binding 7, 256 B records, bindless texture indices), so one
  instance buffer and few draws cover a whole crowd. Sphere culling and LOD selection (`MeshLod`) on the CPU, per view.
- **Shading**: one shader with three modes (item, body, hair) that layers vests, head at uv+(0,1), skin tone, hair/beard overlays and
  dye, then the shared mesh lighting tail (`kenshiLight`, atmosphere), so characters are lit like the world.
- **Shadows**: the same instances are drawn into the existing cascades with a depth program that only does the cut-out test.

## Engine choices

- LOD radius is bounded by the model's real extent (`min(stored radius, model.Radius)`): `human_female.mesh` stores a bounds radius of
  116 for a body about 20 units tall, which kept it at LOD 0 forever.
- No root motion: the walk loop's root translation starts and ends in the same place (**Observed** on `walk lower`), so characters walk in place and
  the simulation moves them.
- Face poses (morphs) are **not** drawn yet: faces use the neutral mesh.

## Findings

- **Observed**: `human_male.mesh` has 23 Ogre poses (cheekbones, mouth, nose, brow, eyes, jaw bite), all on the one submesh, none with
  normals, together moving 2986 of its 15504 vertices, largest offset 0.18 units. A GPU morph would need a sparse
  per-vertex slot table and a weight buffer per appearance (`CharacterAppearance.PoseWeights`), not a per-appearance baked mesh (VRAM).
- **Observed**: `human_female` has no LOD levels; clothing LOD starts at distance 400, body L1 at 200. At 500 characters about 3.5M
  triangles are drawn per view, nearly all at L0.
- **Observed** (RTX 4070, shared and noisy): 50 characters cost about 0.25 ms colour pass, 0.15 ms per shadow call, 0.4 ms CPU update; 500 cost 2.7-10 ms
  colour, 1.8-7 ms per shadow call, 1.3-6 ms CPU update. 500 characters use about 460 MB of textures and 72 MB of meshes: an integrated GPU
  needs mip streaming and coarser LODs first.

## Left

Face morphs; generated LODs for meshes without them and a coarser shadow LOD; texture mip streaming for characters; motion vectors
for TAA/FSR/DLSS (previous bones per `Key`, an `ObjectMotion` pass like the grass); GPU culling.
