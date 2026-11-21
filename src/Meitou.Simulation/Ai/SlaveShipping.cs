using System.Numerics;
using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Ai;

/// <summary>
/// Where a slave shipping package (signals 11 and 28, docs/game/ai.md "Prisoner and slaver packages") takes its slaves: the loaded package's
/// activation and the off-screen stand-in's TRAVEL_TARGET read the same rule. Pure: reads the data only.
/// </summary>
internal static class SlaveShipping
{
    /// <summary>
    /// The building designation of a slave pen (BD 10: the Slaver Shop and Cage Warden templates).
    /// </summary>
    const int PenDesignation = 10;

    /// <summary>
    /// The signal of the anywhere kind (SIG_SLAVER_SHIP_TO_ANYWHERE_CLOSE).
    /// </summary>
    const int AnywhereSignal = 28;

    /// <summary>
    /// The share of the camp's squared distance within which the anywhere kind takes a town (<c>DAT_1416ae650</c>, read from the exe: 0.7).
    /// </summary>
    const float TownShare = 0.7f;

    /// <summary>
    /// The mission town of a shipping package from <paramref name="at"/> (<b>Verified</b>): the nearest slave camp of a faction not hostile to
    /// <paramref name="faction"/> (<c>FUN_1402873a0</c> over <c>FUN_140927f10</c>, type 5). The anywhere kind (28, <c>FUN_140289f90</c> over
    /// <c>FUN_140939090</c>) takes instead the nearest TOWN (type 2) of such a faction within 0.7 x the camp's squared distance (3D; anywhere with
    /// no camp) that houses slave pens (<see cref="HousesPens"/>); none, the camp. -1: neither.
    /// </summary>
    internal static int Town(PopulationData data, Vector3 at, int faction, int signal) => Pick(data, new(at.X, at.Z), at.Y, faction, signal);

    /// <summary>
    /// <see cref="Town(PopulationData, Vector3, int, int)"/> for a stand-in, which has no height: the camp's distance is the X/Z one.
    /// </summary>
    internal static int Town(PopulationData data, Vector2 at, int faction, int signal) => Pick(data, at, null, faction, signal);

    /// <summary>
    /// Whether <paramref name="site"/> is a town a shipping package of <paramref name="signal"/> may have picked: a slave camp, or for the anywhere
    /// kind a TOWN that houses slave pens.
    /// </summary>
    internal static bool IsDestination(PopulationData data, int site, int signal)
    {
        if (site < 0 || site >= data.Sites.Count) return false;

        var town = data.Sites[site];
        return town.Town.Type == TownType.SlaveCamp
            || signal == AnywhereSignal && town.Town.Type == TownType.Town && HousesPens(data, town);
    }

    /// <summary>
    /// The pick of <see cref="Town(PopulationData, Vector3, int, int)"/>; <paramref name="height"/> null: the camp's distance is the X/Z one.
    /// </summary>
    static int Pick(PopulationData data, Vector2 at, float? height, int faction, int signal)
    {
        int camp = Nearest(data, at, faction, TownType.SlaveCamp, float.MaxValue, false);
        if (signal != AnywhereSignal) return camp;

        float within = camp >= 0 ? CampDistance(data.Sites[camp].Position, at, height) * TownShare : float.MaxValue;
        int town = Nearest(data, at, faction, TownType.Town, within, true);
        return town >= 0 ? town : camp;
    }

    /// <summary>
    /// The squared distance from the shipper to the camp at <paramref name="camp"/> (Ogre's <c>squaredDistance</c>, 3D with a height).
    /// </summary>
    static float CampDistance(Vector3 camp, Vector2 at, float? height)
    {
        float flat = Vector2.DistanceSquared(at, new Vector2(camp.X, camp.Z));
        return height is { } y ? flat + (camp.Y - y) * (camp.Y - y) : flat;
    }

    /// <summary>
    /// The nearest town of <paramref name="type"/> (X/Z distance) whose faction is not hostile to <paramref name="faction"/>, within the squared
    /// distance <paramref name="within"/>, and with <paramref name="pens"/> one that houses slave pens; -1: none.
    /// </summary>
    static int Nearest(PopulationData data, Vector2 at, int faction, TownType type, float within, bool pens)
    {
        int best = -1;
        float bestD = float.MaxValue;
        foreach (var site in data.Sites)
        {
            if (site.Town.Type != type || site.Index >= data.SiteFactions.Count || IsHostileTo(data, data.SiteFactions[site.Index], faction)) continue;

            float d = Vector2.DistanceSquared(at, new Vector2(site.Position.X, site.Position.Z));
            if (d > within || d >= bestD || pens && !HousesPens(data, site)) continue;

            (best, bestD) = (site.Index, d);
        }

        return best;
    }

    /// <summary>
    /// Whether the faction <paramref name="f"/> is hostile to <paramref name="faction"/> (false when either is none or has no relations).
    /// </summary>
    static bool IsHostileTo(PopulationData data, int f, int faction) =>
        f >= 0 && faction >= 0 && data.Relations.Contains(f) && data.Relations.Contains(faction) && data.Relations.IsHostile(f, faction);

    /// <summary>
    /// Whether the town's building designation tags hold the slave pen's (bit 10 of the town's <c>TagsClass&lt;BuildingDesignation&gt;</c> at
    /// <c>+0x498</c>, <b>Verified</b>: <c>FUN_140936e60</c> fills it once through <c>FUN_140935d10</c> with the <c>building designation</c> of every
    /// template in the faction's <c>residents</c> (unless <c>residents override</c>) and the town's, whatever the entry's count; a town with no
    /// faction has none).
    /// </summary>
    static bool HousesPens(PopulationData data, TownSite site)
    {
        int f = site.Index < data.SiteFactions.Count ? data.SiteFactions[site.Index] : -1;
        if (f < 0) return false;

        var residents = site.Town.ResidentsOverride ? site.Town.Residents : data.Factions[f].Residents.Concat(site.Town.Residents);
        return residents.Any(entry => IsPenTemplate(data, entry));
    }

    /// <summary>
    /// Whether the resident entry names a squad template of the slave pen's building designation.
    /// </summary>
    static bool IsPenTemplate(PopulationData data, RecordLink entry) =>
        data.Db.Find(entry.Id) is { } record && SquadFactory.IsTemplate(record) && record.GetInt("building designation") == PenDesignation;
}
