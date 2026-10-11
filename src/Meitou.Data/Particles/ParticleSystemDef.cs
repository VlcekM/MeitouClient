using System.Numerics;

namespace Meitou.Data.Particles;

// The typed view of a ParticleUniverse script (docs/formats/particle-universe.md). Defaults are the plugin's documented ones where a
// script leaves an attribute out; for Kenshi they are Observed (a missing quota, box size or velocity in a shipped script is read
// with them), not Verified.

public enum PuEmitterType { Box, Circle, Point, SphereSurface, Line, Slave, Unknown }

public enum PuAffectorType
{
    Colour, Scale, TextureRotator, LinearForce, Vortex, Gravity, Randomiser, SineForce, ForceField, Jet, Align, FlockCentering,
    GeometryRotator, ScaleVelocity, Unknown,
}

public enum PuObserverType { OnPosition, OnTime, OnClear, OnEmission, Unknown }

public enum PuCompare { LessThan, GreaterThan, Equals }

public sealed class PuEmitterDef
{
    public required PuEmitterType Type { get; init; }
    public required string TypeName { get; init; }
    public string Name { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public PuDynamic Rate { get; init; } = PuDynamic.Fixed(10);
    public PuDynamic Life { get; init; } = PuDynamic.Fixed(3);
    public PuDynamic Velocity { get; init; } = PuDynamic.Fixed(100);
    /// <summary>Half-angle of the cone round <see cref="Direction"/> the particles leave in, degrees.</summary>
    public PuDynamic Angle { get; init; } = PuDynamic.Fixed(0);
    public PuDynamic Mass { get; init; } = PuDynamic.Fixed(1);
    /// <summary>How long the emitter works after it starts (null: for ever); then it rests for <see cref="RepeatDelay"/> and starts again (null: never).</summary>
    public PuDynamic? Duration { get; init; }
    public PuDynamic? RepeatDelay { get; init; }
    /// <summary>The particle's size when the emitter sets it: <c>all_particle_dimensions</c> for the three (it wins when both are given), else <c>particle_width</c> / <c>particle_height</c> / <c>particle_depth</c>.</summary>
    public PuDynamic? Width { get; init; }
    public PuDynamic? Height { get; init; }
    public PuDynamic? Depth { get; init; }
    public PuDynamic? AllDimensions { get; init; }
    public Vector3 Position { get; init; }
    public Vector3 Direction { get; init; } = Vector3.UnitY;
    public Vector4 Colour { get; init; } = Vector4.One;
    /// <summary>The emitted colour is picked at random between these when either is given (the missing one is <see cref="Colour"/>).</summary>
    public Vector4? ColourStart { get; init; }
    public Vector4? ColourEnd { get; init; }
    /// <summary>Box: the extent along x, y and z.</summary>
    public Vector3 BoxSize { get; init; } = new(100, 100, 100);
    public float Radius { get; init; } = 100;
    /// <summary>Circle: the angle step (radians) between emissions when not random.</summary>
    public float Step { get; init; } = 0.1f;
    public bool EmitRandom { get; init; } = true;
    /// <summary>Circle / sphere: particles leave along the radius (the direction attribute is ignored).</summary>
    public bool AutoDirection { get; init; }
    public bool ForceEmission { get; init; }
    public bool KeepLocal { get; init; }
    /// <summary>Line: the end point (the line runs from <see cref="Position"/> to it).</summary>
    public Vector3 End { get; init; }
    /// <summary><c>emits technique_particle|emitter_particle|system_particle name</c>: what an emitted particle becomes (not simulated in part one).</summary>
    public string? EmitsKind { get; init; }
    public string? EmitsName { get; init; }
    public required PuNode Source { get; init; }
}

public sealed class PuAffectorDef
{
    public required PuAffectorType Type { get; init; }
    public required string TypeName { get; init; }
    public string Name { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public Vector3 Position { get; init; }
    public IReadOnlyList<string> ExcludedEmitters { get; init; } = [];
    public float MassAffector { get; init; }
    // Colour
    public IReadOnlyList<(float Time, Vector4 Colour)> TimeColours { get; init; } = [];
    public bool MultiplyColour { get; init; }
    // Scale (units per second added to the size, times the system scale; xyz drives all three and then x / y / z are not read)
    public PuDynamic? ScaleX { get; init; }
    public PuDynamic? ScaleY { get; init; }
    public PuDynamic? ScaleZ { get; init; }
    public PuDynamic? ScaleXyz { get; init; }
    /// <summary>Scale: the rates are read at the system's age instead of the particle's life fraction.</summary>
    public bool SinceStartSystem { get; init; }
    // TextureRotator
    public PuDynamic? Rotation { get; init; }
    public PuDynamic? RotationSpeed { get; init; }
    public bool UseOwnRotation { get; init; }
    // LinearForce, SineForce (force_vector); Gravity (gravity); Jet (acceleration)
    public Vector3 Force { get; init; }
    public bool AverageForce { get; init; }
    public PuDynamic? Strength { get; init; }
    public float MinFrequency { get; init; }
    public float MaxFrequency { get; init; }
    // Vortex
    public Vector3 Axis { get; init; } = Vector3.UnitY;
    // Randomiser
    public Vector3 MaxDeviation { get; init; }
    public float TimeStep { get; init; }
    public required PuNode Source { get; init; }
}

public sealed class PuObserverDef
{
    public required PuObserverType Type { get; init; }
    public required string TypeName { get; init; }
    public bool Enabled { get; init; } = true;
    public float Interval { get; init; }
    /// <summary>OnPosition: which axis (0 x, 1 y, 2 z), how it compares and with what. OnTime: the time in <see cref="Value"/>.</summary>
    public int Axis { get; init; } = -1;
    public PuCompare Compare { get; init; }
    public float Value { get; init; }
    public bool SinceStartSystem { get; init; }
    public IReadOnlyList<string> Handlers { get; init; } = [];
    public required PuNode Source { get; init; }
}

public sealed class PuRendererDef
{
    public required string TypeName { get; init; }
    public bool IsBillboard => TypeName == "Billboard";
    /// <summary>point (default), oriented_common, oriented_self, oriented_shape, perpendicular_common, perpendicular_self.</summary>
    public string BillboardType { get; init; } = "point";
    /// <summary>center (default), bottom_center, top_center, center_left, center_right, bottom_left...</summary>
    public string Origin { get; init; } = "center";
    /// <summary>"texcoord" (default) or "vertex": which of the quad's rotations the TextureRotator drives. Drawn the same here.</summary>
    public string RotationType { get; init; } = "texcoord";
    public Vector3 CommonDirection { get; init; } = Vector3.UnitZ;
    public Vector3 CommonUp { get; init; } = Vector3.UnitY;
    public bool Sorting { get; init; }
    public bool AccurateFacing { get; init; }
}

public sealed class PuTechniqueDef
{
    public string Name { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public int VisualQuota { get; init; } = 500;
    public int EmittedEmitterQuota { get; init; } = 50;
    public int EmittedTechniqueQuota { get; init; } = 50;
    public string Material { get; init; } = "";
    public float DefaultWidth { get; init; } = 100;
    public float DefaultHeight { get; init; } = 100;
    public float DefaultDepth { get; init; } = 100;
    public Vector3 Position { get; init; }
    public bool KeepLocal { get; init; }
    public required PuRendererDef Renderer { get; init; }
    public IReadOnlyList<PuEmitterDef> Emitters { get; init; } = [];
    public IReadOnlyList<PuAffectorDef> Affectors { get; init; } = [];
    public IReadOnlyList<PuObserverDef> Observers { get; init; } = [];
    public required PuNode Source { get; init; }
}

public sealed class PuSystemDef
{
    public required string Name { get; init; }
    public string File { get; init; } = "";
    public string Category { get; init; } = "";
    /// <summary>The system's scale: it multiplies emitter extents, positions and particle sizes (Observed).</summary>
    public Vector3 Scale { get; init; } = Vector3.One;
    /// <summary>It multiplies the emitted velocities (Observed).</summary>
    public float ScaleVelocity { get; init; } = 1;
    /// <summary><c>fast_forward time interval</c>: when the system starts it is stepped for <c>time</c> seconds in steps of <c>interval</c>.</summary>
    public (float Time, float Interval)? FastForward { get; init; }
    public float IterationInterval { get; init; }
    public bool KeepLocal { get; init; }
    public IReadOnlyList<PuTechniqueDef> Techniques { get; init; } = [];
    public required PuNode Source { get; init; }

    /// <summary>The largest extent of the Box emitters (width, height, depth) and the Circle emitters' diameter, times the scale's largest component (the EFFECT loader's size measure; docs/formats/weather.md).</summary>
    public float LargestEmitterExtent
    {
        get
        {
            float m = 0;
            foreach (var e in Techniques.SelectMany(t => t.Emitters))
            {
                if (e.Type == PuEmitterType.Box) m = Math.Max(m, Math.Max(e.BoxSize.X * Scale.X, Math.Max(e.BoxSize.Y * Scale.Y, e.BoxSize.Z * Scale.Z)));
                else if (e.Type == PuEmitterType.Circle) m = Math.Max(m, e.Radius * 2 * Math.Max(Scale.X, Scale.Z));
            }
            return m;
        }
    }

    /// <summary>
    /// A bound on how far from the system's node a particle can get (Observed, a viewer measure for culling): the emitter's extent, plus the
    /// speed times the life, plus the linear forces' reach, plus the particle's largest size (with the scale affectors' growth), times the scale.
    /// The camera and global groups (wrapped round the camera) do not use it.
    /// </summary>
    public float BoundingRadius
    {
        get
        {
            float radius = 0;
            float scale = Math.Max(Scale.X, Math.Max(Scale.Y, Scale.Z));
            foreach (var t in Techniques)
            {
                if (!t.Enabled) continue;
                float growth = 0;
                foreach (var a in t.Affectors.Where(a => a.Type == PuAffectorType.Scale))
                    growth += (a.ScaleXyz is not null ? [a.ScaleXyz] : new[] { a.ScaleX, a.ScaleY, a.ScaleZ }).Where(d => d is not null).Select(d => Math.Max(Math.Abs(d!.Range.Min), Math.Abs(d.Range.Max))).DefaultIfEmpty(0).Max() * scale;
                float force = t.Affectors.Where(a => a.Type == PuAffectorType.LinearForce).Select(a => a.Force.Length()).DefaultIfEmpty(0).Sum();
                foreach (var e in t.Emitters)
                {
                    float life = e.Life.Range.Max;
                    float extent = e.Type switch
                    {
                        PuEmitterType.Box => e.BoxSize.Length() * 0.5f * scale,
                        PuEmitterType.Circle or PuEmitterType.SphereSurface => e.Radius * scale,
                        PuEmitterType.Line => e.End.Length() * scale,
                        _ => 0,
                    };
                    float speed = Math.Max(Math.Abs(e.Velocity.Range.Min), Math.Abs(e.Velocity.Range.Max)) * ScaleVelocity;
                    float size = (e.AllDimensions is not null ? [e.AllDimensions] : new[] { e.Width, e.Height }).Where(d => d is not null).Select(d => d!.Range.Max).DefaultIfEmpty(Math.Max(t.DefaultWidth, t.DefaultHeight)).Max() * scale;
                    float reach = (t.Position + e.Position * Scale).Length() + extent + speed * life + 0.5f * force * life * life + size + growth * life;
                    radius = Math.Max(radius, reach);
                }
            }
            return Math.Min(radius, 30000);
        }
    }

    /// <summary>Builds the typed system from a parsed <c>system</c> block.</summary>
    public static PuSystemDef From(PuNode system, string file = "")
    {
        var scale = system.Vec3("scale") ?? Vector3.One;
        (float, float)? ff = system.Find("fast_forward") is { Values.Length: >= 2 } f && PuProperty.TryFloat(f.Values[0], out float ft) && PuProperty.TryFloat(f.Values[1], out float fi) ? (ft, fi) : null;
        return new PuSystemDef
        {
            Name = system.Name, File = file, Source = system,
            Category = system.Word("category") ?? "",
            Scale = scale,
            ScaleVelocity = system.Float("scale_velocity", 1),
            FastForward = ff,
            IterationInterval = system.Float("iteration_interval"),
            KeepLocal = system.Bool("keep_local"),
            Techniques = [.. system.Blocks("technique").Select(Technique)],
        };
    }

    static PuTechniqueDef Technique(PuNode t)
    {
        var rendererNode = t.Blocks("renderer").LastOrDefault();
        return new PuTechniqueDef
        {
            Name = t.Name, Source = t,
            Enabled = t.Bool("enabled", true),
            VisualQuota = (int)t.Float("visual_particle_quota", 500),
            EmittedEmitterQuota = (int)t.Float("emitted_emitter_quota", 50),
            EmittedTechniqueQuota = (int)t.Float("emitted_technique_quota", 50),
            Material = t.Word("material") ?? "",
            DefaultWidth = t.Float("default_particle_width", 100),
            DefaultHeight = t.Float("default_particle_height", 100),
            DefaultDepth = t.Float("default_particle_depth", 100),
            Position = t.Vec3("position") ?? Vector3.Zero,
            KeepLocal = t.Bool("keep_local"),
            Renderer = rendererNode is null ? new PuRendererDef { TypeName = "Billboard" } : Renderer(rendererNode),
            Emitters = [.. t.Blocks("emitter").Select(Emitter)],
            Affectors = [.. t.Blocks("affector").Select(Affector)],
            Observers = [.. t.Blocks("observer").Select(Observer)],
        };
    }

    static PuRendererDef Renderer(PuNode r) => new()
    {
        TypeName = r.Type,
        BillboardType = r.Word("billboard_type") ?? "point",
        Origin = r.Word("billboard_origin") ?? "center",
        RotationType = r.Word("billboard_rotation_type") ?? "texcoord",
        CommonDirection = r.Vec3("common_direction") ?? Vector3.UnitZ,
        CommonUp = r.Vec3("common_up_vector") ?? Vector3.UnitY,
        Sorting = r.Bool("sorting"),
        AccurateFacing = r.Bool("accurate_facing"),
    };

    static PuEmitterDef Emitter(PuNode e)
    {
        var type = e.Type switch
        {
            "Box" => PuEmitterType.Box, "Circle" => PuEmitterType.Circle, "Point" => PuEmitterType.Point,
            "SphereSurface" => PuEmitterType.SphereSurface, "Line" => PuEmitterType.Line, "Slave" => PuEmitterType.Slave,
            _ => PuEmitterType.Unknown,
        };
        var emits = e.Find("emits");
        return new PuEmitterDef
        {
            Type = type, TypeName = e.Type, Name = e.Name, Source = e,
            Enabled = e.Bool("enabled", true),
            Rate = e.Dynamic("emission_rate") ?? PuDynamic.Fixed(10),
            Life = e.Dynamic("time_to_live") ?? PuDynamic.Fixed(3),
            Velocity = e.Dynamic("velocity") ?? PuDynamic.Fixed(100),
            Angle = e.Dynamic("angle") ?? PuDynamic.Fixed(0),
            Mass = e.Dynamic("mass") ?? PuDynamic.Fixed(1),
            Duration = e.Dynamic("duration"),
            RepeatDelay = e.Dynamic("repeat_delay"),
            Width = e.Dynamic("particle_width"), Height = e.Dynamic("particle_height"), Depth = e.Dynamic("particle_depth"),
            AllDimensions = e.Dynamic("all_particle_dimensions"),
            Position = e.Vec3("position") ?? Vector3.Zero,
            Direction = e.Vec3("direction") ?? Vector3.UnitY,
            Colour = e.Vec4("colour") ?? Vector4.One,
            ColourStart = e.Vec4("start_colour_range"),
            ColourEnd = e.Vec4("end_colour_range"),
            BoxSize = new Vector3(e.Float("box_width", 100), e.Float("box_height", 100), e.Float("box_depth", 100)),
            Radius = e.Float("radius", 100),
            Step = e.Float("step", 0.1f),
            EmitRandom = e.Bool("emit_random", true),
            AutoDirection = e.Bool("auto_direction"),
            ForceEmission = e.Bool("force_emission"),
            KeepLocal = e.Bool("keep_local"),
            End = e.Vec3("end") ?? Vector3.Zero,
            EmitsKind = emits is { Values.Length: >= 2 } ? emits.Values[0] : null,
            EmitsName = emits is { Values.Length: >= 2 } ? emits.Values[1] : null,
        };
    }

    static PuAffectorDef Affector(PuNode a)
    {
        var type = a.Type switch
        {
            "Colour" => PuAffectorType.Colour, "Scale" => PuAffectorType.Scale, "TextureRotator" => PuAffectorType.TextureRotator,
            "LinearForce" => PuAffectorType.LinearForce, "Vortex" => PuAffectorType.Vortex, "Gravity" => PuAffectorType.Gravity,
            "Randomiser" => PuAffectorType.Randomiser, "SineForce" => PuAffectorType.SineForce, "ForceField" => PuAffectorType.ForceField,
            "Jet" => PuAffectorType.Jet, "Align" => PuAffectorType.Align, "FlockCentering" => PuAffectorType.FlockCentering,
            "GeometryRotator" => PuAffectorType.GeometryRotator, "ScaleVelocity" => PuAffectorType.ScaleVelocity,
            _ => PuAffectorType.Unknown,
        };
        var colours = new List<(float, Vector4)>();
        foreach (var p in a.All("time_colour"))
            if (p.Values.Length >= 5 && PuProperty.TryFloat(p.Values[0], out float t) && PuProperty.TryFloat(p.Values[1], out float r) && PuProperty.TryFloat(p.Values[2], out float g)
                && PuProperty.TryFloat(p.Values[3], out float b) && PuProperty.TryFloat(p.Values[4], out float al))
                colours.Add((t, new Vector4(r, g, b, al)));
        colours.Sort((x, y) => x.Item1.CompareTo(y.Item1));
        return new PuAffectorDef
        {
            Type = type, TypeName = a.Type, Name = a.Name, Source = a,
            Enabled = a.Bool("enabled", true),
            Position = a.Vec3("position") ?? Vector3.Zero,
            ExcludedEmitters = [.. a.All("exclude_emitter").Where(p => p.Values.Length > 0).Select(p => p.Values[0])],
            MassAffector = a.Float("mass_affector"),
            TimeColours = colours,
            MultiplyColour = a.Word("colour_operation") == "multiply",
            ScaleX = a.Dynamic("x_scale"), ScaleY = a.Dynamic("y_scale"), ScaleZ = a.Dynamic("z_scale"), ScaleXyz = a.Dynamic("xyz_scale"),
            SinceStartSystem = a.Bool("since_start_system"),
            Rotation = a.Dynamic("rotation"), RotationSpeed = a.Dynamic("rotation_speed"), UseOwnRotation = a.Bool("use_own_rotation"),
            Force = a.Vec3("force_vector") ?? (a.Vec3("acceleration") ?? Vector3.Zero),
            AverageForce = a.Word("force_application") == "average",
            Strength = a.Dynamic("gravity") ?? a.Dynamic("acceleration"),
            MinFrequency = a.Float("min_frequency"), MaxFrequency = a.Float("max_frequency"),
            Axis = a.Vec3("rotation_axis") ?? Vector3.UnitY,
            MaxDeviation = new Vector3(a.Float("max_deviation_x"), a.Float("max_deviation_y"), a.Float("max_deviation_z")),
            TimeStep = a.Float("time_step"),
        };
    }

    static PuObserverDef Observer(PuNode o)
    {
        var type = o.Type switch
        {
            "OnPosition" => PuObserverType.OnPosition, "OnTime" => PuObserverType.OnTime, "OnClear" => PuObserverType.OnClear,
            "OnEmission" => PuObserverType.OnEmission, _ => PuObserverType.Unknown,
        };
        int axis = -1;
        PuCompare compare = PuCompare.LessThan;
        float value = 0;
        foreach (var p in o.Properties)
        {
            if (p.Name is "position_x" or "position_y" or "position_z" && p.Values.Length >= 2)
            {
                axis = p.Name[^1] - 'x';
                (compare, value) = Compare(p.Values);
            }
            else if (p.Name == "on_time" && p.Values.Length >= 2) (compare, value) = Compare(p.Values);
        }
        return new PuObserverDef
        {
            Type = type, TypeName = o.Type, Source = o,
            Enabled = o.Bool("enabled", true),
            Interval = o.Float("observe_interval"),
            Axis = axis, Compare = compare, Value = value,
            SinceStartSystem = o.Bool("since_start_system"),
            Handlers = [.. o.Blocks("handler").Select(h => h.Type)],
        };
    }

    static (PuCompare, float) Compare(string[] v)
    {
        var c = v[0] switch { "less_than" => PuCompare.LessThan, "greater_than" => PuCompare.GreaterThan, _ => PuCompare.Equals };
        return (c, PuProperty.TryFloat(v[1], out float f) ? f : 0);
    }
}
