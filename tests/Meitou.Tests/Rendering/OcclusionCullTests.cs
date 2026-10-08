using System.Numerics;
using Meitou.Rendering;

namespace Meitou.Tests.Rendering;

/// <summary>
/// The foliage occlusion cull's rule (<c>HizOccluded</c> / <c>HizRegion</c> in <see cref="FoliageShaders.CullCompute"/>, docs/formats/foliage.md "Occlusion
/// culling"), written out here on the CPU against a synthetic scene: whenever it says a sphere is hidden by the previous frame's depth, every ray from an
/// eye that has moved (up to the step the rule was given) to the sphere is stopped by a surface of the scene. The depth is drawn with the sub-pixel jitter
/// of the real one. A conservative rule may say "visible" for a hidden sphere (the tests count how often it says "hidden" so they are not empty).
/// </summary>
public class OcclusionCullTests
{
    internal const int W = 320, H = 240;
    internal static readonly float TanY = MathF.Tan(25f * MathF.PI / 180), TanX = TanY * W / H;

    internal sealed record Occluder(Vector3 Centre, float Radius);

    /// <summary>The camera: position and view axes (view-space x = right, y = up, forward = -back).</summary>
    internal readonly record struct Camera(Vector3 Eye, Vector3 Right, Vector3 Up, Vector3 Back)
    {
        public static Camera Look(Vector3 eye, float yaw, float pitch)
        {
            var forward = new Vector3(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), MathF.Cos(yaw) * MathF.Cos(pitch));
            var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
            var up = Vector3.Cross(right, forward);
            return new Camera(eye, right, up, -forward);
        }
    }

    /// <summary>The nearest hit of the segment from <paramref name="from"/> towards <paramref name="to"/> (up to <paramref name="to"/>) with the scene: a sphere or the ground (y = 0).</summary>
    static float? Hit(IReadOnlyList<Occluder> scene, Vector3 from, Vector3 to)
    {
        var d = to - from;
        float length = d.Length();
        d /= length;
        float best = float.MaxValue;
        foreach (var s in scene)
        {
            var m = from - s.Centre;
            float b = Vector3.Dot(m, d), c = m.LengthSquared() - s.Radius * s.Radius;
            if (c > 0 && b > 0) continue;
            float disc = b * b - c;
            if (disc < 0) continue;
            float t = -b - MathF.Sqrt(disc);
            if (t < 0) t = 0;
            best = MathF.Min(best, t);
        }
        if (d.Y < 0 && from.Y > 0) best = MathF.Min(best, -from.Y / d.Y);
        return best < length ? best : null;
    }

    /// <summary>The view distance (along the view axis) at pixel (x, y), the ray through its centre moved by (jx, jy) pixels; 1e9 for the sky.</summary>
    internal static float DepthAt(IReadOnlyList<Occluder> scene, in Camera cam, int x, int y, float jx, float jy)
    {
        float tx = ((x + 0.5f + jx) / W * 2 - 1) * TanX, ty = ((y + 0.5f + jy) / H * 2 - 1) * TanY;
        var dir = Vector3.Normalize(cam.Right * tx + cam.Up * ty - cam.Back);
        var far = cam.Eye + dir * 1e6f;
        float? t = Hit(scene, cam.Eye, far);
        return t is { } hit ? hit * Vector3.Dot(dir, -cam.Back) : 1e9f;
    }

    /// <summary>The pyramid of <c>HizPyramid</c>: level 0 takes 4 x 4 pixels, each next level 2 x 2 texels (least, most).</summary>
    internal sealed class Pyramid
    {
        public readonly List<(int W, int H)> Sizes = HizPyramid.Levels(W, H);
        public readonly List<(float Least, float Most)[]> Levels = [];

        public Pyramid(float[,] z)
        {
            var b = new (float, float)[Sizes[0].W * Sizes[0].H];
            for (int ty = 0; ty < Sizes[0].H; ty++)
                for (int tx = 0; tx < Sizes[0].W; tx++)
                {
                    float least = float.MaxValue, most = 0;
                    for (int dy = 0; dy < 4; dy++)
                        for (int dx = 0; dx < 4; dx++)
                        {
                            int x = tx * 4 + dx, y = ty * 4 + dy;
                            if (x >= W || y >= H) continue;
                            least = MathF.Min(least, z[y, x]);
                            most = MathF.Max(most, z[y, x]);
                        }
                    b[ty * Sizes[0].W + tx] = (least, most);
                }
            Levels.Add(b);
            for (int l = 1; l < Sizes.Count; l++)
            {
                var (sw, sh) = Sizes[l - 1];
                var (dw, dh) = Sizes[l];
                var next = new (float, float)[dw * dh];
                for (int y = 0; y < dh; y++)
                    for (int x = 0; x < dw; x++)
                    {
                        var r = (Least: float.MaxValue, Most: 0f);
                        for (int dy = 0; dy < 2; dy++)
                            for (int dx = 0; dx < 2; dx++)
                            {
                                var v = Levels[l - 1][Math.Min(y * 2 + dy, sh - 1) * sw + Math.Min(x * 2 + dx, sw - 1)];
                                r = (MathF.Min(r.Least, v.Least), MathF.Max(r.Most, v.Most));
                            }
                        next[y * dw + x] = r;
                    }
                Levels.Add(next);
            }
        }

        /// <summary><c>HizRegion</c>.</summary>
        public bool Region(Vector4 r, out float least, out float most)
        {
            (least, most) = (float.MaxValue, 0);
            if (r.X < 0 || r.Y < 0 || r.Z >= W || r.W >= H) return false;
            int bw = Sizes[0].W, bh = Sizes[0].H;
            int x0 = (int)r.X >> 2, y0 = (int)r.Y >> 2, x1 = Math.Min((int)r.Z >> 2, bw - 1), y1 = Math.Min((int)r.W >> 2, bh - 1);
            int l = 0;
            while (l < Sizes.Count - 1 && ((x1 >> l) - (x0 >> l) > 1 || (y1 >> l) - (y0 >> l) > 1)) l++;
            int w = Sizes[l].W;
            for (int y = y0 >> l; y <= y1 >> l; y++)
                for (int x = x0 >> l; x <= x1 >> l; x++)
                {
                    var t = Levels[l][y * w + x];
                    least = MathF.Min(least, t.Least);
                    most = MathF.Max(most, t.Most);
                }
            return true;
        }

        /// <summary><c>HizOccluded</c>, for the sphere in the world, the camera the depth was drawn from and the eye's step <paramref name="delta"/>.</summary>
        public bool Occluded(in Camera cam, Vector3 centre, float radius, float delta)
        {
            var d = centre - cam.Eye;
            float zc = -Vector3.Dot(cam.Back, d), xc = Vector3.Dot(cam.Right, d), yc = Vector3.Dot(cam.Up, d);
            float zn = zc - radius, zf = zc + radius;
            if (zn < 40f) return false;
            float tx0 = MathF.Min(MathF.Min((xc - radius) / zn, (xc - radius) / zf), MathF.Min((xc + radius) / zn, (xc + radius) / zf));
            float tx1 = MathF.Max(MathF.Max((xc - radius) / zn, (xc - radius) / zf), MathF.Max((xc + radius) / zn, (xc + radius) / zf));
            float ty0 = MathF.Min(MathF.Min((yc - radius) / zn, (yc - radius) / zf), MathF.Min((yc + radius) / zn, (yc + radius) / zf));
            float ty1 = MathF.Max(MathF.Max((yc - radius) / zn, (yc - radius) / zf), MathF.Max((yc + radius) / zn, (yc + radius) / zf));
            var rect = new Vector4((0.5f + 0.5f * tx0 / TanX) * W - 1, (0.5f + 0.5f * ty0 / TanY) * H - 1, (0.5f + 0.5f * tx1 / TanX) * W + 1, (0.5f + 0.5f * ty1 / TanY) * H + 1);
            float f = H * 0.5f / TanY, pad = 0;
            for (int i = 0; i < 3; i++)
            {
                if (!Region(rect + new Vector4(-pad, -pad, pad, pad), out float least, out float most)) return false;
                float need = delta * f / MathF.Max(least, 1f);
                if (need <= pad + 0.5f) return most + 0.01f * zn + 3f < zn;
                pad = need;
            }
            return false;
        }
    }

    internal static Vector3 RandomUnit(Random r)
    {
        while (true)
        {
            var v = new Vector3((float)r.NextDouble() * 2 - 1, (float)r.NextDouble() * 2 - 1, (float)r.NextDouble() * 2 - 1);
            if (v.LengthSquared() is > 0.01f and <= 1f) return Vector3.Normalize(v);
        }
    }

    [Fact]
    public void Levels_halve_to_one_texel_and_cover_every_pixel()
    {
        var levels = HizPyramid.Levels(1280, 720);
        Assert.Equal((320, 180), levels[0]);
        Assert.Equal((160, 90), levels[1]);
        Assert.Equal((1, 1), levels[^1]);
        Assert.Equal(10, levels.Count);
        for (int i = 1; i < levels.Count; i++)
        {
            Assert.Equal((levels[i - 1].W + 1) / 2, levels[i].W);
            Assert.Equal((levels[i - 1].H + 1) / 2, levels[i].H);
        }
        Assert.Equal((1, 1), HizPyramid.Levels(3, 2)[0]);
        Assert.Single(HizPyramid.Levels(4, 4));
    }

    /// <summary>
    /// Random scenes: spheres in front of the camera and the ground, the depth drawn with a jitter, a sphere behind them. Whenever the rule says hidden, rays
    /// from the drawing eye and from eyes up to the step away to points of the sphere (its centre, its box's near corner side and random points) hit the scene first.
    /// </summary>
    [Fact]
    public void A_sphere_the_rule_calls_hidden_is_hidden_from_every_eye_within_the_step()
    {
        var random = new Random(7);
        int decided = 0, hiddenCalls = 0, rays = 0;
        for (int trial = 0; trial < 1500; trial++)
        {
            var scene = new List<Occluder>();
            int blockers = random.Next(1, 9);
            for (int i = 0; i < blockers; i++)
                scene.Add(new Occluder(new Vector3(random.Next(-600, 600), random.Next(20, 400), random.Next(150, 1500)), random.Next(40, 500)));
            var eye = new Vector3(0, random.Next(20, 120), 0);
            var cam = Camera.Look(eye, (float)(random.NextDouble() * 0.2 - 0.1), (float)(random.NextDouble() * 0.2 - 0.1));
            float jx = (float)random.NextDouble() - 0.5f, jy = (float)random.NextDouble() - 0.5f;
            var z = new float[H, W];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    z[y, x] = DepthAt(scene, cam, x, y, jx, jy);
            var pyramid = new Pyramid(z);
            float[] steps = [0, 0.5f, 3, 12, 40];
            for (int s = 0; s < 6; s++)
            {
                var centre = eye + (cam.Right * random.Next(-900, 900) + cam.Up * random.Next(0, 500) - cam.Back * random.Next(500, 5000));
                float radius = random.Next(3, 80);
                float delta = steps[random.Next(steps.Length)];
                decided++;
                if (!pyramid.Occluded(cam, centre, radius, delta)) continue;
                hiddenCalls++;
                for (int e = 0; e < 6; e++)
                {
                    var moved = e == 0 ? eye : eye + RandomUnit(random) * delta * (e < 3 ? 1f : (float)random.NextDouble());
                    if (moved.Y <= 1) moved.Y = 1;
                    for (int p = 0; p < 40; p++)
                    {
                        var target = p == 0 ? centre : centre + RandomUnit(random) * radius * (float)Math.Cbrt(random.NextDouble());
                        rays++;
                        Assert.True(Hit(scene, moved, target) is not null,
                            $"trial {trial}: hidden call, delta {delta}, eye moved to {moved}, ray to {target} (sphere {centre} radius {radius}) is not stopped");
                    }
                }
            }
        }
        Assert.True(hiddenCalls > 100, $"{hiddenCalls} of {decided} spheres called hidden: the scenes do not exercise the rule");
        Assert.True(rays > 10000);
    }

    [Fact]
    public void Nothing_is_hidden_near_the_eye_by_the_sky_or_when_the_step_is_large()
    {
        var scene = new List<Occluder> { new(new Vector3(0, 100, 800), 300) };
        var cam = Camera.Look(new Vector3(0, 60, 0), 0, 0);
        var z = new float[H, W];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                z[y, x] = DepthAt(scene, cam, x, y, 0, 0);
        var pyramid = new Pyramid(z);
        Assert.True(pyramid.Occluded(cam, new Vector3(0, 100, 3000), 20, 0));          // behind the sphere, the eye still
        Assert.False(pyramid.Occluded(cam, new Vector3(0, 100, 3000), 20, 2000));      // a big step: parallax outgrows the rectangle
        Assert.False(pyramid.Occluded(cam, new Vector3(1200, 100, 1500), 20, 0));      // beside it: the sky
        Assert.False(pyramid.Occluded(cam, new Vector3(0, 100, 500), 20, 0));          // in front of it
        Assert.False(pyramid.Occluded(cam, new Vector3(0, 60, 30), 5, 0));             // within 40 units of the eye
        Assert.False(pyramid.Occluded(cam, new Vector3(0, 100, -3000), 20, 0));        // behind the eye
    }
}

/// <summary>The pyramid's kernels (<c>HizShaders</c>) against the CPU's: odd picture sizes, a near slice that is cleared in places and a far slice.</summary>
public class HizPyramidGpuTests
{
    static float ViewZ(float d, Vector2 nf)
    {
        float zd = 2 * d - 1;
        return nf.X * nf.Y / (nf.Y - zd * (nf.Y - nf.X));
    }

    [Fact]
    [Slow]
    public unsafe void The_kernels_build_the_least_and_greatest_distance_of_every_block_at_every_level()
    {
        Silk.NET.Vulkan.Vk.GetApi();
        Meitou.Rendering.Gpu.Core.VulkanDevice? d;
        try { d = Meitou.Rendering.Gpu.Core.VulkanDevice.Create(new Meitou.Rendering.Gpu.Core.VulkanDeviceOptions { Validation = true, SyncValidation = true }); }
        catch (Exception e) when (e is Meitou.Rendering.Gpu.Core.VulkanException or DllNotFoundException or EntryPointNotFoundException or Silk.NET.Core.Loader.SymbolLoadingException) { d = null; }
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        foreach (var (w, h) in new[] { (1000, 700), (1280, 720), (63, 37), (4, 4), (2049, 513) })
        {
            var random = new Random(w * 31 + h);
            var nearPlanes = new Vector2(3.7f, 20400f);
            var farPlanes = new Vector2(20000f, 80000f);
            var near = new float[w * h];
            var far = new float[w * h];
            var z = new float[w * h];
            for (int i = 0; i < near.Length; i++)
            {
                near[i] = random.Next(3) == 0 ? 1f : 0.5f + 0.5f * (float)Math.Pow(random.NextDouble(), 0.05);
                far[i] = random.Next(3) == 0 ? 1f : 0.5f + 0.5f * (float)Math.Pow(random.NextDouble(), 0.01);
                z[i] = near[i] < 1f ? ViewZ(near[i], nearPlanes) : far[i] < 1f ? ViewZ(far[i], farPlanes) : 1e9f;
            }
            var levels = HizPyramid.Levels(w, h);
            int total = levels.Sum(l => l.W * l.H);
            using (var ctx = new Meitou.Rendering.Gpu.GpuContext(d!))
            {
                ctx.EnsureFrame();
                var desc = new Meitou.Rendering.Gpu.TextureDesc(Silk.NET.Vulkan.Format.R32Sfloat, w, h, Use: Meitou.Rendering.Gpu.TextureUse.Sampled | Meitou.Rendering.Gpu.TextureUse.TransferDst, Name: "hiz test depth");
                using var nearTexture = Meitou.Rendering.Gpu.Texture.Create(ctx, desc, ctx.Frame.PreFrame.Handle);
                using var farTexture = Meitou.Rendering.Gpu.Texture.Create(ctx, desc, ctx.Frame.PreFrame.Handle);
                var region = new Silk.NET.Vulkan.Rect2D(new Silk.NET.Vulkan.Offset2D(0, 0), new Silk.NET.Vulkan.Extent2D((uint)w, (uint)h));
                ctx.Uploads.Write(nearTexture, 0, 0, region, System.Runtime.InteropServices.MemoryMarshal.AsBytes(near.AsSpan()));
                ctx.Uploads.Write(farTexture, 0, 0, region, System.Runtime.InteropServices.MemoryMarshal.AsBytes(far.AsSpan()));
                using var hiz = new HizPyramid(ctx);
                hiz.Build(ctx.Frame.PreFrame, nearTexture, farTexture, nearPlanes, farPlanes, w, h, Vector3.Zero, Matrix4x4.Identity, 0.8f, 16f / 9);
                using var readback = Meitou.Rendering.Gpu.ReadbackBuffer.Create(ctx, (ulong)total * 8, "hiz readback");
                ctx.Frame.PreFrame.CopyBuffer(hiz.Storage!.Handle, readback.Handle, new Silk.NET.Vulkan.BufferCopy(0, 0, (ulong)total * 8));
                ctx.EndFrame();
                d!.Frames.WaitAll();
                var got = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(readback.Read(0, (ulong)total * 8)).ToArray();

                // The CPU's: level 0 from 4 x 4 pixels, then 2 x 2 texels (clamped at the edge).
                var level0 = new (float, float)[levels[0].W * levels[0].H];
                for (int ty = 0; ty < levels[0].H; ty++)
                    for (int tx = 0; tx < levels[0].W; tx++)
                    {
                        float least = float.MaxValue, most = 0;
                        for (int y = ty * 4; y < Math.Min(ty * 4 + 4, h); y++)
                            for (int x = tx * 4; x < Math.Min(tx * 4 + 4, w); x++) { least = MathF.Min(least, z[y * w + x]); most = MathF.Max(most, z[y * w + x]); }
                        level0[ty * levels[0].W + tx] = (least, most);
                    }
                var previous = level0;
                int offset = 0;
                for (int l = 0; l < levels.Count; l++)
                {
                    var (lw, lh) = levels[l];
                    var current = l == 0 ? level0 : new (float, float)[lw * lh];
                    if (l > 0)
                        for (int y = 0; y < lh; y++)
                            for (int x = 0; x < lw; x++)
                            {
                                float least = float.MaxValue, most = 0;
                                for (int dy = 0; dy < 2; dy++)
                                    for (int dx = 0; dx < 2; dx++)
                                    {
                                        var s = previous[Math.Min(y * 2 + dy, levels[l - 1].H - 1) * levels[l - 1].W + Math.Min(x * 2 + dx, levels[l - 1].W - 1)];
                                        least = MathF.Min(least, s.Item1);
                                        most = MathF.Max(most, s.Item2);
                                    }
                                current[y * lw + x] = (least, most);
                            }
                    for (int i = 0; i < lw * lh; i++)
                    {
                        float gl = got[(offset + i) * 2], gm = got[(offset + i) * 2 + 1];
                        Assert.True(MathF.Abs(gl - current[i].Item1) <= 2e-3f * current[i].Item1 + 1e-3f && MathF.Abs(gm - current[i].Item2) <= 2e-3f * current[i].Item2 + 1e-3f,
                            $"{w} x {h}, level {l} texel {i}: GPU ({gl}, {gm}), CPU {current[i]}");
                    }
                    offset += lw * lh;
                    previous = current;
                }
            }
        }
        Assert.True(d!.ValidationErrors == 0, "Validation errors:\n" + string.Join("\n", d.ValidationLog));
        d.Dispose();
    }
}
