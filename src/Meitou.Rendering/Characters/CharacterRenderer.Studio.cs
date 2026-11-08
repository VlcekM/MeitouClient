using System.Numerics;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering.Characters;

/// <summary>The <c>previewsun</c> switch (Meitou): the editor's stage and the portraits lit by the studio's fixed sun, with its shadow and occlusion.</summary>
public static class PreviewStudio
{
    public static bool Sun { get; set; } = true;
}

/// <summary>
/// The studio of the enhanced previews (docs/character-renderer.md "Meitou stage and portraits"): with <see cref="PreviewStudio.Sun"/>, the character is drawn
/// three times. First its depth from a fixed sun into a shadow map, then its depth from the camera, from which a horizon-based occlusion is made. Last its
/// colour in the shader's studio model (a fog distance of -4), which reads both through the view block's interior-cut fields: the characters never use those.
/// A depth draw is the colour shader with a fog distance of -5, which returns as soon as its cut-outs have discarded, so hair cards cast their own shape.
/// </summary>
internal sealed unsafe partial class CharacterRenderer
{
    const int StudioShadowSize = 2048;
    /// <summary>The sky's visibility: the character's depth from this many directions over the whole sphere (a Fibonacci spiral, CharacterShaders'
    /// studioSky makes the same), each in a tile of this many pixels, this many tiles across.</summary>
    const int StudioSkyDirections = 32, StudioSkyTile = 384, StudioSkyColumns = 8;
    /// <summary>The fog distance of the studio's colour draw and of its depth draws (CharacterShaders).</summary>
    const float StudioColour = -4, StudioDepth = -5;

    GpuTexture? studioShadowColour, studioShadowDepth, studioAo, studioSkyColour, studioSkyDepth;
    uint studioShadowIndex, studioAoIndex, studioSkyIndex;
    StudioAoPass? studioAoPass;

    bool StudioOn => look.Enhanced && look.Studio;

    /// <summary>
    /// The studio's shadow and occlusion for <paramref name="index"/>'s character (posed as the caller has it) seen through <paramref name="viewProjection"/> into
    /// <paramref name="depth"/> (the preview's depth target, <paramref name="width"/> × <paramref name="height"/>), outside any pass; returns the view constants
    /// of its colour draw.
    /// </summary>
    ViewConstants StudioView(int index, Matrix4x4 viewProjection, Matrix4x4 projection, Vector3 eye, Vector3 toSun, GpuTexture colour, GpuTexture depth, int width, int height)
    {
        ref var l = ref live[index];
        var centre = l.Centre;
        float radius = MathF.Max(l.Radius, 1f) * 1.3f;
        toSun = Vector3.Normalize(toSun);

        // The sun's map: an orthographic box round the character's sphere, its matrix relative to the centre (the receiver subtracts it: no large numbers).
        var up = MathF.Abs(toSun.Y) > 0.95f ? Vector3.UnitZ : Vector3.UnitY;
        var lightRelative = Matrix4x4.CreateLookAt(toSun * (radius * 2), Vector3.Zero, up) * Matrix4x4.CreateOrthographicOffCenter(-radius, radius, -radius, radius, 0, radius * 4);
        MakeStudioShadow();
        // The camera's eye picks the levels of detail, so the map holds the meshes the picture shows (a coarser level from the sun's distance shaded its own finer one).
        DrawStudioDepth(index, Matrix4x4.CreateTranslation(-centre) * lightRelative, centre + toSun * (radius * 2), eye, studioShadowColour!, studioShadowDepth!, StudioShadowSize, StudioShadowSize, "studio shadow");

        // The sky's visibility: the depth from every direction of the spiral, at the camera's levels of detail.
        DrawStudioSky(index, centre, radius, eye);

        // The camera's depth, then the occlusion from it.
        DrawStudioDepth(index, viewProjection, eye, eye, colour, depth, width, height, "studio depth");
        RunStudioAo(depth, width, height, projection, radius);

        var v = new ViewConstants { ViewProjection = viewProjection, Eye = eye, LightDir = toSun, FogDistance = StudioColour };
        v.CutPad = studioShadowIndex;
        v.CutVolumes = studioAoIndex;
        v.CutRanges[0] = new Vector4(centre, 2 * radius / StudioShadowSize);
        v.CutRanges[1] = new Vector4(studioSkyIndex, StudioSkyDirections, radius, StudioSkyTile);
        v.CutPlanes[0] = new Vector4(lightRelative.M11, lightRelative.M12, lightRelative.M13, lightRelative.M14);
        v.CutPlanes[1] = new Vector4(lightRelative.M21, lightRelative.M22, lightRelative.M23, lightRelative.M24);
        v.CutPlanes[2] = new Vector4(lightRelative.M31, lightRelative.M32, lightRelative.M33, lightRelative.M34);
        v.CutPlanes[3] = new Vector4(lightRelative.M41, lightRelative.M42, lightRelative.M43, lightRelative.M44);
        return v;
    }

    void MakeStudioShadow()
    {
        if (studioShadowDepth is not null) return;
        studioShadowColour = Gpu.CreateTexture(new TextureDesc(InternalFormat.Rgba8, StudioShadowSize, StudioShadowSize, Use: TextureUse.ColourTarget, Name: "studio shadow colour"));
        studioShadowDepth = Gpu.CreateTexture(new TextureDesc(InternalFormat.DepthComponent32f, StudioShadowSize, StudioShadowSize,
            Use: TextureUse.DepthTarget | TextureUse.Sampled, Name: "studio shadow"));
        studioShadowIndex = Gpu.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(StudioNearest(), studioShadowDepth.View()));
    }

    /// <summary>The spiral's direction <paramref name="i"/> of <see cref="StudioSkyDirections"/> (towards the sky), as the shader's studioSky makes it.</summary>
    static Vector3 StudioSkyDirection(int i)
    {
        float y = 1 - (i + 0.5f) * 2 / StudioSkyDirections, r = MathF.Sqrt(MathF.Max(1 - y * y, 0)), phi = i * 2.39996323f;
        return new Vector3(MathF.Cos(phi) * r, y, MathF.Sin(phi) * r);
    }

    /// <summary>The character's depth from each spiral direction into its tile of the sky atlas: orthographic boxes round its sphere, their matrices as
    /// the shader's studioSky rebuilds them (look-at from the direction twice the radius out, up +Y or +Z near the poles, depth 0 to 4 radii).</summary>
    void DrawStudioSky(int index, Vector3 centre, float radius, Vector3 lodEye)
    {
        if (studioSkyDepth is null)
        {
            int w = StudioSkyTile * StudioSkyColumns, h = StudioSkyTile * ((StudioSkyDirections + StudioSkyColumns - 1) / StudioSkyColumns);
            studioSkyColour = Gpu.CreateTexture(new TextureDesc(InternalFormat.Rgba8, w, h, Use: TextureUse.ColourTarget, Name: "studio sky colour"));
            studioSkyDepth = Gpu.CreateTexture(new TextureDesc(InternalFormat.DepthComponent32f, w, h, Use: TextureUse.DepthTarget | TextureUse.Sampled, Name: "studio sky"));
            studioSkyIndex = Gpu.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(StudioNearest(), studioSkyDepth.View()));
        }
        var cmd = Gpu.BeginNative("studio sky");
        var target = PassTargets.Of(studioSkyColour, studioSkyDepth);
        cmd.BeginRendering(new RenderingDesc(target.Colour, target.Depth with { Load = LoadOp.Clear, DepthClear = 1f }, target.Width, target.Height));
        Gpu.BeginHostPass(cmd, target, DrawState.Scene(target.Formats));
        cmd.Clear(true, default, true, 1, new PixelRect(0, 0, (uint)target.Width, (uint)target.Height));
        int was = portraitLive;
        portraitLive = index;
        try
        {
            for (int i = 0; i < StudioSkyDirections; i++)
            {
                var d = StudioSkyDirection(i);
                var up = MathF.Abs(d.Y) > 0.95f ? Vector3.UnitZ : Vector3.UnitY;
                var matrix = Matrix4x4.CreateTranslation(-centre) * Matrix4x4.CreateLookAt(d * (radius * 2), Vector3.Zero, up)
                    * Matrix4x4.CreateOrthographicOffCenter(-radius, radius, -radius, radius, 0, radius * 4);
                int x = i % StudioSkyColumns * StudioSkyTile, y = i / StudioSkyColumns * StudioSkyTile;
                var rect = new PixelRect(x, y, StudioSkyTile, StudioSkyTile);
                Gpu.SetPassViewport(new PixelViewport(x, y, StudioSkyTile, StudioSkyTile, 0, 1), rect);
                DrawView(new ViewConstants { ViewProjection = matrix, Eye = centre + d * (radius * 2), FogDistance = StudioDepth }, lodEye, []);
            }
        }
        finally { portraitLive = was; }
        Gpu.EndHostPass(cmd);
        cmd.EndRendering();
        Gpu.EndNative(cmd);
    }

    GpuSampler StudioNearest() => Gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest,
        TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));

    /// <summary><paramref name="index"/>'s character's depth (cut-outs included) into <paramref name="depth"/>, cleared, its colour target only a carrier.</summary>
    void DrawStudioDepth(int index, Matrix4x4 viewProjection, Vector3 eye, Vector3 lodEye, GpuTexture colour, GpuTexture depth, int width, int height, string label)
    {
        var cmd = Gpu.BeginNative(label);
        var target = PassTargets.Of(colour, depth);
        cmd.BeginRendering(new RenderingDesc(target.Colour, target.Depth with { Load = LoadOp.Clear, DepthClear = 1f }, target.Width, target.Height));
        Gpu.BeginHostPass(cmd, target, DrawState.Scene(target.Formats));
        var rect = new PixelRect(0, 0, (uint)width, (uint)height);
        cmd.Clear(true, default, true, 1, rect);
        Gpu.SetPassViewport(new PixelViewport(0, 0, width, height, 0, 1), rect);
        int was = portraitLive;
        portraitLive = index;
        try { DrawView(new ViewConstants { ViewProjection = viewProjection, Eye = eye, FogDistance = StudioDepth }, lodEye, []); }
        finally { portraitLive = was; }
        Gpu.EndHostPass(cmd);
        cmd.EndRendering();
        Gpu.EndNative(cmd);
    }

    void RunStudioAo(GpuTexture depth, int width, int height, Matrix4x4 projection, float radius)
    {
        if (studioAo is null || studioAo.Desc.Width != width || studioAo.Desc.Height != height)
        {
            if (studioAo is not null) { Gpu.Bindless.Free(BindlessKind.Texture2D, studioAoIndex); studioAo.Dispose(); }
            studioAo = Gpu.CreateTexture(new TextureDesc(InternalFormat.R16f, width, height, Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "studio occlusion"));
            studioAoIndex = Gpu.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(StudioNearest(), studioAo.View()));
        }
        studioAoPass ??= new StudioAoPass(Gpu);
        // The occlusion's reach: a tenth of the character's sphere (a head's span for a portrait's head, an arm's for the whole body).
        studioAoPass.Run(new SampledTexture(StudioNearest(), depth.View()), studioAo, width, height, projection, radius * 0.12f);
    }

    void DisposeStudio()
    {
        if (studioShadowDepth is not null) Gpu.Bindless.Free(BindlessKind.Texture2D, studioShadowIndex);
        if (studioAo is not null) Gpu.Bindless.Free(BindlessKind.Texture2D, studioAoIndex);
        if (studioSkyDepth is not null) Gpu.Bindless.Free(BindlessKind.Texture2D, studioSkyIndex);
        studioSkyColour?.Dispose();
        studioSkyDepth?.Dispose();
        (studioSkyColour, studioSkyDepth) = (null, null);
        studioShadowColour?.Dispose();
        studioShadowDepth?.Dispose();
        studioAo?.Dispose();
        studioAoPass?.Dispose();
        (studioShadowColour, studioShadowDepth, studioAo, studioAoPass) = (null, null, null, null);
    }
}

/// <summary>
/// The studio's occlusion (<see cref="CharacterRenderer"/>'s studio): horizon-based, from the camera's depth alone, eight fixed directions of six steps out to
/// the reach; deterministic (the previews are supersampled, so a fixed pattern averages out). 1 open, 0 closed; the background stays 1.
/// </summary>
internal sealed class StudioAoPass : IDisposable
{
    const string Fragment = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uDepth;
        uniform vec4 uProjection;   // x tan(half fov) × aspect, y tan(half fov), z near, w far
        uniform vec2 uSize;
        uniform float uReach;       // world units

        float linearDepth(float d) { return uProjection.z * uProjection.w / (uProjection.w - d * (uProjection.w - uProjection.z)); }
        vec3 viewAt(ivec2 p)
        {
            float z = linearDepth(texelFetch(uDepth, p, 0).r);
            vec2 ndc = (vec2(p) + 0.5) / uSize * 2.0 - 1.0;
            return vec3(ndc.x * uProjection.x * z, ndc.y * uProjection.y * z, -z);
        }

        void main()
        {
            ivec2 p = ivec2(gl_FragCoord.xy);
            float d = texelFetch(uDepth, p, 0).r;
            if (d >= 1.0) { fragColour = vec4(1.0); return; }
            vec3 pos = viewAt(p);
            ivec2 last = ivec2(uSize) - 1;
            vec3 dx = viewAt(min(p + ivec2(1, 0), last)) - pos, dx2 = pos - viewAt(max(p - ivec2(1, 0), ivec2(0)));
            vec3 dy = viewAt(min(p + ivec2(0, 1), last)) - pos, dy2 = pos - viewAt(max(p - ivec2(0, 1), ivec2(0)));
            vec3 n = normalize(cross(abs(dx.z) < abs(dx2.z) ? dx : dx2, abs(dy.z) < abs(dy2.z) ? dy : dy2));
            if (dot(n, pos) > 0.0) n = -n;
            // The reach in pixels at this depth.
            float pixels = uReach / (-pos.z * uProjection.y) * uSize.y * 0.5;
            if (pixels < 1.0) { fragColour = vec4(1.0); return; }
            const int Directions = 8, Steps = 6;
            float rotate = fract(dot(vec2(p & 3), vec2(0.25, 0.0625)) * 3.0);   // a 4 x 4 turn of the directions, averaged by the supersampling
            float open = 0.0;
            for (int k = 0; k < Directions; k++)
            {
                float a = (float(k) + rotate) * 6.2831853 / float(Directions);
                vec2 dir = vec2(cos(a), sin(a));
                float horizon = 0.0;
                for (int s = 1; s <= Steps; s++)
                {
                    vec2 q = vec2(p) + dir * (pixels * float(s) / float(Steps));
                    ivec2 qi = ivec2(q);
                    if (any(lessThan(qi, ivec2(0))) || any(greaterThan(qi, last))) break;
                    if (texelFetch(uDepth, qi, 0).r >= 1.0) continue;
                    vec3 v = viewAt(qi) - pos;
                    float len = length(v);
                    if (len < 1e-4 || len > uReach) continue;
                    // The sine of the sample's elevation over the surface, faded with distance.
                    float e = dot(v / len, n) * (1.0 - len / uReach);
                    horizon = max(horizon, e);
                }
                open += 1.0 - horizon;
            }
            fragColour = vec4(clamp(open / float(Directions), 0.0, 1.0));
        }
        """;

    readonly IGpuContext gpu;
    readonly LegacyProgram program;
    readonly SamplerSlot depth;
    readonly UniformHandle projection, size, reach;
    GpuPipeline? pipeline;

    public StudioAoPass(IGpuContext gpu)
    {
        this.gpu = gpu;
        program = LegacyProgram.Create(gpu, PostProcessShaders.Vertex, Fragment, "studio occlusion");
        depth = program.Sampler("uDepth");
        (projection, size, reach) = (program.Uniform("uProjection"), program.Uniform("uSize"), program.Uniform("uReach"));
    }

    public void Run(SampledTexture depthTexture, GpuTexture into, int width, int height, Matrix4x4 perspective, float reachWorld)
    {
        var targets = PassTargets.Of(into, null);
        var state = DrawState.For(targets.Formats, false);
        pipeline ??= gpu.Pipelines.Get(state.Pipeline(program.Program, program.VertexLayout([]), PrimitiveType.Triangles, targets.Formats, program.Name));
        // System.Numerics' perspective: M11 = 1 / (tan × aspect), M22 = 1 / tan, depth 0 at the near plane and 1 at the far one.
        float near = perspective.M43 / perspective.M33, far = perspective.M43 / (perspective.M33 + 1);
        var cmd = gpu.BeginNative("studio occlusion");
        cmd.Order();
        cmd.BeginRendering(new RenderingDesc(targets.Colour, default, targets.Width, targets.Height));
        cmd.SetRaster(state.Cull, state.Front);
        cmd.SetDepth(state.DepthTest, state.DepthWrite, state.Compare);
        cmd.SetDepthBias(state.BiasEnable, state.BiasConstant, state.BiasSlope);
        cmd.BindPipeline(pipeline.Value);
        program.Bind(depth, depthTexture);
        program.Set(projection, new Vector4(1 / perspective.M11, 1 / perspective.M22, near, far));
        program.Set(size, new Vector2(width, height));
        program.Set(reach, reachWorld);
        cmd.SetViewport(new PixelViewport(0, 0, width, height, 0, 1));
        cmd.SetScissor(new PixelRect(0, 0, (uint)width, (uint)height));
        program.Flush(cmd);
        cmd.Draw(3);
        cmd.EndRendering();
        cmd.Order();
        gpu.EndNative(cmd);
    }

    public void Dispose() => program.Dispose();
}
