using MoonSharp.Interpreter;

namespace Meitou.Lua;

/// <summary>
/// Deterministic per-mod xorshift64* generator behind a sandbox's <c>math.random</c> and <c>math.randomseed</c>.
/// </summary>
internal sealed class LuaRandom
{
    /// <summary>
    /// The generator state used when the seed is 0, which xorshift cannot run on.
    /// </summary>
    const ulong ZeroSeedState = 0x9E3779B97F4A7C15UL;

    /// <summary>
    /// The xorshift64* output multiplier.
    /// </summary>
    const ulong OutputMultiplier = 0x2545F4914F6CDD1DUL;

    /// <summary>
    /// The generator state.
    /// </summary>
    ulong _state;

    /// <summary>
    /// Starts the generator from <paramref name="seed"/> (0 selects a fixed state).
    /// </summary>
    public LuaRandom(ulong seed)
    {
        _state = seed == 0 ? ZeroSeedState : seed;
    }

    /// <summary>
    /// Lua's <c>math.randomseed</c>: reseeds from the first argument when it is a number, else from 1.
    /// </summary>
    public DynValue Seed(CallbackArguments a)
    {
        long s = a.Count > 0 && a[0].Type == DataType.Number ? (long)a[0].Number : 1;
        _state = unchecked((ulong)s) ^ ZeroSeedState;
        if (_state == 0) _state = 1;

        return DynValue.Nil;
    }

    /// <summary>
    /// Lua's <c>math.random</c>: a float in [0, 1), an integer in [1, m], or an integer in [m, n].
    /// </summary>
    public DynValue Next(CallbackArguments a)
    {
        double u = NextUnitDouble();
        if (a.Count == 0) return DynValue.NewNumber(u);
        if (a.Count == 1) return DynValue.NewNumber(UpTo((long)a[0].Number, u));

        long lo = (long)a[0].Number;
        long max = (long)a[1].Number;
        if (max < lo) throw new ScriptRuntimeException("interval is empty");

        return DynValue.NewNumber(lo + (long)(u * (max - lo + 1)));
    }

    /// <summary>
    /// The integer in [1, <paramref name="hi"/>] that the unit value <paramref name="u"/> picks.
    /// </summary>
    static double UpTo(long hi, double u)
    {
        if (hi < 1) throw new ScriptRuntimeException("interval is empty");

        return 1 + (long)(u * hi);
    }

    /// <summary>
    /// Advances the state and returns a double in [0, 1).
    /// </summary>
    double NextUnitDouble()
    {
        ulong x = _state;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        _state = x;

        return ((x * OutputMultiplier) >> 11) * (1.0 / (1UL << 53));
    }
}
