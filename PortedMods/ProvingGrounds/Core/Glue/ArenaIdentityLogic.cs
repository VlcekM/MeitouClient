using System.Globalization;
using System.Numerics;

namespace ProvingGrounds.Core.Glue;

// The decisions of upstream/ProvingGrounds/plugin/src/ArenaIdentity.h/.cpp (Proving Grounds by Shutori, GPLv3) and of the site search in
// ArenaIngress.cpp:112-233, task 4.1 (PA §2.2, §2.5 "Site search"; CAP 6), every function in the C++ order, over plain building records so they
// run without the game (E3, Tests/Glue/ArenaIdentityLogicTests.cs). src/Game/ArenaIdentity.cs turns Meitou's buildings into ArenaBuilding records.
//
// The C++ takes RootObject* / Building*; here a building is an ArenaBuilding? (null = the C++ null pointer). Every ArenaBuilding is a BUILDING
// (LooksLikeBuilding, ArenaIdentity.cpp:63-71, is always true for one). The C++'s two reads per building, obj->getGameData() and
// building->getGameData(), are one read in Meitou (the record id); the use node's functionality record (UseableStuff + 0x400, :73-84) is
// FunctionRecordId: the BUILDING record's first functionality (Meitou keeps it on the record); null when the building has neither a
// functionality nor an inventory (MachineData.From returns null, MachineData.cs:323-327), as the ArenaIdentity.cs header says.

/// <summary>Where an <see cref="ArenaBuilding"/> came from (CAP 6 "Find placed buildings").</summary>
public enum ArenaBuildingSource
{
    /// <summary>The player's construction (<c>world.Buildings</c>).</summary>
    Player,
    /// <summary>A placed town's level building (<c>TownSite.Buildings</c>). Level placements outside every town are not reachable from a tick
    /// system in 0.17.3 (divergences.md, the getObjectsWithinSphere row): no source for them.</summary>
    Town,
}

/// <summary>
/// WHO a building is, without its state: what later tasks (4.2 right-click, 4.3 ingress and the ring site, M7 town) hold across ticks and save.
/// Hold and save only refs, never an <see cref="ArenaBuilding"/> (a snapshot: its equality includes Finished / Position / Yaw). The glue's
/// <c>Resolve(ref)</c> gives the building as it is now, or null when it is gone or the key now names another record (<see cref="ArenaIdentityLogic.Accept"/>).
/// Key: a player site's <c>BuildSite.Id</c> or a town building's <c>TownBuilding.Key</c> (src/Game/ArenaIdentity.cs header).
/// </summary>
public readonly record struct ArenaBuildingRef(ArenaBuildingSource Source, string Key, string RecordId);

/// <summary>A building as ArenaIdentity reads it: the source and the stable key the glue resolves it by again, the BUILDING record id, the use
/// node's functionality record id when exposed (null otherwise), "finished" (the C++ <c>IsBuildingFinished</c> reading: no construction state, or
/// a complete one), the building's origin and its yaw (radians, Meitou's <c>TownBuildings.YawOf</c>).</summary>
public readonly record struct ArenaBuilding(ArenaBuildingSource Source, string Key, string RecordId, string? FunctionRecordId, bool Finished,
    Vector3 Position, float Yaw)
{
    /// <summary>The identity to hold across ticks (<see cref="ArenaBuildingRef"/>).</summary>
    public ArenaBuildingRef Ref => new(Source, Key, RecordId);
}

/// <summary>C++ <c>ArenaIngress::LocationMode</c> (ArenaIngress.h:12-17): where the window's bout is staged. Values as the C++'s.</summary>
public enum ArenaLocationMode
{
    Arena = 0,
    SmallArena = 1,
    Banner = 2,
}

/// <summary>Port only: one word for what a building is, for the test-only <c>pg sites</c> listing (the C++ has the predicates only).</summary>
public enum ArenaBuildingKind { None, Registry, TownRegistry, Banner, Leaderboard, TownLeaderboard, Arena, SmallArena }

/// <summary>ArenaIdentity.cpp's predicates and ArenaIngress.cpp's site search.</summary>
public static class ArenaIdentityLogic
{
    // C++: ArenaIdentity.cpp:21-31 (FCS stringIDs, building and function ids)
    public const string RegistryStringId = PgRecords.Registry;
    public const string RegistryFunctionStringId = PgRecords.RegistryFunc;
    public const string ArenaStringId = PgRecords.OldArena;
    public const string SmallArenaStringId = PgRecords.SmallArena;
    public const string BannerStringId = PgRecords.Banner;
    public const string BannerFunctionStringId = PgRecords.BannerFunc;
    public const string LeaderboardStringId = PgRecords.Leaderboard;
    public const string LeaderboardFunctionStringId = PgRecords.LeaderboardFunc;
    public const string TownLeaderboardStringId = PgRecords.TownLeaderboard;
    public const string TownLeaderboardFunctionStringId = PgRecords.TownLeaderboardFunc;

    // C++: ArenaIdentity.cpp:35-38 (std::string == const char*: exact equality; a null data never matches)
    static bool IdEquals(string? data, string id) => data != null && data == id;

    // C++: ArenaIdentity.cpp:40-49
    static bool GetLeaderboardDataKind(string? data, ref LeaderboardData.Kind kind)
    {
        if (data == null || !LeaderboardData.IsLeaderboardId(data))
            return false;
        kind = LeaderboardData.KindForId(data);
        return true;
    }

    // C++: ArenaIdentity.cpp:51-55
    static bool IsRegistryData(string? data) => IdEquals(data, RegistryStringId) || IdEquals(data, RegistryFunctionStringId);

    // C++: ArenaIdentity.cpp:57-61
    static bool IsBannerData(string? data) => IdEquals(data, BannerStringId) || IdEquals(data, BannerFunctionStringId);

    // C++: ArenaIdentity.cpp:87-95
    public static string GetStringId(ArenaBuilding? obj) => obj is { } b ? b.RecordId : "";

    // C++: ArenaIdentity.cpp:97-105
    public static bool MatchesId(ArenaBuilding? obj, string? id) => obj is { } b && id != null && b.RecordId == id;

    // C++: ArenaIdentity.cpp:107-124. The building's own data (:111, and again :118 for a building) then its use node's functionality (:120).
    public static bool IsRegistry(ArenaBuilding? obj)
    {
        if (IsTownRegistry(obj)) return true;
        if (obj is not { } b)
            return false;
        if (IsRegistryData(b.RecordId))
            return true;
        if (IsRegistryData(b.FunctionRecordId))
            return true;
        return false;
    }

    // C++: ArenaIdentity.cpp:126-130
    public static bool IsArena(ArenaBuilding? obj) => MatchesId(obj, ArenaStringId) || (obj != null && TownArenaRuntimePolicy.IsNewArena(GetStringId(obj)));

    // C++: ArenaIdentity.cpp:131-134
    public static bool IsTownRegistry(ArenaBuilding? obj) => obj != null && TownArenaRuntimePolicy.IsTownRegistry(GetStringId(obj));

    // C++: ArenaIdentity.cpp:135
    public static bool IsSmallArena(ArenaBuilding? obj) => MatchesId(obj, SmallArenaStringId);

    // C++: ArenaIdentity.cpp:136-150
    public static bool IsBanner(ArenaBuilding? obj)
    {
        if (obj is not { } b)
            return false;
        if (IsBannerData(b.RecordId))
            return true;
        return IsBannerData(b.FunctionRecordId);
    }

    // C++: ArenaIdentity.cpp:152-155
    public static bool IsMatchUiOpener(ArenaBuilding? obj) => IsRegistry(obj) || IsBanner(obj);

    // C++: ArenaIdentity.cpp:157-161
    public static bool IsLeaderboard(ArenaBuilding? obj)
    {
        var kind = LeaderboardData.Kind.Player;
        return GetLeaderboardKind(obj, ref kind);
    }

    // C++: ArenaIdentity.cpp:163-182. <paramref name="kind"/> is written only on success, as the C++ reference parameter.
    public static bool GetLeaderboardKind(ArenaBuilding? obj, ref LeaderboardData.Kind kind)
    {
        if (obj is not { } b)
            return false;
        if (GetLeaderboardDataKind(b.RecordId, ref kind))
            return true;
        if (GetLeaderboardDataKind(b.FunctionRecordId, ref kind))
            return true;
        return false;
    }

    // C++: ArenaIdentity.cpp:184-193. Finished is the glue's reading of the construction state (no state = finished; else isComplete).
    public static bool IsBuildingFinished(ArenaBuilding? building) => building is { } b && b.Finished;

    // C++: ArenaIdentity.cpp:195-198
    public static bool IsRegistryFinished(ArenaBuilding? building) => IsRegistry(building) && IsBuildingFinished(building);

    // C++: ArenaIdentity.cpp:200-203
    public static bool IsMatchUiOpenerFinished(ArenaBuilding? building) => IsMatchUiOpener(building) && IsBuildingFinished(building);

    // C++: ArenaIdentity.cpp:205-209
    public static bool IsLeaderboardFinished(ArenaBuilding? building)
    {
        var kind = LeaderboardData.Kind.Player;
        return GetFinishedLeaderboardKind(building, ref kind);
    }

    // C++: ArenaIdentity.cpp:211-217 (the kind is written before the finished test)
    public static bool GetFinishedLeaderboardKind(ArenaBuilding? building, ref LeaderboardData.Kind kind) =>
        GetLeaderboardKind(building, ref kind) && IsBuildingFinished(building);

    /// <summary>C++: ArenaIdentity.cpp:219-245 <c>LogInteractIdentity</c>: the PGLog::Debug text, or null where the C++ logs nothing (null obj).</summary>
    public static string? InteractIdentityText(ArenaBuilding? obj, string? where)
    {
        if (obj is not { } b)
            return null;
        string sid = !string.IsNullOrEmpty(b.RecordId) ? b.RecordId : "(none)";
        string funcSid = !string.IsNullOrEmpty(b.FunctionRecordId) ? b.FunctionRecordId! : "(none)";
        return "Proving Grounds: " + (where ?? "interact") + " stringID=" + sid + " func=" + funcSid
            + (IsLeaderboard(obj) ? " [leaderboard]" : "")
            + (IsRegistry(obj) ? " [registry]" : "")
            + (IsBanner(obj) ? " [banner]" : "");
    }

    /// <summary>Port only (test listing): what a building is, the C++ predicates in the order ArenaIngress / ContextMenuHooks ask them
    /// (a town registry before a registry).</summary>
    public static ArenaBuildingKind KindOf(ArenaBuilding? obj)
    {
        var kind = LeaderboardData.Kind.Player;
        if (IsTownRegistry(obj)) return ArenaBuildingKind.TownRegistry;
        if (IsRegistry(obj)) return ArenaBuildingKind.Registry;
        if (IsBanner(obj)) return ArenaBuildingKind.Banner;
        if (GetLeaderboardKind(obj, ref kind)) return kind == LeaderboardData.Kind.Town ? ArenaBuildingKind.TownLeaderboard : ArenaBuildingKind.Leaderboard;
        if (IsArena(obj)) return ArenaBuildingKind.Arena;
        if (IsSmallArena(obj)) return ArenaBuildingKind.SmallArena;
        return ArenaBuildingKind.None;
    }

    /// <summary>C++: ArenaRingGuard.cpp:40-48 <c>RingScaleForSite</c>'s reading of the site building (a banner first, then a small arena, else
    /// a full arena), for <c>RingSite.Kind</c> (task 4.3's SetArena). Non-null: the C++'s null site (scale 0, no ring) is "no RingSite".</summary>
    public static RingSiteKind RingKindOf(ArenaBuilding site) =>
        IsBanner(site) ? RingSiteKind.Banner : IsSmallArena(site) ? RingSiteKind.SmallArena : RingSiteKind.Arena;

    /// <summary>Port only (the C++ <c>hand</c> resolve, ArenaIngress.cpp:123-125, :215-217): the building found again by a ref's key, accepted
    /// only when it is the same source and key and STILL THE SAME RECORD (a player site id can be handed out again after a dismantle or a
    /// reload: then the key names another building and the ref is gone). Null otherwise.</summary>
    public static ArenaBuilding? Accept(ArenaBuildingRef held, ArenaBuilding? found) =>
        found is { } b && b.Source == held.Source && b.Key == held.Key && b.RecordId == held.RecordId ? b : null;

    // ---------- the site search, ArenaIngress.cpp ----------

    /// <summary>C++: ArenaIngress.cpp:112 <c>kSiteSearchRadius</c>.</summary>
    public const float SiteSearchRadius = 25000.0f;
    /// <summary>C++: ArenaIngress.cpp:113 <c>kSiteSearchMax</c>.</summary>
    public const int SiteSearchMax = 512;

    // C++: ArenaIngress.cpp:161-166
    static float HorizontalDistSq(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return (dx * dx) + (dz * dz);
    }

    /// <summary>C++ <c>ou-&gt;getObjectsWithinSphere(results, origin, kSiteSearchRadius, BUILDING, kSiteSearchMax, NULL)</c>
    /// (ArenaIngress.cpp:183): every building within 25,000 of the origin in 3D, at most 512. Kenshi's order of the hits is unknown; the port
    /// takes them nearest first (3D, stable for ties), so the cap drops the farthest (divergences.md "ArenaIdentity").</summary>
    public static List<ArenaBuilding> SphereHits(IEnumerable<ArenaBuilding> buildings, Vector3 origin)
    {
        float r2 = SiteSearchRadius * SiteSearchRadius;
        var inside = new List<(float D, int I, ArenaBuilding B)>();
        int i = 0;
        foreach (var b in buildings)
        {
            float d = Vector3.DistanceSquared(b.Position, origin);
            if (d <= r2) inside.Add((d, i, b));
            i++;
        }
        inside.Sort((x, y) => x.D != y.D ? x.D.CompareTo(y.D) : x.I.CompareTo(y.I));
        var hits = new List<ArenaBuilding>(Math.Min(inside.Count, SiteSearchMax));
        for (int k = 0; k < inside.Count && k < SiteSearchMax; k++) hits.Add(inside[k].B);
        return hits;
    }

    /// <summary>C++: ArenaIngress.cpp:175-211 <c>FindNearestMatching</c> over the sphere's hits: the matching building nearest in x/z (strictly
    /// nearer replaces, so the first of a tie stays), and the C++ PGLog::Debug line (always written, :203-208).</summary>
    public static ArenaBuilding? FindNearestMatching(Vector3 origin, IReadOnlyList<ArenaBuilding> results, Func<ArenaBuilding?, bool> matches,
        out string logLine)
    {
        ArenaBuilding? best = null;
        float bestDist = 1.0e30f;
        int matchCount = 0;
        for (int i = 0; i < results.Count; ++i)
        {
            ArenaBuilding obj = results[i];
            if (!matches(obj))
                continue;

            ++matchCount;
            float d = HorizontalDistSq(origin, obj.Position);
            if (d < bestDist)
            {
                bestDist = d;
                best = obj;
            }
        }

        // sprintf_s "%d ... %.1f ... %.0f": the float arguments promote to double
        logLine = "Proving Grounds: site search hits=" + results.Count.ToString(CultureInfo.InvariantCulture)
            + " matched=" + matchCount.ToString(CultureInfo.InvariantCulture)
            + " bestDistXZ=" + CMath.FormatF(best != null ? MathF.Sqrt(bestDist) : -1.0f, 1)
            + " (radius=" + CMath.FormatF(SiteSearchRadius, 0) + ")";
        return best;
    }

    /// <summary>C++: ArenaIngress.cpp:213-224 <c>ResolveSearchOrigin</c>: the bound Registry's position when it is valid, else the first
    /// fighter's when valid, else the origin. Null = not valid / none.</summary>
    public static Vector3 ResolveSearchOrigin(Vector3? boundRegistry, Vector3? firstFighter)
    {
        if (boundRegistry is { } reg)
            return reg;
        if (firstFighter is { } f)
            return f;
        return Vector3.Zero;
    }

    /// <summary>C++: ArenaIngress.cpp:226-233 <c>FindSiteForMode</c> over the sphere's hits.</summary>
    public static ArenaBuilding? FindSiteForMode(ArenaLocationMode mode, Vector3 origin, IReadOnlyList<ArenaBuilding> hits, out string logLine)
    {
        if (mode == ArenaLocationMode.Banner)
            return FindNearestMatching(origin, hits, IsBanner, out logLine);
        if (mode == ArenaLocationMode.SmallArena)
            return FindNearestMatching(origin, hits, IsSmallArena, out logLine);
        return FindNearestMatching(origin, hits, IsArena, out logLine);
    }

    /// <summary><see cref="SphereHits"/> then <see cref="FindSiteForMode"/>: the whole of the C++ search from every building the glue can see.</summary>
    public static ArenaBuilding? FindSite(ArenaLocationMode mode, Vector3 origin, IEnumerable<ArenaBuilding> buildings, out string logLine) =>
        FindSiteForMode(mode, origin, SphereHits(buildings, origin), out logLine);
}
