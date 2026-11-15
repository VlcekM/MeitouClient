# Character rendering reference

Mesh and character preview modes are not current viewer commands. The supported character
renderer is [character-renderer.md](character-renderer.md); world-viewer usage is in
[viewer.md](viewer.md). Source reference: Git tag `model-viewer-last`.

Authoritative game rules: [characters.md](characters.md), [animation.md](animation.md),
[Ogre meshes](formats/ogre-mesh.md), [skeletons](formats/ogre-skeleton.md) and
[runtime materials](formats/runtime-materials.md).

## Retained evidence

These labels describe the checks made with preview screenshots, not automated renderer tests.

- **Verified with screenshots (2026-10-04):** `human_male` binding pose and `ninjarun` at 0.2 s,
  `pack_beast` `walk`, and `Iron Clad Jacket_F` attached to `female_skeleton`. Skeleton overlays
  checked skinning alignment; the jacket's own skeleton link is broken.
- **Verified with screenshots (2026-10-04):** Dust Bandit seeds 1-6 and Hungry Bandit seeds 1-4
  checked generated clothing, weapons, race/sex and appearance variation. Ruka and Beep checked
  race-specific head/body inputs. Height 80/120, muscle/starvation, missing limbs/hidestump and
  forced LOD 0/1 checked body shape, seams and transitions.
- **Observed** (Kenshi `character.hlsl`, preview screenshots): sample the head at body UV plus
  `(0,1)`, use border addressing with transparent black, and read body normal components `.wy`.
  `human_male` showed the correct face with these inputs.
- **Observed** (screenshots): weapon MATERIAL_SPECS textures `01_HI.dds` / `12_HI.dds` mapped
  sensibly onto both `katana05` and `chopper01_bare`, supporting a shared weapon UV convention.
- **Verified by screenshots:** right-handed coordinates, Y up, counter-clockwise fronts,
  textures as stored; human height about 19.5 units, loaded katana length 12.8.
  See [viewer.md](viewer.md#diagnostics-and-references).

No automated test exercised the preview `Animator`, `CharacterScene` or renderers. Screenshot
checks cover the named cases only; they do not establish parity for every race, item or animation.
