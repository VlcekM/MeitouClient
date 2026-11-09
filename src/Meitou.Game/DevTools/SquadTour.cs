using System.Numerics;
using Meitou.Game.Host;
using Meitou.Game.Rendering.Frame;
using Meitou.Simulation;

namespace Meitou.Game.DevTools;

/// <summary>
/// Numpad / in the game (docs/viewer.md "Squad tour"): the camera flies from squad member to squad member in portrait order, keeping the
/// view's yaw, pitch and distance at each stop. Each hop is an arc: the camera climbs first (pulling back and looking more steeply down),
/// crosses at height and comes down onto the next member, who is tracked while they move. Presentation only; nothing is saved.
/// <see cref="Fly"/> is the same hop to one character (a double click on a far squad member, Advanced settings "Character flights").
/// </summary>
sealed class SquadTour
{
    /// <summary>
    /// Seconds the camera stays on each member before the next hop.
    /// </summary>
    const float Hold = 1.5f;

    /// <summary>
    /// Seconds each hop takes, whatever its length (Advanced settings "Character flight time").
    /// </summary>
    public float HopSeconds { get; set; } = UserConfig.DefaultFlightSeconds;

    /// <summary>
    /// The arc's extra boom at its top, per unit of ground crossed (at least <see cref="MinLift"/> times the view's own distance).
    /// </summary>
    const float LiftPerDistance = 0.55f, MinLift = 1.5f;

    /// <summary>
    /// How much steeper the camera looks down at the top of an arc (radians), and its limit.
    /// </summary>
    const float PitchLift = 0.35f, MaxPitch = 1.35f;

    /// <summary>
    /// The members to visit, in order.
    /// </summary>
    readonly List<CharacterId> _stops = [];

    /// <summary>
    /// The index of the stop the camera is on or flying to.
    /// </summary>
    int _next;

    /// <summary>
    /// Seconds into the hop or hold, and the view's yaw, pitch and distance the hops keep.
    /// </summary>
    float _time, _yaw, _pitch, _distance;

    /// <summary>
    /// The camera's target when the hop began.
    /// </summary>
    Vector3 _from;

    /// <summary>
    /// The camera holds on a member between hops.
    /// </summary>
    bool _holding;

    /// <summary>
    /// A flight is under way.
    /// </summary>
    public bool Active { get; private set; }

    /// <summary>
    /// The camera is on a hop now (not on a hold between hops).
    /// </summary>
    public bool Moving => Active && !_holding;

    /// <summary>
    /// Starts at the camera's view of <paramref name="members"/>[0] (flying there first when the view is elsewhere).
    /// </summary>
    public void Start(WorldCamera camera, IReadOnlyList<CharacterId> members, Func<CharacterId, Vector3?> position)
    {
        _stops.Clear();
        _stops.AddRange(members);
        if (!Active) (_yaw, _pitch, _distance) = (camera.Yaw, camera.Pitch, camera.Distance);   // a new flight mid-flight keeps the view it left from
        _next = 0;
        if (_stops.Count > 0 && position(_stops[0]) is { } first && Ground(first - camera.Target) < 0.25f * _distance)
            _next = 1;
        Active = _next < _stops.Count;
        if (Active) BeginHop(camera, position);
    }

    /// <summary>
    /// One hop from the camera's view to <paramref name="id"/>, ending on it.
    /// </summary>
    public void Fly(WorldCamera camera, CharacterId id, Func<CharacterId, Vector3?> position)
    {
        _stops.Clear();
        _stops.Add(id);
        if (!Active) (_yaw, _pitch, _distance) = (camera.Yaw, camera.Pitch, camera.Distance);   // a new flight mid-flight keeps the view it left from
        _next = 0;
        Active = true;
        BeginHop(camera, position);
    }

    /// <summary>
    /// The member the camera is on or flying to.
    /// </summary>
    public CharacterId? Current => _next < _stops.Count ? _stops[_next] : _stops.Count > 0 ? _stops[^1] : null;

    /// <summary>
    /// Ends the hop under way at once: the camera cuts to the view it was flying to.
    /// </summary>
    public void Finish(WorldCamera camera, Func<CharacterId, Vector3?> position)
    {
        if (Current is { } id && position(id) is { } at)
            (camera.Target, camera.Yaw, camera.Pitch, camera.Distance) = (at, _yaw, _pitch, _distance);
        Active = false;
    }

    /// <summary>
    /// The eye of the view the hop under way lands on (the next stop's during a hold), for the streaming to load there first; null with no flight.
    /// </summary>
    public Vector3? ArrivalEye(Func<CharacterId, Vector3?> position) =>
        Active && Current is { } id && position(id) is { } at
            // WorldCamera.Eye's boom
            ? at + new Vector3(MathF.Cos(_pitch) * MathF.Sin(_yaw), MathF.Sin(_pitch), MathF.Cos(_pitch) * MathF.Cos(_yaw)) * _distance
            : null;

    /// <summary>
    /// Ends the flight where the camera is.
    /// </summary>
    public void Stop() => Active = false;

    /// <summary>
    /// Moves the camera by <paramref name="dt"/> seconds; false once the tour is over (the camera is left on the last member).
    /// </summary>
    public bool Advance(double dt, WorldCamera camera, Func<CharacterId, Vector3?> position)
    {
        if (!Active) return false;
        _time += (float)dt;
        if (_holding)
        {
            if (position(_stops[_next - 1]) is { } held) camera.Target = held;
            if (_time < Hold) return true;
            BeginHop(camera, position);
            if (!Active) return false;
        }
        // A member gone mid-hop (dead and removed, left the squad): the hop ends where the camera is.
        var to = position(_stops[_next]) ?? camera.Target;
        float s = Math.Clamp(_time / HopSeconds, 0, 1);
        // A brachistochrone (cycloid) arch, upside down for a climb: its angle runs evenly in time, so the camera rises first (the ground move
        // starts as the cube of time, the lift as its square), crosses fastest at the top and slows into the descent onto the member.
        float theta = 2 * MathF.PI * s;
        float across = (theta - MathF.Sin(theta)) / (2 * MathF.PI);
        float lift = (1 - MathF.Cos(theta)) / 2;
        float height = MathF.Max(LiftPerDistance * Ground(to - _from), MinLift * _distance);
        camera.Target = Vector3.Lerp(_from, to, across);
        camera.Yaw = _yaw;
        camera.Pitch = _pitch + (MathF.Max(MathF.Min(_pitch + PitchLift, MaxPitch), _pitch) - _pitch) * lift;
        camera.Distance = MathF.Min(_distance + height * lift, camera.ViewDistance * 0.5f);
        if (s >= 1)
        {
            _next++;
            _holding = true;
            _time = 0;
            if (_next >= _stops.Count)
            {
                Active = false;
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Begins the hop to the next stop that has a position (skipping the gone ones), from the camera's target; the flight ends when none is left.
    /// </summary>
    void BeginHop(WorldCamera camera, Func<CharacterId, Vector3?> position)
    {
        while (_next < _stops.Count && position(_stops[_next]) is null) _next++;
        if (_next >= _stops.Count)
        {
            Active = false;
            return;
        }
        _from = camera.Target;
        (_time, _holding) = (0, false);
    }

    /// <summary>
    /// The length of <paramref name="v"/> on the ground (x and z).
    /// </summary>
    static float Ground(Vector3 v) => new Vector2(v.X, v.Z).Length();
}