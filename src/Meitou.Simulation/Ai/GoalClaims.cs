using Meitou.Simulation.Factions;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The squads' goal list kept across ticks: an entry (worker, sub-target) is refreshed to <see cref="Seconds"/> by every update of the duty that
/// makes it (<c>FUN_140269ff0</c> from the first aid task's update <c>FUN_14034ef20</c> and <c>FUN_1403346d0</c>) and removed when its timer runs out
/// (<c>Platoon::update</c> <c>FUN_14026a320</c>, Verified), so a claim outlives its duty by up to 3 s. <see cref="World.Claims"/> refreshes it once a
/// tick from the sources (<see cref="SquadGoals.Of"/>). Derived from the duties, not hashed or saved.
/// </summary>
public sealed class GoalClaims
{
    /// <summary>
    /// The timer an entry is given, <c>0x40400000</c>.
    /// </summary>
    public const float Seconds = 3;

    readonly Dictionary<(CharacterId Worker, GoalTarget Target), (int Squad, long Expires)> _map = [];
    readonly List<(CharacterId Worker, GoalTarget Target)> _expired = [];
    long _ticks = 90;

    internal void Configure(float tickSeconds) => _ticks = (long)MathF.Ceiling(Seconds / tickSeconds);

    internal long Now { get; set; }

    /// <summary>
    /// Gives the entry a full timer (made when absent).
    /// </summary>
    internal void Refresh(int squad, CharacterId worker, GoalTarget target) => _map[(worker, target)] = (squad, Now + _ticks);

    /// <summary>
    /// The entries whose timer has not run out at <paramref name="tick"/>; the others are dropped.
    /// </summary>
    internal IEnumerable<(int Squad, CharacterId Worker, GoalTarget Target)> Live(long tick)
    {
        _expired.Clear();
        foreach (var (key, value) in _map)
            if (value.Expires < tick) _expired.Add(key);
        foreach (var key in _expired) _map.Remove(key);
        foreach (var (key, value) in _map) yield return (value.Squad, key.Worker, key.Target);
    }

    public int Count => _map.Count;
}