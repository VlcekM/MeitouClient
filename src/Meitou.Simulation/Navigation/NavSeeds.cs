using System.Numerics;
using Meitou.Content;
using Meitou.Data.World;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// <c>navtiles/seeds.def</c>: float3 records, absolute positions (Kenshi units), bucketed by zone.
/// </summary>
internal static class NavSeeds
{
    /// <summary>
    /// Where the file lies under the install's data folder.
    /// </summary>
    public const string RelativePath = "newland/land/navtiles/seeds.def";

    /// <summary>
    /// The seeds file the game reads: <see cref="RelativePath"/>, or an enabled mod's rebind of it (<see cref="ReKenshiRebinds"/>).
    /// </summary>
    public static string PathOf(GameInstall install) => ReKenshiRebinds.DataFile(install, RelativePath);

    /// <summary>
    /// The seeds by zone (index z x <see cref="WorldLayout.ZoneCount"/> + x), points outside the zone grid dropped; null when the file is missing.
    /// </summary>
    public static List<Vector3>[]? Load(GameInstall install)
    {
        var path = PathOf(install);
        if (!File.Exists(path)) return null;

        var bytes = File.ReadAllBytes(path);
        var zones = new List<Vector3>[WorldLayout.ZoneCount * WorldLayout.ZoneCount];
        for (int i = 0; i < zones.Length; i++)
            zones[i] = [];

        for (int o = 0; o + 12 <= bytes.Length; o += 12)
        {
            var p = new Vector3(BitConverter.ToSingle(bytes, o), BitConverter.ToSingle(bytes, o + 4), BitConverter.ToSingle(bytes, o + 8));
            var z = WorldLayout.ZoneOf(p.X, p.Z);
            if (z.IsInsideGrid) zones[z.Y * WorldLayout.ZoneCount + z.X].Add(p);
        }

        return zones;
    }
}
