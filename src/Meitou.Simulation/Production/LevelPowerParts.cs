using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Production;

/// <summary>
/// The buildings and layout objects of each level town whose record can draw power or generate it, in list order, so a town's power frame walks
/// those alone instead of the whole list (<see cref="ProductionSystem"/>'s <c>NpcDraws</c> and <c>NpcOutput</c>). A record's machine data never
/// changes and a town's list is swapped whole when it changes (<see cref="TownSite.Buildings"/>), so a town's parts stand until its list is another.
/// </summary>
sealed class LevelPowerParts(Func<string, MachineData?> data)
{
    /// <summary>
    /// A building of a town's list that takes part: whether its own record does (<paramref name="Own"/>) and its layout objects whose records do.
    /// </summary>
    public readonly record struct Part(TownBuilding Building, bool Own, TownLayoutBuilding[] Pieces);

    /// <summary>
    /// A town's parts for the list they were made from: the ones that can draw power and the ones that can generate it.
    /// </summary>
    sealed record Parts(IReadOnlyList<TownBuilding> List, Part[] Draws, Part[] Generators);

    /// <summary>
    /// The parts by town index.
    /// </summary>
    readonly Dictionary<int, Parts> _byTown = [];

    /// <summary>
    /// Held while <see cref="_byTown"/> is read or filled: the think phase's power checks ask from every worker.
    /// </summary>
    readonly Lock _gate = new();

    /// <summary>
    /// The buildings of <paramref name="site"/> that can draw power: a consumer record with a functionality, its own or a layout object's.
    /// </summary>
    public Part[] Draws(TownSite site) => Of(site).Draws;

    /// <summary>
    /// The buildings of <paramref name="site"/> that can generate power: a generator record, its own or a layout object's.
    /// </summary>
    public Part[] Generators(TownSite site) => Of(site).Generators;

    /// <summary>
    /// The parts of the town's current list, made when the list is new.
    /// </summary>
    Parts Of(TownSite site)
    {
        var list = site.Buildings;
        lock (_gate)
        {
            if (_byTown.TryGetValue(site.Index, out var known) && ReferenceEquals(known.List, list)) return known;

            var parts = new Parts(list, Taking(list, DrawsPower), Taking(list, GeneratesPower));
            _byTown[site.Index] = parts;
            return parts;
        }
    }

    /// <summary>
    /// Whether a record draws power on a town's frame (what <c>Draw</c> asks first).
    /// </summary>
    bool DrawsPower(string record) => data(record) is { IsConsumer: true, Function: >= 0 };

    /// <summary>
    /// Whether a record generates power.
    /// </summary>
    bool GeneratesPower(string record) => data(record) is { IsGenerator: true };

    /// <summary>
    /// The buildings of <paramref name="list"/> that take part by <paramref name="takes"/>, themselves or with layout objects, in list order.
    /// </summary>
    static Part[] Taking(IReadOnlyList<TownBuilding> list, Func<string, bool> takes)
    {
        var parts = new List<Part>();
        foreach (var b in list)
        {
            bool own = takes(b.BuildingId);
            TownLayoutBuilding[] pieces = [.. b.LayoutBuildings.Where(p => takes(p.RecordId))];
            if (own || pieces.Length > 0) parts.Add(new Part(b, own, pieces));
        }

        return [.. parts];
    }
}
