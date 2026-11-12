using System.Numerics;
using Meitou.Data.Fcs;
using static Meitou.Data.World.TownBuildings;

namespace Meitou.Data.World;

/// <summary>
/// Builds the town buildings one at a time with the data the boot pass shares between them (<see cref="TownBuildings.Build"/> runs it over every
/// level building; the level editor over a single one placed in the session, so the two make a <see cref="TownBuilding"/> the same way). Its tables
/// are filled while it works: one builder is used by one thread.
/// </summary>
public sealed class TownBuildingBuilder
{
    /// <summary>
    /// A building with an interior outside every town's radius goes to the nearest town within this distance (the import's squared bound 1e8).
    /// </summary>
    const float InteriorReach = 10000;

    /// <summary>
    /// The merged game data.
    /// </summary>
    readonly GameDatabase _db;
    /// <summary>
    /// The level data the buildings are placed by.
    /// </summary>
    readonly WorldLevelData _levels;
    /// <summary>
    /// The building layouts; null lays out no contents.
    /// </summary>
    readonly BuildingLayouts? _layouts;
    /// <summary>
    /// The terrain height at a world X, Z.
    /// </summary>
    readonly Func<double, double, float> _terrainHeight;
    /// <summary>
    /// Whether the interior layouts are laid out (<see cref="TownBuildings.Build"/>'s <c>contents</c>).
    /// </summary>
    readonly bool _contents;
    /// <summary>
    /// The placed towns with their TOWN data.
    /// </summary>
    readonly List<(TownPlacement Placement, Gameplay.TownData Town)> _towns = [];
    /// <summary>
    /// Whether a BUILDING is a bed, by id (<see cref="TownBuildings.IsBed"/>'s cache).
    /// </summary>
    readonly Dictionary<string, bool> _beds = new(StringComparer.Ordinal);
    /// <summary>
    /// A BUILDING's functionality, by id (<see cref="TownBuildings.FunctionOf"/>'s cache).
    /// </summary>
    readonly Dictionary<string, GameRecord?> _functions = new(StringComparer.Ordinal);
    /// <summary>
    /// The building key of each building state's own handle (container, slot).
    /// </summary>
    readonly Dictionary<(int C, int I), string> _instances = [];
    /// <summary>
    /// The layout names of a building record by (id, exterior) (<see cref="LayoutNames"/>'s cache).
    /// </summary>
    readonly Dictionary<(string, bool), IReadOnlySet<string>> _names = [];

    /// <summary>
    /// A builder over <paramref name="levels"/>' buildings, standing them on <paramref name="terrainHeight"/>; <paramref name="contents"/> lays out
    /// what they hold.
    /// </summary>
    public TownBuildingBuilder(GameDatabase db, WorldLevelData levels, BuildingLayouts? layouts, Func<double, double, float> terrainHeight,
        bool contents = true)
    {
        (this._db, this._levels, this._layouts, this._terrainHeight, this._contents) = (db, levels, layouts, terrainHeight, contents);
        foreach (var t in levels.Towns())
            if (db.Find(t.TownId) is { Type: FcsRecordType.Town } record) _towns.Add((t, Gameplay.TownData.From(record)));

        // The instance of each building state's own handle, so a turret's mounted handle names the building it stands on.
        foreach (var b in levels.Buildings())
            if (b.StateId is not null && levels.Zones.TryGetValue(b.Zone, out var zone) && zone.Find(b.StateId) is { } state && IsBuildingHandle(state))
                _instances[(state.GetInt("handleC"), state.GetInt("handleI"))] = b.Key;
    }

    /// <summary>
    /// The town building of a level building and the placement id of its town; null for a node marker, a resource building or one no town takes
    /// (<see cref="MemberOf"/>).
    /// </summary>
    public (string Town, TownBuilding Building)? Build(BuildingPlacement b)
    {
        if (BuildingPlacements.Resolve(b, _db, _levels, _terrainHeight) is not { } resolved) return null;
        var record = resolved.Record;
        if (record.GetBool("is node") || record.GetBool("is foliage")) return null;
        var state = b.StateId is not null && _levels.Zones.TryGetValue(b.Zone, out var zoneDb) ? zoneDb.Find(b.StateId) : null;
        if (MemberOf(_towns, resolved.Position, record) is not { } town) return null;

        var functionality = FunctionOf(_db, record, _functions);
        var turret = TurretOf(b, resolved, functionality, state);
        var c = ContentsOf(resolved, state);
        if (_contents) c.AddAlone(_db, resolved, functionality);
        string? owner = state?.GetString("owner faction ID") is { Length: > 0 } o ? o : null;

        return (town.InstanceId, NewBuilding(b, resolved, state, functionality, turret, c, owner));
    }

    /// <summary>
    /// The state's own <c>handle</c> is a building's (type BUILDING, with a slot).
    /// </summary>
    static bool IsBuildingHandle(GameRecord state) => state.GetInt("handleTYPE") == (int)FcsRecordType.Building && state.Ints.ContainsKey("handleI");

    /// <summary>
    /// The turret post of a building whose functionality is BF_TURRET; null otherwise.
    /// </summary>
    TurretPost? TurretOf(BuildingPlacement b, ResolvedBuilding resolved, GameRecord? functionality, GameRecord? state)
    {
        var record = resolved.Record;
        return functionality is { } fn && fn.GetInt("function") == ObjectRanges.TurretFunction
            ? new TurretPost(resolved.Position, YawOf(b.Rotation), fn.GetFloat("use range", 10),
                record.GetReferences("gun turret").Select(r => r.TargetStringId).FirstOrDefault() ?? "", MountedOn(_instances, state))
            : null;
    }

    /// <summary>
    /// What the building's layouts put in it; with no contents laid out (or no layouts or state) only whether it has <c>interior</c> parts.
    /// </summary>
    TownBuildingContents ContentsOf(ResolvedBuilding resolved, GameRecord? state) =>
        _contents && _layouts is not null && state is not null
            ? TownBuildingContents.Lay(_db, _layouts, resolved, state, _beds, _functions)
            : new TownBuildingContents { Furnished = resolved.Record.GetReferences("interior").Count > 0 };

    /// <summary>
    /// The town building of a level building from its parts.
    /// </summary>
    TownBuilding NewBuilding(BuildingPlacement b, ResolvedBuilding resolved, GameRecord? state, GameRecord? functionality, TurretPost? turret,
        TownBuildingContents c, string? owner)
    {
        var record = resolved.Record;
        return new TownBuilding(b.InstanceId, record.StringId, record.Name, resolved.Position, YawOf(b.Rotation), c.Shop, c.Bar, c.Spots)
        {
            OwnerFactionId = owner,
            Fixtures = c.Fixtures,
            ItemSlots = c.ItemSlots,
            Zone = b.Zone,
            Resolved = resolved,
            CanHouse = record.GetBool("is wall gate") || record.GetReferences("interior").Count > 0 && !record.GetBool("links together"),
            Furnished = c.Furnished,
            IsGate = record.GetBool("is wall gate"),
            IsWall = record.GetBool("links together"),
            HasInterior = record.GetReferences("interior").Count > 0,
            Destroyed = resolved.Destroyed,
            RuinedByResidents = _levels.ResidentRuined.Contains(b.Key),
            Turret = turret,
            ResidentsTemplate = state?.GetString("residents template") ?? "",
            LevelState = state,
            InteriorLayout = state?.GetString("interior layout name") ?? "",
            LevelLayouts = _levels.LevelLayoutNames.TryGetValue(b.Key, out var raw) ? raw
                : (state?.GetString(ResidentRuins.InteriorKey) ?? "", state?.GetString(ResidentRuins.ExteriorKey) ?? ""),
            LevelNaming = _levels.LevelNames.TryGetValue(b.Key, out var named) ? named : BuildingNaming.Of(state),
            InteriorLayouts = LayoutNames(_layouts, record, exterior: false, _names),
            ExteriorLayouts = LayoutNames(_layouts, record, exterior: true, _names),
            HandleSlot = state is not null && state.Ints.ContainsKey("handleI") && state.GetInt("handleTYPE") == (int)FcsRecordType.Building
                ? state.GetInt("handleI")
                : -1,
            HandleSerial = state?.GetInt("handleS") ?? 0,
            Tables = c.Tables,
            Furniture = c.Furniture,
            Function = functionality?.GetInt("function") ?? -1,
            Generators = _contents && _layouts is not null ? GeneratorCount(_layouts, resolved, state) : 0,
        };
    }

    /// <summary>
    /// The town a level-data building belongs to (docs/formats/zones.md "Town membership", Observed: the building import <c>FUN_14057cc70</c>): the
    /// nearest town whose <see cref="Gameplay.TownData.Radius"/> holds it (X/Z, nest markers left out); else, for a record with <c>interior</c>
    /// parts, the nearest town within 10000; else none. The state's <c>town</c> handle is not used: the game keeps it only for player towns.
    /// </summary>
    internal static TownPlacement? MemberOf(List<(TownPlacement Placement, Gameplay.TownData Town)> towns, Vector3 position, GameRecord record)
    {
        var at = new Vector2(position.X, position.Z);
        TownPlacement? best = null;
        float bestD = float.MaxValue;
        foreach (var (p, t) in towns)
        {
            if (t.Type == Gameplay.TownType.NestMarker) continue;
            float d = Vector2.DistanceSquared(at, new Vector2(p.Position.X, p.Position.Z));
            if (d <= t.Radius * t.Radius && d < bestD) (best, bestD) = (p, d);
        }
        if (best is not null || record.GetReferences("interior").Count == 0) return best;

        bestD = InteriorReach * InteriorReach;
        foreach (var (p, _) in towns)
        {
            float d = Vector2.DistanceSquared(at, new Vector2(p.Position.X, p.Position.Z));
            if (d < bestD) (best, bestD) = (p, d);
        }
        return best;
    }

    /// <summary>
    /// The key of the building a state's <c>mounted</c> handle names; null when the handle is null, not a building, or unknown.
    /// </summary>
    static string? MountedOn(Dictionary<(int C, int I), string> instances, GameRecord? state) =>
        state is not null && state.GetInt("mountedTYPE") == (int)FcsRecordType.Building && state.Ints.ContainsKey("mountedI") &&
        instances.TryGetValue((state.GetInt("mountedC"), state.GetInt("mountedI")), out string? instance) ? instance : null;

    /// <summary>
    /// The layout names a building record can take (case ignored), cached by record and side; empty without layouts.
    /// </summary>
    static IReadOnlySet<string> LayoutNames(BuildingLayouts? layouts, GameRecord record, bool exterior,
        Dictionary<(string, bool), IReadOnlySet<string>> cache)
    {
        if (layouts is null) return new HashSet<string>();
        if (!cache.TryGetValue((record.StringId, exterior), out var set))
            cache[(record.StringId, exterior)] = set = new HashSet<string>(layouts.Names(record, exterior), StringComparer.OrdinalIgnoreCase);
        return set;
    }
}
