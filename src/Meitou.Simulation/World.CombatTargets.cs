using Meitou.Simulation.Combat;

namespace Meitou.Simulation;

public sealed partial class World
{
    List<ICombatTargetRules>? combatTargetRules;
    internal bool HasCombatTargetRules => combatTargetRules is not null;
    void RegisterCombatTargetRules(ITickSystem system)
    {
        if (system is ICombatTargetRules rules) (combatTargetRules ??= []).Add(rules);
    }
    internal bool TryCombatTarget(CharacterId attacker, CharacterId target, bool picked, out CombatTargetOverride rule)
    {
        if (combatTargetRules is { } rules)
            foreach (var policy in rules)
                if (policy.TryTarget(this, attacker, target, picked, out rule)) return true;
        rule = default; return false;
    }
}
