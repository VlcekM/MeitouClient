using Meitou.Data.Gameplay.Economy;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Dialogue;

/// <summary>
/// What DC_HAS_ILLEGAL_ITEM reads and writes besides the characters (<see cref="DialogueSystem"/>): each culture faction's trade culture with its
/// <c>illegal goods</c>, and when each smuggling message was last posted.
/// </summary>
sealed class IllegalGoods(PopulationData data)
{
    /// <summary>
    /// Per culture faction: its trade culture (Faction +0x90), built on first use.
    /// </summary>
    TradeCulture?[]? _factionCultures;

    /// <summary>
    /// When "{1} has been discovered smuggling {2}" was last posted per carrier and item (game hours); display only, not hashed.
    /// </summary>
    readonly Dictionary<(CharacterId, string), double> _smugglingPosted = [];

    /// <summary>
    /// The trade culture of <paramref name="faction"/> when it bans any goods; null: none (or no such faction).
    /// </summary>
    public TradeCulture? BannedBy(int faction)
    {
        _factionCultures ??= [.. data.Factions.Select(f => TradeCulture.From(data.Db, f.TradeCulture))];
        if ((uint)faction >= (uint)_factionCultures.Length || _factionCultures[faction] is not { Illegal.Count: > 0 } culture) return null;
        return culture;
    }

    /// <summary>
    /// Posts that <paramref name="carrier"/> was found smuggling <paramref name="record"/>: not on the Talk menu's dry run, and once per carrier and
    /// item per game hour (engine choice).
    /// </summary>
    public void Report(World world, CharacterId carrierId, CharacterCold carrier, string record, bool dryRun)
    {
        if (dryRun || (_smugglingPosted.TryGetValue((carrierId, record), out double last) && world.GameHours - last < 1)) return;
        _smugglingPosted[(carrierId, record)] = world.GameHours;
        world.Post(Loc.Tr("{1} has been discovered smuggling {2}", carrier.Name, data.Db.Find(record)?.Name ?? record));
    }
}
