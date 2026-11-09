using System.Numerics;

namespace Meitou.Simulation;

/// <summary>
/// A change one character asks of another entity (a hit, an item handed over, a relation change, a spawn), queued during the parallel Act phase and
/// applied in the serial commit (docs/simulation.md, "Threading" step 6). The meaning of <see cref="Kind"/>, <see cref="Amount"/>,
/// <see cref="Position"/> and <see cref="Arg"/> belongs to the system that emitted it.
/// </summary>
public struct Effect
{
    /// <summary>
    /// The slot the effect acts on (for a spawn, the spawner's own).
    /// </summary>
    public int Target;

    /// <summary>
    /// The slot that emitted it.
    /// </summary>
    public int Source;

    /// <summary>
    /// Index of the emitting system in the world's list (set by the world).
    /// </summary>
    public int System;

    /// <summary>
    /// Order among the effects one source emitted for one system in one tick (set by the buffer).
    /// </summary>
    public int Sequence;

    public int Kind;
    public float Amount;
    public Vector3 Position;
    public int Arg;
}

/// <summary>
/// Where a partition's Act phase puts its effects. Effects are ordered by (target, source, system, sequence), none of which depends on how the work
/// was partitioned or which thread ran it, so the commit applies them in the same order at any thread count. A source must emit all its effects
/// together (it does: a character is handled by one partition, in one go).
/// </summary>
public sealed class EffectBuffer
{
    /// <summary>
    /// The commit's order of the effects: target, then source, then system, then sequence.
    /// </summary>
    internal static readonly Comparison<Effect> Order = static (a, b) =>
    {
        int c = a.Target.CompareTo(b.Target);
        if (c != 0) return c;

        c = a.Source.CompareTo(b.Source);
        if (c != 0) return c;

        c = a.System.CompareTo(b.System);
        return c != 0 ? c : a.Sequence.CompareTo(b.Sequence);
    };

    readonly List<Effect> _items = [];
    int _lastSource = -1;
    int _sequence;

    internal int System { get; set; }

    internal List<Effect> Items => _items;

    public void Emit(int source, int target, int kind, float amount = 0, Vector3 position = default, int arg = 0)
    {
        _sequence = source == _lastSource ? _sequence + 1 : 0;
        _lastSource = source;
        _items.Add(new Effect
        {
            Target = target,
            Source = source,
            System = System,
            Sequence = _sequence,
            Kind = kind,
            Amount = amount,
            Position = position,
            Arg = arg,
        });
    }

    internal void Reset()
    {
        _items.Clear();
        _lastSource = -1;
        _sequence = 0;
    }

    /// <summary>
    /// Starts a new system's run over this buffer, so sequences restart even when the same source comes first again.
    /// </summary>
    internal void BeginSystem(int system)
    {
        System = system;
        _lastSource = -1;
        _sequence = 0;
    }
}
