using Meitou.Simulation.Items;
using Meitou.Simulation.Bodies;
using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.World;

namespace Meitou.Simulation;

public interface IAppearanceSource
{
    /// <summary>The look of a CHARACTER record, rolled from <paramref name="seed"/>; <paramref name="factionId"/> limits hair and beards.</summary>
    CharacterAppearance? Create(string recordId, string? factionId, int seed);
}

/// <summary><see cref="IAppearanceSource"/> over the game data and the install.</summary>
public sealed class GeneratedAppearances(GameDatabase db, string installRoot) : IAppearanceSource
{
    public CharacterAppearance? Create(string recordId, string? factionId, int seed)
    {
        try
        {
            return CharacterAppearance.Build(db, installRoot, recordId, new CharacterOptions { Seed = seed, Faction = factionId });
        }
        catch (Exception e) when (e is IOException or FcsFormatException or KeyNotFoundException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>A town placed in the world.</summary>
public sealed record TownSite(int Index, TownData Town, Vector3 Position, ZoneCoordinate Zone);

/// <summary>What the population needs to know about the game: its data, the placed towns, the factions.</summary>
public sealed class PopulationData
{
    public required GameDatabase Db { get; init; }
    public required IReadOnlyList<TownSite> Sites { get; init; }
    public required IReadOnlyList<FactionData> Factions { get; init; }
    /// <summary>Rolls the characters' looks; null leaves them without (headless tests, markers only).</summary>
    public IAppearanceSource? Appearances { get; init; }
    public IWorldStates States { get; init; } = IWorldStates.None;
    /// <summary>The player's faction (the FACTION named Nameless, 204-gamedata.base), a position in <see cref="Factions"/>; -1 when there is none.</summary>
    public int PlayerFaction { get; init; } = -1;
    /// <summary>The relations the factions start with (who is hostile to whom: roaming squads avoid hostile towns).</summary>
    public FactionRelations Relations { get; init; } = FactionRelations.Build([]);
    /// <summary>The faction of every placed town, a position in <see cref="Factions"/> (-1: none).</summary>
    public IReadOnlyList<int> SiteFactions { get; init; } = [];
    /// <summary>The new-game options that change the body mechanics (hunger time, chance of death...).</summary>
    public BodyOptions BodyOptions { get; init; } = BodyOptions.Default;
    Dictionary<string, int>? factionIndex;

    /// <summary>The position of a faction id in <see cref="Factions"/> (the first when an id repeats), or -1. The index is built once.</summary>
    public int FactionIndex(string id)
    {
        var index = LazyInitializer.EnsureInitialized(ref factionIndex, () => BuildFactionIndex(Factions));
        return index.GetValueOrDefault(id, -1);
    }

    static Dictionary<string, int> BuildFactionIndex(IReadOnlyList<FactionData> factions)
    {
        var index = new Dictionary<string, int>(factions.Count, StringComparer.Ordinal);
        for (int i = 0; i < factions.Count; i++) index.TryAdd(factions[i].Id, i);
        return index;
    }

    BodyFactory? bodies;
    /// <summary>Makes the stats and medical states of the characters spawned (race data cached).</summary>
    public BodyFactory Bodies => LazyInitializer.EnsureInitialized(ref bodies, () => new BodyFactory(Db) { Options = BodyOptions });
    Meitou.Data.Gameplay.Combat.CombatDatabase? combat;
    /// <summary>The weapon, armour and technique data of combat.</summary>
    public Meitou.Data.Gameplay.Combat.CombatDatabase Combat => LazyInitializer.EnsureInitialized(ref combat, () => Meitou.Data.Gameplay.Combat.CombatDatabase.From(Db));
    ItemFactory? items;
    /// <summary>Makes item instances and the starting inventories (loadout and the CHARACTER's <c>inventory</c> list).</summary>
    public ItemFactory Items => LazyInitializer.EnsureInitialized(ref items, () => new ItemFactory(Db, Bodies.Constants));

    public static PopulationData Create(GameDatabase db, IEnumerable<TownPlacement> placements, IAppearanceSource? appearances = null, BodyOptions? bodyOptions = null)
    {
        var sites = new List<TownSite>();
        foreach (var p in placements)
        {
            if (db.Find(p.TownId) is not { Type: FcsRecordType.TOWN } record) continue;
            sites.Add(new TownSite(sites.Count, TownData.From(record), p.Position, WorldLayout.ZoneOf(p.Position.X, p.Position.Z)));
        }
        var factions = FactionData.LoadAll(db);
        var byId = BuildFactionIndex(factions);
        return new PopulationData
        {
            Db = db, Sites = sites, Factions = factions, Appearances = appearances, BodyOptions = bodyOptions ?? BodyOptions.Default, PlayerFaction = factions.FindIndex(f => f.Name == "Nameless"),
            Relations = FactionRelations.Build(factions),
            SiteFactions = [.. sites.Select(s => s.Town.Faction is { } fid ? byId.GetValueOrDefault(fid, -1) : -1)],
        };
    }
}

public sealed class PopulationSettings
{
    /// <summary>The <c>Squad size multiplier</c> setting (factions-squads-towns.md section 9).</summary>
    public float SquadSizeMultiplier { get; init; } = 1;
    /// <summary>The <c>Fast zone hopping</c> setting: a ring of one more zone round the activated ones (game-loop.md "Zones", Verified).</summary>
    public bool FastZoneHopping { get; init; }
    /// <summary>
    /// Game seconds a zone stays loaded after nothing asks for it. The original has three real-time countdowns whose lengths are
    /// Unknown (game-loop.md); this is an engine choice, long enough that walking back and forth over a zone edge never reloads.
    /// </summary>
    public float UnloadGraceSeconds { get; init; } = 60;
    /// <summary>Ticks between looks at the zones (engine choice: twice a game second).</summary>
    public int CheckEveryTicks { get; init; } = 15;
    /// <summary>Build the towns' characters on a background thread (the game) or inside the tick (tests).</summary>
    public bool Background { get; init; }
    /// <summary>Share of the town radius a resident squad may be placed within (engine choice: home buildings are Unknown).</summary>
    public float PlacementRadiusShare { get; init; } = 0.6f;

    // ---- roaming squads (6.4, 6.3) ----
    /// <summary>Ticks between looks at the roaming pools (engine choice: 2 game seconds, the original's area tick).</summary>
    public int RoamCheckTicks { get; init; } = 60;
    /// <summary>Share of a faction's <c>roaming population</c> a town's roaming pool may hold (0.7 is the nests' floor, 6.3; for towns the cap is <b>Unknown</b>).</summary>
    public float RoamingShare { get; init; } = 0.7f;
    /// <summary>The <c>Global population multiplier</c> setting.</summary>
    public float PopulationMultiplier { get; init; } = 1;
    /// <summary>Game seconds a loaded roaming squad stays loaded after its zone stopped being active (the original's 4 to 5 s countdown, 6.x of game-loop.md; engine choice 10).</summary>
    public float RoamUnloadGraceSeconds { get; init; } = 10;
    /// <summary>A roaming squad has reached a town within this distance of its centre.</summary>
    public float RoamArrivalRadius { get; init; } = 60;
    /// <summary>Seconds a roaming squad stays at a town it reached (engine choice).</summary>
    public float RoamWaitMin { get; init; } = 30;
    public float RoamWaitMax { get; init; } = 120;
    /// <summary>Stand-in speeds of an unloaded squad in units per second: 25, or 90 when the way exceeds 25000 (game-loop.md; units not verified).</summary>
    public float StandInSpeed { get; init; } = 25;
    public float StandInFarSpeed { get; init; } = 90;
    public float StandInFarDistance { get; init; } = 25000;
}

/// <summary>Where a town's residents are in their life.</summary>
public enum SiteStatus { Unloaded, Loading, Loaded }
