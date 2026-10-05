using System.Numerics;
using Meitou.Data.World;
using Meitou.Engine.Cameras;
using Meitou.Engine.Input;

namespace Meitou.Tests.Engine;

public class CameraTests
{
    static readonly Func<float, float, float> Flat = (_, _) => 0;
    // The pivot stands on a spike; everywhere else the ground is far below, so only the pitch rule (not the clearance) shapes the view.
    static readonly Func<float, float, float> DeepGround = (x, z) => MathF.Abs(x) < 1 && MathF.Abs(z) < 1 ? 0 : -100000;

    readonly InputState input = new();
    readonly InputBindings bindings = new();

    ActionState Tick() => bindings.Resolve(input.Consume());

    [Fact]
    public void Starts_with_a_boom_of_150_pitched_30_degrees_down()
    {
        var s = new StrategyCamera(Flat).State;
        Assert.Equal(150f, s.Distance, 3);
        Assert.Equal(30f, s.Pitch * 180 / MathF.PI, 3);
        Assert.Equal(-0.5f, s.Forward.Y, 4);
        Assert.Equal(75f, s.Eye.Y, 3);
        Assert.Equal(Vector3.Zero, s.Target);
        // Yaw 0 puts the eye at +Z of the pivot and looks along -Z, as the viewer's WorldCamera does.
        Assert.True(s.Eye.Z > 0 && s.Forward.Z < 0);
    }

    [Fact]
    public void State_reproduces_the_eye_through_the_viewers_orbit_formula()
    {
        var cam = new StrategyCamera((x, z) => 0.05f * x);
        cam.Place(new Vector2(100, 40), 1.1f, 0.7f, 300);
        var s = cam.State;
        Assert.Equal(s.Eye.X, CameraState.EyeFor(s.Target, s.Yaw, s.Pitch, s.Distance).X, 2);
        Assert.Equal(s.Eye.Y, CameraState.EyeFor(s.Target, s.Yaw, s.Pitch, s.Distance).Y, 2);
        Assert.Equal(s.Eye.Z, CameraState.EyeFor(s.Target, s.Yaw, s.Pitch, s.Distance).Z, 2);
        Assert.Equal(Vector3.Normalize(s.Target - s.Eye).X, s.Forward.X, 4);
    }

    [Fact]
    public void Zoom_follows_the_step_formula()
    {
        var cam = new StrategyCamera(Flat); // zoom speed 125
        cam.Zoom(1);
        Assert.Equal(150 - 125 * 1 * (150 / 600f), cam.Distance, 3);
        cam.Place(default, 0, 0.5f, 1000);
        cam.Zoom(1); // length / 600 > 0.75: capped
        Assert.Equal(1000 - 125 * 0.75f, cam.Distance, 3);
        cam.Zoom(-2);
        Assert.Equal(1000 - 125 * 0.75f + 2 * 125 * 0.75f, cam.Distance, 3);
    }

    [Fact]
    public void Zoom_clamps_at_10_and_2000()
    {
        var cam = new StrategyCamera(Flat);
        for (int i = 0; i < 200; i++) cam.Zoom(1);
        Assert.Equal(10f, cam.Distance);
        for (int i = 0; i < 2000; i++) cam.Zoom(-1);
        Assert.Equal(2000f, cam.Distance);
        cam.Zoom(100000);
        Assert.Equal(10f, cam.Distance);
    }

    [Fact]
    public void Zoom_speed_comes_from_the_settings_and_the_wheel_drives_it()
    {
        var rig = new CameraRig(Flat, new CameraSettings { ZoomSpeed = 50 });
        input.AddWheel(2);
        rig.Update(1 / 30f, Tick());
        Assert.Equal(150 - 50 * 2 * 0.25f, rig.Strategy.Distance, 3);
    }

    [Fact]
    public void Pitch_down_is_refused_once_the_view_is_at_minus_0_92_with_one_step_of_overshoot()
    {
        var cam = new StrategyCamera(DeepGround);
        for (int i = 0; i < 200; i++) cam.PitchStep(0.05f);
        float y = cam.State.Forward.Y;
        // The last allowed step started above -0.92 and may overshoot it by less than one sub-step of 0.02 rad.
        Assert.InRange(y, -0.92f - 0.02f, -0.92f);
        float pitch = cam.Pitch;
        cam.PitchStep(1);
        Assert.Equal(pitch, cam.Pitch);
    }

    [Fact]
    public void Pitch_up_is_refused_once_the_view_is_at_0_2_with_one_step_of_overshoot()
    {
        var cam = new StrategyCamera(DeepGround);
        for (int i = 0; i < 200; i++) cam.PitchStep(-0.05f);
        Assert.InRange(cam.State.Forward.Y, 0.2f, 0.2f + 0.02f);
        float pitch = cam.Pitch;
        cam.PitchStep(-1);
        Assert.Equal(pitch, cam.Pitch);
        cam.PitchStep(0.1f); // and down still works
        Assert.True(cam.Pitch > pitch);
    }

    [Fact]
    public void A_big_pitch_step_overshoots_no_more_than_a_small_one_and_the_hard_clamp_removes_it()
    {
        var soft = new StrategyCamera(DeepGround);
        soft.PitchStep(5);
        Assert.InRange(soft.State.Forward.Y, -0.92f - 0.02f, -0.92f);
        var hard = new StrategyCamera(DeepGround, new CameraSettings { HardPitchClamp = true });
        hard.PitchStep(5);
        Assert.Equal(-0.92f, hard.State.Forward.Y, 4);
        hard.PitchStep(-5);
        Assert.Equal(0.2f, hard.State.Forward.Y, 4);
    }

    [Fact]
    public void The_eye_is_at_most_1840_above_the_pivot_at_the_limits()
    {
        Assert.Equal(1840f, KenshiCamera.MaxHeightAbovePivot);
        var cam = new StrategyCamera(Flat);
        cam.Place(default, 0, 0.5f, 2000);
        for (int i = 0; i < 200; i++) cam.PitchStep(0.05f);
        float h = cam.State.Eye.Y - cam.State.Target.Y;
        // At the refusal point the view is at -0.92 or one sub-step steeper.
        Assert.InRange(h, 1840f, 1840f + 2000 * 0.02f * 0.4f);
        // Zoomed out and held there, nothing takes it higher.
        for (int i = 0; i < 100; i++) cam.Zoom(-1);
        Assert.InRange(cam.State.Eye.Y - cam.State.Target.Y, 0f, 1860f);
    }

    [Theory]
    [InlineData(1.57f, 0f)]    // eye uphill of the pivot on a slope
    [InlineData(-1.57f, 0.2f)] // eye downhill
    [InlineData(0f, 0.5f)]
    public void The_eye_stays_above_the_ground_under_it_by_the_clearance_and_the_asked_pitch_is_kept(float yaw, float asked)
    {
        Func<float, float, float> hill = (x, z) => 0.5f * x + 5 * MathF.Sin(z * 0.01f);
        var cam = new StrategyCamera(hill);
        foreach (float boom in new[] { 10f, 40f, 150f, 600f, 2000f })
        {
            cam.Place(new Vector2(20, 30), yaw, asked, boom);
            var eye = cam.State.Eye;
            float min = hill(eye.X, eye.Z) + KenshiCamera.EyeClearance(cam.Distance);
            Assert.True(eye.Y >= min - 0.01f, $"boom {boom}: eye {eye.Y} under {min}");
            Assert.Equal(asked, cam.Pitch, 5);
        }
    }

    [Fact]
    public void Clearance_is_min_of_a_fifth_of_the_boom_and_20_plus_10()
    {
        Assert.Equal(12f, KenshiCamera.EyeClearance(10));
        Assert.Equal(30f, KenshiCamera.EyeClearance(100));
        Assert.Equal(30f, KenshiCamera.EyeClearance(2000));
    }

    [Fact]
    public void The_eye_stays_above_the_ground_over_a_long_random_drive()
    {
        Func<float, float, float> hills = (x, z) => 200 * MathF.Sin(x * 0.003f) * MathF.Cos(z * 0.002f);
        var rig = new CameraRig(hills);
        var rng = new Random(3);
        var keys = new[] { Key.W, Key.A, Key.S, Key.D, Key.Q, Key.E, Key.PageUp, Key.PageDown, Key.Up, Key.Down };
        for (int i = 0; i < 3000; i++)
        {
            if (rng.Next(8) == 0) input.SetKey(keys[rng.Next(keys.Length)], rng.Next(2) == 0);
            input.AddWheel(rng.Next(5) == 0 ? rng.Next(-3, 4) : 0);
            rig.Update(1 / 30f, Tick());
            var s = rig.Current;
            Assert.True(s.Eye.Y >= hills(s.Eye.X, s.Eye.Z) + 12 - 0.05f, $"tick {i}");
            Assert.InRange(rig.Strategy.Distance, 10f, 2000f);
        }
    }

    [Fact]
    public void Pivot_moves_on_the_ground_relative_to_yaw_and_follows_the_terrain()
    {
        var cam = new StrategyCamera((x, z) => 0.1f * x);
        var forwardOnly = new InputState();
        var b = new InputBindings();
        forwardOnly.SetKey(Key.W, true);
        cam.Update(0.1f, b.Resolve(forwardOnly.Consume())); // yaw 0: forward is -Z
        Assert.True(cam.Pivot.Z < 0);
        Assert.Equal(0f, cam.Pivot.X, 4);
        cam.Rotate(-MathF.PI / 2); // turn right: forward is now +X
        float x0 = cam.Pivot.X;
        cam.Update(0.1f, b.Resolve(forwardOnly.Consume()));
        Assert.True(cam.Pivot.X > x0);
        Assert.Equal(0.1f * cam.Pivot.X, cam.Pivot.Y, 4);
        Assert.Equal(cam.Pivot, cam.State.Target);
    }

    [Fact]
    public void Dragging_with_the_orbit_button_turns_the_view()
    {
        var rig = new CameraRig(Flat);
        float yaw = rig.Strategy.Yaw;
        float pitch = rig.Strategy.Pitch;
        input.AddMouseDelta(40, 20);
        rig.Update(1 / 30f, Tick()); // no button: nothing
        Assert.Equal(yaw, rig.Strategy.Yaw);
        input.SetMouseButton(MouseButton.Middle, true);
        input.AddMouseDelta(40, 20);
        rig.Update(1 / 30f, Tick());
        Assert.Equal(yaw - 40 * 0.005f, rig.Strategy.Yaw, 5);
        Assert.Equal(pitch + 20 * 0.005f, rig.Strategy.Pitch, 5);
    }

    [Fact]
    public void Free_camera_toggles_flies_above_the_ground_and_speeds_up_with_height()
    {
        var rig = new CameraRig(Flat);
        var start = rig.Current;
        input.SetKey(Key.Semicolon, true);
        rig.Update(1 / 30f, Tick());
        Assert.True(rig.IsFree);
        Assert.True(rig.Current.IsFree);
        Assert.Equal(start.Eye, rig.Current.Eye);
        Assert.Equal(rig.Current, rig.Previous); // a cut, not a blend
        input.SetKey(Key.Semicolon, false);

        float lowSpeed = rig.Free.Speed;
        input.SetKey(Key.R, true); // up
        for (int i = 0; i < 90; i++) rig.Update(1 / 30f, Tick());
        input.SetKey(Key.R, false);
        Assert.True(rig.Free.Speed > lowSpeed * 3);

        input.SetKey(Key.F, true); // down: stops at the clearance
        for (int i = 0; i < 600; i++) rig.Update(1 / 30f, Tick());
        Assert.Equal(rig.Free.Position.Y, new CameraSettings().FreeClearance, 3);
        Assert.True(rig.Current.Eye.Y >= 10 - 1e-3f);
    }

    [Fact]
    public void Leaving_free_flight_puts_the_pivot_where_the_view_meets_the_ground()
    {
        var rig = new CameraRig(Flat);
        input.SetKey(Key.Semicolon, true);
        rig.Update(1 / 30f, Tick());
        input.SetKey(Key.Semicolon, false);
        input.SetKey(Key.R, true);
        for (int i = 0; i < 30; i++) rig.Update(1 / 30f, Tick());
        input.SetKey(Key.R, false);
        for (int i = 0; i < 60; i++) rig.Update(1 / 30f, Tick()); // coast to a stop
        var eye = rig.Free.Position;
        var look = rig.Free.LookPointOnGround(CameraRig.LookRange);
        Assert.NotNull(look);
        Assert.Equal(0f, look!.Value.Y, 2);

        input.SetKey(Key.Semicolon, true);
        rig.Update(1 / 30f, Tick());
        Assert.False(rig.IsFree);
        Assert.Equal(look.Value.X, rig.Strategy.Pivot.X, 1);
        Assert.Equal(look.Value.Z, rig.Strategy.Pivot.Z, 1);
        Assert.Equal(Vector3.Distance(eye, look.Value), rig.Strategy.Distance, 1);
        Assert.Equal(eye.Y, rig.Current.Eye.Y, 0); // same view
    }

    [Fact]
    public void Leaving_free_flight_while_looking_up_takes_the_ground_under_the_eye()
    {
        var rig = new CameraRig((x, z) => 0.1f * x);
        rig.Free.Enter(new CameraState(new Vector3(500, 400, 0), new Vector3(0, 0.5f, -1), Vector3.UnitY, default, 0, -0.5f, 150, false));
        input.SetKey(Key.Semicolon, true);
        rig.Update(1 / 30f, Tick()); // strategy -> free
        Assert.True(rig.IsFree);
        input.SetKey(Key.Semicolon, false);
        Tick();
        input.SetKey(Key.Semicolon, true);
        rig.Update(1 / 30f, Tick());
        Assert.False(rig.IsFree);
        Assert.Equal(150f, rig.Strategy.Distance, 3);
    }

    [Fact]
    public void Rig_exposes_previous_and_current_for_interpolation()
    {
        var rig = new CameraRig(Flat);
        input.SetKey(Key.W, true);
        rig.Update(1 / 30f, Tick());
        rig.Update(1 / 30f, Tick());
        Assert.NotEqual(rig.Previous.Eye, rig.Current.Eye);
        Assert.Equal(rig.Previous.Eye, rig.At(0).Eye);
        Assert.Equal(rig.Current.Eye, rig.At(1).Eye);
        Assert.Equal((rig.Previous.Eye.Z + rig.Current.Eye.Z) / 2, rig.At(0.5f).Eye.Z, 4);
    }
}
