using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Post;
using System.Numerics;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Characters;

/// <summary>
/// The switches of the enhanced previews' studio (Meitou).
/// </summary>
public static class PreviewStudio
{
    /// <summary>
    /// The <c>previewsun</c> switch (Meitou): the editor's stage and the portraits lit by the studio's fixed sun, with its shadow and occlusion.
    /// </summary>
    public static bool Sun { get; set; } = true;

    /// <summary>
    /// The <c>editorbar</c> switch (Meitou, optional): the character editor shows the bar, drawn by the world's pipeline, instead of the platform.
    /// </summary>
    public static bool Bar { get; set; }
}

/// <summary>
/// The studio of the enhanced previews (docs/character-renderer.md "Meitou stage and portraits"): with <see cref="PreviewStudio.Sun"/>, the character
/// is drawn three times. First its depth from a fixed sun into a shadow map, then its depth from the camera, from which a horizon-based occlusion is
/// made. Last its colour in the shader's studio model (a fog distance of -4), which reads both through the view block's interior-cut fields: the
/// characters never use those. A depth draw is the colour shader with a fog distance of -5, which returns as soon as its cut-outs have discarded, so
/// hair cards cast their own shape.
/// </summary>
internal sealed unsafe partial class CharacterRenderer
{
    /// <summary>
    /// The studio shadow map's side in texels.
    /// </summary>
    const int StudioShadowSize = 2048;

    /// <summary>
    /// The sky's visibility: the character's depth from this many directions over the whole sphere (a Fibonacci spiral, CharacterShaders' studioSky
    /// makes the same).
    /// </summary>
    const int StudioSkyDirections = 32;

    /// <summary>
    /// The side in pixels of one direction's tile of the sky atlas.
    /// </summary>
    const int StudioSkyTile = 384;

    /// <summary>
    /// The sky atlas's tiles across.
    /// </summary>
    const int StudioSkyColumns = 8;

    /// <summary>
    /// The fog distance of the studio's colour draw (CharacterShaders).
    /// </summary>
    const float StudioColour = -4;

    /// <summary>
    /// The fog distance of the studio's depth draws (CharacterShaders).
    /// </summary>
    const float StudioDepth = -5;

    /// <summary>
    /// The colour target the sun's depth draw carries (its colour is unused).
    /// </summary>
    GpuTexture? _studioShadowColour;

    /// <summary>
    /// The sun's shadow map.
    /// </summary>
    GpuTexture? _studioShadowDepth;

    /// <summary>
    /// The screen occlusion, the preview's size.
    /// </summary>
    GpuTexture? _studioAo;

    /// <summary>
    /// The colour target the sky's depth draws carry (its colour is unused).
    /// </summary>
    GpuTexture? _studioSkyColour;

    /// <summary>
    /// The sky atlas: the character's depth from each spiral direction, a tile each.
    /// </summary>
    GpuTexture? _studioSkyDepth;

    /// <summary>
    /// The bindless index of <see cref="_studioShadowDepth"/>.
    /// </summary>
    uint _studioShadowIndex;

    /// <summary>
    /// The bindless index of <see cref="_studioAo"/>.
    /// </summary>
    uint _studioAoIndex;

    /// <summary>
    /// The bindless index of <see cref="_studioSkyDepth"/>.
    /// </summary>
    uint _studioSkyIndex;

    /// <summary>
    /// The occlusion pass, made on first use.
    /// </summary>
    StudioAoPass? _studioAoPass;

    /// <summary>
    /// The casters the current stage draw adds to the studio maps (<see cref="StageCasters"/> while the stage is drawn, null otherwise).
    /// </summary>
    Action<Matrix4x4, Vector3>? _studioCasters;

    /// <summary>
    /// The previews are drawn in the studio.
    /// </summary>
    bool StudioOn => _look.Enhanced && _look.Studio;

    /// <summary>
    /// What else casts into the stage's studio maps (the editor's platform, <c>WorldObjectRenderer.RecordStagePropDepth</c>): called inside each
    /// map's pass with its world view-projection and eye. The host sets it before <see cref="DrawStage"/>; the portraits have none.
    /// </summary>
    internal Action<Matrix4x4, Vector3>? StageCasters { get; set; }

    /// <summary>
    /// The studio's shadow and occlusion for <paramref name="index"/>'s character (posed as the caller has it) seen through
    /// <paramref name="viewProjection"/> into <paramref name="depth"/> (the preview's depth target, <paramref name="width"/> ×
    /// <paramref name="height"/>), outside any pass; returns the view constants of its colour draw.
    /// </summary>
    ViewConstants StudioView(int index, Matrix4x4 viewProjection, Matrix4x4 projection, Vector3 eye, Vector3 toSun, GpuTexture colour,
        GpuTexture depth, int width, int height)
    {
        ref var l = ref _live[index];
        var centre = l.Centre;
        float radius = MathF.Max(l.Radius, 1f) * 1.3f;
        toSun = Vector3.Normalize(toSun);

        // The sun's map: an orthographic box round the character's sphere, its matrix relative to the centre (the receiver subtracts it: no large
        // numbers).
        var up = MathF.Abs(toSun.Y) > 0.95f ? Vector3.UnitZ : Vector3.UnitY;
        var lightRelative = Matrix4x4.CreateLookAt(toSun * (radius * 2), Vector3.Zero, up)
            * Matrix4x4.CreateOrthographicOffCenter(-radius, radius, -radius, radius, 0, radius * 4);
        MakeStudioShadow();

        // The camera's eye picks the levels of detail, so the map holds the meshes the picture shows (a coarser level from the sun's distance shaded
        // its own finer one).
        DrawStudioDepth(index, Matrix4x4.CreateTranslation(-centre) * lightRelative, centre + toSun * (radius * 2), eye, _studioShadowColour!,
            _studioShadowDepth!, StudioShadowSize, StudioShadowSize, "studio shadow");

        // The sky's visibility: the depth from every direction of the spiral, at the camera's levels of detail.
        DrawStudioSky(index, centre, radius, eye);

        // The camera's depth, then the occlusion from it.
        DrawStudioDepth(index, viewProjection, eye, eye, colour, depth, width, height, "studio depth");
        RunStudioAo(depth, width, height, projection, radius);

        // The colour draw's view: the maps through the interior-cut fields.
        var v = new ViewConstants { ViewProjection = viewProjection, Eye = eye, LightDir = toSun, FogDistance = StudioColour };
        v.CutPad = _studioShadowIndex;
        v.CutVolumes = _studioAoIndex;
        v.CutRanges[0] = new Vector4(centre, 2 * radius / StudioShadowSize);
        v.CutRanges[1] = new Vector4(_studioSkyIndex, StudioSkyDirections, radius, StudioSkyTile);
        v.CutPlanes[0] = new Vector4(lightRelative.M11, lightRelative.M12, lightRelative.M13, lightRelative.M14);
        v.CutPlanes[1] = new Vector4(lightRelative.M21, lightRelative.M22, lightRelative.M23, lightRelative.M24);
        v.CutPlanes[2] = new Vector4(lightRelative.M31, lightRelative.M32, lightRelative.M33, lightRelative.M34);
        v.CutPlanes[3] = new Vector4(lightRelative.M41, lightRelative.M42, lightRelative.M43, lightRelative.M44);
        return v;
    }

    /// <summary>
    /// The sun's shadow map and its carrier colour target, made on first use.
    /// </summary>
    void MakeStudioShadow()
    {
        if (_studioShadowDepth is not null) return;

        var colourDesc = new TextureDesc(InternalFormat.Rgba8, StudioShadowSize, StudioShadowSize, Use: TextureUse.ColourTarget,
            Name: "studio shadow colour");
        _studioShadowColour = Gpu.CreateTexture(colourDesc);
        _studioShadowDepth = Gpu.CreateTexture(new TextureDesc(InternalFormat.DepthComponent32F, StudioShadowSize, StudioShadowSize,
            Use: TextureUse.DepthTarget | TextureUse.Sampled, Name: "studio shadow"));
        _studioShadowIndex = Gpu.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(StudioNearest(), _studioShadowDepth.View()));
    }

    /// <summary>
    /// The sky atlas and its carrier colour target, made on first use: <see cref="StudioSkyColumns"/> tiles across, as many rows as the directions
    /// fill.
    /// </summary>
    void MakeStudioSky()
    {
        if (_studioSkyDepth is not null) return;

        int w = StudioSkyTile * StudioSkyColumns;
        int h = StudioSkyTile * ((StudioSkyDirections + StudioSkyColumns - 1) / StudioSkyColumns);
        _studioSkyColour = Gpu.CreateTexture(new TextureDesc(InternalFormat.Rgba8, w, h, Use: TextureUse.ColourTarget, Name: "studio sky colour"));
        var depthDesc = new TextureDesc(InternalFormat.DepthComponent32F, w, h, Use: TextureUse.DepthTarget | TextureUse.Sampled, Name: "studio sky");
        _studioSkyDepth = Gpu.CreateTexture(depthDesc);
        _studioSkyIndex = Gpu.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(StudioNearest(), _studioSkyDepth.View()));
    }

    /// <summary>
    /// The spiral's direction <paramref name="i"/> of <see cref="StudioSkyDirections"/> (towards the sky), as the shader's studioSky makes it.
    /// </summary>
    static Vector3 StudioSkyDirection(int i)
    {
        float y = 1 - (i + 0.5f) * 2 / StudioSkyDirections;
        float r = MathF.Sqrt(MathF.Max(1 - y * y, 0));
        float phi = i * 2.39996323f;
        return new Vector3(MathF.Cos(phi) * r, y, MathF.Sin(phi) * r);
    }

    /// <summary>
    /// The character's depth from each spiral direction into its tile of the sky atlas: orthographic boxes round its sphere, their matrices as the
    /// shader's studioSky rebuilds them (look-at from the direction twice the radius out, up +Y or +Z near the poles, depth 0 to 4 radii).
    /// </summary>
    void DrawStudioSky(int index, Vector3 centre, float radius, Vector3 lodEye)
    {
        MakeStudioSky();

        var cmd = Gpu.BeginNative("studio sky");
        var target = PassTargets.Of(_studioSkyColour!, _studioSkyDepth!);
        cmd.BeginRendering(new RenderingDesc(target.Colour, target.Depth with { Load = LoadOp.Clear, DepthClear = 1f }, target.Width, target.Height));
        Gpu.BeginHostPass(cmd, target, DrawState.Scene(target.Formats));
        cmd.Clear(true, default, true, 1, new PixelRect(0, 0, (uint)target.Width, (uint)target.Height));

        int was = _portraitLive;
        _portraitLive = index;
        try
        {
            for (int i = 0; i < StudioSkyDirections; i++) DrawStudioSkyTile(i, centre, radius, lodEye);
        }
        finally
        {
            _portraitLive = was;
        }

        Gpu.EndHostPass(cmd);
        cmd.EndRendering();
        Gpu.EndNative(cmd);
    }

    /// <summary>
    /// The character's depth from spiral direction <paramref name="i"/> into its tile, inside the sky pass.
    /// </summary>
    void DrawStudioSkyTile(int i, Vector3 centre, float radius, Vector3 lodEye)
    {
        var d = StudioSkyDirection(i);
        var up = MathF.Abs(d.Y) > 0.95f ? Vector3.UnitZ : Vector3.UnitY;
        var matrix = Matrix4x4.CreateTranslation(-centre) * Matrix4x4.CreateLookAt(d * (radius * 2), Vector3.Zero, up)
            * Matrix4x4.CreateOrthographicOffCenter(-radius, radius, -radius, radius, 0, radius * 4);

        int x = i % StudioSkyColumns * StudioSkyTile;
        int y = i / StudioSkyColumns * StudioSkyTile;
        var rect = new PixelRect(x, y, StudioSkyTile, StudioSkyTile);
        Gpu.SetPassViewport(new PixelViewport(x, y, StudioSkyTile, StudioSkyTile, 0, 1), rect);

        DrawView(new ViewConstants { ViewProjection = matrix, Eye = centre + d * (radius * 2), FogDistance = StudioDepth }, lodEye, []);
        _studioCasters?.Invoke(matrix, centre + d * (radius * 2));
    }

    /// <summary>
    /// The nearest-texel, clamped sampler the studio reads its maps with.
    /// </summary>
    GpuSampler StudioNearest() => Gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest,
        TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));

    /// <summary>
    /// <paramref name="index"/>'s character's depth (cut-outs included) into <paramref name="depth"/>, cleared, its colour target only a carrier.
    /// </summary>
    void DrawStudioDepth(int index, Matrix4x4 viewProjection, Vector3 eye, Vector3 lodEye, GpuTexture colour, GpuTexture depth, int width,
        int height, string label)
    {
        var cmd = Gpu.BeginNative(label);
        var target = PassTargets.Of(colour, depth);
        cmd.BeginRendering(new RenderingDesc(target.Colour, target.Depth with { Load = LoadOp.Clear, DepthClear = 1f }, target.Width, target.Height));
        Gpu.BeginHostPass(cmd, target, DrawState.Scene(target.Formats));
        var rect = new PixelRect(0, 0, (uint)width, (uint)height);
        cmd.Clear(true, default, true, 1, rect);
        Gpu.SetPassViewport(new PixelViewport(0, 0, width, height, 0, 1), rect);

        int was = _portraitLive;
        _portraitLive = index;
        try
        {
            DrawView(new ViewConstants { ViewProjection = viewProjection, Eye = eye, FogDistance = StudioDepth }, lodEye, []);
        }
        finally
        {
            _portraitLive = was;
        }

        _studioCasters?.Invoke(viewProjection, eye);
        Gpu.EndHostPass(cmd);
        cmd.EndRendering();
        Gpu.EndNative(cmd);
    }

    /// <summary>
    /// The screen occlusion from <paramref name="depth"/> into <see cref="_studioAo"/> (remade when the preview's size changes).
    /// </summary>
    void RunStudioAo(GpuTexture depth, int width, int height, Matrix4x4 projection, float radius)
    {
        if (_studioAo is null || _studioAo.Desc.Width != width || _studioAo.Desc.Height != height)
        {
            if (_studioAo is not null)
            {
                Gpu.Bindless.Free(BindlessKind.Texture2D, _studioAoIndex);
                _studioAo.Dispose();
            }

            var desc = new TextureDesc(InternalFormat.R16F, width, height, Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "studio occlusion");
            _studioAo = Gpu.CreateTexture(desc);
            _studioAoIndex = Gpu.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(StudioNearest(), _studioAo.View()));
        }

        _studioAoPass ??= new StudioAoPass(Gpu);

        // The occlusion's reach: a tenth of the character's sphere (a head's span for a portrait's head, an arm's for the whole body).
        _studioAoPass.Run(new SampledTexture(StudioNearest(), depth.View()), _studioAo, width, height, projection, radius * 0.12f);
    }

    /// <summary>
    /// Frees the studio's maps, their bindless slots, the occlusion pass and the imprints.
    /// </summary>
    void DisposeStudio()
    {
        DisposeImprints();
        if (_studioShadowDepth is not null) Gpu.Bindless.Free(BindlessKind.Texture2D, _studioShadowIndex);
        if (_studioAo is not null) Gpu.Bindless.Free(BindlessKind.Texture2D, _studioAoIndex);
        if (_studioSkyDepth is not null) Gpu.Bindless.Free(BindlessKind.Texture2D, _studioSkyIndex);

        _studioSkyColour?.Dispose();
        _studioSkyDepth?.Dispose();
        (_studioSkyColour, _studioSkyDepth) = (null, null);
        _studioShadowColour?.Dispose();
        _studioShadowDepth?.Dispose();
        _studioAo?.Dispose();
        _studioAoPass?.Dispose();
        (_studioShadowColour, _studioShadowDepth, _studioAo, _studioAoPass) = (null, null, null, null);
    }
}

/// <summary>
/// The studio's GLSL (<see cref="CharacterRenderer"/>'s studio), for the character shader and the objects' (the editor's platform): the sun's shadow,
/// the sky's visibility and the screen's occlusion, through the view block's interior-cut fields. The objects read <c>cutVolumes</c> as their shadow cut,
/// so their studio views leave it 0 (no screen occlusion there).
/// </summary>
internal static class StudioShaders
{
    /// <summary>
    /// The functions <c>studioShadow</c>, <c>studioSky</c>, <c>studioAmbient</c> and <c>studioOcclusion</c>, spliced into the shaders that read them.
    /// </summary>
    public const string Functions = """
        // The studio (CharacterRenderer.Studio, Meitou previews): its sun's shadow map and occlusion through the view block's interior-cut fields, which the
        // characters never use: cutPad the map's bindless index, cutPlanes[0..3] its matrix relative to cutRanges[0].xyz (w: a texel in world units),
        // cutVolumes the occlusion's index (the preview's own pixels).
        float studioShadow(vec3 world, vec3 n, vec3 l)
        {
            if (view.cutPad == 0u) return 1.0;
            mat4 m = mat4(view.cutPlanes[0], view.cutPlanes[1], view.cutPlanes[2], view.cutPlanes[3]);
            float texel = view.cutRanges[0].w;
            float slope = 1.0 - clamp(dot(n, l), 0.0, 1.0);
            vec4 c = m * vec4(world - view.cutRanges[0].xyz + n * (texel * (1.0 + 2.0 * slope)) + l * (texel * 1.5), 1.0);
            vec3 q = c.xyz / c.w;
            vec2 uv = q.xy * 0.5 + 0.5;   // the viewport is not flipped (GlConventions.VkViewport): clip y -1 is the map's first row
            vec2 size = vec2(textureSize(textures2D[nonuniformEXT(view.cutPad)], 0));
            vec2 base = uv * size - 0.5;
            vec2 f = fract(base);
            ivec2 b = ivec2(floor(base));
            // 4 x 4 texels, each compared, weighted bilinearly at the edges: a soft edge of two texels.
            float lit = 0.0, total = 0.0;
            for (int y = -1; y <= 2; y++)
                for (int x = -1; x <= 2; x++)
                {
                    ivec2 t = clamp(b + ivec2(x, y), ivec2(0), ivec2(size) - 1);
                    float w = (x == -1 ? 1.0 - f.x : x == 2 ? f.x : 1.0) * (y == -1 ? 1.0 - f.y : y == 2 ? f.y : 1.0);
                    float stored = texelFetch(textures2D[nonuniformEXT(view.cutPad)], t, 0).r;
                    lit += (q.z <= stored ? 1.0 : 0.0) * w;
                    total += w;
                }
            return lit / total;
        }
        // The sky's visibility (CharacterRenderer.Studio): the share of the sphere's spiral directions, weighted by the normal, from which nothing of the
        // character hides the point, each read in its tile of the sky atlas (cutRanges[1]: its bindless index, the directions, the radius, a tile's pixels).
        float studioSky(vec3 world, vec3 n)
        {
            if (view.cutRanges[1].y < 0.5) return 1.0;
            uint atlas = uint(view.cutRanges[1].x + 0.5);
            int count = int(view.cutRanges[1].y + 0.5);
            float radius = view.cutRanges[1].z, tile = view.cutRanges[1].w;
            float texel = 2.0 * radius / tile;
            vec3 p = world - view.cutRanges[0].xyz + n * (2.0 * texel);
            float open = 0.0, total = 0.0;
            for (int i = 0; i < count; i++)
            {
                float y = 1.0 - (float(i) + 0.5) * 2.0 / float(count), r = sqrt(max(1.0 - y * y, 0.0)), phi = float(i) * 2.39996323;
                vec3 d = vec3(cos(phi) * r, y, sin(phi) * r);
                float w = max(dot(n, d), 0.0);
                if (w <= 0.0) continue;
                vec3 up = abs(d.y) > 0.95 ? vec3(0.0, 0.0, 1.0) : vec3(0.0, 1.0, 0.0);
                vec3 xa = normalize(cross(up, d)), ya = cross(d, xa);
                vec3 q = p + d * (1.5 * texel);
                vec2 uv = vec2(dot(xa, q), dot(ya, q)) / radius * 0.5 + 0.5;
                float z = (2.0 * radius - dot(d, q)) / (4.0 * radius);
                vec2 base = (vec2(float(i % 8), float(i / 8)) + clamp(uv, 0.0, 1.0)) * tile - 0.5;
                ivec2 b = ivec2(floor(base));
                vec2 f = fract(base);
                float lit = 0.0;
                for (int k = 0; k < 4; k++)
                {
                    ivec2 o = ivec2(k & 1, k >> 1);
                    float stored = texelFetch(textures2D[nonuniformEXT(atlas)], b + o, 0).r;
                    lit += (z <= stored ? 1.0 : 0.0) * ((o.x == 0 ? 1.0 - f.x : f.x) * (o.y == 0 ? 1.0 - f.y : f.y));
                }
                open += w * lit;
                total += w;
            }
            return total > 0.0 ? open / total : 1.0;
        }
        // The studio's indirect light: a cool sky over warm sand by the normal's height. A share of it stays however closed the point is (light bounced in
        // from everything the two maps do not hold), so a side the sun does not reach is dark, not black.
        vec3 studioAmbient(vec3 n, float open)
        {
            vec3 sky = mix(vec3(0.30, 0.23, 0.16), vec3(0.30, 0.36, 0.46), smoothstep(-0.6, 0.8, n.y));
            return sky * 2.2 * mix(0.3, 1.0, open);
        }
        float studioOcclusion()
        {
            if (view.cutVolumes == 0u) return 1.0;
            return texelFetch(textures2D[nonuniformEXT(view.cutVolumes)], ivec2(gl_FragCoord.xy), 0).r;
        }

        """;
}

/// <summary>
/// The studio's occlusion (<see cref="CharacterRenderer"/>'s studio): horizon-based, from the camera's depth alone, eight fixed directions of six steps
/// out to the reach; deterministic (the previews are supersampled, so a fixed pattern averages out). 1 open, 0 closed; the background stays 1.
/// </summary>
internal sealed class StudioAoPass : IDisposable
{
    /// <summary>
    /// The occlusion's fragment shader (GLSL).
    /// </summary>
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

    /// <summary>
    /// The GPU the pass draws on.
    /// </summary>
    readonly IGpuContext _gpu;

    /// <summary>
    /// The occlusion program (the post-process vertex shader and <see cref="Fragment"/>).
    /// </summary>
    readonly LegacyProgram _program;

    /// <summary>
    /// The camera depth's sampler slot.
    /// </summary>
    readonly SamplerSlot _depth;

    /// <summary>
    /// The projection uniform: tan(half fov) × aspect, tan(half fov), near, far.
    /// </summary>
    readonly UniformHandle _projection;

    /// <summary>
    /// The target's size uniform in pixels.
    /// </summary>
    readonly UniformHandle _size;

    /// <summary>
    /// The occlusion's reach uniform in world units.
    /// </summary>
    readonly UniformHandle _reach;

    /// <summary>
    /// The pipeline, made on the first run.
    /// </summary>
    GpuPipeline? _pipeline;

    /// <summary>
    /// The pass on <paramref name="gpu"/>, its program compiled.
    /// </summary>
    public StudioAoPass(IGpuContext gpu)
    {
        this._gpu = gpu;
        _program = LegacyProgram.Create(gpu, PostProcessShaders.Vertex, Fragment, "studio occlusion");
        _depth = _program.Sampler("uDepth");
        (_projection, _size, _reach) = (_program.Uniform("uProjection"), _program.Uniform("uSize"), _program.Uniform("uReach"));
    }

    /// <summary>
    /// The occlusion of <paramref name="depthTexture"/> (seen through <paramref name="perspective"/>) into <paramref name="into"/>, out to
    /// <paramref name="reachWorld"/> world units.
    /// </summary>
    public void Run(SampledTexture depthTexture, GpuTexture into, int width, int height, Matrix4x4 perspective, float reachWorld)
    {
        var targets = PassTargets.Of(into, null);
        var state = DrawState.For(targets.Formats, false);
        _pipeline ??= _gpu.Pipelines.Get(state.Pipeline(_program.Program, _program.VertexLayout([]), PrimitiveType.Triangles, targets.Formats,
            _program.Name));

        // System.Numerics' perspective: M11 = 1 / (tan × aspect), M22 = 1 / tan, depth 0 at the near plane and 1 at the far one.
        float near = perspective.M43 / perspective.M33;
        float far = perspective.M43 / (perspective.M33 + 1);

        var cmd = _gpu.BeginNative("studio occlusion");
        cmd.Order();
        cmd.BeginRendering(new RenderingDesc(targets.Colour, default, targets.Width, targets.Height));
        cmd.SetRaster(state.Cull, state.Front);
        cmd.SetDepth(state.DepthTest, state.DepthWrite, state.Compare);
        cmd.SetDepthBias(state.BiasEnable, state.BiasConstant, state.BiasSlope);
        cmd.BindPipeline(_pipeline.Value);
        _program.Bind(_depth, depthTexture);
        _program.Set(_projection, new Vector4(1 / perspective.M11, 1 / perspective.M22, near, far));
        _program.Set(_size, new Vector2(width, height));
        _program.Set(_reach, reachWorld);
        cmd.SetViewport(new PixelViewport(0, 0, width, height, 0, 1));
        cmd.SetScissor(new PixelRect(0, 0, (uint)width, (uint)height));
        _program.Flush(cmd);
        cmd.Draw(3);
        cmd.EndRendering();
        cmd.Order();
        _gpu.EndNative(cmd);
    }

    /// <summary>
    /// Frees the program.
    /// </summary>
    public void Dispose() => _program.Dispose();
}
