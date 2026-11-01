# Havok binary tagfile (`.hkt`)

Kenshi's `.hkt` files (navmesh tiles, animations) are Havok 2014 binary tagfiles. Reader: `Meitou.Data.Havok.HavokTagfile` (clean room).
Facts from `hkBinaryTagfileReader` (`FUN_140c1e950` and callees, **Observed** by decompile); **Verified**: all 3995 `navtiles/*.hkt`
parse to the end with the rules below.

## Encoding

- Header: `1E 0D B0 CA CE FA 11 D0` (u32 `0xCAB00D1E`, `0xD011FACE`).
- **Int**: first byte bit 0 = sign, bits 1-6 = low 6 bits, bit 7 = more; each further byte adds 7 bits. Negative = −value.
- **String**: int n; n > 0: n UTF-8 bytes, appended to the string table; n ≤ 0: table entry −n. The table starts `["", null]`.
- **Tags** (ints) until 7: 1 file info, 2 class, 3 object, 4 object remembered, 5 back reference (int index), 6 null, 7 end.
- **File info**: int version (2..5); version > 3: SDK string (`hk_2014.2.0-r1`); version > 4: u16, u16 count, count × u16 (meaning unknown).
- **Class**: name string, int version, int parent (index into the class list, which starts `[null]`), int member count; per member: name string,
  int type, int tuple size if type & 0x20, class name string if the kind is 8 or 9. A member may name a class described later.
- **Type**: low nibble 0 void (not stored), 1 byte, 2 int, 3 real, 4/5/6/7 vec4/8/12/16 (reals), 8 pointer, 9 struct, 10 string; 0x10 array, 0x20 tuple.
- **Object** (tag 3/4): int class index; tag 4 appends it to the object list (starts `[null]`). Then presence bits for every member, the parent's
  first (⌈n/8⌉ bytes, LSB first), then each present value:
  byte raw; int an int; real 4 bytes; vecN N floats; string; struct an inline object without class index; pointer an int index into the object
  list (forward references allowed; the root is entry 1); array int count then elements; tuple elements of its size.
- **Elements**: bytes raw; reals raw; ints: (version > 2) int width 4 or 8, then ints; strings; pointers ints; arrays per element count + elements;
  vec4: int stored components (3 or 4) then that many floats each; vec8/12/16 raw; **structs column-wise**: presence bits for the struct's
  members, then for each present member all elements' values (tuples and arrays per element).

## Navmesh tiles

`data/newland/land/navtiles/tileX.Y.hkt` is zone X.Y. Root `namedVariants`: `NavMesh` (`hkaiNavMesh` v14), `Graph`
(`hkaiDirectedGraphExplicitCost`), `Mediator` (`hkaiStaticTreeNavMeshQueryMediator`), `Hash` (`hkStringObject`, hex); then per interior
the same four plus `Info` (`uidhex;x;y;z`, world units) after its mesh. **Verified** (5244 groups, 1249 interiors).
- `hkaiNavMesh`: `faces` (`startEdgeIndex`, `numEdges`, `startUserEdgeIndex` −1, `clusterIndex`), `edges` (`a`, `b` vertex, `oppositeEdge`,
  `oppositeFace` −1 at an end, `flags` 4 on the sample), `vertices` vec4, `streamingSets` (per neighbour tile: `thisUid`, `oppositeUid`,
  `meshConnections` face/edge pairs joined across), `faceData` / `edgeData` (stride 1), `aabb`.
- Interior joins: an interior mesh's `streamingSets` hold one set, `thisUid` its uid, `oppositeUid` the tile's exterior uid, whose
  `meshConnections` (`faceIndex`, `edgeIndex`, `oppositeFaceIndex`, `oppositeEdgeIndex`) pair its door edges with exterior edges; the exterior carries
  the matching set back. **Verified** (tiles 18.35, 19.29, 20.32, 26.13, 43.11, 47.16, 48.13, 48.45, 50.33): every interior has one except two in
  48.13 (`110371`, `150371`); paired edges' midpoints lie within 2 in height for 21 of 21 connections in 18.35, 47 of 51 in 43.11 and
  49 of 51 in 47.16 (the rest 7..24, not examined).
- Exterior vertices: world = zone origin + 10 × (x, y, z). **Verified**: median height off the heightmap 0.0 in 5 zones; swapped axes do not fit.
- Interior vertices: world = Info position + 10 × (x, y, z). **Observed** (tile20.32: `600815` lands on the Storm House `4933-Newwworld-INGAME`
  floor and upper storey, `190815` on Storm House `32-`'s floor; both Infos lie about (−155, −67, −153) from their Storm House's origin).
