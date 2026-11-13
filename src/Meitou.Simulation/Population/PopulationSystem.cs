using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using Meitou.Simulation.Kidnap;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Perception;
using Meitou.Simulation.Player;
using Meitou.Simulation.Weather;

namespace Meitou.Simulation.Population;

/// <summary>
/// Zone activation and town residents (docs/game/game-loop.md "Update, threads and budget", factions-squads-towns.md 6.4). The active zones are the
/// loaded ones (<see cref="RefreshLoaded"/>): the 3 x 3 block round the camera's focus (a <see cref="FocusCommand"/>) and the box of +-340 units
/// round each player character, with a ring of one more zone when <c>Fast zone hopping</c> is on (<see cref="ZoneActivation"/>). A placed town in an
/// active zone gets its resident squads (squad factory, then characters rolled by the generator); when its zone is no longer active for
/// <see cref="PopulationSettings.TownUnloadGraceSeconds"/> its characters are removed (the original turns the squads into abstract "unloaded" ones,
/// which the AI stage will do). The characters are built on a background thread when <see cref="PopulationSettings.Background"/> is set and taken in
/// at the first tick after they are ready.
/// <para>Residents (docs/game/factions-squads-towns.md "Town residents"): the resident table (<see cref="ResidentHomes.Entries"/>) is housed in the
/// town's buildings and each squad made at a node of its home (<c>PopulationHomes.cs</c>); a town without building data puts each entry's v0 squads
/// at random spots within <see cref="PopulationSettings.PlacementRadiusShare"/> of the town radius round its placed position, on dry ground (engine
/// choice). Bar squads, the roaming squads (<c>PopulationRoaming.cs</c>) and the wild, homeless squads and nests (<c>PopulationWild.cs</c>), are made
/// here too.</para>
/// </summary>
public sealed partial class PopulationSystem : ITickSystem, IDisposable
{
    /// <summary>
    /// The population's data.
    /// </summary>
    readonly PopulationData _data;
    /// <summary>
    /// The population's settings.
    /// </summary>
    readonly PopulationSettings _settings;
    /// <summary>
    /// Assembles the characters made (race, stats, medical state, inventory, fighter).
    /// </summary>
    readonly CharacterAssembly _assembly;
    /// <summary>
    /// Each site's resident state, by site index.
    /// </summary>
    readonly SiteState[] _states;

    /// <summary>
    /// The zones the towns are centred in (a town is made when its zone is active).
    /// </summary>
    readonly HashSet<ZoneCoordinate> _centres;
    /// <summary>
    /// The towns built and ready to be taken in at the next tick.
    /// </summary>
    readonly ConcurrentQueue<BuiltTown> _built = new();
    /// <summary>
    /// The build thread's work queue (null: built on the tick's thread, <see cref="PopulationSettings.Background"/> off).
    /// </summary>
    readonly BlockingCollection<Action>? _work;
    /// <summary>
    /// The build thread (null without <see cref="_work"/>).
    /// </summary>
    readonly Thread? _worker;
    /// <summary>
    /// The camera's focus of the last <see cref="FocusCommand"/> (null: none yet).
    /// </summary>
    Vector3? _focus;

    /// <summary>
    /// The zones active as of the last look (the loaded ones), and the set the next look fills (swapped, so a look allocates nothing; the host reads
    /// ActiveZones within a frame only).
    /// </summary>
    HashSet<ZoneCoordinate> _active = [], _spareActive = [];

    /// <summary>
    /// The player's characters, whose box is asked for at each look, and the zones of that box (a scratch set).
    /// </summary>
    readonly List<Vector3> _players = [];
    /// <summary>
    /// The zones of the player's box (see <see cref="_players"/>).
    /// </summary>
    readonly HashSet<ZoneCoordinate> _boxZones = [];

    /// <summary>
    /// The original's loaded zones (ZoneLoad): each zone with the tick until which its latest request keeps it loaded (RefreshLoaded).
    /// </summary>
    readonly Dictionary<ZoneCoordinate, long> _loadedUntil = [];
    /// <summary>
    /// The zone the 3 x 3 block round the camera is centred on (null: none yet; recentred when the camera strays).
    /// </summary>
    ZoneCoordinate? _loadCentre;

    /// <summary>
    /// A town site's resident state: its status, the tick it unloads at, its load number and its squads.
    /// </summary>
    sealed class SiteState
    {
        /// <summary>
        /// Where its residents are in their life.
        /// </summary>
        public SiteStatus Status;
        /// <summary>
        /// The tick at which its residents are removed (-1: not unloading).
        /// </summary>
        public long UnloadAtTick = -1;
        /// <summary>
        /// The number of its latest load: a build of an older one is dropped when it arrives.
        /// </summary>
        public long LoadId;
        /// <summary>
        /// Its resident squads' ids.
        /// </summary>
        public readonly List<int> Squads = [];
        /// <summary>
        /// A nest's top-up is being built.
        /// </summary>
        public bool Pending;
        /// <summary>
        /// The nest rolls used since it loaded (a top-up's keys follow them).
        /// </summary>
        public int Made;
        /// <summary>
        /// A sector's squads asked for at its load and not spawned yet (counted in the island's load, <see cref="IslandRoom"/>).
        /// </summary>
        public int Planned;
        /// <summary>
        /// Replaced, its new residents not made yet (the original's <c>updatedTownOverrides</c>; not saved, as there).
        /// </summary>
        public bool Repopulate;
    }

    /// <summary>
    /// Makes the system over the population data (and its settings: the defaults when null), with the build thread when
    /// <see cref="PopulationSettings.Background"/> is set.
    /// </summary>
    public PopulationSystem(PopulationData data, PopulationSettings? settings = null)
    {
        this._data = data;
        this._settings = settings ?? new PopulationSettings();
        _assembly = new CharacterAssembly(data);
        _states = new SiteState[data.Sites.Count];
        for (int i = 0; i < _states.Length; i++) _states[i] = new SiteState();
        _centres = [.. data.Sites.Select(s => s.Zone)];
        KeepRoamers = this._settings.KeepRoamers;
        OffscreenFights = this._settings.OffscreenFights;
        OffscreenHealing = this._settings.OffscreenHealing;
        CampaignAncestorsOnce = this._settings.CampaignAncestorsOnce;
        KeepWild = this._settings.KeepWild;
        ResidentSchedule = this._settings.ResidentSchedule;
        _live = new WorldStates(data.Db, data.Relations);
        if (!this._settings.Background) return;
        _work = [];
        _worker = new Thread(() => { foreach (var job in _work.GetConsumingEnumerable()) job(); }) { IsBackground = true,
            Name = "Meitou.Sim population" };
        _worker.Start();
    }

    /// <summary>
    /// The off-screen switches (<see cref="PopulationSettings.KeepRoamers"/> and the next two), changed live by the host.
    /// </summary>
    public bool KeepRoamers { get; set; }
    /// <summary>
    /// The off-screen fights switch (<see cref="PopulationSettings.OffscreenFights"/>), changed live by the host.
    /// </summary>
    public bool OffscreenFights { get; set; }
    /// <summary>
    /// The off-screen healing switch (<see cref="PopulationSettings.OffscreenHealing"/>), changed live by the host.
    /// </summary>
    public bool OffscreenHealing { get; set; }
    /// <summary>
    /// The campaign inheritance switch (<see cref="PopulationSettings.CampaignAncestorsOnce"/>), changed live by the host.
    /// </summary>
    public bool CampaignAncestorsOnce { get; set; }
    /// <summary>
    /// The wild-squad switch (<see cref="PopulationSettings.KeepWild"/>), changed live by the host.
    /// </summary>
    public bool KeepWild { get; set; }
    /// <summary>
    /// The residents' schedule switch (<see cref="PopulationSettings.ResidentSchedule"/>), changed live by the host.
    /// </summary>
    public bool ResidentSchedule { get; set; }

    /// <summary>
    /// Game hours a town must have been unloaded for its residents to come back where their schedule puts them (engine choice: a quick reload changes
    /// nothing).
    /// </summary>
    public const double ScheduleAfterHours = 1;

    /// <summary>
    /// The world states of this world, answered live (see <see cref="States"/>).
    /// </summary>
    readonly WorldStates _live;
    /// <summary>
    /// The world states the population's spawns and campaigns read: the data's fixed ones when it has any, else the live ones of this world
    /// (<see cref="WorldStates"/>).
    /// </summary>
    public IWorldStates States => ReferenceEquals(_data.States, IWorldStates.None) ? _live : _data.States;

    /// <summary>
    /// The live world states take the unique table and the player's faction (serial; every tick, again after a look's unique refresh, and before the
    /// loading screen's builds).
    /// </summary>
    void RefreshStates(World world) => _live.Refresh(world.Uniques, world.Player.Faction >= 0 ? world.Player.Faction : _data.PlayerFaction);

    /// <summary>
    /// Whether the tick takes every build and drains the roaming and wild queues at once (set behind the loading screen).
    /// </summary>
    bool _drainAll;

    /// <summary>
    /// The population's data.
    /// </summary>
    public PopulationData Data => _data;
    /// <summary>
    /// The population's settings.
    /// </summary>
    public PopulationSettings Settings => _settings;
    /// <summary>
    /// Where the residents of site <paramref name="site"/> are in their life.
    /// </summary>
    public SiteStatus StatusOf(int site) => _states[site].Status;
    /// <summary>
    /// The zones active as of the last look: the loaded zones (<see cref="RefreshLoaded"/>), where the squads, the items and the navmesh are kept.
    /// </summary>
    public IReadOnlyCollection<ZoneCoordinate> ActiveZones => _active;
    /// <summary>
    /// The camera's focus of the last <see cref="FocusCommand"/> (null: none yet).
    /// </summary>
    public Vector3? Focus => _focus;

    /// <summary>
    /// The commands and the population's start of a tick: the artifacts, formations and forced speeds set up once, the camera's focus, the builds
    /// taken in.
    /// </summary>
    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        EnsureArtifacts(world);
        world.FactionFormations ??= [.. _data.Factions.Select(f => f.SquadFormation)];
        world.ForcedSpeeds ??= _data.Db.OfType(FcsRecordType.SquadTemplate).Concat(_data.Db.OfType(FcsRecordType.UniqueSquadTemplate))
            .Where(r => SquadTemplate.ForceSpeedOf(r) is >= 0 and < Squad.NoForceSpeed).ToDictionary(r => r.StringId,
                r => (byte)SquadTemplate.ForceSpeedOf(r));
        foreach (var c in commands)
        {
            if (c is FocusCommand f) _focus = f.Position;
            else if (c is DebugSpawn spawn) SpawnDebug(world, spawn);
        }

        var ready = new List<BuiltTown>();
        while (_built.TryDequeue(out var b)) ready.Add(b);
        ready.Sort(static (a, b) => a.Site.CompareTo(b.Site));
        foreach (var b in ready)
        {
            var state = _states[b.Site];
            if (state.Status != SiteStatus.Loading || state.LoadId != b.LoadId) continue;   // no longer wanted
            Spawn(world, b);
            state.Status = SiteStatus.Loaded;
            state.UnloadAtTick = -1;
        }

        if (_drainAll) { while (DrainRoamers(world)) { } while (DrainWild(world)) { } }
        else if (!DrainRoamers(world)) DrainWild(world);
        while (world.Characters.LimbsChanged.TryDequeue(out var cut))
        {
            if (world.Characters.Cold(cut.Character) is not { } cold) continue;

            // a severed leg may close the boots (FUN_14064edc0 -> validateInventorySections)
            if (cut.Limb is 2 or 3) InventoryCommands.ValidateSections(world, cut.Character, cold);
            _assembly.RefreshEquipment(cold);
            if (cut.Drops) DropSeveredLimb(world, cut.Character, cold, cut.Limb);
        }
    }

    /// <summary>
    /// The RACE <c>severed limbs</c> item of <paramref name="limb"/> on the ground, if the race has one for it
    /// (docs/game/character-stats.md "Limb replacements and robots"). The original drops it at the limb's <c>bone name 2</c> and lets
    /// physics take it; here it lies flat beside the character on that side (an engine choice), turned by a roll of the tick, and the host
    /// drops it from that bone's place (<see cref="FallSource"/>).
    /// </summary>
    void DropSeveredLimb(World world, int slot, CharacterCold cold, int limb)
    {
        // The first entry of the slot whose record exists (FUN_14064edc0: one whose target did not resolve is skipped, and with none nothing drops;
        // the stump and wound stay).
        ItemInstance? item = null;
        foreach (var severed in cold.Race?.SeveredLimbs ?? [])
            if (severed.Slot == limb && (item = _data.Items.Create(severed.TargetStringId)) is not null) break;
        if (item is null) return;
        ref readonly var hot = ref world.Characters.Next[slot];
        float side = limb is 0 or 2 ? -1 : 1, forward = limb < 2 ? 0.5f : -1.5f;
        var right = new Vector3(MathF.Cos(hot.Yaw), 0, -MathF.Sin(hot.Yaw));
        var ahead = new Vector3(MathF.Sin(hot.Yaw), 0, MathF.Cos(hot.Yaw));
        float yaw = Rng.Float(Rng.Hash(world.Seed, (ulong)slot, RngPurpose.Loot, (ulong)world.Tick)) * MathF.Tau;

        // On the ground under that spot (a drop beside a character on a slope or a step would otherwise hang at its feet's height), as an item
        // dropped from the inventory.
        var at = hot.Position + right * (5 * side) + ahead * forward;
        at.Y = world.Walkability.GroundHeight(at.X, at.Z, hot.Position.Y);

        // The stump: the part of the limb's slot and its bone name 2 (BodyPartTemplate.BoneName2; FUN_14064edc0 reads it for the item's pose).
        string? stump = cold.Medical?.Parts.FirstOrDefault(p => p.Template.LimbSlot == limb)?.Template.BoneName2;
        world.GroundItems.Drop(item, at, yaw,
            stump is { Length: > 0 } bone
                ? new FallSource(world.Characters.IdOf(slot), bone, world.Tick, cold.Appearance?.SkinToneParameter ?? default)
                : null);
    }

    /// <summary>
    /// The population's slow-world step: factions met, nests topped up, the world states refreshed and towns discovered every tick; on every
    /// <see cref="PopulationSettings.CheckEveryTicks"/> the zones, towns, squads and campaigns.
    /// </summary>
    public void SlowWorld(World world)
    {
        MeetFactions(world);
        TopUpNest(world);

        // The original answers a world state from the live unique table (FUN_1409a7e00), so the copy is taken every tick (cheap while nothing
        // changed), not at the look.
        RefreshStates(world);
        TickDiscover(world);
        if (world.Tick % _settings.CheckEveryTicks != 0) { if (_wars is not null) StepWarManager(world); return; }
        world.Uniques.SeedPrisoners(_data.Db);
        RefreshUniques(world);
        RefreshStates(world);
        RefreshZones(world);
        StepRoaming(world);
        RejoinSeparated(world);
        StepCampaigns(world);
        StepWarManager(world);
        world.ItemSlots.Step(world, _data);
    }

    /// <summary>
    /// The XZ distance at which a player character explores a town (and discovers it).
    /// </summary>
    public const float ExploreRange = 400;
    /// <summary>
    /// The XZ distance at which a player character discovers a town: doubled for a TOWN of type Town, halved for Nest and Ruins
    /// (docs/game/ui-screens.md "Map").
    /// </summary>
    public const float DiscoverRange = 8500;
    /// <summary>
    /// The discover range of a town type: twice <see cref="DiscoverRange"/> for a town, half for a nest or ruins.
    /// </summary>
    static float DiscoverRangeOf(TownType type) => DiscoverRange * (type == TownType.Town ? 2 : type is TownType.Nest or TownType.Ruins ? 0.5f : 1);

    /// <summary>
    /// Where each wild nest stood when it last loaded (null before), for the map.
    /// </summary>
    Vector2?[] _wildCentres = [];
    /// <summary>
    /// The player characters of a <see cref="Discover"/> check, with their zones.
    /// </summary>
    readonly List<(Vector2 At, ZoneCoordinate Zone)> _viewers = [];

    /// <summary>
    /// Runs <see cref="Discover"/> once a real-time second has added up.
    /// </summary>
    void TickDiscover(World world)
    {
        // Real time, not simulated (FUN_14092f6b0, Verified): the unscaled frame time adds up while the game runs (a pause adds nothing: no tick) and
        // a check runs once it reaches a second.
        _discoverClock += world.TickSeconds / MathF.Max(world.PhysicsSpeed, 0.01f);
        if (_discoverClock < DiscoverEverySeconds) return;
        _discoverClock = 0;
        Discover(world);
    }

    /// <summary>
    /// Real seconds between two town discovery checks (<c>DAT_14167c308</c>).
    /// </summary>
    public const float DiscoverEverySeconds = 1;
    /// <summary>
    /// The real time added up since the last discover check, in seconds.
    /// </summary>
    float _discoverClock;

    /// <summary>
    /// Towns and camps a player character comes within <see cref="ExploreRange"/> of become discovered and explored, and their faction known; ones
    /// not yet discovered within <see cref="DiscoverRangeOf"/> become discovered, with a "Discovered" message for a town (none for nests and camps).
    /// A character counts only for places within one zone of it: a place's own zone or, for a town, one of its <see cref="TownSite.Coverage"/> zones
    /// (docs/game/ui-screens.md "Map").
    /// </summary>
    void Discover(World world)
    {
        // Not during the new game's character creation: the original's "Discovered" messages come after CONFIRM (its world waits for the editor).
        if (!world.Player.Exists || world.Player.InCreation) return;
        var prev = world.Characters.Previous;
        var player = world.Player;
        _viewers.Clear();
        for (int i = 0; i < prev.Length; i++)
            if (prev[i].Alive && world.Characters.Cold(i) is { IsPlayer: true }) _viewers.Add(Viewer(prev[i].Position));
        AddFocusViewer(world);
        foreach (var site in _data.Sites)
        {
            if (site.Town.Type is TownType.NestMarker) continue;   // the original skips only type 8

            // an explored town is skipped (vfunc 0x340, FUN_14092f6b0:317-319)
            if (site.Index < player.Towns.Length && (player.Towns[site.Index] & TownKnowledge.Explored) != 0) continue;
            float d2 = NearestViewer(new(site.Position.X, site.Position.Z), site.Zone, site.Coverage), far = DiscoverRangeOf(site.Town.Type);
            if (d2 < ExploreRange * ExploreRange)
            {
                if (player.Know(site.Index, TownKnowledge.Discovered | TownKnowledge.Explored) && site.Index < _data.SiteFactions.Count)
                    player.Meet(_data.SiteFactions[site.Index]);
            }
            else if (d2 < far * far && player.Know(site.Index, TownKnowledge.Discovered) && site.Town.Type != TownType.Nest)
                world.Post(Loc.Tr("Discovered {1}", site.Town.UnexploredName is { Length: > 0 } unexplored ? unexplored : site.Town.Name));
        }

        var wild = WildSites;
        for (int w = 0; w < wild.Count && w < _wildCentres.Length; w++)
        {
            if (wild[w] is not { Kind: WildKind.Nest, Nest: { } nest } site || _wildCentres[w] is not { } centre) continue;
            float d2 = NearestViewer(centre, WorldLayout.ZoneOf(centre.X, centre.Y), []), far = DiscoverRangeOf(TownType.Nest);
            var flags = d2 < ExploreRange * ExploreRange
                ? TownKnowledge.Discovered | TownKnowledge.Explored
                : d2 < far * far ? TownKnowledge.Discovered : TownKnowledge.None;
            if (flags == TownKnowledge.None) continue;
            var faction = site.Faction >= 0 && site.Faction < _data.Factions.Count ? _data.Factions[site.Faction] : null;
            if ((flags & TownKnowledge.Explored) != 0 && !player.Camps.Any(c => c.Site == w && (c.Known & TownKnowledge.Explored) != 0))
                player.Meet(faction is null ? -1 : site.Faction);

            player.KnowCamp(new CampView(w, nest.Id, nest.Name, faction?.Id ?? nest.Faction ?? "", faction?.NotReal ?? false,
                new Vector3(centre.X, 0, centre.Y), flags), flags);
        }

        // A camp known from a save gets its place once its nest has loaded (never when the wild layout gives that site another nest).
        foreach (var camp in player.Camps)
        {
            if (camp.Position is null && camp.Site >= 0 && camp.Site < _wildCentres.Length && WildSites[camp.Site].Nest?.Id == camp.TownId
                && _wildCentres[camp.Site] is { } c)
                player.KnowCamp(camp with { Position = new Vector3(c.X, 0, c.Y) }, TownKnowledge.None);
        }
    }

    /// <summary>
    /// The farthest the camera focus can be from the nearest player character: <c>FUN_1406ae990</c> pulls the camera back to this (2250).
    /// </summary>
    const float CameraReach = 2250;

    /// <summary>
    /// The camera focus counts as one more viewer when it lies inside the zone grid (<c>FUN_14092f6b0</c>:166-173). Meitou's camera is unclamped, so
    /// the original's pull-in is applied first: beyond <see cref="CameraReach"/> (XZ) of the nearest player character the focus is taken that far
    /// from it, toward the focus.
    /// </summary>
    void AddFocusViewer(World world)
    {
        if (_focus is not { } f || _viewers.Count == 0) return;
        var at = new Vector2(f.X, f.Z);
        var nearest = _viewers.Select(v => v.At).MinBy(p => Vector2.DistanceSquared(p, at));
        float away = Vector2.Distance(nearest, at);
        if (away > CameraReach) at = nearest + (at - nearest) * (CameraReach / away);
        var zone = WorldLayout.ZoneOf(at.X, at.Y);
        if (zone.X is >= 0 and < WorldLayout.ZoneCount && zone.Y is >= 0 and < WorldLayout.ZoneCount) _viewers.Add((at, zone));
    }

    /// <summary>
    /// A sense update that finds this many characters in sight range also takes everyone within <see cref="CrowdReach"/> (<c>FUN_1407866d0</c>).
    /// </summary>
    const int CrowdCount = 40;
    /// <summary>
    /// The reach within which a crowded sense update also takes everyone (see <see cref="CrowdCount"/>).
    /// </summary>
    const float CrowdReach = 250;

    /// <summary>
    /// The combat system, looked up on first need (a fight target is met too).
    /// </summary>
    Combat.CombatSystem? _fights;
    /// <summary>
    /// The lock system, looked up on first need (its gate regions shorten the sight inside town walls).
    /// </summary>
    Locks.LockSystem? _locks;

    /// <summary>
    /// One of the player's characters whose sense update runs, so it meets factions: not unconscious, dead or carried (<c>FUN_140598c20</c>).
    /// </summary>
    internal static bool MeetsFactions(CharacterCold c) =>
        c is { IsPlayer: true, DiedTick: < 0, CarriedBy.IsNone: true } && c.Medical?.Incapacitated != true;

    /// <summary>
    /// The player's characters meet the factions round them (docs/game/factions-squads-towns.md "Factions met"; <c>FUN_14085a8d0</c>,
    /// <c>FUN_1408593d0</c>): on its turn (one tick in <see cref="PopulationSettings.CheckEveryTicks"/>, by slot), each one not unconscious, dead or
    /// carried meets the faction of every character within its sight range on the ground plane, seen or not, the dead, the down and animals too, and
    /// of its fight target. Not during the character creation. Inside town walls (a gate code above 0, docs/game/pathfinding.md "Gate codes") 500
    /// takes the place of the outdoor sight.
    /// </summary>
    void MeetFactions(World world)
    {
        if (!world.Player.Exists || world.Player.InCreation) return;
        var table = world.Characters;
        var prev = table.Previous;
        _fights ??= world.Systems.OfType<Combat.CombatSystem>().FirstOrDefault();
        _locks ??= world.Systems.OfType<Locks.LockSystem>().FirstOrDefault();
        int every = Math.Max(1, _settings.CheckEveryTicks);
        for (int i = (int)(world.Tick % every); i < prev.Length; i += every)
        {
            if (!prev[i].Alive || table.Cold(i) is not { } cold || !MeetsFactions(cold)) continue;
            var at = new Vector2(prev[i].Position.X, prev[i].Position.Z);
            float range = (_locks?.Regions?.CodeAt(prev[i].Position) > 0
                ? SightMap.IndoorSight * WeatherSystem.SightFactor(cold)
                : AggroSystem.RangeOf(cold, AggroSystem.OutdoorSight))
                * (0.5f + 0.5f * world.Sight.Daylight);
            int found = 0;

            // The count includes the character itself, as the grid lookup's does.
            for (int j = 0; j < prev.Length && found < CrowdCount; j++)
                if (prev[j].Alive && Vector2.DistanceSquared(at, new(prev[j].Position.X, prev[j].Position.Z)) < range * range) found++;
            if (found >= CrowdCount) range = MathF.Max(range, CrowdReach);
            for (int j = 0; j < prev.Length; j++)
            {
                if (j != i && prev[j].Alive && Vector2.DistanceSquared(at, new(prev[j].Position.X, prev[j].Position.Z)) < range * range
                    && table.Cold(j) is { } other)
                    world.Player.Meet(other.Faction);
            }

            if (_fights?.StateOf(new CharacterId(i, prev[i].Generation)) is { TargetSlot: >= 0 and var t } fight && t < prev.Length && prev[t].Alive
                && prev[t].Generation == fight.TargetGeneration && table.Cold(t) is { } target)
                world.Player.Meet(target.Faction);
        }
    }

    /// <summary>
    /// A viewer at <paramref name="at"/>: its ground-plane point and its zone.
    /// </summary>
    static (Vector2 At, ZoneCoordinate Zone) Viewer(Vector3 at) => (new(at.X, at.Z), WorldLayout.ZoneOf(at.X, at.Z));

    /// <summary>
    /// The squared XZ distance from a place to the nearest viewer in or next to its <paramref name="zone"/> or one of its <paramref name="coverage"/>
    /// zones (infinity when none).
    /// </summary>
    float NearestViewer(Vector2 place, ZoneCoordinate zone, IReadOnlyList<ZoneCoordinate> coverage)
    {
        float best = float.PositiveInfinity;
        foreach (var (at, z) in _viewers)
        {
            bool near = Near(z, zone);
            for (int i = 0; i < coverage.Count && !near; i++) near = Near(z, coverage[i]);
            if (near) best = Math.Min(best, Vector2.DistanceSquared(place, at));
        }

        return best;

        static bool Near(ZoneCoordinate a, ZoneCoordinate b) => Math.Abs(a.X - b.X) <= 1 && Math.Abs(a.Y - b.Y) <= 1;
    }

    /// <summary>
    /// The zones the original has loaded (docs/game/character-stats.md "Corpses", <c>FUN_140a0f820</c>, <c>FUN_140a0f080</c>, <c>FUN_140a0b830</c>,
    /// <c>FUN_140a0f640</c>, <b>Verified</b>), which decide what the squads, the corpses and the navmesh keep: the block of one zone each side round
    /// a centre zone, asked for again at every look (the centre moves to the camera's zone only once the camera is more than
    /// <see cref="ZoneActivation.RecentreDistance"/> from the centre zone's middle on either axis; kept
    /// <see cref="ZoneActivation.LoadedLingerSeconds"/> after), and the box round each player character (<see cref="ZoneActivation.Box"/>, a ring of
    /// one more zone when Fast zone hopping is on; kept <see cref="ZoneActivation.BoxLingerSeconds"/> after). A zone stays loaded until the latest of
    /// its requests has run out.
    /// </summary>
    void RefreshLoaded(World world, Vector3 camera)
    {
        bool move = _loadCentre is not { } c
            || MathF.Abs(camera.X - ZoneActivation.CentreOf(c).X) > ZoneActivation.RecentreDistance
            || MathF.Abs(camera.Z - ZoneActivation.CentreOf(c).Y) > ZoneActivation.RecentreDistance;
        if (move) _loadCentre = WorldLayout.ZoneOf(camera.X, camera.Z);
        long now = world.Tick;
        long until = now + (long)Math.Ceiling(ZoneActivation.LoadedLingerSeconds * LingerSpeed(world) / world.TickSeconds);
        var centre = _loadCentre!.Value;
        for (int x = centre.X - 1; x <= centre.X + 1; x++)
        {
            for (int y = centre.Y - 1; y <= centre.Y + 1; y++)
                Ask(new ZoneCoordinate(x, y), until);
        }

        long boxUntil = now + (long)Math.Ceiling(ZoneActivation.BoxLingerSeconds * LingerSpeed(world) / world.TickSeconds);
        int ring = _settings.FastZoneHopping ? 1 : 0;
        _boxZones.Clear();
        foreach (var p in _players) ZoneActivation.AddZonesAround(p, ring, _boxZones);

        // A platoon of the player's faction asks for the box round its place as a player character does, unloaded (FUN_1407ebf30) or loaded
        // (FUN_1404ffad0, both call FUN_140a0f640), so it is made and stays active.
        if (world.Platoons.Count > 0 && PlayerFactionOf(world) is >= 0 and var player)
        {
            foreach (var p in world.Platoons.All)
                if (p.Faction == player) ZoneActivation.AddZonesAround(new Vector3(p.Position.X, 0, p.Position.Y), ring, _boxZones);
        }

        foreach (var zone in _boxZones) Ask(zone, boxUntil);
        AskTownZones();
        List<ZoneCoordinate>? gone = null;
        foreach (var (zone, t) in _loadedUntil) if (t < now) (gone ??= []).Add(zone);
        if (gone is not null) foreach (var zone in gone) _loadedUntil.Remove(zone);
        world.LoadedZones = Ready(_loadedUntil.Keys);
    }

    /// <summary>
    /// The requested zones that are also ready (<see cref="ZoneGate"/>: the host's navmesh for it exists; none set: all): the zones the original
    /// flags loaded (<c>FUN_140a0f820</c>:66-96 sets the loaded flags only once every zone of the load list is accepted, state 4 to 5,
    /// <b>Verified</b>). Characters, corpses and zone content follow this set; the squads' own relevance and the host's navmesh loading follow the
    /// requested one.
    /// </summary>
    HashSet<ZoneCoordinate> Ready(IEnumerable<ZoneCoordinate> requested) =>
        ZoneGate is { } gate ? [.. requested.Where(z => gate(z))] : [.. requested];

    /// <summary>
    /// A loaded town asks for every zone of its list, its own and its coverage, with no ring and the largest timer among those already loaded
    /// (<c>FUN_140929b20</c>, <b>Verified</b>), so the zones of a town lapse together.
    /// </summary>
    void AskTownZones()
    {
        foreach (var site in _data.Sites)
        {
            if (site.Coverage.Count == 0 || _states[site.Index].Status is not (SiteStatus.Loaded or SiteStatus.Loading)) continue;
            long latest = _loadedUntil.GetValueOrDefault(site.Zone, -1);
            foreach (var zone in site.Coverage) latest = Math.Max(latest, _loadedUntil.GetValueOrDefault(zone, -1));
            if (latest < 0) continue;
            Ask(site.Zone, latest);
            foreach (var zone in site.Coverage) Ask(zone, latest);
        }
    }

    /// <summary>
    /// Whether one of the site's coverage zones is active.
    /// </summary>
    bool CoverageActive(TownSite site)
    {
        foreach (var zone in site.Coverage) if (_active.Contains(zone)) return true;
        return false;
    }

    /// <summary>
    /// The zone lingers count real seconds, not game time (<c>FUN_140a0b830</c>, <b>Verified</b>: the timers drop by the unscaled frame time): at
    /// game speed s game time runs s times faster than the clock, so L real seconds are L x s seconds of ticks. A paused world runs no ticks, so its
    /// zones simply stay.
    /// </summary>
    static float LingerSpeed(World world) => world.PhysicsSpeed > 0 ? world.PhysicsSpeed : 1;

    /// <summary>
    /// Keeps a zone of the grid loaded until <paramref name="until"/>, unless a later request already does.
    /// </summary>
    void Ask(ZoneCoordinate zone, long until)
    {
        if (!zone.IsInsideGrid) return;
        if (!_loadedUntil.TryGetValue(zone, out long was) || was < until) _loadedUntil[zone] = until;
    }

    /// <summary>
    /// Works out the active zones from the player's characters and the camera focus (<see cref="RefreshLoaded"/>), then loads the towns in them and
    /// unloads the others after their grace, and steps the wild sites.
    /// </summary>
    void RefreshZones(World world)
    {
        // The player's characters ask for the box round them (the player's faction, docs/game/character-stats.md "Corpses"); the camera focus centres
        // the block, and alone it does without a player.
        _players.Clear();
        if (world.Player.Exists)
        {
            var prev = world.Characters.Previous;
            for (int i = 0; i < prev.Length; i++)
                if (prev[i].Alive && world.Characters.Cold(i) is { IsPlayer: true }) _players.Add(prev[i].Position);
        }

        if (_players.Count == 0 && _focus is null) return;
        RefreshLoaded(world, _focus ?? _players[0]);
        (_active, _spareActive) = (_spareActive, _active);
        _active.Clear();
        foreach (var zone in _loadedUntil.Keys) _active.Add(zone);
        world.ActiveZones = world.LoadedZones;   // the ready ones (RefreshLoaded)
        world.VisitedZones.UnionWith(world.ActiveZones!);
        long grace = (long)Math.Ceiling(_settings.UnloadGraceSeconds / world.TickSeconds);
        long townGrace = (long)Math.Ceiling(_settings.TownUnloadGraceSeconds / world.TickSeconds);
        foreach (var site in _data.Sites)
        {
            var state = _states[site.Index];
            bool wanted = _active.Contains(site.Zone);
            switch (state.Status)
            {
                case SiteStatus.Unloaded when wanted:
                    Load(world, site, state);
                    break;
                case SiteStatus.Loaded or SiteStatus.Loading when wanted:
                    state.UnloadAtTick = -1;
                    break;
                case SiteStatus.Loaded or SiteStatus.Loading when CoverageActive(site):
                    state.UnloadAtTick = -1;
                    break;
                case SiteStatus.Loaded or SiteStatus.Loading:
                    if (state.UnloadAtTick < 0) state.UnloadAtTick = world.Tick + townGrace;
                    if (world.Tick >= state.UnloadAtTick) Unload(world, site.Index, state);
                    break;
            }
        }

        StepWild(world, grace);
    }

    /// <summary>
    /// Whether a living member of one of a wild site's squads stands in an active zone (a nest then stays loaded). A town does not use it: its squads
    /// are platoons that unload by their own place (<see cref="KeepLoaded"/>).
    /// </summary>
    bool AnyMemberActive(World world, SiteState state)
    {
        var table = world.Characters;
        foreach (int id in state.Squads)
        {
            if (world.Squads.Find(id) is not { } squad) continue;
            foreach (var m in squad.Members)
            {
                if (table.TryResolveNext(m, out int slot) && table.IsAlive(m)
                    && _active.Contains(WorldLayout.ZoneOf(table.Previous[slot].Position.X, table.Previous[slot].Position.Z))) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a zone may be loaded now: the host holds a town back until its navmesh is ready, so residents are not placed inside buildings of an
    /// open-ground stand-in (null: always).
    /// </summary>
    public Func<ZoneCoordinate, bool>? ZoneGate { get; set; }

    /// <summary>
    /// Starts loading a town: its replacement or held squads settled, its residents built (on the build thread when there is one, else made at once);
    /// a zone gate that says no leaves it for the next look.
    /// </summary>
    void Load(World world, TownSite site, SiteState state)
    {
        if (ZoneGate is { } gate && !gate(site.Zone)) return;   // retried at the next look
        state.Status = SiteStatus.Loading;
        state.LoadId++;
        long loadId = state.LoadId;
        ulong seed = world.Seed;
        var walk = world.Walkability;
        EnsurePools(world);

        // A town made once (its pools say so) never makes residents again; its own squads that lost too many are made afresh (docs/simulation.md
        // "Off-screen tier"). A town that has just been replaced makes the replacement's residents, as the original treats its zone as fresh; the
        // squads it had stay.
        bool replaced = Replace(world, site);
        bool held = !replaced && (HoldsSquads(world, site.Index) || PoolsOf(site.Index) is { Started: true });
        if (held) CheckRegeneration(world, site.Index);
        else if (!replaced) DropUnmade(world, site.Index);

        // Taken here, not on the build thread: the buildings the player owns house no residents (docs/game/factions-squads-towns.md "Town residents").
        var owned = world.Fixtures.PlayerOwnedIn(_data, site.Index, world.Player.Faction >= 0 ? world.Player.Faction : _data.PlayerFaction);
        if (_work is null)
        {
            Spawn(world, BuildTown(site, loadId, seed, walk, held, owned));
            state.Status = SiteStatus.Loaded;
        }
        else
        {
            _work.Add(() => _built.Enqueue(BuildTown(site, loadId, seed, walk, held, owned)));
        }
    }

    /// <summary>
    /// Whether towns are being loaded behind the loading screen (they keep their TOWN: no override is checked then).
    /// </summary>
    bool _loadingScreen;

    /// <summary>
    /// The original's <c>ZoneManager::checkForRepopulateTown</c> at a town's load (docs/game/factions-squads-towns.md "Town overrides"): not behind
    /// the loading screen, nor for a town of the player's faction; of the current TOWN's <c>override town</c> list, the entries whose every <c>world
    /// state</c> holds, weighted by how many they have (none: never), other than the replacement in place; the heaviest, the first of equals, takes
    /// the town's place. A town replaced whose new residents have not been made yet (the original's <c>updatedTownOverrides</c>) answers yes again
    /// without looking. Either way its zone is reset (<see cref="ResetZone"/>). Whether the town is to be made afresh.
    /// </summary>
    bool Replace(World world, TownSite site)
    {
        if (_loadingScreen || site.Index < _data.SiteFactions.Count && _data.SiteFactions[site.Index] is >= 0 and var f
            && f == PlayerFactionOf(world))
            return false;

        var state = _states[site.Index];
        if (state.Repopulate)
        {
            ResetZone(world, site);
            return true;
        }

        if (site.Town.OverrideTown.Count == 0) return false;
        TownData? best = null;
        int weight = 0;
        foreach (var link in site.Town.OverrideTown)
        {
            if (_data.Db.Find(link.Id) is not { Type: FcsRecordType.Town } record || record.StringId == site.Replacement?.Id) continue;
            var candidate = TownData.From(record);
            var states = candidate.WorldState.Where(w => _data.Db.Find(w.Id) is { Type: FcsRecordType.WorldEventState }).DistinctBy(w => w.Id).ToList();
            if (states.Count > weight && states.All(w => IWorldStates.Passes(States, _data.Db, w.Id, w.V0))) (best, weight) = (candidate, states.Count);
        }

        if (best is null) return false;
        _data.SetReplacement(site.Index, best);
        state.Repopulate = true;
        ResetZone(world, site);
        if (_townLog.Count >= 256) _townLog.RemoveAt(0);
        _townLog.Add($"{site.Town.Name}: now {best.Name}");
        return true;
    }

    /// <summary>
    /// What the original drops from a replaced town's zone file (<c>FUN_1409fe340</c>, docs/game/factions-squads-towns.md "Town overrides"): the
    /// state of the town's buildings in that zone that are not the player's (they come back as the level has them, under the town's faction now), the
    /// item slots' timers there (due at once), and every loose item in the zone. The town's buildings in other zones change owner only.
    /// </summary>
    void ResetZone(World world, TownSite site)
    {
        world.Fixtures.TownReplaced(world, _data, site.Index);
        foreach (int id in world.GroundItems.All.Where(g => WorldLayout.ZoneOf(g.Position.X, g.Position.Z) == site.Zone).Select(g => g.Id).ToList())
            world.GroundItems.Take(id);

        world.ItemSlots.ResetZone(site.Zone);
    }

    /// <summary>
    /// The town leaves the world: each squad it made becomes a pinned platoon that keeps its characters (<see cref="StoredSquad"/>), so the town is
    /// made of them next time and a save writes them (docs/simulation.md "Population"). A member carried off or imprisoned is split off first
    /// (<see cref="SplitCaptives(World, Squad, SplitFrom)"/>). A squad with no one alive left is dropped.
    /// </summary>
    void Unload(World world, int site, SiteState state)
    {
        foreach (int id in state.Squads)
        {
            if (world.Squads.Find(id) is not { } squad) continue;

            // A member carried off or jailed stays in the world as a squad of its own (FUN_14079c1c0), before the squad is placed by the rest.
            if (squad.Members.Count >= 2) SplitCaptives(world, squad, new SplitFrom(site, squad.TemplateId, squad.Faction, 0, squad.Pinned));
            if (KeepLoaded(world, site, squad)) continue;
            var stored = new StoredSquad { Name = squad.Name, HomeBuilding = squad.HomeBuilding, Shop = squad.Shop,
                NextHomeFillTick = squad.NextHomeFillTick, Package = squad.Ai?.Current?.Id, Ai = Saving.AiSave.Of(squad.Ai) ?? squad.SavedAi,
                Memory = Memory?.Take(squad.Id) };
            var at = Store(world, squad, stored);
            world.Squads.Remove(id);

            // A squad with no one alive left is declared dead, unless its template `regenerates` and its town lives: then it is made afresh next
            // time (CM_REFRESH).
            bool refresh = stored.Members.Count == 0 && Regenerates(squad.TemplateId, site);
            if (stored.Members.Count == 0 && !refresh)
            {
                if (squad.Shop is { } shop) Trading.TradeSystem.GiveBack(world, shop);
                continue;
            }

            world.Platoons.Add(new Platoon
            {
                Id = world.Platoons.NextId(),
                Origin = site,
                TemplateId = squad.TemplateId,
                Faction = squad.Faction,
                Key = 0,
                Size = Math.Max(squad.SpawnSize, stored.Members.Count),
                Position = at,
                Money = squad.Money,
                Package = stored.Package,
                Stored = stored,
                Regenerate = refresh,
                IntactFlag = squad.Ai?.Intact ?? true,
            });
        }

        Reset(state);
    }

    /// <summary>
    /// A town squad with a living member in the loaded box of an active zone is not stored with its town: it stays in the world as a pinned platoon
    /// that unloads on its own, as the original checks each platoon by its place (<c>FUN_1404febd0</c>, after 4 s outside every loaded zone; a zone's
    /// unload touches no platoon, <c>FUN_1409fe980</c>), so a squad chasing the player away from home does not vanish with its town. One carrying a
    /// player character is kept wherever it is (<see cref="SquadCarriesPlayer"/>). True when it was kept.
    /// </summary>
    bool KeepLoaded(World world, int site, Squad squad)
    {
        var at = PlatoonPosition(world, squad, new Vector2(squad.Position.X, squad.Position.Z));
        if (!ZoneActivation.InLoadedBox(at.X, at.Y, _active) && !SquadCarriesPlayer(world, squad)) return false;
        var stored = new StoredSquad { Name = squad.Name, HomeBuilding = squad.HomeBuilding, Shop = squad.Shop,
            NextHomeFillTick = squad.NextHomeFillTick, Package = squad.Ai?.Current?.Id, Ai = Saving.AiSave.Of(squad.Ai) ?? squad.SavedAi };
        var platoon = new Platoon
        {
            Id = world.Platoons.NextId(),
            Origin = site,
            TemplateId = squad.TemplateId,
            Faction = squad.Faction,
            Key = 0,
            Size = Math.Max(squad.SpawnSize, squad.Members.Count),
            Position = at,
            Money = squad.Money,
            Package = stored.Package,
            Stored = stored,
            IntactFlag = squad.Ai?.Intact ?? true,
            State = PlatoonState.Loaded,
            SquadId = squad.Id,
        };
        squad.PlatoonId = platoon.Id;
        squad.Pinned = true;
        world.Platoons.Add(platoon);
        return true;
    }

    /// <summary>
    /// A wild site leaves the world: its squads are removed (the population makes them again).
    /// </summary>
    void Drop(World world, SiteState state)
    {
        var table = world.Characters;
        foreach (int id in state.Squads)
        {
            if (world.Squads.Find(id) is not { } squad) continue;
            ReleaseUniques(world, squad);
            foreach (var m in squad.Members) RemoveMember(world, m);
            world.Squads.Remove(id);
        }

        Reset(state);
    }

    /// <summary>
    /// Resets a site's state to unloaded with no squads (a build still in flight is dropped when it arrives).
    /// </summary>
    static void Reset(SiteState state)
    {
        state.Squads.Clear();
        state.Pending = false;
        state.Made = 0;
        state.Status = SiteStatus.Unloaded;
        state.UnloadAtTick = -1;
        state.LoadId++;   // a build still in flight is dropped when it arrives
    }

    /// <summary>
    /// Whether a loaded save holds squads of the town: a pinned saved platoon of the site, made now or not, other than a never-made stand-in
    /// (<see cref="Platoon.Unmade"/>). They are then the town's whole population and it makes no residents or bar squads of its own
    /// (<see cref="BuildTown"/>, docs/simulation.md "Saves").
    /// </summary>
    bool HoldsSquads(World world, int site)
    {
        foreach (var p in world.Platoons.All)
            if (p.Pinned && p.Origin == site && !p.Unmade) return true;
        return false;
    }

    /// <summary>
    /// A town that makes its own residents at its load (not held: its pool not <c>started</c> and no other squad of the save's) drops its never-made
    /// stand-ins, which would double them; their records stay in the save as they are (engine choice: an original save of a made town has
    /// <c>started</c>, so this is only a save without pool state).
    /// </summary>
    static void DropUnmade(World world, int site)
    {
        foreach (var p in world.Platoons.All.ToList())
        {
            if (p is { Unmade: true, State: PlatoonState.Unloaded } && p.Origin == site)
            {
                if (p.Saved is { } sp) sp.PlatoonId = -1;
                world.Platoons.Remove(p.Id);
            }
        }
    }

    /// <summary>
    /// Takes a built town into the world: its fixtures, item slots and squads.
    /// </summary>
    void Spawn(World world, BuiltTown town)
    {
        var site = _data.Sites[town.Site];
        var state = _states[town.Site];
        state.Repopulate = false;
        float radius = site.Town.SizeRadius * site.Town.TownRadiusMult;

        // The town's doors, cages, containers and beds, once (they stay registered when the town unloads).
        world.Fixtures.RegisterSite(world, _data, town.Site);
        world.ItemSlots.RegisterSite(world, _data, town.Site);
        StandTownCamp(world, site);

        // Each home is set up for its template before its residents are made (FUN_14057f1f0). The platoon is made for every home, members or not
        // (FUN_14057f4c0 stamps its template's designation on the building, FUN_140583a10 then stocks a home whose template made nothing).
        var trade = world.SystemOf<Trading.TradeSystem>();
        int townFaction = site.Town.Faction is { } townFactionId ? _data.FactionIndex(townFactionId) : -1;
        var stocked = town.Squads.Where(s => s.Home is not null).Select(s => s.Home!.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (home, template) in town.Homes)
        {
            world.Fixtures.NoteSetup(home.Key, template);
            world.Fixtures.NoteMade(home.Key, template);
            if (!stocked.Contains(home.Key)) trade?.StockEmptyHome(world, home, template, town.Site, townFaction);
        }

        // A town made counts as visited in every zone of its buildings (engine choice: its residents and its buildings' locks are saved whole,
        // docs/formats/save.md "Zone files"), but for the centre zone of another town: a file there would tell the original that town was made, with
        // no residents saved for it.
        foreach (var b in site.Buildings)
            if (b.Zone == site.Zone || !_centres.Contains(b.Zone)) world.VisitedZones.Add(b.Zone);
        var centre = new Vector2(site.Position.X, site.Position.Z);
        foreach (var made in town.Squads)
        {
            // A gate has no nodes and its origin is inside the wall, ~110 from the opening: its residents are made at their gate guard's post
            // (engine choice).
            var built = made.Home is { IsGate: true } gate && !HasNode(gate) && Ai.AiSystem.PostOf(world, gate, centre) is { } post
                && Ai.AiSystem.FrontOf(post) is var front ? made with { Position = new Vector2(front.X, front.Z), NearY = front.Y + SpotLift } : made;
            var squad = SpawnSquad(world, built, site, radius);
            state.Squads.Add(squad.Id);
            MakePrisoners(world, built, squad, site, radius, state);
            MakeSlaves(world, built, squad, site, radius, state);
        }

        if (town.Squads.Count > 0) MadeTown(world, town.Site, town.Squads.Where(s => s.Home is not null).Select(s => s.Members.Count));
        GiveArtifacts(world, town);
        int housed = town.Squads.Count(s => s.Home is not null);
        if (_townLog.Count >= 256) _townLog.RemoveAt(0);   // nobody reads it in a headless world
        _townLog.Add($"{site.Town.Name}: {town.Squads.Count} squad(s), {town.Squads.Sum(s => s.Members.Count)} characters; {housed} at home in " +
            $"{town.Squads.Where(s => s.Home is not null).Select(s => s.Home).Distinct().Count()} of " +
            $"{site.Buildings.Count(b => b.CanHouse)} houses, " +
            $"{town.Squads.Count - housed} elsewhere" +
            (housed < town.Squads.Count ? $" ({string.Join(", ", town.Squads.Where(s => s.Home is null).Select(s => s.Plan.Template.Name))})" : ""));
    }

    /// <summary>
    /// A housed squad's <c>prisoners</c> (the squad factory <c>FUN_140583a10</c>, <b>Observed</b>; docs/simulation.md "Cages, beds and locks"): only
    /// with a home building, each entry a squad of its template's <c>faction</c> (else the owner squad's), each member imprisoned in a random free
    /// cage of the home, its lock set. <b>Engine choices</b>: members beyond the free cages are not made (the original drops one and stops placing);
    /// the prisoners are held without a sentence (<see cref="CharacterCold.Kidnapped"/>), so no release round frees them and no bail is offered (the
    /// original's served test for them is <b>Unknown</b>).
    /// </summary>
    void MakePrisoners(World world, BuiltSquad owner, Squad squad, TownSite site, float radius, SiteState state)
    {
        var links = owner.Plan.Template.Prisoners;
        if (links.Count == 0 || owner.Home is not { } home) return;
        var table = world.Characters;
        var cages = world.Fixtures.OfBuilding(home.Key).Where(f => f.Kind == Locks.FixtureKind.Cage && Locks.FixtureRegistry.IsFree(table, f)).ToList();
        ulong homeKey = Rng.StableHash(home.Key);
        int picks = 0;
        for (int e = 0; e < links.Count && cages.Count > 0; e++)
        {
            if (_data.Db.Find(links[e].Id) is not { } record || !SquadFactory.IsTemplate(record)) continue;
            var faction = (uint)squad.Faction < (uint)_data.Factions.Count ? _data.Factions[squad.Faction] : null;
            ulong key = Rng.Mix(SeedSalts.PrisonerSquad ^ homeKey ^ (ulong)e << 48);
            var at = new Vector2(cages[0].Position.X, cages[0].Position.Z);
            if (BuildOne(SquadTemplate.From(record), squad.Faction, faction, key, world.Seed, at) is not { } built) continue;
            if (built.Members.Count > cages.Count) built = built with { Members = built.Members.Take(cages.Count).ToList() };
            var prisoners = SpawnSquad(world, built with { NearY = cages[0].Position.Y + SpotLift }, site, radius);
            state.Squads.Add(prisoners.Id);
            foreach (var id in prisoners.Members)
            {
                int pick = Rng.Int(Rng.Hash(world.Seed, homeKey, RngPurpose.Spawn, SeedSalts.PrisonerCage + (ulong)picks++), cages.Count);
                var cage = cages[pick];
                cages.RemoveAt(pick);
                if (world.Fixtures.Imprison(world, id, cage)) table.Cold(id.Slot)!.Kidnapped = true;
            }
        }
    }

    /// <summary>
    /// A squad's <c>slaves</c> (the squad factory's recursive slave squads, role 4; docs/game/factions-squads-towns.md "Squad factory"): each planned
    /// slave squad is made as a squad of its template's <c>faction</c> (else the owner's), every member a slave (state 1) of the owner squad's
    /// faction, shaven and chained (<b>Verified</b>). <b>Engine choices</b>: they are made at the owner squad's spot; with a home building, each goes
    /// into a random free cage of it (their package's goal is Slave get in Cage, which Meitou does not act out), those left over stand by the owner
    /// squad. Each obeys the owner squad (job 187: its Slave AI Goals, <see cref="CharacterCold.SlaveMaster"/>).
    /// </summary>
    void MakeSlaves(World world, BuiltSquad owner, Squad squad, TownSite site, float radius, SiteState state)
    {
        if (owner.Plan.Slaves.Count == 0) return;
        var table = world.Characters;
        var cages = owner.Home is { } home
            ? world.Fixtures.OfBuilding(home.Key).Where(f => f.Kind == Locks.FixtureKind.Cage && Locks.FixtureRegistry.IsFree(table, f)).ToList() : [];
        ulong ownerKey = Rng.StableHash(owner.Home?.Key ?? squad.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var ownerFaction = (uint)squad.Faction < (uint)_data.Factions.Count ? _data.Factions[squad.Faction] : null;
        int n = 0, picks = 0;
        foreach (var plan in owner.Plan.Slaves)
        {
            if (plan.Members.Count == 0) continue;
            int faction = plan.Template.Faction is { } tf && _data.FactionIndex(tf) is var fi and >= 0 ? fi : squad.Faction;
            var members = new List<BuiltMember>();
            foreach (var m in plan.Members)
            {
                var look = _data.Appearances?.Create(m.RecordId, ownerFaction?.Id,
                    (int)(Rng.Hash(world.Seed, ownerKey, RngPurpose.Spawn, SeedSalts.SlaveLook + (ulong)n++) & 0x7FFFFFFF));
                members.Add(new BuiltMember(m, look));
            }

            var slaves = SpawnSquad(world, new BuiltSquad(plan with { Slaves = [] }, owner.Position, faction, members, owner.NearY), site, radius);
            state.Squads.Add(slaves.Id);
            foreach (var id in slaves.Members)
            {
                if (table.Cold(id.Slot) is not { } c) continue;

                // Every member, the leader too, gets member type 4 after the recursive squad is made (FUN_140583a10); the squad's leader pointer stays.
                (c.SlaveState, c.SlaveOwner, c.SlaveMaster, c.Role) = (1, squad.Faction, squad.Id, (int)SquadRole.Slave);

                // Then shaven and chained (FUN_140583a10: shaveHead, setChainedMode), so the owner's watchers see a processed slave and leave it
                // be (#356).
                if (c.Fighter is not { IsAnimal: true }) c.Shaved = true;
                _assembly.Chain(c);
                _assembly.MatchShave(c);
                if (cages.Count == 0) continue;
                int pick = Rng.Int(Rng.Hash(world.Seed, ownerKey, RngPurpose.Spawn, SeedSalts.SlaveCage + (ulong)picks++), cages.Count);
                world.Fixtures.Imprison(world, id, cages[pick]);
                cages.RemoveAt(pick);
            }
        }
    }

    /// <summary>
    /// The last 256 lines of the town and campaign log.
    /// </summary>
    readonly List<string> _townLog = [];
    /// <summary>
    /// A line for each town whose squads were made since the last call (for the host's log; not part of the state).
    /// </summary>
    public List<string> TakeTownLog()
    {
        var list = new List<string>(_townLog);
        _townLog.Clear();
        return list;
    }

    /// <summary>
    /// Makes a built squad in the world: its members as characters at their spots, its home noted for its template.
    /// </summary>
    Squad SpawnSquad(World world, BuiltSquad built, TownSite? site, float radius, bool player = false, int platoonId = -1, bool idle = false,
        float yaw = 0, bool pinned = false, HashSet<int>? gated = null, bool wild = false)
    {
        var table = world.Characters;
        var squad = NewSquad(world, built, site, radius, platoonId, pinned, wild);
        if (built.Home is { } residence0)
        {
            world.Fixtures.NoteMade(residence0.Key, built.Plan.Template);
            world.Fixtures.NoteAssigned(residence0.Key, built.Plan.Template);
        }

        var builtMembers = idle || player && gated is null ? built.Members : GateUniques(world, built, squad.Id, gated);
        var leaderPlan = builtMembers.FirstOrDefault(m => m.Plan.Role == SquadRole.Leader)?.Plan;
        bool hasLeader = leaderPlan is not null;
        foreach (var member in builtMembers)
        {
            var id = SpawnMember(world, built, squad, member, hasLeader, player, idle, yaw, site);
            squad.Members.Add(id);
            if (member.Plan.Role == SquadRole.Leader && squad.Leader.IsNone) squad.Leader = id;
        }

        squad.SpawnSize = squad.Members.Count;
        if (squad.Leader.IsNone) squad.Leader = SquadRegistry.Successor(squad.Members, table);
        world.Squads.Add(squad);
        if (SpawnTrace is { } trace && !player && !idle) TraceSpawn(world, trace, squad, built, site, builtMembers, leaderPlan);

        return squad;
    }

    /// <summary>
    /// Appends a made squad to <paramref name="trace"/> (<see cref="SpawnTrace"/>), with its distance to the nearest player character (-1: none).
    /// </summary>
    void TraceSpawn(World world, List<SpawnTraceEntry> trace, Squad squad, BuiltSquad built, TownSite? site, List<BuiltMember> builtMembers,
        MemberPlan? leaderPlan)
    {
        float playerDistance = PlayerSpots(world)
            .Select(a => Vector2.Distance(a, built.Position))
            .DefaultIfEmpty(-1)
            .Min();
        string leader = leaderPlan?.RecordId ?? builtMembers.FirstOrDefault()?.Plan.RecordId ?? "";
        trace.Add(new SpawnTraceEntry(world.Tick, site is not null ? "town" : "wild", squad.Id, squad.TemplateId, squad.Name, squad.Faction,
            squad.Members.Count, built.Position.X, built.Position.Y, playerDistance, leader));
    }

    /// <summary>
    /// A squad made by the population (debug aid, the control channel's <c>spawn-trace</c>): when, from where, which template and where.
    /// </summary>
    public readonly record struct SpawnTraceEntry(long Tick, string Source, int SquadId, string TemplateId, string Name, int Faction, int Members,
        float X, float Z, float PlayerDistance, string LeaderRecord);

    /// <summary>
    /// When set, every squad the population makes (not the player's, not the sandbox's) is appended here (debug aid, not part of the state).
    /// </summary>
    public List<SpawnTraceEntry>? SpawnTrace { get; set; }

    /// <summary>
    /// Makes one fresh member at its planned spot, with its body, initial hunger and slave state, bounties and expanded name.
    /// </summary>
    CharacterId SpawnMember(World world, BuiltSquad built, Squad squad, BuiltMember member, bool hasLeader, bool player, bool idle, float yaw,
        TownSite? site)
    {
        var table = world.Characters;
        var plan = member.Plan;
        var race = _assembly.RaceOf(plan.RecordId, member.Appearance);
        var record = _data.Db.Find(plan.RecordId);
        var hot = SpawnMotion(world, built, squad, plan, race, hasLeader, player, idle, yaw);
        var cold = new CharacterCold
        {
            Name = SpawnName(world, record, member.Appearance, squad.Id, plan.Index) ?? record?.Name ?? plan.RecordId,
            Faction = squad.Faction,
            Appearance = member.Appearance,
            RecordId = plan.RecordId,
            SquadId = squad.Id,
            Role = (int)plan.Role,
            IsPlayer = player,
            Orders = player ? Orders.PlayerDefaults : CharacterOrders.None,
            FootprintRadius = race?.GetFloat("pathfind footprint radius") ?? 0,
        };
        _assembly.Attach(table.PeekNextId(), cold, ref hot, record, race, world.Seed, plan.IsAnimal ? plan.Age : 1);

        // The fresh-character factory (FUN_140581770) gives organic members of a malnourished template 170 hunger, not its free-food floor.
        if (plan.Malnourished && cold is { Race.IsRobot: false, Medical: { } medical }) medical.Hunger = 1.7f;

        ApplyRecordSlave(world, cold, record, site);

        // A new NPC's bounties (economy.md 11.2; engine: not the player's characters, nor the sandbox's idle spawns).
        if (!player && !idle) cold.Bounties = Crime.SpawnBounties.Roll(_data, record, world.Seed, table.PeekNextId(), world.Tick);
        var id = table.Spawn(hot, cold, world.Tick);

        // The name's text tokens, once the character exists (FUN_14062b210 end): a mod's "Trader /GENNAME/".
        if (cold.Name.Contains('/') && world.SystemOf<Dialogue.DialogueSystem>() is { } talk) cold.Name = talk.ExpandName(world, id, cold.Name);
        if (UniqueCharacters.IsUnique(record)) world.Uniques.Bind(plan.RecordId, id);

        return id;
    }

    /// <summary>
    /// A fresh member's position and initial movement, before its body supplies its speed.
    /// </summary>
    CharacterHot SpawnMotion(World world, BuiltSquad built, Squad squad, MemberPlan plan, GameRecord? race, bool hasLeader, bool player,
        bool idle, float yaw)
    {
        var walk = world.Walkability;
        var (x, z, standY) = StandSpot(world, walk, built, plan, squad.Id);

        return new CharacterHot
        {
            Position = new Vector3(x, Ground(walk, x, z, standY), z),
            Yaw = yaw,
            Health = 100,
            Task = (byte)(player || idle
                ? CharacterTask.Idle
                : plan.Role == SquadRole.Leader || !hasLeader
                    ? CharacterTask.Wander
                    : CharacterTask.Follow),
            Mode = player ? SpeedMode.Free : SpeedMode.Walk,
            MaxSpeed = CharacterAssembly.StandInMaxSpeed,
            WalkSpeed = CharacterAssembly.WalkSpeed(race),
            TaskTime = 0.5f + Rng.Float(Rng.Hash(world.Seed, (ulong)squad.Id << 8 | (uint)plan.Index, RngPurpose.Think)) * 3,
        };
    }

    /// <summary>
    /// A new squad for the built one, at its position, homed round the town (else its own spot) and in its home building.
    /// </summary>
    Squad NewSquad(World world, BuiltSquad built, TownSite? site, float radius, int platoonId, bool pinned, bool wild)
    {
        var walk = world.Walkability;
        return new Squad
        {
            Id = world.Squads.NextId(),
            TemplateId = built.Plan.Template.Id,
            Name = built.Plan.Template.Name,
            Faction = built.Faction,
            Town = site?.Index ?? -1,
            PlatoonId = platoonId,
            Pinned = pinned,
            Wild = wild,
            Position = new Vector3(built.Position.X, Ground(walk, built.Position.X, built.Position.Y, built.NearY), built.Position.Y),
            HomeCentre = site is null ? built.Position : new Vector2(site.Position.X, site.Position.Z),
            HomeRadius = Math.Max(radius * 0.8f, 40),
            HomeBuilding = built.Home,
        };
    }

    /// <summary>
    /// Where a member is made: its offset from the squad, a resident at a node of its own, the squad's spot when that is not walkable;
    /// inside, on the floor (the height to stand at is returned).
    /// </summary>
    (float X, float Z, float? StandY) StandSpot(World world, IWalkability walk, BuiltSquad built, MemberPlan plan, int squadId)
    {
        float x = built.Position.X + plan.Offset.X, z = built.Position.Y + plan.Offset.Y;
        float? memberY = built.NearY;

        // A resident is made at a node of its own (FUN_14057f820), not round the platoon point.
        if (built.Home is { } residence && built.NearY is not null
            && MemberSpot(residence, world.Seed, (ulong)squadId << 8 | (uint)plan.Index) is { } spot)
            (x, z, memberY) = (spot.X, spot.Z, spot.Y);

        if (!(memberY is { } nearY ? walk.IsWalkable(x, z, nearY) : walk.IsWalkable(x, z)))
            (x, z, memberY) = (built.Position.X, built.Position.Y, built.NearY);

        // Inside, on the floor and not on the ground showing through a hole furniture cuts in it (the spot's height is its own plus SpotLift).
        float? standY = memberY;
        if (memberY is { } floorY)
        {
            // A building without nodes makes them at its origin, often under a raised floor: that floor first (the ground under it is near the
            // origin).
            bool atOrigin = built.Home is { } h && !HasNode(h);
            if (!atOrigin && walk.FloorNear(x, z, floorY - SpotLift, SpotLift) is { } onFloor) (x, z) = (onFloor.X, onFloor.Z);
            else if (built.Home is { } home && HomeFloor(walk, home, x, z, floorY - SpotLift) is { } inside)
                (x, z, standY) = (inside.X, inside.Z, inside.Y + SpotLift);
            else if (atOrigin && walk.FloorNear(x, z, floorY - SpotLift, SpotLift) is { } onGround) (x, z) = (onGround.X, onGround.Z);
        }

        return (x, z, standY);
    }

    /// <summary>
    /// CHARACTER <c>slave</c> (SlaveStateEnum 1 slave, 2 escaped, 3 ex-slave) on a freshly made character, whatever squad it is in and with or
    /// without an owner (<c>FUN_14062b210</c>, <b>Verified</b>; a saved character is not touched): 1, 2 and 3 are kept. The owner is the faction of
    /// the town the squad belongs to, else none (-1). The state setter adds the obedient-slave job for a player slave.
    /// </summary>
    void ApplyRecordSlave(World world, CharacterCold cold, GameRecord? record, TownSite? site)
    {
        int state = record?.GetInt("slave") ?? 0;
        if (state is not (>= 1 and <= 3)) return;
        cold.SlaveState = (byte)state;
        cold.SlaveOwner = site is not null && site.Index < _data.SiteFactions.Count ? _data.SiteFactions[site.Index] : -1;
        if (state == 1) ObedientSlave.StateChanged(world, cold, 1);
        if (state == 2) cold.RecentlyWanted = Crime.CrimeSystem.RecentlyWantedSeconds;
    }

    /// <summary>
    /// A <c>named</c> CHARACTER's random name from its look's sex's list (docs/characters.md "Names"); null: the record's name.
    /// </summary>
    string? SpawnName(World world, GameRecord? record, CharacterAppearance? look, int squadId, int index) =>
        record is not null && record.GetBool("named") && _data.Appearances is { } source
            ? source.RandomName(look?.Female ?? false, Rng.Hash(world.Seed, (ulong)squadId << 8 | (uint)index, RngPurpose.Name))
            : null;

    /// <summary>
    /// The unique-character gate (<c>FUN_1405836e0</c>, docs/game/factions-squads-towns.md "Unique characters") on the members of a squad about to be
    /// made, in the serial phase so two squads planned at once cannot both make one: a free unique is claimed and made; a claimed one (made
    /// elsewhere, dead) is replaced by its first <c>unique replacement spawn</c> (not gated, with a look rolled now; an animal keeps its look) or by
    /// nobody. The <c>animals</c> lists go through the same gate (<c>FUN_140583a10</c>). When the leader is gone the first member left leads.
    /// <paramref name="only"/>: the member indexes to gate, the rest pass (a new game's squad: only what its templates made).
    /// </summary>
    List<BuiltMember> GateUniques(World world, BuiltSquad built, int squadId, HashSet<int>? only = null)
    {
        List<BuiltMember>? gated = null;
        for (int i = 0; i < built.Members.Count; i++)
        {
            var member = built.Members[i];
            if (only is not null && !only.Contains(member.Plan.Index) || _data.Db.Find(member.Plan.RecordId) is not { } record
                || !UniqueCharacters.IsUnique(record)
                || world.Uniques.TryClaim(member.Plan.RecordId))
            {
                gated?.Add(member);
                continue;
            }

            gated ??= [.. built.Members.Take(i)];
            var replacements = record.GetReferences("unique replacement spawn");
            if (replacements.Count == 0 || _data.Db.Find(replacements[0].TargetStringId) is null) continue;
            string replacement = replacements[0].TargetStringId;
            string? faction = built.Faction >= 0 && built.Faction < _data.Factions.Count ? _data.Factions[built.Faction].Id : null;
            var look = member.Plan.IsAnimal
                ? member.Appearance
                : _data.Appearances?.Create(replacement, faction,
                    (int)(Rng.Hash(world.Seed, Rng.StableHash(replacement) ^ (ulong)squadId << 32, RngPurpose.Spawn,
                        SeedSalts.MemberLook + (ulong)i) & 0x7FFFFFFF));
            gated.Add(new BuiltMember(member.Plan with { RecordId = replacement }, look));
        }

        if (gated is null) return built.Members;
        if (gated.Count > 0 && !gated.Any(m => m.Plan.Role == SquadRole.Leader))
            gated[0] = gated[0] with { Plan = gated[0].Plan with { Role = SquadRole.Leader } };

        return gated;
    }

    /// <summary>
    /// The unique members of a platoon that leaves the world for good without being kept (despawned, or remade from its template): the living are
    /// freed to be made again, as the original's unload does for a free and well unique (<c>FUN_1409a8120</c>); with <paramref name="killed"/> (an
    /// off-screen defeat) they die instead.
    /// </summary>
    void ReleaseUniques(World world, Platoon p, bool killed = false)
    {
        if (world.Squads.Find(p.SquadId) is { } squad) ReleaseUniques(world, squad, killed);
        else if (p.Stored is { } stored)
            foreach (var m in stored.Members) ReleaseUnique(world, m.Cold.RecordId, CharacterId.None, m.Cold.Medical is { Dead: true } || killed);
        else if (p.Saved is { } saved)
        {
            foreach (var m in saved.Members)
                if (!m.Gone) ReleaseUnique(world, m.Source.RecordId, CharacterId.None, m.Dead || killed);
        }
    }

    /// <summary>
    /// The same for the characters of a squad in the world.
    /// </summary>
    void ReleaseUniques(World world, Squad squad, bool killed = false)
    {
        var table = world.Characters;
        foreach (var m in squad.Members)
        {
            if (table.TryResolveNext(m, out int slot) && table.Cold(slot) is { } cold)
                ReleaseUnique(world, cold.RecordId, m, killed || cold.DiedTick >= 0 || cold.Medical is { Dead: true }, cold.Faction);
        }
    }

    /// <summary>
    /// One member: dead, it dies in the registry; alive, its entry is erased when this character holds it (<paramref name="holder"/>, or none for one
    /// not in the world), so a copy made as another's replacement (a unique naming itself) never frees the first.
    /// </summary>
    void ReleaseUnique(World world, string recordId, CharacterId holder, bool dead, int faction = -1)
    {
        if (world.Uniques.Find(recordId) is not { } entry || !UniqueCharacters.IsUnique(_data.Db.Find(recordId))) return;
        if (!holder.IsNone && !entry.Handle.IsNone && entry.Handle != holder) return;
        world.Uniques.Left(holder);
        if (dead) world.Uniques.Died(recordId, false, faction);
        else world.Uniques.Release(recordId);
    }

    /// <summary>
    /// The unique characters in the world, at every population look (the original's per-character refresh <c>FUN_1405cf470</c> and its death and
    /// imprisonment hooks, docs/game/factions-squads-towns.md "Unique characters"): dead → 0 (killed by the player's: set by the combat commit); in a
    /// cage or restraint → 2 (player: the cage's owner is the player's); alive and carried by one of a faction neither its own nor allied → 2
    /// (player: the carrier is the player's); captured but neither held nor carried → 1, player cleared (engine summary of the original's ally and
    /// town tests); in the player's faction and alive though marked dead → 1.
    /// </summary>
    void RefreshUniques(World world)
    {
        var uniques = world.Uniques;
        if (uniques.Count == 0) return;
        var table = world.Characters;
        var relations = _data.Relations;
        int playerFaction = world.Player.Faction;
        List<CharacterId>? gone = null;
        foreach (var (recordId, e) in uniques.All)
        {
            if (e.Handle.IsNone) continue;
            if (!table.TryResolveNext(e.Handle, out int slot) || table.Cold(slot) is not { } cold)
            {
                (gone ??= []).Add(e.Handle);
                continue;
            }

            bool dead = cold.DiedTick >= 0 || cold.Medical is { Dead: true };
            if (dead)
            {
                if (e.State != UniqueState.Dead) uniques.Died(recordId, false, cold.Faction);
            }
            else if (cold.IsImprisoned)
            {
                if (e.State != UniqueState.Imprisoned)
                {
                    uniques.Captured(recordId,
                        cold.InWhat is { } cage && world.Fixtures.Find(cage) is { } f && f.Owner >= 0 && f.Owner == playerFaction);
                }
            }
            else if (!cold.CarriedBy.IsNone && table.TryResolveNext(cold.CarriedBy, out int carrierSlot) && table.Cold(carrierSlot) is { } carrier)
            {
                bool friend = carrier.Faction == cold.Faction || relations.Contains(carrier.Faction) && relations.Contains(cold.Faction)
                    && relations.IsAlly(cold.Faction, carrier.Faction);
                if (e.State == UniqueState.Alive && !friend) uniques.Captured(recordId, carrier.IsPlayer);
            }
            else if (e.State == UniqueState.Imprisoned && FreeAgain(world, cold, table.Next[slot].Position, playerFaction)
                || e.State == UniqueState.Dead && cold.IsPlayer)
                uniques.Freed(recordId);
        }

        if (gone is not null) foreach (var id in gone) uniques.Left(id);
    }

    /// <summary>
    /// State 2 returns to 1 for a character of the player's faction, or standing in a town (its <c>+0x1a0 -> +0x88</c>) that is friendly to it
    /// (<c>FUN_1405cf470</c>, <b>Verified</b>): the town's faction regards the character's faction as an ally (<c>FUN_1406b2630</c>), or it is the
    /// squad's home town, or its faction is the character's own. Not while carried or caged (the caller's branches). A free unique elsewhere stays in
    /// state 2.
    /// </summary>
    internal bool FreeAgain(World world, CharacterCold cold, Vector3 at, int playerFaction)
    {
        if (playerFaction >= 0 && cold.Faction == playerFaction) return true;
        var town = Ai.AiSystem.TownAt(_data, world, at);
        if (town.IsNone) return false;
        int owner = FactionOfTown(world, town);
        var relations = _data.Relations;
        if (owner == cold.Faction || owner >= 0 && relations.Contains(owner) && relations.Contains(cold.Faction)
            && relations.IsAlly(owner, cold.Faction))
            return true;

        return town.Site >= 0 && world.Squads.Find(cold.SquadId)?.Town == town.Site;
    }

    /// <summary>
    /// The floor at X/Z: the one under <paramref name="nearY"/> inside a building, else the ground.
    /// </summary>
    static float Ground(IWalkability walk, float x, float z, float? nearY) => nearY is { } y ? walk.GroundHeight(x, z, y) : walk.GroundHeight(x, z);

    /// <summary>
    /// Whether <see cref="Dispose"/> ran.
    /// </summary>
    bool _disposed;

    /// <summary>
    /// Stops the build thread; safe to call twice.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _work?.CompleteAdding();
        _worker?.Join();
        _work?.Dispose();
    }
}
