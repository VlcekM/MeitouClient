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
/// <c>hkaiNavMesh</c> faces, edges and vertices, the vertices zone-local at 0.1 scale. Only the exterior mesh (the tile's first) is read; the interior groups that follow
/// it are in a frame of their own (not decoded).
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
        var polygons = new int[faces.Length][];
        var kinds = new OriginalEdge[faces.Length][];
        for (int f = 0; f < faces.Length; f++)
        {
            long start = Int(faces[f]["startEdgeIndex"]), count = Int(faces[f]["numEdges"]);
            var poly = new int[count];
            var kind = new OriginalEdge[count];
            for (int k = 0; k < count; k++)
            {
                var e = edges[start + k];
                poly[k] = (int)Int(e["a"]);
                kind[k] = Int(e["oppositeFace"]) >= 0 ? OriginalEdge.Inner : joined.Contains((f, start + k)) ? OriginalEdge.TileBorder : OriginalEdge.End;
            }
            polygons[f] = poly;
            kinds[f] = kind;
        }
        return new OriginalNavMesh(zone, vertices, polygons, kinds);
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
            var polygons = new int[faces.Length][];
            for (int f = 0; f < faces.Length; f++)
            {
                long start = Int(faces[f]["startEdgeIndex"]), count = Int(faces[f]["numEdges"]);
                var poly = new int[count];
                for (int k = 0; k < count; k++) poly[k] = (int)Int(edges[start + k]["a"]);
                polygons[f] = poly;
            }
            result.Add(new OriginalInterior(parts[0], origin, vertices, polygons));
            mesh = null;
        }
        return result;
    }

    static float Float(string s) => float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 0;

    static long Int(object? v) => v is long l ? l : 0;
}

/// <summary>One interior group of an original tile: the uid from its Info string, the Info point and its mesh in world coordinates.</summary>
public sealed record OriginalInterior(string Uid, Vector3 Origin, Vector3[] Vertices, int[][] Polygons);
