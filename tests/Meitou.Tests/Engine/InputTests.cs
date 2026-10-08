using Meitou.Engine.Input;

namespace Meitou.Tests.Engine;

public class InputTests
{
    readonly InputState input = new();
    readonly InputBindings bindings = new();

    ActionState Tick() => bindings.Resolve(input.Consume());

    [Fact]
    public void A_press_is_seen_by_exactly_one_of_several_ticks_in_a_frame()
    {
        input.SetKey(Key.W, true);
        var t1 = Tick();
        var t2 = Tick();
        var t3 = Tick();
        Assert.True(t1.Pressed(InputAction.MoveForward));
        Assert.False(t2.Pressed(InputAction.MoveForward));
        Assert.False(t3.Pressed(InputAction.MoveForward));
        Assert.True(t1.Held(InputAction.MoveForward) && t2.Held(InputAction.MoveForward) && t3.Held(InputAction.MoveForward));
    }

    [Fact]
    public void A_press_in_a_frame_without_a_tick_waits_for_the_next_tick_once()
    {
        // Three render frames, no tick ran; the press happened in the second.
        input.SetKey(Key.Semicolon, true);
        var t1 = Tick();
        Assert.True(t1.Pressed(InputAction.ToggleFreeCamera));
        Assert.False(Tick().Pressed(InputAction.ToggleFreeCamera));
    }

    [Fact]
    public void A_tap_inside_one_tick_window_is_pressed_and_released_but_not_held()
    {
        input.SetKey(Key.Space, true);
        input.SetKey(Key.Space, false);
        var t = Tick();
        Assert.True(t.Pressed(InputAction.Pause));
        Assert.True(t.Released(InputAction.Pause));
        Assert.False(t.Held(InputAction.Pause));
    }

    [Fact]
    public void Release_is_one_tick_and_key_repeat_is_not_a_new_press()
    {
        input.SetKey(Key.Space, true);
        Tick();
        input.SetKey(Key.Space, true); // OS key repeat
        Assert.False(Tick().Pressed(InputAction.Pause));
        input.SetKey(Key.Space, false);
        var up = Tick();
        Assert.True(up.Released(InputAction.Pause));
        Assert.False(up.Held(InputAction.Pause));
        Assert.False(Tick().Released(InputAction.Pause));
    }

    [Fact]
    public void Press_release_press_inside_a_window_is_still_one_press_and_held()
    {
        input.SetKey(Key.Space, true);
        input.SetKey(Key.Space, false);
        input.SetKey(Key.Space, true);
        var t = Tick();
        Assert.True(t.Pressed(InputAction.Pause));
        Assert.True(t.Held(InputAction.Pause));
        Assert.False(Tick().Pressed(InputAction.Pause));
    }

    [Fact]
    public void An_action_with_two_keys_is_held_while_either_is_down_and_released_when_both_are_up()
    {
        input.SetKey(Key.Q, true);
        input.SetKey(Key.Left, true);
        Tick();
        input.SetKey(Key.Q, false);
        var t = Tick();
        Assert.True(t.Held(InputAction.RotateLeft));
        Assert.False(t.Released(InputAction.RotateLeft));
        input.SetKey(Key.Left, false);
        Assert.True(Tick().Released(InputAction.RotateLeft));
    }

    [Fact]
    public void Mouse_buttons_move_and_wheel_go_to_the_first_tick_only()
    {
        input.SetMouseButton(MouseButton.Middle, true);
        input.AddMouseDelta(3, 4);
        input.AddMouseDelta(1, -1);
        input.AddWheel(1);
        input.AddWheel(2);
        input.SetMousePosition(50, 60);
        var t1 = Tick();
        var t2 = Tick();
        Assert.True(t1.Pressed(InputAction.Orbit));
        Assert.Equal(new System.Numerics.Vector2(4, 3), t1.MouseDelta);
        Assert.Equal(3f, t1.Wheel);
        Assert.Equal(new System.Numerics.Vector2(50, 60), t1.Input.MousePosition);
        Assert.Equal(System.Numerics.Vector2.Zero, t2.MouseDelta);
        Assert.Equal(0f, t2.Wheel);
        Assert.True(t2.Held(InputAction.Orbit));
        Assert.Equal(new System.Numerics.Vector2(50, 60), t2.Input.MousePosition);
    }

    [Fact]
    public void Releasing_everything_on_focus_loss_shows_as_releases()
    {
        input.SetKey(Key.W, true);
        input.SetMouseButton(MouseButton.Middle, true);
        Tick();
        input.ReleaseAll();
        var t = Tick();
        Assert.True(t.Released(InputAction.MoveForward));
        Assert.True(t.Released(InputAction.Orbit));
    }

    [Fact]
    public void Defaults_bind_every_action_but_none_share_a_key_between_movement_and_toggles()
    {
        foreach (var action in Enum.GetValues<InputAction>()) Assert.NotEmpty(bindings.Get(action));
        Assert.Contains(Binding.Of(Key.Semicolon), bindings.Get(InputAction.ToggleFreeCamera));
        var keys = Enum.GetValues<InputAction>().SelectMany(a => bindings.Get(a).Where(b => !b.IsMouse)).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void Overrides_replace_bindings_and_report_bad_entries()
    {
        var problems = bindings.Apply(new Dictionary<string, string>
        {
            ["moveforward"] = "I, Up",
            ["Orbit"] = "Mouse:Left",
            ["Pause"] = "",
            ["Nonsense"] = "A",
            ["Screenshot"] = "NotAKey",
        });
        Assert.Equal(2, problems.Count);
        Assert.Equal([Binding.Of(Key.I), Binding.Of(Key.Up)], bindings.Get(InputAction.MoveForward));
        Assert.Equal([Binding.Of(MouseButton.Left)], bindings.Get(InputAction.Orbit));
        Assert.Empty(bindings.Get(InputAction.Pause));
        Assert.Equal([Binding.Of(Key.F8), Binding.Of(Key.PrintScreen)], bindings.Get(InputAction.Screenshot)); // unchanged: the entry was invalid

        input.SetKey(Key.I, true);
        Assert.True(Tick().Held(InputAction.MoveForward));
    }

    [Fact]
    public void Bindings_round_trip_through_strings()
    {
        var copy = new InputBindings();
        copy.Apply(new Dictionary<string, string> { ["MoveForward"] = "Z" });
        var other = new InputBindings();
        Assert.Empty(other.Apply(copy.ToDictionary()));
        Assert.Equal(copy.Get(InputAction.MoveForward), other.Get(InputAction.MoveForward));
        Assert.Equal(copy.Get(InputAction.Orbit), other.Get(InputAction.Orbit));
        Assert.True(Binding.TryParse("mouse:right", out var b) && b.IsMouse && b.Button == MouseButton.Right);
    }
}
