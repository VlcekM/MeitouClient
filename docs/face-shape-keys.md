# Face shape keys (runtime pose weights)

Mesh poses ("shape keys" in Blender, morph targets in Ogre) are the faces' sliders: a look's `PoseWeights` sum into one offset per vertex slot before
skinning ([character-renderer](character-renderer.md), [ogre-mesh](formats/ogre-mesh.md)). This page is the **runtime** layer on top: weights that change
every frame, set by a code mod through a small API. First user: [compat-characters-emotions](compat-characters-emotions.md).

## What a mod can do

`ModContext.AddFaceController(IFaceController)` ([modding](modding.md)). Once a frame, on the host's thread, `Update(in FaceFrame, FaceWeights)` runs for each
character the host puts in the draw list (not hidden indoors) whose look is built and whose **body mesh has poses**:

- `FaceFrame.Character`: the latest tick `CharacterSnapshot` (animations, combat, body status, bed...), read only. `FaceFrame.Seconds`: the simulation clock at
  the frame, tick plus interpolation (stands still while paused, follows game speed, a `step n` advances it by n ticks).
- `FaceWeights.Available`: the pose names of that mesh; `Set(key, weight)` (0..1, rounded to 1/128) for any of them. A key set **replaces** the look's own weight
  for that pose (0 switches a slider pose off); keys not set keep the look's. `Clear()` has run before the first controller; later controllers override earlier ones.
- Presentation only: nothing enters the world or `StateHash`; a throwing controller is disabled for the session (logged).

## Renderer

- `CharacterInstance.Face` carries the weights (one `FaceWeights` per character slot, refilled every frame by `GameHost.FaceFor`). Null or empty: the asset's own
  morph (`CharacterAsset.MorphBase`).
- `CharacterRenderer.FaceMorph`: per character (`CharState`), when the weights or the asset changed, `MeshMorphs.Compose` sums the look's poses (minus the replaced
  keys) and the weights into the character's own region of the `MorphArena` (a `FaceLease`: three rotating regions of SlotCount vec4s so a frame in flight never
  reads what this frame writes; released with the character's state, reused by size). Unchanged weights cost one array compare; the draw path reads
  `Live.MorphBase` in every view (colour, motion, shadow cascades, ray parts), so shadows, LOD levels and motion vectors follow with no extra pass (**Observed** in code; only the colour view was looked at). A decapitated body or a neck cap
  shares the same base (its parts are `Morphed` too). The vertex program is unchanged (adds the slot's offset before skinning).
- Parts of an asset with poses are now always `Morphed` (it was "the look has non-zero weights"), so a look with all-zero sliders can still be driven.
- A character off screen keeps its last morph (rebuilt when it is posed again). Races without the shape keys are untouched: `FaceFor` hands `null` for a mesh
  without poses, and a mod checks `Available` before setting.
- Limits: portraits are drawn from cached cells that redraw only when the look changes, so they show the face of the last redraw, not the live expression
  (**Observed** in code, not tested); normals and tangents are not morphed (as the game's own poses: no pose in base data or the installed mods has normal offsets).
  Per-frame motion vectors do not include the morph (a few pixels of face movement are drawn as static).

## Cost

Measured numbers (0.03 ms per rebuilt morph, 150 KB per expressive character): [compat-characters-emotions](compat-characters-emotions.md#cost).
