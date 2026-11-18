using System.Numerics;
using Meitou.Data.World;
using Meitou.Game.Rendering.Overlays;

namespace Meitou.Game.Host.Photo;

/// <summary>
/// Photo mode's lights (docs/viewer.md "Photo mode", Light tab): up to <see cref="Count"/> lamps of its own, each placed where the camera stands
/// (pointing where it looks) or carried with it, a point light or a spot, of a colour temperature, a brightness and a reach. They are drawn as the
/// game's lamps are (<see cref="Rendering.Objects.WorldLamps.Moving"/>, so the lamp shadows trace them too), by day and night alike; leaving the mode
/// or Reset puts them out. Over the picture, never in a saved one, each placed light has a mark (<see cref="DrawGizmos"/>) that a press chooses and
/// a drag moves.
/// </summary>
sealed class PhotoLights
{
    /// <summary>
    /// One light.
    /// </summary>
    sealed class Light
    {
        /// <summary>
        /// It shines.
        /// </summary>
        public bool On;

        /// <summary>
        /// It goes with the camera (at the eye, pointing where it looks) rather than staying where it was placed.
        /// </summary>
        public bool Carried;

        /// <summary>
        /// It was placed somewhere (Place here): before that it has no place of its own and no mark.
        /// </summary>
        public bool Placed;

        /// <summary>
        /// Where it stands and the way a spot points, as placed.
        /// </summary>
        public Vector3 Position, Direction = -Vector3.UnitZ;

        /// <summary>
        /// A spot (a cone of <see cref="Cone"/>) rather than a point light.
        /// </summary>
        public bool Spot;

        /// <summary>
        /// The colour temperature in kelvin.
        /// </summary>
        public float Kelvin = 4500;

        /// <summary>
        /// The power, as a LIGHT record's <c>brightness</c> (the game's lamps: 0.2 to 1).
        /// </summary>
        public float Brightness = 2;

        /// <summary>
        /// How far it reaches, in units (the game's lamps: 25 to 750).
        /// </summary>
        public float Reach = 200;

        /// <summary>
        /// A spot's full cone in degrees.
        /// </summary>
        public float Cone = 50;
    }

    /// <summary>
    /// The most lights.
    /// </summary>
    public const int Count = 3;

    /// <summary>
    /// The lights.
    /// </summary>
    readonly Light[] _lights = [new(), new(), new()];

    /// <summary>
    /// This frame's lamps for the renderer.
    /// </summary>
    readonly List<WorldLight> _drawn = [];

    /// <summary>
    /// The light the tab's settings change (0 to <see cref="Count"/> - 1).
    /// </summary>
    public int Chosen { get; private set; }

    /// <summary>
    /// The chosen light.
    /// </summary>
    Light Current => _lights[Chosen];

    /// <summary>
    /// Chooses the next light (<paramref name="by"/> 1) or the one before.
    /// </summary>
    public void Step(int by) => Chosen = ((Chosen + by) % Count + Count) % Count;

    /// <summary>
    /// The chosen light's name for the panel: its number and whether it shines.
    /// </summary>
    public string ChosenName => $"Light {Chosen + 1}{(Current.On ? "" : " (off)")}";

    /// <summary>
    /// Places the chosen light where the camera stands, pointing where it looks, and turns it on.
    /// </summary>
    public void PlaceAt(Vector3 eye, Vector3 forward) =>
        (Current.Position, Current.Direction, Current.On, Current.Placed) = (eye, Vector3.Normalize(forward), true, true);

    /// <summary>
    /// The chosen light shines.
    /// </summary>
    public bool On
    {
        get => Current.On;
        set => Current.On = value;
    }

    /// <summary>
    /// The chosen light goes with the camera.
    /// </summary>
    public bool Carried
    {
        get => Current.Carried;
        set => Current.Carried = value;
    }

    /// <summary>
    /// The chosen light is a spot.
    /// </summary>
    public bool Spot
    {
        get => Current.Spot;
        set => Current.Spot = value;
    }

    /// <summary>
    /// The chosen light's colour temperature in kelvin, 1500 to 12000.
    /// </summary>
    public float Kelvin
    {
        get => Current.Kelvin;
        set => Current.Kelvin = Math.Clamp(value, 1500, 12000);
    }

    /// <summary>
    /// The chosen light's power, 0.05 to 20.
    /// </summary>
    public float Brightness
    {
        get => Current.Brightness;
        set => Current.Brightness = Math.Clamp(value, 0.05f, 20);
    }

    /// <summary>
    /// The chosen light's reach in units, 10 to 1000.
    /// </summary>
    public float Reach
    {
        get => Current.Reach;
        set => Current.Reach = Math.Clamp(value, 10, 1000);
    }

    /// <summary>
    /// The chosen spot's full cone in degrees, 5 to 150.
    /// </summary>
    public float Cone
    {
        get => Current.Cone;
        set => Current.Cone = Math.Clamp(value, 5, 150);
    }

    /// <summary>
    /// Every light out and as new, the first chosen.
    /// </summary>
    public void Clear()
    {
        for (int i = 0; i < Count; i++) _lights[i] = new Light();
        Chosen = 0;
        _drawn.Clear();
        _drag = null;
    }

    /// <summary>
    /// The lamps to draw this frame, with the camera at <paramref name="eye"/> looking along <paramref name="forward"/> (the carried ones go with it).
    /// </summary>
    public IReadOnlyList<WorldLight> Drawn(Vector3 eye, Vector3 forward)
    {
        _drawn.Clear();
        for (int i = 0; i < Count; i++)
        {
            var l = _lights[i];
            if (!l.On) continue;

            var (at, way) = l.Carried ? (eye, Vector3.Normalize(forward)) : (l.Position, l.Direction);
            float cone = l.Cone * MathF.PI / 180;
            _drawn.Add(new WorldLight(at, Colour(l.Kelvin), l.Brightness, l.Reach, l.Spot ? WorldLightType.Spot : WorldLightType.Point, way,
                cone * 0.7f, cone, 1, WorldLightEffect.None, WorldLightRule.WhenBuildingOn, false, 0, "photo", "", $"photo/{i}"));
        }

        return _drawn;
    }

    #region Gizmos

    /// <summary>
    /// The side of a light's icon in pixels at a frame's pixel scale <paramref name="u"/> (1 at 1080 p).
    /// </summary>
    static float IconSide(float u) => PhotoIcons.Extent * 1.1f * u;

    /// <summary>
    /// The light being dragged and how it moves: in the plane facing the camera through where it stood when the drag began.
    /// </summary>
    (Vector3 Normal, Vector3 Offset)? _drag;

    /// <summary>
    /// A light is being dragged (<see cref="Press"/>).
    /// </summary>
    public bool Dragging => _drag is not null;

    /// <summary>
    /// The light placed in the world (not carried) whose mark is nearest <paramref name="pixel"/> within half its icon's side; -1 none.
    /// </summary>
    int At(PlayerInterface view, Vector2 pixel)
    {
        float best = IconSide(Math.Max(0.75f, view.Height / 1080f)) * 0.5f;
        int found = -1;
        for (int i = 0; i < Count; i++)
        {
            var l = _lights[i];
            if (l.Carried || !l.Placed || view.Project(l.Position) is not { } at) continue;

            float d = Vector2.Distance(at, pixel);
            if (d < best) (best, found) = (d, i);
        }

        return found;
    }

    /// <summary>
    /// A press at <paramref name="pixel"/>: on a light's mark it chooses that light, on the chosen one's it starts dragging it. False when no mark
    /// is there.
    /// </summary>
    public bool Press(PlayerInterface view, Vector2 pixel)
    {
        int i = At(view, pixel);
        if (i < 0) return false;
        if (i != Chosen)
        {
            Chosen = i;
            return true;
        }

        if (view.Ray(pixel) is not { } ray) return true;

        var normal = Vector3.Normalize(view.Eye - Current.Position);
        _drag = (normal, Current.Position - OnPlane(ray, Current.Position, normal));
        return true;
    }

    /// <summary>
    /// The pointer moved while dragging: the chosen light follows it in the plane facing the camera through where it began, so it keeps its
    /// distance from the eye.
    /// </summary>
    public void Drag(PlayerInterface view, Vector2 pixel)
    {
        if (_drag is not { } drag || view.Ray(pixel) is not { } ray) return;

        var anchor = Current.Position - drag.Offset;
        Current.Position = OnPlane(ray, anchor, drag.Normal) + drag.Offset;
    }

    /// <summary>
    /// The drag ended.
    /// </summary>
    public void Release() => _drag = null;

    /// <summary>
    /// The transform gizmo's subject: the chosen light when it is placed in the world (null otherwise). A spot also turns: its yaw and pitch.
    /// </summary>
    public GizmoSubject? GizmoSubject()
    {
        var l = Current;
        if (l.Carried || !l.Placed) return null;
        if (!l.Spot) return new GizmoSubject(l.Position, world => l.Position = world);

        return new GizmoSubject(l.Position, world => l.Position = world, () => Yaw(l.Direction), yaw => l.Direction = Aim(yaw, Pitch(l.Direction)),
            () => Pitch(l.Direction), pitch => l.Direction = Aim(Yaw(l.Direction), pitch));
    }

    /// <summary>
    /// A direction's yaw, as the simulation's facing has it.
    /// </summary>
    static float Yaw(Vector3 direction) => MathF.Atan2(direction.X, direction.Z);

    /// <summary>
    /// A direction's pitch, positive looking up.
    /// </summary>
    static float Pitch(Vector3 direction) => MathF.Asin(Math.Clamp(direction.Y, -1, 1));

    /// <summary>
    /// The unit direction with <paramref name="yaw"/> and <paramref name="pitch"/> (kept short of straight up and down).
    /// </summary>
    static Vector3 Aim(float yaw, float pitch)
    {
        pitch = Math.Clamp(pitch, -1.5f, 1.5f);
        return new Vector3(MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch), MathF.Cos(pitch) * MathF.Cos(yaw));
    }

    /// <summary>
    /// A light's kind icon on the picture: its centre and side in pixels, the light's colour (tinting the glass or the beam) and its opacity.
    /// </summary>
    public readonly record struct MarkIcon(Vector2 Centre, float Side, PhotoIcons.Kind Kind, Vector3 Colour, float Alpha);

    /// <summary>
    /// The icons of the last <see cref="DrawGizmos"/>: the panel draws them (with its other images) over the marks.
    /// </summary>
    readonly List<MarkIcon> _icons = [];

    /// <summary>
    /// The icons of the last <see cref="DrawGizmos"/>.
    /// </summary>
    public IReadOnlyList<MarkIcon> Icons => _icons;

    /// <summary>
    /// A light placed in the world has a mark (<see cref="DrawGizmos"/>).
    /// </summary>
    public bool AnyMarked => _lights.Any(l => l.Placed && !l.Carried);

    /// <summary>
    /// The chosen light's mark on the picture; null when it has none or stands behind the camera.
    /// </summary>
    public Vector2? MarkOf(PlayerInterface view) => Current.Carried || !Current.Placed ? null : view.Project(Current.Position);

    /// <summary>
    /// Where <paramref name="ray"/> meets the plane through <paramref name="point"/> with <paramref name="normal"/> (the point itself when the ray
    /// runs along the plane).
    /// </summary>
    static Vector3 OnPlane((Vector3 Origin, Vector3 Direction) ray, Vector3 point, Vector3 normal)
    {
        float along = Vector3.Dot(ray.Direction, normal);
        if (MathF.Abs(along) < 1e-4f) return point;

        float t = Vector3.Dot(point - ray.Origin, normal) / along;
        return t > 0 ? ray.Origin + ray.Direction * t : point;
    }

    /// <summary>
    /// The lights' marks over the picture (never in a saved picture): for each light placed in the world a disc of its colour with its number,
    /// dimmed when it is off; for the chosen one a ring. On the Light tab only (<paramref name="lightTab"/>): a stroke the way a spot points and,
    /// for the chosen one, its reach (three circles round a point light) or its cone (to the reach) in thin lines. A light carried with the camera
    /// stands at the eye and has no mark.
    /// </summary>
    public void DrawGizmos(DebugOverlay o, PlayerInterface view, float u, bool lightTab)
    {
        _icons.Clear();
        for (int i = 0; i < Count; i++)
        {
            var l = _lights[i];
            if (l.Carried || !l.Placed) continue;

            bool chosen = i == Chosen;
            float alpha = l.On ? 1 : 0.45f;
            var line = new Vector4(Colour(l.Kelvin) * 0.5f + new Vector3(0.5f), (chosen ? 0.8f : 0.5f) * alpha);
            float thin = Math.Max(1, 1.25f * u);
            if (!lightTab) { }
            else if (chosen) Shape(o, view, l, line, thin);
            else if (l.Spot) Segment(o, view, l.Position, l.Position + l.Direction * MathF.Min(l.Reach, 40), line, thin);

            if (view.Project(l.Position) is not { } at) continue;

            // The icon (drawn by the panel, PhotoIcons) is the mark: centred on the light's place, ringed when chosen, its number under its right.
            float side = IconSide(u);
            if (chosen) Ring(o, at, side * 0.62f, Math.Max(1.5f, 2 * u), new Vector4(1, 1, 1, 0.9f * alpha));
            _icons.Add(new MarkIcon(at, side, l.Spot ? PhotoIcons.Kind.Spot : PhotoIcons.Kind.Bulb, Colour(l.Kelvin), alpha));
            o.Text((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), at.X + side * 0.38f, at.Y + side * 0.3f,
                new Vector4(1, 1, 1, 0.9f * alpha));
        }
    }

    /// <summary>
    /// The chosen light's reach: three circles round a point light (one in each axis plane), a spot's cone to its reach (its axis, four edges and the
    /// rim).
    /// </summary>
    static void Shape(DebugOverlay o, PlayerInterface view, Light l, Vector4 colour, float thickness)
    {
        const int steps = 48;
        if (!l.Spot)
        {
            Circle(o, view, l.Position, Vector3.UnitX, Vector3.UnitY, l.Reach, steps, colour, thickness);
            Circle(o, view, l.Position, Vector3.UnitX, Vector3.UnitZ, l.Reach, steps, colour, thickness);
            Circle(o, view, l.Position, Vector3.UnitY, Vector3.UnitZ, l.Reach, steps, colour, thickness);
            return;
        }

        var axis = Vector3.Normalize(l.Direction);
        var side = Vector3.Normalize(Vector3.Cross(axis, MathF.Abs(axis.Y) < 0.95f ? Vector3.UnitY : Vector3.UnitX));
        var up = Vector3.Cross(side, axis);
        float half = l.Cone * 0.5f * MathF.PI / 180;
        var centre = l.Position + axis * (l.Reach * MathF.Cos(half));
        float rim = l.Reach * MathF.Sin(half);
        Segment(o, view, l.Position, centre, colour, thickness);
        for (int k = 0; k < 4; k++)
        {
            float a = k * MathF.PI / 2;
            Segment(o, view, l.Position, centre + (side * MathF.Cos(a) + up * MathF.Sin(a)) * rim, colour, thickness);
        }

        Circle(o, view, centre, side, up, rim, steps, colour, thickness);
    }

    /// <summary>
    /// A circle of <paramref name="radius"/> round <paramref name="centre"/> in the plane of <paramref name="a"/> and <paramref name="b"/>, in
    /// <paramref name="steps"/> strokes.
    /// </summary>
    static void Circle(DebugOverlay o, PlayerInterface view, Vector3 centre, Vector3 a, Vector3 b, float radius, int steps, Vector4 colour,
        float thickness)
    {
        var last = centre + a * radius;
        for (int k = 1; k <= steps; k++)
        {
            float t = k * MathF.Tau / steps;
            var next = centre + (a * MathF.Cos(t) + b * MathF.Sin(t)) * radius;
            Segment(o, view, last, next, colour, thickness);
            last = next;
        }
    }

    /// <summary>
    /// A world segment as a stroke; left out when either end is behind the camera.
    /// </summary>
    static void Segment(DebugOverlay o, PlayerInterface view, Vector3 a, Vector3 b, Vector4 colour, float thickness)
    {
        if (view.Project(a) is not { } pa || view.Project(b) is not { } pb) return;

        o.Line(pa.X, pa.Y, pb.X, pb.Y, thickness, colour);
    }

    /// <summary>
    /// A ring on the picture.
    /// </summary>
    static void Ring(DebugOverlay o, Vector2 at, float radius, float thickness, Vector4 colour)
    {
        const int steps = 28;
        var last = at + new Vector2(radius, 0);
        for (int k = 1; k <= steps; k++)
        {
            float t = k * MathF.Tau / steps;
            var next = at + new Vector2(MathF.Cos(t), MathF.Sin(t)) * radius;
            o.Line(last.X, last.Y, next.X, next.Y, thickness, colour);
            last = next;
        }
    }

    #endregion

    /// <summary>
    /// The colour of a black body at <paramref name="kelvin"/>, its brightest channel 1 (Tanner Helland's fit to the CIE data).
    /// </summary>
    public static Vector3 Colour(float kelvin)
    {
        float t = Math.Clamp(kelvin, 1000, 40000) / 100;
        float r = t <= 66 ? 255 : 329.698727446f * MathF.Pow(t - 60, -0.1332047592f);
        float g = t <= 66 ? 99.4708025861f * MathF.Log(t) - 161.1195681661f : 288.1221695283f * MathF.Pow(t - 60, -0.0755148492f);
        float b = t >= 66 ? 255 : t <= 19 ? 0 : 138.5177312231f * MathF.Log(t - 10) - 305.0447927307f;
        var c = Vector3.Clamp(new Vector3(r, g, b) / 255, Vector3.Zero, Vector3.One);
        return c / MathF.Max(c.X, MathF.Max(c.Y, MathF.Max(c.Z, 1e-3f)));
    }
}
