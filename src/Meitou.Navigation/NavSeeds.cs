using System.Numerics;
using Meitou.Content;
using Meitou.Data.World;

namespace Meitou.Navigation;

/// <summary><c>navtiles/seeds.def</c>: float3 records, absolute positions (Kenshi units), bucketed by zone.</summary>
public static class NavSeeds
{
    public const string RelativePath = "newland/land/navtiles/seeds.def";

    public static List<Vector3>[]? Load(GameInstall install)
    {
        var path = Path.Combine(install.DataDirectory, RelativePath);
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        var zones = new List<Vector3>[WorldLayout.ZoneCount * WorldLayout.ZoneCount];
        for (int i = 0; i < zones.Length; i++) zones[i] = [];
        for (int o = 0; o + 12 <= bytes.Length; o += 12)
        {
            var p = new Vector3(BitConverter.ToSingle(bytes, o), BitConverter.ToSingle(bytes, o + 4), BitConverter.ToSingle(bytes, o + 8));
            var z = WorldLayout.ZoneOf(p.X, p.Z);
            if (z.IsInsideGrid) zones[z.Y * WorldLayout.ZoneCount + z.X].Add(p);
        }
        return zones;
    }
}
