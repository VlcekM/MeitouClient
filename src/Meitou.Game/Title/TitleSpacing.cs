using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Combat;

namespace Meitou.Game.Title;

/// <summary>
/// Keeps each pair of the title's road fight apart: its world has no fight footwork (the pursuit's band and repulsion), so blows and lunges carry the
/// two into each other, and facing a target a hair away turned them round every tick. At the end of each tick it pushes an overlapping pair back
/// apart, each half the way, along its last good axis. Slots 2p and 2p + 1 are pair p (<see cref="TitleBattle"/>).
/// </summary>
sealed class TitleSpacing(int pairs) : ITickSystem
{
    /// <summary>
    /// The gap kept between the two bodies' radii: the fight steering's band never closes below it (<see cref="CombatSteering.Band"/>).
    /// </summary>
    const float MinGap = 1;

    /// <summary>
    /// The squared length below which a pair's offset gives no direction.
    /// </summary>
    const float Degenerate = 1e-4f;

    /// <summary>
    /// Each pair's last direction from its first fighter to its second (they are spawned along +Z).
    /// </summary>
    readonly Vector2[] _axes = Enumerable.Repeat(Vector2.UnitY, pairs).ToArray();

    /// <summary>
    /// Pushes every pair that came closer than its radii and <see cref="MinGap"/> back to that distance.
    /// </summary>
    public void SlowWorld(World world)
    {
        var table = world.Characters;
        for (int p = 0; p < _axes.Length && 2 * p + 1 < table.HighWater; p++)
            Separate(world, 2 * p, 2 * p + 1, ref _axes[p]);
    }

    /// <summary>
    /// Separates the fighters in slots <paramref name="a"/> and <paramref name="b"/>, remembering their axis while it is well defined.
    /// </summary>
    static void Separate(World world, int a, int b, ref Vector2 axis)
    {
        var next = world.Characters.Next;
        var offset = new Vector2(next[b].Position.X - next[a].Position.X, next[b].Position.Z - next[a].Position.Z);
        float distance = offset.Length();
        if (distance * distance > Degenerate) axis = offset / distance;

        float min = CombatSteering.Radius(world.Characters.Cold(a)) + CombatSteering.Radius(world.Characters.Cold(b)) + MinGap;
        if (distance >= min) return;

        var push = axis * ((min - distance) / 2);
        Shift(world, a, -push);
        Shift(world, b, push);
    }

    /// <summary>
    /// Moves the fighter in <paramref name="slot"/> by <paramref name="by"/> on the ground.
    /// </summary>
    static void Shift(World world, int slot, Vector2 by)
    {
        ref var n = ref world.Characters.Next[slot];
        float x = n.Position.X + by.X;
        float z = n.Position.Z + by.Y;
        n.Position = new Vector3(x, world.Walkability.GroundHeight(x, z, n.Position.Y), z);
    }
}
