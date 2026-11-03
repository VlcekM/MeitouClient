# Birds

Flocks of birds in the sky (a3-k021). Meitou: `BirdFlockData` (data), `BirdFlocks` (flight, `Meitou.Game`), drawn as loose meshes by `DebrisRenderer` (`GameHost.UpdateBirds`, `WorldFrame.BirdPieces`).

## Original (decompile of kenshi_x64.exe)

- **Verified**: `BirdManager` is a singleton (`FUN_140262270`, 0x170 bytes, constructor `FUN_140260f50`) on its own thread named "Birds" (kicked with the real dt by `FUN_1403beeb0`, see [game loop](game-loop.md)); its flight step is `FUN_140262830`, its loader `FUN_140261090`.
- **Verified** (loader): every WILDLIFE_BIRDS (68) record becomes a flock kind with `speed`, `height`, `ground height`, `ground strength`(+0x3c is set to 100 in code), `contour strength`, `distance`, `spawn height min/max`, `scale min/max`, `spawn count`, `flocking`, `circling`, `mesh`, `texture`. The mesh is made with the Ogre mesh manager, the material is a clone of `HWBirds` (`data/materials/deferred/birds.material`: hardware-instanced static mesh, the standard object fragment program, no wing animation in the vertex shader `birds.hlsl`) with the record's `texture` as `DiffuseMap`.
- **Verified**: where a kind flies comes from the BIOME_GROUP `birds` reference list: per group an amount (the reference's first value; 2 to 15 in the install's data) kept in a map keyed by the group's area colour (`index`).
- **Verified**: the flight step has per-bird states 1 to 6 and 8 (spawn/return, free flight, following a flock mate, circling, circling an attractor), a position `+0`, a quaternion `+0xc`, steering with a turn limit, vertical steering to a target height, and `BirdAttractorObject` perches (`FUN_140264a10`..`FUN_140268350`). The constants (`DAT_1416abe..`) and the state transitions were not decoded (**Unknown**).
- **Observed** (install data): the base record `WILDLIFE_BIRDS2218` and Newwworld's `BowlBirdsTEST` use `data/meshes/birdtype1.mesh` (about 26 x 5.5 x 7.5 units, wings along x), 10 birds, speed 220 to 500, height 200 to 800, `distance` 500000.

## Meitou

- Flocks per kind = the amount of the BIOME_GROUP under the camera (`WeatherAreas.CameraCellOf`, as the weather's lookup); spawned on a ring round the camera, replaced when they leave 1.2 x the radius. **Engine choice / Unknown**: `distance` is read as a squared radius (500000 gives about 707), `ground height` and `ground strength` are unused, the steering (a leader per flock with followers in a loose formation, circling kinds turning at a constant rate, others wandering and turning back at two thirds of the radius, climbing at a quarter of the speed to `height` over the ground blended by `contour strength`) is Meitou's own.
- Birds run on the display's real time (the original's thread also takes the real dt), so they do not stop with the game paused and are not part of the simulation or the saves.
- Not done: the record's `texture` (the birds are drawn with the mesh's own material: untextured when it resolves to none), attractor perches, wing motion (the original has none either).
