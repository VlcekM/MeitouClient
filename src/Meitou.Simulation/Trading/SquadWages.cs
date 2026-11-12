using Meitou.Simulation.Population;

namespace Meitou.Simulation.Trading;

/// <summary>
/// CHARACTER `wages` (Character::giveBirth and Character::dailyUpdate, Verified): a character not of the player's faction with wages of 1 or more
/// sets (not adds to) its squad's money to them when it is made and every day; with several waged members the last one wins. Engine choices: the
/// day pass runs when the game day changes, members in squad order; "when made" is the first pass that sees the squad, except the squads already
/// there at this system's first pass and a squad of characters back from a save or from out of the world (Squad.Restored: giveBirth pays only a
/// character made without saved state, so a loaded game keeps its saved purses until the next day).
/// </summary>
sealed class SquadWages(PopulationData data)
{
    /// <summary>
    /// Each CHARACTER record's <c>wages</c>, read once.
    /// </summary>
    readonly Dictionary<string, int> _wages = new(StringComparer.Ordinal);

    /// <summary>
    /// The squads seen by a pass so far (a squad first seen after the first pass is paid at once).
    /// </summary>
    readonly HashSet<int> _paidSquads = [];

    /// <summary>
    /// The game day of the last pass (<see cref="long.MinValue"/>: none yet).
    /// </summary>
    long _wageDay = long.MinValue;

    /// <summary>
    /// The pass (every trade check): on a new game day every squad's waged members pay, else only a squad first seen now that is not restored.
    /// </summary>
    internal void Pay(World world)
    {
        long day = (long)Math.Floor(world.GameHours / 24);
        bool first = _wageDay == long.MinValue, newDay = !first && day != _wageDay;
        _wageDay = day;

        foreach (var squad in world.Squads.All)
        {
            bool appeared = _paidSquads.Add(squad.Id);
            if (first || !newDay && (!appeared || squad.Restored)) continue;

            foreach (var id in squad.Members)
            {
                if (!world.Characters.TryResolveNext(id, out int s) || world.Characters.Cold(s) is not { } c || c.IsPlayer
                    || data.PlayerFaction >= 0 && c.Faction == data.PlayerFaction)
                {
                    continue;
                }

                if (!_wages.TryGetValue(c.RecordId, out int pay))
                    _wages[c.RecordId] = pay = data.Db.Find(c.RecordId) is { Type: Data.Fcs.FcsRecordType.Character } r ? r.GetInt("wages", 0) : 0;
                if (pay >= 1) squad.Money = pay;
            }
        }
    }
}
