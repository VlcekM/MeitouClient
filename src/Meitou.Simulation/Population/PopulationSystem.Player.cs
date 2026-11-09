using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Population;

public sealed partial class PopulationSystem
{
    /// <summary>
    /// The squad a new game starts with when its start-off makes nobody: the SQUAD_TEMPLATE "startoff- Wanderer squad", whose id is a constant of the executable.
    /// </summary>
    public const string WandererSquadId = "45550-gamedata.base";

    /// <summary>
    /// Where a new game of <paramref name="start"/> with <paramref name="seed"/> stands its squad (docs/game/ui-screens.md "New game"):
    /// <see cref="NewGameStart.ForceStartPos"/>'s place, else a placement drawn among the start's towns, else among the fallback towns, else (-43500, -6625);
    /// <c>Town</c> is the drawn placement. <c>Missing</c>: the start lists towns and none of them is placed. The host loads the world round it before
    /// <see cref="StartPlayer"/>, which draws the same.
    /// </summary>
    public static (Vector2 Centre, TownSite? Town, bool Missing) StartPlace(PopulationData data, ulong seed, NewGameStart start)
    {
        if (start.ForceStartPos) return (start.StartPosition, null, false);

        var candidates = data.Sites.Where(site => start.Towns.Any(t => t.Id == site.Town.Id)).ToList();
        bool missing = candidates.Count == 0 && start.Towns.Count > 0;
        if (candidates.Count == 0 && data.PlayerFaction >= 0)
        {
            // The fallback reads the relations the start makes, from a table of its own: only StartPlayer changes the world's.
            var relations = FactionRelations.Build(data.Factions);
            relations.PlayerFaction = data.PlayerFaction;
            ApplyStartRelations(relations, start);
            candidates = [.. data.Sites.Where(site => site.Town.Type is TownType.Town or TownType.Village && site.Index < data.SiteFactions.Count
                && data.SiteFactions[site.Index] is var f and >= 0 && !data.Factions[f].NotReal
                && data.Factions[f].FundamentalType != FundamentalType.Bandit && relations.Get(f, data.PlayerFaction) > FactionRelations.HostileThreshold)];
        }

        if (candidates.Count == 0) return (new Vector2(-43500, -6625), null, missing);

        ulong key = Rng.Mix(SeedSalts.PlayerStart ^ Rng.StableHash(start.Id));
        var town = candidates[(int)(Rng.Hash(seed, key, RngPurpose.Spawn, SeedSalts.PlayerStartTown) % (ulong)candidates.Count)];
        return (new Vector2(town.Position.X, town.Position.Z), town, missing);
    }

    /// <summary>
    /// A new game's relations: back to the FACTION records' values, as the original builds every faction anew for a new game; then each entry of the start-off's
    /// <c>faction relations</c> sets that faction's relation with the player to its v0.
    /// </summary>
    static void ApplyStartRelations(FactionRelations relations, NewGameStart start)
    {
        relations.Reset();
        foreach (var link in start.FactionRelations)
        {
            int other = relations.IndexOf(link.Id);
            if (other >= 0) relations.Add(other, relations.PlayerFaction, link.V0 - relations.Get(other, relations.PlayerFaction));
        }
    }

    /// <summary>
    /// Starts the player (a new game, before the first tick) as docs/game/ui-screens.md "New game" describes: their faction, their money and their squad, made
    /// from the start-off's <c>squad</c> list (a SQUAD_TEMPLATE is planned by the squad factory, a CHARACTER makes v0 members) as one squad of the player's
    /// faction, standing idle, selected for them, at <see cref="NewGameStart.ForceStartPos"/>'s place or a town drawn from the start's list
    /// (<see cref="StartPlace"/>). The start research is applied (<see cref="Meitou.Data.Gameplay.Construction.ResearchGate.StartResearch"/>) and the start-off's
    /// <c>faction relations</c>, whose factions are known with <see cref="PopulationData.KnownAtNewGame"/>'s; <c>force race</c> is kept as the races the
    /// character creation offers (<see cref="PlayerState.CreationRaces"/>). The characters' items are what the generator rolls for their look; their inventory is
    /// built like everybody's (<see cref="CharacterAssembly"/>).
    /// </summary>
    public Squad StartPlayer(World world, NewGameStart start)
    {
        if (_data.PlayerFaction < 0) throw new InvalidOperationException($"the game data has no player faction (FACTION {FactionData.PlayerId})");

        ulong key = Rng.Mix(SeedSalts.PlayerStart ^ Rng.StableHash(start.Id));
        ApplyStartRelations(_data.Relations, start);
        _data.ClearReplacements();
        var (centre, town, missing) = StartPlace(_data, world.Seed, start);
        if (missing) world.Post(Loc.Tr("Cannot find any towns from the game start town list."));

        var at = StandingPlace(world, centre, key);
        DiscoverBiomeTowns(world, centre);
        var squad = SpawnStartSquad(world, start, key, at, town);
        SetStartSlaves(world, squad, town);

        // The first player squad reads like its platoon, the faction's name and 0 ("Nameless_0", docs/game/ui-screens.md "Squad tabs").
        squad.Name = $"{(_data.Factions[_data.PlayerFaction].Name is { Length: > 0 } f ? f : "Nameless")}_0";
        SetStartPlayer(world, start, squad);
        return squad;
    }

    /// <summary>
    /// The place a new game's squad stands at: the place itself when walkable, else a walkable spot near it (the original stands the squad at the place itself).
    /// </summary>
    Vector2 StandingPlace(World world, Vector2 centre, ulong key)
    {
        // The original stands the squad at the place itself; here on a walkable spot near it.
        const float spread = 50;
        var at = centre;
        if (world.Walkability.IsWalkable(centre.X, centre.Y)) return at;

        for (int attempt = 0; attempt < 20; attempt++)
        {
            var p = Rng.PointInDisc(Rng.Hash(world.Seed, key, RngPurpose.Spawn, (ulong)attempt), centre, spread * (1 + attempt));
            if (world.Walkability.IsWalkable(p.X, p.Y))
            {
                at = p;
                break;
            }
        }

        return at;
    }

    /// <summary>
    /// The towns of the biome group the start place is in are known to the player.
    /// </summary>
    void DiscoverBiomeTowns(World world, Vector2 centre)
    {
        if (_data.BiomeGroupAt(centre) is not { } group) return;

        foreach (var site in _data.Sites)
        {
            if (site.Town.Type == TownType.Town && _data.BiomeGroupAt(new Vector2(site.Position.X, site.Position.Z)) == group)
                world.Player.Know(site.Index, TownKnowledge.Discovered);
        }
    }

    /// <summary>
    /// Makes the start squad: one member per planned entry (the squad templates' members, then the CHARACTER entries), each with its rolled look.
    /// </summary>
    Squad SpawnStartSquad(World world, NewGameStart start, ulong key, Vector2 at, TownSite? town)
    {
        var members = new List<BuiltMember>();
        var first = new List<MemberPlan>();
        var fromTemplate = new HashSet<int>();   // the members the squad templates made: they go through the unique gate, the CHARACTER entries do not (FUN_140583a10)
        SquadTemplate? template = null;
        void AddTemplate(GameRecord record)
        {
            var t = SquadTemplate.From(record);
            template ??= t;
            int before = first.Count;
            first.AddRange(SquadFactory.Plan(_data.Db, t, _settings.SquadSizeMultiplier, world.Seed, Rng.Mix(key ^ (uint)first.Count), States).Members);
            for (int i = before; i < first.Count; i++) fromTemplate.Add(i);
        }

        // A CHARACTER entry's characters stand up to 50 from the place on x and z, as the original's do.
        Vector2 Scatter(int i) => new(
            Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Spawn, SeedSalts.PlayerScatter + 2 * (ulong)i)) * 100 - 50,
            Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Spawn, SeedSalts.PlayerScatter + 2 * (ulong)i + 1)) * 100 - 50);

        foreach (var link in start.Squad)
        {
            var record = _data.Db.Find(link.Id);
            if (record is null) continue;

            if (SquadFactory.IsTemplate(record))
            {
                AddTemplate(record);
            }
            else
            {
                for (int n = 0; n < Math.Max(1, link.V0); n++)
                    first.Add(new MemberPlan(link.Id, SquadRole.Squad1, first.Count, Scatter(first.Count), false, 1));
            }
        }

        if (first.Count == 0 && _data.Db.Find(WandererSquadId) is { } wanderer && SquadFactory.IsTemplate(wanderer)) AddTemplate(wanderer);

        // Renumber in creation order and make the first the leader when none is; each template keeps its own layout (in the original, a squad of its own).
        bool hasLeader = first.Any(m => m.Role == SquadRole.Leader);
        for (int i = 0; i < first.Count; i++)
            first[i] = first[i] with { Index = i, Role = !hasLeader && i == 0 ? SquadRole.Leader : first[i].Role };

        var factionId = _data.Factions[_data.PlayerFaction].Id;
        foreach (var m in first)
        {
            CharacterAppearance? look = null;
            if (_data.Appearances is { } source)   // an animal gets its species' shared look (GeneratedAppearances)
                look = source.Create(m.RecordId, factionId, (int)(Rng.Hash(world.Seed, key, RngPurpose.Spawn, SeedSalts.PlayerLook + (ulong)m.Index) & 0x7FFFFFFF));
            members.Add(new BuiltMember(m, look));
        }

        var plan = new SquadPlan(template ?? new SquadTemplate { Id = start.Id, Name = start.Name }, first, [], []);
        var built = new BuiltSquad(plan, at, _data.PlayerFaction, members);
        float radius = town is null ? 100 : town.Town.SizeRadius * town.Town.TownRadiusMult;
        return SpawnSquad(world, built, null, radius, player: true, gated: fromTemplate);
    }

    /// <summary>
    /// A start slave (CHARACTER <c>slave</c>, set for every new character in SpawnSquad) belongs to the start town's faction (engine choice: the original's owner
    /// is Unknown); an escaped one is stamped now (lie low, FixtureRegistry.LayLow); an ex-slave start is left free.
    /// </summary>
    void SetStartSlaves(World world, Squad squad, TownSite? town)
    {
        int owner = town is not null && town.Index < _data.SiteFactions.Count ? _data.SiteFactions[town.Index] : -1;
        if (owner < 0) return;

        foreach (var id in squad.Members)
        {
            if (world.Characters.Cold(id.Slot) is { SlaveState: not 0 } c)
            {
                c.SlaveOwner = owner;
                if (c.SlaveState == 2) c.SlaveSince = world.GameHours;
            }
        }
    }

    /// <summary>
    /// Sets the player's faction, money, research, known factions and creation state for a new game with its start squad.
    /// </summary>
    void SetStartPlayer(World world, NewGameStart start, Squad squad)
    {
        world.Player.Faction = _data.PlayerFaction;
        var known = _data.KnownAtNewGame();
        known.UnionWith(start.FactionRelations.Select(link => _data.FactionIndex(link.Id)).Where(f => f >= 0));
        world.Player.KnownFactions = known;
        world.Player.Money = start.Money;
        world.Research.Set(_data.Db, Meitou.Data.Gameplay.Construction.ResearchGate.StartResearch(_data.Db, start));
        world.Player.Squad = squad.Id;
        world.Player.CreationRaces = [.. start.ForceRace.Select(r => r.Id)];
        world.Player.InCreation = true;   // the character editor may remake them until it is confirmed (docs/characters.md "Character editor")
        world.Player.Selection.Clear();
        if (squad.Members.Count > 0) world.Player.Selection.Add(squad.Members[0]);
    }
}
