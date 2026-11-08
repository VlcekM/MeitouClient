namespace Meitou.Game;

/// <summary>
/// The character editor's camera (docs/characters.md "Character editor", Preview): it orbits the character, dragged with a mouse button and zoomed with the wheel, and starts
/// over when the tab changes between the body and the head or the character changes. The original's camera is a rig that turns about the character (its yaw about the
/// vertical, its pitch about the side axis), with the camera on a boom whose length the wheel changes inside limits for the whole body and for the head; its start angles,
/// zooms and pitch limits are the exe's (<b>Verified</b>, <c>FUN_1405e9490</c> and its static data). The drag and wheel speeds are engine choices.
/// </summary>
sealed class EditorOrbit
{
    /// <summary>The camera's distance from its pivot for the whole body and for the head: the boom's own length (30.15, the original's offset) with its zoom limits for the
    /// head (-23 to -17) and the whole body (-9 to 7) added; a head starts at the original's zoom -21, a body at 0.</summary>
    public const float BodyNear = 21.15f, BodyFar = 37.15f, BodyStart = 30.15f, HeadNear = 7.15f, HeadFar = 13.15f, HeadStart = 9.15f;
    /// <summary>The lift (radians) and its limits: a body starts at 0.15 pi from above and keeps between 0.025 pi and 0.2 pi; a head starts level and keeps between -0.1 pi and 0.25 pi.</summary>
    public const float StartElevation = 0.15f * MathF.PI, MinElevation = 0.025f * MathF.PI, MaxElevation = 0.2f * MathF.PI,
        HeadStartElevation = 0, HeadMinElevation = -0.1f * MathF.PI, HeadMaxElevation = 0.25f * MathF.PI;
    /// <summary>The turn both views start at: in front of the character.</summary>
    public const float StartYaw = 0;
    /// <summary>Radians a pixel of dragging turns, and the distance a wheel step moves (the head's zoom range is a third of the body's).</summary>
    public const float RadiansPerPixel = 0.008f, BodyStep = 1.5f, HeadStep = 0.5f;

    public bool Head { get; private set; }
    /// <summary>The camera's turn about the character (0: in front of it) and its lift, radians.</summary>
    public float Yaw { get; private set; } = StartYaw;
    public float Elevation { get; private set; } = StartElevation;
    public float Distance { get; private set; } = BodyStart;

    public void Reset(bool head)
    {
        Head = head;
        Yaw = StartYaw;
        Elevation = head ? HeadStartElevation : StartElevation;
        Distance = head ? HeadStart : BodyStart;
    }

    /// <summary>Dragging by (<paramref name="dx"/>, <paramref name="dy"/>) pixels: right turns the character to the viewer's right, down looks at it from higher up.</summary>
    public void Drag(float dx, float dy)
    {
        Yaw -= dx * RadiansPerPixel;
        Elevation = Head ? Math.Clamp(Elevation + dy * RadiansPerPixel, HeadMinElevation, HeadMaxElevation) : Math.Clamp(Elevation + dy * RadiansPerPixel, MinElevation, MaxElevation);
    }

    /// <summary>Wheel steps, positive up: closer.</summary>
    public void Zoom(float steps) =>
        Distance = Head ? Math.Clamp(Distance - steps * HeadStep, HeadNear, HeadFar) : Math.Clamp(Distance - steps * BodyStep, BodyNear, BodyFar);
}
