using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Population;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

// Port of upstream/ProvingGrounds/plugin/src/ArenaIdentity.h/.cpp (Proving Grounds by Shutori, GPLv3) and the site search of ArenaIngress.cpp:112-233,
// task 4.1 (PA §2.2 first bullets, §2.5 "Site search"; CAP 6 "Find placed buildings"). Every C++ function, in order with its line range, is in
// Core/Glue/ArenaIdentityLogic.cs over ArenaBuilding records (tested with fakes, Tests/Glue/ArenaIdentityLogicTests.cs). This file turns Meitou's
// buildings into those records. Meitou 0.17.3 (tag v0.17.3, fa34a1b).
//
// Engine calls of the C++ and what replaces them (E1):
//   RootObject::getGameData()->stringID, Building::getGameData() (:87-105, :111, :118) ... ArenaBuilding.RecordId: a player site's
//       BuildSite.Record (src/Meitou.Simulation/Construction/BuildingRegistry.cs:51), a town building's TownBuilding.BuildingId
//       (src/Meitou.Data/World/TownBuildings.cs:116), both the BUILDING record's string id
//   RootObject::getDataType() == BUILDING / BUILDING_FUNCTIONALITY / FOLIAGE_BUILDING (LooksLikeBuilding :63-71) ... always a building here
//   Building::getUseableStuff() + 0x400 = the use node's functionality GameData (:73-84) ... ArenaBuilding.FunctionRecordId =
//       PgEngine.FunctionRecord: ProductionSystem.Data(record)?.Functionality (ProductionSystem.cs:181-187, MachineData.cs:59/:360), the BUILDING
//       record's first functionality; null for a building that neither works nor stores (MachineData.From, :323-327: Arena 183 / 334), whose
//       record is then its only match, as in the C++ for a building without a use node
//   Building::isValid(), getBuildState(), ConstructionState::isComplete (IsBuildingFinished :184-193) ... ArenaBuilding.Finished: a player
//       site's BuildSite.Complete (BuildingRegistry.cs:105; set true only by the private ConstructionSystem.Finish, ConstructionSystem.cs:1386-1390);
//       a town building has no construction state (finished) unless the player repairs it, then its site's Complete (PgEngine.FromTown).
//       A ruin / destroyed site is never listed (PgEngine.IsListed, BuildingRegistry.cs:123, FixtureRegistry.Ruins.cs:50). Unknown, port
//       choice: the C++ filters only isValid(), so the original may pick a destroyed site; the port never does
//   Building::getPosition(), getOrientation() ... ArenaBuilding.Position / Yaw (BuildSite.Position + TownBuildings.YawOf(BuildSite.Rotation),
//       TownBuildings.cs:534; TownBuilding.Position / Yaw, TownBuildings.cs:116; CAP 6)
//   ou->getObjectsWithinSphere(results, origin, 25000, BUILDING, 512, NULL) (ArenaIngress.cpp:183) ... PgEngine.BuildingsNear (town
//       buildings of every placed site, then the player's construction) + ArenaIdentityLogic.SphereHits (nearest first, cap 512)
//   level placements outside every town (CAP 6 last bullet, BuildingFootprints.PlacementsAround) ... not reachable from a tick system
//       (NavSystem is the host's, WorldBoot.cs:159): none listed; every building the original matches is a player site or a town building
//       (divergences.md "Forced by Meitou", the getObjectsWithinSphere row; DATA §1)
//   hand g_boundRegistry.isValid() / getBuilding() (ArenaIngress.cpp:215-217) ... Resolve(ref): the building found again by its key, only
//       while the key still names the same record (ArenaIdentityLogic.Accept)
//   PGLog::Debug (LogInteractIdentity :219-245, site search :203-208) ... PGLog.Debug, the C++ texts (%.1f / %.0f through CMath.FormatF)
//
// Key rule (for 4.2 / 4.3 / M7 holding a building across ticks): later tasks hold and save ONLY an ArenaBuildingRef (Source, Key, RecordId:
// ArenaBuilding.Ref), never an ArenaBuilding (a snapshot: Finished / Position / Yaw as of when it was read). Key = a player site's BuildSite.Id
// ("1-zone10.35-INGAME", BuildingRegistry.cs:39; a load makes the site again with the saved instance id, src/Meitou.Simulation/Saving/
// BuildingSave.cs:115-117, so the key survives a save and load: checked live by tools/scenarios/arena-identity.sh with pg resolve) or a town
// building's TownBuilding.Key (TownBuildings.cs:160, zone + level instance id, fixed by the level data). Resolve(ref) each tick gives the building
// as it is now, or null when it is gone (dismantled, destroyed, a new world) or the key now names another record (a site id handed out again).
// Cost: a player ref = one dictionary lookup; a town ref = the hint position first (cached here per key), else a scan of the placed towns' lists.

/// <summary>The glue of the original ArenaIdentity.cpp: Meitou buildings as <see cref="ArenaBuilding"/> records, the site search, re-finding a
/// held building. Sim thread only.</summary>
static class ArenaIdentity
{
    static ArenaBuildingSource SourceOf(PgEngine.BuildingSource s) => s == PgEngine.BuildingSource.Player ? ArenaBuildingSource.Player : ArenaBuildingSource.Town;

    /// <summary>Per world: town keys -> where FindBuilding found them last (sim thread only; a stale hint is checked and falls back to the scan).
    /// Keyed by the world instance, so a new world (a load, a new game) starts with no hints and an old world's table goes with it. Bounded by
    /// the number of town buildings ever resolved in that world.</summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<World, Dictionary<string, (int Site, int Index)>> townHintsOf = new();

    /// <summary>An engine building as ArenaIdentity reads it; the functionality record from <paramref name="production"/> (see the header).</summary>
    internal static ArenaBuilding From(PgEngine.PgBuilding b, Meitou.Simulation.Production.ProductionSystem? production) =>
        new(SourceOf(b.Source), b.Key, b.RecordId, PgEngine.FunctionRecord(production, b.RecordId), b.Complete, b.Position, b.Yaw);

    /// <summary>Every building (any record) within <paramref name="radius"/> of <paramref name="centre"/> (3D), town buildings first.</summary>
    internal static List<ArenaBuilding> Near(World world, PopulationData data, Vector3 centre, float radius)
    {
        var production = PgEngine.Production(world);
        var list = new List<ArenaBuilding>();
        foreach (var b in PgEngine.BuildingsNear(world, data, centre, radius)) list.Add(From(b, production));
        return list;
    }

    /// <summary>The buildings ArenaIdentity recognises (<see cref="ArenaIdentityLogic.KindOf"/> not None) within <paramref name="radius"/>.</summary>
    internal static List<ArenaBuilding> RecognisedNear(World world, PopulationData data, Vector3 centre, float radius) =>
        [.. Near(world, data, centre, radius).Where(b => ArenaIdentityLogic.KindOf(b) != ArenaBuildingKind.None)];

    /// <summary>The held building as it is now (the C++ <c>hand::isValid()</c> + <c>getBuilding()</c>), or null when it is gone or its key now
    /// names another record (<see cref="ArenaIdentityLogic.Accept"/>).</summary>
    internal static ArenaBuilding? Resolve(World world, PopulationData data, ArenaBuildingRef held) => Resolve(world, data, held, out _);

    /// <summary><see cref="Resolve(World, PopulationData, ArenaBuildingRef)"/> and the building's town (the C++ <c>Building::getTown()</c>,
    /// <see cref="PgEngine.PgBuilding.Town"/>; <c>CampaignTown.None</c> when it is gone). Task 7.1 (TownArena's held registries).</summary>
    internal static ArenaBuilding? Resolve(World world, PopulationData data, ArenaBuildingRef held, out CampaignTown town)
    {
        var found = Find(world, data, held);
        var accepted = ArenaIdentityLogic.Accept(held, found is null ? null : From(found, PgEngine.Production(world)));
        town = accepted is not null && found is not null ? found.Town : CampaignTown.None;
        return accepted;
    }

    static PgEngine.PgBuilding? Find(World world, PopulationData data, ArenaBuildingRef held)
    {
        PgEngine.PgBuilding? found;
        if (held.Source == ArenaBuildingSource.Player)
        {
            (int, int) none = (-1, -1);
            found = PgEngine.FindBuilding(world, data, PgEngine.BuildingSource.Player, held.Key, ref none);
        }
        else
        {
            var townHints = townHintsOf.GetValue(world, static _ => new(StringComparer.Ordinal));
            var hint = townHints.TryGetValue(held.Key, out var h) ? h : (-1, -1);
            found = PgEngine.FindBuilding(world, data, PgEngine.BuildingSource.Town, held.Key, ref hint);
            if (hint.Item1 >= 0) townHints[held.Key] = hint; else townHints.Remove(held.Key);
        }
        return found;
    }

    /// <summary>Still there, same record, and finished (the C++ <c>isValid() &amp;&amp; IsBuildingFinished</c> on a held building).</summary>
    internal static bool StillFinished(World world, PopulationData data, ArenaBuildingRef held) =>
        ArenaIdentityLogic.IsBuildingFinished(Resolve(world, data, held));

    /// <summary>C++: ArenaIngress.cpp:213-224 <c>ResolveSearchOrigin</c>: the bound Registry (if it still exists) else the first fighter (if
    /// valid) else the origin.</summary>
    internal static Vector3 ResolveSearchOrigin(World world, PopulationData data, ArenaBuildingRef? boundRegistry, IReadOnlyList<CharacterId> fighters)
    {
        Vector3? reg = boundRegistry is { } held && Resolve(world, data, held) is { } now ? now.Position : null;
        Vector3? first = fighters.Count > 0 ? PgEngine.Position(world, fighters[0]) : null;
        return ArenaIdentityLogic.ResolveSearchOrigin(reg, first);
    }

    /// <summary>C++: ArenaIngress.cpp:175-211 + :226-233 <c>FindSiteForMode(mode, origin)</c>: the nearest (x/z) building of the mode's kind
    /// among the buildings within 25,000 of <paramref name="origin"/>; logs the C++ "site search" line (PGLog.Debug).</summary>
    internal static ArenaBuilding? FindSite(World world, PopulationData data, ArenaLocationMode mode, Vector3 origin)
    {
        var all = Near(world, data, origin, ArenaIdentityLogic.SiteSearchRadius);
        var site = ArenaIdentityLogic.FindSite(mode, origin, all, out string line);
        PGLog.Debug(line);
        return site;
    }

    /// <summary>C++: ArenaIdentity.cpp:219-245 <c>LogInteractIdentity</c> (task 4.2's right-click calls it).</summary>
    internal static void LogInteractIdentity(ArenaBuilding? obj, string? where)
    {
        if (ArenaIdentityLogic.InteractIdentityText(obj, where) is { } line) PGLog.Debug(line);
    }

    /// <summary>Port only: "player:&lt;id&gt;" / "town:&lt;key&gt;", the text the test verbs print and accept.</summary>
    internal static string KeyText(ArenaBuildingRef r) => (r.Source == ArenaBuildingSource.Player ? "player:" : "town:") + r.Key;

    /// <summary>Port only: parses <see cref="KeyText"/> back (false for any other text).</summary>
    internal static bool TryParseKey(string text, out ArenaBuildingSource source, out string key)
    {
        source = ArenaBuildingSource.Player;
        key = "";
        if (text.StartsWith("player:", StringComparison.Ordinal)) { key = text[7..]; return key.Length > 0; }
        if (text.StartsWith("town:", StringComparison.Ordinal)) { source = ArenaBuildingSource.Town; key = text[5..]; return key.Length > 0; }
        return false;
    }
}
