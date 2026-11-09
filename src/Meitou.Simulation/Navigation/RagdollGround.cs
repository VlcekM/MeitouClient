using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// The terrain ragdolls fall onto at full resolution, from blocks of the heightmap read as they are needed; the simulation's coarse ground height
/// where the heightmap cannot be opened or a block holds no sample.
/// </summary>
internal sealed class RagdollGround : IDisposable
{
    /// <summary>
    /// Heightmap samples along a block of the ground cache, and blocks kept at most (65 × 65 samples each, about 8 KB; a block is 1152 units across).
    /// </summary>
    const int BlockSamples = 64, MaxBlocks = 256;

    /// <summary>
    /// The simulation's ground height, used only where the full heightmap cannot be opened.
    /// </summary>
    readonly Func<float, float, float> _terrain;

    /// <summary>
    /// The full heightmap; null when it cannot be opened.
    /// </summary>
    readonly TerrainHeightmap? _map;

    /// <summary>
    /// The heightmap blocks read so far, by block column and row.
    /// </summary>
    readonly Dictionary<(int, int), HeightWindow> _blocks = [];

    /// <summary>
    /// Opens the heightmap of <paramref name="install"/>; <paramref name="terrainHeight"/> stands in where it cannot be opened.
    /// </summary>
    public RagdollGround(GameInstall install, Func<float, float, float> terrainHeight)
    {
        _terrain = terrainHeight;
        try
        {
            _map = TerrainHeightmap.Open(install);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // No heightmap: the coarse grid stands in.
        }
    }

    /// <summary>
    /// The terrain at full resolution, from blocks of the heightmap read as they are needed (the simulation's own height is the coarse grid, a sample
    /// in every 144 units, outside the area the game loaded at the start: a body would meet a ground metres off the one the character stands on and
    /// drawn terrain). Called from the simulation thread only.
    /// </summary>
    public float HeightAt(float x, float z)
    {
        if (_map is null) return _terrain(x, z);

        var (column, row) = WorldLayout.ToSample(x, z);
        int bx = (int)Math.Floor(column / BlockSamples);
        int bz = (int)Math.Floor(row / BlockSamples);
        if (!_blocks.TryGetValue((bx, bz), out var block))
        {
            if (_blocks.Count >= MaxBlocks) _blocks.Clear();

            block = _map.ReadWindow(bx * BlockSamples, bz * BlockSamples, BlockSamples + 1, BlockSamples + 1);
            _blocks[(bx, bz)] = block;
        }

        return block.TryHeightAt(x, z, out float height) ? height : _terrain(x, z);
    }

    /// <summary>
    /// Closes the heightmap.
    /// </summary>
    public void Dispose() => _map?.Dispose();
}
