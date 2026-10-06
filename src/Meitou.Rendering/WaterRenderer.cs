using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Textures;
using Meitou.Data.World;

using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// Water as the game places it (docs/formats/terrain.md, "Water"): one surface at <see cref="WorldWater.Height"/>
/// over the whole world, hidden wherever the terrain is higher. Shading follows the facts of Kenshi's water material:
/// colour from <c>watercolourmap.png</c>, flow from <c>flowmap.png</c>, three phase-shifted scrolling samples of the
/// normal map, the biomes' water parameters, alpha from the water depth near the camera and opaque beyond 4000
/// units. With a <see cref="ReflectionPass"/> the water reflects the mirrored scene, else the sky colour. Not reproduced: scum, turbulence,
/// rain ripples and per-biome normal maps (all use <c>water.png</c>).
/// </summary>
public sealed unsafe class WaterRenderer : IDisposable
{
    const string Vertex = """
        #version 330 core
        layout(location = 0) in vec2 aXZ;
        uniform mat4 uViewProjection;
        uniform float uWaterHeight;
        uniform vec2 uCentre;          // the plane follows the eye, so the sea has no edge
        uniform float uExtent;         // half its side, past the far plane
        out vec3 vWorld;
        void main()
        {
            vWorld = vec3(uCentre.x + aXZ.x * uExtent, uWaterHeight, uCentre.y + aXZ.y * uExtent);
            gl_Position = uViewProjection * vec4(vWorld, 1.0);
        }
        """;

    static readonly string Fragment = "#version 330 core\n" + TerrainShaders.HeightFunctions + SkyRenderer.SkyFunctions + """

        in vec3 vWorld;
        out vec4 fragColour;
        uniform vec3 uEye;
        uniform float uWaterHeight;
        uniform float uHalfWorld;
        uniform float uTime;
        uniform vec3 uSunDir;
        uniform vec3 uSunColour;
        uniform vec3 uFogColour;
        uniform float uFogDistance;
        uniform sampler2D uColourMap;   // watercolourmap.png: the biomes' water colour, whole world
        uniform sampler2D uFlowMap;     // flowmap.png: RG flow direction, B scum amount
        uniform sampler2D uNormalMap;   // water.png
        uniform sampler2D uParamsA;     // per pixel, blended over biomes: scale X, scale Y (repeats per unit), invStrength, invOpacity
        uniform sampler2D uParamsB;     // gloss, glow, distortion, -
        uniform vec4 uSeaA;             // the open sea past the world's edge: parameters as in the two maps above, and colour
        uniform vec4 uSeaB;
        uniform vec3 uSeaColour;
        uniform sampler2D uReflection;  // the scene mirrored about the water (ReflectionPass), looked up with uReflectionViewProjection
        uniform mat4 uReflectionViewProjection;
        uniform float uReflect;         // 1 with a reflection this frame, else the sky colour is reflected

        vec3 sampleNormal(vec2 coord, vec2 direction, float speed, float time)
        {
            float t = fract(time);
            vec3 n = texture(uNormalMap, coord + direction * speed * t).rgb * 2.0 - 1.0;
            return n.xzy * (1.0 - abs(t * 2.0 - 1.0));
        }

        void main()
        {
            vec2 map = (vWorld.xz + uHalfWorld) / (2.0 * uHalfWorld);
            vec4 pa = texture(uParamsA, map);
            vec4 pb = texture(uParamsB, map);
            vec3 waterColour = texture(uColourMap, map).rgb;
            // Past the world's edge the data's last pixels would stretch outwards: fade to the open sea instead.
            vec2 beyond = abs(vWorld.xz) - uHalfWorld;
            float outside = max(max(beyond.x, beyond.y), 0.0);
            // The data is shallow-coast colours right up to its edge: fade into the open sea across the edge, from inside it, so there is no straight seam.
            float sea = smoothstep(-30000.0, 12000.0, max(beyond.x, beyond.y));
            pa = mix(pa, uSeaA, sea);
            pb = mix(pb, uSeaB, sea);
            waterColour = mix(waterColour, uSeaColour, sea);
            vec3 toEye = uEye - vWorld;
            float dist = length(toEye);
            vec3 view = toEye / dist;

            // Flow: direction from the flow map, speed from the texture scale and the biome's distortion.
            vec2 tex = vWorld.xz * pa.xy;
            vec2 direction = (texture(uFlowMap, map).rg * 2.0 - 1.0) * (pa.xy * 5000.0);
            float speed = length(direction);
            direction /= max(speed, 1e-5);
            float distortion = max(pb.z, 1.0);
            speed /= distortion;
            float time = uTime * distortion;
            vec3 n = vec3(0.0, 1.0, 0.0);
            n += sampleNormal(tex, direction, speed, time);
            n += sampleNormal(tex + vec2(0.1, 0.3), direction, speed, time + 0.33);
            n += sampleNormal(tex + vec2(0.4, 0.7), direction, speed, time + 0.66);
            n.y *= pa.z;
            n = normalize(n);

            // Lighting: sun specular, sky reflection, a little diffuse for the water colour.
            float gloss = clamp(pb.x, 0.0, 1.0);
            vec3 l = normalize(uSunDir);
            vec3 h = normalize(l + view);
            // The ripples are finer than a pixel far away, so the glint widens and dims with distance instead of staying a hot point.
            float power = max(exp2(gloss * 11.0 + 1.0) / (1.0 + dist / 6000.0), 12.0);
            float spec = pow(max(dot(n, h), 0.0), power) * (power + 8.0) / 25.0 * gloss;
            // Above the game's camera heights (uAtmoAltitude.z, a viewer choice) every water pixel is far away and its widened glint covers
            // a large patch of sea: weigh it by the Fresnel term the game's own sun specular has (F0 0.04), so it stays a soft sheen.
            float lh = clamp(dot(l, h), 0.0, 1.0);
            spec *= mix(1.0, 0.04 + 0.96 * exp2((-5.55473 * lh - 6.98316) * lh), uAtmoAltitude.z);
            float cosv = max(dot(view, n), 0.0);
            float schlick = 0.02 + 0.98 * pow(1.0 - cosv, 5.0);
            vec3 reflected = skyColour(reflect(-view, n), false);
            if (uReflect > 0.5)
            {
                // As the game's water: the normal pushes the lookup sideways by 60 / depth, less the further away.
                vec4 rc = uReflectionViewProjection * vec4(vWorld, 1.0);
                vec2 offset = n.xz * (60.0 / rc.w);
                offset *= min(1.0, 0.04 / max(length(offset), 1e-5));
                vec2 uv = rc.xy / rc.w * 0.5 + 0.5 + offset;
                // Past the picture's border there is nothing to reflect: fade to the sky colour.
                vec2 inside = smoothstep(vec2(0.0), vec2(0.04), uv) * smoothstep(vec2(0.0), vec2(0.04), 1.0 - uv);
                reflected = mix(reflected, min(texture(uReflection, uv).rgb, vec3(3.0)), inside.x * inside.y);
            }
            vec3 diffuse = waterColour * (max(dot(n, l), 0.0) * uSunColour * 0.6 + uSkyZenith * 0.5 + 0.03);
            vec3 colour = mix(diffuse, reflected, schlick * gloss) + min(spec, 4.0) * uSunColour * 0.25 + pb.y * waterColour;

            // Alpha as the game's water: see-through near the camera where shallow, opaque beyond 4000 units.
            float depth = max(0.0, uWaterHeight - terrainHeight(vWorld.xz)) / max(view.y, 0.05);
            if (outside > 0.0) depth = 1e4;   // past the edge of the world there is only open sea
            float fresnel = 1.0 - pow(1.0 - cosv, 2.0);
            float a = clamp(1.0 - (dist - 4000.0) / 1000.0, 0.0, 1.0);
            a *= mix(1.0, clamp(depth * pa.w, 0.0, 1.0), fresnel);
            a *= clamp(depth / 2.0, 0.0, 1.0);
            a = mix(1.0, a, clamp((4000.0 - dist) / 400.0, 0.0, 1.0));

            fragColour = vec4(atmoApply(colour, uEye, vWorld), a);   // aerial perspective (AtmosphereShaders)
        }
        """;

    /// <summary>The native GPU API (phase 8 stage 2: the water makes no GL call; docs/renderer-native.md 8.6).</summary>
    public GpuContext Gpu { get; }
    readonly DeviceBuffer quad;
    readonly VertexArrayBindings quadSource;   // the quad's vertex input, as the GL vertex array was exported (the pipeline cache's key)
    readonly BufferBinding[] quadVertices;
    readonly SampledImage[] maps;   // colour, flow, normal, parameters A and B, in the order of mapSamplers
    readonly Vector4 seaA, seaB;
    readonly Vector3 seaColour;
    readonly LegacyProgram program;
    readonly NativeSegment segment;
    readonly Handles h;
    readonly (SamplerSlot Slot, Meitou.Rendering.Vulkan.Shaders.SamplerInfo Info)[] mapSamplers;
    readonly SamplerSlot reflectionSlot;
    readonly Meitou.Rendering.Vulkan.Shaders.SamplerInfo reflectionInfo;

    /// <summary>The program's loose uniforms, resolved once.</summary>
    readonly record struct Handles(UniformHandle ViewProjection, UniformHandle WaterHeight, UniformHandle Centre, UniformHandle Extent, UniformHandle Eye,
        UniformHandle HalfWorld, UniformHandle SeaA, UniformHandle SeaB, UniformHandle SeaColour, UniformHandle Time, UniformHandle SunDir,
        UniformHandle SunColour, UniformHandle FogColour, UniformHandle FogDistance, UniformHandle Reflect, UniformHandle ReflectionViewProjection,
        SkyColourHandles Sky);

    WaterRenderer(GpuContext gpu, SampledImage[] maps, Vector4 seaA, Vector4 seaB, Vector3 seaColour)
    {
        (this.seaA, this.seaB, this.seaColour) = (seaA, seaB, seaColour);
        Gpu = gpu;
        this.maps = maps;
        program = LegacyProgram.Create(gpu, Vertex, Fragment, "water");
        segment = new NativeSegment(gpu, program, Silk.NET.Vulkan.PrimitiveTopology.TriangleStrip, "water");
        h = new Handles(program.Uniform("uViewProjection"), program.Uniform("uWaterHeight"), program.Uniform("uCentre"), program.Uniform("uExtent"),
            program.Uniform("uEye"), program.Uniform("uHalfWorld"), program.Uniform("uSeaA"), program.Uniform("uSeaB"), program.Uniform("uSeaColour"),
            program.Uniform("uTime"), program.Uniform("uSunDir"), program.Uniform("uSunColour"), program.Uniform("uFogColour"), program.Uniform("uFogDistance"),
            program.Uniform("uReflect"), program.Uniform("uReflectionViewProjection"), SkyColourHandles.Resolve(program));
        mapSamplers = new[] { "uColourMap", "uFlowMap", "uNormalMap", "uParamsA", "uParamsB" }
            .Select(n => { var slot = program.Sampler(n); return (slot, program.SamplerInfo(slot)); }).ToArray();
        reflectionSlot = program.Sampler("uReflection");
        reflectionInfo = program.SamplerInfo(reflectionSlot);
        float[] corners = [-1, -1, -1, 1, 1, -1, 1, 1]; // triangle strip, counter-clockwise from above
        quad = DeviceBuffer.Create(gpu, sizeof(float) * (ulong)corners.Length, BufferUse.Vertex, "water quad");
        using (var batch = gpu.Uploads.Begin()) batch.Write(quad, 0, System.Runtime.InteropServices.MemoryMarshal.AsBytes(corners.AsSpan()));
        LegacyProgram.Attribute?[] attributes = [QuadAttribute(quad)];
        quadSource = new VertexArrayBindings(attributes, default);
        quadVertices = program.VertexBuffers(attributes, 0, 1);
    }

    /// <summary>The quad's only vertex input, location 0: two floats, 8 bytes apart (the GL vertex array's
    /// <c>VertexAttribPointer(0, 2, FLOAT, false, 8, 0)</c>); no element buffer.</summary>
    internal static LegacyProgram.Attribute QuadAttribute(DeviceBuffer quad) =>
        new(new BufferBinding(quad.Handle, 0), GlConventions.VertexFormat(GLEnum.Float, 2, false, false), 8, false);

    /// <param name="gl">Unused since phase 8 stage 2 (kept for the callers that still pass it: <c>WorldFrame</c>).</param>
    /// <param name="sky">Unused since phase 8 stage 2: the atmosphere comes through the frame globals.</param>
    public static WaterRenderer Create(IGl? gl, GpuContext gpu, GameInstall install, GameDatabase db, AssetLocator assets, SkyRenderer? sky, List<string> messages)
    {
        _ = (gl, sky);
        var colour = Load(install, WorldWater.ColourMap);
        var flow = Load(install, WorldWater.FlowMap);
        var normalPath = assets.Find("water.png");
        var normal = normalPath is not null ? TextureLoader.LoadFile(normalPath, allMips: false).Levels[0] : null;
        if (normal is null) messages.Add("water.png not found: flat water");

        // Biome water parameters blended per blend-map pixel.
        var info = BlendInfoFile.Open(install);
        var water = BiomeWater.ByIndex(db);
        var blend = TextureLoader.LoadImage(File.ReadAllBytes(Path.Combine(install.DataDirectory, TerrainMaps.BlendMap)));
        var fallback = BiomeWater.FromRecord(db.OfType(Meitou.Data.Fcs.FcsRecordType.BIOMES).First());
        Vector4 A(BiomeWater w) => new(w.Scale.X, w.Scale.Y, w.InvStrength, w.InvOpacity);
        Vector4 B(BiomeWater w) => new(w.Gloss, w.Glow, w.Distortion.X, 0);
        var a = BiomeField.Bake(info, blend.Width, blend.Height, blend.Pixels, c => water.TryGetValue(c, out var w) ? A(w) : null, A(fallback));
        var b = BiomeField.Bake(info, blend.Width, blend.Height, blend.Pixels, c => water.TryGetValue(c, out var w) ? B(w) : null, B(fallback));

        var sea = OpenSea(a, b, blend.Width, blend.Height, colour);
        Console.WriteLine($"sea       open-sea water: scale {sea.A.X * 5000:0.#}, {sea.A.Y * 5000:0.#}, gloss {sea.B.X:0.##}, colour {sea.Colour.X:0.##} {sea.Colour.Y:0.##} {sea.Colour.Z:0.##}");
        // As the GL textures were made (WorldGl.Texture2D): a found map has mips only where it repeats; the 1 × 1 stand-in for a missing one
        // always had its (single-level) chain and a trilinear filter.
        SampledImage Rgba(RgbaImage? img, bool repeat, byte[] flat, string name) =>
            img is null ? SampledImage.Rgba8(gpu, 1, 1, flat, repeat, mipmaps: true, name) : SampledImage.Rgba8(gpu, img, repeat, mipmaps: repeat, name);
        return new WaterRenderer(gpu,
            [Rgba(colour, false, [0, 32, 64, 255], "water colour map"), Rgba(flow, false, [128, 128, 0, 255], "water flow map"),
             Rgba(normal, true, [128, 255, 128, 255], "water normal map"),
             SampledImage.Rgba32F(gpu, a, blend.Width, blend.Height, "water parameters a"), SampledImage.Rgba32F(gpu, b, blend.Width, blend.Height, "water parameters b")],
            sea.A, sea.B, sea.Colour);
    }

    /// <summary>
    /// The water of the open sea: the most common parameters among the pixels of the maps' outer ring (the world's edge is
    /// sea nearly all round), with the watercolourmap colour at one such pixel. The water plane uses it beyond the world.
    /// </summary>
    static (Vector4 A, Vector4 B, Vector3 Colour) OpenSea(Vector4[] a, Vector4[] b, int width, int height, RgbaImage? colour)
    {
        const int ring = 8;
        var counts = new Dictionary<(Vector4, Vector4), (int Count, int Pixel)>();
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                if (x >= ring && x < width - ring && y >= ring && y < height - ring) { x = width - ring - 1; continue; }
                int i = y * width + x;
                var key = (a[i], b[i]);
                counts[key] = counts.TryGetValue(key, out var c) ? (c.Count + 1, c.Pixel) : (1, i);
            }
        var best = counts.MaxBy(p => p.Value.Count);
        var rgb = new Vector3(0, 32, 64) / 255;
        if (colour is not null)
        {
            int px = best.Value.Pixel % width * colour.Width / width, py = best.Value.Pixel / width * colour.Height / height;
            int o = (py * colour.Width + px) * 4;
            rgb = new Vector3(colour.Pixels[o], colour.Pixels[o + 1], colour.Pixels[o + 2]) / 255;
        }
        return (best.Key.Item1, best.Key.Item2, rgb);
    }

    static RgbaImage? Load(GameInstall install, string relative)
    {
        var path = Path.Combine(install.DataDirectory, relative);
        return File.Exists(path) ? TextureLoader.LoadImage(File.ReadAllBytes(path)) : null;
    }

    /// <summary>
    /// The water surface: one quad, blended, into the pass VkGl has open. Prepare sets the uniforms and the textures the shader reads (the height
    /// textures, the atmosphere and the heights' uniforms come through the frame globals); Record is one native segment with the pass's state
    /// and the water's own: no culling, depth test without write, alpha blending. No GL state is changed (phase 8 stage 2): the GL code set
    /// these and put back the depth write and blending after the draw; what it left differently (the cull face off, the blend factors) is read by
    /// no later draw (docs/renderer-native.md 8.6).
    /// </summary>
    /// <param name="time">Animation time; the game's unit for it is Unknown (the viewer uses real hours).</param>
    public void Draw(Matrix4x4 viewProjection, Vector3 eye, WorldLighting light, SkyColours colours, float time, float extent, ReflectionPass? reflection = null)
    {
        Prepare(viewProjection, eye, light, colours, time, extent, reflection);
        Record();
    }

    void Prepare(Matrix4x4 viewProjection, Vector3 eye, WorldLighting light, SkyColours colours, float time, float extent, ReflectionPass? reflection)
    {
        var p = program;
        p.Set(h.ViewProjection, in viewProjection);
        p.Set(h.WaterHeight, WorldWater.Height);
        p.Set(h.Centre, eye.X, eye.Z);
        p.Set(h.Extent, extent);
        p.Set(h.Eye, eye.X, eye.Y, eye.Z);
        p.Set(h.HalfWorld, (float)WorldLayout.HalfWorldSize);
        p.Set(h.SeaA, seaA.X, seaA.Y, seaA.Z, seaA.W);
        p.Set(h.SeaB, seaB.X, seaB.Y, seaB.Z, seaB.W);
        p.Set(h.SeaColour, seaColour.X, seaColour.Y, seaColour.Z);
        p.Set(h.Time, time);
        p.Set(h.SunDir, light.SunDirection.X, light.SunDirection.Y, light.SunDirection.Z);
        p.Set(h.SunColour, light.SunColour.X, light.SunColour.Y, light.SunColour.Z);
        p.Set(h.FogColour, light.FogColour.X, light.FogColour.Y, light.FogColour.Z);
        p.Set(h.FogDistance, light.FogDistance);
        h.Sky.Set(p, colours);
        p.ApplyGlobals();   // the atmosphere's and the heights' uniforms and textures, through the frame globals
        for (int i = 0; i < maps.Length; i++) p.Bind(mapSamplers[i].Slot, maps[i].Sampled());
        bool reflect = reflection is { Valid: true };
        p.Set(h.Reflect, reflect ? 1f : 0f);
        if (reflect)
        {
            p.Bind(reflectionSlot, reflection!.Sampled);
            p.Set(h.ReflectionViewProjection, reflection.ViewProjection);
        }
    }

    static readonly BlendState AlphaBlend = new(true, Silk.NET.Vulkan.BlendFactor.SrcAlpha, Silk.NET.Vulkan.BlendFactor.OneMinusSrcAlpha);

    void Record()
    {
        var cmd = Gpu.BeginGuest("water");
        var targets = Gpu.CurrentTargets();
        // What the GL code's Enable(DepthTest), DepthMask(false), Disable(CullFace), Enable(Blend) and BlendFunc made of the pass's state (the
        // depth test and blending only with the attachment they need, as VkGl's CurrentState).
        bool hasDepth = targets.Formats.Depth != Silk.NET.Vulkan.Format.Undefined, hasColour = targets.Formats.Colour != Silk.NET.Vulkan.Format.Undefined;
        var state = Gpu.CurrentState() with
        {
            Cull = Silk.NET.Vulkan.CullModeFlags.None, DepthTest = hasDepth, DepthWrite = false, Blend = hasColour ? AlphaBlend : BlendState.Off,
        };
        var va = quadSource;
        cmd.SetViewport(targets.Viewport);
        cmd.SetScissor(targets.Scissor);
        cmd.SetRaster(state.Cull, state.Front);
        cmd.SetDepth(state.DepthTest, state.DepthWrite, state.Compare);
        cmd.SetDepthBias(state.BiasEnable, state.BiasConstant, state.BiasSlope);
        cmd.BindPipeline(segment.Get(state, targets.Formats, va));
        cmd.BindVertexBuffers(0, quadVertices);
        program.Flush(cmd);
        cmd.Draw(4);
        Gpu.EndGuest(cmd);
    }

    public void Dispose()
    {
        quad.Dispose();
        foreach (var t in maps) t.Dispose();
        program.Dispose();
    }
}
