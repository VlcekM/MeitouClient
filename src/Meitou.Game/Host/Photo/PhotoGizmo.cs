using System.Numerics;
using Meitou.Game.Rendering.Overlays;

namespace Meitou.Game.Host.Photo;

/// <summary>
/// What a transform gizmo acts on: where it stands, how it moves there and, when it can turn, its yaw (radians, the simulation's: forward is
/// (sin, 0, cos)) and its pitch (radians, positive looks up).
/// </summary>
/// <param name="Origin">The point the gizmo is drawn at.</param>
/// <param name="Move">Puts the subject's origin at a world point (the subject keeps its own bounds).</param>
/// <param name="Yaw">The subject's yaw; null: it does not turn about the vertical.</param>
/// <param name="SetYaw">Sets the yaw.</param>
/// <param name="Pitch">The subject's pitch; null: it does not pitch.</param>
/// <param name="SetPitch">Sets the pitch.</param>
sealed record GizmoSubject(Vector3 Origin, Action<Vector3> Move, Func<float>? Yaw = null, Action<float>? SetYaw = null, Func<float>? Pitch = null,
    Action<float>? SetPitch = null);

/// <summary>
/// The handles of a transform gizmo.
/// </summary>
enum GizmoHandle
{
    /// <summary>
    /// None.
    /// </summary>
    None,

    /// <summary>
    /// The arrow along the world X axis (red).
    /// </summary>
    X,

    /// <summary>
    /// The arrow along the world Y axis, up (green).
    /// </summary>
    Y,

    /// <summary>
    /// The arrow along the world Z axis (blue).
    /// </summary>
    Z,

    /// <summary>
    /// The ring that turns the subject about the vertical (yellow).
    /// </summary>
    Yaw,

    /// <summary>
    /// The ring that pitches the subject up and down (magenta).
    /// </summary>
    Pitch,
}

/// <summary>
/// Photo mode's transform gizmo (docs/viewer.md "Photo mode"): over the chosen light or the character chosen on the Pose tab, three arrows (a drag
/// moves along that world axis only), and where the subject turns a ring for its yaw and, for a spot, one for its pitch. Drawn over the picture at a
/// constant size on screen, never in a saved picture; the handle under the pointer is lit, and a press within a few pixels of one takes it before
/// any character or light mark. The subject keeps its own bounds (a character's moves are the Pose tab's Forward, Sideways and Height).
/// </summary>
sealed class PhotoGizmo
{
    #region State

    /// <summary>
    /// The arrows' length and the rings' radius on screen, in pixels at 1080 p.
    /// </summary>
    const float ArrowPixels = 90, RingPixels = 60;

    /// <summary>
    /// Where an arrow starts along its length (pixels at 1080 p): the middle is left to the light's icon, half a side (15 px) and a little more.
    /// </summary>
    const float ArrowStart = 18;

    /// <summary>
    /// How near the pointer must come to a handle to take it, in pixels at 1080 p.
    /// </summary>
    const float HitPixels = 9;

    /// <summary>
    /// The strokes of a ring.
    /// </summary>
    const int RingSteps = 48;

    /// <summary>
    /// The handle under the pointer.
    /// </summary>
    public GizmoHandle Hover { get; private set; }

    /// <summary>
    /// The handle being dragged.
    /// </summary>
    public GizmoHandle Active { get; private set; }

    /// <summary>
    /// A handle is being dragged.
    /// </summary>
    public bool Dragging => Active != GizmoHandle.None;

    /// <summary>
    /// The subject of the last <see cref="Layout"/>, and where it stood.
    /// </summary>
    GizmoSubject? _subject;

    /// <summary>
    /// The world length of a pixel at the subject (at 1080 p), the origin on screen.
    /// </summary>
    float _pixel;

    /// <summary>
    /// The origin on screen, and the axis arrows' ends.
    /// </summary>
    Vector2 _origin;

    /// <summary>
    /// The arrows' ends on screen, by X, Y, Z.
    /// </summary>
    readonly Vector2[] _tips = new Vector2[3];

    /// <summary>
    /// The rings on screen: the yaw ring, the pitch ring (empty when the subject has none).
    /// </summary>
    readonly List<Vector2>[] _rings = [[], []];

    /// <summary>
    /// What a drag began with: the origin, the position along the axis or the angle on the ring, and the yaw or pitch.
    /// </summary>
    Vector3 _startOrigin;

    /// <summary>
    /// The starting parameter of the drag (along the axis, or the ring angle) and the subject's angle then.
    /// </summary>
    float _startParameter, _startAngle;

    #endregion

    #region Layout

    /// <summary>
    /// The pixel scale of a frame (1 at 1080 p, at least 0.75).
    /// </summary>
    static float Unit(PlayerInterface view) => Math.Max(0.75f, view.Height / 1080f);

    /// <summary>
    /// The world length one pixel spans at <paramref name="at"/>, from the angle between the rays through two pixels of the centre, 64 apart.
    /// The angle is the length of the directions' cross product: the arc cosine of their dot product, a hair under 1 for neighbouring pixels, took
    /// a handful of values in single precision, and the gizmo changed size in steps as the camera moved.
    /// </summary>
    static float PixelAt(PlayerInterface view, Vector3 at)
    {
        const float apart = 64;
        var centre = new Vector2(view.Width / 2f, view.Height / 2f);
        if (view.Ray(centre - Vector2.UnitX * (apart / 2)) is not { } a || view.Ray(centre + Vector2.UnitX * (apart / 2)) is not { } b) return 0.1f;

        float angle = MathF.Asin(Math.Clamp(Vector3.Cross(a.Direction, b.Direction).Length(), 0, 1)) / apart;
        return Math.Max(angle * Vector3.Distance(view.Eye, at), 1e-4f);
    }

    /// <summary>
    /// The world axis of arrow <paramref name="handle"/>.
    /// </summary>
    static Vector3 Axis(GizmoHandle handle) => handle switch
    {
        GizmoHandle.X => Vector3.UnitX,
        GizmoHandle.Y => Vector3.UnitY,
        _ => Vector3.UnitZ,
    };

    /// <summary>
    /// Lays the gizmo of <paramref name="subject"/> out for this frame (null: none), and finds the handle under <paramref name="pointer"/> unless one
    /// is being dragged. False when there is nothing to show (no subject, or its origin is behind the camera).
    /// </summary>
    public bool Layout(PlayerInterface view, GizmoSubject? subject, Vector2 pointer)
    {
        _subject = null;
        Hover = GizmoHandle.None;
        if (subject is null || view.Project(subject.Origin) is not { } origin) return false;

        float u = Unit(view);
        _subject = subject;
        _origin = origin;
        _pixel = PixelAt(view, subject.Origin) * u;
        for (int k = 0; k < 3; k++)
            _tips[k] = view.Project(subject.Origin + Axis(GizmoHandle.X + k) * ArrowPixels * _pixel) ?? origin;

        _rings[0].Clear();
        _rings[1].Clear();
        if (subject.Yaw is not null) Ring(view, subject.Origin, Vector3.UnitX, Vector3.UnitZ, _rings[0]);
        if (subject.Pitch is not null)
        {
            float yaw = subject.Yaw?.Invoke() ?? 0;
            Ring(view, subject.Origin, new Vector3(MathF.Sin(yaw), 0, MathF.Cos(yaw)), Vector3.UnitY, _rings[1]);
        }

        if (!Dragging) Hover = Nearest(pointer, u);
        return true;
    }

    /// <summary>
    /// The points of a ring round <paramref name="centre"/> in the plane of <paramref name="a"/> and <paramref name="b"/>, on screen; a point behind
    /// the camera breaks it (left as the origin's).
    /// </summary>
    void Ring(PlayerInterface view, Vector3 centre, Vector3 a, Vector3 b, List<Vector2> into)
    {
        for (int k = 0; k <= RingSteps; k++)
        {
            float t = k * MathF.Tau / RingSteps;
            var world = centre + (a * MathF.Cos(t) + b * MathF.Sin(t)) * RingPixels * _pixel;
            into.Add(view.Project(world) ?? _origin);
        }
    }

    /// <summary>
    /// The handle nearest <paramref name="pointer"/> within <see cref="HitPixels"/>.
    /// </summary>
    GizmoHandle Nearest(Vector2 pointer, float u)
    {
        float best = HitPixels * u;
        var found = GizmoHandle.None;
        for (int k = 0; k < 3; k++)
        {
            var toward = _tips[k] - _origin;
            if (toward.Length() < 4) continue;

            var from = _origin + Vector2.Normalize(toward) * Math.Min(ArrowStart * u, toward.Length() * 0.4f);
            float d = DistanceToSegment(pointer, from, _tips[k]);
            if (d < best) (best, found) = (d, GizmoHandle.X + k);
        }

        for (int r = 0; r < 2; r++)
        {
            float d = DistanceToPolyline(pointer, _rings[r]);
            if (d < best) (best, found) = (d, r == 0 ? GizmoHandle.Yaw : GizmoHandle.Pitch);
        }

        return found;
    }

    /// <summary>
    /// The distance from <paramref name="p"/> to the segment <paramref name="a"/> to <paramref name="b"/>.
    /// </summary>
    static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float length = ab.LengthSquared();
        float t = length < 1e-6f ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / length, 0, 1);
        return Vector2.Distance(p, a + ab * t);
    }

    /// <summary>
    /// The distance from <paramref name="p"/> to the polyline <paramref name="line"/> (infinite when it is empty).
    /// </summary>
    static float DistanceToPolyline(Vector2 p, List<Vector2> line)
    {
        float best = float.PositiveInfinity;
        for (int i = 1; i < line.Count; i++) best = MathF.Min(best, DistanceToSegment(p, line[i - 1], line[i]));
        return best;
    }

    /// <summary>
    /// A point on the handle on screen, for a drag that does not come from the mouse (the control channel): the middle of an arrow, a point of a ring.
    /// </summary>
    public Vector2? Anchor(GizmoHandle handle)
    {
        if (_subject is null) return null;

        return handle switch
        {
            GizmoHandle.X or GizmoHandle.Y or GizmoHandle.Z => (_origin + _tips[handle - GizmoHandle.X]) / 2,
            GizmoHandle.Yaw when _rings[0].Count > 0 => _rings[0][RingSteps / 8],
            GizmoHandle.Pitch when _rings[1].Count > 0 => _rings[1][RingSteps / 8],
            _ => null,
        };
    }

    #endregion

    #region Dragging

    /// <summary>
    /// A press at <paramref name="pixel"/>: takes the handle there and starts dragging it (the layout of <paramref name="subject"/> is made afresh);
    /// false when no handle is there.
    /// </summary>
    public bool Press(PlayerInterface view, GizmoSubject? subject, Vector2 pixel)
    {
        if (!Layout(view, subject, pixel) || Hover == GizmoHandle.None || view.Ray(pixel) is not { } ray) return false;

        var handle = Hover;
        var parameter = Parameter(handle, ray, subject!.Origin, subject);
        if (parameter is null) return false;

        (Active, _startOrigin, _startParameter) = (handle, subject.Origin, parameter.Value);
        _startAngle = handle == GizmoHandle.Yaw ? subject.Yaw!() : handle == GizmoHandle.Pitch ? subject.Pitch!() : 0;
        return true;
    }

    /// <summary>
    /// The pointer moved while dragging: the subject follows along the handle (an arrow: its axis only; a ring: the angle round it).
    /// </summary>
    public void Drag(PlayerInterface view, Vector2 pixel)
    {
        if (!Dragging || _subject is not { } subject || view.Ray(pixel) is not { } ray) return;
        if (Parameter(Active, ray, _startOrigin, subject) is not { } now) return;

        if (Active is GizmoHandle.X or GizmoHandle.Y or GizmoHandle.Z)
            subject.Move(_startOrigin + Axis(Active) * (now - _startParameter));
        else if (Active == GizmoHandle.Yaw) subject.SetYaw!(_startAngle + Wrap(now - _startParameter));
        else subject.SetPitch!(_startAngle + Wrap(now - _startParameter));
    }

    /// <summary>
    /// The drag ended.
    /// </summary>
    public void Release() => Active = GizmoHandle.None;

    /// <summary>
    /// Where the pointer's ray meets <paramref name="handle"/>: for an arrow the distance along its axis from <paramref name="origin"/> of the point
    /// of the axis nearest the ray, for a ring the angle (radians) of where the ray meets its plane; null when the ray runs along it.
    /// </summary>
    static float? Parameter(GizmoHandle handle, (Vector3 Origin, Vector3 Direction) ray, Vector3 origin, GizmoSubject subject)
    {
        if (handle is GizmoHandle.X or GizmoHandle.Y or GizmoHandle.Z)
        {
            var axis = Axis(handle);
            var w = origin - ray.Origin;
            float b = Vector3.Dot(axis, ray.Direction), d = Vector3.Dot(axis, w), e = Vector3.Dot(ray.Direction, w);
            float denominator = 1 - b * b;
            return denominator < 1e-4f ? null : (b * e - d) / denominator;
        }

        bool yaw = handle == GizmoHandle.Yaw;
        float heading = subject.Yaw?.Invoke() ?? 0;
        var forward = new Vector3(MathF.Sin(heading), 0, MathF.Cos(heading));
        var normal = yaw ? Vector3.UnitY : Vector3.Cross(forward, Vector3.UnitY);
        float along = Vector3.Dot(ray.Direction, normal);
        if (MathF.Abs(along) < 1e-3f) return null;

        float t = Vector3.Dot(origin - ray.Origin, normal) / along;
        if (t <= 0) return null;

        var local = ray.Origin + ray.Direction * t - origin;
        return yaw ? MathF.Atan2(local.X, local.Z) : MathF.Atan2(local.Y, Vector3.Dot(local, forward));
    }

    /// <summary>
    /// An angle brought to -pi..pi.
    /// </summary>
    static float Wrap(float angle)
    {
        while (angle > MathF.PI) angle -= MathF.Tau;
        while (angle < -MathF.PI) angle += MathF.Tau;
        return angle;
    }

    #endregion

    #region Drawing

    /// <summary>
    /// The handle's colour.
    /// </summary>
    static Vector3 ColourOf(GizmoHandle handle) => handle switch
    {
        GizmoHandle.X => new Vector3(1, 0.25f, 0.25f),
        GizmoHandle.Y => new Vector3(0.35f, 1, 0.35f),
        GizmoHandle.Z => new Vector3(0.35f, 0.55f, 1),
        GizmoHandle.Yaw => new Vector3(1, 0.85f, 0.3f),
        _ => new Vector3(1, 0.4f, 0.9f),
    };

    /// <summary>
    /// Draws the gizmo of <paramref name="subject"/> over the picture (nothing when it has none): the rings, then the arrows with their heads; the
    /// handle under the pointer, or being dragged, is lit and thicker.
    /// </summary>
    public void Draw(DebugOverlay o, PlayerInterface view, GizmoSubject? subject, Vector2 pointer)
    {
        if (!Layout(view, subject, pointer)) return;

        float u = Unit(view);
        for (int r = 0; r < 2; r++)
        {
            var handle = r == 0 ? GizmoHandle.Yaw : GizmoHandle.Pitch;
            var ring = _rings[r];
            bool lit = handle == (Dragging ? Active : Hover);
            var colour = new Vector4(ColourOf(handle), lit ? 1 : 0.7f);
            float thickness = (lit ? 3.5f : 2f) * u;
            for (int i = 1; i < ring.Count; i++) o.Line(ring[i - 1].X, ring[i - 1].Y, ring[i].X, ring[i].Y, thickness, colour);
        }

        for (int k = 0; k < 3; k++) Arrow(o, GizmoHandle.X + k, u, _tips[k]);
    }

    /// <summary>
    /// One arrow: a stroke from near the origin to its tip, and a head.
    /// </summary>
    void Arrow(DebugOverlay o, GizmoHandle handle, float u, Vector2 tip)
    {
        var toward = tip - _origin;
        if (toward.Length() < 4) return;

        bool lit = handle == (Dragging ? Active : Hover);
        var colour = new Vector4(ColourOf(handle), lit ? 1 : 0.8f);
        var dark = new Vector4(0, 0, 0, 0.5f);
        var way = Vector2.Normalize(toward);
        var side = new Vector2(-way.Y, way.X);
        var from = _origin + way * Math.Min(ArrowStart * u, toward.Length() * 0.4f);
        float head = (lit ? 15 : 12) * u, half = (lit ? 6.5f : 5) * u;
        var neck = tip - way * head;
        o.Line(from.X, from.Y, neck.X, neck.Y, (lit ? 6 : 4.5f) * u, dark);
        o.Line(from.X, from.Y, neck.X, neck.Y, (lit ? 3.5f : 2.5f) * u, colour);
        o.Triangle(tip + way * u, neck + side * (half + u), neck - side * (half + u), dark);
        o.Triangle(tip, neck + side * half, neck - side * half, colour);
    }

    #endregion
}
