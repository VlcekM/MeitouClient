using System.Globalization;
using System.Numerics;
using Meitou.Content;
using Meitou.Data.Havok;
using Meitou.Data.World;

namespace Meitou.Navigation;

/// <summary>How an edge of the original's navmesh ends: shared with a face of its tile, joined to a neighbouring tile, or the mesh's end.</summary>
public enum OriginalEdge : byte { Inner, TileBorder, End }

/// <summary>A zone's exterior navmesh as the original game built it, in world coordinates: polygons (vertex indices) and, per polygon edge, how it ends.</summary>
public sealed record OriginalNavMesh(ZoneCoordinate Zone, Vector3[] Vertices, int[][] Polygons, OriginalEdge[][] Edges);

/// <summary>
/// The original game's cached navmesh tiles (<c>data/newland/land/navtiles/tileX.Y.hkt</c>, Havok tagfiles; docs/game/pathfinding.md "Original tiles"): its
/// <c>hkaiNavMesh</c> faces, edges and vertices. The exterior mesh (the tile's first) has its vertices zone-local at 0.1 scale; the interior groups that follow it
/// (<see cref="LoadInteriors"/>) have theirs relative to their <c>Info</c> point (docs/formats/havok-tagfile.md).
/// </summary>
public static class OriginalNavTiles
{
    /// <summary>Havok units per world unit.</summary>
    const float Scale = 10;

    public static string PathOf(GameInstall install, ZoneCoordinate zone) =>
        Path.Combine(install.DataDirectory, TerrainMaps.LandDirectory, "navtiles", $"tile{zone.X}.{zone.Y}.hkt");

    /// <summary>The zone's exterior mesh (null when the install has no tile for it).</summary>
    public static OriginalNavMesh? LoadExterior(GameInstall install, ZoneCoordinate zone)
    {
        string path = GameFiles.Resolve(PathOf(install, zone));
        return File.Exists(path) ? Exterior(HavokTagfile.ReadFile(path), zone) : null;
    }

    public static OriginalNavMesh? Exterior(HavokTagfile file, ZoneCoordinate zone)
    {
        if (file.Variant("hkaiNavMesh") is not { } mesh || mesh["faces"] is not HkObject[] faces || mesh["edges"] is not HkObject[] edges || mesh["vertices"] is not object[] raw)
            return null;
        var (ox, oz) = WorldLayout.ZoneOrigin(zone);
        var vertices = new Vector3[raw.Length];
        for (int i = 0; i < raw.Length; i++)
            if (raw[i] is float[] v) vertices[i] = new Vector3((float)ox + v[0] * Scale, v[1] * Scale, (float)oz + v[2] * Scale);
        // Edges joined to the neighbouring tiles: the streaming sets' connections (face, edge).
        var joined = new HashSet<(long, long)>();
        if (mesh["streamingSets"] is HkObject[] sets)
            foreach (var set in sets)
                if (set["meshConnections"] is HkObject[] connections)
                    foreach (var c in connections) joined.Add((Int(c["faceIndex"]), Int(c["edgeIndex"])));
        var polygons = Polygons(faces, edges);
        var kinds = new OriginalEdge[faces.Length][];
        for (int f = 0; f < faces.Length; f++)
        {
            long start = Int(faces[f]["startEdgeIndex"]);
            var kind = new OriginalEdge[polygons[f].Length];
            for (int k = 0; k < kind.Length; k++)
                kind[k] = Int(edges[start + k]["oppositeFace"]) >= 0 ? OriginalEdge.Inner : joined.Contains((f, start + k)) ? OriginalEdge.TileBorder : OriginalEdge.End;
            kinds[f] = kind;
        }
        return new OriginalNavMesh(zone, vertices, polygons, kinds);
    }

    /// <summary>Each face's vertex indices: the <c>a</c> end of each of its edges, in order.</summary>
    static int[][] Polygons(HkObject[] faces, HkObject[] edges)
    {
        var polygons = new int[faces.Length][];
        for (int f = 0; f < faces.Length; f++)
        {
            long start = Int(faces[f]["startEdgeIndex"]), count = Int(faces[f]["numEdges"]);
            var poly = new int[count];
            for (int k = 0; k < count; k++) poly[k] = (int)Int(edges[start + k]["a"]);
            polygons[f] = poly;
        }
        return polygons;
    }

    /// <summary>
    /// The tile's interior groups (docs/formats/havok-tagfile.md: after the exterior, per interior a NavMesh and an Info <c>uidhex;x;y;z</c>; vertices are
    /// 0.1-scale offsets from the Info point), in world coordinates. Empty when the install has no tile.
    /// </summary>
    public static List<OriginalInterior> LoadInteriors(GameInstall install, ZoneCoordinate zone)
    {
        var result = new List<OriginalInterior>();
        string path = GameFiles.Resolve(PathOf(install, zone));
        if (!File.Exists(path)) return result;
        var file = HavokTagfile.ReadFile(path);
        if (file.Root?["namedVariants"] is not object[] variants) return result;
        HkObject? mesh = null;
        foreach (var v in variants.OfType<HkObject>())
        {
            string name = v["name"] as string ?? "";
            if (name == "NavMesh") { mesh = v["variant"] as HkObject; continue; }
            if (name != "Info" || v["variant"] is not HkObject info || info["string"] is not string text || mesh is null) continue;
            var parts = text.Split(';');
            if (parts.Length < 4) continue;
            var origin = new Vector3(Float(parts[1]), Float(parts[2]), Float(parts[3]));
            if (mesh["faces"] is not HkObject[] faces || mesh["edges"] is not HkObject[] edges || mesh["vertices"] is not object[] raw) continue;
            var vertices = new Vector3[raw.Length];
            for (int i = 0; i < raw.Length; i++)
                if (raw[i] is float[] f) vertices[i] = origin + Scale * new Vector3(f[0], f[1], f[2]);
            // Its door connections to the exterior: a streaming set's mesh connections (docs/formats/havok-tagfile.md, "Interior joins").
            int connections = (mesh["streamingSets"] as HkObject[] ?? []).Sum(set => (set["meshConnections"] as HkObject[])?.Length ?? 0);
            result.Add(new OriginalInterior(parts[0], origin, vertices, Polygons(faces, edges), connections));
            mesh = null;
        }
        return result;
    }

    /// <summary>How far (units) above or below an original interior face's centre one of our kept polygons may lie and still cover it.</summary>
    public const float InteriorCoverHeight = 12;

    /// <summary>
    /// How far (units, X/Z) from an original interior face's centre a kept polygon at its height may end and still cover it: the original's small faces
    /// along a mesh end have their centres a unit or two past ours, which is not a lost floor.
    /// </summary>
    public const float InteriorCoverReach = 3;

    /// <summary>
    /// How much of each original interior group a built zone covers: of the group's faces whose centre lies in the zone's box, those with a kept polygon of
    /// <paramref name="mesh"/> within <see cref="InteriorCoverHeight"/> of the centre's height over the centre or within <see cref="InteriorCoverReach"/> of
    /// it. A stair or storey we lose shows as uncovered faces
    /// (<c>meitou-tools navmesh --compare-interiors</c>, <c>InteriorCoverageTests</c>).
    /// </summary>
    public static List<InteriorCoverage> CoverInteriors(IReadOnlyList<OriginalInterior> groups, ZoneNavMesh mesh, Vector2 zoneMin, Vector2 zoneMax)
    {
        var result = new List<InteriorCoverage>(groups.Count);
        var heights = new List<float>();
        foreach (var group in groups)
        {
            int n = 0, hit = 0;
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var poly in group.Polygons)
            {
                if (poly.Length < 3) continue;
                var c = Vector3.Zero;
                foreach (int i in poly) c += group.Vertices[i];
                c /= poly.Length;
                if (c.X < zoneMin.X || c.X >= zoneMax.X || c.Z < zoneMin.Y || c.Z >= zoneMax.Y) continue;
                n++;
                if (Covers(mesh, c, heights)) hit++;
                else { lo = MathF.Min(lo, c.Y); hi = MathF.Max(hi, c.Y); }
            }
            result.Add(new InteriorCoverage(group, n, hit, lo, hi));
        }
        return result;
    }

    /// <summary>Whether a kept polygon of <paramref name="mesh"/> covers an original face centre (<see cref="CoverInteriors"/>); <paramref name="heights"/> is scratch.</summary>
    public static bool Covers(ZoneNavMesh mesh, Vector3 c, List<float> heights)
    {
        heights.Clear();
        mesh.HeightsAt(c.X, c.Z, heights);
        return heights.Any(y => MathF.Abs(y - c.Y) <= InteriorCoverHeight)
            || mesh.Nearest(c.X, c.Y, c.Z, InteriorCoverReach, out _, p => MathF.Abs(mesh.HeightAt(p, c.X, c.Z) - c.Y) > InteriorCoverHeight) >= 0;
    }

    static float Float(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0;

    static long Int(object? v) => v is long l ? l : 0;
}

/// <summary>One interior group of an original tile: the uid from its Info string, the Info point, its mesh in world coordinates and its mesh connections to the exterior.</summary>
public sealed record OriginalInterior(string Uid, Vector3 Origin, Vector3[] Vertices, int[][] Polygons, int Connections = 0);

/// <summary>One original interior group against a built zone: its faces in the zone, how many are covered, and the uncovered faces' height range.</summary>
public sealed record InteriorCoverage(OriginalInterior Group, int Faces, int Covered, float MissingLow, float MissingHigh);
