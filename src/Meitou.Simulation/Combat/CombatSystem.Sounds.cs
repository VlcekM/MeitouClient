using System.Numerics;

namespace Meitou.Simulation.Combat;

public sealed partial class CombatSystem
{
    /// <summary>Queues a sound-worthy happening for the host when it plays them (<see cref="World.Sounds"/>); display only.</summary>
    static void Sound(World world, SoundKind kind, int subject, int other, Vector3 at, bool heavy = false, bool unarmed = false, bool robot = false, bool armoured = false, bool fatal = false)
    {
        if (world.Sounds is not { } queue) return;
        queue.Enqueue(new SoundEvent(kind, Id(world, subject), Id(world, other), at, heavy, unarmed, robot, armoured, fatal));
    }

    /// <summary>The character in <paramref name="slot"/>, none for -1 (a bolt in the ground) or a slot past the table (a shooter gone since firing).</summary>
    static CharacterId Id(World world, int slot) =>
        slot >= 0 && slot < world.Characters.Previous.Length ? world.Characters.IdOf(slot) : CharacterId.None;
}