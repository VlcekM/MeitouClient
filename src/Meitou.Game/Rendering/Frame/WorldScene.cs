using System.Numerics;
using Meitou.Data;
using Meitou.Data.World;
using Meitou.Game.Rendering.Objects;

namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// The loaded world region: heights (the region fine, the whole world coarse), and where the camera starts.
/// </summary>
sealed class WorldScene : IDisposable
{
    /// <summary>
    /// The world heightmap file; null for the flat floor of the game's animation sandbox (<see cref="FlatHeight"/>).
    /// </summary>
    public TerrainHeightmap? Heightmap;

    /// <summary>
    /// The floor's height when there is no <see cref="Heightmap"/>.
    /// </summary>
    public float FlatHeight;

    /// <summary>
    /// The region's heights in memory at the loaded step (<see cref="HeightWindow"/>).
    /// </summary>
    public required HeightWindow Window;

    /// <summary>
    /// Whole-world heights every <see cref="WorldFrame.CoarseStep"/>-th sample, (CoarseSize)² raw values.
    /// </summary>
    public required ushort[] Coarse;

    /// <summary>
    /// The side of <see cref="Coarse"/> in samples.
    /// </summary>
    public required int CoarseSize;

    /// <summary>
    /// The sky's clock: from the game data when there is any, else <see cref="SkyClock.Fallback"/>.
    /// </summary>
    public SkyClock Clock = SkyClock.Fallback;

    /// <summary>
    /// Where the camera starts: the world point the region is centred on.
    /// </summary>
    public required Vector3 Focus;

    /// <summary>
    /// The region's world X/Z bounds: its first window sample (X0, Z0) and its last (X1, Z1).
    /// </summary>
    public required double X0, Z0, X1, Z1;

    /// <summary>
    /// The game data the region was loaded with; null without one.
    /// </summary>
    public GameDatabase? Database;

    /// <summary>
    /// The region's placed objects; null when objects are off or there is no game data.
    /// </summary>
    public WorldObjects? Objects;

    /// <summary>
    /// The ground height at world (<paramref name="x"/>, <paramref name="z"/>): the heightmap's, else <see cref="FlatHeight"/>.
    /// </summary>
    public float GroundAt(float x, float z) => Heightmap?.HeightAt(x, z) ?? FlatHeight;

    /// <summary>
    /// Releases the heightmap file.
    /// </summary>
    public void Dispose() => Heightmap?.Dispose();
}
