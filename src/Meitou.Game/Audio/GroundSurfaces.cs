using System.Collections.Concurrent;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Textures;
using Meitou.Data.World;

namespace Meitou.Game.Audio;

/// <summary>
/// What a character walks on, for the footsteps' <c>Surface_Material</c> switch (docs/audio.md "Footsteps"): the GroundType the BIOMES record under
/// the point names for the overlay map's channel at the point (<b>Verified</b>, <c>FUN_140a16e50</c> / <c>FUN_140a16c90</c> / the biome loader
/// <c>FUN_140a13750</c>). The overlay pixel (36 units) picks the type: road (A above 160), else dirt (B), else grass (G, then R), else the biome's
/// ground; the record keeps five (ground, slope, grass, dirt, road). The overlay tiles (1024², 4 MB) load on a worker when first needed and the least
/// recently used are dropped.
/// </summary>
public sealed class GroundSurfaces
{
    /// <summary>
    /// GroundType values, the index into the two footstep name tables (<see cref="CharacterSounds.SurfaceName"/>).
    /// </summary>
    public const int Sand = 0, Grass = 1, Concrete = 2, Wood = 3, Metal = 4, Water = 5, Mud = 6, Snow = 7, Dirt = 8;

    /// <summary>
    /// The overlay channel value above which a channel counts as present (<c>0xA0</c>, strictly).
    /// </summary>
    public const int Threshold = 0xA0;
    /// <summary>
    /// The most overlay tiles kept; the least recently used goes when a new one is needed.
    /// </summary>
    const int MaxTiles = 6;

    /// <summary>
    /// The install the overlay tiles and the biome map are read from.
    /// </summary>
    readonly GameInstall _install;
    /// <summary>
    /// Each BIOMES record's five GroundTypes (ground, slope, grass, dirt, road) by its 24-bit <c>index</c>.
    /// </summary>
    readonly Dictionary<uint, byte[]> _biomes = [];
    /// <summary>
    /// Each BIOMES record's five diffuse texture names (base, slope, grass, dirt, road; null: none) by its 24-bit <c>index</c>.
    /// </summary>
    readonly Dictionary<uint, string?[]> _textures = [];
    /// <summary>
    /// Guards <see cref="_tiles"/> and <see cref="_clock"/> (<see cref="At"/> may run on more than one thread).
    /// </summary>
    readonly object _gate = new();
    /// <summary>
    /// The overlay tiles loaded or loading, by tile column and row.
    /// </summary>
    readonly Dictionary<(int X, int Z), Tile> _tiles = [];
    /// <summary>
    /// The biome map, loading on a worker from the first lookup; its result is null when it cannot be read.
    /// </summary>
    Task<FoliageBiomeMap?>? _biomeMap;
    /// <summary>
    /// The use counter that stamps each tile's last use.
    /// </summary>
    long _clock;

    /// <summary>
    /// One overlay tile: its pixels (loading on a worker) and its last use.
    /// </summary>
    sealed class Tile
    {
        /// <summary>
        /// The tile's RGBA pixels; the result is null when it cannot be read or has the wrong size.
        /// </summary>
        public Task<byte[]?> Pixels = null!;
        /// <summary>
        /// The <see cref="_clock"/> value of the tile's last lookup.
        /// </summary>
        public long Used;
    }

    /// <summary>
    /// Reads every BIOMES record's ground types from the database; a missing field falls back to sand (ground), concrete (road) or dirt (the rest).
    /// </summary>
    public GroundSurfaces(GameInstall install, GameDatabase db)
    {
        this._install = install;
        foreach (var r in db.OfType(FcsRecordType.Biomes).OrderBy(r => r.StringId, StringComparer.Ordinal))
        {
            byte[] types =
            [
                (byte)r.GetInt("ground type", Sand),
                (byte)r.GetInt("slope type", Dirt),
                (byte)r.GetInt("grass type", Dirt),
                (byte)r.GetInt("dirt type", Dirt),
                (byte)r.GetInt("road type", Concrete),
            ];
            _biomes.TryAdd((uint)r.GetInt("index") & 0xFFFFFF, types);

            string? Texture(string field) => r.Filenames.TryGetValue(field, out var file) && file.Length > 0 ? file : null;
            _textures.TryAdd(
                (uint)r.GetInt("index") & 0xFFFFFF,
                [Texture("texture base"), Texture("texture slope"), Texture("texture grass"), Texture("texture dirt"), Texture("texture road")]);
        }
    }

    /// <summary>
    /// The GroundType at a world point, or -1 while the map it needs is still loading (the caller keeps what it had).
    /// </summary>
    public int At(float x, float z) => LayerAt(x, z).Type;

    /// <summary>
    /// The GroundType at a world point with the diffuse texture of the biome layer that gives it (the record's <c>texture base</c>, <c>grass</c>,
    /// <c>dirt</c> or <c>road</c>; null when the record names none or the map cannot be read); the type is -1 while the map loads.
    /// </summary>
    public (int Type, string? Texture) LayerAt(float x, float z)
    {
        _biomeMap ??= Task.Run(() => TryLoad(() => FoliageBiomeMap.Open(_install)));
        int tx = (int)MathF.Floor((x + WorldLayout.HalfWorldSize) / (FoliageOverlay.TilePixels * FoliageOverlay.PixelSize));
        int tz = (int)MathF.Floor((z + WorldLayout.HalfWorldSize) / (FoliageOverlay.TilePixels * FoliageOverlay.PixelSize));
        tx = Math.Clamp(tx, 0, TerrainMaps.OverlayTiles - 1);
        tz = Math.Clamp(tz, 0, TerrainMaps.OverlayTiles - 1);
        Tile tile;
        lock (_gate)
        {
            if (!_tiles.TryGetValue((tx, tz), out tile!))
            {
                if (_tiles.Count >= MaxTiles) _tiles.Remove(_tiles.MinBy(t => t.Value.Used).Key);
                _tiles[(tx, tz)] = tile = new Tile { Pixels = Task.Run(() => TryLoad(() => ReadTile(tx, tz))) };
            }
            tile.Used = ++_clock;
        }
        if (!tile.Pixels.IsCompleted || !_biomeMap.IsCompleted) return (-1, null);
        // A map that cannot be read leaves the original's answer for "no overlay": 2 (concrete)
        // (FUN_140a16e50 returns 2 when FUN_140a16c90 finds none).
        if (tile.Pixels.Result is not { } pixels || _biomeMap.Result is not { } map) return (Concrete, null);

        float x0 = tx * FoliageOverlay.TilePixels * FoliageOverlay.PixelSize - WorldLayout.HalfWorldSize;
        float z0 = tz * FoliageOverlay.TilePixels * FoliageOverlay.PixelSize - WorldLayout.HalfWorldSize;
        int i = Math.Clamp((int)((x - x0) / FoliageOverlay.PixelSize), 0, FoliageOverlay.TilePixels - 1);
        int j = Math.Clamp((int)((z - z0) / FoliageOverlay.PixelSize), 0, FoliageOverlay.TilePixels - 1);
        int o = (j * FoliageOverlay.TilePixels + i) * 4;
        int channel = pixels[o + 3] > Threshold ? 4 : pixels[o + 2] > Threshold ? 3 : pixels[o + 1] > Threshold ? 2 : pixels[o] > Threshold ? 2 : 0;
        uint biome = map.At(x, z);
        return _biomes.TryGetValue(biome, out var types) ? (types[channel], _textures[biome][channel]) : (Sand, null);
    }

    /// <summary>
    /// The biome index colour at a world point (<c>biomemap.png</c>, the lookup the footsteps and the water effects share, <c>FUN_140a09630</c>);
    /// null while the map loads or when it cannot be read.
    /// </summary>
    public uint? BiomeAt(float x, float z)
    {
        _biomeMap ??= Task.Run(() => TryLoad(() => FoliageBiomeMap.Open(_install)));
        return _biomeMap.IsCompletedSuccessfully && _biomeMap.Result is { } map ? map.At(x, z) : null;
    }

    /// <summary>
    /// Reads overlay tile (<paramref name="tx"/>, <paramref name="tz"/>) of <c>new_overlay</c>; null when it is not 1024 pixels square.
    /// </summary>
    byte[]? ReadTile(int tx, int tz)
    {
        var image = TextureLoader.LoadImage(File.ReadAllBytes(TerrainMaps.OverlayTile(_install, "new_overlay", tx, tz)));
        return image.Width == FoliageOverlay.TilePixels && image.Height == FoliageOverlay.TilePixels ? image.Pixels : null;
    }

    /// <summary>
    /// Runs <paramref name="load"/>; null when it fails to read the file.
    /// </summary>
    static T? TryLoad<T>(Func<T?> load) where T : class
    {
        try
        {
            return load();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}