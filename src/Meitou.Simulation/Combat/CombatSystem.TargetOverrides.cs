using System.Numerics;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation.Combat;

public sealed partial class CombatSystem
{
    bool TargetAllowed(World world, int attacker, int target, bool picked, bool native)
    {
        if (!world.TryCombatTarget(world.Characters.IdOf(attacker), world.Characters.IdOf(target), picked, out var rule)) return native;
        return TargetAllowed(world.Characters.Cold(target), rule);
    }
    static bool TargetAllowed(CharacterCold? victim, in CombatTargetOverride rule)
    {
        if (!rule.Allowed || victim is not { Medical: { Dead: false } medical, CarriedBy.IsNone: true, IsImprisoned: false }) return false;
        if (rule.Part is { } part && ((uint)part >= (uint)medical.Parts.Count || medical.Parts[part].Limb == LimbState.Stump)) return false;
        return !medical.Incapacitated || rule.AllowIncapacitated;
    }
    bool TargetOverride(World world, int attacker, in CombatSlot state, out CombatTargetOverride rule) =>
        state.TargetSlot >= 0 && world.Characters.IsAlive(new(state.TargetSlot, state.TargetGeneration))
            ? world.TryCombatTarget(world.Characters.IdOf(attacker), new(state.TargetSlot, state.TargetGeneration), state.Picked, out rule)
            : NoTarget(out rule);
    static bool NoTarget(out CombatTargetOverride rule) { rule = default; return false; }
    internal Vector3 TargetPoint(World world, int attacker, in CombatSlot state, Vector3 fallback) =>
        TargetOverride(world, attacker, state, out var rule) && rule.Allowed && rule.AimPoint is { } point
            && float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z) ? point : fallback;
    internal bool TargetsPart(World world, int attacker, in CombatSlot state) =>
        TargetOverride(world, attacker, state, out var rule) && rule.Part is not null;
    static float PartTargetGap(Vector3 from, Vector3 aim, CharacterCold attacker) =>
        MathF.Max(0, Vector3.Distance(from, aim) - 1.5f * CombatTuning.Footprint(attacker) * (attacker.Medical?.ScaleK ?? 1));
    bool StrikeTargetPart(World world, int attacker, in CombatSlot state, Zone zone, int blow, EffectBuffer effects)
    {
        if (!TargetOverride(world, attacker, state, out var rule) || rule.Part is null) return false;
        if (!TargetAllowed(world, attacker, state.TargetSlot, state.Picked, false)) return true;
        var point = TargetPoint(world, attacker, state, world.Characters.Previous[state.TargetSlot].Position);
        var delta = new Vector2(point.X, point.Z) - zone.Centre;
        float along = MathF.Max(0, MathF.Abs(Vector2.Dot(delta, zone.Forward)) - zone.Deep);
        float across = MathF.Max(0, MathF.Abs(delta.X * zone.Forward.Y - delta.Y * zone.Forward.X) - zone.Half);
        if (MathF.Abs(point.Y - zone.Y) <= zone.High && along * along + across * across <= 1)
            effects.Emit(attacker, state.TargetSlot, BlowEffect, state.AttackSeq, new Vector3(state.AttackTech, 0, 0), blow);
        return true;
    }
}
