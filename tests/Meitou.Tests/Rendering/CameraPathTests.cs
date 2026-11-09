using System.Numerics;
using Meitou.Game.Rendering.Frame;

namespace Meitou.Tests.Rendering;

public class CameraPathTests
{
    // Unevenly timed on purpose: 4 s, then 10 s, then 2 s.
    static readonly CameraKey[] Keys = [new(Shot(0, 0, 0), 0), new(Shot(5000, 0, 90), 4), new(Shot(5000, 8000, 180), 14),
            new(Shot(9000, 8000, 200), 16)];

    static CameraShot Shot(float x, float z, float yawDeg, float hour = 12, float distance = 2000) =>
        new(new Vector3(x, 500, z), yawDeg * MathF.PI / 180, 0.3f, distance, 0.9f, hour);

    /// <summary>
    /// The velocity arriving at <paramref name="t"/> and leaving it (one-sided differences, so a key's two segments are measured apart).
    /// </summary>
    static (Vector3 In, Vector3 Out) Through(IReadOnlyList<CameraKey> keys, float t, float? loopBack = null, float h = 2e-3f)
    {
        var at = CameraPath.Sample(keys, t, loopBack).Eye;
        return ((at - CameraPath.Sample(keys, t - h, loopBack).Eye) / h, (CameraPath.Sample(keys, t + h, loopBack).Eye - at) / h);
    }

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
}
