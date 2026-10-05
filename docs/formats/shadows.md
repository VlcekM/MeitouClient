# Sun shadows

How Kenshi shadows the sun: the three `shadow mode`s, the cascaded shadow maps (CSM) the viewer rebuilds, the
rectilinear-texture-warped map (RTW) that is the game's other choice, the caster and receiver rules, and how the shadow term
enters the deferred lighting ([lighting.md](lighting.md)). Labels as in [../README.md](../README.md).

Sources: `kenshi_x64.exe` (the deferred light renderer's shadow-mode switch and per-frame update, the CSM class (creation,
split set-up, per-cascade fit, render), the RTW class (creation, fit, importance, warp and shadow-map passes), the options
screen, the settings loader and writer, the `setCastShadows` callers), decompiled outside the repository on 2026-10-05; the
shipped shaders and scripts `data/materials/common/shadowFunctions.hlsl`, `shadowcaster.hlsl`, `shadows.material`,
`shadows.program`, `common.program`, `rtwshadows.hlsl` / `.material` / `.program`, `rtwforward.hlsl`, `rtwbackward.hlsl`,
`deferred/deferred.hlsl`, `deferred.material`, `terrain.material`, `terrain.hlsl`, `foliage.material`, `objects.material`,
`skin.material`, `construction.material` (read for facts only). The published techniques are Zhang et al.'s parallel-split /
"practical split scheme" cascades (PSSM) and Rosen's *Rectilinear Texture Warping for Fast Adaptive Shadow Mapping* (2012).

## Settings (Verified: exe settings loader, writer and options screen; `settings.cfg`)

| `settings.cfg` key | Values | Meaning |
| --- | --- | --- |
| `shadow mode` | 0, 1, 2 | 0 "Disabled", 1 "CSM", 2 "RTWSM" (the options list's labels). A missing key reads as **0**. |
| `shadow quality` | 0, 1, 2 | The map side, from the table **{1024, 2048, 4096}** indexed by the value. A missing key leaves the previous value. |
| `Shadow Range` | 1000 … 9000 | How far shadows reach (world units; options slider 1000 to 9000). A missing key reads as **5000**. |

- The renderer switches modes in `DeferredLightRenderer::setShadowMode(mode, size)` (the exe names it in its exception text):
  mode 0 sets the lighting material `Main_Lighting_NoShadow`; mode 1 `Main_Lighting_CSM` and creates the CSM object with
  **4 cascades** and the map side; mode 2 `Main_Lighting_RTW` and creates the RTW object with the map side and a **512**
  importance size. Any other mode throws "Invalid light mode". Applying the options calls it with
  `table[shadow quality]`; when the renderer creates its shadows lazily before that, it passes **2048** (**Verified**).
- **Unknown**: which label belongs to which size. The options list adds "2048 (nice)", "1024 (poor)" and "4096 (some video
  cards may freak out)" with the tags 0, 1 and 2, while the size table maps 0, 1, 2 to 1024, 2048, 4096; the two disagree for
  0 and 1, and it is not settled whether the tag is the item's data or its position.
- **Observed**: this install's `settings.cfg` has `shadow mode=0`, `shadow quality=0`, `Shadow Range=5000`, i.e. shadows off.
- The tooltips: "Turn off shadows if you have problems with performance"; for the range, a longer range "reduces the shadows
  resolution/quality" (one map, or one set of cascades, is stretched over it).

## The CSM mode (mode 1)

### The map (Verified: exe CSM creation)

- **One** square texture of the quality's side, format code 33 (Ogre `PF_FLOAT32_R`, one 32-bit float channel; **Observed**:
  the enum value read against Ogre's `PixelFormat` list), a render target with its own depth buffer. The cascades are
  **viewports in a ceil(√n) × ceil(√n) grid** of it: 2 × 2 tiles of half the side each (512² per cascade at 1024, 1024² at
  2048). Tile `i` is at column `i / 2`, row `i % 2` of the grid (the viewer's order is row-major; only the bookkeeping differs).
- Each viewport draws only the shadow-caster visibility layer, render queues **10 to 80**, with the scene manager in its
  shadow-texture stage (so every material's `shadow_caster_material` is used); cleared to white (depth 1) per frame.
- The value stored is the caster's depth (below), not a hardware depth buffer: the receiver reads it with point sampling
  (`filtering none`) and a white border (`tex_address_mode border`, colour 1: outside the map is lit).

### Splits (Verified: exe CSM split set-up)

Called with near **1**, far **`Shadow Range`**, 4 cascades and λ **0.95**:

```
split[0] = 1,   split[4] = Shadow Range
split[i] = 0.5 · ( λ · near · (far / near)^(i/4) + (1 − λ) · (near + (far − near) · i/4) )      i = 1..3
```

The practical split scheme, with the **inner splits halved** (an extra factor 0.5 in the exe). For the default range:
**1, 35.3, 96.1, 376.2, 5000**. The receiver's `csmParams[i].x` is `split[i + 1] − split[0]`.

### Cascade fitting (Verified: exe per-cascade fit, disassembly where the decompiler was unclear)

For cascade `i` (serving view depths `split[i] .. split[i + 1]`), every frame:

1. **Light view.** A rotation built from the direction the sky controller keeps for the sun, `d`, towards the sun
   (**Observed**: the direction field is read from the sky controller object; "towards the sun" follows from the depth order
   below). The view's z axis is `−d`, its up is world **+Y**, or world **+Z** when `|d·Y| > 0.985` and `|d·Y| > |d·Z|`,
   made perpendicular to z; x = up × z. Depth therefore **grows away from the sun**.
2. **Stable size.** `D = max( √((f + n)² k + (f − n)²), 2 f √k )` with `n = split[i]`, `f = split[i + 1]` and
   `k = (aspect · t)² + t + t`, `t = tan(fovY / 2)`. This is the classic rotation-invariant bounding-sphere diameter of the
   slice, except that the textbook `k` is `(1 + aspect²) t²`: the exe adds the tangent twice instead of squaring it
   (**Verified** in the disassembly), which only makes the cascades larger.
3. **Box.** The camera's frustum from **its own near plane** to `split[i + 1]` (Ogre's camera with the far plane set to the split;
   the near plane is not moved to `split[i]`) is transformed to light space and bounded by an axis-aligned box. Its depth range is
   grown at each end by `max(0.025 · depth, 2) + D / 65536`. The size used is `max(D, box size)` per axis.
4. **Snapping.** The translation `−centre` is floored to whole texels in x and y (`texel = D / tile pixels`) and to `D / 65536`
   in z, so the map moves in whole texels as the camera moves and keeps its size as it turns (no shimmering).
5. **Projection.** A plain scale `2 / size` on all three axes (centred box, z −1..1 in Ogre's GL convention; the Direct3D 11
   render system turns it into 0..1). Near-plane culling of the shadow camera is **disabled**, and the caster vertex program
   clamps clip z at 0: casters between the box and the sun are flattened onto the box's near face ("pancaking").
6. **Per-cascade parameters**, written before that cascade is drawn:
   - caster bias `biasParams = (fixed, 4.0, 0.04, 0)` with `fixed = max(0.75 · |2 / size.z|, 0.001)`, i.e. about
     **1.5 world units** of depth, or 0.1 % of the box depth when that is larger (PCF branch; a VSM branch exists with other
     constants and is not used, its flag is off);
   - `csmParams[i].y`, the PCF radius, `max(0.3 · 2 / size.x, 0.8 / tile pixels)` in the atlas's UV units;
   - `csmScale[i]`, `csmTrans[i]`: the cascade's transform relative to cascade 0's light view (`shadowViewMat`, the rotation
     with the translation zeroed), including the tile's place in the atlas and the texture flip.
7. Fixed for the whole map: `shadowParams = (0, 0.05, 0, ·)` (debug off, a layer overlap 0.05 used only by an unused
   cascade-selection variant), the UV bounds of each tile inset by 8 texels (same unused variant).

### Casters (Verified: `shadowcaster.hlsl`, `shadows.material`, `terrain.hlsl`, the `.material` files; Observed where noted)

- The caster stores `z + min(maxSlope, slope · |(∂z/∂x, ∂z/∂y)|) + fixed` (the depth's screen-space gradient length).
- `cull_hardware clockwise` (Ogre's default) on the standard and terrain casters. With the light view above (right-handed,
  depth growing along +z, x and y kept), the faces whose normals point **away from the sun** are the ones drawn: in effect the
  game renders the back faces of closed meshes and of the terrain into the shadow map (**Observed**: derived from the matrices
  and the culling mode, not seen in a capture). That hides self-shadowing acne on lit faces.
- Which materials cast with what (from `shadow_caster_material`):
  - terrain (`Terrain`, `DistantTerrain`) → `TerrainShadow`: the terrain's own vertex program (morph between LOD levels), no
    alpha;
  - `StaticObject` (buildings and objects), characters (`character.material`), birds, distant towns, map features, previews
    → `standardShadowCaster` (no alpha test); skinned meshes → `SkinnedShadowCaster`; buildings under construction →
    `ConstructionShadowCaster` (scaffold texture cut-out above the built height);
  - foliage and grass → `FoliageShadowCaster`: alpha-tested (`clip(alpha − threshold)`), **no culling**; farm plants →
    `FarmShadowCaster`. At run time the foliage builder clones `<material>_shadow` from the technique's caster and puts the
    mesh's texture into its first unit (**Verified**: exe foliage material builder); the object material builder also sets
    casters (not traced further).
  - **Unknown**: the alpha-test `threshold` of these casters (`shadows.program` gives no default; the foliage colour
    materials use 0.4, grass 0.6), and whether the grass is drawn into the map at all (its material names a caster, but its
    render queue and caster flag were not traced).
- Objects that do **not** cast (`setCastShadows(false)` callers, identified by the strings they use): editor gizmos, effect
  objects (emission and light effects), interior masks, the water planes and the reflection camera's helpers, debug lines,
  some instanced-node helpers (**Observed**: the callers were not all traced to their objects). Characters' bodies and the
  sun light set it **true**; other entities keep Ogre's default (cast).

### Receiver (Verified: `deferred.hlsl` `main_fs` with `CSM`, `shadowFunctions.hlsl` `computeShadowMultiplier`)

Every G-buffer pixel receives (the lighting pass is full screen); forward-drawn things (water, sky, particles) do not.

1. **Cascade by view depth**: the pixel's clip-space z (≈ view depth − near) against `csmParams[i].x`; the first cascade whose
   split is beyond it. **Beyond the last split the term is 1**: no fade-out at the far end (a fade is commented out in the RTW
   branch only).
2. Light-space position `shadowViewMat · worldPos`, then `csmTrans[c] + csmScale[c] · that` → atlas UV and depth.
3. **PCF, 12 taps** ("HEX12"): offsets on a hexagonal lattice around the point (radii 1 to about 2.65, one of the listed offsets
   is a duplicate of another), shifted by `noise · 0.25` along x, turned by the angle `noise · 2π` and scaled by
   `csmParams[c].y · 0.3`. `noise` is the red channel of a white-noise texture (`white-noise.png`, bound in the `WarpMap` slot)
   read at **atlas UV × 1024**, so the pattern is fixed to the ground, not to the screen. The offsets are first **projected onto
   the plane of the pixel's normal** in light space, so the taps follow the surface (their depth changes with it).
   `lit = mean(stored ≥ tap depth)`; no receiver bias (`csmParams[c].z = 0`).
4. `shadow = ambient + lit · (1 − ambient)` with the ambient level forced to **0** by the lighting shader.

Other kernels exist behind defines (7-tap hexagon, 3 taps, 8-tap Poisson disk, 4-tap square, a single tap, VSM with a
Chebyshev bound); the shipped defaults select HEX12 with jitter.

## How the term enters the lighting (Verified: `deferred.hlsl`)

The sun's colour is multiplied by it before the BRDF: `CalcPunctualLight(N, L, V, gloss, specColour, lightColour · shadow,
translucency)`. So **the sun's diffuse, its specular and the translucent back-light are shadowed; the ambient and
environment light are not**. The debug mode 4 of the lighting shader shows the term.

## The RTW mode (mode 2; Verified from the shaders and the exe RTW class unless noted; not reproduced yet)

One map of the quality's side whose texels are redistributed towards where the camera needs them (Rosen's RTW, the
"backward" importance analysis):

1. **Fit.** A light view along the sun's direction with the up taken from the camera's orientation (another camera axis when
   the two are within `|cos| > 0.985`), boxed around the camera frustum out to `Shadow Range`, scaled `2 / size`, no snapping.
   `shadow_matrix` (texture flip and bias × projection × view × the camera's translation) maps camera-relative positions.
2. **Importance.** A grid of points, one per 4 × 4 screen pixels (screen size × 0.25 each way), reads the G-buffer's depth and
   normal, is projected into light space (with the depth row zeroed) and writes `clamp((1 − depth / range)³, 1e−4, 1) ·
   (1 + direction_bonus · saturate(N · view axis))` (`direction_bonus` 2) into a 512² float map cleared to 0.
3. **Warp.** `RTWWarp` pass 0 reduces it to 512 × 2 (row 0: the maximum of each column, row 1: of each row); pass 1 spreads
   each maximum to its neighbours over 10 texels with weights 0.75ᵏ; pass 2 builds a 513 × 2 map of offsets placing each
   texel at the normalised running sum of the importance (texels outside the important range go to ±1.05).
4. **Shadow map.** Casters' technique `RTWShadow`: the vertex program moves clip x and y through the warp map (with a
   tessellation hull/domain pair when the GPU supports it, else `RTWShadow_Legacy`, "Tessellation not supported. Using legacy
   RTW shader"). Caster bias `(0, 6, 0.002)`.
5. **Receiver** (`RTWShadow`): the position through `shadow_matrix` and the warp map, 3 × 3 Gaussian-weighted taps 1e−4 apart
   (jittered by a small fixed table), each a soft compare `1 − saturate((depth − stored − bias) / bias)` with `shadow_bias`
   **3e−5** (the shared-parameter default; **Observed**: no setter found) plus an edge bias near the map's border that grows
   with distance beyond 0.6 × range. The code after the `return` (a border fade) never runs.

## What the viewer does

`--world` draws the **CSM mode** by default (`--no-shadows` turns it off, `--shadow-quality <0|1|2>` picks the side from
the table, default 1 = 2048², `--shadow-range <u>`). It follows the facts above, with these differences:

- **Backend split.** `Meitou.Data.World.ShadowCascades` (no GL) does the splits, the light view, the stable size, the box,
  the snapping (in double, in absolute light space, so far from the origin the map still moves in whole texels), the bias and
  filter radius per cascade, the caster culling planes (four sides and the far end, no near plane) and the receivers' matrices
  (relative to the eye, so the GPU sees only small numbers). `ShadowPass` is the GL part: a `DEPTH_COMPONENT32F` atlas,
  depth clamp for the pancaking, the bias written as `gl_FragDepth` (the same formula on the same 0..1 depth), one uniform
  block for the receivers and one for the casters' bias.
- **Casters** are the renderers' own geometry through depth-only entry points: the terrain's patches (levels chosen from the
  camera's eye, as in the picture), objects (including TERRAIN-mode meshes), foliage meshes with their cut-out mask. The
  renderers' culling stays as it is (the terrain culls back faces as the game's caster does; objects are drawn two-sided).
  Grass does not cast. Foliage casts out to 1.2 × the range (a saving; the game's limit is Unknown).
- **Receiver**: 12 taps on the viewer's own hexagonal ring (six at radius 1, six at 2.4 between them) with the game's radius,
  0.25 shift and per-texel random turn (a hash of the atlas texel × 1024 instead of the noise texture), the taps kept on the
  surface plane (computed in light-space units), and **hardware comparison with bilinear weights** for each tap (the game
  point-samples), which smooths the penumbra.
- Below a sun height of 0.02 the viewer draws no map (the term is 1). The game keeps drawing it with the light on the horizon
  (its lighting direction has y clamped to 0) while the sun still has colour down to −0.2; there the viewer shows no shadows.
- The camera's near plane can lie beyond the first split (the viewer's near plane grows with the eye's height, up to 200): such
  a cascade covers nothing on screen and is not drawn.
- `--debug-shadows 1` shows the four cascade tiles; `2` the term over the scene's surfaces tinted by cascade (red, orange,
  yellow, green: the game's own debug colours), reconstructed from the near depth slice with the depth's own slope as the
  normal; `3` multiplies the term over the finished picture.
- The lighting itself (`kenshiLight`) applies the term once `AtmosphereShaders` includes `ShadowShaders.Functions` and
  multiplies the sun by `kenshiShadow(world, n)`; the program builder already binds the blocks and the map's unit.

### Meitou shadows (the `shadows` switch)

Not the game's: the remaster's choice, on by default (F5 / `--faithful shadows` gives the CSM above, pixel-identical). Same atlas,
`shadow quality` and `Shadow Range`; `ShadowPass.Meitou.cs`, `MeitouShadowFit` (Meitou.Data), `MeitouShadowShaders`,
`TerrainShadowMap`. The receiver is `kenshiShadowMeitou`, chosen by `uShadowAtlas.z = 1` in the receiver block; the game's body is
`kenshiShadowFaithful`, `kenshiShadow` dispatches.

- **Fit.** Splits by the practical scheme (λ 0.8, no halving) from the camera's near plane, rounded down to a power of 1.25 so the
  splits change only in steps while zooming, to the range: all four cascades cover something visible (with the game's splits the
  first two lie in front of a near plane of 100-200 in most views). Each cascade is a light-space square around the bounding sphere of
  its own slice (the textbook sphere, smaller than the game's), its centre snapped to whole texels (no shimmering), 2-8 % larger than
  the sphere as slack for the schedule, its depth reaching two radii further towards the sun so casters there keep their depth.
  Example (The Hub from 300 units): texels 0.64 / 1.58 / 3.71 / 10.0 units against the game's 0.09 / 0.24 / 0.94 / 12.4 of which
  only the last two are on screen.
- **Schedule.** Cascade 0 every frame, 1 every other frame, 2 and 3 every fourth frame on alternating frames (at most two a frame),
  each kept with the matrices it was drawn with (the receiver uses each cascade's own), only its tile cleared. A cascade is redrawn at
  once when the current slice's sphere (plus the filter's reach) no longer fits its box, when the splits or the map size change, or
  when the sun jumps by more than 2°. A point outside a stale box falls through to the next cascade. Newly streamed casters reach the
  far cascades up to three frames late.
- **Receiver.** The point is moved off its triangle by a normal offset of 0.5-2 texels (more at grazing light; the triangle's normal
  from screen derivatives, not the shading normal), then a blocker search (8 taps in a half-resolution map of the nearest depths,
  rebuilt for the tiles drawn) gives the penumbra: the blocker's distance × tan(0.35°) (a sun of 0.7°), at least 1 texel, at most 3
  world units; 16 bilinear comparisons on a Vogel disk of that radius, rotated per pixel by interleaved gradient noise (changing every
  frame when a temporal anti-aliasing pass runs, fixed otherwise), their depths following the receiver's plane. The last 15 % of each
  slice blends into the next cascade (whose fit covers it); the last 15 % of the range fades to lit.
- **Terrain beyond the range.** For the sun's direction, per sample of the whole-world height grid (2049², 144 units), the height of
  the top of the shadow the land towards the sun casts there and the distance to that land: 13 passes of a doubling sweep (each looks
  2^k samples further towards the sun, lowered by the sun's slope) into RG32F, rebuilt when the sun turns by more than 0.1°. A point
  is shadowed below that height (bias 20 units, softness 6 units plus the occluder's distance × the sun's angular radius); one fetch.
  It fades in from 55 % to 90 % of the range and combines with the cascades by the minimum, so it agrees with them where both apply
  (Observed: the same picture at range 5000 and 9000 at 07:00).
- **Costs:** see the viewer's measurements in [../viewer.md](../viewer.md#shadows).
