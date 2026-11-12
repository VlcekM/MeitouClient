namespace Meitou.Data.World;

/// <summary>
/// Terrain blood decals (docs/game/blood-decals.md): the four native <c>Blood</c> folders followed by Meitou effects.
/// </summary>
public enum TerrainDecalType : byte
{
    /// <summary>
    /// 0 <c>line</c>: nothing known makes these.
    /// </summary>
    Line = 0,
    /// <summary>
    /// 1 <c>splash</c>: a hit.
    /// </summary>
    Splash = 1,
    /// <summary>
    /// 2 <c>pool</c>: under a bleeding character that stands still or is carried.
    /// </summary>
    Pool = 2,
    /// <summary>
    /// 3 <c>smear</c>: behind a bleeding character that moves.
    /// </summary>
    Smear = 3,
    /// <summary>
    /// A temporary imprint left by a bloodied foot. Kept out of native saves.
    /// </summary>
    Footprint = 4,
}

/// <summary>
/// One blood decal as the game keeps it per decal cell (<c>TerrainDecalsManager::DecalInfo</c>, 24 bytes; the fields of a TERRAIN_DECALS instance,
/// docs/formats/save.md). A decal that crosses cell edges is stored once in every cell its corners touch, each copy placed relative to its own cell.
/// </summary>
/// <param name="Key">The cell (<see cref="TerrainDecalCells.Key(float, float)"/>).</param>
/// <param name="Variant">Index of the texture within the type's folder.</param>
/// <param name="LocalX">The centre's X in the cell, 1/1000 of the cell (outside 0..1000 for the copies in neighbouring cells).</param>
/// <param name="LocalZ">The centre's Z, likewise.</param>
/// <param name="HalfWidth">Size × the texture's width × 10.</param>
/// <param name="HalfHeight">Size × the texture's height × 10.</param>
/// <param name="Rotation">The angle in radians × 1000: the texture's width axis points along (sin, cos) in X/Z.</param>
/// <param name="Colour">ABGR (A in the top byte, R in the bottom): the race's blood colour and the decal's opacity.</param>
/// <param name="Life">Seconds of game time left (99 when made; the opacity is at most this), as a save holds it; inside a
/// <see cref="TerrainDecalStore"/> the <see cref="TerrainDecalStore.Clock"/> time it runs out at.</param>
public record struct TerrainDecal(uint Key, TerrainDecalType Type, byte Variant, short LocalX, short LocalZ, short HalfWidth, short HalfHeight,
    short Rotation, uint Colour, float Life);

/// <summary>
/// The cells blood decals are kept and drawn in: the terrain quadtree's level 10, 1024 × 1024 squares of 288 units over the world
/// (docs/game/blood-decals.md). A cell's key is 1 &lt;&lt; 20 with the cell's column bits on the even bits and its row bits on the odd ones (the
/// quadtree path, <b>Verified</b> against save keys).
/// </summary>
public static class TerrainDecalCells
{
    /// <summary>
    /// The terrain quadtree level of the cells.
    /// </summary>
    public const int Level = 10;
    /// <summary>
    /// Cells per world side.
    /// </summary>
    public const int PerSide = 1 << Level;
    /// <summary>
    /// The edge of a cell in world units (288).
    /// </summary>
    public const float Size = (float)WorldLayout.WorldSize / PerSide;
    /// <summary>
    /// The bit above the cell bits that every key has.
    /// </summary>
    const uint Marker = 1u << (2 * Level);

    /// <summary>
    /// The cell holding X/Z (clamped to the world).
    /// </summary>
    public static uint Key(float x, float z) => Key(Column(x), Column(z));

    /// <summary>
    /// The key of the cell at <paramref name="column"/>, <paramref name="row"/>.
    /// </summary>
    public static uint Key(int column, int row) => Marker | Spread((uint)column) | Spread((uint)row) << 1;

    /// <summary>
    /// The column (or row) of a coordinate, clamped to the world.
    /// </summary>
    public static int Column(float x) => Math.Clamp((int)MathF.Floor((x + WorldLayout.HalfWorldSize) / Size), 0, PerSide - 1);

    /// <summary>
    /// The column and row of <paramref name="key"/>.
    /// </summary>
    public static (int Column, int Row) Cell(uint key) => ((int)Gather(key), (int)Gather(key >> 1));

    /// <summary>
    /// The X/Z corner of <paramref name="key"/>'s cell nearest the world's negative corner.
    /// </summary>
    public static (float X, float Z) Origin(uint key)
    {
        var (c, r) = Cell(key);
        return (c * Size - WorldLayout.HalfWorldSize, r * Size - WorldLayout.HalfWorldSize);
    }

    /// <summary>
    /// The low 10 bits of <paramref name="v"/> moved to the even bits.
    /// </summary>
    static uint Spread(uint v)
    {
        v &= 0x3FF;
        v = (v | v << 8) & 0x00FF00FF;
        v = (v | v << 4) & 0x0F0F0F0F;
        v = (v | v << 2) & 0x33333333;
        v = (v | v << 1) & 0x55555555;
        return v;
    }

    /// <summary>
    /// The even bits of <paramref name="v"/> packed into the low 10 bits.
    /// </summary>
    static uint Gather(uint v)
    {
        v &= 0x55555;
        v = (v | v >> 1) & 0x33333333;
        v = (v | v >> 2) & 0x0F0F0F0F;
        v = (v | v >> 4) & 0x00FF00FF;
        v = (v | v >> 8) & 0x0000FFFF;
        return v & 0x3FF;
    }
}

/// <summary>
/// One blood decal on the walls, floors and objects (the Meitou <c>bloodsurfaces</c> switch, docs/game/blood-decals.md "Blood on surfaces"), as a
/// save holds it (<see cref="Save.SaveSurfaceDecals"/>).
/// </summary>
/// <param name="Centre">Its 3D centre (a splash's lifted above the feet).</param>
/// <param name="Angle">Radians: the texture's width axis points along (sin, cos) in X/Z.</param>
/// <param name="Size">The game's decal size (half extents = size × texels × 288 / 4096).</param>
/// <param name="Life">Seconds of game time left.</param>
/// <param name="Colour">ABGR, as <see cref="TerrainDecal.Colour"/>.</param>
/// <param name="Floor">Drawn only on up-facing pixels at its height (a pool, drip or smear); else on any surface facing it (a splash).</param>
public readonly record struct SurfaceBloodDecal(TerrainDecalType Type, byte Variant, System.Numerics.Vector3 Centre, float Angle, float Size,
    float Life, uint Colour, bool Floor);