namespace Meitou.Simulation.Session;

/// <summary>
/// Lerp helpers for drawing between two tick states.
/// </summary>
public static class Interp
{
    /// <summary>
    /// The value <paramref name="t"/> of the way from <paramref name="a"/> to <paramref name="b"/>.
    /// </summary>
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>
    /// Wraps an angle in radians to (-pi, pi].
    /// </summary>
    public static float WrapAngle(float a)
    {
        a = MathF.IEEERemainder(a, MathF.Tau);
        return a <= -MathF.PI ? a + MathF.Tau : a;
    }

    /// <summary>
    /// Interpolates radians along the shortest arc (across +-pi too); the result is wrapped to (-pi, pi].
    /// </summary>
    public static float LerpAngle(float a, float b, float t) => WrapAngle(a + WrapAngle(b - a) * t);
}

/// <summary>
/// "Previous and current tick state, draw at alpha": the simulation calls <see cref="Push"/> once per tick, the renderer calls
/// <see cref="At"/> with <see cref="FixedStepClock.Alpha"/>.
/// </summary>
public sealed class Interpolated<T>(T initial, Func<T, T, float, T> lerp)
{
    /// <summary>
    /// The previous tick's state.
    /// </summary>
    public T Previous { get; private set; } = initial;
    /// <summary>
    /// The current tick's state.
    /// </summary>
    public T Current { get; private set; } = initial;

    /// <summary>
    /// Makes <paramref name="next"/> the current state; the old current becomes the previous one.
    /// </summary>
    public void Push(T next)
    {
        Previous = Current;
        Current = next;
    }

    /// <summary>
    /// Sets both states (a teleport or cut: nothing to blend).
    /// </summary>
    public void Snap(T state) => Previous = Current = state;

    /// <summary>
    /// The state <paramref name="alpha"/> of the way from the previous to the current one.
    /// </summary>
    public T At(float alpha) => lerp(Previous, Current, alpha);
}