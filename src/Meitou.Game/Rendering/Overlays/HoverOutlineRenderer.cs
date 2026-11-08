using System.Numerics;
using Meitou.Game.Rendering.Characters;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Sky;
using Vk = Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Overlays;

/// <summary>One outline mask: what is outlined in one colour, drawn alone (<see cref="CharacterRenderer.DrawHoverMasks"/>, <see cref="WorldObjectRenderer.DrawSelectionMask"/>).</summary>
/// <param name="Colour">Its colour target, alpha 1 where something outlined is.</param>
/// <param name="Depth">Its depth, from the projection <paramref name="Near"/>..<paramref name="Far"/>.</param>
/// <param name="Tint">The outline's and tint's colour (alpha: their strength).</param>
/// <param name="Spheres">The bounding spheres of what is in it.</param>
public readonly record struct HoverMask(GpuTexture Colour, GpuTexture Depth, float Near, float Far, Vector4 Tint, IReadOnlyList<(Vector3 Centre, float Radius)> Spheres);

/// <summary>
/// The hover outline (Meitou only, docs/game/ui-input.md "Hover outline"): the character under the mouse (and, with Alt held, those
/// near) and the selected building, drawn alone into masks (<see cref="CharacterRenderer.DrawHoverMasks"/>, <see cref="WorldObjectRenderer.DrawSelectionMask"/>), are outlined
/// in their colours and their visible parts tinted in them, over the finished picture. The outline shows through walls; the tint only where the scene's depth does not cover them.
/// </summary>
public sealed class HoverOutlineRenderer
{
    const string Vertex = """
        #version 330 core
        void main() { gl_Position = vec4(vec2((gl_VertexID << 1) & 2, gl_VertexID & 2) * 2.0 - 1.0, 0.0, 1.0); }
        """;

    // Inside the mask: a tint where the character is in front of the scene (both depths made linear; the scene's is jittered and at the render size,
    // hence the tolerance). Outside it: the outline, strongest next to the silhouette, fading over uRadius pixels.
    const string Fragment = """
        #version 330 core
        out vec4 fragColour;
        uniform sampler2D uMask;
        uniform sampler2D uMaskDepth;
        uniform sampler2D uSceneDepth;
        uniform vec2 uDepthScale;
        uniform vec2 uMaskPlanes;
        uniform vec2 uScenePlanes;
        uniform vec4 uColour;
        uniform int uRadius;
        """ + DepthConvention.Glsl + """
        float coverage(ivec2 p)
        {
            ivec2 size = textureSize(uMask, 0);
            return texelFetch(uMask, clamp(p, ivec2(0), size - 1), 0).a;
        }
        void main()
        {
            ivec2 p = ivec2(gl_FragCoord.xy);
            if (coverage(p) > 0.5)
            {
                float character = depthViewZ(texelFetch(uMaskDepth, p, 0).r, uMaskPlanes);
                float scene = depthViewZ(texelFetch(uSceneDepth, ivec2(gl_FragCoord.xy * uDepthScale), 0).r, uScenePlanes);
                if (character > scene * 1.01 + 1.0) discard;
                float a = uColour.a * 0.11;
                fragColour = vec4(uColour.rgb * a, a);
                return;
            }
            float nearest = 1e9;
            for (int y = -uRadius; y <= uRadius; y++)
                for (int x = -uRadius; x <= uRadius; x++)
                {
                    float d2 = float(x * x + y * y);
                    if (d2 < nearest && coverage(p + ivec2(x, y)) > 0.5) nearest = d2;
                }
            float d = sqrt(nearest);
            if (d > float(uRadius) + 0.5) discard;
            float a = uColour.a * 0.6 * clamp(1.0 - (d - 1.0) / (float(uRadius) + 0.5), 0.0, 1.0);
            fragColour = vec4(uColour.rgb * a, a);
        }
        """;

    readonly IGpuContext gpu;
    readonly LegacyProgram program;
    readonly NativeSegment segment;
    readonly UniformHandle depthScale, maskPlanes, scenePlanes, colour, radius;
    readonly SamplerSlot mask, maskDepth, sceneDepth;

    public HoverOutlineRenderer(IGpuContext gpu)
    {
        this.gpu = gpu;
        program = LegacyProgram.Create(gpu, Vertex, Fragment, "hover outline");
        segment = new NativeSegment(gpu, program, PrimitiveType.Triangles, "hover outline");
        depthScale = program.Uniform("uDepthScale");
        maskPlanes = program.Uniform("uMaskPlanes");
        scenePlanes = program.Uniform("uScenePlanes");
        colour = program.Uniform("uColour");
        radius = program.Uniform("uRadius");
        mask = program.Sampler("uMask");
        maskDepth = program.Sampler("uMaskDepth");
        sceneDepth = program.Sampler("uSceneDepth");
    }

    /// <summary>
    /// Blends the outline of each of <paramref name="masks"/> over <paramref name="picture"/> (<paramref name="width"/> × <paramref name="height"/>) in its tint, only
    /// inside its characters' projected bounding spheres (<paramref name="viewProjection"/>, unjittered) widened by the outline, the overlapping ones joined so no pixel
    /// is blended twice. <paramref name="sceneDepthTexture"/> is the scene's depth (planes <paramref name="sceneNear"/>..<paramref name="sceneFar"/>),
    /// <paramref name="renderWidth"/> × <paramref name="renderHeight"/> of it used.
    /// </summary>
    internal void Record(GpuTexture picture, int width, int height, IReadOnlyList<HoverMask> masks,
        GpuTexture sceneDepthTexture, int renderWidth, int renderHeight, float sceneNear, float sceneFar, Matrix4x4 viewProjection)
    {
        int outline = Math.Clamp((int)MathF.Round(2.5f * height / 1080f), 1, 6);
        var nearest = gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));
        var viewport = new PixelViewport(0, 0, width, height, 0, 1);
        foreach (var hover in masks)
        {
            var rects = new List<PixelRect>();
            foreach (var (centre, sphereRadius) in hover.Spheres)
                if (Bounds(centre, sphereRadius, viewProjection, width, height, outline + 1) is { } r) rects.Add(r);
            Join(rects, height);
            foreach (var scissor in rects)
            {
                var cmd = gpu.BeginNative("hover outline");
                var over = PassTargets.Of(picture, null).At(viewport, scissor);
                cmd.BeginRendering(over.Rendering);
                program.Bind(mask, new SampledTexture(nearest, hover.Colour.View()));
                program.Bind(maskDepth, new SampledTexture(nearest, hover.Depth.View()));
                program.Bind(sceneDepth, new SampledTexture(nearest, sceneDepthTexture.View()));
                program.Set(depthScale, renderWidth / (float)width, renderHeight / (float)height);
                program.Set(maskPlanes, hover.Near, hover.Far);
                program.Set(scenePlanes, sceneNear, sceneFar);
                program.Set(colour, hover.Tint.X, hover.Tint.Y, hover.Tint.Z, hover.Tint.W);
                program.Set(radius, outline);
                var state = DrawState.For(over.Formats, gpu.DepthClamp, blend: new BlendState(true, BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha));
                state.Record(cmd, over);
                cmd.BindPipeline(segment.Get(state, over.Formats, null));
                program.Flush(cmd);
                cmd.Draw(3);
                cmd.EndRendering();
                gpu.EndNative(cmd);
            }
        }
    }

    /// <summary>Replaces overlapping rectangles by their bounding rectangle until none overlap.</summary>
    static void Join(List<PixelRect> rects, int height)
    {
        for (bool joined = true; joined;)
        {
            joined = false;
            for (int i = 0; i < rects.Count && !joined; i++)
                for (int j = i + 1; j < rects.Count && !joined; j++)
                {
                    var (a, b) = (rects[i], rects[j]);
                    int left = Math.Min(a.X, b.X), right = Math.Max(a.X + (int)a.Width, b.X + (int)b.Width);
                    int bottom = Math.Min(a.Y, b.Y), top = Math.Max(a.Y + (int)a.Height, b.Y + (int)b.Height);
                    if (a.X >= b.X + (int)b.Width || b.X >= a.X + (int)a.Width ||
                        a.Y >= b.Y + (int)b.Height || b.Y >= a.Y + (int)a.Height) continue;
                    rects[i] = new PixelRect(left, bottom, (uint)(right - left), (uint)(top - bottom));
                    rects.RemoveAt(j);
                    joined = true;
                }
        }
    }

    /// <summary>
    /// The scissor round the sphere's projection, <paramref name="pad"/> pixels wider, in the picture's attachment rows (the UI's top-left rectangle turned
    /// as the overlay turns it); the whole picture when the sphere reaches behind the eye, null when it is off screen.
    /// </summary>
    internal static PixelRect? Bounds(Vector3 centre, float radius, Matrix4x4 viewProjection, int width, int height, int pad)
    {
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        for (int corner = 0; corner < 8; corner++)
        {
            var p = centre + new Vector3((corner & 1) == 0 ? -radius : radius, (corner & 2) == 0 ? -radius : radius, (corner & 4) == 0 ? -radius : radius);
            var clip = Vector4.Transform(new Vector4(p, 1), viewProjection);
            if (clip.W <= 0.01f) return new PixelRect(0, 0, (uint)width, (uint)height);
            float x = (clip.X / clip.W * 0.5f + 0.5f) * width, y = (0.5f - clip.Y / clip.W * 0.5f) * height;   // top-left pixels
            (x0, y0, x1, y1) = (MathF.Min(x0, x), MathF.Min(y0, y), MathF.Max(x1, x), MathF.Max(y1, y));
        }
        int left = Math.Max(0, (int)MathF.Floor(x0) - pad), right = Math.Min(width, (int)MathF.Ceiling(x1) + pad);
        int top = Math.Max(0, (int)MathF.Floor(y0) - pad), bottom = Math.Min(height, (int)MathF.Ceiling(y1) + pad);
        if (right <= left || bottom <= top) return null;
        return new PixelRect(left, height - bottom, (uint)(right - left), (uint)(bottom - top));
    }
}
