using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;
using Meitou.Simulation.Ai;

namespace Meitou.Simulation.Locks;

/// <summary>What a <see cref="Round"/> does: free a served prisoner (goal 211), lock a home's doors at night (155, 197), unlock them when that is over, lock a faction's cages (195).</summary>
public enum RoundKind : byte { Release, LockDoors, UnlockDoors, LockCages }

/// <summary>Where a round is: unlocking its own shut building to get in, walking to the next target, working the lock (the owner's 3.3 s).</summary>
public enum RoundPhase : byte { Keys, Walk, Work }

/// <summary>One NPC's round of lock work (serial state of the <see cref="LockSystem"/>): the fixtures it works, in order.</summary>
public sealed class Round
{
    public required RoundKind Kind { get; init; }
    public required CharacterId Worker { get; init; }
    public required int Faction { get; init; }
    /// <summary>Fixture ids: the cage (release, cages) or the home's doors.</summary>
    public required string[] Targets { get; init; }
    /// <summary>The building of the doors (door rounds), and the prisoner (release).</summary>
    public string? Building { get; init; }
    public CharacterId Prisoner { get; init; } = CharacterId.None;
    /// <summary>Within this of a target the worker works it (a cage's <c>use range</c> + <see cref="LockSystem.Reach"/>; for doors the home's extent).</summary>
    public required float Reach { get; init; }
    public int Index { get; set; }
    public RoundPhase Phase { get; set; }
    /// <summary>Game seconds in the phase, of the whole round, and walks that ended away from the target.</summary>
    public float Time { get; set; }
    public float Age { get; set; }
    public int Walks { get; set; }

    internal void Hash(ref StateHasher h)
    {
        h.Add((int)Kind); h.Add(Worker); h.Add(Faction); h.Add(Targets.Length);
        foreach (var t in Targets) h.Add(Rng.StableHash(t));
        h.Add(Prisoner); h.Add(Index); h.Add((int)Phase); h.Add(Time); h.Add(Age); h.Add(Walks);
    }
}

/// <summary>
/// The NPCs' lock rounds (docs/simulation.md "Arrest and jail"; the original's goals in docs/game/locks.md and economy.md 11.5). Serial, in <c>SlowWorld</c>; a sweep every
/// <see cref="RoundEveryTicks"/> ticks starts at most <see cref="MaxNewRounds"/> rounds with at most <see cref="MaxRounds"/> running, each worked by one NPC the AI leaves alone
/// meanwhile (<see cref="CharacterCold.Engaged"/>). The work at a lock is the owner's lock/unlock time, 0.3 per second (<b>Verified</b>).
/// <list type="bullet">
/// <item><b>Release</b> (211 "police free prisoners", economy.md 11.5): an awake prisoner whose sentence is served, in a cage of a faction that books bounties, is freed by an NPC of the
/// cage's faction (one whose role list holds goal 211 in its hours first, else a police-like one, the nearest within <see cref="ReleaserRange"/>): it walks there, unlocks the cage and
/// lets the prisoner out (<see cref="FixtureRegistry.Release"/> in its faction's name). <b>Engine choice</b>: with no such NPC, or after <see cref="ReleaseTimeout"/>, the system frees
/// it in the jail faction's name. Cages of factions that book no bounties (slavers, bandits, the player's) free nobody.</item>
/// <item><b>Lock-up</b> (155, 197): while a member's role list holds the goal in its hours, a free member that has it locks every door of the squad's home, door by door; 155 only
/// once every member is inside (engine: within the home's extent) and, for both, no stranger is (<c>!strangers in house</c>). When no member's goal is in its hours any more,
/// a free member unlocks what a round locked (<b>engine choice</b>: the morning unlock is not traced; the squad's "house locked" flag is kept here, not saved).</item>
/// <item><b>Wardens</b> (195): a free member with the goal in its hours locks the occupied, unlocked cages of its faction in its town (not one whose prisoner is being freed).</item>
/// <item><b>Keys</b> (<b>engine choice</b>): a worker whose target is in a building shut by its own faction's (or its home's) locked doors unlocks them before it walks, standing,
/// as the simulation does not know where a door is. An arrest's guard carrying a captive to a cage behind its own faction's shut doors has them opened at once.</item>
/// </list>
/// </summary>
public sealed partial class LockSystem : IStateHashed
{
    /// <summary>The owner's lock and unlock tasks progress 0.3 per game second (docs/game/locks.md "Doors", <b>Verified</b>).</summary>
    public const float LockWorkSeconds = 1 / 0.3f;
    /// <summary>Engine choices of the rounds (see the class remarks).</summary>
    public const int RoundEveryTicks = 30, MaxRounds = 8, MaxNewRounds = 4, RoundWalks = 3;
    public const float ReleaserRange = AggroSystem.OutdoorSight, ReleaseTimeout = 120, WalkSlack = 200;

    readonly List<Round> rounds = [];
    // Homes a lock-up round locked (the squad's "house locked" flag), by building instance id; sorted so the hash has a fixed order.
    readonly SortedSet<string> houseLocked = new(StringComparer.Ordinal);
    readonly Dictionary<string, float> extents = new(StringComparer.Ordinal);
    Crime.ArrestSystem? arrests;
    bool arrestsLooked;

    public IReadOnlyList<Round> ActiveRounds => rounds;

    /// <summary>Meitou's save key (bool, on the building's GAMESTATE_BUILDING) of a home a lock-up round locked: without it a loaded night's locked homes would never be unlocked in the morning.</summary>
    public const string HouseLockedKey = "meitou house locked";
    bool savedRead;

    /// <summary>Takes the homes a loaded save had locked up from the zones' building records (<see cref="FixtureRegistry.Saved"/>), once.</summary>
    void ReadSaved(World world)
    {
        if (savedRead) return;
        savedRead = true;
        if (world.Fixtures.Saved is not { } saved) return;
        foreach (var (building, state) in saved)
            if (state.Bools.GetValueOrDefault(HouseLockedKey)) houseLocked.Add(building);
    }

    /// <summary>A lock-up round locked this home and nothing has unlocked it since (what <see cref="Saving.SaveCapture"/> writes as <see cref="HouseLockedKey"/>).</summary>
    public bool IsHouseLocked(World world, string building)
    {
        ReadSaved(world);
        return houseLocked.Contains(building);
    }

    void Rounds(World world)
    {
        ReadSaved(world);
        OpenArrestWays(world);
        for (int k = 0; k < rounds.Count;)
        {
            if (Step(world, rounds[k])) k++;
            else rounds.RemoveAt(k);
        }
        if (world.Tick % RoundEveryTicks == 0) Sweep(world);
    }

    Crime.ArrestSystem? Arrests(World world)
    {
        if (!arrestsLooked)
        {
            arrestsLooked = true;
            foreach (var s in world.Systems) if (s is Crime.ArrestSystem a) { arrests = a; break; }
        }
        return arrests;
    }

    bool InArrest(World world, CharacterId id)
    {
        if (Arrests(world) is not { } a) return false;
        foreach (var x in a.Arrests) if (x.Guard == id || x.Target == id) return true;
        return false;
    }

    /// <summary>An arrest's guard carrying a captive to a cage behind his own faction's shut doors: they open for him (engine choice, see the class remarks).</summary>
    void OpenArrestWays(World world)
    {
        if (Arrests(world) is not { Arrests.Count: > 0 } a) return;
        foreach (var x in a.Arrests)
            if (x.Phase == Crime.ArrestPhase.Walk && x.Cage is { } id && world.Fixtures.Find(id) is { } cage && world.Fixtures.IsShut(cage.Building) && world.Fixtures.DoorOwner(cage.Building) == x.Faction)
                world.Fixtures.SetDoorsLocked(cage.Building, false);
    }

    // ------------------------------------------------------------------ the sweep

    bool Busy(CharacterId id)
    {
        foreach (var r in rounds) if (r.Worker == id || r.Prisoner == id) return true;
        return false;
    }

    bool Targeted(string id)
    {
        foreach (var r in rounds)
        {
            if (r.Building == id) return true;
            foreach (var t in r.Targets) if (t == id) return true;
        }
        return false;
    }

    /// <summary>A worker the AI holds and nothing else: idle, wandering, at its post or asleep, not in a round or an arrest.</summary>
    bool Free(World world, int slot)
    {
        var table = world.Characters;
        return table.Next[slot].Alive && table.Cold(slot) is { } c && AiSystem.Holds(table.Next[slot], c) && !Busy(table.IdOf(slot)) && !InArrest(world, table.IdOf(slot));
    }

    /// <summary>The goal of <paramref name="type"/> is in the character's current role list, in its hours (the original's package goals).</summary>
    static bool HasGoal(World world, int slot, CharacterCold c, TaskType type, int hour)
    {
        if (c.Ai is not { Squad: { Ai: { } board } squad }) return false;
        var roles = board.Roles[(int)AiSystem.RoleOf(squad, world.Characters.IdOf(slot), c)];
        for (int k = 0; k < roles.Count; k++) if (roles[k].Task.Type == type && roles[k].InWindow(hour)) return true;
        return false;
    }

    void Sweep(World world)
    {
        int started = 0;
        ReleaseSweep(world, ref started);
        LockUpSweep(world, ref started);
    }

    void ReleaseSweep(World world, ref int started)
    {
        if (Crimes is null) return;
        var table = world.Characters;
        var next = table.Next;
        int hour = world.Hour;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!next[s].Alive || table.Cold(s) is not { IsImprisoned: true, InWhat: { } where } c || c.Medical is { Incapacitated: true }) continue;
            if (!FixtureRegistry.SentenceServed(c, world.GameHours) || world.Fixtures.Find(where) is not { } cage || !Crimes.Factions.Books(cage.Owner)) continue;
            var prisoner = table.IdOf(s);
            if (Busy(prisoner)) continue;
            int releaser = -1, bestRank = int.MaxValue;
            float bestD2 = ReleaserRange * ReleaserRange;
            for (int w = 0; w < table.HighWater; w++)
            {
                if (w == s || !next[w].Alive || table.Cold(w) is not { IsPlayer: false } wc || wc.Faction != cage.Owner) continue;
                int rank = HasGoal(world, w, wc, TaskType.POLICE_FREE_PRISONERS_WHEN_DONE, hour) ? 0 : Crimes.IsPoliceLike(world, wc) ? 1 : -1;
                if (rank < 0 || rank > bestRank || !Free(world, w)) continue;
                float d2 = Vector3.DistanceSquared(next[w].Position, cage.Position);
                if (rank == bestRank && d2 >= bestD2 || d2 > ReleaserRange * ReleaserRange) continue;
                (releaser, bestRank, bestD2) = (w, rank, d2);
            }
            if (releaser < 0)
            {
                world.Fixtures.Release(world, prisoner, cage.Owner, Crimes);   // nobody to do it: the jail's faction frees it (engine)
                continue;
            }
            if (rounds.Count >= MaxRounds || started >= MaxNewRounds) return;
            Start(world, releaser, new Round
            {
                Kind = RoundKind.Release, Worker = table.IdOf(releaser), Faction = table.Cold(releaser)!.Faction, Targets = [cage.Id], Prisoner = prisoner,
                Reach = cage.UseRange + Reach,
            }, cage);
            started++;
        }
    }

    void LockUpSweep(World world, ref int started)
    {
        var table = world.Characters;
        int hour = world.Hour;
        foreach (var squad in world.Squads.All)
        {
            if (rounds.Count >= MaxRounds || started >= MaxNewRounds) return;
            if (squad.Ai is not { } board) continue;
            int lockUp = -1, outside = -1, warden = -1, any = -1;
            bool inside155 = false, from197 = false, cages = false;
            foreach (var m in squad.Members)
            {
                if (!table.TryResolveNext(m, out int s) || table.Cold(s) is not { } c) continue;
                bool free = Free(world, s);
                if (free && any < 0) any = s;
                if (HasGoal(world, s, c, TaskType.LOCK_ALL_MY_DOORS, hour)) { inside155 = true; if (free && lockUp < 0) lockUp = s; }
                if (HasGoal(world, s, c, TaskType.LOCK_ALL_MY_DOORS_FROM_OUTSIDE, hour)) { from197 = true; if (free && outside < 0) outside = s; }
                if (HasGoal(world, s, c, TaskType.LOCK_ALL_THE_CAGES, hour)) { cages = true; if (free && warden < 0) warden = s; }
            }
            if (cages && warden >= 0 && board.Town >= 0) LockCages(world, squad, board, warden, ref started);
            if (board.OwnHome is not { } home || world.Fixtures.DoorOwner(home.Key) < 0 || world.Fixtures.OwnsBuilding(home.Key, world.Player.Faction) || Targeted(home.Key)) continue;
            string building = home.Key;
            if (inside155 || from197)
            {
                if (world.Fixtures.AllDoorsLocked(building)) continue;
                int worker = inside155 ? lockUp : outside;
                if (worker < 0 && from197) worker = outside;
                if (worker < 0 || Busy(table.IdOf(worker))) continue;   // the warden of this sweep may be it
                float extent = Extent(home);
                if (inside155 && !AllInside(world, squad, home, extent)) continue;
                if (StrangerInside(world, squad, home, extent)) continue;
                StartDoors(world, worker, RoundKind.LockDoors, building, extent);
                started++;
            }
            else if (houseLocked.Contains(building))
            {
                if (!world.Fixtures.AllDoorsLocked(building)) { houseLocked.Remove(building); continue; }   // opened or broken meanwhile
                if (any < 0 || Busy(table.IdOf(any))) continue;
                StartDoors(world, any, RoundKind.UnlockDoors, building, Extent(home));
                started++;
            }
        }
    }

    void LockCages(World world, Squad squad, Blackboard board, int warden, ref int started)
    {
        var table = world.Characters;
        List<string>? targets = null;
        foreach (var f in world.Fixtures.All)
        {
            if (f.Kind != FixtureKind.Cage || f.Owner != squad.Faction || f.Site != board.Town || f.Lock is not { Locked: false, Broken: false } || FixtureRegistry.IsFree(table, f) || Targeted(f.Id)) continue;
            // Not one whose prisoner is about to be freed (served, in a jail that frees).
            if (table.TryResolveNext(f.Occupant, out int o) && FixtureRegistry.SentenceServed(table.Cold(o)!, world.GameHours) && Crimes?.Factions.Books(f.Owner) == true) continue;
            (targets ??= []).Add(f.Id);
            if (targets.Count >= 4) break;
        }
        if (targets is null) return;
        var first = world.Fixtures.Find(targets[0])!;
        Start(world, warden, new Round
        {
            Kind = RoundKind.LockCages, Worker = table.IdOf(warden), Faction = table.Cold(warden)!.Faction, Targets = [.. targets], Reach = first.UseRange + Reach,
        }, first);
        started++;
    }

    void StartDoors(World world, int worker, RoundKind kind, string building, float extent)
    {
        var doors = new List<string>();
        foreach (var f in world.Fixtures.OfBuilding(building)) if (f.Kind == FixtureKind.Door) doors.Add(f.Id);
        var first = world.Fixtures.Find(doors[0])!;
        Start(world, worker, new Round
        {
            Kind = kind, Worker = world.Characters.IdOf(worker), Faction = world.Characters.Cold(worker)!.Faction, Targets = [.. doors], Building = building, Reach = extent,
        }, first);
    }

    /// <summary>How far from a building's origin its inside reaches (engine: its farthest node or bed + 10, at least 30).</summary>
    float Extent(TownBuilding b)
    {
        if (extents.TryGetValue(b.Key, out float e)) return e;
        float r = 0;
        foreach (var s in b.Spots) r = MathF.Max(r, Vector2.Distance(new Vector2(s.Position.X, s.Position.Z), new Vector2(b.Position.X, b.Position.Z)));
        return extents[b.Key] = MathF.Max(30, r + 10);
    }

    static bool Within(in CharacterHot n, TownBuilding b, float extent) =>
        Vector2.DistanceSquared(new Vector2(n.Position.X, n.Position.Z), new Vector2(b.Position.X, b.Position.Z)) <= extent * extent;

    /// <summary>Every member that is about (alive, loaded, not carried or held elsewhere) is inside the home.</summary>
    static bool AllInside(World world, Squad squad, TownBuilding home, float extent)
    {
        var table = world.Characters;
        foreach (var m in squad.Members)
            if (table.TryResolveNext(m, out int s) && table.Next[s].Alive && table.Cold(s) is { Medical.Dead: false } && !Within(table.Next[s], home, extent)) return false;
        return true;
    }

    /// <summary>Someone not of the squad (and not caged) is inside the home: the original's <c>strangers in house</c>.</summary>
    static bool StrangerInside(World world, Squad squad, TownBuilding home, float extent)
    {
        var table = world.Characters;
        var next = table.Next;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!next[s].Alive || table.Cold(s) is not { } c || c.SquadId == squad.Id || c.IsImprisoned || c.Medical is { Dead: true }) continue;
            if (Within(next[s], home, extent)) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ a round

    void Start(World world, int worker, Round round, Fixture first)
    {
        var table = world.Characters;
        var c = table.Cold(worker)!;
        c.Engaged = true;   // the AI leaves it alone until the round ends
        rounds.Add(round);
        MovementSystem.Stop(table, worker);   // up from its bed or post
        if (round.Kind is not (RoundKind.LockDoors or RoundKind.UnlockDoors) && NeedsKeys(world, round, c, first)) round.Phase = RoundPhase.Keys;
        else Walk(world, worker, round, first);
    }

    /// <summary>The target's building is shut by doors the worker may open: its faction's, or its squad's home.</summary>
    static bool NeedsKeys(World world, Round round, CharacterCold c, Fixture f) =>
        world.Fixtures.IsShut(f.Building) && (world.Fixtures.DoorOwner(f.Building) == round.Faction || c.Ai?.Squad.Ai?.OwnHome?.Key == f.Building);

    static void Walk(World world, int worker, Round round, Fixture f)
    {
        round.Phase = RoundPhase.Walk;
        round.Time = 0;
        if (Distance(world.Characters.Next[worker], f) > round.Reach) MovementSystem.GoTo(world.Characters, worker, new Vector2(f.Position.X, f.Position.Z), SpeedMode.Walk);
    }

    static float Distance(in CharacterHot n, Fixture f) => Vector2.Distance(new Vector2(n.Position.X, n.Position.Z), new Vector2(f.Position.X, f.Position.Z));

    /// <summary>One tick of a round; false when it is over.</summary>
    bool Step(World world, Round r)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(r.Worker, out int w) || table.Cold(w) is not { Medical.Incapacitated: false, InCombat: false, Fleeing: false, IsImprisoned: false, Carrying.IsNone: true, CarriedBy.IsNone: true } wc
            || table.Next[w].Task is not ((byte)CharacterTask.Idle or (byte)CharacterTask.GoTo) || InArrest(world, r.Worker))
            return End(world, r, fallback: false);   // taken by something else: the next sweep looks again
        float dt = world.TickSeconds;
        r.Age += dt;
        if (r.Kind == RoundKind.Release)
        {
            // Freed meanwhile (a bail, a pick, a lift), or no longer due.
            if (!table.TryResolveNext(r.Prisoner, out int p) || table.Cold(p) is not { IsImprisoned: true } pc || pc.InWhat != r.Targets[0]) return End(world, r, fallback: false);
            if (r.Age > ReleaseTimeout) return End(world, r, fallback: true);
        }
        if (r.Index >= r.Targets.Length || world.Fixtures.Find(r.Targets[r.Index]) is not { } f) return End(world, r, fallback: true);
        if (r.Faction != world.Player.Faction && world.Fixtures.OwnsBuilding(f.Building, world.Player.Faction)) return End(world, r, fallback: false);
        ref var n = ref table.Next[w];
        switch (r.Phase)
        {
            case RoundPhase.Keys:
                r.Time += dt;
                if (r.Time < LockWorkSeconds) return true;
                world.Fixtures.SetDoorsLocked(f.Building, false);
                Walk(world, w, r, f);
                return true;
            case RoundPhase.Walk:
            {
                bool walking = n.Task == (byte)CharacterTask.GoTo;
                float d = Distance(n, f);
                if (d <= r.Reach || !walking && d <= WalkSlack)
                {
                    if (walking) MovementSystem.Stop(table, w);
                    r.Phase = RoundPhase.Work;
                    r.Time = 0;
                    return true;
                }
                if (walking) return true;
                if (++r.Walks > RoundWalks)
                {
                    if (f.Kind == FixtureKind.Cage) world.Fixtures.MarkUnreachable(world, f, r.Faction);
                    return End(world, r, fallback: true);
                }
                MovementSystem.GoTo(table, w, new Vector2(f.Position.X, f.Position.Z), SpeedMode.Walk);
                return true;
            }
            case RoundPhase.Work:
                r.Time += dt;
                if (r.Time < LockWorkSeconds) return true;
                Apply(world, r, f, wc);
                world.EmitSound(r.Kind is RoundKind.LockDoors or RoundKind.LockCages ? SoundKind.Locked : SoundKind.Unlocked, w, n.Position);
                if (++r.Index >= r.Targets.Length) return End(world, r, fallback: false, done: true);
                var nextTarget = world.Fixtures.Find(r.Targets[r.Index]);
                if (nextTarget is null) return End(world, r, fallback: false, done: true);
                Walk(world, w, r, nextTarget);
                if (r.Phase == RoundPhase.Walk && Distance(n, nextTarget) <= r.Reach) r.Phase = RoundPhase.Work;   // the next door is where it stands
                return true;
        }
        return true;
    }

    void Apply(World world, Round r, Fixture f, CharacterCold worker)
    {
        switch (r.Kind)
        {
            case RoundKind.Release:
                world.Fixtures.SetLocked(f, false);
                // A releaser of a faction that books bounties tells the prisoner it may go (EV_PRISONER_FREE_TO_GO, Task_ReleasePrisoner, economy.md 11.5).
                if (world.Fixtures.Release(world, r.Prisoner, r.Faction, Crimes) && Crimes?.Factions.Books(r.Faction) == true)
                    world.Defer(new Dialogue.DialogueEvent(r.Worker, r.Prisoner, PrisonerFreeToGo) { Tick = world.Tick + 1 });
                break;
            case RoundKind.LockDoors:
                f.Motion = DoorMotion.Closed;   // locking closes the door first
                f.OpenAmount = 0;
                world.Fixtures.SetLocked(f, true);
                break;
            case RoundKind.UnlockDoors:
                world.Fixtures.SetLocked(f, false);
                break;
            case RoundKind.LockCages:
                if (!FixtureRegistry.IsFree(world.Characters, f) && f.Lock is { Locked: false }) world.Fixtures.SetLocked(f, true);
                break;
        }
    }

    /// <summary>Ends a round: the worker goes back to its routine; an unfinished release falls back to the system (<paramref name="fallback"/>), a finished door round sets or clears the house flag.</summary>
    bool End(World world, Round r, bool fallback, bool done = false)
    {
        var table = world.Characters;
        if (table.TryResolveNext(r.Worker, out int w) && table.Cold(w) is { } wc && !InArrest(world, r.Worker))
        {
            wc.Engaged = false;
            if (table.Next[w].Task == (byte)CharacterTask.GoTo) MovementSystem.Stop(table, w);
        }
        if (fallback && r.Kind == RoundKind.Release && table.TryResolveNext(r.Prisoner, out int p) && table.Cold(p) is { IsImprisoned: true } pc && pc.InWhat == r.Targets[0]
            && world.Fixtures.Find(r.Targets[0]) is { } cage)
            world.Fixtures.Release(world, r.Prisoner, cage.Owner, Crimes);
        if (done && r.Building is { } b)
        {
            if (r.Kind == RoundKind.LockDoors) houseLocked.Add(b);
            else if (r.Kind == RoundKind.UnlockDoors) houseLocked.Remove(b);
        }
        return false;
    }

    public void Hash(ref StateHasher hasher)
    {
        if (rounds.Count == 0 && houseLocked.Count == 0) return;   // only while a round runs or a home is locked up, so worlds without them hash as before
        hasher.Add(0x526F756E);
        hasher.Add(rounds.Count);
        foreach (var r in rounds) r.Hash(ref hasher);
        hasher.Add(houseLocked.Count);
        foreach (var b in houseLocked) hasher.Add(Rng.StableHash(b));
    }
}
