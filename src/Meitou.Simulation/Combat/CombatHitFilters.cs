namespace Meitou.Simulation.Combat;

/// <summary>
/// The world's systems that filter hits before they land (<see cref="ICombatHitFilter"/>).
/// </summary>
sealed class CombatHitFilters
{
    /// <summary>
    /// The filtering systems, each with its index and whether it is still asked.
    /// </summary>
    List<(ICombatHitFilter Filter, int SystemIndex, bool Enabled)>? _filters;
    /// <summary>
    /// The filters still asked, and the ones disabled after a failure.
    /// </summary>
    int _active, _faulted;

    /// <summary>
    /// Whether any filter is asked; a hit site can skip building the payload without one.
    /// </summary>
    internal bool Active => _active > 0;

    /// <summary>
    /// Registers <paramref name="system"/> when it is a filter.
    /// </summary>
    internal void Register(ITickSystem system, int systemIndex)
    {
        if (system is not ICombatHitFilter filter) return;

        (_filters ??= []).Add((filter, systemIndex, true));
        _active++;
    }

    /// <summary>
    /// Adds the disabled filters to the world's state hash: a failed filter no longer changes hits. Working filters add nothing.
    /// </summary>
    internal void HashFaults(ref StateHasher hash)
    {
        if (_faulted == 0) return;

        hash.Add(0x48495446);
        hash.Add(_faulted);
        foreach (var entry in _filters!)
        {
            if (!entry.Enabled) hash.Add(entry.SystemIndex);
        }
    }

    /// <summary>
    /// Asks the filters about a hit about to land at the world's tick, in system order, until one cancels it; <paramref name="packet"/> comes back
    /// as they left it. False when the hit is cancelled. Nothing, not even the payload, without a filter. Serial phases only.
    /// </summary>
    internal bool Pass(World world, CombatHitSource source, CharacterId attacker, CharacterId victim, int sequence, int blow, bool intended,
        ref DamagePacket packet)
    {
        if (_active == 0) return true;

        var hit = new PendingHit
        {
            Tick = world.Tick, Source = source, Attacker = attacker, Victim = victim, Sequence = sequence, Blow = blow, Intended = intended,
            Packet = packet,
        };
        var filters = _filters!;
        for (int i = 0; i < filters.Count && !hit.Cancelled; i++)
        {
            if (filters[i].Enabled) hit = Ask(world, i, hit);
        }

        packet = hit.Packet;
        return !hit.Cancelled;
    }

    /// <summary>
    /// One filter's answer: the hit as it came with the filter's packet and cancel (a filter changes nothing else, even by replacing the whole
    /// struct); as it came when the filter throws or changes a packet value to one that is not finite, or a damage, stun or bleed value to one below
    /// 0 (armour penetration may be negative, as some weapons' is), which disables it.
    /// </summary>
    PendingHit Ask(World world, int i, PendingHit before)
    {
        var entry = _filters![i];
        var hit = before;
        string? fault;
        try
        {
            entry.Filter.HitLanding(world, ref hit);
            fault = Valid(hit.Packet, before.Packet) ? null : $"packet {hit.Packet} has a changed value not finite or below 0";
        }
        catch (Exception e)
        {
            fault = $"{e.GetType().Name}: {e.Message}";
        }

        if (fault is null) return before with { Packet = hit.Packet, Cancelled = hit.Cancelled };

        _filters[i] = (entry.Filter, entry.SystemIndex, false);
        _active--;
        _faulted++;
        LogFault(entry.Filter, fault);
        return before;
    }

    /// <summary>
    /// Whether every value the filter changed is finite and, but for armour penetration, not negative; an unchanged value is the engine's own.
    /// </summary>
    static bool Valid(in DamagePacket p, in DamagePacket was) =>
        Fine(p.Cut, was.Cut) && Fine(p.Blunt, was.Blunt) && Fine(p.Pierce, was.Pierce) && Fine(p.Stun, was.Stun)
        && Fine(p.BleedMultiplier, was.BleedMultiplier) && (p.ArmourPenetration.Equals(was.ArmourPenetration) || float.IsFinite(p.ArmourPenetration));

    /// <summary>
    /// An unchanged value, or a finite one that is not negative.
    /// </summary>
    static bool Fine(float v, float was) => v.Equals(was) || float.IsFinite(v) && v >= 0;

    /// <summary>
    /// A disabled filter's log line: the mod and the name it registered the system under, as the mods' other log lines; else its type.
    /// </summary>
    static void LogFault(ICombatHitFilter filter, string fault)
    {
        if (SimulationExtensions.FactoryOf(filter) is { } factory)
            Console.Error.WriteLine($"mod       {factory.Owner}: hit filter '{factory.Name}' disabled for this world: {fault}");
        else
            Console.Error.WriteLine($"hit filter '{filter.GetType().FullName}' disabled for this world: {fault}");
    }
}
