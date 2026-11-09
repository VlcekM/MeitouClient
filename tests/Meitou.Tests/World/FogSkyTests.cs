using System.Numerics;
using Meitou.Game.Rendering.Sky;

namespace Meitou.Tests.World;

/// <summary>
/// The sky's fog-volume early-out (<see cref="FogVolumes.SkyHidden"/>, docs/render-clouds.md "Fog early-out"): when it holds, every ray of the view
/// is fogged to 0.9998 at the far clip.
/// </summary>
public class FogSkyTests
{
    const float Fov = 50 * MathF.PI / 180, Aspect = 16f / 9;

    static Vector3 Forward(float yawDegrees, float pitchDegrees)
    {
        float yaw = yawDegrees * MathF.PI / 180, pitch = pitchDegrees * MathF.PI / 180;
        return new Vector3(MathF.Cos(pitch) * MathF.Cos(yaw), MathF.Sin(pitch), MathF.Cos(pitch) * MathF.Sin(yaw));
    }

    /// <summary>
    /// Unit rays over the frustum (its edges and corners included), as the projection makes them, without the bound's 2 % widening.
    /// </summary>
    static IEnumerable<Vector3> Rays(Vector3 forward, int n = 24)
    {
        var side = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var up = Vector3.Cross(side, forward);
        float tanY = MathF.Tan(Fov / 2), tanX = tanY * Aspect;
        for (int i = 0; i <= n; i++)
            for (int j = 0; j <= n; j++)
                yield return Vector3.Normalize(forward + side * tanX * (2f * i / n - 1) + up * tanY * (2f * j / n - 1));
    }

    [Fact]
    public void Where_the_sky_counts_as_hidden_every_ray_of_the_view_is_fogged_at_the_far_clip_by_the_shaders_formula()
    {
        var f = FogCullTests.Block();
        var rng = new Random(11);
        int hidden = 0, shown = 0;
        for (int trial = 0; trial < 600; trial++)
        {
            var eye = new Vector3(rng.Next(-40000, 40000), rng.Next(-500, 2900), rng.Next(-40000, 40000));
            var forward = Forward(rng.Next(0, 360), rng.Next(-60, 30));
            float far = rng.Next(8000, 60000);
            var v = new FogVolumes([f]) { SolveInline = true };
            v.Update(eye, forward, Fov, Aspect, far, 0.5f, on: true);
            if (!v.SkyHidden)
            {
                shown++;
                continue;
            }
            hidden++;
            foreach (var d in Rays(forward))
            {
                float alpha = FogCullTests.Alpha(f, eye, d, far);
                Assert.True(alpha >= 0.9998f - 1e-4f, $"alpha {alpha} eye {eye} forward {forward} ray {d} far {far}");
            }
        }
        Assert.True(hidden > 30 && shown > 30, $"{hidden} hidden, {shown} shown: the test does not exercise both sides");
    }
}
