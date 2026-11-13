using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Construction;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Items;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Production;

/// <summary>
/// The numbers of <see cref="ProductionSystem"/>; the engine choices say so (docs/simulation.md "Production").
/// </summary>
public sealed class ProductionSettings
{
    /// <summary>
    /// The <i>Production speed</i> option (multiplies production work); its default is Unknown, 1 is neutral.
    /// </summary>
    public float ProductionOption { get; init; } = 1;
    /// <summary>
    /// The <i>Research speed</i> option (divides research hours); its default is Unknown, 1 is neutral.
    /// </summary>
    public float ResearchOption { get; init; } = 1;
    /// <summary>
    /// The footprint radius of a BUILDING record (the host knows it); null: 0.
    /// </summary>
    public Func<string, float>? Footprint { get; init; }
    /// <summary>
    /// The X/Z gap from a point to a site's footprint hull, 0 inside; null, or a null answer: the gap to its position (<see cref="SiteReach"/>).
    /// </summary>
    public Func<BuildSite, Vector2, float?>? EdgeGap { get; init; }
    /// <summary>
    /// A character gives or takes items from within this far (X/Z) of a building's footprint, else "Out of trading range." (the original: the
    /// building's bounds radius + 40 from its position for an item out of it, 60 for one out of a character's inventory, docs/game/economy.md
    /// "Trading range"; engine choice: 40 from the footprint both ways, or 60 from the position, <see cref="ContainerAccess.TradingRange"/>).
    /// </summary>
    public float TransferReach { get; init; } = 40;
    /// <summary>
    /// Operators empty a full output into storages and fill empty inputs from them (docs/simulation.md "Production"; engine option: the original's
    /// wording "put any excess materials ... into storage" is the Ditch items squad AI option's, <see cref="SquadAi.Ditch"/>).
    /// </summary>
    public bool AutoHaul { get; init; } = true;
    /// <summary>
    /// Farms, automatic machines and generators work in unloaded zones too (Meitou default); false: they stop there and farms catch up when their
    /// zone loads again, as the game does (docs/game/buildings-production.md "Unloaded zones").
    /// </summary>
    public bool UnloadedProduction { get; init; } = true;
    /// <summary>
    /// How far (X/Z) from its machine an operator hauls (engine choice; the original's limit is Unknown).
    /// </summary>
    public float HaulRange { get; init; } = 3000;
}

/// <summary>
/// Production, farming and research of the player's complete buildings (docs/simulation.md "Production"; the original's rules in
/// docs/game/buildings-production.md "Production", "Farming", "Research").
/// <list type="bullet">
/// <item><b>Machines</b> (<see cref="World.Machines"/>): one per complete player building that works or stores, made when the site completes, dropped
/// (its items on the ground) with it.</item>
/// <item><b>Operators</b>: <see cref="OperateOrder"/> sets <see cref="CharacterTask.Operate"/>; <c>Schedule</c> walks the character there,
/// <c>SlowWorld</c> applies its work each tick with the skill factor <c>lerp(stat/100, 0.5, 1.4)</c> and the operating XP.</item>
/// <item><b>Farms</b> grow every tick, whether anyone is there; operators harvest ripe crops and clear dead ones. <b>Research</b> benches feed the
/// first queued research with the 0.6 falloff.</item>
/// </list>
/// Placed after construction and before combat; everything here is serial and reads only the world's state and the command order.
/// </summary>
public sealed partial class ProductionSystem : ITickSystem, ITaskOwner, ITaskHandler, IOrderOwner, IDecisionOwner, IGoalSource
{
    #region State and wiring

    /// <summary>
    /// The game data: the machines' and research records.
    /// </summary>
    readonly GameDatabase _db;

    /// <summary>
    /// The game's constants.
    /// </summary>
    readonly GameConstants _constants;

    /// <summary>
    /// Makes the items a machine puts out.
    /// </summary>
    readonly ItemFactory _items;

    /// <summary>
    /// The XP operating and training gain.
    /// </summary>
    readonly XpService _xp;

    /// <summary>
    /// The ground's resources, for the mines and prospecting.
    /// </summary>
    readonly ResourceField _resources;

    /// <summary>
    /// The world's resource nodes (null: none).
    /// </summary>
    readonly IResourceNodes? _nodes;

    /// <summary>
    /// The machine facts of each record, by record id (null: not a machine); derived from the data.
    /// </summary>
    readonly Dictionary<string, MachineData?> _machineData = new(StringComparer.Ordinal);

    /// <summary>
    /// Some character may still hold a machine that is gone.
    /// </summary>
    bool _sweep;

    /// <summary>
    /// A production system on the game data (the resource nodes, when given, for the mines).
    /// </summary>
    public ProductionSystem(GameDatabase db, GameConstants constants, BodyOptions options, ItemFactory items, BiomeMap? biomes,
        ProductionSettings? settings = null, IResourceNodes? nodes = null)
    {
        this._db = db;
        this._constants = constants;
        this._items = items;
        Settings = settings ?? new ProductionSettings();
        _xp = new XpService(constants, options);
        _resources = new ResourceField(db, biomes, nodes);
        this._nodes = nodes;
        UnloadedProduction = Settings.UnloadedProduction;
    }

    /// <summary>
    /// A production system on the population's data.
    /// </summary>
    public ProductionSystem(PopulationData data, ProductionSettings? settings = null)
        : this(data.Db, data.Bodies.Constants, data.BodyOptions, data.Items, data.Biomes, settings, data.ResourceNodes)
    {
        _towns = data;
    }

    /// <summary>
    /// The settings (the defaults when none were given).
    /// </summary>
    public ProductionSettings Settings { get; }

    /// <summary>
    /// See <see cref="ProductionSettings.UnloadedProduction"/>; the host sets it live.
    /// </summary>
    public bool UnloadedProduction { get; set; }

    /// <summary>
    /// Makes the items a machine puts out.
    /// </summary>
    public ItemFactory Items => _items;

    /// <summary>
    /// The construction system the engineer job (125) builds through; null: the job never runs.
    /// </summary>
    public ConstructionSystem? Construction { get; init; }

    /// <summary>
    /// Re-dresses a character that put on something taken out of a storage or container (null: not re-dressed).
    /// </summary>
    internal CharacterAssembly? Assembly { get; set; }

    /// <summary>
    /// The beds the "Sleep when injured" option lies in (null: the option does nothing).
    /// </summary>
    public Locks.BedSystem? Beds { get; set; }

    /// <summary>
    /// What a BUILDING does once built (null for one that neither works nor stores); read once.
    /// </summary>
    public MachineData? Data(string record)
    {
        if (!_machineData.TryGetValue(record, out var data))
            _machineData[record] = data = _db.Find(record) is { Type: FcsRecordType.Building } r ? MachineData.From(_db, r) : null;

        return data;
    }

    #endregion

    #region Machines

    /// <summary>
    /// Makes the machine of a complete site (empty, a farm freshly planted) unless it has one; <paramref name="world"/> seeds the planting.
    /// </summary>
    public Machine? Ensure(World world, BuildSite site)
    {
        if (world.Machines.Find(site.Id) is { } existing) return existing;
        if (Create(world, site) is not { } m) return null;
        world.Machines.Add(m);
        return m;
    }

    /// <summary>
    /// A new machine of the site's record (null: the record is no machine): empty, a farm freshly planted.
    /// </summary>
    Machine? Create(World world, BuildSite site)
    {
        if (Data(site.Record) is not { } data) return null;
        var m = new Machine
        {
            Site = site.Id,
            Data = data,
            Position = site.Position,
            WindRotor = data.IsWind && _db.Find(site.Record) is { } record
                ? Meitou.Data.World.WorldObjectLayout.WindRotor(_db, record, site.Position)
                : null,
            Inputs = [.. data.Inputs.Select(i => new InputSlot(i))],
            Output = data.Output is { } o ? new OutputSlot(o) : null,
            Storage = data.HasInventory
                ? new Inventory(data.StorageWidth, data.StorageHeight) { StackBonusMult = data.StackBonusMult, Limited = data.StorageLimit.Count > 0 }
                : data.IsFurnace ? FurnaceGrid() : null,
            Farm = data.Farm is { } fd ? new FarmState(FarmLayout.Count(fd)) : null,
            Craft = data.IsCrafting ? new CraftState() : null,
            // the constructor FUN_1402a3750 starts a production building at 2 (not saved, so a load does too)
            Status = (byte)(data.IsProducer ? 2 : 0),
        };
        var p = site.Position;
        if (data.Farm is { } farm)
        {
            // Every farm alike (FUN_1400e8420): the fertility effect 0 of a hydroponic one makes the ground matter only at or below the minimum, the
            // biome's water usage applies, and the crop match is the biome's unless the farm stands inside a building.
            float fertility = _resources.Value(MachineData.GroundResource, p.X, p.Y, p.Z);
            m.Yield = farm.FertilityFactor(fertility);
            m.Indoors = IsIndoors(data, site);   // the power tick keeps it
            m.CropMatch = _resources.CropMatch(farm, p.X, p.Z, m.Indoors);
            m.WaterRate = farm.ConsumptionRate / MachineData.DaySeconds * _resources.WaterUsage(p.X, p.Z);
            Replant(world, m);
        }

        // The yield (FUN_14029c670, Verified): a rock its own value, at least 0.1; any other building what the ground or the nearest rock gives, and
        // 0 becomes 0.5 for one not the player's (FUN_140546dd0), so only the player's own mines can stand on nothing.
        else if (data.Resource != 0)
        {
            m.Yield = IsNatural(site.Id) ? _resources.NodeYield(data.Resource, p) : _resources.Yield(data.Resource, p);
            if (!IsNatural(site.Id) && m.Yield == 0 && site.Owner != world.Player.Faction) m.Yield = 0.5f;
        }

        return m;
    }

    /// <summary>
    /// Machines follow the sites: a complete player site gets one, a gone site loses its own (items dropped where it stood).
    /// </summary>
    void Sync(World world)
    {
        var reg = world.Buildings;
        foreach (var site in reg.All)
        {
            if (site.Complete && site.Owner == world.Player.Faction && site.WallParent is null && world.Machines.Find(site.Id) is null
                && Data(site.Record) is not null)
                Ensure(world, site);
        }

        if (world.Machines.Count == 0) return;
        List<(Machine, BuildSite)>? swapped = null;
        foreach (var m in world.Machines.All)
            if (Site(world, m.Site) is { } s && s.Record != m.Data.Id && !IsNatural(s.Id)) (swapped ??= []).Add((m, s));
        if (swapped is not null)
            foreach (var (m, s) in swapped) Rebuild(world, m, s);
        List<Machine>? gone = null;
        foreach (var m in world.Machines.All)
            if (Site(world, m.Site) is null && !RockAwaited(m.Site)) (gone ??= []).Add(m);
        if (gone is null) return;
        foreach (var m in gone)
        {
            DropAll(world, m);
            world.Machines.Remove(m.Site);
            _sweep = true;
        }
    }

    /// <summary>
    /// The machine of a building upgraded or downgraded in place (docs/game/buildings-production.md "Upgrades"; engine reading: the original keeps
    /// the one object and its inventory) is made anew for its new record and keeps what the old one held: input and output stock of the same items,
    /// the storage's items (what no longer fits drops at the building), a farm's growth state (plants by index; the new layout's extra plants as
    /// replanted), a bench's queue and finished items, and the power settings. The rest drops on the ground.
    /// </summary>
    void Rebuild(World world, Machine old, BuildSite site)
    {
        if (Create(world, site) is not { } m)
        {
            DropAll(world, old);
            world.Machines.Remove(old.Site);
            _sweep = true;
            return;
        }

        var at = old.Position;
        void Drop(ItemInstance? item) { if (item is not null) world.GroundItems.Drop(item, at, 0); }
        foreach (var i in old.Inputs)
        {
            if (i.Count <= 0) continue;
            if (Array.Find(m.Inputs, n => n.Item == i.Item) is { } into) { into.Count += i.Count; into.Level = Math.Max(into.Level, i.Level); }
            else Drop(_items.Create(i.Item, i.Count));
        }

        if (old.Output is { Count: > 0 } o)
        {
            if (m.Output is { } no && no.Recipe.Item == o.Recipe.Item) { no.Count = o.Count; no.Progress = o.Progress; }
            else Drop(_items.Create(o.Recipe.Item, o.Count));
        }

        if (old.Storage is { } store)
        {
            foreach (var item in store.Items.ToList())
                if (m.Storage is not { } into || !into.Add(item)) Drop(item);
        }

        if (old.Farm is { } of && m.Farm is { } nf)
        {
            for (int i = 0; i < Math.Min(of.Plants.Length, nf.Plants.Length); i++) nf.Plants[i] = of.Plants[i];
            (nf.Growth, nf.Death, nf.Start, nf.Harvested, nf.HarvestProgress, nf.ClearProgress, nf.Status, nf.Plantings, nf.Rolls)
                = (of.Growth, of.Death, of.Start, Math.Min(of.Harvested, nf.Plants.Length), of.HarvestProgress, of.ClearProgress, of.Status,
                    of.Plantings, of.Rolls);
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
        _sweep = true;
    }

    /// <summary>
    /// Puts what a removed machine held on the ground at its last position (engine choice: the original's dismantle leaves items too, the details are
    /// Unknown).
    /// </summary>
    void DropAll(World world, Machine m)
    {
        var at = m.Position;
        void Drop(ItemInstance? item) { if (item is not null) world.GroundItems.Drop(item, at, 0); }
        foreach (var i in m.Inputs) if (i.Count > 0) Drop(_items.Create(i.Item, i.Count));
        if (m.Output is { Count: > 0 } o) Drop(_items.Create(o.Recipe.Item, o.Count));
        if (m.Storage is { } s) foreach (var item in s.Items.ToList()) Drop(item);
        if (m.Craft is { } c) foreach (var item in c.Done) Drop(item);
    }

    #endregion

    #region Orders and the operators' walk

    /// <summary>
    /// The gunner spots of the player's turrets, as their walks found them, with each turret's <c>use range</c> (derived from the site and the
    /// ground; not saved, not hashed).
    /// </summary>
    readonly Dictionary<string, (Vector3 Spot, float Range)> _turretSpots = [];

    /// <summary>
    /// The clip a turret's gunner holds.
    /// </summary>
    public const string TurretClip = "combatstance";

    /// <summary>
    /// The characters an order names, else the player's selection.
    /// </summary>
    static IReadOnlyList<CharacterId> Who(World world, IReadOnlyList<CharacterId> ids) => ids.Count > 0 ? ids : world.Player.Selection.ToArray();

    /// <summary>
    /// Applies the player's commands: operating, turret and prospecting orders, the job lists, putting into and taking out of machines, research,
    /// crafts, power and furnaces, the camera focus.
    /// </summary>
    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        foreach (var command in commands)
        {
            switch (command)
            {
                case OperateOrder order:
                    {
                        var site = Site(world, order.Site) ?? TownMachineSite(world, order.Site);
                        if (site is not { Complete: true } || (Ensure(world, site) is not { } machine)) break;
                        var table = world.Characters;

                        // On a storage it is one haul trip there (order 124 without Shift, FUN_1407fa650's one-shot dispatch), no job.
                        if (machine.Data.IsStorage && !machine.Data.Operable)
                        {
                            foreach (var id in Who(world, order.Characters))
                            {
                                if (table.TryResolveNext(id, out int hauler))
                                    PlayerOrders.Give(world, hauler, new OrderEntry(TaskType.OperateStorage, this) { Site = site.Id }, order.Queued);
                            }

                            break;
                        }

                        if (!(machine.Data.Operable || machine.Data.IsTraining || IsSeat(machine))) break;
                        var row = machine.Data.IsTraining ? TaskType.UseTrainingDummy : TaskType.OperateMachinery;
                        foreach (var id in Who(world, order.Characters))
                        {
                            if (table.TryResolveNext(id, out int slot)
                                && PlayerOrders.Give(world, slot, new OrderEntry(row, this) { Site = site.Id }, order.Queued) is { Posted: true })
                                break;   // the machine is in use: said once
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
                case FocusCommand look: _focus = look.Position; break;
                case QueueResearch queue: Queue(world, queue.Research); break;
                case CancelResearch cancel: world.Research.Dequeue(cancel.Research); break;
                case QueueCraft craft: QueueCraft(world, craft); break;
                case CancelCraft cancel: CancelCraft(world, cancel); break;
                case RepeatCrafts repeat:
                    if (world.Machines.Find(repeat.Site)?.Craft is { } state)
                    {
                        state.Repeat = repeat.Repeat;
                        world.Machines.Touch();
                    }

                    break;
                case SetMachinePower set when world.Machines.Find(set.Site) is { } powered:
                    if (set.PowerOn is { } on) powered.PowerOn = on;
                    if (set.GetsBattery is { } battery && powered.Data.IsConsumer) powered.GetsBattery = battery;
                    world.Machines.Touch();
                    break;
                case IncinerateFurnace burn when world.Machines.Find(burn.Site) is { Data.IsFurnace: true } furnace
                    && Site(world, burn.Site) is { Complete: true } lit && lit.Owner == world.Player.Faction:
                    Incinerate(furnace);
                    world.Machines.Touch();
                    break;
            }
        }
    }

    /// <summary>
    /// The nearest able one of the order's characters mans the turret, the player's or a town's (<see cref="TownMachineSite"/>; the original's
    /// <c>Task_UseTurret</c>): it operates it, walking to its gunner spot (engine choice: one gunner, the original's pick among several is Unknown);
    /// someone else at it already: "is already in use".
    /// </summary>
    void UseTurret(World world, UseTurretOrder order)
    {
        var target = Site(world, order.Site) ?? TownMachineSite(world, order.Site);
        if (target is not { Complete: true } site || TurretPostOf(site) is not { } post || Ensure(world, site) is not { } m) return;
        var spot = AiSystem.TurretSpot(world, post);
        var table = world.Characters;
        int best = -1;
        float bestGap = float.MaxValue;
        foreach (var id in Who(world, order.Characters))
        {
            if (table.TryResolveNext(id, out int slot) && table.Cold(slot) is { IsPlayer: true } cold && Able(cold)
                && TaskStart.Eligible(world, cold, TaskType.UseTurret) && Gap(table.Next[slot].Position, spot) is var gap && gap < bestGap)
                (best, bestGap) = (slot, gap);
        }

        if (best >= 0) PlayerOrders.Give(world, best, new OrderEntry(TaskType.UseTurret, this) { Site = site.Id }, order.Queued);
    }

    /// <summary>
    /// The orders of machines (operate 87, training 97, turret gunner 146, a haul trip to a storage 124) and prospecting (150): a player character's
    /// (else the order leaves the queue) on a complete site that has its machine (or a turret's post); one already working that machine needs nothing
    /// more; the character must be able to work (<see cref="Able"/>), the machine have a free operator place ("is already in use", posted) and, to
    /// operate or man it (rows 87 and 146), power (state 93, <see cref="Powered"/>; its text "It needs electricity." said); a prospector must not swim.
    /// </summary>
    OrderCheck IOrderOwner.CanRun(World world, int slot, OrderEntry order)
    {
        var cold = world.Characters.Cold(slot)!;
        if (!cold.IsPlayer) return OrderCheck.Gone;
        if (order.Row == TaskType.Prospecting) return Able(cold) && cold.WaterState != 3 ? OrderCheck.Run : OrderCheck.Blocked();
        if (order.Site is not { } id || Site(world, id) is not { Complete: true } site || world.Machines.Find(id) is not { } m
            || order.Row == TaskType.UseTurret && TurretPostOf(site) is null) return OrderCheck.Gone;
        if (order.Row == TaskType.OperateStorage) return Able(cold) ? OrderCheck.Run : OrderCheck.Blocked();
        if (cold.OperateSite == id) return OrderCheck.Run;
        if (!Able(cold)) return OrderCheck.Blocked();
        if (IsSeat(m) && SeatOccupied(world, site)) return OrderCheck.Blocked($"{site.Name} is already in use.", posted: true);
        if (OperatorsOf(world, id) >= m.Data.MaxOperators) return OrderCheck.Blocked($"{site.Name} is already in use.", posted: true);
        return Unpowered(world, order, site, m) ? OrderCheck.Blocked(Loc.Tr("It needs electricity.")) : OrderCheck.Run;
    }

    /// <summary>
    /// A running operate or turret order stops when its machine loses power (state 93, <see cref="Unpowered"/>).
    /// </summary>
    bool IOrderOwner.StillHolds(World world, int slot, OrderEntry order) =>
        order.Site is not { } id || Site(world, id) is not { } site || world.Machines.Find(id) is not { } m || !Unpowered(world, order, site, m);

    /// <summary>
    /// Whether the order operates or mans a machine (rows 87 and 146, which need state 93) that has no power (<see cref="Powered"/>).
    /// </summary>
    bool Unpowered(World world, OrderEntry order, BuildSite site, Machine m) =>
        order.Row is TaskType.OperateMachinery or TaskType.UseTurret && !Powered(world, site, m);

    /// <summary>
    /// A loaded order's machine is made as the live order makes it (<see cref="Inputs"/>): a rock's, a town dummy's or a seat's is not saved, and
    /// without it <see cref="IOrderOwner.CanRun"/> drops the order (#598).
    /// </summary>
    void IOrderOwner.Restored(World world, OrderEntry order)
    {
        if (order.Site is { } id && Site(world, id) is { Complete: true } site) Ensure(world, site);
    }

    /// <summary>
    /// Starts the order's task (prospecting, a storage haul, operating a machine).
    /// </summary>
    void IOrderOwner.Start(World world, int slot, OrderEntry order)
    {
        var table = world.Characters;
        switch (order.Row)
        {
            case TaskType.Prospecting:
                // one at it keeps its progress
                if (table.Next[slot].Task != (byte)CharacterTask.Prospect)
                    TaskStart.Start(world, slot, new TaskRequest(CharacterTask.Prospect, TaskType.Prospecting, TaskSource.Order));

                break;
            case TaskType.OperateStorage:
                TaskStart.Start(world, slot, new TaskRequest(CharacterTask.Haul, TaskType.OperateStorage, TaskSource.Order) { Site = order.Site });
                break;
            default:
                if (table.Cold(slot)!.OperateSite != order.Site)
                    TaskStart.Start(world, slot, new TaskRequest(CharacterTask.Operate, order.Row, TaskSource.Order) { Site = order.Site });

                break;
        }
    }

    /// <summary>
    /// A player turret's post (its functionality's <c>use range</c> and <c>gun turret</c>), null for a building that is no turret.
    /// </summary>
    Meitou.Data.World.TurretPost? TurretPostOf(BuildSite site)
    {
        if (_db.Find(site.Record) is not { } record
            || (record.GetReferences("functionality") is [var link, ..] ? _db.Find(link.TargetStringId) : null) is not { } fn
            || fn.GetInt("function") != Meitou.Data.World.ObjectRanges.TurretFunction) return null;
        var forward = Vector3.Transform(Vector3.UnitZ, site.Rotation);
        return new Meitou.Data.World.TurretPost(site.Position, MathF.Atan2(forward.X, forward.Z), fn.GetFloat("use range", 10),
            record.GetReferences("gun turret").Select(r => r.TargetStringId).FirstOrDefault() ?? "", null);
    }

    /// <summary>
    /// Where an operator walks to: a turret's gunner spot (<see cref="Ai.AiSystem.TurretSpot"/>), else the floor point at or next to the building's
    /// place (furniture cuts a hole in the floor's navmesh; <see cref="MovementSystem.GoToFloor"/>).
    /// </summary>
    Vector2 GoalOf(World world, BuildSite site, Machine m)
    {
        if (m.Data.Function == Meitou.Data.World.ObjectRanges.TurretFunction && TurretPostOf(site) is { } post)
        {
            var spot = AiSystem.TurretSpot(world, post);
            _turretSpots[site.Id] = (spot, post.UseRange);
            return new Vector2(spot.X, spot.Z);
        }

        var at = world.Walkability.FloorNear(site.Position.X, site.Position.Z, site.Position.Y, MovementSystem.FloorSlack) ?? site.Position;
        return new Vector2(at.X, at.Z);
    }

    /// <summary>
    /// Sends an operator towards <paramref name="goal"/> (<see cref="GoalOf"/>) on the site's own floor (not a turret's, whose spot may stand above
    /// it).
    /// </summary>
    void WalkToSite(ref CharacterHot n, CharacterCold cold, BuildSite site, Vector2 goal)
    {
        n.Goal = goal;
        n.Flags |= (ushort)MoveFlags.NeedPath;
        (cold.GoalHeight, cold.GoalHeightAt) = _turretSpots.ContainsKey(site.Id) ? (float.NaN, default) : (site.Position.Y, n.Goal);
    }

    /// <summary>
    /// A character that can work: awake, not carrying or carried. A fight is no gate: an order's new task ends it
    /// (<see cref="TaskStart.Start"/>; docs/game/combat.md "Fight lifecycle and orders"), and a fight that starts ends the work the same way.
    /// Nor is a cage: no production row registers the imprisoned or chained repertoire (<see cref="TaskStart.Eligible"/>), and a caging ends the
    /// task (<see cref="Locks.FixtureRegistry.Imprison"/>).
    /// </summary>
    static bool Able(CharacterCold cold) =>
        cold.Medical is not { Incapacitated: true } && cold.CarriedBy.IsNone && cold.Carrying.IsNone;

    /// <summary>
    /// <see cref="Able"/> for a list job of <paramref name="type"/>, except that a carrier standing idle with nothing under way (no provenance, no
    /// errand; e.g. after a pick-up order) takes up a follow row (31, 45, 289; their list B has no IS_CARRYING_SOMETHING 47, docs/game/ai-tasks.md).
    /// </summary>
    static bool AbleFor(CharacterCold cold, TaskType type, CharacterTask task, bool onErrand) =>
        cold.Medical is not { Incapacitated: true } && cold.CarriedBy.IsNone
        && (cold.Carrying.IsNone || PermaJobs.Follows(type) && task == CharacterTask.Idle && cold.Running is null && !onErrand);

    /// <summary>
    /// The characters operating the site.
    /// </summary>
    static int OperatorsOf(World world, string site)
    {
        var table = world.Characters;
        int n = 0;
        for (int i = 0; i < table.HighWater; i++)
            if (table.Next[i].Alive && table.Next[i].Task == (byte)CharacterTask.Operate && table.Cold(i)?.OperateSite == site) n++;
        return n;
    }

    /// <summary>
    /// The ground distance between two points.
    /// </summary>
    static float Gap(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X, dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>
    /// Building slot 59 (<c>isIndoors</c>, <b>Verified</b>): a machine whose record is interior furniture and which stands in a building. One rule
    /// for the power tick, a light's work and a farm's crop match and rain.
    /// </summary>
    static bool IsIndoors(MachineData data, BuildSite? site) => data.InteriorFurniture && site?.Parent is not null;

    /// <summary>
    /// Whether the operator is at the machine: a turret's gunner at its spot or within the turret's use range, a rock's within its footprint or use
    /// range, else within reach of the site; a gunner also at its spot's height, a machine's operator at the site's.
    /// </summary>
    bool AtSite(World world, in CharacterHot n, BuildSite site, Machine m)
    {
        // A turret's gunner walks to its spot (once its walk has found it) and mans the turret from within its use range of the turret, doubled when
        // stopped, as any machine (AI::isWithinChoppingRange has no settle slack; a 150 slack let gunners man turrets from 15 m off, #580). The height
        // is the spot's, which may stand above the turret's origin.
        if (_turretSpots.TryGetValue(site.Id, out var turret))
        {
            bool near = Gap(n.Position, turret.Spot) <= AiSystem.TurretSlack || SiteReach.Within(n, Gap(n.Position, site.Position), turret.Range);
            return near && MathF.Abs(n.Position.Y - turret.Spot.Y) < SiteReach.HeightLimit(door: false, site.Parent is not null);
        }

        // A rock: inside its footprint scaled 1.2, or within its use range of the box's centre (the original measures to its position marker; a
        // rock's origin can lie off its pile).
        if (_naturalBounds.TryGetValue(site.Id, out var rock))
        {
            return rock.Touches(n.Position)
                || SiteReach.Within(n, Vector2.Distance(new Vector2(n.Position.X, n.Position.Z), rock.Flat), m.Data.UseRange);
        }

        // The machine's use range (UseableStuff::getReachRange).
        if (!SiteReach.Within(n, GapTo(site, n.Position), m.Data.UseRange)) return false;
        return SiteReach.AtHeight(n, site, world.Fixtures.DoorsByBuilding[site.Id].Any()) || OffFloor(world, n, site);
    }

    /// <summary>
    /// A character stopped within reach of a site whose height has no floor (furniture placed on an upper storey the navmesh lacks, #388: a Hub
    /// house's storages): its walk gets no nearer, so it counts as there (engine choice; the original's floor test passes on the same floor only).
    /// </summary>
    static bool OffFloor(World world, in CharacterHot n, BuildSite site) =>
        (n.Flags & (ushort)MoveFlags.AnyPath) == 0
        && world.Walkability.FloorNear(site.Position.X, site.Position.Z, site.Position.Y, MovementSystem.FloorSlack) is null;

    /// <summary>
    /// The X/Z gap from <paramref name="from"/> to a site (<see cref="SiteReach.Gap"/>). A rock's BUILDING footprint is not scaled like the rock, so
    /// a rock is measured to its mesh's footprint as placed (engine choice for the original's 1.2 x bounds box of a foliage building, which also
    /// wants a movement flag; a circle of its longest half-extent left miners standing off a long rock's side), else to its position.
    /// </summary>
    float GapTo(BuildSite site, Vector3 from)
    {
        if (!IsNatural(site.Id)) return SiteReach.Gap(Settings.EdgeGap, site, from);
        return _naturalBounds.TryGetValue(site.Id, out var rock) ? rock.Gap(from) : SiteReach.Gap(null, site, from);
    }

    /// <summary>
    /// The yaw from <paramref name="n"/> to a site: a rock's bounds centre (engine choice, as <see cref="GapTo"/>: a rock's origin can lie off its
    /// pile, so facing it turned miners away), else its position.
    /// </summary>
    float FacingTo(in CharacterHot n, BuildSite site)
    {
        var at = _naturalBounds.TryGetValue(site.Id, out var rock) ? rock.Flat : new Vector2(site.Position.X, site.Position.Z);
        return MathF.Atan2(at.X - n.Position.X, at.Y - n.Position.Z);
    }

    /// <summary>
    /// Registers the operate, haul and prospect tasks with the task step.
    /// </summary>
    public void RegisterTasks(TaskHandlers tasks)
    {
        tasks.Register(CharacterTask.Operate, this);
        tasks.Register(CharacterTask.Haul, this);
        tasks.Register(CharacterTask.Prospect, this);
    }

    /// <summary>
    /// Operating <see cref="TaskRequest.Site"/> (rows 87, 97, 146, the jobs 152 and 234, the implicit sitting 275), a haul trip to the storage
    /// <see cref="TaskRequest.Site"/> (124, the implicit ditching 242), or prospecting where the character stands (150).
    /// </summary>
    void ITaskHandler.Start(World world, int slot, in TaskRequest request)
    {
        if (request.Task == CharacterTask.Prospect)
        {
            ProspectStart(world, slot);
            return;
        }

        var site = Site(world, request.Site!)!;
        var m = Ensure(world, site)!;
        if (request.Task == CharacterTask.Haul) StartHaulTrip(world, slot, site, m, ditch: request.Row == TaskType.DitchAllResources);
        else Begin(world, slot, site, m);
    }

    /// <summary>
    /// An operator, hauler or prospector that another task replaces or the Stop order ends lets go of its machine, trip or progress (the task it goes
    /// back to is the new start's).
    /// </summary>
    void ITaskHandler.End(World world, int slot, CharacterTask task, TaskEnd reason)
    {
        var cold = world.Characters.Cold(slot)!;
        if (task == CharacterTask.Prospect)
        {
            cold.Prospecting = null;
            return;
        }

        cold.OperateSite = null;
        cold.HaulSite = null;
        cold.HaulFetch = null;
        cold.Operating = false;
        cold.HaulTo = null;
    }

    /// <summary>
    /// Starts the operate task at the site: the character stands, takes the task and walks to the machine.
    /// </summary>
    void Begin(World world, int slot, BuildSite site, Machine m)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        ref var n = ref table.Next[slot];
        TaskStart.Stand(table, slot);
        n.Task = (byte)CharacterTask.Operate;
        n.Mode = SpeedMode.Free;
        n.TaskTime = 0;
        cold.OperateSite = site.Id;

        // A gunner holds "combatstance" (Verified, Task_UseTurret FUN_1403474f0), not its functionality's clip.
        cold.OperateClip = m.Data.Function == Meitou.Data.World.ObjectRanges.TurretFunction ? TurretClip : m.Data.Animation;
        cold.OperateTool = m.Data.Tool;
        cold.OperateFunctionality = m.Data.Function == Meitou.Data.World.ObjectRanges.TurretFunction ? null : m.Data.Functionality;
        cold.OperateHungerRate = m.Data.HungerRate;
        cold.HaulSite = null;
        cold.HaulFetch = null;
        cold.Operating = false;
        cold.HaulTo = null;
        var goal = GoalOf(world, site, m);
        if (!AtSite(world, n, site, m)) WalkToSite(ref n, cold, site, goal);
        _sweep = true;
    }

    /// <summary>
    /// Ends the task of a job, an operator's or a haul trip by itself (serial phases; <see cref="TaskEnd.Done"/>): the character stands idle and the
    /// decision step decides it again. A follow job's follow ends with it (its <see cref="CharacterCold.FollowOrder"/> is dropped).
    /// </summary>
    static void End(World world, int slot)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        var task = (CharacterTask)table.Next[slot].Task;
        bool followJob = PermaJobs.ListJob(cold, task) is { } job && PermaJobs.Follows(job.Row);
        if (followJob) cold.FollowOrder = CharacterId.None;
        if (followJob || task is CharacterTask.Operate or CharacterTask.Haul or CharacterTask.Sleep) TaskStart.End(world, slot, TaskEnd.Done);
        cold.OperateSite = null;
        cold.HaulSite = null;
        cold.HaulFetch = null;
        cold.Operating = false;
        cold.HaulTo = null;
    }

    /// <summary>
    /// The per-tick walk of the operators to their machines (the job ends for one whose machine is gone).
    /// </summary>
    public void Schedule(World world)
    {
        // The jobs are the decision step's (RegisterSources); with no machine a sleeper of the "Sleep when injured" option still lets go of its bed
        // here once up.
        if (world.Machines.Count == 0 && !_sweep && !(Beds is not null && (world.Player.SquadAi & SquadAi.Sleep) != 0 && world.Fixtures.Count > 0))
            return;

        var table = world.Characters;
        float dt = world.TickSeconds;
        for (int i = 0; i < table.HighWater; i++)
        {
            ref var n = ref table.Next[i];
            if (!n.Alive) continue;
            var cold = table.Cold(i)!;
            if (n.Task == (byte)CharacterTask.Haul)
            {
                if (cold.HaulSite is { } to && Site(world, to) is { Complete: true } leg && world.Machines.Find(to) is { } legMachine && Able(cold))
                    Approach(world, ref n, cold, leg, legMachine, dt);
                else End(world, i);
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
                End(world, i);
                continue;
            }

            // A hauling operator walks to its storage instead.
            if (Hauling(world, cold) is { } haul) (site, m) = haul;
            Approach(world, ref n, cold, site, m, dt);
        }

        if (world.Machines.Count == 0) _sweep = false;
    }

    /// <summary>
    /// Walks a character to a building: at it, its path is dropped; else a path is asked for, again every <see cref="PursuitSystem.RepathSeconds"/>
    /// while it has none.
    /// </summary>
    void Approach(World world, ref CharacterHot n, CharacterCold cold, BuildSite site, Machine m, float dt)
    {
        const ushort path = (ushort)MoveFlags.AnyPath;
        if (m.Data.Function == Meitou.Data.World.ObjectRanges.TurretFunction && !_turretSpots.ContainsKey(site.Id)) GoalOf(world, site, m);
        if (AtSite(world, n, site, m))
        {
            if ((n.Flags & path) != 0)
            {
                n.Flags = (ushort)(n.Flags & ~path);
                n.PathCursor = 0;
                cold.PathRequest = 0;
            }

            n.TaskTime = 0;
            return;
        }

        if ((n.Flags & path) != 0) return;
        n.TaskTime -= dt;
        if (n.TaskTime > 0) return;
        WalkToSite(ref n, cold, site, GoalOf(world, site, m));
        n.Mode = SpeedMode.Free;
        n.TaskTime = PursuitSystem.RepathSeconds;
    }

    #endregion

    #region The tick

    /// <summary>
    /// The number of the nearest town of <paramref name="cold"/>'s faction whose radius holds <paramref name="at"/>, 0 for none. A method of its own:
    /// lambdas capturing the operator in the loop over every character made a closure per character each tick.
    /// </summary>
    static int NearestOwnTown(World world, CharacterCold cold, Vector2 at)
    {
        int best = 0;
        float bestD2 = float.MaxValue;
        foreach (var t in world.Buildings.Towns)
        {
            var centre = new Vector2(t.Position.X, t.Position.Z);
            float d2 = Vector2.DistanceSquared(centre, at);
            if (t.Owner != cold.Faction || d2 > t.Radius * t.Radius || d2 >= bestD2) continue;
            (best, bestD2) = (t.Number, d2);
        }

        return best;
    }

    /// <summary>
    /// The serial step: prospecting, the sync and the errands, the operator counts and the research bench, the town workers; then the farms, the
    /// machines' own work and every operator's step, the power, and the touches.
    /// </summary>
    public void SlowWorld(World world)
    {
        ProspectStep(world);
        Sync(world);
        Errands(world);
        var research = world.Research;
        research.Rate = 0;
        int bench = ResetOperators(world);
        TownFarms(world);
        TownWorkers(world, world.TickSeconds);

        // Raise-only (FUN_140834390): losing the best bench does not lower the level; it starts at 0 with the research state and on a load.
        if (bench > research.BenchLevel)
        {
            research.BenchLevel = bench;
            _publishedResearch = null;
        }

        if (world.Machines.Count == 0) return;

        float dt = world.TickSeconds;
        FarmsStep(world, dt);
        ClearBlocked(world);
        UnattendedStep(world, dt);
        int contributions = OperatorsStep(world, dt);
        PowerTick(world, dt);
        if (research.Queue.Count > 0 && contributions > 0) research.Touch();
        world.Machines.Touch();
    }

    /// <summary>
    /// Moves each machine's operator count to last tick's and clears it; returns the best tech level of a complete research bench of the player.
    /// </summary>
    int ResetOperators(World world)
    {
        int bench = 0;
        foreach (var m in world.Machines.All)
        {
            m.LastOperators = m.Operators;
            m.Operators = 0;
            if (m.Data.IsResearch && CanResearch(world, m))
                bench = Math.Max(bench, m.Data.TechLevel);
        }

        return bench;
    }

    /// <summary>
    /// Each live farm of a complete building grows; one back in a loaded zone after over an hour first catches up.
    /// </summary>
    void FarmsStep(World world, float dt)
    {
        foreach (var m in world.Machines.All)
        {
            if (m.Farm is not { } farm || Site(world, m.Site) is not { Complete: true } site || !Live(world, m)) continue;

            // A farm back in a loaded zone after over an hour catches up (Verified, FarmBuilding::loadFromSerialise FUN_1400e3de0).
            if (!UnloadedProduction && world.GameHours - farm.LiveHours > 1)
                TimeSkip(world, m, (float)(world.GameHours - farm.LiveHours), site.Owner != world.Player.Faction);

            farm.LiveHours = world.GameHours;
            FarmTick(world, m, dt);
        }
    }

    /// <summary>
    /// A machine that takes operators clears its blocked status by itself (Verified, ProductionBuilding::vfunc_28 FUN_14029ad90, when +0x3a9 is
    /// set): output full (1) goes back to 2 once the output has room, nothing to work with (0) once no input is empty. An operator's step below
    /// sets it again the same tick if it is still blocked.
    /// </summary>
    void ClearBlocked(World world)
    {
        foreach (var m in world.Machines.All)
        {
            if (!m.Data.IsProducer || m.Data.Unattended || m.Output is not { } held || Site(world, m.Site) is not { Complete: true } || !Live(world, m))
                continue;

            if (m.Status == 1 && !held.Full) m.Status = 2;
            if (m.Status == 0 && !m.HasEmptyInputs) m.Status = 2;
        }
    }

    /// <summary>
    /// An unattended machine (max operators 0, or both records negative) works with nobody at it, at skill factor 1 (Verified,
    /// ProductionBuilding::vfunc_28 calls slot 150 with no operator when +0x3a9 is clear); a furnace burns and its metal is kept current.
    /// </summary>
    void UnattendedStep(World world, float dt)
    {
        foreach (var m in world.Machines.All)
        {
            if (m.Data.Unattended && m.Data.IsProducer && Site(world, m.Site) is { Complete: true } && Live(world, m))
            {
                ProduceStep(world, m, 1, dt);
            }
            else if (m.Data.IsFurnace && m.Storage is { } grid)
            {
                if (m.Burning && Site(world, m.Site) is { Complete: true } && Live(world, m)) FurnaceStep(world, m, dt);

                float metal = FurnaceMetal(grid);
                if (metal != m.Metal)
                {
                    m.Metal = metal;
                    world.Machines.Touch();
                }
            }
        }
    }

    /// <summary>
    /// Every hauler's and operator's step, in slot order; returns the research contributions made.
    /// </summary>
    int OperatorsStep(World world, float dt)
    {
        var table = world.Characters;
        int contributions = 0;
        for (int i = 0; i < table.HighWater; i++)
        {
            ref var n = ref table.Next[i];
            if (!n.Alive) continue;
            if (n.Task == (byte)CharacterTask.Haul)
            {
                HaulStep(world, i);
                continue;
            }

            if (n.Task == (byte)CharacterTask.Operate) Operate(world, i, ref n, dt, ref contributions);
        }

        return contributions;
    }

    /// <summary>
    /// One operator's step: the job ends without its machine or when it can't work; a haul under way goes on; at the machine, a rock becomes the
    /// faction's, then the turret, dummy, seat or automatic machine is used, else the machine is worked.
    /// </summary>
    void Operate(World world, int i, ref CharacterHot n, float dt, ref int contributions)
    {
        var cold = world.Characters.Cold(i)!;
        if (cold.OperateSite is not { } id || Site(world, id) is not { Complete: true } site || world.Machines.Find(id) is not { } m || !Able(cold))
        {
            End(world, i);
            return;
        }

        cold.Operating = false;
        if (Hauling(world, cold) is { } haul)
        {
            if (AtSite(world, n, haul.Site, haul.Machine)) HaulAt(world, cold, m, haul.Machine);
            return;
        }

        if (!AtSite(world, n, site, m)) return;

        // A rock (an "is foliage" building) becomes the operator's faction's once a player character works it, and joins a player town whose area
        // holds it (Task_OperateMachine slot 1, FUN_14035b430; the town by its radius here).
        if (cold.Faction == world.Player.Faction && site.Owner != cold.Faction && IsNatural(site.Id))
        {
            site.Owner = cold.Faction;
            site.Town = NearestOwnTown(world, cold, new Vector2(site.Position.X, site.Position.Z));
        }

        if (UseSpecial(world, i, ref n, cold, site, m, dt)) return;
        if (Settings.AutoHaul && StartHaul(world, cold, site, m)) return;

        WorkMachine(world, i, ref n, cold, site, m, dt, ref contributions);
    }

    /// <summary>
    /// The use of a machine that is not worked for output (a turret, a training dummy, a seat, an automatic machine); false for any other.
    /// </summary>
    bool UseSpecial(World world, int i, ref CharacterHot n, CharacterCold cold, BuildSite site, Machine m, float dt)
    {
        // A turret: manned, facing it; the combat system fires it (docs/simulation.md "Combat", turrets). Its gunner counts as an operator, so it
        // draws its power.
        if (m.Data.Function == Meitou.Data.World.ObjectRanges.TurretFunction)
        {
            // A broken turret makes manning impossible (Task_UseTurret FUN_1403474f0 -> AITaskSytem::taskImpossible): the job ends.
            if (m.Broken)
            {
                End(world, i);
                return true;
            }

            if (n.Velocity.X == 0 && n.Velocity.Z == 0) n.Yaw = FacingTo(n, site);
            m.Operators++;
            cold.Operating = true;
            cold.UseSkill(StatsEnumerated.Turrets, world.Tick);
            return true;
        }

        // A training dummy: the user trains the building's stat (docs/game/ai-tasks.md "Finders of the jobs").
        if (m.Data.IsTraining)
        {
            cold.UseSkill((StatsEnumerated)m.Data.Stat, world.Tick);
            Train(ref n, cold, site, m, dt);
            return true;
        }

        // A chair or throne (an order or an implicit job): sat on, no work; its stat used trains as at any machine (mods' gym seats).
        if (!m.Data.Operable)
        {
            if (IsSeat(m) && SeatOccupied(world, site))
            {
                End(world, i);
                return true;
            }

            Settle(world, ref n, cold, site, m);
            OperatingXp(world, cold, m.Data.Stat, dt);
            return true;
        }

        // An automatic machine (the 152 job): its hauler fills its inputs and carries its output away; the machine works by itself.
        if (!m.Data.IsAutomatic) return false;

        if (!StartHaul(world, cold, site, m) && n.Velocity.X == 0 && n.Velocity.Z == 0) n.Yaw = FacingTo(n, site);
        return true;
    }

    /// <summary>
    /// Works the machine at the operator's skill: research, crafting, a farm's harvest or tending, else production; then the operating XP.
    /// </summary>
    void WorkMachine(World world, int i, ref CharacterHot n, CharacterCold cold, BuildSite site, Machine m, float dt, ref int contributions)
    {
        if (n.Velocity.X == 0 && n.Velocity.Z == 0) n.Yaw = FacingTo(n, site);
        m.Operators++;
        var stat = (StatsEnumerated)m.Data.Stat;
        float skill = m.Data.Stat > 0 ? Effective(cold, stat) : -1;

        // No clamp on s: a stat above 100 lifts it past 1.4 (Verified, FUN_1408852a0 hands the lerp straight to the work slot in FUN_14035bc90).
        float s = skill < 0 ? 1 : Lerp(0.5f, 1.4f, skill / 100);
        bool worked;
        if (m.Data.IsResearch)
        {
            worked = ResearchStep(world, m, s, dt, ref contributions);
        }
        else if (m.Data.IsCrafting)
        {
            m.Smith = world.Characters.IdOf(i);
            worked = CraftStep(world, m, skill, s, dt);
        }
        else if (m.Farm is not null)
        {
            // A growing crop with water is tended: the operator plays the farm's clip (engine reading of task 87's start conditions,
            // docs/game/buildings-production.md "Farming").
            worked = HarvestStep(world, m, skill, s, dt) || Tended(m);
        }
        else
        {
            worked = ProduceStep(world, m, s, dt);
        }

        cold.Operating = worked;
        OperatingXp(world, cold, m.Data.Stat, dt);
    }

    /// <summary>
    /// Operating trains <paramref name="stat"/> (<c>stat used</c>; none at 0) every frame at the machine: <c>Task_OperateMachine</c>'s update
    /// (<c>FUN_14035bc90</c>, <b>Verified</b>) calls <c>FUN_1408c7250</c> for every function but BF_TRAINING, seats included (<b>Observed</b>
    /// amounts, <see cref="OperateXp"/>, through the continuous entry).
    /// </summary>
    void OperatingXp(World world, CharacterCold cold, int stat, float dt)
    {
        if (stat <= 0) return;
        cold.UseSkill((StatsEnumerated)stat, world.Tick);
        if (cold.Stats is { } stats && cold.Race is { } race) _xp.Continuous(stats, race, (StatsEnumerated)stat, dt, OperateXp(stat));
    }

    /// <summary>
    /// The operating XP amount of a stat (<b>Observed</b>, <c>FUN_1408c7250</c>): 0.5, stats 10 and 37 0.1, 11 1, 24 3, 35 1.2 (0.8 falling into the
    /// farming case), 12 (farming) 0.75.
    /// </summary>
    static float OperateXp(int stat) => stat switch { 10 or 37 => 0.1f, 11 => 1, 24 => 3, 35 => 1.2f, 12 => 0.75f, _ => 0.5f };

    /// <summary>
    /// The stat's level, with the medical multiplier.
    /// </summary>
    static float Effective(CharacterCold cold, StatsEnumerated stat) => (cold.Stats?[stat] ?? 0) * (cold.Medical?.StatMultiplier(stat) ?? 1);

    /// <summary>
    /// The linear blend of <paramref name="a"/> and <paramref name="b"/> at <paramref name="t"/>.
    /// </summary>
    static float Lerp(float a, float b, float t) => a + (b - a) * t;

    #endregion

    #region Production

    /// <summary>
    /// One operator's work on a producer (<b>Observed</b>, <c>FUN_140298700</c>): <c>yield x rate x dt x P x s x option</c>, clamped to what the
    /// inputs hold and the output has room for (<c>vfunc_185</c>); the inputs are drawn and each 0.999 of progress makes one output item.
    /// </summary>
    bool ProduceStep(World world, Machine m, float s, float dt)
    {
        if (m.Output is not { } output) return false;
        float amount = m.Yield * m.Data.Rate * WorkFactor(world, m) * dt * _constants.ProductionSpeed * s * Settings.ProductionOption;
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

    #endregion

    #region Farms

    /// <summary>
    /// The factor of the rain's water (the exe's constant at <c>14168a254</c>, <b>Verified</b>).
    /// </summary>
    const float RainWaterFactor = 3;

    /// <summary>
    /// Replants (<b>Observed</b>, <c>FUN_1400e48e0</c>): each plant's growth -0.2 x U[0, 1), the farm's the lowest of them, nothing harvested, no
    /// death.
    /// </summary>
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

    /// <summary>
    /// Whether a machine updates: always with <see cref="UnloadedProduction"/>, else only in an active zone (Verified, ZoneManager updates the
    /// buildings of its active zones only).
    /// </summary>
    bool Live(World world, Machine m) =>
        UnloadedProduction || world.ActiveZones is not { } active || active.Contains(Meitou.Data.World.WorldLayout.ZoneOf(m.Position.X, m.Position.Z));

    /// <summary>
    /// A farm's catch-up for <paramref name="h"/> unloaded game hours (<b>Verified</b>, <c>FarmBuilding::timeSkip</c> <c>FUN_1400df6e0</c>,
    /// docs/game/buildings-production.md "Unloaded zones"): the crop advances along its cycle (growth G, harvest H, death D) for the watered hours;
    /// an NPC farm ignores water. A finished cycle only wraps (nothing is harvested, nothing replants); the hours short of water stretch only the
    /// growth phase (G / drought multiplier) and add to the death that was already there.
    /// </summary>
    internal void TimeSkip(World world, Machine m, float h, bool ignoreWater)
    {
        var f = m.Farm!;
        var fd = m.Data.Farm!;
        float g = fd.GrowthTime / MathF.Max(m.Yield, 1e-6f), cycle = g + fd.HarvestTime + fd.DeathTime;
        if (cycle <= 0) return;
        float p = f.Growth < 1
            ? MathF.Max(f.Growth, 0) * g
            : f.Growth < 2 ? g + (f.Growth - 1) * fd.HarvestTime : g + fd.HarvestTime + f.Death * fd.DeathTime;
        float rate = m.WaterRate, water = float.MaxValue;
        foreach (var input in m.Inputs) water = MathF.Min(water, input.Level + input.Count);

        // The watered share mixes the per-second water rate with the skipped game hours and divides by h as well, as the game writes
        // it (FUN_1400df6e0).
        float w = m.Inputs.Length == 0 ? float.MaxValue : rate > 0 ? water / (rate * h) : h;
        float death, growthLength = g;
        if (ignoreWater || h <= w)
        {
            p += h;
            death = 0;
            if (p > cycle)
            {
                f.Harvested = 0;
                p -= MathF.Floor(p / cycle) * cycle;
            }
        }
        else (p, death, growthLength) = DroughtSkip(f, fd, p, g, w, h, cycle);
        float harvest = MathF.Max(fd.HarvestTime, 1e-6f);
        float growth = p < growthLength ? p / growthLength : p < growthLength + fd.HarvestTime ? 1 + (p - growthLength) / harvest : 2;
        if (growth >= 2) death += (p - growthLength - fd.HarvestTime) / MathF.Max(fd.DeathTime, 1e-6f);

        // the plants' own growth is not touched: while below 1 the next tick sets the growth to the lowest plant again (FUN_1400e5580)
        f.Growth = growth;
        f.Death = death;
        if (ignoreWater) return;
        foreach (var input in m.Inputs)
        {
            float need = w * rate * input.Rate - input.Level;
            input.Level = 0;
            if (need <= 0) continue;
            if (input.Count <= need) input.Count = 0;
            else
            {
                float whole = MathF.Floor(need);
                input.Count -= (int)whole;
                input.Level = 1 - (need - whole);
            }
        }
    }

    /// <summary>
    /// The short-of-water branch of <see cref="TimeSkip"/> (<c>FUN_1400df6e0</c>, <b>Verified</b>): the position after the <paramref name="w"/>
    /// watered hours wraps on the normal cycle, the other hours move it unstretched; the death time adds (h - w) / drought death time to the death
    /// already there, and past 1 nothing is harvested, the death keeps its fraction and the position becomes that fraction of the drought death time;
    /// the position then wraps on the drought cycle (growth G / drought multiplier, 1e8 when that is 0). Returns the position, the death and the
    /// growth length.
    /// </summary>
    static (float P, float Death, float GrowthLength) DroughtSkip(FarmState f, FarmData fd, float p, float g, float w, float h, float cycle)
    {
        p += w;
        p -= MathF.Floor(p / cycle) * cycle;
        float growthLength = fd.DroughtMultiplier != 0 ? g / fd.DroughtMultiplier : 1e8f;
        float droughtCycle = growthLength + fd.HarvestTime + fd.DeathTime;
        p += h - w;
        float death = f.Death;
        if (fd.DroughtDeathTime > 0)
        {
            death += (h - w) / fd.DroughtDeathTime;
            if (death > 1)
            {
                f.Harvested = 0;
                death -= MathF.Floor(death);
                p = death * fd.DroughtDeathTime;
            }
        }

        p -= MathF.Floor(p / droughtCycle) * droughtCycle;
        return (p, death, growthLength);
    }

    /// <summary>
    /// How ripe a farm is, 0..1 over its growth (the HUD's figure): the growth while growing, 1 once ripe.
    /// </summary>
    public static float Ripeness(FarmState f) => Math.Clamp(f.Growth, 0, 1);

    /// <summary>
    /// The water draw (<b>Observed</b>, <c>FUN_1400df4c0</c>): each input needs <c>amount</c>; one below it takes an item from its section, or the
    /// draw fails (its level 0).
    /// </summary>
    static bool DrawWater(Machine m, float amount)
    {
        foreach (var input in m.Inputs)
        {
            if ((input.Level < amount || input.Level <= 0) && input.Count == 0)
            {
                input.Level = 0;
                return false;
            }
        }

        foreach (var input in m.Inputs)
        {
            if (input.Level < amount && input.Count > 0) { input.Count--; input.Level += 1; }
            input.Level -= amount;
        }

        return true;
    }

    /// <summary>
    /// The farm tick (<b>Observed</b>, <c>FarmBuilding::vfunc_28</c>), every tick whether anyone is there. A farm not inside a building
    /// (<see cref="Machine.Indoors"/>, slot 59; its own indoor flag, fertility effect 0, is not read) has its first input rained on first: level +=
    /// wetness x water rate x rate x dt x 3, at most 1 (<b>Verified</b>, <c>FUN_1400e5580</c>).
    /// </summary>
    void FarmTick(World world, Machine m, float dt)
    {
        var f = m.Farm!;
        var fd = m.Data.Farm!;
        float h = dt / (MachineData.DaySeconds / 24);
        if (m.Inputs.Length > 0 && !m.Indoors && world.Weather?.At(m.Position.X, m.Position.Z) is { Wetness: > 0 } rain)
            m.Inputs[0].Level = MathF.Min(1, m.Inputs[0].Level + rain.Wetness * m.WaterRate * m.Inputs[0].Rate * dt * RainWaterFactor);

        bool watered = m.Inputs.Length == 0 || DrawWater(m, dt * m.WaterRate);
        float inc = h * m.Yield / fd.GrowthTime;
        if (Rated(world, m) != 0) inc *= m.Health * m.Power;   // slot 109 only for a farm that uses power (FUN_1402976a0)
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
            else if (f.Death > 0 && f.Death < 0.6f) f.Death -= h / fd.DroughtDeathTime;   // no floor: one step can overshoot below 0 (FUN_1400e5580)
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
    /// One operator's work on a farm (<b>Observed</b>, <c>FarmBuilding::vfunc_150</c>): a ripe crop is harvested plant by plant (<c>health x powerSat
    /// x P x dt x s x harvest rate</c>; when the progress completes the output's room is tested, else status 1 keeps the progress; then
    /// ceil(<c>output per plant</c>) rolls against the efficiency and the fraction once), a dead one cleared (<c>clear rate</c>, the same factors;
    /// slot 109, <b>Verified</b> <c>FUN_1400e59e0</c>).
    /// </summary>
    bool HarvestStep(World world, Machine m, float farming, float s, float dt)
    {
        var f = m.Farm!;
        var fd = m.Data.Farm!;
        float work = m.Health * m.Power * _constants.ProductionSpeed * dt * s;
        if (f.Growth >= 1 && f.Death < fd.DeathThreshold && f.Harvested < f.Plants.Length)
        {
            // status 1: the work function does nothing (FUN_1400e59e0)
            if (m.Output is not { } output || f.Status == FarmStatus.OutputFull) return false;
            f.HarvestProgress += work * fd.HarvestRate / MachineData.DaySeconds;
            if (f.HarvestProgress < 1) return true;
            if (output.Full)
            {
                f.Status = FarmStatus.OutputFull;   // the progress is kept; the tick lets go of it once there is room
                return false;
            }

            float skill = farming < 0 ? 0.01f : farming * 0.01f;
            float e = Math.Clamp(Lerp(0.5f, 2, skill), 0, 1) * m.CropMatch * m.Yield;
            ulong key = Rng.StableHash(m.Site);
            int whole = (int)fd.OutputPerPlant;
            int made = 0;

            // ceil(output) rolls against e, then the fraction rolled once against itself (a do-while on a positive output).
            for (float u = 0; u < fd.OutputPerPlant; u++)
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

    /// <summary>
    /// A farm whose crop is still growing and whose every input holds water.
    /// </summary>
    static bool Tended(Machine m) =>
        m.Farm is { Growth: < 1 } f && f.Death < m.Data.Farm!.DeathThreshold && m.Inputs.All(i => i.Rate <= 0 || i.Level + i.Count > 0);

    /// <summary>
    /// <c>FarmBuilding::eat</c> (<c>FUN_1400e5e60</c>, docs/game/ai-tasks.md "Eating crops"): adds <paramref name="amount"/> (the eater's age, 1, x
    /// the game seconds) to the eat accumulator; past 1 a plant is destroyed as a harvest takes it (<see cref="NextPlant"/>) and the accumulator
    /// restarts. False once no plant is left.
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

    /// <summary>
    /// Whether animals may eat a farm (<c>AI::farmHasFood</c> <c>FUN_1408ca350</c>): grown past 0.4, dead under 0.9.
    /// </summary>
    public static bool HasFood(FarmState f) => f.Growth > 0.4f && f.Death < 0.9f && f.Harvested < f.Plants.Length;

    /// <summary>
    /// A plant is done (<b>Observed</b>, <c>FUN_1400e4800</c>): its growth -10, one more harvested.
    /// </summary>
    static void NextPlant(FarmState f)
    {
        if (f.Harvested >= f.Plants.Length) return;
        f.Plants[f.Harvested] = -10;
        f.Harvested++;
    }

    #endregion

    #region Research

    /// <summary>
    /// The research falloff per earlier contribution in the same tick (<b>Observed</b>, <c>FUN_140836b70</c>).
    /// </summary>
    public const float ResearchFalloff = 0.6f;

    /// <summary>
    /// The research facts of each record, by record id (null: not research); derived from the data.
    /// </summary>
    readonly Dictionary<string, ResearchData?> _researchData = new(StringComparer.Ordinal);

    /// <summary>
    /// Every research record's facts (built on first use).
    /// </summary>
    List<ResearchData>? _allResearch;

    /// <summary>
    /// The research view last published (null: build it again).
    /// </summary>
    ResearchSnapshot? _publishedResearch;

    /// <summary>
    /// The research state's and the machines' versions <see cref="_publishedResearch"/> was built at.
    /// </summary>
    long _researchVersion = -1, _machinesVersion = -1;

    /// <summary>
    /// Where the camera looks (the last <see cref="FocusCommand"/>; null: not told, as in headless runs): research costs are counted round it.
    /// Derived from the commands, not saved or hashed.
    /// </summary>
    Vector3? _focus;

    /// <summary>
    /// The focus zone <see cref="_publishedResearch"/> was built for.
    /// </summary>
    Meitou.Data.World.ZoneCoordinate? _researchZone;

    /// <summary>
    /// The research lists' sizes <see cref="_publishedResearch"/> was built with (<see cref="ResearchView"/>).
    /// </summary>
    (int, int, int, int) _researchLists;

    /// <summary>
    /// The tick <see cref="_publishedResearch"/> was built at.
    /// </summary>
    long _researchBuilt;

    /// <summary>
    /// How often the research view is built again while nothing changed, in ticks.
    /// </summary>
    const int ResearchRefreshTicks = 30;

    /// <summary>
    /// The research facts of the record <paramref name="id"/> (null: not research), cached.
    /// </summary>
    ResearchData? Research(string id)
    {
        if (!_researchData.TryGetValue(id, out var data))
            _researchData[id] = data = _db.Find(id) is { Type: FcsRecordType.Research } r ? ResearchData.From(r) : null;

        return data;
    }

    /// <summary>
    /// A complete player-owned bench counts regardless of its town association.
    /// </summary>
    bool CanResearch(World world, Machine machine) => Site(world, machine.Site) is { Complete: true } site &&
        site.Owner == world.Player.Faction;

    /// <summary>
    /// One operator's work at a research bench (<b>Observed</b>, <c>FUN_14029b8d0</c> and <c>FUN_140836b70</c>): <c>rate x s</c>, x 0.6 for each
    /// earlier contribution this tick, adds that x dt seconds to the first queued research, which completes past <c>hours x G/24</c>.
    /// </summary>
    bool ResearchStep(World world, Machine m, float s, float dt, ref int k)
    {
        if (!CanResearch(world, m)) return false;

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

        if (item.Seconds > data.Hours(_constants, Settings.ResearchOption, world.Research.Times(data.Id)) * (MachineData.DaySeconds / 24))
        {
            research.Complete(_db, item.Id);
            _publishedResearch = null;
            world.Post(Loc.Tr("Research complete: {1}", data.Name));
        }

        return true;
    }

    /// <summary>
    /// A blueprint read (<b>Verified</b>, <c>BlueprintItem::learnResearch</c> <c>FUN_1402b65d0</c>): its research's requirements unmet
    /// (<c>checkRequirements</c> without the level or cost test) or the research already finished once posts a message and keeps the blueprint
    /// (false); otherwise the research completes as at a bench and the caller uses the blueprint up.
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

        world.Research.Complete(_db, id);
        _publishedResearch = null;
        world.Post(Loc.Tr("Research complete: {1}", data.Name));
        return true;
    }

    /// <summary>
    /// <see cref="Learn(World, string)"/> for a blueprint item (false for any other item).
    /// </summary>
    public bool Learn(World world, ItemInstance blueprint) => _items.ResearchOf(blueprint) is { } research && Learn(world, research.StringId);

    /// <summary>
    /// The research the player may queue now (<b>Observed</b>, <c>FUN_1408333b0</c>): not blueprint-only, not exhausted (every repetition done) or
    /// queued, <c>level</c> within the tech level, requirements met.
    /// </summary>
    public bool Available(World world, ResearchData r) =>
        !r.BlueprintOnly && !world.Research.Exhausted(_db, r.Id) && !world.Research.IsQueued(r.Id)
            && r.LevelAt(world.Research.Times(r.Id)) <= world.Research.TechLevel && Met(world, r.Requirements, lenient: true);

    /// <summary>
    /// Whether the research's requirements are all met.
    /// </summary>
    bool Met(World world, ResearchData r) => Met(world, r.Requirements, lenient: false);

    /// <summary>
    /// The requirement check (<b>Verified</b>, <c>FUN_140832fa0</c>): a RESEARCH must be finished, any other record unlocked.
    /// <paramref name="lenient"/> (the research list) also passes an unmet one whose own <c>requirements</c> are all met strictly (one level; none
    /// passes); blueprint learning is strict.
    /// </summary>
    bool Met(World world, IEnumerable<string> requirements, bool lenient)
    {
        foreach (var req in requirements)
        {
            var record = _db.Find(req);
            if (record is null) continue;
            if (record.Type == FcsRecordType.Research ? world.Research.IsDone(req) : world.Research.IsUnlocked(req)) continue;
            if (!lenient || !Met(world, record.GetReferences("requirements").Select(l => l.TargetStringId), lenient: false)) return false;
        }

        return true;
    }

    /// <summary>
    /// The zone of the camera focus, null while none was told.
    /// </summary>
    Meitou.Data.World.ZoneCoordinate? FocusZone() => _focus is { } f ? Meitou.Data.World.WorldLayout.ZoneOf(f.X, f.Z) : null;

    /// <summary>
    /// The items of the player's research benches where costs are counted and paid (<b>Verified</b>, <c>FUN_14082e750</c>, <c>FUN_14082e810</c>,
    /// <c>FUN_14082e900</c>, <c>FUN_1409fc450</c>): only the benches (function 3, the player's) in the 3 x 3 zones round the camera focus; without a
    /// focus (headless runs) every bench.
    /// </summary>
    IEnumerable<Inventory> Benches(World world)
    {
        var near = FocusZone();
        foreach (var m in world.Machines.All)
        {
            if (!m.Data.IsResearch || m.Storage is not { } s || Site(world, m.Site) is not { Complete: true } site
                || site.Owner != world.Player.Faction)
                continue;

            if (near is { } z && Meitou.Data.World.WorldLayout.ZoneOf(m.Position.X, m.Position.Z) is var at
                && (Math.Abs(at.X - z.X) > 1 || Math.Abs(at.Y - z.Y) > 1))
                continue;

            yield return s;
        }
    }

    /// <summary>
    /// How many of <paramref name="item"/> the research benches hold.
    /// </summary>
    int CountInBenches(World world, string item)
    {
        int n = 0;
        foreach (var s in Benches(world))
        {
            foreach (var i in s.Items)
                if (i.Record == item) n += i.Quantity;
        }

        return n;
    }

    /// <summary>
    /// Whether the research is paid for, or the benches hold its cost.
    /// </summary>
    bool Affordable(World world, ResearchData r)
    {
        if (world.Research.Paid.Contains(r.Id, StringComparer.Ordinal)) return true;
        foreach (var (item, count) in r.CostAt(world.Research.Times(r.Id)))
            if (CountInBenches(world, item) < count) return false;
        return true;
    }

    /// <summary>
    /// Queues the research when it is available, the bench is good enough and its cost is held; else posts why not.
    /// </summary>
    void Queue(World world, string id)
    {
        if (Research(id) is not { } r || !Available(world, r)) return;
        int level = r.LevelAt(world.Research.Times(id));
        if (level > world.Research.BenchLevel)
        {
            world.Post(world.Research.BenchLevel == 0 ? "Research needs a research bench." : $"{r.Name} needs a level {level} research bench.");
            return;
        }

        if (!Affordable(world, r))
        {
            world.Post($"Not enough materials in the research benches for {r.Name}.");
            return;
        }

        if (!world.Research.Paid.Contains(id, StringComparer.Ordinal))
        {
            foreach (var (item, count) in r.CostAt(world.Research.Times(id)))
            {
                int left = count;
                foreach (var s in Benches(world))
                {
                    foreach (var i in s.Items.Where(i => i.Record == item).ToList())
                    {
                        int take = Math.Min(left, i.Quantity);
                        i.Quantity -= take;
                        if (i.Quantity == 0) s.Items.Remove(i);
                        left -= take;
                        if (left == 0) break;
                    }
                }
            }
        }

        world.Research.Enqueue(id);
        world.Machines.Touch();
    }

    /// <summary>
    /// The research state for the host (rebuilt when it or the machines changed).
    /// </summary>
    public ResearchSnapshot ResearchView(World world)
    {
        var zone = FocusZone();
        if (_publishedResearch is not null && _researchVersion == world.Research.Version && _machinesVersion == world.Machines.Version
            && _researchZone == zone)
            return _publishedResearch;

        // Progress, bench stock and the focus zone (each zone crossed in a flight) move often: those refresh at most once a second; finishing,
        // queueing or paying at once.
        var lists = (world.Research.Completed.Count, world.Research.Queue.Count, world.Research.Paid.Count, world.Research.BenchLevel);
        if (_publishedResearch is not null && _researchLists == lists && (ulong)(world.Tick - _researchBuilt) < ResearchRefreshTicks)
            return _publishedResearch;

        _researchLists = lists;
        _researchBuilt = world.Tick;
        _researchZone = zone;
        _researchVersion = world.Research.Version;
        _machinesVersion = world.Machines.Version;
        _allResearch ??= ResearchData.LoadAll(_db);
        var state = world.Research;
        var queue = state.Queue.Select(q => (q.Id, Research(q.Id)?.Name ?? q.Id,
            Research(q.Id) is { } d
                ? Math.Clamp(q.Seconds / (d.Hours(_constants, Settings.ResearchOption, state.Times(q.Id)) * (MachineData.DaySeconds / 24)), 0, 1)
                : 0)).ToArray();
        var available = _allResearch.Where(r => Available(world, r))
            .Select(r => new ResearchOption(r.Id, r.Name, r.Category, r.LevelAt(state.Times(r.Id)),
                r.Hours(_constants, Settings.ResearchOption, state.Times(r.Id)), Affordable(world, r),
                r.LevelAt(state.Times(r.Id)) > state.BenchLevel,
                state.Partial.TryGetValue(r.Id, out float s)
                    ? Math.Clamp(s / (r.Hours(_constants, Settings.ResearchOption, state.Times(r.Id)) * (MachineData.DaySeconds / 24)), 0, 1)
                    : 0)
            {
                Description = r.Description,
                Paid = state.Paid.Contains(r.Id, StringComparer.Ordinal),
                Cost = state.Paid.Contains(r.Id, StringComparer.Ordinal) ? []
                    : [.. r.CostAt(state.Times(r.Id)).Select(c => (_items.Info(c.Item)?.Name ?? c.Item, c.Count,
                        CountInBenches(world, c.Item) >= c.Count))],
                // The description lists the RESEARCH requirements only (FUN_140495e00 looks them up as type 21).
                Requirements = [.. r.Requirements.Select(_db.Find).Where(q => q is { Type: FcsRecordType.Research })
                    .Select(q => (q!.Name, state.IsDone(q.StringId), state.IsQueued(q.StringId)))],
                Unlocks = [.. r.Unlocks.Select(u => (u.Field, _db.Find(u.Id)?.Name)).Where(u => u.Name is not null).Select(u => (u.Field, u.Name!))],
            }).ToArray();
        return _publishedResearch = new ResearchSnapshot(state.TechLevel, state.BenchLevel, queue, state.Rate, available)
        {
            Finished = state.Completed.Count,
            Known = state.Completed.ToHashSet(StringComparer.Ordinal),
            // Strict, as the blueprint tooltip asks (FUN_1407ab430 calls FUN_140832fa0 with the lenient flag clear, like learning); the research list
            // is the lenient one.
            RequirementsMet = _allResearch.Where(r => Met(world, r)).Select(r => r.Id).ToHashSet(StringComparer.Ordinal),
        };
    }

    #endregion

    #region Items in and out

    /// <summary>
    /// Whether the player character is within reach of the site (else posts it is out of range): <see cref="ProductionSettings.TransferReach"/> of
    /// its footprint, or <see cref="ContainerAccess.TradingRange"/> of its position, so a small storage is never stricter than the original (#505).
    /// </summary>
    bool Near(World world, CharacterId who, BuildSite site, out CharacterCold cold)
    {
        cold = null!;
        var table = world.Characters;
        if (!table.TryResolveNext(who, out int slot) || table.Cold(slot) is not { IsPlayer: true } c || c.Inventory is null) return false;
        cold = c;

        var at = table.Next[slot].Position;
        float centre = Vector2.Distance(new Vector2(at.X, at.Z), new Vector2(site.Position.X, site.Position.Z));
        if (GapTo(site, at) <= Settings.TransferReach || centre <= ContainerAccess.TradingRange) return true;

        world.Post("Out of trading range.");
        return false;
    }

    /// <summary>
    /// Put an item into a container's grid (<see cref="ContainerAccess.ContainerFor"/>): at a free cell, or with X below 0 where it first fits
    /// (<see cref="PutInMachine.Count"/> units); a bag or limb item is refused like the machine's.
    /// </summary>
    static void PutInContainer(World world, Locks.Fixture container, CharacterCold cold, PutInMachine put)
    {
        // engine choice: a shop's stock is not a place to leave things
        if (ContainerAccess.ShopOf(world, container) is not null) { world.Post("That stock is the shop's."); return; }
        var root = cold.Inventory!;
        var storage = ContainerAccess.GridOf(world, container);
        var item = root.All().ElementAtOrDefault(put.Item.Index);
        var token = put.Item;
        if (item is null || item.Record != token.Record || item.Section != token.Section || item.X != token.X || item.Y != token.Y
            || item.Quantity != token.Quantity || item.LockedOn ||
            !InventoryCommands.IsMovableSection(item.Section) || item.Contents is not null && item.Section == "backpack_attach") return;
        var owner = root.Items.Contains(item) ? root : root.Backpack?.Contents;
        if (owner is null) return;
        if (put.X < 0)
        {
            if (InventoryCommands.MoveUnits(owner, item, InventoryCommands.ClickCount(item, put.Count), storage) > 0) world.Machines.Touch();
            return;
        }

        if (put.Y < 0 || put.X > storage.Width - item.Width || put.Y > storage.Height - item.Height
            || !storage.IsFree("main", put.X, put.Y, item.Width, item.Height))
            return;

        owner.Remove(item);
        item.Section = "main";
        item.X = put.X;
        item.Y = put.Y;
        storage.Items.Add(item);
        world.Machines.Touch();
    }

    /// <summary>
    /// Take an item (<see cref="TakeFromMachine.Count"/> units) out of a container into the character's inventory
    /// (<see cref="InventoryCommands.MoveInto"/>); what does not fit stays in the container. Taken from another faction's container it is stolen from
    /// its owner (<see cref="Crime.CrimeSystem.OwnerMark"/>): a Theft and the steal roll (<see cref="Crime.CrimeSystem.Steal"/>), caught, nothing
    /// moves.
    /// </summary>
    void TakeFromContainer(World world, Locks.Fixture container, CharacterCold cold, TakeFromMachine take)
    {
        var storage = ContainerAccess.GridOf(world, container, out var region);
        if (take.Index < 0 || take.Index >= storage.Items.Count || !region.Holds(storage.Items[take.Index])) return;
        var item = storage.Items[take.Index];
        if (world.SystemOf<Crime.CrimeSystem>() is { } crimes && world.Characters.TryResolveNext(take.Character, out int thief)
            && crimes.Steal(world, thief, container, Crime.CrimeCode.Theft)) return;
        var mark = item.Mark;
        if (container.Owner >= 0 && container.Owner != cold.Faction && Crime.CrimeSystem.IsStealing(world, container)
            && mark == StolenMark.None)
            item.Mark = Crime.CrimeSystem.OwnerMark(world, container.Building, container.Owner);

        int moved = InventoryCommands.MoveInto(storage, item, InventoryCommands.ClickCount(item, take.Count), cold.Inventory!, take.BagFirst,
            out bool worn, cold);
        if (storage.Items.Contains(item)) item.Mark = mark;   // what stayed keeps its mark
        if (worn) Assembly?.RefreshEquipment(cold);
        if (moved > 0) world.Machines.Touch();
    }

    /// <summary>
    /// The complete machine at <paramref name="site"/> when <paramref name="who"/> (one of the player's characters) may move items in and out of it
    /// from where it stands; posts "Out of trading range." when it is too far. For the held item (<see cref="PlayerState.Held"/>).
    /// </summary>
    internal Machine? Reach(World world, CharacterId who, string site, out CharacterCold? cold)
    {
        cold = null;
        if (Site(world, site) is not { Complete: true } built || Ensure(world, built) is not { } m || !Near(world, who, built, out var c)) return null;
        cold = c;
        return m;
    }

    /// <summary>
    /// Puts the player's item into the machine (or the container) named.
    /// </summary>
    void Put(World world, PutInMachine put)
    {
        if (ContainerAccess.ContainerFor(world, put.Site, put.Character, out var holder) is { } container)
        {
            if (holder is not null && put.Slot == -1) PutInContainer(world, container, holder, put);
            return;
        }

        if (Site(world, put.Site) is not { Complete: true } site || Ensure(world, site) is not { } m
            || !Near(world, put.Character, site, out var cold))
            return;

        var root = cold.Inventory!;
        var item = root.All().ElementAtOrDefault(put.Item.Index);
        var token = put.Item;
        if (item is null || item.Record != token.Record || item.Section != token.Section || item.X != token.X || item.Y != token.Y
            || item.Quantity != token.Quantity || item.LockedOn ||
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
            if (m.Data.IsStorage && !m.Data.TakesByHand(item.Record, item.Type))
            {
                world.Post($"{m.Data.Name} does not take {item.Name}.");
                return;
            }

            if (put.X < 0)
            {
                if (InventoryCommands.MoveUnits(owner, item, InventoryCommands.ClickCount(item, put.Count), storage) > 0) world.Machines.Touch();
                return;
            }

            if (put.Y < 0 || put.X > storage.Width - item.Width || put.Y > storage.Height - item.Height
                || !storage.IsFree("main", put.X, put.Y, item.Width, item.Height))
                return;

            owner.Remove(item);
            item.Section = "main";
            item.X = put.X;
            item.Y = put.Y;
            storage.Items.Add(item);
        }

        world.Machines.Touch();
    }

    /// <summary>
    /// Takes an item out of the machine (or the container) named.
    /// </summary>
    void Take(World world, TakeFromMachine take)
    {
        if (ContainerAccess.ContainerFor(world, take.Site, take.Character, out var holder) is { } container)
        {
            if (holder is not null && take.Slot == -1) TakeFromContainer(world, container, holder, take);
            return;
        }

        if (Site(world, take.Site) is not { Complete: true } site || world.Machines.Find(take.Site) is not { } m
            || !Near(world, take.Character, site, out var cold))
            return;
        if (CaughtTaking(world, TownGoods(take.Site), take.Character, cold, out var mark)) return;

        var root = cold.Inventory!;
        if (take.Slot == -1)
        {
            TakeStored(world, m, cold, take, mark);
            return;
        }

        if (take.Slot == -2 && m.Craft is { } craft)
        {
            TakeCrafted(world, m, craft, root, take.Index, take.Count);
            return;
        }

        (string Item, int Count) held = take.Slot == -2
            ? (m.Output?.Recipe.Item ?? "", m.Output?.Count ?? 0)
            : take.Slot >= 0 && take.Slot < m.Inputs.Length ? (m.Inputs[take.Slot].Item, m.Inputs[take.Slot].Count) : ("", 0);
        if (take.Count > 0) held.Count = Math.Min(held.Count, take.Count);
        if (held.Count <= 0 || _items.Create(held.Item, held.Count) is not { } made) return;
        made.Mark = mark;
        int moved = Placed(root, made);
        if (moved <= 0) return;
        if (take.Slot == -2) m.Output!.Count -= moved;
        else m.Inputs[take.Slot].Count -= moved;
        world.Machines.Touch();
    }

    /// <summary>
    /// Takes an item out of a machine's storage grid into the character's inventory (what does not fit stays in the storage); the units taken get
    /// <paramref name="mark"/> when it is a theft (<see cref="CaughtTaking"/>).
    /// </summary>
    void TakeStored(World world, Machine m, CharacterCold cold, TakeFromMachine take, StolenMark mark)
    {
        if (m.Storage is not { } storage || m.Closed || take.Index < 0 || take.Index >= storage.Items.Count) return;

        var item = storage.Items[take.Index];
        var kept = item.Mark;
        if (mark != StolenMark.None && kept == StolenMark.None) item.Mark = mark;

        int moved = InventoryCommands.MoveInto(storage, item, InventoryCommands.ClickCount(item, take.Count), cold.Inventory!, take.BagFirst,
            out bool worn, cold);
        if (storage.Items.Contains(item)) item.Mark = kept;   // what stayed keeps its mark
        if (moved > 0) world.Machines.Touch();
        if (worn) Assembly?.RefreshEquipment(cold);
    }

    #endregion
}