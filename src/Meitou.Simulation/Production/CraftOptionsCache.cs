using Meitou.Data.Gameplay.Construction;

namespace Meitou.Simulation.Production;

/// <summary>
/// The benches' craft options as the host shows them (<see cref="ProductionSystem.CraftsView"/>, docs/simulation.md "Snapshots"): one option list
/// per bench record, shared by every bench of that record and kept until research unlocks change; the site map is made again only when the benches
/// or the unlocks changed. Exact: an option reads only its bench record (its options, tech level and inputs), the static game data and the unlocks.
/// </summary>
sealed class CraftOptionsCache(Func<World, Machine, IReadOnlyList<CraftOptionView>> build)
{
    /// <summary>
    /// The site map last published (null: made again).
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<CraftOptionView>>? _published;

    /// <summary>
    /// The shown text revision (<see cref="ShownText.Revision"/>) the published view was built at: its names are rebuilt when a mod's text changes.
    /// </summary>
    int _shownRevision;

    /// <summary>
    /// The crafting machines <see cref="_published"/> was made from, in the registry's order. A machine's site, record and bench state are fixed when
    /// it is made, so a bench added, dropped or replaced (an upgrade to another record) shows as a different machine here.
    /// </summary>
    Machine[] _benches = [];

    /// <summary>
    /// The unlocked crafts the option lists were made with. Research replaces the set whenever its unlocks change (<c>ResearchState.Refresh</c>: a
    /// completion, a load) and never changes a set in place, so the same set means the same unlocks.
    /// </summary>
    IReadOnlySet<(CraftKind, string)>? _unlocked;

    /// <summary>
    /// The option list of each bench record, made with <see cref="_unlocked"/>; by reference, as the records are shared by every machine of their
    /// building (<see cref="ProductionSystem.Data"/>).
    /// </summary>
    readonly Dictionary<MachineData, IReadOnlyList<CraftOptionView>> _lists = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Every crafting bench's options by site (an empty list for a bench record without crafts).
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<CraftOptionView>> Of(World world)
    {
        Sync(world);
        if (_published is not null && _shownRevision == ShownText.Revision && SameBenches(world)) return _published;

        _shownRevision = ShownText.Revision;

        _benches = [.. world.Machines.All.Where(m => m.Craft is not null)];
        var map = new Dictionary<string, IReadOnlyList<CraftOptionView>>(StringComparer.Ordinal);
        foreach (var m in _benches) map[m.Site] = ListOf(world, m);
        return _published = map;
    }

    /// <summary>
    /// The options of the machine of site <paramref name="site"/> (empty: no crafting bench there).
    /// </summary>
    public IReadOnlyList<CraftOptionView> Of(World world, string site)
    {
        Sync(world);
        return world.Machines.Find(site) is { } m ? ListOf(world, m) : [];
    }

    /// <summary>
    /// Drops the option lists and the site map when the unlocks are not the ones they were made with.
    /// </summary>
    void Sync(World world)
    {
        if (ReferenceEquals(_unlocked, world.Research.UnlockedCrafts)) return;

        _unlocked = world.Research.UnlockedCrafts;
        _lists.Clear();
        _published = null;
    }

    /// <summary>
    /// Whether the registry's crafting machines are still <see cref="_benches"/>, the same machines in the same order.
    /// </summary>
    bool SameBenches(World world)
    {
        int k = 0;
        foreach (var m in world.Machines.All)
        {
            if (m.Craft is null) continue;
            if (k == _benches.Length || !ReferenceEquals(m, _benches[k])) return false;
            k++;
        }

        return k == _benches.Length;
    }

    /// <summary>
    /// The option list of the machine's record, made on first use since the unlocks last changed.
    /// </summary>
    IReadOnlyList<CraftOptionView> ListOf(World world, Machine m)
    {
        if (m.Data.Craft is null) return [];
        if (_lists.TryGetValue(m.Data, out var list)) return list;
        return _lists[m.Data] = build(world, m);
    }
}
