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
/// Zone activation and town residents (docs/game/game-loop.md "Update, threads and budget", factions-squads-towns.md 6.4). The active zones are the
/// loaded ones (<see cref="RefreshLoaded"/>): the 3 x 3 block round the camera's focus (a <see cref="FocusCommand"/>) and the box of +-340 units round each
/// player character, with a ring of one more zone when <c>Fast zone hopping</c> is on (<see cref="ZoneActivation"/>). A placed town in an active zone gets its resident squads (squad
/// factory, then characters rolled by the generator); when its zone is no longer active for <see cref="PopulationSettings.UnloadGraceSeconds"/>
/// its characters are removed (the original turns the squads into abstract "unloaded" ones, which the AI stage will do). The
/// characters are built on a background thread when <see cref="PopulationSettings.Background"/> is set and taken in at the first tick
/// after they are ready.
/// <para>Residents (docs/game/factions-squads-towns.md "Town residents"): the resident table (<see cref="ResidentEntries"/>) is housed in the town's buildings
/// and each squad made at a node of its home (<c>PopulationHomes.cs</c>); a town without building data puts each entry's v0 squads at random spots within
/// <see cref="PopulationSettings.PlacementRadiusShare"/> of the town radius round its placed position, on dry ground (engine choice). Bar squads, the roaming squads (<c>PopulationRoaming.cs</c>) and the wild, homeless squads and nests (<c>PopulationWild.cs</c>), are made here too.</para>
/// </summary>
public sealed partial class PopulationSystem : ITickSystem, IDisposable
{
    readonly PopulationData data;
    readonly PopulationSettings settings;
    readonly CharacterAssembly assembly;
    readonly SiteState[] states;
    // The zones the towns are centred in (a town is made when its zone is active).
    readonly HashSet<ZoneCoordinate> centres;
    readonly ConcurrentQueue<BuiltTown> built = new();
    readonly BlockingCollection<Action>? work;
    readonly Thread? worker;
    Vector3? focus;
    // The zones active as of the last look (the loaded ones), and the set the next look fills (swapped, so a look allocates nothing; the host reads ActiveZones within a frame only).
    HashSet<ZoneCoordinate> active = [], spareActive = [];
    // The player's characters, whose box is asked for at each look, and the zones of that box (a scratch set).
    readonly List<Vector3> players = [];
    readonly HashSet<ZoneCoordinate> boxZones = [];
    // The original's loaded zones (ZoneLoad): each zone with the tick until which its latest request keeps it loaded (RefreshLoaded).
    readonly Dictionary<ZoneCoordinate, long> loadedUntil = [];
    ZoneCoordinate? loadCentre;

    sealed class SiteState
    {
        public SiteStatus Status;
        public long UnloadAtTick = -1;
        public long LoadId;
        public readonly List<int> Squads = [];
        /// <summary>A nest's top-up is being built.</summary>
        public bool Pending;
        /// <summary>The nest rolls used since it loaded (a top-up's keys follow them).</summary>
        public int Made;
        /// <summary>Replaced, its new residents not made yet (the original's <c>updatedTownOverrides</c>; not saved, as there).</summary>
        public bool Repopulate;
    }

    public PopulationSystem(PopulationData data, PopulationSettings? settings = null)
    {
        this.data = data;
        this.settings = settings ?? new PopulationSettings();
        assembly = new CharacterAssembly(data);
        states = new SiteState[data.Sites.Count];
        for (int i = 0; i < states.Length; i++) states[i] = new SiteState();
        centres = [.. data.Sites.Select(s => s.Zone)];
        KeepRoamers = this.settings.KeepRoamers;
        OffscreenFights = this.settings.OffscreenFights;
        OffscreenHealing = this.settings.OffscreenHealing;
        CampaignAncestorsOnce = this.settings.CampaignAncestorsOnce;
        KeepWild = this.settings.KeepWild;
        ResidentSchedule = this.settings.ResidentSchedule;
        live = new WorldStates(data.Db, data.Relations);
        if (!this.settings.Background) return;
        work = [];
        worker = new Thread(() => { foreach (var job in work.GetConsumingEnumerable()) job(); }) { IsBackground = true, Name = "Meitou.Sim population" };
        worker.Start();
    }

    /// <summary>The off-screen switches (<see cref="PopulationSettings.KeepRoamers"/> and the next two), changed live by the host.</summary>
    public bool KeepRoamers { get; set; }
    public bool OffscreenFights { get; set; }
    public bool OffscreenHealing { get; set; }
    /// <summary>The campaign inheritance switch (<see cref="PopulationSettings.CampaignAncestorsOnce"/>), changed live by the host.</summary>
    public bool CampaignAncestorsOnce { get; set; }
    /// <summary>The wild-squad switch (<see cref="PopulationSettings.KeepWild"/>), changed live by the host.</summary>
    public bool KeepWild { get; set; }
    /// <summary>The residents' schedule switch (<see cref="PopulationSettings.ResidentSchedule"/>), changed live by the host.</summary>
    public bool ResidentSchedule { get; set; }

    /// <summary>Game hours a town must have been unloaded for its residents to come back where their schedule puts them (engine choice: a quick reload changes nothing).</summary>
    public const double ScheduleAfterHours = 1;

    readonly WorldStates live;
    /// <summary>The world states the population's spawns and campaigns read: the data's fixed ones when it has any, else the live ones of this world (<see cref="WorldStates"/>).</summary>
    public IWorldStates States => ReferenceEquals(data.States, IWorldStates.None) ? live : data.States;

    /// <summary>The live world states take the unique table and the player's faction (serial; every tick, again after a look's unique refresh, and before the loading screen's builds).</summary>
    void RefreshStates(World world) => live.Refresh(world.Uniques, world.Player.Faction >= 0 ? world.Player.Faction : data.PlayerFaction);

    public PopulationData Data => data;
    public PopulationSettings Settings => settings;
    public SiteStatus StatusOf(int site) => states[site].Status;
    /// <summary>The zones active as of the last look: the loaded zones (<see cref="RefreshLoaded"/>), where the squads, the items and the navmesh are kept.</summary>
    public IReadOnlyCollection<ZoneCoordinate> ActiveZones => active;
    public Vector3? Focus => focus;

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        EnsureArtifacts(world);
        world.FactionFormations ??= [.. data.Factions.Select(f => f.SquadFormation)];
        world.ForcedSpeeds ??= data.Db.OfType(FcsRecordType.SQUAD_TEMPLATE).Concat(data.Db.OfType(FcsRecordType.UNIQUE_SQUAD_TEMPLATE))
            .Where(r => SquadTemplate.ForceSpeedOf(r) is >= 0 and < Squad.NoForceSpeed).ToDictionary(r => r.StringId, r => (byte)SquadTemplate.ForceSpeedOf(r));
        foreach (var c in commands)
        {
            if (c is FocusCommand f) focus = f.Position;
            else if (c is DebugSpawn spawn) SpawnSandbox(world, spawn.Record, spawn.At, player: false, variant: ++debugSpawns);
        }

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
        DrainWild(world);
        while (world.Characters.LimbsChanged.TryDequeue(out var cut))
        {
            if (world.Characters.Cold(cut.Character) is not { } cold) continue;
            if (cut.Limb is 2 or 3) InventoryCommands.ValidateSections(world, cut.Character, cold);   // a severed leg may close the boots (FUN_14064edc0 -> validateInventorySections)
            assembly.RefreshEquipment(cold);
            DropSeveredLimb(world, cut.Character, cold, cut.Limb);
        }
    }

    /// <summary>
    /// The RACE <c>severed limbs</c> item of <paramref name="limb"/> on the ground, if the race has one for it (docs/game/character-stats.md "Limb replacements and robots"). The original drops it at the
    /// limb's <c>bone name 2</c> and lets physics take it; here it lies flat beside the character on that side (an engine choice), turned by a roll of the tick, and the host
    /// drops it from that bone's place (<see cref="FallSource"/>).
    /// </summary>
    void DropSeveredLimb(World world, int slot, CharacterCold cold, int limb)
    {
        // The first entry of the slot whose record exists (FUN_14064edc0: one whose target did not resolve is skipped, and with none nothing drops; the stump and wound stay).
        ItemInstance? item = null;
        foreach (var severed in cold.Race?.SeveredLimbs ?? [])
            if (severed.Slot == limb && (item = data.Items.Create(severed.TargetStringId)) is not null) break;
        if (item is null) return;
        ref readonly var hot = ref world.Characters.Next[slot];
        float side = limb is 0 or 2 ? -1 : 1, forward = limb < 2 ? 0.5f : -1.5f;
        var right = new Vector3(MathF.Cos(hot.Yaw), 0, -MathF.Sin(hot.Yaw));
        var ahead = new Vector3(MathF.Sin(hot.Yaw), 0, MathF.Cos(hot.Yaw));
        float yaw = Rng.Float(Rng.Hash(world.Seed, (ulong)slot, RngPurpose.Loot, (ulong)world.Tick)) * MathF.Tau;
        // On the ground under that spot (a drop beside a character on a slope or a step would otherwise hang at its feet's height), as an item dropped from the inventory.
        var at = hot.Position + right * (5 * side) + ahead * forward;
        at.Y = world.Walkability.GroundHeight(at.X, at.Z, hot.Position.Y);
        // The stump: the part of the limb's slot and its bone name 2 (BodyPartTemplate.BoneName2; FUN_14064edc0 reads it for the item's pose).
        string? stump = cold.Medical?.Parts.FirstOrDefault(p => p.Template.LimbSlot == limb)?.Template.BoneName2;
        world.GroundItems.Drop(item, at, yaw, stump is { Length: > 0 } bone ? new FallSource(world.Characters.IdOf(slot), bone, world.Tick, cold.Appearance?.SkinToneParameter ?? default) : null);
    }

    public void SlowWorld(World world)
    {
        MeetFactions(world);
        TopUpNest(world);
        // The original answers a world state from the live unique table (FUN_1409a7e00), so the copy is taken every tick (cheap while nothing changed), not at the look.
        RefreshStates(world);
        if (world.Tick % settings.CheckEveryTicks != 0) return;
        world.Uniques.SeedPrisoners(data.Db);
        RefreshUniques(world);
        RefreshStates(world);
        RefreshZones(world);
        Discover(world);
        StepRoaming(world);
        StepCampaigns(world);
        world.ItemSlots.Step(world, data);
    }

    /// <summary>The XZ distance at which a player character explores a town (and discovers it).</summary>
    public const float ExploreRange = 400;
    /// <summary>The XZ distance at which a player character discovers a town: doubled for a TOWN of type Town, halved for Nest and Ruins (docs/game/ui-screens.md "Map").</summary>
    public const float DiscoverRange = 8500;
    static float DiscoverRangeOf(TownType type) => DiscoverRange * (type == TownType.Town ? 2 : type is TownType.Nest or TownType.Ruins ? 0.5f : 1);

    /// <summary>Where each wild nest stood when it last loaded (null before), for the map.</summary>
    Vector2?[] wildCentres = [];
    /// <summary>The player characters of a <see cref="Discover"/> check, with their zones.</summary>
    readonly List<(Vector2 At, ZoneCoordinate Zone)> viewers = [];

    /// <summary>
    /// Towns and camps a player character comes within <see cref="ExploreRange"/> of become discovered and explored, and their faction known; ones not yet discovered within
    /// <see cref="DiscoverRangeOf"/> become discovered, with a "Discovered" message for a town (none for nests and camps). A character counts only for
    /// places within one zone of it: a place's own zone or, for a town, one of its <see cref="TownSite.Coverage"/> zones (docs/game/ui-screens.md "Map").
    /// </summary>
    void Discover(World world)
    {
        // Not during the new game's character creation: the original's "Discovered" messages come after CONFIRM (its world waits for the editor).
        if (!world.Player.Exists || world.Player.InCreation) return;
        var prev = world.Characters.Previous;
        var player = world.Player;
        viewers.Clear();
        for (int i = 0; i < prev.Length; i++)
            if (prev[i].Alive && world.Characters.Cold(i) is { IsPlayer: true }) viewers.Add(Viewer(prev[i].Position));
        foreach (var site in data.Sites)
        {
            if (site.Town.Type is TownType.NestMarker) continue;   // the original skips only type 8
            float d2 = NearestViewer(new(site.Position.X, site.Position.Z), site.Zone, site.Coverage), far = DiscoverRangeOf(site.Town.Type);
            if (d2 < ExploreRange * ExploreRange)
            {
                if (player.Know(site.Index, TownKnowledge.Discovered | TownKnowledge.Explored) && site.Index < data.SiteFactions.Count) player.Meet(data.SiteFactions[site.Index]);
            }
            else if (d2 < far * far && player.Know(site.Index, TownKnowledge.Discovered) && site.Town.Type != TownType.Nest)
                world.Post(Loc.Tr("Discovered {1}", site.Town.UnexploredName is { Length: > 0 } unexplored ? unexplored : site.Town.Name));
        }
        var wild = WildSites;
        for (int w = 0; w < wild.Count && w < wildCentres.Length; w++)
        {
            if (wild[w] is not { Kind: WildKind.Nest, Nest: { } nest } site || wildCentres[w] is not { } centre) continue;
            float d2 = NearestViewer(centre, WorldLayout.ZoneOf(centre.X, centre.Y), []), far = DiscoverRangeOf(TownType.Nest);
            var flags = d2 < ExploreRange * ExploreRange ? TownKnowledge.Discovered | TownKnowledge.Explored : d2 < far * far ? TownKnowledge.Discovered : TownKnowledge.None;
            if (flags == TownKnowledge.None) continue;
            var faction = site.Faction >= 0 && site.Faction < data.Factions.Count ? data.Factions[site.Faction] : null;
            if ((flags & TownKnowledge.Explored) != 0 && !player.Camps.Any(c => c.Site == w && (c.Known & TownKnowledge.Explored) != 0)) player.Meet(faction is null ? -1 : site.Faction);
            player.KnowCamp(new CampView(w, nest.Id, nest.Name, faction?.Id ?? nest.Faction ?? "", faction?.NotReal ?? false, new Vector3(centre.X, 0, centre.Y), flags), flags);
        }
        // A camp known from a save gets its place once its nest has loaded (never when the wild layout gives that site another nest).
        foreach (var camp in player.Camps)
            if (camp.Position is null && camp.Site >= 0 && camp.Site < wildCentres.Length && WildSites[camp.Site].Nest?.Id == camp.TownId && wildCentres[camp.Site] is { } c)
                player.KnowCamp(camp with { Position = new Vector3(c.X, 0, c.Y) }, TownKnowledge.None);
    }

    /// <summary>A sense update that finds this many characters in sight range also takes everyone within <see cref="CrowdReach"/> (<c>FUN_1407866d0</c>).</summary>
    const int CrowdCount = 40;
    const float CrowdReach = 250;

    Combat.CombatSystem? fights;
    Locks.LockSystem? locks;

    /// <summary>One of the player's characters whose sense update runs, so it meets factions: not unconscious, dead or carried (<c>FUN_140598c20</c>).</summary>
    internal static bool MeetsFactions(CharacterCold c) => c is { IsPlayer: true, DiedTick: < 0, CarriedBy.IsNone: true } && c.Medical?.Incapacitated != true;

    /// <summary>
    /// The player's characters meet the factions round them (docs/game/factions-squads-towns.md "Factions met"; <c>FUN_14085a8d0</c>, <c>FUN_1408593d0</c>): on its turn (one tick in
    /// <see cref="PopulationSettings.CheckEveryTicks"/>, by slot), each one not unconscious, dead or carried meets the faction of every character within its sight range on the ground plane,
    /// seen or not, the dead, the down and animals too, and of its fight target. Not during the character creation. Inside town walls (a gate code above 0, docs/game/pathfinding.md
    /// "Gate codes") 500 takes the place of the outdoor sight.
    /// </summary>
    void MeetFactions(World world)
    {
        if (!world.Player.Exists || world.Player.InCreation) return;
        var table = world.Characters;
        var prev = table.Previous;
        fights ??= world.Systems.OfType<Combat.CombatSystem>().FirstOrDefault();
        locks ??= world.Systems.OfType<Locks.LockSystem>().FirstOrDefault();
        int every = Math.Max(1, settings.CheckEveryTicks);
        for (int i = (int)(world.Tick % every); i < prev.Length; i += every)
        {
            if (!prev[i].Alive || table.Cold(i) is not { } cold || !MeetsFactions(cold)) continue;
            var at = new Vector2(prev[i].Position.X, prev[i].Position.Z);
            float range = (locks?.Regions?.CodeAt(prev[i].Position) > 0 ? SightMap.IndoorSight * WeatherSystem.SightFactor(cold) : AggroSystem.RangeOf(cold, AggroSystem.OutdoorSight))
                * (0.5f + 0.5f * world.Sight.Daylight);
            int found = 0;
            // The count includes the character itself, as the grid lookup's does.
            for (int j = 0; j < prev.Length && found < CrowdCount; j++)
                if (prev[j].Alive && Vector2.DistanceSquared(at, new(prev[j].Position.X, prev[j].Position.Z)) < range * range) found++;
            if (found >= CrowdCount) range = MathF.Max(range, CrowdReach);
            for (int j = 0; j < prev.Length; j++)
                if (j != i && prev[j].Alive && Vector2.DistanceSquared(at, new(prev[j].Position.X, prev[j].Position.Z)) < range * range && table.Cold(j) is { } other)
                    world.Player.Meet(other.Faction);
            if (fights?.StateOf(new CharacterId(i, prev[i].Generation)) is { TargetSlot: >= 0 and var t } fight && t < prev.Length && prev[t].Alive
                && prev[t].Generation == fight.TargetGeneration && table.Cold(t) is { } target)
                world.Player.Meet(target.Faction);
        }
    }

    static (Vector2 At, ZoneCoordinate Zone) Viewer(Vector3 at) => (new(at.X, at.Z), WorldLayout.ZoneOf(at.X, at.Z));

    /// <summary>
    /// The squared XZ distance from a place to the nearest viewer in or next to its <paramref name="zone"/> or one of its <paramref name="coverage"/> zones
    /// (infinity when none).
    /// </summary>
    float NearestViewer(Vector2 place, ZoneCoordinate zone, IReadOnlyList<ZoneCoordinate> coverage)
    {
        float best = float.PositiveInfinity;
        foreach (var (at, z) in viewers)
        {
            bool near = Near(z, zone);
            for (int i = 0; i < coverage.Count && !near; i++) near = Near(z, coverage[i]);
            if (near) best = Math.Min(best, Vector2.DistanceSquared(place, at));
        }
        return best;

        static bool Near(ZoneCoordinate a, ZoneCoordinate b) => Math.Abs(a.X - b.X) <= 1 && Math.Abs(a.Y - b.Y) <= 1;
    }

    /// <summary>
    /// The zones the original has loaded (docs/game/character-stats.md "Corpses", <c>FUN_140a0f820</c>, <c>FUN_140a0f080</c>, <c>FUN_140a0b830</c>, <c>FUN_140a0f640</c>,
    /// <b>Verified</b>), which decide what the squads, the corpses and the navmesh keep: the block of one zone each side round a centre zone, asked for again at every look (the
    /// centre moves to the camera's zone only once the camera is more than <see cref="ZoneActivation.RecentreDistance"/> from the centre zone's middle on either axis; kept
    /// <see cref="ZoneActivation.LoadedLingerSeconds"/> after), and the box round each player character (<see cref="ZoneActivation.Box"/>, a ring of one more zone when Fast zone
    /// hopping is on; kept <see cref="ZoneActivation.BoxLingerSeconds"/> after). A zone stays loaded until the latest of its requests has run out.
    /// </summary>
    void RefreshLoaded(World world, Vector3 camera)
    {
        bool move = loadCentre is not { } c
            || MathF.Abs(camera.X - ZoneActivation.CentreOf(c).X) > ZoneActivation.RecentreDistance || MathF.Abs(camera.Z - ZoneActivation.CentreOf(c).Y) > ZoneActivation.RecentreDistance;
        if (move) loadCentre = WorldLayout.ZoneOf(camera.X, camera.Z);
        long now = world.Tick;
        long until = now + (long)Math.Ceiling(ZoneActivation.LoadedLingerSeconds / world.TickSeconds);
        var centre = loadCentre!.Value;
        for (int x = centre.X - 1; x <= centre.X + 1; x++)
            for (int y = centre.Y - 1; y <= centre.Y + 1; y++)
                Ask(new ZoneCoordinate(x, y), until);
        long boxUntil = now + (long)Math.Ceiling(ZoneActivation.BoxLingerSeconds / world.TickSeconds);
        int ring = settings.FastZoneHopping ? 1 : 0;
        boxZones.Clear();
        foreach (var p in players) ZoneActivation.AddZonesAround(p, ring, boxZones);
        // A platoon of the player's faction asks for the box round its place as a player character does, unloaded (FUN_1407ebf30) or loaded (FUN_1404ffad0, both call FUN_140a0f640), so it is made and stays active.
        if (world.Platoons.Count > 0 && PlayerFactionOf(world) is >= 0 and var player)
            foreach (var p in world.Platoons.All)
                if (p.Faction == player) ZoneActivation.AddZonesAround(new Vector3(p.Position.X, 0, p.Position.Y), ring, boxZones);
        foreach (var zone in boxZones) Ask(zone, boxUntil);
        List<ZoneCoordinate>? gone = null;
        foreach (var (zone, t) in loadedUntil) if (t < now) (gone ??= []).Add(zone);
        if (gone is not null) foreach (var zone in gone) loadedUntil.Remove(zone);
        world.LoadedZones = new HashSet<ZoneCoordinate>(loadedUntil.Keys);
    }

    /// <summary>Keeps a zone of the grid loaded until <paramref name="until"/>, unless a later request already does.</summary>
    void Ask(ZoneCoordinate zone, long until)
    {
        if (!zone.IsInsideGrid) return;
        if (!loadedUntil.TryGetValue(zone, out long was) || was < until) loadedUntil[zone] = until;
    }

    void RefreshZones(World world)
    {
        // The player's characters ask for the box round them (the player's faction, docs/game/character-stats.md "Corpses"); the camera focus centres the block, and alone it does without a player.
        players.Clear();
        if (world.Player.Exists)
        {
            var prev = world.Characters.Previous;
            for (int i = 0; i < prev.Length; i++)
                if (prev[i].Alive && world.Characters.Cold(i) is { IsPlayer: true }) players.Add(prev[i].Position);
        }
        if (players.Count == 0 && focus is null) return;
        RefreshLoaded(world, focus ?? players[0]);
        (active, spareActive) = (spareActive, active);
        active.Clear();
        foreach (var zone in loadedUntil.Keys) active.Add(zone);
        world.VisitedZones.UnionWith(active);
        world.ActiveZones = active;
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
                case SiteStatus.Loaded or SiteStatus.Loading when AnyMemberActive(world, state):
                    state.UnloadAtTick = -1;
                    break;
                case SiteStatus.Loaded or SiteStatus.Loading:
                    if (state.UnloadAtTick < 0) state.UnloadAtTick = world.Tick + grace;
                    else if (world.Tick >= state.UnloadAtTick) Unload(world, site.Index, state);
                    break;
            }
        }
        StepWild(world, grace);
    }

    /// <summary>
    /// Whether a living member of one of the site's squads stands in an active zone: the site then stays loaded wherever its own zone is, so a squad
    /// chasing the player away from home does not vanish with it (the original unloads each platoon by its own place, docs/simulation.md "Population").
    /// </summary>
    bool AnyMemberActive(World world, SiteState state)
    {
        var table = world.Characters;
        foreach (int id in state.Squads)
        {
            if (world.Squads.Find(id) is not { } squad) continue;
            foreach (var m in squad.Members)
                if (table.TryResolveNext(m, out int slot) && table.IsAlive(m)
                    && active.Contains(WorldLayout.ZoneOf(table.Previous[slot].Position.X, table.Previous[slot].Position.Z))) return true;
        }
        return false;
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
        EnsurePools(world);
        // A town made once (its pools say so) never makes residents again; its own squads that lost too many are made afresh (docs/simulation.md "Off-screen tier").
        // A town that has just been replaced makes the replacement's residents, as the original treats its zone as fresh; the squads it had stay.
        bool replaced = Replace(world, site);
        bool held = !replaced && (HoldsSquads(world, site.Index) || PoolsOf(site.Index) is { Started: true });
        if (held) CheckRegeneration(world, site.Index);
        else if (!replaced) DropUnmade(world, site.Index);
        if (work is null)
        {
            Spawn(world, BuildTown(site, loadId, seed, walk, held));
            state.Status = SiteStatus.Loaded;
        }
        else
        {
            work.Add(() => built.Enqueue(BuildTown(site, loadId, seed, walk, held)));
        }
    }

    bool loadingScreen;

    /// <summary>
    /// The original's <c>ZoneManager::checkForRepopulateTown</c> at a town's load (docs/game/factions-squads-towns.md "Town overrides"): not behind the loading screen, nor for a town of the player's faction;
    /// of the current TOWN's <c>override town</c> list, the entries whose every <c>world state</c> holds, weighted by how many they have (none: never), other than the
    /// replacement in place; the heaviest, the first of equals, takes the town's place. A town replaced whose new residents have not been made yet (the original's
    /// <c>updatedTownOverrides</c>) answers yes again without looking. Either way its zone is reset (<see cref="ResetZone"/>). Whether the town is to be made afresh.
    /// </summary>
    bool Replace(World world, TownSite site)
    {
        if (loadingScreen || site.Index < data.SiteFactions.Count && data.SiteFactions[site.Index] is >= 0 and var f && f == PlayerFactionOf(world)) return false;
        var state = states[site.Index];
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
            if (data.Db.Find(link.Id) is not { Type: FcsRecordType.TOWN } record || record.StringId == site.Replacement?.Id) continue;
            var candidate = TownData.From(record);
            var states = candidate.WorldState.Where(w => data.Db.Find(w.Id) is { Type: FcsRecordType.WORLD_EVENT_STATE }).DistinctBy(w => w.Id).ToList();
            if (states.Count > weight && states.All(w => IWorldStates.Passes(States, data.Db, w.Id, w.V0))) (best, weight) = (candidate, states.Count);
        }
        if (best is null) return false;
        data.SetReplacement(site.Index, best);
        state.Repopulate = true;
        ResetZone(world, site);
        if (townLog.Count >= 256) townLog.RemoveAt(0);
        townLog.Add($"{site.Town.Name}: now {best.Name}");
        return true;
    }

    /// <summary>
    /// What the original drops from a replaced town's zone file (<c>FUN_1409fe340</c>, docs/game/factions-squads-towns.md "Town overrides"): the state of the town's buildings in
    /// that zone that are not the player's (they come back as the level has them, under the town's faction now), the item slots' timers there (due at once), and every loose
    /// item in the zone. The town's buildings in other zones change owner only.
    /// </summary>
    void ResetZone(World world, TownSite site)
    {
        world.Fixtures.TownReplaced(world, data, site.Index);
        foreach (int id in world.GroundItems.All.Where(g => WorldLayout.ZoneOf(g.Position.X, g.Position.Z) == site.Zone).Select(g => g.Id).ToList())
            world.GroundItems.Take(id);
        world.ItemSlots.ResetZone(site.Zone);
    }

    /// <summary>
    /// The town leaves the world: each squad it made becomes a pinned platoon that keeps its characters (<see cref="StoredSquad"/>), so the town is made of them next
    /// time and a save writes them (docs/simulation.md "Population"). A squad with no one alive left is dropped.
    /// </summary>
    void Unload(World world, int site, SiteState state)
    {
        foreach (int id in state.Squads)
        {
            if (world.Squads.Find(id) is not { } squad) continue;
            var stored = new StoredSquad { Name = squad.Name, HomeBuilding = squad.HomeBuilding, Shop = squad.Shop, NextHomeFillTick = squad.NextHomeFillTick, Package = squad.Ai?.Current?.Id, Ai = Saving.AiSave.Of(squad.Ai) ?? squad.SavedAi, Memory = Memory?.Take(squad.Id) };
            var at = Store(world, squad, stored);
            world.Squads.Remove(id);
            // A squad with no one alive left is declared dead, unless its template `regenerates` and its town lives: then it is made afresh next time (CM_REFRESH).
            bool refresh = stored.Members.Count == 0 && Regenerates(squad.TemplateId, site);
            if (stored.Members.Count == 0 && !refresh)
            {
                if (squad.Shop is { } shop) Trading.TradeSystem.GiveBack(world, shop);
                continue;
            }
            world.Platoons.Add(new Platoon
            {
                Id = world.Platoons.NextId(), Origin = site, TemplateId = squad.TemplateId, Faction = squad.Faction, Key = 0, Size = Math.Max(squad.SpawnSize, stored.Members.Count),
                Position = at, Money = squad.Money, Package = stored.Package, Stored = stored,
                Regenerate = refresh, IntactFlag = squad.Ai?.Intact ?? true,
            });
        }
        Reset(state);
    }

    /// <summary>A wild site leaves the world: its squads are removed (the population makes them again).</summary>
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
    /// Whether a loaded save holds squads of the town: a pinned saved platoon of the site, made now or not, other than a never-made stand-in (<see cref="Platoon.Unmade"/>).
    /// They are then the town's whole population and it makes no residents or bar squads of its own (<see cref="BuildTown"/>, docs/simulation.md "Saves").
    /// </summary>
    bool HoldsSquads(World world, int site)
    {
        foreach (var p in world.Platoons.All)
            if (p.Pinned && p.Origin == site && !p.Unmade) return true;
        return false;
    }

    /// <summary>
    /// A town that makes its own residents at its load (not held: its pool not <c>started</c> and no other squad of the save's) drops its never-made stand-ins, which would
    /// double them; their records stay in the save as they are (engine choice: an original save of a made town has <c>started</c>, so this is only a save without pool state).
    /// </summary>
    static void DropUnmade(World world, int site)
    {
        foreach (var p in world.Platoons.All.ToList())
            if (p is { Unmade: true, State: PlatoonState.Unloaded } && p.Origin == site)
            {
                if (p.Saved is { } sp) sp.PlatoonId = -1;
                world.Platoons.Remove(p.Id);
            }
    }


    void Spawn(World world, BuiltTown town)
    {
        var site = data.Sites[town.Site];
        var state = states[town.Site];
        state.Repopulate = false;
        float radius = site.Town.SizeRadius * site.Town.TownRadiusMult;
        // The town's doors, cages, containers and beds, once (they stay registered when the town unloads).
        world.Fixtures.RegisterSite(world, data, town.Site);
        world.ItemSlots.RegisterSite(world, data, town.Site);
        // A town made counts as visited in every zone of its buildings (engine choice: its residents and its buildings' locks are saved whole, docs/formats/save.md "Zone files"),
        // but for the centre zone of another town: a file there would tell the original that town was made, with no residents saved for it.
        foreach (var b in site.Buildings)
            if (b.Zone == site.Zone || !centres.Contains(b.Zone)) world.VisitedZones.Add(b.Zone);
        var centre = new Vector2(site.Position.X, site.Position.Z);
        foreach (var made in town.Squads)
        {
            // A gate has no nodes and its origin is inside the wall, ~110 from the opening: its residents are made at their gate guard's post (engine choice).
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
        if (townLog.Count >= 256) townLog.RemoveAt(0);   // nobody reads it in a headless world
        townLog.Add($"{site.Town.Name}: {town.Squads.Count} squad(s), {town.Squads.Sum(s => s.Members.Count)} characters; {housed} at home in " +
            $"{town.Squads.Where(s => s.Home is not null).Select(s => s.Home).Distinct().Count()} of {site.Buildings.Count(b => b.CanHouse)} houses, {town.Squads.Count - housed} elsewhere" +
            (housed < town.Squads.Count ? $" ({string.Join(", ", town.Squads.Where(s => s.Home is null).Select(s => s.Plan.Template.Name))})" : ""));
    }

    /// <summary>
    /// A housed squad's <c>prisoners</c> (the squad factory <c>FUN_140583a10</c>, <b>Observed</b>; docs/simulation.md "Cages, beds and locks"): only with a home building, each entry
    /// a squad of its template's <c>faction</c> (else the owner squad's), each member imprisoned in a random free cage of the home, its lock set. <b>Engine choices</b>: members
    /// beyond the free cages are not made (the original drops one and stops placing); the prisoners are held without a sentence (<see cref="CharacterCold.Kidnapped"/>), so no
    /// release round frees them and no bail is offered (the original's served test for them is <b>Unknown</b>).
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
            if (data.Db.Find(links[e].Id) is not { } record || !SquadFactory.IsTemplate(record)) continue;
            var faction = (uint)squad.Faction < (uint)data.Factions.Count ? data.Factions[squad.Faction] : null;
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
    /// A squad's <c>slaves</c> (the squad factory's recursive slave squads, role 4; docs/game/factions-squads-towns.md "Squad factory"): each planned slave squad is made as a squad of its
    /// template's <c>faction</c> (else the owner's), every member a slave (state 1) of the owner squad's faction. <b>Engine choices</b>: they are made at the owner squad's spot; with a
    /// home building, each goes into a random free cage of it (their package's goal is Slave get in Cage, which Meitou does not act out), those left over stand by the owner squad.
    /// Each obeys the owner squad (job 187: its Slave AI Goals, <see cref="CharacterCold.SlaveMaster"/>).
    /// </summary>
    void MakeSlaves(World world, BuiltSquad owner, Squad squad, TownSite site, float radius, SiteState state)
    {
        if (owner.Plan.Slaves.Count == 0) return;
        var table = world.Characters;
        var cages = owner.Home is { } home
            ? world.Fixtures.OfBuilding(home.Key).Where(f => f.Kind == Locks.FixtureKind.Cage && Locks.FixtureRegistry.IsFree(table, f)).ToList() : [];
        ulong ownerKey = Rng.StableHash(owner.Home?.Key ?? squad.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var ownerFaction = (uint)squad.Faction < (uint)data.Factions.Count ? data.Factions[squad.Faction] : null;
        int n = 0, picks = 0;
        foreach (var plan in owner.Plan.Slaves)
        {
            if (plan.Members.Count == 0) continue;
            int faction = plan.Template.Faction is { } tf && data.FactionIndex(tf) is var fi and >= 0 ? fi : squad.Faction;
            var members = new List<BuiltMember>();
            foreach (var m in plan.Members)
            {
                var look = data.Appearances?.Create(m.RecordId, ownerFaction?.Id, (int)(Rng.Hash(world.Seed, ownerKey, RngPurpose.Spawn, SeedSalts.SlaveLook + (ulong)n++) & 0x7FFFFFFF));
                members.Add(new BuiltMember(m, look));
            }
            var slaves = SpawnSquad(world, new BuiltSquad(plan with { Slaves = [] }, owner.Position, faction, members, owner.NearY), site, radius);
            state.Squads.Add(slaves.Id);
            foreach (var id in slaves.Members)
            {
                if (table.Cold(id.Slot) is not { } c) continue;
                // Every member, the leader too, gets member type 4 after the recursive squad is made (FUN_140583a10); the squad's leader pointer stays.
                (c.SlaveState, c.SlaveOwner, c.SlaveMaster, c.Role) = (1, squad.Faction, squad.Id, (int)SquadRole.Slave);
                if (cages.Count == 0) continue;
                int pick = Rng.Int(Rng.Hash(world.Seed, ownerKey, RngPurpose.Spawn, SeedSalts.SlaveCage + (ulong)picks++), cages.Count);
                world.Fixtures.Imprison(world, id, cages[pick]);
                cages.RemoveAt(pick);
            }
        }
    }

    readonly List<string> townLog = [];
    /// <summary>A line for each town whose squads were made since the last call (for the host's log; not part of the state).</summary>
    public List<string> TakeTownLog()
    {
        var list = new List<string>(townLog);
        townLog.Clear();
        return list;
    }

    Squad SpawnSquad(World world, BuiltSquad built, TownSite? site, float radius, bool player = false, int platoonId = -1, bool idle = false, float yaw = 0, bool pinned = false)
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
            Pinned = pinned,
            Position = new Vector3(built.Position.X, Ground(walk, built.Position.X, built.Position.Y, built.NearY), built.Position.Y),
            HomeCentre = site is null ? built.Position : new Vector2(site.Position.X, site.Position.Z),
            HomeRadius = Math.Max(radius * 0.8f, 40),
            HomeBuilding = built.Home,
        };
        var builtMembers = player || idle ? built.Members : GateUniques(world, built, squad.Id);
        var leaderPlan = builtMembers.FirstOrDefault(m => m.Plan.Role == SquadRole.Leader)?.Plan;
        bool hasLeader = leaderPlan is not null;
        foreach (var member in builtMembers)
        {
            var plan = member.Plan;
            float x = built.Position.X + plan.Offset.X, z = built.Position.Y + plan.Offset.Y;
            if (!(built.NearY is { } nearY ? walk.IsWalkable(x, z, nearY) : walk.IsWalkable(x, z))) (x, z) = (built.Position.X, built.Position.Y);
            // Inside, on the floor and not on the ground showing through a hole furniture cuts in it (the spot's height is its own plus SpotLift).
            float? standY = built.NearY;
            if (built.NearY is { } floorY)
            {
                // A building without nodes makes them at its origin, often under a raised floor: that floor first (the ground under it is near the origin).
                bool atOrigin = built.Home is { } h && !HasNode(h);
                if (!atOrigin && walk.FloorNear(x, z, floorY - SpotLift, SpotLift) is { } onFloor) (x, z) = (onFloor.X, onFloor.Z);
                else if (built.Home is { } home && HomeFloor(walk, home, x, z, floorY - SpotLift) is { } inside) (x, z, standY) = (inside.X, inside.Z, inside.Y + SpotLift);
                else if (atOrigin && walk.FloorNear(x, z, floorY - SpotLift, SpotLift) is { } onGround) (x, z) = (onGround.X, onGround.Z);
            }
            var race = assembly.RaceOf(plan.RecordId, member.Appearance);
            var record = data.Db.Find(plan.RecordId);
            var hot = new CharacterHot
            {
                Position = new Vector3(x, Ground(walk, x, z, standY), z),
                Yaw = yaw,
                Health = 100,
                Task = (byte)(player || idle ? CharacterTask.Idle : plan.Role == SquadRole.Leader || !hasLeader ? CharacterTask.Wander : CharacterTask.Follow),
                Mode = player ? SpeedMode.Free : SpeedMode.Walk,
                MaxSpeed = CharacterAssembly.StandInMaxSpeed,
                WalkSpeed = CharacterAssembly.WalkSpeed(race),
                TaskTime = 0.5f + Rng.Float(Rng.Hash(world.Seed, (ulong)squad.Id << 8 | (uint)plan.Index, RngPurpose.Think)) * 3,
            };
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
            assembly.Attach(table.PeekNextId(), cold, ref hot, record, race, world.Seed, plan.IsAnimal ? plan.Age : 1);
            // A new NPC's bounties (economy.md 11.2; engine: not the player's characters, nor the sandbox's idle spawns).
            if (!player && !idle) cold.Bounties = Crime.SpawnBounties.Roll(data, record, world.Seed, table.PeekNextId(), world.Tick);
            var id = table.Spawn(hot, cold, world.Tick);
            if (UniqueCharacters.IsUnique(record)) world.Uniques.Bind(plan.RecordId, id);
            squad.Members.Add(id);
            if (plan.Role == SquadRole.Leader && squad.Leader.IsNone) squad.Leader = id;
        }
        squad.SpawnSize = squad.Members.Count;
        if (squad.Leader.IsNone) squad.Leader = SquadRegistry.Successor(squad.Members, table);
        world.Squads.Add(squad);
        return squad;
    }

    /// <summary>A <c>named</c> CHARACTER's random name from its look's sex's list (docs/characters.md "Names"); null: the record's name.</summary>
    string? SpawnName(World world, GameRecord? record, CharacterAppearance? look, int squadId, int index) =>
        record is not null && record.GetBool("named") && data.Appearances is { } source
            ? source.RandomName(look?.Female ?? false, Rng.Hash(world.Seed, (ulong)squadId << 8 | (uint)index, RngPurpose.Name))
            : null;


    /// <summary>
    /// The unique-character gate (<c>FUN_1405836e0</c>, docs/game/factions-squads-towns.md "Unique characters") on the members of a squad about to be made, in the serial
    /// phase so two squads planned at once cannot both make one: a free unique is claimed and made; a claimed one (made elsewhere, dead) is replaced by its first
    /// <c>unique replacement spawn</c> (not gated, with a look rolled now; an animal keeps its look) or by nobody. The <c>animals</c> lists go through the same gate (<c>FUN_140583a10</c>). When the leader is
    /// gone the first member left leads.
    /// </summary>
    List<BuiltMember> GateUniques(World world, BuiltSquad built, int squadId)
    {
        List<BuiltMember>? gated = null;
        for (int i = 0; i < built.Members.Count; i++)
        {
            var member = built.Members[i];
            if (data.Db.Find(member.Plan.RecordId) is not { } record || !UniqueCharacters.IsUnique(record)
                || world.Uniques.TryClaim(member.Plan.RecordId))
            {
                gated?.Add(member);
                continue;
            }
            gated ??= [.. built.Members.Take(i)];
            var replacements = record.GetReferences("unique replacement spawn");
            if (replacements.Count == 0 || data.Db.Find(replacements[0].TargetStringId) is null) continue;
            string replacement = replacements[0].TargetStringId;
            string? faction = built.Faction >= 0 && built.Faction < data.Factions.Count ? data.Factions[built.Faction].Id : null;
            var look = member.Plan.IsAnimal ? member.Appearance : data.Appearances?.Create(replacement, faction, (int)(Rng.Hash(world.Seed, Rng.StableHash(replacement) ^ (ulong)squadId << 32, RngPurpose.Spawn, SeedSalts.MemberLook + (ulong)i) & 0x7FFFFFFF));
            gated.Add(new BuiltMember(member.Plan with { RecordId = replacement }, look));
        }
        if (gated is null) return built.Members;
        if (gated.Count > 0 && !gated.Any(m => m.Plan.Role == SquadRole.Leader)) gated[0] = gated[0] with { Plan = gated[0].Plan with { Role = SquadRole.Leader } };
        return gated;
    }

    /// <summary>
    /// The unique members of a platoon that leaves the world for good without being kept (despawned, or remade from its template): the living are freed to be made again,
    /// as the original's unload does for a free and well unique (<c>FUN_1409a8120</c>); with <paramref name="killed"/> (an off-screen defeat) they die instead.
    /// </summary>
    void ReleaseUniques(World world, Platoon p, bool killed = false)
    {
        if (world.Squads.Find(p.SquadId) is { } squad) ReleaseUniques(world, squad, killed);
        else if (p.Stored is { } stored)
            foreach (var m in stored.Members) ReleaseUnique(world, m.Cold.RecordId, CharacterId.None, m.Cold.Medical is { Dead: true } || killed);
        else if (p.Saved is { } saved)
            foreach (var m in saved.Members)
                if (!m.Gone) ReleaseUnique(world, m.Source.RecordId, CharacterId.None, m.Dead || killed);
    }

    /// <summary>The same for the characters of a squad in the world.</summary>
    void ReleaseUniques(World world, Squad squad, bool killed = false)
    {
        var table = world.Characters;
        foreach (var m in squad.Members)
            if (table.TryResolveNext(m, out int slot) && table.Cold(slot) is { } cold)
                ReleaseUnique(world, cold.RecordId, m, killed || cold.DiedTick >= 0 || cold.Medical is { Dead: true }, cold.Faction);
    }

    /// <summary>
    /// One member: dead, it dies in the registry; alive, its entry is erased when this character holds it (<paramref name="holder"/>, or none for one not in the world), so a
    /// copy made as another's replacement (a unique naming itself) never frees the first.
    /// </summary>
    void ReleaseUnique(World world, string recordId, CharacterId holder, bool dead, int faction = -1)
    {
        if (world.Uniques.Find(recordId) is not { } entry || !UniqueCharacters.IsUnique(data.Db.Find(recordId))) return;
        if (!holder.IsNone && !entry.Handle.IsNone && entry.Handle != holder) return;
        world.Uniques.Left(holder);
        if (dead) world.Uniques.Died(recordId, false, faction);
        else world.Uniques.Release(recordId);
    }

    /// <summary>
    /// The unique characters in the world, at every population look (the original's per-character refresh <c>FUN_1405cf470</c> and its death and imprisonment hooks,
    /// docs/game/factions-squads-towns.md "Unique characters"): dead → 0 (killed by the player's: set by the combat commit); in a cage or restraint → 2 (player: the cage's
    /// owner is the player's); alive and carried by one of a faction neither its own nor allied → 2 (player: the carrier is the player's); captured but neither held nor
    /// carried → 1, player cleared (engine summary of the original's ally and town tests); in the player's faction and alive though marked dead → 1.
    /// </summary>
    void RefreshUniques(World world)
    {
        var uniques = world.Uniques;
        if (uniques.Count == 0) return;
        var table = world.Characters;
        var relations = data.Relations;
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
                if (e.State != UniqueState.Imprisoned) uniques.Captured(recordId, cold.InWhat is { } cage && world.Fixtures.Find(cage) is { } f && f.Owner >= 0 && f.Owner == playerFaction);
            }
            else if (!cold.CarriedBy.IsNone && table.TryResolveNext(cold.CarriedBy, out int carrierSlot) && table.Cold(carrierSlot) is { } carrier)
            {
                bool friend = carrier.Faction == cold.Faction || relations.Contains(carrier.Faction) && relations.Contains(cold.Faction) && relations.IsAlly(cold.Faction, carrier.Faction);
                if (e.State == UniqueState.Alive && !friend) uniques.Captured(recordId, carrier.IsPlayer);
            }
            else if (e.State == UniqueState.Imprisoned || e.State == UniqueState.Dead && cold.IsPlayer) uniques.Freed(recordId);
        }
        if (gone is not null) foreach (var id in gone) uniques.Left(id);
    }

    /// <summary>The floor at X/Z: the one under <paramref name="nearY"/> inside a building, else the ground.</summary>
    static float Ground(IWalkability walk, float x, float z, float? nearY) => nearY is { } y ? walk.GroundHeight(x, z, y) : walk.GroundHeight(x, z);

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
