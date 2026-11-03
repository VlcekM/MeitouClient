using System.Numerics;
using Meitou.Rendering;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// Numpad / in the game (docs/viewer.md "Squad tour"): the camera flies from squad member to squad member in portrait order, keeping the
/// view's yaw, pitch and distance at each stop. Each hop is an arc: the camera climbs first (pulling back and looking more steeply down),
/// crosses at height and comes down onto the next member, who is tracked while they move. Presentation only; nothing is saved.
/// <see cref="Fly"/> is the same hop to one character (a double click on a far squad member, Advanced settings "Character flights").
/// </summary>
sealed class SquadTour
{
    /// <summary>Seconds the camera stays on each member before the next hop.</summary>
    const float Hold = 1.5f;
    /// <summary>Seconds each hop takes, whatever its length (Advanced settings "Character flight time").</summary>
    public float HopSeconds { get; set; } = UserConfig.DefaultFlightSeconds;
    /// <summary>The arc's extra boom at its top, per unit of ground crossed (at least <see cref="MinLift"/> times the view's own distance).</summary>
    const float LiftPerDistance = 0.55f, MinLift = 1.5f;
    /// <summary>How much steeper the camera looks down at the top of an arc (radians), and its limit.</summary>
    const float PitchLift = 0.35f, MaxPitch = 1.35f;

    readonly List<CharacterId> stops = [];
    int next;
    float time, yaw, pitch, distance;
    Vector3 from;
    bool holding;

    public bool Active { get; private set; }
    /// <summary>The camera is on a hop now (not on a hold between hops).</summary>
    public bool Moving => Active && !holding;

    /// <summary>Starts at the camera's view of <paramref name="members"/>[0] (flying there first when the view is elsewhere).</summary>
    public void Start(WorldCamera camera, IReadOnlyList<CharacterId> members, Func<CharacterId, Vector3?> position)
    {
        stops.Clear();
        stops.AddRange(members);
        if (!Active) (yaw, pitch, distance) = (camera.Yaw, camera.Pitch, camera.Distance);   // a new flight mid-flight keeps the view it left from
        next = 0;
        if (stops.Count > 0 && position(stops[0]) is { } first && Ground(first - camera.Target) < 0.25f * distance) next = 1;
        Active = next < stops.Count;
        if (Active) BeginHop(camera, position);
    }

    /// <summary>One hop from the camera's view to <paramref name="id"/>, ending on it.</summary>
    public void Fly(WorldCamera camera, CharacterId id, Func<CharacterId, Vector3?> position)
    {
        stops.Clear();
        stops.Add(id);
        if (!Active) (yaw, pitch, distance) = (camera.Yaw, camera.Pitch, camera.Distance);   // a new flight mid-flight keeps the view it left from
        next = 0;
        Active = true;
        BeginHop(camera, position);
    }

    /// <summary>The member the camera is on or flying to.</summary>
    public CharacterId? Current => next < stops.Count ? stops[next] : stops.Count > 0 ? stops[^1] : null;

    /// <summary>Ends the hop under way at once: the camera cuts to the view it was flying to.</summary>
    public void Finish(WorldCamera camera, Func<CharacterId, Vector3?> position)
    {
        if (Current is { } id && position(id) is { } at) (camera.Target, camera.Yaw, camera.Pitch, camera.Distance) = (at, yaw, pitch, distance);
        Active = false;
    }

    /// <summary>The eye of the view the hop under way lands on (the next stop's during a hold), for the streaming to load there first; null with no flight.</summary>
    public Vector3? ArrivalEye(Func<CharacterId, Vector3?> position) =>
        Active && Current is { } id && position(id) is { } at
            ? at + new Vector3(MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch), MathF.Cos(pitch) * MathF.Cos(yaw)) * distance   // WorldCamera.Eye's boom
            : null;

    public void Stop() => Active = false;

    /// <summary>Moves the camera by <paramref name="dt"/> seconds; false once the tour is over (the camera is left on the last member).</summary>
    public bool Advance(double dt, WorldCamera camera, Func<CharacterId, Vector3?> position)
    {
        if (!Active) return false;
        time += (float)dt;
        if (holding)
        {
            if (position(stops[next - 1]) is { } held) camera.Target = held;
            if (time < Hold) return true;
            BeginHop(camera, position);
            if (!Active) return false;
        }
        // A member gone mid-hop (dead and removed, left the squad): the hop ends where the camera is.
        var to = position(stops[next]) ?? camera.Target;
        float s = Math.Clamp(time / HopSeconds, 0, 1);
        // A brachistochrone (cycloid) arch, upside down for a climb: its angle runs evenly in time, so the camera rises first (the ground move
        // starts as the cube of time, the lift as its square), crosses fastest at the top and slows into the descent onto the member.
        float theta = 2 * MathF.PI * s;
        float across = (theta - MathF.Sin(theta)) / (2 * MathF.PI);
        float lift = (1 - MathF.Cos(theta)) / 2;
        float height = MathF.Max(LiftPerDistance * Ground(to - from), MinLift * distance);
        camera.Target = Vector3.Lerp(from, to, across);
        camera.Yaw = yaw;
        camera.Pitch = pitch + (MathF.Max(MathF.Min(pitch + PitchLift, MaxPitch), pitch) - pitch) * lift;
        camera.Distance = MathF.Min(distance + height * lift, camera.ViewDistance * 0.5f);
        if (s >= 1) { next++; holding = true; time = 0; if (next >= stops.Count) { Active = false; return false; } }
        return true;
    }

    void BeginHop(WorldCamera camera, Func<CharacterId, Vector3?> position)
    {
        while (next < stops.Count && position(stops[next]) is null) next++;
        if (next >= stops.Count) { Active = false; return; }
        from = camera.Target;
        (time, holding) = (0, false);
    }

    static float Ground(Vector3 v) => new Vector2(v.X, v.Z).Length();
}
