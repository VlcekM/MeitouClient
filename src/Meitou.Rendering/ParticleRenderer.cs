using System.Numerics;
using Meitou.Content;
using Meitou.Data.Particles;
using Meitou.Data.Textures;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// ParticleUniverse particles as instanced billboards (docs/formats/particle-universe.md "Drawing"): the weather's effect groups are
/// simulated on the CPU (<see cref="EffectGroup"/>), each technique's particles are written into the frame's constants (a ring per frame
/// slot, no stall) and drawn as one instanced quad strip with its material's texture and blend, after the opaque scene, depth tested and
/// never written. Nothing is recorded while no group has a particle, so a clear weather draws exactly what it did.
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
        void main()
        {
            // basic.hlsl: texture * colour * vertex colour
            fragColour = texture(uTexture, vUv) * vColour;
        }
        """;

    const int InstanceBytes = 64;

    readonly GpuContext gpu;
    readonly string texturesDirectory;
    readonly LegacyProgram program;
    readonly NativeSegment segment;
    readonly DeviceBuffer quad;
    readonly VertexArrayBindings vertexSource;
    readonly BufferBinding[] bindings = new BufferBinding[5];
    readonly UniformHandle viewProjection, cameraRight, cameraUp, mode, anchor, commonDirection, commonUp, colourScale;
    readonly SamplerSlot textureSlot;
    readonly SampledImage white;
    readonly Dictionary<string, SampledImage?> textures = new(StringComparer.OrdinalIgnoreCase);
    readonly List<EffectGroup> groups = [];
    readonly List<DrawItem> draws = [];
    WeatherEffectInput input = WeatherEffectInput.None;
    object? inputIdentity;
    int inputVersion = -1;
    bool needsPrewarm;
    double lastSeconds = double.NaN;
    int drawnParticles;

    public ParticleLibrary Library { get; }
    /// <summary>The viewer's <c>--no-particles</c>: nothing is simulated or drawn.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Seconds simulated before the first picture, for the new groups: null (the default) is each system's longest particle life, capped at <see cref="MaxAutoPrewarm"/>; 0 starts empty.</summary>
    public float? PrewarmSeconds { get; set; }
    public const float MaxAutoPrewarm = 40;
    /// <summary>The seed of the groups' random numbers (a group takes the seed plus its place in the list).</summary>
    public int Seed { get; set; } = 1;
    /// <summary>Particles drawn in the last frame, for the statistics.</summary>
    public int DrawnParticles => drawnParticles;
    public int ParticleCount => groups.Sum(g => g.Simulation.ParticleCount);
    public IReadOnlyList<EffectGroup> Groups => groups;
    /// <summary>The entries of the weather's effect list that have no group yet (the point, wandering and global types; or an unknown particle system).</summary>
    public IReadOnlyList<WeatherEffectEntry> Skipped => skipped;
    readonly List<WeatherEffectEntry> skipped = [];

    struct DrawItem
    {
        public Transient Instances;
        public int Count, Close;
        public PuTechniqueDef Technique;
        public ParticleMaterial Material;
        public SampledImage Texture;
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
        textureSlot = program.Sampler("uTexture");
        float[] corners = [-0.5f, -0.5f, -0.5f, 0.5f, 0.5f, -0.5f, 0.5f, 0.5f];   // triangle strip
        quad = DeviceBuffer.Create(gpu, sizeof(float) * (ulong)corners.Length, BufferUse.Vertex, "particle quad");
        using (var batch = gpu.Uploads.Begin()) batch.Write(quad, 0, System.Runtime.InteropServices.MemoryMarshal.AsBytes(corners.AsSpan()));
        var attributes = new LegacyProgram.Attribute?[5];
        attributes[0] = WaterRenderer.QuadAttribute(quad);
        for (int a = 0; a < 4; a++)
            attributes[1 + a] = new LegacyProgram.Attribute(default, Silk.NET.Vulkan.Format.R32G32B32A32Sfloat, InstanceBytes, true);
        vertexSource = new VertexArrayBindings(attributes, default);
        white = SampledImage.Rgba8(gpu, 1, 1, [255, 255, 255, 255], repeat: false, mipmaps: false, "particle white");
    }

    public static ParticleRenderer Create(GpuContext gpu, GameInstall install, ParticleLibrary? library = null) =>
        new(gpu, Path.Combine(install.DataDirectory, "particles", "textures"), library ?? ParticleLibrary.Load(install));

    /// <summary>
    /// Sets the weather's effect list. Groups are rebuilt when the list object or its <see cref="WeatherEffectInput.Version"/> changed; a
    /// group whose type has none yet (everything but the camera effects, in this part) is skipped.
    /// </summary>
    public void SetWeather(WeatherEffectInput weather)
    {
        if (ReferenceEquals(weather, inputIdentity) && weather.Version == inputVersion) { input = weather; return; }
        (input, inputIdentity, inputVersion) = (weather, weather, weather.Version);
        groups.Clear();
        skipped.Clear();
        int i = 0;
        foreach (var entry in weather.Effects)
        {
            if (EffectGroups.Create(entry, Library, Seed + i++) is { } group) groups.Add(group);
            else skipped.Add(entry);
        }
        needsPrewarm = groups.Count > 0;
    }

    /// <summary>
    /// Steps the groups to <paramref name="seconds"/> on the frame clock (the first call only sets the clock, so a held-still frame
    /// advances nothing) and, once after a weather change, runs the start-up so particles are present at the first picture.
    /// </summary>
    public void Update(double seconds, WorldCamera camera)
    {
        if (!Enabled || groups.Count == 0) { lastSeconds = seconds; return; }
        float dt = double.IsNaN(lastSeconds) ? 0 : (float)Math.Clamp(seconds - lastSeconds, 0, 0.25);
        lastSeconds = seconds;
        var view = new EffectCamera(camera.Eye, camera.Forward);
        if (needsPrewarm)
        {
            needsPrewarm = false;
            foreach (var g in groups)
            {
                float warm = PrewarmSeconds ?? Math.Min(ParticleSimulation.LongestLife(g.Simulation.Definition), MaxAutoPrewarm);
                if (warm > 0) g.Prewarm(warm, view, input);
            }
        }
        foreach (var g in groups) g.Update(dt, view, input);
    }

    /// <summary>
    /// Writes this frame's instances into the frame's constants and records the draws into the open pass. Called in the near depth slice
    /// only (the camera groups' particles are within a few hundred units of the eye). The viewer's near plane grows with the camera's
    /// height (to 200), the game's is a few units: a particle nearer than the slice's near plane (<paramref name="nearPlane"/>) is drawn with
    /// <paramref name="closeViewProjection"/> (a projection with a near plane of 0.5) and no depth test, which is right because nothing of
    /// the scene is nearer than that plane; the others as usual, tested against the scene's depth.
    /// </summary>
    public void Draw(Matrix4x4 viewProjectionMatrix, Matrix4x4 closeViewProjection, float nearPlane, Matrix4x4 view, Vector3 eye, Vector3 sunDirection)
    {
        drawnParticles = 0;
        if (!Enabled || groups.Count == 0) return;
        draws.Clear();
        var constants = gpu.Frame.Constants;
        var forward = -new Vector3(view.M13, view.M23, view.M33);   // the view's -Z axis in the world
        foreach (var g in groups)
        {
            var sim = g.Simulation;
            var offset = g.Anchor - eye;
            for (int t = 0; t < sim.Techniques.Count; t++)
            {
                int n = sim.TechniqueParticleCount(t);
                var def = sim.Techniques[t];
                if (n == 0 || !def.Enabled || !def.Renderer.IsBillboard) continue;
                var material = Library.FindMaterial(def.Material);
                if (material is null) continue;
                var instances = constants.Allocate((ulong)(n * InstanceBytes), 16);
                int written = sim.Collect(t, new Span<ParticleInstance>(instances.Pointer, n), g.Tint, g.Alpha, offset);
                if (written == 0) continue;
                int close = PartitionByDepth(new Span<ParticleInstance>(instances.Pointer, written), forward, nearPlane);
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
        foreach (var d in draws)
        {
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
                    Blend = hasColour ? BlendFor(m.Blend) : BlendState.Off,
                };
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
        gpu.EndGuest(cmd);
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
        foreach (var t in textures.Values) t?.Dispose();
        white.Dispose();
        quad.Dispose();
        program.Dispose();
    }
}
