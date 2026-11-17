namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// <see cref="Environment.TickCount64"/> as of the frame's start, for stamps taken tens of thousands of times a frame (a texture's last use:
/// <see cref="WorldTexture"/>): read from the system at each of them it was 1 ms of an 8 ms frame's render thread (a town, 47 000 traced object
/// parts and the drawn ones). A timer keeps it within a tenth of a second of the system's while no frame runs (a load), so what worker threads
/// stamp then is not taken for long unused.
/// </summary>
public static class FrameTick
{
    /// <summary>
    /// The milliseconds as last read.
    /// </summary>
    static long s_now = Environment.TickCount64;

    /// <summary>
    /// Reads the clock ten times a second, frames or not.
    /// </summary>
    static readonly Timer Keep = new(static _ => Advance(), null, 100, 100);

    /// <summary>
    /// The milliseconds clock, at most a frame (or a tenth of a second) old.
    /// </summary>
    public static long Now => Volatile.Read(ref s_now);

    /// <summary>
    /// Reads the clock: once a frame, on the render thread.
    /// </summary>
    public static void Advance()
    {
        Volatile.Write(ref s_now, Environment.TickCount64);
        GC.KeepAlive(Keep);
    }
}
