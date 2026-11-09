using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Data.World;

namespace Meitou.Tests.Save;

/// <summary>
/// The CAMERA record's view: <c>pos</c>, <c>rot</c> (w, x, y, z) and <c>zoom</c> against the strategy camera's yaw, pitch and boom
/// (docs/formats/camera.md "Saved camera").
/// </summary>
public class SavedCameraTests
{
    [Theory]
    [InlineData(99.7f, 30f, 88f)]
    [InlineData(-148.2f, 46.8f, 250f)]
    public void A_view_round_trips_through_rot_and_zoom(float yawDegrees, float pitchDegrees, float distance)
    {
        float yaw = yawDegrees * MathF.PI / 180, pitch = pitchDegrees * MathF.PI / 180;
        var (rot, zoom) = KenshiCamera.ToSaved(yaw, pitch, distance);

        Assert.Equal(1, rot.Length(), 4);   // a unit quaternion
        Assert.Equal(-distance, zoom);

        var back = KenshiCamera.FromSaved(rot, zoom);

        Assert.Equal(yaw, back.Yaw, 3);
        Assert.Equal(pitch, back.Pitch, 3);
        Assert.Equal(distance, back.Distance, 3);
    }

    [Fact]
    public void A_camera_the_game_saved_reads_as_its_view()
    {
        // rot and zoom of a Kenshi 1.0.68 save (numbers only): the stored order is w, x, y, z; read as x, y, z, w the view would point above the
        // pitch limit.
        var (yaw, pitch, distance) = KenshiCamera.FromSaved(new Vector4(0.7381f, 0.1978f, -0.6231f, 0.167f), -88.0341f);

        Assert.Equal(99.7f, yaw * 180 / MathF.PI, 0);
        Assert.Equal(30f, pitch * 180 / MathF.PI, 0);
        Assert.Equal(88.0341f, distance, 3);
    }

    [Fact]
    public void A_pivot_out_of_bounds_goes_to_the_loaders_fallback_and_an_old_meitou_save_has_no_view()
    {
        Assert.True(KenshiCamera.SavedPivotInBounds(new Vector3(-64080, 1586, 21640)));
        Assert.False(KenshiCamera.SavedPivotInBounds(new Vector3(147457, 0, 0)));
        Assert.False(KenshiCamera.SavedPivotInBounds(new Vector3(0, 0, -150000)));
        Assert.Equal(new Vector2(500, 51000), KenshiCamera.SavedPivotFallback);

        // Meitou saves written before the camera was kept: pos 0 and rot (0, 0, 0, 1). A new save now starts at the game's own view.
        var legacy = new FcsRecord { Type = (int)FcsRecordType.Camera };
        legacy.Vector3S["pos"] = Vector3.Zero;
        legacy.Vector4S["rot"] = new Vector4(0, 0, 0, 1);

        Assert.False(new SaveCamera(legacy).HasView);

        var fresh = new SaveBuilder(1).Game.Camera;

        Assert.True(fresh.HasView);
        Assert.Equal(30f, KenshiCamera.FromSaved(fresh.Rotation, fresh.Zoom).Pitch * 180 / MathF.PI, 2);
    }
}
