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
    public float FlatHeight;
    public required HeightWindow Window;

    /// <summary>
    /// Whole-world heights every <see cref="WorldFrame.CoarseStep"/>-th sample, (CoarseSize)² raw values.
    /// </summary>
    public required ushort[] Coarse;
    public required int CoarseSize;
    public SkyClock Clock = SkyClock.Fallback;
    public required Vector3 Focus;
    public required double X0, Z0, X1, Z1;
    public GameDatabase? Database;
    public WorldObjects? Objects;

    public float GroundAt(float x, float z) => Heightmap?.HeightAt(x, z) ?? FlatHeight;

    public void Dispose() => Heightmap?.Dispose();
}
