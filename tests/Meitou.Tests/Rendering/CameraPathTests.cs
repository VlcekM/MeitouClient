using System.Numerics;

using Meitou.Rendering;

namespace Meitou.Tests.Rendering;

public class CameraPathTests
{
    static CameraShot Shot(float x, float z, float yawDeg, float hour = 12, float distance = 2000) =>
        new(new Vector3(x, 500, z), yawDeg * MathF.PI / 180, 0.3f, distance, 0.9f, hour);

    static readonly CameraShot[] Three = [Shot(0, 0, 0), Shot(5000, 0, 90), Shot(5000, 8000, 180)];

    [Fact]
    public void The_flight_passes_every_shot()
    {
        for (int i = 0; i < Three.Length; i++)
        {
            var s = CameraPath.Sample(Three, i, loop: false);
            Assert.True(Vector3.Distance(s.Eye, Three[i].Eye) < 0.5f, $"shot {i}: {s.Eye}");
            Assert.Equal(Three[i].Yaw, s.Yaw, 3);
            Assert.Equal(Three[i].Distance, s.Distance, 0);
        }
        // Looping, the lap ends on the first shot again.
        Assert.True(Vector3.Distance(CameraPath.Sample(Three, 3, loop: true).Eye, Three[0].Eye) < 0.5f);
    }

    [Fact]
    public void Yaw_and_hour_take_the_short_way_round()
    {
        CameraShot[] wrap = [Shot(0, 0, 350, hour: 23), Shot(1000, 0, 10, hour: 1)];
        var mid = CameraPath.Sample(wrap, 0.5f, loop: false);
        float yaw = (mid.Yaw * 180 / MathF.PI % 360 + 360) % 360;
        Assert.True(yaw > 355 || yaw < 5, $"yaw {yaw}");   // through 0, not back through 180
        Assert.True(mid.Hour > 23.5f || mid.Hour < 0.5f, $"hour {mid.Hour}");   // through midnight
    }

    [Fact]
    public void An_open_flight_starts_and_ends_at_rest_and_keeps_an_even_pace_between()
    {
        const float per = 4;
        float At(float t) => CameraPath.Progress(4, per, t, loop: false);
        Assert.Equal(0, At(0), 4);
        Assert.Equal(3, At(CameraPath.Duration(4, per, false)), 4);
        Assert.Equal(3, At(100), 4);
        // Slow at the ends, even in the middle.
        float start = At(0.1f) - At(0), middle = At(6.1f) - At(6), end = At(12) - At(11.9f);
        Assert.True(start < middle * 0.2f && end < middle * 0.2f, $"{start} {middle} {end}");
        Assert.Equal(At(6.1f) - At(6), At(5.1f) - At(5), 4);
        // Monotonic.
        for (float t = 0; t < 12; t += 0.05f) Assert.True(At(t + 0.05f) >= At(t));
    }

    [Fact]
    public void Close_shots_do_not_overshoot()
    {
        // Two shots near each other between two far apart: the centripetal spline keeps the short segment between its ends.
        CameraShot[] shots = [Shot(0, 0, 0), Shot(10000, 0, 0), Shot(10050, 0, 0), Shot(20000, 0, 0)];
        for (float s = 1; s <= 2; s += 0.05f)
        {
            float x = CameraPath.Sample(shots, s, loop: false).Eye.X;
            Assert.InRange(x, 9999, 10051);
        }
    }

    [Fact]
    public void Apply_puts_the_eye_and_the_view_back()
    {
        var camera = new WorldCamera { Target = new Vector3(100, 200, 300), Yaw = 1.2f, Pitch = 0.4f, Distance = 1500 };
        var shot = CameraShot.Of(camera, 9);
        var other = new WorldCamera();
        shot.Apply(other);
        Assert.True(Vector3.Distance(other.Eye, camera.Eye) < 0.01f);
        Assert.True(Vector3.Distance(other.Target, camera.Target) < 0.01f);
    }
}
