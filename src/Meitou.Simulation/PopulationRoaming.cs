using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.World;

namespace Meitou.Simulation;

/// <summary>
/// Roaming squads (docs/game/factions-squads-towns.md 6.4, 6.3; ai.md "Unloaded squads"; game-loop.md "Factions and squads").
/// <list type="bullet">
/// <item><b>Pool</b> (6.4, Observed): a TOWN's <c>roaming squads</c> (template, v0 = weight) feed the town's second pool; its budget is the faction's
/// <c>roaming population</c> (default 50). A loaded town below its cap draws one squad per look, each entry weighted by <c>max(0, v0 - squads of that template
/// the town already has)</c> (6.2's town branch with k = 1). The cap is <see cref="PopulationSettings.RoamingShare"/> of the budget (0.7 is the nests'
/// rule; for towns <b>Unknown</b>); a town not loaded makes none.</item>
/// <item><b>Travel</b> (engine choice on the Observed destination chooser <c>FUN_14092c5e0</c>: the nearest 4 towns by distance, hostile ones skipped, one at
/// random): out to a town, a wait, then home to the origin, a wait, then out again. Settlements only (outposts, towns, villages, military).</item>
/// <item><b>Active and unloaded halves</b> (game-loop.md): a <see cref="Platoon"/> whose position is in an active zone has its characters; one that is not for
/// <see cref="PopulationSettings.RoamUnloadGraceSeconds"/> loses them and goes on as a stand-in at its job's speed (<see cref="StandInSpeed"/>), stepped in parallel; it
/// gets its characters back (kept, or made again from its key) on walkable ground when a zone near it is active.</item>
/// </list>
/// </summary>
public sealed partial class PopulationSystem
{
    readonly ConcurrentQueue<BuiltRoamer> builtRoamers = new();
    readonly List<Platoon> platoonBuffer = [];   // StepRoaming's copy of the registry (a step may remove a platoon)
    readonly List<Vector2> playerPositions = [];

    sealed record BuiltRoamer(int Platoon, BuiltSquad? Squad, List<(Saving.SavedMember Member, Saving.SavedParts Parts)>? Saved = null);

    static bool IsSettlement(TownType t) => t is TownType.Town or TownType.Village or TownType.Outpost or TownType.Military;

    Vector2 SiteCentre(int site) => new(data.Sites[site].Position.X, data.Sites[site].Position.Z);

    /// <summary>Whether a platoon should have its characters: a zone near it is active, or (a town's own saved squad) its town's zone is, as the town's fresh squads are made with the town.</summary>
    bool Wanted(Platoon p) =>
        active.Contains(WorldLayout.ZoneOf(p.Position.X, p.Position.Y)) || p.Pinned && p.Origin >= 0 && active.Contains(data.Sites[p.Origin].Zone);

    /// <summary>
    /// Whether a wanted platoon may be made now: the host's <see cref="ZoneGate"/> holds it back until the navmesh is ready, of its town's zone for a town's own (as the
    /// town's load is gated; its members may stand in a neighbouring zone), else of its own zone.
    /// </summary>
    bool Ready(Platoon p) =>
        ZoneGate is not { } gate || gate(p.Pinned && p.Origin >= 0 ? data.Sites[p.Origin].Zone : WorldLayout.ZoneOf(p.Position.X, p.Position.Y));

    /// <summary>
    /// The activation dwell (<c>FUN_1407ebf30</c>): with its zone ready, a platoon outside the zone's box (<see cref="ZoneActivation.TrimmedBox"/>, trim 170) waits 2 s again, inside
    /// it counts <paramref name="dt"/> down; true once below 0. A zone not ready changes nothing.
    /// </summary>
    bool Dwelt(Platoon p, float dt)
    {
        if (!Ready(p)) return false;
        var zone = WorldLayout.ZoneOf(p.Position.X, p.Position.Y);   // the zone at the platoon's own position, as the original
        var (minX, minZ, maxX, maxZ) = ZoneActivation.TrimmedBox(zone, active.Contains, ZoneActivation.ActivationTrim);
        if (p.Position.X < minX || p.Position.X > maxX || p.Position.Y < minZ || p.Position.Y > maxZ) p.DwellLeft = 2;
        else p.DwellLeft -= dt;
        return p.DwellLeft < 0;
    }

    // ---- the look at the roaming squads, in the slow phase ----

    void StepRoaming(World world)
    {
        float dt = settings.CheckEveryTicks * world.TickSeconds;
        platoonBuffer.Clear();
        platoonBuffer.AddRange(world.Platoons.All);
        // The off-screen step (docs/simulation.md "Off-screen tier"): every travelling stand-in moves on, in parallel; each changes only its own platoon and reads
        // nothing another one changes, so the result does not depend on the order. What changes the registry or the world follows, serially, in id order.
        movers.Clear();
        foreach (var p in platoonBuffer)
            if (p.State == PlatoonState.Unloaded && !p.Pinned && p.Campaign is null) movers.Add(p);   // a campaign's are moved by it (they follow their leader)
        world.ParallelFor((movers.Count + MoversPerJob - 1) / MoversPerJob, job =>
        {
            for (int i = job * MoversPerJob, end = Math.Min(movers.Count, i + MoversPerJob); i < end; i++) StepOffscreen(world, movers[i], dt);
        });
        foreach (var p in platoonBuffer)
        {
            switch (p.State)
            {
                case PlatoonState.Unloaded:
                    if (p.OrphanSeconds > OrphanLimitSeconds)
                    {
                        Despawn(world, p);
                        break;
                    }
                    if (p.State == PlatoonState.Unloaded && Dwelt(p, dt) && Wanted(p)) Activate(world, p);
                    break;
                case PlatoonState.Loaded:
                    StepLoaded(world, p, dt);
                    break;
            }
        }
        if (world.Tick % settings.RoamCheckTicks == 0)
        {
            EnsurePools(world);
            GrowPools(world);
            CheckTownWipes(world);
            FillPools(world);
            StepHomeless(world);
            if (OffscreenFights) Fights(world);
        }
    }

    /// <summary>The original's orphan timer: a platoon it does not keep is activated, and so despawned, after this many seconds unloaded.</summary>
    const float OrphanLimitSeconds = 60;

    /// <summary>
    /// Whether a platoon out of the world keeps its characters: by the Meitou switches (<see cref="KeepWild"/> for a save's wild one, else <see cref="KeepRoamers"/>), and always
    /// while it occupies a town, as the original makes an occupier persistent (docs/game/ai.md "Package classes"), or when a save's platoon is <c>persistent</c> (the original
    /// never removes one, docs/game/factions-squads-towns.md "Object model": e.g. a dismissed character's squad); never one declared <see cref="Platoon.Dead"/>.
    /// </summary>
    bool Kept(Platoon p) => !p.Dead && ((p.Wild ? KeepWild : KeepRoamers) || Ai.AiSystem.Occupies(data.Ai, p.Package) || p.Saved?.Source.Record.Bools.GetValueOrDefault("persistent") == true);

    readonly List<Platoon> movers = [];
    /// <summary>Stand-ins one worker job steps (engine choice: a job costs more than a few stand-ins).</summary>
    const int MoversPerJob = 64;

    /// <summary>
    /// One stand-in's off-screen step (any thread): the Faithful orphan timer (a stand-in out of range counts up; despawned in the serial pass past the limit), then its
    /// travel (<see cref="StandIn"/>).
    /// </summary>
    void StepOffscreen(World world, Platoon p, float dt)
    {
        if (!Kept(p) && !Wanted(p)) p.OrphanSeconds += dt;
        else p.OrphanSeconds = 0;
        if (p.OrphanSeconds <= OrphanLimitSeconds) StandIn(world, p, dt);
    }

    /// <summary>
    /// Takes a platoon out of the world for good (serial phase), as the original does with one it does not keep: its characters (if made) and its stand-in; a save's platoon
    /// is marked so a save leaves it out.
    /// </summary>
    void Despawn(World world, Platoon p, bool killed = false, bool release = true)
    {
        if (release) ReleaseUniques(world, p, killed);
        if (world.Squads.Find(p.SquadId) is { } squad)
        {
            foreach (var m in squad.Members) RemoveMember(world, m);
            world.Squads.Remove(squad.Id);
        }
        if (p.Saved is { } saved)
        {
            saved.Despawned = true;
            saved.PlatoonId = -1;
        }
        world.Platoons.Remove(p.Id);
    }

    /// <summary>The unloaded half: moves a stand-in for the squad towards its target, with no physics (the original's <c>UnloadedPlatoon</c>).</summary>
    void StandIn(World world, Platoon p, float dt)
    {
        if (p.Pinned || p.Wild || p.Campaign is not null) return;   // a town's own saved squad, or a wild one, stays where it was; a campaign moves its own
        // It stands still (FUN_1407ee790, Verified): a platoon of the player's faction, one off the zone grid, one inside the trimmed box of a loaded zone (it waits to be made), and a
        // platoon left with a member caged.
        if (p.Faction >= 0 && p.Faction == PlayerFactionOf(world) || p.Imprisoned) return;
        var zone = WorldLayout.ZoneOf(p.Position.X, p.Position.Y);
        if (!zone.IsInsideGrid) return;
        if (active.Contains(zone))
        {
            var (minX, minZ, maxX, maxZ) = ZoneActivation.TrimmedBox(zone, active.Contains, StandInStopTrim);
            if (p.Position.X >= minX && p.Position.X <= maxX && p.Position.Y >= minZ && p.Position.Y <= maxZ) return;
        }
        if (ShippingCamp(p) is >= 0 and var camp)
        {
            // Shipping slaves: on to the camp, then it stays there; the slaves are handed over once it loads (Deliver 192).
            p.Target = camp;
            p.Point = null;
            var at = SiteCentre(camp);
            float left = Vector2.Distance(p.Position, at);
            if (left > settings.RoamArrivalRadius) Travel(p, at, MathF.Min(TravelTargetSpeed, StandInSpeedCap) * dt);
            return;
        }
        if (p.WaitLeft > 0)
        {
            p.WaitLeft -= dt;
            if (p.WaitLeft <= 0) ChooseNext(world, p);
            return;
        }
        if (Goal(p) is not { } goal)
        {
            ChooseNext(world, p);
            return;
        }
        var to = goal - p.Position;
        float d = to.Length();
        if (d <= (p.Point is not null ? Ai.PlatoonJobs.PatrolArrive : settings.RoamArrivalRadius))
        {
            Arrive(world, p);
            return;
        }
        Travel(p, goal, StandInSpeed(p, d) * dt);
    }

    /// <summary>
    /// Moves a stand-in <paramref name="step"/> units on its way to <paramref name="goal"/> (any thread; only this platoon changes): by the roads when the original's rule takes
    /// them (<see cref="RoadNetwork.Route"/>, worked out when the goal changes), point after point, else straight.
    /// </summary>
    void Travel(Platoon p, Vector2 goal, float step)
    {
        if (p.RouteGoal != goal)
        {
            p.RouteGoal = goal;
            p.Route = data.Roads is { } roads && p.Faction >= 0 && p.Faction < data.Factions.Count ? roads.Route(p.Position, goal, data.Factions[p.Faction].RoadPreference) : null;
            p.RouteStep = 0;
        }
        while (step > 0)
        {
            bool onRoute = p.Route is { } route && p.RouteStep < route.Count;
            var next = onRoute ? p.Route![p.RouteStep] : goal;
            var to = next - p.Position;
            float d = to.Length();
            if (d > step)
            {
                p.Position += to / d * step;
                return;
            }
            p.Position = next;
            step -= d;
            if (!onRoute) return;
            p.RouteStep++;
        }
    }

    /// <summary>
    /// A stand-in's speed in units per second, <paramref name="d"/> from its goal: the original's per job (docs/game/factions-squads-towns.md "Off-screen world": GOHOME 55,
    /// TRAVEL_TARGET 25, TRAVEL_TARGET_FAST 60 or 90 beyond 25000), else the engine's travel stand-in (25, 90 far; a short-range patrol keeps walking at 25, where the
    /// original's stands still), all capped at the original mover's 70.
    /// </summary>
    float StandInSpeed(Platoon p, float d)
    {
        bool far = d > settings.StandInFarDistance;
        float speed = p.Job switch
        {
            Ai.PlatoonJob.GoHome => 55,
            Ai.PlatoonJob.TravelTarget => 25,
            Ai.PlatoonJob.TravelTargetFast => far ? 90 : 60,
            Ai.PlatoonJob.PatrolShortRange => settings.StandInSpeed,
            _ => far ? settings.StandInFarSpeed : settings.StandInSpeed,
        };
        return MathF.Min(speed, StandInSpeedCap);
    }

    /// <summary>
    /// The slave camp a kept stand-in ships its slaves to, -1 when it does not (docs/simulation.md "Captives", off screen): its package is a shipping one (signal 11 or 28, whose
    /// <c>unloaded func</c> is 5 TRAVEL_TARGET in the data) and someone of it is a living slave (state 1). The original's unloaded TRAVEL_TARGET heads for the package's mission target
    /// and does not move inside it (docs/game/factions-squads-towns.md "Off-screen world"): the camp chosen when the package started (kept in <see cref="Platoon.Target"/> as the
    /// squad unloads), else the nearest slave camp not hostile to it, the loaded package's rule. A save's platoon not made yet reads its members from the save (slave role, <c>slavestate</c>
    /// 1, not dead). Any thread: reads the data and this platoon only.
    /// </summary>
    int ShippingCamp(Platoon p)
    {
        if (p.Package is not { } id || !IsShipping(id)) return -1;
        bool slaves = false;
        if (p.Stored is { } stored)
        {
            foreach (var m in stored.Members) if (m.Cold is { SlaveState: 1, Role: (int)SquadRole.Slave, Medical.Dead: false }) { slaves = true; break; }
        }
        else if (p.Saved is { } saved)
        {
            foreach (var m in saved.Members)
                if (!m.Dead && !m.Gone && m.Source.SquadMemberType == (int)SquadRole.Slave && m.Source.GameState?.Ints.GetValueOrDefault(Saving.SlaveSave.StateKey) == 1) { slaves = true; break; }
        }
        if (!slaves) return -1;
        if (p.Target >= 0 && p.Target < data.Sites.Count && data.Sites[p.Target].Town.Type == TownType.SlaveCamp) return p.Target;
        int best = -1;
        float bestD = float.MaxValue;
        foreach (var site in data.Sites)
        {
            if (site.Town.Type != TownType.SlaveCamp || site.Index >= data.SiteFactions.Count) continue;
            int f = data.SiteFactions[site.Index];
            if (f >= 0 && p.Faction >= 0 && data.Relations.Contains(f) && data.Relations.Contains(p.Faction) && data.Relations.IsHostile(f, p.Faction)) continue;
            float d = Vector2.DistanceSquared(p.Position, SiteCentre(site.Index));
            if (d < bestD) (best, bestD) = (site.Index, d);
        }
        return best;
    }

    /// <summary>A shipping package (signal 11 or 28) whose off-screen job is TRAVEL_TARGET (<c>unloaded func</c> 5, as all four in the base data).</summary>
    bool IsShipping(string package) => data.Ai.Package(package) is { Signal: 11 or 28, Unloaded: (int)Ai.PlatoonJob.TravelTarget };

    /// <summary>The original's TRAVEL_TARGET speed off screen without a campaign (docs/game/factions-squads-towns.md "Off-screen world").</summary>
    const float TravelTargetSpeed = 25;

    /// <summary>The trim of the box inside which an unloaded platoon in a loaded zone stops (<c>DAT_141718a0c</c> = 178.49998, <b>Verified</b>, <c>FUN_1407ee790</c>); the activation box's is <see cref="ZoneActivation.ActivationTrim"/>.</summary>
    const float StandInStopTrim = 178.49998f;

    /// <summary>The original unloaded mover's top speed (its constructor's 70; no other writer found, Observed).</summary>
    public const float StandInSpeedCap = 70;

    void Arrive(World world, Platoon p)
    {
        ulong roll = Rng.Hash(world.Seed, (ulong)p.Id, RngPurpose.Wander, (ulong)world.Tick);
        p.WaitLeft = settings.RoamWaitMin + Rng.Float(roll) * (settings.RoamWaitMax - settings.RoamWaitMin);
        if (p.Point is not null)
        {
            p.Point = null;   // a short-range patrol point: the next one after the wait
            return;
        }
        p.GoingHome = p.Target != p.Origin;   // having reached another town, the way home is next
    }

    /// <summary>Where a platoon is heading: its campaign's goal, else its patrol point, else its target town's centre; null when it has none.</summary>
    Vector2? Goal(Platoon p) => p.CampaignGoal ?? p.Point ?? (p.Target >= 0 ? SiteCentre(p.Target) : null);

    /// <summary>Picks where a platoon goes next: home after a visit, else one of the 4 nearest settlements that are not hostile to it.</summary>
    void ChooseNext(World world, Platoon p)
    {
        // The off-screen job of its package (docs/simulation.md "AI", Off screen): a point near home, or home; the rest travel as below.
        if (p.Job == Ai.PlatoonJob.PatrolShortRange)
        {
            p.Point = PatrolPoint(world, p);
            p.Target = p.Origin;
            p.GoingHome = false;
            return;
        }
        if (p.Job == Ai.PlatoonJob.GoHome)
        {
            p.Target = p.Origin;
            p.GoingHome = false;
            return;
        }
        if (p.GoingHome)
        {
            p.Target = p.Origin;
            return;
        }
        var candidates = new List<(float Distance, int Site)>();
        foreach (var site in data.Sites)
        {
            if (site.Index == p.Origin || !IsSettlement(site.Town.Type)) continue;
            int theirs = data.SiteFactions[site.Index];
            if (p.Faction >= 0 && theirs >= 0 && data.Relations.IsHostile(p.Faction, theirs)) continue;
            candidates.Add((Vector2.Distance(p.Position, SiteCentre(site.Index)), site.Index));
        }
        candidates.Sort(static (a, b) => a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance) : a.Site.CompareTo(b.Site));
        if (candidates.Count == 0)
        {
            p.Target = p.Origin;
            return;
        }
        int n = Math.Min(4, candidates.Count);
        ulong roll = Rng.Hash(world.Seed, (ulong)p.Id, RngPurpose.Wander, (ulong)world.Tick ^ SeedSalts.RoamNext);
        p.Target = candidates[(int)(Rng.Float(roll) * n) % n].Site;
        p.GoingHome = false;
    }

    /// <summary>A walkable point within <see cref="Ai.PlatoonJobs.PatrolRange"/> of the home town (seeded; 8 tries, else the town's centre).</summary>
    Vector2 PatrolPoint(World world, Platoon p)
    {
        var home = SiteCentre(p.Origin);
        for (int k = 0; k < 8; k++)
        {
            ulong roll = Rng.Hash(world.Seed, (ulong)p.Id, RngPurpose.Wander, (ulong)world.Tick ^ SeedSalts.PlatoonPatrol ^ (ulong)k << 32);
            var at = Rng.PointInDisc(roll, home, Ai.PlatoonJobs.PatrolRange);
            if (world.Walkability.IsWalkable(at.X, at.Y)) return at;
        }
        return home;
    }

    // ---- loaded roaming squads ----

    void StepLoaded(World world, Platoon p, float dt)
    {
        var table = world.Characters;
        var squad = world.Squads.Find(p.SquadId);
        if (squad is null || squad.Members.Count == 0 || squad.Leader.IsNone)
        {
            // Wiped out (or removed): the pool is free to make another (a saved platoon's characters are marked, for the next save).
            if (p.Saved is { } gone)
            {
                if (p.Stored is null) Saving.SavedCharacters.Store(world, gone, p.Position);
                gone.PlatoonId = -1;
            }
            if (squad is not null) world.Squads.Remove(squad.Id);
            world.Platoons.Remove(p.Id);
            return;
        }
        p.Position = PlatoonPosition(world, squad, p.Position);
        if (!p.Dead && (p.Faction < 0 || p.Faction != PlayerFactionOf(world)) && AllDead(world, squad)) DeclareDead(p);

        // Away from every active zone for long enough: only the stand-in goes on.
        if (Wanted(p)) p.UnloadAtTick = -1;
        // A town's own squad keeps the town's grace (it leaves with its town), a roaming one the roaming grace.
        else if (p.UnloadAtTick < 0) p.UnloadAtTick = world.Tick + (long)Math.Ceiling((p.Pinned ? settings.UnloadGraceSeconds : settings.RoamUnloadGraceSeconds) / world.TickSeconds);
        // Faithful: a platoon the original does not keep is not unloaded whole: each member out of the zone is destroyed, and the platoon stays while any is left.
        else if (world.Tick >= p.UnloadAtTick && !(!Kept(p) && !p.Pinned && p.Campaign is null && ThinOutOfZone(world, p, squad)))
        {
            p.Imprisoned = squad.Members.Any(m => table.TryResolveNext(m, out int ms) && table.Cold(ms)!.IsImprisoned);
            if (p.Campaign is null && squad.Ai is { } board) p.IntactFlag = board.Intact;   // kept as it was at unload (+0x100)
            if (squad.Ai?.Current is { } running) FollowPackage(world, p, running);
            if (p.Package is { } shipping && IsShipping(shipping) && squad.Ai is { MissionTown: >= 0 and var mission }) p.Target = mission;   // the camp it ships to (ShippingCamp)
            // Faithful: a platoon the original does not keep (not a town's own, not in a campaign, not occupying) is despawned as it leaves; a save's wild one by the wild switch.
            if ((p.Dead || !Kept(p) && !p.Pinned) && p.Campaign is null)
            {
                PayRoamerDespawn(world, p, squad);
                Despawn(world, p);
                return;
            }
            if (p.Saved is { } saved && p.Stored is null) Saving.SavedCharacters.Store(world, saved, p.Position);   // a made never-made one keeps its squad in Stored
            p.Money = squad.Money;
            // Meitou: a roaming squad the population made keeps its characters from now on.
            if (p.Stored is null && p.Saved is null && !p.Pinned) p.Stored = new StoredSquad { Name = squad.Name, Pinned = false };
            if (p.Stored is { } stored)
            {
                stored.Shop = squad.Shop;
                stored.Ai = Saving.AiSave.Of(squad.Ai) ?? squad.SavedAi;
                stored.Memory = Memory?.Take(squad.Id);
                stored.NextHomeFillTick = squad.NextHomeFillTick;
                stored.Package = p.Package = squad.Ai?.Current?.Id ?? stored.Package;
                p.Position = Store(world, squad, stored);
            }
            foreach (var m in squad.Members) { world.Uniques.Left(m); RemoveMember(world, m); }
            world.Squads.Remove(squad.Id);
            p.State = PlatoonState.Unloaded;
            p.DwellLeft = 5;
            p.SquadId = -1;
            p.UnloadAtTick = -1;
            return;
        }
        if (p.Campaign is not null)
        {
            StepCampaignLoaded(world, p, squad, dt);
            return;
        }
        if (p.Pinned || p.Wild || p.Dead) return;   // a town's own or a wild one: its AI runs it, it has no trips (nor has a dead one)
        // A leader in a fight or a loot is left to it: an order now would replace that task (the trip goes on when it is over).
        if (table.Next[squad.Leader.Slot].Task is (byte)CharacterTask.Attack or (byte)CharacterTask.Loot) return;

        if (p.WaitLeft > 0)
        {
            p.WaitLeft -= dt;
            if (p.WaitLeft <= 0)
            {
                ChooseNext(world, p);
                OrderLeader(world, p, squad);
            }
            return;
        }
        if (Goal(p) is not { } goal)
        {
            ChooseNext(world, p);
            OrderLeader(world, p, squad);
            return;
        }
        bool point = p.Point is not null;
        if (Vector2.Distance(p.Position, goal) <= settings.RoamArrivalRadius)
        {
            Arrive(world, p);
            // At the town the squad mills about in it.
            if (table.TryResolveNext(squad.Leader, out int slot))
            {
                TaskStart.Become(world, slot, CharacterTask.Wander, Meitou.Data.Gameplay.Ai.TaskType.WANDERER, TaskSource.Goal);
                table.Next[slot].Mode = SpeedMode.Walk;
                table.Next[slot].TaskTime = 0.5f;
            }
            squad.HomeCentre = goal;
            squad.HomeRadius = point ? 60 : Math.Max(data.Sites[p.Target].Town.SizeRadius * data.Sites[p.Target].Town.TownRadiusMult * 0.5f, 40);
            return;
        }
        // On the way: the leader keeps a go-to; ask again when it has none (arrived short, blocked, or just loaded).
        if (table.TryResolveNext(squad.Leader, out int leaderSlot))
        {
            ref var n = ref table.Next[leaderSlot];
            bool busy = n.Task == (byte)CharacterTask.GoTo && (n.Flags & (ushort)MoveFlags.AnyPath) != 0;
            p.OrderCooldown -= dt;
            if (!busy && p.OrderCooldown <= 0)
            {
                OrderLeader(world, p, squad);
                p.OrderCooldown = 4;
            }
        }
    }

    /// <summary>
    /// A squad unloading with <paramref name="running"/> as its package: the platoon keeps it and takes its off-screen job (<c>FUN_1407ee790</c> re-reads the job from the blackboard's
    /// current package each step, <c>FUN_140268840</c>, <b>Verified</b>). A job that goes to fixed places (home, a patrol round it) starts at once: no wait, a new destination.
    /// </summary>
    void FollowPackage(World world, Platoon p, Meitou.Data.Gameplay.Ai.AiPackage running)
    {
        p.Package = running.Id;
        var job = Ai.PlatoonJobs.JobOf(running.Unloaded);
        if (job == p.Job) return;
        p.Job = job;
        if (job is not (Ai.PlatoonJob.GoHome or Ai.PlatoonJob.PatrolShortRange) || p.Campaign is not null || p.Pinned || p.Wild) return;
        p.Point = null;
        p.WaitLeft = 0;
        ChooseNext(world, p);
    }

    /// <summary>The squared distance (600) within which a member goes with a leader that is out of the zone (<c>DAT_141714bd4</c> = 360000, <b>Verified</b>).</summary>
    const float LeaderOutOfZoneRadiusSq = 600 * 600;

    /// <summary>
    /// Whether a point is out of the loaded zones (<c>FUN_1407c06f0</c>, <b>Verified</b>): its zone is not active, or it lies outside that zone's box trimmed by
    /// <see cref="ZoneActivation.ActivationTrim"/> (the box of <see cref="Dwelt"/>).
    /// </summary>
    bool OutOfZone(Vector3 at)
    {
        var zone = WorldLayout.ZoneOf(at.X, at.Z);
        if (!active.Contains(zone)) return true;
        var (minX, minZ, maxX, maxZ) = ZoneActivation.TrimmedBox(zone, active.Contains, ZoneActivation.ActivationTrim);
        return at.X < minX || at.Z < minZ || at.X > maxX || at.Z > maxZ;
    }

    /// <summary>A member the squad's place may follow: there, not down (<see cref="Ai.SquadIntact.Down"/>: out, dead or carried) and not in a bed or cage.</summary>
    static bool Standing(CharacterTable table, CharacterId id) =>
        !Ai.SquadIntact.Down(table, id) && table.TryResolveNext(id, out int s) && table.Cold(s) is { InSomething: Locks.HeldState.None };

    /// <summary>
    /// The squad's effective leader (<c>FUN_140792b90</c>, <b>Verified</b>): its leader while standing, else the cached <see cref="Squad.Backup"/> while standing, else the first member (in
    /// list order) that is not down and stands in an active zone, which is cached as the backup; else the leader itself, down or not. The member scan is skipped while the leader is in
    /// a bed or cage: the original tests the <i>leader's</i> bed or cage state there, not the member's (<b>Verified</b> by the decompile; possibly a slip of the original).
    /// </summary>
    CharacterId EffectiveLeader(World world, Squad squad)
    {
        var table = world.Characters;
        if (Standing(table, squad.Leader)) return squad.Leader;
        if (Standing(table, squad.Backup)) return squad.Backup;
        if (table.TryResolveNext(squad.Leader, out int ls) && table.Cold(ls) is { InSomething: not Locks.HeldState.None }) return squad.Leader;
        foreach (var m in squad.Members)
        {
            if (Ai.SquadIntact.Down(table, m) || !table.TryResolveNext(m, out int s)) continue;
            var at = table.Previous[s].Position;
            if (!active.Contains(WorldLayout.ZoneOf(at.X, at.Z))) continue;
            squad.Backup = m;
            return m;
        }
        return squad.Leader;
    }

    /// <summary>
    /// A loaded platoon's place (<c>FUN_1404fed50</c>, <b>Verified</b>): its effective leader's while that one is not down, else the mean of the members that are not dead (a knocked-out
    /// one counts), else where it was.
    /// </summary>
    Vector2 PlatoonPosition(World world, Squad squad, Vector2 last)
    {
        var table = world.Characters;
        var lead = EffectiveLeader(world, squad);
        if (!Ai.SquadIntact.Down(table, lead) && table.TryResolveNext(lead, out int ls))
        {
            var at = table.Previous[ls].Position;
            return new Vector2(at.X, at.Z);
        }
        var sum = Vector2.Zero;
        int n = 0;
        foreach (var m in squad.Members)
        {
            if (!table.TryResolveNext(m, out int s) || table.Cold(s) is not { } cold || cold.DiedTick >= 0 || cold.Medical is { Dead: true }) continue;
            sum += new Vector2(table.Previous[s].Position.X, table.Previous[s].Position.Z);
            n++;
        }
        return n > 0 ? sum / n : last;
    }

    /// <summary>Whether no member is a character that is not dead (<c>FUN_1404fed50</c> counts every member but a dead character; a knocked-out one counts).</summary>
    static bool AllDead(World world, Squad squad)
    {
        var table = world.Characters;
        foreach (var m in squad.Members)
            if (table.TryResolveNext(m, out int s) && table.Cold(s) is { } cold && cold.DiedTick < 0 && cold.Medical is not { Dead: true }) return false;
        return true;
    }

    /// <summary>
    /// <c>Platoon::declareDead</c> (<c>FUN_140797090</c>, <b>Verified</b>), once: a squad of a template with <c>regenerates</c> whose town lives is only marked to be made afresh at its next
    /// activation (and may declare again); any other platoon is <see cref="Platoon.Dead"/>: out of its town's pools at once (<see cref="FillPools"/> no longer counts it), not kept, destroyed
    /// when it unloads. Its dead lie where they fell.
    /// </summary>
    void DeclareDead(Platoon p)
    {
        if (Regenerates(p)) p.Regenerate = true;
        else p.Dead = true;
    }

    readonly List<CharacterId> thinStay = [], thinGo = [];

    /// <summary>
    /// The unload of a platoon the original does not keep (<c>FUN_14079c600</c>, serial phase): not the whole squad at once but each living member by its own place. One out of the
    /// zone is destroyed ("wandered out of zone"); while the anchor (the effective leader, <see cref="EffectiveLeader"/>) is out of it, so is every member within 600 of it ("leader
    /// wandered out of zone"). A member in the zone and not near an absent leader stays, and the platoon with it. True when some stay (the others are gone, the squad goes on); false
    /// when none would, which leaves the whole squad to the caller's despawn (and its pool payment) unchanged. <b>Not built</b>: the exemption of a member in the camera's view
    /// (<c>Character+0x1a9</c>, render state), the split of imprisoned members into squads of their own first (<c>FUN_14079c1c0</c>), and that dead members count as staying in the zone (here a corpse is no part of it).
    /// </summary>
    bool ThinOutOfZone(World world, Platoon p, Squad squad)
    {
        var table = world.Characters;
        thinStay.Clear();
        thinGo.Clear();
        var anchor = EffectiveLeader(world, squad);
        bool leaderOut = false;
        var anchorAt = Vector3.Zero;
        if (table.TryResolveNext(anchor, out int anchorSlot))
        {
            anchorAt = table.Next[anchorSlot].Position;
            leaderOut = OutOfZone(anchorAt);
        }
        foreach (var m in squad.Members)
        {
            if (!Living(world, m) || !table.TryResolveNext(m, out int slot)) continue;   // a corpse is no part of the platoon's staying
            var at = table.Next[slot].Position;
            if (OutOfZone(at) || leaderOut && Vector3.DistanceSquared(at, anchorAt) < LeaderOutOfZoneRadiusSq) thinGo.Add(m);
            else thinStay.Add(m);
        }
        if (thinStay.Count == 0) return false;
        foreach (var m in thinGo)
        {
            if (table.Cold(m.Slot) is { } cold) ReleaseUnique(world, cold.RecordId, m, false);
            RemoveMember(world, m);
        }
        squad.Members.RemoveAll(m => thinGo.Contains(m));
        if (thinGo.Contains(squad.Leader)) squad.Leader = SquadRegistry.Successor(thinStay, table);
        if (table.TryResolveNext(squad.Leader, out int lead)) p.Position = new Vector2(table.Next[lead].Position.X, table.Next[lead].Position.Z);
        return true;
    }

    void OrderLeader(World world, Platoon p, Squad squad)
    {
        if (Goal(p) is not { } goal || !world.Characters.TryResolveNext(squad.Leader, out int slot)) return;
        // A short-range patrol and going home walk; travel runs (as before the packages).
        TaskStart.Walk(world, slot, goal, p.Job is Ai.PlatoonJob.PatrolShortRange or Ai.PlatoonJob.GoHome ? SpeedMode.Walk : SpeedMode.Run, Meitou.Data.Gameplay.Ai.TaskType.TRAVEL_TO_TARGET_TOWN,
            TaskSource.Goal);
    }

    // ---- unloaded to loaded ----

    void Activate(World world, Platoon p)
    {
        p.State = PlatoonState.Activating;
        // A stand-in that travelled comes back on walkable ground (it went in straight lines, over water and through buildings); one that did not keeps its characters' places.
        var anchor = p.Stored?.Anchor ?? p.Saved?.Anchor;
        bool travelled = anchor is not { } a || Vector2.DistanceSquared(a, p.Position) > 1;
        if (travelled && !p.Pinned && !p.Wild && WalkableSpots.Nearest(world.Walkability, p.Position, out var spot)) p.Position = spot;
        ulong seed = world.Seed;
        var position = p.Position;
        int id = p.Id;
        // A save's never-made squad: made from its template into this platoon (FUN_1404ffad0). With no one planned it has nothing to come as: it is dropped (its
        // record stays in the save, which will try again at the next load).
        if (p.Unmade)
        {
            if (MakeUnmade(world, p) is null)
            {
                if (p.Saved is { } none) none.PlatoonId = -1;
                world.Platoons.Remove(p.Id);
            }
            return;
        }
        // A town's squad that lost too many: made afresh (its characters are gone).
        if (p.Regenerate)
        {
            p.Regenerate = false;
            if (Regenerate(world, p) is not null) return;   // else (the template plans no one now) it comes back as it is
        }
        if (OffscreenHealing) Recover(world, p);
        // A stored squad: its own characters, at once (nothing to build).
        if (p.Stored is not null)
        {
            FinishRoamer(world, new BuiltRoamer(id, null));
            return;
        }
        // A save's platoon: its saved characters (their records read on the build thread, like a new squad's looks).
        if (p.Saved is { } saved && world.Saved is { } context)
        {
            if (work is null) FinishRoamer(world, new BuiltRoamer(id, null, Saving.SavedCharacters.Prepare(saved, data, context.Appearances, context)));
            else work.Add(() => builtRoamers.Enqueue(new BuiltRoamer(id, null, Saving.SavedCharacters.Prepare(saved, data, context.Appearances, context))));
            return;
        }
        if (work is null) FinishRoamer(world, new BuiltRoamer(id, BuildRoamer(p.TemplateId, p.Faction, p.Key, seed, position)));
        else
        {
            string template = p.TemplateId;
            int faction = p.Faction;
            ulong key = p.Key;
            work.Add(() => builtRoamers.Enqueue(new BuiltRoamer(id, BuildRoamer(template, faction, key, seed, position))));
        }
    }

    /// <summary>Null when the template is gone or plans no one now (e.g. a save's platoon whose members are all dead or unique).</summary>
    BuiltSquad? BuildRoamer(string templateId, int faction, ulong key, ulong seed, Vector2 position)
    {
        if (data.Db.Find(templateId) is not { } record) return null;
        var townFaction = faction >= 0 ? data.Factions[faction] : null;
        return BuildOne(SquadTemplate.From(record), faction, townFaction, key, seed, position);
    }

    void DrainRoamers(World world)
    {
        var ready = new List<BuiltRoamer>();
        while (builtRoamers.TryDequeue(out var b)) ready.Add(b);
        ready.Sort(static (a, b) => a.Platoon.CompareTo(b.Platoon));
        foreach (var b in ready) FinishRoamer(world, b);
    }

    void FinishRoamer(World world, BuiltRoamer b)
    {
        var p = world.Platoons.Find(b.Platoon);
        if (p is null || p.State != PlatoonState.Activating) return;
        Squad? squad;
        if (p.Stored is { } stored) squad = SpawnStored(world, p, stored);
        else if (p.Saved is { Wild: not null } wild)
        {
            // A save's wild platoon wanders round its nest, or (homeless) round where it stands.
            var (centre, homeRadius) = WildHome(wild, p.Position);
            squad = b.Saved is { } wildMembers ? Saving.SavedCharacters.Spawn(world, p, wild, wildMembers, data, assembly, centre, homeRadius) : null;
        }
        else
        {
            var site = data.Sites[p.Origin];
            float radius = site.Town.SizeRadius * site.Town.TownRadiusMult;
            if (b.Saved is { } members && p.Saved is { } saved)
                squad = Saving.SavedCharacters.Spawn(world, p, saved, members, data, assembly, new Vector2(site.Position.X, site.Position.Z), Math.Max(radius * 0.8f, 40));
            else squad = b.Squad is null ? null : SpawnSquad(world, b.Squad, site, radius, platoonId: p.Id);
        }
        if (squad is null)
        {
            // Nobody to spawn: the pool is free to make another (a saved platoon whose members are all dead stays in the save as it is).
            if (p.Saved is { } empty) empty.PlatoonId = -1;
            world.Platoons.Remove(p.Id);
            return;
        }
        if (squad.SpawnSize == 0) squad.SpawnSize = p.Saved?.Members.Count ?? p.Size;
        squad.Money = p.Money;
        p.State = PlatoonState.Loaded;
        p.SquadId = squad.Id;
        p.UnloadAtTick = -1;
        if (Goal(p) is not null && p.WaitLeft <= 0) OrderLeader(world, p, squad);
    }

    /// <summary>
    /// Where a save's wild platoon keeps to: its nest's centre and radius (as the nest's own squads, <c>PopulationWild.cs</c>) when it stands within 3 radii of it, else
    /// its own place with the homeless radius (engine choice: saves hold nest squads kilometres from their nest, and they are not walked back).
    /// </summary>
    (Vector2 Centre, float Radius) WildHome(Saving.SavedPlatoon sp, Vector2 at)
    {
        if (sp.Wild == WildKind.Nest && sp.WildSite >= 0 && sp.WildSite < WildSites.Count && WildSites[sp.WildSite].Nest is { } nest)
        {
            var centre = wildCentres[sp.WildSite] ?? WildSites[sp.WildSite].Spots[0];
            float radius = NestRadius(nest);
            if (Vector2.DistanceSquared(centre, at) <= 9 * radius * radius) return (centre, radius);
        }
        return (at, settings.HomelessHomeRadius);
    }

    // ---- the pools ----

    /// <summary>Every loaded town with roaming squads below its cap makes one (6.4, the town branch of 6.2's choice).</summary>
    void FillPools(World world)
    {
        var table = world.Characters;
        bool playersListed = false;   // the player's characters, scanned once per look and only when a town gets as far as placing a squad
        foreach (var site in data.Sites)
        {
            if (states[site.Index].Status != SiteStatus.Loaded || site.Town.RoamingSquads.Count == 0) continue;
            int factionIndex = data.SiteFactions[site.Index];
            if (factionIndex < 0) continue;
            int cap = (int)MathF.Floor(settings.RoamingShare * data.Factions[factionIndex].RoamingPopulation * settings.PopulationMultiplier);
            int members = 0;
            foreach (var p in world.Platoons.All) if (p.Origin == site.Index && !p.Pinned && p.Campaign is null && !p.Dead) members += p.Size;   // a town's own saved squads are not its roaming ones, nor campaign squads; a dead one is out of the pool
            if (members >= cap) continue;

            var weights = new List<(RecordLink Entry, float Weight)>();
            float total = 0;
            foreach (var entry in site.Town.RoamingSquads)
            {
                if (entry.V0 <= 0 || !SquadFactory.IsTemplate(data.Db.Find(entry.Id))) continue;
                int existing = 0;
                foreach (var p in world.Platoons.All) if (p.Origin == site.Index && !p.Pinned && p.Campaign is null && !p.Dead && p.TemplateId == entry.Id) existing++;
                float w = MathF.Max(0, entry.V0 - existing);
                if (w <= 0) continue;
                weights.Add((entry, w));
                total += w;
            }
            if (total <= 0) continue;
            int id = world.Platoons.NextId();
            ulong key = Rng.Mix(SeedSalts.RoamingPool ^ (ulong)id << 8 ^ (ulong)site.Index << 40);
            float pick = Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Spawn, SeedSalts.RoamingPick)) * total;
            var chosen = weights[^1].Entry;
            foreach (var (entry, w) in weights)
            {
                if ((pick -= w) < 0) { chosen = entry; break; }
            }
            var template = SquadTemplate.From(data.Db.Find(chosen.Id)!);
            var plan = SquadFactory.Plan(data.Db, template, settings.SquadSizeMultiplier, world.Seed, key, States);
            if (plan.TotalMembers == 0 || members + plan.TotalMembers > cap) continue;
            var position = Place(site, world.Seed, key, world.Walkability);
            // Not on top of the player (6.3: within 35.4 of the view point).
            if (!playersListed)
            {
                playersListed = true;
                playerPositions.Clear();
                for (int i = 0; i < table.Previous.Length; i++)
                    if (table.Previous[i].Alive && table.Cold(i) is { IsPlayer: true }) playerPositions.Add(new Vector2(table.Previous[i].Position.X, table.Previous[i].Position.Z));
            }
            bool crowded = false;
            foreach (var at in playerPositions)
                if (Vector2.Distance(at, position) < 36) { crowded = true; break; }
            if (crowded) continue;
            int squadFaction = template.Faction is { } tf && data.FactionIndex(tf) is var ti and >= 0 ? ti : factionIndex;
            var (package, job) = Ai.PlatoonJobs.For(data.Ai, template);
            var platoon = new Platoon
            {
                Id = id, Origin = site.Index, TemplateId = chosen.Id, Faction = squadFaction, Key = key, Size = plan.TotalMembers,
                Position = position, Package = package?.Id, Job = job,
            };
            world.Platoons.Add(platoon);
            ChooseNext(world, platoon);
            Activate(world, platoon);
        }
    }
}
