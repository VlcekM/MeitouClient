namespace Meitou.Game.Rendering.Impostors;

/// <summary>
/// Where an <see cref="ImpostorTextures"/>' step-by-step upload stands: the map (0 albedo, 1 normal), its level on the GPU and the next row (of
/// texels, or of 4 x 4 blocks for the compressed maps) to write. The maps are written in order, each level top to bottom, each map's levels from
/// the finest kept. Render thread only.
/// </summary>
/// <param name="levels">The atlas' levels.</param>
/// <param name="skips">The top levels left out per map (the owner's array, filled before the first step).</param>
sealed class ImpostorUploadCursor(int levels, int[] skips)
{
    /// <summary>
    /// The map being written (2 once both are).
    /// </summary>
    int _map;

    /// <summary>
    /// The level of <see cref="Map"/> being written, counted on the GPU (0 is the finest kept).
    /// </summary>
    int _level;

    /// <summary>
    /// The next row of <see cref="Level"/> to write.
    /// </summary>
    int _row;

    /// <summary>
    /// The map being written (0 albedo, 1 normal).
    /// </summary>
    public int Map => _map;

    /// <summary>
    /// The level of <see cref="Map"/> being written, on the GPU.
    /// </summary>
    public int Level => _level;

    /// <summary>
    /// The next row of <see cref="Level"/> to write.
    /// </summary>
    public int Row => _row;

    /// <summary>
    /// The atlas level <see cref="Level"/> is (the skipped top levels added).
    /// </summary>
    public int SourceLevel => skips[_map] + _level;

    /// <summary>
    /// Every level of both maps written.
    /// </summary>
    public bool Complete => _map >= 2;

    /// <summary>
    /// <paramref name="n"/> rows written of a level of <paramref name="rows"/>: at its end, on to the next level, after the map's last to the next
    /// map.
    /// </summary>
    public void Advance(int n, int rows)
    {
        _row += n;
        if (_row >= rows)
        {
            _row = 0;
            if (++_level == levels - skips[_map]) (_map, _level) = (_map + 1, 0);
        }
    }
}
