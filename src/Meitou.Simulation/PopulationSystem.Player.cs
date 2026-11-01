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

public sealed partial class PopulationSystem
{
    /// <summary>The squad a new game starts with when its start-off makes nobody: the SQUAD_TEMPLATE "startoff- Wanderer squad", whose id is a constant of the executable.</summary>
    public const string WandererSquadId = "45550-gamedata.base";
    /// <summary>
    /// Where a new game of <paramref name="start"/> with <paramref name="seed"/> stands its squad (docs/game/ui-screens.md "New game"): <see cref="NewGameStart.ForceStartPos"/>'s
    /// place, else a placement drawn among the start's towns, else among the fallback towns, else (-43500, -6625); <c>Town</c> is the drawn placement. <c>Missing</c>: the start
    /// lists towns and none of them is placed. The host loads the world round it before <see cref="StartPlayer"/>, which draws the same.
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
    /// Starts the player (a new game, before the first tick) as docs/game/ui-screens.md "New game" describes: their faction, their money and their squad,
    /// made from the start-off's <c>squad</c> list (a SQUAD_TEMPLATE is planned by the squad factory, a CHARACTER makes v0 members) as one squad of the
    /// player's faction, standing idle, selected for them, at <see cref="NewGameStart.ForceStartPos"/>'s place or a town drawn from the start's list (<see cref="StartPlace"/>). The start
    /// research is applied (<see cref="Meitou.Data.Gameplay.Construction.ResearchGate.StartResearch"/>) and the start-off's <c>faction relations</c>, whose factions are known with
    /// <see cref="PopulationData.KnownAtNewGame"/>'s; <c>force race</c>
    /// is kept as the races the character creation offers (<see cref="PlayerState.CreationRaces"/>). The characters' items are what the generator rolls for their
    /// look; their inventory is built like everybody's (<see cref="CharacterAssembly"/>).
    /// </summary>
    public Squad StartPlayer(World world, NewGameStart start)
    {
        if (data.PlayerFaction < 0) throw new InvalidOperationException($"the game data has no player faction (FACTION {FactionData.PlayerId})");
        ulong key = Rng.Mix(SeedSalts.PlayerStart ^ Rng.StableHash(start.Id));
        ApplyStartRelations(data.Relations, start);
        data.ClearReplacements();
        var (centre, town, missing) = StartPlace(data, world.Seed, start);
        if (missing) world.Post(Loc.Tr("Cannot find any towns from the game start town list."));
        // The original stands the squad at the place itself; here on a walkable spot near it.
        const float spread = 50;
        var at = centre;
        if (!world.Walkability.IsWalkable(centre.X, centre.Y))
            for (int attempt = 0; attempt < 20; attempt++)
            {
                var p = Rng.PointInDisc(Rng.Hash(world.Seed, key, RngPurpose.Spawn, (ulong)attempt), centre, spread * (1 + attempt));
                if (world.Walkability.IsWalkable(p.X, p.Y)) { at = p; break; }
            }

        if (data.BiomeGroupAt(centre) is { } group)
            foreach (var site in data.Sites)
                if (site.Town.Type == TownType.Town && data.BiomeGroupAt(new Vector2(site.Position.X, site.Position.Z)) == group) world.Player.Know(site.Index, TownKnowledge.Discovered);

        // One squad of everything the start lists.
        var members = new List<BuiltMember>();
        var first = new List<MemberPlan>();
        SquadTemplate? template = null;
        void AddTemplate(GameRecord record)
        {
            var t = SquadTemplate.From(record);
            template ??= t;
            first.AddRange(SquadFactory.Plan(data.Db, t, settings.SquadSizeMultiplier, world.Seed, Rng.Mix(key ^ (uint)first.Count), States).Members);
        }
        // A CHARACTER entry's characters stand up to 50 from the place on x and z, as the original's do.
        Vector2 Scatter(int i) => new(Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Spawn, SeedSalts.PlayerScatter + 2 * (ulong)i)) * 100 - 50,
            Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Spawn, SeedSalts.PlayerScatter + 2 * (ulong)i + 1)) * 100 - 50);
        foreach (var link in start.Squad)
        {
            var record = data.Db.Find(link.Id);
            if (record is null) continue;
            if (SquadFactory.IsTemplate(record)) AddTemplate(record);
            else for (int n = 0; n < Math.Max(1, link.V0); n++) first.Add(new MemberPlan(link.Id, SquadRole.Squad1, first.Count, Scatter(first.Count), false, 1));
        }
        if (first.Count == 0 && data.Db.Find(WandererSquadId) is { } wanderer && SquadFactory.IsTemplate(wanderer)) AddTemplate(wanderer);
        // Renumber in creation order and make the first the leader when none is; each template keeps its own layout (in the original, a squad of its own).
        bool hasLeader = first.Any(m => m.Role == SquadRole.Leader);
        for (int i = 0; i < first.Count; i++)
            first[i] = first[i] with { Index = i, Role = !hasLeader && i == 0 ? SquadRole.Leader : first[i].Role };
        var factionId = data.Factions[data.PlayerFaction].Id;
        foreach (var m in first)
        {
            CharacterAppearance? look = null;
            if (data.Appearances is { } source)   // an animal gets its species' shared look (GeneratedAppearances)
                look = source.Create(m.RecordId, factionId, (int)(Rng.Hash(world.Seed, key, RngPurpose.Spawn, SeedSalts.PlayerLook + (ulong)m.Index) & 0x7FFFFFFF));
            members.Add(new BuiltMember(m, look));
        }
        var plan = new SquadPlan(template ?? new SquadTemplate { Id = start.Id, Name = start.Name }, first, [], []);
        var built = new BuiltSquad(plan, at, data.PlayerFaction, members);
        var squad = SpawnSquad(world, built, null, town is null ? 100 : town.Town.SizeRadius * town.Town.TownRadiusMult, player: true);
        // CHARACTER `slave` (fcs.def "sets initial slave state", SlaveStateEnum NOT_SLAVE 0, IS_SLAVE 1, ESCAPING_SLAVE 2, EX_SLAVE 3): The Slaves start's "start slave" is 1. The owner is
        // the start town's faction (engine choice: the original's is Unknown); an ex-slave is free.
        int owner = town is not null && town.Index < data.SiteFactions.Count ? data.SiteFactions[town.Index] : -1;
        foreach (var id in squad.Members)
            if (world.Characters.Cold(id.Slot) is { } c && data.Db.Find(c.RecordId)?.GetInt("slave") is (1 or 2) and var state && owner >= 0)
                (c.SlaveState, c.SlaveOwner) = ((byte)state, owner);
        // The first player squad reads like its platoon, the faction's name and 0 ("Nameless_0", docs/game/ui-screens.md "Squad tabs").
        squad.Name = $"{(data.Factions[data.PlayerFaction].Name is { Length: > 0 } f ? f : "Nameless")}_0";
        world.Player.Faction = data.PlayerFaction;
        var known = data.KnownAtNewGame();
        known.UnionWith(start.FactionRelations.Select(link => data.FactionIndex(link.Id)).Where(f => f >= 0));
        world.Player.KnownFactions = known;
        world.Player.Money = start.Money;
        world.Research.Set(data.Db, Meitou.Data.Gameplay.Construction.ResearchGate.StartResearch(data.Db, start));
        world.Player.Squad = squad.Id;
        world.Player.CreationRaces = [.. start.ForceRace.Select(r => r.Id)];
        world.Player.InCreation = true;   // the character editor may remake them until it is confirmed (docs/characters.md "Character editor")
        world.Player.Selection.Clear();
        if (squad.Members.Count > 0) world.Player.Selection.Add(squad.Members[0]);
        return squad;
    }
}
