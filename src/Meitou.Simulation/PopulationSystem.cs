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

/// <summary>Makes the drawable half of a character (rolled by <see cref="CharacterGenerator"/>); null when it cannot be made.</summary>

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
/// squads pick a home building is Unknown, so this is an engine choice. Bar squads and the roaming squads (<c>PopulationRoaming.cs</c>) are made here too; default residents come later.</para>
/// </summary>
public sealed partial class PopulationSystem : ITickSystem, IDisposable
{
    readonly PopulationData data;
    readonly PopulationSettings settings;
    readonly CharacterAssembly assembly;
    readonly SiteState[] states;
    readonly ConcurrentQueue<BuiltTown> built = new();
    readonly BlockingCollection<Action>? work;
    readonly Thread? worker;
    Vector3? focus;
    // The zones active as of the last look, and the set the next look fills (swapped, so a look allocates nothing; the host reads ActiveZones within a frame only).
    HashSet<ZoneCoordinate> active = [], spareActive = [];
    readonly List<Vector3> foci = [];

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
        assembly = new CharacterAssembly(data);
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
        foci.Clear();
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
        (active, spareActive) = (spareActive, active);
        active.Clear();
        foreach (var f in foci) ZoneActivation.AddZonesAround(f, settings.FastZoneHopping ? 1 : 0, active);
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

    /// <summary>Whether a zone may be loaded now: the host holds a town back until its navmesh is ready, so residents are not placed inside buildings of an open-ground stand-in (null: always).</summary>
    public Func<ZoneCoordinate, bool>? ZoneGate { get; set; }

    void Load(World world, TownSite site, SiteState state)
    {
        if (ZoneGate is { } gate && !gate(site.Zone)) return;   // retried at the next look
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


    void Spawn(World world, BuiltTown town)
    {
        var site = data.Sites[town.Site];
        var state = states[town.Site];
        float radius = site.Town.SizeRadius * site.Town.TownRadiusMult;
        foreach (var built in town.Squads)
        {
            var squad = SpawnSquad(world, built, site, radius);
            state.Squads.Add(squad.Id);   // slave squads are planned (built.Plan.Slaves) but not spawned yet
        }
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
            var race = assembly.RaceOf(plan.RecordId, member.Appearance);
            var record = data.Db.Find(plan.RecordId);
            var hot = new CharacterHot
            {
                Position = new Vector3(x, walk.GroundHeight(x, z), z),
                Health = 100,
                Task = (byte)(player ? CharacterTask.Idle : plan.Role == SquadRole.Leader || !hasLeader ? CharacterTask.Wander : CharacterTask.Follow),
                Mode = player ? SpeedMode.Free : SpeedMode.Walk,
                MaxSpeed = CharacterAssembly.StandInMaxSpeed,
                WalkSpeed = CharacterAssembly.WalkSpeed(race),
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
                FootprintRadius = race?.GetFloat("pathfind footprint radius") ?? 0,
            };
            assembly.Attach(table.PeekNextId(), cold, ref hot, record, race, world.Seed);
            var id = table.Spawn(hot, cold, world.Tick);
            squad.Members.Add(id);
            if (plan.Role == SquadRole.Leader && squad.Leader.IsNone) squad.Leader = id;
        }
        if (squad.Leader.IsNone && squad.Members.Count > 0) squad.Leader = squad.Members[0];
        world.Squads.Add(squad);
        return squad;
    }


    bool disposed;

    /// <summary>Stops the build thread; safe to call twice.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        work?.CompleteAdding();
        worker?.Join();
        work?.Dispose();
    }
}
