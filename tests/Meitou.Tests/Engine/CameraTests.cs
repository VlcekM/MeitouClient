using System.Numerics;
using Meitou.Game.Host.Cameras;
using Meitou.Game.Host.Input;

namespace Meitou.Tests.Engine;

public class CameraTests
{
    static readonly Func<float, float, float> Flat = (_, _) => 0;
    readonly InputState _input = new();
    readonly InputBindings _bindings = new();

    ActionState Tick() => _bindings.Resolve(_input.Consume());

    [Fact]
    public void The_eye_stays_above_the_ground_over_a_long_random_drive()
    {
        Func<float, float, float> hills = (x, z) => 200 * MathF.Sin(x * 0.003f) * MathF.Cos(z * 0.002f);
        var rig = new CameraRig(hills);
        var rng = new Random(3);
        var keys = new[] { Key.W, Key.A, Key.S, Key.D, Key.Q, Key.E, Key.Home, Key.End, Key.Up, Key.Down };

        for (int i = 0; i < 3000; i++)
        {
            if (rng.Next(8) == 0) _input.SetKey(keys[rng.Next(keys.Length)], rng.Next(2) == 0);

            _input.AddWheel(rng.Next(5) == 0 ? rng.Next(-3, 4) : 0);
            rig.Update(1 / 30f, Tick());
            var s = rig.Current;
            Assert.True(s.Eye.Y >= hills(s.Eye.X, s.Eye.Z) + 12 - 0.05f, $"tick {i}");
            Assert.InRange(rig.Strategy.Distance, 10f, 2000f);
        }
    }

    [Fact]
    public void Leaving_free_flight_puts_the_pivot_where_the_view_meets_the_ground()
    {
        var rig = new CameraRig(Flat);
        _input.SetKey(Key.Semicolon, true);
        rig.Update(1 / 30f, Tick());
        _input.SetKey(Key.Semicolon, false);
        _input.SetKey(Key.R, true);
        for (int i = 0; i < 30; i++) rig.Update(1 / 30f, Tick());

        _input.SetKey(Key.R, false);
        for (int i = 0; i < 60; i++) rig.Update(1 / 30f, Tick()); // coast to a stop

        var eye = rig.Free.Position;
        var look = rig.Free.LookPointOnGround(CameraRig.LookRange);
        Assert.NotNull(look);
        Assert.Equal(0f, look!.Value.Y, 2);

        _input.SetKey(Key.Semicolon, true);
        rig.Update(1 / 30f, Tick());
        Assert.False(rig.IsFree);
        Assert.Equal(look.Value.X, rig.Strategy.Pivot.X, 1);
        Assert.Equal(look.Value.Z, rig.Strategy.Pivot.Z, 1);
        Assert.Equal(Vector3.Distance(eye, look.Value), rig.Strategy.Distance, 1);
        Assert.Equal(eye.Y, rig.Current.Eye.Y, 0); // same view
    }

    StrategyCamera Placed(Func<float, float, float> ground, float pitch = 30 * MathF.PI / 180, float boom = 600)
    {
        var cam = new StrategyCamera(ground);
        cam.Place(Vector2.Zero, 0, pitch, boom);
        return cam;
    }

    [Fact]
    public void Pan_speed_follows_the_boom_the_diagonal_leans_with_the_pitch_and_a_notch_is_1_2()
    {
        var cam = Placed(Flat);
        _input.SetKey(Key.W, true);
        _input.SetKey(Key.D, true);
        for (int i = 0; i < 30; i++) cam.Update(1 / 30f, Tick());

        Assert.Equal(500, cam.PivotXz.Length(), 1);   // camera speed 500 at a boom of 600, one second
        Assert.Equal(49, MathF.Atan2(cam.PivotXz.X, -cam.PivotXz.Y) * 180 / MathF.PI, 0);   // forward shrinks by cos 30 degrees

        var zoom = Placed(Flat);
        _input.SetKey(Key.W, false);
        _input.SetKey(Key.D, false);
        _input.AddWheel(1);
        zoom.Update(1 / 30f, Tick());
        Assert.Equal(600 - 125 * 1.2f * 0.75f, zoom.Distance, 1);
    }

    [Fact]
    public void Keys_turn_54_degrees_a_second_and_a_100_pixel_drag_turns_15_through_the_smoothing()
    {
        var keys = Placed(Flat);
        _input.SetKey(Key.Q, true);
        for (int i = 0; i < 90; i++) keys.Update(1 / 30f, Tick());

        Assert.InRange(keys.Yaw * 180 / MathF.PI, -162, -160);   // Q turns the view right; the 20-frame average only delays the start

        var drag = Placed(Flat);
        _input.SetKey(Key.Q, false);
        _input.SetMouseButton(MouseButton.Middle, true);
        _input.AddMouseDelta(100, 0);
        for (int i = 0; i < 40; i++) drag.Update(1 / 30f, Tick());

        Assert.Equal(-15, drag.Yaw * 180 / MathF.PI, 1);
    }

    [Fact]
    public void The_pivot_eases_onto_a_step_in_the_ground_edges_pan_and_the_eye_is_never_below_the_clearance()
    {
        Func<float, float, float> step = (x, _) => x > 0 ? 100 : 0;
        var cam = Placed(step, 0.52f, 300);
        _input.SetKey(Key.D, true);
        cam.Update(1 / 30f, Tick());
        Assert.InRange(cam.Pivot.Y, 1, 99);   // on its way, not there at once

        for (int i = 0; i < 20; i++)
        {
            cam.Update(1 / 30f, Tick());
            var eye = cam.State.Eye;
            Assert.True(eye.Y >= step(eye.X, eye.Z) + 10, $"tick {i}");
        }

        Assert.Equal(100, cam.Pivot.Y, 1);

        var edge = Placed(Flat);
        edge.EdgePointer = new Vector2(0.005f, 0.5f);
        edge.Update(1 / 30f, ActionState.None);
        Assert.True(edge.PivotXz.X < -10);   // the left edge pans left
    }
}
