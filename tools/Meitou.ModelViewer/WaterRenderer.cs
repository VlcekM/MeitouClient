using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Textures;
using Meitou.Data.World;
using Silk.NET.OpenGL;

namespace Meitou.ModelViewer;

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

    const int ReflectionUnit = 16;

    readonly GL gl;
    readonly SkyRenderer sky;
    readonly uint program, vao, vbo, colourMap, flowMap, normalMap, paramsA, paramsB;
    readonly Vector4 seaA, seaB;
    readonly Vector3 seaColour;
    readonly Dictionary<string, int> uniforms = [];

    WaterRenderer(GL gl, SkyRenderer sky, uint colourMap, uint flowMap, uint normalMap, uint paramsA, uint paramsB, Vector4 seaA, Vector4 seaB, Vector3 seaColour)
    {
        (this.seaA, this.seaB, this.seaColour) = (seaA, seaB, seaColour);
        this.gl = gl;
        this.sky = sky;
        (this.colourMap, this.flowMap, this.normalMap, this.paramsA, this.paramsB) = (colourMap, flowMap, normalMap, paramsA, paramsB);
        program = WorldGl.Program(gl, Vertex, Fragment);
        float[] quad = [-1, -1, -1, 1, 1, -1, 1, 1]; // triangle strip, counter-clockwise from above
        vao = gl.GenVertexArray();
        gl.BindVertexArray(vao);
        vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, quad.AsSpan(), BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 8, (void*)0);
        gl.BindVertexArray(0);
    }

    public static WaterRenderer Create(GL gl, GameInstall install, GameDatabase db, AssetLocator assets, SkyRenderer sky, List<string> messages)
    {
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
        uint Rgba(RgbaImage? img, bool repeat, byte[] flat) =>
            img is null ? WorldGl.Texture2D(gl, 1, 1, flat, repeat) : WorldGl.Texture2D(gl, img.Width, img.Height, img.Pixels, repeat, mipmaps: repeat);
        return new WaterRenderer(gl, sky,
            Rgba(colour, false, [0, 32, 64, 255]), Rgba(flow, false, [128, 128, 0, 255]), Rgba(normal, true, [128, 255, 128, 255]),
            FloatTexture(gl, a, blend.Width, blend.Height), FloatTexture(gl, b, blend.Width, blend.Height), sea.A, sea.B, sea.Colour);
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

    static uint FloatTexture(GL gl, Vector4[] data, int width, int height)
    {
        uint id = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, id);
        gl.TexImage2D<Vector4>(TextureTarget.Texture2D, 0, InternalFormat.Rgba32f, (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.Float, data.AsSpan());
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        return id;
    }

    /// <param name="time">Animation time; the game's unit for it is Unknown (the viewer uses real hours).</param>
    public void Draw(Matrix4x4 viewProjection, Vector3 eye, WorldLighting light, SkyColours colours, TerrainRenderer terrain, float time, float extent, ReflectionPass? reflection = null)
    {
        gl.UseProgram(program);
        WorldGl.Matrix(gl, U("uViewProjection"), viewProjection);
        gl.Uniform1(U("uWaterHeight"), WorldWater.Height);
        gl.Uniform2(U("uCentre"), eye.X, eye.Z);
        gl.Uniform1(U("uExtent"), extent);
        gl.Uniform3(U("uEye"), eye.X, eye.Y, eye.Z);
        gl.Uniform1(U("uHalfWorld"), (float)WorldLayout.HalfWorldSize);
        gl.Uniform4(U("uSeaA"), seaA.X, seaA.Y, seaA.Z, seaA.W);
        gl.Uniform4(U("uSeaB"), seaB.X, seaB.Y, seaB.Z, seaB.W);
        gl.Uniform3(U("uSeaColour"), seaColour.X, seaColour.Y, seaColour.Z);
        gl.Uniform1(U("uTime"), time);
        gl.Uniform3(U("uSunDir"), light.SunDirection.X, light.SunDirection.Y, light.SunDirection.Z);
        gl.Uniform3(U("uSunColour"), light.SunColour.X, light.SunColour.Y, light.SunColour.Z);
        gl.Uniform3(U("uFogColour"), light.FogColour.X, light.FogColour.Y, light.FogColour.Z);
        gl.Uniform1(U("uFogDistance"), light.FogDistance);
        sky.SetUniforms(program, colours);
        terrain.BindHeights(program);
        uint[] textures = [colourMap, flowMap, normalMap, paramsA, paramsB];
        string[] names = ["uColourMap", "uFlowMap", "uNormalMap", "uParamsA", "uParamsB"];
        for (int i = 0; i < textures.Length; i++)
        {
            gl.ActiveTexture(TextureUnit.Texture0 + 11 + i);
            gl.BindTexture(TextureTarget.Texture2D, textures[i]);
            gl.Uniform1(U(names[i]), 11 + i);
        }
        bool reflect = reflection is { Valid: true };
        gl.Uniform1(U("uReflect"), reflect ? 1f : 0f);
        gl.Uniform1(U("uReflection"), ReflectionUnit);
        if (reflect)
        {
            gl.ActiveTexture(TextureUnit.Texture0 + ReflectionUnit);
            gl.BindTexture(TextureTarget.Texture2D, reflection!.Texture);
            WorldGl.Matrix(gl, U("uReflectionViewProjection"), reflection.ViewProjection);
        }
        gl.ActiveTexture(TextureUnit.Texture0);

        gl.Enable(EnableCap.DepthTest);
        gl.Disable(EnableCap.CullFace);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        gl.DepthMask(false);
        gl.BindVertexArray(vao);
        gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        gl.BindVertexArray(0);
        gl.DepthMask(true);
        gl.Disable(EnableCap.Blend);
    }

    int U(string name)
    {
        if (!uniforms.TryGetValue(name, out int location)) uniforms[name] = location = gl.GetUniformLocation(program, name);
        return location;
    }

    public void Dispose()
    {
        gl.DeleteVertexArray(vao);
        gl.DeleteBuffer(vbo);
        foreach (var t in new[] { colourMap, flowMap, normalMap, paramsA, paramsB }) gl.DeleteTexture(t);
        gl.DeleteProgram(program);
    }
}
