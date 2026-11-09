# Character renderer

`Meitou.Rendering.CharacterRenderer` partials: core content/material/lifetime; Draw cull/LOD/batches;
Motion previous state; Portraits offscreen portraits; Stats diagnostic rings. Inputs are
interpolated simulation snapshots and data appearances, not simulation-owned GPU resources.
Game assembly rules: [characters](characters.md), [animation](animation.md).

## GPU layout and shading

Instance bones/material offsets index shared storage buffers; group compatible mesh parts.
The update places every in-range part once (model and previous matrix relative to the eye, LOD distance at bias 1) inside the
parallel pose loop; the colour, motion and shadow views from that eye reuse it, cull and pick levels in parallel chunks of 16
characters, and keep the serial order within each batch. Portraits, icons, hover masks and other eyes place their own.
The shadow cascades drawn in a frame are gathered together before the first (`PrepareDepth`, from `ShadowPass`'s `CasterPrepare`): one
parallel walk over the chunks tests each character and part against every cascade, then each cascade's batches and instance rows are
filled in parallel; each cascade's draw only records. Every view has its own batches, named by an id per (mesh part, level, sidedness).
Whether a character is indoors (the GI's interior flag; the host's hidden-indoors test too) is asked again only once it has moved farther
than the nearest interior box was when last asked (at most 50 units) or the loaded interiors changed (`WorldObjectRenderer.InteriorMemo`).
Each `PartMaterial` keeps its last record and is written again only in a new epoch (texture cache `Version`: an image loaded, swapped
or unloaded; LOD bias, stand-in, sampler generation, skin settings) or after its own fields changed (blend, body build). The records live
in `CharacterMaterialTable`, a persistent storage buffer (binding 7) with a slot per material: a record that came out different goes to a
new slot (frames in flight still read the old one), uploaded in one copy; a slot comes back FramesInFlight + 1 frames after its material
changed or went 600 updates undrawn. Texture needs
are offered, and the textures marked used, only on the frame `CharacterTextureBudget` commits (every 500 ms, under the cache's 2 s idle limit).
CharacterShaders set0 bindings: bones6, materials7 (persistent), morphs8, previous bones9; material20 texture
slots. CharacterLayoutTests pins CPU/GLSL layouts. Push depthIndex is near-depth bindless index
for motion,0 otherwise. Camera-relative transforms preserve precision; previous bone/transform
state must match motion history. Skin/body/head/hair/item modes share world lighting/haze.
The colour and motion views use the scene's rotation-only view-projection (`WorldFrame` passes it, jittered and unjittered; the
previous frame's is moved by the eye difference in double), not the world one with the eye's position cancelled afterwards
(`FromOrigin`, still used by the other views): the world matrix's translation already holds the eye's rounding.
**Observed** (sandbox hero spawned at x = -50874, `--upscaler off`, camera locked on the leader at the minimum boom 10, CPU replay of the
renderer's matrix path): the look-at point projected 0.2 px (range -0.07 to +0.32) off the screen centre, a different error each
frame, against 0.000 near the origin; the world matrix applied to world positions steps by 0.25 px. Its previous-frame twin put the same
noise into the characters' velocity (median of a walker's vectors, second difference 0.39 to 0.25 px in x with the rotation-only
matrix). The error scales with 1/boom and with the distance from the origin; terrain and objects still use world matrices.
Head uses UV+(0,1); dye/vest/overlays and cutout rules come from research. Same-instance caster
pass preserves alpha coverage. Face morphs and body shape precede skinning.

### Skin (Meitou `skin` switch)

Faithful lights skin as every surface (`kenshiLight`, the game's deferred pass: clamped N·L). Meitou: Penner's pre-integrated skin
diffuse replaces the sun's N·L on bare skin (`SkinScattering`: a 64² RGBA16F table baked at start-up from the six-Gaussian skin
profile of GPU Gems 3 ch. 14, each layer normalised along the ring; u = N·L, v = √(1/r ÷ 1 mm⁻¹)). Curvature 1/r is baked per vertex at load
(`CharacterContent.MarkCurvature`, vertex colour Z, not on icon meshes, whose vertex colours tint them: mean |Δnormal| / |Δposition| over the vertex's edges, averaged over vertices sharing a position) and
interpolated; a screen-space fwidth estimate was constant per triangle and drew the low-poly facets and outlines in red. At 1 unit = 100 mm (an **Observed** scale, [terrain](formats/terrain.md)), times 30 (`SkinScatterScale`, a look: at true size the
profile barely reaches a face's 5-8 cm radius, terminator red 0.016 → 0.019; ×30 gives about 0.16 and a tail past N·L −0.2). Red, green and blue read the table with the
normal map mixed 0.6 / 0.3 / 0 towards the smooth normal. Only what the table adds over plain N·L is kept (its strongest channel): its loss of red
on the lit side (energy moved into the shadow) turned sunlit skin cyan-grey. That extra light takes the skin's own hue, saturated (albedo ÷ its
brightest channel, squared): red-orange on human skin, amber on hive chitin, teal on fishmen, any modded race its own. Skin share: body-shaded
parts of people (`CharacterShaders.Skin`; not animals; stumps included) whose race has it on, times what vest layers and hair/beard overlays leave
bare. Per race (`CharacterRenderer.SkinRaces`, settings Graphics > Skin, `meitou.user.json` `skinRaces`: only races changed from the default): every
person race of the loaded data (RACE records no ANIMAL_CHARACTER uses, mods included) is on unless RACE `is robot`. World and Meitou
stage (fog distance -3) and the portraits (the plain model, fog distance 0: their fixed key light is near the camera, so the glow stays on the
shadowed cheek, jaw and neck); the game's stage and icons keep N·L. Lamps too: the character shader defines `LAMP_SKIN`, and
`LampShaders.lampLight` takes each lamp's diffuse from `lampSkinDiffuse` (the same term, per-fragment inputs set in `main`); the specular keeps
N·L. Faithful: push `skinLut` 0, unchanged.
Limits: the sun shadow is not scattered, so the band past the terminator ends at the shadow edge; no back-lit
translucency (ears). The `chars2` switch lifts both (below).

### Hair, cloth, skin and eyes (Meitou `chars2` switch, off by default)

Not run on a GPU: look and cost **Unknown**. `pc.fill.a` carries the switch; `CharacterSurface` holds the GLSL and adds to `kenshiLight` and `lampLight`, for the
character fragment only, what each light gains (`surfaceSun`, `surfaceSpecular`): the sun's after its shadow, a lamp's as is, the same function for both. World, Meitou stage
and portraits; never the game's stage or the icons. Metal has no switch.

**Hair**: two Kajiya-Kay lobes (Scheuermann's shifted pair, standing in for Marschner's R and TRT) over the game's GGX: a sharp near-white one, a wide one in the hair's own
hue, both scaled by the game's gloss (brightness times the attachment's `specular`, so a matte style stays matte) and broken up by shifting them with the texture's brightness.
The strand direction is the mesh's up axis laid on the surface (`vColour` outside icons), the only direction known without the UV layout: down the sides, out from the
crown, where the highlight fades. **Unknown**: how often a style's real strands disagree (buns, swept hair). Animals keep the game's lobe: fur does not fall down
(`CharacterShaders.Strands`). Every hair-mode fragment takes its normal on the side the camera sees (port of KenshiNative's "Hair on the side the camera sees": cards and
inward-facing meshes went dark under GI and AO).

**Cloth**: a Charlie sheen (roughness 0.6, Neubelt's visibility), in the diffuse's units, tinted √albedo, at 0.25 × the amount; its visibility is capped at 1, as uncapped and at 0.5 it reached 2 × a dark cloth's diffuse (computed over a sweep of angles); now at most 0.46, 0.27, 0.19 × the diffuse at albedo 0.1, 0.3, 0.6. Data holds no cloth mask, and worn items are matte (`glossMult` 0), so the texture's alpha says nothing. What separates them is the item: ARMOUR
`material type` (CLOTH 1, LEATHER 0.35, CHAIN and METAL_PLATE 0, not ARMOUR 0), per worn mesh and per vest layer (`CharacterContent.ClothSheen`), mods included; `1 - metal`
(the colour map's blue, dyed items) takes out the metal fittings of a cloth item. **Unknown**: fittings on an undyed cloth item take the sheen. Sun and lamps only, no sky term.

**Skin** (where the skin switch and the race have it, so robots and races turned off take none of this; severed limbs keep the plain skin; colours are the skin's own saturated
hue, as above): the table's band and the light through a part ask the sun's shadow a second time, where the light went in (`skinSunExtra`: the point moved towards the sun by
the chord 2 r cos θ of a sphere of the curvature's radius, at most 1 unit, plus 0.1), so a body's own shadow no longer ends them and a real caster still does; one more shadow
lookup, on skin near and past the terminator only. A cast shadow's penumbra warms by (√v − v) / 2. Thin parts: curvature 3 to 9 (a guess at the bake's values on ears and
fingers: it reads the mesh's edges, so **Unknown** until measured on the meshes), facing away from the light, thickness 2 / curvature, passing exp(-thickness / 0.25) (a look,
the idea from KenshiNative's skin3; it measures thickness with a ray, which has no characters here), 0.8 at most. Lamps: the same term through `lampSkinDiffuse`; they have no shadow, so no second lookup and the penumbra does not apply.

**Eyes** (needs the skin switch and the race's skin): the game's eyeball is head texture at gloss about 0.93, a mirror of the specular cube (**Observed** by KenshiNative on its
game data, `docs/features/characters.md` "Eyes"). Port of the surface of its eyes2: the eyeball by data (`eyeOf`: head UV, bare, no skin tone, gloss 0.5 to 0.7 up, curvature 2.5
to 4.5 up in place of its race list; hive shells stay out by curvature, **Unknown** until measured) takes no skin terms, gloss 0.4 for the sky, and a bounded tear-film catchlight from the
sun and the lamps. Not ported: eyes2's refracted iris and eyes3's drawn iris: KenshiNative turned them off after play (they often looked worse than the game's eye), and eyes3 needs a
table per head mesh, against the modded-race rule.
**Rain** (port of `makeWet`, KenshiNative's `skin-wet.md` start: Meitou's characters had no wetness): `uWeatherWet.x` on albedo and gloss, outdoors, absorbance
(1 − gloss)(1 − metal), a fifth on skin. Not ported: its drops and film (a baked drop map, a G-buffer path) and the water line (no water height here).
**Face shadows**: nothing to port: KenshiNative's fault was the traced hair's alpha channel, and here the shadow caster cuts by the hair's own `alphaChannel`
(`CharacterShaders.DepthFragment`) and no ray sees characters.

### Precision

Far from the world's origin a float steps by about 0.004 units at 50000 (0.016 at 200000); a character 4 units from the eye moves 1.5 px for one step.
**Observed** in a paused close-up at x 57000 (`siwa-test6`, offscreen frames of the live loop, no upscaler and TAA alike): the character jumped ±2 px every few
frames while the background moved under 0.05 px. Cause: the frame's camera and characters are drawn between two ticks with `Vector3.Lerp`, which computes
a (1 - t) + b t; with both ticks' states equal to the bit the result still landed a step off for some alphas, so a still camera (and a still character) moved
a step as the frame's alpha changed. Everything drawn between ticks now uses `Interp.Lerp` (a + (b - a) t, exact for equal ends): the camera state, the
characters' drawn places and ragdoll bones, trails, wakes, bolts, the characters' sounds and the squad tour. Also: `WorldCamera` forms the eye in double, `Eye` being
its float rounding (every system's origin) and `EyeResidual` the rest, which the characters' colour and motion views take as a translation (`RelativeRotation`),
so an orbit or zoom round a fixed target moves the eye smoothly instead of by steps; the strategy camera's eye clearance lifts the target only for a shortfall
over 0.001 (the ground read under the eye wavers by a few ten-thousandths frame to frame). Every other scene pass follows the same convention: [renderer-native.md "Render origin"](renderer-native.md#render-origin).

## Content, LOD and memory

`CharacterContent` owns buffers/textures/morph arena; worker fetching/preparation precedes render
uploads. Reuse appearances/content; replace only after ready and retire after GPU consumers.
`CharacterTextureBudget` shares mip/VRAM pressure policy; unavailable content uses a stand-in.
Loading readiness includes used textures deferred by memory pressure, not just queued decodes/uploads, but not the governor's coarser
replacements (`WorldTextureCache.AwaitedCount`: the finer image is drawn until one lands). Under the loading screen (`CharacterRenderer.Covered`)
textures upload and appearances build for up to 16 ms a frame instead of the play budgets.
A game's loading screen waits for every look within `LoadingReach` (3000) of the camera's focus, so those are built whatever the eye's draw reach
and tiny-cull; building only the eye's reach left looks waited for but never built (the screen stayed up).
Generated four-level reductions are always enabled; their index lists are cached on disk (`%LOCALAPPDATA%\Meitou\charlods`, keyed by the parts'
vertices and indices, radius and settings; `--no-load-cache` bypasses it), as building them is most of a mesh's preparation; with 100+ characters drawn in the world, pose rates fall with the character's size on screen
(its diameter in pixels at the view's projection, so resolution and field of view count and big animals keep their rate farther): 100 px and more every
frame, then at most 30/15/7.5 poses/s below 100/50/25 px (in time, the game's animation clock, each on its own phase); at most 48 characters, the largest first,
take the full rate by size, the next ones at least 30/s (`CharacterRenderer.PosePixelSteps`, `FullRateBudget`; engine choices). `CharacterInstance.FullRateAnimation` bypasses
the throttling for presentation actors, the first 10 of the player's characters (squad bar, then the
other squads in order) and the first selected one (`PlayerInterface.FullRateCharacters`). Shadow LOD doubles choice with cascade floors.
A character that cannot show (`CharacterRenderer.Shown`: its sphere, its shadow swept from the light, its water mirror image all outside the view; no portrait) is not
posed at all, its held palette still drawn (far cascades are reused for up to 32 frames), and is posed without motion the frame it returns; the host fills no poses or
ragdoll beyond the draw distance + 2000 (`InReach`): it lists those in `CharacterDrawList.Distant`, once a tick, which the renderer scans only when it changes (looks to build, kept-point textures). Engine choices. **Observed** (stutter final boss save, ~1000 characters, ~400 in reach, ~280 of them off screen).
F11's characters line counts what the last colour view culled in (frustum and fog only: those behind terrain or buildings and sub-pixel ones count too);
its `[ ] boxes` checkbox outlines those over everything (`CountedBoxes`).
Clamp stored bounds to real mesh extent. Animation rendering does not independently integrate
walk-loop root movement; simulation owns relocation. Quads/morph and motion buffers retain
correct previous-frame data across skipped poses and content changes.

### Tiny characters (Meitou engine choice)

The original draws every character within its distance, a gigantic race out to 7x it, however small it shows. Meitou does not draw, pose or build one whose
projected height (`asset.Height x scale`, over the distance to the eye, in render pixels: `LodPixelsPerRadian` follows the render size, so before the upscaler) is under 1.5 px,
and draws it again from 2.0 px (`CharacterRenderer.CullTiny`; the hysteresis keeps one at the edge from flickering). A look not built yet is judged by the tallest asset built
for its body mesh, so the first of a body mesh is still built. Never the player's characters, a portrait's, a hidden one, nor a look wanted by a kept point. It leaves the live list, so
no shadow, motion or reflection pass sees it either; the simulation, the host's lists, sight, sounds and selection are untouched (picking reads the live list: a speck is not clickable).
**Observed**: far giants of a mod-heavy install cost 0.6-1.2 ms a frame in a camera flight before this; **Unknown**: popping under DLSS at 1.5 px (not seen at the far zoom).

## Animals and portraits

Animal race/body/mesh/fur/clips/size use shared assembly with animal inputs, not human fallback.
Portraits use the same content/shading with a dedicated camera/render target; avoid mutating
world draw state. Asset/appearance failure must not corrupt another instance or portrait.
Portrait cells redraw only when their character, appearance, size, camera serial or streamed
texture images/swizzle change, or the look below does; animation and frame counts do not.

### Meitou stage and portraits

`PreviewLook` (set from the world's options every frame): enhanced when Meitou's tone map, grade or
temporal AA is on. The editor stage (stage light unclamped, fog distance -3, platform included) is
drawn into RGBA16F at 2x each way with Meitou AA (1x past 16.6 Mpx); each portrait (plain model) alone
into a 1024 px RGBA16F square (4x its cell). `PreviewResolve` tone maps each sample, averages by
coverage (anti-aliased edges on the transparent background), grades and dithers into the 8-bit
picture. Atlas cells are 256 px apart; enhanced portraits fill them (the original's 128 px were stretched to the
squad bar's 120-230 px from 1080 to 4K lines and looked soft), faithful ones keep 128 px in the corner. Portraits keep the
fixed plain light: a still drawn under the live sky would depend on the hour it was drawn. Faithful
keeps the original's direct 8-bit stage and 128 px plain portraits.
Studio (`previewsun`, Meitou, on by default; `CharacterRenderer.Studio`): enhanced previews are lit by a fixed sun instead of the key light, the same
whatever the hour. Per view: the character's depth from the sun into a 2048 px map (an orthographic box round its sphere, the matrix relative to the
sphere's centre), its depth from the camera, a horizon-based occlusion from that depth (8 directions of 6 steps out to an eighth of the sphere, a fixed
4 x 4 turn the supersampling averages), then its colour in the shader's studio model (fog distance -4): a warm sun (shadowed, 4 x 4 compare, a
normal and light offset of a texel or two), a cool sky over warm sand (the ground's bounce, `studioAmbient`: x2.2, 30 % of it staying however closed the point is, so a side the sun does not reach is dark, not black), the sky's visibility and the occlusion on the sky and bounce only.
The sky's visibility is the large-scale occlusion the hybrid GI gives in the world (a hood over the face): the character's depth from 32 directions of a
Fibonacci spiral over the whole sphere (384 px tiles of one atlas, the matrices rebuilt in the shader), each point taking the share of directions,
weighted by its normal, from which nothing of the character hides it (2 x 2 compare a direction); the screen's occlusion adds the folds at 0.6. The depth draws
are the colour shader at fog distance -5, returning after its cut-outs. The map and occlusion indices and the map's matrix ride the view block's
interior-cut fields (`cutPad`, `cutVolumes`, `cutRanges[0]`, `cutPlanes[0..3]`), which characters never read. The sun: portraits from the camera's
right and front, 35 degrees above the level (the camera is at the head's height: the way to it already climbs); the stage fixed to the character (the shadows stay while the editor's camera turns round it): from the right of its first
view (yaw 0, its front), to the side and about 55 up.
The stage's platform (`WorldObjectRenderer.DrawStageProp`, the objects' shader, `StudioShaders`) takes the same view: lit by the studio's sun, it
receives the character's shadow and the sky's occlusion round the feet (its `cutVolumes` left 0: the objects read it as their shadow cut; the
centre made eye-relative like its positions). The platform casts too: the host's `StageCasters` draws it
(`WorldObjectRenderer.RecordStagePropDepth`, the objects' shader returning after its cut-outs at fog distance -5) into each studio map's pass
(the sun's, the sky's spiral, the camera's depth), so its planks and pole shade it and the character, and its bulk closes the sky below the feet.
The map is drawn with the camera's levels of detail. Its lookup takes clip y -1 as the first row (the viewport is not flipped). `MEITOU_STUDIO_DEBUG=1`
draws the terms instead (red the sun's share, green the sky's visibility, blue the screen's occlusion). **Observed** (sandbox, `editor` console command, overhead and camera
suns): the hood shades the eyes, the mantle the shoulders under its edges, a held staff the skirt. Not there: bounced light (the sky and the ground's bounce stand in).
Atlas layout (Meitou): squad members take cells 0-29; the conversation window's speaker and player
character take the last two (30, 31) while a conversation is open.
Item icons (`CharacterRenderer.Icons.cs`) are the same: an item's mesh drawn through the item shader into a texture of its own, as a pseudo-instance that the world and shadow views skip;
the camera, lighting and sizes are in [game/ui-screens.md](game/ui-screens.md) "Item icons".
Harness `--crowd` exercises instance scaling; compare pose/upload/draw/shadow separately.
**Observed** human_male:23 poses, no normal offsets,2986/15504 vertices affected, max offset0.18.
**Verified** (`FUN_140071ac0`, `skin.program`; survey 2026-10-21): the game bakes a look's poses on the CPU once (`Ogre::Mesh::softwareVertexPoseBlend`
per non-zero weight into a clone `<mesh>_morph_<n>`, then `removeAllPoses`) and never rebuilds normals or tangents; its vertex programs take no
pose input. No pose in base data (13 posed meshes, 292 poses), installed mods or the Workshop folder carries normals, so the bind normals stay,
as in Meitou (offsets only).

## Remaining limits

Far shadow casters: in Meitou's coarse cascades (`ShadowLod`) a part whose diameter is under one texel
(`ShadowPass.MinCharacterCasterTexels`) casts nothing (hair, beards, weapons, hats); A/B `char-casters`. Release, 400-character
crowd at The Hub: render-thread shadows -0.28 ms (-15 %), 11k of 921k pixels differ by 4/255 or more.
Texture stand-ins: a part whose cut-out texture is still streaming in is not drawn (shadow and motion passes too); the head's
region shows the skin constant and a flat normal until the head maps are resident; vest layers are drawn per ready layer
(`AlphaPending`, `HeadPending`, `HeadNormalPending`, the vest mask).
GPU culling, merged far shadow casters (one skinning per frame for every cascade), tail-mip texture stand-ins and
original animation-update budgets remain incomplete. The `charlod` switch picks the files' own levels instead (Faithful; [characters](characters.md#lod-of-character-meshes)). Tests pin layouts/data; visual parity requires named screenshots across
races, bodies, gear, animals, attachments, motion and shadow views. [bench](bench.md).
