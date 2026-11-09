using System.Numerics;

namespace Meitou.Simulation.Combat;

public sealed partial class CombatSystem
{
    /// <summary>
    /// The splash a hit leaves on the ground (<c>FUN_140793b20</c>, docs/game/blood-decals.md): flung along the blow's <paramref name="direction"/>
    /// (X/Z) turned by up to 50 degrees either way, up to 20 units from the victim, sized by the damage (cut + blunt + pierce over 100, between 0.05
    /// and 0.5). None in the water or from a bloodless race. Display only (<see cref="World.BloodMarks"/>).
    /// </summary>
    static void Splash(World world, int victim, CharacterCold vc, Vector2 direction, float damage)
    {
        if (world.BloodMarks is not { } marks || vc.WaterState != 0 || vc.Race is not { BleedRate: > 0 } race) return;

        var id = world.Characters.IdOf(victim);
        var prev = world.Characters.Previous;
        if (direction.LengthSquared() < 1e-6f)
            direction = new Vector2(MathF.Sin(prev[victim].Yaw), MathF.Cos(prev[victim].Yaw));

        direction = Vector2.Normalize(direction);
        ulong counter = ~(ulong)world.Tick * 2;
        float turn = (Rng.Float(Rng.Hash(world.Seed, Rng.Key(id), RngPurpose.BloodTrail, counter)) * 2 - 1) * (50 * MathF.PI / 180);
        float far = Rng.Float(Rng.Hash(world.Seed, Rng.Key(id), RngPurpose.BloodTrail, counter + 1)) * 20;
        var (s, c) = MathF.SinCos(turn);
        direction = new Vector2(direction.X * c + direction.Y * s, direction.Y * c - direction.X * s);

        var at = prev[victim].Position + new Vector3(direction.X, 0, direction.Y) * far;
        marks.Enqueue(new BloodMark(BloodMarkKind.Splash, id, at, direction, Math.Clamp(MathF.Min(damage / 100, 1), 0.05f, 0.5f), race.BloodColour));
    }
}
