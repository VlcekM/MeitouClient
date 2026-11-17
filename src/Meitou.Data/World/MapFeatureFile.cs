using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Meitou.Content;

namespace Meitou.Data.World;

/// <summary>
/// <c>data/newland/land/features.dat</c> (magic <c>MF01</c>): the map features (rocks, cliffs and other large
/// meshes, FCS MAP_FEATURES records) placed in each zone. Layout in docs/formats/zones.md.
/// </summary>
public sealed class MapFeatureFile
{
    /// <summary>
    /// The file's path under the data folder.
    /// </summary>
    public const string RelativePath = "newland/land/features.dat";
    /// <summary>
    /// The file's first four bytes, <c>MF01</c>.
    /// </summary>
    static readonly byte[] Magic = "MF01"u8.ToArray();

    /// <summary>
    /// Placements per zone; index <c>y * 64 + x</c> (<see cref="ZoneOf"/>). Zones without features are empty.
    /// </summary>
    public List<List<MapFeature>> Zones { get; } = [];

    /// <summary>
    /// The zone of index <paramref name="index"/> in <see cref="Zones"/>.
    /// </summary>
    public static ZoneCoordinate ZoneOf(int index) => new(index % WorldLayout.ZoneCount, index / WorldLayout.ZoneCount);

    /// <summary>
    /// Every placement with its zone, zone by zone.
    /// </summary>
    public IEnumerable<(ZoneCoordinate Zone, MapFeature Feature)> All() =>
        Zones.SelectMany((list, i) => list.Select(f => (ZoneOf(i), f)));

    /// <summary>
    /// The file the game reads: it opens <c>features.dat</c> by bare name from the resource group <c>Landscape</c> (docs/formats/overview.md), so
    /// an enabled mod's copy in a folder of that group replaces the install's <see cref="RelativePath"/>.
    /// </summary>
    public static string PathOf(GameInstall install) => Ogre.GameResources.Resolve(install, "features.dat", "Landscape", RelativePath);

    /// <summary>
    /// Reads the file the game reads (<see cref="PathOf"/>).
    /// </summary>
    public static MapFeatureFile Open(GameInstall install) => Read(File.ReadAllBytes(PathOf(install)));

    /// <summary>
    /// Parses a map feature file as the original reads it (<c>FUN_14039a4d0</c>, <c>FUN_14039a820</c>): the magic, the zone count, a table of
    /// (offset, size) per zone, and each zone's block read at its offset alone (0: no features). The sizes, the blocks' order and any bytes after a
    /// block are never looked at, so a mod's file may hold its blocks anywhere. A file with another magic or zone count has no features.
    /// </summary>
    public static MapFeatureFile Read(byte[] data)
    {
        var file = new MapFeatureFile();
        if (data.Length < 8 || !data.AsSpan(0, 4).SequenceEqual(Magic)) return file;
        int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        if (count != WorldLayout.ZoneCount * WorldLayout.ZoneCount || 8 + 8L * count > data.Length) return file;

        for (int i = 0; i < count; i++)
        {
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8 + 8 * i));
            var list = new List<MapFeature>();
            file.Zones.Add(list);
            if (offset != 0 && offset < data.Length) ReadBlock(data.AsSpan((int)offset), list);
        }

        return file;
    }

    /// <summary>
    /// Reads one zone's block into <paramref name="list"/>: a count, then per placement its id (a length byte and UTF-8), position, scale and
    /// rotation (w, x, y, z). A block cut short by the end of the file ends there (Meitou's choice for a broken file).
    /// </summary>
    static void ReadBlock(ReadOnlySpan<byte> block, List<MapFeature> list)
    {
        const int floats = 40;
        if (block.Length < 4) return;
        int n = BinaryPrimitives.ReadInt32LittleEndian(block), p = 4;
        static float F(ReadOnlySpan<byte> b, ref int at) { float v = BinaryPrimitives.ReadSingleLittleEndian(b[at..]); at += 4; return v; }
        for (int k = 0; k < n && p < block.Length; k++)
        {
            int len = block[p++];
            if (p + len + floats > block.Length) return;

            var id = Encoding.UTF8.GetString(block.Slice(p, len));
            p += len;
            var position = new Vector3(F(block, ref p), F(block, ref p), F(block, ref p));
            var scale = new Vector3(F(block, ref p), F(block, ref p), F(block, ref p));
            float w = F(block, ref p), x = F(block, ref p), y = F(block, ref p), z = F(block, ref p);
            list.Add(new MapFeature(id, position, scale, new Quaternion(x, y, z, w)));
        }
    }
}

/// <param name="StringId">The placed FCS record (MAP_FEATURES).</param>
/// <param name="Position">World position; Y is absolute.</param>
/// <param name="Rotation">Stored w, x, y, z (assumed, like FCS instances; the w-first order is Observed from unit-length values).</param>
public sealed record MapFeature(string StringId, Vector3 Position, Vector3 Scale, Quaternion Rotation);