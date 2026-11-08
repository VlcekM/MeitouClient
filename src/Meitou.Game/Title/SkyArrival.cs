using System.Numerics;
using Meitou.Rendering;

namespace Meitou.Game;

/// <summary>
/// Presentation-only flight through the sky; the simulation clock and camera rig never change. From the title the hour runs forward to the
/// destination's, then a hold; in play, or when the hour is near, the view climbs at once, the hour running on as it goes. The view pans
/// up to the sky and waits there for the load; the destination fades in from black with the camera high over it, which comes down orbiting onto the view.
/// </summary>
sealed class SkyArrival(WorldCamera source, float hour, float speed = 1, bool immediate = false)
{
    internal const double HoldSeconds = 0.5,AscentSeconds = 3.4, DescentSeconds = 3, FadeOutSeconds = 0.3, FadeInSeconds = 1.4;
    internal const float MinSpeed = 0.25f, MaxSpeed = 4;
    internal const float SkyPitch = -1.45f;
    /// <summary>How far above its view the destination's camera starts, so nothing near blocks the first look; and how far it orbits round on the way down.</summary>
    internal const float ArrivalHeight = 300, ArrivalSweep = 1.2f;
    /// <summary>The most hours the run forward may take for the title to climb at once, without the hold.</summary>
    internal const float NearHours = 3;
    enum Phase { Turn, Hold, Ascent, Stars, Arriving }
    readonly Vector3 sourceEye = source.Eye;
    readonly float sourceYaw = source.Yaw, sourcePitch = source.Pitch, sourceDistance = source.Distance, sourceFov = source.FieldOfView;
    readonly float sourceHour = hour;
    readonly float flightSpeed = float.IsFinite(speed) ? Math.Clamp(speed, MinSpeed, MaxSpeed) : 1;
    float? destinationHour;
    Phase phase = immediate ? Phase.Ascent : Phase.Turn;
    double elapsed, turnElapsed;
    bool covering;

    public bool AtStars => phase >= Phase.Stars;
    public bool Finished => phase == Phase.Arriving && elapsed >= DescentSeconds && Fade == 0;
    public bool Arriving => phase == Phase.Arriving;
    /// <summary>How much of the frame is black, 0..1: drawn over the world while it warms up under it and as it fades in.</summary>
    public float Fade { get; private set; }
    bool Near => destinationHour is { } to && ForwardHours(sourceHour, to) <= NearHours;

    /// <summary>The hour the flight runs forward to before it climbs; until it is known (a save still being read) the view holds at the source's.</summary>
    public void SetDestination(float destination)
    {
        if (destinationHour is null) destinationHour = destination;
    }

    /// <summary>Seconds of the hour's run forward: the longer the way round, the longer it takes (a whole day 4.8 s at speed 1).</summary>
    double TurnSeconds => destinationHour is { } to && ForwardHours(sourceHour, to) is var hours and > 0.05f ? 0.8 + hours / 6 : 0;

    public void Advance(double seconds)
    {
        seconds = Math.Max(0, seconds);
        if (destinationHour is not null) turnElapsed += seconds * flightSpeed;
        Fade = covering ? Math.Min(1, Fade + (float)(seconds * flightSpeed / FadeOutSeconds))
            : phase == Phase.Arriving ? Math.Max(0, Fade - (float)(seconds * flightSpeed / FadeInSeconds)) : Fade;
        // The hold is wall-clock time whatever the speed.
        elapsed += phase == Phase.Hold ? seconds : seconds * flightSpeed;
        switch (phase)
        {
            case Phase.Turn when destinationHour is null: elapsed = 0; break;
            case Phase.Turn when Near: (phase, elapsed) = (Phase.Ascent, 0); break;
            case Phase.Turn when turnElapsed >= TurnSeconds: (phase, elapsed) = (Phase.Hold, 0); break;
            case Phase.Hold when elapsed >= HoldSeconds: (phase, elapsed) = (Phase.Ascent, 0); break;
            case Phase.Ascent when elapsed >= AscentSeconds: (phase, elapsed) = (Phase.Stars, 0); break;
        }
    }

    /// <summary>A new game: straight up at the source's hour, no hold or run of the hour (the start hour comes in under the black).</summary>
    public void ClimbAtOnce() => (destinationHour, phase, elapsed) = (sourceHour, Phase.Ascent, 0);

    /// <summary>The load is done and the destination warms up under the flight: the sky fades to black over it.</summary>
    public void Cover() => covering = true;

    public void Arrive() { phase = Phase.Arriving; elapsed = 0; covering = false; }

    public void Skip()
    {
        if (phase == Phase.Arriving) (elapsed, Fade) = (DescentSeconds, 0);
        else if (phase < Phase.Stars) (phase, elapsed, turnElapsed) = (Phase.Stars, 0, double.MaxValue);
    }

    /// <summary>Places the camera for this frame and returns the hour to light it with. <paramref name="destinationReady"/>: the destination warms up under
    /// the black, seen from where the descent lands (what streams and is waited for is what the view will show).</summary>
    public float Apply(WorldCamera camera, float destination, bool destinationReady = false)
    {
        float to = destinationHour ?? destination;
        if (destinationReady && phase != Phase.Arriving) return destination;
        if (phase == Phase.Arriving)
        {
            float t = Ease((float)(elapsed / DescentSeconds), 0.3f, 0.2f);
            // Orbit round the view's target onto its heading while coming down from above it, the look tipping from the sky to the view's pitch.
            float yaw = camera.Yaw - ArrivalSweep * (1 - t), pitch = camera.Pitch;
            var eye = camera.Target + new Vector3(MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch), MathF.Cos(pitch) * MathF.Cos(yaw)) * camera.Distance
                + Vector3.UnitY * ArrivalHeight * (1 - t);
            Place(camera, eye, yaw, float.Lerp(SkyPitch, pitch, t), camera.Distance, float.Lerp(sourceFov, camera.FieldOfView, t));
            return destination;
        }
        float lift = phase switch { Phase.Ascent => Ease((float)(elapsed / AscentSeconds)), Phase.Stars => 1, _ => 0 };
        Place(camera, sourceEye, sourceYaw, float.Lerp(sourcePitch, SkyPitch, lift), sourceDistance, sourceFov);
        return destinationHour is null ? sourceHour : ForwardHour(sourceHour, to, Ease(TurnSeconds > 0 ? (float)(turnElapsed / TurnSeconds) : 1, 0.5f, 0.5f));
    }

    // Descent holds its cruising speed longer and brakes briefly at the end; ascent uses symmetric ramps.
    // Integrating smoothstep velocity keeps acceleration continuous through each join and at rest.
    static float Ease(float t, float startRamp = 0.4f, float endRamp = 0.4f)
    {
        t = Math.Clamp(t, 0, 1);
        float area = 1 - (startRamp + endRamp) * 0.5f;
        static float RampDistance(float u) => u * u * u * (1 - u * 0.5f);
        if (t < startRamp) return startRamp * RampDistance(t / startRamp) / area;
        if (t > 1 - endRamp) return 1 - endRamp * RampDistance((1 - t) / endRamp) / area;
        return (t - startRamp * 0.5f) / area;
    }
    static float ForwardHours(float from, float to) => (to - from + 24) % 24;
    internal static float ForwardHour(float from, float to, float t) =>
        (from + ForwardHours(from, to) * Math.Clamp(t, 0, 1)) % 24;

    internal static void Place(WorldCamera camera, Vector3 eye, float yaw, float pitch, float distance, float fov)
    {
        camera.Yaw = yaw; camera.Pitch = pitch; camera.Distance = distance; camera.FieldOfView = fov;
        camera.Target = eye + camera.Forward * distance;
    }
}
