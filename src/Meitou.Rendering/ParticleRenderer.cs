using System.Diagnostics;
using System.Numerics;
using Meitou.Content;
using Meitou.Data.Particles;
using Meitou.Data.Textures;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// ParticleUniverse particles as instanced billboards (docs/formats/particle-universe.md "Drawing"): the weather's effect groups and the map's
/// effect placers are simulated on the CPU (<see cref="EffectGroup"/>), each technique's particles are written into the frame's constants (a ring
/// per frame slot, no stall) and drawn as one instanced quad strip with its material's texture and blend, after the opaque scene, depth tested and
/// never written. Fog volumes (the twisters' dust balls) are drawn as analytic soft spheres. Nothing is recorded while no unit has a particle, so
/// a clear weather with no placer in sight draws exactly what it did.
/// </summary>
public sealed unsafe class ParticleRenderer : IDisposable
{
    const string Vertex = """
        #version 330 core
        layout(location = 0) in vec2 aCorner;       // the quad's corner, -0.5 .. 0.5, in triangle-strip order
        layout(location = 1) in vec4 aPositionRotation;   // camera-relative position, texture rotation (radians)
        layout(location = 2) in vec4 aDirectionWidth;     // unit direction of travel, width
        layout(location = 3) in vec4 aColour;
        layout(location = 4) in vec4 aHeight;             // height, depth
        uniform mat4 uViewProjection;               // world-from-camera-relative included: the positions are relative to the eye
        uniform vec3 uCameraRight;
        uniform vec3 uCameraUp;
        uniform int uMode;                          // 0 point, 1 oriented_common, 2 oriented_self, 3 perpendicular_common, 4 perpendicular_self
        uniform vec2 uAnchor;                       // the origin of the quad as an offset from its centre, in widths and heights (bottom_center: 0, -0.5)
        uniform vec3 uCommonDirection;
        uniform vec3 uCommonUp;
        uniform vec4 uColourScale;                  // the fragment program's `colour` parameter, with the ambient factor in rgb
        out vec2 vUv;
        out vec4 vColour;
        void main()
        {
            vec3 position = aPositionRotation.xyz;
            float width = aDirectionWidth.w;
            float height = aHeight.x;
            vec2 q = (aCorner - uAnchor) * vec2(width, height);
            vec3 right, up;
            if (uMode == 0)
            {
                float c = cos(aPositionRotation.w), s = sin(aPositionRotation.w);
                q = vec2(c * q.x - s * q.y, s * q.x + c * q.y);
                right = uCameraRight;
                up = uCameraUp;
            }
            else if (uMode == 1 || uMode == 2)
            {
                // The quad's up axis is the common direction (1) or the direction of travel (2); it turns about it to face the eye.
                up = uMode == 1 ? normalize(uCommonDirection) : (dot(aDirectionWidth.xyz, aDirectionWidth.xyz) > 0.5 ? aDirectionWidth.xyz : uCameraUp);
                vec3 toEye = -position;
                right = cross(up, toEye);
                float l = length(right);
                right = l > 1e-5 ? right / l : uCameraRight;
            }
            else
            {
                // A flat quad: facing along the common direction (3) or the direction of travel (4), its up the common up vector.
                vec3 normal = uMode == 3 ? normalize(uCommonDirection) : (dot(aDirectionWidth.xyz, aDirectionWidth.xyz) > 0.5 ? aDirectionWidth.xyz : normalize(uCommonDirection));
                up = uCommonUp;
                right = cross(up, normal);
                float l = length(right);
                right = l > 1e-5 ? right / l : uCameraRight;
                up = normalize(cross(normal, right));
            }
            vec3 world = position + right * q.x + up * q.y;
            gl_Position = uViewProjection * vec4(world, 1.0);
            vUv = vec2(aCorner.x + 0.5, 0.5 - aCorner.y);
            vColour = vec4(aColour.rgb * uColourScale.rgb, aColour.a * uColourScale.a);
        }
        """;

    const string Fragment = """
        #version 330 core
        in vec2 vUv;
        in vec4 vColour;
        out vec4 fragColour;
        uniform sampler2D uTexture;
        uniform float uCoverageMax;
        uniform int uCoverage;                      // 0: colour; 1: the particle's alpha, 2: its brightest channel (the upscalers' reactive mask)
        void main()
        {
            // basic.hlsl: texture * colour * vertex colour
            vec4 c = texture(uTexture, vUv) * vColour;
            // Thin streaks have a small alpha per pixel: any visible particle pixel counts as mostly "new" (30x, saturating; Observed choice).
            if (uCoverage == 1) c = vec4(clamp(max(c.a, max(c.r, max(c.g, c.b))) * 30.0, 0.0, uCoverageMax));
            else if (uCoverage == 2) c = vec4(clamp(max(c.r, max(c.g, c.b)) * c.a * 30.0, 0.0, uCoverageMax));
            fragColour = c;
        }
        """;

    // The fog volumes (docs/formats/weather.md "Fog volumes", Observed: how the game draws them is unknown): a quad facing the eye at the sphere's
    // nearest point (so the scene's depth hides it where something is in front of the sphere), each pixel's ray cut by the sphere analytically; the
    // alpha follows the chord's length through the sphere over the volume's density distance.
    const string FogVertex = """
        #version 330 core
        layout(location = 0) in vec2 aCorner;
        uniform mat4 uViewProjection;
        uniform vec4 uQuad;                         // camera-relative centre of the quad, half extent
        uniform vec3 uRight;
        uniform vec3 uUp;
        out vec3 vRay;
        void main()
        {
            vec3 p = uQuad.xyz + (uRight * aCorner.x + uUp * aCorner.y) * (2.0 * uQuad.w);
            gl_Position = uViewProjection * vec4(p, 1.0);
            vRay = p;
        }
        """;

    const string FogFragment = """
        #version 330 core
        in vec3 vRay;
        out vec4 fragColour;
        uniform vec4 uSphere;                       // camera-relative centre, radius
        uniform vec4 uColour;                       // rgb, alpha of the volume (with the fade)
        uniform vec4 uDensity;                      // the density distance
        void main()
        {
            vec3 d = normalize(vRay);
            float b = dot(d, uSphere.xyz);
            float h = b * b - (dot(uSphere.xyz, uSphere.xyz) - uSphere.w * uSphere.w);
            if (h <= 0.0) discard;
            float s = sqrt(h);
            float t0 = max(b - s, 0.0), t1 = b + s;
            if (t1 <= 0.0) discard;
            float a = uColour.a * (1.0 - exp(-2.0 * (t1 - t0) / uDensity.x));
            fragColour = vec4(uColour.rgb, a);
        }
        """;

    const int InstanceBytes = 64;
    /// <summary>The farthest a unit is drawn (the near depth slice's far plane is about this).</summary>
    public const float DrawRange = 20400;

    readonly GpuContext gpu;
    readonly string texturesDirectory;
    readonly LegacyProgram program;
    readonly NativeSegment segment;
    readonly LegacyProgram fogProgram;
    readonly NativeSegment fogSegment;
    readonly DeviceBuffer quad;
    readonly VertexArrayBindings vertexSource, fogVertexSource;
    readonly BufferBinding[] bindings = new BufferBinding[5];
    readonly BufferBinding[] fogBindings = new BufferBinding[1];
    readonly UniformHandle viewProjection, cameraRight, cameraUp, mode, anchor, commonDirection, commonUp, colourScale, coverageMode, coverageLimit;
    readonly UniformHandle fogViewProjection, fogQuad, fogRight, fogUp, fogSphere, fogColour, fogDensity;
    readonly SamplerSlot textureSlot;
    readonly SampledImage white;
    readonly Dictionary<string, SampledImage?> textures = new(StringComparer.OrdinalIgnoreCase);
    readonly List<EffectGroup> groups = [];
    readonly List<EffectGroup> placerGroups = [];
    readonly List<EffectGroup> toWarm = [];
    readonly List<DrawItem> draws = [];
    WeatherEffectInput input = WeatherEffectInput.None;
    object? inputIdentity;
    int inputVersion = -1;
    double lastSeconds = double.NaN;
    int drawnParticles, drawnUnits, activeUnits, drawnFog;
    Task? simulation;
    long simulationTicks, waitTicks, simulationTotal, waitTotal, simulationFrames;
    /// <summary>Mean simulation and main-thread wait per simulated frame since the start (for the benchmarks).</summary>
    public (double Simulation, double Wait) MeanMilliseconds => simulationFrames == 0 ? (0, 0) : (simulationTotal * 1000.0 / Stopwatch.Frequency / simulationFrames, waitTotal * 1000.0 / Stopwatch.Frequency / simulationFrames);

    public ParticleLibrary Library { get; }
    /// <summary>What the groups need of the world: the ground height, the area effects are placed in. The viewer fills it once; the scheduler's hookup can change <see cref="EffectWorld.Area"/> with the region.</summary>
    public EffectWorld World { get; } = new();
    /// <summary>The viewer's <c>--no-particles</c>: nothing is simulated or drawn.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Seconds simulated before the first picture, for the new groups: null (the default) is each system's longest particle life, capped at <see cref="MaxAutoPrewarm"/>; 0 starts empty.</summary>
    public float? PrewarmSeconds { get; set; }
    public const float MaxAutoPrewarm = 40;
    /// <summary>The seed of the groups' random numbers (a group takes the seed plus its place in the list).</summary>
    public int Seed { get; set; } = 1;
    /// <summary>Particles drawn in the last frame, for the statistics.</summary>
    public int DrawnParticles => drawnParticles;
    public int DrawnUnits => drawnUnits;
    public int DrawnFogVolumes => drawnFog;
    public int ActiveUnits => activeUnits;
    /// <summary>The last frame's simulation time (summed over its threads' work: the wall time of the background task) and how long the main thread waited for it, in milliseconds.</summary>
    public double SimulationMilliseconds => simulationTicks * 1000.0 / Stopwatch.Frequency;
    public double WaitMilliseconds => waitTicks * 1000.0 / Stopwatch.Frequency;
    public int ParticleCount { get { Sync(); return groups.Concat(placerGroups).Sum(g => g.ParticleCount); } }
    /// <summary>The weather's groups (not the map placers').</summary>
    public IReadOnlyList<EffectGroup> Groups => groups;
    public IReadOnlyList<EffectGroup> PlacerGroups => placerGroups;
    /// <summary>The entries of the weather's effect list that have no group (a type that is not a weather effect, or an unknown particle system).</summary>
    public IReadOnlyList<WeatherEffectEntry> Skipped => skipped;
    readonly List<WeatherEffectEntry> skipped = [];

    struct DrawItem
    {
        public Transient Instances;
        public int Count, Close;
        public PuTechniqueDef Technique;
        public ParticleMaterial Material;
        public SampledImage Texture;
        // a fog volume instead of particles (Technique is null then)
        public bool IsFog;
        public Vector3 FogCentre;
        public float FogRadius, FogDensity, FogAlpha;
        public Vector3 FogColour;
    }

    ParticleRenderer(GpuContext gpu, string texturesDirectory, ParticleLibrary library)
    {
        this.gpu = gpu;
        this.texturesDirectory = texturesDirectory;
        Library = library;
        program = LegacyProgram.Create(gpu, Vertex, Fragment, "particles");
        segment = new NativeSegment(gpu, program, Silk.NET.Vulkan.PrimitiveTopology.TriangleStrip, "particles");
        viewProjection = program.Uniform("uViewProjection");
        cameraRight = program.Uniform("uCameraRight");
        cameraUp = program.Uniform("uCameraUp");
        mode = program.Uniform("uMode");
        anchor = program.Uniform("uAnchor");
        commonDirection = program.Uniform("uCommonDirection");
        commonUp = program.Uniform("uCommonUp");
        colourScale = program.Uniform("uColourScale");
        coverageMode = program.Uniform("uCoverage");
        coverageLimit = program.Uniform("uCoverageMax");
        textureSlot = program.Sampler("uTexture");
        fogProgram = LegacyProgram.Create(gpu, FogVertex, FogFragment, "particle fog volumes");
        fogSegment = new NativeSegment(gpu, fogProgram, Silk.NET.Vulkan.PrimitiveTopology.TriangleStrip, "particle fog volumes");
        fogViewProjection = fogProgram.Uniform("uViewProjection");
        fogQuad = fogProgram.Uniform("uQuad");
        fogRight = fogProgram.Uniform("uRight");
        fogUp = fogProgram.Uniform("uUp");
        fogSphere = fogProgram.Uniform("uSphere");
        fogColour = fogProgram.Uniform("uColour");
        fogDensity = fogProgram.Uniform("uDensity");
        float[] corners = [-0.5f, -0.5f, -0.5f, 0.5f, 0.5f, -0.5f, 0.5f, 0.5f];   // triangle strip
        quad = DeviceBuffer.Create(gpu, sizeof(float) * (ulong)corners.Length, BufferUse.Vertex, "particle quad");
        using (var batch = gpu.Uploads.Begin()) batch.Write(quad, 0, System.Runtime.InteropServices.MemoryMarshal.AsBytes(corners.AsSpan()));
        var attributes = new LegacyProgram.Attribute?[5];
        attributes[0] = WaterRenderer.QuadAttribute(quad);
        for (int a = 0; a < 4; a++)
            attributes[1 + a] = new LegacyProgram.Attribute(default, Silk.NET.Vulkan.Format.R32G32B32A32Sfloat, InstanceBytes, true);
        vertexSource = new VertexArrayBindings(attributes, default);
        fogVertexSource = new VertexArrayBindings([WaterRenderer.QuadAttribute(quad)], default);
        white = SampledImage.Rgba8(gpu, 1, 1, [255, 255, 255, 255], repeat: false, mipmaps: false, "particle white");
    }

    public static ParticleRenderer Create(GpuContext gpu, GameInstall install, ParticleLibrary? library = null) =>
        new(gpu, Path.Combine(install.DataDirectory, "particles", "textures"), library ?? ParticleLibrary.Load(install));

    /// <summary>
    /// Sets the weather's effect list. Groups are rebuilt when the list object or its <see cref="WeatherEffectInput.Version"/> changed; an entry whose
    /// type is not a weather effect (NONE) or whose particle system is unknown is skipped.
    /// </summary>
    public void SetWeather(WeatherEffectInput weather)
    {
        Sync();
        if (ReferenceEquals(weather, inputIdentity) && weather.Version == inputVersion) { input = weather; return; }
        (input, inputIdentity, inputVersion) = (weather, weather, weather.Version);
        toWarm.RemoveAll(groups.Contains);
        groups.Clear();
        skipped.Clear();
        int i = 0;
        foreach (var entry in weather.Effects)
        {
            if (EffectGroups.Create(entry, Library, Seed + i++, World) is { } group) { groups.Add(group); toWarm.Add(group); }
            else skipped.Add(entry);
        }
    }

    /// <summary>
    /// The map's effect placers (docs/formats/weather.md "Effect placers on the map"): always on, whatever the weather. Replaces the previous list.
    /// </summary>
    public void SetPlacers(IReadOnlyList<MapEffectPlacement> placements)
    {
        Sync();
        toWarm.RemoveAll(placerGroups.Contains);
        placerGroups.Clear();
        placerGroups.AddRange(MapEffectPlacers.Groups(placements, Library, World, Seed));
        toWarm.AddRange(placerGroups);
    }

    /// <summary>
    /// Steps the groups to <paramref name="seconds"/> on the frame clock (the first call only sets the clock, so a held-still frame
    /// advances nothing) and, once after a weather change, runs the start-up so particles are present at the first picture. The simulation of the
    /// units (the particles) then runs on the thread pool, one unit per task, while the caller does the rest of its frame; <see cref="Draw"/> waits for it.
    /// </summary>
    public void Update(double seconds, WorldCamera camera)
    {
        long start = Stopwatch.GetTimestamp();
        UpdateInner(seconds, camera);
        updateTotal += Stopwatch.GetTimestamp() - start;
        updateFrames++;
    }

    void UpdateInner(double seconds, WorldCamera camera)
    {
        Sync();
        coveragePending = false;   // the last frame's quads live in its constants: only a Draw in this frame makes coverage
        if (!Enabled || (groups.Count == 0 && placerGroups.Count == 0)) { lastSeconds = seconds; return; }
        float dt = double.IsNaN(lastSeconds) ? 0 : (float)Math.Clamp(seconds - lastSeconds, 0, 0.25);
        lastSeconds = seconds;
        var view = new EffectCamera(camera.Eye, camera.Forward);
        if (toWarm.Count > 0)
        {
            foreach (var g in toWarm.ToArray())
            {
                float warm = PrewarmSeconds ?? Math.Min(ParticleSimulation.LongestLife(g.System), MaxAutoPrewarm);
                if (g is CameraEffectGroup || warm > 0 || g.Entry is not null) g.Prewarm(warm, view, input);
                else g.Prewarm(0, view, input);
            }
            toWarm.Clear();
        }
        foreach (var g in groups) g.Update(dt, view, input);
        foreach (var g in placerGroups) g.Update(dt, view, input);
        StartSimulation();
    }

    void StartSimulation()
    {
        var work = new List<EffectUnit>();
        activeUnits = 0;
        foreach (var g in groups.Concat(placerGroups))
            foreach (var u in g.Units)
            {
                if (!u.Active) continue;
                activeUnits++;
                // Distant units step at a lower rate (the time queues up until a step is due): 20 Hz beyond 4000 units, 10 Hz beyond 9000
                // (Observed choice for the cost; a particle moves about a pixel per step at that range).
                float due = u.DistanceToCamera > 9000 ? 0.1f : u.DistanceToCamera > 4000 ? 0.05f : 0;
                if (u.Pending > 0 && u.Pending >= due) work.Add(u);
            }
        if (work.Count == 0) { simulationTicks = 0; return; }
        // Heavier units first, so the pool's last task is a short one.
        work.Sort((a, b) => b.Pending.CompareTo(a.Pending));
        var array = work.ToArray();
        simulation = Task.Run(() =>
        {
            long start = Stopwatch.GetTimestamp();
            if (array.Length == 1) array[0].Advance();
            else Parallel.ForEach(array, u => u.Advance());
            simulationTicks = Stopwatch.GetTimestamp() - start;
            Interlocked.Add(ref simulationTotal, simulationTicks);
            Interlocked.Increment(ref simulationFrames);
        });
    }

    /// <summary>One line per effect unit with its place relative to the eye (for the viewer's log): which effects exist, how far and in which direction, whether simulated.</summary>
    public IEnumerable<string> Describe(Vector3 eye, int max = 60)
    {
        Sync();
        int n = 0;
        foreach (var g in groups.Concat(placerGroups))
            foreach (var u in g.Units)
            {
                if (g is CameraEffectGroup) continue;
                if (n++ >= max) { yield return "  ..."; yield break; }
                var d = u.Position - eye;
                yield return $"  {u.Effect.Name,-26} [{u.Effect.Type}] {(u.Active ? "sim " : "idle")} {u.Simulation?.ParticleCount ?? 0,6} particles  at {u.DistanceToCamera,7:0} ({d.X:+0;-0}, {d.Y:+0;-0}, {d.Z:+0;-0}) = world ({u.Position.X:0}, {u.Position.Y:0}, {u.Position.Z:0})  life {(float.IsPositiveInfinity(u.Life) ? "inf" : u.Life.ToString("0"))}  radius {u.Radius:0}";
            }
    }

    /// <summary>Waits for the frame's particle simulation.</summary>
    void Sync()
    {
        if (simulation is null) return;
        long start = Stopwatch.GetTimestamp();
        simulation.Wait();
        waitTicks = Stopwatch.GetTimestamp() - start;
        waitTotal += waitTicks;
        simulation = null;
    }

    /// <summary>
    /// Writes this frame's instances into the frame's constants and records the draws into the open pass. Called in the near depth slice
    /// only (units beyond <see cref="DrawRange"/> are not drawn). The viewer's near plane grows with the camera's height (to 200), the game's is a few
    /// units: a particle nearer than the slice's near plane (<paramref name="nearPlane"/>) is drawn with
    /// <paramref name="closeViewProjection"/> (a projection with a near plane of 0.5) and no depth test, which is right because nothing of
    /// the scene is nearer than that plane; the others as usual, tested against the scene's depth. Units are drawn far to near.
    /// </summary>
    public void Draw(Matrix4x4 viewProjectionMatrix, Matrix4x4 closeViewProjection, float nearPlane, Matrix4x4 view, Vector3 eye, Vector3 sunDirection)
    {
        long start = Stopwatch.GetTimestamp();
        DrawInner(viewProjectionMatrix, closeViewProjection, nearPlane, view, eye, sunDirection);
        drawTotal += Stopwatch.GetTimestamp() - start;
        drawFrames++;
    }

    ParticleInstance[] scratch = new ParticleInstance[4096];
    long drawTotal, drawFrames, updateTotal, updateFrames, collectTotal;
    /// <summary>Mean per-frame cost of filling the instance buffers (inside Draw), in ms.</summary>
    public double CollectMilliseconds => drawFrames == 0 ? 0 : collectTotal * 1000.0 / Stopwatch.Frequency / drawFrames;
    /// <summary>Mean main-thread cost of Update and Draw (draw includes the wait for the simulation) per frame, in ms (for the benchmarks).</summary>
    public (double Update, double Draw) MeanMainThreadMilliseconds => (updateFrames == 0 ? 0 : updateTotal * 1000.0 / Stopwatch.Frequency / updateFrames, drawFrames == 0 ? 0 : drawTotal * 1000.0 / Stopwatch.Frequency / drawFrames);

    void DrawInner(Matrix4x4 viewProjectionMatrix, Matrix4x4 closeViewProjection, float nearPlane, Matrix4x4 view, Vector3 eye, Vector3 sunDirection)
    {
        Sync();
        drawnParticles = 0;
        drawnUnits = 0;
        drawnFog = 0;
        coveragePending = false;
        if (!Enabled || (groups.Count == 0 && placerGroups.Count == 0)) return;
        draws.Clear();
        var constants = gpu.Frame.Constants;
        var forward = -new Vector3(view.M13, view.M23, view.M33);   // the view's -Z axis in the world
        var planes = WorldCamera.FrustumPlanes(viewProjectionMatrix);
        var units = new List<EffectUnit>();
        foreach (var g in groups.Concat(placerGroups))
            foreach (var u in g.Units)
            {
                if (u.Radius != float.PositiveInfinity)
                {
                    if (u.DistanceToCamera - u.Radius > DrawRange) continue;
                    if (!InFrustum(planes, u.Position, u.Radius)) continue;
                }
                units.Add(u);
            }
        units.Sort((a, b) => b.DistanceToCamera.CompareTo(a.DistanceToCamera));
        foreach (var u in units)
        {
            var offset = u.Anchor - eye;
            // Fog volumes first (behind the unit's own particles).
            float fade = u.FogFade;
            if (fade > 0)
                foreach (var f in u.Effect.FogVolumes)
                    draws.Add(new DrawItem
                    {
                        IsFog = true, FogCentre = u.Position + f.Offset - eye, FogRadius = f.Radius, FogDensity = Math.Max(f.Distance, 1), FogAlpha = f.Alpha * fade,
                        FogColour = f.Colour,
                    });
            if (u.Simulation is not { } sim || u.CatchingUp) continue;
            drawnUnits++;
            for (int t = 0; t < sim.Techniques.Count; t++)
            {
                int n = sim.TechniqueParticleCount(t);
                var def = sim.Techniques[t];
                if (n == 0 || !def.Enabled || !def.Renderer.IsBillboard) continue;
                var material = Library.FindMaterial(def.Material);
                if (material is null) continue;
                long collectStart = Stopwatch.GetTimestamp();
                // Built in ordinary memory and copied over in one go: the frame constants are write-combined upload memory, where per-field
                // writes and the partition's reads are slow (this was 5 ms for 18000 particles).
                if (scratch.Length < n) scratch = new ParticleInstance[Math.Max(n, scratch.Length * 2)];
                int written = sim.Collect(t, scratch.AsSpan(0, n), u.Tint, u.Alpha, offset);
                if (written == 0) continue;
                int close = PartitionByDepth(scratch.AsSpan(0, written), forward, nearPlane);
                var instances = constants.Allocate((ulong)(written * InstanceBytes), 16);
                scratch.AsSpan(0, written).CopyTo(new Span<ParticleInstance>(instances.Pointer, written));
                collectTotal += Stopwatch.GetTimestamp() - collectStart;
                draws.Add(new DrawItem { Instances = instances, Count = written, Close = close, Technique = def, Material = material, Texture = TextureFor(material) });
                drawnParticles += written;
            }
        }
        if (draws.Count == 0) return;

        var cmd = gpu.BeginGuest("particles");
        var targets = gpu.CurrentTargets();
        bool hasDepth = targets.Formats.Depth != Silk.NET.Vulkan.Format.Undefined, hasColour = targets.Formats.Colour != Silk.NET.Vulkan.Format.Undefined;
        var baseState = gpu.CurrentState();
        // Camera-relative positions: the matrix takes the eye back out (view and projection were built for world positions).
        var vp = Matrix4x4.CreateTranslation(eye) * viewProjectionMatrix;
        var closeVp = Matrix4x4.CreateTranslation(eye) * closeViewProjection;
        var right = new Vector3(view.M11, view.M21, view.M31);
        var up = new Vector3(view.M12, view.M22, view.M32);
        cmd.SetViewport(targets.Viewport);
        cmd.SetScissor(targets.Scissor);
        // The alpha channel of the scene target carries the characters' mask: particles write colour only.
        const Silk.NET.Vulkan.ColorComponentFlags rgb = Silk.NET.Vulkan.ColorComponentFlags.RBit | Silk.NET.Vulkan.ColorComponentFlags.GBit | Silk.NET.Vulkan.ColorComponentFlags.BBit;
        float ambient = Math.Clamp(sunDirection.Y * 5 + 0.2f, 0.1f, 1f);
        (frameVp, frameCloseVp, frameRight, frameUp, frameAmbient) = (vp, closeVp, right, up, ambient);
        RecordDraws(cmd, in baseState, in targets, hasDepth, hasColour, forward, rgb, coverage: 0);
        coveragePending = true;
        gpu.EndGuest(cmd);
    }

    Matrix4x4 frameVp, frameCloseVp;
    Vector3 frameRight, frameUp;
    float frameAmbient;
    bool coveragePending;

    /// <summary>Whether this frame drew particles that <see cref="DrawCoverage"/> has not yet written into the upscalers' reactive mask.</summary>
    public bool HasCoverage => coveragePending && draws.Any(d => !d.IsFog);

    /// <summary>
    /// The frame's particles again into the upscalers' reactive mask (docs/renderer-native.md 8.20): the same quads and matrices as the colour draw, adding
    /// their alpha (or brightness, for additive ones) into <paramref name="mask"/>'s channels. Without it the temporal history keeps about 90% of what was
    /// behind a fast, thin particle (rain), which then barely shows. No depth test (a hidden particle only makes its pixels a little less stable).
    /// Called by the post chain with the target bound (cleared or loaded as it wants), once per frame after <c>Draw</c>.
    /// </summary>
    public void DrawCoverage(Silk.NET.Vulkan.ColorComponentFlags mask)
    {
        if (!HasCoverage) return;
        if (Environment.GetEnvironmentVariable("MEITOU_COVERAGE_LOG") == "1") Console.WriteLine($"coverage  {draws.Count} draws, mask {mask}");
        var cmd = gpu.BeginGuest("particle coverage");
        var targets = gpu.CurrentTargets();
        var baseState = gpu.CurrentState();
        cmd.SetViewport(targets.Viewport);
        cmd.SetScissor(targets.Scissor);
        RecordDraws(cmd, in baseState, in targets, false, true, default, mask, coverage: 1);
        gpu.EndGuest(cmd);
    }

    void RecordDraws(CommandList cmd, in DrawState baseState, in PassTargets targets, bool hasDepth, bool hasColour, Vector3 forward,
        Silk.NET.Vulkan.ColorComponentFlags rgb, int coverage)
    {
        var (vp, closeVp, right, up, ambient) = (frameVp, frameCloseVp, frameRight, frameUp, frameAmbient);
        foreach (var d in draws)
        {
            if (d.IsFog) { if (coverage == 0) { drawnFog++; DrawFog(cmd, d, in baseState, in targets, hasDepth, hasColour, vp, closeVp, right, up, forward, ambient, rgb); } continue; }
            var m = d.Material;
            var r = d.Technique.Renderer;
            // The far particles first (tested against the scene's depth), then the ones nearer than the slice's near plane (no depth test).
            for (int pass = 0; pass < 2; pass++)
            {
                bool close = pass == 1;
                int first = close ? 0 : d.Close, count = close ? d.Close : d.Count - d.Close;
                if (count == 0) continue;
                var state = baseState with
                {
                    Cull = Silk.NET.Vulkan.CullModeFlags.None, DepthTest = hasDepth && m.DepthCheck && !close, DepthWrite = false, ColourMask = rgb,
                    Blend = coverage != 0 ? new BlendState(true, Silk.NET.Vulkan.BlendFactor.One, Silk.NET.Vulkan.BlendFactor.One) : hasColour ? BlendFor(m.Blend) : BlendState.Off,
                };
                program.Set(coverageMode, coverage == 0 ? 0 : m.Blend == ParticleBlend.Alpha ? 1 : 2);
                program.Set(coverageLimit, rgb == Silk.NET.Vulkan.ColorComponentFlags.ABit ? 2f : 1f);   // the TAA takes half the motion target's alpha, so 2 is "all current frame"
                var matrix = close ? closeVp : vp;
                program.Set(this.viewProjection, in matrix);
                program.Set(cameraRight, right);
                program.Set(cameraUp, up);
                program.Set(mode, ModeOf(r.BillboardType));
                program.Set(anchor, AnchorOf(r.Origin));
                program.Set(commonDirection, r.CommonDirection);
                program.Set(commonUp, r.CommonUp);
                float lit = m.Ambient ? ambient : 1;
                program.Set(colourScale, m.ColourScale.X * lit, m.ColourScale.Y * lit, m.ColourScale.Z * lit, m.ColourScale.W);
                program.Bind(textureSlot, d.Texture.Sampled());
                cmd.SetRaster(state.Cull, state.Front);
                cmd.SetDepth(state.DepthTest, state.DepthWrite, state.Compare);
                cmd.SetDepthBias(state.BiasEnable, state.BiasConstant, state.BiasSlope);
                cmd.BindPipeline(segment.Get(state, targets.Formats, vertexSource));
                bindings[0] = new BufferBinding(quad.Handle, 0);
                for (int a = 0; a < 4; a++) bindings[1 + a] = new BufferBinding(d.Instances.Handle, d.Instances.Offset + (ulong)(16 * a));
                cmd.BindVertexBuffers(0, bindings);
                program.Flush(cmd);
                cmd.Draw(4, (uint)count, 0, (uint)first);
            }
        }
    }

    /// <summary>One fog volume: a quad at the sphere's nearest point, or a screen-filling one (no depth test) when the eye is inside the sphere.</summary>
    void DrawFog(CommandList cmd, in DrawItem d, in DrawState baseState, in PassTargets targets, bool hasDepth, bool hasColour, Matrix4x4 vp, Matrix4x4 closeVp,
        Vector3 cameraRightAxis, Vector3 cameraUpAxis, Vector3 forward, float ambient, Silk.NET.Vulkan.ColorComponentFlags rgb)
    {
        float dist = d.FogCentre.Length();
        bool inside = dist <= d.FogRadius * 1.02f;
        Vector4 quadData;
        Vector3 axisRight, axisUp;
        if (inside)
        {
            // Close to the eye on the view axis, wide enough for any field of view (the close projection's near plane is 0.5).
            quadData = new Vector4(forward * 1.0f, 4);
            axisRight = cameraRightAxis;
            axisUp = cameraUpAxis;
        }
        else
        {
            var n = d.FogCentre / dist;
            float nearest = dist - d.FogRadius;
            float half = nearest * d.FogRadius / MathF.Sqrt(Math.Max(dist * dist - d.FogRadius * d.FogRadius, 1)) * 1.06f;
            var helper = MathF.Abs(n.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
            axisRight = Vector3.Normalize(Vector3.Cross(helper, n));
            axisUp = Vector3.Cross(n, axisRight);
            quadData = new Vector4(n * nearest, half);
        }
        var state = baseState with
        {
            Cull = Silk.NET.Vulkan.CullModeFlags.None, DepthTest = hasDepth && !inside, DepthWrite = false, ColourMask = rgb,
            Blend = hasColour ? BlendFor(ParticleBlend.Alpha) : BlendState.Off,
        };
        var matrix = inside ? closeVp : vp;
        fogProgram.Set(fogViewProjection, in matrix);
        fogProgram.Set(fogQuad, quadData);
        fogProgram.Set(fogRight, axisRight);
        fogProgram.Set(fogUp, axisUp);
        fogProgram.Set(fogSphere, d.FogCentre.X, d.FogCentre.Y, d.FogCentre.Z, d.FogRadius);
        fogProgram.Set(fogColour, d.FogColour.X * ambient, d.FogColour.Y * ambient, d.FogColour.Z * ambient, d.FogAlpha);
        fogProgram.Set(fogDensity, d.FogDensity, 0, 0, 0);
        cmd.SetRaster(state.Cull, state.Front);
        cmd.SetDepth(state.DepthTest, state.DepthWrite, state.Compare);
        cmd.SetDepthBias(state.BiasEnable, state.BiasConstant, state.BiasSlope);
        cmd.BindPipeline(fogSegment.Get(state, targets.Formats, fogVertexSource));
        fogBindings[0] = new BufferBinding(quad.Handle, 0);
        cmd.BindVertexBuffers(0, fogBindings);
        fogProgram.Flush(cmd);
        cmd.Draw(4, 1, 0, 0);
    }

    static bool InFrustum(Vector4[] planes, Vector3 centre, float radius)
    {
        foreach (var p in planes)
            if (p.X * centre.X + p.Y * centre.Y + p.Z * centre.Z + p.W < -radius * new Vector3(p.X, p.Y, p.Z).Length()) return false;
        return true;
    }

    /// <summary>Moves the particles whose depth along <paramref name="forward"/> (positions are camera-relative) is under <paramref name="nearPlane"/> to the front; returns how many there are.</summary>
    static int PartitionByDepth(Span<ParticleInstance> instances, Vector3 forward, float nearPlane)
    {
        int front = 0;
        for (int i = 0; i < instances.Length; i++)
        {
            if (Vector3.Dot(instances[i].Position, forward) >= nearPlane) continue;
            (instances[front], instances[i]) = (instances[i], instances[front]);
            front++;
        }
        return front;
    }

    static BlendState BlendFor(ParticleBlend blend) => blend switch
    {
        ParticleBlend.Add => new BlendState(true, Silk.NET.Vulkan.BlendFactor.One, Silk.NET.Vulkan.BlendFactor.One),
        ParticleBlend.Modulate => new BlendState(true, Silk.NET.Vulkan.BlendFactor.DstColor, Silk.NET.Vulkan.BlendFactor.Zero),
        ParticleBlend.ColourBlend => new BlendState(true, Silk.NET.Vulkan.BlendFactor.SrcColor, Silk.NET.Vulkan.BlendFactor.OneMinusSrcColor),
        ParticleBlend.Opaque => BlendState.Off,
        _ => new BlendState(true, Silk.NET.Vulkan.BlendFactor.SrcAlpha, Silk.NET.Vulkan.BlendFactor.OneMinusSrcAlpha),
    };

    static int ModeOf(string billboardType) => billboardType switch
    {
        "oriented_common" => 1,
        "oriented_self" or "oriented_shape" => 2,   // oriented_shape: Unknown, drawn as oriented_self
        "perpendicular_common" => 3,
        "perpendicular_self" => 4,
        _ => 0,
    };

    /// <summary>Ogre's billboard origins as the anchor point's offset from the quad's centre in quad units (y up).</summary>
    static Vector2 AnchorOf(string origin) => origin switch
    {
        "top_left" => new(-0.5f, 0.5f),
        "top_center" => new(0, 0.5f),
        "top_right" => new(0.5f, 0.5f),
        "center_left" => new(-0.5f, 0),
        "center_right" => new(0.5f, 0),
        "bottom_left" => new(-0.5f, -0.5f),
        "bottom_center" => new(0, -0.5f),
        "bottom_right" => new(0.5f, -0.5f),
        _ => Vector2.Zero,
    };

    SampledImage TextureFor(ParticleMaterial material)
    {
        if (material.Texture is not { } name) return white;
        if (textures.TryGetValue(name, out var cached)) return cached ?? white;
        SampledImage? image = null;
        string path = Path.Combine(texturesDirectory, name);
        if (File.Exists(path))
        {
            try
            {
                var loaded = TextureLoader.LoadFile(path, allMips: false);
                image = SampledImage.Rgba8(gpu, loaded.Levels[0], repeat: !material.Clamp, mipmaps: true, "particle " + name);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException)
            {
                Console.WriteLine($"warning   particle texture {name}: {e.Message}");
            }
        }
        textures[name] = image;
        return image ?? white;
    }

    public void Dispose()
    {
        Sync();
        foreach (var t in textures.Values) t?.Dispose();
        white.Dispose();
        quad.Dispose();
        program.Dispose();
        fogProgram.Dispose();
    }
}
