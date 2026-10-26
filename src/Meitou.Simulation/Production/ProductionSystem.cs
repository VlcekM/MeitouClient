using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Construction;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Items;

namespace Meitou.Simulation.Production;

/// <summary>The numbers of <see cref="ProductionSystem"/>; the engine choices say so (docs/simulation.md "Production").</summary>
public sealed class ProductionSettings
{
    /// <summary>The <i>Production speed</i> option (multiplies production work); its default is Unknown, 1 is neutral.</summary>
    public float ProductionOption { get; init; } = 1;
    /// <summary>The <i>Research speed</i> option (divides research hours); its default is Unknown, 1 is neutral.</summary>
    public float ResearchOption { get; init; } = 1;
    /// <summary>A turret's gunner whose path has ended counts as at its spot within this much more (engine choice). Operators work within the machine's <c>use range</c> (<see cref="SiteReach"/>).</summary>
    public float SettleReach { get; init; } = 150;
    /// <summary>The footprint radius of a BUILDING record (the host knows it); null: 0.</summary>
    public Func<string, float>? Footprint { get; init; }
    /// <summary>The X/Z gap from a point to a site's footprint hull, 0 inside; null, or a null answer: the gap to its position (<see cref="SiteReach"/>).</summary>
    public Func<BuildSite, Vector2, float?>? EdgeGap { get; init; }
    /// <summary>A character gives or takes items from within this far (X/Z) of a building's footprint, else "Out of trading range." (the original: the building's bounds radius + 40 from its position for an item out of it, 60 for one out of a character's inventory, docs/game/economy.md "Trading range"; engine choice: 40 from the footprint both ways).</summary>
    public float TransferReach { get; init; } = 40;
    /// <summary>Operators empty a full output into storages and fill empty inputs from them (docs/simulation.md "Production"; engine option: the original's wording "put any excess materials ... into storage" is the Ditch items squad AI option's, <see cref="SquadAi.Ditch"/>).</summary>
    public bool AutoHaul { get; init; } = true;
    /// <summary>Farms, automatic machines and generators work in unloaded zones too (Meitou default); false: they stop there and farms catch up when their zone loads again, as the game does (docs/game/buildings-production.md "Unloaded zones").</summary>
    public bool UnloadedProduction { get; init; } = true;
    /// <summary>How far (X/Z) from its machine an operator hauls (engine choice; the original's limit is Unknown).</summary>
    public float HaulRange { get; init; } = 3000;
}

/// <summary>
/// Production, farming and research of the player's complete buildings (docs/simulation.md "Production"; the original's rules in docs/game/buildings-production.md "Production", "Farming", "Research").
/// <list type="bullet">
/// <item><b>Machines</b> (<see cref="World.Machines"/>): one per complete player building that works or stores, made when the site completes, dropped (its items on the ground) with it.</item>
/// <item><b>Operators</b>: <see cref="OperateOrder"/> sets <see cref="CharacterTask.Operate"/>; <c>Schedule</c> walks the character there, <c>SlowWorld</c> applies its work each tick with the skill
/// factor <c>lerp(stat/100, 0.5, 1.4)</c> and the operating XP.</item>
/// <item><b>Farms</b> grow every tick, whether anyone is there; operators harvest ripe crops and clear dead ones. <b>Research</b> benches feed the first queued research with the 0.6 falloff.</item>
/// </list>
/// Placed after construction and before combat; everything here is serial and reads only the world's state and the command order.
/// </summary>
public sealed partial class ProductionSystem : ITickSystem
{
    /// <summary>The research falloff per earlier contribution in the same tick (<b>Observed</b>, <c>FUN_140836b70</c>).</summary>
    public const float ResearchFalloff = 0.6f;

    readonly GameDatabase db;
    readonly GameConstants constants;
    readonly ItemFactory items;
    readonly XpService xp;
    readonly ResourceField resources;
    readonly IResourceNodes? nodes;
    readonly Dictionary<string, MachineData?> machineData = new(StringComparer.Ordinal);
    readonly Dictionary<string, ResearchData?> researchData = new(StringComparer.Ordinal);
    List<ResearchData>? allResearch;
    ResearchSnapshot? publishedResearch;
    long researchVersion = -1, machinesVersion = -1;
    // Some character may still hold a machine that is gone.
    bool sweep;

    public ProductionSystem(GameDatabase db, GameConstants constants, BodyOptions options, ItemFactory items, BiomeMap? biomes, ProductionSettings? settings = null, IResourceNodes? nodes = null)
    {
        this.db = db;
        this.constants = constants;
        this.items = items;
        Settings = settings ?? new ProductionSettings();
        xp = new XpService(constants, options);
        resources = new ResourceField(db, biomes, nodes);
        this.nodes = nodes;
        UnloadedProduction = Settings.UnloadedProduction;
    }

    public ProductionSystem(PopulationData data, ProductionSettings? settings = null)
        : this(data.Db, data.Bodies.Constants, data.BodyOptions, data.Items, data.Biomes, settings, data.ResourceNodes)
    {
    }

    public ProductionSettings Settings { get; }
    /// <summary>See <see cref="ProductionSettings.UnloadedProduction"/>; the host sets it live.</summary>
    public bool UnloadedProduction { get; set; }
    public ItemFactory Items => items;
    /// <summary>The construction system the engineer job (125) builds through; null: the job never runs.</summary>
    public ConstructionSystem? Construction { get; init; }

    /// <summary>What a BUILDING does once built (null for one that neither works nor stores); read once.</summary>
    public MachineData? Data(string record)
    {
        if (!machineData.TryGetValue(record, out var data))
            machineData[record] = data = db.Find(record) is { Type: FcsRecordType.BUILDING } r ? MachineData.From(db, r) : null;
        return data;
    }

    ResearchData? Research(string id)
    {
        if (!researchData.TryGetValue(id, out var data))
            researchData[id] = data = db.Find(id) is { Type: FcsRecordType.RESEARCH } r ? ResearchData.From(r) : null;
        return data;
    }

    // ------------------------------------------------------------------ machines

    /// <summary>Makes the machine of a complete site (empty, a farm freshly planted) unless it has one; <paramref name="world"/> seeds the planting.</summary>
    public Machine? Ensure(World world, BuildSite site)
    {
        if (world.Machines.Find(site.Id) is { } existing) return existing;
        if (Create(world, site) is not { } m) return null;
        world.Machines.Add(m);
        return m;
    }

    Machine? Create(World world, BuildSite site)
    {
        if (Data(site.Record) is not { } data) return null;
        var m = new Machine
        {
            Site = site.Id,
            Data = data,
            Position = site.Position,
            Inputs = [.. data.Inputs.Select(i => new InputSlot(i))],
            Output = data.Output is { } o ? new OutputSlot(o) : null,
            Storage = data.HasInventory ? new Inventory(data.StorageWidth, data.StorageHeight) { StackBonusMult = data.StackBonusMult, Limited = data.StorageLimit.Count > 0 }
                : data.IsFurnace ? FurnaceGrid() : null,
            Farm = data.Farm is { } fd ? new FarmState(Meitou.Data.Gameplay.Construction.FarmLayout.Count(fd)) : null,
            Craft = data.IsCrafting ? new CraftState() : null,
        };
        var p = site.Position;
        if (data.Farm is { } farm)
        {
            float fertility = farm.Indoor ? 1 : resources.Value(MachineData.GroundResource, p.X, p.Y, p.Z);
            m.Yield = farm.FertilityFactor(fertility);
            m.CropMatch = resources.CropMatch(farm, p.X, p.Z);
            m.WaterRate = farm.ConsumptionRate / MachineData.DaySeconds * (farm.Indoor ? 1 : resources.WaterUsage(p.X, p.Z));
            Replant(world, m);
        }
        // The yield (FUN_14029c670, Verified): a rock its own value, at least 0.1; any other building what the ground or the nearest rock gives, and 0 becomes 0.5 for one not the
        // player's (FUN_140546dd0), so only the player's own mines can stand on nothing.
        else if (data.Resource != 0)
        {
            m.Yield = IsNatural(site.Id) ? resources.NodeYield(data.Resource, p) : resources.Yield(data.Resource, p);
            if (!IsNatural(site.Id) && m.Yield == 0 && site.Owner != world.Player.Faction) m.Yield = 0.5f;
        }
        return m;
    }

    /// <summary>Machines follow the sites: a complete player site gets one, a gone site loses its own (items dropped where it stood).</summary>
    void Sync(World world)
    {
        var reg = world.Buildings;
        foreach (var site in reg.All)
            if (site.Complete && site.Owner == world.Player.Faction && site.WallParent is null && world.Machines.Find(site.Id) is null && Data(site.Record) is not null) Ensure(world, site);
        if (world.Machines.Count == 0) return;
        List<(Machine, BuildSite)>? swapped = null;
        foreach (var m in world.Machines.All)
            if (Site(world, m.Site) is { } s && s.Record != m.Data.Id && !IsNatural(s.Id)) (swapped ??= []).Add((m, s));
        if (swapped is not null)
            foreach (var (m, s) in swapped) Rebuild(world, m, s);
        List<Machine>? gone = null;
        foreach (var m in world.Machines.All)
            if (Site(world, m.Site) is null) (gone ??= []).Add(m);
        if (gone is null) return;
        foreach (var m in gone)
        {
            DropAll(world, m);
            world.Machines.Remove(m.Site);
            sweep = true;
        }
    }

    /// <summary>
    /// The machine of a building upgraded or downgraded in place (docs/game/buildings-production.md "Upgrades"; engine reading: the original keeps the one object and its inventory) is
    /// made anew for its new record and keeps what the old one held: input and output stock of the same items, the storage's items (what no longer fits drops at the building), a
    /// farm's growth state (plants by index; the new layout's extra plants as replanted), a bench's queue and finished items, and the power settings. The rest drops on the ground.
    /// </summary>
    void Rebuild(World world, Machine old, BuildSite site)
    {
        if (Create(world, site) is not { } m)
        {
            DropAll(world, old);
            world.Machines.Remove(old.Site);
            sweep = true;
            return;
        }
        var at = old.Position;
        void Drop(ItemInstance? item) { if (item is not null) world.GroundItems.Drop(item, at, 0); }
        foreach (var i in old.Inputs)
        {
            if (i.Count <= 0) continue;
            if (Array.Find(m.Inputs, n => n.Item == i.Item) is { } into) { into.Count += i.Count; into.Level = Math.Max(into.Level, i.Level); }
            else Drop(items.Create(i.Item, i.Count));
        }
        if (old.Output is { Count: > 0 } o)
        {
            if (m.Output is { } no && no.Recipe.Item == o.Recipe.Item) { no.Count = o.Count; no.Progress = o.Progress; }
            else Drop(items.Create(o.Recipe.Item, o.Count));
        }
        if (old.Storage is { } store)
            foreach (var item in store.Items.ToList())
                if (m.Storage is not { } into || !into.Add(item)) Drop(item);
        if (old.Farm is { } of && m.Farm is { } nf)
        {
            for (int i = 0; i < Math.Min(of.Plants.Length, nf.Plants.Length); i++) nf.Plants[i] = of.Plants[i];
            (nf.Growth, nf.Death, nf.Start, nf.Harvested, nf.HarvestProgress, nf.ClearProgress, nf.Status, nf.Plantings, nf.Rolls)
                = (of.Growth, of.Death, of.Start, Math.Min(of.Harvested, nf.Plants.Length), of.HarvestProgress, of.ClearProgress, of.Status, of.Plantings, of.Rolls);
        }
        if (old.Craft is { } oc)
        {
            if (m.Craft is { } nc)
            {
                nc.Queue.AddRange(oc.Queue);
                nc.Done.AddRange(oc.Done);
                (nc.Repeat, nc.Bound, nc.Rolls) = (oc.Repeat, oc.Bound, oc.Rolls);
            }
            else foreach (var item in oc.Done) Drop(item);
        }
        (m.PowerOn, m.GetsBattery, m.Charge, m.Broken) = (old.PowerOn, old.GetsBattery, old.Charge, old.Broken);
        world.Machines.Replace(m);
        sweep = true;
    }

    /// <summary>Puts what a removed machine held on the ground at its last position (engine choice: the original's dismantle leaves items too, the details are Unknown).</summary>
    void DropAll(World world, Machine m)
    {
        var at = m.Position;
        void Drop(ItemInstance? item) { if (item is not null) world.GroundItems.Drop(item, at, 0); }
        foreach (var i in m.Inputs) if (i.Count > 0) Drop(items.Create(i.Item, i.Count));
        if (m.Output is { Count: > 0 } o) Drop(items.Create(o.Recipe.Item, o.Count));
        if (m.Storage is { } s) foreach (var item in s.Items.ToList()) Drop(item);
        if (m.Craft is { } c) foreach (var item in c.Done) Drop(item);
    }

    // ------------------------------------------------------------------ orders

    static IReadOnlyList<CharacterId> Who(World world, IReadOnlyList<CharacterId> ids) => ids.Count > 0 ? ids : world.Player.Selection.ToArray();

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        foreach (var command in commands)
        {
            switch (command)
            {
                case OperateOrder order:
                {
                    var site = Site(world, order.Site);
                    if (site is not { Complete: true } || (Ensure(world, site) is not { } machine) || !(machine.Data.Operable || machine.Data.IsTraining)) break;
                    var table = world.Characters;
                    foreach (var id in Who(world, order.Characters))
                    {
                        if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { IsPlayer: true } cold || !Able(cold)) continue;
                        if (cold.OperateSite == site.Id) continue;
                        if (OperatorsOf(world, site.Id) >= machine.Data.MaxOperators)
                        {
                            world.Post($"{site.Name} is already in use.");
                            break;
                        }
                        Begin(world, slot, site, machine);
                    }
                    break;
                }
                case UseTurretOrder turret: UseTurret(world, turret); break;
                case ProspectOrder prospect: ProspectBegin(world, prospect); break;
                case AddJob add: AddJobs(world, add); break;
                case AddOrderJob add: AddOrderJobs(world, add); break;
                case DisposeCorpseOrder dispose: DisposeCarried(world, dispose); break;
                case RemoveJob remove: RemoveJobAt(world, remove); break;
                case MoveJob move: MoveJobTo(world, move); break;
                case PutInMachine put: Put(world, put); break;
                case TakeFromMachine take: Take(world, take); break;
                case QueueResearch queue: Queue(world, queue.Research); break;
                case CancelResearch cancel: world.Research.Dequeue(cancel.Research); break;
                case QueueCraft craft: QueueCraft(world, craft); break;
                case CancelCraft cancel: CancelCraft(world, cancel); break;
                case RepeatCrafts repeat: if (world.Machines.Find(repeat.Site)?.Craft is { } state) { state.Repeat = repeat.Repeat; world.Machines.Touch(); } break;
                case SetMachinePower set when world.Machines.Find(set.Site) is { } powered:
                    if (set.PowerOn is { } on) powered.PowerOn = on;
                    if (set.GetsBattery is { } battery && powered.Data.IsConsumer) powered.GetsBattery = battery;
                    world.Machines.Touch();
                    break;
                case IncinerateFurnace burn when world.Machines.Find(burn.Site) is { Data.IsFurnace: true } furnace && Site(world, burn.Site) is { Complete: true } lit && lit.Owner == world.Player.Faction:
                    Incinerate(furnace);
                    world.Machines.Touch();
                    break;
            }
        }
    }

    /// <summary>
    /// The nearest able one of the order's characters mans the turret (the original's <c>Task_UseTurret</c>): it operates it, walking to its gunner spot (engine choice: one gunner, the
    /// original's pick among several is Unknown); someone else at it already: "is already in use".
    /// </summary>
    void UseTurret(World world, UseTurretOrder order)
    {
        if (Site(world, order.Site) is not { Complete: true } site || TurretPostOf(site) is not { } post || Ensure(world, site) is not { } m) return;
        var spot = Ai.AiSystem.TurretSpot(world, post);
        var table = world.Characters;
        int best = -1;
        float bestGap = float.MaxValue;
        foreach (var id in Who(world, order.Characters))
            if (table.TryResolveNext(id, out int slot) && table.Cold(slot) is { IsPlayer: true } cold && Able(cold) && Gap(table.Next[slot].Position, spot) is var gap && gap < bestGap)
                (best, bestGap) = (slot, gap);
        if (best < 0 || table.Cold(best)!.OperateSite == site.Id) return;
        if (OperatorsOf(world, site.Id) >= m.Data.MaxOperators)
        {
            world.Post($"{site.Name} is already in use.");
            return;
        }
        Begin(world, best, site, m);
    }

    /// <summary>A player turret's post (its functionality's <c>use range</c> and <c>gun turret</c>), null for a building that is no turret.</summary>
    Meitou.Data.World.TurretPost? TurretPostOf(BuildSite site)
    {
        if (db.Find(site.Record) is not { } record || (record.GetReferences("functionality") is [var link, ..] ? db.Find(link.TargetStringId) : null) is not { } fn
            || fn.GetInt("function") != Meitou.Data.World.ObjectRanges.TurretFunction) return null;
        var forward = Vector3.Transform(Vector3.UnitZ, site.Rotation);
        return new Meitou.Data.World.TurretPost(site.Position, MathF.Atan2(forward.X, forward.Z), fn.GetFloat("use range", 10),
            record.GetReferences("gun turret").Select(r => r.TargetStringId).FirstOrDefault() ?? "", null);
    }

    /// <summary>
    /// Where an operator walks to: a turret's gunner spot (<see cref="Ai.AiSystem.TurretSpot"/>), else the floor point at or next to the building's place (furniture cuts a hole in
    /// the floor's navmesh; <see cref="MovementSystem.GoToFloor"/>).
    /// </summary>
    Vector2 GoalOf(World world, BuildSite site, Machine m)
    {
        if (m.Data.Function == Meitou.Data.World.ObjectRanges.TurretFunction && TurretPostOf(site) is { } post)
        {
            var spot = Ai.AiSystem.TurretSpot(world, post);
            return turretSpots[site.Id] = new Vector2(spot.X, spot.Z);
        }
        var at = world.Walkability.FloorNear(site.Position.X, site.Position.Z, site.Position.Y, MovementSystem.FloorSlack) ?? site.Position;
        return new Vector2(at.X, at.Z);
    }

    /// <summary>Sends an operator towards <paramref name="goal"/> (<see cref="GoalOf"/>) on the site's own floor (not a turret's, whose spot may stand above it).</summary>
    void WalkToSite(ref CharacterHot n, CharacterCold cold, BuildSite site, Vector2 goal)
    {
        n.Goal = goal;
        n.Flags |= (ushort)MoveFlags.NeedPath;
        (cold.GoalHeight, cold.GoalHeightAt) = turretSpots.ContainsKey(site.Id) ? (float.NaN, default) : (site.Position.Y, n.Goal);
    }

    // The gunner spots of the player's turrets, as their walks found them (derived from the site and the ground; not saved, not hashed).
    readonly Dictionary<string, Vector2> turretSpots = [];

    /// <summary>A character that can work: awake, not fighting, not carrying or carried, not caged.</summary>
    static bool Able(CharacterCold cold) =>
        cold.Medical is not { Incapacitated: true } && !cold.InCombat && cold.CarriedBy.IsNone && cold.Carrying.IsNone && !cold.IsImprisoned;

    static int OperatorsOf(World world, string site)
    {
        var table = world.Characters;
        int n = 0;
        for (int i = 0; i < table.HighWater; i++)
            if (table.Next[i].Alive && table.Next[i].Task == (byte)CharacterTask.Operate && table.Cold(i)?.OperateSite == site) n++;
        return n;
    }

    static float Gap(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X, dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    bool AtSite(in CharacterHot n, BuildSite site, Machine m)
    {
        // A turret's gunner stands at its spot (once its walk has found it).
        if (turretSpots.TryGetValue(site.Id, out var spot))
        {
            float off = Vector2.Distance(new Vector2(n.Position.X, n.Position.Z), spot);
            return off <= Ai.AiSystem.TurretSlack || ((n.Flags & (ushort)MoveFlags.AnyPath) == 0 && off <= Ai.AiSystem.TurretSlack + Settings.SettleReach);
        }
        // The machine's use range (UseableStuff::getReachRange).
        return SiteReach.Within(n, GapTo(site, n.Position), m.Data.UseRange);
    }

    /// <summary>
    /// The X/Z gap from <paramref name="from"/> to a site (<see cref="SiteReach.Gap"/>). A rock's BUILDING footprint is not scaled like the rock, so a rock is measured to its mesh's
    /// bounds box as placed (engine choice for the original's 1.2 x bounds box of a foliage building; a circle of its longest half-extent left miners standing off a long rock's
    /// side), else to its position.
    /// </summary>
    float GapTo(BuildSite site, Vector3 from)
    {
        if (!IsNatural(site.Id)) return SiteReach.Gap(Settings.EdgeGap, site, from);
        return naturalBounds.TryGetValue(site.Id, out var rock) ? rock.Gap(from) : SiteReach.Gap(null, site, from);
    }

    /// <summary>
    /// The yaw from <paramref name="n"/> to a site: a rock's bounds centre (engine choice, as <see cref="GapTo"/>: a rock's origin can lie off its pile, so facing it turned miners
    /// away), else its position.
    /// </summary>
    float FacingTo(in CharacterHot n, BuildSite site)
    {
        var at = naturalBounds.TryGetValue(site.Id, out var rock) ? rock.Flat : new Vector2(site.Position.X, site.Position.Z);
        return MathF.Atan2(at.X - n.Position.X, at.Y - n.Position.Z);
    }

    /// <summary>The clip a turret's gunner holds.</summary>
    public const string TurretClip = "combatstance";

    void Begin(World world, int slot, BuildSite site, Machine m)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        ref var n = ref table.Next[slot];
        MovementSystem.Stop(table, slot);
        n.Task = (byte)CharacterTask.Operate;
        n.Mode = SpeedMode.Free;
        n.TaskTime = 0;
        cold.OperateSite = site.Id;
        // A gunner holds "combatstance" (Verified, Task_UseTurret FUN_1403474f0), not its functionality's clip.
        cold.OperateClip = m.Data.Function == Meitou.Data.World.ObjectRanges.TurretFunction ? TurretClip : m.Data.Animation;
        cold.OperateTool = m.Data.Tool;
        cold.OperateHungerRate = m.Data.HungerRate;
        cold.HaulSite = null;
        cold.HaulFetch = null;
        cold.Operating = false;
        cold.JobIndex = -1;
        cold.Implicit = null;
        cold.HaulTo = null;
        var goal = GoalOf(world, site, m);
        if (!AtSite(n, site, m)) WalkToSite(ref n, cold, site, goal);
        sweep = true;
    }

    static void End(CharacterTable table, int slot)
    {
        var cold = table.Cold(slot)!;
        ref var n = ref table.Next[slot];
        bool followJob = n.Task == (byte)CharacterTask.Follow && cold.JobIndex >= 0 && cold.JobIndex < cold.Jobs.Count
            && cold.Jobs[cold.JobIndex].Type is Meitou.Data.Gameplay.Ai.TaskType.BODYGUARD or Meitou.Data.Gameplay.Ai.TaskType.STAY_CLOSE_TO_TARGET;
        if (followJob) cold.FollowOrder = CharacterId.None;   // the job's follow ends with it; the character follows its squad's leader again
        if (followJob || n.Task is (byte)CharacterTask.Operate or (byte)CharacterTask.Haul or (byte)CharacterTask.Sleep)
        {
            MovementSystem.Stop(table, slot);
            // A job's task goes back to the following or wandering it took the character from.
            n.Task = (byte)((cold.JobIndex >= 0 || cold.Implicit is not null) && cold.ResumeTask is CharacterTask.Wander or CharacterTask.Follow ? cold.ResumeTask : CharacterTask.Idle);
            n.TaskTime = 0;
        }
        cold.OperateSite = null;
        cold.HaulSite = null;
        cold.HaulFetch = null;
        cold.Operating = false;
        cold.JobIndex = -1;
        cold.Implicit = null;
        cold.HaulTo = null;
    }

    public void Schedule(World world)
    {
        // Job lists also run for the engineer job while a site is unfinished or damaged, and with no machine they can still send the injured to bed
        // (the "Sleep when injured" option).
        if (world.Tick % 30 == 0) ScanFollowJobs(world);
        if (world.Machines.Count == 0 && !sweep && !followJobs && (Construction is null || !world.Buildings.AnyNeedsWork)
            && !(Beds is not null && (world.Player.SquadAi & SquadAi.Sleep) != 0 && world.Fixtures.Count > 0)) return;
        PickJobs(world);
        var table = world.Characters;
        float dt = world.TickSeconds;
        for (int i = 0; i < table.HighWater; i++)
        {
            ref var n = ref table.Next[i];
            if (!n.Alive) continue;
            var cold = table.Cold(i)!;
            if (n.Task == (byte)CharacterTask.Haul)
            {
                if (cold.HaulSite is { } to && Site(world, to) is { Complete: true } leg && world.Machines.Find(to) is { } legMachine && Able(cold)) Approach(world, ref n, cold, leg, legMachine, dt);
                else End(table, i);
                continue;
            }
            if (n.Task == (byte)CharacterTask.Sleep) continue;   // the bed of the "Sleep when injured" option, in OperateSite (BedSystem walks it)
            if (cold.OperateSite is not { } id)
            {
                // A hauler an order took off its job.
                if (cold.HaulSite is not null) { cold.HaulSite = null; cold.HaulFetch = null; }
                continue;
            }
            if (n.Task != (byte)CharacterTask.Operate)
            {
                cold.OperateSite = null;
                cold.HaulSite = null;
                cold.HaulFetch = null;
                cold.Operating = false;
            if (cold.StaggeredUntil > world.Tick) continue;   // a stagger interrupts the work
                continue;
            }
            var site = Site(world, id);
            var m = world.Machines.Find(id);
            if (site is not { Complete: true } || m is null || !Able(cold))
            {
                End(table, i);
                continue;
            }
            // A hauling operator walks to its storage instead.
            if (Hauling(world, cold) is { } haul) (site, m) = haul;
            Approach(world, ref n, cold, site, m, dt);
        }
        if (world.Machines.Count == 0) sweep = false;
    }

    /// <summary>Walks a character to a building: at it, its path is dropped; else a path is asked for, again every <see cref="PursuitSystem.RepathSeconds"/> while it has none.</summary>
    void Approach(World world, ref CharacterHot n, CharacterCold cold, BuildSite site, Machine m, float dt)
    {
        const ushort Path = (ushort)MoveFlags.AnyPath;
        if (m.Data.Function == Meitou.Data.World.ObjectRanges.TurretFunction && !turretSpots.ContainsKey(site.Id)) GoalOf(world, site, m);
        if (AtSite(n, site, m))
        {
            if ((n.Flags & Path) != 0)
            {
                n.Flags = (ushort)(n.Flags & ~Path);
                n.PathCursor = 0;
                cold.PathRequest = 0;
            }
            n.TaskTime = 0;
            return;
        }
        if ((n.Flags & Path) != 0) return;
        n.TaskTime -= dt;
        if (n.TaskTime > 0) return;
        WalkToSite(ref n, cold, site, GoalOf(world, site, m));
        n.Mode = SpeedMode.Free;
        n.TaskTime = PursuitSystem.RepathSeconds;
    }

    // ------------------------------------------------------------------ the tick

    public void SlowWorld(World world)
    {
        ProspectStep(world);
        Sync(world);
        Errands(world);
        var research = world.Research;
        research.Rate = 0;
        int bench = 0;
        foreach (var m in world.Machines.All)
        {
            m.LastOperators = m.Operators;
            m.Operators = 0;
            if (m.Data.IsResearch && Site(world, m.Site) is { Complete: true }) bench = Math.Max(bench, m.Data.TechLevel);
        }
        if (bench != research.BenchLevel)
        {
            research.BenchLevel = bench;
            publishedResearch = null;
        }
        if (world.Machines.Count == 0) return;
        float dt = world.TickSeconds;
        foreach (var m in world.Machines.All)
        {
            if (m.Farm is not { } farm || Site(world, m.Site) is not { Complete: true } site || !Live(world, m)) continue;
            // A farm back in a loaded zone after over an hour catches up (Verified, FarmBuilding::loadFromSerialise FUN_1400e3de0).
            if (!UnloadedProduction && world.GameHours - farm.LiveHours > 1) TimeSkip(world, m, (float)(world.GameHours - farm.LiveHours), site.Owner != world.Player.Faction);
            farm.LiveHours = world.GameHours;
            FarmTick(world, m, dt);
        }
        // An automatic machine works with nobody at it, at skill factor 1 (Verified, ProductionBuilding::vfunc_28 calls slot 150 with no operator when +0x3a9 is clear).
        foreach (var m in world.Machines.All)
            if (m.Data.IsAutomatic && m.Data.IsProducer && Site(world, m.Site) is { Complete: true } && Live(world, m)) ProduceStep(world, m, 1, dt);
            else if (m.Data.IsFurnace && m.Storage is { } grid)
            {
                if (m.Burning && Site(world, m.Site) is { Complete: true } && Live(world, m)) FurnaceStep(world, m, dt);
                float metal = FurnaceMetal(grid);
                if (metal != m.Metal) { m.Metal = metal; world.Machines.Touch(); }
            }

        var table = world.Characters;
        int contributions = 0;
        for (int i = 0; i < table.HighWater; i++)
        {
            ref var n = ref table.Next[i];
            if (!n.Alive) continue;
            if (n.Task == (byte)CharacterTask.Haul) { HaulStep(world, i); continue; }
            if (n.Task != (byte)CharacterTask.Operate) continue;
            var cold = table.Cold(i)!;
            if (cold.OperateSite is not { } id || Site(world, id) is not { Complete: true } site || world.Machines.Find(id) is not { } m || !Able(cold))
            {
                End(table, i);
                continue;
            }
            cold.Operating = false;
            if (Hauling(world, cold) is { } haul)
            {
                if (AtSite(n, haul.Site, haul.Machine)) HaulAt(world, cold, m, haul.Machine);
                continue;
            }
            if (!AtSite(n, site, m)) continue;
            // A rock (an "is foliage" building) becomes the operator's faction's once a player character works it, and joins a player town whose area holds it (Task_OperateMachine
            // slot 1, FUN_14035b430; the town by its radius here).
            if (cold.Faction == world.Player.Faction && site.Owner != cold.Faction && IsNatural(site.Id))
            {
                site.Owner = cold.Faction;
                var at = new System.Numerics.Vector2(site.Position.X, site.Position.Z);
                site.Town = world.Buildings.Towns.Where(t => t.Owner == cold.Faction && System.Numerics.Vector2.Distance(new(t.Position.X, t.Position.Z), at) <= t.Radius)
                    .OrderBy(t => System.Numerics.Vector2.DistanceSquared(new(t.Position.X, t.Position.Z), at)).Select(t => t.Number).FirstOrDefault();
            }
            // A turret: manned, facing it; the combat system fires it (docs/simulation.md "Combat", turrets). Its gunner counts as an operator, so it draws its power.
            if (m.Data.Function == Meitou.Data.World.ObjectRanges.TurretFunction)
            {
                // A broken turret makes manning impossible (Task_UseTurret FUN_1403474f0 -> AITaskSytem::taskImpossible): the job ends.
                if (m.Broken) { End(table, i); continue; }
                if (n.Velocity.X == 0 && n.Velocity.Z == 0) n.Yaw = FacingTo(n, site);
                m.Operators++;
                cold.Operating = true;
                cold.UseSkill(StatsEnumerated.Turrets, world.Tick);
                continue;
            }
            // A training dummy: the user trains the building's stat (docs/game/ai-tasks.md "Finders of the jobs").
            if (m.Data.IsTraining)
            {
                cold.UseSkill((StatsEnumerated)m.Data.Stat, world.Tick);
                Train(ref n, cold, site, m, dt);
                continue;
            }
            // A chair (an implicit job): sat on, no work.
            if (!m.Data.Operable) { Settle(ref n, cold, site, m); continue; }
            // An automatic machine (the 152 job): its hauler fills its inputs and carries its output away; the machine works by itself.
            if (m.Data.IsAutomatic)
            {
                if (!StartHaul(world, cold, site, m) && n.Velocity.X == 0 && n.Velocity.Z == 0) n.Yaw = FacingTo(n, site);
                continue;
            }
            if (Settings.AutoHaul && StartHaul(world, cold, site, m)) continue;
            if (n.Velocity.X == 0 && n.Velocity.Z == 0) n.Yaw = FacingTo(n, site);
            m.Operators++;
            var stat = (StatsEnumerated)m.Data.Stat;
            float skill = m.Data.Stat > 0 ? Effective(cold, stat) : -1;
            float s = skill < 0 ? 1 : Lerp(0.5f, 1.4f, Math.Clamp(skill, 0, 100) / 100);
            bool worked;
            if (m.Data.IsResearch) worked = ResearchStep(world, m, s, dt, ref contributions);
            else if (m.Data.IsCrafting) worked = CraftStep(world, m, skill, s, dt);
            // A growing crop with water is tended: the operator plays the farm's clip (engine reading of task 87's start conditions, docs/game/buildings-production.md "Farming").
            else if (m.Farm is not null) worked = HarvestStep(world, m, skill, s, dt) || Tended(m);
            else worked = ProduceStep(world, m, s, dt);
            cold.Operating = worked;
            // Operating trains the stat (Observed, FUN_1408c7250: 0.5, farming x1.5, through the continuous entry), every frame at the machine.
            if (m.Data.Stat > 0) cold.UseSkill(stat, world.Tick);
            if (m.Data.Stat > 0 && cold.Stats is { } stats && cold.Race is { } race) xp.Continuous(stats, race, stat, dt, OperateXp(m.Data.Stat));
        }
        PowerTick(world, dt);
        if (research.Queue.Count > 0 && contributions > 0) research.Touch();
        world.Machines.Touch();
    }

    /// <summary>The operating XP amount of a stat (<b>Observed</b>, <c>FUN_1408c7250</c>): 0.5, stats 10 and 37 0.1, 11 1, 24 3, 35 1.2 (0.8 falling into the farming case), 12 (farming) 0.75.</summary>
    static float OperateXp(int stat) => stat switch { 10 or 37 => 0.1f, 11 => 1, 24 => 3, 35 => 1.2f, 12 => 0.75f, _ => 0.5f };

    static float Effective(CharacterCold cold, StatsEnumerated stat) => (cold.Stats?[stat] ?? 0) * (cold.Medical?.StatMultiplier(stat) ?? 1);

    static float Lerp(float a, float b, float t) => a + (b - a) * t;

    // ------------------------------------------------------------------ production

    /// <summary>
    /// One operator's work on a producer (<b>Observed</b>, <c>FUN_140298700</c>): <c>yield x rate x dt x P x s x option</c>, clamped to what the inputs hold and the output has room for
    /// (<c>vfunc_185</c>); the inputs are drawn and each 0.999 of progress makes one output item.
    /// </summary>
    bool ProduceStep(World world, Machine m, float s, float dt)
    {
        if (m.Output is not { } output) return false;
        float amount = m.Yield * m.Data.Rate * WorkFactor(world, m) * dt * constants.ProductionSpeed * s * Settings.ProductionOption;
        foreach (var input in m.Inputs)
        {
            float rate = input.Rate;
            if (rate == 0) continue;
            float available = input.Level + input.Count;
            if (available < rate * amount) amount = available / rate;
        }
        amount = MathF.Min(amount, output.Recipe.Capacity - output.Count - output.Progress);
        if (amount <= 0)
        {
            m.Status = (byte)(output.Full ? 1 : 0);
            return false;
        }
        m.Status = 2;
        foreach (var input in m.Inputs)
        {
            float need = input.Rate * amount;
            while (input.Level < need && input.Count > 0) { input.Count--; input.Level += 1; }
            input.Level = MathF.Max(0, input.Level - need);
        }
        output.Progress += amount;
        while (output.Progress >= 0.999f && !output.Full)
        {
            output.Count++;
            output.Progress = MathF.Max(0, output.Progress - 1);
        }
        if (output.Full) output.Progress = 0;
        return true;
    }

    // ------------------------------------------------------------------ farms

    /// <summary>Replants (<b>Observed</b>, <c>FUN_1400e48e0</c>): each plant's growth -0.2 x U[0, 1), the farm's the lowest of them, nothing harvested, no death.</summary>
    static void Replant(World world, Machine m)
    {
        var f = m.Farm!;
        ulong key = Rng.StableHash(m.Site);
        float start = 0;
        for (int i = 0; i < f.Plants.Length; i++)
        {
            float g = -0.2f * Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Farm, (ulong)f.Plantings * 4096 + (ulong)i));
            f.Plants[i] = g;
            start = MathF.Min(start, g);
        }
        f.Plantings++;
        f.Start = start;
        f.Growth = start;
        f.Harvested = 0;
        f.Death = 0;
        f.ClearProgress = 0;
        f.HarvestProgress = 0;
    }

    /// <summary>Whether a machine updates: always with <see cref="UnloadedProduction"/>, else only in an active zone (Verified, ZoneManager updates the buildings of its active zones only).</summary>
    bool Live(World world, Machine m) =>
        UnloadedProduction || world.ActiveZones is not { } active || active.Contains(Meitou.Data.World.WorldLayout.ZoneOf(m.Position.X, m.Position.Z));

    /// <summary>
    /// A farm's catch-up for <paramref name="h"/> unloaded game hours (<b>Verified</b>, <c>FarmBuilding::timeSkip</c>, docs/game/buildings-production.md "Unloaded zones"): the crop advances along its
    /// cycle (growth, harvest time, death time) for the watered hours, then at the drought rate while its death grows; a completed cycle replants. An NPC farm ignores water.
    /// </summary>
    internal void TimeSkip(World world, Machine m, float h, bool ignoreWater)
    {
        var f = m.Farm!;
        var fd = m.Data.Farm!;
        float g = fd.GrowthTime / MathF.Max(m.Yield * m.Power, 1e-6f), cycle = g + fd.HarvestTime + fd.DeathTime;
        if (cycle <= 0) return;
        float p = f.Growth < 1 ? MathF.Max(f.Growth, 0) * g : f.Growth < 2 ? g + (f.Growth - 1) * fd.HarvestTime : g + fd.HarvestTime + f.Death * fd.DeathTime;
        float perHour = m.WaterRate * (MachineData.DaySeconds / 24), water = float.MaxValue;
        foreach (var input in m.Inputs) water = MathF.Min(water, input.Level + input.Count);
        // The watered hours divide by h as well, as the game writes it.
        float w = m.Inputs.Length == 0 || perHour <= 0 ? float.MaxValue : water / (perHour * h);
        bool replant = false;
        float death = 0;
        if (ignoreWater || h <= w)
        {
            p += h;
            replant = p > cycle;
        }
        else
        {
            p = (p + w) % cycle;
            float dm = fd.DroughtMultiplier > 0 ? fd.DroughtMultiplier : 1e-8f;
            p += (h - w) * dm;
            death = fd.DroughtDeathTime > 0 ? (h - w) / fd.DroughtDeathTime : 0;
            if (death > 1) { replant = true; death %= 1; }
            replant |= p > cycle;
        }
        p %= cycle;
        if (replant) Replant(world, m);
        float growth = p < g ? p / g : p < g + fd.HarvestTime ? 1 + (p - g) / MathF.Max(fd.HarvestTime, 1e-6f) : 2;
        float delta = growth - f.Growth;
        for (int i = 0; i < f.Plants.Length; i++)
            if (f.Plants[i] >= -1) f.Plants[i] = MathF.Min(f.Plants[i] + delta, MathF.Min(growth, 1));
        f.Growth = growth;
        f.Death = growth >= 2 ? (p - g - fd.HarvestTime) / MathF.Max(fd.DeathTime, 1e-6f) : death;
        if (ignoreWater) return;
        foreach (var input in m.Inputs)
        {
            float need = MathF.Min(w, h) * perHour - input.Level;
            input.Level = 0;
            if (need > 0) input.Count = Math.Max(0, input.Count - (int)MathF.Ceiling(need));
        }
    }

    /// <summary>How ripe a farm is, 0..1 over its growth (the HUD's figure): the growth while growing, 1 once ripe.</summary>
    public static float Ripeness(FarmState f) => Math.Clamp(f.Growth, 0, 1);

    /// <summary>
    /// The water draw (<b>Observed</b>, <c>FUN_1400df4c0</c>): each input needs <c>amount</c>; one below it takes an item from its section, or the draw fails (its level 0).
    /// </summary>
    static bool DrawWater(Machine m, float amount)
    {
        foreach (var input in m.Inputs)
            if ((input.Level < amount || input.Level <= 0) && input.Count == 0)
            {
                input.Level = 0;
                return false;
            }
        foreach (var input in m.Inputs)
        {
            if (input.Level < amount && input.Count > 0) { input.Count--; input.Level += 1; }
            input.Level -= amount;
        }
        return true;
    }

    /// <summary>The factor of the rain's water (the exe's constant at <c>14168a254</c>, <b>Verified</b>).</summary>
    const float RainWaterFactor = 3;

    /// <summary>The farm tick (<b>Observed</b>, <c>FarmBuilding::vfunc_28</c>), every tick whether anyone is there. An outdoor farm's first input is rained on first: level += wetness x water rate x rate x dt x 3, at most 1 (<b>Verified</b>, <c>FUN_1400e5580</c>).</summary>
    void FarmTick(World world, Machine m, float dt)
    {
        var f = m.Farm!;
        var fd = m.Data.Farm!;
        float h = dt / (MachineData.DaySeconds / 24);
        if (m.Inputs.Length > 0 && !fd.Indoor && world.Weather?.At(m.Position.X, m.Position.Z) is { Wetness: > 0 } rain)
            m.Inputs[0].Level = MathF.Min(1, m.Inputs[0].Level + rain.Wetness * m.WaterRate * m.Inputs[0].Rate * dt * RainWaterFactor);
        bool watered = m.Inputs.Length == 0 || DrawWater(m, dt * m.WaterRate);
        float inc = h * m.Yield * m.Power / fd.GrowthTime;
        if (!watered) inc *= fd.DroughtMultiplier;
        bool changed = false;
        if (f.Growth >= 1)
        {
            if (f.Growth >= 2)
            {
                f.Death += h / fd.DeathTime;
                changed = true;
            }
            else f.Growth += h / fd.HarvestTime;
        }
        else if (inc > 0)
        {
            changed = true;
            float lowest = 1;
            for (int i = 0; i < f.Plants.Length; i++)
            {
                if (f.Plants[i] < 1 && f.Plants[i] >= -1)
                {
                    f.Plants[i] += inc;
                    lowest = MathF.Min(lowest, f.Plants[i]);
                }
            }
            f.Growth = lowest;
        }
        if (fd.DroughtDeathTime > 0 && f.Growth > 0 && f.Growth < 1)
        {
            if (!watered) f.Death += h / fd.DroughtDeathTime;
            else if (f.Death > 0 && f.Death < 0.6f) f.Death = MathF.Max(0, f.Death - h / fd.DroughtDeathTime);
            if (f.Death > 0) changed = true;
        }
        if (f.Death >= 1 || f.Harvested >= f.Plants.Length) Replant(world, m);
        _ = changed;
        bool harvestable = f.Growth >= 1 && f.Death < fd.DeathThreshold && f.Harvested < f.Plants.Length;
        if (harvestable) f.Status = f.Status == FarmStatus.OutputFull && m.Output is { Full: true } ? FarmStatus.OutputFull : FarmStatus.Harvest;
        else if (f.Death > fd.DeathThreshold) f.Status = FarmStatus.Harvest;
        else f.Status = watered ? FarmStatus.Growing : FarmStatus.NoWater;
    }

    /// <summary>
    /// One operator's work on a farm (<b>Observed</b>, <c>FarmBuilding::vfunc_150</c>): a ripe crop is harvested plant by plant (<c>P x dt x s x harvest rate</c>, each plant rolling its
    /// <c>output per plant</c> against the efficiency), a dead one cleared (<c>clear rate</c>).
    /// </summary>
    bool HarvestStep(World world, Machine m, float farming, float s, float dt)
    {
        var f = m.Farm!;
        var fd = m.Data.Farm!;
        float work = constants.ProductionSpeed * dt * s;
        if (f.Growth >= 1 && f.Death < fd.DeathThreshold && f.Harvested < f.Plants.Length)
        {
            if (m.Output is not { } output) return false;
            if (output.Full)
            {
                f.Status = FarmStatus.OutputFull;
                return false;
            }
            f.HarvestProgress += work * fd.HarvestRate / MachineData.DaySeconds;
            if (f.HarvestProgress < 1) return true;
            float skill = farming < 0 ? 0.01f : farming * 0.01f;
            float e = Math.Clamp(Lerp(0.5f, 2, skill), 0, 1) * m.CropMatch * m.Yield;
            ulong key = Rng.StableHash(m.Site);
            int whole = (int)fd.OutputPerPlant;
            int made = 0;
            for (int u = 0; u < whole; u++)
                if (Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Farm, (1UL << 40) | (uint)f.Rolls++)) < e) made++;
            float frac = fd.OutputPerPlant - whole;
            if (frac > 0 && Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Farm, (1UL << 40) | (uint)f.Rolls++)) < frac) made++;
            output.Count = Math.Min(output.Recipe.Capacity, output.Count + made);
            f.HarvestProgress = 0;
            NextPlant(f);
            return true;
        }
        if (f.Death >= fd.DeathThreshold)
        {
            f.ClearProgress += work * fd.ClearRate / MachineData.DaySeconds;
            if (f.ClearProgress >= 1)
            {
                f.ClearProgress = 0;
                NextPlant(f);
            }
            return true;
        }
        return false;
    }

    /// <summary>A farm whose crop is still growing and whose every input holds water.</summary>
    static bool Tended(Machine m) =>
        m.Farm is { Growth: < 1 } f && f.Death < m.Data.Farm!.DeathThreshold && m.Inputs.All(i => i.Rate <= 0 || i.Level + i.Count > 0);

    /// <summary>
    /// <c>FarmBuilding::eat</c> (<c>FUN_1400e5e60</c>, docs/game/ai-tasks.md "Eating crops"): adds <paramref name="amount"/> (the eater's age, 1, x the game seconds) to the eat accumulator; past 1
    /// a plant is destroyed as a harvest takes it (<see cref="NextPlant"/>) and the accumulator restarts. False once no plant is left.
    /// </summary>
    public static bool EatCrop(FarmState f, float amount)
    {
        f.EatProgress += amount;
        if (f.EatProgress <= 1) return f.Harvested < f.Plants.Length;
        f.EatProgress = 0;
        if (f.Harvested >= f.Plants.Length) return false;
        NextPlant(f);
        return true;
    }

    /// <summary>Whether animals may eat a farm (<c>AI::farmHasFood</c> <c>FUN_1408ca350</c>): grown past 0.4, dead under 0.9.</summary>
    public static bool HasFood(FarmState f) => f.Growth > 0.4f && f.Death < 0.9f && f.Harvested < f.Plants.Length;

    /// <summary>A plant is done (<b>Observed</b>, <c>FUN_1400e4800</c>): its growth -10, one more harvested.</summary>
    static void NextPlant(FarmState f)
    {
        if (f.Harvested >= f.Plants.Length) return;
        f.Plants[f.Harvested] = -10;
        f.Harvested++;
    }

    // ------------------------------------------------------------------ research

    /// <summary>
    /// One operator's work at a research bench (<b>Observed</b>, <c>FUN_14029b8d0</c> and <c>FUN_140836b70</c>): <c>rate x s</c>, x 0.6 for each earlier contribution this tick, adds that x dt
    /// seconds to the first queued research, which completes past <c>hours x G/24</c>.
    /// </summary>
    bool ResearchStep(World world, Machine m, float s, float dt, ref int k)
    {
        var research = world.Research;
        float c = m.Data.Rate * WorkFactor(world, m) * s;
        for (int i = 0; i < k; i++) c *= ResearchFalloff;
        k++;
        research.Rate += c;
        if (research.Queue.Count == 0) return false;
        var item = research.Queue[0];
        item.Seconds += c * dt;
        if (Research(item.Id) is not { } data)
        {
            research.Dequeue(item.Id);
            return false;
        }
        if (item.Seconds > data.Hours(constants, Settings.ResearchOption) * (MachineData.DaySeconds / 24))
        {
            research.Complete(db, item.Id);
            publishedResearch = null;
            world.Post(Loc.Tr("Research complete: {1}", data.Name));
        }
        return true;
    }

    /// <summary>
    /// A blueprint read (<b>Verified</b>, <c>BlueprintItem::learnResearch</c> <c>FUN_1402b65d0</c>): its research's requirements unmet (<c>checkRequirements</c> without the level or cost
    /// test) or the research already finished once posts a message and keeps the blueprint (false); otherwise the research completes as at a bench and the caller uses the blueprint up.
    /// </summary>
    public bool Learn(World world, string id)
    {
        if (Research(id) is not { } data) return false;
        if (!Met(world, data))
        {
            world.Post(Loc.Tr("Failed to learn from blueprint: Technology requirements not met"));
            return false;
        }
        if (world.Research.IsDone(id))
        {
            world.Post(Loc.Tr("Research already known"));
            return false;
        }
        world.Research.Complete(db, id);
        publishedResearch = null;
        world.Post(Loc.Tr("Research complete: {1}", data.Name));
        return true;
    }

    /// <summary><see cref="Learn(World, string)"/> for a blueprint item (false for any other item).</summary>
    public bool Learn(World world, ItemInstance blueprint) => items.ResearchOf(blueprint) is { } research && Learn(world, research.StringId);

    /// <summary>The research the player may queue now (<b>Observed</b>, <c>FUN_1408333b0</c>): not blueprint-only, not exhausted (every repetition done) or queued, <c>level</c> within the tech level, requirements met.</summary>
    public bool Available(World world, ResearchData r) =>
        !r.BlueprintOnly && !world.Research.Exhausted(db, r.Id) && !world.Research.IsQueued(r.Id) && r.Level <= world.Research.TechLevel && Met(world, r);

    bool Met(World world, ResearchData r)
    {
        foreach (var req in r.Requirements)
        {
            var record = db.Find(req);
            if (record is null) continue;
            if (record.Type == FcsRecordType.RESEARCH ? !world.Research.IsDone(req) : !world.Research.IsUnlocked(req)) return false;
        }
        return true;
    }

    /// <summary>The items of the player's research benches (where costs are paid from; engine: every bench, the original takes the benches near the player).</summary>
    IEnumerable<Inventory> Benches(World world)
    {
        foreach (var m in world.Machines.All)
            if (m.Data.IsResearch && m.Storage is { } s && Site(world, m.Site) is { Complete: true } site && site.Owner == world.Player.Faction) yield return s;
    }

    int CountInBenches(World world, string item)
    {
        int n = 0;
        foreach (var s in Benches(world))
            foreach (var i in s.Items)
                if (i.Record == item) n += i.Quantity;
        return n;
    }

    bool Affordable(World world, ResearchData r)
    {
        if (world.Research.Paid.Contains(r.Id, StringComparer.Ordinal)) return true;
        foreach (var (item, count) in r.CostAt(world.Research.Times(r.Id)))
            if (CountInBenches(world, item) < count) return false;
        return true;
    }

    void Queue(World world, string id)
    {
        if (Research(id) is not { } r || !Available(world, r)) return;
        if (r.Level > world.Research.BenchLevel)
        {
            world.Post(world.Research.BenchLevel == 0 ? "Research needs a research bench." : $"{r.Name} needs a level {r.Level} research bench.");
            return;
        }
        if (!Affordable(world, r))
        {
            world.Post($"Not enough materials in the research benches for {r.Name}.");
            return;
        }
        if (!world.Research.Paid.Contains(id, StringComparer.Ordinal))
            foreach (var (item, count) in r.CostAt(world.Research.Times(id)))
            {
                int left = count;
                foreach (var s in Benches(world))
                    foreach (var i in s.Items.Where(i => i.Record == item).ToList())
                    {
                        int take = Math.Min(left, i.Quantity);
                        i.Quantity -= take;
                        if (i.Quantity == 0) s.Items.Remove(i);
                        left -= take;
                        if (left == 0) break;
                    }
            }
        world.Research.Enqueue(id);
        world.Machines.Touch();
    }

    /// <summary>The research state for the host (rebuilt when it or the machines changed).</summary>
    public ResearchSnapshot ResearchView(World world)
    {
        if (publishedResearch is not null && researchVersion == world.Research.Version && machinesVersion == world.Machines.Version) return publishedResearch;
        researchVersion = world.Research.Version;
        machinesVersion = world.Machines.Version;
        allResearch ??= ResearchData.LoadAll(db);
        var state = world.Research;
        var queue = state.Queue.Select(q => (q.Id, Research(q.Id)?.Name ?? q.Id, Research(q.Id) is { } d ? Math.Clamp(q.Seconds / (d.Hours(constants, Settings.ResearchOption) * (MachineData.DaySeconds / 24)), 0, 1) : 0)).ToArray();
        var available = allResearch.Where(r => Available(world, r))
            .Select(r => new ResearchOption(r.Id, r.Name, r.Category, r.Level, r.Hours(constants, Settings.ResearchOption), Affordable(world, r), r.Level > state.BenchLevel,
                state.Partial.TryGetValue(r.Id, out float s) ? Math.Clamp(s / (r.Hours(constants, Settings.ResearchOption) * (MachineData.DaySeconds / 24)), 0, 1) : 0)
            {
                Description = r.Description,
                Cost = state.Paid.Contains(r.Id, StringComparer.Ordinal) ? [] : [.. r.CostAt(state.Times(r.Id)).Select(c => (items.Info(c.Item)?.Name ?? c.Item, c.Count))],
            }).ToArray();
        return publishedResearch = new ResearchSnapshot(state.TechLevel, state.BenchLevel, queue, state.Rate, available) {
            Finished = state.Completed.Count, Known = state.Completed.ToHashSet(StringComparer.Ordinal),
            RequirementsMet = allResearch.Where(r => Met(world, r)).Select(r => r.Id).ToHashSet(StringComparer.Ordinal),
        };
    }

    // ------------------------------------------------------------------ items in and out

    bool Near(World world, CharacterId who, BuildSite site, out CharacterCold cold)
    {
        cold = null!;
        var table = world.Characters;
        if (!table.TryResolveNext(who, out int slot) || table.Cold(slot) is not { IsPlayer: true } c || c.Inventory is null) return false;
        cold = c;
        if (GapTo(site, table.Next[slot].Position) <= Settings.TransferReach) return true;
        world.Post("Out of trading range.");
        return false;
    }

    /// <summary>
    /// Put an item into a container's grid (<see cref="PlayerSystem.ContainerFor"/>): at a free cell, or with X below 0 where it first fits (<see cref="PutInMachine.Count"/> units); a bag or limb
    /// item is refused like the machine's.
    /// </summary>
    static void PutInContainer(World world, Locks.Fixture container, CharacterCold cold, PutInMachine put)
    {
        if (PlayerSystem.ShopOf(world, container) is not null) { world.Post("That stock is the shop's."); return; }   // engine choice: a shop's stock is not a place to leave things
        var root = cold.Inventory!;
        var storage = PlayerSystem.GridOf(world, container);
        var item = root.All().ElementAtOrDefault(put.Item.Index);
        var token = put.Item;
        if (item is null || item.Record != token.Record || item.Section != token.Section || item.X != token.X || item.Y != token.Y || item.Quantity != token.Quantity || item.LockedOn ||
            !InventoryCommands.IsMovableSection(item.Section) || item.Contents is not null && item.Section == "backpack_attach") return;
        var owner = root.Items.Contains(item) ? root : root.Backpack?.Contents;
        if (owner is null) return;
        if (put.X < 0)
        {
            if (InventoryCommands.MoveUnits(owner, item, put.Count, storage) > 0) world.Machines.Touch();
            return;
        }
        if (put.Y < 0 || put.X > storage.Width - item.Width || put.Y > storage.Height - item.Height || !storage.IsFree("main", put.X, put.Y, item.Width, item.Height)) return;
        owner.Remove(item);
        item.Section = "main";
        item.X = put.X;
        item.Y = put.Y;
        storage.Items.Add(item);
        world.Machines.Touch();
    }

    /// <summary>
    /// Take an item (<see cref="TakeFromMachine.Count"/> units) out of a container into the character's inventory (<see cref="InventoryCommands.MoveInto"/>); what does not fit stays in the
    /// container. Taken from another faction's container it is stolen from that faction: a Theft and the steal roll (<see cref="Crime.CrimeSystem.Steal"/>), caught, nothing moves.
    /// </summary>
    void TakeFromContainer(World world, Locks.Fixture container, CharacterCold cold, TakeFromMachine take)
    {
        var storage = PlayerSystem.GridOf(world, container, out var region);
        if (take.Index < 0 || take.Index >= storage.Items.Count || !region.Holds(storage.Items[take.Index])) return;
        var item = storage.Items[take.Index];
        if (world.SystemOf<Crime.CrimeSystem>() is { } crimes && world.Characters.TryResolveNext(take.Character, out int thief)
            && crimes.Steal(world, thief, container, Crime.CrimeCode.Theft)) return;
        int mark = item.StolenFrom;
        if (container.Owner >= 0 && container.Owner != cold.Faction) item.StolenFrom = container.Owner;
        int moved = InventoryCommands.MoveInto(storage, item, take.Count, cold.Inventory!, take.BagFirst, out bool worn, cold.Race);
        if (storage.Items.Contains(item)) item.StolenFrom = mark;   // what stayed keeps its mark
        if (worn) Assembly?.RefreshEquipment(cold);
        if (moved > 0) world.Machines.Touch();
    }

    /// <summary>Re-dresses a character that put on something taken out of a storage or container (null: not re-dressed).</summary>
    internal CharacterAssembly? Assembly { get; set; }
    /// <summary>The beds the "Sleep when injured" option lies in (null: the option does nothing).</summary>
    public Locks.BedSystem? Beds { get; set; }

    /// <summary>
    /// The complete machine at <paramref name="site"/> when <paramref name="who"/> (one of the player's characters) may move items in and out of it from where it stands; posts
    /// "Out of trading range." when it is too far. For the held item (<see cref="PlayerState.Held"/>).
    /// </summary>
    internal Machine? Reach(World world, CharacterId who, string site, out CharacterCold? cold)
    {
        cold = null;
        if (Site(world, site) is not { Complete: true } built || Ensure(world, built) is not { } m || !Near(world, who, built, out var c)) return null;
        cold = c;
        return m;
    }

    void Put(World world, PutInMachine put)
    {
        if (PlayerSystem.ContainerFor(world, put.Site, put.Character, out var holder) is { } container)
        {
            if (holder is not null && put.Slot == -1) PutInContainer(world, container, holder, put);
            return;
        }
        if (Site(world, put.Site) is not { Complete: true } site || Ensure(world, site) is not { } m || !Near(world, put.Character, site, out var cold)) return;
        var root = cold.Inventory!;
        var item = root.All().ElementAtOrDefault(put.Item.Index);
        var token = put.Item;
        if (item is null || item.Record != token.Record || item.Section != token.Section || item.X != token.X || item.Y != token.Y || item.Quantity != token.Quantity || item.LockedOn ||
            !InventoryCommands.IsMovableSection(item.Section)) return;
        var owner = root.Items.Contains(item) ? root : root.Backpack?.Contents;
        if (owner is null) return;
        if (put.Slot >= 0)
        {
            if (put.Slot >= m.Inputs.Length || m.Inputs[put.Slot] is not { } input || input.Item != item.Record) return;
            int room = input.Recipe.Capacity - input.Count;
            int n = Math.Min(room, put.Count > 0 ? Math.Min(put.Count, item.Quantity) : item.Quantity);
            if (n <= 0) return;
            input.Count += n;
            item.Quantity -= n;
            if (item.Quantity == 0) owner.Remove(item);
        }
        else
        {
            if (m.Storage is not { } storage || m.Closed || item.Contents is not null && item.Section == "backpack_attach") return;
            if (m.Data.IsStorage && !m.Data.Accepts(item.Record, item.Type))
            {
                world.Post($"{m.Data.Name} does not take {item.Name}.");
                return;
            }
            if (put.X < 0)
            {
                if (InventoryCommands.MoveUnits(owner, item, put.Count, storage) > 0) world.Machines.Touch();
                return;
            }
            if (put.Y < 0 || put.X > storage.Width - item.Width || put.Y > storage.Height - item.Height || !storage.IsFree("main", put.X, put.Y, item.Width, item.Height)) return;
            owner.Remove(item);
            item.Section = "main";
            item.X = put.X;
            item.Y = put.Y;
            storage.Items.Add(item);
        }
        world.Machines.Touch();
    }

    void Take(World world, TakeFromMachine take)
    {
        if (PlayerSystem.ContainerFor(world, take.Site, take.Character, out var holder) is { } container)
        {
            if (holder is not null && take.Slot == -1) TakeFromContainer(world, container, holder, take);
            return;
        }
        if (Site(world, take.Site) is not { Complete: true } site || world.Machines.Find(take.Site) is not { } m || !Near(world, take.Character, site, out var cold)) return;
        var root = cold.Inventory!;
        if (take.Slot == -1)
        {
            if (m.Storage is not { } storage || m.Closed || take.Index < 0 || take.Index >= storage.Items.Count) return;
            // What does not fit stays in the storage.
            if (InventoryCommands.MoveInto(storage, storage.Items[take.Index], take.Count, root, take.BagFirst, out bool worn, cold.Race) > 0) world.Machines.Touch();
            if (worn) Assembly?.RefreshEquipment(cold);
            return;
        }
        if (take.Slot == -2 && m.Craft is { } craft)
        {
            TakeCrafted(world, m, craft, root, take.Index, take.Count);
            return;
        }
        (string Item, int Count) held = take.Slot == -2 ? (m.Output?.Recipe.Item ?? "", m.Output?.Count ?? 0) : take.Slot >= 0 && take.Slot < m.Inputs.Length ? (m.Inputs[take.Slot].Item, m.Inputs[take.Slot].Count) : ("", 0);
        if (take.Count > 0) held.Count = Math.Min(held.Count, take.Count);
        if (held.Count <= 0 || items.Create(held.Item, held.Count) is not { } made) return;
        int moved = Placed(root, made);
        if (moved <= 0) return;
        if (take.Slot == -2) m.Output!.Count -= moved;
        else m.Inputs[take.Slot].Count -= moved;
        world.Machines.Touch();
    }
}
