using System.Collections.Concurrent;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Textures;
using Meitou.Data.World;

namespace Meitou.Audio;

/// <summary>
/// What a character walks on, for the footsteps' <c>Surface_Material</c> switch (docs/audio.md "Footsteps"): the GroundType the BIOMES record under the point names for the
/// overlay map's channel at the point (<b>Verified</b>, <c>FUN_140a16e50</c> / <c>FUN_140a16c90</c> / the biome loader <c>FUN_140a13750</c>). The overlay pixel (36 units)
/// picks the type: road (A above 160), else dirt (B), else grass (G, then R), else the biome's ground; the record keeps five (ground, slope, grass, dirt, road).
/// The overlay tiles (1024², 4 MB) load on a worker when first needed and the least recently used are dropped.
/// </summary>
public sealed class GroundSurfaces
{
    /// <summary>GroundType values, the index into the two footstep name tables (<see cref="CharacterSounds.SurfaceName"/>).</summary>
    public const int Sand = 0, Grass = 1, Concrete = 2, Wood = 3, Metal = 4, Water = 5, Mud = 6, Snow = 7, Dirt = 8;

    /// <summary>The overlay channel value above which a channel counts as present (<c>0xA0</c>, strictly).</summary>
    public const int Threshold = 0xA0;
    const int MaxTiles = 6;

    readonly GameInstall install;
    readonly Dictionary<uint, byte[]> biomes = [];
    readonly object gate = new();
    readonly Dictionary<(int X, int Z), Tile> tiles = [];
    Task<FoliageBiomeMap?>? biomeMap;
    long clock;

    sealed class Tile
    {
        public Task<byte[]?> Pixels = null!;
        public long Used;
    }

    public GroundSurfaces(GameInstall install, GameDatabase db)
    {
        this.install = install;
        foreach (var r in db.OfType(FcsRecordType.BIOMES).OrderBy(r => r.StringId, StringComparer.Ordinal))
            biomes.TryAdd((uint)r.GetInt("index") & 0xFFFFFF, [(byte)r.GetInt("ground type", Sand), (byte)r.GetInt("slope type", Dirt), (byte)r.GetInt("grass type", Dirt), (byte)r.GetInt("dirt type", Dirt), (byte)r.GetInt("road type", Concrete)]);
    }

    /// <summary>The GroundType at a world point, or -1 while the map it needs is still loading (the caller keeps what it had).</summary>
    public int At(float x, float z)
    {
        biomeMap ??= Task.Run(() => TryLoad(() => FoliageBiomeMap.Open(install)));
        int tx = (int)MathF.Floor((x + WorldLayout.HalfWorldSize) / (FoliageOverlay.TilePixels * FoliageOverlay.PixelSize));
        int tz = (int)MathF.Floor((z + WorldLayout.HalfWorldSize) / (FoliageOverlay.TilePixels * FoliageOverlay.PixelSize));
        tx = Math.Clamp(tx, 0, TerrainMaps.OverlayTiles - 1);
        tz = Math.Clamp(tz, 0, TerrainMaps.OverlayTiles - 1);
        Tile tile;
        lock (gate)
        {
            if (!tiles.TryGetValue((tx, tz), out tile!))
            {
                if (tiles.Count >= MaxTiles) tiles.Remove(tiles.MinBy(t => t.Value.Used).Key);
                tiles[(tx, tz)] = tile = new Tile { Pixels = Task.Run(() => TryLoad(() => ReadTile(tx, tz))) };
            }
            tile.Used = ++clock;
        }
        if (!tile.Pixels.IsCompleted || !biomeMap.IsCompleted) return -1;
        // A map that cannot be read leaves the original's answer for "no overlay": 2 (concrete) (FUN_140a16e50 returns 2 when FUN_140a16c90 finds none).
        if (tile.Pixels.Result is not { } pixels || biomeMap.Result is not { } map) return Concrete;
        float x0 = tx * FoliageOverlay.TilePixels * FoliageOverlay.PixelSize - WorldLayout.HalfWorldSize, z0 = tz * FoliageOverlay.TilePixels * FoliageOverlay.PixelSize - WorldLayout.HalfWorldSize;
        int i = Math.Clamp((int)((x - x0) / FoliageOverlay.PixelSize), 0, FoliageOverlay.TilePixels - 1), j = Math.Clamp((int)((z - z0) / FoliageOverlay.PixelSize), 0, FoliageOverlay.TilePixels - 1);
        int o = (j * FoliageOverlay.TilePixels + i) * 4;
        int channel = pixels[o + 3] > Threshold ? 4 : pixels[o + 2] > Threshold ? 3 : pixels[o + 1] > Threshold ? 2 : pixels[o] > Threshold ? 2 : 0;
        return biomes.TryGetValue(map.At(x, z), out var types) ? types[channel] : Sand;
    }

    byte[]? ReadTile(int tx, int tz)
    {
        var image = TextureLoader.LoadImage(File.ReadAllBytes(TerrainMaps.OverlayTile(install, "new_overlay", tx, tz)));
        return image.Width == FoliageOverlay.TilePixels && image.Height == FoliageOverlay.TilePixels ? image.Pixels : null;
    }

    static T? TryLoad<T>(Func<T?> load) where T : class
    {
        try { return load(); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }
}
