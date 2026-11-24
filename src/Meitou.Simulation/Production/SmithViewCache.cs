using Meitou.Data;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Construction;

namespace Meitou.Simulation.Production;

/// <summary>
/// The Craft tab's Current Smith per bench as the host shows it (<see cref="ProductionSystem.SmithsView"/>, docs/simulation.md "Snapshots"): each
/// publish after the machines worked or changed reads every bench's smith, his effective level and penalty and the queue, and makes a bench's view
/// again only when one of those differs; the map is new only when some view or the set of benches did. Exact: a view reads nothing else but the
/// bench record and the static game data.
/// </summary>
sealed class SmithViewCache(GameDatabase db)
{
    /// <summary>
    /// A bench's view and every value it was made from.
    /// </summary>
    sealed record Entry(Machine Machine, CharacterId Smith, string Name, float Level, int Penalty, CraftOption[] Queue, CraftSmithView View)
    {
        /// <summary>
        /// Whether the view was made from these values (the level by its bits, the queued options by value).
        /// </summary>
        public bool Matches(Machine machine, CharacterId smith, string name, float level, int penalty, List<CraftJob> queue)
        {
            if (!ReferenceEquals(Machine, machine) || Smith != smith || !string.Equals(Name, name, StringComparison.Ordinal)) return false;
            if (!Level.Equals(level) || Penalty != penalty || Queue.Length != queue.Count) return false;

            for (int i = 0; i < Queue.Length; i++)
                if (!Queue[i].Equals(queue[i].Option)) return false;
            return true;
        }
    }

    /// <summary>
    /// The map last published (null: made again).
    /// </summary>
    IReadOnlyDictionary<string, CraftSmithView>? _published;

    /// <summary>
    /// The shown text revision (<see cref="ShownText.Revision"/>) the published view was built at: its names are rebuilt when a mod's text changes.
    /// </summary>
    int _shownRevision;

    /// <summary>
    /// The entries of <see cref="_published"/>'s views, by site.
    /// </summary>
    readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// This publish's entries in the registry's order (reused).
    /// </summary>
    readonly List<(string Site, Entry Entry)> _fresh = [];

    /// <summary>
    /// The machine registry's version and work count <see cref="_published"/> was checked at (-1: never).
    /// </summary>
    (long Version, long Worked) _version = (-1, -1);

    /// <summary>
    /// Every bench's current smith by site (<c>FUN_1402bcf60</c>): the bench's last operator (<see cref="Machine.Smith"/>) while he is alive with
    /// stats.
    /// </summary>
    public IReadOnlyDictionary<string, CraftSmithView> Of(World world)
    {
        var machines = (world.Machines.Version, world.Machines.Worked);
        if (_published is not null && _version == machines && _shownRevision == ShownText.Revision) return _published;

        _version = machines;
        if (_shownRevision != ShownText.Revision)
        {
            // Every entry's view holds shown names: make them all again.
            _entries.Clear();
            _shownRevision = ShownText.Revision;
        }
        bool changed = Collect(world);
        if (_published is not null && !changed) return _published;
        return _published = Published();
    }

    /// <summary>
    /// Fills <see cref="_fresh"/> with every bench's entry, an unchanged bench's the published one; true when some entry or the set of benches
    /// differs from <see cref="_entries"/>.
    /// </summary>
    bool Collect(World world)
    {
        _fresh.Clear();
        bool changed = false;

        foreach (var m in world.Machines.All)
        {
            if (m.Craft is not { } state || m.Data.Craft is not { } bench || m.Data.Stat <= 0 || !world.Characters.IsAlive(m.Smith)
                || world.Characters.Cold(m.Smith.Slot) is not { Stats: { } stats, Medical: { } medical } cold) continue;
            var (level, penalty) = World.EffectiveSkill(cold, medical, stats, (StatsEnumerated)m.Data.Stat);

            if (_entries.TryGetValue(m.Site, out var entry) && entry.Matches(m, m.Smith, cold.Name, level, penalty, state.Queue))
            {
                _fresh.Add((m.Site, entry));
                continue;
            }

            var view = new CraftSmithView(world.ShownName(cold), (int)level, penalty, Predictions(m, bench, state, level)) { Stat = m.Data.Stat };
            _fresh.Add((m.Site, new Entry(m, m.Smith, cold.Name, level, penalty, [.. state.Queue.Select(j => j.Option)], view)));
            changed = true;
        }

        return changed || _fresh.Count != _entries.Count;
    }

    /// <summary>
    /// A new map of <see cref="_fresh"/>'s views; <see cref="_entries"/> follows it.
    /// </summary>
    Dictionary<string, CraftSmithView> Published()
    {
        _entries.Clear();
        var map = new Dictionary<string, CraftSmithView>(StringComparer.Ordinal);

        foreach (var (site, entry) in _fresh)
        {
            _entries[site] = entry;
            map[site] = entry.View;
        }

        return map;
    }

    /// <summary>
    /// What a smith at <paramref name="level"/> would make of each queued craft (<see cref="CraftPrediction"/>).
    /// </summary>
    List<CraftPrediction> Predictions(Machine m, CraftBench bench, CraftState state, float level)
    {
        // FUN_1402b5900 reads the first queued craft whichever one is hovered.
        int critical = state.Queue.Count == 0
            ? 0
            : Math.Clamp((int)(CraftBench.CriticalChance(level, m.Data.Stat, state.Queue[0].Option.Level) * 100), 0, 100);

        var entries = new List<CraftPrediction>(state.Queue.Count);
        foreach (var job in state.Queue) entries.Add(Prediction(bench, job.Option, level, critical));
        return entries;
    }

    /// <summary>
    /// What a smith at <paramref name="level"/> would make of one queued craft, with the first craft's <paramref name="critical"/> chance.
    /// </summary>
    CraftPrediction Prediction(CraftBench bench, CraftOption option, float level, int critical)
    {
        var none = new CraftPrediction(-1, null, false, 0, -1);
        switch (option.Kind)
        {
            case CraftKind.Armour or CraftKind.Crossbow:
                return none with { Quality = CraftBench.ArmourQuality(level, false), Critical = critical };
            case CraftKind.Weapon:
                var model = ProductionSystem.WeaponModel(bench, option, level, false);
                return none with
                {
                    Model = model.Model is { } id ? db.Find(id)?.ShownName() ?? id : "",
                    ModelChanged = model.Model != option.Model,
                    MaterialCost = db.Find(option.Item)?.GetInt("material cost", 4) ?? 4,
                    Critical = critical,
                };
            default:
                return none;
        }
    }
}
