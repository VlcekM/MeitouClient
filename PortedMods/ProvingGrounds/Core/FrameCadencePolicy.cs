namespace ProvingGrounds.Core;

/// <summary>Runs work every <c>intervalSec</c> of frame time. C++: FrameCadencePolicy.h.</summary>
public static class FrameCadencePolicy
{
    // C++: FrameCadencePolicy.h:7-10
    public static void Reset(ref float elapsedSec)
    {
        elapsedSec = 0.0f;
    }

    /// <summary>Adds a positive <paramref name="dtSec"/>; returns true (and keeps the remainder, std::fmod) once the interval is
    /// reached. A non-positive interval always fires and leaves <paramref name="elapsedSec"/> untouched.</summary>
    // C++: FrameCadencePolicy.h:12-24
    public static bool Advance(ref float elapsedSec, float dtSec, float intervalSec)
    {
        if (intervalSec <= 0.0f)
            return true;

        if (dtSec > 0.0f)
            elapsedSec += dtSec;
        if (elapsedSec < intervalSec)
            return false;

        elapsedSec = elapsedSec % intervalSec;   // std::fmod(float, float) = fmodf: C# float % is the same IEEE remainder-by-truncation
        return true;
    }
}
