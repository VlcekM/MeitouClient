using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Locks;

/// <summary>
/// What a <see cref="Round"/> does: free a served prisoner (goal 211), lock a home's door (155, 197), unlock and open a door (86, and a gate guard's
/// gate), lock a cage (195).
/// </summary>
public enum RoundKind : byte
{
    /// <summary>
    /// Free a served prisoner (goal 211).
    /// </summary>
    Release,
    /// <summary>
    /// Lock a home's door (155, 197).
    /// </summary>
    LockDoors,
    /// <summary>
    /// Unlock and open a door (86, a gate guard's gate).
    /// </summary>
    UnlockDoors,
    /// <summary>
    /// Lock a cage (195).
    /// </summary>
    LockCages
}

/// <summary>
/// Where a round is: walking to its target, or working the lock there (the owner's 3.3 s).
/// </summary>
public enum RoundPhase : byte
{
    /// <summary>
    /// Walking to the next target.
    /// </summary>
    Walk,
    /// <summary>
    /// Working the lock.
    /// </summary>
    Work
}

/// <summary>
/// One NPC's lock work (serial state of the <see cref="LockSystem"/>): the act-out of a warden's goal (<see cref="WardenGoals"/>, one target) or a gate
/// guard's door work (its gate's doors, in order).
/// </summary>
public sealed class Round
{
    /// <summary>
    /// What the round does.
    /// </summary>
    public required RoundKind Kind { get; init; }
    /// <summary>
    /// The worker.
    /// </summary>
    public required CharacterId Worker { get; init; }
    /// <summary>
    /// The worker's faction.
    /// </summary>
    public required int Faction { get; init; }
    /// <summary>
    /// Fixture ids: the cage (release, cage) or the doors.
    /// </summary>
    public required string[] Targets { get; init; }
    /// <summary>
    /// The building of the doors (door rounds).
    /// </summary>
    public string? Building { get; init; }
    /// <summary>
    /// The prisoner a release frees (none for door and cage rounds).
    /// </summary>
    public CharacterId Prisoner { get; init; } = CharacterId.None;
    /// <summary>
    /// Within this of its spot the worker works a target (a cage's <c>use range</c> + <see cref="LockSystem.Reach"/>; for a door locked from inside
    /// or outside <see cref="LockSystem.DoorReach"/> of the door's inner or outer point; a door opened is worked from its nearer side).
    /// </summary>
    public required float Reach { get; init; }
    /// <summary>
    /// A lock-up from outside (goal 197): the worker works the door from its outer point (state 126); else a lock-up works it from its inner point
    /// (state 111).
    /// </summary>
    public bool FromOutside { get; init; }
    /// <summary>
    /// The index in <see cref="Targets"/> of the target worked next.
    /// </summary>
    public int Index { get; set; }
    /// <summary>
    /// Where the round is.
    /// </summary>
    public RoundPhase Phase { get; set; }
    /// <summary>
    /// Game seconds in the phase.
    /// </summary>
    public float Time { get; set; }
    /// <summary>
    /// The round ended on a lost walk: the worker stopped short of its spot (<see cref="LockSystem.Advance"/> returned true for that).
    /// </summary>
    public bool Lost { get; set; }

    /// <summary>
    /// Adds the round to the world's state hash.
    /// </summary>
    internal void Hash(ref StateHasher h)
    {
        h.Add((int)Kind);
        h.Add(Worker);
        h.Add(Faction);
        h.Add(Targets.Length);
        foreach (var t in Targets) h.Add(Rng.StableHash(t));
        h.Add(Prisoner);
        h.Add(Index);
        h.Add((int)Phase);
        h.Add(Time);
        h.Add(FromOutside ? 1 : 0);
        if (Lost) h.Add(0x4C6F7374);   // "Lost": its callers drop the round in that step, so only while set
    }
}

/// <summary>
/// The NPCs' lock work (docs/simulation.md "Arrest and jail"; the original's goals in docs/game/locks.md and economy.md 11.5). Serial, in
/// <c>SlowWorld</c>. The work at a lock is the owner's lock/unlock time, 0.3 per second (<b>Verified</b>).
/// <list type="bullet">
/// <item><b>Wardens' goals</b> (211 freeing prisoners, 155 and 197 locking up, 86 opening up, 195 locking cages): goals of the one chooser
/// (<see cref="WardenGoals"/>), each act-out a <see cref="Round"/> of one target.</item>
/// <item><b>Gates</b> (180 Man the gate): a goal of the one chooser (<see cref="GateGoals"/>) whose act-out unlocks and opens the guard's shut gate
/// (a <see cref="Round"/> of its doors) and stands at the gate.</item>
/// <item><b>Leaving</b> (state 60, rows 84, 74, 80, 287): a character standing in a shut building whose walk leads out unlocks its own door or
/// bashes another's when it wants to, then walks on (<see cref="LeaveSweep"/>); one without a way stays in.</item>
/// <item><b>Arrests</b> (<b>engine choice</b>): an arrest's guard carrying a captive to a cage behind its own faction's shut doors has them opened at
/// once.</item>
/// </list>
/// </summary>
public sealed partial class LockSystem : IStateHashed, Player.IDutyRelease, IDutyGoalOwner
{
    #region The rounds step

    /// <summary>
    /// The owner's lock and unlock tasks progress 0.3 per game second (docs/game/locks.md "Doors", <b>Verified</b>).
    /// </summary>
    public const float LockWorkSeconds = 1 / 0.3f;
    /// <summary>
    /// How often the walkers shut in are looked at (ticks, engine choice).
    /// </summary>
    public const int RoundEveryTicks = 30;
    /// <summary>
    /// The game seconds the release tests take as the longest a served prisoner waits for its jailer; nothing in the simulation reads it.
    /// </summary>
    public const float ReleaseTimeout = 120;
    /// <summary>
    /// A round's walk that ended on its own within this of the spot counts as there (engine choice), but for opening a door.
    /// </summary>
    public const float WalkSlack = 200;
    /// <summary>
    /// The reach of states 111 AT_LOCATION_BUILDING_DOOR_INSIDE and 126 AT_LOCATION_BUILDING_DOOR_OUTSIDE for a door (<c>FUN_140596cf0</c>,
    /// <c>FUN_140596e90</c> against the door's reach range, 45, <b>Observed</b>).
    /// </summary>
    public const float DoorReach = 45;
    /// <summary>
    /// The walkers a sweep sends through a door at most (engine choice).
    /// </summary>
    const int MaxNewDoorWays = 4;

    /// <summary>
    /// Homes whose door a lock-up locked (the squad's "house locked" flag), by building instance id; sorted so the hash has a fixed order.
    /// </summary>
    readonly SortedSet<string> _houseLocked = new(StringComparer.Ordinal);
    /// <summary>
    /// The wardens' goals: their finders' picks and act-outs.
    /// </summary>
    WardenGoals? _wardens;
    /// <summary>
    /// The escape goals: their act-outs.
    /// </summary>
    EscapeGoals? _escapes;
    /// <summary>
    /// The gate guards' goal: its act-outs.
    /// </summary>
    GateGoals? _gateGoals;
    /// <summary>
    /// The arrest system, looked up once (null: none).
    /// </summary>
    Crime.ArrestSystem? _arrests;
    /// <summary>
    /// Whether <see cref="_arrests"/> was looked up.
    /// </summary>
    bool _arrestsLooked;

    /// <summary>
    /// Meitou's save key (bool, on the building's GAMESTATE_BUILDING) of a home whose house flag is set: without it a loaded night's locked homes would
    /// never be opened up in the morning.
    /// </summary>
    public const string HouseLockedKey = "meitou house locked";
    /// <summary>
    /// Whether the loaded save's locked homes were read (<see cref="ReadSaved"/>).
    /// </summary>
    bool _savedRead;

    /// <summary>
    /// The rounds running: the gate guards' and the wardens' act-outs.
    /// </summary>
    public IReadOnlyList<Round> ActiveRounds => [.. Gates.Rounds, .. Wardens.Acting];

    /// <summary>
    /// The wardens' goals, made on first use.
    /// </summary>
    WardenGoals Wardens => _wardens ??= new WardenGoals(this);

    /// <summary>
    /// The escape goals, made on first use.
    /// </summary>
    internal EscapeGoals Escapes => _escapes ??= new EscapeGoals(this);

    /// <summary>
    /// The gate guards' goal, made on first use.
    /// </summary>
    GateGoals Gates => _gateGoals ??= new GateGoals(this);

    /// <summary>
    /// Registers the wardens' goals (<see cref="WardenGoals.Register"/>), the escapes (<see cref="EscapeGoals.Register"/>) and the gate guards'
    /// (<see cref="GateGoals.Register"/>).
    /// </summary>
    void IDutyGoalOwner.RegisterGoals(DutyGoals goals)
    {
        Wardens.Register(goals);
        Escapes.Register(goals);
        Gates.Register(goals);
    }

    /// <summary>
    /// Takes the homes a loaded save had locked up from the zones' building records (<see cref="FixtureRegistry.Saved"/>), once.
    /// </summary>
    void ReadSaved(World world)
    {
        if (_savedRead) return;
        _savedRead = true;
        if (world.Fixtures.Saved is not { } saved) return;
        foreach (var (building, state) in saved)
            if (state.Bools.GetValueOrDefault(HouseLockedKey)) _houseLocked.Add(building);
    }

    /// <summary>
    /// The home's house flag is set and nothing has cleared it since (what <see cref="Saving.SaveCapture"/> writes as <see cref="HouseLockedKey"/>).
    /// </summary>
    public bool IsHouseLocked(World world, string building)
    {
        ReadSaved(world);
        return _houseLocked.Contains(building);
    }

    /// <summary>
    /// The home's house flag, read without the saved homes (the think: the serial step read them first).
    /// </summary>
    internal bool HouseLocked(string building) => _houseLocked.Contains(building);

    /// <summary>
    /// Clears a home's house flag (serial: 86's finder with no door left to open, <see cref="WardenGoals"/>).
    /// </summary>
    internal void ClearHouseLocked(string building) => _houseLocked.Remove(building);

    /// <summary>
    /// The rounds' step: the saved locked homes read once, ways opened for arrests, the gate guards' and the wardens' goals step, and on the cadence
    /// the walkers shut in are looked at.
    /// </summary>
    void Rounds(World world)
    {
        ReadSaved(world);
        OpenArrestWays(world);
        Gates.Step(world);
        Wardens.Look(world);
        Wardens.Step(world);
        if (world.Tick % RoundEveryTicks != 0) return;

        LeaveSweep(world);
    }

    /// <summary>
    /// The arrest system, looked up on first use; null without one.
    /// </summary>
    Crime.ArrestSystem? Arrests(World world)
    {
        if (!_arrestsLooked)
        {
            _arrestsLooked = true;
            foreach (var s in world.Systems) if (s is Crime.ArrestSystem a) { _arrests = a; break; }
        }

        return _arrests;
    }

    /// <summary>
    /// An arrest's guard carrying a captive to a cage behind his own faction's shut doors: they open for him (engine choice, see the class remarks).
    /// </summary>
    void OpenArrestWays(World world)
    {
        if (Arrests(world) is not { Arrests.Count: > 0 } a) return;
        foreach (var x in a.Arrests)
        {
            if (x.Phase == Crime.ArrestPhase.Walk && x.Cage is { } id && world.Fixtures.Find(id) is { } cage && world.Fixtures.IsShut(cage.Building)
                    && world.Fixtures.DoorOwner(cage.Building) == x.Faction)
                world.Fixtures.SetDoorsLocked(cage.Building, false);
        }
    }

    /// <summary>
    /// Whether <paramref name="id"/> is a round's worker or prisoner.
    /// </summary>
    bool Busy(CharacterId id)
    {
        foreach (var r in ActiveRounds)
            if (r.Worker == id || r.Prisoner == id) return true;
        return false;
    }

    /// <summary>
    /// Whether a round works fixture or building <paramref name="id"/>.
    /// </summary>
    internal bool Targeted(string id)
    {
        foreach (var r in ActiveRounds)
        {
            if (r.Building == id) return true;
            foreach (var t in r.Targets)
                if (t == id) return true;
        }

        return false;
    }

    #endregion

    #region The way out

    /// <summary>
    /// The way out of a shut building (docs/game/locks.md "Locked doors on the way", <b>Verified</b> rows and evaluators): every walk lists state 60
    /// LOCATION_IS_ACCESSIBLE (<c>FUN_1405a2710</c>: false in a building with no open door when the destination is elsewhere), whose provider 84
    /// walks to the door here (83) and opens it through 74, which needs it unlocked (!55); only 80 Unlocking door here (list B 70
    /// <c>isObjectMine</c>) or 287 Bashing door gives that. A walker inside whose walk leads out (the paths stop at the shut doors) takes the
    /// building's door its way (<see cref="StartDoorWay"/>); one without a way is not walked out (<see cref="CanLeave"/>).
    /// </summary>
    void LeaveSweep(World world)
    {
        var table = world.Characters;
        int started = 0;
        for (int s = 0; s < table.HighWater && started < MaxNewDoorWays; s++)
        {
            if (ShutIn(world, s) is not { } door) continue;

            ref var n = ref table.Next[s];
            StartDoorWay(world, s, door, n.Goal, n.Mode);
            started++;
        }
    }

    /// <summary>
    /// The door of the shut building a walker wants out of (<c>Building::getDoor</c>, the one of lowest index with a known leaf): it stands in it,
    /// no round works its doors, it has a way through them (<see cref="WayThrough"/>), and its walk (under way or just failed) goes to a point the
    /// building does not cover, whatever runs that walk (state 60 is in every walk row's list B, so any plan's walk gets the way out as its step,
    /// <see cref="StartDoorWay"/>). Null for anyone else.
    /// </summary>
    Fixture? ShutIn(World world, int s)
    {
        var table = world.Characters;
        ref var n = ref table.Next[s];
        if (!n.Alive || table.Cold(s) is not { IsImprisoned: false, Bashing: null, Medical.Incapacitated: false } c) return null;
        if (!c.CarriedBy.IsNone || Busy(table.IdOf(s)) || DecisionSystem.Ordered(c) || world.Sight.BuildingOf(s) is not { } building)
            return null;
        if (!world.Fixtures.IsShut(building) || Targeted(building)) return null;

        bool walking = (n.Flags & (ushort)MoveFlags.AnyPath) != 0 || c.WalkEnd == WalkEnd.Failed;
        if (!walking || world.Sight.Covers(building, n.Goal.X, n.Goal.Y)) return null;

        return WayThrough(world, s, building) != DoorWayKind.None ? MainDoor(world, building) : null;
    }

    /// <summary>
    /// A building's door of lowest index with a known leaf (null: none).
    /// </summary>
    static Fixture? MainDoor(World world, string building)
    {
        Fixture? main = null;
        foreach (var f in world.Fixtures.OfBuilding(building))
        {
            if (f.Kind == FixtureKind.Door && world.Fixtures.DoorSpotOf(f) is not null && (main is null || f.DoorIndex < main.DoorIndex))
                main = f;
        }

        return main;
    }

    /// <summary>
    /// <c>isObjectMine</c> for a building's door (<c>FUN_1405a3c10</c>, <b>Verified</b>): the character's squad lives in the building, or its faction
    /// owns it, or the door is of its faction.
    /// </summary>
    internal static bool IsMine(World world, CharacterCold c, string building) =>
        world.Fixtures.DoorOwner(building) == c.Faction || world.Fixtures.OwnsBuilding(building, c.Faction)
        || world.Squads.Find(c.SquadId)?.HomeBuilding?.Key == building;

    #endregion

    #region A round

    /// <summary>
    /// Starts a round through <see cref="TaskStart.Start"/> (a release on row 110, a lock-up or cage on 77, an unlock on 78) with
    /// <paramref name="source"/>: a warden goal's act-out (<see cref="TaskSource.Goal"/>) or a gate guard's door work; false when the worker's
    /// repertoire does not have the row.
    /// </summary>
    internal static bool Start(World world, int worker, Round round, Fixture first, TaskSource source)
    {
        var row = round.Kind switch
        {
            RoundKind.Release => TaskType.ReleasePrisoner,
            RoundKind.UnlockDoors => TaskType.UnlockDoor,
            _ => TaskType.LockDoor,
        };
        var request = new TaskRequest(CharacterTask.GoTo, row, source) { Site = first.Id, Detail = round };
        return TaskStart.Start(world, worker, request) == StartResult.Started;
    }

    /// <summary>
    /// The round's start (<see cref="Start"/>), a goal's act-out (<see cref="WardenGoals"/>, <see cref="GateGoals"/>): the worker gets up and walks to
    /// the first target.
    /// </summary>
    void StartRound(World world, int worker, Round round, Fixture first)
    {
        var table = world.Characters;
        TaskStart.Stand(table, worker);   // up from its bed or post
        Walk(world, worker, round, first);
    }

    /// <summary>
    /// Where state 126 AT_LOCATION_BUILDING_DOOR_OUTSIDE wants a worker for a door (<c>FUN_140596e90</c>, <b>Verified</b>): the door's outer point
    /// pushed out by 1.1 times the distance between its two points, 15 + 33 from the leaf. The fixture's place without door data.
    /// </summary>
    static Vector2 OuterPoint(World world, Fixture door) =>
        world.Fixtures.DoorSpotOf(door) is { } spot ? DoorPoints.Outer(spot, door.Position) : Flat(door.Position);

    /// <summary>
    /// Where state 111 AT_LOCATION_BUILDING_DOOR_INSIDE wants a worker for a door (<c>FUN_140596cf0</c>, <b>Observed</b>: the door's other point
    /// pushed the same way, <see cref="DoorPoints.Inner"/>). The fixture's place without door data.
    /// </summary>
    static Vector2 InnerPoint(World world, Fixture door) =>
        world.Fixtures.DoorSpotOf(door) is { } spot ? DoorPoints.Inner(spot, door.Position) : Flat(door.Position);

    /// <summary>
    /// The place a round's worker goes to for a target and how near counts: a door opened from its nearer side, a door locked up from its inner or
    /// outer point, a cage from its own place.
    /// </summary>
    static (Vector2 At, float Reach) RoundSpot(World world, Round round, Fixture f, Vector2 from) => round.Kind switch
    {
        RoundKind.UnlockDoors => WorkSpot(world, f, from),
        RoundKind.LockDoors => (round.FromOutside ? OuterPoint(world, f) : InnerPoint(world, f), round.Reach),
        _ => (Flat(f.Position), round.Reach),
    };

    /// <summary>
    /// Sends the worker to the round's next target, unless it is within reach already.
    /// </summary>
    static void Walk(World world, int worker, Round round, Fixture f)
    {
        round.Phase = RoundPhase.Walk;
        round.Time = 0;
        var from = Flat(world.Characters.Next[worker].Position);
        var (at, reach) = RoundSpot(world, round, f, from);
        if (Vector2.Distance(from, at) > reach) TaskStart.StepTo(world.Characters, worker, at, SpeedMode.Walk);
    }

    /// <summary>
    /// One tick of a round for its worker at <paramref name="w"/>; true when it is over: its prisoner freed meanwhile, its target gone or the
    /// player's, its walk ended short of the target, or its last target worked.
    /// </summary>
    internal bool Advance(World world, Round r, int w)
    {
        if (r.Kind == RoundKind.Release && !StillCaged(world, r)) return true;
        if (r.Index >= r.Targets.Length || world.Fixtures.Find(r.Targets[r.Index]) is not { } f) return true;
        if (r.Faction != world.Player.Faction && world.Fixtures.OwnsBuilding(f.Building, world.Player.Faction)) return true;

        return r.Phase == RoundPhase.Walk ? Arrive(world, r, w, f) : Work(world, r, w, f);
    }

    /// <summary>
    /// Whether a release round's prisoner is still held in its cage (a bail, a pick or a lift frees it meanwhile).
    /// </summary>
    static bool StillCaged(World world, Round r) =>
        world.Characters.TryResolveNext(r.Prisoner, out int p) && world.Characters.Cold(p) is { IsImprisoned: true } pc && pc.InWhat == r.Targets[0];

    /// <summary>
    /// The walk phase: at the spot (or a walk ended on its own within <see cref="WalkSlack"/>, but for opening a door) the worker stands and works;
    /// true when the walk ended short (the plan step's lost walk).
    /// </summary>
    static bool Arrive(World world, Round r, int w, Fixture f)
    {
        var table = world.Characters;
        ref var n = ref table.Next[w];
        bool walking = n.Task == (byte)CharacterTask.GoTo;
        var from = Flat(n.Position);
        var (at, reach) = RoundSpot(world, r, f, from);
        float d = Vector2.Distance(from, at);
        if (d > reach && (r.Kind == RoundKind.UnlockDoors || walking || d > WalkSlack))
        {
            r.Lost = !walking;
            return r.Lost;
        }

        if (walking) TaskStart.Stand(table, w);
        r.Phase = RoundPhase.Work;
        r.Time = 0;
        return false;
    }

    /// <summary>
    /// The work phase: the lock is worked for <see cref="LockWorkSeconds"/> (none to open a door that is not locked, 72 alone), then the target is done
    /// (<see cref="Apply"/>) and the worker walks to the next; true after the last.
    /// </summary>
    bool Work(World world, Round r, int w, Fixture f)
    {
        var table = world.Characters;
        r.Time += world.TickSeconds;
        bool worksLock = r.Kind != RoundKind.UnlockDoors || f.Lock is { EffectivelyLocked: true };
        if (worksLock && r.Time < LockWorkSeconds || r.Kind == RoundKind.UnlockDoors && f.Motion == DoorMotion.Closing) return false;

        Apply(world, r, f, table.Cold(w)!);
        if (worksLock)
            world.EmitSound(r.Kind is RoundKind.LockDoors or RoundKind.LockCages ? SoundKind.Locked : SoundKind.Unlocked, w, table.Next[w].Position);
        if (++r.Index >= r.Targets.Length || world.Fixtures.Find(r.Targets[r.Index]) is not { } next) return true;

        Walk(world, w, r, next);
        var here = Flat(table.Next[w].Position);
        var (nextAt, nextReach) = RoundSpot(world, r, next, here);
        if (Vector2.Distance(here, nextAt) <= nextReach) r.Phase = RoundPhase.Work;   // the next door is where it stands
        return false;
    }

    /// <summary>
    /// Works the lock of a round's target: frees the prisoner, locks the door or cage, or unlocks and opens the door.
    /// </summary>
    void Apply(World world, Round r, Fixture f, CharacterCold worker)
    {
        switch (r.Kind)
        {
            case RoundKind.Release:
                world.Fixtures.SetLocked(f, false);
                FreeBy(world, r.Worker, r.Faction, r.Prisoner);
                break;
            case RoundKind.LockDoors:
                LockDoor(world, r, f, worker);
                break;
            case RoundKind.UnlockDoors:
                world.Fixtures.SetLocked(f, false);
                FixtureRegistry.OpenDoor(f);
                NoteDoorOpened(world, f, worker.Faction);
                break;
            case RoundKind.LockCages:
                if (!FixtureRegistry.IsFree(world.Characters, f) && f.Lock is { Locked: false }) world.Fixtures.SetLocked(f, true);
                break;
        }
    }

    /// <summary>
    /// Task_LockDoor's end on a door (<c>FUN_140337670</c>): an open door starts to swing shut and the lock is set at once; each locked door whose
    /// lock is the worker's faction's sets the house flag while no door of the home is open or opening (<c>FUN_1407933a0</c>, <b>Verified</b>: one
    /// locked door is enough, the others may just be closed). A smashed door neither closes nor takes the lock (<b>engine choice</b>: the original
    /// sets the locked byte, which a smashed door ignores), but the flag test still runs, so a home with one stops its lock-up as the original's does.
    /// </summary>
    void LockDoor(World world, Round r, Fixture f, CharacterCold worker)
    {
        if (!f.DoorBroken)
        {
            FixtureRegistry.CloseDoor(f);
            world.Fixtures.SetLocked(f, true);
        }

        if (r.Building is not { } home || worker.Faction != f.Owner) return;

        foreach (var d in world.Fixtures.OfBuilding(home))
            if (d.Kind == FixtureKind.Door && d.Motion is DoorMotion.Open or DoorMotion.Opening) return;
        _houseLocked.Add(home);
    }

    #endregion

    #region Orders and the hash

    /// <summary>
    /// An order takes the worker (<see cref="Player.IDutyRelease"/>): its way through a door is dropped (<see cref="ReleaseDoorWay"/>). A goal's
    /// act-out (a warden's, a gate guard's) ends with its goal, which the AI releases.
    /// </summary>
    void Player.IDutyRelease.ReleaseForOrder(World world, int slot)
    {
        if (world.Characters.Cold(slot) is { } c) ReleaseDoorWay(world.Characters, slot, c);
    }

    /// <summary>
    /// Adds the locked homes and the gate guards', wardens' and escapes' goals to the world's state hash, while there are any.
    /// </summary>
    public void Hash(ref StateHasher hasher)
    {
        // only while a home is locked up, so worlds without them hash as before
        if (_houseLocked.Count > 0)
        {
            hasher.Add(0x526F756E);
            hasher.Add(_houseLocked.Count);
            foreach (var b in _houseLocked) hasher.Add(Rng.StableHash(b));
        }

        _gateGoals?.Hash(ref hasher);
        _wardens?.Hash(ref hasher);
        _escapes?.Hash(ref hasher);
    }

    #endregion
}
