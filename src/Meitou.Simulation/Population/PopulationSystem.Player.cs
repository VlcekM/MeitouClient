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

/// <summary>
/// The population's player squads: the squad a new game starts with.
/// </summary>
public sealed partial class PopulationSystem
{
    /// <summary>
    /// The squad a new game starts with when its start-off makes nobody: the SQUAD_TEMPLATE "startoff- Wanderer squad", whose id is a constant of
    /// the executable.
    /// </summary>
    public const string WandererSquadId = "45550-gamedata.base";

    /// <summary>
    /// Where a new game of <paramref name="start"/> with <paramref name="seed"/> stands its squad (docs/game/ui-screens.md "New game"):
    /// <see cref="NewGameStart.ForceStartPos"/>'s place, else a placement drawn among the start's towns, else among the fallback towns, else (-43500,
    /// -6625); <c>Town</c> is the drawn placement. <c>Missing</c>: the start lists towns and none of them is placed. The host loads the world round
    /// it before <see cref="StartPlayer"/>, which draws the same.
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
                && data.Factions[f].FundamentalType != FundamentalType.Bandit
                && relations.Get(f, data.PlayerFaction) > FactionRelations.HostileThreshold)];
        }

        if (candidates.Count == 0) return (new Vector2(-43500, -6625), null, missing);

        ulong key = Rng.Mix(SeedSalts.PlayerStart ^ Rng.StableHash(start.Id));
        var town = candidates[(int)(Rng.Hash(seed, key, RngPurpose.Spawn, SeedSalts.PlayerStartTown) % (ulong)candidates.Count)];
        return (new Vector2(town.Position.X, town.Position.Z), town, missing);
    }

    /// <summary>
    /// A new game's relations: back to the FACTION records' values, as the original builds every faction anew for a new game; then each entry of the
    /// start-off's <c>faction relations</c> sets that faction's relation with the player to its v0.
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
    /// Starts the player (a new game, before the first tick) as docs/game/ui-screens.md "New game" describes: their faction, their money and their
    /// squads, made from the start-off's <c>squad</c> list (the first SQUAD_TEMPLATE, planned by the squad factory, makes the first squad and each
    /// further one a squad of its own, a CHARACTER makes v0 members of the first squad; <see cref="SpawnStartSquads"/>) in the player's faction,
    /// standing idle, the first squad's first member selected, at <see cref="NewGameStart.ForceStartPos"/>'s place or a town drawn from the start's
    /// list (<see cref="StartPlace"/>). The start research is applied (<see cref="Meitou.Data.Gameplay.Construction.ResearchGate.StartResearch"/>)
    /// and the start-off's <c>faction relations</c>, whose factions are known with <see cref="PopulationData.KnownAtNewGame"/>'s; <c>force race</c>
    /// is kept as the races the character creation offers (<see cref="PlayerState.CreationRaces"/>). The characters' items are what the generator
    /// rolls for their look; their inventory is built like everybody's (<see cref="CharacterAssembly"/>). Returns the first squad.
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
        var squads = SpawnStartSquads(world, start, key, at, town);

        // A player squad reads like its platoon, the faction's name and its number ("Nameless_0", docs/game/ui-screens.md "Squad tabs").
        string faction = _data.Factions[_data.PlayerFaction].Name is { Length: > 0 } f ? f : "Nameless";
        for (int i = 0; i < squads.Count; i++)
        {
            SetStartSlaves(world, squads[i], town);
            squads[i].Name = $"{faction}_{i}";
            squads[i].Index = i;
        }

        SetStartPlayer(world, start, squads[0]);
        return squads[0];
    }

    /// <summary>
    /// The place a new game's squad stands at: the place itself when walkable, else a walkable spot near it (the original stands the squad at the
    /// place itself).
    /// </summary>
    static Vector2 StandingPlace(World world, Vector2 centre, ulong key) =>
        Movement.CharacterPlacement.WalkableNear(world, centre, key) ?? centre;   // the original stands the squad at the place itself

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
    /// One start squad being planned: its template (null for the CHARACTER entries' own squad), its members in creation order, each with its number
    /// over the whole start (which keys its look), and which members its template made (they go through the unique gate, the CHARACTER entries do
    /// not, FUN_140583a10).
    /// </summary>
    sealed record StartGroup(SquadTemplate? Template)
    {
        /// <summary>
        /// The members in creation order, with their numbers over the whole start.
        /// </summary>
        public List<(MemberPlan Plan, int Serial)> Members { get; } = [];

        /// <summary>
        /// The squad indexes of the members the template made.
        /// </summary>
        public HashSet<int> FromTemplate { get; } = [];
    }

    /// <summary>
    /// Makes the start's squads (docs/game/ui-screens.md "New game"): the first SQUAD_TEMPLATE's squad, which every CHARACTER entry's characters join
    /// after its members (a squad of their own when the start lists no template), then a squad for each further template in list order. Each member
    /// has its rolled look.
    /// </summary>
    List<Squad> SpawnStartSquads(World world, NewGameStart start, ulong key, Vector2 at, TownSite? town)
    {
        var groups = PlanStartGroups(world, start, key);
        float radius = town is null ? 100 : town.Town.SizeRadius * town.Town.TownRadiusMult;
        var squads = new List<Squad>();
        foreach (var group in groups)
            squads.Add(SpawnStartGroup(world, start, key, at, radius, group));

        return squads;
    }

    /// <summary>
    /// Plans the start's squads from its <c>squad</c> list (the Wanderer squad when it makes nobody); a template that plans nobody makes no squad,
    /// and at least one squad is always planned.
    /// </summary>
    List<StartGroup> PlanStartGroups(World world, NewGameStart start, ulong key)
    {
        var groups = new List<StartGroup>();
        StartGroup? first = null;
        int made = 0;   // members planned so far over the whole start: keys each template's plan and each CHARACTER's scatter

        void AddTemplate(GameRecord record)
        {
            var group = new StartGroup(SquadTemplate.From(record));
            var plan = SquadFactory.Plan(_data.Db, group.Template!, _settings.SquadSizeMultiplier, world.Seed, Rng.Mix(key ^ (uint)made), States);
            foreach (var m in plan.Members)
            {
                group.FromTemplate.Add(group.Members.Count);
                group.Members.Add((m, made++));
            }

            if (group.Members.Count == 0) return;
            groups.Add(group);
            first ??= group;
        }

        // A CHARACTER entry's characters stand up to 50 from the place on x and z, as the original's do.
        Vector2 Scatter(int i) => new(
            Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Spawn, SeedSalts.PlayerScatter + 2 * (ulong)i)) * 100 - 50,
            Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Spawn, SeedSalts.PlayerScatter + 2 * (ulong)i + 1)) * 100 - 50);

        // The templates first, in list order, then every CHARACTER entry into the first squad, whatever the list order.
        var records = start.Squad.Select(link => (Link: link, Record: _data.Db.Find(link.Id))).Where(e => e.Record is not null).ToList();
        foreach (var (_, record) in records)
            if (SquadFactory.IsTemplate(record!)) AddTemplate(record!);

        foreach (var (link, record) in records)
        {
            if (SquadFactory.IsTemplate(record!)) continue;

            if (first is null)
            {
                first = new StartGroup(null);
                groups.Add(first);
            }

            for (int n = 0; n < Math.Max(1, link.V0); n++)
            {
                first.Members.Add((new MemberPlan(link.Id, SquadRole.Squad1, first.Members.Count, Scatter(made), false, 1), made));
                made++;
            }
        }

        if (made == 0 && _data.Db.Find(WandererSquadId) is { } wanderer && SquadFactory.IsTemplate(wanderer))
            AddTemplate(wanderer);
        if (groups.Count == 0) groups.Add(new StartGroup(null));
        return groups;
    }

    /// <summary>
    /// Makes one planned start squad at <paramref name="at"/>: its members numbered in creation order, the first leading when none is (each template
    /// keeps its own layout), each with its rolled look.
    /// </summary>
    Squad SpawnStartGroup(World world, NewGameStart start, ulong key, Vector2 at, float radius, StartGroup group)
    {
        var plans = new List<MemberPlan>();
        bool hasLeader = group.Members.Any(m => m.Plan.Role == SquadRole.Leader);
        for (int i = 0; i < group.Members.Count; i++)
        {
            var plan = group.Members[i].Plan;
            plans.Add(plan with { Index = i, Role = !hasLeader && i == 0 ? SquadRole.Leader : plan.Role });
        }

        var factionId = _data.Factions[_data.PlayerFaction].Id;
        var members = new List<BuiltMember>();
        for (int i = 0; i < plans.Count; i++)
        {
            CharacterAppearance? look = null;
            if (_data.Appearances is { } source)   // an animal gets its species' shared look (GeneratedAppearances)
            {
                ulong salt = SeedSalts.PlayerLook + (ulong)group.Members[i].Serial;
                look = source.Create(plans[i].RecordId, factionId, (int)(Rng.Hash(world.Seed, key, RngPurpose.Spawn, salt) & 0x7FFFFFFF));
            }

            members.Add(new BuiltMember(plans[i], look));
        }

        var squadPlan = new SquadPlan(group.Template ?? new SquadTemplate { Id = start.Id, Name = start.Name }, plans, [], []);
        var built = new BuiltSquad(squadPlan, at, _data.PlayerFaction, members);
        return SpawnSquad(world, built, null, radius, player: true, gated: group.FromTemplate);
    }

    /// <summary>
    /// A start slave (CHARACTER <c>slave</c>, set for every new character in SpawnSquad) belongs to the start town's faction (engine choice: the
    /// original's owner is Unknown); an escaped one is stamped now (lie low, FixtureRegistry.LayLow); an ex-slave start is left free.
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
