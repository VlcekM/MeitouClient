# Blood decals

Blood left on the terrain: the game's `TerrainDecalsManager` (exe) and the terrain plugin's blood
patches (`Plugin_Terrain_x64.dll`). Implementation: `TerrainDecalStore`, `TerrainDecalCells`
(Meitou.Data), `BodySystem.BloodTrail` / `CombatSystem.Splash` (marks), `BloodDecals`,
`TerrainDecalRenderer` and the terrain colour shader. Settings: [settings.md](../formats/settings.md#graphics-decals-and-effects);
save layout: [save.md](../formats/save.md#folder-and-state-mapping).

## Types and textures

**Verified** (`FUN_1408dfaf0` strings, `resources.cfg`): four types by the folder name of each
`FileSystem` location of the `Blood` resource group: 0 `line`, 1 `splash`, 2 `pool`, 3 `smear`.
A type's variants are every texture in its folders (install: line 3 at 1024×512, splash 7 at 512² +
1 at 1024², pool 5 at 512², smear 1 at 512×256). Each variant keeps its full-size texel dimensions
(the loader shifts them back up by the texture-quality level for names ending `.dds`). Type 0 has
no caller found.

## Cells

**Verified** (`Terrain::getPatchInfo`; keys of two saves): decals are kept per terrain quadtree node
of level 10: 1024² cells of 294912 / 1024 = 288 units. Key = `1 << 20` | column bits on the even
bits | row bits on the odd bits. A decal is stored once in every cell one of its four corners falls
in, each copy placed relative to its own cell (checked: placing the saves' decals again reproduces
their cells and local places).

## Making a decal

**Verified** (`FUN_1408dce50`, `FUN_1408dc6b0`): random variant; angle = atan2(dir.x, dir.z), the
texture's width axis along (sin, cos) and its height axis along (cos, −sin); half extents in world
units = size × texels × 288 / 4096 (**Verified**: `FUN_1408dfaf0` sets the factor at `+0x1c0` to cell 288 × 0.5 × 1/2048 = 288/4096; the draw agrees, below). Life starts at 99 (seconds; the medical and hit callers pass 99.0; the bleed pool's value is lost in the decompile, we use 99).

Sources (`FUN_140652000` medical update, `FUN_140793b20` hit), all only with `Blood` on and the
character out of the water (`FUN_1405c7fd0` = water state 0); colour = race `blood colour` (FCS
default #800000), alpha 1 unless stated:

| Case | Decal | Rule |
| --- | --- | --- |
| bleeding > 0, speed 0 (the carrier's when carried) | pool | grows the character's pool (`FUN_1408dcfa0`, below); remembers the place |
| bleeding > 0, moving, not carried | smear, size 0.2 | once more than 4 units from the last place; along the velocity; alpha = clamp(bleeding, 0, 1) |
| bleeding > 0.01, otherwise (carried) | pool, size min(0.5, bleeding × 0.1) | build-up += rand × bleeding × dt × 10; past 1: a drop if more than 1 unit from the last place, then build-up 0 |
| a hit (`FUN_1406508d0`), a severed limb (`FUN_14064edc0`) | splash, size clamp(min(1, (cut + blunt + pierce) / 100), 0.05, 0.5) | a direction (a character vector at `+0x640`, `+0xd0`), flipped or turned ±90° by a hit-side flag (2, 4, 8), turned by a random ±50°; placed up to 20 units along it |

We emit no limb-sever splash.

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

## Meitou

The `blood` switch (Meitou, default) draws the nearest cells within `Decal Range` in groups of eight layers, up to 32.
The layer budget is 64 MiB through 6 GiB, 128 MiB through 8 GiB and 256 MiB above, with eight layers as the minimum.
Faithful and pressure limit the array to eight layers. The array is freed after 30 seconds with no near cells, or immediately
under pressure. Undrawn cells still age. Engine choices: the blood is mixed into the terrain colour shader's
albedo before lighting (our terrain is forward-lit, no second pass); the cells are a texture array
read by cell index. Marks more than 20 units above the ground are dropped (upper floors). A pool's
decal is grown whenever its stored half size changes (as the game does), in place in each cell's list. A cell's layer is cleared and drawn again
when decals leave the cell or one of its pools grew (`CellRewrite`, once a frame however many ticks the pool grew in, so growth is as smooth as the
frames); otherwise only the decals placed since are drawn over it (`Appended`). Growing pools used to be drawn over their old copy in 25th steps:
that was visibly stepped, stacked the translucent edge dozens of times and popped when the cell was later redrawn. The
original likely draws incrementally too (**Observed**, inferred: it clears only for a new cell and keeps a drawn cell's dead decals); a log past twice the cell's decals + 64 forces a redraw.
A layer is one instanced draw (each decal's texture by bindless index, colour and alpha blended separately in one pass).
Row order: the terrain shader reads a layer with v = the cell's Z fraction from row 0, and Vulkan's row 0 is NDC −1 but Metal's is NDC +1 (`RenderTargetsBottomUp`), so `DrawLayer` mirrors the quads' Y on Metal. Before this, Metal terrain blood landed in the right cell but mirrored in Z across it (up to ~290 units off). **Observed** (owner, in play on Metal): outdoor blood now appears where the fight is.
The store finds a pool by a per-cell index of place → first pool there, not a list scan, and keeps each life as the store
clock's expiry time, so ageing visits only cells with a decal due. Decals
are saved and loaded as stored (`SaveDecals`, TERRAIN_DECALS in [save.md](../formats/save.md#folder-and-state-mapping));
the bleed tokens are not, so a pool under a still-bleeding character starts anew after a load.

## On the body and on severed limbs

**Verified** (shader sources `data/materials/deferred/character.hlsl`, `skin.hlsl`, `character.material`; exe `FUN_140530b30`, `FUN_1400cf490`):
characters carry blood on their skin, not only on the ground.
- Body and worn-item shaders (`BLOOD` define) lay `bloody.png` (256², RGBA splatter, `materials/`) over the albedo before the darkening:
  `albedo = lerp(albedo, tex × bloodColour, saturate(tex.a - 1 + amount))`. The map is a cylinder projection in the part's own space:
  u = height along the mesh Y × the race's `blood vertical`, v = angle round Y (atan2(normal.x, normal.z) / 2π, shifted half a turn where
  normal.z < 0) × `blood horizontal` (human: 0.15 and 1). `amount` is per body part, `bloodAmount[8]` (the first 8 parts), written by
  `FUN_140530b30` as 1 − clamp((flesh − stun + bandage) / (base × mult × scale − wear), 0, 1) (the +0x48 field is the bandage, not the rig): a damaged part is bloodier, a part far below zero is
  fully covered. Only `Character_VP` (bodies) defines `BLOOD` among the people's programs; worn clothing (`skin.program`) has none, animals have their own (`creature.hlsl`).
- A severed limb item draws with the `SeveredLimb` material (`FUN_1400cf490`, cloned as `Limb_<name>`): textures from the item's MATERIAL record
  (`texture map`, `normal map`, `color map` = skin-tone mask; the human's are the female body's), shader `severed_limb_fs`: skin-tone mask tint
  (a vec4 stored on the item), then blood with the same texture, `bloodColour` (a vec4 on the item) and an extra alpha of
  max(−2 × 0.3 × mesh x, 0), so the cut end (negative x) is solid blood. Its mapping: u = 0.3 × x, v = the angle round X (atan2(normal.y, normal.z)).
  **Verified** (`FUN_1400ced10`, the item's setup from the cut character, called by `FUN_14064edc0`; `FUN_1400cf490` hands the values to the entity): the item keeps a vec4 skin tone at
  `+0x1f4` (shader `skinTone`, custom parameter 2) = 1 − the character's appearance `Skin Tone` colour per channel, i.e. the body shader's `skintone` (our `SkinToneParameter`); a vec4
  blood colour at `+0x204` (custom 3) = the race's `blood colour` as ARGB, alpha 1.0; and at `+0x1e8` the size of the character's forearm (arm slots) or calf (leg slots) bone, which
  `FUN_1400cf490` sets as the item node's scale (we draw the limb at scale 1). The fragment is `albedo × (1 − skinTone × colourMap.r)`,
  then the blood, × 0.7, with the normal map on the frame (tangent, cross(tangent, normal) × tangent.w, normal) (DXT5, x in alpha, z rebuilt).

**Meitou**
- Body: `CharacterContent.BodyBlood` gives a person's body material the race's blood colour (`blood colour`), scales (`blood horizontal`, `blood vertical`) and `bloody.png`
  (none for animals and bloodless races, `bleed rate` 0). `MedicalState.BloodAmounts` publishes the first 8 parts' amounts (`CharacterSnapshot.BloodAmounts`), the host hands them
  to the renderer (`CharacterInstance.Blood`), which writes them as exact integers in the w of the body instance's matrix rows (`CharacterRenderer.WithBlood`); the vertex program
  reads its vertex's part (colour Y bits 16-19, `CharacterContent.MarkBody`) and passes the amount on. **Observed**: a vertex's part is the part claimed by its heaviest bone or its
  nearest named ancestor (as the hidden limbs are, `LimbHideOf`), numbered in the health parts' order (`BodyPartTemplate.MedicalOrder`, the order `bloodAmount` is indexed in). The
  race's `combat anatomy` order is a different one: numbering by it put the left arm's blood on the head (sandbox render).
- Stump: a limb-replacement item without a material wears the body material at the full amount (`BloodFull`): a severed part is far below its health.
- Fallen limb: `Debris` draws a race's `severed limbs` items with the debris renderer, falling and at rest, instead of as a world object: the MATERIAL record's
  texture map, the race's blood colour, blood with the cut end solid, and the Meitou skin's scattering (the same table and rule per race as the body; the curvature baked by
  `CharacterContent.MarkCurvature`), the cut character's skin tone through the colour map (`FallSource.SkinTone`, set in `PopulationSystem.DropSeveredLimb`, not saved: a limb read from a
  save has none) and the MATERIAL's normal map on the mesh's own tangents (`Model.Build` `tangentSign`). A limb piece sits at its mesh's origin and keeps the mesh's own space (the blood
  mapping reads x). A limb as a debris piece casts no shadow. **Observed** (sandbox, a light and a red-tinted character): the limb matches the body it came from; the skin normal map is faint
  (clearly visible only amplified).
- **Observed** (sandbox, a dressed character): a stump under a coat or trousers is not visible at all; the blood shows on a bare stump and on a fallen limb. The ground limb of the
  sandbox is white without a material because the sandbox has no world-object layer, so the debris path (no material) drew it.

## Blood on surfaces (Meitou)

The `bloodsurfaces` switch (Meitou, default; Faithful: terrain only, as the game): the same marks also land on walls, buildings, rocks and other static objects. **Observed** (Hub battle, sandbox screenshots), not a game behaviour.
`TerrainDecalRenderer.Surfaces`:

- Each mark is also kept with its 3D place (upper floors included: the terrain path drops marks 20 units above the ground), saved with the terrain's ([save.md](../formats/save.md#folder-and-state-mapping) "Terrain decals"; a loaded pool no longer grows). A pool grows by the store's rule, kept apart.
- Lifetime, **Verified** for the rule (the terrain's, above) and **Observed** for the symptom (hidden run, a wall splash and a floor pool kept in view past 2970 ticks): a mark has the same 99 s life, and as a drawn cell keeps its dead decals, one whose life ran out is removed only while it is out of `Decal Range` of the camera (`AgeSurfaces`; saved with life 0 while it stays). Blood in view never goes by itself, as the terrain's beside it; the colour alpha is the mark's own (no fade in the last second: the terrain draws a decal once, at its full alpha). Before, a mark faded out 99 s after it was made while the camera stood on it.
- Kept marks within `Decal Range` and in the view are drawn, up to `Blood decals drawn` (below; 4096 per draw call); the kept list is guarded at 32768 (oldest first). Every mark is kept here, the ones on bare terrain too, which this pass never draws (the CPU cannot tell them apart).
- Soaked surfaces take no new mark (`Soaked`, `CoverFull` 1.6): each kept mark adds its inked share (the mean alpha of its type's textures: pool 0.35, smear 0.25, splash 0.1, times its alpha) to the 4-unit cells under it (bands of 8 units of height); a new splash, smear or drip whose centre and four points half-way to its edge are all at 1.6 or more (about 80 % of the surface inked) is not kept. A bleeder's pool always is. **Verified** for the shares (install `data/materials/blood`, mean alpha per file: pool 0.19 to 0.56, smear 0.24, splash 0.06 to 0.24). Why, **Observed** (save `lamp`, 6000 marks placed in and round one room, 2560x1440, GPU time of the pass): stacked without limit the pass took 25.5 ms close up and the floor went solid black; with the rule 1868 are kept and it takes 1.4 ms (0.6 ms from further), the floor red with dark pools.
- Drawn as a screen-space decal after the opaque scene, before the characters and the water, so they never take blood: a sphere round the decal's square, each scene pixel in it rebuilt from the scene depth, its normal from the depth's slope, the texture projected along that normal. The result multiplies the lit scene colour.
- Pools, drips and smears only on up-facing pixels (normal Y > 0.8) within 0.4 units of the mark's height (a board raised by more cuts a pool along its edge: **Observed**, the `lamp` tower's floor). Splashes are centred 0.9 above the feet (above the terrain under the mark on the ground). On a wall they take any surface facing the mark within the mark's half size of it along the surface's normal (fading over the outer half); on a floor (normal Y > 0.8) any pixel at most 1.5 above the centre, fading from max(half, 2.5) to max(full half size, 3.5) below it: on a raised floor the centre keeps the victim's height, and a sloped floor puts the landing place up to ±1.3 off it. **Observed** (#972, Proving Grounds arena, a floor about 37 above the terrain sloping 0.07, a CPU copy of the shader): the wall test there cut a centre 0.3 above the floor to a hard disc (radius 20 × that height) and drew nothing of one under it; their drops grow fewer from the middle out, to none at a reach of 0.55 to 1 half size that differs round the mark (a noise of the direction, the mark's angle its seed; which drops go: a noise 14 cells across the square with one of 37, `SurfaceFragment`), and their sphere is 1.55 half sizes. **Observed** (owner; hidden runs, `splash.sh`, twelve splashes on the `lamp` tower's floor and mats): the texture is an even field of drops to the square's sides, so faded by the radius, even with an uneven rim, a splash read as a disc and the gaps between neighbours as clean shapes with arcs for sides; thinned by the noise it ends in scattered drops. Mats take the splashes as the floor does.
- The terrain keeps its cells' blood: the depth is copied after the terrain's draw and again after the static objects; a pixel whose two copies hold the same bits (no object drew over the terrain there) is skipped. An exact test: comparing the scene depth with the terrain copy within a tolerance (2 cm plus 0.05 % of the distance) took floors and rocks lying almost level with the ground for terrain and dropped their blood, and a height-map test failed (the terrain LOD departs from the height map by up to the pixel error). A second copy after the static objects, before the foliage, skips every pixel nearer than it (grass, trees, debris sway or move; the decal would slide over them). Any mesh drawn with the static objects but animated (rotors, doors) is still decorated.
- Debug command `bloodmark <splash|pool|smear|drip> <x> <y> <z> [size] [RRGGBB] [dirx dirz] [--count n]` puts a mark (n spread over 60 x 60 units) for controlled repros.
- Cost, **Observed** (Hub battle with about 100 surface decals in view, 2560x1440, Vulkan, wall-clock per frame): +0.35 ms (6.88 to 7.24 ms), two extra depth copies and pass splits plus the pass, made only while a kept mark is within `Decal Range` (`SurfacesNear`). Low impact. **Observed** (Radeon 8060S, 1920x1080, Vulkan, High, a
  staged fight of six beside a squad of twenty, GPU time): the pass 2.5 ms; the quality tiers turn it on from High ([native-ui](../native-ui.md)).
- **Observed** (owner, in play): works inside buildings and on their floors.
- Depth convention: each scene texel's view depth comes from `depthViewZ` (`DepthConvention.Glsl`): Vulkan's scene depth is reversed, `z = n f / (n + d (f - n))`. The camera's near plane is a quarter of its distance, so a wrong mapping rebuilt positions far off at play distance and showed blood only in close-ups. The texel's ray is rebuilt from the jittered projection (the upscaler's jitter moved out of the texel centre).

## On weapons and gear (Meitou)

**Verified** (install `data/materials/deferred`: `skin.program`, `skin.material`, `objects.program`, `objects.hlsl`, `construction.hlsl`; grep for `blood`): the original bloodies the body only. `BLOOD` is defined for `character.material` (bodies), `creature.material` and the terrain's `Terrain_Blood_FP`; worn items (`skin.hlsl`, no `BLOOD` define in `skin.program`) and held items (`objects.hlsl`, no blood code) draw clean, and nothing in the simulation records blood on a weapon. So this is a Meitou addition: the optional `gearblood` switch ("Blood on weapons and gear", Meitou, **off** in both looks like `objmetal`; `Optional`, `GearBlood.Enabled`; debug command `bloody <hits> [RRGGBB] [--id]`).
- Worn items (`CharacterShaders.GearBloody` material flag, `AssetPart` skinned): the body's splatter (same mapping and scales, the wearer's race colour) by the blood of the body part the vertex is skinned to. `MarkWorn` writes the part of the heaviest bone into bits 16-19 of the vertex's colour Y as `MarkBody` does; the instance carries the amounts as for the body. Same texture and mapping as the skin, so a wound reads as one pattern across skin and cloth. The look depends on the item (hard = its metal share, or gloss over 0.3-0.6): on plate, lacquer and hard leather a wet film as on a blade (below); cloth soaks it in, a soft-edged stain that dyes the weave with the blood's hue (x 0.6 to 0.3 with thickness, the cloth's pattern kept), gloss at least 0.4. Metal share goes to 0 where it lies. **Observed** (t_flats_shore, `hurt`): laid as an opaque film on cloth it read as paint.
- Worn stains last (Advanced → Clothes keep blood stains, `UserConfig.KeptStains` → `GearBlood.KeptStains`, a Faithful / Meitou switch, Meitou on by default; off, worn items show the wound's blood as before): per character (by draw key, in `_wornStain`, beside the weapon blood) the renderer keeps a stain on each of the body's first 8 parts. What the wound's blood on a part rises by is new blood; it soaks into the item at most 1/45 a game second (`SoakSeconds`, `GearBlood.StepStain`), so a stain creeps in rather than appearing. The stain stays when the wound heals; it fades alone over 30 game minutes (`StainFadeSeconds`), washes off wading (water over the feet x 1.1 above 3, `WadeReach`) in 12 s and in full rain (`WeatherSurfaces.Wetness`) in 120 s, the blood still soaking in with it (so in rain fresh blood barely stains). Worn items draw the stain (`Live.WornBlood`), the body its own blood. Stepped only while the character is drawn, from the elapsed game time; a stain loaded from a save takes the wounds as they are, so their blood does not soak in again. Saved with the weapon blood (`worn`, [save.md](../formats/save.md#weapon-blood)). A Meitou choice, not a game behaviour: before, a worn item's blood followed the wounds and went as they healed. **Observed** (sandbox, `hurt` x4 then `heal`): the skin's blood goes with the wound, the trousers keep the stain.
- Weapons: a melee hit with a weapon (not fists, not bolts) that draws blood (the `Splash` rule: bleeding race, not in water) carries the attacker (`BloodMark.Attacker`); `BloodDecals` hands it to `CharacterRenderer.WeaponHit`. Per character (by draw key, in `_weaponBlood`) the renderer keeps amount, wetness, colour (mixed with the old by weight), which weapon (back or hip). A hit adds 0.15 + splash size (0.05-0.5); wetness drops to 0 over 150 game s (colour darkens/browns, gloss 0.94 to 0.25); then the amount flakes off at (1 - wet) / 1500 per s; water over the hand (1.1 x depth above the feet over 8) washes it in 8 s; rain (`WeatherSurfaces.Wetness`) in 300 s of full rain. Stepped only while the character is drawn, from the elapsed game time.
- The blade: the weapon's long axis from the mesh's box, towards the end farther from the mesh origin (the grip); coverage starts at 30-50 % of the length and grows to the tip; as on the body it is the splatter's own alpha less (1 - amount), times a finer copy of the map, so the blade shows spatters and runs at any amount, never a full coat (**Observed**: 0.15..0.6 of the alpha painted the blade red after four hits); the splatter map projected skewed over the mesh, 5 tiles along it. The grip is not told apart from the blade by material (none does), only by position. The weapon's instance matrix rows 0 and 1 carry colour (24 bits) and amount + wetness (2 bytes) in w (instance flag bit 30); only weapon parts (Slot Weapon, `bare sword` or `mesh` without a sheath, not crossbows) take it. The film: opaque a little way in from its edge (no thin tint over the steel), the blood colour x 0.45 at the edge to 0.16 where thick while wet (0.6 to 0.4 dry), and while wet a rounded rim (0.6 mm high, slope limited) that turns the normal so it catches the light. **Observed**: tinted by the splatter map and blended thin it read as rust; at the race colour's full brightness it turned orange in the sun; an unlimited rim slope outlined every spot. The film is laid after the metal estimate, on top of the metal: the lighting has one albedo (reflection colour where metal, diffuse where not), so the albedo is set to give the film's diffuse light at the metalness left bare (`metal x (1 - cover)`), the bare remainder reflecting in the blood's colour; mixing steel and blood in the albedo made a pale red metal, pink in the light (user report). The same for the film on worn plate.
- Saved: weapon blood goes to `meitou-weapon-blood.json` at the root of the save folder ([save.md](../formats/save.md#weapon-blood)), not into the original's files; worn blood follows the saved wounds. A save without the file (or a damaged or newer one) loads clean weapons. The entries are keyed by the character's save handle, put back by `BloodDecals.ReadWeaponBlood` on the characters in the world and, for those not in it yet (their zone unloaded), as they appear; the ageing clock restarts at the load (the time between sessions does not count). With the switch off a save carries the loaded file on unchanged. **Observed** (hidden run, t_flats_shore, `bloody` then `quicksave <name>`, load): the bloodied blade of one character is back after the load, clean when the file is deleted. The colour on a weapon is the blood colour of the race that was hit (`BloodMark.Colour`, the bleeding character's), on a worn item the wearer's. **Observed** (hidden run, t_flats_shore): blade and trousers red with the switch on, clean off; a blue-blooded wearer's trousers stain blue; after about 400 game s the blade is speckled dark brown and thinner. Not checked: a robot's oil colour (the colour is the race's `blood colour`, passed through unchanged), the washing.
- The colour on a weapon is the blood colour of the race that was hit (`BloodMark.Colour`, the bleeding character's), on a worn item the wearer's. **Observed** (hidden run, t_flats_shore): blade and trousers red with the switch on, clean off; a blue-blooded wearer's trousers stain blue; after about 400 game s the blade is speckled dark brown and thinner. Not checked: a robot's oil colour (the colour is the race's `blood colour`, passed through unchanged), the washing.

## Bloody footprints (Meitou)

The `footprints` switch (Meitou, default; Faithful: none, as the game; `BloodFootprints`), not a game behaviour. A walking character's feet pick up the colour of a wet terrain pool or splash (its opaque inner ellipse) and each foot leaves up to four progressively lighter prints on dry terrain. The prints expire in game time and stay out of both save formats; switching it off clears the prints and the blood on the soles. It needs the `Blood` setting.

- Masks: an equipped boot's sole outline (the convex hull of its mesh's lowest band of vertices, `FootprintSoles`), a bare foot the body mesh's own sole: the union of its triangles within 0.45 units of the lowest point on that side, toes included; a band too squat (under 1.6 long per wide, the skeleton and robot races) prints its lowest vertices' outline as footwear does (**Observed**: `robot`, `Skeleton_MkII`, `Robot_Variant01/02` plates about 1.25–1.57 x 2.4–3 units), one covering under 0.75 of its convex hull (a robot foot frame) prints the hull flat (`FootprintSoles`, **Observed** baking `human_male`, `human_female` and `bone_male` to images: a foot outline with five toes, about 1.26 x 3.23 units); a fitted leg (LIMB_REPLACEMENT `slot` 52 left, 53 right) prints its own mesh the same way (skin sole, else outline); the Human and Hive legs' meshes are only the cap above the knee (lowest point 7.4 units up), so their wearer prints the body's sole; Hive feet are pointed tips about 0.43 x 0.91 units, which bare soles down to 0.2 x 0.5 allow (**Observed** baking the human, Athletic and Economy robotic legs: the first two a plate about 1.25 x 2.5–3 units, the Economy peg none, so the generic boot mask); generic boot and barefoot masks (`FootprintTexture`) when a mesh gives none. When a step happens: the ground trails' footfall detection ([render-terrain.md](../render-terrain.md#ground-trails), `GameHost.Trails.Body`), one event per human limb once its sole has settled, side from the limb bone's `L`/`R` name; it runs whenever the prints want it (blood on the terrain), with `trails` off and on the Metal host too, and pressing marks stays behind `trails`. The event wets the foot or places the print at the sole's contact, turned heel to toe. A character with no pose leaves no print (every drawn one is within the trails' 240-unit reach, past the prints' 160). **Observed** in the sandbox: one print per step, under the foot bone. No boot print where no boot is worn: a race with `no shoes` (Hive, Skeletons, Fishmen, Deadhive, Soldierbot, P4, Screamer) or a wearer with neither leg original has its boots section closed (`InventoryCommands.SectionClosed`); **Observed** in the sandbox: Samurai Boots given to a Skeleton Drifter stay in `main`, to a Greenlander go to `boots`.
- Drawn by the surface-blood pass on terrain pixels only (scene depth equal to the terrain's), so the print's detail does not depend on `Decal Resolution`. The terrain depth copy runs while a print is within `Decal Range` and in view.
- `Blood decals drawn` (Tab panel, 64 to 2048, default 2048): the most surface decals and prints drawn in a frame, the ones that look largest kept (least distance over radius). Nearest to the eye, as before, the marks on the terrain outside took the places and a room's blood went and came back with the camera's turn and zoom (**Observed**, save `lamp`: 2400 marks placed outside, pools on the tower's floor missing in 3 of 12 views at 512; none missing now).
