using Meitou.Data.Gameplay;

namespace Meitou.Data.World;

/// <summary>
/// What housing the residents does to the town buildings, laid out once in the level data before anything reads it, from the same seed-free assignment the
/// population houses its towns with (<see cref="ResidentHomes.Assign"/>):
/// <list type="bullet">
/// <item>the ruins (docs/game/buildings-production.md "Ruined by residents"): the original's squad setup <c>FUN_14057f1f0</c> destroys a home whose template has
/// <c>building ruined</c> when the building can be (<c>FUN_14055b520</c>);</item>
/// <item>the layouts (docs/game/factions-squads-towns.md "Town residents"): the original gives a home its template's <c>layout interior</c> and <c>layout exterior</c>
/// (<c>FUN_140552140</c>), replacing or clearing what the level state names.</item>
/// </list>
/// </summary>
public static class ResidentRuins
{
    /// <summary>
    /// Sets the level-data state's <c>destroyed</c> of each building its residents ruin and names each home's layouts as its residents' template does; returns how
    /// many it ruined. A second call on the same levels does nothing.
    /// </summary>
    public static int Apply(GameDatabase db, WorldLevelData levels, BuildingLayouts layouts)
    {
        lock (levels)
        {
            if (levels.ResidentRuinsApplied) return 0;
            levels.ResidentRuinsApplied = true;
            var buildings = TownBuildings.Build(db, levels, layouts, (_, _) => 0f, contents: false);
            string? player = db.Find(FactionData.PlayerId) is not null ? FactionData.PlayerId : null;
            int ruined = 0;
            foreach (var placement in levels.Towns())
            {
                if (!buildings.TryGetValue(placement.InstanceId, out var list) || db.Find(placement.TownId) is not { Type: Fcs.FcsRecordType.TOWN } record) continue;
                var town = TownData.From(record);
                var faction = town.Faction is { } id && db.Find(id) is { Type: Fcs.FcsRecordType.FACTION } f ? FactionData.From(f) : null;
                foreach (var (b, t) in ResidentHomes.Assign(db, town, faction, list, player))
                {
                    if (t.BuildingRuined && Ruin(db, levels, b)) ruined++;
                    LayOut(levels, b, t);
                }
            }
            return ruined;
        }
    }

    static bool Ruin(GameDatabase db, WorldLevelData levels, TownBuilding b)
    {
        if (b.Destroyed || b.LevelState is not { } state || db.Find(b.BuildingId) is not { } record || !CanBeDestroyed(db, record)) return false;
        state.BoolTable["destroyed"] = true;
        levels.ResidentRuined.Add(b.Key);
        return true;
    }

    /// <summary>
    /// Names the template's <c>layout interior</c> and <c>layout exterior</c> on its home whatever the level state said, an empty one clearing it, ruins too (the
    /// original's <c>FUN_140552140</c>; a name the building has no layout for places nothing, as in the original). Nothing reads a ruin's layouts.
    /// </summary>
    static void LayOut(WorldLevelData levels, TownBuilding b, SquadTemplate t)
    {
        if (b.LevelState is not { } state) return;
        var level = (state.GetString(InteriorKey), state.GetString(ExteriorKey));
        if (level != (t.LayoutInterior, t.LayoutExterior)) levels.LevelLayoutNames.TryAdd(b.Key, level);
        foreach (var (key, layout) in Names(t)) state.StringTable[key] = layout;
    }

    /// <summary>The level-state keys housing <paramref name="t"/> names, with the layout names it gives them (<see cref="LayOut"/>).</summary>
    public static IEnumerable<(string Key, string Layout)> Names(SquadTemplate t) =>
        [(InteriorKey, t.LayoutInterior), (ExteriorKey, t.LayoutExterior)];

    public const string InteriorKey = "interior layout name", ExteriorKey = "exterior layout name";

    /// <summary>
    /// The interior and exterior layout names housing <paramref name="town"/>'s residents afresh in <paramref name="buildings"/> gives its homes (a town override,
    /// <see cref="LayOut"/>), by building key; buildings it houses nobody in are not listed. <paramref name="playerOwned"/> as for <see cref="RuinedHomes"/>.
    /// </summary>
    public static Dictionary<string, (string Interior, string Exterior)> HomeLayouts(GameDatabase db, TownData town, FactionData? faction,
        IReadOnlyList<TownBuilding> buildings, string? playerFactionId, IReadOnlySet<string>? playerOwned = null)
    {
        var result = new Dictionary<string, (string Interior, string Exterior)>(StringComparer.Ordinal);
        foreach (var (b, t) in ResidentHomes.Assign(db, town, faction, buildings, playerFactionId, playerOwned)) result[b.Key] = (t.LayoutInterior, t.LayoutExterior);
        return result;
    }

    /// <summary>
    /// The keys of the homes <paramref name="town"/>'s residents ruin when they are housed in <paramref name="buildings"/> afresh (a town override): homes of a ruined
    /// template that can be destroyed. <paramref name="playerOwned"/>: the buildings the player owns now, which house nobody (<see cref="ResidentHomes.Assign"/>).
    /// </summary>
    public static HashSet<string> RuinedHomes(GameDatabase db, TownData town, FactionData? faction, IReadOnlyList<TownBuilding> buildings, string? playerFactionId,
        IReadOnlySet<string>? playerOwned = null)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (b, t) in ResidentHomes.Assign(db, town, faction, buildings, playerFactionId, playerOwned))
            if (t.BuildingRuined && db.Find(b.BuildingId) is { } record && CanBeDestroyed(db, record)) result.Add(b.Key);
        return result;
    }

    /// <summary>
    /// Whether a building can be ruined (<c>FUN_14055b520</c>, Verified decompile): it or a part reached through <c>parts</c> (followed to any depth) has a
    /// <c>destroyed mesh</c>. The original also says yes when the building's int at +0x198 is 7 (its meaning is Unknown; not modelled).
    /// </summary>
    public static bool CanBeDestroyed(GameDatabase db, GameRecord building)
    {
        var queue = new Queue<GameRecord>([building]);
        var seen = new HashSet<string>(StringComparer.Ordinal) { building.StringId };
        while (queue.TryDequeue(out var r))
        {
            if (r.GetPath("destroyed mesh").Length > 0) return true;
            foreach (var part in r.GetReferences("parts"))
                if (seen.Add(part.TargetStringId) && db.Find(part.TargetStringId) is { } p) queue.Enqueue(p);
        }
        return false;
    }
}
