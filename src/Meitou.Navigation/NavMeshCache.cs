using System.Numerics;
using System.Text;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;

namespace Meitou.Navigation;

/// <summary>
/// The on-disk cache of built zone meshes, in our own format, keyed by the zone's building hash and the build settings
/// (docs/simulation.md, stage 5). Lives outside the repository, by default under <c>%LOCALAPPDATA%\Meitou\navmesh</c>.
/// </summary>
public sealed class NavMeshCache
{
    /// <summary>Bump when the builder's output changes for the same input (a new rule in the gatherer or the pruner).</summary>
    public const int BuilderVersion = 39;
    const int FormatVersion = 5;
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
        foreach (var v in new[] { s.TileCells, s.MaxEdgeLength, s.MinRegionArea, s.MergeRegionArea, 0 })
            h = (h * 16777619) ^ (uint)v;
        return h;
    }

    /// <summary>
    /// Hash of the inputs every zone shares, by size and write time (not contents): the data files in load order (record contents: buildings, layouts,
    /// foliage), the seed, height, biome, map feature and overlay files the gather and the foliage placer read, and the DotRecast version. Collision files are not in it.
    /// </summary>
    public static uint ContentHash(GameInstall install, GameDatabase db)
    {
        uint h = 2166136261;
        void Add(string text) { foreach (char c in text) h = (h ^ c) * 16777619; h = (h ^ 0xff) * 16777619; }
        void Stamp(string path)
        {
            var file = new FileInfo(GameFiles.Resolve(path));
            Add(file.Name);
            Add(file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "-");
        }
        var mods = ModList.FindMods(install);
        foreach (var name in db.Files)
            Stamp(LoadOrder.BaseFiles.Contains(name) ? Path.Combine(install.DataDirectory, name)
                : mods.TryGetValue(Path.GetFileNameWithoutExtension(name), out var mod) ? mod : name);
        foreach (var rel in new[] { NavSeeds.RelativePath, TerrainHeightmap.RelativePath, BlendInfoFile.RelativePath, TerrainMaps.BiomeMap, MapFeatureFile.RelativePath })
            Stamp(Path.Combine(install.DataDirectory, rel));
        if (Path.GetDirectoryName(TerrainMaps.OverlayTile(install, "new_overlay", 0, 0)) is { } overlays && System.IO.Directory.Exists(overlays))
            foreach (var tile in System.IO.Directory.GetFiles(overlays, "new_overlay.*").Order(StringComparer.OrdinalIgnoreCase)) Stamp(tile);
        Add(typeof(DotRecast.Recast.RcBuilder).Assembly.GetName().Version?.ToString() ?? "");
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
            var corners = new Vector3[r.ReadInt32() * 3];
            for (int i = 0; i < corners.Length; i++) corners[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            HeightGrid?[]? heights = null;
            if (r.ReadBoolean())
            {
                heights = new HeightGrid?[count];
                for (int p = 0; p < count; p++)
                {
                    if (!r.ReadBoolean()) continue;
                    float x0 = r.ReadSingle(), z0 = r.ReadSingle(), step = r.ReadSingle();
                    int columns = r.ReadUInt16(), rows = r.ReadUInt16();
                    var values = new float[columns * rows];
                    for (int i = 0; i < values.Length; i++) values[i] = r.ReadSingle();
                    heights[p] = columns == 0 ? HeightGrid.OnTerrain : new HeightGrid(x0, z0, step, columns, rows, values);
                }
            }
            if (r.ReadInt32() != count) return null; // trailer: truncated writes fail here
            return new ZoneNavMesh { ZoneX = zoneX, ZoneZ = zoneZ, Vertices = vertices, Polygons = polygons, Neighbours = neighbours, Areas = areas, Links = links, Kept = kept, BoundsMin = boundsMin, BoundsMax = boundsMax, DoorIds = doorIds.Length > 0 ? doorIds : null, DoorOf = doorOf, Surfaces = corners.Length > 0 ? new NavSurfaces(corners) : null, Heights = heights };
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
            var corners = m.Surfaces?.Corners ?? [];
            w.Write(corners.Length / 3);
            foreach (var c in corners) { w.Write(c.X); w.Write(c.Y); w.Write(c.Z); }
            w.Write(m.Heights is not null);
            if (m.Heights is { } heights)
                foreach (var grid in heights)
                {
                    w.Write(grid is not null);
                    if (grid is null) continue;
                    w.Write(grid.X0); w.Write(grid.Z0); w.Write(grid.Step);
                    w.Write((ushort)grid.Columns); w.Write((ushort)grid.Rows);
                    foreach (float v in grid.Values) w.Write(v);
                }
            w.Write(m.PolygonCount);
        }
        File.Move(temp, path, overwrite: true);
    }
}
