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
/// <see cref="PopulationSettings.RoamUnloadGraceSeconds"/> loses them and goes on as a stand-in at 25 units per second (90 beyond 25000, the original's
/// numbers; units not verified); it gets its characters back, made again from its key, when a zone near it is active.</item>
/// </list>
/// </summary>
public sealed partial class PopulationSystem
{
    readonly ConcurrentQueue<BuiltRoamer> builtRoamers = new();

    sealed record BuiltRoamer(int Platoon, BuiltSquad Squad);

    static bool IsSettlement(TownType t) => t is TownType.Town or TownType.Village or TownType.Outpost or TownType.Military;

    Vector2 SiteCentre(int site) => new(data.Sites[site].Position.X, data.Sites[site].Position.Z);

    // ---- the look at the roaming squads, in the slow phase ----

    void StepRoaming(World world)
    {
        float dt = settings.CheckEveryTicks * world.TickSeconds;
        foreach (var p in world.Platoons.All.ToList())
        {
            switch (p.State)
            {
                case PlatoonState.Unloaded:
                    StandIn(world, p, dt);
                    if (p.State == PlatoonState.Unloaded && active.Contains(WorldLayout.ZoneOf(p.Position.X, p.Position.Y))) Activate(world, p);
                    break;
                case PlatoonState.Loaded:
                    StepLoaded(world, p, dt);
                    break;
            }
        }
        if (world.Tick % settings.RoamCheckTicks == 0) FillPools(world);
    }

    /// <summary>The unloaded half: moves a stand-in for the squad towards its target, with no physics (the original's <c>UnloadedPlatoon</c>).</summary>
    void StandIn(World world, Platoon p, float dt)
    {
        if (p.WaitLeft > 0)
        {
            p.WaitLeft -= dt;
            if (p.WaitLeft <= 0) ChooseNext(world, p);
            return;
        }
        if (p.Target < 0)
        {
            ChooseNext(world, p);
            return;
        }
        var to = SiteCentre(p.Target) - p.Position;
        float d = to.Length();
        if (d <= settings.RoamArrivalRadius)
        {
            Arrive(world, p);
            return;
        }
        float speed = d > settings.StandInFarDistance ? settings.StandInFarSpeed : settings.StandInSpeed;
        p.Position += to / d * MathF.Min(speed * dt, d);
    }

    void Arrive(World world, Platoon p)
    {
        ulong roll = Rng.Hash(world.Seed, (ulong)p.Id, RngPurpose.Wander, (ulong)world.Tick);
        p.WaitLeft = settings.RoamWaitMin + Rng.Float(roll) * (settings.RoamWaitMax - settings.RoamWaitMin);
        p.GoingHome = p.Target != p.Origin;   // having reached another town, the way home is next
    }

    /// <summary>Picks where a platoon goes next: home after a visit, else one of the 4 nearest settlements that are not hostile to it.</summary>
    void ChooseNext(World world, Platoon p)
    {
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
        ulong roll = Rng.Hash(world.Seed, (ulong)p.Id, RngPurpose.Wander, (ulong)world.Tick ^ 0x7A6E7UL);
        p.Target = candidates[(int)(Rng.Float(roll) * n) % n].Site;
        p.GoingHome = false;
    }

    // ---- loaded roaming squads ----

    void StepLoaded(World world, Platoon p, float dt)
    {
        var table = world.Characters;
        var squad = world.Squads.Find(p.SquadId);
        if (squad is null || squad.Members.Count == 0 || squad.Leader.IsNone)
        {
            // Wiped out (or removed): the pool is free to make another.
            if (squad is not null) world.Squads.Remove(squad.Id);
            world.Platoons.Remove(p.Id);
            return;
        }
        var lead = table.Previous[squad.Leader.Slot].Position;
        p.Position = new Vector2(lead.X, lead.Z);

        // Away from every active zone for long enough: only the stand-in goes on.
        if (active.Contains(WorldLayout.ZoneOf(p.Position.X, p.Position.Y))) p.UnloadAtTick = -1;
        else if (p.UnloadAtTick < 0) p.UnloadAtTick = world.Tick + (long)Math.Ceiling(settings.RoamUnloadGraceSeconds / world.TickSeconds);
        else if (world.Tick >= p.UnloadAtTick)
        {
            foreach (var m in squad.Members) table.Remove(m);
            world.Squads.Remove(squad.Id);
            p.State = PlatoonState.Unloaded;
            p.SquadId = -1;
            p.UnloadAtTick = -1;
            return;
        }

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
        if (p.Target < 0)
        {
            ChooseNext(world, p);
            OrderLeader(world, p, squad);
            return;
        }
        var goal = SiteCentre(p.Target);
        if (Vector2.Distance(p.Position, goal) <= settings.RoamArrivalRadius)
        {
            Arrive(world, p);
            // At the town the squad mills about in it.
            if (table.TryResolveNext(squad.Leader, out int slot))
            {
                MovementSystem.Stop(table, slot);
                table.Next[slot].Task = (byte)CharacterTask.Wander;
                table.Next[slot].Mode = SpeedMode.Walk;
                table.Next[slot].TaskTime = 0.5f;
            }
            squad.HomeCentre = goal;
            squad.HomeRadius = Math.Max(data.Sites[p.Target].Town.SizeRadius * data.Sites[p.Target].Town.TownRadiusMult * 0.5f, 40);
            return;
        }
        // On the way: the leader keeps a go-to; ask again when it has none (arrived short, blocked, or just loaded).
        if (table.TryResolveNext(squad.Leader, out int leaderSlot))
        {
            ref var n = ref table.Next[leaderSlot];
            bool busy = n.Task == (byte)CharacterTask.GoTo && (n.Flags & (ushort)(MoveFlags.NeedPath | MoveFlags.Pending | MoveFlags.HasPath)) != 0;
            p.OrderCooldown -= dt;
            if (!busy && p.OrderCooldown <= 0)
            {
                OrderLeader(world, p, squad);
                p.OrderCooldown = 4;
            }
        }
    }

    void OrderLeader(World world, Platoon p, Squad squad)
    {
        if (p.Target < 0 || !world.Characters.TryResolveNext(squad.Leader, out int slot)) return;
        MovementSystem.GoTo(world.Characters, slot, SiteCentre(p.Target), SpeedMode.Run);
    }

    // ---- unloaded to loaded ----

    void Activate(World world, Platoon p)
    {
        p.State = PlatoonState.Activating;
        ulong seed = world.Seed;
        var walk = world.Walkability;
        var position = p.Position;
        int id = p.Id;
        if (work is null) FinishRoamer(world, new BuiltRoamer(id, BuildRoamer(p.TemplateId, p.Faction, p.Key, seed, position)));
        else
        {
            string template = p.TemplateId;
            int faction = p.Faction;
            ulong key = p.Key;
            work.Add(() => builtRoamers.Enqueue(new BuiltRoamer(id, BuildRoamer(template, faction, key, seed, position))));
        }
        _ = walk;
    }

    BuiltSquad BuildRoamer(string templateId, int faction, ulong key, ulong seed, Vector2 position)
    {
        var template = SquadTemplate.From(data.Db.Find(templateId)!);
        var townFaction = faction >= 0 ? data.Factions[faction] : null;
        return BuildOne(template, faction, townFaction, key, seed, position)!;
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
        var site = data.Sites[p.Origin];
        var squad = SpawnSquad(world, b.Squad, site, site.Town.SizeRadius * site.Town.TownRadiusMult, platoonId: p.Id);
        p.State = PlatoonState.Loaded;
        p.SquadId = squad.Id;
        p.UnloadAtTick = -1;
        if (p.Target >= 0 && p.WaitLeft <= 0) OrderLeader(world, p, squad);
    }

    // ---- the pools ----

    /// <summary>Every loaded town with roaming squads below its cap makes one (6.4, the town branch of 6.2's choice).</summary>
    void FillPools(World world)
    {
        var table = world.Characters;
        foreach (var site in data.Sites)
        {
            if (states[site.Index].Status != SiteStatus.Loaded || site.Town.RoamingSquads.Count == 0) continue;
            int factionIndex = data.SiteFactions[site.Index];
            if (factionIndex < 0) continue;
            int cap = (int)MathF.Floor(settings.RoamingShare * data.Factions[factionIndex].RoamingPopulation * settings.PopulationMultiplier);
            int members = 0;
            foreach (var p in world.Platoons.All) if (p.Origin == site.Index) members += p.Size;
            if (members >= cap) continue;

            var weights = new List<(RecordLink Entry, float Weight)>();
            float total = 0;
            foreach (var entry in site.Town.RoamingSquads)
            {
                if (entry.V0 <= 0 || data.Db.Find(entry.Id) is not { Type: FcsRecordType.SQUAD_TEMPLATE or FcsRecordType.UNIQUE_SQUAD_TEMPLATE }) continue;
                int existing = world.Platoons.All.Count(p => p.Origin == site.Index && p.TemplateId == entry.Id);
                float w = MathF.Max(0, entry.V0 - existing);
                if (w <= 0) continue;
                weights.Add((entry, w));
                total += w;
            }
            if (total <= 0) continue;
            int id = world.Platoons.NextId();
            ulong key = Rng.Mix(0x20A4UL ^ (ulong)id << 8 ^ (ulong)site.Index << 40);
            float pick = Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Spawn, 5)) * total;
            var chosen = weights[^1].Entry;
            foreach (var (entry, w) in weights)
            {
                if ((pick -= w) < 0) { chosen = entry; break; }
            }
            var template = SquadTemplate.From(data.Db.Find(chosen.Id)!);
            var plan = SquadFactory.Plan(data.Db, template, settings.SquadSizeMultiplier, world.Seed, key, data.States);
            if (plan.TotalMembers == 0 || members + plan.TotalMembers > cap) continue;
            var position = Place(site, world.Seed, key, world.Walkability);
            // Not on top of the player (6.3: within 35.4 of the view point).
            bool crowded = false;
            for (int i = 0; i < table.Previous.Length && !crowded; i++)
                if (table.Previous[i].Alive && table.Cold(i) is { IsPlayer: true } && Vector2.Distance(new Vector2(table.Previous[i].Position.X, table.Previous[i].Position.Z), position) < 36) crowded = true;
            if (crowded) continue;
            int squadFaction = template.Faction is { } tf && data.Factions.ToList().FindIndex(f => f.Id == tf) is var ti and >= 0 ? ti : factionIndex;
            var platoon = new Platoon
            {
                Id = id, Origin = site.Index, TemplateId = chosen.Id, Faction = squadFaction, Key = key, Size = plan.TotalMembers,
                Position = position,
            };
            world.Platoons.Add(platoon);
            ChooseNext(world, platoon);
            Activate(world, platoon);
        }
    }
}
