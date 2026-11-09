using System.Numerics;
using Meitou.Game.Rendering.Shadows;
using Meitou.Game.Rendering.Sky;
using Meitou.Tests.World;

namespace Meitou.Tests.Rendering;

/// <summary>
/// The sun shadows' range cap from the placed fog block (<see cref="ShadowFogCap"/>, <see cref="FogVolumes.ShadowHideDistance"/>); pure arithmetic,
/// no GPU.
/// </summary>
public class ShadowFogCapTests
{
    const float Fov = 50 * MathF.PI / 180, Aspect = 16f / 9;

    [Fact]
    public void A_radius_that_wobbles_within_8_percent_keeps_the_cap_still()
    {
        var cap = new ShadowFogCap();
        float first = cap.Update(4455)!.Value;
        int changes = 0;
        float last = first;

        // Down by up to 8 % and back, as the eye moves a few hundred units.
        for (int i = 0; i < 400; i++)
        {
            float r = 4455 * (1 - 0.04f - 0.04f * MathF.Sin(i * 0.11f));
            float now = cap.Update(r)!.Value;

            Assert.True(now >= r * ShadowFogCap.Margin * 0.9999f, "the cap always covers the radius");

            if (now != last) changes++;
            last = now;
        }

        Assert.Equal(0, changes);
    }

    [Fact]
    public void The_hide_distance_is_given_only_where_the_block_hides_the_whole_view_and_then_every_ray_is_opaque_past_it()
    {
        var f = FogCullTests.Block();
        var rng = new Random(23);
        int given = 0, withheld = 0;
        for (int trial = 0; trial < 400; trial++)
        {
            var eye = new Vector3(rng.Next(-40000, 40000), rng.Next(-500, 2900), rng.Next(-40000, 40000));
            float yaw = rng.Next(0, 360) * MathF.PI / 180, pitch = rng.Next(-60, 30) * MathF.PI / 180;
            var forward = new Vector3(MathF.Cos(pitch) * MathF.Cos(yaw), MathF.Sin(pitch), MathF.Cos(pitch) * MathF.Sin(yaw));
            float far = rng.Next(8000, 60000);
            var v = new FogVolumes([f]) { SolveInline = true };
            v.Update(eye, forward, Fov, Aspect, far, 0.5f, on: true);
            if (v.ShadowHideDistance is not { } hide)
            {
                withheld++;

                Assert.False(v.SkyHidden && v.CullBlock is not null);

                continue;
            }
            given++;

            Assert.True(v.SkyHidden);
            Assert.Equal(v.CullRadius, hide);

            // A surface at the distance along any ray of the view (the frustum's edges and corners) is fogged to 0.9998: nothing beyond it can show.
            var side = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
            var up = Vector3.Cross(side, forward);
            float tanY = MathF.Tan(Fov / 2), tanX = tanY * Aspect;
            for (int i = 0; i <= 8; i++)
                for (int j = 0; j <= 8; j++)
                {
                    var d = Vector3.Normalize(forward + side * tanX * (i / 4f - 1) + up * tanY * (j / 4f - 1));

                    Assert.True(FogCullTests.Alpha(f, eye, d, hide) >= 0.9998f - 1e-4f, $"eye {eye} forward {forward} ray {d} hide {hide}");
                }
        }

        Assert.True(given > 20 && withheld > 20, $"{given} given, {withheld} withheld: the test does not exercise both sides");
    }
}
