using System.Numerics;
using System.Text;

namespace Meitou.Navigation;

/// <summary>
/// The on-disk cache of built zone meshes, in our own format, keyed by the zone's building hash and the build settings
/// (docs/simulation.md, stage 5). Lives outside the repository, by default under <c>%LOCALAPPDATA%\Meitou\navmesh</c>.
/// </summary>
public sealed class NavMeshCache
{
    /// <summary>Bump when the builder's output changes for the same input (a new rule in the gatherer or the pruner).</summary>
    public const int BuilderVersion = 3;
    const int FormatVersion = 3;
    static readonly byte[] Magic = "MNAV"u8.ToArray();

    public string Directory { get; }

    public NavMeshCache(string? directory = null)
    {
        Directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meitou", "navmesh");
    }

    public string PathOf(int zoneX, int zoneZ) => Path.Combine(Directory, $"zone.{zoneX}.{zoneZ}.nav");

    /// <summary>Hash of the settings that change the mesh (not the thread count).</summary>
    public static uint SettingsHash(NavBuildSettings s)
    {
        uint h = BuilderVersion;
        foreach (var v in new[] { s.CellSize, s.CellHeight, s.DoorInflateCells, s.AgentHeight, s.MaxClimb, s.MaxSimplificationError, s.SeedDistance, s.SeedHeightSlack })
            h = (h * 16777619) ^ (uint)BitConverter.SingleToInt32Bits(v);
        foreach (var v in new[] { s.TileCells, s.MaxEdgeLength, s.MinRegionArea, s.MergeRegionArea, s.Watershed ? 1 : 0 })
            h = (h * 16777619) ^ (uint)v;
        return h;
    }

    /// <summary>The cached mesh when the file exists and matches the hashes; null otherwise (missing, stale or damaged).</summary>
    public ZoneNavMesh? TryLoad(int zoneX, int zoneZ, uint buildingHash, uint settingsHash)
    {
        var path = PathOf(zoneX, zoneZ);
        if (!File.Exists(path)) return null;
        try
        {
            using var r = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
            if (!r.ReadBytes(4).AsSpan().SequenceEqual(Magic) || r.ReadInt32() != FormatVersion) return null;
            if (r.ReadInt32() != zoneX || r.ReadInt32() != zoneZ || r.ReadUInt32() != buildingHash || r.ReadUInt32() != settingsHash) return null;
            var boundsMin = new Vector2(r.ReadSingle(), r.ReadSingle());
            var boundsMax = new Vector2(r.ReadSingle(), r.ReadSingle());
            var vertices = new Vector3[r.ReadInt32()];
            for (int i = 0; i < vertices.Length; i++) vertices[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var doorIds = new string[r.ReadInt32()];
            for (int i = 0; i < doorIds.Length; i++) doorIds[i] = r.ReadString();
            int count = r.ReadInt32();
            var doorOf = doorIds.Length > 0 ? new int[count] : null;
            var polygons = new int[count][];
            var neighbours = new int[count][];
            var areas = new byte[count];
            var kept = new bool[count];
            var links = new int[]?[count];
            for (int p = 0; p < count; p++)
            {
                int n = r.ReadByte();
                areas[p] = r.ReadByte();
                kept[p] = r.ReadBoolean();
                polygons[p] = new int[n];
                neighbours[p] = new int[n];
                for (int k = 0; k < n; k++) polygons[p][k] = r.ReadInt32();
                for (int k = 0; k < n; k++) neighbours[p][k] = r.ReadInt32();
                int linkCount = r.ReadUInt16();
                if (linkCount > 0)
                {
                    links[p] = new int[linkCount * 2];
                    for (int i = 0; i < links[p]!.Length; i++) links[p]![i] = r.ReadInt32();
                }
                if (doorOf is not null) doorOf[p] = r.ReadInt16();
            }
            if (r.ReadInt32() != count) return null; // trailer: truncated writes fail here
            return new ZoneNavMesh { ZoneX = zoneX, ZoneZ = zoneZ, Vertices = vertices, Polygons = polygons, Neighbours = neighbours, Areas = areas, Links = links, Kept = kept, BoundsMin = boundsMin, BoundsMax = boundsMax, DoorIds = doorIds.Length > 0 ? doorIds : null, DoorOf = doorOf };
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or InvalidDataException or OverflowException or OutOfMemoryException)
        {
            return null;
        }
    }

    public void Save(ZoneNavMesh m, uint buildingHash, uint settingsHash)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathOf(m.ZoneX, m.ZoneZ);
        var temp = path + "." + Environment.ProcessId + ".tmp";
        using (var w = new BinaryWriter(File.Create(temp), Encoding.UTF8))
        {
            w.Write(Magic);
            w.Write(FormatVersion);
            w.Write(m.ZoneX); w.Write(m.ZoneZ); w.Write(buildingHash); w.Write(settingsHash);
            w.Write(m.BoundsMin.X); w.Write(m.BoundsMin.Y); w.Write(m.BoundsMax.X); w.Write(m.BoundsMax.Y);
            w.Write(m.Vertices.Length);
            foreach (var v in m.Vertices) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
            var ids = m.DoorIds ?? [];
            w.Write(ids.Length);
            foreach (var id in ids) w.Write(id);
            w.Write(m.PolygonCount);
            for (int p = 0; p < m.PolygonCount; p++)
            {
                w.Write((byte)m.Polygons[p].Length);
                w.Write(m.Areas[p]);
                w.Write(m.Kept[p]);
                foreach (int i in m.Polygons[p]) w.Write(i);
                foreach (int n in m.Neighbours[p]) w.Write(n);
                var l = m.LinksOf(p);
                w.Write((ushort)(l.Length / 2));
                foreach (int i in l) w.Write(i);
                if (ids.Length > 0) w.Write((short)(m.DoorOf?[p] ?? -1));
            }
            w.Write(m.PolygonCount);
        }
        File.Move(temp, path, overwrite: true);
    }
}
