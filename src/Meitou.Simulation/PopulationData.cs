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

    /// <summary>The look of a CHARACTER record drawn from a given loadout (a saved character's appearance and worn items); nothing is rolled. Null: no look.</summary>
    CharacterAppearance? Create(string recordId, Loadout loadout) => null;

    /// <summary>A random name for a <c>named</c> CHARACTER of the sex (<see cref="NamePool"/>); null: none (no lists).</summary>
    string? RandomName(bool female, ulong roll) => null;

    /// <summary>A proposed CHARACTER_APPEARANCE made valid for <paramref name="race"/> (<see cref="CharacterDraft.Sanitize"/>, which needs the install's editor limits). Null: not checked.</summary>
    FcsRecord? Sanitize(FcsRecord proposed, GameRecord race) => null;
}

/// <summary><see cref="IAppearanceSource"/> over the game data and the install.</summary>
public sealed class GeneratedAppearances(GameDatabase db, string installRoot) : IAppearanceSource
{
    // One look per ANIMAL_CHARACTER (nothing of an animal's is rolled): every animal of a record shares it, so the renderer builds one asset per species.
    readonly ConcurrentDictionary<string, CharacterAppearance?> animals = new(StringComparer.Ordinal);

    public CharacterAppearance? Create(string recordId, string? factionId, int seed)
    {
        try
        {
            if (db.Find(recordId) is { Type: FcsRecordType.ANIMAL_CHARACTER } animal)
                return animals.GetOrAdd(recordId, static (_, s) =>
                {
                    try { return CharacterAppearance.BuildAnimal(s.db, s.installRoot, s.animal); }
                    catch (KeyNotFoundException) { return null; }
                }, (db, installRoot, animal));
            return CharacterAppearance.Build(db, installRoot, recordId, new CharacterOptions { Seed = seed, Faction = factionId });
        }
        catch (Exception e) when (e is IOException or FcsFormatException or KeyNotFoundException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }


    public FcsRecord? Sanitize(FcsRecord proposed, GameRecord race) => CharacterDraft.Sanitize(db, installRoot, proposed, race);

    readonly Lazy<NamePool> names = new(() => NamePool.Load(installRoot));
    public string? RandomName(bool female, ulong roll) => names.Value.Pick(female, roll);
    public CharacterAppearance? Create(string recordId, Loadout loadout)
    {
        try
        {
            if (db.Find(recordId) is { Type: FcsRecordType.ANIMAL_CHARACTER }) return Create(recordId, null, loadout.Seed);
            return CharacterAppearance.Build(db, installRoot, recordId, new CharacterOptions { Loadout = loadout });
        }
        catch (Exception e) when (e is IOException or FcsFormatException or KeyNotFoundException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>A town placed in the world.</summary>
public sealed record TownSite(int Index, TownData Town, Vector3 Position, ZoneCoordinate Zone)
{
    /// <summary>The placement's instance id in the level data (<c>9-rebirth-INGAME</c>), which the save's town state names.</summary>
    public string InstanceId { get; init; } = "";
    /// <summary>The town's placed buildings with their beds and nodes (<see cref="TownBuildings"/>), the AI's homes; empty when the host gave none.</summary>
    public IReadOnlyList<TownBuilding> Buildings { get; init; } = [];
    /// <summary>The zones the town's level state lists, as the original files them at load, which discovery counts as well as <see cref="Zone"/> (docs/game/ui-screens.md "Map"); empty for a Nest and when the host gave none.</summary>
    public IReadOnlyList<ZoneCoordinate> Coverage { get; init; } = [];
}

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
    /// <summary>
    /// The live relation table of the game (who is hostile to whom: roaming squads avoid hostile towns, <see cref="AggroSystem"/> starts fights by it);
    /// it starts as the data's (section 3.2), a loaded save replaces its values, and the player's deeds change it (<see cref="Combat.CombatSystem"/>).
    /// Worlds built on the same data share it.
    /// </summary>
    public FactionRelations Relations { get; init; } = FactionRelations.Build([]);
    /// <summary>The faction of every placed town, a position in <see cref="Factions"/> (-1: none).</summary>
    public IReadOnlyList<int> SiteFactions { get; init; } = [];

    /// <summary>The nearest placed town whose radius holds a point (the game's position lookup, <c>FUN_140928890</c>), -1 when none.</summary>
    public int TownAt(Vector3 p)
    {
        int best = -1;
        float bestD = float.MaxValue;
        for (int s = 0; s < Sites.Count; s++)
        {
            float r = Sites[s].Town.Radius, d = Vector2.DistanceSquared(new(p.X, p.Z), new(Sites[s].Position.X, Sites[s].Position.Z));
            if (d <= r * r && d < bestD) (best, bestD) = (s, d);
        }
        return best;
    }
    /// <summary>The area sectors and their BIOME_GROUPs (6.2, 6.3): when set, zones get their homeless squads and the biome groups their nests; null: no wild population (the worlds of the pinned tests).</summary>
    public BiomeMap? Biomes { get; init; }
    /// <summary>The world's mineable rocks (ore mines' yield); null: none known, so ore mines yield nothing.</summary>
    public Meitou.Data.Gameplay.Construction.IResourceNodes? ResourceNodes { get; set; }
    /// <summary>The town buildings' rooms and lamps for the sight tests (<see cref="SightMap"/>); null: everyone outdoors and unlit.</summary>
    public ISightSource? Sight { get; set; }
    /// <summary>The road network squads travel by, off screen here and on screen through the walkability's long routes (null: none, they go straight).</summary>
    public RoadNetwork? Roads { get; init; }
    /// <summary>The new-game options that change the body mechanics (hunger time, chance of death...).</summary>
    public BodyOptions BodyOptions { get => bodyOptions; init => bodyOptions = value; }
    BodyOptions bodyOptions = BodyOptions.Default;
    /// <summary>
    /// A new world's body options without changing the data retained by the running world's systems: a shallow copy sharing every other member and cache,
    /// with only the body factory built again; the same data when the options are unchanged.
    /// </summary>
    public PopulationData WithBodyOptions(BodyOptions options)
    {
        if (options == bodyOptions) return this;
        var copy = (PopulationData)MemberwiseClone();
        copy.bodyOptions = options;
        copy.bodies = null;
        return copy;
    }
    Dictionary<string, int>? factionIndex;

    readonly Dictionary<int, IReadOnlyList<TownBuilding>> turrets = [];
    /// <summary>
    /// The turret buildings of the site (<see cref="TownBuilding.Turret"/>), in placement order; empty for no site, no building data or none. Cached: the AI asks the
    /// package cadence of every town squad.
    /// </summary>
    public IReadOnlyList<TownBuilding> Turrets(int site)
    {
        lock (turrets)
        {
            if (turrets.TryGetValue(site, out var known)) return known;
            IReadOnlyList<TownBuilding> list = site >= 0 && site < Sites.Count ? [.. Sites[site].Buildings.Where(b => b.Turret is not null)] : [];
            turrets[site] = list;
            return list;
        }
    }


    /// <summary>The BIOME_GROUP id at a point (null without a biome map or outside every group).</summary>
    public string? BiomeGroupAt(Vector2 at)
    {
        if (Biomes is not { } biomes) return null;
        var (cx, cz) = WeatherAreas.CellOf(at.X, at.Y);
        int g = biomes.GroupOf(cx, cz);
        return g >= 0 ? biomes.Groups[g].Id : null;
    }

    /// <summary>
    /// The trader's next town (the town chooser <c>FUN_14092c5e0</c> as <c>Package_WanderingTrader</c> calls it, docs/game/ai.md "Package classes", Verified by code): towns other than
    /// <paramref name="exclude"/> of type town, village, slave camp or military (mask 0x6c) or any player town (<paramref name="outposts"/>), public or of <paramref name="faction"/>,
    /// not hostile to it and not in its <c>no-go zones</c>; the 4 nearest to <paramref name="at"/> (X/Z), one drawn uniformly by <paramref name="roll"/>. None when there is none.
    /// Placed towns come before the outposts in a tie (engine choice: the original's order over its two lists is <b>Unknown</b>). Unread here: the town faction's flags +0x1d0 and
    /// type +0x34 (8 only for 8) and a faction test <c>FUN_1406b26d0</c> (<b>Unknown</b>).
    /// </summary>
    public CampaignTown TraderTown(int faction, Vector2 at, CampaignTown exclude, IReadOnlyList<Construction.PlayerTown> outposts, ulong roll)
    {
        var fd = faction >= 0 && faction < Factions.Count ? Factions[faction] : null;
        Span<(float D, CampaignTown Town)> best = stackalloc (float, CampaignTown)[4];
        int n = 0;
        bool Eligible(Vector2 centre, int theirs, bool open) =>
            (open || theirs == faction) && !(faction >= 0 && theirs >= 0 && Relations.IsHostile(faction, theirs))
            && !(fd is { NoGoZones.Count: > 0 } && BiomeGroupAt(centre) is { } group && fd.NoGoZones.Any(z => z.Id == group));
        static void Offer(Span<(float D, CampaignTown Town)> best, ref int n, float d, CampaignTown town)
        {
            // Insertion into the 4 nearest (a tie keeps the first, as the original's map keeps the first of equal keys).
            int k = n < 4 ? n++ : 4;
            while (k > 0 && best[k - 1].D > d) { if (k < 4) best[k] = best[k - 1]; k--; }
            if (k < 4) best[k] = (d, town);
        }
        foreach (var site in Sites)
        {
            if (site.Index == exclude.Site || site.Town.Type is not (TownType.Town or TownType.Village or TownType.SlaveCamp or TownType.Military)) continue;
            var centre = new Vector2(site.Position.X, site.Position.Z);
            if (Eligible(centre, site.Index < SiteFactions.Count ? SiteFactions[site.Index] : -1, site.Town.IsPublic)) Offer(best, ref n, Vector2.DistanceSquared(at, centre), CampaignTown.OfSite(site.Index));
        }
        foreach (var town in outposts)
        {
            var centre = new Vector2(town.Position.X, town.Position.Z);
            if (town.Number != exclude.PlayerTown && Eligible(centre, town.Owner, town.Public)) Offer(best, ref n, Vector2.DistanceSquared(at, centre), CampaignTown.OfPlayer(town.Number));
        }
        return n == 0 ? CampaignTown.None : best[Rng.Int(roll, n)].Town;
    }

    /// <summary>FACTION <c>road preference</c> of a position in <see cref="Factions"/>; 0 (never the roads) for none.</summary>
    public float RoadPreferenceOf(int faction) => faction >= 0 && faction < Factions.Count ? Factions[faction].RoadPreference : 0;

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
    public ItemFactory Items => LazyInitializer.EnsureInitialized(ref items, () => new ItemFactory(Db, Bodies.Constants, () => Combat));
    Meitou.Data.Gameplay.Ai.AiDatabase? ai;
    /// <summary>The AI_TASK and AI_PACKAGE records with their inheritance resolved (<see cref="Ai.AiSystem"/>).</summary>
    public Meitou.Data.Gameplay.Ai.AiDatabase Ai => LazyInitializer.EnsureInitialized(ref ai, () => new Meitou.Data.Gameplay.Ai.AiDatabase(Db));
    Meitou.Data.Characters.SurgeonDialogues? surgeons;
    /// <summary>The plastic surgeons' editor lines and costs (<see cref="Meitou.Data.Characters.SurgeonDialogues"/>).</summary>
    public Meitou.Data.Characters.SurgeonDialogues Surgeons => LazyInitializer.EnsureInitialized(ref surgeons, () => new Meitou.Data.Characters.SurgeonDialogues(Db));
    Meitou.Data.Gameplay.Dialogue.DialogueDatabase? dialogue;
    /// <summary>The dialogue trees, packages and word swaps (docs/game/dialogue.md).</summary>
    public Meitou.Data.Gameplay.Dialogue.DialogueDatabase Dialogue => LazyInitializer.EnsureInitialized(ref dialogue, () => new Meitou.Data.Gameplay.Dialogue.DialogueDatabase(Db));

    /// <param name="buildings">The towns' buildings by placement id (<see cref="TownBuildings.Build"/>), for the AI's homes; null: none.</param>
    /// <param name="townZones">The zones each town's level state lists, by placement id (<see cref="WorldLevelData.TownZones"/>), for discovery; null: none.</param>
    public static PopulationData Create(GameDatabase db, IEnumerable<TownPlacement> placements, IAppearanceSource? appearances = null, BodyOptions? bodyOptions = null, BiomeMap? biomes = null,
        IReadOnlyDictionary<string, List<TownBuilding>>? buildings = null, IEnumerable<RoadPath>? roads = null, IReadOnlyDictionary<string, List<ZoneCoordinate>>? townZones = null)
    {
        var sites = new List<TownSite>();
        foreach (var p in placements)
        {
            if (db.Find(p.TownId) is not { Type: FcsRecordType.TOWN } record) continue;
            var zone = WorldLayout.ZoneOf(p.Position.X, p.Position.Z);
            var town = TownData.From(record);
            sites.Add(new TownSite(sites.Count, town, p.Position, zone)
            {
                Buildings = buildings is not null && buildings.TryGetValue(p.InstanceId, out var list) ? list : [],
                InstanceId = p.InstanceId,
                Coverage = town.Type != TownType.Nest && townZones is not null && townZones.TryGetValue(p.InstanceId, out var listed) ? CoverageOf(zone, listed) : [],
            });
        }
        var factions = FactionData.LoadAll(db);
        var byId = BuildFactionIndex(factions);
        int player = factions.FindIndex(f => f.BaseName == "Nameless");
        var relations = FactionRelations.Build(factions);
        relations.PlayerFaction = player;
        return new PopulationData
        {
            Db = db, Sites = sites, Factions = factions, Appearances = appearances, Biomes = biomes, Roads = roads is null ? null : new RoadNetwork(roads), BodyOptions = bodyOptions ?? BodyOptions.Default, PlayerFaction = player,
            Relations = relations,
            SiteFactions = [.. sites.Select(s => s.Town.Faction is { } fid ? byId.GetValueOrDefault(fid, -1) : -1)],
        };
    }

    /// <summary>The listed zones a town keeps: the first 6 inside the grid, none when one is more than 2 zones from its own (docs/game/ui-screens.md "Map").</summary>
    static IReadOnlyList<ZoneCoordinate> CoverageOf(ZoneCoordinate own, List<ZoneCoordinate> listed)
    {
        var kept = new List<ZoneCoordinate>();
        foreach (var z in listed)
        {
            if (!z.IsInsideGrid) continue;
            if (Math.Abs(z.X - own.X) > 2 || Math.Abs(z.Y - own.Y) > 2) return [];
            kept.Add(z);
            if (kept.Count == 6) break;
        }
        return kept;
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
    /// <summary>Stand-in speeds of the engine's travel stand-in (job NONE) in units per second: 25, or 90 when the way exceeds 25000 (the original's TRAVEL_TARGET_FAST numbers), capped at <see cref="PopulationSystem.StandInSpeedCap"/>.</summary>
    public float StandInSpeed { get; init; } = 25;
    public float StandInFarSpeed { get; init; } = 90;
    public float StandInFarDistance { get; init; } = 25000;

    // ---- the off-screen tier's Faithful / Meitou switches (docs/simulation.md "Off-screen tier"; the live values are the population system's) ----
    /// <summary>Meitou: a roaming squad keeps its characters off screen. Faithful: it is despawned on leaving, and a stand-in after 60 s, as the original.</summary>
    public bool KeepRoamers { get; init; } = true;
    /// <summary>Meitou: hostile stand-ins that meet fight it out by strength. Faithful: nothing happens off screen.</summary>
    public bool OffscreenFights { get; init; } = true;
    /// <summary>Meitou: a squad coming back catches up its healing for the time away. Faithful: it comes back as it left.</summary>
    public bool OffscreenHealing { get; init; } = true;
    /// <summary>Meitou: a campaign's <c>inherits from</c> ancestors count once. Faithful: twice, as the original applies them (doubled squad weights and follow-up chances).</summary>
    public bool CampaignAncestorsOnce { get; init; } = true;
    /// <summary>Meitou: a wild site keeps its squads' characters while it is unloaded and makes the same ones again. Faithful: they are dropped (and a save's wild platoon despawned on leaving), as the original.</summary>
    public bool KeepWild { get; init; } = true;
    /// <summary>
    /// Meitou: a town's residents back from at least <see cref="PopulationSystem.ScheduleAfterHours"/> unloaded are where their schedule puts them now (in bed at night, at
    /// the counter, at their node or seat). Faithful: where they stood when the town unloaded, as the original, which runs nothing for them off screen.
    /// </summary>
    public bool ResidentSchedule { get; init; } = true;

    // ---- faction campaigns (docs/game/factions-squads-towns.md "Campaigns") ----
    /// <summary>The original's <c>raidFrequencyMult</c> (settings.cfg, "Town raid events: frequency" 0.25 to 3; default 1): times the roll chance.</summary>
    public float RaidFrequencyMult { get; init; } = 1;
    /// <summary>The original's <c>raidSizeMult</c> ("Town raid events: size" 0.25 to 4; default 1): times <c>num forces</c>.</summary>
    public float RaidSizeMult { get; init; } = 1;

    // ---- the wild: homeless squads and nests (6.2, 6.3) ----
    /// <summary>The new game's <i>Number of nests multiplier</i> (slider 0.5 to 4; its default is <b>Unknown</b>, 1 here).</summary>
    public float NestsMultiplier { get; init; } = 1;
    /// <summary>Radius a homeless squad wanders in round the spot it was made (engine choice: the original's AI packages are not decoded).</summary>
    public float HomelessHomeRadius { get; init; } = 300;
    /// <summary>Least distance of a homeless squad's spot from a player character (6.2, Observed: 1382.4 from the view position).</summary>
    public float HomelessPlayerDistance { get; init; } = 1382.4f;
    /// <summary>Least distance of a nest squad's spot from a player character (6.3: sqrt(1250) = 35.4 from the view point; 36 as the roaming pools use).</summary>
    public float NestPlayerDistance { get; init; } = 36;
    /// <summary>Share of a nest's <c>nest resident population</c> its roaming pool fills (6.3, Observed: floor(0.7 N)).</summary>
    public float NestShare { get; init; } = 0.7f;
}

/// <summary>Where a town's residents are in their life.</summary>
public enum SiteStatus { Unloaded, Loading, Loaded }
