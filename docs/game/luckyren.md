# luckyren Mode

An easter egg (an On/Off switch in the settings menu, off by default, outside the all Faithful / all Meitou switches, `UserConfig.LuckyrenMode`): a character killed by a
hit bursts. No original behaviour; every rule here is an engine choice. Implementation:
`World.Luckyren`, `CombatSystem` (melee `LandBlow`, ranged `BoltHit`), `BodySystem.Burst`,
`Debris`, `DebrisRenderer`.

## Simulation

- A hit whose medical events include a death sets the victim's `CharacterCold.Burst` to the blow's
  direction (melee: attacker → victim; bolt: its flight). Bleeding out or other deaths do not burst.
- At the end of that tick (`BodySystem.SlowWorld`): every top-level inventory item (worn, carried,
  fitted limbs; a backpack with its contents) drops as a ground item within 30 units (section reset
  to `main`, place and yaw from `RngPurpose.Luckyren`, height the walkable ground under it), the character leaves the uniques registry and
  is removed. No corpse remains to loot or carry.
- An `Explosion` blood mark carries the position, blow direction, race id and blood colour
  (amount 0 for a bloodless race, `bleed rate` 0: no blood, but bones still fly).
- Play-dropped ground items keep the `GroundItems.Limit` (default 1024, "Dropped items kept" up to 4096): many bursts evict the oldest drops.
- The host sets `World.Luckyren` from the option every frame, so it changes a run's outcome.

## Display (host)

- Blood (with the `Blood` setting on, terrain only, not above 20 units over it): a pool of size 1.5
  at the spot and 24 splashes (size 0.3–0.5) up to 60 units away, biased along the blow.
- Pieces: the race's `severed limbs` items' meshes (`ground mesh`, else `mesh`) at their own size and
  12 nest bones (`bone1..14.mesh`, beast-sized 0.5–3.5 m, all scaled by 0.12 ±15% so they keep their relative sizes; textured with
  `bone1`'s item material, **Verified** gamedata.base: the others have no record and all name only `StaticObject`). Thrown from 10 units
  up at 30–90 units/s outwards (biased along the blow) and 30–80 up, spinning; gravity 98 units/s²
  (taking 1 unit = 1 dm, [terrain.md](../formats/terrain.md)). On the ground they bounce (vertical 0.35, horizontal and spin 0.6), leave a small
  splash on the first contact, and settle on their thinnest side once slow. Above the terrain (an
  upper floor) they land on the burst's level.
- They move in game time (tick count plus the simulation's alpha each frame, so smoothly at the display rate), last as long as a corpse (`BodySystem.CorpseHours`), at most 400, and are
  not saved; a new world clears them.
- Drawn in the scene pass before the characters, colour only (the alpha is the
  characters' SSAO mask), lit with `kenshiLight` under the game sky; no shadows or motion vectors.
  Textures come from the object material resolver; a mesh without one is bone-coloured.
- A race that bleeds also throws 4 pieces of raw meat (`Item_Meat_Raw.mesh`, rebirth.mod's "Raw Meat", a 39 × 26 cm slab, at 0.5–0.9 size).
- Blood particles (with a bleeding race): `Bloodsplash01` at the burst for 0.3 s and 5 `HackBlood-system` sprays (the system emits along +Z;
  turned to random directions biased along the blow, 30–70% up) for 0.4–0.7 s, both at twice the sprite size and growth and 3 times the particle life;
  `Bloodsplash01` for 0.15 s (1.3 times the size, 1.5 the life) where a limb or meat first lands.
  One-shot units (`ParticleRenderer.Burst`) drawn with the map's placers, on the frame clock.
- Sounds (the game has no gore event: **Verified**, hashing gore-like names against all 148 events of the banks): `Impact` with `Impacts` =
  `Heavy_Hit` at the burst and `Light_Hit` where a limb or meat first lands, on the pooled impact objects; within 400 units of the eye also
  `Item_Drop` with `Inventory_Items` = `Meat` on the GUI object (a 2D sound: no distance falloff, hence the limit).
