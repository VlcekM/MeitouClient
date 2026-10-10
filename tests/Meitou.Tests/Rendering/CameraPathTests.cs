using System.Numerics;

using Meitou.Rendering;

namespace Meitou.Tests.Rendering;

public class CameraPathTests
{
    static CameraShot Shot(float x, float z, float yawDeg, float hour = 12, float distance = 2000) =>
        new(new Vector3(x, 500, z), yawDeg * MathF.PI / 180, 0.3f, distance, 0.9f, hour);

    // Unevenly timed on purpose: 4 s, then 10 s, then 2 s.
    static readonly CameraKey[] Keys = [new(Shot(0, 0, 0), 0), new(Shot(5000, 0, 90), 4), new(Shot(5000, 8000, 180), 14), new(Shot(9000, 8000, 200), 16)];

    static Vector3 Velocity(IReadOnlyList<CameraKey> keys, float t, float? loopBack = null, float h = 1e-3f) =>
        (CameraPath.Sample(keys, t + h, loopBack).Eye - CameraPath.Sample(keys, t - h, loopBack).Eye) / (2 * h);

    /// <summary>The velocity arriving at <paramref name="t"/> and leaving it (one-sided differences, so a key's two segments are measured apart).</summary>
    static (Vector3 In, Vector3 Out) Through(IReadOnlyList<CameraKey> keys, float t, float? loopBack = null, float h = 2e-3f)
    {
        var at = CameraPath.Sample(keys, t, loopBack).Eye;
        return ((at - CameraPath.Sample(keys, t - h, loopBack).Eye) / h, (CameraPath.Sample(keys, t + h, loopBack).Eye - at) / h);
    }

    static float TopSpeed(IReadOnlyList<CameraKey> keys) => Enumerable.Range(1, 159).Max(i => Velocity(keys, i * 0.1f).Length());

    [Fact]
    public void The_flight_reaches_every_key_at_its_time()
    {
        foreach (var k in Keys)
        {
            var s = CameraPath.Sample(Keys, k.Time);
            Assert.True(Vector3.Distance(s.Eye, k.Shot.Eye) < 0.5f, $"{k.Time} s: {s.Eye}");
            Assert.Equal(k.Shot.Yaw, s.Yaw, 3);
            Assert.Equal(k.Shot.Distance, s.Distance, 0);
        }
        // Before the first key and after the last, an open flight holds.
        Assert.Equal(Keys[0].Shot.Eye, CameraPath.Sample(Keys, -5).Eye);
        Assert.Equal(Keys[^1].Shot.Eye, CameraPath.Sample(Keys, 99).Eye);
        Assert.Equal(16, CameraPath.Duration(Keys, null));
    }

    [Fact]
    public void Speed_changes_smoothly_through_unevenly_timed_keys()
    {
        // The velocity just before and just after an inner key is the same (C1 in time), although the legs take 4 s and 10 s.
        foreach (float t in new[] { 4f, 14f })
        {
            var (before, after) = Through(Keys, t);
            Assert.True(Vector3.Distance(before, after) < before.Length() * 0.03f, $"{t} s: {before} against {after}");
            Assert.True(before.Length() > 100, $"{t} s: moving, {before.Length()}");
        }
    }

    [Fact]
    public void An_open_flight_starts_and_ends_at_rest_and_an_easing_key_stops()
    {
        float top = TopSpeed(Keys);
        Assert.True(Through(Keys, 0).Out.Length() < top * 0.03f, $"{Through(Keys, 0).Out} of {top}");
        Assert.True(Through(Keys, 16).In.Length() < top * 0.03f, $"{Through(Keys, 16).In} of {top}");
        var eased = Keys.ToArray();
        eased[1] = eased[1] with { Ease = true };
        var (inEase, outEase) = Through(eased, 4);
        Assert.True(inEase.Length() < top * 0.03f && outEase.Length() < top * 0.03f, $"{inEase} {outEase} of {top}");
        Assert.True(Through(Keys, 4).In.Length() > 100);   // the same key without the ease goes straight through
    }

    [Fact]
    public void Yaw_and_hour_take_the_short_way_round()
    {
        CameraKey[] wrap = [new(Shot(0, 0, 350, hour: 23), 0), new(Shot(1000, 0, 10, hour: 1), 5)];
        var mid = CameraPath.Sample(wrap, 2.5f);
        float yaw = (mid.Yaw * 180 / MathF.PI % 360 + 360) % 360;
        Assert.True(yaw > 355 || yaw < 5, $"yaw {yaw}");   // through 0, not back through 180
        Assert.True(mid.Hour > 23.5f || mid.Hour < 0.5f, $"hour {mid.Hour}");   // through midnight
    }

    [Fact]
    public void A_loop_returns_to_the_first_key_and_keeps_moving_through_it()
    {
        const float back = 3;
        Assert.Equal(19, CameraPath.Duration(Keys, back));
        Assert.True(Vector3.Distance(CameraPath.Sample(Keys, 19, back).Eye, Keys[0].Shot.Eye) < 0.5f);
        Assert.True(Vector3.Distance(CameraPath.Sample(Keys, 19 + 4, back).Eye, Keys[1].Shot.Eye) < 0.5f);   // the second lap
        var (before, after) = Through(Keys, 19, back);
        Assert.True(before.Length() > 100, $"{before}");   // no stop at the seam
        Assert.True(Vector3.Distance(before, after) < before.Length() * 0.03f, $"{before} against {after}");
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
