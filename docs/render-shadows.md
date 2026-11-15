# Shadows

`ShadowPass`, `ShadowLod`, `ShadowFogCap`, `ShadowZoneBox`; original cascade/settings/shader
rules: [formats/shadows.md](formats/shadows.md). Engine API: [renderer-native](renderer-native.md).

## Modes and resources

Receiver plane (`msTileV`; **Code reading**, not compared on a GPU): the filter's taps and the blocker search shift their reference depth by the receiver plane's tilt along
the offset, `dot(o, slope)` in tile UV. `ReceiverTile` flips v on Metal, so light-space y runs against v there and the plane's y tilt flips with it; `msTileV` takes the sign from the
tile's v axis against the light's y axis (-1 on Metal, +1 on Vulkan, whose slope is unchanged). Without it, taps on one side of a lit slope on Metal compared against a reference
up to 2 x radius x tilt too deep, beyond the bias.

## Scheduling and casters

`shadow-coarse` / generated object levels select geometry against cascade texel size, not colour
pixel size. Preserve silhouette and alpha coverage; impostor casters have their own fade/plane.
`shadow-fogcap` can shorten reach only when placed fog conservatively hides every relevant
receiver. Shadow-map rendering and shadow receiving are distinct costs; omit receiver work only
where lighting cannot contribute. Minimum-radius impostor filter omits blocker search.

## Traced sun

**Frame.** `kenshiLight` leaves the sun out of the colour of every lit surface but impostors (`atmoReceiver` -1: the quad is not the surface), at every depth
(`ShadowPass.Traced`; with far cascades as far as the handoff, below), and writes it, shadowless but for clouds, relief, the terrain map and the landmark map (the cascades left out, `msTraced`), to a third scene
attachment (RGBA16F, location 2, `WithAlbedo`), before the haze. Bit 16 of the surface target (RGBA32UI) marks such a pixel, bits 17-18 its receiver (0 solid, 1 grass,
2 foliage, 3 character). After the characters, before `CaptureScene` (refraction sees the sun), a compute pass traces one ray a pixel into an RGBA16F mask (visibility,
blocker distance), the penumbra filter softens it, and a fullscreen pass adds term x mask x the haze's multiplier (`atmoApplyCoefficients`).

**Rays** go to the sun's centre, the same every frame (no noise for the upscaler to average, no history to trail). There is no receiver identity as in KenshiNative's
G-buffer, so what it skips by name is skipped along the ray. Solids leave the plane the depth shows by half a pixel's footprint, slope-scaled; cards leave from the sun's face
and skip 3 footprints plus 0.5. Queries, each after the last missed:
- objects (as far as the shadows' range, at least the probes' 6000), foliage, small foliage (`0x10`: radius 6 and up; see "Casters by size on screen"), characters (`0x40`), alpha-tested in
  candidates; a part an object's material cuts reports candidates through `ForceNoOpaque`;
- grass (`0x20`), only as far as the ray can stay under the tallest blade, fading out by amount (not noise) over the last quarter of its reach.

**Casters by size on screen** (`GiScene.CasterPixels`, 3): small foliage and characters cast while their bounds are at least 3 pixels across at the frame's lens (`PixelsPerRadian`), up to the shadow distance; small foliage takes the largest on screen first within its 5000 instances. Fixed ranges (3000 for small foliage, 4000 for characters) left a tree or a character plainly in view without a shadow, further off or through a long lens (**Verified**, t_flats_shore, the same tree and character from 2500, 5000 and 9000 units at a matching lens: shadows at 2500 only before, at all three after). Through a lens longer than the game's the object range grows by the lens's factor, to 24000 at most. Cost at Gut (13184 foliage instances against 4326 with no small foliage): 0.02 ms of GPU (**Verified**, paused pair).

**Characters** are in the rays (`TracedCharacters`, [render-lights](render-lights.md#traced-lamp-shadows)): the nearest 128 whose shadow can be in view, within 4000 and past it by their size on screen ("Casters by size on screen"), at the level the
colour pass draws, skinned with the frame's palettes, so a surface's ray leaves the same triangles it drew and a body shadows itself only where it does (armour layers, hair over
a face). No character layers are drawn.

**Not traced** (they cast no sun shadow while traced): construction sites (drawn clipped by their progress; the whole mesh would cast the finished building), debris
(bolts, dropped items) and birds (no caster in any mode), particles, objects past the range or drawn only as impostors, foliage past the shadows' range (nearest first within the instance budget; small foliage and characters under 3 pixels across), grass
past the reach, characters past the 128 nearest. Landmarks keep their map.
