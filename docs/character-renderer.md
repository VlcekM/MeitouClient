# Character renderer

`Meitou.Game.Rendering.Characters.CharacterRenderer` partials: core content/material/lifetime; Draw cull/LOD/batches;
Motion previous state; Portraits offscreen portraits; Stats diagnostic rings. Inputs are
interpolated simulation snapshots and data appearances, not simulation-owned GPU resources.
Game assembly rules: [characters](characters.md), [animation](animation.md).

## GPU layout and shading

### Hair, cloth, skin and eyes (Meitou `chars2` switch, off by default)

**Water beads** (`wetBeads`, the Advanced materials part `Water beads`, bit 32; engine choice, the game has a wet film only): where the weather or the water's mark leaves a surface wet, drops stand on it. The drops are the wet world's ([weather.md "Wet world"](formats/weather.md), `wetDrops`): the character hands it the mesh's own space (`vBind`, the modelled position; the mesh's X and Y axes in the world come as `vAxisX` and `vColour`) times the mesh's scale (`vAxisX`'s length), so the drops stay on the body as it moves and have one size on any mesh. The share of cells holding a drop follows the wetness, less on what absorbs (cloth: the game's absorbance); rain wets what faces up. Not on hair or eyes, not under the water. **Verified** by pictures, t_flats_shore: after a swim (blades, leather, human and Shek skin, the part on and off) and in forced rain (a face at three times 2 s apart: drops grow, run down with a track, none vanishes at once).

## Content, LOD and memory

### Bone anchors (Meitou engine choice)

`CharacterInstance.Anchor` (`CharacterAnchor`: carrier key, bone name, offset and rotation in the bone's frame) places a character's node on another character's bone as that one is drawn in the same frame: node position = the bone's world position + (carrier rotation x bone model-space rotation) x offset x carrier size, node rotation = carrier rotation x bone rotation x the anchor's rotation. The anchored character keeps its own size, pose, gear and bounds. `PoseLive` poses every character first, then `PlaceAnchored` moves the anchored ones (world matrix, bounding sphere, motion vectors) and places their parts; a carrier whose pose is held by the thinning anchors on its held palette, and an anchored carrier is placed before what it carries (4 deep). While the carrier is not drawn in the world that update (out of reach, hidden, look not built) or lacks the bone, the instance's own `Position` / `Rotation` draw it; they always feed the gather's culling. Used by the shoulder carry ([carry-loot](game/carry-loot.md)); code mods set it through `CharacterCold.DrawAnchor` ([modding](modding.md#registration-surface), issue #602). **Verified** (throwaway check, male skeleton, 40 clips): the anchor's node equals the simulation's `CarryPose` placement within 0.01 units; not seen in game.

## Remaining limits

**See-through items** (Meitou, a mod's `opacity map`): [render-objects](render-objects.md#see-through-items-meitou-issue-562).

**Gear blood** (Meitou, optional `gearblood`): material flag `GearBloody` (1 << 29) rides in the item's hair fields (colour, scales, blade axis); instance flag bit 30 `WeaponBlood` puts a weapon's colour and amounts in the w of matrix rows 0 and 1, worn items' body amounts in all four as for the body. See [blood-decals.md](game/blood-decals.md#on-weapons-and-gear-meitou).
