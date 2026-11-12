using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;
using Meitou.Simulation.Construction;

namespace Meitou.Simulation.Production;

/// <summary>
/// The power grid (docs/game/buildings-production.md "Power", docs/simulation.md "Production"): each town's generators, batteries and consumers,
/// shared as the original's <c>Town::vfunc_87</c>. Runs after the operators, so a consumer's grant (from last tick's operators) scales its work the
/// tick after.
/// </summary>
public sealed partial class ProductionSystem
{
    /// <summary>
    /// Per town, whether batteries covered a shortfall last tick (the original's town <c>+0x470</c>, read the frame after it is set; not saved).
    /// </summary>
    readonly Dictionary<int, bool> _batteryMode = [];
    /// <summary>
    /// The machines of each town's grid this tick, by town (a scratch map).
    /// </summary>
    readonly Dictionary<int, List<Machine>> _grids = [];
    /// <summary>
    /// Per town, whether its generators give power and its batteries can (the town object's <c>+0x474</c> and <c>+0x480</c> above 0 after
    /// <c>Town::vfunc_87</c>); what <see cref="HasPower"/> reads. Not saved.
    /// </summary>
    readonly Dictionary<int, (bool Generators, bool Batteries)> _townSupply = [];
    /// <summary>
    /// Per town, the grid's totals after the last tick (the town panel's power and battery bars, <see cref="TownPower"/>). Not saved.
    /// </summary>
    readonly Dictionary<int, TownPower> _townPower = [];

    /// <summary>
    /// The research upgrade of the machine's building: the player's only (<c>FUN_14082ec70</c>).
    /// </summary>
    BuildingUpgrade UpgradeOf(World world, Machine m) => world.Research.Upgrade(world, m.Data.Id, Site(world, m.Site)?.Owner ?? -1);

    /// <summary>
    /// A generator's rated output: the data's plus the research increase.
    /// </summary>
    float Rated(World world, Machine m) => m.Data.PowerOutput + UpgradeOf(world, m).PowerIncrease;
    /// <summary>
    /// A battery's capacity now (<c>FUN_140297650</c>, <b>Verified</b>): the construction fraction x (capacity + research increase).
    /// </summary>
    float Capacity(World world, Machine m) => m.Health * (m.Data.PowerCapacity + UpgradeOf(world, m).CapacityIncrease);

    /// <summary>
    /// Whether a generator has fuel: no fuel sections (wind), or something left in one.
    /// </summary>
    static bool Fueled(Machine m) => m.Inputs.Length == 0 || Array.Exists(m.Inputs, i => i.Level > 0 || i.Count > 0);

    /// <summary>
    /// A generator's output now (slot 166, <c>FUN_140297760</c>, <b>Verified</b>): the construction fraction (<see cref="Machine.Health"/>) x (output
    /// + increase) while switched on, 0 out of fuel; wind (<c>FUN_140297810</c>) x <see cref="Machine.Wind"/>, nothing below 1. A battery gives the
    /// same health-scaled figure while it holds charge.
    /// </summary>
    float Output(World world, Machine m)
    {
        if (!m.PowerOn || !Fueled(m)) return 0;
        float output = m.Health * Rated(world, m);
        if (m.Data.IsWind && (output *= m.Wind) < 1) return 0;
        return output;
    }

    /// <summary>
    /// The player outposts with their grids' totals, for the town panel (docs/game/ui-screens.md "Town panel").
    /// </summary>
    public IReadOnlyList<OutpostSnapshot> OutpostView(World world) => world.Buildings.Towns.Count == 0 ? []
        : [.. world.Buildings.Towns.Select(t => new OutpostSnapshot(t.Number, t.Name, t.Position, t.Radius, t.Owner, t.Public,
            _townPower.GetValueOrDefault(t.Number)))];

    /// <summary>
    /// The towns' round-robin cursor of the power frame (the static <c>DAT_14213543c</c>, advanced before each use); not saved.
    /// </summary>
    int _powerCursor;

    /// <summary>
    /// The power step: every machine's indoor, active, wind and rain state, each town's grid sharing its power (<see cref="ShareOneTown"/>), the
    /// generators' fuel burnt, then each machine's health, research multiplier and efficiency.
    /// </summary>
    void PowerTick(World world, float dt)
    {
        foreach (var list in _grids.Values) list.Clear();
        foreach (var m in world.Machines.All)
        {
            // Slot 59 (isIndoors): interior furniture standing in a building; slot 164: a light building outdoors works only while it is not daytime.
            m.Indoors = IsIndoors(m.Data, Site(world, m.Site));
            m.Active = !m.Data.IsLight || m.Indoors || !world.Daytime;
            if (m.Data.IsWind) (m.Wind, m.WindRated) = (WindShare(world, m), Rated(world, m));
            if (m.Data.Function == MachineData.RainCollectorFunction)
                m.Rain = !m.Indoors && world.Weather?.At(m.Position.X, m.Position.Z) is { Affect: not WeatherAffecting.Acid } here ? here.Wetness : 0;

            if (!m.Data.IsConsumer && !m.Data.IsGenerator && !m.Data.IsBattery || IsTownWork(m.Site)
                || Site(world, m.Site) is not { Complete: true } site)
            {
                m.Power = 1;
                continue;
            }
            if (!_grids.TryGetValue(site.Town, out var grid)) _grids[site.Town] = grid = [];
            grid.Add(m);
        }

        ShareOneTown(world, dt);
        foreach (var m in world.Machines.All)
        {
            if (m.Data.IsGenerator)
            {
                m.Status = (byte)(m.Demand > 0 ? 2 : 0);
                if (m.Inputs.Length > 0 && m.PowerOn && Live(world, m)) BurnFuel(m, dt);
            }
        }

        foreach (var m in world.Machines.All)
        {
            var site = Site(world, m.Site);
            bool built = site is { Complete: true };
            m.Health = site is { Target: > 0 } s ? Math.Clamp(s.Progress / s.Target, 0, 1) : 1;
            m.ProductionMult = UpgradeOf(world, m).ProductionMult;
            m.Efficiency = built ? Efficiency(world, m) : 0;
            m.HasPower = built && HasPower(world, m, Site(world, m.Site)!.Town);
            m.Rotation = !built ? 0 : m.Data.IsGenerator ? (m.PowerOn ? Output(world, m) * 0.01f : 0) : m.Efficiency;
            if (m.Data.IsBattery)
            {
                // The battery's word reads its town grid's flows, not its own (FUN_140300b40).
                TownPower grid = default;
                m.OnGrid = site is not null && _townPower.TryGetValue(site.Town, out grid);
                m.GridCharging = grid.Charging > 0;
                m.GridDraining = grid.Draining > 0;
            }
        }
    }

    /// <summary>
    /// One town's power frame per pass, the next town in ascending number after the cursor (<b>Verified</b>: <c>FUN_14092d850</c> advances the cursor
    /// and calls the town's <c>vfunc 0x2b8</c> for that one town only; the original cycles over every eligible town of the world per rendered frame,
    /// so the cadence per town is an engine choice). The other towns keep their last grants, power shares, totals and supply; the batteries of every
    /// town integrate their last grant each pass.
    /// </summary>
    void ShareOneTown(World world, float dt)
    {
        var towns = _grids.Where(g => g.Value.Count > 0).Select(g => g.Key).Order().ToList();
        foreach (var gone in _townPower.Keys.Where(t => !towns.Contains(t)).ToList()) _townPower.Remove(gone);
        if (towns.Count == 0) return;
        _powerCursor++;
        if (_powerCursor >= towns.Count) _powerCursor = 0;
        int picked = towns[_powerCursor];
        foreach (var m in _grids[picked]) m.Power = 1;
        Share(world, picked, _grids[picked]);
        foreach (var town in towns) ChargeBatteries(world, town, _grids[town], dt);
    }

    /// <summary>
    /// The store of each battery moves by flow / rate x dt seconds, between 0 and the capacity (<c>FarmBuilding::vfunc_27</c> on batteries); the
    /// town's charge totals follow.
    /// </summary>
    void ChargeBatteries(World world, int town, List<Machine> grid, float dt)
    {
        float charge = 0, capacity = 0;
        foreach (var b in grid)
        {
            if (!b.Data.IsBattery) continue;
            float rate = Rated(world, b);
            b.Store = Capacity(world, b);
            if (rate > 0) b.Charge = Math.Clamp(b.Charge + b.Granted / rate * dt, 0, b.Store);
            (charge, capacity) = (charge + b.Charge, capacity + b.Store);
        }

        if (_townPower.TryGetValue(town, out var power)) _townPower[town] = power with { Charge = charge, Capacity = capacity };
    }

    /// <summary>
    /// A wind generator's share of its output (<c>PhysicsCollection::getWindRotationPower</c>, <b>Verified</b>): its first ROTATION_WIND_SPEED
    /// part's <see cref="RotorSpec.WindShare"/> of the wind speed of the weather region at the site; 0 without such a part or without weather. The
    /// original reads it from the part's render entity, so a generator without one loaded gives nothing; here every built generator has its wind
    /// (engine choice).
    /// </summary>
    static float WindShare(World world, Machine m)
    {
        m.WindSpeed = world.Buildings.Find(m.Site) is { } site && world.Weather?.At(site.Position.X, site.Position.Z) is { } here ? here.WindSpeed : 0;
        return m.WindRotor is { } rotor ? RotorSpec.WindShare(m.WindSpeed, rotor.Min, rotor.Max) : 0;
    }

    /// <summary>
    /// The efficiency a machine's sound emitter hears (<c>FarmBuilding::vfunc_108</c>, <b>Observed</b>): the share of its operators (1 when the data
    /// has no operator count) x health x the granted share of its demand (1 with no demand; for a battery, below its capacity, the share of its rate
    /// it is charging, negative while it drains) x the yield x the BUILDING's raw output rate x the research production multiplier. A wind generator
    /// hears its blade's wind share (<c>WindGeneratorBuilding::getSoundIntensity</c>, <b>Verified</b>): <see cref="Machine.Wind"/>.
    /// </summary>
    float Efficiency(World world, Machine m)
    {
        if (m.Data.IsWind) return m.Wind;
        float share = m.Data.PowerOperators > 0 ? (float)m.Operators / m.Data.PowerOperators : 1;
        float saturation = m.Power;
        if (m.Data.IsBattery)
        {
            float rate = Rated(world, m);
            saturation = rate > 0 && m.Charge < Capacity(world, m) ? m.Granted / rate : 1;
        }

        return m.Active ? share * m.Health * saturation * m.Yield * m.Data.OutputRate * m.ProductionMult : 0;
    }

    /// <summary>
    /// State 93 <c>BUILDING_HAS_POWER</c> (<c>FUN_140596ab0</c>, <b>Verified</b>), a start condition of operating (87, 221) and manning a turret (146):
    /// the target building's <see cref="HasPower"/>, read from the town's supply rather than the machine's granted share. An NPC town's machine
    /// always has it (engine choice: NPC towns have no power grid here).
    /// </summary>
    bool Powered(World world, BuildSite site, Machine m) => IsTownWork(site.Id) || HasPower(world, m, site.Town);

    /// <summary>
    /// Whether a machine counts as powered for its sound emitter (<c>FUN_140299370</c>, <b>Observed</b>): a generator always (fuel and switch are not
    /// asked); a machine drawing nothing always; else it must be switched on and its town's generators give power, or its batteries do while the grid
    /// is in battery mode and the machine takes battery power.
    /// </summary>
    bool HasPower(World world, Machine m, int town)
    {
        if (m.Data.IsGenerator || Rated(world, m) == 0) return true;
        if (!m.PowerOn) return false;
        if (!_townSupply.TryGetValue(town, out var supply)) return true;
        return supply.Generators || (m.GetsBattery && _batteryMode.GetValueOrDefault(town) && supply.Batteries);
    }

    /// <summary>
    /// One town's frame (<b>Observed</b>, <c>Town::vfunc_87</c>): generator output is shared among the consumers in ascending demand, each taking at
    /// most an equal share of what is left (<c>FUN_140927350</c> sorts); when batteries were in use last tick, consumers left short that get battery
    /// power share the batteries' output. With nothing left over the batteries drain by what was drawn; with a surplus they charge from it (each at
    /// most its rate) and the grid leaves battery mode.
    /// </summary>
    void Share(World world, int town, List<Machine> grid)
    {
        bool mode = _batteryMode.GetValueOrDefault(town);
        float supply = 0, batteries = 0, demandTotal = 0;
        var consumers = new List<Machine>();
        foreach (var m in grid)
        {
            m.Granted = 0;
            if (m.Data.IsGenerator) supply += m.Demand = Output(world, m);
            else if (m.Data.IsBattery) { m.Demand = m.PowerOn && m.Charge > 0 ? m.Health * Rated(world, m) : 0; batteries += m.Demand; }
            else
            {
                // A farm wants its whole draw whoever works it and whether or not it is switched on (FarmBuilding slot 163, FUN_1400df330:
                // |power output + increase|).
                float share = m.Data.Farm is null && m.Data.PowerOperators > 0 ? (float)m.LastOperators / m.Data.PowerOperators : 1;
                m.Demand = (m.Data.Farm is not null || m.PowerOn) && m.Active ? MathF.Abs(Rated(world, m)) * share : 0;
                demandTotal += m.Demand;
                consumers.Add(m);
            }
        }

        float generated = supply;
        _townSupply[town] = (generated > 0, batteries > 0);
        consumers.Sort((a, b) => a.Demand != b.Demand ? a.Demand.CompareTo(b.Demand) : string.CompareOrdinal(a.Site, b.Site));
        int left = consumers.Count, @short = 0;
        foreach (var c in consumers)
        {
            float share = supply / Math.Max(1, left--);
            float give = MathF.Min(share, c.Demand);
            supply -= give;
            c.Granted = give;
            if (give < c.Demand && c.GetsBattery) @short++;
        }

        float batteryLeft = batteries;
        if (mode && @short > 0)
        {
            foreach (var c in consumers)
            {
                if (!c.GetsBattery || c.Granted >= c.Demand) continue;
                float give = MathF.Min(c.Demand - c.Granted, batteryLeft / Math.Max(1, @short--));
                batteryLeft -= give;
                c.Granted += give;
            }
        }

        float charged = 0, drained = 0;
        if (supply <= 0)
        {
            float used = drained = batteries - batteryLeft;
            foreach (var b in grid)
                if (b.Data.IsBattery) b.Granted = used > 0 && batteries > 0 ? -used * b.Demand / batteries : 0;
            _batteryMode[town] = true;
        }
        else
        {
            _batteryMode[town] = false;
            charged = ChargeFromSurplus(world, grid, supply, batteries: grid.Count(b => b.Data.IsBattery));
        }

        foreach (var m in grid)
        {
            if (m.Data.IsConsumer) m.Power = m.Demand > 0 ? m.Granted / m.Demand : 1;
            else if (m.Data.IsGenerator)
            {
                // The load (GeneratorBuilding::vfunc_167): output / rated x (charging + consumer demand) / all generators' output, at most 1.
                float rated = Rated(world, m);
                m.Usage = generated > 0 && rated > 0 ? MathF.Min(1, m.Demand / rated * (charged + demandTotal) / generated) : 0;
            }
        }

        _townPower[town] = new(generated, demandTotal, charged, drained, 0, 0);
    }

    /// <summary>
    /// The surplus among the batteries (<b>Verified</b>, <c>FUN_14092cd60</c>): in grid order every battery clears its grant; one that is on and
    /// below its capacity (the engine's reading of its switch byte and of a rate that is 0 when full, <b>Observed</b>) takes min(surplus left /
    /// batteries still to come, its rate); the count falls by one for every battery, chargeable or not. Returns the total charged.
    /// </summary>
    float ChargeFromSurplus(World world, List<Machine> grid, float surplus, int batteries)
    {
        float charged = 0;
        foreach (var b in grid)
        {
            if (!b.Data.IsBattery) continue;
            b.Granted = 0;
            if (b.PowerOn && b.Charge < Capacity(world, b))
            {
                float give = MathF.Min(surplus / Math.Max(1, batteries), MathF.Abs(Rated(world, b)));
                surplus -= give;
                charged += give;
                b.Granted = give;
            }

            batteries--;
        }

        return charged;
    }

    /// <summary>
    /// A generator burns fuel as a producer draws inputs (<c>vfunc_183</c>): <c>rate x dt x P x option</c>, times its load.
    /// </summary>
    void BurnFuel(Machine m, float dt)
    {
        float amount = m.Data.Rate * dt * _constants.ProductionSpeed * Settings.ProductionOption * m.Usage;
        if (amount <= 0) return;
        foreach (var input in m.Inputs)
        {
            float need = input.Rate * amount;
            if (need <= 0) continue;
            while (input.Level < need && input.Count > 0) { input.Count--; input.Level += 1; }
            input.Level = MathF.Max(0, input.Level - need);
        }
    }

    /// <summary>
    /// The factor a machine's work is scaled by: its health, its power share (slot 109, which slot 150 <c>FUN_140298700</c> reads virtually) and its
    /// research production multiplier. A rain collector's slot 109 (<c>FUN_140298470</c>, <b>Verified</b>) is that times the rain strength, so it
    /// collects nothing while it does not rain.
    /// </summary>
    float WorkFactor(World world, Machine m) => m.Health * m.Power * UpgradeOf(world, m).ProductionMult
        * (m.Data.Function == MachineData.RainCollectorFunction ? m.Rain : 1);
}
