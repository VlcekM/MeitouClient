namespace Meitou.Simulation.Combat;

/// <summary>
/// The code mods' combat moves in one world (docs/modding.md "Combat moves"), asked in the animation's parallel step. A source that throws, or
/// answers a move without a technique or with a progress that is not a finite number, faults at that tick: it still answers the rest of that tick
/// and answers nothing from the next tick on, so the cut falls on a tick boundary whatever the threads did. The faults are kept per world and
/// hashed; the first is logged with the mod's id.
/// </summary>
sealed class ModCombatMoves(IReadOnlyList<ModCombatMove> sources)
{
    /// <summary>
    /// Each source's fault tick (<see cref="long.MaxValue"/>: none), in <c>sources</c>' order.
    /// </summary>
    readonly long[] _faults = [.. sources.Select(_ => long.MaxValue)];

    /// <summary>
    /// The first answer of a source that has not faulted before <paramref name="tick"/>, its progress clamped to 0..1; null when none answers.
    /// </summary>
    public CombatMove? Move(World world, CharacterId id, long tick)
    {
        for (int i = 0; i < sources.Count; i++)
        {
            if (Volatile.Read(ref _faults[i]) < tick) continue;

            CombatMove? move;
            try
            {
                move = sources[i].Source(world, id, tick);
            }
            catch (Exception e)
            {
                Fault(i, tick, e);
                continue;
            }

            if (move is not { } m) continue;
            if (m.Technique is null || !float.IsFinite(m.Progress))
            {
                Fault(i, tick, new InvalidOperationException("A combat move needs a technique and a finite progress."));
                continue;
            }

            return m with { Progress = Math.Clamp(m.Progress, 0, 1) };
        }

        return null;
    }

    /// <summary>
    /// Records source <paramref name="i"/>'s fault at <paramref name="tick"/> (the earliest wins; threads of one tick all write the same tick) and
    /// logs the first.
    /// </summary>
    void Fault(int i, long tick, Exception e)
    {
        long seen = Volatile.Read(ref _faults[i]);
        while (tick < seen)
        {
            long was = Interlocked.CompareExchange(ref _faults[i], tick, seen);
            if (was == seen)
            {
                if (seen == long.MaxValue)
                {
                    Console.Error.WriteLine($"mod       {sources[i].Owner}: combat move failed at tick {tick}, dropped from the next tick: {e}");
                    Content.CodeModFaults.Tell(sources[i].Owner, e);
                }

                return;
            }

            seen = was;
        }
    }

    /// <summary>
    /// Adds the faulted sources and their ticks to <paramref name="hasher"/>; nothing while none has faulted.
    /// </summary>
    public void Hash(ref StateHasher hasher)
    {
        for (int i = 0; i < _faults.Length; i++)
        {
            if (_faults[i] == long.MaxValue) continue;

            hasher.Add(i);
            hasher.Add(_faults[i]);
        }
    }
}
