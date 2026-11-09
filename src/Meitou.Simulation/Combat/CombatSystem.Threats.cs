using System.Numerics;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Perception;

namespace Meitou.Simulation.Combat;

public sealed partial class CombatSystem
{
    /// <summary>
    /// Self preservation's desire for a character (goal 32, AI slot 17 <c>FUN_140599d20</c>, <b>Verified</b>; docs/game/combat.md "Fight lifecycle
    /// and orders"): 1 while a threat stands within <see cref="RetaliationSystem.ImmediateReach"/> (Character <c>+0x2b0</c>, <c>iAmBeingMeleeAttacked
    /// immediate</c>), else 0.7 while one of positive aggression stands within <see cref="RetaliationSystem.SelfDefenceReach"/>, else 0. The threats
    /// are the senses' threats list (<see cref="SenseBlock.Threats"/>, <c>SensoryData</c> +0x50: characters of another faction sensed at the last
    /// look whose fight targets this one; each counts as of positive aggression) that still stand, at their places now; the immediate ones are those
    /// <see cref="ImmediateThreat"/> counts.
    /// <paramref name="centre"/> is the mean position of the threats within reach (the origin <c>Task_Runaway</c> flees from, <c>FUN_14034b440</c>).
    /// Reads the last tick's state, so any thread after Schedule.
    /// </summary>
    public float ThreatLevel(World world, int self, out Vector3 centre)
    {
        centre = default;
        var table = world.Characters;
        var prev = table.Previous;
        if ((uint)self >= (uint)prev.Length || table.Cold(self) is not { } me) return 0;

        float level = 0;
        int count = 0;
        var sum = Vector3.Zero;
        var here = prev[self].Position;
        float reach2 = RetaliationSystem.SelfDefenceReach * RetaliationSystem.SelfDefenceReach;
        float near2 = RetaliationSystem.ImmediateReach * RetaliationSystem.ImmediateReach;
        if (CharacterFacts.Senses(world, self) is not { } senses) return 0;

        foreach (var id in senses.Threats)
        {
            int a = id.Slot;
            if ((uint)a >= (uint)prev.Length || !prev[a].Alive || prev[a].Generation != id.Generation || _read[a].Down || table.Cold(a) is not { } c)
                continue;

            var at = prev[a].Position;
            var d = at - here;
            float d2 = d.X * d.X + d.Z * d.Z;
            if (d2 > reach2) continue;

            // The immediate flag also wants the threat free (not in a bed or cage, not carried) and not behind a closed gate (ImmediateThreat).
            bool immediate = d2 <= near2 && c.InSomething == global::Meitou.Simulation.Locks.HeldState.None && c.CarriedBy.IsNone
                && (Locks is not { } locks || locks.NotBlocked(world, here, at));
            level = MathF.Max(level, immediate ? 1 : Ai.SelfPreservation.Attacking);
            sum += at;
            count++;
        }

        if (count > 0) centre = sum / count;

        return level;
    }

    /// <summary>
    /// Whether a threat of the senses' list (<see cref="SenseBlock.Threats"/>, as of the last look) still stands, at any distance: Self preservation's list A
    /// <c>!159</c> does not hold (<c>FUN_140436720</c>, the threats count non-zero). Reads the last tick's state, so any thread after Schedule.
    /// </summary>
    public bool Threatened(World world, int self)
    {
        var prev = world.Characters.Previous;
        if (CharacterFacts.Senses(world, self) is not { } senses) return false;

        foreach (var id in senses.Threats)
            if ((uint)id.Slot < (uint)prev.Length && prev[id.Slot].Alive && prev[id.Slot].Generation == id.Generation && !_read[id.Slot].Down) return true;

        return false;
    }
}
