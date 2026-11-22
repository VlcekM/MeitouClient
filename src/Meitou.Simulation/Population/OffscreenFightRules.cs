using Meitou.Simulation.Factions;

namespace Meitou.Simulation.Population;

/// <summary>
/// The world's systems asked about off-screen fights (<see cref="IOffscreenFightRule"/>).
/// </summary>
sealed class OffscreenFightRules
{
    /// <summary>
    /// The rules, each with its system index and whether it is still asked.
    /// </summary>
    List<(IOffscreenFightRule Rule, int SystemIndex, bool Enabled)>? _rules;
    /// <summary>
    /// The rules still asked, and the ones disabled after a failure.
    /// </summary>
    int _active, _faulted;

    /// <summary>
    /// Whether any rule is asked; the fights skip asking without one.
    /// </summary>
    internal bool Active => _active > 0;

    /// <summary>
    /// Registers <paramref name="system"/> when it is a rule.
    /// </summary>
    internal void Register(ITickSystem system, int systemIndex)
    {
        if (system is not IOffscreenFightRule rule) return;

        (_rules ??= []).Add((rule, systemIndex, true));
        _active++;
    }

    /// <summary>
    /// Adds the disabled rules to the world's state hash: a failed rule is no longer asked. Working rules add nothing.
    /// </summary>
    internal void HashFaults(ref StateHasher hash)
    {
        if (_faulted == 0) return;

        hash.Add(0x4F464654);
        hash.Add(_faulted);
        foreach (var entry in _rules!)
        {
            if (!entry.Enabled) hash.Add(entry.SystemIndex);
        }
    }

    /// <summary>
    /// The rules' joint answer about <paramref name="first"/> meeting <paramref name="second"/>: <see cref="OffscreenFightVerdict.Skip"/> when any
    /// rule skips or either platoon left the world while they were asked; else the winner the rules name when they agree; else
    /// <see cref="OffscreenFightVerdict.Fight"/>. Every enabled rule is asked, so the answer does not depend on their order. Serial phases only.
    /// </summary>
    internal OffscreenFightVerdict Judge(World world, Platoon first, Platoon second)
    {
        var rules = _rules!;
        bool skip = false;
        var named = OffscreenFightVerdict.Fight;
        bool disagree = false;
        for (int i = 0; i < rules.Count; i++)
        {
            if (!rules[i].Enabled) continue;
            if (Ask(world, i, first, second) is not { } verdict) continue;

            if (verdict == OffscreenFightVerdict.Skip) skip = true;
            else if (verdict != OffscreenFightVerdict.Fight && named != verdict)
            {
                disagree |= named != OffscreenFightVerdict.Fight;
                named = verdict;
            }
        }

        bool gone = world.Platoons.Find(first.Id) != first || world.Platoons.Find(second.Id) != second;
        if (skip || gone) return OffscreenFightVerdict.Skip;

        return disagree ? OffscreenFightVerdict.Fight : named;
    }

    /// <summary>
    /// Rule <paramref name="index"/>'s answer; null, the rule disabled, when it throws, answers outside the verdicts, or adds or removes a platoon.
    /// </summary>
    OffscreenFightVerdict? Ask(World world, int index, Platoon first, Platoon second)
    {
        var entry = _rules![index];
        string? fault = null;
        var verdict = OffscreenFightVerdict.Fight;
        int platoons = world.Platoons.Count;
        try
        {
            verdict = entry.Rule.Judge(world, first, second);
            if (verdict > OffscreenFightVerdict.SecondWins) fault = $"it answered {(int)verdict}, which is no verdict";
            else if (!Kept(world, platoons, first, second)) fault = "it added or removed a platoon";
        }
        catch (Exception e)
        {
            fault = $"{e.GetType().Name}: {e.Message}";
            SimulationExtensions.TellOutdated(entry.Rule, e);
        }

        if (fault is null) return verdict;

        _rules[index] = (entry.Rule, entry.SystemIndex, false);
        _active--;
        _faulted++;
        LogFault(entry.Rule, fault);
        return null;
    }

    /// <summary>
    /// Whether the world still has <paramref name="count"/> platoons, <paramref name="first"/> and <paramref name="second"/> among them.
    /// </summary>
    static bool Kept(World world, int count, Platoon first, Platoon second) =>
        world.Platoons.Count == count && world.Platoons.Find(first.Id) == first && world.Platoons.Find(second.Id) == second;

    /// <summary>
    /// A disabled rule's log line: the mod and the name it registered the system under, as the mods' other log lines; else its type.
    /// </summary>
    static void LogFault(IOffscreenFightRule rule, string fault)
    {
        if (SimulationExtensions.FactoryOf(rule) is { } factory)
            Console.Error.WriteLine($"mod       {factory.Owner}: off-screen fight rule '{factory.Name}' disabled for this world: {fault}");
        else
            Console.Error.WriteLine($"off-screen fight rule '{rule.GetType().FullName}' disabled for this world: {fault}");
    }
}
