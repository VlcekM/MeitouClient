# Blood decals

Blood left on the terrain: the game's `TerrainDecalsManager` (exe) and the terrain plugin's blood
patches (`Plugin_Terrain_x64.dll`). Implementation: `TerrainDecalStore`, `TerrainDecalCells`
(Meitou.Data), `BodySystem.BloodTrail` / `CombatSystem.Splash` (marks), `BloodDecals`,
`TerrainDecalRenderer` and the terrain colour shader. Settings: [settings.md](../formats/settings.md#graphics-decals-and-effects);
save layout: [save.md](../formats/save.md#folder-and-state-mapping).

## Cells

**Verified** (`Terrain::getPatchInfo`; keys of two saves): decals are kept per terrain quadtree node
of level 10: 1024² cells of 294912 / 1024 = 288 units. Key = `1 << 20` | column bits on the even
bits | row bits on the odd bits. A decal is stored once in every cell one of its four corners falls
in, each copy placed relative to its own cell (checked: placing the saves' decals again reproduces
their cells and local places).

## Making a decal

Sources (`FUN_140652000` medical update, `FUN_140793b20` hit), all only with `Blood` on and the
character out of the water (`FUN_1405c7fd0` = water state 0); colour = race `blood colour` (FCS
default #800000), alpha 1 unless stated:

| Case | Decal | Rule |
| --- | --- | --- |
| bleeding > 0, speed 0 (the carrier's when carried) | pool | grows the character's pool (`FUN_1408dcfa0`, below); remembers the place |
| bleeding > 0, moving, not carried | smear, size 0.2 | once more than 4 units from the last place; along the velocity; alpha = clamp(bleeding, 0, 1) |
| bleeding > 0.01, otherwise (carried) | pool, size min(0.5, bleeding × 0.1) | build-up += rand × bleeding × dt × 10; past 1: a drop if more than 1 unit from the last place, then build-up 0 |
| a hit (`FUN_1406508d0`), a severed limb (`FUN_14064edc0`) | splash, size clamp(min(1, (cut + blunt + pierce) / 100), 0.05, 0.5) | a direction (a character vector at `+0x640`, `+0xd0`), flipped or turned ±90° by a hit-side flag (2, 4, 8), turned by a random ±50°; placed up to 20 units along it |

**Pool growth** (**Verified**, `FUN_1408dcfa0`): per character a token (place, size). Increment =
bleeding × scaled frame dt × 0.2. No token: size = increment. Within √10 units (X/Z) of the token:
size += increment / max(1, 10 × size), capped at 2 (a size past 2 is set to 2). Farther: the token
moves there, size = increment. The pool decal at the token's exact cell place is replaced when its
stored half size (size × texels × 10, a short) differs from the new one: removed from that cell and
the cells its corners reach, then made again at the same turn (`FUN_1408dc6b0`, so at the end of
each list); none there: a new pool with a random direction. The cap is **Verified** (float
constants read from the exe: 2.0, 10.0, 0.2). A capped pool on a 512² texture is 2 × 512 × 288/4096 × 2
= 144 units across (half a cell, about 15 characters), reached after 100 / bleeding seconds of standing
still (from the growth rule), so a quickly bleeding body does reach it. One pool per character at a
time, but a new one each time it stands more than √10 units from the last.

## Ageing and drawing

**Verified** (`FUN_1408dd860`, `FUN_1408de890`, `FUN_1408dec40`, `Terrain::createBloodPatches`,
`TerrainBlood` material): every update life −= the scaled frame time (0 paused); a decal at life ≤ 0
is removed unless its cell is on a texture now. The cells within `Decal Range` of the camera get up
to 8 `Kenshi_Decal_Texture_n` render targets of `Decal Resolution`² (Ogre format 0x1c, **Observed**
to be `PF_R8G8B8A8` from Ogre's enum). A target is cleared to 0 when it gets a new cell; decals are
drawn as quads (centre `lx·2/1000 − 1`, half sizes / 10240 in clip space, rotated by angle − π/2)
with `texture × colour` (the unit quad runs −0.5..0.5, so its full width is the half size / 10240 of clip space, 2 clip = 288 units: the
same 288/4096 per texel and size as the placing above, **Verified** by the two agreeing; our store keeps the half sizes ×10 as the game does), colour alpha = min(alpha, life), alpha-blended colour and additive alpha.
The terrain draws the cell's nodes again with `TerrainBlood`: depth equal, the texture's colour
alpha-blended over the G-buffer albedo, gloss kept. The game ranks the candidate cells by decal
count and distance (**Observed**; we take the nearest). Consequence (**Observed**, vanilla battle
footage): a big battle's blood ends at a straight cell edge, and the cells past the eighth age out
unseen.

## On the body and on severed limbs

**Meitou**
- Stump: a limb-replacement item without a material wears the body material at the full amount (`BloodFull`): a severed part is far below its health.

## Blood on surfaces (Meitou)

- Each mark is also kept with its 3D place (upper floors included: the terrain path drops marks 20 units above the ground), saved with the terrain's ([save.md](../formats/save.md#folder-and-state-mapping) "Terrain decals"; a loaded pool no longer grows). A pool grows by the store's rule, kept apart.
- Lifetime, **Verified** for the rule (the terrain's, above) and **Observed** for the symptom (hidden run, a wall splash and a floor pool kept in view past 2970 ticks): a mark has the same 99 s life, and as a drawn cell keeps its dead decals, one whose life ran out is removed only while it is out of `Decal Range` of the camera (`AgeSurfaces`; saved with life 0 while it stays). Blood in view never goes by itself, as the terrain's beside it; the colour alpha is the mark's own (no fade in the last second: the terrain draws a decal once, at its full alpha). Before, a mark faded out 99 s after it was made while the camera stood on it.
- Drawn as a screen-space decal after the opaque scene, before the characters and the water, so they never take blood: a sphere round the decal's square, each scene pixel in it rebuilt from the scene depth, its normal from the depth's slope, the texture projected along that normal. The result multiplies the lit scene colour.
- Pools, drips and smears only on up-facing pixels (normal Y > 0.8) within 0.4 units of the mark's height. Splashes are centred 0.9 above the feet (above the terrain under the mark on the ground) and take any surface facing the mark, fading over the outer 45 % of the radius.
- The terrain keeps its cells' blood: the depth is copied after the terrain's draw and again after the static objects; a pixel whose two copies hold the same bits (no object drew over the terrain there) is skipped. An exact test: comparing the scene depth with the terrain copy within a tolerance (2 cm plus 0.05 % of the distance) took floors and rocks lying almost level with the ground for terrain and dropped their blood, and a height-map test failed (the terrain LOD departs from the height map by up to the pixel error). A second copy after the static objects, before the foliage, skips every pixel nearer than it (grass, trees, debris sway or move; the decal would slide over them). Any mesh drawn with the static objects but animated (rotors, doors) is still decorated.
- Debug command `bloodmark <splash|pool|smear|drip> <x> <y> <z> [size] [RRGGBB] [dirx dirz] [--count n]` puts a mark (n spread over 60 x 60 units) for controlled repros.

## On weapons and gear (Meitou)

- Weapons: a melee hit with a weapon (not fists, not bolts) that draws blood (the `Splash` rule: bleeding race, not in water) carries the attacker (`BloodMark.Attacker`); `BloodDecals` hands it to `CharacterRenderer.WeaponHit`. Per character (by draw key, in `_weaponBlood`) the renderer keeps amount, wetness, colour (mixed with the old by weight), which weapon (back or hip). A hit adds 0.15 + splash size (0.05-0.5); wetness drops to 0 over 150 game s (colour darkens/browns, gloss 0.94 to 0.25); then the amount flakes off at (1 - wet) / 1500 per s; water over the hand (1.1 x depth above the feet over 8) washes it in 8 s; rain (`WeatherSurfaces.Wetness`) in 300 s of full rain. Stepped only while the character is drawn, from the elapsed game time.

## Bloody footprints (Meitou)

The `footprints` switch (Meitou, default; Faithful: none, as the game; `BloodFootprints`), not a game behaviour. A walking character's feet pick up the colour of a wet terrain pool or splash (its opaque inner ellipse) and each foot leaves up to four progressively lighter prints on dry terrain. The prints expire in game time and stay out of both save formats; switching it off clears the prints and the blood on the soles. It needs the `Blood` setting.

- Masks: an equipped boot's sole outline (the convex hull of its mesh's lowest band of vertices, `FootprintSoles`), else generic boot and barefoot masks (`FootprintTexture`). The foot and toe bones place and turn a grounded print; a distance-based stride when the pose is not available.
- Drawn by the surface-blood pass on terrain pixels only (scene depth equal to the terrain's), so the print's detail does not depend on `Decal Resolution`. The terrain depth copy runs while a print is within `Decal Range` and in view.
- `Blood decals drawn` (Tab panel, 64 to 2048, default 512): the most surface decals and prints drawn in a frame, the nearest kept.
