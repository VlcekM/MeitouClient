using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.World;

namespace Meitou.Simulation;

/// <summary>Makes the drawable half of a character (rolled by <see cref="CharacterGenerator"/>); null when it cannot be made.</summary>
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

    public static PopulationData Create(GameDatabase db, IEnumerable<TownPlacement> placements, IAppearanceSource? appearances = null)
    {
        var sites = new List<TownSite>();
        foreach (var p in placements)
        {
            if (db.Find(p.TownId) is not { Type: FcsRecordType.TOWN } record) continue;
            sites.Add(new TownSite(sites.Count, TownData.From(record), p.Position, WorldLayout.ZoneOf(p.Position.X, p.Position.Z)));
        }
        var factions = FactionData.LoadAll(db);
        return new PopulationData
        {
            Db = db, Sites = sites, Factions = factions, Appearances = appearances, PlayerFaction = factions.FindIndex(f => f.Name == "Nameless"),
            Relations = FactionRelations.Build(factions),
            SiteFactions = [.. sites.Select(s => s.Town.Faction is { } fid ? factions.FindIndex(f => f.Id == fid) : -1)],
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

/// <summary>
/// Zone activation and town residents (docs/game/game-loop.md "Zones", factions-squads-towns.md 6.4). The camera's focus (a
/// <see cref="FocusCommand"/>) activates every zone overlapping the box of +-340 units round it, plus a ring of one more zone when
/// <c>Fast zone hopping</c> is on (<see cref="ZoneActivation"/>). A placed town in an active zone gets its resident squads (squad
/// factory, then characters rolled by the generator); when its zone is no longer active for <see cref="PopulationSettings.UnloadGraceSeconds"/>
/// its characters are removed (the original turns the squads into abstract "unloaded" ones, which the AI stage will do). The
/// characters are built on a background thread when <see cref="PopulationSettings.Background"/> is set and taken in at the first tick
/// after they are ready.
/// <para>Resident lists (6.4, Observed): the town's own <c>residents</c>, plus the faction's unless the town has <c>residents override</c>
/// with a list of its own; an entry makes v0 squads (v0 = 0 is dropped). Squads are placed at random spots within
/// <see cref="PopulationSettings.PlacementRadiusShare"/> of the town radius round its placed position, on dry ground: how resident
/// squads pick a home building is Unknown, so this is an engine choice. Roaming squads, bar squads and default residents come later.</para>
/// </summary>
public sealed partial class PopulationSystem : ITickSystem, IDisposable
{
    readonly PopulationData data;
    readonly PopulationSettings settings;
    readonly SiteState[] states;
    readonly ConcurrentQueue<BuiltTown> built = new();
    readonly BlockingCollection<Action>? work;
    readonly Thread? worker;
    Vector3? focus;
    HashSet<ZoneCoordinate> active = [];

    sealed class SiteState
    {
        public SiteStatus Status;
        public long UnloadAtTick = -1;
        public long LoadId;
        public readonly List<int> Squads = [];
    }

    public PopulationSystem(PopulationData data, PopulationSettings? settings = null)
    {
        this.data = data;
        this.settings = settings ?? new PopulationSettings();
        states = new SiteState[data.Sites.Count];
        for (int i = 0; i < states.Length; i++) states[i] = new SiteState();
        if (!this.settings.Background) return;
        work = [];
        worker = new Thread(() => { foreach (var job in work.GetConsumingEnumerable()) job(); }) { IsBackground = true, Name = "Meitou.Sim population" };
        worker.Start();
    }

    public PopulationData Data => data;
    public PopulationSettings Settings => settings;
    public SiteStatus StatusOf(int site) => states[site].Status;
    /// <summary>The zones active as of the last look.</summary>
    public IReadOnlyCollection<ZoneCoordinate> ActiveZones => active;
    public Vector3? Focus => focus;

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        foreach (var c in commands)
            if (c is FocusCommand f) focus = f.Position;

        var ready = new List<BuiltTown>();
        while (built.TryDequeue(out var b)) ready.Add(b);
        ready.Sort(static (a, b) => a.Site.CompareTo(b.Site));
        foreach (var b in ready)
        {
            var state = states[b.Site];
            if (state.Status != SiteStatus.Loading || state.LoadId != b.LoadId) continue;   // no longer wanted
            Spawn(world, b);
            state.Status = SiteStatus.Loaded;
            state.UnloadAtTick = -1;
        }
        DrainRoamers(world);
    }

    public void SlowWorld(World world)
    {
        if (world.Tick % settings.CheckEveryTicks != 0) return;
        // The player's characters activate the zones round them (game-loop.md "Zones": the player's faction); the camera focus does only without a player.
        var foci = new List<Vector3>();
        if (world.Player.Exists)
        {
            var prev = world.Characters.Previous;
            for (int i = 0; i < prev.Length; i++)
                if (prev[i].Alive && world.Characters.Cold(i) is { IsPlayer: true }) foci.Add(prev[i].Position);
        }
        if (foci.Count == 0 && focus is { } camera) foci.Add(camera);
        if (foci.Count == 0)
        {
            StepRoaming(world);
            return;
        }
        active = [];
        foreach (var f in foci) active.UnionWith(ZoneActivation.ZonesAround(f, settings.FastZoneHopping ? 1 : 0));
        long grace = (long)Math.Ceiling(settings.UnloadGraceSeconds / world.TickSeconds);
        foreach (var site in data.Sites)
        {
            var state = states[site.Index];
            bool wanted = active.Contains(site.Zone);
            switch (state.Status)
            {
                case SiteStatus.Unloaded when wanted:
                    Load(world, site, state);
                    break;
                case SiteStatus.Loaded or SiteStatus.Loading when wanted:
                    state.UnloadAtTick = -1;
                    break;
                case SiteStatus.Loaded or SiteStatus.Loading:
                    if (state.UnloadAtTick < 0) state.UnloadAtTick = world.Tick + grace;
                    else if (world.Tick >= state.UnloadAtTick) Unload(world, state);
                    break;
            }
        }
        StepRoaming(world);
    }

    void Load(World world, TownSite site, SiteState state)
    {
        state.Status = SiteStatus.Loading;
        state.LoadId++;
        long loadId = state.LoadId;
        ulong seed = world.Seed;
        var walk = world.Walkability;
        if (work is null)
        {
            Spawn(world, BuildTown(site, loadId, seed, walk));
            state.Status = SiteStatus.Loaded;
        }
        else
        {
            work.Add(() => built.Enqueue(BuildTown(site, loadId, seed, walk)));
        }
    }

    void Unload(World world, SiteState state)
    {
        var table = world.Characters;
        foreach (int id in state.Squads)
        {
            if (world.Squads.Find(id) is not { } squad) continue;
            foreach (var m in squad.Members) table.Remove(m);
            world.Squads.Remove(id);
        }
        state.Squads.Clear();
        state.Status = SiteStatus.Unloaded;
        state.UnloadAtTick = -1;
        state.LoadId++;   // a build still in flight is dropped when it arrives
    }

    /// <summary>The resident entries of a town (6.4): template id and number of squads, v0 = 0 dropped.</summary>
    public static List<RecordLink> ResidentEntries(TownData town, FactionData? faction)
    {
        var list = new List<RecordLink>();
        list.AddRange(town.Residents);
        if (!(town.ResidentsOverride && town.Residents.Count > 0) && faction is not null) list.AddRange(faction.Residents);
        list.RemoveAll(e => e.V0 <= 0);
        return list;
    }

    // ---- building (pure given the seed; safe on another thread) ----

    internal sealed record BuiltMember(MemberPlan Plan, CharacterAppearance? Appearance);
    internal sealed record BuiltSquad(SquadPlan Plan, Vector2 Position, int Faction, List<BuiltMember> Members);
    sealed record BuiltTown(int Site, long LoadId, List<BuiltSquad> Squads);

    BuiltTown BuildTown(TownSite site, long loadId, ulong seed, IWalkability walk)
    {
        var town = site.Town;
        var faction = town.Faction is { } fid ? data.Factions.FirstOrDefault(f => f.Id == fid) : null;
        int factionIndex = faction is null ? -1 : data.Factions.ToList().IndexOf(faction);
        var squads = new List<BuiltSquad>();
        var entries = ResidentEntries(town, faction);
        for (int e = 0; e < entries.Count; e++)
        {
            if (data.Db.Find(entries[e].Id) is not { } record || record.Type is not (FcsRecordType.SQUAD_TEMPLATE or FcsRecordType.UNIQUE_SQUAD_TEMPLATE)) continue;
            var template = SquadTemplate.From(record);
            for (int k = 0; k < entries[e].V0; k++)
            {
                ulong key = Rng.Mix((ulong)site.Index << 40 ^ (ulong)e << 20 ^ (uint)k);
                if (BuildOne(template, factionIndex, faction, key, seed, Place(site, seed, key, walk)) is { } one) squads.Add(one);
            }
        }
        // Bar squads (6.4): the town's own list, plus the faction's for towns of type Town; v0 slots each, present with the chance v1 per cent (0 counts as 100).
        var bars = BarEntries(town, faction);
        for (int e = 0; e < bars.Count; e++)
        {
            if (data.Db.Find(bars[e].Id) is not { } record || record.Type is not (FcsRecordType.SQUAD_TEMPLATE or FcsRecordType.UNIQUE_SQUAD_TEMPLATE)) continue;
            var template = SquadTemplate.From(record);
            for (int k = 0; k < bars[e].V0; k++)
            {
                ulong key = Rng.Mix(0xBA45UL ^ (ulong)site.Index << 40 ^ (ulong)e << 20 ^ (uint)k);
                int chance = bars[e].V1 <= 0 ? 100 : bars[e].V1;
                if (Rng.Float(Rng.Hash(seed, key, RngPurpose.Spawn, 77)) * 100 >= chance) continue;
                if (BuildOne(template, factionIndex, faction, key, seed, Place(site, seed, key, walk)) is { } one) squads.Add(one);
            }
        }
        return new BuiltTown(site.Index, loadId, squads);
    }

    /// <summary>The bar squad entries of a town (6.4, Observed): its own <c>bar squads</c>, plus the faction's default list for towns of type Town. v0 = 0 entries are dropped.</summary>
    public static List<RecordLink> BarEntries(TownData town, FactionData? faction)
    {
        var list = new List<RecordLink>(town.BarSquads);
        if (town.Type == TownType.Town && faction is not null) list.AddRange(faction.BarSquads);
        list.RemoveAll(e => e.V0 <= 0);
        return list;
    }

    /// <summary>One squad: its plan from the factory and a rolled look for every human member. Pure given its arguments (any thread).</summary>
    BuiltSquad? BuildOne(SquadTemplate template, int townFactionIndex, FactionData? townFaction, ulong key, ulong seed, Vector2 position)
    {
        var plan = SquadFactory.Plan(data.Db, template, settings.SquadSizeMultiplier, seed, key, data.States);
        if (plan.TotalMembers == 0) return null;
        // The squad's own faction (its template's) wins over the town's (section 4).
        int squadFaction = template.Faction is { } tf && data.Factions.ToList().FindIndex(f => f.Id == tf) is var ti and >= 0 ? ti : townFactionIndex;
        var members = new List<BuiltMember>();
        int n = 0;
        foreach (var m in plan.Members)
        {
            CharacterAppearance? look = null;
            if (!m.IsAnimal && data.Appearances is { } source)
                look = source.Create(m.RecordId, townFaction?.Id, (int)(Rng.Hash(seed, key, RngPurpose.Spawn, 1000UL + (ulong)n) & 0x7FFFFFFF));
            members.Add(new BuiltMember(m, look));
            n++;
        }
        return new BuiltSquad(plan, position, squadFaction, members);
    }

    Vector2 Place(TownSite site, ulong seed, ulong key, IWalkability walk)
    {
        float radius = site.Town.SizeRadius * site.Town.TownRadiusMult * settings.PlacementRadiusShare;
        var centre = new Vector2(site.Position.X, site.Position.Z);
        for (int attempt = 0; attempt < 12; attempt++)
        {
            ulong roll = Rng.Hash(seed, key, RngPurpose.Spawn, (ulong)attempt);
            float angle = Rng.Float(roll) * MathF.Tau, r = MathF.Sqrt(Rng.Float(Rng.Mix(roll))) * radius;
            var p = centre + new Vector2(MathF.Sin(angle), MathF.Cos(angle)) * r;
            if (walk.IsWalkable(p.X, p.Y)) return p;
        }
        return centre;
    }

    void Spawn(World world, BuiltTown town)
    {
        var table = world.Characters;
        var site = data.Sites[town.Site];
        var state = states[town.Site];
        float radius = site.Town.SizeRadius * site.Town.TownRadiusMult;
        foreach (var built in town.Squads)
        {
            var squad = SpawnSquad(world, built, site, radius);
            state.Squads.Add(squad.Id);
            foreach (var slave in built.Plan.Slaves) _ = slave;   // slave squads: not placed yet (roles in the plan only)
        }
        _ = table;
    }

    Squad SpawnSquad(World world, BuiltSquad built, TownSite? site, float radius, bool player = false, int platoonId = -1)
    {
        var table = world.Characters;
        var walk = world.Walkability;
        var squad = new Squad
        {
            Id = world.Squads.NextId(),
            TemplateId = built.Plan.Template.Id,
            Name = built.Plan.Template.Name,
            Faction = built.Faction,
            Town = site?.Index ?? -1,
            PlatoonId = platoonId,
            Position = new Vector3(built.Position.X, walk.GroundHeight(built.Position.X, built.Position.Y), built.Position.Y),
            HomeCentre = site is null ? built.Position : new Vector2(site.Position.X, site.Position.Z),
            HomeRadius = Math.Max(radius * 0.8f, 40),
        };
        var leaderPlan = built.Members.FirstOrDefault(m => m.Plan.Role == SquadRole.Leader)?.Plan;
        bool hasLeader = leaderPlan is not null;
        foreach (var member in built.Members)
        {
            var plan = member.Plan;
            float x = built.Position.X + plan.Offset.X, z = built.Position.Y + plan.Offset.Y;
            if (!walk.IsWalkable(x, z)) (x, z) = (built.Position.X, built.Position.Y);
            var (maxSpeed, walkSpeed) = SpeedStats(plan.RecordId, member.Appearance);
            var record = data.Db.Find(plan.RecordId);
            var hot = new CharacterHot
            {
                Position = new Vector3(x, walk.GroundHeight(x, z), z),
                Health = 100,
                Task = (byte)(player ? CharacterTask.Idle : plan.Role == SquadRole.Leader || !hasLeader ? CharacterTask.Wander : CharacterTask.Follow),
                Mode = player ? SpeedMode.Free : SpeedMode.Walk,
                MaxSpeed = maxSpeed,
                WalkSpeed = walkSpeed,
                TaskTime = 0.5f + Rng.Float(Rng.Hash(world.Seed, (ulong)squad.Id << 8 | (uint)plan.Index, RngPurpose.Think)) * 3,
            };
            var cold = new CharacterCold
            {
                Name = record?.Name ?? plan.RecordId,
                Faction = squad.Faction,
                Appearance = member.Appearance,
                RecordId = plan.RecordId,
                SquadId = squad.Id,
                Role = (int)plan.Role,
                FormationOffset = plan.Offset - (leaderPlan?.Offset ?? Vector2.Zero),
                IsPlayer = player,
            };
            var id = table.Spawn(hot, cold, world.Tick);
            squad.Members.Add(id);
            if (plan.Role == SquadRole.Leader && squad.Leader.IsNone) squad.Leader = id;
        }
        if (squad.Leader.IsNone && squad.Members.Count > 0) squad.Leader = squad.Members[0];
        world.Squads.Add(squad);
        return squad;
    }

    /// <summary>
    /// Starts the player (a new game, before the first tick): their faction, their money and their squad at the start town, made from the
    /// start-off's <c>squad</c> list (a SQUAD_TEMPLATE is planned by the squad factory, a CHARACTER is one member) as one squad of the
    /// player's faction, standing idle, selected for them. Where: <see cref="NewGameStart.ForceStartPos"/> gives the place itself,
    /// otherwise the first listed town that is placed in the world (the new-game map's own choice of start town is not modelled), at a
    /// seeded spot within a third of its radius. Faction relation overrides, research and <c>force race</c> are not applied yet (the first
    /// two are <b>Unknown</b> in the original, docs/game/factions-squads-towns.md 3.2). The characters' items are what the generator rolls
    /// for their look; the inventory of the record comes with the inventory stage.
    /// </summary>
    public Squad StartPlayer(World world, NewGameStart start)
    {
        if (data.PlayerFaction < 0) throw new InvalidOperationException("the game data has no player faction (Nameless)");
        var centre = start.StartPosition;
        float radius = 100;
        TownSite? town = null;
        if (!start.ForceStartPos)
        {
            foreach (var link in start.Towns)
                if (data.Sites.FirstOrDefault(s => s.Town.Id == link.Id) is { } site) { town = site; break; }
            if (town is not null)
            {
                centre = new Vector2(town.Position.X, town.Position.Z);
                radius = town.Town.SizeRadius * town.Town.TownRadiusMult;
            }
        }
        ulong key = Rng.Mix(0x57A27UL ^ StableHash(start.Id));
        var at = centre;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            ulong roll = Rng.Hash(world.Seed, key, RngPurpose.Spawn, (ulong)attempt);
            float angle = Rng.Float(roll) * MathF.Tau, r = MathF.Sqrt(Rng.Float(Rng.Mix(roll))) * radius / 3;
            var p = centre + new Vector2(MathF.Sin(angle), MathF.Cos(angle)) * r;
            if (world.Walkability.IsWalkable(p.X, p.Y)) { at = p; break; }
        }

        // One squad of everything the start lists.
        var members = new List<BuiltMember>();
        var first = new List<MemberPlan>();
        SquadTemplate? template = null;
        foreach (var link in start.Squad)
        {
            var record = data.Db.Find(link.Id);
            if (record is null) continue;
            if (record.Type is FcsRecordType.SQUAD_TEMPLATE or FcsRecordType.UNIQUE_SQUAD_TEMPLATE)
            {
                var t = SquadTemplate.From(record);
                template ??= t;
                first.AddRange(SquadFactory.Plan(data.Db, t, settings.SquadSizeMultiplier, world.Seed, Rng.Mix(key ^ (uint)first.Count), data.States).Members);
            }
            else
            {
                first.Add(new MemberPlan(link.Id, SquadRole.Squad1, first.Count, default, false, 1));
            }
        }
        // Renumber in creation order (the layout counts every member) and make the first the leader when none is.
        bool hasLeader = first.Any(m => m.Role == SquadRole.Leader);
        for (int i = 0; i < first.Count; i++)
            first[i] = first[i] with { Index = i, Offset = SquadFactory.Offset(i), Role = !hasLeader && i == 0 ? SquadRole.Leader : first[i].Role };
        var factionId = data.Factions[data.PlayerFaction].Id;
        foreach (var m in first)
        {
            CharacterAppearance? look = null;
            if (!m.IsAnimal && data.Appearances is { } source)
                look = source.Create(m.RecordId, factionId, (int)(Rng.Hash(world.Seed, key, RngPurpose.Spawn, 5000UL + (ulong)m.Index) & 0x7FFFFFFF));
            members.Add(new BuiltMember(m, look));
        }
        var plan = new SquadPlan(template ?? new SquadTemplate { Id = start.Id, Name = start.Name }, first, [], []);
        var built = new BuiltSquad(plan, at, data.PlayerFaction, members);
        var squad = SpawnSquad(world, built, null, radius, player: true);
        world.Player.Faction = data.PlayerFaction;
        world.Player.Money = start.Money;
        world.Player.Squad = squad.Id;
        world.Player.Selection.Clear();
        return squad;
    }

    /// <summary>A string hash that is the same in every process (string.GetHashCode is not).</summary>
    static ulong StableHash(string s)
    {
        ulong h = 0xCBF29CE484222325UL;
        foreach (char c in s) h = (h ^ c) * 0x100000001B3UL;
        return h;
    }

    /// <summary>The speed stat S and the walk speed of a character (docs/game/pathfinding.md "Speed"): S = lerp(race <c>speed min skill</c>, <c>speed max skill</c>, athletics / 100), with a stand-in athletics until stats exist (stage 7).</summary>
    (float Max, float Walk) SpeedStats(string recordId, CharacterAppearance? look)
    {
        const float standInAthletics = 20;
        var race = look?.Race;
        if (race is null && data.Db.Find(recordId) is { } rec)
        {
            var refs = rec.GetReferences("race").Where(r => r.Values.Value0 > 0).ToList();
            if (refs.Count > 0) race = data.Db.Find(refs[0].TargetStringId);
        }
        if (race is null) return (45, 15);
        float min = race.GetInt("speed min skill", 70), max = race.GetInt("speed max skill", 120);
        return (min + (max - min) * standInAthletics * 0.01f, race.GetFloat("walk speed", 15));
    }

    public void Dispose()
    {
        work?.CompleteAdding();
        worker?.Join();
        work?.Dispose();
    }
}

/// <summary>Which zones a focus keeps active (docs/game/game-loop.md "Zones", Verified for the ring, Observed for the box).</summary>
public static class ZoneActivation
{
    /// <summary>Half the side of the box round the focus whose overlapping zones are activated.</summary>
    public const float Box = 340;

    /// <summary>Every zone overlapping the box of +-<see cref="Box"/> round <paramref name="p"/>, plus <paramref name="ring"/> more zones on every side (0 or 1, the <c>Fast zone hopping</c> setting).</summary>
    public static HashSet<ZoneCoordinate> ZonesAround(Vector3 p, int ring)
    {
        var lo = WorldLayout.ZoneOf(p.X - Box, p.Z - Box);
        var hi = WorldLayout.ZoneOf(p.X + Box, p.Z + Box);
        var set = new HashSet<ZoneCoordinate>();
        for (int x = lo.X - ring; x <= hi.X + ring; x++)
            for (int y = lo.Y - ring; y <= hi.Y + ring; y++)
                set.Add(new ZoneCoordinate(x, y));
        return set;
    }
}
