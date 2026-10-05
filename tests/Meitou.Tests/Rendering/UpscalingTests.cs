using System.Numerics;
using Meitou.Rendering;

namespace Meitou.Tests.Rendering;

/// <summary>Jitter, render sizes and the camera reprojection behind the motion vectors (no GPU).</summary>
public class UpscalingTests
{
    [Fact]
    public void Halton_is_the_radical_inverse()
    {
        Assert.Equal(0.5f, Jitter.Halton(1, 2));
        Assert.Equal(0.25f, Jitter.Halton(2, 2));
        Assert.Equal(0.75f, Jitter.Halton(3, 2));
        Assert.Equal(1 / 3f, Jitter.Halton(1, 3), 6);
        Assert.Equal(2 / 3f, Jitter.Halton(2, 3), 6);
        Assert.Equal(1 / 9f, Jitter.Halton(3, 3), 6);
    }

    [Fact]
    public void Phase_count_grows_with_the_square_of_the_ratio()
    {
        Assert.Equal(8, Jitter.PhaseCount(1600, 1600));
        Assert.Equal(18, Jitter.PhaseCount(1067, 1600));   // quality, 1.5x: 8 * 2.25
        Assert.Equal(32, Jitter.PhaseCount(800, 1600));
        for (int i = 0; i < 64; i++)
        {
            var o = Jitter.Offset(i, 18);
            Assert.InRange(o.X, -0.5f, 0.5f);
            Assert.InRange(o.Y, -0.5f, 0.5f);
        }
        Assert.Equal(Jitter.Offset(3, 18), Jitter.Offset(21, 18));
    }

    [Fact]
    public void Jitter_moves_the_picture_by_render_pixels()
    {
        var p = Matrix4x4.CreatePerspectiveFieldOfView(0.9f, 16 / 9f, 1, 1000);
        var j = Jitter.Apply(p, new Vector2(0.25f, -0.5f), 1600, 900);
        foreach (var point in new[] { new Vector3(3, 2, -10), new Vector3(-40, 7, -600) })
        {
            var a = Vector4.Transform(new Vector4(point, 1), p);
            var b = Vector4.Transform(new Vector4(point, 1), j);
            Assert.Equal(a.X / a.W + 2 * 0.25f / 1600, b.X / b.W, 5);
            Assert.Equal(a.Y / a.W - 2 * 0.5f / 900, b.Y / b.W, 5);
            Assert.Equal(a.Z / a.W, b.Z / b.W, 6);
        }
    }

    [Fact]
    public void Render_size_follows_the_scale()
    {
        var o = new UpscaleOptions();
        Assert.Equal((1600, 900), o.RenderSize(1600, 900));   // off: always full size
        o.Kind = UpscalerKind.Fsr;
        Assert.Equal((1600, 900), o.RenderSize(1600, 900));   // native by default
        o.Scale = UpscaleOptions.ParseScale("performance");
        Assert.Equal((1280, 720), o.RenderSize(2560, 1440));
        o.Kind = UpscalerKind.Taa;
        o.Scale = null;
        Assert.Equal((2560, 1440), o.RenderSize(2560, 1440));
        Assert.Throws<ArgumentException>(() => UpscaleOptions.ParseScale("2"));
    }

    static Matrix4x4 Rotation(Vector3 eye, Vector3 target) => Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY) with { M41 = 0, M42 = 0, M43 = 0 };

    /// <summary>Where the world point lands on screen (NDC) for a camera: the reference the reprojection must agree with.</summary>
    static Vector2 Project(Vector3 world, Vector3 eye, Vector3 target, Matrix4x4 projection)
    {
        var c = Vector4.Transform(new Vector4(world, 1), Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY) * projection);
        return new Vector2(c.X / c.W, c.Y / c.W);
    }

    [Fact]
    public void Reprojection_carries_a_surface_point_into_the_previous_frame()
    {
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(0.9f, 16 / 9f, 2, 20000);
        // Far from the origin, as in the game world; the camera steps and turns between the frames.
        var previousEye = new Vector3(-51000, 900, -14300);
        var previousTarget = previousEye + new Vector3(300, -80, 40);
        var eye = previousEye + new Vector3(35, -3, 12);
        var target = eye + new Vector3(290, -75, 70);
        var jitter = new Vector2(0.3f, -0.2f);
        var jittered = Jitter.Apply(projection, jitter, 1600, 900);
        var m = Reprojection.ClipToPrevious(Rotation(eye, target), eye, jittered, Rotation(previousEye, previousTarget), previousEye, projection);
        foreach (var offset in new[] { new Vector3(250, -60, 30), new Vector3(800, -150, 300), new Vector3(1500, -200, -100) })
        {
            var world = eye + offset;
            // What this frame's depth buffer holds for the point (jittered), turned back into clip space as the shader does.
            var c = Vector4.Transform(new Vector4(offset, 1), Rotation(eye, target) * jittered);
            var ndc = new Vector4(c.X / c.W, c.Y / c.W, c.Z / c.W, 1);
            var prev = Vector4.Transform(ndc, m);
            var expected = Project(world, previousEye, previousTarget, projection);
            Assert.Equal(expected.X, prev.X / prev.W, 3);
            Assert.Equal(expected.Y, prev.Y / prev.W, 3);
        }
    }

    [Fact]
    public void A_still_camera_has_no_motion_but_the_jitter()
    {
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(0.9f, 16 / 9f, 2, 20000);
        var eye = new Vector3(-51000, 900, -14300);
        var rotation = Rotation(eye, eye + new Vector3(300, -80, 40));
        var jitter = new Vector2(-0.4f, 0.1f);
        var m = Reprojection.ClipToPrevious(rotation, eye, Jitter.Apply(projection, jitter, 1600, 900), rotation, eye, projection);
        var ndc = new Vector4(0.3f, -0.2f, 0.98f, 1);
        var prev = Vector4.Transform(ndc, m);
        // The previous position is this pixel minus the jitter: motion (current unjittered − previous) is zero.
        Assert.Equal(0.3f - 2 * jitter.X / 1600, prev.X / prev.W, 4);
        Assert.Equal(-0.2f - 2 * jitter.Y / 900, prev.Y / prev.W, 4);
    }
}
