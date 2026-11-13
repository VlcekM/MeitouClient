using Meitou.Data.Gameplay;

namespace Meitou.Data.World;

/// <summary>
/// What housing the residents does to the town buildings, laid out once in the level data before anything reads it, from the same seed-free
/// assignment the population houses its towns with (<see cref="ResidentHomes.Assign"/>):
/// <list type="bullet">
/// <item>the ruins (docs/game/buildings-production.md "Ruined by residents"): the original's squad setup <c>FUN_14057f1f0</c> destroys a home whose
/// template has <c>building ruined</c> when the building can be (<c>FUN_14055b520</c>);</item>
/// <item>the layouts (docs/game/factions-squads-towns.md "Town residents"): the original gives a home its template's <c>layout interior</c> and
/// <c>layout exterior</c> (<c>FUN_140552140</c>), replacing or clearing what the level state names;</item>
/// <item>the templates: a home carries its residents' template, any other building of a town none: the original's state reader drops the level
/// state's <c>residents template</c> (<c>FUN_140552b70</c>), so only housing gives a building one.</item>
/// </list>
/// </summary>
public static class ResidentRuins
{
    /// <summary>
    /// The level state's keys of the interior and exterior layout names.
    /// </summary>
    public const string InteriorKey = "interior layout name", ExteriorKey = "exterior layout name";
    /// <summary>
    /// The level state's keys of the building's name and its squad template.
    /// </summary>
    public const string NameKey = "name", SquadTemplateKey = "squad template";
    /// <summary>
    /// The level state's key of the template of the building's residents (the original saves it twice, also as <see cref="SquadTemplateKey"/>).
    /// </summary>
    public const string ResidentsTemplateKey = "residents template";

    /// <summary>
    /// Sets the level-data state's <c>destroyed</c> of each building its residents ruin and names each home's layouts as its residents' template
    /// does; returns how many it ruined. A second call on the same levels does nothing.
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
                if (!buildings.TryGetValue(placement.InstanceId, out var list)
                    || db.Find(placement.TownId) is not { Type: Fcs.FcsRecordType.Town } record)
                    continue;
                var town = TownData.From(record);
                var faction = town.Faction is { } id && db.Find(id) is { Type: Fcs.FcsRecordType.Faction } f ? FactionData.From(f) : null;
                var homes = new HashSet<TownBuilding>(ReferenceEqualityComparer.Instance);
                foreach (var (b, t) in ResidentHomes.Assign(db, town, faction, list, player))
                {
                    if (t.BuildingRuined && Ruin(db, levels, b)) ruined++;
                    LayOut(levels, b, t);
                    homes.Add(b);
                }

                foreach (var b in list)
                {
                    if (!homes.Contains(b)) Vacate(b);
                }
            }
            return ruined;
        }
    }

    /// <summary>
    /// Destroys <paramref name="b"/>'s level state when it is standing and its record can be destroyed; whether it did.
    /// </summary>
    static bool Ruin(GameDatabase db, WorldLevelData levels, TownBuilding b)
    {
        if (b.Destroyed || b.LevelState is not { } state || db.Find(b.BuildingId) is not { } record || !CanBeDestroyed(db, record)) return false;
        state.BoolTable["destroyed"] = true;
        levels.ResidentRuined.Add(b.Key);
        return true;
    }

    /// <summary>
    /// A town building nobody is housed in carries no template, whatever its level state names.
    /// </summary>
    static void Vacate(TownBuilding b)
    {
        if (b.LevelState is not { } state) return;

        if (state.GetString(ResidentsTemplateKey).Length > 0) state.StringTable[ResidentsTemplateKey] = "";
        if (state.GetString(SquadTemplateKey).Length > 0) state.StringTable[SquadTemplateKey] = "";
    }

    /// <summary>
    /// Names the template's <c>layout interior</c> and <c>layout exterior</c> on its home whatever the level state said, an empty one clearing it,
    /// ruins too (the original's <c>FUN_140552140</c>; a name the building has no layout for places nothing, as in the original). Nothing reads a
    /// ruin's layouts. The home is named as housing names it (<see cref="DisplayName"/>) and its state names the template, as the original's saved
    /// state does.
    /// </summary>
    static void LayOut(WorldLevelData levels, TownBuilding b, SquadTemplate t)
    {
        if (b.LevelState is not { } state) return;
        var level = (state.GetString(InteriorKey), state.GetString(ExteriorKey));
        if (level != (t.LayoutInterior, t.LayoutExterior)) levels.LevelLayoutNames.TryAdd(b.Key, level);
        foreach (var (key, layout) in Names(t)) state.StringTable[key] = layout;

        // The level's own naming has no template: the original never reads the level state's.
        var levelNaming = BuildingNaming.Of(state) with { SquadTemplate = "" };
        var naming = Naming(t, levelNaming);
        levels.LevelNames.TryAdd(b.Key, levelNaming);
        state.StringTable[NameKey] = naming.Name;
        state.StringTable[SquadTemplateKey] = naming.SquadTemplate;
        state.StringTable[ResidentsTemplateKey] = t.Id;
    }

    /// <summary>
    /// The name and squad template housing <paramref name="t"/> gives a home named <paramref name="own"/> before (<see cref="DisplayName"/>).
    /// </summary>
    public static BuildingNaming Naming(SquadTemplate t, BuildingNaming own) => new(DisplayName(t) ?? own.Name, t.Id);

    /// <summary>
    /// The name housing <paramref name="t"/> gives its home (docs/game/factions-squads-towns.md "Town residents", Layouts): its <c>building name</c>,
    /// else its non-empty <c>layout interior</c>; null keeps the building's own.
    /// </summary>
    public static string? DisplayName(SquadTemplate t) =>
        t.BuildingName.Length > 0 ? t.BuildingName : t.LayoutInterior.Length > 0 ? t.LayoutInterior : null;

    /// <summary>
    /// The level-state keys housing <paramref name="t"/> names, with the layout names it gives them (<see cref="LayOut"/>).
    /// </summary>
    public static IEnumerable<(string Key, string Layout)> Names(SquadTemplate t) =>
        [(InteriorKey, t.LayoutInterior), (ExteriorKey, t.LayoutExterior)];

    /// <summary>
    /// The templates housing <paramref name="town"/>'s residents afresh in <paramref name="buildings"/> puts in its homes (a town override, which
    /// names them as <see cref="LayOut"/> does), by building key; buildings it houses nobody in are not listed. <paramref name="playerOwned"/> as
    /// for <see cref="RuinedHomes"/>.
    /// </summary>
    public static Dictionary<string, SquadTemplate> HomeTemplates(GameDatabase db, TownData town, FactionData? faction,
        IReadOnlyList<TownBuilding> buildings, string? playerFactionId, IReadOnlySet<string>? playerOwned = null)
    {
        var result = new Dictionary<string, SquadTemplate>(StringComparer.Ordinal);
        foreach (var (b, t) in ResidentHomes.Assign(db, town, faction, buildings, playerFactionId, playerOwned)) result[b.Key] = t;
        return result;
    }

    /// <summary>
    /// The keys of the homes <paramref name="town"/>'s residents ruin when they are housed in <paramref name="buildings"/> afresh (a town override):
    /// homes of a ruined template that can be destroyed. <paramref name="playerOwned"/>: the buildings the player owns now, which house
    /// nobody (<see cref="ResidentHomes.Assign"/>).
    /// </summary>
    public static HashSet<string> RuinedHomes(GameDatabase db, TownData town, FactionData? faction, IReadOnlyList<TownBuilding> buildings,
        string? playerFactionId, IReadOnlySet<string>? playerOwned = null)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (b, t) in ResidentHomes.Assign(db, town, faction, buildings, playerFactionId, playerOwned))
            if (t.BuildingRuined && db.Find(b.BuildingId) is { } record && CanBeDestroyed(db, record)) result.Add(b.Key);
        return result;
    }

    /// <summary>
    /// Whether a building can be ruined (<c>FUN_14055b520</c>, Verified decompile): it or a part reached through <c>parts</c> (followed to any depth)
    /// has a <c>destroyed mesh</c>. The original also says yes when the building's class (+0x358, +0x198) is 7, GatewayBuilding, the building the
    /// factory makes for an `is wall gate` record (**Verified**, class table in docs/game/pathfinding.md): a gate can be ruined.
    /// </summary>
    public static bool CanBeDestroyed(GameDatabase db, GameRecord building)
    {
        if (building.GetBool("is wall gate")) return true;
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

/// <summary>
/// A building's display <c>name</c> and its residents' <c>squad template</c> (docs/game/factions-squads-towns.md "Town residents", Layouts).
/// </summary>
public readonly record struct BuildingNaming(string Name, string SquadTemplate)
{
    /// <summary>
    /// What <paramref name="state"/>, a GAMESTATE_BUILDING, names.
    /// </summary>
    public static BuildingNaming Of(GameRecord? state) =>
        new(state?.GetString(ResidentRuins.NameKey) ?? "", state?.GetString(ResidentRuins.SquadTemplateKey) ?? "");
}