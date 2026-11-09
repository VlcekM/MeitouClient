using System.Numerics;
using Meitou.Game.Rendering.Frame;

namespace Meitou.Tests.Rendering;

/// <summary>
/// A tiled screenshot's tiles (docs/game/ui-input.md "Screenshots"): the tile's projection and its rebuilt view rays agree with the whole view's.
/// </summary>
public class ViewWindowTests
{
    [Fact]
    public void A_tiles_projection_and_view_rays_match_the_whole_views()
    {
        var camera = new WorldCamera { FieldOfView = 1.0f };
        const int n = 3, w = 1600, h = 900, guard = 100;
        float aspect = (w + 2 * guard) / (float)(h + 2 * guard);
        var point = new Vector3(20, -10, -300);   // in view space
        float fullAspect = w / (float)h, tanY = MathF.Tan(camera.FieldOfView * 0.5f);
        var whole = Vector4.Transform(new Vector4(point, 1), camera.Projection(fullAspect, 1, 1000));
        var wholeNdc = new Vector2(whole.X, whole.Y) / whole.W;
        try
        {
            var tile = ViewWindow.Tile(n, 2, 1, w, h, guard);
            ViewWindow.Current = tile;
            var clip = Vector4.Transform(new Vector4(point, 1), camera.Projection(aspect, 1, 1000));
            var ndc = new Vector2(clip.X, clip.Y) / clip.W;

            // The tile covers 1/n of the picture (plus the guard): (column 2, row 1 of 3) is centred at ndc (2/3, 0) of the whole view.
            var expected = new Vector2((wholeNdc.X - 2f / 3) * n * w / (w + 2 * guard), (wholeNdc.Y - 0) * n * h / (h + 2 * guard));

            Assert.Equal(expected.X, ndc.X, 1e-3f);
            Assert.Equal(expected.Y, ndc.Y, 1e-3f);

            // A shader rebuilds the ray as right * ndc.x * tan.x + up * ndc.y * tan.y - back; it must hit the same point.
            var tan = ViewWindow.Tan(tanY * aspect, tanY);
            var back = ViewWindow.Back(Matrix4x4.Identity, tanY * aspect, tanY);
            var ray = Vector3.UnitX * ndc.X * tan.X + Vector3.UnitY * ndc.Y * tan.Y - back;

            Assert.Equal(point.X / -point.Z, ray.X / -ray.Z, 1e-3f);
            Assert.Equal(point.Y / -point.Z, ray.Y / -ray.Z, 1e-3f);
        }
        finally
        {
            ViewWindow.Current = ViewWindow.Whole;
        }
    }
}
