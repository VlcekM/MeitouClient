using System.Numerics;

namespace Meitou.Simulation.Movement;

/// <summary>
/// The speed caps of the characters in <see cref="SpeedMode.Group"/>: the player's speed groups, gathered once per tick (serial, in
/// <see cref="MovementSystem.Schedule"/>) and read by the parallel <see cref="MovementSystem.Move"/>, and the NPCs' slowest squad member.
/// </summary>
internal sealed class GroupSpeeds
{
    /// <summary>
    /// One speed group of this tick: its speed, its members' mean position and the sum of their velocities.
    /// </summary>
    readonly record struct SpeedGroupState(float Speed, Vector2 Centre, Vector2 Direction);

    /// <summary>
    /// The player's speed groups of this tick, by key (serial Schedule; read by the parallel Move).
    /// </summary>
    readonly Dictionary<int, SpeedGroupState> _speedGroups = [];

    /// <summary>
    /// <c>SpeedGroup::getSpeed</c> (<c>1407f5740</c>, docs/game/pathfinding.md "Speed groups"), once per tick: a group's speed is the lowest top
    /// speed of its members not down and not carried, its centre their mean position, its direction the sum of their velocities (last tick's).
    /// </summary>
    public void Gather(World world)
    {
        _speedGroups.Clear();
        var table = world.Characters;
        var prev = table.Previous;
        var acc = new Dictionary<int, (float Speed, Vector2 Sum, int N, Vector2 Dir)>();

        for (int s = 0; s < table.HighWater; s++)
        {
            if (!prev[s].Alive || table.Cold(s) is not { SpeedGroup: >= 0, RunSpeed: SpeedMode.Group } c || c.Medical is { Incapacitated: true }
                || !c.CarriedBy.IsNone)
            {
                continue;
            }

            if (!acc.TryGetValue(c.SpeedGroup, out var a)) a = (float.MaxValue, Vector2.Zero, 0, Vector2.Zero);

            acc[c.SpeedGroup] = (MathF.Min(a.Speed, prev[s].MaxSpeed), a.Sum + new Vector2(prev[s].Position.X, prev[s].Position.Z), a.N + 1,
                a.Dir + new Vector2(prev[s].Velocity.X, prev[s].Velocity.Z));
        }

        foreach (var (key, a) in acc) _speedGroups[key] = new SpeedGroupState(a.Speed, a.Sum / a.N, a.Dir);
    }

    /// <summary>
    /// The speed cap of a character in <see cref="SpeedMode.Group"/>: in a player's speed group, the group's speed, × max(0.8, 1 - 0.001 × its
    /// distance to the centre) for one ahead of the centre along the group's direction; otherwise (an NPC) the slowest of its squad
    /// (<see cref="SlowestOfSquad"/>).
    /// </summary>
    public float Of(World world, int slot, CharacterCold cold, Vector3 at)
    {
        if (cold.SpeedGroup < 0 || !_speedGroups.TryGetValue(cold.SpeedGroup, out var g))
            return SlowestOfSquad(world, cold);

        var off = new Vector2(at.X, at.Z) - g.Centre;
        return Vector2.Dot(off, g.Direction) > 0 ? g.Speed * MathF.Max(0.8f, 1 - 0.001f * off.Length()) : g.Speed;
    }

    /// <summary>
    /// The NPCs' group speed (<see cref="SpeedMode.Group"/>; the original makes speed groups for the player's selection only): the lowest top speed
    /// among the squad's members walking a path in that mode (last tick's, so the order the partitions run in does not matter).
    /// </summary>
    static float SlowestOfSquad(World world, CharacterCold cold)
    {
        var table = world.Characters;
        float slowest = float.MaxValue;
        if (cold.SquadId < 0 || world.Squads.Find(cold.SquadId) is not { } squad) return slowest;

        foreach (var member in squad.Members)
        {
            if (!table.IsAlive(member)) continue;

            ref readonly var p = ref table.Previous[member.Slot];
            if (p.Mode != SpeedMode.Group || ((MoveFlags)p.Flags & MoveFlags.HasPath) == 0
                || table.Cold(member.Slot)?.Medical is { Incapacitated: true })
            {
                continue;
            }

            slowest = MathF.Min(slowest, p.MaxSpeed);
        }

        return slowest;
    }
}
