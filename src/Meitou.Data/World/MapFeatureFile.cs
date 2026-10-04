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
    public const string RelativePath = "newland/land/features.dat";
    static readonly byte[] Magic = "MF01"u8.ToArray();

    /// <summary>Placements per zone; index <c>y * 64 + x</c> (<see cref="ZoneOf"/>). Zones without features are empty.</summary>
    public List<List<MapFeature>> Zones { get; } = [];

    public static ZoneCoordinate ZoneOf(int index) => new(index % WorldLayout.ZoneCount, index / WorldLayout.ZoneCount);

    public IEnumerable<(ZoneCoordinate Zone, MapFeature Feature)> All() =>
        Zones.SelectMany((list, i) => list.Select(f => (ZoneOf(i), f)));

    public static MapFeatureFile Open(GameInstall install) => Read(File.ReadAllBytes(Path.Combine(install.DataDirectory, RelativePath)));

    public static MapFeatureFile Read(byte[] data)
    {
        if (data.Length < 8 || !data.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidDataException("Not a map feature file (magic MF01 missing).");
        int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        long tableEnd = 8 + 8L * count;
        if (count < 0 || tableEnd > data.Length)
            throw new InvalidDataException($"Implausible zone count {count}.");
        var file = new MapFeatureFile();
        long expected = tableEnd;
        for (int i = 0; i < count; i++)
        {
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8 + 8 * i));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12 + 8 * i));
            var list = new List<MapFeature>();
            file.Zones.Add(list);
            if (size == 0) continue;
            if (offset != expected || offset + (long)size > data.Length)
                throw new InvalidDataException($"Zone {i}: block at {offset} (+{size}) is not where the previous one ended ({expected}).");
            ReadBlock(data.AsSpan((int)offset, (int)size), list, i);
            expected = offset + size;
        }
        if (expected != data.Length)
            throw new InvalidDataException($"{data.Length - expected} bytes after the last block.");
        return file;
    }

    static void ReadBlock(ReadOnlySpan<byte> block, List<MapFeature> list, int zone)
    {
        int n = BinaryPrimitives.ReadInt32LittleEndian(block), p = 4;
        static float F(ReadOnlySpan<byte> b, ref int at) { float v = BinaryPrimitives.ReadSingleLittleEndian(b[at..]); at += 4; return v; }
        for (int k = 0; k < n; k++)
        {
            int len = block[p++];
            var id = Encoding.UTF8.GetString(block.Slice(p, len));
            p += len;
            var position = new Vector3(F(block, ref p), F(block, ref p), F(block, ref p));
            var scale = new Vector3(F(block, ref p), F(block, ref p), F(block, ref p));
            float w = F(block, ref p), x = F(block, ref p), y = F(block, ref p), z = F(block, ref p);
            list.Add(new MapFeature(id, position, scale, new Quaternion(x, y, z, w)));
        }
        if (p != block.Length)
            throw new InvalidDataException($"Zone {zone}: {block.Length - p} bytes left in its block.");
    }
}

/// <param name="StringId">The placed FCS record (MAP_FEATURES).</param>
/// <param name="Position">World position; Y is absolute.</param>
/// <param name="Rotation">Stored w, x, y, z (assumed, like FCS instances; the w-first order is Observed from unit-length values).</param>
public sealed record MapFeature(string StringId, Vector3 Position, Vector3 Scale, Quaternion Rotation);
