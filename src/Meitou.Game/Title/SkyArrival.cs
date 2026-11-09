using System.Numerics;
using Meitou.Game.Rendering.Frame;

namespace Meitou.Game.Title;

/// <summary>
/// Presentation-only flight through the sky; the simulation clock and camera rig never change. From the title the hour runs forward to the
/// destination's, then a hold; in play, or when the hour is near, the view climbs at once, the hour running on as it goes. The view pans up to the
/// sky and waits there for the load; the destination fades in from black with the camera high over it, which comes down orbiting onto the view.
/// </summary>
sealed class SkyArrival(WorldCamera source, float hour, float speed = 1, bool immediate = false)
{
    internal const double HoldSeconds = 0.5, AscentSeconds = 3.4, DescentSeconds = 3, FadeOutSeconds = 0.3, FadeInSeconds = 1.4;
    internal const float MinSpeed = 0.25f, MaxSpeed = 4;
    internal const float SkyPitch = -1.45f;

    /// <summary>
    /// How far above its view the destination's camera starts, so nothing near blocks the first look; and how far it orbits round on the way down.
    /// </summary>
    internal const float ArrivalHeight = 300, ArrivalSweep = 1.2f;

    /// <summary>
    /// The most hours the run forward may take for the title to climb at once, without the hold.
    /// </summary>
    internal const float NearHours = 3;

    enum Phase { Turn, Hold, Ascent, Stars, Arriving }

    readonly Vector3 _sourceEye = source.Eye;
    readonly float _sourceYaw = source.Yaw, _sourcePitch = source.Pitch, _sourceDistance = source.Distance, _sourceFov = source.FieldOfView;
    readonly float _sourceHour = hour;
    readonly float _flightSpeed = float.IsFinite(speed) ? Math.Clamp(speed, MinSpeed, MaxSpeed) : 1;
    float? _destinationHour;
    Phase _phase = immediate ? Phase.Ascent : Phase.Turn;
    double _elapsed, _turnElapsed;
    bool _covering;

    public bool AtStars => _phase >= Phase.Stars;
    public bool Finished => _phase == Phase.Arriving && _elapsed >= DescentSeconds && Fade == 0;
    public bool Arriving => _phase == Phase.Arriving;

    /// <summary>
    /// How much of the frame is black, 0..1: drawn over the world while it warms up under it and as it fades in.
    /// </summary>
    public float Fade { get; private set; }

    bool Near => _destinationHour is { } to && ForwardHours(_sourceHour, to) <= NearHours;

    /// <summary>
    /// The hour the flight runs forward to before it climbs; until it is known (a save still being read) the view holds at the source's.
    /// </summary>
    public void SetDestination(float destination)
    {
        if (_destinationHour is null) _destinationHour = destination;
    }

    /// <summary>
    /// Seconds of the hour's run forward: the longer the way round, the longer it takes (a whole day 4.8 s at speed 1).
    /// </summary>
    double TurnSeconds => _destinationHour is { } to && ForwardHours(_sourceHour, to) is var hours and > 0.05f ? 0.8 + hours / 6 : 0;

    public void Advance(double seconds)
    {
        seconds = Math.Max(0, seconds);
        if (_destinationHour is not null) _turnElapsed += seconds * _flightSpeed;

        Fade = _covering
            ? Math.Min(1, Fade + (float)(seconds * _flightSpeed / FadeOutSeconds))
            : _phase == Phase.Arriving
                ? Math.Max(0, Fade - (float)(seconds * _flightSpeed / FadeInSeconds))
                : Fade;

        // The hold is wall-clock time whatever the speed.
        _elapsed += _phase == Phase.Hold ? seconds : seconds * _flightSpeed;
        switch (_phase)
        {
            case Phase.Turn when _destinationHour is null:
                _elapsed = 0;
                break;
            case Phase.Turn when Near:
                (_phase, _elapsed) = (Phase.Ascent, 0);
                break;
            case Phase.Turn when _turnElapsed >= TurnSeconds:
                (_phase, _elapsed) = (Phase.Hold, 0);
                break;
            case Phase.Hold when _elapsed >= HoldSeconds:
                (_phase, _elapsed) = (Phase.Ascent, 0);
                break;
            case Phase.Ascent when _elapsed >= AscentSeconds:
                (_phase, _elapsed) = (Phase.Stars, 0);
                break;
        }
    }

    /// <summary>
    /// A new game: straight up at the source's hour, no hold or run of the hour (the start hour comes in under the black).
    /// </summary>
    public void ClimbAtOnce() => (_destinationHour, _phase, _elapsed) = (_sourceHour, Phase.Ascent, 0);

    /// <summary>
    /// The load is done and the destination warms up under the flight: the sky fades to black over it.
    /// </summary>
    public void Cover() => _covering = true;

    public void Arrive()
    {
        _phase = Phase.Arriving;
        _elapsed = 0;
        _covering = false;
    }

    public void Skip()
    {
        if (_phase == Phase.Arriving) (_elapsed, Fade) = (DescentSeconds, 0);
        else if (_phase < Phase.Stars) (_phase, _elapsed, _turnElapsed) = (Phase.Stars, 0, double.MaxValue);
    }

    /// <summary>
    /// Places the camera for this frame and returns the hour to light it with. <paramref name="destinationReady"/>: the destination warms up under
    /// the black, seen from where the descent lands (what streams and is waited for is what the view will show).
    /// </summary>
    public float Apply(WorldCamera camera, float destination, bool destinationReady = false)
    {
        float to = _destinationHour ?? destination;
        if (destinationReady && _phase != Phase.Arriving) return destination;

        if (_phase == Phase.Arriving)
        {
            ApplyDescent(camera);
            return destination;
        }

        float lift = _phase switch { Phase.Ascent => Ease((float)(_elapsed / AscentSeconds)), Phase.Stars => 1, _ => 0 };
        Place(camera, _sourceEye, _sourceYaw, float.Lerp(_sourcePitch, SkyPitch, lift), _sourceDistance, _sourceFov);
        return _destinationHour is null ? _sourceHour : ForwardHour(_sourceHour, to, Ease(TurnSeconds > 0 ? (float)(_turnElapsed / TurnSeconds) : 1, 0.5f, 0.5f));
    }

    /// <summary>
    /// The descent: orbit round the view's target onto its heading while coming down from above it, the look tipping from the sky to the view's
    /// pitch.
    /// </summary>
    void ApplyDescent(WorldCamera camera)
    {
        float t = Ease((float)(_elapsed / DescentSeconds), 0.3f, 0.2f);
        float yaw = camera.Yaw - ArrivalSweep * (1 - t);
        float pitch = camera.Pitch;
        var eye = camera.Target + new Vector3(MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch), MathF.Cos(pitch) * MathF.Cos(yaw)) * camera.Distance
            + Vector3.UnitY * ArrivalHeight * (1 - t);
        float fov = float.Lerp(_sourceFov, camera.FieldOfView, t);
        Place(camera, eye, yaw, float.Lerp(SkyPitch, pitch, t), camera.Distance, fov);
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
        camera.Yaw = yaw;
        camera.Pitch = pitch;
        camera.Distance = distance;
        camera.FieldOfView = fov;
        camera.Target = eye + camera.Forward * distance;
    }
}
